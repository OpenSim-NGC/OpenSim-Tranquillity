/*
 * SLua Tier-1 front-end (ADDITIVE, parallel to the LSL front-end).
 *
 * Compiles the TRIVIAL SLua subset (SL's Luau dialect) to Phlox ASSEMBLY TEXT, which the proven
 * back-half (CompilerFrontend.AssembleText -> assembler -> VM -> serialization) consumes unchanged.
 * Targets SL's source-verified surface (see SLUA_SURFACE.md):
 *   - ll calls:   ll.Name(args)  ->  Phlox "ll"+Name  ->  existing 674-fn table (TableIndex)
 *   - events:     bare global function whose name is a Phlox event (touch_start, timer, ...)
 *   - top-level:  code outside any function = the state_entry-equivalent (runs on rez)
 *   - types:      Luau `number` == double  -> Phlox Float; coerce to a function's declared
 *                 param type at the call boundary using the EXISTING cast opcodes (icast/fcast).
 *
 * Scope: locals, number arithmetic + coercion, if/elseif/else, while, comparison, event-named
 * global functions, top-level code, ll.* calls. Everything else (tables, closures, LLEvents:on
 * object model, metatables, multiple states, user functions) is Tier-2 and intentionally rejected
 * with a clear error rather than mis-compiled.
 *
 * NO VM/opcode change: this is pure front-end codegen, mirroring what the LSL GenVisitor emits.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using InWorldz.Phlox.Types;

namespace InWorldz.Phlox.SLua
{
    // ======================================================================================
    // Public entry
    // ======================================================================================
    public static class SLuaCompiler
    {
        /// <summary>
        /// Heuristic script-kind detection. LSL never begins with a Lua line comment ("--"), so a
        /// leading "--" (and especially the explicit "--!slua"/"--!lua" marker) routes to SLua.
        /// </summary>
        public static bool IsLuaScript(string src)
        {
            if (string.IsNullOrEmpty(src)) return false;
            string s = src.TrimStart();
            return s.StartsWith("--!slua", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("--!lua", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("--");
        }

        /// <summary>
        /// Compile SLua source to Phlox assembly text. Returns null on error (reported via listener).
        /// </summary>
        public static string CompileToAssembly(string src, ILSLListener listener)
        {
            try
            {
                var tokens = new SLuaLexer(src).Tokenize();
                var chunk = new SLuaParser(tokens).ParseChunk();
                return new SLuaCodeGen().Generate(chunk);
            }
            catch (SLuaException e)
            {
                if (listener != null) listener.Error(string.Format("SLua: {0} (line {1})", e.Message, e.Line));
                return null;
            }
            catch (Exception e)
            {
                if (listener != null) listener.Error("SLua: internal compiler error: " + e.Message);
                return null;
            }
        }
    }

    public sealed class SLuaException : Exception
    {
        public int Line;
        public SLuaException(string msg, int line) : base(msg) { Line = line; }
    }

    // ======================================================================================
    // Lexer
    // ======================================================================================
    internal enum TT { Name, Number, String, Keyword, Op, EOF }

    internal struct Tok
    {
        public TT Type;
        public string Text;
        public double Num;
        public int Line;
        public Tok(TT t, string s, int line, double n = 0) { Type = t; Text = s; Line = line; Num = n; }
        public override string ToString() { return Type + ":" + Text; }
    }

    internal sealed class SLuaLexer
    {
        private static readonly HashSet<string> Keywords = new HashSet<string>
        {
            "local","function","end","if","then","elseif","else","while","do","return",
            "true","false","nil","and","or","not","for","in"
        };

        private readonly string _s;
        private int _i;
        private int _line = 1;

        public SLuaLexer(string s) { _s = s ?? string.Empty; }

        private char Cur => _i < _s.Length ? _s[_i] : '\0';
        private char Peek(int k = 1) => _i + k < _s.Length ? _s[_i + k] : '\0';

        public List<Tok> Tokenize()
        {
            var toks = new List<Tok>();
            while (true)
            {
                SkipTrivia();
                if (_i >= _s.Length) { toks.Add(new Tok(TT.EOF, "<eof>", _line)); break; }

                char c = Cur;
                if (char.IsLetter(c) || c == '_') { toks.Add(LexName()); continue; }
                if (char.IsDigit(c) || (c == '.' && char.IsDigit(Peek()))) { toks.Add(LexNumber()); continue; }
                if (c == '"' || c == '\'') { toks.Add(LexString(c)); continue; }
                toks.Add(LexOperator());
            }
            return toks;
        }

        private void SkipTrivia()
        {
            while (_i < _s.Length)
            {
                char c = Cur;
                if (c == '\n') { _line++; _i++; }
                else if (c == ' ' || c == '\t' || c == '\r') { _i++; }
                else if (c == '-' && Peek() == '-')
                {
                    _i += 2;
                    if (Cur == '[' && Peek() == '[') { _i += 2; SkipBlockComment(); }
                    else { while (_i < _s.Length && Cur != '\n') _i++; }
                }
                else break;
            }
        }

        private void SkipBlockComment()
        {
            while (_i < _s.Length)
            {
                if (Cur == ']' && Peek() == ']') { _i += 2; return; }
                if (Cur == '\n') _line++;
                _i++;
            }
        }

        private Tok LexName()
        {
            int start = _i;
            while (_i < _s.Length && (char.IsLetterOrDigit(Cur) || Cur == '_')) _i++;
            string text = _s.Substring(start, _i - start);
            return new Tok(Keywords.Contains(text) ? TT.Keyword : TT.Name, text, _line);
        }

        private Tok LexNumber()
        {
            int start = _i;
            // hex
            if (Cur == '0' && (Peek() == 'x' || Peek() == 'X'))
            {
                _i += 2;
                while (_i < _s.Length && Uri.IsHexDigit(Cur)) _i++;
                string hx = _s.Substring(start, _i - start);
                long hv = Convert.ToInt64(hx.Substring(2), 16);
                return new Tok(TT.Number, hx, _line, hv);
            }
            while (_i < _s.Length && char.IsDigit(Cur)) _i++;
            if (Cur == '.') { _i++; while (_i < _s.Length && char.IsDigit(Cur)) _i++; }
            if (Cur == 'e' || Cur == 'E')
            {
                _i++;
                if (Cur == '+' || Cur == '-') _i++;
                while (_i < _s.Length && char.IsDigit(Cur)) _i++;
            }
            string text = _s.Substring(start, _i - start);
            double d = double.Parse(text, CultureInfo.InvariantCulture);
            return new Tok(TT.Number, text, _line, d);
        }

        private Tok LexString(char quote)
        {
            int line = _line;
            _i++; // opening quote
            var sb = new StringBuilder();
            while (_i < _s.Length && Cur != quote)
            {
                char c = Cur;
                if (c == '\n') throw new SLuaException("unterminated string", line);
                if (c == '\\')
                {
                    _i++;
                    char e = Cur;
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '"': sb.Append('"'); break;
                        case '\'': sb.Append('\''); break;
                        case '\\': sb.Append('\\'); break;
                        default: sb.Append(e); break;
                    }
                    _i++;
                }
                else { sb.Append(c); _i++; }
            }
            if (_i >= _s.Length) throw new SLuaException("unterminated string", line);
            _i++; // closing quote
            return new Tok(TT.String, sb.ToString(), line);
        }

        private Tok LexOperator()
        {
            int line = _line;
            char c = Cur;
            char n = Peek();
            // two-char operators
            if ((c == '=' && n == '=') || (c == '~' && n == '=') ||
                (c == '<' && n == '=') || (c == '>' && n == '=') || (c == '.' && n == '.'))
            {
                _i += 2;
                return new Tok(TT.Op, new string(new[] { c, n }), line);
            }
            _i++;
            return new Tok(TT.Op, c.ToString(), line);
        }
    }

    // ======================================================================================
    // AST
    // ======================================================================================
    internal abstract class Node { public int Line; }

    internal abstract class Expr : Node { }
    internal sealed class NumberLit : Expr { public double Value; }
    internal sealed class StringLit : Expr { public string Value; }
    internal sealed class BoolLit : Expr { public bool Value; }
    internal sealed class NameRef : Expr { public string Name; }
    internal sealed class Binary : Expr { public string Op; public Expr L, R; }
    internal sealed class Unary : Expr { public string Op; public Expr E; }
    internal sealed class LlCall : Expr { public string Member; public List<Expr> Args; }
    // Tier-2 table expressions
    internal sealed class NilLit : Expr { }
    internal sealed class TableField { public Expr Key; public Expr Value; } // Key==null => array element
    internal sealed class TableLit : Expr { public List<TableField> Fields; }
    internal sealed class Index : Expr { public Expr Target; public Expr Key; }   // t[k] / t.x
    internal sealed class Len : Expr { public Expr E; }                            // #t
    internal sealed class Builtin : Expr { public string Name; public Expr Arg; }  // type/tostring/tonumber
    // Tier-2 essentials expressions
    internal sealed class LibCall : Expr { public string Lib; public string Fn; public List<Expr> Args; } // string.X(..)/math.X(..)
    internal sealed class LibValue : Expr { public string Lib; public string Name; }                       // math.pi / math.huge
    internal sealed class UserCall : Expr { public string Name; public List<Expr> Args; }                  // user function call
    internal sealed class FuncExpr : Expr { public List<string> Params; public List<Stmt> Body; }          // anonymous function
    internal sealed class MethodCall : Expr { public Expr Target; public string Method; public List<Expr> Args; } // obj:method(args)
    internal sealed class MetaCall : Expr { public string Name; public List<Expr> Args; }                   // setmetatable/getmetatable
    internal sealed class VecCtor : Expr { public bool IsRot; public List<Expr> Args; }                      // vector(x,y,z) / rotation(x,y,z,s)
    internal sealed class CoreCall : Expr { public string Name; public List<Expr> Args; }                    // print/error/assert/pcall
    internal sealed class CallExpr : Expr { public Expr Callee; public List<Expr> Args; }                     // <expr>(args) -- e.g. T.f(args)

    internal abstract class Stmt : Node { }
    internal sealed class LocalDecl : Stmt { public string Name; public Expr Init; }
    internal sealed class Assign : Stmt { public string Name; public Expr Value; }
    internal sealed class ExprStmt : Stmt { public LlCall Call; }
    internal sealed class IfStmt : Stmt { public Expr Cond; public List<Stmt> Then; public List<Stmt> Else; }
    internal sealed class WhileStmt : Stmt { public Expr Cond; public List<Stmt> Body; }
    internal sealed class ReturnStmt : Stmt { public List<Expr> Values; }
    internal sealed class FuncDecl : Stmt { public string Name; public List<string> Params; public List<Stmt> Body; }
    // Tier-2 table statements
    internal sealed class IndexAssign : Stmt { public Expr Target; public Expr Key; public Expr Value; } // t[k]=v / t.x=v
    internal sealed class ForIn : Stmt { public List<string> Vars; public Expr TableExpr; public List<Stmt> Body; public bool Gmatch; } // for k,v in pairs(t) / for w in gmatch(..)
    internal sealed class TableInsert : Stmt { public Expr Table; public Expr Value; } // table.insert(t, v)
    // Tier-2 essentials statements
    internal sealed class ForNum : Stmt { public string Var; public Expr Start; public Expr Stop; public Expr Step; public List<Stmt> Body; }
    internal sealed class LocalMulti : Stmt { public List<string> Names; public List<Expr> Values; }      // local a,b = ...
    internal sealed class AssignMulti : Stmt { public List<string> Names; public List<Expr> Values; }     // a,b = ...
    internal sealed class CallStmt : Stmt { public Expr Call; }                                           // f(...) / lib.x(...) / ll.X(...) statement

    // ======================================================================================
    // Parser (recursive descent)
    // ======================================================================================
    internal sealed class SLuaParser
    {
        private readonly List<Tok> _t;
        private int _p;

        public SLuaParser(List<Tok> toks) { _t = toks; }

        private Tok Cur => _t[_p];
        private Tok Next => _t[Math.Min(_p + 1, _t.Count - 1)];
        private bool IsOp(string s) => Cur.Type == TT.Op && Cur.Text == s;
        private bool IsKw(string s) => Cur.Type == TT.Keyword && Cur.Text == s;

        private Tok Eat() { return _t[_p++]; }
        private void ExpectOp(string s) { if (!IsOp(s)) Err("expected '" + s + "'"); _p++; }
        private void ExpectKw(string s) { if (!IsKw(s)) Err("expected '" + s + "'"); _p++; }
        private void Err(string m) { throw new SLuaException(m + " near '" + Cur.Text + "'", Cur.Line); }

        public List<Stmt> ParseChunk()
        {
            var stmts = new List<Stmt>();
            while (Cur.Type != TT.EOF) stmts.Add(ParseStat());
            return stmts;
        }

        // Parse a block until a terminator keyword (end/else/elseif) or EOF.
        private List<Stmt> ParseBlock()
        {
            var stmts = new List<Stmt>();
            while (Cur.Type != TT.EOF && !IsKw("end") && !IsKw("else") && !IsKw("elseif"))
                stmts.Add(ParseStat());
            return stmts;
        }

        private Stmt ParseStat()
        {
            int line = Cur.Line;
            if (IsOp(";")) { Eat(); return ParseStatNonEmpty(); } // skip stray ';'
            return ParseStatNonEmpty();
        }

        private Stmt ParseStatNonEmpty()
        {
            int line = Cur.Line;
            if (IsKw("local")) return ParseLocal();
            if (IsKw("function")) return ParseFunction();
            if (IsKw("if")) return ParseIf();
            if (IsKw("while")) return ParseWhile();
            if (IsKw("for")) return ParseFor();
            if (IsKw("return")) return ParseReturn();

            // Name-led statements.
            if (Cur.Type == TT.Name)
            {
                Expr first = ParsePrimary();  // NameRef / Index / LlCall / LibCall / UserCall

                // single index-assign: t[k] = v
                if (first is Index ix && IsOp("="))
                {
                    Eat();
                    var v = ParseExpr();
                    return new IndexAssign { Target = ix.Target, Key = ix.Key, Value = v, Line = line };
                }

                // name assignment (single or multi): a = ...  /  a, b = ...
                if (IsOp(",") || IsOp("="))
                {
                    if (!(first is NameRef nr0))
                        throw new SLuaException("assignment target must be a variable", line);
                    var names = new List<string> { nr0.Name };
                    while (IsOp(",")) { Eat(); if (Cur.Type != TT.Name) Err("expected variable name"); names.Add(Eat().Text); }
                    ExpectOp("=");
                    var vals = ParseExprList();
                    if (names.Count == 1 && vals.Count == 1)
                        return new Assign { Name = names[0], Value = vals[0], Line = line };
                    return new AssignMulti { Names = names, Values = vals, Line = line };
                }

                // call statement (result discarded)
                if (first is LlCall || first is UserCall || first is LibCall || first is MethodCall || first is CoreCall || first is CallExpr)
                    return new CallStmt { Call = first, Line = line };

                throw new SLuaException("expected '=' (assignment) or a call statement", line);
            }
            Err("unexpected statement");
            return null;
        }

        // Numeric for (for i = a, b [, c] do) or generic for (for k[,v] in pairs(t) do).
        private Stmt ParseFor()
        {
            int line = Cur.Line; ExpectKw("for");
            if (Cur.Type != TT.Name) Err("expected loop variable name");
            string first = Eat().Text;

            if (IsOp("="))  // numeric for
            {
                Eat();
                var start = ParseExpr();
                ExpectOp(",");
                var stop = ParseExpr();
                Expr step = null;
                if (IsOp(",")) { Eat(); step = ParseExpr(); }
                ExpectKw("do");
                var nbody = ParseBlock();
                ExpectKw("end");
                return new ForNum { Var = first, Start = start, Stop = stop, Step = step, Body = nbody, Line = line };
            }

            // generic for: first [, more] in (pairs(expr) | ipairs(expr) | string.gmatch(s,p)) do
            var vars = new List<string> { first };
            while (IsOp(",")) { Eat(); if (Cur.Type != TT.Name) Err("expected loop variable name"); vars.Add(Eat().Text); }
            ExpectKw("in");

            bool gmatch = false;
            Expr iter;
            if (Cur.Type == TT.Name && (Cur.Text == "pairs" || Cur.Text == "ipairs"))
            {
                Eat();
                ExpectOp("(");
                iter = ParseExpr();
                ExpectOp(")");
            }
            else if (Cur.Type == TT.Name && Cur.Text == "string" && Next.Type == TT.Op && Next.Text == ".")
            {
                Expr lc = ParseLibAccess();
                if (!(lc is LibCall g && g.Fn == "gmatch"))
                    throw new SLuaException("for-in iterator must be pairs/ipairs or string.gmatch in the Tier-2 subset", line);
                iter = lc; gmatch = true;
            }
            else throw new SLuaException("for-in requires pairs(...), ipairs(...), or string.gmatch(...) in the Tier-2 subset", Cur.Line);

            ExpectKw("do");
            var body = ParseBlock();
            ExpectKw("end");
            return new ForIn { Vars = vars, TableExpr = iter, Body = body, Gmatch = gmatch, Line = line };
        }

        // table.insert(t, v)  (Tier-2: t must be a simple name)
        private Stmt ParseTableLibCall()
        {
            int line = Cur.Line;
            Eat(); // 'table'
            ExpectOp(".");
            if (Cur.Type != TT.Name) Err("expected table library function name");
            string fn = Eat().Text;
            if (fn != "insert")
                throw new SLuaException("table." + fn + " is not supported in the Tier-2 subset (only table.insert)", line);
            ExpectOp("(");
            var t = ParseExpr();
            ExpectOp(",");
            var v = ParseExpr();
            ExpectOp(")");
            return new TableInsert { Table = t, Value = v, Line = line };
        }

        private Stmt ParseLocal()
        {
            int line = Cur.Line; ExpectKw("local");
            // local function name(...) ... end  ==  local name; name = function(...)...end
            if (IsKw("function"))
            {
                Eat();
                if (Cur.Type != TT.Name) Err("expected function name");
                string fname = Eat().Text;
                var fe = ParseFuncRest(line);
                return new LocalDecl { Name = fname, Init = fe, Line = line };
            }
            var names = new List<string>();
            while (true)
            {
                if (Cur.Type != TT.Name) Err("expected name after 'local'");
                names.Add(Eat().Text);
                if (IsOp(":")) { Eat(); SkipTypeAnnotation(); } // optional Luau type annotation
                if (IsOp(",")) { Eat(); continue; }
                break;
            }
            var vals = new List<Expr>();
            if (IsOp("="))
            {
                Eat();
                vals = ParseExprList();
            }
            if (names.Count == 1)
            {
                Expr init = (vals.Count == 1) ? vals[0] : new NilLit { Line = line };
                return new LocalDecl { Name = names[0], Init = init, Line = line };
            }
            return new LocalMulti { Names = names, Values = vals, Line = line };
        }

        private List<Expr> ParseExprList()
        {
            var list = new List<Expr> { ParseExpr() };
            while (IsOp(",")) { Eat(); list.Add(ParseExpr()); }
            return list;
        }

        private List<Expr> ParseCallArgs()
        {
            ExpectOp("(");
            var args = new List<Expr>();
            if (!IsOp(")")) { args.Add(ParseExpr()); while (IsOp(",")) { Eat(); args.Add(ParseExpr()); } }
            ExpectOp(")");
            return args;
        }

        private Stmt ParseFunction()
        {
            int line = Cur.Line; ExpectKw("function");
            if (Cur.Type != TT.Name) Err("expected function name");
            string baseName = Eat().Text;

            // Luau funcname:  Name ('.' Name)* (':' Name)?
            var dots = new List<string>();
            string method = null;
            while (IsOp(".")) { Eat(); if (Cur.Type != TT.Name && Cur.Type != TT.Keyword) Err("expected field name after '.'"); dots.Add(Eat().Text); }
            if (IsOp(":")) { Eat(); if (Cur.Type != TT.Name && Cur.Type != TT.Keyword) Err("expected method name after ':'"); method = Eat().Text; }

            // bare  function name(...)  -> existing local/global function declaration (unchanged)
            if (dots.Count == 0 && method == null)
            {
                var fe0 = ParseFuncRest(line);
                return new FuncDecl { Name = baseName, Params = fe0.Params, Body = fe0.Body, Line = line };
            }

            // table-field / method form: desugar to  <table-chain>.<field> = function(...) ... end
            var fe = ParseFuncRest(line);
            var pars = fe.Params;
            if (method != null) { pars = new List<string> { "self" }; pars.AddRange(fe.Params); } // colon -> implicit self

            // names that form the TABLE expression vs the final field key:
            //   dot form  T.a.b   -> table = T.a,        field = b
            //   colon form T.a:m  -> table = T.a,        field = m (the full dot chain is the table)
            var chain = new List<string> { baseName };
            chain.AddRange(dots);
            string field;
            int tableNames; // count of leading names forming the table expr
            if (method != null) { field = method; tableNames = chain.Count; }
            else { field = chain[chain.Count - 1]; tableNames = chain.Count - 1; }

            Expr target = new NameRef { Name = chain[0], Line = line };
            for (int i = 1; i < tableNames; i++)
                target = new Index { Target = target, Key = new StringLit { Value = chain[i], Line = line }, Line = line };

            return new IndexAssign
            {
                Target = target,
                Key = new StringLit { Value = field, Line = line },
                Value = new FuncExpr { Params = pars, Body = fe.Body, Line = line },
                Line = line
            };
        }

        // Parse the part after 'function' [name]: ( params ) [: rettype] block end -> FuncExpr.
        private FuncExpr ParseFuncRest(int line)
        {
            ExpectOp("(");
            var pars = new List<string>();
            if (!IsOp(")"))
            {
                while (true)
                {
                    if (Cur.Type != TT.Name) Err("expected parameter name");
                    pars.Add(Eat().Text);
                    if (IsOp(":")) { Eat(); SkipTypeAnnotation(); }
                    if (IsOp(",")) { Eat(); continue; }
                    break;
                }
            }
            ExpectOp(")");
            if (IsOp(":")) { Eat(); SkipTypeAnnotation(); } // optional return type
            var body = ParseBlock();
            ExpectKw("end");
            return new FuncExpr { Params = pars, Body = body, Line = line };
        }

        // Consume a (simple) Luau type annotation token-wise: a Name optionally followed by
        // {..}, generics, or table types are NOT in Tier-1; we accept a bare type name only.
        private void SkipTypeAnnotation()
        {
            if (Cur.Type == TT.Name || Cur.Type == TT.Keyword) { Eat(); return; }
            throw new SLuaException("unsupported type annotation in Tier-1 subset", Cur.Line);
        }

        private Stmt ParseIf()
        {
            int line = Cur.Line; ExpectKw("if");
            var cond = ParseExpr();
            ExpectKw("then");
            var then = ParseBlock();
            List<Stmt> els = null;
            if (IsKw("elseif"))
            {
                // desugar elseif into a nested if in the else branch
                els = new List<Stmt> { ParseElseIf() };
                return new IfStmt { Cond = cond, Then = then, Else = els, Line = line };
            }
            if (IsKw("else")) { Eat(); els = ParseBlock(); }
            ExpectKw("end");
            return new IfStmt { Cond = cond, Then = then, Else = els, Line = line };
        }

        private Stmt ParseElseIf()
        {
            int line = Cur.Line; ExpectKw("elseif");
            var cond = ParseExpr();
            ExpectKw("then");
            var then = ParseBlock();
            List<Stmt> els = null;
            if (IsKw("elseif")) { els = new List<Stmt> { ParseElseIf() }; return new IfStmt { Cond = cond, Then = then, Else = els, Line = line }; }
            if (IsKw("else")) { Eat(); els = ParseBlock(); }
            ExpectKw("end");
            return new IfStmt { Cond = cond, Then = then, Else = els, Line = line };
        }

        private Stmt ParseWhile()
        {
            int line = Cur.Line; ExpectKw("while");
            var cond = ParseExpr();
            ExpectKw("do");
            var body = ParseBlock();
            ExpectKw("end");
            return new WhileStmt { Cond = cond, Body = body, Line = line };
        }

        private Stmt ParseReturn()
        {
            int line = Cur.Line; ExpectKw("return");
            var vals = new List<Expr>();
            if (Cur.Type != TT.EOF && !IsKw("end") && !IsKw("else") && !IsKw("elseif") && !IsOp(";"))
                vals = ParseExprList();
            return new ReturnStmt { Values = vals, Line = line };
        }

        // ---- expressions (Lua precedence: or < and < comparison < .. < add < mul < unary) ----
        private Expr ParseExpr() { return ParseOr(); }

        private Expr ParseOr()
        {
            var l = ParseAnd();
            while (IsKw("or")) { Eat(); var r = ParseAnd(); l = new Binary { Op = "or", L = l, R = r, Line = l.Line }; }
            return l;
        }

        private Expr ParseAnd()
        {
            var l = ParseComparison();
            while (IsKw("and")) { Eat(); var r = ParseComparison(); l = new Binary { Op = "and", L = l, R = r, Line = l.Line }; }
            return l;
        }

        private Expr ParseComparison()
        {
            var l = ParseConcat();
            while (IsOp("<") || IsOp(">") || IsOp("<=") || IsOp(">=") || IsOp("==") || IsOp("~="))
            {
                string op = Eat().Text;
                var r = ParseConcat();
                l = new Binary { Op = op, L = l, R = r, Line = l.Line };
            }
            return l;
        }

        private Expr ParseConcat()
        {
            var l = ParseAdd();
            if (IsOp(".."))   // right-associative
            {
                Eat();
                var r = ParseConcat();
                return new Binary { Op = "..", L = l, R = r, Line = l.Line };
            }
            return l;
        }

        private Expr ParseAdd()
        {
            var l = ParseMul();
            while (IsOp("+") || IsOp("-"))
            {
                string op = Eat().Text;
                var r = ParseMul();
                l = new Binary { Op = op, L = l, R = r, Line = l.Line };
            }
            return l;
        }

        private Expr ParseMul()
        {
            var l = ParseUnary();
            while (IsOp("*") || IsOp("/") || IsOp("%"))
            {
                string op = Eat().Text;
                var r = ParseUnary();
                l = new Binary { Op = op, L = l, R = r, Line = l.Line };
            }
            return l;
        }

        private Expr ParseUnary()
        {
            if (IsOp("-"))
            {
                int line = Cur.Line; Eat();
                var e = ParseUnary();
                return new Unary { Op = "-", E = e, Line = line };
            }
            if (IsOp("#"))
            {
                int line = Cur.Line; Eat();
                var e = ParseUnary();
                return new Len { E = e, Line = line };
            }
            if (IsKw("not"))
            {
                int line = Cur.Line; Eat();
                var e = ParseUnary();
                return new Unary { Op = "not", E = e, Line = line };
            }
            return ParsePrimary();
        }

        private Expr ParsePrimary()
        {
            var t = Cur;
            if (t.Type == TT.Number) { Eat(); return new NumberLit { Value = t.Num, Line = t.Line }; }
            if (t.Type == TT.String) { Eat(); return new StringLit { Value = t.Text, Line = t.Line }; }
            if (IsKw("true")) { Eat(); return new BoolLit { Value = true, Line = t.Line }; }
            if (IsKw("false")) { Eat(); return new BoolLit { Value = false, Line = t.Line }; }
            if (IsKw("nil")) { Eat(); return new NilLit { Line = t.Line }; }
            if (IsKw("function")) { Eat(); return ParseFuncRest(t.Line); } // anonymous function expression
            if (IsOp("{")) return ParseTableLit();
            if (IsOp("("))
            {
                Eat();
                var e = ParseExpr();
                ExpectOp(")");
                return ParsePostfix(e);
            }
            if (t.Type == TT.Name)
            {
                if (t.Text == "ll" && Next.Type == TT.Op && Next.Text == ".")
                    return ParseLlCall();
                if ((t.Text == "string" || t.Text == "math" || t.Text == "table") && Next.Type == TT.Op && Next.Text == ".")
                    return ParseLibAccess();
                if ((t.Text == "type" || t.Text == "tostring" || t.Text == "tonumber")
                    && Next.Type == TT.Op && Next.Text == "(")
                {
                    Eat();                  // builtin name
                    ExpectOp("(");
                    var arg = ParseExpr();
                    ExpectOp(")");
                    return new Builtin { Name = t.Text, Arg = arg, Line = t.Line };
                }
                if ((t.Text == "setmetatable" || t.Text == "getmetatable")
                    && Next.Type == TT.Op && Next.Text == "(")
                {
                    Eat();                  // builtin name
                    var margs = ParseCallArgs();
                    return ParsePostfix(new MetaCall { Name = t.Text, Args = margs, Line = t.Line });
                }
                if ((t.Text == "vector" || t.Text == "rotation" || t.Text == "quaternion")
                    && Next.Type == TT.Op && Next.Text == "(")
                {
                    bool isRot = (t.Text != "vector");   // rotation/quaternion are synonyms
                    Eat();                  // constructor name
                    var vargs = ParseCallArgs();
                    return ParsePostfix(new VecCtor { IsRot = isRot, Args = vargs, Line = t.Line });
                }
                if ((t.Text == "print" || t.Text == "error" || t.Text == "assert" || t.Text == "pcall")
                    && Next.Type == TT.Op && Next.Text == "(")
                {
                    Eat();                  // core builtin name
                    var cargs = ParseCallArgs();
                    return ParsePostfix(new CoreCall { Name = t.Text, Args = cargs, Line = t.Line });
                }
                Eat();
                if (IsOp("("))   // user function call: name(args)
                    return new UserCall { Name = t.Text, Args = ParseCallArgs(), Line = t.Line };
                return ParsePostfix(new NameRef { Name = t.Text, Line = t.Line });
            }
            Err("expected expression");
            return null;
        }

        // string.fn(...) / math.fn(...)  -> LibCall ;  math.pi / math.huge -> LibValue
        private Expr ParseLibAccess()
        {
            var lib = Eat();           // 'string' / 'math'
            ExpectOp(".");
            if (Cur.Type != TT.Name) Err("expected library member name");
            string member = Eat().Text;
            if (IsOp("("))
                return new LibCall { Lib = lib.Text, Fn = member, Args = ParseCallArgs(), Line = lib.Line };
            return new LibValue { Lib = lib.Text, Name = member, Line = lib.Line };
        }

        // A prefix expression: a Name followed by a postfix chain of .field / [key] indexing.
        private Expr ParsePrefixExpr()
        {
            var t = Cur;
            if (t.Type != TT.Name) { Err("expected name"); return null; }
            Eat();
            Expr e = new NameRef { Name = t.Text, Line = t.Line };
            return ParsePostfix(e);
        }

        private Expr ParsePostfix(Expr e)
        {
            while (true)
            {
                if (IsOp("."))
                {
                    int line = Cur.Line; Eat();
                    if (Cur.Type != TT.Name && Cur.Type != TT.Keyword) Err("expected field name after '.'");
                    string field = Eat().Text;
                    e = new Index { Target = e, Key = new StringLit { Value = field, Line = line }, Line = line };
                }
                else if (IsOp("["))
                {
                    int line = Cur.Line; Eat();
                    var key = ParseExpr();
                    ExpectOp("]");
                    e = new Index { Target = e, Key = key, Line = line };
                }
                else if (IsOp(":"))   // method call: obj:method(args)
                {
                    int line = Cur.Line; Eat();
                    if (Cur.Type != TT.Name) Err("expected method name after ':'");
                    string method = Eat().Text;
                    var margs = ParseCallArgs();
                    e = new MethodCall { Target = e, Method = method, Args = margs, Line = line };
                }
                else if (IsOp("("))   // call a computed function value: T.f(args), g()(args), etc.
                {
                    int line = Cur.Line;
                    var cargs = ParseCallArgs();
                    e = new CallExpr { Callee = e, Args = cargs, Line = line };
                }
                else break;
            }
            return e;
        }

        // { } | { e1, e2, ... } | { x = v, ... } | { [k] = v, ... } | mixed
        private Expr ParseTableLit()
        {
            int line = Cur.Line; ExpectOp("{");
            var fields = new List<TableField>();
            while (!IsOp("}"))
            {
                if (IsOp("["))
                {
                    Eat();
                    var k = ParseExpr();
                    ExpectOp("]");
                    ExpectOp("=");
                    var v = ParseExpr();
                    fields.Add(new TableField { Key = k, Value = v });
                }
                else if (Cur.Type == TT.Name && Next.Type == TT.Op && Next.Text == "=")
                {
                    string name = Eat().Text; // field name
                    Eat();                    // '='
                    var v = ParseExpr();
                    fields.Add(new TableField { Key = new StringLit { Value = name, Line = line }, Value = v });
                }
                else
                {
                    var v = ParseExpr();
                    fields.Add(new TableField { Key = null, Value = v }); // array element
                }

                if (IsOp(",") || IsOp(";")) { Eat(); continue; }
                break;
            }
            ExpectOp("}");
            return new TableLit { Fields = fields, Line = line };
        }

        private LlCall ParseLlCall()
        {
            int line = Cur.Line;
            // 'll'
            if (!(Cur.Type == TT.Name && Cur.Text == "ll")) Err("expected 'll'");
            Eat();
            ExpectOp(".");
            if (Cur.Type != TT.Name) Err("expected ll member name");
            string member = Eat().Text;
            ExpectOp("(");
            var args = new List<Expr>();
            if (!IsOp(")"))
            {
                while (true)
                {
                    args.Add(ParseExpr());
                    if (IsOp(",")) { Eat(); continue; }
                    break;
                }
            }
            ExpectOp(")");
            return new LlCall { Member = member, Args = args, Line = line };
        }
    }

    // ======================================================================================
    // Code generator: AST -> Phlox assembly text
    // ======================================================================================
    internal sealed class SLuaCodeGen
    {
        private StringBuilder _sb = new StringBuilder();
        private readonly SupportedEventList _events = new SupportedEventList();

        // top-level locals -> global slots (with type)
        private readonly Dictionary<string, VarVar> _globals = new Dictionary<string, VarVar>();
        private int _labelCounter;

        // user functions: name -> declared param count / return count (computed in a pre-pass so
        // call sites know the calling convention). Return values live on the shared operand stack.
        private readonly Dictionary<string, int> _userParams = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _userReturns = new Dictionary<string, int>();

        // Pseudo-type for dynamically-typed values (table reads, nil, table literals, closures).
        private const VarType Dynamic = (VarType)99;

        private struct VarVar { public int Slot; public VarType Type; public VarVar(int s, VarType t) { Slot = s; Type = t; } }

        // ---- closures: nested function scope + upvalue resolution ----
        private sealed class LocalVar { public int Slot; public VarType Type; public bool IsCell; }
        private struct UpvalDesc { public bool FromParentLocal; public int Index; } // Index = parent local slot OR parent upval index
        private sealed class FuncScope
        {
            public FuncScope Parent;
            public Dictionary<string, LocalVar> Locals = new Dictionary<string, LocalVar>();
            public int NextSlot;
            public int Returns;
            public List<UpvalDesc> Upvals = new List<UpvalDesc>();
            public Dictionary<string, int> UpvalIdx = new Dictionary<string, int>();
            public HashSet<string> Captured = new HashSet<string>(); // own locals captured by nested fns -> cells
        }
        private FuncScope _scope;                                  // current function scope (null in globals-init)
        private readonly List<string> _lambdaDefs = new List<string>(); // flattened .def blocks for anon functions
        private int _lambdaSeq;

        // LLEvents:on registry — a hidden global table (event-name -> list of handler closures).
        // Serializes for free via Globals. _lleventsSlot is -1 when LLEvents:on is unused.
        private int _lleventsSlot = -1;
        private readonly HashSet<string> _lleventsUsed = new HashSet<string>(); // literal event names registered

        // Allocate a named local in the current scope; a captured name becomes a cell.
        private LocalVar AllocLocal(string name, VarType type)
        {
            var lv = new LocalVar { Slot = _scope.NextSlot++, Type = type, IsCell = _scope.Captured.Contains(name) };
            _scope.Locals[name] = lv;
            return lv;
        }
        private int AllocTempLocal() { return _scope.NextSlot++; } // compiler temp (never captured)

        // ---- variable resolution (local / upvalue / global / top-level function) ----
        private enum RKind { Local, Upval, Global, TopFunc, None }
        private struct Resolved { public RKind Kind; public LocalVar Local; public int UpvalIdx; public VarVar Global; }

        private Resolved Resolve(string name)
        {
            var r = new Resolved();
            if (_scope != null && _scope.Locals.TryGetValue(name, out var lv)) { r.Kind = RKind.Local; r.Local = lv; return r; }
            if (_scope != null) { int u = ResolveUpval(_scope, name); if (u >= 0) { r.Kind = RKind.Upval; r.UpvalIdx = u; return r; } }
            if (_globals.TryGetValue(name, out var gv)) { r.Kind = RKind.Global; r.Global = gv; return r; }
            if (_userParams.ContainsKey(name)) { r.Kind = RKind.TopFunc; return r; }
            r.Kind = RKind.None; return r;
        }

        // Register (transitively) an upvalue for `scope` resolving `name` in an ancestor; returns its index.
        private int ResolveUpval(FuncScope scope, string name)
        {
            if (scope.UpvalIdx.TryGetValue(name, out var ex)) return ex;
            if (scope.Parent == null) return -1;
            if (scope.Parent.Locals.TryGetValue(name, out var pl))
            {
                int idx = scope.Upvals.Count;
                scope.Upvals.Add(new UpvalDesc { FromParentLocal = true, Index = pl.Slot });
                scope.UpvalIdx[name] = idx;
                return idx;
            }
            int p = ResolveUpval(scope.Parent, name);
            if (p < 0) return -1;
            int i2 = scope.Upvals.Count;
            scope.Upvals.Add(new UpvalDesc { FromParentLocal = false, Index = p });
            scope.UpvalIdx[name] = i2;
            return i2;
        }

        private VarType EmitLoadName(string name, int line)
        {
            var r = Resolve(name);
            switch (r.Kind)
            {
                case RKind.Local:
                    Line("load " + r.Local.Slot);
                    if (r.Local.IsCell) { Line("cellget"); return Dynamic; }
                    return r.Local.Type;
                case RKind.Upval: Line("getupval " + r.UpvalIdx); return Dynamic;
                case RKind.Global: Line("gload " + r.Global.Slot); return r.Global.Type;
                case RKind.TopFunc: Line("mkclosure " + name + "(), 0"); return Dynamic; // function as a value
                default: throw new SLuaException("reference to undeclared variable '" + name + "'", line);
            }
        }

        private void EmitAssignName(string name, System.Func<VarType> emitValue, int line)
        {
            var r = Resolve(name);
            switch (r.Kind)
            {
                case RKind.Local:
                    if (r.Local.IsCell) { Line("load " + r.Local.Slot); emitValue(); Line("cellput"); }
                    else { VarType t = emitValue(); Coerce(t, r.Local.Type, line); Line("store " + r.Local.Slot); }
                    break;
                case RKind.Upval: emitValue(); Line("setupval " + r.UpvalIdx); break;
                case RKind.Global: { VarType t = emitValue(); Coerce(t, r.Global.Type, line); Line("gstore " + r.Global.Slot); } break;
                default: throw new SLuaException("assignment to undeclared variable '" + name + "'", line);
            }
        }

        // Emit a function body as a flattened .def (appended at top level). For a lambda, `parent` is
        // the enclosing scope and the resolved upvalue descriptors are returned via `outUpvals`.
        private void EmitFunctionDef(string defName, List<string> pars, List<Stmt> body, int R, FuncScope parent, List<UpvalDesc> outUpvals)
        {
            FuncScope savedScope = _scope;
            StringBuilder savedSb = _sb;

            _scope = new FuncScope { Parent = parent };
            _scope.Captured = CollectCaptured(body);
            _scope.Returns = R;
            for (int i = 0; i < pars.Count; i++)
                _scope.Locals[pars[i]] = new LocalVar { Slot = i, Type = Dynamic, IsCell = _scope.Captured.Contains(pars[i]) };
            _scope.NextSlot = pars.Count;

            StringBuilder bodyBuf = new StringBuilder();
            _sb = bodyBuf;
            // wrap captured params into cells at entry
            foreach (var p in pars) { var lv = _scope.Locals[p]; if (lv.IsCell) { Line("load " + lv.Slot); Line("mkcell"); Line("store " + lv.Slot); } }
            EmitBlock(body);
            for (int i = 0; i < R; i++) Line("pushnil"); // fall-off: always leave exactly R values

            int inner = _scope.NextSlot - pars.Count;
            var upvals = _scope.Upvals;
            _scope = savedScope;
            _sb = savedSb;

            var def = new StringBuilder();
            def.Append(".def " + defName + ": args=" + pars.Count + ", locals=" + inner + "\n");
            def.Append(bodyBuf.ToString());
            def.Append("ret\n\n");
            _lambdaDefs.Add(def.ToString());

            if (outUpvals != null) outUpvals.AddRange(upvals);
        }

        // ---- free-variable analysis: names referenced inside nested functions (-> captured cells) ----
        private static HashSet<string> CollectCaptured(List<Stmt> body)
        {
            var names = new HashSet<string>();
            foreach (var s in body) ScanStmtForNested(s, names);
            return names;
        }
        private static void ScanStmtForNested(Stmt s, HashSet<string> names)
        {
            switch (s)
            {
                case LocalDecl ld: ScanExprForNested(ld.Init, names); break;
                case LocalMulti lm: foreach (var v in lm.Values) ScanExprForNested(v, names); break;
                case Assign a: ScanExprForNested(a.Value, names); break;
                case AssignMulti am: foreach (var v in am.Values) ScanExprForNested(v, names); break;
                case IndexAssign ia: ScanExprForNested(ia.Target, names); ScanExprForNested(ia.Key, names); ScanExprForNested(ia.Value, names); break;
                case TableInsert ti: ScanExprForNested(ti.Table, names); ScanExprForNested(ti.Value, names); break;
                case ExprStmt es: ScanExprForNested(es.Call, names); break;
                case CallStmt cs: ScanExprForNested(cs.Call, names); break;
                case IfStmt ifs: ScanExprForNested(ifs.Cond, names); foreach (var st in ifs.Then) ScanStmtForNested(st, names); if (ifs.Else != null) foreach (var st in ifs.Else) ScanStmtForNested(st, names); break;
                case WhileStmt w: ScanExprForNested(w.Cond, names); foreach (var st in w.Body) ScanStmtForNested(st, names); break;
                case ForIn fi: ScanExprForNested(fi.TableExpr, names); foreach (var st in fi.Body) ScanStmtForNested(st, names); break;
                case ForNum fn: ScanExprForNested(fn.Start, names); ScanExprForNested(fn.Stop, names); if (fn.Step != null) ScanExprForNested(fn.Step, names); foreach (var st in fn.Body) ScanStmtForNested(st, names); break;
                case ReturnStmt r: foreach (var v in r.Values) ScanExprForNested(v, names); break;
                case FuncDecl fd: AllNamesList(fd.Body, names); break;
            }
        }
        private static void ScanExprForNested(Expr e, HashSet<string> names)
        {
            switch (e)
            {
                case FuncExpr fe: AllNamesList(fe.Body, names); break;
                case Binary b: ScanExprForNested(b.L, names); ScanExprForNested(b.R, names); break;
                case Unary u: ScanExprForNested(u.E, names); break;
                case LlCall c: foreach (var a in c.Args) ScanExprForNested(a, names); break;
                case LibCall lc: foreach (var a in lc.Args) ScanExprForNested(a, names); break;
                case UserCall uc: foreach (var a in uc.Args) ScanExprForNested(a, names); break;
                case Index ix: ScanExprForNested(ix.Target, names); ScanExprForNested(ix.Key, names); break;
                case Len ln: ScanExprForNested(ln.E, names); break;
                case TableLit tl: foreach (var f in tl.Fields) { if (f.Key != null) ScanExprForNested(f.Key, names); ScanExprForNested(f.Value, names); } break;
                case Builtin bi: ScanExprForNested(bi.Arg, names); break;
                case MethodCall mc: ScanExprForNested(mc.Target, names); foreach (var a in mc.Args) ScanExprForNested(a, names); break;
                case MetaCall mtc: foreach (var a in mtc.Args) ScanExprForNested(a, names); break;
                case VecCtor vtc: foreach (var a in vtc.Args) ScanExprForNested(a, names); break;
                case CoreCall cc: foreach (var a in cc.Args) ScanExprForNested(a, names); break;
                case CallExpr ce: ScanExprForNested(ce.Callee, names); foreach (var a in ce.Args) ScanExprForNested(a, names); break;
            }
        }
        private static void AllNamesList(List<Stmt> body, HashSet<string> names) { foreach (var s in body) AllNamesStmt(s, names); }
        private static void AllNamesStmt(Stmt s, HashSet<string> names)
        {
            switch (s)
            {
                case LocalDecl ld: AllNamesExpr(ld.Init, names); break;
                case LocalMulti lm: foreach (var v in lm.Values) AllNamesExpr(v, names); break;
                case Assign a: names.Add(a.Name); AllNamesExpr(a.Value, names); break;
                case AssignMulti am: foreach (var n in am.Names) names.Add(n); foreach (var v in am.Values) AllNamesExpr(v, names); break;
                case IndexAssign ia: AllNamesExpr(ia.Target, names); AllNamesExpr(ia.Key, names); AllNamesExpr(ia.Value, names); break;
                case TableInsert ti: AllNamesExpr(ti.Table, names); AllNamesExpr(ti.Value, names); break;
                case ExprStmt es: AllNamesExpr(es.Call, names); break;
                case CallStmt cs: AllNamesExpr(cs.Call, names); break;
                case IfStmt ifs: AllNamesExpr(ifs.Cond, names); AllNamesList(ifs.Then, names); if (ifs.Else != null) AllNamesList(ifs.Else, names); break;
                case WhileStmt w: AllNamesExpr(w.Cond, names); AllNamesList(w.Body, names); break;
                case ForIn fi: AllNamesExpr(fi.TableExpr, names); AllNamesList(fi.Body, names); break;
                case ForNum fn: AllNamesExpr(fn.Start, names); AllNamesExpr(fn.Stop, names); if (fn.Step != null) AllNamesExpr(fn.Step, names); AllNamesList(fn.Body, names); break;
                case ReturnStmt r: foreach (var v in r.Values) AllNamesExpr(v, names); break;
                case FuncDecl fd: AllNamesList(fd.Body, names); break;
            }
        }
        private static void AllNamesExpr(Expr e, HashSet<string> names)
        {
            switch (e)
            {
                case NameRef nr: names.Add(nr.Name); break;
                case UserCall uc: names.Add(uc.Name); foreach (var a in uc.Args) AllNamesExpr(a, names); break;
                case Binary b: AllNamesExpr(b.L, names); AllNamesExpr(b.R, names); break;
                case Unary u: AllNamesExpr(u.E, names); break;
                case LlCall c: foreach (var a in c.Args) AllNamesExpr(a, names); break;
                case LibCall lc: foreach (var a in lc.Args) AllNamesExpr(a, names); break;
                case Index ix: AllNamesExpr(ix.Target, names); AllNamesExpr(ix.Key, names); break;
                case Len ln: AllNamesExpr(ln.E, names); break;
                case TableLit tl: foreach (var f in tl.Fields) { if (f.Key != null) AllNamesExpr(f.Key, names); AllNamesExpr(f.Value, names); } break;
                case Builtin bi: AllNamesExpr(bi.Arg, names); break;
                case FuncExpr fe: AllNamesList(fe.Body, names); break;
                case MethodCall mc: AllNamesExpr(mc.Target, names); foreach (var a in mc.Args) AllNamesExpr(a, names); break;
                case MetaCall mtc: foreach (var a in mtc.Args) AllNamesExpr(a, names); break;
                case VecCtor vtc: foreach (var a in vtc.Args) AllNamesExpr(a, names); break;
                case CoreCall cc: foreach (var a in cc.Args) AllNamesExpr(a, names); break;
                case CallExpr ce: AllNamesExpr(ce.Callee, names); foreach (var a in ce.Args) AllNamesExpr(a, names); break;
            }
        }

        // ---- LLEvents:on collection (literal event names) + dispatcher generation ----
        private static void CollectLLEvents(List<Stmt> body, HashSet<string> evs) { foreach (var s in body) LLEStmt(s, evs); }
        private static void LLEStmt(Stmt s, HashSet<string> evs)
        {
            switch (s)
            {
                case LocalDecl ld: LLEExpr(ld.Init, evs); break;
                case LocalMulti lm: foreach (var v in lm.Values) LLEExpr(v, evs); break;
                case Assign a: LLEExpr(a.Value, evs); break;
                case AssignMulti am: foreach (var v in am.Values) LLEExpr(v, evs); break;
                case IndexAssign ia: LLEExpr(ia.Target, evs); LLEExpr(ia.Key, evs); LLEExpr(ia.Value, evs); break;
                case TableInsert ti: LLEExpr(ti.Table, evs); LLEExpr(ti.Value, evs); break;
                case ExprStmt es: LLEExpr(es.Call, evs); break;
                case CallStmt cs: LLEExpr(cs.Call, evs); break;
                case IfStmt ifs: LLEExpr(ifs.Cond, evs); CollectLLEvents(ifs.Then, evs); if (ifs.Else != null) CollectLLEvents(ifs.Else, evs); break;
                case WhileStmt w: LLEExpr(w.Cond, evs); CollectLLEvents(w.Body, evs); break;
                case ForIn fi: LLEExpr(fi.TableExpr, evs); CollectLLEvents(fi.Body, evs); break;
                case ForNum fn: LLEExpr(fn.Start, evs); LLEExpr(fn.Stop, evs); if (fn.Step != null) LLEExpr(fn.Step, evs); CollectLLEvents(fn.Body, evs); break;
                case ReturnStmt r: foreach (var v in r.Values) LLEExpr(v, evs); break;
                case FuncDecl fd: CollectLLEvents(fd.Body, evs); break;
            }
        }
        private static void LLEExpr(Expr e, HashSet<string> evs)
        {
            switch (e)
            {
                case MethodCall mc:
                    if (mc.Target is NameRef nr && nr.Name == "LLEvents" && mc.Method == "on" && mc.Args.Count >= 1 && mc.Args[0] is StringLit sl)
                        evs.Add(sl.Value);
                    LLEExpr(mc.Target, evs); foreach (var a in mc.Args) LLEExpr(a, evs); break;
                case FuncExpr fe: CollectLLEvents(fe.Body, evs); break;
                case Binary b: LLEExpr(b.L, evs); LLEExpr(b.R, evs); break;
                case Unary u: LLEExpr(u.E, evs); break;
                case LlCall c: foreach (var a in c.Args) LLEExpr(a, evs); break;
                case LibCall lc: foreach (var a in lc.Args) LLEExpr(a, evs); break;
                case UserCall uc: foreach (var a in uc.Args) LLEExpr(a, evs); break;
                case Index ix: LLEExpr(ix.Target, evs); LLEExpr(ix.Key, evs); break;
                case Len ln: LLEExpr(ln.E, evs); break;
                case TableLit tl: foreach (var f in tl.Fields) { if (f.Key != null) LLEExpr(f.Key, evs); LLEExpr(f.Value, evs); } break;
                case Builtin bi: LLEExpr(bi.Arg, evs); break;
                case MetaCall mtc: foreach (var a in mtc.Args) LLEExpr(a, evs); break;
                case VecCtor vtc: foreach (var a in vtc.Args) LLEExpr(a, evs); break;
                case CoreCall cc: foreach (var a in cc.Args) LLEExpr(a, evs); break;
                case CallExpr ce: LLEExpr(ce.Callee, evs); foreach (var a in ce.Args) LLEExpr(a, evs); break;
            }
        }

        // Synthesize a .evt that dispatches registered LLEvents:on handlers for an event.
        private void EmitLLEventDispatcher(string ev)
        {
            if (!_events.HasEventByName(ev))
                throw new SLuaException("LLEvents:on: unknown event '" + ev + "'", 0);
            int argc = new List<VarType>(_events.GetArguments(ev)).Count;
            Line(".evt default/" + ev + ": args=" + argc + ", locals=0");
            Line("gload " + _lleventsSlot);                 // registry table
            for (int i = 0; i < argc; i++) Line("load " + i); // the event's args
            Line("firellevents \"" + EscapeString(ev) + "\", " + argc);
            Line("ret");
            Line("");
        }

        // obj:method(args). LLEvents:on -> registration; otherwise a runtime method dispatch.
        private VarType EmitMethodCall(MethodCall mc)
        {
            if (mc.Target is NameRef tnr && tnr.Name == "LLEvents" && mc.Method == "on")
            {
                if (mc.Args.Count != 2 || !(mc.Args[0] is StringLit))
                    throw new SLuaException("LLEvents:on expects (\"eventName\", handler) with a literal event name in Tier-2", mc.Line);
                Line("gload " + _lleventsSlot);   // registry
                EmitExpr(mc.Args[0]);             // event name
                EmitExpr(mc.Args[1]);             // handler closure
                Line("regevent");
                return VarType.Void;
            }
            EmitExpr(mc.Target);
            foreach (var a in mc.Args) EmitExpr(a);
            Line("methcall \"" + EscapeString(mc.Method) + "\", " + mc.Args.Count);
            return Dynamic;
        }

        // ---- unified call emission: produce exactly `wanted` values from any call expression ----
        private static bool IsCallExpr(Expr e) { return e is UserCall || (e is LibCall lc && IsMultiLib(lc)) || e is CoreCall || e is CallExpr; }

        private void EmitCallTo(Expr e, int wanted)
        {
            if (e is LibCall lc && IsMultiLib(lc))
            {
                int id = LibFuncId(lc.Lib, lc.Fn, lc.Line);
                foreach (var a in lc.Args) EmitExpr(a);
                Line("luacallm " + id + ", " + lc.Args.Count);
                Line("adjustm " + wanted);
                return;
            }
            if (e is CoreCall cc) { EmitCoreCall(cc, wanted); return; }
            if (e is CallExpr ce)
            {
                EmitExpr(ce.Callee);                      // the function value (closure / __call table)
                foreach (var a in ce.Args) EmitExpr(a);   // callv pads/truncates to callee arity
                Line("callv " + ce.Args.Count + ", " + wanted);
                return;
            }
            if (e is UserCall uc)
            {
                var r = Resolve(uc.Name);
                if (r.Kind == RKind.TopFunc)
                {
                    int R = _userReturns[uc.Name];
                    EmitArgsAdjusted(uc.Args, _userParams[uc.Name]);
                    Line("call " + uc.Name + "()");
                    if (R > wanted) for (int k = 0; k < R - wanted; k++) Line("pop");
                    else for (int k = 0; k < wanted - R; k++) Line("pushnil");
                }
                else
                {
                    EmitLoadName(uc.Name, uc.Line);          // the closure value
                    foreach (var a in uc.Args) EmitExpr(a);  // callv pads/truncates args to callee arity
                    Line("callv " + uc.Args.Count + ", " + wanted);
                }
                return;
            }
            // a non-call expression in a value-list position: 1 value, padded to `wanted`
            EmitExpr(e);
            if (wanted == 0) Line("pop");
            else for (int k = 1; k < wanted; k++) Line("pushnil");
        }

        private void EmitArgsAdjusted(List<Expr> args, int p)
        {
            for (int i = 0; i < args.Count; i++) EmitExpr(args[i]);
            if (args.Count > p) for (int k = 0; k < args.Count - p; k++) Line("pop");
            else for (int k = 0; k < p - args.Count; k++) Line("pushnil");
        }

        public string Generate(List<Stmt> chunk)
        {
            var topLocals = new List<LocalDecl>();
            var funcs = new List<FuncDecl>();
            var execStmts = new List<Stmt>();

            foreach (var s in chunk)
            {
                if (s is LocalDecl ld) topLocals.Add(ld);
                else if (s is FuncDecl fd) funcs.Add(fd);
                else execStmts.Add(s);
            }

            // SL semantics: when top-level code is present it IS the rez handler, and a user-defined
            // `function state_entry()` is an ORDINARY function the author calls explicitly (NOT auto-fired).
            // So with top-level code, state_entry is reclassified out of the event set into user functions
            // (no auto-fire, no double-fire, no rejection). With NO top-level code we keep the LSL-parity
            // convenience of auto-firing a `state_entry` event. SL's canonical default script (function
            // state_entry + LLEvents:on + an explicit state_entry() call) thus compiles and behaves as on SL.
            bool hasTopLevel = execStmts.Count > 0;

            // partition: event handlers vs user functions; pre-register user-function signatures
            var events = new List<FuncDecl>();
            var userFuncs = new List<FuncDecl>();
            foreach (var f in funcs)
            {
                bool isEvent = _events.HasEventByName(f.Name)
                               && !(hasTopLevel && f.Name == "state_entry"); // SL: state_entry is a plain fn here
                if (isEvent) events.Add(f);
                else
                {
                    userFuncs.Add(f);
                    _userParams[f.Name] = f.Params.Count;
                    _userReturns[f.Name] = AnalyzeReturnCount(f.Body);
                }
            }

            // ---- LLEvents:on pre-scan: collect registered event names; reserve a registry global ----
            CollectLLEvents(chunk, _lleventsUsed);
            bool useLLEvents = _lleventsUsed.Count > 0;
            if (useLLEvents) _lleventsSlot = topLocals.Count; // one extra global slot for the registry
            int globalCount = topLocals.Count + (useLLEvents ? 1 : 0);

            // Pre-register every top-level local as a global slot up front, so references resolve
            // regardless of source order (a later definition seen by an earlier statement is nil,
            // matching Lua). Top-level `local`s are persistent script state = Phlox globals.
            for (int i = 0; i < topLocals.Count; i++)
            {
                if (_globals.ContainsKey(topLocals[i].Name))
                    throw new SLuaException("duplicate top-level local '" + topLocals[i].Name + "'", topLocals[i].Line);
                _globals[topLocals[i].Name] = new VarVar(i, Dynamic);
            }

            // ---- header + globals-init block ----
            Line(".globals " + globalCount);
            Line(".statedef default");
            Line("");
            _scope = null; // global-init runs in no function frame
            if (useLLEvents) { Line("buildtable 0"); Line("gstore " + _lleventsSlot); } // registry first
            if (!hasTopLevel)
            {
                // no top-level code: initialize the persistent locals here (Tier-1 / event-script form)
                for (int i = 0; i < topLocals.Count; i++) { EmitExpr(topLocals[i].Init); Line("gstore " + i); }
            }
            Line("halt");
            Line("");

            // events for which the script defines a Form-1 global handler (those win per-event)
            var form1Events = new HashSet<string>();
            foreach (var f in events) form1Events.Add(f.Name);

            // ---- synthesized state_entry = ALL top-level code in SOURCE ORDER (the rez handler). A
            //      top-level `local x = e` lowers to a global store; everything else runs as-is, so an
            //      instance built at top level sees classes/functions defined earlier in source. This
            //      runs in a real frame, so nested block-locals in top-level control flow work too. ----
            if (hasTopLevel)
            {
                var topBody = new List<Stmt>();
                foreach (var s in chunk)
                {
                    if (s is FuncDecl) continue;
                    if (s is LocalDecl ld) topBody.Add(new Assign { Name = ld.Name, Value = ld.Init, Line = ld.Line });
                    else topBody.Add(s);
                }
                EmitHandler("state_entry", new List<string>(), topBody, topBody[0].Line);
            }

            // ---- event handlers (Form-1 global functions) ----
            foreach (var f in events)
                EmitHandler(f.Name, f.Params, f.Body, f.Line);

            // ---- LLEvents:on dispatchers (Form-2) for events without a Form-1 handler ----
            foreach (var ev in _lleventsUsed)
            {
                if (form1Events.Contains(ev)) continue; // Form-1 wins per-event
                if (ev == "state_entry" && execStmts.Count > 0) continue;
                EmitLLEventDispatcher(ev);
            }

            // ---- user functions ----
            foreach (var f in userFuncs)
                EmitUserFunction(f);

            // ---- flattened .def blocks (user functions + anonymous lambdas) ----
            foreach (var def in _lambdaDefs) _sb.Append(def);

            return _sb.ToString();
        }

        // Return count = max arity among the function's `return` statements (0 if none).
        private static int AnalyzeReturnCount(List<Stmt> body)
        {
            int max = 0;
            void Walk(List<Stmt> list)
            {
                foreach (var s in list)
                {
                    if (s is ReturnStmt r) max = Math.Max(max, r.Values.Count);
                    else if (s is IfStmt ifs) { Walk(ifs.Then); if (ifs.Else != null) Walk(ifs.Else); }
                    else if (s is WhileStmt w) Walk(w.Body);
                    else if (s is ForIn fi) Walk(fi.Body);
                    else if (s is ForNum fn) Walk(fn.Body);
                }
            }
            Walk(body);
            return max;
        }

        private void EmitUserFunction(FuncDecl f)
        {
            EmitFunctionDef(f.Name, f.Params, f.Body, _userReturns[f.Name], null, null);
        }

        private void EmitHandler(string eventName, List<string> declaredParams, List<Stmt> body, int line)
        {
            var eventArgs = new List<VarType>(_events.GetArguments(eventName));
            if (declaredParams.Count > eventArgs.Count)
                throw new SLuaException("event '" + eventName + "' takes " + eventArgs.Count +
                                        " parameter(s); " + declaredParams.Count + " declared", line);

            _scope = new FuncScope { Parent = null };
            _scope.Captured = CollectCaptured(body);
            _scope.Returns = 0; // events return no values
            for (int i = 0; i < declaredParams.Count; i++)
                _scope.Locals[declaredParams[i]] = new LocalVar { Slot = i, Type = eventArgs[i], IsCell = _scope.Captured.Contains(declaredParams[i]) };
            _scope.NextSlot = eventArgs.Count;

            // Two-pass: emit body to a temp buffer, then emit the .evt header (precedes the body).
            StringBuilder outer = _sb;
            StringBuilder bodyBuf = new StringBuilder();
            _sb = bodyBuf;
            foreach (var p in declaredParams) { var lv = _scope.Locals[p]; if (lv.IsCell) { Line("load " + lv.Slot); Line("mkcell"); Line("store " + lv.Slot); } }
            try { EmitBlock(body); }
            finally { _sb = outer; }

            int innerLocals = _scope.NextSlot - eventArgs.Count;
            Line(".evt default/" + eventName + ": args=" + eventArgs.Count + ", locals=" + innerLocals);
            _sb.Append(bodyBuf.ToString());
            Line("ret");
            Line("");
            _scope = null;
        }

        private void EmitBlock(List<Stmt> stmts)
        {
            foreach (var s in stmts) EmitStmt(s);
        }

        private void EmitStmt(Stmt s)
        {
            switch (s)
            {
                case LocalDecl ld:
                {
                    LocalVar lv = _scope.Locals.TryGetValue(ld.Name, out var ex) ? ex : AllocLocal(ld.Name, Dynamic);
                    if (lv.IsCell)
                    {
                        // pre-create the cell (so a self-referencing init / later capture share it),
                        // then assign the value through it
                        Line("pushnil"); Line("mkcell"); Line("store " + lv.Slot);
                        Line("load " + lv.Slot); EmitExpr(ld.Init); Line("cellput");
                    }
                    else
                    {
                        VarType t = EmitExpr(ld.Init); lv.Type = t; Line("store " + lv.Slot);
                    }
                    break;
                }
                case Assign a:
                    EmitAssignName(a.Name, () => EmitExpr(a.Value), a.Line);
                    break;
                case IndexAssign ia:
                {
                    EmitExpr(ia.Target);   // table
                    EmitExpr(ia.Key);      // key (float number keys normalized to int in the VM)
                    EmitExpr(ia.Value);    // value (stored with its natural type)
                    Line("tabset");
                    break;
                }
                case TableInsert ti:
                    EmitTableInsert(ti);
                    break;
                case ExprStmt es:
                    EmitLlCall(es.Call, statementLevel: true);
                    break;
                case CallStmt cs:
                    EmitCallStmt(cs);
                    break;
                case IfStmt ifs:
                    EmitIf(ifs);
                    break;
                case WhileStmt w:
                    EmitWhile(w);
                    break;
                case ForIn fi:
                    EmitForIn(fi);
                    break;
                case ForNum fn:
                    EmitForNum(fn);
                    break;
                case LocalMulti lm:
                    EmitLocalMulti(lm);
                    break;
                case AssignMulti am:
                    EmitAssignMulti(am);
                    break;
                case ReturnStmt r:
                    EmitValuesAdjusted(r.Values, _scope.Returns, r.Line); // adjust to this fn's return count
                    Line("ret");
                    break;
                case FuncDecl fd:
                    // nested named function -> treat as a local function value
                    EmitStmt(new LocalDecl { Name = fd.Name, Init = new FuncExpr { Params = fd.Params, Body = fd.Body, Line = fd.Line }, Line = fd.Line });
                    break;
                default:
                    throw new SLuaException("unsupported statement", s.Line);
            }
        }

        private void EmitTableInsert(TableInsert ti)
        {
            // table.insert(t, v) == t[#t+1] = v. Tier-2: t must be a simple name (evaluated twice).
            if (!(ti.Table is NameRef))
                throw new SLuaException("table.insert's first argument must be a simple variable in the Tier-2 subset", ti.Line);

            EmitExpr(ti.Table);   // table (for tabset, deepest on stack)
            EmitExpr(ti.Table);   // table (for tablen)
            Line("tablen");       // -> int length
            Line("iconst 1");
            Line("iadd");         // key = #t + 1 (int)
            EmitExpr(ti.Value);   // value
            Line("tabset");
        }

        private void EmitForIn(ForIn f)
        {
            if (f.Gmatch) { EmitForGmatch(f); return; }
            // for k [, v] in pairs(t) do ... end  (insertion-ordered next() protocol)
            int tSlot = AllocTempLocal();   // _t : the table
            int kSlot = AllocTempLocal();   // _k : iteration cursor
            int var0 = AllocLoopVar(f.Vars[0], f.Line);
            int var1 = (f.Vars.Count >= 2) ? AllocLoopVar(f.Vars[1], f.Line) : -1;

            EmitExpr(f.TableExpr);
            Line("store " + tSlot);   // _t = table
            Line("pushnil");
            Line("store " + kSlot);   // _k = nil (start)

            string top = NewLabel("forin");
            string end = NewLabel("forend");
            Label(top);
            Line("load " + tSlot);
            Line("load " + kSlot);
            Line("tabnext");          // stack: [nextValue, nextKey]
            Line("store " + kSlot);   // _k = nextKey (top)
            if (var1 >= 0) Line("store " + var1);  // v = nextValue
            else Line("pop");                      // (single-var for: discard value)
            Line("load " + kSlot);
            Line("isnil");
            Line("brt " + end);       // cursor exhausted -> done
            Line("load " + kSlot);
            Line("store " + var0);    // k = _k
            EmitBlock(f.Body);
            Line("jmp " + top);
            Label(end);
        }

        // for v1[..vK] in string.gmatch(s, p) do ... end  (mirrors pairs/tabnext via gmatchnext)
        private void EmitForGmatch(ForIn f)
        {
            var lc = (LibCall)f.TableExpr;
            int itSlot = AllocTempLocal();
            var varSlots = new int[f.Vars.Count];
            for (int i = 0; i < f.Vars.Count; i++) varSlots[i] = AllocLoopVar(f.Vars[i], f.Line);

            int id = LibFuncId(lc.Lib, lc.Fn, lc.Line); // StrGmatch -> single LuaGmatch
            foreach (var a in lc.Args) EmitExpr(a);      // s, p
            Line("luacall " + id + ", " + lc.Args.Count);
            Line("store " + itSlot);

            string top = NewLabel("gm");
            string end = NewLabel("gmend");
            Label(top);
            Line("load " + itSlot);
            Line("gmatchnext " + f.Vars.Count);          // [cap1..capK, 1] or [0]
            Line("brf " + end);                          // pop flag; done when 0
            for (int i = f.Vars.Count - 1; i >= 0; i--) Line("store " + varSlots[i]); // top = capK
            EmitBlock(f.Body);
            Line("jmp " + top);
            Label(end);
        }

        // Numeric for: for i = start, stop [, step] do ... end.
        // Continue condition (no sign branching): (i - stop) * step <= 0.
        private int AllocLoopVar(string name, int line)
        {
            var lv = AllocLocal(name, Dynamic);
            if (lv.IsCell) throw new SLuaException("capturing a loop variable in a nested function is not supported in Tier-2", line);
            return lv.Slot;
        }

        private void EmitForNum(ForNum f)
        {
            int iSlot = AllocLoopVar(f.Var, f.Line);
            int stopSlot = AllocTempLocal();
            int stepSlot = AllocTempLocal();

            EmitExpr(f.Start); Line("store " + iSlot);
            EmitExpr(f.Stop);  Line("store " + stopSlot);
            if (f.Step != null) EmitExpr(f.Step); else Line("fconst 1.0");
            Line("store " + stepSlot);

            string top = NewLabel("fornum");
            string end = NewLabel("fornend");
            Label(top);
            Line("load " + iSlot);
            Line("load " + stopSlot);
            Line("fsub");
            Line("load " + stepSlot);
            Line("fmul");
            Line("fconst 0.0");
            Line("flte");             // (i-stop)*step <= 0 ? 1 : 0
            Line("brf " + end);       // exit when condition false
            EmitBlock(f.Body);
            Line("load " + iSlot);
            Line("load " + stepSlot);
            Line("fadd");
            Line("store " + iSlot);   // i = i + step
            Line("jmp " + top);
            Label(end);
        }

        private void EmitLocalMulti(LocalMulti lm)
        {
            // evaluate RHS (in the OUTER scope) first, then declare + bind the new locals
            EmitValuesAdjusted(lm.Values, lm.Names.Count, lm.Line);  // N values on stack (top = last)
            var lvs = new LocalVar[lm.Names.Count];
            for (int i = 0; i < lm.Names.Count; i++) lvs[i] = AllocLocal(lm.Names[i], Dynamic);
            for (int i = lm.Names.Count - 1; i >= 0; i--)
            {
                if (lvs[i].IsCell) Line("mkcell"); // wrap the value into a fresh cell
                Line("store " + lvs[i].Slot);
            }
        }

        private void EmitAssignMulti(AssignMulti am)
        {
            EmitValuesAdjusted(am.Values, am.Names.Count, am.Line); // N values, top = last
            for (int i = am.Names.Count - 1; i >= 0; i--)
            {
                var r = Resolve(am.Names[i]);
                switch (r.Kind)
                {
                    case RKind.Local:
                        if (r.Local.IsCell) throw new SLuaException("multi-assignment to a captured variable is not supported in Tier-2", am.Line);
                        Line("store " + r.Local.Slot); break;
                    case RKind.Upval: Line("setupval " + r.UpvalIdx); break;
                    case RKind.Global: Line("gstore " + r.Global.Slot); break;
                    default: throw new SLuaException("assignment to undeclared variable '" + am.Names[i] + "'", am.Line);
                }
            }
        }

        // Emit a value list leaving exactly `target` values on the stack. Only the LAST expression
        // expands to its full multiplicity (a call's results); earlier ones adjust to 1.
        private void EmitValuesAdjusted(List<Expr> values, int target, int line)
        {
            int n = values.Count;
            if (n == 0) { for (int k = 0; k < target; k++) Line("pushnil"); return; }
            for (int i = 0; i < n - 1; i++) EmitExpr(values[i]); // earlier exprs: 1 value each
            int earlier = n - 1;
            Expr last = values[n - 1];
            if (IsCallExpr(last))
            {
                int need = target - earlier;
                if (need >= 0) EmitCallTo(last, need);
                else { EmitCallTo(last, 0); for (int k = 0; k < -need; k++) Line("pop"); }
            }
            else
            {
                EmitExpr(last);                       // 1 value
                int produced = earlier + 1;
                if (produced > target) for (int k = 0; k < produced - target; k++) Line("pop");
                else for (int k = 0; k < target - produced; k++) Line("pushnil");
            }
        }

        private void EmitCallStmt(CallStmt cs)
        {
            switch (cs.Call)
            {
                case LlCall ll: EmitLlCall(ll, statementLevel: true); break;
                case LibCall lc when IsMultiLib(lc): EmitCallTo(lc, 0); break;  // discard
                case LibCall lc2: EmitLibCall(lc2); Line("pop"); break;         // single-result: discard
                case UserCall uc: EmitCallTo(uc, 0); break;                     // discard all returns
                case MethodCall mc: { var t = EmitMethodCall(mc); if (t != VarType.Void) Line("pop"); break; }
                case CoreCall cc: EmitCoreCall(cc, 0); break;          // discard result(s)
                case CallExpr ce: EmitCallTo(ce, 0); break;            // discard result(s)
                default: throw new SLuaException("invalid call statement", cs.Line);
            }
        }

        private VarType EmitLibCall(LibCall lc)
        {
            if (IsMultiLib(lc)) { EmitCallTo(lc, 1); return Dynamic; }
            // table.sort needs a comparator closure invoked by the VM -> dedicated 'luasort' op.
            if (lc.Lib == "table" && lc.Fn == "sort")
            {
                if (lc.Args.Count < 1) throw new SLuaException("table.sort expects (table [, comparator])", lc.Line);
                EmitExpr(lc.Args[0]);                               // the table
                if (lc.Args.Count >= 2) EmitExpr(lc.Args[1]); else Line("pushnil"); // comparator or nil
                Line("luasort");                                    // sorts in place, pushes the table back
                return Dynamic;
            }
            int id = LibFuncId(lc.Lib, lc.Fn, lc.Line);
            foreach (var a in lc.Args) EmitExpr(a);
            Line("luacall " + id + ", " + lc.Args.Count);
            return Dynamic;
        }

        private VarType EmitLibValue(LibValue lv)
        {
            if (lv.Lib == "math" && lv.Name == "pi") { Line("fconst 3.14159265358979"); return VarType.Float; }
            if (lv.Lib == "math" && lv.Name == "huge") { Line("luacall " + (int)LuaLib.Func.MathHuge + ", 0"); return Dynamic; }
            throw new SLuaException("unsupported library value '" + lv.Lib + "." + lv.Name + "'", lv.Line);
        }

        private static int LibFuncId(string lib, string fn, int line)
        {
            string key = lib + "." + fn;
            switch (key)
            {
                case "string.format": return (int)LuaLib.Func.StrFormat;
                case "string.sub":    return (int)LuaLib.Func.StrSub;
                case "string.len":    return (int)LuaLib.Func.StrLen;
                case "string.upper":  return (int)LuaLib.Func.StrUpper;
                case "string.lower":  return (int)LuaLib.Func.StrLower;
                case "string.rep":    return (int)LuaLib.Func.StrRep;
                case "string.byte":   return (int)LuaLib.Func.StrByte;
                case "string.char":   return (int)LuaLib.Func.StrChar;
                case "math.floor":    return (int)LuaLib.Func.MathFloor;
                case "math.ceil":     return (int)LuaLib.Func.MathCeil;
                case "math.abs":      return (int)LuaLib.Func.MathAbs;
                case "math.min":      return (int)LuaLib.Func.MathMin;
                case "math.max":      return (int)LuaLib.Func.MathMax;
                case "math.sqrt":     return (int)LuaLib.Func.MathSqrt;
                case "math.random":   return (int)LuaLib.Func.MathRandom;
                case "math.randomseed": return (int)LuaLib.Func.MathRandomSeed;
                case "string.find":   return (int)LuaLib.Func.StrFind;
                case "string.match":  return (int)LuaLib.Func.StrMatch;
                case "string.gsub":   return (int)LuaLib.Func.StrGsub;
                case "string.gmatch": return (int)LuaLib.Func.StrGmatch;
                // ---- conformance pass: math breadth ----
                case "math.sin":      return (int)LuaLib.Func.MathSin;
                case "math.cos":      return (int)LuaLib.Func.MathCos;
                case "math.tan":      return (int)LuaLib.Func.MathTan;
                case "math.asin":     return (int)LuaLib.Func.MathAsin;
                case "math.acos":     return (int)LuaLib.Func.MathAcos;
                case "math.atan":     return (int)LuaLib.Func.MathAtan;
                case "math.atan2":    return (int)LuaLib.Func.MathAtan;   // atan2(y,x) == atan(y,x)
                case "math.exp":      return (int)LuaLib.Func.MathExp;
                case "math.log":      return (int)LuaLib.Func.MathLog;
                case "math.pow":      return (int)LuaLib.Func.MathPow;
                case "math.fmod":     return (int)LuaLib.Func.MathFmod;
                case "math.deg":      return (int)LuaLib.Func.MathDeg;
                case "math.rad":      return (int)LuaLib.Func.MathRad;
                case "math.round":    return (int)LuaLib.Func.MathRound;
                case "math.sign":     return (int)LuaLib.Func.MathSign;
                case "math.clamp":    return (int)LuaLib.Func.MathClamp;
                case "math.modf":     return (int)LuaLib.Func.MathModf;
                case "math.log10":    return (int)LuaLib.Func.MathLog10;
                case "math.sinh":     return (int)LuaLib.Func.MathSinh;
                case "math.cosh":     return (int)LuaLib.Func.MathCosh;
                case "math.tanh":     return (int)LuaLib.Func.MathTanh;
                case "math.noise":    return (int)LuaLib.Func.MathNoise;
                case "math.map":      return (int)LuaLib.Func.MathMap;
                case "math.lerp":     return (int)LuaLib.Func.MathLerp;
                case "math.isnan":    return (int)LuaLib.Func.MathIsNan;
                case "math.isinf":    return (int)LuaLib.Func.MathIsInf;
                case "math.isfinite": return (int)LuaLib.Func.MathIsFinite;
                // ---- conformance pass: string / table breadth ----
                case "string.reverse": return (int)LuaLib.Func.StrReverse;
                case "string.split":   return (int)LuaLib.Func.StrSplit;
                case "table.insert":   return (int)LuaLib.Func.TblInsert;
                case "table.remove":   return (int)LuaLib.Func.TblRemove;
                case "table.concat":   return (int)LuaLib.Func.TblConcat;
                case "table.unpack":   return (int)LuaLib.Func.TblUnpack;
                default:
                    throw new SLuaException("unsupported stdlib function '" + key + "' in the Tier-2 subset", line);
            }
        }

        // find/match/gsub return a runtime-variable number of values (captures).
        private static bool IsMultiLib(LibCall lc)
        {
            return (lc.Lib == "string" && (lc.Fn == "find" || lc.Fn == "match" || lc.Fn == "gsub"))
                || (lc.Lib == "math" && lc.Fn == "modf")
                || (lc.Lib == "table" && lc.Fn == "unpack");
        }

        private void EmitIf(IfStmt ifs)
        {
            string elseL = NewLabel("else");
            EmitCondBool(ifs.Cond);
            Line("brf " + elseL);
            EmitBlock(ifs.Then);
            if (ifs.Else != null && ifs.Else.Count > 0)
            {
                string endL = NewLabel("endif");
                Line("jmp " + endL);
                Label(elseL);
                EmitBlock(ifs.Else);
                Label(endL);
            }
            else
            {
                Label(elseL);
            }
        }

        private void EmitWhile(WhileStmt w)
        {
            string topL = NewLabel("while");
            string endL = NewLabel("wend");
            Label(topL);
            EmitCondBool(w.Cond);
            Line("brf " + endL);
            EmitBlock(w.Body);
            Line("jmp " + topL);
            Label(endL);
        }

        // Emit a condition leaving an integer boolean on the stack (for brf/brt).
        // Emit a condition leaving an int 1/0 on the stack (for brf/brt), using correct Lua
        // truthiness (only nil and false are falsy; 0 and "" are truthy).
        private void EmitCondBool(Expr cond)
        {
            EmitExpr(cond);
            Line("luatruthy");
        }

        // ---- expressions ----
        private VarType EmitExpr(Expr e)
        {
            switch (e)
            {
                case NumberLit n:
                    Line("fconst " + FormatFloat(n.Value));
                    return VarType.Float;
                case StringLit s:
                    Line("sconst \"" + EscapeString(s.Value) + "\"");
                    return VarType.String;
                case BoolLit b:
                    Line(b.Value ? "pushtrue" : "pushfalse");
                    return Dynamic;
                case NameRef nr:
                    return EmitNameRef(nr);
                case Unary u:
                {
                    if (u.Op == "not") { EmitExpr(u.E); Line("lnot"); return Dynamic; }
                    EmitExpr(u.E);
                    Line("luaunm");                  // __unm if table, else numeric negate
                    return Dynamic;
                }
                case Builtin bi:
                    EmitExpr(bi.Arg);
                    Line(bi.Name == "type" ? "luatype" : bi.Name == "tostring" ? "luatostr" : "luatonum");
                    return bi.Name == "tonumber" ? Dynamic : VarType.String;
                case MetaCall mc:
                    return EmitMetaCall(mc);
                case VecCtor vc:
                    return EmitVecCtor(vc);
                case CoreCall cc:
                    EmitCoreCall(cc, 1);
                    return Dynamic;
                case CallExpr ce:
                    EmitCallTo(ce, 1);
                    return Dynamic;
                case Binary bin:
                    return EmitBinary(bin);
                case LlCall c:
                    return EmitLlCall(c, statementLevel: false);
                case NilLit:
                    Line("pushnil");
                    return Dynamic;
                case TableLit tl:
                    return EmitTableLit(tl);
                case Index ix:
                    EmitExpr(ix.Target);   // table
                    EmitExpr(ix.Key);      // key
                    Line("tabget");
                    return Dynamic;        // value type only known at runtime
                case Len len:
                    EmitExpr(len.E);       // table
                    Line("tablen");
                    return VarType.Integer;
                case LibCall lc:
                    return EmitLibCall(lc);
                case LibValue lv:
                    return EmitLibValue(lv);
                case UserCall uc:
                    EmitCallTo(uc, 1);   // single-value context
                    return Dynamic;
                case FuncExpr fe:
                    return EmitFuncExpr(fe);
                case MethodCall mc:
                {
                    var t = EmitMethodCall(mc);
                    if (t == VarType.Void) Line("pushnil"); // method-call as a value must leave 1
                    return Dynamic;
                }
                default:
                    throw new SLuaException("unsupported expression", e.Line);
            }
        }

        private VarType EmitTableLit(TableLit tl)
        {
            int arrayIndex = 1;
            foreach (var field in tl.Fields)
            {
                if (field.Key == null)
                {
                    Line("iconst " + arrayIndex);  // positional key (1-based int)
                    arrayIndex++;
                }
                else
                {
                    EmitExpr(field.Key);           // string name, or [k] expr (normalized in VM)
                }
                EmitExpr(field.Value);             // value stored with its natural type
            }
            Line("buildtable " + tl.Fields.Count);
            return Dynamic;
        }

        private VarType EmitNameRef(NameRef nr)
        {
            return EmitLoadName(nr.Name, nr.Line);
        }

        // Anonymous function expression: emit a flattened .def, then build a closure capturing the
        // resolved upvalue cells from the current (defining) scope.
        private VarType EmitFuncExpr(FuncExpr fe)
        {
            string name = "slua_lam_" + (_lambdaSeq++); // valid assembler identifier (no '$')
            int R = AnalyzeReturnCount(fe.Body);
            var upvals = new List<UpvalDesc>();
            EmitFunctionDef(name, fe.Params, fe.Body, R, _scope, upvals);
            foreach (var ud in upvals)
            {
                if (ud.FromParentLocal) Line("load " + ud.Index);   // parent's cell-local slot holds the cell
                else Line("pushupval " + ud.Index);                 // transitive: current closure's upval cell
            }
            Line("mkclosure " + name + "(), " + upvals.Count);
            return Dynamic;
        }

        // print/error/assert/pcall. `wanted` = how many values the context consumes.
        private void EmitCoreCall(CoreCall cc, int wanted)
        {
            switch (cc.Name)
            {
                case "print":
                    // tostring each arg, tab-separated, -> llOwnerSay (debug/owner channel). Returns nothing.
                    if (cc.Args.Count == 0) Line("sconst \"\"");
                    else
                    {
                        EmitExpr(cc.Args[0]); Line("luatostr");
                        for (int i = 1; i < cc.Args.Count; i++)
                        {
                            Line("sconst \"\\t\""); Line("concat");
                            EmitExpr(cc.Args[i]); Line("luatostr"); Line("concat");
                        }
                    }
                    Line("syscall llOwnerSay()");
                    for (int k = 0; k < wanted; k++) Line("pushnil");   // print returns nothing
                    return;

                case "error":
                    if (cc.Args.Count > 0) EmitExpr(cc.Args[0]); else Line("pushnil");
                    Line("luaerror");                                   // never returns
                    for (int k = 0; k < wanted; k++) Line("pushnil");   // keep stack shape (unreachable)
                    return;

                case "assert":
                {
                    if (cc.Args.Count < 1) throw new SLuaException("assert expects (value [, message])", cc.Line);
                    EmitExpr(cc.Args[0]);                               // [v]
                    Line("dup");                                       // [v, v]
                    Line("luatruthy");                                 // [v, int]
                    string ok = NewLabel("assert");
                    Line("brt " + ok);                                 // pops int; truthy -> ok ([v])
                    Line("pop");                                       // drop v
                    if (cc.Args.Count >= 2) EmitExpr(cc.Args[1]); else Line("sconst \"assertion failed!\"");
                    Line("luaerror");
                    Label(ok);                                         // [v]
                    if (wanted == 0) Line("pop"); else for (int k = 1; k < wanted; k++) Line("pushnil");
                    return;
                }

                case "pcall":
                {
                    if (cc.Args.Count < 1) throw new SLuaException("pcall expects (function [, args...])", cc.Line);
                    EmitExpr(cc.Args[0]);                               // the function value
                    for (int i = 1; i < cc.Args.Count; i++) EmitExpr(cc.Args[i]);
                    Line("luapcall " + (cc.Args.Count - 1));           // pushes exactly 2: (ok, result)
                    if (wanted < 2) for (int k = 0; k < 2 - wanted; k++) Line("pop");
                    else for (int k = 0; k < wanted - 2; k++) Line("pushnil");
                    return;
                }
            }
            throw new SLuaException("unknown core builtin '" + cc.Name + "'", cc.Line);
        }

        // vector(x,y,z) -> buildvec ; rotation/quaternion(x,y,z,s) -> buildrot. Args coerced to float by
        // the VM ops (ConvToFloat). Result is a boxed Vector3/Quaternion (passes the shim's ConvToVector).
        private VarType EmitVecCtor(VecCtor vc)
        {
            int need = vc.IsRot ? 4 : 3;
            if (vc.Args.Count != need)
                throw new SLuaException((vc.IsRot ? "rotation" : "vector") + " expects " + need + " components", vc.Line);
            foreach (var a in vc.Args) EmitExpr(a);
            Line(vc.IsRot ? "buildrot" : "buildvec");
            return vc.IsRot ? VarType.Rotation : VarType.Vector;
        }

        // setmetatable(t, mt) -> t ; getmetatable(t) -> mt|nil
        private VarType EmitMetaCall(MetaCall mc)
        {
            if (mc.Name == "setmetatable")
            {
                if (mc.Args.Count != 2) throw new SLuaException("setmetatable expects (table, metatable)", mc.Line);
                EmitExpr(mc.Args[0]);
                EmitExpr(mc.Args[1]);
                Line("setmeta");
            }
            else // getmetatable
            {
                if (mc.Args.Count != 1) throw new SLuaException("getmetatable expects (table)", mc.Line);
                EmitExpr(mc.Args[0]);
                Line("getmeta");
            }
            return Dynamic;
        }

        private VarType EmitBinary(Binary bin)
        {
            // short-circuit logical operators return a value (not necessarily boolean)
            if (bin.Op == "and" || bin.Op == "or") return EmitAndOr(bin);

            // string concat: '..' coerces number/string operands in the VM
            if (bin.Op == "..")
            {
                EmitExpr(bin.L);
                EmitExpr(bin.R);
                Line("concat");
                return VarType.String;
            }

            // nil comparison: x == nil / x ~= nil
            if ((bin.Op == "==" || bin.Op == "~=") && (bin.L is NilLit || bin.R is NilLit))
            {
                Expr other = (bin.L is NilLit) ? bin.R : bin.L;
                EmitExpr(other);
                Line("isnil");
                Line("tobool");                  // -> boolean (is nil)
                if (bin.Op == "~=") Line("lnot");
                return Dynamic;
            }

            // Lua equality (type-aware: different types are never equal)
            if (bin.Op == "==" || bin.Op == "~=")
            {
                EmitExpr(bin.L);
                EmitExpr(bin.R);
                Line("luaeq");                   // -> boolean
                if (bin.Op == "~=") Line("lnot");
                return Dynamic;
            }

            // arithmetic + relational: 'luabinop <sel>' dispatches a table-operand metamethod
            // (__add..__le, gt/ge swap to __lt/__le) and otherwise does the numeric op. Relational
            // selectors (>=5) already yield a boolean, so no separate 'tobool' is needed.
            EmitExpr(bin.L);
            EmitExpr(bin.R);
            switch (bin.Op)
            {
                case "+": Line("luabinop 0"); return Dynamic;
                case "-": Line("luabinop 1"); return Dynamic;
                case "*": Line("luabinop 2"); return Dynamic;
                case "/": Line("luabinop 3"); return Dynamic;
                case "%": Line("luabinop 4"); return Dynamic;
                case "<": Line("luabinop 5"); return Dynamic;
                case "<=": Line("luabinop 6"); return Dynamic;
                case ">": Line("luabinop 7"); return Dynamic;
                case ">=": Line("luabinop 8"); return Dynamic;
                default: throw new SLuaException("unsupported operator '" + bin.Op + "'", bin.Line);
            }
        }

        // a and b: a if a is falsy, else b.   a or b: a if a is truthy, else b.  (short-circuit)
        private VarType EmitAndOr(Binary bin)
        {
            string skip = NewLabel(bin.Op == "and" ? "and" : "or");
            EmitExpr(bin.L);                                   // [a]
            Line("dup");                                       // [a, a]
            Line("luatruthy");                                 // [a, t]
            Line((bin.Op == "and" ? "brf " : "brt ") + skip);  // and: keep a if falsy; or: keep a if truthy
            Line("pop");                                       // discard a
            EmitExpr(bin.R);                                   // [b]
            Label(skip);
            return Dynamic;
        }

        private VarType EmitLlCall(LlCall c, bool statementLevel)
        {
            string phloxName = "ll" + c.Member; // ll.Say -> llSay
            if (!Defaults.SystemMethods.TryGetValue(phloxName, out FunctionSig sig))
                throw new SLuaException("unknown ll function 'll." + c.Member + "' (-> " + phloxName + ")", c.Line);

            if (c.Args.Count != sig.ParamTypes.Length)
                throw new SLuaException("ll." + c.Member + " expects " + sig.ParamTypes.Length +
                                        " argument(s), got " + c.Args.Count, c.Line);

            for (int i = 0; i < c.Args.Count; i++)
            {
                VarType at = EmitExpr(c.Args[i]);
                Coerce(at, sig.ParamTypes[i], c.Line);
            }
            Line("syscall " + phloxName + "()");

            if (sig.ReturnType == VarType.Void)
            {
                if (!statementLevel)
                    throw new SLuaException("ll." + c.Member + " returns no value; cannot use in an expression", c.Line);
                return VarType.Void;
            }
            if (statementLevel) Line("pop"); // discard unused return value
            return sig.ReturnType;
        }

        // ---- coercion (uses existing cast opcodes; NO VM change) ----
        private void Coerce(VarType from, VarType to, int line)
        {
            if (from == to) return;
            if (from == Dynamic || to == Dynamic) return;   // VM coerces at consumption (no static cast)
            if (to == VarType.Integer && from == VarType.Float) { Line("icast"); return; }
            if (to == VarType.Float && from == VarType.Integer) { Line("fcast"); return; }
            // Remaining conversions (e.g. number<->string) are performed by the VM ops / syscall shim
            // at the point of consumption (ConvToInt/ConvToFloat/ConvToStr). Emit nothing rather than
            // fail, so the value flows to its coercing consumer.
        }

        // ---- emit helpers ----
        private void Line(string s) { _sb.Append(s); _sb.Append('\n'); }
        private void Label(string name) { _sb.Append(name); _sb.Append(":\n"); }
        private string NewLabel(string tag) { return "sl_" + tag + "_" + (_labelCounter++); }

        private static string FormatFloat(double d)
        {
            // Must match the assembler FLOAT token (INT '.' INT*); no exponent form.
            string s = d.ToString("0.0###############", CultureInfo.InvariantCulture);
            if (s.IndexOf('.') < 0) s += ".0";
            return s;
        }

        private static string EscapeString(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\r': break; // assembler STRING has no \r escape; drop
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
