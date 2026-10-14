/*
 * From HalcyonGrid/phlox (Source/Halcyon.Phlox/Compiler/LabelSymbol.cs, commit e805a56).
 * Licence: Apache License 2.0, see ThirdPartyLicenses/Phlox.txt.
 * Modified 2026 by Legion Builds.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Compiler
{
    public class LabelSymbol : Symbol
    {
        private int _id;
        public override string Name
        {
            get
            {
                return "@" + base.Name;
            }
        }

        public string DecoratedName
        {
            get
            {
                return base.Name + "_usrlbl__" + _id;
            }
        }

        public string RawName
        {
            get
            {
                return base.Name;
            }
        }

        public int UniqueId
        {
            get
            {
                return _id;
            }
        }

        public LabelSymbol(string name, int id)
            : base(name, SymbolTable.VOID)
        {
            _id = id;
        }
    }
}
