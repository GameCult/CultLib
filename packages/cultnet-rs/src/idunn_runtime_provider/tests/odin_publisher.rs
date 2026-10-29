//! The publisher against cultmesh-rs's real RUDP document server, the receiver
//! Odin runs. cultmesh-rs links the non-test build of this crate, so the
//! server's types are a second instance of ours: only bytes and strings cross.

use std::net::{SocketAddr, UdpSocket};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use cultmesh_rs::{
    CultMeshRudpDocumentServer, CultMeshRudpDocumentServerOptions, CultMeshRudpPollOutcome,
    CultMeshRudpRawDocumentReceipt, CultMeshRudpSnapshotQuery, CultMeshSystemClock,
};

use super::*;
use crate::idunn_runtime_provider::odin_publisher::Cadence;

const ODIN_CAPABILITY: &str = "odin.verse-rendezvous";
/// Text a refusing sink carries; Odin's error frame must not repeat it.
const SINK_CANARY: &str = "SINK-CANARY-8841";

const TEST_CADENCE: Cadence = Cadence {
    heartbeat: Duration::from_millis(100),
    lease_poll: Duration::from_millis(5),
    connect: Duration::from_millis(500),
    ack: Duration::from_millis(500),
};

// --------------------------------------------------------------- Odin harness

#[derive(Clone, Debug)]
struct Delivered {
    message_id: String,
    record_key: String,
    schema_id: String,
    payload: Vec<u8>,
    from: SocketAddr,
}

impl Delivered {
    fn record(&self) -> GameCultRuntimePresenceHealthRecord {
        rmp_serde::from_slice(&self.payload).expect("a delivered presence record decodes")
    }
}

#[derive(Clone, Copy)]
enum Sink {
    Accept,
    Refuse,
}

/// cultmesh-rs's document server on a thread, with a sink that records or refuses.
struct Odin {
    addr: SocketAddr,
    received: Arc<Mutex<Vec<Delivered>>>,
    stop: Arc<AtomicBool>,
    thread: Option<JoinHandle<()>>,
}

impl Odin {
    fn start(sink: Sink) -> Self {
        Self::start_on("127.0.0.1:0".parse().unwrap(), sink)
    }

    fn start_on(addr: SocketAddr, sink: Sink) -> Self {
        let socket = UdpSocket::bind(addr).expect("the Odin test socket binds");
        let addr = socket.local_addr().unwrap();
        let received = Arc::new(Mutex::new(Vec::new()));
        let stop = Arc::new(AtomicBool::new(false));
        let record = Arc::clone(&received);
        let mut server = CultMeshRudpDocumentServer::new(
            socket,
            move |receipt: CultMeshRudpRawDocumentReceipt| -> Result<()> {
                match sink {
                    Sink::Refuse => bail!(SINK_CANARY),
                    Sink::Accept => {
                        record.lock().unwrap().push(Delivered {
                            message_id: receipt.message_id,
                            record_key: receipt.document.record_key,
                            schema_id: receipt.document.schema_id,
                            payload: receipt.document.payload,
                            from: receipt.session.remote_addr,
                        });
                        Ok(())
                    }
                }
            },
            |_: &CultMeshRudpSnapshotQuery| -> Result<Vec<_>> { Ok(Vec::new()) },
            CultMeshSystemClock::default(),
            CultMeshRudpDocumentServerOptions::default(),
        )
        .expect("the Odin test server starts");
        let running = Arc::clone(&stop);
        let thread = std::thread::spawn(move || {
            while !running.load(Ordering::Relaxed) {
                if matches!(
                    server.poll_once(),
                    Ok(CultMeshRudpPollOutcome::Idle) | Err(_)
                ) {
                    std::thread::sleep(Duration::from_millis(1));
                }
            }
        });
        Self {
            addr,
            received,
            stop,
            thread: Some(thread),
        }
    }

    fn deliveries(&self) -> Vec<Delivered> {
        self.received.lock().unwrap().clone()
    }

    fn count(&self) -> usize {
        self.received.lock().unwrap().len()
    }

