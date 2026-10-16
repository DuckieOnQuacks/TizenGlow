using System;

namespace TizenGlow
{
    /// <summary>
    /// Black bars (letterboxed films) leave the top and bottom sample rows in the bars, so those LEDs stay
    /// dark for the whole film. Auto watches for it: once every top and bottom sample has been black for
    /// HoldMs while the sides show a picture, the rows move in by BarHeight. While moved in, one frame every
    /// ProbeMs samples the rows at the edge again; a picture there means the bars are gone. That probe
    /// frame shows the previous rows, so the LEDs never blink black for it.
    /// </summary>
    public static class Letterbox
    {
        /// <summary>2.39:1 on a 16:9 screen is 0.128 per bar; 2.0:1 is 0.056, which this still samples inside the picture.</summary>
        public const double BarHeight = 0.125;
        internal static int HoldMs = 3000, ProbeMs = 5000;
        public const int BlackMax = 8, PictureMin = 24; // raw channel values on a 0-255 scale

        private static readonly object _lock = new object();
        private static volatile string _mode = "auto";
        private static volatile bool _detected;
        private static double _blackSince = double.NaN, _lastProbe;
        private static Color[] _lastFrame; // the previous frame captured with the rows moved in

        /// <summary>"off", "auto" or "on" (stored as the "letterbox" preference).</summary>
        public static string Mode { get { return _mode; } }
        public static bool Detected { get { return _detected; } }

        /// <summary>True while the rows are moved in (forced on, or detected in auto).</summary>
        public static bool Inset { get { return _mode == "on" || (_mode == "auto" && _detected); } }

        /// <summary>off, watching, detected or always: what the UI shows.</summary>
        public static string State
        {
            get { return _mode == "off" ? "off" : _mode == "on" ? "always" : _detected ? "detected" : "watching"; }
        }

        public static bool TrySetMode(string value)
        {
            if (value != "off" && value != "auto" && value != "on") return false;
            lock (_lock)
            {
                _mode = value;
                _detected = false;
                _blackSince = double.NaN;
                _lastFrame = null;
            }
            return true;
        }

        /// <summary>The point set to capture with now. probe is true for a frame that checks the edge rows while bars are detected.</summary>
        public static PointSet Choose(CaptureMode mode, double now, out bool probe)
        {
            probe = false;
            if (_mode == "on") return mode.BarsPoints;
            if (_mode != "auto" || !_detected) return mode.Points;
            lock (_lock)
            {
                if (now - _lastProbe < ProbeMs) return mode.BarsPoints;
                _lastProbe = now;
                probe = true;
                return mode.Points;
            }
        }

        /// <summary>
        /// After a capture: updates the detection and returns the set the colours now belong to. A probe
        /// frame that still saw bars gets the previous frame's rows back and counts as a moved-in frame.
        /// </summary>
        public static PointSet AfterCapture(Color[] colors, PointSet used, bool probe, CaptureMode mode, double now)
        {
            if (_mode != "auto") return used;
            bool rowsBlack = true, sidesLit = false;
            foreach (int i in used.Edges[0]) if (Max(colors[i]) > BlackMax) rowsBlack = false;
            foreach (int i in used.Edges[2]) if (Max(colors[i]) > BlackMax) rowsBlack = false;
            foreach (int i in used.Edges[1]) if (Max(colors[i]) >= PictureMin) sidesLit = true;
            foreach (int i in used.Edges[3]) if (Max(colors[i]) >= PictureMin) sidesLit = true;

            lock (_lock)
            {
                if (probe)
                {
                    if (!rowsBlack)
                    {
                        // A picture at the edge: the bars are gone, and this frame already is the picture.
                        _detected = false;
                        _blackSince = double.NaN;
                        _lastFrame = null;
                        return used;
                    }
                    if (_lastFrame != null && _lastFrame.Length == colors.Length)
                    {
                        foreach (int i in used.Edges[0]) colors[i] = _lastFrame[i];
                        foreach (int i in used.Edges[2]) colors[i] = _lastFrame[i];
                    }
                    return mode.BarsPoints;
                }
                if (_detected)
                {
                    _lastFrame = (Color[])colors.Clone();
                    return used;
                }
                if (rowsBlack && sidesLit)
                {
                    if (double.IsNaN(_blackSince)) _blackSince = now;
                    else if (now - _blackSince >= HoldMs)
                    {
                        _detected = true;
                        _lastProbe = now;
                        _blackSince = double.NaN;
                    }
                }
                else _blackSince = double.NaN;
                return used;
            }
        }

        private static int Max(Color c) { return Math.Max(c.R, Math.Max(c.G, c.B)); }

        /// <summary>Tests only.</summary>
        internal static void Reset()
        {
            lock (_lock) { _mode = "auto"; _detected = false; _blackSince = double.NaN; _lastProbe = 0; _lastFrame = null; }
        }
    }
}
