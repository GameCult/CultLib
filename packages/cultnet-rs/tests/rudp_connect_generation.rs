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
        initial_sequence: Some(initial_sequence),
        resend_delay_ms: 250,
        max_pending_reliable_packets: None,
    })
}

fn send(from: &mut CultNetRudpSession, text: &str) -> Result<CultNetRudpPacket> {
    from.send("schema", text.as_bytes().to_vec(), ordered())
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

/// A client that reconnects to a restarted server is not silenced by what it
/// received from the old one: the new server's sequences may overlap the old
/// ones, and none of them is a duplicate.
#[test]
fn a_reconnect_forgets_what_the_old_server_sent() -> Result<()> {
    let mut client = session(1);
    let mut old_server = session(500);
    handshake(&mut client, &mut old_server)?;
    for name in ["d1", "d2", "d3"] {
        let packet = send(&mut old_server, name)?;
        assert_eq!(names(&client.receive(&packet, 1)?.delivered), [name]);
    }

    let mut new_server = session(501);
    let connect = client.create_connect(2, Vec::new())?;
    let accept = new_server.accept_connect(&connect, 2, Vec::new())?;
    client.receive(&accept, 2)?;
    let fresh = send(&mut new_server, "fresh")?;
    assert_eq!(names(&client.receive(&fresh, 3)?.delivered), ["fresh"]);
    Ok(())
}

/// An Accept for a Connect that was abandoned before it was answered does not
/// connect the session.
#[test]
fn an_accept_for_an_abandoned_connect_does_not_connect_the_session() -> Result<()> {
    let mut client = session(1);
    let mut server = session(500);
    let connect = client.create_connect(0, Vec::new())?;
    let accept = server.accept_connect(&connect, 0, Vec::new())?;
    client.create_disconnect(b"never mind".to_vec());

    client.receive(&accept, 1)?;
    assert!(!client.connected(), "an Accept revived an abandoned Connect");
    Ok(())
}

/// What a session still owed a vanished peer does not keep the next peer out:
/// a new generation drops it before the queue is asked for room.
#[test]
fn a_full_queue_owed_to_a_vanished_peer_does_not_refuse_a_new_generation() -> Result<()> {
    let bounded = |initial_sequence: u32| {
        CultNetRudpSession::new(CultNetRudpSessionOptions {
            connection_id: CONNECTION_ID,
            initial_sequence: Some(initial_sequence),
            resend_delay_ms: 250,
            max_pending_reliable_packets: Some(1),
        })
    };
    let mut server = bounded(500);
    server.accept_connect(&session(1).create_connect(0, Vec::new())?, 0, Vec::new())?;
    assert_eq!(server.outstanding_reliable_packet_count(), 1, "the Accept is owed and never acknowledged");
    let next = session(9).create_connect(1, Vec::new())?;
    server.accept_connect(&next, 1, Vec::new())?;
    assert!(server.connect_repeats(&next));

    let mut client = bounded(1);
    client.create_connect(0, Vec::new())?;
    client.create_connect(1, Vec::new())?;
    assert_eq!(client.outstanding_reliable_packet_count(), 1, "only the new Connect is owed");
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

/// The options say nothing about the initial sequence; each session draws its
/// own, so one options object serves many sessions without giving them one
/// sequence.
#[test]
fn sessions_from_one_options_object_each_draw_their_own_initial_sequence() -> Result<()> {
    fn assert_random(label: &str, draws: &BTreeSet<u32>) {
        assert!(draws.len() > 32, "{label} does not draw at random: {draws:?}");
        assert!(
            draws.iter().all(|value| (1..(1_u32 << 31)).contains(value)),
            "{label} left [1, 2^31): {draws:?}"
        );
    }
    let address: SocketAddr = "127.0.0.1:9".parse()?;
    assert_eq!(CultNetRudpSessionOptions::default().initial_sequence, None);
    assert_eq!(
        CultNetRudpSocketTransportOptions::client("c", socket()?, address, 1).initial_sequence,
        None
    );
    assert_eq!(
        CultNetRudpSocketTransportOptions::server("s", socket()?, 1).initial_sequence,
        None
    );
    assert_eq!(
        CultNetRudpServerHubOptions::new("h", socket()?, 1).initial_sequence,
        None
    );
    assert_eq!(CultMeshRudpSocketOptions::default().initial_sequence, None);

    let options = CultNetRudpSessionOptions {
        connection_id: CONNECTION_ID,
        ..Default::default()
    };
    let connects: BTreeSet<u32> = (0..64)
        .map(|_| Ok(CultNetRudpSession::new(options.clone()).create_connect(0, Vec::new())?.sequence))
        .collect::<Result<_>>()?;
    assert_random("sessions from one options object", &connects);
    Ok(())
}

/// A transport built from the same options each time (a reconnect loop's
/// factory) opens each connection with its own Connect sequence.
#[test]
fn a_reused_options_object_yields_a_different_connect_sequence_for_each_transport() -> Result<()> {
    let listener = socket()?;
    let endpoint = CultNetRudpEndpoint { host: "127.0.0.1".into(), port: listener.local_addr()?.port() };
    let options = CultMeshRudpSocketOptions::default();
    let mut sequences = BTreeSet::new();
    for _ in 0..8 {
        let mut client =
            CultMesh::create_rudp_client("client", CONNECTION_ID, &endpoint, options.clone())?;
        client.connect(Vec::new())?;
        let mut buffer = vec![0_u8; 65_535];
        let (received, _) = listener.recv_from(&mut buffer)?;
        sequences.insert(decode_rudp_packet(&buffer[..received])?.sequence);
    }
    assert_eq!(sequences.len(), 8, "two connections opened with one Connect sequence: {sequences:?}");
    Ok(())
}

/// A hub builds a session for every client it admits; each draws its own
/// sequence, so no two clients are accepted with the same one.
#[test]
fn a_hub_accepts_each_client_with_its_own_sequence() -> Result<()> {
    let mut hub = CultNetRudpServerHub::new(CultNetRudpServerHubOptions::new("hub", socket()?, CONNECTION_ID))?;
    let hub_addr = hub.local_addr()?;
    let mut accepts = BTreeSet::new();
    for _ in 0..8 {
        let peer = socket()?;
        let connect = session(1).create_connect(0, Vec::new())?;
        peer.send_to(&encode_rudp_packet(&connect)?, hub_addr)?;
        hub_event_matching(&mut hub, |event| matches!(event, CultNetRudpServerEvent::Connected { .. }))?;
        let mut buffer = vec![0_u8; 65_535];
        let (received, _) = peer.recv_from(&mut buffer)?;
        accepts.insert(decode_rudp_packet(&buffer[..received])?.sequence);
    }
    assert_eq!(accepts.len(), 8, "two clients were accepted with one sequence: {accepts:?}");
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
    options.initial_sequence = Some(initial_sequence);
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
    let mut first = client_on(shared, hub_addr, 50_000)?;
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
    let mut first = client_on(shared, server_addr, 50_000)?;
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

/// An acknowledgement that names only `sequence`.
fn ack_naming(sequence: u32) -> CultNetRudpPacket {
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

/// A connected pair with one sequence the receiver will never see, so every
/// later ordered frame waits behind it. The sender's flow window would stop it
/// 1,023 sequences above its lowest unacked packet, so the missing one is
/// acknowledged by hand (the receiver never saw it) and the rest by what the
/// receiver has: the receiver's hold buffer is what this probes, not the sender.
fn held_behind_a_gap(
    frames: usize,
    channel_of: impl Fn(usize) -> String,
) -> Result<Result<usize>> {
    let mut sender = session(1);
    let mut receiver = session(500);
    handshake(&mut sender, &mut receiver)?;
    let missing = sender.send("gap", b"lost".to_vec(), ordered())?;
    sender.receive(&ack_naming(missing.sequence), 1)?;
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

// Ack Cut 1d: a frame from an earlier generation is a duplicate, a delayed
// Connect from an earlier attempt is stale, a Connect from a new endpoint is a
// new client.

/// Whether an acknowledgement names `sequence` by its ack field or its mask.
fn names_sequence(ack: &CultNetRudpPacket, sequence: u32) -> bool {
    ack.ack == sequence
        || (0..32).any(|bit| {
            ack.ack_mask & (1_u32 << bit) != 0 && ack.ack > bit && ack.ack - bit - 1 == sequence
        })
}

/// A frame the client's old generation already delivered, retransmitted after
/// the client reconnected, is acknowledged and not delivered into the new
/// generation. Every sequence the old generation issued is below the new
/// Connect.
#[test]
fn a_frame_from_the_clients_earlier_generation_is_acknowledged_not_delivered_again() -> Result<()> {
    let mut client = session(10);
    let mut server = session(500);
    handshake(&mut client, &mut server)?;
    let old = send(&mut client, "old")?;
    assert_eq!(names(&server.receive(&old, 1)?.delivered), ["old"]);

    let connect = client.create_connect(2, Vec::new())?;
    let accept = server.accept_connect(&connect, 2, Vec::new())?;
    client.receive(&accept, 2)?;
    assert!(old.sequence < connect.sequence);
    let late = server.receive(&old, 3)?;
    assert!(late.delivered.is_empty(), "an earlier generation's frame was delivered again");
    let ack = server.create_ack_for_received(old.sequence);
    assert!(names_sequence(&ack, old.sequence), "the duplicate was not acknowledged: {ack:?}");

    let fresh = send(&mut client, "fresh")?;
    assert_eq!(names(&server.receive(&fresh, 4)?.delivered), ["fresh"]);
    Ok(())
}

/// The other direction: the server's frame delivered before the client
/// reconnected is not delivered to the client's new generation.
#[test]
fn a_frame_from_the_servers_earlier_generation_is_acknowledged_not_delivered_again() -> Result<()> {
    let mut client = session(10);
    let mut server = session(500);
    handshake(&mut client, &mut server)?;
    let old = send(&mut server, "old")?;
    assert_eq!(names(&client.receive(&old, 1)?.delivered), ["old"]);

    let connect = client.create_connect(2, Vec::new())?;
    let accept = server.accept_connect(&connect, 2, Vec::new())?;
    client.receive(&accept, 2)?;
    let late = client.receive(&old, 3)?;
    assert!(late.delivered.is_empty(), "an earlier generation's frame was delivered again");
    assert!(names_sequence(&client.create_ack_for_received(old.sequence), old.sequence));

    let fresh = send(&mut server, "fresh")?;
    assert_eq!(names(&client.receive(&fresh, 4)?.delivered), ["fresh"]);
    Ok(())
}

/// A frame far below the receiver's highest sequence is a duplicate that is
/// acknowledged by name; without the acknowledgement its sender retransmits it
/// for ever.
#[test]
fn a_duplicate_below_the_receive_window_is_acknowledged_by_name() -> Result<()> {
    let mut client = session(10);
    let mut server = session(500);
    handshake(&mut client, &mut server)?;
    let first = send(&mut client, "first")?;
    assert_eq!(names(&server.receive(&first, 1)?.delivered), ["first"]);
    for _ in 0..4_200 {
        let packet = send(&mut client, "x")?;
        server.receive(&packet, 1)?;
        client.receive(&server.create_ack(), 1)?;
    }
    let again = server.receive(&first, 2)?;
    assert!(again.delivered.is_empty());
    let ack = server.create_ack_for_received(first.sequence);
    assert_eq!(ack.ack, first.sequence, "a duplicate below the window was not acknowledged by name");
    Ok(())
}

/// A Connect delayed from an earlier attempt of the client that is now
/// connected does not restart the session: the server answers with an Ack and
/// the client's ordered frames keep flowing.
#[test]
fn a_delayed_connect_from_an_earlier_attempt_does_not_strand_the_client() -> Result<()> {
    let mut client = session(10);
    let mut server = session(500);
    let earlier = client.create_connect(0, Vec::new())?;
    let retried = client.create_connect(300, Vec::new())?;
    let accept = server.accept_connect(&retried, 301, Vec::new())?;
    client.receive(&accept, 302)?;
    server.receive(&client.create_ack_for_received(accept.sequence), 302)?;
    assert!(client.connected());
    let a = send(&mut client, "a")?;
    assert_eq!(names(&server.receive(&a, 303)?.delivered), ["a"]);
    client.receive(&server.create_ack_for_received(a.sequence), 303)?;

    assert!(server.connect_repeats(&earlier), "the delayed Connect starts nothing");
    let reply = server.accept_connect(&earlier, 304, Vec::new())?;
    assert_eq!(reply.packet_type, CultNetRudpPacketType::Ack);
    client.receive(&reply, 305)?;
    assert!(client.connected());

    let b = send(&mut client, "b")?;
    assert_eq!(names(&server.receive(&b, 306)?.delivered), ["b"], "the delayed Connect reset the server");
    client.receive(&server.create_ack_for_received(b.sequence), 307)?;
    assert!(!client.pending_reliable_sequences().contains(&b.sequence));
    Ok(())
}

/// A stale Connect is the peer's echo from the past, not evidence it is alive;
/// a retransmit of the accepted Connect is.
#[test]
fn a_repeated_connect_refreshes_liveness_and_a_stale_one_does_not() -> Result<()> {
    let mut client = session(10);
    let mut server = session(500);
    let earlier = client.create_connect(0, Vec::new())?;
    let current = client.create_connect(1, Vec::new())?;
    server.accept_connect(&current, 0, Vec::new())?;

    server.accept_connect(&current, 900, Vec::new())?;
    assert!(!server.check_timeout(1_000, 500), "a repeated Connect did not refresh liveness");

    server.accept_connect(&earlier, 1_400, Vec::new())?;
    assert!(server.check_timeout(1_600, 500), "a stale Connect refreshed liveness");
    Ok(())
}

/// The window is serial: a sequence just before the current Connect's is stale
/// across the wrap of the 32-bit space, one just after is a new client.
#[test]
fn stale_connects_are_recognised_by_serial_arithmetic() -> Result<()> {
    let mut server = session(500);
    let current = session(3).create_connect(0, Vec::new())?;
    server.accept_connect(&current, 0, Vec::new())?;
    let before_the_wrap = session(u32::MAX - 1).create_connect(0, Vec::new())?;
    assert!(server.connect_repeats(&before_the_wrap));
    let after = session(4).create_connect(0, Vec::new())?;
    assert!(!server.connect_repeats(&after));
    let beyond_the_window = session(3 + 4_096).create_connect(0, Vec::new())?;
    assert!(!server.connect_repeats(&beyond_the_window));
    let just_inside = session(3_u32.wrapping_sub(4_095)).create_connect(0, Vec::new())?;
    assert!(server.connect_repeats(&just_inside));
    let just_outside = session(3_u32.wrapping_sub(4_096)).create_connect(0, Vec::new())?;
    assert!(!server.connect_repeats(&just_outside));
    Ok(())
}

/// A restarted client whose random first sequence lands just before the
/// server's current Connect is answered with an Ack, never an Accept. It does
/// not retransmit that Connect for ever: once the attempt times out it starts
/// a fresh one with a newly drawn sequence, and is admitted.
#[test]
fn a_restarted_client_whose_first_sequence_is_stale_connects_after_redrawing() -> Result<()> {
    let mut server = session(500);
    let mut old = session(1_000);
    let connect = old.create_connect(0, Vec::new())?;
    let old_accept = server.accept_connect(&connect, 0, Vec::new())?;
    old.receive(&old_accept, 0)?;

    let mut restarted = session(900);
    let first = restarted.create_connect(0, b"join".to_vec())?;
    // The old client has not acknowledged its Accept yet, so that is the reply:
    // it names the old Connect, not this one.
    let reply = server.accept_connect(&first, 1, Vec::new())?;
    assert_eq!(reply.packet_type, CultNetRudpPacketType::Accept);
    restarted.receive(&reply, 1)?;
    assert!(!restarted.connected(), "an Accept for another Connect connected the client");
    server.receive(&old.create_ack_for_received(old_accept.sequence), 1)?;
    let reply = server.accept_connect(&first, 2, Vec::new())?;
    assert_eq!(reply.packet_type, CultNetRudpPacketType::Ack);
    restarted.receive(&reply, 2)?;
    assert!(!restarted.connected());

    let retransmitted = restarted.due_resends(1_000);
    assert_eq!(retransmitted.len(), 1);
    assert_eq!(retransmitted[0].sequence, first.sequence, "the attempt is still young");

    let fresh = restarted.due_resends(3_500);
    assert_eq!(fresh.len(), 1);
    assert_eq!(fresh[0].packet_type, CultNetRudpPacketType::Connect);
    assert_ne!(fresh[0].sequence, first.sequence, "the same Connect was retransmitted for ever");
    assert_eq!(fresh[0].payload, b"join", "the fresh attempt lost the Connect payload");

    let accept = server.accept_connect(&fresh[0], 3_500, Vec::new())?;
    assert_eq!(accept.packet_type, CultNetRudpPacketType::Accept);
    restarted.receive(&accept, 3_500)?;
    assert!(restarted.connected());
    let frame = send(&mut restarted, "hello")?;
    assert_eq!(names(&server.receive(&frame, 3_501)?.delivered), ["hello"]);
    Ok(())
}

/// A client whose Connect was answered is not given a new Connect by the
/// clock: the attempt timeout belongs to an unanswered Connect.
#[test]
fn an_answered_connect_is_never_replaced_by_the_attempt_timeout() -> Result<()> {
    let mut client = session(10);
    let mut server = session(500);
    let (connect, _) = handshake(&mut client, &mut server)?;
    let resent = client.due_resends(60_000);
    assert!(
        resent.iter().all(|packet| packet.packet_type != CultNetRudpPacketType::Connect
            || packet.sequence == connect.sequence),
        "a connected client started a new attempt"
    );
    assert!(client.connected());
    Ok(())
}

/// A pinned client that restarts on a new port against a server-mode transport
/// is a new client: the Connect starts a new generation and the client is
/// admitted, though its sequence is the one the old client's Connect carried.
#[test]
fn server_mode_admits_a_pinned_client_that_restarts_on_a_new_port() -> Result<()> {
    let server_socket = socket()?;
    let server_addr = server_socket.local_addr()?;
    let mut server = CultNetRudpSocketTransportConnection::new(CultNetRudpSocketTransportOptions::server(
        "server",
        server_socket,
        CONNECTION_ID,
    ))?;
    let mut first = client_on(socket()?, server_addr, 1)?;
    first.connect(b"join".to_vec())?;
    assert!(server.receive_once()?.is_none());
    first.receive_once()?;
    first.send("schema", b"hello".to_vec())?;
    assert_eq!(server.receive_once()?.expect("first client frame").payload, b"hello");
    drop(first);

    let mut second = client_on(socket()?, server_addr, 1)?;
    second.connect(b"join".to_vec())?;
    assert!(server.receive_once()?.is_none());
    second.receive_once()?;
    assert!(second.connected(), "the restarted client on a new port was not admitted");
    second.send("schema", b"after".to_vec())?;
    assert_eq!(server.receive_once()?.expect("restarted client frame").payload, b"after");
    Ok(())
}

/// An accepted peer that never speaks again times out: accepting a Connect is
/// the first thing heard from it.
#[test]
fn an_accepted_peer_that_goes_silent_times_out() -> Result<()> {
    let mut server = session(500);
    server.accept_connect(&session(10).create_connect(0, Vec::new())?, 0, Vec::new())?;
    assert!(server.check_timeout(100_000, 1_000));
    Ok(())
}

// Ack Cut 1d, batch 2: the connect-attempt timeout.

/// A server that keeps a session answers a client's repeated or stale Connect
/// with an Ack that may name the client's pending Connect. That Ack says no
/// session started: only an Accept the client honours retires its Connect, so
/// the attempt still times out and is replaced.
#[test]
fn an_ack_naming_the_pending_connect_does_not_retire_it() -> Result<()> {
    let mut server = session(500);
    let mut old = session(1);
    let old_accept = server.accept_connect(&old.create_connect(0, Vec::new())?, 0, Vec::new())?;
    old.receive(&old_accept, 0)?;
    server.receive(&old.create_ack_for_received(old_accept.sequence), 0)?;

    let mut restarted = session(1);
    let first = restarted.create_connect(0, b"join".to_vec())?;
    let reply = server.accept_connect(&first, 1, Vec::new())?;
    assert_eq!(reply.packet_type, CultNetRudpPacketType::Ack);
    assert!(names_sequence(&reply, first.sequence), "the Ack must name the pending Connect for this test to bite");
    restarted.receive(&reply, 1)?;
    assert!(!restarted.connected());
    assert_eq!(restarted.pending_reliable_sequences(), vec![first.sequence], "an Ack retired the Connect");

    let fresh = restarted.due_resends(3_000);
    assert_eq!(fresh.len(), 1, "the client waits for ever with nothing to resend");
    assert_eq!(fresh[0].payload, b"join");
    let accept = server.accept_connect(&fresh[0], 3_000, Vec::new())?;
    assert_eq!(accept.packet_type, CultNetRudpPacketType::Accept);
    restarted.receive(&accept, 3_000)?;
    assert!(restarted.connected());
    Ok(())
}

/// The fresh attempt is the abandoned sequence plus the receive window less
/// one, so a late copy of the abandoned Connect is stale for the server that
/// took the fresh one: answered with an Ack, no restart, no stranding.
#[test]
fn a_late_copy_of_an_abandoned_connect_is_stale_once_its_replacement_is_accepted() -> Result<()> {
    let mut server = session(500);
    let mut client = session(10);
    let abandoned = client.create_connect(0, Vec::new())?;
    let fresh = client.due_resends(3_000);
    assert_eq!(fresh.len(), 1);
    assert_eq!(fresh[0].sequence, abandoned.sequence + 4_095);
    let accept = server.accept_connect(&fresh[0], 3_001, Vec::new())?;
    client.receive(&accept, 3_002)?;
    server.receive(&client.create_ack_for_received(accept.sequence), 3_002)?;
    assert!(client.connected());
    let a = send(&mut client, "a")?;
    assert_eq!(names(&server.receive(&a, 3_003)?.delivered), ["a"]);
    client.receive(&server.create_ack_for_received(a.sequence), 3_003)?;

    assert!(server.connect_repeats(&abandoned), "the late copy would restart the server");
    let reply = server.accept_connect(&abandoned, 3_004, Vec::new())?;
    assert_eq!(reply.packet_type, CultNetRudpPacketType::Ack);
    client.receive(&reply, 3_005)?;
    assert!(client.connected());
    let b = send(&mut client, "b")?;
    assert_eq!(names(&server.receive(&b, 3_006)?.delivered), ["b"]);
    Ok(())
}

/// A server whose generation sits exactly where the fresh attempt lands takes
/// it for a repeat; the attempt after that leaves the window and is admitted.
#[test]
fn a_server_sitting_at_the_jump_is_left_by_the_next_attempt() -> Result<()> {
    let mut server = session(500);
    let mut old = session(5_095);
    let old_accept = server.accept_connect(&old.create_connect(0, Vec::new())?, 0, Vec::new())?;
    old.receive(&old_accept, 0)?;
    server.receive(&old.create_ack_for_received(old_accept.sequence), 0)?;

    let mut restarted = session(1_000);
    let first = restarted.create_connect(0, b"join".to_vec())?;
    restarted.receive(&server.accept_connect(&first, 1, Vec::new())?, 1)?;
    assert!(!restarted.connected());

    let second = restarted.due_resends(3_000).remove(0);
    assert_eq!(second.sequence, 5_095, "the jump lands on the server's generation");
    let reply = server.accept_connect(&second, 3_001, Vec::new())?;
    assert_eq!(reply.packet_type, CultNetRudpPacketType::Ack);
    restarted.receive(&reply, 3_001)?;
    assert!(!restarted.connected());

    let third = restarted.due_resends(6_000).remove(0);
    assert_eq!(third.sequence, 5_095 + 4_095);
    let accept = server.accept_connect(&third, 6_001, Vec::new())?;
    assert_eq!(accept.packet_type, CultNetRudpPacketType::Accept);
    restarted.receive(&accept, 6_001)?;
    assert!(restarted.connected());
    Ok(())
}

fn receive_packet(socket: &UdpSocket) -> Result<CultNetRudpPacket> {
    let mut buffer = vec![0_u8; 65_535];
    let (received, _) = socket.recv_from(&mut buffer)?;
    decode_rudp_packet(&buffer[..received])
}

/// A pinned client that restarts on the same address is a repeat by sequence,
/// so the hub answers with an Ack; the client's attempt times out, and the
/// fresh Connect (another sequence) starts a new generation the hub admits.
#[test]
fn the_hub_admits_a_pinned_client_that_restarts_on_the_same_address() -> Result<()> {
    let mut hub = CultNetRudpServerHub::new(CultNetRudpServerHubOptions::new("hub", socket()?, CONNECTION_ID))?;
    let hub_addr = hub.local_addr()?;
    let peer = socket()?;
    let mut first = session(1);
    peer.send_to(&encode_rudp_packet(&first.create_connect(0, b"join".to_vec())?)?, hub_addr)?;
    hub_event_matching(&mut hub, |event| matches!(event, CultNetRudpServerEvent::Connected { .. }))?;
    let accept = receive_packet(&peer)?;
    first.receive(&accept, 0)?;
    peer.send_to(&encode_rudp_packet(&first.create_ack_for_received(accept.sequence))?, hub_addr)?;
    hub.receive_event_once()?;

    let mut restarted = session(1);
    peer.send_to(&encode_rudp_packet(&restarted.create_connect(0, b"join".to_vec())?)?, hub_addr)?;
    hub.receive_event_once()?;
    restarted.receive(&receive_packet(&peer)?, 1)?;
    assert!(!restarted.connected());

    let fresh = restarted.due_resends(3_000).remove(0);
    peer.send_to(&encode_rudp_packet(&fresh)?, hub_addr)?;
    hub_event_matching(&mut hub, |event| matches!(event, CultNetRudpServerEvent::Connected { .. }))?;
    restarted.receive(&receive_packet(&peer)?, 3_001)?;
    assert!(restarted.connected(), "the restarted pinned client was never admitted");
    Ok(())
}
