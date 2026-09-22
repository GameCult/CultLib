// Mutation check for the CultMesh rules that are not proven by reading: the
// route verifier, the realtime frame codec, and the native bridge's runtime
// lifetime.
//
// Two files own the route rules and each is one target here. `shared` is
// `packages/cultnet-ts/src/cultmesh-authority.ts`, the one verifier, killed by
// `npm run test --workspace packages/cultnet-ts`. `browser` is
// `packages/cultmesh-browser/src/index.ts`, which must *call* the shared rules
// rather than re-read them locally, killed by `npm run test --workspace
// packages/cultmesh-browser`. A browser mutation cannot be killed by the
// cultnet-ts tests and a shared one is not the browser's to pin, so the killer
// is the target's own workspace.
//
// Each rule the cut map names gets one mutation. A survivor means no test pins
// that rule. Each file is rewritten as UTF-8 bytes and restored from the
// original bytes with a digest check, never from git. A no-op control runs
// first per target through the same write path so a false kill from the write
// itself cannot hide.
//
// The original bytes also live in `<file>.mutation-original` from the first
// write until a verified restore; a run killed mid-mutation is repaired from
// that sidecar by the next run, not by git. After the final restore each
// package is rebuilt so `dist/` never carries the last mutant.
//
// `realtime` is `packages/cultmesh-ts/src/realtime-wire.ts`, the frame codec,
// killed by `npm run test --workspace packages/cultmesh-ts`.
//
// `native` is `native/GameCult.Mesh.Quic.Native/cultmesh_quic_native.cpp`, and
// its killer is the scenario runner in that project's `tests/`, built and run
// through CMake rather than npm. It has no `dist/`, so it has no sentinel and no
// post-run rebuild check; the verified restore covers it.
//
// A mutation is only killed on a target that can see it. Some of the native
// rules are visible only under a sanitizer, and there is no ThreadSanitizer for
// MSVC; a run on the wrong platform would otherwise report "killed" for a
// mutation it never actually exercised. Every native entry therefore names the
// platforms it is honest on, and a run elsewhere reports it as `skipped`, which
// is not coverage. Soul's Cut 3 pass found this the hard way: `B1` and `L2` were
// reported killed from a win32-x64 release run where both in fact survive, and
// die deterministically only under linux-x64 AddressSanitizer.
//
// This script tests the platform it is running on, and a run that skipped
// entries exits 2 rather than 0: those rules were covered by nothing. For the
// linux-x64 native entries, run it inside the committed development image,
// `scripts/quic-native-linux-dev.Dockerfile`, which is the same debian:13 digest
// the bridge is built in and carries Node, ninja, setarch and MsQuic's runtime
// dependencies:
//
//   docker build -t cultlib-quic-native-dev -f scripts/quic-native-linux-dev.Dockerfile scripts
//   docker run --rm --security-opt seccomp=unconfined -v "${PWD}:/src" -w /src cultlib-quic-native-dev bash -lc "node scripts/mutate-cultmesh.mjs native"
//
// Both from the repository root; the mount is that root wherever it is, as
// `${PWD}` in PowerShell or `$(pwd)` in a POSIX shell. The second is one line
// because a continuation would have to pick one of those shells.
//
// The win32-x64 half wants the checkout somewhere short, near a drive root. MSVC
// builds the bridge through MSBuild, whose file tracker gives out on long paths,
// and a run from a deep temporary directory fails its own no-op control.
//
// The seccomp flag is required, not cautious: the ThreadSanitizer configuration
// is re-executed under `setarch -R`, and Docker's default profile denies the
// personality call that needs. See `explainUnrunnable` below, and the bridge's
// README.
//
//   node scripts/mutate-cultmesh.mjs [target...]

import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, rmSync, unlinkSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const npmCli = process.env.npm_execpath ??
  join(dirname(process.execPath), "node_modules", "npm", "bin", "npm-cli.js");

// The platform this run can speak for. It is the target the native entries are
// matched against, and it is named in the output so a report cannot be read as
// covering a platform it never touched.
const platform = process.platform === "win32" ? "win32-x64" : "linux-x64";

// Each target's `sentinel` is present in its restored source and compiled
// verbatim into its dist/; that target's last mutation removes it, which is
// what makes the post-rebuild check distinguish a rebuilt dist/ from one still
// carrying that mutant.
const targets = {
  shared: {
    workspace: "packages/cultnet-ts",
    file: join(repoRoot, "packages", "cultnet-ts", "src", "cultmesh-authority.ts"),
    dist: join(repoRoot, "packages", "cultnet-ts", "dist", "cultmesh-authority.js"),
    sentinel: 'scheme === "https"',
  },
  browser: {
    workspace: "packages/cultmesh-browser",
    file: join(repoRoot, "packages", "cultmesh-browser", "src", "index.ts"),
    dist: join(repoRoot, "packages", "cultmesh-browser", "dist", "index.js"),
    sentinel: "isUnsignedCertificate(route.certificate)",
  },
  realtime: {
    workspace: "packages/cultmesh-ts",
    file: join(repoRoot, "packages", "cultmesh-ts", "src", "realtime-wire.ts"),
    dist: join(repoRoot, "packages", "cultmesh-ts", "dist", "realtime-wire.js"),
    sentinel: "0x31545343",
  },
  native: {
    file: join(repoRoot, "native", "GameCult.Mesh.Quic.Native", "cultmesh_quic_native.cpp"),
    check: () => nativeScenariosPass(),
  },
};

