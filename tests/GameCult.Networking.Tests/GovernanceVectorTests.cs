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
                case JsonValue v when v.TryGetValue<int>(out var i): w.Write(i); break;
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
            if (c["depends"]!.AsArray().Any(d => Encoding.UTF8.GetByteCount(S(d)) > 1000)) yield return "a depends line is over 1000 UTF-8 bytes";
            if (S(c["status"]) == "open" && c["ruling"] is not null) yield return "an open card names a ruling";
            if (c["ruling"] is null && c["ruled_from"]!.AsArray().Count > 0) yield return "ruled_from without a ruling";
            if (S(c["options_hash"]) != HashOf(c)) yield return "options_hash is not the hash of the shown members";
        }

        // body_sha256 is Huginn's private "same body" digest, not a wire field: compact JSON, fixed key order, null for missing, the two illegal-form keys only when present.
        [Test]
        public void ForkCard_AnAnswerResultWithoutCountedAnswerId_IsRefusedBySchema()
        {
            var result = Clone(Fork["scenarios"]!.AsArray().SelectMany(sc => sc!["steps"]!.AsArray()).First(st => st!["response"]?["answer"] is not null)!["response"])!.AsObject();
            Assert.That(Errors(ForkSchema, "answerResult", result), Is.Empty);
            result.Remove("counted_answer_id");
            Assert.That(Errors(ForkSchema, "answerResult", result), Is.Not.Empty);
        }

        [Test]
        public void ForkCard_BodyDigest_IsSha256OfTheCompactJsonInTheStatedKeyOrder_AndTheFixedFormsSeparateRetractedTrueFromFalse()
        {
            var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            var digests = Fork["bodyDigests"]!.AsArray().ToDictionary(d => S(d!["name"]), d => d!);
            foreach (var d in digests.Values)
            {
                var i = d["input"]!;
                var o = new JsonObject();
                foreach (var key in new[] { "fork", "options_hash", "answered_at", "choice", "note", "retracts", "illegal_retraction", "retracted" })
                    if (key is "illegal_retraction" or "retracted" ? i[key] is not null : true) o[key] = Clone(i[key]);
                var json = o.ToJsonString(options);
                Assert.That(json, Is.EqualTo(S(d["canonical"])), S(d["name"]));
                Assert.That(Sha(Encoding.UTF8.GetBytes(json)), Is.EqualTo(S(d["sha256"])), S(d["name"]));
            }
            Assert.That(S(digests["plain_answer_choice_A"]["sha256"]), Does.StartWith("8a4d5b2e"));
            Assert.That(S(digests["legal_retraction"]["sha256"]), Does.StartWith("9f807538"));
            Assert.That(S(digests["legal_retraction_door_form"]["sha256"]), Does.StartWith("84576840"));
            Assert.That(digests["legal_retraction_door_form"]["input"]!["retracts"]!.ToString(), Does.EndWith(".n1"), "the door's form: the wire n<seq> converted to the full key");
            Assert.That(S(digests["illegal_i_retraction_with_an_answer_retracted_true"]["sha256"]), Does.StartWith("935cab1a"));
            Assert.That(S(digests["illegal_iii_plain_answer_with_a_target_retracted_false"]["sha256"]), Does.StartWith("82b24ccc"));
            Assert.That(S(digests["illegal_ii_no_answer_no_target_retracted_true"]["sha256"]), Does.StartWith("34ef86ed"));
            Assert.That(digests.Values.Select(d => S(d["sha256"])).Distinct().Count(), Is.EqualTo(digests.Count), "no two vectors share a digest");
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
                    Assert.That(NoteRuleErrors(def, v["message"]), Is.Empty, S(v["name"]));
                    Assert.That(HexOf(v["message"]), Is.EqualTo(S(v["messagePackHex"])), S(v["name"]));
                    var decoded = JsonNode.Parse(MessagePackSerializer.ConvertToJson(Convert.FromHexString(S(v["messagePackHex"]))))!;
                    Assert.That(JsonNode.DeepEquals(decoded, v["message"]), Is.True, S(v["name"]));
                }
            var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "contracts", "cultnet", ForkSchema)))!;
            IEnumerable<string> codesOfRefusal() => schema["$defs"]!["refusal"]!["properties"]!["code"]!["enum"]!.AsArray().Select(S);
            IEnumerable<string> Codes(string def) => schema["$defs"]![def]!["properties"]!["code"]!["enum"]!.AsArray().Select(S).OrderBy(x => x, StringComparer.Ordinal);
            Assert.That(Fork["results"]!.AsArray().Select(r => S(r!["name"])).OrderBy(x => x, StringComparer.Ordinal), Is.EqualTo(Codes("deskAnswerRefused")));
            foreach (var v in Fork["results"]!.AsArray())
            {
                Assert.That(Errors(ForkSchema, "answerResult", v!["message"]), Is.Empty, S(v["name"]));
                Assert.That(HexOf(v["message"]), Is.EqualTo(S(v["messagePackHex"])), S(v["name"]));
                Assert.That(v["message"]!["card"] is not null, Is.EqualTo(S(v["name"]) == "stale_fork"), "only a stale refusal shows the new fork");
            }
            Assert.That(Fork["doorRefusals"]!.AsArray().Select(r => S(r!["name"])).OrderBy(x => x, StringComparer.Ordinal), Is.EqualTo(Codes("refusal")));
            foreach (var v in Fork["unavailable"]!.AsArray())
            {
                Assert.That(Errors(ForkSchema, "unavailable", v!["message"]), Is.Empty, S(v["name"]));
                Assert.That(HexOf(v["message"]), Is.EqualTo(S(v["messagePackHex"])), S(v["name"]));
                Assert.That(v["httpStatus"]!.GetValue<int>(), Is.EqualTo(503));
                Assert.That(codesOfRefusal(), Does.Not.Contain("unavailable"), "unavailable is kept apart from the refusal set");
            }
            foreach (var v in Fork["doorRefusals"]!.AsArray())
            {
                Assert.That(Errors(ForkSchema, "refusal", v!["message"]), Is.Empty, S(v["name"]));
                Assert.That(HexOf(v["message"]), Is.EqualTo(S(v["messagePackHex"])), S(v["name"]));
            }
            Assert.That(Fork["doorRefusals"]!.AsArray().Single(r => S(r!["name"]) == "fork_too_large")!["httpStatus"]!.GetValue<int>(), Is.EqualTo(422));
        }

        [Test]
        public void ForkCard_EveryNegative_IsRefusedBySchemaOrAStatedRule()
        {
            foreach (var v in Fork["card"]!["invalid"]!.AsArray())
                Assert.That(Errors(ForkSchema, "card", v!["message"]).Count > 0 || CardRuleErrors(v["message"]!).Any(), Is.True, $"{S(v["name"])}: {S(v["why"])}");
            foreach (var (section, def) in new[] { ("request", "answerRequest"), ("deskAnswer", "deskAnswer") })
                foreach (var v in Fork[section]!["invalid"]!.AsArray())
                {
                    Assert.That(Errors(ForkSchema, def, v!["message"]).Count > 0 || NoteRuleErrors(def, v["message"]).Any(), Is.True, $"{S(v["name"])}: {S(v["why"])}");
                    Assert.That(HexOf(v["message"]), Is.EqualTo(S(v["messagePackHex"])), S(v["name"]));
                }
        }

        // Rules a JSON Schema cannot say: a note is at most 4000 UTF-8 bytes (maxLength counts code points and is only the looser bound).
        private static IEnumerable<string> NoteRuleErrors(string def, JsonNode? message)
        {
            var note = def switch { "answerRequest" => message?["answer"]?["note"], "deskAnswer" => message?["note"], _ => null };
            if (note is not null && Encoding.UTF8.GetByteCount(S(note)) > 4000) yield return "note is over 4000 UTF-8 bytes";
        }

        private static readonly Dictionary<string, string> Tails = new()
        {
            ["unauthenticated"] = "a verified session is required",
            ["body_forbidden_field"] = "answerer and authority are set by the receiver",
            ["body_unknown_field"] = "the body is a closed shape",
            ["body_invalid"] = "the body does not satisfy gamecult.fork.answer_request.v0",
            ["fork_unknown"] = "no such fork",
            ["forbidden"] = "only Active members may use the desk",
            ["request_id_conflict"] = "this request_id was used for a different body",
        };
        private static readonly string[] RequestKeys = { "schemaVersion", "fork_id", "options_hash", "answer", "retracted", "retracts", "answered_at", "request_id" };

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
            var seen = new Dictionary<string, (JsonNode Body, JsonNode Response)>();   // (answerer, request_id) -> the first stored outcome
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
                        ["of"] = outcome == "Revision" ? link : null, ["retracts"] = outcome == "Retraction" ? link : outcome == "Refused" ? Clone(b["retracts"]) : null, ["code"] = outcome == "Refused" ? code : null,
                        ["options_hash"] = Clone(b["options_hash"]), ["choice"] = Clone(a?["choice"]), ["note"] = Clone(a?["note"]), ["answered_at"] = Clone(b["answered_at"]),
                        ["received_at"] = Clone(s["received_at"]),
                        ["claim_in_future"] = (DateTimeOffset.Parse(S(b["answered_at"])) - DateTimeOffset.Parse(S(s["received_at"]))).TotalSeconds > 300,
                        ["answerer"] = Clone(s["answerer"]), ["authority"] = Clone(s["authority"]), ["request_id"] = Clone(b["request_id"])
                    };
                    answers.Add(Clone(doc));
                    var stored = new JsonObject { ["schemaVersion"] = "gamecult.fork.answer_result.v0", ["answer"] = doc, ["card"] = code == "stale_fork" ? Clone(card) : null, ["counted_answer_id"] = Counted() };
                    seen[S(s["answerer"]) + "|" + S(b["request_id"])] = (Clone(b)!, Clone(stored)!);
                    return stored;
                }
                JsonNode Refuse(string code) => Store("Refused", null, code);
                var deciders = card["deciders"]!.AsArray().Select(S).ToList();
                var retracted = b["retracted"] is JsonValue rv && rv.TryGetValue<bool>(out var rb) && rb;
                var down = s["door_down"] is not null;          // the second door failed to answer; "stored" says whether Huginn had the request by then
                var reached = s["stored"] is not null;
                JsonNode Unavailable() => new JsonObject { ["schemaVersion"] = "gamecult.fork.unavailable.v0", ["code"] = "unavailable", ["error"] = "unavailable: the outcome is not confirmed, try again with the same request_id", ["retryAfterSeconds"] = Clone(s["retry_after_seconds"]) };
                // a stored desk_answer has no card, so a replayed stale_fork carries the CURRENT card (fork-card.md, request_id)
                JsonNode Replay(JsonNode stored) { var copy = Clone(stored)!; if (S(copy["answer"]!["code"] ?? "") == "stale_fork") copy["card"] = Clone(card); copy["counted_answer_id"] = Counted(); return copy; }
                JsonNode response;
                if (s["answerer"] is null) response = Door("unauthenticated");
                else if (s["active"] is JsonValue activeValue && !activeValue.GetValue<bool>()) response = Door("forbidden");
                else if (HasKey(b, "answerer", "authority")) response = Door("body_forbidden_field");
                else if (b.Any(kv => !RequestKeys.Contains(kv.Key)) || (b["answer"] is JsonObject ao && ao.Any(kv => kv.Key is not ("choice" or "note")))) response = Door("body_unknown_field");
                else if (Errors(ForkSchema, "answerRequest", b).Count > 0 || NoteRuleErrors("answerRequest", b).Any() || (!retracted && b["answer"] is null)) response = Door("body_invalid");
                else if (down && !reached) response = Unavailable();
                else if (seen.TryGetValue(S(s["answerer"]) + "|" + S(b["request_id"]), out var prior)) response = JsonNode.DeepEquals(prior.Body, b) ? Replay(prior.Response) : Door("request_id_conflict");
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
                responses.Add(down && reached ? Unavailable() : response);
            }
            var record = new JsonObject
            {
                ["schemaVersion"] = "gamecult.fork.record.v0", ["fork_id"] = Clone(card["fork_id"]), ["options_hash"] = HashOf(card), ["counted_answer_id"] = Counted(), ["answers"] = answers
            };
            return (responses, record);
        }

        [Test]
        public void ForkCard_ARequestIdBelongsToItsAccount_AnotherAccountsIdenticalBodyGetsItsOwnAnswerNeverTheFirstOutcome()
        {
            var crossed = 0;
            foreach (var sc in Fork["scenarios"]!.AsArray())
            {
                var first = new Dictionary<string, (string Answerer, JsonNode Result)>();   // request_id -> the account that first got a stored result and what it got
                foreach (var step in sc!["steps"]!.AsArray())
                {
                    if (S(step!["kind"]) != "answer" || step["response"]?["answer"] is null) continue;
                    var who = S(step["server"]!["answerer"]);
                    var result = step["response"]!;
                    Assert.That(S(result["answer"]!["answerer"]), Is.EqualTo(who), $"{S(sc["name"])}: a result is stamped with the account that asked");
                    var rid = S(step["body"]!["request_id"]);
                    if (!first.TryGetValue(rid, out var prior)) { first[rid] = (who, result); continue; }
                    if (prior.Answerer == who) continue;
                    crossed++;
                    Assert.That(S(result["answer"]!["answer_id"]), Is.Not.EqualTo(S(prior.Result["answer"]!["answer_id"])), $"{S(sc["name"])}: its own answer id");
                    Assert.That(JsonNode.DeepEquals(result, prior.Result), Is.False, $"{S(sc["name"])}: the first account's outcome is never returned");
                }
            }
            Assert.That(crossed, Is.GreaterThanOrEqualTo(3), "the vectors carry the cross-account cases");
        }

        [Test]
        public void ForkCard_EveryScenario_ReplaysToTheVectorsResponsesAndRecord_BytesIncluded()
        {
            var seen = new HashSet<string>();
            var doorRefusals = 0;
            var door = new HashSet<string> { "unauthenticated", "body_forbidden_field", "body_unknown_field", "body_invalid", "fork_unknown", "forbidden", "request_id_conflict" };
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
                    Assert.That(Errors(ForkSchema, "answerResult", r).Count == 0 || Errors(ForkSchema, "refusal", r).Count == 0 || Errors(ForkSchema, "unavailable", r).Count == 0, Is.True, name);
                    if (r["code"] is not null)
                    {
                        seen.Add(S(r["code"]));
                        // unavailable promises nothing: the outcome is not confirmed, so the answer is stored exactly when Huginn had it
                        if (S(r["code"]) == "unavailable")
                            Assert.That(sc["record"]!["answers"]!.AsArray().Any(a => S(a!["answer_id"]) == S(step["server"]!["answer_id"])), Is.EqualTo(step["server"]!["stored"] is not null), $"{name} unavailable");
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
            foreach (var want in new[] { "Counted", "Revision", "Retraction", "Refused", "unauthenticated", "body_forbidden_field", "body_unknown_field", "body_invalid", "fork_unknown", "unavailable", "forbidden", "request_id_conflict", "stale_fork",
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

        // ---- transport: list, read, auth (fork-card.md "Transport") ----
        private static IEnumerable<string> EntryRuleErrors(JsonNode e)
        {
            var card = e["card"]!; var record = e["record"]!;
            foreach (var error in CardRuleErrors(card)) yield return error;
            if (S(record["fork_id"]) != S(card["fork_id"])) yield return "record of another fork";
            if (S(record["options_hash"]) != S(card["options_hash"])) yield return "record under another hash";
            var answers = record["answers"]!.AsArray();
            var counted = answers.Where(a => (S(a!["outcome"]) is "Counted" or "Revision") && S(a["options_hash"]) == S(card["options_hash"])
                && !answers.Any(r => S(r!["outcome"]) == "Retraction" && S(r["retracts"]) == S(a["answer_id"]))).Select(a => S(a!["answer_id"])).FirstOrDefault();
            if ((record["counted_answer_id"] is null ? null : S(record["counted_answer_id"])) != counted) yield return "counted_answer_id is not derived";
        }

        // Order is a unique rank per Mind: the refusal names both fork ids, the first holder and the second.
        private static List<string> OrderErrors(IEnumerable<JsonNode?> cards)
        {
            var holder = new Dictionary<long, string>();
            var errors = new List<string>();
            foreach (var c in cards)
            {
                var order = c!["order"]!.GetValue<long>();
                if (holder.TryGetValue(order, out var first)) errors.Add($"order {order} is shared by {first} and {S(c["fork_id"])}");
                else holder[order] = S(c["fork_id"]);
            }
            return errors;
        }

        // The reference list judge: newest first by card order, status filter, cursor c<N> = cards below order N, a page of limit, counts over the whole set.
        private static JsonNode JudgeList(JsonArray entries, long ordinal, JsonNode request)
        {
            long Order(JsonNode? e) => e!["card"]!["order"]!.GetValue<long>();
            var rows = entries.OrderByDescending(Order).ToList();
            var want = rows.Where(e => request["status"] is null || (S(e!["card"]!["status"]) == "open") == (S(request["status"]) == "open")).ToList();
            if (request["cursor"] is not null) { var below = long.Parse(S(request["cursor"])[1..]); want = want.Where(e => Order(e) < below).ToList(); }
            var page = want.Take(request["limit"]!.GetValue<int>()).ToList();
            var more = want.Count > page.Count && page.Count > 0;
            return new JsonObject
            {
                ["schemaVersion"] = "gamecult.fork.list.v0", ["mind"] = Clone(request["mind"]), ["stamp"] = $"s{ordinal}",
                ["open_count"] = rows.Count(e => S(e!["card"]!["status"]) == "open"), ["recorded_count"] = rows.Count(e => S(e!["card"]!["status"]) == "closed"),
                ["forks"] = new JsonArray(page.Select(Clone).ToArray()), ["next_cursor"] = more ? JsonValue.Create($"c{Order(page[^1])}") : null
            };
        }

        // Bearer, audience, scope, in that order: a votes.* token fails at the audience and a desk token without the route's scope fails at the scope.
        private static string AuthJudge(string route, JsonNode? token, bool huginnDown)
        {
            if (token is null || S(token["audience"]) != "bifrost-desk") return "unauthenticated";
            var need = route == "answer" ? "desk.answer" : "desk.read";
            if (!token["scopes"]!.AsArray().Any(x => S(x) == need)) return "insufficient_scope";
            if (!token["active"]!.GetValue<bool>()) return "forbidden";
            return huginnDown ? "unavailable" : "ok";
        }

        private static string PercentDecode(string s)
        {
            var bytes = new List<byte>();
            for (var i = 0; i < s.Length;)
            {
                if (s[i] == '%' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2])) { bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16)); i += 3; continue; }
                var rune = System.Text.Rune.GetRuneAt(s, i);
                bytes.AddRange(Encoding.UTF8.GetBytes(rune.ToString())); i += rune.Utf16SequenceLength;
            }
            return new UTF8Encoding(false, true).GetString(bytes.ToArray());   // throws DecoderFallbackException (an ArgumentException) on invalid UTF-8
        }

        // The {mind} segment is decoded once and compared, ordinally, only with the configured list; nothing is opened from it (fork-card.md, "The {mind} segment").
        private static (string Code, string? Opened, string? StoredIn) MindRoute(JsonNode config, string route, string segment, string? fork, JsonNode? token)
        {
            var auth = AuthJudge(route, token, false);
            if (auth != "ok") return (auth, null, null);
            string mind;
            try { mind = PercentDecode(segment); } catch (ArgumentException) { return ("fork_unknown", null, null); }   // bytes that are not UTF-8 are a miss
            var minds = config["minds"]!.AsObject();
            if (!minds.TryGetPropertyValue(mind, out var socket)) return ("fork_unknown", null, null);
            if (route != "list" && !config["forks"]![mind]!.AsArray().Any(f => S(f) == fork)) return ("fork_unknown", S(socket), null);
            return ("ok", S(socket), route == "answer" ? mind : null);
        }

        [Test]
        public void ForkTransport_MindSegment_IsOnlyComparedWithTheConfiguredList_AnUnknownMindIsTheUnknownForkRefusal()
        {
            var config = Fork["transport"]!["mindRoutes"]!;
            var refusals = Fork["doorRefusals"]!.AsArray().ToDictionary(r => S(r!["name"]), r => r!);
            var byName = new Dictionary<string, (string Code, string? Opened, string? StoredIn)>();
            foreach (var c in config["cases"]!.AsArray())
            {
                var got = MindRoute(config, S(c!["route"]), S(c["mind"]), c["fork_id"] is null ? null : S(c["fork_id"]), c["token"]);
                byName[S(c["name"])] = got;
                Assert.That(got.Code, Is.EqualTo(S(c["expect"])), S(c["name"]));
                Assert.That(got.Opened, Is.EqualTo(c["opened"] is null ? null : S(c["opened"])), $"{S(c["name"])}: the socket opened");
                Assert.That(got.StoredIn, Is.EqualTo(c["stored_in"] is null ? null : S(c["stored_in"])), $"{S(c["name"])}: where an answer was stored");
                Assert.That(c["httpStatus"]!.GetValue<int>(), Is.EqualTo(got.Code == "ok" ? 200 : refusals[got.Code]["httpStatus"]!.GetValue<int>()), S(c["name"]));
                if (got.Code == "fork_unknown")
                {
                    var error = S(refusals["fork_unknown"]["message"]!["error"]);
                    Assert.That(error.Contains(S(c["mind"])), Is.False, $"{S(c["name"])}: the refusal does not name the Mind");
                    Assert.That(c["fork_id"] is not null && error.Contains(S(c["fork_id"])), Is.False, $"{S(c["name"])}: the refusal does not name the fork");
                }
            }
            foreach (var pair in config["sameResponse"]!.AsArray())
            {
                var a = byName[S(pair![0])]; var b = byName[S(pair[1])];
                Assert.That(a.Code, Is.EqualTo(b.Code));
                string Built(string code) => HexOf(new JsonObject { ["schemaVersion"] = "gamecult.fork.refusal.v0", ["code"] = code, ["error"] = $"{code}: {Tails[code]}" });
                Assert.That(Built(a.Code), Is.EqualTo(Built(b.Code)).And.EqualTo(S(refusals[a.Code]["messagePackHex"])), $"{S(pair[0])} and {S(pair[1])} answer with the same bytes, built from the spec's rule");
            }
            Assert.That(byName.Values.Count(v => v.Code == "fork_unknown" && v.Opened is null), Is.GreaterThanOrEqualTo(39), "every bad segment opens nothing");
            Assert.That(byName.Values.Where(v => v.StoredIn is not null).Select(v => v.StoredIn).Distinct().Count(), Is.EqualTo(2), "an answer is stored in the route's Mind only, and both Minds take one");
        }

        [Test]
        public void ForkTransport_RequestsAndEntriesValidateEncodeToTheVectorBytes_AndEveryNegativeIsRefused()
        {
            var t = Fork["transport"]!;
            foreach (var (section, def) in new[] { ("listRequest", "listRequest"), ("readRequest", "readRequest"), ("entry", "entry") })
            {
                foreach (var v in t[section]!["valid"]!.AsArray())
                {
                    Assert.That(Errors(ForkSchema, def, v!["message"]), Is.Empty, S(v["name"]));
                    if (def == "entry") Assert.That(EntryRuleErrors(v["message"]!), Is.Empty, S(v["name"]));
                    Assert.That(HexOf(v["message"]), Is.EqualTo(S(v["messagePackHex"])), S(v["name"]));
                }
                foreach (var v in t[section]!["invalid"]!.AsArray())
                {
                    Assert.That(Errors(ForkSchema, def, v!["message"]).Count > 0 || (def == "entry" && EntryRuleErrors(v["message"]!).Any()), Is.True, $"{S(v["name"])}: {S(v["why"])}");
                    Assert.That(HexOf(v["message"]), Is.EqualTo(S(v["messagePackHex"])), S(v["name"]));
                }
            }
            foreach (var v in t["listInvalid"]!.AsArray())
            {
                var shared = OrderErrors(v!["message"]!["forks"]!.AsArray().Select(f => f!["card"]));
                Assert.That(Errors(ForkSchema, "list", v["message"]).Count > 0 || shared.Count > 0, Is.True, $"{S(v["name"])}: {S(v["why"])}");
                if (v["refusal"] is not null) Assert.That(shared, Is.EqualTo(new[] { S(v["refusal"]) }), S(v["name"]));
                Assert.That(HexOf(v["message"]), Is.EqualTo(S(v["messagePackHex"])), S(v["name"]));
            }
        }

        [Test]
        public void ForkTransport_EveryListRequest_ReplaysToTheVectorsResponse_NewestFirst_PagedByTheCursor()
        {
            foreach (var sc in Fork["transport"]!["listScenarios"]!.AsArray())
            {
                var name = S(sc!["name"]);
                var entries = sc["entries"]!.AsArray();
                foreach (var e in entries) Assert.That(EntryRuleErrors(e!), Is.Empty, name);
                Assert.That(OrderErrors(entries.Select(e => e!["card"])), Is.Empty, name);
                JsonNode? previous = null;
                foreach (var step in sc["steps"]!.AsArray())
                {
                    var response = JudgeList(entries, sc["ordinal"]!.GetValue<long>(), step!["request"]!);
                    Assert.That(JsonNode.DeepEquals(response, step["response"]), Is.True, $"{name}: {response.ToJsonString()}");
                    Assert.That(Errors(ForkSchema, "list", response), Is.Empty, name);
                    Assert.That(HexOf(response), Is.EqualTo(S(step["responseHex"])), name);
                    var orders = response["forks"]!.AsArray().Select(f => f!["card"]!["order"]!.GetValue<long>()).ToList();
                    Assert.That(orders, Is.Ordered.Descending, name);
                    if (previous is not null) Assert.That(S(step["request"]!["cursor"]), Is.EqualTo(S(previous["next_cursor"])), $"{name}: the second request follows the first page's cursor");
                    previous = response;
                }
            }
        }

        [Test]
        public void ForkTransport_OrderZero_IsLegal_NoCursorNamesIt_AndThePageEndingAtOrderOneHandsOnToIt()
        {
            var sc = Fork["transport"]!["listScenarios"]!.AsArray().Single(x => S(x!["name"]) == "order_zero_is_legal_but_no_cursor_names_it")!;
            var steps = sc["steps"]!.AsArray();
            Assert.That(S(steps[0]!["response"]!["next_cursor"]), Is.EqualTo("c1"));
            Assert.That(steps[1]!["response"]!["next_cursor"], Is.Null);
            Assert.That(steps[1]!["response"]!["forks"]!.AsArray().Select(f => f!["card"]!["order"]!.GetValue<long>()), Is.EqualTo(new[] { 0L }));
            Assert.That(Errors(ForkSchema, "cursor", JsonValue.Create("c0")), Is.Not.Empty);
        }

        [Test]
        public void ForkTransport_AShortPage_AndForkTooLarge422_ContinuedBelowItsOrder()
        {
            var sc = Fork["transport"]!["listShortPages"]!.AsArray();
            Assert.That(sc.Select(x => S(x!["name"])), Is.EqualTo(new[] { "a_short_page_resumes_below_its_last_entry", "a_first_entry_past_the_budget_is_fork_too_large_and_the_client_continues_below_its_order", "fork_too_large_on_order_zero_leaves_nothing_below" }));
            var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "contracts", "cultnet", ForkSchema)))!;
            Assert.That(schema["$defs"]!["refusal"]!["properties"]!["code"]!["enum"]!.AsArray().Select(S), Does.Contain("fork_too_large"));
            var covered = new List<long>();
            foreach (var s in sc)
            {
                var entries = s!["entries"]!.AsArray(); var steps = s["steps"]!.AsArray(); string? last = null; var follow = false;
                for (var i = 0; i < steps.Count; i++)
                {
                    var step = steps[i]!; var name = S(s["name"]);
                    if (follow) Assert.That(step["request"]!["cursor"] is null ? null : S(step["request"]!["cursor"]), Is.EqualTo(last), name);
                    var full = JudgeList(entries, s["ordinal"]!.GetValue<long>(), step["request"]!);
                    var fullForks = full["forks"]!.AsArray();
                    if (step["response"] is not null)
                    {
                        var r = step["response"]!; var forks = r["forks"]!.AsArray(); var n = forks.Count;
                        Assert.That(Errors(ForkSchema, "list", r), Is.Empty, name);
                        Assert.That(HexOf(r), Is.EqualTo(S(step["responseHex"])), name);
                        Assert.That(n, Is.GreaterThanOrEqualTo(1), name);
                        for (var k = 0; k < n; k++) Assert.That(JsonNode.DeepEquals(forks[k], fullForks[k]), Is.True, $"{name}: a prefix of the unbudgeted page");
                        var o = forks[n - 1]!["card"]!["order"]!.GetValue<long>();
                        if (full["next_cursor"] is not null || n < fullForks.Count) Assert.That(S(r["next_cursor"]), Is.EqualTo($"c{o}"), name); else Assert.That(r["next_cursor"], Is.Null, name);
                        Assert.That(S(r["stamp"]), Is.EqualTo(S(full["stamp"]))); Assert.That(r["open_count"]!.GetValue<int>(), Is.EqualTo(full["open_count"]!.GetValue<int>())); Assert.That(r["recorded_count"]!.GetValue<int>(), Is.EqualTo(full["recorded_count"]!.GetValue<int>()));
                        covered.AddRange(forks.Select(f => f!["card"]!["order"]!.GetValue<long>()));
                        last = r["next_cursor"] is null ? null : S(r["next_cursor"]); follow = true;
                    }
                    else
                    {
                        var r = step["refusal"]!; var first = fullForks[0]!["card"]!;
                        Assert.That(Errors(ForkSchema, "refusal", r), Is.Empty, name);
                        Assert.That(HexOf(r), Is.EqualTo(S(step["refusalHex"])), name);
                        Assert.That(S(r["code"]), Is.EqualTo("fork_too_large")); Assert.That(step["httpStatus"]!.GetValue<int>(), Is.EqualTo(422));
                        Assert.That(S(step["fork_id"]), Is.EqualTo(S(first["fork_id"]))); Assert.That(step["order"]!.GetValue<long>(), Is.EqualTo(first["order"]!.GetValue<long>()));
                        Assert.That(S(r["error"]), Is.EqualTo($"fork_too_large: fork {S(first["fork_id"])} (order {first["order"]!.GetValue<long>()}) alone is past the response budget"));
                        Assert.That(step["request"]!["limit"]!.GetValue<int>(), Is.GreaterThanOrEqualTo(1));
                        var order = first["order"]!.GetValue<long>();
                        if (order >= 1) { Assert.That(i + 1, Is.LessThan(steps.Count), name); last = $"c{order}"; follow = true; Assert.That(Errors(ForkSchema, "cursor", JsonValue.Create(last)), Is.Empty); }
                        else { Assert.That(i, Is.EqualTo(steps.Count - 1), $"{name}: order 0 is the end"); follow = false; }
                    }
                }
            }
            Assert.That(covered, Is.EqualTo(new long[] { 3, 2, 1, 0, 2, 1, 0, 1 }));
            Assert.That(sc[0]!["steps"]!.AsArray().Select(x => x!["response"]!["forks"]!.AsArray().Count), Is.EqualTo(new[] { 2, 2 }));
        }

        [Test]
        public void ForkTransport_AReadWhoseEntryIsPastTheCap_IsForkTooLarge422_NamedByForkIdAlone()
        {
            var doorStatus = Fork["doorRefusals"]!.AsArray().Single(r => S(r!["name"]) == "fork_too_large")!["httpStatus"]!.GetValue<int>();
            var cases = Fork["transport"]!["readTooLarge"]!.AsArray();
            Assert.That(cases.Count, Is.EqualTo(1));
            foreach (var c in cases)
            {
                var held = c!["entries"]!.AsArray().Single(e => S(e!["card"]!["fork_id"]) == S(c["request"]!["fork_id"]))!;
                Assert.That(Errors(ForkSchema, "readRequest", c["request"]), Is.Empty, S(c["name"]));
                Assert.That(Errors(ForkSchema, "refusal", c["refusal"]), Is.Empty, S(c["name"]));
                Assert.That(S(c["refusal"]!["code"]), Is.EqualTo("fork_too_large"));
                Assert.That(S(c["refusal"]!["error"]), Is.EqualTo($"fork_too_large: fork {S(held["card"]!["fork_id"])} alone is past the response budget"));
                Assert.That(S(c["refusal"]!["error"]), Does.Not.Contain("order"), "a read names no order");
                Assert.That(HexOf(c["refusal"]), Is.EqualTo(S(c["refusalHex"])), S(c["name"]));
                Assert.That(c["httpStatus"]!.GetValue<int>(), Is.EqualTo(422)); Assert.That(c["httpStatus"]!.GetValue<int>(), Is.EqualTo(doorStatus));
            }
        }

        [Test]
        public void ForkTransport_AStaleAnswerWhoseCardRereadIsPastTheCap_IsTheReadFormForkTooLarge422_NothingStored()
        {
            var cases = Fork["transport"]!["answerTooLarge"]!.AsArray();
            Assert.That(cases.Count, Is.EqualTo(1));
            var read = Fork["transport"]!["readTooLarge"]![0]!;
            foreach (var c in cases)
            {
                var held = c!["entries"]!.AsArray().Single(e => S(e!["card"]!["fork_id"]) == S(c["request"]!["fork_id"]))!;
                Assert.That(Errors(ForkSchema, "answerRequest", c["request"]), Is.Empty, S(c["name"]));
                Assert.That(S(c["request"]!["options_hash"]), Is.Not.EqualTo(S(held["card"]!["options_hash"])), "the card moved: the answer is stale");
                Assert.That(Errors(ForkSchema, "refusal", c["refusal"]), Is.Empty, S(c["name"]));
                Assert.That(S(c["refusal"]!["code"]), Is.EqualTo("fork_too_large"));
                Assert.That(S(c["refusal"]!["error"]), Is.EqualTo($"fork_too_large: fork {S(held["card"]!["fork_id"])} alone is past the response budget"));
                Assert.That(HexOf(c["refusal"]), Is.EqualTo(S(c["refusalHex"])), S(c["name"]));
                Assert.That(S(c["refusalHex"]), Is.EqualTo(S(read["refusalHex"])), "the same document as the read form");
                Assert.That(c["httpStatus"]!.GetValue<int>(), Is.EqualTo(422));
                Assert.That(c["httpStatus"]!.GetValue<int>(), Is.EqualTo(Fork["doorRefusals"]!.AsArray().Single(r => S(r!["name"]) == "fork_too_large")!["httpStatus"]!.GetValue<int>()));
                Assert.That(c["stored"]!.GetValue<bool>(), Is.False);
            }
        }

        [Test]
        public void ForkTransport_ADoorRequestIdConflict_IsAFaultOnReadAndList_AndARefusalOnlyOnTheAnswer()
        {
            var cases = Fork["transport"]!["doorFaults"]!.AsArray();
            Assert.That(cases.Select(c => S(c!["route"])), Is.EqualTo(new[] { "read", "list", "answer" }));
            foreach (var c in cases)
            {
                var source = S(c!["route"]) == "answer" ? Fork["doorRefusals"]!.AsArray().Single(r => S(r!["name"]) == S(c["doorCode"]))! : Fork["unavailable"]![0]!;
                Assert.That(S(c["responseHex"]), Is.EqualTo(S(source["messagePackHex"])), S(c["name"]));
                Assert.That(HexOf(c["response"]), Is.EqualTo(S(c["responseHex"])), S(c["name"]));
                Assert.That(c["httpStatus"]!.GetValue<int>(), Is.EqualTo(source["httpStatus"]!.GetValue<int>()), S(c["name"]));
                Assert.That(Errors(ForkSchema, S(c["route"]) == "answer" ? "refusal" : "unavailable", c["response"]), Is.Empty, S(c["name"]));
            }
        }

        [Test]
        public void ForkTransport_Auth_BearerThenAudienceThenScope_AVotesTokenTouchesNothing()
        {
            var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "contracts", "cultnet", ForkSchema)))!;
            var codes = schema["$defs"]!["refusal"]!["properties"]!["code"]!["enum"]!.AsArray().Select(S).ToList();
            var seen = new HashSet<string>();
            foreach (var c in Fork["transport"]!["authCases"]!.AsArray())
            {
                var got = AuthJudge(S(c!["route"]), c["token"], c["huginn_down"]!.GetValue<bool>());
                Assert.That(got, Is.EqualTo(S(c["expect"])), S(c["name"]));
                if (got is not ("ok" or "unavailable")) Assert.That(codes, Does.Contain(got), S(c["name"]));
                seen.Add(S(c["route"]) + ":" + got);
            }
            Assert.That(seen, Does.Contain("list:insufficient_scope").And.Contain("answer:insufficient_scope").And.Contain("read:forbidden").And.Contain("list:unavailable").And.Contain("read:unavailable").And.Contain("answer:unavailable").And.Contain("read:unauthenticated").And.Contain("answer:ok"));
        }
    }
}
