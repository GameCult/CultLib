//! Forward error correction for CultMesh Media Stream records.
//!
//! Pure functions over the records in [`crate::media_stream_contracts`]: no
//! sockets, no clock, no randomness. A producer calls [`protect_video_frame`]
//! and [`protect_audio_block`]; a consumer calls [`recover_video_block`] and
//! [`recover_audio_block`]. Both ends link this one implementation, so a
//! producer and a consumer cannot disagree about the math without one of them
//! failing to build against the same version of this crate.
//!
//! # The code
//!
//! Every block is a systematic Reed-Solomon code over GF(2^8): `k` data shards
//! are sent unchanged and `m` parity shards are computed from them. Any `k` of
//! the `k + m` shards recover the block (the code is MDS), so any `m` erasures
//! are repaired and any more are reported as [`MediaFecError::BeyondRepair`],
//! never as wrong bytes. The construction is named on every parity record by
//! [`MEDIA_FEC_SCHEME_RS_GF256_V1`] and backed by the `reed-solomon-erasure`
//! crate. The library is not the contract: the construction below is, and
//! `tests/media_fec.rs` pins the parity bytes of known inputs, so a library that
//! changes the matrix fails there instead of on the wire, and a decoder in
//! another runtime has vectors to meet.
//!
//! # The `rs-gf256-v1` construction
//!
//! Enough to implement it in another runtime without this crate.
//!
//! * The field is GF(2^8) over the polynomial `x^8 + x^4 + x^3 + x^2 + 1`
//!   (`0x11D`). Addition is XOR; multiplication is carry-less multiplication
//!   reduced by `0x11D`.
//! * For a block of `k` data shards and `m` parity shards, let `n = k + m`
//!   (`n <= 256`) and build the `n x k` Vandermonde matrix `V` whose entry in
//!   row `r`, column `c` is `r^c`, where the row number `r` (0 through
//!   `n - 1`) is itself read as a field element and `0^0 = 1`. The evaluation
//!   points are the integers 0 through `n - 1`, not powers of a generator.
//! * The encoding matrix is `E = V * inverse(top)`, where `top` is the first
//!   `k` rows of `V`. Its first `k` rows are the identity, so data shards are
//!   sent unchanged; its last `m` rows generate parity.
//! * Every shard of a block has the same length, `shard_payload_bytes`. A
//!   shorter final video chunk is zero-padded to that length before encoding.
//!   Byte `j` of parity shard `p` is the XOR over data shards `i` of
//!   `E[k + p][i] * data[i][j]`.
//! * Any `k` surviving shards recover the block: invert the `k x k` submatrix
//!   of `E` for the surviving rows and multiply.
//!
//! The known-answer vectors in `tests/media_fec.rs` were checked against an
//! independent implementation of exactly this text.
//!
//! # What a block is
//!
//! * Video: a frame's chunks split into `ceil(n / 16)` contiguous, near-equal
//!   blocks, each protected by `max(2, ceil(k / 4))` parity shards. A block
//!   never spans two frames, so one frame's deadline governs its recovery.
//!   [`protect_video_frame`] returns the frame's records in send order: blocks
//!   are interleaved, so a burst of consecutive losses lands as one loss per
//!   block rather than many in one.
//! * Audio: `4` consecutive packets of one payload length, protected by `2`
//!   parity shards.
//!
//! # One datagram
//!
//! Every record the protect functions return, data or parity, fits one
//! datagram once wrapped for the wire, so parity never reintroduces the
//! fragment amplification it exists to remove. The protect functions enforce
//! that against [`MediaFecPolicy::max_wire_bytes`]: they encode each record
//! with the caller's provenance and refuse the block if any is larger.
//! The default budget is [`MEDIA_FEC_MAX_WIRE_BYTES`], an IPv4 path with a
//! 1500-byte MTU. Pass a smaller budget when the path carries less: IPv6 over
//! 1500 leaves a UDP payload of 1452 bytes, and a WireGuard or Tailscale
//! tunnel about 1252, so subtract the 41 bytes of RUDP header and channel name
//! from the path's UDP payload limit.
//!
//! The budget is measured with the provenance given, so a producer whose
//! `stored_at` or identifiers grow later must pass the widest it will use.
//!
//! # Audio playout
//!
//! A recovered audio packet has the block's latest `deadline_ticks`, so a
//! packet recovered early in the block reports a deadline later than its own.
//! Audio consumers must gate playout on `pts_ticks`, which recovery derives
//! exactly, and use `deadline_ticks` only to give up on the block as a whole.
//!
//! # What this module does not own
//!
//! When to give up on a block is the receiver's deadline, and the `(k, m)`
//! choice is the producer's policy ([`MediaFecPolicy`]); this module only makes
//! and consumes shards.

