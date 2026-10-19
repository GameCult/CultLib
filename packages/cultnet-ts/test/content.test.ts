// The shared CultMesh CDN shapes, pinned rule by rule.
//
// Wire bytes and record keys are judged against files the C# reference and Rust write
// (`contracts/cultmesh/content-vectors.cs-written.json`, `.rs-written.json`, `cdn-record-key-vectors.json`); a fixture
// authored on this side alone would only agree with itself. The TypeScript-written file is judged here from its own
// declared inputs and by the C# reference in `CultMeshContentVectorTests`.

import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { build } from "esbuild";
import { encode } from "@msgpack/msgpack";
import { CultCachePayloadDepthError } from "@gamecult/cultcache-ts/document";

import {
  CULTMESH_CDN_MANIFEST_SCHEMA,
  DEFAULT_CHUNK_SIZE_BYTES,
  EMPTY_SHA256,
  artifactRecordKey,
  chunkRecordKey,
  decodeManifest,
  encodeManifest,
  normalizeHash,
  packContent,
  parseSha256Hex,
  sha256Hex,
  validateManifest,
  type CultMeshCdnArtifactManifest,
} from "../src/content";

const CONTRACTS = join(__dirname, "..", "..", "..", "..", "contracts", "cultmesh");
const vectors = (name: string): { vectors: { label: string; kind: string; messagePackBase64: string }[] } =>
  JSON.parse(readFileSync(join(CONTRACTS, name), "utf8"));
const manifestVectors = (name: string) => vectors(name).vectors.filter(v => v.kind === "manifest")
  .map(v => ({ label: v.label, bytes: new Uint8Array(Buffer.from(v.messagePackBase64, "base64")) }));
const csManifest = (label: string) => manifestVectors("content-vectors.cs-written.json").find(v => v.label === label)!.bytes;

// The C# fixture: byte i is i % 251, 262,144-byte chunks, and the options its test passes.
const FIXTURE_BODY = Uint8Array.from({ length: 1_315_551 }, (_, i) => i % 251);
const FIXTURE_OPTIONS = {
  chunkSizeBytes: 262_144,
  version: "1",
  mimeType: "application/x-cultnet-vector",
  createdAtUtc: "2026-09-22T00:00:00.0000000+00:00",
};
const FIXTURE_HASH = "f4ae2a7c95f4d966058f75c13185aa0ab6a91fc5afad886296ef2aee755a2af1";
const HASH = "a".repeat(64);

const manifestOf = (over: Partial<CultMeshCdnArtifactManifest> = {}): CultMeshCdnArtifactManifest => ({
  artifactId: "id", kind: "asset", version: "1", contentHash: HASH, sizeBytes: 4, mimeType: "text/plain", createdAtUtc: "t",
  chunks: [{ chunkHash: HASH, offset: 0, sizeBytes: 4, recordKey: `mesh:cdn:chunk:${HASH}` }], tags: ["t"], metadata: [["k", "v"]],
  ...over,
});
// Wire bytes of a ten-array with any field replaced, metadata as a plain object (no integer-like keys).
const fieldsOf = (over: Record<number, unknown> = {}) => {
  const m = manifestOf();
  const fields: unknown[] = [m.artifactId, m.kind, m.version, m.contentHash, m.sizeBytes, m.mimeType, m.createdAtUtc,
    m.chunks.map(c => [c.chunkHash, c.offset, c.sizeBytes, c.recordKey]), m.tags, { k: "v" }];
  for (const [index, value] of Object.entries(over)) fields[Number(index)] = value;
  return fields;
};
const wire = (over: Record<number, unknown> = {}) => encode(fieldsOf(over), { useBigInt64: true, maxDepth: 1000 });
const refusal = (bytes: Uint8Array) => assert.throws(() => decodeManifest(bytes), /./);

test("each C# manifest vector decodes and re-encodes byte-identically", () => {
  const vs = manifestVectors("content-vectors.cs-written.json");
  assert.deepEqual(vs.map(v => v.label), ["manifest", "manifest_out_of_order", "manifest_metadata_order", "manifest_empty_body"]);
  for (const { label, bytes } of vs) assert.deepEqual(encodeManifest(decodeManifest(bytes)), bytes, label);
});

