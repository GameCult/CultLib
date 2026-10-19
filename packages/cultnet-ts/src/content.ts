// The one TypeScript owner of the CultMesh CDN shapes: the manifest and chunk reference, their MessagePack form, hash
// normalization, both record-key grammars, SHA-256 hashing and packing. The C# reference is
// `src/GameCult.Mesh/CultMeshCdn.cs`; `contracts/cultmesh/content-vectors.*.json` and `cdn-record-key-vectors.json` pin
// the bytes and the keys.
//
// Browser-safe by construction: `crypto.subtle`, `TextEncoder`, `Uint8Array` and the codecs this package already ships.
// No `node:` import. A manifest names its chunks by hash and carries no address: where bytes are fetched from is the
// caller's to resolve through CultMesh.

import { Decoder, encode } from "@msgpack/msgpack";
import { checkPayloadDepth } from "@gamecult/cultcache-ts/document";
import { isNullOrWhiteSpaceCSharp, trimCSharp } from "./cultmesh-authority";

export const CULTMESH_CDN_MANIFEST_SCHEMA = "gamecult.mesh.cdn_artifact_manifest.v1";
export const DEFAULT_CHUNK_SIZE_BYTES = 4 * 1024 * 1024;
export const EMPTY_SHA256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

export interface CultMeshCdnChunkRef {
  readonly chunkHash: string;
  readonly offset: number;
  readonly sizeBytes: number;
  readonly recordKey: string;
}

// Metadata is ordered entries, never an object: a JS object reorders integer-like keys, and the entry order is wire bytes.
export interface CultMeshCdnArtifactManifest {
  readonly artifactId: string;
  readonly kind: string;
  readonly version: string;
  readonly contentHash: string;
  readonly sizeBytes: number;
  readonly mimeType: string;
  readonly createdAtUtc: string;
  readonly chunks: readonly CultMeshCdnChunkRef[];
  readonly tags: readonly string[];
  readonly metadata: readonly (readonly [string, string])[];
}

export interface CultMeshCdnPackOptions {
  readonly kind?: string;
  readonly version?: string;
  readonly mimeType?: string;
  readonly createdAtUtc?: string;
  readonly chunkSizeBytes?: number;
  readonly tags?: readonly string[];
  readonly metadata?: readonly (readonly [string, string])[];
}

export interface CultMeshCdnPackedChunk {
  readonly chunkHash: string;
  readonly payload: Uint8Array;
}

// ---- hashes and keys -------------------------------------------------------------------------------------------------

// `ToLowerInvariant`: each character on its own, so no context rule applies (a final sigma stays `σ`), and a character
// whose lower case is not a single character (`İ`) stays as it is.
const lowerInvariant = (text: string): string =>
  Array.from(text, character => { const lower = character.toLowerCase(); return Array.from(lower).length === 1 ? lower : character; }).join("");

/** `CultMeshCdn.NormalizeHash`: a case-insensitive `sha256:` prefix is stripped before the trim, and nothing checks for hex. */
export function normalizeHash(hash: string, parameterName = "hash"): string {
  if (isNullOrWhiteSpaceCSharp(hash)) throw new Error(`Hash must be non-empty. (Parameter '${parameterName}')`);
  return lowerInvariant(trimCSharp(hash.slice(0, 7).toLowerCase() === "sha256:" ? hash.slice(7) : hash));
}

/** The hash a file name or a fetch may rest on: `normalizeHash`, then exactly 64 lowercase hexadecimal digits. */
export function parseSha256Hex(hash: string, parameterName = "hash"): string {
  const hex = normalizeHash(hash, parameterName);
  if (!/^[0-9a-f]{64}$/.test(hex)) throw new Error(`Hash must be 64 hexadecimal digits. (Parameter '${parameterName}')`);
  return hex;
}

export async function sha256Hex(bytes: Uint8Array): Promise<string> {
  const subtle = globalThis.crypto?.subtle;
  if (!subtle) throw new Error("WebCrypto SHA-256 is unavailable.");
  return Array.from(new Uint8Array(await subtle.digest("SHA-256", bytes as BufferSource)), byte => byte.toString(16).padStart(2, "0")).join("");
}

