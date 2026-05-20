# Durable Node Quickstart

This is the smallest honest path from a durable local typed document to a
running CultMesh node.

It uses the public surface only:

- `CultMesh.CreateNodeAsync(...)` to open or create the durable node
- `node.Database.PutAsync(...)` and `node.Database.GetAsync(...)` for typed reads
  and writes
- `node.FlushAsync()` to persist the current local state before shutdown

## Ownership Split

- CultCache owns the local durable state file, typed document registry, record
  keys, and persistence metadata.
- CultNet owns the document registry that turns typed CultCache documents into
  schema-v0 wire payloads when the node has to replicate them.
- CultMesh owns the newcomer-facing node and database illusion your game code
  talks to.

That means the game writes to `node.Database`, CultMesh routes through CultNet,
and the durable local snapshot still belongs to CultCache.

## Sample

Define one typed document. CultCache owns this model and its schema identity.

```csharp
using GameCult.Caching;
using MessagePack;

[CultDocument("sample.mesh_note", "sample.mesh_note.v0")]
[MessagePackObject]
public sealed class MeshNote
{
    [Key(0)]
    [CultName]
    public string NoteId { get; set; } = string.Empty;

    [Key(1)]
    public string Body { get; set; } = string.Empty;
}
```

Open a durable node, write the document through CultMesh, flush it, then reopen
the same node and read the document back.

```csharp
using GameCult.Caching;
using GameCult.Mesh;
using GameCult.Networking;

var key = new CultRecordKey("note:intro");
var documents = new CultNetDocumentRegistry()
    .Register(CultNetDocumentBinding.ForDocument<MeshNote>());

using (var node = await CultMesh.CreateNodeAsync("world.ccmp", new CultMeshNodeOptions
{
    DatabaseOptions = new CultNetDatabaseOptions
    {
        RuntimeId = "quickstart-local",
        DocumentRegistry = documents
    }
}))
{
    await node.Database.PutAsync(key, new MeshNote
    {
        NoteId = key.Value,
        Body = "hello from a durable CultMesh node"
    });

    var live = await node.Database.GetAsync<MeshNote>(key);
    Console.WriteLine(live?.Body);

    await node.FlushAsync();
}

using (var reopened = await CultMesh.CreateNodeAsync("world.ccmp", new CultMeshNodeOptions
{
    DatabaseOptions = new CultNetDatabaseOptions
    {
        RuntimeId = "quickstart-local",
        DocumentRegistry = documents
    }
}))
{
    var stored = await reopened.Database.GetAsync<MeshNote>(key);
    Console.WriteLine(stored?.Body);
}
```

What happened:

1. `CultMesh.CreateNodeAsync("world.ccmp", ...)` opened a durable CultCache
   snapshot file and wrapped it in a CultMesh node.
2. `node.Database.PutAsync(...)` wrote a typed document through the public
   CultMesh database surface.
3. CultNet kept the document registry needed for raw replication and
   subscriptions, but the sample did not need to drop into wire messages.
4. `node.FlushAsync()` persisted the local CultCache snapshot so the reopened
   node could read the same typed document back.

## Which Layer Owns What?

If you peel this path apart, the authority split stays simple:

- CultCache: `world.ccmp`, schema ids, record keys, typed document payloads,
  local indexes, flush/load
- CultNet: `CultNetDocumentRegistry`, raw document puts/snapshots,
  subscriptions, shard routing, transport-facing replication
- CultMesh: `CultMeshNode`, `node.Database`, and the "treat this as one
  reactive database" surface

If you need the explicit raw-envelope version of the same handoff, see
[typed-document-path.md](typed-document-path.md).
