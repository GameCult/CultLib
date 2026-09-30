//! One peer whose datagrams cannot be sent must not stop the server serving
//! the others. The failure is injected in `send_packet` through
//! `failing_peers`; a loopback peer cannot be made unroutable on demand.

use super::*;
use cultnet_rs::{
    CultNetRawPayloadEncoding, CultNetRudpSocketMode, CultNetRudpSocketTransportConnection,
    CultNetRudpSocketTransportOptions,
};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;

#[derive(Clone)]
struct Clock(Arc<AtomicU64>);

impl CultMeshRudpServerClock for Clock {
    fn now_unix_millis(&self) -> u64 {
        self.0.load(Ordering::SeqCst)
    }
    fn now_monotonic_millis(&self) -> u64 {
        self.0.load(Ordering::SeqCst)
    }
}

type Received = Arc<Mutex<Vec<String>>>;
type Server = CultMeshRudpDocumentServer<
    Box<dyn FnMut(CultMeshRudpRawDocumentReceipt) -> Result<()>>,
    Box<dyn FnMut(&CultMeshRudpSnapshotQuery) -> Result<Vec<CultNetRawDocumentRecord>>>,
    Clock,
>;
type Client = CultNetRudpSocketTransportConnection;

fn document(key: &str) -> CultNetRawDocumentRecord {
    CultNetRawDocumentRecord {
        schema_id: "test.raw.v1".into(),
        record_key: key.into(),
        stored_at: "2026-09-03T00:00:00Z".into(),
        payload_encoding: CultNetRawPayloadEncoding::Messagepack,
        payload: vec![0x81, 0xa1, 0x78, 0x2a],
        source_runtime_id: None,
        source_agent_id: None,
        source_role: None,
        tags: None,
    }
}

fn client(target: SocketAddr, id: u32) -> (Client, SocketAddr) {
    let socket = UdpSocket::bind("127.0.0.1:0").unwrap();
    socket.set_nonblocking(true).unwrap();
    let addr = socket.local_addr().unwrap();
    let client = CultNetRudpSocketTransportConnection::new(CultNetRudpSocketTransportOptions {
        media_delivery: None,
        runtime_id: "test-client".into(),
        socket,
        mode: CultNetRudpSocketMode::Client,
        remote_addr: Some(target),
        connection_id: id,
        initial_sequence: Some(1),
        resend_delay_ms: 10,
        transport_id: None,
        max_payload_bytes: None,
        max_fragment_bytes: Some(1200),
        max_pending_reliable_packets: Some(64),
        media_reliable_expire_after_ms: None,
        reconnect_policy: None,
    })
    .unwrap();
    (client, addr)
}

fn send(client: &mut Client, message: &CultNetMessage) {
    client
        .send(
            "schema",
            encode_cultnet_message_to_vec(message, CultNetWireContract::CultNetSchemaV0).unwrap(),
        )
        .unwrap();
}

/// A server with a 1 s idle timeout and two connected peers, the first
/// returned being the one whose address sorts first.
fn two_peers() -> (
    Server,
    Clock,
    Received,
    (Client, SocketAddr),
    (Client, SocketAddr),
) {
    let clock = Clock(Arc::new(AtomicU64::new(1_000)));
    let received: Received = Arc::default();
    let sink_received = received.clone();
    let sink: Box<dyn FnMut(CultMeshRudpRawDocumentReceipt) -> Result<()>> =
        Box::new(move |receipt| {
            sink_received.lock().unwrap().push(receipt.message_id);
            Ok(())
        });
    let source: Box<
        dyn FnMut(&CultMeshRudpSnapshotQuery) -> Result<Vec<CultNetRawDocumentRecord>>,
    > = Box::new(|_| Ok(vec![document("catalog")]));
    let mut server = CultMeshRudpDocumentServer::new(
        UdpSocket::bind("127.0.0.1:0").unwrap(),
        sink,
        source,
        clock.clone(),
        CultMeshRudpDocumentServerOptions {
            session_idle_timeout: Duration::from_secs(1),
            resend_delay: Duration::from_millis(10),
            ..Default::default()
        },
    )
    .unwrap();
    let target = server.local_addr().unwrap();
    let (mut a, a_addr) = client(target, 7);
    let (mut b, b_addr) = client(target, 7);
    a.connect(Vec::new()).unwrap();
    b.connect(Vec::new()).unwrap();
    for _ in 0..500 {
        server.poll_once().unwrap();
        a.receive_once().unwrap();
        b.receive_once().unwrap();
        if a.connected() && b.connected() {
            break;
        }
        thread::sleep(Duration::from_millis(1));
    }
    assert!(a.connected() && b.connected());
    for _ in 0..4 {
        server.poll_once().unwrap();
    }
    assert_eq!(server.session_count(), 2);
    if a_addr < b_addr {
        (server, clock, received, (a, a_addr), (b, b_addr))
    } else {
        (server, clock, received, (b, b_addr), (a, a_addr))
    }
}

