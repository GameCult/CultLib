# Windows store replace: prior art and probe (2026-10-03)

This research backs `variants:question:windows-store-replace-primitive`. It came from an Imagination pass during the variants campaign. The probe source and raw results lived in that session's scratchpad (`win-replace/probe/Replacers/Replacers.cs`, `result-net10.txt`, `result-mono.txt`).

## Problem

On Windows, CultCache C# replaces a single-file store with `File.Replace`, which calls Win32 `ReplaceFile`. While that replace runs, a lock-free reader can find nothing at the path, and the store opens as empty. Under the h1 cut's probe on Starfire, 1,722 of 17,313 opens came back empty. On Linux, which replaces with `rename`, 0 of 6,252 did.

## Probe

The probe copies the C# write path:
- `<file>.lock` opened with `FileShare.None`;
- a GUID temp file written with WriteThrough and `Flush(true)`;
- the replace itself.

It copies the read path too: an attribute check, then an open with `ReadWrite|Delete`. A writer thread and a tight reader loop share one process. The store sits on NTFS (C:) with Defender real-time protection on. A hash check found no torn reads.

### Empty-store opens per primitive

| Primitive | .NET 10 (2,000 writes) | Unity Mono 6.13 (1,000 writes) | Notes |
|---|---|---|---|
| `File.Replace` (`ReplaceFile`) | 46,336 / 244,197 (19%) | 15,915 / 48,298 (33%) | Readers also hit sharing violations: 2,157 on .NET, 5 on Mono |
| `MoveFileExW(REPLACE_EXISTING\|WRITE_THROUGH)` | 0 / 417,115 | 0 / 39,094 | 1,145 of 2,000 first writer attempts failed with error 5 (access denied); all landed after retries; 2x slower |
| `SetFileInformationByHandle(FileRenameInfoEx, REPLACE_IF_EXISTS\|POSIX_SEMANTICS)` | 0 / 9,756 | 0 / 5,282 | 0 errors, fastest (7.2 s). The reader sampled sparsely, about 5 opens per write against 120-200 for the others |
| Reader waits on `<file>.lock` when the path is empty | 50 / 12,660, all recovered | 37 / 2,275, all recovered | Reader waits up to 1,584 ms (mean 371 ms) |

### A reader holding the store open during one replace, no retry

| Holder's share mode | `File.Replace` | `MoveFileExW` | POSIX rename |
|---|---|---|---|
| `ReadWrite\|Delete` (C# readers, Rust std, libuv) | ok | error 5 | ok |
| `ReadWrite` (CRT and Python `open`) | error 32 | error 5 | error 32 |
| `Read` | error 32 | error 5 | error 32 |

When a replace succeeded, the holder kept reading the old bytes, and a new open got the new bytes.

## Primary sources

- **ReplaceFileW.** It renames the original aside and then moves the replacement in, which is the empty window. It opens the replacement with no sharing, and it makes no atomicity claim. https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew
- **FILE_RENAME_INFORMATION.** Under classic semantics, a rename fails if the target exists and has open handles; that is `MoveFileExW`'s error 5. POSIX semantics replaces a target that has open handles, and is available from Windows 10 1607. https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information

## What established software does on Windows

| Software | Replace primitive on Windows |
|---|---|
| .NET `File.Replace` | `ReplaceFile` |
| .NET `File.Move(overwrite)` | `MoveFileExW(COPY_ALLOWED\|REPLACE_EXISTING)`; not in netstandard2.1 |
| Rust std `rename` | `MoveFileExW`, then POSIX rename if that returns access denied |
| git-for-windows `mingw_rename` | POSIX rename first, then `MoveFileExW`, then clears read-only and retries |
| Python `os.replace`, libuv (Node) | `MoveFileExW(REPLACE_EXISTING)` (Python from bug-tracker history) |
| Postgres | `MoveFileEx` with 100 retries, 100 ms apart, on errors 5, 32 and 33 (antivirus named) |
| SQLite, LMDB | Never rename the database. SQLite writes in place under a journal or WAL, and retries I/O on lock errors. LMDB uses copy-on-write pages and a meta-page switch, so readers take no locks |
| Mono `File.Replace` | `ReplaceFileW` |
| IL2CPP (Unity 6000.4.2f1) | `os/Win32/File.cpp:263` calls `ReplaceFileW`, so built players have the same window |

C# is the only CultCache runtime that uses `ReplaceFile`, and the only one with the empty window. CultCache Rust calls `MoveFileExW` directly.

## Portability of a P/Invoke path

- **netstandard2.1:** builds clean with the three kernel32 calls. A runtime OS check is enough; no platform split is needed.
- **Unity Mono:** ran under the Editor's `mono.exe`.
- **IL2CPP:** not run. Its Win32 loader hardcodes `CreateFileW`, `MoveFileExW` and `SetFileInformationByHandle`.
- **GDK (`IL2CPP_TARGET_WINDOWS_GAMES`):** that loader table is absent, so behaviour is unverified. Catch `DllNotFoundException` and `EntryPointNotFoundException`, and fall back.
- **FAT, exFAT and possibly SMB:** probably lack POSIX rename (unverified), so the `MoveFileExW` fallback is needed there.
- **What leaving `ReplaceFile` drops:** creation time, DACL, object id and alternate streams. CultCache uses none of them.

## Caveats

- The POSIX-rename reader was sparse. 0 of 9,756 is still strong: about 1,850 empty opens would be expected at `File.Replace`'s rate.
- All runs were in one process.
- Mono reported `arch=X86`; the rename succeeded, so the struct layout was right.

What would change the conclusion: a cross-process rerun at h1's reader density that shows a window under POSIX rename, or an IL2CPP or GDK player where the P/Invoke does not resolve.
