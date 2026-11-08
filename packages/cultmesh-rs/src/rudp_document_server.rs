use anyhow::{Result, anyhow};
use cultnet_rs::{
    CultNetErrorCode, CultNetErrorDetails, CultNetMessage, CultNetRawDocumentRecord, CultNetRudpPacket, CultNetRudpPacketType,
    CultNetRudpSendOptions, CultNetRudpSession, CultNetRudpSessionOptions, CultNetWireContract,
    decode_cultnet_message_from_slice, decode_rudp_packet, encode_cultnet_message_to_vec,
    encode_rudp_packet, is_permanent_send_error, send_error_code,
};
use std::collections::BTreeMap;
#[cfg(test)]
use std::collections::BTreeSet;
use std::io::ErrorKind;
use std::net::{SocketAddr, UdpSocket};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

const MAX_UDP_DATAGRAM_BYTES: usize = 65_535;
/// The most fragments one RUDP message can carry: its fragment count is a u16.
const MAX_FRAGMENTS_PER_MESSAGE: usize = u16::MAX as usize;
/// The shortest message id CultNet encodes: one character. A snapshot request
/// can carry no shorter id, so no response to it can be smaller.
const SHORTEST_MESSAGE_ID: &str = "0";

/// How many consecutive refused frames of one kind a session may send before it is ended: undecodable
/// frames (reset by any frame that decodes) and refused deletes (reset by any decoded non-delete).
/// Fewer are answered with a typed refusal and the session is kept; the cap that reaches this number
/// is answered too, then the session ends. The same number is the cap in every runtime.
pub const MAX_CONSECUTIVE_REFUSED_FRAMES: u32 = 8;

/// How long a session whose capped frame was answered waits for the peer to acknowledge the answer
/// before it ends anyway.
const CAPPED_ANSWER_GRACE_MILLIS: u64 = 2_000;

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
    DocumentDelete,
    SnapshotRequest,
    /// A schema frame that did not decode as any CultNet message; there is no message id to name.
    Unreadable,
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
    /// A packet of the response can never be sent to the peer as built
    /// (`is_permanent_send_error`). Carries the failure's fixed name
    /// (`send_error_code`). The session has ended with a goodbye carrying the
    /// same name, and no refusal: the server knows the peer cannot be reached.
    ResponseSendFailed(&'static str),
    /// The server has no delete path, so every delete is refused. Nothing reached the sink.
    DeleteNotSupported,
    /// A schema frame did not decode as a CultNet message. Carries the decoder's error text.
    MessageUnreadable(String),
}

impl std::fmt::Display for CultMeshRudpRejectionReason {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::SinkRefused(error) | Self::SnapshotSourceFailed(error) => f.write_str(error),
            Self::DeleteNotSupported => f.write_str("the server does not take deletes"),
            Self::MessageUnreadable(error) => write!(f, "message could not be read: {error}"),
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
            Self::ResponseSendFailed(code) => {
                write!(f, "snapshot response could not be sent: {code}")
            }
        }
    }
}

/// The texts a refused peer is sent, one per rejection variant. They are fixed:
/// a sink's or source's error text can quote what a peer sent, so it stays with
/// the caller that receives the rejection and never reaches the wire.
pub(crate) const REFUSAL_TEXTS: [&str; 11] = [
    "the catalog refused the document",
    "the document can never be served",
    "the snapshot source failed",
    "the snapshot has too many documents",
    "the snapshot response is too large",
    "the snapshot response could not be encoded",
    "the retained payload budget is full",
    "the snapshot response could not be queued",
    "the snapshot response could not be sent",
    "the server does not take deletes",
    "the message could not be read",
];

impl CultMeshRudpRejectionReason {
    /// The fixed text the refused peer is sent for this variant.
    fn refusal_text(&self) -> &'static str {
        REFUSAL_TEXTS[match self {
            Self::SinkRefused(_) => 0,
            Self::DocumentUnservable { .. } => 1,
            Self::SnapshotSourceFailed(_) => 2,
            Self::SnapshotTooManyDocuments { .. } => 3,
            Self::SnapshotResponseTooLarge { .. } => 4,
            Self::ResponseEncodingFailed(_) => 5,
            Self::PayloadBudgetFull => 6,
            Self::ResponseQueueFailed(_) => 7,
            Self::ResponseSendFailed(_) => 8,
            Self::DeleteNotSupported => 9,
            Self::MessageUnreadable(_) => 10,
        }]
    }
}

