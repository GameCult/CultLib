//! The media FEC codec's guarantees, proved against its public surface.
//!
//! Every test drives `protect_*` and `recover_*` the way a producer and a
//! consumer would, and asserts what a consumer is entitled to rely on: what is
//! recovered is exactly what was sent, what cannot be recovered is reported and
//! never guessed, and a shard always fits a datagram.

use cultnet_rs::{
    GameCultMediaAudioPacketRecord, GameCultMediaAudioParityShardRecord,
    GameCultMediaVideoAccessUnitRecord, GameCultMediaVideoParityShardRecord,
    GAMECULT_MEDIA_MAX_WIRE_BYTES, GameCultMediaWireRecord, MEDIA_FEC_MAX_WIRE_BYTES,
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
    let records = protect_video_frame(&original, &STANDARD, provenance()).expect("protects");
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
    let parity = protect_audio_block(&original, &STANDARD, provenance()).expect("protects");
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
    let (data, parity) = split(protect_video_frame(&original, &STANDARD, provenance()).unwrap());
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
    let (mut data, parity) = split(protect_video_frame(&original, &STANDARD, provenance()).unwrap());
    let lost = data.pop().unwrap();
    assert_eq!(lost.payload.len(), 7);
    let recovered = recover_video_block(&parity, &data).unwrap();
    assert_eq!(recovered, vec![lost]);
}

#[test]
fn duplicate_shards_neither_count_twice_nor_break_recovery() {
    let original = frame(5, 4, 50, 50);
    let (mut data, parity) = split(protect_video_frame(&original, &STANDARD, provenance()).unwrap());
    let lost = data.remove(1);
    data.push(data[0].clone());
    let mut parity_twice = parity.clone();
    parity_twice.extend(parity);
    assert_eq!(recover_video_block(&parity_twice, &data).unwrap(), vec![lost]);
}

#[test]
fn a_complete_block_recovers_nothing() {
    let original = frame(6, 4, 50, 50);
    let (data, parity) = split(protect_video_frame(&original, &STANDARD, provenance()).unwrap());
    assert_eq!(recover_video_block(&parity[..1], &data).unwrap(), Vec::new());
}

#[test]
fn recovery_refuses_records_that_are_not_one_block() {
    let original = frame(7, 32, 40, 40);
    let (data, parity) = split(protect_video_frame(&original, &STANDARD, provenance()).unwrap());
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
    let (data, parity) = split(protect_video_frame(&original, &STANDARD, provenance()).unwrap());
    let mut last = data.iter().find(|chunk| chunk.chunk_index == 4).unwrap().clone();
    last.payload.push(0);
    assert!(matches!(recover_video_block(&parity, &[last]), Err(MediaFecError::Invalid(_))));
}

#[test]
fn within_a_block_data_and_parity_alternate() {
    let records = protect_video_frame(&frame(9, 4, 20, 20), &STANDARD, provenance()).unwrap();
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
    let parity = protect_audio_block(&original, &STANDARD, provenance()).unwrap();
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
    let records = protect_video_frame(&original, &STANDARD, provenance()).unwrap();
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
    let parity = protect_audio_block(&block, &STANDARD, provenance()).unwrap();
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
    let (_, parity) = split(protect_video_frame(&chunks, &STANDARD, provenance()).unwrap());
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
        protect_video_frame(&original, &STANDARD, provenance()).unwrap(),
        protect_video_frame(&original, &STANDARD, provenance()).unwrap()
    );
    let block = audio_block(8, 64);
    assert_eq!(
        protect_audio_block(&block, &STANDARD, provenance()).unwrap(),
        protect_audio_block(&block, &STANDARD, provenance()).unwrap()
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

/// A provenance whose every field is 32 bytes, the widest the tests claim.
fn widest_provenance() -> MediaWireProvenance<'static> {
    let field = |prefix: &str| -> &'static str { Box::leak(long(prefix).into_boxed_str()) };
    MediaWireProvenance {
        stored_at: field("unix:"),
        runtime_id: field("runtime-"),
        role: field("role-"),
        producer: field("producer-"),
    }
}

fn long(prefix: &str) -> String {
    format!("{prefix}{}", "x".repeat(32 - prefix.len()))
}

