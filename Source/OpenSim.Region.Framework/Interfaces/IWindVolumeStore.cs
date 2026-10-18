/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace OpenSim.Region.Framework.Interfaces;

/// <summary>The shape of a wind volume.</summary>
public enum WindVolumeShape : int
{
    /// <summary>A ball: the radius is measured in three dimensions.</summary>
    Sphere = 0,

    /// <summary>An upright cylinder: the radius is horizontal, the height is the full vertical extent.</summary>
    Cylinder = 1,
}

/// <summary>
/// Results of the wind-volume calls. A create call returns a handle (1 or more) or one of these negative codes;
/// a remove call returns <see cref="Ok"/> or one of them.
/// </summary>
public static class WindVolumeError
{
    public const int Ok = 0;

    /// <summary>Weather is switched off in this region ([Weather] enabled = false).</summary>
    public const int Disabled = -1;

    /// <summary>The caller may not do this (a script whose owner is not an estate owner, estate manager or god).</summary>
    public const int NotAllowed = -2;

    /// <summary>A rule is missing, unknown, of the wrong type, not a finite number or out of its range.</summary>
    public const int InvalidParameter = -3;

    /// <summary>The radius or the height is above the configured maximum.</summary>
    public const int TooLarge = -4;

    /// <summary>The lifetime is above the configured maximum.</summary>
    public const int LifetimeTooLong = -5;

    /// <summary>The wind speed of the volume is above the configured maximum.</summary>
    public const int TooFast = -6;

    /// <summary>The region already holds the maximum number of volumes.</summary>
    public const int RegionFull = -7;

    /// <summary>The creating object already holds the maximum number of volumes.</summary>
    public const int ObjectFull = -8;

    /// <summary>No volume has that handle (or it has expired).</summary>
    public const int NotFound = -9;
}

/// <summary>What a script asks for when it creates a volume.</summary>
public readonly struct WindVolumeSpec
{
    public WindVolumeShape Shape { get; init; }

    /// <summary>The middle of the volume, in region coordinates.</summary>
    public Vector3 Center { get; init; }

    /// <summary>Metres. Horizontal for a cylinder, in all directions for a sphere.</summary>
    public float Radius { get; init; }

    /// <summary>Metres, the full vertical extent of a cylinder, centred on <see cref="Center"/>. Ignored for a sphere.</summary>
    public float Height { get; init; }

    /// <summary>
    /// The fraction of the radius, 0 to 1, inside which the wind is at full strength. From there it falls smoothly
    /// to nothing at the radius. 1 gives an edge with no falloff.
    /// </summary>
    public float FalloffStart { get; init; }

    /// <summary>Seconds from creation. Must be above 0.</summary>
    public float LifetimeSeconds { get; init; }

    /// <summary>The wind inside the volume at full strength, in metres per second. All three components count.</summary>
    public Vector3 Velocity { get; init; }
}

/// <summary>A volume as it is stored. Immutable: a reader that holds one never sees it change.</summary>
public sealed class WindVolume
{
    /// <summary>The fraction of a cylinder's height, at its top and at its bottom, over which the wind falls to nothing.</summary>
    public const float VerticalFalloffFraction = 0.2f;

    public int Id { get; }
    public UUID CreatorObject { get; }
    public UUID Owner { get; }
    public WindVolumeShape Shape { get; }
    public Vector3 Center { get; }
    public float Radius { get; }
    public float Height { get; }
    public float FalloffStart { get; }
    public Vector3 Velocity { get; }

    /// <summary>When the volume was created, in the store's clock (milliseconds).</summary>
    public long CreatedMs { get; }

    /// <summary>The volume is gone at this time and after it (the store's clock, milliseconds).</summary>
    public long ExpiresAtMs { get; }

    public WindVolume(int id, UUID creatorObject, UUID owner, in WindVolumeSpec spec, long createdMs, long expiresAtMs)
    {
        Id = id;
        CreatorObject = creatorObject;
        Owner = owner;
        Shape = spec.Shape;
        Center = spec.Center;
        Radius = spec.Radius;
        Height = spec.Shape == WindVolumeShape.Sphere ? spec.Radius * 2f : spec.Height;
        FalloffStart = spec.FalloffStart;
        Velocity = spec.Velocity;
        CreatedMs = createdMs;
        ExpiresAtMs = expiresAtMs;
    }

