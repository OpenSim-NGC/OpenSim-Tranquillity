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

using System.Collections.Specialized;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using OpenSim.Framework.ServiceAuth;

namespace OpenSim.Groups;

/// <summary>
/// <c>[Groups] SecretKey</c>: a key the regions and Robust's groups service share. Robust's groups service takes the
/// requesting avatar from the request, so with a key set it answers only callers that send it.
/// </summary>
public static class GroupsSecretKey
{
    /// <summary>The header the key travels in.</summary>
    public const string Header = "X-OpenSim-Groups-Key";

    /// <summary>Whether <paramref name="given"/> is the key, compared in constant time.</summary>
    public static bool Matches(string expected, string given)
    {
        if (string.IsNullOrEmpty(expected) || given is null)
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(given));
    }
}

/// <summary>
/// Adds the groups key to every request a region sends Robust's groups service, alongside the HTTP authentication
/// the region may also be set up for (<c>AuthType</c>).
/// </summary>
public sealed class GroupsSecretKeyAuth : IServiceAuth
{
    private readonly string m_key;
    private readonly IServiceAuth m_inner;

    public GroupsSecretKeyAuth(string key, IServiceAuth inner)
    {
        m_key = key;
        m_inner = inner;
    }

    public string Name => m_inner is null ? "GroupsSecretKey" : m_inner.Name + "+GroupsSecretKey";

    public bool Authenticate(string data) => m_inner?.Authenticate(data) ?? true;

    public bool Authenticate(NameValueCollection headers, AddHeaderDelegate d, out HttpStatusCode statusCode)
    {
        if (m_inner is not null)
            return m_inner.Authenticate(headers, d, out statusCode);
        statusCode = HttpStatusCode.OK;
        return true;
    }

    public void AddAuthorization(NameValueCollection headers)
    {
        m_inner?.AddAuthorization(headers);
        headers[GroupsSecretKey.Header] = m_key;
    }

    public void AddAuthorization(HttpRequestHeaders headers)
    {
        m_inner?.AddAuthorization(headers);
        headers.Remove(GroupsSecretKey.Header);
        headers.Add(GroupsSecretKey.Header, m_key);
    }
}