/// Twenty chunks (two blocks) of an odd frame with a dependency, every field
/// at the width MessagePack spends the most bytes on.
fn widest_frame(payload_bytes: usize) -> Vec<GameCultMediaVideoAccessUnitRecord> {
    let mut chunks = frame(u64::MAX, 20, payload_bytes, payload_bytes);
    for chunk in &mut chunks {
        chunk.stream_id = long("stream-");
        chunk.session_id = long("session-");
        chunk.codec = long("codec-");
        chunk.pts_ticks = i64::MIN;
        chunk.deadline_ticks = i64::MAX;
        chunk.duration_ticks = u32::MAX;
        chunk.timebase_num = u32::MAX;
        chunk.timebase_den = u32::MAX;
        assert!(chunk.dependency_frame_id.is_some() && !chunk.keyframe);
    }
    chunks
}

fn widest_audio(payload_bytes: usize) -> Vec<GameCultMediaAudioPacketRecord> {
    let mut packets = audio_block(u64::MAX - 3, payload_bytes);
    for (index, packet) in packets.iter_mut().enumerate() {
        packet.stream_id = long("stream-");
        packet.session_id = long("session-");
        packet.codec = long("codec-");
        packet.duration_ticks = u32::MAX;
        packet.timebase_num = u32::MAX;
        packet.timebase_den = u32::MAX;
        packet.pts_ticks = i64::MIN + i64::from(u32::MAX) * index as i64;
        packet.deadline_ticks = i64::MAX;
    }
    packets
}

/// Fitting is monotonic in the payload, so the largest payload that fits is a
/// partition point.
fn largest_fitting_payload(fits: impl Fn(usize) -> bool) -> usize {
    let payloads: Vec<usize> = (1..=MEDIA_FEC_MAX_WIRE_BYTES).collect();
    let count = payloads.partition_point(|&payload| fits(payload));
    assert!(count > 0, "no payload fits");
    payloads[count - 1]
}

fn wire_len(record: &GameCultMediaWireRecord, provenance: MediaWireProvenance<'_>) -> usize {
    encode_media_wire_record(record, provenance).unwrap().len()
}

fn is_over_budget(error: &MediaFecError) -> bool {
    matches!(error, MediaFecError::Invalid(message) if message.contains("datagram budget"))
}

/// The invariant is "an encoded record fits one datagram", enforced where the
/// record is produced. At the widest legal identifiers the largest payload that
/// fits is found by search, the record it produces is exactly the budget, and
/// one byte more is refused.
#[test]
fn a_video_frame_is_protected_only_if_every_record_fits_the_datagram_budget() {
    let widest = widest_provenance();
    let fits = |payload: usize| protect_video_frame(&widest_frame(payload), &STANDARD, widest);
    let largest_payload = largest_fitting_payload(|payload| fits(payload).is_ok());
    assert!(
        largest_payload >= 600,
        "maximum-width identifiers must still leave room for a useful chunk, got {largest_payload}"
    );

    let records = fits(largest_payload).unwrap();
    let largest = records.iter().map(|record| wire_len(record, widest)).max().unwrap();
    assert_eq!(largest, MEDIA_FEC_MAX_WIRE_BYTES, "the budget is used to the byte, not approximated");

    let error = fits(largest_payload + 1).unwrap_err();
    assert!(is_over_budget(&error), "{error}");

    // The budget is the policy's, and it is inclusive.
    let exact = MediaFecPolicy { max_wire_bytes: largest, ..STANDARD };
    assert!(protect_video_frame(&widest_frame(largest_payload), &exact, widest).is_ok());
    let one_short = MediaFecPolicy { max_wire_bytes: largest - 1, ..STANDARD };
    let error = protect_video_frame(&widest_frame(largest_payload), &one_short, widest).unwrap_err();
    assert!(is_over_budget(&error), "{error}");
}

#[test]
fn an_audio_block_is_protected_only_if_every_record_fits_the_datagram_budget() {
    let widest = widest_provenance();
    let fits = |payload: usize| protect_audio_block(&widest_audio(payload), &STANDARD, widest);
    let largest_payload = largest_fitting_payload(|payload| fits(payload).is_ok());
    assert!(largest_payload >= 600, "got {largest_payload}");

    let packets = widest_audio(largest_payload);
    let parity = fits(largest_payload).unwrap();
    let largest_parity = parity
        .iter()
        .map(|shard| wire_len(&GameCultMediaWireRecord::AudioParity(shard.clone()), widest))
        .max()
        .unwrap();
    assert_eq!(largest_parity, MEDIA_FEC_MAX_WIRE_BYTES);
    // The parity is the largest record of the block, which is why only parity
    // is measured.
    for packet in &packets {
        assert!(wire_len(&GameCultMediaWireRecord::Audio(packet.clone()), widest) < largest_parity);
    }

    let error = fits(largest_payload + 1).unwrap_err();
    assert!(is_over_budget(&error), "{error}");
    let one_short = MediaFecPolicy { max_wire_bytes: largest_parity - 1, ..STANDARD };
    let error = protect_audio_block(&packets, &one_short, widest).unwrap_err();
    assert!(is_over_budget(&error), "{error}");
}

