// The one TypeScript owner of the CultMesh CDN shapes: the manifest and chunk reference, their MessagePack form, hash
// normalization, both record-key grammars, SHA-256 hashing and packing. The C# reference is
// `src/GameCult.Mesh/CultMeshCdn.cs`; `contracts/cultmesh/content-vectors.*.json` and `cdn-record-key-vectors.json` pin
// the bytes and the keys.
//
// Browser-safe by construction: `crypto.subtle`, `TextEncoder`, `Uint8Array` and the codecs this package already ships.
// No `node:` import. A manifest names its chunks by hash and carries no address: where bytes are fetched from is the
// caller's to resolve through CultMesh.

import { encode } from "@msgpack/msgpack";
import { checkPayloadDepth } from "@gamecult/cultcache-ts/document";
import { isNullOrWhiteSpaceCSharp, trimCSharp } from "./cultmesh-authority";
import { wellFormed } from "./utf8-bound";

/** C#'s `string.IsNullOrWhiteSpace`, the one blank rule every consumer of the content shapes shares. */
export { isNullOrWhiteSpaceCSharp };

export const CULTMESH_CDN_MANIFEST_SCHEMA = "gamecult.mesh.cdn_artifact_manifest.v1";
export const DEFAULT_CHUNK_SIZE_BYTES = 4 * 1024 * 1024;
export const EMPTY_SHA256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

// The reference's value model: a string, the tag array and the metadata map are reference types there, so MessagePack
// nil reads as null and writes back as nil. `chunks` is the exception: `ValidateManifest` refuses a nil one.
export interface CultMeshCdnChunkRef {
  readonly chunkHash: string | null;
  readonly offset: number;
  readonly sizeBytes: number;
  readonly recordKey: string | null;
}

