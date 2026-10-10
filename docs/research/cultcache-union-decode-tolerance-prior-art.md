# Union and record decode tolerance: prior art and CultCache's stance

Fork: thing2:question:contract-decode-tolerance. Follow-up: thing2:follow_up:cultcache-union-parity.
Eyes pass, 2026-10-10, session self-2026-10-10c-eureka. Facts only; no recommendation.
Tags: [fetched] read this session; [local] read in the repo this session; [background] prior knowledge, not refetched; [not found] the fetched page did not say.

## 1. Established systems

### Protocol Buffers
- Unknown fields: proto3 "preserve[s] unknown fields and include[s] them during parsing and in the serialized output", matching proto2. Lost on JSON serialization and on field-by-field copy into a new message; CopyFrom/MergeFrom avoid the loss. (protobuf.dev/programming-guides/proto3/, "Unknown Fields") [fetched]. The version proto3 started preserving (3.5) [background].
- Unknown oneof case: no distinct state. The page warns a NOT_SET result is ambiguous: the oneof may be unset, or set to a field from a newer schema [fetched]. A oneof member the reader lacks is an ordinary unknown field, so it sits in the unknown-field set and is re-emitted [background].
- Unknown enum values: "unrecognized enum values will be preserved in the message" (raw int in open-enum languages, accessor in Java) and re-serialized [fetched].

### Cap'n Proto
- Union: a separate tag says which member is set. Adding a member to a union is safe because "existing programs should already know to check the union's tag"; they "may or may not behave reasonably when the tag has a value they don't recognize". Moving an existing field into a new union can show an old reader "a garbage value or throw an exception" (capnproto.org/language.html, "Unions", "Evolving Your Protocol") [fetched].
- which() on an unknown discriminant: [not found] in the fetched language and encoding pages. C++ generated which() returns the tag as an enum and the reader is expected to switch with a default [background].
- Preservation: [not found] on the fetched pages; structs "can be traversed (e.g., copied) without knowing their type" (encoding.html, Structs) [fetched]. Copy preservation of unknown data is [background].

### Avro (schema resolution; reader schema and writer schema both required)
- Both unions: reader takes its first branch matching the writer's selected branch; "if none match, an error is signalled". Writer union, reader non-union: the selected writer branch must match, else error (Avro spec, Schema Resolution) [fetched; also quoted from the 1.11.1 spec in docs/research/read-only-record-signal-prior-art.md:176-178].
- Record, writer has an extra field: "the writer's value for that field is ignored". Reader field missing from the writer: reader's default if it has one; no default is an error [fetched].
- Enum, writer symbol absent in reader: reader's declared default if any, else error [fetched].
- Preservation: none. Extra fields are dropped by resolution [fetched].

### FlatBuffers
- Union evolution: appending a member is safe ("CodeV1 will not recognize the another_a"); inserting in the middle without an explicit discriminant misreads members; inserting with an explicit discriminant is safe (flatbuffers.dev/evolution/) [fetched].
- What an old reader sees for an unknown union type, and re-serialization preservation: [not found] on that page. Generated readers expose a type byte plus a union table pointer; an unknown type value falls outside the generated enum [background].

### Thrift
- Unknown fields and unknown union field ids: [not found] in thrift-rpc.md. Protocol readers skip unrecognized field ids and drop the data; an unknown union id leaves the union empty, and a union with no field set can fail validation [background].

### MessagePack-CSharp [Union]
- README Union section says only "Unique union keys are required"; it states no behaviour for an unknown key and no option for it [fetched, first 100k chars of the README].
- Missing keys: members initialize to default (README, Object Serialization) [fetched]. Extra array elements for [Key(int)] and unknown string keys: README silent; it shows a manual formatter that loops and skips extras [fetched].
- Generated and dynamic union formatters skip the payload and yield null when the key is absent from the union's key map [background]. The estate's own measurement agrees: unknown arm key decodes to null, no error (question document, "Measured in the C# reference... MessagePack-CSharp 3.1.7") [local, via eureka-state view]. The vendored copy at F:\Projects\CultPong\Assets\Scripts\MessagePack\Resolvers\DynamicUnionResolver.cs does a key-map TryGetValue at :347 (type-map TryGetValue at :206); the miss branch was not read.
- No configurable unknown-union behaviour found in the README; whether a newer version adds one [not found].

### serde
- enum-representations page says nothing on unknown variant names, #[serde(other)] or deny_unknown_fields [fetched]. serde_derive de.rs was fetched but the identifier/enum_ modules holding unknown_variant were not in the excerpt [not found].
- [background] default: an unknown variant is an error ("unknown variant `X`, expected one of ..."); #[serde(other)] on a unit variant makes the identifier decoder return it for any unknown name (units only, payload discarded); unknown struct fields are ignored unless #[serde(deny_unknown_fields)]; a missing field is an error unless #[serde(default)] or Option.

### Bincode / postcard
- [background] Both are non-self-describing: an enum is a variant index (bincode u32, postcard varint) followed by fields in order; an out-of-range index is a decode error; there are no field tags, so an extra or missing slot is detectable only by running out of or leaving bytes. Evolution is by schema versioning outside the format. Not fetched.

