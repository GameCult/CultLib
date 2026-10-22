// https-shape-parity across three OS processes. The TS provider (content-https-provider) serves over TLS; the TS reader
// (content-https-reader) and the C# transfer service over the unmodified HTTPS connector (CultMeshHttpsContentInteropTests)
// each fetch from it. Each consumer decides its own acceptance; this file only observes outcomes and judges what the provider saw.
// The provider's source is the cultmesh-ts package, so cultmesh-ts must be built first (scripts/run-typescript-workspaces.mjs order).

import test from "node:test";
import assert from "node:assert/strict";
import { execFile, execFileSync, spawn } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { createInterface } from "node:readline";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { EMPTY_SHA256, encodeManifest, packContent } from "cultnet-ts/content";

const execFileAsync = promisify(execFile);
const here = dirname(fileURLToPath(import.meta.url)); // dist-test/test/interop
const packageRoot = resolve(here, "../../..");
const repoRoot = resolve(packageRoot, "../..");
const fixtures = join(packageRoot, "test/fixtures");
const keyPem = join(fixtures, "content-https-test-key.pem");
const certPem = join(fixtures, "content-https-test-cert.pem");
// A chunk recordKey with every character Uri.EscapeDataString treats specially: ! ' ( ) * (which encodeURIComponent leaves bare), a space,
// a non-ASCII character and a lone percent sign. RecordKey is a free string on a chunk ref, and the connector sends it verbatim into the query.
const escapeKey = "k!'()* é%41+~";
const escapedKey = "k%21%27%28%29%2A%20%C3%A9%2541%2B~";
const dotnet = process.env.DOTNET ?? "dotnet";

const haveDotnet = (() => { try { execFileSync(dotnet, ["--version"], { stdio: "ignore" }); return true; } catch { return false; } })();
const skip = !haveDotnet && !process.env.CI;

/** xorshift32 with a fixed seed: non-periodic, so every chunk of a multi-chunk body has its own hash (map F32). */
function xorshiftBytes(length: number, seed: number): Uint8Array {
  const bytes = new Uint8Array(length);
  let state = seed >>> 0;
  for (let i = 0; i < length; i++) { state ^= state << 13; state >>>= 0; state ^= state >>> 17; state ^= state << 5; state >>>= 0; bytes[i] = state & 255; }
  return bytes;
}

type Line = { phase?: string; mount?: string; url?: string; fault?: string; status?: string; endpoints?: Record<string, string>; ack?: string };
type ReaderResult = { case: string; ok: boolean; sha256?: string; code?: string; status?: number };

function startProvider(args: string[]) {
  const child = spawn(process.execPath, [join(here, "content-https-provider.js"), ...args], { stdio: ["pipe", "pipe", "inherit"] });
  const lines: Line[] = [];
  const waiting: Array<() => void> = [];
  createInterface({ input: child.stdout }).on("line", text => { lines.push(JSON.parse(text) as Line); waiting.splice(0).forEach(wake => wake()); });
  const exited = new Promise<number | null>(resolveExit => child.once("exit", code => resolveExit(code)));
  const next = async (match: (line: Line) => boolean, what: string): Promise<Line> => {
    const deadline = Date.now() + 30_000;
    for (;;) {
      const found = lines.find(match);
      if (found) return found;
      if (Date.now() > deadline) throw new Error(`provider did not report ${what} in 30 s`);
      await new Promise<void>(wake => { waiting.push(wake); setTimeout(wake, 250); });
    }
  };
  return { child, lines, exited, next };
}

async function runDotnetTestFilter(projectPath: string, testName: string, env: NodeJS.ProcessEnv): Promise<string> {
  try {
    const { stdout } = await execFileAsync(dotnet, ["test", projectPath, "--filter", `FullyQualifiedName~${testName}`, "--logger", "console;verbosity=normal", "-nologo"],
      { cwd: repoRoot, env: { ...process.env, ...env }, timeout: 600_000 });
    return stdout;
  } catch (error) {
    const failure = error as { stdout?: string; stderr?: string; message: string };
    throw new Error(`dotnet test ${testName} failed.\nstdout:\n${failure.stdout ?? ""}\nstderr:\n${failure.stderr ?? failure.message}`);
  }
}

