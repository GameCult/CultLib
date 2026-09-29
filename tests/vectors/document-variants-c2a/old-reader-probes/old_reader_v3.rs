// Copied into a checkout of the C0 merge (e382bb4) by old-reader-refusal.sh. Not part of this tree's suite.
use cultcache_rs::{CacheBackingStore, SingleFileMessagePackBackingStore};

#[test]
fn old_rust_reader_refuses_the_v3_element_id_store_by_header() {
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("store.msgpack");
    let vector = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../tests/vectors/document-variants-c2a/v3-base.msgpack");
    std::fs::copy(vector, &path).unwrap();
    let error = SingleFileMessagePackBackingStore::new(&path).pull_all().unwrap_err();
    let message = format!("{error:#}");
    println!("OLD-READER rust refused: {message}");
    assert!(message.contains("cultcache.store.v3"), "{message}");
}
