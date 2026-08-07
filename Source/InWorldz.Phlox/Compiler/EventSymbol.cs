using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using InWorldz.Phlox.Types;

namespace InWorldz.Phlox.Compiler
{
    public class EventSymbol : ScopedSymbol
    {
        private Util.LinkedHashMap<string, Symbol> _locals = new Util.LinkedHashMap<string, Symbol>();

        public override string Name
        {
            get
            {
                return base.Name + "()";
            }
        }

        public string FullEventName
        {
            get
            {
                return ((StateSymbol)this.EnclosingScope).RawName + "/" + base.Name;
            }
        }

        public EventSymbol(string name, IScope parent)
            : base(name, SymbolTable.VOID, parent)
        {

        }

        public override Util.LinkedHashMap<string, Symbol> Members
        {
            get { return _locals; }
        }

        public List<VarType> ExtractArgumentTypes()
        {
            List<VarType> types = new List<VarType>();
            foreach (Symbol sym in _locals)
            {
                types.Add((VarType)sym.Type.TypeIndex);
            }

            return types;
        }
    }
}
