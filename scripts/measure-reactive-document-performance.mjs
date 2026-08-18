import { monitorEventLoopDelay, performance } from "node:perf_hooks";
import process from "node:process";

import { CultMesh } from "../packages/cultmesh-ts/dist/index.js";

const quick = process.argv.includes("--quick");
const idleMs = quick ? 250 : 10_000;
const activeMs = quick ? 500 : 10_000;
const results = [];

for (const documentCount of [1, 100, 1000]) {
  results.push(await measure(documentCount));
}

console.log(JSON.stringify({
  runtime: "typescript",
  workload: {
    documentCounts: [1, 100, 1000],
    payloadBytes: 16 * 1024,
    idleSeconds: idleMs / 1000,
    activeSeconds: activeMs / 1000,
    updateRateHz: 60,
    changedFraction: 0.01,
  },
  results,
}, undefined, 2));

if (results.some(result =>
  result.actualPublishes !== result.expectedPublishes ||
  result.p99PublishLatencyMilliseconds >= 250 ||
  result.activeHeapPeakGrowthBytes >= 128 * 1024 * 1024)) {
  process.exitCode = 1;
}

async function measure(documentCount) {
  const payload = "x".repeat(16 * 1024);
  const changedDocumentCount = Math.max(1, Math.floor(documentCount / 100));
  const reactiveDocuments = [];
  const publishSignals = new Array(documentCount);
  const updateStartedAt = new Float64Array(documentCount);
  const latencies = [];
  let payloadBytesPublished = 0;
  let publishes = 0;

  for (let index = 0; index < documentCount; index++) {
    let current = { id: `performance:${index}`, payload, revision: 1 };
    let watcher;
    const document = CultMesh.document(
      `performance:${index}`,
      { schemaId: "gamecult.mesh.performance_probe.v1" },
      async () => current,
      {
        submitPrediction: async (_context, value) => {
          payloadBytesPublished += Buffer.byteLength(JSON.stringify(value));
          latencies.push(performance.now() - updateStartedAt[index]);
          publishes++;
          current = value;
          watcher?.(value);
          publishSignals[index]?.();
        },
        watchDocument: (_context, callback) => {
          watcher = callback;
          return () => { watcher = undefined; };
        },
      },
    );
    const reactive = document.predictionWriter().reactive();
    reactiveDocuments.push(reactive);
  }

  await Promise.all(reactiveDocuments.map(document => document.ready));
  collect();
  const idleHeapStart = process.memoryUsage().heapUsed;
  const idleCpuStart = process.cpuUsage();
  await delay(idleMs);
  collect();
  const idleHeapGrowthBytes = process.memoryUsage().heapUsed - idleHeapStart;
  const idleCpu = process.cpuUsage(idleCpuStart);

  collect();
  const activeHeapStart = process.memoryUsage().heapUsed;
  let activeHeapPeak = activeHeapStart;
  const activeCpuStart = process.cpuUsage();
  const eventLoop = monitorEventLoopDelay({ resolution: 10 });
  eventLoop.enable();
  const activeStartedAt = performance.now();
  let frames = 0;
  while (performance.now() - activeStartedAt < activeMs) {
    const completions = [];
    for (let index = 0; index < changedDocumentCount; index++) {
      completions.push(new Promise(resolve => { publishSignals[index] = resolve; }));
      updateStartedAt[index] = performance.now();
      reactiveDocuments[index].update(value => { value.revision++; });
    }
    await Promise.all(completions);
    frames++;
    activeHeapPeak = Math.max(activeHeapPeak, process.memoryUsage().heapUsed);
    const remaining = frames * (1000 / 60) - (performance.now() - activeStartedAt);
    if (remaining > 0) await delay(remaining);
  }
  eventLoop.disable();
  const activeElapsedSeconds = (performance.now() - activeStartedAt) / 1000;
  const activeCpu = process.cpuUsage(activeCpuStart);
  const ordered = latencies.toSorted((left, right) => left - right);

  for (const document of reactiveDocuments) document.dispose();

  return {
    documentCount,
    changedDocumentCount,
    frames,
    expectedPublishes: frames * changedDocumentCount,
    actualPublishes: publishes,
    payloadBytesPublished,
    idleHeapGrowthBytes,
    idleCpuMilliseconds: (idleCpu.user + idleCpu.system) / 1000,
    activeHeapPeakGrowthBytes: activeHeapPeak - activeHeapStart,
    activeCpuMilliseconds: (activeCpu.user + activeCpu.system) / 1000,
    publishedPayloadBytesPerSecond: payloadBytesPublished / activeElapsedSeconds,
    p50PublishLatencyMilliseconds: percentile(ordered, 0.50),
    p95PublishLatencyMilliseconds: percentile(ordered, 0.95),
    p99PublishLatencyMilliseconds: percentile(ordered, 0.99),
    p99EventLoopDelayMilliseconds: eventLoop.percentile(99) / 1_000_000,
  };
}

function percentile(ordered, ratio) {
  if (ordered.length === 0) return 0;
  return ordered[Math.max(0, Math.ceil(ordered.length * ratio) - 1)];
}

function collect() {
  if (typeof globalThis.gc !== "function") {
    throw new Error("Run this probe with node --expose-gc.");
  }
  globalThis.gc();
}

function delay(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}
