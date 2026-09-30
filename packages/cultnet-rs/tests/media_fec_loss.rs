//! Video frames protected by the media FEC codec, sent through the impairment
//! schedules `cultnet_impair` applies to real sockets, with no sockets: each
//! record of a frame in send order takes the next datagram decision, and what
//! survives is handed to `recover_video_block`.
//!
//! Two kinds of claim. The guarantee is asserted: a block recovers exactly when
//! at most `m` of its records were lost, and a burst short enough that the send
//! order spreads it under `m` per block always leaves every frame whole. The
//! recovered-frame ratios are evidence, not assertions; `--nocapture` prints the
//! table and `tests/impairment/loss-matrix.txt` is that output (200 frames per cell, seed 20260930).

#[allow(dead_code)]
#[path = "../examples/cultnet_impair/policy.rs"]
mod policy;

use std::fmt::Write as _;
use std::path::Path;

use cultnet_rs::{
    GameCultMediaVideoAccessUnitRecord, GameCultMediaWireRecord, MediaFecError, MediaFecPolicy,
    MediaWireProvenance, protect_video_frame, recover_video_block,
};
use policy::{Profile, Scheduler};

const POLICY: MediaFecPolicy = MediaFecPolicy::STANDARD;
const SEED: u64 = 20_260_930;
const FRAMES: u64 = 200;

fn provenance() -> MediaWireProvenance<'static> {
    MediaWireProvenance {
        stored_at: "unix:1700000000000",
        runtime_id: "raven-muninn-primary",
        role: "muninn.media",
        producer: "muninn",
    }
}

fn frame(frame_id: u64, chunk_count: u16) -> Vec<GameCultMediaVideoAccessUnitRecord> {
    (0..chunk_count)
        .map(|index| GameCultMediaVideoAccessUnitRecord {
            stream_id: "muninn.raven.av.rudp".to_string(),
            session_id: "session-1".to_string(),
            frame_id,
            codec: "h264".to_string(),
            pts_ticks: frame_id as i64 * 3_000,
            duration_ticks: 3_000,
            timebase_num: 1,
            timebase_den: 90_000,
            keyframe: false,
            dependency_frame_id: frame_id.checked_sub(1),
            deadline_ticks: frame_id as i64 * 3_000 + 22_500,
            chunk_index: index,
            chunk_count,
            payload: (0..if index + 1 == chunk_count { 60 } else { 100 })
                .map(|byte| (frame_id as usize * 31 + usize::from(index) * 7 + byte) as u8)
                .collect(),
        })
        .collect()
}

#[derive(Default)]
struct Tally {
    frames: u64,
    whole_without_parity: u64,
    whole_with_parity: u64,
    blocks_recovered: u64,
    blocks_beyond_repair: u64,
}

