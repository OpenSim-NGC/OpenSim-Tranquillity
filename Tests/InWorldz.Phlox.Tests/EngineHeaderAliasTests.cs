/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using InWorldz.Phlox.Glue;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// "//Phlox:" is accepted wherever "//InWorldz.Phlox:" is: the region's engine selection (through the engine's header
/// alias), the engine's own ownership test, and the language part that picks LSL or SLua. The long name keeps working.
/// </summary>
public class EngineHeaderAliasTests
{
    private const string Phlox = "InWorldz.Phlox", Other = "SLua";
    private static readonly string[] Loaded = { Phlox, Other, "YEngine" };

    private static CompiledScriptResult Compile(string source)
    {
        var listener = new PhloxCompiler();
        var frontend = new CompilerFrontend(listener, templatePath: null);
        return new CompiledScriptResult(PhloxScriptLoader.CompileByLanguage(frontend, source), listener);
    }

    private sealed record CompiledScriptResult(object Script, PhloxCompiler Listener);

    [Fact]
    public void ThePhloxEngineNamesTheShortHeaderAndClaimsNothing()
    {
        var engine = new PhloxEngine();
        Assert.Equal(new[] { "Phlox" }, engine.HeaderAliases);
        Assert.False(engine.ClaimsScript("--!slua\nprint(1)"));
        Assert.False(engine.ClaimsScript("default {}"));
    }

    [Theory]
    [InlineData("//Phlox:\ndefault {}", Other)]
    [InlineData("//Phlox:slua\nprint(1)", Other)]
    [InlineData("// Phlox :lsl\ndefault {}", Other)]
    [InlineData("//Phlox:\ndefault {}", "YEngine")]
    [InlineData("//InWorldz.Phlox:\ndefault {}", Other)]
    [InlineData("//InWorldz.Phlox:slua\nprint(1)", "YEngine")]
    public void AHeaderNamingPhloxByEitherNameGivesItTheScriptWhateverTheDefault(string source, string defaultEngine)
    {
        Assert.Equal(Phlox, PhloxEngineHeader.Owner(source, defaultEngine, Loaded));
    }

    [Theory]
    [InlineData("//phlox:\ndefault {}")]
    [InlineData("//PHLOX:\ndefault {}")]
    [InlineData("//Phloxx:\ndefault {}")]
    [InlineData("//Phlox\ndefault {}")]
    public void TheShortNameIsReadAsWrittenAndNeedsItsColon(string source)
    {
        Assert.Equal(Phlox, PhloxEngineHeader.Owner("//Phlox:\ndefault {}", Other, Loaded));
        Assert.Equal(Other, PhloxEngineHeader.Owner(source, Other, Loaded));
    }

    [Fact]
    public void AHeaderNamingPhloxWhenPhloxIsNotLoadedFallsToTheDefault()
    {
        Assert.Equal(Phlox, PhloxEngineHeader.Owner("//Phlox:\ndefault {}", Other, Loaded));
        Assert.Equal(Other, PhloxEngineHeader.Owner("//Phlox:\ndefault {}", Other, new[] { Other, "YEngine" }));
    }

    [Fact]
    public void TheShortHeaderPicksTheLanguageLikeTheLongOne()
    {
        // SLua: the header line is blanked and the rest compiled as SLua.
        CompiledScriptResult slua = Compile("//Phlox:slua\nll.Say(0, \"x\")\n");
        Assert.NotNull(slua.Script);
        Assert.False(slua.Listener.HasErrors(), slua.Listener.Report);

        CompiledScriptResult lsl = Compile("//Phlox:\ndefault { state_entry() { llSay(0, \"x\"); } }\n");
        Assert.NotNull(lsl.Script);
        Assert.False(lsl.Listener.HasErrors(), lsl.Listener.Report);

        CompiledScriptResult lslNamed = Compile("//Phlox:LSL\ndefault { state_entry() { llSay(0, \"x\"); } }\n");
        Assert.NotNull(lslNamed.Script);
        Assert.False(lslNamed.Listener.HasErrors(), lslNamed.Listener.Report);
    }

    [Fact]
    public void TheShortHeaderWithAnotherLanguageIsACompileErrorOnLineOneNamingTheHeader()
    {
        Assert.NotNull(Compile("//Phlox:slua\nll.Say(0, \"x\")\n").Script);   // the same header with a language Phlox runs compiles

        CompiledScriptResult python = Compile("//Phlox:python\nprint(1)\n");
        Assert.Null(python.Script);
        Assert.Contains(python.Listener.Errors, e => e.Contains("//Phlox:python") && e.Contains("\"python\"") && e.Contains("line 1"));
    }

    [Fact]
    public void TheLongHeaderStillPicksTheLanguage()
    {
        Assert.NotNull(Compile("//InWorldz.Phlox:slua\nll.Say(0, \"x\")\n").Script);
        CompiledScriptResult python = Compile("//InWorldz.Phlox:python\nprint(1)\n");
        Assert.Null(python.Script);
        Assert.Contains(python.Listener.Errors, e => e.Contains("//InWorldz.Phlox:python"));
    }
}
