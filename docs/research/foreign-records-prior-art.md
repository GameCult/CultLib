# Foreign Records: Prior Art Survey

Facts file for the single-file "foreign records" fork (lay-back versus refusing
whole-view writes while holding records the writer does not own). Eyes pass,
2026-10-02. Facts and evidence pointers only; no recommendation.

Verification marks:

- **[fetched]** the claim was read on the cited page during this pass.
- **[search]** the claim came from a search-result summary of the cited page,
  not a full read.
- **[memory]** recalled from the cited source, not re-read in this pass.
  Treat it as unverified.

## 0. Our design, for comparison

Source: `src/GameCult.Caching/Contracts/cultcache-persistence-format.md` (main, 614fd445).

- Store is `[header, catalog, records]`; the catalog "must embed a catalog entry
  for every schema referenced by the records it contains" (Embedded Schema Catalog).
- The stated purpose of the catalog is to let another implementation "decide
  whether it can: read directly, soft-migrate with warnings, or refuse the data
  honestly".
- Soft migration lists "persisted schema has extra slots the local reader can
  safely ignore or preserve" and the warning "field preserved but not surfaced
  natively". That is slot-level preservation inside a record the reader owns. The
  contract does not mention records under schema ids the reader does not own;
  the lay-back ruling lives elsewhere (grep of `src/**/*.md` for "foreign" or
  "lay-back" found nothing).
- Concurrency: "A plain flush and an unconditional commit are last-writer-wins
  ... a single file is replaced by the writer's whole view." "Conditional commit
  is the only safe multi-process write." C# and Rust implement conditional
  commit; TypeScript and Python do not yet (Transaction Visibility).

Granularity note used throughout: most systems below handle unknown *fields
inside a record whose type the writer knows*. Our fork is about unknown *whole
records* (schema ids) inside a container the writer rewrites. Each entry says
which granularity it covers.

## 1. Serialization formats

### 1.1 Protocol Buffers: unknown-field preservation

- **What it does.** Unknown fields are "well-formed protocol buffer serialized
  data representing fields that the parser does not recognize". Proto3 messages
  "preserve unknown fields and include them during parsing and in the serialized
  output, which matches proto2 behavior." [fetched] https://protobuf.dev/programming-guides/proto3/ (Unknown Fields)
- **Where preservation fails, per the docs.** Serializing to JSON; iterating
  fields to populate a new message. Mitigation given: use binary, use
  `CopyFrom()`/`MergeFrom()`. TextFormat prints unknown fields by number but
  cannot parse them back. [fetched] same page.
- **History.** Proto3 shipped dropping unknown fields. v3.5.0 release notes:
  "Unknown fields are now preserved in proto3 for most of the language
  implementations for proto3 by default." C++ and Python offer
  `DiscardUnknownFields()`, Java a `DiscardUnknownFieldsParser`, for callers who
  relied on dropping. The release notes give no rationale. [fetched]
  https://github.com/protocolbuffers/protobuf/releases/tag/v3.5.0
- **Why it was restored.** No official rationale document was found. The
  user-facing request is issue #272 (2015-04-07): "I know that unknown fields
  have been removed from proto3, but I am trying to get an explanation about why
  this change was made". The thread body was not readable in this pass. [fetched,
  partial] https://github.com/protocolbuffers/protobuf/issues/272. Secondary
  accounts attribute the reversal to pass-through and read-modify-write
  intermediaries in uncoordinated upgrades. [search]
  https://github.com/scalapb/ScalaPB/issues/406, https://kmcd.dev/posts/protobuf-unknown-fields/.
  **The primary reasoning is unverified.**
