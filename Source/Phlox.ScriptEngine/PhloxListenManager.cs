/*
 * Phlox Script Engine Integration
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
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using InWorldz.Phlox.VM;
using InWorldz.Phlox.Types;

using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Framework;

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
        /// <summary>osListenRegex: when set, the name / message filter is a regular expression instead of an exact match.</summary>
        public System.Text.RegularExpressions.Regex NameRegex;
        public System.Text.RegularExpressions.Regex MsgRegex;
        public int RegexBitfield;   // as osListenRegex was given it; 0 for llListen
        public bool Active;
        public bool Removed;        // set under the manager's lock when the listen is removed
    }

    internal class PhloxListenManager
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly PhloxExecutionScheduler m_Scheduler;
        private readonly object m_Lock = new();

        /// <summary>The listen events per second one script may receive with no [InWorldz.Phlox] MaxListenEventsPerSecond.</summary>
        public const int DefaultMaxListenEventsPerSecond = 20;

        /// <summary>
        /// [InWorldz.Phlox] MaxListenEventsPerSecond: listen events past this many in one second are dropped for that
        /// script; 0 or below delivers them all, as Halcyon's and YEngine's WorldCommModule do (SL documents no
        /// per-script listen rate). The default keeps the 20 per second Phlox has always applied.
        /// </summary>
        public int MaxListenEventsPerSecond { get; set; } = DefaultMaxListenEventsPerSecond;

        // Per-script listen delivery count in the current one-second window, and whether that window's drops were logged.
        private readonly Dictionary<UUID, (int Count, long WindowStart, bool Logged)> m_RateTracker = new();

        /// <summary>Listen events dropped by the rate cap, and the log lines that reported them (tests).</summary>
        internal long DroppedListenEvents;
        internal long RateLimitLogLines;

        // All listens, keyed by (itemID, handle) for removal, and by channel for delivery: a line of chat looks only
        // at the listens on its channel instead of every listen in the region.
        private readonly Dictionary<UUID, Dictionary<int, ListenEntry>> m_ByItem = new();
        private readonly Dictionary<int, List<ListenEntry>> m_ByChannel = new();

        /// <summary>
        /// The most listens one script may hold, active or switched off, with no [LL-Functions] max_listens_per_script:
        /// SL's 65, and the core WorldCommModule's default (m_maxhandles = 65). Halcyon's ListenerManager allowed 64.
        /// </summary>
        public const int DefaultMaxListensPerScript = 65;

        /// <summary>The most listens all of this engine's scripts in the region may hold with no [LL-Functions]
        /// max_listens_per_region: WorldCommModule's default (m_maxlisteners = 1000).</summary>
        public const int DefaultMaxListensPerRegion = 1000;

        /// <summary>The per-script cap in force; int.MaxValue when the config asks for no limit.</summary>
        public int MaxListensPerScript { get; }

        /// <summary>The per-region cap in force, never below <see cref="MaxListensPerScript"/>.</summary>
        public int MaxListensPerRegion { get; }

        // Every listen this engine holds in the region, active or switched off, for the per-region cap.
        private int m_ListenCount;

        /// <summary>Tests only: called on the delivering thread with the listening item and channel once a line of chat has
        /// matched a listen and before its event is posted, so a test can hold a delivery that is under way.</summary>
        internal Action<UUID, int> BeforePostForTest;

        /// <summary>Tests only: called with the item when all of its listens are about to be removed (a reset, a state change,
        /// a stop), before the manager's lock is taken, so a test can deliver a line of chat at that point.</summary>
        internal Action<UUID> BeforeRemoveForTest;

        /// <summary>How many listens this engine's scripts hold in the region.</summary>
        public int ListenCount { get { lock (m_Lock) return m_ListenCount; } }

        /// <summary>
        /// [LL-Functions] max_listens_per_region and max_listens_per_script, read as the core WorldCommModule
        /// reads them for YEngine (WorldCommModule.Initialise), so one config value means the same to both engines:
        /// one GetInt each, the region's first, with WorldComm's defaults 1000 and 65; a value that does not parse ends
        /// the read there and keeps the defaults for it and the key after it, as WorldComm's single try does; a value
        /// below 1 means no limit (int.MaxValue); a region cap below the script cap is raised to it. WorldComm swallows
        /// a value that does not parse; here <paramref name="warning"/> says so, for the engine to log once.
        /// </summary>
        public static (int PerScript, int PerRegion) ReadListenCaps(IConfigSource config, out string warning)
        {
            warning = null;
            int perRegion = DefaultMaxListensPerRegion;
            int perScript = DefaultMaxListensPerScript;
            IConfig ll = config?.Configs["LL-Functions"];
            if (ll != null)
            {
                string key = "max_listens_per_region";
                try
                {
                    perRegion = ll.GetInt(key, perRegion);
                    key = "max_listens_per_script";
                    perScript = ll.GetInt(key, perScript);
                }
                catch (Exception e)
                {
                    warning = $"[LL-Functions] {key} = '{ll.GetString(key)}' is not an integer ({e.GetType().Name}); " +
                              $"Phlox keeps max_listens_per_script = {perScript} and max_listens_per_region = {perRegion}, " +
                              "as the core WorldCommModule does";
                }
            }
            if (perRegion < 1) perRegion = int.MaxValue;
            if (perScript < 1) perScript = int.MaxValue;
            if (perRegion < perScript) perRegion = perScript;
            return (perScript, perRegion);
        }

        // Where listeners are, to measure chat range from. Null only in a manager built without a scene.
        private readonly Scene m_Scene;

        // [Chat] whisper_distance / say_distance / shout_distance: the keys and defaults the chat module,
        // WorldComm and YEngine's LSL_Api read, so every listener in a region hears the same range.
        public const int DefaultWhisperDistance = 10;
        public const int DefaultSayDistance = 20;
        public const int DefaultShoutDistance = 100;
        private readonly float m_WhisperDistance;
        private readonly float m_SayDistance;
        private readonly float m_ShoutDistance;

        public PhloxListenManager(PhloxExecutionScheduler scheduler)
            : this(scheduler, null, DefaultWhisperDistance, DefaultSayDistance, DefaultShoutDistance)
        {
        }

        public PhloxListenManager(PhloxExecutionScheduler scheduler, Scene scene,
                                  int whisperDistance, int sayDistance, int shoutDistance)
            : this(scheduler, scene, whisperDistance, sayDistance, shoutDistance,
                   DefaultMaxListensPerScript, DefaultMaxListensPerRegion)
        {
        }

        /// <param name="maxListensPerScript">From <see cref="ReadListenCaps"/>.</param>
        /// <param name="maxListensPerRegion">From <see cref="ReadListenCaps"/>.</param>
        public PhloxListenManager(PhloxExecutionScheduler scheduler, Scene scene,
                                  int whisperDistance, int sayDistance, int shoutDistance,
                                  int maxListensPerScript, int maxListensPerRegion)
        {
            MaxListensPerScript = maxListensPerScript < 1 ? int.MaxValue : maxListensPerScript;
            MaxListenEventsPerSecond = scheduler?.EngineConfig?.GetInt("MaxListenEventsPerSecond", DefaultMaxListenEventsPerSecond)
                                       ?? DefaultMaxListenEventsPerSecond;
            MaxListensPerRegion = Math.Max(maxListensPerRegion < 1 ? int.MaxValue : maxListensPerRegion, MaxListensPerScript);
            m_Scheduler = scheduler;
            m_Scene = scene;
            m_WhisperDistance = whisperDistance;
            m_SayDistance = sayDistance;
            m_ShoutDistance = shoutDistance;
        }

        // ── Called from llListen ───────────────────────────────────────────────

        public int Add(uint localID, UUID itemID, UUID hostID,
                       int channel, string name, UUID key, string msg)
            => Add(localID, itemID, hostID, channel, name, key, msg, 0);

        /// <summary>osListenRegex - bit 1 (OS_LISTEN_REGEX_NAME) makes the name a regex, bit 2 (OS_LISTEN_REGEX_MESSAGE) the message; the caller has validated them.</summary>
        public int Add(uint localID, UUID itemID, UUID hostID,
                       int channel, string name, UUID key, string msg, int regexBitfield)
        {
            name ??= string.Empty;
            msg ??= string.Empty;
            lock (m_Lock)
            {
                m_ByItem.TryGetValue(itemID, out var existing);
                if (existing != null)
                {
                    // Halcyon's AddListener: "called with same filter settings, return same handle" - an active
                    // listen of this script with the same channel, name, key and message; one switched off with
                    // llListenControl is not reused. The filters must be the same, not merely overlapping.
                    foreach (var held in existing.Values)
                        if (held.Active && held.Channel == channel && held.HostID == hostID && held.FilterKey == key
                            && held.RegexBitfield == regexBitfield
                            && string.Equals(held.FilterName, name, StringComparison.Ordinal)
                            && string.Equals(held.FilterMsg, msg, StringComparison.Ordinal))
                            return held.Handle;

                    // Halcyon returns -1 with no script error when a script has no handle left.
                    if (existing.Count >= MaxListensPerScript)
                    {
                        m_log.LogDebug("[PhloxListen]: item {0} already holds {1} listens; llListen returns -1",
                            itemID, existing.Count);
                        return -1;
                    }
                }

                // WorldCommModule.Listen: `if (m_curlisteners < m_maxlisteners)`, after the reuse check; -1 otherwise.
                if (m_ListenCount >= MaxListensPerRegion)
                {
                    m_log.LogDebug("[PhloxListen]: the region already holds {0} Phlox listens; llListen returns -1",
                        m_ListenCount);
                    return -1;
                }

                // Each script numbers its own listens: the lowest handle it does not hold, from 1 (0 is never a
                // handle), as the core WorldCommModule's ListenerManager.GetNewHandle and Halcyon's do.
                int handle = 1;
                if (existing != null)
                    while (existing.ContainsKey(handle)) handle++;

                var entry = new ListenEntry
                {
                    Handle      = handle,
                    LocalID     = localID,
                    ItemID      = itemID,
                    HostID      = hostID,
                    Channel     = channel,
                    FilterName  = name,
                    FilterKey   = key,
                    FilterMsg   = msg,
                    NameRegex   = (regexBitfield & 1) != 0 && !string.IsNullOrEmpty(name) ? ScriptRegex.Create(name) : null,
                    MsgRegex    = (regexBitfield & 2) != 0 && !string.IsNullOrEmpty(msg) ? ScriptRegex.Create(msg) : null,
                    RegexBitfield = regexBitfield,
                    Active      = true
                };

                if (existing == null)
                {
                    existing = new Dictionary<int, ListenEntry>();
                    m_ByItem[itemID] = existing;
                }
                existing[handle] = entry;
                m_ListenCount++;
                IndexAdd(entry);

                m_log.LogDebug("[PhloxListen]: Registered listen handle {0} ch={1} item={2}",
                    handle, channel, itemID);
                return handle;
            }
        }

        /// <summary>
        /// A restored script's saved listen, registered again with the handle the script holds (Halcyon Relisten).
        /// Returns that handle, or -1 when the handle is taken or a cap is full.
        /// </summary>
        public int Restore(uint localID, UUID itemID, UUID hostID, int handle,
                           int channel, string name, UUID key, string msg)
        {
            name ??= string.Empty;
            msg ??= string.Empty;
            if (handle <= 0) return -1;
            lock (m_Lock)
            {
                m_ByItem.TryGetValue(itemID, out var existing);
                if (existing != null && (existing.ContainsKey(handle) || existing.Count >= MaxListensPerScript)) return -1;
                if (m_ListenCount >= MaxListensPerRegion) return -1;
                var entry = new ListenEntry
                {
                    Handle = handle, LocalID = localID, ItemID = itemID, HostID = hostID, Channel = channel,
                    FilterName = name, FilterKey = key, FilterMsg = msg, Active = true
                };
                if (existing == null) m_ByItem[itemID] = existing = new Dictionary<int, ListenEntry>();
                existing[handle] = entry;
                m_ListenCount++;
                IndexAdd(entry);
                return handle;
            }
        }

        private void IndexAdd(ListenEntry entry)
        {
            if (!m_ByChannel.TryGetValue(entry.Channel, out var list))
                m_ByChannel[entry.Channel] = list = new List<ListenEntry>();
            list.Add(entry);
        }

        private void IndexRemove(ListenEntry entry)
        {
            entry.Removed = true;   // a delivery that matched it before now does not post (PostListenEvent)
            if (!m_ByChannel.TryGetValue(entry.Channel, out var list)) return;
            list.Remove(entry);
            if (list.Count == 0) m_ByChannel.Remove(entry.Channel);
        }

        private void RemoveAllOf(UUID itemID)
        {
            if (!m_ByItem.Remove(itemID, out var byHandle)) return;
            m_ListenCount -= byHandle.Count;
            foreach (var entry in byHandle.Values) IndexRemove(entry);
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

        /// <summary>Is this listen registered and active? Null if it is not registered.</summary>
        internal bool? IsActive(UUID itemID, int handle)
        {
            lock (m_Lock)
                return m_ByItem.TryGetValue(itemID, out var byHandle) && byHandle.TryGetValue(handle, out var entry)
                    ? entry.Active : (bool?)null;
        }

        // ── Called from llListenRemove ─────────────────────────────────────────

        /// <summary>Remove a single handle for a script.</summary>
        public void Remove(UUID itemID, int handle)
        {
            lock (m_Lock)
            {
                if (m_ByItem.TryGetValue(itemID, out var byHandle))
                {
                    if (byHandle.Remove(handle, out var entry)) { m_ListenCount--; IndexRemove(entry); }
                    if (byHandle.Count == 0)
                        m_ByItem.Remove(itemID);
                }
            }
        }

        /// <summary>Remove ALL listens for a script: on a reset, a state change, when it stops and when it unloads.</summary>
        public void Remove(UUID itemID)
        {
            BeforeRemoveForTest?.Invoke(itemID);
            lock (m_Lock)
            {
                RemoveAllOf(itemID);
            }
        }

        /// <summary>The script is unloaded - its listens and its listen-rate record go (the record was never freed).</summary>
        public void Forget(UUID itemID)
        {
            lock (m_Lock)
            {
                RemoveAllOf(itemID);
                m_RateTracker.Remove(itemID);
            }
        }

        /// <summary>How many listens this script holds on a channel (tests).</summary>
        internal int ListensOnChannel(UUID itemID, int channel)
        {
            lock (m_Lock)
            {
                if (!m_ByItem.TryGetValue(itemID, out var byHandle)) return 0;
                int n = 0;
                foreach (var entry in byHandle.Values) if (entry.Channel == channel) n++;
                return n;
            }
        }

        /// <summary>Scripts with a listen, and scripts with a rate record (tests and the leak check).</summary>
        internal int ScriptsWithListens { get { lock (m_Lock) return m_ByItem.Count; } }
        internal int RateRecords { get { lock (m_Lock) return m_RateTracker.Count; } }

        // ── Called by PhloxEngine's chat hook ─────────────────────────────────

        /// <summary>
        /// The chat type a Phlox script spoke with, set by the API around its Scene.SimChat call. The scene raises
        /// OnChatFromWorld on the calling thread, so <see cref="DeliverChat(ChatTypeEnum, int, string, UUID, string, Vector3, UUID)"/>
        /// reads it during that delivery; null at any other time.
        /// </summary>
        [ThreadStatic] internal static ChatTypeEnum? SpokenType;

        /// <summary>
        /// Region-wide chat with no target: every matching listen hears it, as llRegionSay does.
        /// </summary>
        public void DeliverChat(int channel, string speakerName, UUID speakerKey, string message)
            => DeliverChat(ChatTypeEnum.Region, channel, speakerName, speakerKey, message, Vector3.Zero, UUID.Zero);

        /// <summary>
        /// Evaluate all registered listens against an incoming chat message, as Halcyon's
        /// WorldCommModule.DeliverMessage does, and dispatch a listen event into each matching script's queue.
        /// <list type="bullet">
        /// <item>A prim never hears its own chat (the listen's prim is the speaker); other prims of the same
        /// object do.</item>
        /// <item>With a <paramref name="destId"/> (llRegionSayTo), only that prim's listens hear it, or, when
        /// it is an avatar, the listens in that avatar's attachments (<see cref="DestIdMatches"/>).</item>
        /// <item>Whisper, say and shout reach listeners closer than the configured [Chat] distance; region
        /// chat and llRegionSayTo reach the whole region. Other chat types reach no listen.</item>
        /// </list>
        /// Distance runs from the speaker to the listening prim; an attachment speaks and listens at its avatar.
        /// </summary>
        public void DeliverChat(ChatTypeEnum type, int channel, string speakerName, UUID speakerKey,
                                string message, Vector3 speakerPosition, UUID destId)
        {
            // Empty chat is delivered, as Halcyon's and YEngine's WorldCommModule.DeliverMessage deliver it: an
            // llSay(ch, "") wake-up reaches a Phlox listen as it reaches a YEngine listen beside it.
            message ??= string.Empty;

            // The chat module rewrites object chat on DEBUG_CHANNEL to DebugChannel before this sees it
            // (ChatModule.DeliverChatToAvatars runs first). A Phlox script's own chat keeps the type it was spoken
            // with (SpokenType); other DebugChannel chat - another engine's run-time errors - goes as far as llSay,
            // SL's distance for errors (wiki, DEBUG_CHANNEL: "Server-generated errors are broadcast the same distance
            // as llSay").
            if (type == ChatTypeEnum.DebugChannel)
                type = SpokenType ?? ChatTypeEnum.Say;

            float range;
            switch (type)
            {
                case ChatTypeEnum.Whisper: range = m_WhisperDistance; break;
                case ChatTypeEnum.Say: range = m_SayDistance; break;
                case ChatTypeEnum.Shout: range = m_ShoutDistance; break;
                case ChatTypeEnum.Region:
                case ChatTypeEnum.Direct: range = float.PositiveInfinity; break;
                default: return;
            }
            bool ranged = !float.IsPositiveInfinity(range);
            if (ranged)
            {
                // Where the speaker is now: an attachment's own position is its attach-point offset, so it
                // speaks from its avatar, and an avatar speaks from where it stands.
                SceneObjectPart speakerPart = m_Scene?.GetSceneObjectPart(speakerKey);
                ScenePresence speakerAvatar;
                if (speakerPart != null)
                    speakerPosition = ChatPosition(speakerPart);
                else if ((speakerAvatar = m_Scene?.GetScenePresence(speakerKey)) != null)
                    speakerPosition = speakerAvatar.AbsolutePosition;
            }
            float rangeSq = range * range;

            // Snapshot under lock so delivery doesn't hold the lock
            List<ListenEntry> candidates;
            lock (m_Lock)
            {
                candidates = m_ByChannel.TryGetValue(channel, out var onChannel)
                    ? new List<ListenEntry>(onChannel) : new List<ListenEntry>();
            }

            foreach (var entry in candidates)
            {
                if (!entry.Active) continue;
                if (entry.Channel != channel) continue;

                // A prim does not hear its own chat.
                if (entry.HostID == speakerKey) continue;

                if (ranged || destId != UUID.Zero)
                {
                    // A listen's host is its prim, or for botListen the bot's avatar.
                    Vector3 hostPosition;
                    SceneObjectPart host = m_Scene?.GetSceneObjectPart(entry.HostID);
                    if (host != null)
                    {
                        if (destId != UUID.Zero && !DestIdMatches(destId, host)) continue;
                        hostPosition = ChatPosition(host);
                    }
                    else
                    {
                        ScenePresence bot = m_Scene?.GetScenePresence(entry.HostID);
                        if (bot == null) continue;
                        if (destId != UUID.Zero && destId != bot.UUID) continue;
                        hostPosition = bot.AbsolutePosition;
                    }
                    if (ranged && Vector3.DistanceSquared(hostPosition, speakerPosition) >= rangeSq) continue;
                }

                // Name filter (empty = wildcard). An exact, case-sensitive match: the SL wiki's llListen page
                // says the speaker's name "must match name exactly (case sensitive)".
                if (entry.NameRegex != null ? !RegexMatches(entry, entry.NameRegex, speakerName)
                    : entry.FilterName.Length > 0 && !string.Equals(entry.FilterName, speakerName, StringComparison.Ordinal))
                    continue;

                // Key filter (UUID.Zero = wildcard)
                if (entry.FilterKey != UUID.Zero && entry.FilterKey != speakerKey)
                    continue;

                // Message filter (empty = wildcard)
                if (entry.MsgRegex != null ? !RegexMatches(entry, entry.MsgRegex, message)
                    : entry.FilterMsg.Length > 0 && !string.Equals(entry.FilterMsg, message, StringComparison.Ordinal))
                    continue;

                // Rate limit: drop if this script has received too many listens this second
                if (IsRateLimited(entry.ItemID)) continue;

                // Match — build and queue the event
                BeforePostForTest?.Invoke(entry.ItemID, entry.Channel);
                PostListenEvent(entry, channel, speakerName, speakerKey, message);
            }
        }

        /// <summary>
        /// Halcyon's WorldCommModule.DestIdMatches: a listen hears an addressed message when its prim is the
        /// destination, or when its prim is an attachment and the destination is the attachment's owner.
        /// </summary>
        private static bool DestIdMatches(UUID destId, SceneObjectPart part)
        {
            if (destId == part.UUID)
                return true;
            if (part.ParentGroup == null || !part.ParentGroup.IsAttachment)
                return false;
            return destId == part.OwnerID;
        }

        /// <summary>Where a prim speaks and listens from: its avatar's position when it is worn.</summary>
        private Vector3 ChatPosition(SceneObjectPart part)
        {
            SceneObjectGroup group = part.ParentGroup;
            if (group != null && group.IsAttachment)
            {
                ScenePresence sp = m_Scene?.GetScenePresence(group.AttachedAvatar);
                if (sp != null)
                    return sp.AbsolutePosition;
            }
            return part.AbsolutePosition;
        }

        /// <summary>
        /// An osListenRegex filter that times out (ScriptRegex.MatchTimeout) does not match,
        /// and delivery to every other listener carries on.
        /// It also switches the listener off, exactly as llListenControl(handle, FALSE)
        /// would, so the pattern cannot cost a timeout on every later line of the channel. The owner
        /// is told once on DEBUG_CHANNEL and the region logs it once; llListenControl(handle, TRUE)
        /// turns it back on.
        /// </summary>
        private bool RegexMatches(ListenEntry entry, System.Text.RegularExpressions.Regex regex, string input)
        {
            try { return regex.IsMatch(input ?? string.Empty); }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                bool disabledNow;
                lock (m_Lock)
                {
                    disabledNow = entry.Active;
                    entry.Active = false;
                }
                if (disabledNow)
                {
                    m_log.LogWarning("[PhloxListen]: {0} for listen handle {1} of item {2}; listener disabled",
                        ScriptRegex.TimedOutMessage, entry.Handle, entry.ItemID);
                    try { m_Scheduler.FindScript(entry.ItemID)?.ShoutError(ListenRegexTimedOutNotice); }
                    catch (Exception e) { m_log.LogWarning("[PhloxListen]: could not tell the owner: {0}", e.Message); }
                }
                return false;
            }
        }

        internal const string ListenRegexTimedOutNotice = "osListenRegex: pattern timed out; listener disabled";

        private bool IsRateLimited(UUID itemID)
        {
            int cap = MaxListenEventsPerSecond;
            if (cap <= 0) return false;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            lock (m_Lock)
            {
                if (m_RateTracker.TryGetValue(itemID, out var entry) && entry.WindowStart == now)
                {
                    if (entry.Count >= cap)
                    {
                        DroppedListenEvents++;
                        if (!entry.Logged)
                        {
                            // One line per script per one-second window, not one per dropped event.
                            RateLimitLogLines++;
                            m_RateTracker[itemID] = (entry.Count, now, true);
                            m_log.LogWarning("[PhloxListen]: script {0} heard more than {1} listen events this second; the rest of this second's are dropped ([InWorldz.Phlox] MaxListenEventsPerSecond)",
                                itemID, cap);
                        }
                        return true;
                    }
                    m_RateTracker[itemID] = (entry.Count + 1, now, entry.Logged);
                }
                else
                {
                    // A new second: the count starts again
                    m_RateTracker[itemID] = (1, now, false);
                }
            }
            return false;
        }

        /// <summary>
        /// A listen botListen registered (its host is the bot's avatar, not a prim) carries the bot's key for
        /// iwDetectedBot, as Halcyon's ExecutionScheduler gave it; a prim's listen has no detect data.
        /// </summary>
        private InWorldz.Phlox.VM.DetectVariables[] BotListenDetect(UUID hostID)
        {
            if (m_Scene?.GetScenePresence(hostID) == null) return null;
            return new[] { new InWorldz.Phlox.VM.DetectVariables { BotID = hostID.ToString() } };
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
                    DetectVars = BotListenDetect(entry.HostID),
                    Args       = new object[]
                    {
                        channel,
                        name    ?? string.Empty,
                        key.ToString(),
                        message ?? string.Empty
                    }
                };

                // Posted under the lock Remove takes, and only while the listen is still registered: once a reset, a
                // state change or llListenRemove has removed it, a delivery that matched it before does not post. So the
                // state change's release returns only after such a post has landed, and its queue clear takes it.
                lock (m_Lock)
                {
                    if (entry.Removed) return;
                    m_Scheduler.PostEvent(entry.ItemID, evt);
                }

                m_log.LogDebug(
                    "[PhloxListen]: Delivered listen ch={0} from '{1}' to item={2}",
                    channel, name, entry.ItemID);
            }
            catch (Exception ex)
            {
                m_log.LogError("[PhloxListen]: Exception delivering listen to {0}: {1}",
                    entry.ItemID, ex.Message);
            }
        }
    }
}
