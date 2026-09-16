// Mutation check for the CultMesh rules TypeScript owns: route verification and
// the realtime frame codec.
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
//   node scripts/mutate-cultmesh.mjs

import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, unlinkSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const npmCli = process.env.npm_execpath ??
  join(dirname(process.execPath), "node_modules", "npm", "bin", "npm-cli.js");

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
  // Not listed: removing `payloadLength < 0` from the decoder's length refusal.
  // The clause mirrors the reference but cannot fire on its own: a negative
  // payload length makes the sum smaller than the frame, and reaching the sum
  // check at all means the frame is already at least 37 bytes, so the sum can
  // never match while the length is negative. The mutant is equivalent, and it
  // survived when tried.
  { target: "realtime", rule: "decode refuses a length sum that misses the frame", old: "  if (payloadLength < 0 || expected !== bytes.byteLength) throw", new: "  if (payloadLength < 0) throw" },
  { target: "realtime", rule: "decode keeps a leading byte-order mark, as Encoding.UTF8.GetString does", old: 'const decoder = new TextDecoder("utf-8", { ignoreBOM: true });', new: "const decoder = new TextDecoder();" },
  { target: "realtime", rule: "decode reads exactly the viewed bytes of an offset view", old: "  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);", new: "  const view = new DataView(bytes.buffer);" },
  { target: "realtime", rule: "the decoded payload does not alias the frame it came from", old: "    payload: bytes.slice(offset, offset + payloadLength),", new: "    payload: bytes.subarray(offset, offset + payloadLength)," },
  // Last, because it removes this target's sentinel.
  { target: "realtime", rule: "the frame magic is 0x31545343", old: "const MAGIC = 0x31545343;", new: "const MAGIC = 0x31545344;" },
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

for (const [name, target] of Object.entries(targets)) {
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

  const last = own[own.length - 1];
  if (!originalText.includes(target.sentinel) ||
      originalText.replace(withEol(last.old), withEol(last.new)).includes(target.sentinel)) {
    throw new Error(
      `the sentinel '${target.sentinel}' must be in ${target.file} and removed by its last mutation ('${last.rule}')`,
    );
  }

  writeFileSync(sidecar, original);
  try {
    writeFileSync(target.file, Buffer.from(originalText, "utf8"));
    const controlPass = testsPass(target.workspace);
    results.push({ target: name, outcome: controlPass ? "green" : "RED", rule: "control (no-op rewrite)" });
    if (!controlPass) failed = true;
    else {
      for (const mutation of own) {
        const [old, replacement] = [withEol(mutation.old), withEol(mutation.new)];
        const count = occurrences(originalText, old);
        if (count !== 1) {
          throw new Error(`anchor for '${mutation.rule}' matched ${count} times in ${target.file}, expected exactly 1`);
        }
        writeFileSync(target.file, Buffer.from(originalText.replace(old, replacement), "utf8"));
        const pass = testsPass(target.workspace);
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

for (const result of results) console.log(`${result.outcome.padEnd(9)} [${result.target}] ${result.rule}`);
process.exit(failed ? 1 : 0);
