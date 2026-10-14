namespace HyperTizen
{
    /// <summary>
    /// User-facing capture presets. Each sets only parameters the capture loop really has; sampling
    /// density is not one of them (14 fixed points, batched by the firmware's ScreenCapturePoints, mapped
    /// one-to-one onto ToImage's border strips). Colour smoothing applies only to WLED Direct; HyperHDR smooths its own.
    /// </summary>
    public sealed class CaptureMode
    {
        /// <summary>Stored in the "captureMode" preference.</summary>
        public string Name;
        public string Label;

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

        public static readonly CaptureMode Fast = new CaptureMode { Name = "fast", Label = "Fast", TargetFps = 30, KeepFrameGrid = false, ErrorBackoffMs = 1000, ConditionRetryMs = 5000, SmoothingMs = 50 };
        public static readonly CaptureMode Balanced = new CaptureMode { Name = "balanced", Label = "Balanced", TargetFps = 25, KeepFrameGrid = false, ErrorBackoffMs = 5000, ConditionRetryMs = 10000, SmoothingMs = 90 };
        public static readonly CaptureMode Smooth = new CaptureMode { Name = "smooth", Label = "Smooth", TargetFps = 15, KeepFrameGrid = true, ErrorBackoffMs = 5000, ConditionRetryMs = 10000, SmoothingMs = 150 };

        public static readonly CaptureMode[] All = { Fast, Balanced, Smooth };

        /// <summary>The mode with this stored name, or null.</summary>
        public static CaptureMode Find(string name)
        {
            foreach (CaptureMode mode in All) if (mode.Name == name) return mode;
            return null;
        }
    }
}
