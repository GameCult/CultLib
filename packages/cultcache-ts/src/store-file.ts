// The TypeScript owner of a single-file store's write transaction: the cross-process lock on
// `<path>.lock`, the staged file, its fsync, the replace and the directory fsync.
// Node only, and not exported from the package index. The OS calls Node does not expose
// (flock, LockFileEx, a POSIX-semantics rename on Windows) go through koffi, which is required
// lazily inside `loadKernel`, so a consumer that never writes a store never loads it.

import { closeSync, openSync, constants as fsConstants } from "node:fs";
import { mkdir, open, rename, rm } from "node:fs/promises";
import type { FileHandle } from "node:fs/promises";
import { dirname } from "node:path";

const LOCK_POLL_MS = 10;
const LOCK_TIMEOUT_MS = 30_000;

/** Thrown when the staged file replaced the store but the directory entry may not be durable. */
export class StoreReplacedNotDurableError extends Error {
  readonly replaced = true;
  constructor(storePath: string, cause: unknown) {
    super(
      `CultCache store ${storePath} was replaced, but syncing its directory failed, so the replace may not survive a crash: ${
        cause instanceof Error ? cause.message : String(cause)
      }`,
    );
    this.name = "StoreReplacedNotDurableError";
  }
}

/** The file operations of one commit. Tests record or fail them; production uses `nodeStoreFileOps`. */
export interface StoreFileOps {
  createExclusive(path: string): Promise<FileHandle>;
  write(handle: FileHandle, bytes: Uint8Array): Promise<void>;
  sync(handle: FileHandle): Promise<void>;
  close(handle: FileHandle): Promise<void>;
  replace(staged: string, destination: string): Promise<void>;
  syncDirectory(directory: string): Promise<void>;
  remove(path: string): Promise<void>;
}

export const nodeStoreFileOps: StoreFileOps = {
  createExclusive: (path) => open(path, "wx"),
  write: async (handle, bytes) => {
    await handle.writeFile(bytes);
  },
  sync: (handle) => handle.sync(),
  close: (handle) => handle.close(),
  replace: (staged, destination) => replaceStoreFile(staged, destination),
  syncDirectory: async (directory) => {
    const handle = await open(directory, "r");
    try {
      await handle.sync();
    } finally {
      await handle.close();
    }
  },
  remove: (path) => rm(path, { force: true }),
};

/**
 * Writes `bytes` to a unique staged file beside the store, fsyncs it, replaces the store with it
 * and, on Unix, fsyncs the directory so the replace is durable. Windows relies on the replace
 * itself, as the Rust runtime does. Any failure before the replace removes the staged file and
 * leaves the store untouched; a directory sync failure after it throws
 * `StoreReplacedNotDurableError`. The caller holds the store lock.
 */
export async function commitStoreFile(
  storePath: string,
  bytes: Uint8Array,
  ops: StoreFileOps = nodeStoreFileOps,
): Promise<void> {
  const staged = `${storePath}.tmp-${process.pid}-${Date.now()}-${Math.random().toString(36).slice(2)}`;
  let handle: FileHandle | undefined;
  try {
    handle = await ops.createExclusive(staged);
    await ops.write(handle, bytes);
    await ops.sync(handle);
    const closing = handle;
    handle = undefined;
    await ops.close(closing);
    await ops.replace(staged, storePath);
  } catch (error) {
    if (handle) {
      await ops.close(handle).catch(() => undefined);
    }
    await ops.remove(staged).catch(() => undefined);
    throw error;
  }

  if (process.platform !== "win32") {
    try {
      await ops.syncDirectory(dirname(storePath));
    } catch (error) {
      throw new StoreReplacedNotDurableError(storePath, error);
    }
  }
}

/** Replaces `destination` with `staged`: POSIX-first on Windows, `rename` elsewhere. */
export async function replaceStoreFile(staged: string, destination: string): Promise<void> {
  if (process.platform === "win32") {
    await replaceStoreFileWindows(staged, destination, windowsKernel());
    return;
  }

  await rename(staged, destination);
}

const ERROR_ACCESS_DENIED = 5;
const ERROR_SHARING_VIOLATION = 32;
const ERROR_LOCK_VIOLATION = 33;
const ERROR_NOT_SUPPORTED = 50;
const ERROR_INVALID_PARAMETER = 87;
const REPLACE_ATTEMPTS = 25;
const REPLACE_RETRY_MS = 20;
/** MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH. */
export const MOVEFILE_REPLACE_WRITE_THROUGH = 0x9;

/** The kernel32 calls a Windows replace needs, each answering 0 or a Win32 error code. */
export interface ReplaceKernel {
  /** CreateFileW(staged, DELETE|SYNCHRONIZE) then FileRenameInfoEx with REPLACE_IF_EXISTS|POSIX_SEMANTICS, then CloseHandle. */
  renamePosix(staged: string, destination: string): number;
  /** MoveFileExW(staged, destination, flags). */
  moveFileEx(staged: string, destination: string, flags: number): number;
  sleep(milliseconds: number): Promise<void>;
}

