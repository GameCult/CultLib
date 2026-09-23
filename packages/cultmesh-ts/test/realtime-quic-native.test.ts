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
import { spawnSync } from "node:child_process";
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

      // Connection B: a normal, fully connected loopback pair, established
      // before A ever faults — so proving B still works afterward exercises
      // an already-running connection's handler, not a second accept cycle
      // racing whatever state A's fault left behind.
      const acceptedB = waitFor<bigint>((resolve) => {
        runtime.onListenerEvent(listenerId, (event) => {
          if (event.type === CULTMESH_QUIC_EVENT_LISTENER_NEW_CONNECTION) resolve(event.connectionId);
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
      const serverBId = await acceptedB;
      const serverBFrame = waitFor<Uint8Array>((resolve) => {
        runtime.onConnectionEvent(serverBId, (event) => {
          if (event.type === CULTMESH_QUIC_EVENT_STREAM_FRAME) resolve(event.payload);
        });
      });
      await clientBConnected;

      // Connection A, opened only after B is fully connected: both its
      // listener and its onFault handler throw on the first event delivered
      // to it. Without `faultConnection`'s own try/catch isolating a
      // throwing `onFault` (P10b's target), that throw would escape the
      // pump's dispatch loop and take every other connection — including
      // the already-live B — down with it, instead of staying scoped to A.
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

      // Prove B's handler is still live after A's onFault threw: send a
      // frame on B and confirm the server side still receives it.
      const streamId = runtime.streamOpen(clientB, CULTMESH_QUIC_STREAM_RELIABLE);
      runtime.streamSendFrame(streamId, new Uint8Array([9, 9, 9]), true);
      const payload = await serverBFrame;
      assert.deepEqual(
        Array.from(payload),
        [9, 9, 9],
        "connection B must still deliver frames after A's onFault threw",
      );

      runtime.connectionShutdown(clientA, 0n);
      runtime.connectionShutdown(clientB, 0n);
      runtime.connectionShutdown(serverBId, 0n);
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

// The measurement below runs in its own `node --expose-gc` child process
// instead of in this test's own process: by the time this test would run,
// ~150 earlier tests in the suite have already grown and partly freed the
// glibc heap, so a real per-call native leak can land in already-mapped free
// space and never show up as RSS growth in *this* process (confirmed by hand:
// the in-process version of this test passed even with `nextEvent` mutated
// back to allocating fresh koffi buffers every call). A freshly spawned
// process has no such history, matching the ~8 MiB (fixed) vs ~30 MiB
// (pre-fix) split measured in isolation.
const MEMORY_PROBE_SCRIPT = `
const { CultMeshQuicNativeRuntime } = require(process.argv[1]);
(async () => {
  const runtime = await CultMeshQuicNativeRuntime.open();
  const nextEvent = runtime.nextEvent.bind(runtime);
  try {
    for (let i = 0; i < 2000; i += 1) await nextEvent(0, null, 0);
    global.gc();
    const before = process.memoryUsage().rss;
    const ITERATIONS = 200000;
    for (let i = 0; i < ITERATIONS; i += 1) await nextEvent(0, null, 0);
    global.gc();
    const after = process.memoryUsage().rss;
    process.stdout.write(JSON.stringify({ before, after, growthBytes: after - before, ITERATIONS }));
  } finally {
    await runtime.release();
  }
})().catch((error) => {
  console.error(error);
  process.exit(1);
});
`;

test(
  "memory: sustained nextEvent calls do not leak koffi-allocated out-parameter buffers",
  { timeout: 60_000 },
  (t) => {
    if (!nativeBridgeAvailable()) {
      t.skip("CULTMESH_QUIC_NATIVE_DIR is not set to a built native bridge.");
      return;
    }

    // `nextEvent` is private; the compiled module path is resolved the same
    // way this file's own `import` resolves it, and handed to the child by
    // argv so the child needs no path logic of its own.
    const modulePath = require.resolve("../src/realtime-quic-native");
    const result = spawnSync(process.execPath, ["--expose-gc", "-e", MEMORY_PROBE_SCRIPT, modulePath], {
      encoding: "utf8",
      env: process.env,
    });
    if (result.status !== 0 || typeof result.stdout !== "string" || result.stdout.trim().length === 0) {
      if (/--expose-gc/.test(result.stderr ?? "")) {
        t.skip("this Node build does not support --expose-gc.");
        return;
      }
      assert.fail(
        `memory probe child process failed (status ${String(result.status)}):\n${result.stderr ?? "(no stderr)"}`,
      );
    }

    const { before, after, growthBytes, ITERATIONS } = JSON.parse(result.stdout.trim()) as {
      before: number;
      after: number;
      growthBytes: number;
      ITERATIONS: number;
    };
    // Measured on Yggdrasil, same build and host, in a dedicated process:
    // fixed code (0 `koffi.alloc` calls in the loop): ~8.0 MiB RSS growth.
    // pre-fix code (400k `koffi.alloc` calls, never freed): ~29.9 MiB.
    // 18 MiB sits well above the fixed baseline and well below the leaking
    // one, so the bound stays robust to ordinary run-to-run noise while
    // still catching the regression.
    const BOUND_BYTES = 18 * 1024 * 1024;
    assert.ok(
      growthBytes < BOUND_BYTES,
      `RSS grew by ${growthBytes} bytes (${(growthBytes / 1024 / 1024).toFixed(2)} MiB, from ${before} to ${after}) ` +
        `over ${ITERATIONS} nextEvent calls; expected it to stay under ${BOUND_BYTES / 1024 / 1024} MiB once ` +
        "out-parameter buffers are reused instead of allocated per call.",
    );
  },
);
