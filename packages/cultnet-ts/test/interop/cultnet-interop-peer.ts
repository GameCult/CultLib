import { createSocket, type RemoteInfo, type Socket as DgramSocket } from "node:dgram";
import { connect as connectTcp, createServer, type Server, type Socket as TcpSocket } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import process from "node:process";
import { setTimeout as delay } from "node:timers/promises";

import { decode, encode } from "@msgpack/msgpack";
import { CultCache, SingleFileMessagePackBackingStore, defineDocumentType, type AnyCultCacheDocumentDefinition } from "@gamecult/cultcache-ts";
import {
  CultNetDocumentRegistry,
  CultNetPeer,
  CultNetRudpPacket,
  CultNetRudpSession,
  CultNetRudpSocketTransportConnection,
  CultNetSchemaRegistry,
  TcpFramedTransportConnection,
  cultNetBuiltinSchemaRegistry,
  createRudpTransportProfile,
  createTcpFramedTransportProfile,
  decodeRudpPacket,
  defineCultNetDocumentBinding,
  encodeCultNetMessageForWire,
  encodeRudpPacket,
  parseCultNetMessage,
  type CultNetDocumentBinding,
  type CultNetDocumentPutRawMessage,
  type CultNetHelloMessage,
  type CultNetMessage,
  type CultNetSchemaCatalogRequestMessage,
  type CultNetSchemaCatalogResponseMessage,
  type CultNetSnapshotRequestMessage,
  type CultNetSnapshotResponseRawMessage,
} from "../../src";
import {
  DISCOVERY_ANNOUNCE_SCHEMA_VERSION,
  DISCOVERY_PROBE_SCHEMA_VERSION,
  INTEROP_DOCUMENT_TYPE,
  INTEROP_FIRE_COMMAND_DOCUMENT_TYPE,
  INTEROP_FIRE_COMMAND_SCHEMA_ID,
  INTEROP_FIRE_COMMAND_SCHEMA_VERSION,
  INTEROP_FIRE_RECEIPT_DOCUMENT_TYPE,
  INTEROP_FIRE_RECEIPT_SCHEMA_ID,
  INTEROP_FIRE_RECEIPT_SCHEMA_VERSION,
  INTEROP_MUTATION_INTENT_DOCUMENT_TYPE,
  INTEROP_MUTATION_INTENT_SCHEMA_ID,
  INTEROP_MUTATION_INTENT_SCHEMA_VERSION,
  INTEROP_MUTATION_RECEIPT_DOCUMENT_TYPE,
  INTEROP_MUTATION_RECEIPT_SCHEMA_ID,
  INTEROP_MUTATION_RECEIPT_SCHEMA_VERSION,
  INTEROP_SCHEMA_VERSION,
  INTEROP_WIRE_CONTRACT,
  buildInteropNote,
  createInteropFireCommandFormatter,
  createInteropFireReceiptFormatter,
  createInteropFormatter,
  createInteropMutationIntentFormatter,
  createInteropMutationReceiptFormatter,
  discoveryAnnounceSchema,
  discoveryProbeSchema,
  interopFireCommandSchema,
  interopFireReceiptSchema,
  interopMutationIntentSchema,
  interopMutationReceiptSchema,
  interopNoteSchema,
  loadInteropSchemaRegistration,
  optionalIntArg,
  parseArgs,
  requireArg,
  type DiscoveryAnnounce,
  type DiscoveryProbe,
  type InteropFireCommand,
  type InteropFireReceipt,
  type InteropMutationIntent,
  type InteropMutationReceipt,
  type InteropNote,
} from "./cultnet-interop-shared";

const RUDP_CONNECTION_ID = 0x43554c54;
const textEncoder = new TextEncoder();

async function main(): Promise<void> {
  const [mode, ...rest] = process.argv.slice(2);
  if (!mode) {
    throw new Error("Expected mode: serve | probe | dial");
  }

  const args = parseArgs(rest);

  switch (mode) {
    case "serve":
      await serve(args);
      return;
    case "probe":
      await probe(args);
      return;
    case "dial":
      await dial(args);
      return;
    default:
      throw new Error(`Unknown mode ${mode}`);
  }
}

