# CultMesh Public API

Package: `GameCult.Mesh`

Brand: CultMesh

Namespace: `GameCult.Mesh`

## Entry Points

- `CultMesh.CreateNodeAsync(...)`
- `CultMesh.StartNodeAsync(...)`
- `CultMesh.CreateVerseCatalog()`
- `CultMesh.CreatePeerCatalog()`
- `CultMesh.CreateVerseDiscoveryClient(...)`
- `CultMesh.CreatePeerExchangeClient(...)`
- `CultMesh.CreateAuthorityLeaseCatalog()`
- `CultMesh.ParseRudpEndpoint(...)`
- `CultMesh.CreateRudpServer(...)`
- `CultMesh.CreateRudpClient(...)`
- `CultMesh.CreateRudpClientForPeer(...)`
- `CultMesh.CreateRudpClientForAuthorizedPeer(...)`
- `CultMesh.ConnectRudpClient(...)`
- `CultMesh.ConnectRudpClientForPeer(...)`
- `CultMesh.ConnectRudpClientForAuthorizedPeer(...)`
- `CultMesh.CreateSimulationFactCommitter(...)`
- `CultMesh.CreateGameSession(...)`
- `CultMesh.CreateClient(...)`
- `CultMesh.ConnectClient(...)`

`CultMeshNode` wraps the current local runtime pieces:

- `Cache`: the underlying `CultCache`
- `Server`: the underlying CultNet server
- `Database`: the distributed realtime database facade
- `DatabaseServer`: the schema-v0 database bridge

`CultMeshNodeOptions.EnableDurableShardLogs` attaches a file-backed
authoritative shard-log store when `DatabaseOptions.MutationLogStore` is not
already supplied. `ShardLogPath` can override the location; otherwise CultMesh
uses a `.cultmesh/shard-logs` directory beside the cache file.

The package home and primary entrypoint are `GameCult.Mesh` / `CultMesh`.
Some lower-level database and wire-contract types still retain `CultNet` names
because they are shared with the transport package. Treat those as plumbing,
not the brand surface.

## Core Behaviors

### Verses

`CultMeshVerseDescriptor` describes a rule-bearing consensus graph. It includes
transport compatibility, rules hash, authority model, discovery endpoints,
known authority runtimes, optional parent Verse id, and plugin requirements.

`CultMeshVerseCatalog` is the local reactive catalog for discovered Verses.
It can publish discovery updates and find compatible transfer targets.

`CultMesh.ServeVerseCatalog(node, catalog)` attaches schema-v0 Verse discovery
responses to a node. The wire contracts are
`cultmesh.verse_catalog_request.v0` and `cultmesh.verse_catalog_response.v0`.
Consumers can apply a response directly with
`CultMeshVerseCatalog.Upsert(CultMeshVerseCatalogResponseMessage)`.

### Peer Exchange

`CultMeshPeerCatalog` is the local reactive catalog for peer cards.
`CultMeshPeerCard` describes a candidate peer endpoint for one Verse with
roles, shard hints, optional region, optional authority lease id, expiry, and
signature.

`CultMesh.ServePeerExchange(node, catalog)` attaches schema-v0 peer exchange
responses to a node. The wire contracts are
`cultmesh.peer_exchange_request.v0` and `cultmesh.peer_exchange_response.v0`.
`CultMeshPeerExchangeClient` can fetch peer cards from known endpoints and
upsert them into a local catalog.
Peer cards are discovery hints; authority still requires a valid lease or
signature for the target Verse. `CultMeshPeerCatalog.FindAuthorized(...)` and
`FirstAuthorized(...)` compose peer lookup with
`CultMeshAuthorityLeaseCatalog.IsAuthorized(...)`.

### Authority Leases

`CultMeshAuthorityLease` grants one peer specific roles for a Verse, optionally
scoped to shard ids and bounded by time. `CultMeshAuthorityLeaseCatalog` checks
whether a peer card is authorized for a requested role and shard. This keeps
peer exchange separate from authority instead of letting contact gossip mutate
the world.

### Cross-Runtime Primitives

