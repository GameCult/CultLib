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

The package-artifact browser/headless checkpoint is executable from CultLib:

```powershell
cd CultLib
powershell -ExecutionPolicy Bypass -File .\scripts\verify-eve-two-runtime-sample.ps1 `
  -EveRoot ..\Eve
```

The verifier builds and packs `cultcache-ts`, `cultnet-ts`, `cultmesh-ts`, and
`@gamecult/eve-browser-lowering`, installs the tarballs into an empty temporary
consumer, and runs `samples/eve-two-runtime/sample.mjs`. One provider-owned
counter surface is lowered by a browser runtime and observed by a headless
runtime. A browser command crosses the typed operation boundary, both runtimes
converge on the same state and receipt identity, a duplicate idempotency key is
not applied twice, and the state plus receipt survive reopening the `.ccmp`
store.

Current verification for the Unity consumer remains:

```powershell
cd E:\Projects\EveUnity
powershell -ExecutionPolicy Bypass -File .\scripts\run-release-consumer-tests.ps1
```

This checkpoint proves clean package consumption and two lowering/observation
runtimes. It does not claim an Odin-discovered network hop or cross-language
command execution; those remain separate public Verse conformance gates.

