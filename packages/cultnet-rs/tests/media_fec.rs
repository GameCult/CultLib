//! The media FEC codec's guarantees, proved against its public surface.
//!
//! Every test drives `protect_*` and `recover_*` the way a producer and a
//! consumer would, and asserts what a consumer is entitled to rely on: what is
//! recovered is exactly what was sent, what cannot be recovered is reported and
//! never guessed, and a shard always fits a datagram.

use cultnet_rs::{
    GameCultMediaAudioPacketRecord, GameCultMediaAudioParityShardRecord,
    GameCultMediaVideoAccessUnitRecord, GameCultMediaVideoParityShardRecord,
    GameCultMediaWireRecord, MEDIA_FEC_MAX_SHARD_PAYLOAD_BYTES, MEDIA_FEC_MAX_WIRE_BYTES,
    MEDIA_FEC_SCHEME_RS_GF256_V1, MediaFecError, MediaFecPolicy, MediaWireProvenance,
    decode_media_wire_record, encode_media_wire_record, protect_audio_block, protect_video_frame,
    recover_audio_block, recover_video_block,
};

const STANDARD: MediaFecPolicy = MediaFecPolicy::STANDARD;

/// Deterministic, non-constant bytes so a shard mix-up cannot pass by accident.
fn bytes(seed: u32, len: usize) -> Vec<u8> {
    let mut state = seed.wrapping_mul(2_654_435_761).wrapping_add(1);
    (0..len)
        .map(|_| {
            state = state.wrapping_mul(1_664_525).wrapping_add(1_013_904_223);
            (state >> 24) as u8
        })
        .collect()
}

/// One frame of `chunk_count` chunks: every chunk `chunk_bytes` long except the
/// last, which is `last_bytes`.
fn frame(
    frame_id: u64,
    chunk_count: u16,
    chunk_bytes: usize,
    last_bytes: usize,
) -> Vec<GameCultMediaVideoAccessUnitRecord> {
    (0..chunk_count)
        .map(|index| GameCultMediaVideoAccessUnitRecord {
            stream_id: "muninn.raven.av.rudp".to_string(),
            session_id: "session-1".to_string(),
            frame_id,
            codec: "h264".to_string(),
            pts_ticks: 90_000,
            duration_ticks: 3_000,
            timebase_num: 1,
            timebase_den: 90_000,
            keyframe: frame_id % 2 == 0,
            dependency_frame_id: (frame_id % 2 == 1).then(|| frame_id - 1),
            deadline_ticks: 90_000 + 22_500,
            chunk_index: index,
            chunk_count,
            payload: bytes(
                (frame_id as u32).wrapping_mul(1_000).wrapping_add(u32::from(index)),
                if index + 1 == chunk_count { last_bytes } else { chunk_bytes },
            ),
        })
        .collect()
}

fn audio_block(base_packet_id: u64, payload_bytes: usize) -> Vec<GameCultMediaAudioPacketRecord> {
    (0..4_u64)
        .map(|index| GameCultMediaAudioPacketRecord {
            stream_id: "muninn.raven.av.rudp".to_string(),
            session_id: "session-1".to_string(),
            packet_id: base_packet_id + index,
            codec: "opus".to_string(),
            pts_ticks: 48_000 + 960 * index as i64,
            duration_ticks: 960,
            timebase_num: 1,
            timebase_den: 48_000,
            deadline_ticks: 60_000 + 960 * index as i64,
            payload: bytes((base_packet_id as u32).wrapping_mul(10).wrapping_add(index as u32), payload_bytes),
        })
        .collect()
}

fn split(
    records: Vec<GameCultMediaWireRecord>,
) -> (Vec<GameCultMediaVideoAccessUnitRecord>, Vec<GameCultMediaVideoParityShardRecord>) {
    let mut data = Vec::new();
    let mut parity = Vec::new();
    for record in records {
        match record {
            GameCultMediaWireRecord::Video(record) => data.push(record),
            GameCultMediaWireRecord::VideoParity(record) => parity.push(record),
            other => panic!("a video frame produced {}", other.schema_id()),
        }
    }
    (data, parity)
}

/// The block a wire record belongs to, from its own fields.
fn block_of(record: &GameCultMediaWireRecord, sizes: &[u16]) -> usize {
    match record {
        GameCultMediaWireRecord::Video(chunk) => {
            let mut start = 0_u16;
            for (block, size) in sizes.iter().enumerate() {
                if chunk.chunk_index < start + size {
                    return block;
                }
                start += size;
            }
            panic!("chunk {} is outside every block", chunk.chunk_index)
        }
        GameCultMediaWireRecord::VideoParity(parity) => usize::from(parity.block_index),
        other => panic!("not a video record: {}", other.schema_id()),
    }
}

