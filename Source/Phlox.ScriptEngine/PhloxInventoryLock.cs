/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using OpenSim.Framework;

namespace Phlox.ScriptEngine
{
    /// <summary>
    /// The lock a script call holds while it reads or changes a prim's task inventory. The core's inventory writers
    /// (SceneObjectPartInventory.AddInventoryItem, RemoveInventoryItem, UpdateInventoryItem and the others) change the
    /// dictionary under <see cref="TaskInventoryDictionary.LockItemsForWrite"/>, and its readers
    /// (GetInventoryItem, GetInventoryItems, Clone) take <see cref="TaskInventoryDictionary.LockItemsForRead"/>. A
    /// <c>lock</c> on the dictionary object itself is a different lock that no writer takes, so a script that walked
    /// the dictionary under it could be broken by a change made on another thread ("Collection was modified").
    /// Use as <c>using (InventoryLock.Read(part.TaskInventory)) ...</c>.
    /// <para>
    /// What may be done inside: read fields of the items and of the dictionary, nothing else. The dictionary's
    /// lock is not re-entrant in the usual sense (taking it again on the same thread releases the first hold), so no
    /// call into the core's inventory methods (RemoveInventoryItem, GetInventoryItem, Clone, ...) belongs inside a scope,
    /// and no other lock is taken inside one. Where the thread already holds the read lock, the scope takes nothing
    /// and releases nothing.
    /// </para>
    /// </summary>
    internal static class InventoryLock
    {
        /// <summary>The read lock: for walking or looking up in the dictionary.</summary>
        public static Scope Read(TaskInventoryDictionary inventory) => Take(inventory, write: false);

        /// <summary>The write lock: for changing an item's fields, as the core's own writers of those fields do.</summary>
        public static Scope Write(TaskInventoryDictionary inventory) => Take(inventory, write: true);

        private static Scope Take(TaskInventoryDictionary inventory, bool write)
        {
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));
            try
            {
                if (!write && inventory.IsReadLockedByMe()) return default;   // already protected by a caller up the stack
                if (write) inventory.LockItemsForWrite(true);
                else inventory.LockItemsForRead(true);
                return new Scope(inventory, write);
            }
            catch (Exception e) when (e is ObjectDisposedException || e is NullReferenceException)
            {
                // The part's inventory was disposed (the object was derezzed): nothing can change it any more.
                return default;
            }
        }

        internal readonly struct Scope : IDisposable
        {
            private readonly TaskInventoryDictionary m_inventory;
            private readonly bool m_write;

            internal Scope(TaskInventoryDictionary inventory, bool write)
            {
                m_inventory = inventory;
                m_write = write;
            }

            public void Dispose()
            {
                if (m_inventory == null) return;
                try
                {
                    if (m_write) m_inventory.LockItemsForWrite(false);
                    else m_inventory.LockItemsForRead(false);
                }
                catch (Exception e) when (e is ObjectDisposedException || e is NullReferenceException)
                {
                    // Disposed while held: its lock went with it.
                }
            }
        }
    }
}
