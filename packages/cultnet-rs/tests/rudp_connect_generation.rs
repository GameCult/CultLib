//! A Connect's sequence says whether it repeats the one a session accepted, and
//! the handshake alone seeds the watermark that orders delivery. Each test puts
//! one rule in front of real sessions or real transports.

use std::collections::BTreeSet;
use std::net::{SocketAddr, UdpSocket};
use std::time::Duration;

use anyhow::{Result, anyhow};
use cultnet_rs::*;

const CONNECTION_ID: u32 = 0x4355_4c54;

fn socket() -> Result<UdpSocket> {
    let socket = UdpSocket::bind("127.0.0.1:0")?;
    socket.set_read_timeout(Some(Duration::from_millis(20)))?;
    Ok(socket)
}

fn session(initial_sequence: u32) -> CultNetRudpSession {
    CultNetRudpSession::new(CultNetRudpSessionOptions {
        connection_id: CONNECTION_ID,
        initial_sequence,
        resend_delay_ms: 250,
        max_pending_reliable_packets: None,
    })
}

fn ordered() -> CultNetRudpSendOptions {
    CultNetRudpSendOptions {
        reliable: true,
        ordered: true,
        ..Default::default()
    }
}

fn names(frames: &[CultNetRudpDeliveredFrame]) -> Vec<String> {
    frames
        .iter()
        .map(|frame| String::from_utf8(frame.payload.clone()).unwrap())
        .collect()
}

/// A completed handshake between two sessions; returns the Connect and Accept.
fn handshake(
    client: &mut CultNetRudpSession,
    server: &mut CultNetRudpSession,
) -> Result<(CultNetRudpPacket, CultNetRudpPacket)> {
    let connect = client.create_connect(0, Vec::new())?;
    let accept = server.accept_connect(&connect, 0, Vec::new())?;
    client.receive(&accept, 0)?;
    assert!(client.connected() && server.connected());
    Ok((connect, accept))
}

/// What the server owed the client when the session ended is gone. A client
/// that reconnects on the same session must still receive what the new server
/// session sends: the old session's lost write is not a hole in the new one.
#[test]
fn a_reconnect_on_the_same_session_is_not_stranded_by_a_write_the_ended_session_owed() -> Result<()> {
    let mut client = session(1);
    let mut server = session(500);
    handshake(&mut client, &mut server)?;
    let _lost = server.send("schema", b"lost".to_vec(), ordered())?;
    server.create_disconnect(b"gone".to_vec());

    let connect = client.create_connect(1, Vec::new())?;
    let accept = server.accept_connect(&connect, 1, Vec::new())?;
    client.receive(&accept, 1)?;
    let after = server.send("schema", b"after".to_vec(), ordered())?;
    assert_eq!(names(&client.receive(&after, 2)?.delivered), ["after"]);
    Ok(())
}

/// A server session that ended and then accepts a Connect from a new client
/// forgets what the old client sent: the new client's sequences are its own.
#[test]
fn a_server_that_accepts_a_new_client_forgets_the_old_clients_sequences() -> Result<()> {
    let mut old_client = session(50);
    let mut server = session(500);
    handshake(&mut old_client, &mut server)?;
    for name in ["c1", "c2", "c3"] {
        let packet = old_client.send("schema", name.as_bytes().to_vec(), ordered())?;
        assert_eq!(names(&server.receive(&packet, 1)?.delivered), [name]);
    }
    server.create_disconnect(b"restart".to_vec());

    let mut new_client = session(2);
    let connect = new_client.create_connect(2, Vec::new())?;
    let accept = server.accept_connect(&connect, 2, Vec::new())?;
    new_client.receive(&accept, 2)?;
    let frame = new_client.send("schema", b"fresh".to_vec(), ordered())?;
    assert_eq!(names(&server.receive(&frame, 3)?.delivered), ["fresh"]);
    Ok(())
}

/// The same Connect again is a retransmit: it does not reset the session, so
/// what the session already delivered stays delivered once.
#[test]
fn a_retransmitted_connect_after_data_has_flowed_does_not_reset_the_session() -> Result<()> {
    let mut client = session(1);
    let mut server = session(500);
    let (connect, _) = handshake(&mut client, &mut server)?;
    let frame = client.send("schema", b"once".to_vec(), ordered())?;
    assert_eq!(names(&server.receive(&frame, 1)?.delivered), ["once"]);

    assert!(server.connect_repeats(&connect));
    let reply = server.accept_connect(&connect, 2, Vec::new())?;
    assert_eq!(reply.packet_type, CultNetRudpPacketType::Ack, "the Accept was already acknowledged");
    assert_eq!(server.outstanding_reliable_packet_count(), 0, "a repeat queued something");
    assert!(
        server.receive(&frame, 3)?.delivered.is_empty(),
        "a retransmitted Connect made the server forget what it had delivered"
    );
    Ok(())
}

