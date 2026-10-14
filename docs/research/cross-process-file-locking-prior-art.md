# Cross-process file locking and atomic replace: prior art

Facts only. Gathered 2026-10-02 for fork `variants:question:ts-python-write-lock`. No conclusions, no recommendation.

Provenance legend: **[src]** read from CultLib source this pass; **[primary]** vendor/spec/upstream doc or source fetched this pass (the fetch tool summarises pages through a small model, so quoted phrasing is paraphrase-grade unless shown in quotes); **[secondary]** search-result snippet or third-party issue, weak; **[UNVERIFIED]** not confirmed by any source read, or derived by inference only. Nothing here was tested by running code on Windows or Linux.

Dates caveat: the doc-site fetches reported release dates for `fs2 0.4.3` ("September 9, 2026") and `fs4 1.1.0` ("September 23, 2026"). The fs2 date is implausible against my memory of that crate (long stable, last release years earlier); treat crate dates as **[UNVERIFIED]**.

---

## 0. What CultLib does today (source, this checkout)

| Runtime | Lock | Temp name | Replace | Pointer |
|---|---|---|---|---|
| C# | Sidecar `<file>.lock`, `new FileStream(lockPath, OpenOrCreate, ReadWrite, FileShare.None, bufferSize 1)`. On `IOException`: if `wait` is false return null, else sleep 10 ms and retry for up to 30 s, after which the exception propagates. | `.{name}.{Guid:N}.tmp`, `FileMode.CreateNew`, `FileShare.None`, `WriteThrough`, `Flush(true)` | `File.Replace(temp, path, null)` if target exists, else `File.Move` | `F:\Projects\CultLib\src\GameCult.Caching\CultCache.cs` lines ~3475-3530 |
| Rust | Sidecar `<file>.lock` (`.lock` appended to full file name), opened `create/read/write/truncate(false)` (std default share mode), then `fs2::FileExt::lock_exclusive` / `lock_shared` / `try_lock_exclusive` (WouldBlock => `None`). Unlock explicit, then drop. | `{name}.{uuid v4}.tmp`; staged file `sync_all` then closed | Unix: `fs::rename`. Windows: `MoveFileExW(MOVEFILE_REPLACE_EXISTING \| MOVEFILE_WRITE_THROUGH)` retried 25 times, 20 ms apart, on ERROR_ACCESS_DENIED(5)/SHARING_VIOLATION(32)/LOCK_VIOLATION(33). Comment cites a Muninn crash on 2026-09-11. | `F:\Projects\CultLib\packages\cultcache-rs\src\lib.rs` ~925-945, 1040-1084, 2870-3030; `Cargo.toml`: `fs2 = "0.4"`, edition 2024 |
| TypeScript | None. In-process promise queue only (`next`/`result` chain). | `${file}.tmp-${pid}-${Date.now()}-${random}` (unique per writer) | `fs/promises.rename`, retried 10 times with 10 ms doubling on EPERM/EBUSY/EACCES; temp removed on error. No fsync of temp. | `F:\Projects\CultLib\packages\cultcache-ts\src\single-file-messagepack-backing-store.ts` ~143-205 |
| Python | None. `threading.RLock` per store object (in-process only). | Fixed `path.with_suffix(suffix + ".tmp")`, i.e. `store.cc.tmp`, shared by all writers | `Path.replace` (`os.replace`); no fsync | `F:\Projects\CultLib\packages\cultcache-py\src\cultcache_py\stores.py` lines 70-82, 121-126 |

Both C# and Rust name the lock `<store path>.lock`. The same-name sidecar is the only thing the two currently share. Both also hold the lock around read-modify-write of the snapshot **[src]**. A lock file is never deleted by either (no stale-file problem; the lock is the kernel object, not the file's existence) **[src, inferred from absence of delete]**.

Note re the fork text: C# does use a lock (stated in the C# code comment: "excludes other handles in this process and in others alike").

---

## 1. Language and OS primitives

### 1.1 .NET `FileStream` / `FileShare`

