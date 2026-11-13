using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace GameCult.Caching
{
    /// <summary>The kernel32 calls a Windows replace needs, each answering 0 or a Win32 error code.</summary>
    internal interface IReplaceKernel
    {
        /// <summary>CreateFileW(staged, DELETE|SYNCHRONIZE), FileRenameInfoEx with REPLACE_IF_EXISTS|POSIX_SEMANTICS, CloseHandle.</summary>
        int RenamePosix(string staged, string destination);

        /// <summary>MoveFileExW(staged, destination, flags).</summary>
        int MoveFileEx(string staged, string destination, uint flags);

        void Sleep(int milliseconds);
    }

    /// <summary>
    /// Owns how a staged file becomes the store, for the single-file and directory stores. Windows tries a POSIX-semantics
    /// rename first (which makes a moment with the path absent rare for a lock-free reader, not impossible: a stalled rename was
    /// measured leaving it absent for 5-47 ms), falls back to write-through MoveFileExW where that is unsupported or does not
    /// resolve, falls back to the portable File.Replace if MoveFileExW itself does not resolve, and retries transient sharing
    /// errors; other OSes keep rename. Mirrors cultcache-ts store-file.ts
    /// replaceStoreFile (ruling variants:ruling:windows-replace-posix-first).
    /// </summary>
    internal static class StoreReplace
    {
        private const int ErrorAccessDenied = 5;
        private const int ErrorSharingViolation = 32;
        private const int ErrorLockViolation = 33;
        private const int ErrorNotSupported = 50;
        private const int ErrorInvalidParameter = 87;
        private const int ReplaceAttempts = 25;
        private const int ReplaceRetryMs = 20;

        /// <summary>MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH.</summary>
        internal const uint MoveFileReplaceWriteThrough = 0x9;

        internal static readonly IReplaceKernel WindowsKernel = new Kernel32Replace();

        public static void Replace(string staged, string destination)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                ReplaceWindows(staged, destination, WindowsKernel);
            }
            else
            {
                ReplacePortable(staged, destination);
            }
        }

        private static void ReplacePortable(string staged, string destination)
        {
            if (File.Exists(destination))
            {
                File.Replace(staged, destination, null);
            }
            else
            {
                File.Move(staged, destination);
            }
        }

        private static bool Unresolved(Exception ex) => ex is DllNotFoundException || ex is EntryPointNotFoundException;

        internal static void ReplaceWindows(string staged, string destination, IReplaceKernel kernel)
        {
            var posix = true;
            var lastCode = 0;
            for (var attempt = 1; attempt <= ReplaceAttempts; attempt++)
            {
                int code;
                try
                {
                    code = posix ? RenamePosixOrUnsupported(kernel, staged, destination) : kernel.MoveFileEx(staged, destination, MoveFileReplaceWriteThrough);
                    if (posix && (code == 1 || code == ErrorNotSupported || code == ErrorInvalidParameter))
                    {
                        // POSIX semantics are unsupported here (FAT, exFAT, before Windows 10 1607): the rest of this call is write-through.
                        posix = false;
                        code = kernel.MoveFileEx(staged, destination, MoveFileReplaceWriteThrough);
                    }
                }
                catch (Exception ex) when (Unresolved(ex))
                {
                    // kernel32's MoveFileExW does not resolve here (a sandboxed player): the runtime-owned replace is all that is left.
                    ReplacePortable(staged, destination);
                    return;
                }

                if (code == 0)
                {
                    return;
                }

                if (code != ErrorAccessDenied && code != ErrorSharingViolation && code != ErrorLockViolation)
                {
                    throw new IOException($"CultCache could not replace the store: Win32 error {code}.");
                }

                lastCode = code;
                kernel.Sleep(ReplaceRetryMs);
            }

            throw new IOException($"CultCache could not replace the store after {ReplaceAttempts} attempts: last Win32 error {lastCode}.");
        }

        private static int RenamePosixOrUnsupported(IReplaceKernel kernel, string staged, string destination)
        {
            try
            {
                return kernel.RenamePosix(staged, destination);
            }
            catch (Exception ex) when (Unresolved(ex))
            {
                // The POSIX call does not resolve: the same answer as "unsupported", so the replace falls back to MoveFileExW.
                return ErrorNotSupported;
            }
        }

        private sealed class Kernel32Replace : IReplaceKernel
        {
            private const uint DeleteSynchronize = 0x00110000;
            private const uint ShareAll = 0x7;
            private const uint OpenExisting = 3;
            private const uint FileAttributeNormal = 0x80;
            private const int FileRenameInfoEx = 22;
            private const uint RenameReplacePosix = 0x3;

            public int RenamePosix(string staged, string destination)
            {
                var handle = CreateFileW(staged, DeleteSynchronize, ShareAll, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
                if (handle == new IntPtr(-1))
                {
                    return Marshal.GetLastWin32Error();
                }

                try
                {
                    // FILE_RENAME_INFO: Flags u32 at 0, RootDirectory handle, FileNameLength u32, FileName (UTF-16, offsets for this pointer size).
                    var name = System.Text.Encoding.Unicode.GetBytes(Path.GetFullPath(destination));
                    var lengthOffset = 2 * IntPtr.Size;
                    var nameOffset = lengthOffset + 4;
                    var size = nameOffset + name.Length + 2;
                    var info = Marshal.AllocHGlobal(size);
                    try
                    {
                        for (var i = 0; i < size; i++) Marshal.WriteByte(info, i, 0);
                        Marshal.WriteInt32(info, 0, (int)RenameReplacePosix);
                        Marshal.WriteInt32(info, lengthOffset, name.Length);
                        Marshal.Copy(name, 0, info + nameOffset, name.Length);
                        return SetFileInformationByHandle(handle, FileRenameInfoEx, info, (uint)size) ? 0 : Marshal.GetLastWin32Error();
                    }
                    finally { Marshal.FreeHGlobal(info); }
                }
                finally { CloseHandle(handle); }
            }

            public int MoveFileEx(string staged, string destination, uint flags)
                => MoveFileExW(staged, destination, flags) ? 0 : Marshal.GetLastWin32Error();

            public void Sleep(int milliseconds) => Thread.Sleep(milliseconds);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool SetFileInformationByHandle(IntPtr handle, int informationClass, IntPtr information, uint size);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CloseHandle(IntPtr handle);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool MoveFileExW(string existing, string target, uint flags);
        }
    }
}
