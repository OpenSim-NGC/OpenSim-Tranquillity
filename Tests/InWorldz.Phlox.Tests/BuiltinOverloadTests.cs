using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Built-in (<c>ll*</c> / <c>os*</c>) functions that OSSL overloads must resolve by
/// name **and** signature, as they do on every other engine that offers them.
///
/// <para>
/// The in-world case is `9898c41e-8e85-45ed-9235-be1cc6176936`, which failed at load
/// with <i>"Function 'osTeleportAgent' expects 4 arguments, got 3"</i> at lines
/// 16:12 and 20:12. Its call is the 3-argument local-teleport overload, which
/// <c>OSSL_Api.cs:1051</c> implements and Phlox's table does not carry
/// (<c>InWorldz.Phlox/Types/Defaults.cs:4733</c> holds only the 4-argument form).
/// </para>
///
/// <para>
/// <b>Scope:</b> this is about BUILT-IN overloads only. User-function overloading stays
/// rejected — SL rejects it, so Phlox's rule is the parity rule. <see cref="UserFunctionOverloadTests"/>
/// pins that, with the airship script as the example.
/// </para>
/// </summary>
public class BuiltinOverloadTests
{
    // ------------------------------------------------------------------ osTeleportAgent

    [Fact]
    public void The3ArgLocalTeleportOverloadCompiles()
    {
        // Verbatim from the failing script (asset 01d4448d-087e-49fb-9253-4b06ce522811, line 16).
        var c = PhloxCompiler.CompileInDefault(@"
        key id = llDetectedKey(0);
        vector opos = llGetPos();
        osTeleportAgent(id, opos + <0,0,-3>, <0,1,0>);");

        Assert.False(c.HasErrors(), $"osTeleportAgent(key, vector, vector) is OSSL_Api.cs:1051: {c.Report}");
    }

    [Fact]
    public void The5ArgGridCoordinateOverloadCompiles()
    {
        // OSSL_Api.cs:1015 - osTeleportAgent(string agent, int regionX, int regionY, vector, vector)
        var c = PhloxCompiler.CompileInDefault(@"
        key id = llDetectedKey(0);
        osTeleportAgent(id, 1000, 1000, <128,128,25>, <0,1,0>);");

        Assert.False(c.HasErrors(), $"osTeleportAgent(key, int, int, vector, vector) is OSSL_Api.cs:1015: {c.Report}");
    }

    [Fact]
    public void The4ArgRegionNameOverloadStillCompiles()
    {
        // The one Phlox already had. It must survive the change that adds the other two.
        var c = PhloxCompiler.CompileInDefault(@"
        key id = llDetectedKey(0);
        osTeleportAgent(id, ""Test Region"", <128,128,25>, <0,1,0>);");

        Assert.False(c.HasErrors(), $"the 4-argument form is the one Phlox has always had: {c.Report}");
    }

    // ------------------------------------------------------------------ llLinkPlaySound

    [Fact]
    public void LlLinkPlaySoundBothFormsCompile()
    {
        var three = PhloxCompiler.CompileInDefault(@"llLinkPlaySound(LINK_THIS, ""snd"", 1.0);");
        Assert.False(three.HasErrors(), $"llLinkPlaySound(int, string, float): {three.Report}");

        var four = PhloxCompiler.CompileInDefault(@"llLinkPlaySound(LINK_THIS, ""snd"", 1.0, 0);");
        Assert.False(four.HasErrors(), $"llLinkPlaySound(int, string, float, int): {four.Report}");
    }

    // ------------------------------------------------------------------ SL names and arities

    /// <summary>
    /// Compile a state_entry body, run it against the recording ISystemAPI, and return every
    /// syscall that reached the API as "name(arg, arg)" - so a test can say not just that the SL
    /// spelling COMPILES but that it DISPATCHES to the SL body and not the older one. The two
    /// forms of each function differ in arity, and the recording proxy records the CLR method's
    /// argument list, so the count tells them apart.
    /// </summary>
    private static List<string> Run(string body)
    {
        var compiled = PhloxCompiler.CompileTo(
            "default { state_entry() { " + body + " } }", out var listener);
        Assert.False(listener.HasErrors(), listener.Report);
        Assert.NotNull(compiled);
        var api = RecordingSystemApi.Create(out var calls);
        var shim = new InWorldz.Phlox.Glue.SyscallShim(call => call());
        shim.SystemAPI = api;
        var interp = new InWorldz.Phlox.VM.Interpreter(compiled, shim);
        shim.Interpreter = interp;
        var info = compiled.FindEvent(interp.ScriptState.LSLState,
            (int)InWorldz.Phlox.Types.SupportedEventList.Events.STATE_ENTRY);
        Assert.NotNull(info);
        interp.ScriptState.DoEvent(info,
            new InWorldz.Phlox.VM.PostedEvent { EventType = InWorldz.Phlox.Types.SupportedEventList.Events.STATE_ENTRY, Args = Array.Empty<object>() },
            Array.Empty<object>());
        try { for (var i = 0; i < 100_000 && interp.ScriptState.RunningEvent != null; i++) interp.Tick(); }
        catch (InvalidOperationException) { /* ticked past the end of the event */ }
        return calls;
    }

    // The recording ISystemAPI returns null for string and list results, and storing null into a
    // local trips Op_Store - so each SL form is called as a statement and the VM pops the result.
    // The call reaching the API is what is under test, not the store.
    /// <summary>
    /// The calls of this name that reached the API - at least one - so the test can assert that
    /// EVERY one of them carried the SL arity. Driving the interpreter by hand, without the
    /// scheduler, replays state_entry once more after it finishes (every recorded call appears
    /// twice), so "exactly one" is a claim about the harness, not about dispatch; "all of them
    /// have the SL shape" is the claim that matters.
    /// </summary>
    private static List<string> AllOf(List<string> calls, string name)
    {
        var hits = calls.Where(c => c.StartsWith(name + "(")).ToList();
        Assert.True(hits.Count >= 1, $"no {name} call reached the API; all syscalls: [{string.Join(" | ", calls)}]");
        return hits;
    }

    // The recording ISystemAPI returns null for string and list results, and storing null into a
    // local trips Op_Store - so each SL form is called as a statement and the VM pops the result.
    // The call reaching the API is what is under test, not the store.
    /// <summary>Exactly one call of this name reached the API, and here is everything that did if not.</summary>
    private static int Arity(string call) => call.EndsWith("()") ? 0 : call.Count(c => c == ',') + 1;

    [Fact]
    public void LlsRGB2LinearDispatchesToTheSlSpelling()
    {
        // wiki.secondlife.com/wiki/LlsRGB2Linear - lowercase s. Phlox only had llSRGB2Linear.
        var calls = Run("llsRGB2Linear(<0.5, 0.5, 0.5>);");
        Assert.Contains(calls, c => c.StartsWith("llsRGB2Linear("));
        Assert.DoesNotContain(calls, c => c.StartsWith("llSRGB2Linear("));
    }

    [Fact]
    public void LlListSortStridedDispatchesToTheSlName()
    {
        // wiki.secondlife.com/wiki/LlListSortStrided. Phlox only had llSortListStrided.
        var calls = Run("llListSortStrided([1, \"a\", 2, \"b\"], 2, 0, TRUE);");
        Assert.Contains(calls, c => c.StartsWith("llListSortStrided("));
        Assert.DoesNotContain(calls, c => c.StartsWith("llSortListStrided("));
    }

    [Fact]
    public void LlSHA256StringOneArgDispatchesToTheSlBody()
    {
        // wiki: string llSHA256String(string src) - no nonce. Phlox's (src, nonce) form is 567.
        var calls = Run("llSHA256String(\"abc\");");
        var hits = AllOf(calls, "llSHA256String");
        Assert.All(hits, hit => Assert.Equal(1, Arity(hit)));
    }

    [Fact]
    public void LlTargetedEmailThreeArgDispatchesToTheSlBody()
    {
        // wiki: llTargetedEmail(integer target, string subject, string message).
        var calls = Run("llTargetedEmail(TARGETED_EMAIL_OBJECT_OWNER, \"s\", \"m\");");
        var hits = AllOf(calls, "llTargetedEmail");
        Assert.All(hits, hit => Assert.Equal(3, Arity(hit)));
        Assert.All(hits, hit => Assert.StartsWith("llTargetedEmail(2,", hit));   // the constant resolved to upstream's value
    }

    [Fact]
    public void LlUpdateKeyValueFourArgDispatchesToTheSlBody()
    {
        // wiki: key llUpdateKeyValue(string k, string v, integer checked, string original_value).
        var calls = Run("llUpdateKeyValue(\"k\", \"v\", TRUE, \"old\");");
        var hits = AllOf(calls, "llUpdateKeyValue");
        Assert.All(hits, hit => Assert.Equal(4, Arity(hit)));
    }

    [Fact]
    public void LlDerezObjectTwoArgDispatchesToTheSlBody()
    {
        // wiki: integer llDerezObject(key id, integer flag), with DEREZ_DIE = 0.
        var calls = Run("llDerezObject(llGetKey(), DEREZ_DIE);");
        var hits = AllOf(calls, "llDerezObject");
        Assert.All(hits, hit => Assert.Equal(2, Arity(hit)));
    }

    /// <summary>Every older Phlox spelling and arity must still compile - existing content
    /// depends on them. These are the aliases, and they are not going anywhere.</summary>
    [Theory]
    [InlineData("vector v = llSRGB2Linear(<0.5, 0.5, 0.5>);")]
    [InlineData("list l = llSortListStrided([1, \"a\", 2, \"b\"], 2, 0, TRUE);")]
    [InlineData("string h = llSHA256String(\"abc\", 7);")]
    [InlineData("llTargetedEmail(2, \"who@example.com\", \"s\", \"m\");")]
    [InlineData("key r = llUpdateKeyValue(\"k\", \"v\", \"old\");")]   // Answers in dataserver, returns its key
    [InlineData("llDerezObject(llGetKey());")]
    public void TheOlderSpellingStillCompiles(string body)
    {
        var c = PhloxCompiler.CompileInDefault(body);
        Assert.False(c.HasErrors(), body + ": " + c.Report);
    }

    // ------------------------------------------------------------------ still rejected, and helpfully

    [Fact]
    public void AnArityThatMatchesNoOverloadIsStillRejected()
    {
        // Two arguments is none of the three forms. Overload resolution must not become
        // "accept anything called osTeleportAgent".
        var c = PhloxCompiler.CompileInDefault(@"
        key id = llDetectedKey(0);
        osTeleportAgent(id, <0,1,0>);");

        Assert.True(c.HasErrors(), "a 2-argument osTeleportAgent matches no overload and must fail");
    }

    [Fact]
    public void TheRejectionListsTheAcceptedSignatures()
    {
        // The message that sent an earlier investigation looking at the wrong thing said "expects 4
        // arguments, got 3" - true of one overload and misleading about the function. With
        // several accepted forms the error has to say what they are.
        var c = PhloxCompiler.CompileInDefault(@"
        key id = llDetectedKey(0);
        osTeleportAgent(id, <0,1,0>);");

        Assert.True(c.HasErrors());
        var joined = string.Join(" | ", c.Errors);
        Assert.Contains("osTeleportAgent", joined);
        Assert.True(
            joined.Contains("accepts") || joined.Contains("overload") || joined.Contains("signature"),
            $"the error should name the accepted signatures, not one arity: {joined}");
    }
}

/// <summary>
/// The other half of the rule: <b>user-function overloading stays rejected.</b>
/// SL has no user-function overloading, so rejecting it is the parity behaviour and Phlox's
/// existing rule is correct. YEngine accepts it (its symbol table is keyed by name plus
/// signature, <c>MMRScriptVarDict.cs:132-151</c>), which makes the two engines in this tree
/// disagree; this test records that Phlox does not follow YEngine here, deliberately.
/// </summary>
public class UserFunctionOverloadTests
{
    [Fact]
    public void TwoUserFunctionsOfTheSameNameAreRejected()
    {
        // Reduced from the airship, asset b8079466-322a-47f5-ba8d-cd17d9e0da61, lines 92-96:
        //     SetVehicleSettings()          { SetVehicleSettings(""); }
        //     SetVehicleSettings(string f)  { ... }
        var c = PhloxCompiler.Compile(@"
SetVehicleSettings()
{
    SetVehicleSettings("""");
}
SetVehicleSettings(string filter)
{
    llSay(0, filter);
}
default
{
    state_entry()
    {
        SetVehicleSettings();
    }
}
");

        Assert.True(c.HasErrors(),
            "SL has no user-function overloading; Phlox rejecting it is the parity rule, not a defect");
        Assert.Contains("SetVehicleSettings", string.Join(" | ", c.Errors));
    }
}
