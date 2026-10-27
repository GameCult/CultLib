# Prior art: structural versus name-inclusive schema ids

Serves the store-identity question raised 2026-10-27 in campaign `variants` (operator: member and declared names vary with runtime naming conventions). Eyes pass, session `self-2026-10-27b-eureka`. Facts only; items marked "summary" rest on search summaries not opened.

# Prior art: structural vs name-inclusive schema identity (campaign variants)

Eyes pass, 2026-10-27. Facts and evidence pointers only. "(fetched)" = page opened via WebFetch (the answer is a small-model extraction of the page, not raw text). "(summary)" = WebSearch result summary, page not opened.

## 0. Local facts (read-only)

- `F:\Projects\CultLib\src\GameCult.Caching\CultCache.cs:717-718` schemaId = Sha256(BuildSemanticFingerprint(...)). `:735-761` fingerprint = `schemaName|schemaVersion` then per member ordered by Slot: `slot:MemberName:TypeName:ref|value:TargetSchemaName:one|many`. `contentHash` (`:716`) is a separate Sha256 of canonical JSON (`:763+`).
- `TypeName` = `CultSchemaTypeNames.FromType`: CLR `Type.FullName` (namespace-qualified), generics as `Name<args>`, arrays `T[]` (`src\GameCult.Caching\CultSchemaTypeNames.cs:9-31`). So the fingerprint contains .NET type names as well as member names.
- Contract `Contracts\cultcache-persistence-format.md:257-284` lists what canonicalization must preserve: slots, types, required/default, nullability, collection semantics, enum constraints, encoding shape. Member names and declared schema name are not in that list. `:171-179` says the id is not derived from C#/Rust/TS type names and the same shape should give the same id "regardless of origin language". `:180-190` defines schemaId = "same semantic persistence shape?" vs contentHash = "exact same canonical schema document?". Example catalog entry (`:140-170`) carries optional `supersedesSchemaIds`, `migrationFamily`; its example canonicalSchema fields do carry `"name": "Name"` (PascalCase). Soft-migration reader flow `:286-345` (exact_match / soft_migrate / reject).
- Runtimes do not derive ids; they emulate C# names:
  - Python: `packages\cultcache-py\src\cultcache_py\documents.py:326` `member_name=_pascal_case(field.name)`; `:516-517` splits on `_` and capitalises. `schema_id` defaults to `self.type` (`:218`); `compatible_schema_ids` field (`:188,242`).
  - TypeScript: `packages\cultcache-ts\src\codec.ts:226-232` `pascalCase` (splits on non-alphanumerics), `:267` `memberName: pascalCase(field.name)`; throws if a codec has no C# type name (`:~262`).
  - Rust: `packages\cultcache-rs\src\lib.rs:295,308` `serde(rename_all = "camelCase")` on envelope structs; `schema_id: Some(T::TYPE.to_string())` at `:2856`. I did not trace how Rust document types obtain a sha256 id.
  - Python test pins a literal id from shared vectors: `packages\cultcache-py\tests\test_cultcache.py:808` (`tests\vectors\document-variants-c0`).
- C# also has `CompatibleSchemaIds` (`CultCache.cs:28,313,570`): a record whose persisted id is in the list is accepted (an alias-list mechanism; I saw only the grep lines, not the logic).
- Ruling `variants:ruling:wire-id-is-version` (eureka-state, InForce, ruled 2026-09-30, operator quote "Agreed on schema identity"): wire schema id = declared version string `<name>.vN`; every runtime stamps/resolves it exactly; a wire binding without `.v<digits>` is refused; "Store ids are unchanged." Cited source: document-variants-cut.md:552-555 (not opened).

## 1. How established formats define identity/compatibility, and whether names participate

