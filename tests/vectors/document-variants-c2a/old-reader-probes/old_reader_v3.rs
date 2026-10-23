// Copied into a checkout of cultcache-rs-v0.3.1 (02cf04b5) by old-reader-refusal.sh. Not part of this tree's suite.
// The vector is the store C2aGateWriter wrote through CultCacheMessagePack.Create.
use cultcache_rs::{CacheBackingStore, CultCacheEnvelope, PushAllOptions, SingleFileMessagePackBackingStore};

fn copy_of_the_vector() -> (tempfile::TempDir, std::path::PathBuf, Vec<u8>) {
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("store.msgpack");
    let vector = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../tests/vectors/document-variants-c2a/c2a-written-ids.msgpack");
    std::fs::copy(vector, &path).unwrap();
    let bytes = std::fs::read(&path).unwrap();
    (temp, path, bytes)
}

fn envelope() -> CultCacheEnvelope {
    CultCacheEnvelope {
        key: "probe".into(),
        r#type: "probe".into(),
        payload: vec![0x80],
        stored_at: "2026-10-22T00:00:00Z".into(),
        schema_id: None,
    }
}

fn refused_v3(label: &str, error: anyhow::Error) {
    let message = format!("{error:#}");
    println!("OLD-READER rust refused {label}: {message}");
    assert!(message.contains("cultcache.store.v3"), "{message}");
}

#[test]
fn old_rust_reader_refuses_the_c2a_element_id_store_at_read() {
    let (_temp, path, bytes) = copy_of_the_vector();
    refused_v3("pull_all", SingleFileMessagePackBackingStore::new(&path).pull_all().unwrap_err());
    assert_eq!(std::fs::read(&path).unwrap(), bytes);
}

#[test]
fn old_rust_reader_refuses_a_per_record_push_to_the_c2a_element_id_store() {
    let (_temp, path, bytes) = copy_of_the_vector();
    refused_v3("push", SingleFileMessagePackBackingStore::new(&path).push(&envelope()).unwrap_err());
    assert_eq!(std::fs::read(&path).unwrap(), bytes);
}

#[test]
fn old_rust_reader_refuses_a_whole_store_push_all_over_the_c2a_element_id_store() {
    let (_temp, path, bytes) = copy_of_the_vector();
    let error = SingleFileMessagePackBackingStore::new(&path)
        .push_all(std::slice::from_ref(&envelope()), PushAllOptions::default())
        .unwrap_err();
    refused_v3("push_all", error);
    assert_eq!(std::fs::read(&path).unwrap(), bytes);
}
