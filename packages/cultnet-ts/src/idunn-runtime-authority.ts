import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { decode, encode } from "@msgpack/msgpack";

import {
  GAMECULT_RUNTIME_PRESENCE_HEALTH_SCHEMA,
  GAMECULT_RUNTIME_PRESENCE_IDENTITY_NAME,
  IDUNN_RUNTIME_ACTIVATION_CREDENTIAL_NAME,
  runtimePresenceActivationSigningMessage,
  runtimePresenceProofPayload,
  runtimePresenceProviderSigningMessage,
  encodeRuntimePresenceHealth,
  type RuntimePresenceHealth,
} from "./runtime-presence-health";
import { encodeCultNetMessageForWire, parseCultNetMessage, type CultNetRawDocumentRecord, type CultNetSnapshotRequestMessage } from "./contracts";
import { CultNetRudpSession, decodeRudpPacket, encodeRudpPacket } from "./rudp";
import type { CultNetRudpPacket } from "./rudp";
import dgram from "node:dgram";

export const IDUNN_EXPECTED_INCARNATION_SCHEMA = "idunn.expected_incarnation.v2";
export const IDUNN_RUNTIME_ACTIVATION_SCHEMA = "idunn.runtime_activation.v2";
export const IDUNN_PROCESS_WRITE_LEASE_SCHEMA = "idunn.process_write_lease.v1";
export const IDUNN_PROCESS_WRITE_LEASE_ENVIRONMENT = "GAMECULT_IDUNN_PROCESS_WRITE_LEASE";
export const IDUNN_RUNTIME_BUNDLE_ENVIRONMENT = "GAMECULT_IDUNN_RUNTIME_BUNDLE";
export const IDUNN_RUNTIME_CANDIDATE_BIND_ENVIRONMENT = "GAMECULT_IDUNN_CANDIDATE_BIND";

const PROVIDER_ID_DOMAIN = Buffer.from("gamecult.provider-health.identity.v1\0", "utf8");
const PROVIDER_PROTECTOR_CONTEXT = "gamecult-provider-health-identity-v1";
const ACTIVATION_ID_DOMAIN = Buffer.from("idunn.runtime-activation.id.v1\0", "utf8");
const CULTCACHE_STORE_FORMAT = "cultcache.store.v1";
const ED25519_PKCS8_PREFIX = Buffer.from("302e020100300506032b657004220420", "hex");
const MAX_RECENT_WARMING_PROOFS = 64;
const authorityKeys = new WeakMap<object, { providerPrivateKey: crypto.KeyObject; activationPrivateKey: crypto.KeyObject }>();

type ExpectedIncarnation = {
  schemaVersion: string;
  target: string;
  planId: string;
  incarnationId: string;
  sealedReleaseId: string;
  sourceRepository: string;
  sourceRevision: string;
  recipeSha256: string;
  runtimeId: string;
  expectedSignerIdentityId: string;
  healthContract: string;
  artifactSha256: string;
  stateSchemaGeneration: string | null;
  stateContractSha256: string | null;
  writeLeaseRequired: boolean;
  route: { routeId: string; transport: string; stableEndpoint: string; candidateEndpoint: string } | null;
  capabilities: Array<{ capability: string; schema: string; compatibility: string; minimumCapacity: number }>;
  dependencies: Array<{ kind: string; capability: string; schema: string; compatibility: string; providerEndpoint: string | null }>;
  canonicalBytes: Buffer;
  canonicalSha256: string;
};

type RuntimeActivation = {
  schemaVersion: string;
  expectedProjectionSha256: string;
  runtimeId: string;
  runtimeInstanceId: string;
  activationSignerIdentityId: string;
  activationSignerPublicKey: Buffer;
  canonicalSha256: string;
};

export type IdunnRuntimeAuthority = {
  expected: ExpectedIncarnation;
  activation: RuntimeActivation;
  providerSignerIdentityId: string;
  activationSignerIdentityId: string;
  boundEndpoint: string;
  processWriteLeasePath?: string;
};

export type IdunnRuntimePresencePublisher = {
  readonly runtimeId: string;
  readonly runtimeInstanceId: string;
  readonly requiresWriteLease: boolean;
  latestPresenceDocument(): CultNetRawDocumentRecord | null;
  publishRouteObservation(request: CultNetSnapshotRequestMessage): Promise<CultNetRawDocumentRecord>;
  publish(state: RuntimePresenceHealth["state"], detail: string): Promise<string>;
  waitForWriteLease(options?: { pollIntervalMs?: number; signal?: AbortSignal }): Promise<string>;
  assertWriteLease(): Promise<string>;
};

export type IdunnRuntimePresencePublisherOptions = {
  authority: IdunnRuntimeAuthority;
  endpoint: string;
  healthContract: string;
  capabilities: RuntimePresenceHealth["capabilities"];
};

/**
 * Loads the root-provisioned Idunn bundle and parent-only signing descriptors.
 * The expected record and activation witness are accepted only when their
 * exact positional contracts and mutual incarnation binding agree.
 */
