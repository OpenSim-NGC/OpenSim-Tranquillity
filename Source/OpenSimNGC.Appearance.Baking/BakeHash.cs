using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenMetaverse;

namespace OpenSimNGC.Appearance.Baking;

/// <summary>
/// Deterministic input hashing for bake reuse (ADR-004 <c>BakeHash:&lt;channel&gt;</c>).
/// The hash covers only the inputs that can influence the named channel, so a change to, say, a shirt does
/// not invalidate the lower-body bake.
/// </summary>
public static class BakeHash
{
    /// <summary>
    /// SHA-256 (lower-case hex) over: the channel, the bake size, the sorted asset ids of the wearables that feed
    /// the channel's layer set (the types that own one of its texture slots or one of its parameters, plus the
    /// shape, whose `male` gates every layer), the sorted ids of the textures those wearables reference in the
    /// channel's slots, and the values of the parameters the channel's layers read (colour, alpha, global colour
    /// and their drivers) in parameter-id order, taken from <see cref="BakeRequest.VisualParams"/> or, failing
    /// that, from the topmost wearable that stores them. Independent of dictionary and list ordering.
    /// </summary>
    public static string Compute(BakeChannel ch, BakeRequest r) => Compute(ch, r, new TexLayerCompositor());

    public static string Compute(BakeChannel ch, BakeRequest r, TexLayerCompositor compositor)
    {
        ArgumentNullException.ThrowIfNull(r);
        var lad = compositor.Lad;
        var slots = compositor.SlotsOf(ch).ToHashSet();
        var paramIds = compositor.ParamsOf(ch).OrderBy(i => i).ToList();

        var feedingKinds = new HashSet<WearableKind> { WearableKind.Shape };
        foreach (var s in slots) { var k = TexLayerCompositor.WearableOf(s); if (k != WearableKind.Invalid) feedingKinds.Add(k); }
        foreach (var id in paramIds)
            if (lad.Params.TryGetValue(id, out var def) && def.Wearable is { Length: > 0 } owner && WearableKinds.FromName(owner) is { } k) feedingKinds.Add(k);

        var wearableIds = new List<string>();
        var textureIds = new List<string>();
        var stored = new Dictionary<int, float>();   // topmost stored value per parameter among the feeding wearables
        foreach (var w in r.Wearables)
        {
            ParsedWearable? pw = null;
            try { pw = WearableParser.Parse(w.RawText); } catch (FormatException) { }
            var kind = pw?.Kind ?? (WearableKind)w.WearableType;
            if (!feedingKinds.Contains(kind)) continue;
            wearableIds.Add(w.AssetId.ToString());
            if (pw is null) { textureIds.Add($"corrupt:{w.AssetId}"); continue; }
            foreach (var (slot, id) in pw.Textures)
                if (slots.Contains(slot) && id != UUID.Zero) textureIds.Add(id.ToString());
            foreach (var (id, v) in pw.Params) stored[id] = v;
        }
        wearableIds.Sort(StringComparer.Ordinal);
        textureIds = textureIds.Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();

        var sb = new StringBuilder();
        sb.Append("ch=").Append((int)ch).Append('|');
        sb.Append("size=").Append(r.BakeSize).Append('|');
        sb.Append("w=").Append(string.Join(",", wearableIds)).Append('|');
        sb.Append("t=").Append(string.Join(",", textureIds)).Append('|');
        sb.Append("p=");
        foreach (var id in paramIds)
        {
            if (r.VisualParams.TryGetValue(id, out var v) || stored.TryGetValue(id, out v))
                sb.Append(id).Append('=').Append(v.ToString("R", CultureInfo.InvariantCulture)).Append(',');
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
