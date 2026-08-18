# Browser Verse Transport Boundary

Date: 2026-08-17

## Status

This is the implementation map for the browser Verse boundary.
`samples/eve-two-runtime` remains the clean package-consumer/jsdom checkpoint.
`samples/eve-browser-network` runs a durable C# provider, a real Chromium Eve
lowerer, an independent C# headless observer, and a separate local Odin fixture
over authenticated binary CultNet WebSocket routes. It proves one canonical
command effect, idempotent retry, shared observation, provider restart on a new
physical route, browser rediscovery, retained lease resubscription, and a second
canonical command after reconnection.

The current substrate has a browser client boundary and local live proof, but
not a complete public Verse proof:

- `cultmesh-ts` is a Node package and imports `node:dgram` at its root;
- `cultnet-ts/contracts` is the browser-safe contract entrypoint; the package
  root still includes Node transport, filesystem, crypto, stream, and UDP
  modules and must not enter browser bundles;
- C# and TypeScript now share `cultnet.database_subscribe.v0`,
  `cultnet.database_unsubscribe.v0`, and `cultnet.database_change_raw.v0`;
  `cultmesh-browser` consumes those messages as explicit document leases;
- `cultmesh-browser` owns stable identity, route refresh, resubscription, and
  typed operation correlation without exposing direct document writes;
- `CultMeshBrowserOdinRendezvous` resolves stable Verse/provider identity with
  `cultmesh.verse_catalog_request.v0` and
  `cultmesh.verse_catalog_response.v0`; application callbacks no longer need to
  smuggle provider URLs into the browser client;
- Kotlin has a channel-aware WebSocket transport adapter, TypeScript has the
  browser client, and `GameCult.Networking.WebSockets` provides the bounded C#
  schema server host adapter;
- Hermodr can lower Odin state into a browser, but it is an infrastructure
  runtime, not the clean-consumer game API;
- the sample's separate local Odin fixture serves the production Verse-catalog
  contract and is restarted with a changed provider route; the same Chromium
  client automatically rediscovers that route.
- the sample validates its surface against Eve's canonical TypeScript contract,
  but its provider still carries a minimal local C# DTO mirror. The
  released-artifact gate requires the renderer-neutral C# Eve surface contract
  as an independently consumable Eve artifact; Unity must not be that contract's
  package owner.

## Authority Map

Owner:

- CultNet owns binary WebSocket framing and schema-v0 message validation.
- CultMesh owns stable Verse/provider identity, document leases,
  subscriptions, route replacement, and command/receipt correlation.
- Odin owns discovery of the current `wss://` candidate for a stable Verse and
  provider identity.
- The provider daemon owns application state, operations, receipts, and the Eve
  surface.
- Eve owns surface and command contracts; the browser lowerer owns DOM only.

Inputs:

- an Odin-discovered WebSocket transport candidate;
- stable Verse and provider identities;
- authenticated session evidence;
- requested schema ids and record keys;
- typed operation invocations and idempotency keys;
- canonical CultNet schema-v0 MessagePack messages.

Outputs:

- leased typed document snapshots and live changes;
- typed operation receipts;
- explicit connection/session diagnostics;
- deterministic unsubscribe and disposal;
- route replacement without changing provider or document identity.

Derived state:

- browser caches, DOM trees, loading/error presentation, and optimistic visual
  affordances are projections only;
- a socket URL is a disposable discovery candidate, never provider identity;
- reconnect status is observable session state, not application truth.

Forbidden writers:

- browser lowerers cannot mutate provider documents directly;
- Hermodr, service workers, renderer caches, and app-specific HTTP endpoints
  cannot become alternate gameplay authorities;
- no JSON polling API may mirror the database as a second state system;
- `node:dgram`, Node streams, filesystem stores, and Node crypto cannot enter a
  browser bundle;
- a late socket, stale subscription generation, or reconnect replay cannot
  overwrite a newer session;
- application code cannot own reconnect sleeps, endpoint ranking, or
  resubscription loops.

Shared paths:

- initial load, live update, reconnect, tab background/resume, command retry,
  and provider route rotation all use one CultMesh document-lease/session path;
