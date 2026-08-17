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

The package-artifact DOM-lowerer/headless checkpoint is executable from CultLib:

```powershell
cd CultLib
powershell -ExecutionPolicy Bypass -File .\scripts\verify-eve-two-runtime-sample.ps1 `
  -EveRoot ..\Eve
```

The verifier builds and packs `cultcache-ts`, `cultnet-ts`, `cultmesh-ts`,
`@gamecult/eve-contracts`, and `@gamecult/eve-browser-lowering`, installs the
tarballs into an empty temporary consumer, and runs
`samples/eve-two-runtime/sample.mjs`. Eve's generated runtime validators reject
an invalid surface or receipt before either enters the store. One provider-owned
counter surface is lowered into a jsdom DOM host and observed by an independent
headless CultMesh observer. A DOM command crosses the typed operation boundary,
both consumers converge on the same state and canonical receipt identity, a
duplicate idempotency key is not applied twice, and the state plus receipt
survive reopening the `.cc` store.

Current verification for the Unity consumer remains:

```powershell
cd E:\Projects\EveUnity
powershell -ExecutionPolicy Bypass -File .\scripts\run-release-consumer-tests.ps1
```

This checkpoint proves clean package consumption, canonical Eve contract
validation, and two independent lowering/observation consumers inside Node. It
does **not** claim a real browser process, an Odin-discovered network hop, or
cross-language command execution. Those are the next public Verse conformance
gates; do not cite this sample as evidence that they exist. Their owner map and
executable acceptance contract are recorded in
[`browser-verse-transport.md`](../browser-verse-transport.md).