These are the first shared primitives for the cozy typed Verse surface that
projects like Aetheria should build on instead of inventing daemon-specific
adapters:

In CultMesh, a primitive can be more than a data shape. Useful state-access
patterns should graduate into shared managed affordances: typed handles,
generated binding metadata, route selection, authority context, watches,
diagnostics, and native-view descriptors that let each runtime use the same
semantic API. The domain package names the thing; CultMesh supplies the fast,
portable machinery that makes it feel native.

- `CultMeshStatePointer<T>` is a typed pointer to Verse state that UI surfaces,
  tools, and clients can resolve or watch without string state-ref plumbing.
  `CultMesh.StatePointer(...)` creates simple or context-aware pointers.
  Pointers carry source descriptors and route hints, and
  `CultMesh.DescribeStatePointer(...)` exposes that metadata for catalogs and
  inspectors. Context-aware pointers can bind with `Bind(...)`,
  `CultMesh.BindStatePointer(...)`, or `CultMeshVerse.BindStatePointer(...)`,
  producing a Verse-bound handle that resolves and watches through the shared
  Verse query context instead of forcing UI/tool code to rebuild route plumbing.
- `CultMeshMutableStatePointer<T>` extends the same state pointer shape with a
  typed `ReplaceAsync(...)` operation for authoritative document handles,
  editor tools, and UI surfaces that intentionally write state. It keeps the
  read/watch/replace trio in CultMesh instead of letting each domain package
  invent private document wrappers. `CultMesh.MutableStatePointer(...)`,
  `CultMesh.BindMutableStatePointer(...)`, and
  `CultMeshVerse.BindMutableStatePointer(...)` preserve the same Verse context,
  route hints, source diagnostics, and state-binding metadata as read-only
  pointers.
- `CultMeshStateRefResolver` is the shared bridge for UI/tool surfaces that
  still receive state references from authored Eve/CultUI documents. Resolvers
  are named, composable, carry source descriptors and route hints, and expose
  diagnostics through `CultMesh.DescribeStateRefResolver(...)`. Legacy
  `Func<string, string>` lowerers can use `AsFunc()` while generated surfaces
  migrate to typed state pointers and binding descriptors.
- `CultMeshStateBindingDescriptor` and `CultMeshStateBindingRecord` are the
  UI/tool state edge. Live surfaces bind a component prop to a typed state
  pointer; persistence and transport boundaries use
  `CultMesh.StateBindingRecord(...)` instead of local pointer-field copies or
  route parsers.
- `CultMeshOperationHandle<TRequest, TResponse>` is a method-shaped operation
  handle. `CultMeshOperationContext`, `CultMeshAuthorityClaim`, and
  `CultMeshOperationReceipt` carry runtime identity, authority metadata,
  route hints, idempotency, acceptance, and diagnostics.
  `CultMesh.DescribeOperationHandle(...)` exposes the operation id for surface
  catalogs and generated tooling. `Bind(...)`, `CultMesh.BindOperation(...)`,
  and `CultMeshVerse.BindOperation(...)` produce Verse-bound handles so
  generated domain facades can invoke without re-threading context plumbing at
  every call site.
- `CultMeshOperationBindingDescriptor`, `CultMeshOperationBindingRecord`,
  `CultMeshOperationInvocationDescriptor`, `CultMeshOperationInvocationRecord`,
  and `CultMeshOperationPayload` are the UI/tool operation edge. Live surfaces
  carry typed operation identity and scalar payload reads; persistence and
  transport boundaries use `CultMesh.OperationBindingRecord(...)`,
  `CultMesh.OperationInvocationRecord(...)`, and
  `CultMeshOperationPayload.ToDictionary()` instead of local route parsers or
  ad-hoc payload maps.
- `CultMeshQuerySurface<TParameters, TResult>` is a typed derived-state query
  surface. Query execution can be local, remote, shared-memory, IPC, or
  WASM-routed while preserving the same semantic API. Query surfaces can also
  carry source descriptors, route hints, and reactive watches so tools and UI
  runtimes can inspect what state a generated query depends on, which route it
  prefers, and subscribe without inventing polling glue. Query surfaces can be
  bound with `Bind(...)`, `CultMesh.BindQuery(...)`, or
  `CultMeshVerse.BindQuery(...)`.
