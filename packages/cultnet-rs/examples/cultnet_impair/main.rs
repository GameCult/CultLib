//! `cultnet-impair`: a deterministic bidirectional UDP proxy for pressure-testing
//! real CultNet RUDP sessions. It owns transport impairment only; what a stream
//! does with the loss is the stream's business.
//!
//! The proxy listens at the endpoint a client dials and forwards to `--upstream`
//! through one upstream socket per client, so the reverse ACK and feedback
//! traffic crosses the same schedule. The schedule is `policy.rs`.
//!
//! ```text
//! cargo run --release --example cultnet_impair -- \
//!   --listen 0.0.0.0:17890 --upstream 127.0.0.1:17990 \
//!   --profile tests/impairment/loss-1pct.toml --seed 424242 --metrics loss-1pct.csv
//! ```
//!
//! Profiles are a small TOML subset of unsigned integers; see
//! `tests/impairment/`. The receiver binds the upstream endpoint and the client
//! dials the proxy's; never one port for both. For a two-host test, run the proxy
//! on the receiver host so reverse traffic is impaired too.

mod policy;

use std::collections::HashMap;
use std::env;
use std::fs::{self, File};
use std::io::{self, Write};
use std::net::{SocketAddr, UdpSocket};
use std::path::{Path, PathBuf};
use std::thread;
use std::time::{Duration, Instant};

use policy::{Direction, Profile, Scheduler, Stats};

const MAX_DATAGRAM: usize = 65_535;
const MAX_CLIENT_FLOWS: usize = 64;
const CLIENT_FLOW_IDLE_TIMEOUT_MS: u64 = 30_000;
const MAX_DATAGRAMS_PER_FLOW_PER_TURN: usize = 16;

#[derive(Debug)]
struct UpstreamFlow {
    socket: UdpSocket,
    last_activity_ms: u64,
}

fn open_upstream_flow(upstream: SocketAddr, now_ms: u64) -> io::Result<UpstreamFlow> {
    let socket = UdpSocket::bind("0.0.0.0:0")?;
    socket.connect(upstream)?;
    socket.set_nonblocking(true)?;
    Ok(UpstreamFlow {
        socket,
        last_activity_ms: now_ms,
    })
}

fn ensure_upstream_flow(
    flows: &mut HashMap<SocketAddr, UpstreamFlow>,
    client: SocketAddr,
    upstream: SocketAddr,
    now_ms: u64,
    stats: &mut Stats,
) -> io::Result<()> {
    if flows.contains_key(&client) {
        return Ok(());
    }
    if flows.len() >= MAX_CLIENT_FLOWS
        && let Some(oldest) = flows
            .iter()
            .min_by_key(|(_, flow)| flow.last_activity_ms)
            .map(|(address, _)| *address)
    {
        flows.remove(&oldest);
        stats.flow_evicted += 1;
    }
    flows.insert(client, open_upstream_flow(upstream, now_ms)?);
    stats.max_flows = stats.max_flows.max(flows.len());
    Ok(())
}

fn reap_idle_flows(
    flows: &mut HashMap<SocketAddr, UpstreamFlow>,
    now_ms: u64,
    stats: &mut Stats,
) {
    let before = flows.len();
    flows.retain(|_, flow| {
        now_ms.saturating_sub(flow.last_activity_ms) < CLIENT_FLOW_IDLE_TIMEOUT_MS
    });
    stats.flow_evicted += (before - flows.len()) as u64;
}

#[derive(Debug)]
struct Options {
    listen: SocketAddr,
    upstream: SocketAddr,
    profile: PathBuf,
    seed: u64,
    metrics: Option<PathBuf>,
}

fn parse_options() -> Result<Options, String> {
    let mut args = env::args().skip(1);
    let mut listen = None;
    let mut upstream = None;
    let mut profile = None;
    let mut seed = 1;
    let mut metrics = None;
    while let Some(arg) = args.next() {
        let mut value = || args.next().ok_or_else(|| format!("{arg} requires a value"));
        match arg.as_str() {
            "--listen" => {
                listen = Some(
                    value()?
                        .parse()
                        .map_err(|_| "--listen must be a socket address")?,
                )
            }
            "--upstream" => {
                upstream = Some(
                    value()?
                        .parse()
                        .map_err(|_| "--upstream must be a socket address")?,
                )
            }
            "--profile" => profile = Some(PathBuf::from(value()?)),
            "--seed" => {
                seed = value()?
                    .parse()
                    .map_err(|_| "--seed must be an unsigned integer")?
            }
            "--metrics" => metrics = Some(PathBuf::from(value()?)),
            "--help" | "-h" => return Err(usage().into()),
            _ => return Err(format!("unknown argument {arg}\n{}", usage())),
        }
    }
    Ok(Options {
        listen: listen.ok_or("--listen is required")?,
        upstream: upstream.ok_or("--upstream is required")?,
        profile: profile.ok_or("--profile is required")?,
        seed,
        metrics,
    })
}

fn usage() -> &'static str {
    "usage: cultnet-impair --listen HOST:PORT --upstream HOST:PORT --profile PATH [--seed N] [--metrics PATH]"
}

fn elapsed_ms(start: Instant) -> u64 {
    start.elapsed().as_millis().try_into().unwrap_or(u64::MAX)
}

