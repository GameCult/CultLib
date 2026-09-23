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
  CULTMESH_QUIC_EVENT_CONNECTION_SHUTDOWN,
  CULTMESH_QUIC_EVENT_STREAM_STARTED,
  CULTMESH_QUIC_EVENT_STREAM_FRAME,
  CULTMESH_QUIC_EVENT_STREAM_SEND_COMPLETE,
  CULTMESH_QUIC_STREAM_RELIABLE,
} from "../src/realtime-quic-native";
import { nativeBridgeAvailable } from "./support/native-bridge";

// __dirname at runtime is dist-test/test; fixtures are binary and are never
// compiled/copied there, so they are read from their source location, two
// levels up.
const FIXTURE_P12 = join(__dirname, "..", "..", "test", "fixtures", "quic-test.p12");

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

test("F7: a 64-bit event code round-trips exactly through the struct decode", async (t) => {
  if (!nativeBridgeAvailable()) {
    t.skip("CULTMESH_QUIC_NATIVE_DIR is not set to a built native bridge.");
    return;
  }
  const runtime = await CultMeshQuicNativeRuntime.open();
  try {
    const pkcs12 = readFileSync(FIXTURE_P12);
    const { listenerId, boundPort } = runtime.listenerOpen("127.0.0.1", 0, pkcs12, "");

    // `code` (a `uint64_t`, the same field `CULTMESH_QUIC_SEND_CANCELED` is
    // compared against on the stream-send path) is exercised here through
    // CONNECTION_SHUTDOWN instead, whose code is an application-chosen value
    // this test fully controls: two values above 2^53 (where koffi's decode
    // stops being a plain safe-integer `number`) prove the whole
    // encode-native-decode-coerce path is exact, deterministically, with no
    // dependency on MsQuic's own send-completion timing.
    for (const code of [(1n << 53n) + 1n, (1n << 62n) - 1n]) {
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
      await clientConnected;

      const seenOnClient = waitFor<bigint>((resolve) => {
        runtime.onConnectionEvent(clientConnectionId, (event) => {
          if (event.type === CULTMESH_QUIC_EVENT_CONNECTION_SHUTDOWN) resolve(event.code);
        });
      });
      runtime.connectionShutdown(serverConnectionId, code);
      const seen = await seenOnClient;
      assert.equal(typeof seen, "bigint", "a decoded event code must always be bigint, never number");
      assert.equal(seen, code, `shutdown code ${code} must round-trip exactly through the struct decode`);
    }

    runtime.listenerClose(listenerId);
  } finally {
    await runtime.release();
  }
});

test(
  "P10b: a throwing onFault handler is isolated — the pump survives and other connections' handlers still run",
  { timeout: 10_000 },
  async (t) => {
    if (!nativeBridgeAvailable()) {
      t.skip("CULTMESH_QUIC_NATIVE_DIR is not set to a built native bridge.");
      return;
    }
    const runtime = await CultMeshQuicNativeRuntime.open();
    try {
      const pkcs12 = readFileSync(FIXTURE_P12);
      const { listenerId, boundPort } = runtime.listenerOpen("127.0.0.1", 0, pkcs12, "");

      // Connection A: both its listener and its onFault handler throw.
      // Without `faultConnection`'s own try/catch isolating a throwing
      // `onFault` (P10b's target), that throw would escape the pump's
      // dispatch loop and take every other connection down with it instead
      // of staying scoped to connection A.
      //
      // The server-side connection listener is registered synchronously,
      // inside the LISTENER_NEW_CONNECTION callback itself, rather than
      // after an `await`: the native bridge can dispatch a connection's next
      // event (CERTIFICATE_RECEIVED, or CONNECTED) before a later `await`'s
      // continuation ever runs, and an event with no listener registered for
      // its connection id is silently dropped (`dispatch`'s own
      // `if (!entry) return`) — the same async-registration gap fix 1 closed
      // for the provider's own accept path.
      const serverAFaulted = waitFor<void>((resolve) => {
        runtime.onListenerEvent(listenerId, (event) => {
          if (event.type !== CULTMESH_QUIC_EVENT_LISTENER_NEW_CONNECTION) return;
          runtime.onConnectionEvent(
            event.connectionId,
            () => {
              throw new Error("P10b: connection A's listener always throws.");
            },
            () => {
              resolve();
              throw new Error("P10b: connection A's onFault also throws.");
            },
          );
        });
      });
      const clientA = runtime.connectionOpen("127.0.0.1", boundPort);
      runtime.onConnectionEvent(clientA, (event) => {
        if (event.type === CULTMESH_QUIC_EVENT_CONNECTION_CERTIFICATE_RECEIVED) {
          runtime.connectionCertificateComplete(clientA, true);
        }
      });
      await serverAFaulted;

      // Connection B, opened only after A faulted: if the throw above ever
      // escaped the pump, this connection would never reach CONNECTED at all.
      let serverBConnected = false;
      const serverBAccepted = waitFor<void>((resolve) => {
        runtime.onListenerEvent(listenerId, (event) => {
          if (event.type !== CULTMESH_QUIC_EVENT_LISTENER_NEW_CONNECTION) return;
          runtime.onConnectionEvent(event.connectionId, (connectionEvent) => {
            if (connectionEvent.type === CULTMESH_QUIC_EVENT_CONNECTION_CONNECTED) serverBConnected = true;
          });
          resolve();
        });
      });
      const clientB = runtime.connectionOpen("127.0.0.1", boundPort);
      const clientBConnected = waitFor<void>((resolve) => {
        runtime.onConnectionEvent(clientB, (event) => {
          if (event.type === CULTMESH_QUIC_EVENT_CONNECTION_CERTIFICATE_RECEIVED) {
            runtime.connectionCertificateComplete(clientB, true);
            return;
          }
          if (event.type === CULTMESH_QUIC_EVENT_CONNECTION_CONNECTED) resolve();
        });
      });
      await serverBAccepted;
      await clientBConnected;
      assert.ok(serverBConnected, "connection B's handler must still run after A's onFault threw");

      runtime.connectionShutdown(clientA, 0n);
      runtime.connectionShutdown(clientB, 0n);
      runtime.listenerClose(listenerId);
    } finally {
      await runtime.release();
    }
  },
);

test("fix 2: release() refuses a call with no outstanding reference", async (t) => {
  if (!nativeBridgeAvailable()) {
    t.skip("CULTMESH_QUIC_NATIVE_DIR is not set to a built native bridge.");
    return;
  }
  const runtime = await CultMeshQuicNativeRuntime.open();
  await runtime.release();
  await assert.rejects(runtime.release(), /no outstanding reference/i);
});

test("negative grep: no koffi callbacks, .async only on nextEvent", () => {
  // __dirname at runtime is dist-test/test; the source lives two levels up,
  // in src/, since only .js output is mirrored under dist-test.
  const source = readFileSync(join(__dirname, "..", "..", "src", "realtime-quic-native.ts"), "utf8");
  // `.async` is referenced (not necessarily called with a literal "(" at the
  // reference site: it is handed to `promisify`, which calls it) only for
  // `nextEvent`, the ABI's single blocking export.
  const asyncUses = [...source.matchAll(/\.async\b/g)];
  assert.ok(asyncUses.length > 0, "expected at least one .async use for nextEvent");
  for (const match of asyncUses) {
    const before = source.slice(Math.max(0, match.index! - 40), match.index!);
    assert.match(before, /nextEvent/i, "the only .async reference must be nextEvent");
  }
  assert.doesNotMatch(source, /koffi\.register/, "no koffi callbacks");
});
