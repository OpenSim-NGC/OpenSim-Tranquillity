using InWorldz.Phlox.ByteCompiler;
using InWorldz.Phlox.Types;
using Xunit;

namespace InWorldz.Phlox.Tests;

public class StringEscapeTests
{
    [Theory]
    [InlineData(@"\u0000", "\0")]
    [InlineData(@"\u0041", "A")]
    [InlineData(@"\u00e9", "\u00e9")]
    [InlineData(@"\u00E9", "\u00e9")]
    [InlineData(@"\u4e2d\u6587", "\u4e2d\u6587")]
    [InlineData(@"\uFFFF", "\uffff")]
    [InlineData(@"\uD83D\uDE00", "\ud83d\ude00")]
    [InlineData(@"before\u00e9after", "before\u00e9after")]
    [InlineData(@"\u0041B", "AB")]
    [InlineData(@"\u005cu0041", @"\u0041")]
    [InlineData(@"\u0022", "\"")]
    [InlineData(@"\u000a\u0009", "\n\t")]
    [InlineData(@"\\u0041", @"\u0041")]
    [InlineData(@"\\\u0041", @"\A")]
    [InlineData(@"/u0041", "/u0041")]
    [InlineData(@"a\nb\tc\" + "\"" + @"d\\", "a\nb    c\"d\\")]
    public void StringEscapesReachRuntimeLocallyAndGlobally(string source, string expected)
    {
        var result = ExprRunner.RunInDefault(
            $"string local = \"{source}\"; llOwnerSay(local); llOwnerSay(global);",
            $"string global = \"{source}\";");

        Assert.True(result.Ok, result.Describe());
        Assert.Equal(new[] { expected, expected }, result.Said);
    }

    [Theory]
    [InlineData(@"\u")]
    [InlineData(@"\u0")]
    [InlineData(@"\u000")]
    [InlineData(@"\u000g")]
    [InlineData(@"\u-001")]
    [InlineData(@"\u 001")]
    public void MalformedUnicodeEscapesAreCompileErrors(string source)
    {
        var result = PhloxCompiler.CompileInDefault($"llOwnerSay(\"{source}\");");
        Assert.True(result.HasErrors(), result.Report);
        Assert.Throws<GenerationException>(() =>
            new BytecodeGenerator(Array.Empty<FunctionSig>()).UnescapeStringChars(source));
    }

    [Fact]
    public void UnicodeAndExistingAssemblerEscapesAreDecodedOnce()
    {
        Assert.Equal("\u00e9\n\r    \"\\", new BytecodeGenerator(Array.Empty<FunctionSig>()).UnescapeStringChars(
            @"\u00e9\n\r\t\" + "\"" + @"\\"));
    }
}