- **Cost and regret.** Go: preservation through `XXX_unrecognized []byte`
  broke users who compared proto3 messages with `==` or used them as map keys.
  [search] https://groups.google.com/d/msg/golang-nuts/F5xFHTfwRnY/sPv5nTVXBQAJ.
  JSON round trips still drop unknown fields; there is an open request to keep
  them. [search] https://github.com/golang/protobuf/issues/1390. A secondary
  source flags preserved unknown fields as a way to smuggle unvalidated payloads
  past validation at trust boundaries. [fetched] https://kmcd.dev/posts/protobuf-unknown-fields/
- **Granularity.** Field level, inside a message the reader's code knows.
  Protobuf does not preserve unknown *message types*; the closest is
  `google.protobuf.Any` (type URL plus bytes), which carries an opaque typed
  payload as a declared field. [memory]
- **Differs from us.** There is no in-band schema. Unknown bytes are
  uninterpretable without the newer `.proto`, and only tag and wire type are
  self-describing. There is no container rewrite or multi-process store
  concern; protobuf is a message codec.

### 1.2 Protobuf field-number reuse

- **Rule.** "Field numbers should never be reused. Never take a field number out
  of the reserved list for reuse with a new field definition." Listed
  consequences: "Developer time lost to debugging, A parse/merge error (best case
  scenario), Leaked PII/SPII, Data corruption." Syntax: `reserved 2, 15, 9 to 11;`
  and `reserved "foo", "bar";`. [fetched] https://protobuf.dev/programming-guides/proto3/
- Best practices: "Never re-use a tag number. It messes up deserialization."
  Reserve deleted numbers. "Almost never change the type of a field." Renaming
  breaks JSON and text format because they key on names. [fetched]
  https://protobuf.dev/best-practices/dos-donts/
- Kleppmann (2012): "you must never reuse the tag number for another field in
  future, because you may still have data stored that uses that tag for the field
  you deleted". [fetched] https://martin.kleppmann.com/2012/12/05/schema-evolution-in-avro-protocol-buffers-thrift.html
- **Named public incidents:** none found in this pass. The docs' consequence
  list is the strongest primary evidence; blog posts repeat it. **Unverified.**
- **Differs from us.** Our `schemaId` is a content hash of the canonical slot
  schema, so changing slot meaning changes the id. The reuse hazard moves from
  "number collides" to "a record under an old id is carried, or reinterpreted,
  after an undeclared rename".

### 1.3 Avro and schema registries

- **Resolution.** The reader needs the writer's schema. Fields in the writer
  schema but not the reader's are ignored. Reader fields missing from the writer
  take the reader default, or raise an error when there is no default. Aliases
  map renames. [fetched] https://avro.apache.org/docs/1.11.1/specification/ (Schema Resolution)
- **Object Container Files** carry the schema once in the header (`avro.schema`,
  required). One file has one writer schema. [fetched] same page.
- **Kleppmann (2012):** Avro needs "the exact same version of the schema as the
  writer of the data", so you either "annotate each record with its schema
  version" or use "a schema registry". [fetched] blog above.
- **Confluent wire format:** byte 0 is the magic byte (0), bytes 1 to 4 are the
  big-endian schema id; the deserializer fetches the writer schema by id.
  [fetched] https://docs.confluent.io/platform/current/schema-registry/fundamentals/serdes-develop/index.html
- **Compatibility modes:** BACKWARD (the default), FORWARD, FULL, their
  `_TRANSITIVE` forms, and NONE. BACKWARD is the default "so that you can rewind
  consumers to the beginning of the topic". Upgrade order follows the mode:
  consumers first for BACKWARD, producers first for FORWARD. [fetched]
  https://docs.confluent.io/platform/current/schema-registry/fundamentals/schema-evolution.html
- **Failure mode.** Resolution projects writer data onto the reader schema, so
  writer-only fields are gone from the decoded object. A read-modify-write by an
  older reader loses them unless the application keeps the writer record.
  [inference from the spec; no Avro doc states it as a warning]
- **Differs from us.** Kafka records are immutable log entries, so consumers
  never rewrite the producer's records. A container file is written once by one
  writer. Neither case has whole-file rewrite by several processes.

