using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TizenGlow.Wled
{
    /// <summary>
    /// WLED Direct: maps the capture samples onto the physical strip (LedLayout), applies optional
    /// smoothing, black threshold, saturation, gamma and brightness, and sends the strip to WLED as DDP
    /// (DdpSender). While smoothing is on, a glide task keeps re-sending at GlideFps between captured
    /// frames, so colours slide towards each new frame instead of stepping once per capture (a frame
    /// every 60-140 ms on an S90D). Buffers, the layout mapping and the socket are reused; only config
    /// changes rebuild them. One lock covers the capture loop, the glide task, the test pattern task and
    /// config changes from the UI.
    /// </summary>
    internal sealed class WledOutput : IFrameOutput
    {
        public const int TestLedCount = 2048;   // test frames before a layout is saved; WLED ignores pixels past its strip
        internal static int TestHoldMs = 10000; // test colours are resent until then: WLED drops realtime data after 2.5 s (default)
        public const int TestIntervalMs = 100;
        public const int ErrorBackoffMs = 1000; // after a failed send, skip sends this long instead of retrying every frame
        internal static int InfoPort = 80;      // WLED's web server, for the one-off LED count check
        internal static int InfoRetryMs = 30000;
        internal static int GlideFps = 30;      // sends between captured frames while smoothing is on
        internal static int GlideIdleMs = 3000; // the glide task ends this long after the last captured frame
        public const double RateWindowMs = 2000;

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
        private PointSet _points = CaptureMode.Balanced.Points;
        private int _shift;
        private int _brightness = 100;
        private int _blackThreshold;
        private bool _smoothing = true;
        private int _saturation = 100;
        private bool _offWithTv = true;   // "wledTvOff": switch the strip off when the TV turns off
        private bool _switchedOff;        // did so; the next frame sent switches it back on
        private double _gamma = 1.0;
        private byte[] _gammaTable; // null while gamma is 1.0

        private float[] _samples = new float[0];  // the latest captured frame (the target)
        private float[] _smoothed = new float[0]; // where the strip is now, moving towards _samples
        private bool _hasSmoothed, _hasTarget, _settled = true, _glideRunning;
        private bool _dirty = true; // a setting changed: render the next frame even if the picture didn't move
        private double _lastRenderMs, _targetAt;
        private float[] _leds = new float[0];
        private byte[] _rgb = new byte[0];
        private int _frameLength;
        private readonly Queue<double> _sendTimes = new Queue<double>();

        private long _framesSent, _packetsSent, _sendErrors;
        private double _lastSendMs;
        private double _lastSuccessAt = double.NegativeInfinity, _lastErrorAt = double.NegativeInfinity, _backoffUntil;
        private string _lastError;
        private bool _lastSendFailed;
        private CancellationTokenSource _test;
        private bool _testActive;
        private int? _wledLedCount;
        private string _wledInfo;
        private int _infoGeneration;

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
                    if (_switchedOff) return "switched off with the TV";
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
            int generation;
            lock (_lock)
            {
                _host = host;
                _port = port;
                _endpoint = null;
                _backoffUntil = 0;
                _lastError = null;
                _wledLedCount = null;
                _wledInfo = "asking WLED for its LED count...";
                generation = ++_infoGeneration;
            }
            QueryInfo(host, generation);
            return true;
        }

        // The target can be set by anything that reaches port 8086, so the info request is fenced in:
        // local-network addresses only, one fixed path, a capped reply, and only sanitised fields echoed.
        private const int MaxInfoBytes = 64 * 1024;      // WLED's /json/info is a few KB
        internal static bool AllowLoopbackInfo = false;  // tests only: their fake WLED runs on this PC

        /// <summary>
        /// Reads WLED's own LED count (GET /json/info, read-only; frames never use HTTP) so the UI can warn
        /// when the layout total doesn't match it. Retries every InfoRetryMs while WLED can't be reached,
        /// and stops as soon as the target changes.
        /// </summary>
        private void QueryInfo(string host, int generation)
        {
            Task.Run(async () =>
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3), MaxResponseContentBufferSize = MaxInfoBytes })
                {
                    while (true)
                    {
                        lock (_lock) if (generation != _infoGeneration) return; // a newer target took over
                        string info;
                        int? count = null;
                        bool retry = true;
                        try
                        {
                            IPAddress ip = await ResolveLocalAsync(host);
                            if (ip == null)
                            {
                                info = "not asking " + host + " for its LED count: not a local network address";
                                retry = false;
                            }
                            else
                            {
                                // Connect to the address just checked, so DNS can't switch it before the request.
                                var request = new HttpRequestMessage(HttpMethod.Get, "http://" + ip + ":" + InfoPort + "/json/info");
                                request.Headers.Host = host + (InfoPort == 80 ? "" : ":" + InfoPort);
                                using (HttpResponseMessage response = await http.SendAsync(request))
                                {
                                    response.EnsureSuccessStatusCode();
                                    JObject json = JObject.Parse(await response.Content.ReadAsStringAsync());
                                    count = (int?)json["leds"]?["count"];
                                    info = count == null ? "WLED answered without an LED count"
                                        : "WLED " + Clean(json["ver"]) + (json["name"] != null ? " \"" + Clean(json["name"]) + "\"" : "");
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            string message = e.Message.Length > 120 ? e.Message.Substring(0, 120) + "..." : e.Message;
                            info = "couldn't read WLED's LED count (" + e.GetType().Name + ": " + message + "); retrying every " + InfoRetryMs / 1000 + " s";
                        }
                        lock (_lock)
                        {
                            if (generation != _infoGeneration) return; // target changed meanwhile
                            _wledInfo = info;
                            _wledLedCount = count;
                        }
                        if (count != null || !retry) return;
                        await Task.Delay(InfoRetryMs);
                    }
                }
            });
        }

        /// <summary>Private (RFC 1918) and link-local IPv4 only: where a WLED can be, never the TV itself or the internet.</summary>
        internal static bool IsLocalNetwork(IPAddress ip)
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (AllowLoopbackInfo && b[0] == 127);
        }

        /// <summary>A WLED-reported string reduced to plain characters and at most 40 of them.</summary>
        private static string Clean(JToken token)
        {
            string s = token == null ? "" : token.ToString();
            var sb = new System.Text.StringBuilder();
            foreach (char c in s)
            {
                if (sb.Length == 40) break;
                if (char.IsLetterOrDigit(c) || " ._-()".IndexOf(c) >= 0) sb.Append(c);
            }
            return sb.ToString();
        }

        public bool TrySetLayout(string value)
        {
            lock (_lock)
            {
                LedLayout layout;
                if (!LedLayout.TryParse(value, _points, out layout)) return false;
                _layout = _shift == 0 ? layout : layout.WithShift(_shift);
                _dirty = true;
            }
            return true;
        }

        /// <summary>Gamma 0.5-3.0 applied to each LED (2.2 deepens darks and saturates; 1.0 is as captured).</summary>
        public bool TrySetGamma(string value)
        {
            double gamma;
            if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out gamma) || gamma < 0.5 || gamma > 3.0) return false;
            byte[] table = null;
            if (gamma != 1.0)
            {
                table = new byte[256];
                for (int i = 0; i < 256; i++) table[i] = ToByte((float)(255 * Math.Pow(i / 255.0, gamma)));
            }
            lock (_lock) { _gamma = gamma; _gammaTable = table; _dirty = true; }
            return true;
        }

        /// <summary>"off": switch the strip off when the TV turns off (default); "leave": leave it to WLED's own effect.</summary>
        public bool TrySetTvOff(string value)
        {
            if (value != "off" && value != "leave") return false;
            lock (_lock) _offWithTv = value == "off";
            return true;
        }

        /// <summary>
        /// The TV turned off while TizenGlow drove the strip: switch it off with WLED's JSON API, ending realtime
        /// at once ("live": false), instead of letting WLED fall back to its own effect after its 2.5 s timeout.
        /// WLED still shows realtime data while off, so frames light it again when the TV comes back; the first
        /// frame sent then also switches it back on, so it behaves as before once capture stops.
        /// </summary>
        public async Task TurnOffWithTvAsync()
        {
            string host;
            lock (_lock)
            {
                if (!_offWithTv || _host == null) return;
                host = _host;
            }
            string error = await PostStateAsync(host, "{\"on\":false,\"live\":false}");
            lock (_lock)
            {
                if (error == null) _switchedOff = true;
                else Fail(_clock(), "couldn't switch the strip off with the TV: " + error);
            }
        }

        /// <summary>POST /json/state to WLED; null when it worked, otherwise why not. Same fences as the LED count check.</summary>
        private async Task<string> PostStateAsync(string host, string json)
        {
            try
            {
                IPAddress ip = await ResolveLocalAsync(host);
                if (ip == null) return host + " is not a local network address";
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2), MaxResponseContentBufferSize = MaxInfoBytes })
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, "http://" + ip + ":" + InfoPort + "/json/state")
                    {
                        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
                    };
                    request.Headers.Host = host + (InfoPort == 80 ? "" : ":" + InfoPort);
                    using (HttpResponseMessage response = await http.SendAsync(request)) response.EnsureSuccessStatusCode();
                }
                return null;
            }
            catch (Exception e)
            {
                return e.GetType().Name + ": " + (e.Message.Length > 120 ? e.Message.Substring(0, 120) + "..." : e.Message);
            }
        }

        /// <summary>The host's IPv4 address when it is on the local network (connect to it, not the name, so DNS can't switch it), else null.</summary>
        private static async Task<IPAddress> ResolveLocalAsync(string host)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip))
            {
                ip = null;
                foreach (IPAddress a in await Dns.GetHostAddressesAsync(host))
                    if (a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; }
            }
            return ip != null && IsLocalNetwork(ip) ? ip : null;
        }

        /// <summary>Saturation 0-200 %: 100 as captured, 0 greyscale, 200 doubled.</summary>
        public bool TrySetSaturation(string value) { return TrySetPercent(value, 200, ref _saturation); }

        /// <summary>LED shift, e.g. "3" or "-2": LEDs to move the picture clockwise round the strip. Kept across layout changes.</summary>
        public bool TrySetShift(string value)
        {
            int shift;
            if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out shift)
                || shift < -LedLayout.MaxShift || shift > LedLayout.MaxShift) return false;
            lock (_lock)
            {
                _shift = shift;
                if (_layout != null) _layout = _layout.WithShift(shift);
                _dirty = true;
            }
            return true;
        }

        public bool TrySetBrightness(string value) { return TrySetPercent(value, 100, ref _brightness); }
        public bool TrySetBlackThreshold(string value) { return TrySetPercent(value, 10, ref _blackThreshold); }

        public bool TrySetSmoothing(string value)
        {
            if (value != "on" && value != "off") return false;
            lock (_lock) { _smoothing = value == "on"; _hasSmoothed = false; _dirty = true; }
            return true;
        }

        private bool TrySetPercent(string value, int max, ref int field)
        {
            int percent;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out percent) || percent > max) return false;
            lock (_lock) { field = percent; _dirty = true; }
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
                _sendTimes.Clear();
            }
        }

        // ---- frames ----

        public void Prepare(Color[] samples, PointSet points)
        {
            lock (_lock)
            {
                if (points != _points || _samples.Length != points.Count * 3)
                {
                    // The capture mode or the black-bar rows changed: the samples mean something else now.
                    _points = points;
                    if (_layout != null) _layout = _layout.WithPoints(points);
                    _samples = new float[points.Count * 3];
                    _smoothed = new float[points.Count * 3];
                    _hasSmoothed = false;
                }
                _frameLength = 0;
                if (_layout == null) return;

                for (int i = 0, o = 0; i < points.Count; i++, o += 3)
                {
                    // Same 8-bit clamp ToImage applies for HyperHDR.
                    _samples[o] = Math.Min(255, Math.Max(0, samples[i].R));
                    _samples[o + 1] = Math.Min(255, Math.Max(0, samples[i].G));
                    _samples[o + 2] = Math.Min(255, Math.Max(0, samples[i].B));
                }
                _hasTarget = true;
                _targetAt = _clock();
                RenderLocked(_targetAt);
                if (_smoothing && !_glideRunning)
                {
                    _glideRunning = true;
                    Task.Run(GlideAsync);
                }
            }
        }

        /// <summary>
        /// Moves the strip towards the latest frame and builds the DDP bytes. Time-based exponential
        /// smoothing on the samples (mapping is linear, so this equals smoothing every LED, at a fraction
        /// of the work); alpha comes from the real time since the last render, so the response time stays
        /// the same whatever the frame or glide rate. Returns true when something changed.
        /// </summary>
        private bool RenderLocked(double now)
        {
            LedLayout layout = _layout;
            double dt = now - _lastRenderMs;
            int tau = _smoothing ? _smoothingMs() : 0;
            float alpha = !_hasSmoothed || tau <= 0 || dt >= 1000 ? 1f : (float)(1 - Math.Exp(-dt / tau));
            bool moved = false;
            float remaining = 0;
            for (int j = 0; j < _smoothed.Length; j++)
            {
                float d = _samples[j] - _smoothed[j];
                if (alpha * d != 0) moved = true;
                _smoothed[j] += alpha * d;
                remaining = Math.Max(remaining, Math.Abs(_samples[j] - _smoothed[j]));
            }
            _hasSmoothed = true;
            _lastRenderMs = now;
            _settled = remaining < 0.5f;
            if (!moved && !_dirty && _frameLength != 0) return false;
            _dirty = false;

            int count = layout.Count;
            if (_leds.Length != count * 3)
            {
                _leds = new float[count * 3];
                _rgb = new byte[count * 3];
            }
            layout.Map(_smoothed, _leds);

            // Black threshold first (judged on the picture, not the processed output), then saturation,
            // gamma and brightness.
            float threshold = _blackThreshold * 255f / 100f;
            float sat = _saturation / 100f;
            float gain = _brightness / 100f;
            byte[] gamma = _gammaTable;
            for (int o = 0; o < count * 3; o += 3)
            {
                float r = _leds[o], g = _leds[o + 1], b = _leds[o + 2];
                if (r < threshold && g < threshold && b < threshold) r = g = b = 0;
                else if (sat != 1f)
                {
                    float l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    r = l + (r - l) * sat;
                    g = l + (g - l) * sat;
                    b = l + (b - l) * sat;
                }
                byte rb = ToByte(r), gb = ToByte(g), bb = ToByte(b);
                if (gamma != null) { rb = gamma[rb]; gb = gamma[gb]; bb = gamma[bb]; }
                _rgb[o] = gain == 1f ? rb : ToByte(rb * gain);
                _rgb[o + 1] = gain == 1f ? gb : ToByte(gb * gain);
                _rgb[o + 2] = gain == 1f ? bb : ToByte(bb * gain);
            }
            _frameLength = count * 3;
            return true;
        }

        /// <summary>Between captured frames: keeps sending while the strip is still moving towards the latest frame.</summary>
        private async Task GlideAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(Math.Max(1, 1000 / GlideFps));
                    lock (_lock)
                    {
                        double now = _clock();
                        if (!_hasTarget || !_smoothing || now - _targetAt > GlideIdleMs) { _glideRunning = false; return; }
                        if (_testActive || _layout == null || _settled) continue;
                        if (RenderLocked(now)) SendLocked(_rgb, _frameLength);
                    }
                }
            }
            catch (Exception) { lock (_lock) _glideRunning = false; }
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

        /// <summary>The last strip sent, in canonical order (top-left clockwise) with the side counts, or null before the first frame.</summary>
        public object Preview()
        {
            lock (_lock)
            {
                LedLayout layout = _layout;
                if (layout == null || _frameLength == 0) return null;
                int count = layout.Count;
                var canonical = new byte[count * 3];
                for (int p = 0; p < count; p++)
                {
                    int slot = layout.Slot(p) * 3;
                    canonical[slot] = _rgb[p * 3];
                    canonical[slot + 1] = _rgb[p * 3 + 1];
                    canonical[slot + 2] = _rgb[p * 3 + 2];
                }
                return new { top = layout.Top, right = layout.Right, bottom = layout.Bottom, left = layout.Left, rgb = Convert.ToBase64String(canonical) };
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
                _sendTimes.Enqueue(end);
                while (_sendTimes.Count > 0 && _sendTimes.Peek() <= end - RateWindowMs) _sendTimes.Dequeue();
                if (_switchedOff)
                {
                    // Back from TV off: this frame already holds the strip (realtime), so switching WLED on
                    // now shows no flash of its own effect, and it returns to that effect when capture stops.
                    _switchedOff = false;
                    string host = _host;
                    Task.Run(async () =>
                    {
                        string error = await PostStateAsync(host, "{\"on\":true}");
                        if (error != null) lock (_lock) Fail(_clock(), "couldn't switch the strip back on: " + error);
                    });
                }
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
                s.wledLedCount = _wledLedCount;
                s.wledInfo = _wledInfo;

                // DDP frames per second over the last 2 s, glide sends included; the loop's own count only sees captured frames.
                double now = _clock();
                while (_sendTimes.Count > 0 && _sendTimes.Peek() <= now - RateWindowMs) _sendTimes.Dequeue();
                if (s.sendFps != null) s.sendFps = Math.Round(_sendTimes.Count * 1000 / RateWindowMs, 1);
            }
        }
    }
}
