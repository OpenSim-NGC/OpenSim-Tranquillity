using System;
using System.Collections.Concurrent;
using OpenMetaverse;

namespace OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;

/// <summary>What the cap should do with one <c>UpdateAvatarAppearance</c> POST.</summary>
public enum CofVerdict
{
    /// <summary>The viewer and the sim agree on the COF version: bake (or reuse) and answer <c>{success:true}</c>.</summary>
    Bake,

    /// <summary>The viewer is behind the sim: answer <c>{success:false, expected:&lt;server&gt;}</c> and let it re-request.</summary>
    Stale,

    /// <summary>
    /// Too many mismatches too quickly (Ledger R-2). Bake at the server's version and answer
    /// <c>{success:true}</c> rather than trade refusals with the viewer forever.
    /// </summary>
    LivelockBake,
}

/// <summary>One decision: what to do, and the version to quote back.</summary>
/// <param name="Verdict">The action.</param>
/// <param name="Version">The server's COF version after any re-read — the value to bake at, or to return as <c>expected</c>.</param>
/// <param name="Reason">A short human-readable account, for the log and the test failure message.</param>
public sealed record CofDecision(CofVerdict Verdict, int Version, string Reason)
{
    /// <summary>Whether the cap answers <c>success:true</c>.</summary>
    public bool Success => Verdict is CofVerdict.Bake or CofVerdict.LivelockBake;
}

/// <summary>
/// The Design Brief §4.3 handshake, kept free of <c>Scene</c>, HTTP and the clock so every branch is a plain unit
/// test. One instance per region; it holds only the per-agent mismatch counters the anti-livelock rule needs.
///
/// <list type="bullet">
///   <item><c>cof_version == server</c> → <see cref="CofVerdict.Bake"/>.</item>
///   <item><c>cof_version &lt; server</c> → <see cref="CofVerdict.Stale"/>, quoting the server's version.</item>
///   <item><c>cof_version &gt; server</c> → the viewer changed the COF by a path this sim has not seen yet, so the
///     folder is re-read <b>once</b> and the comparison repeated. Equal after the re-read is a
///     <see cref="CofVerdict.Bake"/>; anything else is <see cref="CofVerdict.Stale"/> quoting the freshly read
///     version, which is the only number the sim can honestly offer.</item>
///   <item>After <see cref="MaxMismatches"/> Stale verdicts for one agent inside <see cref="Window"/>, the next
///     POST is a <see cref="CofVerdict.LivelockBake"/> instead: bake at the server's version and log it
///     (Ledger R-2). A successful bake clears the agent's counter.</item>
/// </list>
/// </summary>
public sealed class CofHandshake
{
    /// <summary>Consecutive mismatches inside <see cref="Window"/> before the anti-livelock rule fires.</summary>
    public int MaxMismatches { get; init; } = 5;

    /// <summary>The window the mismatches have to fall inside.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(30);

    private sealed class Counter
    {
        public int Count;
        public DateTime FirstUtc;
    }

    private readonly ConcurrentDictionary<UUID, Counter> m_mismatches = new();

    /// <summary>Mismatches currently counted for an agent; 0 when it has none or they have aged out.</summary>
    public int MismatchesFor(UUID agentId) => m_mismatches.TryGetValue(agentId, out var c) ? c.Count : 0;

    /// <summary>Forget an agent's counter — on a successful bake, and when the agent leaves the region.</summary>
    public void Clear(UUID agentId) => m_mismatches.TryRemove(agentId, out _);

    /// <summary>
    /// Decide one POST.
    /// </summary>
    /// <param name="agentId">The posting agent.</param>
    /// <param name="clientVersion">The <c>cof_version</c> the viewer sent.</param>
    /// <param name="serverVersion">The COF folder version the sim has just read.</param>
    /// <param name="reread">
    /// Reads the COF folder version again. Called at most once per decision, and only on the greater-than branch.
    /// </param>
    /// <param name="nowUtc">The clock, injected so the window is testable.</param>
    public CofDecision Decide(UUID agentId, int clientVersion, int serverVersion, Func<int> reread, DateTime nowUtc)
    {
        if (clientVersion == serverVersion)
        {
            Clear(agentId);
            return new CofDecision(CofVerdict.Bake, serverVersion, "cof_version matches");
        }

        if (clientVersion > serverVersion)
        {
            // The viewer is ahead: it changed the COF through a path this sim has not observed (AIS, most
            // likely). Read the folder again before refusing — the write may simply have landed after our read.
            int fresh = serverVersion;
            try { fresh = reread is null ? serverVersion : reread(); }
            catch (Exception) { /* keep the version we already had; the mismatch path below is still correct */ }

            if (clientVersion == fresh)
            {
                Clear(agentId);
                return new CofDecision(CofVerdict.Bake, fresh, "cof_version matched after re-reading the folder");
            }
            serverVersion = fresh;
        }

        // Not equal, and the re-read did not rescue it. Count the mismatch and either refuse or, if the viewer
        // has been refused too often too fast, bake anyway so the two cannot trade refusals forever.
        var counter = m_mismatches.AddOrUpdate(
            agentId,
            _ => new Counter { Count = 1, FirstUtc = nowUtc },
            (_, existing) =>
            {
                if (nowUtc - existing.FirstUtc > Window)
                {
                    existing.Count = 1;
                    existing.FirstUtc = nowUtc;
                }
                else existing.Count++;
                return existing;
            });

        if (counter.Count >= MaxMismatches)
        {
            Clear(agentId);
            return new CofDecision(CofVerdict.LivelockBake, serverVersion,
                $"{counter.Count} mismatches within {Window.TotalSeconds:F0}s (client {clientVersion}, server {serverVersion}); baking at the server's version");
        }

        return new CofDecision(CofVerdict.Stale, serverVersion,
            $"client cof_version {clientVersion}, server {serverVersion}");
    }
}
