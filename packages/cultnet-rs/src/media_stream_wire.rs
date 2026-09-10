//! How a CultMesh Media Stream record travels: the envelope both ends must
//! agree on.
//!
//! The records in `media_stream_contracts` describe media. This describes how
//! one reaches a peer — wrapped in `cultnet.document_put_raw.v0` with a schema
//! id, a record key, and provenance. It lives here because a producer and a
//! consumer that never link each other still have to agree on it exactly, and
//! the last time they did not, one end was Rust and the other was 6,515 lines
//! of C++ that had drifted.
//!
//! # No producer's name belongs in a shared envelope
//!
//! The Muninn implementation this replaces hardcoded `"muninn-media:"` into
//! every message id and `"muninn.media"` into every tag, so a second producer
//! would have had to label its traffic as Muninn's. The producer namespace is a
//! parameter here: see [`MediaWireProvenance`].
//!
//! # Record keys are addresses, not decoration
//!
//! A key identifies one piece of media within a session, so a consumer can tell
//! a retransmission from a new frame and a chunk from its siblings. The shapes
//! are fixed by this module rather than by each producer, because a consumer
//! parses them.

use anyhow::{Result, anyhow};

use crate::contracts::{
    CultNetMessage, CultNetRawDocumentRecord, CultNetRawPayloadEncoding, CultNetWireContract,
    decode_cultnet_message_from_slice, encode_cultnet_message_to_vec,
};
use crate::media_stream_contracts::{
    GAMECULT_MEDIA_AUDIO_PACKET_SCHEMA, GAMECULT_MEDIA_RECEIVER_FEEDBACK_SCHEMA,
    GAMECULT_MEDIA_VIDEO_ACCESS_UNIT_SCHEMA, GAMECULT_MEDIA_VIDEO_PARITY_SHARD_SCHEMA,
    GameCultMediaAudioPacketRecord, GameCultMediaReceiverFeedbackRecord,
    GameCultMediaVideoAccessUnitRecord, GameCultMediaVideoParityShardRecord,
};

/// The CultNet channel media rides.
pub const GAMECULT_MEDIA_CHANNEL: &str = "media";

/// One media record on its way somewhere.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum GameCultMediaWireRecord {
    Video(GameCultMediaVideoAccessUnitRecord),
    VideoParity(GameCultMediaVideoParityShardRecord),
    Audio(GameCultMediaAudioPacketRecord),
    Feedback(GameCultMediaReceiverFeedbackRecord),
}

/// Who is sending, and when. `producer` namespaces message ids and tags so a
/// stream can be attributed without the envelope knowing any producer by name.
#[derive(Clone, Copy, Debug)]
pub struct MediaWireProvenance<'a> {
    pub stored_at: &'a str,
    pub runtime_id: &'a str,
    pub role: &'a str,
    pub producer: &'a str,
}

impl MediaWireProvenance<'_> {
    fn validate(&self) -> Result<()> {
        for (name, value) in [
            ("stored_at", self.stored_at),
            ("runtime_id", self.runtime_id),
            ("role", self.role),
            ("producer", self.producer),
        ] {
            if value.is_empty() {
                return Err(anyhow!("media wire provenance {name} must be non-empty"));
            }
        }
        Ok(())
    }
}

impl GameCultMediaWireRecord {
    pub fn schema_id(&self) -> &'static str {
        match self {
            Self::Video(_) => GAMECULT_MEDIA_VIDEO_ACCESS_UNIT_SCHEMA,
            Self::VideoParity(_) => GAMECULT_MEDIA_VIDEO_PARITY_SHARD_SCHEMA,
            Self::Audio(_) => GAMECULT_MEDIA_AUDIO_PACKET_SCHEMA,
            Self::Feedback(_) => GAMECULT_MEDIA_RECEIVER_FEEDBACK_SCHEMA,
        }
    }

    /// Addresses this record within its session.
    pub fn record_key(&self) -> String {
        match self {
            Self::Video(record) => format!(
                "{}:{}:video:{}:{}",
                record.stream_id, record.session_id, record.frame_id, record.chunk_index
            ),
            Self::VideoParity(record) => format!(
                "{}:{}:video-parity:{}:{}",
                record.stream_id, record.session_id, record.frame_id, record.parity_index
            ),
            Self::Audio(record) => format!(
                "{}:{}:audio:{}",
                record.stream_id, record.session_id, record.packet_id
            ),
            Self::Feedback(record) => format!(
                "{}:{}:feedback:{}",
                record.stream_id, record.session_id, record.receiver_id
            ),
        }
    }

    /// The record's own MessagePack form.
    ///
    /// Serializing the record directly is only correct because the payload
    /// fields carry `#[cultcache(key = N, bytes)]`. Muninn previously mirrored
    /// each record into a parallel tuple struct — every field restated, in
    /// order, by hand — solely so `#[serde(with = "serde_bytes")]` could be
    /// applied to the payload, because the `DatabaseEntry` derive does not read
    /// serde attributes. Those shadow structs are gone; the derive emits `bin`
    /// itself now. Do not reintroduce one.
    fn payload_bytes(&self) -> Result<Vec<u8>> {
        match self {
            Self::Video(record) => rmp_serde::to_vec(record),
            Self::VideoParity(record) => rmp_serde::to_vec(record),
            Self::Audio(record) => rmp_serde::to_vec(record),
            Self::Feedback(record) => rmp_serde::to_vec(record),
        }
        .map_err(Into::into)
    }
}

