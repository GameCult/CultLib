# CultLib

CultLib is a set of reusable C# libraries for game backends, game-adjacent services, and Unity-integrated tooling.

The libraries cover three main areas:

- logging primitives and implementations
- a typed in-memory cache with pluggable persistence
- LiteNetLib-based networking with encrypted credential exchange and signed session tokens

## Repository Scope

The solution includes:

- `GameCult.Logging`: common logging abstraction plus console and file implementations
- `GameCult.Caching`: `DatabaseEntry`-based cache, indexes, global entries, and backing-store abstractions
- `GameCult.Caching.MessagePack`: MessagePack-backed persistence for the cache
- `GameCult.Caching.NewtonsoftJson`: Newtonsoft.Json-backed persistence for the cache
- `GameCult.Caching.MessagePack.Generator`: source generator for MessagePack formatters for cache models
- `GameCult.Caching.MessagePack.Analyzers`: packaging project that delivers the generator to consuming projects
- `GameCult.Networking`: encrypted login/register/verify flows and message dispatch over LiteNetLib
- `GameCult.Caching.Tests`: NUnit tests for cache and backing-store behavior
- `GameCult.Networking.Tests`: NUnit tests for networking behavior
- `GameCult.Unity`: Unity-side project and integrations used with the runtime libraries

## Repository Layout

```text
src/
  GameCult.Logging/
  GameCult.Caching/
  GameCult.Caching.MessagePack/
  GameCult.Caching.NewtonsoftJson/
  GameCult.Caching.MessagePack.Generator/
  GameCult.Caching.MessagePack.Analyzers/
  GameCult.Networking/
  GameCult.Unity/
tests/
  GameCult.Caching.Tests/
  GameCult.Networking.Tests/
```

## Build

```powershell
dotnet build CultLib.sln
```

## Test

```powershell
dotnet test CultLib.sln
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
- [GameCult.Networking](src/GameCult.Networking/README.md)
- [GameCult.Unity](src/GameCult.Unity/README.md)
- [GameCult.Caching.Tests](tests/GameCult.Caching.Tests/README.md)
- [GameCult.Networking.Tests](tests/GameCult.Networking.Tests/README.md)
