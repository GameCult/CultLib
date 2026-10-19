# CultMesh Browser

`cultmesh-browser` is the browser-safe CultMesh client boundary. It owns one
WebSocket session for a stable Verse/authority-runtime target, retains explicit raw
document leases across route replacement, and correlates typed CultNet
operations with provider receipts.

It does not own application state and it does not expose direct document
writes. A renderer watches provider-owned records and invokes provider-owned
operations.

```ts
const odin = new CultMeshBrowserOdinRendezvous({
  endpoints: ["wss://odin.example/verse"],
  runtimeId: "counter-browser.discovery",
});

const mesh = await CultMeshBrowserClient.connect({
  verseId: "sample.counter",
  authorityRuntimeId: "sample.counter-daemon",
  runtimeId: "counter-browser",
  rendezvous: odin,
});

const counter = await mesh.leaseRawDocument({
  schemaId: "sample.counter_state.v1",
  recordKey: "counter:main",
});

counter.watch(record => renderCounter(decodeCultNetPayload(record, counterDocument)));
```

A record's `payload` is a `DocumentPayload`, an opaque type over the same bytes, and
`decodeCultNetPayload(record, definition)` is how it is read: through the CultCache document
definition that says whether the bytes are a plain map, a codec's slot record or a
formatter's, then through the definition's schema. A plain MessagePack decode of a payload
does not compile, and `decodeCultNetPayload` takes no definition-less form. The type stops direct reads
and assignment; it does not stop runtime narrowing (`payload instanceof Uint8Array`) or a cast, which
would read the raw bytes. For the bytes themselves use `documentPayloadBytes`. The definition
comes from `@gamecult/cultcache-ts` (this package imports only its `./document` entry, which
holds no Node built-ins).

`invoke(...)` returns the correlated provider response. Routing, schema, and
envelope failures reject immediately with `CultMeshBrowserOperationError`;
callers can branch on its stable `status` and `code` without parsing prose or
waiting for the request timeout. Domain rejections remain typed provider
responses because the domain still owns their receipt schema.

`CultMeshBrowserOdinRendezvous` speaks the canonical CultNet Verse-catalog
messages to one or more configured Odin WebSocket endpoints. Application code
supplies the Verse and authority-runtime identity; it does not own endpoint
selection, reconnect, or resubscription. This is deliberately distinct from an
Eve surface's `providerId`, which identifies the UI provider inside the Verse.
The physical socket is a replaceable route, not either stable identity.

Odin returns exact authority-route bindings. The browser selects only routes
bound to the requested authority and `cultmesh.documents.v1`, then performs a
session-open handshake. It does not report `connected` until the peer proves
the exact Verse, authority runtime, protocol, and route generation. Operation
responses must also name that authority. Legacy catalogs are accepted only
when one authority runtime makes the endpoint binding unambiguous.

The browser handshake carries authentication through the host application's
ordinary secure cookie or equivalent upgrade credential. The C# host adapter
rejects endpoints without an authorization predicate unless anonymous local
development is explicitly enabled.

Run `scripts/verify-eve-browser-network.mjs --eve-root <path-to-Eve>` for the
real Chromium + C# headless chronology. The witness publishes a lower-priority
decoy endpoint owned by another runtime and proves that neither client connects
to it before and after route rotation.

## Verified content

`fetchVerifiedContent(endpoint, manifest)` and
`fetchVerifiedChunk(endpoint, hash, size)` read CultMesh CDN content over HTTP.
The endpoint comes from CultMesh and never from content: a manifest carries no
URL. No byte is released that failed its chunk or whole-body SHA-256 (WebCrypto,
so a secure context in browsers). Only https is accepted, and http only on
loopback. Options: `maxConcurrentChunkRequests` (1 to 32, default 4), `maxBytes`
(default 256 MiB, checked before any request), `signal` (an abort rejects with
the signal's own reason). Failures are `CultMeshContentError` with a `code`
(`manifest-invalid`, `too-large`, `insecure-endpoint`, `webcrypto-unavailable`,
`network`, `http-status`, `length-mismatch`, `chunk-hash-mismatch`,
`content-hash-mismatch`); messages never carry the endpoint or body bytes. The
byte count actually received is judged, never a length header, because Fetch
decodes content codings.

```ts
import { fetchVerifiedContent } from "cultmesh-browser";

const bytes = await fetchVerifiedContent(endpoint, manifest, {
  signal: AbortSignal.timeout(30_000),
  maxConcurrentChunkRequests: 8,
});
```
