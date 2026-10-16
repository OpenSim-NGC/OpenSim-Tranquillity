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

// based on XMREngine from Mike Rieker (DreamNation), Melanie Thielker and meta7
// but with several changes to be more cross platform.

/*
 * Phlox Script Engine Integration
 *
 * Adapted 2026 by Legion Builds
 */

using System.Collections.Generic;

namespace Phlox.ScriptEngine
{
    /// <summary>
    /// Which engine a script belongs to, by the first-line header ("//&lt;engine&gt;:&lt;language&gt;") exactly as
    /// YEngine reads it (XMREngine.OnRezScript). Every engine of a region receives every rez with the region's default
    /// engine name, and each decides for itself; with both engines deciding by this one rule, a script runs in one.
    /// </summary>
    internal static class PhloxEngineHeader
    {
        /// <summary>
        /// The engine the script's first line names, or null. YEngine's parse, call for call: "//" at the very start, a
        /// first line ending at a '\n' past index 5, trimmed; the first ':' at index 3 or more; the name before it with
        /// trailing blanks trimmed. The comparison with engine names is ordinal (case matters).
        /// </summary>
        internal static string NamedEngine(string script)
        {
            if (script is null || !script.StartsWith("//"))
                return null;
            int lineEnd = script.IndexOf('\n');
            if (lineEnd <= 5)
                return null;
            string firstline = script[2..lineEnd].Trim();
            int colon = firstline.IndexOf(':');
            if (colon < 3)
                return null;
            string name = firstline[..colon].TrimEnd();
            return string.IsNullOrEmpty(name) ? null : name;
        }

        /// <summary>Phlox's engine name (PhloxEngine.Name), the name its own header carries.</summary>
        internal const string PhloxName = "InWorldz.Phlox";

        /// <summary>
        /// The language part of the first line ("//&lt;engine&gt;:&lt;language&gt;"), or "" for none. YEngine's parse, call for
        /// call (XMREngine.OnRezScript): the text after the first ':' when at least two characters follow it, trimmed and
        /// lowercased; a shorter one is ignored. YEngine runs "" and "lsl" and declines anything else; Phlox reads its own
        /// header the same way and adds "slua" (<see cref="PhloxScriptLoader.CompileByLanguage"/>).
        /// </summary>
        internal static string Language(string script)
        {
            if (script is null || !script.StartsWith("//"))
                return "";
            int lineEnd = script.IndexOf('\n');
            if (lineEnd <= 5)
                return "";
            string firstline = script[2..lineEnd].Trim();
            int colon = firstline.IndexOf(':');
            if (colon > 0 && colon < firstline.Length - 2)
                return firstline[(colon + 1)..].Trim().ToLower();
            return "";
        }

        /// <summary>
        /// The engine that runs the script: the engine its header names when that engine is loaded in the region, otherwise
        /// the default engine (a header naming an engine that is not loaded falls to the default, as in YEngine).
        /// </summary>
        internal static string Owner(string script, string defaultEngine, IEnumerable<string> loadedEngines)
        {
            string named = NamedEngine(script);
            if (named is not null)
            {
                foreach (string loaded in loadedEngines)
                {
                    if (loaded == named)
                        return named;
                }
            }
            return defaultEngine;
        }
    }
}
