import assert from "node:assert/strict";
import test from "node:test";

import { decode, encode } from "@msgpack/msgpack";
import {
  encodeCultNetMessageForWire,
  parseCultNetMessage,
  type CultNetDatabaseSubscribeMessage,
  type CultNetMessage,
  type CultNetOperationRequestMessage,
  type CultNetRawDocumentRecord,
  type CultMeshSessionOpenMessage,
} from "cultnet-ts/contracts";

import {
  CultMeshBrowserClient,
  CultMeshBrowserOperationError,
  CultMeshBrowserOdinRendezvous,
  decodeCultNetOperationPayload,
  decodeCultNetPayload,
  type CultMeshBrowserRoute,
  type CultMeshBrowserSocket,
} from "../src/index.js";

test("Odin rendezvous resolves stable authority-runtime identity and observes route rotation", async () => {
  const odin = new FakeOdin("ws://127.0.0.1:4050/mesh");
  let id = 0;
  const rendezvous = new CultMeshBrowserOdinRendezvous({
    endpoints: ["ws://127.0.0.1:4040/odin"],
    runtimeId: "browser-odin-test",
    createId: () => `odin-${++id}`,
    socketFactory: () => odin.open(),
  });

  assert.equal((await rendezvous.resolve({
    verseId: "sample.counter",
    authorityRuntimeId: "sample.counter-daemon",
  })).endpoint, "ws://127.0.0.1:4050/mesh");

  odin.providerEndpoint = "ws://127.0.0.1:4060/mesh";
  assert.equal((await rendezvous.resolve({
    verseId: "sample.counter",
    authorityRuntimeId: "sample.counter-daemon",
  })).endpoint, "ws://127.0.0.1:4060/mesh");

  await assert.rejects(
    rendezvous.resolve({ verseId: "sample.counter", authorityRuntimeId: "other-runtime" }),
    /could not resolve/,
  );
});

test("Odin rendezvous never selects another authority's faster endpoint", async () => {
  const odin = new FakeOdin("ws://127.0.0.1:4050/mesh");
  odin.decoyEndpoint = "ws://127.0.0.1:4049/decoy";
  const rendezvous = new CultMeshBrowserOdinRendezvous({
    endpoints: ["ws://127.0.0.1:4040/odin"],
    runtimeId: "browser-route-binding-test",
    socketFactory: () => odin.open(),
  });

  const route = await rendezvous.resolve({
    verseId: "sample.counter",
    authorityRuntimeId: "sample.counter-daemon",
  });
  assert.equal(route.endpoint, "ws://127.0.0.1:4050/mesh");
});

test("Odin rendezvous accepts one-authority legacy routes and rejects ambiguous legacy catalogs", async () => {
  const odin = new FakeOdin("ws://127.0.0.1:4070/mesh");
  odin.omitAuthorityRoutes = true;
  const rendezvous = new CultMeshBrowserOdinRendezvous({
    endpoints: ["ws://127.0.0.1:4040/odin"],
    runtimeId: "browser-legacy-route-test",
    socketFactory: () => odin.open(),
  });

  const route = await rendezvous.resolve({
    verseId: "sample.counter",
    authorityRuntimeId: "sample.counter-daemon",
  });
  assert.equal(route.endpoint, "ws://127.0.0.1:4070/mesh");

  odin.decoyEndpoint = "ws://127.0.0.1:4069/decoy";
  await assert.rejects(
    rendezvous.resolve({ verseId: "sample.counter", authorityRuntimeId: "sample.counter-daemon" }),
    (error: unknown) =>
      error instanceof AggregateError &&
      error.errors.some(inner => inner instanceof Error && /ambiguous legacy routes/.test(inner.message)),
  );
});

