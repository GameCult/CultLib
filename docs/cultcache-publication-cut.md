# CultCache publication: the cache owns "published once", and order stays data

Status, 2026-09-29: Cuts 1-3 landed on `main`. Cut 3 merged at `ae8576d`. Imagination pass 4 re-took Cuts 4-6
against `3bf1c0c`. Q-P5 is ruled (A). Cut 4 is ready for Hands.
- F4, pinning `Held`'s publish-on-throw guard, is done: Cut 3 pinned it.
- F3 (observer removal on unsubscribe) and F1 (the false "getters run once" claim for conditional commits on variant
  stores) were Cut 3 follow-ups; confirm against the Cut 3 merge.

Recorded:
- F2, the Q-P2 hazard. A rethrown observer exception makes a committed write look cancelled or refused. The
  only live typed catch around a cache write is AetheriaEve `Aetheria.State.Daemon/Program.cs:3722`. It needs a
  fail-fast handler, and no production code installs one.
- F5: subscribe and unsubscribe cost O(n).
- F6: writes after `Dispose` are dropped silently.

Map written by Imagination pass 1, 2026-09-29. The ends are the
operator ruling of 2026-09-30: every committed cache change reaches subscribers
exactly once. This document owns the means.

- **Body.** CultLib `main` `65311c9ad770828adafe40c2c14b2d3da517628e`. Every
  `file:line` is against it unless it names the branch.
- **Superseded attempt.** `origin/hands/cultnet-publish-commits`, tip
  `28248eb`, base `f9f107a`. Soul pass 3 said do not merge it, and that stands.
  - Main has not touched `CultCache.cs`, `CultNetDatabase.cs`, the composition
    doc or the networking tests since `f9f107a`: `git diff --stat` is empty.
  - `git merge-tree 65311c9 28248eb` is clean.
- **Scope.** The C# cache's publication and CultNetDatabase's use of it. There
  is no wire-format change and no other runtime changes (§6).


## Rulings (operator, 2026-09-30)

- **Q-P1 A: order as data.** Each change is published exactly once, on its writer's thread, before the write
  returns, and carries its `Sequence`. The shard log is written in cache order by the gate-held journal. Wire
  peers drop changes that are stale by `Sequence`. The turnstile (`bcae483`/`4562340`) and the drainer are
  both rejected.
- **Q-P2 A:** a throwing observer does not stop the others. Every observer runs, the exception is rethrown to
  the writer afterwards, and the observer stays subscribed. The commit stands.
- **Q-P3 A:** a public `CultCache.AddJournal(...)`, with reentry guarded.
- **Q-P4 A:** Cut 6, which drops stale changes per key on the wire, lands in this campaign.
- **Q-P5 A (2026-09-29):** a primary that cannot log a committed change throws a typed `CultNetShardLogException`
  naming the shard and the burned sequence, after publication, not derived from `InvalidOperationException`.

## The finding that shapes this map

The direction Imagination was handed was: "ordered delivery belongs in the
cache's own publication, on the writer's thread, so a writer delivers its own
changes before returning". That design **has been built before, and it was
deleted**.

- `bcae483` (2026-09-13 20:09) made the cache a delivery scheduler: tickets, a
  delivery gate, turn waits and per-thread queues for reentrant writes.
- `4562340`, 34 minutes later, deleted it: "It deadlocked inside CultLib: a
  load's delivery reaching a CultNetDatabaseSubscriptionServer observer that
  needs _lifecycleGate, while a subscribe's demand handler holding that gate
  writes the cache and waits for the delivery's turn."
- It replaced it with **order as data**: a per-cache `Sequence`, and no
  delivery scheduler.
- The regression tests it left, `ObserverWritingTheSameCacheDoesNotWaitOnOtherDeliveries`
  (`ConditionalCommitTests.cs:763`) and
  `SubscriptionDemandHandlerWritingCacheDuringDeliveryDoesNotDeadlock`
  (`NetworkingTests.cs:2244`), are live on main.

This pass rebuilt that scheduler as a prototype. It is the per-cache turnstile
with same-thread trampolining at `79f4639`, and it reproduces the deletion:

- Caching tests: 278 of 279 pass, and `ObserverWritingTheSameCacheDoesNotWaitOnOtherDeliveries`
  fails.
- The networking suite hangs, and Yggdrasil's idle watchdog killed it at 900 s.
  The blame-hang run that names the hung test is recorded below.

The reason is structural, not an implementation slip. Once a writer waits for
another thread's delivery, any lock that an observer takes and a writer holds
becomes a deadlock:

- total cross-thread order;
- a writer delivering before it returns;
- no writer waiting on another thread.

Pick two. The branch's drainer chose order plus no-wait, and lost
before-return (F3 and F4). The turnstile chose order plus before-return, and
lost no-wait (the `bcae483` deadlock).

**So this map keeps order as data (the `4562340` ruling) and moves the one
order-sensitive consumer, the CultNet mutation log, to the place order is
decided: under the cache gate.**

- Every writer keeps delivering its own changes, on its own thread, before it
  returns, exactly as main does. F3 is satisfied by not regressing it.
- The log becomes a **journal** written in the admitting hold, in Sequence
  order.
- The reorder buffer and drainer die, and no scheduler replaces them. This
  departs from Soul's stated direction on purpose, and **Q-P1** puts it to the
  operator.

## Probe evidence

All of this ran on Yggdrasil via `ygg-verify.sh`, from scratch commits in a
throwaway worktree that no branch holds.

- Probe file: `tests/GameCult.Caching.Tests/PublicationProbeTests.cs` in scratch commit `79f4639`. The worktree has been removed. The commit is unreferenced in the local object store, where gc can collect it; it also survives on the Yggdrasil mirror as `refs/verify/79f4639f8f5b95db7ce16f244a081c1ec101a468`.
- `9c5abf5` is the probes on main. `79f4639` is the same probes plus the
  turnstile prototype in `CultCache.cs`.

