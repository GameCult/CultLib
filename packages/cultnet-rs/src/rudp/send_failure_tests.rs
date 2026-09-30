//! One peer whose datagrams cannot be sent must not stop the hub serving the
//! others. The failure is injected at `send_datagram` through `failing_peers`.

use super::*;
use std::time::Duration;

const CONNECTION_ID: u32 = 0x4355_4c54;

fn socket() -> UdpSocket {
    let socket = UdpSocket::bind("127.0.0.1:0").unwrap();
    socket
        .set_read_timeout(Some(Duration::from_millis(20)))
        .unwrap();
    socket
}

fn client(remote: SocketAddr) -> CultNetRudpSocketTransportConnection {
    let mut options =
        CultNetRudpSocketTransportOptions::client("peer", socket(), remote, CONNECTION_ID);
    options.resend_delay_ms = 10;
    CultNetRudpSocketTransportConnection::new(options).unwrap()
}

fn connect(
    hub: &mut CultNetRudpServerHub,
    client: &mut CultNetRudpSocketTransportConnection,
) -> CultNetRudpServerSessionContext {
    client.connect(Vec::new()).unwrap();
    let mut session = None;
    for _ in 0..20 {
        if let Some(CultNetRudpServerEvent::Connected { session: found }) =
            hub.receive_event_once().unwrap()
        {
            session = Some(found);
            break;
        }
    }
    client.receive_once().unwrap();
    assert!(client.connected());
    session.expect("hub saw the Connect")
}

/// Two connected peers, ordered so the first is the one the hub visits first
/// (the peer map is ordered by address).
fn two_peers() -> (
    CultNetRudpServerHub,
    (
        CultNetRudpSocketTransportConnection,
        CultNetRudpServerSessionContext,
    ),
    (
        CultNetRudpSocketTransportConnection,
        CultNetRudpServerSessionContext,
    ),
) {
    let server_socket = socket();
    let server_addr = server_socket.local_addr().unwrap();
    let mut options = CultNetRudpServerHubOptions::new("hub", server_socket, CONNECTION_ID);
    options.resend_delay_ms = 10;
    let mut hub = CultNetRudpServerHub::new(options).unwrap();
    let mut a = client(server_addr);
    let mut b = client(server_addr);
    let a_session = connect(&mut hub, &mut a);
    let b_session = connect(&mut hub, &mut b);
    if a_session.remote_addr < b_session.remote_addr {
        (hub, (a, a_session), (b, b_session))
    } else {
        (hub, (b, b_session), (a, a_session))
    }
}

#[test]
fn a_failed_resend_to_one_peer_does_not_stop_the_others_resends() {
    let (mut hub, (_x, x_session), (mut y, y_session)) = two_peers();
    // Both peers' first datagram is lost, so both have a reliable resend due.
    hub.failing_peers.insert(x_session.remote_addr);
    hub.failing_peers.insert(y_session.remote_addr);
    hub.send(&x_session, "schema", b"for-x".to_vec()).unwrap();
    hub.send(&y_session, "schema", b"for-y".to_vec()).unwrap();
    assert_eq!(hub.stats().send_failures, 2);

    // Y's path recovers; X's stays dead. X is visited first.
    hub.failing_peers.remove(&y_session.remote_addr);
    std::thread::sleep(Duration::from_millis(40));
    hub.poll_resends().unwrap();

    let mut delivered = None;
    for _ in 0..20 {
        if let Some(frame) = y.receive_once().unwrap() {
            delivered = Some(frame);
            break;
        }
    }
    assert_eq!(delivered.expect("Y receives its resend").payload, b"for-y");
    assert!(hub.stats().send_failures >= 3);
    assert_eq!(hub.sessions().len(), 2, "a send failure ends no session");
}

#[test]
fn a_failed_reply_to_one_peer_does_not_stop_the_hub_serving_the_others() {
    let (mut hub, (mut x, x_session), (mut y, _y_session)) = two_peers();
    hub.failing_peers.insert(x_session.remote_addr);

    x.send("schema", b"from-x".to_vec()).unwrap();
    y.send("schema", b"from-y".to_vec()).unwrap();

    let mut frames = Vec::new();
    for _ in 0..20 {
        if let Some(CultNetRudpServerEvent::Frame { frame, .. }) = hub.receive_event_once().unwrap()
        {
            frames.push(frame.payload);
        }
        if frames.len() == 2 {
            break;
        }
    }
    frames.sort();
    assert_eq!(frames, vec![b"from-x".to_vec(), b"from-y".to_vec()]);
    assert!(
        hub.stats().send_failures >= 1,
        "X's ack was counted as lost"
    );
    assert_eq!(hub.sessions().len(), 2);
}

