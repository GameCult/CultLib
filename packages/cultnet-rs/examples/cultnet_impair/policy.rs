//! The impairment policy: what happens to each datagram, and nothing about
//! sockets. `Profile` says what to do, `Scheduler` decides per datagram and
//! holds the delayed ones, and every decision is a pure function of the
//! profile, the seed and the datagram's position, so a schedule replays
//! exactly. The proxy in `main.rs` and the loss-matrix test in
//! `tests/media_fec_loss.rs` both take their schedule from here.

use std::collections::VecDeque;
use std::fs;
use std::net::SocketAddr;
use std::path::Path;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Profile {
    pub loss_basis_points: u32,
    pub burst_every: u64,
    pub burst_length: u64,
    pub duplicate_every: u64,
    pub reorder_every: u64,
    pub reorder_delay_ms: u64,
    pub delay_ms: u64,
    pub jitter_ms: u64,
    pub stall_at_ms: u64,
    pub stall_for_ms: u64,
    pub max_scheduled: usize,
}

impl Default for Profile {
    fn default() -> Self {
        Self {
            loss_basis_points: 0,
            burst_every: 0,
            burst_length: 0,
            duplicate_every: 0,
            reorder_every: 0,
            reorder_delay_ms: 0,
            delay_ms: 0,
            jitter_ms: 0,
            stall_at_ms: 0,
            stall_for_ms: 0,
            max_scheduled: 4096,
        }
    }
}

