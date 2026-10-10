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

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OpenSim.Region.CoreModules.Scripting.OutboundHardening.Tests;

/// <summary>
/// A minimal HTTP/1.1 endpoint on 127.0.0.1 that records every request it receives and answers it itself.
/// </summary>
/// <remarks>
/// Used both as a plain destination and as the HTTP proxy for the code under test. As a proxy it receives
/// absolute-form request lines ("GET http://10.0.0.1/x HTTP/1.1") and never forwards them, so a test can see
/// which destination the code tried to reach without any packet leaving the machine - whichever version of the
/// code is running.
/// </remarks>
public sealed class RecordingHttpServer : IDisposable
{
    public sealed class Received
    {
        public string RequestLine = "";
        public string Method = "";
        public string Target = "";
        public string HeaderBlock = "";
        public byte[] Body = Array.Empty<byte>();

        public IEnumerable<string> HeaderLines =>
            HeaderBlock.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }

    public sealed class Reply
    {
        public int Status = 200;
        public string Reason = "OK";
        public string ContentType = "text/plain";
        public byte[] Body = Array.Empty<byte>();
        public string? Location;
        public int BodyDelayMilliseconds;
        public int? ContentLength;
        public bool OmitContentLength;
        public Dictionary<string, string> Headers = new();

        public static Reply Text(string body) => new() { Body = Encoding.UTF8.GetBytes(body) };

        public static Reply Redirect(int status, string location) =>
            new() { Status = status, Reason = "Redirect", Location = location };
    }

    private readonly TcpListener m_listener;
    private readonly CancellationTokenSource m_cts = new();
    private readonly Task m_acceptLoop;

    public ConcurrentQueue<Received> Requests { get; } = new();
    public int Connections => m_connections;
    private int m_connections;

    public Func<Received, Reply> Responder { get; set; } = _ => Reply.Text("ok");

    public RecordingHttpServer()
    {
        m_listener = new TcpListener(IPAddress.Loopback, 0);
        m_listener.Start();
        m_acceptLoop = Task.Run(AcceptLoop);
    }

    public int Port => ((IPEndPoint)m_listener.LocalEndpoint).Port;
    public Uri BaseUri => new($"http://127.0.0.1:{Port}/");

    /// <summary>Every request line received, e.g. "POST http://10.0.0.1/ HTTP/1.1".</summary>
    public List<string> Targets => Requests.Select(r => r.Target).ToList();

    /// <summary>Wait until at least <paramref name="count"/> requests arrived, or the timeout passed.</summary>
    public bool WaitForRequests(int count, int timeoutMs = 10000)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (Requests.Count < count && DateTime.UtcNow < end)
            Thread.Sleep(20);
        return Requests.Count >= count;
    }

    private async Task AcceptLoop()
    {
        while (!m_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await m_listener.AcceptTcpClientAsync(m_cts.Token);
            }
            catch
            {
                return;
            }
            Interlocked.Increment(ref m_connections);
            _ = Task.Run(() => Serve(client));
        }
    }

    private void Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 10000;
                NetworkStream stream = client.GetStream();
                byte[]? head = ReadHead(stream);
                if (head is null)
                    return;

                string headText = Encoding.Latin1.GetString(head);
                int firstBreak = headText.IndexOf("\r\n", StringComparison.Ordinal);
                Received rec = new()
                {
                    RequestLine = firstBreak < 0 ? headText : headText[..firstBreak],
                    HeaderBlock = firstBreak < 0 ? "" : headText[(firstBreak + 2)..],
                };
                string[] parts = rec.RequestLine.Split(' ');
                rec.Method = parts.Length > 0 ? parts[0] : "";
                rec.Target = parts.Length > 1 ? parts[1] : "";

                int length = 0;
                foreach (string line in rec.HeaderLines)
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(line[15..].Trim(), out length);
                }
                if (length > 0)
                {
                    byte[] body = new byte[length];
                    int read = 0;
                    while (read < length)
                    {
                        int n = stream.Read(body, read, length - read);
                        if (n <= 0)
                            break;
                        read += n;
                    }
                    rec.Body = body;
                }
                Requests.Enqueue(rec);

                Reply reply = Responder(rec);
                StringBuilder sb = new();
                sb.Append($"HTTP/1.1 {reply.Status} {reply.Reason}\r\n");
                if (reply.Location is not null)
                    sb.Append($"Location: {reply.Location}\r\n");
                sb.Append($"Content-Type: {reply.ContentType}\r\n");
                if (!reply.OmitContentLength)
                    sb.Append($"Content-Length: {reply.ContentLength ?? reply.Body.Length}\r\n");
                foreach (var header in reply.Headers)
                    sb.Append($"{header.Key}: {header.Value}\r\n");
                sb.Append("Connection: close\r\n\r\n");
                byte[] headBytes = Encoding.ASCII.GetBytes(sb.ToString());
                stream.Write(headBytes, 0, headBytes.Length);
                if (reply.BodyDelayMilliseconds > 0)
                    Thread.Sleep(reply.BodyDelayMilliseconds);
                stream.Write(reply.Body, 0, reply.Body.Length);
                stream.Flush();
            }
            catch
            {
                // a test that failed or ended mid-request
            }
        }
    }

    private static byte[]? ReadHead(NetworkStream stream)
    {
        List<byte> buf = new();
        int b;
        while ((b = stream.ReadByte()) >= 0)
        {
            buf.Add((byte)b);
            int n = buf.Count;
            if (n >= 4 && buf[n - 4] == '\r' && buf[n - 3] == '\n' && buf[n - 2] == '\r' && buf[n - 1] == '\n')
                return buf.Take(n - 4).ToArray();
            if (n > 65536)
                return null;
        }
        return buf.Count > 0 ? buf.ToArray() : null;
    }

    public void Dispose()
    {
        m_cts.Cancel();
        m_listener.Stop();
        try { m_acceptLoop.Wait(2000); } catch { }
        m_cts.Dispose();
    }
}
