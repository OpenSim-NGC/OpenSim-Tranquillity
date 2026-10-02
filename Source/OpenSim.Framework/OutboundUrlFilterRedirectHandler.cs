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

using System.Net.Http;

namespace OpenSim.Framework;

/// <summary>
/// Sends a script-initiated request with every URL it goes to - the first one and each redirect - checked by an
/// <see cref="OutboundUrlFilter"/>.
/// </summary>
/// <remarks>
/// The inner handler must not follow redirects itself; this handler follows them, with the rules .NET's own
/// redirect handling uses: 300/301/302 turn a POST into a GET, 303 turns anything but HEAD into a GET, 307/308 keep
/// the method and body, https never redirects to http, the Authorization header is dropped, and after
/// <c>maxRedirects</c> redirects the last redirect response is returned as it is.
/// A URL the filter refuses ends the request with an <see cref="HttpRequestException"/>.
/// </remarks>
public sealed class OutboundUrlFilterRedirectHandler : DelegatingHandler
{
    public const string RedirectBlockedPrefix = "URL from HTTP redirect blocked: ";

    private readonly OutboundUrlFilter m_filter;
    private readonly int m_maxRedirects;

    public OutboundUrlFilterRedirectHandler(OutboundUrlFilter filter, HttpMessageHandler innerHandler, int maxRedirects)
        : base(innerHandler)
    {
        m_filter = filter ?? throw new ArgumentNullException(nameof(filter));
        m_maxRedirects = maxRedirects;
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CheckFirstUrl(request);
        for (int redirects = 0; ; redirects++)
        {
            HttpResponseMessage response;
            try
            {
                response = base.Send(request, cancellationToken);
            }
            catch (HttpRequestException e) when (e.InnerException is OutboundUrlFilterRefusedException refused)
            {
                throw refused;
            }
            if (!PrepareRedirect(request, response, redirects))
                return response;
            response.Dispose();
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CheckFirstUrl(request);
        for (int redirects = 0; ; redirects++)
        {
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException e) when (e.InnerException is OutboundUrlFilterRefusedException refused)
            {
                throw refused;
            }
            if (!PrepareRedirect(request, response, redirects))
                return response;
            response.Dispose();
        }
    }

    private void CheckFirstUrl(HttpRequestMessage request)
    {
        Uri url = request.RequestUri;
        if (url is null || !url.IsAbsoluteUri || !m_filter.CheckAllowed(url))
            throw new HttpRequestException(string.Format("Request to {0} disallowed by filter", url));
    }

    /// <summary>
    /// If <paramref name="response"/> is a redirect this handler follows, point <paramref name="request"/> at its
    /// target and return true. Throws if the filter refuses the target.
    /// </summary>
    private bool PrepareRedirect(HttpRequestMessage request, HttpResponseMessage response, int redirectsSoFar)
    {
        bool forceGet;
        switch ((int)response.StatusCode)
        {
            case 300:
            case 301:
            case 302:
                forceGet = request.Method == HttpMethod.Post;
                break;
            case 303:
                forceGet = request.Method != HttpMethod.Head;
                break;
            case 307:
            case 308:
                forceGet = false;
                break;
            default:
                return false;
        }

        Uri location = response.Headers.Location;
        if (location is null || redirectsSoFar >= m_maxRedirects)
            return false;

        if (!location.IsAbsoluteUri)
            location = new Uri(request.RequestUri, location);

        if (location.Scheme != Uri.UriSchemeHttp && location.Scheme != Uri.UriSchemeHttps)
            return false;
        if (request.RequestUri.Scheme == Uri.UriSchemeHttps && location.Scheme == Uri.UriSchemeHttp)
            return false;

        if (!m_filter.CheckAllowed(location))
        {
            response.Dispose();
            throw new HttpRequestException(RedirectBlockedPrefix + location.AbsoluteUri);
        }

        request.RequestUri = location;
        if (forceGet)
        {
            request.Method = HttpMethod.Get;
            request.Content = null;
        }
        request.Headers.Authorization = null;
        return true;
    }
}
