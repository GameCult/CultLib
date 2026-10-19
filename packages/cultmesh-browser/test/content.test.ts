// The verified content reader, rule by rule. Chunks and manifests come from cultnet-ts's own packContent, so the reader is
// judged against the shapes the provider will serve; the URL expectations are the .NET `BuildChunkUri` AbsoluteUri strings
// probed for the map (F5, F19), not a spelling of this module's own builder.

import test from "node:test";
import assert from "node:assert/strict";
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";
import { readFileSync } from "node:fs";
import { gzipSync } from "node:zlib";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { EMPTY_SHA256, packContent, sha256Hex, type CultMeshCdnArtifactManifest } from "cultnet-ts/content";

import { CultMeshContentError, fetchVerifiedChunk, fetchVerifiedContent, type CultMeshContentErrorCode } from "../src/content.js";

const ENDPOINT = "https://cdn.example/content";
const body = (length: number): Uint8Array => Uint8Array.from({ length }, (_, index) => (index * 7 + 3) & 255);
const pack = (length: number, chunkSizeBytes: number) => packContent("art", body(length), { chunkSizeBytes, createdAtUtc: "2026-01-01T00:00:00.0000000+00:00" });
const streamOf = (bytes: Uint8Array, onCancel?: () => void) =>
  new ReadableStream<Uint8Array>({ start(controller) { controller.enqueue(bytes); controller.close(); }, cancel: onCancel });
const withChunk = (manifest: CultMeshCdnArtifactManifest, index: number, change: object): CultMeshCdnArtifactManifest =>
  ({ ...manifest, chunks: manifest.chunks.map((chunk, i) => i === index ? { ...chunk, ...change } : chunk) });
const codeOf = (code: CultMeshContentErrorCode, extra: object = {}) => (error: unknown) =>
  error instanceof CultMeshContentError && error.code === code && Object.entries(extra).every(([key, value]) => (error as unknown as Record<string, unknown>)[key] === value);

/** A manifest-invalid rejection whose kept cause has a message matching `pattern`. */
const causeIs = (pattern: RegExp) => (error: unknown) => {
  const cause = (error as Error).cause;
  return codeOf("manifest-invalid")(error) && cause instanceof Error && pattern.test(cause.message);
};

/** Fails a stalled promise with a timeout instead of letting the test runner wait for it forever. */
const within = <T>(promise: Promise<T>, ms = 3000): Promise<T> => {
  let timer: NodeJS.Timeout;
  const late = new Promise<never>((_, reject) => { timer = setTimeout(() => reject(new Error(`did not settle within ${ms} ms`)), ms); });
  return Promise.race([promise, late]).finally(() => clearTimeout(timer));
};

interface Call { url: string; init: RequestInit }
type Handler = (call: Call, index: number) => Response | Promise<Response>;

/** A fetch double: records every call, counts requests in flight (the handler's lifetime) and their peak. */
function double(handler: Handler) {
  const calls: Call[] = [];
  const state = { inflight: 0, peak: 0 };
  const fetch = (async (input: string | URL | Request, init?: RequestInit) => {
    const call = { url: String(input), init: init ?? {} };
    const index = calls.push(call) - 1;
    state.peak = Math.max(state.peak, ++state.inflight);
    try { return await handler(call, index); } finally { state.inflight--; }
  }) as typeof globalThis.fetch;
  return { fetch, calls, state };
}

/** Serves each requested chunk by the `chunkHash` query parameter, after `delayMs`. */
function chunkServer(chunks: { chunkHash: string; payload: Uint8Array }[], delayMs = 0) {
  const byHash = new Map(chunks.map(chunk => [chunk.chunkHash, chunk.payload]));
  return double(async call => {
    if (delayMs) await new Promise(resolve => setTimeout(resolve, delayMs));
    const payload = byHash.get(new URL(call.url).searchParams.get("chunkHash")!);
    return payload ? new Response(payload as BodyInit) : new Response(null, { status: 404 });
  });
}

/** A response that never settles until the request's signal aborts, as a real fetch rejects with the signal's reason. */
const hangUntilAborted = (call: Call) => new Promise<Response>((_, reject) => {
  const signal = call.init.signal!;
  if (signal.aborted) reject(signal.reason); else signal.addEventListener("abort", () => reject(signal.reason), { once: true });
});

