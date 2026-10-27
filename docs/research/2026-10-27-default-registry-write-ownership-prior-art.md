# Prior art: a process-global default registry deciding write ownership

Serves `variants:question:registryless-write-reads-shared` (campaign `variants`). Eyes pass, session `self-2026-10-27b-eureka`, 2026-10-27. Facts only; items marked "summary" rest on search summaries not opened.

# Prior art: process-global type registries and write/overwrite ownership

Fork: `variants:question:registryless-write-reads-shared` (viewed in full via eureka-state, status InForce, options `shared-decides` / `written-type-only`, recommended `shared-decides` at ~65%).
Facts only. No conclusion. Date 2026-10-27.

Method note: web pages were read through WebFetch (a small model summarises the page) and WebSearch summaries. Quotes below are what those tools returned; where only a summary was available it is marked "summary". Nothing was run.

## A. Local mechanics (CultLib, read-only, `C:\cl-wt\c2a-land\src\GameCult.Caching\CultCache.cs`)

- `CultDocumentRegistry.Shared` is `Lazy<CultDocumentRegistry>` (line 639-645). Its public constructor calls `Refresh()` (647-650), which scans `ReflectionExtensions.GetAttributedDocumentTypes()` (688; defined in `ReflectionExtensions.cs:10`) and rebuilds the indexes under `_registrationGate`. So Shared's initial content is whatever `[CultDocument]` types are in loaded assemblies at first touch of `Shared`.
- `GetRequired(Type)` (697-711) registers an unregistered `[CultDocument]` type on demand. So Shared also grows whenever any caller asks for a type. `Refresh()` is public and rebuilds the index from currently loaded assemblies.
- `ForTypes(IEnumerable<Type>)` (658-669) builds a registry with `discoverLoadedDocuments:false`, containing only the given types (plus those `GetRequired` pulls in).
- `CultCache(registry = null, ...)` sets `_registry = registry ?? CultDocumentRegistry.Shared` (2202-2205).
- `WriteDocument<TDocument>(key, doc, storedAt, registry = null)` (4515-4545): `resolvedRegistry = registry ?? Shared`; `catalogEntry = resolvedRegistry.GetRequired<TDocument>().ToCatalogEntry()`; then `Cacheless(resolvedRegistry, ...)` lends that registry to the store for the call. XML doc (4510) says Shared "can only refuse more".
- `WriteRecord(..., registry = null)` (4491-4497): XML doc (4489) says that with registry omitted "it decides nothing for copied records and the one entry is the only claimant".
- `OwnsOnDisk(upserts)` (4741-4759): when the store is cacheless and upserts are given, ownership of an on-disk record is decided by `CultDocumentRegistry.Owns(id, catalog, declared, new CatalogEntryLookup(writes, AttachedRegistry), ..., claimant => writes.Any(entry => entry.SchemaId == claimant.SchemaId))`. The comment (4731-4735, 4738-4740) says written entries shadow the lent registry's type for their ids, the registry adds other claimants, and the record is owned only when the claimant is a written entry, "a registry can only refuse more". `CatalogEntryLookup` ctor at 604 takes `(entries, CultDocumentRegistry? others)`.
- Net: with `registry == null` the lent registry's claimant set is Shared's current contents, which depend on assemblies loaded and on every prior `GetRequired` call in the process.

## B. Q1. Process-global, lazily filled registries in established libraries

### MongoDB C# driver, `BsonClassMap`
- Global: yes. `BsonClassMap.RegisterClassMap` is process-wide static; unregistered types are auto-mapped on first use. Docs: "You must register a class map _before_ it's needed in your code." and "We recommend registering class maps before initializing a connection with MongoDB." https://www.mongodb.com/docs/drivers/csharp/current/serialization/class-mapping/ (WebFetch; the page itself says nothing on freezing or late-registration errors).
- Behaviour depends on what has been touched: EF Core provider issue EF-88 reports "Unknown discriminator value 'List`1'" when the first operation after startup is a read; reporter links it to a class map that is created only on write. Status Closed/Fixed in 8.0.0; page has no maintainer comment. https://jira.mongodb.org/browse/EF-88 (WebFetch). Reporter says explicit `RegisterClassMap` at startup did not fix it for them.
- A MongoDB community answer (via search summary, not opened): discriminator mappings persist once registered, so a read after an insert works; fix offered is `[BsonKnownTypes]`. https://mongodb.com/community/forums/t/c-driver-why-is-bsonknowntypes-attribute-needed-for-polymorphism/2183 (summary only).
- Register-once contract: CSHARP-4424 states the three `RegisterClassMap` methods are "meant to only be called once for a given type"; requested because several libraries share data models and do not control init order. Closed Done, fix version 2.19.0; the page does not say which API shipped. https://jira.mongodb.org/browse/CSHARP-4424 (WebFetch). Community workaround named in search summary: `BsonClassMap.IsClassMapRegistered`.
- CSHARP-1879 (search summary): `RegisterClassMap` accepted an unfrozen map and failed later on use; fixed 2.4.2. https://jira.mongodb.com/browse/CSHARP-1879 (not opened).
- CSHARP-5597 (search summary): `BsonSerializer.RegisterDiscriminator` has no effect; discriminator dictionary populated at class-map registration. https://jira.mongodb.org/browse/CSHARP-5597 (not opened).
- Polymorphic docs: "You must specify hierarchical discriminators in root-to-leaf order." and the relationship of all classes in a hierarchy must be documented before deserialization. https://www.mongodb.com/docs/drivers/csharp/current/serialization/polymorphic-objects/ (WebFetch).
- Particular docs report an order-dependent GuidSerializer failure ("incorrect order of class mappings"). https://docs.particular.net/persistence/upgrades/mongodb-4to5 (search summary, not opened).
- Did they move away: not found. Class maps remain global static; mitigation is documentation plus the IfNotRegistered API.

