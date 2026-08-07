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
