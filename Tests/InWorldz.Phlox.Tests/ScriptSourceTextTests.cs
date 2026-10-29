using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// What happens to the script text before and while it is tokenised.
/// 1. The paste clean-up (CompilerFrontend.SanitizeScript drops invisible non-ASCII characters outside strings) tracks
///    strings and comments the way the lexer does, so a string's contents are never altered: SL keeps them verbatim.
///    It used to treat any quote after one backslash as escaped and ignored comments, so after "x\\" or a comment
///    holding one quote the next string lost its non-ASCII characters.
/// 2. A character the lexer does not recognise is a compile error with its line (SL: a syntax error; Halcyon passed
///    lexer diagnostics to the compile listener). It used to be printed to the server console and skipped.
/// Compile-and-run only; no process-wide state, so the class runs in parallel.
/// </summary>
public class ScriptSourceTextTests
{
    private static void AssertSays(ExprRunner.Result r, params string[] expect)
        => Assert.True(r.Ok && r.Said.SequenceEqual(expect),
            $"got {r.Describe()} (expect [{string.Join(" | ", expect)}])");

    [Fact]
    public void AStringAfterOneEndingInAnEscapedBackslashKeepsItsCharacters()
        => AssertSays(ExprRunner.RunInDefault("llOwnerSay(\"x\\\\\"); llOwnerSay(\"Grüße\");"), "x\\", "Grüße");

    [Fact]
    public void AStringWithAnEscapedQuoteKeepsItsCharacters()
        => AssertSays(ExprRunner.RunInDefault("llOwnerSay(\"a \\\" ü\"); llOwnerSay(\"é\");"), "a \" ü", "é");

    [Fact]
    public void AStringAfterALineCommentHoldingAQuoteKeepsItsCharacters()
        => AssertSays(ExprRunner.RunInDefault("// say \"hi\n llOwnerSay(\"Grüße\");"), "Grüße");

    [Fact]
    public void AStringAfterABlockCommentHoldingAQuoteKeepsItsCharacters()
        => AssertSays(ExprRunner.RunInDefault("/* a \" b */ llOwnerSay(\"Grüße\"); /* \" */ llOwnerSay(\"日本\");"), "Grüße", "日本");

    [Fact]
    public void ACommentInsideAStringIsPartOfTheString()
        => AssertSays(ExprRunner.RunInDefault("llOwnerSay(\"// ü\"); llOwnerSay(\"/* é */\");"), "// ü", "/* é */");

    [Fact]
    public void NonAsciiInACommentStillCompiles()
        => AssertSays(ExprRunner.RunInDefault("// Grüße \" \n /* 日本 \" */ llOwnerSay(\"ok\");"), "ok");

    [Fact]
    public void AnInvisibleCharacterOutsideAStringIsStillDropped()
        => AssertSays(ExprRunner.RunInDefault("integer i = 1;​ llOwnerSay(\"ok\" + \" \");"), "ok ");

    [Theory]
    [InlineData("integer i# = 1;")]
    [InlineData("integer $i = 1;")]
    [InlineData("integer i` = 1;")]
    [InlineData("llOwnerSay(\"a\");#")]
    public void AnUnrecognisedCharacterIsACompileError(string body)
    {
        var c = PhloxCompiler.CompileInDefault(body);
        Assert.True(c.HasErrors(), $"'{body}' compiled; SL rejects it");
        Assert.True(c.Errors.Any(e => e.StartsWith("line ")), $"'{body}': no line-numbered error: {c.Report}");
    }

    [Fact]
    public void TheSameCharactersInAStringOrACommentStillCompile()
        => AssertSays(ExprRunner.RunInDefault("// # $ `\n /* # $ ` */ llOwnerSay(\"#$`\");"), "#$`");
}
