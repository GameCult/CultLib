// `CultMeshQuicRealtimeProvider`, Cut 5's StreamPixels role, exercised
// end-to-end against the real native bridge and the real consumer connector
// from Cut 4 (no mocks): reliable ordering in both directions
// (`CultMeshQuicRealtimeTransportTests.cs:71-110`), latest-only coalescing
// per `(channel, body)` key, a non-blocking broadcast, the uppercase pin, and
// eviction on peer shutdown.
//
// Skips instead of failing when no native bridge is available; see
// `realtime-quic-native.test.ts` for why.

import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";

import {
  CultMeshQuicNativeRuntime,
} from "../src/realtime-quic-native";
import {
  CultMeshQuicRealtimeConnector,
  CultMeshQuicRealtimeProvider,
  type CultMeshRealtimeCandidate,
  type CultMeshRealtimeTarget,
  type CultMeshRealtimeTransport,
} from "../src/realtime-quic";
import type { CultMeshRealtimeFrame } from "../src/realtime-wire";
import { nativeBridgeAvailable } from "./support/native-bridge";

// __dirname at runtime is dist-test/test; fixtures are binary and are never
// compiled/copied there, so they are read from their source location, two
// levels up.
const FIXTURE_P12 = join(__dirname, "..", "..", "test", "fixtures", "quic-test.p12");

function testFrame(overrides: Partial<CultMeshRealtimeFrame> = {}): CultMeshRealtimeFrame {
  return {
    channelId: "aetheria.entities",
    schemaId: "eve.entity_soa.v1",
    bodyId: "body:aetheria:entities",
    producerEpoch: 7n,
    sequence: 1n,
    delivery: "latest-only",
    payload: new Uint8Array([3, 4, 5]),
    ...overrides,
  };
}

async function startProvider(): Promise<CultMeshQuicRealtimeProvider> {
  return await CultMeshQuicRealtimeProvider.listen({
    host: "127.0.0.1",
    port: 0,
    serverCertificate: { pkcs12: readFileSync(FIXTURE_P12), password: "" },
  });
}

async function dialProvider(provider: CultMeshQuicRealtimeProvider): Promise<CultMeshRealtimeTransport> {
  const target: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:aetheria.daemon" };
  const candidate: CultMeshRealtimeCandidate = {
    endpoint: provider.advertisedEndpoint,
    authorityRuntimeId: target.authorityRuntimeId,
    priority: 0,
    generation: "gen-1",
  };
  const connector = new CultMeshQuicRealtimeConnector();
  return await connector.connect(candidate, target);
}

async function waitUntil(predicate: () => boolean, timeoutMs = 5_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (!predicate()) {
    if (Date.now() >= deadline) throw new Error("timed out waiting for a provider condition");
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
}

test("the advertised endpoint carries an uppercase cert-sha256 pin the Unity connector requires", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const provider = await startProvider();
  try {
    assert.match(
      provider.advertisedEndpoint,
      /^cultmesh-state\+quic:\/\/127\.0\.0\.1:\d+\?cert-sha256=[0-9A-F]{64}$/,
    );
    // Not merely uppercase-tolerant: it must not contain a lowercase hex digit.
    const pin = new URL(provider.advertisedEndpoint).searchParams.get("cert-sha256")!;
    assert.equal(pin, pin.toUpperCase());
    assert.notEqual(pin, pin.toLowerCase());
  } finally {
    provider.dispose();
  }
});

test("reliable-ordered delivers frames in order, provider to consumer and consumer to provider", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const provider = await startProvider();
  try {
    const consumer = await dialProvider(provider);
    try {
      await waitUntil(() => provider.connectionCount === 1);

      await provider.broadcast(testFrame({ delivery: "reliable-ordered", sequence: 1n }));
      await provider.broadcast(testFrame({ delivery: "reliable-ordered", sequence: 2n }));
      const firstAtConsumer = await consumer.receiveFrame();
      const secondAtConsumer = await consumer.receiveFrame();
      assert.deepEqual([firstAtConsumer.sequence, secondAtConsumer.sequence], [1n, 2n]);

      await consumer.sendFrame(testFrame({ delivery: "reliable-ordered", sequence: 3n }));
      await consumer.sendFrame(testFrame({ delivery: "reliable-ordered", sequence: 4n }));
      const firstAtProvider = await provider.receive();
      const secondAtProvider = await provider.receive();
      assert.deepEqual([firstAtProvider.sequence, secondAtProvider.sequence], [3n, 4n]);
    } finally {
      consumer.dispose();
    }
  } finally {
    provider.dispose();
  }
});