| # | Probe | main `65311c9` | Turnstile prototype `79f4639` |
|---|---|---|---|
| Q1 | A batch `{ok, boom}` where boom's `[CultName]` getter throws | `TargetInvocationException`. `_sequence` goes from 0 to 1, because ok was minted. **Both ok and boom are in memory**, and `GetByName("ok")` finds ok. **Nothing is published**, and the next write gets Sequence 2. F1 is worse than stated: memory is corrupt and unpublished. In a store-backed cache, `land` (`:2325`) already ran, so the store holds it too. | No stall, but memory is still corrupt. |
| Q2 | Two `Watch` observers, the first throws | With the default R3 handler, the second observer sees 2 of 2 and the writer sees nothing. **With a rethrowing handler, the second observer sees 0 of 2 and every writer gets the exception.** The comment at `CultCache.cs:2374`, "OnNext does not throw", is false under fail-fast. | With its own observer array and a per-observer catch, the second observer sees 2 of 2 and the writer gets the exception afterwards. |
| Q3 | Writer A's observer blocks on `a`, then B writes `b` | The recorder sees `b(2)` before `a(1)`. There is no cross-thread order, by design since `4562340`. | Sees `a` then `b`, because B waited for A. |
| Q4 | An observer on `x` writes `y` | `y(2)` before `x(1)`: nested, depth-first, and the nested write returns after `y` is published. | `x` then `y`, and the nested write returns before `y` is published. |
| Q5 | `OnUpdate` on Upsert and on Commit | 0 fires, so loads only. `Watch` sees 2. | Unchanged. |
| Q6 | 8 threads × 1000 writes, plus reentrant writes | 67 ms, 2645 of 8800 cross-thread inversions, and 0 writes returned undelivered. | 516 ms, 0 inversions, 0 undelivered. |
| Q7 | Soul's P2 shape: a handler waits 1 s for another thread's write | Observed in time. | Not observed in time. |
| Suites | Caching, Networking and Mesh | (green on main) | Caching fails `ObserverWritingTheSameCacheDoesNotWaitOnOtherDeliveries`, and Networking **hangs** until the 900 s idle kill. Rerun with `--blame-hang-timeout 90s`: 286 passed, then the test host hung and was dumped. The blame sequence file was not extracted, so **the hung test is not named**. It is consistent with, but not proven to be, `SubscriptionDemandHandlerWritingCacheDuringDeliveryDoesNotDeadlock`, whose own comment says "disposal would hang too". |

## Imagination pass 4: Cut 4 re-taken on the landed journal

Cut 3 landed at `ae8576d`. Its Soul left two questions that Cut 4 had to settle before relying on the
journal: (a) what a throwing journal means for the log, and (b) whether a journal needs the change's origin.
Both have one coherent answer. The one fork, Q-P5, was about who hears of a log failure; the operator ruled it A (2026-09-29).

### Probe evidence (pass 4)

- Probe file: `Pub4ProbeTests.cs` (Networking tests). It was run on Yggdrasil from scratch commits:
  - `0d7397d`: main plus the probes;
  - `6ec2f73`: the same, merged with the branch `28248eb`;
  - `376cd74`: main plus probe C.
- The commits survive on the Yggdrasil mirror. The scratch worktree is gone.
- The probes report facts and assert nothing. `FlakyLogStore` is an in-memory `ICultNetShardMutationLogStore`
  that can refuse `Append` or run a hook inside `GetCompactedThrough`.

| # | Probe | main `3bf1c0c` | branch `28248eb` (queue) |
|---|---|---|---|
| A1 | Primary with a durable log. Put 1, Put 2 (its `Append` throws), Put 3. A replica pulls, then retries. | Writer gets `IOException`. The cache holds 1, 2 and 3, but **2 is never published** (2 of 3). Memory log `[1,2,3]`, durable `[1,3]`. The replica pull throws "log has a gap. Expected 2, received 3", and so does **every retry**: the replica is stuck for good. | **Writer sees nothing**: R3 swallowed the drainer's exception. 2 is not published. The replica is stuck in the same way. |
| A2 | The same failure at the tail, then the primary restarts over the same cache and log store. | The replica applied 1. After restart, note 3 is **minted sequence 2**. The replica takes it, reports applied 2, and holds `1,3` against the primary's `1,2,3`: **silent divergence**, a reused sequence. | Same. |
| A3 | A replica whose log `Append` throws once. | The pull throws and the cursor stays at 0. The retry converges (`1,2`), but the memory log is `[1,1,2]`: a duplicate, because memory is written before disk and kept as a `List`. | The pull "succeeds": the exception was swallowed and the cursor went to 2. The durable log is `[2]`, so **entry 1 is lost**, and a chained replica sees a gap. |
| B1 | Primary with a file store. Another writer puts `pulled` into the file, the primary runs `PullAll`, and a replica pulls the log. | `pulled` is published but **not logged**. The replica gets nothing. | Logged. The replica converges on `pulled`. |
| B2 | A store attached after the database exists. | Nothing is logged. | All three hydrated records are logged. |
| S | `CreateShardSnapshotResponse` with a write landing between its document read and its sequence read (hooked in `GetCompactedThrough`). | The snapshot holds `[1]` at log sequence 2. The replica applies it and pulls, and still lacks 2: **divergence**. | Same. |
| C | Compact through 2, restart, then Put 3. | Note 3 is **minted sequence 1**, and `GetLatestMutationLogSequence` returns 1, below the floor of 2. | (not run; the code is unchanged) |

What the probes establish:
- A1, A2, S and C are pre-existing defects on main. Cut 4 inherits them and must not build on them: the
  resync path it relies on for (a) is itself broken by S and C.
- On a replica, the pull loop already retries. The cursor advances only after an entry applies
  (`CultNetDatabase.cs:1038-1083`), so a replica needs no new mechanism. It needs only to keep the exception
  reaching that loop, which the branch's queue broke (A3).
- Loads on a primary must be logged, or replicas diverge (B1).

### (a) A journal that cannot log a committed change: one answer

The facts:
- The log store is a second durable store written after the cache's store. Without an atomic commit across
  the two, the log can lag the store. It must never lead: a replica would then apply a change the primary
  refused.
- So lag is unavoidable, and it has to be detectable and repaired.
- The protocol already has a repair, and nothing else needs to be invented.
  - "History the primary cannot serve" is `compacted_history`: `CultNetDatabaseServer.cs:189-201` answers
    `ResyncRequired`.
  - `CultNetShardReplication.cs:199-214` then snapshot-resyncs.

**Rule: a primary that cannot log a committed change burns that change's sequence and compacts through it.**