use std::collections::BTreeMap;
use std::fmt;

use reed_solomon_erasure::galois_8::ReedSolomon;

use crate::media_stream_contracts::{
    GameCultMediaAudioPacketRecord, GameCultMediaAudioParityShardRecord,
    GameCultMediaVideoAccessUnitRecord, GameCultMediaVideoParityShardRecord,
};
use crate::media_stream_wire::{
    GAMECULT_MEDIA_CHANNEL, GameCultMediaWireRecord, MediaWireProvenance,
    encode_media_wire_record, validate_audio_parity_record, validate_audio_record,
    validate_video_parity_record, validate_video_record,
};
use crate::rudp::RUDP_FIXED_HEADER_BYTES;

/// Names the construction on every parity record: systematic Reed-Solomon over
/// GF(2^8), as built by `reed-solomon-erasure`'s `galois_8` field. A different
/// matrix is a different scheme id.
pub const MEDIA_FEC_SCHEME_RS_GF256_V1: &str = "rs-gf256-v1";

/// The largest UDP payload an IPv4 datagram carries on a 1500-byte MTU.
pub const MEDIA_IPV4_UDP_PAYLOAD_BYTES: usize = 1_472;

/// The default datagram budget: the most a wrapped media record may occupy so
/// that the RUDP header and the channel name still leave it in one IPv4
/// datagram on a 1500-byte MTU (1,431 bytes).
pub const MEDIA_FEC_MAX_WIRE_BYTES: usize =
    MEDIA_IPV4_UDP_PAYLOAD_BYTES - RUDP_FIXED_HEADER_BYTES - GAMECULT_MEDIA_CHANNEL.len();

/// A producer's choice of `(k, m)` and datagram budget.
/// [`MediaFecPolicy::STANDARD`] is the ruled default; each record carries the
/// `(k, m)` it was made with, so a decoder needs no policy.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct MediaFecPolicy {
    /// The most data chunks in one video block.
    pub video_max_block_data_shards: u16,
    /// The fewest parity shards a video block carries.
    pub video_min_parity_shards: u16,
    /// A video block of `k` data shards carries at least `ceil(k / this)` parity
    /// shards.
    pub video_parity_divisor: u16,
    /// Packets per audio block.
    pub audio_data_shards: u16,
    /// Parity shards per audio block.
    pub audio_parity_shards: u16,
    /// The most a wrapped record may occupy. The protect functions refuse a
    /// block with any record over it. See the module docs for smaller paths.
    pub max_wire_bytes: usize,
}

impl MediaFecPolicy {
    /// Video `k <= 16` with `m = max(2, ceil(k / 4))`; audio 4+2; the IPv4
    /// datagram budget.
    pub const STANDARD: Self = Self {
        video_max_block_data_shards: 16,
        video_min_parity_shards: 2,
        video_parity_divisor: 4,
        audio_data_shards: 4,
        audio_parity_shards: 2,
        max_wire_bytes: MEDIA_FEC_MAX_WIRE_BYTES,
    };

    /// `m` for a video block of `data_shards` chunks.
    pub fn video_parity_shards(&self, data_shards: u16) -> u16 {
        data_shards
            .div_ceil(self.video_parity_divisor.max(1))
            .max(self.video_min_parity_shards)
    }

