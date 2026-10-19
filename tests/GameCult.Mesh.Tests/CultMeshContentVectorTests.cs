using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using GameCult.Caching;
using GameCult.Networking;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Mesh.Tests;

/// <summary>
/// Content-plane parity vectors shared with <c>packages/cultnet-rs/tests/content.rs</c>. Each runtime
/// writes its own file under <c>contracts/cultmesh</c> and judges both. The chunk answers in these
/// files are produced by <see cref="CultMeshLegacyRudpContentServer"/> itself, so the failure
/// spellings are the reference's own bytes.
/// </summary>
[TestFixture]
public sealed class CultMeshContentVectorTests
{
    private const int BodyLength = 1_315_551;
    private const int ChunkSize = 262_144;
    private const string CreatedAtUtc = "2026-09-22T00:00:00.0000000+00:00";
    private const string ArtifactId = "eureka/content-vector";

    private sealed record Vector(string Label, string Kind, byte[] Bytes);

    [Test]
    public void WriteContentVectors()
    {
        if (Environment.GetEnvironmentVariable("CULTNET_WRITE_VECTORS") != "1")
            Assert.Ignore("Set CULTNET_WRITE_VECTORS=1 to (re)write the committed vector file.");
        File.WriteAllText(VectorPath("content-vectors.cs-written.json"), ToJson(BuildVectors(out _, out _)));
        File.WriteAllText(VectorPath("cdn-record-key-vectors.json"), RecordKeyVectorsToJson(BuildRecordKeyVectors()));
        File.WriteAllText(VectorPath("content-acceptance-vectors.json"), AcceptanceVectorsToJson(BuildAcceptanceVectors()));
    }

    [Test]
    public void CommittedCsVectorsEqualAFreshEncode()
    {
        var fresh = BuildVectors(out _, out _);
        var committed = ReadVectors("content-vectors.cs-written.json");
        committed.Select(v => (v.Label, v.Kind)).Should().Equal(fresh.Select(v => (v.Label, v.Kind)));
        foreach (var (expected, actual) in fresh.Zip(committed))
            actual.Bytes.Should().Equal(expected.Bytes, $"vector '{actual.Label}' drifted from the reference's encode");
    }

    [Test]
    public void RustVectorsDecodeInTheReference()
    {
        BuildVectors(out var cache, out var messages);
        var rust = ReadVectors("content-vectors.rs-written.json");
        rust.Should().NotBeEmpty();
        rust.Select(v => v.Label).Should().Equal(messages.Keys, "both runtimes write the same labelled vectors");
        foreach (var vector in rust)
        {
            var expected = messages[vector.Label];
            switch (vector.Kind)
            {
                case "request":
                    var request = MessagePackSerializer.Deserialize<CultMeshContentChunkRequestMessage>(vector.Bytes, CultNetSchemaMessageSerialization.Options);
                    request.Should().BeEquivalentTo((CultMeshContentChunkRequestMessage)expected);
                    Reencode(request).Should().Equal(vector.Bytes, vector.Label);
                    break;
                case "response":
                    var response = MessagePackSerializer.Deserialize<CultMeshContentChunkResponseMessage>(vector.Bytes, CultNetSchemaMessageSerialization.Options);
                    response.Should().BeEquivalentTo((CultMeshContentChunkResponseMessage)expected);
                    Reencode(response).Should().Equal(vector.Bytes, vector.Label);
                    break;
                case "manifest":
                    var manifest = MessagePackSerializer.Deserialize<CultMeshCdnArtifactManifest>(vector.Bytes, CultNetSchemaMessageSerialization.Options);
                    manifest.Should().BeEquivalentTo((CultMeshCdnArtifactManifest)expected);
                    Reencode(manifest).Should().Equal(vector.Bytes, vector.Label);
                    break;
                default:
                    Assert.Fail($"unknown vector kind '{vector.Kind}'");
                    break;
            }
        }

        // Rust's answers to Rust's requests must be what the reference answers to the same bytes.
        foreach (var vector in rust.Where(v => v.Label.EndsWith(".request", StringComparison.Ordinal)))
        {
            var request = MessagePackSerializer.Deserialize<CultMeshContentChunkRequestMessage>(vector.Bytes, CultNetSchemaMessageSerialization.Options);
            var rustAnswer = rust.Single(v => v.Label == vector.Label[..^".request".Length] + ".response");
            Reencode(Answer(cache, request)).Should().Equal(rustAnswer.Bytes, vector.Label);
        }
    }

