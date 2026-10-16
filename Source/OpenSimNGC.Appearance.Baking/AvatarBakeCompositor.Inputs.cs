/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace OpenSimNGC.Appearance.Baking;

/// <summary>One worn wearable as the plane-based callers hold it: its type, stored parameters and decoded textures by slot.</summary>
public sealed class WornWearable
{
    public WearableKind Kind;
    public string Label = "";
    public IReadOnlyDictionary<int, float> Params = new Dictionary<int, float>();
    public IReadOnlyDictionary<TextureSlot, UUID> TextureIds = new Dictionary<TextureSlot, UUID>();
    public IReadOnlyDictionary<TextureSlot, RgbaPlanes> Textures = new Dictionary<TextureSlot, RgbaPlanes>();
}

/// <summary>What happened to one layer of one bake: drawn, or skipped and why.</summary>
public sealed record LayerReport(string Layer, string Status, string Detail, WearableKind? Wearable);

/// <summary>One finished composite in the library's plane types.</summary>
public sealed class CompositeResult
{
    public required RgbaPlanes Image;
    public required List<LayerReport> Layers;
    public bool Invisible;
    /// <summary>True when no layer drew anything (see <see cref="BakeComposite.NothingDrawn"/>).</summary>
    public bool NothingDrawn;
    /// <summary>The morph mask plane (see <see cref="BakeComposite.MorphMask"/>).</summary>
    public required byte[] MorphMask;
}

public sealed partial class AvatarBakeCompositor
{
    /// <summary>The wearable type that owns a texture slot, or <see cref="WearableKind.Invalid"/> for a slot no layer set draws.</summary>
    public static WearableKind WearableOf(TextureSlot slot)
    {
        foreach (var entry in SlotTable.Values)
            if (entry.Slot == slot) return entry.Kind;
        return WearableKind.Invalid;
    }

    /// <summary>Every bundled file the channel's non-bump layers, their parameter masks and the layer set's own alpha file name.</summary>
    public IEnumerable<string> ResourceFilesOf(BakeChannel channel)
    {
        if (!_lad.LayerSets.TryGetValue(RegionOf(channel), out var set)) yield break;
        if (set.StaticAlphaFile is not null) yield return set.StaticAlphaFile;
        foreach (var layer in set.Layers)
        {
            if (layer.Bump) continue;
            if (layer.StaticImage is not null) yield return layer.StaticImage;
            foreach (var id in layer.AlphaParams)
                if (_lad.Params.TryGetValue(id, out var def) && !string.IsNullOrEmpty(def.Alpha?.TgaFile))
                    yield return def.Alpha!.TgaFile;
        }
    }

    /// <summary>
    /// The parameter ids that can influence the channel: those its non-bump layers list for colour or alpha, those of
    /// the global colours they name, and any parameter that (through up to three levels of driving) sets one of them.
    /// </summary>
    public IReadOnlyCollection<int> ParamsOf(BakeChannel channel)
    {
        var ids = new HashSet<int>();
        if (!_lad.LayerSets.TryGetValue(RegionOf(channel), out var set)) return ids;
        foreach (var layer in set.Layers)
        {
            if (layer.Bump) continue;
            ids.UnionWith(layer.ColorParams);
            ids.UnionWith(layer.AlphaParams);
            if (!string.IsNullOrEmpty(layer.GlobalColor) && _lad.GlobalColors.TryGetValue(layer.GlobalColor, out var globals))
                ids.UnionWith(globals);
        }
        for (var level = 0; level < 3; level++)
        {
            var added = false;
            foreach (var def in _lad.Params.Values)
                if (!ids.Contains(def.Id) && def.Driven.Any(d => ids.Contains(d.Id))) { ids.Add(def.Id); added = true; }
            if (!added) break;
        }
        return ids;
    }

    /// <summary>Whether the avatar is male, for wearables held as planes.</summary>
    public bool IsMale(IReadOnlyList<WornWearable> worn) => IsMale(ToBake(worn));

    /// <summary>Composites one channel from wearables held as planes; see <see cref="Bake(BakeChannel, IReadOnlyList{BakeWearable}, int, IReadOnlyDictionary{int, float}?)"/>.</summary>
    public CompositeResult Bake(BakeChannel channel, IReadOnlyList<WornWearable> worn, int size, IReadOnlyDictionary<int, float>? callerValues = null)
    {
        var c = Bake(channel, ToBake(worn), size, callerValues);
        return new CompositeResult
        {
            Image = new RgbaPlanes(c.Size, c.Size, c.R, c.G, c.B, c.A, hasAlpha: true),
            Layers = c.Layers.Select(l => new LayerReport(l.Layer, l.Status, l.Detail, l.Wearable)).ToList(),
            Invisible = c.Invisible,
            NothingDrawn = c.NothingDrawn,
            MorphMask = c.MorphMask,
        };
    }

    private static List<BakeWearable> ToBake(IReadOnlyList<WornWearable> worn)
        => worn.Select(w => new BakeWearable
        {
            Kind = w.Kind,
            Label = w.Label,
            Params = w.Params,
            TextureIds = w.TextureIds,
            Textures = w.Textures.ToDictionary(t => t.Key, t => ToBitmap(t.Value)),
        }).ToList();

    /// <summary>The planes of a decoded texture as a bitmap; a texture without alpha has no alpha plane.</summary>
    public static BakeBitmap ToBitmap(RgbaPlanes p) => new(p.W, p.H, p.R, p.G, p.B, p.HasAlpha ? p.A : null);
}
