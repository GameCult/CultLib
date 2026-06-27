# CultMesh Cross-Runtime Primitives Roadmap

CultMesh should feel like a cozy typed runtime, not a transport kit with good
manners. Application code should read as native state and native operations in
every runtime, while CultMesh chooses the fastest valid path: in-process,
shared memory, IPC, network, or WASM.

In this roadmap, "primitive" does not mean "tiny DTO" or local convenience
wrapper. It means a reusable cross-runtime developer affordance promoted into
the shared library layer: the abstraction, metadata, generated bindings,
runtime routing, diagnostics, and fast path that make an obvious state operation
feel native everywhere. A `Rect` value is a primitive; so is a typed viewport
query surface that can execute against an in-process daemon, a shared CultCache
slab, a remote Verse peer, or a browser/WASM host without changing the caller's
API.

This roadmap is the shared-layer backlog for abstractions that Aetheria,
Brokkr, Odin, Eve, Bifrost, Unity, Electron, Rust, and browser clients should
all be able to use without cloning local facade code.

## Design Rule

If two runtimes need the same semantic shape, hoist it into CultMesh or the
adjacent CultLib foundation layer. Do not leave it as an Aetheria-only wrapper,
an Electron preload convention, a Unity bridge helper, or an MCP-specific
adapter.

CultMesh should be expansive enough to absorb repeated developer-experience
patterns, not just repeated serialization shapes. When Aetheria, Brokkr, Odin,
Bifrost, Eve, Ymir, VoidBot, Unity, TS, Rust, or browser clients all want the
same kind of state pointer, operation, query, watch, authority claim, native
view, or surface binding, that is evidence of a missing shared primitive. The
domain package keeps the nouns and policy; CultMesh owns the managed, cozy
machinery that makes those nouns feel magical and run very fast.

The application layer may name domain operations:

```ts
await verse.aetheria.zone(zoneId).pilot(entityId).move(CultMesh.vec2(1, 0));
```

The shared layer must own the reusable primitives that make that sugar possible:
typed state pointers, operation handles, query surfaces, authority claims,
projection recipes, native slab descriptors, watches, and generated binding
metadata.

The test is whether a downstream runtime can say the domain thing directly. If
the caller has to remember record keys, schema ids, MessagePack slots, endpoint
paths, IPC channel names, local publication files, cache filenames, queue names,
or whether the target is local/remote/WASM, the shared primitive is not cozy
enough yet.

## Managed Magic Contract

Every promoted primitive should carry both a high-level developer promise and a
low-level performance promise:

| Promise | Requirement |
| --- | --- |
| Native-feeling API | Public calls look like typed domain state, operations, queries, or views rather than transport instructions. |
| One semantic surface | C#, TypeScript, Rust, browser/WASM, Unity, Eve/CultUI, and tool runtimes use the same conceptual API even when adapters differ. |
| Fastest valid route | The runtime can choose in-process calls, shared slabs, IPC, RUDP/network, copied snapshots, or WASM views without creating a second application API. |
| Typed safety | State, operation payloads, query parameters, results, authority claims, receipts, and view columns are generated or strongly typed. |
| Reactive by default | Values that can change expose watch/subscription semantics with explicit lifetime and backpressure behavior. |
| Inspectable | Diagnostics can explain the chosen route, authority decision, schema version, cache source, native handle, and copy/fallback cost. |
| Portable fallback | Remote or browser clients keep the same semantic surface when zero-copy locality is unavailable. |

## Primitive Families