/// A Connect that carries another sequence is not a repeat, even from a peer
/// that sent the same bytes.
#[test]
fn a_connect_with_another_sequence_is_not_a_repeat_whatever_it_carries() -> Result<()> {
    let mut server = session(500);
    let mut first = session(1);
    let connect = first.create_connect(0, b"same payload".to_vec())?;
    server.accept_connect(&connect, 0, Vec::new())?;
    assert!(server.connect_repeats(&connect));

    let mut restarted = session(9);
    let again = restarted.create_connect(0, b"same payload".to_vec())?;
    assert!(!server.connect_repeats(&again));
    let mut not_a_connect = connect.clone();
    not_a_connect.packet_type = CultNetRudpPacketType::Ping;
    assert!(!server.connect_repeats(&not_a_connect));
    server.create_disconnect(Vec::new());
    assert!(!server.connect_repeats(&connect), "an ended session repeats nothing");
    Ok(())
}

/// An Accept counts only when its ack field names the pending Connect.
#[test]
fn an_accept_that_does_not_name_the_pending_connect_is_ignored() -> Result<()> {
    let mut client = session(10);
    let mut server = session(500);
    let connect = client.create_connect(0, Vec::new())?;
    let accept = server.accept_connect(&connect, 0, Vec::new())?;

    let mut stale = accept.clone();
    stale.ack = connect.sequence + 50;
    stale.ack_mask = 0;
    client.receive(&stale, 1)?;
    assert!(!client.connected(), "a stale Accept connected the session");
    assert_eq!(client.pending_reliable_sequences(), vec![connect.sequence]);

    let mut named_by_mask = accept.clone();
    named_by_mask.ack = connect.sequence + 3;
    named_by_mask.ack_mask = 1 << 2;
    client.receive(&named_by_mask, 2)?;
    assert!(client.connected(), "the mask names the Connect");
    Ok(())
}

/// A late or duplicate Accept after a local disconnect must not revive the
/// ended session.
#[test]
fn an_accept_after_a_local_disconnect_does_not_reconnect_the_session() -> Result<()> {
    let mut client = session(1);
    let mut server = session(500);
    let (_, accept) = handshake(&mut client, &mut server)?;
    client.create_disconnect(b"bye".to_vec());
    assert!(!client.connected());

    client.receive(&accept, 5)?;
    assert!(!client.connected(), "a late Accept revived an ended session");
    Ok(())
}

/// An Accept counts once: a duplicate of the one honoured does not seed the
/// watermark again.
#[test]
fn a_duplicate_accept_does_not_reseed_the_watermark() -> Result<()> {
    let mut client = session(1);
    let mut server = session(500);
    let (_, accept) = handshake(&mut client, &mut server)?;
    let s1 = server.send("schema", b"s1".to_vec(), ordered())?;
    let s2 = server.send("schema", b"s2".to_vec(), ordered())?;
    let mut forged = accept.clone();
    forged.sequence = s2.sequence;
    client.receive(&forged, 1)?;
    assert!(client.receive(&s2, 2)?.delivered.is_empty(), "s1 is still missing");
    assert_eq!(names(&client.receive(&s1, 3)?.delivered), ["s1", "s2"]);
    Ok(())
}

/// Reliable data that arrives before the connecting side has the Accept has no
/// place in the order: it is not delivered and not acknowledged, so the sender
/// retransmits it after the handshake.
#[test]
fn reliable_data_before_the_accept_is_neither_delivered_nor_acknowledged() -> Result<()> {
    let mut client = session(1);
    let mut server = session(500);
    let connect = client.create_connect(0, Vec::new())?;
    let accept = server.accept_connect(&connect, 0, Vec::new())?;
    let a = server.send("schema", b"A".to_vec(), ordered())?;
    let b = server.send("schema", b"B".to_vec(), ordered())?;

    for early in [&b, &a] {
        assert!(client.receive(early, 1)?.delivered.is_empty());
        let ack = client.create_ack_for_received(early.sequence);
        assert_eq!((ack.ack, ack.ack_mask), (0, 0), "an unhandled packet was acknowledged");
    }
    client.receive(&accept, 2)?;
    assert!(client.connected());
    assert_eq!(server.outstanding_reliable_packet_count(), 3, "nothing was acknowledged");

    assert_eq!(names(&client.receive(&a, 3)?.delivered), ["A"]);
    assert_eq!(names(&client.receive(&b, 3)?.delivered), ["B"]);
    Ok(())
}

