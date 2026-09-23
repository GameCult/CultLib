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
  CultMeshQuicRealtimeTransport as CultMeshQuicRealtimeTransportClass,
  parseQuicRealtimeEndpoint,
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

/**
 * `assert.rejects`, but safe against a mutant or a real regression that
 * makes the promise resolve instead: a transport that unexpectedly connects
 * is disposed before the assertion fails, so a broken certificate check
 * cannot hang this file the way M1 did before realtime-quic-consumer.test.ts
 * gained this same helper.
 */
async function assertRejectsAndDisposes(
  promise: Promise<{ dispose(): void }>,
  match?: RegExp,
): Promise<void> {
  let resolved: { dispose(): void } | undefined;
  try {
    resolved = await promise;
  } catch (error) {
    if (match) assert.match(error instanceof Error ? error.message : String(error), match);
    return;
  }
  resolved.dispose();
  assert.fail("expected the promise to reject, but it resolved (the resolved value has been disposed)");
}

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

/**
 * Waits for `CultMeshQuicNativeRuntime.refCount` to stop moving: `dispose()`
 * releases its runtime reference fire-and-forget (`void this.runtime.release()`),
 * so a just-finished test's cleanup can still be landing when the next test
 * starts. A test that snapshots refCount as a baseline without waiting for
 * this first can catch that in-flight decrement mid-test and see refCount
 * undershoot its own "before" value instead of returning to it.
 */
