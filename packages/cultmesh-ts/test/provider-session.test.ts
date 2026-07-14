import test from "node:test";
import assert from "node:assert/strict";
import {
  CultMeshMemoryProviderReceiptStore,
  CultMeshProviderSession,
  type CultMeshProviderCommand,
  type CultMeshProviderCommandListener,
  type CultMeshProviderCommandReceipt,
  type CultMeshProviderConnection,
  type CultMeshProviderIdentity,
  type CultMeshProviderLease,
  type CultMeshProviderPublication,
  type CultMeshProviderRegistration,
  type CultMeshProviderScheduledTask,
  type CultMeshProviderScheduler,
  type CultMeshProviderTransport,
  type CultMeshProviderWithdrawal,
  type CultMeshProviderUnsubscribe,
} from "../src/provider-session";

const identity: CultMeshProviderIdentity = {
  providerId: "voidbot.swarm",
  serviceInstanceId: "voidbot-worker-7",
  endpointId: "odin:voidbot-worker-7",
  verseId: "voidbot.local",
};

const surface: CultMeshProviderPublication = {
  publicationId: "voidbot.swarm.surface",
  documentType: "gamecult.eve.surface_state",
  schemaId: "gamecult.eve.surface_state.v1",
  recordKey: "voidbot.swarm",
  value: { version: 1 },
};

const advertisement: CultMeshProviderPublication = {
  publicationId: "voidbot.swarm.advertisement",
  documentType: "gamecult.eve.provider_advertisement",
  schemaId: "gamecult.eve.provider_advertisement.v1",
  recordKey: "voidbot.swarm",
  value: { providerId: "voidbot.swarm" },
};

test("provider session owns registration, renewal, desired publication, and withdrawal", async () => {
  const scheduler = new ManualScheduler();
  const transport = new FakeTransport(scheduler);
  const states: string[] = [];
  const session = new CultMeshProviderSession({
    identity,
    transport,
    scheduler,
    receiptStore: new CultMeshMemoryProviderReceiptStore(),
    publications: [surface, advertisement],
    leaseDurationMs: 1_000,
    renewalLeadMs: 200,
    reconnectBaseDelayMs: 100,
    reconnectMaxDelayMs: 1_000,
  });
  session.watchState(state => states.push(state.status));

  await session.start();

  const connection = transport.connections[0]!;
  assert.equal(session.state.status, "active");
  assert.deepEqual(connection.registrations[0]?.identity, identity);
  assert.deepEqual(
    connection.publications.map(value => value.publicationId),
    [advertisement.publicationId, surface.publicationId],
    "initial desired state is republished in stable order",
  );
  assert.notEqual(identity.providerId, identity.serviceInstanceId);
  assert.notEqual(identity.serviceInstanceId, identity.endpointId);

  await scheduler.advance(800);
  assert.equal(connection.renewals.length, 1);
  assert.equal(session.state.status, "active");

  const updatedSurface = { ...surface, value: { version: 2 } };
  const finalSurface = { ...surface, value: { version: 3 } };
  const publishBarrier = new Deferred<void>();
  connection.publishBarrier = publishBarrier;
  const updating = session.upsertPublication(updatedSurface);
  const finalizing = session.upsertPublication(finalSurface);
  await settle();
  assert.deepEqual(connection.publications.at(-1), surface, "the second update waits behind the first delivery");
  publishBarrier.resolve();
  await Promise.all([updating, finalizing]);
  assert.deepEqual(
    connection.publications.slice(-2),
    [updatedSurface, finalSurface],
    "desired publication updates preserve caller order on the live connection",
  );

  assert.equal(await session.removePublication(surface.publicationId), true);
  assert.deepEqual(connection.withdrawnPublications, [surface.publicationId]);

  await session.stop();
  assert.equal(connection.closed, true);
  assert.deepEqual(connection.withdrawals[0]?.identity, identity);
  assert.deepEqual(connection.withdrawals[0]?.publicationIds, [advertisement.publicationId]);
  assert.equal(session.state.status, "stopped");
  assert.ok(states.includes("connecting"));
  assert.ok(states.includes("withdrawing"));
});

