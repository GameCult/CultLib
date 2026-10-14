using System.Collections.Generic;
using GameCult.Caching;
using MessagePack;

// RefKeyedDictionaryCompilesInAConsumer: this project building is the test.
System.Console.WriteLine(new ConsumerFaction().Standing.Count);

// An outside store kind versions its foreign records: this project has no InternalsVisibleTo, so building and running it pins that
// Foreign(record, catalog, version) is all a store kind outside the library needs for ExpectUnchanged to hold over them.
using var outside = new CultCache(CultDocumentRegistry.ForTypes(new[] { typeof(ConsumerFaction) }));
outside.AddBackingStore(new OutsideStore(), typeof(ConsumerFaction));
var committed = outside.Commit(batch =>
{
    batch.ExpectUnchanged();
    batch.Upsert(new ConsumerFaction { Name = "a" }, new CultRecordHandle<ConsumerFaction>(new CultRecordKey("a")));
});
if (!committed)
    return 1;
return 0;

// Lists one record under an id no type claims, at version "v1", and commits only when the conditions hold over it.
public sealed class OutsideStore : CacheBackingStore
{
    private static readonly CultSchemaCatalogEntry Entry = new() { SchemaId = "outside.unclaimed", SchemaName = "outside.unclaimed" };

    public override void PullAll()
    {
        var record = new CultPersistedRecord { Key = "w", SchemaId = Entry.SchemaId, StoredAt = "t" };
        SetForeignRecords(new[] { Foreign(record, new[] { Entry }, "v1")! });
    }

    public override void Push(CultStoredDocument entry) { }

    public override void Delete(CultStoredDocument entry) { }

    public override void PushAll() { }

    public override CultCommitOutcome CommitBatch(CultCommitRequest request, bool wait) =>
        request.ConditionsHold(new Dictionary<string, string> { ["w"] = "v1" }, Entries.Values, ForeignRecords)
            ? CultCommitOutcome.Committed
            : CultCommitOutcome.Mismatch;
}

[CultDocument("tests.consumer_faction", "tests.consumer_faction.v1")]
[MessagePackObject]
public sealed class ConsumerFaction
{
    [Key(0)] [CultName] public string Name { get; set; } = string.Empty;
    [Key(1)] public Dictionary<CultRecordRef<ConsumerFaction>, float> Standing { get; set; } = new();
}
