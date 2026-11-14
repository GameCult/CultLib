//! `CultCache::update`: read the record as its home store holds it, apply, commit only over what was read, rerun on a lost race
//! (C# `CultCache.Update`, as amended by PR #170). A rival is a second `CultCache` on the same file.

use std::cell::Cell;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Barrier};

use cultcache_rs::{
    CacheBackingStore, CultCache, CultCacheEnvelope, CultCommitIf, CultStoredRecord, CultUpdateOutcome, DatabaseEntry,
    PushAllOptions, RedbMessagePackBackingStore, SingleFileMessagePackBackingStore,
};

#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "counter")]
struct Counter {
    #[cultcache(key = 0)]
    n: u32,
}

/// The same type with a second slot: a record it writes carries a slot `Counter` does not read.
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "counter")]
struct WideCounter {
    #[cultcache(key = 0)]
    n: u32,
    #[cultcache(key = 1)]
    extra: u32,
}

#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "note")]
struct Note {
    #[cultcache(key = 0)]
    text: String,
}

#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "unique_sku")]
struct Sku {
    #[cultcache(key = 0, index = "Sku")]
    sku: String,
}

fn counter(n: u32) -> Counter {
    Counter { n }
}

fn open(path: &Path) -> CultCache {
    let mut cache = CultCache::new();
    cache.register_entry_type::<Counter>().unwrap();
    cache.register_entry_type::<Note>().unwrap();
    cache.register_entry_type::<Sku>().unwrap();
    cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(path)).unwrap();
    cache.pull_all_backing_stores().unwrap();
    cache
}

fn path_in(dir: &tempfile::TempDir) -> PathBuf {
    dir.path().join("store.cc")
}

/// What a fresh cache reads at `key`.
fn stored(path: &Path, key: &str) -> Option<u32> {
    open(path).get::<Counter>(key).unwrap().map(|counter| counter.n)
}

fn file(path: &Path) -> Option<Vec<u8>> {
    std::fs::read(path).ok()
}

#[test]
fn two_writers_updating_lose_no_increment() {
    let dir = tempfile::tempdir().unwrap();
    let path = path_in(&dir);
    let calls = Arc::new(AtomicUsize::new(0));
    let start = Arc::new(Barrier::new(2));
    let threads: Vec<_> = (0..2)
        .map(|_| {
            let (path, calls, start) = (path.clone(), calls.clone(), start.clone());
            std::thread::spawn(move || {
                let mut cache = open(&path);
                start.wait();
                for _ in 0..50 {
                    let result = cache
                        .update_with_attempts::<Counter, _>("c", 100, |current| {
                            calls.fetch_add(1, Ordering::SeqCst);
                            // The window a rival can land in.
                            std::thread::sleep(std::time::Duration::from_millis(1));
                            Some(counter(current.map_or(0, |held| held.n) + 1))
                        })
                        .unwrap();
                    assert_eq!(result.outcome, CultUpdateOutcome::Updated);
                }
            })
        })
        .collect();
    for thread in threads {
        thread.join().unwrap();
    }
    assert_eq!(stored(&path, "c"), Some(100));
    assert!(calls.load(Ordering::SeqCst) > 100, "no apply was rerun, so no race was exercised");
}

#[test]
fn a_lost_attempt_reruns_apply_on_the_fresh_value() {
    let dir = tempfile::tempdir().unwrap();
    let path = path_in(&dir);
    let mut cache = open(&path);
    cache.put("c", &counter(1)).unwrap();
    let mut rival = open(&path);
    let mut seen = Vec::new();
    let result = cache
        .update::<Counter, _>("c", |current| {
            let current = current.unwrap().n;
            if seen.is_empty() {
                rival.put("c", &counter(100)).unwrap();
            }
            seen.push(current);
            Some(counter(current + 1))
        })
        .unwrap();
    assert_eq!(seen, [1, 100]);
    assert_eq!(result.outcome, CultUpdateOutcome::Updated);
    assert_eq!(result.document, Some(counter(101)));
    assert_eq!(stored(&path, "c"), Some(101));
}

