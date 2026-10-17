/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.Framework.Interfaces;

/// <summary>
/// The rule list a script passes to create a wind volume, as <c>[WV_SHAPE, WV_CYLINDER, WV_RADIUS, 30.0, ...]</c>:
/// pairs of a rule number and its value, the way llSetPrimitiveParams reads its list. Both script engines turn
/// their list into plain objects (int, float or Vector3) and call <see cref="TryParse"/>, so they read it alike.
/// </summary>
public static class WindVolumeRules
{
    /// <summary>The value is <see cref="WV_SPHERE"/> or <see cref="WV_CYLINDER"/>. Without it the shape is a sphere.</summary>
    public const int WV_SHAPE = 0;

    /// <summary>A vector, the middle of the volume in region coordinates. Required.</summary>
    public const int WV_CENTER = 1;

    /// <summary>A float, metres. Required.</summary>
    public const int WV_RADIUS = 2;

    /// <summary>A float, metres, the full height of a cylinder. Required for a cylinder, ignored for a sphere.</summary>
    public const int WV_HEIGHT = 3;

    /// <summary>A float from 0 to 1, the fraction of the radius that gets full wind. Without it, 0.5.</summary>
    public const int WV_FALLOFF = 4;

    /// <summary>A vector, the wind in metres per second, all three components. Required.</summary>
    public const int WV_VELOCITY = 5;

    /// <summary>A float, seconds. Required.</summary>
    public const int WV_LIFETIME = 6;

    public const int WV_SPHERE = (int)WindVolumeShape.Sphere;
    public const int WV_CYLINDER = (int)WindVolumeShape.Cylinder;

    public const float DefaultFalloffStart = 0.5f;

    /// <summary>
    /// Reads a rule list. Returns <see cref="WindVolumeError.Ok"/>, or <see cref="WindVolumeError.InvalidParameter"/>
    /// for an odd length, an unknown rule number, a value of the wrong type, or a missing required rule. When a rule
    /// is given twice, the later one counts. Whether the numbers are within the region's caps is for the store to say.
    /// </summary>
    public static int TryParse(IReadOnlyList<object> rules, out WindVolumeSpec spec)
    {
        spec = default;
        if (rules == null || rules.Count % 2 != 0) return WindVolumeError.InvalidParameter;

        int shape = WV_SPHERE;
        Vector3? center = null, velocity = null;
        float? radius = null, height = null, lifetime = null;
        float falloff = DefaultFalloffStart;

        for (int i = 0; i < rules.Count; i += 2)
        {
            if (!TryInt(rules[i], out int rule)) return WindVolumeError.InvalidParameter;
            object value = rules[i + 1];
            switch (rule)
            {
                case WV_SHAPE:
                    if (!TryInt(value, out shape) || (shape != WV_SPHERE && shape != WV_CYLINDER))
                        return WindVolumeError.InvalidParameter;
                    break;
                case WV_CENTER:
                    if (value is not Vector3 c) return WindVolumeError.InvalidParameter;
                    center = c;
                    break;
                case WV_RADIUS:
                    if (!TryFloat(value, out float r)) return WindVolumeError.InvalidParameter;
                    radius = r;
                    break;
                case WV_HEIGHT:
                    if (!TryFloat(value, out float h)) return WindVolumeError.InvalidParameter;
                    height = h;
                    break;
                case WV_FALLOFF:
                    if (!TryFloat(value, out falloff)) return WindVolumeError.InvalidParameter;
                    break;
                case WV_VELOCITY:
                    if (value is not Vector3 v) return WindVolumeError.InvalidParameter;
                    velocity = v;
                    break;
                case WV_LIFETIME:
                    if (!TryFloat(value, out float t)) return WindVolumeError.InvalidParameter;
                    lifetime = t;
                    break;
                default:
                    return WindVolumeError.InvalidParameter;
            }
        }

        if (center == null || radius == null || velocity == null || lifetime == null) return WindVolumeError.InvalidParameter;
        if (shape == WV_CYLINDER && height == null) return WindVolumeError.InvalidParameter;

        spec = new WindVolumeSpec
        {
            Shape = (WindVolumeShape)shape,
            Center = center.Value,
            Radius = radius.Value,
            Height = height ?? 0f,
            FalloffStart = falloff,
            Velocity = velocity.Value,
            LifetimeSeconds = lifetime.Value,
        };
        return WindVolumeError.Ok;
    }

    private static bool TryInt(object o, out int value)
    {
        switch (o)
        {
            case int i: value = i; return true;
            case long l when l >= int.MinValue && l <= int.MaxValue: value = (int)l; return true;
            default: value = 0; return false;
        }
    }

    private static bool TryFloat(object o, out float value)
    {
        switch (o)
        {
            case float f: value = f; return true;
            case double d: value = (float)d; return true;
            case int i: value = i; return true;
            default: value = 0f; return false;
        }
    }
}

/// <summary>Who may change a region's wind or weather from a script.</summary>
public static class WindVolumeAccess
{
    /// <summary>
    /// True when the object's owner is the estate owner, an estate manager or a god. This is the rule iwSetWind
    /// uses, and the one the wind-volume functions and osSetWindParam (with weather on) use.
    /// </summary>
    public static bool MayChangeWind(Scene scene, UUID owner)
    {
        if (scene?.Permissions == null) return false;
        return scene.Permissions.CanIssueEstateCommand(owner, false) || scene.Permissions.IsGod(owner);
    }

    /// <summary>True when the region's wind module holds a weather store that is switched on.</summary>
    public static bool WeatherEnabled(Scene scene)
        => scene?.RequestModuleInterface<IWindModule>()?.WindVolumes?.Enabled == true;
}