// A permanent send failure is one that can never succeed for the datagram as
// built. A caller-directed send returns it and queues nothing; inside a poll it
// ends that peer's session and never the poll.

fn is_unsendable_reason(reason: &[u8]) -> bool {
    reason.starts_with(b"packet could not be sent: ")
        && reason.len() > b"packet could not be sent: ".len()
}

fn next_event(hub: &mut CultNetRudpServerHub) -> CultNetRudpServerEvent {
    for _ in 0..20 {
        if let Some(event) = hub.receive_event_once().unwrap() {
            return event;
        }
    }
    panic!("the hub raised no event");
}

fn one_peer(
    max_fragment_bytes: Option<u32>,
) -> (
    CultNetRudpServerHub,
    CultNetRudpSocketTransportConnection,
    CultNetRudpServerSessionContext,
) {
    let server_socket = socket();
    let server_addr = server_socket.local_addr().unwrap();
    let mut options = CultNetRudpServerHubOptions::new("hub", server_socket, CONNECTION_ID);
    options.resend_delay_ms = 10;
    options.max_fragment_bytes = max_fragment_bytes;
    let mut hub = CultNetRudpServerHub::new(options).unwrap();
    let mut peer = client(server_addr);
    let session = connect(&mut hub, &mut peer);
    (hub, peer, session)
}

#[test]
fn an_oversized_send_returns_the_error_and_queues_nothing() {
    let (mut hub, mut peer, session) = one_peer(None);
    // The hub's own Accept is still awaiting its ack; the refused send adds nothing to it.
    let outstanding = hub.peers[&session.remote_addr]
        .session
        .outstanding_reliable_packet_count();

    let error = hub
        .send(&session, "schema", vec![7; 70_000])
        .expect_err("70,000 bytes cannot be one datagram");
    let io_error = error
        .downcast_ref::<std::io::Error>()
        .expect("the send error is the socket's own");
    assert!(is_permanent_send_error(io_error), "{io_error}");
    assert_eq!(hub.stats().send_failures, 0);
    assert_eq!(hub.stats().frames_sent, 0);
    assert_eq!(hub.sessions().len(), 1, "the caller's error ends no session");
    let remote = session.remote_addr;
    assert_eq!(
        hub.peers[&remote].session.outstanding_reliable_packet_count(),
        outstanding,
        "nothing is pending or queued to resend"
    );

    // The refused send consumed no sequence: the next frame is delivered in
    // order instead of waiting behind a gap that never fills.
    hub.send(&session, "schema", b"next".to_vec()).unwrap();
    let mut delivered = None;
    for _ in 0..20 {
        if let Some(frame) = peer.receive_once().unwrap() {
            delivered = Some(frame);
            break;
        }
    }
    assert_eq!(delivered.expect("the next frame arrives").payload, b"next");
}

#[test]
fn a_send_that_fails_permanently_after_a_fragment_left_ends_the_session() {
    let (mut hub, _peer, session) = one_peer(Some(1000));
    hub.unsendable_after.insert(session.remote_addr, 1);

    let error = hub
        .send(&session, "schema", vec![7; 3000])
        .expect_err("the second fragment can never be sent");
    assert!(is_permanent_send_error(
        error.downcast_ref::<std::io::Error>().unwrap()
    ));
    assert!(hub.sessions().is_empty());
    let CultNetRudpServerEvent::Disconnected { reason, .. } = next_event(&mut hub) else {
        panic!("the caller sees the session end");
    };
    assert!(is_unsendable_reason(&reason));
}

