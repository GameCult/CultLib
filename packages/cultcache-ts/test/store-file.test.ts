import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdirSync, readFileSync } from "node:fs";
import { mkdtemp, readFile, readdir, rm, writeFile } from "node:fs/promises";
import fsp = require("node:fs/promises");
import { tmpdir } from "node:os";
import { basename, join, resolve } from "node:path";
import test from "node:test";
import { promisify } from "node:util";

import { decode, encode } from "@msgpack/msgpack";

import { StoreLinkedError as PackageStoreLinkedError } from "../src/index";
import { documentPayloadBytes, documentPayloadFromBytes } from "../src/document";
import { SingleFileMessagePackBackingStore } from "../src/single-file-messagepack-backing-store";
import { SchemaConflictError, StoreUnreadableError } from "../src/store-format";
import type { CultCacheEnvelope } from "../src/types";

import {
  MOVEFILE_REPLACE_WRITE_THROUGH,
  StoreReplacedNotDurableError,
  commitStoreFile,
  nodeStoreFileOps,
  replaceStoreFile,
  replaceStoreFileWindows,
  windowsKernel,
  refuseLinkedStore,
  withStoreWriteLock,
} from "../src/store-file";
import type { ReplaceKernel, StoreFileOps } from "../src/store-file";

const execFileAsync = promisify(execFile);

async function scratch(): Promise<string> {
  return mkdtemp(join(tmpdir(), "cultcache-store-file-"));
}

function recordingOps(calls: string[], overrides: Partial<StoreFileOps> = {}): StoreFileOps {
  const real = nodeStoreFileOps;
  return {
    createExclusive: async (path) => {
      calls.push("create");
      return real.createExclusive(path);
    },
    write: async (handle, bytes) => {
      calls.push("write");
      await real.write(handle, bytes);
    },
    sync: async (handle) => {
      calls.push("sync");
      await real.sync(handle);
    },
    close: async (handle) => {
      calls.push("close");
      await real.close(handle);
    },
    replace: async (staged, destination) => {
      calls.push("replace");
      await real.replace(staged, destination);
    },
    syncDirectory: async (directory) => {
      calls.push("syncDirectory");
      await real.syncDirectory(directory);
    },
    remove: async (path) => {
      calls.push("remove");
      await real.remove(path);
    },
    ...overrides,
  };
}

