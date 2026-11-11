# CultNet TS

`@gamecult/cultnet-ts` is the TypeScript port of the useful shape of GameCult's
networking layer: typed MessagePack messages, direct-pipe framing, CultLib-style
security/session helpers, and state replication that hands typed values into a
`CultCache` instead of making consumer code paw raw database envelopes.

This is not an HTTP wrapper wearing a fake mustache. It is for runtimes that
already trust a direct channel and want a compact shared contract over it.

## Scope

The library currently includes:

- JSON-schema-backed wire contracts for the first CultNet message set
- a MessagePack codec plus 4-byte length-prefixed framing for direct pipes
- a `CultNetPeer` wrapper over any Node `Duplex`
- CultLib-style client/server security options and `Secret` helpers
- session-token signing/validation compatible with the C# CultLib semantics
- explicit wire-contract selection instead of heuristic payload guessing
- `CultCacheTS` replication helpers for document put/delete/snapshot flows
- schema discovery catalog messages and a registry for asking what contracts a
  peer can actually exchange safely
- RUDP socket transport (`CultNetRudpSocketTransportServer` serves many peers from one socket), reconnect policy helpers, and
  `CultNetRudpReconnectLoop` for controller-owned retry scheduling around a
  caller-owned socket factory
- generated TypeScript contracts and runtime verifiers from canonical JSON
  schemas owned elsewhere in the swarm
- an exact mirrored runtime validator and generated TS contract surface for
  Ghostlight's canonical `ghostlight.agent_state.v0` payload

## Why This Exists

We want:

- one shared agent-state vocabulary across `Ghostlight`, `EpiphanyAgent`,
  `VoidBot`, and whatever other little laboratory creature appears next
- typed persistence through `CultCache`
- direct pipes instead of every consumer inventing bespoke JSON fog
- authentication and session semantics that match the original CultLib intent

So the split is:

- `cultcache-ts`: typed local persistence
- `@gamecult/cultnet-ts`: typed transport, auth, and state replication

Same family. Different organs. Nobody needs a second spine growing out of the
transport layer.

## Contracts

Wire contracts live under `contracts/` as JSON Schema files.

Current message families:

- `cultnet.hello.v0`
- `cultnet.login.v0`
- `cultnet.register.v0`
- `cultnet.verify.v0`
- `cultnet.login_success.v0`
- `cultnet.error.v0`
- `cultnet.document_put.v0`
- `cultnet.document_delete.v0`
- `cultnet.document_put_raw.v0`
- `cultnet.snapshot_request.v0`
- `cultnet.snapshot_response.v0`
- `cultnet.snapshot_response_raw.v0`
- `cultnet.snapshot_request.v1`, `cultnet.database_subscribe.v1`, `cultnet.snapshot_response_raw.v1` (typed-selection carriers; no selection engine yet)
- `cultnet.database_change_raw.v0` (optional `commitId` and `commitCount >= 2` mark the changes of one commit; carried and validated here, no TS code groups or applies by them)
- `cultnet.sample.change_name.v0`
- `cultnet.sample.chat.v0`

For shared agent state, the first payload contract is the existing Ghostlight
fixture format:

- payload schema version: `ghostlight.agent_state.v0`
- mirrored validator: `contracts/ghostlight.agent-state.schema.json`

That means `EpiphanyAgent` role dossiers and Ghostlight fixtures can travel over
CultNet without inventing a second ontology first.

## Generated Swarm Contracts

`@gamecult/cultnet-ts` can consume canonical JSON Schema files and generate:

- TypeScript contract classes/interfaces
- runtime schema verifiers
- parse-compatible schema objects that can be handed directly to
  `CultCacheTS`

The manifest lives at [contracts/swarm-contracts.manifest.json](contracts/swarm-contracts.manifest.json).
Right now it points at Ghostlight's canonical agent-state schema, because
Ghostlight owns the swarm's most important mind-shape contract.