test("browser client leases provider state, invokes an operation, and follows route replacement", async () => {
  const first = new FakeProvider("ws://127.0.0.1:4101/mesh", 4);
  const second = new FakeProvider("ws://127.0.0.1:4102/mesh", 9);
  let route = first.route;
  let id = 0;
  const client = await CultMeshBrowserClient.connect({
    verseId: route.verseId,
    authorityRuntimeId: route.authorityRuntimeId,
    runtimeId: "browser-test",
    rendezvous: { resolve: async () => route },
    createId: () => `id-${++id}`,
    socketFactory: endpoint => endpoint === first.route.endpoint ? first.open() : second.open(),
  });

  const lease = await client.leaseRawDocument({
    schemaId: "sample.counter_state.v1",
    recordKey: "counter:main",
  });
  assert.deepEqual(decodeCultNetPayload<{ count: number }>(lease.current!).count, 4);

  const observed: number[] = [];
  const unwatch = lease.watch(record => {
    if (record) observed.push(decodeCultNetPayload<{ count: number }>(record).count);
  });
  const response = await client.invoke({
    serviceId: "sample.counter",
    operation: "increment",
    payloadSchema: "sample.increment.v1",
    payload: { amount: 2 },
  });
  assert.equal(response.status, "accepted");
  assert.deepEqual(decodeCultNetOperationPayload<{ count: number }>(response), { count: 6 });
  assert.deepEqual(observed, [4, 6]);

  route = second.route;
  await client.refreshRoute();
  assert.equal(client.state, "connected");
  assert.deepEqual(decodeCultNetPayload<{ count: number }>(lease.current!).count, 9);
  assert.deepEqual(observed, [4, 6, 9]);
  assert.equal(first.activeSubscriptionCount, 0);
  assert.equal(second.activeSubscriptionCount, 1);

  unwatch();
  lease.dispose();
  assert.equal(second.activeSubscriptionCount, 0);
  await client.dispose();
});

test("browser client rejects wrong rendezvous identity and has no document write API", async () => {
  const provider = new FakeProvider("ws://127.0.0.1:4201/mesh", 1);
  await assert.rejects(
    CultMeshBrowserClient.connect({
      verseId: "sample.counter",
      authorityRuntimeId: "sample.counter-daemon",
      runtimeId: "browser-test",
      rendezvous: {
        resolve: async () => ({
          ...provider.route,
          authorityRuntimeId: "intruder-runtime",
        }),
      },
      socketFactory: () => provider.open(),
    }),
    /wrong stable identity/,
  );
  assert.equal("putDocument" in CultMeshBrowserClient.prototype, false);
  assert.equal("setDocument" in CultMeshBrowserClient.prototype, false);
});

test("browser client rejects a route whose peer proves the wrong authority", async () => {
  const provider = new FakeProvider("ws://127.0.0.1:4251/mesh", 1, false, "intruder-runtime");
  await assert.rejects(
    CultMeshBrowserClient.connect({
      verseId: "sample.counter",
      authorityRuntimeId: "sample.counter-daemon",
      runtimeId: "browser-peer-proof-test",
      rendezvous: { resolve: async () => ({ ...provider.route, authorityRuntimeId: "sample.counter-daemon" }) },
      socketFactory: () => provider.open(),
    }),
    /proved .*intruder-runtime.*expected .*sample.counter-daemon/,
  );
});

test("browser client bounds unanswered provider operations", async () => {
  const route = {
    verseId: "sample.counter",
    authorityRuntimeId: "sample.counter-daemon",
    endpoint: "ws://127.0.0.1:4301/mesh",
    generation: "route-4301",
  };
  const client = await CultMeshBrowserClient.connect({
    ...route,
    runtimeId: "browser-timeout-test",
    rendezvous: { resolve: async () => route },
    requestTimeoutMs: 5,
    socketFactory: () => handshakeOnlySocket(route),
  });
  await assert.rejects(
    client.invoke({
      serviceId: "sample.counter",
      operation: "increment",
      payloadSchema: "sample.increment.v1",
      payload: { amount: 1 },
      idempotencyKey: "timeout-1",
    }),
    /timed out/,
  );
  await client.dispose();
});

