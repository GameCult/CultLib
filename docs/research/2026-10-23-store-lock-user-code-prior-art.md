Serves question variants:question:store-hold-user-code (campaign variants), 2026-10-23.

# Prior-art check: user code under a lock

Facts and pointers only. Each source was fetched or searched this session; what was not reachable is marked.

## 1. Goetz et al., Java Concurrency in Practice (Addison-Wesley, 2006), ch. 10 "Avoiding Liveness Hazards"
Status: EXISTS, SECTION NUMBER CORRECTED.
- "Open Calls" is **10.1.4**, not 10.1.3. 10.1.3 is "Deadlocks Between Cooperating Objects"; 10.1.2 is "Dynamic Lock Order Deadlocks"; 10.1.5 is "Resource Deadlocks". Source: text extracted from a PDF copy of the book (its TOC lists Listing 10.6 "Using Open Calls to Avoiding Deadlock Between Cooperating Objects", p. 133). The copy is an unofficial one at library.megu.edu.ua; a publisher link was not checked.
- (a) 10.1.3 gives the warning sign: an "alien method" (defined p. 40 as a method whose behavior the calling class does not fully specify) called while holding a lock. Paraphrase: such a call may take other locks (deadlock risk) or block a long time, stalling threads that need the lock you hold. 10.1.4 defines an open call as a call made with no locks held (credited to Lea, CPJ 2.4.1.3) and says programs restricted to open calls are far easier to analyze for deadlock-freedom than ones that allow alien calls under lock.
- Cost it names: moving a call out of a synchronized block makes an atomic operation non-atomic (its getImage example stops being a snapshot). Where atomicity is needed it suggests structuring so only one thread can run the code path after the open call (service shutdown example).
- (b), (c): not addressed in this section.
- Pointer: JCIP 10.1.3 and 10.1.4, pp. ~132-134 (chapter 10 starts p. 128).

## 2. Sutter, "Effective Concurrency: Avoid Calling Unknown Code While Inside a Critical Section"
Status: VERIFIED, with date caveat.
- Dr. Dobb's column in the "Effective Concurrency" series. Sutter's blog teaser is dated 2007-11-06: https://herbsutter.com/2007/11/06/effective-concurrency-avoid-calling-unknown-code-while-inside-a-critical-section/ . Original link ddj.com/architect/202802983; Dr. Dobb's mirror https://drdobbs.com/cpp/avoid-calling-unknown-code-while-inside/202802983 (4 pages). The Dr. Dobb's issue date was not shown in anything fetched; the blog date is the only date confirmed. www.drdobbs.com did not resolve from this machine, so the column body comes from search-result summaries and the blog teaser, not a full first-hand read.
- (a) Core claim: locks do not compose; two separately correct modules can deadlock when combined. Trigger: caller inside a critical section, callee enters another critical section or blocks, another thread can take the same two in the opposite order. "Unknown code" listed as: library functions, plug-ins, callbacks/function pointers, virtual methods, generic types.
- Mitigations listed: move the call outside the critical section; pass copies instead of shared data; narrow the critical section (finer locks, reader-writer); require callees not to block (documentation/discipline only).
- (b), (c): not addressed.

## 3. Linux lockdep
Status: VERIFIED, with a nuance.
- Source: kernel docs "Runtime locking correctness validator", https://docs.kernel.org/locking/lockdep-design.html .
- (b) Lockdep tracks lock classes, not instances, and records dependencies between classes as sequences are observed. It reports a circular order as a potential deadlock even if the deadlock never happened. Its "Proof of 100% correctness" section says that once each simple single-task locking sequence has occurred at least once, no timing of them can deadlock. Each new lock chain is validated once and hashed; repeats skip validation.
- Nuance: it flags the dependency edge that closes a cycle, so both orders must have been exercised at some point (possibly by different, non-concurrent runs). A lone nesting is flagged only if it breaks a single-lock rule (IRQ-safe vs unsafe, same-class recursion). "Refuses on first sight" fits the closing edge or a single-lock rule violation only. It warns; it does not block the acquisition.

## 4. ConcurrentHashMap re-check after locking
Status: VERIFIED.
- Source: OpenJDK java/util/concurrent/ConcurrentHashMap.java, `putVal` and `computeIfAbsent`: head node `f` is read unlocked, `synchronized (f)` is taken, then `tabAt(tab, i) == f` is re-checked; on failure nothing is done and the loop retries. The overview comment gives the reason: the locked node can be replaced, so first-in-bin must be confirmed after locking. (Only the first 100k of 269k chars were read; both methods were reported found there.) Empty-bin `computeIfAbsent` uses a ReservationNode plus CAS instead.
- (c) This is the pattern: choose the lock from an unlocked read, validate the read after acquiring.

## 5. LMDB
Status: VERIFIED (paraphrase). Not about alien code or lock order.
- Source: lmdb.h, `mdb_txn_begin` and `mdb_env_open` (https://github.com/LMDB/lmdb, libraries/liblmdb/lmdb.h). A thread may hold one transaction at a time (plus children); a transaction and its cursors belong to one thread; one write transaction at a time, so writers cannot deadlock each other; MDB_NOTLS ties reader slots to the txn object instead of the thread, and the application must then serialize writers itself because write locking is unaware of user threads.
- Relevance: "one hold per thread" as a deliberate rule, parallel to CultCache's one-cache-gate-per-thread.

## 6. SQLite btmutex.c
Status: VERIFIED (summary of trunk source).
- Source: https://github.com/sqlite/sqlite/blob/master/src/btmutex.c (read via raw.githubusercontent.com; sqlite.org served a bot check).
- (b) Prevention by ordering, not detection: sharable B-trees on a connection sit in a list sorted by BtShared address and are always locked ascending (`sqlite3BtreeEnterAll`). `sqlite3BtreeEnter` try-locks first; on failure it releases later-ordered locks, waits, then reacquires in order. `wantToLock` is a count that makes a non-recursive mutex behave recursively.
- No alien-code or callback discussion in these comments.

## Counter-positions and how they stay safe
- Callbacks under lock, documented: Java `ConcurrentHashMap.compute`/`computeIfAbsent`. Javadoc (Java 21): the function should be short and simple because other threads' updates may block meanwhile, and must not modify the map. The javadoc does not say "internal lock"; that it runs under the bin lock is inferred from the source in item 4. Safety rests on documented restrictions, not enforcement.
- Opposite choice: .NET `ConcurrentDictionary.GetOrAdd(key, valueFactory)`. Writes use fine-grained locks but valueFactory runs outside them, "to avoid the problems that can arise from executing unknown code under a lock" (doc remarks). Price: not atomic; the factory may run more than once; a recheck decides the winner (another thread's insert wins). https://learn.microsoft.com/en-us/dotnet/api/system.collections.concurrent.concurrentdictionary-2.getoradd . Same shape as option `encode-first` (user code outside the lock, then recheck and commit) and as item 4.
- JCIP and Sutter both name the cost of leaving the lock: lost atomicity. Sutter also offers "pass copies" and "callee must not block" as alternatives to leaving.
- Not researched (no source fetched): Linux notifier chains, Java `synchronized` event listeners, other systems that call back under lock by design. No verified citation for them.

## Unverified / not done
- JCIP: section numbers and text come from one PDF copy, not the publisher edition.
- Sutter: full column body not read first-hand.
- No Discord or voidbot content read.
