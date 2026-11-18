using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace Epi.Display.Lg
{
    /// <summary>
    /// Reads a webOS display's model over the network (<see cref="LgUpnpDescription"/>): a unicast SSDP search to
    /// the display, then each description it names on its own address, until one has a model. Read-only: a search and
    /// plain HTTP GETs. Kept apart from the controller, whose Crestron namespaces have network types of the same names.
    /// </summary>
    public static class LgUpnpModelReader
    {
        private const int SsdpListenMs = 3000;
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        /// <summary>The display's model, or null when it publishes none. Blocks for up to a few seconds.</summary>
        public static string ReadModel(string host)
        {
            var address = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (address == null) return null;
            var ip = address.ToString();

            var replies = new List<string>();
            using (var udp = new UdpClient(0))
            {
                var request = Encoding.ASCII.GetBytes(LgUpnpDescription.SearchRequest(ip));
                udp.Client.ReceiveTimeout = 1000;
                udp.Send(request, request.Length, new IPEndPoint(address, LgUpnpDescription.SsdpPort));
                var until = DateTime.UtcNow.AddMilliseconds(SsdpListenMs);
                while (DateTime.UtcNow < until)
                {
                    try
                    {
                        var from = new IPEndPoint(IPAddress.Any, 0);
                        replies.Add(Encoding.ASCII.GetString(udp.Receive(ref from)));
                    }
                    catch (SocketException)
                    {
                        if (replies.Count > 0) break;
                    }
                }
            }

            foreach (var url in LgUpnpDescription.Locations(replies, ip))
            {
                var model = LgUpnpDescription.Model(Http.GetStringAsync(url).GetAwaiter().GetResult());
                if (model != null) return model;
            }

            return null;
        }
    }
}
