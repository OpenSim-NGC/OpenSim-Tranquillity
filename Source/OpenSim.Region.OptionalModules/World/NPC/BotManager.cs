/*
 * Copyright (c) Legion Grid Contributors
 * BotManager.cs — Bot/NPC management module for Phlox script engine
 * Wraps OpenSim's INPCModule with additional tracking for tags, profiles,
 * outfits, navigation, speed, and event registration.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Timers;
// Tranquillity has ImplicitUsings=enable (auto-imports System.Threading), so alias
// Timer to System.Timers.Timer to resolve the System.Threading.Timer ambiguity.
using Timer = System.Timers.Timer;
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
        public bool IsFollowing;
        public UUID FollowTarget;
        public Dictionary<int, object> FollowOptions;
        public bool IsWandering;
        public Vector3 WanderOrigin;
        public Vector3 WanderDistances;
        public Dictionary<int, object> WanderOptions;
        public Timer WanderTimer;

        // Event registration
        public UUID PathEventScriptID = UUID.Zero;
        public SceneObjectGroup CollisionEventHost;

        // Navigation arrival tracking (poll-driven). INPCModule.MoveToTarget is
        // fire-and-forget with no arrival callback, so we record the waypoint a bot
        // is currently walking to and poll its position to detect arrival.
        public Vector3 CurrentNavTarget;
        public bool NavInFlight;
        public int NavInFlightTicks;

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
        private const int NAV_INFLIGHT_TIMEOUT_TICKS = 120;  // ~60s safety so a stuck bot still reports

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
                        StopWanderTimer(kvp.Value);
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

        private static UUID OutfitKey(UUID ownerID, string outfitName)
        {
            // Deterministic UUID from owner + outfit name for dictionary keying
            return UUID.Parse(Utils.MD5String(ownerID.ToString() + ":" + outfitName.ToLowerInvariant()));
        }

        private void StopWanderTimer(BotData data)
        {
            if (data.WanderTimer != null)
            {
                data.WanderTimer.Stop();
                data.WanderTimer.Dispose();
                data.WanderTimer = null;
            }
            data.IsWandering = false;
        }

        private void StopAllMovement(BotData data)
        {
            data.IsFollowing = false;
            data.FollowTarget = UUID.Zero;
            data.NavPoints = null;
            data.NavIndex = 0;
            data.NavInFlight = false;
            StopWanderTimer(data);
            Scene scene = GetBotScene(data);
            if (scene != null)
                m_npcModule.StopMoveToTarget(data.BotID, scene);
        }

        private void FirePathEvent(BotData data, int eventType, Vector3 pos)
        {
            if (data.PathEventScriptID == UUID.Zero) return;

            Scene scene = GetBotScene(data);
            if (scene == null) return;

            // Deliver the bot_update script event, mirroring the original InWorldz/Halcyon
            // signature: bot_update(string botID, integer flag, list params). The empty
            // object[] is coerced to an LSLList by the Phlox VM (PostedEvent.Normalize), so
            // this needs no dependency on the Phlox assemblies. eventType carries the flag
            // (1 = BOT_MOVE_COMPLETE, 3 = BOT_MOVE_FAILED), matching what InWorldz scripts expect.
            //
            // The grid may run several script engines (YEngine + Phlox), each registered as
            // IScriptModule; RequestModuleInterface<IScriptModule>() returns only the first
            // (often YEngine), which may not own this script. Post to every engine — the one
            // that owns the script item delivers, the rest ignore an unknown item.
            IScriptModule[] engines = scene.RequestModuleInterfaces<IScriptModule>();
            if (engines == null) return;

            object[] args = new object[] { data.BotID.ToString(), eventType, new object[0] };
            foreach (IScriptModule engine in engines)
                engine?.PostScriptEvent(data.PathEventScriptID, "bot_update", args);
        }

        #endregion

        #region Lifecycle

        public UUID CreateBot(string firstName, string lastName, Vector3 startPos,
            string outfitName, UUID scriptItemID, UUID ownerID, out string reason)
        {
            reason = null;
            if (m_npcModule == null) { reason = "NPC module not available"; return UUID.Zero; }

            // Find the scene the owner is on -- bot will be created there
            Scene ownerScene = FindSceneForRootAgent(ownerID);
            if (ownerScene == null) { reason = "Owner not found in any region"; return UUID.Zero; }

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

            bool senseAsAgent = true; // Bots appear as agents by default
            UUID botID = m_npcModule.CreateNPC(firstName, lastName, startPos,
                ownerID, senseAsAgent, ownerScene, appearance);

            if (botID == UUID.Zero)
            {
                reason = "Failed to create NPC (max NPC limit may be reached)";
                return UUID.Zero;
            }

            BotData data = new BotData
            {
                BotID = botID,
                OwnerID = ownerID,
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

            if (positions.Count > 0)
                MoveToNextNavPoint(data);
        }

        private void MoveToNextNavPoint(BotData data)
        {
            if (data.NavPoints == null || data.NavIndex >= data.NavPoints.Count)
            {
                FirePathEvent(data, 1 /*BOT_MOVE_COMPLETE*/, Vector3.Zero);
                return;
            }

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
                // Teleport directly
                ScenePresence sp = GetBotSP(data);
                if (sp != null)
                {
                    sp.Velocity = Vector3.Zero;
                    sp.AbsolutePosition = target;
                }
                data.NavIndex++;
                MoveToNextNavPoint(data);
            }
            else if (mode == TravelMode.Wait)
            {
                // Wait mode -- just advance to next point
                data.NavIndex++;
                MoveToNextNavPoint(data);
            }
            else
            {
                m_npcModule.MoveToTarget(data.BotID, scene, target, noFly, true, running);
                data.CurrentNavTarget = target;
                data.NavInFlight = true;
                data.NavInFlightTicks = 0;
                data.NavIndex++;
            }
        }

        // Polls bots that are walking to a waypoint. INPCModule gives no arrival callback,
        // so when a bot gets within NAV_ARRIVAL_TOLERANCE (horizontal) of its current target
        // we advance to the next waypoint via MoveToNextNavPoint — which fires bot_update
        // (BOT_MOVE_COMPLETE) once the list is exhausted. A timeout reports BOT_MOVE_FAILED
        // so a stuck bot still notifies the script rather than hanging silently.
        private void NavPollTick(object sender, System.Timers.ElapsedEventArgs e)
        {
            List<BotData> inFlight;
            lock (m_bots)
            {
                inFlight = new List<BotData>();
                foreach (BotData d in m_bots.Values)
                    if (d.NavInFlight) inFlight.Add(d);
            }

            foreach (BotData data in inFlight)
            {
                if (data.MovementPaused) continue;

                ScenePresence sp = GetBotSP(data);
                if (sp == null) { data.NavInFlight = false; continue; }

                float dx = sp.AbsolutePosition.X - data.CurrentNavTarget.X;
                float dy = sp.AbsolutePosition.Y - data.CurrentNavTarget.Y;
                bool arrived = (dx * dx + dy * dy) <= NAV_ARRIVAL_TOLERANCE * NAV_ARRIVAL_TOLERANCE;
                bool timedOut = ++data.NavInFlightTicks >= NAV_INFLIGHT_TIMEOUT_TICKS;

                try
                {
                    if (arrived)
                    {
                        data.NavInFlight = false;
                        MoveToNextNavPoint(data);
                    }
                    else if (timedOut)
                    {
                        data.NavInFlight = false;
                        data.NavPoints = null;
                        FirePathEvent(data, 3 /*BOT_MOVE_FAILED*/, data.CurrentNavTarget);
                    }
                }
                catch (Exception ex)
                {
                    data.NavInFlight = false;
                    m_log.LogWarning("[BotManager]: nav advance for bot {0} failed: {1}", data.BotID, ex.Message);
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

            // Start following by moving to target's position
            Vector3 targetPos = targetSP.AbsolutePosition;

            // Apply follow offset if specified
            if (options.TryGetValue(4 /*BOT_FOLLOW_OFFSET*/, out object offsetObj) && offsetObj is Vector3 offset)
                targetPos += offset;

            m_npcModule.MoveToTarget(botID, scene, targetPos, false, true, false);

            return BotMovementResult.Success;
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

            // Resume navigation if we had waypoints
            if (data.NavPoints != null && data.NavIndex < data.NavPoints.Count)
                MoveToNextNavPoint(data);
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

            // Determine travel mode
            bool running = false;
            bool noFly = true;
            if (options.TryGetValue(1 /*BOT_WANDER_MOVEMENT_TYPE*/, out object modeObj))
            {
                int mode = Convert.ToInt32(modeObj);
                if (mode == 2 /*BOT_TRAVELMODE_RUN*/) running = true;
                if (mode == 3 /*BOT_TRAVELMODE_FLY*/) noFly = false;
            }

            // Determine time between wander nodes
            double interval = 5.0; // default 5 seconds
            if (options.TryGetValue(2 /*BOT_WANDER_TIME_BETWEEN_NODES*/, out object timeObj))
                interval = Convert.ToDouble(timeObj);

            // Move to first random point
            WanderToRandomPoint(data, noFly, running);

            // Set up timer for subsequent wander points
            data.WanderTimer = new Timer(interval * 1000.0);
            data.WanderTimer.Elapsed += (s, e) => WanderToRandomPoint(data, noFly, running);
            data.WanderTimer.AutoReset = true;
            data.WanderTimer.Start();
        }

        private void WanderToRandomPoint(BotData data, bool noFly, bool running)
        {
            if (!data.IsWandering) return;
            Scene scene = GetBotScene(data);
            if (scene == null) return;

            float rx = (float)(Random.Shared.NextDouble() * 2.0 - 1.0) * data.WanderDistances.X;
            float ry = (float)(Random.Shared.NextDouble() * 2.0 - 1.0) * data.WanderDistances.Y;
            Vector3 target = data.WanderOrigin + new Vector3(rx, ry, 0);

            // Clamp to region bounds
            target.X = Math.Clamp(target.X, 0.5f, 255.5f);
            target.Y = Math.Clamp(target.Y, 0.5f, 255.5f);

            // Ensure above terrain
            float terrainHeight = (float)scene.Heightmap[(int)target.X, (int)target.Y];
            if (target.Z < terrainHeight)
                target.Z = terrainHeight;

            m_npcModule.MoveToTarget(data.BotID, scene, target, noFly, true, running);
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
            data.PathEventScriptID = scriptItemID;
        }

        public void BotDeregisterFromPathUpdateEvents(UUID botID, UUID scriptItemID, UUID ownerID)
        {
            BotData data = GetBotWithPermission(botID, ownerID);
            if (data == null) return;
            data.PathEventScriptID = UUID.Zero;
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
