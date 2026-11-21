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

using OpenMetaverse;

namespace OpenSim.Data;

// This MUST be a ref type!
public class GridUserData
{
    public string UserID;
    public Dictionary<string, string> Data;

    public GridUserData()
    {
        Data = new Dictionary<string, string>();
    }
}

/// <summary>
/// An interface for connecting to the user grid datastore
/// </summary>
public interface IGridUserData
{
    GridUserData Get(string userID);

    /// <summary>
    /// The users whose id starts with <paramref name="userID"/>, compared literally (a <c>%</c> or <c>_</c> in it
    /// matches only itself). A Hypergrid user's id is "uuid;url;name", so the bare uuid finds it. An empty
    /// string returns every user.
    /// </summary>
    GridUserData[] GetAll(string userID);

    /// <summary>
    /// The rows of these users, in any order; a user with no row is left out. The stores answer in a statement per
    /// <see cref="GridUserBatch.Size"/> users; this default, for a store that does not, asks once per user.
    /// </summary>
    GridUserData[] GetMany(string[] userIDs)
    {
        var found = new List<GridUserData>();
        foreach (string id in userIDs ?? [])
        {
            GridUserData d = Get(id);
            if (d != null)
                found.Add(d);
        }
        return found.ToArray();
    }

    /// <summary>
    /// The users marked online whose last region is <paramref name="regionID"/>.
    /// </summary>
    GridUserData[] GetOnlineInRegion(UUID regionID);
    bool Store(GridUserData data);
}

/// <summary>How many keys a store puts in one statement when it looks up a list.</summary>
public static class GridUserBatch
{
    public const int Size = 500;
}