export async function replaceStoreFileWindows(
  staged: string,
  destination: string,
  kernel: ReplaceKernel,
): Promise<void> {
  let posix = true;
  let lastCode = 0;
  for (let attempt = 1; attempt <= REPLACE_ATTEMPTS; attempt++) {
    let code = posix ? kernel.renamePosix(staged, destination) : kernel.moveFileEx(staged, destination, MOVEFILE_REPLACE_WRITE_THROUGH);
    if (
      posix &&
      code !== 0 &&
      (code === 1 || code === ERROR_NOT_SUPPORTED || code === ERROR_INVALID_PARAMETER)
    ) {
      // POSIX semantics are unsupported here (FAT, exFAT, before Windows 10 1607): the rest of this call is write-through.
      posix = false;
      code = kernel.moveFileEx(staged, destination, MOVEFILE_REPLACE_WRITE_THROUGH);
    }

    if (code === 0) {
      return;
    }

    if (code !== ERROR_ACCESS_DENIED && code !== ERROR_SHARING_VIOLATION && code !== ERROR_LOCK_VIOLATION) {
      throw new Error(`CultCache could not replace the store: Win32 error ${code}.`);
    }

    lastCode = code;
    await kernel.sleep(REPLACE_RETRY_MS);
  }

  throw new Error(
    `CultCache could not replace the store after ${REPLACE_ATTEMPTS} attempts: last Win32 error ${lastCode}.`,
  );
}

// ---------------------------------------------------------------------------------------------
// The cross-process write lock.

export interface StoreLockOptions {
  /** How long to wait for the lock; 30 s by default, as the C# runtime waits. */
  timeoutMs?: number;
}

/**
 * Runs `operation` while holding the exclusive store lock on `<storePath>.lock`. C# holds it
 * by a share-none open, Rust by an exclusive file lock; this takes the same lock by LockFileEx
 * on Windows and flock on Unix, so all of them exclude each other. Polls every 10 ms and holds
 * no handle on the lock file between polls. The lock file is created if absent, never
 * written and never deleted.
 */
export async function withStoreWriteLock<T>(
  storePath: string,
  operation: () => Promise<T>,
  options: StoreLockOptions = {},
): Promise<T> {
  const lockPath = `${storePath}.lock`;
  const timeoutMs = options.timeoutMs ?? LOCK_TIMEOUT_MS;
  await mkdir(dirname(lockPath), { recursive: true });

  const started = Date.now();
  let release: (() => void) | undefined;
  while (release === undefined) {
    release = tryLock(lockPath);
    if (release === undefined) {
      if (Date.now() - started >= timeoutMs) {
        throw new Error(`CultCache timed out after ${timeoutMs} ms waiting for the store lock ${lockPath}.`);
      }

      await sleep(LOCK_POLL_MS);
    }
  }

  try {
    return await operation();
  } finally {
    release();
  }
}

function tryLock(lockPath: string): (() => void) | undefined {
  const platform = process.platform;
  if (platform === "win32") {
    return tryLockWindows(lockPath);
  }

  if (platform === "linux" || platform === "darwin") {
    return tryLockUnix(lockPath, platform);
  }

  throw new Error(`CultCache cross-process store lock is not implemented on ${platform}.`);
}

