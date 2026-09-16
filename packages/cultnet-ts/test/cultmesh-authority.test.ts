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
  isCSharpWhiteSpace,
  isLoopbackEndpoint,
  isProtectedEndpoint,
  isUnsignedCertificate,
  trimCSharp,
  verifyAuthorityRoute,
  verifyP256,
  verifyProviderSessionProof,
  type CultMeshAuthorityRouteView,
  type CultMeshP256PublicKey,
} from "../src/cultmesh-authority";

const NOW = 1_800_000_000_000;
const CONTRACTS = join(__dirname, "..", "..", "..", "..", "contracts", "cultmesh");
// Written by the C# reference (`CultMeshAuthorityProofTests.AuthorityRouteVectorsAreSharedWithTypeScript`).
const VECTORS_PATH = join(CONTRACTS, "authority-route-vectors.json");
// Written by `scripts/sign-cultmesh-authority-vectors.mjs` and verified by the
// C# reference; checked here too so a transcript change on this side cannot
// leave a stale TypeScript-signed file behind for C# to trust.
const TS_SIGNED_VECTORS_PATH = join(CONTRACTS, "authority-route-vectors.ts-signed.json");

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
      /not IEEE P1363 P-256/,
    );
  }
  assert.equal(await verifyP256(odin.publicKey, canonicalRoute(route), "not base64!"), false);
});