export const chunkRecordKey = (chunkHash: string): string => `mesh:cdn:chunk:${normalizeHash(chunkHash, "chunkHash")}`;

// `CultMeshCdn.StableToken`, unit by unit: a letter or decimal digit, `-`, `_` and `.` survive, anything else (a lone
// surrogate included) becomes `-`, and a token that is empty after the trim falls back to the hash of the original.
async function stableToken(value: string): Promise<string> {
  const token = lowerInvariant(trimCSharp(value)).split("").map(unit => /[\p{L}\p{Nd}\-_.]/u.test(unit) ? unit : "-").join("").replace(/^-+|-+$/g, "");
  return token || sha256Hex(new TextEncoder().encode(value));
}

export async function artifactRecordKey(artifactId: string, version: string, contentHash: string): Promise<string> {
  if (isNullOrWhiteSpaceCSharp(artifactId)) throw new Error("Value must be non-empty. (Parameter 'artifactId')");
  const versioned = isNullOrWhiteSpaceCSharp(version) ? "unversioned" : await stableToken(version);
  return `mesh:cdn:artifact:${await stableToken(artifactId)}:${versioned}:${normalizeHash(contentHash, "contentHash")}`;
}

// ---- validation ------------------------------------------------------------------------------------------------------

/** `ValidateManifestShape`, in its order and with its messages, then the metadata keys a dictionary could not hold twice. */
export function validateManifest(manifest: CultMeshCdnArtifactManifest): void {
  if (manifest.sizeBytes < 0) throw new Error("CDN artifact manifest has a negative size.");
  let expectedOffset = 0;
  for (const reference of [...manifest.chunks].sort((a, b) => a.offset - b.offset)) {
    if (reference.offset !== expectedOffset) throw new Error("CDN artifact chunks are not contiguous.");
    if (reference.sizeBytes < 0) throw new Error("CDN artifact chunk has a negative size.");
    expectedOffset += reference.sizeBytes;
  }
  if (expectedOffset !== manifest.sizeBytes) throw new Error("CDN artifact chunk sizes do not sum to the manifest size.");
  if (new Set(manifest.metadata.map(([key]) => key)).size !== manifest.metadata.length) {
    throw new Error("CDN artifact manifest metadata has a duplicate key.");
  }
}

// ---- MessagePack -----------------------------------------------------------------------------------------------------

const concat = (parts: readonly Uint8Array[]): Uint8Array => {
  const bytes = new Uint8Array(parts.reduce((sum, part) => sum + part.length, 0));
  parts.reduce((offset, part) => (bytes.set(part, offset), offset + part.length), 0);
  return bytes;
};

const mapHeader = (count: number): Uint8Array =>
  count < 16 ? Uint8Array.of(0x80 | count)
    : count < 0x10000 ? Uint8Array.of(0xde, count >> 8, count & 0xff)
      : Uint8Array.of(0xdf, count >>> 24, (count >> 16) & 0xff, (count >> 8) & 0xff, count & 0xff);

/** The positional ten-array of the C# `[MessagePackObject]`: chunk references are four-arrays, metadata a map in entry order. */
export function encodeManifest(manifest: CultMeshCdnArtifactManifest): Uint8Array {
  const { artifactId, kind, version, contentHash, sizeBytes, mimeType, createdAtUtc, chunks, tags, metadata } = manifest;
  return concat([
    Uint8Array.of(0x9a),
    ...[artifactId, kind, version, contentHash, sizeBytes, mimeType, createdAtUtc].map(field => encode(field)),
    encode(chunks.map(c => [c.chunkHash, c.offset, c.sizeBytes, c.recordKey])),
    encode(tags),
    mapHeader(metadata.length),
    ...metadata.flatMap(([key, value]) => [encode(key), encode(value)]),
  ]);
}

const wrongType = (field: string): never => { throw new Error(`CDN artifact manifest field '${field}' has the wrong type.`); };
const text = (value: unknown, field: string): string => typeof value === "string" ? value : wrongType(field);
const list = (value: unknown, field: string): unknown[] => Array.isArray(value) ? value : wrongType(field);
const record = (value: unknown, field: string): Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value) && !(value instanceof Uint8Array) ? value as Record<string, unknown> : wrongType(field);
// Any integer width, as the reference reads. A 64-bit value a JS number cannot hold exactly rounds to 2^53 or beyond, which is
// never a safe integer: it is refused, never silently rounded.
const integer = (value: unknown, field: string): number => Number.isSafeInteger(value) ? value as number : wrongType(field);

