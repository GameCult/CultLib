use anyhow::{Result, anyhow};
use cultnet_rs::{
    CultNetMessage, CultNetRawDocumentRecord, CultNetRudpPacket, CultNetRudpPacketType,
    CultNetRudpSendOptions, CultNetRudpSession, CultNetRudpSessionOptions, CultNetWireContract,
    decode_cultnet_message_from_slice, decode_rudp_packet, encode_cultnet_message_to_vec,
    encode_rudp_packet,
};
use std::collections::BTreeMap;
use std::io::ErrorKind;
use std::net::{SocketAddr, UdpSocket};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

const MAX_UDP_DATAGRAM_BYTES: usize = 65_535;
/// The most fragments one RUDP message can carry: its fragment count is a u16.
const MAX_FRAGMENTS_PER_MESSAGE: usize = u16::MAX as usize;
/// The shortest message id CultNet encodes: one character. A snapshot request
/// can carry no shorter id, so no response to it can be smaller.
const SHORTEST_MESSAGE_ID: &str = "0";

/// The transport identity of one remote CultNet RUDP session.
///
/// Connection identifiers are scoped to a remote socket address. Two clients
/// may therefore use the same connection identifier without sharing ordering,
/// acknowledgement, resend, or fragment state.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct CultMeshRudpSessionKey {
    pub remote_addr: SocketAddr,
    pub connection_id: u32,
}

/// A raw document plus the receipt fact owned by the receiving process.
///
/// `document.payload` is the exact byte vector decoded from the CultNet raw
/// record. The trusted receipt time lives beside the sender-owned record so the
/// transport never rewrites its metadata or payload.
#[derive(Clone, Debug, PartialEq)]
pub struct CultMeshRudpRawDocumentReceipt {
    pub session: CultMeshRudpSessionKey,
    pub message_id: String,
    pub transport_sequence: u32,
    pub received_at_unix_millis: u64,
    pub document: CultNetRawDocumentRecord,
}

/// The caller-visible query for a read-only raw snapshot.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CultMeshRudpSnapshotQuery {
    pub session: CultMeshRudpSessionKey,
    pub message_id: String,
    pub requested_at_unix_millis: u64,
    pub schema_ids: Option<Vec<String>>,
    pub record_keys: Option<Vec<String>>,
}

/// Caller-owned admission/persistence port for received raw documents.
pub trait CultMeshRudpRawDocumentSink {
    fn accept_raw_document(&mut self, receipt: CultMeshRudpRawDocumentReceipt) -> Result<()>;
}

impl<F> CultMeshRudpRawDocumentSink for F
where
    F: FnMut(CultMeshRudpRawDocumentReceipt) -> Result<()>,
{
    fn accept_raw_document(&mut self, receipt: CultMeshRudpRawDocumentReceipt) -> Result<()> {
        self(receipt)
    }
}

/// Caller-owned catalog port for serving raw snapshot requests.
///
/// The caller decides which records the requester may see. CultMesh preserves
/// those records as raw CultNet documents and does not interpret their schemas.
pub trait CultMeshRudpSnapshotSource {
    fn raw_snapshot(
        &mut self,
        query: &CultMeshRudpSnapshotQuery,
    ) -> Result<Vec<CultNetRawDocumentRecord>>;

    /// The record this source would serve for a received put's `document`.
    ///
    /// The server admits a put only if a snapshot response carrying this record
    /// alone fits its limits, so a source that serves a different shape than it
    /// receives (dropping provenance, or adding its own) overrides this to keep
    /// admission exact. The default serves the record as received. An error
    /// refuses the put as `SnapshotSourceFailed`.
    fn served_record(
        &mut self,
        document: &CultNetRawDocumentRecord,
    ) -> Result<CultNetRawDocumentRecord> {
        Ok(document.clone())
    }
}

impl<F> CultMeshRudpSnapshotSource for F
where
    F: FnMut(&CultMeshRudpSnapshotQuery) -> Result<Vec<CultNetRawDocumentRecord>>,
{
    fn raw_snapshot(
        &mut self,
        query: &CultMeshRudpSnapshotQuery,
    ) -> Result<Vec<CultNetRawDocumentRecord>> {
        self(query)
    }
}

