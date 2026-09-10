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

    match document.schema_id.as_str() {
        GAMECULT_MEDIA_VIDEO_ACCESS_UNIT_SCHEMA => Ok(GameCultMediaWireRecord::Video(
            rmp_serde::from_slice(&document.payload)?,
        )),
        GAMECULT_MEDIA_VIDEO_PARITY_SHARD_SCHEMA => Ok(GameCultMediaWireRecord::VideoParity(
            rmp_serde::from_slice(&document.payload)?,
        )),
        GAMECULT_MEDIA_AUDIO_PACKET_SCHEMA => Ok(GameCultMediaWireRecord::Audio(
            rmp_serde::from_slice(&document.payload)?,
        )),
        GAMECULT_MEDIA_RECEIVER_FEEDBACK_SCHEMA => Ok(GameCultMediaWireRecord::Feedback(
            rmp_serde::from_slice(&document.payload)?,
        )),
        other => Err(anyhow!("unknown media schema {other}")),
    }
}