export function loadIdunnRuntimeAuthorityFromEnvironment(
  target: string,
  healthContract: string,
  odinEndpoint: string,
): IdunnRuntimeAuthority {
  const bundle = requiredEnvironment(IDUNN_RUNTIME_BUNDLE_ENVIRONMENT);
  const expected = readAuthorityRecord(path.join(bundle, "expected.cc"), "idunn.expected_incarnation", IDUNN_EXPECTED_INCARNATION_SCHEMA);
  const activation = readAuthorityRecord(path.join(bundle, "activation.cc"), "idunn.runtime_activation", IDUNN_RUNTIME_ACTIVATION_SCHEMA);
  const expectedValue = decodeExpected(expected.payload);
  const activationValue = decodeActivation(activation.payload);
  if (expectedValue.target !== target || expectedValue.healthContract !== healthContract) {
    throw new Error("Idunn Expected target or health contract does not match this runtime.");
  }
  const odinDependency = expectedValue.dependencies.find((dependency) => dependency.kind === "shared-infrastructure"
    && dependency.capability === "odin.verse-rendezvous");
  if (!odinDependency || normalizeRudpEndpoint(odinDependency.providerEndpoint ?? "") !== normalizeRudpEndpoint(odinEndpoint)) {
    throw new Error("Configured Odin endpoint does not match Idunn Expected dependency authority.");
  }
  if (activationValue.expectedProjectionSha256 !== expectedValue.canonicalSha256
    || activationValue.runtimeId !== expectedValue.runtimeId) {
    throw new Error("Idunn Activation does not bind this exact Expected incarnation.");
  }

  const descriptors = inheritedDescriptorMap();
  const providerCredential = descriptors.get(GAMECULT_RUNTIME_PRESENCE_IDENTITY_NAME);
  const activationCredential = descriptors.get(IDUNN_RUNTIME_ACTIVATION_CREDENTIAL_NAME);
  if (providerCredential === undefined || activationCredential === undefined) {
    throw new Error("Idunn runtime-presence signing descriptors are missing.");
  }

  const providerPrivateKey = openProviderHealthIdentity(readDescriptor(providerCredential));
  const activationSeed = readDescriptor(activationCredential);
  if (activationSeed.length !== 32) throw new Error("Idunn activation credential must be exactly 32 bytes.");
  const activationPrivateKey = privateKeyFromSeed(activationSeed);
  activationSeed.fill(0);
  const activationPublicKey = rawEd25519PublicKey(crypto.createPublicKey(activationPrivateKey));
  const activationSignerIdentityId = sha256Hex(Buffer.concat([ACTIVATION_ID_DOMAIN, activationPublicKey]));
  if (activationSignerIdentityId !== activationValue.activationSignerIdentityId
    || !activationPublicKey.equals(activationValue.activationSignerPublicKey)) {
    throw new Error("Idunn activation credential does not match the root-provisioned Activation witness.");
  }

  if (expectedValue.writeLeaseRequired !== (process.env[IDUNN_PROCESS_WRITE_LEASE_ENVIRONMENT] !== undefined)) {
    throw new Error("Idunn process write-lease environment does not match Expected.");
  }
  const boundEndpoint = requiredEnvironment(IDUNN_RUNTIME_CANDIDATE_BIND_ENVIRONMENT);
  if (!expectedValue.route || expectedValue.route.candidateEndpoint !== boundEndpoint) {
    throw new Error("Idunn candidate bind differs from the Expected route.");
  }
  if (expectedValue.expectedSignerIdentityId !== providerSignerIdentityId(providerPrivateKey)) {
    throw new Error("Provider identity does not match Idunn Expected.");
  }

  const authority: IdunnRuntimeAuthority = {
    expected: expectedValue,
    activation: activationValue,
    providerSignerIdentityId: expectedValue.expectedSignerIdentityId,
    activationSignerIdentityId,
    boundEndpoint,
    ...(process.env[IDUNN_PROCESS_WRITE_LEASE_ENVIRONMENT]
      ? { processWriteLeasePath: process.env[IDUNN_PROCESS_WRITE_LEASE_ENVIRONMENT] }
      : {}),
  };
  authorityKeys.set(authority, { providerPrivateKey, activationPrivateKey });
  return authority;
}

/**
 * Signs the fixed Idunn v2 positional runtime-presence contract with the
 * provider identity and this launch's Idunn-issued activation key.
 */
