//! CultMesh Media Stream: the typed contract for carrying live audio and video
//! over CultNet.
//!
//! These records describe a media stream and nothing about who produced it. A
//! capture broker, an editor runtime, a recorder, or a synthetic source may all
//! publish them; an OBS bridge, a compositor, a recorder, or a browser lowering
//! may all consume them. Producers advertise their streams through Odin and
//! consumers resolve them there, so neither end needs to name the other.
//!
//! They lived in `odin-core` as `muninn.media_*`, named after the one producer
//! that happened to emit them first. Every field here is producer-agnostic — the
//! name was the only coupling, and a second producer would have had to
//! impersonate Muninn or fork the shape.
//!
//! # Layout is wire contract
//!
//! CultCache encodes these positionally. Field order and `key` indices are the
//! wire format: append new fields with `default` at the next free index, never
//! reorder or renumber. `missing_video_chunk_keys` shows the pattern.
//!
//! Payloads carry `bytes` so they serialize as MessagePack `bin` rather than an
//! array of integers, which is what the C#, TypeScript and Python runtimes
//! expect and what keeps a 848-byte payload at 851 bytes instead of 1234. Note
//! that this is the derive's own attribute — `#[serde(with = "serde_bytes")]`
//! is inert here, because `DatabaseEntry` writes its own `Serialize`.
//!
//! # Timebase
//!
//! `pts_ticks`, `duration_ticks` and `deadline_ticks` are expressed in
//! `timebase_num / timebase_den` units, carried per record so a consumer never
//! has to assume a producer's clock. `deadline_ticks` is the presentation
//! deadline past which a late arrival is worth dropping rather than displaying.
//!
//! # Reliability
//!
//! Nothing here specifies a delivery guarantee. Whether a stream rides a
//! reliable or lossy CultNet channel, and whether it carries parity, is a
//! transport decision made per stream — not a property of the payload shape.
//! Video parity shards exist for producers that choose forward error correction;
//! audio currently has no parity record, and a producer emitting audio on a
//! lossy channel needs one.

use cultcache_rs::DatabaseEntry;

pub const GAMECULT_MEDIA_VIDEO_ACCESS_UNIT_SCHEMA: &str = "gamecult.media_video_access_unit.v1";
pub const GAMECULT_MEDIA_VIDEO_PARITY_SHARD_SCHEMA: &str = "gamecult.media_video_parity_shard.v2";
pub const GAMECULT_MEDIA_AUDIO_PACKET_SCHEMA: &str = "gamecult.media_audio_packet.v1";
pub const GAMECULT_MEDIA_RECEIVER_FEEDBACK_SCHEMA: &str = "gamecult.media_receiver_feedback.v1";
pub const GAMECULT_MEDIA_STREAM_ADVERTISEMENT_SCHEMA: &str =
    "gamecult.media_stream_advertisement.v1";
pub const GAMECULT_MEDIA_STREAM_REQUEST_SCHEMA: &str = "gamecult.media_stream_request.v1";

/// A media stream a producer can serve on request, as it appears in Odin.
///
/// Odin admits these without any registration: it derives the document type
/// from the schema id (`prefix.vN` -> `prefix`) and refuses only a short list
/// of authority-owned types (`gamecult.runtime_presence_health`,
/// `odin.runtime_topology_correlation`, `idunn.expected_incarnation`,
/// `idunn.runtime_activation`, `idunn.process_write_lease`,
/// `gamecult.service_trust_anchor`). A new record whose type collided with one
/// of those would be rejected at the catalog with no mention of the collision.
///
/// This is the record a consumer's picker is built from: which streams exist,
/// which video and audio sources each can capture, which codecs it can encode,
/// and the defaults it would use. It is state, not media — it travels as a
/// CultMesh document through Odin, never on the media channel — and it names no
/// consumer. `producer_id` is the runtime that answers requests for it.
///
/// Keyed by `stream_id`.
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(
    type = "gamecult.media_stream_advertisement",
    schema = "gamecult.media_stream_advertisement.v1"
)]
pub struct GameCultMediaStreamAdvertisementRecord {
    #[cultcache(key = 0)]
    pub stream_id: String,
    #[cultcache(key = 1)]
    pub producer_id: String,
    #[cultcache(key = 2)]
    pub label: String,
    /// `available`, `streaming`, or `unavailable`.
    #[cultcache(key = 3)]
    pub state: String,
    #[cultcache(key = 4)]
    pub video_source_ids: Vec<String>,
    #[cultcache(key = 5)]
    pub video_source_labels: Vec<String>,
    #[cultcache(key = 6)]
    pub audio_source_ids: Vec<String>,
    #[cultcache(key = 7)]
    pub audio_source_labels: Vec<String>,
    /// Codecs the producer can encode video as, e.g. `h264`.
    #[cultcache(key = 8)]
    pub video_codecs: Vec<String>,
    /// Codecs the producer can emit audio as, e.g. `pcm-f32le-interleaved`.
    #[cultcache(key = 9)]
    pub audio_codecs: Vec<String>,
    #[cultcache(key = 10)]
    pub audio_sample_rate: u32,
    #[cultcache(key = 11)]
    pub audio_channels: u32,
    #[cultcache(key = 12)]
    pub default_video_bitrate_kbps: u32,
    #[cultcache(key = 13)]
    pub default_latency_budget_ms: u32,
    #[cultcache(key = 14)]
    pub media_packet_bytes: u32,
    /// The CultNet RUDP connection id the producer dials the receiver with.
    #[cultcache(key = 15)]
    pub media_connection_id: u32,
    #[cultcache(key = 16)]
    pub updated_at: String,
}