    fn wait_for(&self, count: usize) -> Vec<Delivered> {
        let give_up = Instant::now() + Duration::from_secs(10);
        while self.count() < count {
            assert!(Instant::now() < give_up, "Odin never received {count}");
            std::thread::sleep(Duration::from_millis(2));
        }
        self.deliveries()
    }

    fn rudp_endpoint(&self) -> String {
        format!("rudp://{}", self.addr)
    }
}

impl Drop for Odin {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

/// An address nothing listens on.
fn dead_address() -> SocketAddr {
    UdpSocket::bind("127.0.0.1:0")
        .unwrap()
        .local_addr()
        .unwrap()
}

// ----------------------------------------------------------------------- rigs

fn odin_dependency(schema: &str, endpoint: Option<String>) -> IdunnExpectedDependency {
    IdunnExpectedDependency {
        kind: "shared-infrastructure".into(),
        capability: ODIN_CAPABILITY.into(),
        schema: schema.into(),
        compatibility: "v1".into(),
        minimum_capacity: 1,
        startup: "before-start".into(),
        provider_id: Some("odin-ygg-1".into()),
        provider_authority: Some("managed-incarnation".into()),
        provider_expected_projection_sha256: Some(digest('9')),
        provider_endpoint: endpoint,
    }
}

fn rig_with(write_lease_required: bool, dependencies: Vec<IdunnExpectedDependency>) -> Result<Rig> {
    Rig::with_dependencies(write_lease_required, dependencies)
}

fn odin_rig(write_lease_required: bool, endpoint: &str) -> Result<Rig> {
    rig_with(
        write_lease_required,
        vec![odin_dependency("odin.verse.v1", Some(endpoint.into()))],
    )
}

fn publisher_for(
    signer: &IdunnRuntimePresenceSigner,
    cadence: Cadence,
) -> Result<IdunnRuntimePresencePublisher> {
    Ok(IdunnRuntimePresencePublisher::for_signer(signer)?
        .expect("Expected declares an Odin dependency")
        .with_cadence(cadence))
}

// ------------------------------------------------------------------- endpoint

#[test]
fn for_signer_reads_endpoint_from_expected() -> Result<()> {
    let odin = Odin::start(Sink::Accept);
    // `rudp://host:port` and bare `host:port` both reach the dependency's server.
    for endpoint in [odin.rudp_endpoint(), odin.addr.to_string()] {
        let rig = odin_rig(false, &endpoint)?;
        let mut signer = rig.signer()?;
        let publisher = publisher_for(&signer, TEST_CADENCE)?;
        publisher.publish(&mut signer, "hello")?;
    }
    assert_eq!(odin.count(), 2);

    // No Odin dependency: the target is route-proof and has no publisher.
    let rig = Rig::new(false)?;
    assert!(IdunnRuntimePresencePublisher::for_signer(&rig.signer()?)?.is_none());

    // Only a shared-infrastructure dependency on Odin is Odin.
    let mut bootstrap = odin_dependency("odin.verse.v1", Some(odin.rudp_endpoint()));
    bootstrap.kind = "bootstrap".into();
    let mut other = odin_dependency("odin.verse.v1", Some(odin.rudp_endpoint()));
    other.capability = "telemetry".into();
    let rig = rig_with(false, vec![bootstrap])?;
    assert!(IdunnRuntimePresencePublisher::for_signer(&rig.signer()?)?.is_none());
    let rig = rig_with(false, vec![other])?;
    assert!(IdunnRuntimePresencePublisher::for_signer(&rig.signer()?)?.is_none());

    // IPv6 hosts are bracketed.
    let rig = odin_rig(false, "rudp://[::1]:9000")?;
    assert!(IdunnRuntimePresencePublisher::for_signer(&rig.signer()?)?.is_some());
    Ok(())
}

#[test]
fn for_signer_refuses_what_is_not_one_usable_endpoint() -> Result<()> {
    let canary = "canary-host.invalid";
    let two = rig_with(
        false,
        vec![
            odin_dependency("odin.verse.v1", Some(format!("{canary}:9000"))),
            odin_dependency("odin.verse.v2", Some(format!("{canary}:9001"))),
        ],
    )?;
    let refused = refusal(IdunnRuntimePresencePublisher::for_signer(&two.signer()?));
    assert!(refused.contains(ODIN_CAPABILITY), "{refused}");
    assert!(!refused.contains(canary), "{refused}");

    let missing = rig_with(false, vec![odin_dependency("odin.verse.v1", None)])?;
    let refused = refusal(IdunnRuntimePresencePublisher::for_signer(
        &missing.signer()?,
    ));
    assert!(refused.contains("provider_endpoint"), "{refused}");

    for bad in [
        format!("udp://{canary}:9000"),
        format!("http://{canary}:9000"),
        format!("tcp://{canary}:9000"),
        canary.to_string(),
        format!("{canary}:0"),
        format!("{canary}:notaport"),
        format!("{canary}:9000/path"),
        format!(":{}", 9000),
    ] {
        let rig = rig_with(
            false,
            vec![odin_dependency("odin.verse.v1", Some(bad.clone()))],
        )?;
        let refused = refusal(IdunnRuntimePresencePublisher::for_signer(&rig.signer()?));
        assert!(
            refused.contains("provider_endpoint"),
            "{bad} was not refused by field: {refused}"
        );
        assert!(!refused.contains(canary), "{refused}");
    }
    Ok(())
}

// -------------------------------------------------------------------- publish

#[test]
fn publish_delivers_to_odin_catalog_server() -> Result<()> {
    let odin = Odin::start(Sink::Accept);
    let rig = odin_rig(false, &odin.rudp_endpoint())?;
    let mut signer = rig.signer()?;
    let publisher = publisher_for(&signer, TEST_CADENCE)?;

    let signed = publisher.publish(&mut signer, "hello")?;

    // The sink ran before the ack, so the document is already there.
    let delivered = odin.deliveries();
    assert_eq!(delivered.len(), 1);
    let delivered = &delivered[0];
    assert_eq!(delivered.payload, signed.document.payload);
    assert_eq!(delivered.record_key, TARGET);
    assert_eq!(delivered.schema_id, GAMECULT_RUNTIME_PRESENCE_HEALTH_SCHEMA);
    assert_eq!(
        delivered.message_id,
        format!(
            "runtime-presence:{TARGET}:{}:{}",
            signed.record.runtime_instance_id, signed.record.publisher_sequence
        )
    );
    assert_eq!(signed.record.publisher_sequence, 1);
    assert_odin_accepts(&rig, &signed, signed.record.observed_at_unix_millis + 1)?;
    Ok(())
}

#[test]
fn publish_surfaces_odin_refusal() -> Result<()> {
    let odin = Odin::start(Sink::Refuse);
    let rig = odin_rig(false, &odin.rudp_endpoint())?;
    let mut signer = rig.signer()?;
    let publisher = publisher_for(&signer, TEST_CADENCE)?;

    let refused = refusal(publisher.publish(&mut signer, "hello"));

    assert!(refused.contains("odin_refused"), "{refused}");
    assert!(!refused.contains(SINK_CANARY), "{refused}");
    assert!(!refused.contains("127.0.0.1"), "{refused}");
    assert!(
        !refused.contains(&odin.addr.port().to_string()),
        "{refused}"
    );
    Ok(())
}

#[test]
fn publish_times_out_without_echoing_endpoint() -> Result<()> {
    let dead = dead_address();
    let rig = odin_rig(false, &format!("rudp://{dead}"))?;
    let mut signer = rig.signer()?;
    let cadence = Cadence {
        connect: Duration::from_millis(300),
        ..TEST_CADENCE
    };
    let publisher = publisher_for(&signer, cadence)?;

    let started = Instant::now();
    let refused = refusal(publisher.publish(&mut signer, "hello"));
    let elapsed = started.elapsed();

    assert!(refused.contains("connect_timeout"), "{refused}");
    assert!(!refused.contains("127.0.0.1"), "{refused}");
    assert!(!refused.contains(&dead.port().to_string()), "{refused}");
    assert!(elapsed >= Duration::from_millis(300), "{elapsed:?}");
    assert!(elapsed < Duration::from_millis(1_500), "{elapsed:?}");
    Ok(())
}

#[test]
fn publisher_states_signer_health() -> Result<()> {
    let odin = Odin::start(Sink::Accept);
    let rig = odin_rig(false, &odin.rudp_endpoint())?;
    let mut signer = rig.signer()?;
    let publisher = publisher_for(&signer, TEST_CADENCE)?;

    publisher.publish(&mut signer, "first")?;
    signer.report_health(RuntimePresenceState::Degraded);
    let signed = publisher.publish(&mut signer, "second")?;

    let delivered = odin.deliveries();
    assert_eq!(delivered[0].record().state, "warming");
    assert_eq!(delivered[1].record().state, "degraded");
    assert_eq!(signed.record.state, "degraded");
    Ok(())
}

#[test]
fn two_publishes_strictly_increase_sequence_in_fresh_sessions() -> Result<()> {
    let odin = Odin::start(Sink::Accept);
    let rig = odin_rig(false, &odin.rudp_endpoint())?;
    let mut signer = rig.signer()?;
    let publisher = publisher_for(&signer, TEST_CADENCE)?;

    publisher.publish(&mut signer, "one")?;
    // A route answer signs from the same signer, so it takes sequence 2.
    let answer = signer.answer_route_observation(&CultNetMessage::SnapshotRequest {
        message_id: "challenge".into(),
        schema_ids: None,
        record_keys: None,
    })?;
    assert!(matches!(answer, CultNetMessage::SnapshotResponseRaw { .. }));
    publisher.publish(&mut signer, "two")?;

    let delivered = odin.deliveries();
    assert_eq!(delivered.len(), 2);
    assert_eq!(delivered[0].record().publisher_sequence, 1);
    assert_eq!(delivered[1].record().publisher_sequence, 3);
    assert!(
        delivered[0].message_id.ends_with(":1"),
        "{:?}",
        delivered[0]
    );
    assert!(
        delivered[1].message_id.ends_with(":3"),
        "{:?}",
        delivered[1]
    );
    assert_ne!(
        delivered[0].from, delivered[1].from,
        "each publish is its own RUDP session"
    );
    Ok(())
}

#[test]
fn publisher_refuses_foreign_signer() -> Result<()> {
    let odin = Odin::start(Sink::Accept);
    let rig = odin_rig(false, &odin.rudp_endpoint())?;
    let other = odin_rig(false, &odin.rudp_endpoint())?;
    let publisher = publisher_for(&rig.signer()?, TEST_CADENCE)?;
    let mut foreign = other.signer()?;

    let refused = refusal(publisher.publish(&mut foreign, "hello"));
    assert!(refused.contains("another runtime authority"), "{refused}");
    let refused = refusal(publisher.wait_for_write_lease(&mut foreign, Duration::from_secs(1)));
    assert!(refused.contains("another runtime authority"), "{refused}");

    assert_eq!(odin.count(), 0, "nothing was delivered");
    assert_eq!(
        foreign.sign("untouched")?.record.publisher_sequence,
        1,
        "a refused signer signed nothing"
    );
    Ok(())
}

// ----------------------------------------------------------------- lease wait

#[test]
fn lease_wait_not_required_returns_none_without_publishing() -> Result<()> {
    let odin = Odin::start(Sink::Accept);
    let rig = odin_rig(false, &odin.rudp_endpoint())?;
    let mut signer = rig.signer()?;
    let publisher = publisher_for(&signer, TEST_CADENCE)?;

    let guard = publisher.wait_for_write_lease(&mut signer, Duration::from_secs(5))?;

    assert!(guard.is_none());
    assert_eq!(odin.count(), 0);
    assert_eq!(signer.sign("untouched")?.record.publisher_sequence, 1);
    Ok(())
}

#[test]
fn catalog_connection_id_has_one_spelling() {
    assert_eq!(
        cultmesh_rs::CULTMESH_RUDP_DOCUMENT_CATALOG_CONNECTION_ID,
        crate::CULTMESH_RUDP_DOCUMENT_CATALOG_CONNECTION_ID
    );
    assert_eq!(
        crate::CULTMESH_RUDP_DOCUMENT_CATALOG_CONNECTION_ID,
        0x0d1d_0002,
        "Odin's catalog id is wire contract"
    );
}

/// The lease holder needs the lock files of a unix host; Windows refuses a
/// lease requirement when it opens the authority.
#[cfg(unix)]
mod lease_wait {
    use super::*;