Generate the contract layer with:

```bash
npm run contracts:generate
```

That emits [src/generated/swarm-contracts.generated.ts](src/generated/swarm-contracts.generated.ts),
which exports:

- the generated TS contract shape
- named runtime verifier helpers such as
  `validateGhostlightAgentStateGenerated(...)` and
  `parseGhostlightAgentStateGenerated(...)`
- a runtime `validate(...)` / `parse(...)` contract object via
  `defineJsonSchemaContract(...)`
- a contract object that `CultCacheTS` can persist directly without a second
  hand-written Zod mirror

## Wire Contracts

`@gamecult/cultnet-ts` does not sniff random payloads and pretend that counts as rigor.
You choose the contract explicitly.

Current wire-contract options:

- `cultnet.schema.v0`
  - the newer schema-versioned object contract for CultNet-native message
    families such as hello, document replication, and snapshots
- `gamecult.networking.v0`
  - the legacy C# `GameCult.Networking` MessagePack union contract for
    login/register/verify/session/error plus the sample `ChangeNameMessage` and
    `ChatMessage` payloads

So the transport can stay developer-friendly while the binary stops freelancing
away from the C# lineage.

## Content

`@gamecult/cultnet-ts/content` owns the TypeScript shapes of the CultMesh CDN
(`src/content.ts`, the twin of `CultMeshCdn.cs`): the artifact manifest and chunk
reference, their MessagePack form, hash normalization, both record-key grammars,
SHA-256 hashing through WebCrypto, and `packContent`. It is browser-safe: it
imports no `node:` module, so a browser bundle can take it.

- A manifest names its chunks by hash and carries no endpoint. Where the bytes
  are fetched from is the caller's to resolve through CultMesh.
- Metadata is an ordered list of `[key, value]` entries, never an object: a JS
  object reorders integer-like keys, and the entry order is the wire bytes.
  Duplicate keys, integers a JS number cannot hold exactly, and payloads nested
  past the shared depth limit are refused.
- The decoder reads what MessagePack-CSharp reads and refuses what it refuses:
  nil in a string, tag or metadata slot is `null` and is written back as nil,
  a missing trailing slot takes the reference's default, extra slots and trailing
  bytes are skipped (a chunk reference's fifth slot is its element id, a string
  or nil, `""` when absent; `packContent` writes the decimal offset as the id, as the
  reference does), a float-encoded integer is refused, and invalid UTF-8
  decodes to U+FFFD. A nil `chunks` is refused, as the reference's
  `ValidateManifest` refuses it. `content-acceptance-vectors.json` is the
  reference's own verdict on each of those shapes.
- Bytes and keys are held to files the C# reference writes
  (`contracts/cultmesh/content-vectors.cs-written.json`,
  `cdn-record-key-vectors.json`, `content-acceptance-vectors.json`) and to `content-vectors.rs-written.json`.
  The TypeScript-written `content-vectors.ts-written.json` is judged by the C#
  reference. Regenerate it with `npm run build --workspace packages/cultnet-ts`
  and `node scripts/write-cultmesh-content-vectors.mjs`; never edit it by hand.

## Local Fast Lane

For direct-pipe neighbors that already share the same CultCache formatter
contract, `@gamecult/cultnet-ts` now has a raw replication lane:

- `cultnet.document_put_raw.v0`
- `cultnet.snapshot_response_raw.v0`

Those messages carry the canonical persisted MessagePack payload bytes plus the
typed envelope metadata. Paired with `CultCacheTS.putEnvelope(...)`, that means
the receiving cache can ingest the exact payload bytes directly instead of
decoding them into generic object soup and then re-encoding the same document
again.

This is cheaper. It is not magic:

- framing still copies
- MessagePack decoding still happens once on ingest
- ordinary `CultNetPeer` send/receive still materializes messages

But for local or near-local runtimes sharing the same contract, the fast lane
cuts out the dumbest part of the trip.