async function serve(args: Map<string, string>): Promise<void> {
  const runtimeId = requireArg(args, "runtime-id");
  const runtimeKind = requireArg(args, "runtime-kind");
  const displayName = requireArg(args, "display-name");
  const agentId = requireArg(args, "agent-id");
  const advertiseHost = requireArg(args, "advertise-host");
  const bindHost = args.get("bind-host") ?? "127.0.0.1";
  const tcpPort = optionalIntArg(args, "tcp-port");
  const rudpPort = optionalIntArg(args, "rudp-port", tcpPort ?? undefined);
  const discoveryPort = optionalIntArg(args, "discovery-port");
  const discoveryGroup = requireArg(args, "discovery-group");
  const schemaPath = requireArg(args, "schema-path");

  if (!tcpPort || !rudpPort || !discoveryPort) {
    throw new Error("serve mode requires --tcp-port, --rudp-port, and --discovery-port");
  }

  const interopSchema = await loadInteropSchemaRegistration(schemaPath);
  const documents = defineInteropDocuments(interopSchema.schemaId);
  const cache = CultCache.builder()
    .withDocumentType(documents.note.definition)
    .withDocumentType(documents.mutationIntent.definition)
    .withDocumentType(documents.mutationReceipt.definition)
    .withDocumentType(documents.fireCommand.definition)
    .withDocumentType(documents.fireReceipt.definition)
    .withGenericStore(new SingleFileMessagePackBackingStore(runtimeStorePath(runtimeId)))
    .build();
  const documentRegistry = new CultNetDocumentRegistry(Object.values(documents));
  const customSchemas = new CultNetSchemaRegistry([
    {
      schemaId: interopSchema.schemaId,
      kind: "document_payload",
      schema: interopSchema.schema,
      schemaVersion: INTEROP_SCHEMA_VERSION,
      documentType: INTEROP_DOCUMENT_TYPE,
      title: interopSchema.title,
      wireContracts: [INTEROP_WIRE_CONTRACT],
    },
  ]);

  await cache.put(documents.note.definition, `note:${runtimeId}`, buildInteropNote(runtimeId, displayName));

  const tcpServer = createServer();
  tcpServer.on("connection", (socket) => {
    const transport = new TcpFramedTransportConnection(
      socket,
      createTcpFramedTransportProfile(runtimeId, {
        transportId: "interop-tcp",
        host: advertiseHost,
        port: tcpPort,
      }),
    );
    const peer = new CultNetPeer(transport, { wireContract: INTEROP_WIRE_CONTRACT });
    peer.on("invalidMessage", (error) => writeLog("invalidMessage", { runtimeId, error: error.message }));
    peer.on("error", (error) => writeLog("tcpError", { runtimeId, error: error.message }));
    peer.on("message", async (message) => {
      writeTrace("serveMessage", { runtimeId, schemaVersion: message.schemaVersion });
      try {
        await handleServerMessage({
          peer,
          message,
          runtimeId,
          runtimeKind,
          displayName,
          agentId,
          advertiseHost,
          tcpPort,
          rudpPort,
          cache,
          documentRegistry,
          customSchemas,
          noteSchemaId: interopSchema.schemaId,
        });
      } catch (error) {
        writeLog("serveMessageError", {
          runtimeId,
          error: error instanceof Error ? error.message : String(error),
        });
      }
    });
  });

  const udpSocket = createSocket({ type: "udp4", reuseAddr: true });
  udpSocket.on("error", (error) => writeLog("udpError", { runtimeId, error: error.message }));
  udpSocket.on("message", (packet, remote) => {
    try {
      const probeMessage = discoveryProbeSchema.parse(decode(packet));
      void respondToProbe({
        socket: udpSocket,
        remote,
        probeMessage,
        runtimeId,
        runtimeKind,
        displayName,
        agentId,
        advertiseHost,
        tcpPort,
        rudpPort,
      });
    } catch {
      // ignore unrelated multicast noise instead of becoming dramatic about it
    }
  });

  const rudpSocket = await startRudpInteropServer({
    runtimeId,
    runtimeKind,
    displayName,
    agentId,
    advertiseHost,
    tcpPort,
    rudpPort,
    bindHost,
    cache,
    documentRegistry,
    customSchemas,
    noteSchemaId: interopSchema.schemaId,
  });

  await Promise.all([
    new Promise<void>((resolve, reject) => {
      tcpServer.once("error", reject);
      tcpServer.listen(tcpPort, bindHost, () => resolve());
    }),
    new Promise<void>((resolve, reject) => {
      udpSocket.once("error", reject);
      udpSocket.bind(discoveryPort, "0.0.0.0", () => {
        udpSocket.addMembership(discoveryGroup);
        udpSocket.setMulticastTTL(1);
        udpSocket.setMulticastLoopback(true);
        resolve();
      });
    }),
  ]);

  writeJsonLine({
    status: "ready",
    mode: "serve",
    runtimeId,
    runtimeKind,
    tcpPort,
    rudpPort,
    discoveryPort,
    discoveryGroup,
  });

  await waitForTermination(async () => {
    rudpSocket.close();
    udpSocket.close();
    await closeServer(tcpServer);
  });
}

interface SchemaPeer {
  send(message: CultNetMessage): void;
  sendHello(message: CultNetHelloMessage): void;
  sendSchemaCatalogResponse(message: CultNetSchemaCatalogResponseMessage): void;
}

