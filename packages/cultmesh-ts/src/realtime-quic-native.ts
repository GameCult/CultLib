// The koffi binding for `native/GameCult.Mesh.Quic.Native`'s C ABI v2
// (`include/cultmesh_quic_native.h`). This file is the only place `koffi` is
// imported, and it is imported lazily, inside `loadBindings`, so a consumer
// that never touches QUIC (AetheriaEve Electron, Odin, Stonks, weksa) loads no
// native module at all.
//
// No koffi callbacks. `cultmesh_quic_next_event` is the single blocking
// export; `CultMeshQuicNativeRuntime` runs it on koffi's async thread pool in
// one loop per process-wide runtime and dispatches decoded events to
// registered listener/connection handlers by id. Every other export is a
// plain synchronous call.
//
// Binary resolution: `CULTMESH_QUIC_NATIVE_DIR` when set (the development
// loop and CI point it at `artifacts/quic-native/<platform>` straight from a
// build), otherwise `path.join(__dirname, "..", "native", "<platform>-<arch>")`
// (the committed tree a later cut lands; `__dirname` is `dist/` at runtime, so
// this path holds in the repo, an `npm pack` tarball and a pnpm `file:` copy
// alike). No `node_modules` lookup, no platform package, no registry.
//
// Two koffi quirks this file works around, both probed against the pinned
// 3.3.0 build: `_Out_` does not auto-allocate storage, so every out
// parameter is `koffi.alloc(type, 1)` written by the call and read back with
// `koffi.decode(buffer, type)`; and a decoded 64-bit value (`void *`,
// `uint64_t`) comes back as a plain `number` when it fits a safe integer,
// not a `bigint`. `toBigInt64` below is the one place that coerces either
// into the `bigint` every exported type in this module promises.

import { existsSync } from "node:fs";
import { join } from "node:path";

/** One decoded native event, payload already sliced to `payload_length`. */
export interface CultMeshQuicNativeEvent {
  readonly type: number;
  readonly streamKind: number;
  readonly listenerId: bigint;
  readonly connectionId: bigint;
  readonly streamId: bigint;
  readonly code: bigint;
  readonly status: number;
  readonly payload: Uint8Array;
}

export const CULTMESH_QUIC_EVENT_LISTENER_NEW_CONNECTION = 1;
export const CULTMESH_QUIC_EVENT_CONNECTION_CONNECTED = 2;
export const CULTMESH_QUIC_EVENT_CONNECTION_CERTIFICATE_RECEIVED = 3;
export const CULTMESH_QUIC_EVENT_CONNECTION_SHUTDOWN = 4;
export const CULTMESH_QUIC_EVENT_STREAM_STARTED = 5;
export const CULTMESH_QUIC_EVENT_STREAM_FRAME = 6;
export const CULTMESH_QUIC_EVENT_STREAM_SEND_COMPLETE = 7;
export const CULTMESH_QUIC_EVENT_STREAM_SHUTDOWN = 8;
export const CULTMESH_QUIC_EVENT_LISTENER_STOPPED = 9;

export const CULTMESH_QUIC_SEND_COMPLETED = 0;
export const CULTMESH_QUIC_SEND_CANCELED = 1;

export const CULTMESH_QUIC_STREAM_RELIABLE = 1;
export const CULTMESH_QUIC_STREAM_LATEST_ONLY = 2;

export const CULTMESH_QUIC_RESULT_BAD_CALL = -1;
export const CULTMESH_QUIC_RESULT_BAD_ARGUMENT = -2;

type NativeEventListener = (event: CultMeshQuicNativeEvent) => void;
/** Invoked once when a connection is faulted: a throwing listener, a malformed
 * frame, or any other error raised while dispatching an event for it. */
type NativeFaultHandler = (error: Error) => void;

/** koffi decodes a 64-bit value as `number` when it fits a safe integer and as
 * `bigint` otherwise (probed on the pinned 3.3.0 build); this is the single
 * place that coerces either into the `bigint` every exported type promises. */