/// A path that carries less than the IPv4 default passes a smaller budget, and
/// a frame protected under the default is refused under it.
#[test]
fn a_smaller_datagram_budget_refuses_what_the_default_admits() {
    let chunks = frame(2, 20, 700, 700);
    assert!(protect_video_frame(&chunks, &STANDARD, provenance()).is_ok());
    let tunnel = MediaFecPolicy { max_wire_bytes: 1_211, ..STANDARD };
    let error = protect_video_frame(&chunks, &tunnel, provenance()).unwrap_err();
    assert!(is_over_budget(&error), "{error}");
}

/// The record a shard produces is sized by the fields around it, so a shard of
/// any length the budget admits is admitted at the decoder too: a jumbo-frame
/// producer is not stopped by a fixed shard cap.
#[test]
fn a_larger_datagram_budget_admits_larger_shards() {
    let jumbo = MediaFecPolicy { max_wire_bytes: 8_959, ..STANDARD };
    let chunks = frame(2, 3, 4_000, 4_000);
    let records = protect_video_frame(&chunks, &jumbo, provenance()).unwrap();
    for record in &records {
        let wire = encode_media_wire_record(record, provenance()).unwrap();
        assert!(decode_media_wire_record(&wire).is_ok());
    }
    assert!(protect_video_frame(&chunks, &STANDARD, provenance()).is_err());
}

// ---------------------------------------------------------------------------
// Framing: what a block may and may not contain
// ---------------------------------------------------------------------------

#[test]
fn a_block_never_spans_frames() {
    let mut chunks = frame(20, 4, 30, 30);
    chunks[2].frame_id = 21;
    let error = protect_video_frame(&chunks, &STANDARD, provenance()).unwrap_err();
    assert!(matches!(error, MediaFecError::Invalid(_)), "{error}");

    let mut chunks = frame(20, 4, 30, 30);
    chunks[3].deadline_ticks += 1;
    assert!(protect_video_frame(&chunks, &STANDARD, provenance()).is_err(), "parity would misstate the deadline");

    let mut chunks = frame(20, 4, 30, 30);
    chunks[1].session_id = "session-2".to_string();
    assert!(protect_video_frame(&chunks, &STANDARD, provenance()).is_err());
}

#[test]
fn a_frame_must_be_whole_and_shard_shaped() {
    let mut chunks = frame(30, 4, 30, 30);
    chunks.pop();
    assert!(protect_video_frame(&chunks, &STANDARD, provenance()).is_err(), "a missing chunk");

    let mut chunks = frame(30, 4, 30, 30);
    chunks[3] = chunks[2].clone();
    assert!(protect_video_frame(&chunks, &STANDARD, provenance()).is_err(), "a repeated chunk");

    let mut chunks = frame(30, 4, 30, 30);
    chunks[1].payload.truncate(29);
    assert!(protect_video_frame(&chunks, &STANDARD, provenance()).is_err(), "a short chunk that is not last");

    let mut chunks = frame(30, 4, 30, 30);
    chunks[3].payload.push(0);
    assert!(protect_video_frame(&chunks, &STANDARD, provenance()).is_err(), "a last chunk longer than the shard");

    assert!(protect_video_frame(&[], &STANDARD, provenance()).is_err());
}

#[test]
fn chunks_may_arrive_in_any_order() {
    let original = frame(31, 6, 30, 12);
    let mut shuffled = original.clone();
    shuffled.reverse();
    assert_eq!(
        protect_video_frame(&original, &STANDARD, provenance()).unwrap(),
        protect_video_frame(&shuffled, &STANDARD, provenance()).unwrap()
    );
}

