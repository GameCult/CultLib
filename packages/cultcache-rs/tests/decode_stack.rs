//! Typed decode never aborts a payload inside the nesting bound, whatever the caller's stack.
//! Each test runs its decode in a child test process on a thread of a stated size, so an abort
//! fails one named test. Values are built, encoded, compared and dropped on a large thread; only
//! the decode runs on the small one.

use cultcache_rs::CultSlots;
use cultcache_rs::MAX_PAYLOAD_DEPTH;
use cultcache_rs::PayloadTooDeep;
use cultcache_rs::decode_payload;
use serde::de::DeserializeOwned;

const CHILD_MARKER: &str = "CULTCACHE_DECODE_STACK_CHILD";
const KIB: usize = 1024;

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

#[derive(Clone, Debug, PartialEq, CultSlots)]
struct One {
    #[cultcache(key = 0)]
    a: u8,
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
struct DynamicSlot {
    #[cultcache(key = 0)]
    v: serde_json::Value,
}

/// Runs `body` on a thread with a stack of `bytes`, returning what it returns.
fn on_stack<R: Send + 'static>(bytes: usize, body: impl FnOnce() -> R + Send + 'static) -> R {
    std::thread::Builder::new().stack_size(bytes).spawn(body).unwrap().join().unwrap()
}

/// Runs `body` in a child process that runs only the test `name`; the parent passes when the
/// child exits cleanly having run exactly that one test. An abort in the child fails the parent.
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
        "child {name} failed: {:?}
{stdout}
{stderr}",
        output.status
    );
}

/// A chain of `levels` Wide32 records: `levels` containers deep, plus one for its last arrays.
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

fn decode_on<T: DeserializeOwned + Send + 'static>(stack: usize, bytes: Vec<u8>) -> anyhow::Result<T> {
    on_stack(stack, move || decode_payload::<T>(&bytes))
}

#[test]
fn a_wide_record_at_the_limit_decodes_on_a_1mib_thread() {
    in_child("a_wide_record_at_the_limit_decodes_on_a_1mib_thread", || {
        let bytes = on_stack(64 * KIB * KIB, || {
            rmp_serde::to_vec(&wide_chain(MAX_PAYLOAD_DEPTH - 1)).unwrap()
        });
        let decoded = decode_on::<Wide32>(1024 * KIB, bytes).unwrap();
        on_stack(64 * KIB * KIB, move || {
            assert!(decoded == wide_chain(MAX_PAYLOAD_DEPTH - 1));
        });
    });
}

#[test]
fn one_past_the_limit_is_refused_typed_on_a_1mib_thread() {
    in_child("one_past_the_limit_is_refused_typed_on_a_1mib_thread", || {
        let bytes = on_stack(64 * KIB * KIB, || {
            rmp_serde::to_vec(&wide_chain(MAX_PAYLOAD_DEPTH)).unwrap()
        });
        let error = decode_on::<Wide32>(1024 * KIB, bytes).unwrap_err();
        assert_eq!(
            error.downcast_ref::<PayloadTooDeep>(),
            Some(&PayloadTooDeep { depth: MAX_PAYLOAD_DEPTH + 1, limit: MAX_PAYLOAD_DEPTH })
        );
    });
}

#[test]
fn a_foreign_value_at_the_limit_decodes_on_a_64kib_thread() {
    in_child("a_foreign_value_at_the_limit_decodes_on_a_64kib_thread", || {
        // The slot's array is depth 1, so 255 nested arrays under it reach depth 256.
        let value = on_stack(64 * KIB * KIB, || {
            let mut value = serde_json::Value::Null;
            for _ in 0..MAX_PAYLOAD_DEPTH - 1 {
                value = serde_json::Value::Array(vec![value]);
            }
            value
        });
        let expected = DynamicSlot { v: value };
        let bytes = on_stack(64 * KIB * KIB, {
            let expected = expected.clone();
            move || rmp_serde::to_vec(&expected).unwrap()
        });
        let decoded = decode_on::<DynamicSlot>(64 * KIB, bytes).unwrap();
        on_stack(64 * KIB * KIB, move || {
            assert!(decoded == expected);
            drop(expected);
        });
    });
}

#[test]
fn a_skipped_slot_at_the_limit_decodes_on_a_64kib_thread() {
    in_child("a_skipped_slot_at_the_limit_decodes_on_a_64kib_thread", || {
        // [1, [[...]]]: slot 1 is unknown to One and is skipped; the whole payload is 256 deep.
        let mut bytes = vec![0x92u8, 0x01];
        bytes.extend(std::iter::repeat_n(0x91u8, MAX_PAYLOAD_DEPTH - 2));
        bytes.push(0x90);
        let decoded = decode_on::<One>(64 * KIB, bytes).unwrap();
        assert_eq!(decoded, One { a: 1 });
    });
}
