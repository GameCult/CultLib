//! The CultMesh Media Stream records are a wire contract shared by producers and
//! consumers that never link each other. These tests pin the properties a
//! consumer is entitled to rely on: positional layout, declared field order, and
//! tolerance of records written before an appended field existed.

use cultcache_rs::DatabaseEntry;
use cultnet_rs::{
    GAMECULT_MEDIA_AUDIO_PACKET_SCHEMA, GAMECULT_MEDIA_RECEIVER_FEEDBACK_SCHEMA,
    GAMECULT_MEDIA_VIDEO_ACCESS_UNIT_SCHEMA, GAMECULT_MEDIA_VIDEO_PARITY_SHARD_SCHEMA,
    GameCultMediaAudioPacketRecord, GameCultMediaReceiverFeedbackRecord,
    GameCultMediaVideoAccessUnitRecord, GameCultMediaVideoParityShardRecord,
};
use rmpv::Value;

fn access_unit() -> GameCultMediaVideoAccessUnitRecord {
    GameCultMediaVideoAccessUnitRecord {
        stream_id: "raven-primary-av".to_string(),
        session_id: "session-1".to_string(),
        frame_id: 4242,
        codec: "h264".to_string(),
        pts_ticks: 90_000,
        duration_ticks: 3_000,
        timebase_num: 1,
        timebase_den: 90_000,
        keyframe: true,
        dependency_frame_id: None,
        deadline_ticks: 108_000,
        chunk_index: 2,
        chunk_count: 5,
        payload: vec![0xDE, 0xAD, 0xBE, 0xEF],
    }
}

fn audio_packet() -> GameCultMediaAudioPacketRecord {
    GameCultMediaAudioPacketRecord {
        stream_id: "raven-primary-av".to_string(),
        session_id: "session-1".to_string(),
        packet_id: 77,
        codec: "opus".to_string(),
        pts_ticks: 48_000,
        duration_ticks: 960,
        timebase_num: 1,
        timebase_den: 48_000,
        deadline_ticks: 96_000,
        payload: vec![1, 2, 3],
    }
}

fn decode_as_value(bytes: &[u8]) -> Value {
    rmp_serde::from_slice(bytes).expect("records decode as generic MessagePack")
}

/// The declared `key` indices are the wire layout, not documentation. A consumer
/// in another language reads position 2 for `frame_id` and position 13 for
/// `payload`; reordering the struct would silently break it.
#[test]
fn video_access_unit_encodes_as_a_positional_array() {
    let encoded = rmp_serde::to_vec(&access_unit()).expect("encodes");
    let Value::Array(slots) = decode_as_value(&encoded) else {
        panic!("access units must encode as a positional array");
    };

    assert_eq!(slots.len(), 14, "field count is wire contract");
    assert_eq!(slots[0].as_str(), Some("raven-primary-av"));
    assert_eq!(slots[2].as_u64(), Some(4242), "slot 2 is frame_id");
    assert_eq!(slots[3].as_str(), Some("h264"));
    assert_eq!(slots[8].as_bool(), Some(true), "slot 8 is keyframe");
    assert!(slots[9].is_nil(), "an absent dependency stays nil, not 0");
    assert_eq!(slots[12].as_u64(), Some(5), "slot 12 is chunk_count");
    assert_eq!(
        slots[13].as_slice(),
        Some([0xDE, 0xAD, 0xBE, 0xEF].as_slice()),
        "slot 13 is the payload, carried as bin"
    );
}

/// Payloads must reach the wire as MessagePack `bin`, not as an array of
/// integers. The C#, TypeScript and Python runtimes all read byte fields as
/// `bin`, so a payload serialized as a sequence is unreadable by the reference
/// implementation, and costs 1.45x besides: 848 bytes encode to 1234 as an
/// integer array against 851 as `bin`.
///
/// This is what the derive's `bytes` attribute buys. `#[serde(with =
/// "serde_bytes")]` will not do it — `DatabaseEntry` emits its own `Serialize`
/// and reads only its own `cultcache` attribute namespace.
#[test]
fn payload_should_encode_as_messagepack_bin() {
    let encoded = rmp_serde::to_vec(&access_unit()).expect("encodes");
    let Value::Array(slots) = decode_as_value(&encoded) else {
        panic!("access units must encode as a positional array");
    };

    assert_eq!(
        slots[13].as_slice(),
        Some([0xDE, 0xAD, 0xBE, 0xEF].as_slice()),
        "payloads belong on the wire as bin, not as an array of integers"
    );
}

