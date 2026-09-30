use anyhow::Result;
use cultmesh_rs::{
    CultMeshRudpApplicationOperation, CultMeshRudpDocumentServer,
    CultMeshRudpDocumentServerOptions, CultMeshRudpPollOutcome, CultMeshRudpRawDocumentReceipt,
    CultMeshRudpRawDocumentSink, CultMeshRudpRejectionReason, CultMeshRudpServerClock,
    CultMeshRudpSnapshotQuery, CultMeshRudpSnapshotSource,
};
use cultnet_rs::{
    CultNetMessage, CultNetRawDocumentRecord, CultNetRawPayloadEncoding,
    CultNetRudpReliableSendReceipt, CultNetRudpReliableSendStatus, CultNetRudpSocketMode,
    CultNetRudpSocketTransportConnection, CultNetRudpSocketTransportOptions, CultNetWireContract,
    decode_cultnet_message_from_slice, encode_cultnet_message_to_vec,
};
use pretty_assertions::assert_eq;
use std::net::{SocketAddr, UdpSocket};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::Duration;

#[derive(Default)]
struct SinkState {
    receipts: Vec<CultMeshRudpRawDocumentReceipt>,
    failures_remaining: usize,
}

#[derive(Clone, Default)]
struct Sink(Arc<Mutex<SinkState>>);

impl Sink {
    fn fail_once() -> Self {
        Self(Arc::new(Mutex::new(SinkState {
            failures_remaining: 1,
            ..Default::default()
        })))
    }
}

impl CultMeshRudpRawDocumentSink for Sink {
    fn accept_raw_document(&mut self, receipt: CultMeshRudpRawDocumentReceipt) -> Result<()> {
        let mut state = self.0.lock().unwrap();
        if state.failures_remaining > 0 {
            state.failures_remaining -= 1;
            anyhow::bail!("injected sink failure");
        }
        state.receipts.push(receipt);
        Ok(())
    }
}

#[derive(Default)]
struct SourceState {
    documents: Vec<CultNetRawDocumentRecord>,
    failures_remaining: usize,
    calls: usize,
}

#[derive(Clone, Default)]
struct Source(Arc<Mutex<SourceState>>);

impl Source {
    fn documents(documents: Vec<CultNetRawDocumentRecord>) -> Self {
        Self(Arc::new(Mutex::new(SourceState {
            documents,
            ..Default::default()
        })))
    }

    fn fail_once(documents: Vec<CultNetRawDocumentRecord>) -> Self {
        Self(Arc::new(Mutex::new(SourceState {
            documents,
            failures_remaining: 1,
            ..Default::default()
        })))
    }
}

/// `document` without the source provenance a peer reports about itself.
fn stripped(document: &CultNetRawDocumentRecord) -> CultNetRawDocumentRecord {
    CultNetRawDocumentRecord {
        source_runtime_id: None,
        source_agent_id: None,
        source_role: None,
        tags: None,
        ..document.clone()
    }
}

impl CultMeshRudpSnapshotSource for Source {
    fn raw_snapshot(
        &mut self,
        _: &CultMeshRudpSnapshotQuery,
    ) -> Result<Vec<CultNetRawDocumentRecord>> {
        let mut state = self.0.lock().unwrap();
        state.calls += 1;
        if state.failures_remaining > 0 {
            state.failures_remaining -= 1;
            anyhow::bail!("injected source failure");
        }
        Ok(state.documents.clone())
    }
}

/// A source that serves `Source`'s records without source provenance, and says
/// so to admission through `served_record`. `Source` keeps the default.
#[derive(Clone, Default)]
struct StrippingSource {
    inner: Source,
    /// Refuses to say what it would serve.
    served_record_fails: bool,
}

impl CultMeshRudpSnapshotSource for StrippingSource {
    fn raw_snapshot(
        &mut self,
        query: &CultMeshRudpSnapshotQuery,
    ) -> Result<Vec<CultNetRawDocumentRecord>> {
        Ok(self.inner.raw_snapshot(query)?.iter().map(stripped).collect())
    }

    fn served_record(
        &mut self,
        document: &CultNetRawDocumentRecord,
    ) -> Result<CultNetRawDocumentRecord> {
        if self.served_record_fails {
            anyhow::bail!("injected served-record failure");
        }
        Ok(stripped(document))
    }
}

fn stripping_server(
    options: CultMeshRudpDocumentServerOptions,
    sink: Sink,
    source: StrippingSource,
) -> Result<CultMeshRudpDocumentServer<Sink, StrippingSource, Clock>> {
    CultMeshRudpDocumentServer::new(
        UdpSocket::bind("127.0.0.1:0")?,
        sink,
        source,
        Clock::new(64_000),
        options,
    )
}

#[derive(Clone)]
struct Clock {
    unix: Arc<AtomicU64>,
    monotonic: Arc<AtomicU64>,
}

impl Clock {
    fn new(now: u64) -> Self {
        Self {
            unix: Arc::new(AtomicU64::new(now)),
            monotonic: Arc::new(AtomicU64::new(now)),
        }
    }

    fn set(&self, now: u64) {
        self.unix.store(now, Ordering::SeqCst);
        self.monotonic.store(now, Ordering::SeqCst);
    }

    fn set_unix(&self, now: u64) {
        self.unix.store(now, Ordering::SeqCst);
    }

    fn set_monotonic(&self, now: u64) {
        self.monotonic.store(now, Ordering::SeqCst);
    }
}

impl CultMeshRudpServerClock for Clock {
    fn now_unix_millis(&self) -> u64 {
        self.unix.load(Ordering::SeqCst)
    }

    fn now_monotonic_millis(&self) -> u64 {
        self.monotonic.load(Ordering::SeqCst)
    }
}

type Server = CultMeshRudpDocumentServer<Sink, Source, Clock>;

