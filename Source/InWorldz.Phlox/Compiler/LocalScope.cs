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
