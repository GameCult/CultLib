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
//! Linux only: the provider identity is bound to `/etc/machine-id`, and the
//! generated `machine-id` file records the value the identities were bound to.
//!
//! Usage: idunn_runtime_fixture <out-dir>

use std::fs;
use std::path::Path;

use anyhow::{Result, bail, ensure};
use cultcache_rs::{
    CacheBackingStore, CultCacheEnvelope, DatabaseEntry, SingleFileMessagePackBackingStore,
};
use cultnet_rs::{
    GameCultProviderHealthIdentity, GameCultRuntimeCapability,
    GameCultRuntimePresenceHealthPurpose, GameCultRuntimePresenceHealthRecord,
    IDUNN_EXPECTED_INCARNATION_SCHEMA, IDUNN_RUNTIME_ACTIVATION_SCHEMA,
    IdunnExpectedCapability, IdunnExpectedDependency, IdunnExpectedIncarnationRecord,
    IdunnExpectedRoute, IdunnRuntimeActivationLaunch, IdunnRuntimeActivationRecord,
    IdunnRuntimeActivationSigner, IdunnServiceIdentity, RuntimePresenceAuthenticationContext,
    authenticate_runtime_presence_claim, correlate_runtime_presence_claim,
    enroll_service_identity_at, verify_runtime_authority,
};

/// The TypeScript tests pin the same values; drift shows up as a byte mismatch.
const OBSERVED_AT: u64 = 1_790_000_000_000;
const ISSUED_AT: u64 = OBSERVED_AT - 5_000;
const SEQUENCE: u64 = 1;
const DETAIL: &str = "route-observation:route-challenge-1";
const STORED_AT: &str = "2026-01-01T00:00:00.000Z";

struct Set {
    dir: &'static str,
    target: &'static str,
    transport: &'static str,
    stable: &'static str,
    candidate: &'static str,
    minimum_capacity: u32,
    odin_dependency: bool,
}

const SETS: [Set; 3] = [
    Set {
        dir: "web",
        target: "streampixels-web",
        transport: "http",
        stable: "http://127.0.0.1:8830",
        candidate: "http://127.0.0.1:18830",
        minimum_capacity: 1,
        odin_dependency: false,
    },
    Set {
        dir: "service",
        target: "streampixels-service",
        transport: "tcp",
        stable: "tcp://127.0.0.1:8831",
        candidate: "tcp://127.0.0.1:18831",
        minimum_capacity: 2,
        odin_dependency: true,
    },
    Set {
        dir: "rudp-route",
        target: "streampixels-rudp",
        transport: "rudp",
        stable: "rudp://127.0.0.1:8832",
        candidate: "rudp://127.0.0.1:18832",
        minimum_capacity: 1,
        odin_dependency: false,
    },
];

fn main() -> Result<()> {
    let Some(out) = std::env::args().nth(1) else {
        bail!("usage: idunn_runtime_fixture <out-dir>");
    };
    let machine_id = fs::read_to_string("/etc/machine-id")?;
    fs::create_dir_all(&out)?;
    fs::write(Path::new(&out).join("machine-id"), machine_id.trim())?;
    let idunn = enroll_service_identity_at::<IdunnServiceIdentity>(
        &Path::new(&out).join("idunn-identity.private"),
    )?;
    let idunn_anchor = idunn.trust_anchor()?;
    fs::remove_file(Path::new(&out).join("idunn-identity.private"))?;
    let _ = fs::remove_file(Path::new(&out).join("idunn-identity.private.lock"));
    for set in &SETS {
        build(Path::new(&out).join(set.dir).as_path(), set, &idunn, &idunn_anchor)?;
    }
    Ok(())
}

fn digest(byte: char) -> String {
    format!("sha256-{}", byte.to_string().repeat(64))
}