## Schema Discovery

Apps sharing a CultNet space should be able to ask each other what they can
exchange without resorting to folklore.

`@gamecult/cultnet-ts` now includes:

- `cultnet.schema_catalog_request.v0`
- `cultnet.schema_catalog_response.v0`
- `CultNetSchemaRegistry`
- `CultNetSchemaCatalog`
- `cultNetBuiltinSchemaRegistry`

Descriptors include:

- canonical `schemaId`
- discovery `kind`
- optional `schemaVersion`
- optional `documentType`
- supported `wireContracts`
- a SHA-256 `contentHash`
- optional inline `schemaJson`

The inline schema body travels as canonical JSON text, not as a loose object
graph. Discovery is supposed to reduce ambiguity, not smuggle in a cute new
polymorphic attack surface.

`CultNetSchemaRegistry` owns local schema definitions and can answer catalog
requests. `CultNetSchemaCatalog` owns remote descriptors and applies catalog
responses. `CultNetPeer.requestSchemaCatalog(...)`,
`fetchSchemaDescriptors(...)`, and `syncSchemaCatalog(...)` perform the common
message-id-matched request/response cycle so callers do not need to hand-roll
listener cleanup around every discovery exchange.

Shard discovery follows the same split: `CultNetShardCatalog` owns imported
`cultnet.shard_catalog_response.v0` descriptors, filters by schema id and
record key, and can answer `cultnet.shard_catalog_request.v0`. `CultNetPeer`
also exposes `requestShardCatalog(...)`, `fetchShardDescriptors(...)`, and
`syncShardCatalog(...)` for the same message-id-matched exchange. The built-in
schema registry advertises the shard catalog request/response schemas as
schema-v0 wire messages, so callers can discover the contracts before using
the helper path.

## Quick Example

```ts
import { CultCache, defineDocumentType } from "@gamecult/cultcache-ts";
import {
  CultNetDocumentRegistry,
  createTcpFramedCultNetPeer,
  defineCultNetDocumentBinding,
  ghostlightAgentStateGeneratedContract,
} from "@gamecult/cultnet-ts";

const ghostlightStateDefinition = defineDocumentType({
  type: "ghostlight.agent-state",
  schema: ghostlightAgentStateGeneratedContract,
});

const registry = new CultNetDocumentRegistry([
  defineCultNetDocumentBinding({
    definition: ghostlightStateDefinition,
    payloadSchemaVersion: "ghostlight.agent_state.v0",
  }),
]);

const peer = createTcpFramedCultNetPeer(stream, {
  runtimeId: "node-worker",
  wireContract: "cultnet.schema.v0",
  transportId: "worker-tcp",
});
```

## Security Model

The security helpers intentionally mirror CultLib's shape:

- shared connection key -> SHA-256 derived AES-GCM key
- server-only session signing secret -> HMAC-SHA256 session token signing
- 12-byte nonce
- authentication tag prefixed onto ciphertext
- session token format: `base64url(payload).base64url(signature)`
- versioned C#/Python-compatible session token payloads:
  `userGuidN|expiresUnix|sessionVersion`, while legacy two-field tokens still
  validate with session version `0`

That means a TypeScript runtime can follow the same cryptographic and session
story as the C# version instead of inventing one more house religion around
"trust me, this pipe is probably fine."

For the legacy `gamecult.networking.v0` contract, `@gamecult/cultnet-ts` normalizes the
encrypted binary fields as base64url strings in the ergonomic TS surface and
maps them back to the exact C# union payload shape at the wire boundary.

## Current Limits

- This repo currently implements the client-side wire contract and pipe
  ergonomics, not a full account server.
- The Ghostlight agent-state mirror is an exact copy of Ghostlight's current
  canonical schema. If Ghostlight changes the contract, this mirror must be
  updated in lockstep instead of playing coy with partial validation.
