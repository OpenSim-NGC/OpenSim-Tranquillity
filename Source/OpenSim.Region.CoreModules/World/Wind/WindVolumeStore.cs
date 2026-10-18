/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.CoreModules.World.Wind;

/// <summary>
/// The region's wind volumes, in memory only.
///
/// Writers (create, remove, prune) take a lock and publish a new immutable array of volumes. Readers (sample, list,
/// count) read the current array with a single volatile read and never take the lock, so a sampler never waits for a
/// writer, and never sees a half-made volume. Expiry is decided when a volume is read, so a volume whose lifetime
/// has passed stops counting at once, before anything frees it.
/// </summary>
public sealed class WindVolumeStore : IWindVolumeStore
{
    private static readonly WindVolume[] None = Array.Empty<WindVolume>();

    private readonly Func<long> m_clockMs;
    private readonly object m_writeLock = new();
    private WindVolume[] m_snapshot = None;
    private int m_lastId;

    public bool Enabled { get; }
    public WindVolumeLimits Limits { get; }

    /// <param name="enabled">False makes a store that accepts nothing.</param>
    /// <param name="limits">The caps; defaults when null.</param>
    /// <param name="clockMs">Milliseconds from a steady clock; the environment's tick count when null. Tests pass their own.</param>
    public WindVolumeStore(bool enabled, WindVolumeLimits limits = null, Func<long> clockMs = null)
    {
        Enabled = enabled;
        Limits = limits ?? new WindVolumeLimits();
        m_clockMs = clockMs ?? (() => Environment.TickCount64);
    }

    public int Count
    {
        get
        {
            long now = m_clockMs();
            int n = 0;
            foreach (WindVolume v in Volatile.Read(ref m_snapshot))
                if (now < v.ExpiresAtMs) n++;
            return n;
        }
    }

    public int Create(UUID creatorObject, UUID owner, in WindVolumeSpec spec)
    {
        if (!Enabled) return WindVolumeError.Disabled;

        int bad = Validate(spec);
        if (bad != WindVolumeError.Ok) return bad;

        lock (m_writeLock)
        {
            long now = m_clockMs();
            WindVolume[] live = Live(m_snapshot, now);

            if (live.Length >= Limits.MaxVolumesRegion) return WindVolumeError.RegionFull;
            if (!creatorObject.IsZero() && CountBy(live, creatorObject) >= Limits.MaxVolumesObject)
                return WindVolumeError.ObjectFull;

            int id = NextId(live);
            var volume = new WindVolume(id, creatorObject, owner, spec, now, now + (long)(spec.LifetimeSeconds * 1000f));

            var next = new WindVolume[live.Length + 1];
            live.CopyTo(next, 0);
            next[live.Length] = volume;
            Volatile.Write(ref m_snapshot, next);
            return id;
        }
    }

    public int Remove(int handle, UUID creatorObject)
    {
        lock (m_writeLock)
        {
            long now = m_clockMs();
            WindVolume[] live = Live(m_snapshot, now);
            int at = Array.FindIndex(live, v => v.Id == handle);
            if (at < 0)
            {
                Publish(live);
                return WindVolumeError.NotFound;
            }
            if (!creatorObject.IsZero() && live[at].CreatorObject != creatorObject)
            {
                Publish(live);
                return WindVolumeError.NotAllowed;
            }
            Volatile.Write(ref m_snapshot, live.Where((_, i) => i != at).ToArray());
            return WindVolumeError.Ok;
        }
    }

    public int RemoveByCreator(UUID creatorObject)
    {
        lock (m_writeLock)
        {
            WindVolume[] live = Live(m_snapshot, m_clockMs());
            WindVolume[] kept = live.Where(v => v.CreatorObject != creatorObject).ToArray();
            Volatile.Write(ref m_snapshot, kept);
            return live.Length - kept.Length;
        }
    }

    public int Clear()
    {
        lock (m_writeLock)
        {
            int n = Live(m_snapshot, m_clockMs()).Length;
            Volatile.Write(ref m_snapshot, None);
            return n;
        }
    }

