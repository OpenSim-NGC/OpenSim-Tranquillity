using System;
using System.Collections.Generic;
using System.Globalization;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using Antlr4.StringTemplate;

namespace InWorldz.Phlox.Compiler
{
    /// <summary>
    /// Fourth (final) compiler pass: walks the ANTLR4 parse tree and emits
    /// Phlox bytecode text using ByteCodeEmitter (replaces ByteCode.stg).
    ///
    /// Pass order: DefVisitor → TypesVisitor → AnalyzeVisitor → GenVisitor
    /// </summary>
    public class GenVisitor : LSLBaseVisitor<string>
    {
        private readonly SymbolTable _symtab;
        private readonly LSLNodeAnnotations _annotations;

        private int _labelId = 0;
        private int _errorCount = 0;
        private const int MaxErrors = 10;

        public GenVisitor(SymbolTable symtab, LSLNodeAnnotations annotations, TemplateGroup templates)
        {
            _symtab      = symtab      ?? throw new ArgumentNullException(nameof(symtab));
            _annotations = annotations ?? throw new ArgumentNullException(nameof(annotations));
            // templates parameter retained for API compatibility — ByteCodeEmitter is used instead.
        }

        public string Generate(IParseTree tree) => Visit(tree) ?? string.Empty;

        // ── Helpers ───────────────────────────────────────────────────────────

        private string NextLabel(string prefix) => $"{prefix}{_labelId++}";

        private void Error(string msg)
        {
            _symtab.StatusListener.Error(msg);
            if (++_errorCount >= MaxErrors)
                throw new Types.TooManyErrorsException("Too many errors", null);
        }

        private static string FormatFloat(string text)
        {
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                return f.ToString("0.0##############", CultureInfo.InvariantCulture);
            return text;
        }

        private string DoPromotion(IParseTree node, string st)
        {
            ISymbolType promote = _annotations.GetPromoteToType(node);
            if (promote == SymbolTable.FLOAT)
                return ByteCodeEmitter.FCast(st);
            return st;
        }

        private int Idx(ISymbolType t) => t?.TypeIndex ?? (int)Types.VarType.Void;
        private ISymbolType EvalType(IParseTree node) => _annotations.GetEvalType(node);
        private Symbol GetSymbol(IParseTree node) => _annotations.GetSymbol(node);

        private static string CalcSubIndex(string sub)
        {
            switch (sub)
            {
                case "x": return "0";
                case "y": return "1";
                case "z": return "2";
                case "s": return "3";
                default:  throw new Exception($"Invalid subscript '{sub}'");
            }
        }

        // ── Top-level script ──────────────────────────────────────────────────

        public override string VisitProg([NotNull] LSLParser.ProgContext context)
        {
            var globals   = new List<string>();
            var functions = new List<string>();
            var states    = new List<string>();

            foreach (var gs in context.globalStmt())
            {
                if (gs.funcDef() != null)
                {
                    string s = Visit(gs.funcDef());
                    if (s != null) functions.Add(s);
                }
                else if (gs.stateDef() != null)
                {
                    string s = Visit(gs.stateDef());
                    if (s != null) states.Add(s);
                }
                else if (gs.varDecl() != null)
                {
                    string s = VisitGlobalVarDecl(gs.varDecl());
                    if (s != null) globals.Add(s);
                }
            }

            return ByteCodeEmitter.File(_symtab.NumGlobals, _symtab.States,
                globals, functions, states);
        }

        // ── State definitions ─────────────────────────────────────────────────

        public override string VisitNamedStateDef([NotNull] LSLParser.NamedStateDefContext context)
            => VisitStateBlock(context.stateBlock());

        public override string VisitDefaultStateDef([NotNull] LSLParser.DefaultStateDefContext context)
            => VisitStateBlock(context.stateBlock());

        private string VisitStateBlock(LSLParser.StateBlockContext block)
        {
            var events = new List<string>();
            if (block != null)
                foreach (var sbc in block.stateBlockContent())
                {
                    string s = Visit(sbc);
                    if (s != null) events.Add(s);
                }
            return ByteCodeEmitter.Dump(events);
        }

        // ── Event definitions ─────────────────────────────────────────────────

        public override string VisitEventDef([NotNull] LSLParser.EventDefContext context)
        {
            var es = GetSymbol(context) as EventSymbol;
            if (es == null) return string.Empty;

            var content = new List<string>();
            if (context.funcBlock() != null)
                foreach (var fbc in context.funcBlock().funcBlockContent())
                {
                    string s = Visit(fbc);
                    if (s != null) content.Add(s);
                }

            int locals = es.CurrentVariableIndex - es.Members.Count;
            return ByteCodeEmitter.EventDef(es.FullEventName, es.Members.Count,
                locals < 0 ? 0 : locals, content);
        }

        // ── Function definitions ──────────────────────────────────────────────

