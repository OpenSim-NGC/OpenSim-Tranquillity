/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using static OpenMetaverse.Primitive.RenderMaterials;

namespace OpenSim.Region.Framework.Scenes;

/// <summary>
/// One face's glTF material override, held in the compact LLSD form that the viewer reads
/// (LLGLTFMaterial::applyOverrideLLSD) and that MaterialsModule writes when a viewer edits a face:
/// <code>
///   tex : [ uuid x4 ]        texture per slot (undef = not overridden)
///   ti  : [ {o:[x,y], s:[x,y], r:real} x4 ]  KHR_texture_transform per slot
///   bc  : [ r, g, b, a ]     base color factor (linear) and alpha, always together
///   ec  : [ r, g, b ]        emissive factor (linear)
///   mf, rf, ac : real        metallic, roughness, alpha cutoff
///   am  : integer            alpha mode 0 OPAQUE, 1 BLEND, 2 MASK
///   ds  : boolean            double sided
/// </code>
/// Every accessor returns null when that value is not overridden, which is what a script reads back
/// as the empty string. Setting a value to null clears that one override.
/// </summary>
public sealed class GltfMaterialOverride
{
    // LLGLTFMaterial::TextureInfo order
    public const int SLOT_BASE_COLOR = 0;
    public const int SLOT_NORMAL = 1;
    public const int SLOT_METALLIC_ROUGHNESS = 2;
    public const int SLOT_EMISSIVE = 3;
    public const int SLOT_COUNT = 4;

    public const int ALPHA_MODE_OPAQUE = 0;
    public const int ALPHA_MODE_BLEND = 1;
    public const int ALPHA_MODE_MASK = 2;

    /// <summary>
    /// The viewer's GLTF_OVERRIDE_NULL_UUID: an override that sets a texture slot to "no texture", as opposed to a
    /// slot with no override (which is UUID.Zero / undef in the data).
    /// </summary>
    public static readonly UUID NullTextureOverride = new("ffffffff-ffff-ffff-ffff-ffffffffffff");

    private readonly UUID?[] m_texture = new UUID?[SLOT_COUNT];
    private readonly Vector2?[] m_offset = new Vector2?[SLOT_COUNT];
    private readonly Vector2?[] m_scale = new Vector2?[SLOT_COUNT];
    private readonly float?[] m_rotation = new float?[SLOT_COUNT];

    public Vector3? BaseColor;
    public float? BaseAlpha;
    public Vector3? EmissiveColor;
    public float? MetallicFactor;
    public float? RoughnessFactor;
    public float? AlphaCutoff;
    public int? AlphaMode;
    public bool? DoubleSided;

    public UUID? GetTexture(int slot) => m_texture[slot];
    public void SetTexture(int slot, UUID? id) => m_texture[slot] = id.HasValue && id.Value.IsZero() ? null : id;

    public Vector2? GetOffset(int slot) => m_offset[slot];
    public void SetOffset(int slot, Vector2? v) => m_offset[slot] = v;

    public Vector2? GetScale(int slot) => m_scale[slot];
    public void SetScale(int slot, Vector2? v) => m_scale[slot] = v;

    public float? GetRotation(int slot) => m_rotation[slot];
    public void SetRotation(int slot, float? v) => m_rotation[slot] = v;

    public bool HasTransforms
    {
        get
        {
            for (int i = 0; i < SLOT_COUNT; i++)
            {
                if (m_offset[i].HasValue || m_scale[i].HasValue || m_rotation[i].HasValue)
                    return true;
            }
            return false;
        }
    }

    public bool IsEmpty
    {
        get
        {
            if (HasTransforms)
                return false;
            for (int i = 0; i < SLOT_COUNT; i++)
            {
                if (m_texture[i].HasValue)
                    return false;
            }
            return !BaseColor.HasValue && !BaseAlpha.HasValue && !EmissiveColor.HasValue &&
                   !MetallicFactor.HasValue && !RoughnessFactor.HasValue && !AlphaCutoff.HasValue &&
                   !AlphaMode.HasValue && !DoubleSided.HasValue;
        }
    }