| Primitive family | Shared responsibility | First consumers |
| --- | --- | --- |
| Typed state pointer | A stable typed reference to Verse state that can resolve, watch, and be embedded in Eve/CultUI surfaces. | Eve, Bifrost MCP, Unity inspectors, RTS panels |
| State binding descriptor | Surface metadata that binds a UI/tool prop to a typed CultMesh state pointer with source schema and route hints. | Eve/CultUI, Bifrost MCP, Unity inspectors, RTS status panels |
| Typed operation handle | Method-shaped invocation with typed request/response, authority claims, idempotency, route hints, and receipts. | Aetheria pilot/commander verbs, Brokkr editor tools |
| Operation binding descriptor | Surface metadata that binds a UI/tool command to a typed CultMesh operation id with request schema and route hints. | Eve/CultUI, Bifrost MCP, Unity controls, RTS command panels |
| Operation invocation descriptor | Concrete UI/tool invocation metadata that preserves operation id, request schema, route hints, and idempotency across renderer boundaries. | Eve/CultUI lowerers, Unity UI Toolkit, RTS panels, Bifrost tools |
| Operation payload | Shared invocation field bag with typed scalar readers, copy-on-write updates, and legacy string-field ingestion while generated request schemas take over. | Eve/CultUI lowerers, Unity UI Toolkit, RTS panels, Bifrost tools |
| Typed query surface | Derived-state query with typed parameters and result, callable locally or remotely without exposing transport. | Aetheria objects/gravity/selection/inventory viewports |
| Projection recipe | Declarative composition of typed documents into a named local projection, cacheable and watchable by clients. | Unity render shell, Electron RTS map, browser CultMesh-only clients |
| Authority scope | Runtime claim, lease, witness, or delegated simulation ownership expressed as typed policy state. | Trusted co-op, future quorum/witness modes |
| Native slice view | Typed SoA/slab view descriptors that map to Unity `NativeArray<T>`, Rust slices, C# spans, or browser typed arrays. | Aetheria render state, Ymir physics state |
| Schema-generated binding | Cross-runtime generated metadata for document slots, enum ids, schema ids, and ergonomic method surfaces. | C#, TypeScript, Rust, WASM |
| Reactive watch | Subscription primitive over typed documents, pointers, queries, and native views with disposal/backpressure semantics. | CultUI, Unity presentation caches, RTS status panels |
| Managed Verse context | Shared `verse.use(schema)` binding context carrying runtime id, route hints, and authority claims into generated domain operations and queries. | Unity client, RTS client, browser-only CultMesh clients, Rust rebuild |
| Verse-bound surface | Operation, query, and live-feed handles bound once to a Verse context so generated facades call domain methods without repeatedly materializing transport contexts. | Aetheria RTS facade, Unity facade generation, Rust/WASM bindings |
| Schema-stamped cache recovery | Directory-backed CultCache records may recover missing hot-manifest catalog entries from schema-version metadata embedded in the cold payload, then resolve by shared schema name. | Aetheria persisted Verse state, long-lived daemon stores, editor/runtime co-deployment |

## Required Sugar Shape

The shared primitives should let domain packages expose APIs that look like
state, not transport.

TypeScript target:

```ts
const verse = await CultMesh.connectVerse("starbridge");
const aetheria = verse.use(Aetheria);

const viewport = await aetheria.zone(zoneId)
  .objects()
  .visibleTo(controlledUnits)
  .within(CultMesh.rectFromBounds(-400, -250, 400, 250))
  .query();

await aetheria.entity(ravenId)
  .pilot()
  .move(CultMesh.vec2(1, 0), { claim: "pilot-control" });
```

C# target:

```csharp
var verse = await CultMesh.ConnectVerseAsync("starbridge");
var aetheria = verse.Use(AetheriaRuntime.Schema);

var viewport = await aetheria.Zone(zoneId)
    .Objects()
    .VisibleTo(controlledUnits)
    .Within(CultRect.FromBounds(-400, -250, 400, 250))
    .QueryAsync();

await aetheria.Entity(ravenId)
    .Pilot()
    .MoveAsync(CultVec2.Right, claim: "pilot-control");
```

Current seed:

