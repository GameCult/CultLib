// The TypeScript half of the QUIC realtime interop lane. Two modes:
//
// `dial` (Cut 4): a one-shot process that dials an advertised
// `cultmesh-state+quic://` endpoint with the native connector, receives
// `--expect` frames from a peer (normally the C# managed provider started by
// `cultnet-interop.test.ts`'s "CultMesh QUIC realtime" test), and prints them
// as one JSON line.
//
// `serve` (Cut 5): the StreamPixels role. Opens a `CultMeshQuicRealtimeProvider`
// listener, prints `{status: "ready", endpoint}` (the same shape the C#
// `quic-realtime-serve` peer prints), waits for a first peer before
// broadcasting (mirroring `QuicRealtimeServeAsync`'s
// `WaitForFirstConnectionAsync`, since a broadcast to nobody would otherwise
// silently no-op), then broadcasts `--frames` frames at `--delivery` with
// `--interval-ms` between them, watching stdin for EOF as its own shutdown
// signal like the C# peer's `WatchStdinCloseAsync`.
//
// Neither mode imports `cultmesh-ts` itself (the harness drives processes,
// and the dependency direction is `cultmesh-ts` -> `cultnet-ts`, never the
// reverse); this script reaches the provider/connector through its own
// package's `src/`.

import { readFileSync } from "node:fs";
import { join } from "node:path";
import process from "node:process";

import {
  CultMeshQuicRealtimeConnector,
  CultMeshQuicRealtimeProvider,
  type CultMeshRealtimeTarget,
} from "../../src/realtime-quic";
import type { CultMeshRealtimeDelivery, CultMeshRealtimeFrame } from "../../src/realtime-wire";

// `__dirname` at runtime is `dist-test/test/interop`; fixtures are binary and
// are never compiled/copied there, so they are read from their source
// location, three levels up. The same self-signed test credential the unit
// tests use (`test/fixtures/README.md`): not a production credential.
const FIXTURE_P12 = join(__dirname, "..", "..", "..", "test", "fixtures", "quic-test.p12");

function parseArgs(argv: readonly string[]): Map<string, string> {
  const args = new Map<string, string>();
  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (!token.startsWith("--")) continue;
    const name = token.slice(2);
    const value = argv[index + 1];
    if (!value || value.startsWith("--")) throw new Error(`Missing value for --${name}`);
    args.set(name, value);
    index += 1;
  }
  return args;
}

function requireArg(args: Map<string, string>, name: string): string {
  const value = args.get(name);
  if (!value) throw new Error(`Missing required argument --${name}`);
  return value;
}

function optionalIntArg(args: Map<string, string>, name: string, fallback: number): number {
  const raw = args.get(name);
  if (!raw) return fallback;
  const parsed = Number.parseInt(raw, 10);
  if (!Number.isInteger(parsed)) throw new Error(`Argument --${name} must be an integer.`);
  return parsed;
}

function writeJsonLine(value: unknown): void {
  process.stdout.write(`${JSON.stringify(value)}\n`);
}

function writeLog(event: string, payload: Record<string, unknown>): void {
  process.stderr.write(`${JSON.stringify({ event, ...payload })}\n`);
}

function watchStdinCloseAsync(): Promise<void> {
  return new Promise((resolve) => {
    process.stdin.resume();
    process.stdin.once("end", resolve);
  });
}

