# GameCult.Networking

`GameCult.Networking` provides client/server messaging over LiteNetLib with encrypted credential exchange and signed session tokens.

It is intended for game or game-service scenarios where you want a compact transport layer with a small set of built-in authentication flows rather than a full HTTP stack.

The networking/auth layer is the durable organ. Some payloads in this repo are
just sample application messages riding that organ.

## Scope

The library currently includes:

- a shared message contract model
- a client wrapper around LiteNetLib
- a server wrapper around LiteNetLib
- encrypted login, register, and verify flows
- signed session-token generation and validation
- `PlayerData` integration with `CultCache`
- explicit schema discovery for shared contracts
- a raw MessagePack document/snapshot lane for bit-compatible neighbors
- sample application payloads under `Samples/`

The library is focused on the built-in authentication and session flows in this repository.

Current readiness target:

- trusted local mesh / self-hosted swarm use: yes
- hostile public internet edge: not yet

Keep the distinction clean:

- auth/session semantics belong to the core library
- application payload contracts should be explicit, versioned, and kept in sync
  across runtimes
- runtimes should be able to ask each other which schemas they speak before
  pretending a shared pipe implies shared understanding
- if multiple apps share a message contract, they should be able to talk
  directly without bespoke translation sludge

## Wire Contracts

`GameCult.Networking` now speaks two explicit wire contracts:

- `gamecult.networking.v0`
  - the legacy union-based auth/session/sample message surface
- `cultnet.schema.v0`
  - the newer schema-first contract family for discovery, raw document puts,
    raw snapshot replication, and cross-runtime shared-state work

There is no inbound autodetect priesthood here. Pick the contract on purpose,
keep the schema stable, and let peers discover what they can exchange before
they start lobbing bytes at each other.

Modern schema-v0 helpers live in:

- `CultNetSchemaMessageSerialization`
- `CultNetSchemaRegistry`
- `CultNetDocumentRegistry`
- `CultNetDatabase`
- `CultNetDatabaseServer`
- `NetPeerExtensions.SendCultNet(...)`

## Main Types

- `Message`: base type for all wire messages
- `Client`: client connection, dispatch, reconnect, and login/register helpers
- `Server`: server dispatch, auth flow handling, rate limiting, and session refresh
- `Secret`: encryption and signed-token helper methods
- `PlayerData`: cache-backed player record type

Sample payloads:

- `ChangeNameMessage`
- `ChatMessage`
- `SchemaCatalogRequestMessage`
- `SchemaCatalogResponseMessage`

Those now live under `Samples/` to make it obvious they are example application
messages, not the entire meaning of the library.

Schema-v0 message families include:

- `CultNetHelloMessage`
- `CultNetSchemaCatalogRequestMessage`
- `CultNetSchemaCatalogResponseMessage`
- `CultNetDocumentPutRawMessage`
- `CultNetDocumentDeleteMessage`
- `CultNetSnapshotRequestMessage`
- `CultNetSnapshotResponseRawMessage`

The raw document/snapshot lane is meant for neighbors that already share the
same payload schema and MessagePack semantics. It carries exact payload bytes
plus `schemaId`/record-key metadata; it does not guess what a blob "probably"
means.

## CultMesh Lives In GameCult.Mesh

The distributed realtime database and simulation-consensus surface now has its
own package home: `GameCult.Mesh`, branded publicly as CultMesh. This networking
package still owns CultNet transport, authentication, and schema-v0 wire
contracts. CultMesh owns the developer-facing mesh entrypoints and higher-level
runtime documentation.

See `src/GameCult.Mesh/README.md` for the public-facing CultMesh API.
For one small end-to-end path from durable local typed state into `node.Database`,
see `src/GameCult.Mesh/docs/durable-node-quickstart.md`.

## Distributed CultCache

CultNet should be able to start as a sharding layer over CultCache. In that
mode, applications treat the cluster as one typed realtime database instead of
manually juggling a local cache, a network client, and a synchronization loop.

Target feel:

```csharp
var db = await CultNetDatabase.ConnectAsync("localhost", 3075);

var player = await db.GetAsync<PlayerData>(playerKey);
await db.PutAsync(playerKey, player);

using var subscription = db
    .WatchByIndex<PlayerData>("Region", "eu-west")
    .Subscribe(change => Render(change.Document));
```

CultCache remains the document model:

- schema identity and compatibility
- record keys and handles
- local indexes, globals, and typed lookups
- persistence and local domain-change diffing

CultNet becomes the distribution model:

- peer membership
- shard ownership
- mutation routing
- snapshot catch-up
- remote subscription fanout
- reconnect resynchronization

