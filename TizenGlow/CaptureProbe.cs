using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TizenGlow
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int MeasureConditionFn(out Condition condition);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int MeasurePositionFn(int index, int x, int y);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int MeasurePixelFn(int index, out Color color);

    /// <summary>One complete set of libvideoenhance RGB-measure exports, e.g. ppi_ve_*.</summary>
    public sealed class CaptureFamily
    {
        public string Name;
        public MeasureConditionFn MeasureCondition;
        public MeasurePositionFn MeasurePosition;
        public MeasurePixelFn MeasurePixel;
    }

    /// <summary>
    /// Finds which RGB-measure API this firmware's libvideoenhance.so exports. Samsung renamed
    /// the functions across firmware generations (cs_ve_ -> ve_ -> ppi_ve_), so the choice is
    /// made from the exports that actually exist. A family is only usable when all three
    /// functions are exported.
    /// </summary>
    public sealed class CaptureProbe
    {
        public const string LibraryPath = "/usr/lib/libvideoenhance.so";
        public static readonly string[] FamilyNames = { "ppi_ve", "ve", "cs_ve" };
        private static readonly string[] Functions = { "_get_rgb_measure_condition", "_set_rgb_measure_position", "_get_rgb_measure_pixel" };

        /// <summary>Complete families, best first. Empty means capture is unsupported.</summary>
        public readonly List<CaptureFamily> Candidates = new List<CaptureFamily>();

        /// <summary>Per-family result, e.g. "ppi_ve=complete, ve=missing, cs_ve=partial(missing cs_ve_get_rgb_measure_pixel)".</summary>
        public string Symbols = "not probed";

        /// <summary>Why libvideoenhance.so could not be loaded, or null if it loaded.</summary>
        public string LoadError;

        public string Selected { get { return Candidates.Count > 0 ? Candidates[0].Name : "unsupported"; } }

        public static CaptureProbe Run(string platformVersion)
        {
            IntPtr lib;
            try { lib = NativeLibrary.Load(LibraryPath); }
            catch (Exception e) { return new CaptureProbe { LoadError = e.GetType().FullName + ": " + e.Message }; }
            return Run(platformVersion, name => NativeLibrary.TryGetExport(lib, name, out IntPtr address) ? address : IntPtr.Zero);
        }

        /// <param name="resolve">Returns the export's address, or IntPtr.Zero if it is missing.</param>
        public static CaptureProbe Run(string platformVersion, Func<string, IntPtr> resolve)
        {
            var probe = new CaptureProbe();
            var complete = new Dictionary<string, CaptureFamily>();
            var report = new List<string>();

            foreach (string family in FamilyNames)
            {
                var addresses = new IntPtr[Functions.Length];
                var missing = new List<string>();
                for (int i = 0; i < Functions.Length; i++)
                {
                    addresses[i] = resolve(family + Functions[i]);
                    if (addresses[i] == IntPtr.Zero) missing.Add(family + Functions[i]);
                }

                if (missing.Count == 0)
                {
                    report.Add(family + "=complete");
                    complete[family] = new CaptureFamily
                    {
                        Name = family,
                        MeasureCondition = Marshal.GetDelegateForFunctionPointer<MeasureConditionFn>(addresses[0]),
                        MeasurePosition = Marshal.GetDelegateForFunctionPointer<MeasurePositionFn>(addresses[1]),
                        MeasurePixel = Marshal.GetDelegateForFunctionPointer<MeasurePixelFn>(addresses[2]),
                    };
                }
                else if (missing.Count == Functions.Length) report.Add(family + "=missing");
                else report.Add(family + "=partial(missing " + string.Join(" ", missing) + ")");
            }

            probe.Symbols = string.Join(", ", report);
            foreach (string family in PreferenceOrder(platformVersion))
            {
                if (complete.ContainsKey(family)) probe.Candidates.Add(complete[family]);
            }
            return probe;
        }

        /// <summary>
        /// Order to try complete families in. The version never selects a family on its own: it only
        /// puts the family stock v1.0.0 used first (cs_ve below Tizen 7, ve from 7), so TVs that already
        /// worked keep the exact same calls. Any other complete family is still tried after it.
        /// </summary>
        public static List<string> PreferenceOrder(string platformVersion)
        {
            var order = new List<string>(FamilyNames);
            int major;
            if (int.TryParse((platformVersion ?? "").Split('.')[0], out major))
            {
                string stock = major >= 7 ? "ve" : "cs_ve";
                order.Remove(stock);
                order.Insert(0, stock);
            }
            return order;
        }
    }
}
