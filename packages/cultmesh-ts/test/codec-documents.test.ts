import assert from "node:assert/strict";
import { mkdtemp } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { z } from "zod";
import {
  CultCachePayloadDepthError,
  MAX_PAYLOAD_DEPTH,
  SingleFileMessagePackBackingStore,
  codecs,
  decodeDocumentPayload,
  defineDocumentType,
  documentPayloadBytes,
  documentPayloadFromBytes,
  encodeDocumentPayload,
} from "@gamecult/cultcache-ts";
import type { CacheBackingStore, CultCacheEnvelope } from "@gamecult/cultcache-ts";
import {
  CultNetDocumentRegistry,
  defineCultNetDocumentBinding,
  type CultNetPeer,
} from "cultnet-ts";
import { CultMesh, cultMeshDocumentFromSingleFile, cultMeshDocumentFromStore, type CultMeshRudpDocumentPut } from "../src/index";

// A document written in the reference's positional shape, and a plain map document beside it. Every mesh reader of a
// payload reads it the way the cache does, so both read through the mesh.
const codecNote = defineDocumentType({
  type: "cultmesh.codec_note",
  schemaId: "cultmesh.codec_note.v0",
  schema: z.object({ noteId: z.string(), body: z.string() }),
  name: "noteId",
  codec: codecs.slots<{ noteId: string; body: string }>({
    0: ["noteId", codecs.string],
    1: ["body", codecs.string],
  }, "Tests.CodecNote"),
});

const codecReceipt = defineDocumentType({
  type: "cultmesh.codec_receipt",
  schemaId: "cultmesh.codec_receipt.v0",
  schema: z.object({ commandId: z.string(), body: z.string() }),
  name: "commandId",
  codec: codecs.slots<{ commandId: string; body: string }>({
    0: ["commandId", codecs.string],
    1: ["body", codecs.string],
  }, "Tests.CodecReceipt"),
});

// The same record under another schema id: the store holds it as codec_note, so it is found by parsing, not by id.
const codecNoteAlias = defineDocumentType({
  type: "cultmesh.codec_note_alias",
  schemaId: "cultmesh.codec_note_alias.v0",
  schema: z.object({ noteId: z.string(), body: z.string() }),
  name: "noteId",
  codec: codecNote.codec,
});

const plainNote = defineDocumentType({
  type: "cultmesh.plain_note",
  schemaId: "cultmesh.plain_note.v0",
  schema: z.object({ noteId: z.string(), body: z.string() }),
  name: "noteId",
});

test("the mesh reads a stored codec document as the cache does, and a plain document unchanged", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultmesh-ts-codec-store-"));
  const codecPath = join(dir, "codec.ccmp");
  const plainPath = join(dir, "plain.ccmp");
  const codecNode = await CultMesh.startNode(codecPath, { documents: [codecNote] });
  await codecNode.put(codecNote, "n1", { noteId: "n1", body: "positional" });
  const plainNode = await CultMesh.startNode(plainPath, { documents: [plainNote] });
  await plainNode.put(plainNote, "n2", { noteId: "n2", body: "map" });

  assert.deepEqual(await cultMeshDocumentFromSingleFile(codecPath, codecNote).latest(), { noteId: "n1", body: "positional" });
  assert.deepEqual(await cultMeshDocumentFromSingleFile(codecPath, codecNoteAlias).latest(), { noteId: "n1", body: "positional" });
  // A store holding a plain record and a codec record, the alias found by parsing takes the codec one whichever the
  // single-file store (which sorts records by key) puts first: the probe has to refuse the plain record, not meet it
  // second.
  for (const [plainKey, codecKey] of [["a-plain", "z-codec"], ["z-plain", "a-codec"]] as const) {
    const bothPath = join(dir, `both-${plainKey}.ccmp`);
    const bothNode = await CultMesh.startNode(bothPath, { documents: [plainNote, codecNote] });
    await bothNode.put(plainNote, plainKey, { noteId: plainKey, body: "map" });
    await bothNode.put(codecNote, codecKey, { noteId: codecKey, body: "positional" });
    assert.deepEqual(await cultMeshDocumentFromSingleFile(bothPath, codecNoteAlias).latest(), { noteId: codecKey, body: "positional" });
  }
  assert.deepEqual(await cultMeshDocumentFromSingleFile(plainPath, plainNote).latest(), { noteId: "n2", body: "map" });
});