The current first slice exposes this as `CultNetDatabase` and wires hosted
servers through `CultNetDatabaseServer`. It handles primary-shard writes, typed
R3 watch streams, raw document puts/deletes, raw snapshot responses,
schema/key-filtered live subscriptions, shard catalog exchange, and stale-epoch
write rejection. Non-primary write forwarding is available behind an injectable
`ICultNetShardWriteForwarder`, with `CultNetSchemaWriteForwarder` as the
schema-v0 `cultnet://host:port` endpoint dialer. It also records accepted
mutations in per-shard ordered logs and exposes those logs over
`cultnet.shard_log_request.v0` / `cultnet.shard_log_response.v0` for replica
catch-up. Replicas apply those committed entries through
`CultNetDatabase.ApplyShardLogResponseAsync`, which checks shard epoch, rejects
sequence gaps, and treats replayed entries as already applied.
`CultNetShardReplicator` can then pull non-primary shard logs from advertised
primary endpoints, using `CultNetSchemaShardLogFetcher` for the schema-v0
transport path. Replica cursors can be stored through
`ICultNetShardReplicaCursorStore`; `CultNetFileShardReplicaCursorStore` keeps
those cursors in one local MessagePack file so replicas can resume after a
restart. Authoritative shard logs can be stored through
`ICultNetShardMutationLogStore`; `CultNetFileShardMutationLogStore` keeps one
MessagePack log file per shard and lets a restarted primary continue answering
`cultnet.shard_log_request.v0` from durable wire entries. The same store owns
log compaction: once entries are compacted through a sequence, older catch-up
requests return `ResyncRequired` with `reason = "compacted_history"` instead of
silently handing a replica a partial history. `CultNetShardReplicator` can use
an `ICultNetShardSnapshotFetcher` to recover from that response: it fetches a
shard-bounded snapshot, replaces the local shard view, advances the cursor to
the snapshot's represented log sequence, and resumes normal log pulls on later
ticks.

For client-side prediction, `CultNetClientAuthorityScope` declares which input
documents a runtime may author optimistically. `PutPredictedAsync` updates the
local cache immediately and emits a `Predicted` change; when the authoritative
shard log arrives for the same record, the database emits `Reconciled` and the
cache holds the committed value.

For mesh-side simulation agreement, `CultNetSimulationObservation` records what
a witness saw for one shard epoch, frame, subject, and claim kind.
`CultNetSimulationConsensus` deterministically aggregates those observations
into candidates with support weight, total observed weight, confidence, and
quorum status. Those candidates are opinions ready for an authoritative commit
path, not committed world state by themselves. The schema-v0 wire contracts are
`cultnet.simulation_observation.v0` and
`cultnet.simulation_consensus_candidate.v0`. `CultNetSimulationObservationHub`
collects incoming observations and emits candidate updates reactively.
`CultNetSimulationObservationServer` attaches that hub to a server so incoming
observation messages update the hub and receive current candidate messages in
reply.

The first coherent shard policy is primary ownership. Each shard has one
authoritative writer at a time. Clients may connect to any node; a non-owner
node forwards a write to the owner or rejects it honestly when forwarding is
not available. Followers subscribe to committed shard mutations and apply them
through the same CultCache reconciliation path as local file changes.

This should feel like the local-first parts of Firebase or RethinkDB without
copying their hidden machinery wholesale. The hard line is conflict honesty:
split-brain writes, stale shard epochs, and schema-incompatible documents must
surface as explicit failures instead of being laundered into cheerful-looking
state.

## Authentication Model

The built-in flow is:

1. client connects with the shared LiteNetLib connection key
2. credentials are encrypted with AES-GCM using a per-message nonce
3. server validates credentials and issues a signed session token
4. client stores the encrypted session token and can send `VerifyMessage` on reconnect
5. server validates the token and re-establishes the session

The session token is signed and validated by the server.
Each newly issued session token also carries a monotonic session version so
older signed tokens can be superseded cleanly instead of haunting reconnect
paths forever.

## Runtime Secrets

`GameCult.Networking` uses:

- `GAMECULT_CONNECTION_KEY`
- `GAMECULT_SESSION_SIGNING_SECRET`

Production requirements:

- set both before constructing `Server` without explicit `ServerSecurityOptions`
- use high-entropy random values, not human-memorable strings
- keep `GAMECULT_SESSION_SIGNING_SECRET` in your deployment platform's secret store rather than in source control or appsettings checked into the repo
- distribute `GAMECULT_CONNECTION_KEY` to clients through your build or asset pipeline, not through runtime environment variables on shipped clients
- partial configuration is rejected by `ServerSecurityOptions.FromEnvironment()`

Recommended format:

- 32 or more random bytes per value
- store as Base64/Base64Url text if needed

Example PowerShell generation:

```powershell
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }))
```

Example environment assignment in PowerShell:

```powershell
$env:GAMECULT_CONNECTION_KEY = "<random-base64-value>"
$env:GAMECULT_SESSION_SIGNING_SECRET = "<different-random-base64-value>"
```

