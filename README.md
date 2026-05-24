# CultNet TS

`cultnet-ts` is the TypeScript port of the useful shape of GameCult's
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
- `cultnet-ts`: typed transport, auth, and state replication

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
- `cultnet.sample.change_name.v0`
- `cultnet.sample.chat.v0`

For shared agent state, the first payload contract is the existing Ghostlight
fixture format:

- payload schema version: `ghostlight.agent_state.v0`
- mirrored validator: `contracts/ghostlight.agent-state.schema.json`

That means `EpiphanyAgent` role dossiers and Ghostlight fixtures can travel over
CultNet without inventing a second ontology first.

## Generated Swarm Contracts

`cultnet-ts` can consume canonical JSON Schema files and generate:

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

`cultnet-ts` does not sniff random payloads and pretend that counts as rigor.
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

## Local Fast Lane

For direct-pipe neighbors that already share the same CultCache formatter
contract, `cultnet-ts` now has a raw replication lane:

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

`cultnet-ts` now includes:

- `cultnet.schema_catalog_request.v0`
- `cultnet.schema_catalog_response.v0`
- `CultNetSchemaRegistry`
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

## Quick Example

```ts
import { CultCache, defineDocumentType } from "cultcache-ts";
import {
  CultNetDocumentRegistry,
  CultNetPeer,
  defineCultNetDocumentBinding,
  ghostlightAgentStateGeneratedContract,
} from "cultnet-ts";

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

const peer = new CultNetPeer(stream, {
  wireContract: "cultnet.schema.v0",
});
```

## Security Model

The security helpers intentionally mirror CultLib's shape:

- shared connection key -> SHA-256 derived AES-GCM key
- server-only session signing secret -> HMAC-SHA256 session token signing
- 12-byte nonce
- authentication tag prefixed onto ciphertext
- session token format: `base64url(payload).base64url(signature)`

That means a TypeScript runtime can follow the same cryptographic and session
story as the C# version instead of inventing one more house religion around
"trust me, this pipe is probably fine."

For the legacy `gamecult.networking.v0` contract, `cultnet-ts` normalizes the
encrypted binary fields as base64url strings in the ergonomic TS surface and
maps them back to the exact C# union payload shape at the wire boundary.

## Current Limits

- This repo currently implements the client-side wire contract and pipe
  ergonomics, not a full account server.
- The Ghostlight agent-state mirror is an exact copy of Ghostlight's current
  canonical schema. If Ghostlight changes the contract, this mirror must be
  updated in lockstep instead of playing coy with partial validation.
- The direct transport is any Node `Duplex`; named-pipe/socket server wrappers
  can be added without changing the message contract.

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

It expects these sibling checkouts inside the same workspace:

- `CultNetTS`
- `cultnet-rs`
- `cultcache-rs`
- `CultLib`

The workflow lives at
[.github/workflows/cultnet-interop.yml](.github/workflows/cultnet-interop.yml)
and drives the whole ritual through one command:

```bash
npm run test:interop
```