        public override string VisitFuncDef([NotNull] LSLParser.FuncDefContext context)
        {
            var ms = GetSymbol(context) as MethodSymbol;
            if (ms == null) return string.Empty;

            var content = new List<string>();
            if (context.funcBlock() != null)
                foreach (var fbc in context.funcBlock().funcBlockContent())
                {
                    string s = Visit(fbc);
                    if (s != null) content.Add(s);
                }

            int locals = ms.CurrentVariableIndex - ms.Members.Count;
            return ByteCodeEmitter.MethodDef(ms.RawName, ms.Members.Count,
                locals < 0 ? 0 : locals, content);
        }

        // ── Global variable declarations ──────────────────────────────────────

        public override string VisitGlobalStmt([NotNull] LSLParser.GlobalStmtContext context)
        {
            if (context.varDecl() != null) return VisitGlobalVarDecl(context.varDecl());
            return Visit(context.GetChild(0));
        }

        private string VisitGlobalVarDecl(LSLParser.VarDeclContext context)
        {
            var sym = GetSymbol(context) as VariableSymbol;
            if (sym == null) return string.Empty;

            if (context.expression() != null)
                return ByteCodeEmitter.GStore(GenExpression(context.expression()), sym.ScopeIndex);
            else
                return ByteCodeEmitter.GInit(TemplateMapping.Init[sym.Type.TypeIndex], sym.ScopeIndex);
        }

        // ── Local variable declarations ───────────────────────────────────────

        public override string VisitVarDeclStmt([NotNull] LSLParser.VarDeclStmtContext context)
            => VisitLocalVarDecl(context.varDecl());

        private string VisitLocalVarDecl(LSLParser.VarDeclContext context)
        {
            var sym = GetSymbol(context) as VariableSymbol;
            if (sym == null) return string.Empty;

            if (context.expression() != null)
                return ByteCodeEmitter.LStore(GenExpression(context.expression()), sym.ScopeIndex);
            else
                return ByteCodeEmitter.LInit(TemplateMapping.Init[sym.Type.TypeIndex], sym.ScopeIndex);
        }

        // ── Return / label / jump / state change ──────────────────────────────

        public override string VisitReturnStmt([NotNull] LSLParser.ReturnStmtContext context)
        {
            string expr = context.expression() != null ? GenExpression(context.expression()) : null;
            return ByteCodeEmitter.Return(expr);
        }

        public override string VisitLabelStmt([NotNull] LSLParser.LabelStmtContext context)
            => Visit(context.label_());

        public override string VisitLabel_([NotNull] LSLParser.Label_Context context)
        {
            var sym = GetSymbol(context) as LabelSymbol;
            if (sym == null) return string.Empty;
            return ByteCodeEmitter.Label(sym.DecoratedName);
        }

		public override string VisitJumpStmt([NotNull] LSLParser.JumpStmtContext context)
		{
			string labelName = context.ID().GetText();
			IScope scope = null;
			IParseTree node = context;
			while (node != null && scope == null)
			{
				scope = _annotations.GetScope(node);
				node = node.Parent;
			}
			var sym = scope?.Resolve("@" + labelName) as LabelSymbol;
			if (sym == null) { Error($"Undefined label '{labelName}'"); return string.Empty; }
			return ByteCodeEmitter.Jump(sym.DecoratedName);
		}

        public override string VisitStateChangeStmt([NotNull] LSLParser.StateChangeStmtContext context)
            => ByteCodeEmitter.StateChange(context.ID()?.GetText());

        // ── If / while / for / do-while ───────────────────────────────────────

        public override string VisitIfStmt([NotNull] LSLParser.IfStmtContext context)
        {
            string evalExpr = GenExpression(context.expression());
            bool needsBoolEval = EvalType(context.expression()) != SymbolTable.INT;
            var stmts = context.statement();
            string ifBody   = stmts.Length > 0 ? Visit(stmts[0]) : string.Empty;
            string elseBody = stmts.Length > 1 ? Visit(stmts[1]) : null;
            return ByteCodeEmitter.IfElse(evalExpr, ifBody, elseBody,
                NextLabel("if_end_"), NextLabel("if_else_"), needsBoolEval);
        }

        public override string VisitWhileStmt([NotNull] LSLParser.WhileStmtContext context)
        {
            string evalExpr = GenExpression(context.expression());
            bool needsBoolEval = EvalType(context.expression()) != SymbolTable.INT;
            string stmt = Visit(context.statement());
            return ByteCodeEmitter.While(evalExpr, stmt,
                NextLabel("while_start_"), NextLabel("while_out_"), needsBoolEval);
        }

        public override string VisitDoWhileStmt([NotNull] LSLParser.DoWhileStmtContext context)
        {
            string stmt = Visit(context.statement());
            string evalExpr = GenExpression(context.expression());
            bool needsBoolEval = EvalType(context.expression()) != SymbolTable.INT;
            return ByteCodeEmitter.DoWhile(evalExpr, stmt,
                NextLabel("do_while_start_"), NextLabel("do_while_out_"), needsBoolEval);
        }

