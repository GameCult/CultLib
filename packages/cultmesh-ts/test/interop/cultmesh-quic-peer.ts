// The TypeScript half of the QUIC realtime interop lane: a one-shot process
// that dials an advertised `cultmesh-state+quic://` endpoint with the native
// connector, receives `--expect` frames from a peer (normally the C# managed
// provider started by `cultnet-interop.test.ts`'s
// "CultMesh QUIC realtime" test), and prints them as one JSON line. It never
// imports `cultmesh-ts` itself (the harness drives processes, and the
// dependency direction is `cultmesh-ts` -> `cultnet-ts`, never the reverse);
// this script reaches the connector through its own package's `src/`.

import process from "node:process";

import { CultMeshQuicRealtimeConnector, type CultMeshRealtimeTarget } from "../../src/realtime-quic";

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

async function main(): Promise<void> {
  const [mode, ...rest] = process.argv.slice(2);
  if (mode !== "dial") throw new Error("Expected mode: dial");
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