```csharp
var verse = await CultMesh.ConnectVerseAsync(
    "starbridge",
    "unity-raven",
    new CultMeshRouteHint(CultMeshLocalityKind.SharedMemory, "co-located Verse"),
    new[] { new CultMeshAuthorityClaim("pilot-control", shardId: "zone:raven") });

var aetheria = verse.Use(context => new AetheriaGeneratedFacade(context));
await aetheria.Entity(ravenId).Pilot.MoveAsync(CultVec2.Right);
```

```ts
const verse = await CultMesh.connectVerse("starbridge", "browser-starfire", {
  routeHint: CultMesh.routeHint("network", "remote Verse peer"),
  claims: [CultMesh.authorityClaim("commander-control", { shardId: "zone:frontier" })],
});

const aetheria = verse.use((context) => new AetheriaGeneratedFacade(context));
await aetheria.entity(ravenId).pilot.move(CultMesh.vec2(1, 0));
```

Generated facades should split Verse views by locality and bind surfaces once:

```ts
const queryVerse = verse.withRoute("shared-memory", statePath);
const commandVerse = verse
  .withRoute("network", endpoint)
  .withClaim("commander-control", { shardId: "aetheria.local" });

const objectsViewport = CultMesh.bindQuery(queryVerse, queries.objectsViewport);
const setMoveVector = CultMesh.bindOperation(commandVerse, operations.setMoveVector);

await objectsViewport.execute(viewport);
await setMoveVector.invoke({ actorEntityKey, directionX: 1, directionY: 0 });
```

That seed is intentionally context-only. Discovery, authority negotiation,
transport selection, native slabs, and WASM adapters can grow behind the same
Verse handle without forcing Unity, Electron, browser, or Rust clients to learn
a second public API.

Rust/WASM target:

```rust
let verse = CultMesh::connect_verse("starbridge").await?;
let aetheria = verse.use_schema(aetheria::schema());

let viewport = aetheria
    .zone(zone_id)
    .objects()
    .visible_to(&controlled_units)
    .within(CultRect::from_bounds(-400.0, -250.0, 400.0, 250.0))
    .query()
    .await?;
```

These examples are intentionally aspirational. They are the shape the primitive
families must support; Aetheria should not keep bespoke client facades once the
shared layer can express the same semantics.

## Staged Build

1. Stabilize handle descriptors.
   `CultMeshStatePointer<T>`, `CultMeshOperationHandle<TRequest, TResponse>`,
   `CultMeshQuerySurface<TParameters, TResult>`,
   `CultMeshProjectionRecipe<TParameters, TResult>`, `CultMeshRouteHint`, and
   `CultMeshNativeSliceViewDescriptor` are the current seed. Keep them small,
   serializable where useful, and mirrored across C# and TypeScript. Bound
   operation, query, and live-feed wrappers are the first managed layer above
   those descriptors; generated facades should prefer them over ad hoc
   `OperationContext` / `QueryContext` plumbing.

2. Add generated binding descriptors.
   Domain packages should generate schema ids, slots, enum ids, state pointer
   factories, query handles, and operation handles from the same source
   contract. Hand-entered MessagePack slot maps are compatibility debt.

3. Grow projection recipes.
   The seed now names source state, route hints, projection execution, and
   query-surface conversion. Next it should add generated source handles, local
   cache keys, invalidation/watch behavior, and native-view output where
   applicable. This turns Aetheria `ObjectsViewport`, `GravityViewport`,
   selection, inventory, stats, station stock, and authority status into
   portable CultMesh surfaces.

