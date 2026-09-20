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

using System.Text;

using OpenSim.Framework;

using OpenMetaverse;

namespace OpenSim.Services.Interfaces;

public interface IAvatarService
{
    /// <summary>
    /// Called by the login service
    /// </summary>
    /// <param name="userID"></param>
    /// <returns></returns>
    AvatarAppearance GetAppearance(UUID userID);

    /// <summary>
    /// Called by everyone who can change the avatar data (so, regions)
    /// </summary>
    /// <param name="userID"></param>
    /// <param name="appearance"></param>
    /// <returns></returns>
    bool SetAppearance(UUID userID, AvatarAppearance appearance);

    /// <summary>
    /// Called by the login service
    /// </summary>
    /// <param name="userID"></param>
    /// <returns></returns>
    AvatarData GetAvatar(UUID userID);

    /// <summary>
    /// Called by everyone who can change the avatar data (so, regions)
    /// </summary>
    /// <param name="userID"></param>
    /// <param name="avatar"></param>
    /// <returns></returns>
    bool SetAvatar(UUID userID, AvatarData avatar);

    /// <summary>
    /// Not sure if it's needed
    /// </summary>
    /// <param name="userID"></param>
    /// <returns></returns>
    bool ResetAvatar(UUID userID);

    /// <summary>
    /// These methods raison d'etre:
    /// No need to send the entire avatar data (SetAvatar) for changing attachments
    /// </summary>
    /// <param name="userID"></param>
    /// <param name="attach"></param>
    /// <returns></returns>
    bool SetItems(UUID userID, string[] names, string[] values);
    bool RemoveItems(UUID userID, string[] names);
}

/// <summary>
/// Each region/client that uses avatars will have a data structure
/// of this type representing the avatars.
/// </summary>
/// <summary>
/// Names in the avatar service's key/value store that do <b>not</b> belong to the appearance record.
///
/// <para>
/// <see cref="IAvatarService.SetAvatar"/> has to start by deleting every row for the principal, because the
/// appearance keys it writes are of variable cardinality: <c>Wearable i:j</c> and <c>_ap_&lt;point&gt;</c> exist
/// only while something occupies that slot or attach point, and
/// <see cref="AvatarData.ToAvatarAppearance"/> reads them additively
/// (<c>wearables[index].Add(...)</c>, <c>SetAttachment</c>). Without the delete, taking a shirt off would leave
/// its <c>Wearable 4:0</c> row behind and the next read would put the shirt back on. The delete is load-bearing
/// and stays.
/// </para>
///
/// <para>
/// What must not be caught by it is data some other subsystem keeps in the same table. Server-side baking's
/// ADR-004 index does exactly that — <c>Bake:&lt;channel&gt;</c>, <c>BakeHash:&lt;channel&gt;</c>,
/// <c>BakeCOFVersion</c>, <c>BakeSize</c>, <c>BakeUpdated</c> — and before this existed, every appearance save
/// destroyed it (Ledger Q-14). A name listed here is preserved across <c>SetAvatar</c>; everything else is the
/// appearance record and is replaced wholesale, exactly as before.
/// </para>
///
/// <para>
/// No appearance key may start with a preserved prefix. The appearance layer writes <c>Serial</c>,
/// <c>AvatarHeight</c>, <c>VisualParams</c>, <c>Wearable i:j</c> and <c>_ap_N</c>, plus <c>AvatarType</c> and the
/// legacy <c>&lt;Type&gt;Item</c>/<c>&lt;Type&gt;Asset</c> pairs; none of them begins with <c>Bake</c>, and a test
/// pins that.
/// </para>
/// </summary>
public static class AvatarDataKeys
{
    /// <summary>Server-side baking's bake index (ADR-004). Covers <c>Bake:</c>, <c>BakeHash:</c>, <c>BakeCOFVersion</c>, <c>BakeSize</c> and <c>BakeUpdated</c> in one prefix.</summary>
    public const string BakeIndexPrefix = "Bake";

    /// <summary>Every prefix <see cref="IAvatarService.SetAvatar"/> preserves.</summary>
    public static readonly string[] PreservedPrefixes = { BakeIndexPrefix };

