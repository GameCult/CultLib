use std::collections::BTreeMap;
use std::io::Cursor;

use cultcache_rs::{CacheBackingStore, CultCacheEnvelope};

use super::*;
use crate::{
    IdunnExpectedCapability, IdunnExpectedDependency, IdunnExpectedRoute,
    IdunnRuntimeActivationLaunch, IdunnServiceIdentity, RUNTIME_PRESENCE_IDENTITY_FD_NAME,
    RuntimePresenceAuthenticationContext, VerifiedRuntimeAuthority,
    authenticate_runtime_presence_claim, correlate_runtime_presence_claim,
    enroll_service_identity_at, verify_runtime_authority,
};

const TARGET: &str = "ghostlight";
const HEALTH_CONTRACT: &str = "ghostlight.runtime-health.v1";
const BIND: &str = "127.0.0.1:14103";
const ENDPOINT: &str = "tcp://127.0.0.1:14103";
#[cfg(unix)]
const ACTIVATION_NAME: &str = IDUNN_RUNTIME_ACTIVATION_CREDENTIAL_NAME;
#[cfg(unix)]
const PROVIDER_NAME: &str = RUNTIME_PRESENCE_IDENTITY_FD_NAME;

fn digest(byte: char) -> String {
    format!("sha256-{}", byte.to_string().repeat(64))
}

fn refusal<T>(result: Result<T>) -> String {
    match result {
        Ok(_) => panic!("expected a refusal"),
        Err(error) => format!("{error:#}"),
    }
}

fn capabilities() -> Vec<GameCultRuntimeCapability> {
    ["conversation", "state"]
        .into_iter()
        .map(|name| GameCultRuntimeCapability {
            capability: name.into(),
            schema: format!("{name}.v1"),
            compatibility: "v1".into(),
            capacity: 1,
        })
        .collect()
}

fn write_record(path: &Path, key: &str, ty: &str, schema: &str, payload: Vec<u8>) -> Result<()> {
    SingleFileMessagePackBackingStore::new(path).push(&CultCacheEnvelope {
        key: key.into(),
        r#type: ty.into(),
        payload,
        stored_at: "2026-10-29T00:00:00Z".into(),
        schema_id: Some(schema.into()),
    })
}

// ------------------------------------------------------------------ fake host

struct FakeHost {
    vars: BTreeMap<String, String>,
    pid: u32,
    #[cfg(unix)]
    descriptors: BTreeMap<RawFd, File>,
}

impl FakeHost {
    fn new() -> Self {
        Self {
            vars: BTreeMap::new(),
            pid: 4242,
            #[cfg(unix)]
            descriptors: BTreeMap::new(),
        }
    }

    fn with(mut self, name: &str, value: &str) -> Self {
        self.vars.insert(name.into(), value.into());
        self
    }
}

impl LaunchHost for FakeHost {
    fn var(&self, name: &str) -> Option<String> {
        self.vars.get(name).cloned()
    }

    fn remove_var(&mut self, name: &str) {
        self.vars.remove(name);
    }

    fn process_id(&self) -> u32 {
        self.pid
    }

    #[cfg(unix)]
    fn adopt_descriptor(&mut self, descriptor: RawFd) -> Result<File> {
        self.descriptors
            .remove(&descriptor)
            .with_context(|| format!("descriptor {descriptor} is not open"))
    }
}

// ------------------------------------------------------------------------ rig

/// One launch's worth of Idunn material written under a temp directory.
struct Rig {
    dir: tempfile::TempDir,
    expected: IdunnExpectedIncarnationRecord,
    activation: IdunnRuntimeActivationRecord,
    activation_seed: Vec<u8>,
    provider_credential: Vec<u8>,
    idunn: ServiceIdentitySigner<IdunnServiceIdentity>,
    provider_public_key: Vec<u8>,
}

impl Rig {
    fn new(write_lease_required: bool) -> Result<Self> {
        Self::with_dependencies(write_lease_required, Vec::new())
    }

    fn with_dependencies(
        write_lease_required: bool,
        dependencies: Vec<IdunnExpectedDependency>,
    ) -> Result<Self> {
        let dir = tempfile::tempdir()?;
        let provider_path = dir.path().join("provider.cc");
        let provider =
            enroll_service_identity_at::<GameCultProviderHealthIdentity>(&provider_path)?;
        let idunn =
            enroll_service_identity_at::<IdunnServiceIdentity>(&dir.path().join("idunn.cc"))?;
        let provider_anchor = provider.trust_anchor()?;
        let expected = IdunnExpectedIncarnationRecord {
            schema_version: IDUNN_EXPECTED_INCARNATION_SCHEMA.into(),
            target: TARGET.into(),
            plan_id: digest('2'),
            incarnation_id: "ghostlight/2026-09-02/1".into(),
            sealed_release_id: digest('3'),
            source_repository: "github.com/GameCult/Ghostlight".into(),
            source_revision: "4".repeat(40),
            recipe_sha256: digest('5'),
            runtime_id: "ghostlight-yggdrasil-1".into(),
            expected_signer_identity_id: provider_anchor.identity_id.clone(),
            health_contract: HEALTH_CONTRACT.into(),
            artifact_sha256: digest('6'),
            state_schema_generation: Some("world-v2".into()),
            state_contract_sha256: Some(digest('7')),
            write_lease_required,
            route: Some(IdunnExpectedRoute {
                route_id: "ghostlight-public".into(),
                transport: "tcp".into(),
                stable_endpoint: "tcp://127.0.0.1:14100".into(),
                candidate_endpoint: ENDPOINT.into(),
            }),
            capabilities: ["conversation", "state"]
                .into_iter()
                .map(|name| IdunnExpectedCapability {
                    capability: name.into(),
                    schema: format!("{name}.v1"),
                    compatibility: "v1".into(),
                    minimum_capacity: 1,
                })
                .collect(),
            dependencies,
        };
        let (activation, activation_seed) = Self::issue(&expected, &idunn)?;
        let rig = Self {
            provider_credential: std::fs::read(&provider_path)?,
            provider_public_key: provider_anchor.public_key,
            activation,
            activation_seed,
            expected,
            idunn,
            dir,
        };
        std::fs::create_dir(rig.bundle())?;
        rig.write_bundle(&rig.expected, &rig.activation)?;
        Ok(rig)
    }

    fn issue(
        expected: &IdunnExpectedIncarnationRecord,
        idunn: &ServiceIdentitySigner<IdunnServiceIdentity>,
    ) -> Result<(IdunnRuntimeActivationRecord, Vec<u8>)> {
        let launch = IdunnRuntimeActivationLaunch::issue(expected, digest('8'), 99, idunn)?;
        let mut seed = Vec::new();
        let activation = launch.write_credential(&mut seed)?;
        Ok((activation, seed))
    }

    fn write_bundle(
        &self,
        expected: &IdunnExpectedIncarnationRecord,
        activation: &IdunnRuntimeActivationRecord,
    ) -> Result<()> {
        write_record(
            &self.bundle().join("expected.cc"),
            &expected.target,
            IdunnExpectedIncarnationRecord::TYPE,
            IDUNN_EXPECTED_INCARNATION_SCHEMA,
            expected.canonical_bytes()?,
        )?;
        write_record(
            &self.bundle().join("activation.cc"),
            &expected.target,
            IdunnRuntimeActivationRecord::TYPE,
            IDUNN_RUNTIME_ACTIVATION_SCHEMA,
            activation.canonical_bytes()?,
        )
    }

    fn bundle(&self) -> PathBuf {
        self.dir.path().join("bundle")
    }

    fn lease_path(&self) -> PathBuf {
        self.dir.path().join("lease.cc")
    }

    #[cfg(unix)]
    fn lock_path(&self) -> PathBuf {
        sibling_lock_path(&self.lease_path()).unwrap()
    }

    fn credentials(&self) -> IdunnRuntimeCredentials {
        IdunnRuntimeCredentials::new(
            Cursor::new(self.activation_seed.clone()),
            Cursor::new(self.provider_credential.clone()),
        )
    }

    /// A launch environment that names only the bundle.
    fn bare_host(&self) -> FakeHost {
        FakeHost::new().with(
            IDUNN_RUNTIME_BUNDLE_ENVIRONMENT,
            self.bundle().to_str().unwrap(),
        )
    }

    /// The whole launch environment Idunn sets for this rig.
    fn host(&self) -> FakeHost {
        let host = self
            .bare_host()
            .with(IDUNN_RUNTIME_CANDIDATE_BIND_ENVIRONMENT, BIND);
        if self.expected.write_lease_required {
            host.with(
                IDUNN_PROCESS_WRITE_LEASE_ENVIRONMENT,
                self.lease_path().to_str().unwrap(),
            )
        } else {
            host
        }
    }

    fn open_with(
        &self,
        host: &FakeHost,
        target: &str,
        health_contract: &str,
        credentials: IdunnRuntimeCredentials,
    ) -> Result<IdunnRuntimeAuthority> {
        IdunnRuntimeAuthority::open_in(host, target, health_contract, credentials)
    }

    fn open(&self, host: &FakeHost) -> Result<IdunnRuntimeAuthority> {
        self.open_with(host, TARGET, HEALTH_CONTRACT, self.credentials())
    }

    fn authority(&self) -> Result<IdunnRuntimeAuthority> {
        self.open(&self.host())
    }

    fn signer(&self) -> Result<IdunnRuntimePresenceSigner> {
        Ok(IdunnRuntimePresenceSigner::new(
            self.authority()?,
            capabilities(),
        ))
    }

    fn verified(&self) -> Result<VerifiedRuntimeAuthority> {
        verify_runtime_authority(
            &self.expected,
            &self.activation,
            &self.idunn.trust_anchor()?,
            &self.provider_public_key,
        )
    }