- **Windows**: `FileShare` maps to the `dwShareMode` of `CreateFile`. `FileShare.None` = share mode 0: "Any request to open the file (by this process or another process) will fail until the file is closed." **[primary]** https://learn.microsoft.com/en-us/dotnet/api/system.io.fileshare . Win32 side: dwShareMode 0 "cannot be opened again, either by the application that opened it or by another application, until its handle has been closed"; failure is ERROR_SHARING_VIOLATION. **[primary]** https://learn.microsoft.com/en-us/windows/win32/fileio/creating-and-opening-files . Share modes are checked at **open** time and are not byte-range locks; they do not call `LockFileEx` **[inferred]**.
- **Unix**: .NET emulates `FileShare` with advisory `flock(2)`. Source (`SafeFileHandle.Unix.cs`, runtime main): `lockOperation = (share == FileShare.None) ? LOCK_EX : LOCK_SH`, called as `FLock(handle, lockOperation | LOCK_NB)`; on `EWOULDBLOCK` an `IOException` is thrown; handle close issues `LOCK_UN`. **[primary]** https://raw.githubusercontent.com/dotnet/runtime/main/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs
  - So non-`None` opens take a **shared** flock; `None` takes an **exclusive** flock; non-blocking in all cases. A plain read with `FileShare.ReadWrite | Delete` still takes `LOCK_SH`, which would conflict with a writer holding `LOCK_EX` on the same file (only relevant if the same file is opened both ways). **[inferred from source above]**
  - Disable switch: env `DOTNET_SYSTEM_IO_DISABLEFILELOCKING`; locking is off by default on Browser/WASI and iOS/tvOS (not Mac Catalyst). **[primary]**
  - Because it is advisory flock, it excludes only other openers that also take flock (other .NET processes, or any program calling flock on that file). It does not stop a process that merely `open()`s and writes. **[inferred]**
  - Since which .NET version: **[UNVERIFIED]**. I believe the flock-based FileShare emulation has existed since .NET Core 1.x/2.x, and .NET 6 rewrote FileStream into strategies while keeping it, but no source read this pass dates it.
  - Windows `FileShare.Delete` is meaningful; the Unix code does not differentiate it. **[primary]**
- `File.Replace(src, dst, backup)` -> Win32 `ReplaceFile`; throws if volumes differ. **[primary]** https://learn.microsoft.com/en-us/dotnet/api/system.io.file.replace . Unix mapping to `rename` **[UNVERIFIED]**.

### 1.2 Rust

- `std::fs::File::lock`, `lock_shared`, `try_lock`, `try_lock_shared`, `unlock`: stable since **Rust 1.89.0**. **[primary]** https://doc.rust-lang.org/std/fs/struct.File.html
  - Unix/WASI: `flock` (`LOCK_EX`, `LOCK_SH`, `+LOCK_NB`, `LOCK_UN`). Windows: `LockFileEx` (flags `LOCKFILE_EXCLUSIVE_LOCK`, `LOCKFILE_FAIL_IMMEDIATELY`) and `UnlockFile`.
  - Docs: lock "may be advisory or mandatory", platform-dependent; on Windows the file must be opened for write (or read+append) for an exclusive lock; re-locking the same handle or a clone is unspecified, possibly deadlock; locks released when the file and all duplicated handles close; "this may change in the future".
- `fs2` 0.4.x (what CultCache Rust uses): Unix `flock(2)`; Windows `LockFileEx` (source `src/windows.rs`: `lock_shared` passes 0, `lock_exclusive` passes `LOCKFILE_EXCLUSIVE_LOCK`, try variants add `LOCKFILE_FAIL_IMMEDIATELY`; `unlock` uses `UnlockFile`). Docs: "advisory"; do not lock one `File` concurrently; duplicated files need great care. **[primary]** https://docs.rs/fs2/latest/fs2/trait.FileExt.html , https://raw.githubusercontent.com/danburkert/fs2-rs/master/src/windows.rs . An earlier summary of the fs2 docs page said "LockFile" for Windows; the source file says `LockFileEx`. Maintenance status of fs2: **[UNVERIFIED]**.
- `fs4`: fork of fs2 adding async, rustix instead of libc; docs say `flock`/`fcntl` on Unix, `LockFileEx` on Windows. Whether std's `File::lock` is mentioned or deprecates it: not stated on the page read. **[primary]** https://docs.rs/fs4/latest/fs4/ . Which Unix call it picks by default (flock vs fcntl): **[UNVERIFIED]**.
- Windows default share mode of Rust `OpenOptions`: `FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE`; `share_mode(0)` makes exclusive. **[primary]** https://doc.rust-lang.org/std/os/windows/fs/trait.OpenOptionsExt.html
- `std::fs::rename`: Unix `rename(2)`; Windows 10 1607+ `MoveFileExW(MOVEFILE_REPLACE_EXISTING)` or `SetFileInformationByHandle(FileRenameInfoEx)` (POSIX semantics); earlier, MoveFileExW only. "may change in future". **[primary]** https://doc.rust-lang.org/std/fs/fn.rename.html . (CultCache Rust bypasses std rename on Windows and calls MoveFileExW itself, per 0.)