		public override string VisitForStmt([NotNull] LSLParser.ForStmtContext context)
		{
			string initExpr = context.init != null ? Visit(context.init) : null;
			string condExpr = context.cond != null ? Visit(context.cond) : null;
			string loopExpr = context.loop != null ? GenExpression(context.loop) : null;
			string body = Visit(context.statement());
			ISymbolType condType = context.cond != null ? EvalType(context.cond) : null;
			bool needsBoolEval = condType != null && condType != SymbolTable.INT;
			return ByteCodeEmitter.ForLoop(initExpr, condExpr, loopExpr, body,
				NextLabel("forloop_start_"), NextLabel("forloop_out_"), needsBoolEval);
		}

        // ── Blocks ────────────────────────────────────────────────────────────

        public override string VisitFuncBlock([NotNull] LSLParser.FuncBlockContext context)
        {
            var parts = new List<string>();
            foreach (var fbc in context.funcBlockContent())
            {
                string s = Visit(fbc);
                if (s != null) parts.Add(s);
            }
            return ByteCodeEmitter.Dump(parts);
        }

        public override string VisitAnonBlock([NotNull] LSLParser.AnonBlockContext context)
            => Visit(context.funcBlock());

        public override string VisitSemiStmt([NotNull] LSLParser.SemiStmtContext context)
            => null;

        // ── Assignment statements ─────────────────────────────────────────────

        public override string VisitAssignmentStmt([NotNull] LSLParser.AssignmentStmtContext context)
        {
            string op = context.op?.Text ?? "=";
            string rhsCode = GenExpression(context.expression());
            VariableSymbol varSym = ResolveLhsSymbol(context.lhs(), out string subIdx);
            // The grammar puts ('.' subscript=ID) OUTSIDE the lhs() rule, so ResolveLhsSymbol
            // never sees it.  Pick it up from the labeled token on the statement context.
            if (subIdx == null && context.subscript != null)
                subIdx = CalcSubIndex(context.subscript.Text);
            if (varSym == null) { Error("Invalid assignment target"); return string.Empty; }

            if (op == "=")
                return subIdx != null
                    ? ByteCodeEmitter.SubAssign(varSym.IsGlobal, varSym.ScopeIndex, subIdx, rhsCode, false)
                    : ByteCodeEmitter.Assign(varSym.IsGlobal, varSym.ScopeIndex, rhsCode, false);
            else
            {
                string subtemplate = GetCompoundAssignTemplate(op, varSym.Type,
                    EvalType(context.expression()), subIdx);
                return ByteCodeEmitter.CompoundAssignOp(subtemplate, varSym.IsGlobal,
                    varSym.ScopeIndex, subIdx, rhsCode, false);
            }
        }

        // ── Inc/dec statements ────────────────────────────────────────────────

        public override string VisitPostIncrementStmt([NotNull] LSLParser.PostIncrementStmtContext context)
        {
            var (sym, subIdx, type) = ResolveStmtIncDec(context.ID(), context.subscript);
            return ByteCodeEmitter.Pop(GenPostIncDecDirect(sym, subIdx, type, true));
        }

        public override string VisitPostDecrementStmt([NotNull] LSLParser.PostDecrementStmtContext context)
        {
            var (sym, subIdx, type) = ResolveStmtIncDec(context.ID(), context.subscript);
            return ByteCodeEmitter.Pop(GenPostIncDecDirect(sym, subIdx, type, false));
        }

        public override string VisitPreIncrementStmt([NotNull] LSLParser.PreIncrementStmtContext context)
        {
            var (sym, subIdx, type) = ResolveStmtIncDec(context.ID(), context.subscript);
            return ByteCodeEmitter.Pop(GenPreIncDecDirect(sym, subIdx, type, true));
        }

        public override string VisitPreDecrementStmt([NotNull] LSLParser.PreDecrementStmtContext context)
        {
            var (sym, subIdx, type) = ResolveStmtIncDec(context.ID(), context.subscript);
            return ByteCodeEmitter.Pop(GenPreIncDecDirect(sym, subIdx, type, false));
        }

        public override string VisitFuncCallStmt([NotNull] LSLParser.FuncCallStmtContext context)
            => Visit(context.funcCall());

        // ── Expressions ───────────────────────────────────────────────────────

        private string GenExpression(LSLParser.ExpressionContext context)
            => DoPromotion(context, Visit(context));

        public override string VisitExpression([NotNull] LSLParser.ExpressionContext context)
            => Visit(context.expr());

        public override string VisitExpr([NotNull] LSLParser.ExprContext context)
            => DoPromotion(context, GenAssignmentExpr(context.assignmentExpression(), true));

        public override string VisitAssignmentExpression(
            [NotNull] LSLParser.AssignmentExpressionContext context)
            => DoPromotion(context, GenAssignmentExpr(context, true));

