//! Emit canonical bytes for one fixed runtime-presence record.
//!
//! This is the reference the TypeScript fork is tested against. The two forks
//! agree only if `gamecult.runtime_presence_health.v2` means the same bytes in
//! both, and the only way to know that is to have one produce the vector and
//! the other reproduce it. A fixture written by hand for both sides would agree
//! with itself and prove nothing.

use cultnet_rs::{GameCultRuntimeCapability, GameCultRuntimePresenceHealthRecord};

fn main() -> anyhow::Result<()> {
    let record = GameCultRuntimePresenceHealthRecord {
        schema_version: "gamecult.runtime_presence_health.v2".into(),
        target: "heimdall".into(),
        expected_projection_sha256: format!("sha256-{}", "a".repeat(64)),
        plan_id: format!("sha256-{}", "b".repeat(64)),
        incarnation_id: "service-incarnation-1".into(),
        sealed_release_id: format!("sha256-{}", "c".repeat(64)),
        activation_witness_sha256: format!("sha256-{}", "d".repeat(64)),
        state_schema_generation: Some("v1".into()),
        state_contract_sha256: Some(format!("sha256-{}", "e".repeat(64))),
        runtime_id: "heimdall-yggdrasil".into(),
        runtime_instance_id: format!("sha256-{}", "f".repeat(64)),
        bound_endpoint: Some("http://127.0.0.1:14101".into()),
        capabilities: vec![
            GameCultRuntimeCapability {
                capability: "heimdall.access".into(),
                schema: "heimdall.access.v1".into(),
                compatibility: "v1".into(),
                capacity: 2,
            },
            GameCultRuntimeCapability {
                capability: "zeta.runtime".into(),
                schema: "zeta.runtime.v1".into(),
                compatibility: "v1".into(),
                capacity: 1,
            },
        ],
        health_contract: "heimdall.cultnet-rudp-provider-health".into(),
        state: "warming".into(),
        detail: "Heimdall candidate warming".into(),
        write_lease_sha256: None,
        signer_identity_id: "1".repeat(64),
        publisher_sequence: 7,
        observed_at_unix_millis: 1_757_000_000_000,
        signature_algorithm: "ed25519".into(),
        signature: Vec::new(),
        activation_signer_identity_id: "2".repeat(64),
        activation_signature: Vec::new(),
    };

    // The proof payload: both signature fields empty. This is what both keys
    // sign, so it is the vector that matters most.
    let proof = record.canonical_proof_payload()?;
    println!("proof {}", hex(&proof));

    // And the complete record, with both proofs filled, to pin the signed shape.
    let mut signed = record;
    signed.signature = vec![0x11; 64];
    signed.activation_signature = vec![0x22; 64];
    println!("signed {}", hex(&rmp_serde::to_vec(&signed)?));
    Ok(())
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|byte| format!("{byte:02x}")).collect()
}