function toBigInt64(value: number | bigint): bigint {
  return typeof value === "bigint" ? value : BigInt(value);
}

/** The opaque native runtime handle: a `void *`, decoded by koffi as a `bigint`. */
export type CultMeshQuicNativeHandle = bigint;

interface NativeEventStruct {
  type: number;
  stream_kind: number;
  listener_id: bigint;
  connection_id: bigint;
  stream_id: bigint;
  code: bigint;
  status: number;
  payload_length: number;
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type KoffiLib = any;

/** `nextEventAsync`'s signature, typed explicitly because `promisify` cannot
 * infer one from koffi's untyped (`any`) `.async` function. */
type NextEventAsyncFn = (
  runtime: CultMeshQuicNativeHandle,
  timeoutMs: number,
  outEvent: unknown,
  payload: Uint8Array | null,
  payloadCapacity: number,
  outRequired: unknown,
) => Promise<number>;

/**
 * The loaded native library: koffi itself, the declared struct, and the
 * twelve raw v2 exports. Untyped (koffi is `any`); the runtime class methods
 * below are the type surface for these calls, so wrapping them a second time
 * in a typed interface would only restate what the methods already say. The
 * type is inferred from `buildLibrary`'s return value instead of restated.
 */
// eslint-disable-next-line @typescript-eslint/no-use-before-define
type LoadedNativeLibrary = ReturnType<typeof buildLibrary>;

let libraryCache: LoadedNativeLibrary | undefined;

function platformArchDir(): string {
  return `${process.platform}-${process.arch}`;
}

function platformFileNames(): { bridge: string; dependency: string } {
  if (process.platform === "win32") {
    return { bridge: "gamecult_mesh_quic_native.dll", dependency: "msquic.dll" };
  }
  if (process.platform === "linux") {
    return { bridge: "libgamecult_mesh_quic_native.so", dependency: "libmsquic.so.2" };
  }
  throw new Error(`CultMesh QUIC native binding does not support platform '${process.platform}'.`);
}

function resolveNativeDir(): string {
  const configured = process.env.CULTMESH_QUIC_NATIVE_DIR;
  if (configured && configured.length > 0) return configured;
  return join(__dirname, "..", "native", platformArchDir());
}

/** Loads koffi, resolves the binary directory, and declares the ABI. */
function buildLibrary() {
  // Untyped on purpose: koffi's own type declarations are ESM-flavored and
  // fight a CommonJS `typeof import(...)` reference under Node16 resolution.
  // eslint-disable-next-line @typescript-eslint/no-var-requires, @typescript-eslint/no-explicit-any
  const koffi: any = require("koffi");
  const { bridge, dependency } = platformFileNames();
  const dir = resolveNativeDir();
  const bridgePath = join(dir, bridge);
  const dependencyPath = join(dir, dependency);
  if (!existsSync(bridgePath) || !existsSync(dependencyPath)) {
    const envDir = process.env.CULTMESH_QUIC_NATIVE_DIR;
    throw new Error(
      "CultMesh QUIC native binaries were not found. Tried " +
        `'${dir}' (${envDir ? "CULTMESH_QUIC_NATIVE_DIR" : "the package's committed native/ tree"}). ` +
        "Set CULTMESH_QUIC_NATIVE_DIR to a directory built by scripts/build-quic-native.ps1 or " +
        "scripts/build-quic-native.sh, or ship the committed native/ tree.",
    );
  }

  // The dependent library must be resolvable before the bridge loads.
  koffi.load(dependencyPath);
  const bridgeLib = koffi.load(bridgePath);

  const event = koffi.struct("cultmesh_quic_event", {
    type: "uint32_t",
    stream_kind: "uint32_t",
    listener_id: "uint64_t",
    connection_id: "uint64_t",
    stream_id: "uint64_t",
    code: "uint64_t",
    status: "int32_t",
    payload_length: "int32_t",
    reserved: koffi.array("uint8_t", 16),
  });

  const runtimeOpenFn = bridgeLib.func(
    "int32_t cultmesh_quic_runtime_open(str app_name, void **out_runtime)",
  );
  const runtimeCloseFn = bridgeLib.func("void cultmesh_quic_runtime_close(void *runtime)");
  const listenerOpenFn = bridgeLib.func(
    "int32_t cultmesh_quic_listener_open(void *runtime, str host, uint16_t port, " +
      "uint8_t *pkcs12, int32_t pkcs12_length, str password, " +
      "uint64_t *out_listener_id, uint16_t *out_bound_port)",
  );
  const listenerCloseFn = bridgeLib.func(
    "void cultmesh_quic_listener_close(void *runtime, uint64_t listener_id)",
  );
  const connectionOpenFn = bridgeLib.func(
    "int32_t cultmesh_quic_connection_open(void *runtime, str host, uint16_t port, " +
      "uint64_t *out_connection_id)",
  );
  const connectionCertificateCompleteFn = bridgeLib.func(
    "int32_t cultmesh_quic_connection_certificate_complete(void *runtime, uint64_t connection_id, int32_t accept)",
  );
  const connectionShutdownFn = bridgeLib.func(
    "void cultmesh_quic_connection_shutdown(void *runtime, uint64_t connection_id, uint64_t code)",
  );
  const streamOpenFn = bridgeLib.func(
    "int32_t cultmesh_quic_stream_open(void *runtime, uint64_t connection_id, uint8_t kind, " +
      "uint64_t *out_stream_id)",
  );
  const streamSendFrameFn = bridgeLib.func(
    "int32_t cultmesh_quic_stream_send_frame(void *runtime, uint64_t stream_id, " +
      "uint8_t *encoded_frame, int32_t length, int32_t fin)",
  );
  const streamShutdownFn = bridgeLib.func(
    "void cultmesh_quic_stream_shutdown(void *runtime, uint64_t stream_id, uint64_t code)",
  );
  const nextEventFn = bridgeLib.func(
    "int32_t cultmesh_quic_next_event(void *runtime, int32_t timeout_ms, " +
      `${event.name} *out_event, uint8_t *payload, int32_t payload_capacity, ` +
      "int32_t *out_required)",
  );
  const lastErrorFn = bridgeLib.func(
    "int32_t cultmesh_quic_last_error(void *runtime, uint8_t *destination, int32_t capacity)",
  );

  // eslint-disable-next-line @typescript-eslint/no-var-requires
  const { promisify } = require("node:util") as typeof import("node:util");
  const nextEventAsync = promisify(nextEventFn.async) as NextEventAsyncFn;

  return {
    koffi,
    event,
    runtimeOpenFn,
    runtimeCloseFn,
    listenerOpenFn,
    listenerCloseFn,
    connectionOpenFn,
    connectionCertificateCompleteFn,
    connectionShutdownFn,
    streamOpenFn,
    streamSendFrameFn,
    streamShutdownFn,
    nextEventAsync,
    lastErrorFn,
  };
}

/** Lazily builds and caches the loaded native library. */
function loadBindings(): LoadedNativeLibrary {
  if (libraryCache) return libraryCache;
  libraryCache = buildLibrary();
  return libraryCache;
}

function readLastError(lib: LoadedNativeLibrary, runtime: CultMeshQuicNativeHandle): string {
  const buffer = Buffer.alloc(1024);
  const written = lib.lastErrorFn(runtime, buffer, buffer.length);
  return written > 0 ? buffer.subarray(0, written).toString("utf8") : "CultMesh native QUIC call failed.";
}

/**
 * One MsQuic registration and one event queue, shared process-wide.
 * `open`/`release` are reference-counted: the underlying runtime opens on the
 * first `open` and closes on the last matching `release`. `open`/`release`
 * calls do not need to be paired 1:1 by the same caller as long as the counts
 * balance overall.
 */
export class CultMeshQuicNativeRuntime {
  private static shared: CultMeshQuicNativeRuntime | undefined;
  private static refCountValue = 0;