    /// How a frame of `chunk_count` chunks splits: `ceil(n / max)` blocks whose
    /// sizes differ by at most one, larger blocks first.
    pub fn video_block_sizes(&self, chunk_count: u16) -> Vec<u16> {
        let max = self.video_max_block_data_shards.max(1);
        let blocks = chunk_count.div_ceil(max);
        if blocks == 0 {
            return Vec::new();
        }
        let base = chunk_count / blocks;
        let extra = chunk_count % blocks;
        (0..blocks).map(|index| base + u16::from(index < extra)).collect()
    }

    fn validate(&self) -> Result<(), MediaFecError> {
        let widest = self.video_max_block_data_shards;
        if widest == 0
            || self.video_min_parity_shards == 0
            || self.video_parity_divisor == 0
            || self.audio_data_shards == 0
            || self.audio_parity_shards == 0
        {
            return Err(MediaFecError::invalid("media FEC policy fields must be non-zero"));
        }
        if usize::from(widest) + usize::from(self.video_parity_shards(widest)) > 256
            || usize::from(self.audio_data_shards) + usize::from(self.audio_parity_shards) > 256
        {
            return Err(MediaFecError::invalid("media FEC policy exceeds 256 shards per block"));
        }
        Ok(())
    }
}

impl Default for MediaFecPolicy {
    fn default() -> Self {
        Self::STANDARD
    }
}

/// Why a block could not be protected or recovered.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum MediaFecError {
    /// Fewer than `k` of the block's shards are present. Nothing is returned:
    /// a block that cannot be repaired is reported, never guessed at.
    BeyondRepair { shards_present: usize, shards_needed: usize },
    /// The inputs are not one well-formed block.
    Invalid(String),
}

impl MediaFecError {
    fn invalid(message: impl Into<String>) -> Self {
        Self::Invalid(message.into())
    }
}

impl fmt::Display for MediaFecError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::BeyondRepair { shards_present, shards_needed } => write!(
                f,
                "media FEC block is beyond repair: {shards_present} shards present, {shards_needed} needed"
            ),
            Self::Invalid(message) => write!(f, "invalid media FEC block: {message}"),
        }
    }
}

impl std::error::Error for MediaFecError {}

// ---------------------------------------------------------------------------
// The code: equal-length shards in, equal-length shards out.
// ---------------------------------------------------------------------------

fn parity_shards(data: &[Vec<u8>], parity_count: usize) -> Result<Vec<Vec<u8>>, MediaFecError> {
    let code = ReedSolomon::new(data.len(), parity_count)
        .map_err(|error| MediaFecError::invalid(format!("{error:?}")))?;
    let mut parity = vec![vec![0_u8; data[0].len()]; parity_count];
    code.encode_sep(data, &mut parity)
        .map_err(|error| MediaFecError::invalid(format!("{error:?}")))?;
    Ok(parity)
}

/// `shards` holds the block's `k` data slots then `m` parity slots, `None` for
/// each missing. Returns the `k` data shards.
fn recover_data_shards(
    data_shards: usize,
    mut shards: Vec<Option<Vec<u8>>>,
) -> Result<Vec<Vec<u8>>, MediaFecError> {
    let parity_shards = shards.len() - data_shards;
    let present = shards.iter().flatten().count();
    if present < data_shards {
        return Err(MediaFecError::BeyondRepair {
            shards_present: present,
            shards_needed: data_shards,
        });
    }
    let code = ReedSolomon::new(data_shards, parity_shards)
        .map_err(|error| MediaFecError::invalid(format!("{error:?}")))?;
    code.reconstruct_data(&mut shards)
        .map_err(|error| MediaFecError::invalid(format!("{error:?}")))?;
    Ok(shards
        .into_iter()
        .take(data_shards)
        .map(|shard| shard.expect("reconstruct_data fills every data shard"))
        .collect())
}