    [Test]
    public void PackArtifactOfTheFixtureBodyEqualsTheFixtureManifest()
    {
        var artifact = CultMeshCdn.PackArtifact(ArtifactId, Body(), Options());
        var committed = ReadVectors("content-vectors.cs-written.json").Single(v => v.Label == "manifest");
        Reencode(artifact.Manifest).Should().Equal(committed.Bytes);
        var rust = ReadVectors("content-vectors.rs-written.json").Single(v => v.Label == "manifest");
        rust.Bytes.Should().Equal(committed.Bytes, "Rust's pack_content and the reference pack the same bytes to the same manifest");
    }

    [Test]
    public void CommittedRecordKeyVectorsEqualAFreshDerivation()
    {
        var path = VectorPath("cdn-record-key-vectors.json");
        File.Exists(path).Should().BeTrue($"{path} is written by this fixture under CULTNET_WRITE_VECTORS=1");
        Lines(File.ReadAllText(path)).Should().Be(Lines(RecordKeyVectorsToJson(BuildRecordKeyVectors())), "the key grammar drifted from the committed vectors");
    }

    [Test]
    public void TypeScriptManifestVectorsMatchTheReference()
    {
        var path = VectorPath("content-vectors.ts-written.json");
        File.Exists(path).Should().BeTrue($"{path} is written by scripts/write-cultmesh-content-vectors.mjs");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var vectors = document.RootElement.GetProperty("vectors").EnumerateArray().ToList();
        vectors.Should().NotBeEmpty();
        foreach (var vector in vectors)
        {
            var label = vector.GetProperty("label").GetString()!;
            var bytes = Convert.FromBase64String(vector.GetProperty("messagePackBase64").GetString()!);
            var manifest = MessagePackSerializer.Deserialize<CultMeshCdnArtifactManifest>(bytes, CultNetSchemaMessageSerialization.Options);
            Reencode(manifest).Should().Equal(bytes, $"{label}: the reference reads and rewrites the TypeScript bytes unchanged");
            CultMeshCdnArtifactManifest.CreateRecordKey(manifest).Value.Should().Be(vector.GetProperty("recordKey").GetString(), label);

            var input = vector.GetProperty("input");
            var body = input.GetProperty("body");
            var multiplier = body.GetProperty("multiplier").GetInt64();
            var modulus = body.GetProperty("modulus").GetInt64();
            var packed = CultMeshCdn.PackArtifact(
                input.GetProperty("artifactId").GetString()!,
                Enumerable.Range(0, body.GetProperty("length").GetInt32()).Select(i => (byte)(i * multiplier % modulus)).ToArray(),
                new CultMeshCdnPackOptions
                {
                    Kind = input.GetProperty("kind").GetString()!,
                    Version = input.GetProperty("version").GetString()!,
                    MimeType = input.GetProperty("mimeType").GetString()!,
                    CreatedAtUtc = input.GetProperty("createdAtUtc").GetString()!,
                    ChunkSizeBytes = input.GetProperty("chunkSizeBytes").GetInt32(),
                    Tags = input.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!).ToArray(),
                    Metadata = input.GetProperty("metadata").EnumerateArray()
                        .Aggregate(new Dictionary<string, string>(), (map, pair) =>
                        {
                            map[pair[0].GetString()!] = pair[1].GetString()!;
                            return map;
                        })
                });
            Reencode(packed.Manifest).Should().Equal(bytes, $"{label}: the reference packs the declared input to the TypeScript bytes");
        }
    }

    [Test]
    public void CommittedAcceptanceVectorsEqualAFreshRun()
    {
        var path = VectorPath("content-acceptance-vectors.json");
        File.Exists(path).Should().BeTrue($"{path} is written by this fixture under CULTNET_WRITE_VECTORS=1");
        var fresh = BuildAcceptanceVectors();
        fresh.Select(c => c.Label).Should().OnlyHaveUniqueItems();
        fresh.Count(c => c.Accepted).Should().BeGreaterThan(30);
        fresh.Count(c => !c.Accepted).Should().BeGreaterThan(30);
        Lines(File.ReadAllText(path)).Should().Be(Lines(AcceptanceVectorsToJson(fresh)), "the reference's acceptance drifted from the committed vectors");
    }

    private sealed record Acceptance(string Label, byte[] Input, bool Accepted, byte[]? Reencoded, string? RecordKey);

    /// <summary>What the reference does with these bytes: deserialise, then <c>ValidateManifest</c>; accepted bytes are rewritten and keyed.</summary>
    private static Acceptance Judge(string label, byte[] input)
    {
        CultMeshCdnArtifactManifest manifest;
        try
        {
            manifest = MessagePackSerializer.Deserialize<CultMeshCdnArtifactManifest>(input, CultNetSchemaMessageSerialization.Options);
            CultMeshCdn.ValidateManifest(manifest);
        }
        catch (Exception)
        {
            return new Acceptance(label, input, false, null, null);
        }

        string? key = null;
        try { key = CultMeshCdnArtifactManifest.CreateRecordKey(manifest).Value; }
        catch (ArgumentException) { }
        return new Acceptance(label, input, true, Reencode(manifest), key);
    }

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    private static byte[] Raw(params byte[] bytes) => bytes;
    private static readonly byte[] Nil = { 0xc0 };
    private static byte[] Str(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return Cat(bytes.Length < 32 ? Raw((byte)(0xa0 | bytes.Length)) : Raw(0xd9, (byte)bytes.Length), bytes);
    }
    private static byte[] StrRaw(params byte[] bytes) => Cat(Raw((byte)(0xa0 | bytes.Length)), bytes);
    private static byte[] Arr(params byte[][] items) => Cat(Raw((byte)(0x90 | items.Length)), Cat(items));
    private static byte[] MapOf(params byte[][] pairs) => Cat(Raw((byte)(0x80 | (pairs.Length / 2))), Cat(pairs));
    private static byte[] Be(byte code, long value, int width)
    {
        var bytes = new byte[width];
        for (var i = 0; i < width; i++) bytes[width - 1 - i] = (byte)(value >> (8 * i));
        return Cat(Raw(code), bytes);
    }
    private static byte[] F64(long bits) => Be(0xcb, bits, 8);
    private static byte[] F32(int bits) => Be(0xca, bits, 4);

    private static byte[][] BaseFields() => new[]
    {
        Str("a/b"), Str("asset"), Str("1"), Str(EmptyHash), Raw(0x00), Str("application/x-t"), Str("t"),
        Arr(Chunk(Raw(0x00), Raw(0x00))), Arr(Str("x")), MapOf(Str("k"), Str("v"))
    };

    private const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private static byte[] Chunk(byte[] offset, byte[] size) => Arr(Str(EmptyHash), offset, size, Str("k"));

    private static byte[] Doc(IEnumerable<byte[]> fields)
    {
        var list = fields.ToArray();
        return Cat(Raw((byte)(0x90 | list.Length)), Cat(list));
    }

    private static byte[][] With(params (int Index, byte[] Value)[] edits)
    {
        var fields = BaseFields();
        foreach (var (index, value) in edits) fields[index] = value;
        return fields;
    }

    /// <summary>
    /// Byte shapes at the edge of what MessagePack-CSharp reads: nil in reference slots, missing and extra slots,
    /// trailing bytes, integer and float families, string families and invalid UTF-8. The reference judges each one;
    /// the TypeScript decoder is held to the verdict and to the bytes the reference writes back.
    /// </summary>
    private static List<Acceptance> BuildAcceptanceVectors()
    {
        var cases = new List<Acceptance>();
        void Add(string label, byte[] input) => cases.Add(Judge(label, input));
        void AddFields(string label, params (int Index, byte[] Value)[] edits) => Add(label, Doc(With(edits)));
        var fields = BaseFields();

        Add("base", Doc(fields));
        for (var count = 0; count <= 9; count++) Add($"short_{count}", Doc(fields.Take(count)));
        Add("array16_header", Cat(Raw(0xdc, 0, 10), Cat(fields)));
        Add("array32_header", Cat(Raw(0xdd, 0, 0, 0, 10), Cat(fields)));
        Add("extra_11_nil", Doc(fields.Append(Nil)));
        Add("extra_11_str", Doc(fields.Append(Str("x"))));
        Add("extra_11_nested", Doc(fields.Append(Arr(Arr(Str("x")), MapOf(Str("a"), F64(0))))));
        Add("extra_11_ext", Doc(fields.Append(Raw(0xd4, 1, 2))));
        Add("extra_11_ext8", Doc(fields.Append(Raw(0xc7, 2, 1, 7, 7))));
        Add("extra_11_bin", Doc(fields.Append(Raw(0xc4, 2, 1, 2))));
        Add("extra_11_float", Doc(fields.Append(F32(0))));
        Add("extra_11_uint64", Doc(fields.Append(Be(0xcf, 7, 8))));
        Add("extra_11_array16", Doc(fields.Append(Cat(Raw(0xdc, 0, 2), Raw(1), Raw(2)))));
        Add("extra_11_map16", Doc(fields.Append(Cat(Raw(0xde, 0, 1), Raw(1), Raw(2)))));
        Add("extra_11_str8", Doc(fields.Append(Raw(0xd9, 1, 0x41))));
        Add("extra_12", Doc(fields.Append(Nil).Append(Str("y"))));
        Add("extra_11_reserved_code", Doc(fields.Append(Raw(0xc1))));
        Add("extra_11_truncated", Doc(fields.Append(Raw(0x92, 1))));

        AddFields("chunk_3", (7, Arr(Arr(Str(EmptyHash), Raw(0), Raw(0)))));
        AddFields("chunk_5", (7, Arr(Arr(Str(EmptyHash), Raw(0), Raw(0), Str("k"), Nil))));
        AddFields("chunk_5_nested", (7, Arr(Arr(Str(EmptyHash), Raw(0), Raw(0), Str("k"), Arr(Raw(1), Raw(2))))));
        AddFields("chunk_1", (7, Arr(Arr(Str(EmptyHash)))));
        AddFields("chunk_0", (7, Arr(Arr())));
        AddFields("chunk_is_map", (7, Arr(MapOf(Str("a"), Str("b")))));
        AddFields("chunk_is_str", (7, Arr(Str("a"))));
        AddFields("chunks_empty_with_size", (7, Arr()));
        AddFields("chunks_empty_size_zero", (7, Arr()), (4, Raw(0)));
        AddFields("chunks_is_str", (7, Str("a")));
        AddFields("chunks_is_map", (7, MapOf()));
        AddFields("chunks_gap", (7, Arr(Chunk(Raw(1), Raw(0)))));

        foreach (var (index, name) in new[] { (0, "artifactId"), (1, "kind"), (2, "version"), (3, "contentHash"), (5, "mimeType"), (6, "createdAtUtc") })
            AddFields($"nil_{name}", (index, Nil));
        AddFields("nil_chunks", (7, Nil));
        AddFields("nil_chunk_element", (7, Arr(Nil)));
        AddFields("nil_chunk_hash", (7, Arr(Arr(Nil, Raw(0), Raw(0), Str("k")))));
        AddFields("nil_chunk_recordKey", (7, Arr(Arr(Str(EmptyHash), Raw(0), Raw(0), Nil))));
        AddFields("nil_tags", (8, Nil));
        AddFields("nil_tag_element", (8, Arr(Nil, Str("x"))));
        AddFields("nil_metadata", (9, Nil));
        AddFields("nil_metadata_value", (9, MapOf(Str("k"), Nil)));
        AddFields("nil_metadata_key", (9, MapOf(Nil, Str("v"))));
        AddFields("nil_size", (4, Nil));

        AddFields("metadata_duplicate_key", (9, MapOf(Str("k"), Str("1"), Str("k"), Str("2"))));
        AddFields("metadata_integer_like_keys", (9, MapOf(Str("10"), Str("a"), Str("alpha"), Str("b"), Str("2"), Str("c"))));
        AddFields("metadata_int_key", (9, MapOf(Raw(5), Str("v"))));
        AddFields("metadata_int_value", (9, MapOf(Str("k"), Raw(5))));
        AddFields("metadata_empty_key", (9, MapOf(Str(""), Str("v"))));
        AddFields("metadata_proto_key", (9, MapOf(Str("__proto__"), Str("v"))));
        AddFields("metadata_map16", (9, Cat(Raw(0xde, 0, 1), Str("k"), Str("v"))));
        AddFields("metadata_map32", (9, Cat(Raw(0xdf, 0, 0, 0, 1), Str("k"), Str("v"))));
        AddFields("metadata_is_array", (9, Arr()));
        AddFields("metadata_is_str", (9, Str("k")));
        AddFields("tags_is_map", (8, MapOf()));
        AddFields("tags_int_element", (8, Arr(Raw(5))));
        AddFields("tags_array16", (8, Cat(Raw(0xdc, 0, 1), Str("x"))));

        Add("trailing_nil", Cat(Doc(fields), Nil));
        Add("trailing_reserved_code", Cat(Doc(fields), Raw(0xc1)));
        Add("trailing_truncated_array", Cat(Doc(fields), Raw(0x92)));
        Add("top_nil", Nil);
        Add("top_empty", Array.Empty<byte>());
        Add("top_map", MapOf(Str("a"), Str("b")));
        Add("top_str", Str("a"));
        Add("top_int", Raw(5));
        Add("top_truncated", Doc(fields)[..^3]);

        foreach (var (name, zero) in new (string, byte[])[]
                 {
                     ("u8", Raw(0xcc, 0)), ("u16", Be(0xcd, 0, 2)), ("u32", Be(0xce, 0, 4)), ("u64", Be(0xcf, 0, 8)),
                     ("i8", Raw(0xd0, 0)), ("i16", Be(0xd1, 0, 2)), ("i32", Be(0xd2, 0, 4)), ("i64", Be(0xd3, 0, 8)),
                     ("f32", F32(0)), ("f32_negzero", F32(int.MinValue)), ("f64", F64(0)), ("f64_negzero", F64(long.MinValue)),
                     ("f64_one", F64(0x3FF0000000000000)),
                     ("true", Raw(0xc3)), ("false", Raw(0xc2)), ("str", Str("0")), ("bin", Raw(0xc4, 1, 0)), ("array", Arr()), ("ext", Raw(0xd4, 1, 0))
                 })
        {
            AddFields($"size_{name}", (4, zero));
            AddFields($"offset_{name}", (7, Arr(Chunk(zero, Raw(0)))));
            AddFields($"chunksize_{name}", (7, Arr(Chunk(Raw(0), zero))));
        }

        AddFields("size_negative", (4, Raw(0xff)), (7, Arr()));
        AddFields("size_u64_over_long", (4, Be(0xcf, -1, 8)), (7, Arr()));
        AddFields("size_i64_min", (4, Be(0xd3, long.MinValue, 8)), (7, Arr()));
        AddFields("size_2p53_minus_1", (4, Be(0xcf, (1L << 53) - 1, 8)), (7, Arr()));
        AddFields("chunksize_int_max", (4, Be(0xce, int.MaxValue, 4)), (7, Arr(Chunk(Raw(0), Be(0xce, int.MaxValue, 4)))));
        AddFields("chunksize_2p31", (4, Be(0xce, 1L << 31, 4)), (7, Arr(Chunk(Raw(0), Be(0xce, 1L << 31, 4)))));
        AddFields("chunksize_i32_min", (4, Raw(0)), (7, Arr(Chunk(Raw(0), Be(0xd2, int.MinValue, 4)))));
        AddFields("chunksize_i64_below_i32", (4, Raw(0)), (7, Arr(Chunk(Raw(0), Be(0xd3, (long)int.MinValue - 1, 8)))));
        AddFields("offset_beyond_int32",
            (4, Be(0xcf, 3L * int.MaxValue, 8)),
            (7, Arr(Chunk(Raw(0), Be(0xce, int.MaxValue, 4)), Chunk(Be(0xce, int.MaxValue, 4), Be(0xce, int.MaxValue, 4)),
                Chunk(Be(0xce, 2L * int.MaxValue, 4), Be(0xce, int.MaxValue, 4)))));
        AddFields("chunks_out_of_order",
            (4, Raw(2)), (7, Arr(Chunk(Raw(1), Raw(1)), Chunk(Raw(0), Raw(1)))));
        AddFields("chunks_overlap", (4, Raw(2)), (7, Arr(Chunk(Raw(0), Raw(2)), Chunk(Raw(1), Raw(1)))));
        AddFields("chunk_negative_size", (4, Raw(0)), (7, Arr(Chunk(Raw(0), Raw(0xff)))));

        AddFields("str_bin", (1, Raw(0xc4, 1, 0x41)));
        AddFields("str_ext", (1, Raw(0xd4, 1, 0x41)));
        AddFields("str_int", (1, Raw(0x41)));
        AddFields("str_str16", (1, Raw(0xda, 0, 1, 0x41)));
        AddFields("str_str32", (1, Raw(0xdb, 0, 0, 0, 1, 0x41)));
        AddFields("str_empty", (1, Str("")));
        AddFields("str_invalid_utf8", (1, StrRaw(0xFF, 0xFE)));
        AddFields("str_lone_surrogate_cesu", (1, StrRaw(0xED, 0xA0, 0x80)));
        AddFields("str_overlong", (1, StrRaw(0xC0, 0xAF)));
        AddFields("str_truncated_sequence", (1, StrRaw(0xE2, 0x82)));
        AddFields("str_bom", (0, Str("﻿bom")));
        AddFields("str_nul", (0, StrRaw(0x61, 0x00, 0x62)));
        AddFields("id_invalid_utf8", (0, StrRaw(0xFF)));
        AddFields("hash_invalid_utf8", (3, StrRaw(0xFF)));
        AddFields("version_invalid_utf8", (2, StrRaw(0xFF, 0xFE)));
        AddFields("id_blank", (0, Str("  ")));
        AddFields("hash_blank", (3, Str("  ")));
        AddFields("hash_prefix_upper_long_s", (3, Str("ſha256:abc")));
        return cases;
    }

    private static string AcceptanceVectorsToJson(List<Acceptance> cases) =>
        JsonSerializer.Serialize(
            new
            {
                cases = cases.Select(c => new
                {
                    label = c.Label,
                    inputBase64 = Convert.ToBase64String(c.Input),
                    accepted = c.Accepted,
                    reencodedBase64 = c.Reencoded == null ? null : Convert.ToBase64String(c.Reencoded),
                    recordKey = c.RecordKey
                })
            },
            new JsonSerializerOptions { WriteIndented = true });

    private static byte[] Body() =>
        Enumerable.Range(0, BodyLength).Select(i => (byte)(i % 251)).ToArray();

    private static CultMeshCdnPackOptions Options() => new()
    {
        ChunkSizeBytes = ChunkSize,
        Version = "1",
        MimeType = "application/x-cultnet-vector",
        CreatedAtUtc = CreatedAtUtc
    };

    private static byte[] Reencode(CultMeshContentChunkRequestMessage message) =>
        CultNetSchemaMessageSerialization.Serialize(message);

    private static byte[] Reencode(CultMeshContentChunkResponseMessage message) =>
        CultNetSchemaMessageSerialization.Serialize(message);

    private static byte[] Reencode(CultMeshCdnArtifactManifest manifest) =>
        MessagePackSerializer.Serialize(manifest, CultNetSchemaMessageSerialization.Options);

    private static CultMeshContentChunkRequestMessage Request(string id, string hash, string key, int size) => new()
    {
        MessageId = id,
        ChunkHash = hash,
        RecordKey = key,
        ExpectedSizeBytes = size
    };

    /// <summary>The cs-written file's vectors, in file order. Messages are keyed by label.</summary>
    private static List<Vector> BuildVectors(out CultCache cache, out Dictionary<string, object> messages)
    {
        var artifact = CultMeshCdn.PackArtifact(ArtifactId, Body(), Options());
        var content = new CultCache(CultMesh.CreateCultCacheDocumentRegistry(
            typeof(CultMeshCdnArtifactManifest), typeof(CultMeshCdnArtifactChunk)));
        CultMeshCdn.PublishAsync(content, artifact).GetAwaiter().GetResult();
        cache = content;

        var last = artifact.Chunks[^1];
        var key = "mesh:cdn:chunk:" + last.ChunkHash;
        var lastDigit = last.ChunkHash[^1] == '0' ? '1' : '0';
        var vectors = new List<Vector>();
        var map = new Dictionary<string, object>(StringComparer.Ordinal);
        messages = map;

        void AddRequest(string label, CultMeshContentChunkRequestMessage message)
        {
            map[label] = message;
            vectors.Add(new Vector(label, "request", Reencode(message)));
        }

        void AddResponse(string label, CultMeshContentChunkResponseMessage message)
        {
            map[label] = message;
            vectors.Add(new Vector(label, "response", Reencode(message)));
        }

        void AddAnswer(string name, CultMeshContentChunkRequestMessage request)
        {
            AddRequest(name + ".request", request);
            AddResponse(name + ".response", Answer(content, request));
        }

        AddAnswer("answer_found", Request("vector-found", "SHA256:" + last.ChunkHash, key, last.SizeBytes));
        AddAnswer("answer_not_found", Request("vector-missing", new string('0', 64), string.Empty, 100));
        AddAnswer("answer_record_key_disagrees", Request("vector-key", last.ChunkHash, "mesh:cdn:chunk:other", last.SizeBytes));
        AddAnswer("answer_size_mismatch", Request("vector-size", last.ChunkHash, key, last.SizeBytes - 1));
        // Requests the reference refuses in its handler, never at deserialisation: each is answered.
        AddAnswer("answer_blank_message_id", Request(string.Empty, last.ChunkHash, key, last.SizeBytes));
        AddAnswer("answer_whitespace_message_id", Request("   ", last.ChunkHash, key, last.SizeBytes));
        AddAnswer("answer_blank_hash", Request("vector-blank-hash", string.Empty, string.Empty, 100));
        AddAnswer("answer_whitespace_hash", Request("vector-space-hash", "   ", string.Empty, 100));
        AddAnswer("answer_prefix_only_hash", Request("vector-prefix-hash", "sha256:   ", string.Empty, 100));
        AddAnswer("answer_negative_size", Request("vector-negative", last.ChunkHash, key, -1));
        AddAnswer("answer_record_key_case", Request("vector-key-case", last.ChunkHash, key.ToUpperInvariant(), last.SizeBytes));
        AddAnswer("answer_blank_record_key", Request("vector-key-blank", last.ChunkHash, "   ", last.SizeBytes));
        AddAnswer("answer_double_prefix", Request("vector-double", "sha256:sha256:" + last.ChunkHash, string.Empty, last.SizeBytes));

        // A caller that leaves a string null writes msgpack nil; the reference reads it back as null and
        // answers. These are judged by Rust decoding the reference's bytes (Rust never writes nil), so
        // they stay out of the label-matched mirror set.
        void AddNilAnswer(string name, CultMeshContentChunkRequestMessage request)
        {
            vectors.Add(new Vector(name + ".request", "request", Reencode(request)));
            vectors.Add(new Vector(name + ".response", "response", Reencode(Answer(content, request))));
        }

        AddNilAnswer("nil_message_id", Request(null, last.ChunkHash, key, last.SizeBytes));
        AddNilAnswer("nil_hash", Request("vector-nil-hash", null, string.Empty, 100));
        AddNilAnswer("nil_record_key", Request("vector-nil-key", new string('0', 64), null, 100));
        AddResponse("response_hash_last_digit", new CultMeshContentChunkResponseMessage
        {
            MessageId = "vector-digit",
            Found = true,
            ChunkHash = last.ChunkHash[..^1] + lastDigit,
            SizeBytes = last.SizeBytes,
            Payload = last.Payload,
            Error = string.Empty
        });

        void AddManifest(string label, CultMeshCdnArtifactManifest manifest)
        {
            map[label] = manifest;
            vectors.Add(new Vector(label, "manifest", Reencode(manifest)));
        }

        AddManifest("manifest", artifact.Manifest);
        var shuffled = CultMeshCdn.PackArtifact(ArtifactId, Body(), Options()).Manifest;
        shuffled.Chunks = new[] { 3, 0, 5, 1, 4, 2 }.Select(i => shuffled.Chunks[i]).ToArray();
        AddManifest("manifest_out_of_order", shuffled);
        var tagged = CultMeshCdn.PackArtifact(ArtifactId, Body(), Options()).Manifest;
        tagged.Metadata = new Dictionary<string, string> { ["zeta"] = "1", ["alpha"] = "2", ["mid"] = "3" };
        AddManifest("manifest_metadata_order", tagged);
        AddManifest("manifest_empty_body", CultMeshCdn.PackArtifact(ArtifactId, Array.Empty<byte>(), Options()).Manifest);
        return vectors;
    }

    private static CultMeshContentChunkResponseMessage Answer(CultCache cache, CultMeshContentChunkRequestMessage request)
    {
        var host = new ReferenceHost();
        using var server = new CultMeshLegacyRudpContentServer(host, cache);
        var peer = new ReferencePeer();
        host.Dispatch(request, peer).GetAwaiter().GetResult();
        return peer.Sent ?? throw new InvalidOperationException("The reference server answered with silence.");
    }

    private sealed class ReferenceHost : ICultNetSchemaServer
    {
        private Func<CultMeshContentChunkRequestMessage, ICultNetSchemaServerPeer, Task>? _handler;

        public void OnCultNet<TMessage>(Func<TMessage, ICultNetSchemaServerPeer, Task> callback)
            where TMessage : ICultNetSchemaMessage =>
            _handler = (Func<CultMeshContentChunkRequestMessage, ICultNetSchemaServerPeer, Task>)(object)callback;

        public void RemoveCultNetMessageListener<TMessage>(Delegate callback)
            where TMessage : ICultNetSchemaMessage => _handler = null;

        public Task Dispatch(CultMeshContentChunkRequestMessage request, ICultNetSchemaServerPeer peer) =>
            _handler!(request, peer);
    }

    private sealed class ReferencePeer : ICultNetSchemaServerPeer
    {
        public CultMeshContentChunkResponseMessage? Sent { get; private set; }

        public void SendCultNet<TMessage>(TMessage message) where TMessage : ICultNetSchemaMessage =>
            Sent = (CultMeshContentChunkResponseMessage)(object)message;
    }

    private static string ToJson(List<Vector> vectors) => JsonSerializer.Serialize(
        new
        {
            vectors = vectors.Select(v => new
            {
                label = v.Label,
                kind = v.Kind,
                messagePackBase64 = Convert.ToBase64String(v.Bytes)
            })
        },
        new JsonSerializerOptions { WriteIndented = true });

    private static List<Vector> ReadVectors(string name)
    {
        var path = VectorPath(name);
        if (!File.Exists(path))
            Assert.Inconclusive($"{path} is missing - write it first (CULTNET_WRITE_VECTORS=1).");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("vectors").EnumerateArray()
            .Select(v => new Vector(
                v.GetProperty("label").GetString()!,
                v.GetProperty("kind").GetString()!,
                Convert.FromBase64String(v.GetProperty("messagePackBase64").GetString()!)))
            .ToList();
    }

    private static string Lines(string text) => text.Replace("\r\n", "\n").TrimEnd('\n');

    private static string[] KeyHashes()
    {
        var empty = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        return new[]
        {
            empty, "SHA256:" + empty.ToUpperInvariant(), "  " + empty + "  ", " sha256:" + empty,
            "sha256:sha256:" + empty, "Sha256:\u0130\u03A3\U00010400", "sha256:",
            // U+017F uppercases to 'S': a prefix fold by upper case would strip it, the reference's ordinal-ignore-case does not.
            "\u017Fha256:abc"
        };
    }

    private sealed record KeyVectors(
        List<(string Id, string Version, string Hash, string Key)> Artifacts,
        List<(string Hash, string Key)> Chunks);

    /// <summary>Every key is derived by the reference's public CreateRecordKey, never spelled by hand.</summary>
    private static KeyVectors BuildRecordKeyVectors()
    {
        var ids = new[]
        {
            "eureka/content-vector", "  Mixed Case/Path  ", "\u0130stanbul", "\u0391\u03A3", "\uFEFFbom", "\u0085nel\u0085",
            "\u65E5\u672C\u8A9E/\u8CC7\u7523", "\U0001F642 smile", "---", "a\uD800b", "\uD800", "x\u00B2y", "\u0663d", "\U00010400A",
            // Tokens that are empty after the trim: the fallback hashes the original, neither trimmed nor lower-cased.
            "  ---  ", "\u24B6", "\u216B"
        };
        var versions = new[] { string.Empty, "   ", "1.0.0", "V2 Beta", "\u00FC" };
        var hashes = KeyHashes();
        var artifacts = ids.Select((id, i) => (id, versions[i % versions.Length], hashes[i % hashes.Length]))
            .Concat(versions.Select(version => (ids[0], version, hashes[0])))
            .Concat(hashes.Select(hash => (ids[0], versions[0], hash)))
            .Select(t => (t.Item1, t.Item2, t.Item3, CultMeshCdnArtifactManifest.CreateRecordKey(t.Item1, t.Item2, t.Item3).Value))
            .ToList();
        var chunks = hashes.Select(hash => (hash, CultMeshCdnArtifactChunk.CreateRecordKey(hash).Value)).ToList();
        return new KeyVectors(artifacts, chunks);
    }

    /// <summary>String inputs are stored as UTF-16 code-unit arrays, so a lone surrogate survives the JSON file.</summary>
    private static string RecordKeyVectorsToJson(KeyVectors vectors) =>
        JsonSerializer.Serialize(
            new
            {
                artifacts = vectors.Artifacts.Select(a => new
                {
                    artifactIdUnits = a.Id.Select(c => (int)c),
                    versionUnits = a.Version.Select(c => (int)c),
                    contentHash = a.Hash,
                    recordKey = a.Key
                }),
                chunks = vectors.Chunks.Select(c => new { chunkHash = c.Hash, recordKey = c.Key })
            },
            new JsonSerializerOptions { WriteIndented = true });

    private static string VectorPath(string name) => Path.Combine(RepoRoot(), "contracts", "cultmesh", name);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "CultLib.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("CultLib.sln was not found above the test directory.");
    }
}