// ---------------------------------------------------------------------------
// Recovery: the MDS guarantee, exhaustively
// ---------------------------------------------------------------------------

#[test]
fn the_policy_gives_the_ruled_block_geometry() {
    // m = max(2, ceil(k / 4)), written independently of the policy.
    for k in 1..=16_u16 {
        let expected = std::cmp::max(2, (k + 3) / 4);
        assert_eq!(STANDARD.video_parity_shards(k), expected, "k = {k}");
    }
    assert_eq!(STANDARD.audio_data_shards, 4);
    assert_eq!(STANDARD.audio_parity_shards, 2);
}

#[test]
fn a_frame_splits_into_ceil_n_over_16_near_equal_blocks() {
    for n in 1..=200_u16 {
        let sizes = STANDARD.video_block_sizes(n);
        assert_eq!(sizes.len(), usize::from(n.div_ceil(16)), "n = {n}");
        assert_eq!(sizes.iter().sum::<u16>(), n, "n = {n}");
        assert!(sizes.iter().all(|size| (1..=16).contains(size)), "n = {n}: {sizes:?}");
        let (widest, narrowest) = (sizes.iter().max().unwrap(), sizes.iter().min().unwrap());
        assert!(widest - narrowest <= 1, "n = {n}: {sizes:?}");
        assert!(sizes.windows(2).all(|pair| pair[0] >= pair[1]), "larger blocks first: {sizes:?}");
    }
}

/// Erases every pattern of at most `m` records from a `k`-chunk frame's wire
/// output and requires the data back, byte for byte, then requires every
/// `m + 1` pattern to be refused.
fn assert_every_erasure_pattern_behaves(k: u16) {
    let original = frame(u64::from(k), k, 37, 21);
    let records = protect_video_frame(&original, &STANDARD).expect("protects");
    let m = usize::from(STANDARD.video_parity_shards(k));
    let total = usize::from(k) + m;
    assert_eq!(records.len(), total, "one block: k data and m parity records");

    for erased in 0_u32..(1 << total) {
        let erasures = erased.count_ones() as usize;
        if erasures > m + 1 {
            continue;
        }
        let mut data = Vec::new();
        let mut parity = Vec::new();
        for (slot, record) in records.iter().enumerate() {
            if erased & (1 << slot) != 0 {
                continue;
            }
            match record {
                GameCultMediaWireRecord::Video(chunk) => data.push(chunk.clone()),
                GameCultMediaWireRecord::VideoParity(shard) => parity.push(shard.clone()),
                _ => unreachable!(),
            }
        }
        if parity.is_empty() {
            continue; // a receiver holding no parity has nothing to recover with
        }
        let outcome = recover_video_block(&parity, &data);
        if erasures <= m {
            let recovered = outcome.unwrap_or_else(|error| {
                panic!("k={k} m={m} erased={erased:#b}: {error}");
            });
            data.extend(recovered);
            data.sort_by_key(|chunk| chunk.chunk_index);
            assert_eq!(data, original, "k={k} m={m} erased={erased:#b}");
        } else {
            assert!(
                matches!(outcome, Err(MediaFecError::BeyondRepair { .. })),
                "k={k} m={m} erased={erased:#b}: {outcome:?} must be beyond repair, never wrong bytes"
            );
        }
    }
}

#[test]
fn every_le_m_erasure_recovers_exactly_and_m_plus_one_is_refused_for_every_video_k() {
    for k in 1..=16 {
        assert_every_erasure_pattern_behaves(k);
    }
}