fn padded(payload: &[u8], shard_bytes: usize) -> Vec<u8> {
    let mut shard = vec![0_u8; shard_bytes];
    shard[..payload.len()].copy_from_slice(payload);
    shard
}

fn from_anyhow(error: anyhow::Error) -> MediaFecError {
    MediaFecError::Invalid(error.to_string())
}

/// Refuses a record that would not fit one datagram once wrapped.
fn fits_datagram(
    record: &GameCultMediaWireRecord,
    provenance: MediaWireProvenance<'_>,
    policy: &MediaFecPolicy,
) -> Result<(), MediaFecError> {
    let wire = encode_media_wire_record(record, provenance).map_err(from_anyhow)?;
    if wire.len() > policy.max_wire_bytes {
        return Err(MediaFecError::invalid(format!(
            "a {} record wraps to {} bytes, over the {}-byte datagram budget: cut chunks smaller or raise max_wire_bytes",
            record.schema_id(),
            wire.len(),
            policy.max_wire_bytes
        )));
    }
    Ok(())
}

/// Where slot `slot` of an audio block lands: its packet id and presentation
/// time, or `None` if either overflows. The one place block arithmetic lives;
/// the protect path, the wire validator and recovery all ask it.
pub(crate) fn audio_slot(
    base_packet_id: u64,
    base_pts_ticks: i64,
    packet_duration_ticks: u32,
    slot: u64,
) -> Option<(u64, i64)> {
    let packet_id = base_packet_id.checked_add(slot)?;
    let pts_ticks = i64::from(packet_duration_ticks)
        .checked_mul(i64::try_from(slot).ok()?)?
        .checked_add(base_pts_ticks)?;
    Some((packet_id, pts_ticks))
}

// ---------------------------------------------------------------------------
// Video
// ---------------------------------------------------------------------------

