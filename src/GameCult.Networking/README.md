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

## Authentication Model

The built-in flow is:

1. client connects with the shared LiteNetLib connection key
2. credentials are encrypted with AES-GCM using a per-message nonce
3. server validates credentials and issues a signed session token
4. client stores the encrypted session token and can send `VerifyMessage` on reconnect
5. server validates the token and re-establishes the session

The session token is signed and validated by the server.

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

```csharp
using GameCult.Caching;
using GameCult.Logging;
using GameCult.Networking;

var cache = new CultCache();
var server = new Server(cache)
{
    Logger = new ConsoleLogger()
};

server.AddMessageListener<ChatMessage>(message =>
{
    server.Logger.LogInfo($"Chat: {message.Text}");
});

server.Start();
```

## Basic Client Usage

```csharp
using GameCult.Logging;
using GameCult.Networking;

var client = new Client(new ClientSecurityOptions("<matching-connection-key>"))
{
    Logger = new ConsoleLogger()
};

client.OnError += error => client.Logger.LogError($"Client error: {error}");
client.AddMessageListener<ChatMessage>(message => client.Logger.LogInfo($"Chat: {message.Text}"));

client.Connect("localhost", 3075);
client.Login("user@example.com", "correct horse battery staple");
```

## Reconnect Behavior

The client:

- disposes its prior polling subscription before reconnecting
- stops the prior `NetManager`
- schedules reconnect with a short delay
- automatically re-verifies using the stored signed session token

## Important Constraints

- `Server` currently centers on `PlayerData` as the built-in account model.
- Message authorization is based on the built-in verify/login/register flow.
- Transport is LiteNetLib, so this is not a drop-in substitute for HTTP or WebSocket stacks.

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
