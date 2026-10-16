/*
 * From HalcyonGrid/phlox (Source/Halcyon.Phlox/Compiler/LSLTreeAdaptor.cs, commit e805a56).
 * Licence: Apache License 2.0, see ThirdPartyLicenses/Phlox.txt.
 * Modified 2026 by Legion Builds.
 */

using System;

namespace InWorldz.Phlox.Compiler
{
    /// <summary>
    /// In ANTLR3 this controlled how AST nodes were created. In ANTLR4 the parse tree
    /// is built automatically and cannot be customized via an adaptor.
    /// This class is kept as a stub for compilation compatibility during migration.
    /// It will be removed once CompilerFrontend is fully rewritten for ANTLR4.
    /// </summary>
    [Obsolete("ANTLR4 builds the parse tree automatically. This stub exists only for migration compatibility.")]
    public class LSLTreeAdaptor
    {
        public LSLTreeAdaptor() { }
    }
}