/// Protects one video frame. `chunks` are all of the frame's access-unit
/// records, in any order; the result is every data and parity record in send
/// order.
///
/// Within a block, data and parity alternate; across blocks, the sequence
/// rotates one record from each block at a time. Where every block is the same
/// size, no two consecutive `block_count` records share a block. Blocks that
/// differ in size run out at the end, and the rotation then cycles over the
/// blocks remaining.
///
/// Errors if `chunks` are not exactly one whole frame (mixed frames, a missing
/// or repeated chunk index, disagreeing framing), if a chunk is not shard
/// shaped (every chunk of a block but its last must be the same length, the
/// last no longer), or if any resulting record, wrapped with `provenance`,
/// exceeds `policy.max_wire_bytes`.
pub fn protect_video_frame(
    chunks: &[GameCultMediaVideoAccessUnitRecord],
    policy: &MediaFecPolicy,
    provenance: MediaWireProvenance<'_>,
) -> Result<Vec<GameCultMediaWireRecord>, MediaFecError> {
    policy.validate()?;
    let first = chunks
        .first()
        .ok_or_else(|| MediaFecError::invalid("a video frame needs at least one chunk"))?;
    let chunk_count = usize::from(first.chunk_count);
    if chunks.len() != chunk_count {
        return Err(MediaFecError::invalid(format!(
            "frame {} declares {} chunks but {} were given",
            first.frame_id,
            chunk_count,
            chunks.len()
        )));
    }
    let mut by_index: BTreeMap<u16, &GameCultMediaVideoAccessUnitRecord> = BTreeMap::new();
    for chunk in chunks {
        validate_video_record(chunk).map_err(from_anyhow)?;
        if !same_frame(first, chunk) {
            return Err(MediaFecError::invalid(
                "a block never spans frames: chunks disagree on stream, session, frame or framing",
            ));
        }
        if by_index.insert(chunk.chunk_index, chunk).is_some() {
            return Err(MediaFecError::invalid(format!(
                "chunk {} appears twice",
                chunk.chunk_index
            )));
        }
    }

    let block_sizes = policy.video_block_sizes(first.chunk_count);
    let block_count = block_sizes.len() as u16;
    let mut lanes: Vec<Vec<GameCultMediaWireRecord>> = Vec::with_capacity(block_sizes.len());
    let mut start = 0_u16;
    for (block_index, &size) in block_sizes.iter().enumerate() {
        let members: Vec<&GameCultMediaVideoAccessUnitRecord> = (start..start + size)
            .map(|index| by_index[&index])
            .collect();
        let shard_bytes = members[0].payload.len();
        let (last, body) = members.split_last().expect("block has a member");
        if body.iter().any(|chunk| chunk.payload.len() != shard_bytes)
            || last.payload.len() > shard_bytes
        {
            return Err(MediaFecError::invalid(
                "every chunk of a block but its last must share one length, and the last no longer",
            ));
        }
        let data: Vec<Vec<u8>> = members
            .iter()
            .map(|chunk| padded(&chunk.payload, shard_bytes))
            .collect();
        let parity_count = policy.video_parity_shards(size);
        let parity = parity_shards(&data, usize::from(parity_count))?;

        let parity_records: Vec<GameCultMediaWireRecord> = parity
            .into_iter()
            .enumerate()
            .map(|(parity_index, payload)| {
                GameCultMediaWireRecord::VideoParity(GameCultMediaVideoParityShardRecord {
                    stream_id: first.stream_id.clone(),
                    session_id: first.session_id.clone(),
                    frame_id: first.frame_id,
                    codec: first.codec.clone(),
                    pts_ticks: first.pts_ticks,
                    duration_ticks: first.duration_ticks,
                    timebase_num: first.timebase_num,
                    timebase_den: first.timebase_den,
                    keyframe: first.keyframe,
                    dependency_frame_id: first.dependency_frame_id,
                    deadline_ticks: first.deadline_ticks,
                    chunk_count: first.chunk_count,
                    fec_scheme: MEDIA_FEC_SCHEME_RS_GF256_V1.to_string(),
                    block_index: block_index as u16,
                    block_count,
                    block_data_start: start,
                    block_data_count: size,
                    parity_index: parity_index as u16,
                    parity_count,
                    shard_payload_bytes: shard_bytes as u32,
                    last_chunk_payload_bytes: last.payload.len() as u32,
                    payload,
                })
            })
            .collect();
        let data_records = members
            .iter()
            .map(|chunk| GameCultMediaWireRecord::Video((*chunk).clone()));
        lanes.push(interleave(data_records.collect(), parity_records));
        start += size;
    }
    let records = rotate(lanes);
    for record in &records {
        fits_datagram(record, provenance, policy)?;
    }
    Ok(records)
}

fn same_frame(a: &GameCultMediaVideoAccessUnitRecord, b: &GameCultMediaVideoAccessUnitRecord) -> bool {
    a.stream_id == b.stream_id
        && a.session_id == b.session_id
        && a.frame_id == b.frame_id
        && a.codec == b.codec
        && a.pts_ticks == b.pts_ticks
        && a.duration_ticks == b.duration_ticks
        && a.timebase_num == b.timebase_num
        && a.timebase_den == b.timebase_den
        && a.keyframe == b.keyframe
        && a.dependency_frame_id == b.dependency_frame_id
        && a.deadline_ticks == b.deadline_ticks
        && a.chunk_count == b.chunk_count
}

/// Data and parity alternate: `D0 P0 D1 P1 ...`, the longer list finishing the
/// lane.
fn interleave<T>(data: Vec<T>, parity: Vec<T>) -> Vec<T> {
    let mut data = data.into_iter();
    let mut parity = parity.into_iter();
    let mut lane = Vec::new();
    loop {
        let next_data = data.next();
        let next_parity = parity.next();
        if next_data.is_none() && next_parity.is_none() {
            return lane;
        }
        lane.extend(next_data);
        lane.extend(next_parity);
    }
}

