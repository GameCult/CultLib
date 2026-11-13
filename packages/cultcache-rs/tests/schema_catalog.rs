//! A consumer of a single-file store reads the schema version a record was published under from the store's catalog, through the
//! public surface only: `pull_all`, `pull_schema_catalog` and `resolve_schema_entry`
//! (`cultlib-gaps:follow_up:rs-snapshot-schema-version`).

use std::path::PathBuf;

use cultcache_rs::{
    CacheBackingStore, CultCache, DatabaseEntry, SchemaCatalogEntry, SingleFileMessagePackBackingStore, resolve_schema_entry,
};

#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "tests.sc_one", schema = "tests.sc_one")]
struct One {
    #[cultcache(key = 0)]
    text: String,
}

#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "tests.sc_two", schema = "tests.sc_two")]
struct Two {
    #[cultcache(key = 0)]
    text: String,
}

fn vectors(folder: &str) -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../tests/vectors").join(folder)
}

/// The version a store publishes for each record, in key order: `(key, schema name, schema version)`.
fn versions(store: &SingleFileMessagePackBackingStore) -> Vec<(String, String, String)> {
    let catalog = store.pull_schema_catalog().unwrap();
    let mut rows: Vec<_> = store
        .pull_all()
        .unwrap()
        .iter()
        .map(|envelope| {
            let id = envelope.schema_id.as_deref().unwrap();
            let entry = resolve_schema_entry(&catalog, id).unwrap_or_else(|| panic!("no entry for {id}"));
            (envelope.key.clone(), entry.schema_name.clone(), entry.schema_version.clone())
        })
        .collect();
    rows.sort();
    rows
}

fn copy(from: PathBuf) -> (tempfile::TempDir, SingleFileMessagePackBackingStore) {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("store.cc");
    std::fs::copy(from, &path).unwrap();
    (dir, SingleFileMessagePackBackingStore::new(&path))
}

#[test]
fn a_consumer_reads_the_version_of_each_type_a_rust_store_holds() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("store.cc");
    let mut cache = CultCache::new();
    cache.register_entry_type::<One>().unwrap();
    cache.register_entry_type::<Two>().unwrap();
    cache.add_backing_store(SingleFileMessagePackBackingStore::new(&path), ["tests.sc_one", "tests.sc_two"]).unwrap();
    cache.pull_all_backing_stores().unwrap();
    cache.put("a", &One { text: "1".into() }).unwrap();
    cache.put("b", &Two { text: "2".into() }).unwrap();
    drop(cache);

    let consumer = SingleFileMessagePackBackingStore::new(&path);
    assert_eq!(
        versions(&consumer),
        vec![
            ("a".to_string(), "tests.sc_one".to_string(), "tests.sc_one.v1".to_string()),
            ("b".to_string(), "tests.sc_two".to_string(), "tests.sc_two.v1".to_string()),
        ]
    );
}

#[test]
fn an_id_only_an_entry_lists_as_compatible_resolves_to_that_entry() {
    let (_dir, store) = copy(vectors("document-variants-c2a/readability/compatible-id-only-v3.bin"));
    let catalog = store.pull_schema_catalog().unwrap();
    assert_eq!(catalog.len(), 1);
    assert_eq!(catalog[0].schema_id, "vectors.item.next");
    assert_eq!(catalog[0].compatible_schema_ids, vec!["vectors.item".to_string()]);
    let entry = resolve_schema_entry(&catalog, "vectors.item").unwrap();
    assert_eq!(entry.schema_id, "vectors.item.next");
    assert_eq!(entry.schema_version, "vectors.item.v1");
    assert_eq!(resolve_schema_entry(&catalog, "vectors.nowhere"), None);
}

#[test]
fn an_id_an_entry_owns_resolves_to_its_owner_not_to_an_entry_listing_it() {
    for vector in ["own-id-over-compatible-v3.bin", "compatible-before-owner-v3.bin"] {
        let (_dir, store) = copy(vectors("document-variants-c2a/readability").join(vector));
        let catalog = store.pull_schema_catalog().unwrap();
        assert_eq!(catalog.len(), 2, "{vector}");
        let entry = resolve_schema_entry(&catalog, "vectors.item").unwrap();
        assert_eq!((entry.schema_id.as_str(), entry.schema_version.as_str()), ("vectors.item", "vectors.item.v1"), "{vector}");
    }
}