#[test]
fn exhausted_attempts_return_lost_race() {
    let dir = tempfile::tempdir().unwrap();
    let path = path_in(&dir);
    let mut cache = open(&path);
    cache.put("c", &counter(1)).unwrap();
    let mut rival = open(&path);
    let mut calls = 0;
    let result = cache
        .update_with_attempts::<Counter, _>("c", 3, |current| {
            calls += 1;
            rival.put("c", &counter(100 * calls)).unwrap();
            current
        })
        .unwrap();
    assert_eq!(calls, 3);
    assert_eq!(result.outcome, CultUpdateOutcome::LostRace);
    // The pull before LostRace shows the value that won.
    assert_eq!(result.document, Some(counter(300)));
    assert_eq!(stored(&path, "c"), Some(300));
}

#[test]
fn max_attempts_bounds() {
    let dir = tempfile::tempdir().unwrap();
    let path = path_in(&dir);
    let mut cache = open(&path);
    cache.put("c", &counter(1)).unwrap();
    let mut rival = open(&path);
    // Always loses; panics past 10 calls so a weakened bound fails here instead of hanging.
    let mut losing = |calls: &Cell<u32>| {
        let n = calls.get() + 1;
        calls.set(n);
        assert!(n <= 10, "apply ran past the bound");
        rival.put("c", &counter(1000 + n)).unwrap();
        Some(counter(n))
    };

    let calls = Cell::new(0);
    let one = cache.update_with_attempts::<Counter, _>("c", 1, |_| losing(&calls)).unwrap();
    assert_eq!((calls.get(), one.outcome), (1, CultUpdateOutcome::LostRace));

    let calls = Cell::new(0);
    assert!(cache.update_with_attempts::<Counter, _>("c", 0, |_| losing(&calls)).is_err());
    assert_eq!(calls.get(), 0);

    // The default bound is 5.
    let calls = Cell::new(0);
    let default = cache.update::<Counter, _>("c", |_| losing(&calls)).unwrap();
    assert_eq!((calls.get(), default.outcome), (5, CultUpdateOutcome::LostRace));
}

#[test]
fn none_declines_and_writes_nothing() {
    let dir = tempfile::tempdir().unwrap();
    let path = path_in(&dir);
    let mut cache = open(&path);
    let absent = cache.update::<Counter, _>("c", |current| {
        assert_eq!(current, None);
        None
    });
    let absent = absent.unwrap();
    assert_eq!((absent.outcome, absent.document), (CultUpdateOutcome::Declined, None));
    assert_eq!(file(&path), None, "a declined update created the store");

    cache.put("c", &counter(1)).unwrap();
    let before = file(&path);
    let held = cache.update::<Counter, _>("c", |_| None).unwrap();
    assert_eq!((held.outcome, held.document), (CultUpdateOutcome::Declined, Some(counter(1))));
    assert_eq!(file(&path), before);
}

#[test]
fn a_declined_update_installs_nothing() {
    let dir = tempfile::tempdir().unwrap();
    let path = path_in(&dir);
    let mut cache = open(&path);
    cache.put("c", &counter(1)).unwrap();
    let mut rival = open(&path);
    rival.put("c", &counter(7)).unwrap();
    let mut seen = None;
    let result = cache
        .update::<Counter, _>("c", |current| {
            seen = current;
            None
        })
        .unwrap();
    assert_eq!(seen, Some(counter(7)), "apply saw the held copy, not the store's");
    assert_eq!(result.document, Some(counter(1)));
    assert_eq!(cache.get::<Counter>("c").unwrap(), Some(counter(1)));
    cache.pull_all_backing_stores().unwrap();
    assert_eq!(cache.get::<Counter>("c").unwrap(), Some(counter(7)));
}

