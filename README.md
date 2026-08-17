# CultLib

CultLib is GameCult's shared state, transport, and runtime substrate. It started
as reusable C# game infrastructure, but the useful thing now is larger: typed
`.cc` persistence, schema-stable wire formats, ergonomic reliable-UDP
networking, distributed database sync, simulation witness consensus, streaming
surfaces, and UI/runtime affordances that can be used from C#, TypeScript, Rust,
Python, and Kotlin without each runtime inventing its own local folklore.

The stack is less a single product than a set of capabilities:

- logging primitives and implementations
- CultCache typed documents, `.cc` persistence, and SoA memory access
- CultNet schema-v0 messages over TCP, WebSocket, LiteNetLib adapters, and
  CultLib's native cross-runtime RUDP pipe
- CultMesh distributed realtime database, Verse discovery, authority leases,
  shard replication, and witness-authoritative simulation consensus
- typed geometry domain/chunk documents for distributed CSG and LOD streaming
- low-latency stream catalogs and frame-handle negotiation for media, tensors,
  and runtime-native buffers
- Eve/CultUI daemon-published surfaces that lower into GUI, TUI, web, native,
  overlay, or agent-facing clients
- declarative Unity UI composition and reflective runtime inspector tooling

The practical pitch: a runtime can persist typed state, exchange it with peers,
discover compatible schemas, join a Verse, publish observations, reconcile
canonical state, and expose an operator surface without translating through a
bespoke JSON bridge at every boundary. A frontend is just another CultMesh
client: the daemon publishes a typed Eve/CultUI surface, and any compatible
runtime can lower that surface where it needs to live.

## Which Package Do I Want?

If the job is shared game state, start here before inventing a fourth drawer
and pretending the label makes it furniture.

| Job | Start With | Owns | Use When | Do Not Use It For |
| --- | --- | --- | --- | --- |
| Local typed state | `GameCult.Caching` / CultCache | Document identity, schema compatibility, record keys, indexes, globals, and local persistence | You need a typed cache, file-compatible save data, local reactive reads, or a stable domain document model | Peer discovery, transport security, shard routing, or mesh consensus |
| Procedural geometry state | `GameCult.Geometry` | CultCache-native domain trees, LOD build requests, selected-cut diagnostics, and chunk artifact payloads | Rust, Unity, or remote workers need to share CSG/LOD geometry and graph metadata as typed state | Transport policy, peer discovery, or gameplay authority |
| Network transport and database plumbing | `GameCult.Networking` / CultNet | Native RUDP sessions, LiteNetLib/TCP/WebSocket adapters, authentication, schema-v0 wire contracts, shard authority, raw document replication, snapshots, and subscriptions | You need a client/server pipe, login/session flow, schema discovery, reliable UDP, or a low-level distributed CultCache lane | Gameplay-facing mesh ergonomics, Verse policy, mod branches, or simulation consensus composition |
| Distributed realtime gameplay state | `GameCult.Mesh` / CultMesh | Public mesh entrypoints, Verse discovery, peer exchange, shard replication defaults, authority leases, client prediction, and witness consensus | You want the game to treat clients and servers as one reactive database for persistent state, input state, and simulation facts | A tiny local-only tool, a bare transport client, or a storage format contract |
| Realtime media/frame streams | `GameCult.Mesh` / CultMesh streaming mode | Stream identity, authority, clock metadata, body transport negotiation, frame cursors, and backpressure state | Audio/video/tensor frames need to move between runtimes through shared memory, GPU handles, platform buffers, or CultCache page refs | Durable document mutation, mesh consensus facts, or pretending inline bytes are zero-copy |
| Daemon-published UI | Eve / CultUI | Typed operator and user-facing surfaces over CultMesh state | A daemon should expose controls, inspection, or workflow UI that can lower into GUI, TUI, web, native, overlay, or agent clients | One-off vendor dashboards, untyped debug panels, or runtime-local UI that cannot travel with the daemon |

Quick rule:

- Choose CultCache when the problem is "how do I model and persist typed state?"
- Choose GameCult.Geometry when the problem is "how do geometry workers share
  domain trees, LOD build requests, and mesh chunks as typed state?"
- Choose CultNet when the problem is "how do peers exchange authenticated,
  schema-aware database messages?"
- Choose CultMesh when the problem is "how does a game join a Verse and share
  realtime state across a mesh?"
- Choose CultMesh streaming mode when the problem is "how do these runtimes move
  audio/video frames while the mesh owns identity, clocks, cursors, and pressure?"
- Choose Eve/CultUI when the problem is "how does this daemon publish its own
  operable surface instead of waiting for a hosted admin console?"

CultMesh sits on CultNet, and CultNet distributes CultCache documents. That is
the stack. If a design needs another peer-to-peer category, first check whether
it is really Verse policy, peer exchange, or shard authority wearing a fake
mustache.