test("stopping during registration prevents a stale connection from publishing", async () => {
  const scheduler = new ManualScheduler();
  const transport = new FakeTransport(scheduler);
  transport.registerBarrier = new Deferred<void>();
  const session = new CultMeshProviderSession({
    identity,
    transport,
    scheduler,
    receiptStore: new CultMeshMemoryProviderReceiptStore(),
    publications: [advertisement],
    leaseDurationMs: 1_000,
    renewalLeadMs: 200,
  });

  const starting = session.start();
  await settle();
  const connection = transport.connections[0]!;
  const stopping = session.stop();
  transport.registerBarrier.resolve();
  await Promise.all([starting, stopping]);

  assert.equal(session.state.status, "stopped");
  assert.equal(connection.closed, true);
  assert.deepEqual(connection.publications, []);
  assert.deepEqual(connection.withdrawals, []);
});

test("provider session reconnects and republishes desired state after transient failure", async () => {
  const scheduler = new ManualScheduler();
  const transport = new FakeTransport(scheduler);
  transport.failConnectCount = 2;
  const session = new CultMeshProviderSession({
    identity,
    transport,
    scheduler,
    receiptStore: new CultMeshMemoryProviderReceiptStore(),
    publications: [advertisement],
    leaseDurationMs: 1_000,
    renewalLeadMs: 200,
    reconnectBaseDelayMs: 100,
    reconnectMaxDelayMs: 400,
  });

  await session.start();
  assert.equal(session.state.status, "reconnecting");
  assert.equal(session.state.reconnectAttempt, 1);

  await scheduler.advance(100);
  assert.equal(session.state.status, "reconnecting");
  assert.equal(session.state.reconnectAttempt, 2);
  await scheduler.advance(200);
  assert.equal(session.state.status, "active");
  assert.equal(transport.connections.length, 1);
  assert.deepEqual(
    transport.connections[0]?.publications.map(value => value.publicationId),
    [advertisement.publicationId],
  );

  transport.connections[0]!.failNextPublish = new Error("partition");
  await session.upsertPublication(surface);
  assert.equal(session.state.status, "reconnecting");
  assert.match(session.state.lastError ?? "", /partition/);
  assert.equal(transport.connections[0]?.closed, true);

  await scheduler.advance(100);
  assert.equal(session.state.status, "active");
  assert.deepEqual(
    transport.connections[1]?.publications.map(value => value.publicationId),
    [advertisement.publicationId, surface.publicationId],
  );
  await session.stop();
});

test("receipt persistence failure degrades the provider without inventing a transport outage", async () => {
  const scheduler = new ManualScheduler();
  const transport = new FakeTransport(scheduler);
  const session = new CultMeshProviderSession({
    identity,
    transport,
    scheduler,
    receiptStore: {
      get: async () => { throw new Error("receipt store offline"); },
      put: async () => undefined,
      listPending: async () => [],
      markPublished: async () => undefined,
    },
    leaseDurationMs: 1_000,
    renewalLeadMs: 200,
    commandHandlers: {
      "swarm.set_heat": async () => ({ state: "applied" }),
    },
  });
  await session.start();
  const connection = transport.connections[0]!;
  await connection.emit({
    commandId: "heat-store-failure",
    commandKind: "swarm.set_heat",
    providerId: identity.providerId,
    serviceInstanceId: identity.serviceInstanceId,
    payload: 0.5,
  });

  assert.equal(session.state.status, "degraded");
  assert.match(session.state.lastError ?? "", /receipt store offline/);
  assert.equal(connection.closed, false);
  assert.equal(transport.connections.length, 1);
  await session.stop();
});

