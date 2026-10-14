#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests.PyLeavesProbe
{
    [MessagePackObject] public sealed class ProbeInner { [Key(0)] public string N = ""; [Key(1)] public float W; }
    [Union(0, typeof(ProbeCircle))] public interface IProbeShape { }
    [MessagePackObject] public sealed class ProbeCircle : IProbeShape { [Key(0)] public float R; }

    [CultDocument("cultcache.py-leaves-probe", "cultcache.py_leaves_probe.v1")]
    [MessagePackObject]
    public sealed class ProbeDoc
    {
        [Key(0)] [CultName] public string Id = "";
        [Key(1)] public bool Flag;
        [Key(2)] public byte[] Blob = Array.Empty<byte>();
        [Key(3)] public int I;
        [Key(4)] public long L;
        [Key(5)] public float F;
        [Key(6)] public double D;
        [Key(7)] public int? MaybeI;
        [Key(8)] public List<string> Tags = new();
        [Key(9)] public string[] Names = Array.Empty<string>();
        [Key(10)] public object? Any;
        [Key(11)] public ProbeInner? Nested;
        [Key(12)] public List<ProbeInner> Items = new();
        [Key(13)] public IProbeShape? Shape;
        [Key(14)] public float[] Floats = Array.Empty<float>();
        [Key(15)] public float? MaybeF;
    }

    [MessagePackObject] public sealed class S1 { [Key(0)] public string? V; }
    [MessagePackObject] public sealed class B1 { [Key(0)] public bool V; }
    [MessagePackObject] public sealed class Y1 { [Key(0)] public byte[]? V; }
    [MessagePackObject] public sealed class O1 { [Key(0)] public object? V; }
    [MessagePackObject] public sealed class LS1 { [Key(0)] public List<string>? V; }
    [MessagePackObject] public sealed class NI1 { [Key(0)] public int? V; }
    [MessagePackObject] public sealed class R1 { [Key(0)] public ProbeInner? V; }
    [MessagePackObject] public sealed class U1 { [Key(0)] public IProbeShape? V; }
    [MessagePackObject] public sealed class LI1 { [Key(0)] public List<int>? V; }

    public class PyLeavesProbeTests
    {
        static readonly MessagePackSerializerOptions Options = CultDocumentMessagePackSerialization.OptionsFor(typeof(PyLeavesProbeTests).Assembly);

        [Test]
        public void TypeNames()
        {
            var names = typeof(CultCache).Assembly.GetType("GameCult.Caching.CultSchemaTypeNames")!.GetMethod("FromType")!;
            foreach (var t in new[] { typeof(string), typeof(bool), typeof(byte[]), typeof(int), typeof(long), typeof(float), typeof(double),
                         typeof(int?), typeof(bool?), typeof(float?), typeof(List<string>), typeof(string[]), typeof(List<int?>), typeof(object),
                         typeof(ProbeInner), typeof(List<ProbeInner>), typeof(IProbeShape), typeof(float[]), typeof(List<List<string>>),
                         typeof(Dictionary<string, int>), typeof(byte[][]) , typeof(List<byte[]>)})
                TestContext.Out.WriteLine($"TYPENAME {t} => {names.Invoke(null, new object[] { t })}");
        }

        [TestCase("a-leaf-catalog.cc")]
        [TestCase("b-wrong-leaf.cc")]
        public async Task OpenPythonStore(string file)
        {
            var path = Path.Combine("/tmp/pyst", file);
            try
            {
                var cache = new CultCache();
                cache.AddBackingStore(new SingleFileMessagePackBackingStore(path));
                await cache.PullAllBackingStoresAsync();
                var doc = cache.AllEntries.OfType<ProbeDoc>().Single();
                var bytes = MessagePackSerializer.Serialize(doc, Options);
                TestContext.Out.WriteLine($"OPEN {file} OK id={doc.Id} F={doc.F:R} Shape={doc.Shape?.GetType().Name} Any={doc.Any?.GetType().Name} items={doc.Items.Count}");
                TestContext.Out.WriteLine($"CSHARP-REWRITE {file} {Convert.ToHexString(bytes).ToLowerInvariant()}");
            }
            catch (Exception e)
            {
                TestContext.Out.WriteLine($"OPEN {file} REFUSED {e.GetType().Name}: {e.Message.Split('\n')[0]}");
            }
        }

        static readonly string[] Values = { "05", "ff", "cb3ff8000000000000", "ca3fc00000", "c0", "c2", "c3", "a161", "d90161", "c40100", "90", "9101", "91a161", "80", "81a161a162", "920091ca3f800000" };

        static string Try<T>(byte[] p) { try { var v = MessagePackSerializer.Deserialize<T>(p, Options); return "ok"; } catch { return "--"; } }

        [Test]
        public void ScalarReads()
        {
            foreach (var v in Values)
            {
                var p = Convert.FromHexString("91" + v);
                TestContext.Out.WriteLine($"READ {v,-22} string={Try<S1>(p)} bool={Try<B1>(p)} bytes={Try<Y1>(p)} object={Try<O1>(p)} list<string>={Try<LS1>(p)} int?={Try<NI1>(p)} record={Try<R1>(p)} union={Try<U1>(p)} list<int>={Try<LI1>(p)}");
            }
        }
    }
}
