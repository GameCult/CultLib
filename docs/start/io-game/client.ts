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
