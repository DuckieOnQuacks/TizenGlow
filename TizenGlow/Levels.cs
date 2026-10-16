using System;
using System.Globalization;
using Tizen.Applications;

namespace TizenGlow
{
    /// <summary>
    /// Black and white points of the capture. The TV's capture doesn't necessarily return 0 for black and
    /// 255 for white (one S90D returns about 170 for white), which leaves the LEDs dim and dull. Measured
    /// from the calibration screen's Full White and Full Black, the samples are stretched so measured
    /// white becomes 255,255,255 and measured black 0,0,0, per channel, before either output sees them.
    /// </summary>
    public static class Levels
    {
        public const int MinSpan = 32;                 // white must be at least this much above black, per channel
        public const int MaxRaw = 1023;                // the capture API's values are at most 10-bit
        internal static int MeasureFrames = 6, MeasureMs = 1500;

        private static readonly object _lock = new object();
        private static volatile bool _enabled = true;
        private static int[] _white = { 255, 255, 255 }, _black = { 0, 0, 0 };

        private static string _measuring;              // "white" or "black" while a measurement runs
        private static double _since;
        private static int _frames;
        private static readonly double[] _sum = new double[3];
        private static readonly int[] _min = new int[3], _max = new int[3];
        private static volatile string _result;

        public static bool Enabled { get { return _enabled; } }
        public static string White { get { lock (_lock) return Join(_white); } }
        public static string Black { get { lock (_lock) return Join(_black); } }
        public static bool Measuring { get { return _measuring != null; } }
        /// <summary>What the last measurement did, for the UI.</summary>
        public static string Result { get { return _result; } }
        /// <summary>True when applying would change nothing (nothing measured yet).</summary>
        public static bool Identity { get { lock (_lock) return _white[0] == 255 && _white[1] == 255 && _white[2] == 255 && _black[0] == 0 && _black[1] == 0 && _black[2] == 0; } }

        public static bool TrySetEnabled(string value)
        {
            if (value != "on" && value != "off") return false;
            _enabled = value == "on";
            return true;
        }

        public static bool TrySetWhite(string value) { return TrySet(value, true); }
        public static bool TrySetBlack(string value) { return TrySet(value, false); }

        private static bool TrySet(string value, bool white)
        {
            int[] rgb = Parse(value);
            if (rgb == null) return false;
            lock (_lock)
            {
                int[] other = white ? _black : _white;
                for (int c = 0; c < 3; c++) if ((white ? rgb[c] - other[c] : other[c] - rgb[c]) < MinSpan) return false;
                if (white) _white = rgb; else _black = rgb;
            }
            return true;
        }

        /// <summary>"r,g,b" with each value 0..MaxRaw, or null.</summary>
        private static int[] Parse(string value)
        {
            string[] parts = (value ?? "").Split(',');
            if (parts.Length != 3) return null;
            var rgb = new int[3];
            for (int c = 0; c < 3; c++)
                if (!int.TryParse(parts[c], NumberStyles.None, CultureInfo.InvariantCulture, out rgb[c]) || rgb[c] > MaxRaw) return null;
            return rgb;
        }

        private static string Join(int[] rgb) { return rgb[0] + "," + rgb[1] + "," + rgb[2]; }

        /// <summary>Starts measuring the next frames as the white or black point. The screen must show Full White / Full Black.</summary>
        public static bool BeginMeasure(string which)
        {
            if (which != "white" && which != "black") return false;
            lock (_lock)
            {
                _measuring = which;
                _since = double.NaN;
                _frames = 0;
                for (int c = 0; c < 3; c++) { _sum[c] = 0; _min[c] = int.MaxValue; _max[c] = int.MinValue; }
                _result = "measuring " + which + "...";
            }
            return true;
        }

        /// <summary>Called by the capture loop with every frame's raw samples (before Apply).</summary>
        public static void Observe(Color[] colors, double now)
        {
            if (_measuring == null || colors.Length == 0) return;
            lock (_lock)
            {
                if (_measuring == null) return;
                if (double.IsNaN(_since)) _since = now;
                foreach (Color color in colors)
                {
                    int[] v = { color.R, color.G, color.B };
                    for (int c = 0; c < 3; c++)
                    {
                        _sum[c] += v[c];
                        if (v[c] < _min[c]) _min[c] = v[c];
                        if (v[c] > _max[c]) _max[c] = v[c];
                    }
                }
                _frames++;
                if (_frames < MeasureFrames && now - _since < MeasureMs) return;
                Finish(colors.Length);
            }
        }

        private static void Finish(int samplesPerFrame)
        {
            string which = _measuring;
            _measuring = null;
            var mean = new int[3];
            int brightest = 0, spread = 0;
            for (int c = 0; c < 3; c++)
            {
                mean[c] = (int)Math.Round(_sum[c] / (_frames * samplesPerFrame));
                brightest = Math.Max(brightest, mean[c]);
                spread = Math.Max(spread, _max[c] - _min[c]);
            }
            string stamp = DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "Z";
            if (which == "white" && brightest < 48) { _result = "white not measured: the samples are dark (" + Join(mean) + "). Is the screen showing Full White with capture running?"; return; }
            if (which == "black" && brightest > 48) { _result = "black not measured: the samples are bright (" + Join(mean) + "). Is the screen showing Full Black?"; return; }
            if (spread > Math.Max(24, brightest / 4)) { _result = which + " not measured: the samples differ too much (" + spread + "). Is the whole screen one colour?"; return; }
            int[] other = which == "white" ? _black : _white;
            for (int c = 0; c < 3; c++)
                if ((which == "white" ? mean[c] - other[c] : other[c] - mean[c]) < MinSpan) { _result = which + " not measured: no contrast with the " + (which == "white" ? "black" : "white") + " point (" + Join(mean) + ")"; return; }
            if (which == "white") _white = mean; else _black = mean;
            try { Preference.Set(which == "white" ? "levelsWhite" : "levelsBlack", Join(mean)); } catch { }
            _result = which + " measured " + Join(mean) + " at " + stamp;
        }

        /// <summary>Stretches the samples in place so the measured black and white points become 0 and 255. No-op until something is measured.</summary>
        public static void Apply(Color[] colors)
        {
            if (!_enabled) return;
            int[] white, black;
            lock (_lock) { white = _white; black = _black; }
            if (white[0] == 255 && white[1] == 255 && white[2] == 255 && black[0] == 0 && black[1] == 0 && black[2] == 0) return;
            for (int i = 0; i < colors.Length; i++)
            {
                colors[i].R = Stretch(colors[i].R, black[0], white[0]);
                colors[i].G = Stretch(colors[i].G, black[1], white[1]);
                colors[i].B = Stretch(colors[i].B, black[2], white[2]);
            }
        }

        private static int Stretch(int v, int black, int white)
        {
            int r = (int)Math.Round((v - black) * 255.0 / (white - black));
            return r < 0 ? 0 : r > 255 ? 255 : r;
        }

        /// <summary>Tests only.</summary>
        internal static void Reset()
        {
            lock (_lock)
            {
                _enabled = true;
                _white = new[] { 255, 255, 255 };
                _black = new[] { 0, 0, 0 };
                _measuring = null;
                _result = null;
            }
        }
    }
}