#[test]
fn absent_key_rival_removal_and_a_store_holding_nothing() {
    // Two writers creating one absent key.
    let dir = tempfile::tempdir().unwrap();
    let path = path_in(&dir);
    let start = Arc::new(Barrier::new(2));
    let threads: Vec<_> = (0..2)
        .map(|_| {
            let (path, start) = (path.clone(), start.clone());
            std::thread::spawn(move || {
                let mut cache = open(&path);
                start.wait();
                let result = cache
                    .update_with_attempts::<Counter, _>("new", 20, |current| {
                        std::thread::sleep(std::time::Duration::from_millis(1));
                        Some(counter(current.map_or(0, |held| held.n) + 1))
                    })
                    .unwrap();
                assert_eq!(result.outcome, CultUpdateOutcome::Updated);
            })
        })
        .collect();
    for thread in threads {
        thread.join().unwrap();
    }
    assert_eq!(stored(&path, "new"), Some(2));

    // A rival deletes the record inside apply: the retry sees nothing.
    let dir = tempfile::tempdir().unwrap();
    let path = path_in(&dir);
    let mut cache = open(&path);
    cache.put("c", &counter(1)).unwrap();
    let mut rival = open(&path);
    let mut seen = Vec::new();
    let result = cache
        .update::<Counter, _>("c", |current| {
            if seen.is_empty() {
                assert!(rival.delete::<Counter>("c").unwrap());
            }
            seen.push(current.clone());
            Some(counter(current.map_or(0, |held| held.n) + 1))
        })
        .unwrap();
    assert_eq!(seen, [Some(counter(1)), None]);
    assert_eq!(result.outcome, CultUpdateOutcome::Updated);
    assert_eq!(stored(&path, "c"), Some(1));

    // The cache holds a record the store no longer has: apply is handed None.
    let dir = tempfile::tempdir().unwrap();
    let path = path_in(&dir);
    let mut cache = open(&path);
    cache.put("c", &counter(5)).unwrap();
    assert!(open(&path).delete::<Counter>("c").unwrap());
    let mut seen = Vec::new();
    cache
        .update::<Counter, _>("c", |current| {
            seen.push(current);
            Some(counter(1))
        })
        .unwrap();
    assert_eq!(seen, [None]);
    assert_eq!(stored(&path, "c"), Some(1));
}

#[test]
fn a_rival_writing_and_restoring_the_value_still_reruns_apply() {
    let dir = tempfile::tempdir().unwrap();
    let path = path_in(&dir);
    let mut cache = open(&path);
    cache.put("c", &counter(1)).unwrap();
    let mut rival = open(&path);
    let mut seen = Vec::new();
    cache
        .update::<Counter, _>("c", |current| {
            let current = current.unwrap().n;
            if seen.is_empty() {
                rival.put("c", &counter(5)).unwrap();
                rival.put("c", &counter(1)).unwrap();
            }
            seen.push(current);
            Some(counter(current + 1))
        })
        .unwrap();
    assert_eq!(seen, [1, 1]);
    assert_eq!(stored(&path, "c"), Some(2));
}

#[test]
fn a_stale_caches_first_attempt_reads_the_store() {
    let dir = tempfile::tempdir().unwrap();
    let path = path_in(&dir);
    let mut a = open(&path);
    a.put("c", &counter(1)).unwrap();
    let mut b = open(&path);
    a.put("c", &counter(7)).unwrap();
    let mut seen = Vec::new();
    b.update::<Counter, _>("c", |current| {
        seen.push(current.clone());
        current.map(|held| counter(held.n + 1))
    })
    .unwrap();
    assert_eq!(seen, [Some(counter(7))]);
    assert_eq!(stored(&path, "c"), Some(8));
}

/// A custom store over the single-file store that has no `read` or `commit_if`.
struct ReadlessStore(SingleFileMessagePackBackingStore);

impl CacheBackingStore for ReadlessStore {
    fn pull_all(&self) -> anyhow::Result<Vec<CultCacheEnvelope>> {
        self.0.pull_all()
    }
    fn push(&mut self, entry: &CultCacheEnvelope) -> anyhow::Result<String> {
        self.0.push(entry)
    }
    fn delete(&mut self, entry: &CultCacheEnvelope) -> anyhow::Result<()> {
        self.0.delete(entry)
    }
    fn push_all(&mut self, entries: &[CultCacheEnvelope], options: PushAllOptions) -> anyhow::Result<Vec<String>> {
        self.0.push_all(entries, options)
    }
}