test("request URLs equal the .NET BuildChunkUri AbsoluteUri for every probed endpoint shape", async () => {
  const { manifest, chunks } = await pack(1, 4);
  const hex = manifest.chunks[0]!.chunkHash!;
  const cases: [string, string, string][] = [
    ["https://ygg.example/content", "mesh:cdn:chunk:" + hex, `https://ygg.example/content?chunkHash=${hex}&recordKey=mesh%3Acdn%3Achunk%3A${hex}`],
    ["https://ygg.example/content?tenant=sp", "k", `https://ygg.example/content?tenant=sp&chunkHash=${hex}&recordKey=k`],
    ["https://h", "k", `https://h/?chunkHash=${hex}&recordKey=k`],
    ["https://h/", "k", `https://h/?chunkHash=${hex}&recordKey=k`],
    ["https://h:443/x", "k", `https://h/x?chunkHash=${hex}&recordKey=k`],
    ["https://H.Example/X/", "k", `https://h.example/X/?chunkHash=${hex}&recordKey=k`],
    ["https://h/x#frag", "k", `https://h/x?chunkHash=${hex}&recordKey=k#frag`],
    ["https://h/x?a=%20b+c", "k", `https://h/x?a=%20b+c&chunkHash=${hex}&recordKey=k`],
    ["https://h/x?", "k", `https://h/x?&chunkHash=${hex}&recordKey=k`],
    ["https://h/x??a=1", "k", `https://h/x?a=1&chunkHash=${hex}&recordKey=k`],
    ["https://h/x???a=1", "k", `https://h/x?a=1&chunkHash=${hex}&recordKey=k`],
    ["https://h/x?a=?b", "k", `https://h/x?a=?b&chunkHash=${hex}&recordKey=k`],
    ["https://[::1]:8443/c", "k", `https://[::1]:8443/c?chunkHash=${hex}&recordKey=k`],
    ["https://h/x", "", `https://h/x?chunkHash=${hex}&recordKey=`],
    ["https://h/x", "k with space/ü&=?#", `https://h/x?chunkHash=${hex}&recordKey=k%20with%20space%2F%C3%BC%26%3D%3F%23`],
    ["https://h/x", "!'()*~-._ +é𐐀", `https://h/x?chunkHash=${hex}&recordKey=%21%27%28%29%2A~-._%20%2B%C3%A9%F0%90%90%80`],
  ];
  for (const [endpoint, recordKey, expected] of cases) {
    const server = chunkServer(chunks);
    await fetchVerifiedContent(endpoint, withChunk(manifest, 0, { recordKey }), { fetch: server.fetch });
    assert.equal(server.calls[0]!.url, expected, endpoint);
  }
  const absent = chunkServer(chunks);
  await fetchVerifiedContent(ENDPOINT, withChunk(manifest, 0, { recordKey: undefined }), { fetch: absent.fetch });
  assert.equal(absent.calls[0]!.url, `${ENDPOINT}?chunkHash=${hex}&recordKey=`);
});

// test/fixtures/chunk-uri-dotnet.tsv holds, for each endpoint, the AbsoluteUri .NET 10 builds (or the exception UriBuilder throws);
// chunk-uri-dotnet.fsx produced it, and nothing in this file spells an expected URL by hand. The reader may refuse an endpoint
// on which the two runtimes could disagree, but one it accepts must request exactly the URL .NET does, and one .NET refuses it
// must refuse too.
const FIXTURE_HASH = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
const DOTNET_ROWS = readFileSync(join(dirname(fileURLToPath(import.meta.url)), "..", "..", "test", "fixtures", "chunk-uri-dotnet.tsv"), "utf8")
  .split(/\r?\n/).filter(line => line !== "").map(line => { const [endpoint, uri] = line.split("\t"); return { endpoint: endpoint!, uri: uri! }; });

test("request URLs equal what .NET builds for every endpoint the reader accepts, and endpoints .NET refuses are refused", async () => {
  const { manifest, chunks } = await pack(1, 4);
  const hex = manifest.chunks[0]!.chunkHash!;
  const requested = (uri: string) => uri.replace(`chunkHash=${FIXTURE_HASH}`, `chunkHash=${hex}`);
  let accepted = 0;
  for (const { endpoint, uri } of DOTNET_ROWS) {
    const viaContent = chunkServer(chunks);
    const viaChunk = chunkServer(chunks);
    const content = await fetchVerifiedContent(endpoint, withChunk(manifest, 0, { recordKey: "k" }), { fetch: viaContent.fetch }).then(() => null, (error: unknown) => error);
    const chunk = await fetchVerifiedChunk(endpoint, hex, 1, { fetch: viaChunk.fetch }).then(() => null, (error: unknown) => error);
    if (content === null && chunk === null) {
      accepted++;
      assert.notEqual(viaContent.calls.length, 0, endpoint);
      assert.ok(!uri.startsWith("THROW"), `${endpoint} is refused by .NET but requested`);
      assert.equal(viaContent.calls[0]!.url, requested(uri), endpoint);
      assert.equal(viaChunk.calls[0]!.url, requested(uri).replace("recordKey=k", `recordKey=${encodeURIComponent(`mesh:cdn:chunk:${hex}`)}`), endpoint);
    } else {
      assert.ok(codeOf("insecure-endpoint")(content) && codeOf("insecure-endpoint")(chunk), `${endpoint} is refused for another reason`);
      assert.equal(viaContent.calls.length + viaChunk.calls.length, 0, endpoint);
    }
  }
  assert.ok(accepted >= 90, `only ${accepted} fixture endpoints are accepted`);
});

test("the spellings on which the reader once differed from .NET are rows of the fixture and are refused", async () => {
  const { manifest, chunks } = await pack(1, 4);
  const rows = new Map(DOTNET_ROWS.map(row => [row.endpoint, row.uri]));
  const refused = [
    "https:h/x", "https:/h/x", "https:///h/x", "HTTPS:/evil.example/c", "https:////h/x", "https://[::FFFF:127.0.0.1]/x", "https://[::ffff:7f00:1]/x",
    "https://[::7f00:1]/x", "https://[::ffff:0:7f00:1]/x", "https://[0:0:0:0:0:ffff:7f00:1]/x", "https://h/x?a=%e2%82%ac", "https://h/x?a=%c3%a9", "https://h/%c3%a9",
    "https://h/%80", "https://h/x?a=%C3%A9",
  ];
  for (const endpoint of refused) {
    assert.ok(rows.has(endpoint), `${endpoint} is not a fixture row`);
    const server = chunkServer(chunks);
    await assert.rejects(fetchVerifiedContent(endpoint, manifest, { fetch: server.fetch }), codeOf("insecure-endpoint"), endpoint);
    await assert.rejects(fetchVerifiedChunk(endpoint, manifest.chunks[0]!.chunkHash!, 1, { fetch: server.fetch }), codeOf("insecure-endpoint"), endpoint);
    assert.equal(server.calls.length, 0, endpoint);
  }
});