export function signIdunnRuntimePresence(
  authority: IdunnRuntimeAuthority,
  fields: Omit<RuntimePresenceHealth, "target" | "expectedProjectionSha256" | "planId" | "incarnationId" | "sealedReleaseId" | "activationWitnessSha256" | "stateSchemaGeneration" | "stateContractSha256" | "runtimeId" | "runtimeInstanceId" | "boundEndpoint" | "healthContract" | "signerIdentityId" | "activationSignerIdentityId">,
): { presence: RuntimePresenceHealth; payload: Uint8Array; canonicalSha256: string } {
  const presence: RuntimePresenceHealth = {
    ...fields,
    target: authority.expected.target,
    expectedProjectionSha256: authority.expected.canonicalSha256,
    planId: authority.expected.planId,
    incarnationId: authority.expected.incarnationId,
    sealedReleaseId: authority.expected.sealedReleaseId,
    activationWitnessSha256: authority.activation.canonicalSha256,
    stateSchemaGeneration: authority.expected.stateSchemaGeneration,
    stateContractSha256: authority.expected.stateContractSha256,
    runtimeId: authority.expected.runtimeId,
    runtimeInstanceId: authority.activation.runtimeInstanceId,
    boundEndpoint: authority.boundEndpoint,
    healthContract: authority.expected.healthContract,
    signerIdentityId: authority.providerSignerIdentityId,
    activationSignerIdentityId: authority.activationSignerIdentityId,
  };
  const keys = authorityKeys.get(authority);
  if (!keys) throw new Error("Idunn runtime authority was not opened by the CultNet credential reader.");
  const proofPayload = runtimePresenceProofPayload(presence);
  const providerSignature = crypto.sign(null, runtimePresenceProviderSigningMessage(proofPayload), keys.providerPrivateKey);
  const activationSignature = crypto.sign(null, runtimePresenceActivationSigningMessage(proofPayload), keys.activationPrivateKey);
  const payload = encodeRuntimePresenceHealth(presence, providerSignature, activationSignature);
  return {
    presence,
    payload,
    canonicalSha256: prefixedSha256(Buffer.from(payload)),
  };
}

export function createIdunnRuntimePresencePublisher(
  options: IdunnRuntimePresencePublisherOptions,
): IdunnRuntimePresencePublisher {
  const endpoint = parseRudpEndpoint(options.endpoint);
  const expected = options.authority.expected;
  if (options.healthContract !== expected.healthContract) throw new Error("Runtime presence contract differs from Expected.");
  if (options.authority.boundEndpoint !== expected.route?.candidateEndpoint) throw new Error("Runtime presence bind differs from Expected.");
  for (const required of expected.capabilities) {
    if (!options.capabilities.some((actual) => actual.capability === required.capability
      && actual.schema === required.schema
      && actual.compatibility === required.compatibility
      && actual.capacity >= required.minimumCapacity)) {
      throw new Error(`Runtime does not provide Expected capability ${required.capability}/${required.schema}.`);
    }
  }
  const connectionId = 0x0d1d0002;
  let sequence = 0;
  let latestLeaseSha256: string | null = null;
  let latestPresenceDocument: CultNetRawDocumentRecord | null = null;
  const recentWarmingProofs: string[] = [];

  const assertWriteLease = async () => {
    if (!expected.writeLeaseRequired) return "";
    const leasePath = options.authority.processWriteLeasePath;
    if (!leasePath) throw new Error("Idunn process write lease is required but no lease path is configured.");
    const record = readAuthorityRecord(leasePath, "idunn.process_write_lease", IDUNN_PROCESS_WRITE_LEASE_SCHEMA);
    const lease = decodeArray(record.payload, 14, "Idunn process write lease");
    assertLeaseMatches(lease, options.authority, recentWarmingProofs);
    latestLeaseSha256 = prefixedSha256(Buffer.from(encode(lease)));
    return latestLeaseSha256;
  };

  const publish = async (state: RuntimePresenceHealth["state"], detail: string) => {
    const writeLeaseSha256 = state === "warming" || !expected.writeLeaseRequired ? null : await assertWriteLease();
    sequence += 1;
    const signed = signIdunnRuntimePresence(options.authority, {
      capabilities: options.capabilities,
      state,
      detail: detail.slice(0, 512),
      writeLeaseSha256,
      publisherSequence: sequence,
      observedAtUnixMillis: Date.now(),
    });
    const document: CultNetRawDocumentRecord = {
      schemaId: GAMECULT_RUNTIME_PRESENCE_HEALTH_SCHEMA,
      recordKey: expected.target,
      storedAt: new Date().toISOString(),
      payloadEncoding: "messagepack",
      payload: new Uint8Array(signed.payload),
      sourceRuntimeId: expected.runtimeId,
      sourceRole: "runtime-presence-health-publisher",
      tags: ["cultnet.transport.rudp.v0", "runtime-presence"],
    };
    await publishDocument(endpoint, connectionId, {
      schemaVersion: "cultnet.document_put_raw.v0",
      messageId: `runtime-presence:${expected.target}:${options.authority.activation.runtimeInstanceId}:${sequence}`,
      document,
    });
    latestPresenceDocument = document;
    if (state === "warming") {
      recentWarmingProofs.push(signed.canonicalSha256);
      if (recentWarmingProofs.length > MAX_RECENT_WARMING_PROOFS) recentWarmingProofs.shift();
    }
    return signed.canonicalSha256;
  };

  const copyLatestPresenceDocument = (): CultNetRawDocumentRecord | null => {
    if (!latestPresenceDocument) return null;
    return {
      ...latestPresenceDocument,
      payload: new Uint8Array(latestPresenceDocument.payload),
      ...(latestPresenceDocument.tags ? { tags: [...latestPresenceDocument.tags] } : {}),
    };
  };

  return {
    runtimeId: expected.runtimeId,
    runtimeInstanceId: options.authority.activation.runtimeInstanceId,
    requiresWriteLease: expected.writeLeaseRequired,
    latestPresenceDocument: copyLatestPresenceDocument,
    async publishRouteObservation(request) {
      if (!request.messageId || request.messageId.trim() !== request.messageId) throw new Error("Idunn route observation requires a message id.");
      if ((request.schemaIds && !request.schemaIds.includes(GAMECULT_RUNTIME_PRESENCE_HEALTH_SCHEMA))
        || (request.recordKeys && !request.recordKeys.includes(expected.target))
        || request.shardId !== undefined
        || request.shardEpoch !== undefined) {
        throw new Error("Idunn route observation does not request this runtime presence document.");
      }
      await publish("active", `route-observation:${request.messageId}`);
      const document = copyLatestPresenceDocument();
      if (!document) throw new Error("Idunn route observation was published without a presence document.");
      return document;
    },
    publish,
    async waitForWriteLease({ pollIntervalMs = 5000, signal } = {}) {
      if (!expected.writeLeaseRequired) return "";
      let warmingSequence = 0;
      while (!signal?.aborted) {
        await publish("warming", "waiting-for-process-write-lease");
        warmingSequence += 1;
        try {
          return await assertWriteLease();
        } catch (error) {
          if (warmingSequence > 720) throw new Error("Timed out waiting for Idunn process write lease.", { cause: error });
        }
        await abortableDelay(pollIntervalMs, signal);
      }
      throw signal?.reason ?? new Error("Aborted while waiting for Idunn process write lease.");
    },
    assertWriteLease,
  };
}

