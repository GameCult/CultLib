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

/**
 * `char.IsWhiteSpace` in .NET: Unicode Zs, plus Zl (U+2028), Zp (U+2029),
 * U+0009-U+000D and U+0085.
 *
 * `String.prototype.trim` is a different set and cannot stand in for it:
 * JavaScript counts U+FEFF, which .NET does not, and skips U+0085, which .NET
 * counts. U+200B and U+180E are whitespace to neither (U+180E left Zs in
 * Unicode 6.3). Wherever the C# reference writes `IsNullOrWhiteSpace` or
 * `Trim()`, this is the set it means, so this is the set the TypeScript rules
 * use.
 */
export function isCSharpWhiteSpace(character: string): boolean {
  const code = character.charCodeAt(0);
  return code === 0x20 || (code >= 0x09 && code <= 0x0d) || code === 0x85 || code === 0xa0 ||
    code === 0x1680 || (code >= 0x2000 && code <= 0x200a) ||
    code === 0x2028 || code === 0x2029 || code === 0x202f || code === 0x205f || code === 0x3000;
}

/** `string.Trim()` in .NET, over `isCSharpWhiteSpace`. */
export function trimCSharp(value: string): string {
  let start = 0;
  let end = value.length;
  while (start < end && isCSharpWhiteSpace(value[start]!)) start += 1;
  while (end > start && isCSharpWhiteSpace(value[end - 1]!)) end -= 1;
  return value.slice(start, end);
}

/** `string.IsNullOrWhiteSpace` in .NET, over `isCSharpWhiteSpace`. */
export function isNullOrWhiteSpaceCSharp(value: string | undefined | null): boolean {
  return value === undefined || value === null || trimCSharp(value).length === 0;
}

// A missing certificate, or one whose signature is empty or whitespace, is an
// unsigned route (`CultMeshRouteCertificate` trims the signature; `Verify`
// treats a whitespace signature as no certificate). The verifier and the
// browser's session-proof short-circuit share this one reading, as the C#
// `Verify` and `IsLocalDevelopment` do.
export function isUnsignedCertificate(certificate: CultMeshAuthorityRouteCertificate | undefined): boolean {
  return !certificate || isNullOrWhiteSpaceCSharp(certificate.signature);
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
  const signature = trimCSharp(certificate.signature);
  if (!isProtectedEndpoint(route.endpoint) && !(trust.mode === "local-development" && isLoopbackEndpoint(route.endpoint))) {
    throw new Error("Authenticated remote CultMesh routes require TLS or QUIC channel protection.");
  }
  const odinKeyId = certificateOdinKeyId(certificate);
  const root = roots.get(odinKeyId);
  if (!root) throw new Error(`Odin key '${odinKeyId}' is not trusted by this consumer.`);
  const now = trust.now?.() ?? Date.now();
  if (now < certificate.issuedAtUnixMilliseconds || now >= certificate.expiresAtUnixMilliseconds) {
    throw new Error("The Odin route certificate is not currently valid.");
  }
  // Three refusals, not one. `Verify` in the C# reference decodes the signature
  // itself and names each failure separately: `Convert.FromBase64String` throwing
  // is "not base64", a decoded length other than 64 is "not IEEE P1363 P-256",
  // and only a decoded-but-wrong signature is "invalid". `VerifySession` is the
  // silent path: it returns false for all three, which is why `verifyP256` keeps
  // its own checks.
  let signatureBytes: Uint8Array;
  try {
    signatureBytes = base64ToBytes(signature);
  } catch {
    throw new Error("The Odin route signature is not base64.");
  }
  if (signatureBytes.byteLength !== 64) {
    throw new Error("The Odin route signature is not IEEE P1363 P-256.");
  }
  if (!await verifyP256(root, canonicalRoute(route), signature)) {
    throw new Error("The Odin route certificate signature is invalid.");
  }
}

