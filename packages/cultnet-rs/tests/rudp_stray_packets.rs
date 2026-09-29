//! A packet that does not belong to a session is dropped; only socket failures
//! end a transport loop. Each test puts one class of stray in front of a real
//! session and requires the real session to keep being served.

use std::net::{SocketAddr, UdpSocket};
use std::time::Duration;

use anyhow::Result;
use cultnet_rs::*;

const CONNECTION_ID: u32 = 0x4355_4c54;
const FOREIGN_ID: u32 = 0x0BAD_F10E;

#[derive(Clone, Copy)]
enum Stray {
    /// Bytes that are not a RUDP frame.
    Malformed,
    /// A well-formed session that carries someone else's connection id: what a
    /// moved UDP flow delivers.
    ForeignConnectionId,
    /// The right connection id from a sender no Connect ever admitted, or one
    /// that already closed.
    UnadmittedSender,
}

fn socket() -> Result<UdpSocket> {
    let socket = UdpSocket::bind("127.0.0.1:0")?;
    socket.set_read_timeout(Some(Duration::from_millis(20)))?;
    Ok(socket)
}

fn raw_session(connection_id: u32) -> CultNetRudpSession {
    CultNetRudpSession::new(CultNetRudpSessionOptions {
        connection_id,
        initial_sequence: 1,
        resend_delay_ms: 250,
        max_pending_reliable_packets: None,
    })
}

fn reliable() -> CultNetRudpSendOptions {
    CultNetRudpSendOptions {
        reliable: true,
        ..Default::default()
    }
}

fn send_to(socket: &UdpSocket, to: SocketAddr, packet: &CultNetRudpPacket) -> Result<()> {
    socket.send_to(&encode_rudp_packet(packet)?, to)?;
    Ok(())
}

/// How many datagrams `send_stray` puts on the wire for `kind`.
fn datagrams(kind: Stray) -> u64 {
    match kind {
        Stray::Malformed => 2,
        Stray::ForeignConnectionId => 4,
        Stray::UnadmittedSender => 3,
    }
}

/// A packet the sender's own session accepted for sending and its receiver's
/// session refuses: a fragment with no fragment id.
fn poisoned(session: &mut CultNetRudpSession) -> Result<CultNetRudpPacket> {
    let mut packet = session.send("schema", b"poison".to_vec(), reliable())?;
    packet.fragment_count = 2;
    packet.fragment_id = 0;
    Ok(packet)
}

fn send_stray(kind: Stray, from: &UdpSocket, to: SocketAddr) -> Result<()> {
    match kind {
        Stray::Malformed => {
            from.send_to(b"not a rudp packet", to)?;
            from.send_to(&[], to)?;
        }
        Stray::ForeignConnectionId => {
            let mut foreign = raw_session(FOREIGN_ID);
            send_to(from, to, &foreign.create_connect(0, b"foreign".to_vec())?)?;
            foreign.assume_connected(0);
            send_to(from, to, &foreign.send("schema", b"foreign".to_vec(), reliable())?)?;
            send_to(from, to, &foreign.create_ack())?;
            send_to(from, to, &foreign.create_disconnect(b"foreign".to_vec()))?;
        }
        Stray::UnadmittedSender => {
            let mut orphan = raw_session(CONNECTION_ID);
            orphan.assume_connected(0);
            send_to(from, to, &orphan.send("schema", b"orphan".to_vec(), reliable())?)?;
            send_to(from, to, &orphan.create_ack())?;
            send_to(from, to, &orphan.create_disconnect(b"orphan".to_vec()))?;
        }
    }
    Ok(())
}

fn hub_keeps_serving_its_session(kind: Stray) -> Result<()> {
    let mut options = CultNetRudpServerHubOptions::new("hub", socket()?, CONNECTION_ID);
    options.max_peers = 1;
    let mut hub = CultNetRudpServerHub::new(options)?;
    let hub_addr = hub.local_addr()?;
    let mut real = CultNetRudpSocketTransportConnection::new(
        CultNetRudpSocketTransportOptions::client("real", socket()?, hub_addr, CONNECTION_ID),
    )?;
    real.connect(b"real".to_vec())?;
    let mut admitted = false;
    for _ in 0..20 {
        if let Some(CultNetRudpServerEvent::Connected { .. }) = hub.receive_event_once()? {
            admitted = true;
            break;
        }
    }
    assert!(admitted);
    let _ = real.receive_once()?;

    let stray = socket()?;
    send_stray(kind, &stray, hub_addr)?;
    std::thread::sleep(Duration::from_millis(20));
    while hub.receive_event_once()?.is_some() {}
    assert_eq!(hub.stats().packets_dropped, datagrams(kind), "every stray is counted once");

    real.send("schema", b"still here".to_vec())?;
    std::thread::sleep(Duration::from_millis(20));
    match hub.receive_event_once()? {
        Some(CultNetRudpServerEvent::Frame { frame, .. }) => {
            assert_eq!(frame.payload, b"still here");
        }
        other => panic!("the real session must still be served, got {other:?}"),
    }
    Ok(())
}

