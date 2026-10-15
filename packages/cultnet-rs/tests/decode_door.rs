//! Every MessagePack byte cultnet-rs receives decodes once through `cultcache_rs::decode_payload`:
//! a message, the row arrays of `snapshot_response_raw.v1`, a raw document payload applied to a cache,
//! and an immutable identity credential. Each test decodes in a child test process on a thread of a
//! stated size, so an abort fails one named test; values are built, encoded and compared on a large
//! thread, and only the decode runs on the small one.

use anyhow::Result;
use cultcache_rs::CultCache;
use cultcache_rs::CultSlots;
use cultcache_rs::DatabaseEntry;
use cultcache_rs::MAX_PAYLOAD_DEPTH;
use cultcache_rs::PayloadTooDeep;
use cultnet_rs::*;

const CHILD_MARKER: &str = "CULTNET_DECODE_DOOR_CHILD";
const SMALL: usize = 256 * 1024;
const BIG: usize = 64 * 1024 * 1024;
/// A five-byte MessagePack string standing where a test wants its nested value spliced in.
const SENTINEL: [u8; 5] = [0xa4, b'D', b'E', b'E', b'P'];

#[derive(Clone, Debug, PartialEq, CultSlots)]
struct Wide32 {
    #[cultcache(key = 0)]
    a0: [u64; 32],
    #[cultcache(key = 1)]
    a1: [u64; 32],
    #[cultcache(key = 2)]
    a2: [u64; 32],
    #[cultcache(key = 3)]
    a3: [u64; 32],
    #[cultcache(key = 4)]
    a4: [u64; 32],
    #[cultcache(key = 5)]
    a5: [u64; 32],
    #[cultcache(key = 6)]
    a6: [u64; 32],
    #[cultcache(key = 7)]
    a7: [u64; 32],
    #[cultcache(key = 8)]
    a8: [u64; 32],
    #[cultcache(key = 9)]
    a9: [u64; 32],
    #[cultcache(key = 10)]
    a10: [u64; 32],
    #[cultcache(key = 11)]
    a11: [u64; 32],
    #[cultcache(key = 12)]
    a12: [u64; 32],
    #[cultcache(key = 13)]
    a13: [u64; 32],
    #[cultcache(key = 14)]
    a14: [u64; 32],
    #[cultcache(key = 15)]
    a15: [u64; 32],
    #[cultcache(key = 16)]
    a16: [u64; 32],
    #[cultcache(key = 17)]
    a17: [u64; 32],
    #[cultcache(key = 18)]
    a18: [u64; 32],
    #[cultcache(key = 19)]
    a19: [u64; 32],
    #[cultcache(key = 20)]
    a20: [u64; 32],
    #[cultcache(key = 21)]
    a21: [u64; 32],
    #[cultcache(key = 22)]
    a22: [u64; 32],
    #[cultcache(key = 23)]
    a23: [u64; 32],
    #[cultcache(key = 24)]
    a24: [u64; 32],
    #[cultcache(key = 25)]
    a25: [u64; 32],
    #[cultcache(key = 26)]
    a26: [u64; 32],
    #[cultcache(key = 27)]
    a27: [u64; 32],
    #[cultcache(key = 28)]
    a28: [u64; 32],
    #[cultcache(key = 29)]
    a29: [u64; 32],
    #[cultcache(key = 30)]
    a30: [u64; 32],
    #[cultcache(key = 31)]
    a31: [u64; 32],
    #[cultcache(key = 32)]
    next: Option<Box<Wide32>>,
}

#[derive(Clone, Debug, PartialEq, DatabaseEntry)]
#[cultcache(type = "cultnet.decode-door.wide", schema = "DecodeDoorWide")]
struct WideDocument {
    #[cultcache(key = 0)]
    chain: Wide32,
}

fn on_stack<R: Send + 'static>(bytes: usize, body: impl FnOnce() -> R + Send + 'static) -> R {
    std::thread::Builder::new().stack_size(bytes).spawn(body).unwrap().join().unwrap()
}

/// Runs `body` in a child process that runs only the test `name`; the parent passes when the child
/// exits cleanly having run exactly that one test. An abort in the child fails the parent.
fn in_child(name: &str, body: impl FnOnce()) {
    if std::env::var_os(CHILD_MARKER).is_some() {
        body();
        return;
    }
    let output = std::process::Command::new(std::env::current_exe().unwrap())
        .args([name, "--exact", "--nocapture", "--test-threads=1"])
        .env(CHILD_MARKER, "1")
        .output()
        .unwrap();
    let stdout = String::from_utf8_lossy(&output.stdout);
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(
        output.status.success() && stdout.contains("1 passed"),
        "child {name} failed: {:?}\n{stdout}\n{stderr}",
        output.status
    );
}

