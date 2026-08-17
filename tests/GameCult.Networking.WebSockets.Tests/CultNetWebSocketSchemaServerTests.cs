using System.Net;
using GameCult.Networking.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace GameCult.Networking.Tests;

[TestFixture]
public sealed class CultNetWebSocketSchemaServerTests
{
    [Test]
    public async Task BinarySchemaMessage_RoundTrips_ThroughAuthenticatedHostAdapter()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var server = new CultNetWebSocketSchemaServer();
        server.OnCultNet<CultNetHelloMessage>((message, peer) =>
        {
            peer.SendCultNet(new CultNetHelloMessage
            {
                RuntimeId = "csharp-websocket-provider",
                RuntimeKind = "provider",
                DisplayName = message.RuntimeId
            });
            return Task.CompletedTask;
        });
        var app = builder.Build();
        app.UseWebSockets();
        app.MapCultNetWebSocket("/cultmesh", server, new CultNetWebSocketEndpointOptions
        {
            AuthorizeAsync = context => ValueTask.FromResult(
                string.Equals(context.Request.Headers.Authorization, "Bearer test-session", StringComparison.Ordinal))
        });
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var endpoint = new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal) + "/cultmesh");
            using var client = new CultNetWebSocketSchemaClient(options =>
                options.SetRequestHeader("Authorization", "Bearer test-session"));
            var received = new TaskCompletionSource<CultNetHelloMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            client.OnCultNet<CultNetHelloMessage>(message => received.TrySetResult(message));
            await client.ConnectAsync(endpoint);
            client.SendCultNet(new CultNetHelloMessage
            {
                RuntimeId = "browser-consumer",
                RuntimeKind = "browser"
            });
            var response = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Multiple(() =>
            {
                Assert.That(response.RuntimeId, Is.EqualTo("csharp-websocket-provider"));
                Assert.That(response.DisplayName, Is.EqualTo("browser-consumer"));
                Assert.That(server.PeerCount, Is.EqualTo(1));
            });
            client.Dispose();
            await WaitUntilAsync(() => server.PeerCount == 0);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Test]
    public void Endpoint_Requires_Explicit_Authentication_Or_Development_OptIn()
    {
        var builder = WebApplication.CreateSlimBuilder();
        var app = builder.Build();
        using var server = new AsyncDisposableScope(new CultNetWebSocketSchemaServer());
        Assert.That(
            () => app.MapCultNetWebSocket("/cultmesh", server.Value, new CultNetWebSocketEndpointOptions()),
            Throws.InvalidOperationException.With.Message.Contains("AuthorizeAsync"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class AsyncDisposableScope : IDisposable
    {
        public AsyncDisposableScope(CultNetWebSocketSchemaServer value) => Value = value;
        public CultNetWebSocketSchemaServer Value { get; }
        public void Dispose() => Value.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