#[test]
fn a_legacy_array_and_an_absent_file_publish_no_catalog() {
    let (_dir, store) = copy(vectors("document-variants-c2a/readability/legacy-envelopes.bin"));
    assert!(!store.pull_all().unwrap().is_empty());
    assert_eq!(store.pull_schema_catalog().unwrap(), Vec::<SchemaCatalogEntry>::new());

    let dir = tempfile::tempdir().unwrap();
    let absent = SingleFileMessagePackBackingStore::new(dir.path().join("none.cc"));
    assert_eq!(absent.pull_schema_catalog().unwrap(), Vec::<SchemaCatalogEntry>::new());
    assert!(!dir.path().join("none.cc").exists() && !dir.path().join("none.cc.lock").exists(), "reading an absent store creates nothing");
}

// `tests/vectors/key-identity/foreign-store.cc` is written by the C# reference: two types, each published with its version.
#[test]
fn a_store_the_csharp_reference_wrote_gives_its_versions() {
    let (_dir, store) = copy(vectors("key-identity/foreign-store.cc"));
    assert_eq!(
        versions(&store),
        vec![
            ("j".to_string(), "tests.ki_alpha".to_string(), "tests.ki_alpha.v1".to_string()),
            ("k".to_string(), "tests.ki_gamma".to_string(), "tests.ki_gamma.v1".to_string()),
        ]
    );
    let catalog = store.pull_schema_catalog().unwrap();
    assert_eq!(catalog[0].content_hash, "sha256:02f0c8b7ea07c11705c1365fc29136ada08c8076209cacfb8d5526229461ce7a");
}

type Entry = (&'static str, &'static str, &'static str, &'static str, &'static str, Vec<&'static str>, Vec<()>);

// A hand-built v3 store whose versions are not the `<name>.v1` the writers above publish, so a version read from anywhere but the
// version slot cannot pass. `shared` is listed by two entries (the first lister wins); `id.c` is owned by one entry and listed by
// an earlier one (the owner wins).
#[test]
fn distinct_versions_come_through_with_the_decoders_resolution() {
    let catalog: Vec<Entry> = vec![
        ("id.a", "N.a", "9.9.9", "hash-a", "{}", vec!["id.a", "shared"], vec![]),
        ("id.b", "N.b", "7.0", "hash-b", "{}", vec!["id.b", "shared", "id.c"], vec![]),
        ("id.c", "N.c", "3", "hash-c", "{}", vec!["id.c"], vec![]),
    ];
    let record = |key: &str, id: &str| (key.to_string(), id.to_string(), "2026-11-13T00:00:00Z".to_string(), b"x".to_vec());
    let records = vec![record("r1", "shared"), record("r2", "id.c"), record("r3", "id.b"), record("r4", "id.a")];
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("store.cc");
    std::fs::write(&path, rmp_serde::to_vec(&("cultcache.store.v3", catalog, records)).unwrap()).unwrap();

    let store = SingleFileMessagePackBackingStore::new(&path);
    let listed = store.pull_schema_catalog().unwrap();
    assert_eq!(listed.iter().map(|e| e.schema_version.as_str()).collect::<Vec<_>>(), vec!["9.9.9", "7.0", "3"]);
    assert_eq!(listed[1].content_hash, "hash-b");
    assert_eq!(listed[1].compatible_schema_ids, vec!["id.b", "shared", "id.c"]);
    let got: Vec<(String, String, String)> = {
        let mut rows: Vec<_> = store
            .pull_all()
            .unwrap()
            .iter()
            .map(|e| {
                let entry = resolve_schema_entry(&listed, e.schema_id.as_deref().unwrap()).unwrap();
                (e.key.clone(), entry.schema_name.clone(), entry.schema_version.clone())
            })
            .collect();
        rows.sort();
        rows
    };
    let row = |k: &str, n: &str, v: &str| (k.to_string(), n.to_string(), v.to_string());
    assert_eq!(got, vec![row("r1", "N.a", "9.9.9"), row("r2", "N.c", "3"), row("r3", "N.b", "7.0"), row("r4", "N.a", "9.9.9")]);
}