    /// Writes Idunn's lease for `warming_sha256` once `count` heartbeats arrived,
    /// then stamps when it finished.
    fn grant_after(
        rig: &Rig,
        odin: &Odin,
        count: usize,
        which: usize,
        written: &Mutex<Option<Instant>>,
    ) {
        let delivered = odin.wait_for(count);
        let sha = delivered[which].record().canonical_sha256().unwrap();
        rig.write_lease(&sha, 1).unwrap();
        *written.lock().unwrap() = Some(Instant::now());
    }

    #[test]
    fn lease_wait_heartbeats_and_returns_guard() -> Result<()> {
        let odin = Odin::start(Sink::Accept);
        let rig = odin_rig(true, &odin.rudp_endpoint())?;
        let mut signer = rig.signer()?;
        let cadence = Cadence {
            heartbeat: Duration::from_millis(300),
            ..TEST_CADENCE
        };
        let publisher = publisher_for(&signer, cadence)?;
        let written = Mutex::new(None);

        let started = Instant::now();
        let guard = std::thread::scope(|scope| {
            scope.spawn(|| grant_after(&rig, &odin, 2, 1, &written));
            publisher.wait_for_write_lease(&mut signer, Duration::from_secs(10))
        })?
        .expect("the wait returns the lease guard");
        let returned = Instant::now();

        let delivered = odin.deliveries();
        assert_eq!(delivered.len(), 2, "no heartbeat after the lease");
        for heartbeat in &delivered {
            let record = heartbeat.record();
            assert_eq!(record.state, "warming");
            assert_eq!(record.write_lease_sha256, None);
        }
        // The first heartbeat is sent on entry, the next one heartbeat later.
        let gap = delivered[1].record().observed_at_unix_millis
            - delivered[0].record().observed_at_unix_millis;
        assert!((290..600).contains(&gap), "heartbeat gap {gap} ms");
        assert!(returned - started >= Duration::from_millis(290));
        // The lease is polled far faster than heartbeats are sent.
        let written = written.lock().unwrap().expect("the lease was written");
        assert!(
            returned - written < Duration::from_millis(150),
            "the wait noticed the lease after {:?}",
            returned - written
        );
        // The guard is the one the signer now signs under.
        signer.report_health(RuntimePresenceState::Active);
        let active = publisher.publish(&mut signer, "ready")?;
        assert_eq!(
            active.record.write_lease_sha256.as_deref(),
            Some(guard.lease_sha256())
        );
        Ok(())
    }