test("provider session dispatches commands once and republishes typed receipts", async () => {
  const scheduler = new ManualScheduler();
  const transport = new FakeTransport(scheduler);
  let applyCount = 0;
  const session = new CultMeshProviderSession({
    identity,
    transport,
    scheduler,
    receiptStore: new CultMeshMemoryProviderReceiptStore(),
    leaseDurationMs: 1_000,
    renewalLeadMs: 200,
    commandHandlers: {
      "swarm.set_heat": async command => {
        applyCount++;
        await settle();
        return { state: "applied", result: { heat: command.payload } };
      },
    },
  });
  await session.start();
  const connection = transport.connections[0]!;
  const command: CultMeshProviderCommand = {
    commandId: "heat-1",
    commandKind: "swarm.set_heat",
    providerId: identity.providerId,
    serviceInstanceId: identity.serviceInstanceId,
    payload: 0.75,
  };

  await Promise.all([connection.emit(command), connection.emit(command)]);
  await connection.emit(command);

  assert.equal(applyCount, 1);
  assert.equal(connection.receipts.length, 2, "concurrent duplicates share one delivery; later retries republish");
  assert.ok(connection.receipts.every(receipt =>
    receipt.receiptId === "receipt:13:voidbot.swarm16:voidbot-worker-76:heat-1"));
  assert.ok(connection.receipts.every(receipt => receipt.state === "applied"));

  await connection.emit({ ...command, commandId: "unknown-1", commandKind: "swarm.unknown" });
  assert.equal(connection.receipts.at(-1)?.state, "rejected");

  await connection.emit({ ...command, commandId: "wrong-instance", serviceInstanceId: "somebody-else" });
  assert.equal(connection.receipts.length, 3, "commands for another service instance are ignored");
  await session.stop();
});

test("persisted receipts survive a transport failure and leave the outbox only after replay", async () => {
  const scheduler = new ManualScheduler();
  const transport = new FakeTransport(scheduler);
  const receipts = new CultMeshMemoryProviderReceiptStore();
  let applyCount = 0;
  const session = new CultMeshProviderSession({
    identity,
    transport,
    scheduler,
    receiptStore: receipts,
    leaseDurationMs: 1_000,
    renewalLeadMs: 200,
    reconnectBaseDelayMs: 100,
    commandHandlers: {
      "swarm.set_heat": async () => {
        applyCount++;
        return { state: "applied" };
      },
    },
  });
  await session.start();
  transport.connections[0]!.failNextReceipt = new Error("receipt route lost");
  await transport.connections[0]!.emit({
    commandId: "durable-1",
    commandKind: "swarm.set_heat",
    providerId: identity.providerId,
    serviceInstanceId: identity.serviceInstanceId,
    payload: 0.25,
  });

  assert.equal(session.state.status, "reconnecting");
  assert.equal((await receipts.listPending()).length, 1);
  await scheduler.advance(100);
  assert.equal(session.state.status, "active");
  assert.equal(transport.connections[1]!.receipts.length, 1);
  assert.equal((await receipts.listPending()).length, 0);
  assert.equal(applyCount, 1);

  transport.connections[1]!.failNextPublish = new Error("state route lost");
  await session.upsertPublication(surface);
  await scheduler.advance(100);
  assert.equal(transport.connections[2]!.receipts.length, 0, "marked receipts are not replayed again");
  await session.stop();
});

test("initial replay and live publication changes share one ordered lane", async () => {
  const scheduler = new ManualScheduler();
  const transport = new FakeTransport(scheduler);
  transport.publishBarrier = new Deferred<void>();
  const session = new CultMeshProviderSession({
    identity,
    transport,
    scheduler,
    receiptStore: new CultMeshMemoryProviderReceiptStore(),
    publications: [surface],
    leaseDurationMs: 1_000,
    renewalLeadMs: 200,
  });

  const starting = session.start();
  await settle();
  const connection = transport.connections[0]!;
  const updated = { ...surface, value: { version: 2 } };
  const updating = session.upsertPublication(updated);
  const removing = session.removePublication(surface.publicationId);
  transport.publishBarrier.resolve();
  await Promise.all([starting, updating, removing]);

  assert.deepEqual(connection.publications, [surface, updated]);
  assert.deepEqual(connection.withdrawnPublications, [surface.publicationId]);
  assert.deepEqual(session.publications, []);
  await session.stop();
});