async function publishDocument(
  endpoint: { host: string; port: number },
  connectionId: number,
  message: Record<string, unknown>,
): Promise<void> {
  const socket = dgram.createSocket(endpoint.host.includes(":") ? "udp6" : "udp4");
  await new Promise<void>((resolve, reject) => {
    socket.once("error", reject);
    socket.bind(0, endpoint.host.includes(":") ? "::" : "0.0.0.0", () => {
      socket.off("error", reject);
      resolve();
    });
  });
  const receiver = receivePackets(socket);
  const session = new CultNetRudpSession({ connectionId, initialSequence: 1, resendDelayMs: 100 });
  try {
    await sendPacket(socket, endpoint, session.createConnect(Date.now(), new Uint8Array()));
    await receiveUntil(receiver, session, endpoint, (packet) => packet.packetType === "accept", 5000);
    const wirePayload = encode(encodeCultNetMessageForWire(message as never, "cultnet.schema.v0"));
    const packets = session.sendMany("schema", wirePayload, { reliable: true, ordered: true, nowMs: Date.now() });
    for (const packet of packets) await sendPacket(socket, endpoint, packet);
    await receiveUntil(
      receiver,
      session,
      endpoint,
      () => session.outstandingReliablePacketCount === 0,
      2000,
      (frame) => {
        if (frame.channelId !== "schema") return;
        const response = parseCultNetMessage(decode(frame.payload));
        if (response.schemaVersion === "cultnet.error.v0") {
          throw new Error(`Odin rejected runtime presence: ${response.error}`);
        }
      },
    );
  } finally {
    receiver.close();
    socket.close();
  }
}

type PacketReceiver = {
  socket: dgram.Socket;
  next(timeoutMs: number): Promise<CultNetRudpPacket>;
  close(): void;
};

function receivePackets(socket: dgram.Socket): PacketReceiver {
  const packets: CultNetRudpPacket[] = [];
  const waiters: Array<{ resolve: (packet: CultNetRudpPacket) => void; reject: (error: Error) => void; timer: NodeJS.Timeout }> = [];
  const errors: Error[] = [];
  const drain = () => {
    while (waiters.length && (packets.length || errors.length)) {
      const waiter = waiters.shift()!;
      clearTimeout(waiter.timer);
      if (errors.length) waiter.reject(errors.shift()!);
      else waiter.resolve(packets.shift()!);
    }
  };
  const onMessage = (wire: Buffer) => {
    try { packets.push(decodeRudpPacket(wire)); } catch (error) { errors.push(error as Error); }
    drain();
  };
  const onError = (error: Error) => { errors.push(error); drain(); };
  socket.on("message", onMessage);
  socket.on("error", onError);
  return {
    socket,
    next(timeoutMs) {
      if (packets.length) return Promise.resolve(packets.shift()!);
      if (errors.length) return Promise.reject(errors.shift()!);
      return new Promise((resolve, reject) => {
        const waiter = {
          resolve,
          reject,
          timer: setTimeout(() => {
            const index = waiters.indexOf(waiter);
            if (index >= 0) waiters.splice(index, 1);
            const error = new Error("Timed out waiting for CultNet RUDP packet.") as NodeJS.ErrnoException;
            error.code = "ETIMEDOUT";
            reject(error);
          }, Math.max(1, timeoutMs)),
        };
        waiters.push(waiter);
      });
    },
    close() {
      socket.off("message", onMessage);
      socket.off("error", onError);
      for (const waiter of waiters.splice(0)) {
        clearTimeout(waiter.timer);
        waiter.reject(new Error("CultNet RUDP publisher closed."));
      }
    },
  };
}

