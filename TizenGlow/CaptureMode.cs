namespace TizenGlow
{
    /// <summary>
    /// User-facing capture presets. The main choice is how many points a frame samples: the firmware reads
    /// ScreenCapturePoints per round and waits SleepMS after each, so a frame takes rounds x wait (on an
    /// S90D: 2 per round, 20 ms, so 6 points is 60 ms and 14 points 140 ms). Fewer points means faster,
    /// lower-latency frames and coarser colour along each edge. Colour smoothing applies only to WLED
    /// Direct; HyperHDR smooths its own.
    /// </summary>
    public sealed class CaptureMode
    {
        /// <summary>Stored in the "captureMode" preference.</summary>
        public string Name;
        public string Label;

        /// <summary>The points a frame samples, and the same with the rows moved in for black bars.</summary>
        public PointSet Points, BarsPoints;

        /// <summary>Target FPS applied when the mode is picked; the Target FPS setting can still fine-tune it.</summary>
        public int TargetFps;

        /// <summary>
        /// What a frame that overran its slot does. false: the next frame starts at once (lowest latency,
        /// intervals follow the capture time). true: it waits for the next slot of the target-FPS grid, so
        /// frames are always a whole number of intervals apart (steadier, lower throughput).
        /// </summary>
        public bool KeepFrameGrid;

        /// <summary>Pause after a failed frame before capturing again.</summary>
        public int ErrorBackoffMs;

        /// <summary>Retry interval while the capture API exists but its condition read fails.</summary>
        public int ConditionRetryMs;

        /// <summary>
        /// WLED Direct smoothing time constant when its Smoothing setting is on (time to cover ~63% of a
        /// colour change). HyperHDR output is never smoothed here; HyperHDR does its own.
        /// </summary>
        public int SmoothingMs;

        public static readonly CaptureMode Fast = Make("fast", "Fast", 2, 1, 2, 1, 20, 1000, 5000, 60);
        public static readonly CaptureMode Balanced = Make("balanced", "Balanced", 2, 2, 2, 2, 15, 5000, 10000, 100);
        public static readonly CaptureMode Detailed = Make("detailed", "Detailed", 4, 3, 4, 3, 10, 5000, 10000, 150);

        public static readonly CaptureMode[] All = { Fast, Balanced, Detailed };

        private static CaptureMode Make(string name, string label, int top, int right, int bottom, int left, int fps, int backoff, int retry, int smoothing)
        {
            var points = new PointSet(top, right, bottom, left);
            return new CaptureMode
            {
                Name = name, Label = label, Points = points, BarsPoints = points.WithBars(Letterbox.BarHeight),
                TargetFps = fps, KeepFrameGrid = false, ErrorBackoffMs = backoff, ConditionRetryMs = retry, SmoothingMs = smoothing,
            };
        }

        /// <summary>The mode with this stored name, or null. "smooth" (the old name of the 14-point mode) still finds Detailed.</summary>
        public static CaptureMode Find(string name)
        {
            if (name == "smooth") return Detailed;
            foreach (CaptureMode mode in All) if (mode.Name == name) return mode;
            return null;
        }
    }
}