/// Wraps a media record for the wire.
pub fn encode_media_wire_record(
    record: &GameCultMediaWireRecord,
    provenance: MediaWireProvenance<'_>,
) -> Result<Vec<u8>> {
    provenance.validate()?;

    let schema_id = record.schema_id();
    let record_key = record.record_key();
    let message = CultNetMessage::DocumentPutRaw {
        message_id: format!(
            "{}-media:{}:{}",
            provenance.producer,
            schema_id,
            record_key.replace(':', "-")
        ),
        document: CultNetRawDocumentRecord {
            schema_id: schema_id.to_string(),
            record_key,
            stored_at: provenance.stored_at.to_string(),
            payload_encoding: CultNetRawPayloadEncoding::Messagepack,
            payload: record.payload_bytes()?,
            source_runtime_id: Some(provenance.runtime_id.to_string()),
            source_agent_id: None,
            source_role: Some(provenance.role.to_string()),
            tags: Some(vec![format!("{}.media", provenance.producer)]),
        },
    };

    encode_cultnet_message_to_vec(&message, CultNetWireContract::CultNetSchemaV0).map_err(Into::into)
}

/// Unwraps a media record from the wire.
pub fn decode_media_wire_record(payload: &[u8]) -> Result<GameCultMediaWireRecord> {
    let message = decode_cultnet_message_from_slice(payload, CultNetWireContract::CultNetSchemaV0)?;
    let CultNetMessage::DocumentPutRaw { document, .. } = message else {
        return Err(anyhow!("expected cultnet.document_put_raw.v0"));
    };
    if document.payload_encoding != CultNetRawPayloadEncoding::Messagepack {
        return Err(anyhow!("media document payload must be MessagePack"));
    }

    let record = match document.schema_id.as_str() {
        GAMECULT_MEDIA_VIDEO_ACCESS_UNIT_SCHEMA => {
            let record: GameCultMediaVideoAccessUnitRecord =
                rmp_serde::from_slice(&document.payload)?;
            validate_video_record(&record)?;
            GameCultMediaWireRecord::Video(record)
        }
        GAMECULT_MEDIA_VIDEO_PARITY_SHARD_SCHEMA => {
            let record: GameCultMediaVideoParityShardRecord =
                rmp_serde::from_slice(&document.payload)?;
            validate_video_parity_record(&record)?;
            GameCultMediaWireRecord::VideoParity(record)
        }
        GAMECULT_MEDIA_AUDIO_PACKET_SCHEMA => {
            let record: GameCultMediaAudioPacketRecord =
                rmp_serde::from_slice(&document.payload)?;
            validate_audio_record(&record)?;
            GameCultMediaWireRecord::Audio(record)
        }
        GAMECULT_MEDIA_RECEIVER_FEEDBACK_SCHEMA => {
            let record: GameCultMediaReceiverFeedbackRecord =
                rmp_serde::from_slice(&document.payload)?;
            validate_feedback_record(&record)?;
            GameCultMediaWireRecord::Feedback(record)
        }
        other => return Err(anyhow!("unsupported media schema {other}")),
    };

    // The key is derived from the record's own contents, so a mismatch means the
    // envelope and its payload disagree about what this is. Admitting it would
    // let a consumer address media by a key its producer never meant.
    let expected = record.record_key();
    if document.record_key != expected {
        return Err(anyhow!(
            "record key mismatch: envelope says {}, record derives {expected}",
            document.record_key
        ));
    }

    Ok(record)
}