#[test]
fn hub_drops_a_connect_that_arrives_with_the_peer_table_full() -> Result<()> {
    let mut options = CultNetRudpServerHubOptions::new("hub", socket()?, CONNECTION_ID);
    options.max_peers = 1;
    let mut hub = CultNetRudpServerHub::new(options)?;
    let hub_addr = hub.local_addr()?;
    let mut real = CultNetRudpSocketTransportConnection::new(
        CultNetRudpSocketTransportOptions::client("real", socket()?, hub_addr, CONNECTION_ID),
    )?;
    real.connect(b"real".to_vec())?;
    while !matches!(
        hub.receive_event_once()?,
        Some(CultNetRudpServerEvent::Connected { .. })
    ) {}
    let _ = real.receive_once()?;

    send_to(
        &socket()?,
        hub_addr,
        &raw_session(CONNECTION_ID).create_connect(0, b"late".to_vec())?,
    )?;
    std::thread::sleep(Duration::from_millis(20));
    while hub.receive_event_once()?.is_some() {}
    assert_eq!(hub.sessions().len(), 1);
    assert_eq!(hub.stats().packets_dropped, 1);

    real.send("schema", b"still here".to_vec())?;
    std::thread::sleep(Duration::from_millis(20));
    assert!(matches!(
        hub.receive_event_once()?,
        Some(CultNetRudpServerEvent::Frame { .. })
    ));
    Ok(())
}

/// A moved flow answers a client from its server address with someone else's
/// session.
fn client_keeps_its_session(kind: Stray) -> Result<()> {
    let server = socket()?;
    let server_addr = server.local_addr()?;
    let mut client = CultNetRudpSocketTransportConnection::new(
        CultNetRudpSocketTransportOptions::client("client", socket()?, server_addr, CONNECTION_ID),
    )?;
    client.connect(b"hello".to_vec())?;
    let mut buffer = vec![0_u8; 65_535];
    let (received, client_addr) = server.recv_from(&mut buffer)?;
    let connect = decode_rudp_packet(&buffer[..received])?;
    let mut server_session = raw_session(CONNECTION_ID);
    let accept = server_session.accept_connect(&connect, 0, Vec::new())?;
    send_to(&server, client_addr, &accept)?;
    let _ = client.receive_once()?;
    assert!(client.connected());

    send_stray(kind, &server, client_addr)?;
    send_to(
        &server,
        client_addr,
        &server_session.send("schema", b"real".to_vec(), reliable())?,
    )?;
    let frame = client
        .receive_once()?
        .expect("the real frame behind the strays");
    assert_eq!(frame.payload, b"real");
    assert_eq!(client.stats().packets_dropped, datagrams(kind), "every stray is counted once");
    Ok(())
}

/// A server-mode transport owns one peer. A stray, including a foreign Connect
/// from another address, neither resets that peer's session nor moves the
/// endpoint the transport answers.
fn server_mode_keeps_its_peer(kind: Stray) -> Result<()> {
    let server_socket = socket()?;
    let server_addr = server_socket.local_addr()?;
    let mut server = CultNetRudpSocketTransportConnection::new(
        CultNetRudpSocketTransportOptions::server("server", server_socket, CONNECTION_ID),
    )?;
    let peer = socket()?;
    let mut peer_session = raw_session(CONNECTION_ID);
    send_to(&peer, server_addr, &peer_session.create_connect(0, b"peer".to_vec())?)?;
    let _ = server.receive_once()?;
    let mut buffer = vec![0_u8; 65_535];
    let (received, _) = peer.recv_from(&mut buffer)?;
    peer_session.receive(&decode_rudp_packet(&buffer[..received])?, 0)?;

    send_stray(kind, &socket()?, server_addr)?;
    send_stray(kind, &peer, server_addr)?;
    send_to(
        &peer,
        server_addr,
        &peer_session.send("schema", b"real".to_vec(), reliable())?,
    )?;
    let frame = server
        .receive_once()?
        .expect("the real frame behind the strays");
    assert_eq!(frame.payload, b"real");
    // Once from an unrelated address, once from the peer's own.
    assert_eq!(server.stats().packets_dropped, 2 * datagrams(kind), "every stray is counted once");
    Ok(())
}

macro_rules! per_stray {
    ($body:ident, $malformed:ident, $foreign:ident, $unadmitted:ident) => {
        #[test]
        fn $malformed() -> Result<()> {
            $body(Stray::Malformed)
        }
        #[test]
        fn $foreign() -> Result<()> {
            $body(Stray::ForeignConnectionId)
        }
        #[test]
        fn $unadmitted() -> Result<()> {
            $body(Stray::UnadmittedSender)
        }
    };
    ($body:ident, $malformed:ident, $foreign:ident) => {
        #[test]
        fn $malformed() -> Result<()> {
            $body(Stray::Malformed)
        }
        #[test]
        fn $foreign() -> Result<()> {
            $body(Stray::ForeignConnectionId)
        }
    };
}