  /** Outstanding `open()` references not yet matched by `release()`. Test-visible: the UAF and eviction regression tests assert it returns to its baseline. */
  static get refCount(): number {
    return CultMeshQuicNativeRuntime.refCountValue;
  }

  private readonly handle: CultMeshQuicNativeHandle;
  private readonly lib: LoadedNativeLibrary;
  private readonly listenerListeners = new Map<bigint, NativeEventListener>();
  private readonly connectionListeners = new Map<
    bigint,
    { listener: NativeEventListener; onFault?: NativeFaultHandler }
  >();
  private closed = false;
  private pumpLoop: Promise<void>;

  /**
   * `nextEvent`'s out-parameter buffers, allocated once and reused for every
   * call instead of per call: the pump is single-threaded with at most one
   * `nextEvent` in flight (`nextEventAsync` is only ever awaited, never
   * fired concurrently), so decode-then-reuse is safe. Freed once in
   * `release()` after the pump loop has exited.
   */
  private readonly outEventBuffer: unknown;
  private readonly outRequiredBuffer: unknown;

  private constructor(handle: CultMeshQuicNativeHandle, lib: LoadedNativeLibrary) {
    this.handle = handle;
    this.lib = lib;
    this.outEventBuffer = lib.koffi.alloc(lib.event, 1);
    this.outRequiredBuffer = lib.koffi.alloc("int32_t", 1);
    this.pumpLoop = this.pump();
  }