// ---------------------------------------------------------------------------
// Validation
//
// Moved here from Muninn with the envelope. A decoder that does not validate
// leaves each consumer to invent its own idea of a well-formed record, which is
// how the C++ receiver and the Rust sender came to disagree while both looked
// correct in isolation. Muninn's tests for these came too; they now cover the
// contract rather than one producer's copy of it.
// ---------------------------------------------------------------------------

/// Addresses one chunk of a video frame. The string form appears in receiver
/// feedback and is parsed by consumers, so its shape is contract.
#[derive(Clone, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct VideoChunkKey {
    pub frame_id: u64,
    pub chunk_index: u16,
}

impl VideoChunkKey {
    pub fn new(frame_id: u64, chunk_index: u16) -> Self {
        Self {
            frame_id,
            chunk_index,
        }
    }

    pub fn parse(value: &str) -> Result<Self> {
        if value.matches(':').count() != 1 {
            return Err(anyhow!(
                "video chunk key must contain exactly one ':' separator"
            ));
        }
        let (frame_id, chunk_index) = value
            .split_once(':')
            .ok_or_else(|| anyhow!("video chunk key must be '<frame_id>:<chunk_index>'"))?;
        Ok(Self {
            frame_id: frame_id
                .parse()
                .map_err(|_| anyhow!("video chunk key frame_id must be an integer"))?,
            chunk_index: chunk_index
                .parse()
                .map_err(|_| anyhow!("video chunk key chunk_index must be an integer"))?,
        })
    }

    pub fn as_feedback_key(&self) -> String {
        format!("{}:{}", self.frame_id, self.chunk_index)
    }
}

pub fn video_chunk_feedback_key(frame_id: u64, chunk_index: u16) -> String {
    VideoChunkKey::new(frame_id, chunk_index).as_feedback_key()
}

pub fn normalize_video_chunk_feedback_keys(keys: Vec<String>) -> Result<Vec<String>> {
    let mut parsed = keys
        .iter()
        .map(|key| VideoChunkKey::parse(key))
        .collect::<Result<Vec<_>>>()?;
    parsed.sort_unstable();
    parsed.dedup();
    Ok(parsed
        .into_iter()
        .map(|key| key.as_feedback_key())
        .collect())
}

pub fn validate_video_record(record: &GameCultMediaVideoAccessUnitRecord) -> Result<()> {
    if record.stream_id.is_empty() {
        return Err(anyhow!("video media record stream_id must be non-empty"));
    }
    if record.session_id.is_empty() {
        return Err(anyhow!("video media record session_id must be non-empty"));
    }
    if record.codec.is_empty() {
        return Err(anyhow!("video media record codec must be non-empty"));
    }
    if record.timebase_num == 0 || record.timebase_den == 0 {
        return Err(anyhow!("video media record timebase must be non-zero"));
    }
    if record.duration_ticks == 0 {
        return Err(anyhow!(
            "video media record duration_ticks must be greater than zero"
        ));
    }
    if record.deadline_ticks < record.pts_ticks {
        return Err(anyhow!(
            "video media record deadline_ticks must not precede pts_ticks"
        ));
    }
    if record.chunk_count == 0 {
        return Err(anyhow!("video media record chunk_count must be non-zero"));
    }
    if record.chunk_index >= record.chunk_count {
        return Err(anyhow!(
            "video media record chunk_index {} is outside chunk_count {}",
            record.chunk_index,
            record.chunk_count
        ));
    }
    if record.payload.is_empty() {
        return Err(anyhow!("video media record payload must be non-empty"));
    }
    Ok(())
}

