//! R-AP (docs/cultnet-selection-cut.md, "Self's rulings for fix batch 6"): `RawDocumentHeader` and
//! `Edge`, pinned byte-for-byte against the C# reference, the same way `tests/error_message.rs`
//! pins `cultnet.error.v0` - a hardcoded `csharp_hex` captured from a real MessagePack C# run,
//! compared against this crate's own encoding of the identical value.
//!
//! Unlike `error_message.rs`'s bytes (captured on Yggdrasil via `ygg-verify.sh` per that file's own
//! header), the bytes below were captured **locally**, in this worktree (`C:\ws7`), against
//! MessagePack C# 3.1.7 - the exact version `GameCult.Networking` depends on
//! (`src/GameCult.Networking/GameCult.Networking.csproj`) - using the project's own
//! `CultNetRawDocumentHeader`/`CultNetEdge`/`CultNetSchemaMessageSerialization.Options`
//! (`MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData)`), then
//! `Convert.ToHexString(MessagePackSerializer.Serialize(value, options))`. This session had no
//! access to the Yggdrasil verify image, so unlike the error-message vectors this was not run
//! through `ygg-verify.sh`; the capture itself is still a real MessagePack C# 3.1.7 run on this
//! machine, not hand-derived bytes.
//!
//! What this proves: `RawDocumentHeader`/`Edge`'s `to_vec_named` encoding (R-AP: `skip_serializing_if`
//! removed, every optional field always present and `nil` when absent, camelCase, declared order)
//! matches the C# reference's `[MessagePackObject]`/`[Key("name")]` map encoding byte-for-byte, both
//! at a value carrying every optional absent and - R-AX (docs/cultnet-selection-cut.md, "Self's
//! rulings for fix batch 7"), because an all-absent value proves nothing about `string[]` vs
//! `Vec<String>` or `byte[]` vs `serde_bytes` - at a value carrying `tags`/`payload` populated. R-AX
//! also lands the whole `cultnet.snapshot_response_raw.v1` envelope byte-identical against the C#
//! reference (`snapshot_response_raw_v1_envelope_matches_the_csharp_reference_byte_for_byte`,
//! below): Soul's earlier finding that this was "blocked on unbuilt page-assembly ownership" named
//! the wrong thing - the *encoders* producing identical bytes for the same page value never needed
//! that move, only the *evaluators* producing the same page value from the same rows does (that
//! half is `select_page`'s own vectors in `selection.rs`, a separate claim R-AR still owns).

use cultnet_rs::{
    CultNetMessage, CultNetWireContract, Edge, RawDocumentHeader, RecordRef,
    encode_cultnet_message_to_vec,
};

fn hex_decode(hex: &str) -> Vec<u8> {
    assert!(hex.len() % 2 == 0, "odd-length hex string");
    (0..hex.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&hex[i..i + 2], 16).expect("valid hex byte"))
        .collect()
}

// C#: new CultNetRawDocumentHeader { SchemaId = "sha256:ff35f3...4cff", RecordKey = "a-eq",
// StoredAt = "1970-01-01T00:00:00.000Z" } - SchemaName/SchemaVersion/SchemaContentHash/
// SourceRuntimeId/SourceAgentId/SourceRole/Tags all left null (absent).
// Convert.ToHexString(MessagePackSerializer.Serialize(header, CultNetSchemaMessageSerialization.Options)):
const CSHARP_HEADER_HEX: &str = "8AA8736368656D614964D9477368613235363A66663335663364396436646533663130363662393336396537336665366337363438653838333638643138656663323237356339393437333063643834636666AA736368656D614E616D65C0AD736368656D6156657273696F6EC0B1736368656D61436F6E74656E7448617368C0A97265636F72644B6579A4612D6571A873746F7265644174B8313937302D30312D30315430303A30303A30302E3030305AAF736F7572636552756E74696D654964C0AD736F757263654167656E744964C0AA736F75726365526F6C65C0A474616773C0";

// C#: new CultNetEdge { From = { SchemaId = "sha256:9d0bc9...608dc", RecordKey = "citer-1" },
// Role = "Design", To = { SchemaId = "sha256:ff35f3...4cff", RecordKey = "a-eq" } } -
// PayloadEncoding/Payload left null (absent, header projection).
const CSHARP_EDGE_HEX: &str = "85A466726F6D82A8736368656D614964D9477368613235363A39643062633961656263333631396230376161626635363466393836363761666465343134343463316238363636363164373737363837643434333630386463A97265636F72644B6579A763697465722D31A4726F6C65A644657369676EA2746F82A8736368656D614964D9477368613235363A66663335663364396436646533663130363662393336396537336665366337363438653838333638643138656663323237356339393437333063643834636666A97265636F72644B6579A4612D6571AF7061796C6F6164456E636F64696E67C0A77061796C6F6164C0";