async function waitForRefCountToStabilize(quietMs = 200, timeoutMs = 5_000): Promise<number> {
  const deadline = Date.now() + timeoutMs;
  let last = CultMeshQuicNativeRuntime.refCount;
  let stableSince = Date.now();
  for (;;) {
    await new Promise((resolve) => setTimeout(resolve, 20));
    const current = CultMeshQuicNativeRuntime.refCount;
    if (current !== last) {
      last = current;
      stableSince = Date.now();
    } else if (Date.now() - stableSince >= quietMs) {
      return current;
    }
    if (Date.now() >= deadline) return current;
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

test(
  "20,000 client latest-only frames on one key leave at most 1 pending in the provider's receive queue",
  { timeout: 120_000 },
  async (t) => {
    if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
    const provider = await startProvider();
    try {
      const consumer = await dialProvider(provider);
      try {
        await waitUntil(() => provider.connectionCount === 1);

        // The exact scenario Soul's probe reproduced against the unbounded
        // `readyFrames` array this replaced: a publish-only StreamPixels-style
        // consumer of `receive()` never drains the provider's fan-in queue,
        // so a client hammering one key must not be able to grow it past one
        // entry no matter how many frames it sends.
        const total = 20_000;
        const sends: Promise<void>[] = [];
        for (let sequence = 1; sequence <= total; sequence += 1) {
          sends.push(consumer.sendFrame(testFrame({ delivery: "latest-only", sequence: BigInt(sequence) })));
        }
        await Promise.all(sends);
        await waitUntil(() => provider.receiveQueueSize > 0, 30_000);
        // Let any trailing send_complete events land before asserting the
        // queue has settled, so this isn't racing the last few deliveries.
        await new Promise((resolve) => setTimeout(resolve, 200));

        assert.equal(
          provider.receiveQueueSize,
          1,
          `one client key must coalesce to at most one pending frame, got ${provider.receiveQueueSize}`,
        );
        const frame = await provider.receive();
        assert.equal(frame.sequence, BigInt(total), "the one pending frame must be the newest generation sent");
        assert.equal(provider.receiveQueueSize, 0);
      } finally {
        consumer.dispose();
      }
    } finally {
      provider.dispose();
    }
  },
);

test(
  "many distinct client latest-only keys stay bounded by the key count, not the frame count",
  async (t) => {
    if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
    const provider = await startProvider();
    try {
      const consumer = await dialProvider(provider);
      try {
        await waitUntil(() => provider.connectionCount === 1);

        const keyCount = 50;
        const sendsPerKey = 20;
        const sends: Promise<void>[] = [];
        for (let key = 0; key < keyCount; key += 1) {
          for (let sequence = 1; sequence <= sendsPerKey; sequence += 1) {
            sends.push(
              consumer.sendFrame(
                testFrame({
                  delivery: "latest-only",
                  bodyId: `body:${key}`,
                  sequence: BigInt(sequence),
                }),
              ),
            );
          }
        }
        await Promise.all(sends);
        await waitUntil(() => provider.receiveQueueSize > 0, 10_000);
        await new Promise((resolve) => setTimeout(resolve, 200));

        assert.equal(
          provider.receiveQueueSize,
          keyCount,
          `the queue must hold exactly one pending frame per distinct key, got ${provider.receiveQueueSize}`,
        );
      } finally {
        consumer.dispose();
      }
    } finally {
      provider.dispose();
    }
  },
);

test("reliable-ordered client frames are delivered in order at the provider, never coalesced", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const provider = await startProvider();
  try {
    const consumer = await dialProvider(provider);
    try {
      await waitUntil(() => provider.connectionCount === 1);

      const total = 500;
      for (let sequence = 1; sequence <= total; sequence += 1) {
        await consumer.sendFrame(testFrame({ delivery: "reliable-ordered", sequence: BigInt(sequence) }));
      }
      await waitUntil(() => provider.receiveQueueSize === total, 10_000);

      const received: bigint[] = [];
      for (let i = 0; i < total; i += 1) {
        received.push((await provider.receive()).sequence);
      }
      const expected = Array.from({ length: total }, (_, i) => BigInt(i + 1));
      assert.deepEqual(received, expected, "reliable-ordered frames must never be coalesced or reordered");
    } finally {
      consumer.dispose();
    }
  } finally {
    provider.dispose();
  }
});

test("frames already queued in the provider's receive queue drain before dispose's rejection", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const provider = await startProvider();
  try {
    const consumer = await dialProvider(provider);
    try {
      await waitUntil(() => provider.connectionCount === 1);
      await consumer.sendFrame(testFrame({ delivery: "reliable-ordered", sequence: 1n }));
      await consumer.sendFrame(testFrame({ delivery: "reliable-ordered", sequence: 2n }));
      await waitUntil(() => provider.receiveQueueSize === 2);

      // Dispose while two frames are still sitting in the queue, unread.
      provider.dispose();

      const first = await provider.receive();
      const second = await provider.receive();
      assert.deepEqual([first.sequence, second.sequence], [1n, 2n], "already-queued frames must drain first");
      await assert.rejects(() => provider.receive(), /disposed/, "only a call past the drained backlog rejects");
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
    // Earlier tests' dispose() releases fire-and-forget; wait for the
    // shared refCount to actually settle before treating it as a baseline
    // (see waitForRefCountToStabilize's own doc comment).
    const before = await waitForRefCountToStabilize();
    const provider = await CultMeshQuicRealtimeProvider.listen({
      host: "127.0.0.1",
      port: 0,
      serverCertificate: { pkcs12: readFileSync(FIXTURE_P12), password: "" },
      handshakeTimeoutMs: 30_000,
    });
    try {
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
      // If the certificate check ever regresses into accepting everything
      // (M1), this resolves into a fully attached peer instead of rejecting;
      // assertRejectsAndDisposes() disposes the client-side transport either
      // way, but only the outer `finally` below disposes the provider itself
      // (and the peer it would then hold), which is what actually closes
      // the file-hanging gap M1 found here.
      await assertRejectsAndDisposes(connector.connect(candidate, target), /certificate|rejected/i);

      // The provider's own accept flow is asynchronous (attachPeer awaits a
      // runtime reference before it can even see the rejection); give it a
      // moment to actually start before disposing, so this test exercises a
      // real in-flight pending accept rather than one that never began.
      await new Promise((resolve) => setTimeout(resolve, 100));

      const disposedAt = Date.now();
      provider.dispose();
      await waitUntil(() => CultMeshQuicNativeRuntime.refCount === before, 5_000);
      assert.ok(
        Date.now() - disposedAt < 5_000,
        "dispose() must evict a mid-handshake connection promptly, not wait out its 30s handshake timeout",
      );
    } finally {
      provider.dispose();
    }
  },
);

test("a peer attaching after two latest-only broadcasts for one key receives exactly the newer frame", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const provider = await startProvider();
  try {
    await provider.broadcast(testFrame({ sequence: 1n, payload: new Uint8Array([1]) }));
    await provider.broadcast(testFrame({ sequence: 2n, payload: new Uint8Array([2]) }));

    // Attach after both broadcasts have already resolved: nothing in this
    // peer's outbox came from `broadcast()`'s own fan-out, only from the
    // retained seed on attach.
    const consumer = await dialProvider(provider);
    try {
      const seeded = await consumer.receiveFrame();
      assert.equal(seeded.sequence, 2n, "a late joiner must be seeded with the newest retained generation");
      assert.deepEqual(seeded.payload, new Uint8Array([2]));
    } finally {
      consumer.dispose();
    }
  } finally {
    provider.dispose();
  }
});

test("retention keeps the newer generation when an older one broadcasts afterward", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const provider = await startProvider();
  try {
    await provider.broadcast(testFrame({ sequence: 5n, payload: new Uint8Array([5]) }));
    // An older generation broadcast after a newer one must not overwrite the
    // retained frame: mutating the retention comparison to a plain
    // last-write-wins assignment would regress this.
    await provider.broadcast(testFrame({ sequence: 3n, payload: new Uint8Array([3]) }));

    const consumer = await dialProvider(provider);
    try {
      const seeded = await consumer.receiveFrame();
      assert.equal(seeded.sequence, 5n, "an older generation must not replace the retained newer one");
    } finally {
      consumer.dispose();
    }
  } finally {
    provider.dispose();
  }
});