test("browser client rejects a correlated framework failure without waiting for timeout", async () => {
  const provider = new FakeProvider("ws://127.0.0.1:4351/mesh", 1, true);
  const client = await CultMeshBrowserClient.connect({
    ...provider.route,
    runtimeId: "browser-failure-test",
    rendezvous: { resolve: async () => provider.route },
    requestTimeoutMs: 60_000,
    socketFactory: () => provider.open(),
  });

  await assert.rejects(
    client.invoke({
      serviceId: "sample.counter",
      operation: "increment",
      payloadSchema: "wrong.request.v1",
      payload: { amount: 1 },
      idempotencyKey: "failure-1",
    }),
    (error: unknown) => {
      assert.ok(error instanceof CultMeshBrowserOperationError);
      assert.equal(error.status, "invalid");
      assert.equal(error.code, "request-schema-mismatch");
      assert.match(error.message, /expected payload schema/i);
      return true;
    },
  );
  await client.dispose();
});

test("browser client rejects an operation response from another runtime", async () => {
  const provider = new FakeProvider(
    "ws://127.0.0.1:4371/mesh",
    1,
    false,
    "sample.counter-daemon",
    "intruder-runtime",
  );
  const client = await CultMeshBrowserClient.connect({
    ...provider.route,
    runtimeId: "browser-response-authority-test",
    rendezvous: { resolve: async () => provider.route },
    socketFactory: () => provider.open(),
  });

  await assert.rejects(
    client.invoke({
      serviceId: "sample.counter",
      operation: "increment",
      payloadSchema: "sample.increment.v1",
      payload: { amount: 1 },
    }),
    /response came from 'intruder-runtime'/,
  );
  await client.dispose();
});

test("browser client rejects oversized outbound schema messages", async () => {
  const route = {
    verseId: "sample.counter",
    authorityRuntimeId: "sample.counter-daemon",
    endpoint: "ws://127.0.0.1:4401/mesh",
    generation: "route-4401",
  };
  await assert.rejects(
    CultMeshBrowserClient.connect({
      ...route,
      runtimeId: "browser-size-test",
      rendezvous: { resolve: async () => route },
      maxFrameBytes: 8,
      socketFactory: () => new FakeSocket(() => undefined, () => undefined),
    }),
    /exceeds the 8-byte limit/,
  );
});

test("browser client reserves subscription identity while the initial lease opens", async () => {
  const provider = new FakeProvider("ws://127.0.0.1:4501/mesh", 3);
  const client = await CultMeshBrowserClient.connect({
    ...provider.route,
    runtimeId: "browser-lease-race-test",
    rendezvous: { resolve: async () => provider.route },
    socketFactory: () => provider.open(),
  });
  const first = client.leaseRawDocument({
    schemaId: "sample.counter_state.v1",
    recordKey: "counter:main",
    subscriptionId: "one-owner",
  });
  await assert.rejects(
    client.leaseRawDocument({
      schemaId: "sample.counter_state.v1",
      recordKey: "counter:main",
      subscriptionId: "one-owner",
    }),
    /already leased/,
  );
  (await first).dispose();
  await client.dispose();
});

class FakeProvider {
  readonly route: CultMeshBrowserRoute;
  #count: number;
  #subscriptions = new Map<FakeSocket, Map<string, CultNetDatabaseSubscribeMessage>>();
  #rejectOperations: boolean;
  #runtimeId: string;
  #responseRuntimeId: string;

  constructor(
    endpoint: string,
    count: number,
    rejectOperations = false,
    runtimeId = "sample.counter-daemon",
    responseRuntimeId = runtimeId,
  ) {
    this.route = {
      verseId: "sample.counter",
      authorityRuntimeId: "sample.counter-daemon",
      endpoint,
      generation: endpoint,
    };
    this.#count = count;
    this.#rejectOperations = rejectOperations;
    this.#runtimeId = runtimeId;
    this.#responseRuntimeId = responseRuntimeId;
  }