### 1.3 Node

- No built-in advisory file lock in `node:fs`; **[UNVERIFIED]** by a direct docs read this pass, but nothing in the packages read contradicts it. Options seen:
  - `proper-lockfile`: "mkdir strategy" - creates `<target>.lock` as a **directory**; works across local and network FS; stale detection by lock dir **mtime** (default `stale` 10 s, min 5 s; mtime refreshed every `update` = stale/2, min 1 s); `onCompromised` callback when refresh fails or is late; avoids `O_EXCL` because "O_EXCL is broken on NFS" (their README); contrasts with `lockfile` package which uses ctime for staleness, unsuitable for long-running holders. **[primary]** https://raw.githubusercontent.com/moxystudio/node-proper-lockfile/master/README.md
  - `lockfile` (npm), `fs-ext` (native `flock`/`fcntl` bindings): not fetched. **[UNVERIFIED]**
- Libuv rename on Windows uses `MoveFileExW(MOVEFILE_REPLACE_EXISTING)`; reports say it fails with EPERM while any handle holds the destination open, and refuses a READONLY destination; some runtimes (Bun PR) fall back to POSIX-semantic replace. **[secondary]** search snippets: https://github.com/oven-sh/bun/pull/43997 , https://github.com/joyent/libuv/issues/283 . Not read from libuv source: **[UNVERIFIED]**.
- `npm/write-file-atomic`: temp name = `filename + "." + murmurhex(__filename, process.pid, ++invocations)` (plus worker thread id); same-process writes to one file queued and serialised; fsync on by default; no cross-process lock mentioned. **[primary]** https://raw.githubusercontent.com/npm/write-file-atomic/main/README.md

### 1.4 Python

- `fcntl.flock()` wraps `flock(2)`; docs: "On some systems, this function is emulated using fcntl()". `fcntl.lockf()` is "essentially a wrapper around the fcntl() locking calls". Unix only. "On at least some systems, LOCK_EX can only be used if the file descriptor refers to a file opened for writing." The page does not state whether flock and lockf interact. **[primary]** https://docs.python.org/3/library/fcntl.html
- `msvcrt.locking(fd, mode, nbytes)`: locks `nbytes` from the current file position (may extend past EOF); `LK_LOCK`/`LK_RLCK` retry once per second, 10 attempts, then `OSError`; `LK_NBLCK` fails immediately; regions cannot overlap; adjacent regions not merged. Underlying Win32 call not stated in docs. **[primary]** https://docs.python.org/3/library/msvcrt.html . (The CRT `_locking` is documented elsewhere as `LockFile`-based; **[UNVERIFIED]** here.)
- `filelock` (tox-dev): `FileLock` alias = `UnixFileLock` (`fcntl.flock`, described as kernel-enforced and released on crash) on Unix; `WindowsFileLock` described as `LockFileEx` byte-range locking on Windows (older versions used `msvcrt.locking`; version not checked, **[UNVERIFIED]**); `SoftFileLock` is a "cooperative file-existence marker", with same-host PID inspection and reclaim from dead processes; docs recommend verifying shared-filesystem semantics before deploying soft locks; no statement on cross-type exclusion. **[primary]** https://py-filelock.readthedocs.io/en/latest/index.html
- `portalocker`: README fetch 404; its primitives are **[UNVERIFIED]** (not read).
- `os.replace` on Windows semantics (MoveFileEx REPLACE_EXISTING): **[UNVERIFIED]**, not fetched.

### 1.5 Interoperability matrix (flock / fcntl / LockFileEx / share modes)

