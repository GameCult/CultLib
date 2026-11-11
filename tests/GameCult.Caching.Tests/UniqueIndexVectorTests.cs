#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // variants:ruling:unique-indexes. The canonical index text and the unique-index load verdicts in tests/vectors/unique-index.
    // The C# reference is the only writer (CULTCACHE_WRITE_VECTORS=1); every other run reads the committed bytes and
    // compares them byte for byte. Other runtimes are held to the same text and the same verdicts.
    public class UniqueIndexVectorTests
    {
        private const string WriteVariable = "CULTCACHE_WRITE_VECTORS";
        private static readonly DateTimeOffset StoredAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static bool Writing => Environment.GetEnvironmentVariable(WriteVariable) == "1";

        private static string VectorDirectory()
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                var vectors = Path.Combine(dir.FullName, "tests", "vectors");
                if (Directory.Exists(vectors)) return Path.Combine(vectors, "unique-index");
            }

            throw new DirectoryNotFoundException($"tests/vectors not found above {TestContext.CurrentContext.TestDirectory}.");
        }

        private static string VectorPath(string name) => Path.Combine(VectorDirectory(), name);

        private static void Check(string name, byte[] reference)
        {
            var path = VectorPath(name);
            if (Writing)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, reference);
                return;
            }

            Assert.That(File.Exists(path), Is.True, $"vector {name} is committed");
            Assert.That(Convert.ToHexString(reference), Is.EqualTo(Convert.ToHexString(File.ReadAllBytes(path))), $"vector {name}");
        }

        private static byte[] Committed(string name)
        {
            var path = VectorPath(name);
            Assert.That(File.Exists(path), Is.True, $"vector {name} is committed");
            return File.ReadAllBytes(path);
        }

        // ---- values.msgpack: [label, kind, value, text-or-nil] ----

        private sealed record Row(string Label, string Kind, object Value, string? Text);

        private static readonly MethodInfo RenderCanonicalNumber =
            typeof(CultDocumentRegistry).GetMethod("RenderCanonicalNumber", BindingFlags.NonPublic | BindingFlags.Static)!;

        private static object? ByIndex(CultCache cache, Type type, string alias, string value) =>
            typeof(CultCache).GetMethod(nameof(CultCache.GetByIndex))!.MakeGenericMethod(type).Invoke(cache, new object[] { alias, value });

        private static string? Number(object raw) => (string?)RenderCanonicalNumber.Invoke(null, new[] { raw });

        // Strings and booleans carry the text the rule states; numbers carry whatever the reference renders, never hand-typed.
        private static List<Row> Rows() => new()
        {
            new("plain", "string", "S-1", "S-1"),
            new("empty", "string", "", null),
            new("space", "string", " ", null),
            new("nel", "string", "\u0085", null),
            new("nbsp", "string", "\u00A0", null),
            new("ideographic-space", "string", "\u3000", null),
            new("vertical-tab", "string", "\u000B", null),
            new("bom", "string", "\uFEFF", "\uFEFF"),
            new("unit-separator", "string", "\u001F", "\u001F"),
            new("padded", "string", " a ", " a "),
            new("int64-max", "int64", long.MaxValue, Number(long.MaxValue)),
            new("int64-min", "int64", long.MinValue, Number(long.MinValue)),
            new("uint64-max", "uint64", ulong.MaxValue, Number(ulong.MaxValue)),
            new("double-zero", "double", 0.0, Number(0.0)),
            new("double-negative-zero", "double", -0.0, Number(-0.0)),
            new("double-tenth", "double", 0.1, Number(0.1)),
            new("double-1e21", "double", 1e21, Number(1e21)),
            new("double-2.5e-7", "double", 2.5e-7, Number(2.5e-7)),
            new("double-nan", "double", double.NaN, Number(double.NaN)),
            new("double-infinity", "double", double.PositiveInfinity, Number(double.PositiveInfinity)),
            new("bool-true", "bool", true, "True"),
            new("bool-false", "bool", false, "False"),
        };

        private static byte[] ValuesFile()
        {
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            var rows = Rows();
            writer.WriteArrayHeader(rows.Count);
            foreach (var row in rows)
            {
                writer.WriteArrayHeader(4);
                writer.Write(row.Label);
                writer.Write(row.Kind);
                switch (row.Value)
                {
                    case string s: writer.Write(s); break;
                    case long l: writer.Write(l); break;
                    case ulong u: writer.Write(u); break;
                    case double d: writer.Write(d); break;
                    case bool b: writer.Write(b); break;
                    default: throw new InvalidOperationException(row.Kind);
                }

                if (row.Text == null) writer.WriteNil(); else writer.Write(row.Text);
            }

            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        private static readonly Dictionary<string, Type> KindTypes = new[]
        {
            ("string", typeof(string)), ("int64", typeof(long)), ("uint64", typeof(ulong)), ("double", typeof(double)), ("bool", typeof(bool))
        }.ToDictionary(pair => pair.Item1, pair => EmittedDocumentTypes.Emit(
            "UqKind" + pair.Item1, "tests.uq_kind_" + pair.Item1, "tests.uq_kind_" + pair.Item1 + ".v1",
            new[] { new EmittedDocumentTypes.Field("Value", pair.Item2, 0, Index: "v") }));

        [Test]
        public void ValuesFileIsTheReferencesCanonicalText()
        {
            Check("values.msgpack", ValuesFile());

            var reader = new MessagePackReader(Committed("values.msgpack"));
            var count = reader.ReadArrayHeader();
            var rows = Rows();
            Assert.That(count, Is.EqualTo(rows.Count));
            foreach (var row in rows)
            {
                Assert.That(reader.ReadArrayHeader(), Is.EqualTo(4));
                Assert.That(reader.ReadString(), Is.EqualTo(row.Label));
                Assert.That(reader.ReadString(), Is.EqualTo(row.Kind));
                reader.Skip();
                var text = reader.TryReadNil() ? null : reader.ReadString();
                Assert.That(text, Is.EqualTo(row.Text), row.Label);

                var type = KindTypes[row.Kind];
                var registry = CultDocumentRegistry.ForTypes(new[] { type });
                using var cache = CultCacheMessagePack.Create(Path.Combine(Path.GetTempPath(), $"cultlib-uidxv-{Guid.NewGuid():N}.cc"), new CultCacheOpenOptions { Registry = registry });
                cache.Commit(batch =>
                {
                    batch.Upsert(type, EmittedDocumentTypes.New(type, ("Value", row.Value)), new CultRecordKey("one"));
                    batch.Upsert(type, EmittedDocumentTypes.New(type, ("Value", row.Value)), new CultRecordKey(text == null ? "two" : "one"));
                });
                if (text == null)
                {
                    Assert.That(cache.Get(new CultRecordKey("two")), Is.Not.Null, $"{row.Label}: a blank value commits twice");
                    foreach (var probe in new[] { "", " ", "NaN", "0", "Infinity", "∞" })
                        Assert.That(ByIndex(cache, type, "v", probe), Is.Null, $"{row.Label}: found by '{probe}'");
                }
                else
                {
                    Assert.That(ByIndex(cache, type, "v", text), Is.Not.Null, $"{row.Label}: found by its text");
                }
            }
        }

        // ---- the stores: written through a role, judged by an index ----

        private static readonly EmittedDocumentTypes.Field[] RoleFields =
        {
            new("Sku", typeof(string), 0, Role: "Sku"), new("Category", typeof(string), 1)
        };

        private static readonly EmittedDocumentTypes.Field[] IndexFields =
        {
            new("Sku", typeof(string), 0, Index: "Sku"), new("Category", typeof(string), 1)
        };

        private static readonly Type ItemRole = EmittedDocumentTypes.Emit("UqVecItemRole", "tests.unique_item", "tests.unique_item.v1", RoleFields);
        private static readonly Type ItemIndex = EmittedDocumentTypes.Emit("UqVecItemIndex", "tests.unique_item", "tests.unique_item.v1", IndexFields);

        private static readonly (string Key, string Sku, string Category)[] Items =
        {
            ("item:a", "S-1", "tools"), ("item:b", "S-1", "tools"), ("item:c", " ", "parts"), ("item:d", "\u0085", "parts")
        };

        private static byte[] StoreBytes(string[] keys)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"cultlib-uidxs-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "store.cc");
                using (var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions
                {
                    Registry = CultDocumentRegistry.ForTypes(new[] { ItemRole }), StoreFlushOnDispose = true
                }))
                {
                    cache.Clock = () => StoredAt;
                    foreach (var item in Items.Where(item => keys.Contains(item.Key)))
                        cache.UpsertAsync(ItemRole, EmittedDocumentTypes.New(ItemRole, ("Sku", item.Sku), ("Category", item.Category)), new CultRecordKey(item.Key)).GetAwaiter().GetResult();
                    cache.FlushAsync().GetAwaiter().GetResult();
                }

                return File.ReadAllBytes(path);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static readonly (string File, string? Alias, string Outcome, string? K1, string? K2)[] Outcomes =
        {
            ("duplicate-store.cc", "Sku", "refused", "item:a", "item:b"),
            ("duplicate-store.cc", null, "open", null, null),
            ("blank-store.cc", "Sku", "open", null, null),
        };

        private static byte[] OutcomesFile()
        {
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            writer.WriteArrayHeader(Outcomes.Length);
            foreach (var row in Outcomes)
            {
                writer.WriteArrayHeader(5);
                writer.Write(row.File);
                foreach (var text in new[] { row.Alias, row.Outcome, row.K1, row.K2 })
                    if (text == null) writer.WriteNil(); else writer.Write(text);
            }

            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        private static string? NextText(ref MessagePackReader reader) => reader.TryReadNil() ? null : reader.ReadString();

        [Test]
        public void StoreVectorsHoldTheirVerdicts()
        {
            Check("duplicate-store.cc", StoreBytes(new[] { "item:a", "item:b", "item:c", "item:d" }));
            Check("blank-store.cc", StoreBytes(new[] { "item:c", "item:d" }));
            Check("outcomes.msgpack", OutcomesFile());

            var reader = new MessagePackReader(Committed("outcomes.msgpack"));
            var count = reader.ReadArrayHeader();
            Assert.That(count, Is.EqualTo(Outcomes.Length));
            for (var i = 0; i < count; i++)
            {
                Assert.That(reader.ReadArrayHeader(), Is.EqualTo(5));
                var file = reader.ReadString()!;
                var alias = NextText(ref reader);
                var outcome = NextText(ref reader)!;
                var k1 = NextText(ref reader);
                var k2 = NextText(ref reader);

                var directory = Path.Combine(Path.GetTempPath(), $"cultlib-uidxo-{Guid.NewGuid():N}");
                Directory.CreateDirectory(directory);
                try
                {
                    var path = Path.Combine(directory, "store.cc");
                    File.WriteAllBytes(path, Committed(file));
                    var before = File.ReadAllBytes(path);
                    var type = alias == null ? ItemRole : ItemIndex;
                    var registry = CultDocumentRegistry.ForTypes(new[] { type });
                    if (outcome == "open")
                    {
                        using var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = registry });
                        Assert.That(cache.Get(new CultRecordKey("item:c")), Is.Not.Null, file);
                    }
                    else
                    {
                        var refused = Assert.Throws<InvalidOperationException>(() => CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = registry }).Dispose())!;
                        Assert.That(refused.Message, Is.EqualTo(
                            $"Records {k1} and {k2} (tests.unique_item) both hold unique index '{alias}'; an index value names one record."));
                        Assert.That(refused.Message, Does.Not.Contain("S-1"));
                    }

                    Assert.That(File.ReadAllBytes(path), Is.EqualTo(before), $"{file} is untouched");
                }
                finally
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
    }
}