/// Runs `update` with an apply that counts its calls; the refusal must come before the first.
fn refused(cache: &mut CultCache, path: &Path, key: &str) -> String {
    let before = file(path);
    let calls = Cell::new(0);
    let error = cache
        .update::<Counter, _>(key, |current| {
            calls.set(calls.get() + 1);
            current
        })
        .expect_err("the update must be refused");
    assert_eq!(calls.get(), 0, "apply ran before the refusal: {error:#}");
    assert_eq!(file(path), before, "a refused update touched the store");
    format!("{error:#}")
}

#[test]
fn refusals_come_before_apply() {
    // Another type's record written at the key since the pull: the read resolves it.
    let dir = tempfile::tempdir().unwrap();
    let path = path_in(&dir);
    let mut cache = open(&path);
    open(&path).put("k", &Note { text: "n".into() }).unwrap();
    let text = refused(&mut cache, &path, "k");
    assert!(text.contains("update cannot copy it"), "{text}");

    // Another type held at the key.
    let dir = tempfile::tempdir().unwrap();
    let path = path_in(&dir);
    let mut cache = open(&path);
    cache.put("k", &Note { text: "n".into() }).unwrap();
    refused(&mut cache, &path, "k");

    // A cache with no store holds another type at the key.
    let mut memory = CultCache::new();
    memory.register_entry_type::<Counter>().unwrap();
    memory.register_entry_type::<Note>().unwrap();
    memory.put("k", &Note { text: "n".into() }).unwrap();
    let calls = Cell::new(0);
    assert!(memory
        .update::<Counter, _>("k", |current| {
            calls.set(calls.get() + 1);
            current
        })
        .is_err());
    assert_eq!(calls.get(), 0);

    // A foreign record (no registered type claims it) held at the key, and one that appears after the pull.
    let dir = tempfile::tempdir().unwrap();
    let path = path_in(&dir);
    open(&path).put("k", &Note { text: "n".into() }).unwrap();
    let mut only_counter = CultCache::new();
    only_counter.register_entry_type::<Counter>().unwrap();
    only_counter.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&path)).unwrap();
    only_counter.pull_all_backing_stores().unwrap();
    refused(&mut only_counter, &path, "k");
    let mut later = CultCache::new();
    later.register_entry_type::<Counter>().unwrap();
    let dir = tempfile::tempdir().unwrap();
    let path = path_in(&dir);
    later.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&path)).unwrap();
    later.pull_all_backing_stores().unwrap();
    open(&path).put("z", &Note { text: "n".into() }).unwrap();
    let text = refused(&mut later, &path, "z");
    assert!(text.contains("update cannot copy it"), "{text}");

    // A redb home and a custom store without read.
    let dir = tempfile::tempdir().unwrap();
    let redb_path = dir.path().join("store.redb");
    let mut redb = CultCache::new();
    redb.register_entry_type::<Counter>().unwrap();
    redb.add_generic_backing_store(RedbMessagePackBackingStore::new(&redb_path).unwrap()).unwrap();
    redb.pull_all_backing_stores().unwrap();
    redb.put("c", &counter(1)).unwrap();
    let text = refused(&mut redb, &redb_path, "c");
    assert!(text.contains("read and commit_if"), "{text}");
    let path = path_in(&dir);
    let mut custom = CultCache::new();
    custom.register_entry_type::<Counter>().unwrap();
    custom.add_generic_backing_store(ReadlessStore(SingleFileMessagePackBackingStore::new(&path))).unwrap();
    custom.pull_all_backing_stores().unwrap();
    custom.put("c", &counter(1)).unwrap();
    let text = refused(&mut custom, &path, "c");
    assert!(text.contains("read and commit_if"), "{text}");

    // A held read-only record, and a store record with slots the cache never loaded.
    let dir = tempfile::tempdir().unwrap();
    let path = path_in(&dir);
    let mut wide = CultCache::new();
    wide.register_entry_type::<WideCounter>().unwrap();
    wide.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&path)).unwrap();
    wide.pull_all_backing_stores().unwrap();
    wide.put("held", &WideCounter { n: 1, extra: 2 }).unwrap();
    let mut cache = open(&path);
    let text = refused(&mut cache, &path, "held");
    assert!(text.contains("held"), "{text}");
    wide.put("unloaded", &WideCounter { n: 1, extra: 2 }).unwrap();
    refused(&mut cache, &path, "unloaded");

    // An unpulled cache.
    let mut unpulled = CultCache::new();
    unpulled.register_entry_type::<Counter>().unwrap();
    unpulled.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&path)).unwrap();
    refused(&mut unpulled, &path, "held");
}