const mutations = [
  {
    rule: "unsigned remote route is refused",
    old: 'throw new Error("Remote CultMesh routes require an Odin-signed authority certificate.");',
    new: "return;",
  },
  {
    rule: "unsigned loopback is accepted only under local-development",
    old: 'if (trust.mode === "local-development" && isLoopbackEndpoint(route.endpoint)) return;',
    new: "if (isLoopbackEndpoint(route.endpoint)) return;",
  },
  {
    rule: "protected-scheme gate",
    old: "if (!isProtectedEndpoint(route.endpoint) && !(",
    new: "if (isProtectedEndpoint(route.endpoint) && !(",
  },
  {
    rule: "validity window (expiresAt exclusive)",
    old: "now >= certificate.expiresAtUnixMilliseconds",
    new: "now > certificate.expiresAtUnixMilliseconds",
  },
  {
    rule: "Odin root lookup by key id",
    old: "const root = roots.get(odinKeyId);",
    new: "const root = [...roots.values()][0];",
  },
  {
    rule: "isUnsignedCertificate treats a whitespace signature as unsigned",
    old: "return !certificate || isNullOrWhiteSpaceCSharp(certificate.signature);",
    new: 'return !certificate || certificate.signature === "";',
  },
  {
    rule: "isUnsignedCertificate treats an empty signature as unsigned",
    old: "return !certificate || isNullOrWhiteSpaceCSharp(certificate.signature);",
    new: "return !certificate;",
  },
  {
    rule: "the whitespace set is C#'s char.IsWhiteSpace, not String.prototype.trim",
    old: "return !certificate || isNullOrWhiteSpaceCSharp(certificate.signature);",
    new: 'return !certificate || certificate.signature.trim() === "";',
  },
  {
    rule: "U+0085 is whitespace to C# (String.prototype.trim disagrees)",
    old: "code === 0x85 || ",
    new: "",
  },
  {
    rule: "the two non-verifying signature refusals keep their own C# messages",
    old: '    throw new Error("The Odin route signature is not base64.");\n' +
      "  }\n" +
      "  if (signatureBytes.byteLength !== 64) {\n" +
      '    throw new Error("The Odin route signature is not IEEE P1363 P-256.");\n',
    new: '    throw new Error("The Odin route certificate signature is invalid.");\n' +
      "  }\n" +
      "  if (signatureBytes.byteLength !== 64) {\n" +
      '    throw new Error("The Odin route certificate signature is invalid.");\n',
  },
  {
    rule: "root lookup precedes the validity window",
    old: "  const odinKeyId = certificateOdinKeyId(certificate);\n" +
      "  const root = roots.get(odinKeyId);\n" +
      "  if (!root) throw new Error(`Odin key '${odinKeyId}' is not trusted by this consumer.`);\n" +
      "  const now = trust.now?.() ?? Date.now();\n" +
      "  if (now < certificate.issuedAtUnixMilliseconds || now >= certificate.expiresAtUnixMilliseconds) {\n" +
      '    throw new Error("The Odin route certificate is not currently valid.");\n' +
      "  }\n",
    new: "  const now = trust.now?.() ?? Date.now();\n" +
      "  if (now < certificate.issuedAtUnixMilliseconds || now >= certificate.expiresAtUnixMilliseconds) {\n" +
      '    throw new Error("The Odin route certificate is not currently valid.");\n' +
      "  }\n" +
      "  const odinKeyId = certificateOdinKeyId(certificate);\n" +
      "  const root = roots.get(odinKeyId);\n" +
      "  if (!root) throw new Error(`Odin key '${odinKeyId}' is not trusted by this consumer.`);\n",
  },
  {
    rule: "duplicate Odin root key ids refuse the policy",
    old: "    if (roots.has(keyId)) throw new Error(`Odin root key id '${keyId}' is listed more than once in the trust policy.`);\n",
    new: "",
  },
  {
    rule: "protocol ids are sorted into the transcript",
    old: ")].sort();",
    new: ")];",
  },
  {
    rule: "verifyP256 copies the viewed payload bytes, not the pool behind a Buffer",
    old: "      ownedBytes(payload),",
    new: "      payload.slice().buffer as ArrayBuffer,",
  },
  {
    rule: "route transcript field order",
    old: "    route.verseId,\n    authorityRuntimeId,\n",
    new: "    authorityRuntimeId,\n    route.verseId,\n",
  },
  {
    rule: "session transcript binds the nonce",
    old: '    request.clientNonce ?? "",\n',
    new: "",
  },
  {
    rule: "protocol ids are deduplicated into the transcript",
    old: "  return [...new Set(",
    new: "  return [...(",
  },
  {
    rule: "the generation is trimmed into the transcript",
    old: ": trimCSharp(route.generation),",
    new: ": route.generation,",
  },
  {
    rule: "the provider key id is trimmed into the transcript",
    old: '    requireNonEmptyCSharp(certificate.providerKey.keyId, "keyId"),',
    new: "    certificate.providerKey.keyId,",
  },
  {
    rule: "the Odin key id is trimmed before the root lookup",
    old: "  const odinKeyId = certificateOdinKeyId(certificate);",
    new: "  const odinKeyId = certificate.odinKeyId;",
  },
  {
    rule: "blank protocol ids are dropped from the transcript",
    old: "(values ?? []).filter(value => !isNullOrWhiteSpaceCSharp(value)).map(trimCSharp)",
    new: "(values ?? []).map(trimCSharp)",
  },
  {
    rule: "length-prefix framing is big-endian",
    old: "view.setUint32(offset, value.byteLength, false);",
    new: "view.setUint32(offset, value.byteLength, true);",
  },
  {
    rule: "P-256 verification hashes with SHA-256",
    old: '{ name: "ECDSA", hash: "SHA-256" },',
    new: '{ name: "ECDSA", hash: "SHA-384" },',
  },
  // Not listed: removing `signature.byteLength !== 64` from verifyP256. WebCrypto
  // refuses a non-64-byte P1363 signature on its own, so that guard has no
  // observable behaviour and the mutant is equivalent (it survived when tried).
  {
    rule: "loopback recognises ::1",
    old: 'host === "::1"',
    new: 'host === "::2"',
  },
  {
    rule: "loopback is all of 127.0.0.0/8",
    old: "/^127\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}$/.test(host)",
    new: 'host === "127.0.0.1"',
  },
  {
    rule: "loopback recognises the host name loopback",
    old: 'host === "loopback" || ',
    new: "",
  },
  {
    rule: "loopback recognises the IPv4-mapped ::ffff:127.0.0.1",
    old: 'host === "::ffff:7f00:1" ||',
    new: "",
  },
  {
    rule: "a trailing dot on the host is refused, as System.Uri refuses it",
    old: ' || host.endsWith(".");',
    new: ";",
  },
  {
    rule: "a scheme not followed by // and an authority is refused",
    old: "  if (!shape) return true;",
    new: "  if (!shape) return false;",
  },
  {
    rule: "a backslash in the endpoint is refused",
    old: '  if (raw.includes("\\\\")) return true;\n',
    new: "",
  },
  {
    rule: "a non-ASCII host is refused, so URL cannot fold a fullwidth form to ASCII",
    old: "  return /[^\\u0021-\\u007e]/.test(host)",
    new: "  return false",
  },
  {
    rule: "protected scheme: any scheme containing quic",
    old: 'scheme.includes("quic")',
    new: 'scheme === "quic"',
  },
  {
    // Only `isProtectedEndpoint`'s copy: `isLoopbackEndpoint` keeps its own, so
    // this pins that the protection gate refuses the spellings `URL` repairs
    // rather than inheriting the answer from the loopback rule.
    rule: "isProtectedEndpoint refuses the raw shapes System.Uri refuses",
    old: "  if (refusedByCSharpUriShape(value)) return false;\n  let scheme: string;\n",
    new: "  let scheme: string;\n",
  },
  {
    rule: "protected scheme: https",
    old: 'scheme === "https" || ',
    new: "",
  },
  // The browser target. Its session-acceptance short-circuit must ask the
  // shared module what "unsigned" means; a local re-reading here and the route
  // verifier's reading there can drift apart, and a browser route that the
  // verifier let through unsigned would then be asked for a provider proof.
  {
    target: "browser",
    rule: "the session short-circuit calls the shared isUnsignedCertificate, not a local reading",
    old: "isUnsignedCertificate(route.certificate)",
    new: '(route.certificate?.signature ?? "") === ""',
  },
  // The realtime frame codec. Every rule the cut map names for it gets one
  // mutation; the killer is `npm run test --workspace packages/cultmesh-ts`,
  // whose vectors are written by the C# reference and by this codec in turn.
  // A mutation that changes the encoding symmetrically (an endianness flip, a
  // field swap) still dies, because the vector bytes came from the other side.
  { target: "realtime", rule: "the header is little-endian", old: "view.setUint32(0, MAGIC, true);", new: "view.setUint32(0, MAGIC, false);" },
  { target: "realtime", rule: "the delivery byte sits at offset 4", old: "  result[4] = deliveryByte;\n", new: "  result[5] = deliveryByte;\n" },
  { target: "realtime", rule: "the producer epoch is written before the sequence", old: "  view.setBigInt64(5, frame.producerEpoch, true);\n  view.setBigInt64(13, frame.sequence, true);\n", new: "  view.setBigInt64(5, frame.sequence, true);\n  view.setBigInt64(13, frame.producerEpoch, true);\n" },
  { target: "realtime", rule: "the epoch and sequence are 64-bit, not 32-bit", old: "  view.setBigInt64(5, frame.producerEpoch, true);", new: "  view.setInt32(5, Number(frame.producerEpoch), true);" },
  { target: "realtime", rule: "the three identity lengths are written channel, schema, body", old: "  view.setUint16(23, schema.byteLength, true);\n  view.setUint16(25, body.byteLength, true);\n", new: "  view.setUint16(23, body.byteLength, true);\n  view.setUint16(25, schema.byteLength, true);\n" },
  { target: "realtime", rule: "the identity length prefixes are 16-bit, not 8-bit", old: "  view.setUint16(21, channel.byteLength, true);", new: "  view.setUint8(21, channel.byteLength);" },
  { target: "realtime", rule: "the identity length prefixes are little-endian", old: "  view.setUint16(21, channel.byteLength, true);", new: "  view.setUint16(21, channel.byteLength, false);" },
  { target: "realtime", rule: "the payload length prefix sits at offset 27", old: "  view.setInt32(27, frame.payload.byteLength, true);", new: "  view.setInt32(28, frame.payload.byteLength, true);" },
  { target: "realtime", rule: "the header size is stamped into the frame", old: "  view.setInt32(31, FIXED_HEADER_BYTES, true);", new: "  view.setInt32(31, FIXED_HEADER_BYTES + 1, true);" },
  { target: "realtime", rule: "the wire version is stamped into the frame", old: "  view.setUint16(35, WIRE_VERSION, true);", new: "  view.setUint16(35, WIRE_VERSION + 1, true);" },
  { target: "realtime", rule: "the identities are written channel, schema, body, then the payload", old: "  result.set(channel, offset); offset += channel.byteLength;\n  result.set(schema, offset); offset += schema.byteLength;\n", new: "  result.set(schema, offset); offset += schema.byteLength;\n  result.set(channel, offset); offset += channel.byteLength;\n" },
  { target: "realtime", rule: "the fixed header is 37 bytes", old: "const FIXED_HEADER_BYTES = 37;", new: "const FIXED_HEADER_BYTES = 38;" },
  { target: "realtime", rule: "delivery encodes as the C# enum ordinal, reliable-ordered first", old: 'const DELIVERY_BYTES: readonly CultMeshRealtimeDelivery[] = ["reliable-ordered", "latest-only", "unreliable"];', new: 'const DELIVERY_BYTES: readonly CultMeshRealtimeDelivery[] = ["latest-only", "reliable-ordered", "unreliable"];' },
  { target: "realtime", rule: "an identity the delivery union cannot spell is refused", old: '  if (deliveryByte < 0) throw new Error("Realtime frame delivery mode is invalid.");\n', new: "" },
  { target: "realtime", rule: "identities are UTF-8, and the prefix counts bytes not characters", old: "  const channel = encoder.encode(frame.channelId);", new: "  const channel = Uint8Array.from(frame.channelId, c => c.charCodeAt(0) & 0xff);" },
  { target: "realtime", rule: "an identity over 65535 bytes is refused", old: "  if (channel.byteLength > MAX_IDENTITY_BYTES || schema.byteLength > MAX_IDENTITY_BYTES || body.byteLength > MAX_IDENTITY_BYTES) {\n    throw new Error(\"Realtime frame identity exceeds the QUIC wire limit.\");\n  }\n", new: "" },
  { target: "realtime", rule: "a payload over the 64 MiB ceiling is refused", old: "  if (frame.payload.byteLength > CULTMESH_REALTIME_MAX_PAYLOAD_BYTES) {\n    throw new Error(\"Realtime frame payload exceeds the QUIC wire limit.\");\n  }\n", new: "" },
  { target: "realtime", rule: "the payload ceiling is 64 MiB", old: "export const CULTMESH_REALTIME_MAX_PAYLOAD_BYTES = 64 * 1024 * 1024;", new: "export const CULTMESH_REALTIME_MAX_PAYLOAD_BYTES = 32 * 1024 * 1024;" },
  { target: "realtime", rule: "the encoded ceiling covers the header and three maximal identities", old: "  CULTMESH_REALTIME_MAX_PAYLOAD_BYTES + 37 + 3 * 0xffff;", new: "  CULTMESH_REALTIME_MAX_PAYLOAD_BYTES + 37 + 0xffff;" },
  { target: "realtime", rule: "the ALPN is cultmesh-state-v1", old: 'export const CULTMESH_REALTIME_ALPN = "cultmesh-state-v1";', new: 'export const CULTMESH_REALTIME_ALPN = "cultmesh-state-v2";' },
  { target: "realtime", rule: "the QUIC connection close and stream abort codes", old: "export const CULTMESH_REALTIME_CONNECTION_CLOSE_CODE = 0x43554c54n;", new: "export const CULTMESH_REALTIME_CONNECTION_CLOSE_CODE = 0x53544154n;" },
  { target: "realtime", rule: "the two stream kinds are 1 and 2", old: "export const CULTMESH_REALTIME_RELIABLE_STREAM = 1;", new: "export const CULTMESH_REALTIME_RELIABLE_STREAM = 2;" },
  { target: "realtime", rule: "a blank channel identity is refused, on C#'s whitespace set", old: '  if (isNullOrWhiteSpaceCSharp(frame.channelId)) throw new Error("Realtime channel identity is required.");\n', new: "" },
  { target: "realtime", rule: "the blank reading is C#'s IsNullOrWhiteSpace, not String.prototype.trim", old: "  if (isNullOrWhiteSpaceCSharp(frame.schemaId)) throw new Error", new: '  if (frame.schemaId.trim() === "") throw new Error' },
  { target: "realtime", rule: "a blank body identity is refused", old: '  if (isNullOrWhiteSpaceCSharp(frame.bodyId)) throw new Error("Realtime body identity is required.");\n', new: "" },
  { target: "realtime", rule: "a negative epoch or sequence is refused", old: "  if (frame.producerEpoch < 0n || frame.sequence < 0n) {", new: "  if (false) {" },
  { target: "realtime", rule: "an epoch or sequence outside the i64 range is refused", old: "  if (frame.producerEpoch > INT64_MAX || frame.sequence > INT64_MAX || frame.producerEpoch < INT64_MIN || frame.sequence < INT64_MIN) {", new: "  if (false) {" },
  { target: "realtime", rule: "decode refuses a truncated header", old: '  if (bytes.byteLength < FIXED_HEADER_BYTES) throw new Error("Realtime frame header is truncated.");\n', new: "" },
  { target: "realtime", rule: "decode checks the magic", old: '  if (view.getUint32(0, true) !== MAGIC) throw new Error("Realtime frame magic is invalid.");\n', new: "" },
  { target: "realtime", rule: "decode checks the wire version", old: "  if (view.getUint16(35, true) !== WIRE_VERSION || view.getInt32(31, true) !== FIXED_HEADER_BYTES) {", new: "  if (view.getInt32(31, true) !== FIXED_HEADER_BYTES) {" },
  { target: "realtime", rule: "decode checks the stamped header size", old: "  if (view.getUint16(35, true) !== WIRE_VERSION || view.getInt32(31, true) !== FIXED_HEADER_BYTES) {", new: "  if (view.getUint16(35, true) !== WIRE_VERSION) {" },
  { target: "realtime", rule: "decode refuses a delivery byte outside the three modes", old: '  if (delivery === undefined) throw new Error("Realtime frame delivery mode is invalid.");\n', new: '  if (delivery === undefined) return { channelId: "", schemaId: "", bodyId: "", producerEpoch: 0n, sequence: 0n, delivery: "unreliable", payload: new Uint8Array(0) };\n' },
  { target: "realtime", rule: "decode refuses a length sum that misses the frame", old: "  if (payloadLength < 0 || expected !== bytes.byteLength) throw", new: "  if (payloadLength < 0) throw" },
  // This one was first listed as equivalent — "a negative payload length makes
  // the sum smaller than the frame, so the sum check covers it" — and it did
  // survive. The reasoning was wrong: a negative payload length cancels against
  // the identity lengths, so a 37-byte frame with channelLength 65535 and
  // payloadLength -65535 sums to exactly 37 and decoded to blank identities
  // while the reference refused it. No test covered that shape; one does now.
  { target: "realtime", rule: "decode refuses a negative payload length the sum cancels out", old: "  if (payloadLength < 0 || expected !== bytes.byteLength) throw", new: "  if (expected !== bytes.byteLength) throw" },
  { target: "realtime", rule: "decode keeps a leading byte-order mark, as Encoding.UTF8.GetString does", old: 'const decoder = new TextDecoder("utf-8", { ignoreBOM: true });', new: "const decoder = new TextDecoder();" },
  { target: "realtime", rule: "decode reads exactly the viewed bytes of an offset view", old: "  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);", new: "  const view = new DataView(bytes.buffer);" },
  { target: "realtime", rule: "the decoded payload does not alias the frame it came from", old: "    payload: new Uint8Array(bytes.subarray(offset, offset + payloadLength)),", new: "    payload: bytes.subarray(offset, offset + payloadLength)," },
  // The narrower half of the same rule, and the one that shipped broken: a plain
  // `bytes.slice` copies a `Uint8Array` but aliases a `Buffer`, whose own `slice`
  // is an alias of `subarray`. The mutation above dies on the `Uint8Array` test;
  // this one survives it and dies only on the `Buffer` test.
  { target: "realtime", rule: "the decoded payload copies out of a Buffer, whose own slice is a view", old: "    payload: new Uint8Array(bytes.subarray(offset, offset + payloadLength)),", new: "    payload: bytes.slice(offset, offset + payloadLength)," },
  // And the payload's type does not follow the frame's. This spelling copies, so
  // both mutations above are dead to it, but `@@species` hands a `Buffer` frame a
  // `Buffer` payload; only the `Buffer.isBuffer` assertion kills it.
  { target: "realtime", rule: "the decoded payload is a plain Uint8Array whatever the frame was", old: "    payload: new Uint8Array(bytes.subarray(offset, offset + payloadLength)),", new: "    payload: Uint8Array.prototype.slice.call(bytes, offset, offset + payloadLength)," },
  // Last, because it removes this target's sentinel.
  { target: "realtime", rule: "the frame magic is 0x31545343", old: "const MAGIC = 0x31545343;", new: "const MAGIC = 0x31545344;" },

  // The native bridge's runtime lifetime. Each rule gets two mutations: the
  // revert, which puts back the spelling that was wrong, and a loosening, which
  // keeps the shape and gives away the guarantee. A rule that only the revert
  // kills is pinned by accident.
  //
  // `honestOn` is load-bearing. There is no ThreadSanitizer for MSVC, so the
  // notify rule cannot be seen on win32-x64 at all; the quiesce rule is visible
  // on both, but only because the assertion build states it outright. Without
  // the assertion it is visible on neither in any dependable way: Soul's Cut 3
  // pass hit the use-after-free 4 times in 48 linux-x64 AddressSanitizer
  // attempts with pollers blocked mid-copy of large frames, and never once on
  // release.
  {
    target: "native",
    honestOn: ["linux-x64"],
    rule: "the call scope notifies under `gate` (revert: notify after the unlock)",
    old: "        std::lock_guard<std::mutex> lock(runtime_->gate);\n" +
      "        --runtime_->active_calls;\n" +
      "        runtime_->signal.notify_all();\n",
    new: "        {\n" +
      "            std::lock_guard<std::mutex> lock(runtime_->gate);\n" +
      "            --runtime_->active_calls;\n" +
      "        }\n" +
      "        runtime_->signal.notify_all();\n",
  },
  {
    target: "native",
    honestOn: ["linux-x64"],
    // The tempting spelling: notify only when this was the last call out, which
    // looks like it narrows the window and does not close it at all. The closer's
    // predicate is true the moment the count reaches zero, so this is the same
    // race with a tidier face.
    rule: "the call scope notifies under `gate` (loosening: notify outside it only when the count reaches zero)",
    old: "        std::lock_guard<std::mutex> lock(runtime_->gate);\n" +
      "        --runtime_->active_calls;\n" +
      "        runtime_->signal.notify_all();\n",
    new: "        bool last = false;\n" +
      "        {\n" +
      "            std::lock_guard<std::mutex> lock(runtime_->gate);\n" +
      "            last = (--runtime_->active_calls == 0);\n" +
      "        }\n" +
      "        if (last) runtime_->signal.notify_all();\n",
  },
  // The rule the quiesce rests on, and the one the scenario used to assume
  // rather than test: the blocking crossing is a counted host call for as long
  // as it is inside the library. Soul deleted the scope from `next_event`
  // outright and every configuration stayed green, because the pollers finished
  // before the teardown reached them. `holdclose` reads the bridge's own count
  // instead.
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "the blocking crossing is a counted call (revert: no call scope on next_event)",
    old: "    CallScope scope(runtime);\n" +
      "    if (!scope.entered()) return -1;\n" +
      "    *out_required = 0;\n",
    new: "    *out_required = 0;\n",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    // The shape that passes review: the guard is there, it refuses during a
    // close, it is simply let go before the call blocks. What the closer waits
    // on is the count, and this hands it back at the door.
    rule: "the blocking crossing is a counted call (loosening: the scope is dropped before the wait)",
    old: "    CallScope scope(runtime);\n" +
      "    if (!scope.entered()) return -1;\n" +
      "    *out_required = 0;\n",
    new: "    {\n" +
      "        CallScope scope(runtime);\n" +
      "        if (!scope.entered()) return -1;\n" +
      "    }\n" +
      "    *out_required = 0;\n",
  },
  // And the fixture's own premise, which is a rule of the bridge and not of the
  // scenario: a positive timeout blocks. Soul inverted this comparison and the
  // scenario stayed green on six configurations, because nothing checked that
  // the pollers were inside the library when the close began.
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "a positive timeout blocks until an event or the close (revert: it returns at once)",
    old: "    if (runtime->events.empty() && timeout_ms > 0) {",
    new: "    if (runtime->events.empty() && timeout_ms < 0) {",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    // Still a wait, still the host's timeout in the signature, and the poller is
    // gone a millisecond later. `waitseam` kills this by equality now — the
    // recorded wait is 1 at every probe that is not 1 — where it used to die on
    // `polltimeout` alone, and only because 1 ms is shorter than `closerace`'s
    // 50 ms settle, so the poller left before the close began and the scenario
    // complained about its own fixture rather than the bridge.
    rule: "a positive timeout blocks until an event or the close (loosening: it waits a token 1 ms)",
    old: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)),",
    new: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(1)),",
  },
  // And the rule underneath that one: the duration waited on is the host's, not
  // one of the bridge's own. Self's ruling of 2026-09-22 moved this whole family
  // off wall-clock bands and onto `waitseam`'s equality check, after seven Soul
  // passes each found another derivation that was the identity at whatever
  // probes the wall clock could afford to hold tight. `polltimeout`,
  // `pollbusy` and `holdtimeout` still run every entry below — a constant, a
  // clamp, a floor, an offset, a scale, a later-poll fault — with generous
  // margins, as proof the recorded wait is really waited and not merely
  // recorded; `waitseam` is what actually kills them now, by exact mismatch
  // against the host's own argument, deterministically and on both platforms.
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "the wait is on the host's timeout (revert: a shorter constant of the bridge's own)",
    old: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)),",
    new: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(100)),",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    // The lengthening, which is the half that looks harmless: every poll still
    // blocks, every poll still returns, and nothing anywhere reports an error.
    rule: "the wait is on the host's timeout (loosening: a longer constant of the bridge's own)",
    old: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)),",
    new: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(2000)),",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    // The derived wait, which is the shape the two constants above do not cover
    // and the most ordinary spelling this line will ever be given: cap the wait
    // so a shutdown gets noticed.
    rule: "the wait is on the host's timeout (loosening: the bridge caps it at a second)",
    old: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)),",
    new: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds((std::min)(timeout_ms, 1000))),",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    // The same clamp at a round ceiling nobody would call short.
    rule: "the wait is on the host's timeout (loosening: the bridge caps it at a round five seconds)",
    old: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)),",
    new: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds((std::min)(timeout_ms, 5000))),",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "the wait is on the host's timeout (loosening: a floor, so a polling host cannot spin)",
    old: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)),",
    new: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds((std::max)(timeout_ms, 100))),",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "the wait is on the host's timeout (loosening: a little grace added to it)",
    old: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)),",
    new: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms + 100)),",
  },
  // Soul's seventh pass named eight more derivations that were each the
  // identity at every wall-clock probe the previous six passes could afford:
  // a floor at 50 with no early check to see it, a 16 ms quantum, a 40 ms
  // quantum, a wait that doubles once a runtime has been asked more than
  // twelve times, a clamp at 7275, an offset of 25, a floor of 40, and a 6%
  // scale. `waitseam` reads what the bridge actually hands its wait instead of
  // timing it, so all eight now die by exact mismatch — most of them at more
  // than one probe, which is what "the identity at every probe" cost them.
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "the wait is on the host's timeout (loosening: a floor at 50, invisible to a late-only check)",
    old: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)),",
    new: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms < 50 ? 0 : timeout_ms)),",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "the wait is on the host's timeout (loosening: rounded down to a 16 ms quantum)",
    old: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)),",
    new: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms / 16 * 16)),",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "the wait is on the host's timeout (loosening: rounded up to a 40 ms quantum)",
    old: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)),",
    new: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds((timeout_ms + 39) / 40 * 40)),",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    // Stateful, and the reason `waitseam` polls one runtime forty times: no
    // scenario before it ever asked the same runtime for a thirteenth poll, so
    // a fault that only shows past the twelfth had nowhere to be seen at all.
    rule: "the wait is on the host's timeout (loosening: it doubles once a runtime has polled more than 12 times)",
    old: "        const bool woken = runtime->signal.wait_for(\n" +
      "            lock, CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)),\n" +
      "            [runtime] { return !runtime->events.empty() || runtime->closing; });\n",
    new: "        static std::atomic<int> debug_poll_count{0};\n" +
      "        const int32_t doubled_timeout_ms = (++debug_poll_count > 12) ? timeout_ms * 2 : timeout_ms;\n" +
      "        const bool woken = runtime->signal.wait_for(\n" +
      "            lock, CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(doubled_timeout_ms)),\n" +
      "            [runtime] { return !runtime->events.empty() || runtime->closing; });\n",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "the wait is on the host's timeout (loosening: the bridge caps it at 7275 ms)",
    old: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)),",
    new: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds((std::min)(timeout_ms, 7275))),",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "the wait is on the host's timeout (loosening: 25 ms of grace added to it)",
    old: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)),",
    new: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms + 25)),",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "the wait is on the host's timeout (loosening: a floor of 40, so a polling host cannot spin)",
    old: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)),",
    new: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds((std::max)(timeout_ms, 40))),",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "the wait is on the host's timeout (loosening: scaled up by 6%)",
    old: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)),",
    new: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms * 106 / 100)),",
  },
  // The seam itself: development-only, folded away to a pass-through in a
  // release build (see the macro's `#else` in cultmesh_quic_native.cpp), and
  // still load-bearing enough in a debug build that Self's ruling names it
  // explicitly. A seam that never records is caught the same way a mutation
  // that never happened would be missed by anything else — `waitseam` reads
  // -1 forever and every real-wait probe mismatches.
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "the wait seam records what the bridge actually waits on (revert: it never records)",
    old: "CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)),",
    new: "std::chrono::milliseconds(timeout_ms),",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    // The tempting refactor: pull the record out to the top so it reads
    // unconditionally, once, instead of being wrapped around the wait itself.
    // It still records the host's own argument at every value that reaches a
    // real wait, which is why `waitseam`'s spread does not catch it — the
    // divergence is at `timeout_ms <= 0`, where the bridge's own guard never
    // waits at all and the honest seam must stay untouched. This one touches
    // it anyway.
    rule: "the wait seam records what the bridge actually waits on (loosening: it records the argument unconditionally, instead of only what it hands the wait)",
    old: "    std::unique_lock<std::mutex> lock(runtime->gate);\n" +
      "    if (runtime->events.empty() && timeout_ms > 0) {\n" +
      "        // The host's timeout bounds the wait; an event or the close ends it\n" +
      "        // sooner, and `woken` is which of the two happened. RECORD_WAIT wraps\n" +
      "        // this exact argument; see the development seam note near its macros.\n" +
      "        const bool woken = runtime->signal.wait_for(\n" +
      "            lock, CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)),\n" +
      "            [runtime] { return !runtime->events.empty() || runtime->closing; });\n",
    new: "    std::unique_lock<std::mutex> lock(runtime->gate);\n" +
      "    CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms));\n" +
      "    if (runtime->events.empty() && timeout_ms > 0) {\n" +
      "        // The host's timeout bounds the wait; an event or the close ends it\n" +
      "        // sooner, and `woken` is which of the two happened. RECORD_WAIT wraps\n" +
      "        // this exact argument; see the development seam note near its macros.\n" +
      "        const bool woken = runtime->signal.wait_for(\n" +
      "            lock, std::chrono::milliseconds(timeout_ms),\n" +
      "            [runtime] { return !runtime->events.empty() || runtime->closing; });\n",
  },
  // The wait's predicate: a poll with nothing to deliver stays for its timeout
  // however often the host's other threads call in. Every call leaves through a
  // CallScope that wakes every waiter, so it is the predicate, and nothing else,
  // that sends a woken poll back to waiting. Every scenario before `pollbusy` had
  // a single-threaded host, nothing called during a wait, and dropping the
  // predicate survived the whole matrix on both targets.
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    // "Returns on any wake": the one S1 asked for by name. `pollhammer`
    // notifies this wait far more than once a timeout, from a thread that
    // does nothing but touch the gate in a loop, so this leaves within
    // milliseconds of its 1000 ms ask.
    rule: "an idle poll stays for its timeout while other host threads call (revert: the wait has no predicate)",
    old: "        const bool woken = runtime->signal.wait_for(\n" +
      "            lock, CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)),\n" +
      "            [runtime] { return !runtime->events.empty() || runtime->closing; });\n",
    new: "        const bool woken = runtime->signal.wait_for(\n" +
      "            lock, CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms))) ==\n" +
      "            std::cv_status::no_timeout;\n",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    // The careful-looking spelling: it works out for itself what woke it, so the
    // hold is never asked about a wake that found nothing, and it still leaves on
    // any wake at all. Only a second host thread can see it.
    rule: "an idle poll stays for its timeout while other host threads call (loosening: it reads what woke it and does not wait again)",
    old: "        const bool woken = runtime->signal.wait_for(\n" +
      "            lock, CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)),\n" +
      "            [runtime] { return !runtime->events.empty() || runtime->closing; });\n",
    new: "        runtime->signal.wait_for(\n" +
      "            lock, CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)));\n" +
      "        const bool woken = !runtime->events.empty() || runtime->closing;\n",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    // The hand-written loop, which is the ordinary way to spell a predicate
    // wait and gets the other half wrong: it does wait again, for the whole
    // timeout each time. A host whose other thread calls more often than its poll
    // timeout never sees the poll come back while that thread is busy. A function
    // of the input, so no single probe value makes it the identity.
    rule: "an idle poll stays for its timeout while other host threads call (loosening: each wake restarts the whole timeout)",
    old: "        const bool woken = runtime->signal.wait_for(\n" +
      "            lock, CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)),\n" +
      "            [runtime] { return !runtime->events.empty() || runtime->closing; });\n",
    new: "        while (runtime->events.empty() && !runtime->closing &&\n" +
      "               runtime->signal.wait_for(\n" +
      "                   lock, CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms))) ==\n" +
      "                   std::cv_status::no_timeout) {\n" +
      "        }\n" +
      "        const bool woken = !runtime->events.empty() || runtime->closing;\n",
  },
  // S1's two predicate survivors, named K1 and K2 in Soul's seventh pass: a
  // wake count that gives up past 64, and a runtime error that ends the wait
  // on any later wake regardless of the count. `pollbusy` covered one call
  // kind, at one pace, on a runtime with no error recorded, and both survived
  // it. `pollhammer` is one thread touching the gate in a yield loop instead
  // of every 20 ms, which K1's count cannot outlast, run once clean for K1 and
  // once after a refused call has recorded an error for K2.
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "an idle poll stays for its timeout while other host threads call (loosening: K1, it gives up after 64 wakes)",
    old: "            [runtime] { return !runtime->events.empty() || runtime->closing; });\n",
    new: "            [runtime] {\n" +
      "                static std::atomic<int> debug_wakes{0};\n" +
      "                return !runtime->events.empty() || runtime->closing || ++debug_wakes > 64;\n" +
      "            });\n",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "an idle poll stays for its timeout while other host threads call (loosening: K2, it ends on any wake once an error is recorded)",
    old: "            [runtime] { return !runtime->events.empty() || runtime->closing; });\n",
    new: "            [runtime] { return !runtime->events.empty() || runtime->closing || !runtime->error.empty(); });\n",
  },
  // The seam's own rule, and the only non-comment source change of the last pass
  // that nothing pinned. It is development-only and folds away in release, so
  // nothing shipped was at risk; it is still the shape this campaign keeps
  // finding, a change justified by a scenario that cannot observe it. The
  // timeout scenario never armed the hold and the hold scenarios never let a
  // timeout expire, so no call was ever in both states and the guard could be
  // deleted with everything green. `holdtimeout` puts one call in both.
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "the hold parks only a call the wait woke (revert: it parks whatever reaches it)",
    old: "        if (woken && CULTMESH_QUIC_DEBUG_HELD(lock)) return 0;\n",
    new: "        if (CULTMESH_QUIC_DEBUG_HELD(lock)) return 0;\n",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    // The spelling nobody would look twice at: the same two conditions, the
    // other way round. The hold blocks, so the order is the rule — asked first,
    // it parks the call and then decides whether it should have.
    rule: "the hold parks only a call the wait woke (loosening: it decides after parking)",
    old: "        if (woken && CULTMESH_QUIC_DEBUG_HELD(lock)) return 0;\n",
    new: "        if (CULTMESH_QUIC_DEBUG_HELD(lock) && woken) return 0;\n",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    // A guard that reads the input instead of the wake: a long wait is surely a
    // wake worth holding. It is the identity on any poll under its threshold, and
    // `holdtimeout`'s second, longer poll is what sits above it.
    rule: "the hold parks only a call the wait woke (loosening: it also parks any long wait, over 1000)",
    old: "        if (woken && CULTMESH_QUIC_DEBUG_HELD(lock)) return 0;\n",
    new: "        if ((woken || timeout_ms > 1000) && CULTMESH_QUIC_DEBUG_HELD(lock)) return 0;\n",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    // S5: the same guard at 1500. `holdtimeout`'s long probe sits above it, at
    // 1600, with the hold kept armed past it — a call this guard parks anyway
    // sits out the rest of the arming instead of leaving on its own timeout,
    // well past the scenario's generous late tolerance.
    rule: "the hold parks only a call the wait woke (loosening: it also parks any long wait, over 1500)",
    old: "        if (woken && CULTMESH_QUIC_DEBUG_HELD(lock)) return 0;\n",
    new: "        if ((woken || timeout_ms > 1500) && CULTMESH_QUIC_DEBUG_HELD(lock)) return 0;\n",
  },
  // The close's wake. It was defended by committed code and had no entry, so the
  // table understated what `closerace` covers; these say it.
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "the close wakes every blocked poll (revert: the close marks and waits without waking)",
    old: "        runtime->signal.notify_all();\n" +
      "        CULTMESH_QUIC_DEBUG_AT_CLOSE(runtime->active_calls);\n",
    new: "        CULTMESH_QUIC_DEBUG_AT_CLOSE(runtime->active_calls);\n",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    // The plausible reading: the wait is for events, so wake it when there are
    // events. The wake a close owes has nothing to do with the queue.
    //
    // Not listed beside it: narrowing the close's `notify_all` to `notify_one`.
    // It survived when tried, and it is equivalent rather than uncovered — but
    // the equivalence rests on two premises together, and neither alone carries
    // it, so they are written here where the claim can be checked instead of
    // taken:
    //
    //  - `closing` is set permanently under the gate before the notify and is in
    //    every waiter's predicate, so once a close has begun no poller can block
    //    at all. The set of waiters is therefore fixed when the close wakes one.
    //  - each exiting call's own CallScope destructor takes the gate, decrements
    //    and wakes everyone. So one wake is enough to start a chain that reaches
    //    all of them.
    //
    // Narrowing *both* of those to `notify_one` also survived, and that one is
    // not equivalent by any argument: a wake consumed by the closer, which waits
    // on the same variable for the count to reach zero, re-checks a count that
    // is still non-zero and goes back to waiting, and the chain stops with the
    // remaining pollers sitting out their timeouts. It passes on wait-queue
    // ordering alone — the pollers are all queued before the closer, so the head
    // of a FIFO queue is never the closer until it is the only waiter left.
    //
    // That is recorded as unreached, not unreachable. A scenario cannot force it
    // while the first premise holds, because the only way to put the closer
    // ahead of a poller in that queue is a poller that blocks after the close
    // has begun, which is the thing premise one rules out. Reaching it would
    // take a seam that controls which waiter a notify goes to, which does not
    // exist and is not obviously worth building. The standard does not promise
    // FIFO, so the survival is the implementation's, not the code's.
    rule: "the close wakes every blocked poll (loosening: it wakes only when something is queued)",
    old: "        runtime->signal.notify_all();\n" +
      "        CULTMESH_QUIC_DEBUG_AT_CLOSE(runtime->active_calls);\n",
    new: "        if (!runtime->events.empty()) runtime->signal.notify_all();\n" +
      "        CULTMESH_QUIC_DEBUG_AT_CLOSE(runtime->active_calls);\n",
  },
  // Section 4's other half: a call that races the start of a close is refused.
  // The header has promised it since it was written and nothing checked it. A
  // call admitted instead increments the in-flight count behind a wait that has
  // already read it, and that wait can then be left on a count that only reaches
  // zero if the late caller happens to leave.
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "a call racing the start of a close is refused (revert: the scope admits it)",
    old: "        if (runtime_->closing) return;\n",
    new: "",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    // The reading that sounds conservative: if other calls are still inside, the
    // close is waiting for them anyway, so one more can do no harm. It is the
    // exact case the refusal exists for.
    rule: "a call racing the start of a close is refused (loosening: only when nothing else is inside)",
    old: "        if (runtime_->closing) return;\n",
    new: "        if (runtime_->closing && runtime_->active_calls == 0) return;\n",
  },
  // The two-phase poll, which is how every host sizes its buffer. Reaching it
  // needs no listener, no credential and no established connection: a client
  // opening to a closed loopback port is refused in about a millisecond and the
  // refusal always carries a non-empty reason, which is an 87-byte payload.
  // It was written off as unreachable and is neither unreachable nor safe —
  // moving the pop above the copy crashes outright.
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "a payload that does not fit is refused without being consumed (revert: the refusal pops)",
    old: "    if (required > payload_capacity) {\n" +
      "        *out_required = required;\n" +
      "        return 2;\n" +
      "    }\n",
    new: "    if (required > payload_capacity) {\n" +
      "        *out_required = required;\n" +
      "        runtime->events.pop_front();\n" +
      "        return 2;\n" +
      "    }\n",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    // The helpful shape: give the caller what fits, tell it what it missed. It
    // reports 2, so a host obeying the header asks again — for an event that is
    // no longer there.
    rule: "a payload that does not fit is refused without being consumed (loosening: it truncates)",
    old: "    if (required > payload_capacity) {\n" +
      "        *out_required = required;\n" +
      "        return 2;\n" +
      "    }\n",
    new: "    if (required > payload_capacity) {\n" +
      "        *out_required = required;\n" +
      "        *out_event = queued.header;\n" +
      "        if (payload_capacity > 0 && payload != nullptr)\n" +
      "            std::memcpy(payload, queued.payload.data(), static_cast<size_t>(payload_capacity));\n" +
      "        runtime->events.pop_front();\n" +
      "        return 2;\n" +
      "    }\n",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "the event is copied before it is popped (revert: the pop moves above the copy)",
    old: "    *out_event = queued.header;\n" +
      "    if (required > 0 && payload != nullptr)\n" +
      "        std::memcpy(payload, queued.payload.data(), static_cast<size_t>(required));\n" +
      "    *out_required = required;\n" +
      "    runtime->events.pop_front();\n",
    new: "    runtime->events.pop_front();\n" +
      "    *out_event = queued.header;\n" +
      "    if (required > 0 && payload != nullptr)\n" +
      "        std::memcpy(payload, queued.payload.data(), static_cast<size_t>(required));\n" +
      "    *out_required = required;\n",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    // `queued` is a reference into the deque, and the pop destroys what it names.
    // This is the spelling that hides that: the header is copied first, so only
    // the payload — the part that is a pointer into a freed vector — is read
    // afterwards. Both die; this one dies with the header already correct, which
    // is why the payload bytes are checked and not only their length.
    rule: "the event is copied before it is popped (loosening: only the payload copy moves after it)",
    old: "    *out_event = queued.header;\n" +
      "    if (required > 0 && payload != nullptr)\n" +
      "        std::memcpy(payload, queued.payload.data(), static_cast<size_t>(required));\n" +
      "    *out_required = required;\n" +
      "    runtime->events.pop_front();\n",
    new: "    *out_event = queued.header;\n" +
      "    *out_required = required;\n" +
      "    runtime->events.pop_front();\n" +
      "    if (required > 0 && payload != nullptr)\n" +
      "        std::memcpy(payload, queued.payload.data(), static_cast<size_t>(required));\n",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "the close waits for every in-flight call (revert: wake without the wait)",
    old: "        runtime->signal.wait(lock, [runtime] { return runtime->active_calls == 0; });\n",
    new: "",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    // A bounded wait is the shape that survives review: it looks like the wait,
    // it usually finishes, and it turns the contract into a hope. The bound is
    // 50 ms, not the 1 ms this entry used to carry, because 1 ms only ever lost
    // a race — Soul measured the margin at a few hundred lock handoffs, and the
    // same loosening at 50 ms survived on both targets. `holdclose` holds its
    // pollers for 500 ms, so the bound no longer has to be unlucky: any bound
    // shorter than the hold expires with calls still inside and the bridge's own
    // assertion says so. A bound longer than the hold is not distinguished, and
    // that is the honest limit of this entry.
    rule: "the close waits for every in-flight call (loosening: a bounded wait that gives up)",
    old: "        runtime->signal.wait(lock, [runtime] { return runtime->active_calls == 0; });",
    new: "        runtime->signal.wait_for(lock, std::chrono::milliseconds(50),\n" +
      "            [runtime] { return runtime->active_calls == 0; });",
  },
  // Self's ruling of 2026-09-22, the eighth fix batch. Six findings, six
  // entries below (R1, W1-W3, P1, Z1-Z2); F5 (closerace under starvation) and
  // F6 (stale prose) needed no new mutant.
  //
  // F1: nothing ran the release macro's `#else` branch. `polltimeout`,
  // `pollbusy`, `pollhammer` and `zerotimeout` now run against a plain
  // release configuration (asserts OFF, CMAKE_BUILD_TYPE=Release) on both
  // targets, and R1 changes that branch from a pass-through to a tenth of the
  // real timeout — invisible to every configuration that never built it.
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "R1: the release macro is a pass-through (loosening: it divides the duration by 10)",
    old: "#define CULTMESH_QUIC_DEBUG_RECORD_WAIT(duration) (duration)",
    new: "#define CULTMESH_QUIC_DEBUG_RECORD_WAIT(duration) ((duration)/10)",
  },
  // F2: the seam now wraps the whole duration handed to `wait_for`, so
  // anything that reaches the wait through the seam's own argument is caught
  // by `waitseam`'s equality check. These three apply their arithmetic
  // outside that argument instead — to the return value of RECORD_WAIT,
  // after it has already recorded and returned the honest duration — so
  // `waitseam` cannot see them by construction; only the wall-clock
  // scenarios can, which is why `kGenerousLateToleranceMs` was narrowed
  // alongside this entry.
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "W1: 600 ms added to the wait outside the seam's own argument",
    old: "lock, CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)),",
    new: "lock, CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)) + " +
      "std::chrono::milliseconds(600),",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "W2: the wait floored at 500 ms outside the seam's own argument",
    old: "lock, CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)),",
    new: "lock, (std::max)(CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)), " +
      "std::chrono::milliseconds(500)),",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "W3: the wait scaled by a 1000/909 ratio outside the seam's own argument",
    old: "lock, CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)),",
    new: "lock, CULTMESH_QUIC_DEBUG_RECORD_WAIT(std::chrono::milliseconds(timeout_ms)) * 1000 / 909,",
  },
  // F3: `pollhammer` covered the predicate only on a runtime with nothing
  // live in it. P1 adds the survivor: a busy host with a connection is
  // treated the same as a busy host with events queued, so it never sleeps.
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "P1: an idle poll also ends when the runtime holds a live connection",
    old: "            [runtime] { return !runtime->events.empty() || runtime->closing; });\n",
    new: "            [runtime] {\n" +
      "                return !runtime->events.empty() || runtime->closing ||\n" +
      "                    !runtime->connections.empty();\n" +
      "            });\n",
  },
  // F4: a non-positive timeout was never timed at all — the bridge's own
  // guard skips the wait outright, so nothing pinned how fast that path has
  // to return. Both entries insert a stall on exactly that path, ahead of
  // the guard that already exists.
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "Z1: a non-positive timeout sleeps 500 ms before returning",
    old: "    if (runtime->events.empty() && timeout_ms > 0) {\n",
    new: "    if (runtime->events.empty() && timeout_ms <= 0) {\n" +
      "        std::this_thread::sleep_for(std::chrono::milliseconds(500));\n" +
      "    }\n" +
      "    if (runtime->events.empty() && timeout_ms > 0) {\n",
  },
  {
    target: "native",
    honestOn: ["linux-x64", "win32-x64"],
    rule: "Z2: a non-positive timeout spins for 300 ms before returning",
    old: "    if (runtime->events.empty() && timeout_ms > 0) {\n",
    new: "    if (runtime->events.empty() && timeout_ms <= 0) {\n" +
      "        const auto debug_z2_until =\n" +
      "            std::chrono::steady_clock::now() + std::chrono::milliseconds(300);\n" +
      "        while (std::chrono::steady_clock::now() < debug_z2_until) {\n" +
      "        }\n" +
      "    }\n" +
      "    if (runtime->events.empty() && timeout_ms > 0) {\n",
  },
];

