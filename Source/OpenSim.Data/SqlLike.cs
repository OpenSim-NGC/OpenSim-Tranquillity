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

namespace OpenSim.Data;

/// <summary>
/// LIKE patterns for text that must match literally, such as an id that came in with a request.
/// </summary>
public static class SqlLike
{
    /// <summary>
    /// The escape character the patterns below use. A query using them must say <c>LIKE ... ESCAPE '!'</c>; it is
    /// not the backslash, which MySQL in NO_BACKSLASH_ESCAPES mode and PostgreSQL treat differently in literals.
    /// </summary>
    public const char Escape = '!';

    /// <summary>
    /// A pattern matching every value that starts with <paramref name="prefix"/>: its <c>%</c>, <c>_</c> and
    /// <c>!</c> are escaped so they match only themselves, then a <c>%</c> is added. An empty prefix matches
    /// every value. Bind the result as a parameter; it is not quoted for use in SQL text.
    /// </summary>
    public static string PrefixPattern(string prefix)
    {
        prefix ??= string.Empty;
        StringBuilder sb = new StringBuilder(prefix.Length + 8);
        foreach (char c in prefix)
        {
            if (c == '%' || c == '_' || c == Escape)
                sb.Append(Escape);
            sb.Append(c);
        }
        sb.Append('%');
        return sb.ToString();
    }
}