    #[test]
    fn lease_wait_refuses_non_warming_signer() -> Result<()> {
        let odin = Odin::start(Sink::Accept);
        let rig = odin_rig(true, &odin.rudp_endpoint())?;
        let mut signer = rig.signer()?;
        let publisher = publisher_for(&signer, TEST_CADENCE)?;
        signer.report_health(RuntimePresenceState::Active);

        let refused = refusal(publisher.wait_for_write_lease(&mut signer, Duration::from_secs(5)));

        assert!(refused.contains("Warming"), "{refused}");
        assert_eq!(odin.count(), 0, "refused before any publish");
        Ok(())
    }

    #[test]
    fn lease_wait_survives_failed_heartbeat() -> Result<()> {
        let addr = dead_address();
        let rig = odin_rig(true, &format!("rudp://{addr}"))?;
        let mut signer = rig.signer()?;
        let cadence = Cadence {
            connect: Duration::from_millis(60),
            ..TEST_CADENCE
        };
        let publisher = publisher_for(&signer, cadence)?;
        let written = Mutex::new(None);

        let started = Instant::now();
        let guard = std::thread::scope(|scope| {
            // Odin comes up after the first heartbeats failed.
            let late = scope.spawn(|| {
                std::thread::sleep(Duration::from_millis(350));
                let odin = Odin::start_on(addr, Sink::Accept);
                grant_after(&rig, &odin, 1, 0, &written);
                odin
            });
            let guard = publisher.wait_for_write_lease(&mut signer, Duration::from_secs(10));
            late.join().unwrap();
            guard
        })?;

        assert!(guard.is_some());
        assert!(started.elapsed() >= Duration::from_millis(350));
        Ok(())
    }