/// A consumer asking a producer to start or stop serving a stream to it.
///
/// The producer answers by rewriting `state` and `detail` on the same record
/// key, so a consumer watches one document for the outcome. A newer request for
/// the same `stream_id` and `receiver_id` supersedes an older one.
///
/// Keyed by `media_stream_request_key(stream_id, receiver_id)`.
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(
    type = "gamecult.media_stream_request",
    schema = "gamecult.media_stream_request.v1"
)]
pub struct GameCultMediaStreamRequestRecord {
    #[cultcache(key = 0)]
    pub request_id: String,
    #[cultcache(key = 1)]
    pub stream_id: String,
    #[cultcache(key = 2)]
    pub producer_id: String,
    #[cultcache(key = 3)]
    pub receiver_id: String,
    /// `host:port` the producer should dial with the advertised connection id.
    #[cultcache(key = 4)]
    pub receiver_endpoint: String,
    /// `start` or `stop`.
    #[cultcache(key = 5)]
    pub action: String,
    /// `pending`, `running`, `stopped`, or `failed`; the producer owns it after
    /// `pending`.
    #[cultcache(key = 6)]
    pub state: String,
    /// Empty means no video.
    #[cultcache(key = 7)]
    pub video_source_id: String,
    /// Empty means no audio.
    #[cultcache(key = 8)]
    pub audio_source_id: String,
    #[cultcache(key = 9)]
    pub video_codec: String,
    #[cultcache(key = 10)]
    pub audio_codec: String,
    #[cultcache(key = 11)]
    pub video_bitrate_kbps: u32,
    #[cultcache(key = 12)]
    pub latency_budget_ms: u32,
    #[cultcache(key = 13)]
    pub media_packet_bytes: u32,
    #[cultcache(key = 14)]
    pub detail: String,
    #[cultcache(key = 15)]
    pub updated_at: String,
}

pub const MEDIA_STREAM_REQUEST_ACTIONS: [&str; 2] = ["start", "stop"];
pub const MEDIA_STREAM_REQUEST_STATES: [&str; 4] = ["pending", "running", "stopped", "failed"];
pub const MEDIA_STREAM_ADVERTISEMENT_STATES: [&str; 3] = ["available", "streaming", "unavailable"];

/// One live request per stream per receiver: a newer one replaces the older.
pub fn media_stream_request_key(stream_id: &str, receiver_id: &str) -> String {
    format!("{stream_id}:{receiver_id}")
}

