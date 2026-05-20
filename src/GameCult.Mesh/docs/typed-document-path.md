# Typed Document Path

This is the smallest useful path for a typed document moving from local
`CultCache` into the CultNet/CultMesh runtime story.

It is not a new abstraction. It is the existing machine with the labels put
back on.

## Ownership

- `GameCult.Caching` / CultCache owns the typed document, record key, local
  indexes, and persistence shape.
- `GameCult.Networking` / CultNet owns the raw schema-v0 document envelope used
  to move that typed document across runtimes.
- `GameCult.Mesh` / CultMesh owns the runtime-facing node and distributed
  database surface a game watches and writes against.

## End-to-End Sample

Define a typed document once. CultCache owns this shape.

```csharp
using GameCult.Caching;
using MessagePack;

[CultDocument("sample.note", "sample.note.v0")]
[MessagePackObject]
public sealed class SampleNote
{
    [Key(0)] public string SchemaVersion { get; set; } = "sample.note.v0";
    [Key(1)] [CultName] public string DocumentId { get; set; } = string.Empty;
    [Key(2)] public string Title { get; set; } = string.Empty;
    [Key(3)] public string Body { get; set; } = string.Empty;
}
```

Put the document in a local cache and register the payload codec CultNet will
use on the wire.

```csharp
using GameCult.Caching;
using GameCult.Networking;
using MessagePack;

var cache = new CultCache();
var key = new CultRecordKey("note:intro");
var note = new SampleNote
{
    DocumentId = key.Value,
    Title = "hello",
    Body = "typed locally first"
};

await cache.AddAsync(note, new CultRecordHandle<SampleNote>(key));

var documents = new CultNetDocumentRegistry(cache.Registry)
    .Register(CultNetDocumentBinding.ForDocument<SampleNote>(
        cache.Registry,
        payloadSerializer: value => MessagePackSerializer.Serialize(value, CultNetSchemaMessageSerialization.Options),
        payloadDeserializer: payload => MessagePackSerializer.Deserialize<SampleNote>(payload, CultNetSchemaMessageSerialization.Options)));
```

Create a raw CultNet put message when the document needs to cross runtimes.
This is where wire provenance stays visible.

```csharp
var put = documents.CreateRawDocumentPutMessage(
    "put-1",
    new CultRecordHandle<SampleNote>(key),
    note,
    new CultNetDocumentMessageOptions
    {
        SourceRuntimeId = "editor-1",
        SourceAgentId = "libby",
        SourceRole = "tool",
        Tags = ["local-cache", "intro"]
    });
```

`put.Document` is the raw envelope. It still carries:

- `SchemaId`
- `RecordKey`
- `StoredAt`
- `SourceRuntimeId`
- `SourceAgentId`
- `SourceRole`
- `Tags`

Apply that raw message on another runtime and the typed document lands back in
CultCache.

```csharp
var remoteCache = new CultCache();
var remoteDocuments = new CultNetDocumentRegistry(remoteCache.Registry)
    .Register(CultNetDocumentBinding.ForDocument<SampleNote>(
        remoteCache.Registry,
        payloadSerializer: value => MessagePackSerializer.Serialize(value, CultNetSchemaMessageSerialization.Options),
        payloadDeserializer: payload => MessagePackSerializer.Deserialize<SampleNote>(payload, CultNetSchemaMessageSerialization.Options)));

var remoteNote = await remoteDocuments.ApplyRawDocumentPutMessageAsync<SampleNote>(remoteCache, put);
```

When the runtime is running through CultMesh, pass the same document registry
into the node's database options. CultMesh then exposes the document through the
distributed database surface instead of making game code talk to raw wire
messages directly.

```csharp
using GameCult.Mesh;

using var node = await CultMesh.CreateNodeAsync("world.ccmp", new CultMeshNodeOptions
{
    DatabaseOptions = new CultNetDatabaseOptions
    {
        RuntimeId = "runtime-a",
        DocumentRegistry = documents
    }
});

using var subscription = node.Database.WatchRecord<SampleNote>(key)
    .Subscribe(change => Console.WriteLine(change.Document?.Title));
```

That is the stack:

1. CultCache owns the typed record.
2. CultNet turns it into an explicit raw document message with visible
   provenance.
3. CultMesh wraps the database/runtime surface that watches or commits those
   typed records in a running node.

## Repo-Local Evidence

This path is already exercised in repo code; it was just hiding in the wrong
places.

- `tests/GameCult.Networking.InteropPeer/Program.cs` shows a typed interop note
  being added to `CultCache`, wrapped by `CultNetDocumentRegistry`, moved by raw
  snapshot/put messages, and rehydrated on the other side.
- `tests/GameCult.Networking.Tests/NetworkingTests.cs` covers raw put/snapshot
  application, database watch behavior, prediction, shard log replay, and
  `CultMesh.CreateNodeAsync(...)` with a supplied `CultNetDocumentRegistry`.
