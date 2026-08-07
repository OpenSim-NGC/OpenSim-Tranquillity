using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using Antlr4.StringTemplate;

using InWorldz.Phlox.Types;
using InWorldz.Phlox.Compiler;
using InWorldz.Phlox.ByteCompiler;
using InWorldz.Phlox.Compiler.BranchAnalyze;

namespace InWorldz.Phlox.Glue
{
    /// <summary>
    /// Provides a frontend to compile LSL input through the full Phlox pipeline.
    /// 
    /// ANTLR4 migration notes:
    ///   - ANTLRStringStream       -> AntlrInputStream
    ///   - CommonTokenStream       -> CommonTokenStream (same name, different namespace)
    ///   - LSLParser.prog_return   -> LSLParser.ProgContext
    ///   - CommonTree / AST        -> IParseTree (parse tree, not AST)
    ///   - CommonTreeNodeStream    -> removed; visitors walk IParseTree directly
    ///   - LSLTreeAdaptor          -> removed; ANTLR4 builds parse tree automatically
    ///   - Def/Types/Analyze/Gen   -> now visitors that walk LSLParser.ProgContext
    ///   - Antlr3.ST               -> Antlr.StringTemplate (StringTemplate4 NuGet)
    /// </summary>
    public class CompilerFrontend
    {
        private ILSLListener _listener;
        private LSLListenerTraceRedirector _traceRedirect;
        private string _templatePath;
        private bool _byteCodeDebugging = false;
        private string _byteCode;
        private bool _outputAstGraph = false;

        public bool OutputASTGraph
        {
            get { return _outputAstGraph; }
            set { _outputAstGraph = value; }
        }

        public ILSLListener Listener
        {
            get { return _listener; }
        }

        public string GeneratedByteCode
        {
            get { return _byteCode; }
        }

        public CompilerFrontend(ILSLListener listener, string templatePath)
        {
            _listener = listener;
            _traceRedirect = new LSLListenerTraceRedirector(listener);
            _templatePath = templatePath;
        }

        public CompilerFrontend(ILSLListener listener, string templatePath, bool byteCodeDebugging)
        {
            _listener = listener;
            _traceRedirect = new LSLListenerTraceRedirector(listener);
            _templatePath = templatePath;
            _byteCodeDebugging = byteCodeDebugging;
        }

        public VM.CompiledScript Compile(string input)
        {
            // Sanitize pasted script text: strip non-ASCII characters outside of
            // string literals to prevent invisible Unicode from producing unknownop errors.
            input = SanitizeScript(input);
            return this.Compile(new AntlrInputStream(input));
        }

        /// <summary>
        /// Strip invisible/non-ASCII characters that can appear when scripts are
        /// pasted from web browsers or chat clients. Characters inside string
        /// literals (between unescaped double-quotes) are preserved.
        /// </summary>
        private static string SanitizeScript(string src)
        {
            var sb = new System.Text.StringBuilder(src.Length);
            bool inString = false;
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];

                // Track string literal boundaries (handle escaped quotes)
                if (c == '"' && (i == 0 || src[i - 1] != '\\'))
                {
                    inString = !inString;
                    sb.Append(c);
                    continue;
                }

                if (inString)
                {
                    sb.Append(c);
                    continue;
                }

