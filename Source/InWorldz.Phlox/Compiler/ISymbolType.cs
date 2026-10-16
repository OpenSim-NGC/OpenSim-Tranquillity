/*
 * From HalcyonGrid/phlox (Source/Halcyon.Phlox/Compiler/ISymbolType.cs, commit e805a56).
 * Licence: Apache License 2.0, see ThirdPartyLicenses/Phlox.txt.
 * Modified 2026 by Legion Builds.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Compiler
{
    public interface ISymbolType
    {
        string Name { get; }
        int TypeIndex { get; }
    }
}
