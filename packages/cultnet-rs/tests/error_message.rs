//! R-N (docs/cultnet-selection-cut.md, fix batch 3): `cultnet.error.v0`'s `code`/`details` fields,
//! pinned byte-for-byte against the landed C# reference
//! (`CultNetErrorMessage.ForSelectionInvalid`/`ForCursor`/`ForReferenceOutsideTarget`,
//! `src/GameCult.Networking/CultNetSchemaMessages.cs`, commit `e8a3dec`).
//!
//! The expected bytes below are not hand-derived: they were captured by running MessagePack C#
//! 3.1.7 (the exact package/version `GameCult.Networking` depends on) against a standalone
//! `CultNetErrorMessage`/`CultNetErrorDetails` pair carrying the landed `[Key(...)]` shape, using
//! `MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData)` - the
//! same options `CultNetSchemaMessageSerialization.Options` uses - then
//! `Convert.ToHexString(MessagePackSerializer.Serialize(message, options))`. The probe program lived
//! at `%TEMP%\claude\probe-msgpack` (outside this repo) and was run in the `dotnet` Yggdrasil verify
//! image via `ygg-verify.sh`; it is not part of this crate. Each case below documents the C# call
//! that produced its bytes.
//!
//! This confirms the one thing that matters for byte parity here: MessagePack C#'s
//! `[Key("name")]`-attributed map formatter always writes every declared key, in declaration order,
//! nil for a null value - it never omits a key. `CultNetMessage::Error` is therefore encoded through
//! the same `rmpv::Value::Map`-literal path as the crate's other raw `cultnet.schema.v0` messages
//! (`encode_raw_cultnet_schema_message`/`parse_raw_cultnet_schema_message` in `src/contracts.rs`)
//! rather than through the generic `serde_json::Value` round trip every other `CultNetMessage`
//! variant uses - that path's `serde_json::Value::Object` is a `BTreeMap` (no `preserve_order`
//! feature enabled in this crate's `serde_json` dependency), so it would emit keys sorted
//! alphabetically and silently break this exact byte match.

use cultnet_rs::{
    CultNetErrorCode, CultNetErrorDetails, CultNetMessage, CultNetWireContract,
    decode_cultnet_message_from_slice, encode_cultnet_message_to_vec,
};

fn hex_decode(hex: &str) -> Vec<u8> {
    assert!(hex.len() % 2 == 0, "odd-length hex string");
    (0..hex.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&hex[i..i + 2], 16).expect("valid hex byte"))
        .collect()
}

struct Case {
    name: &'static str,
    /// Captured via `Convert.ToHexString(MessagePackSerializer.Serialize(msg, options))` in the
    /// probe program described in this file's header comment.
    csharp_hex: &'static str,
    message: fn() -> CultNetMessage,
}

