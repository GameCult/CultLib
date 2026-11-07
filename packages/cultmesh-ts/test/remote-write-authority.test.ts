// A peer's put or delete is applied only when the server's write authority grants it. Ports the behaviours of
// cultmesh-py test_remote_write_authority.py (no forwarding: this server does not forward non-primary writes).
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";
import { z } from "zod";
import { documentPayloadFromBytes, defineDocumentType } from "@gamecult/cultcache-ts";
import { encodeWireValue } from "@gamecult/cultnet-ts/wire";
import {
  CultNetDocumentRegistry,
  cultNetWriteNotAuthorizedError,
  defineCultNetDocumentBinding,
  type CultNetMessage,
  type CultNetPeer,
} from "@gamecult/cultnet-ts";
import {
  CultMesh,
  CultMeshGrantAllWrites,
  type CultMeshRemoteWriteAuthority,
  type CultMeshRemoteWriteDecision,
  type CultMeshRemoteWriteRequest,
  type CultMeshRudpDocumentDelete,
  type CultMeshRudpDocumentPut,
} from "../src/index";

const SCHEMA_ID = "cultmesh.authority_note.v0";
const ALIAS = "runtime.generated.authority-note.ui.42";
const KEY_A = "tests:authority:a";
const KEY_B = "tests:authority:b";

const note = defineDocumentType({
  type: "cultmesh.authority_note",
  schemaId: SCHEMA_ID,
  compatibleSchemaIds: [ALIAS],
  schema: z.object({ noteId: z.string(), text: z.string() }),
  name: "noteId",
});

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

function put(recordKey: string, extra: Record<string, unknown> = {}, schemaId = SCHEMA_ID, payload?: Uint8Array) {
  return {
    schemaVersion: "cultnet.document_put_raw.v0" as const,
    messageId: `put-${recordKey}`,
    document: {
      schemaId,
      recordKey,
      storedAt: "2026-11-07T00:00:00Z",
      payloadEncoding: "messagepack" as const,
      payload: documentPayloadFromBytes(payload ?? encodeWireValue({ noteId: recordKey, text: "peer" })),
    },
    ...extra,
  } as CultNetMessage;
}

function del(recordKey: string, schemaId = SCHEMA_ID): CultNetMessage {
  return { schemaVersion: "cultnet.document_delete.v0", messageId: `delete-${recordKey}`, schemaId, recordKey };
}

class Recording implements CultMeshRemoteWriteAuthority {
  readonly requests: CultMeshRemoteWriteRequest[] = [];
  constructor(private readonly answer: (request: CultMeshRemoteWriteRequest) => unknown) {}
  decide(request: CultMeshRemoteWriteRequest): CultMeshRemoteWriteDecision | Promise<CultMeshRemoteWriteDecision> {
    this.requests.push(request);
    return this.answer(request) as CultMeshRemoteWriteDecision | Promise<CultMeshRemoteWriteDecision>;
  }
}

const grant = () => ({ granted: true });

interface Rig {
  puts: CultMeshRudpDocumentPut[];
  deletes: CultMeshRudpDocumentDelete[];
  errors: Error[];
  replies: CultNetMessage[];
  peer: CultNetPeer;
  settle(): Promise<unknown>;
  close(): void;
}

let nextConnection = 0x10204000;

async function rig(writeAuthority?: CultMeshRemoteWriteAuthority, extra: { maxPayloadBytes?: number } = {}): Promise<Rig> {
  const connectionId = nextConnection++;
  const puts: CultMeshRudpDocumentPut[] = [];
  const deletes: CultMeshRudpDocumentDelete[] = [];
  const errors: Error[] = [];
  const server = CultMesh.createRudpDocumentServer("authority-server", connectionId, {
    documents: new CultNetDocumentRegistry([defineCultNetDocumentBinding({ definition: note })]),
    writeAuthority,
    bindHost: "127.0.0.1",
    bindPort: 0,
    resendDelayMs: 25,
    resendPollMs: 5,
    onDocumentPutRaw: (document) => { puts.push(document); },
    onDocumentDeleteRaw: (request) => { deletes.push(request); },
    onError: (error) => errors.push(error),
    ...extra,
  });
  await server.start();
  const peer = await CultMesh.createRudpPeer("authority-peer", connectionId, `rudp://127.0.0.1:${server.bind.port}`, {
    resendDelayMs: 25,
    resendPollMs: 5,
    connectTimeoutMs: 2_000,
  });
  const replies: CultNetMessage[] = [];
  peer.on("message", (message) => replies.push(message));
  return {
    puts, deletes, errors, replies, peer,
    // A refusal is a reply; a grant sends none, so settle on a quiet period after the last send.
    settle: () => sleep(250),
    close: () => { peer.close(); server.close(); },
  };
}

async function using(r: Rig, body: (r: Rig) => Promise<unknown>): Promise<void> {
  try { await body(r); } finally { r.close(); }
}