#[test]
fn a_refusal_after_apply_is_not_retried() {
    let dir = tempfile::tempdir().unwrap();
    let path = path_in(&dir);
    let mut cache = open(&path);
    cache.put("a", &Sku { sku: "S".into() }).unwrap();
    cache.put("b", &Sku { sku: "T".into() }).unwrap();
    let before = file(&path);
    let calls = Cell::new(0);
    let error = cache
        .update::<Sku, _>("b", |_| {
            calls.set(calls.get() + 1);
            Some(Sku { sku: "S".into() })
        })
        .expect_err("a unique-index clash");
    assert_eq!(calls.get(), 1, "{error:#}");
    assert_eq!(file(&path), before);
}

#[test]
fn own_writes_and_the_store_less_cache() {
    let dir = tempfile::tempdir().unwrap();
    let path = path_in(&dir);
    let mut cache = open(&path);
    cache.put("c", &counter(1)).unwrap();
    let calls = Cell::new(0);
    for expected in [2, 3] {
        let result = cache
            .update::<Counter, _>("c", |current| {
                calls.set(calls.get() + 1);
                current.map(|held| counter(held.n + 1))
            })
            .unwrap();
        assert_eq!(result.outcome, CultUpdateOutcome::Updated);
        assert_eq!(result.document, Some(counter(expected)));
        assert_eq!(result.document, cache.get::<Counter>("c").unwrap());
    }
    assert_eq!(calls.get(), 2);

    let mut memory = CultCache::new();
    memory.register_entry_type::<Counter>().unwrap();
    let calls = Cell::new(0);
    let result = memory
        .update::<Counter, _>("c", |current| {
            calls.set(calls.get() + 1);
            Some(counter(current.map_or(0, |held| held.n) + 1))
        })
        .unwrap();
    assert_eq!((calls.get(), result.outcome, result.document), (1, CultUpdateOutcome::Updated, Some(counter(1))));
}

#[test]
fn the_ledger_holds_under_contention() {
    let dir = tempfile::tempdir().unwrap();
    let path = path_in(&dir);
    let start = Arc::new(Barrier::new(3));
    let threads: Vec<_> = (0..3)
        .map(|_| {
            let (path, start) = (path.clone(), start.clone());
            std::thread::spawn(move || {
                let mut cache = open(&path);
                start.wait();
                (0..25)
                    .filter(|_| {
                        let result = cache
                            .update::<Counter, _>("c", |current| Some(counter(current.map_or(0, |held| held.n) + 1)))
                            .unwrap();
                        result.outcome == CultUpdateOutcome::Updated
                    })
                    .count() as u32
            })
        })
        .collect();
    let updated: u32 = threads.into_iter().map(|thread| thread.join().unwrap()).sum();
    assert_eq!(stored(&path, "c"), Some(updated));
}

fn envelope_of<T: DatabaseEntry>(cache: &CultCache, key: &str, value: &T, stamp: &str) -> CultCacheEnvelope {
    let (mut entry, _) = cache.prepare_entry(key, value).unwrap();
    entry.stored_at = stamp.to_string();
    entry
}

const FUTURE: &str = "2099-01-01T00:00:00Z";

fn later_than_future(stamp: &str) -> bool {
    chrono::DateTime::parse_from_rfc3339(stamp).unwrap() > chrono::DateTime::parse_from_rfc3339(FUTURE).unwrap()
}