    public IReadOnlyList<WindVolume> List(UUID creatorObject)
    {
        long now = m_clockMs();
        return Volatile.Read(ref m_snapshot)
            .Where(v => now < v.ExpiresAtMs && (creatorObject.IsZero() || v.CreatorObject == creatorObject))
            .ToArray();
    }

    public Vector3 Sample(Vector3 point) => Sample(point, m_clockMs());

    public Vector3 Sample(Vector3 point, long nowMs)
    {
        WindVolume[] volumes = Volatile.Read(ref m_snapshot);
        if (volumes.Length == 0) return Vector3.Zero;
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)) return Vector3.Zero;

        float x = 0f, y = 0f, z = 0f;
        foreach (WindVolume v in volumes)
        {
            if (nowMs >= v.ExpiresAtMs) continue;
            float w = v.Weight(point);
            if (w <= 0f) continue;
            x += v.Velocity.X * w;
            y += v.Velocity.Y * w;
            z += v.Velocity.Z * w;
        }

        var sum = new Vector3(x, y, z);
        float length = sum.Length();
        float max = Limits.MaxSpeed;
        if (length > max)
            sum = length > 0f && max > 0f ? sum * (max / length) : Vector3.Zero;
        return sum;
    }

    public void PruneExpired()
    {
        lock (m_writeLock)
            Publish(Live(m_snapshot, m_clockMs()));
    }

    // Publishes the live array if it dropped any volume.
    private void Publish(WindVolume[] live)
    {
        if (live.Length != m_snapshot.Length)
            Volatile.Write(ref m_snapshot, live);
    }

    private int Validate(in WindVolumeSpec s)
    {
        if (s.Shape != WindVolumeShape.Sphere && s.Shape != WindVolumeShape.Cylinder)
            return WindVolumeError.InvalidParameter;
        if (!Finite(s.Center) || !Finite(s.Velocity)) return WindVolumeError.InvalidParameter;
        if (!float.IsFinite(s.Radius) || s.Radius <= 0f) return WindVolumeError.InvalidParameter;
        if (s.Shape == WindVolumeShape.Cylinder && (!float.IsFinite(s.Height) || s.Height <= 0f))
            return WindVolumeError.InvalidParameter;
        if (!float.IsFinite(s.FalloffStart) || s.FalloffStart < 0f || s.FalloffStart > 1f)
            return WindVolumeError.InvalidParameter;
        if (!float.IsFinite(s.LifetimeSeconds) || s.LifetimeSeconds <= 0f) return WindVolumeError.InvalidParameter;

        if (s.Radius > Limits.MaxRadius) return WindVolumeError.TooLarge;
        if (s.Shape == WindVolumeShape.Cylinder && s.Height > Limits.MaxHeight) return WindVolumeError.TooLarge;
        if (s.LifetimeSeconds > Limits.MaxLifetimeSeconds) return WindVolumeError.LifetimeTooLong;
        if (s.Velocity.Length() > Limits.MaxSpeed) return WindVolumeError.TooFast;
        return WindVolumeError.Ok;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static WindVolume[] Live(WindVolume[] volumes, long now)
    {
        int expired = 0;
        foreach (WindVolume v in volumes)
            if (now >= v.ExpiresAtMs) expired++;
        return expired == 0 ? volumes : volumes.Where(v => now < v.ExpiresAtMs).ToArray();
    }

    private static int CountBy(WindVolume[] volumes, UUID creatorObject)
    {
        int n = 0;
        foreach (WindVolume v in volumes)
            if (v.CreatorObject == creatorObject) n++;
        return n;
    }

    // Handles count up from 1; if the counter ever wraps, a handle still in use is skipped.
    private int NextId(WindVolume[] live)
    {
        while (true)
        {
            m_lastId = m_lastId == int.MaxValue ? 1 : m_lastId + 1;
            int candidate = m_lastId;
            if (!live.Any(v => v.Id == candidate)) return candidate;
        }
    }
}