#[test]
fn a_permanent_failure_in_resends_ends_only_that_peers_session() {
    let (mut hub, (mut x, x_session), (mut y, y_session)) = two_peers();
    // Both peers' first datagram is lost, so both have a reliable resend due.
    hub.failing_peers.insert(x_session.remote_addr);
    hub.failing_peers.insert(y_session.remote_addr);
    hub.send(&x_session, "schema", b"for-x".to_vec()).unwrap();
    hub.send(&y_session, "schema", b"for-y".to_vec()).unwrap();

    // X's datagram can now never be sent; Y's path recovers. X is visited first.
    hub.failing_peers.clear();
    hub.unsendable_after.insert(x_session.remote_addr, 0);
    std::thread::sleep(Duration::from_millis(40));
    hub.poll_resends().unwrap();

    assert_eq!(hub.sessions(), vec![y_session.clone()]);
    let CultNetRudpServerEvent::Disconnected { session, reason } = next_event(&mut hub) else {
        panic!("the caller sees the session end");
    };
    assert_eq!(session, x_session);
    assert!(is_unsendable_reason(&reason));
    let mut delivered = None;
    for _ in 0..20 {
        if let Some(frame) = y.receive_once().unwrap() {
            delivered = Some(frame);
            break;
        }
    }
    assert_eq!(delivered.expect("Y receives its resend").payload, b"for-y");
    // The goodbye reaches X and names the error.
    for _ in 0..20 {
        x.receive_once().unwrap();
        if x.disconnect_reason().is_some() {
            break;
        }
    }
    assert!(is_unsendable_reason(
        x.disconnect_reason().expect("X is told its session ended")
    ));
}

#[test]
fn a_permanent_failure_replying_to_a_frame_ends_the_session_after_the_frame_is_delivered() {
    let (mut hub, (mut x, x_session), (_y, _y_session)) = two_peers();
    hub.unsendable_after.insert(x_session.remote_addr, 0);
    x.send("schema", b"from-x".to_vec()).unwrap();

    let CultNetRudpServerEvent::Frame { frame, .. } = next_event(&mut hub) else {
        panic!("the frame is delivered before the session ends");
    };
    assert_eq!(frame.payload, b"from-x");
    let CultNetRudpServerEvent::Disconnected { session, reason } = next_event(&mut hub) else {
        panic!("the session ends");
    };
    assert_eq!(session, x_session);
    assert!(is_unsendable_reason(&reason));
    assert_eq!(hub.sessions().len(), 1);
}

#[test]
fn a_permanent_failure_answering_a_repeated_connect_ends_the_session() {
    let server_socket = socket();
    let server_addr = server_socket.local_addr().unwrap();
    let mut options = CultNetRudpServerHubOptions::new("hub", server_socket, CONNECTION_ID);
    options.resend_delay_ms = 10;
    let mut hub = CultNetRudpServerHub::new(options).unwrap();
    let raw = socket();
    let mut session = CultNetRudpSession::new(CultNetRudpSessionOptions {
        connection_id: CONNECTION_ID,
        initial_sequence: Some(5),
        resend_delay_ms: 10,
        max_pending_reliable_packets: None,
    });
    let wire = encode_rudp_packet(&session.create_connect(0, Vec::new()).unwrap()).unwrap();

    raw.send_to(&wire, server_addr).unwrap();
    let CultNetRudpServerEvent::Connected { session: admitted } = next_event(&mut hub) else {
        panic!("the first Connect is admitted");
    };
    hub.unsendable_after.insert(raw.local_addr().unwrap(), 0);
    raw.send_to(&wire, server_addr).unwrap();

    let CultNetRudpServerEvent::Disconnected {
        session: ended,
        reason,
    } = next_event(&mut hub)
    else {
        panic!("the reply to the repeat can never be sent, so the session ends");
    };
    assert_eq!(ended, admitted);
    assert!(is_unsendable_reason(&reason));
    assert!(hub.sessions().is_empty());
}

#[test]
fn a_connect_whose_accept_can_never_be_sent_starts_no_session() {
    let server_socket = socket();
    let server_addr = server_socket.local_addr().unwrap();
    let mut options = CultNetRudpServerHubOptions::new("hub", server_socket, CONNECTION_ID);
    options.resend_delay_ms = 10;
    let mut hub = CultNetRudpServerHub::new(options).unwrap();
    let mut peer = client(server_addr);
    hub.unsendable_after
        .insert(peer.socket.local_addr().unwrap(), 0);

    peer.connect(Vec::new()).unwrap();
    for _ in 0..5 {
        assert_eq!(hub.receive_event_once().unwrap(), None);
    }
    assert!(hub.sessions().is_empty());
    assert_eq!(hub.stats().packets_dropped, 1);
}