test("the mesh syncs a codec document from a peer snapshot and returns a codec receipt", async () => {
  const connectionId = 0x10203060;
  const dir = await mkdtemp(join(tmpdir(), "cultmesh-ts-codec-sync-"));
  const source = await CultMesh.startNode(join(dir, "source.ccmp"), { documents: [codecNote, plainNote] });
  await source.put(codecNote, "n1", { noteId: "n1", body: "synced positional" });
  // Two records at one key, in two shapes: a peer snapshot handle for the alias schema (which neither holds) accepts the
  // one that parses as the codec.
  await source.put(plainNote, "n3", { noteId: "n3", body: "map" });
  await source.put(codecNote, "n3", { noteId: "n3", body: "positional" });
  const target = await CultMesh.startNode(join(dir, "target.ccmp"), { documents: [codecNote] });
  const commandBinding = defineCultNetDocumentBinding({ definition: codecNote });
  const plainBinding = defineCultNetDocumentBinding({ definition: plainNote });
  const receiptBinding = defineCultNetDocumentBinding({ definition: codecReceipt });
  const puts: unknown[] = [];
  const server = CultMesh.createRudpDocumentServer("cultmesh-ts-codec-server", connectionId, {
    documents: new CultNetDocumentRegistry([commandBinding, plainBinding, receiptBinding]),
    getCache: () => source.cache,
    bindHost: "127.0.0.1", bindPort: 0, resendDelayMs: 25, resendPollMs: 5,
    onDocumentPutRaw: async (document) => (puts.push(document.payload), {
      binding: receiptBinding,
      recordKey: document.recordKey,
      value: { commandId: document.recordKey, body: "applied" },
    }),
  });
  let peer: CultNetPeer | undefined;
  try {
    await server.start();
    peer = await CultMesh.createRudpPeer("cultmesh-ts-codec-client", connectionId, `rudp://127.0.0.1:${server.bind.port}`, {
      resendDelayMs: 25, resendPollMs: 5, connectTimeoutMs: 1_000,
    });
    // The accepts probe reads the snapshot's payload as the codec: a plain decode would reject every codec snapshot.
    const synced = await target.syncDocumentFromPeerSnapshot(peer, codecNote, "n1", { timeoutMs: 1_000 });
    assert.deepEqual(synced, { noteId: "n1", body: "synced positional" });
    assert.deepEqual(target.getRequired(codecNote, "n1"), { noteId: "n1", body: "synced positional" });
    // A peer snapshot handle reads the same way, and accepts the snapshot by parsing it as the codec.
    assert.deepEqual(await CultMesh.documentFromPeerSnapshot(peer, codecNote, "n1", { timeoutMs: 1_000 }).latest(), { noteId: "n1", body: "synced positional" });

    assert.deepEqual(
      await CultMesh.documentFromPeerSnapshot(peer, codecNoteAlias, "n3", { timeoutMs: 1_000 }).latest(),
      { noteId: "n3", body: "positional" },
    );

    const receipt = await CultMesh.publishRudpDocumentAndWaitForReceipt(
      "cultmesh-ts-codec-receipt", connectionId, `rudp://127.0.0.1:${server.bind.port}`,
      commandBinding, "n2", { noteId: "n2", body: "command" }, receiptBinding,
      { messageId: "n2", receiptTimeoutMs: 1_000, resendPollMs: 5 },
    );
    assert.deepEqual(receipt, { commandId: "n2", body: "applied" });
    // The handler is handed the document, not its wire shape: the server reads a put through the registered definition.
    await CultMesh.publishRudpDocumentAndWaitForReceipt(
      "cultmesh-ts-plain-receipt", connectionId, `rudp://127.0.0.1:${server.bind.port}`,
      plainBinding, "n4", { noteId: "n4", body: "plain command" }, receiptBinding,
      { messageId: "n4", receiptTimeoutMs: 1_000, resendPollMs: 5 },
    );
    assert.deepEqual(puts, [{ noteId: "n2", body: "command" }, { noteId: "n4", body: "plain command" }]);
  } finally {
    peer?.close();
    server.close();
  }
});

