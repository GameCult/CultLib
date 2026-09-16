// The shared CultMesh route verifier, pinned rule by rule.
//
// Routes here are signed with `node:crypto` and verified through the shared
// module. The transcript bytes themselves are pinned by
// `contracts/cultmesh/authority-route-vectors.json`, which the C# reference
// writes (`CultMeshAuthorityProofTests.AuthorityRouteVectorsAreSharedWithTypeScript`
// under CULTMESH_WRITE_VECTORS=1); a fixture authored on this side alone would
// only agree with itself.

import test from "node:test";
import assert from "node:assert/strict";
import { generateKeyPairSync, sign, type KeyObject } from "node:crypto";
import { readFileSync } from "node:fs";
import { join } from "node:path";

import type { CultMeshSessionOpenMessage } from "../src/contracts";
import {
  base64ToBytes,
  bytesToBase64,
  canonicalRoute,
  canonicalSession,
  isLoopbackEndpoint,
  isProtectedEndpoint,
  verifyAuthorityRoute,
  verifyP256,
  verifyProviderSessionProof,
  type CultMeshAuthorityRouteView,
  type CultMeshP256PublicKey,
} from "../src/cultmesh-authority";

const NOW = 1_800_000_000_000;
const VECTORS_PATH = join(__dirname, "..", "..", "..", "..", "contracts", "cultmesh", "authority-route-vectors.json");

interface Vectors {
  nowUnixMilliseconds: number;
  odinRoot: CultMeshP256PublicKey;
  route: CultMeshAuthorityRouteView;
  sessionProof: {
    request: CultMeshSessionOpenMessage;
    providerKeyId: string;
    providerSignature: string;
  };
}

function keyPair(keyId: string): { privateKey: KeyObject; publicKey: CultMeshP256PublicKey } {
  const pair = generateKeyPairSync("ec", { namedCurve: "P-256" });
  const jwk = pair.publicKey.export({ format: "jwk" });
  return {
    privateKey: pair.privateKey,
    publicKey: {
      keyId,
      x: Buffer.from(jwk.x!, "base64url").toString("base64"),
      y: Buffer.from(jwk.y!, "base64url").toString("base64"),
    },
  };
}

function signP1363(privateKey: KeyObject, payload: Uint8Array): string {
  return sign("sha256", payload, { key: privateKey, dsaEncoding: "ieee-p1363" }).toString("base64");
}

function signedRoute(endpoint = "wss://provider.example/mesh") {
  const odin = keyPair("odin-root-1");
  const provider = keyPair("provider-1");
  const unsigned: CultMeshAuthorityRouteView = {
    verseId: "sample.counter",
    authorityRuntimeId: "sample.counter-daemon",
    endpoint,
    protocolId: "cultmesh.documents.v1",
    protocolIds: ["cultmesh.documents.v1"],
    priority: 0,
    generation: "signed-generation-1",
    certificate: {
      providerKey: provider.publicKey,
      odinKeyId: odin.publicKey.keyId,
      issuedAtUnixMilliseconds: NOW - 60_000,
      expiresAtUnixMilliseconds: NOW + 3_600_000,
      signature: "",
    },
  };
  const route: CultMeshAuthorityRouteView = {
    ...unsigned,
    certificate: { ...unsigned.certificate!, signature: signP1363(odin.privateKey, canonicalRoute(unsigned)) },
  };
  return { route, odin, provider };
}

function request(clientNonce = "nonce-a"): CultMeshSessionOpenMessage {
  return {
    schemaVersion: "cultmesh.session_open.v2",
    messageId: "message-1",
    sourceRuntimeId: "consumer-1",
    verseId: "sample.counter",
    authorityRuntimeId: "sample.counter-daemon",
    protocolId: "cultmesh.documents.v1",
    routeGeneration: "signed-generation-1",
    clientNonce,
  };
}

