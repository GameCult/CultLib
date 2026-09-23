// The TypeScript QUIC realtime consumer, proven two ways: directly against a
// raw listener opened through the native runtime (no provider semantics yet,
// mirrors `CultMeshNativeQuicRealtimeTransportTests.cs`), and through
// `CultMeshQuicRealtimeSessionManager` with a static lookup source and routes
// signed with `node:crypto`, mirroring `CultMeshQuicRealtimeTransportTests.cs`
// and `CultMeshSessions.cs`'s realtime binding.
//
// Skips instead of failing when no native bridge is available; see
// `realtime-quic-native.test.ts` for why.

import assert from "node:assert/strict";
import { createHash, generateKeyPairSync, sign as cryptoSign, type KeyObject } from "node:crypto";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";

import {
  canonicalRoute,
  type CultMeshAuthorityRouteView,
  type CultMeshAuthorityTrustPolicy,
  type CultMeshP256PublicKey,
} from "cultnet-ts";
import type { CultMeshAuthorityRouteMessage, CultMeshVerseDescriptorMessage } from "cultnet-ts/contracts";

import {
  CultMeshQuicNativeRuntime,
  CULTMESH_QUIC_EVENT_LISTENER_NEW_CONNECTION,
  CULTMESH_QUIC_STREAM_LATEST_ONLY,
  CULTMESH_QUIC_STREAM_RELIABLE,
} from "../src/realtime-quic-native";
import {
  CultMeshQuicRealtimeConnector,
  CultMeshQuicRealtimeSessionManager,
  CultMeshStaticRealtimeLookupSource,
  CULTMESH_REALTIME_STATE_PROTOCOL_ID,
  type CultMeshQuicRealtimeTransport,
  type CultMeshRealtimeCandidate,
  type CultMeshRealtimeTarget,
  type CultMeshRealtimeTransport,
  type CultMeshRealtimeTransportConnector,
} from "../src/realtime-quic";
import { encodeRealtimeFrame, type CultMeshRealtimeFrame } from "../src/realtime-wire";
import { nativeBridgeAvailable } from "./support/native-bridge";

// __dirname at runtime is dist-test/test; fixtures are binary and are never
// compiled/copied there, so they are read from their source location, two
// levels up.
const FIXTURE_P12 = join(__dirname, "..", "..", "test", "fixtures", "quic-test.p12");
const FIXTURE_DER = join(__dirname, "..", "..", "test", "fixtures", "quic-test-cert.der");

function fixturePinHex(): string {
  return createHash("sha256").update(readFileSync(FIXTURE_DER)).digest("hex");
}

/** A bare loopback listener over the raw native runtime, no provider semantics. */
class RawQuicListener {
  private constructor(
    private readonly runtime: CultMeshQuicNativeRuntime,
    private readonly listenerId: bigint,
    readonly boundPort: number,
  ) {}

  static async open(): Promise<RawQuicListener> {
    const runtime = await CultMeshQuicNativeRuntime.open();
    try {
      const pkcs12 = readFileSync(FIXTURE_P12);
      const { listenerId, boundPort } = runtime.listenerOpen("127.0.0.1", 0, pkcs12, "");
      return new RawQuicListener(runtime, listenerId, boundPort);
    } catch (error) {
      // A runtime opened above and never handed to a caller would otherwise
      // leak: the shared runtime's refcount never reaches zero, its MsQuic
      // registration and worker threads never tear down, and a process that
      // hits this on every one of several tests never exits on its own.
      await runtime.release();
      throw error;
    }
  }

  /** Waits for one inbound connection and returns its native connection id. */
  async acceptOnce(): Promise<bigint> {
    return await new Promise<bigint>((resolve) => {
      this.runtime.onListenerEvent(this.listenerId, (event) => {
        if (event.type === CULTMESH_QUIC_EVENT_LISTENER_NEW_CONNECTION) resolve(event.connectionId);
      });
    });
  }

  /** Sends one frame to an accepted connection on a fresh stream of `kind`. */
  sendFrame(connectionId: bigint, frame: CultMeshRealtimeFrame, kind: number): void {
    const streamId = this.runtime.streamOpen(connectionId, kind);
    this.runtime.streamSendFrame(streamId, encodeRealtimeFrame(frame), true);
  }

  /** Sends arbitrary bytes (not necessarily a valid encoded frame) on a fresh stream of `kind`. */
  sendRaw(connectionId: bigint, bytes: Uint8Array, kind: number): void {
    const streamId = this.runtime.streamOpen(connectionId, kind);
    this.runtime.streamSendFrame(streamId, bytes, true);
  }

  /** Shuts down one accepted connection from this side, as a peer disconnect. */
  shutdownConnection(connectionId: bigint, code = 0n): void {
    this.runtime.connectionShutdown(connectionId, code);
  }

  async close(): Promise<void> {
    this.runtime.listenerClose(this.listenerId);
    await this.runtime.release();
  }
}

