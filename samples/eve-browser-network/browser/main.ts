import { parseEveCommandReceipt, parseEveProviderAdvertisement, parseEveSurfaceDocument } from "@gamecult/eve-contracts";
import { renderEveSurface } from "@gamecult/eve-browser-lowering";
import {
  CultMeshBrowserClient,
  CultMeshBrowserOdinRendezvous,
  decodeCultNetOperationPayload,
  decodeCultNetPayload,
} from "cultmesh-browser";
import { defineDocumentType } from "@gamecult/cultcache-ts/document";

// A payload is read through the definition that says how its bytes are laid out. These are plain-map definitions: the
// provider's CultCache types are integer-keyed MessagePack, and the Eve documents are validated by their own parsers.
// CounterState is [counterId, count, receipts].
const counterDocument = defineDocumentType({
  type: "sample.counter_state",
  schemaId: "sample.counter_state.v1",
  schema: {
    parse: (value: unknown): [string, number, unknown] => {
      if (!Array.isArray(value) || typeof value[0] !== "string" || typeof value[1] !== "number") {
        throw new Error("The counter state document is not [counterId, count, receipts].");
      }
      return [value[0], value[1], value[2]];
    },
  },
});
const eveDocument = (type: string) => defineDocumentType({ type, schemaId: `${type}.v1`, schema: { parse: (value: unknown) => value } });
const surfaceDocument = eveDocument("gamecult.eve.surface");
const advertisementDocument = eveDocument("gamecult.eve.provider_advertisement");

const counterCount = (record: Parameters<typeof decodeCultNetPayload>[0]) =>
  decodeCultNetPayload(record, counterDocument)[1];

declare global {
  interface Window {
    __sampleReady?: boolean;
    __sampleCount?: number;
    __sampleReceipt?: { receiptId: string; count: number };
    __sampleCommandId?: string;
    __sampleConnectionStates?: string[];
    __sampleError?: string;
  }
}

const params = new URLSearchParams(location.search);
const odinEndpoint = params.get("odin");
const token = params.get("token") || "sample-session";
if (!odinEndpoint) throw new Error("The sample requires an Odin endpoint query parameter.");
document.cookie = `cultnet_session=${encodeURIComponent(token)}; Path=/; SameSite=Strict`;

const host = document.querySelector<HTMLElement>("#surface")!;
const receiptOutput = document.querySelector<HTMLOutputElement>("#receipt")!;

try {
  const mesh = await CultMeshBrowserClient.connect({
    verseId: "sample.counter",
    authorityRuntimeId: "sample.counter-daemon",
    runtimeId: "sample.chromium",
    trust: { mode: "local-development" },
    rendezvous: new CultMeshBrowserOdinRendezvous({
      endpoints: [odinEndpoint],
      runtimeId: "sample.chromium.discovery",
      transportVersion: "cultmesh.v0",
    }),
  });
  window.__sampleConnectionStates = [];
  mesh.watchState(state => window.__sampleConnectionStates!.push(state));
  window.__sampleCommandId = "browser-click-1";
  const counter = await mesh.leaseRawDocument({
    schemaId: "sample.counter_state.v1",
    recordKey: "counter:main",
    subscriptionId: "browser-counter",
  });
  const surface = await mesh.leaseRawDocument({
    schemaId: "gamecult.eve.surface.v1",
    recordKey: "sample.counter",
    subscriptionId: "browser-surface",
  });
  // The provider owns the declaration of where this surface's commands go and
  // what comes back; the page reads it rather than restating it.
  const advertisement = await mesh.leaseRawDocument({
    schemaId: "gamecult.eve.provider_advertisement.v1",
    recordKey: "sample.counter-ui",
    subscriptionId: "browser-provider",
  });
  // Eve's generated contract type and the lowering's own interface disagree on optional plugin requirements; the
  // value is the one parseEveProviderAdvertisement validated.
  const provider = parseEveProviderAdvertisement(
    decodeCultNetPayload(advertisement.current!, advertisementDocument),
  ) as unknown as NonNullable<Parameters<typeof renderEveSurface>[2]>["provider"];
  const currentSurface = parseEveSurfaceDocument(decodeCultNetPayload(surface.current!, surfaceDocument));
  renderEveSurface(currentSurface, host, {
    activeSurfaceId: "sample.counter",
    clientId: "sample.chromium",
    provider,
    commandSink: async intent => {
      const response = await mesh.invoke({
        serviceId: "sample.counter",
        operation: intent.operation.operationId,
        payloadSchema: "sample.increment.v1",
        payload: { amount: 1 },
        idempotencyKey: window.__sampleCommandId,
      });
      const receipt = decodeCultNetOperationPayload<{ receiptId: string; count: number }>(response);
      parseEveCommandReceipt(receipt);
      window.__sampleReceipt = receipt;
      receiptOutput.textContent = receipt.receiptId;
    },
    stateBindingResolver: async binding => {
      if (binding.pointerId !== "sample.counter.count") return undefined;
      return {
        latest: async () => counterCount(counter.current!),
        watch: callback => counter.watch(record => {
          if (!record) return;
          const count = counterCount(record);
          window.__sampleCount = count;
          callback(count);
        }),
      };
    },
  });
  window.__sampleCount = counterCount(counter.current!);
  window.__sampleReady = true;
} catch (error) {
  const details = error instanceof AggregateError
    ? error.errors.map(inner => inner instanceof Error ? inner.stack || inner.message : String(inner)).join("\n---\n")
    : "";
  window.__sampleError = (error instanceof Error ? error.stack || error.message : String(error))
    + (details ? `\n${details}` : "");
  host.textContent = window.__sampleError;
  throw error;
}
