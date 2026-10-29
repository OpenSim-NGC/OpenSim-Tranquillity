/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSimNGC.Appearance.Baking;

/// <summary>
/// The parameter values of one bake, resolved as SPEC.md section 4 describes: which stored values count (4.1), the
/// merged view with caller-supplied values (4.2), values derived by drivers (4.3), each wearable's own view (4.4),
/// the weight of a parameter (4.5), the sex of the avatar (4.6) and the rule for skipping an alpha parameter (4.7).
/// </summary>
internal sealed class BakeParameterView
{
    /// <summary>An insertion-ordered map of parameter id to value. A later write to an id keeps the id's first position.</summary>
    private sealed class OrderedValues
    {
        public readonly List<int> Order = new();
        public readonly Dictionary<int, float> Map = new();
        public void Set(int id, float v)
        {
            if (!Map.ContainsKey(id)) Order.Add(id);
            Map[id] = v;
        }
    }

    private sealed class Instance
    {
        public WearableKind Kind;
        public readonly OrderedValues Direct = new();
        public readonly Dictionary<int, float> Derived = new();
    }

    private readonly AvatarLad _lad;
    private readonly IReadOnlyList<BakeWearable> _worn;
    private readonly OrderedValues _direct = new();
    private readonly Dictionary<int, float> _derived = new();
    private readonly Dictionary<BakeWearable, Instance> _instances = new(ReferenceEqualityComparer.Instance);
    private readonly bool _male;

    public BakeParameterView(AvatarLad lad, IReadOnlyList<BakeWearable> worn, IReadOnlyDictionary<int, float>? callerValues)
    {
        _lad = lad;
        _worn = worn;

        foreach (var w in worn)
        {
            var inst = new Instance { Kind = w.Kind };
            foreach (var (id, v) in w.Params)
            {
                if (!Accepts(w.Kind, id, out var def)) continue;
                var clamped = Clamp(def, v);
                _direct.Set(id, clamped);
                inst.Direct.Set(id, clamped);
            }
            _instances[w] = inst;
        }
        if (callerValues is not null)
            foreach (var (id, v) in callerValues)
                if (lad.Params.TryGetValue(id, out var def) && !_direct.Map.ContainsKey(id))
                    _direct.Set(id, Clamp(def, v));

        Derive(_direct, _derived);
        foreach (var inst in _instances.Values) Derive(inst.Direct, inst.Derived);

        _male = Weight(80, null) > 0.5f;
    }

    /// <summary>SPEC.md 4.1: a stored value counts only for a parameter the data defines and the wearable's own type owns.</summary>
    private bool Accepts(WearableKind kind, int id, out ParamDef def)
        => _lad.Params.TryGetValue(id, out def!) && OwnedBy(def, kind);

    private static bool OwnedBy(ParamDef def, WearableKind kind)
        => !string.IsNullOrEmpty(def.Wearable) && string.Equals(def.Wearable, WearableKinds.TypeName(kind), StringComparison.OrdinalIgnoreCase);

    private static float Clamp(ParamDef def, float v) => Math.Clamp(v, def.Min, def.Max);

    /// <summary>SPEC.md 4.3: up to three rounds of driver mappings; direct values are never overwritten and the first driver to reach an id wins.</summary>
    private void Derive(OrderedValues direct, Dictionary<int, float> derived)
    {
        var frontier = new List<int>(direct.Order);
        for (var round = 0; round < 3 && frontier.Count > 0; round++)
        {
            var next = new List<int>();
            foreach (var id in frontier)
            {
                var driver = _lad.Params[id];
                if (driver.Driven.Count == 0) continue;
                var v = direct.Map.TryGetValue(id, out var dv) ? dv : derived[id];
                foreach (var d in driver.Driven)
                {
                    if (!_lad.Params.TryGetValue(d.Id, out var driven)) continue;
                    if (direct.Map.ContainsKey(d.Id) || derived.ContainsKey(d.Id)) continue;
                    derived[d.Id] = MapDriven(driver, d, driven, v);
                    next.Add(d.Id);
                }
            }
            frontier = next;
        }
    }

    /// <summary>The trapezoid mapping of SPEC.md 4.3.</summary>
    private static float MapDriven(ParamDef driver, DrivenInfo d, ParamDef driven, float v)
    {
        float lo = driven.Min, hi = driven.Max;
        if (v <= d.Min1) return d.Min1 == d.Max1 && d.Min1 <= driver.Min ? hi : lo;
        if (v <= d.Max1) return lo + (v - d.Min1) / (d.Max1 - d.Min1) * (hi - lo);
        if (v <= d.Max2) return hi;
        if (v <= d.Min2) return hi + (v - d.Max2) / (d.Min2 - d.Max2) * (lo - hi);
        return d.Max2 >= driver.Max ? hi : lo;
    }

    /// <summary>SPEC.md 4.5: the weight of a parameter, for the wearable instance being drawn (null when none is current). An unknown id weighs 0.</summary>
    public float Weight(int id, BakeWearable? instance)
    {
        if (!_lad.Params.TryGetValue(id, out var def)) return 0f;
        if (instance is not null && _instances.TryGetValue(instance, out var inst) && OwnedBy(def, inst.Kind))
        {
            if (inst.Direct.Map.TryGetValue(id, out var own)) return Clamp(def, own);
            if (inst.Derived.TryGetValue(id, out var ownDerived)) return Clamp(def, ownDerived);
        }
        if (_direct.Map.TryGetValue(id, out var dv)) return dv;
        if (_derived.TryGetValue(id, out var dr)) return Clamp(def, dr);
        return def.Default;
    }

    /// <summary>SPEC.md 4.6: the weight when the parameter applies to this avatar's sex, otherwise the parameter's default.</summary>
    public float EffectiveWeight(ParamDef def, BakeWearable? instance)
    {
        var applies = def.Sex == ParamSex.Both || (_male ? def.Sex == ParamSex.Male : def.Sex == ParamSex.Female);
        return applies ? Weight(def.Id, instance) : def.Default;
    }

    /// <summary>True when the avatar is male (parameter 80 above 0.5).</summary>
    public bool IsMale => _male;

    /// <summary>SPEC.md 4.7: an alpha parameter is skipped when it is zero and says to skip, or when its wearable type is not worn.</summary>
    public bool SkipAlphaParam(ParamDef def, BakeWearable? instance)
    {
        if (def.Alpha is { SkipIfZero: true } && MathF.Abs(EffectiveWeight(def, instance)) < 0.000001f) return true;
        if (WearableKinds.FromName(def.Wearable ?? "") is { } kind && !_worn.Any(w => w.Kind == kind)) return true;
        return false;
    }
}
