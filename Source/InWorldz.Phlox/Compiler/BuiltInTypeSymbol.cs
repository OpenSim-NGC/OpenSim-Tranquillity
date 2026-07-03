using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Compiler
{
    public class BuiltInTypeSymbol : Symbol, ISymbolType
    {
        private int _typeIndex;
        
        public int TypeIndex
        {
            get { return _typeIndex; }
        }


        public BuiltInTypeSymbol(string name, int typeIndex) : base(name)
        {
            this._typeIndex = typeIndex;
        }

        public override string ToString()
        {
            return this.Name;
        }
    }
}
