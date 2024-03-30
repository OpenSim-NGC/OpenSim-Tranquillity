/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System.Net;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using IPNetwork = LukeSkywalker.IPNetwork.IPNetwork;


namespace OpenSim.Framework
{
    public class OutboundUrlFilter
    {
        public string Name { get; private set; }

        private List<IPNetwork> m_blacklistNetworks;
        private List<IPEndPoint> m_blacklistEndPoints;

        private List<IPNetwork> m_blacklistExceptionNetworks;
        private List<IPEndPoint> m_blacklistExceptionEndPoints;

        private ILogger<OutboundUrlFilter> m_logger;

        public OutboundUrlFilter(
            ILogger<OutboundUrlFilter> logger,
            string name,
            List<IPNetwork> blacklistNetworks, List<IPEndPoint> blacklistEndPoints,
            List<IPNetwork> blacklistExceptionNetworks, List<IPEndPoint> blacklistExceptionEndPoints)
        {
            m_logger = logger;
            Name = name;

            m_blacklistNetworks = blacklistNetworks;
            m_blacklistEndPoints = blacklistEndPoints;
            m_blacklistExceptionNetworks = blacklistExceptionNetworks;
            m_blacklistExceptionEndPoints = blacklistExceptionEndPoints;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="OpenSim.Framework.OutboundUrlFilter"/> class.
        /// </summary>
        /// <param name="name">Name of the filter for logging purposes.</param>
        /// <param name="config">Filter configuration</param>
        public OutboundUrlFilter(ILogger<OutboundUrlFilter> logger, string name, IConfiguration config)
        {
            m_logger = logger;
            Name = name;

            string configBlacklist
                = "0.0.0.0/8|10.0.0.0/8|100.64.0.0/10|127.0.0.0/8|169.254.0.0/16|172.16.0.0/12|192.0.0.0/24|192.0.2.0/24|192.88.99.0/24|192.168.0.0/16|198.18.0.0/15|198.51.100.0/24|203.0.113.0/24|224.0.0.0/4|240.0.0.0/4|255.255.255.255/32";
            string configBlacklistExceptions = "";

            var networkConfig = config.GetSection("Network");
            if (networkConfig.Exists())
            {
                configBlacklist = networkConfig.GetValue("OutboundDisallowForUserScripts", configBlacklist);
                configBlacklistExceptions = networkConfig.GetValue("OutboundDisallowForUserScriptsExcept", configBlacklistExceptions);
            }

            m_logger.LogDebug(
                $"[OUTBOUND URL FILTER]: OutboundDisallowForUserScripts for {Name} is [{configBlacklist}]");
            m_logger.LogDebug(
                $"[OUTBOUND URL FILTER]: OutboundDisallowForUserScriptsExcept for {Name} is [{configBlacklistExceptions}]");

            OutboundUrlFilter.ParseConfigList(
                configBlacklist, Name, out m_blacklistNetworks, out m_blacklistEndPoints);
            OutboundUrlFilter.ParseConfigList(
                configBlacklistExceptions, Name, out m_blacklistExceptionNetworks, out m_blacklistExceptionEndPoints);
        }

        private static void ParseConfigList(
            string fullConfigEntry, string filterName, out List<IPNetwork> networks, out List<IPEndPoint> endPoints)
        {
            // Parse blacklist
            string[] configBlacklistEntries
                = fullConfigEntry.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries);

            configBlacklistEntries = configBlacklistEntries.Select(e => e.Trim()).ToArray();

            networks = new List<IPNetwork>();
            endPoints = new List<IPEndPoint>();

            foreach (string configEntry in configBlacklistEntries)
            {
                if (configEntry.Contains("/"))
                {
                    IPNetwork network;

                    if (!IPNetwork.TryParse(configEntry, out network))
                    {
//                        m_logger.LogError(
//                            $"[OUTBOUND URL FILTER]: Entry [{configEntry}] is invalid network for {filterName}");

                        continue;
                    }

                    networks.Add(network);
                }
                else
                {
                    Uri configEntryUri;

                    if (!Uri.TryCreate("http://" + configEntry, UriKind.Absolute, out configEntryUri))
                    {
//                        m_logger.LogError(
//                            $"[OUTBOUND URL FILTER]: EndPoint entry [{configEntry}] is invalid endpoint for {filterName}");

                        continue;
                    }

                    IPAddress[] addresses = Dns.GetHostAddresses(configEntryUri.Host);

                    foreach (IPAddress addr in addresses)
                    {
                        if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
//                          m_logger.LogDebug($"[OUTBOUND URL FILTER]: Found address [{addr}] in config");

                            IPEndPoint configEntryEp = new IPEndPoint(addr, configEntryUri.Port);
                            endPoints.Add(configEntryEp);

//                         m_logger.LogDebug($"[OUTBOUND URL FILTER]: Added blacklist exception [{configEntryEp}]");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Determines if an url is in a list of networks and endpoints.
        /// </summary>
        /// <returns></returns>
        /// <param name="url">IP address</param>
        /// <param name="port"></param>
        /// <param name="networks">Networks.</param>
        /// <param name="endPoints">End points.</param>
        /// <param name="filterName">Filter name.</param>
        private static bool IsInNetwork(
            IPAddress addr, int port, List<IPNetwork> networks, List<IPEndPoint> endPoints, string filterName)
        {
            foreach (IPNetwork ipn in networks)
            {
//                                            m_log.DebugFormat(
//                                                "[OUTBOUND URL FILTER]: Checking [{0}] against network [{1}]", addr, ipn);

                if (IPNetwork.Contains(ipn, addr))
                {
//                                                    m_log.DebugFormat(
//                                                        "[OUTBOUND URL FILTER]: Found [{0}] in network [{1}]", addr, ipn);

                    return true;
                }
            }

            //                    m_log.DebugFormat("[OUTBOUND URL FILTER]: Found address [{0}]", addr);

            foreach (IPEndPoint ep in endPoints)
            {
//                m_log.DebugFormat(
//                    "[OUTBOUND URL FILTER]: Checking [{0}:{1}] against endpoint [{2}]",
//                    addr, port, ep);

                if (addr.Equals(ep.Address) && port == ep.Port)
                {
//                    m_log.DebugFormat(
//                        "[OUTBOUND URL FILTER]: Found [{0}:{1}] in endpoint [{2}]", addr, port, ep);

                    return true;
                }
            }

//            m_log.DebugFormat("[OUTBOUND URL FILTER]: Did not find [{0}:{1}] in list", addr, port);

            return false;
        }

        /// <summary>
        /// Checks whether the given url is allowed by the filter.
        /// </summary>
        /// <returns></returns>
        public bool CheckAllowed(Uri url)
        {
            bool allowed = true;

            // Check that we are permitted to make calls to this endpoint.
            bool foundIpv4Address = false;

            IPAddress[] addresses = null;
            
            try
            {
                addresses = Dns.GetHostAddresses(url.Host);
            }
            catch
            {
                // If there is a DNS error, we can't stop the script!
                return true;
            }

            foreach (IPAddress addr in addresses)
            {
                if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
//                    m_log.DebugFormat("[OUTBOUND URL FILTER]: Found address [{0}]", addr);

                    foundIpv4Address = true;

                    // Check blacklist
                    if (OutboundUrlFilter.IsInNetwork(addr, url.Port, m_blacklistNetworks, m_blacklistEndPoints, Name))
                    {
//                        m_log.DebugFormat("[OUTBOUND URL FILTER]: Found [{0}] in blacklist for {1}", url, Name);

                        // Check blacklist exceptions
                        allowed
                            = OutboundUrlFilter.IsInNetwork(
                                addr, url.Port, m_blacklistExceptionNetworks, m_blacklistExceptionEndPoints, Name);

//                        if (allowed)
//                            m_log.DebugFormat("[OUTBOUND URL FILTER]: Found [{0}] in whitelist for {1}", url, Name);
                    }
                }

                // Found at least one address in a blacklist and not a blacklist exception
                if (!allowed)
                    return false;
//                else
//                    m_log.DebugFormat("[OUTBOUND URL FILTER]: URL [{0}] not in blacklist for {1}", url, Name);
            }

            // We do not know how to handle IPv6 securely yet.
            if (!foundIpv4Address)
                return false;

//            m_log.DebugFormat("[OUTBOUND URL FILTER]: Allowing request [{0}]", url);

            return allowed;
        }
    }
}
