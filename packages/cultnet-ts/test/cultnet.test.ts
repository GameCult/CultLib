import test from "node:test";
import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { Duplex } from "node:stream";
import dgram, { type Socket } from "node:dgram";
import { rmSync, mkdtempSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";

import { z } from "zod";
import {
  CultCache,
  SingleFileMessagePackBackingStore,
  defineDocumentType,
} from "@gamecult/cultcache-ts";

import {
  CultNetClientSecurityOptions,
  CultNetDocumentRegistry,
  CultNetPeer,
  CultNetRudpReconnectLoop,
  CultNetRudpSession,
  CultNetRudpSocketTransportConnection,
  CULTNET_RUDP_RELIABLE_SEND_WINDOW_PACKETS,
  CultNetSchemaCatalog,
  CultNetSchemaRegistry,
  CultNetSecret,
  CultNetServerSecurityOptions,
  CultNetShardCatalog,
  TcpFramedTransportConnection,
  CultNetReconnectController,
  cultNetSchemas,
  cultNetBuiltinSchemaRegistry,
  computeCultNetReconnectDelayMs,
  createCultNetReconnectPolicy,
  createTcpFramedCultNetPeer,
  createTcpFramedTransportProfile,
  createRudpTransportProfile,
  defineCultNetDocumentBinding,
  decodeRudpPacket,
  type CultNetOperationRequestMessage,
  type CultNetOperationResponseMessage,
  type CultNetRudpPacket,
  encodeCultNetMessageForWire,
  encodeRudpPacket,
  ghostlightAgentStateGeneratedContract,
  parseCultNetMessage,
  invokeCultNetOperation,
  startCultNetOperationServer,
  validateGhostlightAgentStateGenerated,
  validateGhostlightAgentState,
  shardServes,
  type CultNetLoginMessage,
  type GhostlightAgentStateShape,
  type GhostlightAgentStateDocument,
} from "../src";
import {
  INTEROP_SCHEMA_VERSION,
  createInteropFormatter,
  createLegacyInteropNoteFormatter,
  createMismatchedInteropNoteFormatter,
  type InteropNote,
} from "./interop/cultnet-interop-shared";

test("CultNet validates the canonical CultMesh Verse catalog messages", () => {
  const request = parseCultNetMessage({
    schemaVersion: "cultmesh.verse_catalog_request.v0",
    messageId: "catalog-1",
    verseIds: ["sample.counter"],
    transportVersion: "cultmesh.v1",
  });
  assert.equal(request.schemaVersion, "cultmesh.verse_catalog_request.v0");

  const response = parseCultNetMessage({
    schemaVersion: "cultmesh.verse_catalog_response.v0",
    messageId: "catalog-1",
    verses: [{
      verseId: "sample.counter",
      displayName: "Counter",
      authorityModel: "OperatorCluster",
      compatibility: {
        transportVersion: "cultmesh.v1",
        rulesHash: "counter-v1",
        compatibleVerseIds: [],
        requiredPluginIds: [],
        optionalPluginIds: [],
      },
      discoveryEndpoints: ["wss://provider.example/cultmesh"],
      authorityRuntimeIds: ["sample.counter-provider"],
      authorityRoutes: [{
        authorityRuntimeId: "sample.counter-provider",
        endpoint: "wss://provider.example/cultmesh",
        protocolIds: ["cultmesh.documents.v1"],
        priority: 0,
        generation: "provider-route-1",
      }],
    }],
  });
  assert.equal(response.schemaVersion, "cultmesh.verse_catalog_response.v0");
  assert.equal(response.verses[0].authorityRuntimeIds[0], "sample.counter-provider");
  assert.equal(response.verses[0].authorityRoutes![0].generation, "provider-route-1");

  const session = parseCultNetMessage({
    schemaVersion: "cultmesh.session_accepted.v2",
    messageId: "session-1",
    accepted: true,
    verseId: "sample.counter",
    authorityRuntimeId: "sample.counter-provider",
    protocolId: "cultmesh.documents.v1",
    routeGeneration: "provider-route-1",
    clientNonce: "bm9uY2U=",
    providerKeyId: "provider-key-1",
    providerSignature: "c2lnbmF0dXJl",
  });
  assert.equal(session.schemaVersion, "cultmesh.session_accepted.v2");

  assert.throws(() => parseCultNetMessage({
    ...response,
    verses: [{ ...response.verses[0], compatibility: { ...response.verses[0].compatibility, rulesHash: "" } }],
  }), /rulesHash/);
});

class FakeRudpReconnectTransport extends EventEmitter {
  public connectCalls = 0;
  public closeCalls = 0;
  public lastPayload: Uint8Array | undefined;

  public connect(payload = new Uint8Array()): void {
    this.connectCalls += 1;
    this.lastPayload = payload;
  }

  public close(): void {
    this.closeCalls += 1;
    this.emit("close");
  }
}

class LinkedDuplex extends Duplex {
  peer?: LinkedDuplex;

  // eslint-disable-next-line @typescript-eslint/no-empty-function
  _read(): void {}

  _write(
    chunk: Buffer,
    _encoding: BufferEncoding,
    callback: (error?: Error | null) => void,
  ): void {
    this.peer?.push(Buffer.from(chunk));
    callback();
  }

  _final(callback: (error?: Error | null) => void): void {
    this.peer?.push(null);
    callback();
  }
}

function createDuplexPair(): { a: Duplex; b: Duplex } {
  const a = new LinkedDuplex();
  const b = new LinkedDuplex();
  a.peer = b;
  b.peer = a;
  return { a, b };
}

async function bindUdpSocket(): Promise<Socket> {
  const socket = dgram.createSocket("udp4");
  await new Promise<void>((resolve) => socket.bind(0, "127.0.0.1", resolve));
  return socket;
}

function udpPort(socket: Socket): number {
  const address = socket.address();
  assert.notEqual(typeof address, "string");
  return address.port;
}

async function waitFor(predicate: () => boolean, description: string): Promise<void> {
  const startedAt = Date.now();
  while (!predicate()) {
    if (Date.now() - startedAt > 1_000) {
      throw new Error(`Timed out waiting for ${description}.`);
    }
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
}

test("CultNet secret helpers round-trip encrypted strings and validate sessions", () => {
  const serverSecurity = CultNetServerSecurityOptions.development();
  const clientSecurity = serverSecurity.toClientOptions();
  const nonce = CultNetSecret.newNonce();
  const encrypted = CultNetSecret.encryptString("hello", nonce, clientSecurity);
  assert.ok(encrypted);
  assert.equal(CultNetSecret.decryptString(encrypted, nonce, serverSecurity), "hello");

  const token = CultNetSecret.createSessionToken(
    "runtime-face",
    new Date(Date.now() + 60_000),
    serverSecurity,
  );
  const validated = CultNetSecret.tryValidateSessionToken(token, serverSecurity);
  assert.ok(validated);
  assert.equal(validated?.userId, "runtime-face");
  assert.equal(validated?.sessionVersion, 0);
});

test("CultNet secret helpers validate C# and Python compatible versioned sessions", () => {
  const serverSecurity = CultNetServerSecurityOptions.development();
  const expires = new Date(Date.now() + 60_000);
  const token = CultNetSecret.createSessionToken(
    "318fb4b6-ff5e-4c4f-b911-d81807de53a8",
    expires,
    serverSecurity,
    42,
  );
  const [payload] = token.split(".");
  assert.equal(
    new TextDecoder().decode(CultNetSecret.fromBase64Url(payload!)),
    `318fb4b6ff5e4c4fb911d81807de53a8|${Math.floor(expires.getTime() / 1000)}|42`,
  );

  const validated = CultNetSecret.tryValidateSessionToken(token, serverSecurity);
  assert.ok(validated);
  assert.equal(validated?.userId, "318fb4b6ff5e4c4fb911d81807de53a8");
  assert.equal(validated?.sessionVersion, 42);
  assert.throws(
    () => CultNetSecret.createSessionToken("runtime-face", expires, serverSecurity, 1),
    /Guid-compatible/,
  );
});

test("CultNet secret helpers validate Python-created versioned sessions", () => {
  const token = [
    "MzE4ZmI0YjZmZjVlNGM0ZmI5MTFkODE4MDdkZTUzYTh8MjA1MTIyMjQwMHw3Nw",
    "jRrUiE5Om7NQVKMJP4PkBkLFVLXqNb8Uu9jg4VG13pU",
  ].join(".");

  const validated = CultNetSecret.tryValidateSessionToken(token, CultNetServerSecurityOptions.development());
  assert.ok(validated);
  assert.equal(validated?.userId, "318fb4b6ff5e4c4fb911d81807de53a8");
  assert.equal(validated?.sessionVersion, 77);
});

test("CultNet peer frames and decodes typed messages over a direct pipe", async () => {
  const { a, b } = createDuplexPair();
  const sender = new CultNetPeer(a, { wireContract: "cultnet.schema.v0" });
  const receiver = new CultNetPeer(b, { wireContract: "cultnet.schema.v0" });

  const message = await new Promise<ReturnType<typeof parseCultNetMessage>>((resolve, reject) => {
    receiver.once("message", resolve);
    receiver.once("invalidMessage", reject);
    sender.sendHello({
      schemaVersion: "cultnet.hello.v0",
      runtimeId: "voidbot-main",
      runtimeKind: "node-worker",
      agentId: "void",
      displayName: "Void",
      supportedDocumentTypes: ["ghostlight.agent-state"],
      transportProfiles: [
        {
          schemaVersion: "cultnet.transport_profile.v0",
          runtimeId: "voidbot-main",
          transports: [
            {
              transportId: "direct-pipe",
              protocol: "tcp_framed",
              wireContracts: ["cultnet.schema.v0"],
              channels: [{ channelId: "schema", delivery: "reliable", ordering: "ordered" }],
            },
          ],
        },
      ],
    });
  });

  assert.equal(message.schemaVersion, "cultnet.hello.v0");
  if (message.schemaVersion === "cultnet.hello.v0") {
    assert.equal(message.runtimeId, "voidbot-main");
    assert.equal(message.agentId, "void");
    assert.equal(message.transportProfiles?.[0]?.transports[0]?.protocol, "tcp_framed");
  }

  sender.close();
  receiver.close();
});

test("tcp_framed transport carries raw schema channel payloads with stats", async () => {
  const { a, b } = createDuplexPair();
  const left = new TcpFramedTransportConnection(a, createTcpFramedTransportProfile("left"));
  const right = new TcpFramedTransportConnection(b, createTcpFramedTransportProfile("right"));

  const frame = await new Promise<{ channelId: string; payload: Uint8Array }>((resolve, reject) => {
    right.once("frame", resolve);
    right.once("error", reject);
    left.send("schema", Buffer.from("payload", "utf8"));
  });

  assert.equal(frame.channelId, "schema");
  assert.equal(Buffer.from(frame.payload).toString("utf8"), "payload");
  assert.equal(left.stats.framesSent, 1);
  assert.equal(right.stats.framesReceived, 1);
  assert.throws(() => left.send("unreliable", Buffer.alloc(0)), /only supports the schema channel/);

  left.close();
  right.close();
});

test("rudp packet codec has a deterministic reliable ordered fixture", () => {
  const encoded = encodeRudpPacket({
    packetType: "data",
    connectionId: 0x01020304,
    sequence: 0x0000002a,
    ack: 0x00000029,
    ackMask: 0x80000001,
    channelId: "schema",
    reliable: true,
    ordered: true,
    fragmentId: 7,
    fragmentIndex: 1,
    fragmentCount: 3,
    payload: Buffer.from("hello", "utf8"),
  });

  assert.equal(
    Buffer.from(encoded).toString("hex"),
    "434e523000030b2a010203040000002a0000002980000001000700010003000000050600736368656d6168656c6c6f",
  );

  const decoded = decodeRudpPacket(encoded);
  assert.equal(decoded.packetType, "data");
  assert.equal(decoded.connectionId, 0x01020304);
  assert.equal(decoded.sequence, 0x0000002a);
  assert.equal(decoded.ack, 0x00000029);
  assert.equal(decoded.ackMask, 0x80000001);
  assert.equal(decoded.channelId, "schema");
  assert.equal(decoded.reliable, true);
  assert.equal(decoded.ordered, true);
  assert.equal(decoded.sequenced, false);
  assert.equal(decoded.fragmentId, 7);
  assert.equal(decoded.fragmentIndex, 1);
  assert.equal(decoded.fragmentCount, 3);
  assert.equal(Buffer.from(decoded.payload ?? []).toString("utf8"), "hello");
});

test("rudp transport profile advertises state and realtime channel semantics", () => {
  const profile = createRudpTransportProfile("node-rudp", {
    transportId: "public-rudp",
    host: "127.0.0.1",
    port: 7777,
    maxPayloadBytes: 1200,
    maxFragmentBytes: 1000,
  });

  assert.equal(profile.transports[0]?.protocol, "rudp");
  assert.equal(profile.transports[0]?.reconnectPolicy?.schemaVersion, "cultnet.reconnect_policy.v0");
  assert.equal(profile.transports[0]?.reconnectPolicy?.baseDelayMs, 1_000);
  assert.deepEqual(
    profile.transports[0]?.channels.map((channel) => [channel.channelId, channel.delivery, channel.ordering]),
    [
      ["schema", "reliable", "ordered"],
      ["latest", "unreliable", "sequenced"],
      ["realtime", "unreliable", "unordered"],
    ],
  );
});

test("reconnect policy exposes the shared portable delay contract", () => {
  const policy = createCultNetReconnectPolicy({ policyId: "rudp-default", maxAttempts: 8 });

  assert.equal(policy.schemaVersion, "cultnet.reconnect_policy.v0");
  assert.equal(policy.policyId, "rudp-default");
  assert.equal(policy.maxAttempts, 8);
  assert.equal(computeCultNetReconnectDelayMs(policy, 1), 1_000);
  assert.equal(computeCultNetReconnectDelayMs(policy, 3, 17), 4_017);
  assert.equal(computeCultNetReconnectDelayMs(policy, 9, 999), 30_250);
  assert.equal(computeCultNetReconnectDelayMs(policy, 0, -5), 1_000);
});

test("reconnect controller schedules attempts and reset with the shared policy", () => {
  const policy = createCultNetReconnectPolicy({ maxAttempts: 2 });
  const controller = new CultNetReconnectController(policy);

  const first = controller.recordFailure(10_000);
  assert.deepEqual(first, {
    attempt: 1,
    shouldRetry: true,
    delayMs: 1_000,
    nextAttemptAtMs: 11_000,
    exhausted: false,
  });
  assert.equal(controller.canAttempt(10_999), false);
  assert.equal(controller.canAttempt(11_000), true);

  const second = controller.recordFailure(11_000, 17);
  assert.equal(second.attempt, 2);
  assert.equal(second.delayMs, 2_017);
  assert.equal(second.nextAttemptAtMs, 13_017);
  assert.equal(second.shouldRetry, true);

  const exhausted = controller.recordFailure(13_017);
  assert.deepEqual(exhausted, {
    attempt: 2,
    shouldRetry: false,
    delayMs: 0,
    exhausted: true,
  });
  assert.equal(controller.exhausted, true);
  assert.equal(controller.canAttempt(99_000), false);

  controller.reset();
  assert.equal(controller.attempt, 0);
  assert.equal(controller.nextAttemptAtMs, undefined);
  assert.equal(controller.exhausted, false);
  assert.equal(controller.canAttempt(99_000), true);
});

test("rudp reconnect loop consumes the shared reconnect controller", () => {
  let nowMs = 10_000;
  let capturedTimer: (() => void) | undefined;
  const transports: FakeRudpReconnectTransport[] = [];
  const loop = new CultNetRudpReconnectLoop({
    reconnectPolicy: createCultNetReconnectPolicy({ maxAttempts: 2 }),
    connectPayload: Buffer.from("join", "utf8"),
    createTransport: () => {
      const transport = new FakeRudpReconnectTransport();
      transports.push(transport);
      return transport;
    },
    nowMs: () => nowMs,
    jitterMs: () => 17,
    setTimer: (callback, delayMs) => {
      assert.equal(delayMs, 1_017);
      capturedTimer = callback;
      return "timer";
    },
    clearTimer: () => {
      capturedTimer = undefined;
    },
  });

  const first = loop.start() as FakeRudpReconnectTransport;
  assert.equal(first.connectCalls, 1);
  assert.equal(Buffer.from(first.lastPayload ?? []).toString("utf8"), "join");

  first.emit("close");
  assert.equal(loop.reconnectController.attempt, 1);
  assert.equal(loop.reconnectController.nextAttemptAtMs, 11_017);
  assert.equal(typeof capturedTimer, "function");

  nowMs = 11_017;
  capturedTimer?.();
  assert.equal(transports.length, 2);
  assert.equal(transports[1]?.connectCalls, 1);

  loop.markConnected();
  assert.equal(loop.reconnectController.attempt, 0);

  loop.stop();
  assert.equal(transports[1]?.closeCalls, 1);
  assert.equal(loop.transport, undefined);
});

test("rudp session handshake acks reliable connect and accept packets", () => {
  const client = new CultNetRudpSession({ connectionId: 0x0a0b0c0d, initialSequence: 1, resendDelayMs: 50 });
  const server = new CultNetRudpSession({ connectionId: 0x0a0b0c0d, initialSequence: 100, resendDelayMs: 50 });

  const connect = client.createConnect(0, Buffer.from("join", "utf8"));
  assert.equal(connect.packetType, "connect");
  assert.equal(connect.sequence, 1);
  assert.deepEqual(client.pendingReliableSequences, [1]);

  const accept = server.acceptConnect(connect, 10, Buffer.from("ok", "utf8"));
  assert.equal(accept.packetType, "accept");
  assert.equal(accept.ack, 1);
  assert.equal(server.connected, true);
  assert.deepEqual(server.pendingReliableSequences, [100]);

  const retransmittedAccept = server.acceptConnect(connect, 15, Buffer.from("ok", "utf8"));
  assert.deepEqual(
    encodeRudpPacket(retransmittedAccept),
    encodeRudpPacket(accept),
    "a retransmitted Connect reuses the pending Accept packet",
  );
  assert.deepEqual(server.pendingReliableSequences, [100]);

  client.receive(accept, 20);
  assert.equal(client.connected, true);
  assert.deepEqual(client.pendingReliableSequences, []);

  const ack = client.createAck();
  assert.equal(ack.sequence, 0);
  assert.equal(ack.ack, 100);
  server.receive(ack, 30);
  assert.deepEqual(server.pendingReliableSequences, []);
});

test("rudp session computes ack masks and clears pending reliable packets", () => {
  const sender = new CultNetRudpSession({ connectionId: 7, initialSequence: 10, resendDelayMs: 100 });
  const receiver = new CultNetRudpSession({ connectionId: 7, initialSequence: 200, resendDelayMs: 100 });
  sender.receive({ packetType: "accept", connectionId: 7, sequence: 1, ack: 0, ackMask: 0, channelId: "control" });
  receiver.receive({ packetType: "accept", connectionId: 7, sequence: 2, ack: 0, ackMask: 0, channelId: "control" });

  const first = sender.send("schema", Buffer.from("first"), { reliable: true, ordered: true, nowMs: 0 });
  const second = sender.send("schema", Buffer.from("second"), { reliable: true, ordered: true, nowMs: 0 });
  const third = sender.send("schema", Buffer.from("third"), { reliable: true, ordered: true, nowMs: 0 });
  assert.deepEqual(sender.pendingReliableSequences, [10, 11, 12]);

  receiver.receive(first);
  receiver.receive(third);
  const ackWithGap = receiver.createAck();
  assert.equal(ackWithGap.ack, 12);
  assert.equal(ackWithGap.ackMask, 0b10 | (1 << 9));
  sender.receive(ackWithGap);
  assert.deepEqual(sender.pendingReliableSequences, [11]);

  receiver.receive(second);
  const fullAck = receiver.createAck();
  assert.equal(fullAck.ack, 12);
  assert.equal(fullAck.ackMask, 0b11 | (1 << 9));
  sender.receive(fullAck);
  assert.deepEqual(sender.pendingReliableSequences, []);
});

test("rudp session schedules reliable resends until acked", () => {
  const session = new CultNetRudpSession({ connectionId: 99, initialSequence: 1, resendDelayMs: 100 });
  session.receive({ packetType: "accept", connectionId: 99, sequence: 50, ack: 0, ackMask: 0, channelId: "control" });
  const sent = session.send("schema", Buffer.from("payload"), { reliable: true, ordered: true, nowMs: 10 });

  assert.deepEqual(session.dueResends(90), []);
  assert.deepEqual(session.dueResends(110).map((packet) => packet.sequence), [sent.sequence]);
  assert.deepEqual(session.dueResends(150), []);

  session.receive({ packetType: "ack", connectionId: 99, sequence: 51, ack: sent.sequence, ackMask: 0, channelId: "control" });
  assert.deepEqual(session.dueResends(250), []);
});

test("rudp session pings and detects receive timeout", () => {
  const client = new CultNetRudpSession({ connectionId: 101, initialSequence: 1 });
  const server = new CultNetRudpSession({ connectionId: 101, initialSequence: 100 });
  const connect = client.createConnect(0, Buffer.from("join"));
  const accept = server.acceptConnect(connect, 10);
  client.receive(accept, 20);

  const ping = client.createPing(Buffer.from("pulse"));
  const pingResult = server.receive(ping, 30);
  assert.equal(pingResult.reply?.packetType, "pong");
  assert.deepEqual(Buffer.from(pingResult.reply?.payload ?? []), Buffer.from("pulse"));

  const pongResult = client.receive(pingResult.reply!, 40);
  assert.equal(pongResult.pong, true);
  assert.deepEqual(Buffer.from(pongResult.pongPayload ?? []), Buffer.from("pulse"));
  assert.equal(client.checkTimeout(90, 50), false);
  assert.equal(client.checkTimeout(91, 50), true);
  assert.equal(client.connected, false);
});

test("rudp lossy packets cannot create reliable ordered gaps", () => {
  const client = new CultNetRudpSession({ connectionId: 198, initialSequence: 1 });
  const server = new CultNetRudpSession({ connectionId: 198, initialSequence: 100 });
  const connect = client.createConnect(0);
  const accept = server.acceptConnect(connect, 1);
  client.receive(accept, 2);

  const ping = client.createPing(Buffer.from("pulse"));
  const pong = server.receive(ping, 3).reply!;
  const realtime = server.send("realtime", Buffer.from("discarded realtime"));
  const latest = server.send("latest", Buffer.from("discarded latest state"), { sequenced: true });
  const schema = server.send("schema", Buffer.from("committed response"), { reliable: true, ordered: true });

  assert.equal(ping.sequence, 0);
  assert.equal(pong.sequence, 0);
  assert.equal(realtime.sequence, 0);
  assert.equal(latest.sequence, 1);
  assert.equal(schema.sequence, accept.sequence + 1);
  assert.deepEqual(client.receive(schema, 4).delivered.map(frame => Buffer.from(frame.payload).toString("utf8")), [
    "committed response",
  ]);
});

test("rudp unreliable sequenced delivery is scoped to its channel", () => {
  const sender = new CultNetRudpSession({ connectionId: 197, initialSequence: 50 });
  const receiver = new CultNetRudpSession({ connectionId: 197, initialSequence: 100 });
  const connect = sender.createConnect(0);
  const accept = receiver.acceptConnect(connect, 1);
  sender.receive(accept, 2);
  const older = sender.send("latest", Buffer.from("older"), { sequenced: true });
  const newer = sender.send("latest", Buffer.from("newer"), { sequenced: true });

  assert.equal(older.sequence, 1);
  assert.equal(newer.sequence, 2);
  assert.deepEqual(receiver.receive(newer, 3).delivered.map(frame => Buffer.from(frame.payload).toString("utf8")), ["newer"]);
  assert.deepEqual(receiver.receive(older, 4).delivered, []);
});

test("rudp rejects unreliable ordered delivery", () => {
  const session = new CultNetRudpSession({ connectionId: 196 });
  session.receive({ packetType: "accept", connectionId: 196, sequence: 50, ack: 0, ackMask: 0, channelId: "control" });
  assert.throws(
    () => session.send("schema", Buffer.from("cannot order what will not retransmit"), { ordered: true }),
    /ordered delivery requires reliability/,
  );
});

test("rudp session bounds pending reliable packets before enqueue", () => {
  const session = new CultNetRudpSession({ connectionId: 102, initialSequence: 1, maxPendingReliablePackets: 2 });
  session.receive({ packetType: "accept", connectionId: 102, sequence: 50, ack: 0, ackMask: 0, channelId: "control" });

  session.send("schema", Buffer.from("first"), { reliable: true, ordered: true });
  session.send("schema", Buffer.from("second"), { reliable: true, ordered: true });
  assert.throws(
    () => session.send("schema", Buffer.from("third"), { reliable: true, ordered: true }),
    /reliable send queue is full/,
  );
  assert.deepEqual(session.pendingReliableSequences, [1, 2]);

  const fragmented = new CultNetRudpSession({ connectionId: 103, initialSequence: 1, maxPendingReliablePackets: 3 });
  fragmented.receive({ packetType: "accept", connectionId: 103, sequence: 50, ack: 0, ackMask: 0, channelId: "control" });
  assert.throws(
    () => fragmented.sendMany("schema", Buffer.from("fragment-me"), { reliable: true, ordered: true, maxFragmentBytes: 3 }),
    /reliable send queue is full/,
  );
  assert.deepEqual(fragmented.pendingReliableSequences, []);
});

test("rudp session suppresses duplicates and delivers reliable ordered payloads in sequence", () => {
  const sender = new CultNetRudpSession({ connectionId: 123, initialSequence: 1 });
  const receiver = new CultNetRudpSession({ connectionId: 123, initialSequence: 100 });
  sender.receive({ packetType: "accept", connectionId: 123, sequence: 90, ack: 0, ackMask: 0, channelId: "control" });
  receiver.receive({ packetType: "accept", connectionId: 123, sequence: 91, ack: 0, ackMask: 0, channelId: "control" });

  const first = sender.send("schema", Buffer.from("first"), { reliable: true, ordered: true });
  const second = sender.send("schema", Buffer.from("second"), { reliable: true, ordered: true });
  const third = sender.send("schema", Buffer.from("third"), { reliable: true, ordered: true });

  assert.deepEqual(receiver.receive(first).delivered.map((frame) => Buffer.from(frame.payload).toString("utf8")), ["first"]);
  assert.deepEqual(receiver.receive(third).delivered, []);
  assert.deepEqual(receiver.receive(first).delivered, []);
  assert.deepEqual(receiver.receive(second).delivered.map((frame) => Buffer.from(frame.payload).toString("utf8")), [
    "second",
    "third",
  ]);
});

test("rudp session skips received control packets while ordering schema payloads", () => {
  const sender = new CultNetRudpSession({ connectionId: 124, initialSequence: 1 });
  const receiver = new CultNetRudpSession({ connectionId: 124, initialSequence: 100 });
  sender.receive({ packetType: "accept", connectionId: 124, sequence: 90, ack: 0, ackMask: 0, channelId: "control" });
  receiver.receive({ packetType: "accept", connectionId: 124, sequence: 91, ack: 0, ackMask: 0, channelId: "control" });

  const first = sender.send("schema", Buffer.from("first"), { reliable: true, ordered: true });
  const control = sender.createAck();
  const second = sender.send("schema", Buffer.from("second"), { reliable: true, ordered: true });

  assert.equal(control.sequence, 0);
  assert.equal(second.sequence, first.sequence + 1);

  assert.deepEqual(receiver.receive(first).delivered.map((frame) => Buffer.from(frame.payload).toString("utf8")), ["first"]);
  assert.deepEqual(receiver.receive(control).delivered, []);
  assert.deepEqual(receiver.receive(second).delivered.map((frame) => Buffer.from(frame.payload).toString("utf8")), ["second"]);
});

test("rudp session fragments and reassembles reliable ordered payloads", () => {
  const sender = new CultNetRudpSession({ connectionId: 456, initialSequence: 1 });
  const receiver = new CultNetRudpSession({ connectionId: 456, initialSequence: 100 });
  sender.receive({ packetType: "accept", connectionId: 456, sequence: 90, ack: 0, ackMask: 0, channelId: "control" });
  receiver.receive({ packetType: "accept", connectionId: 456, sequence: 91, ack: 0, ackMask: 0, channelId: "control" });

  const packets = sender.sendMany("schema", Buffer.from("fragment-me-please"), {
    reliable: true,
    ordered: true,
    nowMs: 10,
    maxFragmentBytes: 5,
  });
  assert.equal(packets.length, 4);
  assert.deepEqual(packets.map((packet) => packet.fragmentCount), [4, 4, 4, 4]);
  assert.deepEqual(packets.map((packet) => packet.fragmentIndex), [0, 1, 2, 3]);
  assert.ok(packets.every((packet) => packet.fragmentId === packets[0]?.fragmentId));

  assert.deepEqual(receiver.receive(packets[0]!).delivered, []);
  assert.deepEqual(receiver.receive(packets[1]!).delivered, []);
  assert.deepEqual(receiver.receive(packets[2]!).delivered, []);
  const delivered = receiver.receive(packets[3]!).delivered;
  assert.equal(delivered.length, 1);
  assert.equal(Buffer.from(delivered[0]!.payload).toString("utf8"), "fragment-me-please");
  assert.equal(delivered[0]!.sequence, packets[0]!.sequence);
});

test("rudp socket transport handshakes and carries reliable ordered schema frames over UDP", async () => {
  const serverSocket = await bindUdpSocket();
  const clientSocket = await bindUdpSocket();
  const connectionId = 0x10203040;
  const server = new CultNetRudpSocketTransportConnection({
    runtimeId: "rudp-server",
    socket: serverSocket,
    mode: "server",
    connectionId,
    initialSequence: 100,
    resendDelayMs: 25,
    resendPollMs: 5,
  });
  const client = new CultNetRudpSocketTransportConnection({
    runtimeId: "rudp-client",
    socket: clientSocket,
    mode: "client",
    remoteHost: "127.0.0.1",
    remotePort: udpPort(serverSocket),
    connectionId,
    initialSequence: 1,
    resendDelayMs: 25,
    resendPollMs: 5,
  });

  try {
    const serverFrame = new Promise<{ channelId: string; payload: Uint8Array }>((resolve, reject) => {
      server.once("frame", resolve);
      server.once("error", reject);
    });
    client.connect(Buffer.from("join", "utf8"));
    await waitFor(() => client.connected && server.connected, "RUDP socket handshake");
    client.send("schema", Buffer.from("client-state", "utf8"));

    const receivedByServer = await serverFrame;
    assert.equal(receivedByServer.channelId, "schema");
    assert.equal(Buffer.from(receivedByServer.payload).toString("utf8"), "client-state");

    const clientFrame = new Promise<{ channelId: string; payload: Uint8Array }>((resolve, reject) => {
      client.once("frame", resolve);
      client.once("error", reject);
    });
    server.send("schema", Buffer.from("server-state", "utf8"));
    const receivedByClient = await clientFrame;
    assert.equal(receivedByClient.channelId, "schema");
    assert.equal(Buffer.from(receivedByClient.payload).toString("utf8"), "server-state");
    assert.equal(client.stats.framesSent, 1);
    assert.equal(server.stats.framesReceived, 1);
    assert.equal(server.profile.transports[0]?.protocol, "rudp");
  } finally {
    client.close();
    server.close();
  }
});

test("rudp socket transport carries fragmented reliable ordered schema frames over UDP", async () => {
  const serverSocket = await bindUdpSocket();
  const clientSocket = await bindUdpSocket();
  const connectionId = 0x10203041;
  const server = new CultNetRudpSocketTransportConnection({
    runtimeId: "rudp-fragment-server",
    socket: serverSocket,
    mode: "server",
    connectionId,
    initialSequence: 100,
    resendDelayMs: 25,
    resendPollMs: 5,
    maxFragmentBytes: 8,
  });
  const client = new CultNetRudpSocketTransportConnection({
    runtimeId: "rudp-fragment-client",
    socket: clientSocket,
    mode: "client",
    remoteHost: "127.0.0.1",
    remotePort: udpPort(serverSocket),
    connectionId,
    initialSequence: 1,
    resendDelayMs: 25,
    resendPollMs: 5,
    maxFragmentBytes: 8,
  });

  try {
    const payload = Buffer.from("this-schema-frame-is-larger-than-one-rudp-fragment", "utf8");
    const serverFrame = new Promise<{ channelId: string; payload: Uint8Array }>((resolve, reject) => {
      server.once("frame", resolve);
      server.once("error", reject);
    });
    client.connect(Buffer.from("join", "utf8"));
    await waitFor(() => client.connected && server.connected, "fragmented RUDP socket handshake");
    client.send("schema", payload);

    const receivedByServer = await serverFrame;
    assert.equal(receivedByServer.channelId, "schema");
    assert.equal(Buffer.from(receivedByServer.payload).toString("utf8"), payload.toString("utf8"));
    assert.equal(client.stats.framesSent, 1);
    assert.equal(server.stats.framesReceived, 1);
  } finally {
    client.close();
    server.close();
  }
});

test("CultNet peer can speak schema messages through the RUDP socket transport", async () => {
  const serverSocket = await bindUdpSocket();
  const clientSocket = await bindUdpSocket();
  const connectionId = 0x50607080;
  const serverTransport = new CultNetRudpSocketTransportConnection({
    runtimeId: "rudp-peer-server",
    socket: serverSocket,
    mode: "server",
    connectionId,
    initialSequence: 500,
    resendDelayMs: 25,
    resendPollMs: 5,
  });
  const clientTransport = new CultNetRudpSocketTransportConnection({
    runtimeId: "rudp-peer-client",
    socket: clientSocket,
    mode: "client",
    remoteHost: "127.0.0.1",
    remotePort: udpPort(serverSocket),
    connectionId,
    initialSequence: 10,
    resendDelayMs: 25,
    resendPollMs: 5,
  });

  try {
    const sender = new CultNetPeer(clientTransport, { wireContract: "cultnet.schema.v0" });
    const receiver = new CultNetPeer(serverTransport, { wireContract: "cultnet.schema.v0" });
    clientTransport.connect();
    await waitFor(() => clientTransport.connected && serverTransport.connected, "RUDP peer socket handshake");

    const message = await new Promise<ReturnType<typeof parseCultNetMessage>>((resolve, reject) => {
      receiver.once("message", resolve);
      receiver.once("invalidMessage", reject);
      clientTransport.once("error", reject);
      serverTransport.once("error", reject);
      sender.sendHello({
        schemaVersion: "cultnet.hello.v0",
        runtimeId: "rudp-peer-client",
        runtimeKind: "node-worker",
        transportProfiles: [clientTransport.profile],
      });
    });

    assert.equal(message.schemaVersion, "cultnet.hello.v0");
    if (message.schemaVersion === "cultnet.hello.v0") {
      assert.equal(message.runtimeId, "rudp-peer-client");
      assert.equal(message.transportProfiles?.[0]?.transports[0]?.protocol, "rudp");
    }

    sender.close();
    receiver.close();
  } finally {
    clientTransport.close();
    serverTransport.close();
  }
});

test("CultNet peer can speak through a transport connection", async () => {
  const { a, b } = createDuplexPair();
  const leftTransport = new TcpFramedTransportConnection(a, createTcpFramedTransportProfile("left"));
  const rightTransport = new TcpFramedTransportConnection(b, createTcpFramedTransportProfile("right"));
  const sender = new CultNetPeer(leftTransport, { wireContract: "cultnet.schema.v0" });
  const receiver = new CultNetPeer(rightTransport, { wireContract: "cultnet.schema.v0" });

  const message = await new Promise<ReturnType<typeof parseCultNetMessage>>((resolve, reject) => {
    receiver.once("message", resolve);
    receiver.once("invalidMessage", reject);
    sender.sendHello({
      schemaVersion: "cultnet.hello.v0",
      runtimeId: "transport-sender",
      runtimeKind: "node-worker",
    });
  });

  assert.equal(message.schemaVersion, "cultnet.hello.v0");
  if (message.schemaVersion === "cultnet.hello.v0") {
    assert.equal(message.runtimeId, "transport-sender");
  }
  assert.equal(leftTransport.stats.framesSent, 1);
  assert.equal(rightTransport.stats.framesReceived, 1);

  sender.close();
  receiver.close();
});

test("CultNet TCP helper constructs a transport-backed peer with profile metadata", async () => {
  const { a, b } = createDuplexPair();
  const sender = createTcpFramedCultNetPeer(a, {
    runtimeId: "tcp-helper-left",
    wireContract: "cultnet.schema.v0",
    transportId: "helper-left",
    host: "127.0.0.1",
    port: 17777,
    maxPayloadBytes: 64 * 1024,
  });
  const receiver = createTcpFramedCultNetPeer(b, {
    runtimeId: "tcp-helper-right",
    wireContract: "cultnet.schema.v0",
  });

  assert.equal(sender.transportProfile?.runtimeId, "tcp-helper-left");
  assert.equal(sender.transportProfile?.transports[0]?.transportId, "helper-left");
  assert.equal(sender.transportProfile?.transports[0]?.protocol, "tcp_framed");
  assert.equal(sender.transportProfile?.transports[0]?.host, "127.0.0.1");
  assert.equal(sender.transportProfile?.transports[0]?.port, 17777);
  assert.equal(sender.transportProfile?.transports[0]?.channels[0]?.channelId, "schema");
  assert.equal(sender.transportProfile?.transports[0]?.channels[0]?.maxPayloadBytes, 64 * 1024);

  const message = await new Promise<ReturnType<typeof parseCultNetMessage>>((resolve, reject) => {
    receiver.once("message", resolve);
    receiver.once("invalidMessage", reject);
    sender.sendHello({
      schemaVersion: "cultnet.hello.v0",
      runtimeId: "tcp-helper-sender",
      runtimeKind: "node-worker",
    });
  });

  assert.equal(message.schemaVersion, "cultnet.hello.v0");
  if (message.schemaVersion === "cultnet.hello.v0") {
    assert.equal(message.runtimeId, "tcp-helper-sender");
  }

  sender.close();
  receiver.close();
});

test("CultNet can round-trip gamecult.networking.v0 auth messages through the explicit legacy contract", () => {
  const message: CultNetLoginMessage = {
    schemaVersion: "cultnet.login.v0",
    nonce: "bm9uY2U",
    auth: "YXV0aA",
    password: "cGFzc3dvcmQ",
  };

  const wireValue = encodeCultNetMessageForWire(message, "gamecult.networking.v0");
  assert.deepEqual(wireValue, [
    0,
    [
      new TextEncoder().encode("nonce"),
      new TextEncoder().encode("auth"),
      new TextEncoder().encode("password"),
    ],
  ]);

  const decoded = parseCultNetMessage(wireValue, "gamecult.networking.v0");
  assert.deepEqual(decoded, message);
});

test("CultNet schema discovery catalog can advertise canonical schemas without inline bodies by default", () => {
  const response = cultNetBuiltinSchemaRegistry.createCatalogResponse({
    schemaVersion: "cultnet.schema_catalog_request.v0",
    messageId: "catalog-1",
  });

  const ghostlight = response.schemas.find((schema) => schema.documentType === "ghostlight.agent-state");
  assert.ok(ghostlight);
  assert.equal(ghostlight?.kind, "document_payload");
  assert.equal(ghostlight?.documentType, "ghostlight.agent-state");
  assert.equal(typeof ghostlight?.contentHash, "string");
  assert.equal(ghostlight?.schemaJson, undefined);

  const transportProfile = response.schemas.find((schema) => schema.schemaVersion === "cultnet.transport_profile.v0");
  assert.ok(transportProfile);
  assert.equal(transportProfile?.kind, "shared_contract");
  assert.equal(transportProfile?.schemaId, cultNetSchemas.transportProfileSchema.$id);
  assert.deepEqual(transportProfile?.wireContracts, ["cultnet.schema.v0"]);
  const shardCatalogRequest = response.schemas.find((schema) => schema.schemaVersion === "cultnet.shard_catalog_request.v0");
  assert.ok(shardCatalogRequest);
  assert.equal(shardCatalogRequest?.schemaId, cultNetSchemas.shardCatalogRequestSchema.$id);
  assert.deepEqual(shardCatalogRequest?.wireContracts, ["cultnet.schema.v0"]);
  const shardCatalogResponse = response.schemas.find((schema) => schema.schemaVersion === "cultnet.shard_catalog_response.v0");
  assert.ok(shardCatalogResponse);
  assert.equal(shardCatalogResponse?.schemaId, cultNetSchemas.shardCatalogResponseSchema.$id);
  assert.deepEqual(shardCatalogResponse?.wireContracts, ["cultnet.schema.v0"]);
  assert.equal(
    cultNetSchemas.transportProfileSchema.properties.transports.items.properties.reconnectPolicy.properties.schemaVersion.const,
    "cultnet.reconnect_policy.v0",
  );
});

test("CultNet schema catalog applies remote descriptors and notifies watchers", () => {
  const catalog = new CultNetSchemaCatalog();
  let watchedSchemaId: string | undefined;
  const unsubscribe = catalog.watch((descriptor) => {
    watchedSchemaId = descriptor.schemaId;
  });
  const response = cultNetBuiltinSchemaRegistry.createCatalogResponse({
    schemaVersion: "cultnet.schema_catalog_request.v0",
    messageId: "catalog-apply",
    includeSchemaJson: true,
    kinds: ["document_payload"],
  });

  const applied = catalog.applyResponse(response);

  assert.ok(applied.some((descriptor) => descriptor.documentType === "ghostlight.agent-state"));
  assert.equal(watchedSchemaId, applied.at(-1)?.schemaId);
  assert.equal(catalog.get(applied[0]!.schemaId)?.schemaJson, undefined);
  assert.equal(typeof catalog.get(applied[0]!.schemaId, { includeSchemaJson: true })?.schemaJson, "string");
  assert.deepEqual(
    catalog.list({ kinds: ["document_payload"] }).map((descriptor) => descriptor.kind),
    catalog.list().map((descriptor) => descriptor.kind),
  );

  unsubscribe();
});

test("CultNet peer can request and sync schema catalogs by message id", async () => {
  const { a, b } = createDuplexPair();
  const requester = createTcpFramedCultNetPeer(a, {
    runtimeId: "catalog-requester",
    wireContract: "cultnet.schema.v0",
  });
  const responder = createTcpFramedCultNetPeer(b, {
    runtimeId: "catalog-responder",
    wireContract: "cultnet.schema.v0",
  });

  responder.on("message", (message) => {
    if (message.schemaVersion === "cultnet.schema_catalog_request.v0") {
      responder.sendSchemaCatalogResponse(cultNetBuiltinSchemaRegistry.createCatalogResponse(message));
    }
  });

  const synced = new CultNetSchemaCatalog();
  const applied = await requester.syncSchemaCatalog(synced, {
    messageId: "peer-schema-catalog",
    includeSchemaJson: true,
    kinds: ["document_payload"],
    timeoutMs: 1_000,
  });

  assert.ok(applied.some((descriptor) => descriptor.documentType === "ghostlight.agent-state"));
  assert.equal(synced.get(applied[0]!.schemaId)?.schemaJson, undefined);
  assert.equal(typeof synced.get(applied[0]!.schemaId, { includeSchemaJson: true })?.schemaJson, "string");

  requester.close();
  responder.close();
});

test("CultNet shard catalog filters descriptors and answers catalog requests", () => {
  const catalog = new CultNetShardCatalog();
  let watchedShardId: string | undefined;
  const unsubscribe = catalog.watch((descriptor) => {
    watchedShardId = descriptor.shardId;
  });

  catalog.upsert({
    shardId: "notes-a",
    ownerRuntimeId: "ts-owner",
    epoch: 7,
    isPrimary: true,
    schemaIds: ["note.v1"],
    keyPrefix: "note:",
    primaryEndpoints: ["rudp://127.0.0.1:4100"],
    authorityLeaseId: "lease-a",
  });
  catalog.upsert({
    shardId: "profiles-a",
    ownerRuntimeId: "ts-owner",
    epoch: 3,
    schemaIds: ["profile.v1"],
    keyPrefix: "profile:",
  });

  assert.equal(watchedShardId, "profiles-a");
  assert.equal(shardServes(catalog.get("notes-a")!, { schemaId: "note.v1", recordKey: "note:1" }), true);
  assert.equal(shardServes(catalog.get("notes-a")!, { schemaId: "note", recordKey: "note:1" }), true);
  assert.equal(shardServes(catalog.get("notes-a")!, { schemaId: "profile.v1", recordKey: "note:1" }), false);
  assert.deepEqual(catalog.list({ schemaIds: ["note.v1"], recordKeys: ["note:1"] }).map((shard) => shard.shardId), ["notes-a"]);
  assert.deepEqual(catalog.list({ schemaIds: ["note"], recordKeys: ["note:1"] }).map((shard) => shard.shardId), ["notes-a"]);

  const response = catalog.createCatalogResponse({
    schemaVersion: "cultnet.shard_catalog_request.v0",
    messageId: "shards",
    schemaIds: ["note"],
    recordKeys: ["note:2"],
  });
  assert.equal(response.schemaVersion, "cultnet.shard_catalog_response.v0");
  assert.equal(response.shards[0]?.shardId, "notes-a");

  const parsed = parseCultNetMessage(response);
  assert.equal(parsed.schemaVersion, "cultnet.shard_catalog_response.v0");
  if (parsed.schemaVersion === "cultnet.shard_catalog_response.v0") {
    assert.equal(parsed.shards[0]?.ownerRuntimeId, "ts-owner");
  }

  unsubscribe();
});

test("CultNet peer can request and sync shard catalogs by message id", async () => {
  const { a, b } = createDuplexPair();
  const requester = createTcpFramedCultNetPeer(a, {
    runtimeId: "shard-requester",
    wireContract: "cultnet.schema.v0",
  });
  const responder = createTcpFramedCultNetPeer(b, {
    runtimeId: "shard-responder",
    wireContract: "cultnet.schema.v0",
  });
  const remote = new CultNetShardCatalog();
  remote.upsert({
    shardId: "notes-peer",
    ownerRuntimeId: "shard-responder",
    epoch: 1,
    isPrimary: true,
    schemaIds: ["note.v1"],
    keyPrefix: "note:",
    primaryEndpoints: ["rudp://127.0.0.1:4200"],
  });

  responder.on("message", (message) => {
    if (message.schemaVersion === "cultnet.shard_catalog_request.v0") {
      responder.sendShardCatalogResponse(remote.createCatalogResponse(message));
    }
  });

  const synced = new CultNetShardCatalog();
  const applied = await requester.syncShardCatalog(synced, {
    messageId: "peer-shards",
    schemaIds: ["note"],
    recordKeys: ["note:local"],
    timeoutMs: 1_000,
  });

  assert.equal(applied[0]?.shardId, "notes-peer");
  assert.equal(synced.get("notes-peer")?.primaryEndpoints?.[0], "rudp://127.0.0.1:4200");

  requester.close();
  responder.close();
});

test("CultNet schema discovery can round-trip over the legacy wire contract when schemas are requested inline", () => {
  const registry = new CultNetSchemaRegistry([
    {
      schemaId: "https://example.test/contracts/example.schema.json",
      kind: "shared_contract",
      schema: {
        $schema: "https://json-schema.org/draft/2020-12/schema",
        $id: "https://example.test/contracts/example.schema.json",
        type: "object",
        properties: {
          value: { type: "string" },
        },
        required: ["value"],
        additionalProperties: false,
      },
      title: "Example Schema",
      wireContracts: ["cultnet.schema.v0", "gamecult.networking.v0"],
    },
  ]);

  const response = registry.createCatalogResponse({
    schemaVersion: "cultnet.schema_catalog_request.v0",
    messageId: "catalog-legacy",
    includeSchemaJson: true,
  });

  const wireValue = encodeCultNetMessageForWire(response, "gamecult.networking.v0");
  const decoded = parseCultNetMessage(wireValue, "gamecult.networking.v0");
  assert.equal(decoded.schemaVersion, "cultnet.schema_catalog_response.v0");
  if (decoded.schemaVersion === "cultnet.schema_catalog_response.v0") {
    assert.equal(decoded.messageId, "catalog-legacy");
    assert.equal(decoded.schemas[0]?.schemaId, "https://example.test/contracts/example.schema.json");
    assert.match(decoded.schemas[0]?.schemaJson ?? "", /"value"/u);
  }
});

test("CultNet document registry builds snapshots and applies document puts through CultCache", async () => {
  const tempDir = mkdtempSync(join(tmpdir(), "cultnetts-"));

  try {
    const documentDefinition = defineDocumentType({
      type: "ghostlight.agent-state",
      schemaId: cultNetSchemas.ghostlightAgentStateSchema.$id,
      schemaName: "ghostlight.agent-state",
      schemaVersion: "ghostlight.agent_state.v0",
      schema: z.custom<GhostlightAgentStateDocument>((value) => {
        try {
          validateGhostlightAgentState(value);
          return true;
        } catch {
          return false;
        }
      }),
    });

    const registry = new CultNetDocumentRegistry([
      defineCultNetDocumentBinding({
        definition: documentDefinition,
        payloadSchemaVersion: "ghostlight.agent_state.v0",
      }),
    ]);

    const originStore = new SingleFileMessagePackBackingStore(join(tempDir, "origin.msgpack"));
    const targetStore = new SingleFileMessagePackBackingStore(join(tempDir, "target.msgpack"));
    const originCache = CultCache.builder()
      .withDocumentType(documentDefinition)
      .withGenericStore(originStore)
      .build();
    const targetCache = CultCache.builder()
      .withDocumentType(documentDefinition)
      .withGenericStore(targetStore)
      .build();

    const payload = validateGhostlightAgentState({
      schema_version: "ghostlight.agent_state.v0",
      world: {
        world_id: "epiphany-face",
        setting: "test harness",
        time: { label: "now" },
        canon_context: ["test"],
      },
      agents: [
        {
          agent_id: "epiphany.persona",
          identity: {
            name: "Persona",
            roles: ["public-surface"],
            origin: "test",
            public_description: "test",
          },
          canonical_state: {
            underlying_organization: {},
            stable_dispositions: {},
            behavioral_dimensions: {},
            presentation_strategy: {},
            voice_style: {},
            situational_state: {},
            values: [],
          },
          goals: [],
          memories: {
            episodic: [],
            semantic: [],
            relationship_summaries: [],
          },
          perceived_state_overlays: [],
        },
      ],
      relationships: [],
      events: [],
      scenes: [],
    });

    await originCache.put(documentDefinition, "epiphany.persona", payload);
    const snapshot = registry.createSnapshotResponse(originCache, "snapshot-1");
    await registry.applySnapshotResponse(targetCache, snapshot);

    const roundTrip = targetCache.get(documentDefinition, "epiphany.persona");
    assert.ok(roundTrip);
    assert.equal(roundTrip?.schema_version, "ghostlight.agent_state.v0");
    assert.equal(roundTrip?.agents[0]?.agent_id, "epiphany.persona");
  } finally {
    rmSync(tempDir, { recursive: true, force: true });
  }
});

test("CultNet raw replication preserves CultCache payload bytes for bit-compatible neighbors", async () => {
  const tempDir = mkdtempSync(join(tmpdir(), "cultnetts-raw-"));

  try {
    const documentDefinition = defineDocumentType({
      type: "ghostlight.agent-state",
      schemaId: cultNetSchemas.ghostlightAgentStateSchema.$id,
      schemaName: "ghostlight.agent-state",
      schemaVersion: "ghostlight.agent_state.v0",
      schema: z.custom<GhostlightAgentStateDocument>((value) => {
        try {
          validateGhostlightAgentState(value);
          return true;
        } catch {
          return false;
        }
      }),
    });

    const registry = new CultNetDocumentRegistry([
      defineCultNetDocumentBinding({
        definition: documentDefinition,
        payloadSchemaVersion: "ghostlight.agent_state.v0",
      }),
    ]);

    const originCache = CultCache.builder()
      .withDocumentType(documentDefinition)
      .withGenericStore(new SingleFileMessagePackBackingStore(join(tempDir, "origin.msgpack")))
      .build();
    const targetCache = CultCache.builder()
      .withDocumentType(documentDefinition)
      .withGenericStore(new SingleFileMessagePackBackingStore(join(tempDir, "target.msgpack")))
      .build();

    const payload = validateGhostlightAgentState({
      schema_version: "ghostlight.agent_state.v0",
      world: {
        world_id: "epiphany-face",
        setting: "test harness",
        time: { label: "now" },
        canon_context: ["test"],
      },
      agents: [
        {
          agent_id: "epiphany.persona",
          identity: {
            name: "Persona",
            roles: ["public-surface"],
            origin: "test",
            public_description: "test",
          },
          canonical_state: {
            underlying_organization: {},
            stable_dispositions: {},
            behavioral_dimensions: {},
            presentation_strategy: {},
            voice_style: {},
            situational_state: {},
            values: [],
          },
          goals: [],
          memories: {
            episodic: [],
            semantic: [],
            relationship_summaries: [],
          },
          perceived_state_overlays: [],
        },
      ],
      relationships: [],
      events: [],
      scenes: [],
    });

    await originCache.put(documentDefinition, "epiphany.persona", payload);
    const rawSnapshot = registry.createRawSnapshotResponse(originCache, "raw-snapshot-1");
    assert.equal(rawSnapshot.documents[0]?.schemaId, cultNetSchemas.ghostlightAgentStateSchema.$id);
    assert.equal(rawSnapshot.documents[0]?.recordKey, "epiphany.persona");
    await registry.applyRawSnapshotResponse(targetCache, rawSnapshot);

    const sourceEnvelope = originCache.getRequiredEnvelope(documentDefinition, "epiphany.persona");
    const targetEnvelope = targetCache.getRequiredEnvelope(documentDefinition, "epiphany.persona");
    assert.deepEqual(targetEnvelope.payload, sourceEnvelope.payload);
    assert.equal(targetCache.getRequired(documentDefinition, "epiphany.persona").schema_version, "ghostlight.agent_state.v0");
  } finally {
    rmSync(tempDir, { recursive: true, force: true });
  }
});

test("operation envelopes preserve typed service routing and payload correlation", () => {
  const request = parseCultNetMessage({
    schemaVersion: "cultnet.operation_request.v0",
    messageId: "plugin-42",
    serviceId: "sai.vn",
    operation: "project",
    payloadSchema: "gamecult.eve.plugin_abi.request.v1",
    payloadEncoding: "messagepack-base64",
    payload: "gaZzY2hlbWHEJ2dhbWVjdWx0LmV2ZS5wbHVnaW5fYWJpLnJlcXVlc3QudjE=",
    sourceRuntimeId: "eve-unity",
  });
  assert.equal(request.schemaVersion, "cultnet.operation_request.v0");
  assert.equal(request.messageId, "plugin-42");
  assert.equal(request.serviceId, "sai.vn");

  const response = parseCultNetMessage({
    schemaVersion: "cultnet.operation_response.v0",
    messageId: "plugin-42",
    serviceId: "sai.vn",
    operation: "project",
    status: "ok",
    payloadSchema: "gamecult.eve.plugin_abi.response.v1",
    payloadEncoding: "messagepack-base64",
    payload: "gaZzdGF0dXOkb2s=",
    diagnostics: [],
  });
  assert.equal(response.schemaVersion, "cultnet.operation_response.v0");
  assert.equal(response.messageId, request.messageId);
});

test("rudp session advances large fragment sets through a bounded reliable window", () => {
  const connectionId = 457;
  const sender = new CultNetRudpSession({ connectionId, initialSequence: 1, resendDelayMs: 25 });
  const receiver = new CultNetRudpSession({ connectionId, initialSequence: 100, resendDelayMs: 25 });
  sender.receive({ packetType: "accept", connectionId, sequence: 0, ack: 0, ackMask: 0, channelId: "control" });
  receiver.receive({ packetType: "accept", connectionId, sequence: 0, ack: 0, ackMask: 0, channelId: "control" });

  const fragmentCount = CULTNET_RUDP_RELIABLE_SEND_WINDOW_PACKETS + 17;
  const payload = Uint8Array.from(
    { length: fragmentCount * 8 },
    (_, index) => index % 251,
  );
  const wire = sender.sendMany("schema", payload, {
    reliable: true,
    ordered: true,
    nowMs: 1,
    maxFragmentBytes: 8,
  });
  assert.equal(wire.length, CULTNET_RUDP_RELIABLE_SEND_WINDOW_PACKETS);
  const oldestSequence = wire[0]!.sequence;
  assert.equal(sender.pendingReliableSequences.length, wire.length);
  assert.equal(sender.queuedReliablePacketCount, 17);

  const delivered = [];
  while (wire.length > 0) {
    const admitted = wire.length;
    for (let index = 0; index < admitted; index += 1) {
      const packet = wire.shift()!;
      delivered.push(...receiver.receive(packet, 2).delivered);
    }
    const acknowledged = sender.receive(receiver.createAck(), 3);
    wire.push(...(acknowledged.readyToSend ?? []));
  }

  assert.equal(sender.outstandingReliablePacketCount, 0);
  assert.equal(delivered.length, 1);
  assert.deepEqual(delivered[0]?.payload, payload);
  const oldAck = receiver.createAckForReceived(oldestSequence);
  assert.equal(oldAck.ack, oldestSequence);
  assert.equal(oldAck.ackMask, 0);
});

test("CultNet contracts encode legacy bytes without Node Buffer authority", () => {
  const originalBuffer = globalThis.Buffer;
  try {
    Object.defineProperty(globalThis, "Buffer", { configurable: true, value: undefined });
    const message = parseCultNetMessage({
      schemaVersion: "cultnet.login.v0",
      nonce: "AQID",
      auth: "BAUG",
      password: "BwgJ",
    });
    const wire = encodeCultNetMessageForWire(message, "gamecult.networking.v0") as [number, Uint8Array[]];
    assert.equal(wire[0], 0);
    assert.deepEqual([...wire[1][0]], [1, 2, 3]);
  } finally {
    Object.defineProperty(globalThis, "Buffer", { configurable: true, value: originalBuffer });
  }
});

test("database subscription contracts match the C# live document lane", () => {
  const subscribe = parseCultNetMessage({
    schemaVersion: "cultnet.database_subscribe.v0",
    messageId: "subscribe-counter",
    subscriptionId: "counter:browser-1",
    schemaIds: ["sample.counter_state.v1"],
    recordKeys: ["counter:main"],
    includeSnapshot: true,
    consumerRuntimeId: "browser-1",
  });
  assert.equal(subscribe.schemaVersion, "cultnet.database_subscribe.v0");
  assert.equal(subscribe.subscriptionId, "counter:browser-1");

  const added = parseCultNetMessage({
    schemaVersion: "cultnet.database_change_raw.v0",
    messageId: "change-counter-1",
    subscriptionId: "counter:browser-1",
    changeKind: "added",
    document: {
      schemaId: "sample.counter_state.v1",
      recordKey: "counter:main",
      storedAt: "2026-08-17T00:00:00Z",
      payloadEncoding: "messagepack",
      payload: new Uint8Array([0x81, 0xa5, 0x63, 0x6f, 0x75, 0x6e, 0x74, 0x01]),
    },
  });
  assert.equal(added.schemaVersion, "cultnet.database_change_raw.v0");
  assert.equal(added.document?.recordKey, "counter:main");

  const removed = parseCultNetMessage({
    schemaVersion: "cultnet.database_change_raw.v0",
    messageId: "change-counter-2",
    subscriptionId: "counter:browser-1",
    changeKind: "removed",
    recordKey: "counter:main",
    schemaId: "sample.counter_state.v1",
  });
  assert.equal(removed.schemaVersion, "cultnet.database_change_raw.v0");
  assert.equal(removed.recordKey, "counter:main");

  const unsubscribe = parseCultNetMessage({
    schemaVersion: "cultnet.database_unsubscribe.v0",
    messageId: "unsubscribe-counter",
    subscriptionId: "counter:browser-1",
  });
  assert.equal(unsubscribe.schemaVersion, "cultnet.database_unsubscribe.v0");

  assert.throws(
    () => parseCultNetMessage({
      schemaVersion: "cultnet.database_change_raw.v0",
      messageId: "invalid-change",
      subscriptionId: "counter:browser-1",
      changeKind: "updated",
    }),
    /document: is required/,
  );
});

test("RUDP operation service correlates typed payload envelopes", async () => {
  const server = await startCultNetOperationServer({
    runtimeId: "sai-sidecar",
    handler: request => ({
      schemaVersion: "cultnet.operation_response.v0",
      messageId: request.messageId,
      serviceId: request.serviceId,
      operation: request.operation,
      status: "ok",
      payloadSchema: "gamecult.eve.plugin_abi.response.v1",
      payloadEncoding: "messagepack-base64",
      payload: request.payload,
      diagnostics: [],
      sourceRuntimeId: "sai-sidecar",
    }),
  });
  try {
    const response = await invokeCultNetOperation(server.endpoint, {
      schemaVersion: "cultnet.operation_request.v0",
      messageId: "service-1",
      serviceId: "sai.vn",
      operation: "describe",
      payloadSchema: "gamecult.eve.plugin_abi.request.v1",
      payloadEncoding: "messagepack-base64",
      payload: "gaZzY2hlbWHEJ2dhbWVjdWx0LmV2ZS5wbHVnaW5fYWJpLnJlcXVlc3QudjE=",
    }, { runtimeId: "eve-test" });
    assert.equal(response.messageId, "service-1");
    assert.equal(response.serviceId, "sai.vn");
    assert.equal(response.status, "ok");
  } finally {
    await server.close();
  }
});

test("rudp sequence-neutral acknowledgements interoperate with ordered receivers", () => {
  const sender = new CultNetRudpSession({ connectionId: 125, initialSequence: 1 });
  const receiver = new CultNetRudpSession({ connectionId: 125, initialSequence: 100 });
  sender.receive({ packetType: "accept", connectionId: 125, sequence: 90, ack: 0, ackMask: 0, channelId: "control" });
  receiver.receive({ packetType: "accept", connectionId: 125, sequence: 91, ack: 0, ackMask: 0, channelId: "control" });

  const ack = sender.createAck();
  const request = sender.send("schema", Buffer.from("snapshot"), { reliable: true, ordered: true });

  assert.equal(ack.sequence, 0);
  assert.equal(request.sequence, 1);
  assert.deepEqual(receiver.receive(ack).delivered, []);
  assert.deepEqual(receiver.receive(request).delivered.map(frame => Buffer.from(frame.payload).toString("utf8")), ["snapshot"]);
});

test("CultNet raw replication applies compatible foreign-schema snapshots", async () => {
  const tempDir = mkdtempSync(join(tmpdir(), "cultnetts-foreign-schema-raw-"));

  try {
    const documentDefinition = defineDocumentType({
      type: "cultnet.note",
      schemaId: "cultnet.note.v1",
      schemaName: "cultnet.note",
      schemaVersion: "cultnet.note.v1",
      schema: z.object({
        schema_version: z.string(),
        noteId: z.string(),
        body: z.string(),
      }),
      name: "noteId",
    });
    const registry = new CultNetDocumentRegistry([
      defineCultNetDocumentBinding({ definition: documentDefinition }),
    ]);
    const originCache = CultCache.builder()
      .withDocumentType(documentDefinition)
      .withGenericStore(new SingleFileMessagePackBackingStore(join(tempDir, "origin.msgpack")))
      .build();
    const targetCache = CultCache.builder()
      .withDocumentType(documentDefinition)
      .withGenericStore(new SingleFileMessagePackBackingStore(join(tempDir, "target.msgpack")))
      .build();

    await originCache.put(documentDefinition, "note:foreign-schema", {
      schema_version: "cultnet.note.v1",
      noteId: "note:foreign-schema",
      body: "accepted by payload shape",
    });
    const rawSnapshot = registry.createRawSnapshotResponse(originCache, "foreign-schema-raw");
    rawSnapshot.documents[0]!.schemaId = "runtime.generated.cultnet.note.ui.99";

    await registry.applyRawSnapshotResponse(targetCache, rawSnapshot);

    assert.equal(
      targetCache.getRequired(documentDefinition, "note:foreign-schema").body,
      "accepted by payload shape",
    );
    assert.equal(
      targetCache.getRequiredEnvelope(documentDefinition, "note:foreign-schema").schemaId,
      "cultnet.note.v1",
    );
  } finally {
    rmSync(tempDir, { recursive: true, force: true });
  }
});

test("CultNet document registry filters snapshots by schema aliases", async () => {
  const tempDir = mkdtempSync(join(tmpdir(), "cultnetts-alias-snapshot-"));

  try {
    const documentDefinition = defineDocumentType({
      type: "cultnet.alias_note",
      schemaId: "sha256:cultnet-alias-note",
      schemaName: "cultnet.alias_note",
      schemaVersion: "cultnet.alias_note.v1",
      schema: z.object({
        schema_version: z.string(),
        noteId: z.string(),
        body: z.string(),
      }),
      name: "noteId",
    });
    const registry = new CultNetDocumentRegistry([
      defineCultNetDocumentBinding({ definition: documentDefinition }),
    ]);
    const cache = CultCache.builder()
      .withDocumentType(documentDefinition)
      .withGenericStore(new SingleFileMessagePackBackingStore(join(tempDir, "alias.msgpack")))
      .build();

    await cache.put(documentDefinition, "note:alias", {
      schema_version: "cultnet.alias_note.v1",
      noteId: "note:alias",
      body: "filtered by alias",
    });

    const typedSnapshot = registry.createSnapshotResponse(cache, "typed-alias", {
      schemaVersion: "cultnet.snapshot_request.v0",
      messageId: "typed-alias",
      schemaIds: ["cultnet.alias_note.v1"],
    });
    const rawSnapshot = registry.createRawSnapshotResponse(cache, "raw-alias", {
      schemaVersion: "cultnet.snapshot_request.v0",
      messageId: "raw-alias",
      schemaIds: ["cultnet.alias_note.v1"],
    });

    assert.equal(typedSnapshot.documents[0]?.schemaId, "sha256:cultnet-alias-note");
    assert.equal(typedSnapshot.documents[0]?.recordKey, "note:alias");
    assert.equal(rawSnapshot.documents[0]?.schemaId, "sha256:cultnet-alias-note");
    assert.equal(rawSnapshot.documents[0]?.recordKey, "note:alias");
  } finally {
    rmSync(tempDir, { recursive: true, force: true });
  }
});

test("CultNet document registry applies typed put and delete messages by schema alias", async () => {
  const tempDir = mkdtempSync(join(tmpdir(), "cultnetts-alias-typed-"));

  try {
    const documentDefinition = defineDocumentType({
      type: "cultnet.typed_alias_note",
      schemaId: "sha256:cultnet-typed-alias-note",
      schemaName: "cultnet.typed_alias_note",
      schemaVersion: "cultnet.typed_alias_note.v1",
      schema: z.object({
        schema_version: z.string(),
        noteId: z.string(),
        body: z.string(),
      }),
      name: "noteId",
    });
    const registry = new CultNetDocumentRegistry([
      defineCultNetDocumentBinding({ definition: documentDefinition }),
    ]);
    const cache = CultCache.builder()
      .withDocumentType(documentDefinition)
      .withGenericStore(new SingleFileMessagePackBackingStore(join(tempDir, "typed-alias.msgpack")))
      .build();

    await registry.applyDocumentPutMessage(cache, {
      schemaVersion: "cultnet.document_put.v0",
      messageId: "typed-alias-put",
      document: {
        schemaId: "cultnet.typed_alias_note.v1",
        recordKey: "note:typed-alias",
        storedAt: "2026-06-27T00:00:00.000Z",
        payload: {
          schema_version: "cultnet.typed_alias_note.v1",
          noteId: "note:typed-alias",
          body: "typed alias applied",
        },
      },
    });

    assert.equal(
      cache.getRequired(documentDefinition, "note:typed-alias").body,
      "typed alias applied",
    );
    assert.equal(registry.getBySchemaId("cultnet.typed_alias_note.v1")?.definition.type, documentDefinition.type);

    assert.equal(await registry.applyDocumentDeleteMessage(cache, {
      schemaVersion: "cultnet.document_delete.v0",
      messageId: "typed-alias-delete",
      schemaId: "cultnet.typed_alias_note.v1",
      recordKey: "note:typed-alias",
    }), true);
    assert.equal(cache.get(documentDefinition, "note:typed-alias"), undefined);
  } finally {
    rmSync(tempDir, { recursive: true, force: true });
  }
});

test("CultNet interop slot compatibility defaults missing trailing fields and rejects mismatched slots", () => {
  const note: InteropNote = {
    schemaVersion: INTEROP_SCHEMA_VERSION,
    documentId: "note:compat",
    authorRuntimeId: "compat-peer",
    title: "Compatibility",
    body: "Missing trailing fields are allowed when declared defaults cover them.",
    tags: ["compat"],
  };

  assert.deepEqual(createInteropFormatter().decode(createLegacyInteropNoteFormatter().encode(note)), {
    ...note,
    tags: [],
  });
  assert.throws(
    () => createInteropFormatter().decode(createMismatchedInteropNoteFormatter().encode(note)),
    /Expected string/u,
  );
});

test("Ghostlight contract mirror rejects nested payloads that violate the canonical schema", () => {
  assert.throws(
    () => validateGhostlightAgentState({
      schema_version: "ghostlight.agent_state.v0",
      world: {
        world_id: "ghostlight-lab",
        setting: "test",
        time: { label: "now" },
        canon_context: ["test"],
      },
      agents: [
        {
          identity: {
            name: "Persona",
            roles: ["public-surface"],
            origin: "test",
            public_description: "test",
          },
          canonical_state: {
            underlying_organization: {},
            stable_dispositions: {},
            behavioral_dimensions: {},
            presentation_strategy: {},
            voice_style: {},
            situational_state: {},
            values: [],
          },
          goals: [],
          memories: {
            episodic: [],
            semantic: [],
            relationship_summaries: [],
          },
          perceived_state_overlays: [],
        },
      ],
      relationships: [],
      events: [],
      scenes: [],
    }),
    /agent_id/u,
  );
});

test("Generated Ghostlight contracts can feed CultCacheTS directly without a Zod mirror", async () => {
  const tempDir = mkdtempSync(join(tmpdir(), "cultnetts-generated-"));

  try {
    const documentDefinition = defineDocumentType({
      type: "ghostlight.agent-state.generated",
      schema: ghostlightAgentStateGeneratedContract,
      global: true,
    });

    const store = new SingleFileMessagePackBackingStore(join(tempDir, "generated.msgpack"));
    const cache = CultCache.builder()
      .withDocumentType(documentDefinition)
      .withGenericStore(store)
      .build();

    const payload: GhostlightAgentStateShape = {
      schema_version: "ghostlight.agent_state.v0",
      world: {
        world_id: "ghostlight-lab",
        setting: "test harness",
        time: { label: "now" },
        canon_context: ["test"],
      },
      agents: [
        {
          agent_id: "void",
          identity: {
            name: "Void",
            roles: ["observer"],
            origin: "test",
            public_description: "test",
          },
          canonical_state: {
            underlying_organization: {},
            stable_dispositions: {},
            behavioral_dimensions: {},
            presentation_strategy: {},
            voice_style: {},
            situational_state: {},
            values: [],
          },
          goals: [],
          memories: {
            episodic: [],
            semantic: [],
            relationship_summaries: [],
          },
          perceived_state_overlays: [],
        },
      ],
      relationships: [],
      events: [],
      scenes: [],
    };

    await cache.putGlobal(documentDefinition, payload);
    const roundTrip = cache.getRequiredGlobal(documentDefinition);
    assert.equal(validateGhostlightAgentStateGenerated(roundTrip), true);
    assert.equal(roundTrip.schema_version, "ghostlight.agent_state.v0");
    assert.equal(roundTrip.agents[0]?.agent_id, "void");
  } finally {
    rmSync(tempDir, { recursive: true, force: true });
  }
});

test("rudp receiver evicts stranded fragment sets instead of refusing new ones", () => {
  // A fragment set is removed only on successful reassembly, so every set that
  // loses a fragment is stranded for the life of the session. Ghostlight sends
  // Heimdall fragmented envelopes (max_fragment_bytes = 2048), so on the live
  // auth path this map grew for every lost fragment and was never swept.
  const receiver = new CultNetRudpSession({ connectionId: 9, initialSequence: 1, resendDelayMs: 50 });
  receiver.receive({ packetType: "accept", connectionId: 9, sequence: 1, ack: 0, ackMask: 0, channelId: "control" });

  // 200 fragment sets that each lose their second fragment.
  for (let id = 1; id <= 200; id += 1) {
    const stranded = receiver.receive({
      packetType: "data",
      connectionId: 9,
      sequence: 100 + id,
      ack: 0,
      ackMask: 0,
      channelId: "schema",
      fragmentId: id,
      fragmentIndex: 0,
      fragmentCount: 2,
      payload: Buffer.from([id & 0xff]),
    });
    assert.equal(stranded.delivered.length, 0, "an incomplete set must not reassemble");
  }

  // Bounded rather than unbounded, and still serving.
  assert.ok(receiver.pendingFragmentSetCount <= 64, `pending ${receiver.pendingFragmentSetCount}`);
  assert.ok(receiver.fragmentSetsEvicted > 0, "nothing was evicted");

  // The receiver still completes a fresh fragmented payload, which is what
  // erroring at the cap took away: any nonzero loss killed the channel.
  const first = receiver.receive({
    packetType: "data", connectionId: 9, sequence: 900, ack: 0, ackMask: 0,
    channelId: "schema", fragmentId: 1000, fragmentIndex: 0, fragmentCount: 2,
    payload: Buffer.from("he"),
  });
  assert.equal(first.delivered.length, 0);
  const second = receiver.receive({
    packetType: "data", connectionId: 9, sequence: 901, ack: 0, ackMask: 0,
    channelId: "schema", fragmentId: 1000, fragmentIndex: 1, fragmentCount: 2,
    payload: Buffer.from("llo"),
  });
  assert.equal(second.delivered.length, 1);
  assert.equal(Buffer.from(second.delivered[0]!.payload).toString(), "hello");
});

test("rudp socket transport drops strays without an error event and keeps serving its peer", async () => {
  const serverSocket = await bindUdpSocket();
  const clientSocket = await bindUdpSocket();
  const strayHost = await bindUdpSocket();
  const connectionId = 0x10203050;
  const server = new CultNetRudpSocketTransportConnection({
    runtimeId: "rudp-server",
    socket: serverSocket,
    mode: "server",
    connectionId,
    resendPollMs: 5,
  });
  const client = new CultNetRudpSocketTransportConnection({
    runtimeId: "rudp-client",
    socket: clientSocket,
    mode: "client",
    remoteHost: "127.0.0.1",
    remotePort: udpPort(serverSocket),
    connectionId,
    resendPollMs: 5,
  });

  try {
    // No "error" listener on either side: an unhandled "error" event throws
    // and would end the process, which is what a stray must never do.
    const frames: string[] = [];
    server.on("frame", (frame) => frames.push(Buffer.from(frame.payload).toString("utf8")));
    client.connect();
    await waitFor(() => client.connected && server.connected, "RUDP socket handshake");

    const foreign = new CultNetRudpSession({ connectionId: 0x0badf10e });
    const strays = [
      Buffer.from("not a rudp packet"),
      Buffer.alloc(0),
      Buffer.from(encodeRudpPacket(foreign.createConnect(0, Buffer.from("foreign")))),
    ];
    for (const stray of strays) {
      // From an unrelated address, and from the peer's own address.
      strayHost.send(stray, udpPort(serverSocket), "127.0.0.1");
      clientSocket.send(stray, udpPort(serverSocket), "127.0.0.1");
    }
    await waitFor(() => server.stats.packetsDropped >= strays.length * 2, "strays counted");

    client.send("schema", Buffer.from("still here", "utf8"));
    await waitFor(() => frames.length === 1, "the real frame");
    assert.deepEqual(frames, ["still here"]);

    // The client side too: a moved flow answers from the server address.
    for (const stray of strays) {
      serverSocket.send(stray, udpPort(clientSocket), "127.0.0.1");
    }
    await waitFor(() => client.stats.packetsDropped >= strays.length, "client strays counted");
    const clientFrames: string[] = [];
    client.on("frame", (frame) => clientFrames.push(Buffer.from(frame.payload).toString("utf8")));
    server.send("schema", Buffer.from("server still here", "utf8"));
    await waitFor(() => clientFrames.length === 1, "the real server frame");
  } finally {
    strayHost.close();
    client.close();
    server.close();
  }
});

/** A reliable frame no session accepts: a fragment set with no fragment id. */
function poisonedFrame(session: CultNetRudpSession): CultNetRudpPacket {
  const [packet] = session.sendMany("schema", Buffer.from("poison"), { reliable: true, ordered: true, nowMs: 0 });
  return { ...packet!, fragmentCount: 2, fragmentId: 0, fragmentIndex: 0 };
}

test("rudp socket transport ends the session that refuses a reliable frame and never acknowledges it", async () => {
  const serverSocket = await bindUdpSocket();
  const peerSocket = await bindUdpSocket();
  const connectionId = 0x10203051;
  const server = new CultNetRudpSocketTransportConnection({
    runtimeId: "rudp-server",
    socket: serverSocket,
    mode: "server",
    connectionId,
    resendPollMs: 1000,
  });
  const received: CultNetRudpPacket[] = [];
  peerSocket.on("message", (wire) => received.push(decodeRudpPacket(wire)));
  const peer = new CultNetRudpSession({ connectionId });
  const toServer = (packet: CultNetRudpPacket) =>
    peerSocket.send(encodeRudpPacket(packet), udpPort(serverSocket), "127.0.0.1");
  const disconnects: string[] = [];
  server.on("disconnect", ({ reason }) => disconnects.push(Buffer.from(reason).toString("utf8")));

  try {
    toServer(peer.createConnect(0));
    await waitFor(() => received.some((p) => p.packetType === "accept"), "the Accept");
    peer.receive(received.find((p) => p.packetType === "accept")!, 0);

    const poison = poisonedFrame(peer);
    toServer(poison);
    await waitFor(() => disconnects.length === 1, "the server ending the session");
    await waitFor(() => received.some((p) => p.packetType === "disconnect"), "the goodbye");
    assert.equal(server.stats.packetsDropped, 1);
    assert.equal(server.connected, false);

    // The peer sees the session end and never sees the frame acknowledged,
    // neither in the goodbye nor after it retransmits.
    const goodbye = received.find((p) => p.packetType === "disconnect")!;
    assert.equal(peer.receive(goodbye, 1).disconnected, true);
    assert.ok(peer.pendingReliableSequences.includes(poison.sequence), "the refused frame was acknowledged");
    const before = received.length;
    toServer(poison);
    await waitFor(() => server.stats.packetsDropped === 2, "the retransmit dropped");
    await new Promise((resolve) => setTimeout(resolve, 30));
    assert.equal(received.length, before, "the server must not answer a retransmit for an ended session");

    // Only a Connect claims the endpoint again.
    const again = new CultNetRudpSession({ connectionId });
    toServer(again.createConnect(0));
    await waitFor(() => received.length > before, "the new Accept");
    assert.ok(received.slice(before).some((p) => p.packetType === "accept"));
  } finally {
    peerSocket.close();
    server.close();
  }
});

test("rudp sessions reject sequence spaces and queues that cannot admit a Connect", () => {
  assert.throws(() => new CultNetRudpSession({ connectionId: 1, initialSequence: 0xffff_ffff }), /initialSequence/);
  assert.throws(() => new CultNetRudpSession({ connectionId: 1, maxPendingReliablePackets: 0 }), /greater than zero/);
  assert.doesNotThrow(() => new CultNetRudpSession({ connectionId: 1, initialSequence: 0xffff_fffe, maxPendingReliablePackets: 1 }));
});

test("operation service drops and counts a packet its session refuses and keeps serving", async () => {
  const handler = (request: CultNetOperationRequestMessage): CultNetOperationResponseMessage => ({
    schemaVersion: "cultnet.operation_response.v0",
    messageId: request.messageId,
    serviceId: request.serviceId,
    operation: request.operation,
    status: "ok",
    payloadSchema: "gamecult.eve.plugin_abi.response.v1",
    payloadEncoding: "messagepack-base64",
    payload: request.payload,
    diagnostics: [],
    sourceRuntimeId: "sai-sidecar",
  });
  const server = await startCultNetOperationServer({ runtimeId: "sai-sidecar", handler });
  const raw = await bindUdpSocket();
  const port = Number(new URL(server.endpoint).port);
  const connectionId = 0x43554c54;
  const received: CultNetRudpPacket[] = [];
  raw.on("message", (wire) => received.push(decodeRudpPacket(wire)));
  const peer = new CultNetRudpSession({ connectionId });
  const toServer = (packet: CultNetRudpPacket) => raw.send(encodeRudpPacket(packet), port, "127.0.0.1");
  try {
    toServer(peer.createConnect(0));
    await waitFor(() => received.some((p) => p.packetType === "accept"), "the Accept");
    peer.receive(received[0]!, 0);
    toServer(poisonedFrame(peer));
    await waitFor(() => server.packetsDropped === 1, "the poisoned frame dropped");
    await waitFor(() => received.some((p) => p.packetType === "disconnect"), "the session ended");

    // A frame the session accepts but the service cannot handle rejects inside
    // the handler; that too is a dropped packet, not a dead process.
    const garbage = new CultNetRudpSession({ connectionId });
    received.length = 0;
    toServer(garbage.createConnect(0));
    await waitFor(() => received.some((p) => p.packetType === "accept"), "the second Accept");
    garbage.receive(received[0]!, 0);
    const [notMessagePack] = garbage.sendMany("schema", Buffer.from([0xc1]), { reliable: true, ordered: true, nowMs: 0 });
    toServer(notMessagePack!);
    await waitFor(() => server.packetsDropped === 2, "the unhandled frame dropped");
    // The session that could not serve it ends and the client is told, so its
    // retransmit is not acknowledged for a request that was never handled.
    await waitFor(() => received.some((p) => p.packetType === "disconnect"), "the goodbye");
    const beforeRetransmit = received.length;
    toServer(notMessagePack!);
    await waitFor(() => server.packetsDropped === 3, "the retransmit dropped");
    await new Promise((resolve) => setTimeout(resolve, 30));
    assert.equal(received.length, beforeRetransmit, "the retransmit of an unhandled request was answered");

    const response = await invokeCultNetOperation(server.endpoint, {
      schemaVersion: "cultnet.operation_request.v0",
      messageId: "after-poison",
      serviceId: "sai.vn",
      operation: "describe",
      payloadSchema: "gamecult.eve.plugin_abi.request.v1",
      payloadEncoding: "messagepack-base64",
      payload: "gaZzY2hlbWE=",
    }, { runtimeId: "eve-test" });
    assert.equal(response.messageId, "after-poison");
  } finally {
    raw.close();
    await server.close();
  }
});

test("operation service drops a Connect once its session table is full", async () => {
  const server = await startCultNetOperationServer({
    runtimeId: "sai-sidecar",
    handler: () => { throw new Error("no requests expected"); },
  });
  const port = Number(new URL(server.endpoint).port);
  const sockets: Socket[] = [];
  try {
    for (let index = 0; index < 66; index += 1) {
      const socket = await bindUdpSocket();
      sockets.push(socket);
      socket.send(encodeRudpPacket(new CultNetRudpSession({ connectionId: 0x43554c54 }).createConnect(0)), port, "127.0.0.1");
    }
    await waitFor(() => server.packetsDropped === 2, "the Connects past the cap dropped");
    await new Promise((resolve) => setTimeout(resolve, 30));
    assert.equal(server.packetsDropped, 2);
  } finally {
    for (const socket of sockets) socket.close();
    await server.close();
  }
});

test("rudp flush fails for a write the ended session forgot", async () => {
  const serverSocket = await bindUdpSocket();
  const peerSocket = await bindUdpSocket();
  const connectionId = 0x10203056;
  const server = new CultNetRudpSocketTransportConnection({
    runtimeId: "rudp-server",
    socket: serverSocket,
    mode: "server",
    connectionId,
    resendPollMs: 1000,
  });
  const received: CultNetRudpPacket[] = [];
  peerSocket.on("message", (wire) => received.push(decodeRudpPacket(wire)));
  const peer = new CultNetRudpSession({ connectionId });
  const toServer = (packet: CultNetRudpPacket) =>
    peerSocket.send(encodeRudpPacket(packet), udpPort(serverSocket), "127.0.0.1");
  try {
    toServer(peer.createConnect(0));
    await waitFor(() => received.some((p) => p.packetType === "accept"), "the Accept");
    peer.receive(received.find((p) => p.packetType === "accept")!, 0);
    server.send("schema", Buffer.from("never acknowledged"));
    toServer(poisonedFrame(peer));
    await waitFor(() => server.stats.packetsDropped === 1, "the refusal");
    await assert.rejects(server.flush(200), /ended before its reliable writes were acknowledged/);
  } finally {
    peerSocket.close();
    server.close();
  }
});

test("a Connect that repeats after its Accept was acknowledged is answered with an Ack and queues nothing", () => {
  const connectionId = 0x10203057;
  const client = new CultNetRudpSession({ connectionId });
  const server = new CultNetRudpSession({ connectionId });
  const connect = client.createConnect(0);
  const accept = server.acceptConnect(connect, 10);
  client.receive(accept, 11);
  server.receive(client.createAckForReceived(accept.sequence), 12);
  assert.equal(server.outstandingReliablePacketCount, 0);
  for (let attempt = 0; attempt < 20; attempt += 1) {
    assert.equal(server.acceptConnect(connect, 20 + attempt).packetType, "ack");
  }
  assert.equal(server.outstandingReliablePacketCount, 0);
});

test("server-mode transport and operation service owe one reliable Accept however many Connects repeat", async () => {
  const connectionId = 0x43554c54;
  const serverSocket = await bindUdpSocket();
  const transport = new CultNetRudpSocketTransportConnection({
    runtimeId: "rudp-server",
    socket: serverSocket,
    mode: "server",
    connectionId,
    resendPollMs: 1000,
  });
  const service = await startCultNetOperationServer({
    runtimeId: "sai-sidecar",
    handler: () => { throw new Error("no requests expected"); },
  });
  const raw = await bindUdpSocket();
  let received: CultNetRudpPacket[] = [];
  raw.on("message", (wire) => received.push(decodeRudpPacket(wire)));
  const settle = () => new Promise((resolve) => setTimeout(resolve, 100));
  try {
    for (const port of [udpPort(serverSocket), Number(new URL(service.endpoint).port)]) {
      const peer = new CultNetRudpSession({ connectionId });
      const connect = peer.createConnect(0);
      received = [];
      for (let attempt = 0; attempt < 20; attempt += 1) raw.send(encodeRudpPacket(connect), port, "127.0.0.1");
      await settle();
      const accepts = received.filter((p) => p.packetType === "accept");
      assert.equal(new Set(accepts.map((p) => p.sequence)).size, 1, `reliable Accepts sent by port ${port}`);

      // Once the Accept is acknowledged, a repeated Connect is answered with an Ack, never a new Accept.
      peer.receive(accepts[0]!, 0);
      raw.send(encodeRudpPacket(peer.createAckForReceived(accepts[0]!.sequence)), port, "127.0.0.1");
      await settle();
      received = [];
      for (let attempt = 0; attempt < 20; attempt += 1) raw.send(encodeRudpPacket(connect), port, "127.0.0.1");
      await settle();
      assert.equal(received.filter((p) => p.packetType === "accept").length, 0, `Accepts sent by port ${port} after the ack`);
      assert.ok(received.some((p) => p.packetType === "ack"), `an Ack from port ${port}`);
    }
    assert.equal(transport.outstandingReliablePacketCount, 0);
  } finally {
    raw.close();
    transport.close();
    await service.close();
  }
});

test("operation service frees the slots of abandoned sessions once they idle out", async () => {
  const server = await startCultNetOperationServer({
    runtimeId: "sai-sidecar",
    sessionIdleTimeoutMs: 300,
    handler: (request): CultNetOperationResponseMessage => ({
      schemaVersion: "cultnet.operation_response.v0",
      messageId: request.messageId,
      serviceId: request.serviceId,
      operation: request.operation,
      status: "ok",
      payloadSchema: "gamecult.eve.plugin_abi.response.v1",
      payloadEncoding: "messagepack-base64",
      payload: request.payload,
      diagnostics: [],
      sourceRuntimeId: "sai-sidecar",
    }),
  });
  const port = Number(new URL(server.endpoint).port);
  const sockets: Socket[] = [];
  const request: CultNetOperationRequestMessage = {
    schemaVersion: "cultnet.operation_request.v0",
    messageId: "after-abandonment",
    serviceId: "sai.vn",
    operation: "describe",
    payloadSchema: "gamecult.eve.plugin_abi.request.v1",
    payloadEncoding: "messagepack-base64",
    payload: "gaZzY2hlbWE=",
  };
  try {
    for (let index = 0; index < 64; index += 1) {
      const socket = await bindUdpSocket();
      sockets.push(socket);
      socket.send(encodeRudpPacket(new CultNetRudpSession({ connectionId: 0x43554c54 }).createConnect(0)), port, "127.0.0.1");
    }
    await new Promise((resolve) => setTimeout(resolve, 60));
    await assert.rejects(invokeCultNetOperation(server.endpoint, request, { runtimeId: "eve-test", timeoutMs: 100 }));
    await new Promise((resolve) => setTimeout(resolve, 400));
    const response = await invokeCultNetOperation(server.endpoint, request, { runtimeId: "eve-test" });
    assert.equal(response.messageId, "after-abandonment");
  } finally {
    for (const socket of sockets) socket.close();
    await server.close();
  }
});

test("server-mode transport replaces its peer when a Connect arrives from another endpoint", async () => {
  const serverSocket = await bindUdpSocket();
  const socketA = await bindUdpSocket();
  const socketB = await bindUdpSocket();
  const connectionId = 0x10203058;
  const server = new CultNetRudpSocketTransportConnection({
    runtimeId: "rudp-server",
    socket: serverSocket,
    mode: "server",
    connectionId,
    resendPollMs: 1000,
  });
  const receivedByB: CultNetRudpPacket[] = [];
  socketB.on("message", (wire) => receivedByB.push(decodeRudpPacket(wire)));
  const frames: string[] = [];
  server.on("frame", (frame: { payload: Uint8Array }) => frames.push(Buffer.from(frame.payload).toString("utf8")));
  const peerA = new CultNetRudpSession({ connectionId });
  const peerB = new CultNetRudpSession({ connectionId });
  const send = (socket: Socket, packet: CultNetRudpPacket) =>
    socket.send(encodeRudpPacket(packet), udpPort(serverSocket), "127.0.0.1");
  try {
    send(socketA, peerA.createConnect(0));
    await waitFor(() => server.connected, "A accepted");
    send(socketB, peerB.createConnect(0));
    await waitFor(() => receivedByB.some((p) => p.packetType === "accept"), "B's Accept");
    peerB.receive(receivedByB.find((p) => p.packetType === "accept")!, 0);

    // B owns the session; A's traffic is a stranger's and is dropped.
    const [fromA] = peerA.sendMany("schema", Buffer.from("from-A"), { reliable: true, ordered: true, nowMs: 0 });
    send(socketA, fromA!);
    const [fromB] = peerB.sendMany("schema", Buffer.from("from-B"), { reliable: true, ordered: true, nowMs: 0 });
    send(socketB, fromB!);
    await waitFor(() => frames.length > 0, "B's frame");
    await new Promise((resolve) => setTimeout(resolve, 30));
    assert.deepEqual(frames, ["from-B"]);
    assert.equal(server.stats.packetsDropped, 1);
  } finally {
    socketA.close();
    socketB.close();
    assert.doesNotThrow(() => server.close());
  }
});

test("operation service does not idle out a session while its handler is running", async () => {
  const respond = (request: CultNetOperationRequestMessage): CultNetOperationResponseMessage => ({
    schemaVersion: "cultnet.operation_response.v0",
    messageId: request.messageId,
    serviceId: request.serviceId,
    operation: request.operation,
    status: "ok",
    payloadSchema: "gamecult.eve.plugin_abi.response.v1",
    payloadEncoding: "messagepack-base64",
    payload: request.payload,
    diagnostics: [],
    sourceRuntimeId: "sai-sidecar",
  });
  const server = await startCultNetOperationServer({
    runtimeId: "sai-sidecar",
    sessionIdleTimeoutMs: 200,
    handler: async (request) => {
      await new Promise((resolve) => setTimeout(resolve, 700));
      return respond(request);
    },
  });
  try {
    const response = await invokeCultNetOperation(server.endpoint, {
      schemaVersion: "cultnet.operation_request.v0",
      messageId: "slow",
      serviceId: "sai.vn",
      operation: "describe",
      payloadSchema: "gamecult.eve.plugin_abi.request.v1",
      payloadEncoding: "messagepack-base64",
      payload: "gaZzY2hlbWE=",
    }, { runtimeId: "eve-test", timeoutMs: 3000 });
    assert.equal(response.messageId, "slow");
  } finally {
    await server.close();
  }
});

test("a flush is bound to the session it started in, even when a disconnect listener reconnects", async () => {
  const peerSocket = await bindUdpSocket();
  const clientSocket = await bindUdpSocket();
  const connectionId = 0x10203059;
  const client = new CultNetRudpSocketTransportConnection({
    runtimeId: "rudp-client",
    socket: clientSocket,
    mode: "client",
    remoteHost: "127.0.0.1",
    remotePort: udpPort(peerSocket),
    connectionId,
    resendDelayMs: 1000,
    resendPollMs: 1000,
  });
  const received: CultNetRudpPacket[] = [];
  let clientPort = 0;
  peerSocket.on("message", (wire, remote) => {
    clientPort = remote.port;
    received.push(decodeRudpPacket(wire));
  });
  const peer = new CultNetRudpSession({ connectionId });
  try {
    client.connect();
    await waitFor(() => received.some((p) => p.packetType === "connect"), "the Connect");
    const accept = peer.acceptConnect(received.find((p) => p.packetType === "connect")!, 0);
    peerSocket.send(encodeRudpPacket(accept), clientPort, "127.0.0.1");
    await waitFor(() => client.connected, "the client connected");
    client.send("schema", Buffer.from("never acknowledged"));
    client.on("disconnect", () => client.connect());
    const flushed = client.flush(500);
    const outcome = assert.rejects(flushed, /ended before its reliable writes were acknowledged/);
    peerSocket.send(encodeRudpPacket(peer.createDisconnect(Buffer.from("bye"))), clientPort, "127.0.0.1");
    await outcome;
  } finally {
    peerSocket.close();
    client.close();
  }
});