  static async open(): Promise<CultMeshQuicNativeRuntime> {
    if (!CultMeshQuicNativeRuntime.shared) {
      const lib = loadBindings();
      const out = lib.koffi.alloc("void *", 1);
      const status = lib.runtimeOpenFn(null, out);
      if (status !== 0) {
        lib.koffi.free(out);
        throw new Error(`cultmesh_quic_runtime_open failed with status ${status}.`);
      }
      const handle = toBigInt64(lib.koffi.decode(out, "void *"));
      lib.koffi.free(out);
      CultMeshQuicNativeRuntime.shared = new CultMeshQuicNativeRuntime(handle, lib);
    }
    CultMeshQuicNativeRuntime.refCountValue += 1;
    return CultMeshQuicNativeRuntime.shared;
  }

  /**
   * Drops one reference; closes the shared runtime once none remain. Refuses
   * a release when no reference is outstanding. The bridge header (section 4)
   * forbids any host call beginning once `cultmesh_quic_runtime_close` has
   * started, so this marks the runtime closed and waits for the pump to exit
   * (it wakes within its 250 ms `nextEvent` timeout) before making that call:
   * a queued, not-yet-started `nextEvent` call must never begin after close
   * and run against freed memory.
   */
  async release(): Promise<void> {
    if (CultMeshQuicNativeRuntime.refCountValue === 0) {
      throw new Error("CultMeshQuicNativeRuntime.release called with no outstanding reference.");
    }
    CultMeshQuicNativeRuntime.refCountValue -= 1;
    if (CultMeshQuicNativeRuntime.refCountValue > 0) return;
    this.closed = true;
    CultMeshQuicNativeRuntime.shared = undefined;
    await this.pumpLoop;
    this.lib.runtimeCloseFn(this.handle);
    this.lib.koffi.free(this.outEventBuffer);
    this.lib.koffi.free(this.outRequiredBuffer);
  }

  onListenerEvent(listenerId: bigint, listener: NativeEventListener): void {
    this.listenerListeners.set(listenerId, listener);
  }

  offListenerEvent(listenerId: bigint): void {
    this.listenerListeners.delete(listenerId);
  }

  /**
   * Registers the event listener for one connection. `onFault`, if given, is
   * invoked exactly once if `listener` (or anything it calls synchronously,
   * such as a caller-supplied certificate validator) throws while dispatching
   * an event for this connection: the pump isolates the fault to this
   * connection, shuts it down at the native level, removes the listener, and
   * keeps running for every other connection.
   */
  onConnectionEvent(connectionId: bigint, listener: NativeEventListener, onFault?: NativeFaultHandler): void {
    this.connectionListeners.set(connectionId, { listener, onFault });
  }

