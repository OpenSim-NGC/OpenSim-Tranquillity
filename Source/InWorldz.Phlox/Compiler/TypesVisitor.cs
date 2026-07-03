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
            Symbol s = _symtab.Globals.Resolve(typeName);
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
            string stateName = context.ID().GetText();
            string key = stateName == "default" ? "default(*)" : stateName + "(*)";
            if (_symtab.Globals.Resolve(key) == null)
            {
                Error(context.ID().Symbol, $"Unknown state '{stateName}'");
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
			// Only treat as assignment if there's an operator token AND two direct sub-expressions
			string op = GetAssignOp(context);
			if (string.IsNullOrEmpty(op) || context.assignmentExpression(1) == null)
			{
				// Pure boolean expression passthrough
				ISymbolType t = context.booleanExpression() != null
					? Visit(context.booleanExpression())
					: VisitChildren(context);
				SetType(context, t ?? SymbolTable.VOID);
				return t;
			}

			ISymbolType lhsType = Visit(context.assignmentExpression(0));
			ISymbolType rhsType = Visit(context.assignmentExpression(1));
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
					if (promotion != null) SetPromote(context.assignmentExpression(1), promotion);
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
            foreach (var c in children) Visit(c);
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
            // == !=  — result integer
            foreach (var c in children) Visit(c);
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
            // < > <= >=  — result integer
            foreach (var c in children) Visit(c);
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
            // Walk left to right — each MINUS or implicit PLUS between children.
            var minusTokens = context.MINUS();
            for (int i = 1; i < children.Length; i++)
            {
                ISymbolType rhs = Visit(children[i]);
                bool isMinus = (minusTokens != null && i - 1 < minusTokens.Length);
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
            if (t != SymbolTable.INT && t != SymbolTable.FLOAT && t != SymbolTable.VECTOR)
                Error(context.MINUS().Symbol,
                    $"Unary minus cannot be applied to type {t?.Name}");
            SetType(context, t);
            return t;
        }

        public override ISymbolType VisitUnaryBoolNot([NotNull] LSLParser.UnaryBoolNotContext context)
        {
            Visit(context.unaryExpression());
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
            MethodSymbol methSym = funcName != null
                ? _symtab.Globals.Resolve(funcName + "()") as MethodSymbol
                : null;

            // Visit each argument expression.
            List<ISymbolType> argTypes = new List<ISymbolType>();
            if (context.callParamList() != null)
            {
                foreach (var expr in context.callParamList().expr())
                    argTypes.Add(Visit(expr));
            }

            if (methSym == null)
            {
                // Already reported by DefVisitor if undefined; just return void.
                SetType(context, SymbolTable.VOID);
                return SymbolTable.VOID;
            }

            // Check argument count and types.
            var paramSymbols = new List<Symbol>(methSym.Members.Values);
            if (argTypes.Count != paramSymbols.Count)
            {
                ErrorAtContext(context,
                    $"Function '{funcName}' expects {paramSymbols.Count} arguments, got {argTypes.Count}");
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
            // Subscript (.x .y .z .s) always returns float.
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
            SetType(context, SymbolTable.VECTOR);
            return SymbolTable.VECTOR;
        }

        public override ISymbolType VisitRotationLiteralExpr(
            [NotNull] LSLParser.RotationLiteralExprContext context)
        {
            VisitChildren(context);
            SetType(context, SymbolTable.ROTATION);
            return SymbolTable.ROTATION;
        }

        public override ISymbolType VisitListLiteralExpr(
            [NotNull] LSLParser.ListLiteralExprContext context)
        {
            // All element types are valid in LSL lists — just visit children.
            VisitChildren(context);
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

			Symbol sym = scope.Resolve(name) ?? scope.Resolve(name + "()");
			if (sym == null)
			{
				Error(context.ID().Symbol, $"Undefined symbol '{name}'");
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
            MethodSymbol methSym = _symtab.Globals.Resolve(funcName + "()") as MethodSymbol;

            List<ISymbolType> argTypes = new List<ISymbolType>();
            if (context.callParamList() != null)
                foreach (var expr in context.callParamList().expr())
                    argTypes.Add(Visit(expr));

            if (methSym == null)
            {
                SetType(context, SymbolTable.VOID);
                return SymbolTable.VOID;
            }

            var paramSymbols = new List<Symbol>(methSym.Members.Values);
            if (argTypes.Count != paramSymbols.Count)
            {
                ErrorAtContext(context,
                    $"Function '{funcName}' expects {paramSymbols.Count} arguments, got {argTypes.Count}");
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
    }
}
