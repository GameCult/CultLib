import assert from "node:assert/strict";
import { mkdtemp } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { z } from "zod";
import { codecs, defineDocumentType } from "@gamecult/cultcache-ts";
import {
  CultNetDocumentRegistry,
  defineCultNetDocumentBinding,
  type CultNetPeer,
} from "cultnet-ts";
import { CultMesh, cultMeshDocumentFromSingleFile } from "../src/index";

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
