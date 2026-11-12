# union-parity vectors

Written only by the C# reference, `tests/GameCult.Caching.Tests/UnionParityVectorTests.cs`, with
`CULTCACHE_WRITE_VECTORS=1`. Every other run reads these files and compares them byte for byte; a changed
vector is a reviewed commit. Other runtimes decode them and are held to the same outcomes.

Document `tests.union_surface` (schema `tests.union_surface.v1`), a positional array of
`[Id, Version, Root, Outcomes, Part, Crafted, Missing]`. A union value is `nil` or `[armKey, [armSlots...]]`.

| Union | Arm keys | Arm slots |
|---|---|---|
| Node | 0 Box, 1 Text, 2 Use, 5 Image, 6 Wrap (3 and 4 retired) | Box `[Id, Children: Node list]`; Text `[Id, Content]`; Use `[Id, Composition, Slots: Node list]`; Image `[Id, Uri, nil (retired 2), Alt]`; Wrap `[Inner: Node]` |
| Outcome | 0 Pending, 1 Completed, 2 Denied, -2147483648 Floor, 2147483647 Ceiling | Pending `[Message, Id]`; Completed `[SourceVersion, Id]`; Denied, Floor and Ceiling `[Id]`: the arms of a list carry an element id |
| Item | 0 Gem, 1 Tool, 2 Part | Gem `[Name]`; Tool `[Name, Uses]`; Part `[Name, Count]` |
| CraftedItem | 0 Tool, 1 Part | the same arms as Item under its own keys; a key only Item declares is unknown to it |

Composition is a record, not an arm: `[Name, Body: Node]`.

## Files

- `union-surface.msgpack`: the payload. Value: Id `surface`, Version 3; Root is a Box `root` holding Text `t1`, a Use
  `u1` and Image `i2`; the Use's Composition `comp` holds Box `inner` (Text `deep`, Image `img`), and its Slots are
  Text `s1`, nil, Box `b2` (empty); Outcomes are Pending, Completed, Denied; Part is Tool `hammer`; Crafted (a
  CraftedItem field) is Part `gear`; Missing is nil. A union inside a record inside a union, an id-only arm, a list of
  unions with a nil element, an inner union keyed apart from its outer. Written by C#.
- `union-surface.cc`: a single-file store written by the C# store holding that one record under key `surface`. Its
  payload equals `union-surface.msgpack`; rewriting the loaded store is byte-identical. The writer pins the store's
  clock, so the record's stored-at is `2026-01-01T00:00:00.0000000+00:00` and the whole file reproduces byte for byte.
- `unit-arm-denied.msgpack`, `unit-arm-floor.msgpack`, `unit-arm-ceiling.msgpack`: a member-less arm of a union no list reaches
  (so its arms carry no element id), alone, written by C#: `[2, []]`, `[-2147483648, []]`, `[2147483647, []]`. These pin the
  unit-arm encoding; the id-only arms above are not unit arms.
- `outcomes.msgpack`: the expected outcome of every file below, as data. An array of entries
  `[file, outcome, union, key, detail]`: `outcome` is `refused` or `read-only`; `union` is the union (or record)
  named; `key` is the arm key or nil; `detail` is as in the tables below.

## Refusals (outcome `refused`)

A refusal names the union and the arm key and never echoes the payload. Each payload is `union-surface.msgpack`
with one part replaced.

