/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace OpenSimNGC.Appearance.Baking;

/// <summary>
/// An image as planes of 8-bit values, row-major, row 0 at the top (SPEC.md section 1). An image without an alpha
/// plane (<see cref="A"/> null) is treated as alpha 255 everywhere.
/// </summary>
public sealed class BakeBitmap
{
    public int Width { get; }
    public int Height { get; }
    public byte[] R { get; }
    public byte[] G { get; }
    public byte[] B { get; }
    /// <summary>The alpha plane, or null when the image has none.</summary>
    public byte[]? A { get; }

    public BakeBitmap(int width, int height, byte[] r, byte[] g, byte[] b, byte[]? a)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("bitmap size must be positive");
        var n = width * height;
        if (r.Length != n || g.Length != n || b.Length != n || (a is not null && a.Length != n))
            throw new ArgumentException("plane length does not match width x height");
        Width = width; Height = height; R = r; G = g; B = b; A = a;
    }
}

/// <summary>One worn wearable as the compositor needs it (SPEC.md section 2, "Inputs", item 3).</summary>
public sealed class BakeWearable
{
    public WearableKind Kind { get; init; }
    /// <summary>Diagnostic only.</summary>
    public string Label { get; init; } = "";
    /// <summary>The stored parameter values by parameter id, in the order they were stored (the order matters for drivers, SPEC.md 4.3).</summary>
    public IReadOnlyDictionary<int, float> Params { get; init; } = new Dictionary<int, float>();
    /// <summary>The texture id per slot. A slot can have an id and no pixels, or no entry at all.</summary>
    public IReadOnlyDictionary<TextureSlot, UUID> TextureIds { get; init; } = new Dictionary<TextureSlot, UUID>();
    /// <summary>The decoded pixels per slot, where there are any.</summary>
    public IReadOnlyDictionary<TextureSlot, BakeBitmap> Textures { get; init; } = new Dictionary<TextureSlot, BakeBitmap>();
}

/// <summary>One line of the per-layer report (SPEC.md section 2, "Outputs"). The detail text is diagnostic only.</summary>
/// <param name="Layer">The layer name.</param>
/// <param name="Status">drawn, skipped, invisible, mask or morph.</param>
/// <param name="Detail">Free text.</param>
/// <param name="Wearable">The wearable type the entry was made for, when it was made for one.</param>
public sealed record BakeLayerReport(string Layer, string Status, string Detail, WearableKind? Wearable);

/// <summary>The result of compositing one channel (SPEC.md section 2, "Outputs").</summary>
public sealed class BakeComposite
{
    public int Size { get; init; }
    public required byte[] R { get; init; }
    public required byte[] G { get; init; }
    public required byte[] B { get; init; }
    public required byte[] A { get; init; }
    /// <summary>The morph mask, Size x Size, 255 where nothing carves it (SPEC.md section 10).</summary>
    public required byte[] MorphMask { get; init; }
    /// <summary>An alpha wearable hid the whole region (SPEC.md 11.1).</summary>
    public bool Invisible { get; init; }
    /// <summary>No layer drew anything (SPEC.md section 13).</summary>
    public bool NothingDrawn { get; init; }
    public required IReadOnlyList<BakeLayerReport> Layers { get; init; }
}
