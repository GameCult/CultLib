# 3. Publish One Eve Surface To Multiple Runtimes

An Eve application publishes one provider advertisement and one or more typed
surface documents through CultMesh. Browser, Unity, Electron, Flutter, and
other clients discover the same advertisement and lower only the capabilities
they support.

The authority split is fixed:

- provider daemon: live application state, command handling, receipts, assets;
- Eve: surface, command, binding, embedding, and plugin contracts;
- CultMesh: discovery, sessions, document delivery, and asset transfer;
- runtime: native rendering, input sampling, and platform lifecycle.

A runtime configuration contains identity and selection constraints, not a
copy of provider behavior:

```text
rendezvous: odin
provider: aetheria.daemon
surface kind: interactive-world
runtime capabilities: unity-scene + eve-fields
```

EveUnity currently expresses this through
`EveUnityCultMeshPlayableWorldProvider.Configure(...)`. Its provider discovery,
advertisement snapshot, live snapshots, and subscriptions share one managed
CultMesh document session. Other runtime lowerers must preserve the same
boundary: they may reject or report unsupported capabilities, but they may not
import provider internals.

## The Application-Owned Part

Install the renderer-neutral `GameCult.Eve.Surface` package in the provider.
Do not install EveUnity or the browser lowerer there. A provider defines typed
state, one typed operation transaction, and one Eve surface. CultNet envelope
encoding is framework work:

```csharp
using GameCult.Eve.Surface;
using GameCult.Mesh;
using GameCult.Networking;

using var operations = new CultNetOperationServer(schemaServer, "sample.counter-provider")
    .Register<IncrementRequest, IncrementReceipt>(
        "sample.counter",
        "sample.counter.increment",
        "sample.increment.v1",
        "gamecult.eve.command_receipt.v1",
        async command => await IncrementExactlyOnceAsync(
            command.IdempotencyKey,
            command.Value));

var route = new CultMeshRouteHint(CultMeshLocalityKind.Network, "cultmesh");
var count = new CultMeshStateBindingDescriptor(
    "value",
    "sample.counter.count",
    "sample.counter_state:counter:main",
    "sample.counter_state.v1",
    route);
var increment = CultMesh.OperationBinding(
    "sample.counter.increment",
    "Increment",
    "sample.increment.v1",
    route);

var surface = EveSurface.Create("sample.counter")
    .Provider("sample.counter-provider", "sample.daemon")
    .Title("CultMesh browser counter")
    .RootColumn("counter.root", root => root
        .Metric("counter.value", "Canonical count", "0", count)
        .Button("counter.increment", "Increment", increment))
    .Build();
```

`schemaServer` is a transport-neutral CultNet server port. WebSocket, RUDP, and
test transports attach the same dispatcher. The handler owns validation,
idempotency, state mutation, and its durable receipt. `CultNetOperationServer`
owns only route/schema checks and typed envelope serialization. The complete
provider transaction is in
[`samples/eve-browser-network/Program.cs`](../../../../samples/eve-browser-network/Program.cs);
it contains no hand-written operation base64 or MessagePack dispatch switch.

## Multi-Runtime Checkpoint

Run the same provider and connect at least two lowerers. Both must report the
same provider id, surface id, surface version, and command receipt ids. Native
pixels may differ; provider truth may not.

The complete checkpoint is executable from CultLib:

```powershell
cd CultLib
pwsh -File ./scripts/verify-eve-getting-started.ps1
```

This one command runs clean .NET and TypeScript package-artifact checkpoints,
then the real browser/C# network checkpoint. They remain separate programs
because they prove different boundaries; the developer should not have to
orchestrate them.

The .NET layer packs CultLib's managed dependency graph and
`GameCult.Eve.Surface`, then restores and runs an empty project whose only
GameCult dependency is `PackageReference Include="GameCult.Eve.Surface"`.
This proves the renderer-neutral provider contract does not require a sibling
Eve or CultLib source tree after packaging.

The first layer builds and packs `cultcache-ts`, `cultnet-ts`, `cultmesh-ts`,
`@gamecult/eve-contracts`, and `@gamecult/eve-browser-lowering`, installs the
tarballs into an empty temporary consumer, and runs
`samples/eve-two-runtime/sample.mjs`. Eve's generated runtime validators reject
an invalid surface or receipt before either enters the store. One provider-owned
counter surface is lowered into a jsdom DOM host and observed by an independent
headless CultMesh observer. A DOM command crosses the typed operation boundary,
both consumers converge on the same state and canonical receipt identity, a
duplicate idempotency key is not applied twice, and the state plus receipt
survive reopening the `.cc` store.

The second layer runs a durable C# provider, a separate local Odin fixture, a
real Chromium Eve lowerer, and an independent C# headless observer over binary
CultNet WebSocket lanes. The open browser resolves provider identity through
the canonical Verse catalog, survives a provider restart on a different route,
and resubscribes. The retained C# client then invokes a second typed operation
through `CultMeshClient.InvokeAsync`; Chromium and C# observe the same
provider-authored state and receipt chronology. The same retained C# session
then completes 10,000 typed no-op operations under explicit p99 and post-GC
managed-memory gates, so the tutorial pressure-tests the API it teaches.

Current verification for the Unity consumer remains:

```powershell
cd ../EveUnity
pwsh -File ./scripts/run-release-consumer-tests.ps1
```

The artifact layer proves clean package consumption, canonical Eve contract
validation, and two independent lowering/observation consumers inside Node. It
does **not** prove the network layer; the second program owns that evidence.

The network program is
[`samples/eve-browser-network`](../../../../samples/eve-browser-network/README.md).
Run it separately only while diagnosing that layer. A smoke against the
deployed Odin daemon remains a separate infrastructure gate. Its owner map
and full acceptance contract are recorded in
[`browser-verse-transport.md`](../browser-verse-transport.md).

