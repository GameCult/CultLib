using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;

return await ProgramMainAsync(args);

static async Task<int> ProgramMainAsync(string[] args)
{
    try
    {
        if (args.Length == 0)
        {
            throw new InvalidOperationException("Expected mode: write | read | rewrite | write-routed <catalog.cc> <run.cc>");
        }

        var mode = args[0];
        if (mode == "write-routed")
        {
            if (args.Length != 3)
                throw new InvalidOperationException("Expected: write-routed <catalog.cc> <run.cc>");
            WriteRouted(args[1], args[2]);
            return 0;
        }

        var options = ParseArgs(args.Skip(1).ToArray());
        var file = RequireArg(options, "file");
        switch (mode)
        {
            case "write":
                await WriteAsync(file, RequireArg(options, "runtime-id"));
                return 0;
            case "read":
                await ReadAsync(file);
                return 0;
            case "rewrite":
                await RewriteAsync(file);
                return 0;
            case "write-deck":
                await WriteDeckAsync(file);
                return 0;
            case "read-deck":
                await ReadDeckAsync(file);
                return 0;
            case "edit-in-place":
                await EditInPlaceAsync(file, RequireArg(options, "kind"));
                return 0;
            default:
                throw new InvalidOperationException($"Unknown mode {mode}.");
        }
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        return 1;
    }
}

static async Task WriteAsync(string file, string runtimeId)
{
    var cache = BuildCache(file);
    await cache.PullAllBackingStoresAsync();
    var note = new CultCacheInteropNote
    {
        DocumentId = $"note:{runtimeId}",
        AuthorRuntimeId = runtimeId,
        Title = $"{runtimeId} wrote a CultCache note",
        Body = "The v1 store format is the contract.",
        Tags = [runtimeId, "csharp", "interop"]
    };
    await cache.AddAsync(note, new CultRecordHandle<CultCacheInteropNote>(new CultRecordKey(note.DocumentId)));
    cache.FlushAllBackingStores();
    WriteJsonLine(note);
}

static async Task ReadAsync(string file)
{
    var cache = BuildCache(file);
    await cache.PullAllBackingStoresAsync();
    var note = cache.AllEntries
        .OfType<CultCacheInteropNote>()
        .FirstOrDefault()
        ?? throw new InvalidOperationException("No cultcache.interop-note records found.");
    WriteJsonLine(note);
}

// Puts the stored note back: the cache stamps it with the registered schema id, whatever id it was stored under.
static async Task RewriteAsync(string file)
{
    var cache = BuildCache(file);
    await cache.PullAllBackingStoresAsync();
    var note = cache.AllEntries.OfType<CultCacheInteropNote>().Single();
    await cache.UpsertAsync(note, new CultRecordHandle<CultCacheInteropNote>(new CultRecordKey(note.DocumentId)));
    cache.FlushAllBackingStores();
    WriteJsonLine(note);
}

// A record with an object list: the cache mints each mark's element id, and every runtime reads it as an ordinary member.
static async Task WriteDeckAsync(string file)
{
    var cache = BuildCache(file);
    await cache.PullAllBackingStoresAsync();
    var deck = new CultCacheInteropDeck
    {
        DocumentId = "deck:csharp",
        Marks = [new CultCacheInteropMark { Label = "twin" }, new CultCacheInteropMark { Label = "twin" }, new CultCacheInteropMark { Label = "other" }]
    };
    await cache.AddAsync(deck, new CultRecordHandle<CultCacheInteropDeck>(new CultRecordKey(deck.DocumentId)));
    cache.FlushAllBackingStores();
    WriteDeckJsonLine(deck);
}

static async Task ReadDeckAsync(string file)
{
    var cache = BuildCache(file);
    await cache.PullAllBackingStoresAsync();
    WriteDeckJsonLine(cache.AllEntries.OfType<CultCacheInteropDeck>().Single());
}

static void WriteDeckJsonLine(CultCacheInteropDeck deck) =>
    Console.Out.WriteLine(JsonSerializer.Serialize(new { documentId = deck.DocumentId, ids = deck.Marks.Select(mark => mark.Id).ToArray() }));

// Edits a stored record in place, as a consumer holding the object would, and writes it: the file must hold the edit, in whatever
// runtime reads it. payload and override go through the public snapshot API (a decoded record's bytes edited, then
// SerializeSnapshot); collection and object edit the document the cache holds and put the same instance back.
static async Task EditInPlaceAsync(string file, string kind)
{
    switch (kind)
    {
        case "payload":
        {
            var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(file));
            OverwriteInPlace(snapshot.Records.Single().Payload, "wrote", "WROTE");
            File.WriteAllBytes(file, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
            break;
        }
        case "override":
        {
            var cache = CultCacheMessagePack.Create(file, new CultCacheOpenOptions { Registry = CultDocumentRegistry.ForTypes(new[] { typeof(CultCacheInteropNote) }) });
            await cache.PullAllBackingStoresAsync();
            var note = cache.AllEntries.OfType<CultCacheInteropNote>().Single();
            await cache.UpsertVariantAsync(
                new CultRecordKey("note:variant"),
                new CultRecordKey(note.DocumentId),
                new[]
                {
                    cache.Override<CultCacheInteropNote>(nameof(CultCacheInteropNote.DocumentId), "note:variant"),
                    cache.Override<CultCacheInteropNote>(nameof(CultCacheInteropNote.Title), "variant title")
                });
            cache.FlushAllBackingStores();
            var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(file));
            var variant = snapshot.Records.Single(record => record.Variant != null).Variant!;
            OverwriteInPlace(variant.Overrides.Single(entry => entry.Path[0].Slot == 3).Value, "variant", "VARIANT");
            File.WriteAllBytes(file, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
            break;
        }
        case "collection":
        {
            var cache = BuildCache(file);
            await cache.PullAllBackingStoresAsync();
            var note = cache.AllEntries.OfType<CultCacheInteropNote>().Single();
            note.Tags[0] = "EDITED";
            await cache.UpsertAsync(note, new CultRecordHandle<CultCacheInteropNote>(new CultRecordKey(note.DocumentId)));
            cache.FlushAllBackingStores();
            break;
        }
        case "object":
        {
            var cache = BuildCache(file);
            await cache.PullAllBackingStoresAsync();
            var deck = cache.AllEntries.OfType<CultCacheInteropDeck>().Single();
            deck.Marks[0].Label = "EDITED";
            await cache.UpsertAsync(deck, new CultRecordHandle<CultCacheInteropDeck>(new CultRecordKey(deck.DocumentId)));
            cache.FlushAllBackingStores();
            break;
        }
        default:
            throw new InvalidOperationException($"Unknown edit kind {kind}.");
    }

    Console.Out.WriteLine(JsonSerializer.Serialize(new { kind }));
}

