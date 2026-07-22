import test from "node:test";
import assert from "node:assert/strict";
import { createSocket } from "node:dgram";
import { decode } from "@msgpack/msgpack";
import {
  CultNetDocumentRegistry,
  CultNetRudpSession,
  decodeRudpPacket,
  encodeRudpPacket,
  type CultNetOperationRequestMessage,
} from "cultnet-ts";

import {
  CultMesh,
  CultMeshProviderRudpTransport,
  CultMeshProviderSessionBroker,
  CULTMESH_PROVIDER_SESSION_SERVICE_ID,
  cultMeshProviderSessionOperations,
  cultMeshProviderSessionSchemas,
  decodeProviderConnectEvidence,
  encodeProviderConnectEvidence,
  encodeProviderSessionPayload,
  type CultMeshProviderCommand,
  type CultMeshProviderIdentity,
  type CultMeshProviderPublication,
} from "../src";

const identity: CultMeshProviderIdentity = {
  providerId: "voidbot.swarm",
  serviceInstanceId: "voidbot-worker-7",
  endpointId: "voidbot-worker-7.rudp",
  verseId: "voidbot.local",
};

test("RUDP provider transport requires broker acceptance for the full lifecycle", async () => {
  const accepted = new Map<string, unknown>();
  const acceptedProvenance = new Map<string, { runtime?: string; agent?: string }>();
  const deleted: string[] = [];
  const receipts: string[] = [];
  let leaseSequence = 0;
  const broker = new CultMeshProviderSessionBroker({
    runtimeId: "odin-test",
    createLeaseId: () => `lease-${++leaseSequence}`,
    authorizeRegistration: (_identity, session) =>
      session.connectPayload !== undefined
      && decodeProviderConnectEvidence(session.connectPayload).sessionToken === "signed-provider-token",
    acceptPublication: (_identity, publicationId, document) => {
      accepted.set(publicationId, decode(document.payload));
      acceptedProvenance.set(publicationId, {
        runtime: document.sourceRuntimeId,
        agent: document.sourceAgentId,
      });
    },
    deletePublications: (_identity, publications) => {
      for (const { publicationId } of publications) {
        deleted.push(publicationId);
        accepted.delete(publicationId);
      }
    },
    acceptReceipt: (_identity, receipt) => {
      receipts.push(receipt.receiptId);
    },
  });
  const server = CultMesh.createRudpDocumentServer("odin-test", 0x43554c54, {
    bindPort: 0,
    documents: new CultNetDocumentRegistry(),
    onOperationRequest: (request, session) => broker.handle(request, session),
    onSessionClosed: session => broker.sessionClosed(session),
  });
  await server.start();

  const unauthorizedConnection = await new CultMeshProviderRudpTransport({
    endpoint: `rudp://127.0.0.1:${server.bind.port}`,
    runtimeId: "unauthorized-provider",
    connectionId: 0x43554c54,
    sessionToken: "not-authority",
  }).connect(identity, new AbortController().signal);
  try {
    await assert.rejects(
      unauthorizedConnection.register(
        { identity, requestedLeaseDurationMs: 10_000 },
        new AbortController().signal,
      ),
      /denied|not authorized/i,
    );
    assert.equal(
      broker.activeLeaseCount,
      0,
      "a successful RUDP handshake does not grant provider authority",
    );
  } finally {
    unauthorizedConnection.close();
  }

  const providerSocket = createSocket("udp4");
  const rawSend = providerSocket.send.bind(providerSocket);
  let originalConnectWire: Buffer | undefined;
  providerSocket.send = ((message: Uint8Array, ...args: unknown[]) => {
    if (decodeRudpPacket(message).packetType === "connect" && !originalConnectWire) {
      originalConnectWire = Buffer.from(message);
    }
    return (rawSend as (...parameters: unknown[]) => boolean)(message, ...args);
  }) as typeof providerSocket.send;
  const transport = new CultMeshProviderRudpTransport({
    endpoint: `rudp://127.0.0.1:${server.bind.port}`,
    runtimeId: identity.serviceInstanceId,
    connectionId: 0x43554c54,
    sessionToken: "signed-provider-token",
    socketFactory: () => providerSocket,
  });
  const connection = await transport.connect(identity, new AbortController().signal);
  const secondIdentity: CultMeshProviderIdentity = {
    providerId: "aetheria",
    serviceInstanceId: "aetheria-daemon-2",
    endpointId: "aetheria-daemon-2.rudp",
    verseId: "aetheria.local",
  };
  const secondConnection = await new CultMeshProviderRudpTransport({
    endpoint: `rudp://127.0.0.1:${server.bind.port}`,
    runtimeId: secondIdentity.serviceInstanceId,
    connectionId: 0x43554c54,
    sessionToken: "signed-provider-token",
  }).connect(secondIdentity, new AbortController().signal);
  try {
    broker.enqueueCommand({
      commandId: "refresh-1",
      commandKind: "voidbot.refresh",
      providerId: identity.providerId,
      serviceInstanceId: identity.serviceInstanceId,
      payload: { force: true },
    });
    const lease = await connection.register({ identity, requestedLeaseDurationMs: 10_000 }, new AbortController().signal);
    assert.equal(lease.leaseId, "lease-1");
    assert.ok(originalConnectWire);
    await new Promise<void>((resolve, reject) => {
      rawSend(originalConnectWire!, server.bind.port, "127.0.0.1", error => error ? reject(error) : resolve());
    });
    await new Promise(resolve => setTimeout(resolve, 25));
    assert.equal(broker.activeLeaseCount, 1, "a duplicate Connect cannot revoke the live provider lease");
    const secondLease = await secondConnection.register({
      identity: secondIdentity,
      requestedLeaseDurationMs: 10_000,
    }, new AbortController().signal);
    assert.equal(secondLease.leaseId, "lease-2");
    assert.equal(broker.activeLeaseCount, 2, "the broker demultiplexes simultaneous physical provider sessions");

    const publication: CultMeshProviderPublication = {
      publicationId: "voidbot.surface",
      documentType: "gamecult.eve.surface_state",
      schemaId: "gamecult.eve.surface_state.v1",
      recordKey: "voidbot.swarm",
      value: { generation: 3 },
    };
    await connection.publish(publication, lease);
    assert.deepEqual(accepted.get(publication.publicationId), publication.value);
    assert.deepEqual(acceptedProvenance.get(publication.publicationId), {
      runtime: identity.serviceInstanceId,
      agent: identity.providerId,
    }, "the broker derives provenance from the fenced identity");
    const secondPublication: CultMeshProviderPublication = {
      publicationId: "aetheria.surface",
      documentType: "gamecult.eve.surface_state",
      schemaId: "gamecult.eve.surface_state.v1",
      recordKey: "aetheria.pilot",
      value: { generation: 8 },
    };
    await secondConnection.publish(secondPublication, secondLease);
    await assert.rejects(secondConnection.publish({
      ...secondPublication,
      publicationId: "aetheria.stolen-tuple",
      recordKey: publication.recordKey,
    }, secondLease), /already owned/);
    await assert.rejects(
      secondConnection.publish({ ...secondPublication, value: { generation: 9 } }, lease),
      /belongs to another physical session/,
    );

    const commandArrived = deferred<CultMeshProviderCommand>();
    connection.watchCommands(command => commandArrived.resolve(command));
    const command = await commandArrived.promise;
    assert.equal(command.commandId, "refresh-1");
    await connection.publishReceipt({
      receiptId: "receipt-refresh-1",
      commandId: command.commandId,
      commandKind: command.commandKind,
      providerId: identity.providerId,
      serviceInstanceId: identity.serviceInstanceId,
      state: "applied",
      completedAt: new Date(),
      result: { refreshed: true },
    }, lease);
    assert.deepEqual(receipts, ["receipt-refresh-1"]);

    const renewed = await connection.renew(lease);
    assert.equal(renewed.leaseId, "lease-3");
    await assert.rejects(connection.withdrawPublication(publication.publicationId, lease), /expired|fenced/);
    await connection.withdraw({ identity, leaseId: renewed.leaseId, publicationIds: [publication.publicationId] });
    await secondConnection.withdraw({
      identity: secondIdentity,
      leaseId: secondLease.leaseId,
      publicationIds: [secondPublication.publicationId],
    });
    assert.deepEqual(deleted, [publication.publicationId, secondPublication.publicationId]);
    assert.equal(accepted.size, 0);
    assert.equal(broker.activeLeaseCount, 0);
  } finally {
    connection.close();
    secondConnection.close();
    server.close();
    broker.close();
  }
});