    /// Idunn's lease record at `path`, naming `warming_sha256`.
    #[cfg(unix)]
    fn write_lease_at(&self, path: &Path, warming_sha256: &str, epoch: u64) -> Result<()> {
        let lease = IdunnProcessWriteLeaseRecord {
            schema_version: IDUNN_PROCESS_WRITE_LEASE_SCHEMA.into(),
            target: TARGET.into(),
            expected_projection_sha256: self.expected.canonical_sha256()?,
            plan_id: self.expected.plan_id.clone(),
            incarnation_id: self.expected.incarnation_id.clone(),
            sealed_release_id: self.expected.sealed_release_id.clone(),
            activation_witness_sha256: self.activation.canonical_sha256()?,
            state_schema_generation: "world-v2".into(),
            state_contract_sha256: digest('7'),
            runtime_id: self.expected.runtime_id.clone(),
            runtime_instance_id: self.activation.runtime_instance_id.clone(),
            warming_presence_sha256: warming_sha256.into(),
            lease_epoch: epoch,
            issued_at_unix_millis: 101,
        };
        write_record(
            path,
            TARGET,
            IdunnProcessWriteLeaseRecord::TYPE,
            IDUNN_PROCESS_WRITE_LEASE_SCHEMA,
            lease.canonical_bytes()?,
        )
    }

    #[cfg(unix)]
    fn write_lease(&self, warming_sha256: &str, epoch: u64) -> Result<()> {
        self.write_lease_at(&self.lease_path(), warming_sha256, epoch)
    }
}

/// Runs `presence` through the shared verifier exactly as Odin would.
fn assert_odin_accepts(rig: &Rig, presence: &SignedRuntimePresence, now: u64) -> Result<()> {
    let authority = rig.verified()?;
    let claim = authenticate_runtime_presence_claim(
        &presence.document.payload,
        &authority,
        RuntimePresenceAuthenticationContext {
            trusted_received_at_unix_millis: now,
            maximum_age_millis: 100,
            maximum_future_skew_millis: 5,
        },
    )?;
    assert_eq!(claim.record(), &presence.record);
    let verified =
        correlate_runtime_presence_claim(claim, &authority)?.into_undisputed_present()?;
    assert_eq!(verified.signed_presence_sha256(), presence.canonical_sha256);
    Ok(())
}

// ---------------------------------------------------------------- signer tests

#[test]
fn signed_presence_authenticates_and_correlates() -> Result<()> {
    let rig = Rig::new(false)?;
    let mut signer = rig.signer()?;
    let warming = signer.sign_at("starting", 1_000)?;
    signer.report_health(RuntimePresenceState::Active);
    let active = signer.sign_at("ready", 1_001)?;

    for (presence, state, detail) in [
        (&warming, "warming", "starting"),
        (&active, "active", "ready"),
    ] {
        assert_odin_accepts(&rig, presence, 1_010)?;
        assert_eq!(presence.record.state, state);
        assert_eq!(presence.record.detail, detail);
        assert_eq!(presence.record.bound_endpoint.as_deref(), Some(ENDPOINT));
        assert_eq!(
            presence.document.payload,
            rmp_serde::to_vec(&presence.record)?
        );
        assert_eq!(
            presence.canonical_sha256,
            presence.record.canonical_sha256()?
        );
        assert_eq!(
            presence.document.schema_id,
            GAMECULT_RUNTIME_PRESENCE_HEALTH_SCHEMA
        );
        assert_eq!(presence.document.record_key, TARGET);
        assert_eq!(
            presence.document.source_runtime_id.as_deref(),
            Some("ghostlight-yggdrasil-1")
        );
        assert_eq!(
            presence.document.source_agent_id.as_deref(),
            Some(presence.record.signer_identity_id.as_str())
        );
        assert_eq!(
            presence.document.source_role.as_deref(),
            Some(DOCUMENT_ROLE)
        );
        assert_eq!(
            presence.document.tags.as_deref(),
            Some(
                &[
                    "cultnet.transport.rudp.v0".to_owned(),
                    "runtime-presence".to_owned()
                ][..]
            )
        );
    }
    assert_eq!(warming.document.stored_at, "1970-01-01T00:00:01+00:00");
    assert_eq!(signer.authority().expected().target, TARGET);
    assert_eq!(signer.authority().activation(), &rig.activation);
    assert_eq!(signer.authority().bound_endpoint(), Some(ENDPOINT));
    Ok(())
}

#[test]
fn sequence_strictly_increases_and_health_is_signer_owned() -> Result<()> {
    let rig = Rig::new(false)?;
    let mut signer = rig.signer()?;
    assert_eq!(signer.health(), RuntimePresenceState::Warming);
    let first = signer.sign_at("a", 1_000)?;
    assert_eq!(
        (first.record.state.as_str(), first.record.publisher_sequence),
        ("warming", 1)
    );
    let mut sequence = 1;
    for (health, text) in [
        (RuntimePresenceState::Active, "active"),
        (RuntimePresenceState::Degraded, "degraded"),
        (RuntimePresenceState::Failed, "failed"),
        (RuntimePresenceState::Warming, "warming"),
    ] {
        signer.report_health(health);
        assert_eq!(signer.health(), health);
        let signed = signer.sign_at("b", 1_000)?;
        sequence += 1;
        assert_eq!(signed.record.state, text);
        assert_eq!(signed.record.publisher_sequence, sequence);
        assert_odin_accepts(&rig, &signed, 1_010)?;
    }
    Ok(())
}

#[test]
fn detail_is_cut_to_512_bytes_on_a_character_boundary() -> Result<()> {
    let rig = Rig::new(false)?;
    let mut signer = rig.signer()?;
    let exact = "x".repeat(512);
    assert_eq!(signer.sign_at(&exact, 1_000)?.record.detail, exact);
    let over = "x".repeat(513);
    assert_eq!(signer.sign_at(&over, 1_000)?.record.detail, exact);
    let wide = "é".repeat(400);
    assert_eq!(signer.sign_at(&wide, 1_000)?.record.detail, "é".repeat(256));
    let odd = format!("x{}", "é".repeat(400));
    assert_eq!(
        signer.sign_at(&odd, 1_000)?.record.detail,
        format!("x{}", "é".repeat(255))
    );
    Ok(())
}

#[cfg(unix)]
#[test]
fn warming_never_carries_lease_and_active_requires_held_lease() -> Result<()> {
    let rig = Rig::new(true)?;
    let mut signer = rig.signer()?;
    let warming = signer.sign_at("awaiting-process-write-lease", 1_000)?;
    assert_eq!(warming.record.write_lease_sha256, None);

    signer.report_health(RuntimePresenceState::Active);
    let refused = refusal(signer.sign_at("ready", 1_001));
    assert!(
        refused.contains("lacks its process-write lease"),
        "{refused}"
    );
    // Degraded needs the lease as Active does, whatever the history.
    signer.report_health(RuntimePresenceState::Degraded);
    let refused = refusal(signer.sign_at("serving reduced", 1_001));
    assert!(
        refused.contains("degraded stateful presence lacks its process-write lease"),
        "{refused}"
    );
    // Failed is the honest report and needs none.
    signer.report_health(RuntimePresenceState::Failed);
    assert_eq!(
        signer
            .sign_at("no lease yet", 1_001)?
            .record
            .write_lease_sha256,
        None
    );

    rig.write_lease(&warming.canonical_sha256, 1)?;
    let guard = signer
        .try_acquire_write_lease()?
        .expect("the lease binds a warming proof");
    signer.report_health(RuntimePresenceState::Active);
    let active = signer.sign_at("ready", 1_002)?;
    assert_eq!(
        active.record.write_lease_sha256.as_deref(),
        Some(guard.lease_sha256())
    );
    assert_odin_accepts(&rig, &active, 1_010)?;

    signer.report_health(RuntimePresenceState::Degraded);
    assert_eq!(
        signer
            .sign_at("slow", 1_003)?
            .record
            .write_lease_sha256
            .as_deref(),
        Some(guard.lease_sha256())
    );
    signer.report_health(RuntimePresenceState::Warming);
    assert_eq!(
        signer.sign_at("again", 1_004)?.record.write_lease_sha256,
        None
    );
    Ok(())
}

#[test]
fn a_runtime_without_a_lease_requirement_signs_active_and_refuses_a_lease() -> Result<()> {
    let rig = Rig::new(false)?;
    let mut signer = rig.signer()?;
    signer.report_health(RuntimePresenceState::Active);
    assert_eq!(
        signer.sign_at("ready", 1_000)?.record.write_lease_sha256,
        None
    );
    let refused = refusal(signer.try_acquire_write_lease());
    assert!(refused.contains("does not require"), "{refused}");
    Ok(())
}

#[cfg(unix)]
#[test]
fn lease_must_bind_a_recent_warming_proof_of_this_signer() -> Result<()> {
    let rig = Rig::new(true)?;
    let mut signer = rig.signer()?;
    assert!(signer.try_acquire_write_lease()?.is_none(), "no record yet");

    let mut warming = Vec::new();
    for index in 0..65 {
        warming.push(
            signer
                .sign_at(&format!("heartbeat {index}"), 1_000)?
                .canonical_sha256,
        );
    }
    signer.report_health(RuntimePresenceState::Failed);
    let not_warming = signer.sign_at("failed", 1_000)?.canonical_sha256;

    // A proof some other signer signed, a presence that was never Warming and a
    // Warming proof pushed out of the last 64 are all refused.
    for (epoch, sha) in [(1, digest('d')), (2, not_warming), (3, warming[0].clone())] {
        rig.write_lease(&sha, epoch)?;
        let refused = refusal(signer.try_acquire_write_lease());
        assert!(
            refused.contains("provider-owned Warming proof"),
            "{refused}"
        );
    }

    // The oldest proof still inside the window is accepted.
    rig.write_lease(&warming[1], 4)?;
    assert!(signer.try_acquire_write_lease()?.is_some());
    Ok(())
}

#[cfg(unix)]
#[test]
fn a_lease_for_another_runtime_instance_is_refused() -> Result<()> {
    let rig = Rig::new(true)?;
    let mut signer = rig.signer()?;
    let warming = signer.sign_at("w", 1_000)?;
    rig.write_lease(&warming.canonical_sha256, 1)?;
    let stored = SingleFileMessagePackBackingStore::new(rig.lease_path())
        .pull_all()?
        .remove(0);
    let mut lease = IdunnProcessWriteLeaseRecord::decode_canonical(&stored.payload)?;
    lease.runtime_instance_id = digest('1');
    write_record(
        &rig.lease_path(),
        TARGET,
        IdunnProcessWriteLeaseRecord::TYPE,
        IDUNN_PROCESS_WRITE_LEASE_SCHEMA,
        lease.canonical_bytes()?,
    )?;
    let refused = refusal(signer.try_acquire_write_lease());
    assert!(
        refused.contains("provider-owned Warming proof"),
        "{refused}"
    );
    Ok(())
}

