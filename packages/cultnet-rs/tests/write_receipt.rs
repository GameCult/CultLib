//! Wire parity with contracts/cultnet/interop/write-receipt-vectors.json: the content hash, and the two new frames.

use cultnet_rs::{
    CultNetErrorCode, CultNetMessage, CultNetWireContract, decode_cultnet_message_from_slice,
    encode_cultnet_message_to_vec, write_content_hash,
};

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}

#[test]
fn the_content_hash_matches_the_spec_vectors() {
    let a = [0x81, 0xA2, 0x69, 0x64, 0xA1, 0x31];
    let b = [0x81, 0xA2, 0x69, 0x64, 0xA1, 0x32];
    let cases = [
        (write_content_hash("put", "note.v0", "note:1", Some(&a)), "7051c10071123b6ade50fbd5075e3859cae4ea70ce358a24a0b0c6980090e95d"),
        (write_content_hash("put", "note.v0", "note:1", Some(&b)), "58d3fc6b858d2605a68550c9d4811ccdca6b9db7a0c3bfebf304d279257f6f9f"),
        (write_content_hash("put", "note.v0", "note:2", Some(&a)), "a248cd920e58d41bc6ccca9e108030b1d491239000047fef088122364a32b6f8"),
        (write_content_hash("delete", "note.v0", "note:1", None), "232b0f96651b28aa8ab7673a6106944a9f6a8d66852b6e21291a23ed084b8b37"),
    ];
    for (hash, expected) in cases {
        assert_eq!(hex(&hash.unwrap()), expected);
    }
}

#[test]
fn the_new_frames_decode_back_equal() {
    let admitted = CultNetMessage::WriteAdmitted {
        message_id: "put-1".into(),
        operation: "put".into(),
        schema_id: "note.v0".into(),
        record_key: "note:1".into(),
    };
    let bytes = encode_cultnet_message_to_vec(&admitted, CultNetWireContract::CultNetSchemaV0).unwrap();
    assert_eq!(
        decode_cultnet_message_from_slice(&bytes, CultNetWireContract::CultNetSchemaV0).unwrap(),
        admitted
    );
    // error.v1 with a nil id round-trips as nil, and the code enum is v0's.
    let error = CultNetMessage::ErrorV1 {
        error: "message_invalid: unreadable frame".into(),
        code: Some(CultNetErrorCode::MessageInvalid),
        details: None,
        message_id: None,
    };
    let bytes = encode_cultnet_message_to_vec(&error, CultNetWireContract::CultNetSchemaV0).unwrap();
    assert_eq!(
        decode_cultnet_message_from_slice(&bytes, CultNetWireContract::CultNetSchemaV0).unwrap(),
        error
    );
}
