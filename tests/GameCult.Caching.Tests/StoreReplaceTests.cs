#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    public class StoreReplaceTests
    {
        private sealed class FakeKernel : IReplaceKernel
        {
            private readonly Queue<int> _posix;
            private readonly Queue<int> _moveEx;
            public readonly List<string> Log = new List<string>();
            public readonly List<int> Sleeps = new List<int>();
            public Exception? PosixThrows;
            public Exception? MoveExThrows;

            public FakeKernel(int[] posix, int[] moveEx)
            {
                _posix = new Queue<int>(posix);
                _moveEx = new Queue<int>(moveEx);
            }

            public int RenamePosix(string staged, string destination)
            {
                Log.Add("posix");
                if (PosixThrows != null) throw PosixThrows;
                return _posix.Count > 0 ? _posix.Dequeue() : 0;
            }

            public int MoveFileEx(string staged, string destination, uint flags)
            {
                Log.Add("moveex:" + flags);
                if (MoveExThrows != null) throw MoveExThrows;
                return _moveEx.Count > 0 ? _moveEx.Dequeue() : 0;
            }

            public void Sleep(int milliseconds) => Sleeps.Add(milliseconds);
        }

        private static IOException Fails(FakeKernel kernel)
            => Assert.Throws<IOException>(() => StoreReplace.ReplaceWindows("a", "b", kernel))!;

        [Test]
        public void APosixRenameThatWorksIsTheWholeReplace()
        {
            var kernel = new FakeKernel(new[] { 0 }, new int[0]);
            StoreReplace.ReplaceWindows("a", "b", kernel);
            Assert.That(kernel.Log, Is.EqualTo(new[] { "posix" }));
            Assert.That(kernel.Sleeps, Is.Empty);
        }

        [TestCase(1)]
        [TestCase(50)]
        [TestCase(87)]
        public void WhereAPosixRenameIsUnsupportedTheReplaceFallsBackToWriteThroughMoveFileEx(int unsupported)
        {
            var kernel = new FakeKernel(new[] { unsupported }, new[] { 0 });
            StoreReplace.ReplaceWindows("a", "b", kernel);
            Assert.That(kernel.Log, Is.EqualTo(new[] { "posix", "moveex:9" }));
        }

        [Test]
        public void OnceThePosixRenameIsUnsupportedTheRestOfTheCallStaysOnMoveFileEx()
        {
            var kernel = new FakeKernel(new[] { 87 }, new[] { 32, 0 });
            StoreReplace.ReplaceWindows("a", "b", kernel);
            Assert.That(kernel.Log, Is.EqualTo(new[] { "posix", "moveex:9", "moveex:9" }));
            Assert.That(kernel.Sleeps, Is.EqualTo(new[] { 20 }));
        }

        [TestCase(5)]
        [TestCase(32)]
        [TestCase(33)]
        public void ATransientErrorRetriesTheSamePrimitiveAfterTwentyMilliseconds(int transient)
        {
            var kernel = new FakeKernel(new[] { transient, transient, 0 }, new int[0]);
            StoreReplace.ReplaceWindows("a", "b", kernel);
            Assert.That(kernel.Log, Is.EqualTo(new[] { "posix", "posix", "posix" }));
            Assert.That(kernel.Sleeps, Is.EqualTo(new[] { 20, 20 }));
        }

        [TestCase(1)]
        [TestCase(50)]
        [TestCase(87)]
        public void AnUnsupportedCodeAfterTheFallbackIsAnErrorNotAnotherFallback(int unsupported)
        {
            var kernel = new FakeKernel(new[] { 87 }, new[] { unsupported });
            var error = Fails(kernel);
            Assert.That(error.Message, Does.Contain("Win32 error " + unsupported));
            Assert.That(kernel.Log, Is.EqualTo(new[] { "posix", "moveex:9" }));
        }

        private static IEnumerable<Exception> Unresolved()
        {
            yield return new DllNotFoundException("kernel32.dll");
            yield return new EntryPointNotFoundException("SetFileInformationByHandle");
        }

        [TestCaseSource(nameof(Unresolved))]
        public void APosixCallThatDoesNotResolveTakesTheUnsupportedFallback(Exception unresolved)
        {
            var kernel = new FakeKernel(new int[0], new[] { 0 }) { PosixThrows = unresolved };
            StoreReplace.ReplaceWindows("a", "b", kernel);
            Assert.That(kernel.Log, Is.EqualTo(new[] { "posix", "moveex:9" }));
        }

        [TestCaseSource(nameof(Unresolved))]
        public void AMoveFileExThatDoesNotResolveDegradesToThePortableReplace(Exception unresolved)
        {
            var dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "storereplace-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var destination = Path.Combine(dir, "store.cc");
                var staged = Path.Combine(dir, "staged");
                File.WriteAllBytes(destination, new byte[] { 1 });
                File.WriteAllBytes(staged, new byte[] { 2 });
                var kernel = new FakeKernel(new[] { 87 }, new int[0]) { MoveExThrows = unresolved };
                StoreReplace.ReplaceWindows(staged, destination, kernel);
                Assert.That(kernel.Log, Is.EqualTo(new[] { "posix", "moveex:9" }));
                Assert.That(File.ReadAllBytes(destination), Is.EqualTo(new byte[] { 2 }));
                Assert.That(File.Exists(staged), Is.False);

                // Both calls unresolved: the same portable replace, here onto an absent destination.
                File.Delete(destination);
                File.WriteAllBytes(staged, new byte[] { 3 });
                var both = new FakeKernel(new int[0], new int[0]) { PosixThrows = unresolved, MoveExThrows = unresolved };
                StoreReplace.ReplaceWindows(staged, destination, both);
                Assert.That(File.ReadAllBytes(destination), Is.EqualTo(new byte[] { 3 }));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void TheFallbackRunsInTheSameAttemptEvenOnTheLastOne()
        {
            var posix = new int[25].Fill(32);
            posix[24] = 87;
            var kernel = new FakeKernel(posix, new[] { 0 });
            StoreReplace.ReplaceWindows("a", "b", kernel);
            Assert.That(kernel.Log.Count, Is.EqualTo(26));
            Assert.That(kernel.Log[24], Is.EqualTo("posix"));
            Assert.That(kernel.Log[25], Is.EqualTo("moveex:9"));
        }

        [Test]
        public void ARetriedMoveFileExThatAnswersUnsupportedFailsAfterOneCallNotTwo()
        {
            var kernel = new FakeKernel(new[] { 87 }, new[] { 32, 87 });
            Assert.That(Fails(kernel).Message, Does.Contain("Win32 error 87"));
            Assert.That(kernel.Log, Is.EqualTo(new[] { "posix", "moveex:9", "moveex:9" }));
        }

        [Test]
        public void ARetryThatNeverSucceedsGivesUpAfterTwentyFiveAttemptsNamingTheCode()
        {
            var kernel = new FakeKernel(new int[30].Fill(32), new int[0]);
            var error = Fails(kernel);
            Assert.That(error.Message, Does.Contain("25 attempts").And.Contain("last Win32 error 32"));
            Assert.That(kernel.Log.Count, Is.EqualTo(25));
            Assert.That(kernel.Sleeps.Count, Is.EqualTo(25));
        }

        [Test]
        public void AnyOtherCodeThrowsAtOnceNamingTheCode()
        {
            var kernel = new FakeKernel(new[] { 2 }, new int[0]);
            var error = Fails(kernel);
            Assert.That(error.Message, Does.Contain("Win32 error 2"));
            Assert.That(kernel.Log.Count, Is.EqualTo(1));
            Assert.That(kernel.Sleeps, Is.Empty);
        }

        [Test]
        public void AFallbackThatFailsOtherwiseThrowsItsCode()
        {
            var kernel = new FakeKernel(new[] { 87 }, new[] { 3 });
            Assert.That(Fails(kernel).Message, Does.Contain("Win32 error 3"));
        }

        [Test]
        public void ReplaceOverAnExistingAndThenAnAbsentDestinationLeavesTheStagedBytes()
        {
            var dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "storereplace-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var destination = Path.Combine(dir, "store.cc");
                var staged = Path.Combine(dir, "staged");
                File.WriteAllBytes(destination, new byte[] { 1 });
                File.WriteAllBytes(staged, new byte[] { 2 });
                StoreReplace.Replace(staged, destination);
                Assert.That(File.ReadAllBytes(destination), Is.EqualTo(new byte[] { 2 }));
                Assert.That(File.Exists(staged), Is.False);

                File.Delete(destination);
                File.WriteAllBytes(staged, new byte[] { 3 });
                StoreReplace.Replace(staged, destination);
                Assert.That(File.ReadAllBytes(destination), Is.EqualTo(new byte[] { 3 }));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void OnWindowsAReplaceOverADestinationAReaderHoldsOpenSucceedsOnTheFirstAttempt()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Assert.Ignore("The real kernel runs on Windows only.");

            var dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "storereplace-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var destination = Path.Combine(dir, "store.cc");
                var staged = Path.Combine(dir, "staged");
                File.WriteAllBytes(destination, new byte[] { 1 });
                File.WriteAllBytes(staged, new byte[] { 2 });
                using (new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var kernel = new CountingKernel(StoreReplace.WindowsKernel);
                    StoreReplace.ReplaceWindows(staged, destination, kernel);
                    Assert.That(kernel.PosixCalls, Is.EqualTo(1));
                    Assert.That(kernel.MoveExCalls, Is.EqualTo(0));
                    Assert.That(kernel.Sleeps, Is.EqualTo(0));
                    Assert.That(kernel.LastCode, Is.EqualTo(0));
                }

                Assert.That(File.ReadAllBytes(destination), Is.EqualTo(new byte[] { 2 }));
            }
            finally { Directory.Delete(dir, true); }
        }

        private sealed class CountingKernel : IReplaceKernel
        {
            private readonly IReplaceKernel _inner;
            public int PosixCalls, MoveExCalls, Sleeps, LastCode;
            public CountingKernel(IReplaceKernel inner) { _inner = inner; }

            public int RenamePosix(string staged, string destination)
            {
                PosixCalls++;
                return LastCode = _inner.RenamePosix(staged, destination);
            }

            public int MoveFileEx(string staged, string destination, uint flags)
            {
                MoveExCalls++;
                return LastCode = _inner.MoveFileEx(staged, destination, flags);
            }

            public void Sleep(int milliseconds) { Sleeps++; _inner.Sleep(milliseconds); }
        }
    }

    internal static class ArrayFill
    {
        public static int[] Fill(this int[] array, int value)
        {
            for (var i = 0; i < array.Length; i++) array[i] = value;
            return array;
        }
    }
}