fn cases() -> Vec<Case> {
    vec![
        Case {
            name: "selection_invalid_schemas_null",
            // C#: ForSelectionInvalid(new CultNetSelectionInvalidException("schemas", null,
            // "must not be empty"))
            csharp_hex: "85AD736368656D6156657273696F6EB063756C746E65742E6572726F722E7630A56572726F72D92473656C656374696F6E5F696E76616C69643A206D757374206E6F7420626520656D707479AB726F7574696E6748696E74C0A4636F6465B173656C656374696F6E5F696E76616C6964A764657461696C7384A56669656C64A7736368656D6173A576616C7565C0A461734F66C0A763757272656E74C0",
            message: || CultNetMessage::Error {
                error: "selection_invalid: must not be empty".to_string(),
                code: Some(CultNetErrorCode::SelectionInvalid),
                details: Some(CultNetErrorDetails {
                    field: Some("schemas".to_string()),
                    value: None,
                    as_of: None,
                    current: None,
                }),
            },
        },
        Case {
            name: "selection_invalid_field_value",
            // C#: ForSelectionInvalid(new CultNetSelectionInvalidException("keys", "   ",
            // "blank key entry"))
            csharp_hex: "85AD736368656D6156657273696F6EB063756C746E65742E6572726F722E7630A56572726F72D92273656C656374696F6E5F696E76616C69643A20626C616E6B206B657920656E747279AB726F7574696E6748696E74C0A4636F6465B173656C656374696F6E5F696E76616C6964A764657461696C7384A56669656C64A46B657973A576616C7565A3202020A461734F66C0A763757272656E74C0",
            message: || CultNetMessage::Error {
                error: "selection_invalid: blank key entry".to_string(),
                code: Some(CultNetErrorCode::SelectionInvalid),
                details: Some(CultNetErrorDetails {
                    field: Some("keys".to_string()),
                    value: Some("   ".to_string()),
                    as_of: None,
                    current: None,
                }),
            },
        },
        Case {
            name: "cursor_stale",
            // C#: ForCursor(new CultNetSelectionCursorException("cursor_stale",
            // "cursor is stale", asOf: 1, current: 2))
            csharp_hex: "85AD736368656D6156657273696F6EB063756C746E65742E6572726F722E7630A56572726F72BD637572736F725F7374616C653A20637572736F72206973207374616C65AB726F7574696E6748696E74C0A4636F6465AC637572736F725F7374616C65A764657461696C7384A56669656C64C0A576616C7565C0A461734F6601A763757272656E7402",
            message: || CultNetMessage::Error {
                error: "cursor_stale: cursor is stale".to_string(),
                code: Some(CultNetErrorCode::CursorStale),
                details: Some(CultNetErrorDetails {
                    field: None,
                    value: None,
                    as_of: Some(1),
                    current: Some(2),
                }),
            },
        },
        Case {
            name: "cursor_invalid",
            // C#: ForCursor(new CultNetSelectionCursorException("cursor_invalid",
            // "cannot parse cursor")) - no asOf/current, so Details stays null.
            csharp_hex: "85AD736368656D6156657273696F6EB063756C746E65742E6572726F722E7630A56572726F72D923637572736F725F696E76616C69643A2063616E6E6F7420706172736520637572736F72AB726F7574696E6748696E74C0A4636F6465AE637572736F725F696E76616C6964A764657461696C73C0",
            message: || CultNetMessage::Error {
                error: "cursor_invalid: cannot parse cursor".to_string(),
                code: Some(CultNetErrorCode::CursorInvalid),
                details: None,
            },
        },
        Case {
            name: "reference_outside_target",
            // C#: ForReferenceOutsideTarget(...) - built directly with the final Error text here
            // (the constructor path itself double-prefixes "reference_outside_target: ", see this
            // crate's SelectionRefusal -> CultNetMessage mapping doc comment in src/selection.rs
            // for the discrepancy); the wire shape under test does not depend on the text's content.
            csharp_hex: "85AD736368656D6156657273696F6EB063756C746E65742E6572726F722E7630A56572726F72D9317265666572656E63655F6F7574736964655F7461726765743A206F757473696465206465636C6172656420746172676574AB726F7574696E6748696E74C0A4636F6465B87265666572656E63655F6F7574736964655F746172676574A764657461696C73C0",
            message: || CultNetMessage::Error {
                error: "reference_outside_target: outside declared target".to_string(),
                code: Some(CultNetErrorCode::ReferenceOutsideTarget),
                details: None,
            },
        },
    ]
}

#[test]
fn rust_encode_matches_csharp_bytes_for_every_r_n_code() {
    for case in cases() {
        let expected = hex_decode(case.csharp_hex);
        let actual = encode_cultnet_message_to_vec(&(case.message)(), CultNetWireContract::CultNetSchemaV0)
            .unwrap_or_else(|err| panic!("{}: encode failed: {err}", case.name));
        assert_eq!(
            actual, expected,
            "{}: Rust-encoded bytes do not match the captured C# encoding",
            case.name
        );
    }
}

#[test]
fn rust_decodes_the_csharp_written_bytes_back_into_the_same_typed_message() {
    for case in cases() {
        let bytes = hex_decode(case.csharp_hex);
        let decoded = decode_cultnet_message_from_slice(&bytes, CultNetWireContract::CultNetSchemaV0)
            .unwrap_or_else(|err| panic!("{}: decode failed: {err}", case.name));
        assert_eq!(decoded, (case.message)(), "{}: decoded value mismatch", case.name);
    }
}

#[test]
fn round_trip_is_stable() {
    for case in cases() {
        let message = (case.message)();
        let encoded = encode_cultnet_message_to_vec(&message, CultNetWireContract::CultNetSchemaV0)
            .unwrap_or_else(|err| panic!("{}: encode failed: {err}", case.name));
        let decoded = decode_cultnet_message_from_slice(&encoded, CultNetWireContract::CultNetSchemaV0)
            .unwrap_or_else(|err| panic!("{}: decode failed: {err}", case.name));
        assert_eq!(decoded, message, "{}: round trip mismatch", case.name);
    }
}