#[test]
fn a_healthy_send_counts_its_bytes_and_no_failure() {
    let (mut hub, mut peer, session) = one_peer(None);
    let before = hub.stats().bytes_sent;
    let received_before = peer.stats().bytes_received;
    hub.send(&session, "schema", b"hello".to_vec()).unwrap();
    for _ in 0..20 {
        if peer.receive_once().unwrap().is_some() {
            break;
        }
    }
    let sent = hub.stats().bytes_sent - before;
    assert!(sent > b"hello".len() as u64);
    assert_eq!(sent, peer.stats().bytes_received - received_before);
    assert_eq!(hub.stats().send_failures, 0);
}

#[test]
fn only_a_datagram_that_can_never_be_sent_is_a_permanent_failure() {
    use std::io::{Error, ErrorKind};
    assert!(is_permanent_send_error(&Error::from(ErrorKind::InvalidInput)));
    assert!(!is_permanent_send_error(&Error::other("no os code")));
    assert!(!is_permanent_send_error(&Error::from(ErrorKind::WouldBlock)));
    assert!(!is_permanent_send_error(&Error::from(ErrorKind::ConnectionReset)));
    #[cfg(target_os = "linux")]
    let (permanent, transient) = (
        // EMSGSIZE, EAFNOSUPPORT, EINVAL
        [90, 97, 22],
        // EPERM, EINTR, EAGAIN, EACCES, ENETDOWN, ENETUNREACH, EADDRNOTAVAIL, ENOBUFS, ECONNREFUSED, EHOSTUNREACH
        [1, 4, 11, 13, 100, 101, 99, 105, 111, 113],
    );
    #[cfg(windows)]
    let (permanent, transient) = (
        // WSAEMSGSIZE, WSAEAFNOSUPPORT, WSAEINVAL
        [10040, 10047, 10022],
        // WSAEACCES, WSAEWOULDBLOCK, WSAENETDOWN, WSAENETUNREACH, WSAEADDRNOTAVAIL, WSAENOBUFS, WSAECONNRESET,
        // WSAECONNREFUSED, WSAEHOSTUNREACH, WSAEINTR
        [10013, 10035, 10050, 10051, 10049, 10055, 10054, 10061, 10065, 10004],
    );
    #[cfg(any(target_os = "linux", windows))]
    {
        for code in permanent {
            assert!(
                is_permanent_send_error(&Error::from_raw_os_error(code)),
                "os error {code} is permanent"
            );
        }
        for code in transient {
            assert!(
                !is_permanent_send_error(&Error::from_raw_os_error(code)),
                "os error {code} is transient"
            );
        }
    }
}

#[test]
fn a_permanent_failure_sending_a_disconnect_is_returned_to_the_caller() {
    let (mut hub, _peer, session) = one_peer(None);
    hub.unsendable_after.insert(session.remote_addr, 0);

    let error = hub
        .disconnect(&session, b"bye".to_vec())
        .expect_err("the goodbye can never be sent");
    assert!(is_permanent_send_error(
        error.downcast_ref::<std::io::Error>().unwrap()
    ));
}

#[test]
fn a_peer_ended_in_the_resend_loop_is_sent_nothing_more_from_it() {
    let (mut hub, (_x, x_session), (_y, _y_session)) = two_peers();
    hub.failing_peers.insert(x_session.remote_addr);
    hub.send(&x_session, "schema", b"one".to_vec()).unwrap();
    hub.send(&x_session, "schema", b"two".to_vec()).unwrap();
    assert_eq!(hub.stats().send_failures, 2);

    hub.unsendable_after.insert(x_session.remote_addr, 0);
    std::thread::sleep(Duration::from_millis(40));
    hub.poll_resends().unwrap();

    // Both resends are due. The first can never be sent and ends the session;
    // the goodbye is the only other datagram attempted at X.
    assert_eq!(hub.stats().send_failures, 3);
    assert_eq!(hub.sessions().len(), 1);
}

#[test]
fn a_refused_send_withdraws_the_fragments_the_window_queued_as_well() {
    let (mut hub, _peer, session) = one_peer(Some(1));
    let outstanding = hub.peers[&session.remote_addr]
        .session
        .outstanding_reliable_packet_count();
    hub.unsendable_after.insert(session.remote_addr, 0);

    // 1,100 one-byte fragments: the flow window admits the first ones and
    // queues the rest, and the first datagram can never be sent.
    hub.send(&session, "schema", vec![7; 1_100])
        .expect_err("the first fragment can never be sent");

    assert_eq!(
        hub.peers[&session.remote_addr]
            .session
            .outstanding_reliable_packet_count(),
        outstanding,
        "nothing of the refused send stays queued"
    );
}