async function handleServerMessage(input: {
  peer: SchemaPeer;
  message: CultNetMessage;
  runtimeId: string;
  runtimeKind: string;
  displayName: string;
  agentId: string;
  advertiseHost: string;
  tcpPort: number;
  rudpPort: number;
  cache: CultCache;
  documentRegistry: CultNetDocumentRegistry;
  customSchemas: CultNetSchemaRegistry;
  noteSchemaId: string;
}): Promise<void> {
  const {
    peer,
    message,
    runtimeId,
    runtimeKind,
    displayName,
    agentId,
    advertiseHost,
    tcpPort,
    rudpPort,
    cache,
    documentRegistry,
    customSchemas,
    noteSchemaId,
  } = input;

  switch (message.schemaVersion) {
    case "cultnet.hello.v0":
      peer.sendHello({
        schemaVersion: "cultnet.hello.v0",
        runtimeId,
        runtimeKind,
        agentId,
        displayName,
        supportedDocumentTypes: [INTEROP_DOCUMENT_TYPE],
        supportedMutationContracts: [{
          documentType: INTEROP_DOCUMENT_TYPE,
          payloadSchemaVersion: INTEROP_SCHEMA_VERSION,
          operations: ["snapshot", "documentPut", "intentSubmit", "receiptWatch"],
          authority: "runtime",
          intentDocumentTypes: [INTEROP_MUTATION_INTENT_DOCUMENT_TYPE, INTEROP_FIRE_COMMAND_DOCUMENT_TYPE],
          receiptDocumentTypes: [INTEROP_MUTATION_RECEIPT_DOCUMENT_TYPE, INTEROP_FIRE_RECEIPT_DOCUMENT_TYPE],
        }],
        supportedMessageVersions: [INTEROP_SCHEMA_VERSION],
        transportProfiles: [createTcpFramedTransportProfile(runtimeId, {
          transportId: "interop-tcp",
          host: advertiseHost,
          port: tcpPort,
        }), createRudpTransportProfile(runtimeId, {
          transportId: "interop-rudp",
          host: advertiseHost,
          port: rudpPort,
        })],
        supportsSchemaCatalog: true,
      });
      break;
    case "cultnet.schema_catalog_request.v0":
      peer.sendSchemaCatalogResponse(createCatalogResponse(customSchemas, message));
      break;
    case "cultnet.snapshot_request.v0":
      peer.send(documentRegistry.createRawSnapshotResponse(cache, message.messageId, message));
      break;
    case "cultnet.document_put_raw.v0":
      await handleRawPut({
        peer,
        message,
        runtimeId,
        agentId,
        cache,
        documentRegistry,
        noteSchemaId,
      });
      break;
    default:
      break;
  }
}

async function handleRawPut(input: {
  peer: Pick<SchemaPeer, "send">;
  message: CultNetDocumentPutRawMessage;
  runtimeId: string;
  agentId: string;
  cache: CultCache;
  documentRegistry: CultNetDocumentRegistry;
  noteSchemaId: string;
}): Promise<void> {
  const { peer, message, runtimeId, agentId, cache, documentRegistry, noteSchemaId } = input;
  const applied = await documentRegistry.applyRawDocumentPutMessage(cache, message);
  if (message.document.schemaId === INTEROP_MUTATION_INTENT_SCHEMA_ID) {
    const intent = applied as InteropMutationIntent;
    const note = cache.getRequired(
      documentRegistry.getBySchemaId(noteSchemaId)?.definition ?? raise("missing note binding"),
      intent.targetDocumentId,
    ) as InteropNote;
    const mutated: InteropNote = {
      ...note,
      body: `${note.body}${intent.appendBody}`,
      tags: [...note.tags, intent.appendTag],
    };
    await cache.put(documentRegistry.getBySchemaId(noteSchemaId)?.definition ?? raise("missing note binding"), mutated.documentId, mutated);
    const receipt: InteropMutationReceipt = {
      schemaVersion: INTEROP_MUTATION_RECEIPT_SCHEMA_VERSION,
      intentId: intent.intentId,
      accepted: true,
      documentId: mutated.documentId,
      body: mutated.body,
      tags: mutated.tags,
    };
    const receiptBinding = documentRegistry.getBySchemaId(INTEROP_MUTATION_RECEIPT_SCHEMA_ID) ?? raise("missing mutation receipt binding");
    const noteBinding = documentRegistry.getBySchemaId(noteSchemaId) ?? raise("missing note binding");
    peer.send(documentRegistry.createRawDocumentPutMessage(receiptBinding, `${runtimeId}-mutation-receipt`, receipt.intentId, receipt, {
      sourceRuntimeId: runtimeId,
      sourceAgentId: agentId,
      sourceRole: "peer",
      tags: ["mutation", runtimeId],
    }));
    peer.send(documentRegistry.createRawDocumentPutMessage(noteBinding, `${runtimeId}-mutated-note`, mutated.documentId, mutated, {
      sourceRuntimeId: runtimeId,
      sourceAgentId: agentId,
      sourceRole: "peer",
      tags: ["mutation", runtimeId],
    }));
    return;
  }

  if (message.document.schemaId === INTEROP_FIRE_COMMAND_SCHEMA_ID) {
    const command = applied as InteropFireCommand;
    const receipt: InteropFireReceipt = {
      schemaVersion: INTEROP_FIRE_RECEIPT_SCHEMA_VERSION,
      commandId: command.commandId,
      accepted: true,
      characterId: command.characterId,
      weaponId: command.weaponId,
      shotsFired: 1,
      ammoRemaining: 29,
    };
    const receiptBinding = documentRegistry.getBySchemaId(INTEROP_FIRE_RECEIPT_SCHEMA_ID) ?? raise("missing fire receipt binding");
    peer.send(documentRegistry.createRawDocumentPutMessage(receiptBinding, `${runtimeId}-fire-receipt`, receipt.commandId, receipt, {
      sourceRuntimeId: runtimeId,
      sourceAgentId: agentId,
      sourceRole: "peer",
      tags: ["side-effect", runtimeId],
    }));
  }
}

