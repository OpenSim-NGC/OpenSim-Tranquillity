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
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

using Microsoft.Extensions.Logging;

namespace OpenSim.Region.CoreModules.World.Objects.BlockedOwners;

/// <summary>
/// Refuses every rez by an owner the region operator has blocked.
/// </summary>
/// <remarks>
/// Every rez path in the region asks Scene.Permissions.CanRezObject (viewer rez from user or prim
/// inventory, a new prim, the object-add and upload capabilities, script rez in every engine, detach to
/// ground, NPC creation), except duplication, which asks Scene.Permissions.CanDuplicateObject. This module
/// answers both. It registers its handlers only while the region blocks someone, so a region that blocks
/// no one runs exactly the code it ran before.
///
/// Owners are blocked from the console (block owner / unblock owner / show blocked owners), or, with
/// [BlockedOwners] BlockEstateBanned = true, by the region's estate ban. Both start empty or off.
/// The console list is kept in memory and is empty again after a restart.
/// </remarks>
public class BlockedOwnerModule : INonSharedRegionModule, IBlockedOwnerModule
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private readonly object m_lock = new();
    private readonly HashSet<UUID> m_blocked = new();
    private Scene m_scene;
    private bool m_blockEstateBanned;
    private bool m_handlersRegistered;

    public string Name => "BlockedOwnerModule";

    public Type ReplaceableInterface => null;

    public void Initialise(IConfigSource source)
    {
        IConfig config = source?.Configs["BlockedOwners"];
        m_blockEstateBanned = config?.GetBoolean("BlockEstateBanned", false) ?? false;
    }

    public void PostInitialise()
    {
    }

    public void Close()
    {
    }

    public void AddRegion(Scene scene)
    {
        m_scene = scene;
        scene.RegisterModuleInterface<IBlockedOwnerModule>(this);
        UpdateHandlers();

        ICommandConsole console = MainConsole.Instance;
        if (console is null)
            return;

        console.Commands.AddCommand(
            "Objects", false, "block owner",
            "block owner <UUID>",
            "Refuse every rez in this region by objects or avatars of this owner",
            "Applies to the console's current region, or to every region when none is selected.\n"
                + "The list is kept in memory and is empty again after a restart.",
            HandleBlockOwner);

        console.Commands.AddCommand(
            "Objects", false, "unblock owner",
            "unblock owner <UUID>",
            "Allow rezzing again by an owner blocked with \"block owner\"",
            HandleUnblockOwner);

        console.Commands.AddCommand(
            "Objects", false, "show blocked owners",
            "show blocked owners",
            "Show the owners whose rezzing this region refuses",
            HandleShowBlockedOwners);
    }

    public void RegionLoaded(Scene scene)
    {
    }

    public void RemoveRegion(Scene scene)
    {
        lock (m_lock)
        {
            if (m_handlersRegistered)
            {
                scene.Permissions.OnRezObject -= CanRezObject;
                scene.Permissions.OnDuplicateObject -= CanDuplicateObject;
                m_handlersRegistered = false;
            }
        }
        scene.UnregisterModuleInterface<IBlockedOwnerModule>(this);
        m_scene = null;
    }

    public bool IsBlocked(UUID owner)
    {
        if (owner.IsZero())
            return false;

        lock (m_lock)
        {
            if (m_blocked.Contains(owner))
                return true;
        }

        // EstateSettings.IsBanned never reports an estate owner or manager as banned.
        return m_blockEstateBanned && m_scene is not null && m_scene.RegionInfo.EstateSettings.IsBanned(owner);
    }

    public bool Block(UUID owner)
    {
        if (owner.IsZero())
            return false;

        bool added;
        lock (m_lock)
            added = m_blocked.Add(owner);

        UpdateHandlers();
        return added;
    }

    public bool Unblock(UUID owner)
    {
        bool removed;
        lock (m_lock)
            removed = m_blocked.Remove(owner);

        UpdateHandlers();
        return removed;
    }

    public UUID[] GetBlockedOwners()
    {
        lock (m_lock)
            return m_blocked.ToArray();
    }

    /// <summary>
    /// Keeps the permission handlers registered exactly while the region can block someone.
    /// </summary>
    private void UpdateHandlers()
    {
        lock (m_lock)
        {
            Scene scene = m_scene;
            if (scene is null)
                return;

            bool wanted = m_blockEstateBanned || m_blocked.Count > 0;
            if (wanted == m_handlersRegistered)
                return;

            if (wanted)
            {
                scene.Permissions.OnRezObject += CanRezObject;
                scene.Permissions.OnDuplicateObject += CanDuplicateObject;
            }
            else
            {
                scene.Permissions.OnRezObject -= CanRezObject;
                scene.Permissions.OnDuplicateObject -= CanDuplicateObject;
            }
            m_handlersRegistered = wanted;
        }
    }

    private bool CanRezObject(int objectCount, UUID owner, Vector3 objectPosition)
    {
        if (!IsBlocked(owner))
            return true;

        m_log.LogInformation(
            "[BLOCKED OWNERS]: Refused a rez of {0} prim(s) by blocked owner {1} in {2}",
            objectCount, owner, m_scene?.Name);
        return false;
    }

    // The copy belongs to the agent who duplicates.
    private bool CanDuplicateObject(SceneObjectGroup sog, ScenePresence sp)
    {
        if (sp is null || !IsBlocked(sp.UUID))
            return true;

        m_log.LogInformation(
            "[BLOCKED OWNERS]: Refused a duplicate of {0} by blocked owner {1} in {2}",
            sog?.UUID, sp.UUID, m_scene?.Name);
        return false;
    }

    private bool IsForThisRegion()
    {
        ICommandConsole console = MainConsole.Instance;
        return console is not null && (console.ConsoleScene is null || console.ConsoleScene == m_scene);
    }

    private void HandleBlockOwner(string module, string[] cmdparams)
    {
        if (!IsForThisRegion())
            return;

        ICommandConsole console = MainConsole.Instance;
        if (cmdparams.Length < 3)
        {
            console.Output("Usage: block owner <UUID>");
            return;
        }

        if (!ConsoleUtil.TryParseConsoleUuid(console, cmdparams[2], out UUID owner))
            return;

        console.Output(Block(owner)
            ? $"Owner {owner} is now blocked from rezzing in {m_scene.Name}."
            : $"Owner {owner} was already blocked in {m_scene.Name}.");
    }

    private void HandleUnblockOwner(string module, string[] cmdparams)
    {
        if (!IsForThisRegion())
            return;

        ICommandConsole console = MainConsole.Instance;
        if (cmdparams.Length < 3)
        {
            console.Output("Usage: unblock owner <UUID>");
            return;
        }

        if (!ConsoleUtil.TryParseConsoleUuid(console, cmdparams[2], out UUID owner))
            return;

        console.Output(Unblock(owner)
            ? $"Owner {owner} may rez again in {m_scene.Name}."
            : $"Owner {owner} was not blocked in {m_scene.Name}.");
    }

    private void HandleShowBlockedOwners(string module, string[] cmdparams)
    {
        if (!IsForThisRegion())
            return;

        UUID[] owners = GetBlockedOwners();
        ICommandConsole console = MainConsole.Instance;
        console.Output($"Blocked owners in {m_scene.Name}: {owners.Length}"
            + (m_blockEstateBanned ? " (owners the estate bans are also blocked)" : ""));
        foreach (UUID owner in owners)
            console.Output($"  {owner}");
    }
}
