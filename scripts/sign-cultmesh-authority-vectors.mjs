// The TypeScript -> C# direction of the CultMesh authority parity check.
//
// `contracts/cultmesh/authority-route-vectors.json` is written by the C#
// reference and verified by TypeScript. This script writes the mirror file,
// `contracts/cultmesh/authority-route-vectors.ts-signed.json`: TypeScript
// builds the route and session transcripts with the shared module
// (`packages/cultnet-ts/src/cultmesh-authority.ts`), signs them with fixed
// TypeScript-owned test keys, and the C# test
// `CultMeshAuthorityProofTests.AuthorityRouteVectorsSignedByTypeScriptVerifyHere`
// verifies the committed file with the reference implementation only.
//
// The inputs are deliberately awkward: a quic scheme with a query, three
// unsorted protocol ids, non-ASCII generation text, a non-default priority.
//
//   npm run build --workspace packages/cultnet-ts
//   node scripts/sign-cultmesh-authority-vectors.mjs
//
// Rewrite the file only when the transcript rules change; ECDSA signatures are
// randomised, so every run produces a different byte-for-byte file. The keys
// sign only this vector and prove nothing else.

import { createHash, createPrivateKey, sign } from "node:crypto";
import { existsSync, writeFileSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const modulePath = join(repoRoot, "packages", "cultnet-ts", "dist", "cultmesh-authority.js");
const outputPath = join(repoRoot, "contracts", "cultmesh", "authority-route-vectors.ts-signed.json");
if (!existsSync(modulePath)) {
  throw new Error(`${modulePath} is missing; run 'npm run build --workspace packages/cultnet-ts' first.`);
}
const { canonicalRoute, canonicalSession, verifyAuthorityRoute, verifyProviderSessionProof } =
  createRequire(import.meta.url)(modulePath);

const NOW = 1_800_000_000_000;
const odin = testKey("odin-ts-vector", {
  kty: "EC", crv: "P-256",
  x: "lqw9TE0EyuDU5Ghe8SafIbBBYkhHMUF7HXJQUc0nXsw",
  y: "nUj-Xhakih97JiJcTUNMHM9JIo7MVQaauuAkCXph-tw",
  d: "8X45DCbNJ7IR2iPoz5JUC_zB8fHh3zoUJUaAA7mnOnQ",
});
const provider = testKey("provider-ts-vector", {
  kty: "EC", crv: "P-256",
  x: "JKyjtWnoE5fhSWbeqr188SbReQWCjssf5mSMEa5pV-E",
  y: "vtduzojDpbT49sbxBSsBQ-f3PG3rm-CUk_MTy2MM_ws",
  d: "ZSdIb9sLpAfb-nTAUR1Y1re4OsVM4ABJ1Gpq5pStSyQ",
});

function testKey(keyId, jwk) {
  return {
    privateKey: createPrivateKey({ key: jwk, format: "jwk" }),
    publicKey: {
      keyId,
      x: Buffer.from(jwk.x, "base64url").toString("base64"),
      y: Buffer.from(jwk.y, "base64url").toString("base64"),
    },
  };
}

function signP1363(privateKey, payload) {
  return sign("sha256", payload, { key: privateKey, dsaEncoding: "ieee-p1363" }).toString("base64");
}

const unsigned = {
  verseId: "aetheria",
  authorityRuntimeId: "aetheria-daemon",
  endpoint: "cultmesh-state+quic://provider.example:4433/?cert-sha256=ABCDEF",
  protocolIds: ["cultmesh.documents.v1", "cultmesh.content.v1", "cultmesh.realtime.v1"],
  priority: 13,
  generation: "ts-generation-ünïcode-∞-1",
  certificate: {
    providerKey: provider.publicKey,
    odinKeyId: odin.publicKey.keyId,
    issuedAtUnixMilliseconds: NOW - 60_000,
    expiresAtUnixMilliseconds: NOW + 3_600_000,
    signature: "",
  },
};
const route = {
  ...unsigned,
  certificate: { ...unsigned.certificate, signature: signP1363(odin.privateKey, canonicalRoute(unsigned)) },
};
const request = {
  schemaVersion: "cultmesh.session_open.v2",
  messageId: "ts-vector-message-1",
  sourceRuntimeId: "ts-vector-consumer",
  verseId: "aetheria",
  authorityRuntimeId: "aetheria-daemon",
  protocolId: "cultmesh.documents.v1",
  routeGeneration: route.generation,
  clientNonce: createHash("sha256").update("ts-vector-nonce").digest("base64"),
};
const providerSignature = signP1363(provider.privateKey, canonicalSession(request, route.endpoint));

// A file TypeScript cannot verify itself would make a C# refusal meaningless.
await verifyAuthorityRoute(route, { mode: "authenticated-remote", odinRoots: [odin.publicKey], now: () => NOW });
if (!await verifyProviderSessionProof(request, route.endpoint, provider.publicKey, providerSignature)) {
  throw new Error("The TypeScript module refused its own session proof.");
}

writeFileSync(outputPath, JSON.stringify({
  nowUnixMilliseconds: NOW,
  odinRoot: odin.publicKey,
  route,
  sessionProof: { request, providerKeyId: provider.publicKey.keyId, providerSignature },
}, null, 2) + "\n");
console.log(`wrote ${outputPath}`);
