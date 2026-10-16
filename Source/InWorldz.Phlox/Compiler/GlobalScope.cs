/*
 * From HalcyonGrid/phlox (Source/Halcyon.Phlox/Compiler/GlobalScope.cs, commit e805a56).
 * Licence: Apache License 2.0, see ThirdPartyLicenses/Phlox.txt.
 * Modified 2026 by Legion Builds.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Compiler
{
    public class GlobalScope : BaseScope
    {
        public override string ScopeName
        {
            get
            {
                return "global";
            }
        }

        public GlobalScope() : base(null)
        {
        }

        public override void Define(VariableSymbol varSym)
        {
            varSym.IsGlobal = true;
            base.Define(varSym);
        }
    }
}