function assertRefused(reply: CultNetMessage | undefined, key: string, error?: string): void {
  assert.ok(reply && reply.schemaVersion === "cultnet.error.v0");
  assert.equal(reply.code, "write_not_authorized");
  assert.ok(reply.error.startsWith("write_not_authorized"));
  assert.deepEqual(reply.details, { field: "recordKey", value: key, asOf: null, current: null });
  if (error !== undefined) assert.equal(reply.error, error);
}

test("with no authority a peer's put and delete are refused and the handlers never run", async () => {
  await using(await rig(undefined), async (r) => {
    r.peer.send(put(KEY_A));
    r.peer.send(del(KEY_A));
    r.peer.send(put(KEY_B));
    await r.settle();
    assert.equal(r.replies.length, 3);
    assertRefused(r.replies[0], KEY_A);
    assertRefused(r.replies[1], KEY_A);
    assertRefused(r.replies[2], KEY_B);
    assert.deepEqual([r.puts.length, r.deletes.length], [0, 0]);
    assert.deepEqual(r.errors, []);
  });
});

test("a granted put and delete reach their handlers, send nothing back, and the authority saw this peer", async () => {
  const authority = new Recording(grant);
  await using(await rig(authority), async (r) => {
    r.peer.send(put(KEY_A));
    r.peer.send(del(KEY_A));
    await r.settle();
    assert.deepEqual(r.replies, []);
    assert.deepEqual(r.puts.map((p) => p.recordKey), [KEY_A]);
    assert.deepEqual(r.deletes.map((d) => d.recordKey), [KEY_A]);
    assert.equal(authority.requests.length, 2);
    for (const request of authority.requests) {
      assert.equal(request.peer.address, "127.0.0.1");
      assert.ok(request.peer.port > 0);
    }
    assert.deepEqual(authority.requests[1]!.peer, r.deletes[0]!.remote, "the delete handler sees the peer the authority was asked about");
  });
});

test("the request carries the resolved write, and scoped grants refuse their neighbours", async () => {
  const authority = new Recording((request) =>
    request.operation === "put" && request.recordKey === KEY_A ? { granted: true } : { granted: false, reason: "scoped" });
  await using(await rig(authority), async (r) => {
    r.peer.send(put(KEY_A, { shardId: "authority-shard", shardEpoch: 1 }, ALIAS));
    await r.settle();
    const request = authority.requests[0]!;
    assert.equal(request.schemaId, SCHEMA_ID, "the authority sees the canonical id, not the alias as sent");
    assert.equal(request.operation, "put");
    assert.equal(request.recordKey, KEY_A);
    assert.equal(request.shardId, "authority-shard");
    assert.equal(request.shardEpoch, 1);
    assert.deepEqual(r.puts.map((p) => p.recordKey), [KEY_A]);

    r.peer.send(put(KEY_B));
    r.peer.send(del(KEY_A));
    await r.settle();
    assertRefused(r.replies[0], KEY_B, "write_not_authorized: scoped");
    assertRefused(r.replies[1], KEY_A, "write_not_authorized: scoped");
    assert.deepEqual(r.puts.map((p) => p.recordKey), [KEY_A], "a grant scoped to key a refuses key b");
    assert.deepEqual(r.deletes, [], "a grant scoped to put refuses delete");
    assert.equal(authority.requests.at(-1)!.operation, "delete");
    assert.equal(authority.requests.at(-1)!.schemaId, SCHEMA_ID);
    assert.equal(authority.requests.at(-1)!.shardId, null);
  });
});

test("a delete sent under a schema alias asks the authority with the canonical id", async () => {
  const authority = new Recording(grant);
  await using(await rig(authority), async (r) => {
    r.peer.send(del(KEY_A, ALIAS));
    await r.settle();
    assert.equal(authority.requests[0]!.schemaId, SCHEMA_ID);
    assert.equal(r.deletes.length, 1);
  });
});

test("refusal precedes every other judgment of the put", async () => {
  await using(await rig(undefined, { maxPayloadBytes: 256 }), async (r) => {
    r.peer.send(put("unrelated:authority", {}, "sha256:nobody-owns-this"));
    r.peer.send(put(KEY_A, {}, SCHEMA_ID, new Uint8Array(20_000)));
    await r.settle();
    assertRefused(r.replies[0], "unrelated:authority");
    assertRefused(r.replies[1], KEY_A);
    assert.deepEqual(r.errors, [], "the gate ran before the put was sized or resolved");
    assert.equal(r.puts.length, 0);
  });
  const granting = new Recording(grant);
  await using(await rig(granting, { maxPayloadBytes: 256 }), async (r) => {
    r.peer.send(put(KEY_A, {}, SCHEMA_ID, new Uint8Array(20_000)));
    await r.settle();
    assert.equal(r.replies[0]?.schemaVersion, "cultnet.error.v0");
    assert.notEqual((r.replies[0] as { code?: string }).code, "write_not_authorized", "a granted put still meets the size judgment");
    assert.equal(r.errors.length, 1);
  });
});

