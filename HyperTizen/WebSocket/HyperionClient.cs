using Newtonsoft.Json;
using System.Threading.Tasks;
using Tizen.Applications;
using HyperTizen.WebSocket.DataTypes;
using System.Net.WebSockets;
using System.Text;
using System;
using System.Globalization;
using System.Threading;

namespace HyperTizen.WebSocket
{
    internal class HyperionClient
    {
        WebSocketClient client;
        private int _loopRunning;
        private DateTime _lastReconnect = DateTime.MinValue;

        internal static TimeSpan ReconnectInterval = TimeSpan.FromSeconds(5);

        // The presets HyperTizenUI offers; anything else sent as "targetFps" is ignored.
        public static readonly int[] FpsPresets = { 10, 15, 20, 25, 30, 40 };
        private static volatile CaptureMode _mode = CaptureMode.Balanced;
        private static volatile int _targetFps = CaptureMode.Balanced.TargetFps;

        /// <summary>Live performance numbers; reset when capture starts and when the mode or target FPS changes.</summary>
        public static readonly CaptureStats Stats = new CaptureStats();

        /// <summary>Both are read by the loop every frame, so changes apply without a restart.</summary>
        public static CaptureMode Mode { get { return _mode; } }
        public static int TargetFps { get { return _targetFps; } }

        public static bool TrySetTargetFps(string value)
        {
            int fps;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out fps) || Array.IndexOf(FpsPresets, fps) < 0) return false;
            _targetFps = fps;
            Stats.Reset();
            return true;
        }

        /// <summary>Switches mode and moves the target FPS to the mode's.</summary>
        public static bool TrySetMode(string name)
        {
            CaptureMode mode = CaptureMode.Find(name);
            if (mode == null) return false;
            _mode = mode;
            _targetFps = mode.TargetFps;
            Stats.Reset();
            return true;
        }

        public static CaptureStats.Snapshot ReadStats()
        {
            CaptureStats.Snapshot s = Stats.Read(_targetFps);
            s.mode = _mode.Label;
            s.resolution = Capturer.Resolution;
            return s;
        }

        public HyperionClient()
        {
            // Mode first: it sets its own target FPS, which a saved targetFps then fine-tunes.
            if (Preference.Contains("captureMode")) TrySetMode(Preference.Get<string>("captureMode"));
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
                await Task.Delay(_mode.ConditionRetryMs);
                if (!App.Configuration.Enabled)
                {
                    Capturer.SetState("stopped");
                    return;
                }
            }

            Stats.Reset(); // fresh numbers for every capture session
            double nextFrameMs = Stats.Now();
            while (App.Configuration.Enabled)
            {
                int epoch = Stats.Epoch;
                CaptureMode mode = _mode;
                double start = Stats.Now(), captured, imaged;
                string image;
                try
                {
                    Color[] colors = Capturer.GetColors();
                    captured = Stats.Now();
                    image = Capturer.ToImage(colors);
                    imaged = Stats.Now();
                }
                catch (Exception e)
                {
                    Stats.AddFailed(epoch);
                    Capturer.ReportError("frame capture", e);
                    await Task.Delay(mode.ErrorBackoffMs);
                    nextFrameMs = Stats.Now();
                    continue;
                }

                // Only a frame that actually came back from the capture API counts as running.
                Capturer.SetState("running");
                bool sent = await SendAsync(image);
                double worked = Stats.Now();

                // Pace against a deadline instead of sleeping a fixed time: capture, image and send
                // time count toward the frame, and a timer that wakes late shortens the next wait.
                double interval = 1000.0 / _targetFps;
                nextFrameMs += interval;
                if (worked > nextFrameMs)
                {
                    // This frame ran past the next slot. Nothing is queued, overlapped or burst to
                    // catch up: the slots that went by are dropped.
                    double behind = (worked - nextFrameMs) / interval;
                    if (mode.KeepFrameGrid)
                    {
                        double skipped = Math.Ceiling(behind);
                        Stats.AddDropped(epoch, skipped);
                        nextFrameMs += skipped * interval; // wait for the next slot on the grid
                    }
                    else
                    {
                        Stats.AddDropped(epoch, behind);
                        nextFrameMs = worked;              // start the next frame now
                    }
                }
                double wait = nextFrameMs - worked;
                if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait));

                double end = Stats.Now();
                Stats.AddFrame(epoch, captured - start, imaged - captured, worked - imaged, end - worked, end - start, sent);
            }
            Capturer.SetState("stopped");
        }

        /// <summary>True when HyperHDR got the frame.</summary>
        private async Task<bool> SendAsync(string image)
        {
            WebSocketClient c = client;
            if (c.client.State == WebSocketState.Open)
            {
                try
                {
                    var buffer = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new ImageCommand(image)));
                    await c.client.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
                    return true;
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
            return false;
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
