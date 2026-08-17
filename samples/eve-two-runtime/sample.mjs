import assert from "node:assert/strict";
import { mkdtemp } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { JSDOM } from "jsdom";
import { z } from "zod";
import { defineDocumentType } from "cultcache-ts";
import { CultMesh } from "cultmesh-ts";
import {
  parseEveCommandReceipt,
  parseEveSurfaceDocument,
} from "@gamecult/eve-contracts";
import { renderEveSurface } from "@gamecult/eve-browser-lowering";

const providerId = "sample.counter-provider";
const surfaceId = "sample.counter";
const stateKey = "counter:main";

const counterDocument = defineDocumentType({
  type: "sample.counter_state",
  schemaId: "sample.counter_state.v1",
  schema: z.object({ counterId: z.string(), count: z.number().int() }),
  name: "counterId",
});

const surfaceDocument = defineDocumentType({
  type: "gamecult.eve.surface",
  schemaId: "gamecult.eve.surface.v1",
  schema: z.unknown().transform(value => parseEveSurfaceDocument(value)),
});

const receiptDocument = defineDocumentType({
  type: "gamecult.eve.command_receipt",
  schemaId: "gamecult.eve.command_receipt.v1",
  schema: z.unknown().transform(value => parseEveCommandReceipt(value)),
  name: "receiptId",
});

function counterSurface() {
  return {
    type: "surface-state",
    schema: "gamecult.eve.surface.v1",
    providerId,
    providerKind: "sample.daemon",
    title: "CultMesh two-runtime counter",
    version: 1,
    updatedAtUtc: "2026-08-17T00:00:00Z",
    surface: {
      id: surfaceId,
      title: "Counter",
      root: {
        id: "counter.root",
        kind: "column",
        props: {},
        children: [
          {
            id: "counter.value",
            kind: "metric",
            props: { label: "Canonical count", value: 0 },
            children: [],
            stateBindings: [{
              targetProp: "value",
              pointerId: "sample.counter.count",
              sourceId: `${counterDocument.type}:${stateKey}`,
              schemaId: counterDocument.schemaId,
              routeKind: "shared-memory",
            }],
          },
          {
            id: "counter.increment",
            kind: "control.button",
            props: { label: "Increment", command: "sample.counter.increment" },
            children: [],
          },
        ],
      },
      styles: {},
    },
    commands: [{
      schema: "gamecult.eve.command.v1",
      command: "sample.counter.increment",
      label: "Increment",
      surfaceId,
      transport: "cultmesh",
      authority: "provider-daemon",
      result: "gamecult.eve.command_receipt.v1",
    }],
  };
}

function installDom() {
  const dom = new JSDOM("<!doctype html><html><head></head><body><main id='surface'></main></body></html>", {
    url: "https://eve.sample/",
    pretendToBeVisual: true,
  });
  for (const name of [
    "document",
    "window",
    "Element",
    "HTMLElement",
    "HTMLButtonElement",
    "HTMLInputElement",
    "HTMLTextAreaElement",
    "HTMLImageElement",
  ]) globalThis[name] = dom.window[name];
  return dom;
}

async function waitFor(predicate, description) {
  const deadline = Date.now() + 2_000;
  while (!predicate()) {
    if (Date.now() >= deadline) throw new Error(`Timed out waiting for ${description}.`);
    await new Promise(resolve => setTimeout(resolve, 5));
  }
}

assert.throws(
  () => parseEveSurfaceDocument({ providerId, surface: { id: surfaceId } }),
  /Invalid Eve surface/,
);
assert.throws(
  () => parseEveCommandReceipt({ receiptId: "unowned" }),
  /Invalid Eve commandReceipt/,
);

const statePath = join(await mkdtemp(join(tmpdir(), "cultmesh-eve-two-runtime-")), "state.cc");
const node = await CultMesh.startNode(statePath, {
  documents: [counterDocument, surfaceDocument, receiptDocument],
});
await node.put(counterDocument, stateKey, { counterId: stateKey, count: 0 });
await node.put(surfaceDocument, surfaceId, counterSurface());
await node.flush();