#[test]
fn an_audio_block_is_four_contiguous_equal_length_packets() {
    assert!(protect_audio_block(&audio_block(1, 20)[..3], &STANDARD, provenance()).is_err(), "three packets");
    let mut packets = audio_block(1, 20);
    packets[2].packet_id += 1;
    assert!(protect_audio_block(&packets, &STANDARD, provenance()).is_err(), "a gap in packet ids");
    let mut packets = audio_block(1, 20);
    packets[1].pts_ticks += 1;
    assert!(protect_audio_block(&packets, &STANDARD, provenance()).is_err(), "a gap in presentation time");
    let mut packets = audio_block(1, 20);
    packets[3].payload.push(0);
    let error = protect_audio_block(&packets, &STANDARD, provenance()).unwrap_err();
    assert!(error.to_string().contains("payload length"), "unequal payload lengths: {error}");
    let mut packets = audio_block(1, 20);
    packets[0].codec = "pcm".to_string();
    assert!(protect_audio_block(&packets, &STANDARD, provenance()).is_err(), "mixed codecs");
    let mut packets = audio_block(1, 20);
    packets[2].session_id = "session-2".to_string();
    assert!(protect_audio_block(&packets, &STANDARD, provenance()).is_err(), "mixed sessions");
    let mut packets = audio_block(1, 20);
    packets[2].stream_id = "other".to_string();
    assert!(protect_audio_block(&packets, &STANDARD, provenance()).is_err(), "mixed streams");
    let mut packets = audio_block(1, 20);
    packets[1].timebase_den = 44_100;
    assert!(protect_audio_block(&packets, &STANDARD, provenance()).is_err(), "mixed timebases");
    let mut packets = audio_block(1, 20);
    packets[3].duration_ticks = 480;
    assert!(protect_audio_block(&packets, &STANDARD, provenance()).is_err(), "mixed durations");
}

#[test]
fn parity_carries_its_frames_deadline_unchanged() {
    let original = frame(40, 40, 50, 50);
    let (_, parity) = split(protect_video_frame(&original, &STANDARD, provenance()).unwrap());
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
    let parity = protect_audio_block(&packets, &STANDARD, provenance()).unwrap();
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
        let records = protect_video_frame(&original, &STANDARD, provenance()).unwrap();
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
        let records = protect_video_frame(&frame(70, n, 20, 20), &STANDARD, provenance()).unwrap();
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
        let records = protect_video_frame(&frame(71, n, 20, 20), &STANDARD, provenance()).unwrap();
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
    let records = protect_video_frame(&original, &STANDARD, provenance()).unwrap();

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
    let (data, parity) = split(protect_video_frame(&frame(90, 20, 20, 20), &policy, provenance()).unwrap());
    assert_eq!(data.len(), 20);
    assert_eq!(parity.len(), 4 + 4 + 3);
    assert_eq!(MediaFecPolicy::default(), STANDARD);
}

#[test]
fn a_policy_that_cannot_form_a_block_is_refused() {
    let zero = MediaFecPolicy { video_min_parity_shards: 0, ..STANDARD };
    assert!(protect_video_frame(&frame(1, 2, 10, 10), &zero, provenance()).is_err());
    let no_divisor = MediaFecPolicy { video_parity_divisor: 0, ..STANDARD };
    assert!(protect_video_frame(&frame(1, 2, 10, 10), &no_divisor, provenance()).is_err());
    let no_audio = MediaFecPolicy { audio_data_shards: 0, ..STANDARD };
    let error = protect_audio_block(&audio_block(1, 10), &no_audio, provenance()).unwrap_err();
    assert!(is_invalid_mentioning(&error, "non-zero"), "{error}");
    let no_audio_parity = MediaFecPolicy { audio_parity_shards: 0, ..STANDARD };
    let error = protect_audio_block(&audio_block(1, 10), &no_audio_parity, provenance()).unwrap_err();
    assert!(is_invalid_mentioning(&error, "non-zero"), "{error}");
    let too_wide = MediaFecPolicy { video_max_block_data_shards: 255, ..STANDARD };
    assert!(protect_video_frame(&frame(1, 2, 10, 10), &too_wide, provenance()).is_err());
    let audio_too_wide = MediaFecPolicy { audio_data_shards: 255, audio_parity_shards: 2, ..STANDARD };
    assert!(protect_audio_block(&audio_block(1, 10), &audio_too_wide, provenance()).is_err());
}

#[test]
fn parity_records_survive_the_positional_encoding_unchanged() {
    let (_, parity) = split(protect_video_frame(&frame(5, 5, 40, 17), &STANDARD, provenance()).unwrap());
    let audio = protect_audio_block(&audio_block(5, 25), &STANDARD, provenance()).unwrap();
    for shard in parity {
        let bytes = rmp_serde::to_vec(&shard).unwrap();
        assert_eq!(rmp_serde::from_slice::<GameCultMediaVideoParityShardRecord>(&bytes).unwrap(), shard);
    }
    for shard in audio {
        let bytes = rmp_serde::to_vec(&shard).unwrap();
        assert_eq!(rmp_serde::from_slice::<GameCultMediaAudioParityShardRecord>(&bytes).unwrap(), shard);
    }
}

