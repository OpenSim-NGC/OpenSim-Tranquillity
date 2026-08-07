using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ProtoBuf;

namespace InWorldz.Phlox.Types
{
    /// <summary>
    /// Represents a sentinal value that can be used for testing
    /// </summary>
    [ProtoContract]
    public class Sentinel
    {
        public readonly static Sentinel Instance = new Sentinel();

        [ProtoMember(1, IsRequired = false)]
        public int SentinalValue = 0xbadf00d;
    }
}
