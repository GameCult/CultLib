//! `tests/vectors/content-version`, written only by the C# reference: the content version of a stored record is the lowercase
//! hex SHA-256 of its persisted encoding (`variants:ruling:write-set-content-version-declared-ownership`). The store reports
//! the version of every record of the committed `store.cc` as `versions.msgpack` says.

use std::path::PathBuf;

use cultcache_rs::{CacheBackingStore, SingleFileMessagePackBackingStore};

fn vectors() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../tests/vectors/content-version")
}

#[test]
fn read_reports_every_version_the_reference_reads() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("store.cc");
    std::fs::copy(vectors().join("store.cc"), &path).unwrap();
    let rows: Vec<(String, String)> = rmp_serde::from_slice(&std::fs::read(vectors().join("versions.msgpack")).unwrap()).unwrap();
    assert!(rows.len() >= 2);
    let store = SingleFileMessagePackBackingStore::new(&path);
    for (key, version) in rows {
        let record = store.read(&key).unwrap().unwrap_or_else(|| panic!("no record {key}"));
        assert_eq!(record.version, version, "{key}");
    }
    assert_eq!(store.read("absent").unwrap(), None);
}
