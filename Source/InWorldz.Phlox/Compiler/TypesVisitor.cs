using System;
using System.Collections.Generic;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using InWorldz.Phlox.Types;

namespace InWorldz.Phlox.Compiler
{
    /// <summary>
    /// Second compiler pass: resolves and checks types on every expression node.
    /// Writes evalType (and promoteToType where needed) into LSLNodeAnnotations.
    /// Corresponds to the original ANTLR3 Types.g tree grammar.
    ///
    /// Pass order: DefVisitor → TypesVisitor → AnalyzeVisitor → GenVisitor
    ///
    /// Design note: all type-checking helpers live here rather than in SymbolTable
    /// because the ANTLR3 symtab helpers (Bop, Assign, MethodCall, etc.) were never
    /// ported. This visitor is self-contained.
    /// </summary>
    public class TypesVisitor : LSLBaseVisitor<ISymbolType>
    {
        // The recursive dispatch runs out of stack before a deeply nested tree does (DepthGuard).
        // The counted limits (NestingLimits) are the rule, the same levels the parser counted;
        // DepthGuard stays as the backstop. VisitChildren goes through Visit so every child is counted.
        private readonly NestingCounter _nesting = new NestingCounter();

        public override ISymbolType Visit(Antlr4.Runtime.Tree.IParseTree tree)
        {
            DepthGuard.Check(tree);
            NestingKind? kind = NestingCounter.Classify(tree);
            if (!kind.HasValue) return base.Visit(tree);
            var start = (tree as Antlr4.Runtime.ParserRuleContext)?.Start;
            _nesting.Enter(kind.Value, start?.Line ?? 0, start?.Column ?? 0);
            try { return base.Visit(tree); }
            finally { _nesting.Exit(kind.Value); }
        }

