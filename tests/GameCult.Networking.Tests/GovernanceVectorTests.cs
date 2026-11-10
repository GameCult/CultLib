#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    // contracts/cultnet/governance: the vote-API and fork-card contracts (vote-api.md, fork-card.md). The vectors are spec-authored
    // (scripts/gen-governance-vectors.py); this suite re-encodes every message with MessagePack C#, must reproduce the bytes, judges
    // every negative with the committed schema, and replays the fork scenarios through a reference judge written from fork-card.md.
    public sealed class GovernanceVectorTests
    {
        private const string VoteSchema = "governance/vote-api.schema.json";
        private const string ForkSchema = "governance/fork-card.schema.json";

        private static string RepoRoot()
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "CultLib.sln")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("CultLib.sln was not found above the test directory.");
        }

        private static JsonNode Load(string name) =>
            JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "contracts", "cultnet", "governance", name)))!;

        private static readonly JsonNode Vote = Load("vote-api-vectors.json");
        private static readonly JsonNode Fork = Load("fork-card-vectors.json");
        private static MiniJsonSchemaValidator Validator() => new(Path.Combine(RepoRoot(), "contracts", "cultnet"));

        private static List<string> Errors(string file, string def, JsonNode? value)
        {
            var element = JsonSerializer.SerializeToElement(value);
            return Validator().Validate(element, $"{file}#/$defs/{def}");
        }

        // MessagePack C# writes the message in the order the keys appear: shortest ints, shortest strings.
        private static void Write(ref MessagePackWriter w, JsonNode? n)
        {
            switch (n)
            {
                case null: w.WriteNil(); break;
                case JsonObject o:
                    w.WriteMapHeader(o.Count);
                    foreach (var kv in o) { w.Write(kv.Key); Write(ref w, kv.Value); }
                    break;
                case JsonArray a:
                    w.WriteArrayHeader(a.Count);
                    foreach (var item in a) Write(ref w, item);
                    break;
                case JsonValue v when v.TryGetValue<bool>(out var b): w.Write(b); break;
                case JsonValue v when v.TryGetValue<long>(out var l): w.Write(l); break;
                case JsonValue v: w.Write(v.GetValue<string>()); break;
            }
        }

        private static string HexOf(JsonNode? message)
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            Write(ref writer, message);
            writer.Flush();
            return Convert.ToHexString(buffer.WrittenSpan);
        }

        private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        private static string S(JsonNode? n) => n!.GetValue<string>();
        private static JsonNode? Clone(JsonNode? n) => n is null ? null : JsonNode.Parse(n.ToJsonString());

        // Rules a JSON Schema cannot say: vote-api.md counts a reason in UTF-16 code units (as Bifrost does); maxLength counts code points.
        private static IEnumerable<string> VoteRuleErrors(string def, JsonNode? message)
        {
            if (def == "cast" && message?["ballot"] is JsonNode ballot && S(ballot["reason"]).Length > 280) yield return "reason is over 280 UTF-16 code units";
        }

        [Test]
        public void VoteApi_ValidDocuments_ValidateEncodeToTheVectorBytesAndDecodeBack()
        {
            foreach (var v in Vote["valid"]!.AsArray())
            {
                var name = S(v!["name"]);
                Assert.That(Errors(VoteSchema, S(v["def"]), v["message"]), Is.Empty, name);
                Assert.That(VoteRuleErrors(S(v["def"]), v["message"]), Is.Empty, name);
                Assert.That(HexOf(v["message"]), Is.EqualTo(S(v["messagePackHex"])), name);
                var decoded = JsonNode.Parse(MessagePackSerializer.ConvertToJson(Convert.FromHexString(S(v["messagePackHex"]))))!;
                Assert.That(JsonNode.DeepEquals(decoded, v["message"]), Is.True, name);
            }
        }

        [Test]
        public void VoteApi_EveryNegative_IsRefusedBySchemaAndKeepsItsBytes()
        {
            foreach (var v in Vote["invalid"]!.AsArray())
            {
                var name = S(v!["name"]);
                Assert.That(Errors(VoteSchema, S(v["def"]), v["message"]).Count > 0 || VoteRuleErrors(S(v["def"]), v["message"]).Any(), Is.True, $"{name}: {S(v["why"])}");
                Assert.That(HexOf(v["message"]), Is.EqualTo(S(v["messagePackHex"])), name);
            }
        }

        private static byte[] CommitmentBytes(JsonNode b, string voter) => Encoding.UTF8.GetBytes(
            $"gamecult.sealed_ballot.v0\n{S(b["motionId"])}\n{voter}\n{S(b["persona"])}\n{b["round"]!.GetValue<long>()}\n{S(b["candidate"])}\n{S(b["reason"])}\n{S(b["salt"])}");

        [Test]
        public void VoteApi_Commitment_IsSha256OfTheCanonicalBytes_AndRevealMatchesExactlyWhenItHashesToIt()
        {
            foreach (var c in Vote["commitment"]!.AsArray())
            {
                var bytes = CommitmentBytes(c!["ballot"]!, S(c["voterUserAccountId"]));
                Assert.That(Convert.ToHexString(bytes), Is.EqualTo(S(c["canonicalHex"])), S(c["name"]));
                Assert.That(Sha(bytes), Is.EqualTo(S(c["sha256"])), S(c["name"]));
            }
            foreach (var p in Vote["pairs"]!.AsArray())
            {
                var digest = Sha(CommitmentBytes(p!["reveal"]!["ballot"]!, S(p["voterUserAccountId"])));
                Assert.That(digest == S(p["commit"]!["commitment"]), Is.EqualTo(p["matches"]!.GetValue<bool>()), S(p["name"]));
                Assert.That(Errors(VoteSchema, "cast", p["commit"]), Is.Empty);
                Assert.That(Errors(VoteSchema, "cast", p["reveal"]), Is.Empty);
            }
        }

        [Test]
        public void VoteApi_EachRefusalCode_HasAVectorAStatusAndTheVectorBytes()
        {
            var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "contracts", "cultnet", VoteSchema)))!;
            var codes = schema["$defs"]!["refusal"]!["properties"]!["code"]!["enum"]!.AsArray().Select(S).OrderBy(x => x, StringComparer.Ordinal);
            var vectors = Vote["refusals"]!.AsArray();
            Assert.That(vectors.Select(r => S(r!["message"]!["code"])).OrderBy(x => x, StringComparer.Ordinal), Is.EqualTo(codes));
            foreach (var r in vectors)
            {
                Assert.That(Errors(VoteSchema, "refusal", r!["message"]), Is.Empty, S(r["name"]));
                Assert.That(HexOf(r["message"]), Is.EqualTo(S(r["messagePackHex"])), S(r["name"]));
                Assert.That(r["httpStatus"]!.GetValue<int>(), Is.GreaterThanOrEqualTo(400));
            }
            Assert.That(vectors.Single(r => S(r!["name"]) == "rate_limited")!["message"]!["retryAfterSeconds"]!.GetValue<int>(), Is.GreaterThanOrEqualTo(1));
        }

        // ---- fork card (rule source: /opt/commons/handoff/fork-desk-answer-binding-20261110.md) ----
        private static string Jstr(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var rune in s.EnumerateRunes())
            {
                var o = rune.Value;
                sb.Append(o switch
                {
                    '"' => "\\\"", '\\' => "\\\\", 8 => "\\b", 9 => "\\t", 10 => "\\n", 12 => "\\f", 13 => "\\r",
                    < 0x20 => "\\u" + o.ToString("x4"),
                    _ => rune.ToString()
                });
            }
            return sb.Append('"').ToString();
        }

        // RFC 8785 over the five shown members, every string NFC-normalised first; keys sorted; no numbers in the member set.
        private static string N(string s) => s.Normalize(NormalizationForm.FormC);
        private static byte[] Canonical(JsonNode c)
        {
            var options = string.Join(",", c["options"]!.AsArray().Select(o => $"{{\"id\":{Jstr(N(S(o!["id"])))},\"text\":{Jstr(N(S(o["text"])))}}}"));
            var depends = string.Join(",", c["depends"]!.AsArray().Select(d => Jstr(N(S(d)))));
            return Encoding.UTF8.GetBytes($"{{\"context\":{Jstr(N(S(c["context"])))},\"depends\":[{depends}],\"options\":[{options}],\"recommendation\":{Jstr(N(S(c["recommendation"])))},\"title\":{Jstr(N(S(c["title"])))}}}");
        }
        private static string HashOf(JsonNode c) => Sha(Canonical(c));

        private static IEnumerable<string> CardRuleErrors(JsonNode c)
        {
            var ids = c["options"]!.AsArray().Select(o => S(o!["id"])).ToList();
            if (ids.Distinct().Count() != ids.Count) yield return "duplicate option ids";
            if (!ids.Contains(S(c["recommendation"]))) yield return "recommendation is not an option";
            if (c["leans"] is not null && !ids.Contains(S(c["leans"]))) yield return "leans is not an option";
            var deciders = c["deciders"]!.AsArray().Select(S).ToList();
            if (deciders.Distinct().Count() != deciders.Count) yield return "duplicate deciders";
            if (S(c["status"]) == "closed" && c["ruling"] is null) yield return "closed without a ruling";
            if (S(c["status"]) == "open" && c["ruled_from"]!.AsArray().Count > 0) yield return "an open card cites an answer in ruled_from";
            if (S(c["options_hash"]) != HashOf(c)) yield return "options_hash is not the hash of the shown members";
        }

        [Test]
        public void ForkCard_OptionsHash_IsSha256OfTheCanonicalJson_ComputedHere_AndEveryShownMemberMovesIt()
        {
            foreach (var h in Fork["optionsHash"]!.AsArray())
            {
                var bytes = Canonical(h!);
                Assert.That(Encoding.UTF8.GetString(bytes), Is.EqualTo(S(h["canonical"])), S(h["name"]));
                Assert.That(Convert.ToHexString(bytes), Is.EqualTo(S(h["canonicalHex"])), S(h["name"]));
                Assert.That(Sha(bytes), Is.EqualTo(S(h["sha256"])), S(h["name"]));
            }
            var byName = Fork["optionsHash"]!.AsArray().ToDictionary(h => S(h!["name"]), h => S(h!["sha256"]));
            var baseline = byName["heimdall_A_five_members"];
            Assert.That(byName["hidden_members_do_not_move_it"], Is.EqualTo(baseline));
            Assert.That(byName["heimdall_E_decomposed_equals_D"], Is.EqualTo(byName["heimdall_D_nfc_escapes_astral"]), "an NFD and an NFC spelling hash alike");
            foreach (var moved in new[] { "heimdall_B_recommendation_changed", "heimdall_C_option_order_changed", "heimdall_F_option_id_changed", "heimdall_G_depends_emptied",
                                          "option_text_changed", "title_changed", "context_changed", "depends_order_changed", "depends_order_changed_reversed" })
                Assert.That(byName[moved], Is.Not.EqualTo(baseline), moved);
        }

        [Test]
        public void ForkCard_MutatingAnyShownMember_AnOptionIdIncluded_ChangesTheHash()
        {
            var card = Fork["card"]!["valid"]![0]!["message"]!;
            var baseline = HashOf(card);
            var mutations = new Action<JsonNode>[]
            {
                c => c["title"] = S(c["title"]) + "x", c => c["context"] = S(c["context"]) + "x",
                c => c["options"]![0]!["id"] = S(c["options"]![0]!["id"]) + "x", c => c["options"]![0]!["text"] = S(c["options"]![0]!["text"]) + "x",
                c => { var o = c["options"]!.AsArray(); var first = o[0]!; o.RemoveAt(0); o.Add(first); },
                c => c["recommendation"] = S(c["options"]![0]!["id"]) == S(c["recommendation"]) ? S(c["options"]![1]!["id"]) : S(c["options"]![0]!["id"]),
                c => c["depends"]!.AsArray().Add("a:b:c"), c => c["depends"]!.AsArray().Clear(),
            };
            for (var i = 0; i < mutations.Length; i++)
            {
                var copy = Clone(card);
                mutations[i](copy);
                Assert.That(HashOf(copy), Is.Not.EqualTo(baseline), $"mutation {i}");
            }
            var hidden = Clone(card);
            hidden["fork_id"] = "other:question:id"; hidden["leans"] = "yes"; hidden["status"] = "closed"; hidden["ruled_from"] = new JsonArray("n1");
            Assert.That(HashOf(hidden), Is.EqualTo(baseline), "fork_id and the unshown members stay outside the hash");
        }

        [Test]
        public void ForkCard_DocumentsValidateAndEncodeToTheVectorBytes()
        {
            foreach (var v in Fork["card"]!["valid"]!.AsArray())
            {
                Assert.That(Errors(ForkSchema, "card", v!["message"]), Is.Empty, S(v["name"]));
                Assert.That(CardRuleErrors(v["message"]!), Is.Empty, S(v["name"]));
                Assert.That(HexOf(v["message"]), Is.EqualTo(S(v["messagePackHex"])), S(v["name"]));
            }
            foreach (var (section, def) in new[] { ("request", "answerRequest"), ("deskAnswer", "deskAnswer") })
                foreach (var v in Fork[section]!["valid"]!.AsArray())
                {
                    Assert.That(Errors(ForkSchema, def, v!["message"]), Is.Empty, S(v["name"]));
                    Assert.That(HexOf(v["message"]), Is.EqualTo(S(v["messagePackHex"])), S(v["name"]));
                    var decoded = JsonNode.Parse(MessagePackSerializer.ConvertToJson(Convert.FromHexString(S(v["messagePackHex"]))))!;
                    Assert.That(JsonNode.DeepEquals(decoded, v["message"]), Is.True, S(v["name"]));
                }
            var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "contracts", "cultnet", ForkSchema)))!;
            IEnumerable<string> Codes(string def) => schema["$defs"]![def]!["properties"]!["code"]!["enum"]!.AsArray().Select(S).OrderBy(x => x, StringComparer.Ordinal);
            Assert.That(Fork["results"]!.AsArray().Select(r => S(r!["name"])).OrderBy(x => x, StringComparer.Ordinal), Is.EqualTo(Codes("deskAnswerRefused")));
            foreach (var v in Fork["results"]!.AsArray())
            {
                Assert.That(Errors(ForkSchema, "answerResult", v!["message"]), Is.Empty, S(v["name"]));
                Assert.That(HexOf(v["message"]), Is.EqualTo(S(v["messagePackHex"])), S(v["name"]));
                Assert.That(v["message"]!["card"] is not null, Is.EqualTo(S(v["name"]) == "stale_fork"), "only a stale refusal shows the new fork");
            }
            Assert.That(Fork["doorRefusals"]!.AsArray().Select(r => S(r!["name"])).OrderBy(x => x, StringComparer.Ordinal), Is.EqualTo(Codes("refusal")));
            foreach (var v in Fork["doorRefusals"]!.AsArray())
            {
                Assert.That(Errors(ForkSchema, "refusal", v!["message"]), Is.Empty, S(v["name"]));
                Assert.That(HexOf(v["message"]), Is.EqualTo(S(v["messagePackHex"])), S(v["name"]));
            }
        }

        [Test]
        public void ForkCard_EveryNegative_IsRefusedBySchemaOrAStatedRule()
        {
            foreach (var v in Fork["card"]!["invalid"]!.AsArray())
                Assert.That(Errors(ForkSchema, "card", v!["message"]).Count > 0 || CardRuleErrors(v["message"]!).Any(), Is.True, $"{S(v["name"])}: {S(v["why"])}");
            foreach (var (section, def) in new[] { ("request", "answerRequest"), ("deskAnswer", "deskAnswer") })
                foreach (var v in Fork[section]!["invalid"]!.AsArray())
                {
                    Assert.That(Errors(ForkSchema, def, v!["message"]), Is.Not.Empty, $"{S(v["name"])}: {S(v["why"])}");
                    Assert.That(HexOf(v["message"]), Is.EqualTo(S(v["messagePackHex"])), S(v["name"]));
                }
        }

        private static readonly Dictionary<string, string> Tails = new()
        {
            ["unauthenticated"] = "a verified session is required",
            ["body_forbidden_field"] = "answerer and authority are set by the receiver",
            ["body_unknown_field"] = "the body is a closed shape",
            ["body_invalid"] = "the body does not satisfy gamecult.fork.answer_request.v0",
            ["fork_unknown"] = "no such fork",
        };
        private static readonly string[] RequestKeys = { "schemaVersion", "fork_id", "options_hash", "answer", "retracted", "retracts", "answered_at" };

        private static bool HasKey(JsonNode? n, params string[] names) => n switch
        {
            JsonObject o => o.Any(kv => names.Contains(kv.Key) || HasKey(kv.Value, names)),
            JsonArray a => a.Any(x => HasKey(x, names)),
            _ => false
        };

        // The reference judge: Huginn's half behind the second door as fork-card.md states it. Server-set fields come from the step.
        private static (List<JsonNode?> Responses, JsonNode Record) Judge(JsonNode startCard, JsonArray steps)
        {
            var card = startCard;
            var answers = new JsonArray();
            var responses = new List<JsonNode?>();
            string? Counted()
            {
                var hash = HashOf(card);
                foreach (var a in answers)
                {
                    if (S(a!["outcome"]) is not ("Counted" or "Revision") || S(a["options_hash"]) != hash) continue;
                    if (answers.Any(r => S(r!["outcome"]) == "Retraction" && S(r["retracts"]) == S(a["answer_id"]))) continue;
                    return S(a["answer_id"]);
                }
                return null;
            }
            foreach (var step in steps)
            {
                var kind = S(step!["kind"]);
                if (kind == "matrix_event") { responses.Add(null); continue; }   // rule 9: Matrix never changes a decision
                if (kind == "card") { card = step["card"]!; responses.Add(null); continue; }
                var s = step["server"]!;
                var b = step["body"]!.AsObject();
                JsonNode Door(string code) => new JsonObject { ["schemaVersion"] = "gamecult.fork.refusal.v0", ["code"] = code, ["error"] = $"{code}: {Tails[code]}" };
                JsonNode Store(string outcome, string? link, string? code)
                {
                    var a = b["answer"] as JsonObject;
                    var doc = new JsonObject
                    {
                        ["schemaVersion"] = "gamecult.desk_answer.v0", ["answer_id"] = S(s["answer_id"]), ["fork_id"] = Clone(b["fork_id"]), ["outcome"] = outcome,
                        ["of"] = outcome == "Revision" ? link : null, ["retracts"] = outcome == "Retraction" ? link : null, ["code"] = outcome == "Refused" ? code : null,
                        ["options_hash"] = Clone(b["options_hash"]), ["choice"] = Clone(a?["choice"]), ["note"] = Clone(a?["note"]), ["answered_at"] = Clone(b["answered_at"]),
                        ["received_at"] = Clone(s["received_at"]),
                        ["claim_in_future"] = (DateTimeOffset.Parse(S(b["answered_at"])) - DateTimeOffset.Parse(S(s["received_at"]))).TotalSeconds > 300,
                        ["answerer"] = Clone(s["answerer"]), ["authority"] = Clone(s["authority"])
                    };
                    answers.Add(Clone(doc));
                    return new JsonObject { ["schemaVersion"] = "gamecult.fork.answer_result.v0", ["answer"] = doc, ["card"] = code == "stale_fork" ? Clone(card) : null };
                }
                JsonNode Refuse(string code) => Store("Refused", null, code);
                var deciders = card["deciders"]!.AsArray().Select(S).ToList();
                var retracted = b["retracted"] is JsonValue rv && rv.TryGetValue<bool>(out var rb) && rb;
                JsonNode response;
                if (s["answerer"] is null) response = Door("unauthenticated");
                else if (HasKey(b, "answerer", "authority")) response = Door("body_forbidden_field");
                else if (b.Any(kv => !RequestKeys.Contains(kv.Key)) || (b["answer"] is JsonObject ao && ao.Any(kv => kv.Key is not ("choice" or "note")))) response = Door("body_unknown_field");
                else if (Errors(ForkSchema, "answerRequest", b).Count > 0 || (!retracted && b["answer"] is null)) response = Door("body_invalid");
                else if (S(b["fork_id"]) != S(card["fork_id"])) response = Door("fork_unknown");
                else if (S(b["options_hash"]) != HashOf(card)) response = Refuse("stale_fork");
                else if (!deciders.Contains(S(s["answerer"]))) response = Refuse("not_a_decider");
                else if (S(card["status"]) == "closed") response = Refuse("fork_closed");
                else if (retracted || b["retracts"] is not null)
                {
                    var target = answers.FirstOrDefault(a => a is not null && b["retracts"] is not null && S(a["answer_id"]) == S(b["retracts"])
                        && S(a["outcome"]) is "Counted" or "Revision" && S(a["options_hash"]) == S(b["options_hash"]));
                    if (!retracted || b["answer"] is not null || b["retracts"] is null || target is null) response = Refuse("bad_retraction");
                    else if (answers.Any(a => S(a!["outcome"]) == "Retraction" && S(a["retracts"]) == S(target["answer_id"]))) response = Refuse("already_retracted");
                    else response = Store("Retraction", S(target["answer_id"]), null);
                }
                else if (b["answer"]!["choice"] is not null && !card["options"]!.AsArray().Any(o => S(o!["id"]) == S(b["answer"]!["choice"]))) response = Refuse("choice_invalid");
                else
                {
                    var first = Counted();
                    response = first is null ? Store("Counted", null, null) : Store("Revision", first, null);
                }
                responses.Add(response);
            }
            var record = new JsonObject
            {
                ["schemaVersion"] = "gamecult.fork.record.v0", ["fork_id"] = Clone(card["fork_id"]), ["options_hash"] = HashOf(card), ["counted_answer_id"] = Counted(), ["answers"] = answers
            };
            return (responses, record);
        }

        [Test]
        public void ForkCard_EveryScenario_ReplaysToTheVectorsResponsesAndRecord_BytesIncluded()
        {
            var seen = new HashSet<string>();
            var doorRefusals = 0;
            var door = new HashSet<string> { "unauthenticated", "body_forbidden_field", "body_unknown_field", "body_invalid", "fork_unknown" };
            foreach (var sc in Fork["scenarios"]!.AsArray())
            {
                var name = S(sc!["name"]);
                Assert.That(HashOf(sc["card"]!), Is.EqualTo(S(sc["optionsHash"])), name);
                Assert.That(CardRuleErrors(sc["card"]!), Is.Empty, name);
                var steps = sc["steps"]!.AsArray();
                var (responses, record) = Judge(sc["card"]!, steps);
                Assert.That(responses.Count, Is.EqualTo(steps.Count), name);
                for (var i = 0; i < steps.Count; i++)
                    Assert.That(JsonNode.DeepEquals(responses[i], steps[i]!["response"]), Is.True, $"{name} step {i}: {responses[i]?.ToJsonString()}");
                Assert.That(JsonNode.DeepEquals(record, sc["record"]), Is.True, $"{name}: {record.ToJsonString()}");
                Assert.That(Errors(ForkSchema, "record", record), Is.Empty, name);
                Assert.That(HexOf(record), Is.EqualTo(S(sc["recordHex"])), name);
                foreach (var step in steps)
                {
                    var r = step!["response"];
                    if (r is null) continue;
                    Assert.That(Errors(ForkSchema, "answerResult", r).Count == 0 || Errors(ForkSchema, "refusal", r).Count == 0, Is.True, name);
                    if (r["code"] is not null)
                    {
                        seen.Add(S(r["code"]));
                        if (door.Contains(S(r["code"])))
                        {
                            doorRefusals++;
                            Assert.That(sc["record"]!["answers"]!.AsArray().Any(a => S(a!["answer_id"]) == S(step["server"]!["answer_id"])), Is.False, $"{name} stored a door refusal");
                        }
                    }
                    else seen.Add(S(r["answer"]!["code"] ?? r["answer"]!["outcome"]));
                }
                foreach (var a in sc["record"]!["answers"]!.AsArray()) seen.Add(S(a!["outcome"]));
            }
            Assert.That(doorRefusals, Is.GreaterThanOrEqualTo(8));
            foreach (var want in new[] { "Counted", "Revision", "Retraction", "Refused", "unauthenticated", "body_forbidden_field", "body_unknown_field", "body_invalid", "fork_unknown", "stale_fork",
                                         "not_a_decider", "fork_closed", "choice_invalid", "bad_retraction", "already_retracted" })
                Assert.That(seen, Does.Contain(want));
        }

        // A ruling may carry authority Operator only if it cites a Counted desk_answer stamped Operator (binding file 1.3d).
        private static bool AdmitRuling(JsonNode cite, JsonArray answers)
        {
            if (S(cite["authority"]) != "Operator") return true;
            var cited = answers.FirstOrDefault(a => cite["answer_id"] is not null && S(a!["answer_id"]) == S(cite["answer_id"]));
            return cited is not null && S(cited["outcome"]) == "Counted" && S(cited["authority"]) == "Operator";
        }

        [Test]
        public void ForkCard_OperatorAuthority_NeedsACitedCountedAnswerRecord()
        {
            foreach (var c in Fork["rulingCites"]!.AsArray())
            {
                Assert.That(Errors(ForkSchema, "rulingCite", c!["message"]), Is.Empty, S(c["name"]));
                Assert.That(HexOf(c["message"]), Is.EqualTo(S(c["messagePackHex"])), S(c["name"]));
                Assert.That(AdmitRuling(c["message"]!, c["answers"]!.AsArray()), Is.EqualTo(c["admit"]!.GetValue<bool>()), S(c["name"]));
            }
        }
    }
}