        private string GenAssignmentExpr(LSLParser.AssignmentExpressionContext ctx, bool pushFinal)
        {
            var assigns = ctx.assignmentExpression();
            if (assigns.Length == 0)
                return DoPromotion(ctx, GenBooleanExpr(ctx.booleanExpression()));

		string op = GetAssignOpText(ctx);
		if (string.IsNullOrEmpty(op) || ctx.assignmentExpression(1) == null)
			return DoPromotion(ctx, GenBooleanExpr(ctx.booleanExpression()));

		ISymbolType rhsType = EvalType(ctx.assignmentExpression(1));
		string rhsCode = GenAssignmentExpr(ctx.assignmentExpression(1), true);
		VariableSymbol varSym = WalkForVarSym(ctx.assignmentExpression(0), out string subIdx);
		
            if (varSym == null) { Error("Invalid assignment target"); return string.Empty; }

            if (op == "=")
                return subIdx != null
                    ? ByteCodeEmitter.SubAssign(varSym.IsGlobal, varSym.ScopeIndex, subIdx, rhsCode, pushFinal)
                    : ByteCodeEmitter.Assign(varSym.IsGlobal, varSym.ScopeIndex, rhsCode, pushFinal);
            else
            {
                string subtemplate = GetCompoundAssignTemplate(op, varSym.Type, rhsType, subIdx);
                return ByteCodeEmitter.CompoundAssignOp(subtemplate, varSym.IsGlobal,
                    varSym.ScopeIndex, subIdx, rhsCode, pushFinal);
            }
        }

        // ── Boolean / bitwise / equality / relational ─────────────────────────

        private string GenBooleanExpr(LSLParser.BooleanExpressionContext ctx)
            => DoPromotion(ctx, Visit(ctx));

        public override string VisitBooleanExpression(
            [NotNull] LSLParser.BooleanExpressionContext context)
        {
            var children = context.bitwiseExpression();
            if (children.Length == 1) return DoPromotion(context, Visit(children[0]));
            string op = GetBinaryOpText(context);
            return ByteCodeEmitter.BinaryOp(op == "&&" ? "booland" : "boolor",
                Visit(children[0]), Visit(children[1]));
        }

        public override string VisitBitwiseExpression(
            [NotNull] LSLParser.BitwiseExpressionContext context)
        {
            var children = context.equalityExpression();
            if (children.Length == 1) return DoPromotion(context, Visit(children[0]));
            string op = GetBinaryOpText(context);
            string tname = op == "|" ? "bitor" : op == "&" ? "bitand" : "bitxor";
            string result = Visit(children[0]);
            for (int i = 1; i < children.Length; i++)
            {
                result = ByteCodeEmitter.BinaryOp(tname, result, Visit(children[i]));
            }
            return DoPromotion(context, result);
        }

        public override string VisitEqualityExpression(
            [NotNull] LSLParser.EqualityExpressionContext context)
        {
            var children = context.relationalExpression();
            if (children.Length == 1) return DoPromotion(context, Visit(children[0]));

            string result = Visit(children[0]);
            ISymbolType lType = EvalType(children[0]);
            for (int i = 1; i < children.Length; i++)
            {
                ISymbolType rType = EvalType(children[i]);
                string op = GetBinaryOpTextAt(context, i);
                string subtemplate = op == "=="
                    ? TemplateMapping.Equality[Idx(lType), Idx(rType)]
                    : TemplateMapping.Inequality[Idx(lType), Idx(rType)];
                result = ByteCodeEmitter.BinaryOp(subtemplate, result, Visit(children[i]));
                lType = SymbolTable.INT; // == and != always produce integer
            }
            return DoPromotion(context, result);
        }

        public override string VisitRelationalExpression(
            [NotNull] LSLParser.RelationalExpressionContext context)
        {
            var children = context.binaryBitwiseExpression();
            if (children.Length == 1) return DoPromotion(context, Visit(children[0]));

            string result = Visit(children[0]);
            ISymbolType lType = EvalType(children[0]);
            for (int i = 1; i < children.Length; i++)
            {
                ISymbolType rType = EvalType(children[i]);
                string op = GetBinaryOpTextAt(context, i);
                string subtemplate;
                switch (op)
                {
                    case "<":  subtemplate = TemplateMapping.LTCompare[Idx(lType), Idx(rType)]; break;
                    case ">":  subtemplate = TemplateMapping.GTCompare[Idx(lType), Idx(rType)]; break;
                    case "<=": subtemplate = TemplateMapping.LTECompare[Idx(lType), Idx(rType)]; break;
                    case ">=": subtemplate = TemplateMapping.GTECompare[Idx(lType), Idx(rType)]; break;
                    default:   Error($"Unknown relational op '{op}'"); return string.Empty;
                }
                result = ByteCodeEmitter.BinaryOp(subtemplate, result, Visit(children[i]));
                lType = SymbolTable.INT; // comparisons always produce integer
            }
            return DoPromotion(context, result);
        }

        public override string VisitBinaryBitwiseExpression(
            [NotNull] LSLParser.BinaryBitwiseExpressionContext context)
        {
            var children = context.additiveExpression();
            if (children.Length == 1) return DoPromotion(context, Visit(children[0]));

            string result = Visit(children[0]);
            for (int i = 1; i < children.Length; i++)
            {
                string op = GetBinaryOpTextAt(context, i);
                result = ByteCodeEmitter.BinaryOp(op == "<<" ? "lshift" : "rshift",
                    result, Visit(children[i]));
            }
            return DoPromotion(context, result);
        }

