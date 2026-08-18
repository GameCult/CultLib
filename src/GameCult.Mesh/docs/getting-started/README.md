# CultMesh And Eve Getting Started

This series builds one typed application state and presents it through multiple
Eve runtimes. It is maintained as an API acceptance test: examples use public
APIs, name the owner of each decision, and include a verification checkpoint.

Run the complete getting-started checkpoint:

```powershell
pwsh -File ./scripts/verify-eve-getting-started.ps1
```

With clean sibling CultLib and Eve checkouts, the command installs missing build
dependencies and runs two explicit evidence layers. First it packs the five
TypeScript packages into an empty temporary consumer, validates the Eve surface
and receipt, executes one idempotent counter command, proves a DOM lowerer and
headless observer converge, and reopens the durable `.cc` store. Then it boots a
durable C# provider, local Odin fixture, real Chromium lowerer, and independent
C# client. Chromium invokes before provider replacement; C# invokes afterward.
Both retained clients prove discovery by identity, canonical receipts, provider
restart on another route, resubscription, and durable convergence. The retained
C# session also completes 10,000 non-persistent typed operations under p99 and
post-GC managed-memory gates.
Set `CHROME_PATH` only when no installed Chrome, Chromium, Edge, or Playwright
Chromium can be found automatically.

Follow the chapters in order:

1. [Persist typed application state](01-persist-typed-state.md)
2. [Connect a client by stable identity](02-connect-by-identity.md)
3. [Publish one Eve surface to multiple runtimes](03-publish-an-eve-surface.md)
4. [Keep a TypeScript provider alive](04-keep-a-typescript-provider-alive.md)

The intended application shape is small:

```text
provider daemon -> typed CultCache documents -> CultMesh advertisement
                                               |
                      +------------------------+--------------------+
                      |                        |                    |
                 Eve browser              EveUnity          another lowerer
```

The provider owns live truth. Eve owns the surface contract. Each runtime owns
native projection. CultMesh owns discovery and connection continuity.

## Maintenance Contract

- A chapter must not tell an application to choose physical endpoints, build a
  reconnect loop, or duplicate provider state.
- Code must compile against the package version named by the release that
  publishes it.
- Every release changing a demonstrated API must update this series in the same
  pull request.
- A setup step that depends on repository internals is an API defect, not
  acceptable tutorial folklore.
