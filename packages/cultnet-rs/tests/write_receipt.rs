//! Wire parity with contracts/cultnet/interop/write-receipt-vectors.json: the content hash, and the two new frames.

use cultnet_rs::{
    CultNetErrorCode, CultNetErrorDetails, CultNetMessage, CultNetWireContract, decode_cultnet_message_from_slice,
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

fn unhex(text: &str) -> Vec<u8> {
    (0..text.len()).step_by(2).map(|i| u8::from_str_radix(&text[i..i + 2], 16).unwrap()).collect()
}

/// The spec's wire vectors (write-receipt-vectors.json `wire`): the bytes decode to the vector's fields, not
/// merely to something that encodes back to itself.
#[test]
fn the_spec_wire_vectors_decode_to_their_expected_fields() {
    let admitted = decode_cultnet_message_from_slice(
        &unhex("85AD736368656D6156657273696F6EB963756C746E65742E77726974655F61646D69747465642E7630A96D6573736167654964A57075742D31A96F7065726174696F6EA3707574A8736368656D614964A76E6F74652E7630A97265636F72644B6579A66E6F74653A31"),
        CultNetWireContract::CultNetSchemaV0,
    )
    .unwrap();
    assert_eq!(
        admitted,
        CultNetMessage::WriteAdmitted {
            message_id: "put-1".into(),
            operation: "put".into(),
            schema_id: "note.v0".into(),
            record_key: "note:1".into(),
        }
    );
    let refused = decode_cultnet_message_from_slice(
        &unhex("86AD736368656D6156657273696F6EB063756C746E65742E6572726F722E7631A56572726F72D93677726974655F6E6F745F617574686F72697A65643A2074686520636174616C6F6720726566757365642074686520646F63756D656E74AB726F7574696E6748696E74C0A4636F6465B477726974655F6E6F745F617574686F72697A6564A764657461696C7384A56669656C64A97265636F72644B6579A576616C7565A66E6F74653A32A461734F66C0A763757272656E74C0A96D6573736167654964A57075742D32"),
        CultNetWireContract::CultNetSchemaV0,
    )
    .unwrap();
    let CultNetMessage::ErrorV1 { error, code, details, message_id } = refused else {
        panic!("expected error.v1");
    };
    assert_eq!(error, "write_not_authorized: the catalog refused the document");
    assert_eq!(code, Some(CultNetErrorCode::WriteNotAuthorized));
    assert_eq!(message_id.as_deref(), Some("put-2"));
    let details = details.unwrap();
    assert_eq!(details.field.as_deref(), Some("recordKey"));
    assert_eq!(details.value.as_deref(), Some("note:2"));
    assert_eq!((details.as_of, details.current), (None, None));
}

/// Spec vector `error_v1_limit_reached`: the bytes the server must send, and what they decode to.
#[test]
fn error_v1_limit_reached_is_the_spec_vector() {
    let vector = "86AD736368656D6156657273696F6EB063756C746E65742E6572726F722E7631A56572726F72D93B6C696D69745F726561636865643A207468652073657373696F6E27732072656D656D62657265642D7772697465206C696D69742069732066756C6CAB726F7574696E6748696E74C0A4636F6465AD6C696D69745F72656163686564A764657461696C7384A56669656C64C0A576616C7565C0A461734F66C0A763757272656E74C0A96D6573736167654964A87075742D31303235";
    let message = CultNetMessage::ErrorV1 {
        error: "limit_reached: the session's remembered-write limit is full".into(),
        code: Some(CultNetErrorCode::LimitReached),
        details: Some(CultNetErrorDetails::default()),
        message_id: Some("put-1025".into()),
    };
    let bytes = encode_cultnet_message_to_vec(&message, CultNetWireContract::CultNetSchemaV0).unwrap();
    assert_eq!(bytes, unhex(vector), "encoded bytes must equal the spec vector");
    let CultNetMessage::ErrorV1 { error, code, details, message_id } =
        decode_cultnet_message_from_slice(&unhex(vector), CultNetWireContract::CultNetSchemaV0).unwrap()
    else {
        panic!("expected error.v1");
    };
    assert_eq!(error, "limit_reached: the session's remembered-write limit is full");
    assert_eq!(code, Some(CultNetErrorCode::LimitReached));
    assert_eq!(message_id.as_deref(), Some("put-1025"));
    assert_eq!(details, Some(CultNetErrorDetails::default()));
}

/// Spec vectors `write_admitted_put` and `write_admitted_delete`: the encoder emits the spec's field order.
#[test]
fn write_admitted_encodes_to_the_spec_vector_bytes() {
    let cases = [
        ("put-1", "put", "85AD736368656D6156657273696F6EB963756C746E65742E77726974655F61646D69747465642E7630A96D6573736167654964A57075742D31A96F7065726174696F6EA3707574A8736368656D614964A76E6F74652E7630A97265636F72644B6579A66E6F74653A31"),
        ("del-1", "delete", "85AD736368656D6156657273696F6EB963756C746E65742E77726974655F61646D69747465642E7630A96D6573736167654964A564656C2D31A96F7065726174696F6EA664656C657465A8736368656D614964A76E6F74652E7630A97265636F72644B6579A66E6F74653A31"),
    ];
    for (id, operation, vector) in cases {
        let message = CultNetMessage::WriteAdmitted {
            message_id: id.into(),
            operation: operation.into(),
            schema_id: "note.v0".into(),
            record_key: "note:1".into(),
        };
        let bytes = encode_cultnet_message_to_vec(&message, CultNetWireContract::CultNetSchemaV0).unwrap();
        assert_eq!(bytes, unhex(vector));
    }
}

/// `limit_reached` is in the error.v1 enum only: a v0 error neither encodes nor decodes it.
#[test]
fn error_v0_refuses_the_v1_only_limit_reached_code() {
    let v0 = CultNetMessage::Error {
        error: "x".into(),
        code: Some(CultNetErrorCode::LimitReached),
        details: None,
    };
    assert!(encode_cultnet_message_to_vec(&v0, CultNetWireContract::CultNetSchemaV0).is_err());
    let v1 = CultNetMessage::ErrorV1 {
        error: "x".into(),
        code: Some(CultNetErrorCode::LimitReached),
        details: None,
        message_id: None,
    };
    let v1_bytes = encode_cultnet_message_to_vec(&v1, CultNetWireContract::CultNetSchemaV0).unwrap();
    let at = v1_bytes.windows(16).position(|w| w == b"cultnet.error.v1").unwrap();
    let mut as_v0 = v1_bytes.clone();
    as_v0[at + 15] = b'0';
    assert!(decode_cultnet_message_from_slice(&as_v0, CultNetWireContract::CultNetSchemaV0).is_err());
}
