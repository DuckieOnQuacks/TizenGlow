using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TizenGlow.WebSocket
{
    // Owns one upgraded connection, including the HTTP response that backs it on Tizen.
    internal static class WebSocketConnection
    {
        internal static async Task HandleAsync(System.Net.WebSockets.WebSocket socket,
            Func<System.Net.WebSockets.WebSocket, string, Task> onMessage, Action releaseResponse)
        {
            try
            {
                var buffer = new byte[8192];
                while (socket.State == WebSocketState.Open)
                {
                    int length = 0;
                    WebSocketReceiveResult result;
                    do
                    {
                        if (length == buffer.Length) throw new InvalidOperationException("Message too large");
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length,
                            buffer.Length - length), CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            // The peer sent Close already. Bound the reply, then release both resources.
                            using (var closing = new CancellationTokenSource(2000))
                                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", closing.Token);
                            return;
                        }
                        if (result.MessageType != WebSocketMessageType.Text)
                            throw new InvalidOperationException("Text messages required");
                        length += result.Count;
                    } while (!result.EndOfMessage);

                    // Decode only complete messages: even a UTF-8 character can span receive fragments.
                    await onMessage(socket, Encoding.UTF8.GetString(buffer, 0, length));
                }
            }
            finally
            {
                try
                {
                    try { socket.Abort(); }
                    finally { socket.Dispose(); }
                }
                finally
                {
                    try { releaseResponse(); }
                    catch (ObjectDisposedException) { }
                }
            }
        }
    }
}
