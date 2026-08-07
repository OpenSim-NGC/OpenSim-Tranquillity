using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Compiler
{
    public abstract class BaseScope : IScope
    {
        private IScope _enclosingScope;
        private Util.LinkedHashMap<string, Symbol> _symbols = new Util.LinkedHashMap<string, Symbol>();

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

        /// <summary>
        /// Must be reimplemented in child class
        /// </summary>
        public abstract string ScopeName { get; }

        public IEnumerable<Symbol> Symbols
        {
            get
            {
                foreach (Symbol sym in _symbols)
                {
                    yield return sym;
                }
            }
        }

        public BaseScope(IScope parent)
        {
            this._enclosingScope = parent;
        }

        #region IScope Members

        public IScope EnclosingScope
        {
            get { return _enclosingScope; }
        }

        public void Define(Symbol sym)
        {
            _symbols.Add(sym.Name, sym);
            sym.Scope = this;
        }

        public virtual void Define(VariableSymbol varSym)
        {
            this.Define((Symbol)varSym);
            if (!(varSym is ConstantSymbol))
            {
                varSym.ScopeIndex = CurrentVariableIndex++;
            }
        }

        public Symbol Resolve(string name)
        {
            Symbol s;
            if (_symbols.TryGetValue(name, out s))
            {
                return s;
            }
            else if (_enclosingScope != null)
            {
                return _enclosingScope.Resolve(name);
            }
            else
            {
                return null;
            }
        }

        public bool IsDefinedLocally(string name)
        {
            return _symbols.ContainsKey(name);
        }

        public override string ToString()
        {
            StringBuilder syms = new StringBuilder();
            syms.Append(String.Format("Scope:{0}[", ScopeName));

            foreach (Symbol sym in _symbols.Values)
            {
                syms.Append(sym.ToString());
                syms.Append(',');
            }

            syms.Append("]\n");
            return syms.ToString();
        }

        #endregion
    }
}
