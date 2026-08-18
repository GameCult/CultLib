# CultMesh Browser

`cultmesh-browser` is the browser-safe CultMesh client boundary. It owns one
WebSocket session for a stable Verse/provider identity, retains explicit raw
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
  providerId: "sample.counter-provider",
  runtimeId: "counter-browser",
  rendezvous: odin,
});

const counter = await mesh.leaseRawDocument({
  schemaId: "sample.counter_state.v1",
  recordKey: "counter:main",
});

counter.watch(record => renderCounter(decodeCultNetPayload(record)));
```

`invoke(...)` returns the correlated provider response. Routing, schema, and
envelope failures reject immediately with `CultMeshBrowserOperationError`;
callers can branch on its stable `status` and `code` without parsing prose or
waiting for the request timeout. Domain rejections remain typed provider
responses because the domain still owns their receipt schema.

`CultMeshBrowserOdinRendezvous` speaks the canonical CultNet Verse-catalog
messages to one or more configured Odin WebSocket endpoints. Application code
supplies stable identity; it does not own endpoint selection, reconnect, or
resubscription. The physical provider socket is a replaceable route, not the
provider identity.

The browser handshake carries authentication through the host application's
ordinary secure cookie or equivalent upgrade credential. The C# host adapter
rejects endpoints without an authorization predicate unless anonymous local
development is explicitly enabled.

Run `scripts/verify-eve-browser-network.mjs --eve-root <path-to-Eve>` for the
real Chromium + C# headless chronology.
