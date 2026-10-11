# Python slot scalar types vs the C# reference: facts and pointers

Fork served: cultlib-gaps: Python slot scalar types vs the C# reference's refusals, raised by cut-union-bounds-py.h1 undone.
Date: 2026-10-11. Gathered by eyes-py-slot-scalar-types (read-only). Facts and pointers only; no recommendation.

Checkout caveat: line numbers below are the operator checkout at `main` 4ea296ae. cultcache-py's bounds cut lives on
branch `hands/union-bounds-py` (head 4328ddb4); cultcache-ts's union codec lives on `hands/union-codec-ts`
(absent from `main`: `git ls-tree HEAD packages/cultcache-ts/src` has no `codec.ts`).

## 1. In CultLib

### 1.1 The measured gap (the trigger)

Verdict `cultlib-gaps:verdict:cut-union-bounds-py.s1`, claim `scalar-type-parity-unproven`, outcome Unproven, no finding
(spec did not promise it). Probe `cross.py g-` files, 168 cases, job1.log:
- 13 scalar slots x 8 wrong values; agree 86; C# refuses and Python accepts 82; C# accepts and Python refuses 0.
- String slots (Id, Box.Id, Text.Id, Text.Content, Image.Uri, Image.Alt, Comp.Name, Pending.Message, Tool.Name):
  Python accepts int, float, true, bin, array, map (refuses neither str nor nil, as C#).
- Int slots (Version, Completed.SourceVersion, Tool.Uses, Part.Count): Python accepts float, str, true, bin, array, map
  and nil; int5 agrees.
- All 8 container-shape slots (children list, record, slot list, outcomes list, arm slots, union x2, document) agree on
  every wrong value (64 shape replacements agree). So only scalar leaves differ.
- Related earlier finding: `cultlib-gaps:finding:cut-union-codec-py.s2.python-decode-slot-strictness-differs`
  (Python accepts Tool wire value [1,['hammer',12]] as Text(content=12), C# refuses; Python refuses short Gem value,
  C# accepts with default). Resolved Deferred to `cultlib-gaps:cut_spec:cut-union-bounds-py.r1`.

### 1.2 C# reference: how a slot's scalar type is declared and refused

- Declaration is the C# member type plus `[Key(n)]`; there is no CultLib scalar-type vocabulary.
  `src/GameCult.Caching/README.md:16-27` (example), `src/GameCult.Caching/Contracts/cultcache-schema-compatibility.md:31-35`
  ("A member's slot is its MessagePack `[Key(n)]` integer").
- Registry checks are structural only (CultCache.cs:1015-1053, messages 1430-1461: needs [MessagePackObject], [Key] on every
  public member, no string keys, distinct keys, constructor shape).
- The wrong-scalar refusal is made by MessagePack-CSharp's per-type formatter (external library, not CultLib code); CultLib
  contains no scalar-type check of its own. (Grep of `src/` for scalar/wrong-type checks found none; unions get a guard:
  `src/GameCult.Caching.MessagePack/CultUnionGuard.cs:44-50` refuses non-integer key, key outside Int32, undeclared key,
  nil arm slots.)
- Published schema: each member's `TypeName` is generated from the member's CLR type,
  `CultCache.cs:647` `TypeName = CultSchemaTypeNames.FromType(member.MemberType)`; `CultSchemaTypeNames.cs:9-31` yields
  `System.String`, `System.Int32`, `System.String[]`, `System.Collections.Generic.List<...>`.
  Catalog members persisted with `Slot, MemberName, TypeName, IsReference, IsMany, TargetSchemaName, IndexAlias, IsName`
  (`CultDocumentMessagePackSerialization.cs:465-496`; `CultCache.cs:39,326`).
- The catalog TypeName is load-bearing at open: `CultCache.cs:874-877` a persisted member with a different TypeName is an
  error (`slot N changed type 'A' -> 'B'`); an absent member is only a warning (`:863-872`). The catalog names a type; no
  code reads it to validate payload scalars on read.

### 1.3 cultcache-rs

- Slot types are the Rust field types, enforced by serde. `packages/cultcache-rs/cultcache-rs-derive/src/lib.rs`:
  `#[cultcache(key = N)]` per field (`:126-171`), `slot_codec` (`:177`) generates `Serialize` as a positional tuple with nil
  gaps and `Deserialize` by slot (`:213-258`), extra trailing slots skipped with `IgnoredAny` (`:233`, `:305`). `bytes` and
  `default` attributes are the only extras (`:175-176`). Union: `CultUnion` derive, arm key Int32, unknown key a decode error
  (`:87-97`, `:326-340`; `packages/cultcache-rs/README.md:187-196`).
- No separate scalar-type vocabulary; no catalog-type derivation was looked for in rs here (not read).

### 1.4 cultcache-ts

- On `main`: no slot codec. `packages/cultcache-ts/src/document.ts:51-64` takes author-declared schema members and refuses
  an empty `typeName`; `types.ts:22-24` member has `slot`, `typeName`. `swarm-documents.ts:91-95` hand-writes
  `typeName: "string"|"long"|"object"`. Payload encode/decode is a user function (`types.ts:17-18`).
- On branch `hands/union-codec-ts`, `packages/cultcache-ts/src/codec.ts`: typed leaf codecs `string`, `boolean`, `bytes`,
  `int32`, `int64`, `float32`, `float64`, `list`, `array`, `nullable`, each with the reference's `typeName`
  (`"System.String"`, `"System.Int32"`, ... lines 39-128) and `expect`/`refuse` on a wrong kind in both encode and decode
  (lines 31-42, 60-79). `SlotsCodec.members` derives the schema catalog from the slot codecs (lines 17-20). Header
  comment lines 5-8 say the reference refuses a store whose slot names another type.
- Precedent in the mind: finding `cultlib-gaps:finding:cut-union-codec-ts.s1.codec-catalog-refused-by-reference` (High,
  Fixed 0882eee9): TS codec first derived `System.Object` for every slot; C# refused the whole store (7 slots).

### 1.5 cultcache-py today

- Slot = `DatabaseEntryField(name, key, default, codec, many)`; `codec` is a `SlotCodec` (encode/decode callables) only for
  nested record or union (`documents.py:61-92`). A scalar slot has no codec and no type: `encode_slot`/`decode_slot`
  return the value unchanged when `field.codec is None` (`:370-386`). Consumers write `("name", 0)` tuples, 3-tuples with a
  default, or `database_entry_field("body", 1, codec=..., many=True)` (`:276-283, 417-`;
  `packages/cultcache-py/tests/test_union_parity.py:25-59`; README `packages/cultcache-py/README.md:30,114-148`).
- Python-side checks that exist: record is a list (`:394-396`), missing required slot (`:398-409`), union shape/arm key
  (`:313-325`, bool excluded by `_is_key` `:346-347`), many-slot is a list (`:383-384`), key >= 0 (`:88-89`).
- Catalog: `define_database_entry_type` emits every member with `type_name="System.Object"` (`documents.py:244`) and
  `canonical_schema_json` built from the members (`:140-160`). `interop.py:38-46` instead hard-codes C# names
  (`System.String`, `System.String[]`) for its one fixed document.
  Same `System.Object` default earlier caused the TS finding in 1.4 (finding text: "the codec's default copies Python's
  define_database_entry_type, whose own stores would hit the same refusal").
- Published schema carrying slot types for Python to read: the C# catalog carries `TypeName` per slot (1.2), persisted in
  the store and compared at open; `interop.py` shows Python reading catalog entries. No JSON Schema / CultNet schema with
  scalar types was found by grep in cultcache-py; `canonical_schema_json` in Python is built from `type_name` members.
  Whether a CultNet schema catalog outside the store (cultnet-py) exposes slot scalar types was not checked.

## 2. Prior art (dynamic host, declared field types)

| System | Types come from | Write | Read | Evidence |
|---|---|---|---|---|
| protobuf python | .proto IDL, generated classes | type-checked on field assignment (`TypeError`; `ValueError` out of range; bool rejected for int) | wire-type mismatch handling not stated in docs | protobuf.dev/reference/python/python-generated ("setting `foo` to a value of the wrong type will raise a `TypeError`"); `type_checkers.py` (IntValueChecker rejects bool `TypeError`, out of range `ValueError`; UnicodeValueChecker non-UTF-8 `ValueError`) https://raw.githubusercontent.com/protocolbuffers/protobuf/main/python/google/protobuf/internal/type_checkers.py ; proto3 guide: wire format "doesn't provide a way to detect fields encoded using one definition and decoded using another" https://protobuf.dev/programming-guides/proto3/ |
| Apache Avro python | Avro schema (JSON), not host code | `DatumWriter.write` calls `validate(..., raise_on_error=True)` -> `AvroTypeException`; union branch picked by non-raising validate | reader does not validate values; checks schema resolution (`SchemaResolutionException`) and decodes by writer schema | https://raw.githubusercontent.com/apache/avro/main/lang/py/avro/io.py ; fastavro writer: `validator` parameter "validation will be done on the records" (opt-in) https://fastavro.readthedocs.io/en/latest/writer.html |
| Thrift python | .thrift IDL, generated code | generated `write` calls `validate()` which checks required-field presence only (not scalar types); `writeFieldBegin` writes the given type unchecked | generated read emits `if ftype == TType.X:` ... `else: iprot.skip(ftype)`: a wrong wire type is silently skipped, field left unset | t_py_generator.cc `generate_py_struct_reader`, `generate_py_struct_required_validator` https://raw.githubusercontent.com/apache/thrift/master/compiler/cpp/src/thrift/generate/t_py_generator.cc ; TBinaryProtocol.py `writeFieldBegin`/`readFieldBegin` have no type check |
| pydantic v2 | Python annotations in host code | validates input at construction / `validate_python` / `validate_json`; lax coercion default, strict opt-in per call, field or config | same entry points | https://pydantic.dev/docs/validation/latest/concepts/strict_mode/ ("Lax mode (default) coerces"; page does not say assignment or serialization validates) |
| msgspec | Python annotations (Struct) in host code | docs (not retrieved; search snippet only): encode serializes values without enforcing annotations | decode validates against the `type=` and raises `ValidationError`; `array_like=True` encodes Struct as positional array | search result only, unverified: https://jcristharif.com/msgspec/usage.html (fetch returned 404 for usage/structs/supported-types pages) |
| Cap'n Proto (pycapnp) | .capnp schema loaded at runtime (`capnp.load`) | assignment type-error behavior not documented on the fetched page | n/a (pointer-based, no parse step) | https://capnproto.github.io/pycapnp/capnp.html (schema loading: "much like loading a Python module") |
| FlatBuffers python | .fbs schema, `flatc --python` generated typed accessors/builders | no runtime type validation stated on the fetched page | typed accessors read at fixed offsets; no value validation stated | https://flatbuffers.dev/languages/python/ |
| MessagePack (msgpack-python) | none | none | none | no IDL; the format is schemaless (msgpack-python itself not fetched) |

Observed pattern in the fetched sources, stated as fact only:
- Type source is an IDL/schema in protobuf, Avro, Thrift, Cap'n Proto, FlatBuffers; host-code annotations in pydantic and
  msgspec.
- Write-side type checking: protobuf (assignment), Avro (default on), pydantic/msgspec (on construction/decode of input);
  Thrift write checks required presence only; fastavro opt-in.
- Read-side: Avro reader trusts the writer schema; Thrift generated reader skips mismatched wire types; protobuf docs silent.

Not retrieved / unverified: pycapnp assignment error; FlatBuffers runtime checks; Thrift generated `validate` on type;
msgspec authoritative statements; protobuf python parse behavior for wrong wire type.

## 3. Precedent in the mind

Tool `query` (`eureka-state`), semantic "python slot scalar type validation parity with C# reference", top_k 10, no schema
filter (a `schemas` filter named Ruling/Resolution/Finding was refused: no such schema; schema names are
`<campaign>.<kind>`-style, not enumerated here). Roots variants and cultlib-gaps both appeared; matched 40. Relevant hits:
- `cultlib-gaps:finding:cut-union-codec-py.s2.python-decode-slot-strictness-differs` (PreExisting, Low, Deferred to
  union-bounds-py.r1): first record of Python decode strictness differing in both directions.
- `cultlib-gaps:finding:cut-union-codec-ts.s1.codec-catalog-refused-by-reference` (High, Fixed): `System.Object` catalog
  types refused by the reference; evidence note says Python's `define_database_entry_type` default has the same shape.
- `cultlib-gaps:finding:cut-union-codec-py.s1.missing-field-typed-item` (Fixed 36aa1a44): Python fixture declared slot 6 as
  Item union where C# says Node?; golden carried nil so tests passed.
- `aetheria-release:follow_up:py-float-width-parity`: Python msgpack round-trip widens float32 to float64 (204 -> 284 bytes);
  float width is another undeclared-scalar-type consequence in Python (owner CultLib cultcache-py).
- `cultlib-gaps:cut_spec:cut-slot-readonly-parity.r2` and `cut-slot-tolerance.r*`: read-only/unread-slot rules, adjacent.
- No ruling or follow_up was returned that decides scalar-type parity for Python. A dedicated Ruling query was not run
  (schema id unknown; the only attempted names were refused).
