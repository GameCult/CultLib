// The Cut 3 FFI smoke, now run against `CultMeshQuicNativeRuntime` instead of
// raw koffi calls: open a runtime, open a listener with a real credential,
// connect a real loopback connection, exchange one frame's worth of raw
// bytes, and close everything down. This proves the binding's declared ABI
// matches the shipped bridge; frame semantics (delivery, generation) are
// `realtime-quic-consumer.test.ts`'s job.
//
// Skips instead of failing when no native bridge is available: set
// CULTMESH_QUIC_NATIVE_DIR to a directory `scripts/build-quic-native.ps1` or
// `scripts/build-quic-native.sh` built, or run from the committed
// `packages/cultmesh-ts/native/<platform>` tree once a later cut lands it.

import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";

import {
  CultMeshQuicNativeRuntime,
  CULTMESH_QUIC_EVENT_LISTENER_NEW_CONNECTION,
  CULTMESH_QUIC_EVENT_CONNECTION_CERTIFICATE_RECEIVED,
  CULTMESH_QUIC_EVENT_CONNECTION_CONNECTED,
  CULTMESH_QUIC_EVENT_STREAM_STARTED,
  CULTMESH_QUIC_EVENT_STREAM_FRAME,
  CULTMESH_QUIC_EVENT_STREAM_SEND_COMPLETE,
  CULTMESH_QUIC_STREAM_RELIABLE,
} from "../src/realtime-quic-native";

const FIXTURE_P12 = join(__dirname, "fixtures", "quic-test.p12");

function nativeBridgeAvailable(): boolean {
  const dir = process.env.CULTMESH_QUIC_NATIVE_DIR;
  if (!dir) return false;
  try {
    const bridge = process.platform === "win32" ? "gamecult_mesh_quic_native.dll" : "libgamecult_mesh_quic_native.so";
    const dependency = process.platform === "win32" ? "msquic.dll" : "libmsquic.so.2";
    readFileSync(join(dir, bridge));
    readFileSync(join(dir, dependency));
    return true;
  } catch {
    return false;
  }
}

async function waitFor<T>(collect: (resolve: (value: T) => void) => void, timeoutMs = 5_000): Promise<T> {
  return await new Promise<T>((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("timed out waiting for a native QUIC event")), timeoutMs);
    collect((value) => {
      clearTimeout(timer);
      resolve(value);
    });
  });
}

test("CultMeshQuicNativeRuntime opens and closes with no listener or connection", async (t) => {
  if (!nativeBridgeAvailable()) {
    t.skip("CULTMESH_QUIC_NATIVE_DIR is not set to a built native bridge.");
    return;
  }
  const runtime = await CultMeshQuicNativeRuntime.open();
  await runtime.release();
});

test("CultMeshQuicNativeRuntime opens and closes a listener with a real PKCS12 credential", async (t) => {
  if (!nativeBridgeAvailable()) {
    t.skip("CULTMESH_QUIC_NATIVE_DIR is not set to a built native bridge.");
    return;
  }
  const runtime = await CultMeshQuicNativeRuntime.open();
  try {
    const pkcs12 = readFileSync(FIXTURE_P12);
    const { listenerId, boundPort } = runtime.listenerOpen("127.0.0.1", 0, pkcs12, "");
    assert.ok(boundPort > 0);
    runtime.listenerClose(listenerId);
  } finally {
    await runtime.release();
  }
});

test("a full loopback connection delivers one raw frame end to end", async (t) => {
  if (!nativeBridgeAvailable()) {
    t.skip("CULTMESH_QUIC_NATIVE_DIR is not set to a built native bridge.");
    return;
  }
  const runtime = await CultMeshQuicNativeRuntime.open();
  try {
    const pkcs12 = readFileSync(FIXTURE_P12);
    const { listenerId, boundPort } = runtime.listenerOpen("127.0.0.1", 0, pkcs12, "");

    const accepted = waitFor<bigint>((resolve) => {
      runtime.onListenerEvent(listenerId, (event) => {
        if (event.type === CULTMESH_QUIC_EVENT_LISTENER_NEW_CONNECTION) resolve(event.connectionId);
      });
    });

    const clientConnectionId = runtime.connectionOpen("127.0.0.1", boundPort);
    const clientConnected = waitFor<void>((resolve) => {
      runtime.onConnectionEvent(clientConnectionId, (event) => {
        if (event.type === CULTMESH_QUIC_EVENT_CONNECTION_CERTIFICATE_RECEIVED) {
          runtime.connectionCertificateComplete(clientConnectionId, true);
          return;
        }
        if (event.type === CULTMESH_QUIC_EVENT_CONNECTION_CONNECTED) resolve();
      });
    });

    const serverConnectionId = await accepted;
    const serverFrame = waitFor<Uint8Array>((resolve) => {
      runtime.onConnectionEvent(serverConnectionId, (event) => {
        if (event.type === CULTMESH_QUIC_EVENT_STREAM_STARTED) {
          assert.equal(event.streamKind, CULTMESH_QUIC_STREAM_RELIABLE);
          return;
        }
        if (event.type === CULTMESH_QUIC_EVENT_STREAM_FRAME) resolve(event.payload);
      });
    });

    await clientConnected;
    const streamId = runtime.streamOpen(clientConnectionId, CULTMESH_QUIC_STREAM_RELIABLE);
    const sendComplete = waitFor<void>((resolve) => {
      runtime.onConnectionEvent(clientConnectionId, (event) => {
        if (event.type === CULTMESH_QUIC_EVENT_STREAM_SEND_COMPLETE) resolve();
      });
    });
    runtime.streamSendFrame(streamId, new Uint8Array([1, 2, 3, 4]), true);
    await sendComplete;

    const payload = await serverFrame;
    assert.deepEqual(Array.from(payload), [1, 2, 3, 4]);

    runtime.connectionShutdown(clientConnectionId, 0n);
    runtime.connectionShutdown(serverConnectionId, 0n);
    runtime.listenerClose(listenerId);
  } finally {
    await runtime.release();
  }
});

test("negative grep: no koffi callbacks, .async only on nextEvent", () => {
  // __dirname at runtime is dist-test/test; the source lives two levels up,
  // in src/, since only .js output is mirrored under dist-test.
  const source = readFileSync(join(__dirname, "..", "..", "src", "realtime-quic-native.ts"), "utf8");
  const asyncUses = [...source.matchAll(/\.async\(/g)];
  assert.ok(asyncUses.length > 0, "expected at least one .async( use for nextEvent");
  for (const match of asyncUses) {
    const before = source.slice(Math.max(0, match.index! - 40), match.index!);
    assert.match(before, /nextEvent/, "the only .async( call site must be nextEvent");
  }
  assert.doesNotMatch(source, /koffi\.register/, "no koffi callbacks");
});
