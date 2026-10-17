/*
 * Copyright (c) Legion Builds
 * Copyright (c) 2026 Legion Builds
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
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace OpenSim.Framework;

/// <summary>
/// One entry of the [ViewerAddress] Addresses list: a viewer network and the address its viewers are given,
/// written as "CIDR=address" or just "CIDR". The address is an IPv4 address or a host name. A bare CIDR
/// means the region's registered local address if it has one, else the region's own (operating system
/// resolved) address. A host without a prefix is a /32 (/128).
/// </summary>
public sealed class LocalNetwork
{
    /// <summary>The network address, host bits cleared.</summary>
    public IPAddress Network { get; }

    public int PrefixLength { get; }

    /// <summary>The IPv4 address handed to viewers on this network, or null (a host name, or the region's own address).</summary>
    public IPAddress Address { get; }

    /// <summary>The host name whose address is handed to viewers on this network, or null.</summary>
    public string Host { get; }

    public LocalNetwork(IPAddress network, int prefixLength, IPAddress address, string host = null)
    {
        if (network.IsIPv4MappedToIPv6)
            network = network.MapToIPv4();
        Network = Mask(network, prefixLength);
        PrefixLength = prefixLength;
        Address = address;
        Host = host;
    }

    public bool Contains(IPAddress ip)
    {
        if (ip is null)
            return false;
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        if (ip.AddressFamily != Network.AddressFamily)
            return false;
        return Mask(ip, PrefixLength).Equals(Network);
    }

    private static IPAddress Mask(IPAddress ip, int prefix)
    {
        byte[] bytes = ip.GetAddressBytes();
        for (int i = 0; i < bytes.Length; i++)
        {
            int bitsInThisByte = Math.Clamp(prefix - (i * 8), 0, 8);
            int mask = bitsInThisByte == 0 ? 0 : (0xFF << (8 - bitsInThisByte)) & 0xFF;
            bytes[i] = (byte)(bytes[i] & mask);
        }
        return new IPAddress(bytes);
    }

    /// <summary>Parses one entry. On failure <paramref name="error"/> says why.</summary>
    public static bool TryParse(string entry, out LocalNetwork network, out string error)
    {
        network = null;
        error = null;

        if (string.IsNullOrWhiteSpace(entry))
        {
            error = "empty entry";
            return false;
        }

        string cidr = entry.Trim();
        IPAddress address = null;
        string host = null;
        int eq = cidr.IndexOf('=');
        if (eq >= 0)
        {
            string addressText = cidr[(eq + 1)..].Trim();
            cidr = cidr[..eq].Trim();
            if (TryParseIp(addressText, out address))
            {
                if (address.AddressFamily != AddressFamily.InterNetwork || address.Equals(IPAddress.Any))
                {
                    error = $"\"{addressText}\" is not an IPv4 address";
                    return false;
                }
            }
            else if (IsHostName(addressText))
                host = addressText;
            else
            {
                error = $"\"{addressText}\" is not an IPv4 address or a host name";
                return false;
            }
        }

        string ipText = cidr;
        int prefix = -1;
        int slash = cidr.IndexOf('/');
        if (slash >= 0)
        {
            ipText = cidr[..slash].Trim();
            if (!int.TryParse(cidr[(slash + 1)..].Trim(), out prefix))
            {
                error = $"\"{cidr}\" has no valid prefix length";
                return false;
            }
        }

        if (!TryParseIp(ipText, out IPAddress ip))
        {
            error = $"\"{ipText}\" is not an IP address";
            return false;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
            if (prefix > 32 && slash >= 0)
                prefix -= 96;
        }

        int max = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (slash < 0)
            prefix = max;
        if (prefix < 0 || prefix > max)
        {
            error = $"prefix length {prefix} is outside 0..{max}";
            return false;
        }

        network = new LocalNetwork(ip, prefix, address, host);
        return true;
    }

    /// <summary>Parses a comma or semicolon separated list; an entry that does not parse is logged and skipped.</summary>
    public static List<LocalNetwork> ParseList(string value, ILogger log)
    {
        List<LocalNetwork> list = new();
        if (string.IsNullOrWhiteSpace(value))
            return list;

        foreach (string part in value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParse(part, out LocalNetwork n, out string error))
                list.Add(n);
            else
                log?.LogWarning("[VIEWER ADDRESS]: Addresses entry \"{0}\" ignored: {1}", part, error);
        }
        return list;
    }

    // Letters, digits and hyphens in dot separated labels of 1 to 63 characters, no label starting or ending with a
    // hyphen. A name made only of digits and dots is a mistyped address, not a host name.
    private static bool IsHostName(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 253)
            return false;

        bool onlyDigitsAndDots = true;
        foreach (string label in text.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-')
                return false;
            foreach (char c in label)
            {
                if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-'))
                    return false;
                if (!char.IsAsciiDigit(c))
                    onlyDigitsAndDots = false;
            }
        }
        return !onlyDigitsAndDots;
    }

    // IPAddress.TryParse accepts "10" and "192.168.1" as shorthand addresses; a network list never means that.
    private static bool TryParseIp(string text, out IPAddress ip)
    {
        ip = null;
        if (string.IsNullOrWhiteSpace(text) || !IPAddress.TryParse(text, out IPAddress parsed))
            return false;
        if (parsed.AddressFamily == AddressFamily.InterNetwork && text.Split('.').Length != 4)
            return false;
        ip = parsed;
        return true;
    }
}
