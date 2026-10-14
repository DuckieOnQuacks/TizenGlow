using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HyperTizen.Wled
{
    /// <summary>
    /// WLED Direct: maps the capture samples onto the physical strip (LedLayout), applies optional
    /// smoothing, black threshold and brightness, and sends the strip to WLED as DDP (DdpSender).
    /// Buffers, the layout mapping and the socket are reused; only config changes rebuild them.
    /// One lock covers the capture loop, the test pattern task and config changes from the UI.
    /// </summary>
    internal sealed class WledOutput : IFrameOutput
    {
        public const int TestLedCount = 2048;   // test frames before a layout is saved; WLED ignores pixels past its strip
        internal static int TestHoldMs = 10000; // test colours are resent until then: WLED drops realtime data after 2.5 s (default)
        public const int TestIntervalMs = 100;
        public const int ErrorBackoffMs = 1000; // after a failed send, skip sends this long instead of retrying every frame

        private static readonly Regex HostPattern = new Regex(@"^[A-Za-z0-9.-]+$");

        private readonly object _lock = new object();
        private readonly Func<double> _clock;
        private readonly Func<int> _smoothingMs;
        private DdpSender _sender;

        private string _host;
        private int _port;
        private IPEndPoint _endpoint;
        private bool _resolving;
        private LedLayout _layout;
        private int _brightness = 100;
        private int _blackThreshold;
        private bool _smoothing = true;

        private readonly float[] _samples = new float[LedLayout.SampleCount * 3];
        private readonly float[] _smoothed = new float[LedLayout.SampleCount * 3];
        private bool _hasSmoothed;
        private double _lastPrepareMs;
        private float[] _leds = new float[0];
        private byte[] _rgb = new byte[0];
        private int _frameLength;

        private long _framesSent, _packetsSent, _sendErrors;
        private double _lastSendMs;
        private double _lastSuccessAt = double.NegativeInfinity, _lastErrorAt = double.NegativeInfinity, _backoffUntil;
        private string _lastError;
        private bool _lastSendFailed;
        private CancellationTokenSource _test;
        private bool _testActive;

        /// <param name="smoothingMs">Smoothing time constant while smoothing is on (follows the Capture Quality mode).</param>
        public WledOutput(Func<int> smoothingMs, Func<double> clock = null)
        {
            _smoothingMs = smoothingMs;
            if (clock == null)
            {
                var sw = Stopwatch.StartNew();
                clock = () => sw.Elapsed.TotalMilliseconds;
            }
            _clock = clock;
        }

        public string Label { get { return "WLED Direct"; } }
        public bool Configured { get { lock (_lock) return _host != null; } }
        public string Target { get { lock (_lock) return _host == null ? null : _host + ":" + _port; } }
        public LedLayout Layout { get { lock (_lock) return _layout; } }

        /// <summary>UDP has no connection, so this says what last happened rather than "connected".</summary>
        public string State
        {
            get
            {
                lock (_lock)
                {
                    double now = _clock();
                    if (_host == null) return "not configured";
                    if (_testActive) return "testing";
                    if (_lastSendFailed && now - _lastErrorAt < 5000) return "send error";
                    if (now - _lastSuccessAt < 2000) return "sending";
                    if (_layout == null) return "no LED layout";
                    return "ready";
                }
            }
        }

        // ---- settings (validated here: the UI is not the only thing that can reach port 8086) ----

        /// <summary>"host:port", host an IPv4 address or hostname.</summary>
        public bool TrySetTarget(string value)
        {
            int colon = (value ?? "").LastIndexOf(':');
            if (colon <= 0) return false;
            string host = value.Substring(0, colon);
            int port;
            if (!HostPattern.IsMatch(host) || !int.TryParse(value.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535) return false;
            lock (_lock)
            {
                _host = host;
                _port = port;
                _endpoint = null;
                _backoffUntil = 0;
                _lastError = null;
            }
            return true;
        }

        public bool TrySetLayout(string value)
        {
            LedLayout layout;
            if (!LedLayout.TryParse(value, Capturer.CapturePoints, out layout)) return false;
            lock (_lock) _layout = layout;
            return true;
        }

        public bool TrySetBrightness(string value) { return TrySetPercent(value, 100, ref _brightness); }
        public bool TrySetBlackThreshold(string value) { return TrySetPercent(value, 10, ref _blackThreshold); }

        public bool TrySetSmoothing(string value)
        {
            if (value != "on" && value != "off") return false;
            lock (_lock) { _smoothing = value == "on"; _hasSmoothed = false; }
            return true;
        }

        private bool TrySetPercent(string value, int max, ref int field)
        {
            int percent;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out percent) || percent > max) return false;
            lock (_lock) field = percent;
            return true;
        }

        public void Reset()
        {
            lock (_lock)
            {
                _framesSent = _packetsSent = _sendErrors = 0;
                _lastSendMs = 0;
                _lastError = null;
                _lastSuccessAt = _lastErrorAt = double.NegativeInfinity;
                _lastSendFailed = false;
                _backoffUntil = 0;
                _hasSmoothed = false;
            }
        }

        // ---- frames ----

        public void Prepare(Color[] samples)
        {
            lock (_lock)
            {
                _frameLength = 0;
                LedLayout layout = _layout;
                if (layout == null) return;

                for (int i = 0, o = 0; i < LedLayout.SampleCount; i++, o += 3)
                {
                    // Same 8-bit clamp ToImage applies for HyperHDR.
                    _samples[o] = Math.Min(255, Math.Max(0, samples[i].R));
                    _samples[o + 1] = Math.Min(255, Math.Max(0, samples[i].G));
                    _samples[o + 2] = Math.Min(255, Math.Max(0, samples[i].B));
                }

                // Time-based exponential smoothing on the samples (mapping is linear, so this equals
                // smoothing every LED, at a fraction of the work). alpha comes from the real time since
                // the last frame, so the response time stays the same whatever the frame rate.
                double now = _clock();
                double dt = now - _lastPrepareMs;
                int tau = _smoothing ? _smoothingMs() : 0;
                float alpha = !_hasSmoothed || tau <= 0 || dt >= 1000 ? 1f : (float)(1 - Math.Exp(-dt / tau));
                for (int j = 0; j < _smoothed.Length; j++) _smoothed[j] += alpha * (_samples[j] - _smoothed[j]);
                _hasSmoothed = true;
                _lastPrepareMs = now;

                int count = layout.Count;
                if (_leds.Length != count * 3)
                {
                    _leds = new float[count * 3];
                    _rgb = new byte[count * 3];
                }
                layout.Map(_smoothed, _leds);

                // Black threshold first (judged on the picture, not the dimmed output), then brightness.
                float threshold = _blackThreshold * 255f / 100f;
                float gain = _brightness / 100f;
                for (int o = 0; o < count * 3; o += 3)
                {
                    float r = _leds[o], g = _leds[o + 1], b = _leds[o + 2];
                    if (r < threshold && g < threshold && b < threshold) r = g = b = 0;
                    _rgb[o] = ToByte(r * gain);
                    _rgb[o + 1] = ToByte(g * gain);
                    _rgb[o + 2] = ToByte(b * gain);
                }
                _frameLength = count * 3;
            }
        }

        private static byte ToByte(float v) { return (byte)(v >= 255f ? 255 : v <= 0f ? 0 : (int)(v + 0.5f)); }

        public Task<bool> SendAsync()
        {
            lock (_lock)
            {
                // A running test pattern owns the strip; no layout yet means nothing to send.
                if (_testActive || _frameLength == 0) return Task.FromResult(false);
                return Task.FromResult(SendLocked(_rgb, _frameLength));
            }
        }

        private bool SendLocked(byte[] rgb, int length)
        {
            double now = _clock();
            if (now < _backoffUntil) return false;
            EnsureEndpoint();
            if (_endpoint == null) return false;
            try
            {
                if (_sender == null) _sender = new DdpSender();
                int packets = _sender.Send(_endpoint, rgb, length);
                double end = _clock();
                _lastSendMs = end - now;
                _framesSent++;
                _packetsSent += packets;
                _lastSuccessAt = end;
                _lastSendFailed = false;
                return true;
            }
            catch (Exception e)
            {
                // A failed send never stops capture: note it, back off briefly, and start over with a fresh socket.
                Fail(now, e.GetType().Name + ": " + e.Message);
                try { _sender?.Dispose(); } catch { }
                _sender = null;
                return false;
            }
        }

        private void Fail(double now, string message)
        {
            _sendErrors++;
            _lastError = DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "Z " + message;
            _lastErrorAt = now;
            _lastSendFailed = true;
            _backoffUntil = now + ErrorBackoffMs;
        }

        private void EnsureEndpoint()
        {
            if (_endpoint != null || _host == null || _resolving) return;
            IPAddress ip;
            if (IPAddress.TryParse(_host, out ip))
            {
                _endpoint = new IPEndPoint(ip, _port);
                return;
            }

            // Hostnames resolve in the background so a slow DNS lookup never stalls capture.
            _resolving = true;
            string host = _host;
            int port = _port;
            Dns.GetHostAddressesAsync(host).ContinueWith(t =>
            {
                lock (_lock)
                {
                    _resolving = false;
                    if (host != _host || port != _port) return; // target changed meanwhile
                    IPAddress v4 = null;
                    if (t.Status == TaskStatus.RanToCompletion)
                        foreach (IPAddress a in t.Result) if (a.AddressFamily == AddressFamily.InterNetwork) { v4 = a; break; }
                    if (v4 != null) _endpoint = new IPEndPoint(v4, port);
                    else Fail(_clock(), "cannot resolve " + host);
                }
            });
        }

        // ---- test pattern ----

        /// <summary>
        /// red / green / blue / white / off: fills the whole strip through the same DDP sender, works with
        /// capture off, ignores brightness and threshold, and holds for TestHoldMs. Pressing another one
        /// replaces it. While it runs, captured frames are not sent.
        /// </summary>
        public bool TryTest(string name)
        {
            byte r, g, b;
            switch (name)
            {
                case "red": r = 255; g = 0; b = 0; break;
                case "green": r = 0; g = 255; b = 0; break;
                case "blue": r = 0; g = 0; b = 255; break;
                case "white": r = g = b = 255; break;
                case "off": r = g = b = 0; break;
                default: return false;
            }

            CancellationTokenSource cts = new CancellationTokenSource();
            byte[] frame;
            lock (_lock)
            {
                _test?.Cancel();
                _test = cts;
                _testActive = true;
                frame = new byte[(_layout != null ? _layout.Count : TestLedCount) * 3];
                for (int o = 0; o < frame.Length; o += 3) { frame[o] = r; frame[o + 1] = g; frame[o + 2] = b; }
            }

            Task.Run(async () =>
            {
                double until = _clock() + TestHoldMs;
                try
                {
                    while (!cts.IsCancellationRequested && _clock() < until)
                    {
                        lock (_lock) { if (!cts.IsCancellationRequested) SendLocked(frame, frame.Length); }
                        await Task.Delay(TestIntervalMs, cts.Token);
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    lock (_lock) if (_test == cts) _testActive = false;
                }
            });
            return true;
        }

        // ---- status ----

        public void Describe(CaptureStats.Snapshot s)
        {
            lock (_lock)
            {
                s.ledCount = _layout != null ? _layout.Count : (int?)null;
                s.packetsPerFrame = _layout != null ? DdpSender.PacketsFor(_layout.Count * 3) : (int?)null;
                s.lastSendMs = _framesSent > 0 ? Math.Round(_lastSendMs, 2) : (double?)null;
                s.ddpFramesSent = _framesSent;
                s.ddpPacketsSent = _packetsSent;
                s.ddpSendErrors = _sendErrors;
                s.outputError = _lastError;
            }
        }
    }
}
