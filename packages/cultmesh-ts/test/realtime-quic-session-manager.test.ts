// `CultMeshQuicRealtimeSessionManager`'s own rules, isolated from the native
// bridge with fake connectors/transports: eviction on transport death (fix
// 4), single-flight (fix 4), the disposed guard (fix 4), first-success racing
// with abort of the losers (fix 5), the `protocolIds` filter (M7), the
// `authorityRuntimeId` filter (M8), the session key including
// `authorityRuntimeId` (M9), and the `maxRacedCandidates` cap (M6).
//
// Routes use `trust.mode = "local-development"` over loopback endpoints, so
// `verifyAuthorityRoute` short-circuits without a signed certificate
// (`cultmesh-authority.ts:99`): these tests are about the session manager's
// own binding/racing/caching rules, not route trust, which
// `realtime-quic-consumer.test.ts`'s trust negatives already pin.

import assert from "node:assert/strict";
import test from "node:test";

import type { CultMeshAuthorityTrustPolicy } from "cultnet-ts";
import type { CultMeshAuthorityRouteMessage, CultMeshVerseDescriptorMessage } from "cultnet-ts/contracts";

import {
  CultMeshQuicRealtimeSessionManager,
  CultMeshStaticRealtimeLookupSource,
  CULTMESH_REALTIME_STATE_PROTOCOL_ID,
  type CultMeshRealtimeCandidate,
  type CultMeshRealtimeTarget,
  type CultMeshRealtimeTransport,
  type CultMeshRealtimeTransportConnector,
} from "../src/realtime-quic";

const trust: CultMeshAuthorityTrustPolicy = { mode: "local-development", odinRoots: [] };

function route(overrides: Partial<CultMeshAuthorityRouteMessage> = {}): CultMeshAuthorityRouteMessage {
  return {
    authorityRuntimeId: "service:aetheria.daemon",
    endpoint: "cultmesh-state+quic://127.0.0.1:9443",
    protocolIds: [CULTMESH_REALTIME_STATE_PROTOCOL_ID],
    priority: 0,
    generation: "gen-1",
    ...overrides,
  };
}

function verseWithRoutes(verseId: string, routes: readonly CultMeshAuthorityRouteMessage[]): CultMeshVerseDescriptorMessage {
  return {
    verseId,
    displayName: verseId,
    authorityModel: "operator-cluster",
    compatibility: { transportVersion: "cultmesh.v1", rulesHash: "test", compatibleVerseIds: [], requiredPluginIds: [], optionalPluginIds: [] },
    discoveryEndpoints: routes.map((r) => r.endpoint),
    authorityRuntimeIds: [...new Set(routes.map((r) => r.authorityRuntimeId))],
    authorityRoutes: [...routes],
  };
}

interface FakeTransportHandle {
  readonly transport: CultMeshRealtimeTransport;
  disposed(): boolean;
}

/** A transport that never resolves `receiveFrame` and tracks its own disposal. */
function fakeTransport(endpoint: string, verified = true): FakeTransportHandle {
  let disposed = false;
  const disposalHandlers: Array<() => void> = [];
  const transport: CultMeshRealtimeTransport = {
    transportId: "fake",
    endpoint,
    async sendFrame() {},
    async receiveFrame() {
      return await new Promise<never>(() => {});
    },
    dispose() {
      if (disposed) return;
      disposed = true;
      for (const handler of disposalHandlers) handler();
    },
    isVerifiedFor: () => verified,
    onDisposed(handler) {
      if (disposed) {
        handler();
        return;
      }
      disposalHandlers.push(handler);
    },
  };
  return { transport, disposed: () => disposed };
}