test("latest-only broadcast coalesces back-to-back sends for the same key to the newest frame, and never blocks on I/O", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const provider = await startProvider();
  try {
    const consumer = await dialProvider(provider);
    try {
      await waitUntil(() => provider.connectionCount === 1);

      // Three broadcasts for the same (channelId, bodyId) key, issued back to
      // back with no await between them. `broadcast` for latest-only never
      // awaits any I/O (mutating it to await the peer's send would make this
      // loop, and the assertion below, run after real network round trips
      // instead of within one JS turn): the synchronous middle publish is
      // therefore always coalesced away, deterministically, not by luck of
      // network timing. Mutating the coalescing key (e.g. dropping bodyId)
      // would let sequence 200 leak through as a separate delivered frame
      // instead of overwriting 100.
      const before = Date.now();
      const first = provider.broadcast(testFrame({ sequence: 1n }));
      const second = provider.broadcast(testFrame({ sequence: 100n }));
      const third = provider.broadcast(testFrame({ sequence: 200n }));
      const afterCallsReturned = Date.now();
      // All three promises exist before any of them could possibly have
      // waited on the peer's round trip: the loop above ran in well under a
      // network round trip's worth of time.
      assert.ok(afterCallsReturned - before < 50, "broadcast must not block on any peer's I/O");
      await Promise.all([first, second, third]);

      // The consumer's own receive-side inbox (Cut 4) also coalesces
      // latest-only frames it has not yet read, so whether 1 is delivered as
      // its own frame or is itself coalesced away by 200 arriving first
      // depends on read timing; that is Cut 4's contract, not this test's.
      // What this test owns is the provider's outbox: 100 must never appear,
      // and the last frame delivered must be 200.
      const received: bigint[] = [];
      let last: bigint | undefined;
      while (last !== 200n) {
        const frame = await consumer.receiveFrame();
        received.push(frame.sequence);
        last = frame.sequence;
      }
      assert.ok(!received.includes(100n), `100 must be coalesced away, got ${received}`);
      assert.ok(received.length <= 2, `expected at most 2 delivered frames, got ${received}`);
      assert.equal(last, 200n);
    } finally {
      consumer.dispose();
    }
  } finally {
    provider.dispose();
  }
});

test("a stalled peer's outbox holds at most one pending frame per key across many broadcasts", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const provider = await startProvider();
  try {
    const consumer = await dialProvider(provider);
    try {
      await waitUntil(() => provider.connectionCount === 1);

      // 200 broadcasts, none awaited individually, none read by the consumer
      // until they are all issued: mirrors the interop lane's stalled-reader
      // scenario at unit-test speed. `broadcast` completing this loop at all
      // (bounded time) proves it never blocks on the unread peer.
      const started = Date.now();
      const sends: Promise<void>[] = [];
      for (let sequence = 1; sequence <= 200; sequence += 1) {
        sends.push(provider.broadcast(testFrame({ sequence: BigInt(sequence) })));
      }
      await Promise.all(sends);
      assert.ok(Date.now() - started < 2_000, "200 broadcasts to one key must complete quickly, unread");

      const receivedSequences: bigint[] = [];
      let last: bigint | undefined;
      while (last !== 200n) {
        const frame = await consumer.receiveFrame();
        receivedSequences.push(frame.sequence);
        last = frame.sequence;
      }
      assert.equal(last, 200n);
      // Coalescing means far fewer than 200 frames were actually put on the
      // wire; the exact count depends on scheduling, but it must be small.
      assert.ok(receivedSequences.length < 200, `expected coalescing, got ${receivedSequences.length} frames`);
    } finally {
      consumer.dispose();
    }
  } finally {
    provider.dispose();
  }
});