test("hash spellings are normalized into the URL: a prefixed upper-case manifest hash is requested as bare lower-case hex", async () => {
  const { manifest, chunks } = await pack(5, 5);
  const hex = manifest.chunks[0]!.chunkHash!;
  const server = chunkServer(chunks);
  const spelled = { ...withChunk(manifest, 0, { chunkHash: `SHA256:${hex.toUpperCase()}` }), contentHash: hex.toUpperCase() };
  assert.deepEqual(await fetchVerifiedContent(ENDPOINT, spelled, { fetch: server.fetch }), body(5));
  assert.equal(new URL(server.calls[0]!.url).searchParams.get("chunkHash"), hex);
});

test("a 5.5-chunk manifest returns the input bytes with one credential-free GET per chunk, whatever order the refs are listed in", async () => {
  const { manifest, chunks } = await pack(55, 10);
  assert.equal(manifest.chunks.length, 6);
  for (const refs of [manifest.chunks, [...manifest.chunks].reverse()]) {
    const server = chunkServer(chunks);
    assert.deepEqual(await fetchVerifiedContent(ENDPOINT, { ...manifest, chunks: refs }, { fetch: server.fetch }), body(55));
    assert.equal(server.calls.length, 6);
    assert.deepEqual(new Set(server.calls.map(call => new URL(call.url).searchParams.get("chunkHash"))), new Set(manifest.chunks.map(chunk => chunk.chunkHash)));
    for (const call of server.calls) { assert.equal(call.init.method, "GET"); assert.equal(call.init.credentials, "omit"); }
  }
});

test("with 10 chunks, maxConcurrentChunkRequests 3 peaks at exactly 3 in flight and the default at 4; bad bounds reject RangeError with no fetch", async () => {
  const { manifest, chunks } = await pack(100, 10);
  for (const [limit, peak] of [[3, 3], [undefined, 4], [1, 1], [32, 10]] as const) {
    const server = chunkServer(chunks, 5);
    assert.deepEqual(await fetchVerifiedContent(ENDPOINT, manifest, { fetch: server.fetch, maxConcurrentChunkRequests: limit }), body(100));
    assert.equal(server.state.peak, peak, `limit ${limit}`);
  }
  for (const maxConcurrentChunkRequests of [0, 33, 1.5, Number.NaN]) {
    const server = chunkServer(chunks);
    await assert.rejects(fetchVerifiedContent(ENDPOINT, manifest, { fetch: server.fetch, maxConcurrentChunkRequests }), RangeError);
    await assert.rejects(fetchVerifiedChunk(ENDPOINT, manifest.chunks[0]!.chunkHash!, 10, { fetch: server.fetch, maxConcurrentChunkRequests }), RangeError);
    assert.equal(server.calls.length, 0);
  }
  for (const maxBytes of [-1, 1.5, Number.NaN]) await assert.rejects(fetchVerifiedContent(ENDPOINT, manifest, { maxBytes }), RangeError);
  await assert.rejects(fetchVerifiedContent(ENDPOINT, manifest, { maxConcurrentChunkRequests: 0 }), /maxConcurrentChunkRequests/);
  await assert.rejects(fetchVerifiedContent(ENDPOINT, manifest, { maxBytes: -1 }), /maxBytes/);
});

test("an empty artifact returns an empty array with no fetch, but still answers to its content hash", async () => {
  const { manifest } = await pack(0, 4);
  const server = double(() => { throw new Error("no request expected"); });
  const result = await fetchVerifiedContent(ENDPOINT, manifest, { fetch: server.fetch });
  assert.equal(result.length, 0);
  await assert.rejects(fetchVerifiedContent(ENDPOINT, { ...manifest, contentHash: "ab".repeat(32) }, { fetch: server.fetch }), codeOf("content-hash-mismatch"));
  await assert.rejects(fetchVerifiedContent(ENDPOINT, withChunk(manifest, 0, { chunkHash: "ab".repeat(32) }), { fetch: server.fetch }), codeOf("chunk-hash-mismatch", { chunkHash: "ab".repeat(32) }));
  assert.equal(server.calls.length, 0);
});

test("a zero-size chunk beside others is manifest-invalid with no fetch", async () => {
  const { manifest } = await pack(8, 4);
  const [first, second] = manifest.chunks as [typeof manifest.chunks[number], typeof manifest.chunks[number]];
  const flood = { ...manifest, chunks: [first, { ...first, chunkHash: EMPTY_SHA256, offset: 4, sizeBytes: 0 }, second] };
  const server = double(() => { throw new Error("no request expected"); });
  await assert.rejects(fetchVerifiedContent(ENDPOINT, flood, { fetch: server.fetch }), causeIs(/^A zero-size chunk must be the only chunk\.$/));
  // two refs are enough, and the empty one may come last or first
  const one = (await pack(4, 4)).manifest;
  const empty = { ...one.chunks[0]!, chunkHash: EMPTY_SHA256, sizeBytes: 0 };
  for (const chunks of [[one.chunks[0]!, { ...empty, offset: 4 }], [{ ...empty, offset: 0 }, one.chunks[0]!], [{ ...empty, offset: 0 }, { ...empty, offset: 0 }]]) {
    await assert.rejects(fetchVerifiedContent(ENDPOINT, { ...one, sizeBytes: chunks.reduce((sum, chunk) => sum + chunk.sizeBytes, 0), chunks }, { fetch: server.fetch }), codeOf("manifest-invalid"));
  }
  assert.equal(server.calls.length, 0);
});