| File | Replaced part | detail | Must name |
|---|---|---|---|
| `unknown-arm-field.msgpack` | Root is an arm with the retired key 3 | unknown-arm | Node, key 3 |
| `unknown-arm-list.msgpack` | an Outcomes element has key 7 | unknown-arm | Outcome, key 7 |
| `unknown-arm-nested.msgpack` | key 9 three Boxes deep in Children | unknown-arm | Node, key 9 |
| `inner-union-outer-key.msgpack` | the Crafted field carries key 2, declared only by Item | unknown-arm | CraftedItem, key 2 |
| `arm-arity.msgpack` | Root is written `[1]`, an arm with no slots array | arity | a refusal of the arm as malformed (C#: MessagePack's "Invalid Union data") |

## Extra trailing slot (outcome `read-only`)

Rulings `cultlib-gaps:ruling:extra-trailing-slots` and `cultlib-gaps:ruling:slot-mark-consent`: a payload carrying a
slot past its reader's declared slots, in any object at any depth, is read, held read-only, and every write or
removal of it is refused before anything is staged. The first four payloads are `union-surface.msgpack` with the
string `"extra-slot"` appended as one more slot of one object; a reader decodes the value of `union-surface.msgpack`
from each. The fifth is a bare arm of union `UnitOutcome` (0 Waiting `[Message]`; 2 Denied2, -2147483648 Floor2 and
2147483647 Ceiling2, each `[]`), the union of the `unit-arm-*` files; a reader decodes Denied2 from it.

An object's declared slots are the slots its type's own formatter writes for one instance (for these fixtures,
the highest key plus one); a reader derives them from the layout it writes, never from a list of public members.
Only a type the runtime writes by its own positional `[Key]` layout is checked. Exempt, as in the C# reference
(`cultlib-gaps:ruling:leaf-acceptance-follows-reference`): a type the runtime formats some other way (a
user codec or formatter, a hand-written serializer, a resolver ahead of the runtime's own, a collection type
written as its items), and a type no instance of which the runtime can write for measuring
(`cultlib-gaps:ruling:slot-unmeasurable-type`). Neither is ever marked. No vector carries an exempt type: each
runtime pins its own exemption with its own fixture.
Only an array longer than that marks: a short array
(missing trailing slots) keeps the defaults and is writable, and a non-nil value in a retired slot below the highest
key (Image's slot 2) does not mark. A union adds no depth: an arm's array is counted against the arm's own slots.

| File | detail | Object with the extra slot | Depth | Mark: type, slots carried, slots declared |
|---|---|---|---|---|
| `extra-slot-record.msgpack` | record | the document (slot 7) | 0 | UnionSurface, 8, 7 |
| `extra-slot-nested-record.msgpack` | nested-record | Composition `comp` | nested | Composition, 3, 2 |
| `extra-slot-arm.msgpack` | arm | the Root Box arm | nested | Box, 3, 2 |
| `extra-slot-list-element.msgpack` | list-element | Text `s1`, an element of Use.Slots | nested | Text, 3, 2 |
| `extra-slot-unit-arm.msgpack` | unit-arm | Denied2 in `[2, ["extra-slot"]]` (union `UnitOutcome`, key 2) | as placed | Denied2, 1, 0 |
| `depth-skipped-at-limit.msgpack` (bounds file) | depth | the document (slot 7, a value of nested arrays) | 0 | UnionSurface, 8, 7 |

Each payload marks exactly one object. Five rows of `outcomes.msgpack` are `read-only` (record, nested-record, arm,
list-element, unit-arm); the sixth `read-only` payload is in `bounds-outcomes.msgpack`. The payload nesting check
runs before the mark: `depth-skipped-past-limit.msgpack` is refused, never marked.

### What a runtime asserts for a `read-only` payload

Load it as a store record under the reader's own schema id (the C# reference puts each UnionSurface payload in a
`tests.union_surface` store; the unit arm goes in slot 1 of a document `[Name, Outcome: UnitOutcome]`). Then:

1. the value is read past the extra slot;
2. the record reports the mark as in the table: the first type read with extra slots, the slots it carried and the
   slots the reader declares, and the count of objects that carried extra slots (1 here);
3. the record is not writable (C#: `CultCache.IsWritable(key)` false), and a key no store holds is writable;
4. a replacing write, a variant write and a removal of the key, and a flatten of a variant whose base chain holds
   the marked record, are refused before anything is staged,
   with the runtime's schema conflict refusal carrying the mark; its message names the type and both counts and
   never contains `extra-slot`, the slot's value;
5. a write to another key of the same store succeeds.

Consent: a mark is explained only when it is at depth 0 and the schema comparison of the record's stored id against
the reader reported ignored extra slots covering it: the record is stored under an id the reader lists compatible,
that id's catalog entry has more members, and the mark's slot count is at most the highest reported slot plus one.
Declaring that id consents to an explained mark only. Every mark in the table above is unexplained (stored under
the reader's own id), so each record stays read-only whatever is declared.

## Slot stores (`slot-outcomes.msgpack`)

Two single-file stores, each record written as a value and its payload then replaced, the store's clock pinned to
2026-01-01T00:00:00Z as `union-surface.cc`'s:

- `extra-slot-store.cc`, read with `tests.union_surface` (schema `tests.union_surface.v1`) and `tests.slot_unit`
  (schema `tests.slot_unit.v1`, a document `[Name, Outcome: UnitOutcome]`). `marked` is
  `extra-slot-nested-record.msgpack`; `clean` is `union-surface.msgpack`; `unit-arm` is the `tests.slot_unit`
  document `["unit-arm", [2, ["extra-slot"]]]`, whose Denied2 arm sits at depth 1 (the document is depth 0; a union
  adds none); `marked-variant` is a variant of `marked` overriding only `Id` (slot 0) to `"marked-variant"`.
- `extra-slot-consent.cc`: every record is stored under `tests.union_surface.wide`, whose catalog entry is
  `tests.union_surface`'s seven members plus slot 7 (`Wider`). Read it with a `tests.union_surface` type of schema
  `tests.union_surface.v2` (the reference names it `SurfaceDeclaring`) that lists `tests.union_surface.wide`
  compatible, so the comparison reports slot 7 ignored. `consented` is `union-surface.msgpack` plus one root slot
  (slot 7); `over-consent` plus two (slots 7 and 8, past the highest reported); `nested` is
  `extra-slot-nested-record.msgpack` plus one root slot; `clean` is `union-surface.msgpack`.

`slot-outcomes.msgpack` is a second outcomes file, as `bounds-outcomes.msgpack` is, so readers of `outcomes.msgpack`
stay green. It is an array of rows `[store, key, writable, explained, type, slots, declared, occurrences, flattenRefusedBy]`,
one per record of both stores: `writable` is what `IsWritable` answers; `flattenRefusedBy` is nil unless the record
is a variant whose flatten is refused, and then it is the key of the base-chain record whose own refusal refuses
it (the refusal names that key; the store is unchanged). For an unmarked record `explained` through `occurrences`
are nil, otherwise they are the mark: whether it is explained, the first type read with extra slots, the slots it carried,
the slots the reader declares, and how many objects carried extra slots. A depth-0 type is the reader's document
type (the reference's names: `SurfaceDeclaring` for the consent store). For each row, open a copy of the store,
assert the mark and `IsWritable`, flatten the key when `flattenRefusedBy` is set and assert the refusal, then
remove the key: refused (with the mark, the file unchanged) exactly where
`writable` is false. In the consent store a write of `consented` lands and a write of `over-consent` is refused.

The carried-verbatim check: open `extra-slot-store.cc`, assert `marked` read-only and `clean` writable, write
`clean` and flush: `marked`'s payload bytes, schema id and stored-at, and every catalog entry's bytes, equal the
originals.

## Bounds (`bounds-outcomes.msgpack`)

A second outcomes file, so runtimes that read only `outcomes.msgpack` stay green. Its first entry is
`["limit", 256]`, the payload nesting limit; every other entry is `[file, outcome, union, key, detail]` with
`outcome` `accepted`, `refused` or `read-only`. For `accepted`, `detail` names the file the value's re-encoding
must equal. Each payload is `union-surface.msgpack` with one part replaced.

Arm keys are Int32 whatever their MessagePack width; a float is never a key.

| File | Replaced part | outcome, detail | Must |
|---|---|---|---|
| `arm-key-int32-min.msgpack` | a fourth Outcomes element Floor, key `d2 80000000` | accepted, itself | re-encode byte-identical |
| `arm-key-int32-max.msgpack` | a fourth element Ceiling, key `ce 7fffffff` | accepted, itself | re-encode byte-identical |
| `arm-key-wide-coding.msgpack` | Ceiling's key as uint64 `cf 000000007fffffff` | accepted, `arm-key-int32-max.msgpack` | re-encode to the canonical coding |
| `arm-key-above-int32.msgpack` | a unit arm with key `ce 80000000` | refused, key-out-of-range | name Outcome, say outside Int32 |
| `arm-key-below-int32.msgpack` | a unit arm with key `d3 ffffffff7fffffff` | refused, key-out-of-range | name Outcome, say outside Int32 |
| `arm-key-float32.msgpack` | Completed with key float32 `ca 3f800000` | refused, key-not-integer | name Outcome, say non-integer |
| `arm-key-float64-negzero.msgpack` | Pending with key float64 `cb 8000000000000000` | refused, key-not-integer | name Outcome, say non-integer |
| `nil-arm-slots.msgpack` | a fourth element `[1, nil]` | refused, nil-slots | name Outcome and key 1; never read as a null element |

Payload nesting: every array and map counts, read or skipped, and the outermost array is depth 1. Depth 256 is
accepted and 257 refused, on read and on write, naming the type, the depth reached and the limit, never the payload.
`depth-at-limit` and `depth-past-limit` are chains of Wrap arms ending in a leaf (a Box with no children, or a Text
whose content is a canary string); `depth-skipped-*` is the base document with one extra trailing slot of plain nested
arrays around a canary string, which no formatter reads.

| File | Depth | outcome, detail | Must |
|---|---|---|---|
| `depth-at-limit.msgpack` | 256 | accepted, itself | decode, re-encode byte-identical; the constructed value writes it |
| `depth-past-limit.msgpack` | 257 | refused, depth | refuse on read; refuse to write the constructed value and return nothing |
| `depth-skipped-at-limit.msgpack` | 256 | read-only, depth | read the value of `union-surface.msgpack` |
| `depth-skipped-past-limit.msgpack` | 257 | refused, depth | refuse on read though the typed depth is under 10 |

A walk of the payload must not recurse: 100000 nested arrays in an extra slot are refused on a 256 KiB stack.
