using System.Collections.Generic;
using Antlr4.Runtime;
using InWorldz.Phlox.Compiler;

/// <summary>
/// The nesting checks on the generated parser's rule entry, kept out of LSLParser.cs so
/// regenerating the grammar does not drop them. The primary rule is COUNTED (<see cref="NestingLimits"/>);
/// <see cref="DepthGuard"/> stays as the stack backstop.
/// </summary>
public partial class LSLParser
{
    private readonly NestingCounter _nesting = new NestingCounter();
    // One entry per entry into a rule that CAN count (unaryExpression, assignmentExpression, statement, funcBlock),
    // null when that entry does not; ExitRule pops it. Labelled alternatives replace the context object after
    // entry, so the kind is remembered here rather than keyed on the context.
    private readonly Stack<NestingKind?> _nestFrames = new Stack<NestingKind?>();

    private static bool CanCount(int ruleIndex) =>
        ruleIndex == RULE_unaryExpression || ruleIndex == RULE_assignmentExpression ||
        ruleIndex == RULE_statement || ruleIndex == RULE_funcBlock;

    /// <summary>What an entry into <paramref name="ruleIndex"/> counts as - the same rule as NestingCounter.Classify on the finished tree.</summary>
    private NestingKind? KindAtEntry(ParserRuleContext localctx, int ruleIndex)
    {
        switch (ruleIndex)
        {
            case RULE_unaryExpression:
                return NestingKind.Expression;
            case RULE_assignmentExpression:
                return localctx.Parent is AssignmentExpressionContext ? NestingKind.AssignmentChain : (NestingKind?)null;
            case RULE_statement:
                // 'else if': this statement is the else branch and holds the next if of the chain.
                return TokenStream.LT(-1)?.Text == "else" && CurrentToken.Text == "if" ? NestingKind.ElseIfChain : NestingKind.Block;
            case RULE_funcBlock:
                return localctx.Parent is FuncBlockContentContext ? NestingKind.Block : (NestingKind?)null;
            default:
                return null;
        }
    }

    public override void EnterRule(ParserRuleContext localctx, int state, int ruleIndex)
    {
        DepthGuard.Check(CurrentToken);
        if (CanCount(ruleIndex))
        {
            var kind = KindAtEntry(localctx, ruleIndex);
            if (kind.HasValue) _nesting.Enter(kind.Value, CurrentToken.Line, CurrentToken.Column);
            _nestFrames.Push(kind);
        }
        base.EnterRule(localctx, state, ruleIndex);
    }

    public override void ExitRule()
    {
        if (Context != null && CanCount(Context.RuleIndex) && _nestFrames.Count > 0)
        {
            var kind = _nestFrames.Pop();
            if (kind.HasValue) _nesting.Exit(kind.Value);
        }
        base.ExitRule();
    }

    public override void EnterRecursionRule(ParserRuleContext localctx, int state, int ruleIndex, int precedence)
    {
        DepthGuard.Check(CurrentToken);
        base.EnterRecursionRule(localctx, state, ruleIndex, precedence);
    }
}
