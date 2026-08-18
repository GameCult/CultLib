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
} from "cultcache-ts";

import {
  CultNetClientSecurityOptions,
  CultNetDocumentRegistry,
  CultNetPeer,
  CultNetRudpReconnectLoop,
  CultNetRudpSession,
  CultNetRudpSocketTransportConnection,
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
    schemaVersion: "cultmesh.session_accepted.v1",
    messageId: "session-1",
    accepted: true,
    verseId: "sample.counter",
    authorityRuntimeId: "sample.counter-provider",
    protocolId: "cultmesh.documents.v1",
    routeGeneration: "provider-route-1",
  });
  assert.equal(session.schemaVersion, "cultmesh.session_accepted.v1");

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
