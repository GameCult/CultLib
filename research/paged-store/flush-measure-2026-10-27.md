# CultCache whole-view single-file flush: measured cost on Aetheria's catalog

Serves ruling `variants:ruling:page-tree-store-research` and follow-up `variants:follow_up:paged-single-file-store` (the eureka-state `view` calls refused the bare ids, `missing field id` / `expected struct PipelineRef`, so the brief's text was the spec). Session `self-2026-10-27-eureka`. Measuring probe: nothing admitted, nothing committed, no repo changed. Date 2026-10-27.

## Headline

- Aetheria's real catalog is small: `GameData/Aetheria.cc` is 214,588 bytes and **394 records** in 18 schemas; `aetheria.nakovalnya/ship.cc` is 40,059 bytes and **3 records**. Neither is a store where a flush is slow.
- A flush rewrites the whole view whatever changed. 1, 10, 1% and 10% of records changed cost the same (Aetheria.cc on the container's disk: 5.0 to 6.0 ms for 1 to 39 records changed, 214.6 KB written each time, 2.56 MB allocated each time).
- Cost per flush on the real catalog: about 5.5 ms on the container disk (serialise 1.8 ms; the other 3.7 ms is record assembly, about 1.1 ms of it CPU, plus the write and fsync), 2.5 ms with disk free. Sustained rate: about 180 one-record flushes per second on disk, about 300 in memory.
- The cost is linear in record count. On a synthetic catalog of the same record shape: 3,940 records flush in about 31 ms (about 33 per second), 39,400 records in about 320 ms (3 per second). A 30 Hz writer fits the real catalog (5.5 ms of a 33.3 ms frame) and stops fitting between roughly 4,000 and 5,000 records, whatever the disk.
- CPU, not I/O, owns the cost once the store passes a few thousand records: at 39,400 records the in-memory (tmpfs) flush is 300 ms against 320 ms on disk.
- The existing `DirectoryMessagePackBackingStore` does not fix this. A 1-record flush rewrites the whole manifest: 113 KB per flush on Aetheria.cc (0.53 of the file), 700 KB at 3,940 records (0.31), and takes as long as the single-file flush (5.8 ms against 5.4 ms; 39 ms against 32 ms at 3,940 records). It accepted all 394 records (no variants in the catalog, so nothing refused).
- C2a (`7c7cc799`) cannot open Aetheria's current types at all: its registry throws `Faction.Places: list element type FactionPlace has no [CultElementId] member`. Its serialise step is cheaper (per-record bytes are kept: 0.16 ms against 1.1 ms at 394 records, 74 ms against 128 ms at 39,400), but totals move little (-11 percent at 39,400 records, 5.6 to 3.7 ms at 394) because record assembly and the whole-file write remain O(records). C2a loads slower (2x at 394 records, 2.3x at 39,400). Its numbers are synthetic-only.

## Method

Where: Yggdrasil via `ygg-verify.sh` (image `mcr.microsoft.com/dotnet/sdk:10.0`, 4 CPUs by cgroup `cpu.max 400000 100000`, 6 GiB, niced, up to 5 jobs share the host). **Every absolute number is from that shared, capped, niced container** and carries its noise (run-to-run max values show it); the shape (flat in changed records, linear in total records) is the finding, not the milliseconds. Starfire ran nothing but git, file copies and text editing. Container disk is `/dev/vda4` (the host's disk); "shm" is `/dev/shm` tmpfs in the same container, where fsync and write cost almost nothing, so shm separates CPU from I/O.

Inputs:
- Aetheria `GameData/Aetheria.cc` and `GameData/Mods/aetheria.nakovalnya/ship.cc` smudged from the `origin/master` blobs (Aetheria `b375cfc8`) by `git cat-file -p | git lfs smudge` into a scratch directory; the LFS content reached the container as part of the bundled job repo, so no LFS cache question arose. The operator checkout was not touched.
- Aetheria's own types: the bundle carries `Aetheria.Shared` (the headless build of `Assets/Scripts/ServerShared`, UniRx, Ink) from `origin/master`, so the real `SingleFileMessagePackBackingStore` hydrates real `ItemData`, `Faction`, `ShipAuthoring` and the rest. Wiring as in `AetheriaStores.Open`: `new CultCache()`, `AddBackingStore(new SingleFileMessagePackBackingStore(path), AetheriaStores.CatalogTypes)`; `Open`'s post-load validations are not timed.
- CultLib: `origin/main` `6a20309d`; `origin/hands/variants-c2a` `7c7cc799` (the branch tip when the job ran; it was `0c19d080` when the session began, a commit was added since); Aetheria's pinned CultLib `b8fc5eb` as a third column ("pin"), because that, not main, is what the game compiles today. Aetheria master compiles unchanged against main and against the pin (the `Directory.Build.targets` pin check was left out of the bundle). It does not run against C2a.

Measurements (harness `Program.cs`, scratchpad `flush-measure\bundle\harness\`):
- Load: `new store + new CultCache + AddBackingStore` (attaching reads the store). "Process-cold" = first load in a fresh process, JIT and type discovery included, median of 5 processes; the OS page cache could not be dropped in the container, so it is cold for the process, warm for the file. "Warm" = median of 15 further loads in one process.
- Flush: mutate N records through `cache.UpsertAsync(type, doc, key)` (first writable non-Name float, int or string member, deterministic spread over the record list), assert `cache.IsDirty`, then time only `cache.FlushAllBackingStores()` with a `Stopwatch`; 3 discarded warm-up flushes, then 15 timed. N = 1, 10, 1 percent, 10 percent of records. Medians reported; min and max are in the raw log.
- Serialise vs rest: the harness subclasses the real store and overrides only its two serialise hooks (`SerializePayload`, `SerializeSnapshot`; on C2a `SerializePayload/Record/CatalogEntry/Store`) to accumulate `Stopwatch` ticks, then calls `base`. "Rest" = total minus serialise: lock sidecar, record assembly and ordering, schema catalog build, and write + `FlushToDisk` + `File.Replace`. It is not pure I/O: the in-memory column shows what remains with I/O free. A separate floor row times the store's exact atomic write (temp file, `WriteThrough`, fsync, `File.Replace`) of a same-size random payload.
- Bytes written: the store replaces the file whole, so bytes written = resulting file size (measured). For the directory store, the files whose length or mtime changed or that are new, summed.
- Allocation: `GC.GetAllocatedBytesForCurrentThread()` delta across the flush. This is bytes allocated, an upper bound on peak transient managed memory, not a measured high-water mark.
- Throughput: loop of (mutate one record + upsert + flush) for 4 s (3 s synthetic); cycles per second, plus the flush-only median.
- Directory store: `DirectoryMessagePackBackingStore(manifestPath)` filled by upserting the same documents under the same keys, then the same flush measurements. It refuses variants (`NotSupportedException`); the catalog holds none (single-file format v1), so it refused 0 of 394 and 0 of 3.
- Synthetic sweep: a standalone `SynthItem` document (name, description, 4 numbers, a string list, a 30-float list, a nested object; about 570 bytes per record, the real catalog's mean is 545) at 394, 3,940 and 39,400 records, because real `[CultGlobal]` records cannot be cloned to scale and C2a refuses the real types. Same harness code on main and C2a.

Raw logs: `...\scratchpad\flush-measure\run1.log` (real data, main and pin), `run3.log` (synthetic main and C2a). `run2.log` is the failed first C2a attempt and is evidence of the registry refusal. Re-run: `tables.py` regenerates the tables below from the logs.

## Tables

## A. Real Aetheria stores (Aetheria.Shared types, CultLib main 6a20309d and Aetheria's pinned CultLib b8fc5eb)

### A1. Load
| store | rev | process-cold load, ms (median of 5 fresh processes, JIT included) | first load after warm-up, ms | warm load, ms median (min-max, n=15) | dir-store warm load, ms |
|---|---|---|---|---|---|
| Aetheria.cc | main | 688 (659-716) | 695.56 | 5.42 (4.71-17.23) | 6.22 |
| Aetheria.cc | pin | 722 (676-781) | 634.87 | 6.35 (4.36-20.42) | 5.43 |
| ship.cc | main | 18 (17-33) | 28.11 | 0.18 (0.16-0.40) | 0.38 |
| ship.cc | pin | 17 (16-29) | 17.84 | 0.19 (0.17-0.37) | 0.36 |

### A2. Single-file flush (disk = Yggdrasil container work dir on host disk; shm = /dev/shm tmpfs, fsync is free)
| store | rev | medium | changed | total ms | serialise ms | rest ms (lock, order, write, fsync, replace) | allocated MB | file bytes written |
|---|---|---|---|---|---|---|---|---|
| Aetheria.cc | main | disk | 1rec (n=1) | 5.97 | 1.85 | 3.72 | 2.56 | 214599 |
| Aetheria.cc | main | disk | 1pct (n=3) | 5.16 | 1.79 | 3.26 | 2.56 | 214750 |
| Aetheria.cc | main | disk | 10rec (n=10) | 5.01 | 1.75 | 2.98 | 2.56 | 214681 |
| Aetheria.cc | main | disk | 10pct (n=39) | 8.46 | 2.41 | 4.80 | 2.56 | 214757 |
| Aetheria.cc | main | shm | 1rec (n=1) | 2.65 | 1.06 | 1.14 | 2.54 | 214599 |
| Aetheria.cc | main | shm | 1pct (n=3) | 2.27 | 1.09 | 1.13 | 2.54 | 214750 |
| Aetheria.cc | main | shm | 10rec (n=10) | 2.83 | 1.11 | 1.17 | 2.54 | 214681 |
| Aetheria.cc | main | shm | 10pct (n=39) | 2.60 | 1.21 | 1.18 | 2.54 | 214757 |
| Aetheria.cc | pin | disk | 1rec (n=1) | 5.25 | 1.78 | 3.35 | 2.56 | 214599 |
| Aetheria.cc | pin | disk | 1pct (n=3) | 6.51 | 2.38 | 3.94 | 2.56 | 214750 |
| Aetheria.cc | pin | disk | 10rec (n=10) | 8.07 | 2.53 | 4.02 | 2.56 | 214681 |
| Aetheria.cc | pin | disk | 10pct (n=39) | 8.82 | 4.26 | 5.01 | 2.56 | 214757 |
| Aetheria.cc | pin | shm | 1rec (n=1) | 2.13 | 1.01 | 1.06 | 2.54 | 214599 |
| Aetheria.cc | pin | shm | 1pct (n=3) | 2.18 | 1.03 | 1.07 | 2.54 | 214750 |
| Aetheria.cc | pin | shm | 10rec (n=10) | 2.15 | 1.02 | 1.04 | 2.54 | 214681 |
| Aetheria.cc | pin | shm | 10pct (n=39) | 2.27 | 1.08 | 1.11 | 2.54 | 214757 |
| ship.cc | main | disk | 1pct (n=1) | 2.29 | 0.19 | 2.11 | 0.21 | 28642 |
| ship.cc | main | disk | 1rec (n=1) | 1.68 | 0.08 | 1.59 | 0.21 | 28642 |
| ship.cc | main | disk | 10pct (n=1) | 1.76 | 0.10 | 1.66 | 0.21 | 28642 |
| ship.cc | main | disk | 10rec (n=10) | 1.76 | 0.11 | 1.66 | 0.21 | 28641 |
| ship.cc | main | shm | 1pct (n=1) | 0.23 | 0.09 | 0.14 | 0.21 | 28642 |
| ship.cc | main | shm | 1rec (n=1) | 0.23 | 0.09 | 0.14 | 0.21 | 28642 |
| ship.cc | main | shm | 10pct (n=1) | 0.23 | 0.09 | 0.14 | 0.21 | 28642 |
| ship.cc | main | shm | 10rec (n=10) | 0.23 | 0.09 | 0.14 | 0.21 | 28641 |
| ship.cc | pin | disk | 1pct (n=1) | 1.96 | 0.18 | 1.78 | 0.21 | 28642 |
| ship.cc | pin | disk | 1rec (n=1) | 1.75 | 0.08 | 1.67 | 0.21 | 28642 |
| ship.cc | pin | disk | 10pct (n=1) | 1.77 | 0.10 | 1.62 | 0.21 | 28642 |
| ship.cc | pin | disk | 10rec (n=10) | 1.72 | 0.08 | 1.60 | 0.21 | 28641 |
| ship.cc | pin | shm | 1pct (n=1) | 0.17 | 0.06 | 0.11 | 0.21 | 28642 |
| ship.cc | pin | shm | 1rec (n=1) | 0.18 | 0.06 | 0.12 | 0.21 | 28642 |
| ship.cc | pin | shm | 10pct (n=1) | 0.18 | 0.06 | 0.12 | 0.21 | 28642 |
| ship.cc | pin | shm | 10rec (n=10) | 0.17 | 0.06 | 0.11 | 0.21 | 28641 |

### A3. Throughput of 1-record change + flush (4 s loop)
| store | rev | medium | single-file cycles/s | single-file flush ms median | directory-store cycles/s | raw atomic write of same size, ms median |
|---|---|---|---|---|---|---|
| Aetheria.cc | main | disk | 180 | 5.41 | 129 | 1.795 |
| Aetheria.cc | main | shm | 297 | 2.36 | 264 | 0.115 |
| Aetheria.cc | pin | disk | 167 | 5.37 | 126 | 1.722 |
| Aetheria.cc | pin | shm | 275 | 2.55 | 263 | 0.119 |
| ship.cc | main | disk | 468 | 1.93 | 281 | 1.427 |
| ship.cc | main | shm | 4809 | 0.17 | 3393 | 0.043 |
| ship.cc | pin | disk | 497 | 1.71 | 355 | 1.458 |
| ship.cc | pin | shm | 4607 | 0.17 | 3347 | 0.053 |

### A4. DirectoryMessagePackBackingStore, same records
| store | rev | medium | initial full write ms | files | bytes on disk | 1-record flush ms | 1-record bytes written (new+changed files) | files touched | 10-record flush ms | 10-record bytes |
|---|---|---|---|---|---|---|---|---|---|---|
| Aetheria.cc | main | disk | 564 | 396 | 284792 | 5.83 | 113484 | 2 | 20.98 | 117092 |
| Aetheria.cc | main | shm | 32 | 396 | 284791 | 3.24 | 113484 | 2 | 4.67 | 117094 |
| Aetheria.cc | pin | disk | 618 | 396 | 284788 | 8.18 | 113484 | 2 | 25.18 | 117093 |
| Aetheria.cc | pin | shm | 36 | 396 | 284781 | 2.63 | 113483 | 2 | 3.71 | 117092 |
| ship.cc | main | disk | 5 | 5 | 29235 | 3.58 | 11168 | 2 | 6.73 | 29235 |
| ship.cc | main | shm | 0 | 5 | 29234 | 0.25 | 1782 | 1 | 0.42 | 19849 |
| ship.cc | pin | disk | 5 | 5 | 29234 | 3.24 | 11167 | 2 | 5.40 | 29234 |
| ship.cc | pin | shm | 0 | 5 | 29235 | 0.23 | 1783 | 1 | 0.43 | 19850 |

## B. Synthetic catalog scale sweep (SynthItem, about 570 B per record; main vs c2a; 1/10/10% of records changed)

| records | medium | rev | file MB | load warm ms | 1-rec flush total / serialise ms | 10-rec flush total / serialise ms | 10% flush total / serialise ms | alloc MB per 1-rec flush | 1-rec cycles/s | dir 1-rec flush ms | dir 1-rec bytes written | dir 10-rec flush ms | dir cycles/s |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 394 | disk | main | 0.23 | 4.67 | 5.63 / 1.12 | 5.81 / 1.45 | 5.49 / 1.58 | 2.68 | 196 | 7.94 | 72655 | 20.30 | 148 |
| 394 | disk | c2a | 0.23 | 10.63 | 3.69 / 0.16 | 3.79 / 0.49 | 4.24 / 0.56 | 2.11 | 206 | 7.08 | 72655 | 24.59 | 141 |
| 394 | shm | main | 0.23 | 1.48 | 2.81 / 1.31 | 1.67 / 0.59 | 1.78 / 0.65 | 2.65 | 469 | 2.91 | 72655 | 3.13 | 339 |
| 394 | shm | c2a | 0.23 | 4.77 | 1.51 / 0.19 | 0.81 / 0.09 | 0.97 / 0.12 | 2.11 | 693 | 2.70 | 72655 | 3.32 | 321 |
| 3940 | disk | main | 2.25 | 27.34 | 31.84 / 13.48 | 30.11 / 12.92 | 34.20 / 12.55 | 29.43 | 35 | 38.83 | 700297 | 44.11 | 26 |
| 3940 | disk | c2a | 2.25 | 53.70 | 30.15 / 7.50 | 27.31 / 7.51 | 38.03 / 8.24 | 28.25 | 33 | 34.48 | 700297 | 46.68 | 25 |
| 3940 | shm | main | 2.25 | 16.78 | 27.11 / 11.22 | 28.50 / 11.86 | 29.08 / 11.50 | 29.43 | 34 | 36.00 | 700297 | 34.33 | 29 |
| 3940 | shm | c2a | 2.25 | 58.77 | 15.13 / 1.02 | 16.09 / 1.16 | 20.43 / 1.74 | 28.25 | 56 | 28.02 | 700297 | 37.51 | 30 |
| 39400 | disk | main | 22.67 | 251.42 | 319.89 / 127.74 | 317.56 / 130.91 | 373.04 / 154.01 | 268.25 | 3 | - | - | - | - |
| 39400 | disk | c2a | 22.67 | 591.61 | 283.51 / 74.10 | 335.84 / 79.71 | 383.73 / 85.22 | 252.89 | 3 | - | - | - | - |
| 39400 | shm | main | 22.67 | 248.32 | 299.82 / 131.95 | 326.44 / 137.70 | 369.78 / 167.95 | 268.25 | 3 | - | - | - | - |
| 39400 | shm | c2a | 22.67 | 653.54 | 281.16 / 70.06 | 280.78 / 72.06 | 363.50 / 84.19 | 252.89 | 3 | - | - | - | - |

## What the numbers say

- Flat in changed records, linear in total records. One record changed costs what ten percent changed costs, because `PushAll` serialises every entry and writes the whole file (`SingleFileBackingStore.PushAll` in `CultCache.cs`). On the real catalog the work per flush is 394 payload serialisations and a 214 KB write for a 600-byte edit.
- At the real catalog's size the cost is small: 5.5 ms on a congested container disk, 2.5 ms in memory, 1.7 ms for the 3-record mod store (0.2 ms in memory). An editor saving one record never notices it. The case for paging is not this catalog today.
- A 30 Hz writer (33.3 ms frame) spends about 16 percent of the frame on the real catalog on this disk, 7 percent with I/O free. The knee is the record count: 3,940 records already take 30 to 32 ms (a whole frame, 33 per second sustained), 39,400 take 300 ms. The same holds in memory (27 and 300 ms), so a faster disk does not move the knee; record assembly and serialisation do.
- The existing directory store is not the paged store. It is not cheaper per flush (manifest rewritten whole, 0.3 to 0.5 of the single-file bytes, same milliseconds), costs 396 files and 285 KB for the catalog, and its first full write took 560 to 620 ms on disk against about 70 ms for the single file.
- C2a's per-record byte reuse removes most of the serialise cost, not the O(records) flush. Whole-file writes and per-flush record assembly remain; at 39,400 records the total moved from 320 to 284 ms on disk (shm 300 to 281). Load got slower (394 records 4.7 to 10.6 ms; 39,400 records 251 to 592 ms). That load cost is the price of the format that C2a introduces and is worth a look separately.
- C2a is a breaking change for the game's catalog: every list-of-objects member needs an element id member, and the existing stores have none. That is a migration question for the C2a cut, not a flush number.

## Caveats

- Container noise: one shared, niced, CPU-capped job among up to five; medians of 15 (8 at 39,400 records) with wide max values. Treat differences under about 20 percent as noise (main against pin at the same size are the control: they agree within that).
- Process-cold load of Aetheria.cc is about 690 ms, almost all JIT and registry/type discovery; the file part is the 5 ms warm figure.
- C2a figures are synthetic, one record shape, one run per medium. Main and C2a synthetic runs came from one job, one after the other.
- Allocation is bytes allocated per flush, not peak working set.
- A real-type scale sweep failed (`[CultGlobal]` records cannot be duplicated: "aetheria.verse_grammar is a global and already has record"); the synthetic sweep replaces it.