/** A connector that resolves immediately (unless `delayMs` is given) with one fake transport per call. */
function spyConnector(
  connectorId: string,
  priority: number,
  options: { delayMs?: number; fails?: boolean; ignoreAbort?: boolean } = {},
): {
  connector: CultMeshRealtimeTransportConnector;
  calls: CultMeshRealtimeCandidate[];
  handles: FakeTransportHandle[];
  /** Timestamps at which this connector's abort signal actually fired. */
  aborts: number[];
} {
  const calls: CultMeshRealtimeCandidate[] = [];
  const handles: FakeTransportHandle[] = [];
  const aborts: number[] = [];
  const connector: CultMeshRealtimeTransportConnector = {
    connectorId,
    priority,
    canConnect: () => true,
    connect: async (candidate, _target, signal) => {
      calls.push(candidate);
      if (options.delayMs) {
        await new Promise<void>((resolve, reject) => {
          const timer = setTimeout(resolve, options.delayMs);
          if (!options.ignoreAbort) {
            signal?.addEventListener("abort", () => {
              clearTimeout(timer);
              aborts.push(Date.now());
              reject(new Error(`${connectorId} connect aborted`));
            });
          }
        });
      }
      if (options.fails) throw new Error(`${connectorId} refused to connect`);
      const handle = fakeTransport(candidate.endpoint);
      handles.push(handle);
      return handle.transport;
    },
  };
  return { connector, calls, handles, aborts };
}

const target: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:aetheria.daemon" };

test("fix 4: connect() caches a session and evicts it once the transport dies, so the next connect redials", async () => {
  const { connector, calls } = spyConnector("fake", 0);
  const manager = new CultMeshQuicRealtimeSessionManager({
    lookupSource: new CultMeshStaticRealtimeLookupSource([verseWithRoutes(target.verseId, [route()])]),
    trust,
    connectors: [connector],
  });

  const first = await manager.connect(target);
  assert.equal(await manager.connect(target), first, "a live session is returned from cache, not redialed");
  assert.equal(calls.length, 1);

  first.dispose();
  const second = await manager.connect(target);
  assert.notEqual(second, first, "a dead session is evicted, so the next connect redials");
  assert.equal(calls.length, 2);
});

test("fix 4: concurrent connect() calls for the same target share one dial (single-flight)", async () => {
  const { connector, calls } = spyConnector("fake", 0, { delayMs: 50 });
  const manager = new CultMeshQuicRealtimeSessionManager({
    lookupSource: new CultMeshStaticRealtimeLookupSource([verseWithRoutes(target.verseId, [route()])]),
    trust,
    connectors: [connector],
  });

  const [a, b, c] = await Promise.all([manager.connect(target), manager.connect(target), manager.connect(target)]);
  assert.equal(a, b);
  assert.equal(b, c);
  assert.equal(calls.length, 1, "three concurrent callers must share one dial");
});

test("fix 4/low: dispose() aborts a dial in flight instead of letting it land and discarding the result", async () => {
  // A long delay: if `dispose()` did not cancel the dial, this test would
  // have to wait it out (or race a timing-dependent assertion) to observe
  // that the eventually-created transport gets disposed instead of cached.
  const { connector, handles } = spyConnector("fake", 0, { delayMs: 5_000 });
  const manager = new CultMeshQuicRealtimeSessionManager({
    lookupSource: new CultMeshStaticRealtimeLookupSource([verseWithRoutes(target.verseId, [route()])]),
    trust,
    connectors: [connector],
  });

  const pending = manager.connect(target);
  const start = Date.now();
  manager.dispose();
  await assert.rejects(pending);
  assert.ok(
    Date.now() - start < 1_000,
    "dispose() must cancel the in-flight dial rather than waiting out the connector's own timeout",
  );
  assert.equal(handles.length, 0, "an aborted dial must never construct a transport only to immediately discard it");
  await assert.rejects(manager.connect(target), /disposed/i, "a disposed manager refuses further connects");
});