### 1.4 Thrift

- Generated readers switch on field id and call
  `TProtocolUtil.skip(iprot, schemeField.type)` in the default case: unknown
  fields are skipped. [search; generated-code pattern] Kleppmann (2012) says the
  same of protobuf and Thrift: the parser "can figure out how many bytes it needs
  to skip". [fetched]
- **Conflict.** "Thrift: The Missing Guide" says unknown fields "are not
  discarded, and if the message is later serialized, the unknown fields are
  serialized along with it". That text reads as copied from protobuf docs.
  [fetched] https://diwakergupta.github.io/thrift-missing-guide/. Apache Thrift's
  generated Java skips without retaining. **Retention is unverified and likely
  false for stock Apache Thrift.**
- Rules: never change numeric tags; new fields optional; prefix removed fields
  `OBSOLETE_`. [fetched] Missing Guide.

### 1.5 Cap'n Proto

- Field ordinals are append-only: "as long as each new member's number is larger
  than all previous members"; "You cannot change a field, method, or enumerant's
  number." [fetched] https://capnproto.org/language.html
- Unknown fields survive a copy: "If you copy the struct from one message to
  another (e.g. by calling a set() method on a parent object), the extra fields
  will be preserved." Caveat: "copying a struct value from another message into
  an element of a list will truncate the value". [fetched] https://capnproto.org/cxx.html
- The wire format carries just enough structure (struct or list, sizes) to copy
  an object recursively without its schema. [search]
  https://capnproto.org/encoding.html. Preservation is a property of the encoding,
  not of a per-type rule.
- **Differs from us.** The copy is opaque; nothing is reinterpreted. There is no
  catalog and no store.

### 1.6 FlatBuffers

- Add new fields at the end; never remove one, mark it `deprecated` instead (no
  accessors are generated); explicit `id` attributes relax ordering. Old readers
  ignore unknown fields. [search] https://flatbuffers.dev/evolution/
- The object API (unpack, mutate, pack) builds a fresh buffer from native
  fields. Whether unknown fields survive that round trip: **not verified in
  this pass** [memory: they do not].
- Known defect: C++ object API with deprecated fields generated non-compiling
  code. [search] https://github.com/google/flatbuffers/issues/5186

### 1.7 Microsoft Bond

- `bonded<T>` defers deserialization and holds a reference to the serialized
  form. "Using bonded<Response> allows the intermediary to aggregate responses,
  preserving their full content, even though schema of Response is not known
  when the aggregator is built." Transcoding through a bonded value preserves
  fields not in `T`. [fetched] https://microsoft.github.io/bond/manual/bond_cpp.html
- Untagged protocols (Simple Binary) "serialize only data and thus require that
  consumers know the payload schema via some out-of-band mechanism"; Bond also
  has runtime schemas (`SchemaDef`) for that. [fetched; SchemaDef from memory]
- Changing ordinals is a breaking change. [fetched]
- **Note.** Bond's preservation is an explicit opt-in wrapper type at the
  declared pass-through point, not default retention of everything.

### 1.8 MessagePack-based schema systems

- MessagePack itself has no schema. MessagePack-CSharp (`[Key(n)]` int keys, the
  basis of our slot layout) has no documented unknown-key or extension-data
  retention for typed objects. A search found no such feature and no issue
  requesting it. **Unverified absence.**
  https://github.com/MessagePack-CSharp/MessagePack-CSharp
- Comparable opt-in field-level retention in other ecosystems:
  Newtonsoft.Json and System.Text.Json `[JsonExtensionData]` capture unmapped
  properties into a dictionary and write them back; serde `#[serde(flatten)]` into
  a map does the same, and is incompatible with `deny_unknown_fields`. [search]
  https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/migrate-from-newtonsoft,
  https://serde.rs/attr-flatten.html. All are field level and opt in per type.
