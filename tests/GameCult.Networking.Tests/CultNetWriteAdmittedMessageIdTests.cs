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
    }
}
