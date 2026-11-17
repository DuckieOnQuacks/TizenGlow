using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TizenGlow.WebSocket;

class Program
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static async Task Run(string name, FakeSocket socket, Type expectedError = null,
        Func<System.Net.WebSockets.WebSocket, string, Task> handler = null, Action release = null,
        params string[] expectedMessages)
    {
        var messages = new List<string>();
        int releases = 0;
        Exception error = null;
        try
        {
            await WebSocketConnection.HandleAsync(socket, async (ws, message) =>
            {
                messages.Add(message);
                if (handler != null) await handler(ws, message);
            }, () => { releases++; release?.Invoke(); });
        }
        catch (Exception e) { error = e; }

        Check(expectedError == null ? error == null : error != null && expectedError.IsInstanceOfType(error),
            name + ": unexpected error: " + error);
        Check(socket.Aborts == 1 && socket.Disposals == 1 && releases == 1,
            name + ": connection resources were not released exactly once");
        Check(messages.Count == expectedMessages.Length, name + ": unexpected message count");
        for (int i = 0; i < messages.Count; i++)
            Check(messages[i] == expectedMessages[i], name + ": message differs at " + i);
        Console.WriteLine("PASS " + name);
    }

    static async Task Main()
    {
        var close = new FakeSocket().Close();
        await Run("peer close releases socket and HTTP response", close);
        Check(close.CloseReplies == 1 && close.CloseTokenWasCancellable, "missing bounded close reply");

        // Split inside the three-byte euro character, then send another independent message.
        var bytes = Encoding.UTF8.GetBytes("{\"value\":\"€\"}");
        await Run("fragmented UTF-8 and consecutive messages", new FakeSocket()
            .Frame(bytes[..11], false).Frame(bytes[11..], true).Text("next").Close(),
            expectedMessages: new[] { "{\"value\":\"€\"}", "next" });

        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ordered = new FakeSocket().Text("first").Text("second").Close();
        var pending = Run("await handler before receiving next message", ordered, handler: async (ws, message) =>
        {
            if (message == "first") { entered.SetResult(true); await gate.Task; }
        }, expectedMessages: new[] { "first", "second" });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(ordered.Receives == 1, "received another message while handler was pending");
        gate.SetResult(true);
        await pending;

        await Run("first receive failure releases resources", new FakeSocket(), typeof(WebSocketException));
        await Run("later receive failure releases resources", new FakeSocket().Text("ok"),
            typeof(WebSocketException), expectedMessages: new[] { "ok" });
        await Run("message handler failure releases resources", new FakeSocket().Text("invalid"),
            typeof(FormatException), (ws, message) => throw new FormatException(),
            expectedMessages: new[] { "invalid" });

        var closeFailure = new FakeSocket { FailClose = true }.Close();
        await Run("close reply failure releases resources", closeFailure, typeof(WebSocketException));
        var blockedClose = new FakeSocket { BlockClose = true }.Close();
        await Run("close reply cancellation releases resources", blockedClose, typeof(OperationCanceledException));

        await Run("binary message is rejected", new FakeSocket()
            .Frame(new byte[] { 1 }, true, WebSocketMessageType.Binary), typeof(InvalidOperationException));
        await Run("oversized fragmented message is rejected before dispatch", new FakeSocket()
            .Frame(new byte[8192], false).Text("overflow"), typeof(InvalidOperationException));
        string limit = new string('x', 8192);
        await Run("complete message at the size limit is accepted", new FakeSocket().Text(limit).Close(),
            expectedMessages: new[] { limit });
        await Run("close during fragmented message discards partial text", new FakeSocket()
            .Frame(Encoding.UTF8.GetBytes("partial"), false).Close());

        await Run("already disposed HTTP response is harmless", new FakeSocket().Close(),
            release: () => throw new ObjectDisposedException("response"));
        await Run("abort failure still disposes and releases HTTP response",
            new FakeSocket { FailAbort = true }.Close(), typeof(InvalidOperationException));
        await Run("dispose failure still releases HTTP response",
            new FakeSocket { FailDispose = true }.Close(), typeof(InvalidOperationException));

        Console.WriteLine("15 WebSocket connection checks passed.");
    }

    sealed class FakeSocket : System.Net.WebSockets.WebSocket
    {
        readonly Queue<(byte[] bytes, bool end, WebSocketMessageType type)> frames =
            new Queue<(byte[], bool, WebSocketMessageType)>();
        WebSocketState state = WebSocketState.Open;
        public int Aborts, Disposals, CloseReplies, Receives;
        public bool FailClose, BlockClose, FailAbort, FailDispose, CloseTokenWasCancellable;
        public override WebSocketState State => state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string CloseStatusDescription => null;
        public override string SubProtocol => null;

        public FakeSocket Frame(byte[] bytes, bool end, WebSocketMessageType type = WebSocketMessageType.Text)
        { frames.Enqueue((bytes, end, type)); return this; }
        public FakeSocket Text(string value) => Frame(Encoding.UTF8.GetBytes(value), true);
        public FakeSocket Close() => Frame(Array.Empty<byte>(), true, WebSocketMessageType.Close);

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            Receives++;
            if (frames.Count == 0) throw new WebSocketException("Peer disconnected");
            var frame = frames.Dequeue();
            Check(frame.bytes.Length <= buffer.Count, "fake frame exceeds receive buffer");
            Array.Copy(frame.bytes, 0, buffer.Array, buffer.Offset, frame.bytes.Length);
            if (frame.type == WebSocketMessageType.Close) state = WebSocketState.CloseReceived;
            return Task.FromResult(new WebSocketReceiveResult(frame.bytes.Length, frame.type, frame.end));
        }

        public override async Task CloseOutputAsync(WebSocketCloseStatus status, string description, CancellationToken token)
        {
            Check(state == WebSocketState.CloseReceived, "reply without peer Close");
            CloseReplies++;
            CloseTokenWasCancellable = token.CanBeCanceled;
            if (FailClose) throw new WebSocketException("Close reply failed");
            if (BlockClose) await Task.Delay(Timeout.Infinite, token);
            state = WebSocketState.Closed;
        }

        public override void Abort()
        { Aborts++; state = WebSocketState.Aborted; if (FailAbort) throw new InvalidOperationException("Abort failed"); }
        public override void Dispose()
        { Disposals++; if (FailDispose) throw new InvalidOperationException("Dispose failed"); }
        public override Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken token)
            => throw new Exception("Use CloseOutputAsync to answer the peer's Close");
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token)
            => throw new NotSupportedException();
    }
}
