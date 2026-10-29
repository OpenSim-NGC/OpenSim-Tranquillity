using System;
using Antlr4.Runtime;

namespace InWorldz.Phlox.Compiler
{
    /// <summary>
    /// In ANTLR3 this was a custom error node. In ANTLR4 error handling is done
    /// via error listeners and error recovery strategies on the parser itself.
    /// This class is kept as a stub for compilation compatibility during migration.
    /// It will be removed once CompilerFrontend is fully rewritten for ANTLR4.
    /// </summary>
    [Obsolete("ANTLR4 handles errors via IAntlrErrorListener. This stub exists only for migration compatibility.")]
    public class LSLErrorNode : LSLAst
    {
        public LSLErrorNode(ITokenStream input, IToken start, IToken stop, RecognitionException e)
        {
        }
    }
}