## 2. Preservation across an older reader, and the failure each answers
| System | Unknown data survives re-encode? | Failure answered |
|---|---|---|
| Protobuf | yes: unknown fields (hence unknown oneof cases) and unknown enum ints, in binary APIs only [fetched] | old intermediary re-serializing a newer message (purpose: background) |
| Cap'n Proto | page silent; reader sees a tag it may not know and must handle it [fetched] | in-place access; append-only evolution |
| Avro | no: extra field dropped, or error on no branch / no default [fetched] | writer schema travels with data, so resolution is exact and loud |
| FlatBuffers | page silent [fetched] | append-only evolution, explicit discriminants |
| Thrift | no (skipped) [background] | forward tolerance for old readers |
| MessagePack-CSharp | no: arm becomes null (measured), extra slot skipped | not documented |
| serde | no; error by default [background] | type safety; leniency is opt-in |
| Bincode/postcard | n/a [background] | compactness; no evolution in the format |
| Unity SerializeReference, DataContract ExtensionData | yes: bytes retained and recoverable (docs/research/read-only-record-signal-prior-art.md:182-205) [local] | data loss from missing types |

## 3. CultCache's own stance (F:\Projects\CultLib)
- docs/persisted-record-encoding.md:1-19: persisted records are verified by re-encoding what was decoded and comparing bytes; a record that reads but re-encodes differently is refused, on live data at startup or next read. Three Idunn guards cited at :10-14. [local] A tolerated extra trailing slot or a null-collapsed arm would not re-encode to the stored bytes; whether those guards would run on such records was not tested.
- No evolution/compatibility doc: `ls docs | grep persist|evol|compat|schema|version` returns only persisted-record-encoding.md. docs/document-variants-cut.md:327-333 covers union-slot reuse at registration ("late union-slot reuse passes registration"; "interface unions are never minted") [local, grep lines only].
- Reference union handling: src/GameCult.Caching/CultInspectorModel.cs:768-774 classifies an abstract/interface type by its [UnionAttribute]s ordered by key and refuses a type with none ("declares no [Union] subtypes", :772); also :120. This is the inspector's shape classifier; decode is MessagePack-CSharp's. No other use of UnionAttribute in src/GameCult.Caching. [local]
- Trailing-slot tolerance: packages/cultcache-ts/test/cult-cache.test.ts:1176-1195, "CultCache interop reader accepts missing compatible trailing slots and rejects mismatched slots": a legacy-payload store with fewer slots reads with `tags: []`; a mismatched-type payload rejects with /Expected string/u. Missing trailing slots are tested; extra trailing slots are not. [local]
- TS decode: packages/cultcache-ts/src/cult-cache.ts:679-692 wraps a formatter as `schema.parse(decode(payload))`, so TS strictness is whatever the zod schema says; no TS union codec (follow-up text). [local]
- Rust: packages/cultcache-rs/src/*.rs has no unknown-arm or extra-slot handling found by grep; "unknown" hits are store-header tests (:4889-4890 refuses an unknown header by name; :5009-5019 unknown-header.msgpack, variant-v2.msgpack). Rust enum arms persist as serde external tags (follow-up text). [local]
- Consumers persisting unions: Aetheria (F:\Projects\Aetheria) has `[Union]` abstract classes in Assets/Scripts/ServerShared: Provenance.cs:96-98 (keys 0-2), ItemInstance.cs:18-21 (keys 0-3) and :29-31, Environment.cs:73. The assembly registers CultCache documents (AssemblyInfo.cs:3-4; Galaxy.cs, ItemData.cs, AetheriaStores.cs reference CultCache); ItemInstance is held in entity item lists (Entity.cs:2003). An older build reading a store with a newer arm key would take the C# null-arm path, if these types sit inside persisted CultCache documents; that persistence path was not traced. Aetheria-legacy has more unions (Behaviors.cs:29-38, Item.cs:43-63, Contract.cs:10-11); commented-out keys 8, 9 at Behaviors.cs:37-38 show arms retired in place. [local]
- Ghostlight and Huginn: not searched for [Union] (the grep matched only Aetheria). Follow-up text: "No live record crosses runtimes as a union today" [local, view]. Same-runtime older-build-reads-newer-store: unverified.
- The question document records the C# measurement: extra trailing slot skipped and equal; missing trailing slot leaves member default; unknown arm null, for a Node in a list and for an Outcome; Rust and TS union decoders with explicit keys refuse by construction; Rust extra-slot behaviour unmeasured [local, view].

## 4. Operator's stated value, and how each option relates (inferred)
Verbatim, operator-rapport.md:50: "nobody should have to carry bytes they don't understand ... CultCache's whole promise is that a cache's contents are self-identifying via the upfront catalog." Context: pushing back on storing retired slots as metadata (:49). [local]
- Preserve-unknown (protobuf, Unity): the older reader carries bytes the catalog does not describe. Opposite to the stated value.
- Drop silently (Avro extra fields, MessagePack slot skipping, Thrift skip): nobody carries the bytes, but the loss is silent and a re-encode no longer equals the stored bytes (persisted-record-encoding.md:4-6).
- Null the arm (current C# union behaviour): the unknown arm becomes a value the catalog does not describe either; the record decodes as if valid.
- Refuse (Avro no-branch, serde default, Rust/TS explicit-key decoders, store-header refusal): the reader declares it cannot understand the bytes; consistent with self-identifying content because undescribed content is not accepted.
- Tolerate trailing slots but refuse unknown arms (fork option 1): the catalog-described slot list governs required members; slots beyond it are skipped, which is the drop-silently pattern for slots and the refuse pattern for arms.