fn document(key: &str, payload: Vec<u8>) -> CultNetRawDocumentRecord {
    CultNetRawDocumentRecord {
        schema_id: "test.raw.v1".into(),
        record_key: key.into(),
        stored_at: "2026-09-03T00:00:00Z".into(),
        payload_encoding: CultNetRawPayloadEncoding::Messagepack,
        payload,
        source_runtime_id: Some(format!("runtime-{key}")),
        source_agent_id: None,
        source_role: None,
        tags: None,
    }
}

fn server(
    options: CultMeshRudpDocumentServerOptions,
    clock: Clock,
    sink: Sink,
    source: Source,
) -> Result<Server> {
    CultMeshRudpDocumentServer::new(
        UdpSocket::bind("127.0.0.1:0")?,
        sink,
        source,
        clock,
        options,
    )
}

fn client(target: SocketAddr, id: u32) -> Result<CultNetRudpSocketTransportConnection> {
    client_on(UdpSocket::bind("127.0.0.1:0")?, target, id, 1)
}

fn client_on(
    socket: UdpSocket,
    target: SocketAddr,
    id: u32,
    initial_sequence: u32,
) -> Result<CultNetRudpSocketTransportConnection> {
    socket.set_nonblocking(true)?;
    CultNetRudpSocketTransportConnection::new(CultNetRudpSocketTransportOptions {
        media_delivery: None,
        runtime_id: "test-client".into(),
        socket,
        mode: CultNetRudpSocketMode::Client,
        remote_addr: Some(target),
        connection_id: id,
        initial_sequence: Some(initial_sequence),
        resend_delay_ms: 10,
        transport_id: None,
        max_payload_bytes: None,
        max_fragment_bytes: Some(1200),
        max_pending_reliable_packets: Some(64),
        media_reliable_expire_after_ms: None,
        reconnect_policy: None,
    })
}

fn connect<Q: CultMeshRudpSnapshotSource>(
    server: &mut CultMeshRudpDocumentServer<Sink, Q, Clock>,
    clients: &mut [&mut CultNetRudpSocketTransportConnection],
) -> Result<()> {
    for client in &mut *clients {
        client.connect(Vec::new())?;
    }
    for _ in 0..500 {
        server.poll_once()?;
        for client in &mut *clients {
            client.receive_once()?;
        }
        if clients.iter().all(|client| client.connected()) {
            for _ in 0..clients.len() {
                server.poll_once()?;
            }
            return Ok(());
        }
        thread::sleep(Duration::from_millis(1));
    }
    anyhow::bail!("client connection timed out")
}

/// The encoded size of the snapshot response that carries `document` alone
/// under a one-character message id, the shortest CultNet encodes: the
/// smallest response that could ever serve it.
fn served_alone_bytes(document: &CultNetRawDocumentRecord) -> Result<usize> {
    Ok(encode_cultnet_message_to_vec(
        &CultNetMessage::SnapshotResponseRaw {
            message_id: "r".into(),
            documents: vec![document.clone()],
        },
        CultNetWireContract::CultNetSchemaV0,
    )?
    .len())
}

/// A document keyed `key` whose served-alone response is exactly `bytes`.
fn document_served_at(key: &str, bytes: usize) -> Result<CultNetRawDocumentRecord> {
    let probe = bytes / 2;
    let base = served_alone_bytes(&document(key, vec![7; probe]))?;
    let fitted = document(key, vec![7; probe + bytes - base]);
    assert_eq!(
        served_alone_bytes(&fitted)?,
        bytes,
        "fixture: the served size is linear in the payload"
    );
    Ok(fitted)
}

/// Polls the server and client until the client receives a frame.
fn serve_until_frame<Q: CultMeshRudpSnapshotSource>(
    server: &mut CultMeshRudpDocumentServer<Sink, Q, Clock>,
    client: &mut CultNetRudpSocketTransportConnection,
) -> Result<Vec<u8>> {
    for _ in 0..2_000 {
        let outcome = server.poll_once()?;
        assert!(
            !matches!(outcome, CultMeshRudpPollOutcome::ApplicationRejected(_)),
            "{outcome:?}"
        );
        if let Some(frame) = client.receive_once()? {
            return Ok(frame.payload);
        }
        thread::sleep(Duration::from_millis(1));
    }
    anyhow::bail!("the client received no frame")
}

/// Polls the server, and the client so its sends progress, until the server
/// returns a rejection or the sink holds `receipts`.
fn poll_until_rejected_or_stored<Q: CultMeshRudpSnapshotSource>(
    server: &mut CultMeshRudpDocumentServer<Sink, Q, Clock>,
    client: &mut CultNetRudpSocketTransportConnection,
    sink: &Sink,
    receipts: usize,
) -> Result<Option<CultMeshRudpRejectionReason>> {
    for _ in 0..5_000 {
        client.receive_once()?;
        client.poll_resends()?;
        match server.poll_once()? {
            CultMeshRudpPollOutcome::ApplicationRejected(rejection) => {
                return Ok(Some(rejection.reason));
            }
            CultMeshRudpPollOutcome::Idle => thread::sleep(Duration::from_millis(1)),
            _ => {}
        }
        if sink.0.lock().unwrap().receipts.len() >= receipts {
            return Ok(None);
        }
    }
    anyhow::bail!("the put was neither refused nor stored")
}

/// What a refused peer is sent: a `cultnet.error.v0`, then the goodbye. The
/// refused message is never acknowledged, so its receipt stays pending until the
/// goodbye invalidates it. Returns the refusal's text.
fn refusal_seen_by(
    client: &mut CultNetRudpSocketTransportConnection,
    receipt: &CultNetRudpReliableSendReceipt,
) -> Result<String> {
    let mut refusal = None;
    for _ in 0..2_000 {
        if let Some(frame) = client.receive_once()? {
            refusal = Some(decode_cultnet_message_from_slice(
                &frame.payload,
                CultNetWireContract::CultNetSchemaV0,
            )?);
            break;
        }
        thread::sleep(Duration::from_millis(1));
    }
    let Some(CultNetMessage::Error {
        error,
        code,
        details,
    }) = refusal
    else {
        panic!("the refused peer was sent no refusal: {refusal:?}");
    };
    assert_eq!((code, details), (None, None));
    assert_eq!(
        client.reliable_send_status(receipt),
        CultNetRudpReliableSendStatus::Pending,
        "a refusal must not acknowledge the refused message"
    );
    for _ in 0..2_000 {
        client.receive_once()?;
        if !client.connected() {
            break;
        }
        thread::sleep(Duration::from_millis(1));
    }
    assert!(!client.connected(), "the refusal is followed by a goodbye");
    assert_eq!(
        client.reliable_send_status(receipt),
        CultNetRudpReliableSendStatus::Invalidated
    );
    Ok(error)
}