const remoteTrust = (odinRoot: CultMeshP256PublicKey, now: number = NOW) =>
  ({ mode: "authenticated-remote" as const, odinRoots: [odinRoot], now: () => now });

test("a signed remote route verifies against its Odin root", async () => {
  const { route, odin } = signedRoute();
  await verifyAuthorityRoute(route, remoteTrust(odin.publicKey));
});

test("an unsigned remote route is refused in both trust modes", async () => {
  const { route } = signedRoute();
  const unsigned = { ...route, certificate: undefined };
  await assert.rejects(verifyAuthorityRoute(unsigned, { mode: "authenticated-remote" }), /Odin-signed authority certificate/);
  await assert.rejects(verifyAuthorityRoute(unsigned, { mode: "local-development" }), /Odin-signed authority certificate/);
});

test("an unsigned loopback route is accepted only under local-development", async () => {
  const { route } = signedRoute("ws://127.0.0.1:4050/mesh");
  const unsigned = { ...route, certificate: undefined };
  await verifyAuthorityRoute(unsigned, { mode: "local-development" });
  await assert.rejects(verifyAuthorityRoute(unsigned, { mode: "authenticated-remote" }), /Odin-signed authority certificate/);
});

test("a certified route on an unprotected scheme is refused unless it is loopback under local-development", async () => {
  const remote = signedRoute("ws://provider.example/mesh");
  await assert.rejects(verifyAuthorityRoute(remote.route, remoteTrust(remote.odin.publicKey)), /channel protection/);
  const loopback = signedRoute("ws://localhost:4050/mesh");
  await assert.rejects(verifyAuthorityRoute(loopback.route, remoteTrust(loopback.odin.publicKey)), /channel protection/);
  await verifyAuthorityRoute(loopback.route, { ...remoteTrust(loopback.odin.publicKey), mode: "local-development" });
});

test("the validity window is half-open: issuedAt inclusive, expiresAt exclusive", async () => {
  const { route, odin } = signedRoute();
  const certificate = route.certificate!;
  await verifyAuthorityRoute(route, remoteTrust(odin.publicKey, certificate.issuedAtUnixMilliseconds));
  await verifyAuthorityRoute(route, remoteTrust(odin.publicKey, certificate.expiresAtUnixMilliseconds - 1));
  await assert.rejects(
    verifyAuthorityRoute(route, remoteTrust(odin.publicKey, certificate.expiresAtUnixMilliseconds)),
    /not currently valid/,
  );
  await assert.rejects(
    verifyAuthorityRoute(route, remoteTrust(odin.publicKey, certificate.issuedAtUnixMilliseconds - 1)),
    /not currently valid/,
  );
});

test("an Odin root the consumer does not trust is refused before any signature check", async () => {
  const { route } = signedRoute();
  const stranger = keyPair("odin-root-1");
  await assert.rejects(verifyAuthorityRoute(route, { mode: "authenticated-remote", odinRoots: [], now: () => NOW }), /is not trusted/);
  await assert.rejects(
    verifyAuthorityRoute(route, { mode: "authenticated-remote", odinRoots: [{ ...stranger.publicKey, keyId: "other-root" }], now: () => NOW }),
    /is not trusted/,
  );
  await assert.rejects(verifyAuthorityRoute(route, remoteTrust(stranger.publicKey)), /signature is invalid/);
});

test("a mutated endpoint invalidates the Odin signature", async () => {
  const { route, odin } = signedRoute();
  await assert.rejects(
    verifyAuthorityRoute({ ...route, endpoint: "wss://evil.example/mesh" }, remoteTrust(odin.publicKey)),
    /signature is invalid/,
  );
});

test("a signature that is not 64 P1363 bytes is refused", async () => {
  const { route, odin } = signedRoute();
  const bytes = base64ToBytes(route.certificate!.signature);
  for (const wrong of [bytes.subarray(0, 63), new Uint8Array([...bytes, 0])]) {
    await assert.rejects(
      verifyAuthorityRoute(
        { ...route, certificate: { ...route.certificate!, signature: bytesToBase64(wrong) } },
        remoteTrust(odin.publicKey),
      ),
      /signature is invalid/,
    );
  }
  assert.equal(await verifyP256(odin.publicKey, canonicalRoute(route), "not base64!"), false);
});

