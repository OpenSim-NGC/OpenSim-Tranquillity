using System;
using Antlr4.Runtime.Tree;

namespace InWorldz.Phlox.Compiler
{
    public enum NestingKind { Expression, Block, ElseIfChain, AssignmentChain }

    /// <summary>
    /// The nesting limits, COUNTED - the same in a cold or warm process, Debug or Release. The earlier
    /// stack-based guard (<see cref="DepthGuard"/>) tripped at ~3,017 expression levels in a fresh process and
    /// ~9,739 in a warm one, so one script compiled on a running region and failed in the harness; it stays as a
    /// backstop only.
    ///
    /// <para>What a level is. Expression: one construct wrapped around a value - a parenthesis, a unary
    /// operator, a cast, a call argument, a list/vector/table element, or one more link of a '..' (SLua)
    /// chain. <c>((1))</c> and <c>- - 1</c> are 2. Block: one statement body or bare block (LSL), one
    /// then/else/loop/function body (SLua). Else-if chain: the number of branches of one if / else if ...
    /// chain; its links are not blocks, because a 1,000-branch menu is real content. Assignment chain (LSL):
    /// the '=' links inside one expression - <c>x = a = b = 0</c> is 2.</para>
    ///
    /// <para>The values come from measurement (PhloxKnownDefects). Stack: the lowest cold capacity of
    /// the 16 MB compile thread is ~2,410 expression levels (a visitor pass), far higher for blocks and chains.
    /// Time: a script with a syntax error is parsed again with full-context (LL) prediction, whose cost grows much
    /// faster than the nesting for else-if chains (5,000 branches: 19.7 s cold) and assignment chains (100 links:
    /// 1 s; 1,000: over 15 minutes). Each limit sits under both and far above real scripts.</para>
    /// </summary>
    public static class NestingLimits
    {
        public const int Expression = 1000;
        public const int Block = 500;
        public const int ElseIfChain = 2500;
        public const int AssignmentChain = 64;

        public static int Limit(NestingKind k) => k switch
        {
            NestingKind.Expression => Expression,
            NestingKind.Block => Block,
            NestingKind.ElseIfChain => ElseIfChain,
            _ => AssignmentChain,
        };

        public static string Message(NestingKind k) => k switch
        {
            NestingKind.Expression => $"expression nested too deeply (limit {Expression})",
            NestingKind.Block => $"blocks nested too deeply (limit {Block})",
            NestingKind.ElseIfChain => $"else-if chain too long (limit {ElseIfChain})",
            _ => $"assignment chain too long (limit {AssignmentChain})",
        };
    }

    /// <summary>
    /// The active nesting of one parse or one tree walk. <see cref="Enter"/> throws
    /// <see cref="NestingTooDeepException"/> with the kind's message when a limit is passed.
    /// </summary>
    public sealed class NestingCounter
    {
        // Active frames per kind. An expression's base value is itself one frame (its unaryExpression), so
        // levels = frames - 1; a chain's first branch is the 'if', so branches = links + 1.
        private int _expr, _block, _chain, _assign;

        public void Enter(NestingKind k, int line, int column)
        {
            int levels;
            switch (k)
            {
                case NestingKind.Expression: levels = ++_expr - 1; break;
                case NestingKind.Block: levels = ++_block; break;
                case NestingKind.ElseIfChain: levels = ++_chain + 1; break;
                default: levels = ++_assign; break;
            }
            if (levels > NestingLimits.Limit(k)) throw new NestingTooDeepException(line, column, k);
        }

        public void Exit(NestingKind k)
        {
            switch (k)
            {
                case NestingKind.Expression: _expr--; break;
                case NestingKind.Block: _block--; break;
                case NestingKind.ElseIfChain: _chain--; break;
                default: _assign--; break;
            }
        }

        /// <summary>
        /// The LSL parse-tree classification the parser also applies at rule entry (LSLParserDepthGuard), so the
        /// visitors count the same levels over the finished tree.
        /// </summary>
        public static NestingKind? Classify(IParseTree tree)
        {
            switch (tree)
            {
                case LSLParser.UnaryExpressionContext _:
                    return NestingKind.Expression;
                case LSLParser.AssignmentExpressionContext a when a.Parent is LSLParser.AssignmentExpressionContext:
                    return NestingKind.AssignmentChain;
                case LSLParser.StatementContext s:
                    return s.Parent is LSLParser.IfStmtContext ifs && ifs.e == s && s.funcBlockContent() is LSLParser.IfStmtContext
                        ? NestingKind.ElseIfChain : NestingKind.Block;
                case LSLParser.FuncBlockContext b when b.Parent is LSLParser.FuncBlockContentContext:
                    return NestingKind.Block;
                default:
                    return null;
            }
        }
    }
}
