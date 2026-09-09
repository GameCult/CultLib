//! Does adding `#[cultcache(key = N, bytes)]` to a field break reading the
//! records already written without it?
//!
//! The attribute changes a slot from a MessagePack array-of-integers (serde's
//! default for `Vec<u8>`) to a real `bin`. Six of cultnet-rs's byte fields
//! already carry it and four do not, so converting the remaining four is a
//! stored-data question before it is a signature question: every record
//! written before the flip holds an array in that slot.
//!
//! Both directions are tested, because a migration has to survive a rollback
//! as well as a roll-forward.

use cultcache_rs::DatabaseEntry;
use serde::Serialize;

#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "test.byte_slot", schema = "test.byte_slot.v1")]
struct ArrayEncoded {
    #[cultcache(key = 0)]
    schema_version: String,
    #[cultcache(key = 1)]
    payload: Vec<u8>,
}

#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "test.byte_slot", schema = "test.byte_slot.v1")]
struct BinEncoded {
    #[cultcache(key = 0)]
    schema_version: String,
    #[cultcache(key = 1, bytes)]
    payload: Vec<u8>,
}

fn sample() -> Vec<u8> {
    (0u8..=255).collect()
}

#[test]
fn the_attribute_is_what_switches_the_wire_shape() {
    let array = rmp_serde::to_vec(&ArrayEncoded {
        schema_version: "test.byte_slot.v1".into(),
        payload: sample(),
    })
    .expect("array encodes");
    let bin = rmp_serde::to_vec(&BinEncoded {
        schema_version: "test.byte_slot.v1".into(),
        payload: sample(),
    })
    .expect("bin encodes");

    // 256 bytes as `bin` is a 3-byte header plus the payload. As an array it
    // is one or two bytes per element, because every value above 0x7f needs a
    // u8 marker of its own.
    println!("array-encoded {} bytes, bin-encoded {} bytes", array.len(), bin.len());
    assert!(
        array.len() > bin.len(),
        "array {} should exceed bin {}",
        array.len(),
        bin.len()
    );
}

#[test]
fn a_bytes_reader_still_reads_records_written_before_the_attribute() {
    let written_before = rmp_serde::to_vec(&ArrayEncoded {
        schema_version: "test.byte_slot.v1".into(),
        payload: sample(),
    })
    .expect("array encodes");

    // serde_bytes::ByteBuf accepts an array marker as well as bin, so the
    // flip is NOT a hard read break. This was worth measuring rather than
    // assuming: the opposite would have forced a dual-read path or a rewrite
    // migration onto every field with persisted records.
    let read_after =
        rmp_serde::from_slice::<BinEncoded>(&written_before).expect("bytes reader reads an array");
    assert_eq!(read_after.payload, sample());
}

#[test]
fn a_pre_attribute_reader_still_reads_records_written_after_it() {
    let written_after = rmp_serde::to_vec(&BinEncoded {
        schema_version: "test.byte_slot.v1".into(),
        payload: sample(),
    })
    .expect("bin encodes");

    // The rollback direction, so reverting the binary is survivable.
    let read_by_old =
        rmp_serde::from_slice::<ArrayEncoded>(&written_after).expect("plain reader reads bin");
    assert_eq!(read_by_old.payload, sample());
}

#[test]
fn re_encoding_an_old_record_under_the_attribute_does_not_reproduce_its_bytes() {
    // Where the cost actually lands. Reading is compatible; reproducing is
    // not. Idunn byte-exact-checks its control store by re-encoding what it
    // decoded, and canonical_sha256 hashes the same serialization -- so a
    // record written before the flip can be read, but can never re-encode to
    // the bytes it was stored as, and its canonical digest changes.
    let written_before = rmp_serde::to_vec(&ArrayEncoded {
        schema_version: "test.byte_slot.v1".into(),
        payload: sample(),
    })
    .expect("array encodes");
    let decoded = rmp_serde::from_slice::<BinEncoded>(&written_before).expect("reads");
    let re_encoded = rmp_serde::to_vec(&decoded).expect("re-encodes");

    assert_ne!(
        written_before, re_encoded,
        "identical bytes would mean the attribute changed nothing"
    );
}