#[test]
fn commit_if_writes_only_over_the_version_it_was_given() {
    let dir = tempfile::tempdir().unwrap();
    let path = path_in(&dir);
    let cache = open(&path);
    let mut store = SingleFileMessagePackBackingStore::new(&path);
    assert_eq!(store.read("c").unwrap(), None);
    let one = envelope_of(&cache, "c", &counter(1), FUTURE);
    // Against an absent key, expecting a version mismatches; expecting nothing commits.
    assert_eq!(store.commit_if(&one, Some("0000")).unwrap(), CultCommitIf::Mismatch);
    assert_eq!(file(&path), None);
    assert_eq!(store.commit_if(&one, None).unwrap(), CultCommitIf::Committed { stored_at: FUTURE.to_string() });
    let CultStoredRecord { envelope, version } = store.read("c").unwrap().unwrap();
    assert_eq!(envelope.stored_at, FUTURE);

    let before = file(&path);
    let two = envelope_of(&cache, "c", &counter(2), FUTURE);
    assert_eq!(store.commit_if(&two, None).unwrap(), CultCommitIf::Mismatch);
    assert_eq!(store.commit_if(&two, Some("not-the-version")).unwrap(), CultCommitIf::Mismatch);
    assert_eq!(file(&path), before, "a mismatch wrote");
    let CultCommitIf::Committed { stored_at } = store.commit_if(&two, Some(&version)).unwrap() else { panic!("the version matches") };
    assert!(later_than_future(&stored_at), "{stored_at}");
    assert_eq!(stored(&path, "c"), Some(2));
}

#[test]
fn the_cache_facing_writes_stamp_over_a_replaced_record() {
    let seeded = |dir: &tempfile::TempDir| {
        let path = path_in(dir);
        let seed = open(&path);
        let mut store = SingleFileMessagePackBackingStore::new(&path);
        store.push(&envelope_of(&seed, "c", &counter(1), FUTURE)).unwrap();
        (path, seed)
    };
    let on_disk = |path: &Path| {
        let mut store = SingleFileMessagePackBackingStore::new(path);
        let stamp = store.read("c").unwrap().unwrap().envelope.stored_at;
        let _ = &mut store;
        stamp
    };

    // The store: push, push_all and commit_if each return the stamp they wrote, strictly later than what they replaced.
    let dir = tempfile::tempdir().unwrap();
    let (path, seed) = seeded(&dir);
    let mut store = SingleFileMessagePackBackingStore::new(&path);
    let stamp = store.push(&envelope_of(&seed, "c", &counter(2), FUTURE)).unwrap();
    assert!(later_than_future(&stamp) && stamp == on_disk(&path), "push: {stamp}");
    let stamps = store.push_all(&[envelope_of(&seed, "c", &counter(3), FUTURE)], PushAllOptions::default()).unwrap();
    assert!(later_than_future(&stamps[0]) && stamps[0] == on_disk(&path), "push_all: {stamps:?}");
    let version = store.read("c").unwrap().unwrap().version;
    let CultCommitIf::Committed { stored_at } = store.commit_if(&envelope_of(&seed, "c", &counter(4), FUTURE), Some(&version)).unwrap()
    else {
        panic!("matching version")
    };
    assert!(later_than_future(&stored_at) && stored_at == on_disk(&path), "commit_if: {stored_at}");
    // One stamp per input, in order, a duplicate getting the stamp of the entry that landed; a soft write that wrote nothing none.
    let stamps = store
        .push_all(
            &[envelope_of(&seed, "c", &counter(5), FUTURE), envelope_of(&seed, "d", &counter(6), FUTURE), envelope_of(&seed, "c", &counter(7), FUTURE)],
            PushAllOptions::default(),
        )
        .unwrap();
    assert_eq!(stamps.len(), 3);
    assert_eq!(stamps[0], stamps[2]);
    assert_eq!(stamps[1], FUTURE, "a record replacing nothing keeps its stamp");
    assert_eq!(stamps[0], on_disk(&path));
    assert!(store.push_all(&[envelope_of(&seed, "e", &counter(1), FUTURE)], PushAllOptions { soft: true }).unwrap().is_empty());

    // The cache holds the stamp the store wrote after every write entry.
    type Write = fn(&mut CultCache) -> ();
    let writes: [(&str, Write); 5] = [
        ("put", |cache| drop(cache.put("c", &counter(9)).unwrap())),
        ("put_envelope", |cache| {
            let entry = envelope_of(cache, "c", &counter(9), FUTURE);
            drop(cache.put_envelope::<Counter>(entry).unwrap())
        }),
        ("put_raw_envelope", |cache| {
            let entry = envelope_of(cache, "c", &counter(9), FUTURE);
            cache.put_raw_envelope(entry).unwrap()
        }),
        ("put_prepared_batch", |cache| {
            let entry = envelope_of(cache, "c", &counter(9), FUTURE);
            cache.put_prepared_batch(vec![entry]).unwrap()
        }),
        ("update", |cache| drop(cache.update::<Counter, _>("c", |_| Some(counter(9))).unwrap())),
    ];
    for (name, write) in writes {
        let dir = tempfile::tempdir().unwrap();
        let (path, _) = seeded(&dir);
        let mut cache = open(&path);
        write(&mut cache);
        let held = cache.get_envelope::<Counter>("c").unwrap().unwrap().stored_at;
        assert!(later_than_future(&held), "{name}: {held}");
        assert_eq!(held, on_disk(&path), "{name}: the cache holds a stamp the store did not write");
    }
}

