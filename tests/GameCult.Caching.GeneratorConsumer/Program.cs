using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;

// The generator-on twin of ConsumerBuild: this project compiling is the first test, and running it checks that the generated
// formatters are the ones in use and that the bytes are the ones the dynamic formatter wrote.
var options = CultDocumentMessagePackSerialization.OptionsFor(typeof(RefDoc).Assembly);
var target = new CultRecordRef<RefTarget>(new CultRecordKey("p"));
var documents = new (Type Type, object Value)[]
{
    (typeof(MemberDoc), new MemberDoc { Name = "r", Target = target }),
    (typeof(KeyedDoc), new KeyedDoc { Name = "r", Standing = { [target] = 0.5f } }),
    (typeof(RefDoc), new RefDoc { Name = "r", Target = target, Standing = { [target] = 0.5f } }),
};

foreach (var (type, _) in documents)
{
    var formatter = options.Resolver.GetFormatterDynamic(type)!.GetType();
    if (formatter.Assembly.IsDynamic || !(formatter.FullName ?? "").Contains("GeneratedMessagePackResolver"))
        return Fail($"{type.Name} is formatted by {formatter.FullName}, not a generated formatter");
}

var hex = Convert.ToHexString(MessagePackSerializer.Serialize(typeof(RefDoc), documents[2].Value, options));
Console.WriteLine(hex);
if (hex != "93A172A17081A170CA3F000000")
    return Fail("RefDoc payload is " + hex);

var aot = !RuntimeFeature.IsDynamicCodeSupported;
if (aot)
    Console.WriteLine("SKIP ref-keyed dictionary under NativeAOT: AOT cut 3 (map A6)");
foreach (var (type, value) in documents)
{
    if (aot && type != typeof(MemberDoc))
        continue;
    var path = Path.Combine(Path.GetTempPath(), "generator-consumer-" + Guid.NewGuid().ToString("N") + ".cc");
    try
    {
        var registry = CultDocumentRegistry.ForTypes(new[] { type, typeof(RefTarget) });
        using (var cache = new CultCache(registry))
        {
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(path));
            cache.Upsert(type, value, new CultRecordKey("k"));
            cache.FlushAllBackingStores();
        }
        using var reopened = new CultCache(registry);
        reopened.AddBackingStore(new SingleFileMessagePackBackingStore(path));
        reopened.PullAllBackingStoresAsync().GetAwaiter().GetResult();
        var read = reopened.Get(new CultRecordKey("k"));
        if (read == null || !Convert.ToHexString(MessagePackSerializer.Serialize(type, read, options)).Equals(Convert.ToHexString(MessagePackSerializer.Serialize(type, value, options))))
            return Fail($"{type.Name} did not round-trip through the store");
    }
    finally
    {
        File.Delete(path);
    }
}
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

[CultDocument("tests.generator_target", "tests.generator_target.v1")]
[MessagePackObject]
public sealed class RefTarget
{
    [Key(0)] [CultName] public string Name { get; set; } = string.Empty;
}

[CultDocument("tests.generator_member", "tests.generator_member.v1")]
[MessagePackObject]
public sealed class MemberDoc
{
    [Key(0)] [CultName] public string Name { get; set; } = string.Empty;
    [Key(1)] public CultRecordRef<RefTarget> Target { get; set; }
}

[CultDocument("tests.generator_keyed", "tests.generator_keyed.v1")]
[MessagePackObject]
public sealed class KeyedDoc
{
    [Key(0)] [CultName] public string Name { get; set; } = string.Empty;
    [Key(1)] public Dictionary<CultRecordRef<RefTarget>, float> Standing { get; set; } = new();
}

[CultDocument("tests.generator_both", "tests.generator_both.v1")]
[MessagePackObject]
public sealed class RefDoc
{
    [Key(0)] [CultName] public string Name { get; set; } = string.Empty;
    [Key(1)] public CultRecordRef<RefTarget> Target { get; set; }
    [Key(2)] public Dictionary<CultRecordRef<RefTarget>, float> Standing { get; set; } = new();
}