test("the byte cap is checked before any request: one over maxBytes and one over the 256 MiB default are too-large, the cap itself is not", async () => {
  const { manifest, chunks } = await pack(10, 5);
  const server = chunkServer(chunks);
  await assert.rejects(fetchVerifiedContent(ENDPOINT, manifest, { fetch: server.fetch, maxBytes: 9 }), codeOf("too-large"));
  await assert.rejects(fetchVerifiedChunk(ENDPOINT, manifest.chunks[0]!.chunkHash!, 5, { fetch: server.fetch, maxBytes: 4 }), codeOf("too-large"));
  assert.equal(server.calls.length, 0);
  assert.deepEqual(await fetchVerifiedContent(ENDPOINT, manifest, { fetch: server.fetch, maxBytes: 10 }), body(10));
  assert.deepEqual(await fetchVerifiedChunk(ENDPOINT, manifest.chunks[0]!.chunkHash!, 5, { fetch: server.fetch, maxBytes: 5 }), body(5));
  const huge = (size: number) => ({ ...manifest, sizeBytes: size, chunks: [{ ...manifest.chunks[0]!, offset: 0, sizeBytes: size }] });
  const refuses = double(() => { throw new TypeError("offline"); });
  await assert.rejects(fetchVerifiedContent(ENDPOINT, huge(268435457), { fetch: refuses.fetch }), codeOf("too-large"));
  assert.equal(refuses.calls.length, 0);
  await assert.rejects(fetchVerifiedContent(ENDPOINT, huge(268435456), { fetch: refuses.fetch }), codeOf("network"));
});

test("https is required, http only on loopback; ftp and unparseable endpoints are insecure-endpoint with no fetch", async () => {
  const { manifest, chunks } = await pack(5, 5);
  for (const endpoint of ["http://example.com/c", "http://cdn.example:8080/c", "http://localhost.example/c", "http://127.0.0.1.example/c", "http://10.0.0.1:80/c", "ftp://127.0.0.1/c", "not a url", "", "//cdn.example/c"]) {
    const server = chunkServer(chunks);
    await assert.rejects(fetchVerifiedContent(endpoint, manifest, { fetch: server.fetch }), codeOf("insecure-endpoint"), endpoint);
    await assert.rejects(fetchVerifiedChunk(endpoint, manifest.chunks[0]!.chunkHash!, 5, { fetch: server.fetch }), codeOf("insecure-endpoint"), endpoint);
    assert.equal(server.calls.length, 0);
  }
  for (const endpoint of ["http://127.0.0.1:8080/c", "http://localhost/c", "http://[::1]:9/c", "https://cdn.example/c"]) {
    const server = chunkServer(chunks);
    assert.deepEqual(await fetchVerifiedContent(endpoint, manifest, { fetch: server.fetch }), body(5), endpoint);
  }
});

test("non-hex hashes and a gapped manifest are manifest-invalid with no fetch, in both exports", async () => {
  const { manifest, chunks } = await pack(10, 5);
  const server = chunkServer(chunks);
  const bad = [
    withChunk(manifest, 1, { chunkHash: "g".repeat(64) }),
    { ...manifest, contentHash: "abc" },
    withChunk(manifest, 1, { offset: 6 }),
    { ...manifest, sizeBytes: 11 },
    { ...manifest, sizeBytes: 10.5 },
    // fractional chunk sizes that still tile the manifest, and an infinite one: contiguous, so only a size check refuses them
    { ...manifest, chunks: [{ ...manifest.chunks[0]!, sizeBytes: 4 }, { ...manifest.chunks[1]!, offset: 4, sizeBytes: 1.5 }, { ...manifest.chunks[1]!, offset: 5.5, sizeBytes: 4.5 }] },
    { ...manifest, sizeBytes: Infinity, chunks: [{ ...manifest.chunks[0]!, sizeBytes: Infinity }] },
  ];
  for (const candidate of bad) await assert.rejects(fetchVerifiedContent(ENDPOINT, candidate, { fetch: server.fetch }), codeOf("manifest-invalid"));
  // the kept cause names which field was wrong
  await assert.rejects(fetchVerifiedContent(ENDPOINT, bad[0]!, { fetch: server.fetch }), causeIs(/'chunkHash'/));
  await assert.rejects(fetchVerifiedContent(ENDPOINT, bad[1]!, { fetch: server.fetch }), causeIs(/'contentHash'/));
  await assert.rejects(fetchVerifiedChunk(ENDPOINT, "xyz", 5, { fetch: server.fetch }), causeIs(/'chunkHash'/));
  await assert.rejects(fetchVerifiedChunk(ENDPOINT, manifest.chunks[0]!.chunkHash!, 1.5, { fetch: server.fetch }), causeIs(/^Size must be a safe integer\.$/));
  for (const [hash, size] of [["xyz", 5], [manifest.chunks[0]!.chunkHash!, -1], [manifest.chunks[0]!.chunkHash!, 1.5], [manifest.chunks[0]!.chunkHash!, Number.NaN]] as const) {
    await assert.rejects(fetchVerifiedChunk(ENDPOINT, hash, size, { fetch: server.fetch }), codeOf("manifest-invalid"));
  }
  assert.equal(server.calls.length, 0);
});