- In the database journal, on a primary, for each change that is not `NoLog`:
  1. Mint `S`.
  2. Build the entry: the wire message from the door, or `ToLogEntryMessage`.
  3. `Append` durably, **then** record it in memory. Disk goes first, so memory never serves what disk lacks.
- If step 2 or 3 throws:
  - record nothing;
  - raise the database's in-memory floor for the shard to `S`;
  - try `store.CompactThrough(shard, S)`;
  - still set `_lastWriteSequence` to `S`, because the row was written at `S`;
  - carry on with the rest of the admission;
  - throw `CultNetShardLogException` once at the end of the journal call (Q-P5 A).
- Every replica behind `S` is then told `compacted_history` and resyncs by snapshot. The sequence gap at `S`
  is never served, because the floor covers it.
- This **replaces** the old §4 step 3 claim, "a serialization failure then mints nothing, so the log can never
  hold a gap". Minting nothing hides a replication gap behind a dense sequence. That is exactly A2's silent
  divergence.
- **Replica:** there is no floor. The journal's exception reaches `ApplyShardLogResponseAsync` and the cursor
  does not advance. The next pull re-applies the entry: a fresh instance, so the door matches again, and the
  entry is re-recorded under the primary's sequence. The branch's absent-key direct log follows the same
  disk-first order.
- **Required fixes** that the rule depends on, all in Cut 4:
  - `GetCompactedMutationLogSequence` (`CultNetDatabase.cs:597-602`) returns
    `max(store floor, in-memory floor)`.
  - `InitializeLogSequencesFromStore` (`:1363-1395`) resumes at
    `max(highest retained, store.GetCompactedThrough(shard)) + 1`. This is probe C.
  - `GetLatestMutationLogSequence` (`:607-616`) never reports below the floor.
  - `CreateShardSnapshotResponse` (`:624-670`) reads `GetLatestMutationLogSequence` **before** `SelectAll`, on
    both returns, `:645` and `:668`. The documents then reflect at least that sequence, and replaying later
    entries converges: they are whole-document puts and deletes, applied in order. This is probe S.
  - `RecordMutationLogEntry` (`:1331-1352`) writes disk first. The in-memory log becomes
    `SortedList<long, entry>` per shard, replacing by sequence as the durable store does. This fixes A3's
    duplicate.
- **Residual, recorded rather than forked.** Suppose the store refuses both `Append` and `CompactThrough`, and
  then the process restarts.
  - The durable log does not know about the hole, so A2's tail reuse can recur.
  - Fixing that needs a durable write per mint, a sequence reservation, which doubles log I/O under the gate.
  - The thrown exception names both failures, so the outage is loud.
  - Refusing later writes ("fail closed") is not coherent. The database can refuse only its own doors, while
    bare cache writes are journaled regardless: that would be split authority.

### (b) A pulled or loaded change on a primary: no origin needed

- On a primary the log is the shard's **state history**, whoever wrote the state. B1 shows that leaving loads
  out of the log leaves replicas permanently diverged.
- The only origins that change what is logged are already carried by the door table, keyed by instance
  identity and matched by shape. They are:
  - `Replica` (log the primary's entry);
  - `NoLog` (prediction, snapshot resync);
  - `Put`/`Delete` (the wire message);
  - `Predicts`/`Authoritative`.
- A load or pull has no door, so on a primary it is logged like any unattributed commit. On a replica it logs
  nothing, like any local write. That is what the branch does, and B1 converges under it.
- Cost:
  - B2: a store attached **after** the database logs its hydration.
  - `CultNetHost` opens the cache before building the database (`CultNetLocal.cs:146-152`), so production
    hydration is not journaled.
  - A re-pull admits only the records that changed. In B1 the seed was not re-logged.
- `AddJournal` keeps its signature. If a future consumer (audit, say) needs "load or commit", the fact goes on
  `CultCacheDocumentChange` as an additive property, as `Sequence` did. The cache already knows it: `Admit`'s
  `source != null` at `CultCache.cs:2420`. It never needs a new `AddJournal` shape.
- This is recorded, not forked: no consumer needs it today.

### The two recorded hazards, settled for Cut 4

- **Q-P2 typed-exception hazard.** Cut 4 makes it frequent. Every cache write on a primary with a durable log
  now does log I/O inside its admission.
  - AetheriaEve opts into durable logs (`Aetheria.State/AetheriaStateNode.cs:144`).
  - Its `AetheriaHangarCommandJournal.ValidateAsync` writes an envelope (`Program.cs:6509`) inside the `catch
    (InvalidOperationException)` at `Program.cs:3722`.
  - A log failure surfacing as `InvalidOperationException` would therefore produce a "denied" receipt for a
    committed envelope.
  - Q-P5 A (ruled): the writer gets a typed `CultNetShardLogException` that **does not derive from
    `InvalidOperationException`**.
- **Journal deadlock (the Cut 3 note).** The database journal waits only on `_logGate` and on the log store's
  own lock.
  - Lock order: cache gate, then `_logGate`, then the log store's lock.
  - Invariant: **no code holding `_logGate` calls the cache, and no log store calls the cache or waits on a
    thread that does.**
  - Readers that take `_logGate` read only log state: `GetMutationLog`, `LastWriteSequence`, `CurrentAsOf`.
    Readers already inside a cache read, such as the selection evaluator's `ordinalOf`, nest `_logGate` under
    the cache gate, which is the same order.
  - The store rule goes into the `ICultNetShardMutationLogStore` XML doc (`CultNetShardMutationLogStorage.cs:13-34`),
    because a user store runs under the cache's gate.
  - A log store that writes the cache already throws, through Cut 3's guard. Cut 4 pins it with a test.

## Authority map

- **Owner.** `CultCache`. The hold that mints a Sequence range also:
  1. journals the range under the gate, in Sequence order (new);
  2. publishes the range to every observer, isolated, on the minting writer's
     thread, after it releases the gate and before it returns (main's model,
     now isolated).

  Nothing waits on another call's delivery.
- **Inputs.**
  - The changes returned by `Apply` (`CultCache.cs:2707`), the single mutation
    site.
  - The pre-mint projection of names and indexes (Cut 1).
  - The journal list, the observer array and `OnUpdate`.
- **Outputs.**
  - Journal calls: one per admission, under the gate, in Sequence order.
  - `Watch` / `WatchRecord` notifications that carry `Sequence`.
  - `OnUpdate` for loads.
  - Observer and journal exceptions, rethrown to the writer.
- **Derived state.**
  - `Sequence` is order as data.
  - CultNet's primary log sequence is minted in the journal, so log order is
    cache order.
  - A replica's log sequence is copied from the primary entry.
  - The database change kind (`Predicted`, `Reconciled`, `Added`, `Updated`,
    `Removed`) is decided in the journal.
  - `CultNetDatabaseChange.Sequence` (new) is copied from the cache change.
  - Wire projections drop stale by it (Cut 6).
- **Forbidden writers.**
  - Nothing re-sequences or schedules cache changes. The branch's `_parked`,
    `_ready`, `_nextSequence`, `_draining`, `PendingChange` and the drain body
    of `OnCacheChange` die, and so do the turnstile, `bcae483` and the
    prototype.
  - No R3 `Subject` fans out cache changes (Q2).
  - `Apply` and `Index` run no user code.
  - Doors never read or write prediction state: the branch's `HasPrediction`
    and `_predictionGate` die.
  - The database's Watch observer never logs; only the journal logs.
  - A journal never enters the cache.
- **Shared paths.** Every path goes `Held` → `Admit` → `Resolve` → **Project**
  → `land` → `Apply` → **journal** → leave gate → **publish**:
  - loads: `AddBackingStore` `:1909`, `Loaded` `:1940`, `PullAll*` `:1959` and
    `:3052`;
  - `Commit` / `TryCommit` (`Land`, `:2272`);
  - `UpsertAsync`, `UpsertVariantAsync`, `FlattenAsync` and `Remove`
    (`:2004-2056`, `Write` `:2252`);
  - a variant dependent's re-resolution (`VariantPlan.Dependents`, in the same
    `Apply`);
  - direct calls on an attached store (`:2924`);
  - `Dispose` (`:2142`);
  - the database doors, replica apply and snapshot resync, which only write
    the cache and hand context to the journal.

  The single exception is the replica's absent-key delete, which has no cache
  change. It logs directly (§4).