- `CultMeshLiveFeed<TParameters, TResult>` is a typed live view surface for
  composed client snapshots such as an RTS viewport plus selection, health, and
  authority panels. It gives clients one `snapshot(...)` / `watch(...)` handle
  while preserving source descriptors and route defaults. Live feeds can be
  bound with `Bind(...)`, `CultMesh.BindLiveFeed(...)`, or
  `CultMeshVerse.BindLiveFeed(...)`.
- `CultMeshDocumentHandle<TDocument>` is the typed reactive document edge for
  CultCache/CultNet state. It exposes document metadata, one coherent
  `LatestAsync()` read, a `Watch()` stream, and `ReplaceAsync(...)` when the
  handle is backed by mutable state. Distributed `CultNetDatabase` handles also
  expose `SubmitPredictionAsync(...)`, which applies a local prediction through
  the database's configured client-authority scope and later reconciles through
  the normal shard log. Callers can bind a projected live feed, a local
  `CultCache` record, a distributed `CultNetDatabase` record, or a
  `CultMeshNode` record through the same surface instead of walking transport
  and projection layers themselves. Same-schema CLR aliases can be requested
  directly from node/database/snapshot surfaces; CultMesh resolves the canonical
  registered document by shared `[CultDocument(schemaName, schemaVersion)]`
  identity and uses the shared CultCache MessagePack codec for
  read/watch/replace/prediction conversion. `AsSchemaAlias<TAlias>()` remains
  available when a caller already has a handle and wants to project it.
- `CultMeshDocumentCatalog` is the schema-aware lookup edge for a set of
  document handles. It indexes handles by CLR document type, schema name, and
  schema version, and resolves same-schema CLR aliases by delegating to
  `CultMeshDocumentHandle<TDocument>.AsSchemaAlias<TAlias>()`. Domain facades
  can expose `Document<T>()`, `DocumentBySchema(...)`, `LatestAsync<T>()`, and
  `Watch<T>()` without owning schema dictionaries or alias serializers. Mutable
  facades can also call `ReplaceAsync<T>(...)` or
  `SubmitPredictionAsync<T>(...)` directly on the catalog when they only need
  the schema-aliased operation and not the intermediate handle.
- `CultMeshCollectionHandle<TDocument>` is the typed reactive edge for
  multi-record state. It exposes one `LatestAsync()` collection snapshot and a
  `WatchChanges()` stream while hiding whether the backing source is an
  in-process `CultCache`, a distributed `CultNetDatabase`, or a `CultMeshNode`.
  Collection handles can represent all records of a document type, a named
  document view, or an indexed value view, and they support the same
  same-schema CLR alias conversion as document handles.
- `CultMeshCollectionCatalog` is the collection sibling of
  `CultMeshDocumentCatalog`. It indexes typed collection handles by CLR
  document type, schema id, schema name, and schema version, then exposes
  `Collection<T>()`, `LatestAsync<T>()`, and `WatchChanges<T>()` so domain
  facades can publish typed multi-record state without maintaining parallel
  schema lookup tables.
- `CultMeshProjectionRecipe<TParameters, TResult>` names a reusable projection
  from typed source state into derived state. It records source handles,
  route hints, and projection execution, and can be exposed as a typed query
  surface. Reactive projection recipes preserve their watcher when exposed as
  query surfaces. When a caller supplies an automatic query context, the recipe
  route becomes the default; explicit caller routes still win.
- `CultMeshNativeSliceViewDescriptor` and `CultMeshNativeSliceColumn` describe
  typed native views for co-located runtimes such as Unity Burst jobs, Rust
  slices, or browser typed arrays. `CultMesh.DescribeNativeSliceView(...)`
  exposes the full view id, schema id, row count, columns, route, native handle,
  and dense stride for tools and adapters.
