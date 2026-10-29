using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;
using OpenMetaverse;
using OpenSim.Tests.Common;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Service-lane golden test. Every converted syscall, called by one script with fixed arguments against
/// the same scene, with deferral OFF (the inline path) and deferral ALWAYS (every call through
/// the service lane), at zero added delay. The two transcripts - everything said on every channel,
/// including error shouts, plus the dataserver events - must be byte-identical. Values that are random
/// by design (a fresh request key, an NPC key) are reported as "is a key" so the comparison stays exact.
/// </summary>
public class ServiceCallGoldenTests
{
    private readonly ITestOutputHelper _out;
    public ServiceCallGoldenTests(ITestOutputHelper output) { _out = output; }

    private static readonly UUID Account = new UUID("5a5a5a5a-0000-4000-8000-00000000a001");
    private static readonly UUID Present = new UUID("5a5a5a5a-0000-4000-8000-00000000b002");

    private const string Script = @"
string G = ""5a5a5a5a-0000-4000-8000-00000000a001"";
string P = ""5a5a5a5a-0000-4000-8000-00000000b002"";
say(string s) { llSay(0, s); }
string isKey(string k) { if ((key)k) return ""key""; return ""no-key""; }
default
{
    state_entry()
    {
        say(""llGetDisplayName|"" + llGetDisplayName(G));
        say(""llGetDisplayName.here|"" + llGetDisplayName(P));
        say(""llGetUsername|"" + llGetUsername(G));
        say(""iwGetAgentData.2|"" + iwGetAgentData(G, 2));
        say(""iwGetAgentData.3|"" + iwGetAgentData(G, 3));
        say(""llName2Key|"" + (string)llName2Key(""Golden Avatar""));
        say(""llName2Key.none|"" + (string)llName2Key(""Nobody Here""));
        say(""osKey2Name|"" + osKey2Name(G));
        say(""osAvatarName2Key|"" + osAvatarName2Key(""Golden"", ""Avatar""));
        say(""osGetAvatarHomeURI|"" + osGetAvatarHomeURI(G));
        say(""osGetAgentCountry|"" + osGetAgentCountry(G));
        say(""osGetRegionMapTexture.here|"" + (string)osGetRegionMapTexture(""""));
        say(""osGetRegionMapTexture.none|"" + (string)osGetRegionMapTexture(""No Such Region""));
        say(""llGetNotecardLineSync|"" + llGetNotecardLineSync(""nc"", 1));
        say(""llGetNotecardLineSync.miss|"" + llGetNotecardLineSync(""nc"", 9));
        say(""llFindNotecardTextSync|"" + (string)llFindNotecardTextSync(""nc"", ""two"", 0, 0));
        say(""osGetNotecard|"" + osGetNotecard(""nc""));
        say(""osGetNotecardLine|"" + osGetNotecardLine(""nc"", 0));
        say(""osGetNumberOfNotecardLines|"" + (string)osGetNumberOfNotecardLines(""nc""));
        say(""llAgentInExperience|"" + (string)llAgentInExperience(G));
        say(""llGetExperienceDetails|"" + llList2CSV(llGetExperienceDetails(NULL_KEY)));
        say(""llReadKeyValue|"" + isKey((string)llReadKeyValue(""k"")));
        say(""llCreateKeyValue|"" + isKey((string)llCreateKeyValue(""k"", ""v"")));
        say(""llUpdateKeyValue|"" + isKey((string)llUpdateKeyValue(""k"", ""v2"", FALSE, """")));
        say(""llDeleteKeyValue|"" + isKey((string)llDeleteKeyValue(""k"")));
        say(""llKeyCountKeyValue|"" + isKey((string)llKeyCountKeyValue()));
        say(""llKeysKeyValue|"" + isKey((string)llKeysKeyValue(0, 10)));
        say(""llDataSizeKeyValue|"" + isKey((string)llDataSizeKeyValue()));
        say(""osInviteToGroup|"" + (string)osInviteToGroup(P));
        say(""osEjectFromGroup|"" + (string)osEjectFromGroup(P));
        say(""iwGroupInvite|"" + (string)iwGroupInvite(NULL_KEY, P, """"));
        say(""iwGroupEject|"" + (string)iwGroupEject(NULL_KEY, P));
        say(""llRequestSimulatorData|"" + isKey(llRequestSimulatorData(""No Such Region"", DATA_SIM_STATUS)));
        llDialog(P, ""golden"", [""a""], 7);                         say(""llDialog|done"");
        llTeleportAgent(P, """", <128, 128, 25>, <1, 0, 0>);          say(""llTeleportAgent|done"");
        llTeleportAgentGlobalCoords(P, <256000, 256000, 0>, <128, 128, 25>, <1, 0, 0>); say(""llTeleportAgentGlobalCoords|done"");
        llGiveAgentInventory(P, ""nc"");                             say(""llGiveAgentInventory|done"");
        osForceAttachToAvatarFromInventory(""no such object"", 1);   say(""osForceAttachToAvatarFromInventory|done"");
        osSetTerrainTextureHeight(0, 10.0, 20.0);                  say(""osSetTerrainTextureHeight|done"");
        say(""END"");
        llSetTimerEvent(3.0);
    }
    dataserver(key id, string data) { say(""dataserver|"" + data); }
    timer()
    {
        llSetTimerEvent(0.0);
        // Last on purpose: the test client does not implement the permission question, so this
        // throws. Deferred, the exception must come back and stop the script exactly as inline.
        llRequestPermissions(P, PERMISSION_TRIGGER_ANIMATION);
        say(""llRequestPermissions|done"");
    }
}";


    private const string Script2 = @"
string P = ""5a5a5a5a-0000-4000-8000-00000000b002"";
say(string s) { llSay(0, s); }
string isKey(string k) { if ((key)k) return ""key""; return ""no-key""; }
default
{
    state_entry()
    {
        say(""osDetectedCountry|"" + osDetectedCountry(0));
        say(""llCreateKeyValueSL|"" + (string)llCreateKeyValueSL(""k2"", ""v""));
        say(""llReadKeyValueSL|"" + (string)llReadKeyValueSL(""k2""));
        say(""llUpdateKeyValueSL|"" + (string)llUpdateKeyValueSL(""k2"", ""v3"", ""v""));
        say(""llUpdateKeyValue.3|"" + isKey((string)llUpdateKeyValue(""k2"", ""v4"", ""v3"")));
        say(""llClearKeyValue|"" + (string)llClearKeyValue());
        say(""osNpcCreate|"" + isKey((string)osNpcCreate(""Npc"", ""One"", <128, 128, 25>, """")));
        say(""osNpcCreate.5|"" + isKey((string)osNpcCreate(""Npc"", ""Two"", <128, 128, 25>, """", 0)));
        osTeleportOwner(""Nowhere"", <128, 128, 25>, <1, 0, 0>);       say(""osTeleportOwner.3|done"");
        osTeleportOwner(1000, 1000, <128, 128, 25>, <1, 0, 0>);      say(""osTeleportOwner.4|done"");
        osTeleportOwner(<128, 128, 25>, <1, 0, 0>);                  say(""osTeleportOwner.2|done"");
        osForceAttachToOtherAvatarFromInventory(P, ""none"", 1);      say(""osForceAttachToOtherAvatarFromInventory|done"");
        osSetTerrainTexture(0, NULL_KEY);                             say(""osSetTerrainTexture|done"");
        osSetTerrainTextures([NULL_KEY, NULL_KEY, NULL_KEY, NULL_KEY], 0); say(""osSetTerrainTextures|done"");
        llRequestExperiencePermissions(P, """");                      say(""llRequestExperiencePermissions|done"");
        llTargetedEmail(0, ""nobody@example.invalid"", ""subject"", ""body""); say(""llTargetedEmail.4|done"");
        llTargetedEmail(2, ""subject"", ""body"");                    say(""llTargetedEmail.3|done"");
        say(""END2"");
    }
}";

    private string Transcript(string deferral)
    {
        using var h = new SchedulerHarness(cfg =>
        {
            cfg.Configs["InWorldz.Phlox"].Set("ServiceCallDeferral", deferral);
            cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "Severe");
        });
        UserAccountHelpers.CreateUserWithInventory(h.Scene, "Golden", "Avatar", Account, "pw");
        SceneHelpers.AddScenePresence(h.Scene, Present);
        TaskInventoryHelpers.AddNotecard(h.Scene.AssetService, h.Prim, "nc", UUID.Random(), UUID.Random(), "line one\nline two\nline three");
        var chat = new List<(string From, int Channel, string Msg)>();
        h.Scene.EventManager.OnChatFromWorld += (sender, c) => { lock (chat) chat.Add((c.From ?? "", c.Channel, c.Message ?? "")); };
        var item = h.RezScript(Script);
        var prim2 = SceneHelpers.AddSceneObject(h.Scene, "Golden prim two", h.Prim.OwnerID).RootPart;
        var item2 = h.RezScriptInto(prim2, Script2);

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(90) && !(h.Said.Contains("END2") && h.Said.Any(m => m.StartsWith("Script error: Script ") || m == "llRequestPermissions|done")))
            h.PumpFor(TimeSpan.FromMilliseconds(100));
        h.PumpFor(TimeSpan.FromSeconds(1));
        if (!h.Said.Contains("END")) _out.WriteLine($"[{deferral}] did not reach END: " + h.Diagnose(item) + " status=" + h.StatusOf(item));
        if (!h.Said.Contains("END2")) _out.WriteLine($"[{deferral}] prim two did not reach END2: status=" + h.StatusOf(item2));
        // Each prim's chat in order (the two scripts run side by side, so only their interleaving may
        // differ); the script's asset id is random per run; everything else must match byte for byte.
        List<(string From, int Channel, string Msg)> all;
        lock (chat) all = chat.ToList();
        string Of(string from) => string.Join("\n", all.Where(c => c.From == from).Select(c => c.Channel + "|" + c.Msg));
        return System.Text.RegularExpressions.Regex.Replace(
            "== " + h.Prim.Name + "\n" + Of(h.Prim.Name) + "\n== Golden prim two\n" + Of("Golden prim two"),
            "Script [0-9a-f-]{36} stopped", "Script <id> stopped");
    }

    [Fact]
    public void EveryConvertedCallAnswersIdenticallyInlineAndDeferred()
    {
        string inline = Transcript("never");
        string deferred = Transcript("always");
        _out.WriteLine("---- inline (never) ----\n" + inline);
        if (deferred != inline) _out.WriteLine("---- deferred (always) ----\n" + deferred);
        Assert.Contains("0|END", inline);
        Assert.Contains("0|osSetTerrainTextureHeight|done", inline);
        Assert.Contains("0|dataserver|", inline);
        Assert.Contains("0|END2", inline);
        Assert.Equal(inline, deferred);
    }
}
