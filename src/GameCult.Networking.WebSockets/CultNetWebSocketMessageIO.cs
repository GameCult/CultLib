using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace GameCult.Networking.WebSockets;

internal static class CultNetWebSocketMessageIO
{
    public static Task SendAsync(WebSocket socket, byte[] payload, CancellationToken cancellationToken) =>
        socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Binary, true, cancellationToken);

    public static async Task<byte[]?> ReceiveAsync(
        WebSocket socket,
        int maxMessageBytes,
        CancellationToken cancellationToken)
    {
        var segment = new byte[Math.Min(16 * 1024, maxMessageBytes)];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(segment), cancellationToken)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Binary)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.InvalidMessageType,
                    "CultNet requires binary MessagePack WebSocket messages.",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }
            if (message.Length + result.Count > maxMessageBytes)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.MessageTooBig,
                    "CultNet message exceeds the configured maximum.",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }
            message.Write(segment, 0, result.Count);
            if (result.EndOfMessage) return message.ToArray();
        }
    }
}
