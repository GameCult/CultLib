import { mkdtemp } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import assert from "node:assert/strict";
import { encode } from "@msgpack/msgpack";
import { z } from "zod";
import { defineDocumentType } from "cultcache-ts";
import { CultNetDocumentRegistry, CultNetPeer, cultNetBuiltinSchemaRegistry } from "cultnet-ts";
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

async function waitFor(predicate: () => boolean, description: string): Promise<void> {
  const startedAt = Date.now();
  while (!predicate()) {
    if (Date.now() - startedAt > 1_000) {
      throw new Error(`Timed out waiting for ${description}.`);
    }
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
}

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
  assert.deepEqual(
    peers.findAuthorized("local", "shard-primary", leases),
    [],
  );

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
  assert.deepEqual(
    peers.findAuthorized("local", "shard-primary", leases),
    [peer],
  );
  assert.equal(
    peers.firstAuthorized("local", "shard-primary", leases),
    peer,
  );
  assert.equal(peers.firstAuthorized("local", "schema", leases), undefined);
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

test("CultMesh TS branded facade exposes schema and shard catalogs", () => {
  const schemaCatalog = CultMesh.createSchemaCatalog();
  const schemaUpdates: string[] = [];
  const unsubscribeSchema = schemaCatalog.watch((descriptor) => {
    schemaUpdates.push(descriptor.schemaId);
  });
  schemaCatalog.upsert({
    schemaId: "cultmesh.ts.note.v1",
    kind: "document_payload",
    documentType: "cultmesh.ts-note",
    wireContracts: ["cultnet.schema.v0"],
    contentHash: "note-hash",
  });
  unsubscribeSchema();
  schemaCatalog.upsert({
    schemaId: "cultmesh.ts.other.v1",
    kind: "document_payload",
    documentType: "cultmesh.ts-other",
    wireContracts: ["cultnet.schema.v0"],
    contentHash: "other-hash",
  });

  assert.deepEqual(schemaUpdates, ["cultmesh.ts.note.v1"]);
  assert.equal(
    schemaCatalog.get("cultmesh.ts.note.v1")?.documentType,
    "cultmesh.ts-note",
  );

  const builtIns = CultMesh.createBuiltInSchemaCatalog();
  assert.equal(
    builtIns.get("cultnet.shard_catalog_request.v0")?.schemaVersion,
    "cultnet.shard_catalog_request.v0",
  );
  assert.equal(
    builtIns.get("cultnet.shard_catalog_response.v0")?.kind,
    "wire_message",
  );
  assert.equal(
    builtIns.get(
      "https://github.com/GameCult/cultnet-ts/contracts/cultnet.transport-profile.schema.json",
    )?.schemaVersion,
    "cultnet.transport_profile.v0",
  );

  const shardCatalog = CultMesh.createShardCatalog();
  const shardUpdates: string[] = [];
  const unsubscribeShard = shardCatalog.watch((descriptor) => {
    shardUpdates.push(descriptor.shardId);
  });
  shardCatalog.upsert({
    shardId: "notes-a",
    ownerRuntimeId: "ts-runtime",
    epoch: 7,
    schemaIds: ["cultmesh.ts.note.v1"],
    keyPrefix: "note:",
  });
  unsubscribeShard();
  shardCatalog.upsert({
    shardId: "notes-b",
    ownerRuntimeId: "ts-runtime",
    epoch: 8,
  });

  assert.deepEqual(shardUpdates, ["notes-a"]);
  assert.equal(shardCatalog.get("notes-a")?.ownerRuntimeId, "ts-runtime");
  assert.deepEqual(
    shardCatalog
      .list({ schemaIds: ["cultmesh.ts.note.v1"], recordKeys: ["note:1"] })
      .map((shard) => shard.shardId),
    ["notes-a"],
  );
});

test("CultMesh TS branded facade creates RUDP clients from peer endpoints", async () => {
  const connectionId = 0x10203044;
  const server = await CultMesh.createRudpServer(
    "cultmesh-ts-rudp-server",
    connectionId,
    {
      resendDelayMs: 25,
      resendPollMs: 5,
      maxFragmentBytes: 1024,
      maxPendingReliablePackets: 16,
    },
  );
  const serverPort = server.profile.transports[0]?.port;
  assert.equal(typeof serverPort, "number");
  const endpoint = CultMesh.parseRudpEndpoint(`rudp://127.0.0.1:${serverPort}`);
  assert.equal(endpoint.host, "127.0.0.1");
  assert.equal(endpoint.port, serverPort);

  const peer = {
    peerId: "cultmesh-ts-rudp-server",
    verseId: "local",
    endpoints: [endpoint.uri],
    roles: ["schema"],
    authorityLeaseId: "lease:cultmesh-ts-rudp-server",
  };
  const peers = CultMesh.createPeerCatalog();
  const leases = CultMesh.createAuthorityLeaseCatalog();
  peers.upsert(peer);
  await assert.rejects(
    CultMesh.createRudpClientForAuthorizedPeer(
      "cultmesh-ts-rudp-client",
      connectionId,
      peers,
      leases,
      "local",
      "schema",
      {
        resendDelayMs: 25,
        resendPollMs: 5,
        maxFragmentBytes: 1024,
        maxPendingReliablePackets: 16,
      },
    ),
    /No authorized RUDP peer/,
  );
  leases.upsert({
    leaseId: "lease:cultmesh-ts-rudp-server",
    verseId: "local",
    peerId: "cultmesh-ts-rudp-server",
    roles: ["schema"],
    validFrom: new Date(Date.now() - 1000),
    expiresAt: new Date(Date.now() + 1000),
  });
  const client = await CultMesh.createRudpClientForAuthorizedPeer(
    "cultmesh-ts-rudp-client",
    connectionId,
    peers,
    leases,
    "local",
    "schema",
    {
      resendDelayMs: 25,
      resendPollMs: 5,
      maxFragmentBytes: 1024,
      maxPendingReliablePackets: 16,
    },
  );

  try {
    const serverFrame = new Promise<{ channelId: string; payload: Uint8Array }>(
      (resolve, reject) => {
        server.once("frame", resolve);
        server.once("error", reject);
      },
    );
    client.connect(Buffer.from("join", "utf8"));
    await waitFor(
      () => client.connected && server.connected,
      "CultMesh RUDP handshake",
    );
    client.send("schema", Buffer.from("client-state", "utf8"));
    const receivedByServer = await serverFrame;
    assert.equal(receivedByServer.channelId, "schema");
    assert.equal(Buffer.from(receivedByServer.payload).toString("utf8"), "client-state");
    assert.equal(server.profile.transports[0]?.protocol, "rudp");
    assert.equal(client.profile.transports[0]?.protocol, "rudp");
  } finally {
    client.close();
    server.close();
  }
});

test("CultMesh TS creates connected RUDP schema peers for catalog requests", async () => {
  const connectionId = 0x10203045;
  const serverTransport = await CultMesh.createRudpServer(
    "cultmesh-ts-rudp-schema-server",
    connectionId,
    {
      resendDelayMs: 25,
      resendPollMs: 5,
      maxFragmentBytes: 1024,
      maxPendingReliablePackets: 16,
    },
  );
  const serverPort = serverTransport.profile.transports[0]?.port;
  assert.equal(typeof serverPort, "number");
  const serverPeer = new CultNetPeer(serverTransport, {
    wireContract: "cultnet.schema.v0",
  });
  serverPeer.on("message", (message) => {
    if (message.schemaVersion === "cultnet.schema_catalog_request.v0") {
      serverPeer.sendSchemaCatalogResponse(
        cultNetBuiltinSchemaRegistry.createCatalogResponse(message),
      );
    }
  });

  const peer = {
    peerId: "cultmesh-ts-rudp-schema-server",
    verseId: "local",
    endpoints: [`rudp://127.0.0.1:${serverPort}`],
    roles: ["schema"],
    authorityLeaseId: "lease:cultmesh-ts-rudp-schema-server",
  };
  const peers = CultMesh.createPeerCatalog();
  const leases = CultMesh.createAuthorityLeaseCatalog();
  peers.upsert(peer);
  leases.upsert({
    leaseId: "lease:cultmesh-ts-rudp-schema-server",
    verseId: "local",
    peerId: "cultmesh-ts-rudp-schema-server",
    roles: ["schema"],
    validFrom: new Date(Date.now() - 1000),
    expiresAt: new Date(Date.now() + 1000),
  });

  let clientPeer: CultNetPeer | undefined;
  try {
    clientPeer = await CultMesh.createRudpPeerForAuthorizedPeer(
      "cultmesh-ts-rudp-schema-client",
      connectionId,
      peers,
      leases,
      "local",
      "schema",
      {
        resendDelayMs: 25,
        resendPollMs: 5,
        maxFragmentBytes: 1024,
        maxPendingReliablePackets: 16,
        connectTimeoutMs: 1_000,
      },
    );
    const synced = CultMesh.createSchemaCatalog();
    const applied = await clientPeer.syncSchemaCatalog(synced, {
      messageId: "cultmesh-ts-rudp-schema-catalog",
      kinds: ["document_payload"],
      includeSchemaJson: true,
      timeoutMs: 1_000,
    });

    assert.ok(applied.some((descriptor) => descriptor.documentType === "ghostlight.agent-state"));
    assert.equal(clientPeer.transportProfile?.transports[0]?.protocol, "rudp");
    assert.equal(serverPeer.transportProfile?.transports[0]?.protocol, "rudp");
  } finally {
    clientPeer?.close();
    serverPeer.close();
  }
});

test("CultMesh TS RUDP document server accepts raw document puts from multiple peers", async () => {
  const connectionId = 0x10203046;
  const received: string[] = [];
  const server = CultMesh.createRudpDocumentServer(
    "cultmesh-ts-document-server",
    connectionId,
    {
      documents: new CultNetDocumentRegistry(),
      bindHost: "127.0.0.1",
      bindPort: 0,
      resendDelayMs: 25,
      resendPollMs: 5,
      maxFragmentBytes: 1024,
      maxPendingReliablePackets: 16,
      onDocumentPutRaw: (document) => {
        received.push(`${document.sourceRuntimeId}:${document.recordKey}:${document.payload}`);
      },
    },
  );

  let firstPeer: CultNetPeer | undefined;
  let secondPeer: CultNetPeer | undefined;
  try {
    await server.start();
    firstPeer = await CultMesh.createRudpPeer(
      "cultmesh-ts-document-client-a",
      connectionId,
      `rudp://127.0.0.1:${server.bind.port}`,
      {
        resendDelayMs: 25,
        resendPollMs: 5,
        maxFragmentBytes: 1024,
        maxPendingReliablePackets: 16,
        connectTimeoutMs: 1_000,
      },
    );
    secondPeer = await CultMesh.createRudpPeer(
      "cultmesh-ts-document-client-b",
      connectionId,
      `rudp://127.0.0.1:${server.bind.port}`,
      {
        resendDelayMs: 25,
        resendPollMs: 5,
        maxFragmentBytes: 1024,
        maxPendingReliablePackets: 16,
        connectTimeoutMs: 1_000,
      },
    );

    firstPeer.send({
      schemaVersion: "cultnet.document_put_raw.v0",
      messageId: "raw-put-a",
      document: {
        schemaId: "cultmesh.note.v0",
        recordKey: "note:a",
        storedAt: new Date().toISOString(),
        payloadEncoding: "messagepack",
        payload: encode("first"),
        sourceRuntimeId: "client-a",
      },
    });
    secondPeer.send({
      schemaVersion: "cultnet.document_put_raw.v0",
      messageId: "raw-put-b",
      document: {
        schemaId: "cultmesh.note.v0",
        recordKey: "note:b",
        storedAt: new Date().toISOString(),
        payloadEncoding: "messagepack",
        payload: encode("second"),
        sourceRuntimeId: "client-b",
      },
    });

    await waitFor(() => received.length === 2, "two RUDP document puts");
    assert.deepEqual(received.sort(), [
      "client-a:note:a:first",
      "client-b:note:b:second",
    ]);
  } finally {
    firstPeer?.close();
    secondPeer?.close();
    server.close();
  }
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