test("state observers cannot become lifecycle authorities", async () => {
  const scheduler = new ManualScheduler();
  const transport = new FakeTransport(scheduler);
  const observerErrors: unknown[] = [];
  const observed: string[] = [];
  const session = new CultMeshProviderSession({
    identity,
    transport,
    scheduler,
    receiptStore: new CultMeshMemoryProviderReceiptStore(),
    leaseDurationMs: 1_000,
    renewalLeadMs: 200,
    onObserverError: error => observerErrors.push(error),
  });
  session.watchState(() => { throw new Error("display broken"); });
  session.watchState(state => observed.push(state.status));

  await session.start();
  await session.stop();

  assert.equal(session.state.status, "stopped");
  assert.ok(observerErrors.length >= 3);
  assert.deepEqual(observed, ["stopped", "connecting", "active", "withdrawing", "stopped"]);
});

test("shutdown drains command persistence and always closes after cleanup failures", async () => {
  const scheduler = new ManualScheduler();
  const transport = new FakeTransport(scheduler);
  const handlerBarrier = new Deferred<void>();
  const session = new CultMeshProviderSession({
    identity,
    transport,
    scheduler,
    receiptStore: new CultMeshMemoryProviderReceiptStore(),
    leaseDurationMs: 1_000,
    renewalLeadMs: 200,
    commandHandlers: {
      "swarm.set_heat": async () => {
        await handlerBarrier.promise;
        return { state: "applied" };
      },
    },
  });
  await session.start();
  const connection = transport.connections[0]!;
  const command = connection.emit({
    commandId: "stop-drain-1",
    commandKind: "swarm.set_heat",
    providerId: identity.providerId,
    serviceInstanceId: identity.serviceInstanceId,
    payload: 0.5,
  });
  connection.unsubscribeError = new Error("unsubscribe failed");
  connection.withdrawError = new Error("withdraw failed");
  connection.closeError = new Error("close failed");
  let stopped = false;
  const stopping = session.stop().finally(() => { stopped = true; });
  await settle();
  assert.equal(stopped, false, "shutdown waits for the authoritative command transaction");
  handlerBarrier.resolve();
  await command;
  await assert.rejects(stopping, AggregateError);

  assert.equal(connection.closeAttempts, 1);
  assert.equal(session.state.status, "stopped");
});

test("concurrent reuse of a command id with another kind cannot join the first transaction", async () => {
  const scheduler = new ManualScheduler();
  const transport = new FakeTransport(scheduler);
  const barrier = new Deferred<void>();
  let heatCount = 0;
  let otherCount = 0;
  const session = new CultMeshProviderSession({
    identity,
    transport,
    scheduler,
    receiptStore: new CultMeshMemoryProviderReceiptStore(),
    leaseDurationMs: 1_000,
    renewalLeadMs: 200,
    commandHandlers: {
      "swarm.set_heat": async () => {
        heatCount++;
        await barrier.promise;
        return { state: "applied" };
      },
      "swarm.other": async () => {
        otherCount++;
        return { state: "applied" };
      },
    },
  });
  await session.start();
  const connection = transport.connections[0]!;
  const first = connection.emit({
    commandId: "collision-1",
    commandKind: "swarm.set_heat",
    providerId: identity.providerId,
    serviceInstanceId: identity.serviceInstanceId,
    payload: 0.5,
  });
  await settle();
  await connection.emit({
    commandId: "collision-1",
    commandKind: "swarm.other",
    providerId: identity.providerId,
    serviceInstanceId: identity.serviceInstanceId,
    payload: null,
  });
  assert.equal(session.state.status, "degraded");
  barrier.resolve();
  await first;
  assert.equal(heatCount, 1);
  assert.equal(otherCount, 0);
  await session.stop();
});