test("a reliable-ordered frame is never retained and is not delivered to a late joiner", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const provider = await startProvider();
  try {
    const firstConsumer = await dialProvider(provider);
    try {
      await waitUntil(() => provider.connectionCount === 1);
      await provider.broadcast(testFrame({ delivery: "reliable-ordered", sequence: 9n }));
      assert.deepEqual((await firstConsumer.receiveFrame()).sequence, 9n);
    } finally {
      firstConsumer.dispose();
    }
    await waitUntil(() => provider.connectionCount === 0);

    // A second peer attaches after the reliable-ordered frame above and
    // after a real latest-only frame, so the assertion below proves the
    // reliable-ordered frame specifically was never retained, rather than
    // merely proving the provider had nothing at all to seed.
    await provider.broadcast(testFrame({ delivery: "latest-only", sequence: 1n, payload: new Uint8Array([1]) }));
    const secondConsumer = await dialProvider(provider);
    try {
      const seeded = await secondConsumer.receiveFrame();
      assert.equal(seeded.delivery, "latest-only");
      assert.equal(seeded.sequence, 1n, "only the latest-only frame may be seeded, never the reliable-ordered one");
    } finally {
      secondConsumer.dispose();
    }
  } finally {
    provider.dispose();
  }
});

test(
  "fix 1: a peer stays connected and keeps receiving well past handshakeTimeoutMs",
  { timeout: 10_000 },
  async (t) => {
    if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
    // Must fail on the pre-fix code: the buffering-handler/attachPeer swap
    // there loses `state.connected` to whichever handler happens to be
    // registered when CONNECTED actually arrives, and the handshake timer
    // evicts a fully healthy peer at this deadline regardless.
    const handshakeTimeoutMs = 500;
    const provider = await CultMeshQuicRealtimeProvider.listen({
      host: "127.0.0.1",
      port: 0,
      serverCertificate: { pkcs12: readFileSync(FIXTURE_P12), password: "" },
      handshakeTimeoutMs,
    });
    try {
      const consumer = await dialProvider(provider);
      try {
        await waitUntil(() => provider.connectionCount === 1);
        await new Promise((resolve) => setTimeout(resolve, handshakeTimeoutMs * 3));
        assert.equal(provider.connectionCount, 1, "a connected peer must not be evicted by the handshake timer");

        await provider.broadcast(testFrame({ delivery: "reliable-ordered", sequence: 1n }));
        const received = await consumer.receiveFrame();
        assert.equal(received.sequence, 1n, "a peer past the handshake deadline must still receive frames");
      } finally {
        consumer.dispose();
      }
    } finally {
      provider.dispose();
    }
  },
);

