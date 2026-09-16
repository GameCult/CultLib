// Mutation check for the shared CultMesh route verifier.
//
// Each rule the cut map names for `packages/cultnet-ts/src/cultmesh-authority.ts`
// gets one mutation. A mutation is killed when `npm run test --workspace
// packages/cultnet-ts` fails with it applied; a survivor means no test pins that
// rule. The file is rewritten as UTF-8 bytes and restored from the original
// bytes with a digest check, never from git. A no-op control runs first through
// the same write path so a false kill from the write itself cannot hide.
//
//   node scripts/mutate-cultmesh-authority.mjs

import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const target = join(repoRoot, "packages", "cultnet-ts", "src", "cultmesh-authority.ts");
const npmCli = process.env.npm_execpath ??
  join(dirname(process.execPath), "node_modules", "npm", "bin", "npm-cli.js");

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
    old: "candidate => candidate.keyId === certificate.odinKeyId",
    new: "candidate => true",
  },
  {
    rule: "route transcript field order",
    old: "    route.verseId,\n    route.authorityRuntimeId,\n",
    new: "    route.authorityRuntimeId,\n    route.verseId,\n",
  },
  {
    rule: "session transcript binds the nonce",
    old: "    request.clientNonce,\n",
    new: "",
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

const original = readFileSync(target);
const originalText = original.toString("utf8");
const originalDigest = digest(original);

function digest(bytes) {
  return createHash("sha256").update(bytes).digest("hex");
}

function testsPass() {
  try {
    execFileSync(process.execPath, [npmCli, "run", "test", "--workspace", "packages/cultnet-ts"], {
      cwd: repoRoot,
      stdio: ["ignore", "pipe", "pipe"],
    });
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

try {
  writeFileSync(target, Buffer.from(originalText, "utf8"));
  const controlPass = testsPass();
  results.push({ rule: "control (no-op rewrite)", outcome: controlPass ? "green" : "RED" });
  if (!controlPass) failed = true;
  else {
    for (const mutation of mutations) {
      const count = occurrences(originalText, mutation.old);
      if (count !== 1) {
        throw new Error(`anchor for '${mutation.rule}' matched ${count} times, expected exactly 1`);
      }
      writeFileSync(target, Buffer.from(originalText.replace(mutation.old, mutation.new), "utf8"));
      const pass = testsPass();
      restore();
      results.push({ rule: mutation.rule, outcome: pass ? "SURVIVED" : "killed" });
      if (pass) failed = true;
    }
  }
} finally {
  restore();
}

for (const result of results) console.log(`${result.outcome.padEnd(9)} ${result.rule}`);
console.log(`restored ${target} sha256=${originalDigest}`);
process.exit(failed ? 1 : 0);