### protobuf-net `RuntimeTypeModel.Default`
- Global: yes. README: "The Serializer.* methods are basically just shortcuts to RuntimeTypeModel.Default.*". https://cdn.jsdelivr.net/gh/protobuf-net/protobuf-net@main/README.md (WebFetch).
- Lazy fill, freezing, order-dependent bugs: NOT verified. README is silent; a direct docs fetch (docs.protobuf-net.dev/runtime) was redirected and not followed. No issue found.

### MessagePack-CSharp
- Global mutable default exists: `MessagePackSerializer.DefaultOptions`. README (WebFetch): labelled "Global state; Not recommended when writing library code." Safe if you control all MessagePack code; libraries should pass options explicitly because others "may change this static property." Libraries "must not use StaticCompositeResolver - as it is global state". https://github.com/MessagePack-CSharp/MessagePack-CSharp/blob/master/README.md
- Resolvers are an ordered list (`CompositeResolver`): order matters by construction.
- Whether it moved away from the global: not found.

### System.Text.Json
- `JsonSerializerOptions.Default`: "Gets a read-only, singleton instance"; remarks give a performance reason: shared instance avoids "redundant recomputation of converters". https://learn.microsoft.com/dotnet/api/system.text.json.jsonserializeroptions.default (WebFetch). The page states no rationale about cross-component interference; any such reading is inference. Design-discussion issue in dotnet/runtime: searched, not found. Whether Default was ever mutable: not verified.
- Type-info resolvers: not researched beyond the above.

### Json.NET
- `TypeNameHandling` is a security concern, not an order concern: CA2326 says "An attacker could modify the serialized data to include unexpected types"; mitigation is a custom `ISerializationBinder` restricting types. https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca2326 (WebFetch).
- `JsonConvert.DefaultSettings` global behaviour/guidance: the Newtonsoft page fetched says nothing about global effects. Not verified.

### Orleans
- Serializer discovery is build-time source generation (`[GenerateSerializer]`, `[Id]`), not a runtime scan; the docs' older code-generation section lists scan of assemblies referencing the core library plus `KnownType`/`KnownAssembly` attributes. Aliases: "Type aliases are globally scoped, and you cannot have two aliases with the same value in an application." https://learn.microsoft.com/en-us/dotnet/orleans/host/configuration-guide/serialization (saved locally: `C:\Users\Meta\.claude\projects\F--Projects-CultLib\0840c7c7-0e31-410e-9012-afb79c3c0742\tool-results\toolu_0148m7no1AbrJzWmNLicn6N9.txt`, lines 134, 839-841).
- Type manifest / codec registry runtime behaviour, issues: not researched.

### EF Core
- Model is built per `DbContext` type and cached (IMemoryCache; "the built model has a cache size of 100"); initialised at first operation: "just creating a DbContext instance does not cause the EF model to be initialized." https://learn.microsoft.com/en-us/ef/core/performance/advanced-performance-topics
- Assembly scan: `ApplyConfigurationsFromAssembly` — "The order in which the configurations will be applied is undefined, therefore this method should only be used when the order doesn't matter." https://learn.microsoft.com/en-us/ef/core/modeling/ ; and "`EntityTypeConfigurationAttribute` types will not be automatically discovered in an assembly."
- So EF's registry is per context type, not process-global; assembly scan is opt-in and documented as order-undefined.