- **Deletion line.**
  - In the cache: `Subject<Change> _changes` (`:1809`), the `_changes.OnNext`
    call and its false comment (`:2374-2375`), `_changes.Dispose()`
    (`:2154`), and the accessor calls in `Index` (`:2798-2804`).
  - In the database, on the salvaged branch: the reorder buffer and drainer,
    `_predictionGate` and `HasPrediction`, the logging in `Deliver`, and the
    `Subject<object> _changes` fan-out.

## The design, question by question

### 1. Validation before minting: a refused change mints nothing

- **Where.** Add **Project** to `Admit` (`:2313`), between `Resolve` (`:2324`)
  and `land` (`:2325`).
- **What.** Over `plan.Admitted ∪ plan.Dependents`, it evaluates once per
  record: `NameAccessor(document)` and every `IndexAccessors[alias](document)`.
  It keeps `(Name, (Alias, Value)[])` per key on the plan.
- **A throwing getter refuses the whole admission.** It raises an
  `InvalidOperationException` that names the key, the member and the unwrapped
  message; today the caller sees a bare `TargetInvocationException`. The
  refusal comes before `land`, so the admission writes no store, touches no
  memory and mints nothing.
- **Readers take the projection, not the document.**
  - `Index` (`:2793-2806`).
  - `RefuseVariantIndexSharing` (`:2659-2660`).
  - The variant-name rule in `ResolveOne` (`:2616`). Its ancestor read at
    `:2621` reads held documents, pre-land, and may stay.

  `Resolve` currently runs the refusal rules before any projection exists.
  Hands makes Project the first pass over the resolved plan and moves both
  rules after it, so rules read the projection and only the projection reads
  documents.
- **Apply becomes user-code-free.** It then does only dictionary and
  `ConditionalWeakTable` work on precomputed data.
- **Loads refuse too.** A store whose record's getter throws fails to open,
  loudly. That is the rule "hydration failure on open is loud" (composition doc
  `:88-92`). Today it half-indexes silently.

### 2. Observer isolation

- **Replace the `Subject<Change>` with the cache's own observer array**,
  copy-on-write under a small lock.
  - `Watch<T>` (`:1887`) becomes `Observable.Create<Change>(register)` followed
    by the existing `Where` and `Select`.
  - `Dispose` calls `OnCompleted` on each observer, which is what
    `Subject.Dispose` did.
- **Publish loops change → observer, and wraps each `OnNext` in its own
  try/catch.**
  - R3's `Observer<T>` already catches handler exceptions (`OnErrorResume`, then
    the unhandled handler). So an exception reaches the cache only when fail-fast
    was asked for: a rethrowing `ObservableSystem` handler, or a custom observer.
  - Escaped exceptions join the `OnUpdate` failures. They are rethrown to the
    writer after every observer has received every change of the hold. That is
    the existing `OnUpdate` policy (composition doc `:77-82`).
  - The observer stays subscribed (**Q-P2**). The commit stands either way.
- **Prototype.** At `79f4639` (Q2) this took about 15 lines. Hands keeps that
  part and drops the turnstile around it.

### 3. Delivery thread, order and reentrancy

- **Unchanged from main, on purpose.** Each outermost hold publishes exactly its
  own changes, on its thread, after it leaves the gate and before it returns
  (composition doc `:59-65`, `Held` `:2339`).
  - A write from inside a handler is its own outermost hold. It delivers
    depth-first before returning, as Q4 shows on main.
  - There is no cross-thread order. Every change carries `Sequence`, and
    latest-state consumers drop stale changes by it (doc `:68-73`, and the Mesh
    mirrors at `CultMesh.cs:1534-1540`).
- **Why this is not a regression of the ruling.** The ruling asks for
  exactly-once, not for a total delivery order.
  - Exactly-once in the cache comes from the single mint site plus isolation
    plus Cut 1 (§1-2).
  - Order is needed by exactly one consumer, the shard log. It gets that order
    from the journal (§4), not from delivery.
