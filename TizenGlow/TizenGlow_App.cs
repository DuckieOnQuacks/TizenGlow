using Tizen.Applications;
using Tizen.Applications.Notifications;
using Tizen.System;
using TizenGlow.WebSocket;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace TizenGlow
{
    class App : ServiceApplication
    {
        public static CaptureLoop client;
        protected override void OnCreate()
        {
            base.OnCreate();
            if (!Preference.Contains("enabled")) Preference.Set("enabled", "false");
            Task.Run(() => WebSocketServer.StartServerAsync());
            Display.StateChanged += Display_StateChanged;
            Capturer.Probe(); // publish detectedApi / captureState even while capture is disabled
            // Started by the autostart alarm while the TV is in standby: wait for the screen to come on
            // (Display_StateChanged turns capture on then) instead of capturing a dark panel.
            try { if (Display.State == DisplayState.Off) Configuration.Enabled = false; } catch { }
            client = new CaptureLoop();
        }

        private void Display_StateChanged(object sender, DisplayStateChangedEventArgs e)
        {
            if (e.State == DisplayState.Off)
            {
                Task.Run(() => client.Stop());
            } else if (e.State == DisplayState.Normal)
            {
                Configuration.Enabled = bool.Parse(Preference.Get<string>("enabled"));
                Task.Run(() => client.Start());
            }
        }

        protected override void OnAppControlReceived(AppControlReceivedEventArgs e)
        {
            base.OnAppControlReceived(e);
            bool launch = _launching;
            _launching = false;
            string command;
            if (e.ReceivedAppControl.ExtraData.TryGet(OnOffApp.CommandKey, out command) && command == OnOffApp.Toggle)
            {
                // The On/Off app started the service: the strip wasn't TizenGlow's, so the press means on.
                SetLights(launch || Preference.Get<string>("enabled") != "true");
            }
        }

        // True until the request that started this process has been handled.
        private static bool _launching = true;

        /// <summary>The On/Off app: capture and strip on, or both off. Saved like the page's "Capture enabled".</summary>
        private static void SetLights(bool on)
        {
            Preference.Set("enabled", on ? "true" : "false");
            if (on)
            {
                Configuration.Enabled = true;
                Task.Run(() => client.Start()); // the first frame switches the strip back on
            }
            else Task.Run(() => client.SwitchOffAsync());
        }

        protected override void OnDeviceOrientationChanged(DeviceOrientationEventArgs e)
        {
            base.OnDeviceOrientationChanged(e);
        }

        protected override void OnLocaleChanged(LocaleChangedEventArgs e)
        {
            base.OnLocaleChanged(e);
        }

        protected override void OnLowBattery(LowBatteryEventArgs e)
        {
            base.OnLowBattery(e);
        }

        protected override void OnLowMemory(LowMemoryEventArgs e)
        {
            base.OnLowMemory(e);
        }

        protected override void OnRegionFormatChanged(RegionFormatChangedEventArgs e)
        {
            base.OnRegionFormatChanged(e);
        }

        protected override void OnTerminate()
        {
            base.OnTerminate();
        }

        static void Main(string[] args)
        {
            // Both apps in the package run this DLL.
            if (LaunchedAppId() == OnOffApp.AppId)
            {
                new OnOffApp().Run(args);
                return;
            }
            App app = new App();
            app.Run(args);
        }

        [DllImport("libcapi-appfw-app-common.so.0", EntryPoint = "app_get_id")]
        private static extern int AppGetId(out string appId);

        /// <summary>The app ID this process was launched as, or null when it can't be read (then it runs as the service).</summary>
        private static string LaunchedAppId()
        {
            try
            {
                string id;
                return AppGetId(out id) == 0 ? id : null;
            }
            catch (Exception) { return null; }
        }

        public static class Configuration
        {
            public static bool Enabled = bool.Parse(Preference.Get<string>("enabled"));
        }
    }
}