// The C# policy is a dictionary keyed by Odin key id and its constructor
// throws on a duplicate. A TypeScript policy is a plain object, so the same
// refusal happens at the first use of the policy, before any route is judged.
// The key is `CultMeshEcdsaP256PublicKey.KeyId`, which `Require` already
// trimmed, so two roots whose ids differ only in padding are one duplicate here
// too, and a padded certificate id still finds its root.
function trustedOdinRoots(trust: CultMeshAuthorityTrustPolicy): Map<string, CultMeshP256PublicKey> {
  const roots = new Map<string, CultMeshP256PublicKey>();
  for (const root of trust.odinRoots ?? []) {
    const keyId = requireNonEmptyCSharp(root.keyId, "keyId");
    if (roots.has(keyId)) throw new Error(`Odin root key id '${keyId}' is listed more than once in the trust policy.`);
    roots.set(keyId, root);
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

/**
 * `CultMeshAuthorityRoute.Clean`: trim each protocol id, drop the blank ones,
 * keep one of each ordinally, and sort ordinally. JavaScript's default sort is
 * UTF-16 code-unit order, which is what `StringComparer.Ordinal` compares.
 *
 * A route that lists no protocol ids at all is `Array.Empty<string>()` in C#
 * and transcribes as the empty string, so there is no substitute id here: the
 * TypeScript route view carries a separate `protocolId` for the handshake, and
 * reading it as a one-element list would transcribe bytes the reference never
 * writes.
 */
function cleanProtocolIds(values: readonly string[] | undefined): string[] {
  return [...new Set((values ?? []).filter(value => !isNullOrWhiteSpaceCSharp(value)).map(trimCSharp))].sort();
}

/** `RequireNonEmpty` and `Require` in the C# reference: one refusal, one trimmed value. */
function requireNonEmptyCSharp(value: string | undefined, parameterName: string): string {
  if (isNullOrWhiteSpaceCSharp(value)) throw new Error(`Value must be non-empty. (Parameter '${parameterName}')`);
  return trimCSharp(value!);
}

/** `CultMeshRouteCertificate`: the Odin key identity is required, and trimmed. */
function certificateOdinKeyId(certificate: CultMeshAuthorityRouteCertificate): string {
  if (isNullOrWhiteSpaceCSharp(certificate.odinKeyId)) {
    throw new Error("Odin key identity is required. (Parameter 'odinKeyId')");
  }
  return trimCSharp(certificate.odinKeyId);
}

/**
 * The transcript the C# reference signs is over a *constructed* route, and the
 * C# constructors clean every field on the way in. Cleaning at the transcript
 * is where TypeScript gets the same bytes: a duplicated protocol id, a padded
 * protocol id, a padded generation and a padded Odin key id all verify against
 * a C#-written signature, because C# never saw the padding either.
 *
 * `verseId` is the exception. It is not a route field: `CanonicalRoute` passes
 * the caller's string straight into `Canonical`, untrimmed, so padding it
 * changes the bytes on both sides.
 */
export function canonicalRoute(route: CultMeshAuthorityRouteView): Uint8Array {
  const certificate = route.certificate!;
  const authorityRuntimeId = requireNonEmptyCSharp(route.authorityRuntimeId, "authorityRuntimeId");
  const endpoint = requireNonEmptyCSharp(route.endpoint, "endpoint");
  return canonicalFields(
    "gamecult.cultmesh.route-certificate.v1",
    route.verseId,
    authorityRuntimeId,
    endpoint,
    cleanProtocolIds(route.protocolIds).join(""),
    String(route.priority ?? 0),
    // A blank generation is not blank in C#: the route constructor substitutes
    // the runtime id and endpoint, already cleaned, joined by "@".
    isNullOrWhiteSpaceCSharp(route.generation) ? `${authorityRuntimeId}@${endpoint}` : trimCSharp(route.generation),
    requireNonEmptyCSharp(certificate.providerKey.keyId, "keyId"),
    requireNonEmptyCSharp(certificate.providerKey.x, "x"),
    requireNonEmptyCSharp(certificate.providerKey.y, "y"),
    certificateOdinKeyId(certificate),
    String(certificate.issuedAtUnixMilliseconds),
    String(certificate.expiresAtUnixMilliseconds),
  );
}

/**
 * `CanonicalSession` cleans nothing on the request: it substitutes the empty
 * string for a null field and trims none of them, because the handshake message
 * is what the wire carried, not a constructed route. The endpoint is the only
 * cleaned field, because the one C# passes is `Route.Endpoint`, which the route
 * constructor already required non-empty and trimmed.
 */
export function canonicalSession(request: CultMeshSessionOpenMessage, endpoint: string): Uint8Array {
  return canonicalFields(
    "gamecult.cultmesh.session-proof.v1",
    request.clientNonce ?? "",
    request.messageId ?? "",
    request.sourceRuntimeId ?? "",
    request.verseId ?? "",
    request.authorityRuntimeId ?? "",
    request.protocolId ?? "",
    requireNonEmptyCSharp(endpoint, "endpoint"),
    request.routeGeneration ?? "",
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
 * `System.Uri` refuses spellings that WHATWG `URL` repairs, and a repaired
 * spelling would be loopback here and not in C#. This is that refusal, on the
 * raw string, before `URL` sees it: the scheme must be followed by `//` and a
 * non-empty authority, the string carries no backslash, and the host is ASCII,
 * free of whitespace, and does not end in a dot.
 *
 * Each clause answers one spelling Soul found `System.Uri` refusing and `URL`
 * accepting on 2026-09-16: `ws://127.0.0.1.` (the trailing dot, which `URL`
 * drops), `ws:127.0.0.1`, `ws:/127.0.0.1` and `ws:///127.0.0.1` (the missing or
 * empty authority, which `URL` infers), `ws://127.0.0.1\mesh` (the backslash,
 * which `URL` reads as a path separator), `ws://１２７.0.0.1` and
 * `ws://ｌｏｃａｌｈｏｓｔ` (fullwidth forms, which `URL` folds to ASCII), and
 * `ws://localhost\t/mesh` (the tab, which `URL` deletes).
 *
 * Leading and trailing C0 control characters and spaces around the whole
 * endpoint are stripped first, because `URL` strips them and `System.Uri`
 * accepts them: " ws://127.0.0.1/mesh" is loopback to both.
 */
function refusedByCSharpUriShape(value: string): boolean {
  const raw = value.replace(/^[\u0000-\u0020]+|[\u0000-\u0020]+$/g, "");
  if (raw.includes("\\")) return true;
  const shape = /^[A-Za-z][A-Za-z0-9+\-.]*:\/\/([^/?#]+)/.exec(raw);
  if (!shape) return true;
  const authority = shape[1]!;
  const afterUserInfo = authority.slice(authority.lastIndexOf("@") + 1);
  const host = afterUserInfo.startsWith("[")
    ? afterUserInfo.slice(0, afterUserInfo.indexOf("]") + 1)
    : afterUserInfo.split(":")[0]!;
  if (host.length === 0) return true;
  // eslint-disable-next-line no-control-regex
  return /[^\u0021-\u007e]/.test(host) || host.endsWith(".");
}

/**
 * The C# rule (`CultMeshAuthorityProof.cs`, `IsLoopback`): `System.Uri.IsLoopback`,
 * which is any `127.0.0.0/8` IPv4 host, `::1`, the IPv4-mapped `::ffff:127.0.0.1`
 * (and only `.1`), and the host names `localhost` and `loopback`, case-insensitively.
 * WHATWG `URL` already normalises `127.1`, `0x7f000001` and `2130706433` to
 * `127.0.0.1` and serialises the mapped form as `::ffff:7f00:1`, as `System.Uri`
 * does on its side. `localhost.` is not loopback on either side.
 *
 * This is not `System.Uri.IsLoopback`, and the spellings where it still differs
 * are named rather than claimed away. `URL` repairs seven spellings `System.Uri`
 * refuses, and `refusedByCSharpUriShape` refuses those first. Two divergences
 * remain, both unreachable from a browser CultMesh route:
 *
 * - An IPv6 zone id (`ws://[::1%eth0]`, `%25`-escaped or not) is loopback to
 *   `System.Uri` and throws in `URL`, so this answers false. A zone id is a
 *   host-local interface name; it cannot be dialled from a browser socket.
 * - `file://localhost/mesh` and `mailto:localhost` are loopback to `System.Uri`
 *   and have no host in `URL`, so this answers false. Neither is a CultMesh
 *   route: `openSocket` refuses any scheme but `ws:` and `wss:`.
 */
export function isLoopbackEndpoint(value: string): boolean {
  if (refusedByCSharpUriShape(value)) return false;
  try {
    const host = new URL(value).hostname.replace(/^\[|\]$/g, "").toLowerCase();
    return host === "localhost" || host === "loopback" || host === "::1" || host === "::ffff:7f00:1" ||
      /^127\.\d{1,3}\.\d{1,3}\.\d{1,3}$/.test(host);
  } catch {
    return false;
  }
}

/**
 * The C# rule (`CultMeshAuthorityProof.cs`, `IsProtected`): wss, https, or any
 * scheme containing "quic", over a string `System.Uri` parses at all.
 *
 * `refusedByCSharpUriShape` runs first here for the same reason it runs first in
 * `isLoopbackEndpoint`, and it matters more: `URL` repairs eleven spellings
 * `System.Uri` refuses outright, and every one of them would be *protected* here
 * and unprotected in C#. `wss:x`, `wss:host`, `wss:host:8443`, `wss:/host` and
 * `wss:///host` have no authority for `System.Uri` to accept; `wss://host\path`
 * and `wss://host:8443\` carry a backslash; `wss://ho\tst/m`, `wss://host\t/m`,
 * `quic://ho\tst` carry a tab and `wss://host\r\n/m` a CR/LF, which `URL`
 * deletes. Letting any of them answer true would clear the channel-protection
 * gate on a route C# refuses.
 *
 * Two spellings of Soul's 71 still disagree, both the safe way round: C# calls
 * them protected and this answers false, so a route is refused rather than
 * admitted. `wss://[::1%eth0]/m` (the IPv6 zone id) and `wss://host /m`
 * (a non-breaking space in the host) both throw in `URL`, so they answered
 * false before this pre-check existed too. Neither is dialable from a browser.
 */
export function isProtectedEndpoint(value: string): boolean {
  if (refusedByCSharpUriShape(value)) return false;
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
