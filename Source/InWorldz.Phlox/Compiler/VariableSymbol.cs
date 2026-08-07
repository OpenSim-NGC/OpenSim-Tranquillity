using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Compiler
{
    public class VariableSymbol : Symbol
    {
        public const int INVALID_SCOPE_INDEX = -1;

        private int _scopeIndex = INVALID_SCOPE_INDEX;
        public int ScopeIndex
        {
            get
            {
                return _scopeIndex;
            }

            set
            {
                _scopeIndex = value;
            }
        }

        private bool _isGlobal = false;
        public bool IsGlobal 
        {
            get
            {
                return _isGlobal;
            }

            set
            {
                _isGlobal = value;
            }
        }

        public VariableSymbol(string name, ISymbolType type) : base(name, type)
        {
        }
    }
}