#[test]
fn every_le_m_erasure_recovers_exactly_and_m_plus_one_is_refused_for_audio_4_plus_2() {
    let original = audio_block(100, 41);
    let parity = protect_audio_block(&original, &STANDARD).expect("protects");
    assert_eq!(parity.len(), 2);
    for erased in 0_u32..(1 << 6) {
        let erasures = erased.count_ones();
        let data: Vec<_> = (0..4)
            .filter(|slot| erased & (1 << slot) == 0)
            .map(|slot| original[slot].clone())
            .collect();
        let present_parity: Vec<_> = (0..2)
            .filter(|slot| erased & (1 << (4 + slot)) == 0)
            .map(|slot| parity[slot].clone())
            .collect();
        if present_parity.is_empty() {
            continue;
        }
        let outcome = recover_audio_block(&present_parity, &data);
        if erasures <= 2 {
            let mut all = data.clone();
            let block_deadline = original.iter().map(|p| p.deadline_ticks).max().unwrap();
            for mut packet in outcome.unwrap_or_else(|error| panic!("erased={erased:#b}: {error}")) {
                // A recovered packet carries the block's deadline, not its own.
                assert_eq!(packet.deadline_ticks, block_deadline, "erased={erased:#b}");
                packet.deadline_ticks = original[(packet.packet_id - 100) as usize].deadline_ticks;
                all.push(packet);
            }
            all.sort_by_key(|packet| packet.packet_id);
            assert_eq!(all, original, "erased={erased:#b}");
        } else {
            assert!(
                matches!(outcome, Err(MediaFecError::BeyondRepair { .. })),
                "erased={erased:#b}: {outcome:?}"
            );
        }
    }
}

#[test]
fn recovery_reports_how_short_the_block_is() {
    let original = frame(9, 8, 37, 37);
    let (data, parity) = split(protect_video_frame(&original, &STANDARD).unwrap());
    // 8 data + 2 parity; keep 6 data and 1 parity: 7 present, 8 needed.
    let outcome = recover_video_block(&parity[..1], &data[..6]);
    assert_eq!(
        outcome,
        Err(MediaFecError::BeyondRepair { shards_present: 7, shards_needed: 8 })
    );
}

#[test]
fn a_recovered_short_final_chunk_has_its_true_length() {
    let original = frame(4, 5, 100, 7);
    let (mut data, parity) = split(protect_video_frame(&original, &STANDARD).unwrap());
    let lost = data.pop().unwrap();
    assert_eq!(lost.payload.len(), 7);
    let recovered = recover_video_block(&parity, &data).unwrap();
    assert_eq!(recovered, vec![lost]);
}

#[test]
fn duplicate_shards_neither_count_twice_nor_break_recovery() {
    let original = frame(5, 4, 50, 50);
    let (mut data, parity) = split(protect_video_frame(&original, &STANDARD).unwrap());
    let lost = data.remove(1);
    data.push(data[0].clone());
    let mut parity_twice = parity.clone();
    parity_twice.extend(parity);
    assert_eq!(recover_video_block(&parity_twice, &data).unwrap(), vec![lost]);
}

#[test]
fn a_complete_block_recovers_nothing() {
    let original = frame(6, 4, 50, 50);
    let (data, parity) = split(protect_video_frame(&original, &STANDARD).unwrap());
    assert_eq!(recover_video_block(&parity[..1], &data).unwrap(), Vec::new());
}

#[test]
fn recovery_refuses_records_that_are_not_one_block() {
    let original = frame(7, 32, 40, 40);
    let (data, parity) = split(protect_video_frame(&original, &STANDARD).unwrap());
    let block_zero: Vec<_> = parity.iter().filter(|p| p.block_index == 0).cloned().collect();
    let block_one: Vec<_> = parity.iter().filter(|p| p.block_index == 1).cloned().collect();
    assert_eq!(block_zero.len(), 4);

    let mixed = vec![block_zero[0].clone(), block_one[0].clone()];
    assert!(matches!(recover_video_block(&mixed, &[]), Err(MediaFecError::Invalid(_))));

    // A chunk from block one handed to block zero's parity.
    // The first chunk of block one, exactly one past block zero's last.
    let stray = data.iter().find(|chunk| chunk.chunk_index == 16).unwrap().clone();
    assert!(matches!(recover_video_block(&block_zero, &[stray]), Err(MediaFecError::Invalid(_))));

    // A chunk whose length disagrees with the block's.
    let mut wrong_length = data[0].clone();
    wrong_length.payload.push(0);
    assert!(matches!(
        recover_video_block(&block_zero, &[wrong_length]),
        Err(MediaFecError::Invalid(_))
    ));

    assert!(matches!(recover_video_block(&[], &data), Err(MediaFecError::Invalid(_))));

    // Another frame's chunk.
    let mut other_frame = data.iter().find(|chunk| chunk.chunk_index == 3).unwrap().clone();
    other_frame.frame_id += 1;
    assert!(matches!(recover_video_block(&block_zero, &[other_frame]), Err(MediaFecError::Invalid(_))));
}

#[test]
fn a_final_chunk_of_the_wrong_length_is_refused() {
    let original = frame(8, 5, 100, 7);
    let (data, parity) = split(protect_video_frame(&original, &STANDARD).unwrap());
    let mut last = data.iter().find(|chunk| chunk.chunk_index == 4).unwrap().clone();
    last.payload.push(0);
    assert!(matches!(recover_video_block(&parity, &[last]), Err(MediaFecError::Invalid(_))));
}

