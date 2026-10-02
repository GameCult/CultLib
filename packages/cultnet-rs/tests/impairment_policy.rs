//! The impairment policy is deterministic, or a loss experiment cannot be
//! replayed. These pin each decision the proxy example makes, against the same
//! `policy.rs` the example compiles.

#[allow(dead_code)]
#[path = "../examples/cultnet_impair/policy.rs"]
mod policy;

use std::net::SocketAddr;

use policy::{Direction, Profile, Scheduler};

fn client() -> SocketAddr {
    "127.0.0.1:1234".parse().unwrap()
}

#[test]
fn iid_decisions_are_seed_reproducible() {
    let profile = Profile {
        loss_basis_points: 2500,
        ..Default::default()
    };
    let mut a = Scheduler::new(profile.clone(), 42);
    let mut b = Scheduler::new(profile, 42);
    let left: Vec<_> = (0..100).map(|_| a.decide(0)).collect();
    let right: Vec<_> = (0..100).map(|_| b.decide(0)).collect();
    assert_eq!(left, right);
    assert!(left.iter().any(|d| d.drop));
    assert!(left.iter().any(|d| !d.drop));
}

#[test]
fn burst_drop_is_exact() {
    let profile = Profile {
        burst_every: 5,
        burst_length: 3,
        ..Default::default()
    };
    let mut scheduler = Scheduler::new(profile, 1);
    let drops: Vec<_> = (1..=9).filter(|_| scheduler.decide(0).drop).collect();
    assert_eq!(drops, vec![5, 6, 7], "a burst starts on the Nth datagram and runs burst_length");
}

#[test]
fn duplicate_delay_and_reorder_are_deterministic() {
    let profile = Profile {
        duplicate_every: 2,
        reorder_every: 3,
        reorder_delay_ms: 20,
        delay_ms: 5,
        ..Default::default()
    };
    let mut scheduler = Scheduler::new(profile, 1);
    assert_eq!(scheduler.decide(0).copies, 1);
    assert_eq!(scheduler.decide(0).copies, 2);
    let third = scheduler.decide(0);
    assert!(third.reordered);
    assert_eq!(third.delay_ms, 25);
}

#[test]
fn stall_drops_only_inside_window() {
    let profile = Profile {
        stall_at_ms: 100,
        stall_for_ms: 50,
        ..Default::default()
    };
    let mut scheduler = Scheduler::new(profile, 1);
    assert!(!scheduler.decide(99).drop);
    let stalled = scheduler.decide(100);
    assert!(stalled.drop && stalled.stalled);
    assert!(scheduler.decide(149).drop);
    assert!(!scheduler.decide(150).drop);
}

#[test]
fn reorder_delay_places_packet_behind_later_packet() {
    let profile = Profile {
        reorder_every: 2,
        reorder_delay_ms: 20,
        ..Default::default()
    };
    let mut scheduler = Scheduler::new(profile, 1);
    scheduler.admit(0, Direction::ClientToUpstream, b"first", client());
    scheduler.admit(0, Direction::ClientToUpstream, b"held", client());
    scheduler.admit(1, Direction::ClientToUpstream, b"later", client());
    assert_eq!(scheduler.pop_due(1).unwrap().bytes, b"first");
    assert_eq!(scheduler.pop_due(1).unwrap().bytes, b"later");
    assert!(scheduler.pop_due(19).is_none());
    assert_eq!(scheduler.pop_due(20).unwrap().bytes, b"held");
}

#[test]
fn scheduled_queue_is_bounded_and_counts_overflow() {
    let profile = Profile {
        delay_ms: 100,
        max_scheduled: 2,
        ..Default::default()
    };
    let mut scheduler = Scheduler::new(profile, 1);
    for value in 0..5 {
        scheduler.admit(0, Direction::ClientToUpstream, &[value], client());
    }
    assert_eq!(scheduler.queue.len(), 2);
    assert_eq!(scheduler.stats.max_queue, 2);
    assert_eq!(scheduler.stats.queue_overflow, 3);
    assert_eq!(scheduler.stats.dropped, 3);
}

fn profile_path(name: &str) -> std::path::PathBuf {
    std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("tests/impairment")
        .join(format!("{name}.toml"))
}