/// Reads every datagram waiting at the server, such as a refused peer's
/// acknowledgement of its refusal, which reaches no session and is dropped.
fn drain_until_idle<Q: CultMeshRudpSnapshotSource>(
    server: &mut CultMeshRudpDocumentServer<Sink, Q, Clock>,
) -> Result<()> {
    for _ in 0..100 {
        match server.poll_once()? {
            CultMeshRudpPollOutcome::Idle => return Ok(()),
            CultMeshRudpPollOutcome::Handled => {}
            rejected => panic!("draining must not reject: {rejected:?}"),
        }
    }
    anyhow::bail!("the server never went idle")
}

fn send(client: &mut CultNetRudpSocketTransportConnection, message: &CultNetMessage) -> Result<()> {
    client.send(
        "schema",
        encode_cultnet_message_to_vec(message, CultNetWireContract::CultNetSchemaV0)?,
    )
}

fn send_reliable(
    client: &mut CultNetRudpSocketTransportConnection,
    message: &CultNetMessage,
) -> Result<CultNetRudpReliableSendReceipt> {
    client.send_reliable(
        "schema",
        encode_cultnet_message_to_vec(message, CultNetWireContract::CultNetSchemaV0)?,
    )
}

#[test]
fn same_connection_id_is_peer_scoped_and_raw_bytes_are_untouched() -> Result<()> {
    let clock = Clock::new(42_000);
    let sink = Sink::default();
    let mut server = server(Default::default(), clock, sink.clone(), Source::default())?;
    let mut a = client(server.local_addr()?, 77)?;
    let mut b = client(server.local_addr()?, 77)?;
    connect(&mut server, &mut [&mut a, &mut b])?;

    let a_bytes = vec![0xc4, 0x04, 0x00, 0xff, 0x80, 0x01];
    let b_bytes = vec![0x92, 0x01, 0xc4, 0x02, 0x00, 0xff];
    send(
        &mut a,
        &CultNetMessage::DocumentPutRaw {
            message_id: "a".into(),
            document: document("a", a_bytes.clone()),
        },
    )?;
    send(
        &mut b,
        &CultNetMessage::DocumentPutRaw {
            message_id: "b".into(),
            document: document("b", b_bytes.clone()),
        },
    )?;
    for _ in 0..500 {
        server.poll_once()?;
        a.receive_once()?;
        b.receive_once()?;
        if sink.0.lock().unwrap().receipts.len() == 2 {
            break;
        }
    }

    let receipts = sink.0.lock().unwrap();
    assert_eq!(server.session_count(), 2);
    assert_eq!(receipts.receipts.len(), 2);
    assert_ne!(
        receipts.receipts[0].session.remote_addr,
        receipts.receipts[1].session.remote_addr
    );
    assert_eq!(
        receipts
            .receipts
            .iter()
            .find(|r| r.message_id == "a")
            .unwrap()
            .document
            .payload,
        a_bytes
    );
    assert_eq!(
        receipts
            .receipts
            .iter()
            .find(|r| r.message_id == "b")
            .unwrap()
            .document
            .payload,
        b_bytes
    );
    assert!(
        receipts
            .receipts
            .iter()
            .all(|r| r.received_at_unix_millis == 42_000)
    );
    Ok(())
}

#[test]
fn a_second_connect_from_a_client_replaces_its_session_and_a_fresh_epoch_is_separate() -> Result<()>
{
    let clock = Clock::new(45_000);
    let sink = Sink::default();
    let mut server = server(Default::default(), clock, sink.clone(), Source::default())?;
    let target = server.local_addr()?;
    let mut incumbent = client(target, 91)?;
    connect(&mut server, &mut [&mut incumbent])?;

    incumbent.connect(Vec::new())?;
    server.poll_once()?;
    incumbent.receive_once()?;
    assert_eq!(server.session_count(), 1);

    send(
        &mut incumbent,
        &CultNetMessage::DocumentPutRaw {
            message_id: "after-duplicate".into(),
            document: document("incumbent", vec![1, 2, 3]),
        },
    )?;
    for _ in 0..20 {
        server.poll_once()?;
        if sink.0.lock().unwrap().receipts.len() == 1 {
            break;
        }
    }
    assert_eq!(sink.0.lock().unwrap().receipts.len(), 1);

    let mut fresh_epoch = client(target, 92)?;
    connect(&mut server, &mut [&mut fresh_epoch])?;
    assert_eq!(server.session_count(), 2);
    Ok(())
}

/// A client that restarts on the same address and connection id starts a new
/// session: its Connect carries a sequence the accepted one did not, so the
/// server does not mistake it for a retransmit, and what it sends is delivered.
#[test]
fn a_restarted_client_on_the_same_address_and_connection_id_is_admitted() -> Result<()> {
    let sink = Sink::default();
    let mut server = server(
        Default::default(),
        Clock::new(46_000),
        sink.clone(),
        Source::default(),
    )?;
    let target = server.local_addr()?;
    let shared = UdpSocket::bind("127.0.0.1:0")?;
    let twin = shared.try_clone()?;
    let mut first = client_on(shared, target, 93, 50_000)?;
    connect(&mut server, &mut [&mut first])?;
    for index in 0..3 {
        send(
            &mut first,
            &CultNetMessage::DocumentPutRaw {
                message_id: format!("before-{index}"),
                document: document("before", vec![index]),
            },
        )?;
    }
    for _ in 0..40 {
        server.poll_once()?;
        first.receive_once()?;
    }
    assert_eq!(sink.0.lock().unwrap().receipts.len(), 3);

    let mut restarted = client_on(twin, target, 93, 7)?;
    connect(&mut server, &mut [&mut restarted])?;
    assert_eq!(server.session_count(), 1);
    send(
        &mut restarted,
        &CultNetMessage::DocumentPutRaw {
            message_id: "after-restart".into(),
            document: document("after", vec![9]),
        },
    )?;
    for _ in 0..40 {
        server.poll_once()?;
        restarted.receive_once()?;
    }
    let receipts = sink.0.lock().unwrap();
    assert!(
        receipts
            .receipts
            .iter()
            .any(|r| r.message_id == "after-restart"),
        "the restarted client's frame was not delivered"
    );
    Ok(())
}

