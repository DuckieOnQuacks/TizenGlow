using System;
using System.Globalization;

namespace TizenGlow.Wled
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
    /// Each edge's LEDs are coloured only from that edge's samples (the active PointSet), interpolated by
    /// position along the edge (LEDs outside the first/last sample take that sample's colour; an edge with
    /// no sample stays dark). The physical strip is that loop entered at Start and walked clockwise or
    /// counter-clockwise. Shift then moves the picture round the loop: each LED shows the LED Shift places
    /// counter-clockwise of it, so a positive shift moves the colours clockwise whichever way the strip
    /// runs (for a strip whose first LED isn't exactly at the corner). All of it is precomputed into one
    /// (sample a, sample b, weight) triple per physical LED, so mapping a frame is a single pass.
    /// </summary>
    public sealed class LedLayout
    {
        public const int MaxPerSide = 2000;
        public const int MaxShift = 4 * MaxPerSide;

        private static readonly string[] CornerNames = { "topleft", "topright", "bottomright", "bottomleft" };

        public readonly int Top, Right, Bottom, Left;
        public readonly StartCorner Start;
        public readonly bool Clockwise;
        public readonly int Shift;
        public readonly PointSet Points;
        public int Count { get { return Top + Right + Bottom + Left; } }

        /// <summary>Samples per frame this layout maps from (Points.Count).</summary>
        public int SampleCount { get { return Points.Count; } }

        private readonly int[] _slot;      // physical LED -> its place on the canonical loop
        private readonly int[] _canonical; // physical LED -> canonical LED it shows (slot moved by Shift)
        private readonly int[] _a, _b;     // physical LED -> sample indices (-1: no sample on that edge)
        private readonly float[] _w;       // physical LED -> weight of sample b

        public LedLayout(int top, int right, int bottom, int left, StartCorner start, bool clockwise, PointSet points, int shift = 0)
        {
            foreach (int n in new[] { top, right, bottom, left })
                if (n < 0 || n > MaxPerSide) throw new ArgumentOutOfRangeException(nameof(top), "each side must be 0-" + MaxPerSide);
            if (top + right + bottom + left == 0) throw new ArgumentException("the layout needs at least one LED");
            if (points == null) throw new ArgumentNullException(nameof(points));

            Top = top; Right = right; Bottom = bottom; Left = left; Start = start; Clockwise = clockwise; Shift = shift; Points = points;
            int count = Count;
            int[] perEdge = { top, right, bottom, left };

            var ca = new int[count];
            var cb = new int[count];
            var cw = new float[count];
            for (int edge = 0, c = 0; edge < 4; edge++)
            {
                int[] samples = points.Edges[edge];
                var along = new double[samples.Length];
                for (int i = 0; i < samples.Length; i++) along[i] = PointSet.Along(edge, points.Points[samples[i]]);
                for (int i = 0; i < perEdge[edge]; i++, c++)
                    Interpolate(samples, along, (i + 0.5) / perEdge[edge], out ca[c], out cb[c], out cw[c]);
            }

            int first = start == StartCorner.TopLeft ? 0
                : start == StartCorner.TopRight ? top
                : start == StartCorner.BottomRight ? top + right
                : top + right + bottom;
            _slot = new int[count];
            _canonical = new int[count];
            _a = new int[count];
            _b = new int[count];
            _w = new float[count];
            for (int p = 0; p < count; p++)
            {
                // Counter-clockwise from a corner starts with the LED just before it in canonical order.
                int slot = clockwise ? (first + p) % count : ((first - 1 - p) % count + count) % count;
                int c = ((slot - shift) % count + count) % count;
                _slot[p] = slot;
                _canonical[p] = c;
                _a[p] = ca[c];
                _b[p] = cb[c];
                _w[p] = cw[c];
            }
        }

        private static void Interpolate(int[] samples, double[] along, double t, out int a, out int b, out float w)
        {
            if (samples.Length == 0) { a = b = -1; w = 0; return; }
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

        /// <summary>The same layout with another shift.</summary>
        public LedLayout WithShift(int shift)
        {
            return new LedLayout(Top, Right, Bottom, Left, Start, Clockwise, Points, shift);
        }

        /// <summary>The same layout mapped from another point set (the capture mode or black bars changed).</summary>
        public LedLayout WithPoints(PointSet points)
        {
            return points == Points ? this : new LedLayout(Top, Right, Bottom, Left, Start, Clockwise, points, Shift);
        }

        /// <summary>The canonical LED shown by physical LED p (for tests and diagnostics).</summary>
        public int CanonicalIndex(int p) { return _canonical[p]; }

        /// <summary>Where physical LED p sits on the canonical loop (unaffected by Shift): for the preview.</summary>
        public int Slot(int p) { return _slot[p]; }

        /// <summary>
        /// samples: SampleCount capture points as RGB floats. leds: Count x RGB, filled in physical strip
        /// order. Linear RGB interpolation; a different colour space would only change this loop.
        /// </summary>
        public void Map(float[] samples, float[] leds)
        {
            for (int p = 0, o = 0; p < _a.Length; p++, o += 3)
            {
                if (_a[p] < 0) { leds[o] = leds[o + 1] = leds[o + 2] = 0; continue; }
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

        public static bool TryParse(string value, PointSet points, out LedLayout layout)
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
