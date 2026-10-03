using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using InWorldz.Phlox.Compiler;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Land;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// SL's own constant table (secondlife/lsl-definitions @ 10741b9, dumped into
/// Fixtures/sl-constants-10741b9.txt) is the reference: the compiler's table must carry every public
/// SL constant with SL's value, the implementation must not keep private copies that disagree, and
/// each family that did disagree is pinned by one behaviour test using the fixture's value.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class SlConstantsTests
{
    private readonly ITestOutputHelper _out;
    public SlConstantsTests(ITestOutputHelper o) => _out = o;

    internal sealed record SlConstant(string Name, string Type, string Value, bool Private);

    private static string ProjectDir => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(typeof(SlConstantsTests).Assembly.Location)!, "..", "..", ".."));

    internal static readonly System.Lazy<Dictionary<string, SlConstant>> Fixture = new(() =>
        File.ReadAllLines(Path.Combine(ProjectDir, "Fixtures", "sl-constants-10741b9.txt"))
            .Where(l => l.Length > 0 && !l.StartsWith("#"))
            .Select(l => l.Split('\t'))
            .ToDictionary(p => p[0], p => new SlConstant(p[0], p[1], p[2], p.Length > 3 && p[3] == "private")));

    private static int Sl(string name) => int.Parse(Fixture.Value[name].Value, CultureInfo.InvariantCulture);

    /// <summary>Names where the table deliberately differs from SL, each with its reason.</summary>
    private static readonly Dictionary<string, string> AllowList = new()
    {
        ["ESTATE_ACCESS_ALLOWED_AGENT_ADD"] = "Phlox numbers 0-5; renumbering waits on the bytecode-cache decision",
        ["ESTATE_ACCESS_ALLOWED_AGENT_REMOVE"] = "Phlox numbers 0-5; renumbering waits on the bytecode-cache decision",
        ["ESTATE_ACCESS_ALLOWED_GROUP_ADD"] = "Phlox numbers 0-5; renumbering waits on the bytecode-cache decision",
        ["ESTATE_ACCESS_ALLOWED_GROUP_REMOVE"] = "Phlox numbers 0-5; renumbering waits on the bytecode-cache decision",
        ["ESTATE_ACCESS_BANNED_AGENT_ADD"] = "Phlox numbers 0-5; renumbering waits on the bytecode-cache decision",
        ["ESTATE_ACCESS_BANNED_AGENT_REMOVE"] = "Phlox numbers 0-5; renumbering waits on the bytecode-cache decision",
        ["EOF"] = "the table spells the LSL escape (\\n\\n\\n); the value a script sees is SL's - StringConstantsHaveSlValuesAtRuntime",
        ["NAK"] = "the table spells the LSL escape (\\n\\u0015\\n); the value a script sees is SL's - StringConstantsHaveSlValuesAtRuntime",
    };

    private static bool Same(SlConstant sl, ConstantSymbol mine)
    {
        var v = mine.ConstValue;
        switch (sl.Type)
        {
            case "integer":
                return SlConstTests.TryParseInt(v, out var i) && i == int.Parse(sl.Value, CultureInfo.InvariantCulture);
            case "float":
                return float.TryParse(v.TrimEnd('f', 'F'), NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
                       && Math.Abs(f - float.Parse(sl.Value, CultureInfo.InvariantCulture)) <= 1e-6 * Math.Max(1, Math.Abs(f));
            case "vector":
            case "rotation":
                static float[] Nums(string s) => s.Trim('<', '>').Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                return Nums(v).SequenceEqual(Nums(sl.Value));
            default:
                return v == Regex.Unescape(sl.Value);
        }
    }

    [Fact]
    public void SlConstantsMatchLL()
    {
        var bad = new List<string>();
        var allowed = new List<string>();
        foreach (var sl in Fixture.Value.Values.OrderBy(c => c.Name))
        {
            if (sl.Private) { allowed.Add($"{sl.Name}: private in SL"); continue; }
            if (AllowList.TryGetValue(sl.Name, out var why)) { allowed.Add($"{sl.Name}: {why}"); continue; }
            if (!DefaultConstants.Constants.TryGetValue(sl.Name, out var mine)) { bad.Add($"{sl.Name}: missing (SL {sl.Type} {sl.Value})"); continue; }
            if (!Same(sl, mine)) bad.Add($"{sl.Name}: Phlox '{mine.ConstValue}' vs SL '{sl.Value}'");
        }
        _out.WriteLine("allow-list:\n  " + string.Join("\n  ", allowed));
        Assert.True(bad.Count == 0, bad.Count + " constant(s) differ from SL:\n" + string.Join("\n", bad));
    }

    // ---- JSON_*, EOF, NAK: what a script actually holds ------------------------------------------

    [Fact]
    public void StringConstantsHaveSlValuesAtRuntime()
    {
        string Probe(string n) => $"llSay(0, \"{n}=\" + (string)llStringLength({n}) + \":\" + (string)llOrd({n}, 0) + \":\" + (string)llOrd({n}, 1) + \":\" + (string)llOrd({n}, 2));";
        var names = new[] { "EOF", "NAK", "JSON_INVALID", "JSON_OBJECT", "JSON_ARRAY", "JSON_NUMBER", "JSON_STRING", "JSON_NULL", "JSON_TRUE", "JSON_FALSE", "JSON_DELETE" };
        using var h = new SchedulerHarness();
        h.RezScript("default { state_entry() { " + string.Concat(names.Select(Probe)) + " } }");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith(names[^1] + "=")));
        _out.WriteLine(string.Join(" | ", h.Said));
        foreach (var n in names)
        {
            var s = Regex.Unescape(Fixture.Value[n].Value);
            int Ord(int i) => i < s.Length ? s[i] : 0;
            Assert.Contains($"{n}={s.Length}:{Ord(0)}:{Ord(1)}:{Ord(2)}", h.Said);
        }
    }

    // ---- no private copy in LSLSystemAPI.cs disagrees with SL -----------------------------------

    [Fact]
    public void LocalConstantsScan()
    {
        var path = Path.GetFullPath(Path.Combine(ProjectDir, "..", "..", "Source", "Phlox.ScriptEngine", "LSLSystemAPI.cs"));
        var lines = File.ReadAllLines(path);
        var ints = Fixture.Value.Values.Where(c => c.Type == "integer").ToDictionary(c => c.Name, c => int.Parse(c.Value, CultureInfo.InvariantCulture));
        var bad = new List<string>();
        var decl = new Regex(@"\bconst int\s+(.*?);");
        var literal = new Regex(@"(?<![\w.])(-?0x[0-9A-Fa-f]+|-?\d+)(?![\w.])");
        var slName = new Regex(@"\b[A-Z][A-Z0-9]*_[A-Z0-9_]+\b");
        for (int n = 0; n < lines.Length; n++)
        {
            var line = lines[n];
            var m = decl.Match(line);
            if (m.Success)
            {
                foreach (var part in m.Groups[1].Value.Split(','))
                {
                    var kv = part.Split('=', 2);
                    if (kv.Length != 2) continue;
                    var name = kv[0].Trim();
                    if (ints.TryGetValue(name, out var sl) && (!SlConstTests.TryParseInt(kv[1].Trim(), out var mine) || mine != sl))
                        bad.Add($"LSLSystemAPI.cs:{n + 1} const {name} = {kv[1].Trim()} (SL {sl})");
                }
                continue;
            }
            int c = line.IndexOf("//", StringComparison.Ordinal);
            if (c < 0) continue;
            var code = Regex.Replace(line.Substring(0, c), "\"(?:[^\"\\\\]|\\\\.)*\"", "\"\"");
            var named = slName.Matches(line.Substring(c)).Select(x => x.Value).Where(ints.ContainsKey).Distinct().ToList();
            if (named.Count != 1) continue;
            var values = literal.Matches(code).Select(x => SlConstTests.TryParseInt(x.Value, out var v) ? v : (int?)null).Where(v => v.HasValue).ToList();
            if (values.Count > 0 && !values.Contains(ints[named[0]]))
                bad.Add($"LSLSystemAPI.cs:{n + 1} {code.Trim()} // {named[0]} (SL {ints[named[0]]})");
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    // ---- one behaviour test per family ------------------------------------------------------------

    private static string Said(SchedulerHarness h) => string.Join(" | ", h.Said);

    [Fact]
    public void AgentWalkingIsSetForAWalkingPresence()
    {
        using var h = new SchedulerHarness();
        var client = h.AddClient();
        var sp = h.Scene.GetScenePresence(client.AgentId);
        sp.Flying = false;
        // Walking is the movement animation (SL: AGENT_WALKING is "walking, running or crouch walking"; Halcyon
        // llGetAgentInfo reads the movement animation), not a held key.
        typeof(OpenSim.Region.Framework.Scenes.Animation.ScenePresenceAnimator)
            .GetProperty("CurrentMovementAnimation")!.SetValue(sp.Animator, "WALK");
        h.RezScript($"default {{ state_entry() {{ llSay(0, \"walk=\" + (string)((llGetAgentInfo(\"{sp.UUID}\") & {Sl("AGENT_WALKING")}) != 0)); }} }}");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("walk=")));
        Assert.True(h.Said.Contains("walk=1"), Said(h));
    }

    [Fact]
    public void AgentListRegionReturnsAPresenceOnAnotherParcel()
    {
        using var h = new SchedulerHarness();
        var near = new LandObject(UUID.Random(), false, h.Scene);
        var far = new LandObject(UUID.Random(), false, h.Scene);
        near.LandData.GlobalID = UUID.Random();
        far.LandData.GlobalID = UUID.Random();
        h.Scene.LandChannel = TwoParcels.Create(near, far);
        h.Prim.ParentGroup.AbsolutePosition = new Vector3(10, 10, 25);
        var client = h.AddClient();
        var sp = h.Scene.GetScenePresence(client.AgentId);
        sp.AbsolutePosition = new Vector3(200, 200, 25);
        h.RezScript($"default {{ state_entry() {{ llSay(0, \"list=\" + llList2CSV(llGetAgentList({Sl("AGENT_LIST_REGION")}, []))); }} }}");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("list=")));
        Assert.True(h.Said.Contains("list=" + sp.UUID), Said(h));
    }

    [Fact]
    public void StartGlowReachesPartStartGlow()
    {
        using var h = new SchedulerHarness();
        h.RezScript($"default {{ state_entry() {{ llParticleSystem([{Sl("PSYS_PART_START_GLOW")}, 0.5, {Sl("PSYS_PART_END_GLOW")}, 0.25]); llSay(0, \"set\"); }} }}");
        h.PumpUntil(() => h.Said.Contains("set"));
        Assert.Contains("set", h.Said);
        // libOMV's ParticleSystem(byte[], int) in this build does not decode the glow block back, so
        // the serialized system is read directly: the extended data carries a flags word
        // (DataGlow 0x01, DataBlend 0x02) 18 bytes from the end, and the glow block - start and end
        // glow as bytes of 255 - is the last two. No DataBlend means SL's default blend
        // (PSYS_PART_BF_SOURCE_ALPHA / PSYS_PART_BF_ONE_MINUS_SOURCE_ALPHA); Phlox's old default of
        // 0/1 wrote a blend block.
        var bytes = h.Prim.ParticleSystem;
        _out.WriteLine(BitConverter.ToString(bytes));
        int flags = bytes[bytes.Length - 18];
        Assert.True((flags & 0x01) != 0, "no glow block: " + BitConverter.ToString(bytes));
        Assert.True((flags & 0x02) == 0, "a blend block was written, so the default blend is not SL's");
        Assert.Equal((byte)(0.5f * 255), bytes[bytes.Length - 2]);
        Assert.Equal((byte)(0.25f * 255), bytes[bytes.Length - 1]);
    }

    [Fact]
    public void PrimRenderMaterialReachesTheRenderMaterialHandler()
    {
        using var h = new SchedulerHarness();
        var mat = UUID.Random();
        h.RezScript($"default {{ state_entry() {{ llSetPrimitiveParams([{Sl("PRIM_RENDER_MATERIAL")}, 0, \"{mat}\"]); llSay(0, \"got=\" + llList2String(llGetPrimitiveParams([{Sl("PRIM_RENDER_MATERIAL")}, 0]), 0)); }} }}");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("got=")));
        Assert.True(h.Said.Contains("got=" + mat), Said(h));
        Assert.Contains(h.Prim.Shape.RenderMaterials?.entries ?? Array.Empty<Primitive.RenderMaterials.RenderMaterialEntry>(), e => e.te_index == 0 && e.id == mat);
    }

    [Fact]
    public void StatusSandboxIsSetAndReadBack()
    {
        using var h = new SchedulerHarness();
        h.RezScript($"default {{ state_entry() {{ llSetStatus({Sl("STATUS_SANDBOX")}, TRUE); llSay(0, \"sandbox=\" + (string)llGetStatus({Sl("STATUS_SANDBOX")}) + \" shadows=\" + (string)llGetStatus({Sl("STATUS_CAST_SHADOWS")})); }} }}");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("sandbox=")));
        Assert.True(h.Said.Contains("sandbox=1 shadows=0"), Said(h));
        Assert.True(h.Prim.GetStatusSandbox());
    }

    [Fact]
    public void BreakLinkThisBreaksTheScriptsPrim()
    {
        using var h = new SchedulerHarness();
        var owner = (TestClient)SceneHelpers.AddScenePresence(h.Scene, h.Prim.OwnerID).ControllingClient;
        var group = SceneHelpers.AddSceneObject(h.Scene, 3, h.Prim.OwnerID, "lk", 0x21);
        var child = group.Parts.First(p => p != group.RootPart);
        var item = h.RezScriptInto(child, $"default {{ state_entry() {{ llRequestPermissions(llGetOwner(), PERMISSION_CHANGE_LINKS); }} " +
                                          $"run_time_permissions(integer p) {{ llBreakLink({Sl("LINK_THIS")}); llSay(0, \"broke\"); }} }}");
        h.PumpUntil(() => owner.ScriptQuestions.Count >= 1);
        owner.FireScriptAnswer(child.UUID, item, Sl("PERMISSION_CHANGE_LINKS"));
        h.PumpUntil(() => h.Said.Contains("broke") && group.PrimCount == 2);
        Assert.Contains("broke", h.Said);
        Assert.NotSame(group, child.ParentGroup);
        Assert.Equal(2, group.PrimCount);
    }

    [Fact]
    public void DataPayinfoIsAnswered()
    {
        using var h = new SchedulerHarness();
        var client = h.AddClient();
        // iwGetAgentData is the synchronous twin of llRequestAgentData and shares its DATA_* switch; the
        // llRequestAgentData form is in DataserverQueryKeyTests (part F), which it needs to return at all.
        h.RezScript($"default {{ state_entry() {{ llSay(0, \"ds=\" + iwGetAgentData(\"{client.AgentId}\", {Sl("DATA_PAYINFO")})); }} }}");
        h.PumpUntil(() => h.Said.Any(s => s.StartsWith("ds=")));
        Assert.True(h.Said.Any(s => Regex.IsMatch(s, "^ds=[0-3]$")), Said(h));
    }

    [Fact]
    public void CharacterDesiredSpeedReachesTheBot()
    {
        using var h = new SchedulerHarness();
        var bots = RecordingBots.Create(out var rec);
        h.Scene.RegisterModuleInterface<IBotManager>(bots);
        h.RezScript($"default {{ state_entry() {{ llCreateCharacter([{Sl("CHARACTER_DESIRED_SPEED")}, 3.5]); llUpdateCharacter([{Sl("CHARACTER_DESIRED_SPEED")}, 2.0]); llSay(0, \"done\"); }} }}");
        h.PumpUntil(() => { lock (rec.Speeds) return h.Said.Contains("done") && rec.Speeds.Count >= 2; });
        Assert.Contains("done", h.Said);
        Assert.Equal(new[] { 3.5f, 2.0f }, rec.Speeds);
    }

    [Fact]
    public void CastRayHonoursMaxHitsAndReturnsTheNormal()
    {
        using var h = new SchedulerHarness();
        var old = h.Scene.PhysicsScene;
        h.Scene.PhysicsScene = new RayScene();
        try
        {
            h.RezScript($"default {{ state_entry() {{ list r = llCastRay(<10,10,50>, <10,10,0>, [{Sl("RC_MAX_HITS")}, 3, {Sl("RC_DATA_FLAGS")}, {Sl("RC_GET_NORMAL")}]); " +
                        "llSay(0, \"n=\" + (string)llGetListLength(r) + \" status=\" + (string)llList2Integer(r, -1) + \" normal=\" + (string)llList2Vector(r, 2)); } }");
            h.PumpUntil(() => h.Said.Any(s => s.StartsWith("n=")));
            Assert.True(h.Said.Contains("n=10 status=3 normal=<0.00000, 0.00000, 1.00000>"), Said(h));
        }
        finally { h.Scene.PhysicsScene = old; }
    }

    /// <summary>A physics scene whose raycast reports five terrain hits, each with an up normal.</summary>
    private sealed class RayScene : OpenSim.Region.PhysicsModules.BasicPhysics.BasicScene
    {
        public override List<ContactResult> RaycastWorld(Vector3 position, Vector3 direction, float length, int Count)
            => Enumerable.Range(1, 5).Select(i => new ContactResult { ConsumerID = 0, Depth = i, Pos = new Vector3(10, 10, 50 - i), Normal = Vector3.UnitZ }).ToList();
    }
}

