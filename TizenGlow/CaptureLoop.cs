using System.Threading.Tasks;
using Tizen.Applications;
using TizenGlow.Wled;
using System;
using System.Globalization;
using System.Reflection;
using System.Threading;

namespace TizenGlow
{
    /// <summary>
    /// Owns the single capture loop. Each frame is captured once and sent to WLED (WledOutput).
    /// </summary>
    internal class CaptureLoop
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
            var attr = typeof(CaptureLoop).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            string version = attr != null ? attr.InformationalVersion : "unknown";
            int plus = version.IndexOf('+'); // the SDK appends "+<commit>"
            return plus >= 0 ? version.Substring(0, plus) : version;
        }

        /// <summary>Live performance numbers; reset when capture starts and when the mode or target FPS changes.</summary>
        public static readonly CaptureStats Stats = new CaptureStats();

        /// <summary>Failed frames in a row before the capture settings are read again.</summary>
        internal static int RecoverAfterFailures = 2;

        /// <summary>How often a running loop checks whether the TV changed its capture settings.</summary>
        internal static int ConditionCheckMs = 5000;

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

        public readonly WledOutput Wled;

        public CaptureLoop()
        {
            // Mode first: it sets its own target FPS, which a saved targetFps then fine-tunes.
            if (Preference.Contains("captureMode")) TrySetMode(Preference.Get<string>("captureMode"));
            if (Preference.Contains("targetFps")) TrySetTargetFps(Preference.Get<string>("targetFps"));

            Wled = new WledOutput(() => _mode.SmoothingMs);
            if (Preference.Contains("wledTarget")) Wled.TrySetTarget(Preference.Get<string>("wledTarget"));
            if (Preference.Contains("ledShift")) Wled.TrySetShift(Preference.Get<string>("ledShift"));
            if (Preference.Contains("ledLayout")) Wled.TrySetLayout(Preference.Get<string>("ledLayout"));
            if (Preference.Contains("wledBrightness")) Wled.TrySetBrightness(Preference.Get<string>("wledBrightness"));
            if (Preference.Contains("wledBlackThreshold")) Wled.TrySetBlackThreshold(Preference.Get<string>("wledBlackThreshold"));
            if (Preference.Contains("wledSmoothing")) Wled.TrySetSmoothing(Preference.Get<string>("wledSmoothing"));
            if (Preference.Contains("wledGamma")) Wled.TrySetGamma(Preference.Get<string>("wledGamma"));
            if (Preference.Contains("wledSaturation")) Wled.TrySetSaturation(Preference.Get<string>("wledSaturation"));
            if (Preference.Contains("wledTvOff")) Wled.TrySetTvOff(Preference.Get<string>("wledTvOff"));
            if (Preference.Contains("letterbox")) Letterbox.TrySetMode(Preference.Get<string>("letterbox"));
            Autostart.TrySet(Preference.Contains("autostart") ? Preference.Get<string>("autostart") : "on"); // re-registered on every start
            if (Preference.Contains("levels")) Levels.TrySetEnabled(Preference.Get<string>("levels"));
            if (Preference.Contains("levelsWhite")) Levels.TrySetWhite(Preference.Get<string>("levelsWhite"));
            if (Preference.Contains("levelsBlack")) Levels.TrySetBlack(Preference.Get<string>("levelsBlack"));

            Task.Run(() => Start());
        }

        public CaptureStats.Snapshot ReadStats()
        {
            CaptureStats.Snapshot s = Stats.Read(_targetFps);
            s.mode = _mode.Label;
            s.resolution = Capturer.Resolution;
            s.outputTarget = Wled.Target;
            s.outputState = Wled.State;
            Wled.Describe(s);

            PointSet points = Letterbox.Inset ? _mode.BarsPoints : _mode.Points;
            Condition condition = Capturer.CurrentCondition;
            s.points = points.Count;
            s.pointsPerEdge = points.Describe();
            s.capturePoints = new double[points.Count][];
            for (int i = 0; i < points.Count; i++) s.capturePoints[i] = new[] { Math.Round(points.Points[i].X, 4), Math.Round(points.Points[i].Y, 4) };
            s.pointsPerRound = condition.ScreenCapturePoints;
            s.sleepMs = condition.SleepMS;
            s.ceilingFps = points.CeilingFps(condition.ScreenCapturePoints, condition.SleepMS);
            s.letterbox = Letterbox.State;
            s.autostart = Autostart.State;
            s.levels = new CaptureStats.LevelsInfo { enabled = Levels.Enabled, white = Levels.White, black = Levels.Black, measuring = Levels.Measuring, result = Levels.Result };
            return s;
        }

        public async Task Start()
        {
            // Exactly one capture loop. The display handler, saving a WLED target and the UI's
            // "enabled" all call Start, and stacked loops would fight over the capture API.
            if (Interlocked.Exchange(ref _loopRunning, 1) == 1) return;
            try { await RunAsync(); }
            catch (Exception e) { Capturer.ReportError("capture loop", e); }
            finally { Interlocked.Exchange(ref _loopRunning, 0); }
        }

        private async Task RunAsync()
        {
            if (!App.Configuration.Enabled) return;
            if (!Wled.Configured)
            {
                Capturer.SetState("no-server");
                return;
            }

            Capturer.SetState("starting");
            if (!await ReadConditionAsync()) return;

            Stats.Reset(); // fresh numbers for every capture session
            double nextFrameMs = Stats.Now();
            double lastConditionCheck = nextFrameMs;
            int failedInRow = 0;
            while (App.Configuration.Enabled)
            {
                int epoch = Stats.Epoch;
                CaptureMode mode = _mode;
                double start = Stats.Now(), captured, prepared;
                int peak = 0;
                try
                {
                    bool probe;
                    PointSet points = Letterbox.Choose(mode, start, out probe);
                    Capturer.SetCapturePoints(points.Points);
                    Color[] colors = Capturer.GetColors();
                    captured = Stats.Now();
                    foreach (Color c in colors) peak = Math.Max(peak, Math.Max(c.R, Math.Max(c.G, c.B)));
                    Levels.Observe(colors, captured);
                    points = Letterbox.AfterCapture(colors, points, probe, mode, captured);
                    Levels.Apply(colors);
                    Wled.Prepare(colors, points);
                    prepared = Stats.Now();
                }
                catch (Exception e)
                {
                    Stats.AddFailed(epoch);
                    Capturer.ReportError("frame capture", e);
                    if (++failedInRow >= RecoverAfterFailures)
                    {
                        // Frames keep failing: the TV may have changed its capture settings (a console, Game
                        // Mode, 120 Hz). Read them again, re-probing if the current API no longer answers;
                        // when they changed, try again at once.
                        failedInRow = 0;
                        bool changed;
                        if (Capturer.RefreshCondition(out changed))
                        {
                            if (changed) { nextFrameMs = Stats.Now(); continue; }
                        }
                        else
                        {
                            // A failed re-probe leaves no usable API: wait until the settings can be read
                            // again, as at start, rather than capture without them.
                            if (!await ReadConditionAsync()) return;
                            nextFrameMs = Stats.Now();
                            continue;
                        }
                    }
                    await Task.Delay(mode.ErrorBackoffMs);
                    nextFrameMs = Stats.Now();
                    continue;
                }
                failedInRow = 0;

                // The TV can also change its capture settings without any frame failing (resolution, points
                // per round, wait): look every few seconds. Only a change is written anywhere.
                if (captured - lastConditionCheck >= ConditionCheckMs)
                {
                    lastConditionCheck = captured;
                    bool changed;
                    Capturer.RefreshCondition(out changed);
                }

                // Switched off while this frame was captured (a slow capture can outlast HaltAsync's wait):
                // drop it, since a frame sent after the strip went off would switch it back on.
                if (!App.Configuration.Enabled) break;

                // Only a frame that actually came back from the capture API counts as running.
                Capturer.SetState("running");
                bool sent = await Wled.SendAsync();
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

        /// <summary>
        /// Reads the capture settings, retrying slowly while they can't be read. False when capture should stop:
        /// no usable API on this TV, or capture was switched off while waiting.
        /// </summary>
        private async Task<bool> ReadConditionAsync()
        {
            while (!Capturer.GetCondition())
            {
                // A missing API never appears at runtime. Otherwise the API exists but the condition
                // read failed (already persisted and notified): retry slowly instead of spinning.
                if (Capturer.Unsupported) return false;
                await Task.Delay(_mode.ConditionRetryMs);
                if (!App.Configuration.Enabled)
                {
                    Capturer.SetState("stopped");
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Capture off, and waits (briefly) for the loop to finish its frame, so none of its frames lands after
        /// what the caller sends next. A frame sent after the strip was switched off would switch it back on.
        /// </summary>
        private async Task HaltAsync()
        {
            App.Configuration.Enabled = false;
            for (int waited = 0; Volatile.Read(ref _loopRunning) == 1 && waited < 3000; waited += 20) await Task.Delay(20);
        }

        /// <summary>The On/Off app's "off": capture off and the strip off.</summary>
        public async Task SwitchOffAsync()
        {
            await HaltAsync();
            await Wled.SwitchOffAsync();
        }

        /// <summary>Display turned off: stop capturing and send one last frame.</summary>
        public async Task Stop()
        {
            bool wasCapturing = App.Configuration.Enabled;
            await HaltAsync();
            try
            {
                PointSet points = Letterbox.Inset ? _mode.BarsPoints : _mode.Points;
                Capturer.SetCapturePoints(points.Points);
                Color[] colors = Capturer.GetColors();
                Levels.Apply(colors);
                Wled.Prepare(colors, points);
                await Wled.SendAsync();
            }
            catch (Exception) { } // capture may not answer while the panel turns off
            // Only a strip TizenGlow was driving: with capture off, WLED's own effect is left alone.
            if (wasCapturing) await Wled.TurnOffWithTvAsync();
        }
    }
}