/** The reference's reader: depth first, exact types, then `validateManifest`. */
export function decodeManifest(bytes: Uint8Array): CultMeshCdnArtifactManifest {
  checkPayloadDepth(bytes, "CDN artifact manifest");
  // The decoder builds a JS object, which reorders integer-like keys; the converter sees the keys in wire order.
  const keys: string[] = [];
  const decoder = new Decoder({ mapKeyConverter: key => { keys.push(text(key, "metadata")); return key as string; } });
  const fields = list(decoder.decode(bytes), "manifest");
  if (fields.length !== 10) wrongType("manifest");
  const map = record(fields[9], "metadata");
  const manifest: CultMeshCdnArtifactManifest = {
    artifactId: text(fields[0], "artifactId"),
    kind: text(fields[1], "kind"),
    version: text(fields[2], "version"),
    contentHash: text(fields[3], "contentHash"),
    sizeBytes: integer(fields[4], "sizeBytes"),
    mimeType: text(fields[5], "mimeType"),
    createdAtUtc: text(fields[6], "createdAtUtc"),
    chunks: list(fields[7], "chunks").map(item => {
      const chunk = list(item, "chunks");
      if (chunk.length !== 4) wrongType("chunks");
      const sizeBytes = integer(chunk[2], "chunks.sizeBytes");
      return { chunkHash: text(chunk[0], "chunks.chunkHash"), offset: integer(chunk[1], "chunks.offset"),
        sizeBytes: sizeBytes >= -(2 ** 31) && sizeBytes < 2 ** 31 ? sizeBytes : wrongType("chunks.sizeBytes"), recordKey: text(chunk[3], "chunks.recordKey") };
    }),
    tags: list(fields[8], "tags").map(tag => text(tag, "tags")),
    metadata: keys.map(key => [key, text(map[key], "metadata")] as const),
  };
  validateManifest(manifest);
  return manifest;
}

// ---- packing ---------------------------------------------------------------------------------------------------------

/** `CultMeshCdn.PackArtifact`: content-addressed chunks and the manifest that names them. An empty body is one empty chunk. */
export async function packContent(
  artifactId: string,
  bytes: Uint8Array,
  options: CultMeshCdnPackOptions = {},
): Promise<{ manifest: CultMeshCdnArtifactManifest; chunks: CultMeshCdnPackedChunk[] }> {
  if (isNullOrWhiteSpaceCSharp(artifactId)) throw new Error("Artifact id must be non-empty. (Parameter 'artifactId')");
  const size = options.chunkSizeBytes ?? DEFAULT_CHUNK_SIZE_BYTES;
  if (!Number.isInteger(size) || size < 1 || size > 2 ** 31 - 1) throw new Error("ChunkSizeBytes must be between 1 and 2147483647.");
  const offsets = bytes.length === 0 ? [0] : Array.from({ length: Math.ceil(bytes.length / size) }, (_, index) => index * size);
  const chunks = await Promise.all(offsets.map(async offset => {
    const payload = bytes.slice(offset, offset + size);
    return { chunkHash: await sha256Hex(payload), payload };
  }));
  const manifest: CultMeshCdnArtifactManifest = {
    artifactId,
    kind: isNullOrWhiteSpaceCSharp(options.kind) ? "asset" : options.kind!,
    version: options.version ?? "",
    contentHash: await sha256Hex(bytes),
    sizeBytes: bytes.length,
    mimeType: isNullOrWhiteSpaceCSharp(options.mimeType) ? "application/octet-stream" : options.mimeType!,
    createdAtUtc: options.createdAtUtc ?? `${new Date().toISOString().slice(0, 23)}0000+00:00`,
    chunks: chunks.map(({ chunkHash, payload }, index) => ({ chunkHash, offset: offsets[index]!, sizeBytes: payload.length, recordKey: chunkRecordKey(chunkHash) })),
    tags: options.tags ?? [],
    metadata: options.metadata ?? [],
  };
  validateManifest(manifest);
  return { manifest, chunks };
}
