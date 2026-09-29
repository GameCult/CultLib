//! Generate the Idunn runtime fixtures that cultnet-ts is tested against.
//!
//! The Rust owner builds every input: real enrolled identities, an Expected that
//! passes `validate`, an Activation issued by `IdunnRuntimeActivationLaunch`, the
//! bundle files written through `SingleFileMessagePackBackingStore`, and
//! runtime-presence vectors signed by the Rust signers. Each vector is also run
//! through `authenticate_runtime_presence_claim` and
//! `correlate_runtime_presence_claim` before it is written, so the bytes
//! committed to the TypeScript tests are ones this verifier accepted (or, for
//! the low-capacity vector, ones it correlates to the typed capacity
//! disagreement). Ed25519 is deterministic, so the TypeScript signer must
//! reproduce each vector byte for byte from the same fields.
//!
//! A `stateful` set is the lease-bound shape: Expected carries a state
//! lineage and requires a write lease, and Rust issues `lease.cc` naming the
//! warming vector's digest. Its warming vector is sequence 1; the active and
//! degraded vectors are sequence 2 and bind to that lease. Two further leases are
//! deliberately wrong (`lease-other-warming.cc` names a different warming digest,
//! `lease-other-target.cc` names another target) for refusal tests.
//!
//! Linux only: the provider identity is bound to `/etc/machine-id`, and the
//! generated `machine-id` file records the value the identities were bound to.
//!
//! Usage: idunn_runtime_fixture <out-dir> <spec>...
//!
//! The specification is command-line flags. `@<file>` splices in a response
//! file: one flag per line, `--flag value` split at the first whitespace, blank
//! lines and `#` lines ignored. CultLib's own sets are
//! `packages/cultnet-ts/test/fixtures/idunn-runtime/fixture.args`.
//!
//! Global flags:
//!   --source-repository <repo>        Expected.source_repository (default github.com/GameCult/Example)
//!   --detail <text>                   presence detail (default route-observation:route-challenge-1)
//!
//! `--set <dir>` opens a fixture set written to `<out-dir>/<dir>`; the flags
//! below apply to the set most recently opened:
//!   --target <name>                   required
//!   --transport <t> --stable <endpoint> --candidate <endpoint>   required: the route
//!   --capability <name>               default `<target>.api`
//!   --schema <schema>                 default `<capability>.v1`
//!   --health-contract <contract>      default `<target>.runtime-health`
//!   --minimum-capacity <n>            default 1; above 1 also writes presence-active-below-minimum.bin
//!   --stateful                        state lineage, write lease and lease vectors
//!   --state-schema-generation <id>    stateful only; default state-v1
//!   --lease-other-target <name>       stateful only; default the first other set's target
//!   --dependency k=v,k=v,...          repeatable; keys kind, capability, schema, provider,
//!                                     endpoint (required), compatibility (v1), capacity (1),
//!                                     startup (before-promotion), authority (managed-incarnation),
//!                                     projection (one hex digit the digest is filled with, 5)

use std::collections::BTreeMap;
use std::fs;
use std::path::Path;

use anyhow::{Context, Result, bail, ensure};
use cultcache_rs::{
    CacheBackingStore, CultCacheEnvelope, DatabaseEntry, SingleFileMessagePackBackingStore,
};
use cultnet_rs::{
    GameCultProviderHealthIdentity, GameCultRuntimeCapability,
    GameCultRuntimePresenceHealthPurpose, GameCultRuntimePresenceHealthRecord,
    IDUNN_EXPECTED_INCARNATION_SCHEMA, IDUNN_PROCESS_WRITE_LEASE_SCHEMA,
    IDUNN_RUNTIME_ACTIVATION_SCHEMA, IdunnExpectedCapability, IdunnExpectedDependency,
    IdunnExpectedIncarnationRecord, IdunnExpectedRoute, IdunnProcessWriteLeaseRecord,
    IdunnRuntimeActivationLaunch, IdunnRuntimeActivationRecord,
    IdunnRuntimeActivationSigner, IdunnServiceIdentity, RuntimePresenceAuthenticationContext,
    authenticate_runtime_presence_claim, correlate_runtime_presence_claim,
    enroll_service_identity_at, verify_runtime_authority,
};

