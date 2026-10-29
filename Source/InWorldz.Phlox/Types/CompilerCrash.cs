using System;

namespace InWorldz.Phlox.Types
{
    /// <summary>
    /// Tells a compiler <b>crash</b> apart from a script <b>error</b>.
    ///
    /// <para>
    /// <c>CompilerFrontend.Compile</c> ends in a blanket <c>catch (Exception e)</c> that reported
    /// <c>e.Message</c> through <see cref="ILSLListener"/> and nothing else. So a
    /// <c>NullReferenceException</c> in the type-check pass reached the resident as
    /// <i>"Object reference not set to an instance of an object."</i> — indistinguishable, in the log
    /// and in the owner's dialog, from a message about their own script. Someone was being told their
    /// script was wrong when what actually happened is that the compiler fell over. A
    /// <c>state default;</c> compiler defect went unnoticed in world for a long time partly because of that.
    /// </para>
    ///
    /// <para>
    /// <see cref="ILSLListener"/> carries strings, so the distinction travels as a marked prefix
    /// rather than as a new interface member: the compiler front end has no logger and no dialog
    /// module, and both live on the other side of that interface. The marker is defined here, next to
    /// the interface it rides on, so neither side owns a magic string.
    /// </para>
    /// </summary>
    public static class CompilerCrash
    {
        /// <summary>
        /// The prefix that marks an error as a crash rather than a fault in the script. Deliberately
        /// wordy: if it ever leaks to a resident unformatted it still reads as the compiler's fault.
        /// </summary>
        public const string Marker = "internal compiler error: ";

        /// <summary>
        /// The full record, for the log: marker, exception type, message and stack. The stack is
        /// carried so the region log gets it without the front end needing a logger of its own.
        /// </summary>
        public static string Format(Exception e)
        {
            if (e is null) return Marker + "unknown";
            return Marker + e.GetType().Name + ": " + e.Message + Environment.NewLine + e.StackTrace;
        }

        /// <summary>True when a listener message came from <see cref="Format"/>.</summary>
        public static bool IsCrash(string message)
            => message != null && message.StartsWith(Marker, StringComparison.Ordinal);

        /// <summary>
        /// The exception type out of a formatted message — what the owner is told, and all they are
        /// told. A resident gets a name, never a stack.
        /// </summary>
        public static string TypeNameOf(string message)
        {
            if (!IsCrash(message)) return null;
            string rest = message.Substring(Marker.Length);
            int colon = rest.IndexOf(':');
            string name = colon > 0 ? rest.Substring(0, colon) : rest;
            name = name.Trim();
            return name.Length == 0 ? "Exception" : name;
        }
    }
}
