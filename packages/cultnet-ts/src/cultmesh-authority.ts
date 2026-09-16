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

export async function verifyAuthorityRoute(
  route: CultMeshAuthorityRouteView,
  trust: CultMeshAuthorityTrustPolicy,
): Promise<void> {
  const certificate = route.certificate;
  if (!certificate) {
    if (trust.mode === "local-development" && isLoopbackEndpoint(route.endpoint)) return;
    throw new Error("Remote CultMesh routes require an Odin-signed authority certificate.");
  }
  if (!isProtectedEndpoint(route.endpoint) && !(trust.mode === "local-development" && isLoopbackEndpoint(route.endpoint))) {
    throw new Error("Authenticated remote CultMesh routes require TLS or QUIC channel protection.");
  }
  const now = trust.now?.() ?? Date.now();
  if (now < certificate.issuedAtUnixMilliseconds || now >= certificate.expiresAtUnixMilliseconds) {
    throw new Error("The Odin route certificate is not currently valid.");
  }
  const root = trust.odinRoots?.find(candidate => candidate.keyId === certificate.odinKeyId);
  if (!root) throw new Error(`Odin key '${certificate.odinKeyId}' is not trusted by this consumer.`);
  if (!await verifyP256(root, canonicalRoute(route), certificate.signature)) {
    throw new Error("The Odin route certificate signature is invalid.");
  }
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
      raw.slice().buffer as ArrayBuffer,
      { name: "ECDSA", namedCurve: "P-256" },
      false,
      ["verify"],
    );
    return await crypto.subtle.verify(
      { name: "ECDSA", hash: "SHA-256" },
      publicKey,
      signature.slice().buffer as ArrayBuffer,
      payload.slice().buffer as ArrayBuffer,
    );
  } catch {
    return false;
  }
}

export function isLoopbackEndpoint(value: string): boolean {
  try {
    const host = new URL(value).hostname.replace(/^\[|\]$/g, "").toLowerCase();
    return host === "localhost" || host === "127.0.0.1" || host === "::1";
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
