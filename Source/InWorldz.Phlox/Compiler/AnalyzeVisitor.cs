using System;
using System.Collections.Generic;
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
            string typeName = context.TYPE() != null ? context.TYPE().GetText() : null;

            // Build a synthetic LSLAst for the FunctionBranch node (used for line info only).
            LSLAst defNode = new LSLAst(context.ID().Symbol) { Text = context.ID().GetText() };

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
            LSLAst defNode = new LSLAst(context.ID().Symbol) { Text = context.ID().GetText() };
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
