using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using OpenSim.Framework;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;

/// <summary>
/// One region's server-side-baking state: whether the flag resolved on here, which agents this sim has baked and
/// at what COF version, and the §4.3 handshake's counters. Registered on the scene as
/// <see cref="IServerSideBakingRegion"/> so <c>LLClientView</c> and <c>ScenePresence</c> can read the two wire
/// facts without referencing this module.
/// </summary>
public sealed class ServerSideBakingRegion : IServerSideBakingRegion
{
    private readonly ConcurrentDictionary<UUID, int> m_bakedCof = new();

    public ServerSideBakingRegion(bool enabled, CofHandshake handshake)
    {
        ServerSideBakingEnabled = enabled;
        Handshake = handshake;
    }

    /// <inheritdoc/>
    public bool ServerSideBakingEnabled { get; }

    /// <summary>This region's copy of the §4.3 handshake, with its own per-agent mismatch counters.</summary>
    public CofHandshake Handshake { get; }

    /// <inheritdoc/>
    public int BakedCofVersion(UUID agentId) => m_bakedCof.TryGetValue(agentId, out var v) ? v : -1;

    /// <summary>
    /// Record that this sim applied a bake to the agent at the given COF version, which is what makes the
    /// appearance carry an <c>AppearanceData</c> block from now on (V4/V5).
    ///
    /// <para>Only ever called on a flag-on region. On a flag-off region a console bake still writes faces and
    /// sends the appearance, but it must not change the wire — Firestorm is client-baking there and expects the
    /// packet it has always had (ADR-001).</para>
    /// </summary>
    public void RecordBake(UUID agentId, int cofVersion)
    {
        if (!ServerSideBakingEnabled || cofVersion < 0) return;
        m_bakedCof[agentId] = cofVersion;
    }

    /// <summary>Forget an agent on close, so a returning agent is not credited with a bake this sim no longer knows about.</summary>
    public void Forget(UUID agentId)
    {
        m_bakedCof.TryRemove(agentId, out _);
        Handshake.Clear(agentId);
        m_lastChangeBake.TryRemove(agentId, out _);
        m_lastGoodBodyParts.TryRemove(agentId, out _);
    }

    // ------------------------------------------------------------------ S8: the body-part guard

    /// <summary>
    /// The four body-part slots. A body part is not something a resident takes off: a viewer will not let you
    /// remove your skin, and every avatar has all four from creation. So a wearable set that HAD one of these and
    /// now does not is, in practice, always the result of a failed resolution rather than an outfit change - which
    /// is exactly what happened on 2026-09-05, when four unresolvable item ids emptied slots 1-4 and the bake that
    /// followed superseded the four good bakes with "no Skin worn / no Eyes worn / no Hair worn".
    /// </summary>
    public static readonly int[] BodyPartSlots =
    {
        (int)WearableType.Shape, (int)WearableType.Skin, (int)WearableType.Hair, (int)WearableType.Eyes,
    };

    private readonly ConcurrentDictionary<UUID, int[]> m_lastGoodBodyParts = new();

    /// <summary>Which of the four body-part slots this wearable set actually has something in.</summary>
    public static int[] BodyPartsPresent(AvatarWearable[] wearables)
    {
        var present = new List<int>(BodyPartSlots.Length);
        foreach (var slot in BodyPartSlots)
            if (wearables is not null && slot < wearables.Length && wearables[slot] is { Count: > 0 })
                present.Add(slot);
        return present.ToArray();
    }

    /// <summary>
    /// Whether this bake must be refused because a body-part slot the agent had is now empty. Returns null to
    /// proceed. The check is against the last set this sim actually baked from, so the first bake of a session
    /// always proceeds - there is nothing to compare with, and refusing would leave a new arrival unbaked.
    ///
    /// <para>Nothing is recorded here. A refusal must not become the new baseline, or the second attempt would
    /// see no loss and go through; <see cref="RecordGoodBodyParts"/> is called only after a bake succeeds.</para>
    /// </summary>
    public string RefusalForBodyPartLoss(UUID agentId, AvatarWearable[] wearables)
    {
        if (!m_lastGoodBodyParts.TryGetValue(agentId, out var before)) return null;

        var now = BodyPartsPresent(wearables);
        var lost = new List<string>();
        foreach (var slot in before)
            if (Array.IndexOf(now, slot) < 0)
                lost.Add($"{(WearableType)slot} (slot {slot})");

        return lost.Count == 0
            ? null
            : $"body-part slots lost since the last bake: {string.Join(", ", lost)}. A body part is never removed by a resident, "
              + "so this is a resolution failure upstream of the bake, not an outfit change; nothing is baked and nothing is superseded.";
    }

