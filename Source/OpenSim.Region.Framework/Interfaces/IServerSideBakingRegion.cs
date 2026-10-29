using OpenMetaverse;

namespace OpenSim.Region.Framework.Interfaces;

/// <summary>
/// The wire-visible part of server-side baking, registered per scene by the baking module and read by the client
/// stack. It exists so that <c>LLClientView</c> and <c>ScenePresence</c> can gate two pieces of the LL viewer
/// contract on the region's <c>[Appearance] ServerSideBaking</c> flag without either of them referencing the
/// optional module or the bake library:
/// <list type="bullet">
///   <item><b>V1</b> — <c>RegionHandshake.RegionProtocols</c> bit 0 tells the viewer to expect server bakes
///     (<c>llviewerregion.cpp:3097</c>).</item>
///   <item><b>V4/V5</b> — an <c>AvatarAppearance</c> for a sim-baked avatar carries an <c>AppearanceData</c>
///     block; without it the viewer discards its own appearance as stale
///     (<c>llvoavatar.cpp:9779-9800</c>, <c>:9727-9737</c>).</item>
/// </list>
///
/// <para>
/// A region with no baking module registers nothing, <c>RequestModuleInterface</c> returns null, and every call
/// site keeps its pre-SSB behaviour — which is the ADR-001 rule that Firestorm's client-bake path must be
/// untouched wherever the flag is off.
/// </para>
/// </summary>
public interface IServerSideBakingRegion
{
    /// <summary>
    /// Whether <c>[Appearance] ServerSideBaking</c> resolved true for this region. Gates bit 0 of
    /// <c>RegionProtocols</c> and nothing else on its own: an avatar this sim has not baked still gets the
    /// count-0 <c>AppearanceData</c> form.
    /// </summary>
    bool ServerSideBakingEnabled { get; }

    /// <summary>
    /// The Current Outfit folder version of the bake this sim last applied to the agent in this region, or
    /// <b>-1</b> when this sim has not baked that agent — in which case the appearance goes out in exactly the
    /// form it always has, with no <c>AppearanceData</c> block.
    /// </summary>
    int BakedCofVersion(UUID agentId);
}
