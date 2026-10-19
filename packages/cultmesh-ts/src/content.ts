import { constants } from "node:fs";
import { open } from "node:fs/promises";
import type { IncomingMessage, ServerResponse } from "node:http";
import { join } from "node:path";
import { DEFAULT_CHUNK_SIZE_BYTES, EMPTY_SHA256, chunkRecordKey, parseSha256Hex, sha256Hex } from "cultnet-ts/content";

/** What the provider reads of a request. The path and the headers are never read: the route is the query alone. */
export interface ContentChunkRequest { method: string; url: string; }
export interface ContentChunkAnswer { status: number; headers: Readonly<Record<string, string>>; body?: Uint8Array; }

/** The port a host implements. Whatever it returns is hashed before a byte is served. `chunkHash` is canonical lowercase hex. */
export interface ContentChunkStore { readChunk(chunkHash: string, maxBytes: number): Promise<Uint8Array | undefined>; }

export interface ContentChunkHandlerOptions {
  /** Largest chunk served, and the most memory one in-flight request holds. Defaults to the packer's chunk size. */
  maxChunkBytes?: number;
  /** Called for every 500 answer; a throwing hook leaves the answer unchanged. */
  onServerFault?: (fault: { chunkHash: string; kind: "hash-mismatch" | "too-large" | "store-error"; error?: unknown }) => void;
}

/** A store's refusal to read a chunk larger than `maxBytes`. */
export class ContentChunkTooLargeError extends Error {
  constructor(readonly sizeBytes: number) { super("Chunk exceeds the size bound."); }
}

const errorHeaders = { "Content-Type": "text/plain; charset=utf-8", "X-Content-Type-Options": "nosniff", "Cache-Control": "no-store", "Access-Control-Allow-Origin": "*" };
const reasons: Record<number, string> = { 400: "invalid chunk request", 404: "chunk not found", 405: "method not allowed", 500: "chunk unavailable" };

function refusal(status: number, extra: Record<string, string> = {}): ContentChunkAnswer {
  const body = new TextEncoder().encode(reasons[status]);
  return { status, headers: { ...errorHeaders, ...extra, "Content-Length": String(body.length) }, body };
}

const chunkAnswer = (body: Uint8Array): ContentChunkAnswer => ({
  status: 200,
  headers: {
    "Content-Length": String(body.length),
    "Content-Type": "application/octet-stream",
    "X-Content-Type-Options": "nosniff",
    "Cache-Control": "public, max-age=31536000, immutable, no-transform",
    "Access-Control-Allow-Origin": "*",
  },
  body,
});

/**
 * The HTTP answer to `?chunkHash=<hex>&recordKey=<key>`, as the C# connector asks it. Every chunk is hashed against its
 * name before the first byte is sent, GET and HEAD alike, and nothing is cached between requests.
 */
export function createContentChunkHandler(store: ContentChunkStore, options: ContentChunkHandlerOptions = {}): (request: ContentChunkRequest) => Promise<ContentChunkAnswer> {
  const maxChunkBytes = options.maxChunkBytes ?? DEFAULT_CHUNK_SIZE_BYTES;
  if (!Number.isInteger(maxChunkBytes) || maxChunkBytes < 1 || maxChunkBytes > 2147483647) throw new RangeError("maxChunkBytes must be an integer in 1..2147483647.");

  const fault = (chunkHash: string, kind: "hash-mismatch" | "too-large" | "store-error", error?: unknown): ContentChunkAnswer => {
    try { options.onServerFault?.({ chunkHash, kind, error }); } catch { /* a hook cannot change the answer */ }
    return refusal(500);
  };

  const serve = async (url: string): Promise<ContentChunkAnswer> => {
    let hex: string;
    try {
      const query = new URL(url, "http://provider.invalid").searchParams;
      const [hash, ...otherHashes] = query.getAll("chunkHash");
      const [key = "", ...otherKeys] = query.getAll("recordKey");
      hex = parseSha256Hex(hash ?? "");
      if (otherHashes.length || otherKeys.length || (key.trim() !== "" && key !== chunkRecordKey(hex))) return refusal(400);
    } catch { return refusal(400); }
    if (hex === EMPTY_SHA256) return chunkAnswer(new Uint8Array());
    let bytes: Uint8Array | undefined;
    try { bytes = await store.readChunk(hex, maxChunkBytes); }
    catch (error) { return fault(hex, error instanceof ContentChunkTooLargeError ? "too-large" : "store-error", error); }
    if (!bytes) return refusal(404);
    if (bytes.length > maxChunkBytes) return fault(hex, "too-large");
    // The copy is the provider's own: the bytes hashed are exactly the bytes sent, whatever the store does with its buffer afterwards.
    const owned = new Uint8Array(bytes);
    return await sha256Hex(owned) === hex ? chunkAnswer(owned) : fault(hex, "hash-mismatch");
  };

  return async ({ method, url }) => {
    if (method !== "GET" && method !== "HEAD") return refusal(405, { Allow: "GET, HEAD" });
    const { body, ...answer } = await serve(url);
    return method === "GET" ? { ...answer, body } : answer;
  };
}

/** A read-only flat directory (opened non-blocking, so a FIFO named by a hash is refused by fstat instead of holding a thread): the chunk whose hash is `<hex>` lives in `<root>/<hex>`. It writes, lists and caches nothing. */
export function createDirectoryChunkStore(root: string): ContentChunkStore {
  return {
    async readChunk(chunkHash, maxBytes) {
      if (parseSha256Hex(chunkHash) !== chunkHash) throw new Error("chunkHash must be canonical lowercase hex.");
      const file = await open(join(root, chunkHash), constants.O_RDONLY | constants.O_NONBLOCK).catch(error => { if (error?.code === "ENOENT") return undefined; throw error; });
      if (!file) return undefined;
      try {
        const stat = await file.stat();
        if (!stat.isFile()) throw new Error("Chunk is not a regular file.");
        if (stat.size > maxBytes) throw new ContentChunkTooLargeError(stat.size);
        const bytes = new Uint8Array(stat.size);
        const { bytesRead } = await file.read(bytes, 0, stat.size, 0);
        return bytes.subarray(0, bytesRead);
      } finally { await file.close(); }
    },
  };
}

/** Mounts a handler on `node:http` or `node:https`: method and URL in, status, headers and body out. A server request always has both. */
export const toNodeListener = (handler: (request: ContentChunkRequest) => Promise<ContentChunkAnswer>) =>
  (req: IncomingMessage, res: ServerResponse): void => {
    handler({ method: req.method!, url: req.url! }).catch(() => refusal(500)).then(({ status, headers, body }) => {
      res.writeHead(status, headers);
      res.end(body);
    });
  };
