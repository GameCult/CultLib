# 2. Connect A Client By Stable Identity

Clients address a live session by stable Verse/provider identity. In this tutorial,
`sample.counter` is the Verse id and `sample.counter-daemon` is the authority
runtime Odin currently advertises for it. Application clients pass that typed
pair, never a socket address and never one ambiguous string that might mean
either identity. A product may expose only a Verse choice to its user; its
selection owner then resolves the provider runtime from Odin's descriptor before
opening the session. Discovery owns the current physical routes, and the session
manager owns connection reuse, path rotation, and transport failure state.
Each Odin route binds that authority runtime to one endpoint, protocol set, and
generation. The client verifies the same tuple with the connected peer before
the session becomes online; runtime membership and endpoint reachability are
not independent routing evidence.

```csharp
using GameCult.Mesh;

var odinEndpoint = Environment.GetEnvironmentVariable("ODIN_ENDPOINT")
    ?? throw new InvalidOperationException("Set ODIN_ENDPOINT to the configured rendezvous route.");
const string verseId = "sample.counter";
var target = new CultMeshSessionTarget(verseId, "sample.counter-daemon");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var cancellationToken = timeout.Token;
using var mesh = new CultMeshClient(new CultMeshClientOptions
{
    RendezvousEndpoints = new[] { odinEndpoint }
});

using var counterLease = await mesh.LeaseDocumentAsync<CounterState>(
    target,
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
    target,
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

The real checkpoint in chapter 4 additionally advertises a better-priority
endpoint for the wrong authority runtime. Both the C# and browser clients must
ignore it, verify the intended peer, survive route replacement, and reject
wrong-source operation responses.

Advanced hosts may inject lookup sources, connectors, clocks, persistence, and
diagnostics through the lower-level discovery and session APIs. Application
features should not need them.

Chapter 4 runs the real network chronology and proves that the retained lease
survives Odin moving `sample.counter` to a new physical provider route.

Next: [publish one Eve surface to multiple runtimes](03-publish-an-eve-surface.md).