test("the provider session proof binds the nonce, the endpoint and the certified key", async () => {
  const { route, provider } = signedRoute();
  const open = request();
  const signature = signP1363(provider.privateKey, canonicalSession(open, route.endpoint));
  assert.equal(await verifyProviderSessionProof(open, route.endpoint, provider.publicKey, signature), true);
  assert.equal(await verifyProviderSessionProof(request("nonce-b"), route.endpoint, provider.publicKey, signature), false);
  assert.equal(await verifyProviderSessionProof(open, "wss://evil.example/mesh", provider.publicKey, signature), false);
  assert.equal(await verifyProviderSessionProof(open, route.endpoint, keyPair("provider-1").publicKey, signature), false);
});

test("isProtectedEndpoint is the C# rule: wss, https, or any scheme containing quic", () => {
  for (const accepted of ["wss://provider.example/mesh", "https://provider.example/content", "cultmesh-state+quic://provider.example:4433"]) {
    assert.equal(isProtectedEndpoint(accepted), true, accepted);
  }
  for (const refused of ["ws://provider.example/mesh", "cultnet+tcp://provider.example:3076", "not a url"]) {
    assert.equal(isProtectedEndpoint(refused), false, refused);
  }
});

test("isLoopbackEndpoint recognises localhost, 127.0.0.1 and ::1 only", () => {
  for (const loopback of ["ws://localhost:4050/mesh", "ws://127.0.0.1:4050/mesh", "ws://[::1]:4050/mesh", "wss://LOCALHOST/mesh"]) {
    assert.equal(isLoopbackEndpoint(loopback), true, loopback);
  }
  for (const remote of ["ws://192.0.2.10:4050/mesh", "wss://provider.example/mesh", "ws://127.0.0.2/mesh", "not a url"]) {
    assert.equal(isLoopbackEndpoint(remote), false, remote);
  }
});

test("base64 helpers round-trip across the 32 KiB chunk boundary", () => {
  const bytes = new Uint8Array(70_000).map((_, index) => index % 251);
  assert.deepEqual(base64ToBytes(bytesToBase64(bytes)), bytes);
});

test("the C# reference vectors verify through the shared module", async () => {
  const vectors = JSON.parse(readFileSync(VECTORS_PATH, "utf8")) as Vectors;
  const trust = { mode: "authenticated-remote" as const, odinRoots: [vectors.odinRoot], now: () => vectors.nowUnixMilliseconds };
  await verifyAuthorityRoute(vectors.route, trust);
  await assert.rejects(verifyAuthorityRoute({ ...vectors.route, priority: 8 }, trust), /signature is invalid/);
  await assert.rejects(
    verifyAuthorityRoute({ ...vectors.route, protocolIds: [...vectors.route.protocolIds!, "cultmesh.realtime.v1"] }, trust),
    /signature is invalid/,
  );

  const proof = vectors.sessionProof;
  assert.equal(proof.providerKeyId, vectors.route.certificate!.providerKey.keyId);
  assert.equal(
    await verifyProviderSessionProof(proof.request, vectors.route.endpoint, vectors.route.certificate!.providerKey, proof.providerSignature),
    true,
  );
  assert.equal(
    await verifyProviderSessionProof({ ...proof.request, clientNonce: "replayed" }, vectors.route.endpoint, vectors.route.certificate!.providerKey, proof.providerSignature),
    false,
  );
});

test("the shared module keeps the browser-safe surface: no node: import", () => {
  const source = readFileSync(join(__dirname, "..", "..", "src", "cultmesh-authority.ts"), "utf8");
  assert.doesNotMatch(source, /from "node:|require\("node:/);
});