/// A restarted client is a new session, and starts with a new payload budget:
/// what the old one spent is not charged to it.
#[test]
fn a_restarted_client_starts_with_a_fresh_payload_budget() -> Result<()> {
    let put = |id: &str| CultNetMessage::DocumentPutRaw {
        message_id: id.into(),
        document: document("budget", vec![7; 64]),
    };
    let encoded =
        encode_cultnet_message_to_vec(&put("put-a"), CultNetWireContract::CultNetSchemaV0)?;
    let budget = encoded
        .len()
        .max(served_alone_bytes(&document("budget", vec![7; 64]))?);
    let options = CultMeshRudpDocumentServerOptions {
        max_admitted_payload_bytes: budget * 2,
        max_admitted_payload_bytes_per_session: budget,
        max_snapshot_response_bytes: budget,
        ..Default::default()
    };
    let sink = Sink::default();
    let mut server = server(options, Clock::new(47_000), sink.clone(), Source::default())?;
    let target = server.local_addr()?;
    let shared = UdpSocket::bind("127.0.0.1:0")?;
    let twin = shared.try_clone()?;
    let mut first = client_on(shared, target, 94, 50_000)?;
    connect(&mut server, &mut [&mut first])?;
    send(&mut first, &put("put-a"))?;
    for _ in 0..20 {
        server.poll_once()?;
    }
    assert_eq!(sink.0.lock().unwrap().receipts.len(), 1);

    let mut restarted = client_on(twin, target, 94, 7)?;
    connect(&mut server, &mut [&mut restarted])?;
    send(&mut restarted, &put("put-b"))?;
    for _ in 0..20 {
        server.poll_once()?;
    }
    assert_eq!(
        sink.0.lock().unwrap().receipts.len(),
        2,
        "the restarted client was charged what the old session spent"
    );
    Ok(())
}

#[test]
fn application_rejection_is_nonfatal_peer_scoped_refused_to_the_peer_and_unacknowledged() -> Result<()> {
    let clock = Clock::new(48_000);
    let sink = Sink::fail_once();
    let source = Source::fail_once(vec![document("snapshot", vec![4, 5, 6])]);
    let mut server = server(
        Default::default(),
        clock.clone(),
        sink.clone(),
        source.clone(),
    )?;
    let target = server.local_addr()?;

    let mut publisher = client(target, 101)?;
    let mut snapshot_client = client(target, 102)?;
    let mut survivor = client(target, 103)?;
    connect(
        &mut server,
        &mut [&mut publisher, &mut snapshot_client, &mut survivor],
    )?;
    let publish_receipt = send_reliable(
        &mut publisher,
        &CultNetMessage::DocumentPutRaw {
            message_id: "rejected-put".into(),
            document: document("rejected", vec![0, 255, 1]),
        },
    )?;
    let CultMeshRudpPollOutcome::ApplicationRejected(rejection) = server.poll_once()? else {
        panic!("sink rejection must be returned as a nonfatal poll outcome");
    };
    assert_eq!(rejection.session.connection_id, 101);
    assert_eq!(
        rejection.operation,
        CultMeshRudpApplicationOperation::DocumentPutRaw
    );
    assert_eq!(rejection.message_id, "rejected-put");
    assert_eq!(
        rejection.reason,
        CultMeshRudpRejectionReason::SinkRefused("injected sink failure".into())
    );
    assert_eq!(server.session_count(), 2);
    assert_eq!(
        refusal_seen_by(&mut publisher, &publish_receipt)?,
        "injected sink failure"
    );
    drain_until_idle(&mut server)?;

    let snapshot_receipt = send_reliable(
        &mut snapshot_client,
        &CultNetMessage::SnapshotRequest {
            message_id: "rejected-snapshot".into(),
            schema_ids: None,
            record_keys: None,
        },
    )?;
    let CultMeshRudpPollOutcome::ApplicationRejected(rejection) = server.poll_once()? else {
        panic!("source rejection must be returned as a nonfatal poll outcome");
    };
    assert_eq!(rejection.session.connection_id, 102);
    assert_eq!(
        rejection.operation,
        CultMeshRudpApplicationOperation::SnapshotRequest
    );
    assert_eq!(rejection.message_id, "rejected-snapshot");
    assert_eq!(
        rejection.reason,
        CultMeshRudpRejectionReason::SnapshotSourceFailed("injected source failure".into())
    );
    assert_eq!(server.session_count(), 1);
    assert_eq!(
        refusal_seen_by(&mut snapshot_client, &snapshot_receipt)?,
        "injected source failure"
    );
    drain_until_idle(&mut server)?;

    let survivor_receipt = send_reliable(
        &mut survivor,
        &CultNetMessage::DocumentPutRaw {
            message_id: "accepted-after-rejections".into(),
            document: document("survivor", vec![9, 8, 7]),
        },
    )?;
    assert_eq!(server.poll_once()?, CultMeshRudpPollOutcome::Handled);
    for _ in 0..20 {
        survivor.receive_once()?;
        if survivor.reliable_send_status(&survivor_receipt)
            == CultNetRudpReliableSendStatus::Acknowledged
        {
            break;
        }
    }
    assert_eq!(
        survivor.reliable_send_status(&survivor_receipt),
        CultNetRudpReliableSendStatus::Acknowledged
    );
    assert_eq!(sink.0.lock().unwrap().receipts.len(), 1);
    assert_eq!(source.0.lock().unwrap().calls, 1);
    Ok(())
}

