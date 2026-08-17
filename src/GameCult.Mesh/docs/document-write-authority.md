# CultMesh Document Write Authority

CultMesh document reads, authoritative replacements, and client predictions are
different capabilities. A generic setter cannot choose between them without
hiding an authority decision from the caller.

## Authority map

Owner:

- `CultMeshDocumentHandle<T>` owns typed snapshot and watch access;
- an authoritative writer owns canonical replacement capability;
- a prediction writer owns candidate/prediction submission capability;
- `CultMeshReactiveDocument<T>` owns only a local editable mirror and its
  coalescing schedule.

Inputs:

- a bound typed live feed;
- an optional authoritative replacement delegate;
- an optional prediction delegate;
- explicit local mutations made through the reactive document API.

Outputs:

- coherent typed snapshots and watches;
- authoritative replacements only through an explicitly requested
  authoritative writer;
- predictions only through an explicitly requested prediction writer;
- one coalesced write for each explicit burst of local mutations.

Derived state:

- the reactive snapshot is a local mirror, not canonical truth;
- reconciliation metadata compares a pending local write with an incoming
  canonical snapshot;
- debounce timers schedule already-declared work and never inspect documents
  for undeclared mutations.

Forbidden writers:

- `SetAsync` may not select prediction or replacement based on whichever
  delegate happens to exist;
- reading or watching a document may not start serialization polling;
- mutating an exposed object graph may not silently become a network write;
- alias conversion may not erase which writer was selected.

Shared paths:

- direct edits, nested edits, automation, and UI bindings use `Update` or
  `ReplaceLocal` on the same reactive mirror;
- explicit flush and debounced flush use the same selected writer;
- canonical watch updates and refresh use the same snapshot adoption path.

Cut line:

- remove `CanSet`, `SetAsync`, and ambiguous `UpdateAsync`/`ReactiveAsync`
  entry points from document handles and catalogs;
- obtain either an authoritative writer or a prediction writer before editing;
- remove change-detection timers and full-document idle serialization;
- expose cloned snapshots so callers cannot mutate the live mirror behind the
  mutation boundary.

## Required proof

- a handle carrying both capabilities cannot write until the caller selects a
  writer;
- authoritative and prediction writers reach only their named delegates;
- nested edits made through `Update` are coalesced and published;
- direct mutation of a returned snapshot cannot publish;
- an idle reactive document creates no periodic detection work;
- scheduling 1, 100, or 1,000 reactive documents creates zero idle work, and
  editing one percent schedules only that one percent;
- canonical reconciliation and in-flight edits remain deterministic.

Run the cross-runtime scheduling gate from a CultLib checkout:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/verify-reactive-document-scaling.ps1
```

The gate runs the C#, TypeScript, and Python implementations against the same
1/100/1,000-document cases. It proves ownership and scheduling proportionality;
it does not claim allocation, payload-size, or end-to-end latency budgets. Those
need a separate measured benchmark against representative document bodies and
real transports.