### Marten
- Storage is a table and upsert function per document type, alias `mt_doc_<alias>`; alias collisions are resolved with `DocumentAlias`. https://martendb.io/schema/storage (WebFetch). Hierarchies share one table via `AddSubClass<T>()`; subclass docs are "stored as the parent type" in the identity map. https://www.martendb.io/documents/hierarchies (WebFetch). Schema is configured in `StoreOptions` per store, not a process-global.

### Java ServiceLoader
- "Service providers are loaded and instantiated lazily, that is, on demand." Providers on the class path load in the order `getResources` finds config files; "The ordering of modules in a module layer is not defined." "Instances of this class are not safe for use by multiple concurrent threads." Caches cleared by `reload`. https://docs.oracle.com/en/java/javase/21/docs/api/java.base/java/util/ServiceLoader.html (WebFetch). Visibility is class-loader dependent ("must be visible to the class loader").

## C. Q2. Write/overwrite decisions against an existing record of another type

- RavenDB: REST put docs, request body section: updates must "include its collection name in the metadata" and "A document's collection cannot be modified." Server error code for mismatch: the first fetch summary said HTTP 500; not confirmed on a second fetch. https://docs.ravendb.net/7.1/client-api/rest-api/document-commands/put-documents (WebFetch twice). Ayende, RavenDB 4.0 storage: document key namespace is global, same key cannot exist in two collections (summary). https://ayende.com/blog/173954/the-design-of-ravendb-4-0-physically-segregating-collections The check is against the collection named in the write (derived from the writer's type by the client), not a registry; the "client maps entity type to collection" part is inference from the search summary, not a quoted source. `PutDocumentCommand` page: a missing collection means `@empty`; concurrency via `changeVector`, null "to skip the check". https://docs.ravendb.net/client-api/commands/documents/put
- Marten: per-type tables, so the same id under two types are two rows in two tables; no cross-type check. Same id across subclasses of one hierarchy table: not covered by docs fetched; unverified. Marten docs: upsert is used so users "don't have to check whether a document is new or existing" (search summary of https://www.martendb.io/documents/storing, not opened).
- MongoDB: the server has no type; `_id` unique per collection; the C# driver writes a `_t` discriminator from the written object's class map. Whether the driver checks an existing document's `_t` on replace: not researched; nothing found stating it does.
- Couchbase: key unique within a collection; upsert replaces; no server-side type field enforcement; guidance is one type per collection. https://docs.couchbase.com/server/7.0/learn/data/scopes-and-collections.html (search summary, not opened).
- Orleans: grain identity is type plus key ("type/key"), so same key under two grain classes is two identities, not a conflict. https://learn.microsoft.com/en-ca/%20dotnet/orleans/grains/grain-identity (search summary, not opened). How storage providers key state: not found.
- Result of this pass: no store found in which a process-global type registry decides whether a write may replace an existing record of another type. Found: collection/type fixed by metadata of the writer (RavenDB), type as part of the key (Orleans, Marten tables), or no check (Couchbase upsert). This is a negative search result, not proof of absence. Not searched: Cosmos DB, DynamoDB, EF Core TPH update of an existing row with another discriminator, Realm, LiteDB, Akka.Persistence.

## D. Q3. Guidance on ambient/global registries and determinism

- Mark Seemann (ploeh, 2019): after discussion with co-author van Deursen, "Ambient Context really is an anti-pattern." Reasons given: he has never used it and superior alternatives exist; he says the claim is unproven. https://blog.ploeh.dk/2019/01/21/some-thoughts-on-anti-patterns/ (WebFetch). Examples he cites as found in the BCL (search summary, not verified on page): `Thread.CurrentPrincipal`, `CultureInfo.CurrentCulture`, `HttpContext.Current`. DI book 2nd ed. chapter 5 lists it among DI anti-patterns: https://www.oreilly.com/library/view/dependency-injection-principles/9781617294730/OEBPS/Text/c05.xhtml (not opened).
- MessagePack README (above) is itself a library-author statement on global mutable defaults. MongoDB CSHARP-4424 (above) states the shared-library init-order problem. EF Core docs (above) state assembly-scan order is undefined.
- No .NET guidance specifically about AppDomain-wide/assembly-scan registries and write decisions found.

## E. Not verified / gaps
- protobuf-net lazy registration, freezing and bug history.
- Why System.Text.Json made Default immutable; no maintainer statement found.
- Json.NET DefaultSettings guidance.
- RavenDB exact error text and HTTP status; whether the stored document is unchanged after refusal.
- Marten same-id-across-subclasses behaviour.
- MongoDB driver behaviour on replacing a document whose `_t` differs.
- Several items above rest on WebSearch summaries I did not open (marked "summary").