test("unreliable broadcast fails closed", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const provider = await startProvider();
  try {
    const consumer = await dialProvider(provider);
    try {
      await waitUntil(() => provider.connectionCount === 1);
      await assert.rejects(provider.broadcast(testFrame({ delivery: "unreliable" })), /datagrams/i);
    } finally {
      consumer.dispose();
    }
  } finally {
    provider.dispose();
  }
});

test("a peer is evicted from the provider on connection shutdown", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const provider = await startProvider();
  try {
    const consumer = await dialProvider(provider);
    await waitUntil(() => provider.connectionCount === 1);
    consumer.dispose();
    await waitUntil(() => provider.connectionCount === 0);
  } finally {
    provider.dispose();
  }
});

test("a reliable-ordered send failure evicts the peer without broadcast() throwing to the caller", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const provider = await startProvider();
  try {
    const consumer = await dialProvider(provider);
    await waitUntil(() => provider.connectionCount === 1);
    consumer.dispose();
    // Keep broadcasting reliable-ordered frames into the dying/dead peer's
    // outbound stream for a short window: somewhere in this window the
    // send to that peer fails (either the native stream call itself, or
    // the peer's own CONNECTION_SHUTDOWN cleanup rejecting it), and
    // CultMeshQuicRealtimeProvider.broadcast()'s per-peer try/catch must
    // dispose it rather than let the rejection propagate or leave a dead
    // peer counted forever.
    const deadline = Date.now() + 2_000;
    let sequence = 0n;
    while (Date.now() < deadline && provider.connectionCount > 0) {
      sequence += 1n;
      // Must never throw: a removed subtraction of the catch's dispose()
      // call would not itself make broadcast() throw, but a regression
      // that let the rejection propagate instead of being caught at all
      // would, and this loop would then fail loudly instead of hanging.
      await provider.broadcast(testFrame({ delivery: "reliable-ordered", sequence }));
    }
    assert.equal(provider.connectionCount, 0, "the dead peer must be evicted, not counted forever");
  } finally {
    provider.dispose();
  }
});

test("connectionCount reflects accepted peers, and dispose releases the runtime for a fresh listen", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const provider = await startProvider();
  assert.equal(provider.connectionCount, 0);
  const consumer = await dialProvider(provider);
  try {
    await waitUntil(() => provider.connectionCount === 1);
  } finally {
    consumer.dispose();
    provider.dispose();
  }
  // A leaked runtime reference from any earlier path (accept, handshake
  // timeout, dispose) would leave the shared runtime unable to open a fresh
  // listener cleanly; opening and tearing down a second provider proves
  // release ran on every path exercised above.
  const again = await startProvider();
  again.dispose();
});

test("the runtime reference is released when extracting the certificate fails, before any listener opens", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const before = CultMeshQuicNativeRuntime.refCount;
  await assert.rejects(
    CultMeshQuicRealtimeProvider.listen({
      host: "127.0.0.1",
      port: 0,
      // Not a PKCS12 credential at all: extractLeafCertificateDer() throws
      // before CultMeshQuicNativeRuntime.open() is ever called, so there is
      // no reference to leak on this path; this pins that (trivial but
      // real) invariant rather than exercising listen()'s own
      // `catch { await runtime.release(); throw error; }`, which the next
      // test covers with a listener-level failure instead.
      serverCertificate: { pkcs12: new Uint8Array([1, 2, 3, 4]), password: "" },
    }),
  );
  assert.equal(CultMeshQuicNativeRuntime.refCount, before, "must not leak a reference that was never opened");
});

