using System;
using System.Runtime.CompilerServices;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;

namespace InWorldz.Phlox.Compiler
{
    /// <summary>
    /// This is now the BACKSTOP only; the primary rule is the counted <see cref="NestingLimits"/>, which
    /// does not move with JIT warm-up the way remaining stack does.
    /// Both front ends are recursive descent over the script's own nesting, so a script
    /// nested deeply enough overflowed the stack - which no catch can stop, and which ends the region.
    /// Every recursive entry calls <see cref="Check(int,int)"/>; when the thread is running out of
    /// stack it throws <see cref="NestingTooDeepException"/>, which the front ends report as an
    /// ordinary compile error.
    /// </summary>
    public static class DepthGuard
    {
        public const string Message = "expression nested too deeply";

        public static void Check(int line, int column)
        {
            try { RuntimeHelpers.EnsureSufficientExecutionStack(); }
            catch (InsufficientExecutionStackException) { throw new NestingTooDeepException(line, column); }
        }

        public static void Check(IToken token) => Check(token?.Line ?? 0, token?.Column ?? 0);

        public static void Check(IParseTree tree)
        {
            if (tree is ParserRuleContext ctx) Check(ctx.Start);
            else if (tree is ITerminalNode t) Check(t.Symbol);
            else Check(0, 0);
        }
    }

    public sealed class NestingTooDeepException : Exception
    {
        public int Line { get; }
        public int Column { get; }
        /// <summary>The counted limit that was passed, or null for the stack backstop.</summary>
        public NestingKind? Kind { get; }

        /// <summary>The stack backstop (<see cref="DepthGuard.Check(int,int)"/>).</summary>
        public NestingTooDeepException(int line, int column) : base(DepthGuard.Message)
        {
            Line = line;
            Column = column;
        }

        /// <summary>A counted limit (<see cref="NestingLimits"/>).</summary>
        public NestingTooDeepException(int line, int column, NestingKind kind) : base(NestingLimits.Message(kind))
        {
            Line = line;
            Column = column;
            Kind = kind;
        }
    }
}