test("fix 2: an invalid frame throws from broadcast, and connectionCount is unchanged", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const provider = await startProvider();
  try {
    const consumer = await dialProvider(provider);
    try {
      await waitUntil(() => provider.connectionCount === 1);
      // A 70,000-byte channelId exceeds the wire's 0xffff identity limit:
      // `encodeRealtimeFrame` throws. Before fix 2, encoding happened once
      // per peer inside `sendFrame`, so the same failure evicted every peer
      // while `broadcast()` itself reported success.
      await assert.rejects(
        provider.broadcast(testFrame({ delivery: "reliable-ordered", channelId: "x".repeat(70_000) })),
        /exceeds the QUIC wire limit/i,
      );
      assert.equal(provider.connectionCount, 1, "an invalid frame must not evict any peer");

      // The peer must still be usable afterward: prove the connection itself
      // survived, not merely that the count field wasn't decremented.
      await provider.broadcast(testFrame({ delivery: "reliable-ordered", sequence: 42n }));
      assert.equal((await consumer.receiveFrame()).sequence, 42n);
    } finally {
      consumer.dispose();
    }
  } finally {
    provider.dispose();
  }
});

test("fix 2: a latest-only invalid frame also throws from broadcast without evicting any peer", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const provider = await startProvider();
  try {
    const consumer = await dialProvider(provider);
    try {
      await waitUntil(() => provider.connectionCount === 1);
      await assert.rejects(
        provider.broadcast(testFrame({ delivery: "latest-only", channelId: "x".repeat(70_000) })),
        /exceeds the QUIC wire limit/i,
      );
      assert.equal(provider.connectionCount, 1, "an invalid latest-only frame must not evict any peer");
    } finally {
      consumer.dispose();
    }
  } finally {
    provider.dispose();
  }
});

test("fix 3: advertisedHost is used in place of the bind address, and round-trips through the module's parser", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const provider = await CultMeshQuicRealtimeProvider.listen({
    host: "0.0.0.0",
    port: 0,
    serverCertificate: { pkcs12: readFileSync(FIXTURE_P12), password: "" },
    advertisedHost: "streampixels.gamecult.org",
  });
  try {
    assert.match(
      provider.advertisedEndpoint,
      /^cultmesh-state\+quic:\/\/streampixels\.gamecult\.org:\d+\?cert-sha256=[0-9A-F]{64}$/,
    );
    const parsed = parseQuicRealtimeEndpoint(provider.advertisedEndpoint);
    assert.equal(parsed.host, "streampixels.gamecult.org");
    assert.equal(parsed.port, Number(new URL(provider.advertisedEndpoint).port));
    assert.ok(parsed.certificateSha256);
  } finally {
    provider.dispose();
  }
});

