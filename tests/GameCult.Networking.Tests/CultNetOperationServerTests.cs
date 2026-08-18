#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    [TestFixture]
    public sealed class CultNetOperationServerTests
    {
        [Test]
        public async Task DispatchesTypedRequestAndCorrelatedReplyWithoutEnvelopePlumbing()
        {
            var transport = new FakeServer();
            var peer = new FakePeer();
            CultNetOperationContext<AddRequest>? observed = null;
            using var operations = new CultNetOperationServer(transport, "sample.provider")
                .Register<AddRequest, AddReceipt>(
                    "sample.counter",
                    "sample.counter.add",
                    "sample.add.v1",
                    "sample.add_receipt.v1",
                    context =>
                    {
                        observed = context;
                        return Task.FromResult(new AddReceipt { Count = context.Value.Amount + 4 });
                    });

            await transport.DispatchAsync(Request(new AddRequest { Amount = 3 }), peer);

            var response = peer.Messages.OfType<CultNetOperationResponseMessage>().Single();
            var receipt = MessagePackSerializer.Deserialize<AddReceipt>(
                Convert.FromBase64String(response.Payload),
                CultNetSchemaMessageSerialization.Options);
            Assert.Multiple(() =>
            {
                Assert.That(observed!.Value.Amount, Is.EqualTo(3));
                Assert.That(observed.IdempotencyKey, Is.EqualTo("command-7"));
                Assert.That(observed.SourceRuntimeId, Is.EqualTo("sample.browser"));
                Assert.That(response.MessageId, Is.EqualTo("command-7"));
                Assert.That(response.ServiceId, Is.EqualTo("sample.counter"));
                Assert.That(response.Operation, Is.EqualTo("sample.counter.add"));
                Assert.That(response.Status, Is.EqualTo("accepted"));
                Assert.That(response.PayloadSchema, Is.EqualTo("sample.add_receipt.v1"));
                Assert.That(response.SourceRuntimeId, Is.EqualTo("sample.provider"));
                Assert.That(receipt.Count, Is.EqualTo(7));
            });
        }

        [Test]
        public async Task PreservesTypedRejectionAndDiagnostics()
        {
            var transport = new FakeServer();
            var peer = new FakePeer();
            using var operations = new CultNetOperationServer(transport)
                .Register<AddRequest, AddReceipt>(
                    "sample.counter",
                    "sample.counter.add",
                    "sample.add.v1",
                    "sample.add_receipt.v1",
                    _ => Task.FromResult(CultNetOperationReply<AddReceipt>.Rejected(
                        new AddReceipt { Count = 4 },
                        "counter is locked")));

            await transport.DispatchAsync(Request(new AddRequest { Amount = 3 }), peer);

            var response = peer.Messages.OfType<CultNetOperationResponseMessage>().Single();
            Assert.That(response.Status, Is.EqualTo("rejected"));
            Assert.That(response.Diagnostics, Is.EqualTo(new[] { "counter is locked" }));
            Assert.That(
                MessagePackSerializer.Deserialize<AddReceipt>(
                    Convert.FromBase64String(response.Payload),
                    CultNetSchemaMessageSerialization.Options).Count,
                Is.EqualTo(4));
        }

        [Test]
        public async Task RejectsWrongSchemaBeforeApplicationHandler()
        {
            var transport = new FakeServer();
            var peer = new FakePeer();
            var calls = 0;
            using var operations = new CultNetOperationServer(transport)
                .Register<AddRequest, AddReceipt>(
                    "sample.counter",
                    "sample.counter.add",
                    "sample.add.v1",
                    "sample.add_receipt.v1",
                    context =>
                    {
                        calls++;
                        return Task.FromResult(new AddReceipt { Count = context.Value.Amount });
                    });
            var request = Request(new AddRequest { Amount = 3 });
            request.PayloadSchema = "wrong.v1";

            await transport.DispatchAsync(request, peer);

            Assert.That(calls, Is.Zero);
            var response = peer.Messages.OfType<CultNetOperationResponseMessage>().Single();
            var failure = MessagePackSerializer.Deserialize<CultNetOperationFailure>(
                Convert.FromBase64String(response.Payload),
                CultNetSchemaMessageSerialization.Options);
            Assert.Multiple(() =>
            {
                Assert.That(response.MessageId, Is.EqualTo("command-7"));
                Assert.That(response.Status, Is.EqualTo("invalid"));
                Assert.That(response.PayloadSchema, Is.EqualTo(CultNetOperationServer.FailureSchemaId));
                Assert.That(failure.Code, Is.EqualTo("request-schema-mismatch"));
                Assert.That(failure.Message, Does.Contain("expected payload schema 'sample.add.v1'"));
            });
        }

        [Test]
        public void RejectsDuplicateRouteAndDetachesOnDispose()
        {
            var transport = new FakeServer();
            var operations = new CultNetOperationServer(transport);
            operations.Register<AddRequest, AddReceipt>(
                "sample.counter",
                "sample.counter.add",
                "sample.add.v1",
                "sample.add_receipt.v1",
                context => Task.FromResult(new AddReceipt { Count = context.Value.Amount }));

            Assert.That(() => operations.Register<AddRequest, AddReceipt>(
                    "sample.counter",
                    "sample.counter.add",
                    "sample.add.v1",
                    "sample.add_receipt.v1",
                    context => Task.FromResult(new AddReceipt { Count = context.Value.Amount })),
                Throws.InvalidOperationException);
            Assert.That(transport.HasHandler<CultNetOperationRequestMessage>(), Is.True);

            operations.Dispose();

            Assert.That(transport.HasHandler<CultNetOperationRequestMessage>(), Is.False);
            Assert.That(() => operations.Register<AddRequest, AddReceipt>(
                    "sample.counter",
                    "sample.counter.add",
                    "sample.add.v1",
                    "sample.add_receipt.v1",
                    context => Task.FromResult(new AddReceipt { Count = context.Value.Amount })),
                Throws.TypeOf<ObjectDisposedException>());
        }

        private static CultNetOperationRequestMessage Request(AddRequest value) => new()
        {
            MessageId = "command-7",
            ServiceId = "sample.counter",
            Operation = "sample.counter.add",
            PayloadSchema = "sample.add.v1",
            PayloadEncoding = "messagepack-base64",
            Payload = Convert.ToBase64String(MessagePackSerializer.Serialize(
                value,
                CultNetSchemaMessageSerialization.Options)),
            SourceRuntimeId = "sample.browser"
        };

        [MessagePackObject]
        public sealed class AddRequest
        {
            [Key(0)] public int Amount { get; set; }
        }

        [MessagePackObject]
        public sealed class AddReceipt
        {
            [Key(0)] public int Count { get; set; }
        }

        private sealed class FakeServer : ICultNetSchemaServer
        {
            private readonly Dictionary<Type, Delegate> _handlers = new();

            public void OnCultNet<TMessage>(Func<TMessage, ICultNetSchemaServerPeer, Task> callback)
                where TMessage : ICultNetSchemaMessage => _handlers[typeof(TMessage)] = callback;

            public void RemoveCultNetMessageListener<TMessage>(Delegate callback)
                where TMessage : ICultNetSchemaMessage
            {
                if (_handlers.TryGetValue(typeof(TMessage), out var current) && current == callback)
                    _handlers.Remove(typeof(TMessage));
            }

            public bool HasHandler<TMessage>() where TMessage : ICultNetSchemaMessage =>
                _handlers.ContainsKey(typeof(TMessage));

            public Task DispatchAsync<TMessage>(TMessage message, ICultNetSchemaServerPeer peer)
                where TMessage : ICultNetSchemaMessage =>
                ((Func<TMessage, ICultNetSchemaServerPeer, Task>)_handlers[typeof(TMessage)])(message, peer);
        }

        private sealed class FakePeer : ICultNetSchemaServerPeer
        {
            public List<ICultNetSchemaMessage> Messages { get; } = new();

            public void SendCultNet<TMessage>(TMessage message) where TMessage : ICultNetSchemaMessage =>
                Messages.Add(message);
        }
    }
}