#[test]
fn snapshot_response_resends_until_acknowledged() -> Result<()> {
    let clock = Clock::new(51_000);
    let expected = document("catalog", vec![0x81, 0xa1, 0x78, 0x2a]);
    let source = Source::documents(vec![expected.clone()]);
    let options = CultMeshRudpDocumentServerOptions {
        resend_delay: Duration::from_millis(10),
        ..Default::default()
    };
    let mut server = server(options, clock.clone(), Sink::default(), source)?;
    let mut client = client(server.local_addr()?, 88)?;
    connect(&mut server, &mut [&mut client])?;
    send(
        &mut client,
        &CultNetMessage::SnapshotRequest {
            message_id: "snapshot".into(),
            schema_ids: Some(vec!["test.raw.v1".into()]),
            record_keys: Some(vec!["catalog".into()]),
        },
    )?;
    server.poll_once()?;

    clock.set(51_011);
    assert_eq!(server.maintain()?.packets_resent, 1);
    let response = loop {
        if let Some(frame) = client.receive_once()? {
            break decode_cultnet_message_from_slice(
                &frame.payload,
                CultNetWireContract::CultNetSchemaV0,
            )?;
        }
    };
    assert_eq!(
        response,
        CultNetMessage::SnapshotResponseRaw {
            message_id: "snapshot".into(),
            documents: vec![expected],
        }
    );

    for _ in 0..20 {
        server.poll_once()?;
    }
    clock.set(51_022);
    assert_eq!(server.maintain()?.packets_resent, 0);
    Ok(())
}

#[test]
fn payload_and_snapshot_output_budgets_fail_closed() -> Result<()> {
    let clock = Clock::new(55_000);
    let sink = Sink::default();
    // The message id is long enough that the put's frame is at least its
    // served-alone response, so the snapshot limit below admits it.
    let message = CultNetMessage::DocumentPutRaw {
        message_id: "budget-exact".into(),
        document: document("budget", vec![7; 64]),
    };
    let encoded = encode_cultnet_message_to_vec(&message, CultNetWireContract::CultNetSchemaV0)?;
    // The put's frame is exactly both budgets, so the first put is admitted only
    // if a charge equal to a limit fits it.
    let budget = encoded.len();
    assert!(
        served_alone_bytes(&document("budget", vec![7; 64]))? <= budget,
        "fixture: the snapshot limit must admit the put"
    );
    let options = CultMeshRudpDocumentServerOptions {
        max_admitted_payload_bytes: budget,
        max_admitted_payload_bytes_per_session: budget,
        max_snapshot_response_bytes: budget,
        ..Default::default()
    };
    let mut budget_server = server(options, clock, sink.clone(), Source::default())?;
    let target = budget_server.local_addr()?;
    let mut first = client(target, 111)?;
    let mut second = client(target, 112)?;
    connect(&mut budget_server, &mut [&mut first, &mut second])?;
    send(&mut first, &message)?;
    budget_server.poll_once()?;
    send(&mut second, &message)?;
    budget_server.poll_once()?;
    assert_eq!(sink.0.lock().unwrap().receipts.len(), 1);

    let oversized = Source::documents(vec![document("oversized", vec![8; 512])]);
    let options = CultMeshRudpDocumentServerOptions {
        max_snapshot_response_bytes: 128,
        ..Default::default()
    };
    let mut snapshot_server = server(options, Clock::new(56_000), Sink::default(), oversized)?;
    let mut snapshot_client = client(snapshot_server.local_addr()?, 113)?;
    connect(&mut snapshot_server, &mut [&mut snapshot_client])?;
    let receipt = send_reliable(
        &mut snapshot_client,
        &CultNetMessage::SnapshotRequest {
            message_id: "too-large".into(),
            schema_ids: None,
            record_keys: None,
        },
    )?;
    let CultMeshRudpPollOutcome::ApplicationRejected(rejection) = snapshot_server.poll_once()?
    else {
        panic!("oversized snapshot must reject only its session");
    };
    assert_eq!(
        rejection.operation,
        CultMeshRudpApplicationOperation::SnapshotRequest
    );
    assert_eq!(rejection.message_id, "too-large");
    assert!(matches!(
        rejection.reason,
        CultMeshRudpRejectionReason::SnapshotResponseTooLarge {
            response_bytes,
            max_snapshot_response_bytes: 128,
        } if response_bytes > 128
    ));
    assert_eq!(snapshot_server.session_count(), 0);
    assert_eq!(
        refusal_seen_by(&mut snapshot_client, &receipt)?,
        rejection.reason.to_string()
    );
    Ok(())
}

