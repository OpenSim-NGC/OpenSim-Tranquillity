namespace OpenSimNGC.Appearance.Baking;

/// <summary>
/// The VisualParams block of AgentSetAppearance, built the way a viewer builds it.
///
/// LLAgent::sendAgentSetAppearance sends one byte per parameter whose group is 0 (tweakable) or 3 (transmit,
/// not tweakable), iterating the avatar's parameter map in id order: 253 parameters with the current
/// avatar_lad.xml. Each byte is F32_to_U8(weight, min, max) = (U8)(((clamp(weight) - min) / (max - min)) * 255)
/// in float32, truncated, not rounded. The weight is the avatar's current value: the value stored in the worn
/// wearable of the parameter's type (the topmost when several are worn); for a type that is not worn the viewer
/// keeps whatever the sim last stored for the avatar, so those bytes are carried forward from the sim's blob;
/// only when there is no such blob does the avatar_lad.xml default apply.
/// </summary>
public static class VisualParamEncoder
{
    public sealed record Result(byte[] Bytes, int FromWearables, int Carried, int Defaults, IReadOnlyList<int> Ids);

    /// <summary>The transmitted parameters in send order.</summary>
    public static List<ParamDef> SendList(AvatarLad lad) => lad.Params.Values.Where(p => p.Group is 0 or 3).OrderBy(p => p.Id).ToList();

    /// <summary>Position of a parameter id in the send order, or -1.</summary>
    public static int IndexOf(AvatarLad lad, int id) => SendList(lad).FindIndex(p => p.Id == id);

    /// <summary>llmath.h F32_to_U8: float32 arithmetic, truncation.</summary>
    public static byte F32ToU8(float val, float lower, float upper)
    {
        val = Math.Clamp(val, lower, upper);
        if (upper == lower) return 0;
        float x = ((val - lower) / (upper - lower)) * 255f;
        return (byte)x;
    }

    /// <param name="lad">The parameter definitions.</param>
    /// <param name="worn">every worn wearable in wear order (later of a type is on top) with its stored parameters</param>
    /// <param name="carried">the VisualParams the sim last sent for this avatar (its stored row), or null</param>
    public static Result Encode(AvatarLad lad, IEnumerable<(WearableKind Kind, IReadOnlyDictionary<int, float> Params)> worn, IReadOnlyList<byte>? carried)
    {
        var list = SendList(lad);
        // topmost wearable per type name
        var byType = new Dictionary<string, IReadOnlyDictionary<int, float>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, prms) in worn)
        {
            var name = WearableKinds.TypeName(kind);
            if (name.Length == 0) continue;
            if (byType.TryGetValue(name, out var have))
            {
                var merged = new Dictionary<int, float>(have);
                foreach (var (k, v) in prms) merged[k] = v;
                byType[name] = merged;
            }
            else byType[name] = prms;
        }
        var useCarried = carried is not null && carried.Count == list.Count;
        var bytes = new byte[list.Count];
        int fromWearables = 0, carriedN = 0, defaults = 0;
        for (var i = 0; i < list.Count; i++)
        {
            var p = list[i];
            if (p.Wearable is { Length: > 0 } owner && byType.TryGetValue(owner, out var prms) && prms.TryGetValue(p.Id, out var v))
            {
                bytes[i] = F32ToU8(v, p.Min, p.Max);
                fromWearables++;
            }
            else if (useCarried)
            {
                bytes[i] = carried![i];
                carriedN++;
            }
            else
            {
                bytes[i] = F32ToU8(p.Default, p.Min, p.Max);
                defaults++;
            }
        }
        return new Result(bytes, fromWearables, carriedN, defaults, list.Select(p => p.Id).ToList());
    }
}
