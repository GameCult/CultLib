use anyhow::Result;
use cultmesh_rs::{
    CultMeshRudpApplicationOperation, CultMeshRudpDocumentServer,
    CultMeshRudpDocumentServerOptions, CultMeshRudpPollOutcome, CultMeshRudpRawDocumentReceipt,
    CultMeshRudpRawDocumentSink, CultMeshRudpRejectionReason, CultMeshRudpServerClock,
    CultMeshRudpSnapshotQuery, CultMeshRudpSnapshotSource,
};
use cultnet_rs::{
    CultNetErrorCode, CultNetErrorDetails, CultNetMessage, CultNetRawDocumentRecord, CultNetRawPayloadEncoding,
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
    /// Record keys the sink refuses to authorize, before and at the write.
    denied_keys: Vec<String>,
    /// Every `authorize_raw_document` call, by message id.
    authorized: Vec<String>,
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

    fn denying(keys: &[&str]) -> Self {
        Self(Arc::new(Mutex::new(SinkState {
            denied_keys: keys.iter().map(|key| key.to_string()).collect(),
            ..Default::default()
        })))
    }
}

impl CultMeshRudpRawDocumentSink for Sink {
    fn authorize_raw_document(&mut self, receipt: &CultMeshRudpRawDocumentReceipt) -> Result<()> {
        self.0.lock().unwrap().authorized.push(receipt.message_id.clone());
        if self.0.lock().unwrap().denied_keys.contains(&receipt.document.record_key) {
            anyhow::bail!("not authorized");
        }
        Ok(())
    }