        // ── Additive / multiplicative ─────────────────────────────────────────

        public override string VisitAdditiveExpression(
            [NotNull] LSLParser.AdditiveExpressionContext context)
        {
            var children = context.multiplicativeExpression();
            if (children.Length == 1) return DoPromotion(context, Visit(children[0]));

            string result = Visit(children[0]);
            ISymbolType lType = EvalType(children[0]);
            var minusTokens = context.MINUS();

            for (int i = 1; i < children.Length; i++)
            {
                ISymbolType rType = EvalType(children[i]);
                bool isMinus = minusTokens != null && (i - 1) < minusTokens.Length;
                string subtemplate = isMinus
                    ? TemplateMapping.Subtraction[Idx(lType), Idx(rType)]
                    : TemplateMapping.Addition[Idx(lType), Idx(rType)];
                result = ByteCodeEmitter.BinaryOp(subtemplate, result, Visit(children[i]));
                lType = EvalType(context);
            }
            return DoPromotion(context, result);
        }

        public override string VisitMultiplicativeExpression(
            [NotNull] LSLParser.MultiplicativeExpressionContext context)
        {
            var children = context.unaryExpression();
            if (children.Length == 1) return DoPromotion(context, Visit(children[0]));

            string result = Visit(children[0]);
            ISymbolType lType = EvalType(children[0]);

            for (int i = 1; i < children.Length; i++)
            {
                ISymbolType rType = EvalType(children[i]);
                string op = GetMultOpText(context, i);
                string subtemplate = op == "%" ? TemplateMapping.Mod[Idx(lType), Idx(rType)]
                    : op == "/" ? TemplateMapping.Division[Idx(lType), Idx(rType)]
                    : TemplateMapping.Multiplication[Idx(lType), Idx(rType)];
                result = ByteCodeEmitter.BinaryOp(subtemplate, result, Visit(children[i]));
                lType = EvalType(context);
            }
            return DoPromotion(context, result);
        }

        // ── Unary ─────────────────────────────────────────────────────────────

        public override string VisitUnaryMinus([NotNull] LSLParser.UnaryMinusContext context)
        {
            ISymbolType t = EvalType(context.unaryExpression());
            string expr = Visit(context.unaryExpression());
            string result = t == SymbolTable.INT    ? ByteCodeEmitter.INeg(expr)
                          : t == SymbolTable.FLOAT  ? ByteCodeEmitter.FNeg(expr)
                          : t == SymbolTable.VECTOR ? ByteCodeEmitter.VNeg(expr)
                          : ByteCodeEmitter.RNeg(expr);
            return DoPromotion(context, result);
        }

        public override string VisitUnaryBoolNot([NotNull] LSLParser.UnaryBoolNotContext context)
            => ByteCodeEmitter.ILNot(Visit(context.unaryExpression()));

        public override string VisitUnaryBitNot([NotNull] LSLParser.UnaryBitNotContext context)
            => ByteCodeEmitter.UBitNot(Visit(context.unaryExpression()));

        public override string VisitTypeCastExpr([NotNull] LSLParser.TypeCastExprContext context)
            => DoPromotion(context, VisitChildren(context));

        public override string VisitTypeCast([NotNull] LSLParser.TypeCastContext context)
        {
            string expr = Visit(context.unaryExpression());
            ISymbolType dest = EvalType(context);
            string result = dest == SymbolTable.INT      ? ByteCodeEmitter.ICast(expr)
                          : dest == SymbolTable.FLOAT    ? ByteCodeEmitter.FCast(expr)
                          : dest == SymbolTable.VECTOR   ? ByteCodeEmitter.VCast(expr)
                          : dest == SymbolTable.ROTATION ? ByteCodeEmitter.RCast(expr)
                          : dest == SymbolTable.LIST     ? ByteCodeEmitter.LCast(expr)
                          : ByteCodeEmitter.SCast(expr);
            return DoPromotion(context, result);
        }

        // ── Pre/post inc/dec (expression context) ─────────────────────────────

        public override string VisitPreIncrement([NotNull] LSLParser.PreIncrementContext context)
        {
            var (sym, subIdx, type) = ResolvePostfixIncDec(context.postfixExpression());
            return GenPreIncDecDirect(sym, subIdx, type, true);
        }

        public override string VisitPreDecrement([NotNull] LSLParser.PreDecrementContext context)
        {
            var (sym, subIdx, type) = ResolvePostfixIncDec(context.postfixExpression());
            return GenPreIncDecDirect(sym, subIdx, type, false);
        }

        public override string VisitPostIncrementPostfix(
            [NotNull] LSLParser.PostIncrementPostfixContext context)
        {
            var (sym, subIdx, type) = ResolvePostfixIncDec(context.postfixExpression());
            return GenPostIncDecDirect(sym, subIdx, type, true);
        }

        public override string VisitPostDecrementPostfix(
            [NotNull] LSLParser.PostDecrementPostfixContext context)
        {
            var (sym, subIdx, type) = ResolvePostfixIncDec(context.postfixExpression());
            return GenPostIncDecDirect(sym, subIdx, type, false);
        }