/// A put is admitted only if some snapshot request could return it. One byte
/// over the served bound is refused before the sink sees it; exactly at the
/// bound is admitted, and a snapshot then serves it at exactly that size. The
/// source keeps the default `served_record`, so the record is sized as received.
#[test]
fn a_put_the_server_could_never_serve_is_refused_and_one_at_the_bound_is_served() -> Result<()> {
    let at_bound = document("fit", vec![7; 100]);
    let over_bound = document("fit", vec![7; 101]);
    let limit = served_alone_bytes(&at_bound)?;
    assert_eq!(
        served_alone_bytes(&over_bound)?,
        limit + 1,
        "fixture: the two documents must straddle the bound by one byte"
    );
    let options = CultMeshRudpDocumentServerOptions {
        max_snapshot_response_bytes: limit,
        ..Default::default()
    };
    let sink = Sink::default();
    let source = Source::default();
    let mut server = server(options, Clock::new(60_000), sink.clone(), source.clone())?;
    let target = server.local_addr()?;

    let mut refused = client(target, 121)?;
    let mut writer = client(target, 122)?;
    connect(&mut server, &mut [&mut refused, &mut writer])?;
    let refused_receipt = send_reliable(
        &mut refused,
        &CultNetMessage::DocumentPutRaw {
            message_id: "put-over".into(),
            document: over_bound,
        },
    )?;
    let CultMeshRudpPollOutcome::ApplicationRejected(rejection) = server.poll_once()? else {
        panic!("an unservable put must be refused as an application rejection");
    };
    assert_eq!(rejection.session.connection_id, 121);
    assert_eq!(
        rejection.operation,
        CultMeshRudpApplicationOperation::DocumentPutRaw
    );
    assert_eq!(rejection.message_id, "put-over");
    assert_eq!(
        rejection.reason,
        CultMeshRudpRejectionReason::DocumentUnservable {
            response_bytes: limit + 1,
            max_snapshot_response_bytes: limit,
            fragment_count: 1,
            max_fragment_count: 1024,
        }
    );
    let sentence = rejection.reason.to_string();
    assert!(sentence.contains(&(limit + 1).to_string()) && sentence.contains(&limit.to_string()));
    assert!(sink.0.lock().unwrap().receipts.is_empty());
    assert_eq!(server.session_count(), 1);
    // The peer hears the refusal and why, rather than waiting on a put that
    // will never be acknowledged.
    assert_eq!(refusal_seen_by(&mut refused, &refused_receipt)?, sentence);

    send(
        &mut writer,
        &CultNetMessage::DocumentPutRaw {
            message_id: "put-at".into(),
            document: at_bound.clone(),
        },
    )?;
    for _ in 0..500 {
        let outcome = server.poll_once()?;
        assert!(
            !matches!(outcome, CultMeshRudpPollOutcome::ApplicationRejected(_)),
            "{outcome:?}"
        );
        if !sink.0.lock().unwrap().receipts.is_empty() {
            break;
        }
        thread::sleep(Duration::from_millis(1));
    }
    let stored: Vec<_> = sink
        .0
        .lock()
        .unwrap()
        .receipts
        .iter()
        .map(|receipt| receipt.document.clone())
        .collect();
    assert_eq!(stored, vec![at_bound.clone()]);
    source.0.lock().unwrap().documents = stored;

    send(
        &mut writer,
        &CultNetMessage::SnapshotRequest {
            message_id: "r".into(),
            schema_ids: None,
            record_keys: None,
        },
    )?;
    let mut served = None;
    for _ in 0..500 {
        let outcome = server.poll_once()?;
        assert!(
            !matches!(outcome, CultMeshRudpPollOutcome::ApplicationRejected(_)),
            "{outcome:?}"
        );
        if let Some(frame) = writer.receive_once()? {
            served = Some(frame.payload);
            break;
        }
        thread::sleep(Duration::from_millis(1));
    }
    let served = served.expect("the admitted document must be served");
    assert_eq!(served.len(), limit);
    assert_eq!(
        decode_cultnet_message_from_slice(&served, CultNetWireContract::CultNetSchemaV0)?,
        CultNetMessage::SnapshotResponseRaw {
            message_id: "r".into(),
            documents: vec![at_bound],
        }
    );
    Ok(())
}

/// A source that serves records without provenance says so through
/// `served_record`, and admission sizes that: a put over the bound only in its
/// received form is admitted and served at exactly the bound.
#[test]
fn a_put_is_sized_as_its_source_would_serve_it() -> Result<()> {
    let provenance = |mut document: CultNetRawDocumentRecord| {
        document.source_agent_id = Some("provenance-agent-that-is-not-served".into());
        document.source_role = Some("provenance-role".into());
        document.tags = Some(vec!["provenance".into(), "tags".into()]);
        document
    };
    let at_bound = provenance(document("fit", vec![7; 100]));
    let over_bound = provenance(document("fit", vec![7; 101]));
    let limit = served_alone_bytes(&stripped(&at_bound))?;
    assert_eq!(
        served_alone_bytes(&stripped(&over_bound))?,
        limit + 1,
        "fixture: the two documents must straddle the bound by one byte as served"
    );
    assert!(
        served_alone_bytes(&at_bound)? > limit + 1,
        "fixture: the at-bound put is over the bound as received"
    );
    let options = CultMeshRudpDocumentServerOptions {
        max_snapshot_response_bytes: limit,
        ..Default::default()
    };
    let sink = Sink::default();
    let source = StrippingSource::default();
    let mut server = stripping_server(options, sink.clone(), source.clone())?;
    let target = server.local_addr()?;

    let mut refused = client(target, 151)?;
    connect(&mut server, &mut [&mut refused])?;
    send(
        &mut refused,
        &CultNetMessage::DocumentPutRaw {
            message_id: "put-over".into(),
            document: over_bound,
        },
    )?;
    assert_eq!(
        poll_until_rejected_or_stored(&mut server, &mut refused, &sink, 1)?,
        Some(CultMeshRudpRejectionReason::DocumentUnservable {
            response_bytes: limit + 1,
            max_snapshot_response_bytes: limit,
            fragment_count: 1,
            max_fragment_count: 1024,
        })
    );

    let mut writer = client(target, 152)?;
    connect(&mut server, &mut [&mut writer])?;
    send(
        &mut writer,
        &CultNetMessage::DocumentPutRaw {
            message_id: "put-at".into(),
            document: at_bound.clone(),
        },
    )?;
    assert_eq!(
        poll_until_rejected_or_stored(&mut server, &mut writer, &sink, 1)?,
        None
    );
    assert_eq!(
        sink.0.lock().unwrap().receipts[0].document,
        at_bound,
        "the sink receives the record as it was sent"
    );
    source.inner.0.lock().unwrap().documents = vec![at_bound.clone()];
    send(
        &mut writer,
        &CultNetMessage::SnapshotRequest {
            message_id: "r".into(),
            schema_ids: None,
            record_keys: None,
        },
    )?;
    let served = serve_until_frame(&mut server, &mut writer)?;
    assert_eq!(served.len(), limit);
    assert_eq!(
        decode_cultnet_message_from_slice(&served, CultNetWireContract::CultNetSchemaV0)?,
        CultNetMessage::SnapshotResponseRaw {
            message_id: "r".into(),
            documents: vec![stripped(&at_bound)],
        }
    );
    Ok(())
}

