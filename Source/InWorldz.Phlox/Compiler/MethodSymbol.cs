using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Compiler
{
    public class MethodSymbol : ScopedSymbol
    {
        private Util.LinkedHashMap<string, Symbol> _locals = new Util.LinkedHashMap<string, Symbol>();
        private bool _isSyscall = false;
        public bool IsSyscall 
        {
            get
            {
                return _isSyscall;
            }

            set
            {
                _isSyscall = value;
            }
        }

        public MethodSymbol(string name, ISymbolType retType, IScope parent) : base(name, retType, parent)
        {

        }

        public override Util.LinkedHashMap<string, Symbol> Members
        {
            get { return _locals; }
        }

        public string RawName
        {
            get
            {
                return base.Name;
            }
        }

        public override string Name
        {
            get
            {
                return base.Name + "()";
            }
        }

        
    }
}