/// One record from each lane in turn, until every lane is empty.
fn rotate<T>(lanes: Vec<Vec<T>>) -> Vec<T> {
    let mut lanes: Vec<_> = lanes.into_iter().map(Vec::into_iter).collect();
    let mut out = Vec::new();
    loop {
        let before = out.len();
        for lane in &mut lanes {
            out.extend(lane.next());
        }
        if out.len() == before {
            return out;
        }
    }
}

/// Rebuilds the data chunks a block lost.
///
/// `parity` are the block's parity shards that arrived (at least one, for its
/// geometry) and `data` its data chunks that arrived; each may be in any order
/// and may repeat. Returns the missing chunks, ordered by `chunk_index`: empty
/// if none were missing. Fails with [`MediaFecError::BeyondRepair`] if fewer
/// than `block_data_count` shards of the block are present, and with
/// [`MediaFecError::Invalid`] if the records are not from one block.
pub fn recover_video_block(
    parity: &[GameCultMediaVideoParityShardRecord],
    data: &[GameCultMediaVideoAccessUnitRecord],
) -> Result<Vec<GameCultMediaVideoAccessUnitRecord>, MediaFecError> {
    let geometry = parity
        .first()
        .ok_or_else(|| MediaFecError::invalid("recovery needs at least one parity shard"))?;
    let k = usize::from(geometry.block_data_count);
    let m = usize::from(geometry.parity_count);
    let start = usize::from(geometry.block_data_start);
    let shard_bytes = geometry.shard_payload_bytes as usize;

    let mut shards: Vec<Option<Vec<u8>>> = vec![None; k + m];
    for shard in parity {
        validate_video_parity_record(shard).map_err(from_anyhow)?;
        if !same_block(geometry, shard) {
            return Err(MediaFecError::invalid("parity shards are not from one block"));
        }
        shards[k + usize::from(shard.parity_index)].get_or_insert_with(|| shard.payload.clone());
    }
    for chunk in data {
        validate_video_record(chunk).map_err(from_anyhow)?;
        let index = usize::from(chunk.chunk_index);
        let framing_agrees = chunk.stream_id == geometry.stream_id
            && chunk.session_id == geometry.session_id
            && chunk.frame_id == geometry.frame_id
            && chunk.chunk_count == geometry.chunk_count;
        if !framing_agrees || index < start || index >= start + k {
            return Err(MediaFecError::invalid("data chunk does not belong to the parity's block"));
        }
        let expected = if index == start + k - 1 {
            geometry.last_chunk_payload_bytes as usize
        } else {
            shard_bytes
        };
        if chunk.payload.len() != expected {
            return Err(MediaFecError::invalid(format!(
                "chunk {index} is {} bytes but the block declares {expected}",
                chunk.payload.len()
            )));
        }
        shards[index - start].get_or_insert_with(|| padded(&chunk.payload, shard_bytes));
    }

    let missing: Vec<usize> = (0..k).filter(|&slot| shards[slot].is_none()).collect();
    let recovered = recover_data_shards(k, shards)?;
    Ok(missing
        .into_iter()
        .map(|slot| {
            let mut payload = recovered[slot].clone();
            if slot == k - 1 {
                payload.truncate(geometry.last_chunk_payload_bytes as usize);
            }
            GameCultMediaVideoAccessUnitRecord {
                stream_id: geometry.stream_id.clone(),
                session_id: geometry.session_id.clone(),
                frame_id: geometry.frame_id,
                codec: geometry.codec.clone(),
                pts_ticks: geometry.pts_ticks,
                duration_ticks: geometry.duration_ticks,
                timebase_num: geometry.timebase_num,
                timebase_den: geometry.timebase_den,
                keyframe: geometry.keyframe,
                dependency_frame_id: geometry.dependency_frame_id,
                deadline_ticks: geometry.deadline_ticks,
                chunk_index: (start + slot) as u16,
                chunk_count: geometry.chunk_count,
                payload,
            }
        })
        .collect())
}