static void OverwriteInPlace(byte[] bytes, string from, string to)
{
    var was = System.Text.Encoding.UTF8.GetBytes(from);
    var now = System.Text.Encoding.UTF8.GetBytes(to);
    var at = bytes.AsSpan().IndexOf(was);
    if (at < 0 || now.Length != was.Length)
        throw new InvalidOperationException($"Cannot overwrite '{from}' with '{to}' in place.");
    now.CopyTo(bytes, at);
}

// Two routed single-file stores: each file is a complete single-store snapshot holding only its own type.
static void WriteRouted(string catalogFile, string runFile)
{
    using var cache = new CultCache();
    cache.AddBackingStore(new SingleFileMessagePackBackingStore(catalogFile), typeof(CultCacheInteropNote));
    cache.AddBackingStore(new SingleFileMessagePackBackingStore(runFile), typeof(CultCacheInteropRunNote));
    var note = new CultCacheInteropNote
    {
        DocumentId = "note:csharp-routed",
        AuthorRuntimeId = "csharp",
        Title = "csharp wrote a routed catalog note",
        Body = "One home store per document type.",
        Tags = ["csharp", "routed"]
    };
    var runNote = new CultCacheInteropRunNote
    {
        DocumentId = "run-note:csharp-routed",
        AuthorRuntimeId = "csharp",
        Body = "The run store holds only run records."
    };
    cache.UpsertAsync(note, new CultRecordHandle<CultCacheInteropNote>(new CultRecordKey(note.DocumentId)));
    cache.UpsertAsync(runNote, new CultRecordHandle<CultCacheInteropRunNote>(new CultRecordKey(runNote.DocumentId)));
    cache.FlushAllBackingStores();
    WriteJsonLine(note);
}

static CultCache BuildCache(string file)
{
    var cache = new CultCache();
    cache.AddBackingStore(new SingleFileMessagePackBackingStore(file));
    return cache;
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < args.Length; index += 2)
    {
        var token = args[index];
        if (!token.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        if (index + 1 >= args.Length)
        {
            throw new InvalidOperationException($"Missing value for {token}.");
        }

        parsed[token[2..]] = args[index + 1];
    }

    return parsed;
}

static string RequireArg(Dictionary<string, string> options, string name)
{
    if (!options.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"Missing required argument --{name}.");
    }

    return value;
}

static void WriteJsonLine(CultCacheInteropNote note)
{
    Console.Out.WriteLine(JsonSerializer.Serialize(new
    {
        schemaVersion = note.SchemaVersion,
        documentId = note.DocumentId,
        authorRuntimeId = note.AuthorRuntimeId,
        title = note.Title,
        body = note.Body,
        tags = note.Tags
    }));
}

[CultDocument("cultcache.interop-note", "cultcache.interop_note.v1", CompatibleSchemaIds = new[] { "cultcache.interop-note.legacy-id" })]
[MessagePackObject]
public sealed class CultCacheInteropNote
{
    [Key(0)] public string SchemaVersion { get; set; } = "cultcache.interop_note.v1";
    [Key(1)] [CultName] public string DocumentId { get; set; } = string.Empty;
    [Key(2)] public string AuthorRuntimeId { get; set; } = string.Empty;
    [Key(3)] public string Title { get; set; } = string.Empty;
    [Key(4)] public string Body { get; set; } = string.Empty;
    [Key(5)] public string[] Tags { get; set; } = Array.Empty<string>();
}

[CultDocument("cultcache.interop-run-note", "cultcache.interop_run_note.v1")]
[MessagePackObject]
public sealed class CultCacheInteropRunNote
{
    [Key(0)] public string SchemaVersion { get; set; } = "cultcache.interop_run_note.v1";
    [Key(1)] [CultName] public string DocumentId { get; set; } = string.Empty;
    [Key(2)] public string AuthorRuntimeId { get; set; } = string.Empty;
    [Key(3)] public string Body { get; set; } = string.Empty;
}

[CultDocument("cultcache.interop-deck", "cultcache.interop_deck.v1")]
[MessagePackObject]
public sealed class CultCacheInteropDeck
{
    [Key(0)] [CultName] public string DocumentId { get; set; } = string.Empty;
    [Key(1)] public List<CultCacheInteropMark> Marks { get; set; } = new();
}

[MessagePackObject]
public sealed class CultCacheInteropMark
{
    [Key(0)] public string Label { get; set; } = string.Empty;
    [Key(1)] [CultElementId] public string Id { get; set; } = string.Empty;
}