- **FIDL (Fuchsia), RFC-0033:** messages with unknown fields "MUST be validated
  and parsed successfully"; the default is `flexible` (unknown values allowed and
  exposed), and the `strict` keyword makes unknown fields invalid. Unknown
  handles must be closed by default. The RFC frames strictness as a security tool
  for protocols where misinterpreting new data is dangerous. [fetched]
  https://fuchsia.dev/fuchsia-src/contribute/governance/rfcs/0033_handling_unknown_fields_strictness

## 2. Embedded stores and document databases

### 2.1 SQLite

- **Unknown tables.** The writer modifies pages through the B-tree and rollback
  or WAL journal. Tables a program never touches are never rewritten, so there is
  no rule about them. [memory; consistent with the file format doc]
- **Unknown schema syntax.** A library older than 3.31.0 opening a schema with
  generated columns "will perceive the generated column syntax as an error and
  will report that the database schema is corrupt". Older versions can read and
  write newer files "as long as the database schema does not contain features
  ... not understood by the earlier version". [search] https://sqlite.org/gencol.html.
  The whole database is refused, not just the table.
- **File format versions.** Write version > 2: "the database file must be treated
  as read-only". Read version > 2: "that database cannot be read or written".
  [fetched] https://www.sqlite.org/fileformat2.html (offsets 18 and 19). This is
  a format-level read-only fallback for content the library cannot safely write.
- **Schema change across connections.** The schema cookie (offset 40) is bumped
  on every schema change. A prepared statement checks it and reprepares or fails
  with `SQLITE_SCHEMA`. [fetched] same page.
- `user_version` (offset 60) and `application_id` (offset 68) are application
  owned; SQLite does not use them. [fetched]
- **Whole-file rewrite.** SQLite's own comparison: custom and wrapped
  pile-of-files formats "usually require a rewrite of the entire document in
  order to change a single byte", and application concurrency logic for them is
  "a notorious bug-magnet". [fetched] https://www.sqlite.org/appfileformat.html
- **Differs from us.** The schema is SQL text in `sqlite_schema`, not a typed
  slot catalog. Writes are partial and in place, and there is one implementation
  rather than five.

### 2.2 LMDB

- Opaque byte keys and values, no schema; named sub-databases. Copy-on-write
  B+tree with MVCC. One writer at a time, serialized by a mutex in the lock file;
  readers do not block the writer and the writer does not block readers;
  several processes may open one environment. [search; lmdb.tech/doc was not
  reachable over TLS in this pass] https://en.wikipedia.org/wiki/Lightning_Memory-Mapped_Database,
  https://lmdb.readthedocs.io/
- `MDB_INCOMPATIBLE`: an operation does not match a database's flags (for
  example dupsort), or "the database type changed". [search]
- **Differs from us.** There are no types, so "foreign" is not a concept;
  untouched keys are never rewritten.

### 2.3 Core Data (Apple)

- **Model mismatch is refused.** "The model used to open the store is
  incompatible with the one used to create the store" (Cocoa error 134100)
  unless a migration runs: lightweight (inferred mapping, using
  `NSMigratePersistentStoresAutomaticallyOption` and
  `NSInferMappingModelAutomaticallyOption`) or custom/staged. [search]
  https://developer.apple.com/forums/thread/100639
- Store metadata holds per-entity version hashes (`NSStoreModelVersionHashes`).
  `isConfiguration(withName:compatibleWithStoreMetadata:)` compares them with
  the model. [search]
  https://developer.apple.com/documentation/coredata/nsmanagedobjectmodel/isconfiguration(withname:compatiblewithstoremetadata:)
- **Atomic stores (XML, binary) are the closest analogue to our single file.**
  They keep the whole object graph in memory and save the whole file: "the old
  file is not deleted until the new one has been successfully written."
  [fetched] https://developer.apple.com/library/archive/documentation/Cocoa/Conceptual/CoreData/PersistentStoreFeatures.html.
  Combined with the refusal above, an atomic store is never rewritten by a model
  that does not cover every entity in it. The page says nothing on multi-process
  access to atomic stores. [fetched: absence]