Deployment model:

- the client and server must share the same `GAMECULT_CONNECTION_KEY`
- the server alone needs `GAMECULT_SESSION_SIGNING_SECRET`
- `GAMECULT_CONNECTION_KEY` is shared protocol configuration, not a server-only secret
- rotating `GAMECULT_CONNECTION_KEY` requires a coordinated client/server rollout or multi-key server support during migration
- `GAMECULT_SESSION_SIGNING_SECRET` can be rotated server-side with the usual session invalidation tradeoffs

Strict production-style configuration:

```csharp
using GameCult.Caching;
using GameCult.Networking;

var cache = new CultCache();
var security = ServerSecurityOptions.FromEnvironment();
var server = new Server(cache, security);
```

Explicit local-development server configuration:

```csharp
using GameCult.Caching;
using GameCult.Networking;

var cache = new CultCache();
var security = ServerSecurityOptions.Development();
var server = new Server(cache, security);
```

Explicit client configuration:

```csharp
using GameCult.Networking;

var security = new ClientSecurityOptions("<matching-connection-key>");
var client = new Client(security);
```

Use `ServerSecurityOptions.Development()` only for local development and tests. `Client` does not read environment variables.

## Basic Server Usage

Quickest local-host path:

```csharp
using GameCult.Networking;

var host = await CultNetLocal.StartHostAsync("Players.msgpack");
host.Server.On<ChatMessage>(message =>
{
    host.Server.Logger.LogInfo($"Chat: {message.Text}");
});
```

Lower-level explicit path:

```csharp
using GameCult.Caching;
using GameCult.Logging;
using GameCult.Networking;

var cache = new CultCache();
var server = new Server(cache)
{
    Logger = new ConsoleLogger()
};

server.On<ChatMessage>(message =>
{
    server.Logger.LogInfo($"Chat: {message.Text}");
});

server.Start();
```

## Basic Client Usage

Quickest local-client path:

```csharp
using GameCult.Networking;

var client = CultNetLocal.ConnectLocal();
client.On<ChatMessage>(message => Console.WriteLine(message.Text));
client.Login("user@example.com", "correct horse battery staple");
```

Lower-level explicit path:

```csharp
using GameCult.Logging;
using GameCult.Networking;

var client = new Client(new ClientSecurityOptions("<matching-connection-key>"))
{
    Logger = new ConsoleLogger()
};

client.OnError += error => client.Logger.LogError($"Client error: {error}");
client.On<ChatMessage>(message => client.Logger.LogInfo($"Chat: {message.Text}"));

client.Connect("localhost", 3075);
client.Login("user@example.com", "correct horse battery staple");
```

## Reconnect Behavior

The client:

- disposes its prior polling subscription before reconnecting
- stops the prior `NetManager`
- schedules reconnect through `CultNetReconnectController`, using the same
  `cultnet.reconnect_policy.v0` backoff contract advertised by RUDP profiles
- automatically re-verifies using the stored signed session token
- exposes reconnect state for UI/operator surfaces

The native RUDP socket path also exposes `CultNetRudpReconnectLoop` for
caller-owned game or service loops. The caller reports closure, calls
`ReconnectIfDue(nowMs)` from its own scheduler, and the shared
`CultNetReconnectController` owns retry attempts, delay, and exhaustion state.

## Important Constraints

- `Server` currently centers on `PlayerData` as the built-in account model.
- Message authorization is based on the built-in verify/login/register flow.
- Transport is LiteNetLib, so this is not a drop-in substitute for HTTP or WebSocket stacks.

Sensitive payload logging is gated. Raw message JSON should only appear when a
caller explicitly opts into diagnostic logging instead of accidentally bleeding
live auth/session payloads into ordinary logs.

## Message Example

Messages derive from `Message` and are MessagePack-serializable:

```csharp
using GameCult.Networking;
using MessagePack;

[MessagePackObject]
public class CustomPingMessage : Message
{
    [Key(0)] public string Value = string.Empty;
}
```

If you extend the built-in message set, update the union and serialization model accordingly.

If you want cross-runtime compatibility with TypeScript, Rust, Python, or
anything else, treat message tags and field keys like CultCache schema:

- explicit
- stable
- shared
- boring in exactly the useful way

For the newer schema-v0 lane, the equivalent rule is:

- keep canonical JSON Schema files in sync
- keep payload codecs in sync
- advertise supported contracts through schema discovery
- do not rely on implicit runtime magic to decide what a peer meant

## Future hostile-network work

The next hardening tranche is documented in
`production-hardening-checklist.md`. That ledger names the missing transport,
replay, abuse-handling, and adversarial-test organs directly instead of
pretending local-mesh readiness and internet readiness are the same rite.
