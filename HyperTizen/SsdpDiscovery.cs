using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace HyperTizen
{
    public static class SsdpDiscovery
    {
        private const string SearchTarget = "urn:hyperhdr.eu:device:basic:1";

        // Returns (ip, port) of HyperHDR's FlatBuffers TCP endpoint, or (null, 0) if not found.
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
                    udp.Client.ReceiveTimeout = 5000;
                    var multicast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                    byte[] req = Encoding.UTF8.GetBytes(ssdpRequest);
                    udp.Send(req, req.Length, multicast);

                    DateTime deadline = DateTime.Now.AddSeconds(5);
                    while (DateTime.Now < deadline)
                    {
                        if (udp.Available == 0) continue;
                        var remote = new IPEndPoint(IPAddress.Any, 0);
                        string response = Encoding.UTF8.GetString(udp.Receive(ref remote));

                        if (!response.ToLower().Contains(SearchTarget.ToLower())) continue;

                        var locationMatch = Regex.Match(response, @"LOCATION:\s*(http://[^\s]+)", RegexOptions.IgnoreCase);
                        var portMatch     = Regex.Match(response, @"HYPERHDR-FBS-PORT:\s*(\d+)", RegexOptions.IgnoreCase);

                        if (locationMatch.Success && portMatch.Success)
                        {
                            string ip = new Uri(locationMatch.Groups[1].Value).Host;
                            int port  = int.Parse(portMatch.Groups[1].Value);
                            Tizen.Log.Debug("HyperTizen", $"SSDP: found HyperHDR at {ip}:{port}");
                            return (ip, port);
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