  get activeSubscriptionCount(): number {
    let count = 0;
    for (const subscriptions of this.#subscriptions.values()) count += subscriptions.size;
    return count;
  }

  open(): FakeSocket {
    const socket = new FakeSocket(
      wire => this.receive(socket, wire),
      () => this.#subscriptions.delete(socket),
    );
    this.#subscriptions.set(socket, new Map());
    return socket;
  }

  private receive(socket: FakeSocket, wire: Uint8Array): void {
    const message = parseCultNetMessage(decode(wire));
    switch (message.schemaVersion) {
      case "cultmesh.session_open.v1":
        this.acceptSession(socket, message);
        return;
      case "cultnet.database_subscribe.v0":
        this.subscribe(socket, message);
        return;
      case "cultnet.database_unsubscribe.v0":
        this.#subscriptions.get(socket)?.delete(message.subscriptionId);
        return;
      case "cultnet.operation_request.v0":
        this.invoke(socket, message);
        return;
      default:
        return;
    }
  }

  private acceptSession(socket: FakeSocket, message: CultMeshSessionOpenMessage): void {
    socket.deliver({
      schemaVersion: "cultmesh.session_accepted.v1",
      messageId: message.messageId,
      accepted: true,
      verseId: message.verseId,
      authorityRuntimeId: this.#runtimeId,
      protocolId: message.protocolId,
      routeGeneration: message.routeGeneration,
    });
  }

  private subscribe(socket: FakeSocket, message: CultNetDatabaseSubscribeMessage): void {
    this.#subscriptions.get(socket)!.set(message.subscriptionId, message);
    socket.deliver({
      schemaVersion: "cultnet.snapshot_response_raw.v0",
      messageId: message.messageId,
      documents: [this.record()],
    });
  }

  private invoke(socket: FakeSocket, message: CultNetOperationRequestMessage): void {
    assert.equal(message.targetRuntimeId, this.#runtimeId);
    if (this.#rejectOperations) {
      socket.deliver({
        schemaVersion: "cultnet.operation_response.v0",
        messageId: message.messageId,
        serviceId: message.serviceId,
        operation: message.operation,
        status: "invalid",
        payloadSchema: "gamecult.cultnet.operation_failure.v1",
        payloadEncoding: "messagepack-base64",
        payload: bytesToBase64(encode({
          code: "request-schema-mismatch",
          message: "Expected payload schema 'sample.increment.v1'.",
        })),
        diagnostics: ["Request schema did not match."],
        sourceRuntimeId: this.#responseRuntimeId,
      });
      return;
    }
    const payload = decode(base64ToBytes(message.payload)) as { amount: number };
    this.#count += payload.amount;
    for (const [peer, subscriptions] of this.#subscriptions) {
      for (const subscriptionId of subscriptions.keys()) {
        peer.deliver({
          schemaVersion: "cultnet.database_change_raw.v0",
          messageId: `change-${message.messageId}`,
          subscriptionId,
          changeKind: "updated",
          document: this.record(),
        });
      }
    }
    socket.deliver({
      schemaVersion: "cultnet.operation_response.v0",
      messageId: message.messageId,
      serviceId: message.serviceId,
      operation: message.operation,
      status: "accepted",
      payloadSchema: "sample.increment_receipt.v1",
      payloadEncoding: "messagepack-base64",
      payload: bytesToBase64(encode({ count: this.#count })),
      sourceRuntimeId: this.#responseRuntimeId,
    });
  }

  private record(): CultNetRawDocumentRecord {
    return {
      schemaId: "sha256:sample-counter-state",
      schemaName: "sample.counter_state",
      schemaVersion: "sample.counter_state.v1",
      schemaContentHash: "sha256:sample-counter-state-content",
      recordKey: "counter:main",
      storedAt: "2026-08-17T00:00:00Z",
      payloadEncoding: "messagepack",
      payload: encode({ count: this.#count }),
      sourceRuntimeId: this.#runtimeId,
    };
  }
}

class FakeOdin {
  providerEndpoint: string;
  decoyEndpoint: string | undefined;
  omitAuthorityRoutes = false;

  constructor(providerEndpoint: string) {
    this.providerEndpoint = providerEndpoint;
  }

  open(): FakeSocket {
    const socket = new FakeSocket(wire => {
      const message = parseCultNetMessage(decode(wire));
      if (message.schemaVersion !== "cultmesh.verse_catalog_request.v0") return;
      socket.deliver({
        schemaVersion: "cultmesh.verse_catalog_response.v0",
        messageId: message.messageId,
        verses: [{
          verseId: "sample.counter",
          displayName: "Counter",
          authorityModel: "ServerAuthoritative",
          compatibility: {
            transportVersion: "cultmesh.v1",
            rulesHash: "counter-v1",
            compatibleVerseIds: [],
            requiredPluginIds: [],
            optionalPluginIds: [],
          },
          discoveryEndpoints: [this.providerEndpoint],
          authorityRuntimeIds: this.decoyEndpoint
            ? ["sample.counter-daemon", "sample.decoy-daemon"]
            : ["sample.counter-daemon"],
          ...(!this.omitAuthorityRoutes ? { authorityRoutes: [
            ...(this.decoyEndpoint ? [{
              authorityRuntimeId: "sample.decoy-daemon",
              endpoint: this.decoyEndpoint,
              protocolIds: ["cultmesh.documents.v1"],
              priority: 0,
              generation: "decoy-generation",
            }] : []),
            {
              authorityRuntimeId: "sample.counter-daemon",
              endpoint: this.providerEndpoint,
              protocolIds: ["cultmesh.documents.v1"],
              priority: 10,
              generation: this.providerEndpoint,
            },
          ] } : {}),
        }],
      });
    }, () => undefined);
    return socket;
  }
}

function handshakeOnlySocket(route: CultMeshBrowserRoute): FakeSocket {
  let socket: FakeSocket;
  socket = new FakeSocket(wire => {
    const message = parseCultNetMessage(decode(wire));
    if (message.schemaVersion !== "cultmesh.session_open.v1") return;
    socket.deliver({
      schemaVersion: "cultmesh.session_accepted.v1",
      messageId: message.messageId,
      accepted: true,
      verseId: route.verseId,
      authorityRuntimeId: route.authorityRuntimeId,
      protocolId: message.protocolId,
      routeGeneration: message.routeGeneration,
    });
  }, () => undefined);
  return socket;
}

class FakeSocket implements CultMeshBrowserSocket {
  binaryType: BinaryType = "arraybuffer";
  readyState = 0;
  onopen: ((event: Event) => void) | null = null;
  onmessage: ((event: MessageEvent) => void) | null = null;
  onerror: ((event: Event) => void) | null = null;
  onclose: ((event: CloseEvent) => void) | null = null;
  #receive: (wire: Uint8Array) => void;
  #closed: () => void;

  constructor(receive: (wire: Uint8Array) => void, closed: () => void) {
    this.#receive = receive;
    this.#closed = closed;
    queueMicrotask(() => {
      this.readyState = 1;
      this.onopen?.({} as Event);
    });
  }

  send(data: ArrayBuffer | ArrayBufferView): void {
    if (this.readyState !== 1) throw new Error("Fake socket is not open.");
    const wire = data instanceof ArrayBuffer
      ? new Uint8Array(data)
      : new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
    this.#receive(wire);
  }

  close(code = 1000, reason = ""): void {
    if (this.readyState === 3) return;
    this.readyState = 3;
    this.#closed();
    this.onclose?.({ code, reason } as CloseEvent);
  }

  deliver(message: CultNetMessage): void {
    const wire = encode(encodeCultNetMessageForWire(message));
    queueMicrotask(() => this.onmessage?.({ data: wire.slice().buffer } as MessageEvent));
  }
}

function bytesToBase64(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary);
}

function base64ToBytes(value: string): Uint8Array {
  const binary = atob(value);
  return Uint8Array.from(binary, character => character.charCodeAt(0));
}