    /// <summary>
    /// How much of the volume's wind reaches a point: 1 at full strength, 0 outside, a smooth fall between.
    /// A point exactly on the radius, or on the top or bottom of a cylinder, gets 0.
    /// </summary>
    public float Weight(Vector3 point)
    {
        float dx = point.X - Center.X;
        float dy = point.Y - Center.Y;
        float dz = point.Z - Center.Z;

        if (Shape == WindVolumeShape.Sphere)
            return Falloff(MathF.Sqrt(dx * dx + dy * dy + dz * dz), Radius, FalloffStart);

        float across = Falloff(MathF.Sqrt(dx * dx + dy * dy), Radius, FalloffStart);
        if (across <= 0f) return 0f;

        // Over the top and the bottom band of the cylinder the wind falls to nothing at the end face.
        float half = Height * 0.5f;
        float band = Height * VerticalFalloffFraction;
        float up = Falloff(MathF.Abs(dz), half, (half - band) / half);
        return across * up;
    }

    // 1 up to start x limit, then a smoothstep down to 0 at the limit; 0 at and beyond the limit.
    private static float Falloff(float distance, float limit, float start)
    {
        if (distance >= limit) return 0f;
        float full = start * limit;
        if (distance <= full) return 1f;
        float t = (distance - full) / (limit - full);
        return 1f - t * t * (3f - 2f * t);
    }
}

/// <summary>The caps on what a region's wind volumes may be. All come from the [Weather] section.</summary>
public sealed class WindVolumeLimits
{
    /// <summary>Volumes alive at one time in the region.</summary>
    public int MaxVolumesRegion { get; init; } = 16;

    /// <summary>Volumes alive at one time that one creating object holds.</summary>
    public int MaxVolumesObject { get; init; } = 4;

    /// <summary>Metres, the horizontal radius, and for a sphere the radius.</summary>
    public float MaxRadius { get; init; } = 128f;

    /// <summary>Metres, the vertical extent of a cylinder.</summary>
    public float MaxHeight { get; init; } = 512f;

    /// <summary>Metres per second: the longest velocity a volume may be given, and the clamp on the sum of all volumes at a point.</summary>
    public float MaxSpeed { get; init; } = 50f;

    /// <summary>Seconds.</summary>
    public float MaxLifetimeSeconds { get; init; } = 3600f;
}

/// <summary>
/// A region's wind volumes: places where a script has added wind to the base field. They are held in memory only and
/// are gone when the region restarts. This slice only stores them and answers queries; physics, the wind model's
/// cells and the wind array sent to viewers do not read them yet.
/// </summary>
public interface IWindVolumeStore
{
    /// <summary>False when [Weather] enabled is false: the store then accepts nothing and answers as if empty.</summary>
    bool Enabled { get; }

    WindVolumeLimits Limits { get; }

    /// <summary>The number of volumes that have not expired.</summary>
    int Count { get; }

    /// <summary>
    /// Adds a volume. Returns its handle (1 or more) or a negative <see cref="WindVolumeError"/> code, and stores
    /// nothing on an error.
    /// </summary>
    /// <param name="creatorObject">The object whose script made the call; its volumes are counted and listed together.</param>
    /// <param name="owner">The owner of that object.</param>
    int Create(UUID creatorObject, UUID owner, in WindVolumeSpec spec);

    /// <summary>
    /// Removes a volume. With a creator object, only a volume that object made can be removed
    /// (<see cref="WindVolumeError.NotAllowed"/> otherwise); <see cref="UUID.Zero"/> removes any volume.
    /// </summary>
    int Remove(int handle, UUID creatorObject);

    /// <summary>Removes every volume a creating object made, for instance when the object is deleted. Returns how many.</summary>
    int RemoveByCreator(UUID creatorObject);

    /// <summary>Removes every volume. Returns how many.</summary>
    int Clear();

    /// <summary>The volumes that have not expired; with a creating object, only those it made. Oldest first.</summary>
    IReadOnlyList<WindVolume> List(UUID creatorObject);

    /// <summary>
    /// The wind that all volumes together add at a point, region coordinates, now: each volume's velocity times its
    /// weight there, summed, and the sum clamped to <see cref="WindVolumeLimits.MaxSpeed"/>. Never waits for a writer.
    /// </summary>
    Vector3 Sample(Vector3 point);

    /// <summary>The same at a given time of the store's clock (milliseconds).</summary>
    Vector3 Sample(Vector3 point, long nowMs);

    /// <summary>Drops the volumes that have expired. Sampling and listing already ignore them; this frees them.</summary>
    void PruneExpired();
}
