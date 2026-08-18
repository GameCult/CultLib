# Eve Two-Consumer Counter

This is the smallest executable package-consumer checkpoint for CultMesh plus
Eve. It intentionally has one owner for each decision:

- the provider node owns the counter document and command transaction;
- Eve owns the validated surface and command-receipt contracts;
- CultMesh owns typed observation and the operation boundary;
- the DOM lowerer owns HTML only;
- the headless observer owns no state.

Run it from the CultLib repository:

```powershell
pwsh -File ./scripts/verify-eve-getting-started.ps1
```

That command continues into the real Chromium/C# network checkpoint after this
artifact layer passes. Run `verify-eve-two-runtime-sample.ps1` directly only to
diagnose clean package consumption in isolation.

From clean CultLib and Eve checkouts, the verifier installs missing build
dependencies, builds package artifacts, installs them into a new empty
temporary consumer, and runs `sample.mjs`. Success proves:

1. no source-tree import is required by the consumer;
2. the provider surface and receipt pass Eve's generated runtime validators;
3. a DOM click executes one idempotent provider operation;
4. the DOM binding and a separate headless observer converge on the same count;
5. the canonical receipt and state survive reopening the `.cc` store.

This artifact checkpoint is deliberately not the network proof. The DOM lowerer
is hosted by jsdom in Node and both consumers share one local CultMesh node. The
same wrapper continues into a separate real Chromium/C# process chronology with
Odin discovery, route replacement, and persistent provider state. Keeping those
evidence layers separate prevents a fast package test from impersonating the
network.