/// A source that cannot say what it would serve refuses the put.
#[test]
fn a_put_whose_served_record_the_source_cannot_give_is_refused() -> Result<()> {
    let sink = Sink::default();
    let source = StrippingSource {
        served_record_fails: true,
        ..Default::default()
    };
    let mut server = stripping_server(Default::default(), sink.clone(), source)?;
    let mut refused = client(server.local_addr()?, 161)?;
    connect(&mut server, &mut [&mut refused])?;
    send(
        &mut refused,
        &CultNetMessage::DocumentPutRaw {
            message_id: "put".into(),
            document: document("unsized", vec![1, 2, 3]),
        },
    )?;
    assert_eq!(
        poll_until_rejected_or_stored(&mut server, &mut refused, &sink, 1)?,
        Some(CultMeshRudpRejectionReason::SnapshotSourceFailed(
            "injected served-record failure".into()
        ))
    );
    assert!(sink.0.lock().unwrap().receipts.is_empty());
    Ok(())
}

/// A put is admitted only if its served response fits the fragments one
/// response may take: here eight of 100 bytes, the reliable queue, although the
/// byte limit is far larger. Eight fragments are admitted and served; nine are
/// refused, and so is Soul's 2000-byte probe, each naming its sizes.
#[test]
fn a_put_whose_response_would_overflow_the_reliable_queue_is_refused() -> Result<()> {
    let options = CultMeshRudpDocumentServerOptions {
        max_fragment_bytes: 100,
        max_pending_reliable_packets_per_session: 8,
        max_snapshot_response_bytes: 8192,
        ..Default::default()
    };
    let sink = Sink::default();
    let source = Source::default();
    let mut server = server(options, Clock::new(61_000), sink.clone(), source.clone())?;
    let target = server.local_addr()?;
    let unservable = |response_bytes, fragment_count| {
        Some(CultMeshRudpRejectionReason::DocumentUnservable {
            response_bytes,
            max_snapshot_response_bytes: 8192,
            fragment_count,
            max_fragment_count: 8,
        })
    };

    for (id, bytes, fragments) in [(131, 2000, 20), (132, 801, 9)] {
        let mut refused = client(target, id)?;
        connect(&mut server, &mut [&mut refused])?;
        send(
            &mut refused,
            &CultNetMessage::DocumentPutRaw {
                message_id: "put-over".into(),
                document: document_served_at("queue", bytes)?,
            },
        )?;
        let reason = poll_until_rejected_or_stored(&mut server, &mut refused, &sink, 1)?;
        assert_eq!(reason, unservable(bytes, fragments));
        let sentence = reason.unwrap().to_string();
        assert!(
            sentence.contains(&format!("{bytes} bytes in {fragments} fragments"))
                && sentence.contains("8 fragments"),
            "{sentence}"
        );
    }
    assert!(sink.0.lock().unwrap().receipts.is_empty());

    let at_bound = document_served_at("queue", 800)?;
    let mut writer = client(target, 133)?;
    connect(&mut server, &mut [&mut writer])?;
    send(
        &mut writer,
        &CultNetMessage::DocumentPutRaw {
            message_id: "put-at".into(),
            document: at_bound.clone(),
        },
    )?;
    assert_eq!(
        poll_until_rejected_or_stored(&mut server, &mut writer, &sink, 1)?,
        None
    );
    source.0.lock().unwrap().documents = vec![at_bound.clone()];
    send(
        &mut writer,
        &CultNetMessage::SnapshotRequest {
            message_id: "r".into(),
            schema_ids: None,
            record_keys: None,
        },
    )?;
    let served = serve_until_frame(&mut server, &mut writer)?;
    assert_eq!(served.len(), 800);
    assert_eq!(
        decode_cultnet_message_from_slice(&served, CultNetWireContract::CultNetSchemaV0)?,
        CultNetMessage::SnapshotResponseRaw {
            message_id: "r".into(),
            documents: vec![at_bound],
        }
    );
    Ok(())
}

/// One message carries at most 65535 fragments, however deep the reliable
/// queue: a response of 65536 one-byte fragments is refused, one of 65535 is not.
#[test]
fn a_put_whose_response_needs_more_than_65535_fragments_is_refused() -> Result<()> {
    let options = CultMeshRudpDocumentServerOptions {
        max_fragment_bytes: 1,
        max_pending_reliable_packets_per_session: 100_000,
        max_snapshot_response_bytes: 70_000,
        ..Default::default()
    };
    let sink = Sink::default();
    let mut server = server(options, Clock::new(62_000), sink.clone(), Source::default())?;
    let target = server.local_addr()?;

    let mut refused = client(target, 141)?;
    connect(&mut server, &mut [&mut refused])?;
    send(
        &mut refused,
        &CultNetMessage::DocumentPutRaw {
            message_id: "put-over".into(),
            document: document_served_at("wide", 65_536)?,
        },
    )?;
    assert_eq!(
        poll_until_rejected_or_stored(&mut server, &mut refused, &sink, 1)?,
        Some(CultMeshRudpRejectionReason::DocumentUnservable {
            response_bytes: 65_536,
            max_snapshot_response_bytes: 70_000,
            fragment_count: 65_536,
            max_fragment_count: 65_535,
        })
    );

    let mut writer = client(target, 142)?;
    connect(&mut server, &mut [&mut writer])?;
    send(
        &mut writer,
        &CultNetMessage::DocumentPutRaw {
            message_id: "put-at".into(),
            document: document_served_at("wide", 65_535)?,
        },
    )?;
    assert_eq!(
        poll_until_rejected_or_stored(&mut server, &mut writer, &sink, 1)?,
        None
    );
    Ok(())
}

