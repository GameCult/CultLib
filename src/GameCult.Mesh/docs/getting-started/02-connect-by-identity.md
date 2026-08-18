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
generation. For a remote Verse, Odin signs that exact tuple and the provider's
P-256 public key. The client pins an Odin root from its own configuration, then
requires the connected provider to sign a fresh client nonce. A route cannot
become usable by merely repeating the expected Verse and runtime strings.

```csharp
using GameCult.Mesh;
using GameCult.Networking.WebSockets;

var odinEndpoint = Environment.GetEnvironmentVariable("ODIN_ENDPOINT")
    ?? throw new InvalidOperationException("Set ODIN_ENDPOINT to the configured rendezvous route.");
const string verseId = "sample.counter";
var target = new CultMeshSessionTarget(verseId, "sample.counter-daemon");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var cancellationToken = timeout.Token;
var odinRoot = new CultMeshEcdsaP256PublicKey(
    Environment.GetEnvironmentVariable("ODIN_ROOT_KEY_ID")
        ?? throw new InvalidOperationException("Set ODIN_ROOT_KEY_ID."),
    Environment.GetEnvironmentVariable("ODIN_ROOT_P256_X")
        ?? throw new InvalidOperationException("Set ODIN_ROOT_P256_X."),
    Environment.GetEnvironmentVariable("ODIN_ROOT_P256_Y")
        ?? throw new InvalidOperationException("Set ODIN_ROOT_P256_Y."));
using var mesh = new CultMeshClient(new CultMeshClientOptions
{
    RendezvousEndpoints = new[] { odinEndpoint },
    Discovery = new CultMeshVerseDiscoveryClientOptions
    {
        CreateClientForEndpoint = _ => new CultNetWebSocketSchemaClient()
    },
    Sessions = new CultMeshSessionManagerOptions
    {
        Trust = new CultMeshAuthorityTrustPolicy(
            CultMeshAuthorityTrustMode.AuthenticatedRemote,
            new[] { odinRoot })
    },
    Connectors = new ICultMeshTransportConnector[]
    {
        new CultMeshUriSchemaTransportConnector(
            "cultnet-websocket",
            new[] { "wss" },
            _ => new CultNetWebSocketSchemaClient())
    }
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

For a deliberately local, moddable Verse, select the exception explicitly and
use only in-process or loopback routes:

```csharp
Sessions = new CultMeshSessionManagerOptions
{
    Trust = new CultMeshAuthorityTrustPolicy(CultMeshAuthorityTrustMode.LocalDevelopment)
}
```

`LocalDevelopment` never accepts an unsigned non-loopback route. Verse names,
provider names, and Odin wire fields cannot turn this exception on. Remote
routes require a trusted Odin signature, provider nonce proof, certificate
validity, and a protected channel.

The remote example requires both `GameCult.Mesh` and
`GameCult.Networking.WebSockets` at the same CultLib package version. Core
CultMesh does not silently install a network stack: the consumer chooses the
secure connector and its TLS policy. The default TCP connector exists for the
explicit loopback-development case and cannot satisfy authenticated-remote
trust.

An authenticated provider receives an Odin-certified route and its matching
provider private key from deployment configuration. It attaches the proof gate
to the same WSS schema server before publishing the route:

```csharp
using var identity = new CultMeshSessionIdentityServer(
    schemaServer,
    authorityRuntimeId: "sample.counter-daemon",
    verseIds: new[] { "sample.counter" },
    protocolIds: new[] { CultMeshProtocols.Documents.Value },
    routeGenerations: new[] { certifiedRoute.Generation },
    proofSigners: new[] { new CultMeshSessionProofSigner(certifiedRoute, providerPrivateKey) });
```

`certifiedRoute` is the exact signed route Odin publishes. The provider never
holds Odin's signing key, and an unsigned WSS route is still untrusted.

Fast session-management check (this uses controlled transports; it is not the
network proof):

```powershell
dotnet test tests/GameCult.Mesh.Tests/GameCult.Mesh.Tests.csproj --filter FullyQualifiedName~CultMeshSessionManagerTests
```

The local checkpoint in chapter 4 additionally advertises a better-priority
endpoint for the wrong authority runtime. Both the C# and browser clients must
ignore it, verify the intended peer, survive route replacement, and reject
wrong-source operation responses.

Advanced hosts may inject lookup sources, connectors, clocks, persistence, and
diagnostics through the lower-level discovery and session APIs. Application
features should not need them.

Chapter 4 runs the real network chronology and proves that the retained lease
survives Odin moving `sample.counter` to a new physical provider route.

Next: [publish one Eve surface to multiple runtimes](03-publish-an-eve-surface.md).