    #[test]
    fn lease_wait_times_out_with_counts() -> Result<()> {
        let dead = dead_address();
        let rig = odin_rig(true, &format!("rudp://{dead}"))?;
        let mut signer = rig.signer()?;
        let cadence = Cadence {
            heartbeat: Duration::from_millis(50),
            connect: Duration::from_millis(30),
            ..TEST_CADENCE
        };
        let publisher = publisher_for(&signer, cadence)?;

        let started = Instant::now();
        let refused =
            refusal(publisher.wait_for_write_lease(&mut signer, Duration::from_millis(500)));
        let elapsed = started.elapsed();

        assert!(elapsed >= Duration::from_millis(500), "{elapsed:?}");
        assert!(elapsed < Duration::from_millis(2_000), "{elapsed:?}");
        let count: u32 = refused
            .split(" failed deliveries")
            .next()
            .and_then(|head| head.rsplit(' ').next())
            .and_then(|count| count.parse().ok())
            .unwrap_or_else(|| panic!("no failure count in {refused}"));
        assert!((3..=11).contains(&count), "{count} failed deliveries");
        assert!(refused.contains("connect_timeout"), "{refused}");
        assert!(!refused.contains("127.0.0.1"), "{refused}");
        assert!(!refused.contains(&dead.port().to_string()), "{refused}");
        assert!(
            !refused.contains(rig.dir.path().to_str().unwrap()),
            "{refused}"
        );
        Ok(())
    }