function digest(bytes) {
  return createHash("sha256").update(bytes).digest("hex");
}

function npm(workspace, ...args) {
  execFileSync(process.execPath, [npmCli, ...args, "--workspace", workspace], {
    cwd: repoRoot,
    stdio: ["ignore", "pipe", "pipe"],
  });
}

function testsPass(workspace) {
  try {
    npm(workspace, "run", "test");
    return true;
  } catch {
    return false;
  }
}

// The native killer. The bridge is a shared library, so "run the tests" is build
// the scenario runner beside it and run the scenarios. There is no npm here and
// no `dist/`; the artifacts live under artifacts/, which is git-ignored.
//
// Two or three configurations, because the rules are visible to different
// things. The assertion build states the quiesce invariant outright, which is
// what makes the missing wait fail on the first close instead of on an unlucky
// one. The ThreadSanitizer build is the only thing that sees a notify land on
// a destroyed condition variable, and it exists on linux-x64 alone. The
// release build is the thing that actually ships: asserts off, no development
// seam, no sanitizer — added by Self's ruling of 2026-09-22 because it is the
// one configuration where R1's release-only macro branch is reachable at all.
//
// `holdclose`, `latecall` and `holdtimeout` run only where the development seam
// exists, which is the assertion build: they drive the quiesce and the refusal
// through that seam rather than through a sleep, and they are what kill the
// rules about counting a host call at all. The ThreadSanitizer build
// deliberately keeps the shipped shape — no assertions, no seam — because what
// it is there for is the race in a library built like the one that ships, and
// the release build keeps that same shape for the rules that ship-shape alone
// can hide.
//
// `polltimeout`, `pollbusy`, `pollhammer` and `holdtimeout` measure a wait
// against the timeout it asked for, so they are run only where nothing
// distorts the clock: a sanitizer's slowdown would make their bands
// meaningless, which is the honest limit of those entries.
const assertScenarios = [
  ["holdclose", "3", "64"],
  ["latecall", "5"],
  ["holdtimeout", "3"],
  ["waitseam", "2"],
  ["polltimeout", "3"],
  ["pollbusy", "3"],
  ["pollhammer", "3"],
  ["zerotimeout", "3"],
  ["payloadfit", "3"],
  ["closerace", "20", "256"],
];
// Self's ruling of 2026-09-22, F1: the assertion build is the only
// configuration that ever ran the timed scenarios, so R1's release-only
// `#else` branch (cultmesh_quic_native.cpp's CULTMESH_QUIC_DEBUG_RECORD_WAIT
// macro) was unobservable by construction — nothing built without
// CULTMESH_QUIC_DEBUG_ASSERTS ever ran `polltimeout`, `pollbusy` or
// `pollhammer` against it. This configuration is the thing that ships: a
// plain Release build, asserts off, no sanitizer, running the timed
// scenarios plus the new zero-timeout check against it.
const releaseScenarios = [
  ["polltimeout", "3"],
  ["pollbusy", "3"],
  ["pollhammer", "3"],
  ["zerotimeout", "3"],
];
const nativeConfigurations = platform === "linux-x64"
  ? [
      { name: "asserts", asserts: "ON", sanitizer: null, scenarios: assertScenarios },
      { name: "tsan", asserts: "OFF", sanitizer: "thread", scenarios: [["closerace", "20", "256"]] },
      { name: "release", asserts: "OFF", sanitizer: null, buildType: "Release", scenarios: releaseScenarios },
    ]
  : [
      { name: "asserts", asserts: "ON", sanitizer: null, scenarios: assertScenarios },
      { name: "release", asserts: "OFF", sanitizer: null, buildType: "Release", scenarios: releaseScenarios },
    ];

