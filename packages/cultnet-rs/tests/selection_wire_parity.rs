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
//! now matches the C# reference's `[MessagePackObject]`/`[Key("name")]` map encoding byte-for-byte
//! for a value carrying absent optionals - the exact shape `SelectionPage`/`CultNetSelectionPage`
//! carry inside `cultnet.snapshot_response_raw.v1`. It does not prove whole-message parity: that
//! still depends on R-AP's unbuilt half (see selection.rs's `SelectionPage` doc comment and this
//! cut's map - "Select gains projection" has not moved page assembly into the evaluator, so no
//! vector yet exercises the actual `CultNetSnapshotResponseRawV1Message`/`select_page` output
//! byte-for-byte, only these two nested wire types in isolation).

use cultnet_rs::{Edge, RawDocumentHeader, RecordRef};

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