per_stray!(
    hub_keeps_serving_its_session,
    hub_survives_malformed_frames,
    hub_survives_foreign_connection_ids,
    hub_survives_unadmitted_senders
);
per_stray!(
    client_keeps_its_session,
    client_survives_malformed_frames,
    client_survives_foreign_connection_ids
);
per_stray!(
    server_mode_keeps_its_peer,
    server_mode_survives_malformed_frames,
    server_mode_survives_foreign_connection_ids
);

/// A session that refuses a packet has already recorded its reliable sequence,
/// so keeping it would acknowledge the sender's retransmit and lose the frame.
/// The session ends and the sender is told.
fn refused_frame_options() -> (u32, Vec<u8>) {
    (64, vec![7_u8; 100])
}

fn client_with_fast_resend(server_addr: SocketAddr) -> Result<CultNetRudpSocketTransportConnection> {
    let mut options =
        CultNetRudpSocketTransportOptions::client("client", socket()?, server_addr, CONNECTION_ID);
    options.resend_delay_ms = 1;
    CultNetRudpSocketTransportConnection::new(options)
}

/// The client's view after the server refused its frame: told the session is
/// over, and never shown the frame as acknowledged, even after it retransmits.
fn assert_client_sees_refusal(
    client: &mut CultNetRudpSocketTransportConnection,
    receipt: &CultNetRudpReliableSendReceipt,
    mut serve: impl FnMut() -> Result<()>,
) -> Result<()> {
    serve()?;
    let _ = client.receive_once()?;
    assert!(
        client.disconnect_reason().is_some(),
        "the client must be told the session ended"
    );
    std::thread::sleep(Duration::from_millis(10));
    client.poll_resends()?;
    serve()?;
    let _ = client.receive_once()?;
    assert_ne!(
        client.reliable_send_status(receipt),
        CultNetRudpReliableSendStatus::Acknowledged,
        "a refused frame must never be acknowledged"
    );
    Ok(())
}

#[test]
fn hub_ends_the_session_of_a_peer_whose_reliable_frame_it_refuses() -> Result<()> {
    let (limit, payload) = refused_frame_options();
    let mut options = CultNetRudpServerHubOptions::new("hub", socket()?, CONNECTION_ID);
    options.max_payload_bytes = Some(limit);
    let mut hub = CultNetRudpServerHub::new(options)?;
    let hub_addr = hub.local_addr()?;
    let mut client = client_with_fast_resend(hub_addr)?;
    client.connect(b"peer".to_vec())?;
    assert!(matches!(
        hub.receive_event_once()?,
        Some(CultNetRudpServerEvent::Connected { .. })
    ));
    let _ = client.receive_once()?;
    assert!(client.connected());

    let receipt = client.send_reliable("schema", payload)?;
    let mut ended = false;
    assert_client_sees_refusal(&mut client, &receipt, || {
        while let Some(event) = hub.receive_event_once()? {
            ended |= matches!(event, CultNetRudpServerEvent::Disconnected { .. });
        }
        Ok(())
    })?;
    assert!(ended, "the hub must report the ended session");
    assert_eq!(hub.sessions().len(), 0);
    assert!(hub.stats().packets_dropped >= 1);
    Ok(())
}

#[test]
fn server_mode_ends_the_session_of_a_peer_whose_reliable_frame_it_refuses() -> Result<()> {
    let (limit, payload) = refused_frame_options();
    let server_socket = socket()?;
    let server_addr = server_socket.local_addr()?;
    let mut options = CultNetRudpSocketTransportOptions::server("server", server_socket, CONNECTION_ID);
    options.max_payload_bytes = Some(limit);
    let mut server = CultNetRudpSocketTransportConnection::new(options)?;
    let mut client = client_with_fast_resend(server_addr)?;
    client.connect(b"peer".to_vec())?;
    let _ = server.receive_once()?;
    let _ = client.receive_once()?;
    assert!(client.connected());

    let receipt = client.send_reliable("schema", payload)?;
    assert_client_sees_refusal(&mut client, &receipt, || {
        let _ = server.receive_once()?;
        Ok(())
    })?;
    assert!(server.disconnect_reason().is_some());
    assert!(server.stats().packets_dropped >= 1);
    Ok(())
}

#[test]
fn client_ends_its_session_when_it_refuses_a_packet() -> Result<()> {
    let server = socket()?;
    let server_addr = server.local_addr()?;
    let mut client = CultNetRudpSocketTransportConnection::new(
        CultNetRudpSocketTransportOptions::client("client", socket()?, server_addr, CONNECTION_ID),
    )?;
    client.connect(b"hello".to_vec())?;
    let mut buffer = vec![0_u8; 65_535];
    let (received, client_addr) = server.recv_from(&mut buffer)?;
    let mut server_session = raw_session(CONNECTION_ID);
    let accept =
        server_session.accept_connect(&decode_rudp_packet(&buffer[..received])?, 0, Vec::new())?;
    send_to(&server, client_addr, &accept)?;
    let _ = client.receive_once()?;

    send_to(&server, client_addr, &poisoned(&mut server_session)?)?;
    assert!(client.receive_once()?.is_none());
    assert_eq!(client.stats().packets_dropped, 1);
    assert!(client.disconnect_reason().is_some());

    // The server is told, not left holding a live session.
    let mut told = false;
    while let Ok((received, _)) = server.recv_from(&mut buffer) {
        told |= decode_rudp_packet(&buffer[..received])?.packet_type
            == CultNetRudpPacketType::Disconnect;
    }
    assert!(told, "the server must be told the session ended");
    Ok(())
}