/// The typed `write_not_authorized` refusal for a put whose record is `record_key`, as C#
/// `CultNetErrorMessage.ForRemoteWriteRefused` builds it: code, and `details` naming the record.
pub(crate) fn write_refused_message(record_key: &str, reason: &str) -> CultNetMessage {
    CultNetMessage::Error {
        error: format!("write_not_authorized: {reason}"),
        code: Some(CultNetErrorCode::WriteNotAuthorized),
        details: Some(CultNetErrorDetails {
            field: Some("recordKey".into()),
            value: Some(record_key.into()),
            as_of: None,
            current: None,
        }),
    }
}

/// The typed `message_invalid` refusal naming the request `field` at fault, as C#
/// `CultNetErrorMessage.ForMessageInvalid` builds it.
pub(crate) fn message_invalid_message(field: &str, reason: &str) -> CultNetMessage {
    CultNetMessage::Error {
        error: format!("message_invalid: {reason}"),
        code: Some(CultNetErrorCode::MessageInvalid),
        details: Some(CultNetErrorDetails {
            field: Some(field.into()),
            value: None,
            as_of: None,
            current: None,
        }),
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CultMeshRudpApplicationRejection {
    pub session: CultMeshRudpSessionKey,
    pub operation: CultMeshRudpApplicationOperation,
    pub message_id: String,
    /// The record a rejected put or delete concerned; `None` for a snapshot request or an unreadable frame.
    /// A sink or delete refusal names it to the peer.
    pub record_key: Option<String>,
    pub reason: CultMeshRudpRejectionReason,
}

impl CultMeshRudpApplicationRejection {
    /// What the refused peer is sent: a refused put or delete is a typed `write_not_authorized` naming
    /// the record, an unreadable frame a typed `message_invalid` naming `message`; every other rejection
    /// is the variant's fixed text, untyped. None carries the sink's, source's or decoder's own text.
    fn refusal_message(&self) -> CultNetMessage {
        let text = self.reason.refusal_text();
        match (&self.reason, &self.record_key) {
            (
                CultMeshRudpRejectionReason::SinkRefused(_)
                | CultMeshRudpRejectionReason::DeleteNotSupported,
                Some(record_key),
            ) => write_refused_message(record_key, text),
            (CultMeshRudpRejectionReason::MessageUnreadable(_), _) => {
                message_invalid_message("message", text)
            }
            _ => CultNetMessage::Error {
                error: text.into(),
                code: None,
                details: None,
            },
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum CultMeshRudpPollOutcome {
    Idle,
    Handled,
    ApplicationRejected(CultMeshRudpApplicationRejection),
}

/// Why a session ends before its peer is done with it.
enum SessionEnd {
    /// The session refused a packet.
    Refused,
    /// The server rejected a peer's message; the peer is sent this refusal.
    Rejected(CultNetMessage),
    /// The session owes its peer a packet that can never be sent as built.
    Unsendable(std::io::Error),
}

struct SessionEntry {
    session: CultNetRudpSession,
    created_at_monotonic_millis: u64,
    last_activity_monotonic_millis: u64,
    admitted_payload_bytes: usize,
    undecodable_frames_in_a_row: u32,
    refused_deletes_in_a_row: u32,
    /// Set once the peer's capped frame has been answered: the session ends when the answer is acknowledged,
    /// or at this time.
    ending_deadline_monotonic_millis: Option<u64>,
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
    send_failures: u64,
    /// Peers whose every datagram fails to send, standing in for an unroutable
    /// or full path that a loopback peer cannot be made to have.
    #[cfg(test)]
    failing_peers: BTreeSet<SocketAddr>,
    /// Peers whose next datagram after this many fails permanently, once.
    #[cfg(test)]
    unsendable_after: BTreeMap<SocketAddr, usize>,
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
            send_failures: 0,
            #[cfg(test)]
            failing_peers: BTreeSet::new(),
            #[cfg(test)]
            unsendable_after: BTreeMap::new(),
        })
    }

    /// Datagrams read and discarded because they belong to no admissible
    /// session: malformed frames, unknown sessions, a Connect the session cap
    /// refuses, a packet that fails its session. Never an
    /// error: a moved flow or a scanner must not end the daemon loop.
    pub fn packets_dropped(&self) -> u64 {
        self.packets_dropped
    }

    /// Datagrams that could not be sent to a peer and may yet be: no route, full
    /// buffers, a refused path. Each is that peer's lost datagram: a reliable
    /// packet stays pending and is resent, and the peer's session ends by idle
    /// timeout, lifetime, refusal or Disconnect, never by the failure. Never an
    /// error: one unreachable peer must not stop the poll that serves the
    /// others. A datagram that can never be sent as built
    /// (`is_permanent_send_error`) is not counted here: it ends that peer's
    /// session, and the peer's goodbye names the error.
    pub fn send_failures(&self) -> u64 {
        self.send_failures
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
    /// is returned as data so a daemon can log it and keep serving. The peer is
    /// sent the refusal as a `cultnet.error.v0` carrying fixed text for the
    /// rejection's variant, then a goodbye; neither acknowledges the refused
    /// message. The rejection's own detail, which can quote a sink's or
    /// source's error, stays with the caller. Local socket and server-state
    /// failures remain errors.
    pub fn poll_once(&mut self) -> Result<CultMeshRudpPollOutcome> {
        self.maintain()?;
        let mut wire = vec![0_u8; MAX_UDP_DATAGRAM_BYTES];
        let (received, remote_addr) = match self.socket.recv_from(&mut wire) {
            Ok(value) => value,
            // Windows reports an earlier ICMP port-unreachable on the next
            // receive; it names no datagram of this poll.
            Err(error)
                if matches!(
                    error.kind(),
                    ErrorKind::WouldBlock | ErrorKind::ConnectionReset
                ) =>
            {
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
                self.end_session(key, SessionEnd::Refused)?;
                self.packets_dropped += 1;
                return Ok(CultMeshRudpPollOutcome::Handled);
            }
        };

        let mut unsendable = match result.reply {
            Some(reply) => self.send_packet(key.remote_addr, &reply)?,
            None => None,
        };

        if result.disconnected {
            self.sessions.remove(&key);
            return Ok(CultMeshRudpPollOutcome::Handled);
        }

        let mut kept_rejection = None;
        for frame in result.delivered {
            if frame.channel_id != "schema" {
                continue;
            }
            // The capped frame was answered: nothing after it is served while the answer is delivered.
            if self
                .sessions
                .get(&key)
                .is_some_and(|entry| entry.ending_deadline_monotonic_millis.is_some())
            {
                break;
            }
            let decoded = decode_cultnet_message_from_slice(
                &frame.payload,
                CultNetWireContract::CultNetSchemaV0,
            );
            // Runs of refusals this frame extends or breaks. A session is kept through a short run and
            // ended by `MAX_CONSECUTIVE_REFUSED_FRAMES` in a row.
            let (is_unreadable, is_delete) = (
                decoded.is_err(),
                matches!(decoded, Ok(CultNetMessage::DocumentDelete { .. })),
            );
            let run = self.sessions.get_mut(&key).map(|entry| {
                entry.undecodable_frames_in_a_row = if is_unreadable {
                    entry.undecodable_frames_in_a_row + 1
                } else {
                    0
                };
                if is_delete {
                    entry.refused_deletes_in_a_row += 1;
                } else if !is_unreadable {
                    entry.refused_deletes_in_a_row = 0;
                }
                if is_unreadable {
                    entry.undecodable_frames_in_a_row
                } else {
                    entry.refused_deletes_in_a_row
                }
            });
            let rejection = match decoded {
                Ok(message) => self.deliver_application_message(
                    key,
                    frame.sequence,
                    message,
                    now_unix,
                    now_monotonic,
                )?,
                Err(error) => Some(CultMeshRudpApplicationRejection {
                    session: key,
                    operation: CultMeshRudpApplicationOperation::Unreadable,
                    message_id: String::new(),
                    record_key: None,
                    reason: CultMeshRudpRejectionReason::MessageUnreadable(format!("{error:#}")),
                }),
            };
            if let Some(rejection) = rejection {
                // A refused delete or unreadable frame below the cap is answered on the live session,
                // which goes on: the peer keeps the connection a stray frame did not break.
                if (is_unreadable || is_delete)
                    && run.is_some_and(|run| run < MAX_CONSECUTIVE_REFUSED_FRAMES)
                {
                    let refusal = rejection.refusal_message();
                    let packets = match self.sessions.remove(&key) {
                        Some(mut entry) => {
                            let packets = self.refusal_packets(&mut entry.session, &refusal, false);
                            self.sessions.insert(key, entry);
                            packets
                        }
                        None => Vec::new(),
                    };
                    for packet in &packets {
                        if let Some(error) = self.send_packet(key.remote_addr, packet)? {
                            unsendable = Some(error);
                            break;
                        }
                    }
                    kept_rejection = Some(rejection);
                    continue;
                }
                // The capped frame is answered reliably, so a lost datagram is resent; the session ends once
                // the peer has acknowledged the answer or the grace runs out (`end_answered_sessions`). When
                // the answer cannot be queued, the session ends at once with the refusal as before.
                if (is_unreadable || is_delete)
                    && run.is_some()
                    && !matches!(
                        rejection.reason,
                        CultMeshRudpRejectionReason::ResponseSendFailed(_)
                    )
                {
                    let refusal = rejection.refusal_message();
                    let packets = match self.sessions.remove(&key) {
                        Some(mut entry) => {
                            let packets = self.refusal_packets(&mut entry.session, &refusal, true);
                            if !packets.is_empty() {
                                entry.ending_deadline_monotonic_millis =
                                    Some(now_monotonic + CAPPED_ANSWER_GRACE_MILLIS);
                            }
                            self.sessions.insert(key, entry);
                            packets
                        }
                        None => Vec::new(),
                    };
                    if packets.is_empty() {
                        self.end_session(key, SessionEnd::Rejected(refusal))?;
                        return Ok(CultMeshRudpPollOutcome::ApplicationRejected(rejection));
                    }
                    for mut packet in packets {
                        // Acknowledges nothing, as every refusal: the refused frame was not admitted.
                        packet.ack = 0;
                        packet.ack_mask = 0;
                        if let Some(error) = self.send_packet(key.remote_addr, &packet)? {
                            unsendable = Some(error);
                            break;
                        }
                    }
                    kept_rejection = Some(rejection);
                    break;
                }
                // The only reply `receive` returns is a Pong, which delivers no
                // frame, so no earlier send of this poll can precede a rejection.
                // A response that could not be sent ended its session where the
                // send failed, over the real error.
                if !matches!(
                    rejection.reason,
                    CultMeshRudpRejectionReason::ResponseSendFailed(_)
                ) {
                    self.end_session(key, SessionEnd::Rejected(rejection.refusal_message()))?;
                }
                return Ok(CultMeshRudpPollOutcome::ApplicationRejected(rejection));
            }
        }

        let answering_capped_frame = self
            .sessions
            .get(&key)
            .is_some_and(|entry| entry.ending_deadline_monotonic_millis.is_some());
        if packet.reliable && unsendable.is_none() && !answering_capped_frame {
            let ack = self
                .sessions
                .get_mut(&key)
                .ok_or_else(|| anyhow!("CultMesh RUDP session disappeared before ACK"))?
                .session
                .create_ack();
            unsendable = self.send_packet(key.remote_addr, &ack)?;
        }
        if let Some(error) = unsendable {
            self.end_session(key, SessionEnd::Unsendable(error))?;
        }
        self.end_answered_sessions()?;
        Ok(match kept_rejection {
            Some(rejection) => CultMeshRudpPollOutcome::ApplicationRejected(rejection),
            None => CultMeshRudpPollOutcome::Handled,
        })
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
        self.end_answered_sessions()?;

        let mut resends = Vec::new();
        for (key, entry) in &mut self.sessions {
            for mut packet in entry.session.due_resends(now) {
                if entry.ending_deadline_monotonic_millis.is_some() {
                    // The resent answer to a capped frame acknowledges nothing either.
                    packet.ack = 0;
                    packet.ack_mask = 0;
                }
                resends.push((*key, packet));
            }
        }
        for (key, packet) in &resends {
            // A session ended earlier in this loop has nothing left to resend.
            if !self.sessions.contains_key(key) {
                continue;
            }
            if let Some(error) = self.send_packet(key.remote_addr, packet)? {
                self.end_session(*key, SessionEnd::Unsendable(error))?;
            }
        }
        Ok(CultMeshRudpMaintenance {
            sessions_expired: expired,
            packets_resent: resends.len(),
        })
    }

    /// Ends each session whose capped frame was answered once the peer has acknowledged every answer (a lost
    /// answer is resent first), or when the grace has run out.
    fn end_answered_sessions(&mut self) -> Result<()> {
        let now = self.clock.now_monotonic_millis();
        let due: Vec<CultMeshRudpSessionKey> = self
            .sessions
            .iter()
            .filter(|(_, entry)| {
                entry.ending_deadline_monotonic_millis.is_some_and(|deadline| {
                    entry.session.outstanding_reliable_packet_count() == 0 || now >= deadline
                })
            })
            .map(|(key, _)| *key)
            .collect();
        for key in due {
            self.end_session(key, SessionEnd::Refused)?;
        }
        Ok(())
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
                        self.end_session(key, SessionEnd::Refused)?;
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
                        undecodable_frames_in_a_row: 0,
                        refused_deletes_in_a_row: 0,
                        ending_deadline_monotonic_millis: None,
                    },
                );
                accept
            }
        };
        if let Some(error) = self.send_packet(key.remote_addr, &reply)? {
            self.end_session(key, SessionEnd::Unsendable(error))?;
            return Ok(false);
        }
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
                let record_key = document.record_key.clone();
                let reject = |reason| {
                    Ok(Some(CultMeshRudpApplicationRejection {
                        session: key,
                        operation: CultMeshRudpApplicationOperation::DocumentPutRaw,
                        message_id: message_id.clone(),
                        record_key: Some(record_key.clone()),
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
                            record_key: None,
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
                        record_key: None,
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
                            record_key: None,
                            reason: CultMeshRudpRejectionReason::ResponseEncodingFailed(error),
                        }));
                    }
                };
                if payload.len() > self.options.max_snapshot_response_bytes {
                    return Ok(Some(CultMeshRudpApplicationRejection {
                        session: key,
                        operation: CultMeshRudpApplicationOperation::SnapshotRequest,
                        message_id,
                        record_key: None,
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
                        record_key: None,
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
                            record_key: None,
                            reason: CultMeshRudpRejectionReason::ResponseQueueFailed(format!(
                                "{error:#}"
                            )),
                        }));
                    }
                };
                entry.admitted_payload_bytes =
                    entry.admitted_payload_bytes.saturating_add(payload_bytes);
                for packet in &packets {
                    if let Some(error) = self.send_packet(key.remote_addr, packet)? {
                        let code = send_error_code(&error);
                        self.end_session(key, SessionEnd::Unsendable(error))?;
                        return Ok(Some(CultMeshRudpApplicationRejection {
                            session: key,
                            operation: CultMeshRudpApplicationOperation::SnapshotRequest,
                            message_id,
                            record_key: None,
                            reason: CultMeshRudpRejectionReason::ResponseSendFailed(code),
                        }));
                    }
                }
            }
            CultNetMessage::DocumentDelete {
                message_id,
                record_key,
                ..
            } => {
                return Ok(Some(CultMeshRudpApplicationRejection {
                    session: key,
                    operation: CultMeshRudpApplicationOperation::DocumentDelete,
                    message_id,
                    record_key: Some(record_key),
                    reason: CultMeshRudpRejectionReason::DeleteNotSupported,
                }));
            }
            _ => {}
        }
        Ok(None)
    }

    /// Ends a session before its peer is done with it, and says goodbye. It is
    /// the one way a session ends early: every end removes the session, sends
    /// what the peer is owed, then the goodbye.
    ///
    /// A rejected message's refusal goes first, unreliable and unordered: the
    /// session ends with it, so it is never resent, and a gap in what the peer
    /// has received must not hold it back. It is built before the reset and
    /// carries acknowledgement fields built after it, which acknowledge
    /// nothing: a publisher reads an acknowledgement as admission. A refusal
    /// that can never be sent as built is not dropped: the goodbye names its
    /// error instead. A refusal that cannot be encoded is not sent.
    fn end_session(&mut self, key: CultMeshRudpSessionKey, end: SessionEnd) -> Result<()> {
        let Some(mut entry) = self.sessions.remove(&key) else {
            return Ok(());
        };
        let (refusal, mut unsendable) = match end {
            SessionEnd::Refused => (Vec::new(), None),
            SessionEnd::Rejected(refusal) => (self.refusal_packets(&mut entry.session, &refusal, false), None),
            SessionEnd::Unsendable(error) => (Vec::new(), Some(error)),
        };
        entry.session.reset_peer_state();
        let nothing = entry.session.create_ack();
        for mut packet in refusal {
            packet.ack = nothing.ack;
            packet.ack_mask = nothing.ack_mask;
            if let Some(error) = self.send_packet(key.remote_addr, &packet)? {
                unsendable = Some(error);
                break;
            }
        }
        let goodbye = match &unsendable {
            Some(error) => entry.session.end_unsendable_session(error),
            None => entry.session.end_refused_session(),
        };
        // The goodbye is the session's last datagram. If it too can never be
        // sent, nobody is left to tell.
        self.send_packet(key.remote_addr, &goodbye)?;
        Ok(())
    }

    /// The refusal a rejected peer is sent: unreliable and unordered, or (`reliable`) queued like any response.
    fn refusal_packets(
        &self,
        session: &mut CultNetRudpSession,
        refusal: &CultNetMessage,
        reliable: bool,
    ) -> Vec<CultNetRudpPacket> {
        encode_cultnet_message_to_vec(refusal, CultNetWireContract::CultNetSchemaV0)
            .and_then(|payload| {
                session.send_many(
                    "schema",
                    payload,
                    CultNetRudpSendOptions {
                        reliable,
                        ordered: reliable,
                        sequenced: false,
                        now_ms: self.clock.now_monotonic_millis(),
                        reliable_expire_after_ms: None,
                    },
                    Some(self.options.max_fragment_bytes),
                )
            })
            .unwrap_or_default()
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

    /// A transient send failure is that peer's lost datagram: counted, never an
    /// error. A permanent one (`is_permanent_send_error`) is returned as `Some`
    /// for the caller to end the session over. Only an encode failure, the
    /// server's own bug, is an error.
    fn send_packet(
        &mut self,
        remote_addr: SocketAddr,
        packet: &CultNetRudpPacket,
    ) -> Result<Option<std::io::Error>> {
        let wire = encode_rudp_packet(packet)?;
        match self.send_datagram(&wire, remote_addr) {
            Ok(_) => {}
            Err(error) if is_permanent_send_error(&error) => return Ok(Some(error)),
            Err(_) => self.send_failures += 1,
        }
        Ok(None)
    }

    fn send_datagram(&mut self, wire: &[u8], remote_addr: SocketAddr) -> std::io::Result<usize> {
        #[cfg(test)]
        {
            match self.unsendable_after.get(&remote_addr).copied() {
                Some(0) => {
                    self.unsendable_after.remove(&remote_addr);
                    return Err(std::io::Error::from(ErrorKind::InvalidInput));
                }
                Some(remaining) => {
                    self.unsendable_after.insert(remote_addr, remaining - 1);
                }
                None => {}
            }
            if self.failing_peers.contains(&remote_addr) {
                return Err(std::io::Error::other("injected send failure"));
            }
        }
        self.socket.send_to(wire, remote_addr)
    }
}