/// The other order: with the Accept in hand, frames that arrive out of order
/// are delivered in order.
#[test]
fn ordered_frames_after_the_accept_are_delivered_in_order_whatever_arrives_first() -> Result<()> {
    let mut client = session(1);
    let mut server = session(500);
    handshake(&mut client, &mut server)?;
    let a = server.send("schema", b"A".to_vec(), ordered())?;
    let b = server.send("schema", b"B".to_vec(), ordered())?;
    assert!(client.receive(&b, 1)?.delivered.is_empty());
    assert_eq!(names(&client.receive(&a, 2)?.delivered), ["A", "B"]);
    Ok(())
}

#[test]
fn default_initial_sequences_are_drawn_at_random_from_one_to_two_to_the_thirty_first() -> Result<()> {
    fn assert_random(label: &str, draw: &dyn Fn() -> u32) {
        let draws: BTreeSet<u32> = (0..64).map(|_| draw()).collect();
        assert!(draws.len() > 32, "{label} does not draw at random: {draws:?}");
        assert!(
            draws.iter().all(|value| (1..(1_u32 << 31)).contains(value)),
            "{label} left [1, 2^31): {draws:?}"
        );
    }
    let address: SocketAddr = "127.0.0.1:9".parse()?;
    assert_random("random_initial_sequence", &random_initial_sequence);
    assert_random("session options", &|| {
        CultNetRudpSessionOptions::default().initial_sequence
    });
    assert_random("client options", &|| {
        CultNetRudpSocketTransportOptions::client("c", UdpSocket::bind("127.0.0.1:0").unwrap(), address, 1)
            .initial_sequence
    });
    assert_random("server options", &|| {
        CultNetRudpSocketTransportOptions::server("s", UdpSocket::bind("127.0.0.1:0").unwrap(), 1)
            .initial_sequence
    });
    assert_random("hub options", &|| {
        CultNetRudpServerHubOptions::new("h", UdpSocket::bind("127.0.0.1:0").unwrap(), 1)
            .initial_sequence
    });
    assert_random("cultmesh options", &|| {
        CultMeshRudpSocketOptions::default().initial_sequence
    });
    Ok(())
}

// Transports.

fn hub_event_matching(
    hub: &mut CultNetRudpServerHub,
    matches: impl Fn(&CultNetRudpServerEvent) -> bool,
) -> Result<CultNetRudpServerEvent> {
    for _ in 0..40 {
        if let Some(event) = hub.receive_event_once()?
            && matches(&event)
        {
            return Ok(event);
        }
    }
    Err(anyhow!("the hub never produced the event"))
}

fn client_on(
    socket: UdpSocket,
    remote: SocketAddr,
    initial_sequence: u32,
) -> Result<CultNetRudpSocketTransportConnection> {
    let mut options = CultNetRudpSocketTransportOptions::client("client", socket, remote, CONNECTION_ID);
    options.initial_sequence = initial_sequence;
    CultNetRudpSocketTransportConnection::new(options)
}