  offConnectionEvent(connectionId: bigint): void {
    this.connectionListeners.delete(connectionId);
  }

  listenerOpen(
    host: string | null,
    port: number,
    pkcs12: Uint8Array,
    password: string | null,
  ): { listenerId: bigint; boundPort: number } {
    const outListenerId = this.lib.koffi.alloc("uint64_t", 1);
    const outBoundPort = this.lib.koffi.alloc("uint16_t", 1);
    try {
      const status = this.lib.listenerOpenFn(
        this.handle,
        host,
        port,
        pkcs12,
        pkcs12.length,
        password,
        outListenerId,
        outBoundPort,
      );
      if (status !== 0) {
        throw new Error(`cultmesh_quic_listener_open failed: ${readLastError(this.lib, this.handle)}`);
      }
      return {
        listenerId: toBigInt64(this.lib.koffi.decode(outListenerId, "uint64_t")),
        boundPort: this.lib.koffi.decode(outBoundPort, "uint16_t") as number,
      };
    } finally {
      this.lib.koffi.free(outListenerId);
      this.lib.koffi.free(outBoundPort);
    }
  }

  listenerClose(listenerId: bigint): void {
    this.lib.listenerCloseFn(this.handle, listenerId);
    this.offListenerEvent(listenerId);
  }

  connectionOpen(host: string, port: number): bigint {
    const out = this.lib.koffi.alloc("uint64_t", 1);
    try {
      const status = this.lib.connectionOpenFn(this.handle, host, port, out);
      if (status !== 0) {
        throw new Error(`cultmesh_quic_connection_open failed: ${readLastError(this.lib, this.handle)}`);
      }
      return toBigInt64(this.lib.koffi.decode(out, "uint64_t"));
    } finally {
      this.lib.koffi.free(out);
    }
  }

  connectionCertificateComplete(connectionId: bigint, accept: boolean): void {
    const status = this.lib.connectionCertificateCompleteFn(this.handle, connectionId, accept ? 1 : 0);
    if (status !== 0) {
      throw new Error(
        `cultmesh_quic_connection_certificate_complete failed: ${readLastError(this.lib, this.handle)}`,
      );
    }
  }

  connectionShutdown(connectionId: bigint, code: bigint): void {
    this.lib.connectionShutdownFn(this.handle, connectionId, code);
  }

  streamOpen(connectionId: bigint, kind: number): bigint {
    const out = this.lib.koffi.alloc("uint64_t", 1);
    try {
      const status = this.lib.streamOpenFn(this.handle, connectionId, kind, out);
      if (status !== 0) {
        throw new Error(`cultmesh_quic_stream_open failed: ${readLastError(this.lib, this.handle)}`);
      }
      return toBigInt64(this.lib.koffi.decode(out, "uint64_t"));
    } finally {
      this.lib.koffi.free(out);
    }
  }

  streamSendFrame(streamId: bigint, encodedFrame: Uint8Array, fin: boolean): void {
    const status = this.lib.streamSendFrameFn(this.handle, streamId, encodedFrame, encodedFrame.length, fin ? 1 : 0);
    if (status !== 0) {
      throw new Error(`cultmesh_quic_stream_send_frame failed: ${readLastError(this.lib, this.handle)}`);
    }
  }

  streamShutdown(streamId: bigint, code: bigint): void {
    this.lib.streamShutdownFn(this.handle, streamId, code);
  }