test("the Rust-written manifests decode and re-encode byte-identically", () => {
  const vs = manifestVectors("content-vectors.rs-written.json");
  assert.ok(vs.length >= 4);
  for (const { label, bytes } of vs) assert.deepEqual(encodeManifest(decodeManifest(bytes)), bytes, label);
});

test("packContent of the C# fixture body and options equals the cs-written manifest, and of an empty body manifest_empty_body", async () => {
  const { manifest, chunks } = await packContent("eureka/content-vector", FIXTURE_BODY, FIXTURE_OPTIONS);
  assert.deepEqual(encodeManifest(manifest), csManifest("manifest"));
  assert.equal(manifest.contentHash, FIXTURE_HASH);
  assert.equal(chunks.length, 6);
  const empty = await packContent("eureka/content-vector", new Uint8Array(0), FIXTURE_OPTIONS);
  assert.deepEqual(encodeManifest(empty.manifest), csManifest("manifest_empty_body"));
  assert.equal(empty.chunks.length, 1, "an empty artifact packs one chunk");
  assert.equal(empty.chunks[0]!.payload.length, 0);
  assert.equal(empty.chunks[0]!.chunkHash, EMPTY_SHA256);
});

test("the packed chunks are copies that rejoin to the body, each named by the hash of its bytes", async () => {
  const body = Uint8Array.from({ length: 10 }, (_, i) => i);
  const { manifest, chunks } = await packContent("x", body, { chunkSizeBytes: 4 });
  body.fill(9);
  assert.deepEqual(chunks.map(c => c.payload.length), [4, 4, 2]);
  assert.deepEqual(Array.from(chunks.flatMap(c => Array.from(c.payload))), [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
  for (const [i, c] of chunks.entries()) {
    assert.equal(c.chunkHash, await sha256Hex(c.payload));
    assert.deepEqual(manifest.chunks[i], { chunkHash: c.chunkHash, offset: i * 4, sizeBytes: c.payload.length, recordKey: chunkRecordKey(c.chunkHash) });
  }
  const twins = await packContent("x", new Uint8Array(8), { chunkSizeBytes: 4 });
  assert.equal(twins.manifest.chunks[0]!.chunkHash, twins.manifest.chunks[1]!.chunkHash, "equal chunks share a hash");
});

test("pack defaults: kind asset, mime octet-stream, empty version, tags and metadata", async () => {
  const bare = (await packContent("x", new Uint8Array(3))).manifest;
  assert.deepEqual([bare.kind, bare.mimeType, bare.version, bare.tags, bare.metadata], ["asset", "application/octet-stream", "", [], []]);
  const blank = (await packContent("x", new Uint8Array(3), { kind: "  ", mimeType: "" })).manifest;
  assert.deepEqual([blank.kind, blank.mimeType], ["asset", "application/octet-stream"]);
  const given = (await packContent("x", new Uint8Array(3), { kind: "build", mimeType: "text/plain", version: "2", tags: ["a"], metadata: [["k", "v"]] })).manifest;
  assert.deepEqual([given.kind, given.mimeType, given.version, given.tags, given.metadata], ["build", "text/plain", "2", ["a"], [["k", "v"]]]);
  assert.equal(given.artifactId, "x");
});

test("packContent refuses a blank artifact id, a chunk size outside 1..2147483647 and duplicate metadata keys", async () => {
  for (const id of ["", "   ", "\u0085\u00a0"]) await assert.rejects(packContent(id, new Uint8Array(1)), /Artifact id must be non-empty/);
  for (const chunkSizeBytes of [0, -1, 2 ** 31, 1.5, NaN]) await assert.rejects(packContent("x", new Uint8Array(1), { chunkSizeBytes }), /ChunkSizeBytes/);
  await packContent("x", new Uint8Array(1), { chunkSizeBytes: 2 ** 31 - 1 });
  await assert.rejects(packContent("x", new Uint8Array(1), { metadata: [["a", "1"], ["a", "2"]] }), /duplicate key/);
});

test("default createdAtUtc matches the C# round-trip 'O' format", async () => {
  const { manifest } = await packContent("x", new Uint8Array(1));
  assert.match(manifest.createdAtUtc!, /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{7}\+00:00$/);
  assert.ok(Math.abs(Date.parse(manifest.createdAtUtc!.replace(/(\.\d{3})\d{4}\+00:00$/, "$1Z")) - Date.now()) < 60_000);
});

test("manifest_out_of_order decodes and validates, and its chunks keep wire order", () => {
  const decoded = decodeManifest(csManifest("manifest_out_of_order"));
  assert.deepEqual(decoded.chunks.map(c => c.offset / 262_144), [3, 0, 5, 1, 4, 2]);
});

test("metadata is ordered entries, immune to JS integer-key ordering", () => {
  assert.deepEqual(decodeManifest(csManifest("manifest_metadata_order")).metadata, [["zeta", "1"], ["alpha", "2"], ["mid", "3"]]);
  const metadata = [["10", "a"], ["alpha", "b"], ["2", "c"]] as const;
  const bytes = encodeManifest(manifestOf({ metadata }));
  assert.deepEqual(decodeManifest(bytes).metadata, metadata);
  assert.deepEqual(encodeManifest(decodeManifest(bytes)), bytes);
});

test("a manifest with a duplicated metadata key is refused, in decode and in validate", () => {
  const metadata = [["a", "1"], ["b", "x"], ["a", "2"]] as const;
  assert.throws(() => decodeManifest(encodeManifest(manifestOf({ metadata }))), /duplicate key/);
  assert.throws(() => validateManifest(manifestOf({ metadata })), /duplicate key/);
  validateManifest(manifestOf({ metadata: [["a", "1"], ["b", "1"]] }));
});

test("a metadata key that is not a string is refused", () => {
  const withMap = (...entry: Uint8Array[]) => Uint8Array.from([...wire().slice(0, -5), 0x81, ...entry.flatMap(part => Array.from(part))]);
  decodeManifest(withMap(encode("k"), encode("v")));
  refusal(withMap(encode(5), encode("v")));
  refusal(withMap(encode(new Uint8Array(2)), encode("v")));
});

test("an integer field accepts any width and refuses what a JS number cannot hold exactly", () => {
  const empty = { 4: 0, 7: [] as unknown[] };
  // Past the integer rule, a size nothing sums to fails the sum check; a refused integer fails the type check first.
  assert.throws(() => decodeManifest(wire({ ...empty, 4: 2n ** 53n - 1n })), /do not sum/);
  assert.throws(() => decodeManifest(wire({ ...empty, 4: 2n ** 53n })), /wrong type/);
  assert.throws(() => decodeManifest(wire({ ...empty, 4: 2n ** 53n + 1n })), /wrong type/);
  assert.throws(() => decodeManifest(wire({ ...empty, 4: 2 ** 53 })), /wrong type/);
  assert.throws(() => decodeManifest(wire({ ...empty, 4: -(2n ** 53n) })), /wrong type/);
  assert.throws(() => decodeManifest(wire({ ...empty, 4: 0.5 })), /wrong type/);
  // A 64-bit encoding of a small value is the same value.
  const marked = wire({ 4: 0xdeadbeef, 7: [[HASH, 0, 4, "k"]] });
  const at = marked.findIndex((_, i) => marked[i] === 0xce && marked[i + 1] === 0xde && marked[i + 4] === 0xef);
  const wide = Uint8Array.from([...marked.slice(0, at), 0xcf, 0, 0, 0, 0, 0, 0, 0, 4, ...marked.slice(at + 5)]);
  assert.equal(decodeManifest(wide).sizeBytes, 4);
});

test("a chunk size outside int32 is refused as the reference's int cannot hold it", () => {
  const chunk = (size: number) => decodeManifest(wire({ 4: size, 7: [[HASH, 0, size, "k"]] }));
  assert.equal(chunk(2 ** 31 - 1).chunks[0]!.sizeBytes, 2 ** 31 - 1);
  assert.throws(() => chunk(2 ** 31), /chunks\.sizeBytes.*wrong type/);
  assert.throws(() => decodeManifest(wire({ 4: -(2n ** 31n) - 1n, 7: [[HASH, 0, -(2n ** 31n) - 1n, "k"]] })), /chunks\.sizeBytes.*wrong type/);
  assert.throws(() => decodeManifest(wire({ 4: 0, 7: [[HASH, 0, -(2 ** 31), "k"]] })), /chunk has a negative size/);
});

test("a payload nested 257 deep is refused before decoding and 256 is not refused for depth", () => {
  const nest = (depth: number) => Uint8Array.from([...Array<number>(depth).fill(0x91), 0xc0]);
  assert.throws(() => decodeManifest(nest(257)), (error: unknown) =>
    error instanceof CultCachePayloadDepthError && /CDN artifact manifest nests 257 .* limit is 256/.test(error.message));
  assert.throws(() => decodeManifest(nest(256)), (error: unknown) => error instanceof Error && !(error instanceof CultCachePayloadDepthError));
  // The nesting is inside a field of an otherwise valid manifest: the outer array is depth 1.
  const inside = (depth: number) => wire({ 8: Array.from({ length: depth }).reduce<unknown>(value => [value], null) });
  assert.throws(() => decodeManifest(inside(256)), CultCachePayloadDepthError);
  assert.throws(() => decodeManifest(inside(255)), (error: unknown) => error instanceof Error && !(error instanceof CultCachePayloadDepthError));
});

test("a manifest of the wrong shape is refused naming the field and never the value", () => {
  const canary = "CANARY-7f3a91";
  const chunk = (...parts: unknown[]) => wire({ 7: [parts] });
  const cases: [Uint8Array, string][] = [
    [wire({ 0: 5 }), "artifactId"], [wire({ 1: 5 }), "kind"], [wire({ 2: 5 }), "version"],
    [wire({ 3: 5 }), "contentHash"], [wire({ 5: 5 }), "mimeType"], [wire({ 6: 5 }), "createdAtUtc"],
    [wire({ 4: canary }), "sizeBytes"], [wire({ 4: "4" }), "sizeBytes"], [wire({ 4: null }), "sizeBytes"], [wire({ 4: 0.5 }), "sizeBytes"],
    [wire({ 7: canary }), "chunks"], [wire({ 7: [canary] }), "chunks"], 
    [chunk(5, 0, 4, "k"), "chunks.chunkHash"], [chunk(HASH, canary, 4, "k"), "chunks.offset"], [chunk(HASH, 0, canary, "k"), "chunks.sizeBytes"],
    [chunk(HASH, 0, 4, 5), "chunks.recordKey"],
    [wire({ 8: canary }), "tags"], [wire({ 8: [5] }), "tags"],
    [wire({ 9: canary }), "metadata"], [wire({ 9: { k: 5 } }), "metadata"], [wire({ 9: [] }), "metadata"],
    [wire({ 9: new Uint8Array(2) }), "metadata"],
    [encode(canary), "manifest"], [encode({ [canary]: canary }), "manifest"],
  ];
  for (const [bytes, field] of cases) {
    assert.throws(() => decodeManifest(bytes), (error: unknown) =>
      error instanceof Error && error.message.includes(`'${field}'`) && !error.message.includes(canary), field);
  }
  // A payload that ends early is not a shape; it does not echo the value either.
  assert.throws(() => decodeManifest(encode([canary]).slice(0, 4)), (error: unknown) => error instanceof Error && !error.message.includes(canary));
  assert.equal(decodeManifest(wire()).chunks.length, 1, "the unmodified fixture decodes");
});

test("a metadata key that is not a string names the field", () => {
  const withMap = (...entry: Uint8Array[]) => Uint8Array.from([...wire().slice(0, -5), 0x81, ...entry.flatMap(part => Array.from(part))]);
  assert.throws(() => decodeManifest(withMap(encode(5), encode("v"))), /'metadata'/);
});

test("validateManifest refuses negative size, a gap, an overlap and a size-sum mismatch with the C# messages", () => {
  const chunk = (offset: number, sizeBytes: number) => ({ chunkHash: HASH, offset, sizeBytes, recordKey: "k" });
  const refuses = (over: Partial<CultMeshCdnArtifactManifest>, message: string) =>
    assert.throws(() => validateManifest(manifestOf(over)), new Error(message));
  refuses({ sizeBytes: -1, chunks: [] }, "CDN artifact manifest has a negative size.");
  refuses({ sizeBytes: 4, chunks: [chunk(0, 2), chunk(3, 1)] }, "CDN artifact chunks are not contiguous.");
  refuses({ sizeBytes: 4, chunks: [chunk(0, 3), chunk(2, 2)] }, "CDN artifact chunks are not contiguous.");
  refuses({ sizeBytes: 4, chunks: [chunk(1, 4)] }, "CDN artifact chunks are not contiguous.");
  refuses({ sizeBytes: 0, chunks: [chunk(0, -1)] }, "CDN artifact chunk has a negative size.");
  refuses({ sizeBytes: 5, chunks: [chunk(0, 4)] }, "CDN artifact chunk sizes do not sum to the manifest size.");
  refuses({ sizeBytes: 3, chunks: [chunk(0, 4)] }, "CDN artifact chunk sizes do not sum to the manifest size.");
  refuses({ sizeBytes: -1, chunks: [chunk(1, 1)] }, "CDN artifact manifest has a negative size.");
  validateManifest(manifestOf({ sizeBytes: 4, chunks: [chunk(2, 2), chunk(0, 2)] }));
  validateManifest(manifestOf({ sizeBytes: 0, chunks: [] }));
});

test("validateManifest refuses a size that is not a whole number: a fraction, NaN or Infinity, in the manifest or in a chunk", () => {
  const chunk = (offset: number, sizeBytes: number) => ({ chunkHash: HASH, offset, sizeBytes, recordKey: "k" });
  const refuses = (over: Partial<CultMeshCdnArtifactManifest>, message: string) =>
    assert.throws(() => validateManifest(manifestOf(over)), new Error(message));
  const manifestMessage = "CDN artifact manifest size is not a whole number.";
  const chunkMessage = "CDN artifact chunk size is not a whole number.";
  for (const size of [1.5, Number.NaN, Number.POSITIVE_INFINITY]) refuses({ sizeBytes: size, chunks: [] }, manifestMessage);
  // fractional chunk sizes that still tile the manifest are contiguous, so only the whole-number rule refuses them
  refuses({ sizeBytes: 6, chunks: [chunk(0, 4), chunk(4, 1.5), chunk(5.5, 0.5)] }, chunkMessage);
  refuses({ sizeBytes: 1.5, chunks: [chunk(0, 1.5)] }, manifestMessage);
  refuses({ sizeBytes: 4, chunks: [chunk(0, Number.NaN)] }, chunkMessage);
  refuses({ sizeBytes: Number.POSITIVE_INFINITY, chunks: [chunk(0, Number.POSITIVE_INFINITY)] }, manifestMessage);
  refuses({ sizeBytes: 4, chunks: [chunk(0, 2), chunk(2, Number.POSITIVE_INFINITY)] }, chunkMessage);
  refuses({ sizeBytes: 0, chunks: [chunk(0, -1)] }, "CDN artifact chunk has a negative size.");
  validateManifest(manifestOf({ sizeBytes: 2 ** 53 - 1, chunks: [chunk(0, 2 ** 53 - 1)] }));
});

test("normalizeHash: 'SHA256:ABC ' -> 'abc', ' sha256:abc' -> 'sha256:abc', blank throws", () => {
  assert.equal(normalizeHash("SHA256:ABC "), "abc");
  assert.equal(normalizeHash("sHa256:AbC"), "abc");
  assert.equal(normalizeHash(" sha256:abc"), "sha256:abc");
  assert.equal(normalizeHash("sha256:sha256:ab"), "sha256:ab");
  assert.equal(normalizeHash("sha256:   "), "");
  assert.equal(normalizeHash("\u00a0\u0130X\u00a0"), "\u0130x");
  for (const blank of ["", "   ", "\u0085\u2003"]) assert.throws(() => normalizeHash(blank, "chunkHash"), /Hash must be non-empty\. \(Parameter 'chunkHash'\)/);
  assert.throws(() => normalizeHash(""), /Hash must be non-empty\. \(Parameter 'hash'\)/);
});

test("parseSha256Hex accepts 64 hex digits in any case and prefix, and refuses anything else without echoing it", () => {
  assert.equal(parseSha256Hex(`SHA256: ${"AB".repeat(32)} `), "ab".repeat(32));
  for (const bad of ["a".repeat(63), "a".repeat(65), `${"a".repeat(63)}g`, "sha256:", `sha256:CANARY-${"a".repeat(50)}`]) {
    assert.throws(() => parseSha256Hex(bad), (error: unknown) =>
      error instanceof Error && error.message === "Hash must be 64 hexadecimal digits. (Parameter 'hash')");
  }
  assert.throws(() => parseSha256Hex("zz", "chunkHash"), /\(Parameter 'chunkHash'\)/);
  assert.throws(() => parseSha256Hex(""), /Hash must be non-empty\. \(Parameter 'hash'\)/);
});

test("every entry in cdn-record-key-vectors.json derives to its recordKey", async () => {
  const file = JSON.parse(readFileSync(join(CONTRACTS, "cdn-record-key-vectors.json"), "utf8")) as {
    artifacts: { artifactIdUnits: number[]; versionUnits: number[]; contentHash: string; recordKey: string }[];
    chunks: { chunkHash: string; recordKey: string }[];
  };
  assert.ok(file.artifacts.length >= 14 && file.chunks.length >= 7, "the file carries the C# traps");
  const text = (units: number[]) => String.fromCharCode(...units);
  for (const a of file.artifacts) {
    assert.equal(await artifactRecordKey(text(a.artifactIdUnits), text(a.versionUnits), a.contentHash), a.recordKey, JSON.stringify(a.artifactIdUnits));
  }
  for (const c of file.chunks) assert.equal(chunkRecordKey(c.chunkHash), c.recordKey, c.chunkHash);
  assert.ok(file.artifacts.some(a => a.artifactIdUnits.includes(0xd800)), "a lone surrogate survives the file");
});

test("record keys refuse a blank id, version-less artifacts are 'unversioned', and a blank hash throws", async () => {
  await assert.rejects(artifactRecordKey("  ", "1", HASH), /Value must be non-empty\. \(Parameter 'artifactId'\)/);
  await assert.rejects(artifactRecordKey("a", "1", " "), /Hash must be non-empty\. \(Parameter 'contentHash'\)/);
  assert.throws(() => chunkRecordKey(""), /\(Parameter 'chunkHash'\)/);
  assert.equal(await artifactRecordKey("a", " ", HASH), `mesh:cdn:artifact:a:unversioned:${HASH}`);
  assert.equal(chunkRecordKey(`SHA256:${HASH.toUpperCase()}`), `mesh:cdn:chunk:${HASH}`);
});

test("sha256Hex of '' and of the fixture body equal the published digests", async () => {
  assert.equal(await sha256Hex(new Uint8Array(0)), "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
  assert.equal(await sha256Hex(FIXTURE_BODY), FIXTURE_HASH);
  assert.equal(await sha256Hex(new TextEncoder().encode("abc")), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
  assert.equal(EMPTY_SHA256, await sha256Hex(new Uint8Array(0)));
});

test("esbuild bundles dist/content.js with platform 'browser' without error", async () => {
  const result = await build({
    entryPoints: [join(__dirname, "..", "..", "dist", "content.js")], bundle: true, platform: "browser", format: "esm", write: false, logLevel: "silent",
  });
  assert.ok(result.outputFiles[0]!.text.includes("gamecult.mesh.cdn_artifact_manifest.v1"));
});

test("the committed ts-written file re-packs from its declared inputs to the same bytes and record keys", async () => {
  type Input = { artifactId: string; kind: string; version: string; mimeType: string; createdAtUtc: string; chunkSizeBytes: number; tags: string[];
    metadata: [string, string][]; body: { length: number; multiplier: number; modulus: number } };
  const file = JSON.parse(readFileSync(join(CONTRACTS, "content-vectors.ts-written.json"), "utf8")) as
    { vectors: { label: string; kind: string; input: Input; recordKey: string; messagePackBase64: string }[] };
  assert.ok(file.vectors.length >= 3);
  assert.ok(file.vectors.some(v => v.input.body.length === 0) && file.vectors.some(v => v.input.body.length > v.input.chunkSizeBytes));
  for (const { label, kind, input, recordKey, messagePackBase64 } of file.vectors) {
    assert.equal(kind, "manifest");
    const { body, ...options } = input;
    const { manifest } = await packContent(input.artifactId, Uint8Array.from({ length: body.length }, (_, i) => (i * body.multiplier) % body.modulus), options);
    const bytes = new Uint8Array(Buffer.from(messagePackBase64, "base64"));
    assert.deepEqual(encodeManifest(manifest), bytes, label);
    assert.deepEqual(encodeManifest(decodeManifest(bytes)), bytes, label);
    assert.equal(await artifactRecordKey(manifest.artifactId, manifest.version, manifest.contentHash), recordKey, label);
  }
});

test("the schema id and the default chunk size are the reference's", async () => {
  assert.equal(CULTMESH_CDN_MANIFEST_SCHEMA, "gamecult.mesh.cdn_artifact_manifest.v1");
  assert.equal(DEFAULT_CHUNK_SIZE_BYTES, 4_194_304);
  const body = new Uint8Array(DEFAULT_CHUNK_SIZE_BYTES + 1);
  assert.deepEqual((await packContent("x", body)).manifest.chunks.map(c => c.sizeBytes), [DEFAULT_CHUNK_SIZE_BYTES, 1]);
  assert.equal((await packContent("x", body.subarray(0, DEFAULT_CHUNK_SIZE_BYTES))).chunks.length, 1);
  assert.deepEqual((await packContent("x", new Uint8Array(3), { chunkSizeBytes: 1 })).manifest.chunks.map(c => c.offset), [0, 1, 2]);
});

test("sha256Hex names its refusal when WebCrypto is missing, whether crypto or only subtle is absent", async () => {
  const original = Object.getOwnPropertyDescriptor(globalThis, "crypto")!;
  try {
    for (const replacement of [undefined, {}]) {
      Object.defineProperty(globalThis, "crypto", { value: replacement, configurable: true, writable: true });
      await assert.rejects(sha256Hex(new Uint8Array(1)), new Error("WebCrypto SHA-256 is unavailable."));
    }
  } finally {
    Object.defineProperty(globalThis, "crypto", original);
  }
  assert.equal(await sha256Hex(new Uint8Array(0)), EMPTY_SHA256);
});

test("the metadata map header is the minimal MessagePack header at every size boundary", () => {
  for (const count of [0, 15, 16, 65535, 65536]) {
    const entries = Array.from({ length: count }, (_, i) => [`k${i}`, "v"] as const);
    const expected = encode(Object.fromEntries(entries));
    const bytes = encodeManifest(manifestOf({ metadata: entries }));
    assert.deepEqual(bytes.slice(bytes.length - expected.length), expected, `${count} entries`);
    assert.equal(decodeManifest(bytes).metadata!.length, count);
  }
});

// The reference's acceptance, case by case: `contracts/cultmesh/content-acceptance-vectors.json` is written by the C# test
// from `MessagePackSerializer.Deserialize` plus `ValidateManifest`; an accepted case carries the bytes the reference
// writes back and the key it derives. Nothing in the file is authored on this side.
test("decodeManifest accepts and refuses exactly what the reference does, and re-encodes what it re-encodes", async () => {
  const file = JSON.parse(readFileSync(join(CONTRACTS, "content-acceptance-vectors.json"), "utf8")) as {
    cases: { label: string; inputBase64: string; accepted: boolean; reencodedBase64: string | null; recordKey: string | null }[];
  };
  const bytes = (base64: string) => new Uint8Array(Buffer.from(base64, "base64"));
  assert.ok(file.cases.filter(c => c.accepted).length > 30 && file.cases.filter(c => !c.accepted).length > 30, "the file holds both verdicts in quantity");
  assert.equal(new Set(file.cases.map(c => c.label)).size, file.cases.length);
  for (const c of file.cases) {
    if (!c.accepted) {
      assert.throws(() => decodeManifest(bytes(c.inputBase64)), /./, `${c.label}: the reference refuses`);
      continue;
    }
    const manifest = decodeManifest(bytes(c.inputBase64));
    assert.deepEqual(encodeManifest(manifest), bytes(c.reencodedBase64!), `${c.label}: the reference's bytes`);
    if (c.recordKey === null) await assert.rejects(artifactRecordKey(manifest.artifactId, manifest.version, manifest.contentHash), /./, `${c.label}: no key`);
    else assert.equal(await artifactRecordKey(manifest.artifactId, manifest.version, manifest.contentHash), c.recordKey, `${c.label}: key`);
  }
});

test("a manifest written as a map names the manifest, not a field inside it", () => {
  assert.throws(() => decodeManifest(encode({ 0: "a", 9: { k: "v" } })), /field 'manifest' has the wrong type/);
});

test("nil in a reference slot reads as null and writes back as nil; a missing trailing slot takes the initialiser", () => {
  const nils = wire({ 0: null, 2: null, 8: null, 9: null });
  const manifest = decodeManifest(nils);
  assert.deepEqual([manifest.artifactId, manifest.version, manifest.tags, manifest.metadata], [null, null, null, null]);
  assert.deepEqual(encodeManifest(manifest), nils);
  const short = decodeManifest(encode(["id"]));
  assert.deepEqual(short, { artifactId: "id", kind: "asset", version: "", contentHash: "", sizeBytes: 0, mimeType: "application/octet-stream", createdAtUtc: "", chunks: [], tags: [], metadata: [] });
});

// Refusals that are the same refusal in the reference but would read differently if the wrong rule fired: the message
// names the true cause (the sign, the nil chunk, the reserved code, the early end), never a neighbouring field.
test("a refusal names its true cause", () => {
  const refuses = (bytes: Uint8Array, message: RegExp) => assert.throws(() => decodeManifest(bytes), message);
  refuses(wire({ 4: -1, 7: [] }), /negative size/);
  refuses(wire({ 4: -32, 7: [] }), /negative size/);
  refuses(wire({ 4: -(2n ** 53n - 1n), 7: [] }), /negative size/);
  refuses(wire({ 4: -(2n ** 53n), 7: [] }), /field 'sizeBytes' has the wrong type/);
  refuses(wire({ 4: 0, 7: [[HASH, 0, -32, "k"]] }), /chunk has a negative size/);
  refuses(wire({ 7: null }), /field 'chunks' has the wrong type/);
  refuses(wire({ 7: [null] }), /field 'chunks' has the wrong type/);
  refuses(Uint8Array.of(0xc0), /field 'manifest' has the wrong type/);
  refuses(wire().slice(0, 8), /ends before its last field/);
  refuses(Uint8Array.of(0x91, 0xa3, 0x41), /ends before its last field/);
  refuses(Uint8Array.from([0x9b, ...wire().slice(1), 0xc1]), /field 'manifest' has the wrong type/);
});

test("a lone surrogate is written as U+FFFD at every string length, short and long alike, in every string slot", async () => {
  const base = (await packContent("x", Uint8Array.of(1, 2, 3), { createdAtUtc: "2026-09-22T00:00:00.0000000+00:00" })).manifest;
  // @msgpack/msgpack switches encoders past 50 UTF-16 units; the reference writes U+FFFD on both sides of that line.
  for (const length of [1, 2, 16, 31, 32, 49, 50, 51, 52, 64, 200]) {
    for (const [lone, replaced] of [["\ud800", "\ufffd"], ["\udfff", "\ufffd"]]) {
      const filler = "a".repeat(length - 1);
      const bad = `${filler}${lone}`;
      const good = `${filler}${replaced}`;
      const withText = (value: string): CultMeshCdnArtifactManifest => ({
        ...base,
        artifactId: value, kind: value, version: value, mimeType: value, createdAtUtc: value,
        chunks: [{ ...base.chunks[0]!, recordKey: value }],
        tags: [value],
        metadata: [[value, value]],
      });
      const bytes = encodeManifest(withText(bad));
      assert.deepEqual(bytes, encodeManifest(withText(good)), `length ${length}`);
      assert.ok(!bytes.some((b, i) => b === 0xed && bytes[i + 1]! >= 0xa0 && bytes[i + 1]! <= 0xbf), `no CESU surrogate bytes at length ${length}`);
      assert.equal(decodeManifest(bytes).artifactId, good, `length ${length} reads back as written`);
    }
  }
  const packed = await packContent(`a${"b".repeat(60)}\ud800`, Uint8Array.of(1), { kind: "k\udc00", createdAtUtc: "2026-09-22T00:00:00.0000000+00:00" });
  assert.equal(decodeManifest(encodeManifest(packed.manifest)).kind, "k\ufffd");
});
