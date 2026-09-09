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
