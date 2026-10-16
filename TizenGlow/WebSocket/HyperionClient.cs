using System.Threading.Tasks;
using Tizen.Applications;
using TizenGlow.Wled;
using System;
using System.Globalization;
using System.Reflection;
using System.Threading;

namespace TizenGlow.WebSocket
{
    /// <summary>
    /// Owns the single capture loop. Each frame is captured once and handed to the active output:
    /// HyperHDR (HyperHdrOutput) or WLED Direct (WledOutput).
    /// </summary>
    internal class HyperionClient
    {
        private int _loopRunning;

        // The presets TizenGlowUI offers; anything else sent as "targetFps" is ignored.
        public static readonly int[] FpsPresets = { 10, 15, 20, 25, 30, 40 };
        private static volatile CaptureMode _mode = CaptureMode.Balanced;
        private static volatile int _targetFps = CaptureMode.Balanced.TargetFps;

        /// <summary>
        /// This service's version (tizen-manifest.xml, stamped in at build time), without build metadata.
        /// TizenGlowUI compares it with its own so a half-done update is obvious.
        /// </summary>
        public static readonly string ServiceVersion = ReadServiceVersion();

        private static string ReadServiceVersion()
        {
            var attr = typeof(HyperionClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            string version = attr != null ? attr.InformationalVersion : "unknown";
            int plus = version.IndexOf('+'); // the SDK appends "+<commit>"
            return plus >= 0 ? version.Substring(0, plus) : version;
        }

        /// <summary>Live performance numbers; reset when capture starts and when the mode, target FPS or output changes.</summary>
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

        public readonly HyperHdrOutput HyperHdr;
        public readonly WledOutput Wled;
        private volatile IFrameOutput _output;

        /// <summary>Stored as "outputMode": "hyperhdr" (default, so upgrades keep their behaviour) or "wled".</summary>
        public string OutputName { get { return _output == Wled ? "wled" : "hyperhdr"; } }

        public HyperionClient()
        {
            // Mode first: it sets its own target FPS, which a saved targetFps then fine-tunes.
            if (Preference.Contains("captureMode")) TrySetMode(Preference.Get<string>("captureMode"));
            if (Preference.Contains("targetFps")) TrySetTargetFps(Preference.Get<string>("targetFps"));

            HyperHdr = new HyperHdrOutput(Preference.Contains("rpcServer") ? Preference.Get<string>("rpcServer") : null);
            Wled = new WledOutput(() => _mode.SmoothingMs);
            if (Preference.Contains("wledTarget")) Wled.TrySetTarget(Preference.Get<string>("wledTarget"));
            if (Preference.Contains("ledShift")) Wled.TrySetShift(Preference.Get<string>("ledShift"));
            if (Preference.Contains("ledLayout")) Wled.TrySetLayout(Preference.Get<string>("ledLayout"));
            if (Preference.Contains("wledBrightness")) Wled.TrySetBrightness(Preference.Get<string>("wledBrightness"));
            if (Preference.Contains("wledBlackThreshold")) Wled.TrySetBlackThreshold(Preference.Get<string>("wledBlackThreshold"));
            if (Preference.Contains("wledSmoothing")) Wled.TrySetSmoothing(Preference.Get<string>("wledSmoothing"));
            _output = Preference.Contains("outputMode") && Preference.Get<string>("outputMode") == "wled" ? (IFrameOutput)Wled : HyperHdr;

            Task.Run(() => Start());
        }

        public void UpdateURI(string uri)
        {
            HyperHdr.UpdateUri(uri);

            // A loop that exited with "no-server" (nothing saved yet) needs starting now.
            if (App.Configuration.Enabled) Task.Run(() => Start());
        }

        /// <summary>Picks the output for the next frame; the running loop keeps going. Counters start over.</summary>
        public bool TrySetOutput(string name)
        {
            IFrameOutput output = name == "wled" ? (IFrameOutput)Wled : name == "hyperhdr" ? HyperHdr : null;
            if (output == null) return false;
            output.Reset();
            _output = output;
            Stats.Reset();
            if (App.Configuration.Enabled) Task.Run(() => Start());
            return true;
        }

        public CaptureStats.Snapshot ReadStats()
        {
            CaptureStats.Snapshot s = Stats.Read(_targetFps);
            IFrameOutput output = _output;
            s.mode = _mode.Label;
            s.resolution = Capturer.Resolution;
            s.output = output.Label;
            s.outputTarget = output.Target;
            s.outputState = output.State;
            if (output == Wled) Wled.Describe(s);
            return s;
        }

        public async Task Start()
        {
            // Exactly one capture loop. The display handler, UpdateURI, the output switch and the
            // UI's "enabled" all call Start, and stacked loops would fight over the capture API.
            if (Interlocked.Exchange(ref _loopRunning, 1) == 1) return;
            try { await RunAsync(); }
            catch (Exception e) { Capturer.ReportError("capture loop", e); }
            finally { Interlocked.Exchange(ref _loopRunning, 0); }
        }

        private async Task RunAsync()
        {
            if (!App.Configuration.Enabled) return;
            if (!_output.Configured)
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
                IFrameOutput output = _output;
                double start = Stats.Now(), captured, prepared;
                int peak = 0;
                try
                {
                    Color[] colors = Capturer.GetColors();
                    captured = Stats.Now();
                    foreach (Color c in colors) peak = Math.Max(peak, Math.Max(c.R, Math.Max(c.G, c.B)));
                    output.Prepare(colors);
                    prepared = Stats.Now();
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
                bool sent = await output.SendAsync();
                double worked = Stats.Now();

                // Pace against a deadline instead of sleeping a fixed time: capture, prepare and send
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
                Stats.AddFrame(epoch, captured - start, prepared - captured, worked - prepared, end - worked, end - start, sent, peak);
            }
            Capturer.SetState("stopped");
        }

        /// <summary>Display turned off: stop capturing and send one last frame through the active output.</summary>
        public async Task Stop()
        {
            if (App.Configuration.Enabled) App.Configuration.Enabled = false;
            IFrameOutput output = _output;
            output.Prepare(Capturer.GetColors());
            await output.SendAsync();
        }
    }
}