    /// <summary>Remember what a successful bake was made from, as the baseline for the next one.</summary>
    public void RecordGoodBodyParts(UUID agentId, AvatarWearable[] wearables)
        => m_lastGoodBodyParts[agentId] = BodyPartsPresent(wearables);

    private readonly ConcurrentDictionary<UUID, DateTime> m_lastChangeBake = new();

    /// <summary>
    /// How close together two change-triggered bakes for one agent have to be before the second is treated as
    /// part of the same outfit change.
    ///
    /// <para>
    /// The real coalescing is done upstream: every route into a rebake goes through
    /// <c>AvatarFactoryModule.QueueAppearanceSave</c>, whose queue is keyed by agent and drains on a timer
    /// (<c>DelayBeforeAppearanceSave</c>, default 5 s), so signals arriving within one drain already collapse
    /// into one save and one event. This window is the second guard, for signals that land either side of a drain
    /// boundary. It is sized against the one interval that is actually measured — the 5 s save delay — and is
    /// deliberately shorter than it, so it cannot suppress a genuinely distinct change that completed its own
    /// save cycle. The spread between the two signals of a single change has <b>not</b> been measured (Ledger
    /// Q-6); 2 s is an estimate comfortably above any plausible value and comfortably below the save delay, and
    /// the S5 live verify is what will replace the estimate.
    /// </para>
    /// </summary>
    public TimeSpan ChangeDebounce { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Claim the right to bake this agent for a change at <paramref name="nowUtc"/>, or report that a bake for
    /// the same change has just happened. Atomic, because appearance saves run on the thread pool and two can
    /// land at once.
    /// </summary>
    public bool TryClaimChangeBake(UUID agentId, DateTime nowUtc)
    {
        // A flag-off region never claims, so the change trigger cannot fire there even if something subscribes
        // it by mistake. Nothing about the wire changes where the flag is off (ADR-001).
        if (!ServerSideBakingEnabled) return false;

        bool claimed = false;
        m_lastChangeBake.AddOrUpdate(
            agentId,
            _ => { claimed = true; return nowUtc; },
            (_, previous) =>
            {
                if (nowUtc - previous < ChangeDebounce) return previous;
                claimed = true;
                return nowUtc;
            });
        return claimed;
    }

    /// <summary>
    /// Whether the flag is on for one region. The simulator-wide <c>[Appearance] ServerSideBaking</c> is the
    /// default and a <c>[&lt;Region Name&gt;]</c> section may override it with the same key — the per-region idiom
    /// <c>AISv3Module.ResolveEnabled</c> uses for <c>AIS_Enabled</c>, which in turn follows
    /// <c>AutoBackupModule</c>. Static and free of <c>Scene</c> so it can be tested with a plain config source.
    ///
    /// <para>The reason it is per region is the same as AIS's: flipping it hands the LL viewer's whole appearance
    /// path to this code, and Firestorm on that region stops client-baking the moment bit 0 appears in the
    /// handshake. It has to be possible to try it on exactly one region (Design Brief §4.5).</para>
    /// </summary>
    public static bool ResolveEnabled(bool simulatorDefault, IConfigSource sceneConfig, string regionName)
    {
        if (sceneConfig is null || string.IsNullOrEmpty(regionName)) return simulatorDefault;
        IConfig regionConfig = sceneConfig.Configs[regionName];
        return regionConfig is null ? simulatorDefault : regionConfig.GetBoolean("ServerSideBaking", simulatorDefault);
    }

    /// <summary>
    /// S12: which config decided this region's flag - <c>"region section"</c> when the region's own section carries
    /// a <c>ServerSideBaking</c> key, <c>"global"</c> otherwise. Purely for the startup line: an operator who has
    /// just added the two global lines needs the log to say the global path is what turned the region on, and an
    /// operator debugging one wrong region needs to know a section is overriding them.
    /// </summary>
    public static string EnabledSource(IConfigSource sceneConfig, string regionName)
    {
        if (sceneConfig is null || string.IsNullOrEmpty(regionName)) return GlobalSource;
        IConfig regionConfig = sceneConfig.Configs[regionName];
        return regionConfig is not null && regionConfig.Contains("ServerSideBaking") ? RegionSource : GlobalSource;
    }

    /// <summary>The two answers <see cref="EnabledSource"/> gives, named so the log line and its test cannot drift.</summary>
    public const string GlobalSource = "global";
    public const string RegionSource = "region section";
}