pub fn validate_video_parity_record(record: &GameCultMediaVideoParityShardRecord) -> Result<()> {
    if record.stream_id.is_empty() {
        return Err(anyhow!(
            "video parity media record stream_id must be non-empty"
        ));
    }
    if record.session_id.is_empty() {
        return Err(anyhow!(
            "video parity media record session_id must be non-empty"
        ));
    }
    if record.codec.is_empty() {
        return Err(anyhow!("video parity media record codec must be non-empty"));
    }
    if record.timebase_num == 0 || record.timebase_den == 0 {
        return Err(anyhow!(
            "video parity media record timebase must be non-zero"
        ));
    }
    if record.duration_ticks == 0 {
        return Err(anyhow!(
            "video parity media record duration_ticks must be greater than zero"
        ));
    }
    if record.deadline_ticks < record.pts_ticks {
        return Err(anyhow!(
            "video parity media record deadline_ticks must not precede pts_ticks"
        ));
    }
    if record.chunk_count == 0 {
        return Err(anyhow!(
            "video parity media record chunk_count must be non-zero"
        ));
    }
    if record.parity_count == 0 || record.parity_index >= record.parity_count {
        return Err(anyhow!(
            "video parity media record has invalid parity stripe metadata"
        ));
    }
    if record.parity_count > record.chunk_count {
        return Err(anyhow!(
            "video parity media record parity_count exceeds chunk_count"
        ));
    }
    if record.chunk_payload_bytes == 0 || record.last_chunk_payload_bytes == 0 {
        return Err(anyhow!(
            "video parity media record chunk lengths must be non-zero"
        ));
    }
    if record.last_chunk_payload_bytes > record.chunk_payload_bytes {
        return Err(anyhow!(
            "video parity media record last chunk length exceeds regular chunk length"
        ));
    }
    if record.payload.is_empty() {
        return Err(anyhow!(
            "video parity media record payload must be non-empty"
        ));
    }
    if record.payload.len() > record.chunk_payload_bytes as usize {
        return Err(anyhow!(
            "video parity media record payload exceeds declared chunk length"
        ));
    }
    Ok(())
}

pub fn validate_audio_record(record: &GameCultMediaAudioPacketRecord) -> Result<()> {
    if record.stream_id.is_empty() {
        return Err(anyhow!("audio media record stream_id must be non-empty"));
    }
    if record.session_id.is_empty() {
        return Err(anyhow!("audio media record session_id must be non-empty"));
    }
    if record.codec.is_empty() {
        return Err(anyhow!("audio media record codec must be non-empty"));
    }
    if record.timebase_num == 0 || record.timebase_den == 0 {
        return Err(anyhow!("audio media record timebase must be non-zero"));
    }
    if record.duration_ticks == 0 {
        return Err(anyhow!(
            "audio media record duration_ticks must be greater than zero"
        ));
    }
    if record.deadline_ticks < record.pts_ticks {
        return Err(anyhow!(
            "audio media record deadline_ticks must not precede pts_ticks"
        ));
    }
    if record.payload.is_empty() {
        return Err(anyhow!("audio media record payload must be non-empty"));
    }
    Ok(())
}

pub fn validate_feedback_record(record: &GameCultMediaReceiverFeedbackRecord) -> Result<()> {
    if record.stream_id.is_empty() {
        return Err(anyhow!(
            "receiver feedback media record stream_id must be non-empty"
        ));
    }
    if record.session_id.is_empty() {
        return Err(anyhow!(
            "receiver feedback media record session_id must be non-empty"
        ));
    }
    if record.receiver_id.is_empty() {
        return Err(anyhow!(
            "receiver feedback media record receiver_id must be non-empty"
        ));
    }
    if record.observed_at.is_empty() {
        return Err(anyhow!(
            "receiver feedback media record observed_at must be non-empty"
        ));
    }
    if record.jitter_us < 0 {
        return Err(anyhow!(
            "receiver feedback media record jitter_us must be non-negative"
        ));
    }
    if record.decode_queue_us < 0 {
        return Err(anyhow!(
            "receiver feedback media record decode_queue_us must be non-negative"
        ));
    }
    normalize_video_chunk_feedback_keys(record.missing_video_chunk_keys.clone())?;
    Ok(())
}

/// What a receiver has to say about a stream. The record it builds is the
/// contract's; this normalises it so both ends agree on its shape: damage
/// lists sorted and unique, chunk keys well-formed, and a keyframe requested
/// whenever a whole frame is missing, since a producer's repair cache holds
/// chunks, not references. Missing chunks alone are a repair request, which
/// exists precisely so that a keyframe is not needed.
#[derive(Clone, Debug)]
pub struct ReceiverFeedbackOptions<'a> {
    pub stream_id: &'a str,
    pub session_id: &'a str,
    pub receiver_id: &'a str,
    pub highest_decodable_frame_id: Option<u64>,
    pub missing_frame_ids: Vec<u64>,
    pub missing_video_chunk_keys: Vec<String>,
    pub late_frame_ids: Vec<u64>,
    pub requested_keyframe: bool,
    pub jitter_us: i64,
    pub decode_queue_us: i64,
    pub observed_at: &'a str,
}