function msquicArguments() {
  if (platform === "win32-x64") {
    const root = join(repoRoot, "artifacts", "dependencies", "msquic-openssl-2.5.9",
      "package", "build", "native");
    if (!existsSync(root))
      throw new Error(`${root} is missing; run scripts/build-quic-native.ps1 once to fetch MsQuic`);
    return {
      configure: [`-DMSQUIC_ROOT=${root.replace(/\\/g, "/")}`, "-A", "x64"],
      runtime: [[join(root, "bin", "x64", "msquic.dll"), "msquic.dll"]],
    };
  }
  const cache = join(repoRoot, "artifacts", "dependencies", "msquic-linux-2.5.9");
  const libraries = join(cache, "package", "usr", "lib", "x86_64-linux-gnu");
  const versioned = join(libraries, "libmsquic.so.2.5.9");
  if (!existsSync(versioned))
    throw new Error(`${versioned} is missing; run scripts/build-quic-native.sh once to fetch MsQuic`);
  // CMake links -lmsquic, which needs an unversioned name to resolve against;
  // the deb ships only the versioned file.
  writeFileSync(join(libraries, "libmsquic.so"), readFileSync(versioned));
  return {
    configure: [
      `-DMSQUIC_INCLUDE_DIR=${join(cache, "include")}`,
      `-DMSQUIC_LIB_DIR=${libraries}`,
    ],
    runtime: [[versioned, "libmsquic.so.2"]],
  };
}

