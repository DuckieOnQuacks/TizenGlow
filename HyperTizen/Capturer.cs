using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using SkiaSharp;
using Tizen.Applications;
using Tizen.Applications.Notifications;
using Tizen.System;

namespace HyperTizen
{
    public static class Capturer
    {
        private static Condition _condition;
        private static CaptureProbe _probe;
        private static CaptureFamily _api;
        private static string _state;
        private static readonly HashSet<string> _notified = new HashSet<string>();

        // A pixel read that keeps failing used to be retried forever, pinning a core.
        private const int MaxPixelAttempts = 100;

        public static string PlatformVersion
        {
            get
            {
                string version;
                return Information.TryGetValue("http://tizen.org/feature/platform.version", out version) ? version : "unknown";
            }
        }

        private static CapturePoint[] _capturedPoints = new CapturePoint[] {
            new CapturePoint(0.21, 0.05),
            new CapturePoint(0.45, 0.05),
            new CapturePoint(0.7, 0.05),
            new CapturePoint(0.93, 0.07),
            new CapturePoint(0.95, 0.275),
            new CapturePoint(0.95, 0.5),
            new CapturePoint(0.95, 0.8),
            new CapturePoint(0.79, 0.95),
            new CapturePoint(0.65, 0.95),
            new CapturePoint(0.35, 0.95),
            new CapturePoint(0.15, 0.95),
            new CapturePoint(0.05, 0.725),
            new CapturePoint(0.05, 0.4),
            new CapturePoint(0.05, 0.2),
            new CapturePoint(0.35, 0.5),
            new CapturePoint(0.65, 0.5)
        };
        
        /// <summary>True when libvideoenhance.so has no complete RGB-measure API, so retrying is pointless.</summary>
        public static bool Unsupported { get { return Probe().Candidates.Count == 0; } }

        /// <summary>
        /// Probes libvideoenhance.so once and publishes what it found. Cheap and call-free (only
        /// dlopen/dlsym), so it runs at service start even while capture is disabled.
        /// </summary>
        /// <param name="resolve">Test seam: symbol resolver to use instead of the real library.</param>
        public static CaptureProbe Probe(Func<string, IntPtr> resolve = null)
        {
            if (_probe != null && resolve == null) return _probe;
            string version = PlatformVersion;
            _probe = resolve == null ? CaptureProbe.Run(version) : CaptureProbe.Run(version, resolve);
            _api = null;
            _condition = default(Condition);
            Report("detectedPlatformVersion", version);
            Report("detectedApi", _probe.Selected);
            Report("captureCondition", "not read yet");
            SetState(_probe.Candidates.Count == 0 ? "unsupported" : "idle");
            return _probe;
        }

        /// <summary>
        /// Reads the panel's measure condition with the best complete API, falling back to the
        /// next complete one if it fails. Every failure is persisted and the TV notification
        /// says why; it never fails silently.
        /// </summary>
        public static bool GetCondition()
        {
            CaptureProbe probe = Probe();
            if (probe.Candidates.Count == 0)
            {
                if (probe.LoadError != null) Fail("unsupported", "cannot load libvideoenhance.so", "cannot load libvideoenhance.so (" + probe.LoadError + ")");
                else Fail("unsupported", "no complete ppi_ve/ve/cs_ve API", "no complete ppi_ve/ve/cs_ve API in libvideoenhance.so");
                return false;
            }

            var failures = new List<string>();
            string firstReason = null; // short form for the TV notification
            foreach (CaptureFamily family in probe.Candidates)
            {
                Condition condition;
                int res;
                try
                {
                    res = family.MeasureCondition(out condition);
                }
                catch (Exception e)
                {
                    failures.Add(family.Name + "_get_rgb_measure_condition threw " + e.GetType().FullName + ": " + e.Message);
                    firstReason = firstReason ?? family.Name + " threw " + e.GetType().Name;
                    continue;
                }

                Report("captureCondition", family.Name + " res=" + res + " points=" + condition.ScreenCapturePoints
                    + " size=" + condition.Width + "x" + condition.Height
                    + " density=" + condition.PixelDensityX + "x" + condition.PixelDensityY + " sleepMs=" + condition.SleepMS);

                if (res >= 0 && condition.ScreenCapturePoints > 0 && condition.Width > 0 && condition.Height > 0)
                {
                    _condition = condition;
                    _api = family;
                    Report("detectedApi", family.Name);
                    Report("lastCaptureError", failures.Count == 0 ? "none" : Diagnostic("GetCondition fell back to " + family.Name, string.Join("; ", failures)));
                    return true;
                }
                failures.Add(family.Name + "_get_rgb_measure_condition returned " + res + (res >= 0 ? " with an unusable condition" : ""));
                firstReason = firstReason ?? family.Name + " condition returned " + res;
            }

            Fail("error", firstReason, string.Join("; ", failures));
            return false;
        }

        /// <summary>Persists a capture-loop exception. No notification: the loop may hit it repeatedly.</summary>
        public static void ReportError(string phase, Exception e)
        {
            Report("lastCaptureError", Diagnostic(phase + " failed", e.GetType().FullName + ": " + e.Message));
            SetState("error");
        }

        /// <summary>captureState: idle, starting, running, stopped, no-server, unsupported or error.</summary>
        public static void SetState(string state)
        {
            if (state == _state) return; // the frame loop calls this every frame; only persist changes
            _state = state;
            Report("captureState", state);
        }

        private static void Fail(string state, string reason, string detail)
        {
            Report("lastCaptureError", Diagnostic(state == "unsupported" ? "probe" : "GetCondition failed", detail));
            SetState(state);
            Notify("Capture " + (state == "unsupported" ? "unavailable" : "failed") + ": " + reason + ". Details in HyperTizenUI.");
        }