pub fn validate_media_stream_advertisement(
    record: &GameCultMediaStreamAdvertisementRecord,
) -> anyhow::Result<()> {
    use anyhow::anyhow;
    for (name, value) in [
        ("stream_id", &record.stream_id),
        ("producer_id", &record.producer_id),
        ("label", &record.label),
        ("updated_at", &record.updated_at),
    ] {
        if value.is_empty() {
            return Err(anyhow!("media stream advertisement {name} must be non-empty"));
        }
    }
    if !MEDIA_STREAM_ADVERTISEMENT_STATES.contains(&record.state.as_str()) {
        return Err(anyhow!(
            "media stream advertisement state {:?} is not one of {:?}",
            record.state,
            MEDIA_STREAM_ADVERTISEMENT_STATES
        ));
    }
    if record.video_source_ids.len() != record.video_source_labels.len() {
        return Err(anyhow!(
            "media stream advertisement has {} video source ids and {} labels",
            record.video_source_ids.len(),
            record.video_source_labels.len()
        ));
    }
    if record.audio_source_ids.len() != record.audio_source_labels.len() {
        return Err(anyhow!(
            "media stream advertisement has {} audio source ids and {} labels",
            record.audio_source_ids.len(),
            record.audio_source_labels.len()
        ));
    }
    if !record.audio_source_ids.is_empty() && (record.audio_sample_rate == 0 || record.audio_channels == 0) {
        return Err(anyhow!(
            "media stream advertisement offers audio without a sample rate and channel count"
        ));
    }
    if record.media_connection_id == 0 {
        return Err(anyhow!("media stream advertisement media_connection_id must be non-zero"));
    }
    Ok(())
}

pub fn validate_media_stream_request(
    record: &GameCultMediaStreamRequestRecord,
) -> anyhow::Result<()> {
    use anyhow::anyhow;
    for (name, value) in [
        ("request_id", &record.request_id),
        ("stream_id", &record.stream_id),
        ("producer_id", &record.producer_id),
        ("receiver_id", &record.receiver_id),
        ("updated_at", &record.updated_at),
    ] {
        if value.is_empty() {
            return Err(anyhow!("media stream request {name} must be non-empty"));
        }
    }
    if !MEDIA_STREAM_REQUEST_ACTIONS.contains(&record.action.as_str()) {
        return Err(anyhow!(
            "media stream request action {:?} is not one of {:?}",
            record.action,
            MEDIA_STREAM_REQUEST_ACTIONS
        ));
    }
    if !MEDIA_STREAM_REQUEST_STATES.contains(&record.state.as_str()) {
        return Err(anyhow!(
            "media stream request state {:?} is not one of {:?}",
            record.state,
            MEDIA_STREAM_REQUEST_STATES
        ));
    }
    if record.action == "start" {
        if record.receiver_endpoint.parse::<std::net::SocketAddr>().is_err() {
            return Err(anyhow!(
                "media stream request receiver_endpoint {:?} must be host:port",
                record.receiver_endpoint
            ));
        }
        if record.video_source_id.is_empty() && record.audio_source_id.is_empty() {
            return Err(anyhow!("media stream request asks for neither video nor audio"));
        }
        if !record.video_source_id.is_empty() && record.video_codec.is_empty() {
            return Err(anyhow!("media stream request names a video source without a codec"));
        }
        if !record.audio_source_id.is_empty() && record.audio_codec.is_empty() {
            return Err(anyhow!("media stream request names an audio source without a codec"));
        }
    }
    Ok(())
}

#[cfg(test)]
mod media_stream_catalog_tests {
    use super::*;

    fn advertisement() -> GameCultMediaStreamAdvertisementRecord {
        GameCultMediaStreamAdvertisementRecord {
            stream_id: "muninn.raven.av.rudp".into(),
            producer_id: "raven".into(),
            label: "Raven desktop".into(),
            state: "available".into(),
            video_source_ids: vec!["display:0".into()],
            video_source_labels: vec!["Display 1".into()],
            audio_source_ids: vec!["wasapi-loopback:Realtek".into()],
            audio_source_labels: vec!["Realtek loopback".into()],
            video_codecs: vec!["h264".into()],
            audio_codecs: vec!["pcm-f32le-interleaved".into()],
            audio_sample_rate: 48_000,
            audio_channels: 2,
            default_video_bitrate_kbps: 12_000,
            default_latency_budget_ms: 250,
            media_packet_bytes: 848,
            media_connection_id: 0x6d75_0001,
            updated_at: "2026-09-10T00:00:00Z".into(),
        }
    }

    fn request() -> GameCultMediaStreamRequestRecord {
        GameCultMediaStreamRequestRecord {
            request_id: "req-1".into(),
            stream_id: "muninn.raven.av.rudp".into(),
            producer_id: "raven".into(),
            receiver_id: "starfire.obs".into(),
            receiver_endpoint: "192.168.178.146:5204".into(),
            action: "start".into(),
            state: "pending".into(),
            video_source_id: "display:0".into(),
            audio_source_id: "wasapi-loopback:Realtek".into(),
            video_codec: "h264".into(),
            audio_codec: "pcm-f32le-interleaved".into(),
            video_bitrate_kbps: 12_000,
            latency_budget_ms: 250,
            media_packet_bytes: 848,
            detail: String::new(),
            updated_at: "2026-09-10T00:00:00Z".into(),
        }
    }