fn write_metrics(path: &Path, stats: &Stats, active_flows: usize) -> io::Result<()> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    let mut file = File::create(path)?;
    writeln!(
        file,
        "received,forwarded,dropped,duplicated,reordered,stalled,queue_overflow,max_queue,flow_evicted,max_flows,active_flows"
    )?;
    writeln!(
        file,
        "{},{},{},{},{},{},{},{},{},{},{}",
        stats.received,
        stats.forwarded,
        stats.dropped,
        stats.duplicated,
        stats.reordered,
        stats.stalled,
        stats.queue_overflow,
        stats.max_queue,
        stats.flow_evicted,
        stats.max_flows,
        active_flows
    )
}

fn run(options: Options) -> Result<(), String> {
    let profile = Profile::load(&options.profile)?;
    let client_socket = UdpSocket::bind(options.listen)
        .map_err(|error| format!("binding {}: {error}", options.listen))?;
    client_socket
        .set_nonblocking(true)
        .map_err(|error| error.to_string())?;
    println!(
        "cultnet-impair listen={} upstream={} seed={} profile={}",
        options.listen,
        options.upstream,
        options.seed,
        options.profile.display()
    );
    let start = Instant::now();
    let mut scheduler = Scheduler::new(profile, options.seed);
    let mut flows = HashMap::<SocketAddr, UpstreamFlow>::new();
    let mut last_flow_reap_at = 0_u64;
    let mut buffer = vec![0; MAX_DATAGRAM];
    loop {
        let now = elapsed_ms(start);
        let mut did_work = false;
        for _ in 0..MAX_DATAGRAMS_PER_FLOW_PER_TURN {
            match client_socket.recv_from(&mut buffer) {
                Ok((size, address)) => {
                    did_work = true;
                    ensure_upstream_flow(
                        &mut flows,
                        address,
                        options.upstream,
                        now,
                        &mut scheduler.stats,
                    )
                    .map_err(|error| format!("opening upstream flow for {address}: {error}"))?;
                    if let Some(flow) = flows.get_mut(&address) {
                        flow.last_activity_ms = now;
                    }
                    scheduler.admit(
                        now,
                        Direction::ClientToUpstream,
                        &buffer[..size],
                        address,
                    );
                }
                Err(error) if error.kind() == io::ErrorKind::WouldBlock => break,
                Err(error) => return Err(error.to_string()),
            }
        }
        for (address, flow) in &mut flows {
            for _ in 0..MAX_DATAGRAMS_PER_FLOW_PER_TURN {
                match flow.socket.recv(&mut buffer) {
                    Ok(size) => {
                        did_work = true;
                        flow.last_activity_ms = now;
                        scheduler.admit(
                            now,
                            Direction::UpstreamToClient,
                            &buffer[..size],
                            *address,
                        );
                    }
                    Err(error) if error.kind() == io::ErrorKind::WouldBlock => break,
                    Err(error) => return Err(error.to_string()),
                }
            }
        }
        while let Some(item) = scheduler.pop_due(now) {
            did_work = true;
            let result = match item.direction {
                Direction::ClientToUpstream => match flows.get_mut(&item.client) {
                    Some(flow) => {
                        flow.last_activity_ms = now;
                        flow.socket.send(&item.bytes)
                    }
                    None => {
                        scheduler.stats.dropped += 1;
                        continue;
                    }
                },
                Direction::UpstreamToClient => client_socket.send_to(&item.bytes, item.client),
            };
            if result.is_ok() {
                scheduler.stats.forwarded += 1;
            }
        }
        if let Some(path) = options.metrics.as_deref() {
            if now % 1000 == 0 {
                let _ = write_metrics(path, &scheduler.stats, flows.len());
            }
        }
        if now.saturating_sub(last_flow_reap_at) >= 1_000 {
            reap_idle_flows(&mut flows, now, &mut scheduler.stats);
            last_flow_reap_at = now;
        }
        if did_work {
            thread::yield_now();
        } else {
            thread::sleep(Duration::from_millis(1));
        }
    }
}

fn main() {
    match parse_options().and_then(run) {
        Ok(()) => {}
        Err(error) => {
            eprintln!("{error}");
            std::process::exit(2);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn independent_clients_keep_independent_upstream_udp_identities() {
        let mut flows = HashMap::new();
        let mut stats = Stats::default();
        let upstream = "127.0.0.1:54321".parse().unwrap();
        let first = "127.0.0.1:12001".parse().unwrap();
        let second = "127.0.0.1:12002".parse().unwrap();

        ensure_upstream_flow(&mut flows, first, upstream, 1, &mut stats).unwrap();
        ensure_upstream_flow(&mut flows, second, upstream, 2, &mut stats).unwrap();

        assert_eq!(flows.len(), 2);
        assert_ne!(
            flows[&first].socket.local_addr().unwrap(),
            flows[&second].socket.local_addr().unwrap()
        );
        assert_eq!(stats.max_flows, 2);
        assert_eq!(stats.flow_evicted, 0);
    }

    #[test]
    fn idle_client_flows_are_reaped_without_touching_active_flows() {
        let mut flows = HashMap::new();
        let mut stats = Stats::default();
        let upstream = "127.0.0.1:54321".parse().unwrap();
        let idle = "127.0.0.1:12001".parse().unwrap();
        let active = "127.0.0.1:12002".parse().unwrap();

        ensure_upstream_flow(&mut flows, idle, upstream, 0, &mut stats).unwrap();
        ensure_upstream_flow(
            &mut flows,
            active,
            upstream,
            CLIENT_FLOW_IDLE_TIMEOUT_MS - 1,
            &mut stats,
        )
        .unwrap();
        reap_idle_flows(&mut flows, CLIENT_FLOW_IDLE_TIMEOUT_MS, &mut stats);

        assert!(!flows.contains_key(&idle));
        assert!(flows.contains_key(&active));
        assert_eq!(stats.flow_evicted, 1);
    }
}