async function receiveUntil(
  receiver: PacketReceiver,
  session: CultNetRudpSession,
  endpoint: { host: string; port: number },
  predicate: (packet: CultNetRudpPacket) => boolean,
  timeoutMs: number,
  onDelivered?: (frame: { channelId: string; payload: Uint8Array; sequence: number }) => void,
): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const packet = await receiver.next(Math.min(100, deadline - Date.now()));
      const received = session.receive(packet, Date.now());
      if (received.reply) throw new Error("Runtime presence received an unexpected reply-required packet.");
      for (const frame of received.delivered) onDelivered?.(frame);
      if (predicate(packet)) return;
    } catch (error) {
      if ((error as NodeJS.ErrnoException)?.code !== "ETIMEDOUT") throw error;
    }
    for (const packet of session.dueResends(Date.now())) await sendPacket(receiver.socket, endpoint, packet);
  }
  throw new Error(`Timed out waiting for CultNet RUDP response after ${timeoutMs} ms.`);
}

async function sendPacket(socket: dgram.Socket, endpoint: { host: string; port: number }, packet: CultNetRudpPacket): Promise<void> {
  const wire = encodeRudpPacket(packet);
  await new Promise<void>((resolve, reject) => {
    socket.send(wire, endpoint.port, endpoint.host, (error) => error ? reject(error) : resolve());
  });
}

function readAuthorityRecord(filePath: string, type: string, schemaId: string): { payload: Uint8Array } {
  const decoded = decode(fs.readFileSync(filePath));
  if (!Array.isArray(decoded) || decoded[0] !== CULTCACHE_STORE_FORMAT || !Array.isArray(decoded[1]) || !Array.isArray(decoded[2])) {
    throw new Error(`Idunn authority file ${filePath} is not a CultCache v1 store.`);
  }
  if (decoded[2].length !== 1) throw new Error(`Idunn authority file ${filePath} must contain exactly one record.`);
  const matchingRecords = (decoded[2] as unknown[]).filter((raw) => {
    const row = array(raw, 4, "CultCache record");
    return row[1] === schemaId;
  });
  if (matchingRecords.length !== 1) throw new Error(`Idunn authority file must contain exactly one ${schemaId} record.`);
  const row = array(matchingRecords[0], 4, "CultCache record");
  const catalog = (decoded[1] as unknown[]).find((raw) => {
    const entry = Array.isArray(raw) ? raw : [];
    return entry[0] === schemaId;
  });
  const catalogEntry = array(catalog, 7, "CultCache schema catalog entry");
  if (catalogEntry[1] !== type) throw new Error(`Idunn authority record has unexpected CultCache type ${String(catalogEntry[1])}.`);
  if (!(row[3] instanceof Uint8Array)) throw new Error("Idunn authority payload is not MessagePack bytes.");
  return { payload: row[3] };
}

function decodeExpected(payload: Uint8Array): ExpectedIncarnation {
  const values = decodeArray(payload, 18, "Idunn Expected");
  if (values[0] !== IDUNN_EXPECTED_INCARNATION_SCHEMA) throw new Error("Idunn Expected schema is unsupported.");
  const canonicalBytes = Buffer.from(encode(values));
  if (!canonicalBytes.equals(Buffer.from(payload))) throw new Error("Idunn Expected is not canonical MessagePack.");
  const stateSchemaGeneration = nullableString(values[12], "Expected state generation");
  const stateContractSha256 = nullableString(values[13], "Expected state contract");
  if ((stateSchemaGeneration === null) !== (stateContractSha256 === null)) throw new Error("Idunn Expected state lineage is partial.");
  if (typeof values[14] !== "boolean") throw new Error("Idunn Expected write-lease flag is not boolean.");
  const route = decodeRoute(values[15]);
  const writeLeaseRequired = values[14];
  if (writeLeaseRequired && stateSchemaGeneration === null) throw new Error("Stateless Idunn Expected cannot require a write lease.");
  const target = string(values[1], "Expected target");
  const planId = string(values[2], "Expected plan id");
  const incarnationId = string(values[3], "Expected incarnation id");
  const sealedReleaseId = string(values[4], "Expected sealed release");
  const sourceRepository = string(values[5], "Expected source repository");
  const sourceRevision = string(values[6], "Expected source revision");
  const recipeSha256 = string(values[7], "Expected recipe hash");
  const runtimeId = string(values[8], "Expected runtime id");
  const expectedSignerIdentityId = string(values[9], "Expected signer identity");
  const healthContract = string(values[10], "Expected health contract");
  const artifactSha256 = string(values[11], "Expected artifact hash");
  for (const [name, hash] of [["plan", planId], ["release", sealedReleaseId], ["recipe", recipeSha256], ["artifact", artifactSha256]] as const) {
    if (!/^sha256-[0-9a-f]{64}$/u.test(hash)) throw new Error(`Expected ${name} hash is malformed.`);
  }
  if (!/^[0-9a-f]{40}$/u.test(sourceRevision)) throw new Error("Expected source revision is malformed.");
  if (!route || route.transport !== "http" || !route.candidateEndpoint.startsWith("http://")) {
    throw new Error("Idunn Expected must include an HTTP candidate route.");
  }
  return {
    schemaVersion: string(values[0], "Expected schema"),
    target,
    planId,
    incarnationId,
    sealedReleaseId,
    sourceRepository,
    sourceRevision,
    recipeSha256,
    runtimeId,
    expectedSignerIdentityId,
    healthContract,
    artifactSha256,
    stateSchemaGeneration,
    stateContractSha256,
    writeLeaseRequired,
    route,
    capabilities: decodeExpectedCapabilities(values[16]),
    dependencies: decodeExpectedDependencies(values[17]),
    canonicalBytes,
    canonicalSha256: prefixedSha256(canonicalBytes),
  };
}