    #[test]
    fn lease_wait_timeout_counts_only_failed_deliveries() -> Result<()> {
        let odin = Odin::start(Sink::Accept);
        let rig = odin_rig(true, &odin.rudp_endpoint())?;
        let mut signer = rig.signer()?;
        let cadence = Cadence {
            heartbeat: Duration::from_millis(50),
            ..TEST_CADENCE
        };
        let publisher = publisher_for(&signer, cadence)?;

        let refused =
            refusal(publisher.wait_for_write_lease(&mut signer, Duration::from_millis(400)));

        assert!(refused.contains(": 0 failed deliveries"), "{refused}");
        assert!(!refused.contains("last:"), "{refused}");
        assert!(odin.count() >= 3, "heartbeats kept arriving");
        Ok(())
    }

    #[test]
    fn lease_wait_ends_at_once_on_a_foreign_incarnation_lease() -> Result<()> {
        let odin = Odin::start(Sink::Accept);
        let rig = odin_rig(true, &odin.rudp_endpoint())?;
        let mut signer = rig.signer()?;
        let publisher = publisher_for(&signer, TEST_CADENCE)?;

        let started = Instant::now();
        let refused = std::thread::scope(|scope| {
            scope.spawn(|| {
                let sha = odin.wait_for(1)[0].record().canonical_sha256().unwrap();
                // Staged elsewhere, so the wait never sees a lease that binds it.
                let staged = rig.dir.path().join("staged-lease.cc");
                rig.write_lease_at(&staged, &sha, 1).unwrap();
                let stored = SingleFileMessagePackBackingStore::new(&staged)
                    .pull_all()
                    .unwrap()
                    .remove(0);
                let mut lease =
                    IdunnProcessWriteLeaseRecord::decode_canonical(&stored.payload).unwrap();
                lease.runtime_instance_id = digest('1');
                write_record(
                    &rig.lease_path(),
                    TARGET,
                    IdunnProcessWriteLeaseRecord::TYPE,
                    IDUNN_PROCESS_WRITE_LEASE_SCHEMA,
                    lease.canonical_bytes().unwrap(),
                )
                .unwrap();
            });
            refusal(publisher.wait_for_write_lease(&mut signer, Duration::from_secs(10)))
        });

        assert!(
            refused.contains("provider-owned Warming proof"),
            "{refused}"
        );
        assert!(started.elapsed() < Duration::from_secs(3));
        Ok(())
    }
}
