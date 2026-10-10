using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ProtoBuf;

namespace InWorldz.Phlox.VM
{
    /// <summary>
    /// A listener that this script has opened
    /// </summary>
    [ProtoContract]
    public class ActiveListen : IEquatable<ActiveListen>
    {
        [ProtoMember(1)]
        public int Handle;

        [ProtoMember(2)]
        public int Channel;

        [ProtoMember(3)]
        public string Name;

        [ProtoMember(4)]
        public string Key;

        [ProtoMember(5)]
        public string Message;

        // Members 6-8 are optional: each is left out of the saved state at its default, so a listen saved without them
        // (by an earlier build, or an llListen listen that is on) loads as it always did, and an earlier build skips them.

        /// <summary>Switched off with llListenControl (or by an osListenRegex pattern that timed out).</summary>
        [ProtoMember(6)]
        public bool Inactive;

        /// <summary>osListenRegex's bitfield (OS_LISTEN_REGEX_NAME 1, OS_LISTEN_REGEX_MESSAGE 2); 0 for llListen.</summary>
        [ProtoMember(7)]
        public int RegexBitfield;

        /// <summary>botListen: the bot whose position the listen hears from; null for a listen of the script's prim.</summary>
        [ProtoMember(8)]
        public string HostKey;

        public ActiveListen()
        {
        }

        #region IEquatable<ActiveListen> Members

        public bool Equals(ActiveListen other)
        {
            return this.Handle == other.Handle && this.Channel == other.Channel && this.Name == other.Name &&
                this.Key == other.Key && this.Message == other.Message && this.Inactive == other.Inactive &&
                this.RegexBitfield == other.RegexBitfield && this.HostKey == other.HostKey;
        }

        public override bool Equals(object obj)
        {
            ActiveListen other = obj as ActiveListen;
            if (obj == null)
            {
                return false;
            }

            return this.Equals(other);
        }

        public override int GetHashCode()
        {
            int hash = 17;

            hash = hash * 23 + Handle.GetHashCode();
            hash = hash * 23 + Channel.GetHashCode();
            hash = hash * 23 + Name.GetHashCode();
            hash = hash * 23 + Key.GetHashCode();
            hash = hash * 23 + Message.GetHashCode();
            hash = hash * 23 + Inactive.GetHashCode();
            hash = hash * 23 + RegexBitfield.GetHashCode();
            hash = hash * 23 + (HostKey?.GetHashCode() ?? 0);

            return hash;
        }

        #endregion
    }
}