// ---------------------------------------------------------------------------
// Rulings pinned: policy bounds, block identity, audio arithmetic, bursts
// ---------------------------------------------------------------------------

fn is_invalid_mentioning(error: &MediaFecError, needle: &str) -> bool {
    matches!(error, MediaFecError::Invalid(message) if message.contains(needle))
}

fn audio_run(count: u64, payload_bytes: usize) -> Vec<GameCultMediaAudioPacketRecord> {
    (0..count)
        .map(|index| GameCultMediaAudioPacketRecord {
            stream_id: "muninn.raven.av.rudp".to_string(),
            session_id: "session-1".to_string(),
            packet_id: 500 + index,
            codec: "opus".to_string(),
            pts_ticks: 960 * index as i64,
            duration_ticks: 960,
            timebase_num: 1,
            timebase_den: 48_000,
            deadline_ticks: 960 * index as i64 + 5_000,
            payload: bytes(index as u32, payload_bytes),
        })
        .collect()
}

/// A block is at most 256 shards, data and parity together, and a block of
/// exactly 256 is admitted.
#[test]
fn a_policy_admits_256_shards_per_block_and_refuses_257() {
    let audio_ok = MediaFecPolicy { audio_data_shards: 250, audio_parity_shards: 6, ..STANDARD };
    let parity = protect_audio_block(&audio_run(250, 16), &audio_ok, provenance()).unwrap();
    assert_eq!(parity.len(), 6);
    let audio_wide = MediaFecPolicy { audio_data_shards: 250, audio_parity_shards: 7, ..STANDARD };
    let error = protect_audio_block(&audio_run(250, 16), &audio_wide, provenance()).unwrap_err();
    assert!(is_invalid_mentioning(&error, "256 shards"), "{error}");

    // One video block of 250 chunks with 6 parity shards.
    let video_ok = MediaFecPolicy {
        video_max_block_data_shards: 250,
        video_min_parity_shards: 6,
        video_parity_divisor: 100,
        ..STANDARD
    };
    let records = protect_video_frame(&frame(1, 250, 4, 4), &video_ok, provenance()).unwrap();
    assert_eq!(records.len(), 256);
    let video_wide = MediaFecPolicy { video_min_parity_shards: 7, ..video_ok };
    let error = protect_video_frame(&frame(1, 250, 4, 4), &video_wide, provenance()).unwrap_err();
    assert!(is_invalid_mentioning(&error, "256 shards"), "{error}");
}

/// Two parity shards that differ only in where the block sits in the frame are
/// not one block, even when each is a well-formed record.
#[test]
fn parity_shards_that_differ_only_in_block_count_or_start_do_not_combine() {
    let (_, parity) = split(protect_video_frame(&frame(7, 32, 40, 40), &STANDARD, provenance()).unwrap());
    let block_zero: Vec<_> = parity.iter().filter(|p| p.block_index == 0).cloned().collect();

    let mut more_blocks = block_zero[1].clone();
    more_blocks.block_count += 1;
    let error = recover_video_block(&[block_zero[0].clone(), more_blocks], &[]).unwrap_err();
    assert!(is_invalid_mentioning(&error, "one block"), "block_count: {error}");

    let mut shifted = block_zero[1].clone();
    shifted.block_data_start += 1;
    let error = recover_video_block(&[block_zero[0].clone(), shifted], &[]).unwrap_err();
    assert!(is_invalid_mentioning(&error, "one block"), "block_data_start: {error}");
}