#[test]
fn server_mode_ends_the_session_when_the_session_refuses_a_packet() -> Result<()> {
    let server_socket = socket()?;
    let server_addr = server_socket.local_addr()?;
    let mut server = CultNetRudpSocketTransportConnection::new(
        CultNetRudpSocketTransportOptions::server("server", server_socket, CONNECTION_ID),
    )?;
    let stray = socket()?;

    // Before any peer: a right-id packet that is not a Connect claims nothing.
    send_stray(Stray::UnadmittedSender, &stray, server_addr)?;
    assert!(server.receive_once()?.is_none());
    assert_eq!(server.stats().packets_dropped, 3);

    let peer = socket()?;
    let mut peer_session = raw_session(CONNECTION_ID);
    send_to(&peer, server_addr, &peer_session.create_connect(0, b"peer".to_vec())?)?;
    let _ = server.receive_once()?;
    let mut buffer = vec![0_u8; 65_535];
    let (received, _) = peer.recv_from(&mut buffer)?;
    peer_session.receive(&decode_rudp_packet(&buffer[..received])?, 0)?;
    assert_eq!(server.stats().packets_dropped, 3);

    // After the peer is known: right-id packets from any other address.
    send_stray(Stray::UnadmittedSender, &stray, server_addr)?;
    assert!(server.receive_once()?.is_none());
    assert_eq!(server.stats().packets_dropped, 6);

    // A packet from the peer that the session itself refuses ends the session
    // and the peer is told.
    send_to(&peer, server_addr, &poisoned(&mut peer_session)?)?;
    assert!(server.receive_once()?.is_none());
    assert_eq!(server.stats().packets_dropped, 7);
    assert!(server.disconnect_reason().is_some());
    let (received, _) = peer.recv_from(&mut buffer)?;
    assert_eq!(
        decode_rudp_packet(&buffer[..received])?.packet_type,
        CultNetRudpPacketType::Disconnect
    );
    Ok(())
}

#[test]
fn constructors_reject_limits_that_make_a_connect_unadmittable() -> Result<()> {
    let mut hub = CultNetRudpServerHubOptions::new("hub", socket()?, CONNECTION_ID);
    hub.initial_sequence = u32::MAX;
    assert!(CultNetRudpServerHub::new(hub).is_err());
    let mut hub = CultNetRudpServerHubOptions::new("hub", socket()?, CONNECTION_ID);
    hub.max_pending_reliable_packets = Some(0);
    assert!(CultNetRudpServerHub::new(hub).is_err());

    for build in [
        (|s, a| CultNetRudpSocketTransportOptions::client("c", s, a, CONNECTION_ID))
            as fn(UdpSocket, SocketAddr) -> CultNetRudpSocketTransportOptions,
        |s, _| CultNetRudpSocketTransportOptions::server("s", s, CONNECTION_ID),
    ] {
        let addr = "127.0.0.1:9".parse()?;
        let mut options = build(socket()?, addr);
        options.initial_sequence = u32::MAX;
        assert!(CultNetRudpSocketTransportConnection::new(options).is_err());
        let mut options = build(socket()?, addr);
        options.max_pending_reliable_packets = Some(0);
        assert!(CultNetRudpSocketTransportConnection::new(options).is_err());
        let mut options = build(socket()?, addr);
        options.initial_sequence = u32::MAX - 1;
        options.max_pending_reliable_packets = Some(1);
        assert!(CultNetRudpSocketTransportConnection::new(options).is_ok());
    }
    Ok(())
}

/// The distinct reliable Accept sequences a peer has been sent so far.
fn accept_sequences(peer: &UdpSocket) -> Result<std::collections::BTreeSet<u32>> {
    let mut buffer = vec![0_u8; 65_535];
    let mut sequences = std::collections::BTreeSet::new();
    while let Ok((received, _)) = peer.recv_from(&mut buffer) {
        let packet = decode_rudp_packet(&buffer[..received])?;
        if packet.packet_type == CultNetRudpPacketType::Accept && packet.reliable {
            sequences.insert(packet.sequence);
        }
    }
    Ok(sequences)
}

/// A Connect that repeats (a moved flow, a retransmit, a storm) is answered
/// with the Accept already owed, never a fresh reliable Accept per Connect.
#[test]
fn hub_owes_one_reliable_accept_however_many_connects_repeat() -> Result<()> {
    let mut hub =
        CultNetRudpServerHub::new(CultNetRudpServerHubOptions::new("hub", socket()?, CONNECTION_ID))?;
    let hub_addr = hub.local_addr()?;
    let peer = socket()?;
    let mut peer_session = raw_session(CONNECTION_ID);
    let connect = peer_session.create_connect(0, b"peer".to_vec())?;
    let mut connected = 0;
    for _ in 0..20 {
        send_to(&peer, hub_addr, &connect)?;
        while let Some(event) = hub.receive_event_once()? {
            assert!(
                matches!(event, CultNetRudpServerEvent::Connected { .. }),
                "a repeated Connect must not replace the admitted session"
            );
            connected += 1;
        }
    }
    assert_eq!(connected, 1);
    assert_eq!(accept_sequences(&peer)?.len(), 1);
    Ok(())
}