#[cfg(unix)]
#[test]
fn lease_guard_holds_shared_lock_and_detects_replacement() -> Result<()> {
    let rig = Rig::new(true)?;
    let mut signer = rig.signer()?;
    let warming = signer.sign_at("w", 1_000)?;
    rig.write_lease(&warming.canonical_sha256, 1)?;
    let guard = signer.try_acquire_write_lease()?.unwrap();
    assert_eq!(
        signer.try_acquire_write_lease()?.unwrap().lease_sha256(),
        guard.lease_sha256(),
        "acquiring again returns the held lease"
    );
    guard.require_current()?;

    let probe = OpenOptions::new().read(true).open(rig.lock_path())?;
    assert!(
        fs2::FileExt::try_lock_exclusive(&probe).is_err(),
        "a guard holds the lock shared"
    );
    fs2::FileExt::try_lock_shared(&probe)?;
    fs2::FileExt::unlock(&probe)?;

    // The lock outlives every clone but the last.
    let clone = guard.clone();
    drop(guard);
    assert!(fs2::FileExt::try_lock_exclusive(&probe).is_err());

    // A writer that renames a new record into place is detected.
    let replacement = rig.dir.path().join("replacement.cc");
    rig.write_lease_at(&replacement, &warming.canonical_sha256, 2)?;
    std::fs::rename(&replacement, rig.lease_path())?;
    let stale = refusal(clone.require_current());
    assert!(stale.contains("no longer the admitted"), "{stale}");
    signer.report_health(RuntimePresenceState::Active);
    assert!(
        signer.sign_at("ready", 1_001).is_err(),
        "a stale lease signs nothing but Warming"
    );

    drop(clone);
    drop(signer);
    fs2::FileExt::try_lock_exclusive(&probe)?;
    Ok(())
}

#[cfg(unix)]
#[test]
fn a_removed_lease_record_is_not_current() -> Result<()> {
    let rig = Rig::new(true)?;
    let mut signer = rig.signer()?;
    let warming = signer.sign_at("w", 1_000)?;
    rig.write_lease(&warming.canonical_sha256, 1)?;
    let guard = signer.try_acquire_write_lease()?.unwrap();
    std::fs::remove_file(rig.lease_path())?;
    let stale = refusal(guard.require_current());
    assert!(stale.contains("no longer the admitted"), "{stale}");
    Ok(())
}

#[cfg(unix)]
#[test]
fn a_lost_lease_still_signs_failed() -> Result<()> {
    let rig = Rig::new(true)?;
    let mut signer = rig.signer()?;
    let warming = signer.sign_at("warming", 1_000)?;
    rig.write_lease(&warming.canonical_sha256, 1)?;
    let guard = signer.try_acquire_write_lease()?.expect("the lease binds");
    signer.report_health(RuntimePresenceState::Active);
    let held = signer.sign_at("held", 1_001)?;
    assert_eq!(
        held.record.write_lease_sha256.as_deref(),
        Some(guard.lease_sha256())
    );
    // While the lease is current, Failed carries its digest like any other state.
    signer.report_health(RuntimePresenceState::Failed);
    assert_eq!(
        signer
            .sign_at("failing", 1_001)?
            .record
            .write_lease_sha256
            .as_deref(),
        Some(guard.lease_sha256())
    );

    // Idunn withdraws the lease: Active and Degraded can no longer be claimed.
    std::fs::remove_file(rig.lease_path())?;
    for state in [RuntimePresenceState::Active, RuntimePresenceState::Degraded] {
        signer.report_health(state);
        let refused = refusal(signer.sign_at("lost", 1_002));
        assert!(
            refused.contains("no longer the admitted"),
            "{state:?}: {refused}"
        );
    }
    // Failed is the honest report, and it carries no lease digest.
    signer.report_health(RuntimePresenceState::Failed);
    let failed = signer.sign_at("lease lost", 1_003)?;
    assert_eq!(failed.record.state, "failed");
    assert_eq!(failed.record.write_lease_sha256, None);
    let authority = rig.verified()?;
    let claim = authenticate_runtime_presence_claim(
        &failed.document.payload,
        &authority,
        RuntimePresenceAuthenticationContext {
            trusted_received_at_unix_millis: 1_010,
            maximum_age_millis: 100,
            maximum_future_skew_millis: 5,
        },
    )?;
    correlate_runtime_presence_claim(claim, &authority)?;
    // Warming still signs, as before.
    signer.report_health(RuntimePresenceState::Warming);
    signer.sign_at("again", 1_004)?;
    Ok(())
}

// ------------------------------------------------------------- authority tests

#[test]
fn authority_open_refuses_mismatches() -> Result<()> {
    let rig = Rig::new(false)?;
    rig.authority()?;

    let refused = refusal(rig.open_with(&rig.host(), "other", HEALTH_CONTRACT, rig.credentials()));
    assert!(refused.contains("target or health contract"), "{refused}");
    let refused = refusal(rig.open_with(&rig.host(), TARGET, "other.v1", rig.credentials()));
    assert!(refused.contains("target or health contract"), "{refused}");

    let mut wrong_seed = rig.activation_seed.clone();
    wrong_seed[0] ^= 1;
    let credentials = IdunnRuntimeCredentials::new(
        Cursor::new(wrong_seed),
        Cursor::new(rig.provider_credential.clone()),
    );
    let refused = refusal(rig.open_with(&rig.host(), TARGET, HEALTH_CONTRACT, credentials));
    assert!(
        refused.contains("activation credential does not belong"),
        "{refused}"
    );

    let other = tempfile::tempdir()?;
    enroll_service_identity_at::<GameCultProviderHealthIdentity>(&other.path().join("p.cc"))?;
    let credentials = IdunnRuntimeCredentials::new(
        Cursor::new(rig.activation_seed.clone()),
        Cursor::new(std::fs::read(other.path().join("p.cc"))?),
    );
    let refused = refusal(rig.open_with(&rig.host(), TARGET, HEALTH_CONTRACT, credentials));
    assert!(
        refused.contains("not the identity selected by Expected"),
        "{refused}"
    );

    // A lease environment on an incarnation that needs none is refused.
    let lease_env = rig
        .host()
        .with(IDUNN_PROCESS_WRITE_LEASE_ENVIRONMENT, "/run/lease.cc");
    let refused = refusal(rig.open(&lease_env));
    assert!(
        refused.contains("write-lease environment does not match"),
        "{refused}"
    );
    let empty = rig.host().with(IDUNN_PROCESS_WRITE_LEASE_ENVIRONMENT, "");
    assert!(refusal(rig.open(&empty)).contains("empty or has surrounding whitespace"));
    Ok(())
}

#[cfg(unix)]
#[test]
fn a_lease_requirement_needs_the_lease_environment() -> Result<()> {
    let rig = Rig::new(true)?;
    rig.authority()?;
    let no_lease_env = rig
        .bare_host()
        .with(IDUNN_RUNTIME_CANDIDATE_BIND_ENVIRONMENT, BIND);
    let refused = refusal(rig.open(&no_lease_env));
    assert!(
        refused.contains("write-lease environment does not match"),
        "{refused}"
    );
    Ok(())
}

#[cfg(not(unix))]
#[test]
fn non_unix_open_refuses_a_write_lease_requirement() -> Result<()> {
    let rig = Rig::new(true)?;
    let refused = refusal(rig.authority());
    assert!(refused.contains("write_lease_required"), "{refused}");
    assert!(
        !refused.contains(rig.dir.path().to_str().unwrap()),
        "{refused}"
    );
    Ok(())
}

#[test]
fn open_reads_the_whole_launch_environment() -> Result<()> {
    let rig = Rig::new(false)?;
    // The bundle comes only from the launch environment: with it missing, open has
    // nowhere to look, whatever else is set.
    let no_bundle = FakeHost::new().with(IDUNN_RUNTIME_CANDIDATE_BIND_ENVIRONMENT, BIND);
    let refused = refusal(rig.open(&no_bundle));
    assert!(
        refused.contains(IDUNN_RUNTIME_BUNDLE_ENVIRONMENT),
        "{refused}"
    );
    let relative = rig
        .host()
        .with(IDUNN_RUNTIME_BUNDLE_ENVIRONMENT, "relative/bundle");
    let refused = refusal(rig.open(&relative));
    assert!(refused.contains("not an absolute path"), "{refused}");
    // The bundle, the candidate bind and (below, unix) the write lease are all
    // read through the one host port.
    let authority = rig.open(&rig.host())?;
    authority.require_candidate_bind(BIND)?;
    Ok(())
}

#[test]
fn the_launch_contract_names_are_the_public_spellings() {
    assert_eq!(
        IDUNN_RUNTIME_BUNDLE_ENVIRONMENT,
        "GAMECULT_IDUNN_RUNTIME_BUNDLE"
    );
    assert_eq!(
        IDUNN_RUNTIME_CANDIDATE_BIND_ENVIRONMENT,
        "GAMECULT_IDUNN_CANDIDATE_BIND"
    );
    assert_eq!(
        IDUNN_PROCESS_WRITE_LEASE_ENVIRONMENT,
        "GAMECULT_IDUNN_PROCESS_WRITE_LEASE"
    );
    assert_eq!(
        IDUNN_ACTIVATION_CREDENTIAL_ENVIRONMENT,
        "GAMECULT_IDUNN_ACTIVATION_CREDENTIAL"
    );
    assert_eq!(
        RUNTIME_PRESENCE_IDENTITY_BINDING,
        "GAMECULT_RUNTIME_PRESENCE_IDENTITY"
    );
    assert_eq!(
        RUNTIME_PRESENCE_IDENTITY_FD_NAME,
        "gamecult-runtime-presence-identity"
    );
}

#[test]
fn authority_open_refuses_an_activation_for_another_expected() -> Result<()> {
    let rig = Rig::new(false)?;
    let mut other_expected = rig.expected.clone();
    other_expected.plan_id = digest('9');
    let (foreign, _) = Rig::issue(&other_expected, &rig.idunn)?;
    write_record(
        &rig.bundle().join("activation.cc"),
        TARGET,
        IdunnRuntimeActivationRecord::TYPE,
        IDUNN_RUNTIME_ACTIVATION_SCHEMA,
        foreign.canonical_bytes()?,
    )?;
    let refused = refusal(rig.authority());
    assert!(
        refused.contains("does not bind the bundled Expected"),
        "{refused}"
    );
    Ok(())
}

