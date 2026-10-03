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

using System.Reflection;
using System.Text;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

using Microsoft.Extensions.Logging;

namespace OpenSim.Region.CoreModules.Avatar.InstantMessage;

/// <summary>
/// Whether the recipient of an instant message has muted (blocked) its sender, so the message transfer
/// modules can drop it before delivery, as the recipient's viewer would.
/// </summary>
/// <remarks>
/// Only a person's message (MessageFromAgent) and an object's message (MessageFromObject, from llInstantMessage)
/// are checked. For an object's message the sender is muted when the recipient muted the object's owner
/// (fromAgentID) or the object itself. The list comes from IMuteListService in the lines MuteListService
/// writes, "type id name|flags". A row with the text chat flag set does not mute text: LL's viewer,
/// llmutelist.h, flagTextChat = 0x1, "If set, don't mute user's text chat". A region with no mute list
/// service mutes nothing.
///
/// On a grid the list is a request to the mute list service on another server, made on the thread that
/// sends the message. Each recipient's list is kept for <see cref="CacheLifetimeMs"/>, so a run of messages to
/// one person asks the service once; a change the recipient makes through this simulator's mute list module
/// drops their entry at once (<see cref="Forget"/>), and a list the module reads to answer the recipient's
/// own viewer fills it (<see cref="Remember"/>). A change made through another simulator shows here when the
/// entry expires.
///
/// The read for a message is given <see cref="ReadTimeoutSeconds"/>. A read that fails or times out lets the
/// message through, is kept for the cache lifetime like a "no list" answer, and logs one warning at most once
/// per <see cref="CacheLifetimeMs"/>, so a dead service costs at most one short wait per recipient per minute.
/// </remarks>
public static class InstantMessageMuteCheck
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private const int MuteFlagTextChat = 0x1;

    /// <summary>How long a recipient's list is kept, in milliseconds.</summary>
    public const long CacheLifetimeMs = 60000;

    /// <summary>How long the read for a message may take, in seconds.</summary>
    public const int ReadTimeoutSeconds = 3;

    private static long s_lastReadWarning = long.MinValue;

    private sealed record CachedList(byte[] Data, long ReadAt);

    // Per recipient, for the whole process: every region in it reads the same grid service.
    private static readonly Dictionary<UUID, CachedList> m_cache = new();

    /// <summary>Drop the cached list of this agent, after they changed it.</summary>
    public static void Forget(UUID agentID)
    {
        lock (m_cache)
            m_cache.Remove(agentID);
    }

    /// <summary>Drop every cached list, and let the next failed read log its warning.</summary>
    public static void ForgetAll()
    {
        lock (m_cache)
        {
            m_cache.Clear();
            s_lastReadWarning = long.MinValue;
        }
    }

    /// <summary>
    /// Keep a list read for another reason (the recipient's viewer asking for it), so the next message to them
    /// needs no read of its own. Only a whole list is kept: data with one byte is the service's "your copy is
    /// current" answer, and null cannot be told apart from a failed read.
    /// </summary>
    public static void Remember(UUID agentID, byte[] data)
    {
        if (data is null || data.Length == 1)
            return;
        lock (m_cache)
            m_cache[agentID] = new CachedList(data, Environment.TickCount64);
    }

    private static byte[] ReadList(IMuteListService mutes, UUID agentID)
    {
        long now = Environment.TickCount64;
        lock (m_cache)
        {
            if (m_cache.TryGetValue(agentID, out CachedList cached) && now - cached.ReadAt < CacheLifetimeMs)
                return cached.Data;
        }

        // A read that fails or times out counts as "no list" and is kept like any other answer, so a dead
        // service is asked at most once per recipient per lifetime.
        byte[] data;
        try
        {
            data = mutes.MuteListRequest(agentID, 0, ReadTimeoutSeconds);
        }
        catch (Exception e)
        {
            data = null;
            bool warn;
            lock (m_cache)
            {
                warn = s_lastReadWarning == long.MinValue || now - s_lastReadWarning >= CacheLifetimeMs;
                if (warn)
                    s_lastReadWarning = now;
            }
            if (warn)
                m_log.LogWarning("[INSTANT MESSAGE]: mute list of {0} could not be read, messages are delivered unchecked: {1}", agentID, e.Message);
        }

        lock (m_cache)
            m_cache[agentID] = new CachedList(data, now);
        return data;
    }

    public static bool IsMutedByRecipient(IEnumerable<Scene> scenes, GridInstantMessage im)
    {
        if (im.dialog != (byte)InstantMessageDialog.MessageFromAgent &&
            im.dialog != (byte)InstantMessageDialog.MessageFromObject)
            return false;

        UUID to = new(im.toAgentID);
        UUID from = new(im.fromAgentID);
        if (to.IsZero() || to.Equals(from))
            return false;

        IMuteListService mutes = null;
        UUID objectID = UUID.Zero;
        bool fromObject = im.dialog == (byte)InstantMessageDialog.MessageFromObject;
        if (fromObject)
            objectID = new UUID(im.imSessionID);   // llInstantMessage puts the sending prim here

        foreach (Scene scene in scenes)
        {
            mutes ??= scene.RequestModuleInterface<IMuteListService>();
            if (fromObject)
            {
                // A viewer mutes an object by its root; the message names the prim the script is in.
                SceneObjectPart part = scene.GetSceneObjectPart(objectID);
                if (part is not null)
                {
                    objectID = part.ParentGroup.UUID;
                    fromObject = false;
                }
            }
        }
        if (mutes is null)
            return false;

        return ListMutesText(ReadList(mutes, to), from, objectID);
    }

    /// <summary>
    /// True when the mute list text has a row for one of the two ids that mutes text chat.
    /// </summary>
    public static bool ListMutesText(byte[] data, UUID senderID, UUID objectID)
    {
        if (data is null || data.Length <= 1)
            return false;

        foreach (string line in Encoding.UTF8.GetString(data).Split('\n'))
        {
            string[] fields = line.Split(' ', 3);
            if (fields.Length < 3 || !UUID.TryParse(fields[1], out UUID id) || id.IsZero())
                continue;
            if (!id.Equals(senderID) && !id.Equals(objectID))
                continue;

            int bar = fields[2].LastIndexOf('|');
            if (bar >= 0 && int.TryParse(fields[2].AsSpan(bar + 1), out int flags) && (flags & MuteFlagTextChat) != 0)
                continue;

            return true;
        }
        return false;
    }
}
