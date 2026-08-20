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

using System;

namespace OpenSim.Framework.TrustedHypergrid;

/// <summary>
/// The single, shared home-URI canonicaliser for the Trusted Hypergrid registry
/// (Design Brief §4). It MUST be used on both the write and the lookup side of every
/// URI-keyed operation. Recon R6 established that ad-hoc string comparison of home URIs
/// is the existing defect; this function is the fix and must not be worked around.
///
/// Canonical form: lowercase scheme and host, an explicit port (the scheme default is
/// made explicit when none was given), and a single trailing slash. Thus
/// <c>http://Grid.Example:80/</c> and <c>http://grid.example/</c> both collapse to
/// <c>http://grid.example:80/</c>, while <c>https://grid.example/</c> normalises to
/// <c>https://grid.example:443/</c> and stays distinct.
/// </summary>
public static class HGUriNormalizer
{
    /// <summary>
    /// Normalise a home/alias URI to its canonical registry form.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The input is null, blank, or not a well-formed absolute URI.
    /// </exception>
    public static string Normalize(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            throw new ArgumentException("home URI must not be null or empty", nameof(uri));

        if (!Uri.TryCreate(uri.Trim(), UriKind.Absolute, out Uri parsed))
            throw new ArgumentException($"'{uri}' is not an absolute URI", nameof(uri));

        string scheme = parsed.Scheme.ToLowerInvariant();
        string host = parsed.Host.ToLowerInvariant();
        // Uri.Port yields the scheme's default when the authority omitted a port,
        // which is exactly the "explicit port" the brief requires.
        int port = parsed.Port;

        // Preserve any path but guarantee exactly one trailing slash so that
        // ".../" and "..." (and "...//") converge.
        string path = parsed.AbsolutePath.TrimEnd('/');

        return $"{scheme}://{host}:{port}{path}/";
    }
}