/// `levels` one-element arrays around a nil, from marker bytes: the encoders recurse too.
fn nested(levels: usize) -> Vec<u8> {
    let mut bytes = vec![0x91; levels];
    bytes.push(0xc0);
    bytes
}

/// Swaps the one sentinel string in `bytes` for `with`.
fn splice(bytes: Vec<u8>, with: &[u8]) -> Vec<u8> {
    let at = bytes
        .windows(SENTINEL.len())
        .position(|window| window == SENTINEL)
        .expect("sentinel present");
    [&bytes[..at], with, &bytes[at + SENTINEL.len()..]].concat()
}

fn map_with_sentinel(mut entries: Vec<(rmpv::Value, rmpv::Value)>) -> Vec<u8> {
    entries.push(("x".into(), "DEEP".into()));
    rmp_serde::to_vec(&rmpv::Value::Map(entries)).unwrap()
}

fn hello_with(unknown: &[u8]) -> Vec<u8> {
    splice(
        map_with_sentinel(vec![
            ("schemaVersion".into(), "cultnet.hello.v0".into()),
            ("runtimeId".into(), "rt".into()),
            ("runtimeKind".into(), "test".into()),
        ]),
        unknown,
    )
}

fn decode_message(bytes: Vec<u8>, stack: usize) -> Result<CultNetMessage> {
    *on_stack(stack, move || {
        Box::new(decode_cultnet_message_from_slice(&bytes, CultNetWireContract::CultNetSchemaV0))
    })
}

#[test]
fn generic_message_with_deep_unknown_key_decodes_on_a_small_thread() {
    in_child("generic_message_with_deep_unknown_key_decodes_on_a_small_thread", || {
        let plain = decode_message(hello_with(&nested(0)), SMALL).unwrap();
        let deep = decode_message(hello_with(&nested(MAX_PAYLOAD_DEPTH - 2)), SMALL).unwrap();
        assert_eq!(deep, plain);
    });
}

#[test]
fn message_past_the_bound_is_refused_typed() {
    in_child("message_past_the_bound_is_refused_typed", || {
        let error = decode_message(hello_with(&nested(MAX_PAYLOAD_DEPTH + 1)), SMALL).unwrap_err();
        let refusal = error.downcast_ref::<PayloadTooDeep>().expect("typed refusal");
        assert_eq!(refusal.limit, MAX_PAYLOAD_DEPTH);
    });
}

fn raw_v1_bytes(header_unknown: Option<&[u8]>) -> Vec<u8> {
    let mut header: Vec<(rmpv::Value, rmpv::Value)> = vec![
        ("schemaId".into(), "s".into()),
        ("recordKey".into(), "k".into()),
        ("storedAt".into(), "2026-10-16T00:00:00Z".into()),
    ];
    if header_unknown.is_some() {
        header.push(("x".into(), "DEEP".into()));
    }
    let message = rmpv::Value::Map(vec![
        ("schemaVersion".into(), "cultnet.snapshot_response_raw.v1".into()),
        ("messageId".into(), "m".into()),
        ("matched".into(), 1.into()),
        ("asOf".into(), 7.into()),
        ("headers".into(), rmpv::Value::Array(vec![rmpv::Value::Map(header)])),
    ]);
    let bytes = rmp_serde::to_vec(&message).unwrap();
    match header_unknown {
        Some(nested) => splice(bytes, nested),
        None => bytes,
    }
}

#[test]
fn raw_v1_rows_with_deep_unknown_key_decode_on_a_small_thread() {
    in_child("raw_v1_rows_with_deep_unknown_key_decode_on_a_small_thread", || {
        let plain = decode_message(raw_v1_bytes(None), SMALL).unwrap();
        // The message map, the headers array and the header map sit above the unknown key.
        let deep = decode_message(raw_v1_bytes(Some(&nested(MAX_PAYLOAD_DEPTH - 3))), SMALL).unwrap();
        let CultNetMessage::SnapshotResponseRawV1 { headers: Some(rows), .. } = &deep else {
            panic!("expected raw v1 rows");
        };
        assert_eq!(rows.len(), 1);
        assert_eq!(rows[0].record_key, "k");
        assert_eq!(deep, plain);
    });
}

fn wide_chain(levels: usize) -> Wide32 {
    let mut chain: Option<Box<Wide32>> = None;
    for level in (0..levels).rev() {
        let mut node = Wide32 { next: chain, ..empty_wide() };
        node.a0[0] = level as u64;
        chain = Some(Box::new(node));
    }
    *chain.unwrap()
}

