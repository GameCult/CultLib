using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    private static string VectorPath(string name) => Path.Combine(RepoRoot(), "contracts", "cultmesh", name);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "CultLib.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("CultLib.sln was not found above the test directory.");
    }
}
