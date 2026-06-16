# CultNet RS

`cultnet-rs` is the Rust sibling of `cultnet-ts`: typed MessagePack messages,
4-byte length-prefixed direct-pipe framing, CultLib-shaped security helpers, and
CultCache replication without making callers paw through raw envelopes like we
lost a bet.

The contract is intentionally boring:

- `cultnet.schema.v0` sends schema-versioned MessagePack objects.
- `gamecult.networking.v0` maps the legacy C# union shape explicitly.
- schema discovery uses explicit catalog request/response messages; no inbound
  auto-detect sludge.
- document put/delete/snapshot messages move typed CultCache entries.
- raw document put/snapshot messages preserve canonical MessagePack payload
  bytes for bit-compatible neighbors.
- hello messages advertise `supportedMutationContracts`, so callers discover
  which document types are read-only, which accept typed intents, which
  authority owns the mutation, and which receipt documents prove the outcome.
- payloads are decoded through registered Rust types before entering the cache.

This crate is not an HTTP wrapper. It is the wire vocabulary Epiphany,
Ghostlight, VoidBot, and the rest of the swarm can share.

The public API surface is the schema plus its mutation contracts. A runtime does
not expose a pile of bespoke verbs and hope everyone remembers the ritual; it
advertises typed documents, typed intents, and typed receipts. Polite machines
knock on the contract before touching the furniture.

## Receipts

```powershell
cargo test
```

The initial tests prove:

- CultLib-compatible AES-GCM string encryption and HMAC session tokens
- 4-byte big-endian MessagePack framing
- schema-versioned message round trips
- legacy `gamecult.networking.v0` login mapping
- schema discovery catalog responses with canonical JSON schema hashes
- CultCache snapshot replication through registered typed documents
- raw snapshot replication that preserves the original payload bytes
- document mutation contract advertisement through hello frames and registries

## Schema Discovery

`cultnet-rs` now ships a built-in schema registry for the shared swarm contract
surface:

- core wire messages
- legacy `gamecult.networking.v0` auth/sample payloads
- schema catalog request/response messages
- the canonical `ghostlight.agent-state` document payload schema

Use `builtin_schema_registry()` when you want the standard catalog, or register
your own closed-world schema set with `CultNetSchemaRegistry`. Discovery stays
explicit on purpose: peers advertise only the contracts they were compiled to
understand, the same way CultCache only consumes the document types you
registered instead of pretending polymorphism is a public park.

## Local Fast Lane

`cultnet-rs` now mirrors the raw replication seam from `cultnet-ts`:

- `cultnet.document_put_raw.v0`
- `cultnet.snapshot_response_raw.v0`

Those messages carry the exact persisted MessagePack payload bytes from
CultCache along with the typed envelope metadata. Combined with
`CultCache::put_envelope::<T>()`, that lets a bit-compatible neighbor ingest the
document without re-encoding the payload first.

That still is not zero-copy in the religious sense. Frames allocate, bytes move,
and the receiving cache still decodes once to keep typed reads and validation
honest. The win is narrower and realer: identical payload bytes stop getting
decoded into generic sludge and then encoded right back into the same bytes for
no reason.

## CultMesh Rust Facade

Rust is still the low-level substrate runtime, not a clone of the C# game
server. It does now expose the small CultMesh entrypoint needed for practical
contact and authority ergonomics:

```rust
use cultnet_rs::{
    CultMesh, CultMeshPeerCard, CultMeshRudpSocketOptions,
};

let mut server = CultMesh::create_rudp_server(
    "rust-server",
    0x1020_3040,
    CultMeshRudpSocketOptions::default(),
)?;
let port = server.profile.transports[0].port.unwrap();

let peer = CultMeshPeerCard::new(
    "rust-server",
    "local",
    [format!("rudp://127.0.0.1:{port}")],
)
.with_roles(["schema"]);

let mut client = CultMesh::create_rudp_client_for_peer(
    "rust-client",
    0x1020_3040,
    &peer,
    CultMeshRudpSocketOptions::default(),
)?;
client.connect(b"join".to_vec())?;
```

For caller-owned reconnect loops, `CultNetRudpReconnectLoop` keeps transport
construction outside the library while sharing the portable retry controller:

```rust
use cultnet_rs::{
    CultNetReconnectPolicyOptions, CultNetRudpReconnectLoop, create_reconnect_policy,
};

let policy = create_reconnect_policy(CultNetReconnectPolicyOptions::default());
let mut reconnect = CultNetRudpReconnectLoop::new(policy, b"join".to_vec(), || {
    CultMesh::create_rudp_client_for_peer(
        "rust-client",
        0x1020_3040,
        &peer,
        CultMeshRudpSocketOptions::default(),
    )
});

reconnect.start()?;
let decision = reconnect.handle_closed(now_ms, 0);
if decision.is_some() {
    reconnect.reconnect_if_due(now_ms)?;
}
```

`CultMesh::parse_rudp_endpoint(...)` handles advertised `rudp://host:port`
contact hints, and `CultMeshAuthorityLeaseCatalog` keeps trust separate from
discovery. A peer card can say where to dial; a lease decides whether that peer
is allowed to own a role or shard.
