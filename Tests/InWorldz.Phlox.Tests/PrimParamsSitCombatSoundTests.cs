using System.Globalization;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;
using LSL_List = OpenSim.Region.ScriptEngine.Shared.LSL_Types.list;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// PRIM_ALLOW_UNSIT, PRIM_SCRIPTED_SIT_ONLY, PRIM_SIT_FLAGS, PRIM_DAMAGE, PRIM_HEALTH and PRIM_COLLISION_SOUND
/// set and read back against the scene state each one uses (SceneObjectPart.AllowUnsit, ScriptedSitOnly,
/// SitFlagsStored, SceneObjectGroup.Damage, CollisionSound / CollisionSoundVolume / CollisionSoundType), on the root and
/// on a child; their value counts in a long list; SL's error cases; the ll functions that share each helper
/// (llSetLinkSitFlags / llGetLinkSitFlags, llSetDamage, llCollisionSound); the values a YEngine script's API writes
/// read back by Phlox; the saved form; the region's sit path refusing a manual sit on a scripted-only prim; and
/// PRIM_OMEGA on a seated avatar shouting SL's "PRIM_OMEGA disallowed on agent" while the rest of the list applies.
/// </summary>
[Collection("phlox-yengine")]
public class PrimParamsSitCombatSoundTests
{
    private readonly ITestOutputHelper _out;
    public PrimParamsSitCombatSoundTests(ITestOutputHelper o) => _out = o;

    private const int DEBUG_CHANNEL = 0x7FFFFFFF;
    private const string NullKey = "00000000-0000-0000-0000-000000000000";
    private static readonly UUID BoomKey = new("aaaaaaaa-1111-2222-3333-444444444444");

    /// <summary>The harness prim as the root "p1" plus a child "p2" at link 2.</summary>
    private static SceneObjectPart[] TwoPrimLinkset(SchedulerHarness h)
    {
        h.Prim.Name = "p1";
        h.Prim.ParentGroup.LinkToGroup(SceneHelpers.AddSceneObject(h.Scene, "p2", h.Prim.OwnerID));
        var group = h.Prim.ParentGroup;
        Assert.Equal(2, group.PrimCount);
        var parts = new[] { group.GetLinkNumPart(1), group.GetLinkNumPart(2) };
        Assert.Same(h.Prim, parts[0]);
        parts[1].Name = "p2";
        return parts;
    }

    /// <summary>An avatar asking the region to sit on <paramref name="seat"/>, as a viewer's sit click does.</summary>
    private static ScenePresence RequestSit(SchedulerHarness h, SceneObjectPart seat, string first = "Ann")
    {
        var acd = SceneHelpers.GenerateAgentData(UUID.Random());
        acd.firstname = first;
        acd.lastname = "Sitter";
        var sp = SceneHelpers.AddScenePresence(h.Scene, acd);
        sp.AbsolutePosition = seat.AbsolutePosition + new Vector3(1, 1, 0);
        sp.HandleAgentRequestSit(sp.ControllingClient, sp.UUID, seat.UUID, Vector3.Zero);
        return sp;
    }