    /// <summary>
    /// Drop everything but the texture transforms. SL does this when a face's render material changes, and to any
    /// override set on a face that has no render material: "GLTF texture transforms are always overrides".
    /// </summary>
    public void ClearAllButTransforms()
    {
        Array.Clear(m_texture);
        BaseColor = null;
        BaseAlpha = null;
        EmissiveColor = null;
        MetallicFactor = null;
        RoughnessFactor = null;
        AlphaCutoff = null;
        AlphaMode = null;
        DoubleSided = null;
    }

    #region Serialization

    /// <summary>
    /// Read stored override data. Unreadable data gives an empty override. Data written by earlier Phlox builds
    /// (keys tex/rep/off/rot, ntex/nrep/noff/nrot, mrtex/..., etex/... as plain values) is read into the slots it
    /// meant, so rewriting it repairs it.
    /// </summary>
    public static GltfMaterialOverride FromData(string data)
    {
        GltfMaterialOverride ovr = new();
        if (string.IsNullOrWhiteSpace(data))
            return ovr;

        OSDMap map;
        try
        {
            map = OSDParser.DeserializeLLSDNotation(data) as OSDMap;
        }
        catch
        {
            return ovr;
        }
        if (map is null)
            return ovr;

        if (map.TryGetValue("tex", out OSD tmp) && tmp is OSDArray texArray)
        {
            for (int i = 0; i < SLOT_COUNT && i < texArray.Count; i++)
            {
                if (texArray[i].Type != OSDType.Unknown)
                    ovr.SetTexture(i, texArray[i].AsUUID());
            }
        }

        if (map.TryGetValue("ti", out tmp) && tmp is OSDArray tiArray)
        {
            for (int i = 0; i < SLOT_COUNT && i < tiArray.Count; i++)
            {
                if (tiArray[i] is not OSDMap ti)
                    continue;
                if (ti.TryGetValue("o", out OSD o) && TryVector2(o, out Vector2 ov))
                    ovr.m_offset[i] = ov;
                if (ti.TryGetValue("s", out OSD s) && TryVector2(s, out Vector2 sv))
                    ovr.m_scale[i] = sv;
                if (ti.TryGetValue("r", out OSD r) && IsNumber(r))
                    ovr.m_rotation[i] = (float)r.AsReal();
            }
        }

        if (map.TryGetValue("bc", out tmp) && tmp is OSDArray bc && bc.Count >= 3)
        {
            ovr.BaseColor = new Vector3((float)bc[0].AsReal(), (float)bc[1].AsReal(), (float)bc[2].AsReal());
            ovr.BaseAlpha = bc.Count >= 4 ? (float)bc[3].AsReal() : 1f;
        }
        if (map.TryGetValue("ec", out tmp) && tmp is OSDArray ec && ec.Count >= 3)
            ovr.EmissiveColor = new Vector3((float)ec[0].AsReal(), (float)ec[1].AsReal(), (float)ec[2].AsReal());
        if (map.TryGetValue("mf", out tmp) && IsNumber(tmp))
            ovr.MetallicFactor = (float)tmp.AsReal();
        if (map.TryGetValue("rf", out tmp) && IsNumber(tmp))
            ovr.RoughnessFactor = (float)tmp.AsReal();
        if (map.TryGetValue("ac", out tmp) && IsNumber(tmp))
            ovr.AlphaCutoff = (float)tmp.AsReal();
        if (map.TryGetValue("am", out tmp) && IsNumber(tmp))
            ovr.AlphaMode = tmp.AsInteger();
        if (map.TryGetValue("ds", out tmp) && tmp.Type != OSDType.Unknown)
            ovr.DoubleSided = tmp.AsBoolean();

        ReadLegacyPhloxSlot(map, ovr, SLOT_BASE_COLOR, "tex", "rep", "off", "rot");
        ReadLegacyPhloxSlot(map, ovr, SLOT_NORMAL, "ntex", "nrep", "noff", "nrot");
        ReadLegacyPhloxSlot(map, ovr, SLOT_METALLIC_ROUGHNESS, "mrtex", "mrrep", "mroff", "mrrot");
        ReadLegacyPhloxSlot(map, ovr, SLOT_EMISSIVE, "etex", "erep", "eoff", "erot");

        return ovr;
    }

