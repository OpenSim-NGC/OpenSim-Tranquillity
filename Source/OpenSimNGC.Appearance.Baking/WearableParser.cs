using System.Globalization;
using OpenMetaverse;

namespace OpenSimNGC.Appearance.Baking;

/// <summary>A wearable asset's body, parsed: its type, name, stored parameters and textures by slot.</summary>
public sealed record ParsedWearable(WearableKind Kind, string Name, IReadOnlyDictionary<int, float> Params, IReadOnlyDictionary<TextureSlot, UUID> Textures);

/// <summary>
/// Reads the LLWearable text format every bodypart and clothing asset uses:
/// <code>
/// LLWearable version 22
/// Name
/// Description
///     permissions 0 { ... }
///     sale_info 0 { ... }
/// type 4
/// parameters 3
/// 800 1
/// ...
/// textures 1
/// 1 &lt;uuid&gt;
/// </code>
/// Tolerant of the braces blocks and of blank lines; strict about the sections it needs.
/// </summary>
public static class WearableParser
{
    public static ParsedWearable Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("wearable: empty asset");
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (!lines[0].TrimStart().StartsWith("LLWearable", StringComparison.Ordinal)) throw new FormatException("wearable: not an LLWearable asset");
        var name = lines.Length > 1 ? lines[1].Trim() : "";

        WearableKind? kind = null;
        var prms = new Dictionary<int, float>();
        var textures = new Dictionary<TextureSlot, UUID>();
        var i = 2;
        while (i < lines.Length)
        {
            var line = lines[i++].Trim();
            if (line.Length == 0) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0])
            {
                case "type" when parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t):
                    kind = (WearableKind)t;
                    break;
                case "parameters" when parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pc):
                    for (var k = 0; k < pc && i < lines.Length; k++)
                    {
                        var p = lines[i++].Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length < 2) { k--; if (lines[i - 1].Trim().Length == 0) continue; throw new FormatException($"wearable: bad parameter line '{lines[i - 1]}'"); }
                        if (!int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) throw new FormatException($"wearable: bad parameter id '{p[0]}'");
                        if (!float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) throw new FormatException($"wearable: bad parameter value '{p[1]}'");
                        prms[id] = v;
                    }
                    break;
                case "textures" when parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tc):
                    for (var k = 0; k < tc && i < lines.Length; k++)
                    {
                        var p = lines[i++].Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length < 2) { k--; if (lines[i - 1].Trim().Length == 0) continue; throw new FormatException($"wearable: bad texture line '{lines[i - 1]}'"); }
                        if (!int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot)) throw new FormatException($"wearable: bad texture slot '{p[0]}'");
                        if (!UUID.TryParse(p[1], out var id)) throw new FormatException($"wearable: bad texture id '{p[1]}'");
                        textures[(TextureSlot)slot] = id;
                    }
                    break;
            }
        }
        if (kind is null) throw new FormatException("wearable: no 'type' line");
        return new ParsedWearable(kind.Value, name, prms, textures);
    }
}