// What the last failed native check actually said. A red control used to arrive
// as a bare RED line with the child's output thrown away, which is how a
// configuration that could not start at all stayed invisible; this is printed
// with it.
let nativeFailure = null;

function childOutput(error) {
  const parts = [error.stderr, error.stdout]
    .map(stream => (stream ? stream.toString("utf8").trimEnd() : ""))
    .filter(text => text.length > 0);
  return parts.length > 0 ? parts.join("\n") : `${error.message}`;
}

// A sanitizer needs the process's address space where it expects it. Under the
// kernel's address-space randomisation a ThreadSanitizer process dies before
// main with "unexpected memory mapping", which is not a mutation being killed —
// it is every mutation being reported killed for a run that never started. So
// the sanitizer configurations are re-executed with randomisation off rather
// than left to whoever invokes this script to know.
//
// `setarch -R` asks for that through personality(ADDR_NO_RANDOMIZE), which
// Docker's default seccomp profile denies. Both failures are named here rather
// than swallowed.
function scenarioCommand(runner, scenario, configuration) {
  if (configuration.sanitizer && platform === "linux-x64")
    return ["setarch", ["-R", runner, ...scenario]];
  return [runner, scenario];
}

function explainUnrunnable(error, configuration, runner, scenario) {
  const output = childOutput(error);
  const invocation = `setarch -R ${runner} ${scenario.join(" ")}`;
  if (error.code === "ENOENT")
    return `the ${configuration.name} configuration needs setarch (util-linux) to disable address-space ` +
      `randomisation, and it is not on PATH.\n  Required invocation: ${invocation}`;
  if (/failed to set personality|Operation not permitted/.test(output))
    return `setarch could not disable address-space randomisation for the ${configuration.name} ` +
      "configuration: personality(ADDR_NO_RANDOMIZE) was denied.\n" +
      "  Docker's default seccomp profile denies it; run the container with --security-opt seccomp=unconfined.\n" +
      `  Required invocation: ${invocation}\n  ${output}`;
  if (/unexpected memory mapping/.test(output))
    return `the ${configuration.name} sanitizer died before the scenario started, on the kernel's ` +
      "address-space randomisation.\n" +
      `  Required invocation: ${invocation}, in a container run with --security-opt seccomp=unconfined.\n` +
      `  ${output}`;
  return null;
}

