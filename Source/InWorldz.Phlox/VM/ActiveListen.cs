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


        public ActiveListen()
        {
        }

        #region IEquatable<ActiveListen> Members

        public bool Equals(ActiveListen other)
        {
            return this.Handle == other.Handle && this.Channel == other.Channel && this.Name == other.Name &&
                this.Key == other.Key && this.Message == other.Message;
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

            return hash;
        }

        #endregion
    }
}
