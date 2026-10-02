using System.Globalization;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// llSetPos goes through the same helper as PRIM_POSITION (Halcyon runs both through SetPos(part, v, true)),
/// so the two cannot disagree. SL llSetPos: a child's vector is "a local coordinate relative to the root prim", an
/// attached root's "relative to the attach point", an unattached root's a region coordinate, and "Movement is capped to
/// 10m per call for unattached root prims". Halcyon's caps: an attached root 3.5 m from the attach point, a child of an
/// attachment 54 m from the root, any other child 256 m.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class LlSetPosSharesPrimPositionTests
{
    private readonly ITestOutputHelper _out;
    public LlSetPosSharesPrimPositionTests(ITestOutputHelper o) => _out = o;

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

    /// <summary>The harness object worn on a new avatar's chest, its root at <paramref name="offset"/> from the attach point.</summary>
    private static void Wear(SchedulerHarness h, Vector3 offset)
    {
        var client = h.AddClient();
        var sp = h.Scene.GetScenePresence(client.AgentId);
        var sog = h.Prim.ParentGroup;
        sog.AttachedAvatar = sp.UUID;
        sog.IsAttachment = true;
        sog.AttachmentPoint = (uint)AttachmentPoint.Chest;
        sp.AddAttachment(sog);
        sog.AbsolutePosition = offset;
        sog.RootPart.AttachedPos = offset;
    }

    private void Run(SchedulerHarness h, SceneObjectPart sender, string body)
    {
        h.RezScriptInto(sender, "default { state_entry() { " + body + " llSay(0, \"done\"); } }");
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until && !h.Said.Contains("done")) h.PumpOnce();
        Assert.True(h.Said.Contains("done"), "the script never finished: " + string.Join(" | ", h.Said));
        h.PumpFor(TimeSpan.FromMilliseconds(300));
        _out.WriteLine(string.Join("\n", h.Said));
    }

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 0.001f)
        => Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    private static Vector3 Vec(string lsl)
    {
        var n = lsl.Trim('<', '>').Split(',').Select(s => float.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
        return new Vector3(n[0], n[1], n[2]);
    }

    private static string Line(SchedulerHarness h, string prefix) => h.Said.Single(s => s.StartsWith(prefix)).Substring(prefix.Length);

    // ── A child ───────────────────────────────────────────────────────────────

    [Fact]
    public void OnAChildTheVectorIsItsOffsetFromATurnedRoot()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        parts[0].ParentGroup.UpdateGroupPosition(new Vector3(120, 90, 40));
        parts[0].UpdateRotation(Quaternion.CreateFromEulers(0, 0, (float)Math.PI / 2));
        var rootPos = parts[0].AbsolutePosition;

        Run(h, parts[1], "llSetPos(<0.5, -0.25, 1>);");

        var offset = new Vector3(0.5f, -0.25f, 1f);
        Near(offset, parts[1].OffsetPosition);
        // Turned with the root: +90 degrees about Z takes the offset's X onto the region's Y.
        Near(rootPos + offset * parts[0].RotationOffset, parts[1].AbsolutePosition);
        Near(rootPos + new Vector3(0.25f, 0.5f, 1f), parts[1].AbsolutePosition);
        Near(rootPos, parts[0].AbsolutePosition);
    }

    [Fact]
    public void OnAChildTheOffsetIsCappedAt256MetresFromTheRootAsHalcyonDoes()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        parts[0].ParentGroup.UpdateGroupPosition(new Vector3(128, 128, 30));

        Run(h, parts[1], "llSetPos(<300, 0, 0>);");

        Near(new Vector3(256, 0, 0), parts[1].OffsetPosition);
        Near(new Vector3(128, 128, 30), parts[0].AbsolutePosition);
    }

    [Fact]
    public void OnAChildOfAnAttachmentTheOffsetIsCappedAt54Metres()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        Wear(h, new Vector3(0, 0, 0.2f));

        Run(h, parts[1], "llSetPos(<0, -60, 0>);");

        Near(new Vector3(0, -54, 0), parts[1].OffsetPosition);
        Near(new Vector3(0, 0, 0.2f), parts[0].AttachedPos);
    }

    [Fact]
    public void OnAChildLlSetPosAndPrimPositionAgreeForTheSameVectors()
    {
        using var h = new SchedulerHarness();
        var parts = TwoPrimLinkset(h);
        parts[0].ParentGroup.UpdateGroupPosition(new Vector3(100, 100, 25));
        parts[0].UpdateRotation(Quaternion.CreateFromEulers(0.3f, 0, 1.1f));

        string[] vectors = { "<0.5, -0.25, 1>", "<-3, 2, -1>", "<0, 0, 0>", "<200, 200, 0>", "<-400, 0, 10>" };
        string body = "";
        for (int i = 0; i < vectors.Length; ++i)
            body += $"llSetPos({vectors[i]}); vector a{i} = llGetLocalPos(); " +
                    "llSetPrimitiveParams([PRIM_POSITION, <1, 1, 1>]); " +
                    $"llSetPrimitiveParams([PRIM_POSITION, {vectors[i]}]); vector b{i} = llGetLocalPos(); " +
                    $"llSay(1, \"v{i} \" + (string)a{i} + \"|\" + (string)b{i}); ";
        Run(h, parts[1], body);

        for (int i = 0; i < vectors.Length; ++i)
        {
            var f = Line(h, $"v{i} ").Split('|');
            Near(Vec(f[1]), Vec(f[0]), 0.01f);
        }
        // Both frames are the root's, not the region's; a long offset stops at 256 m along the same line.
        Near(new Vector3(-3, 2, -1), Vec(Line(h, "v1 ").Split('|')[0]), 0.01f);
        Near(new Vector3(200, 200, 0) * (256f / new Vector3(200, 200, 0).Length()), Vec(Line(h, "v3 ").Split('|')[0]), 0.01f);
    }

    // ── The root ──────────────────────────────────────────────────────────────

    [Fact]
    public void OnAnUnattachedRootAMoveStopsAtTenMetres()
    {
        using var h = new SchedulerHarness();
        h.Prim.ParentGroup.UpdateGroupPosition(new Vector3(100, 100, 25));

        Run(h, h.Prim, "llSetPos(llGetPos() + <30, 0, 0>);");

        Near(new Vector3(110, 100, 25), h.Prim.AbsolutePosition);
    }

    [Fact]
    public void OnAnUnattachedRootAShortMoveGoesWhereItIsSent()
    {
        using var h = new SchedulerHarness();
        h.Prim.ParentGroup.UpdateGroupPosition(new Vector3(100, 100, 25));

        Run(h, h.Prim, "llSetPos(<104, 97, 28>);");

        Near(new Vector3(104, 97, 28), h.Prim.AbsolutePosition);
    }

    [Fact]
    public void OnAnUnattachedRootLlSetPosAndPrimPositionAgreeForTheSameMoves()
    {
        using var h = new SchedulerHarness();
        h.Prim.ParentGroup.UpdateGroupPosition(new Vector3(100, 100, 25));

        string[] moves = { "<3, -2, 1>", "<30, 0, 0>", "<-7, -8, 0>", "<0, 0, 12>" };
        string body = "vector p0 = llGetPos(); ";
        for (int i = 0; i < moves.Length; ++i)
            body += $"llSetPos(p0 + {moves[i]}); vector a{i} = llGetPos(); " +
                    "llSetPrimitiveParams([PRIM_POSITION, p0]); " +
                    $"llSetPrimitiveParams([PRIM_POSITION, p0 + {moves[i]}]); vector b{i} = llGetPos(); " +
                    "llSetPrimitiveParams([PRIM_POSITION, p0]); " +
                    $"llSay(1, \"m{i} \" + (string)a{i} + \"|\" + (string)b{i}); ";
        Run(h, h.Prim, body);

        for (int i = 0; i < moves.Length; ++i)
        {
            var f = Line(h, $"m{i} ").Split('|');
            Near(Vec(f[1]), Vec(f[0]), 0.01f);
        }
        Near(new Vector3(110, 100, 25), Vec(Line(h, "m1 ").Split('|')[0]), 0.01f);
        Near(new Vector3(100, 100, 35), Vec(Line(h, "m3 ").Split('|')[0]), 0.01f);
    }

    // ── An attachment's root ─────────────────────────────────────────────────

    [Fact]
    public void OnAnAttachmentsRootTheVectorIsTheOffsetFromTheAttachPointNegativeAxesIncluded()
    {
        using var h = new SchedulerHarness();
        Wear(h, new Vector3(0, 0, 0.2f));

        Run(h, h.Prim, "llSetPos(<-0.5, 0.1, -0.3>);");

        Near(new Vector3(-0.5f, 0.1f, -0.3f), h.Prim.AttachedPos);
        Near(new Vector3(-0.5f, 0.1f, -0.3f), h.Prim.ParentGroup.AbsolutePosition);
    }

    [Fact]
    public void OnAnAttachmentsRootTheOffsetIsCappedAtThreeAndAHalfMetresAsHalcyonDoes()
    {
        using var h = new SchedulerHarness();
        Wear(h, new Vector3(0, 0, 0.2f));

        Run(h, h.Prim, "llSetPos(<5, 0, 0>);");

        Near(new Vector3(3.5f, 0, 0), h.Prim.AttachedPos);
    }
}