function decodeActivation(payload: Uint8Array): RuntimeActivation {
  const values = decodeArray(payload, 10, "Idunn Activation");
  if (values[0] !== IDUNN_RUNTIME_ACTIVATION_SCHEMA) throw new Error("Idunn Activation schema is unsupported.");
  const canonicalBytes = Buffer.from(encode(values));
  if (!canonicalBytes.equals(Buffer.from(payload))) throw new Error("Idunn Activation is not canonical MessagePack.");
  const publicKey = bytes(values[5], "Activation signer public key");
  if (publicKey.length !== 32) throw new Error("Activation signer public key is not Ed25519.");
  const activationSignerIdentityId = string(values[4], "Activation signer identity");
  if (number(values[6], "Activation issue time") <= 0
    || !/^[0-9a-f]{64}$/u.test(string(values[7], "Idunn signer identity"))
    || values[8] !== "ed25519"
    || bytes(values[9], "Activation signature").length !== 64) {
    throw new Error("Idunn Activation signature contract is malformed.");
  }
  if (sha256Hex(Buffer.concat([ACTIVATION_ID_DOMAIN, publicKey])) !== activationSignerIdentityId) {
    throw new Error("Activation signer identity does not match its public key.");
  }
  return {
    schemaVersion: string(values[0], "Activation schema"),
    expectedProjectionSha256: string(values[1], "Activation Expected hash"),
    runtimeId: string(values[2], "Activation runtime id"),
    runtimeInstanceId: string(values[3], "Activation instance id"),
    activationSignerIdentityId,
    activationSignerPublicKey: Buffer.from(publicKey),
    canonicalSha256: prefixedSha256(canonicalBytes),
  };
}

function decodeExpectedCapabilities(value: unknown): ExpectedIncarnation["capabilities"] {
  return arrayOf(value, "Expected capabilities").map((raw) => {
    const item = array(raw, 4, "Expected capability");
    return {
      capability: string(item[0], "Capability id"),
      schema: string(item[1], "Capability schema"),
      compatibility: string(item[2], "Capability compatibility"),
      minimumCapacity: number(item[3], "Capability capacity"),
    };
  });
}

function decodeExpectedDependencies(value: unknown): ExpectedIncarnation["dependencies"] {
  return arrayOf(value, "Expected dependencies").map((raw) => {
    const item = array(raw, 10, "Expected dependency");
    return {
      kind: string(item[0], "Dependency kind"),
      capability: string(item[1], "Dependency capability"),
      schema: string(item[2], "Dependency schema"),
      compatibility: string(item[3], "Dependency compatibility"),
      providerEndpoint: nullableString(item[9], "Dependency provider endpoint"),
    };
  });
}

function decodeRoute(value: unknown): ExpectedIncarnation["route"] {
  if (value === null) return null;
  const route = array(value, 4, "Expected route");
  return {
    routeId: string(route[0], "Route id"),
    transport: string(route[1], "Route transport"),
    stableEndpoint: string(route[2], "Stable route endpoint"),
    candidateEndpoint: string(route[3], "Candidate route endpoint"),
  };
}

