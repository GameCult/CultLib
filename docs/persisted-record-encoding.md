# The encoding of a persisted CultCache record is part of its contract

Changing how a stored record serializes is never a flag flip in this estate.
Consumers verify canonicality by **re-encoding what they decoded and comparing
bytes**, so a record that reads back correctly but re-encodes differently is
refused — and the refusal happens on live data, at startup or on the next read.

Three guards, three separate stores, one idiom:

| store | guard | refusal |
|---|---|---|
| Idunn runtime projection | `Idunn/src/drivers.rs:3611` | `service trust-anchor projection is noncanonical` |
| Idunn deployment brake | `Idunn/src/control_plane.rs` (`read_deployment_brake`) | `deployment brake is noncanonical or keyed by another authority` |
| Idunn trust bindings | `Idunn/src/provisioning.rs:546`, `:630` | `trust store contains a noncanonical or mismatched binding` |

Idunn's control store applies the same rule to its own transactions. Assume the
guard exists and go looking for it before changing any persisted record's
serialization; three independent instances is a house pattern, not a
coincidence.

## What this means for `#[cultcache(key = N, bytes)]`

The attribute switches a `Vec<u8>` slot from a MessagePack array-of-integers
(serde's default) to a real `bin`. Measured, not assumed
(`packages/cultnet-rs/tests/byte_slot_migration.rs`):

- **Reading stays compatible in both directions.** `serde_bytes::ByteBuf`
  accepts an array marker, and a plain `Vec<u8>` accepts `bin`. No dual-read
  path is needed, and reverting a binary is survivable.
- **Reproduction does not.** A record written before the flip can be read but
  can never re-encode to the bytes it was stored as, and its `canonical_sha256`
  moves with it.

So the cost lands entirely on the guards above, not on readability. A field with
persisted records must flip the attribute **and** rewrite its store in the same
change, one target at a time, and never during a deployment — the projection
store is what the observer reads, and refusing it takes the observer down.

A field with no live producer is free. Check for one before assuming a
migration: `IdunnAuthenticatedProviderHealthProjectionRecord` is constructed
only inside a test module, so its byte field costs nothing to convert.

Size, for whether it is worth doing at all: 406 bytes as an array against 278 as
`bin` for a 256-byte payload, and 1234 against 851 for an 848-byte one. About
1.45x on every byte, which matters on a media path and is noise on a control
record.

## `Option<Vec<u8>>` cannot take the attribute yet

The derive emits `serde_bytes::Bytes::new(&self.field)`, which needs `&[u8]`,
and builds the field via `.into_vec()`, which yields `Vec<u8>`. Both are type
errors against an `Option<Vec<u8>>`, so the derive fails loudly rather than
collapsing "absent slot" into "unsigned". `IdunnDeploymentBrakeRecord.signature`
needs an `Option`-aware path in `cultcache-rs-derive` before it can move.

## Fields validated for shape and produced by nothing

`trust_binding_sha256` is declared on two records with different schemas
(`idunn.authenticated_provider_health_projection.v1` in cultnet-rs,
`idunn.authenticated_daemon_health_admission.v1` in odin-core), shape-validated
in both, and computed by nothing in CultLib, Idunn or Odin.

Do not assume that means "omission". It may be a fossil — a contract whose
producer was deleted. The estate has form: Muninn's receiver runs a Reed-Solomon
audio FEC path against parity shards the Rust sender has never emitted, while an
acceptance ledger describes that FEC as having worked. Settling which requires
reading history, not source, and the answer changes whether the right move is to
implement the field or delete it.
