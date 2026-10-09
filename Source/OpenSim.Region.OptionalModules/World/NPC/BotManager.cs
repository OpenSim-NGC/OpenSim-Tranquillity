/*
 * Copyright (c) Legion Builds
 * BotManager.cs — Bot/NPC management module for Phlox script engine
 * Wraps OpenSim's INPCModule with additional tracking for tags, profiles,
 * outfits, navigation, speed, and event registration.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Services.Interfaces;
using System.IO;

using Microsoft.Extensions.Logging;

namespace OpenSim.Region.OptionalModules.World.NPC
{
    /// <summary>
    /// Per-bot tracking data beyond what INPCModule stores.
    /// </summary>
    internal class BotData
    {
        public UUID BotID;
        public UUID OwnerID;
        public UUID ScriptItemID;
        public Scene BotScene;  // scene this bot was created in
        public HashSet<string> Tags = new HashSet<string>();
        public float SpeedMultiplier = 1.0f;
        public string AboutText = string.Empty;
        public string Email = string.Empty;
        public UUID ImageID = UUID.Zero;
        public string ProfileURL = string.Empty;

        // Navigation state
        public bool MovementPaused;
        public List<Vector3> NavPoints;
        public List<TravelMode> NavModes;
        public int NavIndex;
        public Dictionary<int, object> NavOptions;
        public bool NavFollowIndefinitely;      // BOT_MOVEMENT_TYPE BOT_MOVEMENT_FLAG_FOLLOW_INDEFINITELY: start again after the last point
        public long NavTeleportAfterMs = BotManager.DEFAULT_TELEPORT_AFTER_MS;  // BOT_MOVEMENT_TELEPORT_AFTER
        public bool IsFollowing;
        public UUID FollowTarget;
        public Dictionary<int, object> FollowOptions;
        public bool FollowLost;     // BOT_MOVE_AVATAR_LOST was raised and the avatar has not come back since
        // botFollowAvatar's options, as Halcyon's AvatarFollowerDescription read them, and the follower's state.
        public bool FollowAllowRunning, FollowAllowFlying, FollowAllowJumping, FollowNeedsSight;
        public Vector3 FollowOffset;
        public float FollowStartDistance, FollowStopDistance, FollowLostDistance;
        public bool FollowAtAvatar;         // the bot stopped beside the avatar (Halcyon m_toAvatar)
        public int FollowJumpAttempts;      // Halcyon NumberOfTimesJumpAttempted
        // Wandering runs as a navigation path of one point and, when WanderWait is not 0, a wait after it; at the
        // path's end a new point is picked (Halcyon WanderingAction).
        public bool IsWandering;
        public Vector3 WanderOrigin;
        public Vector3 WanderDistances;
        public Dictionary<int, object> WanderOptions;
        public TravelMode WanderMode = TravelMode.Walk;
        public float WanderWait;

        // Event registration: every script item registered for bot_update (lock the list to use it)
        public List<UUID> PathEventScripts = new List<UUID>();
        public SceneObjectGroup CollisionEventHost;

        // Navigation arrival tracking (poll-driven). INPCModule.MoveToTarget is
        // fire-and-forget with no arrival callback, so we record the waypoint a bot
        // is currently walking to and poll its position to detect arrival.
        public Vector3 CurrentNavTarget;
        public bool NavInFlight;
        public bool NavNoFly, NavRunning;   // how the bot was sent to CurrentNavTarget, to send it again on resume
        // Time spent moving toward CurrentNavTarget while not paused, added up at each nav poll since NavLastPoll
        // (Environment.TickCount64). It reaching NavTeleportAfterMs teleports the bot to the point.
        public long NavElapsedMs;
        public long NavLastPoll;

        // The bot stands still until NavWaitUntil (Environment.TickCount64), then the nav poll moves it on: at a
        // BOT_TRAVELMODE_WAIT point (NavUpdateAfterWait, reported as a move on to the next node), or for one poll
        // before a path starts again.
        public bool NavWaiting;
        public long NavWaitUntil;
        public bool NavUpdateAfterWait;

        // Collision-event bridge: the bot's physics actor we subscribed to, and the handler we
        // attached, so we can detach exactly that subscription on deregister/removal.
        public PhysicsActor CollisionPhysActor;
        public PhysicsActor.CollisionUpdate CollisionHandler;

        // Per-bot collision phase state so the module-contained delivery reproduces core's
        // collision_start-once / collision-repeating / collision_end-once (and the land equivalents).
        // Maps each current collider's localID -> UUID, remembered while the collider is still
        // resolvable, so a collider that is DELETED while in contact can still be reported in
        // collision_end via its remembered UUID (mirrors Halcyon TryExtractCollider's fallback).
        public Dictionary<uint, UUID> CollisionLast = new Dictionary<uint, UUID>();
        public bool LandCollideLast;
    }

    // No Mono.Addins [Extension] attribute: develop discovers region modules by
    // interface reflection (IPluginDiscovery scans for ISharedRegionModule implementers),
    // same as NPCModule.
    public class BotManager : IBotManager, ISharedRegionModule
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly Dictionary<UUID, BotData> m_bots = new Dictionary<UUID, BotData>();
        private readonly Dictionary<string, Dictionary<UUID, AvatarAppearance>> m_savedOutfits
            = new Dictionary<string, Dictionary<UUID, AvatarAppearance>>();
        // m_savedOutfits[ownerID.ToString()][outfitNameHash] = appearance

        // Outfit name reverse map: outfitKey -> outfitName (for listing)
        private readonly Dictionary<string, Dictionary<UUID, string>> m_outfitNames
            = new Dictionary<string, Dictionary<UUID, string>>();

        private readonly List<Scene> m_scenes = new List<Scene>();
        private INPCModule m_npcModule;
        private bool m_enabled;
        private IConfigSource m_config;
        private BotPersistenceManager m_persistence;

        // Navigation arrival poll — supplies the "arrived" trigger INPCModule lacks, so
        // waypoints advance and bot_update (BOT_MOVE_COMPLETE) fires when a path finishes.
        private System.Timers.Timer m_navPollTimer;
        private const double NAV_POLL_INTERVAL_MS = 500.0;
        private const float NAV_ARRIVAL_TOLERANCE = 1.5f;    // metres (horizontal)
        // A bot that has not reached a point after this long is teleported to it: Halcyon MovementDescription's
        // TimeBeforeTeleportToNextPositionOccurs, 60 s unless BOT_MOVEMENT_TELEPORT_AFTER sets it.
        internal const long DEFAULT_TELEPORT_AFTER_MS = 60_000;

        // Collision/land_collision script-event mask — only bridge a bot's collisions to a host whose
        // scripts actually subscribed to one of these, so we don't do work or emit sounds for nobody.
        private const scriptEvents COLLISION_EVENT_MASK =
            scriptEvents.collision_start | scriptEvents.collision | scriptEvents.collision_end |
            scriptEvents.land_collision_start | scriptEvents.land_collision | scriptEvents.land_collision_end;

        /// <summary>
        /// Public accessor for script API to reach persistence manager.
        /// </summary>
        public BotPersistenceManager PersistenceManager => m_persistence;

        #region ISharedRegionModule

        public string Name => "BotManager";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            m_config = source;
            // BotManager is enabled if NPC module is enabled
            IConfig config = source.Configs["NPC"];
            m_enabled = config != null && config.GetBoolean("Enabled", true);

            if (m_enabled)
            {
                m_navPollTimer = new System.Timers.Timer(NAV_POLL_INTERVAL_MS) { AutoReset = true };
                m_navPollTimer.Elapsed += NavPollTick;
                m_navPollTimer.Start();
            }
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled) return;
            lock (m_scenes) m_scenes.Add(scene);
            scene.RegisterModuleInterface<IBotManager>(this);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_enabled) return;
            m_npcModule = scene.RequestModuleInterface<INPCModule>();
            if (m_npcModule == null)
            {
                m_log.LogWarning("[BotManager] INPCModule not found -- bot functions will be unavailable.");
                m_enabled = false;
                return;
            }

            // Initialize bot persistence
            m_persistence = new BotPersistenceManager();
            m_persistence.Initialize(scene, this, m_config);

            // Load and respawn persistent bots (staggered)
            m_persistence.LoadPersistentBots();
            m_persistence.StartTimers();

            // Register console commands
            RegisterConsoleCommands(scene);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled) return;
            // Save persistent bot state before cleanup
            m_persistence?.OnRegionShutdown();
            // Clean up bots on this scene
            lock (m_bots)
            {
                foreach (var kvp in m_bots.ToList())
                {
                    if (kvp.Value.BotScene == scene)
                    {
                        kvp.Value.IsWandering = false;
                        UnsubscribeBotCollision(kvp.Value);
                        m_npcModule?.DeleteNPC(kvp.Key, scene);
                        m_bots.Remove(kvp.Key);
                    }
                }
            }
            lock (m_scenes) m_scenes.Remove(scene);
            scene.UnregisterModuleInterface<IBotManager>(this);
        }

        public void PostInitialise() { }
        public void Close()
        {
            if (m_navPollTimer != null)
            {
                m_navPollTimer.Stop();
                m_navPollTimer.Dispose();
                m_navPollTimer = null;
            }
        }

        #endregion

        #region Helpers

        private BotData GetBot(UUID botID)
        {
            lock (m_bots)
            {
                m_bots.TryGetValue(botID, out BotData data);
                return data;
            }
        }

        private BotData GetBotWithPermission(UUID botID, UUID callerID)
        {
            BotData data = GetBot(botID);
            if (data == null) return null;
            if (!m_npcModule.CheckPermissions(botID, callerID)) return null;
            return data;
        }

        /// <summary>Get the scene for a bot (stored in BotData).</summary>
        private Scene GetBotScene(BotData data)
        {
            return data?.BotScene;
        }

        /// <summary>Get the scene for a bot by ID.</summary>
        private Scene GetBotScene(UUID botID)
        {
            BotData data = GetBot(botID);
            return data?.BotScene;
        }

        /// <summary>Find the scene where the given agent is a root presence.</summary>
        private Scene FindSceneForRootAgent(UUID agentID)
        {
            lock (m_scenes)
            {
                foreach (Scene s in m_scenes)
                {
                    ScenePresence sp = s.GetScenePresence(agentID);
                    if (sp != null && !sp.IsChildAgent) return s;
                }
                // Fall back to any scene that has the agent
                foreach (Scene s in m_scenes)
                {
                    if (s.GetScenePresence(agentID) != null) return s;
                }
                return m_scenes.Count > 0 ? m_scenes[0] : null;
            }
        }

        /// <summary>Get the ScenePresence for a bot using its stored scene.</summary>
        private ScenePresence GetBotSP(BotData data)
        {
            if (data?.BotScene == null) return null;
            return data.BotScene.GetScenePresence(data.BotID);
        }

        public static UUID OutfitKey(UUID ownerID, string outfitName)   // osOwnerSaveAppearance returns it
        {
            // Deterministic UUID from owner + outfit name for dictionary keying
            return UUID.Parse(Utils.MD5String(ownerID.ToString() + ":" + outfitName.ToLowerInvariant()));
        }

        private void StopAllMovement(BotData data)
        {
            data.IsFollowing = false;
            data.FollowTarget = UUID.Zero;
            data.FollowLost = false;
            data.NavPoints = null;
            data.NavIndex = 0;
            data.NavInFlight = false;
            data.NavWaiting = false;
            data.NavFollowIndefinitely = false;
            data.IsWandering = false;
            Scene scene = GetBotScene(data);
            if (scene != null)
                m_npcModule.StopMoveToTarget(data.BotID, scene);
        }

        // bot_update flags (InWorldz BOT_MOVE_* constants)
        private const int BOT_MOVE_COMPLETE = 1, BOT_MOVE_UPDATE = 2, BOT_MOVE_FAILED = 3, BOT_MOVE_AVATAR_LOST = 4;

        private Vector3 BotPosition(BotData data) => GetBotSP(data)?.AbsolutePosition ?? Vector3.Zero;

        // BOT_MOVE_COMPLETE [bot position]: the last node is done.
        private void FireMoveComplete(BotData data)
            => FirePathEvent(data, BOT_MOVE_COMPLETE, new object[] { BotPosition(data) });

        // BOT_MOVE_UPDATE [next node, bot position]: the bot moves on to node nextNode.
        private void FireMoveUpdate(BotData data, int nextNode)
            => FirePathEvent(data, BOT_MOVE_UPDATE, new object[] { nextNode, BotPosition(data) });

        // BOT_MOVE_FAILED [next node, bot position]: the bot did not reach the node before nextNode.
        private void FireMoveFailed(BotData data, int nextNode)
            => FirePathEvent(data, BOT_MOVE_FAILED, new object[] { nextNode, BotPosition(data) });

        // BOT_MOVE_AVATAR_LOST [followed avatar's position, distance, bot position].
        private void FireAvatarLost(BotData data, Vector3 avatarPos, float distance)
            => FirePathEvent(data, BOT_MOVE_AVATAR_LOST, new object[] { avatarPos, distance, BotPosition(data) });

        private void FirePathEvent(BotData data, int eventType, object[] parameters)
        {
            UUID[] scripts;
            lock (data.PathEventScripts)
                scripts = data.PathEventScripts.ToArray();
            if (scripts.Length == 0) return;

            Scene scene = GetBotScene(data);
            if (scene == null) return;

            // Deliver the bot_update script event, with the original InWorldz/Halcyon signature
            // bot_update(string botID, integer flag, list params), to every registered script, as Halcyon's
            // MovementAction.TriggerBotUpdate did. The object[] params is turned into a list by the Phlox VM
            // (PostedEvent.Normalize), so this needs no dependency on the Phlox assemblies.
            //
            // The grid may run several script engines (YEngine + Phlox), each registered as
            // IScriptModule; RequestModuleInterface<IScriptModule>() returns only the first
            // (often YEngine), which may not own this script. Post to every engine — the one
            // that owns the script item delivers, the rest ignore an unknown item.
            IScriptModule[] engines = scene.RequestModuleInterfaces<IScriptModule>();
            if (engines == null) return;

            // The same outcome as SL's path_update(integer type, list reserved), for a
            // script that speaks SL pathfinding (llCreateCharacter / llNavigateTo) rather than the
            // InWorldz bot API. Posted beside bot_update rather than instead of it - a script
            // declares one handler or the other, and an engine drops an event the script has no
            // handler for. BotData carries no marker for how the bot was created, so both go.
            // Mapping per wiki.secondlife.com/wiki/Path_update: BOT_MOVE_COMPLETE (1) ->
            // PU_GOAL_REACHED (1); BOT_MOVE_FAILED (3, a navigation timeout) -> PU_FAILURE_UNREACHABLE
            // (4, "goal is no longer reachable for some reason"); BOT_MOVE_AVATAR_LOST (4) ->
            // PU_FAILURE_TARGET_GONE (5, the target "can no longer be tracked"). BOT_MOVE_UPDATE (2), a
            // node passed on the way, has no path_update counterpart and posts none.
            int puType = eventType switch
            {
                BOT_MOVE_COMPLETE => 1,
                BOT_MOVE_FAILED => 4,
                BOT_MOVE_AVATAR_LOST => 5,
                _ => -1,
            };
            foreach (UUID script in scripts)
            {
                // Each script gets its own arrays: the Phlox VM keeps a posted params array as its list's storage.
                object[] args = new object[] { data.BotID.ToString(), eventType, (object[])parameters.Clone() };
                object[] pathArgs = puType < 0 ? null : new object[] { puType, new object[0] };
                foreach (IScriptModule engine in engines)
                {
                    engine?.PostScriptEvent(script, "bot_update", args);
                    if (pathArgs != null)
                        engine?.PostScriptEvent(script, "path_update", pathArgs);
                }
            }
        }

        #endregion

        #region Lifecycle

        public UUID CreateBot(string firstName, string lastName, Vector3 startPos,
            string outfitName, UUID scriptItemID, UUID ownerID, out string reason)
        {
            UUID botID = CreateBot(firstName, lastName, startPos, outfitName, scriptItemID, ownerID, true, true, out reason);
            // The bot* door registers the creating script for bot_update, as Halcyon's BotManager.CreateBot did.
            if (botID != UUID.Zero && scriptItemID != UUID.Zero)
                BotRegisterForPathUpdateEvents(botID, scriptItemID, ownerID);
            return botID;
        }

        /// <summary>The osNpcCreate door. Same bot, same BotData; ownership and sensing per the OS_NPC_* flags.</summary>
        public UUID CreateBot(string firstName, string lastName, Vector3 startPos,
            string outfitName, UUID scriptItemID, UUID ownerID, bool owned, bool senseAsAgent, out string reason)
        {
            reason = null;
            if (m_npcModule == null) { reason = "NPC module not available"; return UUID.Zero; }

            // Find the scene the owner is on -- bot will be created there
            Scene ownerScene = FindSceneForRootAgent(ownerID);
            if (ownerScene == null) { reason = "Owner not found in any region"; return UUID.Zero; }

            return CreateBotIn(ownerScene, UUID.Zero, firstName, lastName, startPos, outfitName, scriptItemID, ownerID,
                owned, senseAsAgent, out reason);
        }

        /// <summary>
        /// Brings a saved bot back into the region it was saved in, under the key it had, so a script that stored the
        /// key still reaches it. The bot comes back as botCreateBot made it, with the creating script registered for
        /// bot_update. If anything in the simulator already holds the key (an avatar, or another bot), the bot comes
        /// back under a new random key instead, and the caller moves the saved record to it.
        /// </summary>
        public UUID RespawnBot(Scene scene, UUID botID, string firstName, string lastName, Vector3 startPos,
            UUID scriptItemID, UUID ownerID, out string reason)
        {
            reason = null;
            if (m_npcModule == null) { reason = "NPC module not available"; return UUID.Zero; }

            UUID key = botID;
            if (botID != UUID.Zero && KeyInUse(botID))
            {
                m_log.LogWarning("[BotManager] Key {0} is already in use; the saved bot comes back under a new key", botID);
                key = UUID.Zero;
            }

            UUID id = CreateBotIn(scene, key, firstName, lastName, startPos, null, scriptItemID, ownerID, true, true, out reason);
            if (id != UUID.Zero && scriptItemID != UUID.Zero)
                BotRegisterForPathUpdateEvents(id, scriptItemID, ownerID);
            return id;
        }

        // A bot's key is in use when this manager has a bot under it or any region here has a presence under it.
        private bool KeyInUse(UUID key)
        {
            if (IsBot(key)) return true;
            lock (m_scenes)
            {
                foreach (Scene s in m_scenes)
                    if (s.GetScenePresence(key) != null) return true;
            }
            return false;
        }

        // Makes the bot in the given scene; agentID is the key to give it, or UUID.Zero for a new random key.
        private UUID CreateBotIn(Scene ownerScene, UUID agentID, string firstName, string lastName, Vector3 startPos,
            string outfitName, UUID scriptItemID, UUID ownerID, bool owned, bool senseAsAgent, out string reason)
        {
            reason = null;

            // Get appearance -- try saved outfit first, fall back to owner's appearance
            AvatarAppearance appearance = null;

            if (!string.IsNullOrEmpty(outfitName))
            {
                string ownerKey = ownerID.ToString();
                lock (m_savedOutfits)
                {
                    if (m_savedOutfits.TryGetValue(ownerKey, out var outfits))
                    {
                        UUID oKey = OutfitKey(ownerID, outfitName);
                        outfits.TryGetValue(oKey, out appearance);
                    }
                }
            }

            if (appearance == null)
            {
                // Clone the owner's current appearance -- must be root agent
                ScenePresence ownerSP = ownerScene.GetScenePresence(ownerID);
                if (ownerSP != null && !ownerSP.IsChildAgent)
                {
                    appearance = new AvatarAppearance(ownerSP.Appearance, true);
                }
                else
                {
                    // Owner is a child agent or not present -- try avatar service
                    IAvatarService avatarService = ownerScene.RequestModuleInterface<IAvatarService>();
                    if (avatarService != null)
                    {
                        AvatarData avatarData = avatarService.GetAvatar(ownerID);
                        if (avatarData != null)
                            appearance = avatarData.ToAvatarAppearance();
                    }
                    if (appearance == null)
                        appearance = new AvatarAppearance();
                }
            }

            // The bot* door always senses as agent and is always owned; osNpcCreate chooses.
            UUID npcOwner = owned ? ownerID : UUID.Zero;
            UUID botID = m_npcModule.CreateNPC(firstName, lastName, startPos, agentID,
                npcOwner, "", UUID.Zero, senseAsAgent, ownerScene, appearance);

            if (botID == UUID.Zero)
            {
                reason = "Failed to create NPC (max NPC limit may be reached)";
                return UUID.Zero;
            }

            BotData data = new BotData
            {
                BotID = botID,
                OwnerID = npcOwner,
                ScriptItemID = scriptItemID,
                BotScene = ownerScene
            };

            lock (m_bots)
                m_bots[botID] = data;

            m_log.LogInformation("[BotManager] Created bot {0} {1} ({2}) for owner {3} in {4}",
                firstName, lastName, botID, ownerID, ownerScene.RegionInfo.RegionName);

            return botID;
        }

        public void RemoveBot(UUID botID, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;

            StopAllMovement(data);
            UnsubscribeBotCollision(data);
            Scene scene = GetBotScene(data);
            if (scene != null)
                m_npcModule.DeleteNPC(botID, scene);

            lock (m_bots)
                m_bots.Remove(botID);

            // Notify persistence manager
            m_persistence?.OnBotRemoved(botID);
        }

        public bool IsBot(UUID userID)
        {
            lock (m_bots)
                return m_bots.ContainsKey(userID);
        }

        public string GetBotName(UUID botID)
        {
            BotData data = GetBot(botID);
            ScenePresence sp = GetBotSP(data);
            if (sp != null) return sp.Name;
            return string.Empty;
        }

        public UUID GetBotOwner(UUID botID)
        {
            BotData data = GetBot(botID);
            return data?.OwnerID ?? UUID.Zero;
        }

        public bool CheckPermission(UUID botID, UUID callerID)
        {
            return m_npcModule != null && m_npcModule.CheckPermissions(botID, callerID);
        }

        public List<UUID> GetAllBots()
        {
            lock (m_bots)
                return m_bots.Keys.ToList();
        }

        public List<UUID> GetAllOwnedBots(UUID ownerID)
        {
            lock (m_bots)
                return m_bots.Values
                    .Where(b => b.OwnerID == ownerID)
                    .Select(b => b.BotID)
                    .ToList();
        }

        #endregion

        #region Movement & Navigation

        public void SetBotNavigationPoints(UUID botID, List<Vector3> positions,
            List<TravelMode> travelModes, Dictionary<int, object> options, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;

            StopAllMovement(data);

            data.NavPoints = positions;
            data.NavModes = travelModes;
            data.NavOptions = options;
            data.NavIndex = 0;

            // Halcyon NavigationPathDescription: BOT_MOVEMENT_TYPE (0), an integer, BOT_MOVEMENT_FLAG_FOLLOW_INDEFINITELY (1)
            // repeats the path; BOT_MOVEMENT_TELEPORT_AFTER (1), an integer or float, is the seconds before the bot is
            // teleported to a point it has not reached.
            data.NavTeleportAfterMs = DEFAULT_TELEPORT_AFTER_MS;
            if (options != null)
            {
                if (options.TryGetValue(0 /*BOT_MOVEMENT_TYPE*/, out object type) && type is int t)
                    data.NavFollowIndefinitely = t == 1 /*BOT_MOVEMENT_FLAG_FOLLOW_INDEFINITELY*/;
                if (options.TryGetValue(1 /*BOT_MOVEMENT_TELEPORT_AFTER*/, out object after) && (after is int || after is float))
                    data.NavTeleportAfterMs = (long)(Convert.ToSingle(after) * 1000f);
            }

            if (positions.Count > 0)
                MoveToNextNavPoint(data, false);
        }

        // Puts the bot at pos at once, as a BOT_TRAVELMODE_TELEPORT point does.
        private void TeleportBot(BotData data, Scene scene, Vector3 pos)
        {
            m_npcModule.StopMoveToTarget(data.BotID, scene);
            ScenePresence sp = GetBotSP(data);
            if (sp != null)
            {
                sp.Velocity = Vector3.Zero;
                sp.AbsolutePosition = pos;
            }
        }

        // Holds the bot for ms, then the nav poll moves it on; updateAfter reports that as a move on to the next node.
        private static void HoldBot(BotData data, long ms, bool updateAfter)
        {
            data.NavWaitUntil = Environment.TickCount64 + ms;
            data.NavUpdateAfterWait = updateAfter;
            data.NavWaiting = true;
        }

        // Starts the bot toward node NavIndex, or reports the path done. changingNodes: the bot has just
        // finished the node before NavIndex (arrived, or waited), so BOT_MOVE_UPDATE reports the move on to
        // NavIndex, as Halcyon's MovementAction.GetNextDestination did when NodeGraph reported changingNodes.
        private void MoveToNextNavPoint(BotData data, bool changingNodes)
        {
            if (data.NavPoints == null) return;
            if (data.NavIndex >= data.NavPoints.Count)
            {
                if (data.IsWandering)
                {
                    // Halcyon WanderingAction.TriggerFinishedMovement picked the next point instead of reporting the
                    // move complete. It starts at the next poll, so a wander by teleport goes one point at a time.
                    NewWanderPath(data);
                    HoldBot(data, 0, false);
                    return;
                }
                if (data.NavFollowIndefinitely && data.NavPoints.Count > 0)
                {
                    // Halcyon NodeGraph went back to the first point and reported it as a move on to node
                    // NumberOfNodes. The first point starts at the next poll, so a path of teleports goes one
                    // point at a time instead of looping here.
                    data.NavIndex = 0;
                    FireMoveUpdate(data, data.NavPoints.Count);
                    HoldBot(data, 0, false);
                    return;
                }
                FireMoveComplete(data);
                return;
            }

            if (changingNodes)
                FireMoveUpdate(data, data.NavIndex);

            Scene scene = GetBotScene(data);
            if (scene == null) return;

            Vector3 target = data.NavPoints[data.NavIndex];
            TravelMode mode = data.NavIndex < data.NavModes.Count
                ? data.NavModes[data.NavIndex]
                : TravelMode.Walk;

            bool noFly = mode == TravelMode.Walk || mode == TravelMode.Run;
            bool running = mode == TravelMode.Run;

            if (mode == TravelMode.Teleport)
            {
                TeleportBot(data, scene, target);
                data.NavIndex++;
                // Halcyon reported every teleport as a move on to the next node, the last node included.
                FireMoveUpdate(data, data.NavIndex);
                MoveToNextNavPoint(data, false);
            }
            else if (mode == TravelMode.Wait)
            {
                // Stand still for the point's X seconds (botSetNavigationPoints passes the duration as
                // <seconds, 0, 0>), then go on; NavPollTick ends the wait. Halcyon NodeGraph.GetNextPosition
                // waits position.X seconds on a Wait node.
                m_npcModule.StopMoveToTarget(data.BotID, scene);
                HoldBot(data, (long)(Math.Max(0f, target.X) * 1000f), true);
                data.NavIndex++;
            }
            else
            {
                m_npcModule.MoveToTarget(data.BotID, scene, target, noFly, true, running);
                data.CurrentNavTarget = target;
                data.NavNoFly = noFly;
                data.NavRunning = running;
                data.NavInFlight = true;
                data.NavElapsedMs = 0;
                data.NavLastPoll = Environment.TickCount64;
                data.NavIndex++;
            }
        }

        // Polls bots that are walking to a waypoint. INPCModule gives no arrival callback,
        // so when a bot gets within NAV_ARRIVAL_TOLERANCE (horizontal) of its current target
        // we advance to the next waypoint via MoveToNextNavPoint — which fires bot_update
        // (BOT_MOVE_COMPLETE) once the list is exhausted. A bot that has not arrived after
        // NavTeleportAfterMs is teleported to the waypoint and goes on, as Halcyon's
        // MovementAction.GetNextDestination did: BOT_MOVE_FAILED, then BOT_MOVE_UPDATE, for the next node.
        private void NavPollTick(object sender, System.Timers.ElapsedEventArgs e)
        {
            List<BotData> inFlight;
            lock (m_bots)
            {
                inFlight = new List<BotData>();
                foreach (BotData d in m_bots.Values)
                    if (d.NavInFlight || d.NavWaiting) inFlight.Add(d);
            }

            foreach (BotData data in inFlight)
            {
                if (data.MovementPaused) continue;

                if (data.NavWaiting)
                {
                    if (Environment.TickCount64 < data.NavWaitUntil) continue;
                    data.NavWaiting = false;
                    try { MoveToNextNavPoint(data, data.NavUpdateAfterWait); }
                    catch (Exception ex)
                    {
                        m_log.LogWarning("[BotManager]: nav advance for bot {0} failed: {1}", data.BotID, ex.Message);
                    }
                    continue;
                }

                ScenePresence sp = GetBotSP(data);
                if (sp == null) { data.NavInFlight = false; continue; }

                float dx = sp.AbsolutePosition.X - data.CurrentNavTarget.X;
                float dy = sp.AbsolutePosition.Y - data.CurrentNavTarget.Y;
                bool arrived = (dx * dx + dy * dy) <= NAV_ARRIVAL_TOLERANCE * NAV_ARRIVAL_TOLERANCE;
                long now = Environment.TickCount64;
                data.NavElapsedMs += now - data.NavLastPoll;
                data.NavLastPoll = now;
                bool timedOut = data.NavElapsedMs >= data.NavTeleportAfterMs;

                try
                {
                    if (arrived)
                    {
                        data.NavInFlight = false;
                        MoveToNextNavPoint(data, true);
                    }
                    else if (timedOut)
                    {
                        // NavIndex is already the node after the one not reached.
                        data.NavInFlight = false;
                        Scene scene = GetBotScene(data);
                        if (scene != null) TeleportBot(data, scene, data.CurrentNavTarget);
                        FireMoveFailed(data, data.NavIndex);
                        FireMoveUpdate(data, data.NavIndex);
                        MoveToNextNavPoint(data, false);
                    }
                }
                catch (Exception ex)
                {
                    data.NavInFlight = false;
                    m_log.LogWarning("[BotManager]: nav advance for bot {0} failed: {1}", data.BotID, ex.Message);
                }
            }

            List<BotData> following;
            lock (m_bots)
                following = m_bots.Values.Where(d => d.IsFollowing && !d.MovementPaused).ToList();
            foreach (BotData data in following)
            {
                try { FollowStep(data); }
                catch (Exception ex)
                {
                    m_log.LogWarning("[BotManager]: follow check for bot {0} failed: {1}", data.BotID, ex.Message);
                }
            }

            // Lazily attach the collision bridge for bots that registered for collision events before
            // their physics actor existed (e.g. registered in the same event as botCreateBot).
            List<BotData> needCollisionBridge = null;
            lock (m_bots)
            {
                foreach (BotData d in m_bots.Values)
                {
                    if (d.CollisionEventHost != null && d.CollisionHandler == null)
                        (needCollisionBridge ??= new List<BotData>()).Add(d);
                }
            }
            if (needCollisionBridge != null)
                foreach (BotData d in needCollisionBridge)
                    SubscribeBotCollision(d);
        }

        // NOTE: Bot collisions use a module-contained delivery path (BotManager -> registered host group),
        // not core's ScenePresence.RaiseCollisionScriptEvents. This is a deliberate, contained choice to avoid
        // modifying the hot per-frame core collision path and to keep the fix portable to grids (e.g. Tranquillity)
        // whose bot subsystem differs. The phase machine (collision_start-once / collision-repeating /
        // collision_end-once + land_collision_*) and the DetectedObject/ColliderArgs population mirror core's
        // SceneObjectPart.PhysicsCollision exactly, but are reproduced here rather than calling that method:
        // its collision scratch buffers are only allocated for parts that subscribe to their OWN physics
        // collisions, so a phantom host (as the InWorldz bot-collision example requires) has none and calling
        // it would NPE. We deliver through the public EventManager.TriggerScriptColliding* triggers instead.
        // Each collider's UUID is remembered while resolvable (CollisionLast: localID->UUID) so a collider
        // DELETED mid-contact still fires collision_end via its remembered UUID (Halcyon TryExtractCollider
        // parity) rather than being dropped as modern core does.
        // Revisit/converge if core ever exposes a clean per-group collision-registration hook.
        private void SubscribeBotCollision(BotData data)
        {
            if (data.CollisionHandler != null) return;          // already bridged
            ScenePresence sp = GetBotSP(data);
            PhysicsActor pa = sp?.PhysicsActor;
            if (pa == null) return;                             // bot not physical yet; retried from NavPollTick

            data.CollisionPhysActor = pa;
            data.CollisionHandler = (ev) => OnBotCollision(data, ev);
            pa.OnCollisionUpdate += data.CollisionHandler;
        }

        private void UnsubscribeBotCollision(BotData data)
        {
            if (data.CollisionHandler != null && data.CollisionPhysActor != null)
                data.CollisionPhysActor.OnCollisionUpdate -= data.CollisionHandler;
            data.CollisionHandler = null;
            data.CollisionPhysActor = null;
        }

        // Delivers the bot's physics collisions to the registered host's scripts. Reproduces core's phase
        // machine: started = current-last (collision_start), continuing = current∩last (collision), ended =
        // last-current (collision_end); land by transition. Runs on the physics callback thread (same context
        // core uses), and only when a host script actually subscribed to the matching event.
        private void OnBotCollision(BotData data, EventArgs e)
        {
            SceneObjectGroup host = data.CollisionEventHost;   // local copy: deregister may null it concurrently
            if (host == null || host.IsDeleted) return;
            SceneObjectPart root = host.RootPart;
            if (root == null) return;

            scriptEvents ev = root.ScriptEvents;
            if ((ev & COLLISION_EVENT_MASK) == 0) return;
            if (!(e is CollisionEventUpdate cu)) return;

            Scene scene = GetBotScene(data);                   // colliders live in the bot's scene
            if (scene == null) return;

            try
            {
                // Exclude the bot's own local ID: a character's physics collision list can include
                // itself, and a bot must never report colliding with itself.
                uint self = 0;
                ScenePresence botSp = GetBotSP(data);
                if (botSp != null) self = botSp.LocalId;

                Dictionary<uint, UUID> last = data.CollisionLast;

                // Build the current colliding set as localID -> UUID. Remember the UUID now (while the
                // collider still resolves) so a delete-while-colliding can still be reported in
                // collision_end. Reuse an already-remembered UUID for continuing colliders.
                var coldata = cu.m_objCollisionList;
                Dictionary<uint, UUID> current = new Dictionary<uint, UUID>();
                bool curLand = false;
                if (coldata != null)
                {
                    foreach (uint id in coldata.Keys)
                    {
                        if (id == 0) { curLand = true; continue; }
                        if (id == self) continue;
                        if (current.ContainsKey(id)) continue;
                        UUID u;
                        if (!last.TryGetValue(id, out u) || u == UUID.Zero)
                            u = ResolveColliderUuid(scene, id);
                        current[id] = u;
                    }
                }

                EventManager em = scene.EventManager;
                int link = root.LinkNum;

                if ((ev & scriptEvents.collision_start) != 0)
                {
                    List<uint> started = new List<uint>();
                    foreach (uint id in current.Keys) if (!last.ContainsKey(id)) started.Add(id);
                    if (started.Count > 0)
                    {
                        ColliderArgs a = BuildColliderArgs(scene, started, current, link);
                        if (a.Colliders.Count > 0) em.TriggerScriptCollidingStart(root.LocalId, a);
                    }
                }
                if ((ev & scriptEvents.collision_end) != 0)
                {
                    List<uint> ended = new List<uint>();
                    foreach (uint id in last.Keys) if (!current.ContainsKey(id)) ended.Add(id);
                    if (ended.Count > 0)
                    {
                        // Pass `last` as the UUID map: a separated-but-alive collider resolves fully,
                        // a DELETED one falls back to its remembered UUID so collision_end still fires.
                        ColliderArgs a = BuildColliderArgs(scene, ended, last, link);
                        if (a.Colliders.Count > 0) em.TriggerScriptCollidingEnd(root.LocalId, a);
                    }
                }
                if ((ev & scriptEvents.collision) != 0)
                {
                    List<uint> cont = new List<uint>();
                    foreach (uint id in current.Keys) if (last.ContainsKey(id)) cont.Add(id);
                    if (cont.Count > 0)
                    {
                        ColliderArgs a = BuildColliderArgs(scene, cont, current, link);
                        if (a.Colliders.Count > 0) em.TriggerScriptColliding(root.LocalId, a);
                    }
                }

                if (curLand)
                {
                    if (!data.LandCollideLast && (ev & scriptEvents.land_collision_start) != 0)
                        em.TriggerScriptLandCollidingStart(root.LocalId, GroundArgs(root, link));
                    if ((ev & scriptEvents.land_collision) != 0)
                        em.TriggerScriptLandColliding(root.LocalId, GroundArgs(root, link));
                }
                else if (data.LandCollideLast && (ev & scriptEvents.land_collision_end) != 0)
                {
                    em.TriggerScriptLandCollidingEnd(root.LocalId, GroundArgs(root, link));
                }

                data.CollisionLast = current;
                data.LandCollideLast = curLand;
            }
            catch (Exception ex)
            {
                m_log.LogWarning("[BotManager]: bot {0} collision delivery to host {1} failed: {2}",
                    data.BotID, host.UUID, ex.Message);
            }
        }

        // Builds a ColliderArgs of DetectedObjects for the given collider local IDs, resolved against the
        // bot's scene (prim or avatar). Mirrors SceneObjectPart.CreateColliderArgs/CreateDetObject. For a
        // collider that no longer resolves (deleted while colliding), falls back to a DetectedObject carrying
        // the remembered UUID from uuidMap so collision_end still fires — mirrors Halcyon TryExtractCollider.
        private ColliderArgs BuildColliderArgs(Scene scene, List<uint> ids, Dictionary<uint, UUID> uuidMap, int linkNum)
        {
            List<DetectedObject> dets = new List<DetectedObject>();
            foreach (uint id in ids)
            {
                if (id == 0) continue;
                SceneObjectPart obj = scene.GetSceneObjectPart(id);
                if (obj != null)
                {
                    dets.Add(new DetectedObject()
                    {
                        keyUUID = obj.UUID,
                        nameStr = obj.Name,
                        ownerUUID = obj.OwnerID,
                        posVector = obj.AbsolutePosition,
                        rotQuat = obj.GetWorldRotation(),
                        velVector = obj.Velocity,
                        colliderType = 0,
                        groupUUID = obj.GroupID,
                        linkNumber = linkNum
                    });
                    continue;
                }
                ScenePresence av = scene.GetScenePresence(id);
                if (av != null && !av.IsChildAgent)
                {
                    DetectedObject d = new DetectedObject()
                    {
                        keyUUID = av.UUID,
                        nameStr = av.Name,
                        ownerUUID = av.UUID,
                        posVector = av.AbsolutePosition,
                        rotQuat = av.Rotation,
                        velVector = av.Velocity,
                        colliderType = av.IsNPC ? 0x20 : 0x1,
                        groupUUID = av.ControllingClient != null ? av.ControllingClient.ActiveGroupId : UUID.Zero,
                        linkNumber = linkNum
                    };
                    if (av.IsSatOnObject) d.colliderType |= 0x4;
                    else if (!d.velVector.IsZero()) d.colliderType |= 0x2;
                    dets.Add(d);
                    continue;
                }

                // Unresolvable (e.g. deleted while colliding): fall back to the remembered UUID so the
                // collision_end still fires with a valid llDetectedKey, as Halcyon's TryExtractCollider does.
                UUID remembered;
                if (uuidMap != null && uuidMap.TryGetValue(id, out remembered) && remembered != UUID.Zero)
                {
                    dets.Add(new DetectedObject()
                    {
                        keyUUID = remembered,
                        nameStr = string.Empty,
                        ownerUUID = UUID.Zero,
                        posVector = Vector3.Zero,
                        rotQuat = Quaternion.Identity,
                        velVector = Vector3.Zero,
                        colliderType = 0,
                        groupUUID = UUID.Zero,
                        linkNumber = linkNum
                    });
                }
            }
            return new ColliderArgs() { Colliders = dets };
        }

        // Resolves a collider local ID to its UUID while it still exists, so it can be remembered for a
        // later collision_end if the object is deleted mid-contact. Returns UUID.Zero if unresolvable.
        private UUID ResolveColliderUuid(Scene scene, uint localId)
        {
            SceneObjectPart obj = scene.GetSceneObjectPart(localId);
            if (obj != null) return obj.UUID;
            ScenePresence av = scene.GetScenePresence(localId);
            if (av != null) return av.UUID;
            return UUID.Zero;
        }

        // Builds the single ground DetectedObject for a land_collision event (mirrors CreateDetObjectForGround).
        private ColliderArgs GroundArgs(SceneObjectPart root, int linkNum)
        {
            return new ColliderArgs()
            {
                Colliders = new List<DetectedObject>()
                {
                    new DetectedObject()
                    {
                        keyUUID = UUID.Zero,
                        nameStr = "",
                        ownerUUID = UUID.Zero,
                        posVector = root.AbsolutePosition,
                        rotQuat = Quaternion.Identity,
                        velVector = Vector3.Zero,
                        colliderType = 0,
                        groupUUID = UUID.Zero,
                        linkNumber = linkNum
                    }
                }
            };
        }

        public BotMovementResult StartFollowingAvatar(UUID botID, UUID targetID,
            Dictionary<int, object> options, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return BotMovementResult.BotNotFound;

            Scene scene = GetBotScene(data);
            if (scene == null) return BotMovementResult.BotNotFound;

            ScenePresence targetSP = scene.GetScenePresence(targetID);
            if (targetSP == null) return BotMovementResult.UserNotFound;

            StopAllMovement(data);

            data.IsFollowing = true;
            data.FollowTarget = targetID;
            data.FollowOptions = options;
            ReadFollowOptions(data, options);
            data.FollowAtAvatar = false;
            data.FollowJumpAttempts = 0;

            FollowStep(data);
            return BotMovementResult.Success;
        }

        private const float DEFAULT_STOP_FOLLOW_DISTANCE = 2f;      // Halcyon AvatarFollowerDescription
        private const float DEFAULT_START_FOLLOW_DISTANCE = 3f;
        private const float DEFAULT_LOST_AVATAR_DISTANCE = 1000f;
        private const float FOLLOW_JUMP_IMPULSE = 9.4f;             // what ScenePresenceAnimator gives an avatar's jump

        // Halcyon AvatarFollowerDescription: BOT_ALLOW_RUNNING (1), BOT_ALLOW_FLYING (2), BOT_ALLOW_JUMPING (3) and
        // BOT_REQUIRES_LINE_OF_SIGHT (5) are integers, 1 for yes (the first three default to yes, the last to no);
        // BOT_FOLLOW_OFFSET (4) is a vector added to the avatar's position; BOT_START_FOLLOWING_DISTANCE (6),
        // BOT_STOP_FOLLOWING_DISTANCE (7) and BOT_LOST_AVATAR_DISTANCE (8) are metres, an integer or a float.
        private static void ReadFollowOptions(BotData data, Dictionary<int, object> options)
        {
            data.FollowAllowRunning = data.FollowAllowFlying = data.FollowAllowJumping = true;
            data.FollowNeedsSight = false;
            data.FollowOffset = Vector3.Zero;
            data.FollowStartDistance = DEFAULT_START_FOLLOW_DISTANCE;
            data.FollowStopDistance = DEFAULT_STOP_FOLLOW_DISTANCE;
            data.FollowLostDistance = DEFAULT_LOST_AVATAR_DISTANCE;
            if (options == null) return;

            foreach (KeyValuePair<int, object> kvp in options)
            {
                object v = kvp.Value;
                bool number = v is int || v is float;
                switch (kvp.Key)
                {
                    case 1 /*BOT_ALLOW_RUNNING*/: if (v is int run) data.FollowAllowRunning = run == 1; break;
                    case 2 /*BOT_ALLOW_FLYING*/: if (v is int fly) data.FollowAllowFlying = fly == 1; break;
                    case 3 /*BOT_ALLOW_JUMPING*/: if (v is int jump) data.FollowAllowJumping = jump == 1; break;
                    case 4 /*BOT_FOLLOW_OFFSET*/: if (v is Vector3 offset) data.FollowOffset = offset; break;
                    case 5 /*BOT_REQUIRES_LINE_OF_SIGHT*/: if (v is int sight) data.FollowNeedsSight = sight == 1; break;
                    case 6 /*BOT_START_FOLLOWING_DISTANCE*/: if (number) data.FollowStartDistance = Convert.ToSingle(v); break;
                    case 7 /*BOT_STOP_FOLLOWING_DISTANCE*/: if (number) data.FollowStopDistance = Convert.ToSingle(v); break;
                    case 8 /*BOT_LOST_AVATAR_DISTANCE*/: if (number) data.FollowLostDistance = Convert.ToSingle(v); break;
                }
            }
        }

        // One step of following, at each nav poll, after Halcyon's AvatarFollower (CheckInformationBeforeMove,
        // UpdateInformation and DirectFollowing):
        // - closer than the stop distance (the start distance once the bot has stopped) the bot stops beside the avatar;
        // - BOT_MOVE_AVATAR_LOST is raised once each time the avatar is lost: it left the region ([ZERO_VECTOR, 0.0, bot
        //   position]), it is farther than the lost distance ([avatar position, 0.0, bot position]; the bot keeps
        //   following, as Halcyon's did), or line of sight is required and an object is in between ([avatar position,
        //   distance, bot position]; the bot goes no further toward it). The avatar found again re-arms it;
        // - otherwise the bot is sent toward the avatar plus BOT_FOLLOW_OFFSET: flying when the avatar flies, or when the
        //   avatar is more than 3 m above or below, if flying is allowed; running when the avatar runs, if running is
        //   allowed; for an avatar a little above it, at its own height unless jumping is allowed and something taller
        //   than the bot is in the way, when the bot jumps if it is near the foot of it.
        // Halcyon also steered around objects along the avatar's recent positions; this goes straight for the avatar.
        private void FollowStep(BotData data)
        {
            Scene scene = GetBotScene(data);
            ScenePresence bot = GetBotSP(data);
            if (scene == null || bot == null) return;

            ScenePresence target = scene.GetScenePresence(data.FollowTarget);
            if (target == null || target.IsChildAgent)      // a child agent stands in another region
            {
                if (!data.FollowLost)
                {
                    data.FollowLost = true;
                    FireAvatarLost(data, Vector3.Zero, 0.0f);
                }
                return;
            }

            Vector3 botPos = bot.AbsolutePosition;
            Vector3 targetPos = target.AbsolutePosition + data.FollowOffset;
            targetPos.X = Math.Clamp(targetPos.X, 0f, scene.RegionInfo.RegionSizeX);
            targetPos.Y = Math.Clamp(targetPos.Y, 0f, scene.RegionInfo.RegionSizeY);
            float distance = Vector3.Distance(targetPos, botPos);

            float closeEnough = data.FollowAtAvatar ? data.FollowStartDistance : data.FollowStopDistance;
            if (distance < closeEnough)
            {
                if (!data.FollowAtAvatar)
                {
                    m_npcModule.StopMoveToTarget(data.BotID, scene);
                    // A bot that had to fly up to here lands (Halcyon AvatarFollower.UpdateInformation).
                    if (data.FollowJumpAttempts > 0 && !(data.FollowAllowFlying && target.Flying))
                        bot.Flying = false;
                    data.FollowJumpAttempts = 0;
                }
                data.FollowAtAvatar = true;
                return;
            }
            data.FollowAtAvatar = false;

            bool outOfSight = data.FollowNeedsSight && SomethingBetween(scene, botPos, targetPos, 0f);
            bool tooFar = distance > data.FollowLostDistance;
            if (outOfSight || tooFar)
            {
                if (!data.FollowLost)
                {
                    data.FollowLost = true;
                    FireAvatarLost(data, target.AbsolutePosition, outOfSight ? distance : 0.0f);
                }
                if (outOfSight) return;
            }
            else
                data.FollowLost = false;

            bool fly = data.FollowAllowFlying && target.Flying;
            bool jump = false;
            float dz = targetPos.Z - botPos.Z;
            if (!fly && (dz > 0.25f || data.FollowJumpAttempts > 5))
            {
                if (data.FollowJumpAttempts > 5 || dz > 3f)
                {
                    if (data.FollowJumpAttempts <= 5) data.FollowJumpAttempts = 6;
                    if (data.FollowAllowFlying) fly = true;
                }
                else if (!data.FollowAllowJumping || !SomethingBetween(scene, botPos, targetPos, bot.Appearance.AvatarHeight))
                {
                    data.FollowJumpAttempts--;
                    targetPos.Z = botPos.Z + 0.15f;
                }
                else
                {
                    if (data.FollowJumpAttempts < 0) data.FollowJumpAttempts = 0;
                    data.FollowJumpAttempts++;
                    // Halcyon's walkTo jumped when the point was within 2 m across and more than 1.5 m up.
                    jump = Math.Abs(targetPos.X - botPos.X) < 2f && Math.Abs(targetPos.Y - botPos.Y) < 2f && dz > 1.5f;
                }
            }
            else if (!fly)
            {
                if (dz < -3f && data.FollowAllowFlying) fly = true;
                data.FollowJumpAttempts--;
            }

            bool run = data.FollowAllowRunning && target.SetAlwaysRun;
            m_npcModule.MoveToTarget(data.BotID, scene, targetPos, !fly, !fly, run);
            if (jump && bot.IsColliding)
                bot.PhysicsActor?.AvatarJump(FOLLOW_JUMP_IMPULSE);
        }

        // Whether an object stands on the line from 'from' to 'to': a prim, not an attachment or phantom, that the line
        // crosses, and that is taller than minHeight. Halcyon's AvatarFollower cast a physics ray (llCastRay); this
        // tests the objects' boxes, which needs no physics engine.
        private static bool SomethingBetween(Scene scene, Vector3 from, Vector3 to, float minHeight)
        {
            Vector3 dir = to - from;
            float length = dir.Length();
            if (length < 0.001f) return false;
            Ray ray = new Ray(from, dir / length);
            foreach (SceneObjectGroup sog in scene.GetSceneObjectGroups())
            {
                if (sog.IsDeleted || sog.IsAttachment || sog.IsPhantom) continue;
                EntityIntersection hit = sog.TestIntersection(ray, false, false);
                if (hit.HitTF && hit.distance <= length && hit.obj != null && hit.obj.Scale.Z > minHeight)
                    return true;
            }
            return false;
        }

        public void StopMovement(UUID botID, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;
            StopAllMovement(data);
        }

        public void PauseBotMovement(UUID botID, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;
            data.MovementPaused = true;
            Scene scene = GetBotScene(data);
            if (scene != null)
                m_npcModule.StopMoveToTarget(botID, scene);
        }

        public void ResumeBotMovement(UUID botID, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;
            data.MovementPaused = false;

            // Resume navigation if we had waypoints. A wait in progress is left to NavPollTick to finish. A move in
            // progress goes on toward its point, and the time paused does not count toward the teleport, as
            // Halcyon's MovementAction.ResumeMovement restarted its step clock.
            if (data.NavInFlight)
            {
                data.NavLastPoll = Environment.TickCount64;
                Scene scene = GetBotScene(data);
                if (scene != null)
                    m_npcModule.MoveToTarget(botID, scene, data.CurrentNavTarget, data.NavNoFly, true, data.NavRunning);
            }
            else if (!data.NavWaiting && data.NavPoints != null && data.NavIndex < data.NavPoints.Count)
                MoveToNextNavPoint(data, false);
        }

        public void SetBotSpeed(UUID botID, float speed, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;
            data.SpeedMultiplier = speed;

            ScenePresence sp = GetBotSP(data);
            if (sp != null)
                sp.SpeedModifier = speed;
        }

        public void WanderWithin(UUID botID, Vector3 origin, Vector3 distances,
            Dictionary<int, object> options, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;

            StopAllMovement(data);

            data.IsWandering = true;
            data.WanderOrigin = origin;
            data.WanderDistances = distances;
            data.WanderOptions = options;

            // Halcyon WanderingDescription: BOT_WANDER_MOVEMENT_TYPE (1), an integer, is the travel mode to each point
            // (walk by default); BOT_WANDER_TIME_BETWEEN_NODES (2), an integer or float, is the seconds to wait at each
            // point (0 by default: on to the next point at once).
            data.WanderMode = TravelMode.Walk;
            data.WanderWait = 0f;
            if (options.TryGetValue(1 /*BOT_WANDER_MOVEMENT_TYPE*/, out object modeObj) && modeObj is int mode
                && mode >= (int)TravelMode.Walk && mode <= (int)TravelMode.Teleport)
                data.WanderMode = (TravelMode)mode;
            if (options.TryGetValue(2 /*BOT_WANDER_TIME_BETWEEN_NODES*/, out object timeObj) && (timeObj is int || timeObj is float))
                data.WanderWait = Convert.ToSingle(timeObj);

            NewWanderPath(data);
            MoveToNextNavPoint(data, false);
        }

        // The next wander path: a random point within the distances of the origin, and a wait after it when there is one.
        // Reaching the point then moves on to node 1, which raises BOT_MOVE_UPDATE only when the wait is there.
        private void NewWanderPath(BotData data)
        {
            Scene scene = GetBotScene(data);

            float rx = (float)(Random.Shared.NextDouble() * 2.0 - 1.0) * data.WanderDistances.X;
            float ry = (float)(Random.Shared.NextDouble() * 2.0 - 1.0) * data.WanderDistances.Y;
            Vector3 target = data.WanderOrigin + new Vector3(rx, ry, 0);

            // Clamp to region bounds
            target.X = Math.Clamp(target.X, 0.5f, 255.5f);
            target.Y = Math.Clamp(target.Y, 0.5f, 255.5f);

            // Ensure above terrain
            if (scene != null)
            {
                float terrainHeight = (float)scene.Heightmap[(int)target.X, (int)target.Y];
                if (target.Z < terrainHeight)
                    target.Z = terrainHeight;
            }

            var points = new List<Vector3> { target };
            var modes = new List<TravelMode> { data.WanderMode };
            if (data.WanderWait != 0f)
            {
                points.Add(new Vector3(data.WanderWait, 0, 0));
                modes.Add(TravelMode.Wait);
            }
            data.NavPoints = points;
            data.NavModes = modes;
            data.NavIndex = 0;
            data.NavFollowIndefinitely = false;
            data.NavTeleportAfterMs = DEFAULT_TELEPORT_AFTER_MS;
        }

        public Vector3 GetBotPosition(UUID botID, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return Vector3.Zero;

            ScenePresence sp = GetBotSP(data);
            return sp?.AbsolutePosition ?? Vector3.Zero;
        }

        public void SetBotPosition(UUID botID, Vector3 position, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;

            ScenePresence sp = GetBotSP(data);
            if (sp != null)
            {
                sp.Velocity = Vector3.Zero;
                sp.AbsolutePosition = position;
            }
        }

        public void SetBotRotation(UUID botID, Quaternion rotation, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;

            ScenePresence sp = GetBotSP(data);
            if (sp != null)
                sp.Rotation = rotation;
        }

        #endregion

        #region Chat

        public void BotChat(UUID botID, int channel, string message,
            ChatTypeEnum chatType, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;
            Scene scene = GetBotScene(data);
            if (scene == null) return;

            switch (chatType)
            {
                case ChatTypeEnum.Whisper:
                    m_npcModule.Whisper(botID, scene, message, channel);
                    break;
                case ChatTypeEnum.Say:
                    m_npcModule.Say(botID, scene, message, channel);
                    break;
                case ChatTypeEnum.Shout:
                    m_npcModule.Shout(botID, scene, message, channel);
                    break;
                case ChatTypeEnum.StartTyping:
                case ChatTypeEnum.StopTyping:
                    // Typing indicators -- send via chat broadcast
                    OSChatMessage chatMsg = new OSChatMessage
                    {
                        Channel = channel,
                        Message = message,
                        Type = chatType,
                        SenderUUID = botID,
                        From = GetBotName(botID)
                    };
                    ScenePresence sp = GetBotSP(data);
                    if (sp != null)
                        chatMsg.Position = sp.AbsolutePosition;
                    scene.EventManager.TriggerOnChatBroadcast(null, chatMsg);
                    break;
            }
        }

        public void SendInstantMessageForBot(UUID botID, UUID targetID,
            string message, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;
            Scene scene = GetBotScene(data);
            if (scene == null) return;

            GridInstantMessage im = new GridInstantMessage(
                scene, botID, GetBotName(botID), targetID,
                (byte)InstantMessageDialog.MessageFromAgent,
                false, message, UUID.Random(),
                false, Vector3.Zero, Array.Empty<byte>(), true);

            scene.EventManager.TriggerIncomingInstantMessage(im);
        }

        #endregion

        #region Interaction

        public void SitBotOnObject(UUID botID, UUID objectID, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;
            Scene scene = GetBotScene(data);
            if (scene != null)
                m_npcModule.Sit(botID, objectID, scene);
        }

        public void StandBotUp(UUID botID, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;
            Scene scene = GetBotScene(data);
            if (scene != null)
                m_npcModule.Stand(botID, scene);
        }

        public void BotTouchObject(UUID botID, UUID objectID, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;
            m_npcModule.Touch(botID, objectID);
        }

        public void GiveInventoryObject(UUID botID, SceneObjectPart host,
            string objName, UUID objID, byte assetType, UUID destID, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;
            Scene scene = GetBotScene(data);
            if (scene == null) return;

            // Get destination avatar's client for inventory transfer
            ScenePresence destSP = scene.GetScenePresence(destID);
            IClientAPI remoteClient = destSP?.ControllingClient;
            if (remoteClient == null) return;

            scene.MoveTaskInventoryItem(remoteClient, UUID.Zero, host, objID, out string _);
        }

        #endregion

        #region Animations

        public void StartBotAnimation(UUID botID, UUID animID, string animName,
            UUID hostID, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;

            ScenePresence sp = GetBotSP(data);
            if (sp != null)
                sp.Animator.AddAnimation(animID, hostID);
        }

        public void StopBotAnimation(UUID botID, UUID animID, string animName,
            UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;

            ScenePresence sp = GetBotSP(data);
            if (sp != null)
                sp.Animator.RemoveAnimation(animID, false);
        }

        #endregion

        #region Profile

        public void SetBotProfile(UUID botID, string aboutText, string email,
            UUID? imageID, string profileURL, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;

            if (aboutText != null) data.AboutText = aboutText;
            if (email != null) data.Email = email;
            if (imageID.HasValue) data.ImageID = imageID.Value;
            if (profileURL != null) data.ProfileURL = profileURL;

            // Update the INPC profile fields if available
            Scene scene = GetBotScene(data);
            if (scene != null)
            {
                INPC npc = m_npcModule.GetNPC(botID, scene);
                if (npc != null)
                {
                    if (aboutText != null) npc.profileAbout = aboutText;
                    if (imageID.HasValue) npc.profileImage = imageID.Value;
                }
            }
        }

        public bool GetBotProfile(UUID botID, out string aboutText, out string email,
            out UUID imageID, out string profileURL)
        {
            aboutText = email = profileURL = string.Empty;
            imageID = UUID.Zero;
            BotData data = GetBot(botID);
            if (data == null) return false;

            email = data.Email;
            profileURL = data.ProfileURL;
            aboutText = data.AboutText;
            imageID = data.ImageID;

            // About text and image also live on the NPC (osNpcSetProfileAbout and osNpcSetProfileImage
            // write only there, and the NPC caps the about text), so the NPC's values win while it exists.
            Scene scene = GetBotScene(data);
            INPC npc = scene != null ? m_npcModule?.GetNPC(botID, scene) : null;
            if (npc != null)
            {
                aboutText = npc.profileAbout ?? string.Empty;
                imageID = npc.profileImage;
            }
            return true;
        }

        #endregion

        #region Outfits

        public void SaveOutfitToDatabase(UUID ownerID, string outfitName, out string reason)
        {
            reason = null;

            Scene ownerScene = FindSceneForRootAgent(ownerID);
            if (ownerScene == null) { reason = "Owner not in any region"; return; }

            ScenePresence ownerSP = ownerScene.GetScenePresence(ownerID);
            if (ownerSP == null || ownerSP.IsChildAgent)
            {
                reason = "Owner not in region";
                return;
            }

            string ownerKey = ownerID.ToString();
            UUID oKey = OutfitKey(ownerID, outfitName);
            AvatarAppearance saved = new AvatarAppearance(ownerSP.Appearance, true);

            lock (m_savedOutfits)
            {
                if (!m_savedOutfits.ContainsKey(ownerKey))
                    m_savedOutfits[ownerKey] = new Dictionary<UUID, AvatarAppearance>();
                m_savedOutfits[ownerKey][oKey] = saved;

                // Store name for reverse lookup
                if (!m_outfitNames.ContainsKey(ownerKey))
                    m_outfitNames[ownerKey] = new Dictionary<UUID, string>();
                m_outfitNames[ownerKey][oKey] = outfitName;
            }

            m_log.LogInformation("[BotManager] Saved outfit '{0}' for {1}", outfitName, ownerID);
        }

        /// <summary>osNpcSaveAppearance - the bot's own appearance into the caller's outfit store.</summary>
        public UUID SaveBotOutfit(UUID botID, string outfitName, UUID ownerID, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(outfitName)) { reason = "No outfit name"; return UUID.Zero; }
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) { reason = "Bot not found or no permission"; return UUID.Zero; }
            ScenePresence sp = GetBotSP(data);
            if (sp == null) { reason = "Bot has no presence"; return UUID.Zero; }
            string ownerKey = ownerID.ToString();
            UUID oKey = OutfitKey(ownerID, outfitName);
            AvatarAppearance saved = new AvatarAppearance(sp.Appearance, true);
            lock (m_savedOutfits)
            {
                if (!m_savedOutfits.ContainsKey(ownerKey))
                    m_savedOutfits[ownerKey] = new Dictionary<UUID, AvatarAppearance>();
                m_savedOutfits[ownerKey][oKey] = saved;
                if (!m_outfitNames.ContainsKey(ownerKey))
                    m_outfitNames[ownerKey] = new Dictionary<UUID, string>();
                m_outfitNames[ownerKey][oKey] = outfitName;
            }
            m_log.LogInformation("[BotManager] Saved bot {0}'s appearance as outfit '{1}' for {2}", botID, outfitName, ownerID);
            return oKey;
        }

        public void RemoveOutfitFromDatabase(UUID ownerID, string outfitName)
        {
            string ownerKey = ownerID.ToString();
            UUID oKey = OutfitKey(ownerID, outfitName);

            lock (m_savedOutfits)
            {
                if (m_savedOutfits.TryGetValue(ownerKey, out var outfits))
                    outfits.Remove(oKey);
                if (m_outfitNames.TryGetValue(ownerKey, out var names))
                    names.Remove(oKey);
            }
        }

        public void ChangeBotOutfit(UUID botID, string outfitName, UUID ownerID, out string reason)
        {
            reason = null;
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) { reason = "Bot not found or no permission"; return; }

            string ownerKey = ownerID.ToString();
            UUID oKey = OutfitKey(ownerID, outfitName);
            AvatarAppearance appearance = null;

            lock (m_savedOutfits)
            {
                if (m_savedOutfits.TryGetValue(ownerKey, out var outfits))
                    outfits.TryGetValue(oKey, out appearance);
            }

            if (appearance == null)
            {
                reason = "Outfit '" + outfitName + "' not found";
                return;
            }

            Scene scene = GetBotScene(data);
            if (scene != null)
                m_npcModule.SetNPCAppearance(botID, appearance, scene);
        }

        public List<string> GetBotOutfitsByOwner(UUID ownerID)
        {
            string ownerKey = ownerID.ToString();
            lock (m_savedOutfits)
            {
                if (m_outfitNames.TryGetValue(ownerKey, out var names))
                    return names.Values.ToList();
            }
            return new List<string>();
        }

        #endregion

        #region Tagging

        public void AddTagToBot(UUID botID, string tag, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;
            lock (data.Tags) data.Tags.Add(tag);
        }

        public void RemoveTagFromBot(UUID botID, string tag, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;
            lock (data.Tags) data.Tags.Remove(tag);
        }

        public bool BotHasTag(UUID botID, string tag)
        {
            BotData data = GetBot(botID);
            if (data == null) return false;
            lock (data.Tags) return data.Tags.Contains(tag);
        }

        public List<string> GetBotTags(UUID botID)
        {
            BotData data = GetBot(botID);
            if (data == null) return new List<string>();
            lock (data.Tags) return data.Tags.ToList();
        }

        public List<UUID> GetBotsWithTag(string tag)
        {
            // An empty tag means every bot - no bot carries "" as a tag, so this was an
            // always-empty query; it is how botGetBotsWithTag("") lists an osNpcCreate'd NPC.
            if (string.IsNullOrEmpty(tag)) return GetAllBots();
            List<UUID> result = new List<UUID>();
            lock (m_bots)
            {
                foreach (var kvp in m_bots)
                {
                    lock (kvp.Value.Tags)
                    {
                        if (kvp.Value.Tags.Contains(tag))
                            result.Add(kvp.Key);
                    }
                }
            }
            return result;
        }

        public void RemoveBotsWithTag(string tag, UUID ownerID)
        {
            List<UUID> toRemove = new List<UUID>();
            lock (m_bots)
            {
                foreach (var kvp in m_bots)
                {
                    if (kvp.Value.OwnerID == ownerID)
                    {
                        lock (kvp.Value.Tags)
                        {
                            if (kvp.Value.Tags.Contains(tag))
                                toRemove.Add(kvp.Key);
                        }
                    }
                }
            }

            foreach (UUID id in toRemove)
                RemoveBot(id, ownerID);
        }

        #endregion

        #region Event Registration

        public void BotRegisterForPathUpdateEvents(UUID botID, UUID scriptItemID, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;
            lock (data.PathEventScripts)
            {
                if (!data.PathEventScripts.Contains(scriptItemID))
                    data.PathEventScripts.Add(scriptItemID);
            }
        }

        public void BotDeregisterFromPathUpdateEvents(UUID botID, UUID scriptItemID, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;
            lock (data.PathEventScripts)
                data.PathEventScripts.Remove(scriptItemID);
        }

        public void BotRegisterForCollisionEvents(UUID botID, SceneObjectGroup hostGroup, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;
            data.CollisionEventHost = hostGroup;
            SubscribeBotCollision(data);
        }

        public void BotDeregisterFromCollisionEvents(UUID botID, SceneObjectGroup hostGroup, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;
            data.CollisionEventHost = null;
            UnsubscribeBotCollision(data);
        }

        #endregion

        #region Console Commands

        private void RegisterConsoleCommands(Scene scene)
        {
            MainConsole.Instance.Commands.AddCommand(
                "BotPersistence", true, "list persistent bots",
                "list persistent bots",
                "List all active persistent bots in this region",
                HandleListPersistentBots);

            MainConsole.Instance.Commands.AddCommand(
                "BotPersistence", true, "clear persistent bots",
                "clear persistent bots [owner_uuid]",
                "Clear all persistent bots, or only those owned by owner_uuid",
                HandleClearPersistentBots);
        }

        private void HandleListPersistentBots(string module, string[] args)
        {
            if (m_persistence == null)
            {
                MainConsole.Instance.Output("Bot persistence is not enabled.");
                return;
            }

            var bots = m_persistence.ListActiveBots();
            if (bots.Count == 0)
            {
                MainConsole.Instance.Output("No active persistent bots.");
                return;
            }

            MainConsole.Instance.Output($"Active persistent bots: {bots.Count}");
            MainConsole.Instance.Output(
                $"{"Bot ID",-38} {"Name",-25} {"Owner",-38} {"Position",-20} {"Expires"}");
            MainConsole.Instance.Output(new string('-', 140));

            foreach (var bot in bots)
            {
                string expires = bot.ExpiresAt.HasValue
                    ? bot.ExpiresAt.Value.ToString("yyyy-MM-dd HH:mm")
                    : "never";
                string pos = $"<{bot.Position.X:F0},{bot.Position.Y:F0},{bot.Position.Z:F0}>";

                MainConsole.Instance.Output(
                    $"{bot.BotID,-38} {bot.BotFirstName + " " + bot.BotLastName,-25} " +
                    $"{bot.OwnerID,-38} {pos,-20} {expires}");
            }
        }

        private void HandleClearPersistentBots(string module, string[] args)
        {
            if (m_persistence == null)
            {
                MainConsole.Instance.Output("Bot persistence is not enabled.");
                return;
            }

            if (args.Length > 3 && UUID.TryParse(args[3], out UUID ownerID))
            {
                int count = m_persistence.ClearOwnerPersistentBots(ownerID);
                MainConsole.Instance.Output($"Cleared {count} persistent bots for owner {ownerID}");
            }
            else
            {
                int count = m_persistence.ClearAllPersistentBots();
                MainConsole.Instance.Output($"Cleared {count} persistent bots");
            }
        }

        #endregion
    }
}
