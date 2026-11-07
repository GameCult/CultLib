# Recipe: a two-client .io game over CultCache + CultMesh (TypeScript)

A tiny arena. One process is the authority: it holds a single `io.arena_state` document in a CultCache
and serves it over CultMesh. Each client discovers the authority through the Odin catalog, leases the
document, and invokes `join`, `move` and `leave` operations. Two clients see each other join, move and
leave because both lease the same document.

## What ran, what did not

| Piece | Status |
| --- | --- |
| Two clients, real WebSockets, real `@gamecult/cultmesh-browser` client, one arena: join, see each other, move, leave | **tested** (`arena.test.ts`) |
| Edge clamp, and the source below matching the files that ran | **tested** |
| Browser bundle builds and the page is served | **tested** (HTTP fetch only) |
| The page running in a real browser, two windows | **not yet**: no browser on the host that built this recipe |
| Run by CI / `npm run test:ts` | **not yet**: the recipe has its own `package.json` (for `ws`) outside the workspaces; run it by hand as below |
| Authority state on disk (`.cc` file) | **not yet**: the arena cache is in memory |
| Remote play: Odin roots and a signed route | **not yet**: the client uses `local-development` trust, loopback only |
| Dropped players: a closed tab that never sent `leave` stays in the arena | **not yet**: operations do not carry a connection identity to the authority |
| `CultCache` in the browser | **not yet**: the `@gamecult/cultcache-ts` entry imports Node built-ins, so the client keeps the decoded document in a variable and only `./document` is imported |

No `@gamecult/cultmesh-ts` or `@gamecult/cultnet-ts` TypeScript authority server exists. The server below is the CultNet
message handling a provider owes, written in this recipe with the `ws` package.

## Run it

```
npm ci --ignore-scripts                          # repo root
(cd packages/cultnet-ts && npm run build)        # then cultcache-ts, cultmesh-browser, same command
cd docs/start/io-game && npm install
npm test                                         # node --test arena.test.ts, Node 24
node serve.ts                                    # http://127.0.0.1:8080, open it twice
```

Tested on Node v24.14, which runs `.ts` files directly (no type check; `tsc` is not part of this recipe).

## schema.ts

```ts
import { defineDocumentType } from "@gamecult/cultcache-ts/document";

export interface Player { x: number; y: number }
export interface ArenaState { players: Record<string, Player> }

// The one shared document: every player's position. The server owns it; clients lease it.
export const arenaDocument = defineDocumentType({
  type: "io.arena_state",
  schemaId: "io.arena_state.v1",
  schema: { parse: (value: unknown) => value as ArenaState },
});

export const arenaKey = "arena:main";
export const verseId = "io.arena";
export const authorityRuntimeId = "io.arena-daemon";
export const arenaSize = 100;
```

## server.ts

