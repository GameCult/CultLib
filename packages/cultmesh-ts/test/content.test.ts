import assert from "node:assert/strict";
import fsp from "node:fs/promises";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { createServer, type Server } from "node:http";
import type { AddressInfo } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test, { mock } from "node:test";
import { execFileSync } from "node:child_process";

import { EMPTY_SHA256, chunkRecordKey, packContent, sha256Hex } from "cultnet-ts/content";
import { closeSync, constants as fsConstants, openSync, symlinkSync } from "node:fs";

import {
  ContentChunkTooLargeError,
  createContentChunkHandler,
  createDirectoryChunkStore,
  toNodeListener,
  type ContentChunkAnswer,
  type ContentChunkHandlerOptions,
  type ContentChunkStore,
} from "../src/content";

// Chunks come from the packer, so the hashes are the ones a publisher writes.
const packed = async (text: string) => (await packContent("a", new TextEncoder().encode(text))).chunks[0]!;
const decode = (answer: ContentChunkAnswer) => new TextDecoder().decode(answer.body ?? new Uint8Array());
const OK_HEADERS = {
  "Content-Type": "application/octet-stream",
  "X-Content-Type-Options": "nosniff",
  "Cache-Control": "public, max-age=31536000, immutable, no-transform",
  "Access-Control-Allow-Origin": "*",
};

function spyStore(chunks: Record<string, Uint8Array>): ContentChunkStore & { calls: string[] } {
  const calls: string[] = [];
  return { calls, readChunk: async hash => { calls.push(hash); return chunks[hash]; } };
}
const ask = (store: ContentChunkStore, url: string, method = "GET", options?: ContentChunkHandlerOptions) =>
  createContentChunkHandler(store, options)({ method, url });

test("GET of a stored chunk answers 200 with exactly its bytes and the 200 header set", async () => {
  const chunk = await packed("hello chunk");
  const answer = await ask(spyStore({ [chunk.chunkHash]: chunk.payload }), `/c?chunkHash=${chunk.chunkHash}`);
  assert.equal(answer.status, 200);
  assert.deepEqual(answer.body, chunk.payload);
  assert.deepEqual(answer.headers, { ...OK_HEADERS, "Content-Length": String(chunk.payload.length) });
});

test("HEAD answers the same status and headers as GET with no body, and HEAD of a corrupt chunk answers 500", async () => {
  const chunk = await packed("hello chunk");
  const url = `/c?chunkHash=${chunk.chunkHash}`;
  const get = await ask(spyStore({ [chunk.chunkHash]: chunk.payload }), url);
  const head = await ask(spyStore({ [chunk.chunkHash]: chunk.payload }), url, "HEAD");
  assert.equal(head.status, 200);
  assert.deepEqual(head.headers, get.headers);
  assert.equal(head.body, undefined);
  const corrupt = await ask(spyStore({ [chunk.chunkHash]: new TextEncoder().encode("hello chunK") }), url, "HEAD");
  assert.equal(corrupt.status, 500);
  assert.equal(corrupt.body, undefined);
});

test("chunkHash 'SHA256:<UPPER HEX> ' answers like the canonical hash; recordKey absent, empty or exact answers 200", async () => {
  const chunk = await packed("hello chunk");
  const store = spyStore({ [chunk.chunkHash]: chunk.payload });
  const hash = encodeURIComponent(`SHA256:${chunk.chunkHash.toUpperCase()} `);
  const keys = ["", "&recordKey=", "&recordKey=%20%20", `&recordKey=${encodeURIComponent(chunkRecordKey(chunk.chunkHash))}`];
  for (const key of keys) {
    const answer = await ask(store, `/c?chunkHash=${hash}${key}`);
    assert.equal(answer.status, 200, key);
    assert.deepEqual(answer.body, chunk.payload);
  }
  assert.deepEqual(new Set(store.calls), new Set([chunk.chunkHash]));
});

