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

## Multi-Runtime Checkpoint

Run the same provider and connect at least two lowerers. Both must report the
same provider id, surface id, surface version, and command receipt ids. Native
pixels may differ; provider truth may not.

The complete checkpoint is executable from CultLib:

```powershell
cd CultLib
pwsh -File ./scripts/verify-eve-getting-started.ps1
```

This one command runs the clean package-artifact checkpoint and then the real
browser/C# network checkpoint. They remain separate programs because they prove
different boundaries; the developer should not have to orchestrate them.

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
resubscribes, and commits another canonical receipt.

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
deployed Odin daemon and retained C# lease reconnection remain separate
infrastructure gates. Their owner map and full acceptance contract are recorded in
[`browser-verse-transport.md`](../browser-verse-transport.md).

