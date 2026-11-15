using System;
using Tizen.Applications;

namespace TizenGlow
{
    /// <summary>
    /// "TizenGlow On/Off": a second app in the package, for the home bar. The TV only gives remote buttons to the
    /// app on screen, so this is the remote's way in: opening it asks the service to flip the lights, then it
    /// closes at once. It has no window. Same TizenGlow.dll as the service; Main picks the class by app ID.
    /// </summary>
    public class OnOffApp : CoreUIApplication
    {
        public const string AppId = "io.gh.duckieonquacks.TizenGlow.onoff";

        /// <summary>The extra data key and value the service acts on (App.OnAppControlReceived).</summary>
        public const string CommandKey = "command", Toggle = "toggle";

        protected override void OnCreate()
        {
            base.OnCreate();
            try
            {
                var request = new AppControl { ApplicationId = Autostart.AppId };
                request.ExtraData.Add(CommandKey, Toggle);
                AppControl.SendLaunchRequest(request); // starts the service too when it isn't running
            }
            catch (Exception) { } // nothing to show it on; the lights not changing is the sign
            Exit();
        }
    }
}
