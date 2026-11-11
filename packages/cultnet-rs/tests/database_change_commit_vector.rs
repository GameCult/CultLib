//! `tests/vectors/database-change-commit/two-change-commit.msgpack` is written only by the C# reference: two
//! `cultnet.database_change_raw.v0` messages of one commit, every key present, nil where unset. cultnet-rs has no typed
//! `database_change_raw` message yet, so this walks the stream with the same `rmpv` values its wire decoder builds on and
//! pins the keys and values a Rust adopter will read. A rename of `commitId` or `commitCount` in the C# writer fails here.

use rmpv::Value;
use std::io::Cursor;

fn messages() -> Vec<Value> {
    let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../../tests/vectors/database-change-commit/two-change-commit.msgpack");
    let bytes = std::fs::read(path).expect("the vector is committed");
    let mut cursor = Cursor::new(bytes.as_slice());
    let mut out = Vec::new();
    while (cursor.position() as usize) < bytes.len() {
        out.push(rmpv::decode::read_value(&mut cursor).expect("a msgpack value"));
    }
    out
}

fn field<'a>(message: &'a Value, key: &str) -> &'a Value {
    message
        .as_map()
        .and_then(|entries| entries.iter().find(|(k, _)| k.as_str() == Some(key)))
        .map(|(_, v)| v)
        .unwrap_or_else(|| panic!("the C# writer wrote no key {key}"))
}

#[test]
fn a_two_change_commit_written_by_csharp_carries_its_commit_id_count_and_changes() {
    let messages = messages();
    assert_eq!(messages.len(), 2);
    for message in &messages {
        assert_eq!(field(message, "schemaVersion").as_str(), Some("cultnet.database_change_raw.v0"));
        assert_eq!(field(message, "commitId").as_str(), Some("9f1c0f0e4b7a4c1f9d2e"));
        assert_eq!(field(message, "commitCount").as_u64(), Some(2));
        assert_eq!(field(message, "changeKind").as_str(), Some("removed"));
        assert_eq!(field(message, "subscriptionId").as_str(), Some("sub-1"));
        assert_eq!(field(message, "schemaId").as_str(), Some("schema-note"));
        assert!(field(message, "document").is_nil(), "a removed change's document is nil");
    }
    let keys: Vec<_> = messages.iter().map(|m| field(m, "recordKey").as_str().unwrap().to_string()).collect();
    assert_eq!(keys, ["note:a", "note:b"]);
}
