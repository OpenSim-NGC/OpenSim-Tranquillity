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

using System.Globalization;
using OpenMetaverse;

namespace OpenSim.Framework;

public class AssetLandmark : AssetBase
{
    public Vector3 Position;
    public ulong RegionHandle;
    public UUID RegionID;
    public string Gatekeeper = string.Empty;
    public int Version;

    public AssetLandmark(AssetBase a)
        : base(a.FullID, a.Name, a.Type, a.Metadata.CreatorID)
    {
        Data = a.Data;
        Description = a.Description;
        InternData();
    }

    /// <summary>
    /// Reads the landmark text, one "key value" pair per line:
    /// <code>
    /// Landmark version 2
    /// region_id &lt;uuid&gt;
    /// local_pos &lt;x&gt; &lt;y&gt; &lt;z&gt;
    /// region_handle &lt;handle&gt;
    /// gatekeeper &lt;url&gt;
    /// </code>
    /// Lines are matched by key, not by position or column, so a short line, a missing optional line, extra
    /// spaces or CRLF line endings do not stop the landmark being read. As in the viewer's
    /// LLLandmark::constructFromString (indra/llinventory/lllandmark.cpp), region_id and local_pos are required;
    /// region_handle (written by the simulator) and gatekeeper (written for Hypergrid) are optional.
    /// </summary>
    /// <exception cref="FormatException">The data has no valid region_id or local_pos.</exception>
    private void InternData()
    {
        string temp = Data is null ? string.Empty : Util.UTF8.GetString(Data);
        bool haveRegion = false;
        bool havePosition = false;

        foreach (string rawLine in temp.Split('\n'))
        {
            string line = rawLine.Trim();
            int space = line.IndexOfAny(Separators);
            if (space < 0)
                continue;

            string key = line.Substring(0, space);
            string value = line.Substring(space + 1).Trim();

            switch (key)
            {
                case "Landmark":
                    if (value.StartsWith("version", StringComparison.Ordinal))
                        int.TryParse(value.Substring(7).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out Version);
                    break;

                case "region_id":
                    haveRegion = UUID.TryParse(value, out RegionID) && !RegionID.IsZero();
                    break;

                case "local_pos":
                    string[] scalars = value.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
                    havePosition = scalars.Length >= 3
                        && TryParseScalar(scalars[0], out Position.X)
                        && TryParseScalar(scalars[1], out Position.Y)
                        && TryParseScalar(scalars[2], out Position.Z);
                    break;

                case "region_handle":
                    ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out RegionHandle);
                    break;

                case "gatekeeper":
                    Gatekeeper = value;
                    break;
            }
        }

        if (!haveRegion)
            throw new FormatException("landmark has no valid region_id");
        if (!havePosition)
            throw new FormatException("landmark has no valid local_pos");
    }

    private static readonly char[] Separators = { ' ', '\t' };

    /// <summary>
    /// Landmarks are written with the invariant culture ("10.5"). Landmarks written by older servers in the
    /// server's own culture ("10,5") are still read in that culture.
    /// </summary>
    private static bool TryParseScalar(string s, out float value)
    {
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || float.TryParse(s, out value);
    }
}
