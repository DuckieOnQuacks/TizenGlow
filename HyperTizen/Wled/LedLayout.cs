using System;
using System.Globalization;

namespace HyperTizen.Wled
{
    public enum StartCorner { TopLeft, TopRight, BottomRight, BottomLeft }

    /// <summary>
    /// Maps the capture samples onto a physical LED strip around the screen.
    ///
    /// Canonical order is one loop clockwise from the top-left corner, as seen from the front:
    ///   top    left to right
    ///   right  top to bottom
    ///   bottom right to left
    ///   left   bottom to top
    /// Each edge's LEDs are coloured only from that edge's samples, interpolated by position along the
    /// edge (LEDs outside the first/last sample take that sample's colour). The physical strip is that
    /// loop entered at Start and walked clockwise or counter-clockwise. All of it is precomputed into
    /// one (sample a, sample b, weight) triple per physical LED, so mapping a frame is a single pass.
    /// </summary>
    public sealed class LedLayout
    {
        public const int MaxPerSide = 2000;

        // Capture points on each edge (indices into Capturer's table), in canonical order. Points 14
        // and 15 sit mid-screen and are not used.
        public static readonly int[][] EdgeSamples =
        {
            new[] { 0, 1, 2, 3 },   // top
            new[] { 4, 5, 6 },      // right
            new[] { 7, 8, 9, 10 },  // bottom
            new[] { 11, 12, 13 },   // left
        };

        private static readonly string[] CornerNames = { "topleft", "topright", "bottomright", "bottomleft" };

        public readonly int Top, Right, Bottom, Left;
        public readonly StartCorner Start;
        public readonly bool Clockwise;
        public int Count { get { return Top + Right + Bottom + Left; } }

        private readonly int[] _canonical; // physical LED -> canonical LED
        private readonly int[] _a, _b;     // physical LED -> sample indices
        private readonly float[] _w;       // physical LED -> weight of sample b

        public LedLayout(int top, int right, int bottom, int left, StartCorner start, bool clockwise, CapturePoint[] points)
        {
            foreach (int n in new[] { top, right, bottom, left })
                if (n < 0 || n > MaxPerSide) throw new ArgumentOutOfRangeException(nameof(top), "each side must be 0-" + MaxPerSide);
            if (top + right + bottom + left == 0) throw new ArgumentException("the layout needs at least one LED");

            Top = top; Right = right; Bottom = bottom; Left = left; Start = start; Clockwise = clockwise;
            int count = Count;
            int[] perEdge = { top, right, bottom, left };

            var ca = new int[count];
            var cb = new int[count];
            var cw = new float[count];
            for (int edge = 0, c = 0; edge < 4; edge++)
            {
                int[] samples = EdgeSamples[edge];
                var along = new double[samples.Length];
                for (int i = 0; i < samples.Length; i++) along[i] = Along(edge, points[samples[i]]);
                for (int i = 0; i < perEdge[edge]; i++, c++)
                    Interpolate(samples, along, (i + 0.5) / perEdge[edge], out ca[c], out cb[c], out cw[c]);
            }

            int first = start == StartCorner.TopLeft ? 0
                : start == StartCorner.TopRight ? top
                : start == StartCorner.BottomRight ? top + right
                : top + right + bottom;
            _canonical = new int[count];
            _a = new int[count];
            _b = new int[count];
            _w = new float[count];
            for (int p = 0; p < count; p++)
            {
                // Counter-clockwise from a corner starts with the LED just before it in canonical order.
                int c = clockwise ? (first + p) % count : ((first - 1 - p) % count + count) % count;
                _canonical[p] = c;
                _a[p] = ca[c];
                _b[p] = cb[c];
                _w[p] = cw[c];
            }
        }

        /// <summary>Position of a capture point along its edge in canonical direction, 0..1.</summary>
        private static double Along(int edge, CapturePoint p)
        {
            switch (edge)
            {
                case 0: return p.X;
                case 1: return p.Y;
                case 2: return 1 - p.X;
                default: return 1 - p.Y;
            }
        }

        private static void Interpolate(int[] samples, double[] along, double t, out int a, out int b, out float w)
        {
            int last = samples.Length - 1;
            if (t <= along[0]) { a = b = samples[0]; w = 0; return; }
            for (int j = 0; j < last; j++)
            {
                if (t <= along[j + 1])
                {
                    a = samples[j];
                    b = samples[j + 1];
                    w = (float)((t - along[j]) / (along[j + 1] - along[j]));
                    return;
                }
            }
            a = b = samples[last];
            w = 0;
        }

        /// <summary>The canonical LED shown by physical LED p (for tests and diagnostics).</summary>
        public int CanonicalIndex(int p) { return _canonical[p]; }

        /// <summary>
        /// samples: 16 capture points as RGB floats (48 values). leds: Count x RGB, filled in physical
        /// strip order. Linear RGB interpolation; a different colour space would only change this loop.
        /// </summary>
        public void Map(float[] samples, float[] leds)
        {
            for (int p = 0, o = 0; p < _a.Length; p++, o += 3)
            {
                int a = _a[p] * 3, b = _b[p] * 3;
                float w = _w[p];
                leds[o] = samples[a] + (samples[b] - samples[a]) * w;
                leds[o + 1] = samples[a + 1] + (samples[b + 1] - samples[a + 1]) * w;
                leds[o + 2] = samples[a + 2] + (samples[b + 2] - samples[a + 2]) * w;
            }
        }

        /// <summary>Stored form: "top,right,bottom,left,corner,direction", e.g. "100,50,100,50,bottomleft,cw".</summary>
        public string Serialize()
        {
            return Top + "," + Right + "," + Bottom + "," + Left + "," + CornerNames[(int)Start] + "," + (Clockwise ? "cw" : "ccw");
        }

        public static bool TryParse(string value, CapturePoint[] points, out LedLayout layout)
        {
            layout = null;
            string[] parts = (value ?? "").Split(',');
            if (parts.Length != 6) return false;
            var counts = new int[4];
            for (int i = 0; i < 4; i++)
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out counts[i]) || counts[i] > MaxPerSide) return false;
            int corner = Array.IndexOf(CornerNames, parts[4]);
            if (corner < 0 || (parts[5] != "cw" && parts[5] != "ccw")) return false;
            if (counts[0] + counts[1] + counts[2] + counts[3] == 0) return false;
            layout = new LedLayout(counts[0], counts[1], counts[2], counts[3], (StartCorner)corner, parts[5] == "cw", points);
            return true;
        }
    }
}
