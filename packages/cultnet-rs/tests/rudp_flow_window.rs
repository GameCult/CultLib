//! The sender's flow window: a reliable packet goes on the wire only while its
//! sequence is at most 1,023 above the lowest unacked one and the payload above
//! that sequence stays within 4 MiB. Everything else waits in order in the queue.

use anyhow::Result;
use cultnet_rs::*;

const CONNECTION_ID: u32 = 0x464c_4f57;
const MIB: usize = 1024 * 1024;

fn connected(initial_sequence: u32) -> Result<CultNetRudpSession> {
    let mut session = CultNetRudpSession::new(CultNetRudpSessionOptions {
        connection_id: CONNECTION_ID,
        initial_sequence,
        resend_delay_ms: 25,
        max_pending_reliable_packets: None,
    });
    session.receive(
        &CultNetRudpPacket {
            packet_type: CultNetRudpPacketType::Accept,
            connection_id: CONNECTION_ID,
            sequence: 0,
            ack: 0,
            ack_mask: 0,
            channel_id: "control".into(),
            reliable: false,
            ordered: false,
            sequenced: false,
            fragment_id: 0,
            fragment_index: 0,
            fragment_count: 0,
            payload: Vec::new(),
        },
        0,
    )?;
    Ok(session)
}

fn reliable() -> CultNetRudpSendOptions {
    CultNetRudpSendOptions {
        reliable: true,
        ..Default::default()
    }
}

/// What `send_many` puts on the wire now; the rest of the packet waits queued.
fn send(session: &mut CultNetRudpSession, payload_bytes: usize) -> Result<Vec<CultNetRudpPacket>> {
    session.send_many("state", vec![7; payload_bytes], reliable(), None)
}

fn ack_for(sequence: u32) -> CultNetRudpPacket {
    CultNetRudpPacket {
        packet_type: CultNetRudpPacketType::Ack,
        connection_id: CONNECTION_ID,
        sequence: 0,
        ack: sequence,
        ack_mask: 0,
        channel_id: "control".into(),
        reliable: false,
        ordered: false,
        sequenced: false,
        fragment_id: 0,
        fragment_index: 0,
        fragment_count: 0,
        payload: Vec::new(),
    }
}

fn sequences(packets: &[CultNetRudpPacket]) -> Vec<u32> {
    packets.iter().map(|packet| packet.sequence).collect()
}

/// Sends `g`, then acks every packet up to and including the one 1,023 above
/// it, leaving `g` the only unacked sequence. Returns `g`.
fn lose_g_and_fill_the_span(session: &mut CultNetRudpSession) -> Result<CultNetRudpPacket> {
    let g = send(session, 1)?.remove(0);
    for offset in 1..=1_023u32 {
        let sent = send(session, 1)?;
        assert_eq!(sequences(&sent), vec![g.sequence + offset]);
        session.receive(&ack_for(g.sequence + offset), 1)?;
    }
    Ok(g)
}

#[test]
fn a_lost_packet_holds_the_sender_1023_sequences_ahead_and_is_still_delivered() -> Result<()> {
    let mut sender = connected(1)?;
    let mut receiver = connected(100)?;
    let g = send(&mut sender, 1)?.remove(0);
    // g is lost. Everything after it is delivered and acked, until the sender stops.
    let mut admitted_after_g = 0u32;
    for _ in 0..4_200 {
        for packet in send(&mut sender, 1)? {
            admitted_after_g += 1;
            receiver.receive(&packet, 1)?;
            sender.receive(&receiver.create_ack(), 1)?;
        }
    }
    assert_eq!(admitted_after_g, 1_023);
    assert_eq!(sender.queued_reliable_packet_count(), 4_200 - 1_023);

    let retransmit = sender
        .due_resends(1_000)
        .into_iter()
        .find(|packet| packet.sequence == g.sequence)
        .expect("g is still pending and due");
    let delivered = receiver.receive(&retransmit, 1_000)?.delivered;
    assert_eq!(delivered.len(), 1);
    assert_eq!(delivered[0].sequence, g.sequence);

    let promoted = sender
        .receive(&receiver.create_ack_for_received(g.sequence), 1_001)?
        .ready_to_send;
    assert_eq!(promoted.first().map(|packet| packet.sequence), Some(g.sequence + 1_024));
    Ok(())
}

#[test]
fn a_direct_send_past_the_span_is_refused_without_consuming_a_sequence() -> Result<()> {
    let mut sender = connected(1)?;
    let g = lose_g_and_fill_the_span(&mut sender)?;
    assert!(sender.send("state", vec![7], reliable()).is_err());
    assert_eq!(sender.queued_reliable_packet_count(), 0);
    sender.receive(&ack_for(g.sequence), 2)?;
    let next = sender.send("state", vec![7], reliable())?;
    assert_eq!(next.sequence, g.sequence + 1_024);
    Ok(())
}

#[test]
fn payload_above_the_lowest_unacked_sequence_is_bounded_by_4_mib() -> Result<()> {
    let mut sender = connected(1)?;
    let g = send(&mut sender, MIB)?.remove(0);
    // The lowest unacked packet's own bytes do not count: four more MiB fit above it.
    for _ in 0..4 {
        assert_eq!(send(&mut sender, MIB)?.len(), 1);
    }
    assert!(send(&mut sender, MIB)?.is_empty());
    assert!(send(&mut sender, MIB)?.is_empty());
    assert_eq!(sender.queued_reliable_packet_count(), 2);

    // Acking g moves the floor up one packet: exactly one MiB more fits.
    let promoted = sender.receive(&ack_for(g.sequence), 1)?.ready_to_send;
    assert_eq!(sequences(&promoted), vec![g.sequence + 5]);
    assert_eq!(sender.queued_reliable_packet_count(), 1);
    Ok(())
}

#[test]
fn a_packet_larger_than_the_window_goes_out_alone_and_waits_behind_anything_unacked() -> Result<()> {
    let mut sender = connected(1)?;
    assert_eq!(send(&mut sender, 5 * MIB)?.len(), 1);
    let mut sender = connected(1)?;
    let g = send(&mut sender, 1)?.remove(0);
    assert!(send(&mut sender, 5 * MIB)?.is_empty());
    let promoted = sender.receive(&ack_for(g.sequence), 1)?.ready_to_send;
    assert_eq!(sequences(&promoted), vec![g.sequence + 1]);
    Ok(())
}

#[test]
fn a_small_packet_does_not_overtake_a_queued_large_one() -> Result<()> {
    let mut sender = connected(1)?;
    let g = send(&mut sender, 1)?.remove(0);
    assert_eq!(send(&mut sender, 3 * MIB)?.len(), 1);
    assert!(send(&mut sender, 2 * MIB)?.is_empty());
    // One byte would fit above g, but the 2 MiB packet is ahead of it.
    assert!(send(&mut sender, 1)?.is_empty());
    let promoted = sender.receive(&ack_for(g.sequence), 1)?.ready_to_send;
    assert_eq!(sequences(&promoted), vec![g.sequence + 2, g.sequence + 3]);
    Ok(())
}