#[test]
fn authority_open_refuses_a_substituted_record_key() -> Result<()> {
    let rig = Rig::new(false)?;
    let substitute = rig.dir.path().join("substitute.cc");
    write_record(
        &substitute,
        "someone-else",
        IdunnExpectedIncarnationRecord::TYPE,
        IDUNN_EXPECTED_INCARNATION_SCHEMA,
        rig.expected.canonical_bytes()?,
    )?;
    std::fs::rename(&substitute, rig.bundle().join("expected.cc"))?;
    let refused = refusal(rig.authority());
    assert!(refused.contains("key is not its target"), "{refused}");
    Ok(())
}

#[test]
fn candidate_bind_must_equal_expected_route() -> Result<()> {
    let rig = Rig::new(false)?;
    let authority = rig.authority()?;
    authority.require_candidate_bind(BIND)?;
    assert!(authority.require_candidate_bind("127.0.0.1:14104").is_err());
    assert!(authority.require_candidate_bind(ENDPOINT).is_err());
    assert!(authority.require_candidate_bind("").is_err());
    assert_eq!(authority.bound_endpoint(), Some(ENDPOINT));

    // Idunn's environment must itself name Expected's route.
    let bind = |value: &str| {
        rig.bare_host()
            .with(IDUNN_RUNTIME_CANDIDATE_BIND_ENVIRONMENT, value)
    };
    assert!(refusal(rig.open(&bind("127.0.0.1:14104"))).contains("candidate bind differs"));
    let missing = rig.bare_host();
    assert!(refusal(rig.open(&missing)).contains("candidate bind environment"));
    assert!(refusal(rig.open(&bind(" 127.0.0.1:14103"))).contains("surrounding whitespace"));
    Ok(())
}

#[test]
fn candidate_bind_suffix_is_refused_at_open() -> Result<()> {
    let rig = Rig::new(false)?;
    // Idunn passes the bare host:port; only the whole of Expected's endpoint
    // after `tcp://` is that bind. A suffix or prefix of it is not.
    for wrong in [
        "0.0.1:14103",
        "1:14103",
        "127.0.0.1:1410",
        "tcp://127.0.0.1:14103",
    ] {
        let host = rig
            .bare_host()
            .with(IDUNN_RUNTIME_CANDIDATE_BIND_ENVIRONMENT, wrong);
        let refused = refusal(rig.open(&host));
        assert!(
            refused.contains("candidate bind differs"),
            "{wrong}: {refused}"
        );
    }
    let authority = rig.authority()?;
    assert!(authority.require_candidate_bind("0.0.1:14103").is_err());
    Ok(())
}

#[test]
fn a_route_less_incarnation_has_no_bind_to_check() -> Result<()> {
    let mut rig = Rig::new(false)?;
    rig.expected.route = None;
    let (activation, seed) = Rig::issue(&rig.expected, &rig.idunn)?;
    rig.activation = activation;
    rig.activation_seed = seed;
    rig.write_bundle(&rig.expected, &rig.activation)?;
    let authority = rig.open(&rig.bare_host())?;
    assert_eq!(authority.bound_endpoint(), None);
    assert!(authority.require_candidate_bind(BIND).is_err());
    assert!(refusal(rig.open(&rig.host())).contains("candidate bind environment"));
    let mut signer = IdunnRuntimePresenceSigner::new(authority, capabilities());
    assert_eq!(signer.sign_at("w", 1_000)?.record.bound_endpoint, None);
    Ok(())
}

// --------------------------------------------------------------- route answer

fn strings(items: &[&str]) -> Vec<String> {
    items.iter().map(|item| (*item).to_owned()).collect()
}

fn request(id: &str, schemas: Option<&[&str]>, keys: Option<&[&str]>) -> CultNetMessage {
    CultNetMessage::SnapshotRequest {
        message_id: id.into(),
        schema_ids: schemas.map(strings),
        record_keys: keys.map(strings),
    }
}

fn presence_in(answer: CultNetMessage) -> GameCultRuntimePresenceHealthRecord {
    let CultNetMessage::SnapshotResponseRaw { documents, .. } = answer else {
        panic!("a snapshot response");
    };
    let [document] = documents.as_slice() else {
        panic!("one presence document");
    };
    rmp_serde::from_slice(&document.payload).unwrap()
}

#[test]
fn route_observation_refuses_foreign_requests() -> Result<()> {
    let rig = Rig::new(false)?;
    let mut signer = rig.signer()?;
    signer.report_health(RuntimePresenceState::Degraded);
    let schema = GAMECULT_RUNTIME_PRESENCE_HEALTH_SCHEMA;

    let answer =
        signer.answer_route_observation(&request("m-1", Some(&[schema]), Some(&[TARGET])))?;
    let CultNetMessage::SnapshotResponseRaw { message_id, .. } = &answer else {
        panic!("a snapshot response");
    };
    assert_eq!(message_id, "m-1");
    let record = presence_in(answer);
    assert_eq!(
        record.state, "degraded",
        "the answer states the reported health"
    );
    assert_eq!(record.detail, "route-observation:m-1");
    assert_eq!(record.publisher_sequence, 1);
    // An open request and a request listing more than this runtime are fine.
    signer.answer_route_observation(&request("m-2", None, None))?;
    signer.answer_route_observation(&request("m-3", Some(&["x", schema]), Some(&["y", TARGET])))?;

    for refused in [
        request("m", Some(&["other.schema.v1"]), None),
        request("m", None, Some(&["another-target"])),
        request("m", Some(&[schema]), Some(&["another-target"])),
        request("m", Some(&["other.schema.v1"]), Some(&[TARGET])),
        request("", None, None),
        request(" m", None, None),
        request("m ", None, None),
        CultNetMessage::SnapshotResponseRaw {
            message_id: "m".into(),
            documents: Vec::new(),
        },
    ] {
        assert!(
            signer.answer_route_observation(&refused).is_err(),
            "{refused:?}"
        );
    }
    // Refusals sign nothing: the next answer takes the next sequence.
    let record = presence_in(signer.answer_route_observation(&request("m-4", None, None))?);
    assert_eq!(record.publisher_sequence, 4);
    Ok(())
}

#[cfg(unix)]
#[test]
fn route_observation_answers_in_lease_discipline() -> Result<()> {
    let rig = Rig::new(true)?;
    let mut signer = rig.signer()?;
    signer.report_health(RuntimePresenceState::Active);
    assert!(
        signer
            .answer_route_observation(&request("m", None, None))
            .is_err()
    );
    Ok(())
}

// -------------------------------------------------------- descriptor contract

#[cfg(unix)]
fn clear_cloexec(file: &File) {
    // SAFETY: fcntl on a descriptor `file` owns.
    assert_ne!(
        unsafe { libc::fcntl(file.as_raw_fd(), libc::F_SETFD, 0) },
        -1
    );
}

#[cfg(unix)]
fn set_cloexec(file: &File) {
    // SAFETY: fcntl on a descriptor `file` owns.
    assert_ne!(
        unsafe { libc::fcntl(file.as_raw_fd(), libc::F_SETFD, libc::FD_CLOEXEC) },
        -1
    );
}

#[cfg(unix)]
fn is_cloexec(file: &File) -> bool {
    // SAFETY: fcntl on a descriptor `file` owns.
    let flags = unsafe { libc::fcntl(file.as_raw_fd(), libc::F_GETFD) };
    flags != -1 && flags & libc::FD_CLOEXEC != 0
}

#[cfg(unix)]
fn inherited(dir: &Path, name: &str, content: &[u8]) -> Result<File> {
    std::fs::write(dir.join(name), content)?;
    let file = File::open(dir.join(name))?;
    clear_cloexec(&file);
    assert!(!is_cloexec(&file));
    Ok(file)
}

#[cfg(unix)]
fn systemd_host(dir: &Path, listen_pid: &str, fds: &str, names: &str) -> Result<FakeHost> {
    let mut host = FakeHost::new()
        .with(LISTEN_PID_ENVIRONMENT, listen_pid)
        .with(LISTEN_FDS_ENVIRONMENT, fds)
        .with(LISTEN_FDNAMES_ENVIRONMENT, names);
    for (index, name) in names.split(':').enumerate() {
        let descriptor = LISTEN_FDS_START + index as RawFd;
        host.descriptors.insert(
            descriptor,
            inherited(dir, &format!("fd-{index}"), name.as_bytes())?,
        );
    }
    Ok(host)
}

#[cfg(unix)]
fn contents(mut file: File) -> Result<String> {
    let mut text = String::new();
    file.read_to_string(&mut text)?;
    Ok(text)
}

#[cfg(unix)]
#[test]
fn systemd_descriptor_contract() -> Result<()> {
    let dir = tempfile::tempdir()?;
    let names = format!("socket:{ACTIVATION_NAME}:extra:{PROVIDER_NAME}:more");

    // Found by name; extra descriptors are tolerated and left alone.
    let mut host = systemd_host(dir.path(), "4242", "5", &names)?;
    // A descriptor that already carries close-on-exec keeps it.
    set_cloexec(&host.descriptors[&(LISTEN_FDS_START + 3)]);
    let (activation, provider) = take_systemd_descriptors(&mut host, Custody::Profile)?;
    assert!(
        is_cloexec(&activation) && is_cloexec(&provider),
        "signers never reach a child"
    );
    assert_eq!(contents(activation)?, ACTIVATION_NAME);
    assert_eq!(contents(provider)?, PROVIDER_NAME);
    assert!(host.vars.is_empty(), "LISTEN_* leave the environment");
    assert_eq!(host.descriptors.len(), 3, "only the two signers were taken");

    // Order does not matter.
    let swapped = format!("{PROVIDER_NAME}:{ACTIVATION_NAME}");
    let mut host = systemd_host(dir.path(), "4242", "2", &swapped)?;
    let (activation, provider) = take_systemd_descriptors(&mut host, Custody::Profile)?;
    assert_eq!(contents(activation)?, ACTIVATION_NAME);
    assert_eq!(contents(provider)?, PROVIDER_NAME);

    // A claim for another process is refused and still cleared from the environment.
    let both = format!("{ACTIVATION_NAME}:{PROVIDER_NAME}");
    let mut host = systemd_host(dir.path(), "999", "2", &both)?;
    assert!(
        refusal(take_systemd_descriptors(&mut host, Custody::Profile))
            .contains("different process")
    );
    assert!(host.vars.is_empty());

    // PrivatePIDs: pid 1 accepts the host's LISTEN_PID, a descendant does not.
    let mut host = systemd_host(dir.path(), "777", "2", &both)?;
    host.pid = 1;
    take_systemd_descriptors(&mut host, Custody::Profile)?;
    let mut host = systemd_host(dir.path(), "777", "2", &both)?;
    host.pid = 2;
    assert!(take_systemd_descriptors(&mut host, Custody::Profile).is_err());
    Ok(())
}