test("fix 3: an IPv6 bind advertises a bracketed literal that round-trips through the module's parser", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const provider = await CultMeshQuicRealtimeProvider.listen({
    host: "::1",
    port: 0,
    serverCertificate: { pkcs12: readFileSync(FIXTURE_P12), password: "" },
  });
  try {
    assert.match(
      provider.advertisedEndpoint,
      /^cultmesh-state\+quic:\/\/\[::1\]:\d+\?cert-sha256=[0-9A-F]{64}$/,
    );
    // `URL`'s own `hostname` keeps the brackets for an IPv6 literal (unlike
    // the unbracketed form `formatEndpointHost` accepted as input); the
    // round trip's job is proving the endpoint parses at all, not stripping
    // brackets the WHATWG URL parser itself never strips.
    const parsed = parseQuicRealtimeEndpoint(provider.advertisedEndpoint);
    assert.equal(parsed.host, "[::1]");
  } finally {
    provider.dispose();
  }
});

test("fix 4: receive() with an already-aborted signal rejects immediately instead of hanging", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const provider = await startProvider();
  try {
    const controller = new AbortController();
    controller.abort();
    await assert.rejects(provider.receive(controller.signal), /aborted/i);
  } finally {
    provider.dispose();
  }
});

test("P3: a latest-only send failure evicts the peer without broadcast() throwing to the caller", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const provider = await startProvider();
  try {
    const consumer = await dialProvider(provider);
    await waitUntil(() => provider.connectionCount === 1);
    consumer.dispose();
    // Mirrors the existing reliable-ordered eviction test, but for the
    // outbox's own send failure path (`CultMeshQuicRealtimeProviderOutbox.pump`'s
    // catch, which disposes the peer and reports failure through
    // `onSendFailure`) rather than `broadcast()`'s direct per-peer catch.
    //
    // What made this flaky: unlike the reliable-ordered version,
    // `broadcast()` never awaits a latest-only peer's actual send — it only
    // hands the frame to that peer's outbox and returns (see
    // `CultMeshQuicRealtimeProvider.broadcast`). A `while (Date.now() <
    // deadline)` loop therefore does no real I/O per iteration; it just
    // spins encoding and enqueueing frames as fast as the CPU allows,
    // starving the event-loop turns the native pump needs to actually
    // observe the dead connection and fire `send_complete`/
    // `CONNECTION_SHUTDOWN`. Under any extra load in the test process
    // (exactly what running under mutation testing adds, regardless of
    // which line was mutated) that starvation can burn the whole wall-clock
    // bound without ever landing an attempt past the point the connection
    // is actually dead — a failure with nothing to do with the mutant under
    // test. Raising the bound only buys more spinning, not more real
    // attempts. Fixed by decoupling "how many attempts to seed" (a small,
    // fixed count, each with a real yield) from "how long to wait for the
    // result" (a single `waitUntil` afterward, which is what actually
    // bounds this test).
    for (let sequence = 0n; sequence < 20n && provider.connectionCount > 0; sequence += 1n) {
      await provider.broadcast(testFrame({ delivery: "latest-only", sequence, bodyId: `body:${sequence}` }));
      await new Promise((resolve) => setTimeout(resolve, 25));
    }
    await waitUntil(() => provider.connectionCount === 0);
    assert.equal(provider.connectionCount, 0, "the dead peer must be evicted, not counted forever");
  } finally {
    provider.dispose();
  }
});