/// A client that restarts on the same address, connection id and Connect
/// payload is a new session, not a retransmit: the hub reports the old one
/// ended and delivers the new one's frames.
#[test]
fn the_hub_admits_a_restarted_client_with_the_same_address_id_and_payload() -> Result<()> {
    let mut hub = CultNetRudpServerHub::new(CultNetRudpServerHubOptions::new("hub", socket()?, CONNECTION_ID))?;
    let hub_addr = hub.local_addr()?;
    let shared = socket()?;
    let twin = shared.try_clone()?;
    let mut first = client_on(shared, hub_addr, 50)?;
    first.connect(b"same".to_vec())?;
    let CultNetRudpServerEvent::Connected { session: original } =
        hub_event_matching(&mut hub, |event| matches!(event, CultNetRudpServerEvent::Connected { .. }))?
    else {
        unreachable!()
    };
    first.receive_once()?;
    for index in 0..3_u8 {
        first.send("schema", vec![index])?;
        hub_event_matching(&mut hub, |event| matches!(event, CultNetRudpServerEvent::Frame { .. }))?;
    }

    let mut second = client_on(twin, hub_addr, 7)?;
    second.connect(b"same".to_vec())?;
    let CultNetRudpServerEvent::Disconnected { session: ended, .. } =
        hub_event_matching(&mut hub, |event| matches!(event, CultNetRudpServerEvent::Disconnected { .. }))?
    else {
        unreachable!()
    };
    assert_eq!(ended, original);
    let CultNetRudpServerEvent::Connected { session: replacement } =
        hub_event_matching(&mut hub, |event| matches!(event, CultNetRudpServerEvent::Connected { .. }))?
    else {
        unreachable!()
    };
    assert_ne!(replacement.session_generation, original.session_generation);
    second.receive_once()?;
    second.send("schema", b"after restart".to_vec())?;
    let CultNetRudpServerEvent::Frame { session, frame } =
        hub_event_matching(&mut hub, |event| matches!(event, CultNetRudpServerEvent::Frame { .. }))?
    else {
        unreachable!()
    };
    assert_eq!(session, replacement);
    assert_eq!(frame.payload, b"after restart");
    Ok(())
}

/// The same for a server-mode transport, where the restarted client's sequences
/// are all below what the old client used.
#[test]
fn server_mode_admits_a_restarted_client_and_delivers_its_frames() -> Result<()> {
    let server_socket = socket()?;
    let server_addr = server_socket.local_addr()?;
    let mut server = CultNetRudpSocketTransportConnection::new(CultNetRudpSocketTransportOptions::server(
        "server",
        server_socket,
        CONNECTION_ID,
    ))?;
    let shared = socket()?;
    let twin = shared.try_clone()?;
    let mut first = client_on(shared, server_addr, 50)?;
    first.connect(b"same".to_vec())?;
    assert!(server.receive_once()?.is_none());
    first.receive_once()?;
    for index in 0..3_u8 {
        first.send("schema", vec![index])?;
        assert_eq!(server.receive_once()?.expect("first client frame").payload, vec![index]);
    }

    let mut second = client_on(twin, server_addr, 7)?;
    second.connect(b"same".to_vec())?;
    assert!(server.receive_once()?.is_none());
    second.receive_once()?;
    assert!(second.connected());
    second.send("schema", b"after restart".to_vec())?;
    assert_eq!(server.receive_once()?.expect("restarted client frame").payload, b"after restart");
    Ok(())
}

/// A retransmitted Connect in server mode, after data has flowed, keeps the
/// session: the data it delivered is not delivered again.
#[test]
fn server_mode_keeps_its_session_when_a_connect_is_retransmitted_after_data_flowed() -> Result<()> {
    let server_socket = socket()?;
    let server_addr = server_socket.local_addr()?;
    let mut server = CultNetRudpSocketTransportConnection::new(CultNetRudpSocketTransportOptions::server(
        "server",
        server_socket,
        CONNECTION_ID,
    ))?;
    let peer = socket()?;
    let mut client = session(1);
    let connect = client.create_connect(0, b"peer".to_vec())?;
    peer.send_to(&encode_rudp_packet(&connect)?, server_addr)?;
    server.receive_once()?;
    let mut buffer = vec![0_u8; 65_535];
    let (received, _) = peer.recv_from(&mut buffer)?;
    client.receive(&decode_rudp_packet(&buffer[..received])?, 0)?;
    let frame = client.send("schema", b"once".to_vec(), ordered())?;
    peer.send_to(&encode_rudp_packet(&frame)?, server_addr)?;
    assert_eq!(server.receive_once()?.expect("first delivery").payload, b"once");

    peer.send_to(&encode_rudp_packet(&connect)?, server_addr)?;
    server.receive_once()?;
    peer.send_to(&encode_rudp_packet(&frame)?, server_addr)?;
    assert!(server.receive_once()?.is_none(), "the retransmitted Connect reset the session");
    Ok(())
}

