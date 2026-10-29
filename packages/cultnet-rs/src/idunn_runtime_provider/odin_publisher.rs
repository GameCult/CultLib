//! Delivery of signed runtime presence to Odin's document catalog.
//!
//! [`IdunnRuntimePresencePublisher`] is the network half of the Idunn provider.
//! It owns where signed presence goes and how it gets there; the signer it
//! borrows on every call stays the one owner of health, sequence, Warming proofs
//! and the write-lease guard. The publisher keeps no state of its own: Odin's
//! endpoint is read once from the one `shared-infrastructure`
//! `odin.verse-rendezvous` dependency in Expected, and each publish is one
//! fresh RUDP session, so an Odin restart needs no reconnect machinery.
//!
//! Blocking `std::net` only. It builds and runs wherever the provider does.

use std::net::{ToSocketAddrs, UdpSocket};
use std::time::{Duration, Instant};

use anyhow::{Result, bail, ensure};

use super::{
    IdunnRuntimePresenceSigner, ProcessWriteLeaseGuard, RuntimePresenceState, SignedRuntimePresence,
};
use crate::{
    CULTMESH_RUDP_DOCUMENT_CATALOG_CONNECTION_ID, CultNetErrorCode, CultNetMessage,
    CultNetRudpReliableSendStatus, CultNetRudpSocketTransportConnection,
    CultNetRudpSocketTransportOptions, CultNetWireContract, encode_cultnet_message_to_vec,
};

const ODIN_DEPENDENCY_KIND: &str = "shared-infrastructure";
const ODIN_CAPABILITY: &str = "odin.verse-rendezvous";
/// Names the field in refusals; never the value.
const ENDPOINT_FIELD: &str = "Expected odin.verse-rendezvous dependency provider_endpoint";
const RUDP_SCHEME: &str = "rudp://";
/// How long one socket read waits, so the delivery loops poll without spinning.
const RECEIVE_SLICE: Duration = Duration::from_millis(25);
const RESEND_DELAY_MS: u64 = 100;

/// The publisher's timing. Crate-private: tests shorten it, consumers do not
/// tune it. A fixed 5 s heartbeat under Idunn's own Warming deadline needs no
/// backoff.
#[derive(Clone, Copy, Debug)]
pub(super) struct Cadence {
    pub(super) heartbeat: Duration,
    pub(super) lease_poll: Duration,
    pub(super) connect: Duration,
    pub(super) ack: Duration,
}

impl Cadence {
    const PRODUCTION: Self = Self {
        heartbeat: Duration::from_secs(5),
        lease_poll: Duration::from_millis(100),
        connect: Duration::from_secs(2),
        ack: Duration::from_secs(2),
    };
}

/// Why one delivery failed. Each variant is a fixed code: nothing here carries
/// Odin's text, an address or a path.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum DeliveryFailure {
    Unresolved,
    NoSocket,
    ConnectTimeout,
    SendFailed,
    AckTimeout,
    SendInvalidated,
    ReplyUnreadable,
    SessionEnded,
    Refused(Option<CultNetErrorCode>),
}

impl std::fmt::Display for DeliveryFailure {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str("Odin delivery failed: ")?;
        match self {
            Self::Unresolved => formatter.write_str("endpoint_unresolved"),
            Self::NoSocket => formatter.write_str("socket_unavailable"),
            Self::ConnectTimeout => formatter.write_str("connect_timeout"),
            Self::SendFailed => formatter.write_str("send_failed"),
            Self::AckTimeout => formatter.write_str("ack_timeout"),
            Self::SendInvalidated => formatter.write_str("send_invalidated"),
            Self::ReplyUnreadable => formatter.write_str("reply_unreadable"),
            Self::SessionEnded => formatter.write_str("session_ended"),
            Self::Refused(Some(code)) => write!(formatter, "odin_refused code {code:?}"),
            Self::Refused(None) => formatter.write_str("odin_refused without a code"),
        }
    }
}

impl std::error::Error for DeliveryFailure {}