    /// <summary>True when the named row belongs to a module rather than to the appearance record.</summary>
    public static bool IsPreserved(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        foreach (string prefix in PreservedPrefixes)
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        return false;
    }
}

public class AvatarData
{
    // This pretty much determines which name/value pairs will be
    // present below. The name/value pair describe a part of
    // the avatar. For SL avatars, these would be "shape", "texture1",
    // etc. For other avatars, they might be "mesh", "skin", etc.
    // The value portion is a URL that is expected to resolve to an
    // asset of the type required by the handler for that field.
    // It is required that regions can access these URLs. Allowing
    // direct access by a viewer is not required, and, if provided,
    // may be read-only. A "naked" UUID can be used to refer to an
    // asset int he current region's asset service, which is not
    // portable, but allows legacy appearance to continue to
    // function. Closed, LL-based  grids will never need URLs here.

    public int AvatarType;
    public Dictionary<string,string> Data;

    public AvatarData()
    {
    }

    public AvatarData(Dictionary<string, object> kvp)
    {
        Data = new Dictionary<string, string>();

        if (kvp.ContainsKey("AvatarType"))
            Int32.TryParse(kvp["AvatarType"].ToString(), out AvatarType);

        foreach (KeyValuePair<string, object> _kvp in kvp)
        {
            if (_kvp.Value != null)
                Data[_kvp.Key] = _kvp.Value.ToString();
        }
    }

    /// <summary>
    /// </summary>
    /// <returns></returns>
    public Dictionary<string, object> ToKeyValuePairs()
    {
        Dictionary<string, object> result = new Dictionary<string, object>();

        result["AvatarType"] = AvatarType.ToString();
        foreach (KeyValuePair<string, string> _kvp in Data)
        {
            if (_kvp.Value != null)
                result[_kvp.Key] = _kvp.Value;
        }
        return result;
    }

    public AvatarData(AvatarAppearance appearance)
    {
        AvatarType = 1; // SL avatars
        Data = new Dictionary<string, string>();

        Data["Serial"] = appearance.Serial.ToString();
        // Wearables
        Data["AvatarHeight"] = appearance.AvatarHeight.ToString();

        // S11: every type the appearance actually has, not the first LEGACY_VERSION_MAX_WEARABLES (15) of them.
        // The old bound stopped at type 14, so Physics (15) and Universal (16) were never written - the record
        // for a live avatar wearing both held no "Wearable 15:*" or "Wearable 16:*" row at all, however
        // faithfully the rest of the stack carried them. Nothing else in the tree is bounded this way: the
        // wearable table is MAX_WEARABLES (17) wide (AvatarWearable.cs:75), the wire negotiates its own count and
        // sends 15 and 16 in "wrbls8" (AvatarAppearance.cs:801-819), the compositor draws both, and the reader
        // below takes any index (S10). This writer was the only floor. It is driven off the array's own length so
        // it is right for a legacy 15-slot appearance too - AvatarAppearance's constructor and ClearWearables
        // still make one of those (AvatarAppearance.cs:320-325) - and for whatever a later type count adds.
        for (int i = 0 ; i < appearance.Wearables.Length ; i++)
        {
            if (appearance.Wearables[i] is null)
                continue;

            for (int j = 0 ; j < appearance.Wearables[i].Count ; j++)
            {
                string fieldName = String.Format("Wearable {0}:{1}", i, j);
                Data[fieldName] = String.Format("{0}:{1}",
                        appearance.Wearables[i][j].ItemID.ToString(),
                        appearance.Wearables[i][j].AssetID.ToString());
            }
        }

        byte[] binary = appearance.VisualParams;
        int len = binary.Length;
        int last = len - 1;

        StringBuilder sb = new StringBuilder(5 * len);
        for (int i = 0; i < len; i++)
        {
            sb.Append(binary[i].ToString());
            if (i < last)
                sb.Append(",");
        }
        Data["VisualParams"] = sb.ToString();

        // Attachments
        List<AvatarAttachment> attachments = appearance.GetAttachments();
        Dictionary<int, List<string>> atts = new Dictionary<int, List<string>>();
        foreach (AvatarAttachment attach in attachments)
        {
            if (!attach.ItemID.IsZero())
            {
                if (!atts.ContainsKey(attach.AttachPoint))
                    atts[attach.AttachPoint] = new List<string>();
                atts[attach.AttachPoint].Add(attach.ItemID.ToString());
            }
        }
        foreach (KeyValuePair<int, List<string>> kvp in atts)
            Data["_ap_" + kvp.Key] = string.Join(",", kvp.Value.ToArray());
    }