    private static void ReadLegacyPhloxSlot(OSDMap map, GltfMaterialOverride ovr, int slot,
        string texKey, string repKey, string offKey, string rotKey)
    {
        if (map.TryGetValue(texKey, out OSD tmp) && tmp is not OSDArray && UUID.TryParse(tmp.AsString(), out UUID id))
            ovr.SetTexture(slot, id);
        if (map.TryGetValue(repKey, out tmp) && TryVector2(tmp, out Vector2 rep))
            ovr.m_scale[slot] = rep;
        if (map.TryGetValue(offKey, out tmp) && TryVector2(tmp, out Vector2 off))
            ovr.m_offset[slot] = off;
        if (map.TryGetValue(rotKey, out tmp) && IsNumber(tmp))
            ovr.m_rotation[slot] = (float)tmp.AsReal();
    }

    /// <summary>The stored form, or null when nothing is overridden.</summary>
    public string ToData()
    {
        if (IsEmpty)
            return null;

        OSDMap map = new();

        int lastTex = -1;
        for (int i = 0; i < SLOT_COUNT; i++)
        {
            if (m_texture[i].HasValue)
                lastTex = i;
        }
        if (lastTex >= 0)
        {
            OSDArray tex = new(lastTex + 1);
            for (int i = 0; i <= lastTex; i++)
                tex.Add(m_texture[i].HasValue ? OSD.FromUUID(m_texture[i].Value) : new OSD());
            map["tex"] = tex;
        }

        int lastTi = -1;
        for (int i = 0; i < SLOT_COUNT; i++)
        {
            if (m_offset[i].HasValue || m_scale[i].HasValue || m_rotation[i].HasValue)
                lastTi = i;
        }
        if (lastTi >= 0)
        {
            OSDArray ti = new(lastTi + 1);
            for (int i = 0; i <= lastTi; i++)
            {
                if (!m_offset[i].HasValue && !m_scale[i].HasValue && !m_rotation[i].HasValue)
                {
                    ti.Add(new OSD());
                    continue;
                }
                OSDMap t = new();
                if (m_offset[i].HasValue)
                    t["o"] = new OSDArray { Round(m_offset[i].Value.X, 3), Round(m_offset[i].Value.Y, 3) };
                if (m_scale[i].HasValue)
                    t["s"] = new OSDArray { Round(m_scale[i].Value.X, 3), Round(m_scale[i].Value.Y, 3) };
                if (m_rotation[i].HasValue)
                    t["r"] = Round(m_rotation[i].Value, 6);
                ti.Add(t);
            }
            map["ti"] = ti;
        }

        // the viewer keeps color and alpha in one LLColor4: an override of either overrides both
        if (BaseColor.HasValue || BaseAlpha.HasValue)
        {
            Vector3 c = BaseColor ?? Vector3.One;
            map["bc"] = new OSDArray { Round(c.X, 4), Round(c.Y, 4), Round(c.Z, 4), Round(BaseAlpha ?? 1f, 4) };
        }
        if (EmissiveColor.HasValue)
        {
            Vector3 e = EmissiveColor.Value;
            map["ec"] = new OSDArray { Round(e.X, 4), Round(e.Y, 4), Round(e.Z, 4) };
        }
        if (MetallicFactor.HasValue)
            map["mf"] = Round(MetallicFactor.Value, 3);
        if (RoughnessFactor.HasValue)
            map["rf"] = Round(RoughnessFactor.Value, 3);
        if (AlphaMode.HasValue)
            map["am"] = OSD.FromInteger(AlphaMode.Value);
        if (AlphaCutoff.HasValue)
            map["ac"] = Round(AlphaCutoff.Value, 3);
        if (DoubleSided.HasValue)
            map["ds"] = OSD.FromBoolean(DoubleSided.Value);

        return OSDParser.SerializeLLSDNotation(map);
    }

