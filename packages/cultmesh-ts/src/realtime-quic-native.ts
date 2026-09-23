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
// koffi's out-parameter marker (`_Out_`) does not auto-allocate storage or
// reshape the return value the way its name suggests: probed on the pinned
// 3.3.0 build (`node -e`, loading the real bridge), a function declared with
// `_Out_ void **out_runtime` still demands a real buffer at that call-site
// position (a bare `null` is read by the bridge as a null out parameter and
// refused with -1) and its return value is the plain scalar `int32_t`, not an
// object. Every out parameter here is therefore `koffi.alloc(type, 1)`,
// decoded with `koffi.decode(buffer, type)` after the call. A `void *` value
// — the runtime handle, and every listener/connection/stream id, since ids
// share the 64-bit width — decodes as a JavaScript `bigint`.

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

interface NativeBindings {
  runtimeOpen(appName: string | null): { status: number; runtime: CultMeshQuicNativeHandle | null };
  runtimeClose(runtime: CultMeshQuicNativeHandle): void;
  listenerOpen(
    runtime: CultMeshQuicNativeHandle,
    host: string | null,
    port: number,
    pkcs12: Uint8Array,
    pkcs12Length: number,
    password: string | null,
  ): { status: number; listenerId: bigint; boundPort: number };
  listenerClose(runtime: CultMeshQuicNativeHandle, listenerId: bigint): void;
  connectionOpen(
    runtime: CultMeshQuicNativeHandle,
    host: string,
    port: number,
  ): { status: number; connectionId: bigint };
  connectionCertificateComplete(runtime: CultMeshQuicNativeHandle, connectionId: bigint, accept: number): number;
  connectionShutdown(runtime: CultMeshQuicNativeHandle, connectionId: bigint, code: bigint): void;
  streamOpen(
    runtime: CultMeshQuicNativeHandle,
    connectionId: bigint,
    kind: number,
  ): { status: number; streamId: bigint };
  streamSendFrame(
    runtime: CultMeshQuicNativeHandle,
    streamId: bigint,
    encodedFrame: Uint8Array,
    length: number,
    fin: number,
  ): number;
  streamShutdown(runtime: CultMeshQuicNativeHandle, streamId: bigint, code: bigint): void;
  nextEventAsync(
    runtime: CultMeshQuicNativeHandle,
    timeoutMs: number,
    payload: Uint8Array | null,
    payloadCapacity: number,
  ): Promise<{ status: number; event: NativeEventStruct | null; required: number }>;
  lastError(runtime: CultMeshQuicNativeHandle, destination: Uint8Array, capacity: number): number;
  lastStatus(runtime: CultMeshQuicNativeHandle): number;
}

let bindingsCache: NativeBindings | undefined;

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