#[test]
fn within_a_block_data_and_parity_alternate() {
    let records = protect_video_frame(&frame(9, 4, 20, 20), &STANDARD).unwrap();
    let kinds: String = records
        .iter()
        .map(|record| match record {
            GameCultMediaWireRecord::Video(_) => 'D',
            GameCultMediaWireRecord::VideoParity(_) => 'P',
            _ => '?',
        })
        .collect();
    assert_eq!(kinds, "DPDPDD", "k = 4, m = 2: the longer list finishes the lane");
}

#[test]
fn audio_recovery_refuses_packets_that_are_not_from_the_block() {
    let original = audio_block(100, 20);
    let parity = protect_audio_block(&original, &STANDARD).unwrap();
    let next_block = audio_block(104, 20);
    // One past the block's last packet, and one before its first.
    for stray in [next_block[0].clone(), audio_block(96, 20)[3].clone()] {
        assert!(matches!(
            recover_audio_block(&parity, &[stray]),
            Err(MediaFecError::Invalid(_))
        ));
    }
    let mut other_session = original[0].clone();
    other_session.session_id = "session-2".to_string();
    assert!(matches!(recover_audio_block(&parity, &[other_session]), Err(MediaFecError::Invalid(_))));
    let mut wrong_length = original[0].clone();
    wrong_length.payload.push(0);
    assert!(matches!(recover_audio_block(&parity, &[wrong_length]), Err(MediaFecError::Invalid(_))));
    assert!(matches!(recover_audio_block(&[], &original), Err(MediaFecError::Invalid(_))));

    let mut mixed = parity.clone();
    mixed[1].base_packet_id += 1;
    assert!(matches!(recover_audio_block(&mixed, &original[..3]), Err(MediaFecError::Invalid(_))));
}

#[test]
fn a_frame_of_several_blocks_recovers_each_block_from_its_own_parity() {
    let original = frame(11, 40, 60, 33);
    let records = protect_video_frame(&original, &STANDARD).unwrap();
    let sizes = STANDARD.video_block_sizes(40);
    assert_eq!(sizes, vec![14, 13, 13]);
    let (data, parity) = split(records);

    // Lose the first two data chunks of every block.
    let mut lost_indexes = Vec::new();
    let mut start = 0_u16;
    for size in &sizes {
        lost_indexes.extend([start, start + 1]);
        start += size;
    }
    let held: Vec<_> = data.iter().filter(|c| !lost_indexes.contains(&c.chunk_index)).cloned().collect();
    let mut all = held.clone();
    for block in 0..3_u16 {
        let block_parity: Vec<_> = parity.iter().filter(|p| p.block_index == block).cloned().collect();
        let lo = sizes[..usize::from(block)].iter().sum::<u16>();
        let block_data: Vec<_> = held
            .iter()
            .filter(|c| c.chunk_index >= lo && c.chunk_index < lo + sizes[usize::from(block)])
            .cloned()
            .collect();
        all.extend(recover_video_block(&block_parity, &block_data).unwrap());
    }
    all.sort_by_key(|chunk| chunk.chunk_index);
    assert_eq!(all, original);
}

// ---------------------------------------------------------------------------
// Known answers: the scheme is pinned, not the library
// ---------------------------------------------------------------------------

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}

/// Four 37-byte audio payloads, `payload[i][j] = (i * 37 + j * 7 + 3) mod 256`.
/// If these bytes ever change, `rs-gf256-v1` changed meaning: the matrix, the
/// field polynomial, or the shard order. Bump the scheme id, do not edit them.
#[test]
fn known_answer_audio_parity_bytes() {
    let mut block = audio_block(1, 37);
    for (i, packet) in block.iter_mut().enumerate() {
        packet.payload = (0..37).map(|j| (i * 37 + j * 7 + 3) as u8).collect();
    }
    let parity = protect_audio_block(&block, &STANDARD).unwrap();
    assert_eq!(parity[0].fec_scheme, "rs-gf256-v1");
    assert_eq!(
        hex(&parity[0].payload),
        "f246399711f2ecefa4147ae3856317be83b9a87c0ed13bc552918473bc08e869a91dd98356",
        "parity 0"
    );
    assert_eq!(
        hex(&parity[1].payload),
        "a1532c08e588cc05da6175c61b09a704f7d6c7597d76fbef76435656bf65e2331def91dcbf",
        "parity 1"
    );
}