interface RudpInteropServerOptions {
  runtimeId: string;
  runtimeKind: string;
  displayName: string;
  agentId: string;
  advertiseHost: string;
  tcpPort: number;
  rudpPort: number;
  bindHost: string;
  cache: CultCache;
  documentRegistry: CultNetDocumentRegistry;
  customSchemas: CultNetSchemaRegistry;
  noteSchemaId: string;
}

interface RudpInteropRemote {
  session: CultNetRudpSession;
}

async function startRudpInteropServer(options: RudpInteropServerOptions): Promise<DgramSocket> {
  const socket = createSocket("udp4");
  const remotes = new Map<string, RudpInteropRemote>();
  socket.on("error", (error) => writeLog("rudpError", { runtimeId: options.runtimeId, error: error.message }));
  socket.on("message", (wire, remote) => {
    void handleRudpDatagram(socket, remotes, options, wire, remote);
  });

  await new Promise<void>((resolve, reject) => {
    socket.once("error", reject);
    socket.bind(options.rudpPort, options.bindHost, () => resolve());
  });

  const resendTimer = setInterval(() => {
    for (const [remoteKey, peer] of remotes) {
      const [host, portValue] = remoteKey.split(":");
      const port = Number(portValue);
      for (const packet of peer.session.dueResends(Date.now())) {
        sendRudpPacket(socket, host ?? "", port, packet);
      }
    }
  }, 25);
  resendTimer.unref?.();
  socket.once("close", () => clearInterval(resendTimer));
  return socket;
}

async function handleRudpDatagram(
  socket: DgramSocket,
  remotes: Map<string, RudpInteropRemote>,
  options: RudpInteropServerOptions,
  wire: Buffer,
  remote: RemoteInfo,
): Promise<void> {
  let packet: CultNetRudpPacket;
  try {
    packet = decodeRudpPacket(wire);
  } catch {
    return;
  }
  if (packet.connectionId !== RUDP_CONNECTION_ID) {
    return;
  }

  const remoteKey = `${remote.address}:${remote.port}`;
  let peer = remotes.get(remoteKey);
  if (packet.packetType === "connect") {
    peer = {
      session: new CultNetRudpSession({
        connectionId: RUDP_CONNECTION_ID,
        resendDelayMs: 25,
      }),
    };
    remotes.set(remoteKey, peer);
    sendRudpPacket(socket, remote.address, remote.port, peer.session.acceptConnect(packet, Date.now(), encodeText("cultnet-interop-rudp")));
    return;
  }
  if (!peer) {
    return;
  }

  try {
    const result = peer.session.receive(packet, Date.now());
    if (result.reply) {
      sendRudpPacket(socket, remote.address, remote.port, result.reply);
    }
    if (result.disconnected) {
      remotes.delete(remoteKey);
      return;
    }
    const schemaPeer = new RudpSchemaPeer(socket, remote, peer.session);
    for (const frame of result.delivered) {
      if (frame.channelId !== "schema") {
        continue;
      }
      const message = parseCultNetMessage(decode(frame.payload), INTEROP_WIRE_CONTRACT);
      await handleServerMessage({
        peer: schemaPeer,
        message,
        runtimeId: options.runtimeId,
        runtimeKind: options.runtimeKind,
        displayName: options.displayName,
        agentId: options.agentId,
        advertiseHost: options.advertiseHost,
        tcpPort: options.tcpPort,
        rudpPort: options.rudpPort,
        cache: options.cache,
        documentRegistry: options.documentRegistry,
        customSchemas: options.customSchemas,
        noteSchemaId: options.noteSchemaId,
      });
    }
    if (packet.packetType === "data" || result.delivered.length > 0) {
      sendRudpPacket(socket, remote.address, remote.port, peer.session.createAck());
    }
  } catch (error) {
    writeLog("rudpMessageError", {
      runtimeId: options.runtimeId,
      error: error instanceof Error ? error.message : String(error),
    });
  }
}

class RudpSchemaPeer implements SchemaPeer {
  constructor(
    private readonly socket: DgramSocket,
    private readonly remote: RemoteInfo,
    private readonly session: CultNetRudpSession,
  ) {}

  send(message: CultNetMessage): void {
    const payload = encode(encodeCultNetMessageForWire(message, INTEROP_WIRE_CONTRACT));
    for (const packet of this.session.sendMany("schema", payload, {
      reliable: true,
      ordered: true,
      nowMs: Date.now(),
    })) {
      sendRudpPacket(this.socket, this.remote.address, this.remote.port, packet);
    }
  }

  sendHello(message: CultNetHelloMessage): void {
    this.send(message);
  }