/** Lazily loads koffi, resolves the binary directory, and declares the ABI. */
function loadBindings(): NativeBindings {
  if (bindingsCache) return bindingsCache;

  // Untyped on purpose: koffi's own type declarations are ESM-flavored and
  // fight a CommonJS `typeof import(...)` reference under Node16 resolution.
  // eslint-disable-next-line @typescript-eslint/no-var-requires
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
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

  const CultMeshQuicEvent = koffi.struct("cultmesh_quic_event", {
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
      `${CultMeshQuicEvent.name} *out_event, uint8_t *payload, int32_t payload_capacity, ` +
      "int32_t *out_required)",
  );
  const lastErrorFn = bridgeLib.func(
    "int32_t cultmesh_quic_last_error(void *runtime, uint8_t *destination, int32_t capacity)",
  );
  const lastStatusFn = bridgeLib.func("int32_t cultmesh_quic_last_status(void *runtime)");

  // eslint-disable-next-line @typescript-eslint/no-var-requires
  const { promisify } = require("node:util") as typeof import("node:util");
  const nextEventAsyncRaw = promisify(nextEventFn.async) as (
    runtime: CultMeshQuicNativeHandle,
    timeoutMs: number,
    outEvent: unknown,
    payload: Uint8Array | null,
    payloadCapacity: number,
    outRequired: unknown,
  ) => Promise<number>;

  bindingsCache = {
    runtimeOpen: (appName) => {
      const out = koffi.alloc("void *", 1);
      const status = runtimeOpenFn(appName, out);
      return { status, runtime: status === 0 ? (koffi.decode(out, "void *") as CultMeshQuicNativeHandle) : null };
    },
    runtimeClose: (runtime) => runtimeCloseFn(runtime),
    listenerOpen: (runtime, host, port, pkcs12, pkcs12Length, password) => {
      const outListenerId = koffi.alloc("uint64_t", 1);
      const outBoundPort = koffi.alloc("uint16_t", 1);
      const status = listenerOpenFn(runtime, host, port, pkcs12, pkcs12Length, password, outListenerId, outBoundPort);
      return {
        status,
        listenerId: status === 0 ? (koffi.decode(outListenerId, "uint64_t") as bigint) : 0n,
        boundPort: status === 0 ? (koffi.decode(outBoundPort, "uint16_t") as number) : 0,
      };
    },
    listenerClose: (runtime, listenerId) => listenerCloseFn(runtime, listenerId),
    connectionOpen: (runtime, host, port) => {
      const out = koffi.alloc("uint64_t", 1);
      const status = connectionOpenFn(runtime, host, port, out);
      return { status, connectionId: status === 0 ? (koffi.decode(out, "uint64_t") as bigint) : 0n };
    },
    connectionCertificateComplete: (runtime, connectionId, accept) =>
      connectionCertificateCompleteFn(runtime, connectionId, accept),
    connectionShutdown: (runtime, connectionId, code) => connectionShutdownFn(runtime, connectionId, code),
    streamOpen: (runtime, connectionId, kind) => {
      const out = koffi.alloc("uint64_t", 1);
      const status = streamOpenFn(runtime, connectionId, kind, out);
      return { status, streamId: status === 0 ? (koffi.decode(out, "uint64_t") as bigint) : 0n };
    },
    streamSendFrame: (runtime, streamId, encodedFrame, length, fin) =>
      streamSendFrameFn(runtime, streamId, encodedFrame, length, fin),
    streamShutdown: (runtime, streamId, code) => streamShutdownFn(runtime, streamId, code),
    nextEventAsync: async (runtime, timeoutMs, payload, payloadCapacity) => {
      const outEvent = koffi.alloc(CultMeshQuicEvent, 1);
      const outRequired = koffi.alloc("int32_t", 1);
      const status = await nextEventAsyncRaw(runtime, timeoutMs, outEvent, payload, payloadCapacity, outRequired);
      return {
        status,
        event: status === 1 ? (koffi.decode(outEvent, CultMeshQuicEvent) as NativeEventStruct) : null,
        required: koffi.decode(outRequired, "int32_t") as number,
      };
    },
    lastError: (runtime, destination, capacity) => lastErrorFn(runtime, destination, capacity),
    lastStatus: (runtime) => lastStatusFn(runtime),
  };
  return bindingsCache;
}

function readLastError(bindings: NativeBindings, runtime: CultMeshQuicNativeHandle): string {
  const buffer = Buffer.alloc(1024);
  const written = bindings.lastError(runtime, buffer, buffer.length);
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
  private static refCount = 0;

  private readonly handle: CultMeshQuicNativeHandle;
  private readonly bindings: NativeBindings;
  private readonly listenerListeners = new Map<bigint, NativeEventListener>();
  private readonly connectionListeners = new Map<bigint, NativeEventListener>();
  private closed = false;
  private pumpLoop: Promise<void>;

  private constructor(handle: CultMeshQuicNativeHandle, bindings: NativeBindings) {
    this.handle = handle;
    this.bindings = bindings;
    this.pumpLoop = this.pump();
  }

  static async open(): Promise<CultMeshQuicNativeRuntime> {
    if (!CultMeshQuicNativeRuntime.shared) {
      const bindings = loadBindings();
      const opened = bindings.runtimeOpen(null);
      if (opened.status !== 0 || opened.runtime === null) {
        throw new Error(`cultmesh_quic_runtime_open failed with status ${opened.status}.`);
      }
      CultMeshQuicNativeRuntime.shared = new CultMeshQuicNativeRuntime(opened.runtime, bindings);
    }
    CultMeshQuicNativeRuntime.refCount += 1;
    return CultMeshQuicNativeRuntime.shared;
  }

  /** Drops one reference; closes the shared runtime once none remain. */
  async release(): Promise<void> {
    CultMeshQuicNativeRuntime.refCount = Math.max(0, CultMeshQuicNativeRuntime.refCount - 1);
    if (CultMeshQuicNativeRuntime.refCount > 0) return;
    this.closed = true;
    this.bindings.runtimeClose(this.handle);
    CultMeshQuicNativeRuntime.shared = undefined;
    await this.pumpLoop;
  }

  onListenerEvent(listenerId: bigint, listener: NativeEventListener): void {
    this.listenerListeners.set(listenerId, listener);
  }

  offListenerEvent(listenerId: bigint): void {
    this.listenerListeners.delete(listenerId);
  }

  onConnectionEvent(connectionId: bigint, listener: NativeEventListener): void {
    this.connectionListeners.set(connectionId, listener);
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
    const opened = this.bindings.listenerOpen(this.handle, host, port, pkcs12, pkcs12.length, password);
    if (opened.status !== 0) {
      throw new Error(`cultmesh_quic_listener_open failed: ${readLastError(this.bindings, this.handle)}`);
    }
    return { listenerId: opened.listenerId, boundPort: opened.boundPort };
  }

  listenerClose(listenerId: bigint): void {
    this.bindings.listenerClose(this.handle, listenerId);
    this.offListenerEvent(listenerId);
  }

  connectionOpen(host: string, port: number): bigint {
    const opened = this.bindings.connectionOpen(this.handle, host, port);
    if (opened.status !== 0) {
      throw new Error(`cultmesh_quic_connection_open failed: ${readLastError(this.bindings, this.handle)}`);
    }
    return opened.connectionId;
  }

  connectionCertificateComplete(connectionId: bigint, accept: boolean): void {
    const status = this.bindings.connectionCertificateComplete(this.handle, connectionId, accept ? 1 : 0);
    if (status !== 0) {
      throw new Error(
        `cultmesh_quic_connection_certificate_complete failed: ${readLastError(this.bindings, this.handle)}`,
      );
    }
  }

  connectionShutdown(connectionId: bigint, code: bigint): void {
    this.bindings.connectionShutdown(this.handle, connectionId, code);
  }

  streamOpen(connectionId: bigint, kind: number): bigint {
    const opened = this.bindings.streamOpen(this.handle, connectionId, kind);
    if (opened.status !== 0) {
      throw new Error(`cultmesh_quic_stream_open failed: ${readLastError(this.bindings, this.handle)}`);
    }
    return opened.streamId;
  }

  streamSendFrame(streamId: bigint, encodedFrame: Uint8Array, fin: boolean): void {
    const status = this.bindings.streamSendFrame(this.handle, streamId, encodedFrame, encodedFrame.length, fin ? 1 : 0);
    if (status !== 0) {
      throw new Error(`cultmesh_quic_stream_send_frame failed: ${readLastError(this.bindings, this.handle)}`);
    }
  }

  streamShutdown(streamId: bigint, code: bigint): void {
    this.bindings.streamShutdown(this.handle, streamId, code);
  }

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
      this.listenerListeners.get(decoded.listenerId)?.(decoded);
      return;
    }
    this.connectionListeners.get(decoded.connectionId)?.(decoded);
  }

  private async pump(): Promise<void> {
    while (!this.closed) {
      let first: Awaited<ReturnType<NativeBindings["nextEventAsync"]>>;
      try {
        first = await this.bindings.nextEventAsync(this.handle, 250, null, 0);
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
        let second: Awaited<ReturnType<NativeBindings["nextEventAsync"]>>;
        try {
          second = await this.bindings.nextEventAsync(this.handle, 0, buffer, buffer.length);
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