#[cfg(unix)]
#[test]
fn systemd_listen_pid_rule() {
    assert!(listen_pid_matches("4242", 4242));
    assert!(!listen_pid_matches("4243", 4242));
    // PrivatePIDs: the main process is pid 1 and cannot see the host pid.
    assert!(listen_pid_matches("777", 1));
    assert!(listen_pid_matches("1", 1));
    assert!(
        !listen_pid_matches("777", 2),
        "a descendant must not reuse the set"
    );
    assert!(!listen_pid_matches("0", 1));
    // The text must be the canonical number.
    assert!(!listen_pid_matches("04242", 4242));
    assert!(!listen_pid_matches("+4242", 4242));
    assert!(!listen_pid_matches("", 1));
    assert!(!listen_pid_matches("abc", 1));
}

#[cfg(unix)]
#[test]
fn systemd_descriptor_contract_refuses_malformed_claims() -> Result<()> {
    let dir = tempfile::tempdir()?;
    let both = format!("{ACTIVATION_NAME}:{PROVIDER_NAME}");
    let only_activation = ACTIVATION_NAME.to_owned();
    let doubled = format!("{ACTIVATION_NAME}:{PROVIDER_NAME}:{PROVIDER_NAME}");
    for (fds, names, expect) in [
        (
            "2",
            only_activation.as_str(),
            "LISTEN_FDNAMES does not name",
        ),
        (
            "1",
            only_activation.as_str(),
            "did not pass the gamecult-runtime-presence-identity",
        ),
        ("3", doubled.as_str(), "more than once"),
        ("2", "a:b", "did not pass the gamecult-idunn"),
        ("02", both.as_str(), "LISTEN_FDS"),
        ("x", both.as_str(), "LISTEN_FDS"),
        ("3", both.as_str(), "does not name every"),
    ] {
        let mut host = systemd_host(dir.path(), "4242", "2", &both)?;
        host.vars.insert(LISTEN_FDS_ENVIRONMENT.into(), fds.into());
        host.vars
            .insert(LISTEN_FDNAMES_ENVIRONMENT.into(), names.into());
        let refused = refusal(take_systemd_descriptors(&mut host, Custody::Profile));
        assert!(refused.contains(expect), "{names}: {refused}");
        assert!(host.vars.is_empty());
    }
    for missing in [
        LISTEN_PID_ENVIRONMENT,
        LISTEN_FDS_ENVIRONMENT,
        LISTEN_FDNAMES_ENVIRONMENT,
    ] {
        let mut host = systemd_host(dir.path(), "4242", "2", &both)?;
        host.vars.remove(missing);
        assert!(refusal(take_systemd_descriptors(&mut host, Custody::Profile)).contains(missing));
        assert!(host.vars.is_empty(), "the rest of the claim is removed too");
    }
    // A descriptor the process does not have open is refused.
    let mut host = systemd_host(dir.path(), "4242", "2", &both)?;
    host.descriptors.clear();
    assert!(refusal(take_systemd_descriptors(&mut host, Custody::Profile)).contains("not open"));
    Ok(())
}

#[cfg(unix)]
#[test]
fn systemd_descriptors_must_be_read_only() -> Result<()> {
    let dir = tempfile::tempdir()?;
    let both = format!("{ACTIVATION_NAME}:{PROVIDER_NAME}");
    let mut host = systemd_host(dir.path(), "4242", "2", &both)?;
    let writable = OpenOptions::new()
        .read(true)
        .write(true)
        .open(dir.path().join("fd-1"))?;
    host.descriptors.insert(LISTEN_FDS_START + 1, writable);
    let refused = refusal(take_systemd_descriptors(&mut host, Custody::Profile));
    assert!(refused.contains("not read-only"), "{refused}");
    Ok(())
}

#[cfg(unix)]
#[test]
fn write_only_pipe_descriptor_is_not_read_only() -> Result<()> {
    let mut fds = [0 as libc::c_int; 2];
    // SAFETY: pipe writes two descriptors into the array.
    assert_eq!(unsafe { libc::pipe(fds.as_mut_ptr()) }, 0);
    // SAFETY: both descriptors were just created and are owned exactly once.
    let (read_end, write_end) = unsafe { (File::from_raw_fd(fds[0]), File::from_raw_fd(fds[1])) };
    // SAFETY: F_GETFL reads the flags of a descriptor `write_end` owns.
    let status = unsafe { libc::fcntl(write_end.as_raw_fd(), libc::F_GETFL) };
    assert_eq!(
        status & libc::O_ACCMODE,
        libc::O_WRONLY,
        "a valid status of 1"
    );
    // F_GETFL succeeds here, so the refusal is the access mode and not a failed read.
    let refused = refusal(protect_descriptor(
        write_end,
        "probe signer",
        Custody::Profile,
    ));
    assert!(
        refused.contains("probe signer descriptor is not read-only"),
        "{refused}"
    );
    assert!(
        protect_descriptor(read_end, "probe signer", Custody::Profile).is_ok(),
        "the read end of the same pipe is read-only"
    );
    Ok(())
}

#[cfg(unix)]
#[test]
fn systemd_descriptors_pass_the_root_custody_rule_or_are_refused() -> Result<()> {
    let dir = tempfile::tempdir()?;
    let both = format!("{ACTIVATION_NAME}:{PROVIDER_NAME}");
    // The temp files are mode 0644, never root's 0400.
    let mut host = systemd_host(dir.path(), "4242", "2", &both)?;
    let refused = refusal(take_systemd_descriptors(&mut host, Custody::Root));
    assert!(refused.contains("root-owned 0400"), "{refused}");
    // The public delivery is root custody, with no way to ask for less.
    let mut host = systemd_host(dir.path(), "4242", "2", &both)?;
    let refused = refusal(IdunnRuntimeCredentials::from_systemd_descriptors_in(
        &mut host,
    ));
    assert!(refused.contains("root-owned 0400"), "{refused}");
    Ok(())
}

#[test]
fn custody_follows_delivery() -> Result<()> {
    let rig = Rig::new(false)?;
    assert_eq!(rig.credentials().custody, Custody::Profile, "new");

    let dir = tempfile::tempdir()?;
    let seed = dir.path().join("activation-key");
    let identity = dir.path().join("identity.cc");
    std::fs::write(&seed, &rig.activation_seed)?;
    std::fs::write(&identity, &rig.provider_credential)?;
    let host = FakeHost::new()
        .with(
            IDUNN_ACTIVATION_CREDENTIAL_ENVIRONMENT,
            seed.to_str().unwrap(),
        )
        .with(
            RUNTIME_PRESENCE_IDENTITY_BINDING,
            identity.to_str().unwrap(),
        );
    assert_eq!(
        IdunnRuntimeCredentials::from_host_environment_in(&host)?.custody,
        Custody::Profile,
        "host files"
    );

    // Profile custody takes the real thing and nothing else, on every platform.
    for (kind, rule, accepted) in [
        (FileKind::Directory, Rule::ReadOnlyDirectory, true),
        (FileKind::Regular, Rule::ReadOnlyFile, true),
        (FileKind::Regular, Rule::ControlledFile, true),
        (FileKind::Symlink, Rule::ReadOnlyDirectory, false),
        (FileKind::Symlink, Rule::ReadOnlyFile, false),
        (FileKind::Symlink, Rule::ControlledFile, false),
        (FileKind::Other, Rule::ReadOnlyFile, false),
        (FileKind::Regular, Rule::ReadOnlyDirectory, false),
        (FileKind::Directory, Rule::ReadOnlyFile, false),
    ] {
        assert_eq!(profile_accepts(kind, rule), accepted, "{kind:?} {rule:?}");
    }
    Ok(())
}

#[test]
fn profile_custody_reads_the_real_filesystem() -> Result<()> {
    let dir = tempfile::tempdir()?;
    let file = dir.path().join("record.cc");
    std::fs::write(&file, b"x")?;
    let custody = Custody::Profile;
    custody.require_file(&file, "record", Rule::ReadOnlyFile)?;
    custody.require_file(dir.path(), "dir", Rule::ReadOnlyDirectory)?;
    // The wrong kind, an absent path and a relative path are refused.
    let refused = refusal(custody.require_file(dir.path(), "dir", Rule::ReadOnlyFile));
    assert!(refused.contains("dir is not a regular file"), "{refused}");
    assert!(
        custody
            .require_file(&file, "record", Rule::ReadOnlyDirectory)
            .is_err()
    );
    assert!(
        custody
            .require_file(&dir.path().join("absent"), "absent", Rule::ReadOnlyFile)
            .is_err()
    );
    assert!(
        custody
            .require_file(Path::new("record.cc"), "record", Rule::ReadOnlyFile)
            .is_err()
    );
    assert_eq!(
        FileKind::of(&std::fs::symlink_metadata(&file)?),
        FileKind::Regular
    );
    assert_eq!(
        FileKind::of(&std::fs::symlink_metadata(dir.path())?),
        FileKind::Directory
    );
    Ok(())
}