fn same_block(a: &GameCultMediaVideoParityShardRecord, b: &GameCultMediaVideoParityShardRecord) -> bool {
    a.stream_id == b.stream_id
        && a.session_id == b.session_id
        && a.frame_id == b.frame_id
        && a.codec == b.codec
        && a.pts_ticks == b.pts_ticks
        && a.duration_ticks == b.duration_ticks
        && a.timebase_num == b.timebase_num
        && a.timebase_den == b.timebase_den
        && a.keyframe == b.keyframe
        && a.dependency_frame_id == b.dependency_frame_id
        && a.deadline_ticks == b.deadline_ticks
        && a.chunk_count == b.chunk_count
        && a.fec_scheme == b.fec_scheme
        && a.block_index == b.block_index
        && a.block_count == b.block_count
        && a.block_data_start == b.block_data_start
        && a.block_data_count == b.block_data_count
        && a.parity_count == b.parity_count
        && a.shard_payload_bytes == b.shard_payload_bytes
        && a.last_chunk_payload_bytes == b.last_chunk_payload_bytes
}

// ---------------------------------------------------------------------------
// Audio
// ---------------------------------------------------------------------------

/// Protects one block of audio packets: exactly `policy.audio_data_shards`
/// packets with contiguous ids and presentation times, one payload length, and
/// one codec and timebase. Returns the parity records in `parity_index` order.
/// Errors if a parity record, wrapped with `provenance`, exceeds
/// `policy.max_wire_bytes`; the packets are smaller than their parity.
pub fn protect_audio_block(
    packets: &[GameCultMediaAudioPacketRecord],
    policy: &MediaFecPolicy,
    provenance: MediaWireProvenance<'_>,
) -> Result<Vec<GameCultMediaAudioParityShardRecord>, MediaFecError> {
    policy.validate()?;
    if packets.len() != usize::from(policy.audio_data_shards) {
        return Err(MediaFecError::invalid(format!(
            "an audio block is {} packets, got {}",
            policy.audio_data_shards,
            packets.len()
        )));
    }
    let first = &packets[0];
    let shard_bytes = first.payload.len();
    for (index, packet) in packets.iter().enumerate() {
        validate_audio_record(packet).map_err(from_anyhow)?;
        let expected =
            audio_slot(first.packet_id, first.pts_ticks, first.duration_ticks, index as u64);
        if packet.stream_id != first.stream_id
            || packet.session_id != first.session_id
            || packet.codec != first.codec
            || packet.timebase_num != first.timebase_num
            || packet.timebase_den != first.timebase_den
            || packet.duration_ticks != first.duration_ticks
            || Some((packet.packet_id, packet.pts_ticks)) != expected
            || packet.payload.len() != shard_bytes
        {
            return Err(MediaFecError::invalid(
                "audio block packets must be contiguous and share codec, timebase, duration and payload length",
            ));
        }
    }
    let data: Vec<Vec<u8>> = packets.iter().map(|packet| packet.payload.clone()).collect();
    let deadline_ticks = packets.iter().map(|packet| packet.deadline_ticks).max().unwrap_or(0);
    let parity: Vec<GameCultMediaAudioParityShardRecord> =
        parity_shards(&data, usize::from(policy.audio_parity_shards))?
        .into_iter()
        .enumerate()
        .map(|(parity_index, payload)| GameCultMediaAudioParityShardRecord {
            stream_id: first.stream_id.clone(),
            session_id: first.session_id.clone(),
            codec: first.codec.clone(),
            fec_scheme: MEDIA_FEC_SCHEME_RS_GF256_V1.to_string(),
            base_packet_id: first.packet_id,
            base_pts_ticks: first.pts_ticks,
            packet_duration_ticks: first.duration_ticks,
            timebase_num: first.timebase_num,
            timebase_den: first.timebase_den,
            deadline_ticks,
            data_shard_count: policy.audio_data_shards,
            parity_index: parity_index as u16,
            parity_shard_count: policy.audio_parity_shards,
            shard_payload_bytes: shard_bytes as u32,
            payload,
        })
        .collect();
    // A parity record repeats every field of a packet and adds the scheme and
    // the geometry, so it is the larger of the block's records: if it fits, the
    // packets do.
    for shard in &parity {
        fits_datagram(&GameCultMediaWireRecord::AudioParity(shard.clone()), provenance, policy)?;
    }
    Ok(parity)
}

