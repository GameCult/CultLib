# CultCache publication: the cache owns "published once", and order stays data

Status: cut map, Imagination pass 1, 2026-09-29. It is not committed yet. Self
commits it to `main` as `docs/cultcache-publication-cut.md`. The ends are the
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
     - On a shard primary, where the door is not `NoLog` (prediction, snapshot):
       build the log entry, including the wire message from a `Put`/`Delete`
       door or `ToLogEntryMessage`, and **only then** mint the per-shard
       sequence and append it to memory and to the durable store. A
       serialization failure then mints nothing, so the log can never hold a
       gap.
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

- **4a (history).** `git merge origin/hands/cultnet-publish-commits` into the
  cut branch. It is clean. The merge carries the tests; the queue dies in 4b.
  Self may choose a squash-salvage instead, with the same content.
- **4b (subtraction, then behaviour).**
  - **Deletes first**, at branch `28248eb` `CultNetDatabase.cs`:
    - `_parked`, `_ready`, `_nextSequence` and `_draining` (`:502-505`);
    - the handshake at `:530`;
    - `OnCacheChange`'s queue body (`:1083-1135`);
    - `PendingChange` (`:1265-1275`);
    - `_predictionGate` and `HasPrediction` (`:506-507`, `:1209-1213`) and
      their call sites (`:916`, `:1574`);
    - the logging in `Deliver` (`:1168-1201`).
  - **Moves.** Logging, predictions and door consumption move into the journal
    (§4). `Deliver` becomes the publisher that reads the stash.
  - **Changes.**
    - Door flags: `Predicts` and `Authoritative`.
    - The snapshot removal door sets `Removal`.
    - The in-memory log becomes a `SortedList`.
  - **Keeps.** Door identity and the shape rule, primacy, `_logGate` for
    readers, and the absent-key delete's direct log.
- **Verification.**
  - Every salvaged test passes:
    - `CultNetPublishesCommitsTests` (13);
    - `CultNetPublicationOrderTests`: S1, S1b, S2, S3, S4, S6, S7,
      log-before-publish, and in-place re-upsert.
  - Soul's P1-P7 from `SoulPub3Tests.cs` are committed, all passing. P2 passes
    because nothing orders delivery.
  - New tests:
    - a reentrant `database.PutAsync` from inside a database handler is logged
      with its context and published once;
    - a reentrant `ApplyShardLogResponseAsync` from inside a handler keeps the
      replica log in primary order;
    - a primary serialization failure mints no log sequence.
  - Commit latency with `CultNetFileShardMutationLogStore`, main against Cut 4.
    It is reported, not gated.
  - Negative grep: `rg -n "_parked|_ready|_draining|_nextSequence|_predictionGate|HasPrediction" src/GameCult.Networking`
    returns nothing.
  - The `4562340` tests stay green.
  - Stryker on the diff.
- **Ledger.** Against the branch, about −120 and +60.

### Cut 5. Database fan-out isolation and Sequence (networking, behaviour)

- **Deletes first.** The database's `Subject<object> _changes` (main `:487`).
- **Adds.**
  - The isolated observer array.
  - `CultNetDatabaseChange<T>.Sequence` (main `:319-371`), set by the journal.
- **Verification.**
  - With a rethrowing handler and one throwing database subscriber, the other
    subscriber and a live subscription-server peer receive every change.
  - The disposed-database test.
  - `Sequence` equals the cache change's.
- **Ledger.** About −10 and +30.

### Cut 6. Stale protection on wire projections (networking, behaviour)

This is a separate cut that closes a pre-existing hole. Composition doc
`:50-53` says the CultNet streams "have no ordering guarantee and no stale
protection", and the branch's total order was reaching for this.

- **The rule.** A projection sends a record only if the change's `Sequence` is
  above the Sequence it last applied for that key.
  - The subscription server applies it under `_lifecycleGate`, keyed in
    `SubscriptionProjectionState` next to `DeliveredBySourceRecordKey`
    (`CultNetDatabaseSubscriptionServer.cs:446`).
  - `CultNetDatabaseServer.CreateSubscription` (`:501-512`) applies it under a
    per-subscription lock.
  - `Reconcile` (`:347`) stamps each key it reads with a per-key atomic
    `GetWithSequence`.
- **Verification.** Two threads write one key with a blocking observer that
  forces the inverted delivery (Q3 shape). The peer's last state for the key
  equals the cache's, on both servers.
- **Operator.** Q-P4 asks whether this cut belongs here or is recorded.
- **Ledger.** About +40.

### Salvage from `hands/cultnet-publish-commits`