| Question | Finding | Source |
|---|---|---|
| Linux, local FS: does `flock` exclude `fcntl` (POSIX byte-range) locks? | No. man flock(2): on Linux "there is no interaction between the types of lock placed by flock() and fcntl(2)" (local filesystems). | **[primary]** https://man7.org/linux/man-pages/man2/flock.2.html |
| Linux over NFS (since 2.6.12) | flock is emulated as whole-file fcntl byte-range locks, so flock and fcntl **do** interact there. | same |
| Linux over CIFS (since 5.5) | flock emulated with SMB byte-range locks; flock and fcntl interact; and the lock becomes **mandatory**: I/O from other descriptors on a locked file fails with EACCES. | same |
| Duplicate fds / fork | A lock belongs to the open file description; shared by dup/fork; persists until `LOCK_UN` or **all** duplicates close. Independent `open()`s in the same process conflict with each other under flock. | same |
| Does a Rust `flock` (std or fs2) exclude a .NET-on-Unix `FileShare.None` open? | Both are `flock(LOCK_EX)` on the same inode; by the sources above they should conflict (.NET's non-blocking attempt gets EWOULDBLOCK -> IOException -> CultCache C# retries). **Not tested.** | **[inferred]**; **[UNVERIFIED]** empirically |
| Same, but .NET `FileShare.ReadWrite` opener (shared flock) vs Rust `lock_exclusive` | Conflicts (LOCK_SH vs LOCK_EX), so a reader opening the lock file with any FileShare would block/fail against a Rust holder. CultCache does not open the lock file for reading. | **[inferred]** |
| Windows: does a share-mode-0 open (.NET `FileShare.None`) exclude a Rust `LockFileEx` locker? | Order A: .NET holds the file with share mode 0. Rust's `OpenOptions::open` (share mode default RWD) then fails at `CreateFile` with ERROR_SHARING_VIOLATION, before it can lock. In `open_lock_file` that is a `with_context` error, **not a wait/retry** (looked at the source). Order B: Rust holds a handle (RWD) and `LockFileEx`; .NET `CreateFile` with share mode 0 conflicts with the existing open handle's access -> ERROR_SHARING_VIOLATION -> `IOException` -> C# retries up to 30 s. So mutual exclusion holds in both orders, but in order A Rust errors instead of waiting. | **[inferred]** from MS CreateFile share-mode table + Rust OpenOptionsExt default; **[UNVERIFIED]** empirically |
| Windows: is `LockFileEx` mandatory? | Exclusive region lock "denies all other processes both read and write access to the specified region"; shared lock denies writes; locks are not honoured through mapped views. If a process dies or closes the handle the OS releases the locks, but "the time it takes ... depends upon available system resources" (MS recommends explicit unlock). Handle needs GENERIC_READ or GENERIC_WRITE. | **[primary]** https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-lockfileex |
| Windows: second handle in same process | "If the locking process opens the file a second time, it cannot access the specified region through this second handle until it unlocks the region." | same |
| Rust-on-Windows `LockFileEx` vs Python `msvcrt.locking` (same region?) | `LockFileEx` regions conflict if they overlap; fs2 locks which byte range (offset 0, len u32::MAX..?) : **[UNVERIFIED]**; msvcrt.locking locks `nbytes` from the current position (so depends on `lseek`); whether CRT `_locking` uses LockFile/LockFileEx and which range would need to match: **[UNVERIFIED]**. |
| Python Unix `flock` vs Rust/.NET flock | Same kernel primitive on Linux/macOS (Python's flock "may be emulated using fcntl on some systems", so platform-dependent). Python `lockf` is fcntl: independent from flock on local Linux FS. | **[primary]** python docs + man page |
| macOS flock vs fcntl | **[UNVERIFIED]** (not fetched). |
| POSIX fcntl lock semantics | Per-process: `close()` of **any** fd for the file by the process drops all of that process's fcntl locks (SQLite doc 2.2 below). flock locks are per open-file-description and do not have that hazard. | **[primary]** SQLite howtocorrupt; flock(2) |
| Python `fcntl.flock` on Windows | Module is Unix only (docs). | **[primary]** |

---

## 2. Lockfile protocols in established software

### git
- Lockfile API: lock for `foo` is `foo.lock`, created with `O_CREAT|O_EXCL`; writer writes new content into it; `commit_lock_file()` renames it over `foo`; `rollback_lock_file()` removes it; writers exclude each other; readers never block and see either old or new content (relies on atomic rename); cleanup of outstanding lockfiles is registered with `atexit` and signal handlers. **[primary]** https://git-scm.com/docs/api-lockfile
- `lockfile.c`: retry with quadratic backoff for at least `timeout_ms`; PID file registered as a tempfile for signal/atexit cleanup; failure message says another git process may be running "or the lock file may be stale" and tells the user to remove it manually. **[primary]** https://raw.githubusercontent.com/git/git/master/lockfile.c
- Consequences recorded in the docs: a hard kill (SIGKILL, power loss) leaves a `.lock` file and later writers fail until it is removed; git does not time-based-reclaim index.lock **[inferred from message text, not stated; UNVERIFIED]**. Note the git design makes the lock file **the temp file** (lock name = staging name); CultCache's current design has a separate sidecar lock plus uniquely named temp.

### SQLite (see also section 4)
- Unix default VFS: POSIX advisory locks via `fcntl()`; Windows VFS: `LockFile`/`LockFileEx`/`UnlockFile`. Five states UNLOCKED/SHARED/RESERVED/PENDING/EXCLUSIVE. **[primary]** https://www.sqlite.org/lockingv3.html (byte-range layout not stated on that page).
- Built-in unix VFS variants: `unix` (POSIX advisory), `unix-dotfile` (dot-file locking), `unix-excl` (holds exclusive lock, WAL index in heap), `unix-none` (no locking), `unix-namedsem` (VxWorks). "Except for 'unix' and 'unix-excl', the various Unix VFSes all use incompatible locking implementations"; processes using different VFSes on one DB "may not see each others locks ... resulting in database corruption". **[primary]** https://www.sqlite.org/vfs.html . (`unix-flock` / `unix-afp` / `unix-nfs` appear in SQLite's source as separate io-method variants; not listed on that page: **[UNVERIFIED]**.)

### LMDB
- Uses a separate `lock.mdb` file for the reader table and writer mutex. Header warnings: stale reader slots from aborted programs make the DB grow quickly (`mdb_reader_check`); stale writer lock clears automatically on Windows, BSD with SysV semaphores, and Linux with robust POSIX mutexes, otherwise all DB users must be closed; "Do not have open an LMDB database twice in the same process at the same time. Not even from a plain open() call - close()ing it breaks flock() advisory locking."; "Do not use LMDB databases on remote filesystems, even between processes on the same host. This breaks flock() on some OSes"; `MDB_NOLOCK` disables locking entirely. **[primary]** https://raw.githubusercontent.com/LMDB/lmdb/mdb.master/libraries/liblmdb/lmdb.h
- Note: that warning is about `flock` and `close()`, which contradicts the usual statement that only fcntl locks have the close() hazard; the source of the discrepancy (older Linux/BSD semantics, or the emulated flock on some OS) is **[UNVERIFIED]**.

### Cargo / npm / pip / Gradle
- Cargo: package-cache and build-dir lock using an OS file lock, prints "Blocking waiting for file lock"; source fetch (`src/cargo/util/flock.rs`) 404; details (flock/fs4/std, handling of filesystems without locking) **[UNVERIFIED]**.
- Gradle: `DefaultFileLockManager` holds cross-process locks on cache/daemon-registry files; reports of builds hanging on locks left by crashed or killed daemons. **[secondary]** web search snippets only (https://discuss.gradle.org/t/gradle-daemon-keeps-locking-intermediate-files-preventing-builds/38004 , https://www.javathinking.com/blog/gradle-build-is-hanging-without-failure-defaultfilelockmanager-acquiring-and-releasing-lock-on-daemon-addresses-registry/ ); Gradle's own docs on lock protocol not read. **[UNVERIFIED]**
- npm: `write-file-atomic` has no cross-process lock (above); npm's cache (cacache) locking: **[UNVERIFIED]**, not fetched.
- pip: **[UNVERIFIED]**, not fetched.

### Windows rename retry (Postgres)
- `pgrename()` on Windows calls `MoveFileEx(from, to, MOVEFILE_REPLACE_EXISTING)` and retries on ERROR_ACCESS_DENIED, ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION: up to 100 retries, 0.1 s apart (~10 s). Comment: other applications may have the file open without sharing flags; LOCK_VIOLATION seen from some anti-virus software; bounded because the caller may hold locks affecting other backends. **[primary]** https://raw.githubusercontent.com/postgres/postgres/master/src/port/dirmod.c . CultCache Rust uses the same error set with 25 x 20 ms (0.5 s); TS retries EPERM/EBUSY/EACCES 10 times with doubling from 10 ms (about 10 s worst case) **[src]**.

---

## 3. Temp-file naming and rename atomicity

- Unique temp names per writer: C# GUID, Rust uuid v4, TS pid+time+random **[src]**; npm write-file-atomic pid + counter hash **[primary]**; Python CultCache fixed `.tmp` **[src]**. Rust additionally has a startup cleanup that deletes only temps matching `<store>.<uuid>.tmp` (tests at `lib.rs` ~3420, 4667-4689 distinguish `not-a-uuid.tmp`, bare `.tmp`, neighbour stores) **[src]**. Note the naming differs between runtimes: C# `.{name}.{guid:N}.tmp` (leading dot, no dashes) vs Rust `{name}.{uuid-with-dashes}.tmp` vs TS `{name}.tmp-...`; whether the Rust stale-temp sweeper recognises C#/TS temps: it matches the Rust format only (per the test names) **[src, partial reading]**.
- Fixed temp name consequence (inferred, not tested): two processes writing `store.cc.tmp` concurrently share one file: interleaved `write_bytes` (truncate-and-write) can yield a mixed or truncated file that one process then renames into place, and the other process's `replace` can fail with source-not-found. **[inferred]**
- POSIX `rename()`: if `new` exists it "shall be removed and old renamed to new"; the rationale requires the action to be atomic; a directory entry named `new` stays visible to other threads throughout and refers to either old or new file; on failure other than I/O errors `new` is unaffected; spec does not define crash durability. **[primary]** https://pubs.opengroup.org/onlinepubs/9799919799/functions/rename.html
- Directory fsync: the usual pattern is write temp, fsync temp, rename, then fsync the parent directory so the rename survives power loss; many projects have filed bugs for omitting the directory fsync. **[secondary]** search snippets (e.g. https://github.com/npm/write-file-atomic/issues/64 titled "Rename atomicity is not enough"; https://0xkiire.com/crash-consistency-fsync-rename/). Linux ext4 `auto_da_alloc` replace-via-rename heuristic: **[UNVERIFIED]**, not fetched. Whether CultCache Rust/C# fsync the parent directory on Unix: **[UNVERIFIED]** (not seen in the excerpts read; Rust does `sync_all` on the temp, C# `Flush(true)` + `WriteThrough`; TS and Python do neither).
- Windows `MoveFileExW`:
  - `MOVEFILE_REPLACE_EXISTING` replaces the destination if ACLs allow; errors if destination is an existing directory; `MOVEFILE_WRITE_THROUGH` waits for the move to be flushed (documented as guaranteeing flush of a copy+delete move). Same-volume rename is a metadata operation. The doc page does not use the word "atomic" and does not describe behavior when the destination is open by another handle. **[primary]** https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw
  - Open-handle failure: reported from libuv/Bun and Postgres as ERROR_ACCESS_DENIED / SHARING_VIOLATION / LOCK_VIOLATION when another process has the destination open without FILE_SHARE_DELETE, or an anti-virus scan holds it. **[primary]** Postgres comment; **[secondary]** libuv/Bun.
  - Rust's std rename (1.0+ Windows 10 1607) may use `FileRenameInfoEx` with POSIX semantics, which can replace an open destination; CultCache Rust calls `MoveFileExW` directly instead **[src; primary for std]**.
- Windows `ReplaceFileW` (used by .NET `File.Replace`): replaced file opened with GENERIC_READ|DELETE|SYNCHRONIZE and share mode `READ|WRITE|DELETE`; replacement file opened with **no sharing mode specified**; preserves creation time, short name, object id, DACLs, encryption, compression, named streams; resulting file has the replacement's file ID; all files must be on the same volume; documented failure states leave the system in partially-moved states: ERROR_UNABLE_TO_REMOVE_REPLACED (1175; names unchanged), ERROR_UNABLE_TO_MOVE_REPLACEMENT (1176; with no backup, the replaced file "no longer exists" and the replacement exists under its original name), ERROR_UNABLE_TO_MOVE_REPLACEMENT_2 (1177). `REPLACEFILE_WRITE_THROUGH` "not supported". The page makes no statement that ReplaceFile is atomic; it describes it as combining steps. **[primary]** https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew
  - So on Windows, C# (`File.Replace`) and Rust/TS/Python (MoveFileEx or equivalent) use different primitives with different failure windows. **[inferred from the two pages]**
- .NET `File.Move` with overwrite on Windows/Unix: **[UNVERIFIED]** (CultCache C# uses `File.Move(temp, path)` only when the target does not exist; there is a window in which another writer creates the target between the `File.Exists` check and the move; the lock covers it when taken **[src, inferred]**).

---

## 4. Known failures and warnings

SQLite "How To Corrupt An SQLite Database File" (https://www.sqlite.org/howtocorrupt.html) **[primary]**:
- 2.1 Filesystems with broken or missing lock implementations (network filesystems, NFS) can corrupt when multiple processes access.
- 2.2 POSIX advisory locks are cancelled by `close()`: `close()` on any descriptor for the file by any thread in the process releases the process's locks on that file; example: thread B opens, reads and closes the DB file for a backup and thread A's locks vanish silently. SQLite works around it with a mutex-protected global list of open files, which fails if two copies of SQLite are linked into one app (2.3; a commercial product shipped this bug with infrequent corruption on Linux/Mac). WAL multi-process defenses added in 3.51.0 (2025-11-04).
- 2.4 Two processes using different locking protocols (e.g. POSIX advisory vs dot-file) cannot see each other's locks; all connections must use the same protocol.
- 2.5 Unlinking or renaming the DB file while in use: processes end up with different physical files sharing one journal; SQLite warns (since 3.7.17) when the file is unlinked while open.
- 2.6 Multiple links (hard/symbolic links): same hazard; resolved by canonicalising symlinks since 3.10.0.
- 2.7 Carrying a connection across `fork()`: all locking mechanisms fail with unpredictable results.
- 1.3/1.4 Hot journal deleted, moved or mispaired with database.
- The SQLite VFS page: incompatible unix VFS locking => corruption (section 2 above).

Other recorded failure modes:
- Windows `LockFileEx` locks are released by the OS on process death but with a lag (MS doc). **[primary]**
- CIFS flock becomes mandatory (man flock(2)). **[primary]**
- Lock file left behind: git (cleanup via atexit/signal; a hard kill leaves `.lock`, message tells operator to remove) **[primary]**; Gradle hangs after killed daemons **[secondary]**; `proper-lockfile` and `filelock.SoftFileLock` provide mtime / PID-based stale reclaim as a mitigation **[primary]**.
- Lock protocols that are compatible only when everyone uses the same one: SQLite (above); `filelock` docs do not state cross-type exclusion between `UnixFileLock`/`SoftFileLock`. **[primary]**
- Lost-update by missing cross-process lock around read-modify-write: `write-file-atomic` documents only same-process serialisation; no source read this pass reports a concrete lost-update bug in it. A search-based report of corrupted/missing "settings.json" on Windows from a failed atomic write exists as an unvetted issue (https://github.com/BarganConstantin/ccdeck/issues/772, **[secondary]**, read-only destination). Reports specific to cross-language writers of one file, other than SQLite's different-VFS warning: not found this pass. **[UNVERIFIED]**
- Lock file deleted by one participant while others hold or wait on it (different inode afterward): general hazard of delete-on-unlock lock files, not sourced this pass beyond SQLite 2.5 (unlink of a live file); **[UNVERIFIED]** as a named issue for flock sidecars.

---

## 5. Open items not resolved by reading (candidates for an empirical check; nothing done)

1. Do the C# `FileShare.None` lock and Rust `fs2::lock_exclusive` actually exclude each other on Windows (both open orders) and on Linux? Only inferred above.
2. Does fs2 on Windows lock the same byte range as std 1.89 `File::lock` and as Python `msvcrt.locking`/`filelock`? Unread.
3. macOS behavior of flock vs fcntl and of .NET FileShare there. Unread.
4. .NET release that introduced Unix FileShare-via-flock. Unread.
5. Whether any runtime fsyncs the parent directory after rename on Unix. Unread.
6. Cargo, npm, pip, Gradle lock protocol primary sources. Unread.
7. `portalocker`, `fs-ext`, `lockfile`, `proper-lockfile` behavior under Windows share modes. Unread.