        public override string VisitPreIncDecExpr([NotNull] LSLParser.PreIncDecExprContext context)
            => DoPromotion(context, VisitChildren(context));

        public override string VisitPostfixExpr([NotNull] LSLParser.PostfixExprContext context)
            => DoPromotion(context, Visit(context.postfixExpression()));

        // ── Postfix: subscript, method call, primary ──────────────────────────

        public override string VisitSubscriptPostfix(
            [NotNull] LSLParser.SubscriptPostfixContext context)
        {
            string subIdx = CalcSubIndex(context.ID().GetText());
            var varSym = GetVarSymFromPostfix(context.postfixExpression());
            if (varSym == null) return string.Empty;
            return ByteCodeEmitter.LoadSub(varSym.IsGlobal, varSym.ScopeIndex, subIdx);
        }

        public override string VisitMethodCallPostfix(
            [NotNull] LSLParser.MethodCallPostfixContext context)
        {
            string funcName = GetCallName(context.postfixExpression());
            MethodSymbol methSym = funcName != null
                ? _symtab.Globals.Resolve(funcName + "()") as MethodSymbol : null;

            var exprs = new List<string>();
            if (context.callParamList() != null)
                foreach (var e in context.callParamList().expr())
                    exprs.Add(DoPromotion(e, Visit(e)));

            bool isSyscall = methSym?.IsSyscall ?? false;
            return ByteCodeEmitter.MethCall(
                methSym?.Name ?? (funcName + "()"), exprs, isSyscall, false);
        }

        public override string VisitPrimaryExpr([NotNull] LSLParser.PrimaryExprContext context)
            => DoPromotion(context, Visit(context.primary()));

        // ── Primary expressions ───────────────────────────────────────────────

        public override string VisitIntegerLiteral([NotNull] LSLParser.IntegerLiteralContext context)
            => ByteCodeEmitter.IConst(context.INTEGER_LITERAL().GetText());

        public override string VisitFloatLiteral([NotNull] LSLParser.FloatLiteralContext context)
            => ByteCodeEmitter.FConst(FormatFloat(context.FLOAT_LITERAL().GetText()));

        public override string VisitStringLiteral([NotNull] LSLParser.StringLiteralContext context)
            => ByteCodeEmitter.SConst(context.STRING_LITERAL().GetText());

        public override string VisitIdExpr([NotNull] LSLParser.IdExprContext context)
        {
            var sym = GetSymbol(context);
            if (sym is ConstantSymbol cs)
                return ByteCodeEmitter.SysConstLoad(cs.TemplateName, cs.ConstValue);
            if (sym is VariableSymbol vs)
                return ByteCodeEmitter.IdLoad(vs.IsGlobal, vs.ScopeIndex);
            Error($"Unresolved identifier '{context.ID().GetText()}'");
            return string.Empty;
        }

        public override string VisitParenExpr([NotNull] LSLParser.ParenExprContext context)
            => GenExpression(context.expression());

        public override string VisitVectorLiteralExpr(
            [NotNull] LSLParser.VectorLiteralExprContext context)
            => Visit(context.vecLiteral());

        public override string VisitRotationLiteralExpr(
            [NotNull] LSLParser.RotationLiteralExprContext context)
            => Visit(context.rotLiteral());

        public override string VisitListLiteralExpr(
            [NotNull] LSLParser.ListLiteralExprContext context)
            => Visit(context.listLiteral());

        // ── Compound literals ─────────────────────────────────────────────────

        public override string VisitVecLiteral([NotNull] LSLParser.VecLiteralContext context)
        {
            var exprs = context.expr();
            if (AllFloatLiterals(exprs))
                return ByteCodeEmitter.VConst(
                    FormatFloat(exprs[0].GetText()),
                    FormatFloat(exprs[1].GetText()),
                    FormatFloat(exprs[2].GetText()));
            return ByteCodeEmitter.BuildVec(
                Visit(exprs[0]), Visit(exprs[1]), Visit(exprs[2]));
        }

        public override string VisitRotLiteral([NotNull] LSLParser.RotLiteralContext context)
        {
            var exprs = context.expr();
            if (AllFloatLiterals(exprs))
                return ByteCodeEmitter.RConst(
                    FormatFloat(exprs[0].GetText()), FormatFloat(exprs[1].GetText()),
                    FormatFloat(exprs[2].GetText()), FormatFloat(exprs[3].GetText()));
            return ByteCodeEmitter.BuildRot(
                Visit(exprs[0]), Visit(exprs[1]), Visit(exprs[2]), Visit(exprs[3]));
        }

        public override string VisitListLiteral([NotNull] LSLParser.ListLiteralContext context)
        {
            var parts = new List<string>();
            if (context.listContents() != null)
                foreach (var e in context.listContents().expr())
                    parts.Add(DoPromotion(e, Visit(e)));
            return ByteCodeEmitter.BuildList(parts);
        }

        // ── FuncCall (standalone statement) ───────────────────────────────────

