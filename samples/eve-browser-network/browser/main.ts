import { parseEveCommandReceipt, parseEveSurfaceDocument } from "@gamecult/eve-contracts";
import { renderEveSurface } from "@gamecult/eve-browser-lowering";
import {
  CultMeshBrowserClient,
  decodeCultNetOperationPayload,
  decodeCultNetPayload,
} from "cultmesh-browser";

declare global {
  interface Window {
    __sampleReady?: boolean;
    __sampleCount?: number;
    __sampleReceipt?: { receiptId: string; count: number };
    __sampleError?: string;
  }
}

const params = new URLSearchParams(location.search);
const endpoint = params.get("endpoint");
const token = params.get("token") || "sample-session";
if (!endpoint) throw new Error("The sample requires an endpoint query parameter.");
document.cookie = `cultnet_session=${encodeURIComponent(token)}; Path=/; SameSite=Strict`;

const host = document.querySelector<HTMLElement>("#surface")!;
const receiptOutput = document.querySelector<HTMLOutputElement>("#receipt")!;

try {
  const mesh = await CultMeshBrowserClient.connect({
    verseId: "sample.counter",
    providerId: "sample.counter-provider",
    runtimeId: "sample.chromium",
    rendezvous: {
      resolve: async identity => ({ ...identity, endpoint }),
    },
  });
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
  const currentSurface = parseEveSurfaceDocument(decodeCultNetPayload(surface.current!));
  renderEveSurface(currentSurface, host, {
    activeSurfaceId: "sample.counter",
    clientId: "sample.chromium",
    commandSink: async intent => {
      const response = await mesh.invoke({
        serviceId: "sample.counter",
        operation: intent.command,
        payloadSchema: "sample.increment.v1",
        payload: { amount: 1 },
        idempotencyKey: "browser-click-1",
      });
      const receipt = parseEveCommandReceipt(decodeCultNetOperationPayload(response)) as {
        receiptId: string;
        count: number;
      };
      window.__sampleReceipt = receipt;
      receiptOutput.textContent = receipt.receiptId;
    },
    stateBindingResolver: async binding => {
      if (binding.pointerId !== "sample.counter.count") return undefined;
      return {
        latest: async () => decodeCultNetPayload<{ count: number }>(counter.current!).count,
        watch: callback => counter.watch(record => {
          if (!record) return;
          const count = decodeCultNetPayload<{ count: number }>(record).count;
          window.__sampleCount = count;
          callback(count);
        }),
      };
    },
  });
  window.__sampleCount = decodeCultNetPayload<{ count: number }>(counter.current!).count;
  window.__sampleReady = true;
} catch (error) {
  window.__sampleError = error instanceof Error ? error.stack || error.message : String(error);
  host.textContent = window.__sampleError;
  throw error;
}
