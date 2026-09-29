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