    private static void WaitFor(SchedulerHarness h, Func<IReadOnlyList<string>, bool> done, double seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && !done(h.Said)) h.PumpOnce();
    }

    private void Run(SchedulerHarness h, string body) => Run(h, h.Prim, body);

    private void Run(SchedulerHarness h, SceneObjectPart into, string body)
    {
        h.RezScriptInto(into, "default { state_entry() { " + body + " llSay(0, \"done\"); } }");
        WaitFor(h, said => said.Contains("done"));
        Assert.True(h.Said.Contains("done"), "the script never finished: " + string.Join(" | ", h.Said));
        h.PumpFor(TimeSpan.FromMilliseconds(300));
        _out.WriteLine(string.Join("\n", h.SaidOn.Select(s => $"[{s.Channel}] {s.Message}")));
    }

    /// <summary>llSay(0, "<tag>=" + the rules' read on <paramref name="link"/>, "|"-joined).</summary>
    private static string Say(string tag, string link, string rules)
        => "llSay(0, \"" + tag + "=\" + llDumpList2String(llGetLinkPrimitiveParams(" + link + ", [" + rules + "]), \"|\")); ";

    private static string Say(string tag, string rules)
        => "llSay(0, \"" + tag + "=\" + llDumpList2String(llGetPrimitiveParams([" + rules + "]), \"|\")); ";

    private static string Line(SchedulerHarness h, string prefix) => h.Said.Single(s => s.StartsWith(prefix)).Substring(prefix.Length);

    private static string[] Fields(SchedulerHarness h, string prefix, int count)
    {
        var line = Line(h, prefix);
        var f = line.Length == 0 ? Array.Empty<string>() : line.Split('|');
        Assert.True(f.Length == count, $"{prefix} expected {count} values, got {f.Length}: {line}");
        return f;
    }

    private static IReadOnlyList<string> Errors(SchedulerHarness h)
        => h.SaidOn.Where(s => s.Channel == DEBUG_CHANNEL).Select(s => s.Message).ToList();

    private static float Num(string lsl) => float.Parse(lsl.Trim(), CultureInfo.InvariantCulture);

    private static void Near(float expected, string lsl, float tolerance = 0.001f)
        => Assert.True(Math.Abs(expected - Num(lsl)) < tolerance, $"expected {expected}, got {lsl}");

    private static void Near(float expected, float actual, float tolerance = 0.001f)
        => Assert.True(Math.Abs(expected - actual) < tolerance, $"expected {expected}, got {actual}");

    /// <summary>An inventory item in the prim, as a builder drops one in.</summary>
    private static UUID AddItem(SceneObjectPart part, string name, AssetType type, InventoryType invType, UUID asset = default)
    {
        if (asset == default) asset = UUID.Random();
        const uint full = (uint)(OpenSim.Framework.PermissionMask.Copy | OpenSim.Framework.PermissionMask.Modify | OpenSim.Framework.PermissionMask.Transfer);
        part.Inventory.AddInventoryItem(new TaskInventoryItem
        {
            Name = name, AssetID = asset, ItemID = UUID.Random(),
            Type = (int)type, InvType = (int)invType,
            BasePermissions = full, CurrentPermissions = full, NextPermissions = full, OwnerID = part.OwnerID,
        }, true);
        return asset;
    }

    private static UUID AddSound(SceneObjectPart part, string name, UUID asset = default)
        => AddItem(part, name, AssetType.Sound, InventoryType.Sound, asset);

    // ── PRIM_ALLOW_UNSIT and PRIM_SCRIPTED_SIT_ONLY ──────────────────────────

    [Fact]
    public void AllowUnsitAndScriptedSitOnlySetAndReadBackOnTheRootAndAChild()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);

        Run(h,
            Say("default", "PRIM_ALLOW_UNSIT, PRIM_SCRIPTED_SIT_ONLY") +
            "llSetPrimitiveParams([PRIM_ALLOW_UNSIT, FALSE, PRIM_SCRIPTED_SIT_ONLY, TRUE]); " +
            Say("root", "PRIM_ALLOW_UNSIT, PRIM_SCRIPTED_SIT_ONLY") +
            "llSetLinkPrimitiveParamsFast(2, [PRIM_ALLOW_UNSIT, 0, PRIM_SCRIPTED_SIT_ONLY, 7]); " +
            Say("child", "2", "PRIM_ALLOW_UNSIT, PRIM_SCRIPTED_SIT_ONLY") +
            "llSetPrimitiveParams([PRIM_ALLOW_UNSIT, 5, PRIM_SCRIPTED_SIT_ONLY, 0]); " +
            Say("back", "PRIM_ALLOW_UNSIT, PRIM_SCRIPTED_SIT_ONLY"));

        // SL's defaults: standing allowed, manual sits allowed.
        Assert.Equal(new[] { "1", "0" }, Fields(h, "default=", 2));
        Assert.Equal(new[] { "0", "1" }, Fields(h, "root=", 2));
        // Any non-zero integer is TRUE.
        Assert.Equal(new[] { "0", "1" }, Fields(h, "child=", 2));
        Assert.Equal(new[] { "1", "0" }, Fields(h, "back=", 2));
        Assert.True(parts[0].AllowUnsit);
        Assert.False(parts[0].ScriptedSitOnly);
        Assert.False(parts[1].AllowUnsit);
        Assert.True(parts[1].ScriptedSitOnly);
        Assert.Empty(Errors(h));
    }

    [Fact]
    public void AllowUnsitAndScriptedSitOnlyWithAWrongTypeChangeNothingAndTheWalkGoesOn()
    {
        using var h = new SchedulerHarness();
        Run(h,
            "llSetPrimitiveParams([PRIM_ALLOW_UNSIT, \"no\", PRIM_SCRIPTED_SIT_ONLY, 1.0, PRIM_NAME, \"after\"]); " +
            Say("got", "PRIM_ALLOW_UNSIT, PRIM_SCRIPTED_SIT_ONLY, PRIM_NAME"));

        Assert.Equal(new[] { "1", "0", "after" }, Fields(h, "got=", 3));
        Assert.True(h.Prim.AllowUnsit);
        Assert.False(h.Prim.ScriptedSitOnly);
    }

    [Fact]
    public void AScriptedSitOnlyPrimRefusesAManualSitUntilItIsCleared()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);

        Run(h, "llSetLinkPrimitiveParamsFast(2, [PRIM_SCRIPTED_SIT_ONLY, TRUE]);");
        var ann = RequestSit(h, parts[1]);
        // SL: "Agents may only be seated on this prim using llSitOnLink. Attempts to do a manual sit will fail."
        Assert.Equal(0u, ann.ParentID);

        h.ClearSaid(UUID.Zero);
        Run(h, "llSetLinkPrimitiveParamsFast(2, [PRIM_SCRIPTED_SIT_ONLY, FALSE]);");
        var bob = RequestSit(h, parts[1], "Bob");
        Assert.NotEqual(0u, bob.ParentID);
    }

    [Fact]
    public void AManuallySeatedAvatarStandsFromAPrimThatDisallowsUnsit()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        var ann = RequestSit(h, parts[1]);
        Assert.NotEqual(0u, ann.ParentID);

        Run(h, "llSetLinkPrimitiveParamsFast(2, [PRIM_ALLOW_UNSIT, FALSE]);");
        Assert.False(parts[1].AllowUnsit);

        // SL: "This flag has no effect on agents who had seated manually (i.e. not via llSitOnLink using experience
        // permissions)."
        ann.HandleAgentUpdate(ann.ControllingClient, new AgentUpdateArgs
        {
            ControlFlags = (uint)AgentManager.ControlFlags.AGENT_CONTROL_STAND_UP,
            BodyRotation = Quaternion.Identity,
            HeadRotation = Quaternion.Identity,
        });
        Assert.Equal(0u, ann.ParentID);
    }

    // ── PRIM_SIT_FLAGS ───────────────────────────────────────────────────────

    [Fact]
    public void SitFlagsSetAndReadBackTheSameWordAsTheLinkSitFlagFunctions()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);

        Run(h,
            Say("default", "PRIM_SIT_FLAGS") +
            "llSetPrimitiveParams([PRIM_SIT_FLAGS, SIT_FLAG_SIT_TARGET | SIT_FLAG_ALLOW_UNSIT | SIT_FLAG_SCRIPTED_ONLY | SIT_FLAG_NO_COLLIDE | SIT_FLAG_NO_DAMAGE]); " +
            Say("all", "PRIM_SIT_FLAGS") +
            "llSay(0, \"fn=\" + (string)llGetLinkSitFlags(LINK_THIS)); " +
            "llSitTarget(<0, 0, 0.5>, ZERO_ROTATION); " +
            Say("target", "PRIM_SIT_FLAGS") +
            "llSetLinkSitFlags(2, SIT_FLAG_NO_COLLIDE); " +
            Say("child", "2", "PRIM_SIT_FLAGS, PRIM_ALLOW_UNSIT") +
            "llSetLinkPrimitiveParamsFast(2, [PRIM_SIT_FLAGS, SIT_FLAG_SCRIPTED_ONLY]); " +
            "llSay(0, \"childfn=\" + (string)llGetLinkSitFlags(2)); ");

        // A new prim: only ALLOW_UNSIT (standing is allowed).
        Assert.Equal(new[] { "2" }, Fields(h, "default=", 1));
        // SIT_TARGET is read-only: set it and nothing happens; 2|4|16|32.
        Assert.Equal(new[] { "54" }, Fields(h, "all=", 1));
        Assert.Equal("54", Line(h, "fn="));
        // A sit target reports SIT_TARGET.
        Assert.Equal(new[] { "55" }, Fields(h, "target=", 1));
        // llSetLinkSitFlags without ALLOW_UNSIT clears it, and PRIM_ALLOW_UNSIT reads the same.
        Assert.Equal(new[] { "16", "0" }, Fields(h, "child=", 2));
        Assert.Equal("4", Line(h, "childfn="));
        Assert.True(parts[1].ScriptedSitOnly);
        Assert.False(parts[1].AllowUnsit);
        Assert.Equal(0, parts[1].SitFlagsStored);
        Assert.Equal(16 | 32, parts[0].SitFlagsStored);
        Assert.Empty(Errors(h));
    }

    // ── PRIM_DAMAGE ──────────────────────────────────────────────────────────

    [Fact]
    public void DamageSetsTheObjectsDamageHeldToSlsRangeAndReadsItBack()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        Assert.Equal(-1f, parts[0].ParentGroup.Damage); // the scene's "none"

        Run(h,
            Say("default", "PRIM_DAMAGE") +
            "llSetPrimitiveParams([PRIM_DAMAGE, 25.5, DAMAGE_TYPE_FIRE]); " +
            Say("set", "PRIM_DAMAGE") +
            "llSetPrimitiveParams([PRIM_DAMAGE, 150.0, DAMAGE_TYPE_GENERIC]); " +
            Say("high", "PRIM_DAMAGE") +
            "llSetPrimitiveParams([PRIM_DAMAGE, -20.0, DAMAGE_TYPE_GENERIC]); " +
            Say("low", "PRIM_DAMAGE") +
            "llSetLinkPrimitiveParamsFast(2, [PRIM_DAMAGE, 12, DAMAGE_TYPE_ACID]); " +
            Say("child", "2", "PRIM_DAMAGE") +
            Say("rootafter", "PRIM_DAMAGE") +
            "llSetDamage(40.0); " +
            Say("ll", "PRIM_DAMAGE"));

        var d = Fields(h, "default=", 2);
        Near(0f, d[0]);              // SL's 0.0, not the scene's -1
        Assert.Equal("0", d[1]);     // DAMAGE_TYPE_GENERIC
        var s = Fields(h, "set=", 2);
        Near(25.5f, s[0]);
        Assert.Equal("0", s[1]);     // the scene keeps no damage type (known limit)
        Near(100f, Fields(h, "high=", 2)[0]);   // SL: "limited to 100 maximum"
        Near(0f, Fields(h, "low=", 2)[0]);
        // One damage per object, whichever link sets or reads it (an integer amount is accepted as a float).
        Near(12f, Fields(h, "child=", 2)[0]);
        Near(12f, Fields(h, "rootafter=", 2)[0]);
        Near(40f, Fields(h, "ll=", 2)[0]);
        Near(40f, parts[0].ParentGroup.Damage);
        Assert.Empty(Errors(h));
    }

    [Fact]
    public void DamageWithAWrongTypeChangesNothingAndTheWalkGoesOn()
    {
        using var h = new SchedulerHarness();
        Run(h,
            "llSetPrimitiveParams([PRIM_DAMAGE, \"lots\", DAMAGE_TYPE_FIRE, PRIM_DAMAGE, 10.0, 1.5, PRIM_NAME, \"after\"]); " +
            Say("got", "PRIM_DAMAGE, PRIM_NAME"));

        var f = Fields(h, "got=", 3);
        Near(0f, f[0]);
        Assert.Equal("after", f[2]);
        Assert.Equal(-1f, h.Prim.ParentGroup.Damage);
    }

    // ── PRIM_HEALTH ──────────────────────────────────────────────────────────

    [Fact]
    public void HealthIsAcceptedAndReadsSlsDefault()
    {
        using var h = new SchedulerHarness();
        Run(h,
            "llSetPrimitiveParams([PRIM_HEALTH, 75.0, PRIM_NAME, \"after\", PRIM_HEALTH, 3]); " +
            Say("got", "PRIM_HEALTH, PRIM_NAME"));

        // The scene keeps no health for a prim (known limit): SL's "Objects start with 0 health by default".
        var f = Fields(h, "got=", 2);
        Near(0f, f[0]);
        Assert.Equal("after", f[1]);
        Assert.Empty(Errors(h));
    }

    // ── PRIM_COLLISION_SOUND and llCollisionSound ────────────────────────────

    [Fact]
    public void CollisionSoundSetsByNameOrKeyAndReadsTheKeyAndVolume()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        UUID boom = AddSound(parts[0], "boom");
        Assert.Equal(0, (int)parts[0].CollisionSoundType); // the scene's default sounds

        Run(h,
            Say("default", "PRIM_COLLISION_SOUND") +
            "llSetPrimitiveParams([PRIM_COLLISION_SOUND, \"boom\", 0.5]); " +
            Say("name", "PRIM_COLLISION_SOUND") +
            "llSetLinkPrimitiveParamsFast(2, [PRIM_COLLISION_SOUND, \"" + BoomKey + "\", 1.5]); " +
            Say("key", "2", "PRIM_COLLISION_SOUND") +
            "llSetPrimitiveParams([PRIM_COLLISION_SOUND, \"\", 0.25]); " +
            Say("empty", "PRIM_COLLISION_SOUND"));

        var d = Fields(h, "default=", 2);
        Assert.Equal(NullKey, d[0]);
        Near(0f, d[1]);
        var n = Fields(h, "name=", 2);
        Assert.Equal(boom.ToString(), n[0]);
        Near(0.5f, n[1]);
        var k = Fields(h, "key=", 2);
        Assert.Equal(BoomKey.ToString(), k[0]);
        Near(1f, k[1]); // SL: 0.0 <= impact_volume <= 1.0
        Assert.Equal(BoomKey, parts[1].CollisionSound);
        Assert.Equal(1, (int)parts[1].CollisionSoundType);
        // SL: "If impact_sound is an empty string then the collision sound is suppressed."
        var e = Fields(h, "empty=", 2);
        Assert.Equal(NullKey, e[0]);
        Near(0.25f, e[1]);
        Assert.Equal(-1, (int)parts[0].CollisionSoundType);
        Assert.Equal(parts[0].invalidCollisionSoundUUID, parts[0].CollisionSound);
        Assert.Empty(Errors(h));
    }

    [Fact]
    public void CollisionSoundWithAMissingNameOrANonSoundShoutsAndKeepsTheSound()
    {
        using var h = new SchedulerHarness();
        UUID boom = AddSound(h.Prim, "boom");
        AddItem(h.Prim, "tex", AssetType.Texture, InventoryType.Texture);

        Run(h,
            "llSetPrimitiveParams([PRIM_COLLISION_SOUND, \"boom\", 0.5]); " +
            "llSetPrimitiveParams([PRIM_COLLISION_SOUND, \"nope\", 0.9, PRIM_COLLISION_SOUND, \"tex\", 0.8, PRIM_NAME, \"after\"]); " +
            "llCollisionSound(\"nope\", 0.7); " +
            Say("got", "PRIM_COLLISION_SOUND, PRIM_NAME"));

        var f = Fields(h, "got=", 3);
        Assert.Equal(boom.ToString(), f[0]);
        Near(0.5f, f[1]);
        Assert.Equal("after", f[2]);
        // SL: "an error is shouted on DEBUG_CHANNEL".
        var errors = Errors(h);
        Assert.Equal(2, errors.Count(m => m.Contains("Could not find sound 'nope'")));
        Assert.Single(errors, m => m.Contains("Could not find sound 'tex'"));
    }

    [Fact]
    public void LlCollisionSoundAndPrimCollisionSoundShareOneHelper()
    {
        using var h = new SchedulerHarness();
        UUID boom = AddSound(h.Prim, "boom");

        Run(h,
            "llCollisionSound(\"boom\", 0.4); " +
            Say("ll", "PRIM_COLLISION_SOUND") +
            "llCollisionSound(\"\", 0.0); " +
            Say("off", "PRIM_COLLISION_SOUND") +
            "llCollisionSound(\"" + BoomKey + "\", -3.0); " +
            Say("key", "PRIM_COLLISION_SOUND"));

        var l = Fields(h, "ll=", 2);
        Assert.Equal(boom.ToString(), l[0]);
        Near(0.4f, l[1]);
        // The empty string suppresses the sound (before, llCollisionSound restored the default sounds).
        Assert.Equal(NullKey, Fields(h, "off=", 2)[0]);
        var k = Fields(h, "key=", 2);
        Assert.Equal(BoomKey.ToString(), k[0]);
        Near(0f, k[1]);
        Assert.Equal(1, (int)h.Prim.CollisionSoundType);
    }

    // ── value counts in a long list ──────────────────────────────────────────

    [Fact]
    public void ALongListMixingTheSixRulesKeepsItsPlaceBothWays()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);

        Run(h,
            "llSetLinkPrimitiveParamsFast(LINK_THIS, [" +
            "PRIM_NAME, \"one\", PRIM_ALLOW_UNSIT, FALSE, PRIM_TEXT, \"hi\", <1, 0, 0>, 1.0, PRIM_SCRIPTED_SIT_ONLY, TRUE, " +
            "PRIM_DAMAGE, 5.0, DAMAGE_TYPE_COLD, PRIM_HEALTH, 9.0, PRIM_DESC, \"mid\", " +
            "PRIM_LINK_TARGET, 2, PRIM_SIT_FLAGS, SIT_FLAG_NO_DAMAGE, PRIM_COLLISION_SOUND, \"" + BoomKey + "\", 0.3, " +
            "PRIM_NAME, \"two\"]); " +
            Say("got", "LINK_THIS",
                "PRIM_NAME, PRIM_ALLOW_UNSIT, PRIM_SCRIPTED_SIT_ONLY, PRIM_DAMAGE, PRIM_HEALTH, PRIM_SIT_FLAGS, " +
                "PRIM_COLLISION_SOUND, PRIM_DESC, PRIM_LINK_TARGET, 2, PRIM_NAME, PRIM_SIT_FLAGS, PRIM_COLLISION_SOUND, PRIM_ALLOW_UNSIT"));

        var f = Fields(h, "got=", 1 + 1 + 1 + 2 + 1 + 1 + 2 + 1 + 1 + 1 + 2 + 1);
        Assert.Equal("one", f[0]);
        Assert.Equal("0", f[1]);
        Assert.Equal("1", f[2]);
        Near(5f, f[3]);
        Assert.Equal("0", f[4]);
        Near(0f, f[5]);
        Assert.Equal("4", f[6]);          // SCRIPTED_ONLY, ALLOW_UNSIT cleared
        Assert.Equal(NullKey, f[7]);
        Near(0f, f[8]);
        Assert.Equal("mid", f[9]);
        Assert.Equal("two", f[10]);
        Assert.Equal("32", f[11]);        // NO_DAMAGE; ALLOW_UNSIT cleared, as llSetLinkSitFlags
        Assert.Equal(BoomKey.ToString(), f[12]);
        Near(0.3f, f[13]);
        Assert.Equal("0", f[14]);
        Assert.Equal("hi", parts[0].Text);
        Assert.Empty(Errors(h));
    }

    // ── one state for both engines ───────────────────────────────────────────

    [Fact]
    public void WhatAYEngineScriptSetsAPhloxScriptReadsAndTheSceneKeeps()
    {
        using var h = new SchedulerHarness(withYEngine: true);
        UUID boom = AddSound(h.Prim, "boom");
        var item = TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, UUID.Random(), UUID.Random(), "yengine-api", "default { }");
        var yengine = new LSL_Api();
        yengine.Initialize(h.YEngine, h.Prim, item);

        // YEngine: PRIM_ALLOW_UNSIT 0 and PRIM_SCRIPTED_SIT_ONLY 1 (LSL_Api.SetPrimParams), llSetDamage, llCollisionSound.
        yengine.llSetPrimitiveParams(new LSL_List(39, 0, 40, 1));
        yengine.llSetDamage(30.0);
        yengine.llCollisionSound("boom", 0.4);

        Run(h, Say("got", "PRIM_ALLOW_UNSIT, PRIM_SCRIPTED_SIT_ONLY, PRIM_SIT_FLAGS, PRIM_DAMAGE, PRIM_COLLISION_SOUND"));
        var f = Fields(h, "got=", 1 + 1 + 1 + 2 + 2);
        Assert.Equal(new[] { "0", "1", "4" }, f.Take(3));
        Near(30f, f[3]);
        Assert.Equal(boom.ToString(), f[5]);
        Near(0.4f, f[6]);

        // Phlox writes back; the scene holds what YEngine's own sit and collision code reads.
        h.ClearSaid(UUID.Zero);
        Run(h, "llSetPrimitiveParams([PRIM_ALLOW_UNSIT, TRUE, PRIM_SCRIPTED_SIT_ONLY, FALSE, PRIM_COLLISION_SOUND, \"\", 0.0]);");
        Assert.True(h.Prim.AllowUnsit);
        Assert.False(h.Prim.ScriptedSitOnly);
        Assert.Equal(-1, (int)h.Prim.CollisionSoundType);
    }

    [Fact]
    public void TheSitRulesAndTheCollisionSoundSurviveTheSavedForm()
    {
        using var h = new SchedulerHarness();
        UUID boom = AddSound(h.Prim, "boom");
        Run(h, "llSetPrimitiveParams([PRIM_ALLOW_UNSIT, FALSE, PRIM_SCRIPTED_SIT_ONLY, TRUE, PRIM_COLLISION_SOUND, \"boom\", 0.6]);");

        string xml = SceneObjectSerializer.ToOriginalXmlFormat(h.Prim.ParentGroup);
        var copy = SceneObjectSerializer.FromOriginalXmlFormat(xml).RootPart;

        Assert.False(copy.AllowUnsit);
        Assert.True(copy.ScriptedSitOnly);
        Assert.Equal(boom, copy.CollisionSound);
        Near(0.6f, copy.CollisionSoundVolume);
    }

    // ── PRIM_OMEGA on a seated avatar ────────────────────────────────────────

    [Fact]
    public void PrimOmegaOnASeatedAvatarShoutsSlsErrorAndTheRestOfTheListApplies()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        var ann = RequestSit(h, parts[1]);
        Assert.NotEqual(0u, ann.ParentID);

        Run(h,
            "llSetLinkPrimitiveParamsFast(3, [PRIM_OMEGA, <0, 0, 1>, 1.0, 1.0, PRIM_POS_LOCAL, <0.5, 0, 1>]); " +
            Say("pos", "3", "PRIM_POS_LOCAL") +
            "llSetLinkPrimitiveParamsFast(2, [PRIM_OMEGA, <0, 0, 1>, 1.0, 1.0]); ");

        Assert.Single(Errors(h), m => m.Contains("PRIM_OMEGA disallowed on agent"));
        var pos = Line(h, "pos=").Trim('<', '>').Split(',').Select(s => float.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
        Near(0.5f, pos[0]);
        Near(1f, pos[2]);
        // A prim link spins with no error.
        Assert.NotEqual(Vector3.Zero, parts[1].AngularVelocity);
    }

    [Fact]
    public void PrimOmegaOnTheWholeLinksetSpinsThePrimsAndShoutsOncePerSeatedAvatar()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        var ann = RequestSit(h, parts[1]);
        var bob = RequestSit(h, parts[0], "Bob");
        Assert.NotEqual(0u, ann.ParentID);
        Assert.NotEqual(0u, bob.ParentID);

        Run(h, "llSetLinkPrimitiveParamsFast(LINK_SET, [PRIM_OMEGA, <0, 0, 1>, 1.0, 1.0, PRIM_NAME, \"named\"]);");

        Assert.Equal(2, Errors(h).Count(m => m.Contains("PRIM_OMEGA disallowed on agent")));
        Assert.All(parts, p => Assert.Equal("named", p.Name));
    }
}
