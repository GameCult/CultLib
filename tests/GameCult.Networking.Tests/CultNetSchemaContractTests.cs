#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using GameCult.Caching;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    // R-S (docs/cultnet-selection-cut.md, "Self's rulings for fix batch 3", F12): the committed
    // contracts/cultnet/*.schema.json files describe the bytes MessagePack-CSharp actually writes -
    // headers get their own schema (no payload/payloadEncoding, which CultNetRawDocumentHeader has
    // no members for at all), the snapshot page's shape matches map-mode output (every declared key
    // present, nil included), and fieldPredicate refuses values/number together. This decodes real
    // wire bytes from a live CultNetDatabaseServer and checks them against the committed schema
    // files with MiniJsonSchemaValidator, rather than asserting on the C# objects alone - a schema
    // file can drift from the bytes even while the C# types stay internally consistent, which is
    // exactly what F12 was.
    public sealed class CultNetSchemaContractTests
    {
        private static readonly ServerSecurityOptions DevelopmentServerSecurity = ServerSecurityOptions.Development();

        private static string SchemaDir() => Path.Combine(RepoRoot(), "contracts", "cultnet");

        private static string RepoRoot()
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "CultLib.sln")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("CultLib.sln was not found above the test directory.");
        }

        private static JsonElement ToJson<T>(T message) where T : ICultNetSchemaMessage
        {
            var bytes = CultNetSchemaMessageSerialization.Serialize(message);
            var json = MessagePackSerializer.ConvertToJson(bytes);
            return JsonDocument.Parse(json).RootElement;
        }

        [Test]
        public async Task SnapshotResponseRawV1_HeaderProjectionRealBytes_MatchesItsSchema()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache);
            await database.PutAsync(new CultRecordKey("v1:weapon"), new CultNetSelectionEvaluatorTests.SelLeafA
            {
                Name = "sword",
                Kind = "weapon",
                Mass = 3
            });
            using var server = new Server(cache, DevelopmentServerSecurity);
            using var databaseServer = new CultNetDatabaseServer(server, database);

            var response = databaseServer.CreateSelectionResponse(new CultNetSnapshotRequestV1Message
            {
                MessageId = "header-projection",
                Selection = new CultNetSelection() // default projection is "header"
            });
            Assert.That(response.Headers, Is.Not.Null);
            Assert.That(response.Documents, Is.Null);

            var validator = new MiniJsonSchemaValidator(SchemaDir());
            validator.AssertValid(ToJson(response), "cultnet.snapshot-response-raw.v1.schema.json");
        }

        [Test]
        public async Task SnapshotResponseRawV1_DocumentProjectionRealBytes_MatchesItsSchema()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache);
            await database.PutAsync(new CultRecordKey("v1:weapon"), new CultNetSelectionEvaluatorTests.SelLeafA
            {
                Name = "sword",
                Kind = "weapon",
                Mass = 3
            });
            using var server = new Server(cache, DevelopmentServerSecurity);
            using var databaseServer = new CultNetDatabaseServer(server, database);

            var response = databaseServer.CreateSelectionResponse(new CultNetSnapshotRequestV1Message
            {
                MessageId = "document-projection",
                Selection = new CultNetSelection { Projection = CultNetSelectionProjections.Document }
            });
            Assert.That(response.Documents, Is.Not.Null);
            Assert.That(response.Headers, Is.Null);

            var validator = new MiniJsonSchemaValidator(SchemaDir());
            validator.AssertValid(ToJson(response), "cultnet.snapshot-response-raw.v1.schema.json");
        }

        [Test]
        public async Task SnapshotResponseRawV1_WithEdgesRealBytes_MatchesItsSchema()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache);
            var target = new CultNetSelectionEvaluatorTests.SelLeafA { Name = "target", Kind = "k", Mass = 1 };
            await database.PutAsync(new CultRecordKey("target"), target);
            await database.PutAsync(new CultRecordKey("citer"), new CultNetSelectionEvaluatorTests.SelCiter
            {
                Name = "citer",
                Design = new CultRecordRef<CultNetSelectionEvaluatorTests.SelFixtureMiddle>(new CultRecordKey("target"))
            });
            using var server = new Server(cache, DevelopmentServerSecurity);
            using var databaseServer = new CultNetDatabaseServer(server, database);

            var response = databaseServer.CreateSelectionResponse(new CultNetSnapshotRequestV1Message
            {
                MessageId = "with-edges",
                Selection = new CultNetSelection
                {
                    Cited = new CultNetIncoming { Role = "Design", Exists = true },
                    Projection = CultNetSelectionProjections.Document
                }
            });
            Assert.That(response.Edges, Is.Not.Null.And.Length.GreaterThan(0));

            var validator = new MiniJsonSchemaValidator(SchemaDir());
            validator.AssertValid(ToJson(response), "cultnet.snapshot-response-raw.v1.schema.json");
        }

        // R-S: fieldPredicate is now exclusive - values and number may not both be present, even
        // though each alone satisfies one oneOf branch's `required`. Hand-built JSON, not a C# object,
        // because CultNetFieldPredicate itself has no door stopping this shape from being constructed
        // in memory - the door is CultNetSelectionValidation, a different layer; this test is about the
        // wire schema's own oneOf, independent of that door.
        [Test]
        public void FieldPredicate_ValuesAndNumberTogether_FailsItsSchema()
        {
            var instance = JsonDocument.Parse(
                """{"index":"mass","op":"any_of","values":["1"],"number":"1"}""").RootElement;

            var validator = new MiniJsonSchemaValidator(SchemaDir());
            var errors = validator.Validate(instance, "cultnet.selection.schema.json#/$defs/fieldPredicate");

            Assert.That(errors, Is.Not.Empty);
        }

        [Test]
        public void FieldPredicate_ValuesOnly_MatchesItsSchema()
        {
            var instance = JsonDocument.Parse(
                """{"index":"kind","op":"any_of","values":["weapon"]}""").RootElement;

            var validator = new MiniJsonSchemaValidator(SchemaDir());
            var errors = validator.Validate(instance, "cultnet.selection.schema.json#/$defs/fieldPredicate");

            Assert.That(errors, Is.Empty, string.Join("; ", errors));
        }

        [Test]
        public void FieldPredicate_NumberOnly_MatchesItsSchema()
        {
            var instance = JsonDocument.Parse(
                """{"index":"mass","op":"gt","number":"1"}""").RootElement;

            var validator = new MiniJsonSchemaValidator(SchemaDir());
            var errors = validator.Validate(instance, "cultnet.selection.schema.json#/$defs/fieldPredicate");

            Assert.That(errors, Is.Empty, string.Join("; ", errors));
        }
    }
}
