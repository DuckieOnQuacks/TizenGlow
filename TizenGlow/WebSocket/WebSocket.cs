using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TizenGlow.WebSocket.DataTypes;
using Tizen.Applications;

namespace TizenGlow.WebSocket
{
    public class WSServer
    {
        private HttpListener _httpListener;

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
                    _ = HandleWebSocketAsync(wsContext.WebSocket, httpContext.Response);
                }
                else
                {
                    httpContext.Response.StatusCode = 400;
                    httpContext.Response.Close();
                }
            }
        }

        private async Task HandleWebSocketAsync(System.Net.WebSockets.WebSocket webSocket, HttpListenerResponse response)
        {
            try
            {
                await WebSocketConnection.HandleAsync(webSocket, OnMessageAsync, response.Abort);
            }
            catch (Exception e)
            {
                // A failed client must not leave an unobserved task or stop other connections.
                Tizen.Log.Error("TGWS", "connection: " + e.GetType().Name);
            }
        }

        protected async Task OnMessageAsync(System.Net.WebSockets.WebSocket webSocket, string message)
        {
            BasicEvent data = JsonConvert.DeserializeObject<BasicEvent>(message);

            switch (data.Event)
            {
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

        private async Task<string> ReadConfigAsync(ReadConfigEvent readConfigEvent)
        {
            string result;
            if (readConfigEvent.key == "nativeVersion")
            {
                result = JsonConvert.SerializeObject(new ReadConfigResultEvent(false, readConfigEvent.key, CaptureLoop.ServiceVersion));
            }
            else if (readConfigEvent.key == "captureStats")
            {
                // Live values (an object, see CaptureStats.Snapshot), kept out of Preference so a
                // running loop doesn't write storage every frame.
                result = JsonConvert.SerializeObject(new ReadConfigResultEvent(false, readConfigEvent.key, App.client.ReadStats()));
            }
            else if (readConfigEvent.key == "ledPreview")
            {
                // The last strip sent to WLED, in canonical order (top-left clockwise), for the page's preview ring.
                object preview = App.client.Wled.Preview();
                result = JsonConvert.SerializeObject(preview != null
                    ? new ReadConfigResultEvent(false, readConfigEvent.key, preview)
                    : new ReadConfigResultEvent(true, readConfigEvent.key, "No LED frame yet."));
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

        // One reply at a time per connection: its messages are handled in order.
        private async Task SendAsync(System.Net.WebSockets.WebSocket webSocket, string message)
        {
            var buffer = Encoding.UTF8.GetBytes(message);
            await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        void SetConfiguration(SetConfigEvent setConfigEvent)
        {
            switch (setConfigEvent.key)
            {
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
                        if (!CaptureLoop.TrySetTargetFps(setConfigEvent.value)) return;
                        break;
                    }
                case "captureMode":
                    {
                        // fast / balanced / detailed, picked up by the running loop on its next frame. The
                        // mode also sets the target FPS; save that too so both survive a restart. Saved
                        // under the mode's own name ("smooth" is still accepted for Detailed).
                        if (!CaptureLoop.TrySetMode(setConfigEvent.value)) return;
                        Preference.Set("targetFps", CaptureLoop.TargetFps.ToString());
                        Preference.Set("captureMode", CaptureLoop.Mode.Name);
                        return;
                    }
                // WLED. Every value is validated by the output; anything invalid is ignored and not saved.
                case "wledTarget":
                    {
                        if (!App.client.Wled.TrySetTarget(setConfigEvent.value)) return; // "host:port"
                        // A loop that exited with "no-server" (no target saved yet) needs starting now.
                        if (App.Configuration.Enabled) Task.Run(() => App.client.Start());
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
                case "wledTvOff":
                    {
                        if (!App.client.Wled.TrySetTvOff(setConfigEvent.value)) return; // off / leave
                        break;
                    }
                case "wledGamma":
                    {
                        if (!App.client.Wled.TrySetGamma(setConfigEvent.value)) return; // 0.5-3.0
                        break;
                    }
                case "wledSaturation":
                    {
                        if (!App.client.Wled.TrySetSaturation(setConfigEvent.value)) return; // 0-200 (%)
                        break;
                    }
                case "wledTest":
                    {
                        App.client.Wled.TryTest(setConfigEvent.value); // red/green/blue/white/off: a command, never saved
                        return;
                    }
                case "autostart":
                    {
                        if (!Autostart.TrySet(setConfigEvent.value)) return; // on / off
                        break;
                    }
                case "letterbox":
                    {
                        if (!Letterbox.TrySetMode(setConfigEvent.value)) return; // off / auto / on
                        break;
                    }
                case "levels":
                    {
                        if (!Levels.TrySetEnabled(setConfigEvent.value)) return; // on / off
                        break;
                    }
                case "levelsWhite":
                    {
                        if (!Levels.TrySetWhite(setConfigEvent.value)) return; // "r,g,b" raw, normally measured
                        break;
                    }
                case "levelsBlack":
                    {
                        if (!Levels.TrySetBlack(setConfigEvent.value)) return;
                        break;
                    }
                case "measureLevels":
                    {
                        Levels.BeginMeasure(setConfigEvent.value); // white / black: a command, the result is saved by the service
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
}
