using Newtonsoft.Json;
using System.Threading.Tasks;
using Tizen.Applications;
using HyperTizen.WebSocket.DataTypes;
using System.Net.WebSockets;
using System.Text;
using System;
using System.Diagnostics;
using System.Threading;

namespace HyperTizen.WebSocket
{
    internal class HyperionClient
    {
        WebSocketClient client;
        private int _loopRunning;
        private DateTime _lastReconnect = DateTime.MinValue;

        private const int FrameMs = 40; // ~25 fps cap; the loop used to spin with no delay at all
        internal static int ConditionRetryMs = 10000;
        internal static int ErrorBackoffMs = 5000;
        internal static TimeSpan ReconnectInterval = TimeSpan.FromSeconds(5);

        public HyperionClient()
        {
            if (!Preference.Contains("rpcServer")) return;
            client = new WebSocketClient(Preference.Get<string>("rpcServer"));
            Task.Run(() => client.ConnectAsync());
            Task.Run(() => Start());
        }

        public void UpdateURI(string uri)
        {
            WebSocketClient old = client;
            WebSocketClient fresh = new WebSocketClient(uri);

            // Publish the new client before tearing the old one down, so a running loop
            // picks up the replacement instead of sending on a socket being disposed.
            client = fresh;
            Task.Run(async () =>
            {
                try { old?.client?.Abort(); } catch { }
                await fresh.ConnectAsync();
            });

            // A loop that exited with "no-server" (nothing saved yet) needs starting now.
            if (App.Configuration.Enabled) Task.Run(() => Start());
        }

        public async Task Start()
        {
            // Exactly one capture loop. The display handler, UpdateURI and the UI's "enabled"
            // all call Start, and stacked loops would fight over the capture API and socket.
            if (Interlocked.Exchange(ref _loopRunning, 1) == 1) return;
            try { await RunAsync(); }
            catch (Exception e) { Capturer.ReportError("capture loop", e); }
            finally { Interlocked.Exchange(ref _loopRunning, 0); }
        }

        private async Task RunAsync()
        {
            if (!App.Configuration.Enabled) return;
            if (client == null)
            {
                Capturer.SetState("no-server");
                return;
            }

            Capturer.SetState("starting");
            while (!Capturer.GetCondition())
            {
                // A missing API never appears at runtime. Otherwise the API exists but the condition
                // read failed (already persisted and notified): retry slowly instead of spinning.
                if (Capturer.Unsupported) return;
                await Task.Delay(ConditionRetryMs);
                if (!App.Configuration.Enabled)
                {
                    Capturer.SetState("stopped");
                    return;
                }
            }

            var frame = new Stopwatch();
            while (App.Configuration.Enabled)
            {
                frame.Restart();
                string image;
                try
                {
                    image = Capturer.ToImage(Capturer.GetColors());
                }
                catch (Exception e)
                {
                    Capturer.ReportError("frame capture", e);
                    await Task.Delay(ErrorBackoffMs);
                    continue;
                }

                // Only a frame that actually came back from the capture API counts as running.
                Capturer.SetState("running");
                await SendAsync(image);

                int wait = FrameMs - (int)frame.ElapsedMilliseconds;
                if (wait > 0) await Task.Delay(wait);
            }
            Capturer.SetState("stopped");
        }

        private async Task SendAsync(string image)
        {
            WebSocketClient c = client;
            if (c.client.State == WebSocketState.Open)
            {
                try
                {
                    var buffer = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new ImageCommand(image)));
                    await c.client.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch (Exception) { } // dropped mid-send: the socket is no longer Open, so the next frame redials
            }
            else if (DateTime.UtcNow - _lastReconnect >= ReconnectInterval)
            {
                // Nothing else redials a dropped socket, which left HyperHDR holding a frozen
                // frame. The frame loop doubles as the watchdog; dialing runs in the background
                // so a slow connect never stalls capture.
                _lastReconnect = DateTime.UtcNow;
                _ = c.ReconnectAsync();
            }
        }

        public async Task Stop()
        {
            if (App.Configuration.Enabled) App.Configuration.Enabled = false;
            Color[] colors = Capturer.GetColors();
            string image = Capturer.ToImage(colors);

            if (client?.client?.State == WebSocketState.Open)
            {
                ImageCommand imgCmd = new ImageCommand(image);
                string message = JsonConvert.SerializeObject(imgCmd);
                var buffer = Encoding.UTF8.GetBytes(message);
                await client.client.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
    }
}
