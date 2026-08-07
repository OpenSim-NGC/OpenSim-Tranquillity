using System;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;

namespace InWorldz.Phlox.Compiler
{
    /// <summary>
    /// First compiler pass: walks the ANTLR4 parse tree and populates the symbol table.
    /// Corresponds to the original ANTLR3 Def.g tree grammar.
    ///
    /// Pass order: DefVisitor → TypesVisitor → AnalyzeVisitor → GenVisitor
    /// </summary>
    public class DefVisitor : LSLBaseVisitor<object>
    {
        private readonly SymbolTable _symtab;
        private readonly LSLNodeAnnotations _annotations;

        private IScope _currentScope;
        private MethodSymbol _currentMethod;  // non-null when inside a funcDef
        private EventSymbol _currentEvent;    // non-null when inside an eventDef
        private int _labelCounter = 0;

        public DefVisitor(SymbolTable symtab, LSLNodeAnnotations annotations)
        {
            _symtab      = symtab      ?? throw new ArgumentNullException(nameof(symtab));
            _annotations = annotations ?? throw new ArgumentNullException(nameof(annotations));
            _currentScope = symtab.Globals;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves a TYPE token text to an ISymbolType via the global scope.
        /// Returns SymbolTable.VOID when typeName is null (void return).
        /// </summary>
        private ISymbolType ResolveType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return SymbolTable.VOID;

            Symbol sym = _symtab.Globals.Resolve(typeName);
            if (sym is ISymbolType t) return t;

            _symtab.StatusListener.Error($"line 0:0 Unknown type '{typeName}'");
            return SymbolTable.VOID;
        }

        /// <summary>
        /// Builds a minimal LSLAst shim from a terminal node for use as sym.Def,
        /// so that SymbolTable.TestForDefineErrors() can report accurate line/col.
        /// </summary>
        private static LSLAst MakeDef(ITerminalNode node)
        {
            return new LSLAst(node.Symbol);
        }

        private void PushScope(IScope scope) => _currentScope = scope;

        private void PopScope()
        {
            if (_currentScope.EnclosingScope == null)
                throw new InvalidOperationException("Attempted to pop global scope.");
            _currentScope = _currentScope.EnclosingScope;
        }

        // ── Top-level ─────────────────────────────────────────────────────────

        public override object VisitProg([NotNull] LSLParser.ProgContext context)
        {
            _annotations.SetScope(context, _currentScope);
            return VisitChildren(context);
        }

        // ── Global / local variable declarations ─────────────────────────────

        public override object VisitVarDecl([NotNull] LSLParser.VarDeclContext context)
        {
            // TYPE ID ('=' expression)?
            string typeName = context.TYPE().GetText();
            string varName  = context.ID().GetText();

            ISymbolType type = ResolveType(typeName);
            var sym = new VariableSymbol(varName, type);
            sym.Def = MakeDef(context.ID());

            _symtab.Define(sym, _currentScope);

            _annotations.SetScope(context, _currentScope);
            _annotations.SetSymbol(context, sym);

            // Visit initialiser expression only
            if (context.expression() != null)
                Visit(context.expression());

            return null;
        }

        // VarDeclStmt wraps a VarDecl inside a function body — just recurse.
        public override object VisitVarDeclStmt([NotNull] LSLParser.VarDeclStmtContext context)
        {
            return VisitChildren(context);
        }

        // ── Function definitions ──────────────────────────────────────────────

        public override object VisitFuncDef([NotNull] LSLParser.FuncDefContext context)
        {
            // TYPE? ID '(' paramList ')' funcBlock
            string typeName = context.TYPE() != null ? context.TYPE().GetText() : null;
            string funcName = context.ID().GetText();

            ISymbolType returnType = ResolveType(typeName);
            var methSym = new MethodSymbol(funcName, returnType, _currentScope);
            methSym.Def = MakeDef(context.ID());

            _symtab.Define(methSym, _currentScope);

            _annotations.SetScope(context, _currentScope);
            _annotations.SetSymbol(context, methSym);

            MethodSymbol prevMethod = _currentMethod;
            _currentMethod = methSym;
            PushScope(methSym);

            VisitChildren(context);

            PopScope();
            _currentMethod = prevMethod;

            return null;
        }

        // ── Event definitions ─────────────────────────────────────────────────

        public override object VisitEventDef([NotNull] LSLParser.EventDefContext context)
        {
            // ID '(' paramList ')' funcBlock
            string eventName = context.ID().GetText();

            if (!_symtab.HasEventByName(eventName))
            {
                _symtab.StatusListener.Error(
                    $"line {context.ID().Symbol.Line}:{context.ID().Symbol.Column} " +
                    $"Unknown LSL event '{eventName}'");
            }

            var evtSym = new EventSymbol(eventName, _currentScope);
            evtSym.Def = MakeDef(context.ID());

            _symtab.Define(evtSym, _currentScope);

            _annotations.SetScope(context, _currentScope);
            _annotations.SetSymbol(context, evtSym);

            EventSymbol prevEvent = _currentEvent;
            _currentEvent = evtSym;
            PushScope(evtSym);

            VisitChildren(context);

            PopScope();
            _currentEvent = prevEvent;

            return null;
        }