        public override string VisitFuncCall([NotNull] LSLParser.FuncCallContext context)
        {
            string funcName = context.ID().GetText();
            MethodSymbol methSym = _symtab.Globals.Resolve(funcName + "()") as MethodSymbol;

            var exprs = new List<string>();
            if (context.callParamList() != null)
                foreach (var e in context.callParamList().expr())
                    exprs.Add(DoPromotion(e, Visit(e)));

            bool isSyscall = methSym?.IsSyscall ?? false;
            bool isVoid    = methSym == null || methSym.Type == SymbolTable.VOID;
            return ByteCodeEmitter.MethCall(
                methSym?.Name ?? (funcName + "()"), exprs, isSyscall, !isVoid);
        }

        // ── Default ───────────────────────────────────────────────────────────

        public override string VisitTerminal(ITerminalNode node) => null;

        protected override string AggregateResult(string aggregate, string nextResult)
            => nextResult ?? aggregate;

        // ── Utilities ─────────────────────────────────────────────────────────

		private string GetAssignOpText(LSLParser.AssignmentExpressionContext ctx)
		{
			for (int i = 0; i < ctx.ChildCount; i++)
				if (ctx.GetChild(i) is ITerminalNode tn)
					switch (tn.GetText())
					{
						case "=": case "+=": case "-=": case "*=":
						case "/=": case "%=": case "<<=": case ">>=":
							return tn.GetText();
					}
			return string.Empty;  // ← was "=" — that was wrong
		}

       private string GetBinaryOpText(ParserRuleContext ctx)
        {
            for (int i = 0; i < ctx.ChildCount; i++)
                if (ctx.GetChild(i) is ITerminalNode tn)
                {
                    string t = tn.GetText();
                    if (t.Length > 0 && !char.IsLetterOrDigit(t[0]) && t != "(" && t != ")")
                        return t;
                }
            return string.Empty;
        }

        private string GetBinaryOpTextAt(ParserRuleContext ctx, int rhsIndex)
        {
            // In a flat (expr (OP expr)*) rule, operators sit at child positions 1, 3, 5, ...
            int opPos = 2 * rhsIndex - 1;
            if (opPos > 0 && opPos < ctx.ChildCount && ctx.GetChild(opPos) is ITerminalNode tn)
                return tn.GetText();
            return GetBinaryOpText(ctx); // fallback to first op
        }

        private string GetMultOpText(LSLParser.MultiplicativeExpressionContext ctx, int rhsIndex)
        {
            int opPos = 2 * rhsIndex - 1;
            if (opPos < ctx.ChildCount && ctx.GetChild(opPos) is ITerminalNode tn)
                return tn.GetText();
            return "*";
        }

        private string GetCallName(LSLParser.PostfixExpressionContext ctx)
        {
            if (ctx is LSLParser.PrimaryExprContext pc && pc.primary() is LSLParser.IdExprContext id)
                return id.ID().GetText();
            return null;
        }

        private VariableSymbol GetVarSymFromPostfix(LSLParser.PostfixExpressionContext ctx)
        {
            if (ctx is LSLParser.PrimaryExprContext pc && pc.primary() is LSLParser.IdExprContext id)
                return GetSymbol(id) as VariableSymbol;
            return null;
        }

		 private VariableSymbol WalkForVarSym(IParseTree tree, out string subIdx)
		{
			subIdx = null;
			if (tree is LSLParser.IdExprContext id)
			{
				var annotated = GetSymbol(id) as VariableSymbol;
				if (annotated != null) return annotated;
				string name = id.ID().GetText();
				IScope scope = FindScopeForNode(id);
				return scope?.Resolve(name) as VariableSymbol
					?? _symtab.Globals.Resolve(name) as VariableSymbol;
			}
			if (tree is LSLParser.SubscriptPostfixContext sp)
			{
				subIdx = CalcSubIndex(sp.ID().GetText());
				return GetVarSymFromPostfix(sp.postfixExpression());
			}
			for (int i = 0; i < tree.ChildCount; i++)
			{
				var v = WalkForVarSym(tree.GetChild(i), out subIdx);
				if (v != null) return v;
			}
			return null;
		}

		private IScope FindScopeForNode(IParseTree node)
		{
			IParseTree n = node;
			while (n != null)
			{
				IScope s = _annotations.GetScope(n);
				if (s != null) return s;
				n = n.Parent;
			}
			return _symtab.Globals;
		}
		private VariableSymbol ResolveLhsSymbol(LSLParser.LhsContext ctx, out string subIdx)
		{
			subIdx = null;
			if (ctx == null) return null;
			var idNode = ctx.ID();
			if (idNode == null) return null;

			// Walk up the parse tree to find the nearest scope annotation
			IScope scope = FindScopeForNode(ctx);

			string name = idNode.GetText();
			var sym = scope?.Resolve(name) as VariableSymbol
				   ?? _symtab.Globals.Resolve(name) as VariableSymbol;

			if (ctx.ChildCount > 1)
				subIdx = CalcSubIndex(ctx.GetChild(ctx.ChildCount - 1).GetText());
			return sym;
		}

