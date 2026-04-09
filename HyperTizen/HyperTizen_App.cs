using System;
using System.Threading.Tasks;
using Tizen.Applications;
using Tizen.Applications.Notifications;
using Tizen.System;
using HyperTizen.WebSocket;

namespace HyperTizen
{
    class App : ServiceApplication
    {
        public static HyperionClient client;
        protected override void OnCreate()
        {
            base.OnCreate();
            if (!Preference.Contains("enabled")) Preference.Set("enabled", "false");
            Task.Run(() => WebSocketServer.StartServerAsync());
            Display.StateChanged += Display_StateChanged;
            client = new HyperionClient();
        }

        private void Display_StateChanged(object sender, DisplayStateChangedEventArgs e)
        {
            // Dim is a transient state on the way to/from Off — ignore it so
            // we don't spuriously stop the capture mid-transition.
            if (e.State == DisplayState.Dim) return;

            // Fire-and-forget with exception containment. Start/Stop are
            // serialised internally via HyperionClient's lifecycle lock, so
            // a rapid off→on sequence is safe.
            if (e.State == DisplayState.Off)
            {
                _ = Task.Run(async () =>
                {
                    try { await client.Stop(); }
                    catch (Exception ex) { Tizen.Log.Debug("HyperTizen", "Display Off → Stop failed: " + ex.Message); }
                });
            }
            else if (e.State == DisplayState.Normal)
            {
                // Refresh the user-facing enabled flag from preference in case
                // it was toggled while the service wasn't watching.
                if (Preference.Contains("enabled"))
                    Configuration.Enabled = bool.Parse(Preference.Get<string>("enabled"));

                if (!Configuration.Enabled) return;

                _ = Task.Run(async () =>
                {
                    try { await client.Start(); }
                    catch (Exception ex) { Tizen.Log.Debug("HyperTizen", "Display Normal → Start failed: " + ex.Message); }
                });
            }
        }

        protected override void OnAppControlReceived(AppControlReceivedEventArgs e)
        {
            base.OnAppControlReceived(e);
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
            App app = new App();
            app.Run(args);
        }

        public static class Configuration
        {
            public static string RPCServer = Preference.Contains("rpcServer") ? Preference.Get<string>("rpcServer") : null;
            public static bool Enabled = bool.Parse(Preference.Get<string>("enabled"));
        }
    }
}