  sendSchemaCatalogResponse(message: CultNetSchemaCatalogResponseMessage): void {
    this.send(message);
  }
}

function createCatalogResponse(
  customSchemas: CultNetSchemaRegistry,
  request: CultNetSchemaCatalogRequestMessage,
): CultNetSchemaCatalogResponseMessage {
  const builtIn = cultNetBuiltinSchemaRegistry.list({
    includeSchemaJson: request.includeSchemaJson,
    schemaIds: request.schemaIds,
    kinds: request.kinds,
  });
  const custom = customSchemas.list({
    includeSchemaJson: request.includeSchemaJson,
    schemaIds: request.schemaIds,
    kinds: request.kinds,
  });
  const schemas = [...builtIn];
  for (const entry of custom) {
    if (!schemas.some((candidate) => candidate.schemaId === entry.schemaId)) {
      schemas.push(entry);
    }
  }

  return {
    schemaVersion: "cultnet.schema_catalog_response.v0",
    messageId: request.messageId,
    schemas,
  };
}

async function respondToProbe(input: {
  socket: DgramSocket;
  remote: RemoteInfo;
  probeMessage: DiscoveryProbe;
  runtimeId: string;
  runtimeKind: string;
  displayName: string;
  agentId: string;
  advertiseHost: string;
  tcpPort: number;
  rudpPort: number;
}): Promise<void> {
  const {
    socket,
    remote,
    probeMessage,
    runtimeId,
    runtimeKind,
    displayName,
    agentId,
    advertiseHost,
    tcpPort,
    rudpPort,
  } = input;

  const announceMessage: DiscoveryAnnounce = {
    schemaVersion: DISCOVERY_ANNOUNCE_SCHEMA_VERSION,
    messageId: probeMessage.messageId,
    runtimeId,
    runtimeKind,
    displayName,
    agentId,
    tcpHost: advertiseHost,
    tcpPort,
    wireContract: INTEROP_WIRE_CONTRACT,
    supportedDocumentTypes: [INTEROP_DOCUMENT_TYPE],
    transportProfiles: [createTcpFramedTransportProfile(runtimeId, {
      transportId: "interop-tcp",
      host: advertiseHost,
      port: tcpPort,
    }), createRudpTransportProfile(runtimeId, {
      transportId: "interop-rudp",
      host: advertiseHost,
      port: rudpPort,
    })],
    supportsSchemaCatalog: true,
  };

  await new Promise<void>((resolve, reject) => {
    socket.send(encode(announceMessage), remote.port, remote.address, (error) => {
      if (error) {
        reject(error);
        return;
      }

      resolve();
    });
  });
}

async function probe(args: Map<string, string>): Promise<void> {
  const runtimeId = requireArg(args, "runtime-id");
  const discoveryPort = optionalIntArg(args, "discovery-port");
  const discoveryGroup = requireArg(args, "discovery-group");
  const timeoutMs = optionalIntArg(args, "timeout-ms", 1500) ?? 1500;

  if (!discoveryPort) {
    throw new Error("probe mode requires --discovery-port");
  }

  const socket = createSocket({ type: "udp4", reuseAddr: true });
  const messageId = `${runtimeId}-${Date.now()}`;
  const found = new Map<string, DiscoveryAnnounce>();

  socket.on("message", (packet) => {
    try {
      const announce = discoveryAnnounceSchema.parse(decode(packet));
      if (announce.messageId === messageId) {
        found.set(announce.runtimeId, announce);
      }
    } catch {
      // ignore unrelated packets
    }
  });

  await new Promise<void>((resolve, reject) => {
    socket.once("error", reject);
    socket.bind(0, "0.0.0.0", () => {
      socket.setMulticastTTL(1);
      socket.setMulticastLoopback(true);
      resolve();
    });
  });

  const probeMessage: DiscoveryProbe = {
    schemaVersion: DISCOVERY_PROBE_SCHEMA_VERSION,
    messageId,
    requesterRuntimeId: runtimeId,
  };

  await new Promise<void>((resolve, reject) => {
    socket.send(encode(probeMessage), discoveryPort, discoveryGroup, (error) => {
      if (error) {
        reject(error);
        return;
      }

      resolve();
    });
  });

  await delay(timeoutMs);
  socket.close();

  writeJsonLine({
    mode: "probe",
    runtimeId,
    peers: [...found.values()].sort((left, right) => left.runtimeId.localeCompare(right.runtimeId)),
  });
}