test("every status but 200 (404, 500, 206, 304 and each other 2xx and 3xx tried) is http-status carrying the status, and the body is cancelled", async () => {
  const { manifest } = await pack(5, 5);
  for (const status of [404, 500, 206, 304, 201, 202, 203, 204, 205, 299, 301]) {
    let cancelled = false;
    const bodyless = [204, 205, 304].includes(status);
    const server = double(() => new Response(bodyless ? null : streamOf(body(5), () => { cancelled = true; }), { status }));
    await assert.rejects(fetchVerifiedContent(ENDPOINT, manifest, { fetch: server.fetch }), codeOf("http-status", { status, chunkHash: manifest.chunks[0]!.chunkHash! }));
    assert.equal(cancelled, !bodyless, String(status));
  }
});

test("wrong bytes of the right length are chunk-hash-mismatch naming the chunk; the other in-flight requests are aborted and nothing is returned", async () => {
  const { manifest, chunks } = await pack(30, 10);
  const signals: AbortSignal[] = [];
  const server = double((call, index) => {
    signals.push(call.init.signal!);
    return index === 0 ? new Response(new Uint8Array(10).fill(9)) : hangUntilAborted(call);
  });
  await assert.rejects(within(fetchVerifiedContent(ENDPOINT, manifest, { fetch: server.fetch, maxConcurrentChunkRequests: 3 })),
    codeOf("chunk-hash-mismatch", { chunkHash: chunks[0]!.chunkHash! }));
  assert.equal(signals.length, 3);
  assert.deepEqual(signals.map(signal => signal.aborted), [true, true, true]);
});

test("chunks that each verify under a wrong contentHash are content-hash-mismatch", async () => {
  const { manifest, chunks } = await pack(20, 10);
  const server = chunkServer(chunks);
  await assert.rejects(fetchVerifiedContent(ENDPOINT, { ...manifest, contentHash: await sha256Hex(body(21)) }, { fetch: server.fetch }), codeOf("content-hash-mismatch"));
});

test("an endless body is cancelled at the first byte past the chunk, a short body is refused at its end", async () => {
  const { manifest } = await pack(10, 10);
  let pulled = 0;
  let cancelled = false;
  const endless = double(() => new Response(new ReadableStream<Uint8Array>({
    pull(controller) { pulled++; controller.enqueue(Uint8Array.of(1)); }, cancel() { cancelled = true; },
  }, { highWaterMark: 0 })));
  await assert.rejects(fetchVerifiedContent(ENDPOINT, manifest, { fetch: endless.fetch }), codeOf("length-mismatch"));
  assert.ok(pulled <= 11, `pulled ${pulled}`);
  assert.ok(cancelled);
  for (const length of [0, 9]) {
    const short = double(() => new Response(body(10).subarray(0, length) as BodyInit));
    await assert.rejects(fetchVerifiedContent(ENDPOINT, manifest, { fetch: short.fetch }), codeOf("length-mismatch"));
  }
  const bodyless = double(() => new Response(null));
  await assert.rejects(fetchVerifiedContent(ENDPOINT, manifest, { fetch: bodyless.fetch }), codeOf("length-mismatch"));
});

test("a fetch TypeError and a mid-body stream error are network, with the cause kept", async () => {
  const { manifest } = await pack(10, 10);
  const down = new TypeError("fetch failed");
  await assert.rejects(fetchVerifiedContent(ENDPOINT, manifest, { fetch: double(() => { throw down; }).fetch }), (error: unknown) => codeOf("network")(error) && (error as Error).cause === down);
  const boom = new Error("boom");
  const broken = double(() => new Response(new ReadableStream<Uint8Array>({ start(controller) { controller.enqueue(body(4)); controller.error(boom); } })));
  await assert.rejects(fetchVerifiedContent(ENDPOINT, manifest, { fetch: broken.fetch }), (error: unknown) => codeOf("network")(error) && (error as Error).cause === boom);
});

test("a pre-aborted signal rejects with its reason and no fetch; an abort mid-flight rejects with the same reason and starts no further fetch", async () => {
  const { manifest } = await pack(100, 10);
  const reason = new Error("caller gave up");
  const server = double(hangUntilAborted);
  await assert.rejects(fetchVerifiedContent(ENDPOINT, manifest, { fetch: server.fetch, signal: AbortSignal.abort(reason) }), (error: unknown) => error === reason);
  await assert.rejects(fetchVerifiedChunk(ENDPOINT, manifest.chunks[0]!.chunkHash!, 10, { fetch: server.fetch, signal: AbortSignal.abort(reason) }), (error: unknown) => error === reason);
  assert.equal(server.calls.length, 0);
  const controller = new AbortController();
  const pending = fetchVerifiedContent(ENDPOINT, manifest, { fetch: server.fetch, signal: controller.signal, maxConcurrentChunkRequests: 2 });
  for (let waited = 0; server.calls.length < 2 && waited < 2000; waited++) await new Promise(resolve => setTimeout(resolve, 1));
  assert.equal(server.calls.length, 2, "two requests are in flight before the abort");
  controller.abort(reason);
  await assert.rejects(within(pending), (error: unknown) => error === reason);
  await new Promise(resolve => setTimeout(resolve, 10));
  assert.equal(server.calls.length, 2);
});

test("with crypto.subtle shadowed, both exports are webcrypto-unavailable before any fetch", async () => {
  const { manifest, chunks } = await pack(5, 5);
  const server = chunkServer(chunks);
  const original = Object.getOwnPropertyDescriptor(globalThis, "crypto")!;
  try {
    for (const value of [{}, undefined]) {
      Object.defineProperty(globalThis, "crypto", { value, configurable: true });
      await assert.rejects(fetchVerifiedContent(ENDPOINT, manifest, { fetch: server.fetch }), codeOf("webcrypto-unavailable"));
      await assert.rejects(fetchVerifiedChunk(ENDPOINT, manifest.chunks[0]!.chunkHash!, 5, { fetch: server.fetch }), codeOf("webcrypto-unavailable"));
    }
  } finally { Object.defineProperty(globalThis, "crypto", original); }
  assert.equal(server.calls.length, 0);
});