async function serveAsync(args: Map<string, string>): Promise<void> {
  const frames = optionalIntArg(args, "frames", 5);
  const deliveryArg = args.get("delivery") ?? "latest-only";
  if (deliveryArg !== "latest-only" && deliveryArg !== "reliable-ordered") {
    throw new Error(`Unsupported --delivery '${deliveryArg}'.`);
  }
  const delivery = deliveryArg as CultMeshRealtimeDelivery;
  const intervalMs = optionalIntArg(args, "interval-ms", 50);
  const port = optionalIntArg(args, "port", 0);

  const provider = await CultMeshQuicRealtimeProvider.listen({
    host: "127.0.0.1",
    port,
    serverCertificate: { pkcs12: readFileSync(FIXTURE_P12), password: "" },
  });
  writeJsonLine({ status: "ready", endpoint: provider.advertisedEndpoint });

  try {
    // A broadcast to zero peers is not an error, but it sends nothing:
    // matching the C# `quic-realtime-serve` peer, wait for a first
    // connection, bounded, so a peer that never shows up still exits.
    const connectDeadline = Date.now() + 30_000;
    while (provider.connectionCount === 0) {
      if (Date.now() >= connectDeadline) throw new Error("quic-realtime-serve: no client connected within 30000ms.");
      await new Promise((resolve) => setTimeout(resolve, 20));
    }

    const stdinClosed = watchStdinCloseAsync();
    let stopped = false;
    void stdinClosed.then(() => {
      stopped = true;
    });
    for (let sequence = 1; sequence <= frames && !stopped; sequence += 1) {
      const frame: CultMeshRealtimeFrame = {
        channelId: "interop.quic-realtime",
        schemaId: "gamecult.interop.quic_realtime_frame.v0",
        bodyId: "interop:quic-realtime:frame",
        producerEpoch: 1n,
        sequence: BigInt(sequence),
        delivery,
        payload: Buffer.from(`frame-${sequence}`, "utf8"),
      };
      await provider.broadcast(frame);
      writeLog("quic-realtime-serve", { sent: sequence });
      if (intervalMs > 0) await new Promise((resolve) => setTimeout(resolve, intervalMs));
    }
  } finally {
    provider.dispose();
    // Mirrors the C# peer returning from `QuicRealtimeServeAsync`: once
    // broadcasting is done (or stdin closed early), let the process exit on
    // its own instead of keeping the event loop alive on a stdin that may
    // never close. The harness still ends/kills this process explicitly in
    // its own cleanup; this only lets a plain manual run exit cleanly too.
    process.stdin.pause();
  }
}

async function main(): Promise<void> {
  const [mode, ...rest] = process.argv.slice(2);
  if (mode === "serve") {
    await serveAsync(parseArgs(rest));
    return;
  }
  if (mode !== "dial") throw new Error("Expected mode: dial | serve");
  const args = parseArgs(rest);
  const endpoint = requireArg(args, "endpoint");
  const expect = optionalIntArg(args, "expect", 1);
  const timeoutMs = optionalIntArg(args, "timeout-ms", 15_000);

  const target: CultMeshRealtimeTarget = { verseId: "interop", authorityRuntimeId: "interop.csharp-provider" };
  const connector = new CultMeshQuicRealtimeConnector();
  const transport = await connector.connect(
    { endpoint, authorityRuntimeId: target.authorityRuntimeId, priority: 0, generation: "interop" },
    target,
  );
  writeLog("connected", { endpoint, transportId: transport.transportId });

  const frames: Array<{
    channelId: string;
    schemaId: string;
    bodyId: string;
    producerEpoch: string;
    sequence: string;
    delivery: string;
    payloadHex: string;
  }> = [];
  try {
    const deadline = Date.now() + timeoutMs;
    while (frames.length < expect) {
      const remaining = deadline - Date.now();
      if (remaining <= 0) throw new Error(`Timed out after receiving ${frames.length} of ${expect} frames.`);
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), remaining);
      try {
        const frame = await transport.receiveFrame(controller.signal);
        frames.push({
          channelId: frame.channelId,
          schemaId: frame.schemaId,
          bodyId: frame.bodyId,
          producerEpoch: frame.producerEpoch.toString(),
          sequence: frame.sequence.toString(),
          delivery: frame.delivery,
          payloadHex: Buffer.from(frame.payload).toString("hex"),
        });
      } finally {
        clearTimeout(timer);
      }
    }
  } finally {
    transport.dispose();
  }

  writeJsonLine({ endpoint, transportId: transport.transportId, frames });
}

main().catch((error) => {
  writeLog("fatal", { error: error instanceof Error ? (error.stack ?? error.message) : String(error) });
  process.exitCode = 1;
});
