using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Services.Interfaces;

/// <summary>
/// The read side of server-side baking (Design Brief C4, ADR-002): given an agent, a bake channel and the asset
/// UUID the viewer believes that channel holds, hand back the stored bake.
///
/// <para>
/// This is what the LL viewer fetches from on a bit-0 region. It stops compositing there and asks
/// <c>agent_appearance_service</c> for every other avatar's bakes, so with no such service an avatar on such a
/// region never textures — which is precisely what a bit-0 region without S4 produces, and why the build plan
/// puts S4 before any flag flip.
/// </para>
/// </summary>
public interface IAppearanceService
{
    /// <summary>
    /// The stored bake for one channel, or null when there is nothing to serve.
    /// </summary>
    /// <param name="agentId">Whose bake.</param>
    /// <param name="channelToken">
    /// The channel as the viewer spells it in the URL — see <see cref="AppearanceChannels"/>, which is the
    /// authority for the token set.
    /// </param>
    /// <param name="expectedAssetId">
    /// The asset id the viewer took from the avatar's TextureEntry. The service returns null unless the index
    /// agrees with it: a viewer asking for a bake the sim has since superseded must get a 404 and re-read the
    /// appearance, never a different avatar's pixels.
    /// </param>
    /// <returns>The asset, or null for "nothing here" — which the connector turns into 404.</returns>
    AssetBase GetBake(UUID agentId, string channelToken, UUID expectedAssetId);
}