test("an authority that throws or rejects refuses, fails closed, and is reported without being quoted", async () => {
  for (const decide of [
    () => { throw new Error("authority exploded"); },
    () => Promise.reject(new Error("authority exploded")),
  ]) {
    await using(await rig(new Recording(decide)), async (r) => {
      r.peer.send(put(KEY_A));
      r.peer.send(del(KEY_A));
      await r.settle();
      assertRefused(r.replies[0], KEY_A);
      assertRefused(r.replies[1], KEY_A);
      assert.deepEqual([r.puts.length, r.deletes.length], [0, 0]);
      assert.equal(r.errors.length, 2);
      assert.ok(r.errors.every((e) => e.message.includes("Error") && !e.message.includes("exploded")));
    });
  }
});

test("a refusal with no reason still says why, and a missing decision refuses", async () => {
  await using(await rig(new Recording(() => ({ granted: false }))), async (r) => {
    r.peer.send(put(KEY_A));
    await r.settle();
    assertRefused(r.replies[0], KEY_A, "write_not_authorized");
  });
  for (const answer of [null, undefined, {}, { granted: "yes" }, { granted: 1 }]) {
    await using(await rig(new Recording(() => answer)), async (r) => {
      r.peer.send(put(KEY_A));
      r.peer.send(del(KEY_A));
      await r.settle();
      assertRefused(r.replies[0], KEY_A);
      assertRefused(r.replies[1], KEY_A);
      assert.deepEqual([r.puts.length, r.deletes.length], [0, 0]);
    });
  }
});

test("the refusal on the wire is the pinned error-vectors bytes", async () => {
  const vectors = JSON.parse(readFileSync(join(__dirname, "../../../../contracts/cultnet/interop/error-vectors.json"), "utf8")).vectors;
  const vector = vectors.find((v: { name: string }) => v.name === "write_not_authorized");
  const expected = Buffer.from(vector.messagePackHex, "hex");
  assert.deepEqual(Buffer.from(encodeWireValue(cultNetWriteNotAuthorizedError("some:key", "not granted"))), expected);
  await using(await rig(new Recording(() => ({ granted: false, reason: "not granted" }))), async (r) => {
    r.peer.send(put("some:key"));
    await r.settle();
    assert.deepEqual(Buffer.from(encodeWireValue(r.replies[0])), expected);
  });
});

test("wrong-typed shard fields on a put are refused before the authority and reach no handler", async () => {
  const authority = new Recording(grant);
  await using(await rig(authority), async (r) => {
    const bad: Record<string, unknown>[] = [
      { shardId: { not: "a string" } }, { shardId: 5 }, { shardEpoch: "7" }, { shardEpoch: true }, { shardEpoch: 1.5 },
    ];
    for (const fields of bad) r.peer.send(put(KEY_A, fields));
    await r.settle();
    assert.equal(r.replies.length, bad.length);
    assert.ok(r.replies.every((reply) => reply.schemaVersion === "cultnet.error.v0"));
    assert.deepEqual(authority.requests, [], "the authority was never asked");
    assert.equal(r.puts.length, 0);
  });
});

test("well-typed shard fields reach the authority as sent", async () => {
  const authority = new Recording(grant);
  await using(await rig(authority), async (r) => {
    r.peer.send(put(KEY_A, { shardId: "authority-shard", shardEpoch: 1 }));
    await r.settle();
    assert.deepEqual([authority.requests[0]!.shardId, authority.requests[0]!.shardEpoch], ["authority-shard", 1]);
  });
});

test("grant-all is an explicit choice that grants every write", async () => {
  await using(await rig(new CultMeshGrantAllWrites()), async (r) => {
    r.peer.send(put(KEY_A));
    r.peer.send(del(KEY_A));
    await r.settle();
    assert.deepEqual([r.puts.length, r.deletes.length, r.replies.length], [1, 1, 0]);
  });
});

test("a granted delete with no delete handler is answered with a text refusal", async () => {
  const connectionId = nextConnection++;
  const server = CultMesh.createRudpDocumentServer("no-delete", connectionId, {
    documents: new CultNetDocumentRegistry([defineCultNetDocumentBinding({ definition: note })]),
    writeAuthority: new CultMeshGrantAllWrites(),
    bindHost: "127.0.0.1", bindPort: 0, resendDelayMs: 25, resendPollMs: 5,
  });
  await server.start();
  const peer = await CultMesh.createRudpPeer("p", connectionId, `rudp://127.0.0.1:${server.bind.port}`, { resendDelayMs: 25, resendPollMs: 5, connectTimeoutMs: 2_000 });
  const replies: CultNetMessage[] = [];
  peer.on("message", (m) => replies.push(m));
  try {
    peer.send(del(KEY_A));
    await sleep(250);
    assert.equal(replies.length, 1);
    assert.notEqual((replies[0] as { code?: string }).code, "write_not_authorized");
  } finally { peer.close(); server.close(); }
});