    #[test]
    fn a_complete_advertisement_and_request_validate() -> anyhow::Result<()> {
        validate_media_stream_advertisement(&advertisement())?;
        validate_media_stream_request(&request())?;
        Ok(())
    }

    #[test]
    fn source_ids_and_labels_must_pair_up() {
        let mut record = advertisement();
        record.video_source_labels.clear();
        let error = validate_media_stream_advertisement(&record).unwrap_err();
        assert!(error.to_string().contains("video source ids"), "{error}");
    }

    #[test]
    fn audio_on_offer_needs_a_sample_rate_and_channels() {
        let mut record = advertisement();
        record.audio_channels = 0;
        assert!(validate_media_stream_advertisement(&record).is_err());
        record.audio_source_ids.clear();
        record.audio_source_labels.clear();
        assert!(validate_media_stream_advertisement(&record).is_ok(), "no audio, no requirement");
    }

    #[test]
    fn a_start_needs_somewhere_to_send_and_something_to_send() {
        let mut record = request();
        record.receiver_endpoint = "starfire".into();
        assert!(validate_media_stream_request(&record).unwrap_err().to_string().contains("host:port"));
        let mut record = request();
        record.video_source_id.clear();
        record.audio_source_id.clear();
        assert!(validate_media_stream_request(&record).unwrap_err().to_string().contains("neither"));
        let mut record = request();
        record.video_codec.clear();
        assert!(validate_media_stream_request(&record).unwrap_err().to_string().contains("codec"));
    }

    #[test]
    fn a_stop_needs_no_endpoint_or_sources() -> anyhow::Result<()> {
        let mut record = request();
        record.action = "stop".into();
        record.receiver_endpoint.clear();
        record.video_source_id.clear();
        record.audio_source_id.clear();
        validate_media_stream_request(&record)
    }

    #[test]
    fn unknown_actions_and_states_are_refused() {
        let mut record = request();
        record.action = "pause".into();
        assert!(validate_media_stream_request(&record).is_err());
        let mut record = request();
        record.state = "done".into();
        assert!(validate_media_stream_request(&record).is_err());
    }

    #[test]
    fn one_request_per_stream_per_receiver() {
        assert_eq!(media_stream_request_key("s", "starfire.obs"), "s:starfire.obs");
    }

    /// The records round-trip through the same positional encoding every
    /// other runtime reads, so a Python or TypeScript consumer sees the same
    /// slots.
    #[test]
    fn records_round_trip_positionally() -> anyhow::Result<()> {
        let record = advertisement();
        let encoded = rmp_serde::to_vec(&record)?;
        let decoded: GameCultMediaStreamAdvertisementRecord = rmp_serde::from_slice(&encoded)?;
        assert_eq!(decoded, record);
        let record = request();
        let encoded = rmp_serde::to_vec(&record)?;
        let decoded: GameCultMediaStreamRequestRecord = rmp_serde::from_slice(&encoded)?;
        assert_eq!(decoded, record);
        Ok(())
    }
}

/// One coded video access unit, possibly split across `chunk_count` records
/// sharing a `frame_id`. A consumer reassembles by `(stream_id, session_id,
/// frame_id)` and orders by `chunk_index`.
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(
    type = "gamecult.media_video_access_unit",
    schema = "gamecult.media_video_access_unit.v1"
)]
pub struct GameCultMediaVideoAccessUnitRecord {
    #[cultcache(key = 0)]
    pub stream_id: String,
    #[cultcache(key = 1)]
    pub session_id: String,
    #[cultcache(key = 2)]
    pub frame_id: u64,
    #[cultcache(key = 3)]
    pub codec: String,
    #[cultcache(key = 4)]
    pub pts_ticks: i64,
    #[cultcache(key = 5)]
    pub duration_ticks: u32,
    #[cultcache(key = 6)]
    pub timebase_num: u32,
    #[cultcache(key = 7)]
    pub timebase_den: u32,
    #[cultcache(key = 8)]
    pub keyframe: bool,
    #[cultcache(key = 9)]
    pub dependency_frame_id: Option<u64>,
    #[cultcache(key = 10)]
    pub deadline_ticks: i64,
    #[cultcache(key = 11)]
    pub chunk_index: u16,
    #[cultcache(key = 12)]
    pub chunk_count: u16,
    #[cultcache(key = 13, bytes)]
    pub payload: Vec<u8>,
}

