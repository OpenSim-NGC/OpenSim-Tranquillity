using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Compiler
{
    public class Symbol
    {
        private string _name;
        private ISymbolType _type;
        private IScope _scope;
        private LSLAst _def;

        public ISymbolType Type
        {
            get
            {
                return _type;
            }
        }

        public virtual string Name
        {
            get
            {
                return _name;
            }
        }

        public IScope Scope 
        {
            get
            {
                return _scope;
            }

            set
            {
                _scope = value;
            }
        }

        public LSLAst Def
        {
            get
            {
                return _def;
            }

            set
            {
                _def = value;
            }
        }

        public Symbol(string name)
        {
            this._name = name;
        }

        public Symbol(string name, ISymbolType type)
        {
            this._name = name;
            this._type = type;
        }

        public override string ToString()
        {
            string s = "";
            if (_scope != null) s = _scope.ScopeName + ".";
            if (_type != null) return String.Format("<{0}{1}:{2}>", s, Name, _type);

            return s + Name;
        }
    }
}
