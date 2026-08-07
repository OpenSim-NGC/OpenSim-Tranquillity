using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Compiler
{
    public class StateSymbol : ScopedSymbol
    {
        private Util.LinkedHashMap<string, Symbol> _members = new Util.LinkedHashMap<string,Symbol>();

        public string RawName
        {
            get
            {
                return this.Name.Substring(0, Name.Length - 3);
            }
        }

        public StateSymbol(string name, IScope parent)
            : base(name + "(*)", null, parent)
        {

        }

        public override Util.LinkedHashMap<string, Symbol> Members
        {
            get { return _members; }
        }
    }
}
