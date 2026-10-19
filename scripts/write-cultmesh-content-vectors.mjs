// The TypeScript -> C# direction of the CultMesh content parity check.
//
// `contracts/cultmesh/content-vectors.cs-written.json` and `.rs-written.json` are written by the reference and by Rust
// and decoded by TypeScript. This script writes the mirror file, `contracts/cultmesh/content-vectors.ts-written.json`:
// `packages/cultnet-ts/src/content.ts` packs each declared input and encodes the manifest, and the C# test
// `CultMeshContentVectorTests.TypeScriptManifestVectorsMatchTheReference` judges the committed file with the reference
// only (it decodes the bytes, packs the same input itself and compares, and derives the record key).
//
// Deliberately not the C# file's inputs: a multi-byte artifact id, no version, integer-like metadata keys that a JS object
// would reorder, a different body and chunk size. Agreeing on a copy of the other side's inputs would prove less.
//
//   npm run build --workspace packages/cultnet-ts
//   node scripts/write-cultmesh-content-vectors.mjs
//
// Nothing here is randomised: rerunning on an unchanged codec rewrites the same bytes.

import { existsSync, writeFileSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const modulePath = join(repoRoot, "packages", "cultnet-ts", "dist", "content.js");
const outputPath = join(repoRoot, "contracts", "cultmesh", "content-vectors.ts-written.json");
if (!existsSync(modulePath)) {
  throw new Error(`${modulePath} is missing; run 'npm run build --workspace packages/cultnet-ts' first.`);
}
const { packContent, encodeManifest, artifactRecordKey } = createRequire(import.meta.url)(modulePath);

const common = {
  artifactId: "Pixel Heroes/H\u00fct \u2603",
  kind: "asset",
  version: "",
  mimeType: "application/x-cultnet-ts-vector",
  createdAtUtc: "2026-10-19T00:00:00.0000000+00:00",
  chunkSizeBytes: 131072,
  tags: ["a", "b"],
  metadata: [["10", "x"], ["alpha", "y"], ["2", "z"]],
};
const inputs = [
  ["ts multi-chunk body", { ...common, body: { length: 777777, multiplier: 7, modulus: 256 } }],
  ["ts empty body", { ...common, body: { length: 0, multiplier: 7, modulus: 256 } }],
  ["ts single chunk, versioned", { ...common, version: "V1 Beta", chunkSizeBytes: 4194304, tags: [], metadata: [], body: { length: 1000, multiplier: 7, modulus: 256 } }],
];

const vectors = [];
for (const [label, input] of inputs) {
  const { body, ...options } = input;
  const bytes = Uint8Array.from({ length: body.length }, (_, index) => (index * body.multiplier) % body.modulus);
  const { manifest } = await packContent(input.artifactId, bytes, options);
  vectors.push({
    label,
    kind: "manifest",
    input,
    recordKey: await artifactRecordKey(manifest.artifactId, manifest.version, manifest.contentHash),
    messagePackBase64: Buffer.from(encodeManifest(manifest)).toString("base64"),
  });
}
writeFileSync(outputPath, `${JSON.stringify({ vectors }, null, 2)}\n`);
console.log(`wrote ${vectors.length} vectors to ${outputPath}`);
