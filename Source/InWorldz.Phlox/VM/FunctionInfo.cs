/*
 * From HalcyonGrid/phlox (Source/Halcyon.Phlox/VM/FunctionInfo.cs, commit e805a56).
 * Licence: Apache License 2.0, see ThirdPartyLicenses/Phlox.txt.
 * Modified 2026 by Legion Builds.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ProtoBuf;

namespace InWorldz.Phlox.VM
{
    [ProtoContract]
    public class FunctionInfo
    {
        //an approximation of the size of this struct
        public const int MemSize = 48;

        [ProtoMember(1)]
        public string Name;

        [ProtoMember(2)]
        public int NumberOfArguments;

        [ProtoMember(3)]
        public int NumberOfLocals;

        [ProtoMember(4)]
        public int Address;


        public FunctionInfo()
        {
        }
    }
}