/// The limits must leave room for an empty snapshot response, the smallest the
/// server sends; exactly enough room is accepted.
#[test]
fn options_that_cannot_carry_an_empty_snapshot_response_are_refused() -> Result<()> {
    let empty = encode_cultnet_message_to_vec(
        &CultNetMessage::SnapshotResponseRaw {
            message_id: "r".into(),
            documents: Vec::new(),
        },
        CultNetWireContract::CultNetSchemaV0,
    )?
    .len();
    let build = |options: CultMeshRudpDocumentServerOptions| {
        server(options, Clock::new(63_000), Sink::default(), Source::default()).map(|_| ())
    };
    let bytes = |max_snapshot_response_bytes| CultMeshRudpDocumentServerOptions {
        max_snapshot_response_bytes,
        ..Default::default()
    };
    let fragments = |max_pending_reliable_packets_per_session| CultMeshRudpDocumentServerOptions {
        max_fragment_bytes: 1,
        max_pending_reliable_packets_per_session,
        ..Default::default()
    };
    build(bytes(empty))?;
    let error = build(bytes(empty - 1)).unwrap_err().to_string();
    assert!(error.contains("max_snapshot_response_bytes"), "{error}");
    build(fragments(empty))?;
    let error = build(fragments(empty - 1)).unwrap_err().to_string();
    assert!(
        error.contains("max_pending_reliable_packets_per_session"),
        "{error}"
    );
    Ok(())
}

#[test]
fn session_cap_and_expiry_use_monotonic_time() -> Result<()> {
    let clock = Clock::new(60_000);
    let options = CultMeshRudpDocumentServerOptions {
        max_sessions: 1,
        session_idle_timeout: Duration::from_secs(1),
        session_max_lifetime: Duration::from_millis(100),
        ..Default::default()
    };
    let mut server = server(options, clock.clone(), Sink::default(), Source::default())?;
    let target = server.local_addr()?;
    let mut admitted = client(target, 1)?;
    connect(&mut server, &mut [&mut admitted])?;
    let mut rejected = client(target, 1)?;
    rejected.connect(Vec::new())?;
    server.poll_once()?;
    assert_eq!(server.session_count(), 1);
    assert!(!rejected.connected());

    clock.set_unix(1);
    clock.set_monotonic(60_101);
    assert_eq!(server.maintain()?.sessions_expired, 1);
    assert_eq!(server.session_count(), 0);
    Ok(())
}

#[test]
fn a_connect_storm_and_stray_frames_are_dropped_not_fatal() -> Result<()> {
    use cultnet_rs::{
        CultNetRudpSendOptions, CultNetRudpSession, CultNetRudpSessionOptions, encode_rudp_packet,
    };
    let clock = Clock::new(60_000);
    let sink = Sink::default();
    let options = CultMeshRudpDocumentServerOptions {
        max_pending_reliable_packets_per_session: 4,
        ..Default::default()
    };
    let mut server = server(options, clock.clone(), sink.clone(), Source::default())?;
    let target = server.local_addr()?;
    let mut real = client(target, 101)?;
    connect(&mut server, &mut [&mut real])?;
    assert_eq!(server.packets_dropped(), 0);

    let raw = UdpSocket::bind("127.0.0.1:0")?;
    let session = |id| {
        CultNetRudpSession::new(CultNetRudpSessionOptions {
            connection_id: id,
            initial_sequence: Some(1),
            resend_delay_ms: 10,
            max_pending_reliable_packets: None,
        })
    };
    let send_raw = |server: &mut Server, packet: &cultnet_rs::CultNetRudpPacket| -> Result<()> {
        raw.send_to(&encode_rudp_packet(packet)?, target)?;
        server.poll_once()?;
        Ok(())
    };

    // A malformed frame.
    raw.send_to(b"not a rudp packet", target)?;
    server.poll_once()?;
    assert_eq!(server.packets_dropped(), 1);

    // Data for a session no Connect admitted.
    let mut unadmitted = session(300);
    unadmitted.assume_connected(0);
    let reliable = CultNetRudpSendOptions {
        reliable: true,
        ..Default::default()
    };
    send_raw(
        &mut server,
        &unadmitted.send("schema", vec![1], reliable.clone())?,
    )?;
    assert_eq!(server.packets_dropped(), 2);

    // A moved flow re-sends Connect from a socket that never hears the Accept.
    // Each repeat re-sends the Accept still awaiting acknowledgement and queues
    // nothing, so the storm neither errors nor grows the session's queue.
    let mut stormer = session(102);
    let connect_packet = stormer.create_connect(0, Vec::new())?;
    for _ in 0..200 {
        send_raw(&mut server, &connect_packet)?;
    }
    assert_eq!(server.packets_dropped(), 2);
    assert_eq!(server.session_count(), 2);
    clock.set(60_000 + 1_000);
    assert_eq!(
        server.maintain()?.packets_resent,
        1,
        "one Accept awaits acknowledgement, however many Connects repeated"
    );

    // A packet its own admitted session refuses ends that session, not the loop.
    stormer.assume_connected(0);
    let mut poison = stormer.send("schema", vec![1], reliable)?;
    poison.fragment_count = 2;
    poison.fragment_id = 0;
    send_raw(&mut server, &poison)?;
    assert_eq!(server.packets_dropped(), 3);
    assert_eq!(server.session_count(), 1);
    // ...and the refused client is told, after the Accepts its storm drew.
    raw.set_read_timeout(Some(Duration::from_millis(200)))?;
    let mut buffer = vec![0_u8; 65_535];
    let mut told = false;
    while let Ok(received) = raw.recv(&mut buffer) {
        told |= cultnet_rs::decode_rudp_packet(&buffer[..received])?.packet_type
            == cultnet_rs::CultNetRudpPacketType::Disconnect;
    }
    assert!(told, "the refused client must be sent a goodbye");

    send(
        &mut real,
        &CultNetMessage::DocumentPutRaw {
            message_id: "after-storm".into(),
            document: document("real", vec![9]),
        },
    )?;
    for _ in 0..20 {
        server.poll_once()?;
        if sink.0.lock().unwrap().receipts.len() == 1 {
            break;
        }
    }
    assert_eq!(sink.0.lock().unwrap().receipts.len(), 1);
    Ok(())
}
