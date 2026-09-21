using System;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Server.Base;
using OpenSim.Services.Base;
using OpenSim.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace OpenSim.Services.AvatarService;

/// <summary>
/// The <c>agent_appearance_service</c> read path (Design Brief C4, ADR-002): resolve a bake channel to the asset
/// the sim stored for it and hand that asset back.
///
/// <para>
/// It owns no storage. The channel → asset mapping is S2's ADR-004 index in the avatar service's key/value table
/// (<c>Bake:&lt;channel&gt;</c>), and the bytes are an ordinary texture asset. Both are read through the service
/// interfaces, so this works identically whether Robust holds them locally or the deployment is a standalone.
/// </para>
///
/// <para>
/// <b>The UUID in the path is a check, not a lookup key.</b> The viewer sends the asset id it took from the
/// avatar's TextureEntry. If the index no longer agrees — the agent re-baked and superseded that asset between
/// the appearance the viewer holds and this request — the answer is 404, so the viewer re-reads the appearance
/// and asks again. Serving whatever the index currently holds would texture the avatar with a bake the viewer
/// did not ask for, and serving the requested id blindly would let any caller pull any asset through this route.
/// </para>
/// </summary>
public class AppearanceService : ServiceBase, IAppearanceService
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    public const string ConfigName = "AppearanceService";

    private readonly IAvatarService m_AvatarService;
    private readonly IAssetService m_AssetService;

    public AppearanceService(IConfigSource config) : this(config, ConfigName)
    {
    }

    public AppearanceService(IConfigSource config, string configName) : base(config)
    {
        IConfig serviceConfig = config.Configs[string.IsNullOrEmpty(configName) ? ConfigName : configName];
        if (serviceConfig is null)
            throw new Exception($"No section '{configName}' in config file");

        string avatarService = serviceConfig.GetString("AvatarService", string.Empty);
        string assetService = serviceConfig.GetString("AssetService", string.Empty);

        if (string.IsNullOrWhiteSpace(avatarService))
            throw new Exception($"[{configName}] AvatarService not set; the appearance service has no bake index to read");
        if (string.IsNullOrWhiteSpace(assetService))
            throw new Exception($"[{configName}] AssetService not set; the appearance service has no assets to serve");

        object[] args = new object[] { config };
        m_AvatarService = ServerUtils.LoadPlugin<IAvatarService>(avatarService, args);
        m_AssetService = ServerUtils.LoadPlugin<IAssetService>(assetService, args);

        if (m_AvatarService is null)
            throw new Exception($"Could not load AvatarService '{avatarService}' for the appearance service");
        if (m_AssetService is null)
            throw new Exception($"Could not load AssetService '{assetService}' for the appearance service");

        m_log.LogInformation("[APPEARANCE SERVICE]: serving baked textures for {Count} channels", AppearanceChannels.Tokens.Count);
    }

    /// <summary>Test seam: the service over supplied dependencies, bypassing plugin loading.</summary>
    protected AppearanceService(IConfigSource config, IAvatarService avatarService, IAssetService assetService) : base(config)
    {
        m_AvatarService = avatarService ?? throw new ArgumentNullException(nameof(avatarService));
        m_AssetService = assetService ?? throw new ArgumentNullException(nameof(assetService));
    }

    /// <inheritdoc/>
    public AssetBase GetBake(UUID agentId, string channelToken, UUID expectedAssetId)
    {
        if (agentId.IsZero()) return null;

        string key = AppearanceChannels.BakeKeyFor(channelToken);
        if (key is null)
        {
            m_log.LogDebug("[APPEARANCE SERVICE]: {Agent} asked for unknown channel '{Channel}'", agentId, channelToken);
            return null;
        }

        AvatarData avatar;
        try { avatar = m_AvatarService.GetAvatar(agentId); }
        catch (Exception e)
        {
            m_log.LogWarning("[APPEARANCE SERVICE]: avatar service threw reading the bake index for {Agent}: {Message}", agentId, e.Message);
            return null;
        }

        if (avatar?.Data is null || !avatar.Data.TryGetValue(key, out string stored) || !UUID.TryParse(stored, out UUID storedId) || storedId.IsZero())
        {
            // No index entry: this agent has never been baked by a simulator that stores one, or the index was
            // cleared. Nothing to serve, and nothing to guess at.
            m_log.LogDebug("[APPEARANCE SERVICE]: no {Key} for {Agent}", key, agentId);
            return null;
        }

        if (storedId.NotEqual(expectedAssetId))
        {
            // The viewer is holding an appearance older (or newer) than the index. Refusing sends it back to the
            // appearance it should be using; answering would paint a bake it never asked for.
            m_log.LogDebug("[APPEARANCE SERVICE]: {Agent} {Key} is {Stored} but {Expected} was requested; refusing",
                agentId, key, storedId, expectedAssetId);
            return null;
        }

        AssetBase asset;
        try { asset = m_AssetService.Get(storedId.ToString()); }
        catch (Exception e)
        {
            m_log.LogWarning("[APPEARANCE SERVICE]: asset service threw fetching {Asset} for {Agent}: {Message}", storedId, agentId, e.Message);
            return null;
        }

        if (asset?.Data is not { Length: > 0 })
        {
            // The index points at an asset that is gone. Same answer as no index at all — the sim re-bakes and
            // rewrites the index the next time it sees this agent (ADR-004).
            m_log.LogWarning("[APPEARANCE SERVICE]: {Key} for {Agent} points at {Asset}, which the asset service does not have",
                key, agentId, storedId);
            return null;
        }

        return asset;
    }
}
