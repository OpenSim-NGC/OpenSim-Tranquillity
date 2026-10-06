/*
 * Copyright (c) Legion Builds
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
using System.Xml;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Region.Framework.Scenes.Serialization;

/// <summary>
/// Clears the Experience links of scripts in object data that another grid put on this grid.
/// </summary>
/// <remarks>
/// A script runs in the Experience its task item names (TaskInventoryItem.ExperienceID). YEngine also keeps the
/// link in its saved script state and restores it from there when the script starts (the ExperienceKey element
/// of the state), so both are cleared: the item's ExperienceID element is removed and every ExperienceKey is set
/// to the zero UUID.
/// </remarks>
public static class ForeignExperienceLinks
{
    private const string TaskItemLink = "//TaskInventoryItem/ExperienceID";
    private const string StateLink = "//ExperienceKey";

    /// <summary>
    /// Object XML (one object or a coalesced set, with or without saved script states) with every link
    /// cleared. Data with no link, or that is not XML, is returned as it is.
    /// </summary>
    public static byte[] ClearInObjectXml(byte[] data)
    {
        if (data is null || data.Length == 0)
            return data;

        string xml = Encoding.UTF8.GetString(data);
        string cleared = Clear(xml);
        return ReferenceEquals(cleared, xml) ? data : Encoding.UTF8.GetBytes(cleared);
    }

    /// <summary>A script state snapshot (as an object's scripts carry it to another region) with every link cleared.</summary>
    public static string ClearInScriptState(string stateXml)
    {
        if (string.IsNullOrEmpty(stateXml))
            return stateXml;
        return Clear(stateXml);
    }

    /// <summary>Clear the link of every script item in the object's prims.</summary>
    public static void ClearInObject(SceneObjectGroup sog)
    {
        foreach (SceneObjectPart part in sog.Parts)
        {
            foreach (TaskInventoryItem item in part.Inventory.GetInventoryItems())
                item.ExperienceID = UUID.Zero;
        }
    }

    private static string Clear(string xml)
    {
        if (xml.IndexOf("ExperienceID", StringComparison.Ordinal) < 0 &&
            xml.IndexOf("ExperienceKey", StringComparison.Ordinal) < 0)
            return xml;

        XmlDocument doc = new XmlDocument { XmlResolver = null };
        try
        {
            doc.LoadXml(xml);
        }
        catch (XmlException)
        {
            return xml;
        }

        bool changed = false;
        foreach (XmlNode link in doc.SelectNodes(TaskItemLink))
        {
            link.ParentNode.RemoveChild(link);
            changed = true;
        }
        string zero = UUID.Zero.ToString();
        foreach (XmlNode key in doc.SelectNodes(StateLink))
        {
            if (key.InnerText != zero)
            {
                key.InnerText = zero;
                changed = true;
            }
        }
        return changed ? doc.OuterXml : xml;
    }
}