test("fetchVerifiedChunk sends the packed record key, verifies, sends nothing for size 0, and refuses wrong bytes", async () => {
  const { manifest, chunks } = await pack(10, 10);
  const hex = manifest.chunks[0]!.chunkHash!;
  const server = chunkServer(chunks);
  assert.deepEqual(await fetchVerifiedChunk(ENDPOINT, hex.toUpperCase(), 10, { fetch: server.fetch }), body(10));
  assert.equal(server.calls[0]!.url, `${ENDPOINT}?chunkHash=${hex}&recordKey=mesh%3Acdn%3Achunk%3A${hex}`);
  assert.equal(server.calls[0]!.init.credentials, "omit");
  const none = double(() => { throw new Error("no request expected"); });
  assert.equal((await fetchVerifiedChunk(ENDPOINT, EMPTY_SHA256, 0, { fetch: none.fetch })).length, 0);
  await assert.rejects(fetchVerifiedChunk(ENDPOINT, hex, 0, { fetch: none.fetch }), codeOf("chunk-hash-mismatch", { chunkHash: hex }));
  assert.equal(none.calls.length, 0);
  await assert.rejects(fetchVerifiedChunk(ENDPOINT, hex, 10, { fetch: double(() => new Response(new Uint8Array(10))).fetch }), codeOf("chunk-hash-mismatch", { chunkHash: hex }));
});

test("no error message contains the endpoint, its secret query, or a body byte", async () => {
  const secret = "https://cdn.example/c?token=CANARYSECRET";
  const { manifest } = await pack(10, 10);
  const canary = new TextEncoder().encode("BODYCANARY");
  const doubles: [CultMeshContentErrorCode, Handler | undefined][] = [
    ["http-status", () => new Response(canary, { status: 500 })],
    ["chunk-hash-mismatch", () => new Response(canary)],
    ["length-mismatch", () => new Response(new Uint8Array(40).fill(66))],
    ["network", () => { throw new TypeError(`failed ${secret}`); }],
    ["insecure-endpoint", undefined],
  ];
  for (const [code, handler] of doubles) {
    const server = double(handler ?? (() => new Response()));
    const error = await fetchVerifiedContent(code === "insecure-endpoint" ? "http://example.com/c?token=CANARYSECRET" : secret, manifest, { fetch: server.fetch }).then(() => undefined, reason => reason);
    assert.ok(codeOf(code)(error), code);
    for (const text of [(error as Error).message, String(error)]) { assert.doesNotMatch(text, /CANARY|cdn\.example|token/); }
  }
});

test("every error code has its own fixed message, and a cause only when one was given", () => {
  const codes: CultMeshContentErrorCode[] = ["manifest-invalid", "too-large", "insecure-endpoint", "webcrypto-unavailable", "network", "http-status", "length-mismatch", "chunk-hash-mismatch", "content-hash-mismatch"];
  const messages = codes.map(code => new CultMeshContentError(code).message);
  assert.equal(new Set(messages).size, codes.length);
  assert.ok(messages.every(message => message.length > 0 && !/https?:/.test(message)));
  assert.equal("cause" in new CultMeshContentError("network"), false);
  assert.equal(new CultMeshContentError("network").name, "CultMeshContentError");
  assert.equal(new CultMeshContentError("network", undefined, undefined, 0).cause, 0);
});

test("the caller's abort listener is added once and removed again, after success and after failure", async () => {
  const { manifest, chunks } = await pack(10, 5);
  for (const handler of [undefined, () => new Response(null, { status: 500 })]) {
    const controller = new AbortController();
    const added: unknown[] = [];
    const removed: unknown[] = [];
    const add = controller.signal.addEventListener.bind(controller.signal);
    const remove = controller.signal.removeEventListener.bind(controller.signal);
    controller.signal.addEventListener = ((type: string, listener: EventListener, options?: unknown) => { added.push([type, listener]); add(type, listener, options as AddEventListenerOptions); }) as typeof add;
    controller.signal.removeEventListener = ((type: string, listener: EventListener) => { removed.push([type, listener]); remove(type, listener); }) as typeof remove;
    const server = handler ? double(handler) : chunkServer(chunks);
    await fetchVerifiedContent(ENDPOINT, manifest, { fetch: server.fetch, signal: controller.signal }).catch(() => undefined);
    assert.equal(added.length, 1);
    assert.deepEqual(removed, added);
    assert.equal((added[0] as [string])[0], "abort");
  }
});

interface Stub { endpoint: string; close(): Promise<void> }
async function stub(handler: (request: IncomingMessage, response: ServerResponse) => void): Promise<Stub> {
  const server = createServer(handler);
  await new Promise<void>(resolve => server.listen(0, "127.0.0.1", resolve));
  return {
    endpoint: `http://127.0.0.1:${(server.address() as AddressInfo).port}/content`,
    close: () => new Promise<void>(resolve => { server.close(() => resolve()); server.closeAllConnections(); }),
  };
}