async function dial(args: Map<string, string>): Promise<void> {
  const runtimeId = requireArg(args, "runtime-id");
  const runtimeKind = requireArg(args, "runtime-kind");
  const displayName = requireArg(args, "display-name");
  const agentId = requireArg(args, "agent-id");
  const targetHost = requireArg(args, "target-host");
  const targetPort = optionalIntArg(args, "target-port");
  const targetRudpPort = optionalIntArg(args, "target-rudp-port");
  const schemaPath = requireArg(args, "schema-path");
  const timeoutMs = optionalIntArg(args, "timeout-ms", 4000) ?? 4000;

  if (!targetPort && !targetRudpPort) {
    throw new Error("dial mode requires --target-port or --target-rudp-port");
  }

  const interopSchema = await loadInteropSchemaRegistration(schemaPath);
  const documents = defineInteropDocuments(interopSchema.schemaId);
  const cache = CultCache.builder()
    .withDocumentType(documents.note.definition)
    .withDocumentType(documents.mutationIntent.definition)
    .withDocumentType(documents.mutationReceipt.definition)
    .withDocumentType(documents.fireCommand.definition)
    .withDocumentType(documents.fireReceipt.definition)
    .withGenericStore(new SingleFileMessagePackBackingStore(runtimeStorePath(`${runtimeId}-dial`)))
    .build();
  const documentRegistry = new CultNetDocumentRegistry(Object.values(documents));

  const peer = targetRudpPort
    ? await connectRudpPeer(runtimeId, targetHost, targetRudpPort, timeoutMs)
    : await connectTcpPeer(runtimeId, targetHost, targetPort ?? raise("missing TCP target port"));

  writeTrace("dialSendHello", { runtimeId });
  const remoteHelloWait = waitForMessage(peer, (message) => message.schemaVersion === "cultnet.hello.v0", timeoutMs);
  peer.sendHello({
    schemaVersion: "cultnet.hello.v0",
    runtimeId,
    runtimeKind,
    agentId,
    displayName,
    supportedDocumentTypes: [INTEROP_DOCUMENT_TYPE],
    supportedMutationContracts: [{
      documentType: INTEROP_DOCUMENT_TYPE,
      payloadSchemaVersion: INTEROP_SCHEMA_VERSION,
      operations: ["snapshot", "documentPut", "intentSubmit", "receiptWatch"],
      authority: "runtime",
      intentDocumentTypes: [INTEROP_MUTATION_INTENT_DOCUMENT_TYPE, INTEROP_FIRE_COMMAND_DOCUMENT_TYPE],
      receiptDocumentTypes: [INTEROP_MUTATION_RECEIPT_DOCUMENT_TYPE, INTEROP_FIRE_RECEIPT_DOCUMENT_TYPE],
    }],
    supportedMessageVersions: [INTEROP_SCHEMA_VERSION],
    supportsSchemaCatalog: true,
  });
  const remoteHello = await remoteHelloWait;
  writeTrace("dialReceivedHello", { runtimeId, remoteRuntimeId: remoteHello.runtimeId });

  const catalogRequest: CultNetSchemaCatalogRequestMessage = {
    schemaVersion: "cultnet.schema_catalog_request.v0",
    messageId: `${runtimeId}-catalog`,
    includeSchemaJson: true,
  };
  const catalogResponseWait = waitForMessage(peer, (message) => message.schemaVersion === "cultnet.schema_catalog_response.v0", timeoutMs);
  writeTrace("dialSendCatalogRequest", { runtimeId });
  peer.sendSchemaCatalogRequest(catalogRequest);
  const catalogResponse = await catalogResponseWait;
  writeTrace("dialReceivedCatalogResponse", { runtimeId });

  const snapshotRequest: CultNetSnapshotRequestMessage = {
    schemaVersion: "cultnet.snapshot_request.v0",
    messageId: `${runtimeId}-snapshot`,
    schemaIds: [interopSchema.schemaId],
  };
  const snapshotResponseWait = waitForMessage(peer, (message) => message.schemaVersion === "cultnet.snapshot_response_raw.v0", timeoutMs);
  writeTrace("dialSendSnapshotRequest", { runtimeId });
  peer.sendSnapshotRequest(snapshotRequest);
  const snapshotResponse = await snapshotResponseWait;
  writeTrace("dialReceivedSnapshotResponse", { runtimeId });

  await documentRegistry.applyRawSnapshotResponse(cache, snapshotResponse as CultNetSnapshotResponseRawMessage);
  const note = cache.getRequired(documents.note.definition, `note:${(remoteHello as { runtimeId: string }).runtimeId}`) as InteropNote;
  const hasInteropSchema = (catalogResponse as CultNetSchemaCatalogResponseMessage).schemas.some(
    (schema) => schema.schemaId === interopSchema.schemaId && schema.documentType === INTEROP_DOCUMENT_TYPE,
  );

  const mutationIntent: InteropMutationIntent = {
    schemaVersion: INTEROP_MUTATION_INTENT_SCHEMA_VERSION,
    intentId: `${runtimeId}-decorate`,
    targetDocumentId: note.documentId,
    appendBody: ` Decorated by ${runtimeId}.`,
    appendTag: `decorated:${runtimeId}`,
  };
  const mutationReceiptWait = waitForMessage(peer, isRawDocumentPutFor(INTEROP_MUTATION_RECEIPT_SCHEMA_ID), timeoutMs);
  const mutatedNoteWait = waitForMessage(peer, isRawDocumentPutFor(interopSchema.schemaId), timeoutMs);
  peer.send(documentRegistry.createRawDocumentPutMessage(
    documents.mutationIntent,
    `${runtimeId}-decorate-put`,
    mutationIntent.intentId,
    mutationIntent,
  ));
  const mutationReceiptMessage = await mutationReceiptWait;
  const mutationReceipt = await documentRegistry.applyRawDocumentPutMessage(cache, mutationReceiptMessage) as InteropMutationReceipt;
  const mutatedNoteMessage = await mutatedNoteWait;
  await documentRegistry.applyRawDocumentPutMessage(cache, mutatedNoteMessage);
  const mutatedNote = cache.getRequired(documents.note.definition, note.documentId) as InteropNote;

  const fireCommand: InteropFireCommand = {
    schemaVersion: INTEROP_FIRE_COMMAND_SCHEMA_VERSION,
    commandId: `${runtimeId}-fire`,
    characterId: remoteHello.runtimeId,
    weaponId: "interop-rifle",
  };
  const fireReceiptWait = waitForMessage(peer, isRawDocumentPutFor(INTEROP_FIRE_RECEIPT_SCHEMA_ID), timeoutMs);
  peer.send(documentRegistry.createRawDocumentPutMessage(
    documents.fireCommand,
    `${runtimeId}-fire-put`,
    fireCommand.commandId,
    fireCommand,
  ));
  const fireReceiptMessage = await fireReceiptWait;
  const fireReceipt = await documentRegistry.applyRawDocumentPutMessage(cache, fireReceiptMessage) as InteropFireReceipt;

  peer.close();

  writeJsonLine({
    mode: "dial",
    runtimeId,
    targetHost,
    targetPort: targetRudpPort ?? targetPort,
    transport: targetRudpPort ? "rudp" : "tcp_framed",
    remoteHello,
    hasInteropSchema,
    retrievedNote: note,
    mutatedNote,
    mutationReceipt,
    fireReceipt,
  });
}

