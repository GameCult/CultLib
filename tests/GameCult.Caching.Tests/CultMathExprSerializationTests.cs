using System;
using System.Collections.Generic;
using System.Linq;
using CultMath.Expr;
using FluentAssertions;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    /// <summary>
    /// The MessagePack shape of <c>CultMath.Expr.ExprGraph</c> (<c>CultMathExprResolver</c>, in the default chain): a
    /// consumer record with a graph field needs no registration, a graph encodes to canonical bytes and decodes to a
    /// graph that evaluates bit for bit the same, and decode runs the one validator so a broken encoding is refused
    /// whole, naming its rule and never echoing what it was given.
    /// </summary>
    public sealed class CultMathExprSerializationTests
    {
        private static readonly MessagePackSerializerOptions Options = CultDocumentMessagePackSerialization.Options;

        private const string Canary = "CANARY_NAME";

        [CultDocument("tests.expr_holder", "tests.expr_holder.v1")]
        [MessagePackObject(AllowPrivate = true)]
        [CultGlobal]
        internal sealed class ExprHolder
        {
            [Key(0)] public ExprGraph Graph = default!;
            [Key(1)] public float Scale;
        }

        private static ExprGraph Sample()
        {
            var b = new ExprBuilder();
            var x = b.Input("x");
            var z = b.Input("z");
            var k = b.Param("k", 1.5f);
            var cell = b.Call("cellular3", x, z, k);
            var field = b.Call("snoise2", b.Call("mul", x, k), z);
            b.Output("o", b.Call("add", field, b.Call("sub", b.Pick(cell, 7), b.Const(-0f))));
            return b.Build();
        }

        private static byte[] Encode(ExprGraph graph) => MessagePackSerializer.Serialize(graph, Options);

        private static ExprGraph Decode(byte[] bytes) => MessagePackSerializer.Deserialize<ExprGraph>(bytes, Options);

        private static float[] Evaluate(ExprGraph graph, float[] x, float[] z)
        {
            var outputs = new float[x.Length * graph.OutputNames.Count];
            ExprEvaluator.Evaluate(graph, ExprBindings.Bind(graph), x.Concat(z).ToArray(), outputs);
            return outputs;
        }

        [Test]
        public void ConsumerRecordRoundTrips()
        {
            var graph = Sample();
            var record = new ExprHolder { Graph = graph, Scale = 2f };

            var bytes = MessagePackSerializer.Serialize(record, Options);
            var decoded = MessagePackSerializer.Deserialize<ExprHolder>(bytes, Options);

            decoded.Scale.Should().Be(2f);
            Encode(decoded.Graph).Should().Equal(Encode(graph));
            var rng = new Random(11);
            var x = Enumerable.Range(0, 256).Select(_ => (float)(rng.NextDouble() * 20 - 10)).ToArray();
            var z = Enumerable.Range(0, 256).Select(_ => (float)(rng.NextDouble() * 20 - 10)).ToArray();
            var expected = Evaluate(graph, x, z);
            var actual = Evaluate(decoded.Graph, x, z);
            actual.Select(BitConverter.SingleToInt32Bits).Should().Equal(expected.Select(BitConverter.SingleToInt32Bits));
        }

        [Test]
        public void BuildIsCanonical_TheSameCallsEncodeToTheSameBytes()
        {
            Encode(Sample()).Should().Equal(Encode(Sample()));

            var b = new ExprBuilder();
            var x = b.Input("x");
            var k = b.Param("k", 1.5f);
            b.Output("o", b.Call("add", x, k));
            MessagePackSerializer.ConvertToJson(Encode(b.Build()), Options).Should().Be(
                "[1,[\"x\"],[[\"k\",1069547520]],[[\"o\",2]],[[\"input\",[],0],[\"param\",[],0],[\"add\",[0,1],0]]]");

            var dead = new ExprBuilder();
            var dx = dead.Input("x");
            var dk = dead.Param("k", 1.5f);
            dead.Call("sin", dx);
            dead.Output("o", dead.Call("add", dx, dk));
            Encode(dead.Build()).Should().Equal(Encode(b.Build()));
        }

        [Test]
        public void ConstantsKeepTheirBits()
        {
            const int nanBits = 0x7FC12345;
            var b = new ExprBuilder();
            b.Output("a", b.Const(-0f));
            b.Output("b", b.Const(BitConverter.Int32BitsToSingle(nanBits)));
            b.Output("c", b.Const(BitConverter.Int32BitsToSingle(1)));
            b.Output("d", b.Param("p", BitConverter.Int32BitsToSingle(nanBits)));
            var graph = b.Build();

            var decoded = Decode(Encode(graph));

            Encode(decoded).Should().Equal(Encode(graph));
            decoded.Nodes.Where(n => n.Kind == ExprNodeKind.Const).Select(n => n.Payload)
                .Should().Equal(int.MinValue, nanBits, 1);
            decoded.ParamDefaultBits.Should().Equal(nanBits);
            var outputs = new float[4];
            ExprEvaluator.Evaluate(decoded, ExprBindings.Bind(decoded), ReadOnlySpan<float>.Empty, outputs);
            outputs.Select(BitConverter.SingleToInt32Bits).Should().Equal(int.MinValue, nanBits, 1, nanBits);
        }

        private static object[] Input() => new object[] { "input", new object[0], 0 };

        private static void Put(ref MessagePackWriter writer, object? value)
        {
            switch (value)
            {
                case null: writer.WriteNil(); break;
                case int i: writer.Write(i); break;
                case string s: writer.Write(s); break;
                case System.Collections.IEnumerable items:
                    var all = items.Cast<object>().ToArray();
                    writer.WriteArrayHeader(all.Length);
                    foreach (var item in all) Put(ref writer, item);
                    break;
            }
        }

        private static byte[] Bytes(object? value)
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            Put(ref writer, value);
            writer.Flush();
            return buffer.WrittenMemory.ToArray();
        }

        private static byte[] Wire(object version, object[] inputs, object[] nodes, int outputNode) =>
            Bytes(new object[] { version, inputs, new object[0], new object[] { new object[] { "o", outputNode } }, nodes });

        private static void Refused(byte[] bytes, string rule, int node = -1)
        {
            var act = () => Decode(bytes);
            var thrown = act.Should().Throw<MessagePackSerializationException>().Which;
            var inner = thrown.InnerException.Should().BeOfType<ExprGraphException>().Which;
            inner.Rule.Should().Be(rule);
            inner.NodeIndex.Should().Be(node);
            thrown.ToString().Should().NotContain(Canary);
        }

        [Test]
        public void DecodeRefusesInvalidGraphs()
        {
            var x = new object[] { "x" };
            var cell = new object[] { "cellular3", new object[] { 0, 0, 0 }, 0 };

            Decode(Wire(1, x, new[] { Input() }, 0)).Nodes.Should().HaveCount(1);

            Refused(Wire(1, x, new object[] { Input(), new object[] { Canary + "_op", new object[] { 0 }, 0 } }, 1), "unknown-op", 1);
            Refused(Wire(1, x, new object[] { Input(), new object[] { "add", new object[] { 0 }, 0 } }, 1), "arity", 1);
            Refused(Wire(1, x, new object[] { Input(), new object[] { "neg", new object[] { 2 }, 0 } }, 1), "operand-order", 1);
            Refused(Wire(1, x, new object[] { Input(), new object[] { "neg", new object[] { 1 }, 0 } }, 1), "operand-order", 1);
            Refused(Wire(1, x, new object[] { Input(), new object[] { "pick", new object[] { 0 }, 0 } }, 1), "pick-range", 1);
            Refused(Wire(1, x, new object[] { Input(), cell, new object[] { "pick", new object[] { 1 }, 9 } }, 2), "pick-range", 2);
            Refused(Wire(1, x, new object[] { Input(), cell, new object[] { "neg", new object[] { 1 }, 0 } }, 2), "bare-multi-result", 2);
            Refused(Wire(1, new object[] { "x", "x" }, new[] { Input() }, 0), "duplicate-port-name");
            Refused(Wire(1, new object[] { Canary + "-bad" }, new[] { Input() }, 0), "port-name");
            Refused(Wire(2, x, new[] { Input() }, 0), "version");
            Refused(Wire(1, x, new[] { Input() }, 3), "output-node");
        }

        [Test]
        public void DecodeRefusesNilAndWrongShapes()
        {
            Refused(Bytes(null), "shape");
            Refused(Bytes(new object[] { 1, new object[0] }), "shape");
            var act = () => MessagePackSerializer.Serialize<ExprGraph>(null!, Options);
            act.Should().Throw<MessagePackSerializationException>().Which.InnerException.Should().BeOfType<ExprGraphException>();
        }
    }
}