/// Rebuilds the packets an audio block lost, ordered by `packet_id`. A
/// recovered packet takes its id and presentation time from the block's
/// arithmetic and its deadline from the block's, the latest in the block:
/// gate playout on `pts_ticks`, not `deadline_ticks`. Errors as [`recover_video_block`] does.
pub fn recover_audio_block(
    parity: &[GameCultMediaAudioParityShardRecord],
    data: &[GameCultMediaAudioPacketRecord],
) -> Result<Vec<GameCultMediaAudioPacketRecord>, MediaFecError> {
    let geometry = parity
        .first()
        .ok_or_else(|| MediaFecError::invalid("recovery needs at least one parity shard"))?;
    let k = usize::from(geometry.data_shard_count);
    let m = usize::from(geometry.parity_shard_count);
    let shard_bytes = geometry.shard_payload_bytes as usize;

    let mut shards: Vec<Option<Vec<u8>>> = vec![None; k + m];
    for shard in parity {
        validate_audio_parity_record(shard).map_err(from_anyhow)?;
        if !same_audio_block(geometry, shard) {
            return Err(MediaFecError::invalid("parity shards are not from one block"));
        }
        shards[k + usize::from(shard.parity_index)].get_or_insert_with(|| shard.payload.clone());
    }
    for packet in data {
        validate_audio_record(packet).map_err(from_anyhow)?;
        let slot = packet
            .packet_id
            .checked_sub(geometry.base_packet_id)
            .filter(|slot| *slot < k as u64)
            .ok_or_else(|| MediaFecError::invalid("audio packet does not belong to the parity's block"))?
            as usize;
        if packet.stream_id != geometry.stream_id
            || packet.session_id != geometry.session_id
            || packet.payload.len() != shard_bytes
        {
            return Err(MediaFecError::invalid("audio packet disagrees with the parity's block"));
        }
        shards[slot].get_or_insert_with(|| packet.payload.clone());
    }

    let missing: Vec<usize> = (0..k).filter(|&slot| shards[slot].is_none()).collect();
    let recovered = recover_data_shards(k, shards)?;
    missing
        .into_iter()
        .map(|slot| {
            let (packet_id, pts_ticks) = audio_slot(
                geometry.base_packet_id,
                geometry.base_pts_ticks,
                geometry.packet_duration_ticks,
                slot as u64,
            )
            .ok_or_else(|| MediaFecError::invalid("audio block runs past the packet id or pts range"))?;
            Ok(GameCultMediaAudioPacketRecord {
                stream_id: geometry.stream_id.clone(),
                session_id: geometry.session_id.clone(),
                packet_id,
                codec: geometry.codec.clone(),
                pts_ticks,
                duration_ticks: geometry.packet_duration_ticks,
                timebase_num: geometry.timebase_num,
                timebase_den: geometry.timebase_den,
                deadline_ticks: geometry.deadline_ticks,
                payload: recovered[slot].clone(),
            })
        })
        .collect()
}

fn same_audio_block(a: &GameCultMediaAudioParityShardRecord, b: &GameCultMediaAudioParityShardRecord) -> bool {
    a.stream_id == b.stream_id
        && a.session_id == b.session_id
        && a.codec == b.codec
        && a.fec_scheme == b.fec_scheme
        && a.base_packet_id == b.base_packet_id
        && a.base_pts_ticks == b.base_pts_ticks
        && a.packet_duration_ticks == b.packet_duration_ticks
        && a.timebase_num == b.timebase_num
        && a.timebase_den == b.timebase_den
        && a.deadline_ticks == b.deadline_ticks
        && a.data_shard_count == b.data_shard_count
        && a.parity_shard_count == b.parity_shard_count
        && a.shard_payload_bytes == b.shard_payload_bytes
}
