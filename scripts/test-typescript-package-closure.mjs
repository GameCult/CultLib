import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const workspacePackages = ["cultcache-ts", "cultnet-ts", "cultmesh-ts", "cultmesh-browser"];
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
    if (packageName === "cultmesh-ts") {
      // The committed native binaries only reach a consumer if `files` (and
      // therefore the packed tarball) actually carries them; a fresh clone,
      // an `npm pack` tarball and the deploy `git archive` all depend on it.
      // Assert on every host: the tarball carries both platforms, and the
      // host only decides which one loads.
      const requiredNativeFiles = [
        "native/win32-x64/gamecult_mesh_quic_native.dll",
        "native/win32-x64/msquic.dll",
        "native/linux-x64/libgamecult_mesh_quic_native.so",
        "native/linux-x64/libmsquic.so.2",
        "native/SHA256SUMS",
        "native/MANIFEST.txt",
        "native/MSQUIC-LICENSE.txt",
      ];
      for (const requiredNativeFile of requiredNativeFiles) {
        assert.ok(files.includes(requiredNativeFile), `cultmesh-ts tarball is missing ${requiredNativeFile}`);
      }
    }
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
const cache = require("@gamecult/cultcache-ts");
const documentEntry = require("@gamecult/cultcache-ts/document");
const net = require("cultnet-ts");
const mesh = require("cultmesh-ts");
assert.equal(typeof cache.defineDocumentType, "function");
// The document owner is also an entry of its own, which the browser client imports without the Node-only cache.
assert.equal(documentEntry.decodeDocumentPayload, cache.decodeDocumentPayload);
// No entry exports a function that reads a payload around its schema, whatever it is called, wherever it is mounted: the
// package-internal readers are loaded from the packed dist files by path and looked for by identity in every packed
// entry (own names, 'default', and the members of any namespace or function export), and every exported function is
// shown a MessagePack map and a slot record and must not hand either back decoded. The only reader allowed is the schema-checked owner.
const path = require("node:path");
const msgpack = require("@msgpack/msgpack");
const dist = path.dirname(require.resolve("@gamecult/cultcache-ts"));
const internalReaders = new Map([
  ["readWire", require(path.join(dist, "wire.js")).readWire],
  ["resolveDocumentFormatter", require(path.join(dist, "document-formatter.js")).resolveDocumentFormatter],
  ["@msgpack/msgpack decode", msgpack.decode],
  ["@msgpack/msgpack decodeMulti", msgpack.decodeMulti],
]);
for (const [name, reader] of internalReaders) assert.equal(typeof reader, "function", name + " was found in the packed package");
const inspection = require("@gamecult/cultcache-ts/inspection");
const entries = { root: cache, codecs: cache.codecs, document: documentEntry, inspection };
// A map and a slot record (an array), each with the text its decoded form prints as: a reader of either shape gives itself away.
const probes = [{ probe: 7 }, ["probe", 7]].map((value) => [msgpack.encode(value), JSON.stringify(value)]);
const exportsOf = (entry) => {
  const found = [];
  const seen = new Set();
  const visit = (value, where, depth) => {
    if ((typeof value !== "function" && (typeof value !== "object" || value === null)) || seen.has(value) || depth > 4) return;
    seen.add(value);
    found.push([where, value]);
    for (const key of Object.getOwnPropertyNames(value)) {
      if (typeof value === "function" && ["length", "name", "prototype", "caller", "arguments"].includes(key)) continue;
      let member;
      try { member = value[key]; } catch { continue; }
      visit(member, where + "." + key, depth + 1);
    }
  };
  for (const key of Object.getOwnPropertyNames(entry)) visit(entry[key], key, 0);
  return found;
};
for (const [entryName, entry] of Object.entries(entries)) {
  for (const [where, value] of exportsOf(entry)) {
    for (const [name, reader] of internalReaders) {
      assert.notEqual(value, reader, entryName + " entry exports " + name + " as " + where);
    }
    if (typeof value !== "function" || value === cache.decodeDocumentPayload) continue;
    for (const [probePayload, decodedText] of probes) {
      let shown;
      try {
        shown = JSON.stringify(value(probePayload), (_key, member) => (member instanceof Map ? Object.fromEntries(member) : member));
      } catch { continue; }
      assert.ok(!shown?.includes(decodedText), entryName + " entry exports " + where + ", which reads a payload without its schema");
    }
  }
}
assert.equal(typeof documentEntry.checkPayloadDepth, "function");
assert.equal(documentEntry.MAX_PAYLOAD_DEPTH, 256);
assert.equal(cache.MAX_PAYLOAD_DEPTH, 256);
assert.equal(documentEntry.CultCachePayloadDepthError, cache.CultCachePayloadDepthError);
assert.equal(typeof net.CultNetPeer, "function");
assert.equal(typeof mesh.CultMesh, "function");
assert.equal(typeof mesh.CultMeshProviderSession, "function");
assert.deepEqual(mesh.CultMesh.vec2(3, 4), { x: 3, y: 4 });
`);
  execFileSync(process.execPath, ["runtime-smoke.cjs"], { cwd: consumerRoot, stdio: "inherit" });

  writeFileSync(join(consumerRoot, "browser-runtime-smoke.mjs"), `