- **Differs from us.** Single runtime, entity-hash catalog in metadata, a
  migration-or-refuse gate at open. No foreign entity is ever carried.

### 2.4 Realm

- Opening with a changed schema requires a higher `schemaVersion`, plus a
  `migrationBlock` where needed; otherwise "Migration is required". Added and
  removed properties are applied to the on-disk schema automatically on
  migration. [search] https://github.com/realm/realm-js/issues/4752,
  https://realm.netlify.app/docs/swift/latest/
- A changelog entry allows opening old frozen versions "with a schema that
  contains additional classes, but not additional properties". [search]
  https://github.com/realm/realm-core/releases?page=5
- Whether a Realm opened with a class *subset* keeps the other tables untouched:
  **not verified** [memory: the tables are kept, since Realm writes in place by
  table and does not rewrite the whole file].
- Realm file-format upgrades are one-way. [memory]

### 2.5 CouchDB and PouchDB

- `PUT /{db}/{docid}` writes a new revision of the whole document; there is no
  field merge, and omitted fields are gone in the new revision. A missing or
  stale `_rev` returns `409 Conflict`: "specified revision is not latest for
  target document". The revision may be given in the body, as `rev`, or in
  `If-Match`. [fetched] https://docs.couchdb.org/en/stable/api/document/common.html
- Under replication, conflicting revisions are all kept; one deterministic
  winner is chosen and the others are marked `_conflicts`; CouchDB does not merge.
  [search] https://docs.couchdb.org/en/stable/replication/conflicts.html
- There is no schema; unknown fields survive only if the client writes them
  back. Document-mapper layers drop them: Mongoose's `strict` option "ensures
  that values passed to our model constructor that were not specified in our
  schema do not get saved to the db", and is "enabled by default". [fetched]
  https://mongoosejs.com/docs/guide.html (MongoDB, cited as the same pattern).
- **RxDB** (on PouchDB or other storage): any schema `version` > 0 requires
  `migrationStrategies` for each step; a strategy returning `null` deletes the
  document; writes during a running migration fail with `COL25`; in multi-tab
  setups "exactly one tab is running a migration of a collection". [fetched]
  https://rxdb.info/migration-schema.html. Opening storage written by a newer
  RxDB major is refused ("Cannot open database state with newer RxDB version").
  [search] https://github.com/pubkey/rxdb/issues/5836
- **Differs from us.** The per-document `_rev` gives optimistic concurrency at
  record granularity, and documents are never rewritten as a side effect of
  writing another.

### 2.6 IndexedDB

- No field schema: values are structured clones, stored by value. Schema is
  object stores and indexes, versioned by an integer. [fetched] https://www.w3.org/TR/IndexedDB/
- Opening with a higher version fires `upgradeneeded` inside a versionchange
  transaction. Opening with a *lower* version than the stored one fails with
  `VersionError`: "An attempt was made to open a database using a lower version
  than the existing version." Older code is refused outright. [fetched] same spec.
- Other open connections receive `versionchange`; if they stay open the upgrader
  gets `blocked`. [fetched] same spec and
  https://developer.mozilla.org/en-US/docs/Web/API/IDBOpenDBRequest/blocked_event
- **Differs from us.** The browser engine is the single writer implementation
  and the schema has one integer owner per database.

### 2.7 Whole-file rewrite while preserving unknown content

Systems found that rewrite a container while keeping parts they do not
understand:

- **Automerge binary format.** Programs that edit documents must "maintain all
  columns, even those that they don't understand the meaning of" and write null
  into unknown columns for new rows. Implementations "must preserve operations
  containing actions they don't support". Unknown value type codes: "MUST read and
  store the type code and `length` bytes". Changes are SHA-256 hashed over their
  binary form, so byte preservation is required for hash integrity. [fetched]
  https://automerge.org/automerge-binary-format-spec/. The unit carried is an
  immutable, content-addressed change; nothing preserved is ever re-decided by
  the carrier.