#[test]
fn one_peer_whose_sends_fail_does_not_stop_the_server_serving_the_others() {
    let (mut server, clock, received, (mut x, x_addr), (mut y, _)) = two_peers();
    server.failing_peers.insert(x_addr);

    // X's reply, ack and snapshot response all fail to send. X is visited first.
    send(
        &mut x,
        &CultNetMessage::SnapshotRequest {
            message_id: "x-snapshot".into(),
            schema_ids: None,
            record_keys: None,
        },
    );
    send(
        &mut y,
        &CultNetMessage::DocumentPutRaw {
            message_id: "y-put".into(),
            document: document("y"),
        },
    );
    send(
        &mut y,
        &CultNetMessage::SnapshotRequest {
            message_id: "y-snapshot".into(),
            schema_ids: None,
            record_keys: None,
        },
    );
    let mut y_response = None;
    for step in 0..200 {
        // The clock moves past X's resend delay so `maintain` has X's resend due.
        clock.0.store(1_000 + step * 15, Ordering::SeqCst);
        // Never `Err`: X's failed sends are its own lost datagrams.
        server.poll_once().unwrap();
        if let Some(frame) = y.receive_once().unwrap() {
            y_response = Some(frame);
            break;
        }
        thread::sleep(Duration::from_millis(1));
    }
    let frame = y_response.expect("Y receives its snapshot response");
    let response =
        decode_cultnet_message_from_slice(&frame.payload, CultNetWireContract::CultNetSchemaV0)
            .unwrap();
    assert!(matches!(
        response,
        CultNetMessage::SnapshotResponseRaw { ref message_id, .. } if message_id == "y-snapshot"
    ));
    assert!(server.send_failures() > 0);
    assert_eq!(received.lock().unwrap().as_slice(), ["y-put"]);
    assert!(server.maintain().is_ok());

    // X's session ends by the idle rule that already ends sessions, not by the
    // failure: Y stays active, X does not.
    clock.0.store(1_900, Ordering::SeqCst);
    send(
        &mut y,
        &CultNetMessage::DocumentPutRaw {
            message_id: "y-put-2".into(),
            document: document("y2"),
        },
    );
    for _ in 0..20 {
        server.poll_once().unwrap();
        thread::sleep(Duration::from_millis(1));
    }
    assert_eq!(server.session_count(), 2, "the failure ended no session");
    clock.0.store(2_500, Ordering::SeqCst);
    let maintenance = server.maintain().unwrap();
    assert_eq!(maintenance.sessions_expired, 1);
    assert_eq!(server.session_count(), 1);
}

// A permanent send failure is one that can never succeed for the datagram as
// built (`is_permanent_send_error`). It ends that peer's session, never the
// poll, and the peer's goodbye names the error.

fn put(id: &str) -> CultNetMessage {
    CultNetMessage::DocumentPutRaw {
        message_id: id.into(),
        document: document(id),
    }
}

fn snapshot(id: &str) -> CultNetMessage {
    CultNetMessage::SnapshotRequest {
        message_id: id.into(),
        schema_ids: None,
        record_keys: None,
    }
}

fn goodbye_reason(client: &mut Client) -> Vec<u8> {
    for _ in 0..50 {
        client.receive_once().unwrap();
        if let Some(reason) = client.disconnect_reason() {
            return reason.to_vec();
        }
        thread::sleep(Duration::from_millis(2));
    }
    panic!("the client was never told the session ended");
}

fn is_unsendable_reason(reason: &[u8]) -> bool {
    reason.starts_with(b"packet could not be sent: ")
        && reason.len() > b"packet could not be sent: ".len()
}

