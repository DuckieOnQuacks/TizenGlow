using System;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using HyperTizen.WebSocket.DataTypes;
using Rssdp;
using Tizen.Applications;
using static HyperTizen.WebSocket.DataTypes.SSDPScanResultEvent;

namespace HyperTizen.WebSocket
{
    public class WSServer
    {
        private HttpListener _httpListener;
        private List<string> usnList = new List<string>()
        {
            "urn:hyperion-project.org:device:basic:1",
            "urn:hyperhdr.eu:device:basic:1"
        };

        public WSServer(string uriPrefix)
        {
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add(uriPrefix);
        }

        public async Task StartAsync()
        {
            _httpListener.Start();
            while (true)
            {
                var httpContext = await _httpListener.GetContextAsync();
                if (httpContext.Request.IsWebSocketRequest)
                {
                    var wsContext = await httpContext.AcceptWebSocketAsync(null);
                    _ = HandleWebSocketAsync(wsContext.WebSocket);
                }
                else
                {
                    httpContext.Response.StatusCode = 400;
                    httpContext.Response.Close();
                }
            }
        }

        private async Task HandleWebSocketAsync(System.Net.WebSockets.WebSocket webSocket)
        {
            var buffer = new byte[1024 * 4];
            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

            while (result.MessageType != WebSocketMessageType.Close)
            {
                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                await OnMessageAsync(webSocket, message);
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            }

            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
        }

        protected async Task OnMessageAsync(System.Net.WebSockets.WebSocket webSocket, string message)
        {
            BasicEvent data = JsonConvert.DeserializeObject<BasicEvent>(message);

            switch (data.Event)
            {
                case Event.ScanSSDP:
                    {
                        // A search waits seconds for replies. Run it aside so status reads and
                        // Enabled / target FPS changes on this connection aren't queued behind it.
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var devices = await ScanSSDPAsync();
                                string resultEvent = JsonConvert.SerializeObject(new SSDPScanResultEvent(devices));
                                await SendAsync(webSocket, resultEvent);
                            }
                            catch (Exception) { } // UI went away mid-scan
                        });
                        break;
                    }

                case Event.ReadConfig:
                    {
                        ReadConfigEvent readConfigEvent = JsonConvert.DeserializeObject<ReadConfigEvent>(message);
                        string result = await ReadConfigAsync(readConfigEvent);
                        await SendAsync(webSocket, result);
                        break;
                    }