- browser, Unity, headless C#, and future native clients consume the same
  provider-owned documents and receipts;
- every command crosses the same typed operation boundary regardless of which
  Eve lowerer emitted it.

Deletion line:

- the jsdom checkpoint remains labeled as a Node-hosted DOM proof;
- browser applications import `cultmesh-browser` and
  `cultnet-ts/contracts`, never the Node-only package roots;
- subscription messages remain one shared schema contract across the C# and
  TypeScript catalogs;
- the real provider adapter must consume the canonical WebSocket message lane;
  no sample-only WebSocket protocol is permitted.

## Required Runtime Shape

The browser-facing package must be importable by a standards-mode browser or a
normal bundler without Node polyfills. It exposes a narrow API shaped like:

```ts
const mesh = await CultMeshBrowserClient.connect({
  rendezvous: odin,
  verseId: "sample.counter",
  providerId: "sample.counter-provider",
  runtimeId: "sample.browser",
});

using surface = await mesh.leaseRawDocument({
  schemaId: "gamecult.eve.surface.v1",
  recordKey: "eve:surface:counter",
});
surface.watch(record => render(decodeCultNetPayload(record)));

const receipt = await mesh.invoke({
  serviceId: "sample.counter",
  operation: "increment",
  payloadSchema: "sample.increment.v1",
  payload: {},
  idempotencyKey: crypto.randomUUID(),
});
```

The names are illustrative. The ownership is not: application code supplies
identity and intent; CultMesh owns route/session continuity; the provider owns
the result.

## Wire Contract

WebSocket carries binary MessagePack CultNet schema-v0 messages. The first
implementation must support:

- `cultnet.hello.v0`;
- authentication/session verification;
- `cultnet.snapshot_request.v0` and
  `cultnet.snapshot_response_raw.v0`;
- `cultnet.database_subscribe.v0`,
  `cultnet.database_unsubscribe.v0`, and
  `cultnet.database_change_raw.v0`;
- `cultnet.operation_request.v0` and
  `cultnet.operation_response.v0`;
- `cultmesh.verse_catalog_request.v0` and
  `cultmesh.verse_catalog_response.v0` on the Odin route;
- `cultnet.error.v0`.

The WebSocket frame is a transport body, not a second message schema. Payload
size, queue depth, subscription count, and command rate are bounded. Schema,
identity, authority, and idempotency validation happen before provider code.

## Acceptance Gate

Current executable coverage from `scripts/verify-eve-browser-network.mjs`:

- authenticated binary C# WebSocket provider;
- separate local Odin fixture using the canonical Verse-catalog messages;
- real Chromium and retained C# headless consumers, both discovered through
  the same Odin identity;
- shared surface and counter document leases;
- Eve button to typed operation to provider receipt to shared state;
- duplicate idempotency key produces one effect;
- provider restart on a different physical port rehydrates the `.cc` state;
- the retained browser lease rediscovers through Odin, resubscribes, and
  commits a second provider-authored receipt;
- the retained C# lease independently rediscovers, resubscribes, and observes
  both canonical receipt ids without application reconnect code;
- generated browser bundle contains no `node:` builtin import.

The full public Verse gate still requires the deployed Odin daemon rather than
the local contract fixture and released-artifact-only .NET consumption. The
source-contract gate runs on Windows and Linux CI.

From an empty temporary consumer using packed/released artifacts only:

1. start a provider daemon with one durable counter document and one validated
   Eve surface;
2. advertise it by stable identity through a local Odin fixture or real Odin;
3. connect one real Chromium page and one C# headless client;
4. lease the same surface and counter document in both runtimes;
5. click the browser button and require one provider-authored canonical receipt;
6. observe the same state version and receipt id in both clients;
7. disconnect or rotate the physical WebSocket endpoint, then require both
   leases to resume without application reconnect code;
8. restart the provider and prove state plus receipt persistence;
9. reject malformed schemas, direct document writes, duplicate command
   effects, stale subscription generations, and unsupported authority;
10. inspect the browser bundle and fail if it contains Node builtin imports.

The gate runs on Windows and Linux. A screenshot is useful presentation
evidence, but the receipt/state chronology is the authority proof.