#[test]
fn server_mode_owes_one_reliable_accept_however_many_connects_repeat() -> Result<()> {
    let server_socket = socket()?;
    let server_addr = server_socket.local_addr()?;
    let mut server = CultNetRudpSocketTransportConnection::new(
        CultNetRudpSocketTransportOptions::server("server", server_socket, CONNECTION_ID),
    )?;
    let peer = socket()?;
    let mut peer_session = raw_session(CONNECTION_ID);
    let connect = peer_session.create_connect(0, b"peer".to_vec())?;
    for _ in 0..20 {
        send_to(&peer, server_addr, &connect)?;
        let _ = server.receive_once()?;
    }
    assert!(server.outstanding_reliable_packet_count() <= 1);
    Ok(())
}

/// A late duplicate Connect resets what the server learned of the peer, never
/// what it already issued: a reused sequence is dropped by the peer as a
/// duplicate.
#[test]
fn server_mode_never_reuses_a_sequence_after_a_late_duplicate_connect() -> Result<()> {
    let server_socket = socket()?;
    let server_addr = server_socket.local_addr()?;
    let mut server = CultNetRudpSocketTransportConnection::new(
        CultNetRudpSocketTransportOptions::server("server", server_socket, CONNECTION_ID),
    )?;
    let peer = socket()?;
    let mut peer_session = raw_session(CONNECTION_ID);
    let connect = peer_session.create_connect(0, b"peer".to_vec())?;
    send_to(&peer, server_addr, &connect)?;
    let _ = server.receive_once()?;
    server.send_reliable("schema", b"before".to_vec())?;
    send_to(&peer, server_addr, &connect)?;
    let _ = server.receive_once()?;
    server.send_reliable("schema", b"after".to_vec())?;

    let mut buffer = vec![0_u8; 65_535];
    let mut delivered = Vec::new();
    while let Ok((received, _)) = peer.recv_from(&mut buffer) {
        let result = peer_session.receive(&decode_rudp_packet(&buffer[..received])?, 0)?;
        delivered.extend(result.delivered.into_iter().map(|frame| frame.payload));
    }
    assert!(delivered.contains(&b"before".to_vec()));
    assert!(
        delivered.contains(&b"after".to_vec()),
        "the frame sent after the duplicate Connect was dropped as a reused sequence"
    );
    Ok(())
}

/// A session that ended forgot its unacknowledged writes; a flush that then
/// reports success would claim writes the peer never acknowledged.
#[test]
fn a_write_unacknowledged_when_the_session_is_refused_fails_its_flush() -> Result<()> {
    let server = socket()?;
    let server_addr = server.local_addr()?;
    let mut client = CultNetRudpSocketTransportConnection::new(
        CultNetRudpSocketTransportOptions::client("client", socket()?, server_addr, CONNECTION_ID),
    )?;
    client.connect(b"hello".to_vec())?;
    let mut buffer = vec![0_u8; 65_535];
    let (received, client_addr) = server.recv_from(&mut buffer)?;
    let mut server_session = raw_session(CONNECTION_ID);
    let accept =
        server_session.accept_connect(&decode_rudp_packet(&buffer[..received])?, 0, Vec::new())?;
    send_to(&server, client_addr, &accept)?;
    let _ = client.receive_once()?;

    let receipt = client.send_reliable("schema", b"never acknowledged".to_vec())?;
    send_to(&server, client_addr, &poisoned(&mut server_session)?)?;
    let _ = client.receive_once()?;
    assert!(client.disconnect_reason().is_some());

    let flushed = client.flush_reliable(Duration::from_millis(200));
    assert!(flushed.is_err(), "the write was never acknowledged");
    assert_eq!(
        client.reliable_send_status(&receipt),
        CultNetRudpReliableSendStatus::Invalidated
    );
    Ok(())
}

/// A peer's Disconnect ends the session as surely as a refusal: writes it never
/// acknowledged are lost, and the transport says so.
#[test]
fn a_write_unacknowledged_when_the_peer_disconnects_fails_its_flush() -> Result<()> {
    let server = socket()?;
    let server_addr = server.local_addr()?;
    let mut client = CultNetRudpSocketTransportConnection::new(
        CultNetRudpSocketTransportOptions::client("client", socket()?, server_addr, CONNECTION_ID),
    )?;
    client.connect(b"hello".to_vec())?;
    let mut buffer = vec![0_u8; 65_535];
    let (received, client_addr) = server.recv_from(&mut buffer)?;
    let mut server_session = raw_session(CONNECTION_ID);
    let accept =
        server_session.accept_connect(&decode_rudp_packet(&buffer[..received])?, 0, Vec::new())?;
    send_to(&server, client_addr, &accept)?;
    let _ = client.receive_once()?;

    let receipt = client.send_reliable("schema", b"never acknowledged".to_vec())?;
    send_to(&server, client_addr, &server_session.create_disconnect(b"bye".to_vec()))?;
    let _ = client.receive_once()?;

    assert!(client.flush_reliable(Duration::from_millis(200)).is_err());
    assert_eq!(
        client.reliable_send_status(&receipt),
        CultNetRudpReliableSendStatus::Invalidated
    );
    Ok(())
}

