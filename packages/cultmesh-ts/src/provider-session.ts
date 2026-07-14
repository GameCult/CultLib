export interface CultMeshProviderIdentity {
  readonly providerId: string;
  readonly serviceInstanceId: string;
  readonly endpointId: string;
  readonly verseId: string;
}

export interface CultMeshProviderLease {
  readonly leaseId: string;
  readonly expiresAt: Date;
}

export interface CultMeshProviderPublication<TValue = unknown> {
  readonly publicationId: string;
  readonly documentType: string;
  readonly schemaId: string;
  readonly recordKey: string;
  readonly value: TValue;
}

export interface CultMeshProviderCommand<TPayload = unknown> {
  readonly commandId: string;
  readonly commandKind: string;
  readonly providerId: string;
  readonly serviceInstanceId: string;
  readonly payload: TPayload;
}

export type CultMeshProviderReceiptState = "applied" | "rejected" | "failed";

export interface CultMeshProviderCommandReceipt<TResult = unknown> {
  readonly receiptId: string;
  readonly commandId: string;
  readonly commandKind: string;
  readonly providerId: string;
  readonly serviceInstanceId: string;
  readonly state: CultMeshProviderReceiptState;
  readonly completedAt: Date;
  readonly result?: TResult;
  readonly error?: string;
}

export interface CultMeshProviderCommandOutcome<TResult = unknown> {
  readonly state: CultMeshProviderReceiptState;
  readonly result?: TResult;
  readonly error?: string;
}

export interface CultMeshProviderCommandContext {
  readonly identity: CultMeshProviderIdentity;
}

export type CultMeshProviderCommandHandler = (
  command: CultMeshProviderCommand,
  context: CultMeshProviderCommandContext,
) => Promise<CultMeshProviderCommandOutcome>;

export interface CultMeshProviderReceiptStore {
  get(commandId: string): Promise<CultMeshProviderCommandReceipt | undefined>;
  put(receipt: CultMeshProviderCommandReceipt): Promise<void>;
  listPending(): Promise<readonly CultMeshProviderCommandReceipt[]>;
  markPublished(receiptId: string): Promise<void>;
}

export class CultMeshMemoryProviderReceiptStore implements CultMeshProviderReceiptStore {
  readonly #receipts = new Map<string, CultMeshProviderCommandReceipt>();
  readonly #pendingReceiptIds = new Set<string>();

  public async get(commandId: string): Promise<CultMeshProviderCommandReceipt | undefined> {
    return this.#receipts.get(commandId);
  }

  public async put(receipt: CultMeshProviderCommandReceipt): Promise<void> {
    this.#receipts.set(receipt.commandId, receipt);
    this.#pendingReceiptIds.add(receipt.receiptId);
  }