- **The journal is the new mechanism.**
  - Signature: `IDisposable CultCache.AddJournal(Action<IReadOnlyList<CultCacheDocumentChange<object>>>)`.
    Its name and shape are **Q-P3**.
  - It is called in `Admit` right after `Apply` (`:2328-2329`), under the gate,
    once per admission, with that admission's changes in Sequence order.
    Journals run in registration order.
  - A journal that enters the cache, meaning any hold on this cache while a
    journal runs, throws `InvalidOperationException`. It is guarded by a flag
    checked in `Held`'s `Monitor.IsEntered` branch (`:2341`). This is the
    store rule ("a store call must not wait for a write to its own cache"),
    enforced.
  - Journal exceptions follow the observer policy: the admission stands, it is
    still published, and the exception is rethrown to the writer after
    publication.
  - Adding or removing a journal takes the gate, so registration is atomic with
    respect to admissions.
- **Lock order.** Cache gate, then journal-internal leaf locks (the database's
  `_logGate`). Observers still run outside the gate, so the `bcae483` cycle
  (an observer needing `_lifecycleGate` while a gate holder writes the cache)
  cannot form. No journal takes `_lifecycleGate`, and no writer waits on a
  delivery.
- **Soul's P2 passes.** A handler waiting on another thread's write sees it,
  because nothing orders deliveries (Q7 on main).

### 4. What CultNetDatabase keeps

The database is rebuilt on the journal. It has no queue.

- **Journal (under the cache gate, per admitted change, in Sequence order):**
  1. `TakeDoor(change)`, with the shape rule salvaged from the branch
     (`:1219-1234`). A put door matches a change whose new document is its
     instance and whose previous document is not. A removal door matches a
     change whose previous document is its instance and which has no new one.
     - The consumption now happens inside the door's own hold, before any
       observer runs. So an observer's later write to the door's key or
       instance can never wear the context (the `f3343c8` defect), and
       unconditional `Dispose`-removal stays correct.
  2. Predictions. A `Predicts` door adds the prediction. On an `Authoritative`
     door (`ApplyPutAsync`, replica put), if a prediction exists for the key,
     the kind is `Reconciled` and the prediction is removed.
     - The gate serializes this, so `_predictionGate` and `HasPrediction` are
       deleted (F5).
     - Soul's P6 cannot happen: the prediction is recorded before
       `PutPredictedAsync` returns.
  3. Primacy.
     - On a shard primary, where the door is not `NoLog`: mint, build, append disk-first and record, with a failed
       append burning the sequence and compacting through it. See "(a) A journal that cannot log a committed
       change" in Imagination pass 4; the earlier claim that a serialization failure mints nothing is withdrawn.
     - On a replica, record the primary's entry from the `Replica` door,
       under its own sequence.
     - For a local write on a replica, log nothing.
  4. Stash `(cache Sequence → CultNetDatabaseChange)` under `_logGate`, for the
     publisher.
- **Watch observer (after the gate, on the writer's thread, before it
  returns):** it removes the stash entry for `change.Sequence` and publishes it.
  - A change with no stash was admitted before the database existed and is
    ignored.
  - Register the observer first and the journal second, in the constructor, so
    a stash can never be orphaned.
- **Log-before-publish** is structural: the journal runs before the gate opens.
- **The replica's absent-key delete (F6)** logs directly under `_logGate`. It
  is in order because every earlier entry of the same pull loop was journaled
  inside its own hold, before that `UpsertAsync`/`Remove` returned. This holds
  even when a pull runs inside a handler, since nested holds journal
  synchronously.
- **The in-memory log is keyed and ordered by sequence.** It becomes a
  `SortedList<long, entry>` per shard, to match the durable store
  (`CultNetShardMutationLogStorage.cs:55-64, 75-89`: that store replaces by
  sequence and orders by sequence).
- **Kept from the branch:**
  - the door table keyed by instance identity, and the shape rule;
  - "only a primary mints; replicas publish";
  - log-before-publish;
  - the snapshot resync "logs nothing" rule. Its removal door (branch `:733`)
    must set `Removal = true`, or the shape rule never matches it.
- **Database fan-out isolation.** The database's `Subject<object> _changes`
  (main `:487`) has the Q2 defect one layer up: the subscription server's
  per-peer handlers sit on it. Replace it with the same isolated
  observer-array pattern as §2 (about 20 lines). Its exceptions reach the
  writer through the cache's own per-observer catch.
- **`CultNetDatabaseChange<T>` gains `long Sequence`**, the cache change's
  Sequence, as an optional constructor argument, exactly as
  `CultCacheDocumentChange` did (composition doc `:57-58`). Without it,
  in-process latest-state consumers cannot drop stale changes.
- **Gone:** F3 (no deferred delivery), F4 (no drainer), F7 (no queue to
  strand) and F8 (no guard).

### 5. Backpressure and memory bounds

- **No buffer exists.** A writer delivers its own changes, and they are held
  only in its `mine` list for the length of its own call.
- **The journal stash** holds at most the changes of holds between their
  journal and their publication. That is one hold per writing thread, and each
  entry is removed by the same thread that added it.
- **A slow observer slows only the writer whose change it is handling.** That
  is main's behaviour. It does not slow other writers, and it does not grow
  memory. This replaces the branch's unbounded drainer backlog (F4, Soul P5).
- **The journal runs under the gate, so a slow journal slows every writer.**
  - The database journal does in-memory work plus one durable
    `mutationLogStore.Append` per logged change. `Append` rewrites the shard
    file (`CultNetShardMutationLogStorage.cs:83-89`).
  - That is I/O under the gate, which the locking doctrine already allows for
    stores ("A store doing I/O on behalf of its cache ... holds the gate",
    composition doc `:98-101`). It is still a new cost, so Cut 4's
    verification measures commit latency with a file log store.
  - The whole-file rewrite is a pre-existing monolith. It is recorded as a
    follow-up, not fixed here.

### 6. Cross-runtime impact

The Eyes survey was run at `65311c9`.

- **`cultcache-rs`, `cultcache-ts` and `cultcache-py` have no publication
  surface:** no watch, no listener and no Sequence.
  - Rust: `lib.rs:1970-1975, 2213-2420`.
  - TypeScript: `cult-cache.ts:164-181`, "Observer delivery is not scheduled
    here".
  - Python: `cache.py:30-45`, which refuses reentrant mutation.
- **`cultnet-rs`, `cultnet-ts` and `cultnet-py` have no CultNetDatabase
  equivalent and no cache watcher.** The only server-side shard log is the
  Python interop test peer (`interop_peer.py:565, 583`), which mints
  `len(log)+1` under its own lock, inline with the write. That is already the
  journal's shape.
