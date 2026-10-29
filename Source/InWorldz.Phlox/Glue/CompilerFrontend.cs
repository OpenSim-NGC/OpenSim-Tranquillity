using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Antlr4.Runtime;
using Antlr4.Runtime.Atn;
using Antlr4.Runtime.Misc;
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
        /// pasted from web browsers or chat clients. String literals and comments
        /// are copied unchanged: SL keeps a string's contents verbatim. They are
        /// found as the lexer finds them (LSL.g4 STRING_LITERAL, COMMENT_SINGLE,
        /// COMMENT_BLOCK): inside a string a backslash escapes exactly the next
        /// character, and a quote inside a comment starts nothing.
        /// </summary>
        internal static string SanitizeScript(string src)
        {
            var sb = new System.Text.StringBuilder(src.Length);
            int i = 0, n = src.Length;
            while (i < n)
            {
                char c = src[i];
                int start = i;

                if (c == '"')
                {
                    i++;
                    while (i < n && src[i] != '"')
                        i += src[i] == '\\' && i + 1 < n ? 2 : 1;
                    i = Math.Min(i + 1, n);
                    sb.Append(src, start, i - start);
                    continue;
                }

                if (c == '/' && i + 1 < n && src[i + 1] == '/')
                {
                    i += 2;
                    while (i < n && src[i] != '\r' && src[i] != '\n') i++;
                    sb.Append(src, start, i - start);
                    continue;
                }

                if (c == '/' && i + 1 < n && src[i + 1] == '*')
                {
                    int end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? n : end + 2;
                    sb.Append(src, start, i - start);
                    continue;
                }

                // Outside strings and comments: keep only printable ASCII + common whitespace
                if (c == '\t' || c == '\n' || c == '\r' || (c >= 0x20 && c <= 0x7E))
                    sb.Append(c);
                // else: silently drop the character
                i++;
            }
            return sb.ToString();
        }

        /// <summary>
        /// A linear pre-pass over the tokens that bounds brace depth before the parser runs. The grammar's
        /// `statement : funcBlock | funcBlockContent` (whose anonBlock is also a funcBlock) is ambiguous for every
        /// '{', so prediction reads ahead to the matching '}' - the rest of the script - once per level, before the
        /// counted limit can trip: a 200,000-deep script cost 501 scans of ~6 MB. Every counted block adds at most
        /// one brace on top of the state's and the event's, so a brace depth past Block + 2 is always past the
        /// block limit, and is reported as that limit.
        /// </summary>
        private static void CheckBraceDepth(CommonTokenStream tokens)
        {
            tokens.Fill();
            int depth = 0;
            foreach (IToken t in tokens.GetTokens())
            {
                if (t.Channel != TokenConstants.DefaultChannel) continue;
                if (t.Text == "{")
                {
                    if (++depth > NestingLimits.Block + 2) throw new NestingTooDeepException(t.Line, t.Column, NestingKind.Block);
                }
                else if (t.Text == "}" && depth > 0) depth--;
            }
            tokens.Seek(0);
        }

        public VM.CompiledScript Compile(ICharStream input)
        {
            try
            {
                // --------------------------------------------------------
                // Phase 1: Lex and Parse
                // --------------------------------------------------------
                LSLLexer lexer = new LSLLexer(input);
                // A character the lexer does not recognise is a syntax error, as in SL. Halcyon passed lexer
                // diagnostics to the compile listener (its LSLListenerTraceRedirector); ANTLR's default listener
                // only printed them to the console and the lexer skipped the character.
                LslLexerErrorListener lexerErrors = new LslLexerErrorListener(_listener);
                lexer.RemoveErrorListeners();
                lexer.AddErrorListener(lexerErrors);
                CommonTokenStream tokens = new CommonTokenStream(lexer);

                // Two-stage parse. Full-context (LL) prediction resolves the grammar's dangling
                // 'else' and its assignment chains by walking the WHOLE parser stack at every decision: the
                // cost grew with the square of the nesting (500 nested ifs: 8.7 s; 200 chained assignments:
                // 7.1 s) and its recursion (ParserATNSimulator.Closure_) overflowed the stack at a 10,000-branch
                // else-if chain, below every guard. SLL prediction does not look at the outer stack, and ANTLR
                // guarantees an SLL parse that succeeds is the tree LL would build. A script SLL rejects is
                // parsed again with LL, so a real syntax error is reported exactly as before.
                LslErrorListener errorListener = new LslErrorListener(_listener);
                CheckBraceDepth(tokens);
                LSLParser.ProgContext tree = null;
                {
                    LSLParser sll = new LSLParser(tokens);
                    sll.Interpreter.PredictionMode = PredictionMode.SLL;
                    sll.RemoveErrorListeners();
                    sll.ErrorHandler = new BailErrorStrategy();
                    try { tree = sll.prog(); }
                    catch (ParseCanceledException) { tree = null; }
                }
                if (tree == null)
                {
                    tokens.Seek(0);
                    LSLParser parser = new LSLParser(tokens);
                    parser.Interpreter.PredictionMode = PredictionMode.LL;
                    parser.RemoveErrorListeners();
                    parser.AddErrorListener(errorListener);
                    tree = parser.prog();
                }

                if (errorListener.ErrorCount > 0 || lexerErrors.ErrorCount > 0)
                {
                    _listener.Error(errorListener.ErrorCount + lexerErrors.ErrorCount + " syntax error(s)");
                    return null;
                }

                // --------------------------------------------------------
                // Phase 2: Symbol definition pass (replaces Def tree grammar)
                // --------------------------------------------------------
                LSLNodeAnnotations annotations = new LSLNodeAnnotations();
                SymbolTable symtab = new SymbolTable(tokens, Defaults.AllMethods, DefaultConstants.Constants.Values);
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
                                b.Node.Column + " " + b.Node.Text +
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

                BytecodeGenerator bcgen = new BytecodeGenerator(Defaults.AllMethods);
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
            catch (NestingTooDeepException e)
            {
                // A script nested past what the stack can walk is the script's error.
                // Normally a counted limit, whose message names it ("... (limit N)").
                _listener.Error($"line {e.Line}:{e.Column} {e.Message}");
            }
            catch (TooManyErrorsException e)
            {
                _listener.Error(string.Format("Too many errors {0}", e.InnerException?.Message));
            }
            catch (Exception e)
            {
                // This used to report e.Message and nothing else, so a compiler crash
                // was indistinguishable from a fault in the script - in the log and in the
                // owner's dialog alike. CompilerCrash.Format marks it and carries the type and
                // stack, which the listener logs and the owner-visible path deliberately does not
                // repeat back to the resident.
                _listener.Error(Types.CompilerCrash.Format(e));
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

            BytecodeGenerator bcgen = new BytecodeGenerator(Defaults.AllMethods);
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

    /// <summary>
    /// The lexer's errors, routed to the LSL listener like the parser's. Every one is counted; the first ten are
    /// reported, the rest only counted.
    /// </summary>
    internal class LslLexerErrorListener : IAntlrErrorListener<int>
    {
        private readonly ILSLListener _listener;
        public int ErrorCount { get; private set; }

        public LslLexerErrorListener(ILSLListener listener)
        {
            _listener = listener;
        }

        public void SyntaxError(
            System.IO.TextWriter output,
            IRecognizer recognizer,
            int offendingSymbol,
            int line,
            int charPositionInLine,
            string msg,
            RecognitionException e)
        {
            if (++ErrorCount <= 10)
                _listener?.Error($"line {line}:{charPositionInLine} {msg}");
        }
    }
}
