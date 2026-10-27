Serves the C2a fork on finding variants:finding:cut-land-c2a-gate.s1.reflist-record-read-only-at-head (campaign variants), 2026-10-23.

# Prior art: read with an old writer schema, then write back

Facts only. Pages were fetched 2026-10-23 through a summarising fetcher; "not stated" means the fetched page did not say it, not that no source does. Items marked (memory) are my background knowledge, not verified from a fetched page.

## 1. Apache Avro
- Source: https://avro.apache.org/docs/current/specification/ (Schema Resolution; summarised via apache/avro doc source), Java `DataFileWriter` javadoc (1.11.1).
- Model: the writer schema travels with the data. The reader resolves it against its own reader schema. Resolution is a read-time projection; the spec defines no write-back.
- Data loss on projection: "the writer's value for that field is ignored" when the reader schema lacks the field. A reader-only field takes its default, or the read errors if it has none.
- Write-back: the spec states no rule. Object container files store one schema in the header ("Each datum in a file is of the same schema"). `appendTo` is documented only as "Open a writer appending to an existing file"; it does not say which schema the append uses. (memory: Java `appendTo` reuses the file's header schema, so an append never restamps.)
- Stated reason for the read-time design: readers and writers evolve separately (fields added or removed in a different software version).

## 2. Protocol Buffers (proto3)
- Source: https://protobuf.dev/programming-guides/proto3/ (Unknown Fields).
- On write: unknown fields are "included during parsing and in the serialized output" (matches proto2). Round-trip keeps fields the current type does not know.
- Loss cases the page lists: serializing to JSON, and copying a message field by field into a new message (it recommends `CopyFrom`/`MergeFrom`).
- No schema id or version is written. The wire format carries only field numbers, so "restamp" has no meaning here.
- Not stated: behaviour in versions before 3.5 (memory: proto3 dropped unknown fields before 3.5, then reversed). The Java reference page says nothing on preservation.
- Stated reason: preserve data across a mixed-version fleet.

## 3. Confluent Schema Registry
- Source: https://docs.confluent.io/platform/current/schema-registry/fundamentals/schema-evolution.html
- BACKWARD (default): consumers on the new schema read data written with the new or the previous version. FORWARD: data written with the new schema is readable by consumers on the old one. FULL: both.
- Write: "Each schema version gets a unique ID and an incremented version number." A producer writes under the schema it holds (its own current version). The id is stamped per message at serialisation, from the producer's schema.
- Upgrade order is how the rules prevent loss. BACKWARD means upgrade consumers before producers. FORWARD means upgrade producers first, and keep old-schema data away from consumers until they are upgraded.
- Not stated on the page: what a consume-modify-produce app writes. (memory: it re-serialises under its own latest schema, so fields it does not model are lost unless a compatibility rule kept them.)
- The registry gates registration. It does not gate individual rewrites.

## 4. Document DBs and ORMs with stored versions
### MongoDB schema-versioning pattern
- Sources: https://www.mongodb.com/blog/post/building-with-patterns-the-schema-versioning-pattern ; https://www.mongodb.com/docs/manual/data-modeling/design-patterns/data-versioning/schema-versioning/
- Documents carry `schema_version` (absent means 1). The app keeps a handler per version and writes new documents at the current version.
- Migration strategy is the app's choice: migrate all, migrate "when accessed" (lazy), or never.
- Not stated: whether an update of an old document restamps it. The manual's update example branches on `schemaVersion` and writes into each version's own shape. It notes its `else: null` branch adds stray null fields to the other shape.
- Stated reasons: no downtime, failed migrations easier to handle, timing flexibility. Cost: handler code per live version.

### Realm (Atlas Device SDKs, now deprecated)
- Source: https://www.mongodb.com/docs/atlas/device-sdks/sdk/swift/model-data/change-an-object-model.md
- Per-file schema version. A changed schema needs an incremented `schemaVersion`, plus a `migrationBlock` for non-trivial changes. The file is migrated whole at open, before any read or write, and Realm "will throw an error" if the version is lower or the same but the schema differs.
- No lazy per-object restamp. Writes happen only on a migrated file.
- Data loss: the manual says "Deleting a property will not impact existing objects", i.e. removal is automatic and needs no developer step. Required new properties and renames need a manual block. (memory: removed-property values are dropped from the file.)

### Core Data
- Source: web search results only (Apple forum threads 745114 and 785427, WWDC22-10120, "staged migrations" doc). The Apple docs URL I tried returned 404.
- A store whose model hash differs from the current model fails to open with an incompatible-version error. Opening migrates the whole store when `NSMigratePersistentStoresAutomaticallyOption` and `NSInferMappingModelAutomaticallyOption` are set. Lightweight migration covers only obvious patterns (add entity, remove relationship, required to optional, rename). Anything else needs a staged or custom migration.
- Migration is store-wide at open. It does not happen per record on write.

### EF Core
- Not fetched. (memory: no per-row schema stamp. Migrations are explicit, ordered DDL, tracked in a `__EFMigrationsHistory` table.)

## 5. Old-schema data held read-only until explicit migration
### Elasticsearch archive indices
- Sources: https://www.elastic.co/docs/deploy-manage/upgrade/deployment-or-cluster/reading-indices-from-older-elasticsearch-versions ; https://www.elastic.co/guide/en/elasticsearch/reference/current/archive-indices.html ; 8.0 migration notes (7.0+ indices readable, 6.x or earlier must be reindexed).
- Indices from an older major are readable through search and field-caps. "They do not support the Get API or any write APIs."
- The route to a writable copy is reindex into a new index that is "fully compatible with the current Elasticsearch version". `_source` is readable, so no data is lost on reindex.
- Stated reason: the page does not say why they are read-only. It describes archive access as "slower read-only access", with limited query capability. Some legacy mappings cannot be imported (the original is kept in `_meta`), and that is the one place the page shows a lossy translation.
- Third-party (Search Guard blog, unverified against Elastic): in 9.x a plain `index.blocks.write` does not satisfy the boot check; `index.verified_read_only` is needed.
- This is the closest match to option (b) in the fetched set: an old format, readable but not writable, left until the owner reindexes it.

## Where restamp-on-write loses data (from the sources above)
- Avro: a field present in the writer schema and absent from the reader schema is dropped at read, "ignored". Any rewrite from the resolved value would omit it.
- Protobuf: unknown fields survive a binary round trip. They are lost via JSON, or when copied field by field.
- Realm: removed properties are deleted by the automatic migration.
- MongoDB: a handler that writes only the current shape can lose or relocate old-shape fields (the manual's example adds nulls).
- Confluent: loss is prevented by compatibility rules at registration time, not at each write.

## Pattern across the set
- No fetched system restamps a single record on write when the record's writer schema differs from the application's type, on an app-visible, per-record basis.
  - Avro, protobuf and Confluent carry the schema per file or message.
  - Realm, Core Data and Elasticsearch move or block the whole store or index at open.
  - MongoDB leaves it to the app.
- Whole-store migration at open is the usual explicit step (Realm, Core Data). Elasticsearch is the one case of read-only-until-reindex.
- Not found: a system that declares per-schema-id compatibility, as in option (b). Avro schema resolution and Confluent compatibility rules are the nearest analogues. Both are read-side or registration-side declarations.
