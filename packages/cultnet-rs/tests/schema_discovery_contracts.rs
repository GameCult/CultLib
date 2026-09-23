//! R-AB: `schema_discovery.rs` used to `include_str!("../../../contracts/cultnet/…")`, reaching
//! three levels above `src/` and out of `packages/cultnet-rs` entirely. cargo-mutants builds this
//! crate alone (there is no workspace `Cargo.toml` tying it to its siblings, so its own manifest
//! directory is the whole tree cargo-mutants copies) - a path that escapes the package is a path
//! that does not exist in that copy, and the unmutated baseline build failed with "No such file or
//! directory" on every one of the 22 schemas (`rust-mut3.log`, captured on Soul's whole-cut pass).
//!
//! The fix vendors byte-identical copies of the 22 schemas this crate embeds under
//! `packages/cultnet-rs/contracts/cultnet/`, git-tracked so any tool that copies just this
//! package's directory carries them along, and repoints every `include_str!` at that vendored copy
//! instead of the repo-root `contracts/cultnet/`. A build script staging files into `OUT_DIR` was
//! the other option R-AB named, but it does not actually solve this: the script itself would still
//! need to read from outside the package to find its source, and a build running inside
//! cargo-mutants' copy would fail exactly as `include_str!` did. Vendoring - "another route that
//! keeps every source path inside the package" - is what R-AB's ruling names as the alternative,
//! and it is what actually keeps the crate self-contained.
//!
//! Vendoring is a second copy of files this repo already treats as canonical
//! (`contracts/cultnet/*.schema.json`, the shared source multiple runtimes compile against per
//! `docs/cultnet-selection-cut.md` section 1). A second copy can drift silently, so this test is
//! the thing that makes that impossible without a loud, immediate failure: every vendored file is
//! compared byte-for-byte against its repo-root original. It skips (not fails) when the canonical
//! `contracts/` tree is not present beside the package - i.e. exactly the isolated-copy situation
//! R-AB exists to survive - so this check does not reintroduce the same escape it is guarding
//! against.

use std::fs;
use std::path::Path;

/// Every schema this crate's `schema_discovery::builtin_schema_registry` embeds via
/// `include_str!("../contracts/cultnet/…")` - kept in sync with `schema_discovery.rs` by hand;
/// a schema added there and not here is still caught, because the crate would then fail to
/// compile against a file this list never copied to `contracts/cultnet/`.
const VENDORED_SCHEMAS: &[&str] = &[
    "cultnet.hello.schema.json",
    "cultnet.document-mutation-contract.schema.json",
    "cultnet.transport-profile.schema.json",
    "cultnet.login.schema.json",
    "cultnet.register.schema.json",
    "cultnet.verify.schema.json",
    "cultnet.login-success.schema.json",
    "cultnet.error.schema.json",
    "cultnet.sample-change-name.schema.json",
    "cultnet.sample-chat.schema.json",
    "cultnet.document-put.schema.json",
    "cultnet.document-delete.schema.json",
    "cultnet.raw-document-record.schema.json",
    "cultnet.document-put-raw.schema.json",
    "cultnet.snapshot-request.schema.json",
    "cultnet.snapshot-response.schema.json",
    "cultnet.snapshot-response-raw.schema.json",
    "cultnet.schema-catalog-request.schema.json",
    "cultnet.schema-catalog-response.schema.json",
    "cultnet.shard-catalog-request.schema.json",
    "cultnet.shard-catalog-response.schema.json",
    "ghostlight.agent-state.schema.json",
];

#[test]
fn vendored_contract_schemas_are_present_and_match_every_include_str_call() {
    let manifest_dir = Path::new(env!("CARGO_MANIFEST_DIR"));
    let vendored_root = manifest_dir.join("contracts/cultnet");
    for name in VENDORED_SCHEMAS {
        let path = vendored_root.join(name);
        assert!(
            path.is_file(),
            "{name} is embedded by schema_discovery.rs but missing from the vendored \
             contracts/cultnet/ copy at {path:?}"
        );
    }
}

#[test]
fn vendored_contract_schemas_match_the_canonical_repo_root_source() {
    let manifest_dir = Path::new(env!("CARGO_MANIFEST_DIR"));
    let vendored_root = manifest_dir.join("contracts/cultnet");
    // packages/cultnet-rs -> packages -> repo root -> contracts/cultnet
    let canonical_root = manifest_dir.join("../../contracts/cultnet");

    if !canonical_root.is_dir() {
        // R-AB: exactly the isolated-copy case this crate's own vendored files exist to survive
        // (cargo-mutants, or any tool that copies only this package's directory). Nothing to
        // compare against, and that is expected, not a failure.
        eprintln!(
            "skipping: {canonical_root:?} not present beside the package (isolated build, \
             the case R-AB's vendored copies are for)"
        );
        return;
    }

    for name in VENDORED_SCHEMAS {
        let canonical = fs::read_to_string(canonical_root.join(name))
            .unwrap_or_else(|error| panic!("reading canonical contracts/cultnet/{name}: {error}"));
        let vendored = fs::read_to_string(vendored_root.join(name))
            .unwrap_or_else(|error| panic!("reading vendored contracts/cultnet/{name}: {error}"));
        assert_eq!(
            canonical, vendored,
            "packages/cultnet-rs/contracts/cultnet/{name} has drifted from the canonical \
             contracts/cultnet/{name} - re-copy it"
        );
    }
}

#[test]
fn builtin_schema_registry_loads_every_vendored_schema() {
    let registry = cultnet_rs::builtin_schema_registry().expect("registry builds from the vendored schemas");
    let listed = registry.list(&cultnet_rs::CultNetSchemaCatalogOptions::default());
    assert_eq!(
        listed.len(),
        VENDORED_SCHEMAS.len(),
        "one registration per vendored schema file"
    );
}