import assert from "node:assert/strict";
import { CultMeshBrowserClient, decodeCultNetPayload } from "cultmesh-browser";
import { parseCultNetMessage } from "cultnet-ts/contracts";
assert.equal(typeof CultMeshBrowserClient.connect, "function");
assert.equal(typeof decodeCultNetPayload, "function");
assert.equal(typeof parseCultNetMessage, "function");
assert.equal("putDocument" in CultMeshBrowserClient.prototype, false);
`);
  execFileSync(process.execPath, ["browser-runtime-smoke.mjs"], { cwd: consumerRoot, stdio: "inherit" });

  writeFileSync(join(consumerRoot, "types-smoke.ts"), `
import { defineDocumentType } from "@gamecult/cultcache-ts";
import { decodeDocumentPayload, type DocumentPayload } from "@gamecult/cultcache-ts/document";
import { CultNetPeer } from "cultnet-ts";
import {
  CultMesh,
  CultMeshProviderSession,
  type CultMeshProviderIdentity,
  type CultMeshProviderTransport,
  type CultMeshVec2,
} from "cultmesh-ts";

const point: CultMeshVec2 = CultMesh.vec2(3, 4);
const defineDocument: typeof defineDocumentType = defineDocumentType;
const decodeOwner: typeof decodeDocumentPayload = decodeDocumentPayload;
let opaque: DocumentPayload | undefined;
const peerType: typeof CultNetPeer = CultNetPeer;
const providerSessionType: typeof CultMeshProviderSession = CultMeshProviderSession;
const providerIdentity: CultMeshProviderIdentity = {
  providerId: "smoke.provider",
  serviceInstanceId: "smoke.instance",
  endpointId: "odin:smoke.instance",
  verseId: "smoke",
};
let providerTransport: CultMeshProviderTransport | undefined;
void point;
void defineDocument;
void decodeOwner;
void opaque;
void peerType;
void providerSessionType;
void providerIdentity;
void providerTransport;
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

  writeFileSync(join(consumerRoot, "browser-types-smoke.mts"), `
import { decodeDocumentPayload } from "@gamecult/cultcache-ts/document";
import {
  CultMeshBrowserClient,
  type CultMeshBrowserIdentity,
  type CultMeshRawDocumentLeaseOptions,
} from "cultmesh-browser";

const identity: CultMeshBrowserIdentity = {
  verseId: "smoke",
  authorityRuntimeId: "smoke.authority",
};
const lease: CultMeshRawDocumentLeaseOptions = {
  schemaId: "smoke.document.v1",
  recordKey: "smoke:main",
};
const clientType: typeof CultMeshBrowserClient = CultMeshBrowserClient;
const decodeOwner: typeof decodeDocumentPayload = decodeDocumentPayload;
void identity;
void lease;
void clientType;
void decodeOwner;
`);
  execFileSync(process.execPath, [
    tsc,
    "--noEmit",
    "--strict",
    "--target", "ES2022",
    "--module", "NodeNext",
    "--moduleResolution", "NodeNext",
    "--skipLibCheck",
    "browser-types-smoke.mts",
  ], { cwd: consumerRoot, stdio: "inherit" });

  // The formatter that reads a payload around its schema is in no entry: importing it from the document entry does not compile.
  writeFileSync(join(consumerRoot, "formatter-refused.mts"), `
// @ts-expect-error resolveDocumentFormatter is not exported from the document entry
import { resolveDocumentFormatter } from "@gamecult/cultcache-ts/document";
void resolveDocumentFormatter;
import { codecs } from "@gamecult/cultcache-ts";
// @ts-expect-error codecFormatter is not exported from the document entry
import { codecFormatter } from "@gamecult/cultcache-ts/document";
void codecFormatter;
// @ts-expect-error codecFormatter is not a member of the root's codecs namespace
void codecs.codecFormatter;
`);
  execFileSync(process.execPath, [
    tsc,
    "--noEmit",
    "--strict",
    "--target", "ES2022",
    "--module", "NodeNext",
    "--moduleResolution", "NodeNext",
    "--skipLibCheck",
    "formatter-refused.mts",
  ], { cwd: consumerRoot, stdio: "inherit" });

  console.log("TypeScript package closure smoke: passed");
  for (const tarball of tarballs) console.log(`Packed: ${tarball}`);
} finally {
  rmSync(smokeRoot, { recursive: true, force: true });
}
