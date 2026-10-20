import assert from "node:assert/strict";
import { mkdtemp, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

import {
  MOVEFILE_REPLACE_WRITE_THROUGH,
  StoreReplacedNotDurableError,
  commitStoreFile,
  nodeStoreFileOps,
  replaceStoreFile,
  replaceStoreFileWindows,
} from "../src/store-file";
import type { ReplaceKernel, StoreFileOps } from "../src/store-file";

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