const state = node.document(counterDocument, stateKey, { pollMs: 5 });
const headless = state.observe({ context: "headless-client" });
await headless.ready;
const receiptsByIdempotencyKey = new Map();
const increment = CultMesh.operation("sample.counter.increment", async (_request, context) => {
  const idempotencyKey = context.idempotencyKey || "missing";
  const existing = receiptsByIdempotencyKey.get(idempotencyKey)
    || node.get(receiptDocument, `receipt:${idempotencyKey}`);
  if (existing) return existing;
  const next = await state.authoritativeWriter("provider-daemon").update(current => ({
    ...current,
    count: current.count + 1,
  }));
  const receipt = {
    receiptId: `receipt:${idempotencyKey}`,
    schema: "gamecult.eve.command_receipt.v1",
    commandId: idempotencyKey,
    command: "sample.counter.increment",
    state: "accepted",
    ownerRepo: "CultLib",
    authority: "provider-daemon",
    providerId,
    surfaceId,
    issuedAtUtc: new Date().toISOString(),
    sourceVersion: next.count,
    idempotencyKey,
    count: next.count,
  };
  receiptsByIdempotencyKey.set(idempotencyKey, receipt);
  await node.put(receiptDocument, receipt.receiptId, receipt);
  await node.flush();
  return receipt;
});

const dom = installDom();
const host = document.querySelector("#surface");
const browserVerse = CultMesh.verse("local", "browser-client");
let browserReceipt;
renderEveSurface(await node.document(surfaceDocument, surfaceId).latest(), host, {
  activeSurfaceId: surfaceId,
  clientId: "browser-client",
  commandSink: async intent => {
    browserReceipt = await increment.bind(browserVerse).invoke(intent.payload, {
      idempotencyKey: "browser-click-1",
    });
  },
  stateBindingResolver: async binding => {
    if (binding.pointerId !== "sample.counter.count") return undefined;
    return {
      latest: async () => (await state.latest("browser-client")).count,
      watch: callback => state.watch("browser-client", value => callback(value.count)),
    };
  },
});

await waitFor(() => host.querySelector(".metric-value")?.textContent === "0", "initial browser binding");
host.querySelector("button").click();
await waitFor(() => browserReceipt?.count === 1, "typed command receipt");
await waitFor(() => headless.current.count === 1, "headless convergence");
await waitFor(() => host.querySelector(".metric-value")?.textContent === "1", "browser convergence");

const duplicate = await increment.bind(browserVerse).invoke({}, { idempotencyKey: "browser-click-1" });
assert.equal(duplicate.receiptId, browserReceipt.receiptId);
assert.equal(headless.current.count, 1);

headless.dispose();
renderEveSurface(parseEveSurfaceDocument({
  type: "surface-state",
  schema: "gamecult.eve.surface.v1",
  providerId,
  providerKind: "sample.daemon",
  title: "Closed counter",
  version: 2,
  updatedAtUtc: "2026-08-17T00:00:01Z",
  surface: {
    id: "sample.closed",
    root: { id: "closed.root", kind: "column", props: {}, children: [] },
    styles: {},
  },
  commands: [],
}), host);
dom.window.close();
await node.flush();
const reopened = await CultMesh.startNode(statePath, {
  documents: [counterDocument, surfaceDocument, receiptDocument],
});
assert.equal(reopened.getRequired(counterDocument, stateKey).count, 1);
assert.equal(
  reopened.getRequired(receiptDocument, "receipt:browser-click-1").receiptId,
  browserReceipt.receiptId,
);

console.log(JSON.stringify({
  providerId,
  surfaceId,
  receiptId: browserReceipt.receiptId,
  loweringRuntime: "jsdom",
  browserCount: 1,
  headlessCount: 1,
  restartCount: 1,
}));
