using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    // contracts/cultnet/interop/write-receipt-vectors.json `admittedMessageId`: a write_admitted messageId needs a letter or
    // number and no Default_Ignorable code point, compared raw (never trimmed). The frame is built with this runtime's own
    // encoder; the case with `wireHex` (a lone surrogate is invalid UTF-8) supplies the whole frame.
    public sealed class CultNetWriteAdmittedMessageIdTests
    {
        private static JsonElement Vectors()
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "CultLib.sln")))
                directory = directory.Parent;
            var path = Path.Combine(directory!.FullName, "contracts", "cultnet", "interop", "write-receipt-vectors.json");
            return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
        }

        public static IEnumerable<string> Cases() =>
            Vectors().GetProperty("admittedMessageId").GetProperty("cases").EnumerateArray().Select(c => c.GetProperty("name").GetString()!);

        [Test]
        public void TheFileDeclaresHowManyCasesThisRunnerMustRun() =>
            Assert.That(Cases().Count(), Is.EqualTo(Vectors().GetProperty("counts").GetProperty("admittedMessageIdCases").GetInt32()));

        [TestCaseSource(nameof(Cases))]
        public void AWriteAdmittedMessageIdIsRefusedOrAcceptedRaw(string name)
        {
            var vector = Vectors().GetProperty("admittedMessageId").GetProperty("cases").EnumerateArray()
                .Single(c => c.GetProperty("name").GetString() == name);
            var id = string.Concat(Enumerable.Repeat(
                string.Concat(vector.GetProperty("codePoints").EnumerateArray()
                    .Select(cp => Convert.ToInt32(cp.GetString(), 16)).Where(point => point < 0xD800 || point > 0xDFFF).Select(char.ConvertFromUtf32)),
                vector.TryGetProperty("repeat", out var repeat) ? repeat.GetInt32() : 1));
            var bytes = vector.TryGetProperty("wireHex", out var wire)
                ? Convert.FromHexString(wire.GetString()!)
                : CultNetSchemaMessageSerialization.Serialize(new CultNetWriteAdmittedMessage
                    { MessageId = id, Operation = "put", SchemaId = "note.v0", RecordKey = "note:1" });

            CultNetWriteAdmittedMessage? decoded = null;
            try { decoded = (CultNetWriteAdmittedMessage)CultNetSchemaMessageSerialization.Deserialize(bytes); }
            catch (Exception) { /* a refusal */ }

            if (vector.GetProperty("expect").GetString() == "refuse")
                Assert.That(decoded, Is.Null, vector.GetProperty("why").GetString());
            else
            {
                Assert.That(decoded, Is.Not.Null, vector.GetProperty("why").GetString());
                Assert.That(decoded!.MessageId, Is.EqualTo(id));
            }
        }

        // The generated ranges file is the independent copy of the embedded tables: first, last and the points either side of every range.
        [Test]
        public void EveryRangeOfTheUnicodeTablesIsPinnedAtBothEnds()
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "CultLib.sln")))
                directory = directory.Parent;
            var ranges = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "contracts", "cultnet", "interop", "write-admitted-message-id-ranges.json"))).RootElement;
            var letters = ranges.GetProperty("letterOrNumber").EnumerateArray().Select(r => (r[0].GetInt32(), r[1].GetInt32())).ToArray();
            var ignorable = ranges.GetProperty("defaultIgnorable").EnumerateArray().Select(r => (r[0].GetInt32(), r[1].GetInt32())).ToArray();
            Assert.That(letters.Length, Is.GreaterThan(700));
            Assert.That(ignorable.Length, Is.EqualTo(17));
            static bool Accepted(string id)
            {
                try
                {
                    CultNetSchemaMessageSerialization.Deserialize(CultNetSchemaMessageSerialization.Serialize(
                        new CultNetWriteAdmittedMessage { MessageId = id, Operation = "put", SchemaId = "note.v0", RecordKey = "note:1" }));
                    return true;
                }
                catch (Exception) { return false; }
            }

            var failures = new List<string>();
            foreach (var (low, high) in letters)
                foreach (var (point, want) in new[] { (low, true), (high, true), (low - 1, false), (high + 1, false) })
                    if (Accepted(char.ConvertFromUtf32(point)) != want) failures.Add($"U+{point:X} alone should be {(want ? "accepted" : "refused")}");
            foreach (var (low, high) in ignorable)
                foreach (var (point, want) in new[] { (low, false), (high, false), (low - 1, true), (high + 1, true) })
                    if (Accepted("a" + char.ConvertFromUtf32(point)) != want) failures.Add($"a + U+{point:X} should be {(want ? "accepted" : "refused")}");
            Assert.That(failures, Is.Empty);
        }

        // admittedMessageIdDuplicates: C# reads the raw map, so it sees a repeated messageId key and refuses it whatever the values.
        [Test]
        public void ARepeatedMessageIdKeyIsRefusedWhateverTheValues()
        {
            var cases = Vectors().GetProperty("admittedMessageIdDuplicates").GetProperty("cases").EnumerateArray().ToArray();
            Assert.That(cases.Length, Is.EqualTo(Vectors().GetProperty("counts").GetProperty("admittedMessageIdDuplicateCases").GetInt32()));
            foreach (var vector in cases)
            {
                Assert.That(vector.GetProperty("seesDuplicate").GetString(), Is.EqualTo("refuse"));
                Assert.That(() => CultNetSchemaMessageSerialization.Deserialize(Convert.FromHexString(vector.GetProperty("wireHex").GetString()!)),
                    Throws.Exception, vector.GetProperty("name").GetString());
            }
        }
    }
}