pub fn build_receiver_feedback(
    options: ReceiverFeedbackOptions<'_>,
) -> Result<GameCultMediaReceiverFeedbackRecord> {
    let mut missing_frame_ids = options.missing_frame_ids;
    missing_frame_ids.sort_unstable();
    missing_frame_ids.dedup();
    let missing_video_chunk_keys =
        normalize_video_chunk_feedback_keys(options.missing_video_chunk_keys)?;
    let mut late_frame_ids = options.late_frame_ids;
    late_frame_ids.sort_unstable();
    late_frame_ids.dedup();
    let requested_keyframe = options.requested_keyframe || !missing_frame_ids.is_empty();

    let record = GameCultMediaReceiverFeedbackRecord {
        stream_id: options.stream_id.to_string(),
        session_id: options.session_id.to_string(),
        receiver_id: options.receiver_id.to_string(),
        highest_decodable_frame_id: options.highest_decodable_frame_id,
        missing_frame_ids,
        late_frame_ids,
        requested_keyframe,
        jitter_us: options.jitter_us,
        decode_queue_us: options.decode_queue_us,
        observed_at: options.observed_at.to_string(),
        missing_video_chunk_keys,
    };
    validate_feedback_record(&record)?;
    Ok(record)
}

#[cfg(test)]
mod receiver_feedback_tests {
    use super::*;

    fn options() -> ReceiverFeedbackOptions<'static> {
        ReceiverFeedbackOptions {
            stream_id: "muninn.raven.av.rudp",
            session_id: "session-1",
            receiver_id: "starfire.obs",
            highest_decodable_frame_id: Some(40),
            missing_frame_ids: Vec::new(),
            missing_video_chunk_keys: Vec::new(),
            late_frame_ids: Vec::new(),
            requested_keyframe: false,
            jitter_us: 0,
            decode_queue_us: 2_000,
            observed_at: "2026-06-18T00:00:00Z",
        }
    }

    #[test]
    fn builds_receiver_feedback_with_sorted_unique_damage_lists() -> Result<()> {
        let feedback = build_receiver_feedback(ReceiverFeedbackOptions {
            missing_frame_ids: vec![43, 42, 43],
            missing_video_chunk_keys: vec![
                video_chunk_feedback_key(43, 2),
                video_chunk_feedback_key(42, 1),
                video_chunk_feedback_key(42, 1),
            ],
            late_frame_ids: vec![39, 39, 38],
            jitter_us: 700,
            ..options()
        })?;

        assert_eq!(feedback.missing_frame_ids, vec![42, 43]);
        assert_eq!(feedback.missing_video_chunk_keys, vec!["42:1", "43:2"]);
        assert_eq!(feedback.late_frame_ids, vec![38, 39]);
        assert!(feedback.requested_keyframe, "a whole missing frame is a lost reference");
        Ok(())
    }

    /// A producer answers a keyframe request with an IDR. A repair request is
    /// the cheaper alternative and must not carry one by accident.
    #[test]
    fn a_repair_request_does_not_ask_for_a_keyframe() -> Result<()> {
        let feedback = build_receiver_feedback(ReceiverFeedbackOptions {
            missing_video_chunk_keys: vec![video_chunk_feedback_key(42, 1)],
            ..options()
        })?;
        assert!(!feedback.requested_keyframe);
        Ok(())
    }

    #[test]
    fn rejects_negative_receiver_feedback_timing() {
        let error = build_receiver_feedback(ReceiverFeedbackOptions { jitter_us: -1, ..options() })
            .unwrap_err();
        assert!(error.to_string().contains("jitter_us"));
    }

    #[test]
    fn rejects_malformed_receiver_feedback_chunk_keys() {
        let error = build_receiver_feedback(ReceiverFeedbackOptions {
            missing_video_chunk_keys: vec!["frame:chunk".to_string()],
            ..options()
        })
        .unwrap_err();
        assert!(error.to_string().contains("frame_id"), "{error}");
    }

    #[test]
    fn rejects_an_empty_receiver_id() {
        let error = build_receiver_feedback(ReceiverFeedbackOptions { receiver_id: "", ..options() })
            .unwrap_err();
        assert!(error.to_string().contains("receiver_id"));
    }
}