test("a recordKey for another hash, a repeated recordKey, a missing or repeated chunkHash, 63 hex, non-hex, '../x' and '%2e%2e%2f' answer 400 and the store is never called", async () => {
  const chunk = await packed("hello chunk");
  const other = await packed("another");
  const store = spyStore({ [chunk.chunkHash]: chunk.payload });
  const h = chunk.chunkHash;
  const key = encodeURIComponent(chunkRecordKey(h));
  const urls = [
    `/c?chunkHash=${h}&recordKey=${encodeURIComponent(chunkRecordKey(other.chunkHash))}`,
    `/c?chunkHash=${h}&recordKey=x`,
    `/c?chunkHash=${h}&recordKey=${key}&recordKey=${key}`,
    `/c?recordKey=${key}`,
    "/c",
    `/c?chunkHash=${h}&chunkHash=${h}`,
    `/c?chunkHash=${h.slice(1)}`,
    `/c?chunkHash=${"g".repeat(64)}`,
    "/c?chunkHash=../x",
    "/c?chunkHash=%2e%2e%2f",
    "/c?chunkHash=",
  ];
  for (const url of urls) {
    const answer = await ask(store, url);
    assert.equal(answer.status, 400, url);
    assert.equal(answer.headers["Cache-Control"], "no-store");
    assert.equal(answer.headers["Content-Type"], "text/plain; charset=utf-8");
    assert.equal(decode(answer), "invalid chunk request");
  }
  assert.deepEqual(store.calls, []);
});

test("a valid unknown hash answers 404 with Cache-Control: no-store", async () => {
  const chunk = await packed("hello chunk");
  const answer = await ask(spyStore({}), `/c?chunkHash=${chunk.chunkHash}`);
  assert.equal(answer.status, 404);
  assert.equal(answer.headers["Cache-Control"], "no-store");
  assert.equal(decode(answer), "chunk not found");
});

test("the empty hash answers 200 with Content-Length 0 from a store that throws when called", async () => {
  const store: ContentChunkStore = { readChunk: async () => { throw new Error("must not be called"); } };
  const answer = await ask(store, `/c?chunkHash=${EMPTY_SHA256}`);
  assert.equal(answer.status, 200);
  assert.equal(answer.headers["Content-Length"], "0");
  assert.equal(answer.body?.length, 0);
});

test("stored bytes that do not hash to their name answer 500, the body holds none of them, and onServerFault gets hash-mismatch; a throwing hook still yields 500", async () => {
  const chunk = await packed("hello chunk");
  const canary = "CANARY-corrupt-bytes";
  const store = spyStore({ [chunk.chunkHash]: new TextEncoder().encode(canary) });
  const faults: unknown[] = [];
  const answer = await ask(store, `/c?chunkHash=${chunk.chunkHash}`, "GET", { onServerFault: f => faults.push(f) });
  assert.equal(answer.status, 500);
  assert.equal(decode(answer), "chunk unavailable");
  assert.equal(JSON.stringify(answer).includes(canary), false);
  assert.deepEqual(faults, [{ chunkHash: chunk.chunkHash, kind: "hash-mismatch", error: undefined }]);
  const throwing = await ask(store, `/c?chunkHash=${chunk.chunkHash}`, "GET", { onServerFault: () => { throw new Error("hook"); } });
  assert.equal(throwing.status, 500);
});