#[cfg(test)]
mod send_failure_tests;

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

#[cfg(test)]
mod tests {
    use super::*;

    // contracts/cultnet/interop/error-vectors.json, `write_not_authorized`: bytes captured from C# MessagePack
    // (`ForRemoteWriteRefused(request for "some:key", "not granted")`) and pinned by C#, TS and Python too.
    const WRITE_NOT_AUTHORIZED_HEX: &str = "85AD736368656D6156657273696F6EB063756C746E65742E6572726F722E7630A56572726F72D92177726974655F6E6F745F617574686F72697A65643A206E6F74206772616E746564AB726F7574696E6748696E74C0A4636F6465B477726974655F6E6F745F617574686F72697A6564A764657461696C7384A56669656C64A97265636F72644B6579A576616C7565A8736F6D653A6B6579A461734F66C0A763757272656E74C0";

    // The same file's `message_invalid` vector: field `document`, reason "Raw put messages must contain a document map."
    const MESSAGE_INVALID_HEX: &str = "85AD736368656D6156657273696F6EB063756C746E65742E6572726F722E7630A56572726F72D93E6D6573736167655F696E76616C69643A2052617720707574206D65737361676573206D75737420636F6E7461696E206120646F63756D656E74206D61702EAB726F7574696E6748696E74C0A4636F6465AF6D6573736167655F696E76616C6964A764657461696C7384A56669656C64A8646F63756D656E74A576616C7565C0A461734F66C0A763757272656E74C0";

    fn hex(bytes: &[u8]) -> String {
        bytes.iter().map(|byte| format!("{byte:02X}")).collect()
    }

    #[test]
    fn the_sink_refusal_is_the_pinned_write_not_authorized_bytes() {
        let bytes = encode_cultnet_message_to_vec(
            &write_refused_message("some:key", "not granted"),
            CultNetWireContract::CultNetSchemaV0,
        )
        .unwrap();
        assert_eq!(hex(&bytes), WRITE_NOT_AUTHORIZED_HEX);
    }

    #[test]
    fn the_unreadable_refusal_is_the_pinned_message_invalid_bytes() {
        let bytes = encode_cultnet_message_to_vec(
            &message_invalid_message("document", "Raw put messages must contain a document map."),
            CultNetWireContract::CultNetSchemaV0,
        )
        .unwrap();
        assert_eq!(hex(&bytes), MESSAGE_INVALID_HEX);
    }
}