/// The TypeScript tests pin the same values; drift shows up as a byte mismatch.
const OBSERVED_AT: u64 = 1_790_000_000_000;
const ISSUED_AT: u64 = OBSERVED_AT - 5_000;
const SEQUENCE: u64 = 1;
const STORED_AT: &str = "2026-01-01T00:00:00.000Z";

struct Spec {
    source_repository: String,
    detail: String,
    sets: Vec<Set>,
}

#[derive(Default)]
struct Set {
    dir: String,
    target: String,
    transport: String,
    stable: String,
    candidate: String,
    capability: Option<String>,
    schema: Option<String>,
    health_contract: Option<String>,
    minimum_capacity: u32,
    stateful: bool,
    state_schema_generation: Option<String>,
    lease_other_target: Option<String>,
    dependencies: Vec<IdunnExpectedDependency>,
}

fn expand(args: impl Iterator<Item = String>) -> Result<Vec<String>> {
    let mut flat = Vec::new();
    for arg in args {
        let Some(file) = arg.strip_prefix('@') else {
            flat.push(arg);
            continue;
        };
        for line in fs::read_to_string(file).with_context(|| format!("reading {file}"))?.lines() {
            let line = line.trim();
            if line.is_empty() || line.starts_with('#') {
                continue;
            }
            match line.split_once(char::is_whitespace) {
                Some((flag, value)) => flat.extend([flag.to_string(), value.trim().to_string()]),
                None => flat.push(line.to_string()),
            }
        }
    }
    Ok(flat)
}

fn dependency(text: &str) -> Result<IdunnExpectedDependency> {
    let mut fields = BTreeMap::new();
    for pair in text.split(',') {
        let (key, value) = pair
            .split_once('=')
            .with_context(|| format!("dependency field `{pair}` is not key=value"))?;
        ensure!(fields.insert(key, value).is_none(), "dependency key `{key}` repeated");
    }
    let mut take = |key: &str, default: Option<&str>| -> Result<String> {
        match (fields.remove(key), default) {
            (Some(value), _) => Ok(value.to_string()),
            (None, Some(default)) => Ok(default.to_string()),
            (None, None) => bail!("dependency needs `{key}`"),
        }
    };
    let dependency = IdunnExpectedDependency {
        kind: take("kind", None)?,
        capability: take("capability", None)?,
        schema: take("schema", None)?,
        compatibility: take("compatibility", Some("v1"))?,
        minimum_capacity: take("capacity", Some("1"))?.parse()?,
        startup: take("startup", Some("before-promotion"))?,
        provider_id: Some(take("provider", None)?),
        provider_authority: Some(take("authority", Some("managed-incarnation"))?),
        provider_expected_projection_sha256: Some(digest(
            take("projection", Some("5"))?.chars().next().context("empty projection")?,
        )),
        provider_endpoint: Some(take("endpoint", None)?),
    };
    ensure!(fields.is_empty(), "unknown dependency keys {:?}", fields.keys().collect::<Vec<_>>());
    Ok(dependency)
}