/// <summary>An ILandChannel with two parcels: x below 128 is the first, the rest the second.</summary>
public class TwoParcels : DispatchProxy
{
    private ILandObject m_near, m_far;

    public static ILandChannel Create(ILandObject near, ILandObject far)
    {
        var p = Create<ILandChannel, TwoParcels>();
        var me = (TwoParcels)(object)p;
        me.m_near = near;
        me.m_far = far;
        return p;
    }

    protected override object Invoke(MethodInfo m, object[] a)
    {
        if (m.Name == "GetLandObject" || m.Name == "GetLandObjectClippedXY")
        {
            float x = a.Length == 2 ? Convert.ToSingle(a[0]) : a[0] is Vector3 v ? v.X : 0;
            return x < 128 ? m_near : m_far;
        }
        if (m.Name == "AllParcels") return new List<ILandObject> { m_near, m_far };
        var rt = m.ReturnType;
        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
    }
}

/// <summary>An IBotManager that creates a bot id on request and records every speed it is given.</summary>
public class RecordingBots : DispatchProxy
{
    public List<float> Speeds { get; } = new();

    public static IBotManager Create(out RecordingBots rec)
    {
        var p = Create<IBotManager, RecordingBots>();
        rec = (RecordingBots)(object)p;
        return p;
    }

    protected override object Invoke(MethodInfo m, object[] a)
    {
        if (m.Name == "CreateBot") return UUID.Random();
        if (m.Name == "SetBotSpeed") lock (Speeds) Speeds.Add((float)a[1]);
        var rt = m.ReturnType;
        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
    }
}