#[test]
fn raw_document_header_matches_the_csharp_reference_byte_for_byte() {
    let header = RawDocumentHeader {
        schema_id: "sha256:ff35f3d9d6de3f1066b9369e73fe6c7648e88368d18efc2275c994730cd84cff".to_string(),
        schema_name: None,
        schema_version: None,
        schema_content_hash: None,
        record_key: "a-eq".to_string(),
        stored_at: "1970-01-01T00:00:00.000Z".to_string(),
        source_runtime_id: None,
        source_agent_id: None,
        source_role: None,
        tags: None,
    };

    let actual = rmp_serde::to_vec_named(&header).expect("encodes");
    let expected = hex_decode(CSHARP_HEADER_HEX);
    assert_eq!(
        actual, expected,
        "RawDocumentHeader's Rust-encoded bytes do not match the captured C# encoding"
    );
}

#[test]
fn edge_matches_the_csharp_reference_byte_for_byte() {
    let edge = Edge {
        from: RecordRef::new(
            "sha256:9d0bc9aebc3619b07aabf564f98667afde41444c1b866661d777687d443608dc",
            "citer-1",
        ),
        role: "Design".to_string(),
        to: RecordRef::new(
            "sha256:ff35f3d9d6de3f1066b9369e73fe6c7648e88368d18efc2275c994730cd84cff",
            "a-eq",
        ),
        payload_encoding: None,
        payload: None,
    };

    let actual = rmp_serde::to_vec_named(&edge).expect("encodes");
    let expected = hex_decode(CSHARP_EDGE_HEX);
    assert_eq!(
        actual, expected,
        "Edge's Rust-encoded bytes do not match the captured C# encoding"
    );
}

// R-AX (docs/cultnet-selection-cut.md, "Self's rulings for fix batch 7"): the two vectors above
// pin `RawDocumentHeader`/`Edge` at a single value with every optional absent - exactly the shape
// that proves nothing about `tags` (`string[]` vs `Vec<String>`) or `payload` (`byte[]` vs
// `serde_bytes`), since an absent optional never exercises either mapping. These two extend the
// same byte-for-byte proof to a header with `tags` populated and an edge with a `payload`
// populated, captured the same way (`Convert.ToHexString(MessagePackSerializer.Serialize(...))`
// against the real MessagePack C# 3.1.7 GameCult.Networking depends on, via ygg-verify.sh on
// Yggdrasil). Both matched byte-for-byte on capture - no divergence found.

// C#: new CultNetRawDocumentHeader { SchemaId = LEAF_A, RecordKey = "a-eq",
// StoredAt = "1970-01-01T00:00:00.000Z", Tags = ["alpha", "beta"] } - every other optional absent.
const CSHARP_HEADER_WITH_TAGS_HEX: &str = "8AA8736368656D614964D9477368613235363A66663335663364396436646533663130363662393336396537336665366337363438653838333638643138656663323237356339393437333063643834636666AA736368656D614E616D65C0AD736368656D6156657273696F6EC0B1736368656D61436F6E74656E7448617368C0A97265636F72644B6579A4612D6571A873746F7265644174B8313937302D30312D30315430303A30303A30302E3030305AAF736F7572636552756E74696D654964C0AD736F757263654167656E744964C0AA736F75726365526F6C65C0A47461677392A5616C706861A462657461";

// C#: new CultNetEdge { From = { SchemaId = CITER_ID, RecordKey = "citer-1" }, Role = "Design",
// To = { SchemaId = LEAF_A, RecordKey = "a-eq" }, PayloadEncoding = "messagepack",
// Payload = [1, 2, 3] }.
const CSHARP_EDGE_WITH_PAYLOAD_HEX: &str = "85A466726F6D82A8736368656D614964D9477368613235363A39643062633961656263333631396230376161626635363466393836363761666465343134343463316238363636363164373737363837643434333630386463A97265636F72644B6579A763697465722D31A4726F6C65A644657369676EA2746F82A8736368656D614964D9477368613235363A66663335663364396436646533663130363662393336396537336665366337363438653838333638643138656663323237356339393437333063643834636666A97265636F72644B6579A4612D6571AF7061796C6F6164456E636F64696E67AB6D6573736167657061636BA77061796C6F6164C403010203";

