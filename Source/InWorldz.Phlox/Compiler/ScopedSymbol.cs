using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Compiler
{
    public abstract class ScopedSymbol : Symbol, IScope
    {
        IScope _enclosingScope;

        public abstract Util.LinkedHashMap<string, Symbol> Members { get; }

        public IEnumerable<Symbol> Symbols
        {
            get
            {
                foreach (Symbol sym in Members)
                {
                    yield return sym;
                }
            }
        }

        public IScope EnclosingScope
        {
            get
            {
                return _enclosingScope;
            }
        }

        public string ScopeName
        {
            get
            {
                return Name;
            }
        }

        private int _currentVarIndex = 0;

        /// <summary>
        /// Holds the current variable index for this scope. Can be overridden 
        /// by child classes to provide different behaviours such as the case for
        /// local scope which does not hold it's own indexes
        /// </summary>
        public virtual int CurrentVariableIndex
        {
            get
            {
                return _currentVarIndex;
            }

            set
            {
                _currentVarIndex = value;
            }
        }

        public ScopedSymbol(string name, ISymbolType type, IScope enclosingScope) : base(name, type)
        {
            this._enclosingScope = enclosingScope;
        }

        public ScopedSymbol(string name, IScope enclosingScope) : base(name)
        {
            this._enclosingScope = enclosingScope;
        }

        public Symbol Resolve(string name)
        {
            Symbol s;
            if (this.Members.TryGetValue(name, out s))
            {
                return s;
            }
            else if (EnclosingScope != null)
            {
                return EnclosingScope.Resolve(name);
            }
            else
            {
                return null;
            }
        }

        public Symbol ResolveType(string name)
        {
            return this.Resolve(name);
        }

        public void Define(Symbol sym)
        {
            this.Members.Add(sym.Name, sym);
            sym.Scope = this;
        }

        public void Define(VariableSymbol sym)
        {
            this.Define((Symbol)sym);

            if (!(sym is ConstantSymbol))
            {
                sym.ScopeIndex = CurrentVariableIndex++;
            }
        }

        public bool IsDefinedLocally(string name)
        {
            return this.Members.ContainsKey(name);
        }
    }
}