function sleep(milliseconds: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

// ---------------------------------------------------------------------------------------------
// koffi bindings, loaded on first use.

const GENERIC_READ_WRITE = 0xc0000000;
const GENERIC_ALL_DELETE_SYNC = 0x00110000; // DELETE | SYNCHRONIZE
const SHARE_READ_WRITE_DELETE = 0x7;
const OPEN_EXISTING = 3;
const OPEN_ALWAYS = 4;
const FILE_ATTRIBUTE_NORMAL = 0x80;
const LOCKFILE_EXCLUSIVE_FAIL_IMMEDIATELY = 0x3;
const FILE_RENAME_INFO_EX = 22;
const RENAME_REPLACE_POSIX = 0x3;
const INVALID_HANDLE_VALUE = -1;
const LOCK_EX_NB = 6;
const LOCK_UN = 8;
const EINTR = 4;

type Kernel32 = {
  createFile(path: string, access: number, share: number, disposition: number): number;
  lockFileEx(handle: number): boolean;
  unlockFileEx(handle: number): void;
  closeHandle(handle: number): void;
  setRenameInfo(handle: number, buffer: Buffer): boolean;
  moveFileEx(staged: string, destination: string, flags: number): boolean;
  lastError(): number;
};

type Libc = {
  flock(fd: number, operation: number): number;
  errno(): number;
};

let kernel32Cache: Kernel32 | undefined;
let libcCache: Libc | undefined;

function loadKernel32(): Kernel32 {
  if (kernel32Cache) {
    return kernel32Cache;
  }

  // eslint-disable-next-line @typescript-eslint/no-var-requires
  const koffi = require("koffi");
  const lib = koffi.load("kernel32.dll");
  const createFileW = lib.func(
    "intptr_t CreateFileW(str16 name, uint32_t access, uint32_t share, void *security, uint32_t disposition, uint32_t flags, void *template)",
  );
  const lockFileEx = lib.func(
    "int LockFileEx(intptr_t handle, uint32_t flags, uint32_t reserved, uint32_t low, uint32_t high, void *overlapped)",
  );
  const unlockFileEx = lib.func(
    "int UnlockFileEx(intptr_t handle, uint32_t reserved, uint32_t low, uint32_t high, void *overlapped)",
  );
  const closeHandle = lib.func("int CloseHandle(intptr_t handle)");
  const setFileInformationByHandle = lib.func(
    "int SetFileInformationByHandle(intptr_t handle, int infoClass, void *info, uint32_t size)",
  );
  const moveFileExW = lib.func("int MoveFileExW(str16 existing, str16 destination, uint32_t flags)");
  const getLastError = lib.func("uint32_t GetLastError()");

  kernel32Cache = {
    createFile: (path, access, share, disposition) =>
      Number(createFileW(path, access, share, null, disposition, FILE_ATTRIBUTE_NORMAL, null)),
    lockFileEx: (handle) => lockFileEx(handle, LOCKFILE_EXCLUSIVE_FAIL_IMMEDIATELY, 0, 0xffffffff, 0xffffffff, Buffer.alloc(32)) !== 0,
    unlockFileEx: (handle) => {
      unlockFileEx(handle, 0, 0xffffffff, 0xffffffff, Buffer.alloc(32));
    },
    closeHandle: (handle) => {
      closeHandle(handle);
    },
    setRenameInfo: (handle, buffer) => setFileInformationByHandle(handle, FILE_RENAME_INFO_EX, buffer, buffer.length) !== 0,
    moveFileEx: (staged, destination, flags) => moveFileExW(staged, destination, flags) !== 0,
    lastError: () => Number(getLastError()),
  };
  return kernel32Cache;
}

function loadLibc(platform: "linux" | "darwin"): Libc {
  if (libcCache) {
    return libcCache;
  }

  // eslint-disable-next-line @typescript-eslint/no-var-requires
  const koffi = require("koffi");
  const lib = koffi.load(platform === "darwin" ? "libSystem.B.dylib" : "libc.so.6");
  const flock = lib.func("int flock(int fd, int operation)");
  libcCache = {
    flock: (fd, operation) => flock(fd, operation),
    errno: () => koffi.errno(),
  };
  return libcCache;
}

function windowsKernel(): ReplaceKernel {
  const k = loadKernel32();
  return {
    renamePosix: (staged, destination) => {
      const handle = k.createFile(staged, GENERIC_ALL_DELETE_SYNC, SHARE_READ_WRITE_DELETE, OPEN_EXISTING);
      if (handle === INVALID_HANDLE_VALUE) {
        return k.lastError();
      }

      try {
        const name = Buffer.from(destination, "utf16le");
        const info = Buffer.alloc(20 + name.length + 2);
        info.writeUInt32LE(RENAME_REPLACE_POSIX, 0);
        info.writeUInt32LE(name.length, 16);
        name.copy(info, 20);
        return k.setRenameInfo(handle, info) ? 0 : k.lastError();
      } finally {
        k.closeHandle(handle);
      }
    },
    moveFileEx: (staged, destination, flags) => (k.moveFileEx(staged, destination, flags) ? 0 : k.lastError()),
    sleep,
  };
}

function tryLockWindows(lockPath: string): (() => void) | undefined {
  const k = loadKernel32();
  const handle = k.createFile(lockPath, GENERIC_READ_WRITE, SHARE_READ_WRITE_DELETE, OPEN_ALWAYS);
  if (handle === INVALID_HANDLE_VALUE) {
    const code = k.lastError();
    if (code === ERROR_SHARING_VIOLATION) {
      // A C# holder opened the lock file share-none.
      return undefined;
    }

    throw new Error(`CultCache could not open the store lock ${lockPath}: Win32 error ${code}.`);
  }

  if (!k.lockFileEx(handle)) {
    const code = k.lastError();
    k.closeHandle(handle);
    if (code === ERROR_LOCK_VIOLATION) {
      // A Rust or TypeScript holder holds the exclusive byte-range lock.
      return undefined;
    }

    throw new Error(`CultCache could not lock the store lock ${lockPath}: Win32 error ${code}.`);
  }

  return () => {
    k.unlockFileEx(handle);
    k.closeHandle(handle);
  };
}

function tryLockUnix(lockPath: string, platform: "linux" | "darwin"): (() => void) | undefined {
  const libc = loadLibc(platform);
  const wouldBlock = platform === "darwin" ? 35 : 11; // EWOULDBLOCK == EAGAIN
  const fd = openSync(lockPath, fsConstants.O_RDWR | fsConstants.O_CREAT, 0o666);
  for (;;) {
    if (libc.flock(fd, LOCK_EX_NB) === 0) {
      return () => {
        libc.flock(fd, LOCK_UN);
        closeSync(fd);
      };
    }

    const errno = libc.errno();
    if (errno === EINTR) {
      continue;
    }

    closeSync(fd);
    if (errno === wouldBlock) {
      return undefined;
    }

    throw new Error(`CultCache could not lock the store lock ${lockPath}: errno ${errno}.`);
  }
}
