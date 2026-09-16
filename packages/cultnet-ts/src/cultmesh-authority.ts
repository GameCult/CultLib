// The one TypeScript implementation of CultMesh route verification.
//
// Browser-safe by construction: only `crypto.subtle`, `TextEncoder`, `atob`,
// `btoa`, `URL`, `DataView` and `Uint8Array`. No `node:` import. The browser
// WebSocket client and the Node realtime consumer verify the same route bytes
// through the same functions; the C# reference is
// `src/GameCult.Mesh/CultMeshAuthorityProof.cs`.

import type { CultMeshSessionOpenMessage } from "./contracts";

export interface CultMeshAuthorityIdentity {
  verseId: string;
  authorityRuntimeId: string;
}

export interface CultMeshAuthorityRouteView extends CultMeshAuthorityIdentity {
  endpoint: string;
  protocolId?: string;
  protocolIds?: readonly string[];
  priority?: number;
  generation: string;
  certificate?: CultMeshAuthorityRouteCertificate;
}

export interface CultMeshP256PublicKey {
  keyId: string;
  x: string;
  y: string;
}

export interface CultMeshAuthorityRouteCertificate {
  providerKey: CultMeshP256PublicKey;
  odinKeyId: string;
  issuedAtUnixMilliseconds: number;
  expiresAtUnixMilliseconds: number;
  signature: string;
}

export type CultMeshAuthorityTrustMode = "authenticated-remote" | "local-development";

export interface CultMeshAuthorityTrustPolicy {
  mode: CultMeshAuthorityTrustMode;
  odinRoots?: readonly CultMeshP256PublicKey[];
  now?: () => number;
}

// A missing certificate, or one whose signature is empty or whitespace, is an
// unsigned route (`CultMeshRouteCertificate` trims the signature; `Verify`
// treats a whitespace signature as no certificate). The verifier and the
// browser's session-proof short-circuit share this one reading, as the C#
// `Verify` and `IsLocalDevelopment` do.
export function isUnsignedCertificate(certificate: CultMeshAuthorityRouteCertificate | undefined): boolean {
  return !certificate || certificate.signature.trim() === "";
}

// Rule for rule, this is `CultMeshAuthorityTrustPolicy.Verify` in the C#
// reference, in the same order: unsigned, channel protection, root lookup,
// validity window, signature. The same input yields the same refusal on both
// sides.
export async function verifyAuthorityRoute(
  route: CultMeshAuthorityRouteView,
  trust: CultMeshAuthorityTrustPolicy,
): Promise<void> {
  const roots = trustedOdinRoots(trust);
  const certificate = route.certificate;
  if (!certificate || isUnsignedCertificate(certificate)) {
    if (trust.mode === "local-development" && isLoopbackEndpoint(route.endpoint)) return;
    throw new Error("Remote CultMesh routes require an Odin-signed authority certificate.");
  }
  const signature = certificate.signature.trim();
  if (!isProtectedEndpoint(route.endpoint) && !(trust.mode === "local-development" && isLoopbackEndpoint(route.endpoint))) {
    throw new Error("Authenticated remote CultMesh routes require TLS or QUIC channel protection.");
  }
  const root = roots.get(certificate.odinKeyId);
  if (!root) throw new Error(`Odin key '${certificate.odinKeyId}' is not trusted by this consumer.`);
  const now = trust.now?.() ?? Date.now();
  if (now < certificate.issuedAtUnixMilliseconds || now >= certificate.expiresAtUnixMilliseconds) {
    throw new Error("The Odin route certificate is not currently valid.");
  }
  if (!await verifyP256(root, canonicalRoute(route), signature)) {
    throw new Error("The Odin route certificate signature is invalid.");
  }
}

// The C# policy is a dictionary keyed by Odin key id and its constructor
// throws on a duplicate. A TypeScript policy is a plain object, so the same
// refusal happens at the first use of the policy, before any route is judged.
function trustedOdinRoots(trust: CultMeshAuthorityTrustPolicy): Map<string, CultMeshP256PublicKey> {
  const roots = new Map<string, CultMeshP256PublicKey>();
  for (const root of trust.odinRoots ?? []) {
    if (roots.has(root.keyId)) throw new Error(`Odin root key id '${root.keyId}' is listed more than once in the trust policy.`);
    roots.set(root.keyId, root);
  }
  return roots;
}

/** Verifies the provider's session proof over the accepted handshake. */
export function verifyProviderSessionProof(
  request: CultMeshSessionOpenMessage,
  endpoint: string,
  providerKey: CultMeshP256PublicKey,
  signatureBase64: string,
): Promise<boolean> {
  return verifyP256(providerKey, canonicalSession(request, endpoint), signatureBase64);
}