/// Trusted local clocks for receipt facts and transport maintenance.
///
/// Receipt time is wall time. Expiry and resend decisions use the monotonic
/// value, so a wall-clock correction cannot resurrect or pin a session.
pub trait CultMeshRudpServerClock {
    fn now_unix_millis(&self) -> u64;
    fn now_monotonic_millis(&self) -> u64;
}

#[derive(Clone, Debug)]
pub struct CultMeshSystemClock {
    monotonic_origin: Instant,
}

impl Default for CultMeshSystemClock {
    fn default() -> Self {
        Self {
            monotonic_origin: Instant::now(),
        }
    }
}

impl CultMeshRudpServerClock for CultMeshSystemClock {
    fn now_unix_millis(&self) -> u64 {
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_millis()
            .try_into()
            .unwrap_or(u64::MAX)
    }

    fn now_monotonic_millis(&self) -> u64 {
        self.monotonic_origin
            .elapsed()
            .as_millis()
            .try_into()
            .unwrap_or(u64::MAX)
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CultMeshRudpDocumentServerOptions {
    pub max_sessions: usize,
    pub session_idle_timeout: Duration,
    pub session_max_lifetime: Duration,
    /// Conservative payload admission budget across all live sessions.
    ///
    /// Admitted ingress and queued snapshot-response bytes are charged until
    /// the session ends even when CultNet releases them sooner. Together with
    /// CultNet's per-session receive caps, this makes retained payload memory
    /// finite without duplicating its fragment/ordering machinery here.
    pub max_admitted_payload_bytes: usize,
    pub max_admitted_payload_bytes_per_session: usize,
    /// The largest encoded snapshot response the server sends. It also bounds
    /// puts: a document whose snapshot response alone would exceed it, or would
    /// take more fragments than one message or the reliable queue can hold, is
    /// refused with `DocumentUnservable`, since no request could return it.
    pub max_snapshot_response_bytes: usize,
    pub max_snapshot_documents: usize,
    pub resend_delay: Duration,
    pub max_pending_reliable_packets_per_session: usize,
    pub max_fragment_bytes: usize,
}

impl Default for CultMeshRudpDocumentServerOptions {
    fn default() -> Self {
        Self {
            max_sessions: 64,
            session_idle_timeout: Duration::from_secs(30),
            session_max_lifetime: Duration::from_secs(15 * 60),
            max_admitted_payload_bytes: 32 * 1024 * 1024,
            max_admitted_payload_bytes_per_session: 4 * 1024 * 1024,
            max_snapshot_response_bytes: 1024 * 1024,
            max_snapshot_documents: 4096,
            resend_delay: Duration::from_millis(50),
            max_pending_reliable_packets_per_session: 1024,
            max_fragment_bytes: 1200,
        }
    }
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct CultMeshRudpMaintenance {
    pub sessions_expired: usize,
    pub packets_resent: usize,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CultMeshRudpApplicationOperation {
    DocumentPutRaw,
    SnapshotRequest,
}

/// Why the server refused an application message.
///
/// `Display` gives the operator-facing sentence; match on the variant to act on
/// the cause.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum CultMeshRudpRejectionReason {
    /// The caller's sink refused the document. Carries the sink's error text.
    SinkRefused(String),
    /// The document could never be served: a snapshot response carrying it
    /// alone, as the snapshot source would serve it (`served_record`) and under
    /// the shortest message id CultNet allows, would
    /// exceed `max_snapshot_response_bytes`, or would take more than
    /// `max_fragment_count` fragments of `max_fragment_bytes`: the lesser of
    /// 65535 and `max_pending_reliable_packets_per_session`. It was not offered
    /// to the sink.
    DocumentUnservable {
        response_bytes: usize,
        max_snapshot_response_bytes: usize,
        fragment_count: usize,
        max_fragment_count: usize,
    },
    /// The caller's snapshot source failed. Carries its error text.
    SnapshotSourceFailed(String),
    SnapshotTooManyDocuments {
        documents: usize,
        max_snapshot_documents: usize,
    },
    SnapshotResponseTooLarge {
        response_bytes: usize,
        max_snapshot_response_bytes: usize,
    },
    /// A snapshot response could not be encoded. Carries the encoder's error text.
    ResponseEncodingFailed(String),
    /// The server's retained payload budget cannot hold the response.
    PayloadBudgetFull,
    /// The session could not queue the response. Carries the session's error text.
    ResponseQueueFailed(String),
}

impl std::fmt::Display for CultMeshRudpRejectionReason {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::SinkRefused(error) | Self::SnapshotSourceFailed(error) => f.write_str(error),
            Self::DocumentUnservable {
                response_bytes,
                max_snapshot_response_bytes,
                fragment_count,
                max_fragment_count,
            } => write!(
                f,
                "document can never be served: its snapshot response is {response_bytes} bytes in {fragment_count} fragments; limits are {max_snapshot_response_bytes} bytes and {max_fragment_count} fragments"
            ),
            Self::SnapshotTooManyDocuments {
                documents,
                max_snapshot_documents,
            } => write!(
                f,
                "snapshot returned {documents} documents; limit is {max_snapshot_documents}"
            ),
            Self::SnapshotResponseTooLarge {
                response_bytes,
                max_snapshot_response_bytes,
            } => write!(
                f,
                "snapshot response is {response_bytes} bytes; limit is {max_snapshot_response_bytes}"
            ),
            Self::ResponseEncodingFailed(error) => {
                write!(f, "snapshot response could not be encoded: {error}")
            }
            Self::PayloadBudgetFull => f.write_str("retained payload budget is full"),
            Self::ResponseQueueFailed(error) => {
                write!(f, "snapshot response could not be queued: {error}")
            }
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CultMeshRudpApplicationRejection {
    pub session: CultMeshRudpSessionKey,
    pub operation: CultMeshRudpApplicationOperation,
    pub message_id: String,
    pub reason: CultMeshRudpRejectionReason,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum CultMeshRudpPollOutcome {
    Idle,
    Handled,
    ApplicationRejected(CultMeshRudpApplicationRejection),
}

struct SessionEntry {
    session: CultNetRudpSession,
    created_at_monotonic_millis: u64,
    last_activity_monotonic_millis: u64,
    admitted_payload_bytes: usize,
}

/// A synchronous, multi-session CultNet RUDP raw-document server.
///
/// The server owns exactly one UDP socket. Call `poll_once` from an existing
/// daemon loop; no executor or background thread is created here.
pub struct CultMeshRudpDocumentServer<S, Q, C> {
    socket: UdpSocket,
    sessions: BTreeMap<CultMeshRudpSessionKey, SessionEntry>,
    sink: S,
    snapshot_source: Q,
    clock: C,
    options: CultMeshRudpDocumentServerOptions,
    packets_dropped: u64,
}

impl<S, Q, C> CultMeshRudpDocumentServer<S, Q, C>
where
    S: CultMeshRudpRawDocumentSink,
    Q: CultMeshRudpSnapshotSource,
    C: CultMeshRudpServerClock,
{
    pub fn new(
        socket: UdpSocket,
        sink: S,
        snapshot_source: Q,
        clock: C,
        options: CultMeshRudpDocumentServerOptions,
    ) -> Result<Self> {
        validate_options(&options)?;
        socket.local_addr().map_err(|error| {
            anyhow!("CultMesh RUDP server requires a bound UDP socket: {error}")
        })?;
        socket.set_nonblocking(true)?;
        Ok(Self {
            socket,
            sessions: BTreeMap::new(),
            sink,
            snapshot_source,
            clock,
            options,
            packets_dropped: 0,
        })
    }

    /// Datagrams read and discarded because they belong to no admissible
    /// session: malformed frames, unknown sessions, a Connect the session cap
    /// refuses, a packet that fails its session. Never an
    /// error: a moved flow or a scanner must not end the daemon loop.
    pub fn packets_dropped(&self) -> u64 {
        self.packets_dropped
    }

    pub fn local_addr(&self) -> Result<SocketAddr> {
        Ok(self.socket.local_addr()?)
    }

    pub fn session_count(&self) -> usize {
        self.sessions.len()
    }

    /// Poll transport maintenance and receive at most one UDP datagram.
    ///
    /// Application rejection invalidates only the responsible peer session and
    /// is returned as data so a daemon can log it and keep serving. Local
    /// socket and server-state failures remain errors.
    pub fn poll_once(&mut self) -> Result<CultMeshRudpPollOutcome> {
        self.maintain()?;
        let mut wire = vec![0_u8; MAX_UDP_DATAGRAM_BYTES];
        let (received, remote_addr) = match self.socket.recv_from(&mut wire) {
            Ok(value) => value,
            Err(error) if error.kind() == ErrorKind::WouldBlock => {
                return Ok(CultMeshRudpPollOutcome::Idle);
            }
            Err(error) => return Err(error.into()),
        };
        wire.truncate(received);

        let packet = match decode_rudp_packet(&wire) {
            Ok(packet) => packet,
            Err(_) => {
                self.packets_dropped += 1;
                return Ok(CultMeshRudpPollOutcome::Handled);
            }
        };
        let now_unix = self.clock.now_unix_millis();
        let now_monotonic = self.clock.now_monotonic_millis();
        let key = CultMeshRudpSessionKey {
            remote_addr,
            connection_id: packet.connection_id,
        };

        if packet.packet_type == CultNetRudpPacketType::Connect {
            if !self.accept_connection(key, &packet, now_monotonic)? {
                self.packets_dropped += 1;
            }
            return Ok(CultMeshRudpPollOutcome::Handled);
        }

        if !self.sessions.contains_key(&key) {
            self.packets_dropped += 1;
            return Ok(CultMeshRudpPollOutcome::Handled);
        }
        if packet.packet_type == CultNetRudpPacketType::Data
            && packet.channel_id == "schema"
            && !packet.reliable
        {
            return Ok(CultMeshRudpPollOutcome::Handled);
        }
        let payload_charge = if packet.packet_type == CultNetRudpPacketType::Data {
            packet.payload.len()
        } else {
            0
        };
        if !self.payload_budget_allows(key, payload_charge) {
            return Ok(CultMeshRudpPollOutcome::Handled);
        }

        let result = {
            let entry = self
                .sessions
                .get_mut(&key)
                .expect("checked session must remain present");
            entry.last_activity_monotonic_millis = now_monotonic;
            entry.admitted_payload_bytes =
                entry.admitted_payload_bytes.saturating_add(payload_charge);
            entry.session.receive(&packet, now_monotonic)
        };
        let result = match result {
            Ok(result) => result,
            Err(_) => {
                self.end_refused_session(key)?;
                self.packets_dropped += 1;
                return Ok(CultMeshRudpPollOutcome::Handled);
            }
        };

        if let Some(reply) = result.reply {
            self.send_packet(key.remote_addr, &reply)?;
        }

        if result.disconnected {
            self.sessions.remove(&key);
            return Ok(CultMeshRudpPollOutcome::Handled);
        }

        for frame in result.delivered {
            if frame.channel_id != "schema" {
                continue;
            }
            let message = match decode_cultnet_message_from_slice(
                &frame.payload,
                CultNetWireContract::CultNetSchemaV0,
            ) {
                Ok(message) => message,
                Err(_) => continue,
            };
            if let Some(rejection) = self.deliver_application_message(
                key,
                frame.sequence,
                message,
                now_unix,
                now_monotonic,
            )? {
                self.sessions.remove(&key);
                return Ok(CultMeshRudpPollOutcome::ApplicationRejected(rejection));
            }
        }

        if packet.reliable {
            let ack = self
                .sessions
                .get_mut(&key)
                .ok_or_else(|| anyhow!("CultMesh RUDP session disappeared before ACK"))?
                .session
                .create_ack();
            self.send_packet(key.remote_addr, &ack)?;
        }
        Ok(CultMeshRudpPollOutcome::Handled)
    }

    /// Expire idle sessions and resend reliable packets whose deadline passed.
    pub fn maintain(&mut self) -> Result<CultMeshRudpMaintenance> {
        let now = self.clock.now_monotonic_millis();
        let idle_timeout_ms = duration_millis(self.options.session_idle_timeout);
        let lifetime_ms = duration_millis(self.options.session_max_lifetime);
        let before = self.sessions.len();
        self.sessions.retain(|_, entry| {
            now.saturating_sub(entry.last_activity_monotonic_millis) <= idle_timeout_ms
                && now.saturating_sub(entry.created_at_monotonic_millis) <= lifetime_ms
        });
        let expired = before - self.sessions.len();

        let mut resends = Vec::new();
        for (key, entry) in &mut self.sessions {
            for packet in entry.session.due_resends(now) {
                resends.push((key.remote_addr, packet));
            }
        }
        for (remote_addr, packet) in &resends {
            self.send_packet(*remote_addr, packet)?;
        }
        Ok(CultMeshRudpMaintenance {
            sessions_expired: expired,
            packets_resent: resends.len(),
        })
    }

    /// `Ok(false)` when the Connect was refused because the session cap is full;
    /// only a socket failure is an `Err`.
    fn accept_connection(
        &mut self,
        key: CultMeshRudpSessionKey,
        packet: &CultNetRudpPacket,
        now: u64,
    ) -> Result<bool> {
        // The session decides whether a Connect repeats the one it accepted. Any
        // other Connect from this key is a new client session: the old one ends
        // with its budget, and the new one starts fresh.
        if self
            .sessions
            .get(&key)
            .is_some_and(|entry| !entry.session.connect_repeats(packet))
        {
            self.sessions.remove(&key);
        }
        let reply = match self.sessions.get_mut(&key) {
            Some(entry) => {
                entry.last_activity_monotonic_millis = now;
                match entry.session.accept_connect(packet, now, Vec::new()) {
                    Ok(reply) => reply,
                    Err(_) => {
                        self.end_refused_session(key)?;
                        return Ok(false);
                    }
                }
            }
            None => {
                if self.sessions.len() >= self.options.max_sessions {
                    return Ok(false);
                }
                let mut session = CultNetRudpSession::new(CultNetRudpSessionOptions {
                    connection_id: key.connection_id,
                    initial_sequence: None,
                    resend_delay_ms: duration_millis(self.options.resend_delay),
                    max_pending_reliable_packets: Some(
                        self.options.max_pending_reliable_packets_per_session,
                    ),
                });
                let accept = session.accept_connect(packet, now, Vec::new())?;
                self.sessions.insert(
                    key,
                    SessionEntry {
                        session,
                        created_at_monotonic_millis: now,
                        last_activity_monotonic_millis: now,
                        admitted_payload_bytes: 0,
                    },
                );
                accept
            }
        };
        self.send_packet(key.remote_addr, &reply)?;
        Ok(true)
    }

    fn deliver_application_message(
        &mut self,
        key: CultMeshRudpSessionKey,
        transport_sequence: u32,
        message: CultNetMessage,
        now_unix: u64,
        now: u64,
    ) -> Result<Option<CultMeshRudpApplicationRejection>> {
        match message {
            CultNetMessage::DocumentPutRaw {
                message_id,
                document,
            } => {
                let reject = |reason| {
                    Ok(Some(CultMeshRudpApplicationRejection {
                        session: key,
                        operation: CultMeshRudpApplicationOperation::DocumentPutRaw,
                        message_id: message_id.clone(),
                        reason,
                    }))
                };
                // Admit only what some snapshot request can return: the smallest
                // response that could carry this document is the record the snapshot
                // source would serve for it, alone, under the shortest message id
                // CultNet encodes, sized by the snapshot path's encoder and fragmented
                // as the snapshot path sends it.
                let served = match self.snapshot_source.served_record(&document) {
                    Ok(served) => served,
                    Err(error) => {
                        return reject(CultMeshRudpRejectionReason::SnapshotSourceFailed(
                            format!("{error:#}"),
                        ));
                    }
                };
                let alone = CultNetMessage::SnapshotResponseRaw {
                    message_id: SHORTEST_MESSAGE_ID.into(),
                    documents: vec![served],
                };
                let response_bytes = match encode_snapshot_response(&alone) {
                    Ok(payload) => payload.len(),
                    Err(error) => {
                        return reject(CultMeshRudpRejectionReason::ResponseEncodingFailed(error));
                    }
                };
                let fragment_count = self.fragments_for(response_bytes);
                let max_fragment_count = self.max_fragments_per_response();
                if response_bytes > self.options.max_snapshot_response_bytes
                    || fragment_count > max_fragment_count
                {
                    return reject(CultMeshRudpRejectionReason::DocumentUnservable {
                        response_bytes,
                        max_snapshot_response_bytes: self.options.max_snapshot_response_bytes,
                        fragment_count,
                        max_fragment_count,
                    });
                }
                let receipt = CultMeshRudpRawDocumentReceipt {
                    session: key,
                    message_id: message_id.clone(),
                    transport_sequence,
                    received_at_unix_millis: now_unix,
                    document,
                };
                if let Err(error) = self.sink.accept_raw_document(receipt) {
                    return reject(CultMeshRudpRejectionReason::SinkRefused(format!(
                        "{error:#}"
                    )));
                }
            }
            CultNetMessage::SnapshotRequest {
                message_id,
                schema_ids,
                record_keys,
            } => {
                let query = CultMeshRudpSnapshotQuery {
                    session: key,
                    message_id: message_id.clone(),
                    requested_at_unix_millis: now_unix,
                    schema_ids,
                    record_keys,
                };
                let documents = match self.snapshot_source.raw_snapshot(&query) {
                    Ok(documents) => documents,
                    Err(error) => {
                        return Ok(Some(CultMeshRudpApplicationRejection {
                            session: key,
                            operation: CultMeshRudpApplicationOperation::SnapshotRequest,
                            message_id,
                            reason: CultMeshRudpRejectionReason::SnapshotSourceFailed(format!(
                                "{error:#}"
                            )),
                        }));
                    }
                };
                if documents.len() > self.options.max_snapshot_documents {
                    return Ok(Some(CultMeshRudpApplicationRejection {
                        session: key,
                        operation: CultMeshRudpApplicationOperation::SnapshotRequest,
                        message_id,
                        reason: CultMeshRudpRejectionReason::SnapshotTooManyDocuments {
                            documents: documents.len(),
                            max_snapshot_documents: self.options.max_snapshot_documents,
                        },
                    }));
                }
                let response = CultNetMessage::SnapshotResponseRaw {
                    message_id: message_id.clone(),
                    documents,
                };
                let payload = match encode_snapshot_response(&response) {
                    Ok(payload) => payload,
                    Err(error) => {
                        return Ok(Some(CultMeshRudpApplicationRejection {
                            session: key,
                            operation: CultMeshRudpApplicationOperation::SnapshotRequest,
                            message_id,
                            reason: CultMeshRudpRejectionReason::ResponseEncodingFailed(error),
                        }));
                    }
                };
                if payload.len() > self.options.max_snapshot_response_bytes {
                    return Ok(Some(CultMeshRudpApplicationRejection {
                        session: key,
                        operation: CultMeshRudpApplicationOperation::SnapshotRequest,
                        message_id,
                        reason: CultMeshRudpRejectionReason::SnapshotResponseTooLarge {
                            response_bytes: payload.len(),
                            max_snapshot_response_bytes: self.options.max_snapshot_response_bytes,
                        },
                    }));
                }
                if !self.payload_budget_allows(key, payload.len()) {
                    return Ok(Some(CultMeshRudpApplicationRejection {
                        session: key,
                        operation: CultMeshRudpApplicationOperation::SnapshotRequest,
                        message_id,
                        reason: CultMeshRudpRejectionReason::PayloadBudgetFull,
                    }));
                }
                let payload_bytes = payload.len();
                let entry = self
                    .sessions
                    .get_mut(&key)
                    .ok_or_else(|| anyhow!("CultMesh RUDP session disappeared before response"))?;
                let packets = match entry.session.send_many(
                    "schema",
                    payload,
                    CultNetRudpSendOptions {
                        reliable: true,
                        ordered: true,
                        sequenced: false,
                        now_ms: now,
                        reliable_expire_after_ms: None,
                    },
                    Some(self.options.max_fragment_bytes),
                ) {
                    Ok(packets) => packets,
                    Err(error) => {
                        return Ok(Some(CultMeshRudpApplicationRejection {
                            session: key,
                            operation: CultMeshRudpApplicationOperation::SnapshotRequest,
                            message_id,
                            reason: CultMeshRudpRejectionReason::ResponseQueueFailed(format!(
                                "{error:#}"
                            )),
                        }));
                    }
                };
                entry.admitted_payload_bytes =
                    entry.admitted_payload_bytes.saturating_add(payload_bytes);
                for packet in &packets {
                    self.send_packet(key.remote_addr, packet)?;
                }
            }
            _ => {}
        }
        Ok(None)
    }

    /// The session refused a packet: it ends, and the client is told so it does
    /// not wait on a session the server no longer holds.
    fn end_refused_session(&mut self, key: CultMeshRudpSessionKey) -> Result<()> {
        let Some(mut entry) = self.sessions.remove(&key) else {
            return Ok(());
        };
        let goodbye = entry.session.end_refused_session();
        self.send_packet(key.remote_addr, &goodbye)
    }

    /// The fragments `send_many` splits a response of `bytes` into.
    fn fragments_for(&self, bytes: usize) -> usize {
        bytes.div_ceil(self.options.max_fragment_bytes).max(1)
    }

    /// The most fragments one response can take: one message's fragment count,
    /// and the reliable queue it must fit into whole.
    fn max_fragments_per_response(&self) -> usize {
        MAX_FRAGMENTS_PER_MESSAGE.min(self.options.max_pending_reliable_packets_per_session)
    }

    fn payload_budget_allows(&self, key: CultMeshRudpSessionKey, bytes: usize) -> bool {
        let Some(session_bytes) = self
            .sessions
            .get(&key)
            .map(|entry| entry.admitted_payload_bytes)
        else {
            return false;
        };
        if session_bytes
            .checked_add(bytes)
            .is_none_or(|total| total > self.options.max_admitted_payload_bytes_per_session)
        {
            return false;
        }
        self.sessions
            .values()
            .try_fold(0_usize, |total, entry| {
                total.checked_add(entry.admitted_payload_bytes)
            })
            .and_then(|total| total.checked_add(bytes))
            .is_some_and(|total| total <= self.options.max_admitted_payload_bytes)
    }

    fn send_packet(&mut self, remote_addr: SocketAddr, packet: &CultNetRudpPacket) -> Result<()> {
        let wire = encode_rudp_packet(packet)?;
        self.socket.send_to(&wire, remote_addr)?;
        Ok(())
    }
}

fn validate_options(options: &CultMeshRudpDocumentServerOptions) -> Result<()> {
    if options.max_sessions == 0 {
        return Err(anyhow!("max_sessions must be greater than zero"));
    }
    if options.session_idle_timeout.is_zero() {
        return Err(anyhow!("session_idle_timeout must be greater than zero"));
    }
    if options.session_max_lifetime.is_zero() {
        return Err(anyhow!("session_max_lifetime must be greater than zero"));
    }
    if options.max_admitted_payload_bytes == 0 {
        return Err(anyhow!(
            "max_admitted_payload_bytes must be greater than zero"
        ));
    }
    if options.max_admitted_payload_bytes_per_session == 0
        || options.max_admitted_payload_bytes_per_session > options.max_admitted_payload_bytes
    {
        return Err(anyhow!(
            "per-session admitted payload limit must be non-zero and no larger than the server limit"
        ));
    }
    if options.max_snapshot_response_bytes == 0
        || options.max_snapshot_response_bytes > options.max_admitted_payload_bytes_per_session
    {
        return Err(anyhow!(
            "snapshot response limit must be non-zero and fit in one session's payload budget"
        ));
    }
    if options.max_snapshot_documents == 0 {
        return Err(anyhow!("max_snapshot_documents must be greater than zero"));
    }
    if options.resend_delay.is_zero() {
        return Err(anyhow!("resend_delay must be greater than zero"));
    }
    if options.max_pending_reliable_packets_per_session == 0 {
        return Err(anyhow!(
            "max_pending_reliable_packets_per_session must be greater than zero"
        ));
    }
    if options.max_fragment_bytes == 0 {
        return Err(anyhow!("max_fragment_bytes must be greater than zero"));
    }
    // The limits must leave room for the smallest response the server sends, an
    // empty snapshot: a server that could answer nothing is misconfigured.
    let empty = encode_snapshot_response(&CultNetMessage::SnapshotResponseRaw {
        message_id: SHORTEST_MESSAGE_ID.into(),
        documents: Vec::new(),
    })
    .map_err(|error| anyhow!(error))?
    .len();
    if empty > options.max_snapshot_response_bytes {
        return Err(anyhow!(
            "max_snapshot_response_bytes cannot hold an empty snapshot response"
        ));
    }
    let max_fragments =
        MAX_FRAGMENTS_PER_MESSAGE.min(options.max_pending_reliable_packets_per_session);
    if empty.div_ceil(options.max_fragment_bytes) > max_fragments {
        return Err(anyhow!(
            "max_fragment_bytes and max_pending_reliable_packets_per_session cannot carry an empty snapshot response"
        ));
    }
    Ok(())
}


/// The one encoder for a snapshot response, shared by the snapshot path and by
/// put admission so the size a put is judged by is the size it would be served at.
fn encode_snapshot_response(response: &CultNetMessage) -> std::result::Result<Vec<u8>, String> {
    encode_cultnet_message_to_vec(response, CultNetWireContract::CultNetSchemaV0)
        .map_err(|error| format!("{error:#}"))
}

fn duration_millis(duration: Duration) -> u64 {
    duration.as_millis().try_into().unwrap_or(u64::MAX)
}
