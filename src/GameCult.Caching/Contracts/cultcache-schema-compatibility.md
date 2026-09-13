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
The registry also refuses a persisted member without `[Key]`, a string
`[Key]`, two members sharing a slot (including `new`-hidden members), a hidden
persisted member, an override whose `[Key]` or `[IgnoreMember]` differs from its
base declaration's (MessagePack reads the base declaration), and a readonly
persisted field.

Visibility follows MessagePack's rule: the registry accepts what MessagePack
round-trips and refuses what it would silently lose. The registry persists
public fields and properties only. Without `AllowPrivate`, a persisted property
needs a public setter, and a `[Key]` on a non-public member is refused because
MessagePack skips that member. With `AllowPrivate`, private, internal and
init-only setters are all writable. But MessagePack then also reads non-public
members, so every non-public instance field or property must be
`[IgnoreMember]`. A class also needs a constructor MessagePack can call. That
is a parameterless one (public unless `AllowPrivate`), or one whose parameters
take the members at `[Key(0)]`, `[Key(1)]`, ... in order. A primary constructor
usually has neither.

## Which schema a record carries

A record is written under the schema of its runtime type. Writing a derived
document through a base-typed handle or generic parameter (`UpsertAsync<Gear>`
with a `Weapon`) persists the `Weapon` schema, and the record reloads as a
`Weapon`. A runtime type without `[CultDocument]` is refused.

Typed lookups and watches (`Get<T>`, `GetAll<T>`, `GetByName<T>`,
`GetByIndex<T>`, `GetGlobal<T>`, `Watch<T>`) match every record whose runtime
type is assignable to `T`. A single-result lookup with several candidates
throws.

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
