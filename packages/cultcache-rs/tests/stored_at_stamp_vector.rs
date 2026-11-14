//! `tests/vectors/stored-at-stamp`: the one shape a peer's `storedAt` must parse as. Every row is walked against
//! `is_stored_at_stamp`, and each row that is no stamp against the cache's two raw doors, `put_raw_envelope` and
//! `put_prepared_batch`: refused, nothing stored or minted, the store still writable after
//! (`variants:ruling:raw-put-without-stamp-refused`).

use std::path::PathBuf;

use cultcache_rs::{CacheBackingStore, CultCache, DatabaseEntry, SingleFileMessagePackBackingStore, is_stored_at_stamp};

#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "tests.stamp_note", schema = "tests.stamp_note")]
struct Note {
    #[cultcache(key = 0)]
    text: String,
}

fn rows() -> Vec<(String, bool)> {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../tests/vectors/stored-at-stamp/stamps.msgpack");
    rmp_serde::from_slice(&std::fs::read(path).unwrap()).unwrap()
}

#[test]
fn the_stamp_check_agrees_with_every_vector_row() {
    let rows = rows();
    assert!(rows.len() > 30, "the vector was not read");
    for (text, parses) in rows {
        assert_eq!(is_stored_at_stamp(&text), parses, "{text:?}");
    }
}

#[test]
fn a_raw_envelope_that_is_no_stamp_is_refused_and_nothing_is_stored() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("stamps.cc");
    let mut cache = CultCache::new();
    cache.register_entry_type::<Note>().unwrap();
    cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&path)).unwrap();
    cache.pull_all_backing_stores().unwrap();
    for (text, parses) in rows() {
        if parses {
            continue;
        }
        let (mut entry, _) = cache.prepare_entry("a", &Note { text: "peer".into() }).unwrap();
        entry.stored_at = text.clone();
        let error = cache.put_raw_envelope(entry.clone()).unwrap_err();
        assert!(format!("{error:#}").contains("stored_at"), "{text:?}: {error:#}");
        let error = cache.put_prepared_batch(vec![entry]).unwrap_err();
        assert!(format!("{error:#}").contains("stored_at"), "{text:?}: {error:#}");
        assert!(cache.get::<Note>("a").unwrap().is_none(), "{text:?} was stored");
        assert!(!path.exists() || SingleFileMessagePackBackingStore::new(&path).pull_all().unwrap().is_empty(), "{text:?} reached the file");
    }
    // Refused writes left the store writable: a stamped one lands under the stamp it carries.
    let (mut entry, _) = cache.prepare_entry("b", &Note { text: "stamped".into() }).unwrap();
    entry.stored_at = "2026-11-07T00:00:00Z".into();
    cache.put_raw_envelope(entry).unwrap();
    assert_eq!(cache.get::<Note>("b").unwrap().unwrap().text, "stamped");
    assert!(cache.get::<Note>("a").unwrap().is_none());
}