/// The largest packet id and pts a block may end on are admitted and recover
/// to exactly those values; one tick or id more is refused everywhere: at the
/// wire, in recovery, and at the producer.
#[test]
fn an_audio_block_that_would_overflow_its_last_id_or_pts_is_refused() {
    // Id: the block's last packet is u64::MAX.
    let base = u64::MAX - 3;
    let packets = audio_block(base, 20);
    let parity = protect_audio_block(&packets, &STANDARD, provenance()).unwrap();
    let recovered = recover_audio_block(&parity, &packets[..3]).unwrap();
    assert_eq!(recovered.len(), 1);
    assert_eq!(recovered[0].packet_id, u64::MAX);
    assert_eq!(recovered[0], packets[3]);

    let mut past_end = parity.clone();
    for shard in &mut past_end {
        shard.base_packet_id += 1;
    }
    let error = recover_audio_block(&past_end, &[]).unwrap_err();
    assert!(is_invalid_mentioning(&error, "packet id or pts range"), "{error}");
    let wire = encode_media_wire_record(
        &GameCultMediaWireRecord::AudioParity(past_end[0].clone()),
        provenance(),
    )
    .unwrap();
    assert!(decode_media_wire_record(&wire).unwrap_err().to_string().contains("packet id or pts range"));
    let mut overflowing = audio_block(base, 20);
    overflowing[0].packet_id += 1;
    assert!(protect_audio_block(&overflowing, &STANDARD, provenance()).is_err());

    // Pts: the block's last packet ends on i64::MAX.
    let mut packets = audio_block(9, 20);
    let first_pts = i64::MAX - 3 * 960;
    for (index, packet) in packets.iter_mut().enumerate() {
        packet.pts_ticks = first_pts + 960 * index as i64;
        packet.deadline_ticks = i64::MAX;
    }
    let parity = protect_audio_block(&packets, &STANDARD, provenance()).unwrap();
    let recovered = recover_audio_block(&parity, &packets[..3]).unwrap();
    assert_eq!(recovered[0].pts_ticks, i64::MAX);
    assert_eq!(recovered[0], packets[3]);

    let mut past_end = parity.clone();
    for shard in &mut past_end {
        shard.base_pts_ticks += 1;
    }
    let error = recover_audio_block(&past_end, &[]).unwrap_err();
    assert!(is_invalid_mentioning(&error, "packet id or pts range"), "{error}");
    let mut overflowing = packets.clone();
    overflowing[1].pts_ticks += 1;
    assert!(protect_audio_block(&overflowing, &STANDARD, provenance()).is_err());
}

/// Sixteen 8-byte chunks, the widest standard video block (m = 4).
/// `payload[i][j] = (i * 29 + j * 13 + 5) mod 256`. The expected bytes come
/// from an independent implementation of the construction in the module docs.
#[test]
fn known_answer_video_parity_bytes_for_k16_m4() {
    let mut chunks = frame(2, 16, 8, 8);
    for chunk in &mut chunks {
        let i = usize::from(chunk.chunk_index);
        chunk.payload = (0..8).map(|j| (i * 29 + j * 13 + 5) as u8).collect();
    }
    let (_, parity) = split(protect_video_frame(&chunks, &STANDARD, provenance()).unwrap());
    let hexes: Vec<String> = parity.iter().map(|shard| hex(&shard.payload)).collect();
    assert_eq!(
        hexes,
        ["5ed2f608c328adbe", "e46d011eabb600e2", "7c1168f2ba8a2343", "4f03d220d182f232"]
    );
}

/// Blocks interleave, so a burst of consecutive losses costs each block a
/// share: the longest burst that leaves every frame whole, wherever it starts,
/// is the block count times the smallest parity count among the blocks.
#[test]
fn the_worst_burst_that_always_recovers_is_blocks_times_the_smallest_m() {
    for chunk_count in 1..=399_u16 {
        let sizes = STANDARD.video_block_sizes(chunk_count);
        let parity_of: Vec<usize> =
            sizes.iter().map(|&size| usize::from(STANDARD.video_parity_shards(size))).collect();
        let records = protect_video_frame(&frame(u64::from(chunk_count), chunk_count, 1, 1), &STANDARD, provenance())
            .unwrap();
        let blocks: Vec<usize> = records.iter().map(|record| block_of(record, &sizes)).collect();

        // The shortest burst, over every start, that costs some block more
        // than its m; the burst one shorter always recovers.
        let mut shortest_failing = usize::MAX;
        for start in 0..blocks.len() {
            let mut lost = vec![0_usize; sizes.len()];
            for (offset, &block) in blocks[start..].iter().enumerate() {
                lost[block] += 1;
                if lost[block] > parity_of[block] {
                    shortest_failing = shortest_failing.min(offset + 1);
                    break;
                }
            }
        }
        let smallest_m = *parity_of.iter().min().unwrap();
        assert_eq!(
            shortest_failing - 1,
            sizes.len() * smallest_m,
            "{chunk_count} chunks in {} blocks",
            sizes.len()
        );
    }
}

// ---------------------------------------------------------------------------
// The ceiling, and what the audio budget check relies on
// ---------------------------------------------------------------------------

