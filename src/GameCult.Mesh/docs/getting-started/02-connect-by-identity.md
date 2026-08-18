# 2. Connect A Client By Stable Identity

Clients address a Verse session by stable identity. In this tutorial,
`sample.counter-provider` is the sole authority runtime Odin advertises for the
`sample.counter` Verse; neither identity is a socket address. Discovery owns
current physical candidates and the session manager owns connection reuse,
path rotation, and transport failure state.

```csharp
using GameCult.Mesh;

using var mesh = new CultMeshClient(new CultMeshClientOptions
{
    RendezvousEndpoints = new[] { "rudp://odin.gamecult.net:3076" }
});

using var counterLease = await mesh.LeaseDocumentAsync<CounterState>(
    "sample.counter",
    "counter:main",
    cancellationToken);
var counter = counterLease.Handle;

using var counterWatch = counter.Watch(value =>
    Console.WriteLine($"Count: {value.Count}"));
```

`CounterState` is the application document from chapter 1. Eve documents enter
in chapter 3, where the sample installs the renderer-neutral Eve contract
package explicitly. This identity/session API does not depend on a UI runtime.

Keep `mesh` for the application lifetime and dispose each document lease when
the consuming screen or system closes. Leases for the same identity share one
subscription. The document handle is stable while leased: when
Odin advertises a new physical route after a partition, CultMesh migrates the
session, restores the server-side subscription, refreshes the typed snapshot,
and continues the same watch.

Open a session directly only when implementing infrastructure that needs
protocol-level state or diagnostics:

```csharp
var session = await mesh.ConnectAsync(
    "aetheria.daemon",
    CultMeshProtocols.Documents,
    cancellationToken);

using var stateWatch = session.WatchState().Subscribe(state =>
    Console.WriteLine($"{state.Status}: {state.Path?.Endpoint}"));
```

Application code does not reconnect, sleep, rank endpoints, or replace the
session. Those decisions belong to CultMesh.

Fast session-management check (this uses controlled transports; it is not the
network proof):

```powershell
dotnet test tests/GameCult.Mesh.Tests/GameCult.Mesh.Tests.csproj --filter FullyQualifiedName~CultMeshSessionManagerTests
```

Advanced hosts may inject lookup sources, connectors, clocks, persistence, and
diagnostics through the lower-level discovery and session APIs. Application
features should not need them.

Chapter 4 runs the real network chronology and proves that the retained lease
survives Odin moving `sample.counter` to a new physical provider route.

Next: [publish one Eve surface to multiple runtimes](03-publish-an-eve-surface.md).
