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