                // Outside strings: keep only printable ASCII + common whitespace
                if (c == '\t' || c == '\n' || c == '\r' || (c >= 0x20 && c <= 0x7E))
                    sb.Append(c);
                // else: silently drop the character
            }
            return sb.ToString();
        }

        public VM.CompiledScript Compile(ICharStream input)
        {
            try
            {
                // --------------------------------------------------------
                // Phase 1: Lex and Parse
                // --------------------------------------------------------
                LSLLexer lexer = new LSLLexer(input);
                CommonTokenStream tokens = new CommonTokenStream(lexer);
                LSLParser parser = new LSLParser(tokens);

                // Wire up error listener
                LslErrorListener errorListener = new LslErrorListener(_listener);
                parser.RemoveErrorListeners();
                parser.AddErrorListener(errorListener);
 

                LSLParser.ProgContext tree = parser.prog();

                if (errorListener.ErrorCount > 0)
                {
                    _listener.Error(errorListener.ErrorCount + " syntax error(s)");
                    return null;
                }

                // --------------------------------------------------------
                // Phase 2: Symbol definition pass (replaces Def tree grammar)
                // --------------------------------------------------------
                LSLNodeAnnotations annotations = new LSLNodeAnnotations();
                SymbolTable symtab = new SymbolTable(tokens, Defaults.SystemMethods.Values, DefaultConstants.Constants.Values);
                symtab.StatusListener = _listener;

                DefVisitor def = new DefVisitor(symtab, annotations);
                def.Visit(tree);

                if (_listener.HasErrors())
                    return null;

                // --------------------------------------------------------
                // Phase 3: Type checking pass (replaces Types tree grammar)
                // --------------------------------------------------------
                TypesVisitor types = new TypesVisitor(symtab, annotations);
                types.Visit(tree);

                if (_listener.HasErrors())
                    return null;

                // --------------------------------------------------------
                // Phase 4: Branch/return analysis (replaces Analyze tree grammar)
                // --------------------------------------------------------
                AnalyzeVisitor analyze = new AnalyzeVisitor(symtab, annotations);
                analyze.Visit(tree);

                foreach (FunctionBranch b in analyze.FunctionBranches.Where(pred => pred.Type != null))
                {
                    if (!b.AllCodePathsReturn())
                    {
                        if (_listener != null)
                        {
                            _listener.Error("line: " + b.Node.Line + ":" +
                                b.Node.CharPositionInLine + " " + b.Node.Text +
                                "(): Not all control paths return a value");
                        }
                    }
                }

                if (_listener.HasErrors())
                    return null;

                // --------------------------------------------------------
                // Phase 5: Code generation (replaces Gen tree grammar)
                // --------------------------------------------------------
				
				TemplateGroup templates = null; // ByteCodeEmitter used instead of STG	

                GenVisitor gen = new GenVisitor(symtab, annotations, templates);
                string bytecodeText = gen.Generate(tree);

                if (_listener.HasErrors())
                    return null;

                if (string.IsNullOrEmpty(bytecodeText))
                    return null;

                if (_byteCodeDebugging)
                    _byteCode = bytecodeText;

                // --------------------------------------------------------
                // Phase 6: Bytecode assembly
                // --------------------------------------------------------
                AntlrInputStream asmInput = new AntlrInputStream(bytecodeText);
                AssemblerLexer asmLexer = new AssemblerLexer(asmInput);
                CommonTokenStream asmTokens = new CommonTokenStream(asmLexer);
                AssemblerParser asmParser = new AssemblerParser(asmTokens);

                LslErrorListener asmErrorListener = new LslErrorListener(_listener);
                asmParser.RemoveErrorListeners();
                asmParser.AddErrorListener(asmErrorListener);

                BytecodeGenerator bcgen = new BytecodeGenerator(Defaults.SystemMethods.Values);
                asmParser.SetGenerator(bcgen);

                try
                {
                    asmParser.program();

                    if (asmErrorListener.ErrorCount > 0)
                    {
                        _listener.Error(asmErrorListener.ErrorCount + " bytecode generation error(s)");
                        return null;
                    }

                    return bcgen.Result;
                }
                catch (GenerationException e)
                {
                    _listener.Error(e.Message);
                }

                return null;
            }
            catch (TooManyErrorsException e)
            {
                _listener.Error(string.Format("Too many errors {0}", e.InnerException?.Message));
            }
            catch (Exception e)
            {
                _listener.Error(e.Message);
            }

            return null;
        }

        /// <summary>
        /// Assemble Phlox assembly text directly into a CompiledScript, reusing the existing,
        /// tested assembler stage (AssemblerParser + BytecodeGenerator) that Compile() runs after
        /// GenVisitor. Exposed so a non-LSL front-end (e.g. a future SLua/Luau front-end) — and the
        /// SLua Tier-1 back-half proof — can produce a CompiledScript from assembly text with no new
        /// assembly logic. Additive: does not alter the LSL Compile() path.
        /// </summary>
        public VM.CompiledScript AssembleText(string bytecodeText)
        {
            if (string.IsNullOrEmpty(bytecodeText)) return null;

            AntlrInputStream asmInput = new AntlrInputStream(bytecodeText);
            AssemblerLexer asmLexer = new AssemblerLexer(asmInput);
            CommonTokenStream asmTokens = new CommonTokenStream(asmLexer);
            AssemblerParser asmParser = new AssemblerParser(asmTokens);

            LslErrorListener asmErrorListener = new LslErrorListener(_listener);
            asmParser.RemoveErrorListeners();
            asmParser.AddErrorListener(asmErrorListener);

            BytecodeGenerator bcgen = new BytecodeGenerator(Defaults.SystemMethods.Values);
            asmParser.SetGenerator(bcgen);

            try
            {
                asmParser.program();
                if (asmErrorListener.ErrorCount > 0)
                {
                    _listener.Error(asmErrorListener.ErrorCount + " bytecode generation error(s)");
                    return null;
                }
                return bcgen.Result;
            }
            catch (GenerationException e)
            {
                _listener.Error(e.Message);
            }
            return null;
        }

        /// <summary>
        /// Compile an SLua (Luau-dialect) script through the parallel SLua Tier-1 front-end into a
        /// CompiledScript: SLua source -> Phlox assembly text -> existing assembler (AssembleText).
        /// Additive; the LSL Compile() path is untouched. Returns null on error (reported to the
        /// listener). Route to this via PhloxScriptLoader when SLuaCompiler.IsLuaScript() is true.
        /// </summary>
        public VM.CompiledScript CompileLua(string input)
        {
            string asm = InWorldz.Phlox.SLua.SLuaCompiler.CompileToAssembly(input, _listener);
            if (string.IsNullOrEmpty(asm))
                return null;
            if (_byteCodeDebugging)
                _byteCode = asm;
            return AssembleText(asm);
        }
    }

    /// <summary>
    /// ANTLR4 error listener that routes errors to the LSL listener interface.
    /// Replaces the override of Recover() that was used in ANTLR3.
    /// </summary>
    internal class LslErrorListener : BaseErrorListener
    {
        private readonly ILSLListener _listener;
        public int ErrorCount { get; private set; }

        public LslErrorListener(ILSLListener listener)
        {
            _listener = listener;
        }

        public override void SyntaxError(
            System.IO.TextWriter output,
            IRecognizer recognizer,
            IToken offendingSymbol,
            int line,
            int charPositionInLine,
            string msg,
            RecognitionException e)
        {
            ErrorCount++;
            if (ErrorCount >= 10)
                throw new TooManyErrorsException("Too many errors", e);

            _listener?.Error($"line {line}:{charPositionInLine} {msg}");
        }
    }
}