    public AvatarAppearance ToAvatarAppearance()
    {
        AvatarAppearance appearance = new AvatarAppearance();

        if (Data.Count == 0)
            return appearance;

        appearance.ClearWearables();
        try
        {
            if (Data.ContainsKey("Serial"))
                appearance.Serial = Int32.Parse(Data["Serial"]);

            if (Data.ContainsKey("AvatarHeight"))
            {
                float h = float.Parse(Data["AvatarHeight"]);
                if( h == 0f)
                    h = 1.9f;
                appearance.SetSize(new Vector3(0.45f, 0.6f, h ));
            }

            // Legacy Wearables
            if (Data.ContainsKey("BodyItem"))
                appearance.Wearables[AvatarWearable.BODY].Wear(
                        UUID.Parse(Data["BodyItem"]),
                        UUID.Parse(Data["BodyAsset"]));

            if (Data.ContainsKey("SkinItem"))
                appearance.Wearables[AvatarWearable.SKIN].Wear(
                        UUID.Parse(Data["SkinItem"]),
                        UUID.Parse(Data["SkinAsset"]));

            if (Data.ContainsKey("HairItem"))
                appearance.Wearables[AvatarWearable.HAIR].Wear(
                        UUID.Parse(Data["HairItem"]),
                        UUID.Parse(Data["HairAsset"]));

            if (Data.ContainsKey("EyesItem"))
                appearance.Wearables[AvatarWearable.EYES].Wear(
                        UUID.Parse(Data["EyesItem"]),
                        UUID.Parse(Data["EyesAsset"]));

            if (Data.ContainsKey("ShirtItem"))
                appearance.Wearables[AvatarWearable.SHIRT].Wear(
                        UUID.Parse(Data["ShirtItem"]),
                        UUID.Parse(Data["ShirtAsset"]));

            if (Data.ContainsKey("PantsItem"))
                appearance.Wearables[AvatarWearable.PANTS].Wear(
                        UUID.Parse(Data["PantsItem"]),
                        UUID.Parse(Data["PantsAsset"]));

            if (Data.ContainsKey("ShoesItem"))
                appearance.Wearables[AvatarWearable.SHOES].Wear(
                        UUID.Parse(Data["ShoesItem"]),
                        UUID.Parse(Data["ShoesAsset"]));

            if (Data.ContainsKey("SocksItem"))
                appearance.Wearables[AvatarWearable.SOCKS].Wear(
                        UUID.Parse(Data["SocksItem"]),
                        UUID.Parse(Data["SocksAsset"]));

            if (Data.ContainsKey("JacketItem"))
                appearance.Wearables[AvatarWearable.JACKET].Wear(
                        UUID.Parse(Data["JacketItem"]),
                        UUID.Parse(Data["JacketAsset"]));

            if (Data.ContainsKey("GlovesItem"))
                appearance.Wearables[AvatarWearable.GLOVES].Wear(
                        UUID.Parse(Data["GlovesItem"]),
                        UUID.Parse(Data["GlovesAsset"]));

            if (Data.ContainsKey("UnderShirtItem"))
                appearance.Wearables[AvatarWearable.UNDERSHIRT].Wear(
                        UUID.Parse(Data["UnderShirtItem"]),
                        UUID.Parse(Data["UnderShirtAsset"]));

            if (Data.ContainsKey("UnderPantsItem"))
                appearance.Wearables[AvatarWearable.UNDERPANTS].Wear(
                        UUID.Parse(Data["UnderPantsItem"]),
                        UUID.Parse(Data["UnderPantsAsset"]));

            if (Data.ContainsKey("SkirtItem"))
                appearance.Wearables[AvatarWearable.SKIRT].Wear(
                        UUID.Parse(Data["SkirtItem"]),
                        UUID.Parse(Data["SkirtAsset"]));

            if (Data.ContainsKey("VisualParams"))
            {
                string[] vps = Data["VisualParams"].Split(new char[] {','});
                byte[] binary = new byte[vps.Length];

                for (int i = 0; i < vps.Length; i++)
                    binary[i] = (byte)Convert.ToInt32(vps[i]);

                appearance.VisualParams = binary;
            }

            AvatarWearable[] wearables = appearance.Wearables;
            int currentLength = wearables.Length;

            // S10: the key is "Wearable <type>:<index>" and BOTH numbers matter. Several wearables of one type
            // are layered in index order, later index on top (LLTexLayerTemplate::render, lltexlayer.cpp:1659-1689),
            // so a record read back in row order would silently reorder them: Data comes from a row store, which
            // owes no order at all. Collect first, then apply by (type, index).
            var wornRows = new List<(int Type, int Index, UUID ItemID, UUID AssetID)>();
            foreach (KeyValuePair<string, string> _kvp in Data)
            {
                // New style wearables
                if (_kvp.Key.StartsWith("Wearable ")  && _kvp.Key.Length > 9)
                {
                    string wearIndex = _kvp.Key.Substring(9);
                    string[] wearIndices = wearIndex.Split(new char[] {':'});
                    int index = Convert.ToInt32(wearIndices[0]);
                    // A record written before the index was carried has one entry per type and no ":<index>";
                    // it reads as index 0, which is what it was.
                    int slot = wearIndices.Length > 1 ? Convert.ToInt32(wearIndices[1]) : 0;

                    string[] ids = _kvp.Value.Split(new char[] {':'});
                    wornRows.Add((index, slot, new UUID(ids[0]), new UUID(ids[1])));
                    continue;
                }
                // Attachments
                if (_kvp.Key.StartsWith("_ap_") && _kvp.Key.Length > 4)
                {
                    string pointStr = _kvp.Key.Substring(4);
                    int point = 0;
                    if (Int32.TryParse(pointStr, out point))
                    {
                        List<string> idList = new List<string>(_kvp.Value.Split(new char[] {','}));

                        appearance.SetAttachment(point, UUID.Zero, UUID.Zero);
                        foreach (string id in idList)
                        {
                            UUID uuid = UUID.Zero;
                            if(UUID.TryParse(id, out uuid))
                                appearance.SetAttachment(point | 0x80, uuid, UUID.Zero);
                        }
                    }
                }
            }

            wornRows.Sort((a, b) => a.Type != b.Type ? a.Type.CompareTo(b.Type) : a.Index.CompareTo(b.Index));
            foreach (var row in wornRows)
            {
                if (row.Type >= currentLength)
                {
                    Array.Resize(ref wearables, row.Type + 1);
                    for (int i = currentLength ; i < wearables.Length ; i++)
                        wearables[i] = new AvatarWearable();
                    currentLength = wearables.Length;
                }
                wearables[row.Type].Add(row.ItemID, row.AssetID);
            }
            appearance.Wearables = wearables;

            if (appearance.Wearables[AvatarWearable.BODY].Count == 0)
                appearance.Wearables[AvatarWearable.BODY].Wear(
                        AvatarWearable.DefaultWearables[
                        AvatarWearable.BODY][0]);

            if (appearance.Wearables[AvatarWearable.SKIN].Count == 0)
                appearance.Wearables[AvatarWearable.SKIN].Wear(
                        AvatarWearable.DefaultWearables[
                        AvatarWearable.SKIN][0]);

            if (appearance.Wearables[AvatarWearable.HAIR].Count == 0)
                appearance.Wearables[AvatarWearable.HAIR].Wear(
                        AvatarWearable.DefaultWearables[
                        AvatarWearable.HAIR][0]);

            if (appearance.Wearables[AvatarWearable.EYES].Count == 0)
                appearance.Wearables[AvatarWearable.EYES].Wear(
                        AvatarWearable.DefaultWearables[
                        AvatarWearable.EYES][0]);

        }
        catch
        {
            // We really should report something here, returning null
            // will at least break the wrapper
            return null;
        }

        return appearance;
    }
}
