// Verified reads of CultMesh CDN content over HTTP(S). This module is the only place bytes are released to a caller, and it
// releases them after judging what it received: each chunk against its hash, then the whole body against the manifest's.
// The response's declared length and coding are never consulted (Fetch decodes codings; the bytes received are the evidence).
// Hashing, hex rules, record keys, the loopback rule and manifest validation belong to cultnet-ts and are only called here.

import { EMPTY_SHA256, chunkRecordKey, parseSha256Hex, sha256Hex, validateManifest, type CultMeshCdnArtifactManifest } from "cultnet-ts/content";
import { isLoopbackEndpoint } from "cultnet-ts/authority";

export type CultMeshContentErrorCode =
  | "manifest-invalid" | "too-large" | "insecure-endpoint" | "webcrypto-unavailable" | "network"
  | "http-status" | "length-mismatch" | "chunk-hash-mismatch" | "content-hash-mismatch";

const MESSAGES: Record<CultMeshContentErrorCode, string> = {
  "manifest-invalid": "The CDN manifest or chunk description is invalid.",
  "too-large": "The content is larger than maxBytes.",
  "insecure-endpoint": "Content endpoints must be plain https, or http on loopback.",
  "webcrypto-unavailable": "WebCrypto SHA-256 is unavailable.",
  "network": "The content request failed.",
  "http-status": "The content server did not answer 200.",
  "length-mismatch": "The content body did not match the chunk size.",
  "chunk-hash-mismatch": "A content chunk did not match its hash.",
  "content-hash-mismatch": "The content did not match the manifest content hash.",
};

/** A refusal to release bytes. The message is fixed per code and never carries the endpoint or any body byte. */
export class CultMeshContentError extends Error {
  constructor(readonly code: CultMeshContentErrorCode, readonly chunkHash?: string, readonly status?: number, cause?: unknown) {
    super(MESSAGES[code], cause === undefined ? undefined : { cause });
    this.name = "CultMeshContentError";
  }
}

export interface CultMeshContentFetchOptions {
  readonly signal?: AbortSignal;
  /** Largest whole body, checked before any request. Default 256 MiB. */
  readonly maxBytes?: number;
  /** Chunk requests in flight at once, an integer 1..32. Default 4. */
  readonly maxConcurrentChunkRequests?: number;
  readonly fetch?: typeof globalThis.fetch;
}

interface Resolved { readonly doFetch: typeof globalThis.fetch; readonly caller?: AbortSignal; readonly maxBytes: number; readonly concurrency: number }
interface Job { readonly url: string; readonly hash: string; readonly slot: Uint8Array }

const fail = (code: CultMeshContentErrorCode, chunkHash?: string, status?: number, cause?: unknown) => new CultMeshContentError(code, chunkHash, status, cause);
const isByteCount = (value: number): boolean => Number.isSafeInteger(value) && value >= 0;

