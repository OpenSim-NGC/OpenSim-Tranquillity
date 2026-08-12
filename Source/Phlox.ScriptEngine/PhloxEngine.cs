/*
 * Legion Grid — Phlox Script Engine Integration
 * Adapted from InWorldz Halcyon EngineInterface.cs
 * Copyright (c) InWorldz Halcyon Developers (original)
 * Adapted 2026 for Legion Grid / OpenSim 0.9.3 .NET 8
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Services.Interfaces;

namespace Phlox.ScriptEngine
{
    public delegate void WorkArrivedDelegate();

    // No Mono.Addins [assembly: Addin]/[Extension] registration: develop discovers
    // region modules by interface reflection (IPluginDiscovery scans for
    // INonSharedRegionModule implementers), same as the other engine modules.
    public class PhloxEngine : INonSharedRegionModule, IScriptEngine, IScriptModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const ThreadPriority SUBTASK_PRIORITY = ThreadPriority.Lowest;

        private Scene m_Scene;
        private IConfigSource m_ConfigSource;
        private IConfig m_Config;
        private bool m_Enabled = false;

        private PhloxScriptLoader m_ScriptLoader;
        private PhloxExecutionScheduler m_ExeScheduler;
        private PhloxMasterScheduler m_MasterScheduler;
        private InWorldz.Phlox.Types.SupportedEventList m_EventList = new InWorldz.Phlox.Types.SupportedEventList();

        internal PhloxListenManager ListenManager { get; private set; }
        internal AsyncCommandManager AsyncCommands { get; private set; }
		internal StateManager StateManager { get; private set; }

        #region INonSharedRegionModule

        public string Name => "InWorldz.Phlox";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource config)
        {
            m_ConfigSource = config;
            m_Config = config.Configs["InWorldz.Phlox"];
            if (m_Config == null)
            {
                m_log.Info("[PhloxEngine]: No config section [InWorldz.Phlox] found, disabled");
                return;
            }
            m_Enabled = m_Config.GetBoolean("Enabled", false);
            m_log.InfoFormat("[PhloxEngine]: Enabled = {0}", m_Enabled);

            // Deploy-hygiene guard: Phlox is compiled against the tree's Library/C5.dll
            // (1.1 identity). If the runtime resolves a different C5 (e.g. a NuGet 3.x
            // copy leaks into the bin dir), scripts die at first timer use with
            // MissingMethodException. Surface the loaded identity loudly at startup so a
            // compile/runtime C5 split is caught here, not by script autopsies.
            m_log.InfoFormat("[PhloxEngine]: C5 loaded: version {0} from {1}",
                typeof(C5.IntervalHeap<int>).Assembly.GetName().Version,
                typeof(C5.IntervalHeap<int>).Assembly.Location);

            // SLua Tier-1 back-half proof: offline self-test invokable from the region console
            // ("phlox sluaproof"). Registered once (static guard) across regions. Additive; it
            // touches no scene/world state and is unrelated to normal script execution.
            if (!s_sluaProofCmdRegistered && MainConsole.Instance != null)
            {
                s_sluaProofCmdRegistered = true;
                MainConsole.Instance.Commands.AddCommand(
                    "Phlox", false, "phlox sluaproof",
                    "phlox sluaproof",
                    "Run the SLua Tier-1 back-half proof (assemble non-LSL bytecode, run, serialize, resume).",
                    HandleSluaProofCommand);
            }
        }

        private static bool s_sluaProofCmdRegistered = false;

        private void HandleSluaProofCommand(string module, string[] cmdparams)
        {
            MainConsole.Instance.Output(SluaBackHalfProof.Run());
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled) return;
            m_Scene = scene;
            m_Scene.RegisterModuleInterface<IScriptModule>(this);
            m_Scene.StackModuleInterface<IScriptModule>(this);
            m_log.InfoFormat("[PhloxEngine]: Added to region {0}", scene.RegionInfo.RegionName);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled) return;

            // IWorldComm must be resolved here (not AddRegion) because
            // WorldCommModule may not have registered yet during AddRegion.
            IWorldComm worldComm = scene.RequestModuleInterface<IWorldComm>();
            if (worldComm == null)
            {
                m_log.Error("[PhloxEngine]: No IWorldComm module found, script engine disabled");
                m_Enabled = false;
                return;
            }
            m_ExeScheduler = new PhloxExecutionScheduler(WorkArrived, this, worldComm);
            m_ScriptLoader = new PhloxScriptLoader(scene.AssetService, m_ExeScheduler, WorkArrived, this);
            m_MasterScheduler = new PhloxMasterScheduler(m_ExeScheduler, m_ScriptLoader);
            ListenManager = new PhloxListenManager(m_ExeScheduler);
            AsyncCommands = new AsyncCommandManager(this);
            StateManager = new StateManager(this);
            StateManager.Start();
            m_MasterScheduler.Start();

            m_Scene.EventManager.OnRezScript += OnRezScript;
            m_Scene.EventManager.OnRemoveScript += OnRemoveScript;
            m_Scene.EventManager.OnScriptReset += OnScriptReset;
            m_Scene.EventManager.OnStartScript += OnStartScript;
            m_Scene.EventManager.OnStopScript += OnStopScript;
            m_Scene.EventManager.OnGetScriptRunning += OnGetScriptRunning;
            m_Scene.EventManager.OnChatFromWorld += OnChatFromWorld;
            m_Scene.EventManager.OnChatFromClient += OnChatFromClient;
            m_Scene.EventManager.OnObjectGrab += OnObjectGrab;
            m_Scene.EventManager.OnObjectGrabbing += OnObjectGrabbing;
            m_Scene.EventManager.OnObjectDeGrab += OnObjectDeGrab;
            m_Scene.EventManager.OnScriptChangedEvent += OnScriptChangedEvent;
            m_Scene.EventManager.OnScriptControlEvent += OnScriptControlEvent;
			m_Scene.EventManager.OnShutdown += OnShutdown;
            m_Scene.EventManager.OnScriptColliderStart     += OnScriptColliderStart;
            m_Scene.EventManager.OnScriptColliding         += OnScriptColliding;
            m_Scene.EventManager.OnScriptCollidingEnd      += OnScriptCollidingEnd;
            m_Scene.EventManager.OnScriptLandColliderStart += OnScriptLandColliderStart;
            m_Scene.EventManager.OnScriptLandColliding     += OnScriptLandColliding;
            m_Scene.EventManager.OnScriptLandColliderEnd   += OnScriptLandColliderEnd;
            m_Scene.EventManager.OnAttach                  += OnAttach;
            m_Scene.EventManager.OnScriptMovingStartEvent  += OnScriptMovingStartEvent;
            m_Scene.EventManager.OnScriptMovingEndEvent    += OnScriptMovingEndEvent;
            m_Scene.EventManager.OnScriptAtTargetEvent       += OnScriptAtTargetEvent;
            m_Scene.EventManager.OnScriptNotAtTargetEvent    += OnScriptNotAtTargetEvent;
            m_Scene.EventManager.OnScriptAtRotTargetEvent    += OnScriptAtRotTargetEvent;
            m_Scene.EventManager.OnScriptNotAtRotTargetEvent += OnScriptNotAtRotTargetEvent;
            m_Scene.EventManager.OnObjectBeingRemovedFromScene += OnObjectBeingRemovedFromScene;
            IMoneyModule moneyModule = m_Scene.RequestModuleInterface<IMoneyModule>();
            if (moneyModule != null)
                moneyModule.OnObjectPaid += HandleObjectPaid;

            // Operator path for transient suspend/resume. There is NO viewer wire for
            // per-script suspend (Top Objects has Return/Kick/Refresh only; nothing in core
            // calls IScriptModule.SuspendScript), so the console is the actuator: Top Scripts
            // identifies the offender, these commands act on it. Registered per region
            // instance (INonSharedRegionModule) with the console-scene guard — the
            // ExperienceModule pattern.
            if (MainConsole.Instance != null)
            {
                MainConsole.Instance.Commands.AddCommand("Phlox", false,
                    "phlox suspend",
                    "phlox suspend <script-item-uuid | object-name>",
                    "Transiently pause Phlox script(s): timers/listens/state survive; no timeslices until 'phlox resume'. Not persisted — a region restart clears it. Does NOT touch the Running flag.",
                    HandleSuspendCommand);
                MainConsole.Instance.Commands.AddCommand("Phlox", false,
                    "phlox resume",
                    "phlox resume <script-item-uuid | object-name>",
                    "Resume script(s) paused by 'phlox suspend' (accumulated events then deliver).",
                    HandleResumeCommand);
            }

            m_log.InfoFormat("[PhloxEngine]: Region loaded {0}", scene.RegionInfo.RegionName);
        }

        // Matches the stock LandManagementModule / ExperienceModule guard: proceed only when
        // no region is selected (root) or the selected region is THIS instance's scene.
        private bool WrongConsoleScene()
        {
            return !(MainConsole.Instance.ConsoleScene is null
                     || MainConsole.Instance.ConsoleScene == m_Scene);
        }

        private void HandleSuspendCommand(string module, string[] args) => HandleSuspendResume(args, true);
        private void HandleResumeCommand(string module, string[] args) => HandleSuspendResume(args, false);

        private void HandleSuspendResume(string[] args, bool suspend)
        {
            if (WrongConsoleScene()) return;
            string verb = suspend ? "suspend" : "resume";
            if (args.Length < 3)
            {
                MainConsole.Instance.Output($"Usage: phlox {verb} <script-item-uuid | object-name>");
                return;
            }
            if (m_ExeScheduler == null)
            {
                MainConsole.Instance.Output("Script engine not running.");
                return;
            }

            string target = string.Join(" ", args, 2, args.Length - 2);

            // Direct script-item UUID (as printed by 'experience list-scripts').
            if (UUID.TryParse(target, out UUID itemId))
            {
                bool known = suspend
                    ? m_ExeScheduler.RequestSuspend(itemId)
                    : ApplyResume(itemId);
                MainConsole.Instance.Output(known
                    ? $"{(suspend ? "Suspended" : "Resumed")} script {itemId}."
                    : $"Script {itemId} is not running under Phlox in this region.");
                return;
            }

            // Object name — act on every Phlox script in matching objects.
            int hit = 0, missed = 0;
            foreach (var sog in m_Scene.GetSceneObjectGroups())
            {
                if (!string.Equals(sog.Name, target, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var part in sog.Parts)
                {
                    foreach (var item in part.Inventory.GetInventoryItems(InventoryType.LSL))
                    {
                        bool known = suspend
                            ? m_ExeScheduler.RequestSuspend(item.ItemID)
                            : ApplyResume(item.ItemID);
                        if (known) hit++; else missed++;
                    }
                }
            }
            if (hit == 0 && missed == 0)
                MainConsole.Instance.Output($"No object named '{target}' with scripts found in this region.");
            else
                MainConsole.Instance.Output(
                    $"{(suspend ? "Suspended" : "Resumed")} {hit} Phlox script(s) in '{target}'." +
                    (missed > 0 ? $" ({missed} script item(s) not run by Phlox — other engine or not loaded.)" : ""));
        }

        private bool ApplyResume(UUID itemId)
        {
            if (m_ExeScheduler.FindScript(itemId) == null) return false;
            m_ExeScheduler.RequestResume(itemId);
            return true;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_Enabled) return;
            m_Scene.EventManager.OnRezScript -= OnRezScript;
            m_Scene.EventManager.OnRemoveScript -= OnRemoveScript;
            m_Scene.EventManager.OnScriptReset -= OnScriptReset;
            m_Scene.EventManager.OnStartScript -= OnStartScript;
            m_Scene.EventManager.OnStopScript -= OnStopScript;
            m_Scene.EventManager.OnGetScriptRunning -= OnGetScriptRunning;
            m_Scene.EventManager.OnChatFromWorld -= OnChatFromWorld;
            m_Scene.EventManager.OnChatFromClient -= OnChatFromClient;
            m_Scene.EventManager.OnObjectGrab -= OnObjectGrab;
            m_Scene.EventManager.OnObjectGrabbing -= OnObjectGrabbing;
            m_Scene.EventManager.OnObjectDeGrab -= OnObjectDeGrab;
            m_Scene.EventManager.OnScriptChangedEvent -= OnScriptChangedEvent;
            m_Scene.EventManager.OnScriptControlEvent -= OnScriptControlEvent;
            IMoneyModule moneyModule = m_Scene.RequestModuleInterface<IMoneyModule>();
            if (moneyModule != null)
                moneyModule.OnObjectPaid -= HandleObjectPaid;
            m_Scene.EventManager.OnScriptNotAtRotTargetEvent -= OnScriptNotAtRotTargetEvent;
            m_Scene.EventManager.OnScriptAtRotTargetEvent    -= OnScriptAtRotTargetEvent;
            m_Scene.EventManager.OnScriptNotAtTargetEvent    -= OnScriptNotAtTargetEvent;
            m_Scene.EventManager.OnScriptAtTargetEvent       -= OnScriptAtTargetEvent;
            m_Scene.EventManager.OnScriptMovingEndEvent    -= OnScriptMovingEndEvent;
            m_Scene.EventManager.OnScriptMovingStartEvent  -= OnScriptMovingStartEvent;
            m_Scene.EventManager.OnAttach                  -= OnAttach;
            m_Scene.EventManager.OnScriptLandColliderEnd   -= OnScriptLandColliderEnd;
            m_Scene.EventManager.OnScriptLandColliding     -= OnScriptLandColliding;
            m_Scene.EventManager.OnScriptLandColliderStart -= OnScriptLandColliderStart;
            m_Scene.EventManager.OnScriptCollidingEnd      -= OnScriptCollidingEnd;
            m_Scene.EventManager.OnScriptColliding         -= OnScriptColliding;
            m_Scene.EventManager.OnScriptColliderStart     -= OnScriptColliderStart;
            m_Scene.EventManager.OnObjectBeingRemovedFromScene -= OnObjectBeingRemovedFromScene;
            LSLSystemAPI.ClearRegionCharacters(scene.RegionInfo.RegionID);
            m_MasterScheduler?.Stop();
            AsyncCommands?.Shutdown();
            m_Scene = null;
			
			StateManager?.Stop();
			StateManager = null;
        }

        public void Close() { }

        #endregion

        private void WorkArrived()
        {
            m_MasterScheduler?.WorkArrived();
        }

        #region Scene event handlers

        private void OnRezScript(uint localID, UUID itemID, string script,
            int startParam, bool postOnRez, string engine, int stateSource)
        {
            if (engine != Name) return;

            SceneObjectPart part = m_Scene.GetSceneObjectPart(localID);
            if (part == null)
            {
                m_log.ErrorFormat("[PhloxEngine]: OnRezScript: prim {0} not found for script {1}", localID, itemID);
                return;
            }

            m_log.DebugFormat("[PhloxEngine]: OnRezScript {0} in prim {1}", itemID, localID);

            m_ScriptLoader.PostLoadRequest(new PhloxLoadRequest
            {
                LocalID = localID,
                ItemID = itemID,
                ScriptText = script,
                StartParam = startParam,
                PostOnRez = postOnRez,
                StateSource = stateSource,
                Prim = part,
            });
        }

        private void OnRemoveScript(uint localID, UUID itemID)
        {
            m_log.DebugFormat("[PhloxEngine]: OnRemoveScript {0}", itemID);
            m_ScriptLoader.PostUnloadRequest(localID, itemID);
            OnScriptRemoved?.Invoke(itemID);
        }

        private void OnScriptReset(uint localID, UUID itemID)
        {
            m_ExeScheduler?.ResetScript(itemID);
        }

		private void OnShutdown()
        {
            m_log.Info("[PhloxEngine]: Shutdown event, flushing script state");
            StateManager?.Stop();
            StateManager = null;
        }

        private void OnObjectBeingRemovedFromScene(SceneObjectGroup obj)
        {
            // When a prim leaves the scene, clean up any character it owned (M-14b).
            // BotManager has no per-prim hook, so orphaned bots must be removed here.
            Scene scene = m_Scene;
            if (scene == null) return;
            IBotManager mgr = scene.RequestModuleInterface<IBotManager>();
            UUID regionID = scene.RegionInfo.RegionID;
            foreach (SceneObjectPart part in obj.Parts)
            {
                UUID botID = LSLSystemAPI.ClearCharacter(regionID, part.LocalId);
                if (botID != UUID.Zero)
                    mgr?.RemoveBot(botID, obj.OwnerID);
            }
        }

        private void OnStartScript(uint localID, UUID itemID)
        {
            m_ExeScheduler?.ChangeEnabledStatus(itemID, true);
        }

        private void OnStopScript(uint localID, UUID itemID)
        {
            m_ExeScheduler?.ChangeEnabledStatus(itemID, false);
        }

        private void OnGetScriptRunning(IClientAPI controllingClient, UUID objectID, UUID itemID)
        {
            if (m_ExeScheduler == null) return;
            // TODO: implement ScriptRunningReply when LindenCaps reference is available
        }

        private void OnChatFromWorld(object sender, OSChatMessage chat)
        {
            ListenManager?.DeliverChat(chat.Channel, chat.From, chat.SenderUUID, chat.Message);
        }

        private void OnChatFromClient(object sender, OSChatMessage chat)
        {
            // HandlerScriptDialogReply (LLClientView) sets chat.Sender but leaves
            // chat.SenderUUID at its UUID.Zero default.  A key-filtered llListen
            // (llListen(chan, "", ownerKey, "")) would never match because
            // DeliverChat compares FilterKey against speakerKey == UUID.Zero.
            // Fall back to the client's AgentId so dialog-button replies reach scripts.
            UUID speakerKey = chat.SenderUUID;
            if (speakerKey == UUID.Zero && chat.Sender != null)
                speakerKey = chat.Sender.AgentId;
            string speakerName = chat.From;
            if (string.IsNullOrEmpty(speakerName) && chat.Sender != null)
                speakerName = chat.Sender.Name;
            ListenManager?.DeliverChat(chat.Channel, speakerName, speakerKey, chat.Message);
        }

        // ── Touch events ───────────────────────────────────────────────────────

        private void OnObjectGrab(uint localID, uint originalID, Vector3 offsetPos,
            IClientAPI remoteClient, SurfaceTouchEventArgs surfaceArgs)
        {
            SceneObjectPart part = m_Scene?.GetSceneObjectPart(localID);
            if (part == null) return;

            var dp = BuildTouchDetectParams(part, remoteClient, offsetPos, surfaceArgs);

            PostTouchEvent(part.ParentGroup,
                InWorldz.Phlox.Types.SupportedEventList.Events.TOUCH_START,
                "touch_start", dp);
        }

        private void OnObjectGrabbing(uint localID, uint originalID, Vector3 offsetPos,
            IClientAPI remoteClient, SurfaceTouchEventArgs surfaceArgs)
        {
            SceneObjectPart part = m_Scene?.GetSceneObjectPart(localID);
            if (part == null) return;

            var dp = BuildTouchDetectParams(part, remoteClient, offsetPos, surfaceArgs);

            PostTouchEvent(part.ParentGroup,
                InWorldz.Phlox.Types.SupportedEventList.Events.TOUCH,
                "touch", dp);
        }

        private void OnObjectDeGrab(uint localID, uint originalID,
            IClientAPI remoteClient, SurfaceTouchEventArgs surfaceArgs)
        {
            SceneObjectPart part = m_Scene?.GetSceneObjectPart(localID);
            if (part == null) return;

            var dp = BuildTouchDetectParams(part, remoteClient, Vector3.Zero, surfaceArgs);

            PostTouchEvent(part.ParentGroup,
                InWorldz.Phlox.Types.SupportedEventList.Events.TOUCH_END,
                "touch_end", dp);
        }

        /// <summary>
        /// Builds a DetectParams for a touch event from the grabbing avatar's data.
        /// DetectParams uses LSL_Types for position/rotation/velocity, and exposes
        /// touch surface data only via the SurfaceTouchArgs write-only setter.
        /// </summary>
        private DetectParams BuildTouchDetectParams(SceneObjectPart part,
            IClientAPI remoteClient, Vector3 offsetPos, SurfaceTouchEventArgs surfaceArgs)
        {
            ScenePresence sp = m_Scene?.GetScenePresence(remoteClient.AgentId);

            var dp = new DetectParams
            {
                Key     = remoteClient.AgentId,
                Name    = remoteClient.Name,
                Owner   = remoteClient.AgentId,
                Group   = UUID.Zero,
                Type    = DetectParams.AGENT,
                LinkNum = part.LinkNum,
                OffsetPos = new LSL_Types.Vector3(
                    offsetPos.X, offsetPos.Y, offsetPos.Z),
                Position = sp != null
                    ? new LSL_Types.Vector3(
                        sp.AbsolutePosition.X,
                        sp.AbsolutePosition.Y,
                        sp.AbsolutePosition.Z)
                    : new LSL_Types.Vector3(),
                Velocity = sp != null
                    ? new LSL_Types.Vector3(
                        sp.Velocity.X,
                        sp.Velocity.Y,
                        sp.Velocity.Z)
                    : new LSL_Types.Vector3(),
                Rotation = sp != null
                    ? new LSL_Types.Quaternion(
                        sp.Rotation.X,
                        sp.Rotation.Y,
                        sp.Rotation.Z,
                        sp.Rotation.W)
                    : new LSL_Types.Quaternion(),
            };

            // SurfaceTouchArgs is a write-only setter that populates all the
            // read-only Touch* properties (TouchFace, TouchPos, TouchNormal, etc.)
            // Passing null resets them to safe defaults (-1 face, zero vectors).
            dp.SurfaceTouchArgs = surfaceArgs;

            return dp;
        }

        /// <summary>
        /// Posts a touch event to every script in the linkset that has that
        /// event handler registered in its current state.
        /// </summary>
        private void PostTouchEvent(SceneObjectGroup group,
            InWorldz.Phlox.Types.SupportedEventList.Events eventType,
            string eventName, DetectParams dp)
        {
            if (group == null || group.IsDeleted) return;

            var parms = new EventParams(
                eventName,
                new object[] { 1 },
                new DetectParams[] { dp });

            // Post to every prim in the linkset — scripts that don't handle
            // the event will have it dropped by FindEventHandler in the scheduler.
            foreach (SceneObjectPart part in group.Parts)
                PostObjectEvent(part.LocalId, parms);
        }

        // ── Changed event ──────────────────────────────────────────────────────

        // CHANGED_* constants matching LSL spec
        private const int CHANGED_INVENTORY  = 0x1;
        private const int CHANGED_COLOR      = 0x2;
        private const int CHANGED_SHAPE      = 0x4;
        private const int CHANGED_SCALE      = 0x8;
        private const int CHANGED_TEXTURE    = 0x10;
        private const int CHANGED_LINK       = 0x20;
        private const int CHANGED_ALLOWED_DROP = 0x40;
        private const int CHANGED_OWNER      = 0x80;
        private const int CHANGED_REGION     = 0x100;
        private const int CHANGED_TELEPORT   = 0x200;
        private const int CHANGED_REGION_START = 0x400;
        private const int CHANGED_MEDIA      = 0x800;

        private static readonly DetectParams[] s_emptyDetectParams = Array.Empty<DetectParams>();

        private void OnScriptChangedEvent(uint localID, uint change, object data)
        {
            // Delivers changed() events fired by OpenSim's own infrastructure:
            // CHANGED_LINK (sit/stand/link/unlink), CHANGED_SCALE, CHANGED_SHAPE, etc.
            // The localID is the specific part that changed — post only to that part's scripts.
            var parms = new EventParams("changed",
                new object[] { (int)change },
                s_emptyDetectParams);
            PostObjectEvent(localID, parms);
        }

        // ── Collision events ───────────────────────────────────────────────────

        private void OnScriptColliderStart(uint localID, ColliderArgs col)
        {
            int dc = col.Colliders.Count;
            if (dc == 0) return;
            DetectParams[] det = new DetectParams[dc];
            int i = 0;
            foreach (DetectedObject detobj in col.Colliders)
            {
                DetectParams d = new DetectParams();
                d.Key = detobj.keyUUID;
                d.Populate(m_Scene, detobj);
                det[i++] = d;
            }
            PostObjectEvent(localID, new EventParams("collision_start", new object[] { dc }, det));
        }

        private void OnScriptColliding(uint localID, ColliderArgs col)
        {
            int dc = col.Colliders.Count;
            if (dc == 0) return;
            DetectParams[] det = new DetectParams[dc];
            int i = 0;
            foreach (DetectedObject detobj in col.Colliders)
            {
                DetectParams d = new DetectParams();
                d.Key = detobj.keyUUID;
                d.Populate(m_Scene, detobj);
                det[i++] = d;
            }
            PostObjectEvent(localID, new EventParams("collision", new object[] { dc }, det));
        }

        private void OnScriptCollidingEnd(uint localID, ColliderArgs col)
        {
            int dc = col.Colliders.Count;
            if (dc == 0) return;
            DetectParams[] det = new DetectParams[dc];
            int i = 0;
            foreach (DetectedObject detobj in col.Colliders)
            {
                DetectParams d = new DetectParams();
                d.Key = detobj.keyUUID;
                d.Populate(m_Scene, detobj);
                det[i++] = d;
            }
            PostObjectEvent(localID, new EventParams("collision_end", new object[] { dc }, det));
        }

        // ── Land collision events ──────────────────────────────────────────────

        private void OnScriptLandColliderStart(uint localID, ColliderArgs col)
        {
            foreach (DetectedObject detobj in col.Colliders)
                PostObjectEvent(localID, new EventParams(
                    "land_collision_start", new object[] { detobj.posVector }, s_emptyDetectParams));
        }

        private void OnScriptLandColliding(uint localID, ColliderArgs col)
        {
            foreach (DetectedObject detobj in col.Colliders)
                PostObjectEvent(localID, new EventParams(
                    "land_collision", new object[] { detobj.posVector }, s_emptyDetectParams));
        }

        private void OnScriptLandColliderEnd(uint localID, ColliderArgs col)
        {
            foreach (DetectedObject detobj in col.Colliders)
                PostObjectEvent(localID, new EventParams(
                    "land_collision_end", new object[] { detobj.posVector }, s_emptyDetectParams));
        }

        // ── Attach / Detach ────────────────────────────────────────────────────

        private void OnAttach(uint localID, UUID itemID, UUID avatarID)
        {
            PostObjectEvent(localID, new EventParams(
                "attach", new object[] { avatarID.ToString() },
                s_emptyDetectParams));
        }

        // ── Moving events ──────────────────────────────────────────────────────

        private void OnScriptMovingStartEvent(uint localID)
        {
            PostObjectEvent(localID, new EventParams(
                "moving_start", new object[0],
                s_emptyDetectParams));
        }

        private void OnScriptMovingEndEvent(uint localID)
        {
            PostObjectEvent(localID, new EventParams(
                "moving_end", new object[0],
                s_emptyDetectParams));
        }

        // ── Target events ──────────────────────────────────────────────────────

        private void OnScriptAtTargetEvent(UUID scriptID, uint handle, Vector3 targetpos, Vector3 atpos)
        {
            PostScriptEvent(scriptID, new EventParams(
                "at_target", new object[] { (int)handle, targetpos, atpos },
                s_emptyDetectParams));
        }

        private void OnScriptNotAtTargetEvent(UUID scriptID)
        {
            PostScriptEvent(scriptID, new EventParams(
                "not_at_target", new object[0],
                s_emptyDetectParams));
        }

        private void OnScriptAtRotTargetEvent(UUID scriptID, uint handle, Quaternion targetrot, Quaternion atrot)
        {
            PostScriptEvent(scriptID, new EventParams(
                "at_rot_target", new object[] { (int)handle, targetrot, atrot },
                s_emptyDetectParams));
        }

        private void OnScriptNotAtRotTargetEvent(UUID scriptID)
        {
            PostScriptEvent(scriptID, new EventParams(
                "not_at_rot_target", new object[0],
                s_emptyDetectParams));
        }

        // ── Money event ────────────────────────────────────────────────────────
        // Dormant until a real IMoneyModule that fires OnObjectPaid is deployed.
        // SampleMoneyModule declares the event but never invokes it.

        private void HandleObjectPaid(UUID objectID, UUID agentID, int amount)
        {
            SceneObjectPart part = m_Scene.GetSceneObjectPart(objectID);
            if (part == null) return;

            if ((part.ScriptEvents & scriptEvents.money) == 0)
                part = part.ParentGroup.RootPart;

            if (part == null) return;

            DetectParams[] det = new DetectParams[1];
            det[0] = new DetectParams();
            det[0].Key = agentID;
            det[0].Populate(m_Scene);

            PostObjectEvent(part.LocalId, new EventParams(
                "money", new object[] { agentID.ToString(), amount },
                det));
        }

        #endregion

        #region IScriptModule

        public string ScriptEngineName => Name;

        public event ScriptRemoved OnScriptRemoved;
        public event ObjectRemoved OnObjectRemoved;

        public string GetXMLState(UUID itemID) => string.Empty;
        public bool SetXMLState(UUID itemID, string xml) => false;

        public bool PostScriptEvent(UUID itemID, string name, object[] args)
            => PostScriptEvent(itemID, new EventParams(name, args, null));

        public bool PostObjectEvent(UUID localID, string name, object[] args)
            => false;

        public bool PostScriptEvent(UUID itemID, EventParams parms)
        {
            if (m_ExeScheduler == null) return false;

            if (!m_EventList.HasEventByName(parms.EventName)) return false;
            InWorldz.Phlox.Types.FunctionSig eventInfo = m_EventList.GetEventByName(parms.EventName);

            InWorldz.Phlox.VM.DetectVariables[] detectVars = ConvertDetectParams(parms.DetectParams);

            var evt = new InWorldz.Phlox.VM.PostedEvent
            {
                EventType = (InWorldz.Phlox.Types.SupportedEventList.Events)eventInfo.TableIndex,
                Args = parms.Params,
                DetectVars = detectVars
            };
            evt.Normalize();
            m_ExeScheduler.PostEvent(itemID, evt);
            return true;
        }

        private void OnScriptControlEvent(UUID itemID, UUID agentID, uint held, uint change)
        {
            PostScriptEvent(itemID, new EventParams(
                "control", new object[] {
                    agentID.ToString(),
                    (int)held,
                    (int)change },
                null));
        }

        public bool PostObjectEvent(uint localID, EventParams parms)
        {
            SceneObjectPart part = World?.GetSceneObjectPart(localID);
            if (part == null) return false;

            // Defer the inventory snapshot and event dispatch to a thread pool work item.
            //
            // This method can be invoked synchronously from inside callers that hold a
            // write lock on part.TaskInventory's underlying ReaderWriterLockSlim — most
            // notably OpenSim.Region.Framework.Scenes.EventManager.TriggerOnScriptChangedEvent,
            // which fires when prim inventory mutates (script add/remove, notecard save).
            //
            // TaskInventoryDictionary.Clone() acquires a read lock on that same
            // ReaderWriterLockSlim internally. In the default (non-recursive) policy,
            // ReaderWriterLockSlim throws LockRecursionException when the same thread
            // attempts a read while already holding the write lock. That manifested as:
            //   "A read lock may not be acquired with the write lock held in this mode"
            // inside [EVENT MANAGER]: Delegate for TriggerOnScriptChangedEvent failed.
            //
            // Hopping to the thread pool guarantees the original caller has released
            // its write lock by the time Clone() runs. The trade-off: this method now
            // returns true *before* events are actually delivered. No current caller
            // (OnScriptChangedEvent, OnSceneObjectPartUpdated, PostTouchEvent,
            //  PostObjectLinksetDataEvent) inspects the return value, so this is safe.
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                try
                {
                    TaskInventoryDictionary scripts;
                    lock (part.TaskInventory)
                        scripts = (TaskInventoryDictionary)part.TaskInventory.Clone();

                    foreach (var kvp in scripts)
                    {
                        if (kvp.Value.Type == (int)AssetType.LSLText || kvp.Value.Type == 10)
                            PostScriptEvent(kvp.Value.ItemID, parms);
                    }
                }
                catch (Exception e)
                {
                    // Unhandled exceptions in ThreadPool work items terminate the
                    // process on .NET Core/5+. Swallow and log instead.
                    m_log.ErrorFormat(
                        "[PhloxEngine]: PostObjectEvent deferred dispatch failed for localID {0}: {1}",
                        localID, e);
                }
            }, null);

            return true;
        }

        public bool PostObjectLinksetDataEvent(uint localID, int action,
            ReadOnlySpan<char> name, ReadOnlySpan<char> value)
        {
            var parms = new EventParams("linkset_data",
                new object[] { action, name.ToString(), value.ToString() }, null);

            // SL fires linkset_data in EVERY script in the linkset, not only the prim that changed
            // the store. Fan out to all parts of the group (each PostObjectEvent dispatches to that
            // part's scripts). Fall back to the single part if the group can't be resolved.
            SceneObjectPart part = World?.GetSceneObjectPart(localID);
            SceneObjectGroup group = part?.ParentGroup;
            if (group == null)
                return PostObjectEvent(localID, parms);

            bool any = false;
            foreach (SceneObjectPart p in group.Parts)
                any |= PostObjectEvent(p.LocalId, parms);
            return any;
        }

        public System.Collections.ArrayList GetScriptErrors(UUID itemID) => new System.Collections.ArrayList();
        public bool HasScript(UUID itemID, out bool running) { running = false; return false; }
        public void SaveAllState() { }
        public void StartProcessing()
        {
            // Phlox compiles asynchronously (OnRezScript enqueues; PhloxScriptLoader
            // compiles on a worker thread), so unlike YEngine the boot batch is NOT
            // complete at this point. We fire unconditionally anyway: RegionReadyModule
            // holds LoginLock until this signal arrives, and gating it on an async
            // drain barrier risks never firing at all, which is the exact defect this
            // fixes. Logins may open slightly before the last script finishes
            // compiling. Do not "correct" this to wait for the queue.
            if (m_Scene == null || m_Scene.EventManager == null)
                return;

            m_Scene.EventManager.TriggerEmptyScriptCompileQueue(0, string.Empty);
            m_log.Info("[PhloxEngine]: StartProcessing fired TriggerEmptyScriptCompileQueue(0) — RegionReady LoginLock release signal");
        }
        public float GetScriptExecutionTime(List<UUID> itemIDs)
        {
            if (m_ExeScheduler == null || itemIDs == null) return 0f;
            float time = 0f;
            foreach (UUID itemID in itemIDs)
            {
                var s = m_ExeScheduler.FindScript(itemID);
                if (s != null && s.ScriptState.Enabled)
                    time += (float)s.GetExecutionTime();
            }
            return time;
        }

        public Dictionary<uint, float> GetObjectScriptsExecutionTimes()
        {
            Dictionary<uint, float> topScripts = new Dictionary<uint, float>();
            if (m_ExeScheduler == null) return topScripts;
            foreach (var st in m_ExeScheduler.SnapshotScriptStats())
            {
                uint root = ResolveRootLocalId(st.HostLocalId);
                if (root == 0) continue;
                topScripts.TryGetValue(root, out float t);
                topScripts[root] = t + (float)st.ExecMs;
            }
            return topScripts;
        }
        // Transient suspend (Suspend/Resume Slice 2): pauses timeslice delivery only —
        // listens/timers/state survive, the Running flag is untouched, and nothing is
        // persisted (region restart clears it). Returns false for scripts this engine
        // doesn't run, so a multi-engine caller can try the next engine.
        public bool SuspendScript(UUID itemID)
            => m_ExeScheduler != null && m_ExeScheduler.RequestSuspend(itemID);

        // Returning TRUE for unknown scripts is deliberate (fe31bac769): Phlox scripts are
        // never rez-suspended, so "not suspended" IS success — returning false made
        // SceneObjectPartInventory.ResumeScripts() `continue` past the changed(CHANGED_OWNER)
        // post, swallowing that event on ownership transfer. Known suspended scripts now
        // actually resume (RequestResume is a cheap no-op for non-suspended ones).
        public bool ResumeScript(UUID itemID)
        {
            m_ExeScheduler?.RequestResume(itemID);
            return true;
        }
        public int GetScriptsMemory(List<UUID> itemIDs)
        {
            if (m_ExeScheduler == null || itemIDs == null) return 0;
            int memory = 0;
            foreach (UUID itemID in itemIDs)
            {
                var s = m_ExeScheduler.FindScript(itemID);
                if (s != null && s.ScriptState.Enabled)
                    memory += s.ScriptState.MemInfo.MemoryUsed;
            }
            return memory;
        }

        public ICollection<ScriptTopStatsData> GetTopObjectStats(float mintime, int minmemory,
            out float totaltime, out float totalmemory)
        {
            totaltime = 0f;
            totalmemory = 0f;
            Dictionary<uint, ScriptTopStatsData> topScripts = new Dictionary<uint, ScriptTopStatsData>();
            if (m_ExeScheduler == null) return topScripts.Values;

            foreach (var st in m_ExeScheduler.SnapshotScriptStats())
            {
                uint root = ResolveRootLocalId(st.HostLocalId);
                if (root == 0) continue;

                float time = (float)st.ExecMs;
                int mem = st.MemoryUsed;
                totaltime += time;
                totalmemory += mem;

                if (time > mintime || mem > minmemory)
                {
                    if (topScripts.TryGetValue(root, out ScriptTopStatsData sd))
                    {
                        sd.time += time;
                        sd.memory += mem;
                    }
                    else
                    {
                        topScripts[root] = new ScriptTopStatsData
                        {
                            localID = root,
                            time = time,
                            memory = mem
                        };
                    }
                }
            }
            return topScripts.Values;
        }

        // Resolve a host prim LocalId to its linkset root LocalId (off the hot path).
        private uint ResolveRootLocalId(uint hostLocalId)
        {
            if (hostLocalId == 0 || m_Scene == null) return 0;
            SceneObjectPart part = m_Scene.GetSceneObjectPart(hostLocalId);
            SceneObjectGroup grp = part?.ParentGroup;
            if (grp == null || grp.IsDeleted) return 0;
            return grp.RootPart.LocalId;
        }

        #endregion

        #region IScriptEngine

        public Scene World => m_Scene;
        public IScriptModule ScriptModule => this;
        public IConfig Config => m_Config;
        public IConfigSource ConfigSource => m_ConfigSource;
        public string ScriptEnginePath => "ScriptEngines/Phlox";
        public string ScriptClassName => "PhloxScript";
        public string ScriptBaseClassName => "InWorldz.Phlox.VM.Interpreter";
        public string[] ScriptReferencedAssemblies => Array.Empty<string>();
        public ParameterInfo[] ScriptBaseClassParameters => null;

        public IScriptWorkItem QueueEventHandler(object parms) => null;
        public void CancelScriptEvent(UUID itemID, string eventName) { }

        public DetectParams GetDetectParams(UUID item, int number) => null;
        public void SetMinEventDelay(UUID itemID, double delay) { }
        public int GetStartParameter(UUID itemID) => 0;

        public void SetScriptState(UUID itemID, bool state, bool self)
            => m_ExeScheduler?.ChangeEnabledStatus(itemID, state);

        public bool GetScriptState(UUID itemID)
            => m_ExeScheduler?.GetScriptRunning(itemID) ?? false;

        public void SetState(UUID itemID, string newState) { }

        public void ApiResetScript(UUID itemID)
            => m_ExeScheduler?.ResetNow(itemID);

        public void ResetScript(UUID itemID)
            => m_ExeScheduler?.ResetScript(itemID);

        public void SleepScript(UUID itemID, int delay) { }

        public IScriptApi GetApi(UUID itemID, string name) => null;

        #endregion

        #region Internal helpers used by LSLSystemAPI

        /// <summary>
        /// Called by LSLSystemAPI.state() to trigger a state change.
        /// State changes are driven by the VM via OnStateChg — this is a no-op at the engine level.
        /// </summary>
        public void SetStateInternal(UUID itemID, string newState) { }

        /// <summary>
        /// Called by LSLSystemAPI when a long-running syscall completes.
        /// </summary>
        public void SysReturn(UUID itemId, object retValue, int delay)
            => m_ExeScheduler?.PostSyscallReturn(itemId, retValue, delay);

        /// <summary>
        /// Called by LSLSystemAPI.llSetTimerEvent.
        /// </summary>
        public void SetTimerEvent(uint localID, UUID itemID, float sec)
            => m_ExeScheduler?.SetTimer(itemID, sec);

        #endregion

        #region Helpers

        private InWorldz.Phlox.VM.DetectVariables[] ConvertDetectParams(DetectParams[] parms)
        {
            if (parms == null) return Array.Empty<InWorldz.Phlox.VM.DetectVariables>();

            var result = new InWorldz.Phlox.VM.DetectVariables[parms.Length];
            for (int i = 0; i < parms.Length; i++)
            {
                result[i] = new InWorldz.Phlox.VM.DetectVariables
                {
                    Key          = parms[i].Key.ToString(),
                    Group        = parms[i].Group.ToString(),
                    LinkNumber   = parms[i].LinkNum,
                    Name         = parms[i].Name,
                    Owner        = parms[i].Owner.ToString(),
                    Pos          = parms[i].Position,
                    Rot          = parms[i].Rotation,
                    Type         = parms[i].Type,
                    Vel          = parms[i].Velocity,
                    Grab         = parms[i].OffsetPos,
                    TouchBinormal= parms[i].TouchBinormal,
                    TouchFace    = parms[i].TouchFace,
                    TouchNormal  = parms[i].TouchNormal,
                    TouchPos     = parms[i].TouchPos,
                    TouchST      = parms[i].TouchST,
                    TouchUV      = parms[i].TouchUV,
                };
            }
            return result;
        }

        #endregion
    }
}