        // ── Parameter declarations ────────────────────────────────────────────

        public override object VisitParamDecl([NotNull] LSLParser.ParamDeclContext context)
        {
            // TYPE ID
            string typeName  = context.TYPE().GetText();
            string paramName = context.ID().GetText();

            ISymbolType type = ResolveType(typeName);
            var sym = new VariableSymbol(paramName, type);
            sym.Def = MakeDef(context.ID());

            _symtab.Define(sym, _currentScope);

            _annotations.SetScope(context, _currentScope);
            _annotations.SetSymbol(context, sym);

            return null;
        }

        // ── State definitions ─────────────────────────────────────────────────

        public override object VisitNamedStateDef([NotNull] LSLParser.NamedStateDefContext context)
        {
            // 'state' ID stateBlock
            string stateName = context.ID().GetText();
            var stateSym = new StateSymbol(stateName, _currentScope);
            stateSym.Def = MakeDef(context.ID());

            _symtab.Define(stateSym, _currentScope);

            _annotations.SetScope(context, _currentScope);
            _annotations.SetSymbol(context, stateSym);

            PushScope(stateSym);
            VisitChildren(context);
            PopScope();

            return null;
        }

        public override object VisitDefaultStateDef([NotNull] LSLParser.DefaultStateDefContext context)
        {
            // 'default' stateBlock
            // StateSymbol constructor appends "(*)" internally, so pass plain "default".
            // SymbolTable.Define(StateSymbol) then checks for "default(*)" correctly.
            var stateSym = new StateSymbol("default", _currentScope);
            stateSym.Def = new LSLAst();   // no ID token; synthetic def at line 0

            _symtab.Define(stateSym, _currentScope);

            _annotations.SetScope(context, _currentScope);
            _annotations.SetSymbol(context, stateSym);

            PushScope(stateSym);
            VisitChildren(context);
            PopScope();

            return null;
        }

        // ── Function body blocks ──────────────────────────────────────────────

        public override object VisitFuncBlock([NotNull] LSLParser.FuncBlockContext context)
        {
            // Fresh local scope for the body; params live in MethodSymbol/EventSymbol above.
            var local = new LocalScope(_currentScope);
            _annotations.SetScope(context, local);
            PushScope(local);

            VisitChildren(context);

            PopScope();
            return null;
        }

        // Anonymous block { ... } creates a nested local scope.
        public override object VisitAnonBlock([NotNull] LSLParser.AnonBlockContext context)
        {
            var local = new LocalScope(_currentScope);
            _annotations.SetScope(context, local);
            PushScope(local);

            VisitChildren(context);

            PopScope();
            return null;
        }

        // ── Labels ────────────────────────────────────────────────────────────

        public override object VisitLabel_([NotNull] LSLParser.Label_Context context)
        {
            // '@' ID
            string labelName = context.ID().GetText();
            var labelSym = new LabelSymbol(labelName, _labelCounter++);
            labelSym.Def = MakeDef(context.ID());

            _symtab.Define(labelSym, _currentScope);

            _annotations.SetScope(context, _currentScope);
            _annotations.SetSymbol(context, labelSym);

            return null;
        }

        public override object VisitLabelStmt([NotNull] LSLParser.LabelStmtContext context)
        {
            return VisitChildren(context);
        }

        // ── Jump statements ───────────────────────────────────────────────────

        public override object VisitJumpStmt([NotNull] LSLParser.JumpStmtContext context)
        {
            // 'jump' ID SEMI — record scope so later passes can resolve the label target.
            _annotations.SetScope(context, _currentScope);
            return null;
        }

        // ── Return statements ─────────────────────────────────────────────────

        public override object VisitReturnStmt([NotNull] LSLParser.ReturnStmtContext context)
        {
            // 'return' expression? SEMI
            _annotations.SetScope(context, _currentScope);

            // Tag with the enclosing function or event so TypesVisitor can check the type.
            if (_currentMethod != null)
                _annotations.SetSymbol(context, _currentMethod);
            else if (_currentEvent != null)
                _annotations.SetSymbol(context, _currentEvent);

            if (context.expression() != null)
                Visit(context.expression());

            return null;
        }

        // ── Default visitor ───────────────────────────────────────────────────

        public override object VisitTerminal(ITerminalNode node)
        {
            return null;
        }

        protected override object AggregateResult(object aggregate, object nextResult)
            => nextResult ?? aggregate;
    }
}
