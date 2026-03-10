using Newtonsoft.Json;
using System.Threading.Tasks;
using Tizen.Applications;
using HyperTizen.WebSocket.DataTypes;
using System.Net.WebSockets;
using System.Text;
using System;
using System.Threading;

namespace HyperTizen.WebSocket
{
    internal class HyperionClient
    {
        WebSocketClient client;
        public HyperionClient()
        {
            if (!Preference.Contains("rpcServer")) return;
            client = new WebSocketClient(Preference.Get<string>("rpcServer"));
            Task.Run(() => client.ConnectAsync());
            Task.Run(() => Start());
        }

        public void UpdateURI(string uri)
        {
            if (client?.client != null && client.client.State == WebSocketState.Open)
            {
                client.client.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
            }

            client = new WebSocketClient(uri);
            Task.Run(() => client.ConnectAsync());
        }

        public async Task Start(bool shouldStart = false)
        {
            if (client != null && Capturer.GetCondition())
            {
                var cond = Capturer.LastCondition;
                Tizen.Log.Debug("HyperTizen", $"Condition: ScreenCapturePoints={cond.ScreenCapturePoints}, SleepMS={cond.SleepMS}, Width={cond.Width}, Height={cond.Height}");
                // Save condition to preferences so it can be queried via WebSocket ReadConfig
                Preference.Set("cond_scp", cond.ScreenCapturePoints.ToString());
                Preference.Set("cond_sleep", cond.SleepMS.ToString());
                Preference.Set("cond_w", cond.Width.ToString());
                Preference.Set("cond_h", cond.Height.ToString());

                while (App.Configuration.Enabled || shouldStart)
                {
                    Color[] colors = await Capturer.GetColors();
                    string image = Capturer.ToImage(colors);

                    if (client?.client?.State == WebSocketState.Open)
                    {
                        ImageCommand imgCmd = new ImageCommand(image);
                        string message = JsonConvert.SerializeObject(imgCmd);
                        var buffer = Encoding.UTF8.GetBytes(message);
                        await client.client.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
                    }

                    if (App.Configuration.Enabled && shouldStart) shouldStart = false;
                    else if (!App.Configuration.Enabled && shouldStart) App.Configuration.Enabled = true;
                }
            }
        }

        public async Task Stop()
        {
            if (App.Configuration.Enabled) App.Configuration.Enabled = false;
            Color[] colors = await Capturer.GetColors();
            string image = Capturer.ToImage(colors);

            if (client?.client?.State == WebSocketState.Open)
            {
                ImageCommand imgCmd = new ImageCommand(image);
                string message = JsonConvert.SerializeObject(imgCmd);
                var buffer = Encoding.UTF8.GetBytes(message);
                await client.client.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
    }
}