/// Only the parity is measured against the budget, on the claim that it is the
/// largest record of an audio block. It carries every field a packet does plus
/// the stripe geometry, so this holds across the id, pts, duration and deadline
/// widths that vary the encoding.
#[test]
fn an_audio_parity_record_is_larger_on_the_wire_than_every_packet_it_protects() {
    let bases: [u64; 8] = [0, 0x7c, 0xfc, 0xfffc, 0xffff_fffc, 99_997, u64::MAX - 3, 1];
    let starts: [i64; 7] = [i64::MIN, -1, 0, 125, 65_530, 4_294_967_290, i64::MAX - 3 * 4_000_000_000];
    let durations: [u32; 5] = [1, 120, 960, 65_535, 4_000_000_000];
    for base in bases {
        for start in starts {
            for duration in durations {
                for wide_deadline in [false, true] {
                    let mut packets = audio_block(base, 40);
                    for (index, packet) in packets.iter_mut().enumerate() {
                        packet.pts_ticks = start + i64::from(duration) * index as i64;
                        packet.duration_ticks = duration;
                        packet.deadline_ticks = if wide_deadline { i64::MAX } else { packet.pts_ticks };
                    }
                    let parity = protect_audio_block(&packets, &STANDARD, provenance()).unwrap();
                    let parity_len = wire_len(&GameCultMediaWireRecord::AudioParity(parity[0].clone()), provenance());
                    for packet in &packets {
                        let packet_len = wire_len(&GameCultMediaWireRecord::Audio(packet.clone()), provenance());
                        assert!(
                            packet_len < parity_len,
                            "base {base} start {start} duration {duration}: packet {packet_len} >= parity {parity_len}"
                        );
                    }
                }
            }
        }
    }
}

/// A parity whose deadline precedes its last packet's pts would recover a packet
/// that fails `validate_audio_record`. Recovery refuses it, and so does the wire.
#[test]
fn audio_parity_with_a_deadline_before_its_last_packet_recovers_nothing() {
    let packets = audio_block(10, 8);
    let mut parity = protect_audio_block(&packets, &STANDARD, provenance()).unwrap();
    for shard in &mut parity {
        shard.deadline_ticks = shard.base_pts_ticks;
    }
    let error = recover_audio_block(&parity, &packets[..2]).unwrap_err();
    assert!(is_invalid_mentioning(&error, "last pts_ticks"), "{error}");
    let wire = encode_media_wire_record(
        &GameCultMediaWireRecord::AudioParity(parity[0].clone()),
        provenance(),
    )
    .unwrap();
    assert!(decode_media_wire_record(&wire).is_err());
}

/// The datagram budget is the producer's, but no budget may promise a record the
/// decoder would refuse.
#[test]
fn a_datagram_budget_over_the_media_record_ceiling_is_refused() {
    let over = MediaFecPolicy { max_wire_bytes: GAMECULT_MEDIA_MAX_WIRE_BYTES + 1, ..STANDARD };
    let error = protect_audio_block(&audio_block(1, 100), &over, provenance()).unwrap_err();
    assert!(is_invalid_mentioning(&error, "ceiling"), "{error}");
    let error = protect_video_frame(&frame(2, 3, 100, 100), &over, provenance()).unwrap_err();
    assert!(is_invalid_mentioning(&error, "ceiling"), "{error}");
}

/// A budget at the ceiling admits shards up to it, and the decoder accepts
/// what the policy admitted.
#[test]
fn a_budget_at_the_media_record_ceiling_admits_large_shards_end_to_end() {
    let at_ceiling = MediaFecPolicy { max_wire_bytes: GAMECULT_MEDIA_MAX_WIRE_BYTES, ..STANDARD };
    let records =
        protect_video_frame(&frame(2, 3, 60_000, 60_000), &at_ceiling, provenance()).unwrap();
    for record in &records {
        let wire = encode_media_wire_record(record, provenance()).unwrap();
        assert!(wire.len() <= GAMECULT_MEDIA_MAX_WIRE_BYTES);
        decode_media_wire_record(&wire).unwrap();
    }
    let parity = protect_audio_block(&audio_block(1, 60_000), &at_ceiling, provenance()).unwrap();
    let wire = encode_media_wire_record(
        &GameCultMediaWireRecord::AudioParity(parity[0].clone()),
        provenance(),
    )
    .unwrap();
    decode_media_wire_record(&wire).unwrap();
}

// ---------------------------------------------------------------------------
// Known-answer generator: reed-solomon-erasure 6.0.0 is the reference that
// produced `fixtures/media_fec_rs_gf256_v1.kat`. It runs once, before the
// crate is replaced by the owned construction, and is deleted with the crate.
// ---------------------------------------------------------------------------

