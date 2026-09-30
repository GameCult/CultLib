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
//! the thing that makes that impossible without a loud, immediate failure: every `*.schema.json`
//! under the vendored `contracts/cultnet/` is compared byte-for-byte against its repo-root
//! original. The files are found by listing the directory, not from a list kept by hand, so a newly
//! vendored copy cannot escape the check. It skips (not fails) when the canonical
//! `contracts/` tree is not present beside the package - i.e. exactly the isolated-copy situation
//! R-AB exists to survive - so this check does not reintroduce the same escape it is guarding
//! against. A schema embedded by `schema_discovery.rs` but not vendored fails to compile.

use std::fs;
use std::path::{Path, PathBuf};

/// Every `*.schema.json` under `root`, as paths relative to it, sorted.
fn schema_files(root: &Path) -> Vec<PathBuf> {
    fn walk(root: &Path, dir: &Path, found: &mut Vec<PathBuf>) {
        for entry in fs::read_dir(dir).unwrap_or_else(|error| panic!("listing {dir:?}: {error}")) {
            let path = entry.expect("directory entry").path();
            if path.is_dir() {
                walk(root, &path, found);
            } else if path.to_string_lossy().ends_with(".schema.json") {
                found.push(path.strip_prefix(root).expect("path under root").to_path_buf());
            }
        }
    }
    let mut found = Vec::new();
    walk(root, root, &mut found);
    found.sort();
    found
}

fn vendored_root() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("contracts/cultnet")
}

#[test]
fn vendored_contract_schemas_match_the_canonical_repo_root_source() {
    // packages/cultnet-rs -> packages -> repo root -> contracts/cultnet
    let canonical_root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../contracts/cultnet");

    if !canonical_root.is_dir() {
        // R-AB: exactly the isolated-copy case this crate's own vendored files exist to survive
        // (cargo-mutants, or any tool that copies only this package's directory). Nothing to
        // compare against, and that is expected, not a failure.
        eprintln!(
            "skipping: {canonical_root:?} not present beside the package (isolated build, the case R-AB's vendored copies are for)"
        );
        return;
    }

    let vendored = schema_files(&vendored_root());
    assert!(!vendored.is_empty(), "no vendored schemas found under {:?}", vendored_root());
    for name in vendored {
        let shown = name.display();
        let canonical = fs::read_to_string(canonical_root.join(&name)).unwrap_or_else(|error| {
            panic!("packages/cultnet-rs/contracts/cultnet/{shown} has no canonical contracts/cultnet/{shown}: {error}")
        });
        let copy = fs::read_to_string(vendored_root().join(&name))
            .unwrap_or_else(|error| panic!("reading vendored contracts/cultnet/{shown}: {error}"));
        assert_eq!(
            canonical, copy,
            "packages/cultnet-rs/contracts/cultnet/{shown} has drifted from the canonical contracts/cultnet/{shown} - re-copy it"
        );
    }
}

#[test]
fn builtin_schema_registry_loads_every_vendored_schema() {
    let registry = cultnet_rs::builtin_schema_registry().expect("registry builds from the vendored schemas");
    let listed = registry.list(&cultnet_rs::CultNetSchemaCatalogOptions::default());
    assert_eq!(
        listed.len(),
        schema_files(&vendored_root()).len(),
        "one registration per vendored schema file"
    );
}