test("registration failure closes the connection before retry", async () => {
  const scheduler = new ManualScheduler();
  const transport = new FakeTransport(scheduler);
  transport.registerError = new Error("registration rejected");
  const session = new CultMeshProviderSession({
    identity,
    transport,
    scheduler,
    receiptStore: new CultMeshMemoryProviderReceiptStore(),
    leaseDurationMs: 1_000,
    renewalLeadMs: 200,
    reconnectBaseDelayMs: 100,
  });
  await session.start();
  assert.equal(transport.connections[0]!.closed, true);
  assert.equal(session.state.status, "reconnecting");
  await session.stop();
});

test("an expired replay lease never carries the next pending receipt", async () => {
  const scheduler = new ManualScheduler();
  const transport = new FakeTransport(scheduler);
  const receipts = new CultMeshMemoryProviderReceiptStore();
  const pending = (commandId: string): CultMeshProviderCommandReceipt => ({
    receiptId: `receipt:13:voidbot.swarm16:voidbot-worker-7${commandId.length}:${commandId}`,
    commandId,
    commandKind: "swarm.set_heat",
    providerId: identity.providerId,
    serviceInstanceId: identity.serviceInstanceId,
    state: "applied",
    completedAt: scheduler.now(),
  });
  await receipts.put(pending("one"));
  await receipts.put(pending("two"));
  const session = new CultMeshProviderSession({
    identity,
    transport,
    scheduler,
    receiptStore: receipts,
    leaseDurationMs: 1_000,
    renewalLeadMs: 200,
    reconnectBaseDelayMs: 100,
  });
  transport.onFirstConnection = connection => {
    connection.afterPublishReceipt = async () => scheduler.advance(1_001);
  };

  await session.start();
  assert.equal(session.state.status, "reconnecting");
  assert.deepEqual(
    transport.connections[0]!.receipts.map(value => value.commandId),
    ["one"],
    "the expired lease is rejected before the next receipt",
  );
  assert.deepEqual((await receipts.listPending()).map(value => value.commandId), ["two"]);
  await scheduler.advance(100);
  assert.equal(session.state.status, "active");
  assert.deepEqual(transport.connections[1]!.receipts.map(value => value.commandId), ["two"]);
  assert.equal((await receipts.listPending()).length, 0);
  await session.stop();
});

test("overlapping shutdown callers share cleanup and restart waits for its boundary", async () => {
  const scheduler = new ManualScheduler();
  const transport = new FakeTransport(scheduler);
  const session = new CultMeshProviderSession({
    identity,
    transport,
    scheduler,
    receiptStore: new CultMeshMemoryProviderReceiptStore(),
    leaseDurationMs: 1_000,
    renewalLeadMs: 200,
  });
  await session.start();
  const connection = transport.connections[0]!;
  connection.withdrawBarrier = new Deferred<void>();

  const firstStop = session.stop();
  const secondStop = session.stop();
  assert.equal(firstStop, secondStop, "concurrent callers own one shutdown operation");
  await assert.rejects(session.start(), /shutdown is in progress/);
  await settle();
  assert.equal(connection.closed, false);
  connection.withdrawBarrier.resolve();
  await firstStop;
  assert.equal(session.state.status, "stopped");

  await session.start();
  assert.equal(session.state.status, "active");
  assert.equal(transport.connections.length, 2);
  await session.stop();
});

