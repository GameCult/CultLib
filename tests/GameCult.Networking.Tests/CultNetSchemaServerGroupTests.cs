#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    [TestFixture]
    public sealed class CultNetSchemaServerGroupTests
    {
        [Test]
        public async Task RegistersAndRemovesTheSameLogicalHandlerAcrossTransports()
        {
            var tcp = new FakeServer();
            var webSocket = new FakeServer();
            using var group = new CultNetSchemaServerGroup(tcp, webSocket);
            var received = new List<string>();
            Func<CultNetHelloMessage, ICultNetSchemaServerPeer, Task> handler = (message, _) =>
            {
                received.Add(message.RuntimeId);
                return Task.CompletedTask;
            };

            group.OnCultNet(handler);
            await tcp.DispatchAsync(new CultNetHelloMessage { RuntimeId = "tcp" });
            await webSocket.DispatchAsync(new CultNetHelloMessage { RuntimeId = "websocket" });
            group.RemoveCultNetMessageListener<CultNetHelloMessage>(handler);

            Assert.That(received, Is.EqualTo(new[] { "tcp", "websocket" }));
            Assert.That(tcp.HasHandler<CultNetHelloMessage>(), Is.False);
            Assert.That(webSocket.HasHandler<CultNetHelloMessage>(), Is.False);
        }

        [Test]
        public void AggregatesPeerLifetimeWithoutTakingTransportOwnership()
        {
            var tcp = new FakeServer();
            var webSocket = new FakeServer();
            var group = new CultNetSchemaServerGroup(tcp, webSocket);
            var peer = new FakePeer();
            var disconnected = new List<ICultNetSchemaServerPeer>();
            group.PeerDisconnected += disconnected.Add;

            tcp.Disconnect(peer);
            webSocket.Disconnect(peer);
            group.Dispose();
            tcp.Disconnect(peer);

            Assert.That(disconnected, Is.EqualTo(new[] { peer, peer }));
            Assert.That(tcp.Disposed, Is.False);
            Assert.That(webSocket.Disposed, Is.False);
        }

        [Test]
        public void RejectsAGroupThatDoesNotActuallyAddASecondTransport()
        {
            var server = new FakeServer();
            Assert.Multiple(() =>
            {
                Assert.That(() => new CultNetSchemaServerGroup(server), Throws.ArgumentException);
                Assert.That(() => new CultNetSchemaServerGroup(server, server), Throws.ArgumentException);
            });
        }

        private sealed class FakeServer :
            ICultNetSchemaServer,
            ICultNetSchemaServerPeerLifecycle
        {
            private readonly Dictionary<Type, Delegate> _handlers = new();
            public bool Disposed { get; private set; }
            public event Action<ICultNetSchemaServerPeer>? PeerDisconnected;

            public void OnCultNet<TMessage>(Func<TMessage, ICultNetSchemaServerPeer, Task> callback)
                where TMessage : ICultNetSchemaMessage =>
                _handlers[typeof(TMessage)] = callback;

            public void RemoveCultNetMessageListener<TMessage>(Delegate callback)
                where TMessage : ICultNetSchemaMessage
            {
                if (_handlers.TryGetValue(typeof(TMessage), out var current) && current == callback)
                    _handlers.Remove(typeof(TMessage));
            }

            public bool HasHandler<TMessage>() where TMessage : ICultNetSchemaMessage =>
                _handlers.ContainsKey(typeof(TMessage));

            public Task DispatchAsync<TMessage>(TMessage message) where TMessage : ICultNetSchemaMessage =>
                ((Func<TMessage, ICultNetSchemaServerPeer, Task>)_handlers[typeof(TMessage)])(message, new FakePeer());

            public void Disconnect(ICultNetSchemaServerPeer peer) => PeerDisconnected?.Invoke(peer);
        }

        private sealed class FakePeer : ICultNetSchemaServerPeer
        {
            public void SendCultNet<TMessage>(TMessage message) where TMessage : ICultNetSchemaMessage
            {
            }
        }
    }
}