fn empty_wide() -> Wide32 {
    Wide32 {
        a0: [0; 32],
        a1: [0; 32],
        a2: [0; 32],
        a3: [0; 32],
        a4: [0; 32],
        a5: [0; 32],
        a6: [0; 32],
        a7: [0; 32],
        a8: [0; 32],
        a9: [0; 32],
        a10: [0; 32],
        a11: [0; 32],
        a12: [0; 32],
        a13: [0; 32],
        a14: [0; 32],
        a15: [0; 32],
        a16: [0; 32],
        a17: [0; 32],
        a18: [0; 32],
        a19: [0; 32],
        a20: [0; 32],
        a21: [0; 32],
        a22: [0; 32],
        a23: [0; 32],
        a24: [0; 32],
        a25: [0; 32],
        a26: [0; 32],
        a27: [0; 32],
        a28: [0; 32],
        a29: [0; 32],
        a30: [0; 32],
        a31: [0; 32],
        next: None,
    }
}

fn registry() -> CultNetDocumentRegistry {
    let mut registry = CultNetDocumentRegistry::new();
    registry.register(CultNetDocumentBinding::for_entry::<WideDocument>("cultnet.decode-door.wide".to_string()));
    registry
}

/// The wire bytes of a raw put of a `levels`-deep Wide32 document, built on a large thread.
fn raw_put_bytes(levels: usize) -> Vec<u8> {
    on_stack(BIG, move || {
        let document = WideDocument { chain: wide_chain(levels) };
        let message = registry()
            .create_raw_document_put_message("m", "k", &document, CultNetDocumentPutOptions::default())
            .unwrap();
        encode_cultnet_message_to_vec(&message, CultNetWireContract::CultNetSchemaV0).unwrap()
    })
}

/// Receives and applies on the small thread; the applied record is boxed there.
fn receive_and_apply(bytes: Vec<u8>) -> Result<WideDocument> {
    *on_stack(SMALL, move || {
        Box::new((|| {
            let message = decode_cultnet_message_from_slice(&bytes, CultNetWireContract::CultNetSchemaV0)?;
            let mut cache = CultCache::new();
            cache.register_entry_type::<WideDocument>()?;
            registry().apply_raw_document_put_message::<WideDocument>(&mut cache, &message)
        })())
    })
}

#[test]
fn raw_put_of_a_wide32_chain_applies_on_a_small_thread() {
    in_child("raw_put_of_a_wide32_chain_applies_on_a_small_thread", || {
        let levels = MAX_PAYLOAD_DEPTH - 2;
        let applied = receive_and_apply(raw_put_bytes(levels)).unwrap();
        on_stack(BIG, move || assert!(applied.chain == wide_chain(levels)));
        let error = receive_and_apply(raw_put_bytes(levels + 1)).unwrap_err();
        assert!(error.downcast_ref::<PayloadTooDeep>().is_some(), "typed refusal");
    });
}

#[test]
fn retired_dynamic_put_is_refused_typed() {
    in_child("retired_dynamic_put_is_refused_typed", || {
        let document = rmpv::Value::Map(vec![
            ("schemaId".into(), "s".into()),
            ("recordKey".into(), "k".into()),
            ("storedAt".into(), "2026-10-16T00:00:00Z".into()),
            ("payload".into(), "DEEP".into()),
        ]);
        let bytes = splice(
            rmp_serde::to_vec(&rmpv::Value::Map(vec![
                ("schemaVersion".into(), "cultnet.document_put.v0".into()),
                ("messageId".into(), "m".into()),
                ("document".into(), document),
            ]))
            .unwrap(),
            &nested(253),
        );
        let error = decode_message(bytes, SMALL).unwrap_err();
        // The refusal keeps its kind and never quotes the received variant name.
        assert!(!format!("{error:#}").contains("document_put"), "{error:#}");
    });
}

#[test]
fn identity_credential_with_deep_key_is_refused_on_a_small_thread() {
    in_child("identity_credential_with_deep_key_is_refused_on_a_small_thread", || {
        let temp = tempfile::tempdir().unwrap();
        let path = temp.path().join("identity.cc");
        enroll_service_identity_at::<IdunnServiceIdentity>(&path).unwrap();
        let mut envelopes: rmpv::Value = rmp_serde::from_slice(&std::fs::read(path).unwrap()).unwrap();
        let rmpv::Value::Map(envelope) = &mut envelopes.as_array_mut().unwrap()[0] else {
            panic!("envelope is a map");
        };
        envelope.push(("zz".into(), "DEEP".into()));
        let credential = rmp_serde::to_vec(&envelopes).unwrap();
        for levels in [1000, 250] {
            let bytes = splice(credential.clone(), &nested(levels));
            let refused = on_stack(SMALL, move || {
                open_service_identity_credential_bytes::<IdunnServiceIdentity>(&bytes).is_err()
            });
            assert!(refused, "{levels} levels deep");
        }
    });
}
