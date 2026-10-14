//! Typed decode never aborts a payload inside the nesting bound, whatever the caller's stack, for
//! every record the derives accept (up to `MAX_RECORD_INLINE_BYTES` inline); envelope reads of a
//! store go through the same grown decoder.
//! Each test runs its decode in a child test process on a thread of a stated size, so an abort
//! fails one named test. Values are built, encoded, compared and dropped on a large thread; only
//! the decode runs on the small one.

use cultcache_rs::CultSlots;
use cultcache_rs::CultUnion;
use cultcache_rs::MAX_PAYLOAD_DEPTH;
use cultcache_rs::MAX_RECORD_INLINE_BYTES;
use cultcache_rs::PayloadTooDeep;
use cultcache_rs::SingleFileMessagePackBackingStore;
use cultcache_rs::check_payload_depth;
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

/// The decode alone runs on the small thread. Its result is boxed there so that the thread's own
/// return machinery, which would hold several more copies of a large record in a debug build, does
/// not count against the stack under test; what the caller's frames hold is the caller's to size.
fn decode_on<T: DeserializeOwned + Send + 'static>(stack: usize, bytes: Vec<u8>) -> anyhow::Result<T> {
    *on_stack(stack, move || Box::new(decode_payload::<T>(&bytes)))
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

// A record the derives accept at the bound: two slots of 31 arrays of 32 words, 15,880 bytes inline.
#[derive(Clone, Debug, PartialEq, CultSlots)]
struct Largest {
    #[cultcache(key = 0)]
    a: [[u64; 32]; 31],
    #[cultcache(key = 1)]
    b: [[u64; 32]; 31],
    #[cultcache(key = 2)]
    next: Option<Box<Largest>>,
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
struct LargestUnionArm {
    #[cultcache(key = 0)]
    a: [[u64; 32]; 31],
    #[cultcache(key = 1)]
    b: [[u64; 32]; 31],
    #[cultcache(key = 2)]
    next: Option<Box<LargestUnion>>,
}

#[derive(Clone, Debug, PartialEq, CultUnion)]
enum LargestUnion {
    #[cultcache(key = 0)]
    Arm(LargestUnionArm),
    #[cultcache(key = 1)]
    Leaf,
}

// The fixtures sit within a KiB under the bound, so a decode that survives them survives any record.
const _: () = assert!(
    std::mem::size_of::<Largest>() <= MAX_RECORD_INLINE_BYTES
        && std::mem::size_of::<Largest>() + KIB >= MAX_RECORD_INLINE_BYTES
        && std::mem::size_of::<LargestUnion>() <= MAX_RECORD_INLINE_BYTES
        && std::mem::size_of::<LargestUnion>() + KIB >= MAX_RECORD_INLINE_BYTES
);

fn largest_chain(levels: usize) -> Largest {
    let mut chain: Option<Box<Largest>> = None;
    for level in (0..levels).rev() {
        let mut a = [[0u64; 32]; 31];
        a[0][0] = level as u64;
        chain = Some(Box::new(Largest { a, b: [[0; 32]; 31], next: chain }));
    }
    *chain.unwrap()
}

fn union_chain(levels: usize) -> LargestUnion {
    let mut chain = LargestUnion::Leaf;
    for level in (0..levels).rev() {
        let mut a = [[0u64; 32]; 31];
        a[0][0] = level as u64;
        chain = LargestUnion::Arm(LargestUnionArm { a, b: [[0; 32]; 31], next: Some(Box::new(chain)) });
    }
    chain
}

#[test]
fn a_wide_record_20_levels_deep_decodes_on_a_1mib_thread() {
    in_child("a_wide_record_20_levels_deep_decodes_on_a_1mib_thread", || decode_wide_chain(20, 1024 * KIB));
}

#[test]
fn a_wide_record_100_levels_deep_decodes_on_a_1mib_thread() {
    in_child("a_wide_record_100_levels_deep_decodes_on_a_1mib_thread", || decode_wide_chain(100, 1024 * KIB));
}

#[test]
fn a_wide_record_decodes_on_a_96kib_thread_at_one_level_and_at_the_limit() {
    in_child("a_wide_record_decodes_on_a_96kib_thread_at_one_level_and_at_the_limit", || {
        decode_wide_chain(1, 96 * KIB);
        decode_wide_chain(MAX_PAYLOAD_DEPTH - 1, 96 * KIB);
    });
}

fn decode_wide_chain(levels: usize, stack: usize) {
    let bytes = on_stack(64 * KIB * KIB, move || rmp_serde::to_vec(&wide_chain(levels)).unwrap());
    let decoded = decode_on::<Wide32>(stack, bytes).unwrap();
    on_stack(64 * KIB * KIB, move || assert!(decoded == wide_chain(levels)));
}

#[test]
fn the_largest_record_at_the_limit_decodes_on_every_thread_size() {
    in_child("the_largest_record_at_the_limit_decodes_on_every_thread_size", || {
        // 254 records of Largest are 256 containers deep (each is one, its slots' arrays two more);
        // 127 union levels are the same (a union and its arm are two each, the leaf's arrays two).
        let (records, unions) = on_stack(64 * KIB * KIB, || {
            let records = rmp_serde::to_vec(&largest_chain(254)).unwrap();
            let unions = rmp_serde::to_vec(&union_chain(127)).unwrap();
            assert!(check_payload_depth(&records).is_ok());
            assert!(check_payload_depth(&unions).is_ok());
            assert!(check_payload_depth(&rmp_serde::to_vec(&largest_chain(255)).unwrap()).is_err());
            assert!(check_payload_depth(&rmp_serde::to_vec(&union_chain(128)).unwrap()).is_err());
            (records, unions)
        });
        let sizes = (160..=1024).step_by(32).map(|kib| kib * KIB).chain([1536, 2048, 3072, 4096, 8192].map(|kib| kib * KIB));
        for size in sizes {
            println!("thread of {} KiB", size / KIB);
            let decoded = decode_on::<Largest>(size, records.clone()).unwrap();
            let decoded_union = decode_on::<LargestUnion>(size, unions.clone()).unwrap();
            on_stack(64 * KIB * KIB, move || {
                assert!(decoded == largest_chain(254));
                assert!(decoded_union == union_chain(127));
            });
        }
    });
}

#[test]
fn a_legacy_store_with_a_deep_unknown_envelope_key_reads_on_a_256kib_thread() {
    in_child("a_legacy_store_with_a_deep_unknown_envelope_key_reads_on_a_256kib_thread", || {
        // [ {key: "k", type: "t", payload: bin[1], storedAt: "s", extra: [[[...]]]} ]: the envelope
        // skips the unknown key, 1000 containers deep, with no payload walk (the bound is the payload's).
        let mut bytes = vec![0x91, 0x85];
        for (name, value) in [("key", &[0xa1, b'k'][..]), ("type", &[0xa1, b't']), ("payload", &[0xc4, 1, 1]), ("storedAt", &[0xa1, b's'])] {
            bytes.push(0xa0 | name.len() as u8);
            bytes.extend(name.as_bytes());
            bytes.extend(value);
        }
        bytes.extend([0xa5, b'e', b'x', b't', b'r', b'a']);
        bytes.extend(std::iter::repeat_n(0x91u8, 999));
        bytes.push(0x90);
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("legacy.cc");
        std::fs::write(&path, bytes).unwrap();
        let entries = on_stack(256 * KIB, move || SingleFileMessagePackBackingStore::new(path).pull_all_read_only_snapshot()).unwrap();
        assert_eq!(entries.len(), 1);
        assert_eq!(entries[0].key, "k");
    });
}
