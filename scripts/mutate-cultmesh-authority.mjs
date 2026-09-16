// Mutation check for the CultMesh route verification rules.
//
// Two files own those rules and each is one target here. `shared` is
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
//   node scripts/mutate-cultmesh-authority.mjs

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

const results = [];
let failed = false;

for (const [name, target] of Object.entries(targets)) {
  const own = mutations.filter(mutation => (mutation.target ?? "shared") === name);
  if (own.length === 0) throw new Error(`target '${name}' has no mutations`);
  const sidecar = `${target.file}.mutation-original`;
  if (existsSync(sidecar)) {
    writeFileSync(target.file, readFileSync(sidecar));
    unlinkSync(sidecar);
    console.log(`repaired ${target.file} from ${sidecar}: a previous run stopped before restoring it`);
  }

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