fn parse(args: impl Iterator<Item = String>) -> Result<Spec> {
    let mut spec = Spec {
        source_repository: "github.com/GameCult/Example".into(),
        detail: "route-observation:route-challenge-1".into(),
        sets: Vec::new(),
    };
    let mut args = expand(args)?.into_iter();
    while let Some(flag) = args.next() {
        let mut value = || args.next().with_context(|| format!("{flag} needs a value"));
        match flag.as_str() {
            "--source-repository" => spec.source_repository = value()?,
            "--detail" => spec.detail = value()?,
            "--set" => spec.sets.push(Set { dir: value()?, minimum_capacity: 1, ..Set::default() }),
            _ => {
                let set = spec.sets.last_mut().with_context(|| format!("{flag} before any --set"))?;
                match flag.as_str() {
                    "--target" => set.target = value()?,
                    "--transport" => set.transport = value()?,
                    "--stable" => set.stable = value()?,
                    "--candidate" => set.candidate = value()?,
                    "--capability" => set.capability = Some(value()?),
                    "--schema" => set.schema = Some(value()?),
                    "--health-contract" => set.health_contract = Some(value()?),
                    "--minimum-capacity" => set.minimum_capacity = value()?.parse()?,
                    "--stateful" => set.stateful = true,
                    "--state-schema-generation" => set.state_schema_generation = Some(value()?),
                    "--lease-other-target" => set.lease_other_target = Some(value()?),
                    "--dependency" => set.dependencies.push(dependency(&value()?)?),
                    other => bail!("unknown flag {other}"),
                }
            }
        }
    }
    ensure!(!spec.sets.is_empty(), "no --set given");
    for set in &spec.sets {
        for (name, field) in [
            ("target", &set.target),
            ("transport", &set.transport),
            ("stable", &set.stable),
            ("candidate", &set.candidate),
        ] {
            ensure!(!field.is_empty(), "set {} needs --{name}", set.dir);
        }
    }
    Ok(spec)
}

fn main() -> Result<()> {
    let mut args = std::env::args().skip(1);
    let Some(out) = args.next() else {
        bail!("usage: idunn_runtime_fixture <out-dir> <spec>... (see the file header)");
    };
    let spec = parse(args)?;
    let machine_id = fs::read_to_string("/etc/machine-id")?;
    fs::create_dir_all(&out)?;
    fs::write(Path::new(&out).join("machine-id"), machine_id.trim())?;
    let idunn = enroll_service_identity_at::<IdunnServiceIdentity>(
        &Path::new(&out).join("idunn-identity.private"),
    )?;
    let idunn_anchor = idunn.trust_anchor()?;
    fs::remove_file(Path::new(&out).join("idunn-identity.private"))?;
    let _ = fs::remove_file(Path::new(&out).join("idunn-identity.private.lock"));
    for set in &spec.sets {
        build(Path::new(&out).join(&set.dir).as_path(), &spec, set, &idunn, &idunn_anchor)?;
    }
    Ok(())
}

fn digest(byte: char) -> String {
    format!("sha256-{}", byte.to_string().repeat(64))
}