#[cfg(unix)]
#[test]
fn profile_custody_refuses_a_symlink() -> Result<()> {
    let dir = tempfile::tempdir()?;
    let file = dir.path().join("record.cc");
    std::fs::write(&file, b"x")?;
    let link = dir.path().join("link.cc");
    std::os::unix::fs::symlink(&file, &link)?;
    let refused = refusal(Custody::Profile.require_file(&link, "record", Rule::ReadOnlyFile));
    assert!(refused.contains("a link is refused"), "{refused}");
    assert_eq!(
        FileKind::of(&std::fs::symlink_metadata(&link)?),
        FileKind::Symlink
    );
    let dir_link = dir.path().join("dir-link");
    std::os::unix::fs::symlink(dir.path(), &dir_link)?;
    assert!(
        Custody::Profile
            .require_file(&dir_link, "dir", Rule::ReadOnlyDirectory)
            .is_err()
    );
    // A bundle that is a symlink is refused by open, not followed.
    let rig = Rig::new(false)?;
    let bundle_link = rig.dir.path().join("bundle-link");
    std::os::unix::fs::symlink(rig.bundle(), &bundle_link)?;
    let host = rig.host().with(
        IDUNN_RUNTIME_BUNDLE_ENVIRONMENT,
        bundle_link.to_str().unwrap(),
    );
    let refused = refusal(rig.open(&host));
    assert!(
        refused.contains("Idunn runtime bundle is not a directory"),
        "{refused}"
    );
    Ok(())
}

#[cfg(unix)]
#[test]
fn credentials_helper_wires_the_systemd_signers() -> Result<()> {
    let rig = Rig::new(false)?;
    let dir = tempfile::tempdir()?;
    let both = format!("{ACTIVATION_NAME}:{PROVIDER_NAME}");
    let mut host = systemd_host(dir.path(), "4242", "2", &both)?;
    host.descriptors.insert(
        LISTEN_FDS_START,
        inherited(dir.path(), "seed", &rig.activation_seed)?,
    );
    host.descriptors.insert(
        LISTEN_FDS_START + 1,
        inherited(dir.path(), "identity", &rig.provider_credential)?,
    );
    // The temp files are not root's, so the test takes the descriptors under profile
    // custody and wraps them as any two readers.
    let (activation, provider) = take_systemd_descriptors(&mut host, Custody::Profile)?;
    let credentials = IdunnRuntimeCredentials::new(activation, provider);
    let authority = rig.open_with(&rig.host(), TARGET, HEALTH_CONTRACT, credentials)?;
    assert_eq!(authority.expected().target, TARGET);
    Ok(())
}

// ------------------------------------------------------------ host credentials

#[test]
fn host_activation_credential_is_one_shot() -> Result<()> {
    let rig = Rig::new(false)?;
    let dir = tempfile::tempdir()?;
    let seed_path = dir.path().join("activation-key");
    let identity_path = dir.path().join("identity.cc");
    std::fs::write(&seed_path, &rig.activation_seed)?;
    std::fs::write(&identity_path, &rig.provider_credential)?;
    let host = FakeHost::new()
        .with(
            IDUNN_ACTIVATION_CREDENTIAL_ENVIRONMENT,
            seed_path.to_str().unwrap(),
        )
        .with(
            RUNTIME_PRESENCE_IDENTITY_BINDING,
            identity_path.to_str().unwrap(),
        );

    let credentials = IdunnRuntimeCredentials::from_host_environment_in(&host)?;
    assert!(
        !seed_path.exists(),
        "the seed lives only in this process now"
    );
    assert!(
        identity_path.exists(),
        "the provider identity is stable and stays"
    );
    let authority = rig.open_with(&rig.host(), TARGET, HEALTH_CONTRACT, credentials)?;
    assert_eq!(authority.activation(), &rig.activation);
    // The file is gone, so a second launch from the same delivery cannot read it.
    assert!(IdunnRuntimeCredentials::from_host_environment_in(&host).is_err());
    Ok(())
}

#[test]
fn host_credentials_refuse_missing_or_relative_paths() -> Result<()> {
    let dir = tempfile::tempdir()?;
    let seed = dir.path().join("seed");
    std::fs::write(&seed, [1u8; 32])?;
    let identity = dir.path().join("identity.cc");
    std::fs::write(&identity, b"x")?;
    let host = FakeHost::new().with(
        IDUNN_ACTIVATION_CREDENTIAL_ENVIRONMENT,
        seed.to_str().unwrap(),
    );
    assert!(
        refusal(IdunnRuntimeCredentials::from_host_environment_in(&host))
            .contains(RUNTIME_PRESENCE_IDENTITY_BINDING)
    );
    let host = FakeHost::new().with(
        RUNTIME_PRESENCE_IDENTITY_BINDING,
        identity.to_str().unwrap(),
    );
    assert!(
        refusal(IdunnRuntimeCredentials::from_host_environment_in(&host))
            .contains(IDUNN_ACTIVATION_CREDENTIAL_ENVIRONMENT)
    );
    let host = FakeHost::new()
        .with(IDUNN_ACTIVATION_CREDENTIAL_ENVIRONMENT, "relative-seed")
        .with(
            RUNTIME_PRESENCE_IDENTITY_BINDING,
            identity.to_str().unwrap(),
        );
    assert!(
        refusal(IdunnRuntimeCredentials::from_host_environment_in(&host)).contains("not absolute")
    );
    assert!(seed.exists(), "nothing was consumed by a refused delivery");
    Ok(())
}

#[cfg(unix)]
#[test]
fn root_rules_accept_what_idunn_provisions_and_nothing_else() {
    use FileKind::{Directory, Regular};
    // What Idunn's systemd driver provisions: 0555 bundle, 0444 records and locks,
    // 0640 lease files.
    assert!(facts(Directory, 0, 0o555).satisfies(Rule::ReadOnlyDirectory));
    assert!(facts(Regular, 0, 0o444).satisfies(Rule::ReadOnlyFile));
    assert!(facts(Regular, 0, 0o640).satisfies(Rule::ControlledFile));
    // Each rule keeps its own kind and owner.
    assert!(!facts(Directory, 0, 0o555).satisfies(Rule::ReadOnlyFile));
    assert!(!facts(Regular, 0, 0o444).satisfies(Rule::ReadOnlyDirectory));
    assert!(!facts(Regular, 0, 0o640).satisfies(Rule::ReadOnlyFile));
    assert!(!facts(Regular, 1000, 0o640).satisfies(Rule::ControlledFile));
}

#[cfg(unix)]
#[test]
fn only_root_custody_inspects_a_lock() -> Result<()> {
    use std::os::unix::fs::PermissionsExt;
    let dir = tempfile::tempdir()?;
    let lock = dir.path().join("record.cc.lock");
    std::fs::write(&lock, b"")?;
    std::fs::set_permissions(&lock, std::fs::Permissions::from_mode(0o666))?;
    // The profile trusts its own lock; root custody refuses a world-writable one.
    Custody::Profile.require_lock(&lock, "lock", Rule::ControlledFile)?;
    let refused = refusal(Custody::Root.require_lock(&lock, "lock", Rule::ControlledFile));
    assert!(
        refused.contains("lock is not a root-controlled regular file"),
        "{refused}"
    );
    Ok(())
}

/// A directory junction is a reparse point: profile custody refuses it as a bundle.
#[cfg(windows)]
#[test]
fn profile_custody_refuses_a_junction() -> Result<()> {
    let rig = Rig::new(false)?;
    let junction = rig.dir.path().join("bundle-junction");
    let made = std::process::Command::new("cmd")
        .args(["/C", "mklink", "/J"])
        .arg(&junction)
        .arg(rig.bundle())
        .output()?;
    assert!(made.status.success(), "{made:?}");
    assert_eq!(
        FileKind::of(&std::fs::symlink_metadata(&junction)?),
        FileKind::Symlink
    );
    let host = rig
        .host()
        .with(IDUNN_RUNTIME_BUNDLE_ENVIRONMENT, junction.to_str().unwrap());
    let refused = refusal(rig.open(&host));
    assert!(
        refused.contains("Idunn runtime bundle is not a directory"),
        "{refused}"
    );
    Ok(())
}

fn host_delivery(seed: &Path, identity: &Path) -> FakeHost {
    FakeHost::new()
        .with(
            IDUNN_ACTIVATION_CREDENTIAL_ENVIRONMENT,
            seed.to_str().unwrap(),
        )
        .with(
            RUNTIME_PRESENCE_IDENTITY_BINDING,
            identity.to_str().unwrap(),
        )
}

#[test]
fn host_delivery_signs_on_every_platform() -> Result<()> {
    let rig = Rig::new(false)?;
    let dir = tempfile::tempdir()?;
    let seed = dir.path().join("activation-key");
    let identity = dir.path().join("identity.cc");
    std::fs::write(&seed, &rig.activation_seed)?;
    std::fs::write(&identity, &rig.provider_credential)?;
    let credentials =
        IdunnRuntimeCredentials::from_host_environment_in(&host_delivery(&seed, &identity))?;
    let authority = rig.open_with(&rig.host(), TARGET, HEALTH_CONTRACT, credentials)?;
    let mut signer = IdunnRuntimePresenceSigner::new(authority, capabilities());
    let warming = signer.sign_at("starting", 1_000)?;
    signer.report_health(RuntimePresenceState::Active);
    let active = signer.sign_at("ready", 1_001)?;
    assert_odin_accepts(&rig, &warming, 1_010)?;
    assert_odin_accepts(&rig, &active, 1_010)?;
    Ok(())
}

#[test]
fn host_credentials_keep_the_seed_when_the_provider_is_missing() -> Result<()> {
    let rig = Rig::new(false)?;
    let dir = tempfile::tempdir()?;
    let seed = dir.path().join("activation-key");
    std::fs::write(&seed, &rig.activation_seed)?;
    let absent = dir.path().join("absent-identity.cc");
    let refused = refusal(IdunnRuntimeCredentials::from_host_environment_in(
        &host_delivery(&seed, &absent),
    ));
    assert!(
        refused.contains(RUNTIME_PRESENCE_IDENTITY_BINDING),
        "{refused}"
    );
    assert!(seed.exists(), "a refused delivery leaves the one-shot seed");
    // The next launch attempt, with the identity in place, still has its seed.
    let identity = dir.path().join("identity.cc");
    std::fs::write(&identity, &rig.provider_credential)?;
    IdunnRuntimeCredentials::from_host_environment_in(&host_delivery(&seed, &identity))?;
    assert!(!seed.exists());
    Ok(())
}