function openProviderHealthIdentity(credentialBytes: Buffer): crypto.KeyObject {
  const fields = decodeArray(readProviderHealthIdentityCredential(credentialBytes), 10, "provider-health identity");
  const [schema, identityId, publicKeyValue, protectedSeedValue, protectorKind, binding, version, assurance, createdAt, enrollmentNonceValue] = fields;
  if (schema !== "gamecult.provider_health_identity.private.v1" || version !== "v1") throw new Error("Provider-health identity schema is unsupported.");
  string(createdAt, "Provider-health identity creation time");
  const enrollmentNonce = providerIdentityByteVector(enrollmentNonceValue, "Provider-health enrollment nonce");
  if (enrollmentNonce.length !== 32) throw new Error("Provider-health enrollment nonce has an invalid length.");
  const publicKey = Buffer.from(providerIdentityByteVector(publicKeyValue, "Provider-health public key"));
  const protectedSeed = Buffer.from(providerIdentityByteVector(protectedSeedValue, "Provider-health protected seed"));
  const machineId = fs.readFileSync("/etc/machine-id", "utf8").trim();
  if (!machineId) throw new Error("Linux machine-id is unavailable for provider-health identity.");
  const expectedBinding = `${PROVIDER_PROTECTOR_CONTEXT}:machine-id-sha256:${sha256Hex(Buffer.from(machineId))}`;
  if (protectorKind !== "linux_file_mode_machine_id_binding" || binding !== expectedBinding || assurance !== "os_installation_file_bound_cloneable_baseline") {
    throw new Error("Provider-health identity protector does not match this Linux installation.");
  }
  if (publicKey.length !== 32 || protectedSeed.length !== 32) throw new Error("Provider-health identity key material has an invalid length.");
  const mask = crypto.createHash("sha256").update(Buffer.concat([
    Buffer.from("gamecult-linux-service-seed-v1\0", "utf8"),
    Buffer.from(PROVIDER_PROTECTOR_CONTEXT, "utf8"),
    Buffer.from(expectedBinding, "utf8"),
  ])).digest();
  const seed = Buffer.from(protectedSeed.map((value, index) => value ^ mask[index]!));
  const key = privateKeyFromSeed(seed);
  seed.fill(0);
  const derivedPublicKey = rawEd25519PublicKey(crypto.createPublicKey(key));
  const derivedId = sha256Hex(Buffer.concat([PROVIDER_ID_DOMAIN, derivedPublicKey]));
  if (!derivedPublicKey.equals(publicKey) || identityId !== derivedId) throw new Error("Provider-health private key differs from its enrolled identity.");
  return key;
}

function readProviderHealthIdentityCredential(bytes: Buffer): Uint8Array {
  const decoded = decode(bytes);
  if (!Array.isArray(decoded) || decoded.length !== 1) {
    throw new Error("Provider-health identity credential must contain exactly one envelope.");
  }
  const envelope = array(decoded[0], 5, "Provider-health identity envelope");
  if (envelope[0] !== "gamecult-provider-health-identity"
    || envelope[1] !== "gamecult.provider_health_identity.private.v1"
    || envelope[4] !== "gamecult.provider_health_identity.private.v1") {
    throw new Error("Provider-health identity credential belongs to a different profile or schema.");
  }
  string(envelope[3], "Provider-health identity stored-at time");
  return bytesValue(envelope[2], "Provider-health identity payload");
}

function providerIdentityByteVector(value: unknown, label: string): Uint8Array {
  if (!Array.isArray(value) || value.some((byte) => typeof byte !== "number" || !Number.isInteger(byte) || byte < 0 || byte > 255)) {
    throw new Error(`${label} is not a byte vector.`);
  }
  return Uint8Array.from(value as number[]);
}

function readAuthorityRecordBytes(bytes: Buffer, type: string, schemaId: string): Uint8Array {
  const decoded = decode(bytes);
  if (!Array.isArray(decoded) || decoded[0] !== CULTCACHE_STORE_FORMAT || !Array.isArray(decoded[1]) || !Array.isArray(decoded[2])) {
    throw new Error("Runtime credential is not a CultCache v1 store.");
  }
  if (decoded[2].length !== 1) throw new Error("Runtime credential must contain exactly one record.");
  const rows = (decoded[2] as unknown[]).filter((raw) => Array.isArray(raw) && raw[1] === schemaId);
  if (rows.length !== 1) throw new Error(`Runtime credential must contain exactly one ${schemaId} record.`);
  const catalog = (decoded[1] as unknown[]).find((raw) => Array.isArray(raw) && raw[0] === schemaId);
  if (!Array.isArray(catalog) || catalog[1] !== type) throw new Error("Runtime credential has an unexpected CultCache type.");
  const row = array(rows[0], 4, "Runtime credential record");
  return bytesValue(row[3], "Runtime credential payload");
}

function inheritedDescriptorMap(): Map<string, number> {
  if (!systemdListenPidMatches(process.env.LISTEN_PID, process.pid)) throw new Error("Inherited Idunn descriptors do not belong to this process.");
  const count = Number(process.env.LISTEN_FDS);
  const names = process.env.LISTEN_FDNAMES?.split(":") ?? [];
  if (!Number.isInteger(count) || count < 1 || names.length !== count) throw new Error("Inherited Idunn descriptor list is malformed.");
  return new Map(names.map((name, index) => [name, index + 3]));
}