Protocol Buffers (fetched https://protobuf.dev/programming-guides/proto3/ and /json/)
- Field number identifies the field in the binary wire; changing it = delete + add. Field names appear only in TextProto/JSON encodings; reserve deleted numbers and, for JSON/text, names. The proto3 page has no explicit safe/unsafe entry for renaming; names matter only for JSON/text.
- JSON mapping: names become lowerCamelCase unless `json_name` is set; "Parsers accept both the lowerCamelCase name" and the original proto name (/programming-guides/json/).
- Message type identity in `Any` is by name: default type URL `type.googleapis.com/<package>.<message>`, "a globally unique identifier" (proto3 page, #any). Type identity is nominal; compatibility is by field numbers.

Apache Avro (fetched https://avro.apache.org/docs/1.11.1/specification/)
- Parsing Canonical Form (PCF): STRIP keeps only attributes relevant to parsing (type, name, fields, symbols, items, values, size); doc and aliases dropped; FULLNAMES replaces short names with fullnames and drops namespace attributes. Textually equal PCF means "the same" for any reader. Names ARE in the canonical form (record and field names), as is field declaration order. Defaults are absent from the kept list (extractor's inference; spec text not confirmed).
- Fingerprints: CRC-64-AVRO in single-object encoding (marker C3 01 + 8-byte LE fingerprint); spec recommends at least a 64-bit Rabin, with SHA-256 / MD5 options for larger sets.
- Name rules: `[A-Za-z_][A-Za-z0-9_]*`, dot-separated namespaces, equality on case-sensitive fullname.
- Resolution: records match on same unqualified name; "Named types and fields may have aliases"; implementations "may optionally" use aliases to map writer to reader. So in Avro names participate in identity (fingerprint) and resolution; aliases are the rename mechanism and are excluded from the fingerprint.

Cap'n Proto (fetched https://capnproto.org/language.html)
- Files and types carry 64-bit IDs; `capnp id` generates them randomly. Default ID for a declaration without one = first 8 bytes of MD5 of (parent scope ID, declaration name). "You cannot change the name of a type that doesn't have an explicit ID"; symbolic names can change if ID/ordinals stay. Stated rationale for IDs: short, unambiguous, full names waste wire space. The page does not say why structural hashes were rejected; I found no Varda statement (search pointed at a mailing-list thread "Schema Refactoring and Unique IDs", Aug 2023, unopened).
- Encoding page (https://capnproto.org/encoding.html, fetched): adding fields is backward compatible because field positions depend only on lower-numbered fields; nothing on type identity.

FlatBuffers (fetched https://flatbuffers.dev/evolution/ and /schema/)
- Field positions by declaration order or explicit `id: n` (contiguous from 0, on all fields if used). Renaming "generally OK" because names are not in the binary. `file_identifier` is exactly 4 chars at bytes 4-7; docs frame it as a file-level check and recommend unions for distinguishing messages.

Thrift (fetched https://raw.githubusercontent.com/apache/thrift/master/doc/specs/thrift-binary-protocol.md)
- Binary protocol: field header = type + field id; "the field name is not encoded". (IDL page gave nothing on omitted ids.)

MessagePack-CSharp (fetched https://github.com/MessagePack-CSharp/MessagePack-CSharp, first 100k of 105k chars)
- `[Key(int)]` on all members => array encoding; string keys => map. Keys should be sequential from 0 (gaps cost nulls); do not reuse indexes of removed members; keep obsolete members until clients update. Union keys are ints, unique, serialized as [key, object]. No type identity in the payload.

Microsoft Bond (fetched https://microsoft.github.io/bond/manual/bond_cpp.html)
- Changing field ordinals breaks wire compat; no explicit versioning; Simple JSON matches by field name so a rename breaks it. No schema hash/GUID documented there. Bebop: wiki page empty (points to docs.bebop.sh, not opened).

Unison (fetched https://www.unison-lang.org/docs/the-big-idea/ and /docs/language-reference/unique-types; hash size from search summary)
- Definitions identified by hash of syntax tree; names are "separately stored metadata"; hash excludes names (512-bit SHA3 per search summary, unopened).
- Structural types: "Unison does not uniquely identify a type by its name"; same-shape types are the same type (Suit/Direction, four nullary constructors, interchangeable). `unique` types: compiler generates a UUID that feeds the hash, so identity becomes nominal. Which is the default keyword was not confirmed.

IPLD (fetched https://ipld.io/docs/schemas/)
- Matching is structural: "the names of types aren't expected to be found in the serialized data"; match is by structural outline. The page does not say schemas are themselves content-addressed. Versioning is left to developers; "Migrations" subpage not opened.

## 2. Failure modes of purely structural identity

- Unison documents it directly: structurally identical enums are one type unless declared `unique` (unison-lang.org/docs/language-reference/unique-types, fetched). Its mitigation is a per-declaration UUID mixed into the hash (identity re-nominalised).
- Language literature (summary, pages not opened): structural typing is prone to accidental conformance/type equivalence; nominal typing prevents it at the cost of rigidity (Wikipedia "Nominal type system"; arXiv 1007.2123 lecture notes; Erlang nominals doc https://www.erlang.org/doc/system/nominals.html says nominal types exist to prevent misuse of same-structure types). TypeScript "brand" = phantom discriminator field. No serialization-specific source found.
- Formats that avoid the collision by carrying a name or tag outside the shape: protobuf Any type URL (full name), Avro fullname in PCF, Cap'n Proto explicit IDs, Unison `unique`, MessagePack-CSharp union key ints, FlatBuffers file_identifier/unions.
- Not found: a documented incident of two same-shape serialized types colliding under a structural id (one search, nothing).

## 3. Failure modes of name-inclusive identity across languages

- Protobuf: default camelCase conversion is "always valid but not necessarily unique"; proto3 compiler rejects JSON-name conflicts; serialized duplicate keys parse differently by runtime (Edition Zero design doc, summary: https://flutter.googlesource.com/third_party/protobuf/+/HEAD/docs/design/editions/edition-zero-json-handling.md). Snake_case vs camelCase JSON mismatches in practice: https://github.com/fullstorydev/grpcurl/issues/105 (summary); https://github.com/open-telemetry/opentelemetry-proto/issues/476 (summary: spec contradicted itself on camelCase vs original names). Protobuf's mitigation: parsers accept both forms.
- Serde (fetched https://serde.rs/container-attrs.html): `rename_all` accepts lowercase, UPPERCASE, PascalCase, camelCase, snake_case; per-field `rename`.
- Avro restricts names to `[A-Za-z_][A-Za-z0-9_]*` with case-sensitive fullname equality, so snake_case and PascalCase spellings are distinct names; renames go through aliases, which PCF drops.
- Cap'n Proto: auto-derived IDs depend on the declaration name, so a rename without an explicit ID changes the ID.
- Bond Simple JSON: rename breaks compatibility (name matching).
- Local: Python/TS already convert to PascalCase to reproduce C#'s fingerprint (section 0); the fingerprint's TypeName is the CLR full name (`CultSchemaTypeNames.cs:30`) and TS throws when a codec lacks a C# type name.
- Registry normalisation (summary): Confluent Schema Registry `normalize.schemas=true` maps semantically-equal schemas to one id (https://www.confluent.io/blog/best-practices-for-confluent-schema-registry/); sources disagree on whether JSON Schema property order is normalised.

## 4. Migration precedents for changing identity derivation

- Git SHA-1 -> SHA-256 (fetched https://git-scm.com/docs/hash-function-transition): per-repo format marker (`extensions.objectFormat`, `compatObjectFormat`, `repositoryFormatVersion=1` makes old git refuse); bidirectional SHA-256<->SHA-1 mapping (pack idx v3, `loose-object-idx`); four transition modes (dark launch, early, late, post); conversion at the network boundary. It is a design doc, not a statement of what has shipped.
- IPFS CIDv0 -> CIDv1 (fetched https://docs.ipfs.tech/concepts/content-addressing/): v0 (46 chars, "Qm") and v1 (self-describing version/codec/multihash) coexist; `ipfs cid format -v 1` converts; v1->v0 only for dag-pb.
- Docker manifest schema 1 -> 2 (summary, mirror pages unopened): digests differ per manifest format; pull-by-digest gets no translation, pull-by-tag may be translated; repush converts. https://pulp.plan.io/issues/2099.
- Warpcast API hash v1 -> v2 (summary, https://warpcast.notion.site/Warpcast-API-Hash-Migration-Public-ccdd6de8ac604783b9596cfd623ddbac): two-week dual-hash period, then only v2.
- Cap'n Proto explicit-ID escape hatch for renamed/moved declarations; Avro aliases as the rename bridge in resolution; MessagePack-CSharp keep-obsolete-members (all fetched, above).
- Local: contract `supersedesSchemaIds` / `migrationFamily` (`cultcache-persistence-format.md:~140-170`) and C#/Py `CompatibleSchemaIds` exist as alias lists.
- Not found: Unison changing its hash derivation (one search, no docs); any serialization store that switched from name-inclusive to structural ids.

## Could not verify / not done
- Cap'n Proto designer's rationale against structural hashing (not in docs; mailing list unopened).
- Avro: whether PCF drops defaults (spec text) and any JIRA on it; spec page was summarised by the fetch model.
- Bebop, Thrift IDL id-omission rule, Unison default struct/unique keyword, IPLD Migrations page, Edition Zero doc (summary only), Confluent blog (summary only), nominal/structural papers (titles only).
- Rust runtime's sha256 derivation path; `document-variants-cut.md:552-555` not opened; `CompatibleSchemaIds` logic not read.
- eureka-state worked (whoami ok); only the one ruling was queried.
