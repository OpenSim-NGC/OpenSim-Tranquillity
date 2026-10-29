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

using System.Text;
using OpenSim.Framework;

namespace OpenSim.ConsoleClient;

public delegate void ReplyDelegate(string requestUrl, string requestData, string replyData);

public class Requester
{
    public static void MakeRequest(string requestUrl, string data,
            ReplyDelegate action)
    {
        _ = MakeRequestAsync(requestUrl, data, action);
    }

    public static async Task MakeRequestAsync(string requestUrl, string data, ReplyDelegate action)
    {
        try
        {
            using HttpClient client = WebUtil.GetLegacyHttpClient(100000);
            using CancellationTokenSource cts = new(client.Timeout);
            using HttpRequestMessage request = new(HttpMethod.Post, requestUrl);
            request.Content = new ByteArrayContent(Encoding.ASCII.GetBytes(data));
            request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/x-www-form-urlencoded");
            using HttpResponseMessage response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using Stream stream = new MemoryStream(await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false));
            using StreamReader reader = new(stream);
            action(requestUrl, data, reader.ReadToEnd());
        }
        catch (Exception e)
        {
            string operation = (Uri.TryCreate(requestUrl, UriKind.Absolute, out Uri uri) ? uri.AbsolutePath : string.Empty) switch
            {
                "/StartSession/" => "Login failed",
                string path when path.StartsWith("/ReadResponses/", StringComparison.Ordinal) => "Polling stopped; reconnect to resume",
                _ => "Request failed"
            };
            System.Console.Error.WriteLine($"[CONSOLE CLIENT]: {operation}: {e.Message}");
        }
    }
}
