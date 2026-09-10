//! Measures what a media channel actually costs on the wire under loss.
//!
//! The claim under test: a reliable media channel retransmits lost packets, so
//! it adds load exactly when the link has least to give, while an unreliable one
//! drops them and holds its rate. Run both halves through `cultnet-impair` with
//! a `loss_basis_points` profile and compare bytes on the wire against bytes of
//! payload offered.
//!
//! ```text
//! media_delivery_probe recv <bind_addr> <expect_count>
//! media_delivery_probe send <endpoint> <reliable|unreliable> <payload_bytes> <count>
//! ```
//!
//! The sender reports amplification: wire bytes divided by offered payload
//! bytes. At 1.0 nothing was retransmitted. Well above 1.0 under loss is the
//! pathology.

use std::env;
use std::net::{SocketAddr, UdpSocket};
use std::time::{Duration, Instant};

use anyhow::{Result, anyhow};
use cultnet_rs::{
    CultNetRudpServerHubOptions, CultNetRudpSocketTransportConnection,
    CultNetRudpSocketTransportOptions, CultNetTransportDelivery,
};

const CHANNEL: &str = "media";
const DRAIN: Duration = Duration::from_secs(5);

fn main() -> Result<()> {
    let args: Vec<String> = env::args().collect();
    match args.get(1).map(String::as_str) {
        Some("recv") => recv(
            args.get(2).ok_or_else(|| anyhow!("bind addr"))?.parse()?,
            args.get(3).ok_or_else(|| anyhow!("expect count"))?.parse()?,
        ),
        Some("send") => send(
            args.get(2).ok_or_else(|| anyhow!("endpoint"))?.parse()?,
            match args.get(3).map(String::as_str) {
                Some("unreliable") => CultNetTransportDelivery::Unreliable,
                Some("reliable") => CultNetTransportDelivery::Reliable,
                other => return Err(anyhow!("delivery must be reliable|unreliable, got {other:?}")),
            },
            args.get(4).ok_or_else(|| anyhow!("payload bytes"))?.parse()?,
            args.get(5).ok_or_else(|| anyhow!("count"))?.parse()?,
            args.get(6).map(|v| v.parse()).transpose()?.unwrap_or(12.0),
        ),
        _ => Err(anyhow!(
            "usage: media_delivery_probe recv <bind> <count> | send <endpoint> <reliable|unreliable> <bytes> <count>"
        )),
    }
}

fn recv(bind: SocketAddr, expect: u64) -> Result<()> {
    let socket = UdpSocket::bind(bind)?;
    socket.set_nonblocking(true)?;
    let mut hub = cultnet_rs::CultNetRudpServerHub::new(CultNetRudpServerHubOptions::new(
        "probe-recv",
        socket,
        0x0BE0_0001,
    ))?;

    println!("listening on {bind}, expecting {expect} frames");
    let started = Instant::now();
    let mut frames = 0u64;
    let mut payload_bytes = 0u64;
    let mut last_progress = Instant::now();

    loop {
        while let Some(event) = hub.receive_event_once()? {
            if let cultnet_rs::CultNetRudpServerEvent::Frame { frame, .. } = event
                && frame.channel_id == CHANNEL
            {
                frames += 1;
                payload_bytes += frame.payload.len() as u64;
                last_progress = Instant::now();
            }
        }
        if frames >= expect || last_progress.elapsed() > DRAIN {
            break;
        }
        std::thread::sleep(Duration::from_micros(200));
    }

    let stats = hub.stats();
    let elapsed = started.elapsed().as_secs_f64();
    println!("RESULT recv frames={frames} expected={expect} payload_bytes={payload_bytes}");
    println!(
        "RESULT recv wire_bytes_in={} wire_bytes_out={} elapsed_s={elapsed:.2} delivered_pct={:.2} fragment_sets_evicted={}",
        stats.bytes_received,
        stats.bytes_sent,
        (frames as f64 / expect as f64) * 100.0,
        stats.fragment_sets_evicted
    );
    Ok(())
}

fn send(
    endpoint: SocketAddr,
    delivery: CultNetTransportDelivery,
    payload_bytes: usize,
    count: u64,
    target_mbps: f64,
) -> Result<()> {
    let socket = UdpSocket::bind("0.0.0.0:0")?;
    socket.set_nonblocking(true)?;
    let mut options =
        CultNetRudpSocketTransportOptions::client("probe-send", socket, endpoint, 0x0BE0_0001);
    options.media_delivery = Some(delivery);
    options.media_reliable_expire_after_ms = None;

    let mut transport = CultNetRudpSocketTransportConnection::new(options)?;
    transport.connect(b"probe".to_vec())?;
    let deadline = Instant::now() + Duration::from_secs(10);
    while !transport.connected() {
        let _ = transport.receive_once()?;
        transport.poll_resends()?;
        if Instant::now() >= deadline {
            return Err(anyhow!("timed out connecting to {endpoint}"));
        }
        std::thread::sleep(Duration::from_millis(2));
    }

    // Pace to a realistic media rate. An unpaced blast overruns the receiver's
    // socket buffer and manufactures loss that has nothing to do with the link.
    let per_packet = Duration::from_secs_f64((payload_bytes as f64 * 8.0) / (target_mbps * 1e6));
    let payload = vec![0xA5u8; payload_bytes];
    let offered = payload_bytes as u64 * count;
    let started = Instant::now();
    let mut sent = 0u64;
    let mut would_block = 0u64;

    while sent < count {
        match transport.send(CHANNEL, payload.clone()) {
            Ok(()) => sent += 1,
            Err(error) if error.to_string().contains("would block") => {
                would_block += 1;
                transport.poll_resends()?;
                let _ = transport.receive_once()?;
                std::thread::sleep(Duration::from_micros(500));
            }
            Err(error) => return Err(error),
        }
        if sent % 64 == 0 {
            let _ = transport.receive_once()?;
            transport.poll_resends()?;
        }
        let due = started + per_packet.mul_f64(sent as f64);
        if let Some(wait) = due.checked_duration_since(Instant::now()) {
            std::thread::sleep(wait);
        }
    }
    let send_elapsed = started.elapsed();

    // Let retransmission run: this is where a reliable channel spends what an
    // unreliable one does not.
    let drain_start = Instant::now();
    while drain_start.elapsed() < DRAIN {
        let _ = transport.receive_once()?;
        transport.poll_resends()?;
        std::thread::sleep(Duration::from_millis(1));
    }

    let stats = transport.stats();
    let amplification = stats.bytes_sent as f64 / offered as f64;
    println!(
        "RESULT send delivery={delivery:?} payload_bytes={payload_bytes} count={count} offered={offered} target_mbps={target_mbps}"
    );
    println!(
        "RESULT send wire_bytes={} amplification={amplification:.3} send_s={:.2} total_s={:.2} would_block={would_block} reliable_expired={}",
        stats.bytes_sent,
        send_elapsed.as_secs_f64(),
        started.elapsed().as_secs_f64(),
        stats.reliable_packets_expired
    );
    Ok(())
}