/// The video path pads a short final chunk with zeros before computing parity.
/// Chunks of 5, 5 and 3 bytes, `payload[i][j] = (i * 11 + j * 3 + 1)`.
#[test]
fn known_answer_video_parity_bytes_pad_a_short_final_chunk_with_zeros() {
    let mut chunks = frame(2, 3, 5, 3);
    for chunk in &mut chunks {
        let len = chunk.payload.len();
        chunk.payload = (0..len).map(|j| (usize::from(chunk.chunk_index) * 11 + j * 3 + 1) as u8).collect();
    }
    let (_, parity) = split(protect_video_frame(&chunks, &STANDARD).unwrap());
    assert_eq!(parity.len(), 2);
    assert_eq!(hex(&parity[0].payload), "1a11081f15", "parity 0");
    assert_eq!(hex(&parity[1].payload), "1d18f3ce8b", "parity 1");
    assert_eq!(parity[0].shard_payload_bytes, 5);
    assert_eq!(parity[0].last_chunk_payload_bytes, 3);
}

#[test]
fn parity_is_a_pure_function_of_its_inputs() {
    let original = frame(3, 20, 200, 90);
    assert_eq!(
        protect_video_frame(&original, &STANDARD).unwrap(),
        protect_video_frame(&original, &STANDARD).unwrap()
    );
    let block = audio_block(8, 64);
    assert_eq!(
        protect_audio_block(&block, &STANDARD).unwrap(),
        protect_audio_block(&block, &STANDARD).unwrap()
    );
}

// ---------------------------------------------------------------------------
// One datagram
// ---------------------------------------------------------------------------

fn provenance() -> MediaWireProvenance<'static> {
    MediaWireProvenance {
        stored_at: "unix:1700000000000",
        runtime_id: "raven-muninn-primary",
        role: "muninn.media",
        producer: "muninn",
    }
}

#[test]
fn the_datagram_budget_is_the_rudp_fragment_the_media_channel_leaves() {
    // 1,472 bytes of UDP payload, less the 36-byte RUDP header and the 5-byte
    // channel name "media".
    assert_eq!(MEDIA_FEC_MAX_WIRE_BYTES, 1_431);
}

/// Identifiers are the caller's, so the bound is stated for stream and session
/// identifiers, and codec names, of up to this many bytes each.
const IDENTIFIER_BYTES: usize = 32;

#[test]
fn every_record_at_the_maximum_shard_size_fits_one_datagram() {
    let long = |prefix: &str| format!("{prefix}{}", "x".repeat(IDENTIFIER_BYTES - prefix.len()));
    let mut chunks = frame(u64::MAX - 1, 20, MEDIA_FEC_MAX_SHARD_PAYLOAD_BYTES, MEDIA_FEC_MAX_SHARD_PAYLOAD_BYTES);
    for chunk in &mut chunks {
        chunk.stream_id = long("stream-");
        chunk.session_id = long("session-");
        chunk.codec = long("codec-");
    }
    let records = protect_video_frame(&chunks, &STANDARD).unwrap();
    assert!(records.len() > chunks.len());
    let mut largest = 0;
    for record in &records {
        let wire = encode_media_wire_record(record, provenance()).unwrap();
        largest = largest.max(wire.len());
        assert!(
            wire.len() <= MEDIA_FEC_MAX_WIRE_BYTES,
            "{} record is {} bytes, over {MEDIA_FEC_MAX_WIRE_BYTES}",
            record.schema_id(),
            wire.len()
        );
    }

    let mut packets = audio_block(u64::MAX - 8, MEDIA_FEC_MAX_SHARD_PAYLOAD_BYTES);
    for packet in &mut packets {
        packet.stream_id = long("stream-");
        packet.session_id = long("session-");
        packet.codec = long("codec-");
    }
    let mut audio: Vec<_> = packets.iter().cloned().map(GameCultMediaWireRecord::Audio).collect();
    audio.extend(protect_audio_block(&packets, &STANDARD).unwrap().into_iter().map(GameCultMediaWireRecord::AudioParity));
    for record in &audio {
        let wire = encode_media_wire_record(record, provenance()).unwrap();
        largest = largest.max(wire.len());
        assert!(wire.len() <= MEDIA_FEC_MAX_WIRE_BYTES, "{} is {} bytes", record.schema_id(), wire.len());
    }
    // The limit is not slack: the largest record leaves less than a full extra
    // identifier's worth of headroom.
    assert!(
        largest + 96 > MEDIA_FEC_MAX_WIRE_BYTES,
        "the shard limit wastes {} bytes of datagram",
        MEDIA_FEC_MAX_WIRE_BYTES - largest
    );
}

