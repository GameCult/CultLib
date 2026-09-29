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
        initial_sequence: 1,
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