If you want to build an application rather than tour the component drawer,
start with the maintained
[`CultMesh And Eve Getting Started`](src/GameCult.Mesh/docs/getting-started/README.md)
series. Its first executable package-consumer checkpoint is
[`samples/eve-two-runtime`](samples/eve-two-runtime/README.md).

If you want only the smallest durable "open node -> put typed doc -> get typed doc"
path, use
[`src/GameCult.Mesh/docs/durable-node-quickstart.md`](src/GameCult.Mesh/docs/durable-node-quickstart.md).
If you want the lower-level "typed local document -> raw wire document ->
mesh/runtime watch surface" handoff, follow it with
[`src/GameCult.Mesh/docs/typed-document-path.md`](src/GameCult.Mesh/docs/typed-document-path.md).

## Repository Scope

The solution includes:

- `GameCult.Logging`: common logging abstraction plus console and file implementations
- `GameCult.Caching`: `DatabaseEntry`-based cache, indexes, global entries, and backing-store abstractions
- `GameCult.Caching.MessagePack`: MessagePack-backed persistence for the cache
- `GameCult.Caching.NewtonsoftJson`: Newtonsoft.Json-backed persistence for the cache
- `GameCult.Caching.MessagePack.Generator`: source generator for MessagePack formatters for cache models
- `GameCult.Caching.MessagePack.Analyzers`: packaging project that delivers the generator to consuming projects
- `GameCult.Geometry`: CultCache-native geometry domain, selected-cut, and chunk artifact documents for VibeGeometry/Fensalir-style pipelines
- `GameCult.Networking`: encrypted login/register/verify flows, schema-v0 contracts, transport adapters, and native RUDP sessions
- `GameCult.Mesh`: CultMesh package home for distributed realtime database, shard replication, client prediction, Verse discovery, and mesh witness consensus
- `GameCult.Caching.Tests`: NUnit tests for cache and backing-store behavior
- `GameCult.Networking.Tests`: NUnit tests for networking behavior
- `GameCult.Unity`: CultUI, a Unity runtime UI composition framework with reflective inspector generation, prefab-backed field resolvers, reusable controls, and a demo project packaged for UPM-style consumption
- `packages/cultcache-ts`: TypeScript CultCache with MessagePack persistence and inspector tooling
- `packages/cultnet-ts`: TypeScript CultNet schema-v0 contracts, framing, discovery, raw document replication, and interop tests
- `packages/cultmesh-ts`: TypeScript CultMesh local node and mesh catalog surface for local runtimes such as VoidBot
- `packages/cultcache-py`: Python CultCache/CultNet/CultMesh package with CultCache v1 wire parity
- `packages/cultcache-rs`: Rust CultCache and derive macro
- `packages/cultnet-rs`: Rust CultNet contracts, framing, discovery, and interop peer
- `packages/cultmesh-kotlin`: Kotlin/JVM CultMesh and CultNet surface for Android-adjacent runtimes

## Repository Layout

```text
package.json
src/
  GameCult.Logging/
  GameCult.Caching/
  GameCult.Caching.MessagePack/
  GameCult.Caching.NewtonsoftJson/
  GameCult.Caching.MessagePack.Generator/
  GameCult.Caching.MessagePack.Analyzers/
  GameCult.Geometry/
  GameCult.Networking/
  GameCult.Mesh/
  GameCult.Unity/
tests/
  GameCult.Caching.Tests/
  GameCult.Geometry.Tests/
  GameCult.Networking.Tests/
packages/
  cultcache-ts/
  cultnet-ts/
  cultmesh-ts/
  cultmesh-kotlin/
  cultcache-py/
  cultcache-rs/
  cultnet-rs/
```

## Build

```powershell
dotnet build CultLib.sln
```

## Test

```powershell
dotnet test CultLib.sln
```

TypeScript package tests:

```powershell
npm test --workspace packages/cultcache-ts
npm test --workspace packages/cultnet-ts
npm run test:interop --workspace packages/cultnet-ts
npm test --workspace packages/cultmesh-ts
```

Rust package tests:

```powershell
cargo test --manifest-path packages/cultcache-rs/Cargo.toml
cargo test --manifest-path packages/cultnet-rs/Cargo.toml
```

Kotlin package build:

```powershell
.\packages\cultmesh-kotlin\build.ps1
```

## Common Concepts

### `DatabaseEntry`

The cache-centric libraries revolve around `DatabaseEntry`. Every entry has a stable `Guid` identifier and can optionally:

- expose a human-readable name through `INamedEntry`
- participate in generic indexes registered at runtime
- be treated as a global singleton entry through `GlobalSettingsAttribute`

Typical entry shape:

```csharp
using GameCult.Caching;

public class ItemData : DatabaseEntry, INamedEntry
{
    public string Name = string.Empty;
    public int Value;

    public string EntryName
    {
        get => Name;
        set => Name = value;
    }
}
```

### `CultCache` and Backing Stores