/// Delivers an Idunn-managed runtime's signed presence to Odin and waits for
/// Idunn's write lease while Warming.
///
/// Built from a signer, it borrows that signer on every call, so route answers
/// and publishes share one signer and one sequence. Health is whatever the
/// signer was last told with
/// [`report_health`](IdunnRuntimePresenceSigner::report_health); the publisher
/// has no state parameter.
pub struct IdunnRuntimePresencePublisher {
    host: String,
    port: u16,
    expected_sha256: String,
    runtime_instance_id: String,
    cadence: Cadence,
}

impl std::fmt::Debug for IdunnRuntimePresencePublisher {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("IdunnRuntimePresencePublisher")
            .finish_non_exhaustive()
    }
}

impl IdunnRuntimePresencePublisher {
    /// The publisher for `signer`'s Odin, or `None` when Expected declares no
    /// `shared-infrastructure` `odin.verse-rendezvous` dependency. Such a
    /// target is route-proof: Idunn reads its presence through the route
    /// challenge, which [`answer_route_observation`](IdunnRuntimePresenceSigner::answer_route_observation)
    /// answers with no Odin.
    ///
    /// Refuses two such dependencies, a missing `provider_endpoint`, a scheme
    /// other than `rudp://` and an endpoint that is not `host:port`. A refusal
    /// names the field, never the value. The host is resolved at each publish.
    pub fn for_signer(signer: &IdunnRuntimePresenceSigner) -> Result<Option<Self>> {
        let records = &signer.authority.records;
        let mut odin = records.expected.dependencies.iter().filter(|dependency| {
            dependency.kind == ODIN_DEPENDENCY_KIND && dependency.capability == ODIN_CAPABILITY
        });
        let Some(dependency) = odin.next() else {
            return Ok(None);
        };
        ensure!(
            odin.next().is_none(),
            "Expected declares more than one odin.verse-rendezvous shared-infrastructure dependency"
        );
        let Some(endpoint) = dependency.provider_endpoint.as_deref() else {
            bail!("{ENDPOINT_FIELD} is missing");
        };
        let Some((host, port)) = parse_endpoint(endpoint) else {
            bail!("{ENDPOINT_FIELD} must be rudp://host:port or host:port");
        };
        Ok(Some(Self {
            host,
            port,
            expected_sha256: records.expected_sha256.clone(),
            runtime_instance_id: records.activation.runtime_instance_id.clone(),
            cadence: Cadence::PRODUCTION,
        }))
    }

    #[cfg(test)]
    pub(super) fn with_cadence(mut self, cadence: Cadence) -> Self {
        self.cadence = cadence;
        self
    }

    /// Signs one presence in the signer's reported health and delivers it. It
    /// returns after Odin acknowledged the document. The ack proves delivery;
    /// Odin's correlation owns admission. A CultNet error frame from Odin is a
    /// refusal naming its code, never its text.
    pub fn publish(
        &self,
        signer: &mut IdunnRuntimePresenceSigner,
        detail: &str,
    ) -> Result<SignedRuntimePresence> {
        let (signed, delivery) = self.sign_and_deliver(signer, detail)?;
        delivery?;
        Ok(signed)
    }

    /// Waits for Idunn's process write lease, publishing a Warming heartbeat on
    /// entry and every 5 s and polling for the lease every 100 ms. The signer
    /// must report Warming; the wait is refused before any publish otherwise.
    /// Returns `Ok(None)` at once when Expected requires no lease.
    ///
    /// The caller sets `timeout` because Idunn owns the Warming deadline. A
    /// heartbeat that fails delivery is retried at the next heartbeat and does
    /// not end the wait: the signer remembers its Warming proofs whether or not
    /// delivery succeeded. A lease record that binds another incarnation ends
    /// the wait at once. A timeout names the count of failed deliveries and the
    /// last one's code.
    pub fn wait_for_write_lease(
        &self,
        signer: &mut IdunnRuntimePresenceSigner,
        timeout: Duration,
    ) -> Result<Option<ProcessWriteLeaseGuard>> {
        self.require_own_signer(signer)?;
        if !signer.authority.records.expected.write_lease_required {
            return Ok(None);
        }
        ensure!(
            signer.health() == RuntimePresenceState::Warming,
            "waiting for the write lease requires the signer to report Warming"
        );
        let started = Instant::now();
        let mut next_heartbeat = started;
        let mut failed_deliveries = 0_u32;
        let mut last_failure = None;
        loop {
            let now = Instant::now();
            if now >= next_heartbeat {
                let (_, delivery) =
                    self.sign_and_deliver(signer, "waiting-for-process-write-lease")?;
                if let Err(failure) = delivery {
                    failed_deliveries += 1;
                    last_failure = Some(failure);
                }
                next_heartbeat = now + self.cadence.heartbeat;
            }
            if let Some(guard) = signer.try_acquire_write_lease()? {
                return Ok(Some(guard));
            }
            let Some(remaining) = timeout.checked_sub(started.elapsed()) else {
                bail!(
                    "timed out waiting for Idunn's process write lease: {failed_deliveries} failed deliveries{}",
                    last_failure.map_or(String::new(), |failure| format!(", last: {failure}"))
                );
            };
            std::thread::sleep(self.cadence.lease_poll.min(remaining));
        }
    }

