# Parked Features

A parked feature was built for a stated reason, has no consumer, and was
removed from the tree. Its last working form lives at an annotated tag so it
can be restored instead of reinvented.

## CultCache SoA table

- Tag: `parked/cultcache-soa`
- Intent: ECS-style structure-of-arrays columns over cached documents, for
  hot-loop performance without giving up POCO document ergonomics.
- Span at the tag: `src/GameCult.Caching/CultManagedDocument.cs:133-414`
  (`CultSoaTable<T>`, `CultSoaColumn<T>`, `CultCacheSoaStore`,
  `CultCacheSoaTypeTable`, `CultCacheSoaMember`); `CultCache.Soa<T>()` and the
  `_soa` field with its upsert/remove hooks in `CultCache.cs`; the SoA tests in
  `tests/GameCult.Caching.Tests/BackingStoreTests.cs` and
  `tests/GameCult.Mesh.Tests/CultMeshStreamingTests.cs`.
- Managed document: `CultManagedDocument<T>` (`CultManagedDocument.cs:72-131` at
  the tag) with `CultCache.Document<T>` and `CultNetDatabase.Document<T>`, the
  reactive POCO handle added with the SoA storage in 5428870 "Make SoA
  cache-managed document storage". Same tag, same intent: the document stays a
  POCO while the cache owns its columnar storage.
- Tag message correction: the `parked/cultcache-soa` tag message says
  `CultManagedDocument<T>` was "deleted separately, not parked". This note
  supersedes it: `CultManagedDocument<T>` is parked under the same tag and is
  restored alongside SoA. The tag itself is left as written.
- Consumers when parked: none outside tests.
- Restore: `git show parked/cultcache-soa:src/GameCult.Caching/CultManagedDocument.cs`,
  take the SoA types and `CultManagedDocument<T>`, restore both `Document<T>`
  openers from `CultCache.cs` and `CultNetDatabase.cs` at the tag, and re-hook a
  `CultCacheSoaStore` into the cache's single
  admission and eviction paths (`Admit`/`Evict`).