function nativeScenariosPass() {
  const msquic = msquicArguments();
  const source = join(repoRoot, "native", "GameCult.Mesh.Quic.Native");
  for (const configuration of nativeConfigurations) {
    const build = join(repoRoot, "artifacts", "quic-native-mutation", platform, configuration.name);
    // From scratch each time: a stale object file would let a mutation be
    // reported against a binary that does not contain it.
    rmSync(build, { recursive: true, force: true });
    const flags = configuration.sanitizer
      ? [`-fsanitize=${configuration.sanitizer}`, "-fno-omit-frame-pointer", "-g"].join(" ")
      : "";
    // The release configuration builds Release rather than RelWithDebInfo — it
    // is the thing that actually ships, and R1 targets the macro branch that
    // only a build without CULTMESH_QUIC_DEBUG_ASSERTS ever compiles.
    const buildType = configuration.buildType ?? "RelWithDebInfo";
    // Multi-config generators (MSVC) put the binaries in a per-config directory;
    // single-config ones (Ninja, Makefiles) do not.
    const binaries = platform === "win32-x64"
      ? join(build, "bin", buildType)
      : join(build, "bin");
    try {
      execFileSync("cmake", [
        "-S", source, "-B", build,
        // The same generator scripts/build-quic-native.sh uses. CMake's Linux
        // default is Makefiles, and the container the bridge is built in carries
        // ninja rather than make, so the default configured nothing at all.
        ...(platform === "linux-x64" ? ["-G", "Ninja"] : []),
        `-DCMAKE_BUILD_TYPE=${buildType}`,
        `-DCMAKE_CXX_FLAGS=${flags}`,
        `-DCMAKE_EXE_LINKER_FLAGS=${flags}`,
        `-DCMAKE_SHARED_LINKER_FLAGS=${flags}`,
        "-DCULTMESH_QUIC_BUILD_TESTS=ON",
        `-DCULTMESH_QUIC_DEBUG_ASSERTS=${configuration.asserts}`,
        ...msquic.configure,
      ], { cwd: repoRoot, stdio: ["ignore", "pipe", "pipe"] });
      execFileSync("cmake", ["--build", build, "--config", buildType],
        { cwd: repoRoot, stdio: ["ignore", "pipe", "pipe"] });
    } catch (error) {
      // A mutation that does not compile is killed by the build, which is a
      // legitimate kill: the rule it removed was load-bearing to the language.
      nativeFailure = `${configuration.name}: the build failed\n${childOutput(error)}`;
      return false;
    }
    for (const [from, name] of msquic.runtime) writeFileSync(join(binaries, name), readFileSync(from));
    const runner = join(binaries,
      platform === "win32-x64" ? "cultmesh_quic_native_tests.exe" : "cultmesh_quic_native_tests");
    for (const scenario of configuration.scenarios) {
      const [command, commandArguments] = scenarioCommand(runner, scenario, configuration);
      try {
        execFileSync(command, commandArguments, {
          cwd: binaries,
          stdio: ["ignore", "pipe", "pipe"],
          // A held poller plus a stopped close is the shape a broken mutant
          // leaves behind, and it hangs rather than failing. Bounded well above
          // the scenarios' own seconds so only a hang reaches it.
          timeout: 180000,
          // Without this a reported race is printed and the run still exits 0,
          // so every sanitizer mutation would survive.
          env: { ...process.env, TSAN_OPTIONS: "halt_on_error=1", ASAN_OPTIONS: "halt_on_error=1" },
        });
      } catch (error) {
        // A scenario the environment cannot run is not a kill, and reporting it
        // as one is how a whole configuration goes quiet. It stops the run.
        const unrunnable = explainUnrunnable(error, configuration, runner, scenario);
        if (unrunnable) throw new Error(unrunnable);
        nativeFailure = `${configuration.name}: ${scenario.join(" ")}\n${childOutput(error)}`;
        return false;
      }
    }
  }
  return true;
}

