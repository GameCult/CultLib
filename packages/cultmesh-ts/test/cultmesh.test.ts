import { mkdtemp } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import assert from "node:assert/strict";
import { z } from "zod";
import { defineDocumentType } from "cultcache-ts";
import { CultMesh } from "../src/index";

const noteDocument = defineDocumentType({
  type: "cultmesh.note",
  schemaId: "cultmesh.note.v0",
  schema: z.object({
    noteId: z.string(),
    body: z.string(),
  }),
  name: "noteId",
});

test("CultMesh TS opens a durable local node and registers document bindings", async () => {
  const filePath = join(await mkdtemp(join(tmpdir(), "cultmesh-ts-")), "node.ccmp");
  const first = await CultMesh.startNode(filePath, {
    documents: [noteDocument],
  });

  await first.put(noteDocument, "note:intro", {
    noteId: "note:intro",
    body: "hello from local CultMesh TS",
  });
  await first.flush();

  const reopened = await CultMesh.startNode(filePath, {
    documents: [noteDocument],
  });

  assert.equal(
    reopened.getRequired(noteDocument, "note:intro").body,
    "hello from local CultMesh TS",
  );
  assert.ok(reopened.documents.get("cultmesh.note"));
  assert.ok(reopened.documents.getBySchemaId("cultmesh.note.v0"));
});

test("CultMesh TS local authority leases do not trust peer cards by contact alone", () => {
  const peers = CultMesh.createPeerCatalog();
  const leases = CultMesh.createAuthorityLeaseCatalog();
  const peerUpdates: string[] = [];
  const unsubscribePeer = peers.watch((peerCard) => {
    peerUpdates.push(peerCard.peerId);
  });
  const peer = {
    peerId: "voidbot-local",
    verseId: "local",
    endpoints: ["cultmesh://localhost"],
    roles: ["shard-primary"],
    authorityLeaseId: "lease:voidbot-local",
  };

  peers.upsert(peer);
  unsubscribePeer();
  peers.upsert({
    peerId: "unused-peer",
    verseId: "local",
    endpoints: ["cultmesh://unused"],
  });

  assert.deepEqual(peerUpdates, ["voidbot-local"]);
  assert.equal(peers.get("voidbot-local"), peer);
  assert.deepEqual(peers.find("local", "shard-primary"), [peer]);
  assert.equal(leases.isAuthorized(peer, "shard-primary"), false);

  const leaseUpdates: string[] = [];
  const unsubscribeLease = leases.watch((lease) => {
    leaseUpdates.push(lease.leaseId);
  });
  const lease = {
    leaseId: "lease:voidbot-local",
    verseId: "local",
    peerId: "voidbot-local",
    roles: ["shard-primary"],
    validFrom: new Date(Date.now() - 1000),
    expiresAt: new Date(Date.now() + 1000),
  };
  leases.upsert(lease);
  unsubscribeLease();
  leases.upsert({ ...lease, roles: ["shard-primary", "schema"] });

  assert.deepEqual(leaseUpdates, ["lease:voidbot-local"]);
  assert.equal(leases.get("lease:voidbot-local")?.peerId, "voidbot-local");
  assert.deepEqual(leases.leases.map((lease) => lease.leaseId), [
    "lease:voidbot-local",
  ]);
  assert.equal(leases.isAuthorized(peer, "shard-primary"), true);
});

test("CultMesh TS local Verse catalog exposes sorted views and watch ergonomics", () => {
  const verses = CultMesh.createVerseCatalog<{ verseId: string; label: string }>();
  const updates: string[] = [];
  const unsubscribe = verses.watch((verse) => {
    updates.push(verse.verseId);
  });

  const publicVerse = { verseId: "public", label: "Public Verse" };
  const privateVerse = { verseId: "private", label: "Private Verse" };
  verses.upsert(publicVerse.verseId, publicVerse);
  verses.upsert(privateVerse.verseId, privateVerse);
  unsubscribe();
  verses.upsert("scratch", { verseId: "scratch", label: "Scratch" });

  assert.deepEqual(updates, ["public", "private"]);
  assert.equal(verses.get("public"), publicVerse);
  assert.deepEqual(
    verses.verses.map((verse) => verse.verseId),
    ["private", "public", "scratch"],
  );
});

test("CultMesh TS negotiates streaming frame body transports explicitly", () => {
  const streams = CultMesh.createStreamCatalog();
  const streamUpdates: string[] = [];
  const frameUpdates: bigint[] = [];
  const unsubscribeStream = streams.watch((stream) => {
    streamUpdates.push(stream.streamId);
  });
  const unsubscribeFrame = streams.watchFrames((frame) => {
    frameUpdates.push(frame.sequence);
  });
  const stream = {
    streamId: "mimir:kiyo-pro",
    verseId: "studio",
    ownerPeerId: "starfire",
    kind: "video",
    label: "Kiyo Pro",
    clock: {
      clockDomainId: "starfire-qpc",
      confidence: 0.25,
      evidenceKind: "provisional-clock-domain-edge-fit",
    },
    video: {
      width: 1920,
      height: 1080,
      pixelFormat: "YUY2",
      framesPerSecond: 30,
    },
    preferredTransports: [
      "shared-d3d12-texture",
      "shared-memory",
      "cultcache-page",
    ],
    maxInFlightFrames: 3,
  } as const;
  streams.declare(stream);

  const negotiation = streams.negotiate("mimir:kiyo-pro", {
    peerId: "fensalir",
    verseId: "studio",
    supportedTransports: ["shared-d3d12-texture", "cultcache-page"],
    acceptedKinds: ["video"],
    canImportGpuHandles: true,
    maxInFlightFrames: 2,
  });

  assert.deepEqual(negotiation, {
    streamId: "mimir:kiyo-pro",
    producerPeerId: "starfire",
    consumerPeerId: "fensalir",
    transport: "shared-d3d12-texture",
    access: "read",
    maxInFlightFrames: 2,
    copyBudget: "zero-copy-target",
  });
  assert.deepEqual(streamUpdates, ["mimir:kiyo-pro"]);

  const frame = {
    streamId: "mimir:kiyo-pro",
    sequence: 42n,
    timestampNs: 1_000_000_000n,
    durationNs: 33_333_334n,
    transport: "shared-d3d12-texture",
    nativeHandle: "0xfeed",
    fenceHandle: "0xbeef",
    fenceValue: 7n,
    unavoidableCopyCount: 0,
  } as const;
  streams.publishFrame(frame);

  assert.deepEqual(frameUpdates, [42n]);
  assert.equal(streams.latestFrame("mimir:kiyo-pro")?.sequence, 42n);
  unsubscribeStream();
  unsubscribeFrame();
  streams.declare({ ...stream, streamId: "mimir:kiyo-pro-alt" });
  streams.publishFrame({ ...frame, sequence: 43n });
  assert.deepEqual(streamUpdates, ["mimir:kiyo-pro"]);
  assert.deepEqual(frameUpdates, [42n]);
});