/// Drains what a raw peer socket has been sent.
fn drain(socket: &UdpSocket) -> Result<Vec<CultNetRudpPacket>> {
    let mut buffer = vec![0_u8; 65_535];
    let mut packets = Vec::new();
    while let Ok((received, _)) = socket.recv_from(&mut buffer) {
        packets.push(decode_rudp_packet(&buffer[..received])?);
    }
    Ok(packets)
}

/// A receipt belongs to the session it was issued in. A peer's Disconnect ends
/// that session for good: reconnecting must not bring the write back to Pending,
/// and an ack the old peer sent late must not make it Acknowledged.
#[test]
fn a_receipt_invalidated_by_a_peer_disconnect_never_becomes_acknowledged() -> Result<()> {
    let server = socket()?;
    let server_addr = server.local_addr()?;
    let mut client = CultNetRudpSocketTransportConnection::new(
        CultNetRudpSocketTransportOptions::client("client", socket()?, server_addr, CONNECTION_ID),
    )?;
    client.connect(b"hello".to_vec())?;
    let mut buffer = vec![0_u8; 65_535];
    let (received, client_addr) = server.recv_from(&mut buffer)?;
    let mut old_peer = raw_session(CONNECTION_ID);
    let accept = old_peer.accept_connect(&decode_rudp_packet(&buffer[..received])?, 0, Vec::new())?;
    send_to(&server, client_addr, &accept)?;
    let _ = client.receive_once()?;

    let receipt = client.send_reliable("schema", b"written before the disconnect".to_vec())?;
    let data = drain(&server)?
        .into_iter()
        .find(|packet| packet.packet_type == CultNetRudpPacketType::Data)
        .expect("the write reached the peer");
    old_peer.receive(&data, 0)?;
    let late_ack = old_peer.create_ack_for_received(data.sequence);
    send_to(&server, client_addr, &old_peer.create_disconnect(b"bye".to_vec()))?;
    let _ = client.receive_once()?;
    assert_eq!(client.reliable_send_status(&receipt), CultNetRudpReliableSendStatus::Invalidated);

    client.connect(b"hello again".to_vec())?;
    assert_eq!(
        client.reliable_send_status(&receipt),
        CultNetRudpReliableSendStatus::Invalidated,
        "reconnecting brought the write back to Pending"
    );
    let connect = drain(&server)?
        .into_iter()
        .find(|packet| packet.packet_type == CultNetRudpPacketType::Connect)
        .expect("the reconnect reached the peer");
    let mut new_peer = raw_session(CONNECTION_ID);
    let new_accept = new_peer.accept_connect(&connect, 0, Vec::new())?;
    send_to(&server, client_addr, &new_accept)?;
    send_to(&server, client_addr, &late_ack)?;
    while client.receive_once()?.is_some() {}
    assert_eq!(
        client.reliable_send_status(&receipt),
        CultNetRudpReliableSendStatus::Invalidated,
        "the old session's write was acknowledged in the new session"
    );
    // The old write is not retransmitted into the new session either: the
    // Accept acknowledged the reconnect's Connect, and nothing else is owed.
    assert_eq!(client.outstanding_reliable_packet_count(), 0, "the old write survived into the new session");
    Ok(())
}

/// A server-mode transport that accepts a new Connect has a live session again;
/// the reason the previous one ended is history, not state.
#[test]
fn server_mode_forgets_the_end_reason_once_a_new_connect_is_accepted() -> Result<()> {
    let server_socket = socket()?;
    let server_addr = server_socket.local_addr()?;
    let mut server = CultNetRudpSocketTransportConnection::new(
        CultNetRudpSocketTransportOptions::server("server", server_socket, CONNECTION_ID),
    )?;
    let peer = socket()?;
    let mut peer_session = raw_session(CONNECTION_ID);
    send_to(&peer, server_addr, &peer_session.create_connect(0, b"peer".to_vec())?)?;
    let _ = server.receive_once()?;
    send_to(&peer, server_addr, &peer_session.create_disconnect(b"bye".to_vec()))?;
    let _ = server.receive_once()?;
    assert_eq!(server.disconnect_reason(), Some(&b"bye"[..]));

    let mut next_peer = raw_session(CONNECTION_ID);
    send_to(&peer, server_addr, &next_peer.create_connect(0, b"peer".to_vec())?)?;
    let _ = server.receive_once()?;
    assert!(server.connected());
    assert_eq!(server.disconnect_reason(), None, "a stale reason survives the new session");
    Ok(())
}