/// The data of a fixture case, as the fixture's header defines it.
fn kat_data(k: usize, m: usize, shard_bytes: usize, fill: &str) -> Vec<Vec<u8>> {
    (0..k)
        .map(|i| match fill {
            "lcg" => bytes((k * 65_536 + m * 256 + i) as u32, shard_bytes),
            "zero" => vec![0; shard_bytes],
            "ff" => vec![0xff; shard_bytes],
            other => panic!("unknown fill {other}"),
        })
        .collect()
}

/// Erasure patterns of exactly `min(m, k)`-ish shards, each leaving at least
/// one parity shard present and at least one data shard missing.
fn kat_erasures(k: usize, m: usize) -> Vec<Vec<usize>> {
    let mut patterns = Vec::new();
    let d = m.min(k);
    patterns.push((0..d).collect()); // data from the front
    patterns.push((k - d..k).collect()); // data from the back
    let spread = m.div_ceil(2).min(k);
    let mut mixed: Vec<usize> = (0..spread).map(|s| s * k / spread).collect();
    mixed.extend(k..k + (m - spread));
    patterns.push(mixed); // data spread across the block, parity from the front
    let mut one: Vec<usize> = vec![k / 2];
    one.extend(k..k + (m - 1));
    patterns.push(one); // one data shard, every parity shard but the last
    patterns.sort();
    patterns.dedup();
    patterns
}

#[test]
#[ignore = "run once to write the rs-gf256-v1 fixture from the reference crate"]
fn generate_rs_gf256_v1_known_answers() {
    use reed_solomon_erasure::galois_8::ReedSolomon;
    let mut cases: Vec<(usize, usize, usize, &str)> = Vec::new();
    // Every standard video block geometry, k = 1..=16, m = max(2, ceil(k / 4)).
    for k in 1..=16_u16 {
        cases.push((usize::from(k), usize::from(STANDARD.video_parity_shards(k)), 37, "lcg"));
    }
    // Standard audio, 4 + 2, at 1 byte, an odd length and a full datagram.
    cases.extend([(4, 2, 1, "lcg"), (4, 2, 37, "lcg"), (4, 2, 1300, "lcg")]);
    // The widest standard video block at a full datagram, and a long shard.
    cases.extend([(16, 4, 1300, "lcg"), (3, 2, 4000, "lcg")]);
    // Degenerate data.
    cases.extend([(4, 2, 16, "zero"), (16, 4, 16, "ff")]);
    // The policy's edges: one shard each side, and 256 shards split every way.
    cases.extend([
        (1, 1, 1, "lcg"),
        (1, 1, 9, "lcg"),
        (2, 1, 5, "lcg"),
        (255, 1, 1, "lcg"),
        (255, 1, 3, "lcg"),
        (1, 255, 1, "lcg"),
        (1, 255, 4, "lcg"),
        (2, 254, 2, "lcg"),
        (128, 128, 1, "lcg"),
        (128, 128, 4, "lcg"),
        (200, 56, 2, "lcg"),
        (250, 6, 5, "lcg"),
    ]);

    let mut out = String::new();
    for (k, m, shard_bytes, fill) in cases {
        let data = kat_data(k, m, shard_bytes, fill);
        let code = ReedSolomon::new(k, m).unwrap();
        let mut parity = vec![vec![0_u8; shard_bytes]; m];
        code.encode_sep(&data, &mut parity).unwrap();
        out.push_str(&format!("case {k} {m} {shard_bytes} {fill}\n"));
        for (p, shard) in parity.iter().enumerate() {
            out.push_str(&format!("parity {p} {}\n", hex(shard)));
        }
        for pattern in kat_erasures(k, m) {
            let mut shards: Vec<Option<Vec<u8>>> =
                data.iter().chain(parity.iter()).cloned().map(Some).collect();
            for &slot in &pattern {
                shards[slot] = None;
            }
            code.reconstruct_data(&mut shards).unwrap();
            for (i, shard) in shards.iter().take(k).enumerate() {
                assert_eq!(shard.as_ref().unwrap(), &data[i], "k={k} m={m} {pattern:?}");
            }
            let list: Vec<String> = pattern.iter().map(usize::to_string).collect();
            out.push_str(&format!("erase {}\n", list.join(",")));
        }
    }
    println!("BEGIN-KAT\n{out}END-KAT");
}