- `CultMeshFrameBodyPublisher` and `CultMeshMappedFrameBodyCursor` carry
  fixed-layout hot bodies through a triple-buffered memory mapping. Producers
  reserve a writable slot and commit in place; consumers retain one bootstrap
  capability and acquire only newer read leases. Windows uses a named memory
  map; Unix uses a private file-backed map because .NET does not support named
  maps there. The capability and cursor API is identical, and publisher
  disposal revokes new opens and removes the Unix backing file. `Stats()` reports committed
  frames, blocked writes, and unavoidable copies. Direct slot writes report
  zero copies; the convenience `TryPublish(ReadOnlySpan<byte>)` path reports
  one explicit fallback copy instead of laundering it as zero-copy transport.
- `CultMeshRouteHint`, `CultMeshRouteRecord`, and `CultMeshLocalityKind` name
  locality choices without forcing application code to choose transport-specific
  APIs. `CultMesh.RouteRecord(...)` is the shared way to flatten and rehydrate
  route kind/description fields at persisted document, UI, tool, and transport
  boundaries.

These types are intentionally small handles and descriptors. Generated bindings
and runtime adapters should compose them into domain sugar such as
`verse.aetheria().entity(id).pilot().move(...)`,
`zone.objects().visibleTo(units).within(rect)`, and
`renderView.AsNativeArrays()`.

The C# helpers already support the first layer of that shape:

```csharp
var context = CultMeshOperationContext
    .ForRuntime("unity-raven")
    .WithClaim(new CultMeshAuthorityClaim("pilot-control", shardId: "zone:local-rts"))
    .WithRoute(new CultMeshRouteHint(CultMeshLocalityKind.InProcess));

await moveOperation.InvokeAsync(moveRequest, context);
```

Generated facades should bind surfaces once to the correct Verse view:

```csharp
var queryVerse = verse.WithRoute(new CultMeshRouteHint(CultMeshLocalityKind.SharedMemory));
var commandVerse = verse.WithRoute(new CultMeshRouteHint(CultMeshLocalityKind.Network));

var visibleObjects = queryVerse.BindQuery(objectsViewport);
var movePilot = commandVerse.BindOperation(setMoveVector);

var objects = await visibleObjects.ExecuteAsync(viewportRequest);
await movePilot.InvokeAsync(moveRequest, idempotencyKey);
```

A single typed document can be surfaced the same way. The caller asks for the
typed view it wants; if that type is a same-schema alias of the registered
canonical document, CultMesh still opens the correct backing record:

```csharp
var stockForUi = CultMesh.Document<StationStockUiDocument>(
    node,
    new CultRecordKey("station:starbridge:stock"),
    verse);

var stock = await stockForUi.LatestAsync();
using var watch = stockForUi.Watch(RenderStock);
await stockForUi.SetAsync(updatedStockFromUi);
```

Projected or derived documents can use the same handle shape with explicit
snapshot/watch delegates:

```csharp
var cockpit = CultMesh.Document(
    "aetheria.ship.cockpit.current",
    verse,
    context => cockpitStore.LatestAsync(context),
    context => cockpitStore.Watch(context),
    sources: new[]
    {
        CultMesh.ProjectionSource(
            "pilot:cockpit",
            schemaId: "gamecult.aetheria.cockpit_state.v1")
    });

var current = await cockpit.LatestAsync();
using var subscription = cockpit.Watch(next => RenderCockpit(next));

var daemonAlias = cockpit.AsSchemaAlias<DaemonCockpitState>();
```

Remote CultNet snapshots use the same document handle surface. The caller names
the endpoint, record key, and typed document; CultMesh owns the snapshot request
id, response filtering, raw MessagePack payload decode, route metadata, and
polling watch fallback:

```csharp
var remoteHealth = CultMesh.DocumentFromPeerSnapshot<DaemonHealthUiDocument>(
    "cultnet://daemon.local:3075",
    "daemon:aetheria.health.v1",
    verse);

var uiHealth = await remoteHealth.LatestAsync();
```

Snapshot requests are record-key-first so same logical documents can still be
read when another runtime generated a different schema id for a compatible
schema alias. The decode path prefers exact schema id matches when present and
falls back to the requested record key. Ask for the runtime-facing alias type
directly; `AsSchemaAlias<TAlias>()` is for adapting handles that were already
constructed elsewhere.

