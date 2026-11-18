using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace Epi.Display.Lg
{
    /// <summary>What one model read found: the model, or null, and in words how it went.</summary>
    public sealed class LgModelRead
    {
        public string Model { get; set; }

        /// <summary>
        /// "55UR340C9UD from the UPnP search", "55UR340C9UD from port 1584", "no reply to the UPnP search and no
        /// description on the known ports", "a description without a model", or "error: ...".
        /// </summary>
        public string Outcome { get; set; }
    }

    /// <summary>
    /// Reads a webOS display's model over the network (<see cref="LgUpnpDescription"/>): a unicast SSDP search to
    /// the display, then each description it names on its own address; when the search gets no answer, the
    /// descriptions on the ports webOS 4 serves them on. Read-only: a search and plain HTTP GETs. Kept apart from the
    /// controller, whose Crestron namespaces have network types of the same names.
    /// </summary>
    public static class LgUpnpModelReader
    {
        private const int SsdpListenMs = 3000;
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        /// <summary>The display's model, or null when it publishes none. Blocks for up to a few seconds.</summary>
        public static string ReadModel(string host)
        {
            return Read(host).Model;
        }

        /// <summary>The display's model and how the read went. Blocks for up to several seconds; never throws.</summary>
        public static LgModelRead Read(string host)
        {
            try
            {
                var address = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (address == null) return new LgModelRead { Outcome = "no IPv4 address for " + host };
                var ip = address.ToString();

                var searched = LgUpnpDescription.Locations(Search(address, ip), ip);
                var described = false;
                foreach (var url in searched)
                {
                    var model = Fetch(url);
                    if (model == null) { described = true; continue; }
                    return new LgModelRead { Model = model, Outcome = model + " from the UPnP search" };
                }

                foreach (var url in LgUpnpDescription.KnownLocations(ip))
                {
                    string body;
                    try { body = Http.GetStringAsync(url).GetAwaiter().GetResult(); }
                    catch (Exception) { continue; }
                    described = true;
                    var model = LgUpnpDescription.Model(body);
                    if (model != null)
                        return new LgModelRead { Model = model, Outcome = model + " from port " + new Uri(url).Port };
                }

                if (described) return new LgModelRead { Outcome = "a description without a model" };
                return new LgModelRead
                {
                    Outcome = searched.Count == 0
                        ? "no reply to the UPnP search and no description on the known ports"
                        : "no description at the addresses the UPnP search named",
                };
            }
            catch (Exception e)
            {
                return new LgModelRead { Outcome = "error: " + e.Message };
            }
        }

        private static List<string> Search(IPAddress address, string ip)
        {
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

            return replies;
        }

        private static string Fetch(string url)
        {
            try { return LgUpnpDescription.Model(Http.GetStringAsync(url).GetAwaiter().GetResult()); }
            catch (Exception) { return null; }
        }
    }
}
