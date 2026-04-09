using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HyperTizen
{
    public static class SsdpDiscovery
    {
        private const string SearchTarget = "urn:hyperhdr.eu:device:basic:1";
        private static readonly TimeSpan DiscoveryBudget = TimeSpan.FromSeconds(5);

        // Returns (ip, fbsPort) of a discovered HyperHDR instance.
        // ip is set whenever a HyperHDR SSDP response is found.
        // fbsPort is set only if HYPERHDR-FBS-PORT header is present (may be 0 otherwise).
        public static (string ip, int port) GetHyperIpAndPort()
        {
            string ssdpRequest =
                "M-SEARCH * HTTP/1.1\r\n" +
                "HOST: 239.255.255.250:1900\r\n" +
                "MAN: \"ssdp:discover\"\r\n" +
                "MX: 2\r\n" +
                "ST: " + SearchTarget + "\r\n\r\n";

            try
            {
                using (var udp = new UdpClient())
                {
                    var multicast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                    byte[] req = Encoding.UTF8.GetBytes(ssdpRequest);
                    udp.Send(req, req.Length, multicast);

                    DateTime deadline = DateTime.UtcNow + DiscoveryBudget;
                    while (DateTime.UtcNow < deadline)
                    {
                        TimeSpan remaining = deadline - DateTime.UtcNow;
                        if (remaining <= TimeSpan.Zero) break;

                        // Async receive with a timeout — Task.WhenAny yields
                        // while waiting, avoiding the old CPU-pegging spin.
                        var receiveTask = udp.ReceiveAsync();
                        var timeoutTask = Task.Delay(remaining);
                        var completed   = Task.WhenAny(receiveTask, timeoutTask).GetAwaiter().GetResult();

                        if (completed != receiveTask) break; // timed out
                        if (receiveTask.IsFaulted) break;

                        string response = Encoding.UTF8.GetString(receiveTask.Result.Buffer);

                        if (!response.ToLower().Contains(SearchTarget.ToLower())) continue;

                        var locationMatch = Regex.Match(response, @"LOCATION:\s*(http://[^\s]+)", RegexOptions.IgnoreCase);
                        var portMatch     = Regex.Match(response, @"HYPERHDR-FBS-PORT:\s*(\d+)", RegexOptions.IgnoreCase);

                        if (locationMatch.Success)
                        {
                            string ip  = new Uri(locationMatch.Groups[1].Value).Host;
                            int fbsPort = portMatch.Success ? int.Parse(portMatch.Groups[1].Value) : 0;
                            Tizen.Log.Debug("HyperTizen", $"SSDP: found HyperHDR at {ip} (FBS port: {fbsPort})");
                            return (ip, fbsPort);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Tizen.Log.Debug("HyperTizen", "SSDP discovery failed: " + ex.Message);
            }

            return (null, 0);
        }
    }
}
