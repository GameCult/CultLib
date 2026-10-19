// The TypeScript reader in its own process, for content-https.test.ts. Argv: <endpoints json> <manifests dir> [only case].
// One stdout line per case: {case, ok:true, sha256} (hashed here by node:crypto, not by the reader) or {case, ok:false, code, status}.
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { decodeManifest } from "cultnet-ts/content";
import { fetchVerifiedChunk, fetchVerifiedContent, CultMeshContentError } from "../../src/content.js";

const [endpointsJson, manifestsDir, only] = process.argv.slice(2) as [string, string, string | undefined];
const endpoints = JSON.parse(endpointsJson) as Record<"honest" | "tampered" | "corrupt", string>;
const manifest = (name: string) => decodeManifest(readFileSync(join(manifestsDir, `${name}.manifest`)));
const single = manifest("single");

const cases: Record<string, () => Promise<Uint8Array>> = {
  "honest-multi": () => fetchVerifiedContent(endpoints.honest, manifest("multi")),
  "honest-single": () => fetchVerifiedContent(endpoints.honest, single),
  "honest-empty": () => fetchVerifiedContent(endpoints.honest, manifest("empty")),
  "honest-single-chunk": () => fetchVerifiedChunk(endpoints.honest, single.chunks[0]!.chunkHash!, single.chunks[0]!.sizeBytes),
  "tampered-multi": () => fetchVerifiedContent(endpoints.tampered, manifest("multi")),
  "corrupt-single": () => fetchVerifiedContent(endpoints.corrupt, single),
};

for (const [name, run] of Object.entries(cases)) {
  if (only && only !== name) continue;
  try {
    console.log(JSON.stringify({ case: name, ok: true, sha256: createHash("sha256").update(await run()).digest("hex") }));
  } catch (error) {
    const e = error as CultMeshContentError;
    console.log(JSON.stringify({ case: name, ok: false, code: e.code, status: e.status }));
  }
}
