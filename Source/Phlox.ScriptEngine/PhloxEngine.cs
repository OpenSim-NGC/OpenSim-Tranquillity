/*
 * Phlox Script Engine Integration
 * Adapted from InWorldz Halcyon EngineInterface.cs
 * Copyright (c) InWorldz Halcyon Developers (original)
 * Adapted 2026 by Legion Builds for OpenSim 0.9.3 .NET 8
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Services.Interfaces;

using Microsoft.Extensions.Logging;

namespace Phlox.ScriptEngine
{
    public delegate void WorkArrivedDelegate();

    // No Mono.Addins [assembly: Addin]/[Extension] registration: develop discovers
    // region modules by interface reflection (IPluginDiscovery scans for
    // INonSharedRegionModule implementers), same as the other engine modules.
    /// <summary>Where syscalls that can reach a service run.</summary>
    public enum ServiceCallDeferralMode { Auto, Always, Never }

    public class PhloxEngine : INonSharedRegionModule, IScriptEngine, IScriptModule, IParcelScriptPolicyEngine
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

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

        public string Name => PhloxEngineHeader.PhloxName;
        public Type ReplaceableInterface => null;

        /// <summary>
        /// The shipped floor for <c>llSetTimerEvent</c>, in seconds. Matches
        /// <c>OpenSimDefaults.ini</c> <c>[YEngine] MinTimerInterval = 0.1</c>, which is what this grid
        /// already applies to the other engine.
        /// </summary>
        public const float DefaultMinTimerInterval = 0.1f;

        /// <summary>
        /// The floor a positive <c>llSetTimerEvent</c> request is raised to, in seconds. Config key
        /// <c>MinTimerInterval</c> in <c>[InWorldz.Phlox]</c>; 0 disables the floor entirely.
        /// </summary>
        public float MinTimerInterval { get; private set; } = DefaultMinTimerInterval;

        /// <summary>[InWorldz.Phlox] AllowGodFunctions if set, else YEngine's [YEngine] AllowGodFunctions (default false).</summary>
        public bool AllowGodFunctions { get; private set; }

        /// <summary>
        /// YEngine's [YEngine] AutomaticLinkPermission (default false; LSL_Api.LoadConfig, LSL_Api.cs:519), read
        /// from the same key so one setting means the same for both engines: when true, llCreateLink and llBreakLink need no
        /// PERMISSION_CHANGE_LINKS and llGetPermissions reports it (as YEngine and Halcyon).
        /// </summary>
        public bool AutomaticLinkPermission { get; private set; }

        /// <summary>
        /// Halcyon's reset throttle (LSLSystemAPI.ThrottleScriptResets): more than 5 resets of one script in one
        /// second puts it to sleep for 5 s, with a warning once an hour. [InWorldz.Phlox] ResetThrottle, default true: one of
        /// Halcyon's anti-abuse slowdowns, restored on by default with an operator setting, as the others are.
        /// </summary>
        public bool ResetThrottle { get; private set; } = true;

        // The rest of Halcyon's anti-abuse slowdowns, each on by default under its own [InWorldz.Phlox] key;
        // false is exactly the behaviour without them. The rules and their Halcyon sources are on the LSLSystemAPI helpers.

        /// <summary>15 ms after llSay, llShout, llWhisper, llRegionSay, llRegionSayTo and llOwnerSay. [InWorldz.Phlox] ChatThrottle.</summary>
        public bool ChatThrottle { get; private set; } = true;
        /// <summary>15 ms after the bot chat, typing, sit, stand and touch calls. [InWorldz.Phlox] BotThrottle.</summary>
        public bool BotThrottle { get; private set; } = true;
        /// <summary>Halcyon's PhySleep on the physics setters. [InWorldz.Phlox] PhysicsThrottle.</summary>
        public bool PhysicsThrottle { get; private set; } = true;
        /// <summary>50 ms back-pressure on llMessageLinked / botMessageLinked. [InWorldz.Phlox] LinkMessageThrottle.</summary>
        public bool LinkMessageThrottle { get; private set; } = true;
        /// <summary>Halcyon's notecard read delays. [InWorldz.Phlox] NotecardThrottle.</summary>
        public bool NotecardThrottle { get; private set; } = true;
        /// <summary>The parsed-notecard cache (Halcyon's NotecardCache). [InWorldz.Phlox] NotecardCache.</summary>
        public bool NotecardCacheEnabled { get; private set; } = true;
        /// <summary>iwFormatString's 100 ms. [InWorldz.Phlox] FormatStringThrottle.</summary>
        public bool FormatStringThrottle { get; private set; } = true;
        /// <summary>
        /// The HTTP in-flight caps (Halcyon's, 10 per object and 200 per region, a refused llHTTPRequest
        /// gives NULL_KEY after 80 ms). [InWorldz.Phlox] HttpInFlightThrottle, default true; false removes both caps and
        /// the 80 ms.
        /// </summary>
        public bool HttpInFlightThrottle { get; private set; } = true;

        /// <summary>This region's notecard cache (used only while <see cref="NotecardCacheEnabled"/>).</summary>
        internal PhloxNotecardCache NotecardCache { get; } = new PhloxNotecardCache();

        /// <summary>
        /// Halcyon's PhysicsScene.SimulationFrameTimeAvg - a MovingIntegerAverage(10) of the physics frame time
        /// (InWorldz.PhysxPhysics/PhysxScene.cs:134, 192-197, 413). Fed once per heartbeat frame from the scene's own
        /// timings (UpdatePhysics + UpdatePreparePhysics, which the sim stats add up as the physics ms), while
        /// PhysicsThrottle is on.
        /// </summary>
        private readonly MovingIntegerAverage m_PhysicsFrameTimes = new MovingIntegerAverage(10);
        internal int PhysicsFrameTimeAvg => m_PhysicsFrameTimes.CalculateAverage();

        private void OnFrameForPhysicsTime()
        {
            Scene scene = m_Scene;
            if (scene == null) return;
            m_PhysicsFrameTimes.AddValue(scene.MonitorPhysicsUpdateTime + scene.MonitorPhysicsSyncTime);
        }

        /// <summary>
        /// Halcyon's EngineInterface.GetEventQueueFreeSpacePercentage (EngineInterface.cs:814-824): 1.0 for a
        /// script this engine does not run, 0 when the queue is full, else 1 - queued / MAX_EVENT_QUEUE_SIZE.
        /// </summary>
        internal float GetEventQueueFreeSpacePercentage(UUID itemID)
        {
            InWorldz.Phlox.VM.Interpreter script = m_ExeScheduler?.FindScript(itemID);
            if (script == null) return 1.0f;
            int queued;
            lock (script.ScriptState.EventQueueLock) queued = script.ScriptState.EventQueue.Count;
            if (queued >= InWorldz.Phlox.VM.RuntimeState.MAX_EVENT_QUEUE_SIZE) return 0.0f;
            return 1.0f - (float)queued / InWorldz.Phlox.VM.RuntimeState.MAX_EVENT_QUEUE_SIZE;
        }

        /// <summary>The [OSSL] permission gate, read from the same config YEngine reads.</summary>
        internal OsslGate Ossl { get; private set; } = new OsslGate(null);

        public void Initialise(IConfigSource config)
        {
            m_ConfigSource = config;
            Ossl = new OsslGate(config);
            m_Config = config.Configs["InWorldz.Phlox"];
            if (m_Config == null)
            {
                m_log.LogInformation("[PhloxEngine]: No config section [InWorldz.Phlox] found, disabled");
                return;
            }
            m_Enabled = m_Config.GetBoolean("Enabled", false);
            m_log.LogInformation("[PhloxEngine]: Enabled = {0}", m_Enabled);

            // The floor for llSetTimerEvent. Same key name and same default as the
            // other engine on this grid, so an operator sets one number and both agree:
            // LSL_Api.llSetTimerEvent clamps at m_MinTimerInterval (LSL_Api.cs:4005-4011) and
            // OpenSimDefaults.ini ships [YEngine] MinTimerInterval = 0.1. Neither SL nor
            // InWorldz clamps at all - the SL wiki documents no minimum, and Halcyon assigns
            // TimerInterval = (int)(sec * 1000) with none - so 0.1 is this grid's number, not
            // an upstream-of-Phlox one, and it is written down here rather than inferred.
            MinTimerInterval = m_Config.GetFloat("MinTimerInterval", DefaultMinTimerInterval);
            // Chat ranges for Phlox listens: the region's [Chat] distances, read with the same keys and
            // defaults as the chat module, WorldComm and YEngine, so every listener hears the same range.
            IConfig chatConfig = config.Configs["Chat"];
            if (chatConfig != null)
            {
                m_WhisperDistance = chatConfig.GetInt("whisper_distance", m_WhisperDistance);
                m_SayDistance = chatConfig.GetInt("say_distance", m_SayDistance);
                m_ShoutDistance = chatConfig.GetInt("shout_distance", m_ShoutDistance);
            }
            // Listen caps from [LL-Functions] max_listens_per_script / max_listens_per_region, read as the
            // core WorldCommModule reads them for YEngine, so both engines take one config value the same way.
            (m_MaxListensPerScript, m_MaxListensPerRegion) = PhloxListenManager.ReadListenCaps(config, out string listenCapWarning);
            if (listenCapWarning != null) m_log.LogWarning("[PhloxEngine]: {0}", listenCapWarning);
            m_log.LogInformation("[PhloxEngine]: max_listens_per_script = {0}, max_listens_per_region = {1}",
                m_MaxListensPerScript == int.MaxValue ? "no limit" : m_MaxListensPerScript.ToString(),
                m_MaxListensPerRegion == int.MaxValue ? "no limit" : m_MaxListensPerRegion.ToString());
            // YEngine's switch for god functions (llSetInventoryPermMask and llSetObjectPermMask), off by
            // default. Read as YEngine reads it - [YEngine] AllowGodFunctions, default false (LSL_Api.LoadConfig
            // takes it from m_ScriptEngine.Config, which is config.Configs["YEngine"]) - so one value gates both engines.
            // An [InWorldz.Phlox] AllowGodFunctions, where set, still wins, as it did before.
            AllowGodFunctions = m_Config.GetBoolean("AllowGodFunctions",
                config.Configs["YEngine"]?.GetBoolean("AllowGodFunctions", false) ?? false);
            AutomaticLinkPermission = config.Configs["YEngine"]?.GetBoolean("AutomaticLinkPermission", false) ?? false;
            if (MinTimerInterval < 0f) MinTimerInterval = 0f;
            m_log.LogInformation("[PhloxEngine]: MinTimerInterval = {0}s", MinTimerInterval);
            ResetThrottle = m_Config.GetBoolean("ResetThrottle", true);
            // Halcyon's anti-abuse slowdowns, on by default; one line per region with every value.
            ChatThrottle = m_Config.GetBoolean("ChatThrottle", true);
            BotThrottle = m_Config.GetBoolean("BotThrottle", true);
            PhysicsThrottle = m_Config.GetBoolean("PhysicsThrottle", true);
            LinkMessageThrottle = m_Config.GetBoolean("LinkMessageThrottle", true);
            NotecardThrottle = m_Config.GetBoolean("NotecardThrottle", true);
            NotecardCacheEnabled = m_Config.GetBoolean("NotecardCache", true);
            FormatStringThrottle = m_Config.GetBoolean("FormatStringThrottle", true);
            HttpInFlightThrottle = m_Config.GetBoolean("HttpInFlightThrottle", true);
            m_log.LogInformation("[PhloxEngine]: Anti-abuse slowdowns: ResetThrottle = {0}, ChatThrottle = {1}, BotThrottle = {2}, " +
                "PhysicsThrottle = {3}, LinkMessageThrottle = {4}, NotecardThrottle = {5}, NotecardCache = {6}, FormatStringThrottle = {7}, " +
                "HttpInFlightThrottle = {8}",
                ResetThrottle, ChatThrottle, BotThrottle, PhysicsThrottle, LinkMessageThrottle, NotecardThrottle,
                NotecardCacheEnabled, FormatStringThrottle, HttpInFlightThrottle);

            // Syscalls that can reach a service run off the scheduler thread.
            // auto (default) = inline when the answer is local or cached, deferred otherwise;
            // always = defer every such call; never = everything inline.
            string deferral = m_Config.GetString("ServiceCallDeferral", "auto").Trim().ToLowerInvariant();
            ServiceCallDeferral = deferral switch
            {
                "always" => ServiceCallDeferralMode.Always,
                "never" => ServiceCallDeferralMode.Never,
                _ => ServiceCallDeferralMode.Auto,
            };
            m_log.LogInformation("[PhloxEngine]: ServiceCallDeferral = {0}", ServiceCallDeferral);

            // Deploy-hygiene guard: Phlox is compiled against the tree's Library/C5.dll
            // (1.1 identity). If the runtime resolves a different C5 (e.g. a NuGet 3.x
            // copy leaks into the bin dir), scripts die at first timer use with
            // MissingMethodException. Surface the loaded identity loudly at startup so a
            // compile/runtime C5 split is caught here, not by script autopsies.
            m_log.LogInformation("[PhloxEngine]: C5 loaded: version {0} from {1}",
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
            m_log.LogInformation("[PhloxEngine]: Added to region {0}", scene.RegionInfo.RegionName);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled) return;

            // IWorldComm must be resolved here (not AddRegion) because
            // WorldCommModule may not have registered yet during AddRegion.
            IWorldComm worldComm = scene.RequestModuleInterface<IWorldComm>();
            if (worldComm == null)
            {
                m_log.LogError("[PhloxEngine]: No IWorldComm module found, script engine disabled");
                m_Enabled = false;
                return;
            }
            m_WorldComm = worldComm;
            m_ExeScheduler = new PhloxExecutionScheduler(WorkArrived, this, worldComm);
            m_ScriptLoader = new PhloxScriptLoader(scene.AssetService, m_ExeScheduler, WorkArrived, this);
            m_MasterScheduler = new PhloxMasterScheduler(m_ExeScheduler, m_ScriptLoader);
            ListenManager = new PhloxListenManager(m_ExeScheduler, scene,
                m_WhisperDistance, m_SayDistance, m_ShoutDistance, m_MaxListensPerScript, m_MaxListensPerRegion);
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
            m_Scene.EventManager.OnChatBroadcast += OnChatBroadcast;
            m_WorldComm.OnMessageDelivered += OnWorldCommMessage;
            m_Scene.EventManager.OnObjectGrab += OnObjectGrab;
            m_Scene.EventManager.OnObjectGrabbing += OnObjectGrabbing;
            m_Scene.EventManager.OnObjectDeGrab += OnObjectDeGrab;
            m_Scene.EventManager.OnScriptChangedEvent += OnScriptChangedEvent;
            m_Scene.EventManager.OnAvatarKilled += OnAvatarKilled;   // on_death
            m_Scene.EventManager.OnAvatarDamage += OnAvatarDamage;   // on_damage (synchronous)
            m_Scene.EventManager.OnAvatarDamageApplied += OnAvatarDamageApplied;   // final_damage
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
            // The triggers for the No Scripts parcel check (the scene's parcel-crossing events and the land events)
            m_Scene.EventManager.OnGroupCrossedToNewParcel   += OnGroupCrossedToNewParcel;
            m_Scene.EventManager.OnObjectOwnerOrGroupChanged += OnObjectOwnerOrGroupChanged;
            m_Scene.EventManager.OnLandObjectAdded           += OnLandObjectChanged;
            m_Scene.EventManager.OnScriptControlsReleased    += OnScriptControlsReleased;
            m_Scene.EventManager.OnRemovePresence            += OnRemovePresenceForControls;
            m_Scene.EventManager.OnMakeRootAgent             += OnMakeRootAgentForControls;
            m_Scene.EventManager.OnAvatarEnteringNewParcel   += OnAvatarEnteringNewParcelForExperiences;
            if (PhysicsThrottle) m_Scene.EventManager.OnFrame += OnFrameForPhysicsTime;
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
                    "phlox status",
                    "phlox status <script-item-uuid | object-name>",
                    "Read-only: what state a Phlox script is in - RunState, enabled flags, queued events, LSL state, timer interval, the event mask the region holds for the prim, and the item's Running flag. Changes nothing.",
                    HandleStatusCommand);
                MainConsole.Instance.Commands.AddCommand("Phlox", false,
                    "phlox resume",
                    "phlox resume <script-item-uuid | object-name>",
                    "Resume script(s) paused by 'phlox suspend' (accumulated events then deliver).",
                    HandleResumeCommand);
            }

            m_log.LogInformation("[PhloxEngine]: Region loaded {0}", scene.RegionInfo.RegionName);
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

        private void HandleStatusCommand(string module, string[] args)
        {
            if (WrongConsoleScene()) return;
            if (args.Length < 3)
            {
                MainConsole.Instance.Output("Usage: phlox status <script-item-uuid | object-name>");
                return;
            }
            if (m_ExeScheduler == null)
            {
                MainConsole.Instance.Output("Script engine not running.");
                return;
            }

            string target = string.Join(" ", args, 2, args.Length - 2);

            if (UUID.TryParse(target, out UUID itemId))
            {
                ReportStatus(itemId);
                return;
            }

            int found = 0;
            foreach (var sog in m_Scene.GetSceneObjectGroups())
            {
                if (!string.Equals(sog.Name, target, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var part in sog.Parts)
                    foreach (var item in part.Inventory.GetInventoryItems(InventoryType.LSL))
                    {
                        ReportStatus(item.ItemID);
                        found++;
                    }
            }
            if (found == 0)
                MainConsole.Instance.Output($"No object named '{target}' with scripts found in this region.");
        }

        /// <summary>
        /// Everything the last four sessions had to infer from silence, in one line-set:
        /// whether the scheduler even has the script, what state it is in, what is queued for it,
        /// and - the one that mattered - the event mask the REGION holds for the prim, which is
        /// what decides whether a touch ever reaches the script at all.
        /// </summary>
        private void ReportStatus(UUID itemId)
        {
            var st = m_ExeScheduler.GetStatus(itemId);
            var o = MainConsole.Instance;

            if (!st.Found)
            {
                o.Output($"{itemId}: NOT LOADED by Phlox in this region (no interpreter).");
                LogLoadContext(itemId);
                return;
            }

            SceneObjectPart part = m_Scene.GetSceneObjectPart(st.HostLocalId);
            TaskInventoryItem item = part?.Inventory.GetInventoryItem(itemId);

            o.Output($"{itemId}");
            o.Output($"  prim          : {part?.Name ?? "(unknown)"} localId={st.HostLocalId}");
            o.Output($"  script name   : {item?.Name ?? "(not in prim inventory)"}");
            o.Output($"  RunState      : {st.RunState}" + (st.PendingSyscall is null ? "" : $"  (in {st.PendingSyscall})"));
            o.Output($"  enabled       : Enabled={st.Enabled} GeneralEnable={st.GeneralEnable} suspended={st.Suspended}"
                + HeldText(st.LocalDisable));
            o.Output($"  Running flag  : {(item is null ? "(unknown)" : item.ScriptRunning.ToString())}");
            if (st.TerminatedReason is not null)
                o.Output(TerminatedLine(st.TerminatedReason));
            o.Output($"  queued events : {st.QueuedEvents}");
            o.Output($"  LSL state     : {st.LslState}");
            o.Output($"  timer         : {(st.TimerIntervalMs > 0 ? st.TimerIntervalMs + " ms" : "not set")}");
            o.Output($"  region mask   : part.ScriptEvents={part?.ScriptEvents.ToString() ?? "(no part)"}");
            o.Output($"  aggregate     : {part?.AggregatedScriptEvents.ToString() ?? "(no part)"}");
        }

        /// <summary>The status line's note on why a script is held, in words an operator reads.</summary>
        internal static string HeldText(string localDisable)
            => localDisable is null ? "" : $"  HELD: {localDisable}"
                + (localDisable.Contains("StateLoadFailed") ? " (state load failed - row kept, never run or saved this process; restart to retry)" : "")
                + (localDisable.Contains("Parcel") ? " (the parcel does not allow this script; paused until it does, not stopped)" : "");

        /// <summary>The status line for a script that stopped itself: it stays stopped until reset or set running.</summary>
        internal static string TerminatedLine(string reason)
            => $"  terminated    : {reason}  (stays stopped; reset it, or tick Running, to start it fresh)";

        /// <summary>When there is no interpreter, say what the prim still knows about the item.</summary>
        private void LogLoadContext(UUID itemId)
        {
            foreach (var sog in m_Scene.GetSceneObjectGroups())
                foreach (var part in sog.Parts)
                {
                    var item = part.Inventory.GetInventoryItem(itemId);
                    if (item is null) continue;
                    // The engine NAME, not the Running flag printed twice.
                    MainConsole.Instance.Output(
                        $"  found in prim '{part.Name}' (localId={part.LocalId}): asset={item.AssetID} " +
                        $"Running flag={item.ScriptRunning} engine='{ScriptEngineNameFor(item)}'");
                    return;
                }
            MainConsole.Instance.Output("  and no prim in this region holds an inventory item with that id.");
        }

        /// <summary>The engine named in the script's own header if loaded, or this region's default.</summary>
        private string ScriptEngineNameFor(TaskInventoryItem item)
        {
            try
            {
                string engine = m_Scene?.DefaultScriptEngine;
                AssetBase asset = m_Scene?.AssetService?.Get(item.AssetID.ToString());
                if (asset?.Data != null)
                    engine = PhloxEngineHeader.Owner(OpenMetaverse.Utils.BytesToString(asset.Data), engine, LoadedEngineNames());
                return string.IsNullOrEmpty(engine) ? "(unknown)" : engine;
            }
            catch { return "(unknown)"; }
        }

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
            m_Scene.EventManager.OnChatBroadcast -= OnChatBroadcast;
            if (m_WorldComm != null) m_WorldComm.OnMessageDelivered -= OnWorldCommMessage;
            m_Scene.EventManager.OnAvatarKilled -= OnAvatarKilled;
            m_Scene.EventManager.OnAvatarDamage -= OnAvatarDamage;
            m_Scene.EventManager.OnAvatarDamageApplied -= OnAvatarDamageApplied;
            m_Scene.EventManager.OnObjectGrab -= OnObjectGrab;
            m_Scene.EventManager.OnObjectGrabbing -= OnObjectGrabbing;
            m_Scene.EventManager.OnObjectDeGrab -= OnObjectDeGrab;
            m_Scene.EventManager.OnScriptChangedEvent -= OnScriptChangedEvent;
            m_Scene.EventManager.OnScriptControlEvent -= OnScriptControlEvent;
            m_Scene.EventManager.OnFrame -= OnFrameForPhysicsTime;
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
            m_Scene.EventManager.OnGroupCrossedToNewParcel   -= OnGroupCrossedToNewParcel;
            m_Scene.EventManager.OnObjectOwnerOrGroupChanged -= OnObjectOwnerOrGroupChanged;
            m_Scene.EventManager.OnLandObjectAdded           -= OnLandObjectChanged;
            m_Scene.EventManager.OnScriptControlsReleased    -= OnScriptControlsReleased;
            m_Scene.EventManager.OnRemovePresence            -= OnRemovePresenceForControls;
            m_Scene.EventManager.OnMakeRootAgent             -= OnMakeRootAgentForControls;
            m_Scene.EventManager.OnAvatarEnteringNewParcel   -= OnAvatarEnteringNewParcelForExperiences;
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
            // Every engine of the region gets every rez, with the region's default engine name; the script's
            // first line can name another. Phlox runs it exactly when YEngine's rule picks Phlox, so on a region running
            // both a script runs in one. A script that is not Phlox's has no Phlox instance or load (Disown); its saved state is kept.
            if (!IsPhloxScript(script, engine))
            {
                m_ScriptLoader?.Disown(localID, itemID);
                return;
            }

            SceneObjectPart part = m_Scene.GetSceneObjectPart(localID);
            if (part == null)
            {
                m_log.LogError("[PhloxEngine]: OnRezScript: prim {0} not found for script {1}", localID, itemID);
                return;
            }

            m_log.LogDebug("[PhloxEngine]: OnRezScript {0} in prim {1}", itemID, localID);

            m_ScriptLoader.PostLoadRequest(new PhloxLoadRequest
            {
                LocalID = localID,
                ItemID = itemID,
                ScriptText = script,
                AssetId = part.Inventory.GetInventoryItem(itemID)?.AssetID ?? UUID.Zero,
                StartParam = startParam,
                PostOnRez = postOnRez,
                StateSource = stateSource,
                Prim = part,
            });
        }

        /// <summary>Does the first-line rule (<see cref="PhloxEngineHeader"/>) give this script to Phlox?</summary>
        private bool IsPhloxScript(string script, string defaultEngine)
            => PhloxEngineHeader.Owner(script, defaultEngine, LoadedEngineNames()) == Name;

        private IEnumerable<string> LoadedEngineNames()
        {
            foreach (IScriptModule m in m_Scene?.RequestModuleInterfaces<IScriptModule>() ?? Array.Empty<IScriptModule>())
                if (m != null) yield return m.ScriptEngineName;
        }

        private void OnRemoveScript(uint localID, UUID itemID)
        {
            m_log.LogDebug("[PhloxEngine]: OnRemoveScript {0}", itemID);
            m_ScriptLoader.PostUnloadRequest(localID, itemID);
            OnScriptRemoved?.Invoke(itemID);
        }

        private void OnScriptReset(uint localID, UUID itemID)
        {
            m_ScriptLoader?.NoteReset(itemID);
            m_ExeScheduler?.ResetScript(itemID);
        }

		private void OnShutdown()
        {
            m_log.LogInformation("[PhloxEngine]: Shutdown event, flushing script state");
            // The scheduler stops first, so the final save is of scripts that are no longer running (Halcyon
            // MasterScheduler.Stop joins the execution thread before the state manager's backup).
            if (m_MasterScheduler != null && !m_MasterScheduler.StopThread())
                m_log.LogWarning("[PhloxEngine]: The script scheduler did not stop within 5 s; saving script state anyway");
            StateManager?.Stop();
            StateManager = null;
        }

        private void OnObjectBeingRemovedFromScene(SceneObjectGroup obj)
        {
            // When a prim leaves the scene, clean up any character it owned.
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

        /// <summary>The item's Running flag, as the viewer's checkbox and llSetScriptState persist it.</summary>
        internal void SetItemRunningFlag(uint localId, UUID itemId, bool running)
        {
            SceneObjectPart part = m_Scene?.GetSceneObjectPart(localId);
            TaskInventoryItem item = part?.Inventory?.GetInventoryItem(itemId);
            if (item is null || item.ScriptRunning == running) return;
            item.ScriptRunning = running;
            part.Inventory.ForceInventoryPersistence();
            part.ParentGroup.HasGroupChanged = true;
        }

        private void OnStartScript(uint localID, UUID itemID)
        {
            m_ScriptLoader?.NoteScriptState(itemID, true);   // Still compiling - applied when it starts
            m_ExeScheduler?.ChangeEnabledStatus(itemID, true);
        }

        private void OnStopScript(uint localID, UUID itemID)
        {
            m_ScriptLoader?.NoteScriptState(itemID, false);
            m_ExeScheduler?.ChangeEnabledStatus(itemID, false);
        }

        private void OnGetScriptRunning(IClientAPI controllingClient, UUID objectID, UUID itemID)
        {
            // OnGetScriptRunning is a broadcast EventManager event — every registered engine
            // receives it. Reply ONLY for scripts WE own; otherwise, with YEngine + Phlox both
            // enabled, Phlox would race a SendScriptRunningReply(false) for a YEngine-owned script
            // it knows nothing about, and the viewer could show a running script as stopped.
            // HasScript is the ownership check (FindScript != null), mirroring YEngine's
            // TryGetInstance guard (XMREngine.cs:1579). SendScriptRunningReply is on IClientAPI
            // (OpenSim.Framework, already referenced) — no LindenCaps reference is needed.
            bool running;
            if (!HasScript(itemID, out running)) return;
            controllingClient.SendScriptRunningReply(objectID, itemID, running);
        }

        private int m_WhisperDistance = PhloxListenManager.DefaultWhisperDistance;
        private int m_SayDistance = PhloxListenManager.DefaultSayDistance;
        private int m_ShoutDistance = PhloxListenManager.DefaultShoutDistance;
        private int m_MaxListensPerScript = PhloxListenManager.DefaultMaxListensPerScript;
        private int m_MaxListensPerRegion = PhloxListenManager.DefaultMaxListensPerRegion;

        private void OnChatFromWorld(object sender, OSChatMessage chat)
        {
            ListenManager?.DeliverChat(chat.Type, chat.Channel, chat.From, chat.SenderUUID, chat.Message,
                chat.Position, chat.Destination);
        }

        private void OnChatFromClient(object sender, OSChatMessage chat) => DeliverSceneChat(chat, UUID.Zero);

        /// <summary>
        /// Broadcast chat (Scene.SimChatBroadcast and EventManager.TriggerOnChatBroadcast): region modules send it -
        /// bots, the region-ready and concierge modules, the IRC bridge. Nothing else carries it; the core WorldComm
        /// hears it the same way (OnChatBroadcast, beside OnChatFromClient).
        /// </summary>
        private void OnChatBroadcast(object sender, OSChatMessage chat) => DeliverSceneChat(chat, chat.Destination);

        private void DeliverSceneChat(OSChatMessage chat, UUID destination)
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
            ListenManager?.DeliverChat(chat.Type, chat.Channel, speakerName, speakerKey, chat.Message,
                chat.Position, destination);
        }

        // ── Chat with the other script engine's listens ────────────────────────

        private IWorldComm m_WorldComm;

        [ThreadStatic] private static bool t_SendingToWorldComm;

        /// <summary>
        /// Offer a Phlox script's chat to the listens the core WorldComm holds (YEngine's, on a region running
        /// both engines), as YEngine's own llSay/llRegionSay/llRegionSayTo do. Phlox's listens have already
        /// had it, so WorldComm's OnMessageDelivered for it - raised on this thread - is not delivered again.
        /// </summary>
        internal void SendToWorldComm(Action<IWorldComm> send)
        {
            IWorldComm worldComm = m_WorldComm;
            if (worldComm == null) return;
            t_SendingToWorldComm = true;
            try { send(worldComm); }
            finally { t_SendingToWorldComm = false; }
        }

        /// <summary>
        /// A message another script engine sent through WorldComm (YEngine's llRegionSay and llRegionSayTo, and
        /// anything else that calls IWorldComm.DeliverMessage or DeliverMessageTo). Region chat and addressed
        /// messages reach Phlox only this way. Whisper, say and shout also go out as scene chat (Scene.SimChat,
        /// which OnChatFromWorld and OnChatBroadcast bring here), so taking them here too would deliver them twice.
        /// </summary>
        private void OnWorldCommMessage(OSChatMessage chat)
        {
            if (t_SendingToWorldComm) return;
            if (chat.Type != ChatTypeEnum.Region && chat.Type != ChatTypeEnum.Direct) return;
            ListenManager?.DeliverChat(chat.Type, chat.Channel, chat.From, chat.SenderUUID, chat.Message,
                chat.Position, chat.Destination);
        }

        // ── Touch events ───────────────────────────────────────────────────────

        // The region has already chosen the prim (Scene.ProcessObjectGrab and friends): the touched prim when it handles
        // the touch, the root as well on llPassTouches or when the touched prim has no handler. originalID is the touched
        // prim when the root takes a child's touch. Each touch goes to the scripts of that one prim, as Halcyon's
        // EngineInterface.PostObjectEvent did and as SL's llPassTouches describes.

        private void OnObjectGrab(uint localID, uint originalID, Vector3 offsetPos,
            IClientAPI remoteClient, SurfaceTouchEventArgs surfaceArgs)
        {
            SceneObjectPart part = m_Scene?.GetSceneObjectPart(localID);
            if (part == null) return;

            // SL llDetectedGrab: "only works in the touch event"; Halcyon's touch_start carried no offset.
            var dp = BuildTouchDetectParams(TouchedPart(part, originalID), remoteClient, Vector3.Zero, surfaceArgs);

            PostTouchEvent(part.LocalId, "touch_start", dp);
        }

        /// <summary>
        /// A grab update while the mouse is held. A script whose touch is active (touch_start started its 100 ms touch()
        /// repeat) only takes the new detect data for its next repeat, as Halcyon's UpdateTouchData did. A script whose
        /// touch was not started (for example, it changed into a state with touch() while the touch was held)
        /// gets it as a touch() (PhloxExecutionScheduler.FoldGrabUpdate). A state with touch() asks the region for
        /// touch_start and touch_end too (LSLSystemAPI.MapEventFlag), so its touch is started even without handlers for them.
        /// </summary>
        private void OnObjectGrabbing(uint localID, uint originalID, Vector3 offsetPos,
            IClientAPI remoteClient, SurfaceTouchEventArgs surfaceArgs)
        {
            SceneObjectPart part = m_Scene?.GetSceneObjectPart(localID);
            if (part == null) return;

            var dp = BuildTouchDetectParams(TouchedPart(part, originalID), remoteClient, offsetPos, surfaceArgs);

            if (part.ParentGroup == null || part.ParentGroup.IsDeleted) return;
            PostObjectEvent(part.LocalId, new GrabUpdateParams(dp));
        }

        private void OnObjectDeGrab(uint localID, uint originalID,
            IClientAPI remoteClient, SurfaceTouchEventArgs surfaceArgs)
        {
            SceneObjectPart part = m_Scene?.GetSceneObjectPart(localID);
            if (part == null) return;

            var dp = BuildTouchDetectParams(TouchedPart(part, originalID), remoteClient, Vector3.Zero, surfaceArgs);

            PostTouchEvent(part.LocalId, "touch_end", dp);
        }

        /// <summary>The prim the avatar touched: the one the region names in originalID when the root takes a child's touch.</summary>
        private SceneObjectPart TouchedPart(SceneObjectPart target, uint originalID)
            => originalID != 0 && originalID != target.LocalId ? m_Scene.GetSceneObjectPart(originalID) ?? target : target;

        /// <summary>
        /// The toucher's detect data, from the avatar as Halcyon's EventRouter built it (DetectParams.Populate): name,
        /// position, rotation, velocity and active group, and llDetectedType AGENT, plus ACTIVE while the avatar moves
        /// (SL llDetectedType). llDetectedLinkNumber is the touched prim's. Touch surface data comes only through the
        /// write-only SurfaceTouchArgs setter; null leaves TOUCH_INVALID_FACE and TOUCH_INVALID_TEXCOORD.
        /// </summary>
        private DetectParams BuildTouchDetectParams(SceneObjectPart touched,
            IClientAPI remoteClient, Vector3 offsetPos, SurfaceTouchEventArgs surfaceArgs)
        {
            var dp = new DetectParams { Key = remoteClient.AgentId };
            ScenePresence sp = m_Scene?.GetScenePresence(remoteClient.AgentId);
            if (sp != null)
                dp.Populate(m_Scene);
            else
            {
                dp.Name = remoteClient.Name;
                dp.Owner = remoteClient.AgentId;
            }
            // Populate types an NPC 0x20; a touch comes from an avatar, a bot's too (Halcyon: AGENT)
            dp.Type = DetectParams.AGENT | (sp != null && sp.Velocity != Vector3.Zero ? DetectParams.ACTIVE : 0);
            dp.LinkNum = touched.LinkNum;
            dp.OffsetPos = new LSL_Types.Vector3(offsetPos.X, offsetPos.Y, offsetPos.Z);
            dp.SurfaceTouchArgs = surfaceArgs;
            return dp;
        }

        /// <summary>Posts touch_start or touch_end to the scripts of the prim the region routed the touch to.</summary>
        private void PostTouchEvent(uint localID, string eventName, DetectParams dp)
            => PostObjectEvent(localID, new EventParams(eventName, new object[] { 1 }, new DetectParams[] { dp }));

        /// <summary>A grab update on its way to a prim's scripts: it reaches the scheduler as a touch() it may fold into the active touch.</summary>
        private sealed class GrabUpdateParams : EventParams
        {
            public GrabUpdateParams(DetectParams dp) : base("touch", new object[] { 1 }, new DetectParams[] { dp }) { }
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
            DetectParams[] det = FilteredColliders(localID, col);
            if (det.Length == 0) return;
            PostObjectEvent(localID, new EventParams("collision_start", new object[] { det.Length }, det));
        }

        private void OnScriptColliding(uint localID, ColliderArgs col)
        {
            DetectParams[] det = FilteredColliders(localID, col);
            if (det.Length == 0) return;
            PostObjectEvent(localID, new EventParams("collision", new object[] { det.Length }, det));
        }

        private void OnScriptCollidingEnd(uint localID, ColliderArgs col)
        {
            DetectParams[] det = FilteredColliders(localID, col);
            if (det.Length == 0) return;
            PostObjectEvent(localID, new EventParams("collision_end", new object[] { det.Length }, det));
        }

        /// <summary>
        /// The colliders the host part's llCollisionFilter lets through, as DetectParams.
        /// The region's own collision path already applies SceneObjectPart.CollisionFilteredOut
        /// before raising the event (SceneObjectPart.cs:2812-2820, ScenePresence.cs:6462-6470); this
        /// applies it again here so the filter holds for a collision arriving by any other door, and
        /// so the count a script sees is the count it was allowed to see.
        /// </summary>
        private DetectParams[] FilteredColliders(uint localID, ColliderArgs col)
        {
            if (col?.Colliders == null || col.Colliders.Count == 0) return s_emptyDetectParams;
            SceneObjectPart host = m_Scene?.GetSceneObjectPart(localID);
            var det = new List<DetectParams>(col.Colliders.Count);
            foreach (DetectedObject detobj in col.Colliders)
            {
                if (host != null && host.CollisionFilteredOut(detobj.keyUUID, detobj.nameStr)) continue;
                DetectParams d = new DetectParams();
                d.Key = detobj.keyUUID;
                d.Populate(m_Scene, detobj);
                // A collider that left the region between the physics step and now: Populate finds nothing, so the
                // entry keeps what physics saw, as Halcyon's DetectParams.FromDetectedObject copied it.
                if (string.IsNullOrEmpty(d.Name) && detobj.keyUUID.IsNotZero() && m_Scene?.GetScenePresence(detobj.keyUUID) == null
                    && m_Scene?.GetSceneObjectPart(detobj.keyUUID) == null)
                    FromDetectedObject(d, detobj);
                det.Add(d);
            }
            return det.ToArray();
        }

        private static void FromDetectedObject(DetectParams d, DetectedObject detobj)
        {
            d.Name = detobj.nameStr ?? string.Empty;
            d.Owner = detobj.ownerUUID;
            d.Group = detobj.groupUUID;
            d.Position = new LSL_Types.Vector3(detobj.posVector);
            d.Rotation = new LSL_Types.Quaternion(detobj.rotQuat);
            d.Velocity = new LSL_Types.Vector3(detobj.velVector);
            d.LinkNum = detobj.linkNumber;
            d.Type = detobj.colliderType;
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
            // Worn, an object's scripts always run; dropped, the parcel under it decides
            SceneObjectGroup group = m_Scene?.GetGroupByPrim(localID);
            if (group != null) m_ExeScheduler?.RequestParcelCheck(group);
        }

        // ── No Scripts parcels enforced live ─────────────────────────────────────────────────────────

        /// <summary>
        /// IParcelScriptPolicyEngine: the core lets Phlox's scripts start on any parcel and leaves the parcel rule to
        /// Phlox, which pauses and resumes them live (PhloxExecutionScheduler).
        /// </summary>
        public bool EnforcesParcelScriptRules => true;

        private void OnGroupCrossedToNewParcel(SceneObjectGroup group, ILandObject oldParcel, ILandObject newParcel)
            => m_ExeScheduler?.RequestParcelCheck(group);

        private void OnObjectOwnerOrGroupChanged(SceneObjectGroup group, UUID oldOwner, UUID newOwner, UUID oldGroup, UUID newGroup)
        {
            // A new owner ends every grant in the object, and the controls the old grants took
            if (oldOwner != newOwner) m_ExeScheduler?.RequestOwnerChanged(group);
            m_ExeScheduler?.RequestParcelCheck(group);
        }

        /// <summary>Flags, owner, group, sale, subdivide and join all arrive here (LandManagementModule.UpdateLandObject).</summary>
        private void OnLandObjectChanged(ILandObject parcel)
        {
            if (parcel?.LandData != null) m_ExeScheduler?.RequestParcelCheckForParcel(parcel.LandData.LocalID);
        }

        // The scene raises OnScriptControlsReleased whenever a registration goes away, the script's own release
        // or the core's (the viewer's release keys, ClearControls on a crossing, a stand-up, a permission revoke, the
        // avatar leaving the region), after ScenePresence has let go of its lock. Exactly those scripts are asked again.
        //
        // A release Phlox did not ask for, on an avatar still here (not a child, not crossing, not leaving), is
        // the core's stand-up, Release Keys, detach or drop: the script loses TAKE_CONTROLS and CONTROL_CAMERA as in
        // Halcyon's handleMustReleaseControls. Decided here, on the releasing thread, while the avatar's state is the one
        // the release happened in; the permission change itself runs on the scheduler thread.
        private void OnScriptControlsReleased(UUID agentId, UUID[] scriptItemIds)
        {
            bool mustRelease = false;
            if (t_ownControlChange == 0)
            {
                ScenePresence sp = m_Scene?.GetScenePresence(agentId);
                mustRelease = sp != null && !sp.IsDeleted && !sp.IsChildAgent && !sp.IsInTransit;
            }
            foreach (UUID itemId in scriptItemIds)
            {
                if (mustRelease) m_ExeScheduler?.RequestControlsReleasedByCore(itemId, agentId);
                m_ExeScheduler?.RequestParcelCheckForItem(itemId);
            }
        }

        [ThreadStatic] private static int t_ownControlChange;

        /// <summary>
        /// Marks a register/unregister Phlox makes itself (llTakeControls, EndPermissions); the core raises
        /// OnScriptControlsReleased synchronously on the same thread, and that release is already handled.
        /// </summary>
        internal static OwnControlChangeScope OwnControlChange()
        {
            t_ownControlChange++;
            return default;
        }

        internal readonly struct OwnControlChangeScope : IDisposable
        {
            public void Dispose() => t_ownControlChange--;
        }

        // Kept as a backstop: Scene.RemoveClient raises it before the presence goes, and the release event comes from
        // ScenePresence.Dispose in RemoveClient's finally block, which an earlier exception there would skip.
        private void OnRemovePresenceForControls(UUID agentId) => m_ExeScheduler?.RequestControlHoldersCheck();

        // An avatar became a root agent here: a crossing, a teleport, a login. The core carries taken controls in the
        // agent's data for every engine (ScenePresence CopyTo / CopyFrom Controllers); this re-takes those a script holds
        // a record of and the avatar arrived without, on the object it sits on or in its attachments (Halcyon
        // EngineInterface.OnCrossedAvatarReady -> OnGroupCrossedAvatarReady). The seat is set before the core raises it.
        private void OnMakeRootAgentForControls(ScenePresence sp)
        {
            if (sp != null) m_ExeScheduler?.RequestAvatarArrived(sp.UUID);
        }

        // An avatar entered a parcel: a grant it gave from an Experience ends where the Experience cannot run (SL wiki
        // experience_permissions_denied, "The agent has moved to a parcel where the experience cannot run").
        private void OnAvatarEnteringNewParcelForExperiences(ScenePresence sp, int localLandID, UUID regionID)
        {
            if (sp != null && !sp.IsChildAgent) m_ExeScheduler?.RequestExperienceLandCheck(sp.UUID);
        }

        /// <summary>
        /// The grant a database row saves: the script item's grant now, with the object's owner. When the part has gone (a
        /// derez) the grant noted last stays.
        /// </summary>
        internal void NoteGrantForRow(InWorldz.Phlox.VM.Interpreter interp)
        {
            SceneObjectPart part = m_Scene?.GetSceneObjectPart(interp.HostLocalId);
            TaskInventoryItem item = part?.Inventory?.GetInventoryItem(interp.ItemId);
            if (item == null) return;
            LSLSystemAPI.NoteItemGrant(interp.ScriptState, item, part.OwnerID);
        }

        /// <summary>A script took or released controls.</summary>
        internal void RequestParcelCheck(UUID itemId) => m_ExeScheduler?.RequestParcelCheckForItem(itemId);

        /// <summary>
        /// Halcyon's rule (EngineInterface.ScriptsCanRun): the object's owner owns the parcel, or the parcel allows other
        /// scripts, or it allows group scripts and the object's group is the parcel's group. No parcel: not allowed. The
        /// group test needs the parcel to have a group, as core's CanRunScript does (Halcyon compared zero with zero).
        /// </summary>
        internal static bool ParcelAllowsScripts(LandData land, UUID objectOwner, UUID objectGroup)
        {
            if (land == null) return false;
            if (land.OwnerID == objectOwner) return true;
            if ((land.Flags & (uint)ParcelFlags.AllowOtherScripts) != 0) return true;
            return (land.Flags & (uint)ParcelFlags.AllowGroupScripts) != 0
                   && land.GroupID.IsNotZero() && land.GroupID == objectGroup;
        }

        /// <summary>The local id of the parcel under an object, the lookup core's CanRunScript uses; -1 for none.</summary>
        internal int ParcelLocalIdAt(SceneObjectGroup group)
        {
            ILandChannel land = m_Scene?.LandChannel;
            if (land == null) return -1;
            Vector3 pos = group.AbsolutePosition;
            return land.GetLandObjectClippedXY(pos.X, pos.Y)?.LandData?.LocalID ?? -1;
        }

        /// <summary>
        /// "May this script run here": an attachment may; a script the parcel allows may; a script holding taken
        /// controls on an avatar right now may (<paramref name="onlyByControls"/>); nothing else. No estate-manager or god
        /// exemption. A region with no land module has no parcel rules.
        /// </summary>
        internal bool ScriptMayRunHere(SceneObjectPart part, UUID itemId, out bool onlyByControls)
        {
            onlyByControls = false;
            SceneObjectGroup group = part?.ParentGroup;
            if (group == null || group.IsAttachment) return true;
            ILandChannel landChannel = m_Scene?.LandChannel;
            if (landChannel == null) return true;

            Vector3 pos = group.AbsolutePosition;
            ILandObject parcel = landChannel.GetLandObjectClippedXY(pos.X, pos.Y);
            if (ParcelAllowsScripts(parcel?.LandData, part.OwnerID, part.GroupID)) return true;

            if (ScriptHoldsControls(part, itemId))
            {
                onlyByControls = true;
                return true;
            }
            return false;
        }

        /// <summary>Asks the avatar the script's controls were taken on (the permission granter), never Phlox's own record.</summary>
        private bool ScriptHoldsControls(SceneObjectPart part, UUID itemId)
        {
            TaskInventoryItem item = part.Inventory.GetInventoryItem(itemId);
            if (item == null || item.PermsGranter.IsZero()) return false;
            return AvatarHoldsControls(m_Scene.GetScenePresence(item.PermsGranter), itemId);
        }

        /// <summary>Does this avatar hold taken controls for this script item right now? (ScenePresence.HasScriptControls)</summary>
        internal static bool AvatarHoldsControls(ScenePresence presence, UUID itemId)
            => presence != null && presence.HasScriptControls(itemId);

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

            // Halcyon EventRouter.HandleObjectPaid: llDetectedLinkNumber is the prim that was paid, also when the root's
            // money() takes it
            int paidLink = part.LinkNum;
            if ((part.ScriptEvents & scriptEvents.money) == 0)
                part = part.ParentGroup.RootPart;

            if (part == null) return;

            DetectParams[] det = new DetectParams[1];
            det[0] = new DetectParams();
            det[0].Key = agentID;
            det[0].Populate(m_Scene);
            det[0].LinkNum = paidLink;

            PostObjectEvent(part.LocalId, new EventParams(
                "money", new object[] { agentID.ToString(), amount },
                det));
        }

        #endregion

        #region IScriptModule

        public string ScriptEngineName => Name;

        public event ScriptRemoved OnScriptRemoved;
        public event ObjectRemoved OnObjectRemoved;

        // ── State that travels with objects ──────────────────────────────────────
        //
        // The core asks every engine for a script's state when it serializes an object (take, take copy, detach, a
        // crossing, a teleport's attachments) and hands it back when the object arrives, before the scripts are started
        // (SceneObjectPartInventory.GetScriptStates / RestoreSavedScriptState, SceneObjectGroup.GetStateSnapshot /
        // SetState). Phlox's answer is the same protobuf state its state database holds, in YEngine's envelope:
        //   <State Engine="InWorldz.Phlox" UUID="item" Asset="asset" Version="1"><ScriptState>base64</ScriptState></State>
        // The UUID attribute is the item id the core keys a crossing's state by. SL: "A script will NOT automatically
        // re-enter the default state state_entry event when the task is rezzed or attached (even by a new owner), nor if
        // the task is moved to another SIM" (wiki, State).

        /// <summary>The version of the carried-state envelope this build writes and reads.</summary>
        internal const string CarriedStateVersion = "1";

        /// <summary>
        /// How long a region thread waits for the scheduler to capture an object's scripts (Halcyon STATE_REQUEST_TIMEOUT
        /// per request; here per object, however many scripts it holds).
        /// </summary>
        internal int CarriedStateTimeoutMs = 10 * 1000;

        /// <summary>
        /// The largest saved state an object may carry, in bytes before base64: a script's values are held to
        /// <see cref="InWorldz.Phlox.VM.MemoryInfo.MAX_MEMORY"/>, and its queue to <see cref="InWorldz.Phlox.VM.RuntimeState.MAX_EVENT_QUEUE_SIZE"/> events
        /// whose arguments a sending script also holds within that limit. Checked before anything is decoded.
        /// </summary>
        internal const int MaxCarriedStateBytes = (InWorldz.Phlox.VM.RuntimeState.MAX_EVENT_QUEUE_SIZE + 1) * InWorldz.Phlox.VM.MemoryInfo.MAX_MEMORY;

        /// <summary>The longest envelope read: the base64 of <see cref="MaxCarriedStateBytes"/> and room for the attributes.</summary>
        internal const int MaxCarriedStateXmlChars = (MaxCarriedStateBytes + 2) / 3 * 4 + 1024;

        /// <summary>
        /// The script's state for its object to carry, or "" when it is not a Phlox script loaded here (or is held
        /// because its saved row could not be read, or its state is above <see cref="MaxCarriedStateBytes"/>). The capture
        /// is taken on the scheduler thread, between timeslices, as the state database's are, for all the object's scripts
        /// at once (the core asks one script at a time); a caller that is the scheduler thread, or a region whose
        /// scheduler thread is not running, captures directly.
        /// </summary>
        public string GetXMLState(UUID itemID)
        {
            InWorldz.Phlox.VM.Interpreter interp = m_ExeScheduler?.FindScript(itemID);
            if (interp == null) return string.Empty;
            bool here = m_MasterScheduler == null || !m_MasterScheduler.IsRunning
                        || Thread.CurrentThread.ManagedThreadId == m_ExeScheduler.WorkerThreadId;
            byte[] blob;
            UUID assetId;
            bool got;
            if (here) got = m_ExeScheduler.CaptureForObject(itemID, out blob, out assetId);
            else
            {
                SceneObjectGroup group = m_Scene?.GetSceneObjectPart(interp.HostLocalId)?.ParentGroup;
                got = m_ExeScheduler.RequestCaptureForObject(itemID, LoadedScriptsOf(group), group?.Name ?? itemID.ToString(),
                    CarriedStateTimeoutMs, out blob, out assetId);
            }
            if (!got) return string.Empty;
            if (blob.Length > MaxCarriedStateBytes)
            {
                m_log.LogWarning("[PhloxEngine]: The state of {0} is {1} bytes, above the {2} an object carries; it travels without it",
                    itemID, blob.Length, MaxCarriedStateBytes);
                return string.Empty;
            }
            return CarriedStateXml(itemID, assetId, blob);
        }

        /// <summary>The item ids of the group's scripts loaded in this engine.</summary>
        private List<UUID> LoadedScriptsOf(SceneObjectGroup group)
        {
            var items = new List<UUID>();
            if (group == null) return items;
            foreach (SceneObjectPart part in group.Parts)
                foreach (TaskInventoryItem item in part.Inventory.GetInventoryItems(InventoryType.LSL))
                    if (m_ExeScheduler.IsLoaded(item.ItemID)) items.Add(item.ItemID);
            return items;
        }

        internal static string CarriedStateXml(UUID itemID, UUID assetId, byte[] blob)
        {
            var doc = new System.Xml.XmlDocument();
            System.Xml.XmlElement state = doc.CreateElement("", "State", "");
            doc.AppendChild(state);
            state.SetAttribute("Engine", PhloxEngineHeader.PhloxName);
            state.SetAttribute("UUID", itemID.ToString());
            state.SetAttribute("Asset", assetId.ToString());
            state.SetAttribute("Version", CarriedStateVersion);
            System.Xml.XmlElement data = doc.CreateElement("", "ScriptState", "");
            data.InnerText = Convert.ToBase64String(blob);
            state.AppendChild(data);
            return doc.OuterXml;
        }

        /// <summary>
        /// The object brought this script's state. True only for Phlox's own envelope whose state decodes: it is kept for
        /// the item's load, which uses it before the state database's row. Anything else is false, so the core asks the
        /// next engine: another engine's state (YEngine's, XEngine's), a newer envelope version, XML that does not parse,
        /// or a state that does not decode. A Phlox script given no state starts fresh; nothing is written or moved aside.
        /// </summary>
        public bool SetXMLState(UUID itemID, string xml)
        {
            if (string.IsNullOrEmpty(xml) || StateManager == null) return false;
            if (xml.Length > MaxCarriedStateXmlChars)
            {
                m_log.LogWarning("[PhloxEngine]: {0} brought {1} characters of state, above the {2} read; it starts fresh",
                    itemID, xml.Length, MaxCarriedStateXmlChars);
                return false;
            }
            System.Xml.XmlElement state;
            try
            {
                var doc = new System.Xml.XmlDocument();
                doc.LoadXml(xml);
                state = doc.DocumentElement;
            }
            catch (System.Xml.XmlException) { return false; }
            if (state == null || state.Name != "State") return false;
            if (state.GetAttribute("Engine") != PhloxEngineHeader.PhloxName) return false;

            if (state.GetAttribute("Version") != CarriedStateVersion)
            {
                m_log.LogWarning("[PhloxEngine]: {0} brought state in envelope version '{1}', which this build does not read; it starts fresh",
                    itemID, state.GetAttribute("Version"));
                return false;
            }
            if (!UUID.TryParse(state.GetAttribute("Asset"), out UUID assetId)) return false;
            try
            {
                byte[] blob = Convert.FromBase64String(state["ScriptState"]?.InnerText ?? string.Empty);
                StateManager.Decode(blob);
                StateManager.Carry(itemID, assetId, blob);
                return true;
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxEngine]: The state {0} brought with its object cannot be read; it starts fresh: {1}", itemID, e.Message);
                return false;
            }
        }

        public bool PostScriptEvent(UUID itemID, string name, object[] args)
            => PostScriptEvent(itemID, new EventParams(name, args, null));

        public bool PostObjectEvent(UUID localID, string name, object[] args)
            => false;

        public bool PostScriptEvent(UUID itemID, EventParams parms) => PostScriptEvent(itemID, parms, null);

        /// <summary>The same, with a completion callback the scheduler fires when the event is done with.</summary>
        public bool PostScriptEvent(UUID itemID, EventParams parms, Action completed)
        {
            if (m_ExeScheduler == null) return false;

            if (!m_EventList.HasEventByName(parms.EventName)) return false;
            InWorldz.Phlox.Types.FunctionSig eventInfo = m_EventList.GetEventByName(parms.EventName);

            InWorldz.Phlox.VM.DetectVariables[] detectVars = ConvertDetectParams(parms.DetectParams);

            var evt = new InWorldz.Phlox.VM.PostedEvent
            {
                EventType = (InWorldz.Phlox.Types.SupportedEventList.Events)eventInfo.TableIndex,
                Args = ToPhloxArgs(parms.Params),
                DetectVars = detectVars,
                Completed = completed
            };
            evt.Normalize();
            if (parms is GrabUpdateParams) m_ExeScheduler.PostGrabUpdate(itemID, evt);
            else m_ExeScheduler.PostEvent(itemID, evt);
            return true;
        }

        /// <summary>
        /// Arguments in YEngine's types (OpenSim.Region.ScriptEngine.Shared.LSL_Types) as Phlox's VM takes them:
        /// string, int, float, OpenMetaverse vectors and rotations, lists as object[] (Normalize makes them LSLList). The
        /// core's pumps built remote_data that way for every engine, and Phlox's own XML-RPC pump did too, so a Phlox script
        /// got values its VM does not know. Other arguments, and an array with none of these, are passed on as they are.
        /// </summary>
        internal static object[] ToPhloxArgs(object[] args)
        {
            if (args == null || !Array.Exists(args, IsLslType)) return args;
            return Array.ConvertAll(args, ToPhloxValue);
        }

        private static bool IsLslType(object a) =>
            a is LSL_Types.LSLString || a is LSL_Types.LSLInteger || a is LSL_Types.LSLFloat || a is LSL_Types.key
            || a is LSL_Types.Vector3 || a is LSL_Types.Quaternion || a is LSL_Types.list;

        private static object ToPhloxValue(object a) => a switch
        {
            LSL_Types.LSLString s => s.m_string,
            LSL_Types.LSLInteger i => i.value,
            LSL_Types.LSLFloat f => (float)f.value,
            LSL_Types.key k => k.value,
            LSL_Types.Vector3 v => new Vector3((float)v.x, (float)v.y, (float)v.z),
            LSL_Types.Quaternion q => new Quaternion((float)q.x, (float)q.y, (float)q.z, (float)q.s),
            LSL_Types.list l => Array.ConvertAll(l.Data, ToPhloxValue),
            _ => a
        };

        /// <summary>
        /// on_death - "triggered on all attachments worn by an avatar when that avatar's
        /// health reaches 0" (wiki). The region's one death hook is EventManager.OnAvatarKilled,
        /// raised from ScenePresence.PhysicsCollisionUpdate and from llAdjustDamage / llSetHealth
        /// when Health falls to 0. Every script on every part of every attachment gets it, with no
        /// arguments. The killer's local id is not part of the SL event and is not forwarded.
        /// </summary>
        private void OnAvatarKilled(uint killerLocalId, ScenePresence dead)
        {
            if (dead == null) return;
            try
            {
                foreach (SceneObjectGroup attachment in dead.GetAttachments())
                {
                    if (attachment == null || attachment.IsDeleted) continue;
                    foreach (SceneObjectPart part in attachment.Parts)
                        PostObjectEvent(part.LocalId, new EventParams("on_death", Array.Empty<object>(), null));
                }
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxEngine]: on_death delivery for {0} failed: {1}", dead.UUID, e.Message);
            }
        }

        /// <summary>
        /// How long the region waits for every on_damage handler to finish before the damage
        /// lands. SL is synchronous here; a script that sleeps in on_damage forfeits its adjustment.
        /// </summary>
        public const int OnDamageWaitMs = 500;

        /// <summary>
        /// on_damage - "before damage has been applied" (wiki) - to every script on every
        /// attachment the presence wears, with one DetectParams per pending entry: llDetectedKey /
        /// llDetectedOwner name the source, llDetectedDamage(n) is [amount, type, original], and
        /// llAdjustDamage(n, v) writes the entry's Amount through the AdjustDamage hook. The region
        /// thread that raised the damage BLOCKS here, bounded by <see cref="OnDamageWaitMs"/>, until the
        /// scheduler reports every posted event done (handler finished or event dropped) - that is what
        /// makes the adjustment land before the amount does. The wait is skipped, and the events merely
        /// posted, when the caller IS the script thread (a synchronous syscall could never be waited on
        /// from itself); llDamage and llSetHealth are async syscalls for exactly this reason.
        /// </summary>
        private void OnAvatarDamage(ScenePresence presence, List<DamageEntry> batch)
        {
            if (presence == null || batch == null || batch.Count == 0 || m_ExeScheduler == null) return;
            try
            {
                var det = DamageDetectParams(batch, adjustable: true);
                bool canWait = System.Threading.Thread.CurrentThread.ManagedThreadId != m_ExeScheduler.WorkerThreadId;
                using var done = new System.Threading.CountdownEvent(1);
                int posted = 0;
                foreach (UUID itemId in AttachmentScripts(presence))
                {
                    done.AddCount();
                    posted++;
                    if (!PostScriptEvent(itemId, new EventParams("on_damage", new object[] { batch.Count }, det), () => done.Signal()))
                        done.Signal();
                }
                done.Signal();
                if (posted > 0 && canWait && !done.Wait(OnDamageWaitMs))
                    m_log.LogWarning("[PhloxEngine]: on_damage for {0}: {1} handler(s) still running after {2} ms; applying the batch as adjusted so far",
                        presence.UUID, done.CurrentCount, OnDamageWaitMs);
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxEngine]: on_damage delivery for {0} failed: {1}", presence.UUID, e.Message);
            }
        }

        /// <summary>final_damage - what landed, to the same scripts, not waited on.</summary>
        private void OnAvatarDamageApplied(ScenePresence presence, List<DamageEntry> batch)
        {
            if (presence == null || batch == null || batch.Count == 0) return;
            try
            {
                var det = DamageDetectParams(batch, adjustable: false);
                foreach (UUID itemId in AttachmentScripts(presence))
                    PostScriptEvent(itemId, new EventParams("final_damage", new object[] { batch.Count }, det));
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxEngine]: final_damage delivery for {0} failed: {1}", presence.UUID, e.Message);
            }
        }

        private DetectParams[] DamageDetectParams(List<DamageEntry> batch, bool adjustable)
        {
            var det = new DetectParams[batch.Count];
            for (int i = 0; i < batch.Count; i++)
            {
                DamageEntry entry = batch[i];
                SceneObjectPart src = entry.SourceObject.IsZero() ? null : World?.GetSceneObjectPart(entry.SourceObject);
                det[i] = new DetectParams
                {
                    Key = entry.SourceObject,
                    Owner = entry.SourceOwner,
                    Group = src?.GroupID ?? UUID.Zero,
                    Name = src?.Name ?? string.Empty,
                    Type = src == null ? 0 : (src.ParentGroup.ContainsScripts() ? DetectParams.SCRIPTED | DetectParams.ACTIVE : DetectParams.PASSIVE),
                    Position = src == null ? new LSL_Types.Vector3() : new LSL_Types.Vector3(src.AbsolutePosition.X, src.AbsolutePosition.Y, src.AbsolutePosition.Z),
                    Damage = entry.Amount,
                    DamageType = entry.DamageType,
                    OriginalDamage = entry.OriginalDamage,
                    AdjustDamage = adjustable ? (v => entry.Amount = v) : null,
                };
            }
            return det;
        }

        /// <summary>Every script item on every part of every attachment the presence wears - the on_death set.</summary>
        private List<UUID> AttachmentScripts(ScenePresence presence)
        {
            var items = new List<UUID>();
            foreach (SceneObjectGroup attachment in presence.GetAttachments())
            {
                if (attachment == null || attachment.IsDeleted) continue;
                foreach (SceneObjectPart part in attachment.Parts)
                {
                    TaskInventoryDictionary scripts;
                    lock (part.TaskInventory)
                        scripts = (TaskInventoryDictionary)part.TaskInventory.Clone();
                    foreach (var kvp in scripts)
                        if (kvp.Value.Type == (int)AssetType.LSLText || kvp.Value.Type == 10)
                            items.Add(kvp.Value.ItemID);
                }
            }
            return items;
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

            // Any engine's pump can take a Phlox script's http_response and offer it here. A reset or removed Phlox
            // script's late one is dropped, whichever pump took it.
            if (parms.EventName == "http_response" && parms.Params != null && parms.Params.Length > 0
                && AsyncCommands?.HttpRequestPlugin is { } http && !http.Offered(parms.Params[0]))
                return false;

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
            //
            // One pool work item per call delivered a prim's events in whatever order the pool ran them (a burst of
            // link_message or changed events arrived shuffled). Each prim has one lane instead: its events wait in
            // order and a single work item delivers them one after another, as Halcyon's synchronous posting did.
            Interlocked.Increment(ref m_ObjectPostsInFlight);   // Counted only, see ObjectPostsInFlight
            lock (m_PrimLanes)
            {
                if (m_PrimLanes.TryGetValue(localID, out var lane))
                {
                    lane.Enqueue((part, parms));   // the lane's work item is already running and will take it
                    return true;
                }
                lane = new Queue<(SceneObjectPart, EventParams)>();
                lane.Enqueue((part, parms));
                m_PrimLanes[localID] = lane;
            }
            ThreadPool.UnsafeQueueUserWorkItem(DrainPrimLane, localID, false);

            return true;
        }

        /// <summary>Object events waiting for delivery, one queue per prim local id, present while its work item runs.</summary>
        private readonly Dictionary<uint, Queue<(SceneObjectPart Part, EventParams Parms)>> m_PrimLanes = new();

        private void DrainPrimLane(uint localID)
        {
            while (true)
            {
                SceneObjectPart part;
                EventParams parms;
                lock (m_PrimLanes)
                {
                    var lane = m_PrimLanes[localID];
                    if (lane.Count == 0) { m_PrimLanes.Remove(localID); return; }
                    (part, parms) = lane.Dequeue();
                }
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
                    m_log.LogError(
                        "[PhloxEngine]: PostObjectEvent deferred dispatch failed for localID {0}: {1}",
                        localID, e);
                }
                finally
                {
                    Interlocked.Decrement(ref m_ObjectPostsInFlight);
                }
            }
        }

        private int m_ObjectPostsInFlight;

        /// <summary>
        /// Test seam, inert in production (a counter nothing acts on): object events handed to the thread pool
        /// above and not yet posted to their scripts. Until the pool runs the work item the event is in no scheduler
        /// queue, so a test waiting for "nothing pending" reads this too.
        /// </summary>
        internal int ObjectPostsInFlight => Volatile.Read(ref m_ObjectPostsInFlight);

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

        /// <summary>
        /// The errors of the compile the script editor's Save just started, so they show in the editor's
        /// error pane - this returned an empty list at once, and the viewer said "compiled" for any script. Mirrors
        /// YEngine (XMREngine.GetScriptErrors: block until that item's compile has posted its errors, empty for
        /// success; "(line,col) Error: message"), bounded by the region's own 15 s and its
        /// "timedout waiting for errors". Called on the caps thread that answers the Save, never the scheduler's.
        /// </summary>
        public System.Collections.ArrayList GetScriptErrors(UUID itemID)
        {
            var list = new System.Collections.ArrayList();
            if (m_ScriptLoader == null) return list;
            // Never wait on the thread that would have to deliver the answer.
            if (m_ExeScheduler != null && m_ExeScheduler.WorkerThreadId == System.Threading.Thread.CurrentThread.ManagedThreadId) return list;
            List<string> errors = m_ScriptLoader.WaitForCompileErrors(itemID, PhloxScriptLoader.ErrorWaitTimeout);
            if (errors == null) return list;   // not a Phlox load: another engine answers for it
            foreach (string e in PhloxCompileErrorReport.ForEditor(errors)) list.Add(e);
            return list;
        }
        /// <summary>
        /// Is this item a Phlox script - running, or its load posted, waiting or compiling? Events for anything
        /// else are dropped, and a late reply for such an item is one its own script asked for before a reset. Any thread.
        /// </summary>
        internal bool HasOrIsLoading(UUID itemID)
            => (m_ExeScheduler?.IsLoaded(itemID) ?? false) || IsLoading(itemID);

        /// <summary>Is a load of this item in flight (so its early events are worth holding)?</summary>
        internal bool IsLoading(UUID itemID) => m_ScriptLoader?.IsLoading(itemID) ?? false;

        /// <summary>
        /// A dataserver answer goes to every script in the asking script's prim, as
        /// SL ("Dataserver requests will trigger dataserver events in all scripts within the same prim where the request
        /// was made", wiki dataserver) and Halcyon (PostObjectEvent(m_localID, ...)). Never to another prim.
        /// Phlox's scripts in the prim (running or loading) get it in a stable order, by item name then item id, posted on
        /// this thread, so one script's answers still arrive in the order it asked (PostObjectEvent's pool hop would not
        /// keep that). <paramref name="skip"/> is a script that must not get it: the asker, when it is no longer owed the
        /// answer. Then each other script engine of the region is offered it once, with the values YEngine's own
        /// dataserver posts (LSLString key, LSLString data); each posts only to its own scripts in that prim (the
        /// shape of AsyncCommand/Plugins/HttpRequest.cs). Returns how many Phlox scripts it was posted to.
        /// </summary>
        internal int PostDataserverToPrim(SceneObjectPart part, UUID skip, string queryId, string data)
        {
            if (part?.ParentGroup == null || part.ParentGroup.IsDeleted) return 0;

            TaskInventoryDictionary inventory;
            lock (part.TaskInventory)
                inventory = (TaskInventoryDictionary)part.TaskInventory.Clone();
            var scripts = new List<TaskInventoryItem>();
            foreach (TaskInventoryItem item in inventory.Values)
                if (item.Type == (int)AssetType.LSLText || item.Type == 10)
                    scripts.Add(item);
            scripts.Sort((a, b) =>
            {
                int byName = string.CompareOrdinal(a.Name, b.Name);
                return byName != 0 ? byName : a.ItemID.CompareTo(b.ItemID);
            });

            int posted = 0;
            foreach (TaskInventoryItem item in scripts)
            {
                if (item.ItemID == skip || !HasOrIsLoading(item.ItemID)) continue;
                if (PostScriptEvent(item.ItemID, new EventParams("dataserver", new object[] { queryId, data }, new DetectParams[0])))
                    posted++;
            }

            // An exception from another engine is logged for that engine and goes no further: the engines after it are
            // still offered the answer, and it never reaches the asking script's request (whose failure path would
            // report it to the owner as the request's own error).
            var seen = new List<IScriptEngine>();
            foreach (IScriptModule m in World?.RequestModuleInterfaces<IScriptModule>() ?? Array.Empty<IScriptModule>())
            {
                if (ReferenceEquals(m, this) || m is not IScriptEngine e || seen.Contains(e)) continue;
                seen.Add(e);
                try
                {
                    e.PostObjectEvent(part.LocalId, new EventParams("dataserver",
                        new object[] { new LSL_Types.LSLString(queryId), new LSL_Types.LSLString(data) }, new DetectParams[0]));
                }
                catch (Exception ex)
                {
                    m_log.LogWarning("[PhloxEngine]: another script engine failed to take a dataserver event: {0}", ex.Message);
                }
            }
            return posted;
        }

        public bool HasScript(UUID itemID, out bool running)
        {
            running = false;
            if (m_ExeScheduler == null || m_ExeScheduler.FindScript(itemID) == null)
                return false;
            running = m_ExeScheduler.GetScriptRunning(itemID);
            return true;
        }
        /// <summary>Every loaded script's state is written to the state database before this returns.</summary>
        public void SaveAllState()
        {
            if (m_ExeScheduler == null || StateManager == null) return;
            bool here = m_MasterScheduler == null || !m_MasterScheduler.IsRunning
                        || Thread.CurrentThread.ManagedThreadId == m_ExeScheduler.WorkerThreadId;
            if (here) m_ExeScheduler.SaveAllHere();
            else if (!m_ExeScheduler.RequestSaveAll(CarriedStateTimeoutMs))
                m_log.LogError("[PhloxEngine]: SaveAllState timed out after {0} ms", CarriedStateTimeoutMs);
        }
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
            m_log.LogInformation("[PhloxEngine]: StartProcessing fired TriggerEmptyScriptCompileQueue(0) — RegionReady LoginLock release signal");
            m_ScriptLoader?.NoteStartPosted();   // the restore summary, once the start's loads have settled
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

        // Returning TRUE for a Phlox script that is not yet running is deliberate (fe31bac769): Phlox
        // scripts are never rez-suspended, so "not suspended" IS success — returning false made
        // SceneObjectPartInventory.ResumeScripts() `continue` past the changed(CHANGED_OWNER)
        // post, swallowing that event on ownership transfer (the load is still compiling then; the
        // event is held for it). Known suspended scripts now actually resume (RequestResume is a cheap
        // no-op for non-suspended ones). False for another engine's script - ResumeScripts
        // clears OwnerChanged after the first engine that answers true, so answering true for a
        // YEngine script asked of Phlox first took its changed(CHANGED_OWNER) away.
        public bool ResumeScript(UUID itemID)
        {
            m_ExeScheduler?.RequestResume(itemID);
            return HasOrIsLoading(itemID);
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

        /// <summary>
        /// Test seam, inert in production. The folder for this engine's bytecode cache and its schema stamp,
        /// read once when <see cref="AddRegion"/> builds the loader. Null (always, outside tests) means the loader's
        /// constant "ScriptEngines/Phlox/bytecode", exactly as before. Test harnesses running in parallel set it so
        /// each class has its own folder instead of all sharing one.
        /// </summary>
        internal string BytecodeCacheDir { get; set; }
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
        {
            m_ScriptLoader?.NoteScriptState(itemID, state);   // Still compiling - applied when it starts
            m_ExeScheduler?.ChangeEnabledStatus(itemID, state);
        }

        public bool GetScriptState(UUID itemID)
            => m_ExeScheduler?.GetScriptRunning(itemID) ?? false;

        public void SetState(UUID itemID, string newState) { }

        public void ApiResetScript(UUID itemID)
        {
            m_ScriptLoader?.NoteReset(itemID);   // llResetOtherScript on an item still compiling
            m_ExeScheduler?.ResetNow(itemID);
        }

        public void ResetScript(UUID itemID)
        {
            m_ScriptLoader?.NoteReset(itemID);
            m_ExeScheduler?.ResetScript(itemID);
        }

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
        {
            // Inside an off-thread call for this script, record the result; the call's
            // single, sequenced return is posted when its body ends (LSLSystemAPI.CompleteSyscall).
            var ctx = InWorldz.Phlox.Glue.SyscallContext.Current;
            if (ctx != null && ctx.ItemId == itemId) { ctx.SetResult(retValue, delay); return; }
            m_ExeScheduler?.PostSyscallReturn(itemId, retValue, delay);
        }

        /// <summary>Post a call's return with its sequence number (LSLSystemAPI.CompleteSyscall).</summary>
        internal void SysReturnSequenced(UUID itemId, object retValue, int delay, int seq)
            => m_ExeScheduler?.PostSyscallReturn(itemId, retValue, delay, seq, null);

        /// <summary>[InWorldz.Phlox] ServiceCallDeferral.</summary>
        public ServiceCallDeferralMode ServiceCallDeferral { get; private set; } = ServiceCallDeferralMode.Auto;

        /// <summary>
        /// Called by LSLSystemAPI.llSetTimerEvent.
        /// </summary>
        /// <summary>
        /// Let the API answer an asynchronous call on the script's own event queue - the
        /// SL contract for llUpdateKeyValue(k, v, checked, original) is a dataserver reply, not a
        /// return value. The scheduler already has PostEvent; this is the one public door to it.
        /// </summary>
        public void PostScriptEvent(UUID itemID, InWorldz.Phlox.VM.PostedEvent evt)
            => m_ExeScheduler?.PostEvent(itemID, evt);

        /// <summary>llMinEventDelay lands in the execution scheduler.</summary>
        public void SetMinEventDelay(UUID itemID, float seconds)
            => m_ExeScheduler?.SetMinEventDelay(itemID, seconds);

        public void SetTimerEvent(uint localID, UUID itemID, float sec)
            => m_ExeScheduler?.SetTimer(itemID, sec);

        #endregion

        #region Helpers

        /// <summary>
        /// Detect data from a sweep a bot made (botSensor, botSensorRepeat): the shared DetectParams with the scanning bot's
        /// key, which iwDetectedBot returns. Halcyon kept it as DetectParams.BotID; the shared class here has no such field.
        /// </summary>
        internal sealed class BotDetectParams : DetectParams
        {
            public UUID BotID;
        }

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
                    Damage       = parms[i].Damage,
                    DamageType   = parms[i].DamageType,
                    OriginalDamage = parms[i].OriginalDamage,
                    AdjustDamage = parms[i].AdjustDamage,
                    // Halcyon copied the bot flag of every entry: the scanning bot's key, NULL_KEY for anything else
                    BotID        = (parms[i] is BotDetectParams b ? b.BotID : UUID.Zero).ToString(),
                };
            }
            return result;
        }

        #endregion
    }
}
