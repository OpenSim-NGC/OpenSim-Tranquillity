/*
 * From HalcyonGrid/phlox (Source/Halcyon.Phlox/Compiler/LocalScope.cs, commit e805a56).
 * Licence: Apache License 2.0, see ThirdPartyLicenses/Phlox.txt.
 * Modified 2026 by Legion Builds.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Compiler
{
    class LocalScope : BaseScope
    {
        public override int CurrentVariableIndex
        {
            get
            {
                return EnclosingScope.CurrentVariableIndex;
            }
            set
            {
                EnclosingScope.CurrentVariableIndex = value;
            }
        }

        public LocalScope(IScope parentScope)
            : base(parentScope)
        {
            
        }

        public override string ScopeName
        {
            get { return "local"; }
        }
    }
}
