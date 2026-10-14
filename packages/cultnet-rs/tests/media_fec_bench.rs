//! Timings for the media FEC codec, without a bench framework.
//!
//! Ignored by default; run in release, where the numbers mean something:
//!
//! ```text
//! cargo test --release --test media_fec_bench -- --ignored --nocapture
//! ```
//!
//! Each line is nanoseconds per call through the public surface, so record
//! validation and cloning are included. The geometries are the ones the codec
//! meets: standard audio, the widest standard video block at a datagram, and
//! the worst recovery the policy admits (255 + 1).

use std::hint::black_box;
use std::time::Instant;

use cultnet_rs::{
    GAMECULT_MEDIA_MAX_WIRE_BYTES, GameCultMediaAudioPacketRecord, MediaFecPolicy,
    MediaWireProvenance, protect_audio_block, recover_audio_block,
};

fn provenance() -> MediaWireProvenance<'static> {
    MediaWireProvenance {
        stored_at: "unix:1700000000000",
        runtime_id: "bench",
        role: "bench.media",
        producer: "bench",
    }
}

fn packets(k: usize, len: usize) -> Vec<GameCultMediaAudioPacketRecord> {
    let mut state = 0x2545_f491_u32;
    (0..k as u64)
        .map(|index| GameCultMediaAudioPacketRecord {
            stream_id: "bench".to_string(),
            session_id: "bench".to_string(),
            packet_id: index,
            codec: "opus".to_string(),
            pts_ticks: 960 * index as i64,
            duration_ticks: 960,
            timebase_num: 1,
            timebase_den: 48_000,
            deadline_ticks: 960 * index as i64 + 5_000,
            payload: (0..len)
                .map(|_| {
                    state ^= state << 13;
                    state ^= state >> 17;
                    state ^= state << 5;
                    state as u8
                })
                .collect(),
        })
        .collect()
}

/// Mean nanoseconds per call over at least `min_ms`, after three warm-up calls.
fn time(mut f: impl FnMut(), min_ms: u128) -> f64 {
    for _ in 0..3 {
        f();
    }
    let start = Instant::now();
    let mut calls = 0_u64;
    while start.elapsed().as_millis() < min_ms {
        f();
        calls += 1;
    }
    start.elapsed().as_nanos() as f64 / calls as f64
}

#[test]
#[ignore = "timing, not a test: run in release with --ignored --nocapture"]
fn media_fec_timings() {
    for (k, m, len) in [(4, 2, 160), (4, 2, 1300), (16, 4, 1200), (255, 1, 1300)] {
        let policy = MediaFecPolicy {
            audio_data_shards: k as u16,
            audio_parity_shards: m as u16,
            max_wire_bytes: GAMECULT_MEDIA_MAX_WIRE_BYTES,
            ..MediaFecPolicy::STANDARD
        };
        let block = packets(k, len);
        let encode = time(|| drop(black_box(protect_audio_block(black_box(&block), &policy, provenance()))), 300);
        let parity = protect_audio_block(&block, &policy, provenance()).unwrap();
        let lost = m.min(k);
        let survivors = &block[lost..];
        let decode = time(|| drop(black_box(recover_audio_block(black_box(&parity), survivors))), 300);
        println!("k={k:3} m={m:3} len={len:5}: protect {encode:12.0} ns | recover {lost} lost {decode:12.0} ns");
    }
}
