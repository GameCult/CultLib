import test from "node:test";
import assert from "node:assert/strict";
import { decode } from "@msgpack/msgpack";
import { CultNetDocumentRegistry } from "cultnet-ts";

import {
  CultMesh,
  CultMeshProviderRudpTransport,
  CultMeshProviderSessionBroker,
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
    authorizeRegistration: () => true,
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

  const transport = new CultMeshProviderRudpTransport({
    endpoint: `rudp://127.0.0.1:${server.bind.port}`,
    runtimeId: identity.serviceInstanceId,
    connectionId: 0x43554c54,
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

function deferred<T>(): { promise: Promise<T>; resolve(value: T): void } {
  let resolve!: (value: T) => void;
  return { promise: new Promise<T>(done => resolve = done), resolve };
}
