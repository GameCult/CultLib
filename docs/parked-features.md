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
- Consumers when parked: none outside tests.
- Restore: `git show parked/cultcache-soa:src/GameCult.Caching/CultManagedDocument.cs`,
  take the SoA types, and re-hook a `CultCacheSoaStore` into the cache's single
  admission and eviction paths (`Admit`/`Evict`).
