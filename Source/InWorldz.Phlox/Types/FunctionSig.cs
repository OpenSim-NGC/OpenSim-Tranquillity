/*
 * From HalcyonGrid/phlox (Source/Halcyon.Phlox/Types/FunctionSig.cs, commit e805a56).
 * Licence: Apache License 2.0, see ThirdPartyLicenses/Phlox.txt.
 * Modified 2026 by Legion Builds.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Types
{
    /// <summary>
    /// Signature of a function or event
    /// </summary>
    public struct FunctionSig
    {
        public string FunctionName;
        public VarType ReturnType;
        public VarType[] ParamTypes;
        public string[] ParamNames;
        public int TableIndex;
    }
}
