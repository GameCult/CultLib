using System.Collections.Generic;
using GameCult.Caching;
using MessagePack;

// RefKeyedDictionaryCompilesInAConsumer: this project building is the test.
System.Console.WriteLine(new ConsumerFaction().Standing.Count);

[CultDocument("tests.consumer_faction", "tests.consumer_faction.v1")]
[MessagePackObject]
public sealed class ConsumerFaction
{
    [Key(0)] [CultName] public string Name { get; set; } = string.Empty;
    [Key(1)] public Dictionary<CultRecordRef<ConsumerFaction>, float> Standing { get; set; } = new();
}