#[test]
fn every_committed_profile_loads_with_its_named_meaning() {
    let load = |name: &str| Profile::load(&profile_path(name)).unwrap_or_else(|e| panic!("{name}: {e}"));
    assert_eq!(load("clean"), Profile::default());
    assert_eq!(load("loss-1pct").loss_basis_points, 100);
    assert_eq!(load("loss-3pct").loss_basis_points, 300);
    assert_eq!(load("loss-5pct").loss_basis_points, 500);
    let burst4 = load("burst-4");
    assert_eq!((burst4.burst_every, burst4.burst_length), (200, 4));
    let burst8 = load("burst-8");
    assert_eq!((burst8.burst_every, burst8.burst_length), (200, 8));
    let jitter = load("delay-jitter");
    assert_eq!((jitter.delay_ms, jitter.jitter_ms), (5, 10));
    assert_eq!(load("duplicate").duplicate_every, 50);
    let reorder = load("reorder");
    assert_eq!((reorder.reorder_every, reorder.reorder_delay_ms), (20, 12));
    let stall = load("stall-250ms");
    assert_eq!((stall.stall_at_ms, stall.stall_for_ms), (10_000, 250));
}

#[test]
fn a_malformed_profile_is_refused_with_its_line() {
    let dir = tempfile::tempdir().unwrap();
    let write = |name: &str, text: &str| {
        let path = dir.path().join(name);
        std::fs::write(&path, text).unwrap();
        Profile::load(&path).unwrap_err()
    };
    assert!(write("unknown.toml", "jitter = 3
").contains("unknown profile key jitter"));
    assert!(write("word.toml", "delay_ms = soon
").contains("unsigned integer"));
    assert!(write("shape.toml", "delay_ms
").contains("key = value"));
    assert!(write("loss.toml", "loss_basis_points = 10001
").contains("<= 10000"));
    assert!(write("queue.toml", "max_scheduled = 0
").contains("greater than zero"));
    assert!(write("line.toml", "

delay_ms = x
").contains(":3:"));
}

#[test]
fn admission_counts_what_it_did_to_each_datagram() {
    let mut scheduler = Scheduler::new(
        Profile { duplicate_every: 2, reorder_every: 3, reorder_delay_ms: 10, ..Default::default() },
        1,
    );
    for value in 0..6 {
        scheduler.admit(0, Direction::ClientToUpstream, &[value], client());
    }
    // Datagrams 2, 4 and 6 are duplicated, 3 and 6 are reordered (6 is both).
    assert_eq!(scheduler.stats.received, 6);
    assert_eq!(scheduler.stats.duplicated, 3);
    assert_eq!(scheduler.stats.reordered, 2);
    assert_eq!(scheduler.queue.len(), 9);

    let mut stalled = Scheduler::new(Profile { stall_at_ms: 0, stall_for_ms: 10, ..Default::default() }, 1);
    stalled.admit(5, Direction::UpstreamToClient, b"x", client());
    assert_eq!((stalled.stats.stalled, stalled.stats.dropped, stalled.queue.len()), (1, 1, 0));
}

#[test]
fn jitter_is_seeded_uniform_within_its_bound() {
    let profile = Profile { delay_ms: 7, jitter_ms: 5, ..Default::default() };
    let mut scheduler = Scheduler::new(profile, 9);
    let delays: Vec<u64> = (0..300).map(|_| scheduler.decide(0).delay_ms).collect();
    assert!(delays.iter().all(|delay| (7..=12).contains(delay)), "{delays:?}");
    for delay in 7..=12 {
        assert!(delays.contains(&delay), "delay {delay} never drawn");
    }
}

#[test]
fn the_loss_ceiling_is_inclusive() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("all.toml");
    std::fs::write(&path, "loss_basis_points = 10000\n").unwrap();
    let profile = Profile::load(&path).unwrap();
    let mut scheduler = Scheduler::new(profile, 1);
    assert!((0..50).all(|_| scheduler.decide(0).drop), "10000 basis points drops everything");
}

/// The seeded stream is part of the contract: the same seed must drop the same
/// datagrams on every run, on every host. 100,000 draws of xorshift64 from seed
/// 42, taken modulo 10,000 and compared against 100, give 1,008.
#[test]
fn seeded_loss_drops_the_exact_datagrams_the_stream_dictates() {
    let mut scheduler = Scheduler::new(Profile { loss_basis_points: 100, ..Default::default() }, 42);
    let dropped = (0..100_000).filter(|_| scheduler.decide(0).drop).count();
    assert_eq!(dropped, 1_008);
}

#[test]
fn datagrams_due_together_leave_in_arrival_order() {
    let mut scheduler = Scheduler::new(Profile { delay_ms: 5, ..Default::default() }, 1);
    for value in 0..4_u8 {
        scheduler.admit(0, Direction::ClientToUpstream, &[value], client());
    }
    let order: Vec<u8> = std::iter::from_fn(|| scheduler.pop_due(5)).map(|item| item.bytes[0]).collect();
    assert_eq!(order, vec![0, 1, 2, 3]);
}
