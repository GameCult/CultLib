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
- the ambient cache transaction (`ExecuteTransactionAsync`), until Cut 3
  replaces it with the explicit batch commit
- explicit Cult document payload codecs, generated metadata, canonical schema
  fixtures, and schema drift reports
- hand-written MessagePack snapshot and record serialization
- `StoreRoutingTests`: the Cut 1 store-routing contract, red until Cut 3

Selective hydration, the directory store's stage probes, pre-v4 directory
formats, and the SoA table are no longer in the tree; the SoA table is parked
at `parked/cultcache-soa` (`docs/parked-features.md`).

## Run

```powershell
dotnet test tests\GameCult.Caching.Tests\GameCult.Caching.Tests.csproj
```