export function canonicalRoute(route: CultMeshAuthorityRouteView): Uint8Array {
  const certificate = route.certificate!;
  return canonicalFields(
    "gamecult.cultmesh.route-certificate.v1",
    route.verseId,
    route.authorityRuntimeId,
    route.endpoint,
    [...(route.protocolIds ?? [route.protocolId ?? "cultmesh.documents.v1"])].sort().join(""),
    String(route.priority ?? 0),
    route.generation,
    certificate.providerKey.keyId,
    certificate.providerKey.x,
    certificate.providerKey.y,
    certificate.odinKeyId,
    String(certificate.issuedAtUnixMilliseconds),
    String(certificate.expiresAtUnixMilliseconds),
  );
}

export function canonicalSession(request: CultMeshSessionOpenMessage, endpoint: string): Uint8Array {
  return canonicalFields(
    "gamecult.cultmesh.session-proof.v1",
    request.clientNonce,
    request.messageId,
    request.sourceRuntimeId,
    request.verseId,
    request.authorityRuntimeId,
    request.protocolId,
    endpoint,
    request.routeGeneration,
  );
}

export function canonicalFields(...values: string[]): Uint8Array {
  const encoder = new TextEncoder();
  const encoded = values.map(value => encoder.encode(value));
  const total = encoded.reduce((sum, value) => sum + 4 + value.byteLength, 0);
  const result = new Uint8Array(total);
  const view = new DataView(result.buffer);
  let offset = 0;
  for (const value of encoded) {
    view.setUint32(offset, value.byteLength, false);
    offset += 4;
    result.set(value, offset);
    offset += value.byteLength;
  }
  return result;
}

export async function verifyP256(
  key: CultMeshP256PublicKey,
  payload: Uint8Array,
  signatureBase64: string,
): Promise<boolean> {
  try {
    const x = base64ToBytes(key.x);
    const y = base64ToBytes(key.y);
    const signature = base64ToBytes(signatureBase64);
    if (x.byteLength !== 32 || y.byteLength !== 32 || signature.byteLength !== 64) return false;
    const raw = new Uint8Array(65);
    raw[0] = 4;
    raw.set(x, 1);
    raw.set(y, 33);
    const publicKey = await crypto.subtle.importKey(
      "raw",
      ownedBytes(raw),
      { name: "ECDSA", namedCurve: "P-256" },
      false,
      ["verify"],
    );
    return await crypto.subtle.verify(
      { name: "ECDSA", hash: "SHA-256" },
      publicKey,
      ownedBytes(signature),
      ownedBytes(payload),
    );
  } catch {
    return false;
  }
}

// WebCrypto reads an `ArrayBuffer` whole. A view's `.buffer` is not the view:
// a Node `Buffer` (a subarray of a shared pool) or any offset `Uint8Array`
// would hand WebCrypto the pool, and `Buffer.prototype.slice` does not copy.
// Copy exactly the viewed bytes so every view verifies alike.
function ownedBytes(view: Uint8Array): ArrayBuffer {
  return view.buffer.slice(view.byteOffset, view.byteOffset + view.byteLength) as ArrayBuffer;
}

/**
 * The C# rule (`CultMeshAuthorityProof.cs`, `IsLoopback`): `System.Uri.IsLoopback`,
 * which is any `127.0.0.0/8` IPv4 host, `::1`, the IPv4-mapped `::ffff:127.0.0.1`
 * (and only `.1`), and the host names `localhost` and `loopback`, case-insensitively.
 * WHATWG `URL` already normalises `127.1`, `0x7f000001` and `2130706433` to
 * `127.0.0.1` and serialises the mapped form as `::ffff:7f00:1`, as `System.Uri`
 * does on its side. `localhost.` is not loopback on either side.
 */
export function isLoopbackEndpoint(value: string): boolean {
  try {
    const host = new URL(value).hostname.replace(/^\[|\]$/g, "").toLowerCase();
    return host === "localhost" || host === "loopback" || host === "::1" || host === "::ffff:7f00:1" ||
      /^127\.\d{1,3}\.\d{1,3}\.\d{1,3}$/.test(host);
  } catch {
    return false;
  }
}

/** The C# rule (`CultMeshAuthorityProof.cs`, `IsProtected`): wss, https, or any scheme containing "quic". */
export function isProtectedEndpoint(value: string): boolean {
  let scheme: string;
  try {
    scheme = new URL(value).protocol.slice(0, -1).toLowerCase();
  } catch {
    return false;
  }
  return scheme === "wss" || scheme === "https" || scheme.includes("quic");
}

export function bytesToBase64(bytes: Uint8Array): string {
  let binary = "";
  for (let offset = 0; offset < bytes.length; offset += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
  }
  return btoa(binary);
}

export function base64ToBytes(value: string): Uint8Array {
  const binary = atob(value);
  return Uint8Array.from(binary, character => character.charCodeAt(0));
}
