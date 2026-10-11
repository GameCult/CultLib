# union-parity vectors

Written only by the C# reference, `tests/GameCult.Caching.Tests/UnionParityVectorTests.cs`, with
`CULTCACHE_WRITE_VECTORS=1`. Every other run reads these files and compares them byte for byte; a changed
vector is a reviewed commit. Other runtimes decode them and are held to the same outcomes.

Document `tests.union_surface` (schema `tests.union_surface.v1`), a positional array of
`[Id, Version, Root, Outcomes, Part, Crafted, Missing]`. A union value is `nil` or `[armKey, [armSlots...]]`.

| Union | Arm keys | Arm slots |
|---|---|---|
| Node | 0 Box, 1 Text, 2 Use, 5 Image (3 and 4 retired) | Box `[Id, Children: Node list]`; Text `[Id, Content]`; Use `[Id, Composition, Slots: Node list]`; Image `[Id, Uri, nil (retired 2), Alt]` |
| Outcome | 0 Pending, 1 Completed, 2 Denied | Pending `[Message]`; Completed `[SourceVersion]`; Denied is a unit arm, `[2, []]` |
| Item | 0 Gem, 1 Tool, 2 Part | Gem `[Name]`; Tool `[Name, Uses]`; Part `[Name, Count]` |
| CraftedItem | 0 Tool, 1 Part | the same arms as Item under its own keys; a key only Item declares is unknown to it |

Composition is a record, not an arm: `[Name, Body: Node]`.

## Files

- `union-surface.msgpack`: the payload. Value: Id `surface`, Version 3; Root is a Box `root` holding Text `t1`, a Use
  `u1` and Image `i2`; the Use's Composition `comp` holds Box `inner` (Text `deep`, Image `img`), and its Slots are
  Text `s1`, nil, Box `b2` (empty); Outcomes are Pending, Completed, Denied; Part is Tool `hammer`; Crafted (a
  CraftedItem field) is Part `gear`; Missing is nil. A union inside a record inside a union, a unit arm, a list of
  unions with a nil element, an inner union keyed apart from its outer. Written by C#.
- `union-surface.cc`: a single-file store written by the C# store holding that one record under key `surface`. Its
  payload equals `union-surface.msgpack`; rewriting the loaded store is byte-identical. The writer pins the store's
  clock, so the record's stored-at is `2026-01-01T00:00:00.0000000+00:00` and the whole file reproduces byte for byte.
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

Each payload is `union-surface.msgpack` with one slot appended past the reader's last declared slot of one
record (ruling `cultlib-gaps:ruling:extra-trailing-slots`: the record is read, read-only at any depth, every write
of it refused). A reader decodes the value of `union-surface.msgpack` from each; `detail` says where the slot is.

| File | detail | Record with the extra slot |
|---|---|---|
| `extra-slot-record.msgpack` | record | the document (slot 7) |
| `extra-slot-nested-record.msgpack` | nested-record | Composition `comp` |
| `extra-slot-arm.msgpack` | arm | the Root Box arm |
| `extra-slot-list-element.msgpack` | list-element | Text `s1`, an element of Use.Slots |

The C# reference reads these today and its slot-tolerance cut is what makes them read-only; this cut asserts only
that the value is read.