- **Wire parity is unaffected.**
  - Cache `Sequence` never goes on the wire (composition doc `:47-49`).
  - `CultNetShardLogEntryMessage` does not change.
  - `CultNetDatabaseChangeRawMessage` (`CultNetSchemaMessages.cs:1016`) carries
    no sequence, and this map adds none: stale protection for wire
    subscriptions is server-side (Cut 6).
  - No vector in `tests/vectors/` encodes publication order.
- **No other runtime must change.**
- **Follow-up.** `cultmesh-py`'s `CultMeshDatabase` watch (`node.py:421-505,
  1059-1065`) has F2 (a throwing callback skips the rest and reaches the
  writer), and it has no Sequence. When Python grows cache publication, it
  takes this contract.

## Cuts

Every cut is on CultLib, branch `hands/cultcache-publication` from `main`
`65311c9`, in a worktree Self creates. Cuts 1-3 touch only the cache and land
first. Cuts 4-6 depend on Cut 3. Heavy verification runs on Yggdrasil.

**The negative checks that stay green in every cut** are the `4562340` tests:

- `ObserverWritingTheSameCacheDoesNotWaitOnOtherDeliveries`;
- `SubscriptionDemandHandlerWritingCacheDuringDeliveryDoesNotDeadlock`;
- `SequenceIncreasesInAdmissionOrder`;
- `WatchRecordIgnoresStaleSequence`.

They are the proof that no scheduler has come back.

### Cut 1. Project before mint (cache, behaviour)

- **First.** Commit Q1 as a failing test named for the rule.
- **Deletes first.**
  - Accessor calls in `Index` (`:2798-2804`).
  - Direct accessor reads in `RefuseVariantIndexSharing` (`:2659-2660`) and
    `ResolveOne` (`:2616`).
- **Adds.**
  - `Project` in `Admit` (`:2324-2325`).
  - A per-key projection on `VariantPlan` (`:2456`).
  - The early returns in `Resolve` (`:2477-2478`, `:2530-2534`) still pass
    through `Project`, because it is called from `Admit`.
- **Authority.** Project owns "this admission's name and index values", and
  the rules and `Index` read them. `Apply` runs no user code.
- **Verification.**
  - Tests:
    - **A refused getter mints nothing and changes nothing.** `GetWithSequence`
      is unchanged, both keys are absent, the name lookup misses, nothing is
      published, and the next write is previous+1.
    - **Store-backed batch:** the store file is byte-identical, and a reopen
      holds neither key.
    - **A load whose getter throws** refuses the open and leaves the file
      byte-identical.
    - **Once per landed record:** a counting getter runs exactly once per
      landed record, including a variant dependent re-resolved by a base edit.
    - **The refusal names** the key and the member.
    - Soul P4.
  - Negative grep: `rg -n "NameAccessor\?\.Invoke|IndexAccessors" src/GameCult.Caching/CultCache.cs`
    has no hit in `Apply`, `Index` or `Unindex`. `:315` is store-side and
    legitimate.
  - Stryker.NET on the diff.
- **Ledger.** About −15 and +45.

### Cut 2. Observer isolation (cache, behaviour)

- **Deletes first.**
  - `Subject<Change> _changes` (`:1809`).
  - `_changes.OnNext` and the false comment (`:2374-2375`).
  - `_changes.Dispose()` (`:2154`).
- **Adds.**
  - The observer array and `Observable.Create` in `Watch<T>` (`:1887-1897`).
  - The per-observer catch in `Publish` (`:2369`), with escapes joining
    `failures`.
  - `OnCompleted` on `Dispose`.
- **Docs.** Composition doc `:77-82`: escaped observer exceptions follow the
  `OnUpdate` policy.
- **Verification.**
  - Tests:
    - **Q2 with a rethrowing handler:** the second observer receives every
      change. The writer gets the escaped exception after delivery, and the
      thrower is still subscribed (per Q-P2).
    - **Subscription order:** subscribing and unsubscribing during a delivery
      leaves every other observer's stream complete.
    - **Dispose** completes observers.
    - Soul P3.
  - The four `4562340` tests stay green.
  - Stryker on the diff, especially removing the catch and breaking out of
    the loop on the first failure.
- **Ledger.** About −5 and +30.

### Cut 3. The journal (cache, behaviour)

- **Adds.**
  - `AddJournal` (Q-P3), with registration under the gate.
  - The invocation in `Admit` after `Apply` (`:2328`).
  - The no-reentry guard in `Held` (`:2341`).
  - Exceptions routed like observer failures.
- **Docs.** A composition-doc paragraph in "Store composition" and in
  "Locking": journals run under the gate, in Sequence order, and must not
  enter the cache or wait for anything that needs it.
- **Verification.**
  - Tests:
    - **Order:** 8 threads × 1000 writes. The journal sees contiguous,
      strictly increasing Sequences, and each call's changes arrive together.
    - **Timing:** the journal runs before any observer of the same change, and
      after `Apply` (`Get` inside the journal sees the new document).
    - **Reentry:** a journal that writes the cache throws, and the admission
      stands.
    - **Refusal:** a refused admission journals nothing.
    - **Dependents:** a variant dependent is journaled in the same call as its
      base edit.
    - **Failure:** a throwing journal still lets the change publish, and the
      writer gets the exception.
  - The `4562340` tests stay green.
  - Stryker on the diff.
- **Ledger.** About +50.

### Cut 4. Salvage the branch and rebuild CultNetDatabase on the journal (networking)

- **4a (history).** `git merge origin/hands/cultnet-publish-commits` into the cut branch from main
  `3bf1c0c`.
  - It is clean: the probe merge `6ec2f73` auto-merged `NetworkingTests.cs` and the composition doc, then
    built and ran.
  - The merge brings the tests. The queue dies in 4b.
  - Self may squash-salvage instead, with the same content.
- **4b (subtraction first).** Delete at branch `28248eb` `CultNetDatabase.cs`:
  - `_parked`, `_ready`, `_nextSequence` and `_draining` (`:502-505`);
  - `_predictionGate` (`:506-507`);
  - the Watch subscription and handshake (`:529-531`);
  - `OnCacheChange` (`:1080-1135`);
  - `PendingChange` (`:1265-1275`);
  - `HasPrediction` (`:1209-1213`) and its calls (`:916`, `:1574`);
  - the logging and prediction bookkeeping in `Deliver` (`:1137-1201`).

  Main's `PublishCacheUpdate` / `OnUpdate +=` (`:505`, `:1157`, `:1161-1191`) is already gone on the branch.
  Hands confirms it is absent after the merge.
- **4c (the journal).** Register the Watch observer first and the journal second (`AddJournal`), in the
  constructor. Per admission, under the cache gate, per change in Sequence order:
  1. `TakeDoor(change)`, keeping the branch's shape rule (`:1219-1234`).
  2. Resolve the shard and kind. Predictions: a `Predicts` door adds its key. An `Authoritative` door
     (`ApplyPutAsync`, replica put) holding a prediction for the key makes the kind `Reconciled` and removes
     the key.
  3. Log, per §(a):
     - a replica door records the primary's entry, disk first;
     - a primary change that is not `NoLog` mints, builds, appends and records, with failure going to the
       floor;
     - anything else logs nothing.
  4. Stash `(cache Sequence → CultNetDatabaseChange)` under `_logGate`.
  5. If step 3 failed, throw once at the end of the call, after all changes are handled (Q-P5).
- **4c, publication.** The Watch observer removes the stash entry for `change.Sequence` and publishes it. A
  missing stash means the change was admitted before this database existed, so it is ignored. `Dispose`
  removes the journal and the observer and clears the stash.
- **4d (the §(a) fixes).**
  - The floor: `GetCompactedMutationLogSequence`, `GetLatestMutationLogSequence`, and resume in
    `InitializeLogSequencesFromStore`.
  - Disk-first `RecordMutationLogEntry`.
  - A `SortedList` per shard.
  - The snapshot reads its sequence first (`:660` moves below `:668`'s read).
  - `_logGate` around `_lastWriteSequence`/`_nextLogSequences` readers (`:1302`, `:1314`, `:1325`).
- **Door changes.**
  - Flags: `Predicts` and `Authoritative`.
  - The snapshot removal door (branch `:733`) sets `Removal = true`.
  - The replica absent-key delete logs directly and publishes nothing (branch `:1612-1621`).
  - Doors neither log nor publish. They register context, write the cache, and dispose the registration.
- **Verification.**
  - Every salvaged test passes: `CultNetPublishesCommitsTests` (13) and `CultNetPublicationOrderTests` (S1,
    S1b, S2, S3, S4, S6, S7, log-before-publish, in-place re-upsert).
  - Soul pass 3's P1-P7 are committed and passing.
  - The pass 4 probes become tests named for the rule:
    - **A1:** a refused primary `Append` publishes the change. The writer gets `CultNetShardLogException`,
      which names the shard and `S`. A replica pull answers `compacted_history`, and a snapshot resync
      converges.
    - **A2:** the same at the tail with a restart: the replica converges after resync, and no sequence is
      reused. The store's `CompactThrough` succeeded.
    - **A3:** a refused replica `Append` leaves the cursor unchanged. The retry converges, and the memory log
      holds `[1,2]`.
    - **B1:** a pull on a primary is logged, and the replica converges.
    - **S:** a snapshot racing a write converges.
    - **C:** after compacting through 2 and restarting, the next mint is 3.
  - New tests:
    - a reentrant `database.PutAsync` from inside a database handler is logged with its context and published
      once;
    - a reentrant `ApplyShardLogResponseAsync` from inside a handler keeps the replica log in primary order;
    - a log store whose `Append` writes the cache fails with Cut 3's reentry guard, wrapped, and the admission
      stands.
  - Commit latency with `CultNetFileShardMutationLogStore`, main against Cut 4. Also measure a `CultMesh` node
    with `EnableDurableShardLogs`, where every bookkeeping write is now logged. Report it; do not gate on it.
  - Negative grep: `rg -n "_parked|_ready|_draining|_nextSequence|_predictionGate|HasPrediction|PublishCacheUpdate" src/GameCult.Networking`
    returns nothing.
  - Review check: no `_cache.` call inside a `lock (_logGate)` block.
  - The `4562340` tests stay green.
  - Test runs use `--blame-hang-timeout 2m`.
  - Stryker on the diff: dropping the floor raise, memory-before-disk, and the snapshot read order must each
    kill a test.
- **Docs.**
  - Composition doc `:50-53`: replace the "streams derive from `OnUpdate`" sentence. CultNet derives from
    `Watch` plus one journal, and the log is written in cache order.
  - A new paragraph: a primary that cannot log a change compacts through it.
  - The log-store rule under Locking.
- **Blockers.** None. Q-P5 is ruled A.
- **Ledger.** Against the branch, about −130 and +95. The extra over the old estimate is the floor, the
  disk-first order, the `SortedList`, the snapshot order, and the exception type.

### Cut 5. Database fan-out isolation and Sequence (networking, behaviour)

- **Deletes first.** The database's `Subject<object> _changes` (main `:485`, branch `:487`) and its
  `Dispose` (`:1158`).
- **Adds.**
  - The isolated observer array, the same pattern as the cache's `Register`/`Publish`
    (`CultCache.cs:1929-1944`, `:2470-2503`), behind `Watch<T>`, `WatchAllChanges` and the rest (`:1089-1145`).
  - `CultNetDatabaseChange<T>.Sequence` (`:319-371`), as an optional constructor argument. The journal's stash
    sets it from the cache change. A change with no cache change behind it carries 0.
- **Docs.** The binary-signature note, as the composition doc gave for `CultCacheDocumentChange` (`:57-58`).
- **Verification.**
  - With a rethrowing handler and one throwing database subscriber, the other subscriber and a live
    subscription-server peer receive every change. The writer gets the exception after delivery (Q-P2).
  - The disposed-database test.
  - `Sequence` equals the cache change's.
- **Blockers.** Cut 4.
- **Ledger.** About −10 and +35.

### Cut 6. Stale protection on wire projections (networking, behaviour; Q-P4 A)

- **The rule.** A projection applies a change to a key only if its `Sequence` is above what the projection
  last applied for that key. A snapshot or reconcile applies everything at or below the cache sequence it read
  **before** reading documents.
  - That read is `Cache.GetWithSequence(<any key>).Sequence`, the branch's idiom (branch `:531`). No new API
    is needed.
- **Subscription server** (`CultNetDatabaseSubscriptionServer.cs`):
  - `SubscriptionProjectionState` (`:630`) gains `long Floor` and `Dictionary<string, long> Applied`.
  - The single-row fast path in `Watch` (`:296-340`) reads the change's `Sequence` (reflection, like `Key`).
    Under `_lifecycleGate` it drops the change if `Sequence <= max(Floor, Applied[key])`. Otherwise it applies
    the change and records it. Removals are included.
  - `Reconcile` (`:347`) and the subscribe snapshot (`:168-181`) read `seq0` before `CreateProjectedSnapshot`
    (`:402`), set `Floor = seq0`, and clear `Applied`.
    - This is sound: every change applied earlier was applied under the same gate, after its admission, so
      its `Sequence` is at most `seq0`.
  - Hop and unauthorized paths already reconcile from current state, and they set the floor too.
- **`CultNetDatabaseServer.CreateSubscription`** (`:501-513`):
  - A per-subscription `(Floor, Applied)` under a per-subscription lock, held across check, record and send.
  - `HandleSubscribeAsync`'s snapshot (`:362-369`) takes `seq0` first and sets the floor.
- **Verification.**
  - Two threads write one key, with a blocking cache observer that forces the inverted delivery (Q3 shape).
    On both servers, the peer's last state for the key equals the cache's.
  - A removal overtaken by an older upsert leaves the key absent at the peer.
  - The negative check: without the drop, both tests fail.
- **Blockers.** Cut 5, which provides `Sequence` on database changes.
- **Ledger.** About +45.

### Salvage from `hands/cultnet-publish-commits` (revised)

- **Keep:**
  - both test files;
  - the `NetworkingTests.cs` edits;
  - the door table and the shape rule;
  - "replicas never mint";
  - log-before-publish;
  - "the snapshot logs nothing";
  - "loads and pulls on a primary are logged" (B1);
  - the composition-doc paragraph, rewritten to name the journal.
- **Drop:**
  - the reorder buffer and the drainer;
  - `_predictionGate`;
  - logging from the observer;
  - the swallowed exception path (A1 and A3 on the branch).

## Subtraction ledger (estimate)

| Cut | Removed | Added | Other |
|---|---|---|---|
| 1 | ~15 | ~45 | none |
| 2 | ~5 | ~30 | the cache no longer uses R3 `Subject` |
| 3 | 0 | ~50 | one public method (Q-P3) |
| 4 | ~130 vs branch | ~95 | the merge brings +1109/-243, of which about 780 are tests; the floor, disk-first order, `SortedList`, snapshot order and `CultNetShardLogException` are the extra over the first estimate |
| 5 | ~10 | ~35 | the database `Subject` goes, and one public property |
| 6 | 0 | ~45 | none |

- **Source, net against main:** about +245.
- **Source against the branch:** no scheduler anywhere.
- The budget is pressure, not a metric.

## Operator questions

- Q-P1..Q-P4 are ruled (see Rulings). Their option analysis is in the map's git history.

- **Q-P5 A (ruled 2026-09-29): a primary that cannot log a committed change throws a typed
  `CultNetShardLogException` naming the shard and the burned sequence, after publication, not derived from
  `InvalidOperationException`.**
  - The commit stands, the change is published, and replicas behind it snapshot-resync (Imagination pass 4 (a)).
  - The writer hears of it. AetheriaEve `Program.cs:3722` cannot mistake it for a denial.

Recorded, not forked:
- (b): no origin on the journal. Loads and pulls are logged on primaries.
- (a)'s restart residual when the store refuses both writes.
- The replica keeps retry-by-cursor.
- A throwing getter refuses loads too.
- Reentrant writes keep main's depth-first delivery. Order is data.
- Journal I/O under the gate follows the store precedent.

## Follow-ups outside this campaign

- **`cultmesh-py` watch.** It has no isolation and no Sequence
  (`node.py:1059-1065`).
- **Durable log whole-file rewrite.** `CultNetShardMutationLogStorage.cs:83-89`
  rewrites the whole file per `Append`. It is a monolith, and under the gate
  its cost is now every writer's.
- **Mesh database-backed handles.** `CultMesh.cs:2362` and `:3396` can drop
  stale changes once `CultNetDatabaseChange.Sequence` exists.
- **Skill scar.** The Eureka SKILL.md already says "a ticket system that
  serialized delivery was built, deadlocked, and was deleted". The Soul pass 3
  direction re-proposed it anyway. The Soul brief template should tell Soul to
  check `git log -S` for a proposed mechanism before recommending it.
- **A chained replica after a snapshot.** A replica that snapshot-resyncs has no log entries below the snapshot
  sequence. It should raise its own floor to that sequence, so a replica chained on it resyncs instead of seeing
  a gap (`ApplyShardSnapshotResponseAsync` `:675-736`). It predates this campaign.
- **AetheriaEve `Program.cs:3722`.** It reads a committed write's `InvalidOperationException` as a denial. Under
  Q-P5 A, Cut 4's log failures cannot reach it. Observer and `OnUpdate` exceptions still can, so the Cuts 1-2
  recording stands.- **Cut 4 Soul, recorded 2026-09-30 (Self), not fixed in Cut 4:**
  - **File log store is not crash-safe (hard prerequisite).** `CultNetFileShardMutationLogStore.Append` and
    `CompactThrough` use `File.WriteAllBytes` (truncate, rewrite, no temp+rename, no fsync). A failure mid-write
    truncates the log; every later append burns a sequence and a restart cannot construct the database. It also
    rewrites the whole file per append, which Cut 4 now does under the cache gate (2.2 ms per write at 100
    entries, 33 ms at 950, a 2.49 s stall). An atomic, append-only store must land before any live daemon
    enables durable shard logs. Today only AetheriaEve's offline tools do; the live Aetheria daemon passes
    `enableDurableShardLogs: false`.
  - **A slow log append blocks cache readers** (`CultCache.Get` takes the gate). Accepted with a note in the
    store's XML doc.
  - **Crash between the store write and the log append** leaves the log lagging the store, and nothing detects
    it; a restarted primary reuses that sequence (A2's divergence reached by a plain kill). The spec called this
    lag detectable; for a crash it is not.
