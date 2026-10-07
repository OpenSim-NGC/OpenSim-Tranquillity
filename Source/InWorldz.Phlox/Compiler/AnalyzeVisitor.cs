using System;
using System.Collections.Generic;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using InWorldz.Phlox.Compiler.BranchAnalyze;

namespace InWorldz.Phlox.Compiler
{
    /// <summary>
    /// Third compiler pass: builds a branch/return analysis tree for each function
    /// to verify that all code paths return a value where required.
    /// Corresponds to the original ANTLR3 Analyze.g tree grammar.
    ///
    /// Pass order: DefVisitor → TypesVisitor → AnalyzeVisitor → GenVisitor
    ///
    /// After visiting, read FunctionBranches and call AllCodePathsReturn()
    /// on each FunctionBranch to check for missing returns.
    /// </summary>
    public class AnalyzeVisitor : LSLBaseVisitor<object>
    {
        // The recursive dispatch runs out of stack before a deeply nested tree does (DepthGuard).
        // The counted limits (NestingLimits) are the rule, the same levels the parser counted;
        // DepthGuard stays as the backstop. VisitChildren goes through Visit so every child is counted.
        private readonly NestingCounter _nesting = new NestingCounter();

        public override object Visit(Antlr4.Runtime.Tree.IParseTree tree)
        {
            DepthGuard.Check(tree);
            NestingKind? kind = NestingCounter.Classify(tree);
            if (!kind.HasValue) return base.Visit(tree);
            var start = (tree as Antlr4.Runtime.ParserRuleContext)?.Start;
            _nesting.Enter(kind.Value, start?.Line ?? 0, start?.Column ?? 0);
            try { return base.Visit(tree); }
            finally { _nesting.Exit(kind.Value); }
        }

        public override object VisitChildren(Antlr4.Runtime.Tree.IRuleNode node)
        {
            DepthGuard.Check(node);
            object result = DefaultResult;
            int n = node.ChildCount;
            for (int i = 0; i < n; i++)
            {
                if (!ShouldVisitNextChild(node, result)) break;
                result = AggregateResult(result, Visit(node.GetChild(i)));
            }
            return result;
        }

        private readonly SymbolTable _symtab;
        private readonly LSLNodeAnnotations _annotations;

        /// <summary>
        /// Populated after Visit() — one entry per user-defined function/event.
        /// </summary>
        public List<FunctionBranch> FunctionBranches { get; } = new List<FunctionBranch>();

        private Branch _currentBranch;

        public AnalyzeVisitor(SymbolTable symtab, LSLNodeAnnotations annotations)
        {
            _symtab      = symtab      ?? throw new ArgumentNullException(nameof(symtab));
            _annotations = annotations ?? throw new ArgumentNullException(nameof(annotations));
        }

        // ── Top-level ─────────────────────────────────────────────────────────

        public override object VisitProg([NotNull] LSLParser.ProgContext context)
        {
            return VisitChildren(context);
        }

        // ── Function definitions ──────────────────────────────────────────────

        public override object VisitFuncDef([NotNull] LSLParser.FuncDefContext context)
        {
            // Mirrors Analyze.g methodDef / methodOut
            string typeName = context.TYPE() != null ? SymbolTable.CanonicalTypeName(context.TYPE().GetText()) : null;

            IToken defNode = context.ID().Symbol;

            _currentBranch = new FunctionBranch(defNode, typeName);

            VisitChildren(context);

            FunctionBranches.Add((FunctionBranch)_currentBranch);
            _currentBranch = null;

            return null;
        }

        // ── Event definitions ─────────────────────────────────────────────────

        public override object VisitEventDef([NotNull] LSLParser.EventDefContext context)
        {
            // Events are void — treat like a void function for branch analysis.
            IToken defNode = context.ID().Symbol;
            _currentBranch = new FunctionBranch(defNode, null);  // null = void

            VisitChildren(context);

            FunctionBranches.Add((FunctionBranch)_currentBranch);
            _currentBranch = null;

            return null;
        }

        // ── If / else ─────────────────────────────────────────────────────────

        public override object VisitIfStmt([NotNull] LSLParser.IfStmtContext context)
        {
            if (_currentBranch == null)
                return VisitChildren(context);

            var ifelse = new IfElseStatement(_currentBranch);
            _currentBranch.SetNextStatement(ifelse);

            // Visit condition — no branch effect.
            Visit(context.expression());

            var stmts = context.statement();

            // If-body
            _currentBranch = ifelse.IfBranch;
            if (stmts.Length > 0)
                Visit(stmts[0]);

            // Else-body (optional)
            _currentBranch = ifelse.ElseBranch;
            if (stmts.Length > 1)
                Visit(stmts[1]);

            // Pop back to parent.
            _currentBranch = ifelse.ParentBranch;

            return null;
        }

        // ── Loops ─────────────────────────────────────────────────────────────

        public override object VisitWhileStmt([NotNull] LSLParser.WhileStmtContext context)
        {
            if (_currentBranch == null)
                return VisitChildren(context);

            var loop = new LoopStatement(_currentBranch);
            _currentBranch.SetNextStatement(loop);
            _currentBranch = loop;

            VisitChildren(context);

            _currentBranch = _currentBranch.ParentBranch;
            return null;
        }

        public override object VisitForStmt([NotNull] LSLParser.ForStmtContext context)
        {
            if (_currentBranch == null)
                return VisitChildren(context);

            var loop = new LoopStatement(_currentBranch);
            _currentBranch.SetNextStatement(loop);
            _currentBranch = loop;

            VisitChildren(context);

            _currentBranch = _currentBranch.ParentBranch;
            return null;
        }

        public override object VisitDoWhileStmt([NotNull] LSLParser.DoWhileStmtContext context)
        {
            if (_currentBranch == null)
                return VisitChildren(context);

            var loop = new LoopStatement(_currentBranch);
            _currentBranch.SetNextStatement(loop);
            _currentBranch = loop;

            VisitChildren(context);

            _currentBranch = _currentBranch.ParentBranch;
            return null;
        }

        // ── Labels ────────────────────────────────────────────────────────────

        public override object VisitLabel_([NotNull] LSLParser.Label_Context context)
        {
            if (_currentBranch != null)
            {
                var lbl = new Label(_currentBranch);
                _currentBranch.SetNextStatement(lbl);
            }
            return null;
        }

        public override object VisitLabelStmt([NotNull] LSLParser.LabelStmtContext context)
        {
            return VisitChildren(context);
        }

        // ── Return statements ─────────────────────────────────────────────────

        public override object VisitReturnStmt([NotNull] LSLParser.ReturnStmtContext context)
        {
            if (_currentBranch != null)
            {
                var ret = new ReturnStatement(_currentBranch);
                _currentBranch.SetNextStatement(ret);
            }
            return null;
        }

        // ── Default ───────────────────────────────────────────────────────────

        public override object VisitTerminal(ITerminalNode node) => null;

        protected override object AggregateResult(object aggregate, object nextResult)
            => nextResult ?? aggregate;
    }
}