test("a valid 17-byte chunk with maxChunkBytes 16 answers 500 too-large, a store rejection answers 500 store-error, and maxChunkBytes 0, 2^31 and 1.5 throw RangeError", async () => {
  const chunk = await packed("0123456789abcdefg");
  assert.equal(chunk.payload.length, 17);
  const kinds: string[] = [];
  const options = { maxChunkBytes: 16, onServerFault: (f: { kind: string }) => kinds.push(f.kind) };
  const url = `/c?chunkHash=${chunk.chunkHash}`;
  assert.equal((await ask(spyStore({ [chunk.chunkHash]: chunk.payload }), url, "GET", options)).status, 500);
  const refusing: ContentChunkStore = { readChunk: async () => { throw new ContentChunkTooLargeError(17); } };
  assert.equal((await ask(refusing, url, "GET", options)).status, 500);
  const failing: ContentChunkStore = { readChunk: async () => { throw new Error("disk"); } };
  assert.equal((await ask(failing, url, "GET", options)).status, 500);
  assert.deepEqual(kinds, ["too-large", "too-large", "store-error"]);
  assert.equal((await ask(spyStore({ [chunk.chunkHash]: chunk.payload }), url, "GET", { maxChunkBytes: 17 })).status, 200);
  for (const bad of [0, 2 ** 31, 1.5, Number.NaN]) assert.throws(() => createContentChunkHandler(spyStore({}), { maxChunkBytes: bad }), RangeError, String(bad));
  assert.throws(() => createContentChunkHandler(spyStore({}), { maxChunkBytes: 0 }), /maxChunkBytes/);
  const one = await packed("x");
  assert.equal((await ask(spyStore({ [one.chunkHash]: one.payload }), `/c?chunkHash=${one.chunkHash}`, "GET", { maxChunkBytes: 1 })).status, 200);
  assert.doesNotThrow(() => createContentChunkHandler(spyStore({}), { maxChunkBytes: 2 ** 31 - 1 }));
});

test("POST, PUT, DELETE and OPTIONS answer 405 with Allow: GET, HEAD", async () => {
  const chunk = await packed("hello chunk");
  const store = spyStore({ [chunk.chunkHash]: chunk.payload });
  for (const method of ["POST", "PUT", "DELETE", "OPTIONS", ""]) {
    const answer = await ask(store, `/c?chunkHash=${chunk.chunkHash}`, method);
    assert.equal(answer.status, 405, method);
    assert.equal(answer.headers.Allow, "GET, HEAD");
    assert.equal(decode(answer), "method not allowed");
    assert.equal(answer.headers["Access-Control-Allow-Origin"], "*");
  }
  assert.deepEqual(store.calls, []);
});

test("'/anything/../etc/passwd?chunkHash=<h>' answers exactly as '/?chunkHash=<h>', and '/<h>' with no query answers 400", async () => {
  const chunk = await packed("hello chunk");
  const store = spyStore({ [chunk.chunkHash]: chunk.payload });
  const plain = await ask(store, `/?chunkHash=${chunk.chunkHash}`);
  assert.deepEqual(await ask(store, `/anything/../etc/passwd?chunkHash=${chunk.chunkHash}`), plain);
  assert.equal((await ask(store, `/${chunk.chunkHash}`)).status, 400);
});

test("error answers carry ACAO * and never echo the request's hash or key", async () => {
  const canary = "CANARY-request-input";
  const chunk = await packed("hello chunk");
  const urls = [`/c?chunkHash=${canary}`, `/c?chunkHash=${chunk.chunkHash}&recordKey=${canary}`, `/${canary}?chunkHash=${chunk.chunkHash}`];
  for (const url of urls) {
    for (const answer of [await ask(spyStore({}), url), await ask(spyStore({}), url, "POST")]) {
      assert.equal(answer.headers["Access-Control-Allow-Origin"], "*");
      assert.equal(answer.headers["X-Content-Type-Options"], "nosniff");
      assert.equal(answer.headers["Content-Length"], String(answer.body?.length));
      assert.equal(JSON.stringify(answer).includes(canary), false);
    }
  }
});