test("closed physical sessions cannot finish delayed registration", async () => {
  const authorization = deferred<boolean>();
  const broker = new CultMeshProviderSessionBroker({
    runtimeId: "odin-test",
    authorizeRegistration: () => authorization.promise,
    acceptPublication: () => undefined,
    deletePublications: () => undefined,
    acceptReceipt: () => undefined,
  });
  const session = {
    sessionId: "closed-generation",
    remote: { address: "127.0.0.1", family: "IPv4", port: 40000 },
    connectPayload: new Uint8Array(),
    send: () => undefined,
  };
  const request: CultNetOperationRequestMessage = {
    schemaVersion: "cultnet.operation_request.v0",
    messageId: "late-register",
    serviceId: CULTMESH_PROVIDER_SESSION_SERVICE_ID,
    operation: cultMeshProviderSessionOperations.register,
    payloadSchema: cultMeshProviderSessionSchemas.registration,
    payloadEncoding: "messagepack-base64",
    payload: encodeProviderSessionPayload({ ...identity, requestedLeaseDurationMs: 10_000 }),
  };
  try {
    const pending = broker.handle(request, session);
    await new Promise(resolve => setTimeout(resolve, 0));
    broker.sessionClosed(session);
    authorization.resolve(true);
    const response = await pending;
    assert.equal(response.status, "denied");
    assert.equal(broker.activeLeaseCount, 0);
  } finally {
    broker.close();
  }
});