// Metadata is ordered entries, never an object: a JS object reorders integer-like keys, and the entry order is wire bytes.
export interface CultMeshCdnArtifactManifest {
  readonly artifactId: string | null;
  readonly kind: string | null;
  readonly version: string | null;
  readonly contentHash: string | null;
  readonly sizeBytes: number;
  readonly mimeType: string | null;
  readonly createdAtUtc: string | null;
  readonly chunks: readonly CultMeshCdnChunkRef[];
  readonly tags: readonly (string | null)[] | null;
  readonly metadata: readonly (readonly [string, string | null])[] | null;
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
export function normalizeHash(hash: string | null | undefined, parameterName = "hash"): string {
  if (isNullOrWhiteSpaceCSharp(hash)) throw new Error(`Hash must be non-empty. (Parameter '${parameterName}')`);
  return lowerInvariant(trimCSharp(hash!.slice(0, 7).toLowerCase() === "sha256:" ? hash!.slice(7) : hash!));
}

/** The hash a file name or a fetch may rest on: `normalizeHash`, then exactly 64 lowercase hexadecimal digits. */
export function parseSha256Hex(hash: string | null | undefined, parameterName = "hash"): string {
  const hex = normalizeHash(hash, parameterName);
  if (!/^[0-9a-f]{64}$/.test(hex)) throw new Error(`Hash must be 64 hexadecimal digits. (Parameter '${parameterName}')`);
  return hex;
}

export async function sha256Hex(bytes: Uint8Array): Promise<string> {
  const subtle = globalThis.crypto?.subtle;
  if (!subtle) throw new Error("WebCrypto SHA-256 is unavailable.");
  return Array.from(new Uint8Array(await subtle.digest("SHA-256", bytes as BufferSource)), byte => byte.toString(16).padStart(2, "0")).join("");
}

export const chunkRecordKey = (chunkHash: string | null | undefined): string => `mesh:cdn:chunk:${normalizeHash(chunkHash, "chunkHash")}`;

// `CultMeshCdn.StableToken`, unit by unit: a letter or decimal digit, `-`, `_` and `.` survive, anything else (a lone
// surrogate included) becomes `-`, and a token that is empty after the trim falls back to the hash of the original.
async function stableToken(value: string): Promise<string> {
  const token = lowerInvariant(trimCSharp(value)).split("").map(unit => /[\p{L}\p{Nd}\-_.]/u.test(unit) ? unit : "-").join("").replace(/^-+|-+$/g, "");
  return token || sha256Hex(new TextEncoder().encode(value));
}

export async function artifactRecordKey(artifactId: string | null, version: string | null, contentHash: string | null): Promise<string> {
  if (isNullOrWhiteSpaceCSharp(artifactId)) throw new Error("Value must be non-empty. (Parameter 'artifactId')");
  const versioned = isNullOrWhiteSpaceCSharp(version) ? "unversioned" : await stableToken(version!);
  return `mesh:cdn:artifact:${await stableToken(artifactId!)}:${versioned}:${normalizeHash(contentHash, "contentHash")}`;
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
  const metadata = manifest.metadata ?? [];
  if (new Set(metadata.map(([key]) => key)).size !== metadata.length) {
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

/** A string slot is written as C# writes it: a lone surrogate has no UTF-8 encoding and becomes U+FFFD at every length. */
const text = <T>(field: T): T => (typeof field === "string" ? wellFormed(field) : field) as T;

/** The positional ten-array of the C# `[MessagePackObject]`: chunk references are four-arrays, metadata a map in entry order. */
export function encodeManifest(manifest: CultMeshCdnArtifactManifest): Uint8Array {
  const { artifactId, kind, version, contentHash, sizeBytes, mimeType, createdAtUtc, chunks, tags, metadata } = manifest;
  return concat([
    Uint8Array.of(0x9a),
    ...[artifactId, kind, version, contentHash, sizeBytes, mimeType, createdAtUtc].map(field => encode(text(field))),
    encode(chunks.map(c => [text(c.chunkHash), c.offset, c.sizeBytes, text(c.recordKey)])),
    encode(tags === null ? null : tags.map(text)),
    ...(metadata === null ? [encode(null)] : [mapHeader(metadata.length), ...metadata.flatMap(([key, value]) => [encode(text(key)), encode(text(value))])]),
  ]);
}

const wrongType = (field: string): never => { throw new Error(`CDN artifact manifest field '${field}' has the wrong type.`); };

// MessagePack-CSharp's reader for the manifest, family by family: the integer rule must see the wire family (a float
// encoding of 0 is not an integer there), a string is `str` or nil, an array header is an array, and the invalid UTF-8
// of a string decodes to U+FFFD as .NET decodes it. `ignoreBOM` keeps a leading U+FEFF, as `Encoding.GetString` does.
const utf8 = new TextDecoder("utf-8", { ignoreBOM: true });
const SAFE = BigInt(Number.MAX_SAFE_INTEGER);
// Payload size after the code byte, or the width of the length that precedes a variable payload.
const FIXED: Readonly<Record<number, number>> = { 0xca: 4, 0xcb: 8, 0xcc: 1, 0xcd: 2, 0xce: 4, 0xcf: 8, 0xd0: 1, 0xd1: 2, 0xd2: 4, 0xd3: 8, 0xd4: 2, 0xd5: 3, 0xd6: 5, 0xd7: 9, 0xd8: 17 };
const BYTES: Readonly<Record<number, number>> = { 0xc4: 1, 0xc5: 2, 0xc6: 4, 0xd9: 1, 0xda: 2, 0xdb: 4 };
const EXT: Readonly<Record<number, number>> = { 0xc7: 1, 0xc8: 2, 0xc9: 4 };
const ARRAY: Readonly<Record<number, number>> = { 0xdc: 2, 0xdd: 4 };
const MAP: Readonly<Record<number, number>> = { 0xde: 2, 0xdf: 4 };

class Reader {
  position = 0;
  private readonly view: DataView;
  constructor(private readonly bytes: Uint8Array) {
    this.view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  }

  private take(count: number): number {
    const start = this.position;
    if (count > this.bytes.length - start) throw new Error("CDN artifact manifest ends before its last field.");
    this.position += count;
    return start;
  }

  private code(): number { return this.bytes[this.take(1)]!; }

  private width(size: number): number {
    const at = this.take(size);
    return size === 1 ? this.view.getUint8(at) : size === 2 ? this.view.getUint16(at) : this.view.getUint32(at);
  }

  nil(): boolean {
    if (this.bytes[this.position] !== 0xc0) return false;
    this.position += 1;
    return true;
  }

  array(field: string): number {
    const code = this.code();
    return code >= 0x90 && code <= 0x9f ? code & 0x0f : ARRAY[code] ? this.width(ARRAY[code]!) : wrongType(field);
  }

  map(field: string): number {
    const code = this.code();
    return code >= 0x80 && code <= 0x8f ? code & 0x0f : MAP[code] ? this.width(MAP[code]!) : wrongType(field);
  }

  string(field: string): string | null {
    if (this.nil()) return null;
    const code = this.code();
    const length = code >= 0xa0 && code <= 0xbf ? code & 0x1f : code === 0xd9 || code === 0xda || code === 0xdb ? this.width(BYTES[code]!) : wrongType(field);
    const start = this.take(length);
    return utf8.decode(this.bytes.subarray(start, start + length));
  }

  // Any integer family; a 64-bit value a JS number cannot hold exactly is refused, never silently rounded.
  integer(field: string): number {
    const code = this.code();
    if (code <= 0x7f) return code;
    if (code >= 0xe0) return code - 0x100;
    const at = (code >= 0xcc && code <= 0xd3) ? this.take(FIXED[code]!) : wrongType(field);
    const big = code === 0xcf ? this.view.getBigUint64(at) : code === 0xd3 ? this.view.getBigInt64(at) : undefined;
    if (big !== undefined) return big >= -SAFE && big <= SAFE ? Number(big) : wrongType(field);
    return code === 0xcc ? this.view.getUint8(at) : code === 0xcd ? this.view.getUint16(at) : code === 0xce ? this.view.getUint32(at)
      : code === 0xd0 ? this.view.getInt8(at) : code === 0xd1 ? this.view.getInt16(at) : this.view.getInt32(at);
  }

  // `MessagePackReader.Skip`: one value of any family, nested containers included (the depth rule has already bounded them).
  skip(): void {
    for (let pending = 1; pending > 0; pending -= 1) {
      const code = this.code();
      if (code <= 0x7f || code >= 0xe0 || code === 0xc0 || code === 0xc2 || code === 0xc3) continue;
      if (code <= 0x8f) pending += 2 * (code & 0x0f);
      else if (code <= 0x9f) pending += code & 0x0f;
      else if (code <= 0xbf) this.take(code & 0x1f);
      else if (FIXED[code]) this.take(FIXED[code]!);
      else if (BYTES[code]) this.take(this.width(BYTES[code]!));
      else if (EXT[code]) this.take(this.width(EXT[code]!) + 1);
      else if (ARRAY[code]) pending += this.width(ARRAY[code]!);
      else if (MAP[code]) pending += 2 * this.width(MAP[code]!);
      else wrongType("manifest");
    }
  }
}

/** The reference's reader then `validateManifest`: depth first, then the positional array as MessagePack-CSharp reads it. */
export function decodeManifest(bytes: Uint8Array): CultMeshCdnArtifactManifest {
  checkPayloadDepth(bytes, "CDN artifact manifest");
  const reader = new Reader(bytes);
  // A slot the array does not reach keeps the property initialiser; a slot past the tenth is skipped. Trailing bytes are not read.
  const count = reader.array("manifest");
  let index = 0;
  const slot = <T>(read: () => T, initial: T): T => index++ < count ? read() : initial;
  const manifest: CultMeshCdnArtifactManifest = {
    artifactId: slot(() => reader.string("artifactId"), ""),
    kind: slot(() => reader.string("kind"), "asset"),
    version: slot(() => reader.string("version"), ""),
    contentHash: slot(() => reader.string("contentHash"), ""),
    sizeBytes: slot(() => reader.integer("sizeBytes"), 0),
    mimeType: slot(() => reader.string("mimeType"), "application/octet-stream"),
    createdAtUtc: slot(() => reader.string("createdAtUtc"), ""),
    chunks: slot(() => Array.from({ length: reader.array("chunks") }, () => readChunkRef(reader)), []),
    tags: slot(() => reader.nil() ? null : Array.from({ length: reader.array("tags") }, () => reader.string("tags")), []),
    metadata: slot(() => reader.nil() ? null : Array.from({ length: reader.map("metadata") }, () =>
      [reader.string("metadata") ?? wrongType("metadata"), reader.string("metadata")] as const), []),
  };
  for (; index < count; index += 1) reader.skip();
  validateManifest(manifest);
  return manifest;
}

function readChunkRef(reader: Reader): CultMeshCdnChunkRef {
  const count = reader.array("chunks");
  let index = 0;
  const slot = <T>(read: () => T, initial: T): T => index++ < count ? read() : initial;
  const chunk = {
    chunkHash: slot(() => reader.string("chunks.chunkHash"), ""),
    offset: slot(() => reader.integer("chunks.offset"), 0),
    // The reference's `int`.
    sizeBytes: slot(() => { const size = reader.integer("chunks.sizeBytes"); return size >= -(2 ** 31) && size < 2 ** 31 ? size : wrongType("chunks.sizeBytes"); }, 0),
    recordKey: slot(() => reader.string("chunks.recordKey"), ""),
  };
  for (; index < count; index += 1) reader.skip();
  return chunk;
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