  public async listPending(): Promise<readonly CultMeshProviderCommandReceipt[]> {
    return [...this.#receipts.values()]
      .filter(receipt => this.#pendingReceiptIds.has(receipt.receiptId))
      .sort((left, right) => left.receiptId.localeCompare(right.receiptId));
  }

  public async markPublished(receiptId: string): Promise<void> {
    this.#pendingReceiptIds.delete(receiptId);
  }
}

export interface CultMeshProviderRegistration {
  readonly identity: CultMeshProviderIdentity;
  readonly requestedLeaseDurationMs: number;
}

export interface CultMeshProviderWithdrawal {
  readonly identity: CultMeshProviderIdentity;
  readonly leaseId: string;
  readonly publicationIds: readonly string[];
}

export type CultMeshProviderCommandListener = (
  command: CultMeshProviderCommand,
) => void | Promise<void>;

export type CultMeshProviderUnsubscribe = () => void | Promise<void>;

export interface CultMeshProviderConnection {
  register(
    registration: CultMeshProviderRegistration,
    signal: AbortSignal,
  ): Promise<CultMeshProviderLease>;
  renew(lease: CultMeshProviderLease): Promise<CultMeshProviderLease>;
  publish(
    publication: CultMeshProviderPublication,
    lease: CultMeshProviderLease,
  ): Promise<void>;
  withdrawPublication(publicationId: string, lease: CultMeshProviderLease): Promise<void>;
  watchCommands(listener: CultMeshProviderCommandListener): CultMeshProviderUnsubscribe;
  publishReceipt(
    receipt: CultMeshProviderCommandReceipt,
    lease: CultMeshProviderLease,
  ): Promise<void>;
  withdraw(withdrawal: CultMeshProviderWithdrawal): Promise<void>;
  close(): void | Promise<void>;
}

export interface CultMeshProviderTransport {
  connect(
    identity: CultMeshProviderIdentity,
    signal: AbortSignal,
  ): Promise<CultMeshProviderConnection>;
}

export interface CultMeshProviderScheduledTask {
  cancel(): void;
}

export interface CultMeshProviderScheduler {
  now(): Date;
  schedule(delayMs: number, action: () => void | Promise<void>): CultMeshProviderScheduledTask;
}

export class CultMeshSystemProviderScheduler implements CultMeshProviderScheduler {
  public now(): Date {
    return new Date();
  }

  public schedule(delayMs: number, action: () => void | Promise<void>): CultMeshProviderScheduledTask {
    const timer = setTimeout(() => {
      void Promise.resolve(action()).catch(() => undefined);
    }, Math.max(0, delayMs));
    return { cancel: () => clearTimeout(timer) };
  }
}

export type CultMeshProviderSessionStatus =
  | "stopped"
  | "connecting"
  | "active"
  | "degraded"
  | "reconnecting"
  | "withdrawing";

export interface CultMeshProviderSessionState {
  readonly status: CultMeshProviderSessionStatus;
  readonly identity: CultMeshProviderIdentity;
  readonly reconnectAttempt: number;
  readonly leaseId?: string;
  readonly leaseExpiresAt?: Date;
  readonly lastError?: string;
}

export interface CultMeshProviderSessionOptions {
  readonly identity: CultMeshProviderIdentity;
  readonly transport: CultMeshProviderTransport;
  readonly publications?: Iterable<CultMeshProviderPublication>;
  readonly commandHandlers?: Readonly<Record<string, CultMeshProviderCommandHandler>>;
  readonly receiptStore: CultMeshProviderReceiptStore;
  readonly scheduler?: CultMeshProviderScheduler;
  readonly onObserverError?: (error: unknown) => void;
  readonly leaseDurationMs?: number;
  readonly renewalLeadMs?: number;
  readonly reconnectBaseDelayMs?: number;
  readonly reconnectMaxDelayMs?: number;
}

export class CultMeshProviderSession {
  readonly #identity: CultMeshProviderIdentity;
  readonly #transport: CultMeshProviderTransport;
  readonly #receiptStore: CultMeshProviderReceiptStore;
  readonly #scheduler: CultMeshProviderScheduler;
  readonly #onObserverError: (error: unknown) => void;
  readonly #leaseDurationMs: number;
  readonly #renewalLeadMs: number;
  readonly #reconnectBaseDelayMs: number;
  readonly #reconnectMaxDelayMs: number;
  readonly #publications = new Map<string, CultMeshProviderPublication>();
  readonly #handlers = new Map<string, CultMeshProviderCommandHandler>();
  readonly #subscribers = new Set<(state: CultMeshProviderSessionState) => void>();
  readonly #inFlightCommands = new Map<string, {
    command: CultMeshProviderCommand;
    work: Promise<void>;
  }>();
  #state: CultMeshProviderSessionState;
  #connection?: CultMeshProviderConnection;
  #lease?: CultMeshProviderLease;
  #unsubscribeCommands?: CultMeshProviderUnsubscribe;
  #scheduled?: CultMeshProviderScheduledTask;
  #abort?: AbortController;
  #running = false;
  #generation = 0;
  #reconnectAttempt = 0;
  #publicationWork: Promise<void> = Promise.resolve();
  #startWork?: Promise<void>;
  #stopWork?: Promise<void>;
  #lossWork?: { generation: number; work: Promise<void> };

  public constructor(options: CultMeshProviderSessionOptions) {
    this.#identity = validateIdentity(options.identity);
    this.#transport = options.transport;
    if (!options.receiptStore) {
      throw new Error(
        "CultMesh provider sessions require an explicit receiptStore so command receipts survive the provider lifecycle.",
      );
    }
    this.#receiptStore = options.receiptStore;
    this.#scheduler = options.scheduler ?? new CultMeshSystemProviderScheduler();
    this.#onObserverError = options.onObserverError ?? (() => undefined);
    this.#leaseDurationMs = positive(options.leaseDurationMs ?? 30_000, "leaseDurationMs");
    this.#renewalLeadMs = nonNegative(options.renewalLeadMs ?? 10_000, "renewalLeadMs");
    if (this.#renewalLeadMs >= this.#leaseDurationMs) {
      throw new Error("CultMesh provider renewalLeadMs must be less than leaseDurationMs.");
    }
    this.#reconnectBaseDelayMs = positive(
      options.reconnectBaseDelayMs ?? 250,
      "reconnectBaseDelayMs",
    );
    this.#reconnectMaxDelayMs = positive(
      options.reconnectMaxDelayMs ?? 10_000,
      "reconnectMaxDelayMs",
    );
    if (this.#reconnectMaxDelayMs < this.#reconnectBaseDelayMs) {
      throw new Error("CultMesh provider reconnectMaxDelayMs must not be less than reconnectBaseDelayMs.");
    }
    for (const publication of options.publications ?? []) {
      this.#publications.set(validatePublication(publication).publicationId, publication);
    }
    for (const [kind, handler] of Object.entries(options.commandHandlers ?? {})) {
      this.setCommandHandler(kind, handler);
    }
    this.#state = {
      status: "stopped",
      identity: this.#identity,
      reconnectAttempt: 0,
    };
  }

  public get identity(): CultMeshProviderIdentity {
    return this.#identity;
  }

  public get state(): CultMeshProviderSessionState {
    return this.#state;
  }

  public get publications(): readonly CultMeshProviderPublication[] {
    return [...this.#publications.values()].sort((left, right) =>
      left.publicationId.localeCompare(right.publicationId),
    );
  }

  public watchState(callback: (state: CultMeshProviderSessionState) => void): () => void {
    this.#subscribers.add(callback);
    try {
      callback(this.#state);
    } catch (error) {
      this.#reportObserverError(error);
    }
    return () => this.#subscribers.delete(callback);
  }

  public setCommandHandler(kind: string, handler: CultMeshProviderCommandHandler): void {
    this.#handlers.set(required(kind, "command kind"), handler);
  }

  public removeCommandHandler(kind: string): boolean {
    return this.#handlers.delete(required(kind, "command kind"));
  }

  public upsertPublication(publication: CultMeshProviderPublication): Promise<void> {
    const valid = validatePublication(publication);
    this.#publications.set(valid.publicationId, valid);
    if (this.#connection && this.#lease) {
      return this.#enqueuePublication(() => this.#deliverPublication(valid, this.#generation));
    }
    return Promise.resolve();
  }

  public async removePublication(publicationId: string): Promise<boolean> {
    const id = required(publicationId, "publicationId");
    const removed = this.#publications.delete(id);
    if (removed && this.#connection && this.#lease) {
      await this.#enqueuePublication(() => this.#withdrawPublication(id, this.#generation));
    }
    return removed;
  }

  public start(): Promise<void> {
    if (this.#stopWork) {
      return Promise.reject(new Error("CultMesh provider cannot start while shutdown is in progress."));
    }
    if (this.#startWork) return this.#startWork;
    if (this.#running) return Promise.resolve();
    let tracked!: Promise<void>;
    tracked = Promise.resolve().then(() => this.#start()).finally(() => {
      if (this.#startWork === tracked) this.#startWork = undefined;
    });
    this.#startWork = tracked;
    return tracked;
  }

  async #start(): Promise<void> {
    this.#running = true;
    this.#reconnectAttempt = 0;
    const generation = ++this.#generation;
    this.#abort = new AbortController();
    await this.#connect(generation, false);
  }

  public stop(): Promise<void> {
    if (this.#stopWork) return this.#stopWork;
    if (!this.#running && !this.#startWork && this.#state.status === "stopped") {
      return Promise.resolve();
    }
    let tracked!: Promise<void>;
    tracked = Promise.resolve().then(() => this.#stop()).finally(() => {
      if (this.#stopWork === tracked) this.#stopWork = undefined;
    });
    this.#stopWork = tracked;
    return tracked;
  }

  async #stop(): Promise<void> {
    this.#running = false;
    const generation = ++this.#generation;
    this.#cancelScheduled();
    this.#abort?.abort();
    this.#setState("withdrawing");
    const connection = this.#connection;
    const lease = this.#lease;
    const starting = this.#startWork;
    const losing = this.#lossWork?.work;
    this.#connection = undefined;
    this.#lease = undefined;
    const errors: unknown[] = [];
    try {
      await this.#unsubscribe();
    } catch (error) {
      errors.push(error);
    }
    try {
      await this.#publicationWork;
    } catch (error) {
      errors.push(error);
    }
    for (const lifecycleWork of [starting, losing]) {
      if (!lifecycleWork) continue;
      try {
        await lifecycleWork;
      } catch (error) {
        errors.push(error);
      }
    }
    this.#cancelScheduled();
    const commandWork = [...this.#inFlightCommands.values()].map(value => value.work);
    const commandResults = await Promise.allSettled(commandWork);
    for (const result of commandResults) {
      if (result.status === "rejected") errors.push(result.reason);
    }
    if (connection && lease) {
      try {
        await connection.withdraw({
          identity: this.#identity,
          leaseId: lease.leaseId,
          publicationIds: this.publications.map(value => value.publicationId),
        });
      } catch (error) {
        errors.push(error);
      }
    }
    if (connection) {
      try {
        await connection.close();
      } catch (error) {
        errors.push(error);
      }
    }
    if (generation === this.#generation) this.#setState("stopped");
    if (errors.length > 0) {
      throw new AggregateError(errors, "CultMesh provider shutdown completed with cleanup failures.");
    }
  }

  async #connect(generation: number, reconnecting: boolean): Promise<void> {
    if (!this.#isCurrent(generation)) return;
    this.#scheduled = undefined;
    this.#setState(reconnecting ? "reconnecting" : "connecting");
    try {
      const connection = await this.#transport.connect(this.#identity, this.#abort!.signal);
      if (!this.#isCurrent(generation)) {
        await connection.close();
        return;
      }
      this.#connection = connection;
      const lease = validateLease(
        await connection.register({
          identity: this.#identity,
          requestedLeaseDurationMs: this.#leaseDurationMs,
        }, this.#abort!.signal),
        this.#scheduler.now(),
      );
      if (!this.#isCurrent(generation)) {
        await connection.close();
        return;
      }
      this.#lease = lease;
      const initialPublications = this.publications;
      await this.#enqueuePublication(async () => {
        for (const publication of initialPublications) {
          if (!this.#isCurrent(generation)) return;
          requireUnexpiredLease(lease, this.#scheduler.now());
          await connection.publish(publication, lease);
        }
      });
      if (!this.#isCurrent(generation)) return;
      const readyLease = await this.#renewIfNeeded(connection, lease, generation);
      if (!readyLease || !this.#isCurrent(generation)) return;
      this.#lease = readyLease;
      this.#reconnectAttempt = 0;
      const receiptsReady = await this.#drainPendingReceipts(connection, readyLease, generation);
      if (!this.#isCurrent(generation)) return;
      if (receiptsReady) {
        this.#enableCommands(connection, generation);
        this.#setState("active", undefined, readyLease);
      }
      this.#scheduleRenewal(generation, readyLease);
    } catch (error) {
      await this.#loseConnection(generation, error);
    }
  }

  #scheduleRenewal(generation: number, lease: CultMeshProviderLease): void {
    if (!this.#isCurrent(generation)) return;
    this.#scheduled?.cancel();
    const delay = Math.max(
      1,
      lease.expiresAt.getTime() - this.#scheduler.now().getTime() - this.#renewalLeadMs,
    );
    this.#scheduled = this.#scheduler.schedule(delay, () => this.#renew(generation));
  }

  async #renew(generation: number): Promise<void> {
    if (!this.#isCurrent(generation) || !this.#connection || !this.#lease) return;
    this.#scheduled = undefined;
    try {
      let lease = validateLease(
        await this.#connection.renew(this.#lease),
        this.#scheduler.now(),
      );
      if (!this.#isCurrent(generation)) return;
      this.#lease = lease;
      const receiptsReady = await this.#drainPendingReceipts(this.#connection, lease, generation);
      if (!this.#isCurrent(generation)) return;
      if (receiptsReady) {
        lease = await this.#renewIfNeeded(this.#connection, lease, generation) ?? lease;
        if (!this.#isCurrent(generation)) return;
        this.#lease = lease;
        this.#enableCommands(this.#connection, generation);
        this.#setState("active", undefined, lease);
      }
      this.#scheduleRenewal(generation, lease);
    } catch (error) {
      await this.#loseConnection(generation, error);
    }
  }

  async #deliverPublication(
    publication: CultMeshProviderPublication,
    generation: number,
  ): Promise<void> {
    const connection = this.#connection;
    const lease = this.#lease;
    if (!connection || !lease || !this.#isCurrent(generation)) return;
    try {
      requireUnexpiredLease(lease, this.#scheduler.now());
      await connection.publish(publication, lease);
    } catch (error) {
      await this.#loseConnection(generation, error);
    }
  }

  async #withdrawPublication(publicationId: string, generation: number): Promise<void> {
    const connection = this.#connection;
    const lease = this.#lease;
    if (!connection || !lease || !this.#isCurrent(generation)) return;
    try {
      requireUnexpiredLease(lease, this.#scheduler.now());
      await connection.withdrawPublication(publicationId, lease);
    } catch (error) {
      await this.#loseConnection(generation, error);
    }
  }

  async #receiveCommand(command: CultMeshProviderCommand, generation: number): Promise<void> {
    if (!this.#isCurrent(generation) ||
        command.providerId !== this.#identity.providerId ||
        command.serviceInstanceId !== this.#identity.serviceInstanceId) {
      return;
    }
    try {
      required(command.commandId, "commandId");
      required(command.commandKind, "commandKind");
    } catch (error) {
      this.#setState("degraded", errorText(error), this.#lease);
      return;
    }
    const inFlight = this.#inFlightCommands.get(command.commandId);
    if (inFlight) {
      if (!sameCommandIdentity(inFlight.command, command)) {
        this.#setState(
          "degraded",
          `CultMesh command id '${command.commandId}' was reused with different identity while in flight.`,
          this.#lease,
        );
        return;
      }
      await inFlight.work;
      return;
    }
    const work = this.#processCommand(command, generation);
    const entry = { command, work };
    this.#inFlightCommands.set(command.commandId, entry);
    try {
      await work;
    } finally {
      if (this.#inFlightCommands.get(command.commandId) === entry) {
        this.#inFlightCommands.delete(command.commandId);
      }
    }
  }

  async #processCommand(command: CultMeshProviderCommand, generation: number): Promise<void> {
    let receipt: CultMeshProviderCommandReceipt;
    try {
      receipt = await this.#resolveCommand(command);
    } catch (error) {
      if (this.#isCurrent(generation)) {
        await this.#degradeReceiptStore(
          `Command receipt persistence failed: ${errorText(error)}`,
          generation,
        );
      }
      return;
    }
    const connection = this.#connection;
    const lease = this.#lease;
    if (!connection || !lease || !this.#isCurrent(generation)) return;
    try {
      await connection.publishReceipt(receipt, lease);
    } catch (error) {
      await this.#loseConnection(generation, error);
      return;
    }
    try {
      await this.#receiptStore.markPublished(receipt.receiptId);
    } catch (error) {
      if (this.#isCurrent(generation)) {
        await this.#degradeReceiptStore(
          `Command receipt publication could not be committed: ${errorText(error)}`,
          generation,
        );
      }
    }
  }

  async #resolveCommand(command: CultMeshProviderCommand): Promise<CultMeshProviderCommandReceipt> {
    const prior = await this.#receiptStore.get(command.commandId);
    if (prior) {
      validateStoredReceipt(prior, this.#identity);
      if (prior.commandKind !== command.commandKind) {
        throw new Error(`CultMesh command id '${command.commandId}' was reused with different identity.`);
      }
      return prior;
    }
    const handler = this.#handlers.get(command.commandKind);
    let outcome: CultMeshProviderCommandOutcome;
    if (!handler) {
      outcome = { state: "rejected", error: `No handler for '${command.commandKind}'.` };
    } else {
      try {
        outcome = await handler(command, { identity: this.#identity });
        validateOutcomeState(outcome?.state);
      } catch (error) {
        outcome = { state: "failed", error: errorText(error) };
      }
    }
    const receipt: CultMeshProviderCommandReceipt = {
      receiptId: receiptId(this.#identity, command.commandId),
      commandId: command.commandId,
      commandKind: command.commandKind,
      providerId: this.#identity.providerId,
      serviceInstanceId: this.#identity.serviceInstanceId,
      state: validateOutcomeState(outcome.state),
      completedAt: this.#scheduler.now(),
      result: outcome.result,
      error: outcome.error,
    };
    await this.#receiptStore.put(receipt);
    return receipt;
  }

  async #loseConnection(generation: number, error: unknown): Promise<void> {
    if (!this.#isCurrent(generation)) return;
    const existing = this.#lossWork;
    if (existing?.generation === generation) {
      await existing.work;
      return;
    }
    let tracked!: Promise<void>;
    tracked = this.#performConnectionLoss(generation, error).finally(() => {
      if (this.#lossWork?.work === tracked) this.#lossWork = undefined;
    });
    this.#lossWork = { generation, work: tracked };
    await tracked;
  }

  async #performConnectionLoss(generation: number, error: unknown): Promise<void> {
    this.#cancelScheduled();
    const connection = this.#connection;
    this.#connection = undefined;
    this.#lease = undefined;
    this.#reconnectAttempt++;
    const failures = [errorText(error)];
    this.#setState("reconnecting", failures[0]);
    try {
      await this.#unsubscribe();
    } catch (cleanupError) {
      failures.push(`unsubscribe failed: ${errorText(cleanupError)}`);
    }
    if (connection) {
      try {
        await connection.close();
      } catch (cleanupError) {
        failures.push(`close failed: ${errorText(cleanupError)}`);
      }
    }
    if (!this.#isCurrent(generation)) return;
    this.#setState("reconnecting", failures.join("; "));
    const exponent = Math.max(0, this.#reconnectAttempt - 1);
    const delay = Math.min(
      this.#reconnectMaxDelayMs,
      this.#reconnectBaseDelayMs * 2 ** exponent,
    );
    this.#scheduled = this.#scheduler.schedule(delay, () => this.#connect(generation, true));
  }

  async #drainPendingReceipts(
    connection: CultMeshProviderConnection,
    lease: CultMeshProviderLease,
    generation: number,
  ): Promise<boolean> {
    let pending: readonly CultMeshProviderCommandReceipt[];
    try {
      pending = await this.#receiptStore.listPending();
    } catch (error) {
      if (this.#isCurrent(generation)) {
        await this.#degradeReceiptStore(
          `Pending command receipts could not be read: ${errorText(error)}`,
          generation,
        );
      }
      return false;
    }
    for (const receipt of [...pending].sort((left, right) => left.receiptId.localeCompare(right.receiptId))) {
      try {
        validateStoredReceipt(receipt, this.#identity);
      } catch (error) {
        if (this.#isCurrent(generation)) {
          await this.#degradeReceiptStore(
            `Pending command receipt is invalid: ${errorText(error)}`,
            generation,
          );
        }
        return false;
      }
      if (!this.#isCurrent(generation)) return false;
      requireUnexpiredLease(lease, this.#scheduler.now());
      await connection.publishReceipt(receipt, lease);
      try {
        await this.#receiptStore.markPublished(receipt.receiptId);
      } catch (error) {
        if (this.#isCurrent(generation)) {
          await this.#degradeReceiptStore(
            `Command receipt publication could not be committed: ${errorText(error)}`,
            generation,
          );
        }
        return false;
      }
    }
    return true;
  }

  async #renewIfNeeded(
    connection: CultMeshProviderConnection,
    lease: CultMeshProviderLease,
    generation: number,
  ): Promise<CultMeshProviderLease | undefined> {
    if (lease.expiresAt.getTime() - this.#scheduler.now().getTime() > this.#renewalLeadMs) {
      return lease;
    }
    const renewed = validateLease(await connection.renew(lease), this.#scheduler.now());
    return this.#isCurrent(generation) ? renewed : undefined;
  }

  #enableCommands(connection: CultMeshProviderConnection, generation: number): void {
    if (this.#unsubscribeCommands || !this.#isCurrent(generation)) return;
    this.#unsubscribeCommands = connection.watchCommands(command => this.#receiveCommand(command, generation));
  }

  async #degradeReceiptStore(message: string, generation: number): Promise<void> {
    try {
      await this.#unsubscribe();
    } catch (error) {
      this.#reportObserverError(error);
    }
    if (this.#isCurrent(generation)) this.#setState("degraded", message, this.#lease);
  }

  async #unsubscribe(): Promise<void> {
    const unsubscribe = this.#unsubscribeCommands;
    this.#unsubscribeCommands = undefined;
    if (unsubscribe) await unsubscribe();
  }

  #cancelScheduled(): void {
    const scheduled = this.#scheduled;
    this.#scheduled = undefined;
    scheduled?.cancel();
  }

  #enqueuePublication(action: () => Promise<void>): Promise<void> {
    const work = this.#publicationWork.then(action, action);
    this.#publicationWork = work.catch(() => undefined);
    return work;
  }

  #isCurrent(generation: number): boolean {
    return this.#running && generation === this.#generation;
  }

  #setState(
    status: CultMeshProviderSessionStatus,
    lastError?: string,
    lease?: CultMeshProviderLease,
  ): void {
    this.#state = {
      status,
      identity: this.#identity,
      reconnectAttempt: this.#reconnectAttempt,
      leaseId: lease?.leaseId,
      leaseExpiresAt: lease?.expiresAt,
      lastError,
    };
    for (const subscriber of [...this.#subscribers]) {
      try {
        subscriber(this.#state);
      } catch (error) {
        this.#reportObserverError(error);
      }
    }
  }

  #reportObserverError(error: unknown): void {
    try {
      this.#onObserverError(error);
    } catch {
      // Diagnostics cannot become a provider lifecycle authority.
    }
  }
}

function validateIdentity(identity: CultMeshProviderIdentity): CultMeshProviderIdentity {
  required(identity.providerId, "providerId");
  required(identity.serviceInstanceId, "serviceInstanceId");
  required(identity.endpointId, "endpointId");
  required(identity.verseId, "verseId");
  return identity;
}

function validatePublication(publication: CultMeshProviderPublication): CultMeshProviderPublication {
  required(publication.publicationId, "publicationId");
  required(publication.documentType, "documentType");
  required(publication.schemaId, "schemaId");
  required(publication.recordKey, "recordKey");
  return publication;
}

function validateLease(lease: CultMeshProviderLease, now: Date): CultMeshProviderLease {
  required(lease.leaseId, "leaseId");
  if (!(lease.expiresAt instanceof Date) || !Number.isFinite(lease.expiresAt.getTime())) {
    throw new Error("CultMesh provider lease requires a valid expiresAt date.");
  }
  if (lease.expiresAt <= now) {
    throw new Error("CultMesh provider lease must expire in the future.");
  }
  return lease;
}

function requireUnexpiredLease(lease: CultMeshProviderLease, now: Date): void {
  if (lease.expiresAt <= now) {
    throw new Error(`CultMesh provider lease '${lease.leaseId}' expired during replay.`);
  }
}

function validateOutcomeState(state: CultMeshProviderReceiptState): CultMeshProviderReceiptState {
  if (state !== "applied" && state !== "rejected" && state !== "failed") {
    throw new Error(`CultMesh provider command handler returned invalid receipt state '${String(state)}'.`);
  }
  return state;
}

function sameCommandIdentity(
  left: CultMeshProviderCommand,
  right: CultMeshProviderCommand,
): boolean {
  return left.providerId === right.providerId &&
    left.serviceInstanceId === right.serviceInstanceId &&
    left.commandKind === right.commandKind;
}

function validateStoredReceipt(
  receipt: CultMeshProviderCommandReceipt,
  identity: CultMeshProviderIdentity,
): void {
  required(receipt.commandId, "stored receipt commandId");
  required(receipt.commandKind, "stored receipt commandKind");
  required(receipt.receiptId, "stored receipt receiptId");
  validateOutcomeState(receipt.state);
  if (receipt.providerId !== identity.providerId ||
      receipt.serviceInstanceId !== identity.serviceInstanceId ||
      receipt.receiptId !== receiptId(identity, receipt.commandId)) {
    throw new Error(`CultMesh stored receipt '${receipt.receiptId}' does not belong to this provider session.`);
  }
}

function receiptId(identity: CultMeshProviderIdentity, commandId: string): string {
  const parts = [identity.providerId, identity.serviceInstanceId, commandId];
  return `receipt:${parts.map(value => `${value.length}:${value}`).join("")}`;
}

function required(value: string, name: string): string {
  if (!value || value.trim().length === 0) {
    throw new Error(`CultMesh provider ${name} must be non-empty.`);
  }
  return value;
}

function positive(value: number, name: string): number {
  if (!Number.isFinite(value) || value <= 0) {
    throw new Error(`CultMesh provider ${name} must be positive.`);
  }
  return value;
}

function nonNegative(value: number, name: string): number {
  if (!Number.isFinite(value) || value < 0) {
    throw new Error(`CultMesh provider ${name} must be non-negative.`);
  }
  return value;
}

function errorText(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