    /// Every presence that reaches Odin goes through here. The outer error is a
    /// refusal before delivery; the inner result is the delivery.
    fn sign_and_deliver(
        &self,
        signer: &mut IdunnRuntimePresenceSigner,
        detail: &str,
    ) -> Result<(SignedRuntimePresence, Result<(), DeliveryFailure>)> {
        self.require_own_signer(signer)?;
        let signed = signer.sign(detail)?;
        let delivery = self.deliver(&signed);
        Ok((signed, delivery))
    }

    fn require_own_signer(&self, signer: &IdunnRuntimePresenceSigner) -> Result<()> {
        let records = &signer.authority.records;
        ensure!(
            records.expected_sha256 == self.expected_sha256
                && records.activation.runtime_instance_id == self.runtime_instance_id,
            "this publisher was built for another runtime authority"
        );
        Ok(())
    }

    /// One fresh session: connect, one reliable `DocumentPutRaw` on `schema`,
    /// wait for the ack. The message id comes from the signed record, so the
    /// publisher keeps no counter.
    fn deliver(&self, signed: &SignedRuntimePresence) -> Result<(), DeliveryFailure> {
        let remote = (self.host.as_str(), self.port)
            .to_socket_addrs()
            .ok()
            .and_then(|mut addresses| addresses.next())
            .ok_or(DeliveryFailure::Unresolved)?;
        let socket = UdpSocket::bind(if remote.is_ipv4() {
            "0.0.0.0:0"
        } else {
            "[::]:0"
        })
        .map_err(|_| DeliveryFailure::NoSocket)?;
        socket
            .set_read_timeout(Some(RECEIVE_SLICE))
            .map_err(|_| DeliveryFailure::NoSocket)?;
        let mut options = CultNetRudpSocketTransportOptions::client(
            signed.record.runtime_id.clone(),
            socket,
            remote,
            CULTMESH_RUDP_DOCUMENT_CATALOG_CONNECTION_ID,
        );
        options.resend_delay_ms = RESEND_DELAY_MS;
        let mut transport = CultNetRudpSocketTransportConnection::new(options)
            .map_err(|_| DeliveryFailure::NoSocket)?;
        transport
            .connect(Vec::new())
            .map_err(|_| DeliveryFailure::SendFailed)?;
        let connect_deadline = Instant::now() + self.cadence.connect;
        loop {
            pump(&mut transport)?;
            if transport.connected() {
                break;
            }
            if Instant::now() >= connect_deadline {
                return Err(DeliveryFailure::ConnectTimeout);
            }
        }
        let message = CultNetMessage::DocumentPutRaw {
            message_id: format!(
                "runtime-presence:{}:{}:{}",
                signed.record.target,
                signed.record.runtime_instance_id,
                signed.record.publisher_sequence
            ),
            document: signed.document.clone(),
        };
        let payload = encode_cultnet_message_to_vec(&message, CultNetWireContract::CultNetSchemaV0)
            .map_err(|_| DeliveryFailure::SendFailed)?;
        let receipt = transport
            .send_reliable("schema", payload)
            .map_err(|_| DeliveryFailure::SendFailed)?;
        let ack_deadline = Instant::now() + self.cadence.ack;
        loop {
            pump(&mut transport)?;
            match transport.reliable_send_status(&receipt) {
                CultNetRudpReliableSendStatus::Acknowledged => break,
                CultNetRudpReliableSendStatus::Invalidated => {
                    return Err(DeliveryFailure::SendInvalidated);
                }
                CultNetRudpReliableSendStatus::Pending => {}
            }
            if Instant::now() >= ack_deadline {
                return Err(DeliveryFailure::AckTimeout);
            }
        }
        // Best effort: the session is discarded either way.
        let _ = transport.disconnect(Vec::new());
        Ok(())
    }
}