- The preferred direct transport path is `CultNetTransportConnection`.
  `createTcpFramedCultNetPeer(...)` wraps a Node `Duplex` in the shared
  `tcp_framed` profile before schema messages reach `CultNetPeer`. Passing a
  raw `Duplex` to `CultNetPeer` remains as compatibility for older callers.
- The interop peer still keeps the TCP-framed lane for compatibility, but its
  serve and dial commands also exercise schema-v0 over the shared RUDP socket
  transport and advertise both transport profiles.

## Write receipt replay runner

`WriteTracker` is the client half of `contracts/cultnet/interop/write-receipt.md`: a pure state machine on an abstract
clock that owns one decision, how each sent write settles. `tools/replay-write-receipt-vectors.mjs` replays
`write-receipt-vectors.json` against it (build first, then `node tools/replay-write-receipt-vectors.mjs`): it prints
`PASS`/`FAIL` per case and exits nonzero on any failure. Rust and C# port the same runner by implementing this interface
and replaying the same file.

Tracker interface (times are integer milliseconds on the vector's clock; the constructor takes the window):

| Call | Meaning |
| --- | --- |
| `new WriteTracker({ windowMs, onSettle })` | `windowMs` = 5000 when the server's hello lists both `cultnet.write_admitted.v0` and `cultnet.error.v1`, else 500. `onSettle(id, settlement)` fires once per write. |
| `send(id, atMs)` | A write with that `messageId` was sent. Its window runs from here, not from any transport ack. |
| `frame(message, atMs)` | A decoded frame arrived. `write_admitted` and `error.v1` settle the write they name; `error.v0`, or `error.v1` with nil `messageId`, refuses a write only when exactly one is unanswered; an id not in flight settles nothing. |
| `tick(atMs)` | Advance the clock. Every unanswered write with `atMs - sentAt >= windowMs` settles `unconfirmed(window_elapsed)`. `send`, `frame` and `sessionEnd` tick first. |
| `sessionEnd(atMs)` | Every unanswered write settles `unconfirmed(session_ended)`. |
| `state(id)` | `pending`, or the settlement plus `recorded`: `admitted_late` / `refused_late`, set by the first answer that arrives after `unconfirmed`. A settlement never changes. |

Runner, per case in `scenarios.cases` (the transport ack is not an input: delivery is not an answer):

1. The fake server states its capability the only way a real one does, in a hello (`server: "receipt"` lists both frames, `"legacy"` lists none); the window comes from that hello and must equal `constants[case.window]`.
2. Events run in `atMs` order: `send` -> `send(id)`, `frame` -> encode the frame to MessagePack, decode and validate it as the client would, then `frame(message)`, `tick` -> `tick`, `sessionEnd` -> `sessionEnd`, `transportAck` -> nothing.
3. For each id in `expect`, the settlement given to the caller has the expected `outcome` (and `code` or `reason` when given), `state(id).recorded` equals the expected `recorded` (absent when absent), and nothing settled twice or outside `expect`.

The runner also decodes each `wire[].messagePackHex` and compares the fields and the re-encoded bytes to the vector, and requires every `schemaInvalid[]` frame to be refused by the decoder. `dedupe.cases` and `contentHash` are server vectors and not replayed here.

## Development

```bash
npm install
npm run contracts:generate
npm run build
npm run typecheck
npm run test
```

## Interop CI

The cross-runtime interop harness is meant to run in GitHub Actions on
`windows-latest`.

It expects this CultLib checkout with the in-tree TypeScript, Rust, Python,
C#, and Kotlin packages present. The workflow installs the toolchains, builds
the Kotlin interop jar through `packages/cultmesh-kotlin/build.ps1`, and then
lets the TypeScript harness build the Rust and C# interop peers it needs.

The workflow lives at
[.github/workflows/cultnet-interop.yml](.github/workflows/cultnet-interop.yml)
and drives the whole ritual through one command:

```bash
npm run test:interop
```