    private static OSD Round(float v, int digits) => OSD.FromReal(Math.Round((double)v, digits));

    private static bool IsNumber(OSD o) => o.Type == OSDType.Real || o.Type == OSDType.Integer;

    private static bool TryVector2(OSD o, out Vector2 v)
    {
        if (o is OSDArray a && a.Count >= 2 && IsNumber(a[0]) && IsNumber(a[1]))
        {
            v = new Vector2((float)a[0].AsReal(), (float)a[1].AsReal());
            return true;
        }
        v = Vector2.Zero;
        return false;
    }

    #endregion

    #region Prim storage

    /// <summary>The override stored for a face; empty (never null) when the face has none.</summary>
    public static GltfMaterialOverride Get(PrimitiveBaseShape shape, int face)
    {
        RenderMaterialOverrideEntry[] overrides = shape?.RenderMaterials?.overrides;
        if (overrides is not null)
        {
            for (int i = 0; i < overrides.Length; i++)
            {
                if (overrides[i].te_index == face)
                    return FromData(overrides[i].data);
            }
        }
        return new GltfMaterialOverride();
    }

    /// <summary>
    /// Store a face's override, removing the face's entry when nothing is overridden. Returns true when the stored
    /// data changed. RenderMaterials is left in place (empty) once it exists, so viewers are told the face's
    /// overrides went away (SceneObjectPart.ScheduleUpdate drops MaterialOvr when it is null).
    /// </summary>
    public static bool Put(PrimitiveBaseShape shape, int face, GltfMaterialOverride ovr)
    {
        string data = ovr?.ToData();

        if (data is null && shape.RenderMaterials?.overrides is null)
            return false;

        shape.RenderMaterials ??= new Primitive.RenderMaterials();
        RenderMaterialOverrideEntry[] overrides = shape.RenderMaterials.overrides;

        int indx = -1;
        if (overrides is not null)
        {
            for (int i = 0; i < overrides.Length; i++)
            {
                if (overrides[i].te_index == face)
                {
                    indx = i;
                    break;
                }
            }
        }

        if (data is null)
        {
            if (indx < 0)
                return false;
            if (overrides.Length == 1)
            {
                shape.RenderMaterials.overrides = null;
                return true;
            }
            List<RenderMaterialOverrideEntry> rest = new(overrides);
            rest.RemoveAt(indx);
            shape.RenderMaterials.overrides = rest.ToArray();
            return true;
        }

        if (indx >= 0)
        {
            if (data.Equals(overrides[indx].data, StringComparison.Ordinal))
                return false;
            overrides[indx].data = data;
            return true;
        }

        int len = overrides?.Length ?? 0;
        RenderMaterialOverrideEntry[] grown = new RenderMaterialOverrideEntry[len + 1];
        if (len > 0)
            Array.Copy(overrides, grown, len);
        grown[len].te_index = (byte)face;
        grown[len].data = data;
        shape.RenderMaterials.overrides = grown;
        return true;
    }

