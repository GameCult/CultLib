# 4. Keep A TypeScript Provider Alive

A provider process should declare desired typed publications and domain command
handlers once. It should not rebuild discovery registration, lease renewal,
reconnect, republish, receipt delivery, and withdrawal loops in a renderer or
scheduled export script.

`CultMeshProviderSession` owns that lifecycle. The application still owns its
state and command transaction. A transport connector owns the physical CultNet
route. Eve advertisements and surfaces are ordinary typed publications; the
session does not import Eve or learn provider gameplay.

```ts
import {
  CultMeshProviderSession,
  type CultMeshProviderReceiptStore,
  type CultMeshProviderTransport,
} from "cultmesh-ts";

export async function runProvider(
  transport: CultMeshProviderTransport,
  receipts: CultMeshProviderReceiptStore,
) {
  const provider = new CultMeshProviderSession({
    identity: {
      providerId: "voidbot.swarm",
      serviceInstanceId: "voidbot-worker-7",
      endpointId: "odin:voidbot-worker-7",
      verseId: "voidbot.local",
    },
    transport,
    receiptStore: receipts,
    publications: [
      {
        publicationId: "voidbot.swarm.advertisement",
        documentType: "gamecult.eve.provider_advertisement",
        schemaId: "gamecult.eve.provider_advertisement.v1",
        recordKey: "voidbot.swarm",
        value: providerAdvertisement,
      },
      {
        publicationId: "voidbot.swarm.surface",
        documentType: "gamecult.eve.surface_state",
        schemaId: "gamecult.eve.surface_state.v1",
        recordKey: "voidbot.swarm",
        value: currentSurface,
      },
    ],
    commandHandlers: {
      "swarm.set_heat": async command =>
        applyHeatCommandTransaction(command.commandId, command.payload),
    },
  });

  await provider.start();

  await provider.upsertPublication({
    publicationId: "voidbot.swarm.surface",
    documentType: "gamecult.eve.surface_state",
    schemaId: "gamecult.eve.surface_state.v1",
    recordKey: "voidbot.swarm",
    value: nextSurface,
  });

  process.once("SIGTERM", () => {
    provider.stop().catch(error => {
      console.error("CultMesh provider shutdown completed with cleanup failures.", error);
      process.exitCode = 1;
    });
  });
}
```

The four identities are deliberately distinct:

- `providerId` names the product capability clients select;
- `serviceInstanceId` names the running owner incarnation;
- `endpointId` names the stable discovery/session route.
- `verseId` names the discovery and state-sharing domain.

Do not replace any of them with a process id, socket address, body producer id,
or renderer id.

## Receipt Rule

The receipt store is required and must be durable in a daemon. The in-memory
store exported by the package is for tests and disposable tools only. The
domain command handler must apply its mutation idempotently using `commandId`;
the provider session deduplicates delivery and republishes the stored typed
receipt, but it cannot make an unrelated database mutation atomic by wishing
at it harder. `commandId` is the idempotency key: later delivery of the same
provider, service-instance, and command kind returns the original receipt even
if a sender incorrectly changes the payload. Reusing the ID for another kind
or identity is rejected as a conflict.

The store is also the receipt outbox. `put` commits the receipt before any wire
publication. `listPending` supplies receipts after startup or reconnect, and
`markPublished` removes one only after the active connection accepts it. Scope
the store to the provider and service-instance identity. If the store is
unavailable, the session enters `degraded` and stops accepting commands until a
renewal can drain the outbox again; a store failure is not a transport failure.

## Verification Checkpoint

Use an injected scheduler and transport fault double to prove the actual
lifecycle:

1. initial registration publishes every desired document in stable order;
2. lease renewal occurs before expiry;
3. a failed route produces `reconnecting`, then republishes current desired
   state on the replacement connection;
4. duplicate command delivery runs one handler transaction and republishes the
   same receipt;
5. failed receipt transport reconnects and replays the durable outbox exactly
   until `markPublished` succeeds;
6. shutdown drains command persistence, withdraws the lease and remaining
   publications, and closes even when cleanup reports failures;
7. stopping during registration cannot let the stale connection publish;
8. observers and conflicting command IDs cannot become lifecycle authorities.

The package suite contains this timeline in
`packages/cultmesh-ts/test/provider-session.test.ts`.