export function systemdListenPidMatches(listenPid: string | undefined, processPid: number): boolean {
  if (listenPid === String(processPid)) return true;
  // systemd sets LISTEN_PID in the host PID namespace. With PrivatePIDs=yes,
  // the service's main process is namespace PID 1 and cannot read that host
  // PID through its private /proc mount. A descendant has a PID greater than
  // 1 and must not reuse the inherited descriptor set.
  return processPid === 1 && /^\d+$/u.test(listenPid ?? "") && Number(listenPid) > 1;
}

function readDescriptor(descriptor: number): Buffer {
  return fs.readFileSync(descriptor);
}

function privateKeyFromSeed(seed: Uint8Array): crypto.KeyObject {
  return crypto.createPrivateKey({ key: Buffer.concat([ED25519_PKCS8_PREFIX, Buffer.from(seed)]), format: "der", type: "pkcs8" });
}

function rawEd25519PublicKey(publicKey: crypto.KeyObject): Buffer {
  const der = publicKey.export({ type: "spki", format: "der" }) as Buffer;
  return Buffer.from(der.subarray(der.length - 32));
}

function providerSignerIdentityId(privateKey: crypto.KeyObject): string {
  return sha256Hex(Buffer.concat([PROVIDER_ID_DOMAIN, rawEd25519PublicKey(crypto.createPublicKey(privateKey))]));
}

function assertLeaseMatches(values: unknown[], authority: IdunnRuntimeAuthority, recentWarmingProofs: string[]): void {
  const expected = authority.expected;
  const activation = authority.activation;
  const matches = values[0] === IDUNN_PROCESS_WRITE_LEASE_SCHEMA
    && values[1] === expected.target
    && values[2] === expected.canonicalSha256
    && values[3] === expected.planId
    && values[4] === expected.incarnationId
    && values[5] === expected.sealedReleaseId
    && values[6] === activation.canonicalSha256
    && values[7] === expected.stateSchemaGeneration
    && values[8] === expected.stateContractSha256
    && values[9] === expected.runtimeId
    && values[10] === activation.runtimeInstanceId
    && typeof values[11] === "string"
    && recentWarmingProofs.includes(values[11])
    && Number.isSafeInteger(values[12]) && Number(values[12]) > 0
    && Number.isSafeInteger(values[13]) && Number(values[13]) > 0;
  if (!matches) throw new Error("Idunn process write lease does not match the current Expected incarnation.");
}

function parseRudpEndpoint(value: string): { host: string; port: number } {
  const text = value.replace(/^rudp:\/\//i, "");
  const match = text.match(/^\[([^\]]+)\]:(\d+)$/) ?? text.match(/^([^:]+):(\d+)$/);
  if (!match) throw new Error(`Invalid Odin RUDP endpoint: ${value}`);
  const port = Number(match[2]);
  if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error(`Invalid Odin RUDP port: ${match[2]}`);
  return { host: match[1]!, port };
}

function normalizeRudpEndpoint(value: string): string {
  const normalized = value.trim().toLowerCase().replace(/^rudp:\/\//, "");
  const parsed = parseRudpEndpoint(normalized);
  return `${parsed.host.toLowerCase()}:${parsed.port}`;
}

function requiredEnvironment(name: string): string {
  const value = process.env[name]?.trim();
  if (!value) throw new Error(`${name} is required for Idunn runtime presence.`);
  return value;
}

function abortableDelay(milliseconds: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        signal?.removeEventListener("abort", onAbort);
        resolve();
      }, milliseconds);
      const onAbort = () => {
        clearTimeout(timer);
        reject(signal?.reason ?? new Error("Aborted."));
      };
      signal?.addEventListener("abort", onAbort, { once: true });
  });
}

function array(value: unknown, length: number, label: string): unknown[] {
  if (!Array.isArray(value) || value.length !== length) throw new Error(`${label} is not a ${length}-field positional contract.`);
  return value;
}

function decodeArray(payload: Uint8Array, length: number, label: string): unknown[] {
  return array(decode(payload), length, label);
}

function arrayOf(value: unknown, label: string): unknown[] {
  if (!Array.isArray(value)) throw new Error(`${label} is not an array.`);
  return value;
}

function string(value: unknown, label: string): string {
  if (typeof value !== "string" || !value.trim() || value.trim() !== value || /[\u0000-\u001f\u007f]/u.test(value)) {
    throw new Error(`${label} is not a valid authority string.`);
  }
  return value;
}

function nullableString(value: unknown, label: string): string | null {
  return value === null ? null : string(value, label);
}

function number(value: unknown, label: string): number {
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value < 0) throw new Error(`${label} is not a non-negative integer.`);
  return value;
}

function bytes(value: unknown, label: string): Uint8Array {
  if (!(value instanceof Uint8Array)) throw new Error(`${label} is not bytes.`);
  return value;
}

function bytesValue(value: unknown, label: string): Buffer {
  return Buffer.from(bytes(value, label));
}

function prefixedSha256(bytes: Uint8Array): string {
  return `sha256-${sha256Hex(bytes)}`;
}

function sha256Hex(bytes: Uint8Array): string {
  return crypto.createHash("sha256").update(bytes).digest("hex");
}
