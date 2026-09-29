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
    for _ in 0..20 {
        send_to(&peer, hub_addr, &connect)?;
        while hub.receive_event_once()?.is_some() {}
    }
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