fn connected_client(
    server_socket: &UdpSocket,
    server_addr: SocketAddr,
) -> Result<CultNetRudpSocketTransportConnection> {
    let mut client = CultNetRudpSocketTransportConnection::new(CultNetRudpSocketTransportOptions::client(
        "client",
        socket()?,
        server_addr,
        CONNECTION_ID,
    ))?;
    client.connect(b"hello".to_vec())?;
    let mut buffer = vec![0_u8; 65_535];
    // Another client's acknowledgements may be queued ahead of this Connect.
    let (connect, client_addr) = loop {
        let (received, from) = server_socket.recv_from(&mut buffer)?;
        let packet = decode_rudp_packet(&buffer[..received])?;
        if packet.packet_type == CultNetRudpPacketType::Connect {
            break (packet, from);
        }
    };
    let accept = session(500).accept_connect(&connect, 0, Vec::new())?;
    server_socket.send_to(&encode_rudp_packet(&accept)?, client_addr)?;
    client.receive_once()?;
    assert!(client.connected());
    Ok(client)
}

/// A receipt belongs to the transport that issued it: asked of another
/// transport, it is not that transport's write, however alike their numbers.
#[test]
fn a_receipt_asked_of_another_transport_reads_invalidated() -> Result<()> {
    let server = socket()?;
    let server_addr = server.local_addr()?;
    let mut a = connected_client(&server, server_addr)?;
    let mut b = connected_client(&server, server_addr)?;
    let receipt_a = a.send_reliable("schema", b"a".to_vec())?;
    let receipt_b = b.send_reliable("schema", b"b".to_vec())?;
    assert_eq!(a.reliable_send_status(&receipt_a), CultNetRudpReliableSendStatus::Pending);
    assert_eq!(b.reliable_send_status(&receipt_b), CultNetRudpReliableSendStatus::Pending);
    assert_eq!(b.reliable_send_status(&receipt_a), CultNetRudpReliableSendStatus::Invalidated);
    assert_eq!(a.reliable_send_status(&receipt_b), CultNetRudpReliableSendStatus::Invalidated);
    assert_ne!(receipt_a, receipt_b, "receipts from two transports compare equal");
    Ok(())
}

/// A flush cut short by a timeout says so.
#[test]
fn a_flush_that_a_timeout_ended_names_the_timeout() -> Result<()> {
    let server = socket()?;
    let server_addr = server.local_addr()?;
    let mut client = connected_client(&server, server_addr)?;
    client.send_reliable("schema", b"unanswered".to_vec())?;
    std::thread::sleep(Duration::from_millis(5));
    assert!(client.check_timeout(0));
    let error = client.flush_reliable(Duration::from_millis(50)).expect_err("the session timed out");
    assert!(error.to_string().contains("session timed out"), "{error}");
    Ok(())
}

// Hold-buffer boundaries: 1,024 frames and 64 channels are held; one more is
// refused.

/// A connected pair with one sequence the receiver will never see, so every
/// later ordered frame waits behind it. The sender's window is kept open by
/// acknowledging what the receiver has.
fn held_behind_a_gap(
    frames: usize,
    channel_of: impl Fn(usize) -> String,
) -> Result<Result<usize>> {
    let mut sender = session(1);
    let mut receiver = session(500);
    handshake(&mut sender, &mut receiver)?;
    let _missing = sender.send("gap", b"lost".to_vec(), ordered())?;
    let mut held = 0;
    for index in 0..frames {
        let packet = sender.send(&channel_of(index), vec![0], ordered())?;
        match receiver.receive(&packet, 1) {
            Ok(result) => {
                assert!(result.delivered.is_empty());
                held += 1;
            }
            Err(error) => return Ok(Err(error)),
        }
        if index % 16 == 15 {
            sender.receive(&receiver.create_ack(), 1)?;
        }
    }
    Ok(Ok(held))
}

#[test]
fn the_hold_buffer_takes_exactly_1024_frames_and_refuses_the_next() -> Result<()> {
    assert_eq!(held_behind_a_gap(1024, |_| "schema".into())??, 1024);
    let error = held_behind_a_gap(1025, |_| "schema".into())?.expect_err("1025 held frames");
    assert!(error.to_string().contains("hold buffer is full"), "{error}");
    Ok(())
}

#[test]
fn the_hold_buffer_takes_exactly_64_channels_and_refuses_the_next() -> Result<()> {
    assert_eq!(held_behind_a_gap(64, |index| format!("channel-{index}"))??, 64);
    let error = held_behind_a_gap(65, |index| format!("channel-{index}"))?.expect_err("65 held channels");
    assert!(error.to_string().contains("too many ordered channels"), "{error}");
    Ok(())
}