- **Core Data atomic stores** rewrite the whole file but refuse to open under an
  incompatible model (2.3), so they never carry unknown entities.
- No system was found that rewrites a whole mutable file while carrying
  mutable records of unknown types that other writers may change concurrently.
  **Absence of evidence, not a verified negative.**

## 3. Literature

- **Kleppmann, *Designing Data-Intensive Applications* (1st ed., O'Reilly 2017),
  ch. 4 "Encoding and Evolution", section "Dataflow Through Databases".** [memory,
  not re-read] The section says an older version of code may read, update and
  write back a record written by newer code, and newer fields can be lost unless
  the old code keeps unknown fields. Figure 4-7 shows this. The section contains
  the line "data outlives code" [search; confirmed in secondary notes
  https://gist.github.com/bcherny/b870a60d1650973df7e400c8603ac76d]. It also says
  rewriting data into a new schema is possible but expensive, so most databases
  avoid it. **The exact wording is unverified.**
- **Scherzinger, Klettke, Störl, "Managing Schema Evolution in NoSQL Data
  Stores", DBPL 2013.** Contrasts eager migration with *lazy* migration (migrate
  on read or touch) for schema-less stores whose data grows heterogeneous.
  [search] https://arxiv.org/abs/1308.0514
- **Ringlstetter, Scherzinger, Bissyandé, "Data model evolution using
  object-NoSQL mappers: folklore or state-of-the-art?", BIGDSE@ICSE 2016,
  pp. 33-36.** Mapper evolution annotations (rename, remove, transform on load)
  are used on GitHub, but mostly for tasks other than evolving the data model.
  [search] https://dblp.org/rec/conf/icse/RinglstetterSB16.html
- **Scherzinger and Sidortschuck, "An Empirical Study on the Design and Evolution
  of NoSQL Database Schemas", ER 2020.** [search, not read]
  https://arxiv.org/pdf/2003.00054
- **Bancilhon and Spyratos, "Update semantics of relational views", ACM TODS
  6(4), 1981, pp. 557-575.** A view update is translated by holding a chosen
  *complement* constant; a complete set of view updates has a translator iff it
  is translatable under constant complement. [search]
  https://dl.acm.org/doi/10.1145/319628.319634. Mapping (this survey's framing,
  not the paper's): a writer's owned records are a view of the store, the foreign
  records are the complement, and lay-back is constant-complement translation.
- **Foster, Greenwald, Moore, Pierce, Schmitt, "Combinators for bidirectional tree
  transformations", POPL 2005 / TOPLAS 2007.** A lens is `get: S -> V` and
  `put: V x S -> S`. Laws: GetPut, `put(get(s), s) = s`; PutGet,
  `get(put(v, s)) = v`; PutPut is the stronger optional law. `put` takes the
  source `s` as an argument, so the complement is restored from whatever `s` is
  supplied. [search for the paper; the law statements are from memory, and one
  search summary had GetPut and PutGet swapped]
  https://www.cis.upenn.edu/~bcpierce/papers/lenses-toplas-final.pdf
- **Litt, van Hardenberg, Henry, "Project Cambria" (Ink & Switch, Oct 2020).**
  Bidirectional lenses between schema versions, arranged as a graph. In
  Cambria-Automerge every write is tagged with its writer's schema and translated
  at read time, so "old changes can be evolved using lenses that didn't even
  exist at the time of the original change". Lenses travel inside the document.
  Stated limits: lossy conversions (scalar to array), one-way changes
  (split/merge), no external lookups, untested performance. "There are limits to
  interoperability." [fetched] https://www.inkandswitch.com/cambria/
- **Local-first and CRDT, Automerge:** see 2.7. Unknown ops, columns and value
  types from newer peers are preserved byte for byte and are immutable once
  written. [fetched]

## 4. Concurrency facts for whole-file rewrite

- **Last-writer-wins on whole replace** loses a concurrent writer's changes
  unless the write is conditional. Our contract already says this (section 0).
- **HTTP optimistic concurrency.** RFC 9110 §13.1.1 If-Match exists to prevent
  the "lost update" problem; a failed precondition returns 412 Precondition
  Failed (§15.5.13). [memory; the page fetch truncated before those sections]
  https://www.rfc-editor.org/rfc/rfc9110.html#section-13.1.1
- **Per-record CAS:** CouchDB `_rev`, 409 (2.5).
- **Git ref update:** `git update-ref <ref> <new> <old>` stores the new value
  "after verifying that the current value of the <ref> matches <old-oid>".
  Transactions lock every ref (`.lock` files) at `prepare`; "If all <ref>s can be
  locked with matching <old-oid>s simultaneously, all modifications are
  performed. Otherwise, no modifications are performed." [fetched]
  https://git-scm.com/docs/git-update-ref. The pattern: take an exclusive lock
  file, compare the old value under the lock, write, rename into place.
- **SQLite** serializes writers by file lock (rollback) or WAL write lock;
  schema-cookie checks catch schema drift between prepare and run (2.1).
- **LMDB:** single writer mutex across processes, MVCC readers (2.2).
- **IndexedDB:** schema change is an exclusive versionchange transaction that
  waits on other connections (2.6).
- **RxDB:** one migrating tab, writes refused during migration (2.5).
- **Crash consistency of temp-plus-rename.** Pillai et al., "All File Systems Are
  Not Created Equal: On the Complexity of Crafting Crash-Consistent
  Applications", OSDI 2014. The ALICE tool found 60 crash vulnerabilities across
  11 widely used applications; persistence properties, including how rename
  orders against data writes, vary across six Linux file systems. [search]
  https://research.cs.wisc.edu/adsl/Publications/alice-osdi14.pdf. Whether
  `fsync` of the temp file and its directory is needed for durability of the
  rename: [memory: yes on Linux ext4 and others]. Windows `MoveFileEx` with
  `MOVEFILE_REPLACE_EXISTING` and `ReplaceFile` atomicity guarantees: **not
  verified in this pass.**

## 5. Cross-cutting facts, as observed (not conclusions)

- Every system found that preserves unknown data does so at *field* level
  inside a record whose type the code owns (protobuf, Cap'n Proto, Bond opt-in,
  JSON extension data, serde flatten), or as *immutable content-addressed*
  units (Automerge changes).
- Systems that hold several record types in one store either rewrite only what
  they touch (SQLite, LMDB, Realm [unverified], CouchDB, IndexedDB), or rewrite
  the whole file and refuse a model that does not cover the file (Core Data
  atomic stores).
- Systems where older code meets a newer schema at open time do one of:
  refuse to open (IndexedDB `VersionError`, Core Data 134100, RxDB newer-major,
  SQLite unknown schema syntax), go read-only (SQLite write version > 2), or
  require a migration before any write (Realm, RxDB, Core Data).
- Protobuf moved from dropping to preserving (3.5). Its documented residual
  losses are in conversion paths (JSON, field-by-field copy), and Go's
  preservation broke message comparability.
- FIDL made preserve-and-expose the default and added `strict` as an opt-in
  refusal, justified on security grounds.

## Not verified in this pass

- Primary rationale for restoring proto3 unknown fields in 3.5.
- Any named incident caused by protobuf field-number reuse.
- Stock Apache Thrift retention of unknown fields (the Missing Guide says yes,
  the generated-code pattern says skip).
- FlatBuffers object-API round trip of unknown fields.
- MessagePack-CSharp unknown-key retention (absence).
- Realm behaviour opening with a class subset.
- Exact DDIA wording and figure number.
- LMDB primary docs (TLS failure); RFC 9110 section text.
- Windows rename and replace atomicity semantics.
