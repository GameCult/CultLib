# 2. Connect A Client By Stable Identity

Clients address a provider or Verse by stable identity. Discovery owns the
current physical candidates and the session manager owns connection reuse,
path rotation, and transport failure state.

```csharp
using GameCult.Mesh;

using var mesh = new CultMeshClient(new CultMeshClientOptions
{
    RendezvousEndpoints = new[] { "rudp://odin.gamecult.net:3076" }
});

var session = await mesh.ConnectAsync(
    "aetheria.daemon",
    CultMeshProtocols.Documents,
    cancellationToken);

using var client = session.OpenSchemaClient();
```

Keep `mesh` for the application lifetime. `session` is the logical connection:
when Odin advertises a new physical route after a partition, the same session
migrates and retains its typed handler registrations.

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

Advanced hosts may inject lookup sources, connectors, clocks, persistence, and
diagnostics through the lower-level discovery and session APIs. Application
features should not need them.

Next: [publish one Eve surface to multiple runtimes](03-publish-an-eve-surface.md).
