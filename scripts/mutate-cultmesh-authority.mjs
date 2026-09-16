// Mutation check for the shared CultMesh route verifier.
//
// Each rule the cut map names for `packages/cultnet-ts/src/cultmesh-authority.ts`
// gets one mutation. A mutation is killed when `npm run test --workspace
// packages/cultnet-ts` fails with it applied; a survivor means no test pins that
// rule. The file is rewritten as UTF-8 bytes and restored from the original
// bytes with a digest check, never from git. A no-op control runs first through
// the same write path so a false kill from the write itself cannot hide.
//
// The original bytes also live in `<target>.mutation-original` from the first
// write until a verified restore; a run killed mid-mutation is repaired from
// that sidecar by the next run, not by git. After the final restore the
// package is rebuilt so `dist/` never carries the last mutant.
//
//   node scripts/mutate-cultmesh-authority.mjs

import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, unlinkSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const target = join(repoRoot, "packages", "cultnet-ts", "src", "cultmesh-authority.ts");
const sidecar = `${target}.mutation-original`;
const dist = join(repoRoot, "packages", "cultnet-ts", "dist", "cultmesh-authority.js");
const npmCli = process.env.npm_execpath ??
  join(dirname(process.execPath), "node_modules", "npm", "bin", "npm-cli.js");
// Present in the restored source and compiled verbatim into dist/; the last
// mutation in the list removes it, which is what makes the post-rebuild check
// distinguish a rebuilt dist/ from one still carrying that mutant.
const sentinel = 'scheme === "https"';

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
    rule: "protected scheme: https",
    old: 'scheme === "https" || ',
    new: "",
  },
];

if (existsSync(sidecar)) {
  writeFileSync(target, readFileSync(sidecar));
  unlinkSync(sidecar);
  console.log(`repaired ${target} from ${sidecar}: a previous run stopped before restoring it`);
}

const original = readFileSync(target);
const originalText = original.toString("utf8");
const originalDigest = digest(original);

// Anchors are written with "\n"; the checkout may be CRLF (autocrlf). Match
// the file's own line ending so a multi-line anchor cannot silently miss.
const eol = originalText.includes("\r\n") ? "\r\n" : "\n";
const withEol = text => text.replace(/\r?\n/g, eol);

function digest(bytes) {
  return createHash("sha256").update(bytes).digest("hex");
}

function npm(...args) {
  execFileSync(process.execPath, [npmCli, ...args, "--workspace", "packages/cultnet-ts"], {
    cwd: repoRoot,
    stdio: ["ignore", "pipe", "pipe"],
  });
}

function testsPass() {
  try {
    npm("run", "test");
    return true;
  } catch {
    return false;
  }
}

function restore() {
  writeFileSync(target, original);
  const restored = digest(readFileSync(target));
  if (restored !== originalDigest) {
    throw new Error(`restore failed: ${target} digest ${restored} != ${originalDigest}`);
  }
}

function occurrences(text, needle) {
  return text.split(needle).length - 1;
}

const results = [];
let failed = false;

const last = mutations[mutations.length - 1];
if (!originalText.includes(sentinel) || originalText.replace(withEol(last.old), withEol(last.new)).includes(sentinel)) {
  throw new Error(`the sentinel '${sentinel}' must be in the source and removed by the last mutation ('${last.rule}')`);
}

writeFileSync(sidecar, original);
try {
  writeFileSync(target, Buffer.from(originalText, "utf8"));
  const controlPass = testsPass();
  results.push({ rule: "control (no-op rewrite)", outcome: controlPass ? "green" : "RED" });
  if (!controlPass) failed = true;
  else {
    for (const mutation of mutations) {
      const [old, replacement] = [withEol(mutation.old), withEol(mutation.new)];
      const count = occurrences(originalText, old);
      if (count !== 1) {
        throw new Error(`anchor for '${mutation.rule}' matched ${count} times, expected exactly 1`);
      }
      writeFileSync(target, Buffer.from(originalText.replace(old, replacement), "utf8"));
      const pass = testsPass();
      restore();
      results.push({ rule: mutation.rule, outcome: pass ? "SURVIVED" : "killed" });
      if (pass) failed = true;
    }
  }
} finally {
  restore();
}
unlinkSync(sidecar);

// Every test run above rebuilt dist/ from a mutant; put the restored source back in it.
npm("run", "build");
if (!readFileSync(dist, "utf8").includes(sentinel)) {
  throw new Error(`${dist} does not contain '${sentinel}' after the rebuild; it still carries a mutant`);
}

for (const result of results) console.log(`${result.outcome.padEnd(9)} ${result.rule}`);
console.log(`restored ${target} sha256=${originalDigest}`);
console.log(`rebuilt ${dist}`);
process.exit(failed ? 1 : 0);