test("directory store reads canonical hex and rejects non-canonical names, a directory and an oversized file", async () => {
  const root = await mkdtemp(join(tmpdir(), "cm-content-"));
  try {
    const chunk = await packed("0123456789abcdefg");
    const store = createDirectoryChunkStore(root);
    await writeFile(join(root, chunk.chunkHash), chunk.payload);
    assert.deepEqual(await store.readChunk(chunk.chunkHash, 17), chunk.payload);
    assert.equal(await store.readChunk(await sha256Hex(new Uint8Array([1])), 17), undefined);
    // The root does not exist for these, so only the name check can reject them before a filesystem call.
    const nowhere = createDirectoryChunkStore(join(root, "missing"));
    await assert.rejects(nowhere.readChunk(chunk.chunkHash.toUpperCase(), 17), /canonical/);
    for (const name of [chunk.chunkHash.toUpperCase(), `sha256:${chunk.chunkHash}`, `../${chunk.chunkHash}`, chunk.chunkHash.slice(1), ` ${chunk.chunkHash}`]) {
      await assert.rejects(nowhere.readChunk(name, 17), name);
    }
    await assert.rejects(store.readChunk(chunk.chunkHash, 16), (error: unknown) => error instanceof ContentChunkTooLargeError && error.sizeBytes === 17);
    const dirHash = await sha256Hex(new Uint8Array([2]));
    await mkdir(join(root, dirHash));
    await assert.rejects(store.readChunk(dirHash, 1 << 20), process.platform === "win32" ? Error : /regular file/);
    // Only a missing file is "not found": a root that is a file is a store fault (ENOTDIR on POSIX; Windows reports ENOENT).
    if (process.platform !== "win32") await assert.rejects(createDirectoryChunkStore(join(root, chunk.chunkHash)).readChunk(chunk.chunkHash, 17), { code: "ENOTDIR" });
    // Through the handler: a file whose bytes do not match its name is a 500, and the right bytes are a 200.
    const wrongHash = await sha256Hex(new Uint8Array([3]));
    await writeFile(join(root, wrongHash), chunk.payload);
    const handler = createContentChunkHandler(store);
    assert.equal((await handler({ method: "GET", url: `/?chunkHash=${wrongHash}` })).status, 500);
    assert.equal((await handler({ method: "GET", url: `/?chunkHash=${chunk.chunkHash}` })).status, 200);
    assert.equal((await handler({ method: "GET", url: `/?chunkHash=${dirHash}` })).status, 500);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("over node:http through toNodeListener: GET with Range and Accept-Encoding: gzip gets 200 with the full identity body; HEAD carries Content-Length; a handler rejection answers 500", async () => {
  const chunk = await packed("hello chunk over http");
  const handler = createContentChunkHandler(spyStore({ [chunk.chunkHash]: chunk.payload }));
  const serve = async (listener: ReturnType<typeof toNodeListener>, run: (base: string) => Promise<void>) => {
    const server: Server = createServer(listener);
    await new Promise<void>(resolve => server.listen(0, "127.0.0.1", resolve));
    try { await run(`http://127.0.0.1:${(server.address() as AddressInfo).port}`); }
    finally { await new Promise<void>(resolve => { server.close(() => resolve()); server.closeAllConnections(); }); }
  };
  await serve(toNodeListener(handler), async base => {
    const url = `${base}/mount?chunkHash=${chunk.chunkHash}`;
    const get = await fetch(url, { headers: { Range: "bytes=2-5", "Accept-Encoding": "gzip" } });
    assert.equal(get.status, 200);
    assert.equal(get.headers.get("content-encoding"), null);
    assert.equal(get.headers.get("accept-ranges"), null);
    assert.deepEqual(new Uint8Array(await get.arrayBuffer()), chunk.payload);
    const head = await fetch(url, { method: "HEAD" });
    assert.equal(head.status, 200);
    assert.equal(head.headers.get("content-length"), String(chunk.payload.length));
    assert.equal((await head.arrayBuffer()).byteLength, 0);
  });
  await serve(toNodeListener(async () => { throw new Error("boom"); }), async base => {
    const response = await fetch(`${base}/`);
    assert.equal(response.status, 500);
    assert.equal(await response.text(), "chunk unavailable");
  });
});

test("directory store closes every handle it opens, whether it returns bytes, finds a directory or refuses an oversized file", async () => {
  const root = await mkdtemp(join(tmpdir(), "cm-content-"));
  const opened: Awaited<ReturnType<typeof fsp.open>>[] = [];
  const original = fsp.open;
  const spy = mock.method(fsp, "open", async (...args: Parameters<typeof original>) => { const handle = await original(...args); opened.push(handle); return handle; });
  try {
    const chunk = await packed("0123456789abcdefg");
    const dirHash = await sha256Hex(new Uint8Array([2]));
    await writeFile(join(root, chunk.chunkHash), chunk.payload);
    await mkdir(join(root, dirHash));
    const store = createDirectoryChunkStore(root);
    await store.readChunk(chunk.chunkHash, 17);
    await assert.rejects(store.readChunk(chunk.chunkHash, 16));
    await store.readChunk(dirHash, 17).catch(() => undefined);
    assert.ok(opened.length >= 2, "the store opened its files through node:fs/promises open");
    assert.deepEqual(opened.map(handle => handle.fd), opened.map(() => -1));
  } finally { spy.mock.restore(); await rm(root, { recursive: true, force: true }); }
});

test("a recordKey is compared exactly: padded and re-cased forms of the right key answer 400, tab and newline keys are blank and answer 200", async () => {
  const chunk = await packed("hello chunk");
  const store = spyStore({ [chunk.chunkHash]: chunk.payload });
  const key = chunkRecordKey(chunk.chunkHash);
  const q = (value: string) => `/c?chunkHash=${chunk.chunkHash}&recordKey=${encodeURIComponent(value)}`;
  for (const forged of [` ${key}`, `${key} `, `\t${key}`, key.toUpperCase(), key.replace("mesh", "Mesh"), "\uFEFF", `\uFEFF${key}`]) {
    assert.equal((await ask(store, q(forged))).status, 400, JSON.stringify(forged));
  }
  assert.deepEqual(store.calls, []);
  // U+0085 is white space to C# and not to JS trim(); U+FEFF is the reverse: the blank rule is C#'s, not the platform's.
  for (const blank of ["\t", "\n", "\r\n", " \t ", "\u0085", "\u00A0", " \u0085 "]) assert.equal((await ask(store, q(blank))).status, 200, JSON.stringify(blank));
  assert.equal((await ask(store, q(key))).status, 200);
});

test("only the empty chunk's own hash is answered without the store: hashes sharing its prefix or differing in the last digit reach the store", async () => {
  const flipped = EMPTY_SHA256.slice(0, -1) + (EMPTY_SHA256.endsWith("5") ? "6" : "5");
  const prefixed = EMPTY_SHA256.slice(0, 8) + "0".repeat(56);
  for (const hash of [flipped, prefixed]) {
    assert.equal(hash.length, 64);
    const store = spyStore({});
    const answer = await ask(store, `/c?chunkHash=${hash}`);
    assert.equal(answer.status, 404, hash);
    assert.deepEqual(store.calls, [hash]);
  }
});

test("a GET reads the store once and serves the bytes it hashed, not a later read", async () => {
  const first = await packed("first read");
  const second = await packed("second read");
  const reads: Uint8Array[] = [first.payload, second.payload];
  const store: ContentChunkStore = { readChunk: async () => reads.shift() };
  const answer = await ask(store, `/c?chunkHash=${first.chunkHash}`);
  assert.equal(answer.status, 200);
  assert.deepEqual(answer.body, first.payload);
  assert.equal(reads.length, 1, "the store was read exactly once");
});

test("a store that reuses its buffer cannot change the bytes after the hash: the answer is the hashed content, from a private copy", async () => {
  const chunk = await packed("hello chunk");
  const shared = Buffer.from(chunk.payload);
  // The overwrite lands in a microtask chain that runs after the handler resumes and has started hashing, before the digest resolves.
  const store: ContentChunkStore = {
    readChunk: async () => {
      let hops = 0;
      const hop = (): void => { if (++hops < 4) queueMicrotask(hop); else shared.fill(0x58); };
      queueMicrotask(hop);
      return shared;
    },
  };
  const answer = await ask(store, `/c?chunkHash=${chunk.chunkHash}`);
  assert.equal(shared[0], 0x58, "the store's buffer was overwritten while the request was in flight");
  assert.equal(answer.status, 200);
  assert.deepEqual(answer.body, chunk.payload);
  assert.notEqual(answer.body?.buffer, shared.buffer);
});

test("a store that overwrites its buffer on any microtask hop never gets other bytes served under the chunk's name", async () => {
  const chunk = await packed("hello chunk");
  const outcomes = new Set<number>();
  for (let overwriteAt = 0; overwriteAt <= 10; overwriteAt += 1) {
    const shared = Buffer.from(chunk.payload);
    const store: ContentChunkStore = {
      readChunk: async () => {
        let hops = 0;
        const hop = (): void => { if (hops++ < overwriteAt) queueMicrotask(hop); else shared.fill(0x58); };
        queueMicrotask(hop);
        return shared;
      },
    };
    const answer = await ask(store, `/c?chunkHash=${chunk.chunkHash}`);
    outcomes.add(answer.status);
    const served = answer.status === 200 && Buffer.from(answer.body ?? new Uint8Array()).equals(Buffer.from(chunk.payload));
    assert.ok(served || answer.status === 500, `overwrite after ${overwriteAt} hops answered ${answer.status} with ${decode(answer)}`);
  }
  assert.deepEqual([...outcomes].sort(), [200, 500], "the sweep covers an overwrite before the copy and one after the hash");
});

test("a FIFO named by a hash is refused as not a regular file at once and holds no thread: other chunks are still served", { skip: process.platform === "win32" }, async () => {
  const root = await mkdtemp(join(tmpdir(), "cm-content-"));
  let fifoPath: string | undefined;
  try {
    const chunk = await packed("hello chunk");
    await writeFile(join(root, chunk.chunkHash), chunk.payload);
    const fifoHash = await sha256Hex(new Uint8Array([9]));
    fifoPath = join(root, fifoHash);
    execFileSync("mkfifo", [fifoPath]);
    const store = createDirectoryChunkStore(root);
    const within = async <T,>(work: Promise<T>): Promise<T> => {
      let timer: NodeJS.Timeout | undefined;
      try { return await Promise.race([work, new Promise<never>((_, reject) => { timer = setTimeout(() => reject(new Error("blocked")), 3000); })]); }
      finally { clearTimeout(timer); }
    };
    // More than the libuv pool (4) so a blocking open would starve the pool.
    const refusals = Array.from({ length: 8 }, () => within(store.readChunk(fifoHash, 1 << 20)).then(() => "read", (error: Error) => error.message));
    assert.deepEqual(await Promise.all(refusals), Array(8).fill("Chunk is not a regular file."));
    assert.deepEqual(await within(store.readChunk(chunk.chunkHash, 1 << 20)), chunk.payload);
    assert.equal((await within(createContentChunkHandler(store)({ method: "GET", url: `/?chunkHash=${fifoHash}` }))).status, 500);
  } finally {
    // A reverted non-blocking open leaves pool threads waiting for a writer, which would keep the process alive past the failure.
    // Opening the FIFO read-write releases them, so the revert fails the test in seconds. It is synchronous and needs no
    // hash: both hashing and async fs calls queue behind the full pool.
    if (fifoPath) try { closeSync(openSync(fifoPath, fsConstants.O_RDWR | fsConstants.O_NONBLOCK)); } catch { /* already gone */ }
    await rm(root, { recursive: true, force: true });
  }
});

test("a character device named by a hash is refused as not a regular file, whatever the user id", { skip: process.platform === "win32" }, async () => {
  const root = await mkdtemp(join(tmpdir(), "cm-content-"));
  try {
    const store = createDirectoryChunkStore(root);
    for (const [index, device] of ["/dev/null", "/dev/zero"].entries()) {
      const hash = await sha256Hex(new Uint8Array([index]));
      symlinkSync(device, join(root, hash));
      await assert.rejects(store.readChunk(hash, 1 << 20), { message: "Chunk is not a regular file." }, device);
      const seen: string[] = [];
      const answer = await createContentChunkHandler(store, { onServerFault: fault => seen.push(fault.kind) })({ method: "GET", url: `/?chunkHash=${hash}` });
      assert.equal(answer.status, 500, device);
      assert.deepEqual(seen, ["store-error"], device);
    }
  } finally { await rm(root, { recursive: true, force: true }); }
});
