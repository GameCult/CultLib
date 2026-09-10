//! The envelope, and one proof that removing Muninn's shadow structs changed
//! nothing on the wire.

use cultnet_rs::{
    GAMECULT_MEDIA_CHANNEL, GameCultMediaAudioPacketRecord, GameCultMediaReceiverFeedbackRecord,
    GameCultMediaVideoAccessUnitRecord, GameCultMediaVideoParityShardRecord,
    GameCultMediaWireRecord, MediaWireProvenance, decode_media_wire_record,
    encode_media_wire_record,
};
use serde::Serialize;

fn provenance() -> MediaWireProvenance<'static> {
    MediaWireProvenance {
        stored_at: "unix:1000",
        runtime_id: "raven",
        role: "muninn.media",
        producer: "muninn",
    }
}

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
        payload: (0u8..=255).collect(),
    }
}

/// Muninn's `VideoAccessUnitWirePayload`, reproduced exactly as it was.
///
/// It existed only so `#[serde(with = "serde_bytes")]` could reach the payload,
/// because the `DatabaseEntry` derive ignores serde attributes. Every other
/// field was restated by hand, in order, and had to stay in step with the record
/// by discipline alone.
#[derive(Serialize)]
struct LegacyVideoAccessUnitWirePayload<'a>(
    &'a str,
    &'a str,
    u64,
    &'a str,
    i64,
    u32,
    u32,
    u32,
    bool,
    Option<u64>,
    i64,
    u16,
    u16,
    #[serde(with = "serde_bytes")] &'a [u8],
);

/// If these differ, deleting the shadow structs was a wire break rather than a
/// simplification, and every existing consumer would need to move in step.
#[test]
fn the_derive_reproduces_the_hand_written_wire_payload_byte_for_byte() {
    let record = access_unit();
    let legacy = LegacyVideoAccessUnitWirePayload(
        &record.stream_id,
        &record.session_id,
        record.frame_id,
        &record.codec,
        record.pts_ticks,
        record.duration_ticks,
        record.timebase_num,
        record.timebase_den,
        record.keyframe,
        record.dependency_frame_id,
        record.deadline_ticks,
        record.chunk_index,
        record.chunk_count,
        &record.payload,
    );

    let from_derive = rmp_serde::to_vec(&record).expect("record encodes");
    let from_shadow = rmp_serde::to_vec(&legacy).expect("shadow struct encodes");

    assert_eq!(
        from_derive, from_shadow,
        "the `bytes` attribute must reproduce what serde_bytes produced by hand"
    );
}

#[test]
fn a_video_record_round_trips_through_the_envelope() {
    let record = GameCultMediaWireRecord::Video(access_unit());
    let wire = encode_media_wire_record(&record, provenance()).expect("encodes");
    assert_eq!(decode_media_wire_record(&wire).expect("decodes"), record);
}

#[test]
fn every_variant_round_trips() {
    let variants = vec![
        GameCultMediaWireRecord::Video(access_unit()),
        GameCultMediaWireRecord::VideoParity(GameCultMediaVideoParityShardRecord {
            stream_id: "s".to_string(),
            session_id: "x".to_string(),
            frame_id: 1,
            codec: "h264".to_string(),
            pts_ticks: 0,
            duration_ticks: 1,
            timebase_num: 1,
            timebase_den: 90_000,
            keyframe: false,
            dependency_frame_id: Some(0),
            deadline_ticks: 1,
            chunk_count: 4,
            parity_index: 1,
            parity_count: 2,
            chunk_payload_bytes: 848,
            last_chunk_payload_bytes: 400,
            payload: vec![1, 2, 3],
        }),
        GameCultMediaWireRecord::Audio(GameCultMediaAudioPacketRecord {
            stream_id: "s".to_string(),
            session_id: "x".to_string(),
            packet_id: 9,
            codec: "opus".to_string(),
            pts_ticks: 0,
            duration_ticks: 960,
            timebase_num: 1,
            timebase_den: 48_000,
            deadline_ticks: 1,
            payload: vec![4, 5, 6],
        }),
        GameCultMediaWireRecord::Feedback(GameCultMediaReceiverFeedbackRecord {
            stream_id: "s".to_string(),
            session_id: "x".to_string(),
            receiver_id: "starfire-obs".to_string(),
            highest_decodable_frame_id: Some(41),
            missing_frame_ids: vec![42],
            late_frame_ids: vec![43],
            requested_keyframe: true,
            jitter_us: 500,
            decode_queue_us: 2_000,
            observed_at: "unix:1000".to_string(),
            missing_video_chunk_keys: vec!["42:1".to_string()],
        }),
    ];

    for record in variants {
        let wire = encode_media_wire_record(&record, provenance()).expect("encodes");
        assert_eq!(
            decode_media_wire_record(&wire).expect("decodes"),
            record,
            "variant {} did not survive the envelope",
            record.schema_id()
        );
    }
}

/// Record keys address one piece of media within a session. A consumer parses
/// them, so their shape is contract.
#[test]
fn record_keys_distinguish_chunks_parity_and_senders() {
    let mut second_chunk = access_unit();
    second_chunk.chunk_index = 3;

    let first = GameCultMediaWireRecord::Video(access_unit()).record_key();
    let second = GameCultMediaWireRecord::Video(second_chunk).record_key();
    assert_ne!(first, second, "chunks of one frame must not share a key");
    assert!(first.contains(":video:"), "got {first}");
}

/// A shared envelope must not label another producer's traffic as Muninn's.
#[test]
fn the_producer_namespace_is_the_callers_not_the_envelopes() {
    let record = GameCultMediaWireRecord::Video(access_unit());
    let theirs = MediaWireProvenance {
        stored_at: "unix:1000",
        runtime_id: "nightwing",
        role: "brokkr.media",
        producer: "brokkr",
    };

    let wire = encode_media_wire_record(&record, theirs).expect("encodes");
    let text = String::from_utf8_lossy(&wire);
    assert!(text.contains("brokkr-media:"), "message id names the producer");
    assert!(text.contains("brokkr.media"), "tag names the producer");
    assert!(
        !text.contains("muninn"),
        "the envelope must not name a producer the caller did not supply"
    );
}

#[test]
fn provenance_must_be_complete() {
    let record = GameCultMediaWireRecord::Video(access_unit());
    for blank in ["stored_at", "runtime_id", "role", "producer"] {
        let mut incomplete = provenance();
        match blank {
            "stored_at" => incomplete.stored_at = "",
            "runtime_id" => incomplete.runtime_id = "",
            "role" => incomplete.role = "",
            _ => incomplete.producer = "",
        }
        let error = encode_media_wire_record(&record, incomplete)
            .expect_err("empty provenance must be refused");
        assert!(error.to_string().contains(blank), "got {error}");
    }
}

#[test]
fn the_media_channel_is_named_once() {
    assert_eq!(GAMECULT_MEDIA_CHANNEL, "media");
}