  /**
   * Isolates the faulting connection so one bad event never brings down the
   * pump: a malformed frame, a stream-kind/delivery mismatch, or a throwing
   * caller-supplied listener (certificate validator, waiter) faults only its
   * own connection. The listener is removed, the connection is shut down at
   * the native level, and `onFault` (if registered) is told, so pending and
   * future work on that connection can reject with the error. Every other
   * connection, and the pump itself, keep running.
   */
  private dispatch(event: NativeEventStruct, payload: Uint8Array): void {
    const decoded: CultMeshQuicNativeEvent = {
      type: event.type,
      streamKind: event.stream_kind,
      listenerId: event.listener_id,
      connectionId: event.connection_id,
      streamId: event.stream_id,
      code: event.code,
      status: event.status,
      payload,
    };
    if (decoded.type === CULTMESH_QUIC_EVENT_LISTENER_NEW_CONNECTION || decoded.type === CULTMESH_QUIC_EVENT_LISTENER_STOPPED) {
      try {
        this.listenerListeners.get(decoded.listenerId)?.(decoded);
      } catch (error) {
        // No fault contract for listener-level events today; do not let a
        // throwing handler crash the pump.
        this.reportUnhandledDispatchError(error);
      }
      return;
    }
    const entry = this.connectionListeners.get(decoded.connectionId);
    if (!entry) return;
    try {
      entry.listener(decoded);
    } catch (error) {
      this.faultConnection(decoded.connectionId, error instanceof Error ? error : new Error(String(error)), entry.onFault);
    }
  }

  private faultConnection(connectionId: bigint, error: Error, onFault: NativeFaultHandler | undefined): void {
    this.connectionListeners.delete(connectionId);
    try {
      this.lib.connectionShutdownFn(this.handle, connectionId, 0n);
    } catch {
      // Best-effort: the connection may already be gone.
    }
    if (onFault) {
      try {
        onFault(error);
      } catch (handlerError) {
        // The fault handler itself is caller-supplied code; a throw here
        // must not escape into the pump any more than the original fault did.
        this.reportUnhandledDispatchError(handlerError);
      }
    } else {
      this.reportUnhandledDispatchError(error);
    }
  }

  private reportUnhandledDispatchError(error: unknown): void {
    // eslint-disable-next-line no-console
    console.error("CultMesh QUIC native event dispatch failed with no fault handler registered:", error);
  }

  private async nextEvent(
    timeoutMs: number,
    payload: Uint8Array | null,
    payloadCapacity: number,
  ): Promise<{ status: number; event: NativeEventStruct | null; required: number }> {
    // Reuses the buffers allocated once in the constructor instead of
    // allocating fresh ones per call: the pump awaits each `nextEventAsync`
    // before issuing the next one, so at most one call is ever in flight
    // against them, and both are fully decoded here before the buffers can
    // be reused by the pump's next call.
    const status = await this.lib.nextEventAsync(
      this.handle,
      timeoutMs,
      this.outEventBuffer,
      payload,
      payloadCapacity,
      this.outRequiredBuffer,
    );
    const decoded =
      status === 1 ? (this.lib.koffi.decode(this.outEventBuffer, this.lib.event) as NativeEventStruct) : null;
    return {
      status,
      event: decoded && {
        ...decoded,
        listener_id: toBigInt64(decoded.listener_id),
        connection_id: toBigInt64(decoded.connection_id),
        stream_id: toBigInt64(decoded.stream_id),
        code: toBigInt64(decoded.code),
      },
      required: this.lib.koffi.decode(this.outRequiredBuffer, "int32_t") as number,
    };
  }

  private async pump(): Promise<void> {
    while (!this.closed) {
      let first: Awaited<ReturnType<CultMeshQuicNativeRuntime["nextEvent"]>>;
      try {
        first = await this.nextEvent(250, null, 0);
      } catch {
        if (this.closed) return;
        continue;
      }
      if (this.closed) return;
      if (first.status === 0 || first.status === CULTMESH_QUIC_RESULT_BAD_CALL) continue;
      if (first.status === 1 && first.event) {
        this.dispatch(first.event, new Uint8Array(0));
        continue;
      }
      if (first.status === 2 && first.required > 0) {
        const buffer = Buffer.alloc(first.required);
        let second: Awaited<ReturnType<CultMeshQuicNativeRuntime["nextEvent"]>>;
        try {
          second = await this.nextEvent(0, buffer, buffer.length);
        } catch {
          if (this.closed) return;
          continue;
        }
        if (second.status === 1 && second.event) {
          this.dispatch(second.event, buffer.subarray(0, second.event.payload_length));
        }
        continue;
      }
    }
  }
}