#[test]
fn host_credential_errors_never_echo_a_path() -> Result<()> {
    let rig = Rig::new(false)?;
    let root = tempfile::tempdir()?;
    let canary = root.path().join("canary-6e1f0c9a");
    std::fs::create_dir(&canary)?;
    let seed = canary.join("activation-key");
    let identity = canary.join("identity.cc");
    std::fs::write(&identity, &rig.provider_credential)?;
    let directory = canary.join("a-directory");
    std::fs::create_dir(&directory)?;
    let mut refusals = vec![
        // The activation credential is absent.
        refusal(IdunnRuntimeCredentials::from_host_environment_in(
            &host_delivery(&seed, &identity),
        )),
        // The provider identity is absent.
        refusal(IdunnRuntimeCredentials::from_host_environment_in(
            &host_delivery(&directory.join("seed-in-a-dir"), &canary.join("absent.cc")),
        )),
        // The activation credential is a directory: it cannot be read or retired.
        refusal(IdunnRuntimeCredentials::from_host_environment_in(
            &host_delivery(&directory, &identity),
        )),
    ];
    // Bundle and record refusals: a missing bundle and a bundle of the wrong kind.
    let missing_bundle = rig.bare_host().with(
        IDUNN_RUNTIME_BUNDLE_ENVIRONMENT,
        canary.join("no-bundle").to_str().unwrap(),
    );
    refusals.push(refusal(rig.open(&missing_bundle)));
    let file_bundle = rig
        .bare_host()
        .with(IDUNN_RUNTIME_BUNDLE_ENVIRONMENT, identity.to_str().unwrap());
    refusals.push(refusal(rig.open(&file_bundle)));
    let record_missing = tempfile::tempdir()?;
    let host = rig.bare_host().with(
        IDUNN_RUNTIME_BUNDLE_ENVIRONMENT,
        record_missing.path().to_str().unwrap(),
    );
    refusals.push(refusal(rig.open(&host)));
    for refused in &refusals {
        assert!(!refused.contains("canary-6e1f0c9a"), "{refused}");
        assert!(
            !refused.contains(root.path().to_str().unwrap()),
            "{refused}"
        );
        assert!(
            !refused.contains(record_missing.path().to_str().unwrap()),
            "{refused}"
        );
    }
    assert!(
        refusals[0].contains(IDUNN_ACTIVATION_CREDENTIAL_ENVIRONMENT),
        "{}",
        refusals[0]
    );
    assert!(
        refusals[1].contains(RUNTIME_PRESENCE_IDENTITY_BINDING),
        "{}",
        refusals[1]
    );
    Ok(())
}

// --------------------------------------------------------------------- custody

#[cfg(unix)]
fn facts(kind: FileKind, uid: u32, mode: u32) -> Facts {
    Facts {
        kind,
        uid,
        gid: 0,
        mode,
        nlink: 1,
        len: 32,
    }
}

#[cfg(unix)]
#[test]
fn custody_predicates() {
    use FileKind::{Directory, Other, Regular, Symlink};
    // Root-owned read-only files (bundle records and locks).
    assert!(facts(Regular, 0, 0o444).is_root_read_only_file());
    assert!(facts(Regular, 0, 0o400).is_root_read_only_file());
    assert!(
        !facts(Regular, 0, 0o644).is_root_read_only_file(),
        "owner-writable"
    );
    assert!(
        !facts(Regular, 0, 0o464).is_root_read_only_file(),
        "group-writable"
    );
    assert!(
        !facts(Regular, 0, 0o446).is_root_read_only_file(),
        "world-writable"
    );
    assert!(
        !facts(Regular, 1000, 0o444).is_root_read_only_file(),
        "not root"
    );
    assert!(
        !facts(Symlink, 0, 0o444).is_root_read_only_file(),
        "symlink"
    );
    assert!(!facts(Directory, 0, 0o444).is_root_read_only_file());
    assert!(!facts(Other, 0, 0o444).is_root_read_only_file());
    // The bundle directory.
    assert!(facts(Directory, 0, 0o555).is_root_read_only_directory());
    assert!(!facts(Directory, 0, 0o755).is_root_read_only_directory());
    assert!(!facts(Directory, 1000, 0o555).is_root_read_only_directory());
    assert!(!facts(Symlink, 0, 0o555).is_root_read_only_directory());
    assert!(!facts(Regular, 0, 0o555).is_root_read_only_directory());
    // Root-controlled files and directories (the lease and its parents).
    assert!(facts(Regular, 0, 0o644).is_root_controlled_file());
    assert!(
        !facts(Regular, 0, 0o664).is_root_controlled_file(),
        "group-writable"
    );
    assert!(
        !facts(Regular, 0, 0o646).is_root_controlled_file(),
        "world-writable"
    );
    assert!(!facts(Regular, 1000, 0o644).is_root_controlled_file());
    assert!(!facts(Symlink, 0, 0o644).is_root_controlled_file());
    assert!(facts(Directory, 0, 0o755).is_root_controlled_directory());
    assert!(!facts(Directory, 0, 0o775).is_root_controlled_directory());
    assert!(!facts(Directory, 0, 0o757).is_root_controlled_directory());
    assert!(
        !facts(Directory, 0, 0o1777).is_root_controlled_directory(),
        "sticky tmp"
    );
    assert!(!facts(Directory, 1000, 0o755).is_root_controlled_directory());
    assert!(!facts(Symlink, 0, 0o755).is_root_controlled_directory());
    // Inherited signers.
    assert!(facts(Regular, 0, 0o400).is_signer_descriptor());
    assert!(!facts(Regular, 0, 0o440).is_signer_descriptor());
    assert!(!facts(Regular, 0, 0o600).is_signer_descriptor());
    assert!(!facts(Regular, 0, 0o4400).is_signer_descriptor(), "setuid");
    assert!(!facts(Regular, 1000, 0o400).is_signer_descriptor());
    assert!(
        !Facts {
            gid: 1000,
            ..facts(Regular, 0, 0o400)
        }
        .is_signer_descriptor()
    );
    assert!(
        !Facts {
            nlink: 2,
            ..facts(Regular, 0, 0o400)
        }
        .is_signer_descriptor()
    );
    assert!(
        !Facts {
            len: 0,
            ..facts(Regular, 0, 0o400)
        }
        .is_signer_descriptor()
    );
    assert!(!facts(Directory, 0, 0o400).is_signer_descriptor());
}

#[cfg(unix)]
#[test]
fn root_custody_reads_the_real_filesystem_and_refuses() -> Result<()> {
    use std::os::unix::fs::{PermissionsExt, symlink};
    let dir = tempfile::tempdir()?;
    let file = dir.path().join("record.cc");
    std::fs::write(&file, b"x")?;
    let require =
        |custody: Custody, path: &Path| custody.require_file(path, "record", Rule::ControlledFile);

    std::fs::set_permissions(&file, std::fs::Permissions::from_mode(0o666))?;
    assert!(
        refusal(require(Custody::Root, &file))
            .contains("record is not a root-controlled regular file")
    );
    let link = dir.path().join("link.cc");
    symlink(&file, &link)?;
    std::fs::set_permissions(&file, std::fs::Permissions::from_mode(0o644))?;
    assert!(
        require(Custody::Root, &link).is_err(),
        "a symlink is never custody"
    );
    assert!(require(Custody::Root, Path::new("record.cc")).is_err());
    assert!(require(Custody::Root, &dir.path().join("absent")).is_err());
    assert!(
        require(Custody::Profile, &link).is_err(),
        "profile custody refuses a link too"
    );
    assert!(require(Custody::Profile, &file).is_ok());

    assert!(
        Custody::Root
            .require_directory_chain(dir.path(), "dir")
            .is_err()
    );
    assert!(
        Custody::Root
            .require_directory_chain(Path::new("relative"), "dir")
            .is_err()
    );
    assert!(
        Custody::Profile
            .require_directory_chain(dir.path(), "dir")
            .is_ok()
    );
    Ok(())
}

#[cfg(unix)]
#[test]
fn the_default_open_path_enforces_root_custody() -> Result<()> {
    // The rig's bundle is an ordinary writable temp directory.
    let rig = Rig::new(false)?;
    let mut credentials = rig.credentials();
    credentials.custody = Custody::Root;
    let refused = refusal(rig.open_with(&rig.host(), TARGET, HEALTH_CONTRACT, credentials));
    assert!(
        refused.contains("Idunn runtime bundle is not a root-owned read-only directory"),
        "{refused}"
    );
    Ok(())
}

#[test]
fn debug_output_names_the_target_and_never_signing_material() -> Result<()> {
    let rig = Rig::new(false)?;
    let shown = format!("{:?}", rig.authority()?);
    assert!(shown.contains("IdunnRuntimeAuthority") && shown.contains(TARGET));
    Ok(())
}

#[cfg(unix)]
#[test]
fn a_lease_guard_debug_names_its_digest() -> Result<()> {
    let lease_rig = Rig::new(true)?;
    let mut signer = lease_rig.signer()?;
    let warming = signer.sign_at("w", 1_000)?;
    lease_rig.write_lease(&warming.canonical_sha256, 1)?;
    let guard = signer.try_acquire_write_lease()?.unwrap();
    assert!(format!("{guard:?}").contains(guard.lease_sha256()));
    Ok(())
}

// ----------------------------------------------------- the real process, in small

/// A descriptor number far above anything a parallel test is likely to open.
#[cfg(unix)]
fn high_duplicate(file: &File) -> RawFd {
    // SAFETY: F_DUPFD duplicates a descriptor `file` owns into a fresh number.
    let descriptor = unsafe { libc::fcntl(file.as_raw_fd(), libc::F_DUPFD, 700) };
    assert!(descriptor >= 700);
    descriptor
}

#[test]
fn the_process_host_reads_the_real_process() -> Result<()> {
    let mut host = ProcessHost;
    assert_eq!(host.process_id(), std::process::id());

    let name = "CULTNET_RS_PROVIDER_TEST_PROCESS_HOST";
    assert_eq!(host.var(name), None);
    // SAFETY: a name no other test or thread reads or writes.
    unsafe { std::env::set_var(name, "a value") };
    assert_eq!(host.var(name).as_deref(), Some("a value"));
    host.remove_var(name);
    assert_eq!(host.var(name), None);
    assert!(
        std::env::var(name).is_err(),
        "removed from the real environment"
    );
    Ok(())
}