/**
 * `assert.rejects`, but safe against a mutant or a real regression that
 * makes the promise resolve instead: a transport that unexpectedly connects
 * is disposed before the assertion fails, rather than left live with an open
 * native connection and an outstanding runtime reference. A bare
 * `assert.rejects(connector.connect(...))` hung the whole file exactly this
 * way under M1 (the certificate-accept mutation): three tests failed their
 * assertion correctly, but each left a connected, undisposed transport
 * behind, and Node's test runner refuses to exit while any promise or
 * native handle is still outstanding.
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

test("connect with the correct pin succeeds and receives a frame", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const listener = await RawQuicListener.open();
  try {
    const target: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:aetheria.daemon" };
    const candidate: CultMeshRealtimeCandidate = {
      endpoint: `cultmesh-state+quic://127.0.0.1:${listener.boundPort}?cert-sha256=${fixturePinHex()}`,
      authorityRuntimeId: target.authorityRuntimeId,
      priority: 0,
      generation: "gen-1",
    };
    const connector = new CultMeshQuicRealtimeConnector();
    const accepted = listener.acceptOnce();
    const transport = await connector.connect(candidate, target);
    try {
      assert.equal(transport.transportId, "msquic-realtime");
      const connectionId = await accepted;
      listener.sendFrame(connectionId, testFrame(), CULTMESH_QUIC_STREAM_LATEST_ONLY);
      const received = await transport.receiveFrame();
      assert.equal(received.bodyId, "body:aetheria:entities");
      assert.equal(received.sequence, 1n);
    } finally {
      transport.dispose();
    }
  } finally {
    await listener.close();
  }
});

test("connect refuses a wrong advertised pin", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const listener = await RawQuicListener.open();
  try {
    const target: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:aetheria.daemon" };
    const candidate: CultMeshRealtimeCandidate = {
      endpoint: `cultmesh-state+quic://127.0.0.1:${listener.boundPort}?cert-sha256=${"0".repeat(64)}`,
      authorityRuntimeId: target.authorityRuntimeId,
      priority: 0,
      generation: "gen-1",
    };
    const connector = new CultMeshQuicRealtimeConnector();
    await assertRejectsAndDisposes(connector.connect(candidate, target), /certificate|rejected/i);
  } finally {
    await listener.close();
  }
});

test("connect refuses a missing certificate pin when no validator is supplied", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const listener = await RawQuicListener.open();
  try {
    const target: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:aetheria.daemon" };
    const candidate: CultMeshRealtimeCandidate = {
      endpoint: `cultmesh-state+quic://127.0.0.1:${listener.boundPort}`,
      authorityRuntimeId: target.authorityRuntimeId,
      priority: 0,
      generation: "gen-1",
    };
    const connector = new CultMeshQuicRealtimeConnector();
    await assertRejectsAndDisposes(connector.connect(candidate, target), /certificate|rejected/i);
  } finally {
    await listener.close();
  }
});

test("a caller-supplied validator observes the target identity and the DER bytes", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const listener = await RawQuicListener.open();
  try {
    const target: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:aetheria.daemon" };
    const candidate: CultMeshRealtimeCandidate = {
      endpoint: `cultmesh-state+quic://127.0.0.1:${listener.boundPort}`,
      authorityRuntimeId: target.authorityRuntimeId,
      priority: 0,
      generation: "gen-1",
    };
    let observedTarget: CultMeshRealtimeTarget | undefined;
    let observedDerLength = 0;
    const connector = new CultMeshQuicRealtimeConnector({
      validateProviderCertificate: (validatorTarget, der) => {
        observedTarget = validatorTarget;
        observedDerLength = der.byteLength;
        return true;
      },
    });
    const transport = await connector.connect(candidate, target);
    transport.dispose();
    assert.deepEqual(observedTarget, target);
    assert.ok(observedDerLength > 0);
  } finally {
    await listener.close();
  }
});

test("a late older generation is dropped by the receive-side filter", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const listener = await RawQuicListener.open();
  try {
    const target: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:aetheria.daemon" };
    const candidate: CultMeshRealtimeCandidate = {
      endpoint: `cultmesh-state+quic://127.0.0.1:${listener.boundPort}?cert-sha256=${fixturePinHex()}`,
      authorityRuntimeId: target.authorityRuntimeId,
      priority: 0,
      generation: "gen-1",
    };
    const connector = new CultMeshQuicRealtimeConnector();
    const accepted = listener.acceptOnce();
    const transport = await connector.connect(candidate, target);
    try {
      const connectionId = await accepted;
      listener.sendFrame(connectionId, testFrame({ producerEpoch: 8n, sequence: 0n }), CULTMESH_QUIC_STREAM_LATEST_ONLY);
      const latest = await transport.receiveFrame();
      assert.equal(latest.producerEpoch, 8n);
      assert.equal(latest.sequence, 0n);

      listener.sendFrame(connectionId, testFrame({ producerEpoch: 7n, sequence: 999n }), CULTMESH_QUIC_STREAM_LATEST_ONLY);
      const staleTimeout = new AbortController();
      const timer = setTimeout(() => staleTimeout.abort(), 300);
      await assert.rejects(transport.receiveFrame(staleTimeout.signal));
      clearTimeout(timer);
    } finally {
      transport.dispose();
    }
  } finally {
    await listener.close();
  }
});

// ---------------------------------------------------------------------------
// Trust negatives, through the session manager with a static lookup source.
// ---------------------------------------------------------------------------

function base64UrlToBase64(value: string): string {
  let base64 = value.replace(/-/g, "+").replace(/_/g, "/");
  while (base64.length % 4 !== 0) base64 += "=";
  return base64;
}

let odinRootSequence = 0;

function generateOdinRoot(): { privateKey: KeyObject; publicKey: CultMeshP256PublicKey } {
  const { publicKey, privateKey } = generateKeyPairSync("ec", { namedCurve: "prime256v1" });
  const jwk = publicKey.export({ format: "jwk" }) as { x: string; y: string };
  odinRootSequence += 1;
  return {
    privateKey,
    publicKey: {
      keyId: `odin-root-${odinRootSequence}`,
      x: base64UrlToBase64(jwk.x),
      y: base64UrlToBase64(jwk.y),
    },
  };
}

interface SignedRouteOptions {
  readonly verseId: string;
  readonly authorityRuntimeId: string;
  readonly endpoint: string;
  readonly protocolIds: readonly string[];
  readonly privateKey: KeyObject;
  readonly odinKeyId: string;
  readonly priority?: number;
  readonly generation?: string;
  readonly issuedAtUnixMilliseconds?: number;
  readonly expiresAtUnixMilliseconds?: number;
  readonly providerKey?: CultMeshP256PublicKey;
  readonly corruptSignature?: boolean;
}

function signRoute(options: SignedRouteOptions): CultMeshAuthorityRouteMessage {
  const priority = options.priority ?? 0;
  const generation = options.generation ?? "";
  const issuedAt = options.issuedAtUnixMilliseconds ?? Date.now() - 60_000;
  const expiresAt = options.expiresAtUnixMilliseconds ?? Date.now() + 3_600_000;
  const providerKey = options.providerKey ?? { keyId: "provider-1", x: "unused", y: "unused" };
  const view: CultMeshAuthorityRouteView = {
    verseId: options.verseId,
    authorityRuntimeId: options.authorityRuntimeId,
    endpoint: options.endpoint,
    protocolIds: options.protocolIds,
    priority,
    generation,
    certificate: {
      providerKey,
      odinKeyId: options.odinKeyId,
      issuedAtUnixMilliseconds: issuedAt,
      expiresAtUnixMilliseconds: expiresAt,
      signature: "",
    },
  };
  const payload = canonicalRoute(view);
  const signatureBytes = options.corruptSignature
    ? Buffer.alloc(64, 0x41)
    : (cryptoSign("sha256", Buffer.from(payload), { key: options.privateKey, dsaEncoding: "ieee-p1363" }) as Buffer);
  return {
    authorityRuntimeId: options.authorityRuntimeId,
    endpoint: options.endpoint,
    protocolIds: [...options.protocolIds],
    priority,
    generation,
    certificate: {
      providerKeyId: providerKey.keyId,
      providerPublicKeyX: providerKey.x,
      providerPublicKeyY: providerKey.y,
      odinKeyId: options.odinKeyId,
      issuedAtUnixMilliseconds: issuedAt,
      expiresAtUnixMilliseconds: expiresAt,
      signature: signatureBytes.toString("base64"),
    },
  };
}

function verseWithRoute(verseId: string, route: CultMeshAuthorityRouteMessage): CultMeshVerseDescriptorMessage {
  return {
    verseId,
    displayName: verseId,
    authorityModel: "operator-cluster",
    compatibility: { transportVersion: "cultmesh.v1", rulesHash: "test", compatibleVerseIds: [], requiredPluginIds: [], optionalPluginIds: [] },
    discoveryEndpoints: [route.endpoint],
    authorityRuntimeIds: [route.authorityRuntimeId],
    authorityRoutes: [route],
  };
}

const unreachableConnector: CultMeshRealtimeTransportConnector = {
  connectorId: "unreachable",
  priority: 0,
  canConnect: () => true,
  connect: async () => {
    throw new Error("a trust-refused route must never reach a connector");
  },
};

test("trust negative (a): an expired certificate is refused", async () => {
  const target: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:aetheria.daemon" };
  const root = generateOdinRoot();
  const route = signRoute({
    verseId: target.verseId,
    authorityRuntimeId: target.authorityRuntimeId,
    endpoint: "cultmesh-state+quic://127.0.0.1:9443",
    protocolIds: [CULTMESH_REALTIME_STATE_PROTOCOL_ID],
    privateKey: root.privateKey,
    odinKeyId: root.publicKey.keyId,
    issuedAtUnixMilliseconds: Date.now() - 120_000,
    expiresAtUnixMilliseconds: Date.now() - 60_000,
  });
  const trust: CultMeshAuthorityTrustPolicy = { mode: "authenticated-remote", odinRoots: [root.publicKey] };
  const manager = new CultMeshQuicRealtimeSessionManager({
    lookupSource: new CultMeshStaticRealtimeLookupSource([verseWithRoute(target.verseId, route)]),
    trust,
    connectors: [unreachableConnector],
  });
  await assertRejectsAndDisposes(manager.connect(target), /valid|expired/i);
});

test("trust negative (b): an unsigned remote route is refused", async () => {
  const target: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:aetheria.daemon" };
  const route: CultMeshAuthorityRouteMessage = {
    authorityRuntimeId: target.authorityRuntimeId,
    endpoint: "cultmesh-state+quic://198.51.100.7:9443",
    protocolIds: [CULTMESH_REALTIME_STATE_PROTOCOL_ID],
    priority: 0,
    generation: "gen-1",
  };
  const trust: CultMeshAuthorityTrustPolicy = { mode: "authenticated-remote", odinRoots: [] };
  const manager = new CultMeshQuicRealtimeSessionManager({
    lookupSource: new CultMeshStaticRealtimeLookupSource([verseWithRoute(target.verseId, route)]),
    trust,
    connectors: [unreachableConnector],
  });
  await assertRejectsAndDisposes(manager.connect(target), /Odin-signed/i);
});

test("trust negative (c): a route signed by an untrusted Odin root is refused", async () => {
  const target: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:aetheria.daemon" };
  const signer = generateOdinRoot();
  const trustedRoot = generateOdinRoot();
  const route = signRoute({
    verseId: target.verseId,
    authorityRuntimeId: target.authorityRuntimeId,
    endpoint: "cultmesh-state+quic://127.0.0.1:9443",
    protocolIds: [CULTMESH_REALTIME_STATE_PROTOCOL_ID],
    privateKey: signer.privateKey,
    odinKeyId: signer.publicKey.keyId,
  });
  const trust: CultMeshAuthorityTrustPolicy = { mode: "authenticated-remote", odinRoots: [trustedRoot.publicKey] };
  const manager = new CultMeshQuicRealtimeSessionManager({
    lookupSource: new CultMeshStaticRealtimeLookupSource([verseWithRoute(target.verseId, route)]),
    trust,
    connectors: [unreachableConnector],
  });
  await assertRejectsAndDisposes(manager.connect(target), /not trusted/i);
});

test("trust negative (d): a route that verifies but pins the wrong certificate is refused at the transport", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const listener = await RawQuicListener.open();
  try {
    const target: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:aetheria.daemon" };
    const root = generateOdinRoot();
    const route = signRoute({
      verseId: target.verseId,
      authorityRuntimeId: target.authorityRuntimeId,
      endpoint: `cultmesh-state+quic://127.0.0.1:${listener.boundPort}?cert-sha256=${"0".repeat(64)}`,
      protocolIds: [CULTMESH_REALTIME_STATE_PROTOCOL_ID],
      privateKey: root.privateKey,
      odinKeyId: root.publicKey.keyId,
    });
    const trust: CultMeshAuthorityTrustPolicy = { mode: "authenticated-remote", odinRoots: [root.publicKey] };
    const manager = new CultMeshQuicRealtimeSessionManager({
      lookupSource: new CultMeshStaticRealtimeLookupSource([verseWithRoute(target.verseId, route)]),
      trust,
      connectors: [new CultMeshQuicRealtimeConnector()],
    });
    await assertRejectsAndDisposes(manager.connect(target), /certificate|rejected/i);
  } finally {
    await listener.close();
  }
});

test("trust negative (e): a transport that fails isVerifiedFor is disposed and rejected", async () => {
  const target: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:aetheria.daemon" };
  const root = generateOdinRoot();
  const route = signRoute({
    verseId: target.verseId,
    authorityRuntimeId: target.authorityRuntimeId,
    endpoint: "cultmesh-state+quic://127.0.0.1:9443",
    protocolIds: [CULTMESH_REALTIME_STATE_PROTOCOL_ID],
    privateKey: root.privateKey,
    odinKeyId: root.publicKey.keyId,
    generation: "route-generation-1",
  });
  const trust: CultMeshAuthorityTrustPolicy = { mode: "authenticated-remote", odinRoots: [root.publicKey] };

  let disposed = false;
  const staleTransport: CultMeshRealtimeTransport = {
    transportId: "msquic-realtime",
    endpoint: route.endpoint,
    async sendFrame() {},
    async receiveFrame() {
      return testFrame();
    },
    dispose() {
      disposed = true;
    },
    // A replayed/stale generation: the transport proves a different route
    // generation than the one the session manager just verified.
    isVerifiedFor: () => false,
  };
  const staleConnector: CultMeshRealtimeTransportConnector = {
    connectorId: "stale",
    priority: 0,
    canConnect: () => true,
    connect: async () => staleTransport,
  };
  const manager = new CultMeshQuicRealtimeSessionManager({
    lookupSource: new CultMeshStaticRealtimeLookupSource([verseWithRoute(target.verseId, route)]),
    trust,
    connectors: [staleConnector],
  });

  await assertRejectsAndDisposes(manager.connect(target), /did not prove|No realtime state path/i);
  assert.equal(disposed, true, "the session manager must dispose a transport that fails isVerifiedFor");
});

test("M3: a real connected transport's isVerifiedFor rejects a generation other than the one it connected with", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const listener = await RawQuicListener.open();
  try {
    const target: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:aetheria.daemon" };
    const candidate: CultMeshRealtimeCandidate = {
      endpoint: `cultmesh-state+quic://127.0.0.1:${listener.boundPort}?cert-sha256=${fixturePinHex()}`,
      authorityRuntimeId: target.authorityRuntimeId,
      priority: 0,
      generation: "route-generation-1",
    };
    const connector = new CultMeshQuicRealtimeConnector();
    const transport = await connector.connect(candidate, target);
    try {
      assert.equal(
        transport.isVerifiedFor(target.verseId, target.authorityRuntimeId, CULTMESH_REALTIME_STATE_PROTOCOL_ID, "route-generation-1"),
        true,
        "the generation it actually connected with must verify",
      );
      assert.equal(
        transport.isVerifiedFor(target.verseId, target.authorityRuntimeId, CULTMESH_REALTIME_STATE_PROTOCOL_ID, "route-generation-2"),
        false,
        "a mutated/replayed generation must not verify against a real transport",
      );
    } finally {
      transport.dispose();
    }
  } finally {
    await listener.close();
  }
});

test("trust negative (e2): a real transport whose generation no longer matches the freshly verified route is disposed", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const listener = await RawQuicListener.open();
  try {
    const target: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:aetheria.daemon" };
    const root = generateOdinRoot();
    const signedRoute = signRoute({
      verseId: target.verseId,
      authorityRuntimeId: target.authorityRuntimeId,
      endpoint: `cultmesh-state+quic://127.0.0.1:${listener.boundPort}?cert-sha256=${fixturePinHex()}`,
      protocolIds: [CULTMESH_REALTIME_STATE_PROTOCOL_ID],
      privateKey: root.privateKey,
      odinKeyId: root.publicKey.keyId,
      generation: "route-generation-1",
    });
    const trust: CultMeshAuthorityTrustPolicy = { mode: "authenticated-remote", odinRoots: [root.publicKey] };
    const realConnector = new CultMeshQuicRealtimeConnector();
    let connectedTransport: CultMeshRealtimeTransport | undefined;
    // A real transport, connected for real against the raw listener, but
    // stamped with a generation different from the one the session manager
    // just verified — the way a stale cached transport would look after its
    // route rotated underneath it.
    const replayConnector: CultMeshRealtimeTransportConnector = {
      connectorId: "replay",
      priority: 0,
      canConnect: (c) => realConnector.canConnect(c),
      connect: async (c, t, signal) => {
        const accepted = listener.acceptOnce();
        const transport = await realConnector.connect({ ...c, generation: "route-generation-1-stale" }, t, signal);
        await accepted;
        connectedTransport = transport;
        return transport;
      },
    };
    const manager = new CultMeshQuicRealtimeSessionManager({
      lookupSource: new CultMeshStaticRealtimeLookupSource([verseWithRoute(target.verseId, signedRoute)]),
      trust,
      connectors: [replayConnector],
    });

    await assertRejectsAndDisposes(manager.connect(target), /did not prove|No realtime state path/i);
    assert.ok(connectedTransport, "a real transport must have connected");
    // Real cleanup ran: receiveFrame on the disposed transport rejects.
    await assert.rejects(connectedTransport!.receiveFrame(), /disposed/i);
  } finally {
    await listener.close();
  }
});

test("the advertised certificate pin is matched case-insensitively (M2)", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const listener = await RawQuicListener.open();
  try {
    const target: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:aetheria.daemon" };
    const candidate: CultMeshRealtimeCandidate = {
      endpoint: `cultmesh-state+quic://127.0.0.1:${listener.boundPort}?cert-sha256=${fixturePinHex().toUpperCase()}`,
      authorityRuntimeId: target.authorityRuntimeId,
      priority: 0,
      generation: "gen-1",
    };
    const connector = new CultMeshQuicRealtimeConnector();
    const transport = await connector.connect(candidate, target);
    transport.dispose();
  } finally {
    await listener.close();
  }
});

// ---------------------------------------------------------------------------
// Fix batch: pump/dispatch isolation (1), peer-shutdown cleanup (3), the
// bounded receive backlog and stream-kind pruning (6).
// ---------------------------------------------------------------------------

test("fix 1 / M4: a stream-kind/delivery mismatch faults only that connection; the pump keeps running", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const listener = await RawQuicListener.open();
  try {
    const target: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:aetheria.daemon" };
    const endpoint = (): string => `cultmesh-state+quic://127.0.0.1:${listener.boundPort}?cert-sha256=${fixturePinHex()}`;

    // A reliable stream carrying a frame whose own delivery byte says
    // latest-only: `onStreamFrame` throws, which must fault only this
    // connection.
    const connectorA = new CultMeshQuicRealtimeConnector();
    const acceptedA = listener.acceptOnce();
    const transportA = await connectorA.connect(
      { endpoint: endpoint(), authorityRuntimeId: target.authorityRuntimeId, priority: 0, generation: "gen-1" },
      target,
    );
    const connectionIdA = await acceptedA;
    listener.sendFrame(connectionIdA, testFrame({ delivery: "latest-only" }), CULTMESH_QUIC_STREAM_RELIABLE);
    await assert.rejects(transportA.receiveFrame(), /incompatible delivery semantics/i);
    // Future receives on the faulted transport reject immediately, with the
    // same error, rather than hanging.
    await assert.rejects(transportA.receiveFrame(), /incompatible delivery semantics/i);

    // A second, independent connection through the same process-wide runtime
    // and pump must still work: the fault above did not bring it down.
    const connectorB = new CultMeshQuicRealtimeConnector();
    const acceptedB = listener.acceptOnce();
    const transportB = await connectorB.connect(
      { endpoint: endpoint(), authorityRuntimeId: target.authorityRuntimeId, priority: 0, generation: "gen-1" },
      target,
    );
    try {
      const connectionIdB = await acceptedB;
      listener.sendFrame(connectionIdB, testFrame(), CULTMESH_QUIC_STREAM_LATEST_ONLY);
      const received = await transportB.receiveFrame();
      assert.equal(received.bodyId, "body:aetheria:entities");
    } finally {
      transportB.dispose();
    }
  } finally {
    await listener.close();
  }
});

test("M4b: a latest-only stream carrying a reliable-ordered frame faults only that connection", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const listener = await RawQuicListener.open();
  try {
    const target: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:aetheria.daemon" };
    const endpoint = (): string => `cultmesh-state+quic://127.0.0.1:${listener.boundPort}?cert-sha256=${fixturePinHex()}`;

    // The mirror image of the M4 test above: a latest-only stream carrying
    // a frame whose own delivery byte says reliable-ordered.
    const connectorA = new CultMeshQuicRealtimeConnector();
    const acceptedA = listener.acceptOnce();
    const transportA = await connectorA.connect(
      { endpoint: endpoint(), authorityRuntimeId: target.authorityRuntimeId, priority: 0, generation: "gen-1" },
      target,
    );
    const connectionIdA = await acceptedA;
    listener.sendFrame(connectionIdA, testFrame({ delivery: "reliable-ordered" }), CULTMESH_QUIC_STREAM_LATEST_ONLY);
    await assert.rejects(transportA.receiveFrame(), /incompatible delivery semantics/i);

    // A second, independent connection still works.
    const connectorB = new CultMeshQuicRealtimeConnector();
    const acceptedB = listener.acceptOnce();
    const transportB = await connectorB.connect(
      { endpoint: endpoint(), authorityRuntimeId: target.authorityRuntimeId, priority: 0, generation: "gen-1" },
      target,
    );
    try {
      const connectionIdB = await acceptedB;
      listener.sendFrame(connectionIdB, testFrame(), CULTMESH_QUIC_STREAM_LATEST_ONLY);
      const received = await transportB.receiveFrame();
      assert.equal(received.bodyId, "body:aetheria:entities");
    } finally {
      transportB.dispose();
    }
  } finally {
    await listener.close();
  }
});

test("fix 1: a throwing onDisposed handler cannot crash the pump when a malformed frame faults a connection", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const listener = await RawQuicListener.open();
  try {
    const target: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:aetheria.daemon" };
    const endpoint = (): string => `cultmesh-state+quic://127.0.0.1:${listener.boundPort}?cert-sha256=${fixturePinHex()}`;

    const connectorA = new CultMeshQuicRealtimeConnector();
    const acceptedA = listener.acceptOnce();
    const transportA = await connectorA.connect(
      { endpoint: endpoint(), authorityRuntimeId: target.authorityRuntimeId, priority: 0, generation: "gen-1" },
      target,
    );
    // A throwing onDisposed handler, registered before the fault: fix 1
    // must run it inside its own try/catch so it cannot escape cleanup(),
    // and must have already released the runtime reference regardless.
    let secondHandlerRan = false;
    transportA.onDisposed?.(() => {
      throw new Error("first onDisposed handler throws on purpose");
    });
    transportA.onDisposed?.(() => {
      secondHandlerRan = true;
    });

    const connectionIdA = await acceptedA;
    // Random bytes on a reliable stream: not a valid encoded frame, so
    // decodeRealtimeFrame throws inside onStreamFrame, which native's
    // dispatch loop (fix 1's native half) catches and faults only this
    // connection.
    listener.sendRaw(connectionIdA, new Uint8Array([9, 9, 9, 9, 9, 9, 9, 9]), CULTMESH_QUIC_STREAM_RELIABLE);
    await assert.rejects(transportA.receiveFrame());
    assert.equal(secondHandlerRan, true, "a throwing handler must not stop the next onDisposed handler from running");

    // The process (and this test file's pump) survives, and a second,
    // independent connection through the same runtime still works.
    const connectorB = new CultMeshQuicRealtimeConnector();
    const acceptedB = listener.acceptOnce();
    const transportB = await connectorB.connect(
      { endpoint: endpoint(), authorityRuntimeId: target.authorityRuntimeId, priority: 0, generation: "gen-1" },
      target,
    );
    try {
      const connectionIdB = await acceptedB;
      listener.sendFrame(connectionIdB, testFrame(), CULTMESH_QUIC_STREAM_LATEST_ONLY);
      const received = await transportB.receiveFrame();
      assert.equal(received.bodyId, "body:aetheria:entities");
    } finally {
      transportB.dispose();
    }
  } finally {
    await listener.close();
  }
});

test("fix 1: a throwing onDisposed handler on a peer shutdown still releases the runtime and runs every handler", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const listener = await RawQuicListener.open();
  try {
    const target: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:aetheria.daemon" };
    const candidate: CultMeshRealtimeCandidate = {
      endpoint: `cultmesh-state+quic://127.0.0.1:${listener.boundPort}?cert-sha256=${fixturePinHex()}`,
      authorityRuntimeId: target.authorityRuntimeId,
      priority: 0,
      generation: "gen-1",
    };
    const connector = new CultMeshQuicRealtimeConnector();
    const accepted = listener.acceptOnce();
    const transport = await connector.connect(candidate, target);
    const connectionId = await accepted;

    let secondHandlerRan = false;
    transport.onDisposed?.(() => {
      throw new Error("first onDisposed handler throws on purpose");
    });
    transport.onDisposed?.(() => {
      secondHandlerRan = true;
    });

    const before = CultMeshQuicNativeRuntime.refCount;
    listener.shutdownConnection(connectionId);
    // Wait for cleanup() to run (driven by the pump's CONNECTION_SHUTDOWN
    // dispatch), rather than asserting immediately.
    await assert.rejects(transport.receiveFrame());
    assert.equal(
      CultMeshQuicNativeRuntime.refCount,
      before - 1,
      "the runtime reference must be released even though a handler threw",
    );
    assert.equal(secondHandlerRan, true, "every onDisposed handler must still run despite an earlier one throwing");
  } finally {
    await listener.close();
  }
});

test("M10: an equal generation delivered after the first was already consumed is dropped, not redelivered", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const listener = await RawQuicListener.open();
  try {
    const target: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:aetheria.daemon" };
    const candidate: CultMeshRealtimeCandidate = {
      endpoint: `cultmesh-state+quic://127.0.0.1:${listener.boundPort}?cert-sha256=${fixturePinHex()}`,
      authorityRuntimeId: target.authorityRuntimeId,
      priority: 0,
      generation: "gen-1",
    };
    const connector = new CultMeshQuicRealtimeConnector();
    const accepted = listener.acceptOnce();
    const transport = await connector.connect(candidate, target);
    try {
      const connectionId = await accepted;

      // Send one frame and consume it, so the coalescer no longer holds
      // anything pending for this key: the generation filter's own record
      // (`latestGenerations`) is the only thing left that could still know
      // this generation was already seen. Sending a frame back-to-back with
      // an unconsumed duplicate would mask a broken filter behind the
      // coalescer overwriting the pending value with an identical one; this
      // sequencing is what M10 needs to be observable at all (Soul's note).
      listener.sendFrame(connectionId, testFrame({ producerEpoch: 1n, sequence: 3n }), CULTMESH_QUIC_STREAM_LATEST_ONLY);
      const first = await transport.receiveFrame();
      assert.equal(first.sequence, 3n);

      // The exact same generation, sent again after consumption: the
      // filter must drop it outright rather than deliver it as new.
      listener.sendFrame(connectionId, testFrame({ producerEpoch: 1n, sequence: 3n }), CULTMESH_QUIC_STREAM_LATEST_ONLY);
      await new Promise((resolve) => setTimeout(resolve, 200));

      const timeout = new AbortController();
      const timer = setTimeout(() => timeout.abort(), 300);
      await assert.rejects(
        transport.receiveFrame(timeout.signal),
        "an equal generation already consumed must not be redelivered",
      );
      clearTimeout(timer);
    } finally {
      transport.dispose();
    }
  } finally {
    await listener.close();
  }
});

// F7 ("a canceled send must not resolve as success") is covered in
// realtime-quic-native.test.ts, at the native binding layer, not here.
// A consumer-level scenario was attempted first: prime the reliable stream,
// start a second send, and shut the connection down from the peer mid-flight.
// It proved unreliable on this host regardless of payload size or added
// delay before the shutdown call — MsQuic here appears to accept and locally
// complete a `StreamSend` (marking it as a plain, non-canceled
// SEND_COMPLETE) faster than any JS-observable window can reliably land a
// shutdown ahead of it, and a delay long enough to guarantee the shutdown
// wins just lets the send finish first instead. This is recorded as not yet
// reached at the consumer layer, not as unreachable: the native-layer test
// asserts the exact field (`code`, decoded through the same struct decode
// F7 mutates) that this scenario would also exercise, so the fix itself is
// defended; only the end-to-end SEND_CANCELED proof through the consumer's
// own `sendFrame()` promise is the open gap.

test("fix 3: a peer-initiated shutdown rejects a pending receive and cleans up exactly once", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const listener = await RawQuicListener.open();
  try {
    const target: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:aetheria.daemon" };
    const candidate: CultMeshRealtimeCandidate = {
      endpoint: `cultmesh-state+quic://127.0.0.1:${listener.boundPort}?cert-sha256=${fixturePinHex()}`,
      authorityRuntimeId: target.authorityRuntimeId,
      priority: 0,
      generation: "gen-1",
    };
    const connector = new CultMeshQuicRealtimeConnector();
    const accepted = listener.acceptOnce();
    const transport = await connector.connect(candidate, target);
    const connectionId = await accepted;

    const pendingReceive = transport.receiveFrame();
    listener.shutdownConnection(connectionId);
    await assert.rejects(pendingReceive, /closed by the remote peer/i);

    // Cleanup already ran: a further explicit dispose is a harmless no-op,
    // and a fresh receive rejects immediately instead of hanging forever
    // (the leak fix 3 exists for).
    transport.dispose();
    await assert.rejects(transport.receiveFrame(), /closed by the remote peer/i);
  } finally {
    await listener.close();
  }
});

test("fix 6: a slow latest-only reader accumulates at most one pending frame per key, and an equal generation is dropped", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const listener = await RawQuicListener.open();
  try {
    const target: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:aetheria.daemon" };
    const candidate: CultMeshRealtimeCandidate = {
      endpoint: `cultmesh-state+quic://127.0.0.1:${listener.boundPort}?cert-sha256=${fixturePinHex()}`,
      authorityRuntimeId: target.authorityRuntimeId,
      priority: 0,
      generation: "gen-1",
    };
    const connector = new CultMeshQuicRealtimeConnector();
    const accepted = listener.acceptOnce();
    const transport = await connector.connect(candidate, target);
    try {
      const connectionId = await accepted;

      // Three increasing-sequence frames for the same (channel, body),
      // published while nothing reads: an unbounded queue would deliver all
      // three in order; the coalescing inbox keeps only the newest.
      listener.sendFrame(connectionId, testFrame({ producerEpoch: 1n, sequence: 1n }), CULTMESH_QUIC_STREAM_LATEST_ONLY);
      listener.sendFrame(connectionId, testFrame({ producerEpoch: 1n, sequence: 2n }), CULTMESH_QUIC_STREAM_LATEST_ONLY);
      listener.sendFrame(connectionId, testFrame({ producerEpoch: 1n, sequence: 3n }), CULTMESH_QUIC_STREAM_LATEST_ONLY);
      // An equal generation, sent last: it must be dropped outright, not
      // queued as a fourth pending frame.
      listener.sendFrame(connectionId, testFrame({ producerEpoch: 1n, sequence: 3n }), CULTMESH_QUIC_STREAM_LATEST_ONLY);

      // Give the pump time to process all four sends before reading.
      await new Promise((resolve) => setTimeout(resolve, 200));

      const first = await transport.receiveFrame();
      assert.equal(first.sequence, 3n, "only the newest pending frame for the key is kept");

      const timeout = new AbortController();
      const timer = setTimeout(() => timeout.abort(), 300);
      await assert.rejects(transport.receiveFrame(timeout.signal), "nothing else should be pending");
      clearTimeout(timer);
    } finally {
      transport.dispose();
    }
  } finally {
    await listener.close();
  }
});

test("fix 6: a stream shutdown prunes its entry so streamKinds does not grow without bound", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");
  const listener = await RawQuicListener.open();
  try {
    const target: CultMeshRealtimeTarget = { verseId: "aetheria", authorityRuntimeId: "service:aetheria.daemon" };
    const candidate: CultMeshRealtimeCandidate = {
      endpoint: `cultmesh-state+quic://127.0.0.1:${listener.boundPort}?cert-sha256=${fixturePinHex()}`,
      authorityRuntimeId: target.authorityRuntimeId,
      priority: 0,
      generation: "gen-1",
    };
    const connector = new CultMeshQuicRealtimeConnector();
    const accepted = listener.acceptOnce();
    const transport = await connector.connect(candidate, target);
    try {
      const connectionId = await accepted;
      listener.sendFrame(connectionId, testFrame(), CULTMESH_QUIC_STREAM_LATEST_ONLY);
      const received = await transport.receiveFrame();
      assert.equal(received.sequence, 1n);

      // The server-side stream the frame arrived on is a fresh latest-only
      // stream the listener opened and finished with `fin: true`; the native
      // bridge emits its own shutdown event once MsQuic completes teardown.
      // Wait for that STREAM_SHUTDOWN to actually land, then assert the
      // internal map was pruned, not just that a second frame still works
      // (F6kinds's removed prune line has no behavioural symptom until a
      // stream id is reused, which QUIC never does within one connection).
      await new Promise((resolve) => setTimeout(resolve, 300));
      assert.equal(
        (transport as CultMeshQuicRealtimeTransport).trackedStreamCount,
        0,
        "streamKinds must be pruned once the stream shuts down, not accumulate one entry per stream",
      );

      listener.sendFrame(connectionId, testFrame({ sequence: 2n }), CULTMESH_QUIC_STREAM_LATEST_ONLY);
      const second = await transport.receiveFrame();
      assert.equal(second.sequence, 2n);
    } finally {
      transport.dispose();
    }
  } finally {
    await listener.close();
  }
});
