/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The per-listener visibility matrix: for each listener, the set of source UUIDs it must not
 * hear. Exclusion is stored per listener (directed) because the mixer applies it per listener;
 * SeeAVs/ban are computed symmetrically so both directions are present. This one object is the
 * single source of truth for future audio exclusion AND roster/join omission (semantics doc 1).
 */

using System.Collections.Generic;
using OpenMetaverse;

namespace osWebRtcVoice
{
    public sealed class VisibilityMatrix
    {
        private static readonly IReadOnlySet<UUID> EmptySet = new HashSet<UUID>();

        private readonly Dictionary<UUID, HashSet<UUID>> _excluded;
        // Parallel to _excluded: per-listener MODERATION-MUTED sources (Option A mute channel). A
        // source here STAYS in the roster (greyed) rather than being removed; the two sets are
        // disjoint per listener by construction in Build (ban wins). An empty _muted (older code path,
        // or no moderation in force) is exactly today's behaviour.
        private readonly Dictionary<UUID, HashSet<UUID>> _muted;

        // Every agent the matrix was built from, in Build order, including agents with two empty
        // columns. Arming needs the population as a first-class set, because the
        // exclusion and mute maps above store non-empty columns only.
        private readonly IReadOnlyList<UUID> _population;

        private VisibilityMatrix(Dictionary<UUID, HashSet<UUID>> excluded, Dictionary<UUID, HashSet<UUID>> muted,
            IReadOnlyList<UUID> population)
        {
            _excluded = excluded;
            _muted = muted;
            _population = population;
        }

        /// An empty matrix (no agents / no exclusions) - the Diff baseline for bootstrap.
        public static readonly VisibilityMatrix Empty =
            new VisibilityMatrix(new Dictionary<UUID, HashSet<UUID>>(), new Dictionary<UUID, HashSet<UUID>>(), new List<UUID>());

        /// Every agent in the matrix's population, empty columns included (arming).
        public IReadOnlyList<UUID> Population => _population;

        /// Listeners that have at least one exclusion.
        public IReadOnlyCollection<UUID> Listeners => _excluded.Keys;

        /// Listeners that have at least one moderation-mute.
        public IReadOnlyCollection<UUID> MutedListeners => _muted.Keys;

        /// The set of sources excluded (ban/visibility) for a listener (empty if none).
        public IReadOnlySet<UUID> ExcludedFor(UUID listener)
            => _excluded.TryGetValue(listener, out HashSet<UUID> s) ? s : EmptySet;

        /// The set of sources moderation-muted for a listener (empty if none).
        public IReadOnlySet<UUID> MutedFor(UUID listener)
            => _muted.TryGetValue(listener, out HashSet<UUID> s) ? s : EmptySet;

        public bool IsExcluded(UUID listener, UUID source)
            => _excluded.TryGetValue(listener, out HashSet<UUID> s) && s.Contains(source);

        public bool IsMuted(UUID listener, UUID source)
            => _muted.TryGetValue(listener, out HashSet<UUID> s) && s.Contains(source);

        /// One code path: enumerate all presences, resolve each parcel once, apply the rules to
        /// every ordered pair. Root parcels via cached GlobalID; child parcels via position.
        public static VisibilityMatrix Build(IFeederWorld world)
        {
            IReadOnlyList<AgentView> agents = world.SnapshotAgents();
            EstateView estate = world.Estate;

            int n = agents.Count;
            var parcels = new ParcelView[n];
            for (int i = 0; i < n; i++)
                parcels[i] = ResolveParcel(world, agents[i]);

            var excluded = new Dictionary<UUID, HashSet<UUID>>(n);
            var muted = new Dictionary<UUID, HashSet<UUID>>(n);
            var population = new List<UUID>(n);
            for (int i = 0; i < n; i++)
                population.Add(agents[i].Id);
            for (int li = 0; li < n; li++)
            {
                AgentView L = agents[li];
                ParcelView pL = parcels[li];
                HashSet<UUID> exclSet = null;
                HashSet<UUID> muteSet = null;
                for (int si = 0; si < n; si++)
                {
                    if (si == li)
                        continue;
                    // BAN WINS (deliverable #6): a source excluded by ban/visibility goes to the
                    // exclusion set and is NOT also offered to the mute set - removal is stricter than
                    // silence. Only a source that is NOT excluded but IS moderation-muted goes to the
                    // mute set. The two sets are therefore disjoint per listener by construction.
                    if (VisibilityRules.IsExcluded(L, pL, agents[si], parcels[si], estate))
                        (exclSet ??= new HashSet<UUID>()).Add(agents[si].Id);
                    else if (VisibilityRules.IsModerationMuted(agents[si], parcels[si]))
                        (muteSet ??= new HashSet<UUID>()).Add(agents[si].Id);
                }
                if (exclSet != null)
                    excluded[L.Id] = exclSet;
                if (muteSet != null)
                    muted[L.Id] = muteSet;
            }
            return new VisibilityMatrix(excluded, muted, population);
        }

        /// Root: cached currentParcelUUID when known. Child (or root with unknown parcel):
        /// position-derived, since children get no crossing event (baseline 2.2 / #9).
        internal static ParcelView ResolveParcel(IFeederWorld world, in AgentView a)
        {
            if (!a.IsChild && a.CurrentParcelUUID != UUID.Zero)
                return world.GetParcelByGlobalId(a.CurrentParcelUUID);
            return world.GetParcelAt(a.Position);
        }
    }
}
