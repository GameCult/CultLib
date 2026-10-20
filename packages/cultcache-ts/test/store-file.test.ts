import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdtemp, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";
import { promisify } from "node:util";

import { decode, encode } from "@msgpack/msgpack";

import { documentPayloadFromBytes } from "../src/document";
import { SingleFileMessagePackBackingStore } from "../src/single-file-messagepack-backing-store";
import type { CultCacheEnvelope } from "../src/types";

import {
  MOVEFILE_REPLACE_WRITE_THROUGH,
  StoreReplacedNotDurableError,
  commitStoreFile,
  nodeStoreFileOps,
  replaceStoreFile,
  replaceStoreFileWindows,
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
    assert.equal(log.length, 3);
    assert.deepEqual(sleeps, [20, 20]);
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

const entryOf = (key: string): CultCacheEnvelope => ({
  key,
  type: "t",
  payload: documentPayloadFromBytes(Uint8Array.of(0x90)),
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

const legacyObjectPayload = (): Uint8Array =>
  encode([{ key: "note:hello", type: "note", payload: { title: "Hello" }, storedAt: "2026-10-20T00:00:00Z" }]);

test("an open that heals a legacy store waits for the store lock and heals from its own read", async () => {
  const directory = await scratch();
  try {
    const path = join(directory, "legacy.cc");
    const legacy = legacyObjectPayload();
    await writeFile(path, legacy);
    const store = new SingleFileMessagePackBackingStore(path);
    let healed: Promise<unknown> | undefined;
    let settled = false;
    await withStoreWriteLock(path, async () => {
      healed = store.pullAll().then(() => {
        settled = true;
      });
      await sleepFor(300);
      assert.equal(settled, false, "the heal must wait for the lock");
      assert.deepEqual([...(await readFile(path))], [...legacy], "the file keeps its legacy bytes while the lock is held elsewhere");
    });
    await within(healed!, 5000, "the heal");
    assert.equal((decode(await readFile(path)) as unknown[])[0], "cultcache.store.v1");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("push and delete over a legacy store that needs healing finish instead of waiting on their own lock", async () => {
  const directory = await scratch();
  try {
    const path = join(directory, "legacy.cc");
    await writeFile(path, legacyObjectPayload());
    const store = new SingleFileMessagePackBackingStore(path);
    await within(store.push(entryOf("a")), 5000, "push");
    await writeFile(path, legacyObjectPayload());
    await within(store.delete(entryOf("a")), 5000, "delete");
    const keys = (await store.pullAll()).map((entry) => entry.key);
    assert.deepEqual(keys, ["note:hello"]);
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