                case Event.SetConfig:
                    {
                        SetConfigEvent setConfigEvent = JsonConvert.DeserializeObject<SetConfigEvent>(message);
                        SetConfiguration(setConfigEvent);
                        break;
                    }
            }
        }

        private async Task<List<SSDPDevice>> ScanSSDPAsync()
        {
            var devices = new List<SSDPDevice>();
            using (var deviceLocator = new SsdpDeviceLocator())
            {
                var foundDevices = await deviceLocator.SearchAsync();
                foreach (var foundDevice in foundDevices)
                {
                    if (!usnList.Contains(foundDevice.NotificationType)) continue;

                    var fullDevice = await foundDevice.GetDeviceInfo();
                    Uri descLocation = foundDevice.DescriptionLocation;
                    devices.Add(new SSDPDevice(fullDevice.FriendlyName, descLocation.OriginalString.Replace(descLocation.PathAndQuery, "")));
                }
            }
            return devices;
        }

        private async Task<string> ReadConfigAsync(ReadConfigEvent readConfigEvent)
        {
            string result;
            if (readConfigEvent.key == "nativeVersion")
            {
                result = JsonConvert.SerializeObject(new ReadConfigResultEvent(false, readConfigEvent.key, HyperionClient.ServiceVersion));
            }
            else if (readConfigEvent.key == "captureStats")
            {
                // Live values (an object, see CaptureStats.Snapshot), kept out of Preference so a
                // running loop doesn't write storage every frame.
                result = JsonConvert.SerializeObject(new ReadConfigResultEvent(false, readConfigEvent.key, App.client.ReadStats()));
            }
            else if (!Preference.Contains(readConfigEvent.key))
            {
                result = JsonConvert.SerializeObject(new ReadConfigResultEvent(true, readConfigEvent.key, "Key doesn't exist."));
            }
            else
            {
                string value = Preference.Get<string>(readConfigEvent.key);
                result = JsonConvert.SerializeObject(new ReadConfigResultEvent(false, readConfigEvent.key, value));
            }
            return result;
        }

        // A WebSocket allows one send at a time; SSDP results can now race ReadConfig replies.
        // ponytail: one lock for all UI connections, there is normally just one
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        private async Task SendAsync(System.Net.WebSockets.WebSocket webSocket, string message)
        {
            var buffer = Encoding.UTF8.GetBytes(message);
            await _sendLock.WaitAsync();
            try { await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None); }
            finally { _sendLock.Release(); }
        }

        void SetConfiguration(SetConfigEvent setConfigEvent)
        {
            switch (setConfigEvent.key)
            {
                case "rpcServer":
                    {
                        App.Configuration.RPCServer = setConfigEvent.value;
                        App.client.UpdateURI(setConfigEvent.value);
                        break;
                    }
                case "enabled":
                    {
                        bool value = bool.Parse(setConfigEvent.value);
                        App.Configuration.Enabled = value;

                        // Start whenever capture should be on. Start() ignores the call if a loop is
                        // already alive; the old false->true edge check left a loop that had exited
                        // for any other reason impossible to restart from the UI.
                        if (value) Task.Run(() => App.client.Start());
                        break;
                    }
                case "targetFps":
                    {
                        // Picked up by the running loop on its next frame. Anything but a preset is
                        // ignored and not saved.
                        if (!HyperionClient.TrySetTargetFps(setConfigEvent.value)) return;
                        break;
                    }
                case "captureMode":
                    {
                        // fast / balanced / smooth, picked up by the running loop on its next frame. The
                        // mode also sets the target FPS; save that too so both survive a restart.
                        if (!HyperionClient.TrySetMode(setConfigEvent.value)) return;
                        Preference.Set("targetFps", HyperionClient.TargetFps.ToString());
                        break;
                    }
                // WLED Direct. Every value is validated by the output; anything invalid is ignored and not saved.
                case "outputMode":
                    {
                        if (!App.client.TrySetOutput(setConfigEvent.value)) return; // "hyperhdr" or "wled"
                        break;
                    }
                case "wledTarget":
                    {
                        if (!App.client.Wled.TrySetTarget(setConfigEvent.value)) return; // "host:port"
                        break;
                    }
                case "ledLayout":
                    {
                        if (!App.client.Wled.TrySetLayout(setConfigEvent.value)) return; // see LedLayout.Serialize
                        break;
                    }
                case "ledShift":
                    {
                        if (!App.client.Wled.TrySetShift(setConfigEvent.value)) return; // LEDs clockwise, negative = counter-clockwise
                        break;
                    }
                case "wledBrightness":
                    {
                        if (!App.client.Wled.TrySetBrightness(setConfigEvent.value)) return; // 0-100 (%)
                        break;
                    }
                case "wledBlackThreshold":
                    {
                        if (!App.client.Wled.TrySetBlackThreshold(setConfigEvent.value)) return; // 0-10 (%)
                        break;
                    }
                case "wledSmoothing":
                    {
                        if (!App.client.Wled.TrySetSmoothing(setConfigEvent.value)) return; // "on" / "off"
                        break;
                    }
                case "wledTest":
                    {
                        App.client.Wled.TryTest(setConfigEvent.value); // red/green/blue/white/off: a command, never saved
                        return;
                    }
            }

            Preference.Set(setConfigEvent.key, setConfigEvent.value);
        }
    }

    public static class WebSocketServer
    {
        public static async Task StartServerAsync()
        {
            var wsServer = new WSServer("http://+:8086/");
            await wsServer.StartAsync();
        }
    }

    public class WebSocketClient
    {
        private string uri;
        public ClientWebSocket client;
        private byte errorTimes = 0;

        public string Uri { get { return uri; } }

        public WebSocketClient(string uri)
        {
            this.uri = uri;
            client = new ClientWebSocket();
        }

        private int _connecting;

        public async Task ConnectAsync()
        {
            // Callers fire this with Task.Run and never observe it, so a failed dial (HyperHDR
            // down, network not up yet) used to vanish and leave the socket Closed for good.
            // The capture loop sees the non-Open state and calls ReconnectAsync.
            try
            {
                await client.ConnectAsync(new Uri(uri), CancellationToken.None);
                _ = ReceiveMessagesAsync();
            }
            catch (Exception) { }
        }

        /// <summary>A ClientWebSocket cannot be reused once closed, so reconnecting builds a new one.</summary>
        public async Task ReconnectAsync()
        {
            if (Interlocked.Exchange(ref _connecting, 1) == 1) return;
            try
            {
                try { client?.Abort(); client?.Dispose(); } catch { }
                client = new ClientWebSocket();
                await ConnectAsync();
            }
            finally { Interlocked.Exchange(ref _connecting, 0); }
        }

        private async Task ReceiveMessagesAsync()
        {
            var buffer = new byte[1024 * 4];
            try
            {
                while (client.State == WebSocketState.Open)
                {
                    var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                    }
                    else
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        OnMessage(message);
                    }
                }
            }
            catch (Exception) { } // peer went away mid-receive; the capture loop's watchdog redials
        }

        private void OnMessage(string message)
        {

        }
    }
}