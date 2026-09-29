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

#[test]
fn hub_drops_a_packet_its_admitted_peer_session_refuses() -> Result<()> {
    let mut hub = CultNetRudpServerHub::new(CultNetRudpServerHubOptions::new(
        "hub",
        socket()?,
        CONNECTION_ID,
    ))?;
    let hub_addr = hub.local_addr()?;
    let peer = socket()?;
    let mut peer_session = raw_session(CONNECTION_ID);
    send_to(&peer, hub_addr, &peer_session.create_connect(0, b"peer".to_vec())?)?;
    assert!(matches!(
        hub.receive_event_once()?,
        Some(CultNetRudpServerEvent::Connected { .. })
    ));
    let mut buffer = vec![0_u8; 65_535];
    let (received, _) = peer.recv_from(&mut buffer)?;
    peer_session.receive(&decode_rudp_packet(&buffer[..received])?, 0)?;

    send_to(&peer, hub_addr, &poisoned(&mut peer_session)?)?;
    assert!(hub.receive_event_once()?.is_none());
    assert_eq!(hub.stats().packets_dropped, 1);
    assert_eq!(hub.sessions().len(), 1);

    send_to(
        &peer,
        hub_addr,
        &peer_session.send("schema", b"still here".to_vec(), reliable())?,
    )?;
    match hub.receive_event_once()? {
        Some(CultNetRudpServerEvent::Frame { frame, .. }) => {
            assert_eq!(frame.payload, b"still here");
        }
        other => panic!("the peer must still be served, got {other:?}"),
    }
    Ok(())
}

#[test]
fn client_drops_a_packet_its_session_refuses() -> Result<()> {
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
    send_to(
        &server,
        client_addr,
        &server_session.send("schema", b"real".to_vec(), reliable())?,
    )?;
    assert_eq!(client.receive_once()?.expect("the real frame").payload, b"real");
    Ok(())
}

#[test]
fn server_mode_drops_what_is_not_its_peer_or_what_its_session_refuses() -> Result<()> {
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

    // A packet from the peer that the session itself refuses.
    send_to(&peer, server_addr, &poisoned(&mut peer_session)?)?;
    assert!(server.receive_once()?.is_none());
    assert_eq!(server.stats().packets_dropped, 7);

    send_to(
        &peer,
        server_addr,
        &peer_session.send("schema", b"real".to_vec(), reliable())?,
    )?;
    assert_eq!(server.receive_once()?.expect("the real frame").payload, b"real");
    Ok(())
}