`CultCache` is an in-memory index over `DatabaseEntry` objects. It can operate entirely in memory, or it can be attached to one or more backing stores for persistence and synchronization.

- the cache is the query surface
- backing stores are persistence adapters
- indexes and name maps are maintained inside the cache, not inside the store

### Important: Multiple Backing Stores

When multiple backing stores are added, behavior depends on how they are registered.

If a store is added with domain types:

```csharp
cache.AddBackingStore(playerStore, typeof(PlayerData));
cache.AddBackingStore(settingsStore, typeof(AppSettings));
```

then that store becomes the direct persistence target for those types.

If a store is added without domain types:

```csharp
cache.AddBackingStore(primaryStore);
cache.AddBackingStore(mirrorStore);
```

then the first generic store acts as the primary writable store for non-domain-specific entries. Additional generic stores subscribe to the existing stores and mirror their change events.

Implications:

- order matters for generic stores
- `AddAsync` writes to the type-specific store when one exists
- otherwise `AddAsync` writes to the first generic store
- later generic stores do not become co-primaries; they mirror earlier stores
- `PullAllBackingStoresAsync` pulls from every registered store

Recommended patterns:

- use one generic primary store if you want simple persistence
- use domain-specific stores when different entry types belong in different persistence layers
- treat additional generic stores as mirrors or downstream replicas, not independent write targets

## Example: Cache + Networking

```csharp
using GameCult.Caching;
using GameCult.Logging;
using GameCult.Networking;

var cache = new CultCache
{
    Logger = new ConsoleLogger()
};

var server = new Server(cache)
{
    Logger = new ConsoleLogger()
};

server.Start();
```

## Secrets and Runtime Configuration

`GameCult.Networking` uses two pieces of deployment configuration:

- `GAMECULT_CONNECTION_KEY`
- `GAMECULT_SESSION_SIGNING_SECRET`

Production guidance:

- set both values before starting the server
- treat `GAMECULT_CONNECTION_KEY` as shared client/server protocol configuration
- treat `GAMECULT_SESSION_SIGNING_SECRET` as a server-only secret
- generate high-entropy random values and store the server-side value in your platform's secret store
- do not rely on local-development defaults in production
- partial configuration is rejected at startup

Recommended value format:

- at least 32 random bytes per value
- encode as Base64 or Base64Url if you want a portable text representation

Example secret generation in PowerShell:

```powershell
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }))
```

Typical production setup patterns:

- inject `GAMECULT_SESSION_SIGNING_SECRET` and the server copy of `GAMECULT_CONNECTION_KEY` as environment variables from Docker secrets, Kubernetes secrets, systemd environment files, or your cloud secret manager
- ship the matching `GAMECULT_CONNECTION_KEY` to the client through a build-generated config asset, code-generated constants, or another explicit client configuration mechanism
- scope them per environment so development, staging, and production do not share values
- rotate `GAMECULT_SESSION_SIGNING_SECRET` operationally
- treat `GAMECULT_CONNECTION_KEY` as versioned protocol config; changing it requires a coordinated client/server rollout

Example server startup with strict environment validation:

```csharp
using GameCult.Caching;
using GameCult.Networking;

var cache = new CultCache();
var security = ServerSecurityOptions.FromEnvironment();
var server = new Server(cache, security);
```

Example local-development server startup:

```csharp
using GameCult.Caching;
using GameCult.Networking;

var cache = new CultCache();
var security = ServerSecurityOptions.Development();
var server = new Server(cache, security);
```

Example shipped-client startup with explicit client configuration:

```csharp
using GameCult.Networking;

var security = new ClientSecurityOptions("<matching-connection-key>");
var client = new Client(security);
```

## Project Docs

Each subproject has a local README with package-specific detail:

- [GameCult.Logging](src/GameCult.Logging/README.md)
- [GameCult.Caching](src/GameCult.Caching/README.md)
- [GameCult.Caching.MessagePack](src/GameCult.Caching.MessagePack/README.md)
- [GameCult.Caching.NewtonsoftJson](src/GameCult.Caching.NewtonsoftJson/README.md)
- [GameCult.Caching.MessagePack.Generator](src/GameCult.Caching.MessagePack.Generator/README.md)
- [GameCult.Caching.MessagePack.Analyzers](src/GameCult.Caching.MessagePack.Analyzers/README.md)
- [GameCult.Geometry](src/GameCult.Geometry/README.md)
- [GameCult.Networking](src/GameCult.Networking/README.md)
- [GameCult.Mesh](src/GameCult.Mesh/README.md)
- [Durable Node Quickstart](src/GameCult.Mesh/docs/durable-node-quickstart.md)
- [Typed Document Path](src/GameCult.Mesh/docs/typed-document-path.md)
- [GameCult.Unity](src/GameCult.Unity/README.md)
- [GameCult.Caching.Tests](tests/GameCult.Caching.Tests/README.md)
- [GameCult.Networking.Tests](tests/GameCult.Networking.Tests/README.md)