/// Runs one shape through one profile's schedule and asserts the per-block
/// guarantee on every block of every frame.
fn run(profile: &Profile, chunk_count: u16) -> Tally {
    let mut scheduler = Scheduler::new(profile.clone(), SEED);
    let sizes = POLICY.video_block_sizes(chunk_count);
    let mut tally = Tally::default();

    for frame_id in 0..FRAMES {
        let original = frame(frame_id, chunk_count);
        let records = protect_video_frame(&original, &POLICY, provenance()).unwrap();
        let survivors: Vec<GameCultMediaWireRecord> = records
            .into_iter()
            .filter(|_| !scheduler.decide(0).drop)
            .collect();

        let mut whole = true;
        let mut whole_without_parity = true;
        let mut start = 0_u16;
        for (block, &size) in sizes.iter().enumerate() {
            let m = usize::from(POLICY.video_parity_shards(size));
            let mut data = Vec::new();
            let mut parity = Vec::new();
            for record in &survivors {
                match record {
                    GameCultMediaWireRecord::Video(chunk)
                        if chunk.chunk_index >= start && chunk.chunk_index < start + size =>
                    {
                        data.push(chunk.clone())
                    }
                    GameCultMediaWireRecord::VideoParity(shard) if usize::from(shard.block_index) == block => {
                        parity.push(shard.clone())
                    }
                    _ => {}
                }
            }
            let lost = usize::from(size) + m - data.len() - parity.len();
            let missing_data = usize::from(size) - data.len();
            whole_without_parity &= missing_data == 0;

            let block_whole = if missing_data == 0 {
                true
            } else if parity.is_empty() {
                assert!(lost > m, "no parity survived, so more than m shards were lost");
                false
            } else {
                match recover_video_block(&parity, &data) {
                    Ok(recovered) => {
                        assert!(lost <= m, "block {block} of frame {frame_id} recovered {lost} losses with m = {m}");
                        assert_eq!(recovered.len(), missing_data);
                        for chunk in &recovered {
                            assert_eq!(
                                chunk, &original[usize::from(chunk.chunk_index)],
                                "a recovered chunk must be the chunk that was sent"
                            );
                        }
                        tally.blocks_recovered += 1;
                        true
                    }
                    Err(MediaFecError::BeyondRepair { .. }) => {
                        assert!(lost > m, "block {block} of frame {frame_id} refused a recoverable {lost} losses with m = {m}");
                        tally.blocks_beyond_repair += 1;
                        false
                    }
                    Err(other) => panic!("{other}"),
                }
            };
            whole &= block_whole;
            start += size;
        }
        tally.frames += 1;
        tally.whole_with_parity += u64::from(whole);
        tally.whole_without_parity += u64::from(whole_without_parity);
        assert!(whole || !whole_without_parity, "parity never makes a whole frame worse");
    }
    tally
}

fn profile(name: &str) -> Profile {
    Profile::load(&Path::new(env!("CARGO_MANIFEST_DIR")).join(format!("tests/impairment/{name}.toml")))
        .unwrap()
}

/// Every shape here splits into equal blocks, so a burst of `L` consecutive
/// datagrams costs each of `B` blocks at most `ceil(L / B)` of its records.
const EQUAL_BLOCK_SHAPES: [u16; 5] = [8, 16, 24, 32, 48];

#[test]
fn the_loss_matrix() {
    let cells = ["clean", "loss-1pct", "loss-3pct", "loss-5pct", "burst-4", "burst-8"];
    let shapes = [8_u16, 16, 17, 24, 32, 48];
    let mut table = String::new();
    writeln!(
        table,
        "{:<10} {:>6} {:>7} {:>13} {:>12} {:>10} {:>9}",
        "profile", "chunks", "frames", "whole/no-fec", "whole/fec", "recovered", "beyond"
    )
    .unwrap();

    for cell in cells {
        let loaded = profile(cell);
        for &chunks in &shapes {
            let tally = run(&loaded, chunks);
            writeln!(
                table,
                "{:<10} {:>6} {:>7} {:>13} {:>12} {:>10} {:>9}",
                cell,
                chunks,
                tally.frames,
                tally.whole_without_parity,
                tally.whole_with_parity,
                tally.blocks_recovered,
                tally.blocks_beyond_repair
            )
            .unwrap();

            if cell == "clean" {
                assert_eq!(tally.whole_with_parity, tally.frames, "a clean link loses nothing");
            }
            let burst = match cell {
                "burst-4" => 4_usize,
                "burst-8" => 8,
                _ => continue,
            };
            if EQUAL_BLOCK_SHAPES.contains(&chunks) {
                let sizes = POLICY.video_block_sizes(chunks);
                let per_block = burst.div_ceil(sizes.len());
                let m = usize::from(POLICY.video_parity_shards(sizes[0]));
                if per_block <= m {
                    assert_eq!(
                        tally.whole_with_parity, tally.frames,
                        "{cell} over {chunks} chunks costs each of {} blocks at most {per_block} of m = {m}: no frame may be lost",
                        sizes.len()
                    );
                }
            }
        }
    }
    println!("{table}");
}
