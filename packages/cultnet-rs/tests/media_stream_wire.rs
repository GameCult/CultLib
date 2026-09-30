//! The envelope, and one proof that removing Muninn's shadow structs changed
//! nothing on the wire.

use cultnet_rs::{
    CultNetRudpDeliveredFrame, CultNetRudpSendOptions, CultNetRudpSession, CultNetRudpSessionOptions,
    GAMECULT_MEDIA_MAX_WIRE_BYTES, RudpTransportProfileOptions, create_rudp_transport_profile,
    GAMECULT_MEDIA_CHANNEL, GameCultMediaAudioPacketRecord, GameCultMediaAudioParityShardRecord,
    GameCultMediaReceiverFeedbackRecord,
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
            fec_scheme: "rs-gf256-v1".to_string(),
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
            stream_id: "s".to_string(),
            session_id: "x".to_string(),
            codec: "opus".to_string(),
            fec_scheme: "rs-gf256-v1".to_string(),
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

fn video_parity() -> GameCultMediaVideoParityShardRecord {
    GameCultMediaVideoParityShardRecord {
        stream_id: "s".to_string(),
        session_id: "x".to_string(),
        frame_id: 7,
        codec: "h264".to_string(),
        pts_ticks: 0,
        duration_ticks: 1,
        timebase_num: 1,
        timebase_den: 90_000,
        keyframe: false,
        dependency_frame_id: None,
        deadline_ticks: 1,
        chunk_count: 20,
        fec_scheme: "rs-gf256-v1".to_string(),
        block_index: 1,
        block_count: 2,
        block_data_start: 10,
        block_data_count: 10,
        parity_index: 1,
        parity_count: 3,
        shard_payload_bytes: 4,
        last_chunk_payload_bytes: 2,
        payload: vec![1, 2, 3, 4],
    }
}

fn audio_parity() -> GameCultMediaAudioParityShardRecord {
    GameCultMediaAudioParityShardRecord {
        stream_id: "s".to_string(),
        session_id: "x".to_string(),
        codec: "opus".to_string(),
        fec_scheme: "rs-gf256-v1".to_string(),
        base_packet_id: 8,
        base_pts_ticks: 0,
        packet_duration_ticks: 960,
        timebase_num: 1,
        timebase_den: 48_000,
        deadline_ticks: 4_000,
        data_shard_count: 4,
        parity_index: 1,
        parity_shard_count: 2,
        shard_payload_bytes: 3,
        payload: vec![4, 5, 6],
    }
}

fn decodes(record: GameCultMediaWireRecord) -> Result<GameCultMediaWireRecord, String> {
    let wire = encode_media_wire_record(&record, provenance()).map_err(|error| error.to_string())?;
    decode_media_wire_record(&wire).map_err(|error| error.to_string())
}

/// A parity key names the block as well as the shard, or two blocks' parity
/// index 0 would share an address.
#[test]
fn parity_record_keys_name_the_block_and_the_shard() {
    let mut other_block = video_parity();
    other_block.block_index = 0;
    let mut other_shard = video_parity();
    other_shard.parity_index = 0;
    let keys = [
        GameCultMediaWireRecord::VideoParity(video_parity()).record_key(),
        GameCultMediaWireRecord::VideoParity(other_block).record_key(),
        GameCultMediaWireRecord::VideoParity(other_shard).record_key(),
    ];
    assert_eq!(keys[0], "s:x:video-parity:7:1:1");
    assert_ne!(keys[0], keys[1]);
    assert_ne!(keys[0], keys[2]);

    let audio = GameCultMediaWireRecord::AudioParity(audio_parity()).record_key();
    assert_eq!(audio, "s:x:audio-parity:8:1");
    let mut other = audio_parity();
    other.parity_index = 0;
    assert_ne!(audio, GameCultMediaWireRecord::AudioParity(other).record_key());
}

#[test]
fn a_well_formed_parity_record_is_admitted() {
    assert!(decodes(GameCultMediaWireRecord::VideoParity(video_parity())).is_ok());
    assert!(decodes(GameCultMediaWireRecord::AudioParity(audio_parity())).is_ok());
}

/// The decoder is where a consumer's idea of a well-formed shard is enforced.
/// Each corruption below would otherwise reach the recovery arithmetic.
#[test]
fn a_malformed_video_parity_record_is_refused_with_its_reason() {
    let cases: Vec<(&str, Box<dyn Fn(&mut GameCultMediaVideoParityShardRecord)>)> = vec![
        ("fec_scheme", Box::new(|r| r.fec_scheme = "xor-v0".to_string())),
        ("block_index", Box::new(|r| r.block_index = 2)),
        ("block_index", Box::new(|r| r.block_count = 0)),
        ("inside the frame", Box::new(|r| r.block_data_start = 11)),
        ("inside the frame", Box::new(|r| r.block_data_count = 0)),
        ("parity_index", Box::new(|r| r.parity_index = 3)),
        ("parity_index", Box::new(|r| r.parity_count = 0)),
        ("256 shards", Box::new(|r| {
            r.block_data_count = 254;
            r.chunk_count = 300;
            r.parity_count = 3;
        })),
        ("shard_payload_bytes", Box::new(|r| {
            r.shard_payload_bytes = 0;
            r.payload.clear();
        })),
        ("last chunk length", Box::new(|r| r.last_chunk_payload_bytes = 0)),
        ("last chunk length", Box::new(|r| r.last_chunk_payload_bytes = 5)),
        ("declared shard length", Box::new(|r| r.payload.push(0))),
        ("chunk_count", Box::new(|r| r.chunk_count = 0)),
        ("256 shards", Box::new(|r| {
            r.block_data_count = 256;
            r.chunk_count = 300;
            r.parity_count = 1;
            r.parity_index = 0;
        })),
        ("deadline_ticks", Box::new(|r| r.deadline_ticks = -1)),
    ];
    for (expected, corrupt) in cases {
        let mut record = video_parity();
        corrupt(&mut record);
        let error = decodes(GameCultMediaWireRecord::VideoParity(record)).unwrap_err();
        assert!(error.contains(expected), "expected {expected:?} in {error:?}");
    }
}

#[test]
fn a_malformed_audio_parity_record_is_refused_with_its_reason() {
    let cases: Vec<(&str, Box<dyn Fn(&mut GameCultMediaAudioParityShardRecord)>)> = vec![
        ("fec_scheme", Box::new(|r| r.fec_scheme = String::new())),
        ("stripe", Box::new(|r| r.parity_index = 2)),
        ("stripe", Box::new(|r| r.data_shard_count = 0)),
        ("stripe", Box::new(|r| r.data_shard_count = 255)),
        ("timing", Box::new(|r| r.packet_duration_ticks = 0)),
        ("base_pts_ticks", Box::new(|r| r.deadline_ticks = -1)),
        ("packet id or pts range", Box::new(|r| r.base_packet_id = u64::MAX - 2)),
        ("packet id or pts range", Box::new(|r| {
            r.base_pts_ticks = i64::MAX - 960 * 3 + 1;
            r.deadline_ticks = i64::MAX;
        })),
        ("declared shard length", Box::new(|r| { r.payload.pop(); })),
        ("codec", Box::new(|r| r.codec = String::new())),
        ("stream_id", Box::new(|r| r.stream_id = String::new())),
        ("stream_id", Box::new(|r| r.session_id = String::new())),
        ("timing", Box::new(|r| r.timebase_num = 0)),
        ("timing", Box::new(|r| r.timebase_den = 0)),
    ];
    for (expected, corrupt) in cases {
        let mut record = audio_parity();
        corrupt(&mut record);
        let error = decodes(GameCultMediaWireRecord::AudioParity(record)).unwrap_err();
        assert!(error.contains(expected), "expected {expected:?} in {error:?}");
    }
}

/// The 256-shard ceiling and the deadline floor are inclusive on the side that
/// keeps a valid record valid.
#[test]
fn records_on_the_edge_of_each_bound_are_admitted() {
    let mut video = video_parity();
    video.chunk_count = 300;
    video.block_data_count = 254;
    video.parity_count = 2;
    video.parity_index = 1;
    assert!(decodes(GameCultMediaWireRecord::VideoParity(video)).is_ok(), "254 + 2 = 256 shards");

    let mut audio = audio_parity();
    audio.data_shard_count = 254;
    audio.parity_shard_count = 2;
    assert!(decodes(GameCultMediaWireRecord::AudioParity(audio)).is_ok(), "254 + 2 = 256 shards");

    let mut audio = audio_parity();
    audio.deadline_ticks = audio.base_pts_ticks;
    assert!(decodes(GameCultMediaWireRecord::AudioParity(audio)).is_ok(), "a deadline at the base pts");

    let mut audio = audio_parity();
    audio.base_packet_id = u64::MAX - 3;
    assert!(decodes(GameCultMediaWireRecord::AudioParity(audio)).is_ok(), "the last packet id is u64::MAX");

    let mut audio = audio_parity();
    audio.base_pts_ticks = i64::MAX - 960 * 3;
    audio.deadline_ticks = i64::MAX;
    assert!(decodes(GameCultMediaWireRecord::AudioParity(audio)).is_ok(), "the last pts is i64::MAX");
}

// ---------------------------------------------------------------------------
// The ceiling on one media record
// ---------------------------------------------------------------------------

fn audio_parity(payload_bytes: usize, deadline_ticks: i64) -> GameCultMediaAudioParityShardRecord {
    GameCultMediaAudioParityShardRecord {
        stream_id: "s".to_string(),
        session_id: "x".to_string(),
        codec: "opus".to_string(),
        fec_scheme: "rs-gf256-v1".to_string(),
        base_packet_id: 8,
        base_pts_ticks: 0,
        packet_duration_ticks: 100,
        timebase_num: 1,
        timebase_den: 48_000,
        deadline_ticks,
        data_shard_count: 4,
        parity_index: 0,
        parity_shard_count: 2,
        shard_payload_bytes: payload_bytes as u32,
        payload: vec![7; payload_bytes],
    }
}

fn video_parity(payload_bytes: usize) -> GameCultMediaVideoParityShardRecord {
    GameCultMediaVideoParityShardRecord {
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
        fec_scheme: "rs-gf256-v1".to_string(),
        block_index: 0,
        block_count: 1,
        block_data_start: 0,
        block_data_count: 4,
        parity_index: 1,
        parity_count: 2,
        shard_payload_bytes: payload_bytes as u32,
        last_chunk_payload_bytes: 2,
        payload: vec![7; payload_bytes],
    }
}

fn wire_len(record: &GameCultMediaWireRecord) -> usize {
    encode_media_wire_record(record, provenance()).unwrap().len()
}

/// The payload length whose wrapped record is exactly `wire_bytes` long, found
/// from the record's own linear growth in its payload.
fn payload_for_wire_len(
    wire_bytes: usize,
    wrap: &dyn Fn(usize) -> GameCultMediaWireRecord,
) -> usize {
    let probe = 1_000;
    let payload = probe + wire_bytes - wire_len(&wrap(probe));
    assert_eq!(wire_len(&wrap(payload)), wire_bytes, "the record grows one byte per payload byte here");
    payload
}

/// RUDP would reassemble a 16 MiB record, and recovery work scales with the
/// shard length, so the decoder holds every record to one ceiling.
#[test]
fn a_media_record_one_byte_over_the_ceiling_is_refused_at_decode() {
    let ceiling = GAMECULT_MEDIA_MAX_WIRE_BYTES;
    let wraps: [(&str, Box<dyn Fn(usize) -> GameCultMediaWireRecord>); 2] = [
        (
            "audio parity",
            Box::new(|payload| GameCultMediaWireRecord::AudioParity(audio_parity(payload, 300))),
        ),
        (
            "video parity",
            Box::new(|payload| GameCultMediaWireRecord::VideoParity(video_parity(payload))),
        ),
    ];
    for (name, wrap) in &wraps {
        let at = payload_for_wire_len(ceiling, wrap.as_ref());
        let wire = encode_media_wire_record(&wrap(at), provenance()).unwrap();
        assert_eq!(wire.len(), ceiling);
        decode_media_wire_record(&wire)
            .unwrap_or_else(|error| panic!("{name} at the ceiling is refused: {error}"));

        let over = encode_media_wire_record(&wrap(at + 1), provenance()).unwrap();
        assert_eq!(over.len(), ceiling + 1);
        let error = decode_media_wire_record(&over).unwrap_err();
        assert!(error.to_string().contains("ceiling"), "{name}: {error}");
    }
}

#[test]
fn a_jumbo_frame_sized_record_is_accepted() {
    let wire = encode_media_wire_record(
        &GameCultMediaWireRecord::AudioParity(audio_parity(8_800, 300)),
        provenance(),
    )
    .unwrap();
    assert!(wire.len() > 8_800 && wire.len() < GAMECULT_MEDIA_MAX_WIRE_BYTES);
    decode_media_wire_record(&wire).expect("a jumbo-frame record decodes");
}

/// Recovery hands every recovered packet the block's deadline, so a parity
/// whose deadline precedes the last packet's pts would recover a packet that
/// fails `validate_audio_record`.
#[test]
fn audio_parity_must_not_have_a_deadline_before_its_last_packets_pts() {
    // Four packets, 100 ticks each from 0: the last plays at 300.
    let decodes = |deadline_ticks: i64| {
        let wire = encode_media_wire_record(
            &GameCultMediaWireRecord::AudioParity(audio_parity(8, deadline_ticks)),
            provenance(),
        )
        .unwrap();
        decode_media_wire_record(&wire)
    };
    assert!(decodes(300).is_ok());
    assert!(decodes(301).is_ok());
    let error = decodes(299).unwrap_err();
    assert!(error.to_string().contains("last pts_ticks"), "{error}");
    let error = decodes(0).unwrap_err();
    assert!(error.to_string().contains("last pts_ticks"), "{error}");
}

// ---------------------------------------------------------------------------
// The media channel's own payload cap
// ---------------------------------------------------------------------------

fn connected_session() -> CultNetRudpSession {
    let mut session = CultNetRudpSession::new(CultNetRudpSessionOptions {
        connection_id: 9,
        ..CultNetRudpSessionOptions::default()
    });
    session.assume_connected(0);
    session
}

const FRAGMENT_BYTES: usize = 16 * 1024;

fn media_payload_limit(session_limit: Option<u32>) -> Option<u32> {
    let profile = create_rudp_transport_profile(
        "raven",
        RudpTransportProfileOptions {
            max_payload_bytes: session_limit,
            ..RudpTransportProfileOptions::default()
        },
    );
    profile.transports[0]
        .channels
        .iter()
        .find(|channel| channel.channel_id == GAMECULT_MEDIA_CHANNEL)
        .expect("the profile advertises the media channel")
        .max_payload_bytes
}

#[test]
fn the_media_channel_advertises_the_ceiling_or_a_tighter_session_cap() {
    let ceiling = GAMECULT_MEDIA_MAX_WIRE_BYTES as u32;
    assert_eq!(media_payload_limit(None), Some(ceiling));
    assert_eq!(media_payload_limit(Some(16 * 1024 * 1024)), Some(ceiling));
    assert_eq!(media_payload_limit(Some(4_096)), Some(4_096));
}

#[test]
fn a_session_refuses_to_send_more_than_the_ceiling_on_the_media_channel_only() {
    let ceiling = GAMECULT_MEDIA_MAX_WIRE_BYTES;
    let send = |channel: &str, payload: usize| {
        connected_session().send_many(
            channel,
            vec![0; payload],
            CultNetRudpSendOptions::default(),
            Some(FRAGMENT_BYTES),
        )
    };
    assert!(send(GAMECULT_MEDIA_CHANNEL, ceiling).is_ok());
    let error = send(GAMECULT_MEDIA_CHANNEL, ceiling + 1).unwrap_err();
    assert!(error.to_string().contains("max_payload_bytes"), "{error}");
    assert!(send("schema", ceiling + 1).is_ok(), "other channels keep the session cap");
}

/// Reassembly is where memory is spent, so the receiver enforces the ceiling
/// itself, not only the sender.
#[test]
fn a_session_refuses_to_reassemble_more_than_the_ceiling_on_the_media_channel() {
    let ceiling = GAMECULT_MEDIA_MAX_WIRE_BYTES;
    let packets = connected_session()
        .send_many(
            "schema",
            vec![0; ceiling + 1],
            CultNetRudpSendOptions::default(),
            Some(FRAGMENT_BYTES),
        )
        .unwrap();
    assert!(packets.len() > 1);

    let deliver = |channel: &str| -> anyhow::Result<Vec<CultNetRudpDeliveredFrame>> {
        let mut receiver = connected_session();
        let mut delivered = Vec::new();
        for packet in &packets {
            let mut packet = packet.clone();
            packet.channel_id = channel.to_string();
            delivered.extend(receiver.receive(&packet, 0)?.delivered);
        }
        Ok(delivered)
    };
    let frames = deliver("schema").expect("the same fragments reassemble on another channel");
    assert_eq!(frames.len(), 1);
    assert_eq!(frames[0].payload.len(), ceiling + 1);
    let error = deliver(GAMECULT_MEDIA_CHANNEL).unwrap_err();
    assert!(error.to_string().contains("max_payload_bytes"), "{error}");
}
