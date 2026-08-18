# 4. Run The Multi-Runtime Game

The counter is now one small multiplayer game:

- a C# provider owns `CounterState`, Increment, idempotency, receipts, the
  `.cc` store, and the Eve surface;
- Chromium discovers that provider through Odin, lowers the surface, and
  invokes Increment;
- an independent C# client leases the same counter and invokes through the same
  typed operation boundary;
- both clients remain attached when Odin moves the Verse to a replacement
  physical provider route.

Run the whole chronology from the CultLib checkout:

```powershell
pwsh -File ./scripts/verify-eve-getting-started.ps1
```

The command owns setup and teardown. Do not start Odin, the provider, or the
browser by hand. A successful run prints two JSON records followed by:

```text
Eve getting-started verification passed: clean .NET and TypeScript artifacts, DOM and headless convergence, real Chromium and C# clients, Odin discovery, receipts, persistence, and route replacement.
```

## What The Command Proves

The first checkpoint packs the CultLib managed dependency closure and Eve's C#
surface contract, then restores and runs an empty .NET project using only the
resulting `GameCult.Eve.Surface` package. The second checkpoint packs the
TypeScript CultCache, CultNet, CultMesh, and Eve
packages, installs those artifacts into an empty temporary consumer, and proves
DOM/headless convergence plus `.cc` reopen. This rejects a sample that only
works through repository-relative source imports.

The third checkpoint boots separate Odin, provider, Chromium, and C# headless
processes. Chromium performs the first increment. The provider then restarts on
a different route, Odin advertises the replacement, and the retained clients
resubscribe without application reconnect code. C# performs the second
increment. Both clients must observe count 2 and both canonical receipt ids.

Finally, the retained C# session sends 10,000 typed no-op operations. The gate
records throughput, p99 latency, managed-heap growth, and private-memory growth.
It fails at 250 ms p99 or more than 8 MiB post-GC managed growth.

## What It Does Not Prove

The included Odin is a real CultNet Verse-catalog provider but still a local
fixture. It does not prove deployed GameCult infrastructure. The package layer
uses packed checkout artifacts until the package owners publish the named npm
packages. Those are explicit release gates, not reasons to put endpoint or
source-tree fallbacks into application code.

The process harness lives in
[`samples/eve-browser-network`](../../../../samples/eve-browser-network/README.md).
Application code should copy the typed state, operation, and Eve composition
shape—not its benchmark flags or fixture orchestration.

For a TypeScript provider daemon, continue with the
[`CultMeshProviderSession` guide](../../../../packages/cultmesh-ts/README.md#long-lived-providers).
That lifecycle is an alternative provider runtime for the same ownership
model, not a fifth chapter that changes this tutorial into a different product.