A domain facade can still collect existing handles into one schema-aware
catalog when it is composing pre-built surfaces:

```csharp
var documents = CultMesh.Documents(
    currentDocking,
    stationRefit,
    zoneContacts);

var refit = await documents.LatestAsync<StationRefitDocument>();
var uiDocking = documents.Document<CurrentDockingUiDocument>();
var bySchema = documents.DocumentBySchema("gamecult.aetheria.station_refit.v1");
```

Typed collections use the same one-call style:

```csharp
var allies = CultMesh.CollectionByIndex<PlayerShipDocument>(
    node,
    "Faction",
    "au");

var currentAllies = await allies.LatestAsync();
using var changes = allies.WatchChanges(change => UpdateRoster(change));

var uiAllies = allies.AsSchemaAlias<PlayerShipUiDocument>();

var collections = CultMesh.Collections(allies);
var uiRoster = await collections.LatestAsync<PlayerShipUiDocument>();
using var uiRosterChanges = collections.WatchChanges<PlayerShipUiDocument>(RenderRoster);
```

```ts
const queryVerse = verse.withRoute("shared-memory", statePath);
const commandVerse = verse
  .withRoute("network", endpoint)
  .withClaim("commander-control", { shardId: "aetheria.local" });

const visibleObjects = CultMesh.bindQuery(queryVerse, queries.objectsViewport);
const movePilot = CultMesh.bindOperation(commandVerse, operations.setMoveVector);

const objects = await visibleObjects.execute(viewportRequest);
await movePilot.invoke({ actorEntityKey, directionX: 1, directionY: 0 });
```

TypeScript runtimes expose the same document and collection handle shape:

```ts
const station = await CultMesh.startNode(statePath, {
  documents: [stationStockDocument, currentDockingDocument],
});

const stock = station.document(stationStockUiDocument, "station:starbridge:stock");
const docking = station.document(currentDockingDocument, "player:raven:docking");
const catalog = CultMesh.documents(stock, docking);

const currentStock = await stock.latest();
const unsubscribe = stock.watch(next => renderStock(next));
await stock.set(updatedStock);
unsubscribe();

const pilots = station.collection(playerShipUiDocument);
const currentPilots = await pilots.latest();
const stopRoster = pilots.watchChanges(change => refreshRoster(change));

const remoteHealth = CultMesh.documentFromPeerSnapshot(
  () => daemonPeer,
  daemonHealthUiDocument,
  "daemon:aetheria.health.v1",
);
const health = await remoteHealth.latest();
```

Projected, remote, shared-memory, IPC, WASM, and local-cache documents all use
the same C# and TS handle contract. The caller names the typed document or
collection it wants; the handle owns route defaults, snapshot reads, reactive
watches, schema alias validation, prediction submission, and replacement when
the backing source supports those mutations. Generated Aetheria and Ymir
facades should bind those handles to a Verse once and keep transport, quorum,
and cache mechanics behind CultMesh.

Native view descriptors can name unmanaged columns without hand-entered byte
sizes:

```csharp
var view = new CultMeshNativeSliceViewDescriptor(
    "aetheria.zone.render",
    "gamecult.aetheria.render_body.v1",
    rowCount,
    new[]
    {
        CultMeshNativeSliceColumn.For<CultVec2>("position"),
        CultMeshNativeSliceColumn.For<CultVec2>("velocity")
    },
    new CultMeshRouteHint(CultMeshLocalityKind.SharedMemory));
```

Projection recipes give local projection code a shared shape before it becomes
runtime-specific glue:

```csharp
var objectsViewport = CultMesh.ProjectionRecipe<ViewportRequest, ObjectsViewport>(
    "aetheria.zone.objects.visible",
    new[]
    {
        CultMesh.ProjectionSource("daemon:aetheria.frame.latest.v1", "gamecult.aetheria.daemon_frame.v1"),
        CultMesh.ProjectionSource("daemon:aetheria.authority.policy.v1")
    },
    (request, context) => ProjectObjectsAsync(request, context),
    new CultMeshRouteHint(CultMeshLocalityKind.SharedMemory));

var query = objectsViewport.AsQuerySurface();
```