function occurrences(text, needle) {
  return text.split(needle).length - 1;
}

/**
 * Repairs a file left mutated by a run that died before its `finally`, and only
 * that.
 *
 * A SIGKILL leaves the sidecar behind. The repair used to write the sidecar over
 * whatever the file now held, which is right after a kill and destructive after
 * one: a fix made to the source between the kill and the next run was silently
 * reverted to bytes from before the kill, and the run then reported on code
 * nobody had written.
 *
 * So repair only what this script can prove it wrote. The mutation table is the
 * record: every file state a live run can leave behind is either the original
 * (the sidecar, also what the no-op control writes) or the original with exactly
 * one of that target's mutations applied. Anything else — a hand edit, a
 * half-written file, a different branch — is somebody else's, and the run stops
 * with both digests rather than guessing.
 */
function repair(name, target, own, sidecar) {
  const original = readFileSync(sidecar);
  const originalText = original.toString("utf8");
  const eol = originalText.includes("\r\n") ? "\r\n" : "\n";
  const withEol = text => text.replace(/\r?\n/g, eol);
  const current = readFileSync(target.file);
  const currentDigest = digest(current);
  const originalDigest = digest(original);

  if (currentDigest === originalDigest || currentDigest === digest(Buffer.from(originalText, "utf8"))) {
    unlinkSync(sidecar);
    console.log(`${target.file} is already the original; dropped the stale ${sidecar}`);
    return;
  }

  const currentText = current.toString("utf8");
  const mutant = own.find(mutation =>
    originalText.replace(withEol(mutation.old), withEol(mutation.new)) === currentText);
  if (!mutant) {
    throw new Error(
      `${target.file} is neither the original nor any mutation of target '${name}', so a previous run did not write it.\n` +
      `  ${target.file} sha256=${currentDigest}\n` +
      `  ${sidecar} sha256=${originalDigest}\n` +
      "  Refusing to overwrite it. Reconcile the file by hand, then delete the sidecar.",
    );
  }
  writeFileSync(target.file, original);
  unlinkSync(sidecar);
  console.log(`repaired ${target.file} from ${sidecar}: a previous run stopped inside '${mutant.rule}'`);
}