fn build(
    dir: &Path,
    set: &Set,
    idunn: &cultnet_rs::ServiceIdentitySigner<IdunnServiceIdentity>,
    idunn_anchor: &cultnet_rs::ServiceIdentityTrustAnchor,
) -> Result<()> {
    fs::create_dir_all(dir)?;
    let provider_path = dir.join("provider-identity.credential");
    let provider = enroll_service_identity_at::<GameCultProviderHealthIdentity>(&provider_path)?;
    let provider_anchor = provider.trust_anchor()?;
    let _ = fs::remove_file(dir.join("provider-identity.credential.lock"));

    let capability = format!("{}.api", set.target);
    let capability_schema = format!("{}.api.v1", set.target);
    let expected = IdunnExpectedIncarnationRecord {
        schema_version: IDUNN_EXPECTED_INCARNATION_SCHEMA.into(),
        target: set.target.into(),
        plan_id: digest('1'),
        incarnation_id: format!("{}/incarnation-1", set.target),
        sealed_release_id: digest('2'),
        source_repository: "github.com/GameCult/StreamPixels".into(),
        source_revision: "a".repeat(40),
        recipe_sha256: digest('3'),
        runtime_id: format!("{}-yggdrasil", set.target),
        expected_signer_identity_id: provider_anchor.identity_id.clone(),
        health_contract: format!("{}.runtime-health", set.target),
        artifact_sha256: digest('4'),
        state_schema_generation: None,
        state_contract_sha256: None,
        write_lease_required: false,
        route: Some(IdunnExpectedRoute {
            route_id: format!("{}-route", set.target),
            transport: set.transport.into(),
            stable_endpoint: set.stable.into(),
            candidate_endpoint: set.candidate.into(),
        }),
        capabilities: vec![IdunnExpectedCapability {
            capability: capability.clone(),
            schema: capability_schema.clone(),
            compatibility: "v1".into(),
            minimum_capacity: set.minimum_capacity,
        }],
        dependencies: if set.odin_dependency {
            vec![IdunnExpectedDependency {
                kind: "shared-infrastructure".into(),
                capability: "odin.verse-rendezvous".into(),
                schema: "odin.verse-topology.v1".into(),
                compatibility: "v1".into(),
                minimum_capacity: 1,
                startup: "before-promotion".into(),
                provider_id: Some("odin".into()),
                provider_authority: Some("managed-incarnation".into()),
                provider_expected_projection_sha256: Some(digest('5')),
                provider_endpoint: Some("rudp://127.0.0.1:17871".into()),
            }]
        } else {
            Vec::new()
        },
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
    let vectors = [
        ("presence-active.bin", "active", set.minimum_capacity, None),
        ("presence-warming.bin", "warming", set.minimum_capacity, None),
        (
            "presence-active-below-minimum.bin",
            "active",
            set.minimum_capacity.saturating_sub(1),
            Some("expected-capability-000-capacity"),
        ),
    ];
    for (name, state, capacity, disagreement) in vectors {
        if capacity == 0 {
            continue;
        }
        let mut presence = GameCultRuntimePresenceHealthRecord {
            schema_version: cultnet_rs::GAMECULT_RUNTIME_PRESENCE_HEALTH_SCHEMA.into(),
            target: expected.target.clone(),
            expected_projection_sha256: activation.expected_projection_sha256.clone(),
            plan_id: expected.plan_id.clone(),
            incarnation_id: expected.incarnation_id.clone(),
            sealed_release_id: expected.sealed_release_id.clone(),
            activation_witness_sha256: activation.canonical_sha256()?,
            state_schema_generation: None,
            state_contract_sha256: None,
            runtime_id: expected.runtime_id.clone(),
            runtime_instance_id: activation.runtime_instance_id.clone(),
            bound_endpoint: Some(set.candidate.into()),
            capabilities: vec![GameCultRuntimeCapability {
                capability: capability.clone(),
                schema: capability_schema.clone(),
                compatibility: "v1".into(),
                capacity,
            }],
            health_contract: expected.health_contract.clone(),
            state: state.into(),
            detail: DETAIL.into(),
            write_lease_sha256: None,
            signer_identity_id: provider_anchor.identity_id.clone(),
            publisher_sequence: SEQUENCE,
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