/// Forward-error-correction parity for one video access unit. Carries the
/// framing fields of the access unit it protects so a receiver can recover
/// without having seen the original, plus the shard geometry needed to run
/// recovery: `parity_index` within `parity_count`, and the chunk sizes the
/// parity was computed over.
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(
    type = "gamecult.media_video_parity_shard",
    schema = "gamecult.media_video_parity_shard.v2"
)]
pub struct GameCultMediaVideoParityShardRecord {
    #[cultcache(key = 0)]
    pub stream_id: String,
    #[cultcache(key = 1)]
    pub session_id: String,
    #[cultcache(key = 2)]
    pub frame_id: u64,
    #[cultcache(key = 3)]
    pub codec: String,
    #[cultcache(key = 4)]
    pub pts_ticks: i64,
    #[cultcache(key = 5)]
    pub duration_ticks: u32,
    #[cultcache(key = 6)]
    pub timebase_num: u32,
    #[cultcache(key = 7)]
    pub timebase_den: u32,
    #[cultcache(key = 8)]
    pub keyframe: bool,
    #[cultcache(key = 9)]
    pub dependency_frame_id: Option<u64>,
    #[cultcache(key = 10)]
    pub deadline_ticks: i64,
    #[cultcache(key = 11)]
    pub chunk_count: u16,
    #[cultcache(key = 12)]
    pub parity_index: u16,
    #[cultcache(key = 13)]
    pub parity_count: u16,
    #[cultcache(key = 14)]
    pub chunk_payload_bytes: u32,
    #[cultcache(key = 15)]
    pub last_chunk_payload_bytes: u32,
    #[cultcache(key = 16, bytes)]
    pub payload: Vec<u8>,
}

/// One coded audio packet. Unlike video these are not chunked: a packet is
/// whole or it is absent, and `payload` carries whatever `codec` names.
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(
    type = "gamecult.media_audio_packet",
    schema = "gamecult.media_audio_packet.v1"
)]
pub struct GameCultMediaAudioPacketRecord {
    #[cultcache(key = 0)]
    pub stream_id: String,
    #[cultcache(key = 1)]
    pub session_id: String,
    #[cultcache(key = 2)]
    pub packet_id: u64,
    #[cultcache(key = 3)]
    pub codec: String,
    #[cultcache(key = 4)]
    pub pts_ticks: i64,
    #[cultcache(key = 5)]
    pub duration_ticks: u32,
    #[cultcache(key = 6)]
    pub timebase_num: u32,
    #[cultcache(key = 7)]
    pub timebase_den: u32,
    #[cultcache(key = 8)]
    pub deadline_ticks: i64,
    #[cultcache(key = 9, bytes)]
    pub payload: Vec<u8>,
}

/// What a receiver observed, published back to the producer so it can adapt:
/// what decoded, what is missing, what arrived too late, and how much queue and
/// jitter the receiver is carrying.
///
/// This is the only record here that flows consumer-to-producer. A producer is
/// free to ignore it; it reports observation, it does not issue commands.
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(
    type = "gamecult.media_receiver_feedback",
    schema = "gamecult.media_receiver_feedback.v1"
)]
pub struct GameCultMediaReceiverFeedbackRecord {
    #[cultcache(key = 0)]
    pub stream_id: String,
    #[cultcache(key = 1)]
    pub session_id: String,
    #[cultcache(key = 2)]
    pub receiver_id: String,
    #[cultcache(key = 3)]
    pub highest_decodable_frame_id: Option<u64>,
    #[cultcache(key = 4)]
    pub missing_frame_ids: Vec<u64>,
    #[cultcache(key = 5)]
    pub late_frame_ids: Vec<u64>,
    #[cultcache(key = 6)]
    pub requested_keyframe: bool,
    #[cultcache(key = 7)]
    pub jitter_us: i64,
    #[cultcache(key = 8)]
    pub decode_queue_us: i64,
    #[cultcache(key = 9)]
    pub observed_at: String,
    #[cultcache(key = 10, default)]
    pub missing_video_chunk_keys: Vec<String>,
}