/// A flush waits on one session. A Connect from another endpoint replaces the
/// peer and forgets the writes being waited on; the flush must say so.
#[test]
fn a_flush_fails_when_a_new_endpoint_replaces_the_peer_mid_wait() -> Result<()> {
    let server_socket = socket()?;
    let server_addr = server_socket.local_addr()?;
    let mut server = CultNetRudpSocketTransportConnection::new(
        CultNetRudpSocketTransportOptions::server("server", server_socket, CONNECTION_ID),
    )?;
    let peer_a = socket()?;
    let peer_b = socket()?;
    send_to(&peer_a, server_addr, &raw_session(CONNECTION_ID).create_connect(0, b"a".to_vec())?)?;
    let _ = server.receive_once()?;
    server.send_reliable("schema", b"for A".to_vec())?;
    send_to(&peer_b, server_addr, &raw_session(CONNECTION_ID).create_connect(0, b"b".to_vec())?)?;

    let error = server.flush_reliable(Duration::from_millis(300)).expect_err("A's write was forgotten");
    assert!(
        error.to_string().contains("ended before its reliable writes were acknowledged"),
        "{error}"
    );
    Ok(())
}

/// A repeated Connect still carries an acknowledgement field, and the session
/// honours it: the Accept it acknowledges is no longer owed.
#[test]
fn a_repeated_connect_acknowledges_what_it_carries() -> Result<()> {
    let mut client = raw_session(CONNECTION_ID);
    let mut server = raw_session(CONNECTION_ID);
    let connect = client.create_connect(0, Vec::new())?;
    let accept = server.accept_connect(&connect, 0, Vec::new())?;
    let mut repeat = connect.clone();
    repeat.ack = accept.sequence;
    let reply = server.answer_repeated_connect(&repeat, 1)?;
    assert_eq!(reply.packet_type, CultNetRudpPacketType::Ack);
    assert_eq!(server.outstanding_reliable_packet_count(), 0);
    Ok(())
}

/// A connected client session with one ordered write the peer never received.
fn client_with_a_lost_write() -> Result<(CultNetRudpSession, CultNetRudpSession)> {
    let mut client = raw_session(CONNECTION_ID);
    let mut server = raw_session(CONNECTION_ID);
    let accept = server.accept_connect(&client.create_connect(0, Vec::new())?, 0, Vec::new())?;
    client.receive(&accept, 0)?;
    client.send(
        "schema",
        b"owed to the old session".to_vec(),
        CultNetRudpSendOptions {
            reliable: true,
            ordered: true,
            ..Default::default()
        },
    )?;
    // A fragmented write larger than the send window leaves part of it queued.
    client.send_many(
        "schema",
        vec![7u8; 40 * 8],
        CultNetRudpSendOptions {
            reliable: true,
            ordered: true,
            ..Default::default()
        },
        Some(8),
    )?;
    assert_eq!(client.outstanding_reliable_packet_count(), 41);
    assert!(client.queued_reliable_packet_count() > 0);
    Ok((client, server))
}

/// A write belongs to the session it was issued in. Whichever way that session
/// ends, the write it still owed dies with it: it is not retransmitted into a
/// later session, where the new peer would deliver it.
#[test]
fn every_way_a_session_ends_drops_the_writes_it_owed() -> Result<()> {
    type End = fn(&mut CultNetRudpSession, &mut CultNetRudpSession) -> Result<()>;
    let endings: [(&str, End); 4] = [
        ("peer Disconnect", |client, server| {
            client.receive(&server.create_disconnect(b"bye".to_vec()), 1)?;
            Ok(())
        }),
        ("local disconnect", |client, _| {
            client.create_disconnect(b"bye".to_vec());
            Ok(())
        }),
        ("timeout", |client, _| {
            assert!(client.check_timeout(1_000, 10));
            Ok(())
        }),
        ("refusal", |client, _| {
            client.end_refused_session();
            Ok(())
        }),
    ];
    for (name, end) in endings {
        let (mut client, mut server) = client_with_a_lost_write()?;
        end(&mut client, &mut server)?;
        assert_eq!(client.outstanding_reliable_packet_count(), 0, "{name}: the write survived the end");

        let mut next_server = raw_session(CONNECTION_ID);
        let connect = client.create_connect(2_000, Vec::new())?;
        next_server.accept_connect(&connect, 2_000, Vec::new())?;
        for resend in client.due_resends(60_000) {
            let result = next_server.receive(&resend, 60_000)?;
            assert!(
                result.delivered.is_empty(),
                "{name}: the old session's write was delivered in the next session"
            );
        }
    }
    Ok(())
}

fn connected_client(server_socket: &UdpSocket, server_addr: SocketAddr) -> Result<(CultNetRudpSocketTransportConnection, CultNetRudpSession, SocketAddr)> {
    let mut client = CultNetRudpSocketTransportConnection::new(
        CultNetRudpSocketTransportOptions::client("client", socket()?, server_addr, CONNECTION_ID),
    )?;
    client.connect(b"hello".to_vec())?;
    let mut buffer = vec![0_u8; 65_535];
    let (received, client_addr) = server_socket.recv_from(&mut buffer)?;
    let mut peer = raw_session(CONNECTION_ID);
    let accept = peer.accept_connect(&decode_rudp_packet(&buffer[..received])?, 0, Vec::new())?;
    send_to(server_socket, client_addr, &accept)?;
    let _ = client.receive_once()?;
    Ok((client, peer, client_addr))
}