```ts
import { createServer, type RequestListener, type Server } from "node:http";
import { CultCache } from "@gamecult/cultcache-ts";
import { defineDocumentRegistry } from "@gamecult/cultcache-ts/document";
import { decodeWireValue, encodeWireValue } from "@gamecult/cultnet-ts/wire";
import { encodeCultNetMessageForWire, parseCultNetMessage, type CultNetMessage } from "@gamecult/cultnet-ts/contracts";
import { WebSocketServer, type WebSocket } from "ws";
import { arenaDocument, arenaKey, arenaSize, authorityRuntimeId, verseId, type ArenaState } from "./schema.ts";

const bytesToBase64 = (b: Uint8Array) => Buffer.from(b).toString("base64");
const base64ToBytes = (s: string) => new Uint8Array(Buffer.from(s, "base64"));

// One process plays both CultMesh roles: Odin (/odin tells clients where the authority is) and the authority (/mesh).
export async function startArena(port = 0, host = "127.0.0.1", onRequest?: RequestListener) {
  const cache = CultCache.builder().withRegistry(defineDocumentRegistry(arenaDocument)).build();
  await cache.put(arenaDocument, arenaKey, { players: {} });
  const http: Server = createServer(onRequest ?? ((_, res) => { res.writeHead(404).end(); }));
  await new Promise<void>(resolve => http.listen(port, host, resolve));
  const endpoint = `ws://${host}:${(http.address() as { port: number }).port}`;

  const subscribers = new Map<WebSocket, string[]>();
  const send = (ws: WebSocket, m: CultNetMessage) => ws.send(encodeWireValue(encodeCultNetMessageForWire(m)));
  const record = () => ({
    schemaId: arenaDocument.schemaId, schemaName: arenaDocument.type, schemaVersion: arenaDocument.schemaId,
    schemaContentHash: arenaDocument.schemaId, recordKey: arenaKey, storedAt: new Date().toISOString(),
    payloadEncoding: "messagepack" as const,
    payload: encodeWireValue(cache.getRequired(arenaDocument, arenaKey)) as never,
    sourceRuntimeId: authorityRuntimeId,
  });
  const publish = async (next: ArenaState) => {
    await cache.put(arenaDocument, arenaKey, next);
    for (const [ws, ids] of subscribers) for (const subscriptionId of ids) {
      send(ws, { schemaVersion: "cultnet.database_change_raw.v0", messageId: crypto.randomUUID(), subscriptionId, changeKind: "updated", document: record() });
    }
  };

  let nextId = 0;
  const operations: Record<string, (p: any) => Promise<object>> = {
    join: async () => {
      const id = `p${++nextId}`;
      const state = cache.getRequired(arenaDocument, arenaKey);
      await publish({ players: { ...state.players, [id]: { x: Math.floor(Math.random() * arenaSize), y: Math.floor(Math.random() * arenaSize) } } });
      return { id };
    },
    move: async ({ id, dx, dy }: { id: string; dx: number; dy: number }) => {
      const state = cache.getRequired(arenaDocument, arenaKey);
      const me = state.players[id];
      if (!me) return { ok: false };
      const clamp = (v: number) => Math.max(0, Math.min(arenaSize, v));
      await publish({ players: { ...state.players, [id]: { x: clamp(me.x + Math.sign(dx)), y: clamp(me.y + Math.sign(dy)) } } });
      return { ok: true };
    },
    leave: async ({ id }: { id: string }) => {
      const { [id]: _gone, ...rest } = cache.getRequired(arenaDocument, arenaKey).players;
      await publish({ players: rest });
      return { ok: true };
    },
  };

  const wss = new WebSocketServer({ server: http });
  wss.on("connection", (ws, req) => {
    const odin = req.url === "/odin";
    ws.on("close", () => subscribers.delete(ws));
    ws.on("message", async raw => {
      const m = parseCultNetMessage(decodeWireValue(new Uint8Array(raw as Buffer)));
      if (odin && m.schemaVersion === "cultmesh.verse_catalog_request.v0") {
        send(ws, {
          schemaVersion: "cultmesh.verse_catalog_response.v0", messageId: m.messageId,
          verses: [{
            verseId, displayName: "Arena", authorityModel: "ServerAuthoritative",
            compatibility: { transportVersion: "cultmesh.v1", rulesHash: "arena-v1", compatibleVerseIds: [], requiredPluginIds: [], optionalPluginIds: [] },
            discoveryEndpoints: [`${endpoint}/mesh`], authorityRuntimeIds: [authorityRuntimeId],
            authorityRoutes: [{ authorityRuntimeId, endpoint: `${endpoint}/mesh`, protocolIds: ["cultmesh.documents.v1"], priority: 0, generation: "arena-1" }],
          }],
        });
      } else if (m.schemaVersion === "cultmesh.session_open.v2") {
        send(ws, { schemaVersion: "cultmesh.session_accepted.v2", messageId: m.messageId, accepted: true, verseId: m.verseId, authorityRuntimeId,
          protocolId: m.protocolId, routeGeneration: m.routeGeneration, clientNonce: m.clientNonce, providerKeyId: "", providerSignature: "" });
      } else if (m.schemaVersion === "cultnet.database_subscribe.v0") {
        subscribers.set(ws, [...(subscribers.get(ws) ?? []), m.subscriptionId]);
        send(ws, { schemaVersion: "cultnet.snapshot_response_raw.v0", messageId: m.messageId, documents: [record()] });
      } else if (m.schemaVersion === "cultnet.database_unsubscribe.v0") {
        subscribers.set(ws, (subscribers.get(ws) ?? []).filter(id => id !== m.subscriptionId));
      } else if (m.schemaVersion === "cultnet.operation_request.v0") {
        const handler = operations[m.operation];
        const receipt = handler ? await handler(decodeWireValue(base64ToBytes(m.payload))) : undefined;
        send(ws, {
          schemaVersion: "cultnet.operation_response.v0", messageId: m.messageId, serviceId: m.serviceId, operation: m.operation,
          status: handler ? "accepted" : "invalid", payloadSchema: "io.receipt.v1", payloadEncoding: "messagepack-base64",
          payload: bytesToBase64(encodeWireValue(receipt ?? { ok: false })), sourceRuntimeId: authorityRuntimeId,
        });
      }
    });
  });

  return {
    odinUrl: `${endpoint}/odin`,
    state: () => cache.getRequired(arenaDocument, arenaKey),
    close: async () => { for (const c of wss.clients) c.terminate(); wss.close(); await new Promise(r => http.close(r)); },
  };
}
```

## client.ts

```ts
import { CultMeshBrowserClient, CultMeshBrowserOdinRendezvous, decodeCultNetOperationPayload, decodeCultNetPayload } from "@gamecult/cultmesh-browser";
import { arenaDocument, arenaKey, authorityRuntimeId, verseId, type ArenaState } from "./schema.ts";

// Runs unchanged in a browser or in Node 22+: both have a global WebSocket.
export async function joinArena(odinUrl: string, runtimeId: string, onState: (state: ArenaState, myId: string) => void) {
  const rendezvous = new CultMeshBrowserOdinRendezvous({ endpoints: [odinUrl], runtimeId: `${runtimeId}.discovery` });
  const mesh = await CultMeshBrowserClient.connect({
    verseId, authorityRuntimeId, runtimeId, rendezvous,
    trust: { mode: "local-development" }, // loopback only; remote Verses need Odin roots
  });
  let latest: ArenaState | undefined; // CultCache itself pulls in Node built-ins, so the browser client keeps the decoded document
  const call = async (operation: string, payload: object) =>
    decodeCultNetOperationPayload<{ id?: string }>(await mesh.invoke({ serviceId: "io.arena", operation, payloadSchema: "io.request.v1", payload }));

  const lease = await mesh.leaseRawDocument({ schemaId: arenaDocument.schemaId, recordKey: arenaKey });
  const { id } = await call("join", {});
  lease.watch(record => {
    if (!record) return;
    latest = decodeCultNetPayload(record, arenaDocument);
    onState(latest, id!);
  });
  return {
    id: id!,
    move: (dx: number, dy: number) => call("move", { id, dx, dy }),
    state: () => latest,
    leave: async () => { await call("leave", { id }); lease.dispose(); await mesh.dispose(); },
  };
}
```

`browser.ts`, `index.html` and `serve.ts` in [`io-game/`](io-game/) draw the canvas and bundle the client with esbuild.