// Uri.EscapeDataString: encodeURIComponent, plus the five characters it leaves bare.
const escapeData = (value: string): string => encodeURIComponent(value).replace(/[!'()*]/g, c => `%${c.charCodeAt(0).toString(16).toUpperCase()}`);

// An endpoint the request URL of which is the same string in .NET `UriBuilder` and WHATWG `URL`. Both are free to rewrite what
// they dislike (`'` in a query, `{}|^\` and non-ASCII anywhere, a percent sign that is not an escape, an escaped unreserved
// character, an IDN host), and they rewrite it differently, which would split CDN cache entries between runtimes; so those
// spellings are refused instead of translated.
const PLAIN_ENDPOINT = /^(?:[A-Za-z0-9\-._~:/?#@!$&*()+,;=[\]]|%[0-9A-Fa-f]{2})*$/;
const ESCAPED_UNRESERVED = /%(?:2[de]|3\d|4[1-9a-f]|5[0-9af]|6[1-9a-f]|7[0-9ae])/i;
const isPlainEndpoint = (endpoint: string): boolean => PLAIN_ENDPOINT.test(endpoint) && !ESCAPED_UNRESERVED.test(endpoint);

/** `CultMeshHttpsContentTransportConnector.BuildChunkUri`: an existing query (even an empty one, leading `?`s trimmed) is kept and joined with `&`. */
function chunkUrl(endpoint: string, hash: string, recordKey: string): string {
  const url = new URL(endpoint);
  const kept = url.href.split("#")[0]!.includes("?") ? `${url.search.replace(/^\?+/, "")}&` : "";
  url.search = `${kept}chunkHash=${escapeData(hash)}&recordKey=${escapeData(recordKey)}`;
  return url.href;
}

/** Options, caller abort and endpoint scheme: everything that can be refused without reading the manifest. */
function resolve(endpoint: string, options: CultMeshContentFetchOptions): Resolved {
  const { signal, maxBytes = 268435456, maxConcurrentChunkRequests: concurrency = 4, fetch } = options;
  if (!Number.isInteger(concurrency) || concurrency < 1 || concurrency > 32) throw new RangeError("maxConcurrentChunkRequests must be an integer from 1 to 32.");
  if (!isByteCount(maxBytes)) throw new RangeError("maxBytes must be a non-negative safe integer.");
  if (signal?.aborted) throw signal.reason;
  const secure = (() => { try { const { protocol } = new URL(endpoint); return isPlainEndpoint(endpoint) && (protocol === "https:" || (protocol === "http:" && isLoopbackEndpoint(endpoint))); } catch { return false; } })();
  if (!secure) throw fail("insecure-endpoint");
  return { doFetch: fetch ?? ((input, init) => globalThis.fetch(input, init)), caller: signal, maxBytes, concurrency };
}

/** The one private buffer. A size the runtime cannot allocate is an artifact too large for this reader, not a bad option. */
function allocate(sizeBytes: number): Uint8Array {
  try { return new Uint8Array(sizeBytes); } catch (cause) { throw fail("too-large", undefined, undefined, cause); }
}

const requireWebCrypto = (): void => { if (!globalThis.crypto?.subtle) throw fail("webcrypto-unavailable"); };

/** One chunk: GET (a redirect is a failure: the endpoint gate cannot see where it would lead), status 200, the body streamed into its slot and cut at the first byte past the slot, then the chunk hash. */
async function readChunk(r: Resolved, signal: AbortSignal, { url, hash, slot }: Job): Promise<void> {
  if (slot.length === 0) { if (hash !== EMPTY_SHA256) throw fail("chunk-hash-mismatch", hash); return; }
  r.caller?.throwIfAborted();
  const network = (cause: unknown) => r.caller?.aborted ? r.caller.reason : fail("network", hash, undefined, cause);
  const response = await r.doFetch(url, { method: "GET", credentials: "omit", redirect: "error", signal }).catch(cause => { throw network(cause); });
  if (response.status !== 200) { void response.body?.cancel().catch(() => {}); throw fail("http-status", hash, response.status); }
  const reader = response.body?.getReader();
  let filled = 0;
  try {
    for (let part = await reader?.read(); part && !part.done; part = await reader!.read()) {
      if (filled + part.value.length > slot.length) { void reader!.cancel().catch(() => {}); throw fail("length-mismatch", hash); }
      slot.set(part.value, filled);
      filled += part.value.length;
    }
  } catch (error) { throw error instanceof CultMeshContentError ? error : network(error); }
  if (filled < slot.length) throw fail("length-mismatch", hash);
  if (await sha256Hex(slot) !== hash) throw fail("chunk-hash-mismatch", hash);
}

/** A sliding window of chunk reads. The first failure aborts every request still in flight and is thrown at once. */
async function readChunks(r: Resolved, jobs: readonly Job[]): Promise<void> {
  const internal = new AbortController();
  const relay = () => internal.abort(r.caller!.reason);
  r.caller?.addEventListener("abort", relay, { once: true });
  let next = 0;
  const worker = async () => {
    for (let job = jobs[next++]; job && !internal.signal.aborted; job = jobs[next++]) {
      try { await readChunk(r, internal.signal, job); } catch (error) { internal.abort(error); }
    }
  };
  // The rejection waits for no worker: a fetch that ignores its signal must not hold a failure back.
  const failed = new Promise<never>((_, reject) => internal.signal.addEventListener("abort", () => reject(internal.signal.reason), { once: true }));
  try { await Promise.race([Promise.all(Array.from({ length: Math.min(r.concurrency, jobs.length) }, worker)), failed]); }
  finally { r.caller?.removeEventListener("abort", relay); }
}

/** The whole body of a manifest's artifact, returned only after every chunk and the whole-body hash verified. */
export async function fetchVerifiedContent(endpoint: string, manifest: CultMeshCdnArtifactManifest, options: CultMeshContentFetchOptions = {}): Promise<Uint8Array> {
  const r = resolve(endpoint, options);
  let contentHash: string;
  let planned: { hash: string; url: string; offset: number; size: number }[];
  try {
    validateManifest(manifest);
    contentHash = parseSha256Hex(manifest.contentHash, "contentHash");
    const refs = manifest.chunks;
    if (refs.length > 1 && refs.some(ref => ref.sizeBytes === 0)) throw new Error("A zero-size chunk must be the only chunk.");
    planned = refs.map(ref => { const hash = parseSha256Hex(ref.chunkHash, "chunkHash"); return { hash, url: chunkUrl(endpoint, hash, ref.recordKey ?? ""), offset: ref.offset, size: ref.sizeBytes }; });
  } catch (cause) { throw fail("manifest-invalid", undefined, undefined, cause); }
  if (manifest.sizeBytes > r.maxBytes) throw fail("too-large");
  requireWebCrypto();
  const buffer = allocate(manifest.sizeBytes);
  await readChunks(r, planned.map(({ hash, url, offset, size }) => ({ url, hash, slot: buffer.subarray(offset, offset + size) })));
  if (await sha256Hex(buffer) !== contentHash) throw fail("content-hash-mismatch");
  r.caller?.throwIfAborted();
  return buffer;
}

/** One chunk by hash and size, over the same primitive; the record key is the one a packed manifest carries. */
export async function fetchVerifiedChunk(endpoint: string, chunkHash: string, sizeBytes: number, options: CultMeshContentFetchOptions = {}): Promise<Uint8Array> {
  const r = resolve(endpoint, options);
  let hash: string;
  try {
    hash = parseSha256Hex(chunkHash, "chunkHash");
    if (!isByteCount(sizeBytes)) throw new Error("Size must be a safe integer.");
  } catch (cause) { throw fail("manifest-invalid", undefined, undefined, cause); }
  if (sizeBytes > r.maxBytes) throw fail("too-large");
  requireWebCrypto();
  const slot = allocate(sizeBytes);
  await readChunks(r, [{ url: chunkUrl(endpoint, hash, chunkRecordKey(hash)), hash, slot }]);
  r.caller?.throwIfAborted();
  return slot;
}