test("a commit syncs the staged file before it replaces the store, and the directory after (Unix)", async () => {
  const directory = await scratch();
  try {
    const store = join(directory, "store.cc");
    const calls: string[] = [];
    await commitStoreFile(store, new Uint8Array([1, 2, 3]), recordingOps(calls));

    const expected = ["create", "write", "sync", "close", "replace"];
    if (process.platform !== "win32") {
      expected.push("syncDirectory");
    }
    assert.deepEqual(calls, expected);
    assert.deepEqual([...(await readFile(store))], [1, 2, 3]);
    assert.deepEqual(await readdir(directory), ["store.cc"]);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

// The default write path, with nothing injected: every file handle it opens is observed through
// fs/promises.open (the module object, read at call time), recording what the handle did and what the
// store held at that moment. The replace is seen as the store's bytes changing, so the order holds on
// every platform without a seam in the replace itself.
test("the default write path syncs and closes the staged file before the store changes, and syncs the directory after (Unix)", async () => {
  const directory = await scratch();
  const realOpen = fsp.open;
  try {
    const store = join(directory, "store.cc");
    await writeFile(store, "old");
    const storeNow = () => readFileSync(store, "utf8");
    const events: string[] = [];
    (fsp as { open: typeof fsp.open }).open = (async (path: string, flags?: string) => {
      const handle = await realOpen(path, flags as never);
      const kind = basename(String(path)) === basename(directory) ? "dir" : String(path).includes(".tmp-") ? "staged" : "other";
      const sync = handle.sync.bind(handle);
      const close = handle.close.bind(handle);
      handle.sync = () => {
        events.push(`sync:${kind}:${storeNow()}`);
        return sync();
      };
      handle.close = () => {
        events.push(`close:${kind}:${storeNow()}`);
        return close();
      };
      return handle;
    }) as typeof fsp.open;

    await commitStoreFile(store, new TextEncoder().encode("new"));
    (fsp as { open: typeof fsp.open }).open = realOpen;

    // The staged file is synced, then closed, while the store still holds its old bytes.
    assert.deepEqual(events.filter((event) => event.includes(":staged:")), ["sync:staged:old", "close:staged:old"]);
    if (process.platform === "win32") {
      assert.deepEqual(events.filter((event) => event.includes(":dir:")), [], "Windows relies on the replace and opens no directory");
    } else {
      // The directory is synced and closed after the store changed.
      assert.deepEqual(events.filter((event) => event.includes(":dir:")), ["sync:dir:new", "close:dir:new"]);
    }
    assert.equal(storeNow(), "new");
  } finally {
    (fsp as { open: typeof fsp.open }).open = realOpen;
    await rm(directory, { recursive: true, force: true });
  }
});

test("a commit that fails before the replace leaves the store byte for byte and no staged file behind", async () => {
  const directory = await scratch();
  try {
    const store = join(directory, "store.cc");
    await writeFile(store, Buffer.from([9, 9, 9]));
    const calls: string[] = [];
    const ops = recordingOps(calls, {
      replace: async () => {
        calls.push("replace");
        throw new Error("replace refused");
      },
    });

    await assert.rejects(commitStoreFile(store, new Uint8Array([1]), ops), /replace refused/);

    assert.deepEqual([...(await readFile(store))], [9, 9, 9]);
    assert.deepEqual(await readdir(directory), ["store.cc"]);
    assert.ok(calls.includes("remove"));
    assert.equal(calls.filter((call) => call === "close").length, 1, "the staged handle is closed exactly once");
    assert.ok(!calls.includes("syncDirectory"));
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("a commit whose staged file cannot be synced never replaces the store", async () => {
  const directory = await scratch();
  try {
    const store = join(directory, "store.cc");
    await writeFile(store, Buffer.from([9]));
    const calls: string[] = [];
    const ops = recordingOps(calls, {
      sync: async () => {
        calls.push("sync");
        throw new Error("disk full");
      },
    });

    await assert.rejects(commitStoreFile(store, new Uint8Array([1]), ops), /disk full/);

    assert.ok(!calls.includes("replace"));
    assert.ok(calls.indexOf("close") !== -1 && calls.indexOf("close") < calls.indexOf("remove"), "the staged handle is closed, then the file removed");
    assert.deepEqual([...(await readFile(store))], [9]);
    assert.deepEqual(await readdir(directory), ["store.cc"]);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test(
  "a directory sync failure after the replace reports the store replaced but not durable",
  { skip: process.platform === "win32" },
  async () => {
    const directory = await scratch();
    try {
      const store = join(directory, "store.cc");
      await writeFile(store, Buffer.from([9]));
      const ops = recordingOps([], {
        syncDirectory: async () => {
          throw new Error("EIO");
        },
      });

      await assert.rejects(commitStoreFile(store, new Uint8Array([1]), ops), (error: unknown) => {
        assert.ok(error instanceof StoreReplacedNotDurableError);
        assert.equal(error.replaced, true);
        assert.equal(error.name, "StoreReplacedNotDurableError");
        assert.match(error.message, /was replaced/);
        return true;
      });

      assert.deepEqual([...(await readFile(store))], [1]);
    } finally {
      await rm(directory, { recursive: true, force: true });
    }
  },
);

function fakeKernel(
  posix: number[],
  moveEx: number[],
): { kernel: ReplaceKernel; log: string[]; sleeps: number[] } {
  const log: string[] = [];
  const sleeps: number[] = [];
  const kernel: ReplaceKernel = {
    renamePosix: () => {
      log.push("posix");
      return posix.shift() ?? 0;
    },
    moveFileEx: (_staged, _destination, flags) => {
      log.push(`moveex:${flags}`);
      return moveEx.shift() ?? 0;
    },
    sleep: async (ms) => {
      sleeps.push(ms);
    },
  };
  return { kernel, log, sleeps };
}

test("the Windows replace falls back to write-through MoveFileExW where POSIX rename is unsupported, retries 5/32/33, and gives up after 25 attempts", async () => {
  for (const unsupported of [1, 50, 87]) {
    const { kernel, log } = fakeKernel([unsupported], [0]);
    await replaceStoreFileWindows("a", "b", kernel);
    assert.deepEqual(log, ["posix", `moveex:${MOVEFILE_REPLACE_WRITE_THROUGH}`]);
  }
  assert.equal(MOVEFILE_REPLACE_WRITE_THROUGH, 0x9);

  // Once POSIX is unsupported the rest of the call stays on MoveFileExW.
  {
    const { kernel, log } = fakeKernel([87], [32, 0]);
    await replaceStoreFileWindows("a", "b", kernel);
    assert.deepEqual(log, ["posix", "moveex:9", "moveex:9"]);
  }

  for (const transient of [5, 32, 33]) {
    const { kernel, log, sleeps } = fakeKernel([transient, transient, 0], []);
    await replaceStoreFileWindows("a", "b", kernel);
    assert.deepEqual(log, ["posix", "posix", "posix"], "a transient error retries the same primitive");
    assert.deepEqual(sleeps, [20, 20]);
  }

  // A POSIX rename that works is the whole replace.
  {
    const { kernel, log, sleeps } = fakeKernel([0], []);
    await replaceStoreFileWindows("a", "b", kernel);
    assert.deepEqual(log, ["posix"]);
    assert.deepEqual(sleeps, []);
  }

  // Once on MoveFileExW, an "unsupported" code is an error in any later attempt, never another fallback.
  {
    const { kernel, log } = fakeKernel([87], [32, 87]);
    await assert.rejects(replaceStoreFileWindows("a", "b", kernel), /Win32 error 87/);
    assert.deepEqual(log, ["posix", "moveex:9", "moveex:9"]);
  }

  // After the fallback, an "unsupported" code is an error, not another fallback.
  for (const unsupported of [1, 50, 87]) {
    const { kernel, log } = fakeKernel([87], [unsupported]);
    await assert.rejects(replaceStoreFileWindows("a", "b", kernel), new RegExp(`Win32 error ${unsupported}`));
    assert.deepEqual(log, ["posix", "moveex:9"]);
  }

  {
    const { kernel, log } = fakeKernel(Array(30).fill(32), []);
    await assert.rejects(replaceStoreFileWindows("a", "b", kernel), /after 25 attempts.*32/);
    assert.equal(log.length, 25);
  }

  {
    const { kernel, log } = fakeKernel([2], []);
    await assert.rejects(replaceStoreFileWindows("a", "b", kernel), /Win32 error 2/);
    assert.equal(log.length, 1);
  }

  {
    const { kernel } = fakeKernel([87], [3]);
    await assert.rejects(replaceStoreFileWindows("a", "b", kernel), /Win32 error 3/);
  }
});

// The reader is a koffi CreateFileW holding the store sharing read, write and delete, the way
// the C# and Rust readers hold it.
function openSharingAll(path: string): { close: () => void } {
  // eslint-disable-next-line @typescript-eslint/no-var-requires
  const koffi = require("koffi");
  const lib = koffi.load("kernel32.dll");
  const createFileW = lib.func(
    "intptr_t CreateFileW(str16 name, uint32_t access, uint32_t share, void *security, uint32_t disposition, uint32_t flags, void *template)",
  );
  const closeHandle = lib.func("int CloseHandle(intptr_t handle)");
  const handle = Number(createFileW(path, 0x80000000, 0x7, null, 3, 0x80, null));
  assert.notEqual(handle, -1, "the reader could not open the store");
  let closed = false;
  return { close: () => { if (!closed) { closed = true; closeHandle(handle); } } };
}

function openSharingReadWrite(path: string): { close: () => void } {
  // eslint-disable-next-line @typescript-eslint/no-var-requires
  const koffi = require("koffi");
  const lib = koffi.load("kernel32.dll");
  const createFileW = lib.func(
    "intptr_t CreateFileW(str16 name, uint32_t access, uint32_t share, void *security, uint32_t disposition, uint32_t flags, void *template)",
  );
  const closeHandle = lib.func("int CloseHandle(intptr_t handle)");
  const handle = Number(createFileW(path, 0x80000000, 0x3, null, 3, 0x80, null));
  assert.notEqual(handle, -1, "the reader could not open the store");
  let closed = false;
  return { close: () => { if (!closed) { closed = true; closeHandle(handle); } } };
}

test(
  "windows: a store replace lands while a reader holds the store sharing delete, as C# and Rust readers do",
  { skip: process.platform !== "win32" },
  async () => {
    const directory = await scratch();
    try {
      const store = join(directory, "store.cc");
      const staged = join(directory, "staged.tmp");
      await writeFile(store, Buffer.from([1, 1, 1]));
      await writeFile(staged, Buffer.from([2, 2, 2]));
      const reader = openSharingAll(store);
      try {
        await replaceStoreFile(staged, store);
        assert.deepEqual([...(await readFile(store))], [2, 2, 2]);
      } finally {
        reader.close();
      }
    } finally {
      await rm(directory, { recursive: true, force: true });
    }
  },
);

test(
  "windows: a replace waits out a reader that holds the store without delete sharing",
  { skip: process.platform !== "win32" },
  async () => {
    const directory = await scratch();
    try {
      const store = join(directory, "store.cc");
      const staged = join(directory, "staged.tmp");
      await writeFile(store, Buffer.from([1, 1, 1]));
      await writeFile(staged, Buffer.from([2, 2, 2]));
      const reader = openSharingReadWrite(store);
      const release = setTimeout(() => reader.close(), 120);
      try {
        await replaceStoreFile(staged, store);
        assert.deepEqual([...(await readFile(store))], [2, 2, 2]);
      } finally {
        clearTimeout(release);
        reader.close();
      }
    } finally {
      await rm(directory, { recursive: true, force: true });
    }
  },
);

// ---------------------------------------------------------------------------------------------
// The cross-process write lock (TypeScript against TypeScript; C# and Rust are in cult-cache.test.ts).

const entryOf = (key: string, type = "t", marker = 0): CultCacheEnvelope => ({
  key,
  type,
  payload: documentPayloadFromBytes(Uint8Array.of(0x91, marker)),
  storedAt: "2026-10-20T00:00:00Z",
});

function sleepFor(milliseconds: number): Promise<void> {
  return new Promise((resolveSleep) => setTimeout(resolveSleep, milliseconds));
}

function within<T>(promise: Promise<T>, milliseconds: number, what: string): Promise<T> {
  let timer: NodeJS.Timeout | undefined;
  const expired = new Promise<never>((_resolve, reject) => {
    timer = setTimeout(() => reject(new Error(`${what} did not finish within ${milliseconds} ms (deadlock)`)), milliseconds);
  });
  return Promise.race([promise, expired]).finally(() => clearTimeout(timer));
}

test("a second writer in this process waits for the store lock and runs after release", async () => {
  const directory = await scratch();
  try {
    const store = join(directory, "store.cc");
    const order: string[] = [];
    let second: Promise<void> | undefined;
    await withStoreWriteLock(store, async () => {
      second = withStoreWriteLock(store, async () => {
        order.push("second");
      });
      await sleepFor(150);
      order.push("first-done");
    });
    await second;
    assert.deepEqual(order, ["first-done", "second"]);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("a link put while a writer waits on the store lock is refused once it holds the lock", { skip: process.platform === "win32" }, async () => {
  const directory = await scratch();
  try {
    const target = join(directory, "target.cc");
    await new SingleFileMessagePackBackingStore(target).push(noteEnvelope("a"));
    const before = await readFile(target);
    const writes: Array<[string, (store: SingleFileMessagePackBackingStore) => Promise<void>]> = [
      ["push", (store) => store.push(noteEnvelope("b"))],
      ["pushAll", (store) => store.pushAll([noteEnvelope("b")])],
      ["delete", (store) => store.delete(noteEnvelope("a"))],
    ];
    for (const [name, write] of writes) {
      const link = join(directory, `${name}.cc`);
      let writer: Promise<void> | undefined;
      await withStoreWriteLock(link, async () => {
        writer = write(new SingleFileMessagePackBackingStore(link));
        writer.catch(() => undefined);
        await sleepFor(150);
        await fsp.symlink(target, link); // after the writer's pre-lock look, while it waits
      });
      await assert.rejects(writer!, (error: unknown) => error instanceof PackageStoreLinkedError, name);
      assert.ok((await fsp.lstat(link)).isSymbolicLink(), `${name}: the link was replaced`);
      assert.deepEqual(await readFile(target), before, `${name}: the target changed`);
      await rm(link); // the link is refused before the lock, so remove it to probe the lock
      assert.equal(await withStoreWriteLock(link, async () => "free", { timeoutMs: 200 }), "free", `${name}: the lock is held`);
    }
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

function noteEnvelope(key: string): CultCacheEnvelope {
  return { key, type: "note", payload: documentPayloadFromBytes(Uint8Array.of(0x91, 1)), storedAt: "2026-10-20T00:00:00Z" };
}

test("the store lock gives up after its wait bound and names the lock path", async () => {
  const directory = await scratch();
  try {
    const store = join(directory, "store.cc");
    await withStoreWriteLock(store, async () => {
      await assert.rejects(
        withStoreWriteLock(store, async () => undefined, { timeoutMs: 200 }),
        (error: Error) => error.message.includes(`${store}.lock`) && /200 ms/.test(error.message),
      );
    });
    // The failed waiter released nothing it did not hold: the lock is free again.
    assert.equal(await withStoreWriteLock(store, async () => "free", { timeoutMs: 200 }), "free");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("a directory at the lock path fails at once with the lock path named, not after the 30 s wait", async () => {
  const directory = await scratch();
  try {
    const store = join(directory, "store.cc");
    mkdirSync(`${store}.lock`);
    const started = Date.now();
    let ran = false;
    // The default wait bound (30 s) is in force: only a failure that does not wait can beat the race.
    const outcome = await Promise.race([
      withStoreWriteLock(store, async () => {
        ran = true;
      }).then(
        () => ({ error: undefined as Error | undefined }),
        (error: Error) => ({ error }),
      ),
      new Promise<"waiting">((resolveWait) => setTimeout(() => resolveWait("waiting"), 3000).unref()),
    ]);
    assert.notEqual(outcome, "waiting", "the lock attempt was still waiting after 3 s");
    const { error } = outcome as { error: Error | undefined };
    assert.ok(error, "locking a directory must fail");
    assert.ok(error.message.includes(`${store}.lock`), error.message);
    assert.ok(!/timed out/.test(error.message), error.message);
    assert.ok(Date.now() - started < 3000);
    assert.equal(ran, false);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("the store lock is released when the operation throws", async () => {
  const directory = await scratch();
  try {
    const store = join(directory, "store.cc");
    await assert.rejects(withStoreWriteLock(store, async () => Promise.reject(new Error("boom"))), /boom/);
    assert.equal(await withStoreWriteLock(store, async () => "free", { timeoutMs: 200 }), "free");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

// SYNTHETIC pre-v1 vector: no real file of this shape is known to exist. A top-level array of {key,type,payload,storedAt}
// whose payload is not binary (an object, a Node Buffer JSON, an int array) is what the removed heal-on-open rewrote.
const legacyObjectPayload = (): Uint8Array =>
  encode([{ key: "note:hello", type: "note", payload: { title: "Hello" }, storedAt: "2026-10-20T00:00:00Z" }]);

test("opening a synthetic pre-v1 store with object payloads returns them normalized and leaves the file untouched, even while another holder has the lock", async () => {
  const directory = await scratch();
  try {
    const path = join(directory, "legacy.cc");
    const legacy = legacyObjectPayload();
    await writeFile(path, legacy);
    const store = new SingleFileMessagePackBackingStore(path);
    await withStoreWriteLock(path, async () => {
      const [lockHeld] = await Promise.all([readdir(directory)]);
      const envelopes = await within(store.pullAll(), 5000, "an open does not wait for the write lock");
      assert.deepEqual(envelopes.map((entry) => entry.key), ["note:hello"]);
      assert.deepEqual(decode(documentPayloadBytes(envelopes[0].payload)), { title: "Hello" });
      assert.deepEqual([...(await readFile(path))], [...legacy]);
      assert.deepEqual((await readdir(directory)).sort(), lockHeld.sort(), "an open adds no lock or temp file");
    });
    assert.deepEqual([...(await readFile(path))], [...legacy]);
    assert.deepEqual((await readdir(directory)).filter((name) => name !== "legacy.cc" && name !== "legacy.cc.lock"), []);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("opening a synthetic pre-v1 store with Buffer-JSON and int-array payloads normalizes them in memory and leaves the file untouched", async () => {
  const directory = await scratch();
  try {
    const path = join(directory, "legacy.cc");
    const inner = encode({ title: "Hello" });
    const legacy = encode([
      { key: "a", type: "note", payload: { type: "Buffer", data: [...inner] }, storedAt: "2026-10-20T00:00:00Z" },
      { key: "b", type: "note", payload: [...inner], storedAt: "2026-10-20T00:00:00Z" },
    ]);
    await writeFile(path, legacy);
    const envelopes = await new SingleFileMessagePackBackingStore(path).pullAll();
    assert.deepEqual(envelopes.map((entry) => entry.key), ["a", "b"]);
    for (const envelope of envelopes) {
      assert.deepEqual(decode(documentPayloadBytes(envelope.payload)), { title: "Hello" });
    }
    assert.deepEqual([...(await readFile(path))], [...legacy]);
    assert.deepEqual(await readdir(directory), ["legacy.cc"]);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("push and delete over a synthetic pre-v1 store write v1 and keep the other legacy entries, normalized payloads included", async () => {
  const directory = await scratch();
  try {
    const path = join(directory, "legacy.cc");
    await writeFile(path, legacyObjectPayload());
    const store = new SingleFileMessagePackBackingStore(path);
    await within(store.push(entryOf("a")), 5000, "push");
    assert.equal((decode(await readFile(path)) as unknown[])[0], "cultcache.store.v1");
    assert.deepEqual((await store.pullAll()).map((entry) => entry.key).sort(), ["a", "note:hello"]);
    await writeFile(path, legacyObjectPayload());
    await within(store.delete(entryOf("a")), 5000, "delete");
    assert.equal((decode(await readFile(path)) as unknown[])[0], "cultcache.store.v1");
    const kept = await store.pullAll();
    assert.deepEqual(kept.map((entry) => entry.key), ["note:hello"]);
    assert.deepEqual(decode(documentPayloadBytes(kept[0].payload)), { title: "Hello" });
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("two TypeScript processes pushing distinct keys to one store lose none", async () => {
  const directory = await scratch();
  try {
    const path = join(directory, "store.cc");
    const dist = resolve(__dirname, "..", "..", "dist");
    const script = `
      const { SingleFileMessagePackBackingStore } = require(${JSON.stringify(join(dist, "single-file-messagepack-backing-store.js"))});
      const { documentPayloadFromBytes } = require(${JSON.stringify(join(dist, "document.js"))});
      (async () => {
        const store = new SingleFileMessagePackBackingStore(${JSON.stringify(path)});
        for (let i = 0; i < 40; i++) {
          await store.push({ key: process.argv[1] + "-" + i, type: "t", payload: documentPayloadFromBytes(Uint8Array.of(0x90)), storedAt: "2026-10-20T00:00:00Z" });
        }
      })().catch((error) => { console.error(error); process.exit(1); });
    `;
    await Promise.all(
      ["p", "q"].map((name) => execFileAsync(process.execPath, ["-e", script, name], { timeout: 120_000 })),
    );
    const keys = (await new SingleFileMessagePackBackingStore(path).pullAll()).map((entry) => entry.key).sort();
    assert.equal(keys.length, 80);
    assert.equal(new Set(keys).size, 80);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

const snapshotOf = async (store: SingleFileMessagePackBackingStore): Promise<string[]> =>
  (await store.pullAll())
    .map((entry) => `${entry.type}/${entry.key}:${[...documentPayloadBytes(entry.payload)].at(-1)}`)
    .sort();

test("push replaces the record under the same key and type and keeps every other; retyping is delete then push", async () => {
  const directory = await scratch();
  try {
    const store = new SingleFileMessagePackBackingStore(join(directory, "store.cc"));
    await store.pushAll([entryOf("a", "t", 1), entryOf("b", "t", 2), entryOf("c", "u", 3)]);
    await store.push(entryOf("a", "t", 9));
    assert.deepEqual(await snapshotOf(store), ["t/a:9", "t/b:2", "u/c:3"]);
    await store.push(entryOf("d", "t", 4));
    assert.deepEqual(await snapshotOf(store), ["t/a:9", "t/b:2", "t/d:4", "u/c:3"]);
    await store.delete(entryOf("a", "t"));
    await store.push(entryOf("a", "u", 5));
    assert.deepEqual(await snapshotOf(store), ["t/b:2", "t/d:4", "u/a:5", "u/c:3"]);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("a push that would land a second type under an existing key is refused and the file is byte for byte unchanged", async () => {
  const directory = await scratch();
  try {
    const path = join(directory, "store.cc");
    const store = new SingleFileMessagePackBackingStore(path);
    await store.pushAll([entryOf("k", "t", 1), entryOf("m", "t", 2)]);
    const before = [...(await readFile(path))];
    await assert.rejects(
      () => store.push(entryOf("k", "u", 9)),
      (error: unknown) => error instanceof SchemaConflictError && error.recordKey === "k" && /holds record "k" as type "t"/u.test(error.message),
    );
    assert.deepEqual([...(await readFile(path))], before);
    assert.deepEqual(await snapshotOf(new SingleFileMessagePackBackingStore(path)), ["t/k:1", "t/m:2"]);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("a pushAll batch that carries one key under two types is refused and writes nothing", async () => {
  const directory = await scratch();
  try {
    const path = join(directory, "store.cc");
    const store = new SingleFileMessagePackBackingStore(path);
    await assert.rejects(() => store.pushAll([entryOf("k", "t", 1), entryOf("k", "u", 2)]), SchemaConflictError);
    assert.equal((await readdir(directory)).includes("store.cc"), false);
    await store.pushAll([entryOf("a", "t", 1)]);
    const before = [...(await readFile(path))];
    await assert.rejects(() => store.pushAll([entryOf("k", "t", 1), entryOf("k", "u", 2)]), SchemaConflictError);
    assert.deepEqual([...(await readFile(path))], before);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("a pushAll batch that carries one key twice under one type keeps the last and the file reads back with one record", async () => {
  const directory = await scratch();
  try {
    const path = join(directory, "store.cc");
    await new SingleFileMessagePackBackingStore(path).pushAll([entryOf("k", "t", 1), entryOf("a", "t", 3), entryOf("k", "t", 2)]);
    assert.deepEqual(await snapshotOf(new SingleFileMessagePackBackingStore(path)), ["t/a:3", "t/k:2"]);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("a legacy envelope array that holds a key under two types is refused on open, as a v1 store is", async () => {
  const directory = await scratch();
  try {
    const path = join(directory, "legacy.cc");
    const bytes = encode([
      { key: "a", type: "t", payload: { n: 1 }, storedAt: "2026-10-20T00:00:00Z" },
      { key: "a", type: "u", payload: { n: 2 }, storedAt: "2026-10-20T00:00:00Z" },
    ]);
    await writeFile(path, bytes);
    const store = new SingleFileMessagePackBackingStore(path);
    await assert.rejects(
      () => store.pullAll(),
      (error: unknown) => error instanceof StoreUnreadableError && /"a" appears more than once/u.test(error.message),
    );
    await assert.rejects(() => store.push(entryOf("b")), StoreUnreadableError);
    assert.deepEqual([...(await readFile(path))], [...bytes]);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("delete removes only the record at that type and key", async () => {
  const directory = await scratch();
  try {
    const store = new SingleFileMessagePackBackingStore(join(directory, "store.cc"));
    await store.pushAll([entryOf("a", "t", 1), entryOf("b", "t", 2), entryOf("c", "u", 3)]);
    await store.delete(entryOf("a", "t"));
    assert.deepEqual(await snapshotOf(store), ["t/b:2", "u/c:3"]);
    await store.delete(entryOf("zzz", "t"));
    await store.delete(entryOf("c", "t"));
    assert.deepEqual(await snapshotOf(store), ["t/b:2", "u/c:3"]);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("pushAll writes onto the store as it is: it replaces by key and removes nothing, and a soft pushAll leaves an existing store alone", async () => {
  const directory = await scratch();
  try {
    const path = join(directory, "store.cc");
    const store = new SingleFileMessagePackBackingStore(path);
    await store.pushAll([entryOf("a", "t", 1)], { soft: true });
    assert.deepEqual(await snapshotOf(store), ["t/a:1"]);
    const before = await readFile(path);
    await store.pushAll([entryOf("b", "t", 2)], { soft: true });
    assert.deepEqual([...(await readFile(path))], [...before], "soft leaves an existing store byte for byte");
    await store.pushAll([entryOf("b", "t", 2)]);
    assert.deepEqual(await snapshotOf(store), ["t/a:1", "t/b:2"], "a record the batch does not name stays");
    await store.pushAll([entryOf("a", "t", 7)]);
    assert.deepEqual(await snapshotOf(store), ["t/a:7", "t/b:2"], "a record the batch names is replaced");
    await store.pushAll([]);
    assert.deepEqual(await snapshotOf(store), ["t/a:7", "t/b:2"], "an empty flush from a cache that holds nothing wipes nothing");
    const before2 = [...(await readFile(path))];
    await assert.rejects(() => store.pushAll([entryOf("a", "u", 1)]), SchemaConflictError);
    assert.deepEqual([...(await readFile(path))], before2, "a batch that retypes a held key is refused and the file is unchanged");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("a read of a current store never waits for the store lock, and a missing store reads empty", async () => {
  const directory = await scratch();
  try {
    const path = join(directory, "store.cc");
    const store = new SingleFileMessagePackBackingStore(path);
    await store.push(entryOf("a"));
    await withStoreWriteLock(path, async () => {
      assert.deepEqual(await within(store.pullAll().then((rows) => rows.map((row) => row.key)), 2000, "a read"), ["a"]);
      const missing = new SingleFileMessagePackBackingStore(join(directory, "absent.cc"));
      assert.deepEqual(await within(missing.pullAll(), 2000, "a read of a missing store"), []);
    });
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

// A share-none open succeeds only when no other handle on the file is open, as C# readers and
// writers need it to.
function canOpenShareNone(path: string): boolean {
  // eslint-disable-next-line @typescript-eslint/no-var-requires
  const koffi = require("koffi");
  const lib = koffi.load("kernel32.dll");
  const createFileW = lib.func(
    "intptr_t CreateFileW(str16 name, uint32_t access, uint32_t share, void *security, uint32_t disposition, uint32_t flags, void *template)",
  );
  const closeHandle = lib.func("int CloseHandle(intptr_t handle)");
  const handle = Number(createFileW(path, 0x80000000, 0, null, 3, 0x80, null));
  if (handle === -1) {
    return false;
  }

  closeHandle(handle);
  return true;
}

test(
  "windows: the real kernel replaces through both primitives and reports the Win32 error of a missing source",
  { skip: process.platform !== "win32" },
  async () => {
    const directory = await scratch();
    try {
      const kernel = windowsKernel();
      for (const [name, replace] of [
        ["posix", (staged: string, store: string) => kernel.renamePosix(staged, store)],
        ["moveex", (staged: string, store: string) => kernel.moveFileEx(staged, store, MOVEFILE_REPLACE_WRITE_THROUGH)],
      ] as const) {
        const store = join(directory, `${name}.cc`);
        const staged = join(directory, `${name}.tmp`);
        await writeFile(store, Buffer.from([1]));
        await writeFile(staged, Buffer.from([2]));
        assert.equal(replace(staged, store), 0, name);
        assert.ok(canOpenShareNone(store), `${name}: the replace left a handle open on the store`);
        assert.deepEqual([...(await readFile(store))], [2], name);
        assert.deepEqual(await readdir(directory).then((names) => names.filter((n) => n === `${name}.tmp`)), [], name);
        assert.equal(replace(join(directory, "missing.tmp"), store), 2, `${name}: ERROR_FILE_NOT_FOUND`);
      }
    } finally {
      await rm(directory, { recursive: true, force: true });
    }
  },
);

test(
  "windows: after a write and after a waiter gave up, the lock file is held by no handle, so a share-none open (C#'s lock) succeeds",
  { skip: process.platform !== "win32" },
  async () => {
    const directory = await scratch();
    try {
      const store = join(directory, "store.cc");
      await withStoreWriteLock(store, async () => {
        // A waiter that polls and gives up must not leave a handle on the lock file either.
        await assert.rejects(withStoreWriteLock(store, async () => undefined, { timeoutMs: 100 }), /timed out/);
      });
      // eslint-disable-next-line @typescript-eslint/no-var-requires
      const koffi = require("koffi");
      const lib = koffi.load("kernel32.dll");
      const createFileW = lib.func(
        "intptr_t CreateFileW(str16 name, uint32_t access, uint32_t share, void *security, uint32_t disposition, uint32_t flags, void *template)",
      );
      const closeHandle = lib.func("int CloseHandle(intptr_t handle)");
      const handle = Number(createFileW(`${store}.lock`, 0xc0000000, 0, null, 4, 0x80, null));
      assert.notEqual(handle, -1, "the lock file is still held open after the write");
      closeHandle(handle);
    } finally {
      await rm(directory, { recursive: true, force: true });
    }
  },
);

test("a store file of an unrecognized shape is refused, and a store naming a missing schema id is refused naming the record", async () => {
  const directory = await scratch();
  try {
    const odd = join(directory, "odd.cc");
    await writeFile(odd, encode({ not: "a store" }));
    await assert.rejects(new SingleFileMessagePackBackingStore(odd).pullAll(), /is not a recognized CultCache MessagePack store/);

    const dangling = join(directory, "dangling.cc");
    await writeFile(dangling, encode(["cultcache.store.v1", [], [["rec:1", "sha256:missing", "2026-10-20T00:00:00Z", Uint8Array.of(0x90)]]]));
    await assert.rejects(
      new SingleFileMessagePackBackingStore(dangling).pullAll(),
      /persisted record "rec:1" references missing schema id "sha256:missing"/,
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("opening writes nothing: a missing store leaves no lock file, a legacy store with byte payloads keeps its bytes", async () => {
  const directory = await scratch();
  try {
    const absent = join(directory, "nested", "absent.cc");
    assert.deepEqual(await new SingleFileMessagePackBackingStore(absent).pullAll(), []);
    assert.deepEqual(await readdir(directory), [], "a read of a missing store creates neither its directory nor its lock");

    const legacyBytes = encode([{ key: "k", type: "t", payload: Uint8Array.of(0x90), storedAt: "2026-10-20T00:00:00Z" }]);
    const path = join(directory, "legacy-bytes.cc");
    await writeFile(path, legacyBytes);
    assert.deepEqual((await new SingleFileMessagePackBackingStore(path).pullAll()).map((entry) => entry.key), ["k"]);
    assert.deepEqual([...(await readFile(path))], [...legacyBytes], "a legacy store whose payloads are already bytes is not rewritten");
    assert.deepEqual((await readdir(directory)).sort(), ["legacy-bytes.cc"]);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("StoreLinkedError is importable from the package entry and is what a write at a link throws", { skip: process.platform === "win32" }, async () => {
  const directory = await scratch();
  try {
    const target = join(directory, "target.cc");
    await writeFile(target, "x");
    const link = join(directory, "link.cc");
    await fsp.symlink(target, link);
    await assert.rejects(
      () => refuseLinkedStore(link),
      (error: unknown) => error instanceof PackageStoreLinkedError && error.path === link && error.name === "StoreLinkedError",
    );
    await assert.rejects(() => new SingleFileMessagePackBackingStore(link).pushAll([]), (error: unknown) => error instanceof PackageStoreLinkedError);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("refuseLinkedStore lets a path that is absent pass and rethrows any other lstat failure", { skip: process.platform === "win32" }, async () => {
  const directory = await scratch();
  try {
    await refuseLinkedStore(join(directory, "absent.cc"));
    const file = join(directory, "file");
    await writeFile(file, "x");
    await assert.rejects(
      () => refuseLinkedStore(join(file, "child.cc")),
      (error: unknown) => (error as NodeJS.ErrnoException).code === "ENOTDIR",
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
