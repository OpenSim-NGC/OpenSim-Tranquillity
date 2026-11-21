/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;

namespace OpenSim.Region.Framework.Scenes;

/// <summary>
/// Which script engine holds each script item in the region, for what core modules keep by item id on a script's behalf:
/// the prim's script event mask (touch, collision, target), URLs, XML-RPC channels and remote data requests, HTTP requests
/// in flight, taken controls. Two engines can each have an instance for the same item for a while: when a script moves to
/// another engine (a changed engine choice, or a recompile whose first line names another engine), the old engine may let
/// it go later, on its own thread, after the new engine's instance has set those up. An engine that releases them by item
/// does so through <see cref="ReleaseWhileHeld"/>, so it never takes away what another engine's instance holds.
/// <para>An engine calls <see cref="Take"/> when it accepts an item, before its instance sets anything up, and
/// <see cref="Leave"/> when the region removes the item from it. An item no engine has taken is held by none, and every
/// engine may release it, as before.</para>
/// </summary>
public sealed class ScriptItemHolders
{
    private readonly object m_Lock = new();
    private readonly Dictionary<UUID, string> m_Holder = new();

    /// <summary>The engine named (its ScriptEngineName) now holds the item.</summary>
    public void Take(UUID itemID, string engineName)
    {
        lock (m_Lock) m_Holder[itemID] = engineName;
    }

    /// <summary>The engine named lets the item go. Nothing changes when another engine holds it.</summary>
    public void Leave(UUID itemID, string engineName)
    {
        lock (m_Lock)
        {
            if (m_Holder.TryGetValue(itemID, out string holder) && holder == engineName)
                m_Holder.Remove(itemID);
        }
    }

    /// <summary>The engine that holds the item, or null when none does.</summary>
    public string HolderOf(UUID itemID)
    {
        lock (m_Lock) return m_Holder.TryGetValue(itemID, out string holder) ? holder : null;
    }

    /// <summary>
    /// Runs <paramref name="release"/> when the engine named holds the item or no engine does, and returns true; returns false
    /// and runs nothing when another engine holds it. The release runs under the lock <see cref="Take"/> takes, so an engine
    /// that takes the item while it runs sets its own resources up after it. A release must be short and must not wait for
    /// a script engine.
    /// </summary>
    public bool ReleaseWhileHeld(UUID itemID, string engineName, Action release)
    {
        lock (m_Lock)
        {
            if (m_Holder.TryGetValue(itemID, out string holder) && holder != engineName)
                return false;
            release();
            return true;
        }
    }
}
