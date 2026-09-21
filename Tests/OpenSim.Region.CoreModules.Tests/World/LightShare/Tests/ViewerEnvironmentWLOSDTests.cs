using System.Text;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using Xunit;

namespace OpenSim.Region.CoreModules.World.LightShare.Tests;

/// <summary>
/// ENV-1. <see cref="ViewerEnvironment.FromWLOSD"/> given a body that is not an LLSD array.
///
/// <para><b>The defect.</b> The method casts with <c>osd as OSDArray</c> and then guards on
/// <c>if (osd != null)</c> — the parameter, not the cast result. A non-array <see cref="OSD"/> is still
/// non-null, so the guard passed and <c>null</c> was handed to <c>DayCycle.FromWLOSD(OSDArray)</c>, which
/// dereferences <c>array.Count</c> on its first statement (<c>ViewerDaycycle.cs:71</c>) and threw
/// <see cref="System.NullReferenceException"/>.</para>
///
/// <para><b>Why that throw was load-bearing, and why this is not merely tidying.</b> The legacy WindLight
/// setter (<c>EnvironmentModule.SetEnvironmentSettings</c>) parses with the auto-detect entry, holds the result
/// as a bare <c>OSD</c> with no type check, and calls <c>StoreOnRegion(VEnv)</c> — a write. The throw happened
/// to land <i>before</i> that write, so nothing was corrupted. Making <c>DayCycle.FromWLOSD</c> null-tolerant —
/// an obvious-looking hardening — would have removed the throw and let a fresh, <b>default</b>
/// <c>ViewerEnvironment</c> be stored, blanking the region's environment from a truncated request. That is the
/// trap recorded in <c>Docs/feature/ais-v3/AUDIT-1-MALFORMED-LLSD.md</c> §5.</para>
///
/// <para><b>And note that fixing the guard alone would have armed it.</b> With the guard corrected, a non-array
/// body makes <c>FromWLOSD</c> a silent no-op rather than a throw — which is the right contract for the method
/// (it returns <c>void</c> and its only other "cannot use this" path is to leave <c>Cycle</c> alone), but it
/// removes the accidental protection the setter was relying on. That is why ENV-1 changes <b>two</b> sites: the
/// guard here, and a type check at the setter boundary. These tests cover the first; the second is a handler
/// reachable only through a cap dispatch with a Scene, estate permissions and a ScenePresence, and there is no
/// harness for it in this project — see the session report.</para>
///
/// <para>The degenerate value used below is the real one: <c>OSDParser.DeserializeLLSDXml</c> returns a bare
/// <c>OSD</c> with <c>OSDType.Unknown</c> for a truncated body rather than throwing or returning null
/// (AIS-AUDIT-1 §1a). The tests assert against that value, not against a hand-made stand-in.</para>
/// </summary>
public class ViewerEnvironmentWLOSDTests
{
    /// <summary>What a truncated LLSD XML body actually parses to. Not a stand-in: this is the parser's output.</summary>
    private static OSD Degenerate()
    {
        OSD osd = OSDParser.DeserializeLLSDXml(Encoding.UTF8.GetBytes("<llsd><array><map>"));
        Assert.NotNull(osd);                       // the trap: it is NOT null
        Assert.Equal(OSDType.Unknown, osd.Type);   // and it is NOT a usable type
        Assert.Null(osd as OSDArray);              // so the cast in FromWLOSD yields null
        return osd;
    }

    /// <summary>A minimally valid WindLight body: the array shape DayCycle.FromWLOSD expects.</summary>
    private static OSDArray ValidWLBody()
    {
        var skyTracks = new OSDArray { new OSDArray { OSD.FromReal(0.0), OSD.FromString("sky0") } };
        var skyFrames = new OSDMap { ["sky0"] = new OSDMap { ["sun_angle"] = OSD.FromReal(1.0) } };
        var water = new OSDMap { ["waterFogDensity"] = OSD.FromReal(2.0) };
        return new OSDArray { new OSDMap(), skyTracks, skyFrames, water };
    }

    [Fact]
    public void a_degenerate_body_is_refused_without_reaching_DayCycle()
    {
        var env = new ViewerEnvironment();

        // Before ENV-1 this threw NullReferenceException from DayCycle.FromWLOSD(null).
        // After ENV-1 it is a clean refusal: no exception, and nothing handed downstream.
        env.FromWLOSD(Degenerate());

        // A clean refusal, not a silent success: the environment must not claim to be a parsed legacy one.
        Assert.False(env.IsLegacy, "a refused body must not mark the environment as a parsed legacy one");
    }

    [Fact]
    public void an_OSDMap_body_is_refused_without_reaching_DayCycle()
    {
        var env = new ViewerEnvironment();

        // An OSDMap is a perfectly valid OSD, just not the array shape this method takes - so the old
        // `osd != null` guard admitted it too, and it reached DayCycle.FromWLOSD(null) exactly the same way.
        env.FromWLOSD(new OSDMap { ["environment"] = new OSDMap() });

        Assert.False(env.IsLegacy);
    }

    /// <summary>
    /// The property that matters most: a malformed body must not be able to wipe an environment that has
    /// already been populated. This is the object-level form of "the region's environment survives".
    /// </summary>
    [Fact]
    public void a_degenerate_body_does_not_blank_an_already_populated_environment()
    {
        var env = new ViewerEnvironment();
        env.FromWLOSD(ValidWLBody());
        byte[] loaded = OSDParser.SerializeLLSDXmlBytes(env.ToOSD());

        env.FromWLOSD(Degenerate());
        byte[] after = OSDParser.SerializeLLSDXmlBytes(env.ToOSD());

        Assert.Equal(loaded, after);   // byte-identical: the refusal changed nothing
    }

    /// <summary>A valid array still parses, so the fix is a refusal of bad input and not an outage.</summary>
    [Fact]
    public void a_valid_WindLight_array_still_parses()
    {
        var fresh = new ViewerEnvironment();
        byte[] before = OSDParser.SerializeLLSDXmlBytes(fresh.ToOSD());

        var env = new ViewerEnvironment();
        env.FromWLOSD(ValidWLBody());
        byte[] after = OSDParser.SerializeLLSDXmlBytes(env.ToOSD());

        Assert.NotEqual(before, after);   // it really did take the body
    }
}
