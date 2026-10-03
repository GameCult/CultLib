# Read-Only Record Signal: Prior Art Survey

Facts file for Eureka question `variants:question:read-only-record-signal`: how a
developer learns that a held record is read-only before a write is refused. Its
options are (a) no new API, (b) a live query such as
`CultCache.IsWritable(key)`, and (c) a list beside `ForeignRecords`. Eyes pass,
2026-10-03. Facts and evidence pointers only; no recommendation.

Verification marks:

- **[fetched]** the claim was read on the cited page or source during this pass.
- **[search]** the claim came from a search-result summary of the cited page,
  not a full read.
- **[memory]** recalled from the cited source, not re-read in this pass.
  Treat it as unverified.

Signal shapes used below:

- **property**: a flag or state on the loaded object itself.
- **query**: a call on the store, session or registry that answers at call time.
- **list**: an enumeration of affected items, captured or computed.
- **type**: the read-only status is a distinct static type.
- **exception only**: nothing to ask; the write is the first signal.

## 0. Our design, for comparison

Source: branch `hands/variants-c2a` (head e9a66876), `src/GameCult.Caching/CultCache.cs`.

- `CultDocumentRegistry.Declares(string schemaId)` (line 738) is `internal`. Its
  comment: "The one answer to whether a cache may write, relabel or remove a
  record stored under this id ... Read live, so a type registered later that
  declares the id claims its records at their next write." [fetched]
- `GetRequired<T>()` (line 631) registers lazily, so the set of declared ids can
  grow after load. [fetched]
- The backing store already exposes `ForeignRecords` (line 3421), an
  `IReadOnlyList<CultForeignRecord>` "the last load found and carries untouched",
  set at load (`SetForeignRecords`, line 3599). `CultForeignRecord` (line 162)
  carries `Key`, `SchemaId`, `SchemaName`, `StoredAt`. [fetched]
- The store also has a store-level `public bool IsReadOnly { get; }` (line 3419),
  fixed at construction. [fetched]