#[test]
fn raw_document_header_with_tags_matches_the_csharp_reference_byte_for_byte() {
    let header = RawDocumentHeader {
        schema_id: "sha256:ff35f3d9d6de3f1066b9369e73fe6c7648e88368d18efc2275c994730cd84cff".to_string(),
        schema_name: None,
        schema_version: None,
        schema_content_hash: None,
        record_key: "a-eq".to_string(),
        stored_at: "1970-01-01T00:00:00.000Z".to_string(),
        source_runtime_id: None,
        source_agent_id: None,
        source_role: None,
        tags: Some(vec!["alpha".to_string(), "beta".to_string()]),
    };

    let actual = rmp_serde::to_vec_named(&header).expect("encodes");
    let expected = hex_decode(CSHARP_HEADER_WITH_TAGS_HEX);
    assert_eq!(
        actual, expected,
        "RawDocumentHeader with tags populated differs from the C# reference - string[] vs Vec<String> diverges"
    );
}

#[test]
fn edge_with_payload_matches_the_csharp_reference_byte_for_byte() {
    let edge = Edge {
        from: RecordRef::new(
            "sha256:9d0bc9aebc3619b07aabf564f98667afde41444c1b866661d777687d443608dc",
            "citer-1",
        ),
        role: "Design".to_string(),
        to: RecordRef::new(
            "sha256:ff35f3d9d6de3f1066b9369e73fe6c7648e88368d18efc2275c994730cd84cff",
            "a-eq",
        ),
        payload_encoding: Some("messagepack".to_string()),
        payload: Some(vec![1, 2, 3]),
    };

    let actual = rmp_serde::to_vec_named(&edge).expect("encodes");
    let expected = hex_decode(CSHARP_EDGE_WITH_PAYLOAD_HEX);
    assert_eq!(
        actual, expected,
        "Edge with payload populated differs from the C# reference - byte[] vs serde_bytes diverges"
    );
}

// R-AX: the whole `cultnet.snapshot_response_raw.v1` envelope, byte-identical against the same C#
// reference message (`CultNetSnapshotResponseRawV1Message { MessageId = "m", Matched = 1, AsOf = 1,
// Headers = [ { SchemaId = LEAF_A, RecordKey = "a-eq", StoredAt = "1970-01-01T00:00:00.000Z" } ] }`,
// every other field absent). This is R-AX landed, not blocked: `select_page`'s output type
// (`SelectionPage`) is carried inside the caller's own `CultNetMessage::SnapshotResponseRawV1`
// envelope, and this proves the two runtimes' *encoders* agree on that whole shape, byte for byte
// - the rows-to-bytes single-call path R-AR would add is a separate, smaller claim (Self's
// ruling: "R-AR remains its own cut").
const CSHARP_ENVELOPE_ALL_ABSENT_HEX: &str = "8BAD736368656D6156657273696F6ED92063756C746E65742E736E617073686F745F726573706F6E73655F7261772E7631A96D6573736167654964A16DA76D61746368656401A461734F6601A46E657874C0A768656164657273918AA8736368656D614964D9477368613235363A66663335663364396436646533663130363662393336396537336665366337363438653838333638643138656663323237356339393437333063643834636666AA736368656D614E616D65C0AD736368656D6156657273696F6EC0B1736368656D61436F6E74656E7448617368C0A97265636F72644B6579A4612D6571A873746F7265644174B8313937302D30312D30315430303A30303A30302E3030305AAF736F7572636552756E74696D654964C0AD736F757263654167656E744964C0AA736F75726365526F6C65C0A474616773C0A9646F63756D656E7473C0A56564676573C0A773686172644964C0AA736861726445706F6368C0B073686172644C6F6753657175656E6365C0";

#[test]
fn snapshot_response_raw_v1_envelope_matches_the_csharp_reference_byte_for_byte() {
    let message = CultNetMessage::SnapshotResponseRawV1 {
        message_id: "m".to_string(),
        matched: 1,
        as_of: 1,
        next: None,
        headers: Some(vec![RawDocumentHeader {
            schema_id: "sha256:ff35f3d9d6de3f1066b9369e73fe6c7648e88368d18efc2275c994730cd84cff".to_string(),
            schema_name: None,
            schema_version: None,
            schema_content_hash: None,
            record_key: "a-eq".to_string(),
            stored_at: "1970-01-01T00:00:00.000Z".to_string(),
            source_runtime_id: None,
            source_agent_id: None,
            source_role: None,
            tags: None,
        }]),
        documents: None,
        edges: None,
        shard_id: None,
        shard_epoch: None,
        shard_log_sequence: None,
    };

    let actual = encode_cultnet_message_to_vec(&message, CultNetWireContract::CultNetSchemaV0).expect("encodes");
    let expected = hex_decode(CSHARP_ENVELOPE_ALL_ABSENT_HEX);
    assert_eq!(actual.len(), 376);
    assert_eq!(
        actual, expected,
        "the whole cultnet.snapshot_response_raw.v1 envelope differs from the C# reference"
    );
}
