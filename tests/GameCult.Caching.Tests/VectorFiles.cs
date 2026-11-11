#nullable enable
using System;
using System.IO;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // The one place a C#-written vector directory is found, written and compared. The C# reference is the only writer
    // (CULTCACHE_WRITE_VECTORS=1, never set in CI); every other run reads the committed bytes and compares them byte for byte.
    internal static class VectorFiles
    {
        private const string WriteVariable = "CULTCACHE_WRITE_VECTORS";

        internal static readonly DateTimeOffset StoredAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        internal static bool Writing => Environment.GetEnvironmentVariable(WriteVariable) == "1";

        internal static string Directory(string vector)
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                var vectors = System.IO.Path.Combine(dir.FullName, "tests", "vectors");
                if (System.IO.Directory.Exists(vectors)) return System.IO.Path.Combine(vectors, vector);
            }

            throw new DirectoryNotFoundException($"tests/vectors not found above {TestContext.CurrentContext.TestDirectory}.");
        }

        internal static void Check(string vector, string name, byte[] reference)
        {
            var path = System.IO.Path.Combine(Directory(vector), name);
            if (Writing)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, reference);
                return;
            }

            Assert.That(File.Exists(path), Is.True, $"vector {name} is committed");
            Assert.That(Convert.ToHexString(reference), Is.EqualTo(Convert.ToHexString(File.ReadAllBytes(path))), $"vector {name}");
        }

        internal static byte[] Committed(string vector, string name)
        {
            var path = System.IO.Path.Combine(Directory(vector), name);
            Assert.That(File.Exists(path), Is.True, $"vector {name} is committed");
            return File.ReadAllBytes(path);
        }
    }
}
