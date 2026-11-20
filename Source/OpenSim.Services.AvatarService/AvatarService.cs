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

using System.Reflection;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Data;
using OpenSim.Services.Interfaces;
using OpenMetaverse;

using Microsoft.Extensions.Logging;

namespace OpenSim.Services.AvatarService;

public class AvatarService : AvatarServiceBase, IAvatarService
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(
            MethodBase.GetCurrentMethod().DeclaringType);

    public AvatarService(IConfigSource config)
        : base(config)
    {
        m_log.LogDebug("[AVATAR SERVICE]: Starting avatar service");
    }

    /// <summary>Test seam; see <see cref="AvatarServiceBase(IConfigSource, IAvatarData)"/>.</summary>
    protected AvatarService(IConfigSource config, IAvatarData database)
        : base(config, database)
    {
    }

    public AvatarAppearance GetAppearance(UUID principalID)
    {
        AvatarData avatar = GetAvatar(principalID);
        return avatar.ToAvatarAppearance();
    }

    public bool SetAppearance(UUID principalID, AvatarAppearance appearance)
    {
        AvatarData avatar = new AvatarData(appearance);
        return SetAvatar(principalID,avatar);
    }

    public AvatarData GetAvatar(UUID principalID)
    {
        AvatarBaseData[] av = m_Database.Get("PrincipalID", principalID.ToString());
        AvatarData ret = new AvatarData();
        ret.Data = new Dictionary<string,string>();

        if (av.Length == 0)
        {
            ret.AvatarType = 1; // SL avatar
            return ret;
        }

        foreach (AvatarBaseData b in av)
        {
            if (b.Data["Name"] == "AvatarType")
                ret.AvatarType = Convert.ToInt32(b.Data["Value"]);
            else
                ret.Data[b.Data["Name"]] = b.Data["Value"];
        }

        return ret;
    }

    public bool SetAvatar(UUID principalID, AvatarData avatar)
    {
        int count = 0;
        foreach (KeyValuePair<string, string> kvp in avatar.Data)
            if (kvp.Key.StartsWith("_"))
                count++;

//            m_log.LogDebug("[AVATAR SERVICE]: SetAvatar for {0}, attachs={1}", principalID, count);

        // The appearance keys are of variable cardinality (`Wearable i:j`, `_ap_<point>`) and ToAvatarAppearance
        // reads them additively, so a row left behind by a shirt that has been taken off would put the shirt back on
        // at the next read: every appearance row the new appearance does not have is deleted.
        //
        // Another subsystem's rows in the same table are never touched: server-side baking's ADR-004 index (the
        // preserved keys). They used to be read, deleted with everything else and written back after the appearance,
        // which took the 5 s an appearance save can take on SQLite; a bake that wrote the index in that time was
        // overwritten with the old one, and the appearance service then refused the new bake as stale. Stores are
        // upserts on (PrincipalID, Name), so the appearance rows that stay are simply overwritten.
        HashSet<string> keep = new(avatar.Data.Keys, StringComparer.Ordinal) { "AvatarType" };
        foreach (string stale in AppearanceKeys(principalID))
            if (!keep.Contains(stale))
                m_Database.Delete(principalID, stale);

        AvatarBaseData av = new AvatarBaseData();
        av.Data = new Dictionary<string,string>();

        av.PrincipalID = principalID;
        av.Data["Name"] = "AvatarType";
        av.Data["Value"] = avatar.AvatarType.ToString();

        if (!m_Database.Store(av))
        {
            DeleteAppearance(principalID);
            return false;
        }

        foreach (KeyValuePair<string,string> kvp in avatar.Data)
        {
            av.Data["Name"] = kvp.Key;

            // justincc 20110730.  Yes, this is a hack to get around the fact that a bug in OpenSim is causing
            // various simulators on osgrid to inject bad values.  Since these simulators might be around for a
            // long time, we are going to manually police the value.
            //
            // It should be possible to remove this in half a year if we don't want to police values server side.
            if (kvp.Key == "AvatarHeight")
            {
                float height;
                if (!float.TryParse(kvp.Value, out height) || height < 0 || height > 10)
                {
                    string rawHeight = kvp.Value.Replace(",", ".");

                    if (!float.TryParse(rawHeight, out height) || height < 0 || height > 10)
                        height = 1.771488f;

                    m_log.LogDebug(
                        "[AVATAR SERVICE]: Rectifying height of avatar {0} from {1} to {2}",
                        principalID, kvp.Value, height);
                }

                av.Data["Value"] = height.ToString();
            }
            else
            {
                av.Data["Value"] = kvp.Value;
            }

            if (!m_Database.Store(av))
            {
                DeleteAppearance(principalID);
                return false;
            }
        }

        return true;
    }

    /// <summary>The names of the principal's appearance rows: every row but the preserved keys.</summary>
    private List<string> AppearanceKeys(UUID principalID)
    {
        List<string> names = new();
        foreach (AvatarBaseData row in m_Database.Get("PrincipalID", principalID.ToString()))
        {
            if (row.Data is not null && row.Data.TryGetValue("Name", out string name) && !AvatarDataKeys.IsPreserved(name))
                names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// After a failed write, the appearance rows go, so the next read is the default appearance rather than half of
    /// this one. The preserved keys stay: a failed appearance write is no reason to destroy a bake index that still
    /// points at valid assets.
    /// </summary>
    private void DeleteAppearance(UUID principalID)
    {
        foreach (string name in AppearanceKeys(principalID))
            m_Database.Delete(principalID, name);
    }

    public bool ResetAvatar(UUID principalID)
    {
        return m_Database.Delete("PrincipalID", principalID.ToString());
    }

    public bool SetItems(UUID principalID, string[] names, string[] values)
    {
        AvatarBaseData av = new AvatarBaseData();
        av.Data = new Dictionary<string,string>();
        av.PrincipalID = principalID;

        if (names.Length != values.Length)
            return false;

        for (int i = 0 ; i < names.Length ; i++)
        {
            av.Data["Name"] = names[i];
            av.Data["Value"] = values[i];

            if (!m_Database.Store(av))
                return false;
        }

        return true;
    }

    public bool RemoveItems(UUID principalID, string[] names)
    {
        foreach (string name in names)
        {
            m_Database.Delete(principalID, name);
        }
        return true;
    }
}