        private static string Diagnostic(string what, string detail)
        {
            return DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + "Z " + what + ": " + detail
                + " | api=" + (_api != null ? _api.Name : _probe != null ? _probe.Selected : "not probed")
                + " | lib=" + CaptureProbe.LibraryPath
                + " | platform=" + PlatformVersion
                + " | symbols: " + (_probe != null ? _probe.Symbols : "not probed")
                + (_probe != null && _probe.LoadError != null ? " | load: " + _probe.LoadError : "");
        }

        private static void Report(string key, string value)
        {
            try { Preference.Set(key, value); }
            catch { } // ponytail: nowhere left to report a failed diagnostics write; it must not stop capture
        }

        private static void Notify(string content)
        {
            lock (_notified)
            {
                if (!_notified.Add(content)) return; // once per service run; condition retries would repeat it
            }
            try { NotificationManager.Post(new Notification { Title = "HyperTizen", Content = content, Count = 1 }); }
            catch { } // details are already persisted in lastCaptureError
        }

        public static void SetCapturePoints(CapturePoint[] capturePoints)
        {
            _capturedPoints = capturePoints;
        }
        
        public static Color[] GetColors()
        {
            Color[] colorData = new Color[_capturedPoints.Length];
            int[] updatedIndexes = new int[_condition.ScreenCapturePoints];
            CaptureFamily api = _api;

            int i = 0;
            while (i < _capturedPoints.Length)
            {
                if (_condition.ScreenCapturePoints == 0 || api == null) break;
                for (int j = 0; j < _condition.ScreenCapturePoints; j++)
                {
                    updatedIndexes[j] = i;
                    int x = (int)(_capturedPoints[i].X * (double)_condition.Width) - _condition.PixelDensityX / 2;
                    int y = (int)(_capturedPoints[i].Y * (double)_condition.Height) - _condition.PixelDensityY / 2;
                    x = (x >= _condition.Width - _condition.PixelDensityX) ? _condition.Width - (_condition.PixelDensityX + 1) : x;
                    y = (y >= _condition.Height - _condition.PixelDensityY) ? (_condition.Height - _condition.PixelDensityY + 1) : y;
                    int res = api.MeasurePosition(j, x, y);

                    i++;
                    if (res < 0)
                    {
                        // This should not happen, handle it.
                    }
                }

                if (_condition.SleepMS > 0)
                {
                    Thread.Sleep(_condition.SleepMS);
                }
                int k = 0;
                int failedReads = 0;
                while (k < _condition.ScreenCapturePoints)
                {
                    Color color;

                    int res = api.MeasurePixel(k, out color);

                    if (res < 0 || color.R > 1023 || color.G > 1023 || color.B > 1023)
                    {
                        if (++failedReads >= MaxPixelAttempts)
                        {
                            throw new InvalidOperationException(api.Name + "_get_rgb_measure_pixel gave no valid color for point " + k
                                + " in " + MaxPixelAttempts + " attempts (last result " + res + ", rgb " + color + ")");
                        }
                    } else
                    {
                        colorData[i - _condition.ScreenCapturePoints + k] = color;
                        k++;
                    }
                }
            }
            return colorData;
        }

        public static string ToImage(Color[] colors)
        {
            using (var image = new SKBitmap(64, 48))
            {
                for (int x = 0; x < 64; x++)
                {
                    Color color = colors[x / 16];
                    SKColor sKColor = ClampColor(color);
                    for (int y = 0; y < 4; y++)
                    {
                        image.SetPixel(x, y, sKColor);
                    }
                }

                for (int x = 0; x < 64; x++)
                {
                    Color color = colors[x / 16 + 7];
                    SKColor sKColor = ClampColor(color);
                    for (int y = 44; y < 48; y++)
                    {
                        image.SetPixel(x, y, sKColor);
                    }
                }

                for (int y = 0; y < 48; y++)
                {
                    Color color = colors[11 + y / 16];
                    SKColor sKColor = ClampColor(color);
                    for (int x = 0; x < 3; x++)
                    {
                        image.SetPixel(x, y, sKColor);
                    }
                }

                for (int y = 0; y < 48; y++)
                {
                    Color color = colors[4 + y / 16];
                    SKColor sKColor = ClampColor(color);
                    for (int x = 61; x < 64; x++)
                    {
                        image.SetPixel(x, y, sKColor);
                    }
                }

                using (var memoryStream = new MemoryStream())
                {
                    using (var data = SKImage.FromBitmap(image).Encode(SKEncodedImageFormat.Png, 100))
                    {
                        data.SaveTo(memoryStream);
                    }
                    byte[] imageBytes = memoryStream.ToArray();
                    string base64String = Convert.ToBase64String(imageBytes);
                    return base64String;
                }
            }
        }

        static SKColor ClampColor(Color color)
        {
            byte r = (byte)Math.Min(color.R, 255);
            byte g = (byte)Math.Min(color.G, 255);
            byte b = (byte)Math.Min(color.B, 255);
            return new SKColor(r, g, b);
        }
    }

    public struct Color
    {
        public int R;
        public int G;
        public int B;

        public override string ToString() { return R + "," + G + "," + B; }
    }

    public struct Condition
    {
        public int ScreenCapturePoints;

        public int PixelDensityX;

        public int PixelDensityY;

        public int SleepMS;

        public int Width;

        public int Height;
    }

    public struct CapturePoint
    {
        public CapturePoint(double x, double y) {
            this.X = x;
            this.Y = y;
        }

        public double X;
        public double Y;
    }
}