        public override ISymbolType VisitChildren(Antlr4.Runtime.Tree.IRuleNode node)
        {
            DepthGuard.Check(node);
            ISymbolType result = DefaultResult;
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

        // The enclosing function/event — set when we enter a funcDef or eventDef.
        private MethodSymbol _currentMethod;
        private EventSymbol _currentEvent;

        public TypesVisitor(SymbolTable symtab, LSLNodeAnnotations annotations)
        {
            _symtab      = symtab      ?? throw new ArgumentNullException(nameof(symtab));
            _annotations = annotations ?? throw new ArgumentNullException(nameof(annotations));
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private int Idx(ISymbolType t)
        {
            if (t == null) return (int)VarType.Void;
            return t.TypeIndex;
        }

        private ISymbolType TypeOf(IParseTree node)
            => _annotations.GetEvalType(node);

        private void SetType(IParseTree node, ISymbolType type)
            => _annotations.SetEvalType(node, type);

        private void SetPromote(IParseTree node, ISymbolType type)
            => _annotations.SetPromoteToType(node, type);

        private void Error(IToken token, string msg)
            => _symtab.StatusListener.Error($"line {token.Line}:{token.Column} {msg}");

        private void Error(int line, int col, string msg)
            => _symtab.StatusListener.Error($"line {line}:{col} {msg}");

        /// <summary>
        /// Returns the ISymbolType for a TYPE token text, using the global scope.
        /// </summary>
        private ISymbolType ResolveType(string typeName)
        {
            Symbol s = _symtab.Globals.Resolve(SymbolTable.CanonicalTypeName(typeName));
            if (s is ISymbolType t) return t;
            return SymbolTable.VOID;
        }

        /// <summary>
        /// Resolves binary-op result type from the appropriate table.
        /// Returns VOID on type error.
        /// </summary>
        private ISymbolType BinaryOpType(ISymbolType[,] table, ISymbolType lhs, ISymbolType rhs,
            IToken opToken)
        {
            if (lhs == null || rhs == null) return SymbolTable.VOID;
            ISymbolType result = table[Idx(lhs), Idx(rhs)];
            if (result == SymbolTable.VOID)
                Error(opToken, $"Type mismatch: cannot apply operator to {lhs.Name} and {rhs.Name}");
            return result;
        }

        /// <summary>
        /// Checks that a type can be used in a numeric/unary context (int or float).
        /// </summary>
        private bool IsNumeric(ISymbolType t)
            => t == SymbolTable.INT || t == SymbolTable.FLOAT;

        // ── Top level — just recurse ──────────────────────────────────────────

        public override ISymbolType VisitProg([NotNull] LSLParser.ProgContext context)
        {
            VisitChildren(context);
            return null;
        }

        // ── Function / event scope tracking ──────────────────────────────────

        public override ISymbolType VisitFuncDef([NotNull] LSLParser.FuncDefContext context)
        {
            MethodSymbol prev = _currentMethod;
            _currentMethod = _annotations.GetSymbol(context) as MethodSymbol;
            VisitChildren(context);
            _currentMethod = prev;
            return null;
        }

        public override ISymbolType VisitEventDef([NotNull] LSLParser.EventDefContext context)
        {
            EventSymbol prev = _currentEvent;
            _currentEvent = _annotations.GetSymbol(context) as EventSymbol;

            // Validate event signature against SupportedEventList
            if (_currentEvent != null)
            {
                // EventSymbol.Name returns "eventname()" — strip trailing "()" for lookup.
                string rawEventName = _currentEvent.Name.Replace("()", "");
                List<VarType> argTypes = _currentEvent.ExtractArgumentTypes();
                if (!_symtab.HasEventBySig(rawEventName, VarType.Void, argTypes))
                {
                    Error(context.ID().Symbol,
                        $"Event '{rawEventName}' has wrong parameter signature");
                }
            }

            VisitChildren(context);
            _currentEvent = prev;
            return null;
        }

        // ── Variable declarations ─────────────────────────────────────────────

        public override ISymbolType VisitVarDecl([NotNull] LSLParser.VarDeclContext context)
        {
            // Visit initialiser to get its type, then check assignability.
            if (context.expression() != null)
            {
                ISymbolType initType = Visit(context.expression());
                VariableSymbol varSym = _annotations.GetSymbol(context) as VariableSymbol;
                if (varSym != null && initType != null)
                {
                    ISymbolType destType = varSym.Type;
                    ISymbolType promotion = SymbolTable.promoteFromTo[Idx(initType), Idx(destType)];
                    if (!_symtab.CanAssignTo(initType, destType, promotion))
                    {
                        Error(context.ID().Symbol,
                            $"Cannot assign {initType.Name} to {destType.Name}");
                    }
                    else if (promotion != null)
                    {
                        SetPromote(context.expression(), promotion);
                    }
                }
            }
            return null;
        }

        public override ISymbolType VisitVarDeclStmt([NotNull] LSLParser.VarDeclStmtContext context)
        {
            VisitChildren(context);
            return null;
        }

        // ── Assignment statements ─────────────────────────────────────────────

        /// <summary>
        /// x = e; and x op= e; (the statement form, a separate grammar rule from the assignment
        /// expression). The value must be assignable to the target, and an integer stored into
        /// a float or a vector/rotation component is promoted to float.
        /// </summary>
        public override ISymbolType VisitAssignmentStmt([NotNull] LSLParser.AssignmentStmtContext context)
        {
            ISymbolType rhsType = Visit(context.expression());
            IToken nameToken = context.lhs().ID().Symbol;
            string name = nameToken.Text;

            IScope scope = null;
            IParseTree node = context;
            while (node != null && scope == null)
            {
                scope = _annotations.GetScope(node);
                node = node.Parent;
            }
            Symbol sym = _symtab.ResolveVisible(scope ?? _symtab.Globals, name, nameToken.TokenIndex, out bool declaredLater);
            if (!(sym is VariableSymbol varSym) || sym is ConstantSymbol)
            {
                Error(nameToken, sym != null ? $"'{name}' is not assignable"
                    : declaredLater ? $"Symbol '{name}' can not be used before it is defined"
                    : $"Undefined symbol '{name}'");
                return null;
            }
            if (context.subscript != null && !CheckSubscript(nameToken, varSym.Type, context.subscript.Text))
                return null;

            ISymbolType lhsType = context.subscript != null ? SymbolTable.FLOAT : varSym.Type;
            string op = context.op?.Text ?? "=";
            if (rhsType == null) return null;
            if (op == "=")
            {
                ISymbolType promotion = SymbolTable.promoteFromTo[Idx(rhsType), Idx(lhsType)];
                if (!_symtab.CanAssignTo(rhsType, lhsType, promotion))
                    Error(nameToken, $"Cannot assign {rhsType.Name} to {lhsType?.Name}");
                else if (promotion != null)
                    SetPromote(context.expression(), promotion);
            }
            else
            {
                ISymbolType[,] table = _symtab.FindOperationTable(op, nameToken.Line, nameToken.Column);
                if (table != null && table[Idx(lhsType), Idx(rhsType)] == SymbolTable.VOID)
                    Error(nameToken, $"Type mismatch: cannot apply '{op}' to {lhsType?.Name} and {rhsType.Name}");
            }
            return null;
        }

        // ── Return statements ─────────────────────────────────────────────────

        public override ISymbolType VisitReturnStmt([NotNull] LSLParser.ReturnStmtContext context)
        {
            ISymbolType returnType = SymbolTable.VOID;
            if (context.expression() != null)
                returnType = Visit(context.expression());

            // Determine expected return type from enclosing function/event.
            ISymbolType expected = SymbolTable.VOID;
            if (_currentMethod != null)
                expected = _currentMethod.Type ?? SymbolTable.VOID;
            // Events always return void.

            if (returnType == null) returnType = SymbolTable.VOID;

            if (expected == SymbolTable.VOID && returnType != SymbolTable.VOID)
            {
                Error(context.r.Line, context.r.Column,
                    "Void function/event should not return a value");
            }
            else if (expected != SymbolTable.VOID)
            {
                ISymbolType promotion = SymbolTable.promoteFromTo[Idx(returnType), Idx(expected)];
                if (!_symtab.CanAssignTo(returnType, expected, promotion))
                {
                    Error(context.r.Line, context.r.Column,
                        $"Return type mismatch: expected {expected.Name}, got {returnType.Name}");
                }
                else if (promotion != null && context.expression() != null)
                {
                    SetPromote(context.expression(), promotion);
                }
            }
            return null;
        }

        // ── State change ──────────────────────────────────────────────────────

        public override ISymbolType VisitStateChangeStmt([NotNull] LSLParser.StateChangeStmtContext context)
        {
            // Verify the target state exists.
            //
            // `state default;` is valid LSL - the wiki's own example does it - and the
            // grammar matches `default` as a KEYWORD, not an ID (LSL.g4:87,
            // `stateNode='state' (ID | 'default') SEMI`). So ID() is null for exactly that case and
            // this dereferenced it, throwing a NullReferenceException out of the whole compile.
            // GenVisitor already had this right (`context.ID()?.GetText()`, GenVisitor.cs:237) and
            // ByteCodeEmitter.StateChange reads a null id as the default state, so the back end was
            // never wrong - only this check was.
            var id = context.ID();
            string stateName = id?.GetText() ?? "default";
            string key = stateName == "default" ? "default(*)" : stateName + "(*)";
            if (_symtab.Globals.Resolve(key) == null)
            {
                Error(id?.Symbol ?? context.stateNode, $"Unknown state '{stateName}'");
            }
            return null;
        }

        // ── Jump statement ────────────────────────────────────────────────────

		public override ISymbolType VisitJumpStmt([NotNull] LSLParser.JumpStmtContext context)
		{
			string labelName = context.ID().GetText();
			IScope scope = null;
			IParseTree node = context;
			while (node != null && scope == null)
			{
				scope = _annotations.GetScope(node);
				node = node.Parent;
			}
			if (scope != null && scope.Resolve("@" + labelName) == null)
				Error(context.ID().Symbol, $"Undefined label '{labelName}'");	
			return null;
		}

        // ── Expression passthrough nodes ──────────────────────────────────────

        public override ISymbolType VisitExpression([NotNull] LSLParser.ExpressionContext context)
        {
            ISymbolType t = VisitChildren(context);
            SetType(context, t);
            return t;
        }

        public override ISymbolType VisitExpr([NotNull] LSLParser.ExprContext context)
        {
            ISymbolType t = Visit(context.assignmentExpression());
            SetType(context, t);
            return t;
        }

        // ── Assignment expression ─────────────────────────────────────────────

		public override ISymbolType VisitAssignmentExpression(
			[NotNull] LSLParser.AssignmentExpressionContext context)
		{
			// The rule is booleanExpression (op assignmentExpression)*, right-recursive: for
			// x = e the target is booleanExpression() and the value is assignmentExpression(0).
			LSLParser.AssignmentExpressionContext valueCtx = context.assignmentExpression(0);
			if (valueCtx == null)
			{
				// Pure boolean expression passthrough
				ISymbolType t = context.booleanExpression() != null
					? Visit(context.booleanExpression())
					: VisitChildren(context);
				SetType(context, t ?? SymbolTable.VOID);
				return t;
			}
			if (context.assignmentExpression().Length > 1)
				ErrorAtContext(context, "Invalid assignment target");

			ISymbolType lhsType = Visit(context.booleanExpression());
			if (AssignmentTarget(context.booleanExpression()) == null)
				ErrorAtContext(context, "Invalid assignment target");
			ISymbolType rhsType = Visit(valueCtx);
			string op2 = GetAssignOp(context);
			ISymbolType resultType;

			if (op2 == "=")
			{
				ISymbolType promotion = lhsType != null && rhsType != null
					? SymbolTable.promoteFromTo[Idx(rhsType), Idx(lhsType)]
					: null;
				if (!_symtab.CanAssignTo(rhsType, lhsType, promotion))
				{
					ErrorAtContext(context, $"Cannot assign {rhsType?.Name} to {lhsType?.Name}");
					resultType = lhsType ?? SymbolTable.VOID;
				}
				else
				{
					if (promotion != null) SetPromote(valueCtx, promotion);
					resultType = lhsType;
				}
			}
			else
			{
				ISymbolType[,] table = _symtab.FindOperationTable(op2, 0, 0);
				resultType = table != null
					? table[Idx(lhsType), Idx(rhsType)]
					: SymbolTable.VOID;
				if (resultType == SymbolTable.VOID)
					ErrorAtContext(context,
						$"Type mismatch: cannot apply '{op2}' to {lhsType?.Name} and {rhsType?.Name}");
			}

			SetType(context, resultType);
			return resultType;
		}
        // ── Boolean, bitwise, equality, relational chains ─────────────────────

        public override ISymbolType VisitBooleanExpression(
            [NotNull] LSLParser.BooleanExpressionContext context)
        {
            var children = context.bitwiseExpression();
            if (children.Length == 1)
            {
                ISymbolType t = Visit(children[0]);
                SetType(context, t);
                return t;
            }
            // &&  ||  — result is always integer (boolean)
            // SL refuses a key operand at compile time (LL's operator table, as Tailslide's types.cc has it:
            // OP_BOOLEAN_AND / OP_BOOLEAN_OR take LST_INTEGER, LST_INTEGER only); Phlox compiled it and the script
            // stopped at run time. Only the key is refused here: the other non-integer operands keep compiling.
            for (int i = 0; i < children.Length; i++)
            {
                ISymbolType t = Visit(children[i]);
                if (t == SymbolTable.KEY)
                    ErrorAtContext(children[i], $"Type mismatch: '{GetOpAt(context, i == 0 ? 1 : i)}' cannot be applied to a key");
                else CheckHasValue(children[i], t);
            }
            SetType(context, SymbolTable.INT);
            return SymbolTable.INT;
        }

       public override ISymbolType VisitBitwiseExpression(
            [NotNull] LSLParser.BitwiseExpressionContext context)
        {
            var children = context.equalityExpression();
            if (children.Length == 1)
            {
                ISymbolType t = Visit(children[0]);
                SetType(context, t);
                return t;
            }
            // | & ^  — integer only (all operands)
            ISymbolType result = SymbolTable.INT;
            for (int i = 0; i < children.Length; i++)
            {
                ISymbolType t = Visit(children[i]);
                if (t != SymbolTable.INT)
                {
                    ErrorAtContext(context,
                        $"Bitwise operators require integer operands, got {t?.Name}");
                }
            }
            SetType(context, result);
            return result;
        }

        public override ISymbolType VisitEqualityExpression(
            [NotNull] LSLParser.EqualityExpressionContext context)
        {
            var children = context.relationalExpression();
            if (children.Length == 1)
            {
                ISymbolType t = Visit(children[0]);
                SetType(context, t);
                return t;
            }
            // == !=  — result integer. The two sides must have the same type after integer-to-float and key/string
            // conversion (Halcyon's SymbolTable.EqOp and HaveSameTypes; SL refuses the same pairs). A chain compares
            // left to right, so every comparison after the first has an integer on its left.
            ISymbolType lhs = Visit(children[0]);
            for (int i = 1; i < children.Length; i++)
            {
                ISymbolType rhs = Visit(children[i]);
                if (lhs != null && rhs != null && !HaveSameTypes(lhs, rhs))
                    ErrorAtContext(children[i - 1],
                        "Type mismatch, equality operators == and != require arguments of the same type");
                lhs = SymbolTable.INT;
            }
            SetType(context, SymbolTable.INT);
            return SymbolTable.INT;
        }

        public override ISymbolType VisitRelationalExpression(
            [NotNull] LSLParser.RelationalExpressionContext context)
        {
            var children = context.binaryBitwiseExpression();
            if (children.Length == 1)
            {
                ISymbolType t = Visit(children[0]);
                SetType(context, t);
                return t;
            }
            // < > <= >=  — result integer. Halcyon's SymbolTable.RelOp: the same types as == (above), and no strings.
            // Halcyon also accepted key, vector, rotation and list pairs, which SL refuses; content written for it may
            // use them, so they keep compiling.
            ISymbolType lhs = Visit(children[0]);
            for (int i = 1; i < children.Length; i++)
            {
                ISymbolType rhs = Visit(children[i]);
                if (lhs != null && rhs != null)
                {
                    if (!HaveSameTypes(lhs, rhs))
                        ErrorAtContext(children[i - 1],
                            "Type mismatch, relational operators require arguments of the same type");
                    if (lhs == SymbolTable.STRING || rhs == SymbolTable.STRING)
                        ErrorAtContext(children[i - 1],
                            "Type mismatch, strings can not be compared with < or >");
                }
                lhs = SymbolTable.INT;
            }
            SetType(context, SymbolTable.INT);
            return SymbolTable.INT;
        }

       public override ISymbolType VisitBinaryBitwiseExpression(
            [NotNull] LSLParser.BinaryBitwiseExpressionContext context)
        {
            var children = context.additiveExpression();
            if (children.Length == 1)
            {
                ISymbolType t = Visit(children[0]);
                SetType(context, t);
                return t;
            }
            // << >>  — evaluate all children, result type from last pair
            ISymbolType lhs = Visit(children[0]);
            ISymbolType result = lhs;
            for (int i = 1; i < children.Length; i++)
            {
                ISymbolType rhs = Visit(children[i]);
                result = SymbolTable.shiftResultType[Idx(lhs), Idx(rhs)];
                if (result == SymbolTable.VOID)
                    ErrorAtContext(context,
                        $"Type mismatch in shift operation: {lhs?.Name} and {rhs?.Name}");
                lhs = result;
            }
            SetType(context, result);
            return result;
        }

        // ── Additive / multiplicative ─────────────────────────────────────────

        public override ISymbolType VisitAdditiveExpression(
            [NotNull] LSLParser.AdditiveExpressionContext context)
        {
            var children = context.multiplicativeExpression();
            if (children.Length == 1)
            {
                ISymbolType t = Visit(children[0]);
                SetType(context, t);
                return t;
            }

            ISymbolType result = Visit(children[0]);
            // Walk left to right; the operator for each pair sits between the two operands.
            for (int i = 1; i < children.Length; i++)
            {
                ISymbolType rhs = Visit(children[i]);
                bool isMinus = GetOpAt(context, i) == "-";
                ISymbolType[,] table = isMinus
                    ? SymbolTable.subtractionResultType
                    : SymbolTable.additionResultType;
                IToken opToken = children[i].Start;
                result = BinaryOpType(table, result, rhs, opToken);
            }
            SetType(context, result);
            return result;
        }

        public override ISymbolType VisitMultiplicativeExpression(
            [NotNull] LSLParser.MultiplicativeExpressionContext context)
        {
            var children = context.unaryExpression();
            if (children.Length == 1)
            {
                ISymbolType t = Visit(children[0]);
                SetType(context, t);
                return t;
            }

            ISymbolType result = Visit(children[0]);
            for (int i = 1; i < children.Length; i++)
            {
                ISymbolType rhs = Visit(children[i]);
                // Operator is a token between children — detect * / %
                // We use multiplication table as the common case; mod/div also valid.
                // The full operator is available via GetChild() walk but for type
                // purposes * / and % all go through their respective tables.
                // We pick the table based on which token appears at child position 2i-1.
                string op = GetMultOp(context, i);
                ISymbolType[,] table = op == "%" ? SymbolTable.modResultType
                    : op == "/" ? SymbolTable.divisionResultType
                    : SymbolTable.multiplicationResultType;
                IToken opToken = children[i].Start;
                result = BinaryOpType(table, result, rhs, opToken);
            }
            SetType(context, result);
            return result;
        }

        // ── Unary expressions ─────────────────────────────────────────────────

        public override ISymbolType VisitUnaryMinus([NotNull] LSLParser.UnaryMinusContext context)
        {
            ISymbolType t = Visit(context.unaryExpression());
            if (t != SymbolTable.INT && t != SymbolTable.FLOAT && t != SymbolTable.VECTOR && t != SymbolTable.ROTATION)
                Error(context.MINUS().Symbol,
                    $"Unary minus cannot be applied to type {t?.Name}");
            SetType(context, t);
            return t;
        }

        public override ISymbolType VisitUnaryBoolNot([NotNull] LSLParser.UnaryBoolNotContext context)
        {
            ISymbolType t = Visit(context.unaryExpression());
            // SL refuses '!' on a key at compile time (Tailslide types.cc: {'!', LST_INTEGER, LST_NONE,
            // LST_BOOLEAN}); Phlox compiled it and the script stopped at run time.
            if (t == SymbolTable.KEY)
                ErrorAtContext(context, "Type mismatch: '!' cannot be applied to a key");
            else CheckHasValue(context.unaryExpression(), t);
            // ! always produces integer (boolean)
            SetType(context, SymbolTable.INT);
            return SymbolTable.INT;
        }

        public override ISymbolType VisitUnaryBitNot([NotNull] LSLParser.UnaryBitNotContext context)
        {
            ISymbolType t = Visit(context.unaryExpression());
            if (t != SymbolTable.INT)
                ErrorAtContext(context, $"Bitwise NOT requires integer, got {t?.Name}");
            SetType(context, SymbolTable.INT);
            return SymbolTable.INT;
        }

        public override ISymbolType VisitTypeCastExpr([NotNull] LSLParser.TypeCastExprContext context)
        {
            // Passthrough to TypeCastExpression
            ISymbolType t = VisitChildren(context);
            SetType(context, t);
            return t;
        }

        // ── Type cast ─────────────────────────────────────────────────────────

        public override ISymbolType VisitTypeCast([NotNull] LSLParser.TypeCastContext context)
        {
            ISymbolType exprType = Visit(context.unaryExpression());
            ISymbolType destType = ResolveType(context.TYPE().GetText());

            if (!_symtab.CanCast(Idx(exprType), Idx(destType)))
            {
                Error(context.TYPE().Symbol,
                    $"Cannot cast {exprType?.Name} to {destType.Name}");
            }
            SetType(context, destType);
            return destType;
        }

        // ── Pre-inc/dec ───────────────────────────────────────────────────────

        public override ISymbolType VisitPreIncDecExpr(
            [NotNull] LSLParser.PreIncDecExprContext context)
        {
            ISymbolType t = VisitChildren(context);
            SetType(context, t);
            return t;
        }

        public override ISymbolType VisitPreIncrement([NotNull] LSLParser.PreIncrementContext context)
        {
            ISymbolType t = Visit(context.postfixExpression());
            if (!IsNumeric(t))
                ErrorAtContext(context, $"Pre-increment requires numeric type, got {t?.Name}");
            SetType(context, t);
            return t;
        }

        public override ISymbolType VisitPreDecrement([NotNull] LSLParser.PreDecrementContext context)
        {
            ISymbolType t = Visit(context.postfixExpression());
            if (!IsNumeric(t))
                ErrorAtContext(context, $"Pre-decrement requires numeric type, got {t?.Name}");
            SetType(context, t);
            return t;
        }

        public override ISymbolType VisitPostfixExpr([NotNull] LSLParser.PostfixExprContext context)
        {
            ISymbolType t = Visit(context.postfixExpression());
            SetType(context, t);
            return t;
        }

        // ── Postfix expressions ───────────────────────────────────────────────

        public override ISymbolType VisitPrimaryExpr([NotNull] LSLParser.PrimaryExprContext context)
        {
            ISymbolType t = VisitChildren(context);
            SetType(context, t);
            return t;
        }

        public override ISymbolType VisitPostIncrementPostfix(
            [NotNull] LSLParser.PostIncrementPostfixContext context)
        {
            ISymbolType t = Visit(context.postfixExpression());
            if (!IsNumeric(t))
                ErrorAtContext(context, $"Post-increment requires numeric type, got {t?.Name}");
            SetType(context, t);
            return t;
        }

        public override ISymbolType VisitPostDecrementPostfix(
            [NotNull] LSLParser.PostDecrementPostfixContext context)
        {
            ISymbolType t = Visit(context.postfixExpression());
            if (!IsNumeric(t))
                ErrorAtContext(context, $"Post-decrement requires numeric type, got {t?.Name}");
            SetType(context, t);
            return t;
        }

        // ── Method call (postfix) ─────────────────────────────────────────────

        public override ISymbolType VisitMethodCallPostfix(
            [NotNull] LSLParser.MethodCallPostfixContext context)
        {
            // postfixExpression '(' callParamList ')'
            // The function name is in postfixExpression → primary → IdExpr.
            // Visit the callee to get the name/symbol, then check args.
            ISymbolType calleeType = Visit(context.postfixExpression());

            // Resolve the function symbol.
            string funcName = GetCallName(context.postfixExpression());

            // Visit each argument expression first: the arity chooses the overload.
            List<ISymbolType> argTypes = new List<ISymbolType>();
            if (context.callParamList() != null)
            {
                foreach (var expr in context.callParamList().expr())
                    argTypes.Add(Visit(expr));
            }

            MethodSymbol methSym = ResolveCall(context, funcName, argTypes);
            if (methSym != null) _annotations.SetSymbol(context, methSym);   // The gen pass reads this choice

            if (methSym == null)
            {
                // Halcyon's message (SymbolTable.MethodCall); the assembler used to be the first to notice, without a line.
                if (funcName != null)
                    ErrorAtContext(context, $"Call to undefined function {funcName}()");
                SetType(context, SymbolTable.VOID);
                return SymbolTable.VOID;
            }

            // Check argument count and types.
            var paramSymbols = new List<Symbol>(methSym.Members.Values);
            if (argTypes.Count != paramSymbols.Count)
            {
                string accepted = AcceptedSignatures(funcName);
                ErrorAtContext(context, accepted == null
                    ? $"Function '{funcName}' expects {paramSymbols.Count} arguments, got {argTypes.Count}"
                    : $"Function '{funcName}' got {argTypes.Count} arguments; it accepts {accepted}");
            }
            else
            {
               for (int i = 0; i < argTypes.Count; i++)
                {
                    ISymbolType paramType = paramSymbols[i].Type;
                    ISymbolType argType   = argTypes[i];
                    ISymbolType promo = SymbolTable.promoteFromTo[Idx(argType), Idx(paramType)];
                    if (!_symtab.CanAssignTo(argType, paramType, promo))
                    {
                        ErrorAtContext(context,
                            $"Argument {i + 1} of '{funcName}': cannot pass {argType?.Name} as {paramType?.Name}");
                    }
                    else if (promo != null)
                    {
                        SetPromote(context.callParamList().expr()[i], promo);
                    }
                } 
            }

            ISymbolType retType = methSym.Type ?? SymbolTable.VOID;
            SetType(context, retType);
            return retType;
        }

        // ── Subscript (vector/rotation member access) ─────────────────────────

        public override ISymbolType VisitSubscriptPostfix(
            [NotNull] LSLParser.SubscriptPostfixContext context)
        {
            ISymbolType baseType = Visit(context.postfixExpression());
            // Subscript (.x .y .z .s) always returns float. Only a vector or rotation VARIABLE has members (Halcyon's
            // SymbolTable.SubScript; SL refuses llGetPos().z, which compiled to no code at all).
            bool isVariable = context.postfixExpression() is LSLParser.PrimaryExprContext pc
                && pc.primary() is LSLParser.IdExprContext id && _annotations.GetSymbol(id) is VariableSymbol;
            CheckSubscript(context.postfixExpression().Start, isVariable ? baseType : null, context.ID().GetText());
            SetType(context, SymbolTable.FLOAT);
            return SymbolTable.FLOAT;
        }

        // ── Primary expressions ───────────────────────────────────────────────

        public override ISymbolType VisitIntegerLiteral(
            [NotNull] LSLParser.IntegerLiteralContext context)
        {
            SetType(context, SymbolTable.INT);
            return SymbolTable.INT;
        }

        public override ISymbolType VisitFloatLiteral(
            [NotNull] LSLParser.FloatLiteralContext context)
        {
            SetType(context, SymbolTable.FLOAT);
            return SymbolTable.FLOAT;
        }

        public override ISymbolType VisitStringLiteral(
            [NotNull] LSLParser.StringLiteralContext context)
        {
            SetType(context, SymbolTable.STRING);
            return SymbolTable.STRING;
        }

        public override ISymbolType VisitVectorLiteralExpr(
            [NotNull] LSLParser.VectorLiteralExprContext context)
        {
            // Visit the three component expressions inside vecLiteral.
            VisitChildren(context);
            CheckComponents(context.vecLiteral().expr(), "Vector");
            SetType(context, SymbolTable.VECTOR);
            return SymbolTable.VECTOR;
        }

        public override ISymbolType VisitRotationLiteralExpr(
            [NotNull] LSLParser.RotationLiteralExprContext context)
        {
            VisitChildren(context);
            CheckComponents(context.rotLiteral().expr(), "Rotation");
            SetType(context, SymbolTable.ROTATION);
            return SymbolTable.ROTATION;
        }

        public override ISymbolType VisitListLiteralExpr(
            [NotNull] LSLParser.ListLiteralExprContext context)
        {
            // Any value but a list (Halcyon's SymbolTable.CheckListLiteral; SL refuses a nested list too).
            VisitChildren(context);
            var elements = context.listLiteral().listContents()?.expr();
            if (elements != null)
            {
                foreach (var e in elements)
                {
                    ISymbolType t = TypeOf(e);
                    if (t == SymbolTable.LIST)
                        ErrorAtContext(context, "A list can not contain another list");
                    else CheckHasValue(e, t);
                }
            }
            SetType(context, SymbolTable.LIST);
            return SymbolTable.LIST;
        }

        public override ISymbolType VisitParenExpr([NotNull] LSLParser.ParenExprContext context)
        {
            ISymbolType t = Visit(context.expression());
            SetType(context, t);
            return t;
        }

		public override ISymbolType VisitIdExpr([NotNull] LSLParser.IdExprContext context)
		{
			string name = context.ID().GetText();

			// Walk up the parse tree to find the nearest scope annotation
			IScope scope = null;
			IParseTree node = context;
			while (node != null && scope == null)
			{
				scope = _annotations.GetScope(node);
				node = node.Parent;
			}
			if (scope == null) scope = _symtab.Globals;

			Symbol sym = _symtab.ResolveVisible(scope, name, context.ID().Symbol.TokenIndex, out bool declaredLater)
				?? scope.Resolve(name + "()");
			if (sym == null)
			{
				// A callee's name is reported by the call, as "Call to undefined function".
				if (!IsCallee(context))
					Error(context.ID().Symbol, declaredLater
						? $"Symbol '{name}' can not be used before it is defined"
						: $"Undefined symbol '{name}'");
				SetType(context, SymbolTable.VOID);
				return SymbolTable.VOID;
			}

			_annotations.SetSymbol(context, sym);
			ISymbolType t = sym.Type ?? SymbolTable.VOID;
			SetType(context, t);
			return t;
		}

// ── FuncCall (standalone statement) ──────────────────────────────────

        public override ISymbolType VisitFuncCall([NotNull] LSLParser.FuncCallContext context)
        {
            string funcName = context.ID().GetText();

            // Arguments first, because the arity chooses the overload. A
            // statement-level call reaches this visitor rather than VisitMethodCallPostfix, and
            // missing that is why 2b resolved nothing - both paths must use the same rule.
            List<ISymbolType> argTypes = new List<ISymbolType>();
            if (context.callParamList() != null)
                foreach (var expr in context.callParamList().expr())
                    argTypes.Add(Visit(expr));

            MethodSymbol methSym = ResolveCall(context, funcName, argTypes);
            if (methSym != null) _annotations.SetSymbol(context, methSym);   // The gen pass reads this choice

            if (methSym == null)
            {
                ErrorAtContext(context, $"Call to undefined function {funcName}()");
                SetType(context, SymbolTable.VOID);
                return SymbolTable.VOID;
            }

            var paramSymbols = new List<Symbol>(methSym.Members.Values);
            if (argTypes.Count != paramSymbols.Count)
            {
                string accepted = AcceptedSignatures(funcName);
                ErrorAtContext(context, accepted == null
                    ? $"Function '{funcName}' expects {paramSymbols.Count} arguments, got {argTypes.Count}"
                    : $"Function '{funcName}' got {argTypes.Count} arguments; it accepts {accepted}");
            }
            else
            {
                var exprs = context.callParamList().expr();
                for (int i = 0; i < argTypes.Count; i++)
                {
                    ISymbolType paramType = paramSymbols[i].Type;
                    ISymbolType argType   = argTypes[i];
                    ISymbolType promo = SymbolTable.promoteFromTo[Idx(argType), Idx(paramType)];
			if (!_symtab.CanAssignTo(argType, paramType, promo))
                    {
                        ErrorAtContext(context,
                            $"Argument {i + 1} of '{funcName}': cannot pass {argType?.Name} as {paramType?.Name}");
                    }
                    else if (promo != null)
                    {
                        SetPromote(context.callParamList().expr()[i], promo);
                    }
                }                    
            }

            ISymbolType retType = methSym.Type ?? SymbolTable.VOID;
            SetType(context, retType);
            return retType;
        }

        // ── Default ───────────────────────────────────────────────────────────

        public override ISymbolType VisitTerminal(ITerminalNode node) => null;

        protected override ISymbolType AggregateResult(ISymbolType aggregate, ISymbolType nextResult)
            => nextResult ?? aggregate;

        // ── Private utilities ─────────────────────────────────────────────────

        private void ErrorAtContext(ParserRuleContext ctx, string msg)
            => Error(ctx.Start.Line, ctx.Start.Column, msg);

        /// <summary>
        /// Halcyon's SymbolTable.HaveSameTypes: equal, or equal once one side takes LSL's implicit conversion
        /// (integer to float, key to string, string to key).
        /// </summary>
        private static bool HaveSameTypes(ISymbolType a, ISymbolType b)
            => a == b
            || SymbolTable.promoteFromTo[a.TypeIndex, b.TypeIndex] == b
            || SymbolTable.promoteFromTo[b.TypeIndex, a.TypeIndex] == a;

        /// <summary>
        /// A member (.x .y .z .s) of <paramref name="baseType"/>, null when the base is not a variable. Halcyon's
        /// messages (SymbolTable.SubScript): a vector has x, y and z; a rotation x, y, z and s.
        /// </summary>
        private bool CheckSubscript(IToken at, ISymbolType baseType, string member)
        {
            if (baseType != SymbolTable.VECTOR && baseType != SymbolTable.ROTATION)
            {
                Error(at, $"Use of subscript .{member} requires a vector or rotation variable");
                return false;
            }
            if (member != "x" && member != "y" && member != "z" && !(member == "s" && baseType == SymbolTable.ROTATION))
            {
                Error(at, $"Invalid subscript .{member}");
                return false;
            }
            return true;
        }

        /// <summary>Vector and rotation components are floats or integers (Halcyon's CheckVectorLiteral / CheckRotationLiteral).</summary>
        private void CheckComponents(LSLParser.ExprContext[] parts, string kind)
        {
            foreach (var e in parts)
            {
                ISymbolType t = TypeOf(e);
                if (t != null && t != SymbolTable.FLOAT && t != SymbolTable.INT)
                {
                    ErrorAtContext(parts[0], $"{kind} components must be float or implicitly convertable to float ");
                    return;
                }
            }
        }

        /// <summary>
        /// A call to a function that returns nothing, used where a value is needed: the call pushes no value, so the
        /// operator after it would take the wrong operand. SL refuses it at compile time.
        /// </summary>
        private void CheckHasValue(ParserRuleContext e, ISymbolType t)
        {
            if (e != null && t == SymbolTable.VOID)
                ErrorAtContext(e, "A function that returns no value can not be used as a value");
        }

        /// <summary>The name of a call in an expression: f in f(...).</summary>
        private static bool IsCallee(LSLParser.IdExprContext id)
            => id.Parent is LSLParser.PrimaryExprContext pc
            && pc.Parent is LSLParser.MethodCallPostfixContext call
            && call.postfixExpression() == pc;

        /// <summary>
        /// The variable of a statement-level ++x; x++; --x; x--; (separate grammar rules from the expression forms):
        /// it must be in scope at this point, and a member must be one the variable has.
        /// </summary>
        private void CheckStatementTarget(ParserRuleContext context, ITerminalNode id, IToken subscript)
        {
            IScope scope = null;
            IParseTree node = context;
            while (node != null && scope == null)
            {
                scope = _annotations.GetScope(node);
                node = node.Parent;
            }
            string name = id.GetText();
            Symbol sym = _symtab.ResolveVisible(scope ?? _symtab.Globals, name, id.Symbol.TokenIndex, out bool declaredLater);
            if (!(sym is VariableSymbol varSym) || sym is ConstantSymbol)
            {
                Error(id.Symbol, sym != null ? $"'{name}' is not assignable"
                    : declaredLater ? $"Symbol '{name}' can not be used before it is defined"
                    : $"Undefined symbol '{name}'");
                return;
            }
            if (subscript != null) CheckSubscript(id.Symbol, varSym.Type, subscript.Text);
        }

        public override ISymbolType VisitPreIncrementStmt([NotNull] LSLParser.PreIncrementStmtContext context)
        {
            CheckStatementTarget(context, context.ID(0), context.subscript);
            return null;
        }

        public override ISymbolType VisitPreDecrementStmt([NotNull] LSLParser.PreDecrementStmtContext context)
        {
            CheckStatementTarget(context, context.ID(0), context.subscript);
            return null;
        }

        public override ISymbolType VisitPostIncrementStmt([NotNull] LSLParser.PostIncrementStmtContext context)
        {
            CheckStatementTarget(context, context.ID(0), context.subscript);
            return null;
        }

        public override ISymbolType VisitPostDecrementStmt([NotNull] LSLParser.PostDecrementStmtContext context)
        {
            CheckStatementTarget(context, context.ID(0), context.subscript);
            return null;
        }

        // A condition must be a value: a function that returns nothing pushes none.
        public override ISymbolType VisitIfStmt([NotNull] LSLParser.IfStmtContext context)
        {
            VisitChildren(context);
            CheckHasValue(context.expression(), TypeOf(context.expression()));
            return null;
        }

        public override ISymbolType VisitWhileStmt([NotNull] LSLParser.WhileStmtContext context)
        {
            VisitChildren(context);
            CheckHasValue(context.expression(), TypeOf(context.expression()));
            return null;
        }

        public override ISymbolType VisitDoWhileStmt([NotNull] LSLParser.DoWhileStmtContext context)
        {
            VisitChildren(context);
            CheckHasValue(context.expression(), TypeOf(context.expression()));
            return null;
        }

        public override ISymbolType VisitForStmt([NotNull] LSLParser.ForStmtContext context)
        {
            VisitChildren(context);
            var cond = context.cond?.expression();
            CheckHasValue(cond, cond == null ? null : TypeOf(cond));
            return null;
        }

        private static ParserRuleContext AssignmentTarget(LSLParser.BooleanExpressionContext target)
            => GenVisitor.AssignmentTarget(target);

        /// <summary>
        /// Extracts the assignment operator text from an AssignmentExpressionContext.
        /// The operator is a terminal token between the two sub-expressions.
        /// </summary>
        private string GetAssignOp(LSLParser.AssignmentExpressionContext ctx)
        {
            // Walk the children to find the operator token.
            for (int i = 0; i < ctx.ChildCount; i++)
            {
                IParseTree child = ctx.GetChild(i);
                if (child is ITerminalNode tn)
                {
                    string txt = tn.GetText();
                    switch (txt)
                    {
                        case "=": case "+=": case "-=": case "*=":
                        case "/=": case "%=": case "<<=": case ">>=":
                            return txt;
                    }
                }
            }
            return "=";
        }

        /// <summary>
        /// The operator before the rhsIndex-th operand of a flat (expr (op expr)*) rule: it sits at
        /// child position 2*rhsIndex - 1.
        /// </summary>
        private static string GetOpAt(ParserRuleContext ctx, int rhsIndex)
        {
            int opPos = 2 * rhsIndex - 1;
            return opPos < ctx.ChildCount && ctx.GetChild(opPos) is ITerminalNode tn ? tn.GetText() : string.Empty;
        }

        /// <summary>
        /// Returns the operator between the i-th pair of multiplicative children.
        /// </summary>
        private string GetMultOp(LSLParser.MultiplicativeExpressionContext ctx, int rhsIndex)
        {
            // Children are: expr (op expr)*
            // The op for rhsIndex-th rhs is at child position 2*rhsIndex - 1.
            int opPos = 2 * rhsIndex - 1;
            if (opPos < ctx.ChildCount)
            {
                IParseTree child = ctx.GetChild(opPos);
                if (child is ITerminalNode tn)
                    return tn.GetText();
            }
            return "*";
        }

        /// <summary>
        /// Extracts the function name from a postfixExpression that is an IdExpr.
        /// </summary>
        private string GetCallName(LSLParser.PostfixExpressionContext ctx)
        {
            // PrimaryExprContext → PrimaryContext → IdExprContext
            if (ctx is LSLParser.PrimaryExprContext pc)
            {
                if (pc.primary() is LSLParser.IdExprContext id)
                    return id.ID().GetText();
            }
            return null;
        }

        /// <summary>
        /// Resolve a call to a method symbol, choosing among a built-in's overloads by
        /// the number of arguments at the call site. The bare name is tried first, so a
        /// single-signature built-in and every user function resolve exactly as they did; only a
        /// name that has a <c>name$&lt;arity&gt;</c> sibling can pick anything else.
        ///
        /// <para>Argument TYPES are checked by the caller afterwards, through the tree's existing
        /// implicit-conversion rule - <c>SymbolTable.promoteFromTo</c> plus <c>CanAssignTo</c>,
        /// which is LSL's integer-to-float widening and its interchangeable key and string.</para>
        /// </summary>
        private MethodSymbol ResolveCall(ParserRuleContext context, string funcName, List<ISymbolType> argTypes)
        {
            if (funcName == null) return null;

            MethodSymbol bare = _symtab.Globals.Resolve(funcName + "()") as MethodSymbol;
            if (bare == null) return null;

            int argCount = argTypes.Count;

            // A user function or a built-in with one signature: exactly the old path, no selection.
            if (!Defaults.SystemMethods.TryGetValue(funcName, out var sigs) || sigs.Count < 2)
                return bare;

            // Among the signatures of this arity, the argument TYPES choose.
            var argVarTypes = new List<VarType?>(argCount);
            foreach (ISymbolType t in argTypes)
            {
                int i = Idx(t);
                argVarTypes.Add(i >= 0 && i < (int)VarType.Void ? (VarType?)i : null);
            }

            FunctionSig? chosen = Defaults.SelectOverload(funcName, argVarTypes, out FunctionSig? other);
            if (!chosen.HasValue)
            {
                // No signature of this arity at all, or none whose types can be reached: fall back
                // to the arity sibling so the argument-count / argument-type error below reads as it
                // always did, against a signature of the right size where one exists.
                if (bare.Members.Count == argCount) return bare;
                if (_symtab.Globals.Resolve(funcName + Defaults.OverloadSeparator + argCount + "()") is MethodSymbol byArity)
                    return byArity;
                return bare;
            }

            if (other.HasValue)
            {
                ErrorAtContext(context,
                    $"Call to '{funcName}' is ambiguous between {Defaults.DescribeSignature(chosen.Value)} and {Defaults.DescribeSignature(other.Value)}");
            }

            return _symtab.Globals.Resolve(Defaults.SymbolNameFor(chosen.Value) + "()") as MethodSymbol ?? bare;
        }

        /// <summary>Every arity a built-in accepts, for the error message when none of them match.</summary>
        private string AcceptedSignatures(string funcName)
        {
            if (funcName == null || !Defaults.SystemMethods.TryGetValue(funcName, out var sigs))
                return null;
            var forms = new List<string>();
            foreach (var sig in sigs)
                forms.Add(funcName + "(" + string.Join(", ", Array.ConvertAll(sig.ParamTypes, t => t.ToString().ToLowerInvariant())) + ")");
            return string.Join("; ", forms);
        }

    }
}
