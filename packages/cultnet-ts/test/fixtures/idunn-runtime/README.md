# Idunn runtime fixtures

Written by the Rust owner, not by the TypeScript tests. Regenerate with
`packages/cultnet-rs/examples/idunn_runtime_fixture.rs` (CultLib `f5c597a`; it uses
cultnet-rs only, and Idunn's `IdunnRuntimeActivationLaunch` is that same crate's).
It needs Linux and a machine-id equal to the `machine-id` file here, because the
provider identity is bound to it. On Yggdrasil:

    DOCKER_ARGS='--cap-add SYS_ADMIN --security-opt apparmor=unconfined'     bash ygg-verify.sh <CultLib> HEAD rust 'cd packages/cultnet-rs &&       cp <fixed-machine-id> /tmp/mid && mount --bind /tmp/mid /etc/machine-id &&       cargo run -q --example idunn_runtime_fixture /tmp/out'

then copy `/tmp/out` over this directory. Every `presence-*.bin` was accepted by
`authenticate_runtime_presence_claim` and correlated by
`correlate_runtime_presence_claim` before being written; the
`below-minimum` vector correlates to exactly `expected-capability-000-capacity`.
Ed25519 is deterministic, so the TypeScript signer must reproduce them byte for byte.
