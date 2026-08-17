# Browser Verse Transport Boundary

Date: 2026-08-17

## Status

This is the implementation map for the missing real-browser checkpoint. The
current `samples/eve-two-runtime` proof uses jsdom and one local TypeScript
CultMesh node. It is a clean package-consumer and contract-validation proof,
not browser networking.

The current substrate is incomplete:

- `cultmesh-ts` is a Node package and imports `node:dgram` at its root;
- `cultnet-ts` mixes browser-safe schema contracts with Node transport,
  filesystem, crypto, stream, and UDP modules;
- C# and TypeScript now share `cultnet.database_subscribe.v0`,
  `cultnet.database_unsubscribe.v0`, and `cultnet.database_change_raw.v0`;
  the browser-safe transport package that consumes them does not exist yet;
- Kotlin has a channel-aware WebSocket transport adapter; C# and TypeScript do
  not expose the equivalent browser/server pair;
- Hermodr can lower Odin state into a browser, but it is an infrastructure
  runtime, not the missing clean-consumer game API.

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

- stop calling jsdom a browser runtime;
- split browser-safe CultNet contracts from the Node transport package;
- add the missing subscription messages to the shared generated contract
  catalog instead of retyping them in a browser adapter;
- do not retain a sample-only WebSocket protocol after the canonical adapter
  exists.

## Required Runtime Shape

The browser-facing package must be importable by a standards-mode browser or a
normal bundler without Node polyfills. It exposes a narrow API shaped like:

```ts
const mesh = await CultMeshBrowser.connect({
  rendezvous: odin,
  verseId: "sample.counter",
  providerId: "sample.counter-provider",
});

using surface = await mesh.leaseDocument(eveSurface, "eve:surface:counter");
surface.watch(render);

const receipt = await mesh.invoke(incrementCounter, {}, {
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
- `cultnet.error.v0`.

The WebSocket frame is a transport body, not a second message schema. Payload
size, queue depth, subscription count, and command rate are bounded. Schema,
identity, authority, and idempotency validation happen before provider code.

## Acceptance Gate

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
