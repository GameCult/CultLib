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
  CultNetPeer,
  cultNetWriteNotAuthorizedError,
  defineCultNetDocumentBinding,
  type CultNetMessage,
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

async function rig(writeAuthority?: CultMeshRemoteWriteAuthority, extra: { maxPayloadBytes?: number; writeAuthorityTimeoutMs?: number } = {}): Promise<Rig> {
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
  const peer: CultNetPeer = await CultMesh.createRudpPeer("authority-peer", connectionId, `rudp://127.0.0.1:${server.bind.port}`, {
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
      assert.equal(request.schemaId, SCHEMA_ID, "a granted write was judged under the canonical schema id");
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

test("an authority that never settles refuses after the timeout and does not block the frames behind it", async () => {
  // The first put hangs forever; later frames from the same session must still be judged.
  const authority = new Recording((request) => request.recordKey === KEY_A ? new Promise(() => {}) : { granted: true });
  await using(await rig(authority, { writeAuthorityTimeoutMs: 100 }), async (r) => {
    r.peer.send(put(KEY_A));
    r.peer.send(put(KEY_B));
    await sleep(600);
    assert.deepEqual(r.puts.map((p) => p.recordKey), [KEY_B], "the granted put behind the hung one was applied");
    assert.equal(r.replies.length, 1);
    assertRefused(r.replies[0], KEY_A);
    assert.equal((r.replies[0] as { error: string }).error.includes("timed out"), true);
    assert.equal(r.errors.length, 1);
    assert.ok(r.errors[0]!.message.includes("did not answer within 100 ms"));
  });
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

test("wrong-typed shard fields on a put are judged with null shard fields, then refused as malformed once granted", async () => {
  const authority = new Recording(grant);
  await using(await rig(authority), async (r) => {
    const bad: Record<string, unknown>[] = [
      { shardId: { not: "a string" } }, { shardId: 5 }, { shardEpoch: "7" }, { shardEpoch: true }, { shardEpoch: 1.5 },
    ];
    for (const fields of bad) r.peer.send(put(KEY_A, fields));
    await r.settle();
    assert.equal(r.replies.length, bad.length);
    assert.ok(r.replies.every((reply) => reply.schemaVersion === "cultnet.error.v0" && reply.code === null && reply.error.startsWith("malformed_document_put:")));
    assert.equal(authority.requests.length, bad.length, "the authority is asked first");
    assert.ok(authority.requests.every((q) => q.shardId === null && q.shardEpoch === null), "with null shard fields");
    assert.equal(r.puts.length, 0);
  });
  await using(await rig(undefined), async (r) => {
    r.peer.send(put(KEY_A, { shardId: 5 }));
    await r.settle();
    assertRefused(r.replies[0], KEY_A);
  });
});

test("a refusal fits maxPayloadBytes however long the record key and reason are", async () => {
  const longKey = "k".repeat(3_000) + "é";
  await using(await rig(new Recording(() => ({ granted: false, reason: "r".repeat(100_000) })), { maxPayloadBytes: 1024 }), async (r) => {
    r.peer.send(put(longKey));
    r.peer.send(del(longKey));
    await r.settle();
    assert.equal(r.replies.length, 2);
    for (const reply of r.replies) {
      assert.equal((reply as { code?: string }).code, "write_not_authorized");
      assert.ok(encodeWireValue(reply).byteLength <= 1024);
      assert.ok(longKey.startsWith((reply as { details: { value: string } }).details.value));
    }
    assert.deepEqual(r.errors, []);
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

// Hostile frames, sent as raw bytes the typed peer would never build.
const bin = (n: number, ...tail: number[]) => [0xc6, (n >>> 24) & 255, (n >>> 16) & 255, (n >>> 8) & 255, n & 255, ...tail]; // bin32 header
const str = (t: string) => [0xa0 | t.length, ...Buffer.from(t)];
function rawPut(payloadBytes: number[], key = KEY_A): Uint8Array {
  // {schemaVersion, messageId, document: {schemaId, recordKey, storedAt, payloadEncoding, payload: <payloadBytes>}}
  return Uint8Array.from([
    0x83, ...str("schemaVersion"), ...str("cultnet.document_put_raw.v0"), ...str("messageId"), ...str("raw"),
    ...str("document"), 0x85, ...str("schemaId"), ...str(SCHEMA_ID), ...str("recordKey"), ...str(key),
    ...str("storedAt"), ...str("2026-11-07T00:00:00Z"), ...str("payloadEncoding"), ...str("messagepack"),
    ...str("payload"), ...payloadBytes,
  ]);
}

async function rawRig(writeAuthority?: CultMeshRemoteWriteAuthority) {
  const connectionId = nextConnection++;
  const puts: CultMeshRudpDocumentPut[] = [];
  const deletes: CultMeshRudpDocumentDelete[] = [];
  const errors: Error[] = [];
  const server = CultMesh.createRudpDocumentServer("authority-raw", connectionId, {
    documents: new CultNetDocumentRegistry([defineCultNetDocumentBinding({ definition: note })]),
    writeAuthority, bindHost: "127.0.0.1", bindPort: 0, resendDelayMs: 25, resendPollMs: 5,
    onDocumentPutRaw: (d) => { puts.push(d); }, onDocumentDeleteRaw: (d) => { deletes.push(d); }, onError: (e) => errors.push(e),
  });
  await server.start();
  const client = await CultMesh.createRudpClient("raw", connectionId, `rudp://127.0.0.1:${server.bind.port}`, { resendDelayMs: 25, resendPollMs: 5 });
  client.connect();
  const started = Date.now();
  while (!client.connected) {
    if (Date.now() - started > 2_000) throw new Error("not connected");
    await sleep(5);
  }
  const replies: CultNetMessage[] = [];
  new CultNetPeer(client, { wireContract: "cultnet.schema.v0" }).on("message", (m) => replies.push(m));
  return { puts, deletes, errors, replies, send: (b: Uint8Array) => client.send("schema", b), close: () => { client.close(); server.close(); } };
}

const float64 = (v: number) => { const b = Buffer.alloc(9); b[0] = 0xcb; b.writeDoubleBE(v, 1); return [...b]; };

// Each hostile frame, the operation it is judged as, and the ids the authority is told (empty when the frame does not carry them).
const hostile: { name: string; frame: Uint8Array; operation: "put" | "delete"; schemaId: string; recordKey: string }[] = [
  { name: "an int payload", frame: rawPut([0x2a]), operation: "put", schemaId: SCHEMA_ID, recordKey: KEY_A },
  { name: "a uint64-max integer payload", frame: rawPut([0xcf, 255, 255, 255, 255, 255, 255, 255, 255]), operation: "put", schemaId: SCHEMA_ID, recordKey: KEY_A },
  { name: "a float64 2**70 payload", frame: rawPut(float64(2 ** 70)), operation: "put", schemaId: SCHEMA_ID, recordKey: KEY_A },
  { name: "a bin whose claimed length exceeds the frame", frame: rawPut(bin(1_000_000_000, 1, 2, 3)), operation: "put", schemaId: "", recordKey: "" },
  { name: "a frame that does not decode at all", frame: Uint8Array.from([0xc1]), operation: "put", schemaId: "", recordKey: "" },
  { name: "a put whose document is not a map", frame: Uint8Array.from([0x82, ...str("schemaVersion"), ...str("cultnet.document_put_raw.v0"), ...str("document"), 0x05]), operation: "put", schemaId: "", recordKey: "" },
  { name: "a delete whose recordKey is not a string", frame: Uint8Array.from([0x84, ...str("schemaVersion"), ...str("cultnet.document_delete.v0"), ...str("messageId"), ...str("d"), ...str("schemaId"), ...str(SCHEMA_ID), ...str("recordKey"), 0x07]), operation: "delete", schemaId: SCHEMA_ID, recordKey: "" },
];

/** Records the size of every buffer a frame makes the process allocate, so a transient allocation is seen after it is freed. */
function watchAllocations(): { largest: () => number; stop: () => void } {
  let largest = 0;
  const note = (n: unknown) => { if (typeof n === "number" && n > largest) largest = n; };
  const originals = { alloc: Buffer.alloc, unsafe: Buffer.allocUnsafe, slow: Buffer.allocUnsafeSlow, u8: globalThis.Uint8Array, ab: globalThis.ArrayBuffer };
  Buffer.alloc = ((n: number, ...rest: never[]) => { note(n); return originals.alloc.call(Buffer, n, ...rest); }) as typeof Buffer.alloc;
  Buffer.allocUnsafe = ((n: number) => { note(n); return originals.unsafe.call(Buffer, n); }) as typeof Buffer.allocUnsafe;
  Buffer.allocUnsafeSlow = ((n: number) => { note(n); return originals.slow.call(Buffer, n); }) as typeof Buffer.allocUnsafeSlow;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const watched = (ctor: any): any => new Proxy(ctor, { construct: (target, args, newTarget) => { note(args[0]); return Reflect.construct(target, args, newTarget); } });
  (globalThis as { Uint8Array: unknown }).Uint8Array = watched(originals.u8);
  (globalThis as { ArrayBuffer: unknown }).ArrayBuffer = watched(originals.ab);
  return {
    largest: () => largest,
    stop: () => {
      Buffer.alloc = originals.alloc; Buffer.allocUnsafe = originals.unsafe; Buffer.allocUnsafeSlow = originals.slow;
      (globalThis as { Uint8Array: unknown }).Uint8Array = originals.u8;
      (globalThis as { ArrayBuffer: unknown }).ArrayBuffer = originals.ab;
    },
  };
}

test("an ungranted peer's unreadable write is refused with write_not_authorized naming what it carried, and nothing is allocated for it", async () => {
  for (const h of hostile) {
    const authority = new Recording(() => ({ granted: false }));
    for (const withAuthority of [false, true]) {
      const r = await rawRig(withAuthority ? authority : undefined);
      const watch = watchAllocations();
      try {
        r.send(h.frame);
        await sleep(250);
      } finally { watch.stop(); }
      try {
        assert.equal(r.replies.length, 1, h.name);
        assertRefused(r.replies[0], h.recordKey);
        assert.ok(watch.largest() < 10_000_000, `${h.name}: allocated ${watch.largest()} bytes`);
        assert.deepEqual([r.puts.length, r.deletes.length, r.errors.length], [0, 0, 0], h.name);
      } finally { r.close(); }
    }
    assert.equal(authority.requests.length, 1, h.name);
    assert.deepEqual(
      [authority.requests[0]!.operation, authority.requests[0]!.schemaId, authority.requests[0]!.recordKey],
      [h.operation, h.schemaId, h.recordKey], h.name);
  }
});

test("the allocation watch sees a transient allocation", () => {
  const watch = watchAllocations();
  try { void Buffer.alloc(20_000_000); } finally { watch.stop(); }
  assert.ok(watch.largest() >= 20_000_000);
  const typed = watchAllocations();
  try { void new Uint8Array(20_000_000); } finally { typed.stop(); }
  assert.ok(typed.largest() >= 20_000_000);
});

test("a granted peer's unreadable write reaches onError and gets a malformed refusal, and no handler runs", async () => {
  for (const h of hostile) {
    const authority = new Recording(grant);
    const r = await rawRig(authority);
    try {
      r.send(h.frame);
      await sleep(250);
      assert.equal(r.replies.length, 1, h.name);
      const reply = r.replies[0] as { code: unknown; error: string };
      assert.equal(reply.code, null, h.name);
      assert.ok(reply.error.startsWith(`malformed_document_${h.operation}:`), h.name);
      assert.equal(r.errors.length, 1, h.name);
      assert.deepEqual([r.puts.length, r.deletes.length], [0, 0], h.name);
      assert.deepEqual([authority.requests.length, authority.requests[0]!.operation, authority.requests[0]!.recordKey], [1, h.operation, h.recordKey], h.name);
    } finally { r.close(); }
  }
});