test("stub server through the real global fetch: gzip verifies, a short body is network, an endless body is cut, 404 is http-status", async () => {
  const { manifest, chunks } = await pack(1000, 1000);
  const payload = chunks[0]!.payload;
  const modes = new Map<string, (request: IncomingMessage, response: ServerResponse) => void>();
  const server = await stub((request, response) => modes.get(new URL(request.url!, "http://x").searchParams.get("tenant")!)!(request, response));
  const via = (tenant: string) => fetchVerifiedContent(`${server.endpoint}?tenant=${tenant}`, manifest);
  try {
    modes.set("gzip", (_, response) => {
      const encoded = gzipSync(payload);
      response.writeHead(200, { "content-encoding": "gzip", "content-length": encoded.length, "access-control-allow-origin": "*" });
      response.end(encoded);
    });
    assert.deepEqual(await via("gzip"), payload);

    modes.set("short", (_, response) => {
      response.writeHead(200, { "content-length": 1000 });
      response.write(payload.subarray(0, 400));
      setTimeout(() => response.socket!.destroy(), 20);
    });
    await assert.rejects(via("short"), codeOf("network"));

    let sawClose!: () => void;
    const closed = new Promise<void>(resolve => { sawClose = resolve; });
    modes.set("endless", (_, response) => {
      response.writeHead(200);
      const timer = setInterval(() => response.write(new Uint8Array(1000)), 1);
      response.on("close", () => { clearInterval(timer); sawClose(); });
    });
    await assert.rejects(via("endless"), codeOf("length-mismatch"));
    await closed;

    modes.set("missing", (_, response) => { response.writeHead(404, { "access-control-allow-origin": "*" }); response.end("nope"); });
    await assert.rejects(via("missing"), codeOf("http-status", { status: 404 }));
  } finally { await server.close(); }
});

const flipped = (bytes: Uint8Array, at: number): Uint8Array => { const copy = bytes.slice(); copy[at] ^= 1; return copy; };

test("wrong bytes of the right length are refused at every size, whichever byte differs: both exports, one chunk and several", async () => {
  for (const size of [1, 999, 1000, 1001, 65537, 4 * 1024 * 1024 + 1]) {
    const { manifest, chunks } = await pack(size, size);
    const hash = manifest.chunks[0]!.chunkHash!;
    for (const at of [0, size >> 1, size - 1]) {
      const wrong = double(() => new Response(flipped(chunks[0]!.payload, at) as BodyInit));
      // no size may sit past a threshold that follows from an option: the cap equal to the size, and the least and most concurrency, are tried at every size
      for (const options of [{}, { maxBytes: size }, { maxConcurrentChunkRequests: 1 }, { maxConcurrentChunkRequests: 32 }, { maxBytes: size, maxConcurrentChunkRequests: 1 }]) {
        const label = `${size} byte ${at} ${JSON.stringify(options)}`;
        await assert.rejects(fetchVerifiedChunk(ENDPOINT, hash, size, { fetch: wrong.fetch, ...options }), codeOf("chunk-hash-mismatch", { chunkHash: hash }), `chunk ${label}`);
        await assert.rejects(fetchVerifiedContent(ENDPOINT, manifest, { fetch: wrong.fetch, ...options }), codeOf("chunk-hash-mismatch", { chunkHash: hash }), `content ${label}`);
      }
    }
  }
  const { manifest, chunks } = await pack(4500, 1500);
  const middle = manifest.chunks[1]!.chunkHash!;
  const wrongMiddle = double(call => {
    const asked = new URL(call.url).searchParams.get("chunkHash");
    const payload = chunks.find(chunk => chunk.chunkHash === asked)!.payload;
    return new Response((asked === middle ? flipped(payload, 1499) : payload) as BodyInit);
  });
  await assert.rejects(fetchVerifiedContent(ENDPOINT, manifest, { fetch: wrongMiddle.fetch }), codeOf("chunk-hash-mismatch", { chunkHash: middle }));
});

test("a body of the wrong length in one chunk of several is length-mismatch naming that chunk, whichever way it is wrong", async () => {
  const { manifest, chunks } = await pack(30, 10);
  const target = manifest.chunks[2]!.chunkHash!;
  for (const length of [0, 9, 11, 20]) {
    const server = double(call => {
      const asked = new URL(call.url).searchParams.get("chunkHash");
      const payload = chunks.find(chunk => chunk.chunkHash === asked)!.payload;
      return new Response((asked === target ? new Uint8Array(length).fill(payload[0]!) : payload) as BodyInit);
    });
    await assert.rejects(within(fetchVerifiedContent(ENDPOINT, manifest, { fetch: server.fetch })), codeOf("length-mismatch", { chunkHash: target }), `length ${length}`);
  }
});

/** Whether `promise` settles before a timer started now fires: rejection is judged where it is decided, not against a generous test timeout. */
const settlesWithin = async (promise: Promise<unknown>, ms: number): Promise<boolean> => {
  let timer: NodeJS.Timeout;
  const late = new Promise<false>(resolve => { timer = setTimeout(() => resolve(false), ms); });
  return Promise.race([promise.then(() => true, () => true), late]).finally(() => clearTimeout(timer));
};