#[test]
fn a_shard_over_the_limit_is_refused_at_the_producer_and_at_the_decoder() {
    let too_big = MEDIA_FEC_MAX_SHARD_PAYLOAD_BYTES + 1;
    let chunks = frame(1, 3, too_big, too_big);
    assert!(matches!(protect_video_frame(&chunks, &STANDARD), Err(MediaFecError::Invalid(_))));
    assert!(matches!(
        protect_audio_block(&audio_block(1, too_big), &STANDARD),
        Err(MediaFecError::Invalid(_))
    ));

    let ok = frame(1, 3, MEDIA_FEC_MAX_SHARD_PAYLOAD_BYTES, MEDIA_FEC_MAX_SHARD_PAYLOAD_BYTES);
    let (_, mut parity) = split(protect_video_frame(&ok, &STANDARD).unwrap());
    parity[0].shard_payload_bytes = too_big as u32;
    parity[0].payload = vec![0; too_big];
    let wire = encode_media_wire_record(&GameCultMediaWireRecord::VideoParity(parity[0].clone()), provenance()).unwrap();
    assert!(decode_media_wire_record(&wire).is_err());
}

// ---------------------------------------------------------------------------
// Framing: what a block may and may not contain
// ---------------------------------------------------------------------------

#[test]
fn a_block_never_spans_frames() {
    let mut chunks = frame(20, 4, 30, 30);
    chunks[2].frame_id = 21;
    let error = protect_video_frame(&chunks, &STANDARD).unwrap_err();
    assert!(matches!(error, MediaFecError::Invalid(_)), "{error}");

    let mut chunks = frame(20, 4, 30, 30);
    chunks[3].deadline_ticks += 1;
    assert!(protect_video_frame(&chunks, &STANDARD).is_err(), "parity would misstate the deadline");

    let mut chunks = frame(20, 4, 30, 30);
    chunks[1].session_id = "session-2".to_string();
    assert!(protect_video_frame(&chunks, &STANDARD).is_err());
}

#[test]
fn a_frame_must_be_whole_and_shard_shaped() {
    let mut chunks = frame(30, 4, 30, 30);
    chunks.pop();
    assert!(protect_video_frame(&chunks, &STANDARD).is_err(), "a missing chunk");

    let mut chunks = frame(30, 4, 30, 30);
    chunks[3] = chunks[2].clone();
    assert!(protect_video_frame(&chunks, &STANDARD).is_err(), "a repeated chunk");

    let mut chunks = frame(30, 4, 30, 30);
    chunks[1].payload.truncate(29);
    assert!(protect_video_frame(&chunks, &STANDARD).is_err(), "a short chunk that is not last");

    let mut chunks = frame(30, 4, 30, 30);
    chunks[3].payload.push(0);
    assert!(protect_video_frame(&chunks, &STANDARD).is_err(), "a last chunk longer than the shard");

    assert!(protect_video_frame(&[], &STANDARD).is_err());
}

#[test]
fn chunks_may_arrive_in_any_order() {
    let original = frame(31, 6, 30, 12);
    let mut shuffled = original.clone();
    shuffled.reverse();
    assert_eq!(
        protect_video_frame(&original, &STANDARD).unwrap(),
        protect_video_frame(&shuffled, &STANDARD).unwrap()
    );
}

#[test]
fn an_audio_block_is_four_contiguous_equal_length_packets() {
    assert!(protect_audio_block(&audio_block(1, 20)[..3], &STANDARD).is_err(), "three packets");
    let mut packets = audio_block(1, 20);
    packets[2].packet_id += 1;
    assert!(protect_audio_block(&packets, &STANDARD).is_err(), "a gap in packet ids");
    let mut packets = audio_block(1, 20);
    packets[1].pts_ticks += 1;
    assert!(protect_audio_block(&packets, &STANDARD).is_err(), "a gap in presentation time");
    let mut packets = audio_block(1, 20);
    packets[3].payload.push(0);
    assert!(protect_audio_block(&packets, &STANDARD).is_err(), "unequal payload lengths");
    let mut packets = audio_block(1, 20);
    packets[0].codec = "pcm".to_string();
    assert!(protect_audio_block(&packets, &STANDARD).is_err(), "mixed codecs");
    let mut packets = audio_block(1, 20);
    packets[2].session_id = "session-2".to_string();
    assert!(protect_audio_block(&packets, &STANDARD).is_err(), "mixed sessions");
    let mut packets = audio_block(1, 20);
    packets[2].stream_id = "other".to_string();
    assert!(protect_audio_block(&packets, &STANDARD).is_err(), "mixed streams");
    let mut packets = audio_block(1, 20);
    packets[1].timebase_den = 44_100;
    assert!(protect_audio_block(&packets, &STANDARD).is_err(), "mixed timebases");
    let mut packets = audio_block(1, 20);
    packets[3].duration_ticks = 480;
    assert!(protect_audio_block(&packets, &STANDARD).is_err(), "mixed durations");
}