fn build(
    dir: &Path,
    spec: &Spec,
    set: &Set,
    idunn: &cultnet_rs::ServiceIdentitySigner<IdunnServiceIdentity>,
    idunn_anchor: &cultnet_rs::ServiceIdentityTrustAnchor,
) -> Result<()> {
    fs::create_dir_all(dir)?;
    let provider_path = dir.join("provider-identity.credential");
    let provider = enroll_service_identity_at::<GameCultProviderHealthIdentity>(&provider_path)?;
    let provider_anchor = provider.trust_anchor()?;
    let _ = fs::remove_file(dir.join("provider-identity.credential.lock"));

    let capability = set.capability.clone().unwrap_or_else(|| format!("{}.api", set.target));
    let capability_schema = set.schema.clone().unwrap_or_else(|| format!("{capability}.v1"));
    let expected = IdunnExpectedIncarnationRecord {
        schema_version: IDUNN_EXPECTED_INCARNATION_SCHEMA.into(),
        target: set.target.clone(),
        plan_id: digest('1'),
        incarnation_id: format!("{}/incarnation-1", set.target),
        sealed_release_id: digest('2'),
        source_repository: spec.source_repository.clone(),
        source_revision: "a".repeat(40),
        recipe_sha256: digest('3'),
        runtime_id: format!("{}-yggdrasil", set.target),
        expected_signer_identity_id: provider_anchor.identity_id.clone(),
        health_contract: set.health_contract.clone().unwrap_or_else(|| format!("{}.runtime-health", set.target)),
        artifact_sha256: digest('4'),
        state_schema_generation: set.stateful.then(|| set.state_schema_generation.clone().unwrap_or_else(|| "state-v1".into())),
        state_contract_sha256: set.stateful.then(|| digest('7')),
        write_lease_required: set.stateful,
        route: Some(IdunnExpectedRoute {
            route_id: format!("{}-route", set.target),
            transport: set.transport.clone(),
            stable_endpoint: set.stable.clone(),
            candidate_endpoint: set.candidate.clone(),
        }),
        capabilities: vec![IdunnExpectedCapability {
            capability: capability.clone(),
            schema: capability_schema.clone(),
            compatibility: "v1".into(),
            minimum_capacity: set.minimum_capacity,
        }],
        dependencies: set.dependencies.clone(),
    };
    expected.validate()?;

    let launch = IdunnRuntimeActivationLaunch::issue(&expected, digest('6'), ISSUED_AT, idunn)?;
    let mut credential = Vec::new();
    let activation = launch.write_credential(&mut credential)?;
    ensure!(credential.len() == 32, "activation credential is a 32-byte seed");
    fs::write(dir.join("activation-key.seed"), &credential)?;
    let activation_signer = IdunnRuntimeActivationSigner::from_credential_reader(&credential[..])?;

    write_record(
        &dir.join("expected.cc"),
        &expected.target,
        IdunnExpectedIncarnationRecord::TYPE,
        IDUNN_EXPECTED_INCARNATION_SCHEMA,
        expected.canonical_bytes()?,
    )?;
    write_record(
        &dir.join("activation.cc"),
        &expected.target,
        IdunnRuntimeActivationRecord::TYPE,
        IDUNN_RUNTIME_ACTIVATION_SCHEMA,
        activation.canonical_bytes()?,
    )?;

    let authority =
        verify_runtime_authority(&expected, &activation, idunn_anchor, &provider_anchor.public_key)?;
    let sign_vector = |name: &str,
                       state: &str,
                       capacity: u32,
                       sequence: u64,
                       write_lease_sha256: Option<String>,
                       disagreement: Option<&str>|
     -> Result<String> {
        let mut presence = GameCultRuntimePresenceHealthRecord {
            schema_version: cultnet_rs::GAMECULT_RUNTIME_PRESENCE_HEALTH_SCHEMA.into(),
            target: expected.target.clone(),
            expected_projection_sha256: activation.expected_projection_sha256.clone(),
            plan_id: expected.plan_id.clone(),
            incarnation_id: expected.incarnation_id.clone(),
            sealed_release_id: expected.sealed_release_id.clone(),
            activation_witness_sha256: activation.canonical_sha256()?,
            state_schema_generation: expected.state_schema_generation.clone(),
            state_contract_sha256: expected.state_contract_sha256.clone(),
            runtime_id: expected.runtime_id.clone(),
            runtime_instance_id: activation.runtime_instance_id.clone(),
            bound_endpoint: Some(set.candidate.clone()),
            capabilities: vec![GameCultRuntimeCapability {
                capability: capability.clone(),
                schema: capability_schema.clone(),
                compatibility: "v1".into(),
                capacity,
            }],
            health_contract: expected.health_contract.clone(),
            state: state.into(),
            detail: spec.detail.clone(),
            write_lease_sha256,
            signer_identity_id: provider_anchor.identity_id.clone(),
            publisher_sequence: sequence,
            observed_at_unix_millis: OBSERVED_AT,
            signature_algorithm: "ed25519".into(),
            signature: Vec::new(),
            activation_signer_identity_id: activation_signer.identity_id(),
            activation_signature: Vec::new(),
        };
        let proof = presence.canonical_proof_payload()?;
        presence.signature = provider
            .sign::<GameCultRuntimePresenceHealthPurpose>(&proof)
            .signature;
        presence.activation_signature = activation_signer.sign_presence_proof(&presence)?;
        let bytes = rmp_serde::to_vec(&presence)?;

        let claim = authenticate_runtime_presence_claim(
            &bytes,
            &authority,
            RuntimePresenceAuthenticationContext {
                trusted_received_at_unix_millis: OBSERVED_AT + 1_000,
                maximum_age_millis: 30_000,
                maximum_future_skew_millis: 0,
            },
        )?;
        let presence_sha256 = claim.signed_presence_sha256().to_string();
        let correlation = correlate_runtime_presence_claim(claim, &authority)?;
        let codes: Vec<&str> = correlation
            .disagreements()
            .iter()
            .map(|entry| entry.code.as_str())
            .collect();
        match disagreement {
            None => ensure!(codes.is_empty(), "{name}: unexpected disagreements {codes:?}"),
            Some(code) => ensure!(codes == [code], "{name}: expected only {code}, got {codes:?}"),
        }
        fs::write(dir.join(name), bytes)?;
        Ok(presence_sha256)
    };

    if !set.stateful {
        sign_vector("presence-active.bin", "active", set.minimum_capacity, SEQUENCE, None, None)?;
        sign_vector("presence-warming.bin", "warming", set.minimum_capacity, SEQUENCE, None, None)?;
        if set.minimum_capacity > 1 {
            sign_vector(
                "presence-active-below-minimum.bin",
                "active",
                set.minimum_capacity - 1,
                SEQUENCE,
                None,
                Some("expected-capability-000-capacity"),
            )?;
        }
        return Ok(());
    }

    // Lease-bound set. The warming vector comes first because the lease names it.
    let warming_sha256 =
        sign_vector("presence-warming.bin", "warming", set.minimum_capacity, SEQUENCE, None, None)?;
    let activation_sha256 = activation.canonical_sha256()?;
    let lease_for = |target: &str, warming_presence_sha256: &str| IdunnProcessWriteLeaseRecord {
        schema_version: IDUNN_PROCESS_WRITE_LEASE_SCHEMA.into(),
        target: target.into(),
        expected_projection_sha256: activation.expected_projection_sha256.clone(),
        plan_id: expected.plan_id.clone(),
        incarnation_id: expected.incarnation_id.clone(),
        sealed_release_id: expected.sealed_release_id.clone(),
        activation_witness_sha256: activation_sha256.clone(),
        state_schema_generation: expected.state_schema_generation.clone().unwrap_or_default(),
        state_contract_sha256: expected.state_contract_sha256.clone().unwrap_or_default(),
        runtime_id: expected.runtime_id.clone(),
        runtime_instance_id: activation.runtime_instance_id.clone(),
        warming_presence_sha256: warming_presence_sha256.into(),
        lease_epoch: 1,
        issued_at_unix_millis: OBSERVED_AT - 1_000,
    };
    let other_target_name = set.lease_other_target.clone().unwrap_or_else(|| {
        spec.sets.iter().map(|other| other.target.clone()).find(|target| *target != set.target).unwrap_or_else(|| format!("{}-other", set.target))
    });
    let lease = lease_for(&set.target, &warming_sha256);
    let other_warming = lease_for(&set.target, &digest('d'));
    let other_target = lease_for(&other_target_name, &warming_sha256);
    for (name, record) in [
        ("lease.cc", &lease),
        ("lease-other-warming.cc", &other_warming),
        ("lease-other-target.cc", &other_target),
    ] {
        let bytes = record.canonical_bytes()?;
        ensure!(
            IdunnProcessWriteLeaseRecord::decode_canonical(&bytes)? == *record,
            "{name}: lease does not round-trip"
        );
        write_record(
            &dir.join(name),
            &record.target,
            IdunnProcessWriteLeaseRecord::TYPE,
            IDUNN_PROCESS_WRITE_LEASE_SCHEMA,
            bytes,
        )?;
    }
    let lease_sha256 = lease.canonical_sha256()?;
    for (name, state) in [("presence-active.bin", "active"), ("presence-degraded.bin", "degraded")] {
        sign_vector(name, state, set.minimum_capacity, SEQUENCE + 1, Some(lease_sha256.clone()), None)?;
    }
    Ok(())
}

fn write_record(path: &Path, key: &str, kind: &str, schema: &str, payload: Vec<u8>) -> Result<()> {
    SingleFileMessagePackBackingStore::new(path).push(&CultCacheEnvelope {
        key: key.into(),
        r#type: kind.into(),
        payload,
        stored_at: STORED_AT.into(),
        schema_id: Some(schema.into()),
    })?;
    let mut lock = path.as_os_str().to_owned();
    lock.push(".lock");
    let _ = fs::remove_file(lock);
    Ok(())
}
