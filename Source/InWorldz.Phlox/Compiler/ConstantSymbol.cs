/*
 * From HalcyonGrid/phlox (Source/Halcyon.Phlox/Compiler/ConstantSymbol.cs, commit e805a56).
 * Licence: Apache License 2.0, see ThirdPartyLicenses/Phlox.txt.
 * Modified 2026 by Legion Builds.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Compiler
{
    public class ConstantSymbol : VariableSymbol
    {
        private string _constValue;

        public string ConstValue
        {
            get
            {
                return _constValue;
            }
        }

        public string TemplateName
        {
            get
            {
                if (Type == SymbolTable.INT)
                {
                    return "iconst";
                }

                if (Type == SymbolTable.FLOAT)
                {
                    return "fconst";
                }

                if (Type == SymbolTable.STRING)
                {
                    return "syssconst";
                }

                if (Type == SymbolTable.KEY)
                {
                    return "syssconst";
                }

                if (Type == SymbolTable.LIST)
                {
                    return "lconst";
                }

                if (Type == SymbolTable.VECTOR)
                {
                    return "sysvconst";
                }

                if (Type == SymbolTable.ROTATION)
                {
                    return "sysrconst";
                }

                throw new InvalidOperationException("Bad system constant type");
            }
        }

        public ConstantSymbol(string name, ISymbolType type, string value)
            : base(name, type)
        {
            _constValue = value;
        }
    }
}