const results = [];
let failed = false;

// Named targets run only those; no argument runs all of them. The native target
// is built and run inside the Debian 13 container for its linux-x64 entries,
// where the TypeScript workspaces have no installed dependencies, so being able
// to ask for one target is what makes that run possible at all.
const requested = process.argv.slice(2);
for (const name of requested) {
  if (!(name in targets)) throw new Error(`unknown target '${name}'; known: ${Object.keys(targets).join(", ")}`);
}

for (const [name, target] of Object.entries(targets)) {
  if (requested.length > 0 && !requested.includes(name)) continue;
  const own = mutations.filter(mutation => (mutation.target ?? "shared") === name);
  if (own.length === 0) throw new Error(`target '${name}' has no mutations`);
  const sidecar = `${target.file}.mutation-original`;
  if (existsSync(sidecar)) repair(name, target, own, sidecar);

  const original = readFileSync(target.file);
  const originalText = original.toString("utf8");
  const originalDigest = digest(original);

  // Anchors are written with "\n"; the checkout may be CRLF (autocrlf). Match
  // the file's own line ending so a multi-line anchor cannot silently miss.
  const eol = originalText.includes("\r\n") ? "\r\n" : "\n";
  const withEol = text => text.replace(/\r?\n/g, eol);

  const restore = () => {
    writeFileSync(target.file, original);
    const restored = digest(readFileSync(target.file));
    if (restored !== originalDigest) {
      throw new Error(`restore failed: ${target.file} digest ${restored} != ${originalDigest}`);
    }
  };

  // A target with a compiled `dist/` carries a sentinel so the post-run rebuild
  // check can tell a rebuilt output from one still holding the last mutant. The
  // native target has no dist/ and so has neither.
  if (target.dist) {
    const last = own[own.length - 1];
    if (!originalText.includes(target.sentinel) ||
        originalText.replace(withEol(last.old), withEol(last.new)).includes(target.sentinel)) {
      throw new Error(
        `the sentinel '${target.sentinel}' must be in ${target.file} and removed by its last mutation ('${last.rule}')`,
      );
    }
  }

  const check = target.check ?? (() => testsPass(target.workspace));

  writeFileSync(sidecar, original);
  try {
    writeFileSync(target.file, Buffer.from(originalText, "utf8"));
    nativeFailure = null;
    const controlPass = check();
    results.push({ target: name, outcome: controlPass ? "green" : "RED", rule: "control (no-op rewrite)" });
    // A red control is the harness failing, not a rule failing, and it used to
    // arrive with nothing but the colour. Whatever the child said is the
    // diagnosis, so it is printed where it happens rather than discarded.
    if (!controlPass) {
      failed = true;
      console.error(`control RED [${name}]: the unmutated source does not pass its own check`);
      if (nativeFailure) console.error(nativeFailure);
    }
    else {
      for (const mutation of own) {
        const [old, replacement] = [withEol(mutation.old), withEol(mutation.new)];
        const count = occurrences(originalText, old);
        if (count !== 1) {
          throw new Error(`anchor for '${mutation.rule}' matched ${count} times in ${target.file}, expected exactly 1`);
        }
        // A mutation this platform cannot see is not run. Reporting it as killed
        // from a run that never exercised it is the honesty failure these marks
        // exist to prevent; the anchor is still checked above, so a rule whose
        // spelling has moved fails here rather than going quiet.
        if (mutation.honestOn && !mutation.honestOn.includes(platform)) {
          results.push({
            target: name,
            outcome: "skipped",
            rule: `${mutation.rule} — honest only on ${mutation.honestOn.join(", ")}, not ${platform}`,
          });
          continue;
        }
        writeFileSync(target.file, Buffer.from(originalText.replace(old, replacement), "utf8"));
        const pass = check();
        restore();
        results.push({ target: name, outcome: pass ? "SURVIVED" : "killed", rule: mutation.rule });
        if (pass) failed = true;
      }
    }
  } finally {
    // A verified restore is what the sidecar exists to guarantee, so it goes
    // here and not after the try: a throw mid-run used to leave the sidecar
    // behind, and the next run "repaired" the file from bytes that were already
    // stale, silently undoing whatever had been fixed in between.
    restore();
    unlinkSync(sidecar);
  }

  if (!target.dist) {
    // This digest says the file this run started with is the file it ended with.
    // It is not an identity for the source: `text=auto` gives a Windows checkout
    // carriage returns, so two clones of the same commit restore to two
    // different digests. The identity that survives a clone is the commit, or
    // `git rev-parse HEAD:<path>` for one file.
    results.push({ target: name, outcome: "restored", rule: `${target.file} sha256=${originalDigest}` });
    continue;
  }

  // Every test run above rebuilt dist/ from a mutant; put the restored source back in it.
  npm(target.workspace, "run", "build");
  if (!readFileSync(target.dist, "utf8").includes(target.sentinel)) {
    throw new Error(`${target.dist} does not contain '${target.sentinel}' after the rebuild; it still carries a mutant`);
  }
  results.push({
    target: name,
    outcome: "restored",
    rule: `${target.file} sha256=${originalDigest}, rebuilt ${target.dist}`,
  });
}

console.log(`build host and target: ${platform}`);
for (const result of results) console.log(`${result.outcome.padEnd(9)} [${result.target}] ${result.rule}`);

// A run that skipped entries has not covered those rules, and exiting 0 lets a
// gate read a win32-x64 run — where every ThreadSanitizer rule is skipped — as
// proof of something it never touched. The status says which of the three
// outcomes the run reached.
const counted = outcome => results.filter(result => result.outcome === outcome).length;
const [killed, survived, skipped] = [counted("killed"), counted("SURVIVED"), counted("skipped")];
console.log(`${killed} killed, ${survived} survived, ${skipped} skipped on ${platform}`);
if (skipped > 0) {
  console.log(
    `${skipped} rule(s) have no kill on ${platform} and were not exercised by this run; ` +
    "run the native target on the platform each names to cover them.");
}
process.exit(failed ? 1 : (skipped > 0 ? 2 : 0));