4. Add transparent state refs for UI runtimes.
   Eve/CultUI surfaces may carry a typed `CultMeshStatePointer<T>` instead of a
   JSON blob or string state ref. The UI runtime resolves and watches it using
   the local Verse node, with Bifrost/Odin using the same pointer semantics for
   MCP tools. State pointers now carry route hints and typed source metadata in
   both C# and TypeScript, so a surface catalog can advertise where a pointer
   resolves and which schema-backed document/source it depends on instead of
   leaking daemon-specific resolver conventions. They also bind to a Verse like
   operations, queries, and live feeds: `CultMesh.StatePointer(...)` /
   `CultMesh.statePointer(...)` create pointers, and
   `CultMesh.bindStatePointer(...)` / `verse.BindStatePointer(...)` produce a
   bound pointer that resolves and watches through the Verse query context
   instead of requiring UI or tool hosts to rebuild runtime-id and route
   plumbing. Mutable document-style handles use the same shared shape:
   `CultMeshMutableStatePointer<T>` / `CultMesh.mutableStatePointer(...)`
   add typed replacement to the read/watch state pointer contract so tools,
   editor surfaces, and authorized clients do not grow private
   `Document<T>` wrappers. `CultMeshStateBindingDescriptor` / `CultMesh.stateBinding(...)`
   now hoists the repeatable "component prop -> state pointer" shape into the
   shared layer, including pointer id, source id, schema id, and route hint.
   Eve documents, Unity inspectors, browser panels, and Bifrost tools should
   carry this descriptor instead of inventing local `stateRef` string props.
   `CultMeshOperationBindingDescriptor` / `CultMesh.operationBinding(...)`
   applies the same rule to controls: a UI command advertises a typed operation
   id, optional request schema, label, and route hint instead of making
   `command` and `transport` strings the canonical API.

5. Add native slab adapters.
   `CultMeshNativeSliceViewDescriptor` must map to Unity `NativeArray<T>`, C#
   `ReadOnlySpan<T>`, Rust slices, and TS typed arrays where locality permits.
   The descriptor names the column and value type; runtime adapters own handles,
   fences, and unavoidable copy accounting.

6. Add authority-aware routing.
   Operation/query invocation should accept authority claims and route hints,
   then choose local, shared-memory, IPC, network, or WASM execution without
   exposing a second public API for each route.

7. Add cross-runtime conformance tests.
   The same primitive examples must pass in C# and TypeScript first, then Rust.
   Aetheria client tests should assert against these shared primitives rather
   than against private Unity/Electron bridges.

8. Harden durable cache ergonomics.
   Directory-backed CultCache stores should behave like managed state, not
   fragile manifest bookkeeping. A missing catalog entry in the hot manifest
   must not strand a readable cold record when the payload carries a schema
   version. The C# directory store now infers a minimal catalog entry from a
   schema-stamped payload and resolves by schema name, with regression coverage
   in `GameCult.Caching.Tests`. The TypeScript and Rust single-file MessagePack
   stores now perform the same schema-stamped recovery at the snapshot boundary,
   and both cache runtimes normalize recovered schema-name envelopes back to the
   registered public document type before hydration. Python now mirrors the
   same managed recovery path for stores and raw CultNet replication helpers,
   and the TypeScript inspector reports recovered schema names instead of
   leaking stale manifest ids. Keep this parity intact before relying on
   long-lived cross-runtime Verse stores.

## Aetheria Pressure Tests

Aetheria should exercise the roadmap in this order:

1. Sector map and Starbridge session summary as typed state pointers.
2. Objects/gravity viewport split as typed query surfaces.
3. Selected object, inventory, station stock, and current stats as projection
   recipes.
4. Pilot movement, targeting, equipment activation, docking, repair, cooling,
   salvage, and commander verbs as typed operation handles.
5. Zone render and Ymir physics views as native slice descriptors.
6. Eve/CultUI panels that embed state pointers and operation handles instead of
   local payload maps.
7. Unity and RTS clients consuming the same generated domain sugar.

## Stop Lines

- Do not add new public Aetheria command facades unless they can be expressed as
  typed CultMesh operation handles.
- Do not add new viewport APIs unless they can be expressed as typed CultMesh
  query surfaces or projection recipes.
- Do not add new UI state-ref strings when a typed state pointer would express
  the same thing.
- Do not add native memory sharing as a Unity-only bridge; describe the slab in
  CultMesh and let Unity be one adapter.
- Do not accept runtime parity by convention. C#, TypeScript, and Rust need
  shared generated contracts or explicit conformance tests.