test("the TS provider serves both consumers: parity, refusal and the fault log", { skip, timeout: 900_000 }, async () => {
  const root = mkdtempSync(join(tmpdir(), "cultmesh-content-https-"));
  const store = join(root, "store"), corrupt = join(root, "corrupt"), manifests = join(root, "manifests");
  for (const dir of [store, corrupt, manifests]) mkdirSync(dir);
  const createdAtUtc = "2026-10-19T00:00:00.0000000+00:00";
  const multi = await packContent("c4/multi", xorshiftBytes(229_376, 0x9e3779b9), { chunkSizeBytes: 65_536, createdAtUtc });
  const single = await packContent("c4/single", xorshiftBytes(10_000, 0x85ebca6b), { createdAtUtc });
  const empty = await packContent("c4/empty", new Uint8Array(0), { createdAtUtc });
  const escaped = await packContent("c4/escape", xorshiftBytes(5_000, 0xc2b2ae35), { createdAtUtc });
  const escapeManifest = { ...escaped.manifest, chunks: escaped.manifest.chunks.map(chunk => ({ ...chunk, recordKey: escapeKey })) };
  const escapeHash = escaped.chunks[0]!.chunkHash;
  const multiHashes = multi.chunks.map(chunk => chunk.chunkHash);
  assert.equal(multi.chunks.length, 4);
  assert.equal(new Set(multiHashes).size, 4, "a parity fixture needs four distinct chunk hashes");
  for (const chunk of [...multi.chunks, ...single.chunks, ...empty.chunks, ...escaped.chunks]) writeFileSync(join(store, chunk.chunkHash), chunk.payload);
  const flipped = single.chunks[0]!.payload.slice();
  flipped[0]! ^= 1;
  writeFileSync(join(corrupt, single.chunks[0]!.chunkHash), flipped);
  for (const [name, manifest] of Object.entries({ multi: multi.manifest, single: single.manifest, empty: empty.manifest, escape: escapeManifest })) writeFileSync(join(manifests, `${name}.manifest`), encodeManifest(manifest));

  const provider = startProvider([store, corrupt, keyPem, certPem, escapeKey]);
  try {
    const endpoints = (await provider.next(line => line.status === "ready", "ready")).endpoints!;
    const endpointsJson = JSON.stringify(endpoints);
    const setPhase = async (name: string) => { provider.child.stdin!.write(`phase ${name}\n`); await provider.next(line => line.ack === name, `ack ${name}`); };
    const requests = (phase: string, mount: string) => provider.lines.filter(line => line.phase === phase && line.mount === mount && line.url);
    const faults = (phase: string, mount: string) => provider.lines.filter(line => line.phase === phase && line.mount === mount && line.fault);
    const runReader = async (env: NodeJS.ProcessEnv, only?: string): Promise<Record<string, ReaderResult>> => {
      const { stdout } = await execFileAsync(process.execPath, [join(here, "content-https-reader.js"), endpointsJson, manifests, ...(only ? [only] : [])], { env });
      return Object.fromEntries(stdout.trim().split("\n").map(text => JSON.parse(text) as ReaderResult).map(result => [result.case, result]));
    };
    const trusted: NodeJS.ProcessEnv = { ...process.env, NODE_EXTRA_CA_CERTS: certPem };
    delete trusted.NODE_TLS_REJECT_UNAUTHORIZED;
    const untrusted: NodeJS.ProcessEnv = { ...trusted };
    delete untrusted.NODE_EXTRA_CA_CERTS;

    await setPhase("reader");
    const read = await runReader(trusted);
    for (const [name, packed] of [["honest-multi", multi], ["honest-single", single], ["honest-empty", empty]] as const) {
      assert.deepEqual(read[name], { case: name, ok: true, sha256: packed.manifest.contentHash?.replace(/^sha256:/i, "") }, name);
    }
    assert.deepEqual(read["honest-escape"], { case: "honest-escape", ok: true, sha256: escaped.manifest.contentHash?.replace(/^sha256:/i, "") }, "honest-escape");
    assert.deepEqual(read["honest-single-chunk"], { case: "honest-single-chunk", ok: true, sha256: single.manifest.contentHash?.replace(/^sha256:/i, "") });
    assert.equal(read["tampered-multi"]?.ok, false);
    assert.equal(read["tampered-multi"]?.code, "chunk-hash-mismatch");
    assert.equal(read["corrupt-single"]?.ok, false);
    assert.equal(read["corrupt-single"]?.code, "http-status");
    assert.equal(read["corrupt-single"]?.status, 500);

    await setPhase("untrusted");
    const refused = await runReader(untrusted, "honest-multi");
    assert.equal(refused["honest-multi"]?.ok, false);
    assert.equal(refused["honest-multi"]?.code, "network");
    assert.equal(provider.lines.filter(line => line.phase === "untrusted" && line.url).length, 0, "TLS refused the reader before any HTTP request");

    await setPhase("dotnet");
    const out = await runDotnetTestFilter("tests/GameCult.Mesh.Tests/GameCult.Mesh.Tests.csproj", "CultMeshHttpsContentInteropTests", {
      CULTMESH_HTTPS_CONTENT_ENDPOINT: endpoints.honest!, CULTMESH_HTTPS_CONTENT_TAMPERED_ENDPOINT: endpoints.tampered!,
      CULTMESH_HTTPS_CONTENT_CORRUPT_ENDPOINT: endpoints.corrupt!, CULTMESH_HTTPS_CONTENT_MANIFESTS: manifests, CULTMESH_HTTPS_CONTENT_CA_PEM: certPem,
    });
    assert.match(out, /Test Run Successful\./);
    assert.match(out, /Passed:\s*3/);
    assert.doesNotMatch(out, /Skipped:\s*[1-9]/);

    // (a) both consumers asked for the same four chunk URLs on the honest mount
    const multiUrls = (phase: string) => new Set(requests(phase, "honest").map(line => line.url!).filter(url => multiHashes.some(hash => url.includes(`chunkHash=${hash}`))));
    assert.equal(multiUrls("reader").size, 4);
    assert.deepEqual([...multiUrls("dotnet")].sort(), [...multiUrls("reader")].sort());
    // (a2) the escape chunk: the C# connector's URL and the TS reader's URL are the same bytes, and they are the RFC 3986 escaping of escapeKey
    const escapeUrls = (phase: string) => requests(phase, "honest").map(line => line.url!).filter(url => url.includes(`chunkHash=${escapeHash}`));
    assert.deepEqual(escapeUrls("reader"), [`/honest/content?chunkHash=${escapeHash}&recordKey=${escapedKey}`]);
    assert.deepEqual(escapeUrls("dotnet"), escapeUrls("reader"));
    // (b) F30: C# asks for the empty chunk; the reader verifies it locally
    assert.ok(requests("dotnet", "honest").some(line => line.url!.includes(`chunkHash=${EMPTY_SHA256}`)));
    assert.ok(!requests("reader", "honest").some(line => line.url!.includes(`chunkHash=${EMPTY_SHA256}`)));
    // (c) the provider refused the corrupt store for both consumers
    for (const phase of ["reader", "dotnet"]) assert.ok(faults(phase, "corrupt").some(line => line.fault === "hash-mismatch"), `${phase} hit the corrupt chunk`);
    // (d) no fault where the store is honest
    for (const phase of ["reader", "untrusted", "dotnet"]) for (const mount of ["honest", "tampered"]) assert.equal(faults(phase, mount).length, 0, `${phase} ${mount}`);
  } finally {
    provider.child.stdin!.end();
    const killer = setTimeout(() => provider.child.kill(), 5_000);
    await provider.exited;
    clearTimeout(killer);
    rmSync(root, { recursive: true, force: true });
  }
});