test("a RUDP put naming a schema the server has no definition for reaches the handler as the untouched opaque payload", async () => {
  const connectionId = 0x10203070;
  const stray = defineDocumentType({
    type: "cultmesh.stray_note",
    schemaId: "cultmesh.stray_note.v0",
    schema: z.object({ noteId: z.string() }),
    name: "noteId",
    codec: codecs.slots<{ noteId: string }>({ 0: ["noteId", codecs.string] }, "Tests.StrayNote"),
  });
  const strayBinding = defineCultNetDocumentBinding({ definition: stray });
  const known = defineCultNetDocumentBinding({ definition: plainNote });
  const errors: Error[] = [];
  const puts: CultMeshRudpDocumentPut[] = [];
  const server = CultMesh.createRudpDocumentServer("cultmesh-ts-unregistered-server", connectionId, {
    documents: new CultNetDocumentRegistry([known]),
    bindHost: "127.0.0.1", bindPort: 0, resendDelayMs: 25, resendPollMs: 5,
    onError: (error) => errors.push(error),
    onDocumentPutRaw: async (document) => { puts.push(document); },
  });
  let peer: CultNetPeer | undefined;
  try {
    await server.start();
    peer = await CultMesh.createRudpPeer("cultmesh-ts-unregistered-client", connectionId, `rudp://127.0.0.1:${server.bind.port}`, {
      resendDelayMs: 25, resendPollMs: 5, connectTimeoutMs: 1_000,
    });
    peer.send(new CultNetDocumentRegistry([strayBinding]).createRawDocumentPutMessage(strayBinding, "unregistered-1", "k", { noteId: "k" }));
    const startedAt = Date.now();
    while (puts.length === 0 && Date.now() - startedAt < 2_000) await new Promise((resolve) => setTimeout(resolve, 5));
    assert.deepEqual(errors, []);
    assert.equal(puts.length, 1);
    // Not read as plain msgpack: the bytes are the codec's slot record, handed over opaque, and the definition that reads
    // them is the handler's to hold.
    const put = puts[0]!;
    assert.ok(!put.decoded, "the registry holds no definition for this schema");
    const payload = put.payload;
    assert.equal(Buffer.from(documentPayloadBytes(payload)).toString("hex"), "91a16b", "a one-slot codec record, not a map");
    assert.deepEqual(Buffer.from(documentPayloadBytes(payload)), Buffer.from(documentPayloadBytes(encodeDocumentPayload(stray, { noteId: "k" }))));
    assert.deepEqual(decodeDocumentPayload(stray, payload), { noteId: "k" });
  } finally {
    peer?.close();
    server.close();
  }
});

