import { createServer, type RequestListener, type Server } from "node:http";
import { CultCache } from "@gamecult/cultcache-ts";
import { defineDocumentRegistry } from "@gamecult/cultcache-ts/document";
import { decodeWireValue, encodeWireValue } from "cultnet-ts/wire";
import { encodeCultNetMessageForWire, parseCultNetMessage, type CultNetMessage } from "cultnet-ts/contracts";
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