#[test]
fn parity_carries_its_frames_deadline_unchanged() {
    let original = frame(40, 40, 50, 50);
    let (_, parity) = split(protect_video_frame(&original, &STANDARD).unwrap());
    assert!(!parity.is_empty());
    for shard in &parity {
        assert_eq!(shard.deadline_ticks, original[0].deadline_ticks);
        assert_eq!(shard.pts_ticks, original[0].pts_ticks);
        assert_eq!(shard.frame_id, 40);
        assert_eq!(shard.fec_scheme, MEDIA_FEC_SCHEME_RS_GF256_V1);
    }
}

#[test]
fn audio_parity_carries_the_latest_deadline_in_its_block_and_recovery_uses_it() {
    let mut packets = audio_block(50, 30);
    packets[1].deadline_ticks = 99_999; // the block's latest, not its last
    let parity = protect_audio_block(&packets, &STANDARD).unwrap();
    assert!(parity.iter().all(|shard| shard.deadline_ticks == 99_999));

    let held = vec![packets[0].clone(), packets[2].clone(), packets[3].clone()];
    let recovered = recover_audio_block(&parity[..1], &held).unwrap();
    assert_eq!(recovered.len(), 1);
    let packet = &recovered[0];
    assert_eq!(packet.packet_id, 51, "id from the block's arithmetic");
    assert_eq!(packet.pts_ticks, packets[1].pts_ticks, "pts from the block's arithmetic");
    assert_eq!(packet.deadline_ticks, 99_999, "deadline from the block");
    assert_eq!(packet.payload, packets[1].payload);
    assert_eq!(packet.codec, "opus");
}

// ---------------------------------------------------------------------------
// Send order: a burst must land as one loss per block
// ---------------------------------------------------------------------------

#[test]
fn send_order_is_a_permutation_of_the_frames_records() {
    for n in [1_u16, 2, 15, 16, 17, 31, 32, 33, 47, 100] {
        let original = frame(60, n, 20, 9);
        let records = protect_video_frame(&original, &STANDARD).unwrap();
        let sizes = STANDARD.video_block_sizes(n);
        let expected_parity: usize = sizes.iter().map(|&k| usize::from(STANDARD.video_parity_shards(k))).sum();
        assert_eq!(records.len(), usize::from(n) + expected_parity, "n = {n}");
        let mut data_indexes: Vec<u16> = records
            .iter()
            .filter_map(|r| match r {
                GameCultMediaWireRecord::Video(c) => Some(c.chunk_index),
                _ => None,
            })
            .collect();
        data_indexes.sort_unstable();
        assert_eq!(data_indexes, (0..n).collect::<Vec<_>>(), "n = {n}");
    }
}

#[test]
fn equal_blocks_send_one_record_per_block_before_any_block_repeats() {
    for n in [32_u16, 48, 64, 24, 20, 18] {
        let sizes = STANDARD.video_block_sizes(n);
        let blocks = sizes.len();
        assert!(sizes.windows(2).all(|pair| pair[0] == pair[1]), "n = {n} must split equally");
        let records = protect_video_frame(&frame(70, n, 20, 20), &STANDARD).unwrap();
        let order: Vec<usize> = records.iter().map(|r| block_of(r, &sizes)).collect();
        for window in order.windows(blocks) {
            let mut seen = window.to_vec();
            seen.sort_unstable();
            seen.dedup();
            assert_eq!(seen.len(), blocks, "n = {n}: a window of {blocks} repeats a block: {order:?}");
        }
    }
}

