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
} from "../src/realtime-quic-native";
import {
  CultMeshQuicRealtimeConnector,
  CultMeshQuicRealtimeSessionManager,
  CultMeshStaticRealtimeLookupSource,
  CULTMESH_REALTIME_STATE_PROTOCOL_ID,
  type CultMeshRealtimeCandidate,
  type CultMeshRealtimeTarget,
  type CultMeshRealtimeTransport,
  type CultMeshRealtimeTransportConnector,
} from "../src/realtime-quic";
import { encodeRealtimeFrame, type CultMeshRealtimeFrame } from "../src/realtime-wire";

// __dirname at runtime is dist-test/test; fixtures are binary and are never
// compiled/copied there, so they are read from their source location, two
// levels up.
const FIXTURE_P12 = join(__dirname, "..", "..", "test", "fixtures", "quic-test.p12");
const FIXTURE_DER = join(__dirname, "..", "..", "test", "fixtures", "quic-test-cert.der");

function nativeBridgeAvailable(): boolean {
  const dir = process.env.CULTMESH_QUIC_NATIVE_DIR;
  if (!dir) return false;
  try {
    const bridge = process.platform === "win32" ? "gamecult_mesh_quic_native.dll" : "libgamecult_mesh_quic_native.so";
    readFileSync(join(dir, bridge));
    return true;
  } catch {
    return false;
  }
}

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

  async close(): Promise<void> {
    this.runtime.listenerClose(this.listenerId);
    await this.runtime.release();
  }
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
    await assert.rejects(connector.connect(candidate, target), /certificate|rejected/i);
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
    await assert.rejects(connector.connect(candidate, target), /certificate|rejected/i);
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
  await assert.rejects(manager.connect(target), /valid|expired/i);
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
  await assert.rejects(manager.connect(target), /Odin-signed/i);
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
  await assert.rejects(manager.connect(target), /not trusted/i);
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
    await assert.rejects(manager.connect(target), /certificate|rejected/i);
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

  await assert.rejects(manager.connect(target), /did not prove|No realtime state path/i);
  assert.equal(disposed, true, "the session manager must dispose a transport that fails isVerifiedFor");
});
