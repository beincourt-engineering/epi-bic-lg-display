using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

// The plugin builds without nullable annotations; the test project links this file and has them on.
#nullable disable

namespace Epi.Display.Lg
{
    /// <summary>
    /// The model a webOS display publishes in its UPnP device description. The control port has no model query, and a
    /// webOS TV (the UR340C, for one) answers neither the serial number (<c>fy</c>) nor the software version
    /// (<c>fz</c>) query, but it describes itself over UPnP: a unicast SSDP search to its UDP port 1900 names the
    /// description URLs (their ports change), and each description's <c>modelNumber</c> - or <c>modelName</c> when
    /// that is the model rather than "LG Smart TV" - is the model. The descriptions carry no serial number or
    /// firmware version.
    /// </summary>
    /// <remarks>Free of Crestron types so it can be tested on its own.</remarks>
    public static class LgUpnpDescription
    {
        /// <summary>The UDP port a display answers an SSDP search on.</summary>
        public const int SsdpPort = 1900;

        /// <summary>
        /// The TCP ports a webOS 4 display (the UR340C) serves its descriptions on, read when the SSDP search gets no
        /// answer - a processor whose firewall drops the reply never sees the search's result.
        /// </summary>
        public static readonly int[] KnownDescriptionPorts = { 1584, 1819 };

        /// <summary>The description URLs on <see cref="KnownDescriptionPorts"/> for a display's address.</summary>
        public static List<string> KnownLocations(string host)
        {
            var found = new List<string>();
            if (string.IsNullOrWhiteSpace(host)) return found;
            foreach (var port in KnownDescriptionPorts) found.Add("http://" + host.Trim() + ":" + port + "/");
            return found;
        }

        private static readonly Regex Location = new Regex(@"^\s*LOCATION\s*:\s*(\S+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static readonly Regex ModelShape = new Regex(@"^[A-Za-z0-9][A-Za-z0-9\-./]{3,40}$");

        /// <summary>A unicast SSDP search for every service the display offers.</summary>
        public static string SearchRequest(string host)
        {
            return "M-SEARCH * HTTP/1.1\r\n"
                + "HOST: " + host + ":" + SsdpPort + "\r\n"
                + "MAN: \"ssdp:discover\"\r\n"
                + "MX: 2\r\n"
                + "ST: ssdp:all\r\n\r\n";
        }

        /// <summary>
        /// The description URLs in SSDP replies that point at <paramref name="host"/> itself, over http, in the order
        /// first seen. A URL naming another host is left out: only the display's own description is read.
        /// </summary>
        public static List<string> Locations(IEnumerable<string> replies, string host)
        {
            var found = new List<string>();
            if (replies == null || string.IsNullOrWhiteSpace(host)) return found;

            foreach (var reply in replies)
            {
                if (string.IsNullOrEmpty(reply)) continue;
                foreach (Match m in Location.Matches(reply))
                {
                    var url = m.Groups[1].Value.Trim();
                    Uri uri;
                    if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttp) continue;
                    if (!string.Equals(uri.Host, host.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                    if (!found.Contains(url)) found.Add(url);
                }
            }

            return found;
        }

        /// <summary>
        /// The model in a device description: its <c>modelNumber</c>, else its <c>modelName</c> when that reads as a
        /// model (letters and digits, no spaces - "55UR340C9UD", not "LG Smart TV"). Null when it has neither.
        /// </summary>
        public static string Model(string description)
        {
            if (string.IsNullOrEmpty(description)) return null;

            foreach (var field in new[] { "modelNumber", "modelName" })
            {
                var m = Regex.Match(description, "<(?:\\w+:)?" + field + ">\\s*([^<]*?)\\s*</", RegexOptions.IgnoreCase);
                if (m.Success && ModelShape.IsMatch(m.Groups[1].Value)) return m.Groups[1].Value;
            }

            return null;
        }
    }
}
