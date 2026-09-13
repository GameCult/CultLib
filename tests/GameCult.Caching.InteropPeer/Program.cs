using System;
using System.Collections.Generic;
using System.Globalization;
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
            throw new InvalidOperationException("Expected mode: write | read | write-routed <catalog.cc> <run.cc>");
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

[CultDocument("cultcache.interop-note", "cultcache.interop_note.v1")]
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