function waitForMessage<TMessage extends CultNetMessage>(
  peer: CultNetPeer,
  predicate: (message: CultNetMessage) => message is TMessage,
  timeoutMs: number,
): Promise<TMessage> {
  return new Promise<TMessage>((resolve, reject) => {
    const timeout = setTimeout(() => {
      cleanup();
      reject(new Error(`Timed out waiting for CultNet message after ${timeoutMs}ms.`));
    }, timeoutMs);

    const onMessage = (message: CultNetMessage) => {
      if (!predicate(message)) {
        return;
      }

      cleanup();
      resolve(message);
    };

    const onInvalid = (error: Error) => {
      cleanup();
      reject(error);
    };

    const cleanup = () => {
      clearTimeout(timeout);
      peer.off("message", onMessage);
      peer.off("invalidMessage", onInvalid);
    };

    peer.on("message", onMessage);
    peer.on("invalidMessage", onInvalid);
  });
}

function isRawDocumentPutFor(schemaId: string): (message: CultNetMessage) => message is CultNetDocumentPutRawMessage {
  return (message: CultNetMessage): message is CultNetDocumentPutRawMessage =>
    message.schemaVersion === "cultnet.document_put_raw.v0" && message.document.schemaId === schemaId;
}

function raise(message: string): never {
  throw new Error(message);
}

function defineInteropDocuments(noteSchemaId: string): Record<string, CultNetDocumentBinding<AnyCultCacheDocumentDefinition>> {
  const note = defineDocumentType({
    type: INTEROP_DOCUMENT_TYPE,
    schemaId: noteSchemaId,
    schemaName: INTEROP_DOCUMENT_TYPE,
    schemaVersion: INTEROP_SCHEMA_VERSION,
    schema: interopNoteSchema,
    formatter: createInteropFormatter(),
  });
  const mutationIntent = defineDocumentType({
    type: INTEROP_MUTATION_INTENT_DOCUMENT_TYPE,
    schemaId: INTEROP_MUTATION_INTENT_SCHEMA_ID,
    schemaName: INTEROP_MUTATION_INTENT_DOCUMENT_TYPE,
    schemaVersion: INTEROP_MUTATION_INTENT_SCHEMA_VERSION,
    schema: interopMutationIntentSchema,
    formatter: createInteropMutationIntentFormatter(),
  });
  const mutationReceipt = defineDocumentType({
    type: INTEROP_MUTATION_RECEIPT_DOCUMENT_TYPE,
    schemaId: INTEROP_MUTATION_RECEIPT_SCHEMA_ID,
    schemaName: INTEROP_MUTATION_RECEIPT_DOCUMENT_TYPE,
    schemaVersion: INTEROP_MUTATION_RECEIPT_SCHEMA_VERSION,
    schema: interopMutationReceiptSchema,
    formatter: createInteropMutationReceiptFormatter(),
  });
  const fireCommand = defineDocumentType({
    type: INTEROP_FIRE_COMMAND_DOCUMENT_TYPE,
    schemaId: INTEROP_FIRE_COMMAND_SCHEMA_ID,
    schemaName: INTEROP_FIRE_COMMAND_DOCUMENT_TYPE,
    schemaVersion: INTEROP_FIRE_COMMAND_SCHEMA_VERSION,
    schema: interopFireCommandSchema,
    formatter: createInteropFireCommandFormatter(),
  });
  const fireReceipt = defineDocumentType({
    type: INTEROP_FIRE_RECEIPT_DOCUMENT_TYPE,
    schemaId: INTEROP_FIRE_RECEIPT_SCHEMA_ID,
    schemaName: INTEROP_FIRE_RECEIPT_DOCUMENT_TYPE,
    schemaVersion: INTEROP_FIRE_RECEIPT_SCHEMA_VERSION,
    schema: interopFireReceiptSchema,
    formatter: createInteropFireReceiptFormatter(),
  });

  return {
    note: defineCultNetDocumentBinding({ definition: note, payloadSchemaVersion: INTEROP_SCHEMA_VERSION }),
    mutationIntent: defineCultNetDocumentBinding({ definition: mutationIntent, payloadSchemaVersion: INTEROP_MUTATION_INTENT_SCHEMA_VERSION }),
    mutationReceipt: defineCultNetDocumentBinding({ definition: mutationReceipt, payloadSchemaVersion: INTEROP_MUTATION_RECEIPT_SCHEMA_VERSION }),
    fireCommand: defineCultNetDocumentBinding({ definition: fireCommand, payloadSchemaVersion: INTEROP_FIRE_COMMAND_SCHEMA_VERSION }),
    fireReceipt: defineCultNetDocumentBinding({ definition: fireReceipt, payloadSchemaVersion: INTEROP_FIRE_RECEIPT_SCHEMA_VERSION }),
  };
}