/// Blocks that differ in size run out at the end and cannot keep the full
/// spacing. What holds: two records of one block are never closer than the
/// number of blocks still sending from the later one onward.
#[test]
fn unequal_blocks_keep_every_block_spaced_by_the_blocks_still_sending() {
    for n in [17_u16, 19, 35, 40, 50, 100] {
        let sizes = STANDARD.video_block_sizes(n);
        assert!(sizes.windows(2).any(|pair| pair[0] != pair[1]), "n = {n} must split unequally");
        let records = protect_video_frame(&frame(71, n, 20, 20), &STANDARD).unwrap();
        let order: Vec<usize> = records.iter().map(|r| block_of(r, &sizes)).collect();
        for later in 0..order.len() {
            let Some(earlier) = (0..later).rev().find(|&i| order[i] == order[later]) else { continue };
            let mut still_sending = order[later..].to_vec();
            still_sending.sort_unstable();
            still_sending.dedup();
            assert!(
                later - earlier >= still_sending.len(),
                "n = {n}: block {} sent at {earlier} and again at {later} with {} blocks still sending: {order:?}",
                order[later],
                still_sending.len()
            );
        }
    }
}

// ---------------------------------------------------------------------------
// The wire round trip, and the policy
// ---------------------------------------------------------------------------

#[test]
fn a_frame_survives_the_wire_and_recovers_from_what_arrives() {
    let original = frame(80, 20, 300, 111);
    let sizes = STANDARD.video_block_sizes(20);
    let records = protect_video_frame(&original, &STANDARD).unwrap();

    // The network eats a burst of four consecutive records.
    let arrived: Vec<GameCultMediaWireRecord> = records
        .iter()
        .enumerate()
        .filter(|(slot, _)| !(9..13).contains(slot))
        .map(|(_, record)| {
            let wire = encode_media_wire_record(record, provenance()).unwrap();
            decode_media_wire_record(&wire).unwrap()
        })
        .collect();

    let mut all = Vec::new();
    for block in 0..sizes.len() {
        let mut data = Vec::new();
        let mut parity = Vec::new();
        for record in arrived.iter().filter(|r| block_of(r, &sizes) == block) {
            match record {
                GameCultMediaWireRecord::Video(c) => data.push(c.clone()),
                GameCultMediaWireRecord::VideoParity(p) => parity.push(p.clone()),
                _ => unreachable!(),
            }
        }
        let recovered = recover_video_block(&parity, &data).expect("a burst of 4 over 2 blocks is 2 per block");
        all.extend(data);
        all.extend(recovered);
    }
    all.sort_by_key(|chunk| chunk.chunk_index);
    assert_eq!(all, original);
}

#[test]
fn a_custom_policy_sizes_blocks_by_its_own_fields() {
    let policy = MediaFecPolicy {
        video_max_block_data_shards: 8,
        video_min_parity_shards: 1,
        video_parity_divisor: 2,
        ..STANDARD
    };
    assert_eq!(policy.video_block_sizes(20), vec![7, 7, 6]);
    assert_eq!(policy.video_parity_shards(7), 4);
    assert_eq!(policy.video_parity_shards(1), 1);
    let (data, parity) = split(protect_video_frame(&frame(90, 20, 20, 20), &policy).unwrap());
    assert_eq!(data.len(), 20);
    assert_eq!(parity.len(), 4 + 4 + 3);
    assert_eq!(MediaFecPolicy::default(), STANDARD);
}

#[test]
fn a_policy_that_cannot_form_a_block_is_refused() {
    let zero = MediaFecPolicy { video_min_parity_shards: 0, ..STANDARD };
    assert!(protect_video_frame(&frame(1, 2, 10, 10), &zero).is_err());
    let too_wide = MediaFecPolicy { video_max_block_data_shards: 255, ..STANDARD };
    assert!(protect_video_frame(&frame(1, 2, 10, 10), &too_wide).is_err());
    let audio_too_wide = MediaFecPolicy { audio_data_shards: 255, audio_parity_shards: 2, ..STANDARD };
    assert!(protect_audio_block(&audio_block(1, 10), &audio_too_wide).is_err());
}

#[test]
fn parity_records_survive_the_positional_encoding_unchanged() {
    let (_, parity) = split(protect_video_frame(&frame(5, 5, 40, 17), &STANDARD).unwrap());
    let audio = protect_audio_block(&audio_block(5, 25), &STANDARD).unwrap();
    for shard in parity {
        let bytes = rmp_serde::to_vec(&shard).unwrap();
        assert_eq!(rmp_serde::from_slice::<GameCultMediaVideoParityShardRecord>(&bytes).unwrap(), shard);
    }
    for shard in audio {
        let bytes = rmp_serde::to_vec(&shard).unwrap();
        assert_eq!(rmp_serde::from_slice::<GameCultMediaAudioParityShardRecord>(&bytes).unwrap(), shard);
    }
}
