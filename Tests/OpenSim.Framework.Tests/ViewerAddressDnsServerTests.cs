/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Nini.Config;

namespace OpenSim.Framework.Tests;

/// <summary>
/// [ViewerAddress] DnsServers: host names in the Addresses list are looked up by asking the named DNS
/// servers for the A record, instead of the operating system resolver. A fake DNS server on 127.0.0.1 answers
/// with addresses the operating-system stand-in never gives, so each test shows which path produced the address.
/// Only documentation addresses (192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24) and example names are used.
/// </summary>
public class ViewerAddressDnsServerTests
{
    private sealed class CapturingLogger : ILogger
    {
        private readonly object _lock = new();
        private readonly List<(LogLevel Level, string Message)> _entries = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lock)
                _entries.Add((logLevel, formatter(state, exception)));
        }
        public int Warnings { get { lock (_lock) return _entries.Count(e => e.Level == LogLevel.Warning); } }
        public IReadOnlyList<string> Messages { get { lock (_lock) return _entries.Select(e => e.Message).ToList(); } }
    }

    /// <summary>A DNS server on 127.0.0.1 (UDP, port chosen by the system) whose reply is decided per query.</summary>
    private sealed class FakeDns : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly List<string> _names = new();
        private int _queries;

        /// <summary>Returns the datagrams to send back for a query; none means the server stays silent.</summary>
        public Func<byte[], byte[][]> Handler { get; set; } = _ => Array.Empty<byte[]>();

        public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        public IPEndPoint EndPoint => new(IPAddress.Loopback, Port);
        public int Queries => Volatile.Read(ref _queries);
        public IReadOnlyList<string> Names { get { lock (_names) return _names.ToList(); } }

        public FakeDns()
        {
            _loop = Task.Run(Serve);
        }

        public FakeDns Answering(string address)
        {
            IPAddress ip = IPAddress.Parse(address);
            Handler = q => new[] { DnsMessages.Reply(q, ip) };
            return this;
        }

        private async Task Serve()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    UdpReceiveResult received = await _udp.ReceiveAsync(_stop.Token);
                    Interlocked.Increment(ref _queries);
                    lock (_names)
                        _names.Add(DnsMessages.QuestionName(received.Buffer));
                    foreach (byte[] reply in Handler(received.Buffer))
                        await _udp.SendAsync(reply, reply.Length, received.RemoteEndPoint);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _udp.Dispose();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }
            _stop.Dispose();
        }
    }

    /// <summary>Builds and reads DNS messages for the fake server; written separately from the code under test.</summary>
    private static class DnsMessages
    {
        public static string QuestionName(byte[] query)
        {
            List<string> labels = new();
            int i = 12;
            while (i < query.Length && query[i] != 0)
            {
                labels.Add(System.Text.Encoding.ASCII.GetString(query, i + 1, query[i]));
                i += query[i] + 1;
            }
            return string.Join('.', labels);
        }

        private static byte[] Question(byte[] query)
        {
            int i = 12;
            while (query[i] != 0)
                i += query[i] + 1;
            return query[12..(i + 5)];
        }

        public static byte[] EncodedName(string name)
        {
            List<byte> bytes = new();
            foreach (string label in name.Split('.'))
            {
                bytes.Add((byte)label.Length);
                bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
            }
            bytes.Add(0);
            return bytes.ToArray();
        }

        /// <summary>A resource record: owner name, type, class IN, ttl 60, data.</summary>
        public static byte[] Record(byte[] owner, ushort type, byte[] data)
        {
            List<byte> bytes = new(owner) { (byte)(type >> 8), (byte)type, 0, 1, 0, 0, 0, 60, (byte)(data.Length >> 8), (byte)data.Length };
            bytes.AddRange(data);
            return bytes.ToArray();
        }

        /// <summary>A name written as a pointer to the question's name (offset 12).</summary>
        public static readonly byte[] PointerToQuestion = { 0xC0, 0x0C };

        public static byte[] A(IPAddress ip) => Record(PointerToQuestion, 1, ip.GetAddressBytes());

        /// <summary>A response to <paramref name="query"/> carrying the given answer records.</summary>
        public static byte[] Response(byte[] query, ushort? id = null, int rcode = 0, bool isResponse = true,
            byte[]? questionOverride = null, params byte[][] answers)
        {
            byte[] question = questionOverride ?? Question(query);
            ushort replyId = id ?? (ushort)((query[0] << 8) | query[1]);
            List<byte> bytes = new()
            {
                (byte)(replyId >> 8), (byte)replyId,
                (byte)((isResponse ? 0x80 : 0) | 0x01), (byte)(0x80 | rcode),
                0, 1, (byte)(answers.Length >> 8), (byte)answers.Length, 0, 0, 0, 0,
            };
            bytes.AddRange(question);
            foreach (byte[] answer in answers)
                bytes.AddRange(answer);
            return bytes.ToArray();
        }

        public static byte[] Reply(byte[] query, IPAddress ip) => Response(query, answers: A(ip));

        public static byte[] NameQuestion(string name, ushort type = 1)
        {
            List<byte> bytes = new(EncodedName(name)) { (byte)(type >> 8), (byte)type, 0, 1 };
            return bytes.ToArray();
        }
    }

    private const string Host = "region.example.org";
    private static readonly IPEndPoint s_local = new(IPAddress.Parse("192.168.1.10"), 9000);
    private static readonly IPAddress s_viewer = IPAddress.Parse("203.0.113.9");
    private static readonly IPAddress s_os = IPAddress.Parse("192.0.2.50");
    private static IPAddress Ip(string s) => IPAddress.Parse(s);

    private static ViewerAddressConfig Cfg(string addresses, string? dnsServers, ILogger log, int refresh = 30)
        => ViewerAddressConfig.Create(addresses, refresh, log, dnsServers: dnsServers);

    /// <summary>
    /// A selector as the servers build it, with its DNS port and timeout pointed at the fake server, and the
    /// operating system resolver replaced by a stand-in that records what it was asked.
    /// </summary>
    private static ViewerAddressSelector Sel(ViewerAddressConfig cfg, FakeDns dns, ILogger log,
        List<string>? osAsked = null, TimeSpan? timeout = null)
    {
        return ViewerAddressSelector.Create(cfg, log, dns.Port, timeout ?? TimeSpan.FromSeconds(5),
            osResolver: host =>
            {
                osAsked?.Add(host);
                return s_os;
            });
    }

    /// <summary>The address a viewer outside every local network is given, or null when it is the region's own.</summary>
    private static IPAddress? Given(ViewerAddressSelector s)
        => s.ForViewer(9000, s_local, s_viewer).Address is { } a && !a.Equals(s_local.Address) ? a : null;

    // ---- unset: develop's behaviour ----

    [Fact]
    public void Unset_the_operating_system_resolves_every_name_and_no_dns_query_is_sent()
    {
        CapturingLogger log = new();
        using FakeDns dns = new FakeDns().Answering("198.51.100.9");
        ViewerAddressConfig cfg = Cfg($"0.0.0.0/0={Host}", null, log);
        List<string> osAsked = new();

        Assert.Empty(cfg.DnsServers);
        Assert.DoesNotContain("DnsServers", cfg.SetKeys);
        using ViewerAddressSelector s = Sel(cfg, dns, log, osAsked);

        Assert.Equal(s_os, Given(s));
        Assert.Equal(new[] { Host }, osAsked);
        Assert.Equal(0, dns.Queries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_DnsServers_value_is_the_same_as_unset(string? value)
    {
        ViewerAddressConfig cfg = Cfg($"0.0.0.0/0={Host}", value, new CapturingLogger());
        Assert.Empty(cfg.DnsServers);
        Assert.DoesNotContain("DnsServers", cfg.SetKeys);
    }

    // ---- set: names go through the named servers ----

    [Fact]
    public void Set_the_name_in_an_entry_is_resolved_through_the_dns_server()
    {
        CapturingLogger log = new();
        using FakeDns dns = new FakeDns().Answering("198.51.100.9");
        List<string> osAsked = new();
        using ViewerAddressSelector s = Sel(Cfg($"0.0.0.0/0={Host}", "127.0.0.1", log), dns, log, osAsked);

        Assert.Equal(Ip("198.51.100.9"), Given(s));
        Assert.Equal(new[] { Host }, dns.Names);
        Assert.Empty(osAsked);
        Assert.Equal(0, log.Warnings);
    }

    [Fact]
    public void A_refresh_picks_up_a_changed_answer()
    {
        CapturingLogger log = new();
        using FakeDns dns = new FakeDns().Answering("198.51.100.9");
        using ViewerAddressSelector s = Sel(Cfg($"0.0.0.0/0={Host}", "127.0.0.1", log), dns, log);
        Assert.Equal(Ip("198.51.100.9"), Given(s));

        dns.Answering("198.51.100.77");
        Assert.Equal(1, s.Refresh());

        Assert.Equal(Ip("198.51.100.77"), Given(s));
        Assert.Contains(log.Messages, m => m.Contains("changed from 198.51.100.9 to 198.51.100.77"));
    }

    [Fact]
    public void No_answer_keeps_the_last_good_address_and_warns_once_per_refresh()
    {
        CapturingLogger log = new();
        using FakeDns dns = new FakeDns().Answering("198.51.100.9");
        List<string> osAsked = new();
        using ViewerAddressSelector s = Sel(Cfg($"0.0.0.0/0={Host}", "127.0.0.1", log), dns, log, osAsked,
            TimeSpan.FromMilliseconds(300));
        Assert.Equal(Ip("198.51.100.9"), Given(s));

        dns.Handler = _ => Array.Empty<byte[]>();
        Assert.Equal(0, s.Refresh());

        Assert.Equal(Ip("198.51.100.9"), Given(s));
        Assert.Empty(osAsked);
        Assert.Equal(1, log.Warnings);
        Assert.Contains(log.Messages, m => m.Contains(Host) && m.Contains("DNS"));
    }

    [Fact]
    public void Never_answered_falls_back_to_the_operating_system_answer_and_warns_once()
    {
        CapturingLogger log = new();
        using FakeDns dns = new();
        List<string> osAsked = new();
        using ViewerAddressSelector s = Sel(Cfg($"0.0.0.0/0={Host}", "127.0.0.1", log), dns, log, osAsked,
            TimeSpan.FromMilliseconds(300));

        Assert.Equal(s_os, Given(s));
        Assert.Equal(new[] { Host }, osAsked);
        Assert.Equal(1, log.Warnings);
    }

    [Fact]
    public void A_later_dns_answer_replaces_the_operating_system_fallback()
    {
        CapturingLogger log = new();
        using FakeDns dns = new();
        using ViewerAddressSelector s = Sel(Cfg($"0.0.0.0/0={Host}", "127.0.0.1", log), dns, log, null,
            TimeSpan.FromMilliseconds(300));
        Assert.Equal(s_os, Given(s));

        dns.Answering("198.51.100.9");
        Assert.Equal(1, s.Refresh());

        Assert.Equal(Ip("198.51.100.9"), Given(s));
    }

    // ---- a reply that is not an answer to our question is ignored ----

    [Theory]
    [InlineData("garbage")]
    [InlineData("truncated")]
    [InlineData("wrong-id")]
    [InlineData("not-a-response")]
    [InlineData("wrong-question-name")]
    [InlineData("wrong-question-type")]
    [InlineData("name-error")]
    [InlineData("aaaa-only")]
    [InlineData("answer-for-another-name")]
    [InlineData("unspecified-address")]
    [InlineData("short-address-data")]
    public void A_malformed_or_unrelated_reply_is_ignored(string kind)
    {
        CapturingLogger log = new();
        using FakeDns dns = new();
        IPAddress bait = Ip("198.51.100.66");
        dns.Handler = q => kind switch
        {
            "garbage" => new[] { new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 } },
            "truncated" => new[] { DnsMessages.Reply(q, bait)[..20] },
            "wrong-id" => new[] { DnsMessages.Response(q, id: (ushort)(((q[0] << 8) | q[1]) ^ 0x5A5A), answers: DnsMessages.A(bait)) },
            "not-a-response" => new[] { DnsMessages.Response(q, isResponse: false, answers: DnsMessages.A(bait)) },
            "wrong-question-name" => new[] { DnsMessages.Response(q, questionOverride: DnsMessages.NameQuestion("other.example.org"), answers: DnsMessages.A(bait)) },
            "wrong-question-type" => new[] { DnsMessages.Response(q, questionOverride: DnsMessages.NameQuestion(Host, 28), answers: DnsMessages.A(bait)) },
            "name-error" => new[] { DnsMessages.Response(q, rcode: 3, answers: DnsMessages.A(bait)) },
            "aaaa-only" => new[] { DnsMessages.Response(q, answers: DnsMessages.Record(DnsMessages.PointerToQuestion, 28, new byte[16])) },
            "answer-for-another-name" => new[] { DnsMessages.Response(q, answers: DnsMessages.Record(DnsMessages.EncodedName("other.example.org"), 1, bait.GetAddressBytes())) },
            "unspecified-address" => new[] { DnsMessages.Reply(q, IPAddress.Any) },
            "short-address-data" => new[] { DnsMessages.Response(q, answers: DnsMessages.Record(DnsMessages.PointerToQuestion, 1, new byte[] { 198, 51, 100 })) },
            _ => throw new ArgumentException(kind),
        };
        using ViewerAddressSelector s = Sel(Cfg($"0.0.0.0/0={Host}", "127.0.0.1", log), dns, log, null,
            TimeSpan.FromMilliseconds(300));

        // The reply is ignored, so the lookup ends without a DNS answer and the operating system's answer is used.
        Assert.Equal(s_os, Given(s));
        Assert.Equal(1, dns.Queries);
    }

    [Fact]
    public void A_malformed_reply_does_not_hide_the_valid_one_that_follows()
    {
        CapturingLogger log = new();
        using FakeDns dns = new();
        dns.Handler = q => new[]
        {
            DnsMessages.Response(q, id: (ushort)(((q[0] << 8) | q[1]) ^ 0x5A5A), answers: DnsMessages.A(Ip("198.51.100.66"))),
            DnsMessages.Reply(q, Ip("198.51.100.9")),
        };
        using ViewerAddressSelector s = Sel(Cfg($"0.0.0.0/0={Host}", "127.0.0.1", log), dns, log);

        Assert.Equal(Ip("198.51.100.9"), Given(s));
    }

    [Fact]
    public void A_name_answered_through_a_cname_gives_the_address_of_the_target()
    {
        using FakeDns dns = new();
        const string target = "host.example.net";
        dns.Handler = q => new[]
        {
            DnsMessages.Response(q, answers: new[]
            {
                DnsMessages.Record(DnsMessages.PointerToQuestion, 5, DnsMessages.EncodedName(target)),
                DnsMessages.Record(DnsMessages.EncodedName(target), 1, Ip("198.51.100.9").GetAddressBytes()),
            }),
        };
        DnsAddressQuery query = new(new[] { dns.EndPoint }, TimeSpan.FromSeconds(5));

        Assert.Equal(Ip("198.51.100.9"), query.Lookup(Host));
    }

    // ---- bare network entries and literals are unaffected ----

    [Fact]
    public void Bare_network_entries_and_address_literals_send_no_query_and_keep_their_meaning()
    {
        CapturingLogger log = new();
        using FakeDns dns = new FakeDns().Answering("198.51.100.9");
        List<string> osAsked = new();
        ViewerAddressConfig cfg = Cfg($"192.168.1.0/24, 10.0.0.0/8=192.0.2.20, 0.0.0.0/0={Host}", "127.0.0.1", log);
        using ViewerAddressSelector s = Sel(cfg, dns, log, osAsked);
        IPAddress registered = Ip("192.168.1.99");

        // A viewer on the bare network gets the region's registered local address, else the region's own address.
        Assert.Equal(registered, s.ForViewer(9000, s_local, Ip("192.168.1.55"), registeredLocal: registered).Address);
        Assert.Equal(s_local.Address, s.ForViewer(9000, s_local, Ip("192.168.1.55")).Address);
        // An entry with an address literal gives it.
        Assert.Equal(Ip("192.0.2.20"), s.ForViewer(9000, s_local, Ip("10.1.2.3")).Address);
        Assert.Equal(0, dns.Queries);

        // Only the name written after "=" is looked up.
        Assert.Equal(Ip("198.51.100.9"), s.ForViewer(9000, s_local, s_viewer).Address);
        Assert.Equal(new[] { Host }, dns.Names);
        Assert.Empty(osAsked);
    }

    // ---- the servers are tried in turn ----

    [Fact]
    public void The_second_server_is_used_when_the_first_does_not_answer()
    {
        using FakeDns first = new();
        using FakeDns second = new FakeDns().Answering("198.51.100.9");
        DnsAddressQuery query = new(new[] { first.EndPoint, second.EndPoint }, TimeSpan.FromMilliseconds(300));

        Assert.Equal(Ip("198.51.100.9"), query.Lookup(Host));
        Assert.Equal(1, first.Queries);
        Assert.Equal(1, second.Queries);
    }

    [Fact]
    public void The_first_server_that_answers_is_the_only_one_asked()
    {
        using FakeDns first = new FakeDns().Answering("198.51.100.9");
        using FakeDns second = new FakeDns().Answering("198.51.100.77");
        DnsAddressQuery query = new(new[] { first.EndPoint, second.EndPoint }, TimeSpan.FromSeconds(5));

        Assert.Equal(Ip("198.51.100.9"), query.Lookup(Host));
        Assert.Equal(0, second.Queries);
    }

    [Fact]
    public void No_server_answering_gives_null()
    {
        using FakeDns first = new();
        using FakeDns second = new();
        DnsAddressQuery query = new(new[] { first.EndPoint, second.EndPoint }, TimeSpan.FromMilliseconds(200));

        Assert.Null(query.Lookup(Host));
    }

    // ---- configuration ----

    [Fact]
    public void DnsServers_is_a_comma_separated_list_of_addresses_and_counts_as_a_set_key()
    {
        CapturingLogger log = new();
        ViewerAddressConfig cfg = Cfg($"0.0.0.0/0={Host}", " 198.51.100.1 , 203.0.113.5,2001:db8::53", log);

        Assert.Equal(new[] { Ip("198.51.100.1"), Ip("203.0.113.5"), Ip("2001:db8::53") }, cfg.DnsServers);
        Assert.Contains("DnsServers", cfg.SetKeys);
        Assert.Equal(0, log.Warnings);
    }

    [Fact]
    public void A_DnsServers_entry_that_is_not_an_address_is_ignored_with_a_warning()
    {
        CapturingLogger log = new();
        ViewerAddressConfig cfg = Cfg($"0.0.0.0/0={Host}", "198.51.100.1, dns.example.org, 0.0.0.0", log);

        Assert.Equal(new[] { Ip("198.51.100.1") }, cfg.DnsServers);
        Assert.Equal(2, log.Warnings);
    }

    [Fact]
    public void DnsServers_is_read_from_the_section()
    {
        IniConfigSource source = new();
        IConfig section = source.AddConfig("ViewerAddress");
        section.Set("Addresses", $"0.0.0.0/0={Host}");
        section.Set("DnsServers", "198.51.100.1, 203.0.113.5");

        ViewerAddressConfig cfg = ViewerAddressConfig.FromConfig(section, null);

        Assert.Equal(new[] { Ip("198.51.100.1"), Ip("203.0.113.5") }, cfg.DnsServers);
    }

    [Fact]
    public async Task The_startup_line_names_the_servers_and_the_first_answer()
    {
        CapturingLogger log = new();
        using FakeDns dns = new FakeDns().Answering("198.51.100.9");
        ViewerAddressConfig cfg = Cfg($"0.0.0.0/0={Host}", "127.0.0.1", log);
        using ViewerAddressSelector s = Sel(cfg, dns, log);

        await new ViewerAddressStartupLog().ReportOnce(log, cfg, s);

        Assert.Contains(log.Messages, m => m.Contains("DnsServers") && m.Contains("DNS servers: 127.0.0.1")
            && m.Contains($"first lookup of {Host} succeeded: 198.51.100.9"));
    }
}