async function connectTo(host: string, port: number): Promise<TcpSocket> {
  return new Promise<TcpSocket>((resolve, reject) => {
    const socket = connectTcp(port, host);
    socket.once("connect", () => resolve(socket));
    socket.once("error", reject);
  });
}

async function connectTcpPeer(runtimeId: string, targetHost: string, targetPort: number): Promise<CultNetPeer> {
  const socket = await connectTo(targetHost, targetPort);
  const transport = new TcpFramedTransportConnection(
    socket,
    createTcpFramedTransportProfile(runtimeId, {
      transportId: "interop-tcp",
      host: targetHost,
      port: targetPort,
    }),
  );
  return new CultNetPeer(transport, { wireContract: INTEROP_WIRE_CONTRACT });
}

async function connectRudpPeer(
  runtimeId: string,
  targetHost: string,
  targetPort: number,
  timeoutMs: number,
): Promise<CultNetPeer> {
  const socket = createSocket("udp4");
  await new Promise<void>((resolve, reject) => {
    socket.once("error", reject);
    socket.bind(0, "127.0.0.1", () => resolve());
  });
  const transport = new CultNetRudpSocketTransportConnection({
    runtimeId,
    socket,
    mode: "client",
    remoteHost: targetHost,
    remotePort: targetPort,
    connectionId: RUDP_CONNECTION_ID,
    transportId: "interop-rudp",
    resendDelayMs: 25,
  });
  transport.connect(encodeText("cultnet-interop-rudp"));
  await waitUntil(() => transport.connected, timeoutMs, "RUDP interop dial handshake");
  return new CultNetPeer(transport, { wireContract: INTEROP_WIRE_CONTRACT });
}

async function waitUntil(predicate: () => boolean, timeoutMs: number, label: string): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (predicate()) {
      return;
    }
    await delay(5);
  }
  throw new Error(`Timed out waiting for ${label}.`);
}

function sendRudpPacket(socket: DgramSocket, host: string, port: number, packet: CultNetRudpPacket): void {
  socket.send(encodeRudpPacket(packet), port, host);
}

function encodeText(value: string): Uint8Array<ArrayBuffer> {
  return new Uint8Array(textEncoder.encode(value));
}

async function closeServer(server: Server): Promise<void> {
  await new Promise<void>((resolve, reject) => {
    server.close((error) => {
      if (error) {
        reject(error);
        return;
      }

      resolve();
    });
  });
}

async function waitForTermination(cleanup: () => Promise<void>): Promise<void> {
  await new Promise<void>((resolve, reject) => {
    let closed = false;
    const finish = async (signal: NodeJS.Signals) => {
      if (closed) {
        return;
      }

      closed = true;
      try {
        await cleanup();
        writeLog("shutdown", { signal });
        resolve();
      } catch (error) {
        reject(error);
      }
    };

    process.once("SIGTERM", () => void finish("SIGTERM"));
    process.once("SIGINT", () => void finish("SIGINT"));
  });
}

function writeJsonLine(value: unknown): void {
  process.stdout.write(`${JSON.stringify(value)}\n`);
}

function writeLog(event: string, payload: Record<string, unknown>): void {
  process.stderr.write(`${JSON.stringify({ event, ...payload })}\n`);
}

function writeTrace(event: string, payload: Record<string, unknown>): void {
  if (process.env.CULTNET_INTEROP_TRACE === "1") {
    writeLog(event, payload);
  }
}

function runtimeStorePath(runtimeId: string): string {
  return join(tmpdir(), `cultnet-ts-interop-${runtimeId}.msgpack`);
}

main().catch((error) => {
  writeLog("fatal", { error: error instanceof Error ? error.stack ?? error.message : String(error) });
  process.exitCode = 1;
});