test("the runtime reference is released when the listener itself fails to open", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const holder = await startProvider();
  try {
    const boundPort = new URL(holder.advertisedEndpoint).port;
    const before = CultMeshQuicNativeRuntime.refCount;
    // A real, valid PKCS12 (so extractLeafCertificateDer succeeds and
    // CultMeshQuicNativeRuntime.open() actually runs), but the port
    // `holder` already has bound: listenerOpen() itself must fail, driving
    // listen()'s `catch { await runtime.release(); throw error; }`.
    await assert.rejects(
      CultMeshQuicRealtimeProvider.listen({
        host: "127.0.0.1",
        port: Number(boundPort),
        serverCertificate: { pkcs12: readFileSync(FIXTURE_P12), password: "" },
      }),
    );
    assert.equal(
      CultMeshQuicNativeRuntime.refCount,
      before,
      "a listenerOpen() failure must release the reference listen() had already opened",
    );
  } finally {
    holder.dispose();
  }
});

test(
  "the runtime reference is released on dispose with a pending send in flight",
  { timeout: 10_000 },
  async (t) => {
    if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
    const before = CultMeshQuicNativeRuntime.refCount;
    const provider = await startProvider();
    const consumer = await dialProvider(provider);
    await waitUntil(() => provider.connectionCount === 1);

    // Start a reliable-ordered broadcast and dispose before it can possibly
    // have settled; dispose() must tear the peer down (and release its
    // runtime reference) regardless of the outstanding send. `broadcast()`
    // must itself never hang: an explicit `{ timeout }` above fails this
    // test loudly, rather than the whole file, if it ever does.
    const broadcasting = provider.broadcast(testFrame({ delivery: "reliable-ordered" })).catch(() => {});
    provider.dispose();
    await broadcasting;
    consumer.dispose();
    await waitUntil(() => CultMeshQuicNativeRuntime.refCount === before);
  },
);

test(
  "dispose() evicts a connection still mid-handshake, not just fully attached peers",
  { timeout: 10_000 },
  async (t) => {
    if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
    // A long handshake timeout: without dispose() sweeping still-pending
    // accepts (fixed alongside this test), a connection whose handshake
    // never reaches CONNECTED — here, the client rejects the provider's
    // certificate — would sit on its own handshake timer, and once
    // attachPeer's own CultMeshQuicNativeRuntime.open() resolves, hold a
    // runtime reference, for up to this long after dispose() returns. Found
    // this way: mutating the certificate-pin comparison to be
    // case-sensitive (M2) made every provider test that dials with an
    // uppercase-pinned endpoint reject during the handshake, and the whole
    // file then hung for over two minutes past its last visible test.
    const before = CultMeshQuicNativeRuntime.refCount;
    const provider = await CultMeshQuicRealtimeProvider.listen({
      host: "127.0.0.1",
      port: 0,
      serverCertificate: { pkcs12: readFileSync(FIXTURE_P12), password: "" },
      handshakeTimeoutMs: 30_000,
    });
    const target: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:aetheria.daemon" };
    const candidate: CultMeshRealtimeCandidate = {
      // A pin that can never match this provider's real certificate: the
      // connector rejects it during CULTMESH_QUIC_EVENT_CONNECTION_CERTIFICATE_RECEIVED,
      // before the connection ever reaches CONNECTED.
      endpoint: provider.advertisedEndpoint.replace(/cert-sha256=[0-9A-F]+/, `cert-sha256=${"0".repeat(64)}`),
      authorityRuntimeId: target.authorityRuntimeId,
      priority: 0,
      generation: "gen-1",
    };
    const connector = new CultMeshQuicRealtimeConnector();
    await assert.rejects(connector.connect(candidate, target), /certificate|rejected/i);

    // The provider's own accept flow is asynchronous (attachPeer awaits a
    // runtime reference before it can even see the rejection); give it a
    // moment to actually start before disposing, so this test exercises a
    // real in-flight pending accept rather than one that never began.
    await new Promise((resolve) => setTimeout(resolve, 100));

    const disposedAt = Date.now();
    provider.dispose();
    await waitUntil(() => CultMeshQuicNativeRuntime.refCount === before);
    assert.ok(
      Date.now() - disposedAt < 2_000,
      "dispose() must evict a mid-handshake connection promptly, not wait out its 30s handshake timeout",
    );
  },
);
