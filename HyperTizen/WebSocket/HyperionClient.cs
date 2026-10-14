using Newtonsoft.Json;
using System.Threading.Tasks;
using Tizen.Applications;
using HyperTizen.WebSocket.DataTypes;
using System.Net.WebSockets;
using System.Text;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace HyperTizen.WebSocket
{
    internal class HyperionClient
    {
        WebSocketClient client;
        private int _loopRunning;
        private DateTime _lastReconnect = DateTime.MinValue;

        internal static int ConditionRetryMs = 10000;
        internal static int ErrorBackoffMs = 5000;
        internal static TimeSpan ReconnectInterval = TimeSpan.FromSeconds(5);

        // The presets HyperTizenUI offers; anything else sent as "targetFps" is ignored.
        public static readonly int[] FpsPresets = { 10, 15, 20, 25, 30, 40 };
        public const int DefaultFps = 25;
        private static volatile int _targetFps = DefaultFps;
        private static volatile string _measuredFps = "0.0";

        /// <summary>Frames per second the loop paces to; read every frame, so changes apply at once.</summary>
        public static int TargetFps { get { return _targetFps; } }

        /// <summary>Captured frames per second over the last second, "0.0" while not capturing.</summary>
        public static string MeasuredFps { get { return _measuredFps; } }

        public static bool TrySetTargetFps(string value)
        {
            int fps;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out fps) || Array.IndexOf(FpsPresets, fps) < 0) return false;
            _targetFps = fps;
            return true;
        }

        public HyperionClient()
        {
            if (Preference.Contains("targetFps")) TrySetTargetFps(Preference.Get<string>("targetFps"));
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

            var clock = Stopwatch.StartNew();
            double nextFrameMs = 0;
            double windowStartMs = 0;
            int windowFrames = 0;
            while (App.Configuration.Enabled)
            {
                string image;
                try
                {
                    image = Capturer.ToImage(Capturer.GetColors());
                }
                catch (Exception e)
                {
                    Capturer.ReportError("frame capture", e);
                    _measuredFps = "0.0";
                    await Task.Delay(ErrorBackoffMs);
                    nextFrameMs = windowStartMs = clock.Elapsed.TotalMilliseconds;
                    windowFrames = 0;
                    continue;
                }

                // Only a frame that actually came back from the capture API counts as running.
                Capturer.SetState("running");
                await SendAsync(image);

                double now = clock.Elapsed.TotalMilliseconds;
                windowFrames++;
                if (now - windowStartMs >= 1000)
                {
                    _measuredFps = (windowFrames * 1000 / (now - windowStartMs)).ToString("0.0", CultureInfo.InvariantCulture);
                    windowStartMs = now;
                    windowFrames = 0;
                }

                // Pace against a deadline instead of sleeping a fixed time: capture and send time
                // count toward the frame, and a timer that wakes late shortens the next wait. A frame
                // that overran starts the next one immediately rather than bursting to catch up.
                nextFrameMs += 1000.0 / _targetFps;
                double wait = nextFrameMs - now;
                if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait));
                else nextFrameMs = now;
            }
            _measuredFps = "0.0";
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
