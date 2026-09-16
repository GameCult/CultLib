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
  canonicalFields,
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

test("a certificate whose signature is empty or whitespace is an unsigned route, as in C#", async () => {
  for (const signature of ["", "   ", "\n\t "]) {
    const remote = signedRoute();
    const remoteBlank = { ...remote.route, certificate: { ...remote.route.certificate!, signature } };
    await assert.rejects(verifyAuthorityRoute(remoteBlank, remoteTrust(remote.odin.publicKey)), /Odin-signed authority certificate/);
    await assert.rejects(
      verifyAuthorityRoute(remoteBlank, { ...remoteTrust(remote.odin.publicKey), mode: "local-development" }),
      /Odin-signed authority certificate/,
    );
    const loopback = signedRoute("ws://127.0.0.1:4050/mesh");
    const loopbackBlank = { ...loopback.route, certificate: { ...loopback.route.certificate!, signature } };
    await verifyAuthorityRoute(loopbackBlank, { ...remoteTrust(loopback.odin.publicKey), mode: "local-development" });
    await assert.rejects(verifyAuthorityRoute(loopbackBlank, remoteTrust(loopback.odin.publicKey)), /Odin-signed authority certificate/);
  }
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

test("the root lookup precedes the validity window, so an expired route under an unknown root is refused as untrusted (C# order)", async () => {
  const { route, odin } = signedRoute();
  const expired = route.certificate!.expiresAtUnixMilliseconds + 1;
  await assert.rejects(verifyAuthorityRoute(route, remoteTrust(odin.publicKey, expired)), /not currently valid/);
  await assert.rejects(
    verifyAuthorityRoute(route, { mode: "authenticated-remote", odinRoots: [{ ...odin.publicKey, keyId: "other-root" }], now: () => expired }),
    /is not trusted/,
  );
});

test("a trust policy listing one Odin key id twice is refused before any route is judged, as the C# constructor throws", async () => {
  const { route, odin } = signedRoute();
  const twin = { ...keyPair("odin-root-1").publicKey };
  for (const odinRoots of [[odin.publicKey, twin], [twin, odin.publicKey], [odin.publicKey, odin.publicKey]]) {
    await assert.rejects(
      verifyAuthorityRoute(route, { mode: "authenticated-remote", odinRoots, now: () => NOW }),
      /'odin-root-1' is listed more than once/,
    );
  }
  await assert.rejects(
    verifyAuthorityRoute({ ...route, endpoint: "ws://127.0.0.1:4050/mesh", certificate: undefined }, { mode: "local-development", odinRoots: [twin, twin] }),
    /listed more than once/,
  );
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

test("verifyP256 reads exactly the viewed bytes: a Uint8Array, a pooled Buffer and a subarray of a large allocation verify alike", async () => {
  const { route, odin } = signedRoute();
  const payload = canonicalRoute(route);
  const signature = route.certificate!.signature;
  const pooled = Buffer.from(payload);
  const large = Buffer.alloc(16 * 1024, 0xa5);
  const subarray = large.subarray(1000, 1000 + payload.byteLength);
  subarray.set(payload);
  const offsetView = new Uint8Array(new ArrayBuffer(payload.byteLength + 64), 32, payload.byteLength);
  offsetView.set(payload);
  for (const view of [payload, pooled, subarray, offsetView]) {
    assert.equal(await verifyP256(odin.publicKey, view, signature), true, `${view.constructor.name} byteOffset=${view.byteOffset}`);
  }
  const signatureBytes = base64ToBytes(signature);
  const sigLarge = Buffer.alloc(16 * 1024, 0x5a);
  sigLarge.set(signatureBytes, 4000);
  const sigView = sigLarge.subarray(4000, 4064);
  assert.equal(await verifyP256(odin.publicKey, payload, bytesToBase64(sigView)), true);
  assert.equal(await verifyP256(odin.publicKey, Buffer.from(payload.subarray(1)), signature), false);
});

test("protocol ids are sorted ordinally into the transcript, whatever order the route lists them in", () => {
  const { route } = signedRoute();
  const unsorted = { ...route, protocolIds: ["cultmesh.realtime.v1", "cultmesh.content.v1", "cultmesh.documents.v1"] };
  const certificate = route.certificate!;
  const expected = canonicalFields(
    "gamecult.cultmesh.route-certificate.v1",
    route.verseId,
    route.authorityRuntimeId,
    route.endpoint,
    "cultmesh.content.v1cultmesh.documents.v1cultmesh.realtime.v1",
    "0",
    route.generation,
    certificate.providerKey.keyId,
    certificate.providerKey.x,
    certificate.providerKey.y,
    certificate.odinKeyId,
    String(certificate.issuedAtUnixMilliseconds),
    String(certificate.expiresAtUnixMilliseconds),
  );
  assert.deepEqual(canonicalRoute(unsorted), expected);
  assert.deepEqual(canonicalRoute({ ...unsorted, protocolIds: [...unsorted.protocolIds!].reverse() }), expected);
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

// The accepted and refused hosts here are what `System.Uri.IsLoopback` answered
// through `CultMeshAuthorityTrustPolicy` on 2026-09-16; keep them in step.
test("isLoopbackEndpoint is the C# System.Uri.IsLoopback set", () => {
  for (const loopback of [
    "ws://localhost:4050/mesh", "wss://LOCALHOST/mesh", "ws://loopback:4050/mesh", "ws://LoopBack:4050/mesh",
    "ws://127.0.0.1:4050/mesh", "ws://127.0.0.2/mesh", "ws://127.255.255.254/mesh", "ws://127.1/mesh", "ws://0x7f000001/mesh", "ws://2130706433/mesh",
    "ws://[::1]:4050/mesh", "ws://[0:0:0:0:0:0:0:1]:4050/mesh", "ws://[::ffff:127.0.0.1]/mesh", "ws://[::ffff:7f00:1]/mesh",
  ]) {
    assert.equal(isLoopbackEndpoint(loopback), true, loopback);
  }
  for (const remote of [
    "ws://192.0.2.10:4050/mesh", "wss://provider.example/mesh", "ws://[::ffff:127.0.0.2]/mesh", "ws://localhost./mesh",
    "ws://localhost.localdomain/mesh", "ws://127.example/mesh", "ws://0.0.0.0/mesh", "ws://[::]/mesh", "not a url",
  ]) {
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