test("a store read takes the record the catalog matches to the schema and the definition parses, not the first that merely parses", async () => {
  const target = defineDocumentType({ type: "cultmesh.pick", schemaId: "cultmesh.pick.v1", schema: z.object({ n: z.number() }) });
  const lenient = defineDocumentType({ type: "cultmesh.pick", schemaId: "cultmesh.pick.v1", schema: { parse: (value: unknown) => value } });
  const entry = (schemaId: string, compatibleSchemaIds?: readonly string[]) => ({
    schemaId, schemaName: schemaId, schemaVersion: `${schemaId}.v1`, contentHash: "h", canonicalSchemaJson: "{}", compatibleSchemaIds,
  });
  const envelope = (key: string, schemaId: string, value: unknown, compatibleSchemaIds?: readonly string[]): CultCacheEnvelope => ({
    key, type: "cultmesh.pick", schemaId, storedAt: "2026-10-11T00:00:00.000Z",
    payload: encodeDocumentPayload(lenient, value), catalogEntry: entry(schemaId, compatibleSchemaIds),
  });
  // None carries the definition's own schema id, so the exact-id find misses. The first parses and is foreign; the
  // second is matched by the catalog and does not parse; the third is matched and parses.
  const records = [
    envelope("a", "foreign.v1", { n: 1 }),
    envelope("b", "alias-bad.v1", { n: "x" }, ["cultmesh.pick.v1"]),
    envelope("c", "alias-good.v1", { n: 2 }, ["cultmesh.pick.v1"]),
  ];
  const store: CacheBackingStore = { pullAll: async () => records, push: async () => undefined, delete: async () => undefined };
  assert.deepEqual(await cultMeshDocumentFromStore(store, target).latest(), { n: 2 });
});

// A payload nested past the limit: MAX_PAYLOAD_DEPTH + 1 one-element arrays around a nil.
const pastLimitPayload = () => documentPayloadFromBytes(Uint8Array.from([...new Array(MAX_PAYLOAD_DEPTH + 1).fill(0x91), 0xc0]));

test("a payload past the nesting limit is refused at the network doors: a RUDP put and a single-file read", async () => {
  const connectionId = 0x10203080;
  const plainBinding = defineCultNetDocumentBinding({ definition: plainNote });
  const registry = new CultNetDocumentRegistry([plainBinding]);
  const errors: unknown[] = [];
  const puts: CultMeshRudpDocumentPut[] = [];
  const server = CultMesh.createRudpDocumentServer("cultmesh-ts-depth-server", connectionId, {
    documents: registry,
    bindHost: "127.0.0.1", bindPort: 0, resendDelayMs: 25, resendPollMs: 5,
    onError: (error) => errors.push(error),
    onDocumentPutRaw: async (document) => { puts.push(document); },
  });
  let peer: CultNetPeer | undefined;
  try {
    await server.start();
    peer = await CultMesh.createRudpPeer("cultmesh-ts-depth-client", connectionId, `rudp://127.0.0.1:${server.bind.port}`, {
      resendDelayMs: 25, resendPollMs: 5, connectTimeoutMs: 1_000,
    });
    peer.send(registry.createRawDocumentPutMessageFromEnvelope("deep-1", {
      key: "k", type: plainNote.type, schemaId: plainNote.schemaId, payload: pastLimitPayload(), storedAt: new Date().toISOString(),
    }));
    const startedAt = Date.now();
    while (errors.length === 0 && Date.now() - startedAt < 2_000) await new Promise((resolve) => setTimeout(resolve, 5));
    assert.equal(errors.length, 1);
    assert.ok(errors[0] instanceof CultCachePayloadDepthError, "the put is refused by the payload owner");
    assert.deepEqual(puts, [], "the handler never saw the put");

    // The server is still serving: an ordinary put goes through.
    peer.send(registry.createRawDocumentPutMessage(plainBinding, "ok-1", "k2", { noteId: "k2", body: "fine" }));
    while (puts.length === 0 && Date.now() - startedAt < 4_000) await new Promise((resolve) => setTimeout(resolve, 5));
    assert.equal(puts.length, 1);
  } finally {
    peer?.close();
    server.close();
  }

  const dir = await mkdtemp(join(tmpdir(), "cultmesh-ts-depth-store-"));
  const path = join(dir, "deep.ccmp");
  await new SingleFileMessagePackBackingStore(path).push({
    key: "k", type: plainNote.type, schemaId: plainNote.schemaId, payload: pastLimitPayload(), storedAt: new Date().toISOString(),
  });
  await assert.rejects(() => cultMeshDocumentFromSingleFile(path, plainNote).latest(), CultCachePayloadDepthError);
});