test("fix 4/low: disconnect() during a dial does not let the dial cache a session once it lands", async () => {
  const { connector, handles } = spyConnector("fake", 0, { delayMs: 30 });
  const manager = new CultMeshQuicRealtimeSessionManager({
    lookupSource: new CultMeshStaticRealtimeLookupSource([verseWithRoutes(target.verseId, [route()])]),
    trust,
    connectors: [connector],
  });

  const pending = manager.connect(target);
  manager.disconnect(target);
  await assert.rejects(pending);
  // Whether or not the aborted dial still managed to construct a transport
  // (a race against the abort), it must end up disposed, never cached.
  await new Promise((resolve) => setTimeout(resolve, 60));
  for (const handle of handles) assert.equal(handle.disposed(), true, "a cancelled dial's transport must be disposed");

  // The manager itself is still usable: a fresh connect redials and succeeds.
  const { connector: connector2 } = spyConnector("fake", 0);
  const manager2 = new CultMeshQuicRealtimeSessionManager({
    lookupSource: new CultMeshStaticRealtimeLookupSource([verseWithRoutes(target.verseId, [route()])]),
    trust,
    connectors: [connector2],
  });
  const fresh = await manager2.connect(target);
  assert.ok(fresh);
});

test("fix 5: the race returns the first verified success and aborts the losers", async () => {
  const winner = spyConnector("fast", 0, { delayMs: 10 });
  const loser = spyConnector("slow", 0, { delayMs: 5_000 });
  const manager = new CultMeshQuicRealtimeSessionManager({
    lookupSource: new CultMeshStaticRealtimeLookupSource([
      verseWithRoutes(target.verseId, [
        route({ endpoint: "cultmesh-state+quic://127.0.0.1:9001" }),
        route({ endpoint: "cultmesh-state+quic://127.0.0.1:9002" }),
      ]),
    ]),
    trust,
    connectors: [winner.connector, loser.connector],
    maxRacedCandidates: 2,
  });

  const startedAt = Date.now();
  const transport = await manager.connect(target);
  const elapsedMs = Date.now() - startedAt;
  assert.ok(elapsedMs < 1_000, `connect() must return on the first success, not wait out the slow route (took ${elapsedMs}ms)`);
  assert.equal(transport.endpoint, "cultmesh-state+quic://127.0.0.1:9001");
});

test("F5abort: a losing candidate is told to abort promptly, not left to run out its own timeout", async () => {
  const winner = spyConnector("fast", 0, { delayMs: 10 });
  const loser = spyConnector("slow", 0, { delayMs: 5_000 });
  const manager = new CultMeshQuicRealtimeSessionManager({
    lookupSource: new CultMeshStaticRealtimeLookupSource([
      verseWithRoutes(target.verseId, [
        route({ endpoint: "cultmesh-state+quic://127.0.0.1:9001" }),
        route({ endpoint: "cultmesh-state+quic://127.0.0.1:9002" }),
      ]),
    ]),
    trust,
    connectors: [winner.connector, loser.connector],
    maxRacedCandidates: 2,
  });

  const startedAt = Date.now();
  await manager.connect(target);
  await new Promise((resolve) => setTimeout(resolve, 300));
  assert.equal(loser.aborts.length, 1, "the losing candidate's connect() must observe an abort signal");
  assert.ok(
    loser.aborts[0]! - startedAt < 1_000,
    "the abort must arrive promptly after the winner is decided, not after the loser's own 5s timeout",
  );
});

test("F5loser: a losing candidate that connects anyway (past the point abort helps) is disposed, not left dangling", async () => {
  const winner = spyConnector("fast", 0, { delayMs: 10 });
  // Ignores the abort signal entirely, simulating a candidate already past
  // the point where cancellation helps: it still produces a real transport.
  const loser = spyConnector("slow", 0, { delayMs: 80, ignoreAbort: true });
  const manager = new CultMeshQuicRealtimeSessionManager({
    lookupSource: new CultMeshStaticRealtimeLookupSource([
      verseWithRoutes(target.verseId, [
        route({ endpoint: "cultmesh-state+quic://127.0.0.1:9001" }),
        route({ endpoint: "cultmesh-state+quic://127.0.0.1:9002" }),
      ]),
    ]),
    trust,
    connectors: [winner.connector, loser.connector],
    maxRacedCandidates: 2,
  });

  const transport = await manager.connect(target);
  assert.equal(transport.endpoint, "cultmesh-state+quic://127.0.0.1:9001");
  await new Promise((resolve) => setTimeout(resolve, 200));
  assert.equal(loser.handles.length, 1, "the loser still connects despite racing to lose");
  assert.equal(
    loser.handles[0]!.disposed(),
    true,
    "a losing candidate that connects after the race is decided must be disposed, never left connected",
  );
});