impl Profile {
    pub fn load(path: &Path) -> Result<Self, String> {
        let text = fs::read_to_string(path)
            .map_err(|error| format!("reading {}: {error}", path.display()))?;
        let mut profile = Self::default();
        for (index, raw) in text.lines().enumerate() {
            let line = raw.split('#').next().unwrap_or("").trim();
            if line.is_empty() || line.starts_with('[') {
                continue;
            }
            let (key, value) = line
                .split_once('=')
                .ok_or_else(|| format!("{}:{}: expected key = value", path.display(), index + 1))?;
            let key = key.trim();
            let value = value.trim().trim_matches('"');
            let number = value.parse::<u64>().map_err(|_| {
                format!(
                    "{}:{}: {key} must be an unsigned integer",
                    path.display(),
                    index + 1
                )
            })?;
            match key {
                "loss_basis_points" => {
                    profile.loss_basis_points = number
                        .try_into()
                        .map_err(|_| "loss_basis_points is too large")?
                }
                "burst_every" => profile.burst_every = number,
                "burst_length" => profile.burst_length = number,
                "duplicate_every" => profile.duplicate_every = number,
                "reorder_every" => profile.reorder_every = number,
                "reorder_delay_ms" => profile.reorder_delay_ms = number,
                "delay_ms" => profile.delay_ms = number,
                "jitter_ms" => profile.jitter_ms = number,
                "stall_at_ms" => profile.stall_at_ms = number,
                "stall_for_ms" => profile.stall_for_ms = number,
                "max_scheduled" => {
                    profile.max_scheduled = number
                        .try_into()
                        .map_err(|_| "max_scheduled is too large")?
                }
                _ => {
                    return Err(format!(
                        "{}:{}: unknown profile key {key}",
                        path.display(),
                        index + 1
                    ));
                }
            }
        }
        if profile.loss_basis_points > 10_000 {
            return Err("loss_basis_points must be <= 10000".into());
        }
        if profile.max_scheduled == 0 {
            return Err("max_scheduled must be greater than zero".into());
        }
        Ok(profile)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Direction {
    ClientToUpstream,
    UpstreamToClient,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Decision {
    pub drop: bool,
    pub copies: u8,
    pub delay_ms: u64,
    pub reordered: bool,
    pub stalled: bool,
}

#[derive(Clone, Debug)]
pub struct DeterministicRng(u64);

impl DeterministicRng {
    pub fn new(seed: u64) -> Self {
        Self(if seed == 0 { 0x9e3779b97f4a7c15 } else { seed })
    }
    pub fn next(&mut self) -> u64 {
        let mut x = self.0;
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        self.0 = x;
        x
    }
}

#[derive(Debug)]
pub struct Scheduler {
    pub profile: Profile,
    pub rng: DeterministicRng,
    pub seen: u64,
    pub burst_remaining: u64,
    pub queue: VecDeque<Scheduled>,
    pub stats: Stats,
}

#[derive(Debug)]
pub struct Scheduled {
    pub due_ms: u64,
    pub direction: Direction,
    pub bytes: Vec<u8>,
    pub client: SocketAddr,
}

#[derive(Debug, Default)]
pub struct Stats {
    pub received: u64,
    pub forwarded: u64,
    pub dropped: u64,
    pub duplicated: u64,
    pub reordered: u64,
    pub stalled: u64,
    pub queue_overflow: u64,
    pub max_queue: usize,
    pub flow_evicted: u64,
    pub max_flows: usize,
}

impl Scheduler {
    pub fn new(profile: Profile, seed: u64) -> Self {
        Self {
            profile,
            rng: DeterministicRng::new(seed),
            seen: 0,
            burst_remaining: 0,
            queue: VecDeque::new(),
            stats: Stats::default(),
        }
    }

    pub fn decide(&mut self, now_ms: u64) -> Decision {
        self.seen += 1;
        let stalled = self.profile.stall_for_ms > 0
            && now_ms >= self.profile.stall_at_ms
            && now_ms
                < self
                    .profile
                    .stall_at_ms
                    .saturating_add(self.profile.stall_for_ms);
        if stalled {
            return Decision {
                drop: true,
                copies: 0,
                delay_ms: 0,
                reordered: false,
                stalled: true,
            };
        }
        if self.burst_remaining == 0
            && self.profile.burst_every > 0
            && self.seen % self.profile.burst_every == 0
        {
            self.burst_remaining = self.profile.burst_length;
        }
        if self.burst_remaining > 0 {
            self.burst_remaining -= 1;
            return Decision {
                drop: true,
                copies: 0,
                delay_ms: 0,
                reordered: false,
                stalled: false,
            };
        }
        if self.profile.loss_basis_points > 0
            && self.rng.next() % 10_000 < u64::from(self.profile.loss_basis_points)
        {
            return Decision {
                drop: true,
                copies: 0,
                delay_ms: 0,
                reordered: false,
                stalled: false,
            };
        }
        let duplicate =
            self.profile.duplicate_every > 0 && self.seen % self.profile.duplicate_every == 0;
        let reordered =
            self.profile.reorder_every > 0 && self.seen % self.profile.reorder_every == 0;
        let jitter = if self.profile.jitter_ms == 0 {
            0
        } else {
            self.rng.next() % (self.profile.jitter_ms + 1)
        };
        Decision {
            drop: false,
            copies: if duplicate { 2 } else { 1 },
            delay_ms: self.profile.delay_ms
                + jitter
                + if reordered {
                    self.profile.reorder_delay_ms
                } else {
                    0
                },
            reordered,
            stalled: false,
        }
    }

    pub fn admit(&mut self, now_ms: u64, direction: Direction, bytes: &[u8], client: SocketAddr) {
        self.stats.received += 1;
        let decision = self.decide(now_ms);
        if decision.drop {
            self.stats.dropped += 1;
            if decision.stalled {
                self.stats.stalled += 1;
            }
            return;
        }
        if decision.copies == 2 {
            self.stats.duplicated += 1;
        }
        if decision.reordered {
            self.stats.reordered += 1;
        }
        for copy in 0..decision.copies {
            if self.queue.len() >= self.profile.max_scheduled {
                self.stats.queue_overflow += 1;
                self.stats.dropped += 1;
                continue;
            }
            let delay = decision.delay_ms + u64::from(copy);
            let item = Scheduled {
                due_ms: now_ms.saturating_add(delay),
                direction,
                bytes: bytes.to_vec(),
                client,
            };
            let position = self
                .queue
                .iter()
                .position(|queued| queued.due_ms > item.due_ms)
                .unwrap_or(self.queue.len());
            self.queue.insert(position, item);
            self.stats.max_queue = self.stats.max_queue.max(self.queue.len());
        }
    }

    pub fn pop_due(&mut self, now_ms: u64) -> Option<Scheduled> {
        if self.queue.front().is_some_and(|item| item.due_ms <= now_ms) {
            self.queue.pop_front()
        } else {
            None
        }
    }
}