/// A local `disconnect()` ends the session like any other end: the receipt of a
/// write the peer never acknowledged is Invalidated, stays so after a
/// reconnect, and the write is not carried into the new session.
#[test]
fn a_receipt_invalidated_by_a_local_disconnect_stays_invalidated_after_reconnecting() -> Result<()> {
    let server = socket()?;
    let server_addr = server.local_addr()?;
    let (mut client, _peer, _) = connected_client(&server, server_addr)?;
    let receipt = client.send_reliable("schema", b"written before the disconnect".to_vec())?;
    assert_eq!(client.reliable_send_status(&receipt), CultNetRudpReliableSendStatus::Pending);

    client.disconnect(b"bye".to_vec())?;
    assert_eq!(client.reliable_send_status(&receipt), CultNetRudpReliableSendStatus::Invalidated);
    assert_eq!(client.outstanding_reliable_packet_count(), 0, "the write survived the disconnect");

    client.connect(b"hello again".to_vec())?;
    assert_eq!(
        client.reliable_send_status(&receipt),
        CultNetRudpReliableSendStatus::Invalidated,
        "reconnecting brought the write back"
    );
    assert_eq!(client.outstanding_reliable_packet_count(), 1, "only the new Connect is owed");
    Ok(())
}

/// A flush that starts after the session ended has no live session to wait on.
#[test]
fn a_flush_fails_after_a_local_disconnect_or_a_timeout() -> Result<()> {
    let server = socket()?;
    let server_addr = server.local_addr()?;
    let (mut client, _peer, _) = connected_client(&server, server_addr)?;
    client.send_reliable("schema", b"owed".to_vec())?;
    client.disconnect(b"bye".to_vec())?;
    let error = client.flush_reliable(Duration::from_millis(200)).expect_err("the write was dropped");
    assert!(error.to_string().contains("ended before its reliable writes were acknowledged"), "{error}");

    let server = socket()?;
    let server_addr = server.local_addr()?;
    let (mut client, _peer, _) = connected_client(&server, server_addr)?;
    client.send_reliable("schema", b"owed".to_vec())?;
    std::thread::sleep(Duration::from_millis(5));
    assert!(client.check_timeout(1));
    let error = client.flush_reliable(Duration::from_millis(200)).expect_err("the write was dropped");
    assert!(error.to_string().contains("ended before its reliable writes were acknowledged"), "{error}");
    Ok(())
}

/// A flush that starts after a reconnect waits on the new session: the end of
/// the old one does not fail it.
#[test]
fn a_flush_started_after_a_reconnect_waits_on_the_new_session() -> Result<()> {
    let server = socket()?;
    let server_addr = server.local_addr()?;
    let (mut client, _peer, client_addr) = connected_client(&server, server_addr)?;
    client.send_reliable("schema", b"owed to the old session".to_vec())?;
    client.disconnect(b"bye".to_vec())?;
    client.connect(b"hello again".to_vec())?;
    let connect = drain(&server)?
        .into_iter()
        .rfind(|packet| packet.packet_type == CultNetRudpPacketType::Connect)
        .expect("the reconnect reached the peer");
    let accept = raw_session(CONNECTION_ID).accept_connect(&connect, 0, Vec::new())?;
    send_to(&server, client_addr, &accept)?;
    let _ = client.receive_once()?;

    client.flush_reliable(Duration::from_millis(500))?;
    Ok(())
}

/// A server-mode transport that accepts a new Connect after its peer left has a
/// live session again, and a flush started in it waits on that session.
#[test]
fn a_flush_started_after_the_server_accepts_a_new_connect_waits_on_the_new_session() -> Result<()> {
    let server_socket = socket()?;
    let server_addr = server_socket.local_addr()?;
    let mut server = CultNetRudpSocketTransportConnection::new(
        CultNetRudpSocketTransportOptions::server("server", server_socket, CONNECTION_ID),
    )?;
    let peer = socket()?;
    let mut first = raw_session(CONNECTION_ID);
    send_to(&peer, server_addr, &first.create_connect(0, b"peer".to_vec())?)?;
    let _ = server.receive_once()?;
    send_to(&peer, server_addr, &first.create_disconnect(b"bye".to_vec()))?;
    let _ = server.receive_once()?;

    let mut next = raw_session(CONNECTION_ID);
    send_to(&peer, server_addr, &next.create_connect(0, b"peer".to_vec())?)?;
    let _ = server.receive_once()?;
    let accept = drain(&peer)?
        .into_iter()
        .rfind(|packet| packet.packet_type == CultNetRudpPacketType::Accept)
        .expect("the new Connect was accepted");
    next.receive(&accept, 0)?;
    send_to(&peer, server_addr, &next.create_ack_for_received(accept.sequence))?;
    let _ = server.receive_once()?;

    server.flush_reliable(Duration::from_millis(500))?;
    Ok(())
}
