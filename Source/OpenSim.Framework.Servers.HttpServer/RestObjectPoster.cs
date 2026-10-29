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

using Microsoft.Extensions.Logging;

namespace OpenSim.Framework.Servers.HttpServer;

/// <summary>
/// Makes an asynchronous REST request which doesn't require us to do anything with the response.
/// </summary>
public class RestObjectPoster
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(typeof(RestObjectPoster));

    public static void BeginPostObject<TRequest>(string requestUrl, TRequest obj)
    {
        BeginPostObject("POST", requestUrl, obj);
    }

    public static void BeginPostObject<TRequest>(string verb, string requestUrl, TRequest obj)
    {
        _ = PostObjectAsync(verb, requestUrl, obj);
    }

    public static async Task PostObjectAsync<TRequest>(string verb, string requestUrl, TRequest obj,
        int timeout = 100000, Action<Stream> responseCallback = null)
    {
        try
        {
            using HttpClient client = WebUtil.GetLegacyHttpClient(timeout);
            using CancellationTokenSource cts = new(client.Timeout);
            using HttpRequestMessage request = WebUtil.CreateXmlRequest(verb, requestUrl, obj);
            using HttpResponseMessage response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (responseCallback != null)
            {
                byte[] data = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                using MemoryStream stream = new(data);
                responseCallback(stream);
            }
        }
        catch (Exception e)
        {
            m_log.LogError(e, "[REST OBJECT POSTER]: Request {Verb} {Url} failed", verb, requestUrl);
        }
    }
}