test("loss cleanup failures cannot suppress reconnect scheduling", async () => {
  const scheduler = new ManualScheduler();
  const transport = new FakeTransport(scheduler);
  const session = new CultMeshProviderSession({
    identity,
    transport,
    scheduler,
    receiptStore: new CultMeshMemoryProviderReceiptStore(),
    leaseDurationMs: 1_000,
    renewalLeadMs: 200,
    reconnectBaseDelayMs: 100,
  });
  await session.start();
  const connection = transport.connections[0]!;
  connection.unsubscribeError = new Error("watch cleanup failed");
  connection.closeError = new Error("route close failed");
  connection.failNextPublish = new Error("route lost");

  await session.upsertPublication(surface);
  assert.equal(session.state.status, "reconnecting");
  assert.match(session.state.lastError ?? "", /watch cleanup failed/);
  assert.match(session.state.lastError ?? "", /route close failed/);
  await scheduler.advance(100);
  assert.equal(session.state.status, "active");
  await session.stop();
});

test("stop during receipt replay cannot publish active state or leave a renewal timer", async () => {
  const scheduler = new ManualScheduler();
  const transport = new FakeTransport(scheduler);
  const listBarrier = new Deferred<void>();
  const states: string[] = [];
  const session = new CultMeshProviderSession({
    identity,
    transport,
    scheduler,
    receiptStore: {
      get: async () => undefined,
      put: async () => undefined,
      listPending: async () => {
        await listBarrier.promise;
        return [];
      },
      markPublished: async () => undefined,
    },
    leaseDurationMs: 1_000,
    renewalLeadMs: 200,
  });
  session.watchState(state => states.push(state.status));
  const starting = session.start();
  await settle();
  const stopping = session.stop();
  await settle();
  const withdrawingIndex = states.lastIndexOf("withdrawing");
  listBarrier.resolve();
  await Promise.all([starting, stopping]);

  assert.equal(session.state.status, "stopped");
  assert.equal(states.slice(withdrawingIndex + 1).includes("active"), false);
  assert.equal(scheduler.pendingTaskCount, 0);
});