Calling `query.ExecuteAsync(request, "unity-raven")` inherits the recipe's
shared-memory route. Calling it with a `CultMeshQueryContext` that already names
`Network`, `Ipc`, `Wasm`, or another route uses the caller's route instead.
`query.Sources` and `query.RouteHint` preserve the recipe metadata for
diagnostics, tooling, and UI/MCP surfaces. If the projection supports reactive
execution, `query.Watch(...)` / `query.watch(...)` keeps the same projection
route resolution, so a browser map tab, Unity renderer, Eve surface, or MCP
tool can subscribe through the same typed query affordance it uses for one-shot
reads.

`CultMesh.DescribeQuerySurface(...)` and
`CultMesh.DescribeProjectionRecipe(...)` expose the same metadata in a stable
diagnostic shape for generated clients, Eve/CultUI inspectors, MCP tools, and
surface catalogs. Tooling should ask CultMesh for these descriptors instead of
copying source lists and route hints by convention.
`CultMesh.DescribeSurface(...)` and `CultMesh.DescribeSurfaceCatalog(...)`
collect query surfaces, projection recipes, live feeds, operations, document
handles, collection handles, state pointers, and native views into one
inspectable catalog for a runtime or generated binding package. Documents are
advertised by document id, collections by collection id, state pointers by
pointer id, and native slice views by view id and route while the full column
layout stays in `CultMeshNativeSliceViewDescriptor`.
Catalog diagnostics support exact id lookup and kind filtering so tools can
discover, for example, every operation or native view without local scan
conventions. `CultMesh.DescribeSurfaceCatalogIndex(...)` / TS
`CultMesh.surfaceCatalogIndex(...)` promotes that grouping into a shared
diagnostic shape with `queries`, `projectionRecipes`, `liveFeeds`,
`operations`, `documents`, `collections`, `statePointers`, and
`nativeSliceViews`, so generated bindings and tools can ask for the semantic
bucket they need without copying catalog scan logic.

Live feeds compose several query surfaces into one client-facing snapshot
without making the UI own refresh loops:

```csharp
var viewportFeed = CultMesh.LiveFeed<ViewportRequest, RtsViewportSnapshot>(
    "aetheria.rts.viewport.feed",
    (request, context) => SnapshotViewportAsync(request, context),
    (request, context) => WatchViewport(request, context),
    new[]
    {
        CultMesh.ProjectionSource("daemon:aetheria.frame.latest.v1"),
        CultMesh.ProjectionSource("daemon:aetheria.health.latest.v1")
    },
    new CultMeshRouteHint(CultMeshLocalityKind.SharedMemory));

var feedDiagnostics = CultMesh.DescribeLiveFeed(viewportFeed);
```

When a runtime has a snapshot path before it has native reactive transport,
`CultMesh.PollingQueryWatcher(...)` provides a disposable watch adapter:

```csharp
var feed = CultMesh.LiveFeed<ViewportRequest, RtsViewportSnapshot>(
    "aetheria.rts.viewport.feed",
    SnapshotViewportAsync,
    CultMesh.PollingQueryWatcher<ViewportRequest, RtsViewportSnapshot>(
        SnapshotViewportAsync,
        new CultMeshPollingWatchOptions<RtsViewportSnapshot>(
            TimeSpan.FromMilliseconds(50))));
```

The polling adapter is intentionally a bridge primitive: application UI code
still receives a typed `Watch(...)` handle, and the sampling loop can later be
replaced by shared-memory, IPC, network, or WASM-native reactivity without
changing renderer call sites.

The staged roadmap for growing these handles into the cross-runtime developer
experience lives in `docs/cross-runtime-primitives-roadmap.md`. When a project
discovers a reusable state-ref, operation, query, projection, authority, watch,
surface-binding, schema-generation, routing, diagnostic, or native-slab
abstraction, it should graduate into that roadmap and then into the shared
CultMesh/CultLib layer instead of remaining a private bridge.

### RUDP Helpers