#[test]
fn a_permanent_failure_acknowledging_a_document_ends_the_session_after_the_document_is_kept() {
    let (mut server, _clock, received, (mut x, x_addr), (mut y, _)) = two_peers();
    server.unsendable_after.insert(x_addr, 0);
    send(&mut x, &put("x-put"));
    send(&mut y, &put("y-put"));

    for _ in 0..10 {
        // Never `Err`: the failure ends X's session, not the poll.
        server.poll_once().unwrap();
    }
    let mut kept = received.lock().unwrap().clone();
    kept.sort();
    assert_eq!(kept, ["x-put", "y-put"]);
    assert_eq!(server.session_count(), 1);
    assert_eq!(server.send_failures(), 0);
    assert!(is_unsendable_reason(&goodbye_reason(&mut x)));
}

#[test]
fn a_permanent_failure_sending_a_snapshot_response_rejects_it_and_sends_no_refusal() {
    let (mut server, _clock, _received, (mut x, x_addr), (_y, _)) = two_peers();
    server.unsendable_after.insert(x_addr, 0);
    send(&mut x, &snapshot("x-snapshot"));

    let mut rejection = None;
    for _ in 0..10 {
        if let CultMeshRudpPollOutcome::ApplicationRejected(found) = server.poll_once().unwrap() {
            rejection = Some(found);
            break;
        }
    }
    let rejection = rejection.expect("the response can never be sent");
    assert_eq!(rejection.operation, CultMeshRudpApplicationOperation::SnapshotRequest);
    assert_eq!(rejection.message_id, "x-snapshot");
    assert_eq!(
        rejection.reason,
        CultMeshRudpRejectionReason::ResponseSendFailed(ErrorKind::InvalidInput)
    );
    assert_eq!(server.session_count(), 1);
    // The server knows X cannot be reached: X is sent no refusal, only the
    // goodbye naming the error, which carries no text of the peer's.
    let mut frames = Vec::new();
    let mut reason = None;
    for _ in 0..50 {
        if let Some(frame) = x.receive_once().unwrap() {
            frames.push(frame);
        }
        if let Some(found) = x.disconnect_reason() {
            reason = Some(found.to_vec());
            break;
        }
        thread::sleep(Duration::from_millis(2));
    }
    assert!(frames.is_empty(), "no refusal is sent to an unreachable peer: {frames:?}");
    assert!(is_unsendable_reason(&reason.expect("the client was told the session ended")));
}

#[test]
fn a_permanent_failure_in_resends_ends_only_that_peers_session() {
    let (mut server, clock, received, (mut x, x_addr), (mut y, _)) = two_peers();
    // X's snapshot response is lost, so a resend is due; then the datagram can
    // never be sent as built.
    server.failing_peers.insert(x_addr);
    send(&mut x, &snapshot("x-snapshot"));
    for _ in 0..5 {
        server.poll_once().unwrap();
    }
    server.failing_peers.clear();
    server.unsendable_after.insert(x_addr, 0);
    clock.0.store(1_200, Ordering::SeqCst);

    let maintenance = server.maintain().unwrap();
    assert!(maintenance.packets_resent > 0);
    assert_eq!(server.session_count(), 1);
    assert!(is_unsendable_reason(&goodbye_reason(&mut x)));

    send(&mut y, &put("y-put"));
    for _ in 0..5 {
        server.poll_once().unwrap();
    }
    assert_eq!(received.lock().unwrap().as_slice(), ["y-put"]);
}

#[test]
fn a_connect_whose_accept_can_never_be_sent_starts_no_session() {
    let (mut server, _clock, _received, _x, _y) = two_peers();
    let (mut z, z_addr) = client(server.local_addr().unwrap(), 9);
    server.unsendable_after.insert(z_addr, 0);
    z.connect(Vec::new()).unwrap();

    for _ in 0..10 {
        server.poll_once().unwrap();
        thread::sleep(Duration::from_millis(1));
    }
    assert_eq!(server.session_count(), 2);
    assert_eq!(server.packets_dropped(), 1);
}

#[test]
fn healthy_sends_are_not_failures() {
    let (mut server, _clock, _received, (mut x, _), (_y, _)) = two_peers();
    send(&mut x, &put("x-put"));
    send(&mut x, &snapshot("x-snapshot"));
    for _ in 0..10 {
        server.poll_once().unwrap();
        thread::sleep(Duration::from_millis(1));
    }
    assert_eq!(server.send_failures(), 0);
    assert_eq!(server.session_count(), 2);
}

