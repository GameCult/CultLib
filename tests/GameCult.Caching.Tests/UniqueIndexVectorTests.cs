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

        // ---- refusals.msgpack: [label, ops, refusal]; the pair a refusal names is a function of the resulting state ----

        private sealed record Schema(string Name, Type Role, Type Index, string SecondField);

        private static Schema Plain(string suffix, string name) => new(
            name,
            EmittedDocumentTypes.Emit("UqVec" + suffix + "Role", name, name + ".v1", RoleFields),
            EmittedDocumentTypes.Emit("UqVec" + suffix + "Index", name, name + ".v1", IndexFields),
            "Category");

        private static readonly Schema[] Schemas =
        {
            new("tests.unique_item", ItemRole, ItemIndex, "Category"),
            new("tests.uq_pair",
                EmittedDocumentTypes.Emit("UqVecPairRole", "tests.uq_pair", "tests.uq_pair.v1",
                    new EmittedDocumentTypes.Field[] { new("Sku", typeof(string), 0, Role: "Sku"), new("Alt", typeof(string), 1, Role: "Alt") }),
                EmittedDocumentTypes.Emit("UqVecPairIndex", "tests.uq_pair", "tests.uq_pair.v1",
                    new EmittedDocumentTypes.Field[] { new("Sku", typeof(string), 0, Index: "Sku"), new("Alt", typeof(string), 1, Index: "Alt") }),
                "Alt"),
            new("tests.uq_alias",
                EmittedDocumentTypes.Emit("UqVecAliasRole", "tests.uq_alias", "tests.uq_alias.v1",
                    new EmittedDocumentTypes.Field[] { new("Sku", typeof(string), 0, Role: "Sku"), new("Alt", typeof(string), 1, Role: "alt") }),
                EmittedDocumentTypes.Emit("UqVecAliasIndex", "tests.uq_alias", "tests.uq_alias.v1",
                    new EmittedDocumentTypes.Field[] { new("Sku", typeof(string), 0, Index: "Sku"), new("Alt", typeof(string), 1, Index: "alt") }),
                "Alt"),
            Plain("Alpha", "tests.uq_alpha"),
            Plain("Beta", "tests.uq_beta"),
        };

        private static Schema SchemaNamed(string name) => Schemas.Single(schema => schema.Name == name);

        private sealed record Rec(string Key, string Schema, string Sku, string Second = "");

        private sealed record Op(string Kind, string? File = null, Rec[]? Records = null);

        private sealed record Refusal(string K1, string K2, string Schema, string Alias);

        private sealed record Case(string Label, Op[] Ops, Refusal? Refusal);

        private const string Item = "tests.unique_item";
        private const string Pair = "tests.uq_pair";
        private const string Alias = "tests.uq_alias";
        private const string Alpha = "tests.uq_alpha";
        private const string Beta = "tests.uq_beta";

        private static Rec[] Items_(params (string Key, string Sku)[] rows) => rows.Select(row => new Rec(row.Key, Item, row.Sku)).ToArray();
        private static Op Open(string? file = null) => new("open", file);
        private static Op Pull(string file) => new("pull", file);
        private static Op Commit(params Rec[] records) => new("commit", Records: records);

        private static readonly Rec[] PairRecords = { new("item:a", Pair, "S", "X"), new("item:b", Pair, "S", "Y"), new("item:c", Pair, "T", "Y") };
        private static readonly Rec[] SurrogateRecords = Items_(("k\uFF01", "S"), ("k\U0001F600", "S"), ("k\uE000", "S"));
        private static readonly Rec[] TypeRecords =
        {
            new("b:1", Alpha, "P"), new("a:1", Beta, "P"), new("b:2", Alpha, "P"), new("a:2", Beta, "P")
        };
        private static readonly Rec[] AliasRecords = { new("item:a", Alias, "S", "X"), new("item:b", Alias, "S", "Y"), new("item:c", Alias, "T", "Y") };
        private static readonly Rec[] RepullBefore = Items_(("item:a1", "A"), ("item:b1", "C"));
        private static readonly Rec[] RepullAfter = Items_(("item:a1", "A"), ("item:b1", "B"), ("item:b2", "B"), ("item:c1", "A"));
        private static readonly Rec[] KeyCaseRecords = Items_(("a", "S"), ("z", "S"), ("B", "T"), ("C", "T"));
        private static readonly Rec[] HolderCaseRecords = Items_(("a", "S"), ("A", "S"));
        private static readonly Rec[] KeyPunctuationRecords = Items_(("_", "S"), ("a", "S"), ("Z", "T"), ("b", "T"));
        private static readonly Rec[] SurrogateGroupRecords = Items_(("\uE000", "S"), ("\uE000b", "S"), ("\U0001F600", "T"), ("\U0001F600b", "T"));

        private static readonly (string File, Rec[] Records)[] RefusalStores =
        {
            ("pair-store.cc", PairRecords), ("surrogate-store.cc", SurrogateRecords), ("types-store.cc", TypeRecords),
            ("repull-before.cc", RepullBefore), ("repull-after.cc", RepullAfter), ("alias-store.cc", AliasRecords),
            ("key-case-store.cc", KeyCaseRecords), ("key-punctuation-store.cc", KeyPunctuationRecords),
            ("surrogate-groups-store.cc", SurrogateGroupRecords),
            ("holder-case-store.cc", HolderCaseRecords),
        };

        private static readonly Refusal SurrogateRefusal = new("k\U0001F600", "k\uE000", Item, "Sku");
        private static readonly Refusal TypesRefusal = new("a:1", "a:2", Beta, "Sku");
        private static readonly Refusal PairRefusal = new("item:b", "item:c", Pair, "Alt");
        private static readonly Refusal AliasRefusal = new("item:a", "item:b", Alias, "Sku");
        private static readonly Refusal RepullRefusal = new("item:a1", "item:c1", Item, "Sku");
        private static readonly Refusal KeyCaseRefusal = new("B", "C", Item, "Sku");
        private static readonly Refusal HolderCaseRefusal = new("A", "a", Item, "Sku");
        private static readonly Refusal KeyPunctuationRefusal = new("Z", "b", Item, "Sku");
        private static readonly Refusal SurrogateGroupRefusal = new("\U0001F600", "\U0001F600b", Item, "Sku");

        private static readonly Case[] Cases =
        {
            new("batch-later-value-staged-first", new[] { Open(), Commit(Items_(("x", "A"), ("y", "A"), ("a", "B"), ("b", "B"))) },
                new Refusal("a", "b", Item, "Sku")),
            new("batch-surrogate-keys", new[] { Open(), Commit(SurrogateRecords) }, SurrogateRefusal),
            new("batch-interleaved-types", new[] { Open(), Commit(TypeRecords) }, TypesRefusal),
            new("batch-two-aliases", new[] { Open(), Commit(PairRecords) }, PairRefusal),
            new("held-then-landing", new[] { Open(), Commit(Items_(("m", "S"))), Commit(Items_(("a", "S"))) },
                new Refusal("a", "m", Item, "Sku")),
            new("held-in-the-least-group", new[] { Open(), Commit(Items_(("m", "A"))), Commit(Items_(("z", "B"), ("y", "B"), ("a", "A"))) },
                new Refusal("a", "m", Item, "Sku")),
            new("swap-off-a-held-value", new[] { Open(), Commit(Items_(("a1", "A"))), Commit(Items_(("a1", "B"), ("c1", "A"))) }, null),
            new("load-two-aliases", new[] { Open("pair-store.cc") }, PairRefusal),
            new("load-surrogate-keys", new[] { Open("surrogate-store.cc") }, SurrogateRefusal),
            new("load-interleaved-types", new[] { Open("types-store.cc") }, TypesRefusal),
            new("repull-held-pair", new[] { Open("repull-before.cc"), Pull("repull-after.cc") }, RepullRefusal),
            new("load-repull-after", new[] { Open("repull-after.cc") }, RepullRefusal),
            new("commit-onto-a-loaded-holder", new[] { Open("repull-before.cc"), Commit(Items_(("item:z", "A"))) },
                new Refusal("item:a1", "item:z", Item, "Sku")),
            // Rows 14-19 extend the spec's floor of 13: orderings a code-point, case-insensitive or culture compare would flip.
            new("batch-key-case", new[] { Open(), Commit(KeyCaseRecords) }, KeyCaseRefusal),
            new("batch-key-punctuation", new[] { Open(), Commit(KeyPunctuationRecords) }, KeyPunctuationRefusal),
            new("batch-key-case-in-a-group", new[] { Open(), Commit(Items_(("a", "S"), ("B", "S"))) },
                new Refusal("B", "a", Item, "Sku")),
            new("batch-surrogate-groups", new[] { Open(), Commit(SurrogateGroupRecords) }, SurrogateGroupRefusal),
            new("batch-alias-case", new[] { Open(), Commit(AliasRecords) }, AliasRefusal),
            new("load-alias-case", new[] { Open("alias-store.cc") }, AliasRefusal),
            // Rows 20-22 load rows 14, 15 and 17's records, so a runtime with no batch commit still walks cross-group key order.
            new("load-key-case", new[] { Open("key-case-store.cc") }, KeyCaseRefusal),
            new("load-key-punctuation", new[] { Open("key-punctuation-store.cc") }, KeyPunctuationRefusal),
            new("load-surrogate-groups", new[] { Open("surrogate-groups-store.cc") }, SurrogateGroupRefusal),
            // Rows 23-24: two holders of one value that differ only by case are two holders; a case-folding dedupe would see one.
            new("batch-holders-by-case", new[] { Open(), Commit(HolderCaseRecords) }, HolderCaseRefusal),
            new("load-holders-by-case", new[] { Open("holder-case-store.cc") }, HolderCaseRefusal),
        };

        private static byte[] RefusalStoreBytes(Rec[] records)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"cultlib-uidxr-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "store.cc");
                var roles = records.Select(record => SchemaNamed(record.Schema).Role).Distinct().ToArray();
                using (var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions
                {
                    Registry = CultDocumentRegistry.ForTypes(roles), StoreFlushOnDispose = true
                }))
                {
                    cache.Clock = () => StoredAt;
                    foreach (var record in records)
                    {
                        var schema = SchemaNamed(record.Schema);
                        cache.UpsertAsync(schema.Role, EmittedDocumentTypes.New(schema.Role, ("Sku", record.Sku), (schema.SecondField, record.Second)), new CultRecordKey(record.Key))
                            .GetAwaiter().GetResult();
                    }

                    cache.FlushAsync().GetAwaiter().GetResult();
                }

                return File.ReadAllBytes(path);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static byte[] RefusalsFile()
        {
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            writer.WriteArrayHeader(Cases.Length);
            foreach (var row in Cases)
            {
                writer.WriteArrayHeader(3);
                writer.Write(row.Label);
                writer.WriteArrayHeader(row.Ops.Length);
                foreach (var op in row.Ops)
                {
                    writer.WriteArrayHeader(2);
                    writer.Write(op.Kind);
                    if (op.Records != null)
                    {
                        writer.WriteArrayHeader(op.Records.Length);
                        foreach (var record in op.Records)
                        {
                            writer.WriteArrayHeader(4);
                            writer.Write(record.Key);
                            writer.Write(record.Schema);
                            writer.Write(record.Sku);
                            writer.Write(record.Second);
                        }
                    }
                    else if (op.File == null) writer.WriteNil();
                    else writer.Write(op.File);
                }

                if (row.Refusal == null) writer.WriteNil();
                else
                {
                    writer.WriteArrayHeader(4);
                    writer.Write(row.Refusal.K1);
                    writer.Write(row.Refusal.K2);
                    writer.Write(row.Refusal.Schema);
                    writer.Write(row.Refusal.Alias);
                }
            }

            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        // Performs one op of a row against the open cache; the cache's own file is the only state it leaves behind.
        private static void Perform(ref CultCache? cache, string path, Op op)
        {
            var indexTypes = Schemas.Select(schema => schema.Index).ToArray();
            switch (op.Kind)
            {
                case "open":
                    if (op.File != null) File.WriteAllBytes(path, Committed(op.File));
                    cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = CultDocumentRegistry.ForTypes(indexTypes) });
                    break;
                case "pull":
                    File.WriteAllBytes(path, Committed(op.File!));
                    cache!.PullAllBackingStoresAsync().GetAwaiter().GetResult();
                    break;
                case "commit":
                    cache!.Commit(batch =>
                    {
                        foreach (var record in op.Records!)
                        {
                            var schema = SchemaNamed(record.Schema);
                            batch.Upsert(schema.Index, EmittedDocumentTypes.New(schema.Index, ("Sku", record.Sku), (schema.SecondField, record.Second)), new CultRecordKey(record.Key));
                        }
                    });
                    break;
                default:
                    throw new InvalidOperationException(op.Kind);
            }
        }

        [Test]
        public void RefusalsHoldTheRule()
        {
            Check("refusals.msgpack", RefusalsFile());
            foreach (var (file, records) in RefusalStores)
                Check(file, RefusalStoreBytes(records));

            // The committed file is what is walked, not the in-memory table.
            var reader = new MessagePackReader(Committed("refusals.msgpack"));
            var count = reader.ReadArrayHeader();
            Assert.That(count, Is.EqualTo(Cases.Length));
            var walked = 0;
            for (var i = 0; i < count; i++)
            {
                Assert.That(reader.ReadArrayHeader(), Is.EqualTo(3));
                var label = reader.ReadString()!;
                var ops = new List<Op>();
                for (var n = reader.ReadArrayHeader(); n > 0; n--)
                {
                    Assert.That(reader.ReadArrayHeader(), Is.EqualTo(2));
                    var kind = reader.ReadString()!;
                    if (kind == "commit")
                    {
                        var records = new List<Rec>();
                        for (var m = reader.ReadArrayHeader(); m > 0; m--)
                        {
                            Assert.That(reader.ReadArrayHeader(), Is.EqualTo(4));
                            records.Add(new Rec(reader.ReadString()!, reader.ReadString()!, reader.ReadString()!, reader.ReadString()!));
                        }

                        ops.Add(new Op(kind, Records: records.ToArray()));
                    }
                    else ops.Add(new Op(kind, NextText(ref reader)));
                }

                Refusal? refusal = null;
                if (!reader.TryReadNil())
                {
                    Assert.That(reader.ReadArrayHeader(), Is.EqualTo(4));
                    refusal = new Refusal(reader.ReadString()!, reader.ReadString()!, reader.ReadString()!, reader.ReadString()!);
                }

                var directory = Path.Combine(Path.GetTempPath(), $"cultlib-uidxf-{Guid.NewGuid():N}");
                Directory.CreateDirectory(directory);
                CultCache? cache = null;
                try
                {
                    var path = Path.Combine(directory, "store.cc");
                    foreach (var op in ops.Take(ops.Count - 1))
                        Perform(ref cache, path, op);

                    var last = ops[^1];
                    if (refusal == null)
                    {
                        Assert.DoesNotThrow(() => Perform(ref cache, path, last), label);
                    }
                    else
                    {
                        cache?.FlushAsync().GetAwaiter().GetResult();
                        var before = last.Kind == "open" && last.File != null ? Committed(last.File)
                            : last.Kind == "pull" ? Committed(last.File!)
                            : File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
                        CultCache? attempt = cache;
                        var refused = Assert.Throws<InvalidOperationException>(() => Perform(ref attempt, path, last), label)!;
                        Assert.That(refused.Message, Is.EqualTo(
                            $"Records {refusal.K1} and {refusal.K2} ({refusal.Schema}) both hold unique index '{refusal.Alias}'; an index value names one record."), label);
                        if (last.Kind != "open") cache?.FlushAsync().GetAwaiter().GetResult();
                        Assert.That(File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>(), Is.EqualTo(before), $"{label}: the file is untouched");
                    }

                    walked++;
                }
                finally
                {
                    cache?.Dispose();
                    Directory.Delete(directory, recursive: true);
                }
            }

            Assert.That(walked, Is.EqualTo(24));
        }
    }
}
