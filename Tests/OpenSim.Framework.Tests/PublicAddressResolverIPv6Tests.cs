/*
 * Copyright (c) Legion Builds
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
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenSim.Framework.Tests;

/// <summary>
/// The resolver hands viewers IPv4 only. A caller that is not building a viewer message (the gatekeeper's
/// own address check) can ask for any address family, as the operating system resolver gives it.
/// </summary>
public class PublicAddressResolverIPv6Tests
{
    private static readonly IPEndPoint s_dns = new(IPAddress.Parse("192.0.2.53"), 53);

    [Fact]
    public void Default_refuses_an_ipv6_answer()
    {
        using PublicAddressResolver r = new(null, TimeSpan.Zero, NullLogger.Instance,
            osResolver: _ => IPAddress.Parse("2001:db8::1"));

        Assert.Null(r.Resolve("host.example.org"));
    }

    [Fact]
    public void AcceptIPv6_returns_the_os_answer_in_either_family()
    {
        using PublicAddressResolver r = new(null, TimeSpan.Zero, NullLogger.Instance,
            osResolver: h => IPAddress.Parse(h == "six.example.org" ? "2001:db8::1" : "198.51.100.7"), acceptIPv6: true);

        Assert.Equal(IPAddress.Parse("2001:db8::1"), r.Resolve("six.example.org"));
        Assert.Equal(IPAddress.Parse("198.51.100.7"), r.Resolve("four.example.org"));
    }

    [Fact]
    public void AcceptIPv6_passes_an_ipv6_literal_and_refuses_the_unspecified_address()
    {
        using PublicAddressResolver r = new(null, TimeSpan.Zero, NullLogger.Instance,
            osResolver: _ => throw new InvalidOperationException("not called"), acceptIPv6: true);

        Assert.Equal(IPAddress.Parse("2001:db8::2"), r.Resolve("2001:db8::2"));
        Assert.Null(r.Resolve("::"));
        Assert.Null(r.Resolve("0.0.0.0"));
    }

    [Fact]
    public void Default_ipv6_literal_is_still_refused()
    {
        using PublicAddressResolver r = new(null, TimeSpan.Zero, NullLogger.Instance);

        Assert.Null(r.Resolve("2001:db8::2"));
    }

    [Fact]
    public void AcceptIPv6_direct_answer_is_kept_and_a_later_failure_does_not_replace_it()
    {
        bool fail = false;
        using PublicAddressResolver r = new(s_dns, TimeSpan.Zero, NullLogger.Instance,
            directResolver: (_, _) => fail ? null! : IPAddress.Parse("2001:db8::9"), acceptIPv6: true);

        Assert.Equal(IPAddress.Parse("2001:db8::9"), r.Resolve("host.example.org"));
        fail = true;
        r.Refresh();
        Assert.Equal(IPAddress.Parse("2001:db8::9"), r.Resolve("host.example.org"));
    }
}
