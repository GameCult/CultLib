import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const workspacePackages = ["cultcache-ts", "cultnet-ts", "cultmesh-ts"];
const npmCli = process.env.npm_execpath ??
  join(dirname(process.execPath), "node_modules", "npm", "bin", "npm-cli.js");
const childEnv = process.platform === "win32"
  ? {
      ...process.env,
      PATH: `${dirname(process.execPath)};${process.env.SystemRoot}\\System32;${process.env.SystemRoot}`,
    }
  : process.env;
const smokeRoot = mkdtempSync(join(tmpdir(), "cultlib-ts-package-smoke-"));
const tarballRoot = join(smokeRoot, "tarballs");
const consumerRoot = join(smokeRoot, "consumer");
mkdirSync(tarballRoot);
mkdirSync(consumerRoot);

function npm(...args) {
  return execFileSync(process.execPath, [npmCli, ...args], {
    cwd: repoRoot,
    encoding: "utf8",
    env: childEnv,
    stdio: ["ignore", "pipe", "inherit"],
  });
}

try {
  const tarballs = [];
  for (const packageName of workspacePackages) {
    const packageRoot = join(repoRoot, "packages", packageName);
    const packed = JSON.parse(npm("pack", packageRoot, "--json", "--pack-destination", tarballRoot));
    assert.equal(packed.length, 1, `${packageName} must produce exactly one tarball`);
    const files = packed[0].files.map(file => file.path);
    assert.ok(files.includes("dist/index.js"), `${packageName} tarball is missing dist/index.js`);
    assert.ok(files.includes("dist/index.d.ts"), `${packageName} tarball is missing dist/index.d.ts`);
    tarballs.push(join(tarballRoot, packed[0].filename));
  }

  writeFileSync(join(consumerRoot, "package.json"), JSON.stringify({
    name: "cultlib-ts-package-smoke",
    version: "1.0.0",
    private: true,
  }, null, 2));

  execFileSync(process.execPath, [npmCli,
    "install",
    "--ignore-scripts",
    "--no-audit",
    "--no-fund",
    ...tarballs,
  ], { cwd: consumerRoot, env: childEnv, stdio: "inherit" });

  writeFileSync(join(consumerRoot, "runtime-smoke.cjs"), `
const assert = require("node:assert/strict");
const cache = require("cultcache-ts");
const net = require("cultnet-ts");
const mesh = require("cultmesh-ts");
assert.equal(typeof cache.defineDocumentType, "function");
assert.equal(typeof net.CultNetPeer, "function");
assert.equal(typeof mesh.CultMesh, "function");
assert.deepEqual(mesh.CultMesh.vec2(3, 4), { x: 3, y: 4 });
`);
  execFileSync(process.execPath, ["runtime-smoke.cjs"], { cwd: consumerRoot, stdio: "inherit" });

  writeFileSync(join(consumerRoot, "types-smoke.ts"), `
import { defineDocumentType } from "cultcache-ts";
import { CultNetPeer } from "cultnet-ts";
import { CultMesh, type CultMeshVec2 } from "cultmesh-ts";

const point: CultMeshVec2 = CultMesh.vec2(3, 4);
const defineDocument: typeof defineDocumentType = defineDocumentType;
const peerType: typeof CultNetPeer = CultNetPeer;
void point;
void defineDocument;
void peerType;
`);
  const tsc = join(repoRoot, "node_modules", "typescript", "bin", "tsc");
  assert.ok(readFileSync(tsc, "utf8").length > 0, "workspace TypeScript compiler is unavailable");
  execFileSync(process.execPath, [
    tsc,
    "--noEmit",
    "--strict",
    "--target", "ES2022",
    "--module", "CommonJS",
    "--moduleResolution", "Node",
    "--skipLibCheck",
    "types-smoke.ts",
  ], { cwd: consumerRoot, stdio: "inherit" });

  console.log("TypeScript package closure smoke: passed");
  for (const tarball of tarballs) console.log(`Packed: ${tarball}`);
} finally {
  rmSync(smokeRoot, { recursive: true, force: true });
}