#[test]
fn audio_packets_are_whole_and_carry_their_own_timebase() {
    let encoded = rmp_serde::to_vec(&audio_packet()).expect("encodes");
    let Value::Array(slots) = decode_as_value(&encoded) else {
        panic!("audio packets must encode as a positional array");
    };

    assert_eq!(slots.len(), 10, "field count is wire contract");
    assert_eq!(slots[2].as_u64(), Some(77), "slot 2 is packet_id");
    assert_eq!(slots[6].as_u64(), Some(1), "slot 6 is timebase_num");
    assert_eq!(slots[7].as_u64(), Some(48_000), "slot 7 is timebase_den");

    let decoded: GameCultMediaAudioPacketRecord =
        rmp_serde::from_slice(&encoded).expect("round-trips");
    assert_eq!(decoded, audio_packet());
}

#[test]
fn parity_shards_carry_the_geometry_recovery_needs() {
    let shard = GameCultMediaVideoParityShardRecord {
        stream_id: "raven-primary-av".to_string(),
        session_id: "session-1".to_string(),
        frame_id: 4242,
        codec: "h264".to_string(),
        pts_ticks: 90_000,
        duration_ticks: 3_000,
        timebase_num: 1,
        timebase_den: 90_000,
        keyframe: false,
        dependency_frame_id: Some(4241),
        deadline_ticks: 108_000,
        chunk_count: 5,
        parity_index: 1,
        parity_count: 2,
        chunk_payload_bytes: 1024,
        last_chunk_payload_bytes: 512,
        payload: vec![9, 9, 9],
    };

    let encoded = rmp_serde::to_vec(&shard).expect("encodes");
    let Value::Array(slots) = decode_as_value(&encoded) else {
        panic!("parity shards must encode as a positional array");
    };

    assert_eq!(slots.len(), 17, "field count is wire contract");
    assert_eq!(slots[12].as_u64(), Some(1), "slot 12 is parity_index");
    assert_eq!(slots[13].as_u64(), Some(2), "slot 13 is parity_count");
    assert_eq!(
        slots[15].as_u64(),
        Some(512),
        "slot 15 is last_chunk_payload_bytes, which recovery needs to size the tail"
    );

    let decoded: GameCultMediaVideoParityShardRecord =
        rmp_serde::from_slice(&encoded).expect("round-trips");
    assert_eq!(decoded, shard);
}

/// `missing_video_chunk_keys` was appended after producers were already writing
/// feedback. A record written without it must still decode, or the append was a
/// breaking change wearing a `default` attribute.
#[test]
fn receiver_feedback_decodes_records_written_before_its_last_field() {
    let without_appended_field = Value::Array(vec![
        Value::from("raven-primary-av"),
        Value::from("session-1"),
        Value::from("starfire-obs"),
        Value::from(4200u64),
        Value::Array(vec![Value::from(4198u64)]),
        Value::Array(vec![Value::from(4199u64)]),
        Value::from(true),
        Value::from(1_500i64),
        Value::from(9_000i64),
        Value::from("2026-09-09T00:00:00Z"),
    ]);

    let mut bytes = Vec::new();
    rmpv::encode::write_value(&mut bytes, &without_appended_field).expect("writes legacy record");

    let decoded: GameCultMediaReceiverFeedbackRecord =
        rmp_serde::from_slice(&bytes).expect("a pre-append record must still decode");

    assert_eq!(decoded.receiver_id, "starfire-obs");
    assert_eq!(decoded.highest_decodable_frame_id, Some(4200));
    assert!(decoded.requested_keyframe);
    assert!(
        decoded.missing_video_chunk_keys.is_empty(),
        "the appended field defaults rather than failing the decode"
    );
}

/// The published constants and the derived type identity must not drift apart;
/// a consumer resolving by schema id and a producer registering by type would
/// then disagree without either failing to compile.
#[test]
fn published_schema_constants_match_the_derived_identity() {
    assert_eq!(
        GameCultMediaVideoAccessUnitRecord::SCHEMA_NAME,
        GAMECULT_MEDIA_VIDEO_ACCESS_UNIT_SCHEMA
    );
    assert_eq!(
        GameCultMediaVideoParityShardRecord::SCHEMA_NAME,
        GAMECULT_MEDIA_VIDEO_PARITY_SHARD_SCHEMA
    );
    assert_eq!(
        GameCultMediaAudioPacketRecord::SCHEMA_NAME,
        GAMECULT_MEDIA_AUDIO_PACKET_SCHEMA
    );
    assert_eq!(
        GameCultMediaReceiverFeedbackRecord::SCHEMA_NAME,
        GAMECULT_MEDIA_RECEIVER_FEEDBACK_SCHEMA
    );

    assert_eq!(
        GameCultMediaAudioPacketRecord::TYPE,
        "gamecult.media_audio_packet"
    );
}
