using TizenGlow.WebSocket.DataTypes;
using Newtonsoft.Json;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TizenGlow.WebSocket
{
    /// <summary>
    /// The original output: each frame as a small PNG in a HyperHDR / Hyperion JSON "image" command
    /// over the WebSocket saved as rpcServer. Behaviour is unchanged from when this lived in HyperionClient.
    /// </summary>
    internal sealed class HyperHdrOutput : IFrameOutput
    {
        internal static TimeSpan ReconnectInterval = TimeSpan.FromSeconds(5);

        private volatile WebSocketClient _client;
        private DateTime _lastReconnect = DateTime.MinValue;
        private string _image;

        public HyperHdrOutput(string uri)
        {
            if (uri == null) return;
            WebSocketClient client = new WebSocketClient(uri);
            _client = client;
            Task.Run(() => client.ConnectAsync());
        }

        public string Label { get { return "HyperHDR"; } }
        public bool Configured { get { return _client != null; } }
        public string Target { get { WebSocketClient c = _client; return c == null ? null : c.Uri; } }

        public string State
        {
            get
            {
                WebSocketClient c = _client;
                return c == null ? "not configured" : c.client.State == WebSocketState.Open ? "connected" : "disconnected";
            }
        }

        public void UpdateUri(string uri)
        {
            WebSocketClient old = _client;
            WebSocketClient fresh = new WebSocketClient(uri);

            // Publish the new client before tearing the old one down, so a running loop
            // picks up the replacement instead of sending on a socket being disposed.
            _client = fresh;
            Task.Run(async () =>
            {
                try { old?.client?.Abort(); } catch { }
                await fresh.ConnectAsync();
            });
        }

        public void Prepare(Color[] samples, PointSet points)
        {
            _image = Capturer.ToImage(samples, points);
        }

        /// <summary>True when HyperHDR got the frame.</summary>
        public async Task<bool> SendAsync()
        {
            WebSocketClient c = _client;
            if (c == null) return false;
            if (c.client.State == WebSocketState.Open)
            {
                try
                {
                    var buffer = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new ImageCommand(_image)));
                    await c.client.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
                    return true;
                }
                catch (Exception) { } // dropped mid-send: the socket is no longer Open, so the next frame redials
            }
            else if (DateTime.UtcNow - _lastReconnect >= ReconnectInterval)
            {
                // Nothing else redials a dropped socket, which left HyperHDR holding a frozen
                // frame. The frame loop doubles as the watchdog; dialing runs in the background
                // so a slow connect never stalls capture.
                _lastReconnect = DateTime.UtcNow;
                _ = c.ReconnectAsync();
            }
            return false;
        }

        public void Reset() { }
    }
}
