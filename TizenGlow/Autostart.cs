using System;
using Tizen.Applications;

namespace TizenGlow
{
    /// <summary>
    /// Starts the service again after the TV restarts. Tizen ignores the manifest's on-boot flag since 2.4,
    /// so the service keeps a repeating system alarm pointed at itself: while it runs, the alarm does
    /// nothing; after a restart or power cut, the next alarm (at most PeriodSec later) launches it.
    /// CreateAlarm(delay, period, ...) is the alarm call that doesn't turn the screen on. Tizen drops an
    /// app's alarms when the app is reinstalled, so every service start registers it again.
    /// </summary>
    public static class Autostart
    {
        /// <summary>The service's own app ID (tizen-manifest.xml).</summary>
        public const string AppId = "io.gh.duckieonquacks.TizenGlow";

        /// <summary>The shortest repeat Tizen allows for this kind of alarm.</summary>
        public const int PeriodSec = 600;

        private static volatile string _state = "not set";

        /// <summary>"on", "off", or "not available: ..." when the TV refuses the alarm.</summary>
        public static string State { get { return _state; } }

        /// <summary>"on" / "off" (the "autostart" preference). Junk is rejected; a TV that refuses the alarm still saves the choice.</summary>
        public static bool TrySet(string value)
        {
            if (value != "on" && value != "off") return false;
            bool on = value == "on";
            try
            {
                AlarmManager.CancelAll(); // only this app's alarms
                if (on) AlarmManager.CreateAlarm(PeriodSec, PeriodSec, new AppControl { ApplicationId = AppId });
                _state = value;
            }
            catch (Exception e)
            {
                _state = "not available: " + e.GetType().Name + ": " + e.Message;
            }
            return true;
        }
    }
}
