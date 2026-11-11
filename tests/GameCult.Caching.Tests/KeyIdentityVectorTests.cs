#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // tests/vectors/key-identity: a cache holds one record per key whatever its type, and a store holding a record no registered
    // type claims keeps it (variants:ruling:cache-refuses-retype, write-ownership-by-written-type, unheld-removal-refused-at-commit).
    // The C# reference is the only writer: under CULTCACHE_WRITE_VECTORS=1 the rows are run and what the reference answers is
    // written; every other run walks the committed rows against the reference and compares the files byte for byte.
    public class KeyIdentityVectorTests
    {
        private const string Vector = "key-identity";

        private static readonly Type Alpha = Kind("KiAlpha", "tests.ki_alpha");
        private static readonly Type Beta = Kind("KiBeta", "tests.ki_beta");
        private static readonly Type Gamma = Kind("KiGamma", "tests.ki_gamma");

        private static Type Kind(string type, string schema) =>
            EmittedDocumentTypes.Emit(type, schema, schema + ".v1", new[] { new EmittedDocumentTypes.Field("Text", typeof(string), 0) });

        private static readonly Dictionary<string, Type> Types = new() { ["alpha"] = Alpha, ["beta"] = Beta, ["gamma"] = Gamma };

        private sealed record Op(string Kind, string? File = null, string[]? Types = null, string? Type = null, string? Key = null, string? Text = null);

        private sealed record Row(string Label, string[] Registered, Op[] Ops);

        // [refusal key or nil, held [type, key, text], file [key, schema name] or nil, foreign keys, kept foreign keys], all sorted.
        private sealed record Expect(string? Refusal, string[][] Held, string[][]? File, string[] Foreign, string[] Kept);

        private static Op Open(string? file) => new("open", file);
        private static Op Attach(string file, params string[] home) => new("attach", file, home);
        private static Op Reopen() => new("reopen");
        private static Op Put(string type, string key, string text) => new("put", Type: type, Key: key, Text: text);
        private static Op Delete(string type, string key) => new("delete", Type: type, Key: key);

        private static readonly string[] AB = { "alpha", "beta" };
        private static readonly string[] A = { "alpha" };

        // Only the ops are typed here; every verdict is what the reference answers.
        private static readonly Row[] Table =
        {
            new("retype-refused-held-in-file", AB, new[] { Open(null), Put("alpha", "k", "a"), Put("beta", "k", "b") }),
            new("retype-by-remove-then-add", AB, new[] { Open(null), Put("alpha", "k", "a"), Delete("alpha", "k"), Put("beta", "k", "b") }),
            new("retype-refused-memory-cache", AB, new[] { Open("memory"), Put("alpha", "k", "a"), Put("beta", "k", "b") }),
            new("same-type-replaces", A, new[] { Open(null), Put("alpha", "k", "a"), Put("alpha", "k", "b") }),
            new("retype-refused-after-reload", AB, new[] { Open(null), Put("alpha", "k", "a"), Reopen(), Put("beta", "k", "b") }),
            new("foreign-loads", A, new[] { Open("foreign-store.cc") }),
            new("foreign-kept-by-a-write", A, new[] { Open("foreign-store.cc"), Put("alpha", "m", "m") }),
            new("foreign-key-write-refused", A, new[] { Open("foreign-store.cc"), Put("alpha", "k", "x") }),
            new("foreign-key-delete-refused", A, new[] { Open("foreign-store.cc"), Delete("alpha", "k") }),
            new("foreign-claimed-by-a-later-registration", new[] { "alpha", "gamma" }, new[] { Open("foreign-store.cc") }),
            new("key-in-two-stores-refused", AB, new[] { Open("memory"), Attach("catalog-store.cc", "alpha"), Attach("run-store.cc", "beta") }),
        };

        // The stores C# writes: [file name, registered types, (type, key, text) records]. Never typed by a runtime.
        private static readonly (string File, string[] Registered, (string Type, string Key, string Text)[] Records)[] Stores =
        {
            ("foreign-store.cc", new[] { "alpha", "gamma" }, new[] { ("alpha", "j", "a"), ("gamma", "k", "g") }),
            ("catalog-store.cc", new[] { "alpha" }, new[] { ("alpha", "k", "a") }),
            ("run-store.cc", new[] { "beta" }, new[] { ("beta", "k", "b") }),
        };

        private static CultDocumentRegistry RegistryOf(IEnumerable<string> names) => CultDocumentRegistry.ForTypes(names.Select(name => Types[name]).ToArray());

        private static object Doc(string type, string text) => EmittedDocumentTypes.New(Types[type], ("Text", text));

        private static byte[] StoreBytes(string[] registered, (string Type, string Key, string Text)[] records)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"cultlib-kiv-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "store.cc");
                using (var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = RegistryOf(registered), StoreFlushOnDispose = true }))
                {
                    cache.Clock = () => VectorFiles.StoredAt;
                    foreach (var record in records)
                        cache.UpsertAsync(Types[record.Type], Doc(record.Type, record.Text), new CultRecordKey(record.Key)).GetAwaiter().GetResult();
                    cache.FlushAsync().GetAwaiter().GetResult();
                }

                return File.ReadAllBytes(path);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static CultPersistedStoreSnapshot Snapshot(byte[] bytes) => CultDocumentMessagePackSerialization.DeserializeSnapshot(bytes);

        private static string Name(CultPersistedStoreSnapshot snapshot, CultPersistedRecord record) =>
            snapshot.SchemaCatalog.Single(entry => entry.SchemaId == record.SchemaId).SchemaName;

        private static string Short(string schemaName) => Types.Single(pair => pair.Key == schemaName.Replace("tests.ki_", "")).Key;

        private static string RecordBytes(CultPersistedRecord record) => $"{record.SchemaId}|{record.StoredAt}|{Convert.ToBase64String(record.Payload)}";

        // Runs the ops and returns what the reference answers. Every op but the last must succeed; the last may refuse.
        private static Expect Observe(Row row)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"cultlib-kiw-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var registry = RegistryOf(row.Registered);
            string? path = null;
            byte[]? before = null;
            CultCache? cache = null;
            try
            {
                Exception? refusal = null;
                string? refusedKey = null;
                for (var i = 0; i < row.Ops.Length; i++)
                {
                    var op = row.Ops[i];
                    try
                    {
                        switch (op.Kind)
                        {
                            case "open" or "reopen":
                                cache?.Dispose();
                                if (op.Kind == "open" && op.File == "memory")
                                {
                                    cache = new CultCache(registry, CultCacheMessagePack.CreateCodec(registry));
                                    break;
                                }

                                if (op.Kind == "open")
                                {
                                    path = Path.Combine(directory, "store.cc");
                                    if (op.File != null) File.WriteAllBytes(path, VectorFiles.Committed(Vector, op.File));
                                    before = File.Exists(path) ? File.ReadAllBytes(path) : null;
                                }

                                cache = CultCacheMessagePack.Create(path!, new CultCacheOpenOptions { Registry = registry, StoreFlushOnDispose = true });
                                break;
                            case "attach":
                            {
                                var attached = Path.Combine(directory, op.File!);
                                File.WriteAllBytes(attached, VectorFiles.Committed(Vector, op.File!));
                                var held = cache!.AllStoredDocuments.Select(stored => stored.Key.Value).ToHashSet(StringComparer.Ordinal);
                                var shared = Snapshot(File.ReadAllBytes(attached)).Records.Select(record => record.Key).Where(held.Contains).ToArray();
                                try
                                {
                                    cache.AddBackingStore(new SingleFileMessagePackBackingStore(attached), op.Types!.Select(name => Types[name]).ToArray());
                                    cache.PullAllBackingStoresAsync().GetAwaiter().GetResult();
                                }
                                catch (InvalidOperationException) when (shared.Length == 1)
                                {
                                    refusedKey = shared[0];
                                    Assert.That(File.ReadAllBytes(attached), Is.EqualTo(VectorFiles.Committed(Vector, op.File!)), "a refused attach leaves the store as it was");
                                    throw;
                                }

                                break;
                            }
                            case "put":
                                cache!.Commit(batch => batch.Upsert(Types[op.Type!], Doc(op.Type!, op.Text!), new CultRecordKey(op.Key!)));
                                break;
                            case "delete":
                                cache!.Commit(batch => batch.Remove(new CultRecordKey(op.Key!)));
                                break;
                            default:
                                throw new InvalidOperationException(op.Kind);
                        }
                    }
                    catch (Exception failure) when (i == row.Ops.Length - 1 && (failure is CultSchemaConflictException || failure is InvalidOperationException))
                    {
                        refusal = failure;
                    }
                }

                if (refusal is CultSchemaConflictException conflict)
                {
                    refusedKey = conflict.RecordKey;
                    Assert.That(row.Ops[^1].Key, Is.EqualTo(conflict.RecordKey), $"{row.Label}: the refusal names the key written or removed");
                }

                if (refusal != null)
                    Assert.That(refusal.Message, Does.Contain(refusedKey ?? "").And.Not.Contain("\"\""), $"{row.Label}: the refusal names the key");

                var heldNow = cache!.AllStoredDocuments
                    .Select(stored => new[] { Short(stored.Descriptor.SchemaName), stored.Key.Value, (string)EmittedDocumentTypes.Read(stored.Document, "Text")! })
                    .OrderBy(entry => entry[0], StringComparer.Ordinal).ThenBy(entry => entry[1], StringComparer.Ordinal).ToArray();
                var foreign = cache.BackingStores.SelectMany(store => store.ForeignRecords).Select(record => record.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray();

                string[][]? file = null;
                var kept = Array.Empty<string>();
                if (path != null)
                {
                    cache.FlushAsync().GetAwaiter().GetResult();
                    var after = Snapshot(File.ReadAllBytes(path));
                    file = after.Records.OrderBy(record => record.Key, StringComparer.Ordinal).Select(record => new[] { record.Key, Name(after, record) }).ToArray();
                    var start = before == null ? null : Snapshot(before);
                    kept = foreign.Where(key =>
                    {
                        var original = start?.Records.SingleOrDefault(record => record.Key == key);
                        return original != null && RecordBytes(original) == RecordBytes(after.Records.Single(record => record.Key == key));
                    }).ToArray();
                }

                return new Expect(refusedKey, heldNow, file, foreign, kept);
            }
            finally
            {
                cache?.Dispose();
                Directory.Delete(directory, recursive: true);
            }
        }

        // ---- rows.msgpack: [label, registered, ops, expect] ----

        private static void WriteStrings(ref MessagePackWriter writer, IEnumerable<string?> values)
        {
            var list = values.ToArray();
            writer.WriteArrayHeader(list.Length);
            foreach (var value in list)
                if (value == null) writer.WriteNil(); else writer.Write(value);
        }

        private static void WriteRows(ref MessagePackWriter writer, Row row, Expect expect)
        {
            writer.WriteArrayHeader(4);
            writer.Write(row.Label);
            WriteStrings(ref writer, row.Registered);
            writer.WriteArrayHeader(row.Ops.Length);
            foreach (var op in row.Ops)
            {
                var fields = op.Kind switch
                {
                    "open" => new object?[] { op.Kind, op.File },
                    "attach" => new object?[] { op.Kind, op.File, op.Types },
                    "reopen" => new object?[] { op.Kind },
                    "put" => new object?[] { op.Kind, op.Type, op.Key, op.Text },
                    _ => new object?[] { op.Kind, op.Type, op.Key },
                };
                writer.WriteArrayHeader(fields.Length);
                foreach (var field in fields)
                    switch (field)
                    {
                        case null: writer.WriteNil(); break;
                        case string[] list: WriteStrings(ref writer, list); break;
                        default: writer.Write((string)field); break;
                    }
            }

            writer.WriteArrayHeader(5);
            if (expect.Refusal == null) writer.WriteNil(); else WriteStrings(ref writer, new[] { expect.Refusal });
            writer.WriteArrayHeader(expect.Held.Length);
            foreach (var held in expect.Held) WriteStrings(ref writer, held);
            if (expect.File == null) writer.WriteNil();
            else
            {
                writer.WriteArrayHeader(expect.File.Length);
                foreach (var record in expect.File) WriteStrings(ref writer, record);
            }

            WriteStrings(ref writer, expect.Foreign);
            WriteStrings(ref writer, expect.Kept);
        }

        private static byte[] RowsFile()
        {
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            writer.WriteArrayHeader(Table.Length);
            foreach (var row in Table)
                WriteRows(ref writer, row, Observe(row));
            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }

        private static string[] ReadStrings(ref MessagePackReader reader)
        {
            var values = new string[reader.ReadArrayHeader()];
            for (var i = 0; i < values.Length; i++) values[i] = reader.ReadString()!;
            return values;
        }

        private static (Row Row, Expect Expect) ReadRow(ref MessagePackReader reader)
        {
            Assert.That(reader.ReadArrayHeader(), Is.EqualTo(4));
            var label = reader.ReadString()!;
            var registered = ReadStrings(ref reader);
            var ops = new Op[reader.ReadArrayHeader()];
            for (var i = 0; i < ops.Length; i++)
            {
                var length = reader.ReadArrayHeader();
                var kind = reader.ReadString()!;
                ops[i] = kind switch
                {
                    "open" => new Op(kind, reader.TryReadNil() ? null : reader.ReadString()),
                    "attach" => new Op(kind, reader.ReadString(), ReadStrings(ref reader)),
                    "reopen" => new Op(kind),
                    "put" => new Op(kind, Type: reader.ReadString(), Key: reader.ReadString(), Text: reader.ReadString()),
                    "delete" => new Op(kind, Type: reader.ReadString(), Key: reader.ReadString()),
                    _ => throw new InvalidOperationException(kind),
                };
                Assert.That(length, Is.EqualTo(kind switch { "reopen" => 1, "open" => 2, "delete" => 3, "attach" => 3, _ => 4 }), $"{label}: op {kind}");
            }

            Assert.That(reader.ReadArrayHeader(), Is.EqualTo(5));
            var refusal = reader.TryReadNil() ? null : ReadStrings(ref reader).Single();
            var held = new string[reader.ReadArrayHeader()][];
            for (var i = 0; i < held.Length; i++) held[i] = ReadStrings(ref reader);
            string[][]? file = null;
            if (!reader.TryReadNil())
            {
                file = new string[reader.ReadArrayHeader()][];
                for (var i = 0; i < file.Length; i++) file[i] = ReadStrings(ref reader);
            }

            var foreign = ReadStrings(ref reader);
            var kept = ReadStrings(ref reader);
            return (new Row(label, registered, ops), new Expect(refusal, held, file, foreign, kept));
        }

        private static string Render(Expect expect) =>
            $"refusal={expect.Refusal ?? "-"} held={string.Join(";", expect.Held.Select(h => string.Join(",", h)))} " +
            $"file={(expect.File == null ? "-" : string.Join(";", expect.File.Select(f => string.Join(",", f))))} " +
            $"foreign={string.Join(",", expect.Foreign)} kept={string.Join(",", expect.Kept)}";

        [Test]
        public void KeyIdentityRowsHoldTheirVerdicts()
        {
            foreach (var store in Stores)
                VectorFiles.Check(Vector, store.File, StoreBytes(store.Registered, store.Records));
            VectorFiles.Check(Vector, "rows.msgpack", RowsFile());

            var reader = new MessagePackReader(VectorFiles.Committed(Vector, "rows.msgpack"));
            var count = reader.ReadArrayHeader();
            Assert.That(count, Is.EqualTo(Table.Length));
            var walked = 0;
            var failed = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var (row, expect) = ReadRow(ref reader);
                var observed = Observe(row);
                if (Render(observed) == Render(expect)) walked++;
                else failed.Add($"{row.Label}: committed {Render(expect)} but the reference answers {Render(observed)}");
            }

            Assert.That(failed, Is.Empty);
            Assert.That(walked, Is.EqualTo(11), "rows walked");
        }
    }
}