    fn accept_raw_document(&mut self, receipt: CultMeshRudpRawDocumentReceipt) -> Result<()> {
        let mut state = self.0.lock().unwrap();
        if state.denied_keys.contains(&receipt.document.record_key) {
            anyhow::bail!("not authorized");
        }
        if state.failures_remaining > 0 {
            state.failures_remaining -= 1;
            anyhow::bail!("injected sink failure CANARY-7f3a");
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

/// What a refused peer is sent: a `cultnet.error.v0` carrying fixed text, never
/// a sink's or source's error (the fixtures' errors carry a canary), then the goodbye. The
/// refused message is never acknowledged, so its receipt stays pending until the
/// goodbye invalidates it. Returns the refusal's text.
fn refusal_seen_by(
    client: &mut CultNetRudpSocketTransportConnection,
    receipt: &CultNetRudpReliableSendReceipt,
) -> Result<String> {
    let (error, code, details) = typed_refusal_seen_by(client, receipt, None)?;
    assert_eq!((code, details), (None, None));
    Ok(error)
}

/// `server` is polled while the goodbye is awaited when the refusal ends its session only once the peer has
/// acknowledged it (the capped frame's answer).
fn typed_refusal_seen_by(
    client: &mut CultNetRudpSocketTransportConnection,
    receipt: &CultNetRudpReliableSendReceipt,
    mut server: Option<&mut Server>,
) -> Result<(String, Option<CultNetErrorCode>, Option<CultNetErrorDetails>)> {
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
        if let Some(server) = server.as_deref_mut() {
            server.poll_once()?;
        }
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
    Ok((error, code, details))
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
    // A refused put is typed write_not_authorized and names the record; nothing of it reached the sink.
    assert_eq!(
        typed_refusal_seen_by(&mut publisher, &publish_receipt, None)?,
        (
            "write_not_authorized: the catalog refused the document".to_string(),
            Some(CultNetErrorCode::WriteNotAuthorized),
            Some(CultNetErrorDetails {
                field: Some("recordKey".into()),
                value: Some(document("rejected", vec![0, 255, 1]).record_key),
                as_of: None,
                current: None,
            })
        )
    );
    assert_eq!(sink.0.lock().unwrap().receipts.len(), 0, "a refused put changes nothing");
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

/// Sends `payload` reliably, polls until the server refuses it, and returns the typed refusal the
/// peer is sent. The session is not asserted here: the caller says whether it lives.
fn refusal_on_live_session(
    server: &mut CultMeshRudpDocumentServer<Sink, Source, Clock>,
    client: &mut CultNetRudpSocketTransportConnection,
    payload: Vec<u8>,
) -> Result<(Option<CultNetErrorCode>, Option<CultNetErrorDetails>, String)> {
    client.send_reliable("schema", payload)?;
    let mut rejected = false;
    for _ in 0..2_000 {
        if !rejected {
            rejected = matches!(server.poll_once()?, CultMeshRudpPollOutcome::ApplicationRejected(_));
        }
        if let Some(frame) = client.receive_once()? {
            if let CultNetMessage::Error { error, code, details } = decode_cultnet_message_from_slice(
                &frame.payload,
                CultNetWireContract::CultNetSchemaV0,
            )? {
                assert!(rejected, "the refusal preceded the server's own outcome");
                return Ok((code, details, error));
            }
        }
        thread::sleep(Duration::from_millis(1));
    }
    anyhow::bail!("no typed refusal reached the peer")
}

fn garbage() -> Vec<u8> {
    let mut payload = vec![0xAE];
    payload.extend_from_slice(b"CANARY-garbage");
    payload
}

fn delete_of(key: &str) -> Vec<u8> {
    encode_cultnet_message_to_vec(
        &CultNetMessage::DocumentDelete {
            message_id: "del".into(),
            schema_id: "sha256:note".into(),
            record_key: key.into(),
        },
        CultNetWireContract::CultNetSchemaV0,
    )
    .unwrap()
}

fn valid_put(key: &str) -> Vec<u8> {
    encode_cultnet_message_to_vec(
        &CultNetMessage::DocumentPutRaw {
            message_id: format!("put-{key}"),
            document: document(key, vec![1, 2, 3]),
        },
        CultNetWireContract::CultNetSchemaV0,
    )
    .unwrap()
}

fn stored_after_poll(
    server: &mut CultMeshRudpDocumentServer<Sink, Source, Clock>,
    client: &mut CultNetRudpSocketTransportConnection,
    sink: &Sink,
    receipts: usize,
) -> Result<()> {
    client.send_reliable("schema", valid_put(&format!("k{receipts}")))?;
    for _ in 0..2_000 {
        server.poll_once()?;
        client.receive_once()?;
        if sink.0.lock().unwrap().receipts.len() >= receipts {
            return Ok(());
        }
        thread::sleep(Duration::from_millis(1));
    }
    anyhow::bail!("a valid put on the kept session was not stored")
}

#[test]
fn an_unreadable_frame_is_refused_typed_and_the_session_kept_until_eight_in_a_row() -> Result<()> {
    let sink = Sink::default();
    let mut server = server(Default::default(), Clock::new(49_000), sink.clone(), Source::default())?;
    let target = server.local_addr()?;
    let mut garbler = client(target, 111)?;
    let mut survivor = client(target, 113)?;
    connect(&mut server, &mut [&mut garbler, &mut survivor])?;
    let message_invalid = (
        Some(CultNetErrorCode::MessageInvalid),
        Some(CultNetErrorDetails {
            field: Some("message".into()),
            value: None,
            as_of: None,
            current: None,
        }),
        "message_invalid: the message could not be read".to_string(),
    );

    // Seven in a row are answered and the session lives, a valid frame resets the run, and seven more
    // are answered again: a counter that never resets, or a cap below eight, ends the session early.
    for _ in 0..7 {
        assert_eq!(refusal_on_live_session(&mut server, &mut garbler, garbage())?, message_invalid);
        assert!(garbler.connected());
    }
    stored_after_poll(&mut server, &mut garbler, &sink, 1)?;
    for _ in 0..7 {
        assert_eq!(refusal_on_live_session(&mut server, &mut garbler, garbage())?, message_invalid);
        assert!(garbler.connected());
    }
    assert_eq!(server.session_count(), 2);

    // The eighth consecutive one is answered too, then the session ends with a goodbye.
    let receipt = garbler.send_reliable("schema", garbage())?;
    let CultMeshRudpPollOutcome::ApplicationRejected(rejection) = server.poll_once()? else {
        panic!("the eighth consecutive unreadable frame must be refused");
    };
    assert_eq!(rejection.operation, CultMeshRudpApplicationOperation::Unreadable);
    let (error, code, _) = typed_refusal_seen_by(&mut garbler, &receipt, Some(&mut server))?;
    assert_eq!((error.as_str(), code), (message_invalid.2.as_str(), message_invalid.0));
    drain_until_idle(&mut server)?;
    assert_eq!(server.session_count(), 1, "only the capped peer's session ended");
    assert_eq!(sink.0.lock().unwrap().receipts.len(), 1);
    Ok(())
}

#[test]
fn a_refused_delete_keeps_the_session_until_eight_in_a_row() -> Result<()> {
    let sink = Sink::default();
    let mut server = server(Default::default(), Clock::new(50_000), sink.clone(), Source::default())?;
    let target = server.local_addr()?;
    let mut deleter = client(target, 112)?;
    connect(&mut server, &mut [&mut deleter])?;
    let refused = |key: &str| {
        (
            Some(CultNetErrorCode::WriteNotAuthorized),
            Some(CultNetErrorDetails {
                field: Some("recordKey".into()),
                value: Some(key.into()),
                as_of: None,
                current: None,
            }),
            "write_not_authorized: the server does not take deletes".to_string(),
        )
    };
    for _ in 0..7 {
        assert_eq!(refusal_on_live_session(&mut server, &mut deleter, delete_of("note:victim"))?, refused("note:victim"));
        assert!(deleter.connected());
    }
    // A decoded non-delete frame resets the run.
    stored_after_poll(&mut server, &mut deleter, &sink, 1)?;
    for _ in 0..7 {
        assert_eq!(refusal_on_live_session(&mut server, &mut deleter, delete_of("note:victim"))?, refused("note:victim"));
    }
    let receipt = deleter.send_reliable("schema", delete_of("note:victim"))?;
    let CultMeshRudpPollOutcome::ApplicationRejected(rejection) = server.poll_once()? else {
        panic!("the eighth consecutive delete must be refused");
    };
    assert_eq!(rejection.operation, CultMeshRudpApplicationOperation::DocumentDelete);
    assert_eq!(rejection.reason, CultMeshRudpRejectionReason::DeleteNotSupported);
    let (error, code, details) = typed_refusal_seen_by(&mut deleter, &receipt, Some(&mut server))?;
    assert_eq!(
        (error, code, details),
        {
            let (c, d, e) = refused("note:victim");
            (e, c, d)
        }
    );
    drain_until_idle(&mut server)?;
    assert_eq!(server.session_count(), 0);
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

/// A peer driven packet by packet, so a test decides what it receives and what
/// it acknowledges.
struct RawPeer {
    socket: UdpSocket,
    target: SocketAddr,
    session: cultnet_rs::CultNetRudpSession,
}

impl RawPeer {
    fn connect<S: CultMeshRudpRawDocumentSink, Q: CultMeshRudpSnapshotSource>(
        server: &mut CultMeshRudpDocumentServer<S, Q, Clock>,
        connection_id: u32,
    ) -> Result<Self> {
        let socket = UdpSocket::bind("127.0.0.1:0")?;
        socket.set_read_timeout(Some(Duration::from_millis(50)))?;
        let mut peer = Self {
            socket,
            target: server.local_addr()?,
            session: cultnet_rs::CultNetRudpSession::new(cultnet_rs::CultNetRudpSessionOptions {
                connection_id,
                initial_sequence: Some(100),
                resend_delay_ms: 10_000,
                max_pending_reliable_packets: Some(64),
            }),
        };
        let connect = peer.session.create_connect(0, Vec::new())?;
        peer.send_packet(&connect)?;
        server.poll_once()?;
        let accept = peer.receive().expect("the server accepts the connect");
        assert_eq!(
            accept.packet_type,
            cultnet_rs::CultNetRudpPacketType::Accept
        );
        peer.session.receive(&accept, 1)?;
        Ok(peer)
    }

    fn send_packet(&self, packet: &cultnet_rs::CultNetRudpPacket) -> Result<()> {
        self.socket
            .send_to(&cultnet_rs::encode_rudp_packet(packet)?, self.target)?;
        Ok(())
    }

    /// Sends `message` reliably and in order; returns its sequences.
    fn send(&mut self, message: &CultNetMessage) -> Result<Vec<u32>> {
        let payload = encode_cultnet_message_to_vec(message, CultNetWireContract::CultNetSchemaV0)?;
        self.send_payload(payload)
    }

    /// Sends `payload` as one reliable, ordered schema frame; returns its sequences.
    fn send_payload(&mut self, payload: Vec<u8>) -> Result<Vec<u32>> {
        let options = cultnet_rs::CultNetRudpSendOptions {
            reliable: true,
            ordered: true,
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
            error: "write_not_authorized: the catalog refused the document".into(),
            code: Some(CultNetErrorCode::WriteNotAuthorized),
            details: Some(CultNetErrorDetails {
                field: Some("recordKey".into()),
                value: Some(document("refused", vec![4, 5, 6]).record_key),
                as_of: None,
                current: None,
            }),
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

/// A peer on `server` that has sent seven unreadable frames, each answered and processed, and then the
/// eighth, whose answer is waiting at its socket. Returns the peer and the eighth frame's sequence.
fn peer_at_the_cap(server: &mut Server, connection_id: u32) -> Result<(RawPeer, u32)> {
    let mut peer = RawPeer::connect(server, connection_id)?;
    for _ in 0..7 {
        peer.send_payload(garbage())?;
        assert!(matches!(
            server.poll_once()?,
            CultMeshRudpPollOutcome::ApplicationRejected(_)
        ));
        for packet in peer.drain() {
            peer.session.receive(&packet, 3)?;
        }
    }
    let capped = peer.send_payload(garbage())?[0];
    assert!(matches!(
        server.poll_once()?,
        CultMeshRudpPollOutcome::ApplicationRejected(_)
    ));
    Ok((peer, capped))
}

fn is_goodbye(packet: &cultnet_rs::CultNetRudpPacket) -> bool {
    packet.packet_type == cultnet_rs::CultNetRudpPacketType::Disconnect
}

fn is_answer(packet: &cultnet_rs::CultNetRudpPacket) -> bool {
    packet.packet_type == cultnet_rs::CultNetRudpPacketType::Data && packet.reliable
}

/// The capped frame's answer is reliable: its datagram is lost, the server resends it, and only once the
/// peer has acknowledged it does the goodbye follow. Neither transmission acknowledges the capped frame.
#[test]
fn a_lost_capped_answer_is_resent_and_the_goodbye_waits_for_its_acknowledgement() -> Result<()> {
    let clock = Clock::new(81_000);
    let mut server = server(Default::default(), clock.clone(), Sink::default(), Source::default())?;
    let (mut peer, capped) = peer_at_the_cap(&mut server, 81)?;

    let first = peer.drain();
    assert!(first.iter().any(is_answer), "fixture: the answer was sent, and is lost here");
    assert!(!first.iter().any(is_goodbye), "no goodbye before the answer is acknowledged");
    assert!(
        !first.iter().any(|packet| acknowledges(packet, capped)),
        "nothing sent for the capped frame acknowledges it"
    );
    assert_eq!(server.session_count(), 1);

    clock.set_monotonic(81_000 + 60);
    server.maintain()?;
    let resent = peer.drain();
    let answer = resent.iter().find(|packet| is_answer(packet)).expect("the lost answer is resent");
    assert!(!acknowledges(answer, capped), "the resent answer acknowledges nothing the peer sent");
    assert!(!resent.iter().any(is_goodbye), "still no goodbye");
    let delivered = peer.session.receive(answer, 4)?.delivered;
    let Ok(CultNetMessage::Error { code, .. }) =
        decode_cultnet_message_from_slice(&delivered[0].payload, CultNetWireContract::CultNetSchemaV0)
    else {
        panic!("the resent answer is a typed refusal");
    };
    assert_eq!(code, Some(CultNetErrorCode::MessageInvalid));

    peer.send_packet(&peer.session.create_ack_for(answer.sequence))?;
    assert_eq!(server.poll_once()?, CultMeshRudpPollOutcome::Handled);
    assert!(peer.drain().iter().any(is_goodbye), "acknowledged: the goodbye follows");
    assert_eq!(server.session_count(), 0);
    Ok(())
}

/// A peer that never acknowledges the answer is not kept for ever: the session ends after the grace.
#[test]
fn a_capped_session_whose_answer_is_never_acknowledged_ends_after_the_grace() -> Result<()> {
    let clock = Clock::new(82_000);
    let mut server = server(Default::default(), clock.clone(), Sink::default(), Source::default())?;
    let (peer, _) = peer_at_the_cap(&mut server, 82)?;
    peer.drain();

    clock.set_monotonic(82_000 + 1_999);
    server.maintain()?;
    assert!(!peer.drain().iter().any(is_goodbye), "inside the grace");
    assert_eq!(server.session_count(), 1);
    clock.set_monotonic(82_000 + 2_000);
    server.maintain()?;
    assert!(peer.drain().iter().any(is_goodbye), "the grace ran out");
    assert_eq!(server.session_count(), 0);
    Ok(())
}

/// Nothing behind the capped frame is served while its answer is delivered.
#[test]
fn nothing_behind_the_capped_frame_is_served() -> Result<()> {
    let sink = Sink::default();
    let mut server = server(Default::default(), Clock::new(83_000), sink.clone(), Source::default())?;
    let (mut peer, _) = peer_at_the_cap(&mut server, 83)?;
    peer.send_payload(valid_put("behind-the-cap"))?;
    for _ in 0..3 {
        server.poll_once()?;
    }
    assert!(sink.0.lock().unwrap().receipts.is_empty(), "the put behind the cap was not stored");
    Ok(())
}

/// A Ping inside the grace is answered with a Pong that acknowledges nothing: its ack fields are built from
/// what the session received, which includes the capped frame.
#[test]
fn a_pong_in_the_grace_after_the_cap_acknowledges_nothing() -> Result<()> {
    let mut server = server(Default::default(), Clock::new(84_000), Sink::default(), Source::default())?;
    let (mut peer, capped) = peer_at_the_cap(&mut server, 84)?;
    peer.drain();

    let ping = peer.session.create_ping(b"alive".to_vec());
    peer.send_packet(&ping)?;
    assert_eq!(server.poll_once()?, CultMeshRudpPollOutcome::Handled);
    let pong = peer
        .drain()
        .into_iter()
        .find(|packet| packet.packet_type == cultnet_rs::CultNetRudpPacketType::Pong)
        .expect("the ping is answered");
    assert_eq!(pong.payload, b"alive");
    assert!(!acknowledges(&pong, capped), "the Pong acknowledges the capped frame");
    assert_eq!((pong.ack, pong.ack_mask), (0, 0));
    Ok(())
}

/// The grace deadline saturates: at the end of the clock the answer is not given up on at once.
#[test]
fn the_grace_deadline_does_not_wrap_at_the_end_of_the_clock() -> Result<()> {
    let mut server = server(Default::default(), Clock::new(u64::MAX - 10), Sink::default(), Source::default())?;
    let (peer, _) = peer_at_the_cap(&mut server, 85)?;
    peer.drain();
    server.maintain()?;
    assert!(!peer.drain().iter().any(is_goodbye), "the deadline wrapped and the session ended before its grace");
    assert_eq!(server.session_count(), 1);
    Ok(())
}

// Write receipts (contracts/cultnet/interop/write-receipt.md). A connection whose hello lists both new frames is
// answered with `write_admitted.v0` or `error.v1`, each carrying the write's id; any other connection is answered
// exactly as before.

fn receipt_hello() -> CultNetMessage {
    CultNetMessage::Hello {
        runtime_id: "receipt-client".into(),
        runtime_kind: "test".into(),
        agent_id: None,
        role: None,
        display_name: None,
        supported_document_types: None,
        supported_mutation_contracts: None,
        supported_message_versions: Some(vec![
            "cultnet.write_admitted.v0".into(),
            "cultnet.error.v1".into(),
        ]),
        transport_profiles: None,
        supports_schema_catalog: None,
    }
}

fn put(id: &str, key: &str, payload: Vec<u8>) -> CultNetMessage {
    CultNetMessage::DocumentPutRaw {
        message_id: id.into(),
        document: document(key, payload),
    }
}

/// Polls the server until `client` has `count` schema frames or the polling runs out; rejections are expected here.
fn frames_after<Q: CultMeshRudpSnapshotSource>(
    server: &mut CultMeshRudpDocumentServer<Sink, Q, Clock>,
    client: &mut CultNetRudpSocketTransportConnection,
    count: usize,
) -> Result<Vec<CultNetMessage>> {
    let mut frames = Vec::new();
    for _ in 0..600 {
        server.poll_once()?;
        client.poll_resends()?;
        while let Some(frame) = client.receive_once()? {
            if frame.channel_id == "schema" {
                frames.push(decode_cultnet_message_from_slice(
                    &frame.payload,
                    CultNetWireContract::CultNetSchemaV0,
                )?);
            }
        }
        if frames.len() >= count {
            break;
        }
        thread::sleep(Duration::from_millis(1));
    }
    Ok(frames)
}

/// The hello a receipt server sends a receipt-capable client, ahead of any answer.
fn server_hello() -> CultNetMessage {
    CultNetMessage::Hello {
        runtime_id: "cultmesh-rs-rudp-document-server".into(),
        runtime_kind: "cultmesh-rs".into(),
        agent_id: None,
        role: None,
        display_name: None,
        supported_document_types: None,
        supported_mutation_contracts: None,
        supported_message_versions: Some(vec![
            "cultnet.write_admitted.v0".into(),
            "cultnet.error.v1".into(),
        ]),
        transport_profiles: None,
        supports_schema_catalog: None,
    }
}

/// The `count` answers after the server's hello, which must come first.
fn answers_after<Q: CultMeshRudpSnapshotSource>(
    server: &mut CultMeshRudpDocumentServer<Sink, Q, Clock>,
    client: &mut CultNetRudpSocketTransportConnection,
    count: usize,
) -> Result<Vec<CultNetMessage>> {
    let mut frames = frames_after(server, client, count + 1)?;
    assert_eq!(frames.first(), Some(&server_hello()), "the server answers the hello first");
    Ok(frames.split_off(1))
}

fn admitted(id: &str, key: &str) -> CultNetMessage {
    CultNetMessage::WriteAdmitted {
        message_id: id.into(),
        operation: "put".into(),
        schema_id: "test.raw.v1".into(),
        record_key: key.into(),
    }
}

#[test]
fn an_admitted_put_on_a_receipt_connection_is_answered_with_a_receipt_carrying_its_id() -> Result<()> {
    let sink = Sink::default();
    let mut server = server(Default::default(), Clock::new(70_000), sink.clone(), Source::default())?;
    let mut peer = client(server.local_addr()?, 201)?;
    connect(&mut server, &mut [&mut peer])?;
    send_reliable(&mut peer, &receipt_hello())?;
    send_reliable(&mut peer, &put("m1", "note:1", vec![1, 2, 3]))?;
    assert_eq!(answers_after(&mut server, &mut peer, 1)?, vec![admitted("m1", "note:1")]);
    assert_eq!(sink.0.lock().unwrap().receipts.len(), 1);
    Ok(())
}

#[test]
fn a_connection_without_the_capability_gets_no_receipt() -> Result<()> {
    let sink = Sink::default();
    let mut server = server(Default::default(), Clock::new(70_000), sink.clone(), Source::default())?;
    let mut peer = client(server.local_addr()?, 202)?;
    connect(&mut server, &mut [&mut peer])?;
    send_reliable(&mut peer, &put("m1", "note:1", vec![1, 2, 3]))?;
    assert_eq!(frames_after(&mut server, &mut peer, 1)?, vec![]);
    assert_eq!(sink.0.lock().unwrap().receipts.len(), 1, "the put was admitted all the same");
    Ok(())
}

#[test]
fn a_resend_of_an_admitted_write_is_re_receipted_without_applying_it_twice() -> Result<()> {
    let sink = Sink::default();
    let mut server = server(Default::default(), Clock::new(70_000), sink.clone(), Source::default())?;
    let mut peer = client(server.local_addr()?, 203)?;
    connect(&mut server, &mut [&mut peer])?;
    send_reliable(&mut peer, &receipt_hello())?;
    send_reliable(&mut peer, &put("m1", "note:1", vec![1, 2, 3]))?;
    send_reliable(&mut peer, &put("m1", "note:1", vec![1, 2, 3]))?;
    assert_eq!(
        answers_after(&mut server, &mut peer, 2)?,
        vec![admitted("m1", "note:1"), admitted("m1", "note:1")]
    );
    assert_eq!(sink.0.lock().unwrap().receipts.len(), 1);
    Ok(())
}

#[test]
fn the_same_id_with_other_content_is_refused_message_invalid_never_re_receipted() -> Result<()> {
    let sink = Sink::default();
    let mut server = server(Default::default(), Clock::new(70_000), sink.clone(), Source::default())?;
    let mut peer = client(server.local_addr()?, 204)?;
    connect(&mut server, &mut [&mut peer])?;
    send_reliable(&mut peer, &receipt_hello())?;
    send_reliable(&mut peer, &put("m1", "note:1", vec![1, 2, 3]))?;
    send_reliable(&mut peer, &put("m1", "note:1", vec![9, 9, 9]))?;
    let frames = answers_after(&mut server, &mut peer, 2)?;
    assert_eq!(frames[0], admitted("m1", "note:1"));
    let CultNetMessage::ErrorV1 { code, details, message_id, .. } = &frames[1] else {
        panic!("the second answer must be a refusal, got {:?}", frames[1]);
    };
    assert_eq!(*code, Some(CultNetErrorCode::MessageInvalid));
    assert_eq!(message_id.as_deref(), Some("m1"));
    assert_eq!(details.as_ref().and_then(|d| d.field.as_deref()), Some("messageId"));
    assert_eq!(sink.0.lock().unwrap().receipts.len(), 1, "the changed write was not applied");
    Ok(())
}

#[test]
fn a_refused_put_on_a_receipt_connection_is_a_typed_error_naming_its_id() -> Result<()> {
    let sink = Sink::fail_once();
    let mut server = server(Default::default(), Clock::new(70_000), sink.clone(), Source::default())?;
    let mut peer = client(server.local_addr()?, 205)?;
    connect(&mut server, &mut [&mut peer])?;
    send_reliable(&mut peer, &receipt_hello())?;
    send_reliable(&mut peer, &put("m7", "note:7", vec![1]))?;
    let frames = answers_after(&mut server, &mut peer, 1)?;
    let Some(CultNetMessage::ErrorV1 { code, message_id, .. }) = frames.first() else {
        panic!("expected error.v1, got {frames:?}");
    };
    assert_eq!(*code, Some(CultNetErrorCode::WriteNotAuthorized));
    assert_eq!(message_id.as_deref(), Some("m7"));
    Ok(())
}

#[test]
fn a_hello_listing_only_one_of_the_two_frames_does_not_opt_in() -> Result<()> {
    for listed in ["cultnet.write_admitted.v0", "cultnet.error.v1"] {
        let sink = Sink::default();
        let mut server = server(Default::default(), Clock::new(70_000), sink.clone(), Source::default())?;
        let mut peer = client(server.local_addr()?, 206)?;
        connect(&mut server, &mut [&mut peer])?;
        let mut hello = receipt_hello();
        if let CultNetMessage::Hello { supported_message_versions, .. } = &mut hello {
            *supported_message_versions = Some(vec![listed.into()]);
        }
        send_reliable(&mut peer, &hello)?;
        send_reliable(&mut peer, &put("m1", "note:1", vec![1]))?;
        assert_eq!(frames_after(&mut server, &mut peer, 1)?, vec![], "listed only {listed}");
    }
    Ok(())
}

fn refusal_of(message: &CultNetMessage) -> (Option<CultNetErrorCode>, Option<String>) {
    let CultNetMessage::ErrorV1 { code, message_id, .. } = message else {
        panic!("expected error.v1, got {message:?}");
    };
    (code.clone(), message_id.clone())
}

#[test]
fn a_refused_put_leaves_the_receipt_session_open_for_the_next_write() -> Result<()> {
    let sink = Sink::fail_once();
    let mut server = server(Default::default(), Clock::new(70_000), sink.clone(), Source::default())?;
    let mut peer = client(server.local_addr()?, 207)?;
    connect(&mut server, &mut [&mut peer])?;
    send_reliable(&mut peer, &receipt_hello())?;
    send_reliable(&mut peer, &put("m1", "note:1", vec![1]))?;
    send_reliable(&mut peer, &put("m2", "note:2", vec![2]))?;
    let answers = answers_after(&mut server, &mut peer, 2)?;
    assert_eq!(refusal_of(&answers[0]), (Some(CultNetErrorCode::WriteNotAuthorized), Some("m1".into())));
    assert_eq!(answers[1], admitted("m2", "note:2"));
    assert_eq!(server.session_count(), 1);
    Ok(())
}

#[test]
fn a_reused_id_refuses_the_write_not_the_session() -> Result<()> {
    let sink = Sink::default();
    let mut server = server(Default::default(), Clock::new(70_000), sink.clone(), Source::default())?;
    let mut peer = client(server.local_addr()?, 208)?;
    connect(&mut server, &mut [&mut peer])?;
    send_reliable(&mut peer, &receipt_hello())?;
    send_reliable(&mut peer, &put("m1", "note:1", vec![1]))?;
    send_reliable(&mut peer, &put("m1", "note:1", vec![2]))?;
    send_reliable(&mut peer, &put("m3", "note:3", vec![3]))?;
    let answers = answers_after(&mut server, &mut peer, 3)?;
    assert_eq!(refusal_of(&answers[1]), (Some(CultNetErrorCode::MessageInvalid), Some("m1".into())));
    assert_eq!(answers[2], admitted("m3", "note:3"));
    assert_eq!(server.session_count(), 1);
    Ok(())
}

#[test]
fn a_full_dedupe_table_refuses_new_writes_and_keeps_the_session_and_its_receipts() -> Result<()> {
    let sink = Sink::default();
    let mut server = server(Default::default(), Clock::new(70_000), sink.clone(), Source::default())?;
    let mut peer = client(server.local_addr()?, 209)?;
    connect(&mut server, &mut [&mut peer])?;
    send_reliable(&mut peer, &receipt_hello())?;
    for index in 0..1024 {
        send_reliable(&mut peer, &put(&format!("w{index}"), &format!("note:{index}"), vec![1]))?;
        if index % 16 == 15 {
            for _ in 0..40 {
                server.poll_once()?;
                peer.poll_resends()?;
                while peer.receive_once()?.is_some() {}
            }
        }
    }
    send_reliable(&mut peer, &put("over", "note:over", vec![1]))?;
    send_reliable(&mut peer, &put("w0", "note:0", vec![1]))?;
    let answers = frames_after(&mut server, &mut peer, 2)?;
    assert_eq!(refusal_of(&answers[0]), (Some(CultNetErrorCode::LimitReached), Some("over".into())));
    let CultNetMessage::ErrorV1 { error, details, .. } = &answers[0] else { unreachable!() };
    assert_eq!(error, "limit_reached: the session's remembered-write limit is full", "the cap has its own fixed text");
    assert_eq!(details, &Some(CultNetErrorDetails::default()), "limit_reached names no field: details all null");
    assert_eq!(answers[1], admitted("w0", "note:0"), "a remembered id is still answered idempotently");
    assert_eq!(server.session_count(), 1);
    Ok(())
}

#[test]
fn a_readable_id_with_a_bad_field_is_refused_with_that_id() -> Result<()> {
    let sink = Sink::default();
    let mut server = server(Default::default(), Clock::new(70_000), sink.clone(), Source::default())?;
    let mut peer = client(server.local_addr()?, 210)?;
    connect(&mut server, &mut [&mut peer])?;
    send_reliable(&mut peer, &receipt_hello())?;
    // A put with no document: it does not decode, but its id is there to read.
    let frame = rmp_serde::to_vec_named(&serde_json::json!({
        "schemaVersion": "cultnet.document_put_raw.v0",
        "messageId": "bad-1",
    }))?;
    peer.send_reliable("schema", frame)?;
    peer.send_reliable("schema", garbage())?;
    let answers = answers_after(&mut server, &mut peer, 2)?;
    assert_eq!(refusal_of(&answers[0]), (Some(CultNetErrorCode::MessageInvalid), Some("bad-1".into())));
    assert_eq!(refusal_of(&answers[1]), (Some(CultNetErrorCode::MessageInvalid), None), "no id can be read");
    Ok(())
}

#[test]
fn a_second_hello_does_not_change_the_capability() -> Result<()> {
    let mut downgrade = receipt_hello();
    if let CultNetMessage::Hello { supported_message_versions, .. } = &mut downgrade {
        *supported_message_versions = None;
    }
    // Capable, then a hello listing nothing: still capable.
    let sink = Sink::default();
    let mut server_a = server(Default::default(), Clock::new(70_000), sink.clone(), Source::default())?;
    let mut peer = client(server_a.local_addr()?, 212)?;
    connect(&mut server_a, &mut [&mut peer])?;
    send_reliable(&mut peer, &receipt_hello())?;
    send_reliable(&mut peer, &downgrade)?;
    send_reliable(&mut peer, &put("m1", "note:1", vec![1]))?;
    assert_eq!(answers_after(&mut server_a, &mut peer, 1)?, vec![admitted("m1", "note:1")]);
    // Legacy by its first write, then a capable hello: still legacy.
    let mut server_b = server(Default::default(), Clock::new(70_000), Sink::default(), Source::default())?;
    let mut late = client(server_b.local_addr()?, 213)?;
    connect(&mut server_b, &mut [&mut late])?;
    send_reliable(&mut late, &put("m1", "note:1", vec![1]))?;
    send_reliable(&mut late, &receipt_hello())?;
    send_reliable(&mut late, &put("m2", "note:2", vec![1]))?;
    assert_eq!(frames_after(&mut server_b, &mut late, 1)?, vec![]);
    Ok(())
}

#[test]
fn a_receipt_session_that_runs_out_of_payload_budget_is_ended_not_left_silent() -> Result<()> {
    let options = CultMeshRudpDocumentServerOptions {
        max_admitted_payload_bytes_per_session: 400,
        max_snapshot_response_bytes: 200,
        ..Default::default()
    };
    let mut server = server(options, Clock::new(70_000), Sink::default(), Source::default())?;
    let mut peer = client(server.local_addr()?, 214)?;
    connect(&mut server, &mut [&mut peer])?;
    send_reliable(&mut peer, &receipt_hello())?;
    for index in 0..20 {
        send_reliable(&mut peer, &put(&format!("w{index}"), &format!("note:{index}"), vec![1; 64]))?;
    }
    frames_after(&mut server, &mut peer, usize::MAX)?;
    assert_eq!(server.session_count(), 0, "the writes the budget dropped were answered by ending the session");
    Ok(())
}

#[test]
fn a_receipt_that_cannot_be_queued_ends_the_session_instead_of_leaving_the_write_unanswered() -> Result<()> {
    let options = CultMeshRudpDocumentServerOptions {
        max_pending_reliable_packets_per_session: 3,
        ..Default::default()
    };
    let sink = Sink::default();
    let mut server = server(options, Clock::new(70_000), sink.clone(), Source::default())?;
    let mut peer = client(server.local_addr()?, 215)?;
    connect(&mut server, &mut [&mut peer])?;
    send_reliable(&mut peer, &receipt_hello())?;
    // The peer never reads, so it acknowledges nothing and the server's reliable queue fills.
    for index in 0..8 {
        send_reliable(&mut peer, &put(&format!("w{index}"), &format!("note:{index}"), vec![1]))?;
        for _ in 0..20 {
            server.poll_once()?;
        }
    }
    assert_eq!(server.session_count(), 0, "a receipt the queue could not take ended the session");
    // The goodbye names a local failure: it is not the "session refused a packet" goodbye of a protocol refusal.
    let mut reason = None;
    for _ in 0..200 {
        while peer.receive_once()?.is_some() {}
        if let Some(found) = peer.disconnect_reason() {
            reason = Some(found.to_vec());
            break;
        }
        thread::sleep(Duration::from_millis(1));
    }
    assert_eq!(reason.expect("the peer was told the session ended"), b"server could not answer a write".to_vec());
    Ok(())
}

fn delete(id: &str, key: &str) -> CultNetMessage {
    CultNetMessage::DocumentDelete {
        message_id: id.into(),
        schema_id: "test.raw.v1".into(),
        record_key: key.into(),
    }
}

#[test]
fn a_resend_is_answered_from_memory_and_the_sink_is_not_asked_again() -> Result<()> {
    let sink = Sink::default();
    let mut server = server(Default::default(), Clock::new(70_000), sink.clone(), Source::default())?;
    let mut peer = client(server.local_addr()?, 216)?;
    connect(&mut server, &mut [&mut peer])?;
    send_reliable(&mut peer, &receipt_hello())?;
    send_reliable(&mut peer, &put("m1", "note:1", vec![1]))?;
    assert_eq!(answers_after(&mut server, &mut peer, 1)?, vec![admitted("m1", "note:1")]);
    // The peer's authority over the key ends; the answer already given is still the answer.
    sink.0.lock().unwrap().denied_keys.push("note:1".into());
    send_reliable(&mut peer, &put("m1", "note:1", vec![1]))?;
    assert_eq!(frames_after(&mut server, &mut peer, 1)?, vec![admitted("m1", "note:1")]);
    Ok(())
}

#[test]
fn a_used_id_with_other_content_is_message_invalid_even_for_a_key_the_peer_may_not_write() -> Result<()> {
    let sink = Sink::denying(&["secret"]);
    let mut server = server(Default::default(), Clock::new(70_000), sink.clone(), Source::default())?;
    let mut peer = client(server.local_addr()?, 211)?;
    connect(&mut server, &mut [&mut peer])?;
    send_reliable(&mut peer, &receipt_hello())?;
    send_reliable(&mut peer, &put("m1", "note:1", vec![1]))?;
    send_reliable(&mut peer, &put("m1", "secret", vec![1]))?;
    let answers = answers_after(&mut server, &mut peer, 2)?;
    assert_eq!(refusal_of(&answers[1]), (Some(CultNetErrorCode::MessageInvalid), Some("m1".into())));
    Ok(())
}

#[test]
fn a_refused_write_is_remembered_and_its_resend_replays_the_refusal() -> Result<()> {
    let sink = Sink::fail_once();
    let mut server = server(Default::default(), Clock::new(70_000), sink.clone(), Source::default())?;
    let mut peer = client(server.local_addr()?, 217)?;
    connect(&mut server, &mut [&mut peer])?;
    send_reliable(&mut peer, &receipt_hello())?;
    send_reliable(&mut peer, &put("m1", "note:1", vec![1]))?;
    send_reliable(&mut peer, &put("m1", "note:1", vec![1]))?;
    let answers = answers_after(&mut server, &mut peer, 2)?;
    assert_eq!(refusal_of(&answers[0]), (Some(CultNetErrorCode::WriteNotAuthorized), Some("m1".into())));
    assert_eq!(answers[1], answers[0], "the same refusal again, not a fresh attempt that would now succeed");
    assert_eq!(sink.0.lock().unwrap().receipts.len(), 0);
    Ok(())
}

/// The sink is asked once, on an id's first sighting: not again for a resend (admitted or refused), and
/// not at all on a session that never declared the receipt capability. The sink here only counts, so a
/// flag that stopped or inverted the ask cannot hide behind the sink refusing at the write.
#[test]
fn authorize_is_called_once_per_first_sighting_and_only_for_receipt_sessions() -> Result<()> {
    let sink = Sink::default();
    let mut server = server(Default::default(), Clock::new(70_000), sink.clone(), Source::default())?;
    let mut peer = client(server.local_addr()?, 219)?;
    let mut legacy = client(server.local_addr()?, 220)?;
    connect(&mut server, &mut [&mut peer, &mut legacy])?;
    send_reliable(&mut peer, &receipt_hello())?;
    send_reliable(&mut peer, &put("m1", "note:1", vec![1]))?;
    assert_eq!(answers_after(&mut server, &mut peer, 1)?, vec![admitted("m1", "note:1")]);
    assert_eq!(sink.0.lock().unwrap().authorized, vec!["m1".to_string()], "first sighting asks once");
    send_reliable(&mut peer, &put("m1", "note:1", vec![1]))?;
    assert_eq!(frames_after(&mut server, &mut peer, 1)?, vec![admitted("m1", "note:1")]);
    assert_eq!(sink.0.lock().unwrap().authorized, vec!["m1".to_string()], "the resend does not ask again");
    send_reliable(&mut legacy, &put("l1", "note:9", vec![1]))?;
    for _ in 0..40 {
        server.poll_once()?;
        legacy.poll_resends()?;
        while legacy.receive_once()?.is_some() {}
    }
    let state = sink.0.lock().unwrap();
    assert_eq!(state.receipts.len(), 2, "the legacy put was committed");
    assert_eq!(state.authorized, vec!["m1".to_string()], "a legacy session is never asked");
    Ok(())
}

#[test]
fn a_delete_that_reuses_a_puts_id_is_message_invalid() -> Result<()> {
    let sink = Sink::default();
    let mut server = server(Default::default(), Clock::new(70_000), sink.clone(), Source::default())?;
    let mut peer = client(server.local_addr()?, 218)?;
    connect(&mut server, &mut [&mut peer])?;
    send_reliable(&mut peer, &receipt_hello())?;
    send_reliable(&mut peer, &put("m1", "note:1", vec![1]))?;
    send_reliable(&mut peer, &delete("m1", "note:1"))?;
    let answers = answers_after(&mut server, &mut peer, 2)?;
    assert_eq!(answers[0], admitted("m1", "note:1"));
    let CultNetMessage::ErrorV1 { code, details, message_id, .. } = &answers[1] else {
        panic!("expected error.v1, got {:?}", answers[1]);
    };
    assert_eq!(*code, Some(CultNetErrorCode::MessageInvalid));
    assert_eq!(message_id.as_deref(), Some("m1"));
    assert_eq!(details.as_ref().and_then(|d| d.field.as_deref()), Some("messageId"));
    Ok(())
}

#[test]
fn deletes_on_a_receipt_session_are_refused_one_by_one_and_never_end_it() -> Result<()> {
    let mut server = server(Default::default(), Clock::new(70_000), Sink::default(), Source::default())?;
    let mut peer = client(server.local_addr()?, 219)?;
    connect(&mut server, &mut [&mut peer])?;
    send_reliable(&mut peer, &receipt_hello())?;
    for index in 0..12 {
        send_reliable(&mut peer, &delete(&format!("d{index}"), "note:1"))?;
    }
    send_reliable(&mut peer, &delete("d3", "note:1"))?;
    send_reliable(&mut peer, &put("m1", "note:1", vec![1]))?;
    let answers = answers_after(&mut server, &mut peer, 14)?;
    assert_eq!(answers.len(), 14);
    assert_eq!(refusal_of(&answers[12]), refusal_of(&answers[3]), "a resent delete replays its refusal");
    assert_eq!(answers[13], admitted("m1", "note:1"));
    assert_eq!(server.session_count(), 1);
    Ok(())
}

#[test]
fn a_delete_as_the_first_frame_settles_the_connection_as_legacy() -> Result<()> {
    let mut server = server(Default::default(), Clock::new(70_000), Sink::default(), Source::default())?;
    let mut peer = client(server.local_addr()?, 220)?;
    connect(&mut server, &mut [&mut peer])?;
    send_reliable(&mut peer, &delete("d1", "note:1"))?;
    send_reliable(&mut peer, &receipt_hello())?;
    send_reliable(&mut peer, &put("m1", "note:1", vec![1]))?;
    let frames = frames_after(&mut server, &mut peer, 3)?;
    assert_eq!(frames.len(), 1, "only the legacy delete refusal: {frames:?}");
    assert!(matches!(frames[0], CultNetMessage::Error { .. }), "{:?}", frames[0]);
    Ok(())
}

#[test]
fn a_refusal_that_cannot_be_queued_ends_the_session_instead_of_vanishing() -> Result<()> {
    let options = CultMeshRudpDocumentServerOptions {
        max_pending_reliable_packets_per_session: 3,
        ..Default::default()
    };
    let mut server = server(options, Clock::new(70_000), Sink::denying(&["x"]), Source::default())?;
    let mut peer = client(server.local_addr()?, 221)?;
    connect(&mut server, &mut [&mut peer])?;
    send_reliable(&mut peer, &receipt_hello())?;
    for index in 0..8 {
        send_reliable(&mut peer, &put(&format!("w{index}"), "x", vec![1]))?;
        for _ in 0..20 {
            server.poll_once()?;
        }
    }
    assert_eq!(server.session_count(), 0, "a refusal the queue could not take ended the session");
    Ok(())
}

#[test]
fn refusal_frames_from_a_peer_are_not_protocol_violations() -> Result<()> {
    let mut server = server(Default::default(), Clock::new(70_000), Sink::default(), Source::default())?;
    let mut peer = client(server.local_addr()?, 222)?;
    connect(&mut server, &mut [&mut peer])?;
    send_reliable(&mut peer, &receipt_hello())?;
    for _ in 0..12 {
        send_reliable(
            &mut peer,
            &CultNetMessage::ErrorV1 {
                error: "message_invalid: nope".into(),
                code: Some(CultNetErrorCode::MessageInvalid),
                details: None,
                message_id: Some("x".into()),
            },
        )?;
        send_reliable(
            &mut peer,
            &CultNetMessage::Error { error: "nope".into(), code: None, details: None },
        )?;
    }
    send_reliable(&mut peer, &put("m1", "note:1", vec![1]))?;
    assert_eq!(answers_after(&mut server, &mut peer, 1)?, vec![admitted("m1", "note:1")]);
    assert_eq!(server.session_count(), 1);
    Ok(())
}

#[test]
fn a_put_that_reuses_a_deletes_id_is_message_invalid() -> Result<()> {
    let sink = Sink::default();
    let mut server = server(Default::default(), Clock::new(70_000), sink.clone(), Source::default())?;
    let mut peer = client(server.local_addr()?, 223)?;
    connect(&mut server, &mut [&mut peer])?;
    send_reliable(&mut peer, &receipt_hello())?;
    send_reliable(&mut peer, &delete("d1", "note:1"))?;
    send_reliable(&mut peer, &put("d1", "note:1", vec![1]))?;
    let answers = answers_after(&mut server, &mut peer, 2)?;
    assert_eq!(refusal_of(&answers[1]), (Some(CultNetErrorCode::MessageInvalid), Some("d1".into())));
    assert_eq!(sink.0.lock().unwrap().receipts.len(), 0, "the reusing put was not applied");
    Ok(())
}

fn error_text_and_field(message: &CultNetMessage) -> (String, Option<String>) {
    let CultNetMessage::ErrorV1 { error, details, .. } = message else { panic!("expected error.v1, got {message:?}") };
    (error.clone(), details.as_ref().and_then(|d| d.field.clone()))
}

/// The server-wide budget counts fixed-size entries across sessions; a write over it is refused for that write
/// alone, the refusal is remembered (a resend replays it, no slot is freed), and every session is kept.
#[test]
fn a_server_wide_memory_budget_refuses_the_write_over_it_and_keeps_the_sessions() -> Result<()> {
    let options = CultMeshRudpDocumentServerOptions {
        max_remembered_write_bytes: 2 * 65,
        ..Default::default()
    };
    let sink = Sink::default();
    let mut server = server(options, Clock::new(70_000), sink.clone(), Source::default())?;
    let mut a = client(server.local_addr()?, 230)?;
    let mut b = client(server.local_addr()?, 231)?;
    connect(&mut server, &mut [&mut a, &mut b])?;
    send_reliable(&mut a, &receipt_hello())?;
    send_reliable(&mut b, &receipt_hello())?;
    send_reliable(&mut a, &put("a1", "note:a1", vec![1]))?;
    send_reliable(&mut b, &put("b1", "note:b1", vec![1]))?;
    for _ in 0..200 {
        server.poll_once()?;
        a.poll_resends()?;
        b.poll_resends()?;
        while a.receive_once()?.is_some() {}
        while b.receive_once()?.is_some() {}
    }
    assert_eq!(sink.0.lock().unwrap().receipts.len(), 2);
    send_reliable(&mut a, &put("a2", "note:a2", vec![1]))?;
    send_reliable(&mut a, &put("a2", "note:a2", vec![1]))?;
    let answers = frames_after(&mut server, &mut a, 2)?;
    for answer in &answers {
        assert_eq!(refusal_of(answer), (Some(CultNetErrorCode::LimitReached), Some("a2".into())), "the resend replays the refusal");
        let CultNetMessage::ErrorV1 { details, .. } = answer else { unreachable!() };
        assert_eq!(details, &Some(CultNetErrorDetails::default()));
    }
    assert_eq!(sink.0.lock().unwrap().receipts.len(), 2, "the refused write was not applied");
    assert_eq!(server.session_count(), 2, "both sessions are kept");
    send_reliable(&mut a, &put("a1", "note:a1", vec![1]))?;
    assert_eq!(frames_after(&mut server, &mut a, 1)?, vec![admitted("a1", "note:a1")], "a remembered id is still answered");
    Ok(())
}

/// A message id over 256 bytes is refused before anything is remembered: no entry, no budget charge.
#[test]
fn a_message_id_over_256_bytes_is_refused_before_anything_is_remembered() -> Result<()> {
    let options = CultMeshRudpDocumentServerOptions {
        max_remembered_write_bytes: 65,
        ..Default::default()
    };
    let sink = Sink::default();
    let mut server = server(options, Clock::new(70_000), sink.clone(), Source::default())?;
    let mut peer = client(server.local_addr()?, 232)?;
    connect(&mut server, &mut [&mut peer])?;
    send_reliable(&mut peer, &receipt_hello())?;
    let long = "x".repeat(257);
    send_reliable(&mut peer, &put(&long, "note:1", vec![1]))?;
    send_reliable(&mut peer, &put(&long, "note:1", vec![1]))?;
    send_reliable(&mut peer, &put(&"y".repeat(256), "note:2", vec![1]))?;
    let answers = answers_after(&mut server, &mut peer, 3)?;
    for answer in &answers[..2] {
        assert_eq!(refusal_of(answer), (Some(CultNetErrorCode::MessageInvalid), Some(long.clone())));
        assert_eq!(error_text_and_field(answer), ("message_invalid: messageId exceeds 256 bytes".into(), Some("messageId".into())));
    }
    assert_eq!(answers[2], admitted(&"y".repeat(256), "note:2"), "256 bytes is allowed, and the long id charged no budget");
    assert_eq!(sink.0.lock().unwrap().receipts.len(), 1, "the long-id writes were never applied");
    Ok(())
}

/// A write on an unreliable channel of a receipt session is refused, not dropped and not accepted.
#[test]
fn an_unreliable_write_on_a_receipt_session_is_refused_message_invalid_naming_the_channel() -> Result<()> {
    let sink = Sink::default();
    let mut server = server(Default::default(), Clock::new(70_000), sink.clone(), Source::default())?;
    let mut peer = RawPeer::connect(&mut server, 233)?;
    peer.send(&receipt_hello())?;
    server.poll_once()?;
    let payload = encode_cultnet_message_to_vec(&put("u1", "note:1", vec![1]), CultNetWireContract::CultNetSchemaV0)?;
    let packets = peer.session.send_many(
        "schema",
        payload,
        cultnet_rs::CultNetRudpSendOptions { reliable: false, ordered: false, sequenced: false, now_ms: 3, reliable_expire_after_ms: None },
        Some(1200),
    )?;
    for packet in &packets {
        peer.send_packet(packet)?;
    }
    server.poll_once()?;
    let mut answers = Vec::new();
    for packet in peer.drain() {
        for frame in peer.session.receive(&packet, 4)?.delivered {
            answers.push(decode_cultnet_message_from_slice(&frame.payload, CultNetWireContract::CultNetSchemaV0)?);
        }
    }
    let refusals: Vec<_> = answers.iter().filter(|answer| matches!(answer, CultNetMessage::ErrorV1 { .. })).collect();
    assert_eq!(refusals.len(), 1, "the unreliable write is answered, not dropped: {answers:?}");
    assert_eq!(refusal_of(refusals[0]), (Some(CultNetErrorCode::MessageInvalid), Some("u1".into())));
    assert_eq!(
        error_text_and_field(refusals[0]),
        ("message_invalid: receipt sessions require the reliable channel".into(), Some("channel".into()))
    );
    assert_eq!(sink.0.lock().unwrap().receipts.len(), 0, "nothing was applied");
    assert_eq!(server.session_count(), 1);
    // The id was not consumed: the same write on the reliable channel is admitted.
    peer.send(&put("u1", "note:1", vec![1]))?;
    server.poll_once()?;
    assert_eq!(sink.0.lock().unwrap().receipts.len(), 1);
    Ok(())
}

/// A limit refusal is remembered like any refusal: the same id with other content is a reused id, not a fresh
/// limit refusal; and the remembered refusals are a bounded set (1024), past which a refusal is not remembered.
#[test]
fn limit_refusals_are_remembered_in_a_bounded_set() -> Result<()> {
    let options = CultMeshRudpDocumentServerOptions {
        max_remembered_write_bytes: 65,
        ..Default::default()
    };
    let mut server = server(options, Clock::new(70_000), Sink::default(), Source::default())?;
    let mut peer = client(server.local_addr()?, 234)?;
    connect(&mut server, &mut [&mut peer])?;
    send_reliable(&mut peer, &receipt_hello())?;
    send_reliable(&mut peer, &put("full", "note:full", vec![1]))?;
    for index in 0..1024 {
        send_reliable(&mut peer, &put(&format!("r{index}"), "note:r", vec![1]))?;
        if index % 16 == 15 {
            for _ in 0..40 {
                server.poll_once()?;
                peer.poll_resends()?;
                while peer.receive_once()?.is_some() {}
            }
        }
    }
    // r0 was remembered as refused: other content under its id is a reused id.
    send_reliable(&mut peer, &put("r0", "note:other", vec![2]))?;
    let answers = frames_after(&mut server, &mut peer, 1)?;
    assert_eq!(refusal_of(&answers[0]), (Some(CultNetErrorCode::MessageInvalid), Some("r0".into())));
    // The 1025th refusal found the set full: it was refused but not remembered.
    send_reliable(&mut peer, &put("over", "note:over", vec![1]))?;
    send_reliable(&mut peer, &put("over", "note:over", vec![2]))?;
    let answers = frames_after(&mut server, &mut peer, 2)?;
    assert_eq!(refusal_of(&answers[0]), (Some(CultNetErrorCode::LimitReached), Some("over".into())));
    assert_eq!(refusal_of(&answers[1]), (Some(CultNetErrorCode::LimitReached), Some("over".into())), "not remembered: refused afresh");
    assert_eq!(server.session_count(), 1);
    Ok(())
}

/// A refusal the bounded set could not hold is still the session's first answer for that id: when the budget
/// frees (another session ends), the id is not admitted later in the session, and neither is any id the session
/// has not been seen to use, because the refusal that was not remembered cannot be told from a new write.
#[test]
fn a_session_whose_limit_refusals_overflowed_refuses_new_ids_after_the_budget_frees() -> Result<()> {
    let clock = Clock::new(70_000);
    let options = CultMeshRudpDocumentServerOptions {
        max_remembered_write_bytes: 65,
        session_max_lifetime: Duration::from_millis(100),
        ..Default::default()
    };
    let sink = Sink::default();
    let mut server = server(options, clock.clone(), sink.clone(), Source::default())?;
    let mut holder = client(server.local_addr()?, 240)?;
    connect(&mut server, &mut [&mut holder])?;
    send_reliable(&mut holder, &receipt_hello())?;
    send_reliable(&mut holder, &put("held", "note:held", vec![1]))?;
    assert_eq!(frames_after(&mut server, &mut holder, 2)?[1], admitted("held", "note:held"), "after the server's hello");

    clock.set_monotonic(70_050);
    let mut peer = client(server.local_addr()?, 241)?;
    connect(&mut server, &mut [&mut peer])?;
    send_reliable(&mut peer, &receipt_hello())?;
    for index in 0..1024 {
        send_reliable(&mut peer, &put(&format!("r{index}"), "note:r", vec![1]))?;
        if index % 16 == 15 {
            for _ in 0..40 {
                server.poll_once()?;
                peer.poll_resends()?;
                while peer.receive_once()?.is_some() {}
            }
        }
    }
    send_reliable(&mut peer, &put("over", "note:over", vec![1]))?;
    let answers = frames_after(&mut server, &mut peer, 1)?;
    assert_eq!(refusal_of(&answers[0]), (Some(CultNetErrorCode::LimitReached), Some("over".into())));

    clock.set_monotonic(70_101);
    assert_eq!(server.maintain()?.sessions_expired, 1, "the holder ended and freed the budget");
    assert_eq!(server.session_count(), 1);
    send_reliable(&mut peer, &put("over", "note:over", vec![1]))?;
    send_reliable(&mut peer, &put("fresh", "note:fresh", vec![1]))?;
    let answers = frames_after(&mut server, &mut peer, 2)?;
    assert_eq!(refusal_of(&answers[0]), (Some(CultNetErrorCode::LimitReached), Some("over".into())), "never admitted later");
    assert_eq!(refusal_of(&answers[1]), (Some(CultNetErrorCode::LimitReached), Some("fresh".into())));
    assert_eq!(sink.0.lock().unwrap().receipts.len(), 1, "only the holder's write was applied");
    Ok(())
}

/// A session that ends gives its remembered entries back to the server-wide budget, whichever way it ends.
#[test]
fn a_session_that_ends_frees_its_remembered_writes_from_the_budget() -> Result<()> {
    let clock = Clock::new(70_000);
    let options = CultMeshRudpDocumentServerOptions {
        max_remembered_write_bytes: 65,
        session_max_lifetime: Duration::from_millis(100),
        ..Default::default()
    };
    let sink = Sink::default();
    let mut server = server(options, clock.clone(), sink.clone(), Source::default())?;
    let mut first = client(server.local_addr()?, 242)?;
    connect(&mut server, &mut [&mut first])?;
    send_reliable(&mut first, &receipt_hello())?;
    send_reliable(&mut first, &put("a", "note:a", vec![1]))?;
    assert_eq!(frames_after(&mut server, &mut first, 2)?[1], admitted("a", "note:a"), "after the server's hello");

    clock.set_monotonic(70_050);
    let mut second = client(server.local_addr()?, 243)?;
    connect(&mut server, &mut [&mut second])?;
    send_reliable(&mut second, &receipt_hello())?;
    send_reliable(&mut second, &put("b", "note:b", vec![1]))?;
    assert_eq!(refusal_of(&frames_after(&mut server, &mut second, 2)?[1]).0, Some(CultNetErrorCode::LimitReached));

    clock.set_monotonic(70_101);
    assert_eq!(server.maintain()?.sessions_expired, 1);
    send_reliable(&mut second, &put("c", "note:c", vec![1]))?;
    assert_eq!(frames_after(&mut server, &mut second, 1)?, vec![admitted("c", "note:c")], "the ended session's entry was freed");
    clock.set_monotonic(70_152);
    assert_eq!(server.maintain()?.sessions_expired, 1);
    assert_eq!(server.session_count(), 0);
    let mut third = client(server.local_addr()?, 244)?;
    connect(&mut server, &mut [&mut third])?;
    send_reliable(&mut third, &receipt_hello())?;
    send_reliable(&mut third, &put("d", "note:d", vec![1]))?;
    assert_eq!(frames_after(&mut server, &mut third, 2)?[1], admitted("d", "note:d"), "and so was the second's");
    Ok(())
}

/// Whichever way a session ends, its remembered writes go back to the budget: here the peer says goodbye, and
/// here the server ends it itself (an unreliable write whose refusal the reliable queue cannot take).
#[test]
fn a_disconnected_or_server_ended_session_frees_its_remembered_writes() -> Result<()> {
    let options = CultMeshRudpDocumentServerOptions {
        max_remembered_write_bytes: 65,
        max_pending_reliable_packets_per_session: 3,
        ..Default::default()
    };
    let mut server = server(options, Clock::new(70_000), Sink::default(), Source::default())?;
    let mut first = client(server.local_addr()?, 245)?;
    connect(&mut server, &mut [&mut first])?;
    send_reliable(&mut first, &receipt_hello())?;
    send_reliable(&mut first, &put("a", "note:a", vec![1]))?;
    assert_eq!(frames_after(&mut server, &mut first, 2)?[1], admitted("a", "note:a"));
    first.disconnect(Vec::new())?;
    for _ in 0..20 {
        server.poll_once()?;
    }
    assert_eq!(server.session_count(), 0, "the goodbye ended the session");

    // The budget holds one entry. A raw peer is admitted into it, then ended by the server.
    let mut second = RawPeer::connect(&mut server, 246)?;
    second.send(&receipt_hello())?;
    second.send(&put("b", "note:b", vec![1]))?;
    server.poll_once()?;
    for index in 0..8 {
        let payload = encode_cultnet_message_to_vec(&put(&format!("u{index}"), "note:1", vec![1]), CultNetWireContract::CultNetSchemaV0)?;
        let packets = second.session.send_many(
            "schema",
            payload,
            cultnet_rs::CultNetRudpSendOptions { reliable: false, ordered: false, sequenced: false, now_ms: 3, reliable_expire_after_ms: None },
            Some(1200),
        )?;
        for packet in &packets {
            second.send_packet(packet)?;
        }
        server.poll_once()?;
    }
    assert_eq!(server.session_count(), 0, "the server ended the session");

    let mut third = client(server.local_addr()?, 247)?;
    connect(&mut server, &mut [&mut third])?;
    send_reliable(&mut third, &receipt_hello())?;
    send_reliable(&mut third, &put("c", "note:c", vec![1]))?;
    assert_eq!(frames_after(&mut server, &mut third, 2)?[1], admitted("c", "note:c"), "both ended sessions' entries were freed");
    Ok(())
}

/// Receipts need a channel that is reliable and not sequenced: a reliable-sequenced write is refused like an
/// unreliable one, a reliable-unordered write is admitted, and a legacy session's sequenced write is left alone.
#[test]
fn only_reliable_non_sequenced_writes_are_accepted_on_a_receipt_session() -> Result<()> {
    let sink = Sink::default();
    let mut server = server(Default::default(), Clock::new(70_000), sink.clone(), Source::default())?;
    let mut peer = RawPeer::connect(&mut server, 248)?;
    peer.send(&receipt_hello())?;
    server.poll_once()?;
    let send_with = |peer: &mut RawPeer, server: &mut Server, id: &str, ordered: bool, sequenced: bool| -> Result<Vec<CultNetMessage>> {
        let payload = encode_cultnet_message_to_vec(&put(id, "note:1", vec![1]), CultNetWireContract::CultNetSchemaV0)?;
        let packets = peer.session.send_many(
            "schema",
            payload,
            cultnet_rs::CultNetRudpSendOptions { reliable: true, ordered, sequenced, now_ms: 3, reliable_expire_after_ms: None },
            Some(1200),
        )?;
        for packet in &packets {
            peer.send_packet(packet)?;
        }
        server.poll_once()?;
        let mut answers = Vec::new();
        for packet in peer.drain() {
            for frame in peer.session.receive(&packet, 4)?.delivered {
                answers.push(decode_cultnet_message_from_slice(&frame.payload, CultNetWireContract::CultNetSchemaV0)?);
            }
        }
        Ok(answers)
    };
    let sequenced = send_with(&mut peer, &mut server, "s1", false, true)?;
    let refusals: Vec<_> = sequenced.iter().filter(|a| matches!(a, CultNetMessage::ErrorV1 { .. })).collect();
    assert_eq!(refusals.len(), 1, "the sequenced write is answered: {sequenced:?}");
    assert_eq!(refusal_of(refusals[0]), (Some(CultNetErrorCode::MessageInvalid), Some("s1".into())));
    assert_eq!(
        error_text_and_field(refusals[0]),
        ("message_invalid: receipt sessions require the reliable channel".into(), Some("channel".into()))
    );
    assert_eq!(sink.0.lock().unwrap().receipts.len(), 0, "nothing was applied");

    let unordered = send_with(&mut peer, &mut server, "s1", false, false)?;
    assert!(unordered.contains(&admitted("s1", "note:1")), "reliable unordered is accepted, and the id was not consumed: {unordered:?}");
    // A fresh peer: an ordered stream waits on the sequence numbers the refused packet used.
    let mut ordered_peer = RawPeer::connect(&mut server, 251)?;
    ordered_peer.send(&receipt_hello())?;
    server.poll_once()?;
    let ordered = send_with(&mut ordered_peer, &mut server, "o1", true, false)?;
    assert!(ordered.contains(&admitted("o1", "note:1")), "reliable ordered is accepted: {ordered:?}");
    assert_eq!(sink.0.lock().unwrap().receipts.len(), 2);

    let legacy_sink = Sink::default();
    let mut legacy_server: Server = CultMeshRudpDocumentServer::new(
        UdpSocket::bind("127.0.0.1:0")?,
        legacy_sink.clone(),
        Source::default(),
        Clock::new(70_000),
        Default::default(),
    )?;
    let mut legacy = RawPeer::connect(&mut legacy_server, 249)?;
    let none = send_with(&mut legacy, &mut legacy_server, "l1", false, true)?;
    assert!(none.is_empty(), "a legacy session is sent nothing: {none:?}");
    assert_eq!(legacy_sink.0.lock().unwrap().receipts.len(), 1, "and its reliable-sequenced write is applied as before");
    Ok(())
}

/// A Connect that does not repeat the accepted one is a new client session: it starts with nothing remembered, so
/// the id a receipt session already used is a new write on it, and the old session's entries leave the budget.
#[test]
fn a_reconnect_is_a_new_receipt_session_that_remembers_nothing_of_the_old() -> Result<()> {
    let options = CultMeshRudpDocumentServerOptions { max_remembered_write_bytes: 65, ..Default::default() };
    let sink = Sink::default();
    let mut server = server(options, Clock::new(70_000), sink.clone(), Source::default())?;
    let target = server.local_addr()?;
    let shared = UdpSocket::bind("127.0.0.1:0")?;
    let twin = shared.try_clone()?;
    let mut first = client_on(shared, target, 250, 50_000)?;
    connect(&mut server, &mut [&mut first])?;
    send_reliable(&mut first, &receipt_hello())?;
    send_reliable(&mut first, &put("m1", "note:a", vec![1]))?;
    assert_eq!(frames_after(&mut server, &mut first, 2)?[1], admitted("m1", "note:a"));

    let mut restarted = client_on(twin, target, 250, 7)?;
    connect(&mut server, &mut [&mut restarted])?;
    assert_eq!(server.session_count(), 1);
    send_reliable(&mut restarted, &receipt_hello())?;
    send_reliable(&mut restarted, &put("m1", "note:b", vec![2]))?;
    let answers = frames_after(&mut server, &mut restarted, 2)?;
    assert_eq!(answers[1], admitted("m1", "note:b"), "same id, other content: a new write on a new session, within a one-entry budget");
    assert_eq!(sink.0.lock().unwrap().receipts.len(), 2);
    Ok(())
}

/// A session that never declared the capability gets no refusal for an unreliable write: it is dropped as before.
#[test]
fn an_unreliable_write_on_a_legacy_session_is_dropped_silently() -> Result<()> {
    let sink = Sink::default();
    let mut server = server(Default::default(), Clock::new(70_000), sink.clone(), Source::default())?;
    let mut peer = RawPeer::connect(&mut server, 235)?;
    let payload = encode_cultnet_message_to_vec(&put("u1", "note:1", vec![1]), CultNetWireContract::CultNetSchemaV0)?;
    let packets = peer.session.send_many(
        "schema",
        payload,
        cultnet_rs::CultNetRudpSendOptions { reliable: false, ordered: false, sequenced: false, now_ms: 3, reliable_expire_after_ms: None },
        Some(1200),
    )?;
    for packet in &packets {
        peer.send_packet(packet)?;
    }
    server.poll_once()?;
    assert!(peer.drain().is_empty(), "a legacy session is sent nothing");
    assert_eq!(sink.0.lock().unwrap().receipts.len(), 0);
    Ok(())
}

/// A put or delete whose document or schemaId is null or missing is refused `message_invalid` naming that
/// field (document first), with the spec's fixed text and the write's id; the session goes on.
#[test]
fn a_write_missing_its_document_or_schema_id_is_refused_naming_the_field() -> Result<()> {
    let sink = Sink::default();
    let mut server = server(Default::default(), Clock::new(70_000), sink.clone(), Source::default())?;
    let mut peer = client(server.local_addr()?, 236)?;
    connect(&mut server, &mut [&mut peer])?;
    send_reliable(&mut peer, &receipt_hello())?;
    let frames = [
        ("p-missing", serde_json::json!({"schemaVersion": "cultnet.document_put_raw.v0", "messageId": "p-missing"}), "document"),
        ("p-null", serde_json::json!({"schemaVersion": "cultnet.document_put_raw.v0", "messageId": "p-null", "document": null}), "document"),
        ("p-noschema", serde_json::json!({"schemaVersion": "cultnet.document_put_raw.v0", "messageId": "p-noschema",
            "document": {"recordKey": "k", "storedAt": "t", "payloadEncoding": "messagepack", "payload": [1]}}), "schemaId"),
        ("d-null", serde_json::json!({"schemaVersion": "cultnet.document_delete.v0", "messageId": "d-null", "schemaId": null, "recordKey": "k"}), "schemaId"),
        ("d-missing", serde_json::json!({"schemaVersion": "cultnet.document_delete.v0", "messageId": "d-missing", "recordKey": "k"}), "schemaId"),
    ];
    for (_, frame, _) in &frames {
        peer.send_reliable("schema", rmp_serde::to_vec_named(frame)?)?;
    }
    let answers = answers_after(&mut server, &mut peer, frames.len())?;
    for ((id, _, field), answer) in frames.iter().zip(&answers) {
        assert_eq!(refusal_of(answer), (Some(CultNetErrorCode::MessageInvalid), Some((*id).into())));
        assert_eq!(
            error_text_and_field(answer),
            (format!("message_invalid: {field} is required"), Some((*field).into()))
        );
    }
    assert_eq!(sink.0.lock().unwrap().receipts.len(), 0);
    assert_eq!(server.session_count(), 1);
    Ok(())
}

/// An unreliable write whose refusal the full reliable queue cannot take ends the session as a local failure
/// (the peer's write is then unconfirmed, never silent and never "refused a packet").
#[test]
fn an_unreliable_refusal_that_cannot_be_queued_ends_the_session_as_a_local_failure() -> Result<()> {
    let options = CultMeshRudpDocumentServerOptions { max_pending_reliable_packets_per_session: 3, ..Default::default() };
    let mut server = server(options, Clock::new(70_000), Sink::default(), Source::default())?;
    let mut peer = RawPeer::connect(&mut server, 237)?;
    peer.send(&receipt_hello())?;
    server.poll_once()?;
    // The peer acknowledges nothing, so each refusal stays in the server's reliable queue.
    for index in 0..8 {
        let payload = encode_cultnet_message_to_vec(&put(&format!("u{index}"), "note:1", vec![1]), CultNetWireContract::CultNetSchemaV0)?;
        let packets = peer.session.send_many(
            "schema",
            payload,
            cultnet_rs::CultNetRudpSendOptions { reliable: false, ordered: false, sequenced: false, now_ms: 3, reliable_expire_after_ms: None },
            Some(1200),
        )?;
        for packet in &packets {
            peer.send_packet(packet)?;
        }
        server.poll_once()?;
    }
    assert_eq!(server.session_count(), 0, "the refusal the queue could not take ended the session");
    let goodbye = peer.drain().into_iter().find(is_goodbye).expect("the peer is told the session ended");
    assert_eq!(goodbye.payload, b"server could not answer a write".to_vec());
    Ok(())
}
