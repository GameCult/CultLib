#nullable enable
using System;
using System.IO;
using NUnit.Framework;

// snippet:begin
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Networking;
using MessagePack;

[CultDocument("gamecult.first_call_note", "gamecult.first_call_note.v1")]
[MessagePackObject]
public sealed class Note
{
    [Key(0)] [CultName] public string Title = string.Empty;
    [Key(1)] public string Body = string.Empty;
}

public static class CultNetFirstCall
{
    public static async Task<Note?> Run()
    {
        var inbox = new CultCache();                            // the receiver's cache
        var documents = new CultNetDocumentRegistry();
        var received = new TaskCompletionSource<bool>();

        // Receive: a loopback listener that applies each raw document put to the inbox.
        using var server = new TcpFramedCultNetSchemaServer(new TcpListener(IPAddress.Loopback, 0));
        server.OnCultNet<CultNetDocumentPutRawMessage>(async (put, peer) =>
        {
            await documents.ApplyRawDocumentPutMessageAsync<Note>(inbox, put);
            received.TrySetResult(true);
        });

        // Send: a typed document as a cultnet.document_put_raw.v0 message.
        using var client = new TcpFramedCultNetSchemaClient();
        client.Connect("127.0.0.1", server.LocalEndPoint.Port);
        var handle = new CultRecordHandle<Note>(new CultRecordKey("note:hello"));
        client.SendCultNet(documents.CreateRawDocumentPutMessage(
            "put-1", handle, new Note { Title = "Hello", Body = "sent over CultNet" }));

        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return inbox.Get<Note>(handle.Key);
    }
}
// snippet:end

namespace GameCult.Networking.Tests
{
    // The C# CultNet first-call snippet that docs/start/README.md shows verbatim: define a typed document, send it
    // over a loopback TCP pipe, receive it into a second cache. DocsShowTheTestedSnippet fails when they differ.
    public class CultNetFirstCallTests
    {
        [Test]
        public async Task SendATypedDocumentOverLoopbackAndReceiveItIntoASecondCache()
        {
            var note = await CultNetFirstCall.Run();
            Assert.That(note, Is.Not.Null);
            Assert.That(note!.Title, Is.EqualTo("Hello"));
            Assert.That(note.Body, Is.EqualTo("sent over CultNet"));
        }

        [Test]
        public void DocsShowTheTestedSnippet()
        {
            var source = File.ReadAllText(SourcePath());
            const string begin = "// snippet:begin\n";
            var start = source.IndexOf(begin, StringComparison.Ordinal) + begin.Length;
            var snippet = source[start..source.IndexOf("// snippet:end\n", StringComparison.Ordinal)];
            var docs = File.ReadAllText(Path.Combine(Path.GetDirectoryName(SourcePath())!, "..", "..", "docs", "start", "README.md"));
            Assert.That(docs.Replace("\r\n", "\n"), Does.Contain("```csharp\n" + snippet + "```"),
                "docs/start/README.md no longer shows tests/GameCult.Networking.Tests/CultNetFirstCallTests.cs verbatim");
        }

        static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
    }
}