test("a replaced RUDP generation drops operations queued behind an active handler", async () => {
  const connectionId = 0x43554c54;
  const handlerGate = deferred<void>();
  let handlerCalls = 0;
  let closedSessions = 0;
  const server = CultMesh.createRudpDocumentServer("odin-queue-test", connectionId, {
    bindPort: 0,
    documents: new CultNetDocumentRegistry(),
    onOperationRequest: async request => {
      handlerCalls += 1;
      if (handlerCalls === 1) await handlerGate.promise;
      return {
        schemaVersion: "cultnet.operation_response.v0",
        messageId: request.messageId,
        serviceId: request.serviceId,
        operation: request.operation,
        status: "denied",
        payloadSchema: cultMeshProviderSessionSchemas.mutationAcceptance,
        payloadEncoding: "messagepack-base64",
        payload: encodeProviderSessionPayload({}),
      };
    },
    onSessionClosed: () => { closedSessions += 1; },
    onError: () => undefined,
  });
  await server.start();

  const socket = createSocket("udp4");
  const rawSend = socket.send.bind(socket);
  const connection = await new CultMeshProviderRudpTransport({
    endpoint: `rudp://127.0.0.1:${server.bind.port}`,
    runtimeId: identity.serviceInstanceId,
    connectionId,
    sessionToken: "signed-provider-token",
    operationTimeoutMs: 100,
    socketFactory: () => socket,
  }).connect(identity, new AbortController().signal);
  try {
    const first = connection.register(
      { identity, requestedLeaseDurationMs: 10_000 },
      new AbortController().signal,
    );
    const second = connection.register(
      { identity, requestedLeaseDurationMs: 10_000 },
      new AbortController().signal,
    );
    await waitFor(() => handlerCalls === 1, "first queued provider operation");

    const replacement = new CultNetRudpSession({ connectionId });
    const replacementWire = encodeRudpPacket(replacement.createConnect(
      Date.now(),
      Buffer.from(encodeProviderConnectEvidence({
        clientSessionId: "replacement-generation",
        sessionToken: "signed-provider-token",
      })),
    ));
    await new Promise<void>((resolve, reject) => {
      rawSend(replacementWire, server.bind.port, "127.0.0.1", error => error ? reject(error) : resolve());
    });
    await waitFor(() => closedSessions === 1, "old RUDP generation closure");
    handlerGate.resolve();
    await new Promise(resolve => setTimeout(resolve, 20));
    assert.equal(handlerCalls, 1, "transport-queued work from the closed generation must be dropped");

    connection.close();
    await Promise.allSettled([first, second]);
  } finally {
    connection.close();
    server.close();
  }
});

function deferred<T>(): { promise: Promise<T>; resolve(value: T): void } {
  let resolve!: (value: T) => void;
  return { promise: new Promise<T>(done => resolve = done), resolve };
}

async function waitFor(predicate: () => boolean, description: string): Promise<void> {
  const startedAt = Date.now();
  while (!predicate()) {
    if (Date.now() - startedAt > 1_000) throw new Error(`Timed out waiting for ${description}.`);
    await new Promise(resolve => setTimeout(resolve, 5));
  }
}
