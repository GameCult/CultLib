//! Every cultnet-rs decoder of a wire payload that can carry free-form or skipped content holds the
//! shared 256 nesting bound through `cultcache_rs::decode_payload`: 256 is accepted and 257 is refused
//! with the typed refusal, for arrays, maps and union arms, and a 2000-deep payload is an error on a
//! 2 MiB stack, not an abort.

use anyhow::{Error, Result};
use base64::Engine;
use base64::engine::general_purpose::STANDARD;
use cultcache_rs::{CultSlots, CultUnion, MAX_PAYLOAD_DEPTH, PayloadTooDeep};
use cultnet_rs::*;
use serde::Serialize;

const CANARY: &str = "canary-secret-payload-text";
const TOO_DEEP: PayloadTooDeep = PayloadTooDeep { depth: MAX_PAYLOAD_DEPTH + 1, limit: MAX_PAYLOAD_DEPTH };

#[derive(Clone, Debug, PartialEq, CultUnion)]
enum Node {
    #[cultcache(key = 0)]
    Box(BoxArm),
    #[cultcache(key = 1)]
    Text(TextArm),
    #[cultcache(key = 6)]
    Wrap(WrapArm),
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
struct BoxArm {
    #[cultcache(key = 0)]
    id: String,
    #[cultcache(key = 1)]
    children: Vec<Node>,
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
struct TextArm {
    #[cultcache(key = 0)]
    id: String,
    #[cultcache(key = 1)]
    content: String,
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
struct WrapArm {
    #[cultcache(key = 0)]
    inner: Option<std::boxed::Box<Node>>,
}

#[derive(Serialize)]
struct MapNest {
    inner: Option<std::boxed::Box<MapNest>>,
}

/// One value `depth` containers deep, from the production encoder.
fn arrays(depth: usize) -> Vec<u8> {
    let mut value = rmpv::Value::String(CANARY.into());
    for _ in 0..depth {
        value = rmpv::Value::Array(vec![value]);
    }
    rmp_serde::to_vec(&value).unwrap()
}

fn maps(depth: usize) -> Vec<u8> {
    let mut nest = MapNest { inner: None };
    for _ in 1..depth {
        nest = MapNest { inner: Some(std::boxed::Box::new(nest)) };
    }
    rmp_serde::to_vec_named(&nest).unwrap()
}

// Each Wrap adds a pair and a slots array: a Text leaf is 2 * wraps + 2 deep, a Box leaf 2 * wraps + 3.
fn arms(depth: usize) -> Vec<u8> {
    let (mut node, wraps) = if depth % 2 == 0 {
        (Node::Text(TextArm { id: "leaf".into(), content: CANARY.into() }), (depth - 2) / 2)
    } else {
        (Node::Box(BoxArm { id: "leaf".into(), children: vec![] }), (depth - 3) / 2)
    };
    for _ in 0..wraps {
        node = Node::Wrap(WrapArm { inner: Some(std::boxed::Box::new(node)) });
    }
    rmp_serde::to_vec(&node).unwrap()
}

type Nesting = fn(usize) -> Vec<u8>;
const NESTINGS: [(&str, Nesting); 3] = [("arrays", arrays), ("maps", maps), ("arms", arms)];

/// The hole at 2000 containers, from marker bytes: the encoders recurse too.
fn deep_marker_payload() -> Vec<u8> {
    let mut bytes = vec![0x91; 2000];
    bytes.push(0xc0);
    bytes
}

/// Swaps the trailing nil of a document for `nested`: the free field is its last.
fn replace_trailing_nil(mut document: Vec<u8>, nested: &[u8]) -> Vec<u8> {
    assert_eq!(document.pop(), Some(0xc0));
    document.extend_from_slice(nested);
    document
}

/// Adds one `x` entry holding `nested` to a MessagePack map: the unknown key a named-map record skips.
fn add_unknown_entry(mut map: Vec<u8>, nested: &[u8]) -> Vec<u8> {
    match map[0] {
        header @ 0x80..=0x8e => map[0] = header + 1,
        0xde => {
            let count = u16::from_be_bytes([map[1], map[2]]) + 1;
            map[1..3].copy_from_slice(&count.to_be_bytes());
        }
        other => panic!("not a small map: {other:#x}"),
    }
    map.extend_from_slice(&[0xa1, b'x']);
    map.extend_from_slice(nested);
    map
}

/// Adds one trailing slot to a positional record array: the unknown slot the derive skips.
fn add_trailing_slot(mut array: Vec<u8>, nested: &[u8]) -> Vec<u8> {
    match array[0] {
        header @ 0x90..=0x9e => array[0] = header + 1,
        0xdc => {
            let count = u16::from_be_bytes([array[1], array[2]]) + 1;
            array[1..3].copy_from_slice(&count.to_be_bytes());
        }
        other => panic!("not a small array: {other:#x}"),
    }
    array.extend_from_slice(nested);
    array
}

type Decode = fn(&[u8]) -> Result<()>;
/// A payload whose free or skipped part is `nested`, deep by `1 + depth(nested)`.
type Frame = Box<dyn Fn(&[u8]) -> Vec<u8>>;

struct Decoder {
    name: &'static str,
    frame: Frame,
    decode: Decode,
}

fn command_document(payload: Vec<u8>) -> CultNetRawDocumentRecord {
    CultNetRawDocumentRecord {
        schema_id: CULTMESH_PROVIDER_COMMAND_SCHEMA.to_string(),
        record_key: "command-1".to_string(),
        stored_at: "unix:1000".to_string(),
        payload_encoding: CultNetRawPayloadEncoding::Messagepack,
        payload,
        source_runtime_id: None,
        source_agent_id: None,
        source_role: None,
        tags: None,
    }
}

fn command_with_nil() -> Vec<u8> {
    rmp_serde::to_vec_named(&CultMeshProviderCommandWire {
        command_id: "command-1".into(),
        command_kind: "reload".into(),
        provider_id: "aetheria".into(),
        service_instance_id: "aetheria-1".into(),
        payload: rmpv::Value::Nil,
    })
    .unwrap()
}

fn receipt_with_nil() -> Vec<u8> {
    rmp_serde::to_vec_named(&CultMeshProviderCommandReceiptWire {
        receipt_id: "receipt-1".into(),
        command_id: "command-1".into(),
        command_kind: "reload".into(),
        provider_id: "aetheria".into(),
        service_instance_id: "aetheria-1".into(),
        state: CultMeshProviderReceiptStateWire::Applied,
        completed_at_utc: "2026-01-01T00:00:00Z".into(),
        result: Some(rmpv::Value::Nil),
        error: None,
    })
    .unwrap()
}

fn evidence_bytes() -> Vec<u8> {
    encode_provider_connect_evidence(&CultMeshProviderConnectEvidenceWire {
        client_session_id: "client-1".into(),
        session_token: None,
    })
    .unwrap()
}

fn hello_bytes() -> Vec<u8> {
    let hello = CultNetMessage::Hello {
        runtime_id: "peer".into(),
        runtime_kind: "rust-worker".into(),
        agent_id: None,
        role: None,
        display_name: None,
        supported_document_types: None,
        supported_mutation_contracts: None,
        supported_message_versions: None,
        transport_profiles: None,
        supports_schema_catalog: None,
    };
    encode_cultnet_message_to_vec(&hello, CultNetWireContract::CultNetSchemaV0).unwrap()
}

fn media_records() -> Vec<GameCultMediaWireRecord> {
    vec![
        GameCultMediaWireRecord::Video(GameCultMediaVideoAccessUnitRecord {
            stream_id: "s".into(),
            session_id: "x".into(),
            frame_id: 7,
            codec: "h264".into(),
            pts_ticks: 90_000,
            duration_ticks: 3_000,
            timebase_num: 1,
            timebase_den: 90_000,
            keyframe: true,
            dependency_frame_id: None,
            deadline_ticks: 108_000,
            chunk_index: 0,
            chunk_count: 1,
            payload: vec![1, 2, 3],
        }),
        GameCultMediaWireRecord::VideoParity(GameCultMediaVideoParityShardRecord {
            stream_id: "s".into(),
            session_id: "x".into(),
            frame_id: 1,
            codec: "h264".into(),
            pts_ticks: 0,
            duration_ticks: 1,
            timebase_num: 1,
            timebase_den: 90_000,
            keyframe: false,
            dependency_frame_id: Some(0),
            deadline_ticks: 1,
            chunk_count: 4,
            fec_scheme: "rs-gf256-v1".into(),
            block_index: 0,
            block_count: 1,
            block_data_start: 0,
            block_data_count: 4,
            parity_index: 1,
            parity_count: 2,
            shard_payload_bytes: 3,
            last_chunk_payload_bytes: 2,
            payload: vec![1, 2, 3],
        }),
        GameCultMediaWireRecord::AudioParity(GameCultMediaAudioParityShardRecord {
            stream_id: "s".into(),
            session_id: "x".into(),
            codec: "opus".into(),
            fec_scheme: "rs-gf256-v1".into(),
            base_packet_id: 8,
            base_pts_ticks: 0,
            packet_duration_ticks: 960,
            timebase_num: 1,
            timebase_den: 48_000,
            deadline_ticks: 4_000,
            data_shard_count: 4,
            parity_index: 0,
            parity_shard_count: 2,
            shard_payload_bytes: 3,
            payload: vec![4, 5, 6],
        }),
        GameCultMediaWireRecord::Audio(GameCultMediaAudioPacketRecord {
            stream_id: "s".into(),
            session_id: "x".into(),
            packet_id: 9,
            codec: "opus".into(),
            pts_ticks: 0,
            duration_ticks: 960,
            timebase_num: 1,
            timebase_den: 48_000,
            deadline_ticks: 1,
            payload: vec![4, 5, 6],
        }),
        GameCultMediaWireRecord::Feedback(GameCultMediaReceiverFeedbackRecord {
            stream_id: "s".into(),
            session_id: "x".into(),
            receiver_id: "obs".into(),
            highest_decodable_frame_id: Some(41),
            missing_frame_ids: vec![42],
            late_frame_ids: vec![43],
            requested_keyframe: true,
            jitter_us: 500,
            decode_queue_us: 2_000,
            observed_at: "unix:1000".into(),
            missing_video_chunk_keys: vec!["42:1".into()],
        }),
    ]
}

/// A valid media wire message whose record carries one more, unknown slot holding `nested`.
fn media_wire_with_unknown_slot(record: &GameCultMediaWireRecord, nested: &[u8]) -> Vec<u8> {
    let provenance =
        MediaWireProvenance { stored_at: "unix:1000", runtime_id: "peer", role: "media", producer: "peer" };
    let wire = encode_media_wire_record(record, provenance).unwrap();
    let message = decode_cultnet_message_from_slice(&wire, CultNetWireContract::CultNetSchemaV0).unwrap();
    let CultNetMessage::DocumentPutRaw { message_id, mut document } = message else { unreachable!() };
    document.payload = add_trailing_slot(document.payload, nested);
    let message = CultNetMessage::DocumentPutRaw { message_id, document };
    encode_cultnet_message_to_vec(&message, CultNetWireContract::CultNetSchemaV0).unwrap()
}

const MEDIA_NAMES: [&str; 5] = [
    "media video record",
    "media video parity record",
    "media audio parity record",
    "media audio record",
    "media feedback record",
];

fn decoders() -> Vec<Decoder> {
    vec![
        Decoder {
            name: "provider command document (a free Value field)",
            frame: Box::new(|nested| replace_trailing_nil(command_with_nil(), nested)),
            decode: |payload| decode_provider_command_document(&command_document(payload.to_vec())).map(drop),
        },
        Decoder {
            name: "provider session payload (a receipt's free Value field)",
            frame: Box::new(|nested| replace_trailing_nil(receipt_with_nil(), nested)),
            decode: |payload| {
                decode_provider_session_payload::<CultMeshProviderCommandReceiptWire>(&STANDARD.encode(payload))
                    .map(drop)
            },
        },
        Decoder {
            name: "provider connect evidence (an unknown key a named map skips)",
            frame: Box::new(|nested| add_unknown_entry(evidence_bytes(), nested)),
            decode: |payload| decode_provider_connect_evidence(payload).map(drop),
        },
        Decoder {
            name: "CultNet message (a free rmpv::Value tree)",
            frame: Box::new(|nested| add_unknown_entry(hello_bytes(), nested)),
            decode: |payload| decode_cultnet_message_from_slice(payload, CultNetWireContract::CultNetSchemaV0).map(drop),
        },
    ]
    .into_iter()
    .chain(media_records().into_iter().zip(MEDIA_NAMES).map(|(record, name)| Decoder {
        name,
        frame: Box::new(move |nested| media_wire_with_unknown_slot(&record, nested)),
        decode: |wire| decode_media_wire_record(wire).map(drop),
    }))
    .collect()
}

fn refusal(error: &Error) -> Option<PayloadTooDeep> {
    error.downcast_ref::<PayloadTooDeep>().copied()
}

// The document is one container itself, so the free part nests 255 at the limit and 256 past it.
#[test]
fn every_payload_decoder_accepts_256_and_refuses_257_for_arrays_maps_and_arms() {
    for decoder in decoders() {
        for (kind, nesting) in NESTINGS {
            let at = (decoder.frame)(&nesting(MAX_PAYLOAD_DEPTH - 1));
            let past = (decoder.frame)(&nesting(MAX_PAYLOAD_DEPTH));
            (decoder.decode)(&at).unwrap_or_else(|error| panic!("{} {kind} at the limit: {error:#}", decoder.name));
            let error = (decoder.decode)(&past).expect_err(&format!("{} {kind} past the limit", decoder.name));
            assert_eq!(refusal(&error), Some(TOO_DEEP), "{} {kind}: {error:#}", decoder.name);
            assert!(!format!("{error:#}").contains(CANARY), "{} {kind} echoed the payload", decoder.name);
        }
    }
}

#[test]
fn a_2000_deep_payload_is_a_typed_refusal_on_a_2_mib_stack() {
    let handle = std::thread::Builder::new()
        .stack_size(2 * 1024 * 1024)
        .spawn(|| {
            for decoder in decoders() {
                let deep = (decoder.frame)(&deep_marker_payload());
                let error = (decoder.decode)(&deep).expect_err(decoder.name);
                assert_eq!(refusal(&error), Some(TOO_DEEP), "{}: {error:#}", decoder.name);
            }
        })
        .unwrap();
    handle.join().expect("a decoder aborted or panicked on a 2 MiB stack");
}