class ManualScheduler implements CultMeshProviderScheduler {
  #nowMs = Date.parse("2026-07-14T00:00:00.000Z");
  readonly #tasks: Array<{
    dueAt: number;
    cancelled: boolean;
    action: () => void | Promise<void>;
  }> = [];

  public now(): Date {
    return new Date(this.#nowMs);
  }

  public get pendingTaskCount(): number {
    return this.#tasks.filter(value => !value.cancelled).length;
  }

  public schedule(delayMs: number, action: () => void | Promise<void>): CultMeshProviderScheduledTask {
    const task = { dueAt: this.#nowMs + delayMs, cancelled: false, action };
    this.#tasks.push(task);
    return { cancel: () => { task.cancelled = true; } };
  }

  public async advance(delayMs: number): Promise<void> {
    this.#nowMs += delayMs;
    while (true) {
      const task = this.#tasks
        .filter(value => !value.cancelled && value.dueAt <= this.#nowMs)
        .sort((left, right) => left.dueAt - right.dueAt)[0];
      if (!task) return;
      task.cancelled = true;
      await task.action();
    }
  }
}

class FakeTransport implements CultMeshProviderTransport {
  public failConnectCount = 0;
  public registerBarrier?: Deferred<void>;
  public registerError?: Error;
  public publishBarrier?: Deferred<void>;
  public onFirstConnection?: (connection: FakeConnection) => void;
  public readonly connections: FakeConnection[] = [];

  public constructor(private readonly scheduler: CultMeshProviderScheduler) {}

  public async connect(
    connectedIdentity: CultMeshProviderIdentity,
    signal: AbortSignal,
  ): Promise<CultMeshProviderConnection> {
    assert.deepEqual(connectedIdentity, identity);
    assert.equal(signal.aborted, false);
    if (this.failConnectCount-- > 0) throw new Error("bootstrap unavailable");
    const connection = new FakeConnection(this.scheduler);
    connection.registerBarrier = this.registerBarrier;
    connection.registerError = this.registerError;
    connection.publishBarrier = this.publishBarrier;
    this.connections.push(connection);
    if (this.connections.length === 1) this.onFirstConnection?.(connection);
    return connection;
  }
}

class FakeConnection implements CultMeshProviderConnection {
  public readonly registrations: CultMeshProviderRegistration[] = [];
  public readonly renewals: CultMeshProviderLease[] = [];
  public readonly publications: CultMeshProviderPublication[] = [];
  public readonly withdrawnPublications: string[] = [];
  public readonly receipts: CultMeshProviderCommandReceipt[] = [];
  public readonly withdrawals: CultMeshProviderWithdrawal[] = [];
  public failNextPublish?: Error;
  public failNextReceipt?: Error;
  public registerError?: Error;
  public registerBarrier?: Deferred<void>;
  public publishBarrier?: Deferred<void>;
  public unsubscribeError?: Error;
  public withdrawError?: Error;
  public closeError?: Error;
  public closeAttempts = 0;
  public afterPublishReceipt?: () => void | Promise<void>;
  public withdrawBarrier?: Deferred<void>;
  public closed = false;
  #listener?: CultMeshProviderCommandListener;
  #leaseSequence = 0;

  public constructor(private readonly scheduler: CultMeshProviderScheduler) {}

  public async register(
    registration: CultMeshProviderRegistration,
    signal: AbortSignal,
  ): Promise<CultMeshProviderLease> {
    this.registrations.push(registration);
    if (this.registerBarrier) {
      await Promise.race([
        this.registerBarrier.promise,
        new Promise<never>((_resolve, reject) => {
          signal.addEventListener("abort", () => reject(new Error("registration aborted")), { once: true });
        }),
      ]);
    }
    if (this.registerError) throw this.registerError;
    return this.lease(registration.requestedLeaseDurationMs);
  }

  public async renew(lease: CultMeshProviderLease): Promise<CultMeshProviderLease> {
    this.renewals.push(lease);
    return this.lease(1_000);
  }

  public async publish(publication: CultMeshProviderPublication): Promise<void> {
    if (this.failNextPublish) {
      const error = this.failNextPublish;
      this.failNextPublish = undefined;
      throw error;
    }
    const barrier = this.publishBarrier;
    this.publishBarrier = undefined;
    await barrier?.promise;
    this.publications.push(publication);
  }

  public async withdrawPublication(publicationId: string): Promise<void> {
    this.withdrawnPublications.push(publicationId);
  }

  public watchCommands(listener: CultMeshProviderCommandListener): CultMeshProviderUnsubscribe {
    this.#listener = listener;
    return () => {
      this.#listener = undefined;
      if (this.unsubscribeError) throw this.unsubscribeError;
    };
  }

  public async publishReceipt(receipt: CultMeshProviderCommandReceipt): Promise<void> {
    if (this.failNextReceipt) {
      const error = this.failNextReceipt;
      this.failNextReceipt = undefined;
      throw error;
    }
    this.receipts.push(receipt);
    const afterPublish = this.afterPublishReceipt;
    this.afterPublishReceipt = undefined;
    await afterPublish?.();
  }

  public async withdraw(withdrawal: CultMeshProviderWithdrawal): Promise<void> {
    this.withdrawals.push(withdrawal);
    await this.withdrawBarrier?.promise;
    if (this.withdrawError) throw this.withdrawError;
  }

  public close(): void {
    this.closeAttempts++;
    this.closed = true;
    if (this.closeError) throw this.closeError;
  }

  public async emit(command: CultMeshProviderCommand): Promise<void> {
    await this.#listener?.(command);
  }

  private lease(durationMs: number): CultMeshProviderLease {
    this.#leaseSequence++;
    return {
      leaseId: `lease-${this.#leaseSequence}`,
      expiresAt: new Date(this.scheduler.now().getTime() + durationMs),
    };
  }
}

async function settle(): Promise<void> {
  await new Promise<void>(resolve => setImmediate(resolve));
}

class Deferred<T> {
  public readonly promise: Promise<T>;
  #resolve!: (value: T | PromiseLike<T>) => void;

  public constructor() {
    this.promise = new Promise<T>(resolve => { this.#resolve = resolve; });
  }

  public resolve(value?: T): void {
    this.#resolve(value as T);
  }
}
