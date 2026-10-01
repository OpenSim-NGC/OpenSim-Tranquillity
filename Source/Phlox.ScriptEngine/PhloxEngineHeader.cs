/*
 * Phlox Script Engine Integration
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