test("a failure is thrown at once, without waiting for a fetch whose body ignores the abort, and so is a caller abort; nothing is requested after either", async () => {
  const { manifest } = await pack(100, 10);
  const stalled = () => new Response(new ReadableStream<Uint8Array>({ start(controller) { controller.enqueue(body(3)); } }));
  const firstWrong = double((_, index) => index === 0 ? new Response(new Uint8Array(10)) : stalled());
  const failing = fetchVerifiedContent(ENDPOINT, manifest, { fetch: firstWrong.fetch, maxConcurrentChunkRequests: 3 });
  assert.ok(await settlesWithin(failing, 100), "the first failure waited");
  await assert.rejects(failing, codeOf("chunk-hash-mismatch"));
  await new Promise(resolve => setTimeout(resolve, 30));
  assert.equal(firstWrong.calls.length, 3, "a request started after the first failure");
  const reason = new Error("stop waiting");
  const never = double(() => new Promise<Response>(() => {}));
  const early = new AbortController();
  const noResponse = fetchVerifiedContent(ENDPOINT, manifest, { fetch: never.fetch, signal: early.signal });
  early.abort(reason);
  assert.ok(await settlesWithin(noResponse, 100), "the caller's abort waited");
  await assert.rejects(noResponse, (error: unknown) => error === reason);
  const stalledBodies = double(stalled);
  const late = new AbortController();
  const waiting = fetchVerifiedContent(ENDPOINT, manifest, { fetch: stalledBodies.fetch, signal: late.signal });
  for (let waited = 0; stalledBodies.calls.length < 4 && waited < 2000; waited++) await new Promise(resolve => setTimeout(resolve, 1));
  assert.equal(stalledBodies.calls.length, 4);
  late.abort(reason);
  assert.ok(await settlesWithin(waiting, 100), "the caller's abort waited for the stalled bodies");
  await assert.rejects(waiting, (error: unknown) => error === reason);
  await new Promise(resolve => setTimeout(resolve, 30));
  assert.equal(stalledBodies.calls.length, 4, "a request started after the caller's abort");
});

test("an abort that lands while the last bytes are hashed rejects with its reason: no success for a cancelled caller", async () => {
  const { manifest, chunks } = await pack(10, 10);
  const subtle = globalThis.crypto.subtle;
  const digest = subtle.digest.bind(subtle);
  const reason = new Error("cancelled late");
  const hash = manifest.chunks[0]!.chunkHash!;
  try {
    // digest 1 hashes the only chunk, digest 2 the whole body; fetchVerifiedChunk has no second digest
    for (const [name, abortOnDigest, run] of [
      ["content", 1, (signal: AbortSignal) => fetchVerifiedContent(ENDPOINT, manifest, { fetch: chunkServer(chunks).fetch, signal })],
      ["content", 2, (signal: AbortSignal) => fetchVerifiedContent(ENDPOINT, manifest, { fetch: chunkServer(chunks).fetch, signal })],
      ["chunk", 1, (signal: AbortSignal) => fetchVerifiedChunk(ENDPOINT, hash, 10, { fetch: chunkServer(chunks).fetch, signal })],
    ] as const) {
      const controller = new AbortController();
      let digests = 0;
      Object.defineProperty(subtle, "digest", { configurable: true, writable: true, value: (...args: Parameters<typeof digest>) => { if (++digests === abortOnDigest) controller.abort(reason); return digest(...args); } });
      const outcome = await run(controller.signal).then(() => "resolved", error => error);
      assert.equal(outcome, reason, `${name} abort on digest ${abortOnDigest}`);
    }
  } finally { delete (subtle as unknown as Record<string, unknown>).digest; }
});

test("a size the runtime cannot allocate is too-large in both exports, not a RangeError, and nothing is requested", async () => {
  const { manifest } = await pack(10, 10);
  const size = Number.MAX_SAFE_INTEGER;
  // chunks are ints, so a manifest this large is many of them: 32768 of 2147483647 bytes is about 64 TiB
  const int32Max = 2 ** 31 - 1;
  const count = 2 ** 15;
  const huge = { ...manifest, sizeBytes: count * int32Max, chunks: Array.from({ length: count }, (_, index) => ({ ...manifest.chunks[0]!, offset: index * int32Max, sizeBytes: int32Max })) };
  const server = double(() => { throw new Error("no request expected"); });
  await assert.rejects(fetchVerifiedContent(ENDPOINT, huge, { fetch: server.fetch, maxBytes: size }), codeOf("too-large"));
  await assert.rejects(fetchVerifiedChunk(ENDPOINT, manifest.chunks[0]!.chunkHash!, size, { fetch: server.fetch, maxBytes: size }), codeOf("too-large"));
  assert.equal(server.calls.length, 0);
});

test("redirects are refused: the fetch is told so, and a real redirect to another origin is network with nothing requested there", async () => {
  const { manifest, chunks } = await pack(10, 10);
  const seen = chunkServer(chunks);
  await fetchVerifiedContent(ENDPOINT, manifest, { fetch: seen.fetch });
  assert.equal(seen.calls[0]!.init.redirect, "error");
  let elsewhereCalls = 0;
  const elsewhere = await stub((_, response) => { elsewhereCalls++; response.end(chunks[0]!.payload); });
  const here = await stub((_, response) => { response.writeHead(302, { location: `${elsewhere.endpoint}?x=1` }); response.end(); });
  try {
    await assert.rejects(fetchVerifiedContent(here.endpoint, manifest), codeOf("network"));
    await assert.rejects(fetchVerifiedChunk(here.endpoint, manifest.chunks[0]!.chunkHash!, 10), codeOf("network"));
    assert.equal(elsewhereCalls, 0);
  } finally { await here.close(); await elsewhere.close(); }
});

test("esbuild bundles dist/content.js with platform 'browser', and a node:crypto import fails the same bundle", async () => {
  const entry = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "dist", "content.js");
  const options = { bundle: true, platform: "browser", format: "esm", write: false, logLevel: "silent" } as const;
  const result = await build({ ...options, entryPoints: [entry] });
  assert.ok(result.outputFiles[0]!.text.includes("CultMeshContentError"));
  await assert.rejects(build({ ...options, stdin: { contents: `import "node:crypto"; import ${JSON.stringify(entry)};`, resolveDir: dirname(entry) } }));
});