/// Reads what Odin has sent, resends what is due and reports a refusal or an
/// ended session. Waits at most one socket read slice.
fn pump(transport: &mut CultNetRudpSocketTransportConnection) -> Result<(), DeliveryFailure> {
    loop {
        match transport.receive_schema_message_once() {
            Ok(Some(CultNetMessage::Error { code, .. })) => {
                return Err(DeliveryFailure::Refused(code));
            }
            Ok(Some(_)) => {}
            Ok(None) => break,
            Err(_) => return Err(DeliveryFailure::ReplyUnreadable),
        }
    }
    if transport.disconnect_reason().is_some() {
        return Err(DeliveryFailure::SessionEnded);
    }
    transport
        .poll_resends()
        .map_err(|_| DeliveryFailure::SendFailed)
}

/// `rudp://host:port` or bare `host:port` (an IPv6 host in brackets) as host
/// and port. Any other scheme, a path, a blank host or a zero port is `None`.
fn parse_endpoint(endpoint: &str) -> Option<(String, u16)> {
    let rest = match endpoint.strip_prefix(RUDP_SCHEME) {
        Some(rest) => rest,
        None if endpoint.contains("://") => return None,
        None => endpoint,
    };
    let (host, port) = if let Some(bracketed) = rest.strip_prefix('[') {
        let (host, after) = bracketed.split_once(']')?;
        (host, after.strip_prefix(':')?)
    } else {
        let (host, port) = rest.rsplit_once(':')?;
        if host.contains(':') {
            return None;
        }
        (host, port)
    };
    if host.is_empty() || host.contains(|c: char| c.is_whitespace() || c == '/') {
        return None;
    }
    let port: u16 = port.parse().ok().filter(|port| *port != 0)?;
    Some((host.to_owned(), port))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn parsed(endpoint: &str) -> Option<(&'static str, u16)> {
        parse_endpoint(endpoint)
            .map(|(host, port)| (Box::leak(host.into_boxed_str()) as &str, port))
    }

    #[test]
    fn endpoints_are_rudp_or_bare_host_and_port() {
        assert_eq!(
            parsed("rudp://odin.example:7001"),
            Some(("odin.example", 7001))
        );
        assert_eq!(parsed("10.0.0.5:7001"), Some(("10.0.0.5", 7001)));
        assert_eq!(parsed("rudp://[::1]:7001"), Some(("::1", 7001)));
        assert_eq!(parsed("[fd00::5]:65535"), Some(("fd00::5", 65535)));
        for bad in [
            "",
            "odin.example",
            "odin.example:0",
            "odin.example:65536",
            "odin.example:port",
            ":7001",
            "udp://odin.example:7001",
            "http://odin.example:7001",
            "rudp://odin.example:7001/x",
            "odin example:7001",
            "::1:7001",
            "[::1]",
            "[::1]7001",
            "rudp://rudp://odin.example:7001",
        ] {
            assert_eq!(parse_endpoint(bad), None, "{bad:?}");
        }
    }

    #[test]
    fn production_cadence_is_the_ruled_one() {
        let cadence = Cadence::PRODUCTION;
        assert_eq!(cadence.heartbeat, Duration::from_secs(5));
        assert_eq!(cadence.lease_poll, Duration::from_millis(100));
        assert_eq!(cadence.connect, Duration::from_secs(2));
        assert_eq!(cadence.ack, Duration::from_secs(2));
    }

    /// Odin's server sends error frames without a code, so a coded refusal is
    /// reachable only here.
    #[test]
    fn a_refusal_names_its_code_and_nothing_else() {
        let coded = DeliveryFailure::Refused(Some(CultNetErrorCode::WriteNotAuthorized));
        assert_eq!(
            coded.to_string(),
            "Odin delivery failed: odin_refused code WriteNotAuthorized"
        );
        assert_eq!(
            DeliveryFailure::Refused(None).to_string(),
            "Odin delivery failed: odin_refused without a code"
        );
    }
}
