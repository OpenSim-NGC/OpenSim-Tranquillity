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

using OpenMetaverse;
using OpenSim.Framework;

#pragma warning disable IDE1006

namespace OpenSim.Region.ScriptEngine.Shared.Api
{
    public class NotecardCache
    {
        private static readonly ExpiringCacheOS<UUID, string[]>
            m_Notecards = new ExpiringCacheOS<UUID, string[]>(30000);

        public static void Cache(UUID assetID, byte[] text)
        {
            if (m_Notecards.ContainsKey(assetID, 30000))
                return;

            m_Notecards.AddOrUpdate(assetID, SLUtil.ParseNotecardToArray(text), 30);
        }

        public static bool IsCached(UUID assetID)
        {
            return m_Notecards.ContainsKey(assetID, 30000);
        }

        public static int GetLines(UUID assetID)
        {
            if (m_Notecards.TryGetValue(assetID, 30000, out var text))
                return text.Length;
            return -1;
        }

        /// <summary>
        ///     Get a notecard line.
        /// </summary>
        /// <param name="assetID"></param>
        /// <param name="lineNumber">Lines start at index 0</param>
        /// <returns></returns>
        public static string GetLine(UUID assetID, int lineNumber)
        {
            if (lineNumber >= 0 && m_Notecards.TryGetValue(assetID, 30000, out var text))
            {
                if (lineNumber >= text.Length)
                    return "\n\n\n";
                return text[lineNumber];
            }

            return "";
        }

        /// <summary>
        ///     Get a notecard line.
        /// </summary>
        /// <param name="assetID"></param>
        /// <param name="lineNumber">Lines start at index 0</param>
        /// <param name="maxLength">
        ///     Maximum length of the returned line.
        /// </param>
        /// <returns>
        ///     If the line length is longer than <paramref name="maxLength" />,
        ///     the return string will be truncated.
        /// </returns>
        public static string GetLine(UUID assetID, int lineNumber, int maxLength)
        {
            var line = GetLine(assetID, lineNumber);

            if (line.Length > maxLength)
                return line.Substring(0, maxLength);

            return line;
        }
    }
}