- Main (162bd1fc) exposes `LastSchemaMigrationReports` (CultCache.cs:3136), whose
  entries carry no record key (per the question text). [fetched for the
  property; the no-key claim is the question's]

## 1. ORMs

### EF Core: no-tracking queries and keyless entity types

- Signal shape: **query** on the context, plus a **model** query.
  `context.Entry(e).State` returns an `EntityState`; `Detached` means "not being
  tracked by the DbContext", and its "Action on SaveChanges" is none.
  ([Change Tracking](https://learn.microsoft.com/en-us/ef/core/change-tracking/), "Entity states" table) [fetched]
- No-tracking results (`AsNoTracking`, or `QueryTrackingBehavior.NoTracking` on
  the context) are plain instances; changes to them are not persisted by
  `SaveChanges`. ([Tracking vs. No-Tracking](https://learn.microsoft.com/en-us/ef/core/querying/tracking), "No-tracking queries") [fetched]
- Keyless entity types "are never tracked for changes in the DbContext and
  therefore are never inserted, updated or deleted"; their mapped object is
  "treated as a read-only query source".
  ([Keyless Entity Types](https://learn.microsoft.com/en-us/ef/core/modeling/keyless-entity-types), "characteristics", "Mapping to database objects") [fetched]
- Attempting to `Attach`/`Update` a keyless instance throws
  `InvalidOperationException` with `CoreStrings.KeylessTypeTracked`: "Unable to
  track an instance of type '{type}' because it does not have a primary key."
  ([CoreStrings.KeylessTypeTracked](https://learn.microsoft.com/en-gb/dotnet/api/microsoft.entityframeworkcore.diagnostics.corestrings.keylesstypetracked?view=efcore-8.0)) [search]
- The model answers keylessness: `IReadOnlyEntityType.IsKeyless` /
  `FindPrimaryKey() == null`. [memory]
- Pitfall: modifying a no-tracking instance and calling `SaveChanges` is silent;
  nothing is written and nothing is thrown. `ChangeTracker.HasChanges()` returns
  false (Change Tracking page, closing tip). [fetched]

### Hibernate: @Immutable and read-only entities

- Signal shape: **query** on the session. `Session.isReadOnly(Object
  entityOrProxy)`: "Is the specified entity or proxy read-only?";
  `isDefaultReadOnly()` for the session default; `setReadOnly(Object, boolean)`
  flips one entity. ([Session javadoc 6.4](https://docs.hibernate.org/orm/6.4/javadocs/org/hibernate/Session.html)) [fetched]
- Read-only can be set per session (`setDefaultReadOnly`), per query
  (`Query.setReadOnly(true)`, before execution) or per entity.
  ([Hibernate 4.3 manual ch. 12, 12.1.2-12.1.3](https://docs.hibernate.org/orm/4.3/manual/en-US/html/ch12.html)) [fetched]
- Write behaviour: changes to read-only entities are ignored at flush, no
  exception ("Hibernate will not update simple properties or updatable
  single-ended associations"); "A read-only entity can be deleted." (ch. 12.1) [fetched]
- `@Immutable`: "Changes made in memory to the state of an immutable entity are
  never synchronized to the database. The changes are ignored, with no exception
  thrown." Immutable collections, by contrast, throw `HibernateException` on add
  or remove. ([@Immutable javadoc 6.2](https://docs.hibernate.org/orm/6.2/javadocs/org/hibernate/annotations/Immutable.html)) [fetched]
- Bulk HQL updates of immutable entities: setting
  `hibernate.query.immutable_entity_update_query_handling_mode` chooses `warning`
  (default) or `exception`. ([QuerySettings 6.6](https://docs.hibernate.org/orm/6.6/javadocs/org/hibernate/cfg/QuerySettings.html)) [search]
- Pitfall: the default for entity changes is silent loss; the read-only state is
  per session, so the same row is writable in another session.

### ActiveRecord: readonly?

- Signal shape: **property** on the record. `readonly?` "returns true if the
  record is marked as read-only"; `readonly!` marks it.
  ([ActiveRecord::Core](https://api.rubyonrails.org/classes/ActiveRecord/Core.html)) [fetched]
- Relation-level: `User.readonly` marks every loaded record;
  `users.first.save # => ActiveRecord::ReadOnlyRecord: User is marked as readonly`.
  ([QueryMethods#readonly](https://api.rubyonrails.org/classes/ActiveRecord/QueryMethods.html)) [fetched]
- Write behaviour: save, update or destroy raise `ActiveRecord::ReadOnlyRecord`. [fetched]
- The flag is set when the record is loaded and lives on the in-memory object; it
  is not re-derived from the database or the model at write time. [memory]

## 2. Document stores and embedded databases

### Realm (Kotlin SDK): frozen objects

- Signal shape: **property** functions on the object.
  `BaseRealmObject.isFrozen()`: "A frozen object is tied to a specific version of
  the data in the realm and fields retrieved from this object instance will not
  update"; also `isManaged()` and `isValid()` ("the Realm is open and the
  underlying object has not been deleted").
  ([BaseRealmObjectExt.kt L41-71](https://github.com/realm/realm-kotlin/blob/main/packages/library-base/src/commonMain/kotlin/io/realm/kotlin/ext/BaseRealmObjectExt.kt)) [fetched]
- Recovery path: `MutableRealm.findLatest(obj)` returns a live, writable copy
  inside a write block, or `null` "if the object has been deleted".
  ([MutableRealm.kt L50-56](https://github.com/realm/realm-kotlin/blob/main/packages/library-base/src/commonMain/kotlin/io/realm/kotlin/MutableRealm.kt)) [fetched]
- Write behaviour: writing to a frozen object throws. Early SDKs threw a generic
  "Wrong transactional state ..." message; issue #297 asked for "Frozen objects
  cannot be modified" plus a pointer to `findLatest`.
  ([realm-kotlin#297](https://github.com/realm/realm-kotlin/issues/297)) [fetched]
- Pitfall: `isFrozen()` is stable for a given instance, but `isValid()` is live
  and can flip when another writer deletes the object.

### Couchbase Lite

- Signal shape: **type**. "By default, a document is immutable when it is read
  from the database. Use the Document.ToMutable() to create an updatable
  instance." `Document` versus `MutableDocument` makes the distinction static.
  ([Couchbase Lite C# Documents](https://docs.couchbase.com/couchbase-lite/current/csharp/document.html), "Mutability") [fetched]
- Write conflicts with other writers are handled at save by concurrency control
  (last-write-wins or fail-on-conflict) or a conflict handler. [memory]

### LiteDB

- Signal shape: **store-level option** only. Connection string `ReadOnly`
  ("Open datafile in read-only mode"). No per-document read-only state found.
  ([LiteDB connection string](https://www.litedb.org/docs/connection-string/)) [fetched]

### SQLite

- Signal shape: **store query**. `sqlite3_db_readonly(D,N)` "returns 1 if the
  database N of connection D is read-only, 0 if it is read/write, or -1 if N is
  not the name of a database". ([sqlite3_db_readonly](https://www.sqlite.org/c3ref/db_readonly.html)) [fetched]
- `sqlite3_stmt_readonly()` answers whether a prepared statement writes. [memory]

### Core Data

- Signal shape: **store property** plus an open option.
  `NSPersistentStore.isReadOnly`: "A Boolean value that indicates whether the
  persistent store is read-only" (get/set);
  `NSReadOnlyPersistentStoreOption`: "A flag that indicates whether a store is
  treated as read-only", default false.
  ([isReadOnly](https://developer.apple.com/documentation/coredata/nspersistentstore/isreadonly),
  [NSReadOnlyPersistentStoreOption](https://developer.apple.com/documentation/coredata/nsreadonlypersistentstoreoption)) [fetched]
- Granularity: store, not object. An object's store is reachable through its
  `objectID.persistentStore`. [memory]

## 3. Serializers and schema evolution: unknown or foreign data

### Protocol Buffers

- Unknown fields: "Proto3 messages preserve unknown fields and include them
  during parsing and in the serialized output." They are lost on JSON
  serialization or when copying field by field into a new message.
  ([proto3 guide, Unknown Fields](https://protobuf.dev/programming-guides/proto3/)) [fetched]
- Signal shape for unknown fields: **property/list** on the message,
  `getUnknownFields()` returning an `UnknownFieldSet` (Java). [memory]
- `Any` with a foreign type: **query** `any.is(Foo.class)` returns a boolean;
  `unpack(Class)` throws `InvalidProtocolBufferException`.
  ([Any javadoc](https://protobuf.dev/reference/java/api-docs/com/google/protobuf/Any.html)) [fetched]
- Granularity: fields inside a known message, or one embedded payload; no
  ownership or write-permission concept.

### Apache Avro

- Writer fields absent from the reader schema are dropped: "the writer's value
  for that field is ignored". A union branch the reader lacks: "if none match, an
  error is signalled". ([Avro 1.11.1 spec, Schema Resolution](https://avro.apache.org/docs/1.11.1/specification/)) [fetched]
- Signal shape: **exception only** for unresolvable data; nothing marks a record
  as carrying dropped fields.

### Unity SerializeReference: missing types (closest found to CultCache)

- A host asset holds managed references whose recorded type (assembly,
  namespace, class) is not in the current project.
- Signal shape: **query** returning a bool and a **list**.
  `SerializationUtility.HasManagedReferencesWithMissingTypes(Object obj)`: "returns
  true if one or more managed references is missing its type."
  `GetManagedReferencesWithMissingTypes` returns the
  `ManagedReferenceMissingType` entries: `assemblyName`, `className`,
  `namespaceName`, `referenceId`, `serializedData`.
  ([HasManagedReferencesWithMissingTypes](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/SerializationUtility.HasManagedReferencesWithMissingTypes.html),
  [ManagedReferenceMissingType](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/ManagedReferenceMissingType.html)) [fetched]
- Behaviour: the field stays null, the persisted state is retained on resave, and
  if the type returns later, "the state of the Managed Reference object can be
  recovered". `ClearManagedReferenceWithMissingType` removes one deliberately.
  ([GetManagedReferencesWithMissingTypes](https://docs.unity3d.com/ScriptReference/SerializationUtility.GetManagedReferencesWithMissingTypes.html)) [search]
- Both calls are made on demand against a host object; the docs do not say the
  list is cached at load. A sibling `PrefabUtility.HasManagedReferencesWithMissingTypes`
  exists for prefab instances. [search]

### .NET DataContractSerializer: IExtensibleDataObject

- Unknown members round-trip through an opaque `ExtensionDataObject` property on
  the object. It cannot be inspected, so it is a carrier, not a signal. [memory]

## 4. File systems and object stores

### POSIX access(2) and TOCTOU

- Signal shape: **query** on the path. `access(path, W_OK)` fails with `EROFS` on
  a read-only file system. The check uses the real UID/GID, "rather than the
  effective IDs as is done when actually attempting an operation".
  ([access(2)](https://man7.org/linux/man-pages/man2/access.2.html)) [fetched]
- Pitfall: checking before `open(2)` "creates a security hole" because the state
  can change between check and use (access(2) NOTES). [fetched]
- Python's `os.access` docs repeat the warning and show the EAFP alternative: try
  the `open` and catch `PermissionError`.
  ([os.access](https://docs.python.org/3/library/os.html#os.access)) [fetched]
- CWE-367 defines TOCTOU as checking "the state of a resource before using that
  resource, but the resource's state can change between the check and the use".
  Mitigations listed: drop the check, make check and use atomic, lock before the
  check, recheck after use, shrink the window.
  ([CWE-367](https://cwe.mitre.org/data/definitions/367.html)) [fetched]

### Amazon S3 Object Lock

- Signal shape: **metadata on the object version**, read with HEAD/GET.
  `HeadObject` returns `x-amz-object-lock-mode` (`GOVERNANCE | COMPLIANCE`),
  `x-amz-object-lock-retain-until-date` and `x-amz-object-lock-legal-hold`,
  "only returned if the requester has the `s3:GetObjectRetention` permission"
  (legal hold: `s3:GetObjectLegalHold`). Dedicated calls exist as well
  (`GetObjectRetention`, `GetObjectLegalHold`).
  ([HeadObject](https://docs.aws.amazon.com/AmazonS3/latest/API/API_HeadObject.html)) [fetched]
- Write behaviour: a versioned DELETE of a locked version returns `403 Access
  Denied`; a simple DELETE succeeds and adds a delete marker. A PUT to the same
  key succeeds and creates a new version; the old version stays locked. Governance
  mode can be bypassed with `s3:BypassGovernanceRetention` plus the
  `x-amz-bypass-governance-retention:true` header.
  ([Object Lock](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock.html), "How deletes work", "Retention modes") [fetched]
- Pitfalls: the signal depends on read permission (absent header does not mean
  unlocked); legal holds can be "freely placed and removed" by anyone with
  `s3:PutObjectLegalHold`, so the answer can change between HEAD and DELETE; under
  variable retention the retain-until date "moves forward as time passes" while an
  event hold is on. (Object Lock page, "Legal holds", "Retain-until-date behavior") [fetched]

### Git LFS lockable files

- Ownership signal enforced through the file system: files marked lockable "are
  made read-only in the working copy when not locked by the current user"
  (`lfs.setlockablereadonly`, default true).
  ([git-lfs-config](https://github.com/git-lfs/git-lfs/blob/main/docs/man/git-lfs-config.adoc), L448-455) [fetched]
- The authoritative check is server-side at push: Git LFS "verifies that pushes
  don't alter files locked by others".
  ([git-lfs-lock](https://github.com/git-lfs/git-lfs/blob/main/docs/man/git-lfs-lock.adoc)) [fetched]
- Pitfall: the read-only bit reflects lock state as of the last checkout or lock
  command; `chmod` defeats it locally, and the push check decides. [memory for the
  staleness; the push check is fetched]

## 5. Collections (the type-system version of the same question)

- .NET `ICollection<T>.IsReadOnly`: a **property**. Read-only here "does not
  indicate whether individual elements of the collection can be modified"; an
  array cast to `ICollection<T>` reports true while its elements stay writable.
  Mutators throw `NotSupportedException`.
  ([ICollection<T>.IsReadOnly](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.icollection-1.isreadonly)) [fetched; the exception is memory]
- Java `Collection`: **exception only**. Mutators are "optional operations" that
  throw `UnsupportedOperationException`; the interface has no query for
  modifiability. ([Collection, Java 21](https://docs.oracle.com/en/java/javase/21/docs/api/java.base/java/util/Collection.html)) [fetched]

## 6. Summary table

| System | Granularity | Signal shape | Write when read-only | Can the answer go stale? |
|---|---|---|---|---|
| EF Core no-tracking | entity instance | query (`Entry(e).State`) | silent no-op | yes, attach/detach changes it |
| EF Core keyless | entity type | model query (`IsKeyless`) [memory] | throws on attach/update | no (model is fixed after build) |
| Hibernate read-only | entity in session | query (`Session.isReadOnly(e)`) | ignored at flush; delete allowed | per session; settable |
| Hibernate @Immutable | entity type | annotation | ignored, no exception; bulk HQL warns or throws | no |
| ActiveRecord | record | property (`readonly?`) | raises `ReadOnlyRecord` | set at load, not re-derived [memory] |
| Realm frozen | object version | property (`isFrozen()`, `isValid()`) | throws; `findLatest` for a writable copy | `isValid` is live |
| Couchbase Lite | document | type (`Document` vs `MutableDocument`) | n/a, no mutators | no |
| LiteDB, SQLite, Core Data | store or database | option plus store query/property | refused by the store | rarely |
| protobuf unknown fields / Any | field / payload | property list [memory] / `is()` query | preserved / unpack throws | no |
| Avro | field / union branch | exception only | field dropped / error | no |
| Unity missing types | managed reference in a host | query (bool) plus list (key, type name, data) | data preserved; recoverable when type returns | answered on demand |
| POSIX access(2) | path | query | open fails | yes, TOCTOU (documented) |
| S3 Object Lock | object version | metadata via HEAD/GET | versioned delete 403; PUT makes a new version | yes, holds and event holds change |
| Git LFS lockable | file | file-system read-only bit | push refused by server | yes, as of last checkout |
| .NET ICollection | collection | property | `NotSupportedException` | no |
| Java Collection | collection | exception only | `UnsupportedOperationException` | n/a |

Patterns observed, stated as facts across the sources above:

- Every system that offers an advance signal still has the write path decide:
  EF Core, Realm, ActiveRecord, S3, Git LFS and POSIX all refuse or ignore at the
  write regardless of what a check said.
- Where the read-only state can change after it was observed (POSIX permissions,
  S3 holds, Git LFS locks, EF Core attach/detach), the documentation either warns
  of TOCTOU (POSIX, Python, CWE-367) or places the authoritative check at the
  write (S3, Git LFS push).
- Silent-ignore write behaviour (Hibernate entities, EF Core no-tracking) is the
  case where the advance signal is the only signal; Hibernate added an opt-in
  exception mode for bulk updates.
- The closest analog to records under an unregistered schema id (Unity missing
  types) exposes both an on-demand bool query and an on-demand list carrying the
  reference id and the recorded type name, and recovers the data when the type
  returns.
- Store-level read-only (LiteDB, SQLite, Core Data, and CultCache's own
  `CacheBackingStore.IsReadOnly`) is a property or query on the store, not on the
  record.