test("I1: an inbox key keeps its first-arrival position when a newer frame overwrites it", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const provider = await startProvider();
  try {
    const consumer = await dialProvider(provider);
    try {
      await waitUntil(() => provider.connectionCount === 1);

      // Key A arrives first, then key B, then A is republished with a new
      // value while still unread: `CultMeshRealtimeInbox.publish`'s
      // coalescing must overwrite A's *value* in place, not move it to the
      // back of the queue behind B. `received` is exactly that inbox,
      // reached through the provider's client fan-in.
      await consumer.sendFrame(testFrame({ delivery: "latest-only", bodyId: "body:A", sequence: 1n }));
      await waitUntil(() => provider.receiveQueueSize === 1);
      await consumer.sendFrame(testFrame({ delivery: "latest-only", bodyId: "body:B", sequence: 1n }));
      await waitUntil(() => provider.receiveQueueSize === 2);
      await consumer.sendFrame(testFrame({ delivery: "latest-only", bodyId: "body:A", sequence: 2n }));
      // Still 2: the second A publish must coalesce onto the already-queued
      // A entry, not add a third.
      await new Promise((resolve) => setTimeout(resolve, 200));
      assert.equal(provider.receiveQueueSize, 2, "a republish of a still-pending key must not grow the queue");

      const first = await provider.receive();
      const second = await provider.receive();
      assert.equal(first.bodyId, "body:A", "A must still drain first: its position must survive the overwrite");
      assert.equal(first.sequence, 2n, "A's value must be the newer, overwritten one");
      assert.equal(second.bodyId, "body:B");
    } finally {
      consumer.dispose();
    }
  } finally {
    provider.dispose();
  }
});

test("L1: the outbox keeps one send in flight per peer; the next frame dequeues only after send_complete", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const provider = await startProvider();
  try {
    const consumer = await dialProvider(provider);
    try {
      await waitUntil(() => provider.connectionCount === 1);

      // Prototype-patched for the life of this test, restored in `finally`:
      // the provider builds its peer transport internally, so this is the
      // only vantage point outside the module from which to observe
      // concurrency of `sendEncodedFrame` calls, which is what
      // `CultMeshQuicRealtimeProviderOutbox.pump` serializes one-at-a-time
      // against `send_complete`.
      const original = CultMeshQuicRealtimeTransportClass.prototype.sendEncodedFrame;
      let active = 0;
      let maxActive = 0;
      CultMeshQuicRealtimeTransportClass.prototype.sendEncodedFrame = function (
        this: InstanceType<typeof CultMeshQuicRealtimeTransportClass>,
        delivery: "reliable-ordered" | "latest-only",
        encoded: Uint8Array,
      ) {
        active += 1;
        maxActive = Math.max(maxActive, active);
        return original.call(this, delivery, encoded).finally(() => {
          active -= 1;
        });
      };
      try {
        const total = 20;
        const sends: Promise<void>[] = [];
        for (let key = 0; key < total; key += 1) {
          sends.push(provider.broadcast(testFrame({ bodyId: `body:${key}`, sequence: 1n })));
        }
        await Promise.all(sends);

        const seen = new Set<string>();
        while (seen.size < total) {
          const frame = await consumer.receiveFrame();
          seen.add(frame.bodyId);
        }
      } finally {
        CultMeshQuicRealtimeTransportClass.prototype.sendEncodedFrame = original;
      }
      assert.equal(maxActive, 1, "the outbox pump must never have two sends in flight for one peer at once");
    } finally {
      consumer.dispose();
    }
  } finally {
    provider.dispose();
  }
});

test("P15: broadcast() after dispose rejects instead of touching a torn-down provider", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const provider = await startProvider();
  provider.dispose();
  await assert.rejects(provider.broadcast(testFrame()), /disposed/i);
});

