/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The log side of ProvisionRefusalCache. A viewer that is refused a provision re-provisions at about 2 Hz until
 * something changes, so one refusal is a run of them: an episode. The episode writes two lines whatever its length:
 * one when the first refusal happens, and one with the count when it ends. It ends when the viewer is admitted,
 * logs out or is let back in, or when it has not been refused for a quiet minute.
 *
 * The cache hands this the number of retries each window answered from memory, so the count is exact. Pure and
 * clock-injectable so it unit-tests; the owner calls Start to have quiet episodes closed by a timer.
 */

using System.Threading;
using Microsoft.Extensions.Logging;
using OpenMetaverse;

namespace osWebRtcVoice;

public sealed class RefusalEpisodeLog : IDisposable
{
    /// <summary>How long a viewer must go without a refusal for its episode to count as over.</summary>
    public static readonly TimeSpan DefaultQuiet = TimeSpan.FromMinutes(1);

    /// <summary>How often a started log looks for episodes that have gone quiet.</summary>
    public static readonly TimeSpan DefaultSweep = TimeSpan.FromSeconds(15);

    private sealed class Episode
    {
        public long StartMs;
        public long LastMs;
        public int Refusals;
        public string Where;
    }

    private readonly ILogger _log;
    private readonly string _prefix;
    private readonly long _quietMs;
    private readonly Func<long> _clockMs;
    private readonly Dictionary<(UUID Agent, UUID Scope), Episode> _episodes = new();
    private Timer _timer;
    private volatile ProvisionRefusalCache _cache;

    public RefusalEpisodeLog(ILogger pLog, string pPrefix, TimeSpan? pWindow = null, TimeSpan? pQuiet = null, Func<long> pClockMs = null)
    {
        _log = pLog;
        _prefix = pPrefix;
        _quietMs = (long)(pQuiet ?? DefaultQuiet).TotalMilliseconds;
        _clockMs = pClockMs ?? (() => Environment.TickCount64);
        _cache = new ProvisionRefusalCache(pWindow ?? TimeSpan.FromSeconds(ProvisionRefusalCache.DefaultSeconds), _clockMs);
    }

    /// <summary>The negative cache whose refusals this logs.</summary>
    public ProvisionRefusalCache Cache => _cache;

    /// <summary>Replace the cache with one of another window (from config). Episodes in progress are kept.</summary>
    public void SetWindow(TimeSpan pWindow) => _cache = new ProvisionRefusalCache(pWindow, _clockMs);

    /// <summary>Number of episodes that have begun and not ended.</summary>
    public int Open
    {
        get { lock (_episodes) return _episodes.Count; }
    }

    /// <summary>
    /// A refusal was just recorded in the cache. <paramref name="pSuppressed"/> is what RecordRefusal returned: the retries the
    /// previous window answered from the cache. The first refusal of an episode is logged; the others are only counted.
    /// </summary>
    /// <param name="pWhere">Where the refusal was, for the line, with its leading space (for example ` in "Region"`).</param>
    /// <param name="pDetail">Why, for the line, with its leading space; may be empty.</param>
    public void Refused(UUID pAgent, UUID pScope, string pWhere, string pDetail, int pSuppressed)
    {
        if (!_cache.Enabled)
            return;
        long now = _clockMs();
        bool first = false;
        lock (_episodes)
        {
            if (!_episodes.TryGetValue((pAgent, pScope), out Episode e))
            {
                e = new Episode { StartMs = now, Where = pWhere };
                _episodes[(pAgent, pScope)] = e;
                first = true;
            }
            e.LastMs = now;
            e.Refusals += 1 + Math.Max(0, pSuppressed);
        }
        if (first)
            _log.LogInformation("{Prefix} provision from {Agent}{Where} refused{Detail}; further refusals for this viewer are counted, "
                + "and reported when they stop", _prefix, pAgent, pWhere, pDetail);
    }

    /// <summary>
    /// The viewer was admitted, logged out or let back in: forget its cached refusal and close its episode. Says how many
    /// times it was refused, when that was more than once.
    /// </summary>
    public void Ended(UUID pAgent, UUID pScope, string pWhy = "it is no longer refused")
    {
        int live = _cache.Clear(pAgent, pScope);
        Episode e;
        lock (_episodes)
        {
            if (!_episodes.Remove((pAgent, pScope), out e))
                return;
        }
        Report(pAgent, e, live, _clockMs(), pWhy);
    }

    /// <summary>Closes the episodes that have had no refusal for the quiet period. Returns how many.</summary>
    public int Sweep()
    {
        long now = _clockMs();
        List<((UUID Agent, UUID Scope) Key, Episode E)> quiet = new();
        lock (_episodes)
        {
            foreach (KeyValuePair<(UUID Agent, UUID Scope), Episode> kv in _episodes)
                if (now - kv.Value.LastMs >= _quietMs)
                    quiet.Add((kv.Key, kv.Value));
            foreach (var q in quiet)
                _episodes.Remove(q.Key);
        }
        foreach (var q in quiet)
            Report(q.Key.Agent, q.E, _cache.Clear(q.Key.Agent, q.Key.Scope), q.E.LastMs, $"no retry for {_quietMs / 1000} s");
        return quiet.Count;
    }

    private void Report(UUID pAgent, Episode pEpisode, int pLiveSuppressed, long pEndMs, string pWhy)
    {
        int refusals = pEpisode.Refusals + Math.Max(0, pLiveSuppressed);
        if (refusals <= 1)
            return;
        _log.LogInformation("{Prefix} provision from {Agent}{Where} was refused {Count} times in {Seconds} s; {Why}",
            _prefix, pAgent, pEpisode.Where, refusals, Math.Max(0, (pEndMs - pEpisode.StartMs) / 1000), pWhy);
    }

    /// <summary>Have a timer close the quiet episodes. Without it the owner calls <see cref="Sweep"/>.</summary>
    public void Start(TimeSpan? pEvery = null)
    {
        TimeSpan every = pEvery ?? DefaultSweep;
        lock (_episodes)
            _timer ??= new Timer(_ => { try { Sweep(); } catch (Exception e) { _log.LogWarning(e, "{Prefix} refusal episode sweep failed", _prefix); } },
                null, every, every);
    }

    public void Dispose()
    {
        Timer t;
        lock (_episodes)
        {
            t = _timer;
            _timer = null;
        }
        t?.Dispose();
    }
}
