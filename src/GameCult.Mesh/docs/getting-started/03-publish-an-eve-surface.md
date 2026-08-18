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
pwsh -File ./scripts/verify-eve-two-runtime-sample.ps1 -EveRoot ../Eve
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
cd ../EveUnity
pwsh -File ./scripts/run-release-consumer-tests.ps1
```

This first checkpoint proves clean package consumption, canonical Eve contract
validation, and two independent lowering/observation consumers inside Node. It
does **not** claim a real browser process, an Odin-discovered network hop, or
cross-language command execution. Do not cite it as evidence for those layers.

The next executable checkpoint is
[`samples/eve-browser-network`](../../../../samples/eve-browser-network/README.md).
It runs a durable C# provider, a separate local Odin fixture, a real Chromium
Eve lowerer, and an independent C# headless observer over authenticated binary
CultNet WebSocket lanes. The still-open browser resolves stable identity through
the canonical Verse catalog, survives a provider restart on a different
physical route, resubscribes, and commits another canonical receipt. A smoke
against the deployed Odin daemon and retained C# lease reconnection remain
separate infrastructure gates. Their owner map and full acceptance contract are recorded in
[`browser-verse-transport.md`](../browser-verse-transport.md).

