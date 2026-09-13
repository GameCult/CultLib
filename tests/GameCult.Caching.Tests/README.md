# GameCult.Caching.Tests

`GameCult.Caching.Tests` contains NUnit tests for the caching library and its
MessagePack persistence stores.

## Scope

The tests currently cover:

- `SingleFileMessagePackBackingStore` round-trips, external pulls, and the
  dirty-pull guard
- `DirectoryMessagePackBackingStore` with the
  `cultcache.store.v4.directory-content-addressed-pages` manifest: page writes,
  clean flushes, concurrent writers under the commit lease, external deltas,
  unflushed local keys, orphaned pages, and observers running after the lease
  is released
- `CultCacheMessagePack.OpenAsync`, persisted globals, dirty state, and flush on
  dispose
- cache lookups (`Get`, `GetAll`, runtime-type upserts)
- `CultCache.Commit` batches on a directory store: visibility after the store
  commits, abandoned stages, sealed batches, observers committing again
- explicit Cult document payload codecs, generated metadata, canonical schema
  fixtures, and schema drift reports
- hand-written MessagePack snapshot and record serialization
- `StoreRoutingTests`: the store-composition contract (routing, attachment is
  hydration, read-only stores, global singletons, assignable lookups, batches,
  all-or-nothing loads)
- `ConditionalCommitTests`: `Expect`, `ExpectUnchanged`, `TryCommit`, the
  single-file lock, strictly increasing `storedAt`, and the same conditions on a
  directory store
- `DirectoryStoreDurabilityTests`: failed manifest replace, content-addressed
  pages, the commit lease, orphan pages and tampered pages, with faults from
  real file locks
- `PreCut2StoreFormatTests`: stores written before Cut 2 still open

Selective hydration, the directory store's stage probes, pre-v4 directory
formats, and the SoA table are no longer in the tree; the SoA table is parked
at `parked/cultcache-soa` (`docs/parked-features.md`).

## Run

```powershell
dotnet test tests\GameCult.Caching.Tests\GameCult.Caching.Tests.csproj
```
