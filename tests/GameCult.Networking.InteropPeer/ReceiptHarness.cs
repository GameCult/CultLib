using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Networking;
using LiteNetLib;
using R3;

// The real C# receipt server (CultNetDatabaseServer on Server) and the one transport shim the TypeScript client needs to reach it,
// for the cross-runtime write-receipt interop harness (packages/cultmesh-ts/test/interop/write-receipt-interop.test.ts).
// TypeScript has no LiteNetLib client, so the shim moves bytes and nothing else: each CultNet frame is a LiteNetLib reliable
// ordered payload, and on the pipe it is a 4-byte big-endian length followed by the frame.
namespace GameCult.Networking.InteropPeer
{
    // CompatibleSchemaIds are the names the spec's blankFields vectors admit (U+200B, U+001C, and a name padded with NBSP and EM SPACE):
    // a name is any string that is not blank, so the store must hold a record put under one of them.
    [CultDocument("receipt.interop_note", ReceiptHarness.SchemaId, CompatibleSchemaIds = ["\u200b", "\u001c", "\u00a0a\u2003"])]
    [MessagePack.MessagePackObject]
    public sealed class ReceiptInteropNote
    {
        [MessagePack.Key(0)]
        public string Text { get; set; } = string.Empty;
    }

    internal static class ReceiptHarness
    {
        public const string SchemaId = "receipt.interop_note.v1";
        public const string ShardId = "receipt-interop-shard";
        // A record key with this prefix is refused by the authority: how the harness asks for a refused write.
        public const string RefusedPrefix = "refuse:";

        private sealed class Authority : ICultNetRemoteWriteAuthority
        {
            public CultNetRemoteWriteDecision Decide(CultNetRemoteWriteRequest request) =>
                request.RecordKey.ToString().StartsWith(RefusedPrefix, StringComparison.Ordinal)
                    ? CultNetRemoteWriteDecision.Refuse("denied")
                    : CultNetRemoteWriteDecision.Grant();
        }

        private static void Emit(object fact)
        {
            lock (Console.Out) Console.Out.WriteLine(JsonSerializer.Serialize(fact));
        }

        // stdout: {"event":"listening","port":N} then {"event":"applied"|"removed","recordKey":"..."} per committed change; runs until stdin closes.
        public static async Task ServeAsync()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
            {
                Shards = [new CultNetShardDescriptor(ShardId, "primary", epoch: 1, isPrimary: true, schemaIds: [SchemaId])]
            });
            using var server = new Server(cache, ServerSecurityOptions.Development()) { Port = 0 };
            using var databaseServer = new CultNetDatabaseServer(server, database, new CultNetDatabaseServerOptions
            {
                RemoteWriteAuthority = new Authority()
            });
            using var watch = cache.Watch<ReceiptInteropNote>().Subscribe(change =>
                Emit(new { @event = change.Kind == CultCacheDocumentChangeKind.Removed ? "removed" : "applied", recordKey = change.Key.ToString() }));
            server.Start();
            Emit(new { @event = "listening", port = server.Port, shardId = ShardId, schemaId = SchemaId });
            await Task.Run(() => Console.In.ReadToEnd());
        }

        // Connects to the server on `port` and relays frames between stdin/stdout and the connection; exits when either ends.
        public static async Task RelayAsync(int port)
        {
            var listener = new EventBasedNetListener();
            var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var output = Console.OpenStandardOutput();
            var gate = new object();
            listener.PeerConnectedEvent += _ => connected.TrySetResult(true);
            listener.PeerDisconnectedEvent += (_, _) => ended.TrySetResult(true);
            listener.NetworkReceiveEvent += (_, reader, _, _) =>
            {
                var frame = reader.GetRemainingBytes();
                var header = new byte[] { (byte)(frame.Length >> 24), (byte)(frame.Length >> 16), (byte)(frame.Length >> 8), (byte)frame.Length };
                lock (gate)
                {
                    output.Write(header);
                    output.Write(frame);
                    output.Flush();
                }
            };
            var manager = new NetManager(listener) { UnsyncedEvents = true };
            manager.Start(0);
            var peer = manager.Connect("127.0.0.1", port, ServerSecurityOptions.Development().ConnectionKey);
            if (await Task.WhenAny(connected.Task, Task.Delay(TimeSpan.FromSeconds(10))) != connected.Task)
                throw new InvalidOperationException("the relay did not connect");
            var input = Console.OpenStandardInput();
            var pump = Task.Run(() =>
            {
                var header = new byte[4];
                while (ReadExactly(input, header))
                {
                    var frame = new byte[(header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3]];
                    if (!ReadExactly(input, frame)) break;
                    peer.Send(frame, DeliveryMethod.ReliableOrdered);
                }
            });
            await Task.WhenAny(pump, ended.Task);
            manager.Stop();
        }

        private static bool ReadExactly(Stream stream, byte[] buffer)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var count = stream.Read(buffer, read, buffer.Length - read);
                if (count <= 0) return false;
                read += count;
            }
            return true;
        }
    }
}