#[test]
fn the_byte_exact_writers_keep_the_callers_stamp() {
    let dir = tempfile::tempdir().unwrap();
    let path = path_in(&dir);
    let cache = open(&path);
    let mut store = SingleFileMessagePackBackingStore::new(&path);
    let held = envelope_of(&cache, "c", &counter(1), FUTURE);
    store.push(&held).unwrap();
    let swapped = envelope_of(&cache, "c", &counter(2), "2026-01-01T00:00:00Z");
    assert!(store.compare_and_swap_entry(&held, swapped.clone()).unwrap());
    assert_eq!(store.read("c").unwrap().unwrap().envelope, swapped, "compare_and_swap_entry restamped");

    let redb_path = dir.path().join("store.redb");
    let mut redb = RedbMessagePackBackingStore::new(&redb_path).unwrap();
    redb.push(&held).unwrap();
    let older = envelope_of(&cache, "c", &counter(3), "2026-01-01T00:00:00Z");
    assert_eq!(redb.push(&older).unwrap(), older.stored_at);
    assert_eq!(redb.pull_all().unwrap(), vec![older.clone()]);
    assert_eq!(redb.push_all(std::slice::from_ref(&held), PushAllOptions::default()).unwrap(), vec![held.stored_at.clone()]);
    assert_eq!(redb.pull_all().unwrap(), vec![held]);
}

/// A single-file store that reports whatever stamp it is told to, as a custom store might.
struct ReportingStore {
    inner: SingleFileMessagePackBackingStore,
    report: String,
}

impl CacheBackingStore for ReportingStore {
    fn pull_all(&self) -> anyhow::Result<Vec<CultCacheEnvelope>> {
        self.inner.pull_all()
    }
    fn push(&mut self, entry: &CultCacheEnvelope) -> anyhow::Result<String> {
        self.inner.push(entry)?;
        Ok(self.report.clone())
    }
    fn delete(&mut self, entry: &CultCacheEnvelope) -> anyhow::Result<()> {
        self.inner.delete(entry)
    }
    fn push_all(&mut self, entries: &[CultCacheEnvelope], options: PushAllOptions) -> anyhow::Result<Vec<String>> {
        self.inner.push_all(entries, options)?;
        Ok(entries.iter().map(|_| self.report.clone()).collect())
    }
    fn read(&self, key: &str) -> anyhow::Result<Option<CultStoredRecord>> {
        self.inner.read(key)
    }
    fn commit_if(&mut self, entry: &CultCacheEnvelope, expected: Option<&str>) -> anyhow::Result<CultCommitIf> {
        Ok(match self.inner.commit_if(entry, expected)? {
            CultCommitIf::Committed { .. } => CultCommitIf::Committed { stored_at: self.report.clone() },
            CultCommitIf::Mismatch => CultCommitIf::Mismatch,
        })
    }
}

