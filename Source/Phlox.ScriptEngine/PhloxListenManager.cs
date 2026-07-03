/*
 * Legion Grid — Phlox Script Engine Integration
 * PhloxListenManager — implements llListen / llListenControl / llListenRemove
 * and delivers chat events to registered scripts.
 *
 * Design:
 *   - Scripts call llListen → Add() → returns an integer handle
 *   - OpenSim chat events arrive via OnChatFromWorld / OnChatFromClient hooks
 *     registered by PhloxEngine; those call DeliverChat()
 *   - Each match dispatches a "listen" event into the script's event queue
 *     via PhloxExecutionScheduler.PostEvent()
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using log4net;
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using InWorldz.Phlox.VM;
using InWorldz.Phlox.Types;

namespace Phlox.ScriptEngine
{
    /// <summary>
    /// One registered listen. Mirrors the LSL llListen() parameters plus
    /// the script identity needed to deliver events.
    /// </summary>
    internal class ListenEntry
    {
        public int Handle;          // opaque integer returned to the script
        public uint LocalID;        // prim local ID
        public UUID ItemID;         // script item UUID
        public UUID HostID;         // prim UUID (for distance / position checks)
        public int Channel;
        public string FilterName;   // empty string = wildcard
        public UUID FilterKey;      // UUID.Zero = wildcard
        public string FilterMsg;    // empty string = wildcard
        public bool Active;
    }

    internal class PhloxListenManager
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly PhloxExecutionScheduler m_Scheduler;
        private readonly object m_Lock = new();

        // Per-script listen delivery rate limiting: tracks delivery count per second
        // Prevents a script from receiving more than MAX_LISTENS_PER_SECOND listen events
        private const int MAX_LISTENS_PER_SECOND = 20;
        private readonly Dictionary<UUID, (int Count, long WindowStart)> m_RateTracker = new();

        // All active listens, keyed by (itemID, handle) for fast removal
        private readonly Dictionary<UUID, Dictionary<int, ListenEntry>> m_ByItem = new();

        // Next handle value (per-script counter would be cleaner but a global
        // int is fine for thousands of scripts — wraps around after 2^31)
        private int m_NextHandle = 1;

        public PhloxListenManager(PhloxExecutionScheduler scheduler)
        {
            m_Scheduler = scheduler;
        }

        // ── Called from llListen ───────────────────────────────────────────────

        public int Add(uint localID, UUID itemID, UUID hostID,
                       int channel, string name, UUID key, string msg)
        {
            lock (m_Lock)
            {
                int handle = m_NextHandle++;
                if (m_NextHandle <= 0) m_NextHandle = 1; // wrap

                var entry = new ListenEntry
                {
                    Handle      = handle,
                    LocalID     = localID,
                    ItemID      = itemID,
                    HostID      = hostID,
                    Channel     = channel,
                    FilterName  = name  ?? string.Empty,
                    FilterKey   = key,
                    FilterMsg   = msg   ?? string.Empty,
                    Active      = true
                };

                if (!m_ByItem.TryGetValue(itemID, out var byHandle))
                {
                    byHandle = new Dictionary<int, ListenEntry>();
                    m_ByItem[itemID] = byHandle;
                }
                byHandle[handle] = entry;

                m_log.DebugFormat("[PhloxListen]: Registered listen handle {0} ch={1} item={2}",
                    handle, channel, itemID);
                return handle;
            }
        }

        // ── Called from llListenControl ────────────────────────────────────────

        public void SetActive(UUID itemID, int handle, bool active)
        {
            lock (m_Lock)
            {
                if (m_ByItem.TryGetValue(itemID, out var byHandle) &&
                    byHandle.TryGetValue(handle, out var entry))
                {
                    entry.Active = active;
                }
            }
        }

        // ── Called from llListenRemove ─────────────────────────────────────────

        /// <summary>Remove a single handle for a script.</summary>
        public void Remove(UUID itemID, int handle)
        {
            lock (m_Lock)
            {
                if (m_ByItem.TryGetValue(itemID, out var byHandle))
                {
                    byHandle.Remove(handle);
                    if (byHandle.Count == 0)
                        m_ByItem.Remove(itemID);
                }
            }
        }

        /// <summary>Remove ALL listens for a script (called on unload / reset).</summary>
        public void Remove(UUID itemID)
        {
            lock (m_Lock)
            {
                m_ByItem.Remove(itemID);
            }
        }

        // ── Called by PhloxEngine's chat hook ─────────────────────────────────

        /// <summary>
        /// Evaluate all registered listens against an incoming chat message.
        /// Dispatches a listen event into any matching script's queue.
        /// </summary>
        public void DeliverChat(int channel, string speakerName, UUID speakerKey, string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            // Snapshot under lock so delivery doesn't hold the lock
            List<ListenEntry> candidates;
            lock (m_Lock)
            {
                candidates = new List<ListenEntry>();
                foreach (var byHandle in m_ByItem.Values)
                    foreach (var entry in byHandle.Values)
                        candidates.Add(entry);
            }

            foreach (var entry in candidates)
            {
                if (!entry.Active) continue;
                if (entry.Channel != channel) continue;

                // Name filter (empty = wildcard)
                if (entry.FilterName.Length > 0 &&
                    !string.Equals(entry.FilterName, speakerName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Key filter (UUID.Zero = wildcard)
                if (entry.FilterKey != UUID.Zero && entry.FilterKey != speakerKey)
                    continue;

                // Message filter (empty = wildcard)
                if (entry.FilterMsg.Length > 0 &&
                    !string.Equals(entry.FilterMsg, message, StringComparison.Ordinal))
                    continue;

                // Rate limit: drop if this script has received too many listens this second
                if (IsRateLimited(entry.ItemID)) continue;

                // Match — build and queue the event
                PostListenEvent(entry, channel, speakerName, speakerKey, message);
            }
        }

        private bool IsRateLimited(UUID itemID)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            lock (m_Lock)
            {
                if (m_RateTracker.TryGetValue(itemID, out var entry))
                {
                    if (entry.WindowStart == now)
                    {
                        if (entry.Count >= MAX_LISTENS_PER_SECOND)
                        {
                            m_log.WarnFormat("[PhloxListen]: Rate limit hit for script {0} ({1}/s), dropping listen event",
                                itemID, entry.Count);
                            return true;
                        }
                        m_RateTracker[itemID] = (entry.Count + 1, now);
                    }
                    else
                    {
                        // New second — reset counter
                        m_RateTracker[itemID] = (1, now);
                    }
                }
                else
                {
                    m_RateTracker[itemID] = (1, now);
                }
            }
            return false;
        }

        private void PostListenEvent(ListenEntry entry, int channel,
                                     string name, UUID key, string message)
        {
            try
            {
                // Build a PostedEvent for the "listen" handler.
                // The Phlox VM expects: [channel(int), name(str), key(str), message(str)]
                var evt = new InWorldz.Phlox.VM.PostedEvent
                {
                    EventType  = SupportedEventList.Events.LISTEN,
                    DetectVars = null,
                    Args       = new object[]
                    {
                        channel,
                        name    ?? string.Empty,
                        key.ToString(),
                        message ?? string.Empty
                    }
                };

                m_Scheduler.PostEvent(entry.ItemID, evt);

                m_log.DebugFormat(
                    "[PhloxListen]: Delivered listen ch={0} from '{1}' to item={2}",
                    channel, name, entry.ItemID);
            }
            catch (Exception ex)
            {
                m_log.ErrorFormat("[PhloxListen]: Exception delivering listen to {0}: {1}",
                    entry.ItemID, ex.Message);
            }
        }
    }
}