// The C# `Verify` has three signature refusals with three texts. Collapsing any
// two of them loses a distinction the reference draws, so each is pinned by its
// exact C# string, not by a fragment.
test("the three signature refusals carry the C# reference's exact messages", async () => {
  const { route, odin } = signedRoute();
  const certificate = route.certificate!;
  const cases: readonly (readonly [string, string])[] = [
    ["not base64!", "The Odin route signature is not base64."],
    [bytesToBase64(base64ToBytes(certificate.signature).subarray(0, 63)), "The Odin route signature is not IEEE P1363 P-256."],
    [bytesToBase64(new Uint8Array(64)), "The Odin route certificate signature is invalid."],
  ];
  for (const [signature, message] of cases) {
    await assert.rejects(
      verifyAuthorityRoute({ ...route, certificate: { ...certificate, signature } }, remoteTrust(odin.publicKey)),
      (error: Error) => error.message === message,
      message,
    );
  }
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
    "cultmesh.content.v1\u001fcultmesh.documents.v1\u001fcultmesh.realtime.v1",
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

// Every raw-shape spelling Soul put to both sides on 2026-09-16, with the answer
// `IsProtected` gave through `CultMeshAuthorityTrustPolicy` as the expectation.
// The list is Soul's 71 verbatim, duplicate included.
const CSHARP_PROTECTED_ANSWERS: readonly (readonly [string, boolean])[] = [
  ["ws:x", false],
  ["wss:x", false],
  ["wss:/host", false],
  ["wss:///host", false],
  ["wss://host:8443\\", false],
  ["wss://host\\path", false],
  ["wss:\\\\host/m", true],
  ["wss://host./m", true],
  ["wss://\uff48\uff4f\uff53\uff54/m", true],
  ["QUIC://host/m", true],
  ["Quic+x://host/m", true],
  ["cultmesh-state+quic://h:4433/?c=1", true],
  ["wss://ho\tst/m", false],
  ["wss://host\t/m", false],
  ["\twss://host/m", true],
  [" wss://host/m", true],
  ["wss://host/m ", true],
  ["wss://host/m\n", true],
  ["wss://host:99999/m", false],
  ["wss://[::1%eth0]/m", true],
  ["wss://user:pw@host/m", true],
  ["https://host/m", true],
  ["HTTPS://host", true],
  ["ws://host/m", false],
  ["WS://host/m", false],
  ["http://host/m", false],
  ["quic:host", true],
  ["quic:", true],
  ["wss:", false],
  ["wss://", false],
  ["wss:///", false],
  ["quic+wss://host", true],
  ["x-quic://host", true],
  ["xquicx://host", true],
  ["wss ://host/m", false],
  ["w ss://host/m", false],
  ["wss://host:abc/m", false],
  ["wss://\uff11\uff12\uff17.0.0.1/m", true],
  ["wss://127.0.0.1./m", true],
  ["not a url", false],
  ["", false],
  ["   ", false],
  ["wss://host?x=1", true],
  ["wss://host#f", true],
  ["wss://[::1]/m", true],
  ["wss://[::1", false],
  ["wss://host/m\u0000", true],
  ["wss://host/\u0085m", true],
  ["\u0085wss://host/m", false],
  ["\ufeffwss://host/m", false],
  ["ws://127.0.0.1\\m", false],
  ["ws:127.0.0.1", false],
  ["ws:/127.0.0.1", false],
  ["ws:///127.0.0.1", false],
  ["ws://127.0.0.1./m", false],
  ["ws://\uff4c\uff4f\uff43\uff41\uff4c\uff48\uff4f\uff53\uff54/m", false],
  ["ws://localhost\t/m", false],
  ["wss://host:8443\\", false],
  ["wss:host", false],
  ["wss:host:8443", false],
  ["wss://host:8443/\\", true],
  ["wss://ho st/m", false],
  ["wss://host%20/m", false],
  ["wss://host\u00a0/m", true],
  ["wss://host\r\n/m", false],
  ["wss:// host/m", false],
  ["wss://host /m", false],
  ["quic://ho\tst", false],
  ["QUIC:\\\\host", true],
  ["ws://127.0.0.1:1/m\t", false],
  ["wss://xn--80ak6aa92e.com/m", true],
];

// The eleven of the 71 where the two sides still differ, every one of them the
// safe way round: C# calls the endpoint protected and TypeScript does not, so a
// route is refused rather than admitted. They are not one shape. Three carry a
// backslash (`wss:\\host/m`, `wss://host:8443/\`, `QUIC:\\host`) and two have no
// authority for `System.Uri` to accept (`quic:host`, `quic:`); those five
// `refusedByCSharpUriShape` refuses on the raw string, before any host exists to
// repair. Four are host repairs `URL` would make and C# would not — a trailing
// dot on `wss://host./m` and `wss://127.0.0.1./m`, a fullwidth host on the other
// two — which is the same reason that pre-check runs first in
// `isLoopbackEndpoint`. The last two throw in `URL` before any rule of ours
// runs: an IPv6 zone id and a non-breaking space in the host. None of the eleven
// is dialable as a browser CultMesh route. Pinned exactly, not merely excluded:
// if one of these starts answering true, or a twelfth spelling joins them, this
// fails.
const STRICTER_THAN_CSHARP: readonly string[] = [
  "wss:\\\\host/m",
  "wss://host./m",
  "wss://\uff48\uff4f\uff53\uff54/m",
  "wss://[::1%eth0]/m",
  "quic:host",
  "quic:",
  "wss://\uff11\uff12\uff17.0.0.1/m",
  "wss://127.0.0.1./m",
  "wss://host:8443/\\",
  "wss://host\u00a0/m",
  "QUIC:\\\\host",
];

test("isProtectedEndpoint is never more permissive than the C# IsProtected, over every raw-shape spelling Soul probed", () => {
  assert.equal(CSHARP_PROTECTED_ANSWERS.length, 71);
  const stricter = new Set(STRICTER_THAN_CSHARP);
  // The invariant that matters: TypeScript must never call an endpoint protected
  // that C# refuses, or the channel-protection gate opens on a route the
  // reference would have closed. `URL` repairs eleven spellings `System.Uri`
  // refuses outright, and every one of them used to land on the wrong side here.
  const morePermissive: string[] = [];
  const unexpectedlyStricter: string[] = [];
  for (const [endpoint, csharp] of CSHARP_PROTECTED_ANSWERS) {
    const ours = isProtectedEndpoint(endpoint);
    if (ours && !csharp) morePermissive.push(JSON.stringify(endpoint));
    if (!ours && csharp && !stricter.has(endpoint)) unexpectedlyStricter.push(JSON.stringify(endpoint));
    if (stricter.has(endpoint)) assert.equal(ours, false, `${JSON.stringify(endpoint)} is listed as stricter`);
  }
  assert.deepEqual(morePermissive, []);
  assert.deepEqual(unexpectedlyStricter, []);
});

// Every endpoint Soul put to both sides on 2026-09-16, with the answer
// `System.Uri.IsLoopback` gave through `CultMeshAuthorityTrustPolicy` as the
// expectation. Six of the probe's 88 are left out and named in the doc comment
// on `isLoopbackEndpoint` instead, because they are unreachable as browser
// CultMesh routes: the four IPv6 zone-id spellings, `file://localhost/mesh`
// and `mailto:localhost`.
const CSHARP_LOOPBACK_ANSWERS: readonly (readonly [string, boolean])[] = [
  ["ws://127.0.0.1:4050/mesh", true],
  ["ws://127.0.0.2:4050/mesh", true],
  ["ws://127.255.255.254:4050/mesh", true],
  ["ws://127.1:4050/mesh", true],
  ["ws://0x7f000001:4050/mesh", true],
  ["ws://2130706433:4050/mesh", true],
  ["ws://[::1]:4050/mesh", true],
  ["ws://[0:0:0:0:0:0:0:1]:4050/mesh", true],
  ["ws://[::ffff:127.0.0.1]:4050/mesh", true],
  ["ws://[::ffff:7f00:1]:4050/mesh", true],
  ["ws://[::ffff:127.0.0.2]:4050/mesh", false],
  ["ws://localhost:4050/mesh", true],
  ["ws://LOCALHOST:4050/mesh", true],
  ["ws://loopback:4050/mesh", true],
  ["ws://LoopBack:4050/mesh", true],
  ["ws://localhost.:4050/mesh", false],
  ["ws://localhost.localdomain:4050/mesh", false],
  ["ws://0.0.0.0:4050/mesh", false],
  ["ws://[::]:4050/mesh", false],
  ["ws://192.0.2.10:4050/mesh", false],
  ["ws://provider.example:4050/mesh", false],
  ["ws://127.0.0.1.:4050/mesh", false],
  ["ws://127.0.0.1.", false],
  ["ws://[::FFFF:127.0.0.1]:4050/mesh", true],
  ["ws://[::FFFF:7F00:1]:4050/mesh", true],
  ["ws://0177.0.0.1:4050/mesh", true],
  ["ws://0177.0.0.1", true],
  ["ws://127.0.0.1:0", true],
  ["ws://127.0.0.1:0/mesh", true],
  ["ws://LOCALHOST", true],
  ["ws://localhost:8443", true],
  ["ws://localhost:8443/", true],
  ["ws://localhost:8443/mesh/path", true],
  ["wss://[::1]/mesh", true],
  ["wss://[fe80::1]/mesh", false],
  ["wss://[::1]:8443", true],
  ["ws://[::FFFF:7F00:1]", true],
  ["ws://user:pw@127.0.0.1:4050/mesh", true],
  ["ws://user@localhost/mesh", true],
  ["ws://user:pw@provider.example/mesh", false],
  ["ws://127.0.0.1@provider.example/mesh", false],
  ["ws://provider.example@127.0.0.1/mesh", true],
  ["ws://127.00.0.1/mesh", true],
  ["ws://127.0.0.256/mesh", false],
  ["ws://127.0.1/mesh", true],
  ["ws://127/mesh", false],
  ["ws://0x7F.0.0.1/mesh", true],
  ["ws://127.0.0.01/mesh", true],
  ["ws://LOCALHOST./mesh", false],
  ["ws://[::0:1]/mesh", true],
  ["ws://[0::1]/mesh", true],
  ["ws://[::ffff:0:7f00:1]/mesh", false],
  ["ws://[64:ff9b::7f00:1]/mesh", false],
  ["ws://127.0.0.1%20/mesh", false],
  ["ws://localhost%20/mesh", false],
  ["ws://127.0.0.1 /mesh", false],
  ["ws://loopback./mesh", false],
  ["ws:127.0.0.1/mesh", false],
  ["ws:/127.0.0.1/mesh", false],
  ["ws:///127.0.0.1/mesh", false],
  ["WS://127.0.0.1/mesh", true],
  ["ws://127.0.0.1", true],
  ["ws://127.0.0.1/", true],
  ["ws://127.0.0.1?x=1", true],
  ["ws://127.0.0.1#f", true],
  ["ws://[::1]", true],
  ["ws://[::1]/", true],
  ["ws://localhost.localhost/mesh", false],
  ["ws://localhost/../mesh", true],
  ["ws://127.0.0.1\\mesh", false],
  ["ws://127.0.0.1/mesh\n", true],
  [" ws://127.0.0.1/mesh", true],
  ["ws://127.0.0.1/mesh ", true],
  ["ws://１２７.0.0.1/mesh", false],
  ["ws://ｌｏｃａｌｈｏｓｔ/mesh", false],
  ["ws://localhost\t/mesh", false],
  ["ws://[::ffff:127.0.0.1]", true],
  ["ws://[::ffff:7f00:0001]/mesh", true],
  ["ws://[0:0:0:0:0:ffff:7f00:1]/mesh", true],
  ["http://127.0.0.1/mesh", true],
  ["cultmesh-state+quic://127.0.0.1:4433/", true],
  ["ws://127.0.0.1:99999/mesh", false],
];

test("isLoopbackEndpoint answers as System.Uri.IsLoopback did, over every reachable endpoint Soul probed", () => {
  assert.equal(CSHARP_LOOPBACK_ANSWERS.length, 82);
  const disagreements: string[] = [];
  for (const [endpoint, csharp] of CSHARP_LOOPBACK_ANSWERS) {
    if (isLoopbackEndpoint(endpoint) !== csharp) disagreements.push(`${JSON.stringify(endpoint)} (C# said ${csharp})`);
  }
  assert.deepEqual(disagreements, []);
  assert.equal(isLoopbackEndpoint("not a url"), false);
});

// `char.IsWhiteSpace` as .NET 10 answered for each of these code points on
// 2026-09-16, through `CultMeshAuthorityTrustPolicy` over a signature of that
// one character. `String.prototype.trim` disagrees on two of them: U+0085,
// which .NET counts and JavaScript does not, and U+FEFF, which JavaScript
// counts and .NET does not.
const CSHARP_WHITESPACE_ANSWERS: readonly (readonly [number, boolean])[] = [
  [0x0000, false], [0x0009, true], [0x000a, true], [0x000b, true], [0x000c, true], [0x000d, true],
  [0x001c, false], [0x001d, false], [0x001e, false], [0x001f, false], [0x0020, true],
  [0x0085, true], [0x00a0, true], [0x1680, true], [0x180e, false],
  [0x2000, true], [0x2001, true], [0x2002, true], [0x2003, true], [0x2004, true], [0x2005, true],
  [0x2006, true], [0x2007, true], [0x2008, true], [0x2009, true], [0x200a, true],
  [0x200b, false], [0x200c, false], [0x2028, true], [0x2029, true],
  [0x202f, true], [0x205f, true], [0x3000, true], [0xfeff, false],
];

test("the whitespace set is C#'s char.IsWhiteSpace, not String.prototype.trim", async () => {
  assert.equal(CSHARP_WHITESPACE_ANSWERS.length, 34);
  const { route, odin } = signedRoute("ws://127.0.0.1:4050/mesh");
  for (const [code, csharp] of CSHARP_WHITESPACE_ANSWERS) {
    const character = String.fromCharCode(code);
    const label = `U+${code.toString(16).toUpperCase().padStart(4, "0")}`;
    assert.equal(isCSharpWhiteSpace(character), csharp, label);
    const padded = `${character}a${character}`;
    assert.equal(trimCSharp(padded), csharp ? "a" : padded, label);
    // The same answer where the rule is actually consumed: a certificate whose
    // whole signature is this one character is unsigned exactly when C# says so.
    const certificate = { ...route.certificate!, signature: character };
    assert.equal(isUnsignedCertificate(certificate), csharp, label);
    const blank = { ...route, certificate };
    const trust = { ...remoteTrust(odin.publicKey), mode: "local-development" as const };
    if (csharp) {
      await verifyAuthorityRoute(blank, trust);
    } else {
      await assert.rejects(verifyAuthorityRoute(blank, trust), /not base64|not IEEE P1363/, label);
    }
  }
  // The two code points where `trim` would have answered differently.
  assert.equal("\u0085".trim().length, 1);
  assert.equal(isCSharpWhiteSpace("\u0085"), true);
  assert.equal("\ufeff".trim().length, 0);
  assert.equal(isCSharpWhiteSpace("\ufeff"), false);
});

test("base64 helpers round-trip across the 32 KiB chunk boundary", () => {
  const bytes = new Uint8Array(70_000).map((_, index) => index % 251);
  assert.deepEqual(base64ToBytes(bytesToBase64(bytes)), bytes);
});

for (const [label, path] of [["C# reference", VECTORS_PATH], ["TypeScript-signed", TS_SIGNED_VECTORS_PATH]] as const) test(`the ${label} vectors verify through the shared module`, async () => {
  const vectors = JSON.parse(readFileSync(path, "utf8")) as Vectors;
  const trust = { mode: "authenticated-remote" as const, odinRoots: [vectors.odinRoot], now: () => vectors.nowUnixMilliseconds };
  await verifyAuthorityRoute(vectors.route, trust);
  await assert.rejects(verifyAuthorityRoute({ ...vectors.route, priority: 8 }, trust), /signature is invalid/);
  await assert.rejects(
    // An id the vector does not already list: one it does list would be a
    // duplicate, and the C# `Clean` drops duplicates before the transcript.
    verifyAuthorityRoute({ ...vectors.route, protocolIds: [...vectors.route.protocolIds!, "cultmesh.telemetry.v1"] }, trust),
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

// Soul ran each of these against the C# reference on 2026-09-16 by tampering
// with the committed vector and rerunning the C# test: C# accepted every one,
// because its constructors clean the field before `CanonicalRoute` sees it.
// TypeScript refused every one until it cleaned at the transcript too. The
// signature here is the C#-written one; nothing is re-signed.
test("padding and duplication the C# constructors clean still verify against the C#-written vector", async () => {
  const vectors = JSON.parse(readFileSync(VECTORS_PATH, "utf8")) as Vectors;
  const trust = { mode: "authenticated-remote" as const, odinRoots: [vectors.odinRoot], now: () => vectors.nowUnixMilliseconds };
  const route = vectors.route;
  const certificate = route.certificate!;
  const protocolIds = route.protocolIds!;
  const accepted: readonly (readonly [string, CultMeshAuthorityRouteView])[] = [
    ["a duplicated protocol id", { ...route, protocolIds: [...protocolIds, protocolIds[0]!] }],
    ["a padded protocol id", { ...route, protocolIds: [` ${protocolIds[0]!}\u0085`, ...protocolIds.slice(1)] }],
    ["a blank protocol id", { ...route, protocolIds: [...protocolIds, "  "] }],
    ["the protocol ids reversed", { ...route, protocolIds: [...protocolIds].reverse() }],
    ["a padded generation", { ...route, generation: ` ${route.generation}\u3000` }],
    ["a padded endpoint", { ...route, endpoint: ` ${route.endpoint} ` }],
    ["a padded Odin key id", { ...route, certificate: { ...certificate, odinKeyId: `\u00a0${certificate.odinKeyId} ` } }],
    ["a padded provider key id", {
      ...route,
      certificate: { ...certificate, providerKey: { ...certificate.providerKey, keyId: ` ${certificate.providerKey.keyId} ` } },
    }],
  ];
  for (const [label, candidate] of accepted) {
    await verifyAuthorityRoute(candidate, trust).catch((error: Error) => {
      assert.fail(`${label} should verify as it does in C#, but: ${error.message}`);
    });
  }
  // A padded Odin key id also has to find its root, as it does in the C#
  // dictionary, whose keys `Require` already trimmed.
  await verifyAuthorityRoute(
    { ...route, certificate: { ...certificate, odinKeyId: ` ${certificate.odinKeyId} ` } },
    { ...trust, odinRoots: [{ ...vectors.odinRoot, keyId: `\t${vectors.odinRoot.keyId}\t` }] },
  );

  // `verseId` is not a route field and C# does not clean it, so padding it
  // changes the bytes on both sides.
  await assert.rejects(verifyAuthorityRoute({ ...route, verseId: ` ${route.verseId} ` }, trust), /signature is invalid/);
  // A route with no protocol ids transcribes the empty string, as C#'s
  // `Array.Empty<string>()` does; there is no substituted default id.
  await assert.rejects(verifyAuthorityRoute({ ...route, protocolIds: undefined }, trust), /signature is invalid/);
  await assert.rejects(verifyAuthorityRoute({ ...route, protocolIds: [] }, trust), /signature is invalid/);
  assert.deepEqual(
    canonicalRoute({ ...route, protocolIds: undefined }),
    canonicalRoute({ ...route, protocolIds: ["", "   "] }),
  );
});

test("the shared module keeps the browser-safe surface: no node: import", () => {
  const source = readFileSync(join(__dirname, "..", "..", "src", "cultmesh-authority.ts"), "utf8");
  assert.doesNotMatch(source, /from "node:|require\("node:/);
});

// The transcript separator is U+001F, and it sat in these two files as a literal
// byte: invisible in every editor and diff, indistinguishable from the empty
// string in `join("")`. The bytes were right, which is why nothing caught it.
// Any control character in a string literal has the same problem, so none of
// them may be spelled literally here; a backslash-u escape is the only spelling.
for (const [label, path] of [
  ["the shared module", join(__dirname, "..", "..", "src", "cultmesh-authority.ts")],
  ["this test", join(__dirname, "..", "..", "test", "cultmesh-authority.test.ts")],
] as const) test(`${label} spells control characters as escapes, never as literal bytes`, () => {
  const bytes = readFileSync(path);
  const offenders: string[] = [];
  for (const [offset, byte] of bytes.entries()) {
    if (byte >= 0x20 || byte === 0x0a || byte === 0x0d) continue;
    const line = bytes.subarray(0, offset).toString("utf8").split("\n").length;
    offenders.push(`0x${byte.toString(16).padStart(2, "0")} at byte ${offset} (line ${line})`);
  }
  assert.deepEqual(offenders, []);
});
