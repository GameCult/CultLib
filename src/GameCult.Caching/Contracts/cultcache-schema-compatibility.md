# CultCache Schema Compatibility

CultCache compatibility is slot-driven and explicit. The cache will not pretend
that "same-ish looking" schemas are fine just because the names feel friendly.

## Semantic identity

Two CultCache document schemas are considered the same shape when they agree on:

- `schemaName`
- `schemaVersion`
- member slots
- member persisted type names
- reference/cardinality semantics
- target schema names for references
- name/index lookup participation

The canonical fixtures currently exercised in C# are:

- `tests.named_entry`
  - schema id: `sha256:e7b97801b94190f3159012ede45b0069bb09ebf7920f7432c971bc86a0e08de8`
  - content hash: `sha256:23150930afcc1d84f0cb3012ccc2debcb9b4685f62083033bbaab0083f1e832e`
- `tests.reference_holder`
  - schema id: `sha256:bd85064961cc74565fb73e3ccbc4217cfba4dc4869e365a08bea4f704739bd8f`

See `GameCult.Caching.Tests/BackingStoreTests.cs` for the canonical fixture
documents and the expected receipts.

## Slots

A member's slot is its MessagePack `[Key(n)]` integer. MessagePack's `[Key]` and
`[IgnoreMember]` are the single slot authority, so `GameCult.Caching` depends on
`MessagePack.Annotations`. Payloads are written by `MessagePackSerializer`, so
the registry refuses a document type without `[MessagePackObject]` and a
non-public document type without `[MessagePackObject(AllowPrivate = true)]`.
A member's type name is the CLR full name; a nested type is written `Outer+Inner`.
The registry also refuses a public member without `[Key]` or `[IgnoreMember]`
(get-only properties included; MessagePack throws on them), a string `[Key]`,
two members sharing a slot (including `new`-hidden members), a hidden persisted
member, and an override whose `[Key]` or `[IgnoreMember]` differs from its base
declaration's (MessagePack reads the base declaration).

Visibility follows MessagePack's rule: the registry accepts what MessagePack
round-trips and refuses what it would silently lose. The registry persists
every keyed public field and property, get-only ones included, because
MessagePack writes them all. A `[Key]` on a non-public member without
`AllowPrivate` is refused because MessagePack skips that member. With
`AllowPrivate`, MessagePack also reads non-public members, so every non-public
instance field or property must be `[IgnoreMember]`.

A class needs a constructor MessagePack can call: the `[SerializationConstructor]`
if one is marked, otherwise the longest constructor (public unless
`AllowPrivate`) whose every parameter takes the keyed member at its position
(`[Key(0)]`, `[Key(1)]`, ...). A parameterless constructor always qualifies; a
primary constructor usually does not. MessagePack reads a member back through
its setter, or through that constructor when the member's slot is below the
constructor's parameter count. A setter counts when it is public, or any setter
(private, internal, init-only) under `AllowPrivate`; a readonly field counts as
writable under `AllowPrivate` only. A keyed member MessagePack cannot read back
either way is refused: a get-only property, a readonly field without
`AllowPrivate`, or a property whose setter is non-public without
`AllowPrivate`, when the constructor does not fill it.

## Which schema a record carries

A record is written under the schema of its runtime type. Writing a derived
document through a base-typed handle or generic parameter (`UpsertAsync<Gear>`
with a `Weapon`) persists the `Weapon` schema, and the record reloads as a
`Weapon`. A runtime type without `[CultDocument]` is refused.

Typed lookups and watches (`Get<T>`, `GetAll<T>`, `GetByName<T>`,
`GetByIndex<T>`, `GetGlobal<T>`, `Watch<T>`) match every record whose runtime
type is assignable to `T`. A single-result lookup with several candidates
throws.

## Unions

A union is an abstract class or interface carrying `[Union(key, typeof(Arm))]`
attributes on itself (a union whose arms are also a union has its own key set;
attributes are not inherited). A union value is `nil` for a null reference, and
otherwise an arm: the two-element array `[key, armSlots]`, where `armSlots` is the
arm's own slot array (a unit arm is `[key, []]`). Keys start anywhere and may
have gaps.

- A key is the identity of an arm. It is never reused, and a retired arm's key
  stays a gap.
- An arm key the reader's union does not declare is refused on read, in a field,
  a list or an arm, with an error naming the union type and the key. The
  payload is never echoed, and the value is never read back as `null`: a runtime
  that cannot name an arm cannot re-encode it.
- A value whose runtime type is not exactly a declared arm is refused on write,
  naming the union and the type. A subtype of an arm is not the arm.
- A store that holds a refused arm fails to load, and the error names the record
  key and its schema id.
- Arm slots follow the slot rules below; this rule does not change them.

## Soft-migratable drift

CultCache accepts compatible drift only when the local reader can still map the
persisted slots honestly:

- persisted schema id differs, but the embedded catalog points to a compatible
  local schema id
- persisted slot is missing locally and can be ignored
- local slot is missing in persisted data and can fall back to the local
  default value

The cache emits a typed migration report with:

- exact vs compatible-drift classification
- ignored extra slots
- defaulted missing slots
- warning codes and messages

## Hard rejection

CultCache rejects persisted schemas when any slot changes in a way that would
lie about the payload:

- type change
- reference vs value change
- one vs many change
- target schema change
- name/index lookup semantics change
- missing embedded catalog entry
- no compatible local schema candidate

This is not negotiable. Better a loud refusal than quiet bit-rot.

## Variant overrides

A variant's override follows the same outcome as the same member on a plain record; there
is no separate rule for variants, and no separate report.

- An override naming a slot the type no longer has is ignored, exactly as a plain record
  ignores a dropped member. The drift is reported once, by the store's schema resolution
  (`LastSchemaMigrationReports`), for the variant's record as for any other record of the
  schema. The variant inherits the base value.
- An override holding a value that no longer decodes into the member's type refuses the
  load, exactly as a plain record holding such a value does; the refusal names the variant
  and the member.

Drift is for type changes after the fact. A write is validated: an override for a slot the
type does not have, a value that does not decode as the member, or two overrides of one
slot is refused when written.
