using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Compiler
{
    /// <summary>
    /// A variable scope
    /// </summary>
    public interface IScope
    {
        /// <summary>
        /// Returns the name of the scope
        /// </summary>
        string ScopeName { get; }

        /// <summary>
        /// Where to look next for a symbol if it is not in this scope
        /// </summary>
        IScope EnclosingScope { get; }

        /// <summary>
        /// Returns all symbols defined locally in this scope
        /// </summary>
        IEnumerable<Symbol> Symbols { get; }

        /// <summary>
        /// Returns the current variable index for this local scope
        /// </summary>
        int CurrentVariableIndex { get; set; }

        /// <summary>
        /// Defines a symbol in this scope
        /// </summary>
        /// <param name="sym">The symbol to define</param>
        void Define(Symbol sym);

        /// <summary>
        /// Defines a variable symbol in this scope
        /// </summary>
        /// <param name="sym">The symbol to define</param>
        void Define(VariableSymbol sym);

        /// <summary>
        /// Look up name in this scope or in enclosing scope if not here
        /// </summary>
        /// <param name="name">The name of the symbol to find</param>
        /// <returns>The symbol found</returns>
        Symbol Resolve(string name);

        /// <summary>
        /// Returns whether or not the given id is defined locally
        /// </summary>
        /// <param name="name">Name of the symbol</param>
        /// <returns>true if the symbol is already defined locally</returns>
        bool IsDefinedLocally(string name);
    }
}
