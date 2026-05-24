using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Mesh;
using GameCult.Networking;
using MessagePack;

return await ProgramMainAsync(args);

static async Task<int> ProgramMainAsync(string[] args)
{
    try
    {
        if (args.Length == 0)
        {
            throw new InvalidOperationException("Expected mode: write | read");
        }

        var mode = args[0];
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
    using var node = await CreateNodeAsync(file, runtimeId);
    var note = new CultMeshInteropNote
    {
        DocumentId = $"note:{runtimeId}",
        AuthorRuntimeId = runtimeId,
        VerseId = "verse:interop",
        Body = "CultMesh local node state uses the shared CultCache wire store.",
        Tags = [runtimeId, "csharp", "interop", "cultmesh"]
    };
    await node.Database.PutAsync(new CultRecordKey(note.DocumentId), note);
    await node.FlushAsync();
    WriteJsonLine(note);
}

static async Task ReadAsync(string file)
{
    using var node = await CreateNodeAsync(file, "csharp-reader");
    var note = node.Cache.AllEntries
        .OfType<CultMeshInteropNote>()
        .FirstOrDefault()
        ?? throw new InvalidOperationException("No cultmesh.interop-note records found.");
    WriteJsonLine(note);
}

static async Task<CultMeshNode> CreateNodeAsync(string file, string runtimeId)
{
    var registry = new CultDocumentRegistry();
    registry.GetRequired<CultMeshInteropNote>();
    var documentRegistry = new CultNetDocumentRegistry(registry);
    return await CultMesh.CreateNodeAsync(file, new CultMeshNodeOptions
    {
        StartServer = false,
        CacheOptions = new CultCacheOpenOptions
        {
            Registry = registry
        },
        DatabaseOptions = new CultNetDatabaseOptions
        {
            RuntimeId = runtimeId,
            DocumentRegistry = documentRegistry
        }
    });
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

static void WriteJsonLine(CultMeshInteropNote note)
{
    Console.Out.WriteLine(JsonSerializer.Serialize(new
    {
        schemaVersion = note.SchemaVersion,
        documentId = note.DocumentId,
        authorRuntimeId = note.AuthorRuntimeId,
        verseId = note.VerseId,
        body = note.Body,
        tags = note.Tags
    }));
}

[CultDocument("cultmesh.interop-note", "cultmesh.interop_note.v0")]
[MessagePackObject]
public sealed class CultMeshInteropNote
{
    [Key(0)] public string SchemaVersion { get; set; } = "cultmesh.interop_note.v0";
    [Key(1)] [CultName] public string DocumentId { get; set; } = string.Empty;
    [Key(2)] public string AuthorRuntimeId { get; set; } = string.Empty;
    [Key(3)] public string VerseId { get; set; } = string.Empty;
    [Key(4)] public string Body { get; set; } = string.Empty;
    [Key(5)] public string[] Tags { get; set; } = Array.Empty<string>();
}