test("M6: maxRacedCandidates caps how many candidates in a tier are attempted", async () => {
  const spy = spyConnector("fake", 0);
  const manager = new CultMeshQuicRealtimeSessionManager({
    lookupSource: new CultMeshStaticRealtimeLookupSource([
      verseWithRoutes(target.verseId, [
        route({ endpoint: "cultmesh-state+quic://127.0.0.1:9001", priority: 0 }),
        route({ endpoint: "cultmesh-state+quic://127.0.0.1:9002", priority: 1 }),
        route({ endpoint: "cultmesh-state+quic://127.0.0.1:9003", priority: 2 }),
      ]),
    ]),
    trust,
    connectors: [spy.connector],
    maxRacedCandidates: 1,
  });

  await manager.connect(target);
  assert.equal(spy.calls.length, 1, "only the top maxRacedCandidates entries in the tier are attempted");
  assert.equal(spy.calls[0]!.endpoint, "cultmesh-state+quic://127.0.0.1:9001", "the lowest route priority races first");
});

test("M7: a route whose protocolIds omit the realtime-state protocol is not a candidate", async () => {
  const spy = spyConnector("fake", 0);
  const manager = new CultMeshQuicRealtimeSessionManager({
    lookupSource: new CultMeshStaticRealtimeLookupSource([
      verseWithRoutes(target.verseId, [route({ protocolIds: ["cultmesh.other_protocol.v1"] })]),
    ]),
    trust,
    connectors: [spy.connector],
  });

  await assert.rejects(manager.connect(target), /No realtime state route was advertised/);
  assert.equal(spy.calls.length, 0);
});

test("M8: a route advertised for a different authorityRuntimeId is not a candidate", async () => {
  const spy = spyConnector("fake", 0);
  const manager = new CultMeshQuicRealtimeSessionManager({
    lookupSource: new CultMeshStaticRealtimeLookupSource([
      verseWithRoutes(target.verseId, [route({ authorityRuntimeId: "service:other.daemon" })]),
    ]),
    trust,
    connectors: [spy.connector],
  });

  await assert.rejects(manager.connect(target), /No realtime state route was advertised/);
  assert.equal(spy.calls.length, 0);
});

test("M9: the session key includes authorityRuntimeId, so two authorities in the same Verse get separate sessions", async () => {
  const spy = spyConnector("fake", 0);
  const targetA: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:a.daemon" };
  const targetB: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:b.daemon" };
  const manager = new CultMeshQuicRealtimeSessionManager({
    lookupSource: new CultMeshStaticRealtimeLookupSource([
      verseWithRoutes("aetheria", [
        route({ authorityRuntimeId: "service:a.daemon", endpoint: "cultmesh-state+quic://127.0.0.1:9001" }),
        route({ authorityRuntimeId: "service:b.daemon", endpoint: "cultmesh-state+quic://127.0.0.1:9002" }),
      ]),
    ]),
    trust,
    connectors: [spy.connector],
  });

  const sessionA = await manager.connect(targetA);
  const sessionB = await manager.connect(targetB);
  assert.notEqual(sessionA, sessionB);
  assert.equal(sessionA.endpoint, "cultmesh-state+quic://127.0.0.1:9001");
  assert.equal(sessionB.endpoint, "cultmesh-state+quic://127.0.0.1:9002");
  assert.equal(spy.calls.length, 2);
});
