# 2. Connect A Client By Stable Identity

Clients address a provider or Verse by stable identity. Discovery owns the
current physical candidates and the session manager owns connection reuse,
path rotation, and transport failure state.

```csharp
using GameCult.Mesh;

using var discovery = new CultMeshDiscoveryService(new ICultMeshLookupSource[]
{
    odinLookupSource
});

using var sessions = new CultMeshSessionManager(
    discovery,
    new ICultMeshTransportConnector[]
    {
        new CultMeshSchemaTransportConnector()
    });

var provider = CultMeshEndpointId.Parse("aetheria.daemon");
var session = await sessions.ConnectAsync(
    provider,
    CultMeshProtocols.Documents,
    cancellationToken);

using var client = session.OpenSchemaClient();
```

Keep `discovery` and `sessions` for the application lifetime. Do not construct
them per request. `session` is the logical connection: when Odin advertises a
new physical route after a partition, the same session migrates and retains its
typed handler registrations.

Watch connection state for presentation and diagnostics:

```csharp
using var stateWatch = session.WatchState().Subscribe(state =>
    Console.WriteLine($"{state.Status}: {state.Path?.Endpoint}"));
```

Application code does not reconnect, sleep, rank endpoints, or replace the
session. Those decisions belong to CultMesh.

Verification:

```powershell
dotnet test tests/GameCult.Mesh.Tests/GameCult.Mesh.Tests.csproj --filter FullyQualifiedName~CultMeshSessionManagerTests
```

The explicit organ construction above is the current low-level API. The
identity-first `mesh.ConnectAsync(...)` facade remains planned work; this guide
will collapse to that form when it lands.

Next: [publish one Eve surface to multiple runtimes](03-publish-an-eve-surface.md).