    /// <summary>The render material on a face, UUID.Zero for none.</summary>
    public static UUID GetRenderMaterial(PrimitiveBaseShape shape, int face)
    {
        RenderMaterialEntry[] entries = shape?.RenderMaterials?.entries;
        if (entries is not null)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].te_index == face)
                    return entries[i].id;
            }
        }
        return UUID.Zero;
    }

    /// <summary>
    /// Change the overrides of one face, or of every face for a negative <paramref name="face"/> (ALL_SIDES).
    /// A face that does not exist changes nothing. On a face with no render material only the texture transforms
    /// are kept, as SL does. Viewers are updated when anything changed; returns whether anything did.
    /// </summary>
    public static bool Edit(SceneObjectPart part, int face, Action<GltfMaterialOverride> edit)
    {
        if (part?.Shape is null || edit is null)
            return false;

        int nsides = part.GetNumberOfSides();
        bool changed = false;
        if (face < 0)
        {
            for (int f = 0; f < nsides; f++)
                changed |= EditFace(part.Shape, f, edit);
        }
        else if (face < nsides)
            changed = EditFace(part.Shape, face, edit);

        if (changed)
            ScheduleOverrideUpdate(part);
        return changed;
    }

    private static bool EditFace(PrimitiveBaseShape shape, int face, Action<GltfMaterialOverride> edit)
    {
        GltfMaterialOverride ovr = Get(shape, face);
        edit(ovr);
        if (GetRenderMaterial(shape, face).IsZero())
            ovr.ClearAllButTransforms();
        return Put(shape, face, ovr);
    }

    /// <summary>
    /// Set the render material of one face, or of every face for a negative <paramref name="face"/> (ALL_SIDES);
    /// UUID.Zero removes it. Like SL (llSetRenderMaterial) this clears the face's overrides except its texture
    /// transforms. A face that does not exist changes nothing. Viewers are updated when anything changed; returns
    /// whether anything did.
    /// </summary>
    public static bool SetRenderMaterial(SceneObjectPart part, int face, UUID material)
    {
        if (part?.Shape is null)
            return false;

        int nsides = part.GetNumberOfSides();
        bool changed = false;
        if (face < 0)
        {
            for (int f = 0; f < nsides; f++)
                changed |= SetRenderMaterialFace(part.Shape, f, material);
        }
        else if (face < nsides)
            changed = SetRenderMaterialFace(part.Shape, face, material);

        if (changed)
            ScheduleOverrideUpdate(part);
        return changed;
    }

    private static bool SetRenderMaterialFace(PrimitiveBaseShape shape, int face, UUID material)
    {
        bool changed = false;
        RenderMaterialEntry[] entries = shape.RenderMaterials?.entries;

        int indx = -1;
        if (entries is not null)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].te_index == face)
                {
                    indx = i;
                    break;
                }
            }
        }

        if (material.IsZero())
        {
            if (indx >= 0)
            {
                if (entries.Length == 1)
                    shape.RenderMaterials.entries = null;
                else
                {
                    List<RenderMaterialEntry> rest = new(entries);
                    rest.RemoveAt(indx);
                    shape.RenderMaterials.entries = rest.ToArray();
                }
                changed = true;
            }
        }
        else if (indx >= 0)
        {
            if (entries[indx].id.NotEqual(material))
            {
                entries[indx].id = material;
                changed = true;
            }
        }
        else
        {
            shape.RenderMaterials ??= new Primitive.RenderMaterials();
            int len = entries?.Length ?? 0;
            RenderMaterialEntry[] grown = new RenderMaterialEntry[len + 1];
            if (len > 0)
                Array.Copy(entries, grown, len);
            grown[len].te_index = (byte)face;
            grown[len].id = material;
            shape.RenderMaterials.entries = grown;
            changed = true;
        }

        GltfMaterialOverride ovr = Get(shape, face);
        ovr.ClearAllButTransforms();
        changed |= Put(shape, face, ovr);
        return changed;
    }

    private static void ScheduleOverrideUpdate(SceneObjectPart part)
    {
        if (part.ParentGroup is not null)
            part.ParentGroup.HasGroupChanged = true;
        part.ScheduleUpdate(PrimUpdateFlags.MaterialOvr | PrimUpdateFlags.FullUpdate);
    }

    #endregion
}
