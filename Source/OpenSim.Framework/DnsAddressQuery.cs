/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace OpenSim.Framework;

/// <summary>
/// Looks up the IPv4 address of a host name by asking DNS servers directly (RFC 1035: one A-record question
/// in a UDP datagram), without the operating system resolver, so a hosts-file entry does not decide the answer.
/// </summary>
/// <remarks>
/// The servers are asked in turn until one gives an answer. A reply counts only when it carries the id of the
/// query, is a response with no error code, repeats exactly the question that was asked, and holds an A record
/// of 4 bytes for the name (directly or through CNAME records for it). Any other datagram is ignored and the
/// wait for the real reply goes on until the timeout. A server that does not answer in time, refuses the
/// datagram or answers with an error is skipped. Nothing is cached here and no state is kept between lookups.
/// </remarks>
public sealed class DnsAddressQuery
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private const int HeaderLength = 12;
    private const ushort TypeA = 1;
    private const ushort TypeCname = 5;
    private const ushort ClassIn = 1;
    private const int MaxLabelLength = 63;
    private const int MaxNameLength = 253;
    private const int MaxPointerJumps = 16;

    private readonly IReadOnlyList<IPEndPoint> m_servers;
    private readonly TimeSpan m_timeout;

    /// <param name="servers">The servers, in the order they are asked (normally port 53).</param>
    /// <param name="timeout">How long each server is given to answer.</param>
    public DnsAddressQuery(IReadOnlyList<IPEndPoint> servers, TimeSpan timeout)
    {
        m_servers = servers ?? Array.Empty<IPEndPoint>();
        m_timeout = timeout > TimeSpan.Zero ? timeout : DefaultTimeout;
    }

    /// <summary>The first IPv4 address any server gives for the host, or null when none does.</summary>
    public IPAddress Lookup(string host)
    {
        byte[] name = EncodeName(host);
        if (name is null)
            return null;

        foreach (IPEndPoint server in m_servers)
        {
            IPAddress answer = Ask(server, host, name);
            if (answer is not null)
                return answer;
        }
        return null;
    }

    private IPAddress Ask(IPEndPoint server, string host, byte[] encodedName)
    {
        ushort id = (ushort)RandomNumberGenerator.GetInt32(0x10000);
        byte[] query = BuildQuery(id, encodedName);
        string wanted = Normalize(host);

        try
        {
            // A connected socket receives datagrams from the server's address and port only.
            using Socket socket = new(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(server);
            socket.Send(query);

            byte[] buffer = new byte[4096];
            Stopwatch clock = Stopwatch.StartNew();
            while (true)
            {
                TimeSpan remaining = m_timeout - clock.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    return null;

                // Poll takes microseconds as an int.
                int microseconds = (int)Math.Min(remaining.TotalMilliseconds * 1000, int.MaxValue / 2);
                if (!socket.Poll(Math.Max(microseconds, 1), SelectMode.SelectRead))
                    return null;

                int length = socket.Receive(buffer);
                if (TryReadAnswer(buffer, length, id, wanted, out IPAddress address))
                    return address;
            }
        }
        catch (SocketException)
        {
            // Refused (an ICMP port-unreachable shows up as a reset on the connected socket) or unreachable.
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private static string Normalize(string host)
    {
        return host.Trim().TrimEnd('.').ToLowerInvariant();
    }

    /// <summary>The name in wire form, or null when it cannot be a DNS name.</summary>
    private static byte[] EncodeName(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;

        string ascii;
        try
        {
            ascii = new IdnMapping().GetAscii(Normalize(host));
        }
        catch (ArgumentException)
        {
            return null;
        }
        if (ascii.Length == 0 || ascii.Length > MaxNameLength)
            return null;

        List<byte> bytes = new();
        foreach (string label in ascii.Split('.'))
        {
            if (label.Length == 0 || label.Length > MaxLabelLength)
                return null;
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0);
        return bytes.ToArray();
    }

    /// <summary>A standard query (recursion desired) with one question: the name, type A, class IN.</summary>
    private static byte[] BuildQuery(ushort id, byte[] encodedName)
    {
        byte[] query = new byte[HeaderLength + encodedName.Length + 4];
        query[0] = (byte)(id >> 8);
        query[1] = (byte)id;
        query[2] = 0x01; // QR = 0 (query), opcode 0, RD = 1
        query[5] = 1;    // one question
        encodedName.CopyTo(query, HeaderLength);
        int end = HeaderLength + encodedName.Length;
        query[end + 1] = (byte)TypeA;
        query[end + 3] = (byte)ClassIn;
        return query;
    }

    /// <summary>
    /// True with the first A-record address for the asked name when the datagram is a valid reply to the query.
    /// </summary>
    private static bool TryReadAnswer(byte[] data, int length, ushort id, string wantedName, out IPAddress address)
    {
        address = null;
        if (length < HeaderLength)
            return false;
        if (((data[0] << 8) | data[1]) != id)
            return false;
        bool isResponse = (data[2] & 0x80) != 0;
        int opcode = (data[2] >> 3) & 0x0F;
        int rcode = data[3] & 0x0F;
        if (!isResponse || opcode != 0 || rcode != 0)
            return false;
        int questions = (data[4] << 8) | data[5];
        int answers = (data[6] << 8) | data[7];
        if (questions != 1)
            return false;

        // The question must be the one asked.
        int position = HeaderLength;
        string questionName = ReadName(data, length, ref position);
        if (questionName is null || position + 4 > length)
            return false;
        if (questionName != wantedName || ReadUInt16(data, position) != TypeA || ReadUInt16(data, position + 2) != ClassIn)
            return false;
        position += 4;

        // Records for the name, or for the target of a CNAME that was itself given for the name.
        HashSet<string> names = new() { wantedName };
        for (int i = 0; i < answers; i++)
        {
            string owner = ReadName(data, length, ref position);
            if (owner is null || position + 10 > length)
                return false;
            int type = ReadUInt16(data, position);
            int recordClass = ReadUInt16(data, position + 2);
            int dataLength = ReadUInt16(data, position + 8);
            position += 10;
            if (position + dataLength > length)
                return false;

            if (recordClass == ClassIn && names.Contains(owner))
            {
                if (type == TypeCname)
                {
                    int targetPosition = position;
                    string target = ReadName(data, length, ref targetPosition);
                    if (target is null)
                        return false;
                    names.Add(target);
                }
                else if (type == TypeA && dataLength == 4)
                {
                    IPAddress candidate = new(new ReadOnlySpan<byte>(data, position, 4));
                    if (!candidate.Equals(IPAddress.Any))
                    {
                        address = candidate;
                        return true;
                    }
                }
            }
            position += dataLength;
        }
        return false;
    }

    private static int ReadUInt16(byte[] data, int position) => (data[position] << 8) | data[position + 1];

    /// <summary>
    /// Reads a possibly compressed name starting at <paramref name="position"/> (RFC 1035 section 4.1.4) and moves
    /// <paramref name="position"/> past its first form. Returns it lower case without a trailing dot, or null when
    /// it is malformed.
    /// </summary>
    private static string ReadName(byte[] data, int length, ref int position)
    {
        StringBuilder name = new();
        int cursor = position;
        int endAfterName = -1;
        int jumps = 0;
        while (true)
        {
            if (cursor >= length)
                return null;
            int size = data[cursor];
            if (size == 0)
            {
                if (endAfterName < 0)
                    endAfterName = cursor + 1;
                break;
            }
            if ((size & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= length || ++jumps > MaxPointerJumps)
                    return null;
                if (endAfterName < 0)
                    endAfterName = cursor + 2;
                cursor = ((size & 0x3F) << 8) | data[cursor + 1];
                continue;
            }
            if ((size & 0xC0) != 0 || cursor + 1 + size > length)
                return null;

            if (name.Length > 0)
                name.Append('.');
            name.Append(Encoding.ASCII.GetString(data, cursor + 1, size));
            if (name.Length > MaxNameLength)
                return null;
            cursor += 1 + size;
        }

        position = endAfterName;
        return name.ToString().ToLowerInvariant();
    }
}