#[test]
fn the_cache_holds_a_reported_stamp_only_when_it_parses_as_rfc3339() {
    type Write = fn(&mut CultCache);
    let writes: [(&str, Write); 4] = [
        ("put", |cache| drop(cache.put("c", &counter(1)).unwrap())),
        ("put_raw_envelope", |cache| {
            let entry = envelope_of(cache, "c", &counter(1), "2020-01-01T00:00:00Z");
            cache.put_raw_envelope(entry).unwrap()
        }),
        ("put_prepared_batch", |cache| {
            let entry = envelope_of(cache, "c", &counter(1), "2020-01-01T00:00:00Z");
            cache.put_prepared_batch(vec![entry]).unwrap()
        }),
        ("update", |cache| drop(cache.update::<Counter, _>("c", |_| Some(counter(1))).unwrap())),
    ];
    for (report, parses) in [
        ("2030-01-01T00:00:00Z", true),
        ("2030-01-01T00:00:00.123456+00:00", true),
        ("not a stamp", false),
        ("", false),
        ("2030-01-01", false),
        ("2030-01-01 00:00:00", false),
        ("2030-01-01T00:00:00", false),
        ("1893456000", false),
    ] {
        for (name, write) in writes {
            let dir = tempfile::tempdir().unwrap();
            let path = path_in(&dir);
            let mut cache = CultCache::new();
            cache.register_entry_type::<Counter>().unwrap();
            cache
                .add_generic_backing_store(ReportingStore { inner: SingleFileMessagePackBackingStore::new(&path), report: report.to_string() })
                .unwrap();
            cache.pull_all_backing_stores().unwrap();
            write(&mut cache);
            let held = cache.get_envelope::<Counter>("c").unwrap().unwrap().stored_at;
            if parses {
                assert_eq!(held, report, "{name}");
            } else {
                assert_ne!(held, report, "{name} held a stamp that is not RFC 3339");
                assert!(chrono::DateTime::parse_from_rfc3339(&held).is_ok(), "{name}: {held}");
            }
        }
    }
}

#[test]
fn a_stamp_with_no_successor_refuses_every_single_file_write_and_writes_nothing() {
    let dir = tempfile::tempdir().unwrap();
    let path = path_in(&dir);
    let cache = open(&path);
    let mut store = SingleFileMessagePackBackingStore::new(&path);
    let last = "9999-12-31T23:59:59.999999Z";
    store.push(&envelope_of(&cache, "c", &counter(1), last)).unwrap();
    let before = file(&path);
    let version = store.read("c").unwrap().unwrap().version;
    let next = envelope_of(&cache, "c", &counter(2), "2020-01-01T00:00:00Z");
    assert!(store.push(&next).is_err());
    assert!(store.push_all(std::slice::from_ref(&next), PushAllOptions::default()).is_err());
    assert!(store.commit_if(&next, Some(&version)).is_err());
    assert_eq!(file(&path), before, "a refused mint wrote");
}

#[test]
fn commit_if_refuses_a_retype_as_push_does() {
    let dir = tempfile::tempdir().unwrap();
    let path = path_in(&dir);
    let cache = open(&path);
    let mut store = SingleFileMessagePackBackingStore::new(&path);
    let note = envelope_of(&cache, "k", &Note { text: "n".into() }, "2020-01-01T00:00:00Z");
    store.push(&note).unwrap();
    let version = store.read("k").unwrap().unwrap().version;
    let before = file(&path);
    let counter_at_k = envelope_of(&cache, "k", &counter(1), "2020-01-01T00:00:00Z");
    let error = store.commit_if(&counter_at_k, Some(&version)).expect_err("a retype");
    assert!(error.downcast_ref::<cultcache_rs::SchemaConflictError>().is_some(), "{error:#}");
    assert!(store.push(&counter_at_k).is_err());
    assert_eq!(file(&path), before, "a refused retype wrote");
    assert_eq!(store.read("k").unwrap().unwrap().envelope.r#type, "note");
}