#[cfg(unix)]
#[test]
fn the_process_host_adopts_real_descriptors() -> Result<()> {
    let mut host = ProcessHost;
    let dir = tempfile::tempdir()?;
    std::fs::write(dir.path().join("adopted"), b"adopted bytes")?;
    let source = File::open(dir.path().join("adopted"))?;
    let descriptor = high_duplicate(&source);
    let adopted = host.adopt_descriptor(descriptor)?;
    assert_eq!(adopted.as_raw_fd(), descriptor);
    assert_eq!(contents(adopted.try_clone()?)?, "adopted bytes");
    adopted.sync_all()?;

    let closed = high_duplicate(&source);
    // SAFETY: closes the duplicate made on the previous line.
    assert_eq!(unsafe { libc::close(closed) }, 0);
    assert!(refusal(host.adopt_descriptor(closed)).contains("is not open"));
    Ok(())
}

#[cfg(unix)]
#[test]
fn facts_read_the_real_metadata() -> Result<()> {
    use std::os::unix::fs::{PermissionsExt, symlink};
    let dir = tempfile::tempdir()?;
    let file = dir.path().join("file");
    std::fs::write(&file, b"hello")?;
    std::fs::set_permissions(&file, std::fs::Permissions::from_mode(0o640))?;
    let observed = Facts::of(&std::fs::symlink_metadata(&file)?);
    assert_eq!(observed.kind, FileKind::Regular);
    assert_eq!(
        observed.mode, 0o640,
        "permission bits only, not the file type"
    );
    assert_eq!((observed.len, observed.nlink), (5, 1));
    // SAFETY: geteuid takes no arguments and cannot fail.
    assert_eq!(observed.uid, unsafe { libc::geteuid() });

    assert_eq!(Facts::of_path(dir.path(), "dir")?.kind, FileKind::Directory);
    let link = dir.path().join("link");
    symlink(&file, &link)?;
    assert_eq!(Facts::of_path(&link, "link")?.kind, FileKind::Symlink);
    let socket = dir.path().join("socket");
    let _listener = std::os::unix::net::UnixListener::bind(&socket)?;
    assert_eq!(Facts::of_path(&socket, "socket")?.kind, FileKind::Other);
    assert!(Facts::of_path(&dir.path().join("absent"), "absent").is_err());
    Ok(())
}

// --------------------------------------------------------- lease failure shapes

#[cfg(unix)]
#[test]
fn a_lease_path_that_cannot_be_inspected_is_an_error_not_an_absent_lease() -> Result<()> {
    let rig = Rig::new(true)?;
    // The parent is a regular file, so the lease path is ENOTDIR, not "not found".
    let impossible = rig.dir.path().join("provider.cc").join("lease.cc");
    let host = rig
        .bare_host()
        .with(IDUNN_RUNTIME_CANDIDATE_BIND_ENVIRONMENT, BIND)
        .with(
            IDUNN_PROCESS_WRITE_LEASE_ENVIRONMENT,
            impossible.to_str().unwrap(),
        );
    let mut signer = IdunnRuntimePresenceSigner::new(rig.open(&host)?, capabilities());
    assert!(signer.try_acquire_write_lease().is_err());
    Ok(())
}

#[cfg(unix)]
#[test]
fn the_write_lease_is_held_to_root_custody() -> Result<()> {
    let rig = Rig::new(true)?;
    let mut authority = rig.authority()?;
    Arc::get_mut(&mut authority.records).unwrap().custody = Custody::Root;
    let mut signer = IdunnRuntimePresenceSigner::new(authority, capabilities());
    let warming = signer.sign_at("w", 1_000)?;
    assert!(
        signer.try_acquire_write_lease()?.is_none(),
        "an absent record is not yet a custody question"
    );
    rig.write_lease(&warming.canonical_sha256, 1)?;
    let refused = refusal(signer.try_acquire_write_lease());
    assert!(refused.contains("process-write-lease"), "{refused}");
    Ok(())
}

#[test]
fn sign_stamps_the_current_time() -> Result<()> {
    let rig = Rig::new(false)?;
    let mut signer = rig.signer()?;
    let before = unix_millis()?;
    let signed = signer.sign("now")?;
    let after = unix_millis()?;
    assert!(
        (before..=after).contains(&signed.record.observed_at_unix_millis),
        "{before} <= {} <= {after}",
        signed.record.observed_at_unix_millis
    );
    assert!(before > 1_700_000_000_000, "unix_millis is the wall clock");
    Ok(())
}

// ------------------------------------------------------ s2 repairs, attempt 3

/// A lease-required runtime may claim Degraded only under a current lease, with
/// no lease ever held and after one was lost alike.
#[cfg(unix)]
#[test]
fn degraded_needs_a_current_lease_whatever_the_history() -> Result<()> {
    let rig = Rig::new(true)?;
    let mut signer = rig.signer()?;
    let warming = signer.sign_at("w", 1_000)?;
    signer.report_health(RuntimePresenceState::Degraded);
    assert!(signer.sign_at("never held", 1_001).is_err());
    rig.write_lease(&warming.canonical_sha256, 1)?;
    signer.try_acquire_write_lease()?.expect("binds");
    assert!(signer.sign_at("held", 1_002).is_ok());
    std::fs::remove_file(rig.lease_path())?;
    assert!(signer.sign_at("lost", 1_003).is_err());
    Ok(())
}

/// A runtime that never needed a lease reports Degraded and Active without one.
#[test]
fn degraded_and_active_sign_without_a_lease_when_none_is_required() -> Result<()> {
    let rig = Rig::new(false)?;
    let mut signer = rig.signer()?;
    for state in [RuntimePresenceState::Degraded, RuntimePresenceState::Active] {
        signer.report_health(state);
        assert_eq!(signer.sign_at("up", 1_000)?.record.write_lease_sha256, None);
    }
    Ok(())
}

fn assert_no_path(refused: &str, rig: &Rig) {
    assert!(
        !refused.contains(rig.dir.path().to_str().unwrap()),
        "{refused}"
    );
    assert!(
        !refused.contains(rig.dir.path().file_name().unwrap().to_str().unwrap()),
        "{refused}"
    );
}

#[test]
fn a_missing_record_lock_names_no_path() -> Result<()> {
    let rig = Rig::new(false)?;
    let mut lock = rig.bundle().join("expected.cc").into_os_string();
    lock.push(".lock");
    std::fs::remove_file(&lock)?;
    let refused = refusal(rig.open(&rig.host()));
    assert!(
        refused.contains("could not be read as a CultCache store: not found"),
        "{refused}"
    );
    assert_no_path(&refused, &rig);
    Ok(())
}

#[test]
fn an_undecodable_record_names_no_path() -> Result<()> {
    let rig = Rig::new(false)?;
    std::fs::write(
        rig.bundle().join("activation.cc"),
        [0xc1_u8, 0xc1, b'g', b'a'],
    )?;
    let refused = refusal(rig.open(&rig.host()));
    assert!(
        refused.contains("could not be read as a CultCache store: not decodable"),
        "{refused}"
    );
    assert_no_path(&refused, &rig);
    Ok(())
}

#[test]
fn a_record_in_a_newer_store_format_says_so_and_names_no_path() -> Result<()> {
    let rig = Rig::new(false)?;
    // A store header this runtime does not read; the format is judged from the
    // header alone, so the cut-short store is still a newer one.
    let mut newer = vec![0x93_u8, 0xb2];
    newer.extend_from_slice(b"cultcache.store.v9");
    std::fs::write(rig.bundle().join("activation.cc"), newer)?;
    let refused = refusal(rig.open(&rig.host()));
    assert!(
        refused.contains(
            "could not be read as a CultCache store: in a format this runtime does not read"
        ),
        "{refused}"
    );
    assert_no_path(&refused, &rig);
    Ok(())
}

#[test]
fn an_io_failure_is_labelled_by_its_kind_alone() {
    use std::io::{Error, ErrorKind};
    let path = "/profile/secret-dir/store.cc";
    for (kind, label) in [
        (ErrorKind::PermissionDenied, "permission denied"),
        (ErrorKind::NotFound, "not found"),
        (ErrorKind::TimedOut, "I/O error"),
    ] {
        let error = anyhow::Error::new(Error::new(kind, path))
            .context(format!("failed to open pre-created {path}.lock"));
        assert_eq!(store_failure_kind(&error), label);
    }
    assert_eq!(store_failure_kind(&anyhow!("opaque")), "unreadable");
}

/// The provider's own refusals about a record still say what is wrong.
#[test]
fn a_store_of_two_records_is_still_refused_by_its_own_message() -> Result<()> {
    let rig = Rig::new(false)?;
    write_record(
        &rig.bundle().join("expected.cc"),
        "other",
        IdunnExpectedIncarnationRecord::TYPE,
        IDUNN_EXPECTED_INCARNATION_SCHEMA,
        rig.expected.canonical_bytes()?,
    )?;
    let refused = refusal(rig.open(&rig.host()));
    assert!(refused.contains("exactly one record"), "{refused}");
    Ok(())
}

#[cfg(unix)]
#[test]
fn an_undecodable_lease_names_no_path() -> Result<()> {
    let rig = Rig::new(true)?;
    let mut signer = rig.signer()?;
    let warming = signer.sign_at("w", 1_000)?;
    rig.write_lease(&warming.canonical_sha256, 1)?;
    signer.try_acquire_write_lease()?.expect("binds");
    std::fs::write(rig.lease_path(), [0xc1_u8, 0xc1, b'g', b'a'])?;
    signer.report_health(RuntimePresenceState::Active);
    let refused = refusal(signer.sign_at("a", 1_001));
    assert!(refused.contains("could not be read"), "{refused}");
    assert_no_path(&refused, &rig);
    Ok(())
}

/// A provider identity that is not a regular file refuses before the one-shot
/// seed is read, so the next launch attempt still has it.
#[test]
fn host_credentials_keep_the_seed_when_the_provider_is_a_directory() -> Result<()> {
    let rig = Rig::new(false)?;
    let dir = tempfile::tempdir()?;
    let seed = dir.path().join("activation-key");
    std::fs::write(&seed, &rig.activation_seed)?;
    let provider = dir.path().join("provider-is-a-dir");
    std::fs::create_dir(&provider)?;
    let refused = refusal(IdunnRuntimeCredentials::from_host_environment_in(
        &host_delivery(&seed, &provider),
    ));
    assert!(
        refused.contains(RUNTIME_PRESENCE_IDENTITY_BINDING),
        "{refused}"
    );
    assert!(!refused.contains(dir.path().to_str().unwrap()), "{refused}");
    assert!(seed.exists(), "a refused delivery leaves the one-shot seed");
    Ok(())
}

mod odin_publisher;