        private (VariableSymbol sym, string subIdx, ISymbolType type) ResolvePostfixIncDec(
            LSLParser.PostfixExpressionContext pfx)
        {
            string subIdx = null;
            VariableSymbol sym = null;
            if (pfx is LSLParser.SubscriptPostfixContext sp)
            {
                subIdx = CalcSubIndex(sp.ID().GetText());
                sym = GetVarSymFromPostfix(sp.postfixExpression());
            }
            else
                sym = GetVarSymFromPostfix(pfx);
            return (sym, subIdx, sym?.Type ?? EvalType(pfx));
        }

		 private (VariableSymbol sym, string subIdx, ISymbolType type) ResolveStmtIncDec(
			ITerminalNode[] ids, IToken subscriptToken)
		{
			string varName = ids[0].GetText();
			string subIdx = subscriptToken != null
                                ? CalcSubIndex(subscriptToken.Text) : null;
                        IParseTree parent = ids[0].Parent;
                        IScope scope = FindScopeForNode(parent);
			var sym = scope?.Resolve(varName) as VariableSymbol
				   ?? _symtab.Globals.Resolve(varName) as VariableSymbol;
			return (sym, subIdx, sym?.Type);
		}

        private string GenPreIncDecDirect(VariableSymbol sym, string subIdx,
            ISymbolType type, bool isIncrement)
        {
            if (sym == null) return string.Empty;
            if (subIdx != null)
                return isIncrement
                    ? ByteCodeEmitter.FPreIncSub(sym.IsGlobal, sym.ScopeIndex, subIdx)
                    : ByteCodeEmitter.FPreDecSub(sym.IsGlobal, sym.ScopeIndex, subIdx);
            if (type == SymbolTable.INT)
                return isIncrement
                    ? ByteCodeEmitter.IPreInc(sym.IsGlobal, sym.ScopeIndex)
                    : ByteCodeEmitter.IPreDec(sym.IsGlobal, sym.ScopeIndex);
            return isIncrement
                ? ByteCodeEmitter.FPreInc(sym.IsGlobal, sym.ScopeIndex)
                : ByteCodeEmitter.FPreDec(sym.IsGlobal, sym.ScopeIndex);
        }

        private string GenPostIncDecDirect(VariableSymbol sym, string subIdx,
            ISymbolType type, bool isIncrement)
        {
            if (sym == null) return string.Empty;
            if (subIdx != null)
                return isIncrement
                    ? ByteCodeEmitter.FPostIncSub(sym.IsGlobal, sym.ScopeIndex, subIdx)
                    : ByteCodeEmitter.FPostDecSub(sym.IsGlobal, sym.ScopeIndex, subIdx);
            if (type == SymbolTable.INT)
                return isIncrement
                    ? ByteCodeEmitter.IPostInc(sym.IsGlobal, sym.ScopeIndex)
                    : ByteCodeEmitter.IPostDec(sym.IsGlobal, sym.ScopeIndex);
            return isIncrement
                ? ByteCodeEmitter.FPostInc(sym.IsGlobal, sym.ScopeIndex)
                : ByteCodeEmitter.FPostDec(sym.IsGlobal, sym.ScopeIndex);
        }

        private string GetCompoundAssignTemplate(string op, ISymbolType lhs,
            ISymbolType rhs, string subIdx)
        {
            int li = Idx(lhs), ri = Idx(rhs);
            switch (op)
            {
                case "+=":  return subIdx != null
                    ? TemplateMapping.AddAssign[(int)Types.VarType.Float, ri]
                    : TemplateMapping.AddAssign[li, ri];
                case "-=":  return subIdx != null
                    ? TemplateMapping.SubtractAssign[(int)Types.VarType.Float, ri]
                    : TemplateMapping.SubtractAssign[li, ri];
                case "*=":  return subIdx != null
                    ? TemplateMapping.MultiplicationAssign[(int)Types.VarType.Float, ri]
                    : TemplateMapping.MultiplicationAssign[li, ri];
                case "/=":  return subIdx != null
                    ? TemplateMapping.DivisionAssign[(int)Types.VarType.Float, ri]
                    : TemplateMapping.DivisionAssign[li, ri];
                case "%=":  return TemplateMapping.ModulusAssign[li, ri];
                case "<<=": return TemplateMapping.LSHAssign[li, ri];
                case ">>=": return TemplateMapping.RSHAssign[li, ri];
                default: Error($"Unknown compound operator '{op}'"); return null;
            }
        }

        private bool AllFloatLiterals(LSLParser.ExprContext[] exprs)
        {
            foreach (var e in exprs)
            {
                ISymbolType t = EvalType(e);
                if ((t != SymbolTable.FLOAT && t != SymbolTable.INT) || !IsConstantExpr(e))
                    return false;
            }
            return true;
        }

        private bool IsConstantExpr(IParseTree tree)
        {
            if (tree is LSLParser.FloatLiteralContext || tree is LSLParser.IntegerLiteralContext)
                return true;
            if (tree.ChildCount == 1) return IsConstantExpr(tree.GetChild(0));
            return false;
        }
    }
}