test("P17: an accept arriving after dispose is refused, not attached as a peer", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const provider = await startProvider();
  const runtime = await CultMeshQuicNativeRuntime.open();
  let connectionId: bigint | undefined;
  try {
    // A real native connection handle this test owns outright (never dialed
    // anywhere reachable, so shutting it down is safe): stands in for the
    // handle a genuine NEW_CONNECTION event would have carried, without
    // fabricating a bogus native pointer. `acceptConnection` is private;
    // reaching it directly is what lets this test drive the exact race
    // (an accept event landing after `dispose()` has already run) without
    // depending on real listener-close timing to reproduce it.
    connectionId = runtime.connectionOpen("127.0.0.1", 1);
    provider.dispose();
    // This synthetic connection never reaches CONNECTED either way (it was
    // never dialed anywhere), so `connectionCount` alone cannot distinguish
    // "refused" from "attached but still handshaking". `retain()` is
    // synchronous and only the attach path calls it: a stable `refCount`
    // across the call proves no transport (and no runtime reference) was
    // ever allocated for this connection.
    const beforeRefCount = CultMeshQuicNativeRuntime.refCount;
    assert.doesNotThrow(() =>
      (provider as unknown as { acceptConnection(id: bigint): void }).acceptConnection(connectionId!),
    );
    assert.equal(
      CultMeshQuicNativeRuntime.refCount,
      beforeRefCount,
      "a post-dispose accept must never retain a runtime reference for a new transport",
    );
    assert.equal(provider.connectionCount, 0, "a post-dispose accept must never join peers");
  } finally {
    if (connectionId !== undefined) {
      try {
        runtime.connectionShutdown(connectionId, 0n);
      } catch {
        // Best-effort: acceptConnection's own disposed-path may already have shut it down.
      }
    }
    await runtime.release();
  }
});

test("fix 1: a publish-only provider faults a client that sends to it, and its memory stays empty", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const provider = await CultMeshQuicRealtimeProvider.listen({
    host: "127.0.0.1",
    port: 0,
    serverCertificate: { pkcs12: readFileSync(FIXTURE_P12), password: "" },
    acceptClientFrames: false,
  });
  try {
    const consumer = await dialProvider(provider);
    try {
      await waitUntil(() => provider.connectionCount === 1);

      // receive() itself rejects clearly rather than hanging forever.
      await assert.rejects(provider.receive(), /publish-only/i);

      // Any inbound stream from the client faults that connection. The
      // client's own `sendFrame` promise settles on its *local* stream
      // send completing (MsQuic hands the bytes off and reports
      // send_complete without waiting for the peer to process them), so it
      // may well resolve even though the server already faulted its side —
      // the proof this test owns is the server's state, not the client's
      // send outcome, so the client send is fired and ignored either way.
      void consumer.sendFrame(testFrame({ delivery: "latest-only" })).catch(() => {});
      await waitUntil(() => provider.connectionCount === 0);
      assert.equal(provider.receiveQueueSize, 0, "no frame from a faulted publish-only client may be queued");

      // The provider itself is otherwise healthy: broadcasting still works
      // for a freshly attached peer.
      const second = await dialProvider(provider);
      try {
        await waitUntil(() => provider.connectionCount === 1);
        await provider.broadcast(testFrame({ delivery: "reliable-ordered", sequence: 1n }));
        assert.equal((await second.receiveFrame()).sequence, 1n);
      } finally {
        second.dispose();
      }
    } finally {
      consumer.dispose();
    }
  } finally {
    provider.dispose();
  }
});

test("fix 2: a connection beyond maxConnections is refused and existing peers are unaffected", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const provider = await CultMeshQuicRealtimeProvider.listen({
    host: "127.0.0.1",
    port: 0,
    serverCertificate: { pkcs12: readFileSync(FIXTURE_P12), password: "" },
    maxConnections: 1,
  });
  try {
    const first = await dialProvider(provider);
    try {
      await waitUntil(() => provider.connectionCount === 1);

      // Refused at accept: never reaches CONNECTED, never becomes a peer.
      await assertRejectsAndDisposes(dialProvider(provider), /certificate|rejected|closed|shut|timed out/i);
      // Give the refusal a moment to be visible either way, then prove the
      // existing peer was never touched.
      await new Promise((resolve) => setTimeout(resolve, 200));
      assert.equal(provider.connectionCount, 1, "the existing peer must be unaffected by a refused connection");

      await provider.broadcast(testFrame({ delivery: "reliable-ordered", sequence: 7n }));
      assert.equal((await first.receiveFrame()).sequence, 7n, "the existing peer must still be fully usable");
    } finally {
      first.dispose();
    }
  } finally {
    provider.dispose();
  }
});