`CultMesh.ParseRudpEndpoint(...)`, `CreateRudpServer(...)`,
`CreateRudpClient(...)`, `CreateRudpClientForPeer(...)`, and
`CreateRudpClientForAuthorizedPeer(...)` provide the branded CultMesh path to
the native CultNet RUDP socket transport. `ConnectRudpClient(...)`,
`ConnectRudpClientForPeer(...)`, and `ConnectRudpClientForAuthorizedPeer(...)`
also perform the client handshake before returning the same transport. These
helpers parse endpoint contact hints and choose authorized peers;
`GameCult.Networking` still owns the RUDP packet codec, session state, resend,
fragmentation, and channel semantics.

TypeScript runtimes can also wrap a CultNet peer snapshot as a normal document
handle with `CultMesh.documentFromPeerSnapshot(...)`. The caller supplies the
peer or peer factory, document definition or schema id, and record key; CultMesh
sends the snapshot request, waits for the matching response, decodes the raw
MessagePack document, and returns the same `latest()`/`watch()` document surface
used by local cache and single-file documents. Exact schema ids are preferred,
but record-key publications remain readable when two runtimes alias the same
logical document through different schema names.

### Shard Authority

Each shard has one primary writer for now. Non-primary writes are rejected or
explicitly forwarded. Stale epochs fail loudly.

### Reactive Documents

Consumers subscribe to typed document changes through `CultNetDatabase` watch
methods. Subscriptions receive domain changes rather than storage envelopes.
Exact remote values use `SubscribeLiveValueAsync<T>(...)`; they remain ephemeral
and own their unsubscribe lifetime. Hot body state uses
`CultMesh.SubscribeLiveBodyAsync(...)`: the reactive value is only the current
body descriptor, while `OpenCurrentReadOnly()` negotiates shared memory,
shared-file mapping, or network from observed locality and available adapters.
Same-machine bodies are opened from the mapped producer slab. Remote live bodies
are capability-bound and read through one reusable `cultmesh.bodies.v1` session;
they are not packed into CultCache or CDN records. `CultMeshClient.BodyProvider(...)`
creates that verified network adapter without exposing session machinery to the
consumer. Body bytes never become subscription snapshot payload. Either subscription may
legitimately start with `HasValue == false`; it remains active until the provider
publishes the exact record, avoiding a startup retry loop or broad snapshot.
`CultNetDatabaseSubscriptionServer.DemandChanged` exposes the exact requested
record and schema set as well as optional body negotiation. A provider can
therefore materialize a computed record only while it has consumers. The watch
is installed before demand is announced, so the first publication travels as a
normal live change and does not require a bootstrap snapshot.

### Client Prediction

`CultNetClientAuthorityScope` declares input documents a runtime may predict.
CultMesh document handles opened over a `CultNetDatabase` expose
`SubmitPredictionAsync(...)`, which writes local state and emits `Predicted`
through the configured database authority policy. When the authoritative log
arrives, the database emits `Reconciled`.

### Replica Catch-Up

Shard logs can be requested over schema-v0 and applied by replicas. Replica
cursors can be stored with `ICultNetShardReplicaCursorStore`. Authoritative
shard logs can be persisted with `ICultNetShardMutationLogStore`; the file
implementation stores one MessagePack log per shard so catch-up survives a
primary restart. Compacted history returns an explicit resync requirement so a
replica can fall back to a shard-bounded snapshot before applying newer log
entries. Applying that snapshot replaces the local shard view and advances the
replica cursor to the snapshot's represented log sequence.

### Simulation Witness Consensus

`CultNetSimulationObservation` records what a node saw for a simulation fact.
`CultNetSimulationObservationHub` collects those observations and emits
`CultNetSimulationConsensusCandidate` updates.

Candidates are opinions. `CultMeshSimulationFactCommitter` commits quorum
candidates as `CultMeshSimulationFact` documents through `CultNetDatabase`, so
final world state still belongs in the authoritative shard log.

`CultMeshGameSession` is the gameplay-facing composition layer. It owns the
common loop for prediction, observations, candidate watches, simulation fact
watches, and committing new quorum facts without forcing game code to manually
wire every bridge.
