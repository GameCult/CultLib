use anyhow::Result;
use cultmesh_rs::{
    CultMeshRudpApplicationOperation, CultMeshRudpDocumentServer,
    CultMeshRudpDocumentServerOptions, CultMeshRudpPollOutcome, CultMeshRudpPutReply,
    CultMeshRudpRawDocumentReceipt, CultMeshRudpRawDocumentSink, CultMeshRudpRejectionReason,
    CultMeshRudpServerClock, CultMeshRudpSnapshotQuery, CultMeshRudpSnapshotSource,
    UNANSWERED_PUT_REASON,
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
    fn accept_raw_document(
        &mut self,
        receipt: CultMeshRudpRawDocumentReceipt,
        reply: CultMeshRudpPutReply,
    ) {
        let mut state = self.0.lock().unwrap();
        if state.failures_remaining > 0 {
            state.failures_remaining -= 1;
            reply.refuse("injected sink failure CANARY-7f3a");
            return;
        }
        state.receipts.push(receipt);
        reply.accept();
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
            anyhow::bail!("injected source failure CANARY-7f3a");
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
        Ok(self
            .inner
            .raw_snapshot(query)?
            .iter()
            .map(stripped)
            .collect())
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

fn connect<S: CultMeshRudpRawDocumentSink, Q: CultMeshRudpSnapshotSource>(
    server: &mut CultMeshRudpDocumentServer<S, Q, Clock>,
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

/// What a refused peer is sent: a `cultnet.error.v0` carrying fixed text, never
/// a sink's or source's error (the fixtures' errors carry a canary), then the goodbye. The
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
    assert!(
        !error.contains("CANARY"),
        "a sink's or source's text reached the peer: {error}"
    );
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
fn application_rejection_is_nonfatal_peer_scoped_refused_to_the_peer_and_unacknowledged()
-> Result<()> {
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
        CultMeshRudpRejectionReason::SinkRefused("injected sink failure CANARY-7f3a".into())
    );
    assert_eq!(server.session_count(), 2);
    // The caller keeps the sink's text; the peer is sent fixed text, never it.
    assert_eq!(
        refusal_seen_by(&mut publisher, &publish_receipt)?,
        "the catalog refused the document"
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
        CultMeshRudpRejectionReason::SnapshotSourceFailed(
            "injected source failure CANARY-7f3a".into()
        )
    );
    assert_eq!(server.session_count(), 1);
    assert_eq!(
        refusal_seen_by(&mut snapshot_client, &snapshot_receipt)?,
        "the snapshot source failed"
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
        "the snapshot response is too large"
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
    assert_eq!(
        refusal_seen_by(&mut refused, &refused_receipt)?,
        "the document can never be served"
    );

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
        server(
            options,
            Clock::new(63_000),
            Sink::default(),
            Source::default(),
        )
        .map(|_| ())
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

/// A peer driven packet by packet, so a test decides what it receives and what
/// it acknowledges.
struct RawPeer {
    socket: UdpSocket,
    target: SocketAddr,
    session: cultnet_rs::CultNetRudpSession,
    /// The Connect that started the session, for a test to repeat.
    connect: cultnet_rs::CultNetRudpPacket,
}

impl RawPeer {
    fn connect<S: CultMeshRudpRawDocumentSink, Q: CultMeshRudpSnapshotSource>(
        server: &mut CultMeshRudpDocumentServer<S, Q, Clock>,
        connection_id: u32,
    ) -> Result<Self> {
        let socket = UdpSocket::bind("127.0.0.1:0")?;
        socket.set_read_timeout(Some(Duration::from_millis(50)))?;
        let target = server.local_addr()?;
        let (session, connect) = Self::handshake(&socket, target, server, connection_id, 100)?;
        Ok(Self {
            socket,
            target,
            session,
            connect,
        })
    }

    /// Connects again from the same address and connection id, starting a
    /// new generation of the session.
    fn reconnect<S: CultMeshRudpRawDocumentSink, Q: CultMeshRudpSnapshotSource>(
        &mut self,
        server: &mut CultMeshRudpDocumentServer<S, Q, Clock>,
    ) -> Result<()> {
        let connection_id = self.session.connection_id();
        (self.session, self.connect) =
            Self::handshake(&self.socket, self.target, server, connection_id, 5_000)?;
        Ok(())
    }

    fn handshake<S: CultMeshRudpRawDocumentSink, Q: CultMeshRudpSnapshotSource>(
        socket: &UdpSocket,
        target: SocketAddr,
        server: &mut CultMeshRudpDocumentServer<S, Q, Clock>,
        connection_id: u32,
        initial_sequence: u32,
    ) -> Result<(
        cultnet_rs::CultNetRudpSession,
        cultnet_rs::CultNetRudpPacket,
    )> {
        let mut session =
            cultnet_rs::CultNetRudpSession::new(cultnet_rs::CultNetRudpSessionOptions {
                connection_id,
                initial_sequence: Some(initial_sequence),
                resend_delay_ms: 10_000,
                max_pending_reliable_packets: Some(64),
            });
        let connect = session.create_connect(0, Vec::new())?;
        socket.send_to(&cultnet_rs::encode_rudp_packet(&connect)?, target)?;
        server.poll_once()?;
        let mut wire = vec![0_u8; 65_535];
        let (bytes, _) = socket.recv_from(&mut wire)?;
        let accept = cultnet_rs::decode_rudp_packet(&wire[..bytes])?;
        assert_eq!(
            accept.packet_type,
            cultnet_rs::CultNetRudpPacketType::Accept,
            "the server accepts the connect"
        );
        session.receive(&accept, 1)?;
        Ok((session, connect))
    }

    fn send_packet(&self, packet: &cultnet_rs::CultNetRudpPacket) -> Result<()> {
        self.socket
            .send_to(&cultnet_rs::encode_rudp_packet(packet)?, self.target)?;
        Ok(())
    }

    /// Sends `message` reliably and in order; returns its sequences.
    fn send(&mut self, message: &CultNetMessage) -> Result<Vec<u32>> {
        self.send_with(message, true)
    }

    fn send_with(&mut self, message: &CultNetMessage, reliable: bool) -> Result<Vec<u32>> {
        let payload = encode_cultnet_message_to_vec(message, CultNetWireContract::CultNetSchemaV0)?;
        let options = cultnet_rs::CultNetRudpSendOptions {
            reliable,
            ordered: reliable,
            sequenced: false,
            now_ms: 2,
            reliable_expire_after_ms: None,
        };
        let packets = self
            .session
            .send_many("schema", payload, options, Some(1200))?;
        for packet in &packets {
            self.send_packet(packet)?;
        }
        Ok(packets.iter().map(|packet| packet.sequence).collect())
    }

    fn receive(&self) -> Option<cultnet_rs::CultNetRudpPacket> {
        let mut wire = vec![0_u8; 65_535];
        let (bytes, _) = self.socket.recv_from(&mut wire).ok()?;
        cultnet_rs::decode_rudp_packet(&wire[..bytes]).ok()
    }

    /// Every packet waiting, none of them processed or acknowledged.
    fn drain(&self) -> Vec<cultnet_rs::CultNetRudpPacket> {
        std::iter::from_fn(|| self.receive()).collect()
    }
}

/// Whether `packet`'s acknowledgement fields cover `sequence`.
fn acknowledges(packet: &cultnet_rs::CultNetRudpPacket, sequence: u32) -> bool {
    packet.ack == sequence
        || (0..32).any(|bit| {
            packet.ack_mask & (1 << bit) != 0
                && packet.ack > bit
                && packet.ack - bit - 1 == sequence
        })
}

/// The refusal is unreliable and unordered: a peer missing an earlier reliable
/// packet from the server still receives it. An ordered refusal would wait
/// behind the gap for a resend that never comes, because the session ends with it.
#[test]
fn a_refusal_reaches_a_peer_missing_an_earlier_packet() -> Result<()> {
    let mut server = server(
        Default::default(),
        Clock::new(71_000),
        Sink::fail_once(),
        Source::documents(vec![document("held", vec![1, 2, 3])]),
    )?;
    let mut peer = RawPeer::connect(&mut server, 78)?;

    // The snapshot reply is lost: the peer never sees it, so its ordered
    // stream from the server has a gap.
    peer.send(&CultNetMessage::SnapshotRequest {
        message_id: "lost".into(),
        schema_ids: None,
        record_keys: None,
    })?;
    assert_eq!(server.poll_once()?, CultMeshRudpPollOutcome::Handled);
    assert!(
        !peer.drain().is_empty(),
        "fixture: the server sent the reply that is lost"
    );

    peer.send(&CultNetMessage::DocumentPutRaw {
        message_id: "refused".into(),
        document: document("refused", vec![4, 5, 6]),
    })?;
    assert!(matches!(
        server.poll_once()?,
        CultMeshRudpPollOutcome::ApplicationRejected(_)
    ));
    let mut delivered = Vec::new();
    let mut disconnected = false;
    for packet in peer.drain() {
        let result = peer.session.receive(&packet, 3)?;
        delivered.extend(result.delivered);
        disconnected |= result.disconnected;
    }
    let refusals: Vec<_> = delivered
        .iter()
        .map(|frame| {
            decode_cultnet_message_from_slice(&frame.payload, CultNetWireContract::CultNetSchemaV0)
        })
        .collect::<Result<_>>()?;
    assert_eq!(
        refusals,
        vec![CultNetMessage::Error {
            error: "the catalog refused the document".into(),
            code: None,
            details: None,
        }]
    );
    assert!(disconnected, "the refusal is followed by a goodbye");
    Ok(())
}

/// The peer's reliable queue is full to the brim: an 8-fragment reply the peer
/// never acknowledges fills a queue of 8 exactly. The next reply cannot be
/// queued, and its refusal still reaches the peer, because it takes no room in
/// that queue; it acknowledges nothing the peer sent.
#[test]
fn a_refusal_reaches_a_peer_whose_queue_is_full_to_the_brim() -> Result<()> {
    let options = CultMeshRudpDocumentServerOptions {
        max_fragment_bytes: 100,
        max_pending_reliable_packets_per_session: 8,
        max_snapshot_response_bytes: 8192,
        ..Default::default()
    };
    let source = Source::documents(vec![document_served_at("q1", 800)?]);
    let mut server = server(options, Clock::new(74_000), Sink::default(), source)?;
    let mut peer = RawPeer::connect(&mut server, 79)?;
    peer.send(&CultNetMessage::SnapshotRequest {
        message_id: "a".into(),
        schema_ids: None,
        record_keys: None,
    })?;
    assert_eq!(server.poll_once()?, CultMeshRudpPollOutcome::Handled);
    let unacknowledged = peer
        .drain()
        .into_iter()
        .filter(|packet| packet.packet_type == cultnet_rs::CultNetRudpPacketType::Data)
        .count();
    assert_eq!(
        unacknowledged, 8,
        "fixture: the first reply fills the queue exactly"
    );

    let refused = peer.send(&CultNetMessage::SnapshotRequest {
        message_id: "b".into(),
        schema_ids: None,
        record_keys: None,
    })?;
    let CultMeshRudpPollOutcome::ApplicationRejected(rejection) = server.poll_once()? else {
        panic!("the second reply cannot be queued");
    };
    assert!(
        matches!(
            rejection.reason,
            CultMeshRudpRejectionReason::ResponseQueueFailed(_)
        ),
        "{:?}",
        rejection.reason
    );
    let arrived = peer.drain();
    let mut parts: Vec<_> = arrived
        .iter()
        .filter(|packet| {
            packet.packet_type == cultnet_rs::CultNetRudpPacketType::Data
                && packet.channel_id == "schema"
                && !packet.reliable
        })
        .collect();
    assert!(!parts.is_empty(), "the refusal reaches the peer");
    for part in &parts {
        assert!(
            refused
                .iter()
                .all(|sequence| !acknowledges(part, *sequence)),
            "the refusal acknowledges the refused request: ack {} mask {:#x}",
            part.ack,
            part.ack_mask
        );
    }
    parts.sort_by_key(|part| part.fragment_index);
    let payload: Vec<u8> = parts.iter().flat_map(|part| part.payload.clone()).collect();
    assert_eq!(
        decode_cultnet_message_from_slice(&payload, CultNetWireContract::CultNetSchemaV0)?,
        CultNetMessage::Error {
            error: "the snapshot response could not be queued".into(),
            code: None,
            details: None,
        }
    );
    assert!(
        arrived
            .iter()
            .any(|packet| packet.packet_type == cultnet_rs::CultNetRudpPacketType::Disconnect),
        "the refusal is followed by a goodbye"
    );
    Ok(())
}

/// A snapshot the reliable queue cannot take is answered with a refusal, not
/// left to time out: the refusal needs no room in that queue.
#[test]
fn a_snapshot_the_queue_cannot_take_is_answered() -> Result<()> {
    let options = CultMeshRudpDocumentServerOptions {
        max_fragment_bytes: 100,
        max_pending_reliable_packets_per_session: 8,
        max_snapshot_response_bytes: 8192,
        ..Default::default()
    };
    // Each document alone is served in 800 bytes, eight fragments; both need sixteen.
    let source = Source::documents(vec![
        document_served_at("q1", 800)?,
        document_served_at("q2", 800)?,
    ]);
    let mut server = server(options, Clock::new(72_000), Sink::default(), source)?;
    let mut client = client(server.local_addr()?, 151)?;
    connect(&mut server, &mut [&mut client])?;
    let receipt = send_reliable(
        &mut client,
        &CultNetMessage::SnapshotRequest {
            message_id: "both".into(),
            schema_ids: None,
            record_keys: None,
        },
    )?;
    let mut rejection = None;
    for _ in 0..100 {
        if let CultMeshRudpPollOutcome::ApplicationRejected(found) = server.poll_once()? {
            rejection = Some(found);
            break;
        }
        client.receive_once()?;
    }
    let rejection = rejection.expect("the response cannot be queued");
    assert!(
        matches!(
            rejection.reason,
            CultMeshRudpRejectionReason::ResponseQueueFailed(_)
        ),
        "{:?}",
        rejection.reason
    );
    assert_eq!(
        refusal_seen_by(&mut client, &receipt)?,
        "the snapshot response could not be queued"
    );
    Ok(())
}

/// A response too large for any datagram fails with the kernel's EMSGSIZE. The
/// caller's rejection and the peer's goodbye both carry its fixed name, never
/// the platform's text, and the peer is sent no refusal.
#[test]
fn a_response_that_is_too_large_to_send_is_named_emsgsize() -> Result<()> {
    let options = CultMeshRudpDocumentServerOptions {
        max_fragment_bytes: 70_000,
        ..Default::default()
    };
    let source = Source::documents(vec![document("big", vec![7; 66_000])]);
    let mut server = server(options, Clock::new(73_000), Sink::default(), source)?;
    let mut client = client(server.local_addr()?, 152)?;
    connect(&mut server, &mut [&mut client])?;
    send_reliable(
        &mut client,
        &CultNetMessage::SnapshotRequest {
            message_id: "big".into(),
            schema_ids: None,
            record_keys: None,
        },
    )?;
    let mut rejection = None;
    for _ in 0..100 {
        if let CultMeshRudpPollOutcome::ApplicationRejected(found) = server.poll_once()? {
            rejection = Some(found);
            break;
        }
    }
    assert_eq!(
        rejection.expect("the response can never be sent").reason,
        CultMeshRudpRejectionReason::ResponseSendFailed("EMSGSIZE")
    );
    let mut frames = 0;
    for _ in 0..500 {
        if client.receive_once()?.is_some() {
            frames += 1;
        }
        if !client.connected() {
            break;
        }
        thread::sleep(Duration::from_millis(1));
    }
    assert_eq!(frames, 0, "no refusal is sent to an unreachable peer");
    assert_eq!(
        client.disconnect_reason(),
        Some(&b"packet could not be sent: EMSGSIZE"[..])
    );
    Ok(())
}

/// Every datagram waiting at `peer`, encoded as hex, one per line. A reliable
/// packet's own sequence is drawn at random per session, so it is written as
/// zero; everything else, the acknowledgement fields above all, is kept.
fn datagrams_hex(peer: &RawPeer) -> Result<String> {
    let mut lines = String::new();
    for mut packet in peer.drain() {
        if packet.reliable {
            packet.sequence = 0;
        }
        for byte in cultnet_rs::encode_rudp_packet(&packet)? {
            lines.push_str(&format!("{byte:02x}"));
        }
        lines.push('\n');
    }
    Ok(lines)
}

/// A closure sink answers at once, and the server sends exactly the datagrams it
/// sent before a sink could answer later: a Pong, the put's acknowledgement,
/// a snapshot response and its acknowledgement, and for a refused put the
/// refusal and the goodbye. The bytes were recorded from the server before
/// the reply handle existed.
#[test]
fn a_closure_sink_sends_the_same_datagrams_as_before_replies_could_wait() -> Result<()> {
    let accepting = |_: CultMeshRudpRawDocumentReceipt| -> Result<()> { Ok(()) };
    let mut server = CultMeshRudpDocumentServer::new(
        UdpSocket::bind("127.0.0.1:0")?,
        accepting,
        Source::documents(vec![document("held", vec![1, 2, 3])]),
        Clock::new(75_000),
        Default::default(),
    )?;
    let mut peer = RawPeer::connect(&mut server, 80)?;
    let mut seen = String::new();
    let ping = peer.session.create_ping(b"hi".to_vec());
    peer.send_packet(&ping)?;
    server.poll_once()?;
    seen += &datagrams_hex(&peer)?;
    peer.send(&CultNetMessage::DocumentPutRaw {
        message_id: "put".into(),
        document: document("put", vec![4, 5, 6]),
    })?;
    assert_eq!(server.poll_once()?, CultMeshRudpPollOutcome::Handled);
    seen += &datagrams_hex(&peer)?;
    peer.send(&CultNetMessage::SnapshotRequest {
        message_id: "read".into(),
        schema_ids: None,
        record_keys: None,
    })?;
    assert_eq!(server.poll_once()?, CultMeshRudpPollOutcome::Handled);
    seen += &datagrams_hex(&peer)?;

    let refusing = |_: CultMeshRudpRawDocumentReceipt| -> Result<()> {
        anyhow::bail!("injected sink failure CANARY-7f3a")
    };
    let mut server = CultMeshRudpDocumentServer::new(
        UdpSocket::bind("127.0.0.1:0")?,
        refusing,
        Source::default(),
        Clock::new(76_000),
        Default::default(),
    )?;
    let mut peer = RawPeer::connect(&mut server, 81)?;
    peer.send(&CultNetMessage::DocumentPutRaw {
        message_id: "put".into(),
        document: document("put", vec![4, 5, 6]),
    })?;
    let CultMeshRudpPollOutcome::ApplicationRejected(rejection) = server.poll_once()? else {
        panic!("the closure refuses the put");
    };
    assert_eq!(
        rejection.reason,
        CultMeshRudpRejectionReason::SinkRefused("injected sink failure CANARY-7f3a".into())
    );
    seen += &datagrams_hex(&peer)?;

    assert_eq!(
        seen,
        concat!(
            // Pong
            "434e52300006002b00000050000000000000006400000000000000000000000000020700636f6e74726f6c6869
",
            // the put's acknowledgement
            "434e52300004002b00000050000000000000006500000001000000000000000000000700636f6e74726f6c
",
            // the snapshot response
            "434e52300003032a00000050000000000000006600000003000000000000000000f50600736368656d6183ad",
            "736368656d6156657273696f6ed92063756c746e65742e736e617073686f745f726573706f6e73655f7261",
            "772e7630a96d6573736167654964a472656164a9646f63756d656e74739189a8736368656d614964ab7465",
            "73742e7261772e7631a97265636f72644b6579a468656c64a873746f7265644174b4323032362d30392d30",
            "335430303a30303a30305aaf7061796c6f6164456e636f64696e67ab6d6573736167657061636ba77061796c",
            "6f6164c403010203af736f7572636552756e74696d654964ac72756e74696d652d68656c64ad736f757263",
            "654167656e744964c0aa736f75726365526f6c65c0a474616773c0
",
            // the snapshot request's acknowledgement
            "434e52300004002b00000050000000000000006600000003000000000000000000000700636f6e74726f6c
",
            // the refusal, acknowledging nothing
            "434e52300003002a00000051000000000000000000000000000000000000000000640600736368656d6185ad",
            "736368656d6156657273696f6eb063756c746e65742e6572726f722e7630a56572726f72d9207468652063",
            "6174616c6f6720726566757365642074686520646f63756d656e74ab726f7574696e6748696e74c0a4636f",
            "6465c0a764657461696c73c0
",
            // the goodbye
            "434e52300007002b00000051000000000000000000000000000000000000000000180700636f6e74726f6c",
            "73657373696f6e20726566757365642061207061636b6574
",
        )
    );
    Ok(())
}

/// A sink that holds every put's reply for the test to answer.
#[derive(Clone, Default)]
struct HeldSink(Arc<Mutex<HeldState>>);

#[derive(Default)]
struct HeldState {
    offered: Vec<String>,
    replies: Vec<(String, CultMeshRudpPutReply)>,
}

impl CultMeshRudpRawDocumentSink for HeldSink {
    fn accept_raw_document(
        &mut self,
        receipt: CultMeshRudpRawDocumentReceipt,
        reply: CultMeshRudpPutReply,
    ) {
        let mut state = self.0.lock().unwrap();
        state.offered.push(receipt.message_id.clone());
        state.replies.push((receipt.message_id, reply));
    }
}

impl HeldSink {
    /// The message ids of the puts offered so far, in order.
    fn offered(&self) -> Vec<String> {
        self.0.lock().unwrap().offered.clone()
    }

    /// The held reply for the put `message_id`.
    fn reply(&self, message_id: &str) -> CultMeshRudpPutReply {
        let mut state = self.0.lock().unwrap();
        let index = state
            .replies
            .iter()
            .position(|(id, _)| id == message_id)
            .expect("the put was offered and is unanswered");
        state.replies.remove(index).1
    }
}

type HeldServer = CultMeshRudpDocumentServer<HeldSink, Source, Clock>;

fn held_server(sink: &HeldSink, clock: &Clock) -> Result<HeldServer> {
    CultMeshRudpDocumentServer::new(
        UdpSocket::bind("127.0.0.1:0")?,
        sink.clone(),
        Source::documents(vec![document("held", vec![1, 2, 3])]),
        clock.clone(),
        Default::default(),
    )
}

fn put(message_id: &str) -> CultNetMessage {
    CultNetMessage::DocumentPutRaw {
        message_id: message_id.into(),
        document: document(message_id, vec![4, 5, 6]),
    }
}

fn snapshot_request(message_id: &str) -> CultNetMessage {
    CultNetMessage::SnapshotRequest {
        message_id: message_id.into(),
        schema_ids: None,
        record_keys: None,
    }
}

/// Polls until the server has nothing left to read.
fn poll_until_idle<S: CultMeshRudpRawDocumentSink>(
    server: &mut CultMeshRudpDocumentServer<S, Source, Clock>,
) -> Result<()> {
    for _ in 0..100 {
        match server.poll_once()? {
            CultMeshRudpPollOutcome::Idle => return Ok(()),
            CultMeshRudpPollOutcome::Handled => {}
            rejected => panic!("polling must not reject: {rejected:?}"),
        }
    }
    anyhow::bail!("the server never went idle")
}

/// The CultNet message a single-packet schema frame carries.
fn schema_message(packet: &cultnet_rs::CultNetRudpPacket) -> Result<CultNetMessage> {
    assert_eq!(packet.fragment_count, 0, "fixture: one packet per message");
    decode_cultnet_message_from_slice(&packet.payload, CultNetWireContract::CultNetSchemaV0)
}

fn assert_acknowledges_none(packets: &[cultnet_rs::CultNetRudpPacket], sequences: &[u32]) {
    for packet in packets {
        assert!(
            sequences
                .iter()
                .all(|sequence| !acknowledges(packet, *sequence)),
            "{:?} acknowledges an unanswered put: ack {} mask {:#x}",
            packet.packet_type,
            packet.ack,
            packet.ack_mask
        );
    }
}

/// While a put awaits its answer, its session is sent nothing new: no
/// acknowledgement, no Pong, no snapshot response, across five resend
/// intervals. A response built before the put still resends, carrying the
/// acknowledgement fields it was built with. Once the put is accepted, the
/// next poll acknowledges it, then answers the snapshot request that waited.
#[test]
fn a_held_put_is_acknowledged_only_after_it_is_accepted() -> Result<()> {
    let clock = Clock::new(80_000);
    let sink = HeldSink::default();
    let mut server = held_server(&sink, &clock)?;
    let mut peer = RawPeer::connect(&mut server, 90)?;

    // A response the peer never acknowledges, built before the put.
    peer.send(&snapshot_request("before"))?;
    assert_eq!(server.poll_once()?, CultMeshRudpPollOutcome::Handled);
    let earlier: Vec<u32> = peer
        .drain()
        .iter()
        .filter(|packet| packet.packet_type == cultnet_rs::CultNetRudpPacketType::Data)
        .map(|packet| packet.sequence)
        .collect();
    assert_eq!(
        earlier.len(),
        1,
        "fixture: the earlier response is one packet"
    );

    let held = peer.send(&put("held"))?;
    poll_until_idle(&mut server)?;
    assert_eq!(sink.offered(), vec!["held".to_string()]);
    let mut seen = peer.drain();
    for step in 1..=5 {
        clock.set(80_000 + 50 * step);
        let ping = peer.session.create_ping(b"alive".to_vec());
        peer.send_packet(&ping)?;
        if step == 3 {
            peer.send(&snapshot_request("after"))?;
        }
        poll_until_idle(&mut server)?;
        seen.extend(peer.drain());
    }
    assert_acknowledges_none(&seen, &held);
    let kinds: Vec<_> = seen
        .iter()
        .map(|packet| (packet.packet_type, packet.sequence))
        .collect();
    assert_eq!(
        kinds,
        vec![(cultnet_rs::CultNetRudpPacketType::Data, earlier[0]); 5],
        "only the earlier response's resends reach the peer"
    );

    sink.reply("held").accept();
    assert_eq!(server.poll_once()?, CultMeshRudpPollOutcome::Idle);
    let released = peer.drain();
    assert_eq!(released.len(), 2, "{released:?}");
    assert_eq!(
        released[0].packet_type,
        cultnet_rs::CultNetRudpPacketType::Ack
    );
    assert!(
        held.iter()
            .all(|sequence| acknowledges(&released[0], *sequence))
    );
    let CultNetMessage::SnapshotResponseRaw { message_id, .. } = schema_message(&released[1])?
    else {
        panic!(
            "the waiting snapshot request is answered: {:?}",
            released[1]
        );
    };
    assert_eq!(message_id, "after");
    Ok(())
}

/// A put awaiting its answer holds only its own session: another session's
/// snapshot request is answered in the same polls.
#[test]
fn another_session_is_served_while_a_put_is_held() -> Result<()> {
    let clock = Clock::new(81_000);
    let sink = HeldSink::default();
    let mut server = held_server(&sink, &clock)?;
    let mut holding = RawPeer::connect(&mut server, 91)?;
    let mut reading = RawPeer::connect(&mut server, 92)?;
    let held = holding.send(&put("held"))?;
    poll_until_idle(&mut server)?;
    let read = reading.send(&snapshot_request("read"))?;
    poll_until_idle(&mut server)?;

    let answered = reading.drain();
    let response = answered
        .iter()
        .find(|packet| packet.packet_type == cultnet_rs::CultNetRudpPacketType::Data)
        .expect("the other session is answered");
    assert!(matches!(
        schema_message(response)?,
        CultNetMessage::SnapshotResponseRaw { .. }
    ));
    assert!(acknowledges(response, read[0]));
    assert_acknowledges_none(&holding.drain(), &held);
    Ok(())
}

/// A refused reply refuses the put: the poll returns the sink's reason, and
/// the publisher gets the fixed refusal and a goodbye, and never an
/// acknowledgement.
#[test]
fn a_refused_reply_refuses_the_put_and_acknowledges_nothing() -> Result<()> {
    let clock = Clock::new(82_000);
    let sink = HeldSink::default();
    let mut server = held_server(&sink, &clock)?;
    let mut publisher = client(server.local_addr()?, 93)?;
    connect(&mut server, &mut [&mut publisher])?;
    let receipt = send_reliable(&mut publisher, &put("refused"))?;
    for _ in 0..200 {
        publisher.receive_once()?;
        publisher.poll_resends()?;
        server.poll_once()?;
        thread::sleep(Duration::from_millis(1));
    }
    assert_eq!(sink.offered(), vec!["refused".to_string()]);
    assert_eq!(
        publisher.reliable_send_status(&receipt),
        CultNetRudpReliableSendStatus::Pending,
        "a held put is not acknowledged, however often it is resent"
    );

    sink.reply("refused")
        .refuse("the store said no CANARY-7f3a");
    let CultMeshRudpPollOutcome::ApplicationRejected(rejection) = server.poll_once()? else {
        panic!("the refusal is the next poll's outcome");
    };
    assert_eq!(
        (rejection.operation, rejection.message_id, rejection.reason),
        (
            CultMeshRudpApplicationOperation::DocumentPutRaw,
            "refused".to_string(),
            CultMeshRudpRejectionReason::SinkRefused("the store said no CANARY-7f3a".into())
        )
    );
    assert_eq!(
        refusal_seen_by(&mut publisher, &receipt)?,
        "the catalog refused the document"
    );
    assert_eq!(server.session_count(), 0);
    Ok(())
}

/// A reply dropped unanswered refuses its put, over a reason of its own.
#[test]
fn a_dropped_reply_refuses_the_put() -> Result<()> {
    let clock = Clock::new(83_000);
    let sink = HeldSink::default();
    let mut server = held_server(&sink, &clock)?;
    let mut peer = RawPeer::connect(&mut server, 94)?;
    let held = peer.send(&put("dropped"))?;
    poll_until_idle(&mut server)?;

    drop(sink.reply("dropped"));
    let CultMeshRudpPollOutcome::ApplicationRejected(rejection) = server.poll_once()? else {
        panic!("a dropped reply refuses the put");
    };
    assert_eq!(
        rejection.reason,
        CultMeshRudpRejectionReason::SinkRefused(UNANSWERED_PUT_REASON.into())
    );
    assert_eq!(UNANSWERED_PUT_REASON, "the put was not answered");
    let sent = peer.drain();
    assert_acknowledges_none(&sent, &held);
    let kinds: Vec<_> = sent.iter().map(|packet| packet.packet_type).collect();
    assert_eq!(
        kinds,
        vec![
            cultnet_rs::CultNetRudpPacketType::Data,
            cultnet_rs::CultNetRudpPacketType::Disconnect
        ]
    );
    assert_eq!(
        schema_message(&sent[0])?,
        CultNetMessage::Error {
            error: "the catalog refused the document".into(),
            code: None,
            details: None,
        }
    );
    Ok(())
}

/// Two pipelined puts are both offered at once, so one write can cover them.
/// Answering the later first releases nothing; answering the earlier then
/// releases one acknowledgement covering both. A later put's acknowledgement
/// never leaves before an earlier one's.
#[test]
fn a_later_puts_answer_never_leaves_before_an_earlier_ones() -> Result<()> {
    let clock = Clock::new(84_000);
    let sink = HeldSink::default();
    let mut server = held_server(&sink, &clock)?;
    let mut peer = RawPeer::connect(&mut server, 95)?;
    let first = peer.send(&put("first"))?;
    let second = peer.send(&put("second"))?;
    poll_until_idle(&mut server)?;
    assert_eq!(sink.offered(), vec!["first".to_string(), "second".into()]);

    sink.reply("second").accept();
    assert_eq!(server.poll_once()?, CultMeshRudpPollOutcome::Idle);
    assert_eq!(
        peer.drain(),
        Vec::new(),
        "the later answer releases nothing"
    );

    sink.reply("first").accept();
    assert_eq!(server.poll_once()?, CultMeshRudpPollOutcome::Idle);
    let released = peer.drain();
    assert_eq!(released.len(), 1, "{released:?}");
    assert_eq!(
        released[0].packet_type,
        cultnet_rs::CultNetRudpPacketType::Ack
    );
    for sequence in first.iter().chain(&second) {
        assert!(acknowledges(&released[0], *sequence));
    }
    Ok(())
}

/// A put behind a snapshot request that waits on an earlier put is not
/// offered until that request is answered, and the answer is not sent
/// while the put is unanswered: it would acknowledge the put.
#[test]
fn a_put_behind_a_waiting_snapshot_request_is_acknowledged_only_once_accepted() -> Result<()> {
    let clock = Clock::new(85_000);
    let sink = HeldSink::default();
    let mut server = held_server(&sink, &clock)?;
    let mut peer = RawPeer::connect(&mut server, 96)?;
    let first = peer.send(&put("first"))?;
    peer.send(&snapshot_request("between"))?;
    let second = peer.send(&put("second"))?;
    poll_until_idle(&mut server)?;
    assert_eq!(
        sink.offered(),
        vec!["first".to_string()],
        "the second put waits behind the snapshot request"
    );

    sink.reply("first").accept();
    assert_eq!(server.poll_once()?, CultMeshRudpPollOutcome::Idle);
    assert_eq!(sink.offered(), vec!["first".to_string(), "second".into()]);
    assert_eq!(
        peer.drain(),
        Vec::new(),
        "nothing is sent while the second put is unanswered"
    );

    sink.reply("second").accept();
    assert_eq!(server.poll_once()?, CultMeshRudpPollOutcome::Idle);
    let released = peer.drain();
    assert_eq!(released.len(), 2, "{released:?}");
    assert_eq!(
        released[0].packet_type,
        cultnet_rs::CultNetRudpPacketType::Ack
    );
    for sequence in first.iter().chain(&second) {
        assert!(acknowledges(&released[0], *sequence));
    }
    let CultNetMessage::SnapshotResponseRaw { message_id, .. } = schema_message(&released[1])?
    else {
        panic!("the snapshot request is answered: {:?}", released[1]);
    };
    assert_eq!(message_id, "between");
    Ok(())
}

/// A repeated Connect is answered with an acknowledgement built now, so while
/// a put awaits its answer the repeat is answered with nothing.
#[test]
fn a_repeated_connect_is_not_answered_while_a_put_is_held() -> Result<()> {
    let clock = Clock::new(86_000);
    let sink = HeldSink::default();
    let mut server = held_server(&sink, &clock)?;
    let mut peer = RawPeer::connect(&mut server, 97)?;
    let held = peer.send(&put("held"))?;
    poll_until_idle(&mut server)?;
    peer.send_packet(&peer.connect)?;
    poll_until_idle(&mut server)?;
    assert_eq!(peer.drain(), Vec::new());

    sink.reply("held").accept();
    assert_eq!(server.poll_once()?, CultMeshRudpPollOutcome::Idle);
    let released = peer.drain();
    assert_eq!(released.len(), 1, "{released:?}");
    assert!(
        held.iter()
            .all(|sequence| acknowledges(&released[0], *sequence))
    );
    Ok(())
}

/// When a session ends, the answers its puts still owe are discarded. A
/// refusal owed by an earlier generation of the same address and connection
/// id does not end the new one, and an acceptance owed by a session that
/// disconnected or expired sends nothing.
#[test]
fn a_session_that_ends_discards_the_answers_it_is_owed() -> Result<()> {
    let clock = Clock::new(87_000);
    let sink = HeldSink::default();
    let mut server = held_server(&sink, &clock)?;

    // A new generation replaces the one holding the put.
    let mut replaced = RawPeer::connect(&mut server, 98)?;
    replaced.send(&put("replaced"))?;
    poll_until_idle(&mut server)?;
    replaced.reconnect(&mut server)?;
    sink.reply("replaced").refuse("stale CANARY-7f3a");
    assert_eq!(server.poll_once()?, CultMeshRudpPollOutcome::Idle);
    assert_eq!(replaced.drain(), Vec::new());
    let ping = replaced.session.create_ping(b"new".to_vec());
    replaced.send_packet(&ping)?;
    poll_until_idle(&mut server)?;
    let answered = replaced.drain();
    assert_eq!(
        answered
            .iter()
            .map(|packet| packet.packet_type)
            .collect::<Vec<_>>(),
        vec![cultnet_rs::CultNetRudpPacketType::Pong],
        "the new generation is served and is not withheld"
    );

    // The peer says goodbye while its put is held.
    let mut departed = RawPeer::connect(&mut server, 99)?;
    departed.send(&put("departed"))?;
    poll_until_idle(&mut server)?;
    let goodbye = departed.session.create_disconnect(Vec::new());
    departed.send_packet(&goodbye)?;
    poll_until_idle(&mut server)?;

    // The session idles out while its put is held.
    let mut expired = RawPeer::connect(&mut server, 100)?;
    expired.send(&put("expired"))?;
    poll_until_idle(&mut server)?;
    clock.set(87_000 + 31_000);
    poll_until_idle(&mut server)?;
    assert_eq!(server.session_count(), 0);

    sink.reply("departed").accept();
    drop(sink.reply("expired"));
    assert_eq!(server.poll_once()?, CultMeshRudpPollOutcome::Idle);
    assert_eq!(departed.drain(), Vec::new());
    assert_eq!(expired.drain(), Vec::new());
    Ok(())
}

/// A put the server could never serve is refused before the sink sees it.
#[test]
fn an_unservable_put_is_never_offered_to_the_sink() -> Result<()> {
    let clock = Clock::new(88_000);
    let sink = HeldSink::default();
    let mut server = CultMeshRudpDocumentServer::new(
        UdpSocket::bind("127.0.0.1:0")?,
        sink.clone(),
        Source::default(),
        clock,
        CultMeshRudpDocumentServerOptions {
            max_snapshot_response_bytes: 1_000,
            ..Default::default()
        },
    )?;
    let mut peer = RawPeer::connect(&mut server, 101)?;
    peer.send(&CultNetMessage::DocumentPutRaw {
        message_id: "big".into(),
        document: document("big", vec![7; 2_000]),
    })?;
    // The put arrives in two fragments.
    let mut rejection = None;
    for _ in 0..10 {
        if let CultMeshRudpPollOutcome::ApplicationRejected(found) = server.poll_once()? {
            rejection = Some(found);
            break;
        }
    }
    let rejection = rejection.expect("the put can never be served");
    assert!(matches!(
        rejection.reason,
        CultMeshRudpRejectionReason::DocumentUnservable { .. }
    ));
    assert_eq!(sink.offered(), Vec::<String>::new());
    Ok(())
}

/// A sink that holds each put's reply until it is offered the put `last`,
/// then accepts every held reply and that one, inside the same poll.
#[derive(Default)]
struct AnswersOnLast(Vec<CultMeshRudpPutReply>);

impl CultMeshRudpRawDocumentSink for AnswersOnLast {
    fn accept_raw_document(
        &mut self,
        receipt: CultMeshRudpRawDocumentReceipt,
        reply: CultMeshRudpPutReply,
    ) {
        self.0.push(reply);
        if receipt.message_id == "last" {
            self.0.drain(..).for_each(CultMeshRudpPutReply::accept);
        }
    }
}

/// A poll whose packet releases a withheld session sends exactly one
/// acknowledgement. A reliable packet's own acknowledgement is that one; an
/// unreliable packet has none, so the release sends it.
#[test]
fn a_release_inside_a_poll_sends_exactly_one_acknowledgement() -> Result<()> {
    for reliable in [false, true] {
        let mut server = CultMeshRudpDocumentServer::new(
            UdpSocket::bind("127.0.0.1:0")?,
            AnswersOnLast::default(),
            Source::default(),
            Clock::new(89_000),
            Default::default(),
        )?;
        let mut peer = RawPeer::connect(&mut server, 102)?;
        let first = peer.send(&put("first"))?;
        poll_until_idle(&mut server)?;
        assert_eq!(peer.drain(), Vec::new(), "fixture: the first put is held");

        let last = peer.send_with(&put("last"), reliable)?;
        assert_eq!(server.poll_once()?, CultMeshRudpPollOutcome::Handled);
        let sent = peer.drain();
        assert_eq!(sent.len(), 1, "reliable {reliable}: {sent:?}");
        assert_eq!(sent[0].packet_type, cultnet_rs::CultNetRudpPacketType::Ack);
        assert!(
            first
                .iter()
                .all(|sequence| acknowledges(&sent[0], *sequence))
        );
        if reliable {
            assert!(
                last.iter()
                    .all(|sequence| acknowledges(&sent[0], *sequence))
            );
        }
    }
    Ok(())
}