#[test]
fn a_session_ended_in_the_resend_loop_is_sent_nothing_more_from_it() {
    let (mut server, clock, _received, (mut x, x_addr), (_y, _)) = two_peers();
    server.failing_peers.insert(x_addr);
    send(&mut x, &snapshot("one"));
    send(&mut x, &snapshot("two"));
    for _ in 0..10 {
        server.poll_once().unwrap();
    }
    let before = server.send_failures();

    server.unsendable_after.insert(x_addr, 0);
    clock.0.store(1_200, Ordering::SeqCst);
    let maintenance = server.maintain().unwrap();

    // Both responses are due. The first can never be sent and ends the session;
    // the goodbye is the only other datagram attempted at X.
    assert_eq!(maintenance.packets_resent, 2);
    assert_eq!(server.send_failures() - before, 1);
    assert_eq!(server.session_count(), 1);
}

#[test]
fn no_ack_follows_a_reply_that_can_never_be_sent() {
    let (mut server, _clock, _received, _x, _y) = two_peers();
    let target = server.local_addr().unwrap();
    // A peer driven packet by packet: a client never sends a reliable Ping, but a
    // peer may, and the server both answers it and acknowledges it.
    let socket = UdpSocket::bind("127.0.0.1:0").unwrap();
    socket
        .set_read_timeout(Some(Duration::from_millis(20)))
        .unwrap();
    let addr = socket.local_addr().unwrap();
    let mut session = CultNetRudpSession::new(CultNetRudpSessionOptions {
        connection_id: 7,
        ..Default::default()
    });
    let connect = session.create_connect(0, Vec::new()).unwrap();
    socket
        .send_to(&encode_rudp_packet(&connect).unwrap(), target)
        .unwrap();
    let mut wire = vec![0_u8; MAX_UDP_DATAGRAM_BYTES];
    for _ in 0..10 {
        server.poll_once().unwrap();
    }
    let (read, _) = socket.recv_from(&mut wire).unwrap();
    let accept = decode_rudp_packet(&wire[..read]).unwrap();
    assert_eq!(accept.packet_type, CultNetRudpPacketType::Accept);
    session.receive(&accept, 0).unwrap();
    assert_eq!(server.session_count(), 3);

    let mut ping = session.create_ping(b"p".to_vec());
    ping.reliable = true;
    ping.sequence = connect.sequence.wrapping_add(1);
    // The Pong can never be sent; the ack after it could.
    server.unsendable_after.insert(addr, 0);
    socket
        .send_to(&encode_rudp_packet(&ping).unwrap(), target)
        .unwrap();
    for _ in 0..10 {
        server.poll_once().unwrap();
    }

    // The session ends over the Pong: the peer gets its goodbye and nothing else.
    assert_eq!(server.session_count(), 2);
    let mut arrived = Vec::new();
    while let Ok((read, _)) = socket.recv_from(&mut wire) {
        arrived.push(decode_rudp_packet(&wire[..read]).unwrap());
    }
    assert_eq!(arrived.len(), 1, "{arrived:?}");
    assert_eq!(arrived[0].packet_type, CultNetRudpPacketType::Disconnect);
    assert!(is_unsendable_reason(&arrived[0].payload));
}

/// Windows reports an ICMP port-unreachable for an earlier datagram as
/// `ConnectionReset` on the next receive. It names no datagram of this poll, so
/// the poll is idle and the server goes on serving. Only Windows reports it, so
/// only a Windows run can see the rule break.
#[cfg(windows)]
#[test]
fn a_reset_reported_by_a_receive_is_idle_not_an_error() {
    let (mut server, _clock, received, (mut x, _), (mut y, _)) = two_peers();
    // X asks for a snapshot and goes away before the answer.
    send(&mut x, &snapshot("x-snapshot"));
    drop(x);
    for _ in 0..20 {
        server
            .poll_once()
            .expect("a reset from X's closed port is not an error");
        thread::sleep(Duration::from_millis(1));
    }

    send(&mut y, &put("y-put"));
    for _ in 0..20 {
        server.poll_once().unwrap();
        thread::sleep(Duration::from_millis(1));
    }
    assert_eq!(received.lock().unwrap().as_slice(), ["y-put"]);
}