- **Keep:**
  - both new test files;
  - the `NetworkingTests.cs` edits (`:1528`, `:2257`);
  - the door-shape rule;
  - "replicas never mint";
  - log-before-publish;
  - "the snapshot logs nothing";
  - the rewritten composition-doc paragraph, rewritten again by Cut 3 to name
    the journal as the log's order source.
- **Commit** Soul pass 3's P1-P7: P3 in Cut 2, P4 in Cut 1, and the rest in
  Cut 4.
- **Drop:** the reorder buffer, the drainer, `_predictionGate`, and logging
  from the observer.

## Subtraction ledger (estimate)

| Cut | Removed | Added | Other |
|---|---|---|---|
| 1 | ~15 | ~45 | none |
| 2 | ~5 | ~30 | the cache no longer uses R3 `Subject` |
| 3 | 0 | ~50 | one public method (Q-P3) |
| 4 | ~120 vs branch | ~60 | the merge brings +1109/−243, of which about 780 are tests |
| 5 | ~10 | ~30 | the database `Subject` goes, and one public property |
| 6 | 0 | ~40 | none |

- **Source, net against main:** about +200.
- **Source against the branch:** about −60, and no scheduler anywhere.
- The budget is pressure, not a metric.

## Operator questions

- **Q-P1. Order.**
  - **A. Order as data** (the `4562340` ruling, kept).
    - Every change is published exactly once, on its writer's thread, before
      the write returns, and carries `Sequence`.
    - There is no cross-thread delivery order.
    - The shard log is in cache order because the journal writes it under the
      gate.
    - Wire projections drop stale changes by Sequence (Cut 6).
  - **B. Totally ordered delivery in the cache** (a turnstile, which is Soul's
    stated direction). It was built as `bcae483`, deleted for deadlock as
    `4562340`, and reproduced by this pass: the networking suite hangs.
    Making it safe needs a rule that nobody holding a lock an observer takes
    may write the cache, and the subscription server itself breaks that rule.
  - **C. A dedicated delivery thread or drainer** (the branch). It was refuted
    by Soul pass 3: F3 and F4.
  - **Recommended: A.** Only A delivers exactly-once, before-return and
    no-deadlock together. The one consumer that needs order gets it where order
    is decided.
- **Q-P2. An observer whose exception escapes** (a fail-fast R3 handler, or a
  custom observer).
  - **A.** Rethrow it to the writer after every observer has run, and keep the
    observer subscribed.
  - **B.** Report it and unsubscribe.
  - **C.** Route it to R3's unhandled handler only.
  - **Recommended: A.**
    - It matches the `OnUpdate` policy and R3's `OnErrorResume`.
    - An escape only happens when the owner asked for fail-fast.
    - B turns one bad change into permanent deafness.
    - C sends the exception back to the handler that just rethrew it.
  - The commit stands under every option.
- **Q-P3. The journal's surface.**
  - **A.** A public `IDisposable CultCache.AddJournal(Action<IReadOnlyList<CultCacheDocumentChange<object>>>)`,
    guarded so that entering the cache from it throws.
  - **B.** An internal method, with `InternalsVisibleTo("GameCult.Networking")`.
    The comment at `CultCache.cs:214` shows Networking was deliberately kept
    without it.
  - **Recommended: A.**
    - The contract is small, and the dangerous misuse, reentry, is enforced
      rather than documented.
    - An ordered admission journal is a legitimate library capability: audit,
      replication and derived indexes.
    - B couples two assemblies to hide one method.
- **Q-P4. Cut 6 now or later?**
  - **A.** Land it in this campaign.
  - **B.** Record it.
  - **Recommended: A.** With every commit now published, two writers of one key
    on two threads can leave a peer holding the older state. That was already
    true for loads; commits make it common.

Not forks, recorded so they are not re-asked:
- A throwing getter refuses loads too.
- Reentrant writes keep main's depth-first delivery. Order is data.
- Journal I/O under the gate follows the store precedent.

## Follow-ups outside this campaign

- **`cultmesh-py` watch.** It has no isolation and no Sequence
  (`node.py:1059-1065`).
- **`RecordMutationLogEntry` memory-before-disk.** A throwing durable `Append`
  leaves memory ahead of disk. This predates the campaign.
- **Durable log whole-file rewrite.** `CultNetShardMutationLogStorage.cs:83-89`
  rewrites the whole file per `Append`. It is a monolith, and under the gate
  its cost is now every writer's.
- **Mesh database-backed handles.** `CultMesh.cs:2362` and `:3396` can drop
  stale changes once `CultNetDatabaseChange.Sequence` exists.
- **Skill scar.** The Eureka SKILL.md already says "a ticket system that
  serialized delivery was built, deadlocked, and was deleted". The Soul pass 3
  direction re-proposed it anyway. The Soul brief template should tell Soul to
  check `git log -S` for a proposed mechanism before recommending it.
