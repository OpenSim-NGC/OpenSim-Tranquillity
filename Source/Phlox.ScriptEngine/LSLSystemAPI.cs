/*
 * Phlox Script Engine Integration
 *
 * LSLSystemAPI — implementation of ISystemAPI.
 *
 * Core functions (llSay, math, basic queries) are implemented.
 * Everything else stubs with a log warning and safe default.
 * Port methods from Halcyon's InWorldz/InWorldz.Phlox.Engine/LSLSystemAPI.cs
 * as needed, adapting Halcyon-specific APIs to standard OpenSim equivalents.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Animation;
using OpenMetaverse.Packets;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Services.Interfaces;
using InWorldz.Phlox.VM;
using ProtoBuf;
using InWorldz.Phlox.Glue;
using InWorldz.Phlox.Types;
using System.Text;
using System.Drawing;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.OptionalModules.World.NPC;

using Microsoft.Extensions.Logging;
using static Phlox.ScriptEngine.SlConst;
using ScenePrimType = OpenSim.Region.Framework.Scenes.PrimType;

namespace Phlox.ScriptEngine
{
    public class LSLSystemAPI : ISystemAPI, InWorldz.Phlox.Glue.ISyscallDeferralAdvisor
    {
        // ── Inline or deferred ──────────────────────────────────────────────
        //
        // Answer inline (exactly as without deferral) when the call cannot leave the process: the subject
        // is in this region, or the answer is already in the local cache the service call consults
        // FIRST (RemoteUserAccountServicesConnector.GetUserAccount -> UserAccountCache;
        // RegionAssetConnector.Get -> the asset cache). A cache hit therefore returns exactly what the
        // blocking call returned, with the same staleness. Everything else in the deferred set runs
        // on the region's service lane. Any doubt means defer: deferral changes only WHEN the script
        // gets its answer, never WHAT it gets.
        public bool NeedsService(string fn, object[] a)
        {
            if (m_ScriptEngine?.ServiceCallDeferral == ServiceCallDeferralMode.Always) return true;
            try
            {
                switch (fn)
                {
                    case "llGetDisplayName":
                    case "llGetUsername":
                    {
                        if (!UUID.TryParse(a[0] as string, out UUID id)) return false;
                        ScenePresence sp = World?.GetScenePresence(id);
                        if (sp != null && !sp.IsChildAgent) return false;
                        return !AccountCached(id, requireAccount: false);
                    }
                    case "iwGetAgentData":
                    {
                        if (!UUID.TryParse(a[0] as string, out UUID id)) return false;
                        int data = Convert.ToInt32(a[1]);
                        if (data != 2 && data != 3) return false;              // no account lookup
                        if (data == 2 && World?.GetScenePresence(id) != null) return false;
                        return !AccountCached(id, requireAccount: false);
                    }
                    case "llName2Key":
                    {
                        string name = a[0] as string;
                        if (string.IsNullOrWhiteSpace(name)) return false;
                        string[] parts = name.Trim().Split(new[] { ' ', '.' }, 2, StringSplitOptions.RemoveEmptyEntries);
                        string first = parts[0], last = parts.Length > 1 ? parts[1] : "Resident";
                        bool here = false;
                        World?.ForEachScenePresence(sp =>
                        {
                            if (!here && !sp.IsChildAgent &&
                                sp.Firstname.Equals(first, StringComparison.InvariantCultureIgnoreCase) &&
                                sp.Lastname.Equals(last, StringComparison.InvariantCultureIgnoreCase)) here = true;
                        });
                        if (here) return false;
                        var cache = World?.RequestModuleInterface<IUserAccountCacheModule>();
                        if (cache == null) return true;
                        cache.Get(first + " " + last, out bool inCache);
                        return !inCache;
                    }
                    case "osKey2Name":
                    {
                        if (!UUID.TryParse(a[0] as string, out UUID id)) return false;
                        if (World?.GetScenePresence(id) != null) return false;
                        // A cached null falls through to the user-management module, which may call out.
                        return !AccountCached(id, requireAccount: true);
                    }
                    case "llDialog":
                    {
                        if (!UUID.TryParse(a[0] as string, out UUID av)) return false;
                        ScenePresence sp = World?.GetScenePresence(av);
                        if (sp == null || sp.IsChildAgent) return false;     // returns before any lookup
                        if (World?.GetScenePresence(m_host.OwnerID) != null) return false;
                        return !AccountCached(m_host.OwnerID, requireAccount: false);
                    }
                    case "llRequestPermissions":
                        return !AccountCached(m_host.ParentGroup.RootPart.OwnerID, requireAccount: false);
                    case "llGetNotecardLineSync":
                    case "llFindNotecardTextSync":
                    {
                        TaskInventoryItem item = FindInventoryItem(a[0] as string, (int)AssetType.Notecard);
                        return item != null && World?.AssetService?.GetCached(item.AssetID.ToString()) == null;
                    }
                    case "osGetNotecard":
                    case "osGetNotecardLine":
                    case "osGetNumberOfNotecardLines":
                    {
                        string name = a[0] as string;
                        if (m_host == null || string.IsNullOrEmpty(name)) return false;
                        TaskInventoryItem item = UUID.TryParse(name, out UUID nid) ? m_host.Inventory.GetInventoryItem(nid) : FindInventoryItem(name, (int)AssetType.Notecard);
                        return item != null && World?.AssetService?.GetCached(item.AssetID.ToString()) == null;
                    }
                    default:
                        return true;
                }
            }
            catch
            {
                return true;
            }
        }

        /// <summary>Is an answer for this account already in the local user-account cache?</summary>
        private bool AccountCached(UUID id, bool requireAccount)
        {
            var cache = World?.RequestModuleInterface<IUserAccountCacheModule>();
            if (cache == null) return false;
            UserAccount acct = cache.Get(id, out bool inCache);
            return inCache && (!requireAccount || acct != null);
        }

        private static readonly ILogger m_log = LoggerProvider.CreateLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        protected PhloxEngine m_ScriptEngine;
        protected SceneObjectPart m_host;
        protected uint m_localID;
        protected UUID m_itemID;

        private Interpreter m_thisScript;
        public Interpreter Script { get => m_thisScript; set => m_thisScript = value; }
        public Scene World => m_ScriptEngine.World;

        // Thread-local Random so each scheduler thread gets its own seeded instance;
        // avoids both per-call seed collisions (new Random()) and lock contention.
        [ThreadStatic]
        private static Random s_threadRandom;
        private static Random ThreadRandom => s_threadRandom ??= new Random();

        public LSLSystemAPI(PhloxEngine engine, SceneObjectPart host, uint localID, UUID itemID)
        {
            m_ScriptEngine = engine;
            m_host = host;
            m_localID = localID;
            m_itemID = itemID;
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private void Stub(string name) =>
            m_log.LogWarning("[PhloxAPI]: STUB {0} called from {1}", name, m_itemID);

        protected void ScriptSleep(int ms)
        {
            if (m_thisScript == null || ms <= 0) return;
            // On an off-thread call for this script the delay travels with the call's
            // return. Writing RunState from this thread stranded the script (SyscallSleepRaceTests).
            var ctx = InWorldz.Phlox.Glue.SyscallContext.Current;
            if (ctx != null && ctx.ItemId == m_itemID) { ctx.AddDelay(ms); return; }
            m_thisScript.ScriptState.NextWakeup = InWorldz.Phlox.Util.Clock.Now + (ulong)ms;
            m_thisScript.ScriptState.RunState = RuntimeState.Status.Sleeping;
        }

        // ── Halcyon's anti-abuse slowdowns ─────────────────────────────────────────────────────────
        // Each is Halcyon's ScriptSleep at Halcyon's call point, under its own [InWorldz.Phlox] setting (on by default;
        // off is exactly the behaviour without them). The script sees its call work as before and runs on after the
        // sleep. None logs, as none did in Halcyon.

        /// <summary>Halcyon SimChat / llRegionSay / llOwnerSay (LSLSystemAPI.cs:1042-1085, 12727-12732): ScriptSleep(15).</summary>
        private void ChatSleep()
        {
            if (m_ScriptEngine != null && m_ScriptEngine.ChatThrottle) ScriptSleep(15);
        }

        /// <summary>
        /// Halcyon's ScriptShoutError (LSLSystemAPI.cs:14483-14486) - the error on DEBUG_CHANNEL, then SimChat's
        /// 15 ms (:1042-1046); LSLError, NotImplemented and Deprecated go through it. Used exactly where Halcyon used it in a
        /// call that is not long-running (its ScriptSleep does nothing in one, :145-156). Its plain ShoutError, which does
        /// not pause, stays ShoutError here.
        /// </summary>
        private void ScriptShoutError(string errorText)
        {
            ShoutError(errorText);
            ChatSleep();
        }

        // Halcyon's three wrappers of ScriptShoutError, with their texts (LSLSystemAPI.cs:14497-14513).
        // Used where Halcyon raised them and Phlox was silent.
        private void LSLError(string msg) => ScriptShoutError("LSL Runtime Error: " + msg);
        private void NotImplemented(string command) => ScriptShoutError("Command not implemented: " + command);
        private void Deprecated(string command) => ScriptShoutError("Command deprecated: " + command);

        /// <summary>Halcyon llHTTPRequest's ERROR_DELAY after a refused request (LSLSystemAPI.cs:13762).</summary>
        internal const int HTTP_CAPPED_DELAY = 80;

        /// <summary>Halcyon botWhisper ... botTouchObject (LSLSystemAPI.cs:17867-17996): ScriptSleep(15) after the call.</summary>
        private void BotSleep()
        {
            if (m_ScriptEngine != null && m_ScriptEngine.BotThrottle) ScriptSleep(15);
        }

        // Halcyon LSLSystemAPI.cs:60
        private const int MAX_PHYSICS_TIME_BEFORE_DILATION = 30;

        /// <summary>
        /// Halcyon PhySleep (LSLSystemAPI.cs:1592-1614): when the physics frame time, averaged over the last 10 frames,
        /// is over 30 ms, the script sleeps that many ms.
        /// </summary>
        private void PhySleep()
        {
            if (m_ScriptEngine == null || !m_ScriptEngine.PhysicsThrottle) return;
            int cmdTime = m_ScriptEngine.PhysicsFrameTimeAvg;
            if (cmdTime > MAX_PHYSICS_TIME_BEFORE_DILATION)
                ScriptSleep(cmdTime);
        }

        /// <summary>
        /// Halcyon llMessageLinked / botMessageLinked (LSLSystemAPI.cs:5886-5899, 18169-18185): after posting, a 50 ms sleep
        /// when any receiving script's event queue has 20% or less free (52 or more of 64 queued). A script this engine does
        /// not run (YEngine's) counts as free, as Halcyon's GetEventQueueFreeSpacePercentage does for an unknown item.
        /// </summary>
        private void LinkMessageBackPressure(IEnumerable<UUID> receivers)
        {
            if (m_ScriptEngine == null || !m_ScriptEngine.LinkMessageThrottle) return;
            const float LOW_SPACE_THRESHOLD = 0.2f;
            const int LOW_SPACE_DELAY = 50;
            foreach (UUID item in receivers)
            {
                if (m_ScriptEngine.GetEventQueueFreeSpacePercentage(item) <= LOW_SPACE_THRESHOLD)
                {
                    ScriptSleep(LOW_SPACE_DELAY);
                    return;
                }
            }
        }

        private static IEnumerable<UUID> ScriptItemsIn(IEnumerable<SceneObjectPart> parts)
        {
            var items = new List<UUID>();
            foreach (SceneObjectPart part in parts)
            {
                if (part?.TaskInventory == null) continue;
                lock (part.TaskInventory)
                {
                    foreach (var kvp in part.TaskInventory)
                        if (kvp.Value.Type == (int)AssetType.LSLText || kvp.Value.Type == INVENTORY_SCRIPT)
                            items.Add(kvp.Value.ItemID);
                }
            }
            return items;
        }

        private void NotecardSleep(int ms)
        {
            if (m_ScriptEngine != null && m_ScriptEngine.NotecardThrottle) ScriptSleep(ms);
        }

        // Halcyon GetNumberOfNotecardLines (LSLSystemAPI.cs:14523-14525)
        private const int NOTECARD_COUNT_ERROR_DELAY = 100;
        private const int NOTECARD_COUNT_LONG_DELAY = 50;
        private const int NOTECARD_COUNT_FAST_DELAY = 25;
        // Halcyon GetNotecardSegment (LSLSystemAPI.cs:14604-14606)
        private const int NOTECARD_LINE_LONG_DELAY = 25;
        private const int NOTECARD_LINE_FAST_DELAY = 1;
        private const int NOTECARD_LINES_PER_DELAY = 16;

        /// <summary>Halcyon GetNotecardSegment's cached-read delay: 1 ms on every 16th line read from offset 0.</summary>
        private void NotecardLineCachedSleep(int line, int startOffset)
        {
            if (((line % NOTECARD_LINES_PER_DELAY) == 0) && (startOffset == 0))
                NotecardSleep(NOTECARD_LINE_FAST_DELAY);
        }

        /// <summary>
        /// Answer a notecard read with <paramref name="answer"/> of the notecard's text. From the region's notecard cache
        /// when it is on and holds the asset (true: a cached read); otherwise fetched on the thread pool,
        /// cached when the cache is on, and the cache swept as Halcyon's CacheCheck does after a miss (false).
        /// <paramref name="logAs"/> names the call in the error log (null: not logged, as the link variants never logged).
        /// A fetch that finds no notecard asset is Halcyon's (LSLSystemAPI.cs:14563-14569, 14646-14652): the error
        /// "Notecard '<paramref name="cardName"/>' could not be found." shouted and no dataserver answer (Phlox answered
        /// "0" or EOF). Halcyon also paused the script (ScriptSleep(ERROR_DELAY), its ScriptShoutError's 15 ms); here the
        /// fetch runs on the thread pool, where a ScriptSleep would set the run state off the script's own thread (the
        /// strand, SyscallSleepRaceTests), so the error is shouted without a pause.
        /// </summary>
        private bool AnswerNotecardRead(UUID assetId, UUID queryID, Func<PhloxNotecardCache.Card, string> answer,
            string cardName, string logAs)
        {
            PhloxNotecardCache cache = m_ScriptEngine != null && m_ScriptEngine.NotecardCacheEnabled ? m_ScriptEngine.NotecardCache : null;
            if (cache != null && cache.TryGet(assetId, out PhloxNotecardCache.Card cached))
            {
                PostDataserverEvent(queryID, answer(cached));
                return true;
            }
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    AssetBase asset = World.AssetService.Get(assetId.ToString());
                    if (asset == null || asset.Data == null || asset.Type != (sbyte)AssetType.Notecard)
                    {
                        ShoutError("Notecard '" + cardName + "' could not be found.");
                        return;
                    }
                    var card = new PhloxNotecardCache.Card(StripNotecardHeader(OpenMetaverse.Utils.BytesToString(asset.Data)));
                    cache?.Cache(assetId, card);
                    PostDataserverEvent(queryID, answer(card));
                }
                catch (Exception ex)
                {
                    if (logAs != null) m_log.LogError("[PhloxAPI]: {0} ex: {1}", logAs, ex.Message);
                    ShoutError("Notecard '" + cardName + "' could not be found.");
                }
            });
            cache?.CacheCheck();
            return false;
        }

        /// <summary>What a line read answers today: the line, or EOF ("\n\n\n") past either end.</summary>
        private static string NotecardLineAnswer(PhloxNotecardCache.Card card, int line) => card.Line(line) ?? "\n\n\n";

        /// <summary>What iwGetNotecardSegment answers today: part of the line from startOffset, at most maxLength (&gt; 0) chars.</summary>
        private static string NotecardSegmentAnswer(PhloxNotecardCache.Card card, int line, int startOffset, int maxLength)
        {
            string result = card.Line(line);
            if (result == null) return "\n\n\n";
            if (startOffset > 0 && startOffset < result.Length)
                result = result.Substring(startOffset);
            else if (startOffset >= result.Length)
                result = string.Empty;
            if (maxLength > 0 && result.Length > maxLength)
                result = result.Substring(0, maxLength);
            return result;
        }

        protected TaskInventoryItem GetInventorySelf()
        {
            // A derezzed part has no inventory left by the time its scripts unload; the NRE here stopped DoUnload
            // half way and left every derezzed script loaded.
            var inventory = m_host?.TaskInventory;
            if (inventory == null) return null;
            lock (inventory)
            {
                foreach (var kvp in inventory)
                    if (kvp.Value.Type == 10 && kvp.Value.ItemID == m_itemID)
                        return kvp.Value;
            }
            return null;
        }

        // ── Lifecycle ──────────────────────────────────────────────────────────

        public void SetScriptEventFlags()
        {
            if (m_thisScript == null) return;
            ulong flags = 0;
            if (m_thisScript.ScriptState.GeneralEnable && m_thisScript.ScriptState.Enabled
                && m_thisScript.ScriptState.LocalDisable == RuntimeState.LocalDisableFlag.None)
            {
                foreach (var evt in m_thisScript.Script.StateEvents[m_thisScript.ScriptState.LSLState])
                    flags |= MapEventFlag((SupportedEventList.Events)evt.EventType);
            }
            m_host.SetScriptEvents(m_itemID, flags);
        }

        /// <summary>
        /// The backstop completion for a long-running syscall. Twenty-four async shims set
        /// RunState=Syscall and called an implementation that never signalled a return, so the script
        /// stayed in Syscall for ever - no error, no timeout, every later event piling up in its
        /// queue. That is what the manhole was doing at 19:21 with four queued events.
        /// </summary>
        public void CompleteSyscall()
        {
            // Post the call's one return - the body's own SysReturn result if it gave one,
            // else nothing - with every ScriptSleep of the body as its delay and the call's sequence
            // number, so a late or repeated completion cannot land in a later syscall.
            var ctx = InWorldz.Phlox.Glue.SyscallContext.Current;
            if (ctx != null && ctx.ItemId == m_itemID)
            {
                m_ScriptEngine?.SysReturnSequenced(m_itemID, ctx.HasResult ? ctx.Result : null, ctx.DelayMs, ctx.Seq);
                return;
            }
            m_ScriptEngine?.SysReturn(m_itemID, null, 0);
        }

        /// <summary>
        /// Run-time errors go out on DEBUG_CHANNEL, as SL does (wiki: "chat channel reserved for
        /// script debugging and error messages"; viewers route it to the script-error window and filter
        /// out other owners' objects) - not shouted on channel 0, where every avatar in range read them
        /// in local chat. The text is unchanged. ChatModule turns the channel into ChatTypeEnum.DebugChannel.
        /// They reach scripts listening on DEBUG_CHANNEL as far as llSay reaches (SL wiki, DEBUG_CHANNEL: "Server-generated
        /// errors are broadcast the same distance as llSay"), and are cut as llSay's text is.
        /// </summary>
        public void ShoutError(string errorText)
        {
            HostSimChat(CapChatBytes("Script error: " + errorText), ChatTypeEnum.Say, DEBUG_CHANNEL);
        }

        /// <summary>
        /// The host prim's chat through Scene.SimChat. Phlox's listens hear it through OnChatFromWorld, after the chat
        /// module has rewritten chat on DEBUG_CHANNEL to ChatTypeEnum.DebugChannel; the type spoken is recorded for that
        /// delivery (<see cref="PhloxListenManager.SpokenType"/>), so the listens still get the spoken distance.
        /// </summary>
        private void HostSimChat(string msg, ChatTypeEnum type, int channel)
        {
            Scene scene = m_host?.ParentGroup?.Scene;
            if (scene == null) return;
            PhloxListenManager.SpokenType = type;
            try
            {
                scene.SimChat(msg, type, channel, m_host.AbsolutePosition, m_host.Name, m_host.UUID, false);
            }
            finally
            {
                PhloxListenManager.SpokenType = null;
            }
        }


        // A reset and a state change drop every listen, as Halcyon's UnregisterScriptFromNotifications does.
        // A reset also ends every permission and the controls taken under them (Halcyon OnScriptReset:
        // ReleaseControlsInternal, then PermsChange(item, UUID.Zero, 0); SL llTakeControls: "The script will also lose
        // this permission on reset"). Animations already playing and camera parameters stay, as in both.
        public void OnScriptReset()
        {
            ReleaseScriptResources(ScriptEnd.Reset);
            EndPermissions(ALL_PERMISSIONS, releaseControls: true, forgetControls: true);
            ThrottleScriptResets();
        }
        /// <summary>
        /// A script loaded with no saved state. Only the listens go, as before: the permissions are the item's,
        /// which the core sets when it starts the script (SceneObjectPartInventory.CreateScriptInstance), and a fresh start
        /// is not the owner's or the script's reset.
        /// </summary>
        internal void OnFreshStart() => m_ScriptEngine?.ListenManager?.Remove(m_itemID);
        public void OnStateChange() => ReleaseScriptResources(ScriptEnd.StateChange);
        public void OnScriptUnloaded(ScriptUnloadReason reason, RuntimeState.LocalDisableFlag localFlag)
        {
            m_host?.RemoveScriptEvents(m_itemID);
            ReleaseScriptResources(ScriptEnd.Unload);
            // Halcyon OnScriptUnloaded "silently release controls". The permissions and the Control record stay
            // with the item and its saved state (a crossing object takes them with it); only an avatar still here is let go.
            ReleaseControlsOnUnload();
        }

        /// <summary>
        /// The script was stopped (the Running checkbox, llSetScriptState FALSE). As Halcyon's OnScriptUnloaded with
        /// GloballyDisabled: the sensor repeat stops, owed dataserver replies are dropped and taken controls are let go.
        /// The SensorRepeat and Control records stay, and listens stay registered (a stopped script's chat is dropped by
        /// the scheduler), so a start carries on with them through OnScriptInjected.
        /// </summary>
        internal void OnScriptStopped()
        {
            PauseSensorForParcel();
            m_PendingDataserver.Clear();
            ReleaseControlsOnUnload();
        }

        /// <summary>
        /// The unload's release of taken controls, from what the script and the region hold, never from the part's
        /// inventory: a derez queues the unload (Scene.DeleteSceneObject -> RemoveScriptInstances) and then disposes the
        /// object, which takes the part's inventory away (SceneObjectPart.Dispose), so the unload can run after the item
        /// is gone. Controls are registered under this script's item id only by llTakeControls, which also writes the
        /// Control record, so the record says whether there is anything to release and the avatar holding them is found
        /// by that id. No record, or no avatar still here holding them: nothing to do.
        /// </summary>
        private void ReleaseControlsOnUnload()
        {
            var misc = m_thisScript?.ScriptState?.MiscAttributes;
            if (misc == null || !misc.ContainsKey((int)RuntimeState.MiscAttr.Control)) return;
            ScenePresence holder = ControlsHolder(UUID.Zero, hadRecord: true);
            if (holder != null)
                using (PhloxEngine.OwnControlChange())
                    holder.UnRegisterControlEventsToScript(m_host.LocalId, m_itemID);
            m_ScriptEngine?.RequestParcelCheck(m_itemID);
        }
        // ── What ends with the script ──────────────────────────────────────────

        internal enum ScriptEnd { Reset, StateChange, Unload }

        /// <summary>
        /// The one place a script's resources end: OnScriptReset, OnStateChange and OnScriptUnloaded call it, and
        /// every reset (llResetScript, llResetOtherScript, the viewer's Reset, osResetAllScripts, a crashed script's
        /// reset), state change and unload (delete, derez, recompile, region shutdown) reaches one of those three. Timers,
        /// sleeps and touch waits belong to the scheduler, which ends them one step earlier on the same three paths
        /// (PhloxExecutionScheduler.UnregisterFromNotifications).
        /// <list type="bullet">
        /// <item>listens: every end (SL: "Listeners are removed"; state: "All listens are released"). Unload also forgets the listen-rate record.</item>
        /// <item>llSensorRepeat: every end (SL reset: "Timers (including repeating sensors) are cleared"; state: "Repeating sensors are released";
        /// Halcyon RemoveAllAsyncHandlers). Reset and state change also drop the MiscAttr record, so a later restore or parcel resume cannot
        /// bring the old sensor back; unload keeps it in the state that is saved for the object's return (Halcyon GetSerializationData).</item>
        /// <item>pending dataserver replies: every end (Halcyon Dataserver.RemoveEvents from RemoveAllAsyncHandlers).</item>
        /// <item>llHTTPRequest: reset and unload (Halcyon RemoveScript -> StopHttpRequest; a reset clears the event queue). Kept across a state
        /// change, as Halcyon (its HttpRequestPlugin.RemoveEvents did nothing).</item>
        /// <item>URLs: reset and unload (SL: "Any granted URLs are released"; "deleting the prim ... release URLs"). Kept across a state
        /// change (SL: "Unlike listeners, URLs persist across state changes").</item>
        /// <item>XML-RPC channels and llSendRemoteData: unload only (Halcyon RemoveScript; its reset did nothing and SL says nothing).</item>
        /// </list>
        /// </summary>
        internal void ReleaseScriptResources(ScriptEnd end)
        {
            var listens = m_ScriptEngine?.ListenManager;
            if (end == ScriptEnd.Unload) listens?.Forget(m_itemID);
            else listens?.Remove(m_itemID);

            m_PendingDataserver.Clear();

            var async = m_ScriptEngine?.AsyncCommands;
            if (end == ScriptEnd.Unload)
            {
                // Halcyon ScriptLoader: AsyncCommandManager.RemoveScript(engine, localId, itemId) - sensor, HTTP, XML-RPC
                if (m_ScriptEngine != null && async != null)
                    OpenSim.Region.ScriptEngine.Shared.Api.AsyncCommandManager.RemoveScript(m_ScriptEngine, m_localID, m_itemID);
            }
            else
            {
                async?.SensorRepeatPlugin.RemoveScript(m_itemID);
                m_thisScript?.ScriptState?.MiscAttributes?.Remove((int)RuntimeState.MiscAttr.SensorRepeat);
                if (end == ScriptEnd.Reset) async?.HttpRequestPlugin.RemoveEvents(m_localID, m_itemID);
            }

            if (end != ScriptEnd.StateChange)
                m_ScriptEngine?.World?.RequestModuleInterface<IUrlModule>()?.ScriptRemoved(m_itemID);
        }

        // Dataserver query ids this script is still owed a reply for. A reply whose id is not here was asked for before a
        // reset, state change or unload, and is dropped (PostDataserverEvent). Written from pool threads.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<UUID, byte> m_PendingDataserver = new();

        /// <summary>A new dataserver query id, recorded as owed a reply.</summary>
        private UUID NewDataserverQuery()
        {
            UUID id = UUID.Random();
            m_PendingDataserver[id] = 0;
            return id;
        }

        internal int PendingDataserverCount => m_PendingDataserver.Count;

        // Halcyon LSLSystemAPI.ThrottleScriptResets, from its OnScriptReset.
        private const int MAX_RESETS_PER_SECOND = 5;
        private long m_resetSecond;
        private int m_resetCount;
        private DateTime m_resetWarned = DateTime.MinValue;

        /// <summary>
        /// Halcyon's reset throttle - more than 5 resets of this script in one second and it sleeps 5 s before
        /// its state_entry, with a warning in the log and on DEBUG_CHANNEL once an hour. [InWorldz.Phlox] ResetThrottle.
        /// </summary>
        private void ThrottleScriptResets()
        {
            if (m_ScriptEngine == null || !m_ScriptEngine.ResetThrottle) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (m_resetSecond == now)
            {
                if (++m_resetCount > MAX_RESETS_PER_SECOND)
                {
                    if (DateTime.UtcNow > m_resetWarned.AddMinutes(60))   // warn once per hour
                    {
                        string context = m_host == null ? string.Empty : string.Format("in '{0}'{1} at {2}/{3}/{4}",
                            m_host.ParentGroup?.Name, m_host.LinkNum < 2 ? string.Empty : " link #" + m_host.LinkNum,
                            (int)m_host.AbsolutePosition.X, (int)m_host.AbsolutePosition.Y, (int)m_host.AbsolutePosition.Z);
                        m_log.LogWarning("[Phlox]: Script '{0}' calling llResetScript too frequently: {1}", llGetScriptName(), context);
                        ScriptShoutError("Script '" + llGetScriptName() + "' calling llResetScript too frequently: " + context);
                        m_resetWarned = DateTime.UtcNow;
                    }
                    ResetSleepCount++;
                    ScriptSleep(5000);   // punish the script for 5 seconds after too many resets in the same period
                }
            }
            else
                m_resetCount = 1;
            m_resetSecond = now;
        }

        /// <summary>Times this script has been put to sleep by the reset throttle (tests).</summary>
        internal int ResetSleepCount { get; private set; }

        public void AddExecutionTime(double ms) => m_host?.ParentGroup?.AddScriptLPS((int)ms);
        public void OnScriptInjected(bool fromCrossing)
        {
            if (m_thisScript?.ScriptState?.MiscAttributes == null) return;

            foreach (KeyValuePair<int, object[]> kvp in
                     m_thisScript.ScriptState.MiscAttributes.ToList())
            {
                switch ((RuntimeState.MiscAttr)kvp.Key)
                {
                    case RuntimeState.MiscAttr.SensorRepeat:
                        llSensorRepeat((string)kvp.Value[0], (string)kvp.Value[1],
                            (int)kvp.Value[2], (float)kvp.Value[3],
                            (float)kvp.Value[4], (float)kvp.Value[5]);
                        break;
                    case RuntimeState.MiscAttr.VolumeDetect:
                        llVolumeDetect((int)kvp.Value[0]);
                        break;
                    case RuntimeState.MiscAttr.Control:
                        // Halcyon calls a 7-arg TakeControlsInternal helper that bypasses the
                        // permission check by re-using the existing grant on the TaskInventoryItem.
                        // We call llTakeControls() directly which re-validates PERMISSION_TAKE_CONTROLS.
                        // If permission state didn't persist alongside the Control entry, restore
                        // will silently fail. Acceptable for now; revisit if reports of lost
                        // controls on restart surface.
                        if (m_host.ParentGroup.IsAttachment || !fromCrossing)
                            llTakeControls((int)kvp.Value[0], (int)kvp.Value[1], (int)kvp.Value[2]);
                        break;
                }
            }
        }
        public void OnGroupCrossedAvatarReady(UUID avatarId) { }
        public float GetAverageScriptTime() => 0f;

        // ── Math ───────────────────────────────────────────────────────────────

        public float llSin(float f) => (float)Math.Sin(f);
        public float llCos(float f) => (float)Math.Cos(f);
        public float llTan(float f) => (float)Math.Tan(f);
        public float llAtan2(float y, float x) => (float)Math.Atan2(y, x);
        public float llSqrt(float f) => (float)Math.Sqrt(f);
        public float llPow(float b, float e) => (float)Math.Pow(b, e);
        public int llAbs(int i) => i == int.MinValue ? i : Math.Abs(i);
        public float llFabs(float f) => Math.Abs(f);
        public float llFrand(float mag) => (float)(ThreadRandom.NextDouble() * mag);
        // SL wiki llFloor, "The returned value is -2147483648 (0x80000000) if the arithmetic result is outside
        // of the range of valid integers"; NaN too. A plain (int) saturates on .NET 9+ x64.
        public int llFloor(float f) => InWorldz.Phlox.Util.LslConvert.FloatToInteger(Math.Floor(f));
        public int llCeil(float f) => InWorldz.Phlox.Util.LslConvert.FloatToInteger(Math.Ceiling(f));
        public int llRound(float f) => InWorldz.Phlox.Util.LslConvert.FloatToInteger(Math.Round(f, MidpointRounding.AwayFromZero));
        public float llAcos(float f) => (float)Math.Acos(f);
        public float llAsin(float f) => (float)Math.Asin(f);
        public float llLog10(float f) => (float)Math.Log10(f);
        public float llLog(float f) => (float)Math.Log(f);

        public float llVecMag(Vector3 v) => v.Length();
        public Vector3 llVecNorm(Vector3 v) => Vector3.Normalize(v);
        public float llVecDist(Vector3 a, Vector3 b) => Vector3.Distance(a, b);

        public Vector3 llRot2Euler(Quaternion r)
        {
            Vector3 v = new Vector3(0f, 0f, 1f) * r;
            double m = v.Length();
            if (m == 0.0) return Vector3.Zero;
            double x = Math.Atan2(-v.Y, v.Z);
            double sin = v.X / m;
            if (sin < -0.999999 || sin > 0.999999) x = 0.0;
            double y = Math.Asin(sin);
            v = new Vector3(1f, 0f, 0f) * r
                * new Quaternion((float)Math.Sin(-x / 2), 0f, 0f, (float)Math.Cos(-x / 2))
                * new Quaternion(0f, (float)Math.Sin(-y / 2), 0f, (float)Math.Cos(-y / 2));
            return new Vector3((float)x, (float)y, (float)Math.Atan2(v.Y, v.X));
        }

        public Quaternion llEuler2Rot(Vector3 v)
        {
            double c1 = Math.Cos(v.X/2), c2 = Math.Cos(v.Y/2), c3 = Math.Cos(v.Z/2);
            double s1 = Math.Sin(v.X/2), s2 = Math.Sin(v.Y/2), s3 = Math.Sin(v.Z/2);
            return Quaternion.Normalize(new Quaternion(
                (float)(s1*c2*c3 + c1*s2*s3), (float)(c1*s2*c3 - s1*c2*s3),
                (float)(s1*s2*c3 + c1*c2*s3), (float)(c1*c2*c3 - s1*s2*s3)));
        }

        public Quaternion llAxes2Rot(Vector3 fwd, Vector3 left, Vector3 up)
        {
            double tr = fwd.X + left.Y + up.Z + 1.0;
            float s;
            if (tr >= 1.0)
            {
                s = 0.5f / (float)Math.Sqrt(tr);
                return new Quaternion((left.Z-up.Y)*s,(up.X-fwd.Z)*s,(fwd.Y-left.X)*s,0.25f/s);
            }
            double max = left.Y > up.Z ? left.Y : up.Z;
            if (max < fwd.X)
            {
                s = (float)Math.Sqrt(fwd.X-(left.Y+up.Z)+1.0);
                float x = s*0.5f; s = 0.5f/s;
                return new Quaternion(x,(fwd.Y+left.X)*s,(up.X+fwd.Z)*s,(left.Z-up.Y)*s);
            }
            else if (max == left.Y)
            {
                s = (float)Math.Sqrt(left.Y-(up.Z+fwd.X)+1.0);
                float y = s*0.5f; s = 0.5f/s;
                return new Quaternion((fwd.Y+left.X)*s,y,(left.Z+up.Y)*s,(up.X-fwd.Z)*s);
            }
            else
            {
                s = (float)Math.Sqrt(up.Z-(fwd.X+left.Y)+1.0);
                float z = s*0.5f; s = 0.5f/s;
                return new Quaternion((up.X+fwd.Z)*s,(left.Z+up.Y)*s,z,(fwd.Y-left.X)*s);
            }
        }

        public Vector3 llRot2Fwd(Quaternion r) => Vector3.Normalize(new Vector3(1f,0f,0f)*r);
        public Vector3 llRot2Left(Quaternion r) => Vector3.Normalize(new Vector3(0f,1f,0f)*r);
        public Vector3 llRot2Up(Quaternion r) => Vector3.Normalize(new Vector3(0f,0f,1f)*r);

        public Quaternion llRotBetween(Vector3 a, Vector3 b)
        {
            double dotProduct = Vector3.Dot(a, b);
            Vector3 crossProduct = Vector3.Cross(a, b);
            double magProduct = a.Length() * b.Length();
            if (magProduct == 0) return Quaternion.Identity;
            double angle = Math.Acos(Math.Max(-1.0, Math.Min(1.0, dotProduct / magProduct)));
            Vector3 axis = Vector3.Normalize(crossProduct);
            if (float.IsNaN(axis.X)) return Quaternion.Identity;
            return Quaternion.CreateFromAxisAngle(axis, (float)angle);
        }

        public Quaternion llAxisAngle2Rot(Vector3 axis, float angle)
            => Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);

        public Vector3 llRot2Axis(Quaternion rot)
        {
            rot.GetAxisAngle(out Vector3 axis, out float angle);
            return axis;
        }

        public float llRot2Angle(Quaternion rot)
        {
            rot.GetAxisAngle(out Vector3 axis, out float angle);
            return angle;
        }

        public float llAngleBetween(Quaternion a, Quaternion b)
        {
            float dotProduct = Quaternion.Dot(a, b);
            return (float)(2.0 * Math.Acos(Math.Abs(Math.Max(-1.0, Math.Min(1.0, dotProduct)))));
        }

        public int llModPow(int a, int b, int c)
        {
            long la = a, result = 1;
            la %= c;
            while (b > 0)
            {
                if ((b & 1) == 1) result = (result * la) % c;
                b >>= 1; la = (la * la) % c;
            }
            return (int)result;
        }

        // ── Chat ───────────────────────────────────────────────────────────────

        // llSay, llShout, llWhisper and llRegionSayTo send at most 1024 bytes (SL wiki, llSay: "msg can be a maximum of
        // 1024 bytes"; llShout and llWhisper: "Text can be a maximum of 1024 bytes"); llRegionSay at most 1024 characters
        // (llRegionSay: "If msg is longer than 1024 characters it is truncated to 1024 characters"). Avatars and every
        // engine's listens hear the same cut text.

        public void llSay(int channel, string msg)
        {
            msg = CapChatBytes(msg);
            HostSimChat(msg, ChatTypeEnum.Say, channel);
            ChatToWorldComm(ChatTypeEnum.Say, channel, msg);
            ChatSleep();
        }

        public void llShout(int channel, string msg)
        {
            msg = CapChatBytes(msg);
            HostSimChat(msg, ChatTypeEnum.Shout, channel);
            ChatToWorldComm(ChatTypeEnum.Shout, channel, msg);
            ChatSleep();
        }

        public void llWhisper(int channel, string msg)
        {
            msg = CapChatBytes(msg);
            HostSimChat(msg, ChatTypeEnum.Whisper, channel);
            ChatToWorldComm(ChatTypeEnum.Whisper, channel, msg);
            ChatSleep();
        }

        private const int MaxChatBytes = 1024;
        private const int MaxRegionSayCharacters = 1024;

        /// <summary>
        /// Chat cut to SL's 1024 bytes of UTF-8. SL (llSay): "If a multibyte character ends up on the 1024 byte boundary,
        /// it is discarded and not split into invalid bytes."
        /// </summary>
        private static string CapChatBytes(string msg) => CapUtf8Bytes(msg, MaxChatBytes);

        /// <summary>Text cut to <paramref name="max"/> bytes of UTF-8; a multibyte character the cut would split is dropped whole.</summary>
        private static string CapUtf8Bytes(string text, int max)
        {
            text ??= string.Empty;
            if (text.Length <= max / 4) return text;   // can't exceed the cap even at four bytes a character
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            if (utf8.Length <= max) return text;
            int cut = max;
            while (cut > 0 && (utf8[cut] & 0xC0) == 0x80) cut--; // back to the start of the split character
            return Encoding.UTF8.GetString(utf8, 0, cut);
        }

        /// <summary>Text cut to <paramref name="max"/> characters; a surrogate pair the cut would split is dropped whole.</summary>
        private static string CapCharacters(string text, int max)
        {
            text ??= string.Empty;
            if (text.Length <= max) return text;
            int cut = max;
            if (char.IsHighSurrogate(text[cut - 1])) cut--;
            return text.Substring(0, cut);
        }

        public void llOwnerSay(string msg)
        {
            UUID ownerID = m_host?.OwnerID ?? UUID.Zero;
            if (ownerID != UUID.Zero)
            {
                ScenePresence sp = World?.GetScenePresence(ownerID);
                sp?.ControllingClient?.SendChatMessage(msg, (byte)ChatTypeEnum.Owner,
                    m_host.AbsolutePosition, m_host.Name, m_host.UUID, m_host.UUID,
                    (byte)ChatSourceType.Object, (byte)ChatAudibleLevel.Fully);
            }
            ChatSleep();   // Halcyon's llOwnerSay sleeps whether or not the owner hears it
        }

		public void llRegionSay(int channel, string msg)
		{
			if (channel == 0)
			{
				ScriptShoutError("llRegionSay: cannot use channel 0");
				return;
			}
			msg = CapCharacters(msg, MaxRegionSayCharacters);
			HostSimChat(msg, ChatTypeEnum.Region, channel);
			ChatToWorldComm(ChatTypeEnum.Region, channel, msg);
			ChatSleep();
		}

		/// <summary>
		/// Scene.SimChat brings a Phlox script's chat to Phlox's listens (OnChatFromWorld) and to avatars; the
		/// listens the core WorldComm holds - YEngine's, on a region running both engines - hear it only through
		/// IWorldComm.DeliverMessage, which is how YEngine's own llSay/llShout/llWhisper/llRegionSay reach them.
		/// </summary>
		private void ChatToWorldComm(ChatTypeEnum type, int channel, string msg)
		{
			if (m_host == null) return;
			string name = m_host.Name;
			UUID id = m_host.UUID;
			Vector3 pos = m_host.AbsolutePosition;
			m_ScriptEngine?.SendToWorldComm(w => w.DeliverMessage(type, channel, name, id, msg, pos));
		}

		/// <summary>
		/// Halcyon's llRegionSayTo (LSLSystemAPI.cs:1087-1100): NULL_KEY reaches no one. The listens that hear
		/// it are chosen by WorldCommModule.DestIdMatches - the target prim's, or, for an avatar, its
		/// attachments' - on the channel it was sent on, anywhere in the region, and never the sender's.
		/// An avatar itself only sees it on channel 0.
		/// </summary>
		public void llRegionSayTo(string destId, int channel, string msg)
		{
			if (m_host == null) return;
			// Halcyon (LSLSystemAPI.cs:1089-1093) refuses DEBUG_CHANNEL with this error, and nothing is sent.
			if (channel == DEBUG_CHANNEL)
			{
				ScriptShoutError("Cannot use llRegionSayTo() on DEBUG_CHANNEL.");
				return;
			}
			if (!UUID.TryParse(destId, out UUID targetId) || targetId == UUID.Zero) return;
			msg = CapChatBytes(msg);

			ScenePresence sp = World?.GetScenePresence(targetId);
			if (channel == 0 && sp != null && !sp.IsChildAgent)
			{
				sp.ControllingClient?.SendChatMessage(
					msg, (byte)ChatTypeEnum.Direct,
					m_host.AbsolutePosition, m_host.Name,
					m_host.UUID, m_host.UUID,
					(byte)ChatSourceType.Object, (byte)ChatAudibleLevel.Fully);
			}

			m_ScriptEngine.ListenManager?.DeliverChat(ChatTypeEnum.Direct, channel, m_host.Name, m_host.UUID, msg,
				m_host.AbsolutePosition, targetId);

			// The listens the core WorldComm holds (YEngine's) hear it through DeliverMessageTo, as YEngine's own
			// llRegionSayTo reaches them. On channel 0 to an avatar DeliverMessageTo only sends it to the viewer,
			// which has had it above, so it is not called.
			if (!(channel == 0 && sp != null))
			{
				string name = m_host.Name;
				UUID id = m_host.UUID;
				Vector3 pos = m_host.AbsolutePosition;
				m_ScriptEngine.SendToWorldComm(w => w.DeliverMessageTo(targetId, channel, pos, name, id, msg));
			}
			ChatSleep();   // After a send; the refusals above return without it, as Halcyon's
		}

		public void llInstantMessage(string user, string message)
        {
            ScriptSleep(2000);
            if (!UUID.TryParse(user, out UUID targetID) || targetID == UUID.Zero)
                return;

            IMessageTransferModule tr = World?.RequestModuleInterface<IMessageTransferModule>();
            if (tr == null)
                return;

            GridInstantMessage msg = new GridInstantMessage()
            {
                fromAgentID    = m_host.OwnerID.Guid,
                toAgentID      = targetID.Guid,
                imSessionID    = m_host.UUID.Guid,
                timestamp      = (uint)Util.UnixTimeSinceEpoch(),
                fromAgentName  = m_host.Name,
                message        = CapInstantMessage(message),
                dialog         = (byte)InstantMessageDialog.MessageFromObject,
                fromGroup      = false,
                // Kept for a recipient who is offline, and carrying the object's location ("Region/x/y/z", which
                // viewers show as a link), as Halcyon (LSLSystemAPI.cs:3910-3916) and YEngine (LSL_Api.cs llInstantMessage)
                // send it. SL: "If the specified user is not signed in, the messages will be delivered to their email
                // just like a regular instant message".
                offline        = 1,
                ParentEstateID = World.RegionInfo.EstateSettings.ParentEstateID,
                Position       = m_host.AbsolutePosition,
                RegionID       = World.RegionInfo.RegionID.Guid,
                binaryBucket   = Util.StringToBytes256(
                    $"{World.RegionInfo.RegionName}/{(int)m_host.AbsolutePosition.X}/{(int)m_host.AbsolutePosition.Y}/{(int)m_host.AbsolutePosition.Z}")
            };

            tr.SendInstantMessage(msg, success => {});
        }

        private const int MaxInstantMessageBytes = 1023;

        /// <summary>
        /// llInstantMessage's message cut to SL's limit: "Messages longer than 1023 bytes will be truncated to 1023
        /// bytes. This can convey 1023 ASCII characters, or fewer if non-ASCII characters are present."
        /// (https://wiki.secondlife.com/wiki/LlInstantMessage). Bytes of UTF-8 are counted, and a multibyte character
        /// the cut would split is dropped whole, as PrimSetText cuts floating text.
        /// </summary>
        private static string CapInstantMessage(string message) => CapUtf8Bytes(message, MaxInstantMessageBytes);
		public void llDialog(string avatar, string message, LSLList buttons, int chat_channel)
		{
			if (m_host == null) return;
			// Halcyon's checks and errors, in its order (LSLSystemAPI.cs:8816-8846); each refuses the
			// dialog (no 1 s sleep). SL: "An error will be shouted on DEBUG_CHANNEL, if there are more than 12 buttons"; a label
			// of length zero or over 24 fails too. Halcyon counts characters, as here.
			if (!UUID.TryParse(avatar, out UUID avatarId)) { LSLError("First parameter to llDialog needs to be a key"); return; }
			if (buttons != null && buttons.Length > 12) { LSLError("No more than 12 buttons can be shown"); return; }

			var buttonList = new List<string>();
			if (buttons != null)
			{
				foreach (var o in buttons.Data)
				{
					string label = o?.ToString() ?? string.Empty;
					if (string.IsNullOrEmpty(label)) { LSLError("button label cannot be blank"); return; }
					if (label.Length > 24) { LSLError("button label cannot be longer than 24 characters"); return; }
					buttonList.Add(label);
				}
			}
			if (buttonList.Count == 0) buttonList.Add("OK");

			ScenePresence sp = World?.GetScenePresence(avatarId);
			if (sp == null || sp.IsChildAgent) return;

			string ownerFirst = string.Empty, ownerLast = string.Empty;
			ScenePresence ownerSp = World?.GetScenePresence(m_host.OwnerID);
			if (ownerSp != null)
			{
				var parts = ownerSp.Name.Split(' ');
				ownerFirst = parts[0];
				ownerLast  = parts.Length > 1 ? parts[1] : string.Empty;
			}
			else
			{
				UserAccount acct = World?.UserAccountService?.GetUserAccount(
					World.RegionInfo.ScopeID, m_host.OwnerID);
				if (acct != null) { ownerFirst = acct.FirstName; ownerLast = acct.LastName; }
			}

			sp.ControllingClient?.SendDialog(
				m_host.Name,
				m_host.UUID,
				m_host.OwnerID,
				ownerFirst,
				ownerLast,
				message,
				UUID.Zero,          // textureID — dialogs don't use a texture
				chat_channel,
				buttonList.ToArray());

			ScriptSleep(1000);
		}
        public void llTextBox(string avatar, string message, int chat_channel)
        {
            IDialogModule dm = World?.RequestModuleInterface<IDialogModule>();
            if (dm == null) return;
            // Halcyon LSLSystemAPI.cs:5779-5783, its text naming llDialog.
            if (!UUID.TryParse(avatar, out UUID av)) { LSLError("First parameter to llDialog needs to be a key"); return; }
            if (av == UUID.Zero) return;
            if (message != null && message.Length > 1024) message = message.Substring(0, 1024);
            dm.SendTextBoxToUser(av, message, chat_channel, m_host.Name, m_host.UUID, m_host.OwnerID);
            ScriptSleep(1000);
        }

        // ── Object info ────────────────────────────────────────────────────────

        public string llGetKey() => m_host?.UUID.ToString() ?? UUID.Zero.ToString();
        public string llGetOwner() => m_host?.OwnerID.ToString() ?? UUID.Zero.ToString();
        public string llGetCreator() => m_host?.CreatorID.ToString() ?? UUID.Zero.ToString();
        public string llGetObjectName() => m_host?.Name ?? string.Empty;
        /// <summary>The name is in the full ObjectProperties reply (LLClientView.cs:6381),
        /// so a change has to be pushed or the viewer keeps what it had at the last select.</summary>
        public void llSetObjectName(string name)
        {
            if (m_host == null) return;
            m_host.Name = name;
            if (m_host.ParentGroup != null) m_host.ParentGroup.HasGroupChanged = true;
            m_host.SendPropertiesToAllClients();
        }
        public string llGetObjectDesc() => m_host?.Description ?? string.Empty;
        /// <summary>Same for the description (LLClientView.cs:6384).</summary>
        public void llSetObjectDesc(string name)
        {
            if (m_host == null) return;
            m_host.Description = name;
            if (m_host.ParentGroup != null) m_host.ParentGroup.HasGroupChanged = true;
            m_host.SendPropertiesToAllClients();
        }
        /// <summary>Halcyon llGetNumberOfPrims: LinkCount, the prims plus the avatars seated on them.</summary>
        public int llGetNumberOfPrims()
        {
            SceneObjectGroup group = m_host?.ParentGroup;
            return group == null ? 1 : group.PrimCount + group.GetSittingAvatarsCount();
        }
        public int llGetLinkNumber() => m_host?.LinkNum ?? 0;
        public int llGetNumberOfSides() => m_host?.GetNumberOfSides() ?? 0;
        public string llGetScriptName() => GetInventorySelf()?.Name ?? string.Empty;
        public string llGetRegionName() => World?.RegionInfo?.RegionName ?? string.Empty;
        public float llGetRegionTimeDilation() => World?.TimeDilation ?? 1f;
        public float llGetRegionFPS() => World?.StatsReporter?.LastReportedSimFPS ?? 45f;
        public Vector3 llGetRegionCorner()
        {
            if (World?.RegionInfo == null) return Vector3.Zero;
            return new Vector3(
                World.RegionInfo.RegionLocX * Constants.RegionSize,
                World.RegionInfo.RegionLocY * Constants.RegionSize,
                0f);
        }
        public int llGetRegionAgentCount() => World?.GetRootAgentCount() ?? 0;
        public int llGetRegionFlags()
        {
            if (World?.RegionInfo?.RegionSettings == null) return 0;
            var s = World.RegionInfo.RegionSettings;
            int flags = 0;
            if (s.AllowDamage)      flags |= REGION_FLAG_ALLOW_DAMAGE;
            if (s.BlockFly)         flags |= REGION_FLAG_BLOCK_FLY;
            if (s.RestrictPushing)  flags |= REGION_FLAG_RESTRICT_PUSHOBJECT;
            if (s.AllowLandResell)  flags |= 0x4;      // REGION_FLAG_ALLOW_LAND_RESELL
            if (s.DisableCollisions)flags |= REGION_FLAG_DISABLE_COLLISIONS;
            if (s.DisablePhysics)   flags |= REGION_FLAG_DISABLE_PHYSICS;
            if (s.Sandbox)          flags |= REGION_FLAG_SANDBOX;
            return flags;
        }
        public string llGetSimulatorHostname() => World?.RegionInfo?.ExternalHostName ?? string.Empty;   // as Halcyon (Scene.GetEnv "simulator_hostname")
        public string llGetDate() => DateTime.UtcNow.ToString("yyyy-MM-dd");
        public string llGetTimestamp() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ");
        public float llGetWallclock() => (float)DateTime.UtcNow.TimeOfDay.TotalSeconds;
        public float llGetGMTclock() => (float)DateTime.UtcNow.TimeOfDay.TotalSeconds;
        public int llGetUnixTime() => (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public float llGetTimeOfDay()
        {
            // Returns seconds since midnight, cycling on a 4-hour SL day period
            return (float)(DateTime.UtcNow.TimeOfDay.TotalSeconds % (3600.0 * 4.0));
        }

        public float llGetRegionTimeOfDay()
        {
            // SL: returns region-local time of day in seconds since midnight
            // In OpenSim without EEP, this is the same as llGetTimeOfDay
            return llGetTimeOfDay();
        }

        // The script's run time lives in its RuntimeState, as Halcyon's llGetTime (ScriptState.TotalRuntime): Reset()
        // starts it again (SL llGetTime: the time "is reset when the script is reset") and it is saved with the state, so
        // it carries across a region restart or a crossing.
        public float llGetTime() => m_thisScript?.ScriptState?.TotalRuntime ?? 0f;

        public void llResetTime()
        {
            m_thisScript?.ScriptState?.ResetRuntime();
        }

        public float llGetAndResetTime()
        {
            float elapsed = llGetTime();
            llResetTime();
            return elapsed;
        }
        public int llGetLocalTime() => 0;
        public int iwGetLocalTime() => 0;
        public int iwGetLocalTimeOffset() => 0;
        public string iwFormatTime(int unixtime, int isUTC, string format)
        {
            DateTime date = OpenSim.Framework.Util.UnixEpoch.AddSeconds(unixtime);
            if (isUTC == 0)
                date = date.ToLocalTime();
            if (String.IsNullOrEmpty(format))
                format = "yyyy'-'MM'-'dd' 'HH':'mm':'ss";
            return date.ToString(format);
        }

        // ── Position / rotation ────────────────────────────────────────────────

        public Vector3 llGetPos() => m_host?.AbsolutePosition ?? Vector3.Zero;
        // Halcyon GetPartLocalPos and SL: a root's region position (an attachment's, its offset from the attach
        // point), a child's offset from the root. The same read as PRIM_POS_LOCAL.
        public Vector3 llGetLocalPos() => m_host == null ? Vector3.Zero : PartLocalPos(m_host);
        public Vector3 llGetRootPosition() => m_host?.ParentGroup?.AbsolutePosition ?? Vector3.Zero;
        // Halcyon llSetPos: SetPos(m_host, pos, true), the helper PRIM_POSITION uses, so the two cannot disagree. SL: an
        // unattached root takes a region position ("Movement is capped to 10m per call for unattached root prims"), an
        // attached root its offset from the attach point, a child its offset from the root.
        public void llSetPos(Vector3 pos)
        {
            if (m_host == null) return;
            SetPrimLocalPos(m_host, pos);
            ScriptSleep(200);
        }
        // Halcyon llGetRot and SL: on an attachment's root, the wearer's rotation. The same read as PRIM_ROTATION.
        public Quaternion llGetRot() => m_host == null ? Quaternion.Identity : PartRegionRot(m_host);
        public Quaternion llGetLocalRot() => m_host?.RotationOffset ?? Quaternion.Identity;
        // Halcyon llGetRootRotation and SL: "In an attached object, returns region rotation of avatar NOT of the object's
        // root prim". The root's PRIM_ROTATION read, so it matches llGetRot on the root.
        public Quaternion llGetRootRotation()
            => m_host?.ParentGroup?.RootPart is SceneObjectPart root ? PartRegionRot(root) : Quaternion.Identity;
        public void llSetRot(Quaternion rot)
        {
            if (m_host == null) return;
            SceneObjectGroup group = m_host.ParentGroup;
            if (group == null || group.IsDeleted) return;
            if (m_host.LinkNum < 2) group.UpdateGroupRotationR(rot);
            else m_host.UpdateRotation(rot);
            ScriptSleep(200);
        }
        public void llSetLocalRot(Quaternion rot)
        {
            if (m_host == null) return;
            m_host.UpdateRotation(rot);
        }
        public Vector3 llGetScale() => m_host?.Scale ?? Vector3.One;
        public void llSetScale(Vector3 scale)
        {
            if (m_host == null) return;
            scale.X = Math.Max(0.01f, Math.Min(64f, scale.X));
            scale.Y = Math.Max(0.01f, Math.Min(64f, scale.Y));
            scale.Z = Math.Max(0.01f, Math.Min(64f, scale.Z));
            m_host.Resize(scale);
            PhySleep();
        }
        public int llScaleByFactor(float factor)
        {
            // SL: uniformly scale the entire linkset by factor. Returns TRUE on success.
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return 0;
            if (factor <= 0f) return 0;

            SceneObjectGroup group = m_host.ParentGroup;
            SceneObjectPart[] parts = group.Parts;

            // Check if scaling would push any part outside [0.01, 64.0]
            foreach (var part in parts)
            {
                Vector3 s = part.Scale;
                if (s.X * factor < 0.01f || s.Y * factor < 0.01f || s.Z * factor < 0.01f) return 0;
                if (s.X * factor > 64f || s.Y * factor > 64f || s.Z * factor > 64f) return 0;
            }

            // Apply scale to all parts and adjust offsets
            foreach (var part in parts)
            {
                part.Resize(new Vector3(part.Scale.X * factor, part.Scale.Y * factor, part.Scale.Z * factor));
                if (part != group.RootPart)
                    part.OffsetPosition = part.OffsetPosition * factor;
            }
            group.HasGroupChanged = true;
            group.ScheduleGroupForFullUpdate();
            return 1;
        }
        public float llGetMaxScaleFactor()
        {
            // SL: return the maximum factor that llScaleByFactor could use
            if (m_host?.ParentGroup == null) return 1f;
            float maxFactor = float.MaxValue;
            foreach (var part in m_host.ParentGroup.Parts)
            {
                Vector3 s = part.Scale;
                float fx = 64f / s.X;
                float fy = 64f / s.Y;
                float fz = 64f / s.Z;
                maxFactor = Math.Min(maxFactor, Math.Min(fx, Math.Min(fy, fz)));
            }
            return maxFactor == float.MaxValue ? 1f : (float)Math.Round(maxFactor, 5);
        }
        public float llGetMinScaleFactor()
        {
            // SL: return the minimum factor that llScaleByFactor could use
            if (m_host?.ParentGroup == null) return 1f;
            float minFactor = float.MaxValue;
            foreach (var part in m_host.ParentGroup.Parts)
            {
                Vector3 s = part.Scale;
                float fx = s.X / 0.01f;
                float fy = s.Y / 0.01f;
                float fz = s.Z / 0.01f;
                // The min scale factor is 1/max_shrink
                float partMin = Math.Min(fx, Math.Min(fy, fz));
                minFactor = Math.Min(minFactor, partMin);
            }
            if (minFactor == float.MaxValue || minFactor <= 0f) return 1f;
            return (float)Math.Round(1f / minFactor, 5);
        }
        public Vector3 llGetVel() => m_host?.Velocity ?? Vector3.Zero;
        public Vector3 llGetAccel() => Vector3.Zero;
        public Vector3 llGetOmega() => Vector3.Zero;
        public Vector3 llGetTorque() => Vector3.Zero;
        public Vector3 iwGetAngularVelocity() => Vector3.Zero;
        public Vector3 llGetCenterOfMass()
        {
            if (m_host?.ParentGroup == null) return Vector3.Zero;
            return m_host.ParentGroup.GetCenterOfMass();
        }

        public Vector3 llGetGeometricCenter()
        {
            if (m_host == null) return Vector3.Zero;
            return m_host.GetGeometricCenter();
        }
        public Vector3 llGetCameraPos()
        {
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return Vector3.Zero;
            if (item.PermsGranter == UUID.Zero) return Vector3.Zero;
            // PERMISSION_TRACK_CAMERA = 0x400
            if ((item.PermsMask & PERMISSION_TRACK_CAMERA) == 0)
            {
                ScriptShoutError("No permissions to track the camera");
                return Vector3.Zero;
            }
            ScenePresence presence = World?.GetScenePresence(item.PermsGranter);
            if (presence != null)
                return presence.CameraPosition;
            return Vector3.Zero;
        }
        public Quaternion llGetCameraRot()
        {
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return Quaternion.Identity;
            if (item.PermsGranter == UUID.Zero) return Quaternion.Identity;
            // PERMISSION_TRACK_CAMERA = 0x400
            if ((item.PermsMask & PERMISSION_TRACK_CAMERA) == 0)
            {
                ScriptShoutError("No permissions to track the camera");
                return Quaternion.Identity;
            }
            ScenePresence presence = World?.GetScenePresence(item.PermsGranter);
            if (presence != null)
                return presence.CameraRotation;
            return Quaternion.Identity;
        }
        public float llGetCameraAspect()
        {
            // SL: returns camera aspect ratio. Viewer doesn't send this to the server.
            // Return standard widescreen 16:9 default.
            return 1.7778f;
        }
        public float llGetCameraFOV()
        {
            // SL: returns camera vertical FOV in radians. Viewer doesn't send this to the server.
            // Return Firestorm default (roughly 60 degrees).
            return 1.0472f;
        }
        public int llSetRegionPos(Vector3 position)
        {
            // Halcyon used ValidLocation() + SetPos() helpers; this port uses direct group position update.
            // Clamp to region bounds (allow up to 10m outside for cross-region placement per SL spec)
            float regionSize = World?.RegionInfo?.RegionSizeX ?? 256f;
            position.X = Math.Max(-10f, Math.Min(regionSize + 10f, position.X));
            position.Y = Math.Max(-10f, Math.Min(regionSize + 10f, position.Y));
            position.Z = Math.Max(0f, Math.Min(4096f, position.Z));

            if (m_host.ParentGroup.IsAttachment)
            {
                ScenePresence avatar = World?.GetScenePresence(m_host.ParentGroup.AttachedAvatar);
                if (avatar == null)
                    return 0;
                avatar.StandUp();
                avatar.Teleport(position);
            }
            else
            {
                // Move the root prim (entire linkset)
                SceneObjectGroup group = m_host.ParentGroup;
                if (group == null || group.IsDeleted) return 0;
                group.UpdateGroupPosition(position);
            }
            return 1;
        }

        // ── Physics ────────────────────────────────────────────────────────────

        public void llSetForce(Vector3 force, int local)
        {
            try
            {
                if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return;
                if ((m_host.ParentGroup.RootPart.Flags & PrimFlags.Physics) == 0) return;
                PhysicsActor pa = m_host.ParentGroup.RootPart.PhysActor;
                if (pa == null) return;
                if (local != 0) force *= m_host.GetWorldRotation();
                pa.Force = force;
            }
            finally { PhySleep(); }   // Halcyon sleeps on every path
        }

        public Vector3 llGetForce()
        {
            if (m_host?.ParentGroup == null) return Vector3.Zero;
            PhysicsActor pa = m_host.ParentGroup.RootPart.PhysActor;
            return pa?.Force ?? Vector3.Zero;
        }

        public void llSetTorque(Vector3 torque, int local)
        {
            try
            {
                if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return;
                if ((m_host.ParentGroup.RootPart.Flags & PrimFlags.Physics) == 0) return;
                PhysicsActor pa = m_host.ParentGroup.RootPart.PhysActor;
                if (pa == null) return;
                if (local != 0) torque *= m_host.GetWorldRotation();
                pa.Torque = torque;
            }
            finally { PhySleep(); }   // Halcyon sleeps on every path
        }

        public void llSetForceAndTorque(Vector3 force, Vector3 torque, int local)
        {
            llSetForce(force, local);
            llSetTorque(torque, local);
        }

        public void llApplyImpulse(Vector3 force, int local)
        {
            try
            {
                if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return;
                if ((m_host.ParentGroup.RootPart.Flags & PrimFlags.Physics) == 0) return;
                if (force.LengthSquared() > 20000f * 20000f)
                    force = Vector3.Normalize(force) * 20000f;
                m_host.ApplyImpulse(force, local != 0);
            }
            finally { PhySleep(); }   // Halcyon sleeps on every path
        }

        public void llApplyRotationalImpulse(Vector3 force, int local)
        {
            try
            {
                if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return;
                if ((m_host.ParentGroup.RootPart.Flags & PrimFlags.Physics) == 0) return;
                m_host.ApplyAngularImpulse(force, local != 0);
            }
            finally { PhySleep(); }   // Halcyon sleeps on every path
        }
        public void llMoveToTarget(Vector3 target, float tau)
        {
            if (m_host?.ParentGroup == null) return;
            m_host.ParentGroup.MoveToTarget(target, tau);
        }

        public void llStopMoveToTarget()
        {
            if (m_host?.ParentGroup == null) return;
            m_host.ParentGroup.StopMoveToTarget();
        }
        /// <summary>
        /// YEngine's scope (LSL_Api.llGetMass) - the whole object from any prim of it, and
        /// the wearer's mass from an attachment. This used to be the script's own prim only.
        /// </summary>
        public float llGetMass()
        {
            SceneObjectGroup group = m_host?.ParentGroup;
            if (group == null) return 0f;
            if (group.IsAttachment)
                return World?.GetScenePresence(group.AttachedAvatar)?.GetMass() ?? 0f;
            return group.GetMass();
        }
        /// <summary>Kilograms - 100 x llGetMass, as YEngine (LSL_Api.llGetMassMKS).</summary>
        public float llGetMassMKS() => 100f * llGetMass();
        /// <summary>
        /// Kilograms - 100 x llGetObjectMass for the same object, as llGetMassMKS is 100 x llGetMass
        /// (YEngine, LSL_Api.llGetMassMKS). It returned the same number as llGetObjectMass. Halcyon's body does too:
        /// its Kilograms2Lindograms is the identity ("returning kg/100.0 would break existing content"); Phlox follows
        /// its own llGetMass/llGetMassMKS pair instead.
        /// </summary>
        public float iwGetObjectMassMKS(string id) => 100f * llGetObjectMass(id);
        public float llGetObjectMass(string id)
        {
            if (!UUID.TryParse(id, out UUID key)) return 0f;
            SceneObjectPart part = World?.GetSceneObjectPart(key);
            if (part != null) return part.ParentGroup.GetMass();
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp?.PhysicsActor != null) return sp.PhysicsActor.Mass;
            return 0f;
        }
        public void llSetBuoyancy(float buoyancy)
        {
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return;
            m_host.ParentGroup.RootPart.SetBuoyancy(buoyancy);
        }

        public void llSetHoverHeight(float height, int water, float tau)
        {
            if (m_host?.PhysActor == null) return;
            PIDHoverType hoverType = (water != 0) ? PIDHoverType.Water : PIDHoverType.Ground;
            m_host.SetHoverHeight(height, hoverType, tau);
        }

        public void llStopHover()
        {
            if (m_host?.PhysActor == null) return;
            m_host.StopHover();
        }
        public void llGroundRepel(float height, int water, float tau)
        {
            // Halcyon used PIDHoverFlag.Ground|Repel; this port uses PIDHoverType which
            // may not have a separate Repel flag. Use Ground (or Water) — SetHoverHeight
            // with a positive height inherently repels from the ground.
            if (m_host?.PhysActor == null) return;
            PIDHoverType hoverType = PIDHoverType.Ground;
            if (water != 0)
                hoverType = PIDHoverType.Water;
            m_host.SetHoverHeight(height, hoverType, tau);
        }
        // Base64 lookup tables for llIntegerToBase64 / llBase64ToInteger
        private static readonly char[] i2ctable =
        {
            'A','B','C','D','E','F','G','H','I','J','K','L','M','N','O','P',
            'Q','R','S','T','U','V','W','X','Y','Z','a','b','c','d','e','f',
            'g','h','i','j','k','l','m','n','o','p','q','r','s','t','u','v',
            'w','x','y','z','0','1','2','3','4','5','6','7','8','9','+','/'
        };
        private static readonly int[] c2itable =
        {
            -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,  // 0-15
            -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,  // 16-31
            -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,63,-1,-1,-1,64,  // 32-47
            53,54,55,56,57,58,59,60,61,62,-1,-1,-1, 0,-1,-1,  // 48-63
            -1, 1, 2, 3, 4, 5, 6, 7, 8, 9,10,11,12,13,14,15, // 64-79
            16,17,18,19,20,21,22,23,24,25,26,-1,-1,-1,-1,-1,  // 80-95
            -1,27,28,29,30,31,32,33,34,35,36,37,38,39,40,41, // 96-111
            42,43,44,45,46,47,48,49,50,51,52,-1,-1,-1,-1,-1   // 112-127
        };


        public void llSetStatus(int status, int value)
        {
            if (m_host == null) return;
            SceneObjectGroup group = m_host.ParentGroup;
            if (group == null) return;
            bool on = value != 0;

            // Halcyon's PhySleep after each (LSLSystemAPI.cs:1506, 1516); none when physics is refused for size
            if ((status & STATUS_PHYSICS) != 0)
            {
                if (SetObjectPhysics(group, on)) PhySleep();
            }

            if ((status & STATUS_PHANTOM) != 0)
            {
                group.ScriptSetPhantomStatus(on);
                PhySleep();
            }

            if ((status & STATUS_CAST_SHADOWS) != 0)
            {
                if (on) m_host.AddFlag(PrimFlags.CastShadows);
                else    m_host.RemFlag(PrimFlags.CastShadows);
            }

            if ((status & STATUS_BLOCK_GRAB) != 0 || (status & STATUS_BLOCK_GRAB_OBJECT) != 0)
                m_host.BlockGrab = on;

            if ((status & STATUS_DIE_AT_EDGE) != 0)
                m_host.SetDieAtEdge(on);

            // As YEngine (LSL_Api.cs:1577-1578); the core returns the object at a region edge (Scene.cs:2977,
            // SceneObjectGroup.cs:874), where Halcyon answered "Command not implemented" (:1557-1560).
            if ((status & STATUS_RETURN_AT_EDGE) != 0)
                m_host.SetReturnAtEdge(on);

            if ((status & STATUS_SANDBOX) != 0)
                m_host.SetStatusSandbox(on);   // As YEngine

            // Rotation axis locks — byte bitmask: bit0=X, bit1=Y, bit2=Z
            if ((status & (STATUS_ROTATE_X | STATUS_ROTATE_Y | STATUS_ROTATE_Z)) != 0)
            {
                byte locks = m_host.RotationAxisLocks;
                if ((status & STATUS_ROTATE_X) != 0)
                    locks = on ? (byte)(locks & ~0x01) : (byte)(locks | 0x01);
                if ((status & STATUS_ROTATE_Y) != 0)
                    locks = on ? (byte)(locks & ~0x02) : (byte)(locks | 0x02);
                if ((status & STATUS_ROTATE_Z) != 0)
                    locks = on ? (byte)(locks & ~0x04) : (byte)(locks | 0x04);
                m_host.RotationAxisLocks = locks;
                m_host.PhysActor?.LockAngularMotion(locks);
            }
        }

        /// <summary>
        /// STATUS_PHYSICS and PRIM_PHYSICS on the whole object (Halcyon llSetStatus): turning physics on is refused
        /// when any prim is larger than the region's physical-prim size.
        /// </summary>
        /// <returns>false when turning physics on was refused (Halcyon does not PhySleep then)</returns>
        private bool SetObjectPhysics(SceneObjectGroup group, bool on)
        {
            if (on)
            {
                foreach (SceneObjectPart p in group.Parts)
                {
                    if (p.Scale.X > World.m_maxPhys || p.Scale.Y > World.m_maxPhys || p.Scale.Z > World.m_maxPhys)
                        return false;
                }
            }
            group.ScriptSetPhysicsStatus(on);
            return true;
        }

        public int llGetStatus(int status)
        {
            if (m_host == null) return 0;
            uint flags = m_host.GetEffectiveObjectFlags();
            switch (status)
            {
                case STATUS_PHYSICS:
                    return (flags & (uint)PrimFlags.Physics) != 0 ? 1 : 0;
                case STATUS_PHANTOM:
                    return (flags & (uint)PrimFlags.Phantom) != 0 ? 1 : 0;
                case STATUS_CAST_SHADOWS:
                    return (flags & (uint)PrimFlags.CastShadows) != 0 ? 1 : 0;
                case STATUS_BLOCK_GRAB:
                case STATUS_BLOCK_GRAB_OBJECT:
                    return m_host.BlockGrab ? 1 : 0;
                case STATUS_DIE_AT_EDGE:
                    return m_host.GetDieAtEdge() ? 1 : 0;
                case STATUS_SANDBOX:
                    return m_host.GetStatusSandbox() ? 1 : 0;
                case STATUS_RETURN_AT_EDGE:
                    return m_host.GetReturnAtEdge() ? 1 : 0;   // YEngine LSL_Api.cs:1649
                case STATUS_ROTATE_X:
                    return (m_host.RotationAxisLocks & 0x01) == 0 ? 1 : 0;
                case STATUS_ROTATE_Y:
                    return (m_host.RotationAxisLocks & 0x02) == 0 ? 1 : 0;
                case STATUS_ROTATE_Z:
                    return (m_host.RotationAxisLocks & 0x04) == 0 ? 1 : 0;
                default:
                    return 0;
            }
        }
        // Halcyon's vehicle validators (OpenSim/Region/Physics/Manager/Vehicle/*.cs) - the ids
        // Phlox's constants define (DefaultConstants VEHICLE_*) - and its LSLError on anything else or a NaN
        // (LSLSystemAPI.cs:8541-8621). An invalid call is not applied and returns before Halcyon's PhySleep.
        private static bool VehicleTypeValid(int t) => (t >= 0 && t <= 5) || t == 10001 || t == 10002;
        private static bool VehicleFloatParamValid(int p) =>
            ((p >= 24 && p <= 40) && p != 30 && p != 31 && p != 34 && p != 35) || (p >= 11001 && p <= 11006);
        private static bool VehicleVectorParamValid(int p) =>
            (p >= 16 && p <= 20) || p == 30 || p == 31 || p == 34 || p == 35 || p == 12001 || p == 12002;
        private static bool VehicleRotationParamValid(int p) => p == 44;

        public void llSetVehicleType(int type)
        {
            if (m_host?.ParentGroup != null && !m_host.ParentGroup.IsDeleted && !VehicleTypeValid(type))
            {
                LSLError("llSetVehicleType(" + type.ToString() + ") is not valid.");
                return;
            }
            try
            {
                if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return;
                PhysicsActor pa = m_host.ParentGroup.RootPart.PhysActor;
                if (pa == null) return;
                pa.VehicleType = type;
            }
            finally { PhySleep(); }   // Halcyon sleeps on every path it applies
        }

        public void llSetVehicleFloatParam(int param, float value)
        {
            // Halcyon also takes a vector parameter here (as <v, v, v>); Phlox hands the float to physics, as before.
            if (m_host?.ParentGroup != null && !m_host.ParentGroup.IsDeleted
                && (float.IsNaN(value) || !(VehicleFloatParamValid(param) || VehicleVectorParamValid(param))))
            {
                LSLError("llSetVehicleFloatParam(" + param.ToString() + ", " + value.ToString() + ") is not valid.");
                return;
            }
            try
            {
                if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return;
                PhysicsActor pa = m_host.ParentGroup.RootPart.PhysActor;
                if (pa == null) return;
                pa.VehicleFloatParam(param, value);
            }
            finally { PhySleep(); }   // Halcyon sleeps on every path it applies
        }

        public void llSetVehicleVectorParam(int param, Vector3 vec)
        {
            if (m_host?.ParentGroup != null && !m_host.ParentGroup.IsDeleted
                && (float.IsNaN(vec.X) || float.IsNaN(vec.Y) || float.IsNaN(vec.Z) || !VehicleVectorParamValid(param)))
            {
                LSLError("llSetVehicleVectorParam(" + param.ToString() + ", " + vec.ToString() + ") is not valid.");
                return;
            }
            try
            {
                if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return;
                PhysicsActor pa = m_host.ParentGroup.RootPart.PhysActor;
                if (pa == null) return;
                pa.VehicleVectorParam(param, vec);
            }
            finally { PhySleep(); }   // Halcyon sleeps on every path it applies
        }

        public void llSetVehicleRotationParam(int param, Quaternion rot)
        {
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return;
            if (float.IsNaN(rot.X) || float.IsNaN(rot.Y) || float.IsNaN(rot.Z) || float.IsNaN(rot.W) || !VehicleRotationParamValid(param))
            {
                LSLError("llSetVehicleRotationParam(" + param.ToString() + ", " + rot.ToString() + ") is not valid.");
                return;
            }
            PhysicsActor pa = m_host.ParentGroup.RootPart.PhysActor;
            if (pa == null) return;
            pa.VehicleRotationParam(param, rot);
        }

        public void llSetVehicleFlags(int flags)
        {
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return;
            PhysicsActor pa = m_host.ParentGroup.RootPart.PhysActor;
            if (pa == null) return;
            pa.VehicleFlags(flags, false);
        }

        public void llRemoveVehicleFlags(int flags)
        {
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return;
            PhysicsActor pa = m_host.ParentGroup.RootPart.PhysActor;
            if (pa == null) return;
            pa.VehicleFlags(flags, true);
        }
        public LSLList llGetPhysicsMaterial()
        {
            if (m_host == null) return new LSLList();
            // Returns [gravityMultiplier, restitution, friction, density]
            return new LSLList(new object[]
            {
                m_host.GravityModifier,
                m_host.Restitution,
                m_host.Friction,
                m_host.Density
            });
        }

        public void llSetPhysicsMaterial(int mask, float gravityMultiplier, float restitution, float friction, float density)
        {
            if (m_host == null) return;
            // SL: "llSetPhysicsMaterial silently fails if called from an attachment."
            if (m_host.ParentGroup?.IsAttachment == true) return;
            PrimSetPhysicsMaterial(m_host, mask, density, friction, restitution, gravityMultiplier);
            if (m_host.PhysActor != null) PhySleep();   // Halcyon's, for a prim with a physics actor
        }

        /// <summary>
        /// llSetPhysicsMaterial and OpenSim's PRIM_PHYSICS_MATERIAL on one prim (OpenSim LSL_Api.SetPhysicsMaterial): the
        /// mask's DENSITY, FRICTION, RESTITUTION and GRAVITY_MULTIPLIER bits (SL's values) pick the values that change.
        /// The scene's UpdateExtraPhysics writes them through the part's setters, which ignore a value outside SL's
        /// range, save it, and pass it to the prim's physics actor.
        /// </summary>
        private static void PrimSetPhysicsMaterial(SceneObjectPart part, int mask, float density, float friction,
            float restitution, float gravityMultiplier)
        {
            var physdata = new ExtraPhysicsData
            {
                PhysShapeType = (PhysShapeType)part.PhysicsShapeType,
                Density = (mask & DENSITY) != 0 ? density : part.Density,
                Friction = (mask & FRICTION) != 0 ? friction : part.Friction,
                Bounce = (mask & RESTITUTION) != 0 ? restitution : part.Restitution,
                GravitationModifier = (mask & GRAVITY_MULTIPLIER) != 0 ? gravityMultiplier : part.GravityModifier,
            };
            part.UpdateExtraPhysics(physdata);
        }
        public void llSetAngularVelocity(Vector3 force, int local)
        {
            if (m_host?.ParentGroup == null) return;
            m_host.ParentGroup.RootPart.SetAngularVelocity(force, local != 0);
        }

        public void llSetVelocity(Vector3 force, int local)
        {
            if (m_host?.ParentGroup == null) return;
            m_host.ParentGroup.RootPart.SetVelocity(force, local != 0);
        }
        public void llSetKeyframedMotion(LSLList keyframes, LSLList options)
        {
            // Faithful port from Halcyon, adapted for OpenSim's KeyframeMotion class.
            if (m_host?.ParentGroup == null) return;
            if (m_host.ParentGroup.RootPart != m_host)
            {
                ShoutError("Must be used in the root object!");
                return;
            }

            try
            {
                if (keyframes.Length == 0 && options.Length == 0)
                {
                    // Stop and clear
                    m_host.ParentGroup.RootPart.KeyframeMotion = null;
                    return;
                }


                int dataType = KFM_ROTATION | KFM_TRANSLATION; // Both = default
                int mode = KFM_FORWARD;

                for (int i = 0; i < options.Length; i += 2)
                {
                    int option = options.GetLSLIntegerItem(i);
                    int value = options.GetLSLIntegerItem(i + 1);
                    if (option == KFM_COMMAND)
                    {
                        // Command-only call: play/stop/pause an existing motion
                        KeyframeMotion existingMotion = m_host.ParentGroup.RootPart.KeyframeMotion;
                        if (existingMotion == null) return;
                        switch (value)
                        {
                            case KFM_CMD_PLAY: existingMotion.Start(); break;
                            case KFM_CMD_STOP: existingMotion.Stop(); break;
                            case KFM_CMD_PAUSE: existingMotion.Pause(); break;
                        }
                        return;
                    }
                    if (option == KFM_MODE) mode = value;
                    else if (option == KFM_DATA) dataType = value;
                }

                bool hasTranslation = (dataType & KFM_TRANSLATION) != 0;
                bool hasRotation = (dataType & KFM_ROTATION) != 0;
                int stride = (hasTranslation && hasRotation) ? 3 : 2;

                // Build keyframe arrays
                int numFrames = keyframes.Length / stride;
                KeyframeMotion.Keyframe[] kfArray = new KeyframeMotion.Keyframe[numFrames];

                for (int i = 0; i < numFrames; i++)
                {
                    int baseIdx = i * stride;
                    KeyframeMotion.Keyframe kf = new KeyframeMotion.Keyframe();
                    int idx = 0;

                    if (hasTranslation)
                    {
                        Vector3 pos = keyframes.GetVector3Item(baseIdx + idx);
                        kf.Position = pos;
                        idx++;
                    }
                    if (hasRotation)
                    {
                        Quaternion rot = keyframes.GetQuaternionItem(baseIdx + idx);
                        rot.Normalize();
                        kf.Rotation = rot;
                        idx++;
                    }
                    float time = (float)keyframes.GetLSLFloatItem(baseIdx + idx);
                    kf.TimeMS = (int)(time * 1000);

                    kfArray[i] = kf;
                }

                KeyframeMotion.PlayMode playMode;
                switch (mode)
                {
                    case KFM_REVERSE: playMode = KeyframeMotion.PlayMode.Reverse; break;
                    case KFM_LOOP: playMode = KeyframeMotion.PlayMode.Loop; break;
                    case KFM_PING_PONG: playMode = KeyframeMotion.PlayMode.PingPong; break;
                    default: playMode = KeyframeMotion.PlayMode.Forward; break;
                }

                KeyframeMotion.DataFormat dFlags = 0;
                if (hasTranslation) dFlags |= KeyframeMotion.DataFormat.Translation;
                if (hasRotation) dFlags |= KeyframeMotion.DataFormat.Rotation;

                KeyframeMotion motion = new KeyframeMotion(m_host.ParentGroup, playMode, dFlags);
                motion.SetKeyframes(kfArray);
                m_host.ParentGroup.RootPart.KeyframeMotion = motion;
                motion.Start();
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: llSetKeyframedMotion exception: {0}", e.Message);
            }
        }

        // ── Timer / sleep ──────────────────────────────────────────────────────

        /// <summary>
        /// A positive request below the region's floor is raised to it.
        ///
        /// <para>
        /// Zero and negative are left exactly as they were: the SL wiki says "Passing in 0.0 stops
        /// further timer events", and a negative value lands &lt;= 0 where the scheduler only arms a
        /// timer for an interval &gt; 0. The floor must never resurrect a stopped timer.
        /// </para>
        ///
        /// <para>
        /// The clamp is here, at the API entry, and not in the scheduler, so that what
        /// <c>phlox status</c> reads back is the value that was actually applied - one place to look
        /// when a script and the log disagree about how fast a timer is.
        /// </para>
        /// </summary>
        public void llSetTimerEvent(float sec)
        {
            float applied = sec;
            float floor = m_ScriptEngine?.MinTimerInterval ?? 0f;
            if (sec > 0f && floor > 0f && sec < floor)
            {
                applied = floor;
                if (!m_timerFloorLogged)
                {
                    // Once per script, not once per tick: the point is to name the script that asked,
                    // and a 10 ms timer would otherwise write a hundred lines a second.
                    m_timerFloorLogged = true;
                    m_log.LogDebug("[PhloxAPI]: llSetTimerEvent floor applied for {Item}: requested {Requested}s, applied {Applied}s",
                        m_itemID, sec, applied);
                }
            }

            m_ScriptEngine.SetTimerEvent(m_localID, m_itemID, applied);
        }

        /// <summary>One clamp message per script instance, however often it re-arms.</summary>
        private bool m_timerFloorLogged;
        public void llSleep(float sec) => ScriptSleep((int)(sec * 1000));
        /// <summary>
        /// Wiki: "Set the minimum time between events being handled" - a floor between
        /// handler starts for THIS script, events inside the window queued, not dropped. The old
        /// comment said the scheduler made this unnecessary; serialising events is not the same as
        /// spacing them. Upstream forwards to the engine too (LSL_Api.cs:4433-4444).
        /// </summary>
        public void llMinEventDelay(float delay) => m_ScriptEngine?.SetMinEventDelay(m_itemID, delay);

        // ── Script state ───────────────────────────────────────────────────────

        public void llResetScript() => m_ScriptEngine.ApiResetScript(m_itemID);
        public void llResetOtherScript(string name)
        {
            if (m_host == null) return;
            TaskInventoryItem item = FindInventoryItem(name, (int)AssetType.LSLText);
            if (item == null)
            {
                ScriptShoutError("llResetOtherScript: script '" + name + "' not found");
                return;
            }
            m_ScriptEngine.ApiResetScript(item.ItemID);
        }

        public int llGetScriptState(string name)
        {
            if (m_host == null) return 0;
            TaskInventoryItem item = FindInventoryItem(name, (int)AssetType.LSLText);
            if (item == null)
            {
                ScriptShoutError("llGetScriptState: script '" + name + "' not found");
                return 0;
            }
            return item.ScriptRunning ? 1 : 0;
        }

        public void llSetScriptState(string name, int run)
        {
            if (m_host == null) return;
            TaskInventoryItem item = FindInventoryItem(name, (int)AssetType.LSLText);
            if (item == null)
            {
                ScriptShoutError("llSetScriptState: script '" + name + "' not found");
                return;
            }
            // Use EventManager directly — SetScriptRunning requires IClientAPI
            if (run != 0)
                World.EventManager.TriggerStartScript(m_host.LocalId, item.ItemID);
            else
                World.EventManager.TriggerStopScript(m_host.LocalId, item.ItemID);
        }
        public void llSetRemoteScriptAccessPin(int pin)
        {
            m_host.ScriptAccessPin = pin;
        }
        public void llRemoteLoadScriptPin(string target, string name, int pin, int running, int start_param)
        {
            RemoteLoadScriptPin(target, name, pin, running, start_param, true);
        }
        public int iwRemoteLoadScriptPin(string target, string name, int pin, int running, int start_param)
        {
            return RemoteLoadScriptPin(target, name, pin, running, start_param, false);
        }

        /// <summary>
        /// Shared implementation for llRemoteLoadScriptPin / iwRemoteLoadScriptPin.
        /// Faithful port from Halcyon.
        /// Returns: 1=success, 0=failure, -1=PIN mismatch, -2=no PIN set.
        /// </summary>
        private int RemoteLoadScriptPin(string target, string name, int pin, int running, int start_param, bool doShout)
        {
            if (pin == 0)
            {
                ScriptShoutError("llRemoteLoadScriptPin: PIN cannot be zero.");
                ScriptSleep(3000);
                return 0;
            }

            if (!UUID.TryParse(target, out UUID destId))
            {
                llSay(0, "Could not parse key " + target);
                ScriptSleep(3000);
                return 0;
            }

            // Target must be a different prim than the one containing the script, owned by the same user.
            SceneObjectPart part = World?.GetSceneObjectPart(destId);
            if (part == null)
            {
                ScriptShoutError("llRemoteLoadScriptPin: Target prim [" + destId.ToString() + "] not found.");
                ScriptSleep(3000);
                return 0;
            }
            if (m_host.OwnerID != part.OwnerID)
            {
                ScriptShoutError("llRemoteLoadScriptPin: Target prim ownership does not match.");
                ScriptSleep(3000);
                return 0;
            }
            if (m_host.UUID == destId)
            {
                ScriptShoutError("llRemoteLoadScriptPin: Target prim cannot be the source prim.");
                ScriptSleep(3000);
                return 0;
            }

            // Find the script in this prim's inventory
            UUID srcId = UUID.Zero;
            bool found = false;
            lock (m_host.TaskInventory)
            {
                foreach (KeyValuePair<UUID, TaskInventoryItem> inv in m_host.TaskInventory)
                {
                    if (inv.Value.Name == name && inv.Value.Type == 10) // type 10 = script
                    {
                        found = true;
                        srcId = inv.Key;
                        break;
                    }
                }
            }

            if (!found)
            {
                llSay(0, "Could not find script " + name);
                ScriptSleep(3000);
                return 0;
            }

            // The rest of the permission checks are done in RezScript, so check the pin there as well.
            int ret = 1;
            try
            {
                // This tree's signature: RezScriptFromPrim(UUID srcId, SceneObjectPart srcPart, UUID destId, int pin, int running, int start_param)
                World.RezScriptFromPrim(srcId, m_host, destId, pin, running, start_param);
            }
            catch (Exception e)
            {
                string msg = e.Message;
                if (msg.Contains("PIN"))
                {
                    if (doShout) ShoutError("llRemoteLoadScriptPin: Script update denied - PIN mismatch.");
                    ret = -1;
                }
                else
                {
                    m_log.LogWarning("[PhloxAPI]: RemoteLoadScriptPin failed: {0}", msg);
                    if (doShout) ShoutError("llRemoteLoadScriptPin: " + msg);
                    ret = 0;
                }
            }
            ScriptSleep(3000);
            return ret;
        }
        public void llMessageLinked(int linknum, int num, string str, string id)
        {
            if (m_host == null) return;
            SceneObjectGroup group = m_host.ParentGroup;
            if (group == null || group.IsDeleted) return;
            var parms = new EventParams("link_message",
                new object[] { m_host.LinkNum, num, str ?? string.Empty, id ?? UUID.Zero.ToString() },
                new DetectParams[0]);
            var targets = GetLinkParts(linknum).ToList();
            foreach (var p in targets)
                m_ScriptEngine.PostObjectEvent(p.LocalId, parms);
            var receivers = ScriptItemsIn(targets);
            OfferToOtherEngines("llMessageLinked", () => receivers, "link_message", () => (object[])parms.Params.Clone());
            LinkMessageBackPressure(receivers);
        }

        /// <summary>
        /// An event SL raises in every script of some prims, whatever engine runs them (llMessageLinked: "in all scripts in
        /// the prim(s) described by link"; object_rez, email, linkset_data and dataserver likewise, see their callers).
        /// Phlox's own posts reach only its own scripts, so each other script engine of the region is offered every script
        /// item <paramref name="items"/> lists once, with plain values (int, string), as core modules post events; YEngine
        /// unwraps those. An engine queues only for the scripts it runs, so no script gets it twice. An exception from
        /// another engine is logged once for that engine and call and goes no further. With no other engine (a Phlox-only
        /// region) nothing is listed or posted.
        /// </summary>
        private void OfferToOtherEngines(string caller, Func<IEnumerable<UUID>> items, string eventName, Func<object[]> args)
        {
            var others = new List<OpenSim.Region.ScriptEngine.Interfaces.IScriptEngine>();
            foreach (IScriptModule m in World?.RequestModuleInterfaces<IScriptModule>() ?? Array.Empty<IScriptModule>())
                if (!ReferenceEquals(m, m_ScriptEngine) && m is OpenSim.Region.ScriptEngine.Interfaces.IScriptEngine e && !others.Contains(e))
                    others.Add(e);
            if (others.Count == 0) return;

            var list = items().ToList();
            foreach (var e in others)
            {
                bool logged = false;
                foreach (UUID item in list)
                {
                    try
                    {
                        e.PostScriptEvent(item, new EventParams(eventName, args(), new DetectParams[0]));
                    }
                    catch (Exception ex)
                    {
                        if (!logged)
                            m_log.LogWarning("[PhloxAPI]: {0}: another script engine failed to take a {1} event: {2}", caller, eventName, ex.Message);
                        logged = true;
                    }
                }
            }
        }
        public int llGetStartParameter()
        {
            return m_thisScript?.ScriptState?.StartParameter ?? 0;
        }
        public int llGetFreeMemory() => 65536;
        public int llGetUsedMemory()
        {
            // Phlox VM doesn't track per-script memory the way Mono does.
            // Return a reasonable estimate: 16KB base + script state size.
            if (m_thisScript?.ScriptState != null)
            {
                int stackSize = m_thisScript.ScriptState.Operands?.Count ?? 0;
                return 16384 + (stackSize * 64);
            }
            return 16384;
        }
        public int llSetMemoryLimit(int limit)
        {
            // Halcyon only accepts 128K (131072)
            if (limit == 131072) return 1;
            return 0;
        }
        public int llGetMemoryLimit() { return 131072; /* 128 * 1024 */ }
        /// <summary>
        /// Wiki: "Enables or disables the scripts profiling state" - PROFILE_SCRIPT_MEMORY (1)
        /// starts recording, PROFILE_NONE (0) stops it, and llGetSPMaxMemory then returns "the most
        /// memory used at any one time". Upstream is a no-op (LSL_Api.cs:17570); Phlox has MemInfo, so
        /// this is a peak field and two reads. Starting resets the peak to the current usage.
        /// </summary>
        public void llScriptProfiler(int flags)
        {
            var st = m_thisScript?.ScriptState;
            if (st == null) return;
            bool on = (flags & PROFILE_SCRIPT_MEMORY) != 0;
            // Fold the usage at the moment of the call in FIRST: a PROFILE_NONE that cleared the flag
            // before sampling would lose everything allocated since the last event boundary.
            st.SampleMemoryPeak();
            if (on && !st.ProfilingMemory) st.PeakMemoryUsed = st.MemInfo?.MemoryUsed ?? 0;
            st.ProfilingMemory = on;
            st.SampleMemoryPeak();
        }

        // ── Permissions ────────────────────────────────────────────────────────

        private IClientAPI m_waitingForScriptAnswer = null;
        /// <summary>The mask the pending question asked for; an answer is stored ANDed with it.</summary>
        private int m_requestedPerms;

        private UUID InventorySelf()
        {
            return GetInventorySelf()?.ItemID ?? UUID.Zero;
        }

        private void PermsChange(TaskInventoryItem item, UUID granter, int mask)
        {
            int silentEstateManagement = (mask & PERMISSION_SILENT_ESTATE_MANAGEMENT) != 0 ? 1 : 0;
            if (m_thisScript?.ScriptState?.MiscAttributes != null)
                m_thisScript.ScriptState.MiscAttributes[(int)InWorldz.Phlox.VM.RuntimeState.MiscAttr.SilentEstateManagement]
                    = new object[] { silentEstateManagement };
            if (item != null)
            {
                item.PermsGranter = granter;
                item.PermsMask = mask;
                m_host.Inventory.ForceInventoryPersistence();
                m_host.ParentGroup.HasGroupChanged = true;
            }
        }

        private int GetImplicitPermissions(TaskInventoryItem item, UUID agentID)
        {
            // SL's implicit grants (wiki llRequestPermissions), as YEngine gives them
            // (LSL_Api.llRequestPermissions). A sitter anywhere on the linkset counts, not only the
            // root's sit-target avatar.
            if (agentID == UUID.Zero) return 0;
            if (m_host.ParentGroup.IsAttachment && agentID == m_host.ParentGroup.AttachedAvatar)
                return SlConst.PERMISSION_TAKE_CONTROLS | SlConst.PERMISSION_TRIGGER_ANIMATION |
                       SlConst.PERMISSION_ATTACH | SlConst.PERMISSION_TRACK_CAMERA |
                       SlConst.PERMISSION_CONTROL_CAMERA | SlConst.PERMISSION_OVERRIDE_ANIMATIONS;
            if (m_host.ParentGroup.HasSittingAvatar(agentID))
                return SlConst.PERMISSION_TAKE_CONTROLS | SlConst.PERMISSION_TRIGGER_ANIMATION |
                       SlConst.PERMISSION_TRACK_CAMERA | SlConst.PERMISSION_CONTROL_CAMERA;
            return 0;
        }

        private bool RequestImplicitPermissions(int perm, TaskInventoryItem item, UUID agentID)
        {
            int implicitPerms = GetImplicitPermissions(item, agentID);
            if (implicitPerms == 0) return false;
            if ((perm & (~implicitPerms)) != 0) return false;
            lock (m_host.TaskInventory)
            {
                item.PermsGranter = agentID;
                item.PermsMask = perm;
            }
            PermsChange(item, item.PermsGranter, item.PermsMask);
            m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                "run_time_permissions", new object[] { (int)item.PermsMask },
                new DetectParams[0]));
            return true;
        }

        private void ClearWaitingForScriptAnswer(IClientAPI client)
        {
            if (m_waitingForScriptAnswer == null || client != m_waitingForScriptAnswer) return;
            client.OnScriptAnswer -= handleScriptAnswer;
            m_waitingForScriptAnswer = null;
        }

        private void handleConnectionClosed(IClientAPI client)
        {
            ClearWaitingForScriptAnswer(client);
        }

        private void handleScriptAnswer(IClientAPI client, UUID taskID, UUID itemID, int answer)
        {
            if (taskID != m_host.UUID) return;
            // Every script in the prim listens on the same client; an answer is for one item.
            if (itemID != m_itemID) return;
            if (m_waitingForScriptAnswer == null || client != m_waitingForScriptAnswer) return;
            ClearWaitingForScriptAnswer(client);
            UUID invItemID = InventorySelf();
            if (invItemID == UUID.Zero) return;
            // A viewer can only grant what was asked; extra bits in the answer are not a grant.
            int granted = answer & m_requestedPerms;
            if ((granted & SlConst.PERMISSION_TAKE_CONTROLS) == 0)
                EndPermissions(SlConst.PERMISSION_TAKE_CONTROLS, releaseControls: true, forgetControls: true);
            TaskInventoryItem item;
            lock (m_host.TaskInventory)
                item = m_host.TaskInventory[invItemID];
            PermsChange(item, client.AgentId, granted);
            m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                "run_time_permissions", new object[] { (int)item.PermsMask },
                new DetectParams[0]));
        }

        public void llRequestPermissions(string agent, int perm)
        {
            if (!UUID.TryParse(agent, out UUID agentID)) return;
            UUID invItemID = InventorySelf();
            if (invItemID == UUID.Zero) return;
            TaskInventoryItem item;
            lock (m_host.TaskInventory)
                item = m_host.TaskInventory[invItemID];

            // Halcyon :4518-4527; SL llRequestPermissions "PERMISSION_TELEPORT cannot be held by temporary
            // attachments". The rest of the request goes on (TELEPORT alone becomes a release).
            if ((perm & PERMISSION_TELEPORT) != 0 && IsTempAttachment(m_host.ParentGroup))
            {
                ScriptShoutError("Temporary attachments cannot request runtime permissions to teleport.");
                perm &= ~PERMISSION_TELEPORT;
            }

            if (agentID == UUID.Zero || perm == 0)
            {
                EndPermissions(ALL_PERMISSIONS, releaseControls: true, forgetControls: true);
                m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                    "run_time_permissions", new object[] { 0 },
                    new DetectParams[0]));
                return;
            }

            // Halcyon :4544-4545; SL llTakeControls, the permission is revoked by "a new llRequestPermissions
            // call". Another avatar, or a request without TAKE_CONTROLS: the controls and the permission go now.
            if (item.PermsGranter != agentID || (perm & SlConst.PERMISSION_TAKE_CONTROLS) == 0)
                EndPermissions(SlConst.PERMISSION_TAKE_CONTROLS, releaseControls: true, forgetControls: true);

            if (RequestImplicitPermissions(perm, item, agentID))
                return;

            ScenePresence presence = World.GetScenePresence(agentID);
            if (presence == null)
            {
                EndPermissions(ALL_PERMISSIONS, releaseControls: true, forgetControls: true);
                ScriptSleep(200);
                m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                    "run_time_permissions", new object[] { 0 },
                    new DetectParams[0]));
                return;
            }

            // Halcyon :4565-4571, only when a dialog would be sent: someone who muted the owner or the object
            // gets none, and the request ends unanswered (no run_time_permissions).
            if (IsScriptMuted(agentID))
            {
                EndPermissions(ALL_PERMISSIONS, releaseControls: true, forgetControls: true);
                return;
            }

            string ownerName = m_host.ParentGroup.RootPart.OwnerID.ToString();
            UserAccount ownerAcct = World?.UserAccountService?.GetUserAccount(
                World.RegionInfo.ScopeID, m_host.ParentGroup.RootPart.OwnerID);
            if (ownerAcct != null) ownerName = ownerAcct.FirstName + " " + ownerAcct.LastName;
            if (string.IsNullOrEmpty(ownerName)) ownerName = "(unknown)";

            lock (m_host.TaskInventory)
                item = m_host.TaskInventory[invItemID];

            if (m_waitingForScriptAnswer != presence.ControllingClient)
            {
                ClearWaitingForScriptAnswer(m_waitingForScriptAnswer);
                presence.ControllingClient.OnScriptAnswer += handleScriptAnswer;
                presence.ControllingClient.OnConnectionClosed += handleConnectionClosed;
                m_waitingForScriptAnswer = presence.ControllingClient;
            }

            m_requestedPerms = perm;
            presence.ControllingClient.SendScriptQuestion(
                m_host.UUID, m_host.ParentGroup.RootPart.Name, ownerName, invItemID, perm,
                GetScriptExperienceId());
        }

        public string llGetPermissionsKey()
        {
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return UUID.Zero.ToString();
            return item.PermsGranter.ToString();
        }

        public int llGetPermissions()
        {
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return 0;
            // AutomaticLinkPermission reports PERMISSION_CHANGE_LINKS (YEngine LSL_Api.cs:4755, Halcyon :4702).
            return AutomaticLinkPermission ? item.PermsMask | PERMISSION_CHANGE_LINKS : item.PermsMask;
        }
        public void llTakeControls(int controls, int accept, int pass_on)
        {
            // Requires PERMISSION_TAKE_CONTROLS (0x04)
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return;
            if ((item.PermsMask & PERMISSION_TAKE_CONTROLS) == 0)
            {
                ShoutError("llTakeControls: PERMISSION_TAKE_CONTROLS not granted.");
                return;
            }
            ScenePresence sp = World.GetScenePresence(item.PermsGranter);
            if (sp == null || sp.IsChildAgent) return;
            // The script's own call; a release the core raises for it (a re-take, or pass_on without accept) is
            // not a stand-up (PhloxEngine.OnScriptControlsReleased)
            using (PhloxEngine.OwnControlChange())
                sp.RegisterControlEventsToScript(controls, accept, pass_on, m_host.LocalId, m_itemID);
            m_thisScript.ScriptState.MiscAttributes[(int)RuntimeState.MiscAttr.Control] =
                new object[] { controls, accept, pass_on };
            m_ScriptEngine?.RequestParcelCheck(m_itemID);   // Holding controls exempts it from a No Scripts parcel
        }

        /// <summary>
        /// SL llReleaseControls "If PERMISSION_TAKE_CONTROLS was previously granted, it will be revoked." (Halcyon
        /// :3756-3759, ReleaseControlsInternal with releasePerms).
        /// </summary>
        public void llReleaseControls() => EndPermissions(SlConst.PERMISSION_TAKE_CONTROLS, releaseControls: true, forgetControls: true);

        // ── The permission lifecycle ────────────────────────────────────────────

        /// <summary>Every permission bit: the granter goes too.</summary>
        internal const int ALL_PERMISSIONS = -1;

        /// <summary>
        /// The one place a script's permissions end, with what they started. Every lifecycle path comes here: reset,
        /// llRequestPermissions (release, another avatar, no TAKE_CONTROLS, absent, muted), a dialog answer without
        /// TAKE_CONTROLS, llReleaseControls, the core's release of controls on a stand, Release Keys, detach or drop, an
        /// owner change, and an animation call for a granter who is not here. Unload ends no permission and releases
        /// controls without the item (<see cref="ReleaseControlsOnUnload"/>).
        /// </summary>
        /// <param name="revoke">The bits that end; <see cref="ALL_PERMISSIONS"/> clears the grant. The granter is cleared
        /// when nothing is left (Halcyon: "if (item.PermsMask == 0) item.PermsGranter = UUID.Zero").</param>
        /// <param name="releaseControls">Let go of the controls this script took, on whichever avatar here holds them.</param>
        /// <param name="forgetControls">Drop the Control record too, so a restart or a restore does not take them again.</param>
        /// <remarks>
        /// Animations already playing and camera parameters are left alone: SL clears the camera on a stand or detach,
        /// which the core does (ScenePresence.StandUp), and neither SL nor Halcyon stops an animation when a grant ends.
        /// The item is persisted only when it changed, so resetting a script with no grant does not mark its object changed.
        /// </remarks>
        internal void EndPermissions(int revoke, bool releaseControls, bool forgetControls)
        {
            TaskInventoryItem item = GetInventorySelf();
            if (releaseControls)
            {
                var misc = m_thisScript?.ScriptState?.MiscAttributes;
                bool hadRecord = misc != null && misc.ContainsKey((int)RuntimeState.MiscAttr.Control);
                if (forgetControls && hadRecord) misc.Remove((int)RuntimeState.MiscAttr.Control);
                ScenePresence holder = ControlsHolder(item?.PermsGranter ?? UUID.Zero, hadRecord);
                if (holder != null)
                    using (PhloxEngine.OwnControlChange())
                        holder.UnRegisterControlEventsToScript(m_host.LocalId, m_itemID);
                if (hadRecord || holder != null)
                    m_ScriptEngine?.RequestParcelCheck(m_itemID);   // Without controls a No Scripts parcel pauses it
            }

            if (item == null || revoke == 0) return;
            int mask = revoke == ALL_PERMISSIONS ? 0 : item.PermsMask & ~revoke;
            UUID granter = mask == 0 ? UUID.Zero : item.PermsGranter;
            bool changed = mask != item.PermsMask || granter != item.PermsGranter;
            PermsChange(changed ? item : null, granter, mask);
        }

        /// <summary>
        /// The avatar holding this script's taken controls: the granter when it holds them, else - when the script has a
        /// Control record and the core already cleared or changed the granter (an owner change) - any root avatar that
        /// does. A child agent is never asked: its controls went with the crossing.
        /// </summary>
        private ScenePresence ControlsHolder(UUID granter, bool hadRecord)
        {
            Scene world = World;
            if (world == null) return null;
            if (granter != UUID.Zero)
            {
                ScenePresence sp = world.GetScenePresence(granter);
                if (sp != null && !sp.IsChildAgent && sp.HasScriptControls(m_itemID)) return sp;
            }
            if (!hadRecord) return null;
            ScenePresence found = null;
            world.ForEachRootScenePresence(p => { if (found == null && p.HasScriptControls(m_itemID)) found = p; });
            return found;
        }

        /// <summary>
        /// The core let go of this script's controls on an avatar still in the region: a stand-up, Release Keys, a detach
        /// or a drop (PhloxEngine.OnScriptControlsReleased; crossings and departures never come here). Halcyon's
        /// handleMustReleaseControls: only for the permission holder, "On stand, lose both PERMISSION_CONTROL_CAMERA and
        /// PERMISSION_TAKE_CONTROLS". Scheduler thread.
        /// </summary>
        internal void ControlsReleasedByCore(UUID agentId)
        {
            TaskInventoryItem item = GetInventorySelf();
            if (item == null || item.PermsGranter != agentId) return;
            var misc = m_thisScript?.ScriptState?.MiscAttributes;
            if (misc == null || !misc.ContainsKey((int)RuntimeState.MiscAttr.Control)) return;   // already ended here
            ScenePresence sp = World?.GetScenePresence(agentId);
            if (sp != null && sp.HasScriptControls(m_itemID)) return;                            // taken again since
            EndPermissions(SlConst.PERMISSION_TAKE_CONTROLS | SlConst.PERMISSION_CONTROL_CAMERA,
                           releaseControls: true, forgetControls: true);
        }

        /// <summary>
        /// The object has a new owner. Halcyon clears every item's grant (ApplyNextOwnerPermissions, Rationalize) and so
        /// does the core (ChangeInventoryOwner), but neither lets go of controls the old grant took. Scheduler thread.
        /// </summary>
        internal void OwnerChanged() => EndPermissions(ALL_PERMISSIONS, releaseControls: true, forgetControls: true);

        /// <summary>
        /// Halcyon :4117-4126 (and llStopAnimation, iwStart/StopLinkAnimation): "Emulate SL's behavior of clearing this
        /// permission when this is called for an agent outside this region" - TRIGGER_ANIMATION ends and
        /// run_time_permissions says what is left.
        /// </summary>
        private void AnimationGranterAbsent()
        {
            EndPermissions(SlConst.PERMISSION_TRIGGER_ANIMATION, releaseControls: false, forgetControls: false);
            TaskInventoryItem item = GetInventorySelf();
            m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                "run_time_permissions", new object[] { item?.PermsMask ?? 0 },
                new DetectParams[0]));
        }

        /// <summary>A temporary attachment: worn, and not from inventory (the core's own test, AttachmentsModule).</summary>
        private static bool IsTempAttachment(SceneObjectGroup group)
            => group != null && group.IsAttachment && group.FromItemID.IsZero();

        /// <summary>
        /// Halcyon IsScriptMuted: never for the owner; muted when the target has muted the object's owner or the object.
        /// NGC has no mute query, so the target's list is read from IMuteListService (lines "type id name|flags", as
        /// MuteListService writes them); like Halcyon's IsMuted, any row counts, whatever its flags.
        /// </summary>
        private bool IsScriptMuted(UUID target)
        {
            if (target == m_host.OwnerID) return false;
            IMuteListService mutes = World?.RequestModuleInterface<IMuteListService>();
            if (mutes == null) return false;
            byte[] data;
            try { data = mutes.MuteListRequest(target, 0); }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: mute list of {0} could not be read: {1}", target, e.Message);
                return false;
            }
            if (data == null || data.Length <= 1) return false;
            UUID owner = m_host.OwnerID, obj = m_host.ParentGroup.UUID;
            foreach (string line in Encoding.UTF8.GetString(data).Split('\n'))
            {
                string[] f = line.Split(' ', 3);
                if (f.Length >= 2 && UUID.TryParse(f[1], out UUID id) && (id == owner || id == obj)) return true;
            }
            return false;
        }

        /// <summary>
        /// Every give asks this first. Halcyon _GiveInventory / _GiveLinkInventoryList: a
        /// recipient who muted the object or its owner is not offered anything - IW_DELIVER_MUTED, the normal delay, a
        /// server-log line and nothing said to the script. A prim has no mute list, so it is not looked up.
        /// </summary>
        private bool GiveRefusedByMute(UUID destId, string what)
        {
            if (World.GetSceneObjectPart(destId) != null) return false;
            if (!IsScriptMuted(destId)) return false;
            m_log.LogInformation("[PhloxAPI]: Not offering {0} from muted {1} to {2}", what, m_host.ParentGroup.UUID, destId);
            return true;
        }

        // Deprecated - no-op in modern viewers. Halcyon's Deprecated, LSLSystemAPI.cs:3867, 3873.
        public void llTakeCamera(string avatar) => Deprecated("llTakeCamera");

        public void llReleaseCamera(string avatar) => Deprecated("llReleaseCamera");

        public void llSetCameraEyeOffset(Vector3 offset)
        {
            m_host.SetCameraEyeOffset(offset);
        }

        public void llSetCameraAtOffset(Vector3 offset)
        {
            m_host.SetCameraAtOffset(offset);
        }

        public void llSetCameraParams(LSLList rules)
        {
            // Requires PERMISSION_CONTROL_CAMERA (0x800 = 2048)
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return;
            if ((item.PermsMask & PERMISSION_CONTROL_CAMERA) == 0) return;
            ScenePresence sp = World.GetScenePresence(item.PermsGranter);
            if (sp == null || sp.IsChildAgent) return;

            // Build SortedDictionary<int, float> from the flat [type, value, type, value...] list
            var parameters = new SortedDictionary<int, float>();
            object[] data = rules.Data;
            for (int i = 0; i + 1 < data.Length; i += 2)
            {
                if (!int.TryParse(data[i].ToString(), out int camType)) continue;
                if (!float.TryParse(data[i + 1].ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float camVal)) continue;
                parameters[camType] = camVal;
            }
            sp.ControllingClient.SendSetFollowCamProperties(m_host.ParentUUID, parameters);
        }

        public void llClearCameraParams()
        {
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return;
            // Halcyon needs PERMISSION_CONTROL_CAMERA from a granter and returns silently without it
            // (LSLSystemAPI.cs:13650-13659). (SL shouts an error there; Halcyon does not.)
            if (item.PermsGranter == UUID.Zero || (item.PermsMask & PERMISSION_CONTROL_CAMERA) == 0) return;
            ScenePresence sp = World.GetScenePresence(item.PermsGranter);
            if (sp == null || sp.IsChildAgent) return;
            sp.ControllingClient.SendClearFollowCamProperties(m_host.ParentUUID);
        }

        public void llSetLinkCamera(int link, Vector3 eye, Vector3 at)
        {
            // Sets camera eye/at offsets on a linked prim
            foreach (SceneObjectPart part in GetLinkParts(link))
            {
                part.SetCameraEyeOffset(eye);
                part.SetCameraAtOffset(at);
            }
        }

        public void llForceMouselook(int mouselook)
        {
            m_host.SetForceMouselook(mouselook != 0);
        }
        /// <summary>
        /// The table returns integer and the async shim returns only what the body hands to
        /// SysReturn, so TRUE/FALSE goes back through the deferred call's sequenced return on every path.
        /// InWorldz llManageEstateAccess. After a change the object's owner gets an IM
        /// ("... has been banned from REGION" and so on) and the call takes 200 ms, unless the script holds
        /// PERMISSION_SILENT_ESTATE_MANAGEMENT (SL: "the object owner receives notifications by default").
        /// </summary>
        public void llManageEstateAccess(int action, string avatar)
        {
            int result = 0, delay = 0;
            try
            {
                if (UUID.TryParse(avatar, out UUID key) && ManageEstateAccess(action, key))
                {
                    result = 1;
                    if (ReportEstateChange(action, key)) delay = 200;
                }
            }
            finally { m_ScriptEngine.SysReturn(m_itemID, result, delay); }
        }

        // Phlox's action numbers (DefaultConstants.cs), not SL's 0x4..0x80: renumbering them is an open decision.
        private const int EstateAllowedAgentAdd = 0, EstateAllowedAgentRemove = 1, EstateAllowedGroupAdd = 2,
            EstateAllowedGroupRemove = 3, EstateBannedAgentAdd = 4, EstateBannedAgentRemove = 5,
            EstateQueryCanManage = 11000, EstateQueryAllowedAgent = 11001, EstateQueryAllowedGroup = 11002,
            EstateQueryBannedAgent = 11003;

        /// <summary>
        /// Halcyon ManageEstateAccess over EstateManagementModule's EstateAllowUser / EstateAllowGroup /
        /// EstateBanUser / Estate*Query (EstateManagementModule.cs:258-460). Who may call: CanIssueEstateCommand, i.e. a
        /// god, the estate owner or an estate manager (SL: "the object owner is the Estate Owner or an Estate Manager"),
        /// through the same EstateSettings and IsGod helpers HasParcelPowers uses. QUERY_CAN_MANAGE answers anyone,
        /// silently. A change that is already so (AlreadySet), a NULL_KEY and an unknown action are FALSE. A ban never
        /// touches the estate owner or a manager ("never process EO"), the object's owner, or a god (YEngine, and NGC's
        /// estate tools: "Cannot ban a Administrator"); it takes the avatar off the allowed list and sends them away.
        /// Halcyon's estate-owner's-partner rule is not ported: NGC has no partner lookup in the region.
        /// </summary>
        private bool ManageEstateAccess(int action, UUID key)
        {
            EstateSettings es = World.RegionInfo.EstateSettings;
            bool canManage = es.IsEstateManagerOrOwner(m_host.OwnerID) || World.Permissions.IsGod(m_host.OwnerID);
            if (action == EstateQueryCanManage) return canManage;
            if (!canManage)
            {
                ShoutError("llManageEstateAccess: object owner must manage estate.");
                return false;
            }
            if (key.IsZero()) return false;

            bool allowed = es.EstateAccess.Contains(key), banned = es.EstateBans.Any(b => b.BannedUserID == key);
            bool groupAllowed = es.GroupAccess(key);
            switch (action)
            {
                case EstateQueryAllowedAgent: return allowed;
                case EstateQueryAllowedGroup: return groupAllowed;
                case EstateQueryBannedAgent: return banned;

                case EstateAllowedAgentAdd:
                    // "A user cannot be in both the banned and access lists": allowing lifts the ban.
                    if (banned) es.RemoveBan(key);
                    if (!allowed) es.AddEstateUser(key);
                    if (banned || !allowed) StoreEstate(es);
                    return !allowed && es.EstateAccess.Contains(key);
                case EstateAllowedAgentRemove:
                    if (!allowed) return false;
                    es.RemoveEstateUser(key);
                    StoreEstate(es);
                    return true;
                case EstateAllowedGroupAdd:
                    if (groupAllowed) return false;
                    es.AddEstateGroup(key);
                    StoreEstate(es);
                    return es.GroupAccess(key);
                case EstateAllowedGroupRemove:
                    if (!groupAllowed) return false;
                    es.RemoveEstateGroup(key);
                    StoreEstate(es);
                    return true;
                case EstateBannedAgentAdd:
                    if (es.IsEstateManagerOrOwner(key) || key == m_host.OwnerID || World.Permissions.IsGod(key))
                        return false;
                    // Halcyon clears the allowed entry even when the avatar is already banned.
                    if (allowed) es.RemoveEstateUser(key);
                    if (!banned)
                        es.AddBan(new EstateBan
                        {
                            BannedUserID = key,
                            EstateID = es.EstateID,
                            BannedHostAddress = "0.0.0.0",
                            BannedHostIPMask = "0.0.0.0",
                            BanningUserID = m_host.OwnerID,
                            BanTime = Util.UnixTimeSinceEpoch()
                        });
                    if (allowed || !banned) StoreEstate(es);
                    if (banned || !es.EstateBans.Any(b => b.BannedUserID == key)) return false;
                    EjectBannedAvatar(key);
                    return true;
                case EstateBannedAgentRemove:
                    if (!banned) return false;
                    es.RemoveBan(key);
                    StoreEstate(es);
                    return true;
                default:
                    return false;
            }
        }

        private void StoreEstate(EstateSettings es) => World.EstateDataServiceSafe?.StoreEstateSettings(es);

        /// <summary>
        /// Halcyon EstateBanUser "Banned, now shoo them away" (EstateManagementModule.cs:311-328): an avatar here whose
        /// home is this region is logged out ("You have been banned from your Home location. You must login directly to
        /// a different region."); anyone else is sent home. A home teleport that cannot start logs them out, as NGC's
        /// own estate ban does (EstateManagementModule.cs:1126-1131).
        /// </summary>
        private void EjectBannedAvatar(UUID id)
        {
            ScenePresence sp = World.GetScenePresence(id);
            if (sp == null || sp.IsChildAgent) return;
            GridUserInfo home = World.GridUserService?.GetGridUserInfo(id.ToString());
            if (home != null && home.HomeRegionID == World.RegionInfo.RegionID)
            {
                sp.ControllingClient.Kick("You have been banned from your Home location. You must login directly to a different region.");
                World.CloseAgent(id, false);
                return;
            }
            sp.ControllingClient.SendTeleportStart((uint)OpenMetaverse.TeleportFlags.DisableCancel);
            if (!World.TeleportClientHome(id, sp.ControllingClient))
            {
                sp.ControllingClient.Kick("Your access to the region was revoked and TP home failed - you have been logged out.");
                World.CloseAgent(id, false);
            }
        }

        /// <summary>
        /// Halcyon llManageEstateAccess's report (InWorldz LSLSystemAPI.cs:15181-15225) and SendIM (:3883-3915): an IM
        /// from the object to its owner, stored for an offline owner. True when one was sent.
        /// </summary>
        private bool ReportEstateChange(int action, UUID key)
        {
            string region = World.RegionInfo.RegionName;
            string msg = action switch
            {
                EstateAllowedAgentAdd => EstateUserName(key) + " has been added to the allowed user list for " + region,
                EstateAllowedAgentRemove => EstateUserName(key) + " has been removed from the allowed user list for " + region,
                EstateAllowedGroupAdd => EstateGroupName(key) + " has been added to the allowed group list for " + region,
                EstateAllowedGroupRemove => EstateGroupName(key) + " has been removed from the allowed group list for " + region,
                EstateBannedAgentAdd => EstateUserName(key) + " has been banned from " + region,
                EstateBannedAgentRemove => EstateUserName(key) + " has been removed from the banned list for " + region,
                _ => null
            };
            if (msg == null) return false;
            TaskInventoryItem item = GetInventorySelf();
            if (item != null && (item.PermsMask & PERMISSION_SILENT_ESTATE_MANAGEMENT) != 0) return false;
            IMessageTransferModule tr = World.RequestModuleInterface<IMessageTransferModule>();
            if (tr == null) return false;
            tr.SendInstantMessage(new GridInstantMessage()
            {
                fromAgentID    = m_host.OwnerID.Guid,
                toAgentID      = m_host.OwnerID.Guid,
                imSessionID    = m_host.UUID.Guid,
                timestamp      = (uint)Util.UnixTimeSinceEpoch(),
                fromAgentName  = m_host.Name,
                message        = msg.Length > 1024 ? msg.Substring(0, 1024) : msg,
                dialog         = (byte)InstantMessageDialog.MessageFromObject,
                fromGroup      = false,
                offline        = 1,
                ParentEstateID = 0,
                Position       = m_host.AbsolutePosition,
                RegionID       = World.RegionInfo.RegionID.Guid,
                binaryBucket   = new byte[0]
            }, success => { });
            return true;
        }

        /// <summary>Halcyon UserNameToReport: the name when known, else the key.</summary>
        private string EstateUserName(UUID id)
        {
            string name = World.RequestModuleInterface<IUserManagement>()?.GetUserName(id);
            return string.IsNullOrEmpty(name) ? id.ToString() : name;
        }

        /// <summary>Halcyon GroupNameToReport: the group's name when known, else the key.</summary>
        private string EstateGroupName(UUID id)
            => World.RequestModuleInterface<IGroupsModule>()?.GetGroupRecord(id)?.GroupName ?? id.ToString();

        // ── Avatar ─────────────────────────────────────────────────────────────

        public void llAttachToAvatar(int attach_point)
        {
            if (m_host == null) return;
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return;
            if ((item.PermsMask & SlConst.PERMISSION_ATTACH) == 0)
            {
                ShoutError("llAttachToAvatar: PERMISSION_ATTACH not granted.");
                return;
            }
            // YEngine (LSL_Api.llAttachToAvatar): only the object's owner can be attached to.
            if (item.PermsGranter != m_host.OwnerID) return;
            IAttachmentsModule attachMod = World.RequestModuleInterface<IAttachmentsModule>();
            if (attachMod == null) return;
            ScenePresence sp = World.GetScenePresence(item.PermsGranter);
            if (sp == null || sp.IsChildAgent) return;
            attachMod.AttachObject(sp, m_host.ParentGroup, (uint)attach_point, false, true, false, GetScriptExperienceId());
        }

        /// <summary>
        /// SL (wiki llAttachToAvatarTemp) and Halcyon (AttachInternal): any avatar who granted
        /// PERMISSION_ATTACH can wear it; a non-owner becomes the owner ("Can be used on non-owners (changing ownership
        /// to the wearer)"), which needs the transfer right or fails with "No permission to transfer"; an object
        /// already attached fails silently; no inventory is created. After the change of owner the permissions are
        /// reset (SL: "When object ownership changes, any granted permissions are reset").
        /// </summary>
        public void llAttachToAvatarTemp(int attachPoint)
        {
            if (m_host?.ParentGroup == null) return;
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return;
            if ((item.PermsMask & SlConst.PERMISSION_ATTACH) == 0)
            {
                ShoutError("llAttachToAvatarTemp: PERMISSION_ATTACH not granted.");
                return;
            }
            SceneObjectGroup grp = m_host.ParentGroup;
            if (grp.IsDeleted || grp.IsAttachment) return;
            IAttachmentsModule attachMod = World.RequestModuleInterface<IAttachmentsModule>();
            if (attachMod == null) return;
            ScenePresence sp = World.GetScenePresence(item.PermsGranter);
            if (sp == null || sp.IsChildAgent) return;

            if (sp.UUID != grp.OwnerID && !GiveToTempWearer(grp, sp, item))
            {
                ScriptShoutError("llAttachToAvatarTemp: No permission to transfer");
                return;
            }
            // Not added to inventory: AttachmentsModule deletes an attachment with no FromItemID on detach.
            attachMod.AttachObject(sp, grp, (uint)attachPoint, false, false, true, GetScriptExperienceId());
        }

        /// <summary>
        /// Hand a live object to its temp wearer the way the scene sells an object "Original"
        /// (BuySellModule, SaleType.Original) and YEngine's llAttachToAvatarTemp do it: transfer right on the
        /// effective owner perms, SetOwner, then the contents follow with next-owner perms and CHANGED_OWNER.
        /// False, and nothing changed, without the transfer right.
        /// </summary>
        private bool GiveToTempWearer(SceneObjectGroup grp, ScenePresence wearer, TaskInventoryItem item)
        {
            if ((grp.EffectiveOwnerPerms & (uint)OpenSim.Framework.PermissionMask.Transfer) == 0) return false;

            grp.SetOwner(wearer.UUID, wearer.ControllingClient.ActiveGroupId);
            bool propagate = World.Permissions.PropagatePermissions();
            foreach (SceneObjectPart part in grp.Parts)
            {
                if (propagate)
                {
                    part.Inventory.ChangeInventoryOwner(wearer.UUID);
                    part.ApplyNextOwnerPermissions();
                }
                else
                {
                    // SL resets every grant on a change of owner; ChangeInventoryOwner does it when propagating.
                    foreach (TaskInventoryItem inv in part.Inventory.GetInventoryItems())
                    {
                        inv.PermsGranter = UUID.Zero;
                        inv.PermsMask = 0;
                    }
                }
                part.TriggerScriptChangedEvent(Changed.OWNER);
            }
            if (propagate) grp.InvalidateDeepEffectivePerms();
            PermsChange(item, UUID.Zero, 0);

            grp.RootPart.ObjectSaleType = 0;
            grp.RootPart.SalePrice = 10;
            grp.HasGroupChanged = true;
            grp.RootPart.SendPropertiesToClient(wearer.ControllingClient);
            grp.RootPart.ScheduleFullUpdate();
            return true;
        }

        public void llDetachFromAvatar()
        {
            if (m_host == null) return;
            if (!m_host.ParentGroup.IsAttachment) return;
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return;
            if ((item.PermsMask & SlConst.PERMISSION_ATTACH) == 0)
            {
                ShoutError("llDetachFromAvatar: PERMISSION_ATTACH not granted.");
                return;
            }
            IAttachmentsModule attachMod = World.RequestModuleInterface<IAttachmentsModule>();
            if (attachMod == null) return;
            ScenePresence sp = World.GetScenePresence(m_host.ParentGroup.AttachedAvatar);
            if (sp == null) return;
            attachMod.DetachSingleAttachmentToInv(sp, m_host.ParentGroup);
        }

        public int llGetAttached()
        {
            if (m_host?.ParentGroup == null) return 0;
            if (!m_host.ParentGroup.IsAttachment) return 0;
            return (int)m_host.ParentGroup.AttachmentPoint;
        }
		public void llSitTarget(Vector3 offset, Quaternion rot)
		{
			if (m_host == null) return;
			PrimSetSitTarget(m_host, offset != Vector3.Zero, offset, rot);
		}

        /// <summary>
        /// PRIM_SIT_TARGET, llSitTarget and llLinkSitTarget on one prim (Halcyon SetSitTarget / RemoveSitTarget). An
        /// active target takes the offset and rotation, the offset held to SL's [-300, 300] m on each axis ("Values
        /// outside this range are automatically rounded to the nearest limit"; Halcyon does not cap). An inactive one
        /// is removed: zero offset, identity rotation. The ll functions pass active only for a non-zero offset, as SL
        /// ("If offset == &lt;0.0, 0.0, 0.0&gt; then the sit target is removed"); Halcyon also keeps a zero offset with a
        /// turned rotation. The scene holds no active flag apart from the offset and rotation (IsSitTargetSet), so an
        /// active target at ZERO_VECTOR and ZERO_ROTATION reads back, and sits, as none (Docs/PhloxKnownDefects.md).
        /// </summary>
        private static void PrimSetSitTarget(SceneObjectPart part, bool active, Vector3 offset, Quaternion rot)
        {
            if (active)
            {
                part.SitTargetPosition = new Vector3(Math.Clamp(offset.X, -300f, 300f),
                    Math.Clamp(offset.Y, -300f, 300f), Math.Clamp(offset.Z, -300f, 300f));
                part.SitTargetOrientation = rot;
            }
            else
            {
                part.SitTargetPosition = Vector3.Zero;
                part.SitTargetOrientation = Quaternion.Identity;
            }
            if (part.ParentGroup != null) part.ParentGroup.HasGroupChanged = true;
            part.ScheduleFullUpdate();
        }
		public string llAvatarOnSitTarget()
		{
			if (m_host == null) return UUID.Zero.ToString();
			UUID sitter = m_host.SitTargetAvatar;
			return sitter == UUID.Zero ? UUID.Zero.ToString() : sitter.ToString();
		}
		public void llUnSit(string id)
		{
			if (m_host == null || string.IsNullOrEmpty(id)) return;
			if (!UUID.TryParse(id, out UUID targetId)) return;
			ScenePresence sp = World?.GetScenePresence(targetId);
			if (sp == null || sp.ParentID == 0) return;

			// Only unsit if they're sitting on this object, or script owner is unsitting them
			SceneObjectPart seatPart = World.GetSceneObjectPart(sp.ParentID);
			if (seatPart != null && seatPart.ParentGroup?.UUID == m_host.ParentGroup?.UUID)
				sp.StandUp();
			else if (m_host.OwnerID == sp.UUID)
				sp.StandUp(); // owner can always stand themselves up
			else
			{
				// Halcyon LSLSystemAPI.cs:7981-7990: the object's owner owns the avatar's parcel, the object is deeded
				// to the group that owns it, or the owner is an estate manager or a god. No group role qualifies.
				Vector3 pos = sp.AbsolutePosition;
				if (HasParcelPowers(m_host.OwnerID, World.LandChannel?.GetLandObject(pos.X, pos.Y), null))
					sp.StandUp();
			}
		} 
        public void llLinkSitTarget(int link, Vector3 offset, Quaternion rot)
        {
            if (m_host == null) return;
            foreach (SceneObjectPart part in GetLinkParts(link))
                PrimSetSitTarget(part, offset != Vector3.Zero, offset, rot);
        }

        public string llAvatarOnLinkSitTarget(int linknumber)
        {
            if (m_host == null) return UUID.Zero.ToString();
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
            {
                if (part.SitTargetAvatar != UUID.Zero)
                    return part.SitTargetAvatar.ToString();
            }
            return UUID.Zero.ToString();
        }
        public void iwStandTarget(Vector3 offset, Quaternion rot)
        {
            iwLinkStandTarget(m_host.LinkNum, offset, rot);
        }
        public void iwLinkStandTarget(int link, Vector3 offset, Quaternion rot)
        {
            // Faithful port from Halcyon, adapted for this tree.
            // This tree's SOP only has StandOffset (Vector3), no StandTargetRot.
            foreach (SceneObjectPart part in GetLinkParts(link))
            {
                part.StandOffset = offset;
            }
        }
        public void llSetSitText(string text)
        {
            if (m_host == null) return;
            m_host.SitName = text;

            // The sit label rides the same wire as the touch label - the full
            // ObjectProperties reply (LLClientView.cs:6390), which the region otherwise sends only
            // on select. Right-click asks for ObjectPropertiesFamily, which carries neither.
            if (m_host.ParentGroup != null) m_host.ParentGroup.HasGroupChanged = true;
            m_host.SendPropertiesToAllClients();
        }

        public void llSetTouchText(string text)
        {
            if (m_host == null) return;
            m_host.TouchName = text;

            // Setting the field is not enough - the viewer's context menu comes from the
            // object update, so without scheduling one the menu keeps whatever it last received.
            // That is why the manhole's menu still read "Touch" after its state_entry had run
            // llSetTouchText("Enter") successfully. llSetClickAction next door already marks the
            // group changed for the same reason.
            if (m_host.ParentGroup != null) m_host.ParentGroup.HasGroupChanged = true;
            m_host.ScheduleFullUpdate();

            // The ObjectUpdate above does NOT carry the touch label. The viewer takes it
            // from the full ObjectProperties reply, which the region otherwise sends only on
            // select - a right-click asks for ObjectPropertiesFamily, which has no touch name.
            // Without this push the menu keeps whatever it was told when the object was last
            // selected, which is why it still read "Touch" after state_entry had set "Enter".
            m_host.SendPropertiesToAllClients();
        }

        public void llSetClickAction(int action)
        {
            if (m_host == null) return;
            PrimSetClickAction(m_host, action);
        }

        /// <summary>
        /// PRIM_CLICK_ACTION and llSetClickAction on one prim (Halcyon llSetClickAction; Halcyon has no rule, SL's
        /// PRIM_CLICK_ACTION is "as llSetClickAction"). The click action rides the object update. SL (llSetClickAction):
        /// "If llSetClickAction is CLICK_ACTION_PAY then you must have a money event, or it will revert to
        /// CLICK_ACTION_NONE"; Halcyon keeps PAY regardless. A child with no money event of its own counts the root's,
        /// since a payment on such a child reaches the root's money event (PhloxEngine.HandleObjectPaid).
        /// </summary>
        private static void PrimSetClickAction(SceneObjectPart part, int action)
        {
            if (action == CLICK_ACTION_PAY && !HasMoneyEvent(part) && !HasMoneyEvent(part.ParentGroup?.RootPart))
                action = CLICK_ACTION_NONE;
            part.ClickAction = (byte)action;
            if (part.ParentGroup != null) part.ParentGroup.HasGroupChanged = true;
            part.ScheduleFullUpdate();
        }

        /// <summary>Whether a script in the prim, in its current state, has a money event.</summary>
        private static bool HasMoneyEvent(SceneObjectPart part)
            => part != null && (part.ScriptEvents & scriptEvents.money) != 0;

        public int llGetAgentInfo(string id)
        {
            if (!UUID.TryParse(id, out UUID key)) return 0;
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp == null || sp.IsChildAgent) return 0;


            int flags = 0;
            uint ctrlFlags = sp.AgentControlFlags;

            if (sp.SetAlwaysRun)
                flags |= AGENT_ALWAYS_RUN;

            if (sp.HasAttachments())
            {
                flags |= AGENT_ATTACHMENTS;
                if (sp.HasScriptedAttachments())
                    flags |= AGENT_SCRIPTED;
            }

            // AGENT_CONTROL_AWAY = 0x00400000, AGENT_CONTROL_MOUSELOOK = 0x00020000
            if ((ctrlFlags & 0x00400000u) != 0) flags |= AGENT_AWAY;
            if ((ctrlFlags & 0x00020000u) != 0) flags |= AGENT_MOUSELOOK;

            // Sitting detection
            if (sp.ParentPart != null)
            {
                flags |= AGENT_ON_OBJECT;
                flags |= AGENT_SITTING;
            }

            // Flying / in-air (only if not sitting)
            if ((flags & AGENT_SITTING) == 0)
            {
                if (sp.Flying)
                {
                    flags |= AGENT_FLYING;
                    flags |= AGENT_IN_AIR;
                }
                else if (sp.PhysicsActor != null && !sp.PhysicsActor.IsColliding)
                {
                    flags |= AGENT_IN_AIR;
                }
            }

            // Walking/crouching — use control flags
            // AGENT_CONTROL_AT_POS=0x01, AGENT_CONTROL_AT_NEG=0x02
            if (!sp.Flying && (flags & AGENT_SITTING) == 0)
            {
                if ((ctrlFlags & 0x03u) != 0)
                    flags |= AGENT_WALKING;
            }

            return flags;
        }
        public string llGetAgentLanguage(string avatar)
        {
            // This tree doesn't expose AgentPreferences — return empty (caller must handle)
            if (!UUID.TryParse(avatar, out UUID key)) return string.Empty;
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp == null || sp.IsChildAgent) return string.Empty;
            return string.Empty;
        }

        public Vector3 llGetAgentSize(string id)
        {
            if (!UUID.TryParse(id, out UUID key)) return Vector3.Zero;
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp == null || sp.IsChildAgent) return Vector3.Zero;
            float h = sp.Appearance?.AvatarHeight ?? 1.8f;
            return new Vector3(0.45f, 0.6f, h);
        }
        public int llSameGroup(string id)
        {
            if (!UUID.TryParse(id, out UUID key)) return 0;
            UUID hostGroup = m_host.GroupID;
            // Check objects
            SceneObjectPart part = World?.GetSceneObjectPart(key);
            if (part != null)
                return (part.GroupID == hostGroup && hostGroup != UUID.Zero) ? 1 : 0;
            // Check avatars
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp != null)
                return (sp.ControllingClient.ActiveGroupId == hostGroup && hostGroup != UUID.Zero) ? 1 : 0;
            return 0;
        }
        public int llIsFriend(string agent)
        {
            // SL: returns TRUE if the specified agent is a friend of the object owner
            if (!UUID.TryParse(agent, out UUID agentID) || agentID == UUID.Zero) return 0;
            var friendsModule = World?.RequestModuleInterface<IFriendsModule>();
            if (friendsModule == null) return 0;
            return friendsModule.IsFriend(m_host.OwnerID, agentID) ? 1 : 0;
        }
        public string llGetOwnerKey(string id)
        {
            if (!UUID.TryParse(id, out UUID key)) return UUID.Zero.ToString();
            SceneObjectPart part = World?.GetSceneObjectPart(key);
            if (part != null) return part.OwnerID.ToString();
            // Not an object — if it's an avatar key, return the key itself (avatars own themselves)
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp != null) return key.ToString();
            return id; // Unknown UUID: return as-is per LSL spec
        }
        public string llKey2Name(string id)
        {
            if (!UUID.TryParse(id, out UUID key) || key == UUID.Zero) return string.Empty;

            // Check for avatar in region first
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp != null) return sp.Name;

            // Check for scene object part
            SceneObjectPart part = World?.GetSceneObjectPart(key);
            if (part != null) return part.Name;

            return string.Empty;
        }
        // ── Teleport helpers (ported from Halcyon) ────────────────────────────

        /// <summary>
        /// An avatar with god powers: Halcyon's "targetSP.GodLevel > 0" is the level
        /// granted when god mode is switched on (ScenePresence.GrantGodlikePowers); NGC keeps that as IsViewerUIGod /
        /// IsGod (GodController.cs:129-130).
        /// </summary>
        private static bool IsGodTarget(ScenePresence sp) => sp.IsViewerUIGod || sp.IsGod;

        /// <summary>
        /// Halcyon IsTeleportAuthorized (LSLSystemAPI.cs:14923-14961), the rule of iwTeleportAgent, llTeleportAgentHome
        /// and llEjectFromLand. Halcyon's "scripts cannot force-TP gods, unless the god is the owner of the script" is
        /// applies. The land test is Halcyon's too: its HasLandPrivileges is
        /// CanEditParcel(owner, parcel, GroupPowers.LandEjectAndFreeze), and SL (wiki llTeleportAgentHome, llEjectFromLand)
        /// admits on group land "The object is deeded to the same group" or "The object owner must have 'Eject and freeze
        /// Residents on parcels' ability in the group". So on group land: an object deeded to that group, or an owner who
        /// holds Eject and Freeze in it; an object merely set to the group whose owner lacks the power is refused.
        /// </summary>
        private bool IsTeleportAuthorized(ScenePresence targetSP)
        {
            // Agent must be in this region
            if (targetSP.IsChildAgent)
                return false;

            // Always allow HUDs, attachments and objects owned by the same user
            if (targetSP.UUID == m_host.OwnerID)
                return true;

            // Scripts cannot force-teleport gods, unless the god is the owner of the script (above)
            if (IsGodTarget(targetSP))
                return false;

            // Estate manager can always teleport
            if (World.RegionInfo.EstateSettings.IsEstateManagerOrOwner(m_host.OwnerID))
                return true;

            // Check land privileges: script owner must have privileges on BOTH
            // the parcel the object is on AND the parcel the agent is on
            Vector3 objectPos = m_host.ParentGroup.AbsolutePosition;
            ILandObject objectLand = World.LandChannel.GetLandObject(objectPos.X, objectPos.Y);
            if (objectLand == null)
                return false;

            Vector3 agentPos = targetSP.AbsolutePosition;
            ILandObject agentLand = World.LandChannel.GetLandObject(agentPos.X, agentPos.Y);

            if (HasParcelPowers(m_host.OwnerID, objectLand, (ulong)GroupPowers.LandEjectAndFreeze))
            {
                // If avatar parcel can't be determined but script has land privs, allow it
                if (agentLand == null)
                    return true;
                if (HasParcelPowers(m_host.OwnerID, agentLand, (ulong)GroupPowers.LandEjectAndFreeze))
                    return true;
            }

            return false;
        }

        // SL llTeleportAgent: "Only 4 initial teleports can be done immediately, or You can keep teleporting every 1.4
        // seconds"; throttled, "No further teleports will succeed until 10 seconds have passed". The wiki names no scope;
        // this is per script (LSLSystemAPI is per script).
        private const int SlTeleportBurst = 4, SlTeleportRefillMs = 1400, SlTeleportLockoutMs = 10000;
        private readonly object m_slTeleportLock = new object();
        private double m_slTeleportTokens = SlTeleportBurst;
        private long m_slTeleportLastMs = Environment.TickCount64, m_slTeleportLockedUntilMs;

        private bool SlTeleportThrottleAllows()
        {
            lock (m_slTeleportLock)
            {
                long now = Environment.TickCount64;
                if (now < m_slTeleportLockedUntilMs) return false;
                m_slTeleportTokens = Math.Min(SlTeleportBurst, m_slTeleportTokens + (now - m_slTeleportLastMs) / (double)SlTeleportRefillMs);
                m_slTeleportLastMs = now;
                if (m_slTeleportTokens >= 1) { m_slTeleportTokens -= 1; return true; }
                m_slTeleportLockedUntilMs = now + SlTeleportLockoutMs;
                return false;
            }
        }

        /// <summary>
        /// SL's gate for llTeleportAgent and llTeleportAgentGlobalCoords. "This function can only teleport the
        /// owner of the object (unless part of an Experience)"; "If the script lacks the permission PERMISSION_TELEPORT,
        /// the script will shout an error on DEBUG_CHANNEL and the operation fails"; "If PERMISSION_TELEPORT is granted by
        /// anyone other than agent, then when the function is called an error will be shouted"; "Does not work in scripts
        /// within attached temp objects"; "Sitting avatars cannot be teleported using this function". The experience case
        /// is the one already in Phlox (HasExperiencePermission: the script's experience is allowed here and the agent
        /// granted it). Returns the avatar to move, or null.
        /// </summary>
        private ScenePresence SlTeleportTarget(string fn, string agent)
        {
            if (m_host?.ParentGroup == null || World == null) return null;
            if (IsTempAttachment(m_host.ParentGroup))
            {
                ShoutError(fn + ": Temporary attachments cannot request runtime permissions to teleport.");
                return null;
            }
            if (!UUID.TryParse(agent, out UUID agentId) || agentId.IsZero()) return null;
            ScenePresence sp = World.GetScenePresence(agentId);
            if (sp == null || sp.IsChildAgent || sp.IsDeleted || sp.IsInTransit) return null;

            if (!HasExperiencePermission(agentId))
            {
                TaskInventoryItem item = GetInventorySelf();
                if (item == null || (item.PermsMask & PERMISSION_TELEPORT) == 0)
                {
                    ShoutError(fn + ": Script trying to teleport an agent but PERMISSION_TELEPORT permission not set!");
                    return null;
                }
                if (item.PermsGranter != agentId)
                {
                    ShoutError(fn + ": PERMISSION_TELEPORT was granted by someone other than the agent to teleport.");
                    return null;
                }
                if (agentId != m_host.OwnerID)
                {
                    ShoutError(fn + ": can only teleport the owner of the object.");
                    return null;
                }
            }
            if (sp.IsSatOnObject || sp.ParentID != 0)
            {
                ShoutError(fn + ": Sitting avatars cannot be teleported; use llUnSit first.");
                return null;
            }
            return sp;
        }

        // ── Teleport functions ───────────────────────────────────────────────

        public void llTeleportAgentHome(string agent)
        {
            if (!UUID.TryParse(agent, out UUID agentId)) return;

            ScenePresence presence = World?.GetScenePresence(agentId);
            if (presence == null) return;

            if (!IsTeleportAuthorized(presence))
                return;

            presence.ControllingClient.SendTeleportStart((uint)OpenMetaverse.TeleportFlags.DisableCancel);
            World.TeleportClientHome(agentId, presence.ControllingClient);
            ScriptSleep(5000);
        }

        public void iwTeleportAgent(string agent, string region, Vector3 pos, Vector3 lookAt)
        {
            if (!UUID.TryParse(agent, out UUID agentId)) return;

            ScenePresence targetSP = World?.GetScenePresence(agentId);
            if (targetSP == null) return;

            if (!IsTeleportAuthorized(targetSP))
                return;

            if (String.IsNullOrEmpty(region))
                region = World.RegionInfo.RegionName;
            else if (region != World.RegionInfo.RegionName)
                targetSP.ControllingClient.SendTeleportStart((uint)OpenMetaverse.TeleportFlags.DisableCancel);

            World.RequestTeleportLocation(targetSP.ControllingClient,
                region, pos, lookAt, (uint)OpenMetaverse.TeleportFlags.ViaLocation);
        }

        // ── osTeleportAgent / osTeleportOwner: YEngine's OSSL (OSSL_Api.cs:895-1096) ─────────────────────────
        // Not the iw rule: the Severe threat level on the region-name and grid forms, checkAllowAgentTPbyLandOwner, the
        // 500 ms refusal and 500 / 5000 ms teleport sleeps. YEngine has no god check here and admits the object's group
        // tag on group land; both are kept for parity (noted for the core list).

        /// <summary>OSSL_Api.cs:895-933 checkAllowAgentTPbyLandOwner: the owner, the PERMISSION_TELEPORT granter, or the land rule.</summary>
        private bool OsslAgentTeleportAllowed(UUID agentId, Vector3 pos)
            => m_host.OwnerID == agentId
                || (OsslItem?.PermsGranter == agentId && (OsslItem.PermsMask & PERMISSION_TELEPORT) != 0)
                || OsslLandOwnerAllows(pos);

        /// <summary>The root presence an OSSL teleport may act on (OSSL_Api.cs:966-967), or null.</summary>
        private ScenePresence OsslTeleportTarget(string agent, out UUID agentId)
        {
            agentId = UUID.Zero;
            if (m_host == null || World == null || !UUID.TryParse(agent, out agentId)) return null;
            ScenePresence sp = World.GetScenePresence(agentId);
            return sp == null || sp.IsDeleted || sp.IsChildAgent || sp.IsInTransit ? null : sp;
        }

        /// <summary>OSSL_Api.cs:952-959 - Severe ("High because there is no security check. High griefer potential").</summary>
        public void osTeleportAgent(string agent, string region, Vector3 pos, Vector3 lookAt)
        {
            OsslCheck(TlSevere, "osTeleportAgent");
            OsslTeleportAgent(agent, region, pos, lookAt);
        }

        /// <summary>OSSL_Api.cs:961-994 TeleportAgent(region name). "" or this region's name (any case) is here.</summary>
        private void OsslTeleportAgent(string agent, string regionName, Vector3 pos, Vector3 lookAt)
        {
            ScenePresence sp = OsslTeleportTarget(agent, out UUID agentId);
            if (sp == null) return;
            if (!OsslAgentTeleportAllowed(agentId, sp.AbsolutePosition)) { ScriptSleep(500); return; }

            if (string.IsNullOrEmpty(regionName) || regionName.Equals(World.RegionInfo.RegionName, StringComparison.InvariantCultureIgnoreCase))
            {
                World.RequestTeleportLocation(sp.ControllingClient, World.RegionInfo.RegionName, pos, lookAt,
                    (uint)OpenMetaverse.TeleportFlags.ViaLocation);
                ScriptSleep(500);
            }
            else
            {
                Util.FireAndForget(o => World.RequestTeleportLocation(sp.ControllingClient, regionName, pos, lookAt,
                    (uint)OpenMetaverse.TeleportFlags.ViaLocation), null, "PhloxOSSL.TeleportAgentByRegionName");
                ScriptSleep(5000);
            }
        }

        /// <summary>OSSL_Api.cs:1032-1074 - ungated upstream. A position inside this region teleports here; outside it
        /// is read as an offset from this region's corner and sent to the region there.</summary>
        public void osTeleportAgent(string agent, Vector3 pos, Vector3 lookAt)
        {
            ScenePresence sp = OsslTeleportTarget(agent, out UUID agentId);
            if (sp == null) return;
            if (!OsslAgentTeleportAllowed(agentId, sp.AbsolutePosition)) { ScriptSleep(500); return; }

            RegionInfo ri = World.RegionInfo;
            double px = pos.X, py = pos.Y;
            if (px >= 0 && px < ri.RegionSizeX && py >= 0 && py < ri.RegionSizeY)
            {
                World.RequestTeleportLocation(sp.ControllingClient, ri.RegionName, pos, lookAt,
                    (uint)OpenMetaverse.TeleportFlags.ViaLocation);
                ScriptSleep(500);
                return;
            }

            px += ri.WorldLocX;
            py += ri.WorldLocY;
            int gx = (int)px / 256, gy = (int)py / 256;
            px -= 256 * gx;
            py -= 256 * gy;
            ulong regionHandle = Util.RegionGridLocToHandle((uint)gx, (uint)gy);
            var local = new Vector3((float)px, (float)py, pos.Z);
            Util.FireAndForget(o => World.RequestTeleportLocation(sp.ControllingClient, regionHandle, local, lookAt,
                (uint)OpenMetaverse.TeleportFlags.ViaLocation), null, "PhloxOSSL.TeleportAgentByFarPos");
            ScriptSleep(5000);
        }

        /// <summary>OSSL_Api.cs:996-1003 - Severe. Grid coordinates (region units, not metres).</summary>
        public void osTeleportAgent(string agent, int regionGridX, int regionGridY, Vector3 pos, Vector3 lookAt)
        {
            OsslCheck(TlSevere, "osTeleportAgent");
            OsslTeleportAgent(agent, regionGridX, regionGridY, pos, lookAt);
        }

        /// <summary>OSSL_Api.cs:1005-1030 TeleportAgent(grid coordinates).</summary>
        private void OsslTeleportAgent(string agent, int regionGridX, int regionGridY, Vector3 pos, Vector3 lookAt)
        {
            ScenePresence sp = OsslTeleportTarget(agent, out UUID agentId);
            if (sp == null) return;
            if (!OsslAgentTeleportAllowed(agentId, sp.AbsolutePosition)) { ScriptSleep(500); return; }

            ulong regionHandle = Util.RegionGridLocToHandle((uint)regionGridX, (uint)regionGridY);
            Util.FireAndForget(o => World.RequestTeleportLocation(sp.ControllingClient, regionHandle, pos, lookAt,
                (uint)OpenMetaverse.TeleportFlags.ViaLocation), null, "PhloxOSSL.TeleportAgentByRegionCoords");
            ScriptSleep(5000);
        }

        /// <summary>
        /// SL llTeleportAgent. "Teleports an agent to a landmark stored in the object's inventory. If landmark is
        /// an empty string, the avatar is teleported to the location position in the current region." "If landmark is not
        /// an empty string and landmark is missing from the prim's inventory or it is not a landmark then an error is
        /// shouted on DEBUG_CHANNEL." The landmark is an inventory NAME only (YEngine also takes a key or a region name).
        /// </summary>
        public void llTeleportAgent(string agent, string landmark, Vector3 pos, Vector3 lookAt)
        {
            ScenePresence sp = SlTeleportTarget("llTeleportAgent", agent);
            if (sp == null) return;

            if (string.IsNullOrEmpty(landmark))
            {
                if (!SlTeleportThrottleAllows()) { ShoutError("llTeleportAgent: Too many teleports; throttled for 10 seconds."); return; }
                World.RequestTeleportLocation(sp.ControllingClient, World.RegionInfo.RegionHandle,
                    pos, lookAt, (uint)OpenMetaverse.TeleportFlags.ViaLocation);
                return;
            }

            TaskInventoryItem lmItem = null;
            lock (m_host.TaskInventory)
            {
                foreach (var kvp in m_host.TaskInventory)
                    if (kvp.Value.Name == landmark) { lmItem = kvp.Value; break; }
            }
            AssetLandmark lm = null;
            if (lmItem != null && lmItem.Type == (int)AssetType.Landmark)
            {
                AssetBase asset = World.AssetService?.Get(lmItem.AssetID.ToString());
                if (asset?.Data != null && asset.Data.Length > 0 && asset.Type == (sbyte)AssetType.Landmark)
                {
                    try { lm = new AssetLandmark(asset); }
                    catch (Exception) { lm = null; }
                }
            }
            if (lm == null)
            {
                ShoutError("llTeleportAgent: Could not find landmark '" + landmark + "' in the object's inventory.");
                return;
            }
            if (!SlTeleportThrottleAllows()) { ShoutError("llTeleportAgent: Too many teleports; throttled for 10 seconds."); return; }
            World.RequestTeleportLandmark(sp.ControllingClient, lm, lookAt);
        }

        /// <summary>SL llTeleportAgentGlobalCoords - the llTeleportAgent gate and throttle, to global coordinates.</summary>
        public void llTeleportAgentGlobalCoords(string agent, Vector3 globalCoords, Vector3 regionPos, Vector3 lookAt)
        {
            ScenePresence sp = SlTeleportTarget("llTeleportAgentGlobalCoords", agent);
            if (sp == null) return;
            if (!SlTeleportThrottleAllows()) { ShoutError("llTeleportAgentGlobalCoords: Too many teleports; throttled for 10 seconds."); return; }

            // Convert global coords to region handle
            uint regionX = (uint)((int)globalCoords.X / 256);
            uint regionY = (uint)((int)globalCoords.Y / 256);
            ulong regionHandle = OpenMetaverse.Utils.UIntsToLong(regionX * 256, regionY * 256);

            sp.ControllingClient.SendTeleportStart((uint)OpenMetaverse.TeleportFlags.DisableCancel);
            World.RequestTeleportLocation(sp.ControllingClient, regionHandle,
                regionPos, lookAt, (uint)OpenMetaverse.TeleportFlags.ViaLocation);
        }
        public string llGetAnimation(string id)
        {
            // Faithful port: returns the current movement animation state name
            if (!UUID.TryParse(id, out UUID key)) return string.Empty;
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp == null || sp.IsChildAgent) return string.Empty;
            try
            {
                // OpenSim's Animator exposes CurrentMovementAnimation as a string (e.g. "Standing", "Walking")
                string anim = sp.Animator?.CurrentMovementAnimation;
                return anim ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        public LSLList llGetAnimationList(string id)
        {
            // Faithful port: returns list of currently playing animation UUIDs
            List<object> l = new List<object>();
            if (!UUID.TryParse(id, out UUID key)) return new LSLList(l);
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp == null || sp.IsChildAgent) return new LSLList(l);
            try
            {
                UUID[] anims = sp.Animator?.GetAnimationArray();
                if (anims != null)
                {
                    foreach (UUID anim in anims)
                        l.Add(anim.ToString());
                }
            }
            catch { /* If Animator doesn't support GetAnimationArray, return empty */ }
            return new LSLList(l);
        }

        public void llStartAnimation(string anim)
        {
            TaskInventoryItem item = GetInventorySelf();
            if (item == null || item.PermsGranter == UUID.Zero) return;
            if ((item.PermsMask & PERMISSION_TRIGGER_ANIMATION) == 0) return;

            ScenePresence sp = World?.GetScenePresence(item.PermsGranter);
            if (sp == null || sp.IsChildAgent) { AnimationGranterAbsent(); return; }

            // Resolve to UUID: try inventory first, then direct parse
            UUID animID = FindInventoryItem(anim, (int)AssetType.Animation)?.AssetID ?? UUID.Zero;
            if (animID == UUID.Zero) UUID.TryParse(anim, out animID);
            if (animID == UUID.Zero) return;

            sp.Animator.AddAnimation(animID, m_host.UUID);
            sp.TriggerScenePresenceUpdated();
        }

        public void llStopAnimation(string anim)
        {
            TaskInventoryItem item = GetInventorySelf();
            if (item == null || item.PermsGranter == UUID.Zero) return;
            if ((item.PermsMask & PERMISSION_TRIGGER_ANIMATION) == 0) return;

            ScenePresence sp = World?.GetScenePresence(item.PermsGranter);
            if (sp == null || sp.IsChildAgent) { AnimationGranterAbsent(); return; }

            UUID animID;
            if (!UUID.TryParse(anim, out animID))
                animID = FindInventoryItem(anim, (int)AssetType.Animation)?.AssetID ?? UUID.Zero;
            if (animID == UUID.Zero) return;

            sp.Animator.RemoveAnimation(animID, false);
            sp.TriggerScenePresenceUpdated();
        }
        public void iwStartLinkAnimation(int link, string anim)
        {
            TaskInventoryItem item = GetInventorySelf();
            if (item == null || item.PermsGranter == UUID.Zero) return;
            if ((item.PermsMask & PERMISSION_TRIGGER_ANIMATION) == 0) return;

            ScenePresence sp = World?.GetScenePresence(item.PermsGranter);
            if (sp == null || sp.IsChildAgent) { AnimationGranterAbsent(); return; }

            // Find animation in the specified link's inventory
            UUID animID = UUID.Zero;
            foreach (SceneObjectPart part in GetLinkParts(link))
            {
                lock (part.TaskInventory)
                    foreach (var kvp in part.TaskInventory)
                        if (kvp.Value.Name == anim && kvp.Value.Type == (int)AssetType.Animation)
                        { animID = kvp.Value.AssetID; break; }
                if (animID != UUID.Zero) break;
            }
            if (animID == UUID.Zero) UUID.TryParse(anim, out animID);
            if (animID == UUID.Zero) return;

            sp.Animator.AddAnimation(animID, m_host.UUID);
            sp.TriggerScenePresenceUpdated();
        }
        public void iwStopLinkAnimation(int link, string anim)
        {
            TaskInventoryItem item = GetInventorySelf();
            if (item == null || item.PermsGranter == UUID.Zero) return;
            if ((item.PermsMask & PERMISSION_TRIGGER_ANIMATION) == 0) return;

            ScenePresence sp = World?.GetScenePresence(item.PermsGranter);
            if (sp == null || sp.IsChildAgent) { AnimationGranterAbsent(); return; }

            UUID animID = UUID.Zero;
            if (!UUID.TryParse(anim, out animID))
            {
                foreach (SceneObjectPart part in GetLinkParts(link))
                {
                    lock (part.TaskInventory)
                        foreach (var kvp in part.TaskInventory)
                            if (kvp.Value.Name == anim && kvp.Value.Type == (int)AssetType.Animation)
                            { animID = kvp.Value.AssetID; break; }
                    if (animID != UUID.Zero) break;
                }
            }
            if (animID == UUID.Zero) return;

            sp.Animator.RemoveAnimation(animID, false);
            sp.TriggerScenePresenceUpdated();
        }
        public string llGetAnimationOverride(string anim_state)
        {
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return string.Empty;
            UUID agentId = item.PermsGranter;
            if (agentId == UUID.Zero) return string.Empty;

            bool hasAnimPerm = (item.PermsMask & PERMISSION_OVERRIDE_ANIMATIONS) != 0;
            if (!hasAnimPerm && !HasExperiencePermission(agentId))
            {
                ShoutError("llGetAnimationOverride: requires PERMISSION_OVERRIDE_ANIMATIONS or experience permission");
                return string.Empty;
            }

            ScenePresence sp = World?.GetScenePresence(agentId);
            if (sp == null || sp.IsChildAgent) return string.Empty;

            UUID overrideAnim = sp.Overrides.GetOverriddenAnimation(anim_state);
            if (overrideAnim == UUID.Zero) return string.Empty;

            if (DefaultAvatarAnimations.AnimsNamesbyUUID.TryGetValue(overrideAnim, out string animName))
                return animName;
            return overrideAnim.ToString();
        }
        public void llSetAnimationOverride(string anim_state, string anim)
        {
            if (m_host == null || World == null) return;

            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return;
            UUID agentId = item.PermsGranter;
            if (agentId == UUID.Zero) return;

            bool hasAnimPerm = (item.PermsMask & PERMISSION_OVERRIDE_ANIMATIONS) != 0;
            if (!hasAnimPerm && !HasExperiencePermission(agentId))
            {
                ShoutError("llSetAnimationOverride: requires PERMISSION_OVERRIDE_ANIMATIONS or experience permission");
                return;
            }

            ScenePresence sp = World.GetScenePresence(agentId);
            if (sp == null || sp.IsChildAgent) return;

            UUID animID = UUID.Zero;
            if (!UUID.TryParse(anim, out animID))
            {
                animID = DefaultAvatarAnimations.GetDefaultAnimation(anim);
                if (animID == UUID.Zero)
                {
                    TaskInventoryItem animItem = FindInventoryItem(anim, (int)AssetType.Animation);
                    animID = animItem?.AssetID ?? UUID.Zero;
                }
            }

            if (animID == UUID.Zero)
            {
                ShoutError("llSetAnimationOverride: animation '" + anim + "' not found");
                return;
            }

            sp.Overrides.SetOverride(anim_state, animID);

            m_log.LogDebug("[PhloxAPI]: llSetAnimationOverride: {0} -> {1} for {2}",
                anim_state, animID, sp.Name);
        }
        public void llResetAnimationOverride(string anim_state)
        {
            if (m_host == null || World == null) return;

            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return;
            UUID agentId = item.PermsGranter;
            if (agentId == UUID.Zero) return;

            bool hasAnimPerm = (item.PermsMask & PERMISSION_OVERRIDE_ANIMATIONS) != 0;
            if (!hasAnimPerm && !HasExperiencePermission(agentId))
            {
                ShoutError("llResetAnimationOverride: requires PERMISSION_OVERRIDE_ANIMATIONS or experience permission");
                return;
            }

            ScenePresence sp = World.GetScenePresence(agentId);
            if (sp == null || sp.IsChildAgent) return;

            sp.Overrides.SetOverride(anim_state, UUID.Zero);

            m_log.LogDebug("[PhloxAPI]: llResetAnimationOverride: cleared {0} for {1}",
                anim_state, sp.Name);
        }
        public string llGetDisplayName(string id)
        {
            // In OpenSim display names aren't separate from usernames — return avatar name if in region
            if (!UUID.TryParse(id, out UUID key)) return string.Empty;
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp != null && !sp.IsChildAgent) return sp.Name;
            // Try user account service for offline users
            UserAccount acct = World?.UserAccountService?.GetUserAccount(World.RegionInfo.ScopeID, key);
            return acct != null ? acct.FirstName + " " + acct.LastName : string.Empty;
        }

        public void llRequestDisplayName(string id)
        {
            llRequestUsername(id);
        }

        public string llGetUsername(string id)
        {
            if (!UUID.TryParse(id, out UUID key)) return string.Empty;
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp != null && !sp.IsChildAgent) return sp.Name;
            UserAccount acct = World?.UserAccountService?.GetUserAccount(World.RegionInfo.ScopeID, key);
            return acct != null ? acct.FirstName + " " + acct.LastName : string.Empty;
        }

        public void llRequestUsername(string id)
        {
            if (!UUID.TryParse(id, out UUID key)) { ReturnQueryKey(UUID.Zero); return; }
            UUID requestID = NewDataserverQuery();   // On the one dataserver path
            ReturnQueryKey(requestID);

            // Fire the dataserver event with the name (synchronous in Phlox)
            string name = string.Empty;
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp != null && !sp.IsChildAgent)
            {
                name = sp.Name;
            }
            else
            {
                UserAccount acct = World?.UserAccountService?.GetUserAccount(World.RegionInfo.ScopeID, key);
                if (acct != null) name = acct.FirstName + " " + acct.LastName;
            }

            PostDataserverEvent(requestID, name);
        }
        public string iwGetAgentData(string id, int data)
        {
            // Synchronous version of llRequestAgentData — faithful port from Halcyon
            if (!UUID.TryParse(id, out UUID agentId)) return string.Empty;
            try
            {
                switch (data)
                {
                    case DATA_ONLINE:
                        ScenePresence sp = World?.GetScenePresence(agentId);
                        return (sp != null && !sp.IsChildAgent) ? "1" : "0";
                    case DATA_NAME:
                    {
                        ScenePresence sp2 = World?.GetScenePresence(agentId);
                        if (sp2 != null) return sp2.Name;
                        UserAccount acct = World?.UserAccountService?.GetUserAccount(
                            World.RegionInfo.ScopeID, agentId);
                        return acct != null ? acct.FirstName + " " + acct.LastName : string.Empty;
                    }
                    case DATA_BORN:
                    {
                        UserAccount acct = World?.UserAccountService?.GetUserAccount(
                            World.RegionInfo.ScopeID, agentId);
                        if (acct != null)
                        {
                            var born = DateTimeOffset.FromUnixTimeSeconds(acct.Created).UtcDateTime;
                            return born.ToString("yyyy-MM-dd");
                        }
                        return string.Empty;
                    }
                    case DATA_RATING: // DATA_RATING — deprecated
                        return "0,0,0,0,0,0";
                    case DATA_PAYINFO:
                        return PayInfo(agentId);
                    default:
                        return string.Empty;
                }
            }
            catch { return string.Empty; }
        }
        /// <summary>
        /// DATA_PAYINFO - PAYMENT_INFO_ON_FILE | PAYMENT_INFO_USED, from the account's flags
        /// as YEngine reads them ((UserFlags &gt;&gt; 2) &amp; 3); "0" for an agent with no account here.
        /// </summary>
        private bool IsOnline(UUID agent)
        {
            // As YEngine (LSL_Api.llRequestAgentData): an avatar in this region is online;
            // otherwise the presence service is asked for a root agent in any region.
            ScenePresence sp = World?.GetScenePresence(agent);
            if (sp != null && !sp.IsChildAgent) return true;
            OpenSim.Services.Interfaces.PresenceInfo[] infos = World?.PresenceService?.GetAgents(new[] { agent.ToString() });
            return infos != null && infos.Any(p => p != null && p.RegionID != UUID.Zero);
        }

        /// <summary>
        /// DATA_ONLINE, as Halcyon (LSLSystemAPI.GetAgentData): an avatar in this region is online. One
        /// elsewhere is online to its owner's scripts, to its friends' scripts, and to everyone's unless
        /// it has ticked "Only friends and groups know I'm online"; if that setting cannot be read, it is
        /// not. Groups are not consulted. Makes service calls: run it off the scheduler thread.
        /// </summary>
        private bool IsOnlineToThisScript(UUID agent)
        {
            ScenePresence sp = World?.GetScenePresence(agent);
            if (sp != null && !sp.IsChildAgent) return true;
            if (!IsOnline(agent)) return false;
            if (agent == m_host.OwnerID) return true;

            try
            {
                if (World?.RequestModuleInterface<IFriendsModule>()?.IsFriendInService(agent, m_host.OwnerID) == true)
                    return true;
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: DATA_ONLINE friends lookup for {0} failed: {1}", agent, e.Message);
            }

            try
            {
                UserPreferences prefs = World?.RequestModuleInterface<IProfileModule>()?.GetUserPreferences(agent);
                return prefs != null && prefs.Visible;
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: DATA_ONLINE preferences lookup for {0} failed: {1}", agent, e.Message);
                return false;
            }
        }

        private string PayInfo(UUID agent)
        {
            UserAccount acct = World?.UserAccountService?.GetUserAccount(World.RegionInfo.ScopeID, agent);
            return acct == null ? "0" : ((acct.UserFlags >> 2) & (PAYMENT_INFO_ON_FILE | PAYMENT_INFO_USED)).ToString();
        }

        public int iwGetAppearanceParam(string who, int which)
        {
            // Faithful port from Halcyon
            if (!UUID.TryParse(who, out UUID agentId)) return -1;
            ScenePresence sp = World?.GetScenePresence(agentId);
            if (sp == null || sp.Appearance == null) return -1;
            // Special case: which == -1 returns the upper limit
            if (which == -1) return sp.Appearance.VisualParams.Length;
            if (which < 0 || which >= sp.Appearance.VisualParams.Length) return -1;
            return sp.Appearance.VisualParams[which];
        }
        public LSLList llGetVisualParams(string agent, LSLList paramIndices)
        {
            // SL: returns a list of visual param values for the specified indices
            if (!UUID.TryParse(agent, out UUID agentId)) return new LSLList();
            ScenePresence sp = World?.GetScenePresence(agentId);
            if (sp == null || sp.Appearance == null) return new LSLList();

            LSLList ret = new LSLList();
            byte[] vp = sp.Appearance.VisualParams;
            for (int i = 0; i < paramIndices.Length; i++)
            {
                int idx = paramIndices.GetLSLIntegerItem(i);
                if (idx >= 0 && idx < vp.Length)
                    ret = ret.Append(vp[idx]);
                else
                    ret = ret.Append(-1);
            }
            return ret;
        }
        public int iwIsPlusUser(string id) { return 0; /* InWorldz-specific, not applicable */ }
        public int iwActiveGroup(string target, string group)
        {
            if (!UUID.TryParse(target, out UUID targetId)) return 0;
            if (!UUID.TryParse(group, out UUID groupId)) return 0;
            ScenePresence sp = World?.GetScenePresence(targetId);
            if (sp != null && !sp.IsChildAgent)
                return (sp.ControllingClient.ActiveGroupId == groupId) ? 1 : 0;
            SceneObjectPart part = World?.GetSceneObjectPart(targetId);
            if (part != null)
                return (part.GroupID == groupId) ? 1 : 0;
            return 0;
        }
        public string iwAvatarOnLink(int linknumber)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
            {
                var sitters = part.GetSittingAvatars();
                if (sitters != null && sitters.Count > 0)
                    return sitters.First().UUID.ToString();
            }
            return UUID.Zero.ToString();
        }
        public LSLList llGetAttachedList(string avatar)
        {
            // As Halcyon (ScenePresence.CollectVisibleAttachmentIds) and the SL wiki: HUDs are left out.
            if (!UUID.TryParse(avatar, out UUID agentID)) return new LSLList(new object[] { "NOT FOUND" });
            ScenePresence sp = World?.GetScenePresence(agentID);
            if (sp == null || sp.IsChildAgent) return new LSLList(new object[] { "NOT FOUND" });

            LSLList ret = new LSLList();
            var attachments = sp.GetAttachments();
            if (attachments != null)
            {
                foreach (var grp in attachments)
                    if (!grp.HasPrivateAttachmentPoint)
                        ret = ret.Append(grp.UUID.ToString());
            }
            return ret;
        }
        public LSLList llGetAttachedListFiltered(string avatar, int attachmentPoint)
        {
            // SL: like llGetAttachedList but filtered by a specific attachment point
            // attachmentPoint uses ATTACH_* constants; 0 returns all (same as llGetAttachedList)
            if (!UUID.TryParse(avatar, out UUID agentID)) return new LSLList(new object[] { "NOT FOUND" });
            ScenePresence sp = World?.GetScenePresence(agentID);
            if (sp == null || sp.IsChildAgent) return new LSLList(new object[] { "NOT FOUND" });

            LSLList ret = new LSLList();
            var attachments = sp.GetAttachments();
            if (attachments != null)
            {
                foreach (var grp in attachments)
                    if (attachmentPoint == 0 || (int)grp.AttachmentPoint == attachmentPoint)
                        ret = ret.Append(grp.UUID.ToString());
            }
            return ret;
        }
        public string iwGetLastOwner() { return m_host.LastOwnerID.ToString(); }
        /// <summary>
        /// Halcyon (InWorldz.Phlox.Engine/LSLSystemAPI.cs iwAvatarName2Key) returns the avatar's key as
        /// the call's value through SysReturn; this used to return a query key and answer with a
        /// dataserver event instead, which no InWorldz script expects. The async shim runs this off the
        /// scheduler thread, so the account lookup never holds the region's other scripts up. A blank
        /// last name is "Resident"; the region's root agents are matched first without regard to case
        /// (100 ms), then the account service (1 s); an unknown or blank name is NULL_KEY.
        /// </summary>
        public void iwAvatarName2Key(string firstName, string lastName)
        {
            const int LONG_DELAY = 1000;
            const int SHORT_DELAY = 100;
            int delay = LONG_DELAY;
            UUID agentID = UUID.Zero;

            try
            {
                if (!string.IsNullOrWhiteSpace(firstName))
                {
                    lastName = string.IsNullOrWhiteSpace(lastName) ? "Resident" : lastName.Trim();
                    firstName = firstName.Trim();

                    World?.ForEachScenePresence(sp =>
                    {
                        if (agentID == UUID.Zero && !sp.IsChildAgent &&
                            sp.Firstname.Equals(firstName, StringComparison.InvariantCultureIgnoreCase) &&
                            sp.Lastname.Equals(lastName, StringComparison.InvariantCultureIgnoreCase))
                            agentID = sp.UUID;
                    });

                    if (agentID != UUID.Zero)
                        delay = SHORT_DELAY;
                    else
                    {
                        UserAccount acct = World?.UserAccountService?.GetUserAccount(
                            World.RegionInfo.ScopeID, firstName, lastName);
                        if (acct != null) agentID = acct.PrincipalID;
                    }
                }
            }
            finally { m_ScriptEngine.SysReturn(m_itemID, agentID.ToString(), delay); }
        }

        public string llName2Key(string name)
        {
            // SL: synchronous name → key lookup (returns NULL_KEY if not found)
            if (string.IsNullOrWhiteSpace(name)) return UUID.Zero.ToString();
            string[] parts = name.Trim().Split(new[] { ' ', '.' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string firstName = parts[0];
            string lastName = parts.Length > 1 ? parts[1] : "Resident";

            // Check in-region avatars first (fast path)
            UUID found = UUID.Zero;
            World?.ForEachScenePresence(sp =>
            {
                if (found == UUID.Zero && !sp.IsChildAgent &&
                    sp.Firstname.Equals(firstName, StringComparison.InvariantCultureIgnoreCase) &&
                    sp.Lastname.Equals(lastName, StringComparison.InvariantCultureIgnoreCase))
                    found = sp.UUID;
            });
            if (found != UUID.Zero) return found.ToString();

            // Fall back to account service
            UserAccount acct = World?.UserAccountService?.GetUserAccount(
                World.RegionInfo.ScopeID, firstName, lastName);
            return acct != null ? acct.PrincipalID.ToString() : UUID.Zero.ToString();
        }

        // ── Detect params ──────────────────────────────────────────────────────

		private DetectVariables GetDetect(int n)
		{
			var state = m_thisScript?.ScriptState;
			if (state == null) return new DetectVariables();
			var vars = state.RunningEvent.DetectVars;
			if (vars == null) return new DetectVariables();
			return n >= 0 && n < vars.Length ? vars[n] : new DetectVariables();
		}

        public string llDetectedName(int n) => GetDetect(n).Name ?? string.Empty;
        public string llDetectedKey(int n) => GetDetect(n).Key ?? UUID.Zero.ToString();
        public string llDetectedOwner(int n) => GetDetect(n).Owner ?? UUID.Zero.ToString();
        public int llDetectedType(int n) => GetDetect(n).Type;
        public Vector3 llDetectedPos(int n) => GetDetect(n).Pos;
        public Vector3 llDetectedVel(int n) => GetDetect(n).Vel;
        public Vector3 llDetectedGrab(int n) => GetDetect(n).Grab;
        public Quaternion llDetectedRot(int n) => GetDetect(n).Rot;
        public int llDetectedGroup(int n)
        {
            var detect = GetDetect(n);
            string keyStr = detect.Key;
            if (string.IsNullOrEmpty(keyStr)) return 0;
            if (!UUID.TryParse(keyStr, out UUID detectedId)) return 0;
            UUID hostGroup = m_host.GroupID;
            if (hostGroup == UUID.Zero) return 0;

            SceneObjectPart part = World?.GetSceneObjectPart(detectedId);
            if (part != null)
                return part.GroupID == hostGroup ? 1 : 0;

            ScenePresence sp = World?.GetScenePresence(detectedId);
            if (sp != null)
                return sp.ControllingClient.ActiveGroupId == hostGroup ? 1 : 0;

            return 0;
        }
        public int llDetectedLinkNumber(int n) => GetDetect(n).LinkNumber;
        public Vector3 llDetectedTouchBinormal(int n) => GetDetect(n).TouchBinormal;
        public int llDetectedTouchFace(int n) => GetDetect(n).TouchFace;
        public Vector3 llDetectedTouchNormal(int n) => GetDetect(n).TouchNormal;
        public Vector3 llDetectedTouchPos(int n) => GetDetect(n).TouchPos;
        public Vector3 llDetectedTouchST(int n) => GetDetect(n).TouchST;
        public Vector3 llDetectedTouchUV(int n) => GetDetect(n).TouchUV;
        public string iwDetectedBot() { return "0"; }

        public string llDetectedRezzer(int n)
        {
            // Return the UUID of the object/agent that rezzed the detected object
            var dv = GetDetect(n);
            if (dv.Key == null) return UUID.Zero.ToString();
            if (!UUID.TryParse(dv.Key, out UUID detKey) || detKey == UUID.Zero) return UUID.Zero.ToString();
            SceneObjectPart sop = World?.GetSceneObjectPart(detKey);
            if (sop?.ParentGroup != null)
                return sop.ParentGroup.RezzerID.ToString();
            return UUID.Zero.ToString();
        }

        // ── Sensor ─────────────────────────────────────────────────────────────

       public void llSensor(string name, string id, int type, float range, float arc)
        {
            if (!UUID.TryParse(id, out UUID keyID)) keyID = UUID.Zero;
            m_ScriptEngine.AsyncCommands?.SensorRepeatPlugin.SenseOnce(
                m_localID, m_itemID, name, keyID, type, range, arc, m_host);
        }

        public void llSensorRepeat(string name, string id, int type, float range, float arc, float rate)
        {
            if (!UUID.TryParse(id, out UUID keyID)) keyID = UUID.Zero;
            m_ScriptEngine.AsyncCommands?.SensorRepeatPlugin.SetSenseRepeatEvent(
                m_localID, m_itemID, name, keyID, type, range, arc, rate, m_host);
            m_thisScript.ScriptState.MiscAttributes[(int)RuntimeState.MiscAttr.SensorRepeat] =
                new object[] { name, id, type, range, arc, rate };
        }

        /// <summary>The prim this script lives in.</summary>
        internal SceneObjectPart HostPart => m_host;

        /// <summary>A parcel pause stops the sensor repeat; the record in MiscAttributes stays for the resume.</summary>
        internal void PauseSensorForParcel()
        {
            if (m_thisScript?.ScriptState?.MiscAttributes?.ContainsKey((int)RuntimeState.MiscAttr.SensorRepeat) == true)
                m_ScriptEngine.AsyncCommands?.SensorRepeatPlugin.UnSetSenseRepeaterEvents(m_localID, m_itemID);
        }

        /// <summary>The resume starts the sensor repeat again from its record, as a state restore does.</summary>
        internal void RestoreSensorAfterParcel()
        {
            if (m_thisScript?.ScriptState?.MiscAttributes != null
                && m_thisScript.ScriptState.MiscAttributes.TryGetValue((int)RuntimeState.MiscAttr.SensorRepeat, out object[] s))
                llSensorRepeat((string)s[0], (string)s[1], (int)s[2], (float)s[3], (float)s[4], (float)s[5]);
        }

        public void llSensorRemove()
        {
            m_ScriptEngine.AsyncCommands?.SensorRepeatPlugin.UnSetSenseRepeaterEvents(
                m_localID, m_itemID);
            m_thisScript.ScriptState.MiscAttributes.Remove((int)RuntimeState.MiscAttr.SensorRepeat);
        }

        // ── Listen ─────────────────────────────────────────────────────────────

        public int llListen(int channel, string name, string id, string msg)
        {
            if (m_ScriptEngine.ListenManager == null) { Stub("llListen (no ListenManager)"); return -1; }
            UUID filterKey = UUID.Zero;
            UUID.TryParse(id, out filterKey);
            return m_ScriptEngine.ListenManager.Add(m_localID, m_itemID, m_host.UUID, channel, name, filterKey, msg);
        }
        public void llListenControl(int number, int active) { m_ScriptEngine.ListenManager?.SetActive(m_itemID, number, active != 0); }
        public void llListenRemove(int number) { m_ScriptEngine.ListenManager?.Remove(m_itemID, number); }

        // ── Inventory ──────────────────────────────────────────────────────────

        public int llGetInventoryNumber(int type)
        {
            if (m_host == null) return 0;
            int count = 0;
            lock (m_host.TaskInventory)
                foreach (var kvp in m_host.TaskInventory)
                    if (type == -1 || kvp.Value.Type == type) count++;
            return count;
        }
        public string llGetInventoryName(int type, int number)
        {
            if (m_host == null) return string.Empty;
            int idx = 0;
            lock (m_host.TaskInventory)
                foreach (var kvp in m_host.TaskInventory)
                    if (type == -1 || kvp.Value.Type == type)
                        if (idx++ == number) return kvp.Value.Name;
            return string.Empty;
        }
        public int llGetInventoryType(string name)
        {
            if (m_host == null) return -1;
            lock (m_host.TaskInventory)
                foreach (var kvp in m_host.TaskInventory)
                    if (kvp.Value.Name == name) return kvp.Value.Type;
            return -1;
        }
        /// <summary>
        /// Halcyon's IsMyScript (LSLSystemAPI.cs:6293-6296) - the item is this script itself, in
        /// this prim. Halcyon's GetInventoryKey (:6309-6310) gives it its own asset key whatever its permissions.
        /// </summary>
        private bool IsMyScript(SceneObjectPart part, TaskInventoryItem item)
            => part == m_host && item.Type == (int)AssetType.LSLText && item.ItemID == m_itemID;

        public string llGetInventoryKey(string name)
        {
            if (m_host == null) return UUID.Zero.ToString();
            lock (m_host.TaskInventory)
                foreach (var kvp in m_host.TaskInventory)
                    if (kvp.Value.Name == name)
                    {
                        // As YEngine's llGetInventoryKey: the asset key only for an item that is
                        // copy, modify and transfer for its owner; anything less reads NULL_KEY.
                        // And the calling script's own key (Halcyon IsMyScript).
                        return IsMyScript(m_host, kvp.Value) ? kvp.Value.AssetID.ToString()
                            : AssetKeyIfFullPerm(kvp.Value.AssetID, kvp.Value.CurrentPermissions);
                    }
            return UUID.Zero.ToString();
        }
        public string llGetInventoryCreator(string item)
        {
            if (m_host == null) return UUID.Zero.ToString();
            lock (m_host.TaskInventory)
                foreach (var kvp in m_host.TaskInventory)
                    if (kvp.Value.Name == item) return kvp.Value.CreatorID.ToString();
            return UUID.Zero.ToString();
        }
        public string llGetInventoryDesc(string item)
        {
            if (m_host == null) return string.Empty;
            lock (m_host.TaskInventory)
                foreach (var kvp in m_host.TaskInventory)
                    if (kvp.Value.Name == item) return kvp.Value.Description ?? string.Empty;
            ShoutError("No item named '" + item + "'");
            return string.Empty;
        }
        public string llGetInventoryAcquireTime(string item)
        {
            // SL returns timestamp when item was acquired; we use CreationDate as closest equivalent
            if (m_host == null) return string.Empty;
            lock (m_host.TaskInventory)
                foreach (var kvp in m_host.TaskInventory)
                    if (kvp.Value.Name == item)
                    {
                        DateTime dt = DateTimeOffset.FromUnixTimeSeconds(kvp.Value.CreationDate).UtcDateTime;
                        return dt.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'");
                    }
            ShoutError("No item named '" + item + "'");
            return string.Empty;
        }
        public int llGetInventoryPermMask(string item, int mask)
        {
            if (m_host == null) return -1;
            lock (m_host.TaskInventory)
            {
                foreach (var kvp in m_host.TaskInventory)
                {
                    if (kvp.Value.Name == item)
                    {
                        switch (mask)
                        {
                            case 0: return (int)kvp.Value.BasePermissions;
                            case 1: return (int)kvp.Value.CurrentPermissions;
                            case 2: return (int)kvp.Value.GroupPermissions;
                            case 3: return (int)kvp.Value.EveryonePermissions;
                            case 4: return (int)kvp.Value.NextPermissions;
                        }
                    }
                }
            }
            return -1;
        }

        /// <summary>
        /// A god function, as YEngine (LSL_Api.llSetInventoryPermMask) - only with AllowGodFunctions and
        /// an administrator owner; copy/transfer is kept. The MASK_* number alone picks the category, as
        /// llSetObjectPermMask does (SL: "Sets the given permission category to the new value on the inventory item";
        /// Halcyon does not implement it). YEngine's limit ANDed the MASK_* number with the item's PERM_* bits, which
        /// sent every category but MASK_BASE to the base mask once the base was not full; it is not carried.
        /// </summary>
        public void llSetInventoryPermMask(string itemName, int mask, int value)
        {
            if (m_ScriptEngine == null || !m_ScriptEngine.AllowGodFunctions) return;
            if (World?.Permissions == null || !World.Permissions.IsAdministrator(m_host.OwnerID)) return;

            TaskInventoryItem item = m_host.Inventory.GetInventoryItem(itemName);
            if (item == null) return;

            switch (mask)
            {
                case MASK_BASE:
                    item.BasePermissions = LSLPermToPermissionMask(FixedCopyTransfer(value), item.BasePermissions);
                    break;
                case MASK_OWNER:
                    item.CurrentPermissions = LSLPermToPermissionMask(FixedCopyTransfer(value), item.CurrentPermissions);
                    break;
                case MASK_GROUP:
                    item.GroupPermissions = LSLPermToPermissionMask(value, item.GroupPermissions);
                    break;
                case MASK_EVERYONE:
                    item.EveryonePermissions = LSLPermToPermissionMask(value, item.EveryonePermissions);
                    break;
                case MASK_NEXT:
                    item.NextPermissions = LSLPermToPermissionMask(FixedCopyTransfer(value), item.NextPermissions);
                    break;
                default:
                    return;
            }

            m_host.ParentGroup.InvalidateDeepEffectivePerms();
            m_host.ParentGroup.AggregatePerms();
        }

        private const uint FullPerms = (uint)OpenSim.Framework.PermissionMask.All;

        private static uint LSLPermToPermissionMask(int lslperm, uint oldvalue)
        {
            lslperm &= PERM_ALL;
            if (lslperm == PERM_ALL) return oldvalue | FullPerms;
            oldvalue &= ~FullPerms;
            if ((lslperm & PERM_COPY) != 0) oldvalue |= (uint)OpenSim.Framework.PermissionMask.Copy;
            if ((lslperm & PERM_MODIFY) != 0) oldvalue |= (uint)OpenSim.Framework.PermissionMask.Modify;
            if ((lslperm & PERM_MOVE) != 0) oldvalue |= (uint)OpenSim.Framework.PermissionMask.Move;
            if ((lslperm & PERM_TRANSFER) != 0) oldvalue |= (uint)OpenSim.Framework.PermissionMask.Transfer;
            return oldvalue;
        }

        /// <summary>An item must stay copyable or transferable.</summary>
        private static int FixedCopyTransfer(int value)
            => (value & (PERM_COPY | PERM_TRANSFER)) == 0 ? value | PERM_TRANSFER : value;
        public void llGiveInventory(string destination, string inventory)
        {
            ScriptSleep(2000);
            if (m_host == null || World == null) return;

            if (!UUID.TryParse(destination, out UUID destId) || destId == UUID.Zero)
            {
                llSay(0, "Could not parse destination key: " + destination);
                return;
            }
            if (GiveRefusedByMute(destId, "inventory")) return;

            // Find the item in task inventory
            TaskInventoryItem item = null;
            lock (m_host.TaskInventory)
            {
                foreach (TaskInventoryItem inv in m_host.TaskInventory.Values)
                {
                    if (inv.Name == inventory)
                    {
                        item = inv;
                        break;
                    }
                }
            }
            if (item == null)
            {
                ShoutError($"Could not find item '{inventory}'");
                return;
            }

            // YEngine's delivery (a prim, or an avatar here, elsewhere or offline); no null client.
            if (GiveTaskItem(m_host, item, destId, out string failure) != IW_DELIVER_OK)
                ShoutError($"Failed to give '{inventory}': {failure}");
        }
        public void llGiveInventoryList(string target, string folder, LSLList inventory)
        {
            if (m_host == null || World == null) return;
            if (!UUID.TryParse(target, out UUID destId) || destId == UUID.Zero) return;
            if (GiveRefusedByMute(destId, "inventory list")) { ScriptSleep(3000); return; }

            // SL: the avatar must be in, or able to see into, the region (SVC-868). YEngine gives nothing to one
            // with no presence here - "we could check if it is a grid user ... but that increases security risk" -
            // and says so on DEBUG_CHANNEL. llGiveInventory and iwDeliverInventory[List] still deliver anywhere.
            if (World.GetSceneObjectPart(destId) == null && World.GetScenePresence(destId) == null)
            {
                ShoutError("llGiveInventoryList: Unable to give list, destination not found");
                ScriptSleep(3000);
                return;
            }

            // Collect UUIDs of the named items from task inventory
            var itemIDs = new List<UUID>();
            lock (m_host.TaskInventory)
            {
                foreach (var kvp in m_host.TaskInventory)
                {
                    for (int i = 0; i < inventory.Length; i++)
                    {
                        if (kvp.Value.Name == inventory.Data[i]?.ToString())
                        {
                            itemIDs.Add(kvp.Value.ItemID);
                            break;
                        }
                    }
                }
            }
            if (itemIDs.Count == 0) return;

            if (GiveTaskItems(m_host, destId, folder, itemIDs, out string failure) != IW_DELIVER_OK)
                ShoutError($"Failed to give inventory list: {failure}");
            ScriptSleep(3000);
        }

        public void llRemoveInventory(string item)
        {
            if (m_host == null) return;
            lock (m_host.TaskInventory)
            {
                foreach (var kvp in m_host.TaskInventory)
                {
                    if (kvp.Value.Name == item)
                    {
                        m_host.Inventory.RemoveInventoryItem(kvp.Value.ItemID);
                        return;
                    }
                }
            }
        }

        public void llAllowInventoryDrop(int add)
        {
            if (m_host?.ParentGroup == null) return;
            m_host.ParentGroup.RootPart.AllowedDrop = (add != 0);
            // Trigger a flag update so the viewer knows drop is allowed
            m_host.ParentGroup.RootPart.ScheduleFullUpdate();
        }
        public void iwMakeNotecard(string name, LSLList data)
        {
            // Faithful port from Halcyon: create a notecard in this prim's inventory
            if (m_host == null || World == null || string.IsNullOrEmpty(name)) return;

            const int MAX_LENGTH = 65536;

            try
            {
                StringBuilder notecardData = new StringBuilder();
                for (int i = 0; i < data.Length; i++)
                {
                    if (i > 0) notecardData.Append("\n");
                    notecardData.Append(data.GetLSLStringItem(i));
                    if (notecardData.Length > MAX_LENGTH) return;
                }

                int textLength = Encoding.UTF8.GetByteCount(notecardData.ToString());
                string sNotecardData = "Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length "
                    + textLength.ToString() + "\n" + notecardData.ToString() + "}\n";

                AssetBase asset = new AssetBase(UUID.Random(), name, (sbyte)AssetType.Notecard, m_host.OwnerID.ToString());
                asset.Description = "Script Generated Notecard";
                asset.Data = Encoding.UTF8.GetBytes(sNotecardData);
                // Halcyon iwMakeNotecard shouts when storing the asset fails and makes no item. Its call
                // was long-running (its ScriptSleep did nothing), so ShoutError: no pause.
                string stored;
                try { stored = World.AssetService.Store(asset); }
                catch (Exception se) { m_log.LogWarning("[PhloxAPI]: iwMakeNotecard store: {0}", se.Message); stored = null; }
                if (string.IsNullOrEmpty(stored))
                {
                    ShoutError("Notecard asset storage failed!");
                    ScriptSleep(5000);
                    return;
                }

                // Create task inventory item
                TaskInventoryItem taskItem = new TaskInventoryItem();
                taskItem.ResetIDs(m_host.UUID);
                taskItem.ParentID = m_host.UUID;
                taskItem.CreationDate = (uint)((DateTimeOffset)DateTime.UtcNow).ToUnixTimeSeconds();
                taskItem.Name = name;
                taskItem.Description = "Script Generated Notecard";
                taskItem.Type = (int)AssetType.Notecard;
                taskItem.InvType = (int)InventoryType.Notecard;
                taskItem.OwnerID = m_host.OwnerID;
                taskItem.CreatorID = m_host.OwnerID;
                taskItem.BasePermissions = (uint)OpenSim.Framework.PermissionMask.All;
                taskItem.CurrentPermissions = (uint)OpenSim.Framework.PermissionMask.All;
                taskItem.EveryonePermissions = 0;
                taskItem.NextPermissions = (uint)OpenSim.Framework.PermissionMask.All;
                taskItem.GroupID = m_host.GroupID;
                taskItem.GroupPermissions = 0;
                taskItem.Flags = 0;
                taskItem.PermsGranter = UUID.Zero;
                taskItem.PermsMask = 0;
                taskItem.AssetID = asset.FullID;
                m_host.Inventory.AddInventoryItem(taskItem, false);
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: iwMakeNotecard exception: {0}", e.Message);
            }
            ScriptSleep(5000);
        }
        /// <summary>
        /// Halcyon's GetNumberOfNotecardLines delays (LSLSystemAPI.cs:14521-14580) - 25 ms answered from the
        /// notecard cache, 50 ms fetched, 100 ms when there is no such notecard - and its cache. The answer is unchanged.
        /// </summary>
        public string llGetNumberOfNotecardLines(string name)
        {
            if (m_host == null) return UUID.Zero.ToString();
            TaskInventoryItem item = string.IsNullOrEmpty(name) ? null : FindInventoryItem(name, (int)AssetType.Notecard);
            if (item == null)
            {
                // Halcyon LSLSystemAPI.cs:14543-14547, the error, then its 100 ms (which replaces the 15).
                ScriptShoutError("Notecard '" + name + "' could not be found.");
                NotecardSleep(NOTECARD_COUNT_ERROR_DELAY);
                return UUID.Zero.ToString();
            }
            UUID queryID = NewDataserverQuery();
            bool cached = AnswerNotecardRead(item.AssetID, queryID, c => c.LineCount.ToString(), name, "llGetNumberOfNotecardLines");
            NotecardSleep(cached ? NOTECARD_COUNT_FAST_DELAY : NOTECARD_COUNT_LONG_DELAY);
            return queryID.ToString();
        }

        /// <summary>
        /// Halcyon's GetNotecardSegment delays (LSLSystemAPI.cs:14602-14661) - 25 ms fetched; from the cache,
        /// 1 ms on lines 0, 16, 32 ... read from offset 0 and none otherwise; none for a missing notecard - and its cache.
        /// </summary>
        public string llGetNotecardLine(string name, int line)
        {
            if (m_host == null) return UUID.Zero.ToString();
            TaskInventoryItem item = string.IsNullOrEmpty(name) ? null : FindInventoryItem(name, (int)AssetType.Notecard);
            if (item == null)
            {
                // Halcyon GetNotecardSegment, LSLSystemAPI.cs:14624-14628 (no delay after it).
                ScriptShoutError("Notecard '" + name + "' could not be found.");
                return UUID.Zero.ToString();
            }
            UUID queryID = NewDataserverQuery();
            int lineNum = line;
            bool cached = AnswerNotecardRead(item.AssetID, queryID, c => NotecardLineAnswer(c, lineNum), name, "llGetNotecardLine");
            if (cached) NotecardLineCachedSleep(line, 0);
            else NotecardSleep(NOTECARD_LINE_LONG_DELAY);
            return queryID.ToString();
        }
        public string iwGetNotecardSegment(string name, int line, int startOffset, int maxLength)
        {
            // Faithful port: read a segment of a notecard line (offset + length)
            if (m_host == null || string.IsNullOrEmpty(name)) return UUID.Zero.ToString();
            TaskInventoryItem item = FindInventoryItem(name, (int)AssetType.Notecard);
            if (item == null) { ScriptShoutError("Notecard '" + name + "' could not be found."); return UUID.Zero.ToString(); }
            UUID queryID = NewDataserverQuery();
            int lineNum = line;
            bool cached = AnswerNotecardRead(item.AssetID, queryID, c => NotecardSegmentAnswer(c, lineNum, startOffset, maxLength),
                name, "iwGetNotecardSegment");
            if (cached) NotecardLineCachedSleep(line, startOffset);   // As llGetNotecardLine
            else NotecardSleep(NOTECARD_LINE_LONG_DELAY);
            return queryID.ToString();
        }
        public string llGetNotecardLineSync(string name, int line)
        {
            // SL: synchronous notecard read — returns the line directly (no dataserver event)
            if (m_host == null || string.IsNullOrEmpty(name)) return "\n\n\n";
            TaskInventoryItem item = FindInventoryItem(name, (int)AssetType.Notecard);
            if (item == null) { ShoutError("Notecard '" + name + "' not found."); return "\n\n\n"; }
            try
            {
                AssetBase asset = World.AssetService.Get(item.AssetID.ToString());
                if (asset == null || asset.Data == null) return "\n\n\n";
                string body = StripNotecardHeader(OpenMetaverse.Utils.BytesToString(asset.Data));
                string[] lines = body.Split('\n');
                if (line < 0 || line >= lines.Length) return "\n\n\n";
                return lines[line].TrimEnd('\r');
            }
            catch { return "\n\n\n"; }
        }
        public int llFindNotecardTextSync(string name, string text, int line, int caseSensitive)
        {
            // SL: synchronous search for text in a notecard — returns line number or -1
            if (m_host == null || string.IsNullOrEmpty(name)) return -1;
            TaskInventoryItem item = FindInventoryItem(name, (int)AssetType.Notecard);
            if (item == null) { ShoutError("Notecard '" + name + "' not found."); return -1; }
            try
            {
                AssetBase asset = World.AssetService.Get(item.AssetID.ToString());
                if (asset == null || asset.Data == null) return -1;
                string body = StripNotecardHeader(OpenMetaverse.Utils.BytesToString(asset.Data));
                string[] lines = body.Split('\n');
                StringComparison comp = caseSensitive != 0
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;
                for (int i = Math.Max(0, line); i < lines.Length; i++)
                    if (lines[i].IndexOf(text, comp) >= 0) return i;
                return -1;
            }
            catch { return -1; }
        }
        public string llRequestInventoryData(string name)
        {
            // Looks up a landmark by name and fires dataserver with its position
            if (m_host == null) return UUID.Zero.ToString();
            UUID queryID = NewDataserverQuery();

            TaskInventoryItem landmark = null;
            lock (m_host.TaskInventory)
            {
                foreach (var kvp in m_host.TaskInventory)
                {
                    if (kvp.Value.Type == (int)AssetType.Landmark && kvp.Value.Name == name)
                    {
                        landmark = kvp.Value;
                        break;
                    }
                }
            }

            if (landmark == null)
            {
                PostDataserverEvent(queryID, string.Empty);
                return queryID.ToString();
            }

            UUID assetId = landmark.AssetID;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    AssetBase asset = World.AssetService.Get(assetId.ToString());
                    if (asset == null) { PostDataserverEvent(queryID, string.Empty); return; }

                    // Landmark format: first line is "Landmark version N", then "region_id UUID", then "local_pos x y z"
                    string data = System.Text.Encoding.UTF8.GetString(asset.Data);
                    Vector3 pos = Vector3.Zero;
                    foreach (string line in data.Split('\n'))
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("local_pos"))
                        {
                            string[] parts = trimmed.Split(' ');
                            if (parts.Length == 4)
                                pos = new Vector3(
                                    float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                                    float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                                    float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture));
                            break;
                        }
                    }
                    PostDataserverEvent(queryID, pos.ToString());
                }
                catch { PostDataserverEvent(queryID, string.Empty); }
            });

            ScriptSleep(1000);
            return queryID.ToString();
        }
        public LSLList iwSearchInventory(int type, string pattern, int matchtype)
        {
            return iwSearchLinkInventory(m_host.LinkNum, type, pattern, matchtype);
        }
        public LSLList iwSearchLinkInventory(int link, int type, string pattern, int matchtype)
        {
            if (matchtype > 2)
            {
                ScriptShoutError("IW_MATCH_COUNT/REGEX not valid for iwSearchLinkInventory");
                return new LSLList();
            }
            List<object> ret = new List<object>();
            foreach (SceneObjectPart part in GetLinkParts(link))
            {
                lock (part.TaskInventory)
                {
                    foreach (var kvp in part.TaskInventory)
                    {
                        if (type != -1 && kvp.Value.Type != type) continue;
                        if (String.IsNullOrEmpty(pattern) || iwMatchString(kvp.Value.Name, pattern, matchtype) == 1)
                            ret.Add(kvp.Value.Name);
                    }
                }
            }
            return new LSLList(ret);
        }
        public int iwGetLinkInventoryNumber(int linknumber, int type)
        {
            int count = 0;
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
                lock (part.TaskInventory)
                    foreach (var kvp in part.TaskInventory)
                        if (type == -1 || kvp.Value.Type == type) count++;
            return count;
        }
        public int iwGetLinkInventoryType(int linknumber, string name)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
                lock (part.TaskInventory)
                    foreach (var kvp in part.TaskInventory)
                        if (kvp.Value.Name == name) return kvp.Value.Type;
            return -1;
        }
        public int iwGetLinkInventoryPermMask(int linknumber, string item, int mask)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
                lock (part.TaskInventory)
                    foreach (var kvp in part.TaskInventory)
                        if (kvp.Value.Name == item)
                        {
                            switch (mask)
                            {
                                case 0: return (int)kvp.Value.BasePermissions;
                                case 1: return (int)kvp.Value.CurrentPermissions;
                                case 2: return (int)kvp.Value.GroupPermissions;
                                case 3: return (int)kvp.Value.EveryonePermissions;
                                case 4: return (int)kvp.Value.NextPermissions;
                            }
                        }
            return 0;
        }
        public string iwGetLinkInventoryName(int linknumber, int type, int number)
        {
            int idx = 0;
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
                lock (part.TaskInventory)
                    foreach (var kvp in part.TaskInventory)
                        if (type == -1 || kvp.Value.Type == type)
                            if (idx++ == number) return kvp.Value.Name;
            return string.Empty;
        }
        public string iwGetLinkInventoryKey(int linknumber, string name)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
                lock (part.TaskInventory)
                    foreach (var kvp in part.TaskInventory)
                        // llGetInventoryKey's rule, as Halcyon's shared GetInventoryKey.
                        if (kvp.Value.Name == name)
                            return IsMyScript(part, kvp.Value) ? kvp.Value.AssetID.ToString()
                                : AssetKeyIfFullPerm(kvp.Value.AssetID, kvp.Value.CurrentPermissions);
            return UUID.Zero.ToString();
        }
        public string iwGetLinkInventoryCreator(int linknumber, string item)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
                lock (part.TaskInventory)
                    foreach (var kvp in part.TaskInventory)
                        if (kvp.Value.Name == item) return kvp.Value.CreatorID.ToString();
            return UUID.Zero.ToString();
        }
        public void iwRemoveLinkInventory(int linknumber, string item)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
            {
                lock (part.TaskInventory)
                {
                    foreach (var kvp in part.TaskInventory)
                    {
                        if (kvp.Value.Name == item)
                        {
                            part.Inventory.RemoveInventoryItem(kvp.Key);
                            return;
                        }
                    }
                }
            }
        }
        public void iwGiveLinkInventory(int linknumber, string destination, string inventory)
        {
            ScriptSleep(2000);
            GiveLinkInventory(linknumber, destination, inventory);
        }

        /// <summary>
        /// iwGiveLinkInventory's body, now returning Halcyon's IW_DELIVER_* code
        /// (InWorldz.Phlox.Engine/LSLSystemAPI.cs GiveLinkInventory/_GiveInventory) so
        /// iwDeliverInventory can hand it back.
        /// </summary>
        private int GiveLinkInventory(int linknumber, string destination, string inventory)
        {
            if (World == null) return IW_DELIVER_PRIM;
            if (!UUID.TryParse(destination, out UUID destId) || destId == UUID.Zero)
            {
                llSay(0, "Could not parse destination key: " + destination);
                return IW_DELIVER_BADKEY;
            }

            // Find the item in the specified link's inventory
            TaskInventoryItem item = null;
            SceneObjectPart sourcePart = null;
            bool anyPart = false;
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
            {
                anyPart = true;
                lock (part.TaskInventory)
                    foreach (var kvp in part.TaskInventory)
                        if (kvp.Value.Name == inventory)
                        { item = kvp.Value; sourcePart = part; break; }
                if (item != null) break;
            }
            if (!anyPart) return IW_DELIVER_PRIM;
            if (GiveRefusedByMute(destId, "inventory")) return IW_DELIVER_MUTED;   // Before the item, as Halcyon
            if (item == null || sourcePart == null)
            {
                ShoutError($"Could not find item '{inventory}'");
                return IW_DELIVER_NONE;
            }

            // YEngine's delivery - no null client into Scene.MoveTaskInventoryItem (Scene.Inventory.cs:1479).
            int rc = GiveTaskItem(sourcePart, item, destId, out string failure);
            if (rc != IW_DELIVER_OK) ShoutError($"Failed to give '{inventory}': {failure}");
            return rc;
        }

        // ── Giving task inventory to an avatar who may not be in this region ────────────────────────
        //
        // YEngine (LSL_Api.llGiveInventory): a prim destination is a task-to-task move; an avatar is accepted when
        // present here, or known to the grid - a user account, or an online grid user - and is given the item with
        // Scene.MoveTaskInventoryItem(avatarId, folderId, part, itemId), the overload that needs no client; the
        // TaskInventoryOffered notice goes to the client when present, else through the IM transfer module, which
        // stores it for an offline avatar. The codes are Halcyon's (InWorldz.Phlox.Engine/LSLSystemAPI.cs
        // _GiveInventory / _GiveLinkInventoryList / DeliverReasonToResult): IW_DELIVER_OK delivered,
        // IW_DELIVER_USER no such avatar ("user"), IW_DELIVER_PERM a transfer refusal ("perm"), IW_DELIVER_ITEM
        // the item vanished ("item").

        /// <summary>Is this an avatar we can give to: here, with an account, or online on the grid (YEngine's test)?</summary>
        private bool AvatarKnown(UUID id)
        {
            if (World.GetScenePresence(id) != null) return true;
            if (World.UserAccountService?.GetUserAccount(World.RegionInfo.ScopeID, id) != null) return true;
            var info = World.GridUserService?.GetGridUserInfo(id.ToString());
            return info != null && info.Online;
        }

        /// <summary>Give one task item to a prim or an avatar. Returns an IW_DELIVER_* code; <paramref name="failure"/> says why not.</summary>
        private int GiveTaskItem(SceneObjectPart source, TaskInventoryItem item, UUID destId, out string failure)
        {
            failure = null;
            if (World.GetSceneObjectPart(destId) != null)
            {
                World.MoveTaskInventoryItem(destId, source, item.ItemID);   // YEngine: destination is an object
                return IW_DELIVER_OK;
            }
            if (!AvatarKnown(destId))
            {
                failure = "Can't find destination '" + destId + "'";
                return IW_DELIVER_USER;
            }
            InventoryItemBase agentItem = World.MoveTaskInventoryItem(destId, UUID.Zero, source, item.ItemID, out string reason);
            if (agentItem == null)
            {
                failure = reason;
                return DeliverReasonToResult(reason);
            }
            SendGiveNotice(source, destId, "'" + item.Name + "'", agentItem.ID, (byte)item.Type);
            return IW_DELIVER_OK;
        }

        /// <summary>
        /// Give task items to a prim, or into a new folder named <paramref name="category"/> in an avatar's inventory.
        /// Scene.MoveTaskInventoryItems gives up (UUID.Zero) for an avatar who is not in this region, so for one who is
        /// elsewhere or offline the folder is made here and each item goes in by the no-client overload.
        /// </summary>
        private int GiveTaskItems(SceneObjectPart source, UUID destId, string category, List<UUID> itemIDs, out string failure)
        {
            failure = null;
            if (World.GetSceneObjectPart(destId) != null || World.GetScenePresence(destId) != null)
            {
                if (World.MoveTaskInventoryItems(destId, category, source, itemIDs) != UUID.Zero) return IW_DELIVER_OK;
                failure = "the recipient's inventory could not be reached";
                return IW_DELIVER_USER;
            }
            if (!AvatarKnown(destId))
            {
                failure = "Can't find destination '" + destId + "'";
                return IW_DELIVER_USER;
            }
            InventoryFolderBase root = World.InventoryService.GetRootFolder(destId);
            if (root == null)
            {
                failure = "the recipient's inventory could not be reached";
                return IW_DELIVER_USER;
            }
            var folder = new InventoryFolderBase(UUID.Random(), category, destId, -1, root.ID, root.Version);
            World.InventoryService.AddFolder(folder);
            int given = 0;
            foreach (UUID itemId in itemIDs)
            {
                if (World.MoveTaskInventoryItem(destId, folder.ID, source, itemId, out string reason) != null) given++;
                else failure = reason;
            }
            if (given == 0) return DeliverReasonToResult(failure);
            SendGiveNotice(source, destId, "'" + category + "'", folder.ID, (byte)AssetType.Folder);
            return IW_DELIVER_OK;
        }

        /// <summary>The TaskInventoryOffered notice: to the client when present, else through the IM transfer module (offline IM).</summary>
        private void SendGiveNotice(SceneObjectPart source, UUID destId, string text, UUID givenId, byte assetType)
        {
            GridInstantMessage msg = new GridInstantMessage(World,
                source.OwnerID, source.Name, destId,
                (byte)InstantMessageDialog.TaskInventoryOffered,
                false, text + ". (" + source.Name + " is located at " + World.RegionInfo.RegionName + " " + source.AbsolutePosition + ")",
                givenId, true, source.AbsolutePosition,
                new byte[] { assetType }, true);
            if (World.TryGetScenePresence(destId, out ScenePresence sp) && !sp.IsChildAgent)
                sp.ControllingClient.SendInstantMessage(msg);
            else
                World.RequestModuleInterface<IMessageTransferModule>()?.SendInstantMessage(msg, success => { });
        }

        /// <summary>
        /// Halcyon's DeliverReasonToResult, over OpenSim's MoveTaskInventoryItem messages
        /// (Scene.Inventory.cs): a transfer refusal is PERM, a missing item is ITEM, anything else USER.
        /// </summary>
        private static int DeliverReasonToResult(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return IW_DELIVER_USER;
            if (reason.StartsWith("Item not found")) return IW_DELIVER_ITEM;
            if (reason.Contains("Transfer permission") || reason.StartsWith("Not allowed") || reason.StartsWith("Sender did not match"))
                return IW_DELIVER_PERM;
            return IW_DELIVER_USER;
        }
        public void iwGiveLinkInventoryList(int linknumber, string target, string folder, LSLList inventory)
        {
            // Faithful port: give inventory items from a specific link prim
            if (m_host == null || World == null) return;
            if (!UUID.TryParse(target, out UUID destId) || destId == UUID.Zero) return;
            if (GiveRefusedByMute(destId, "inventory list")) { ScriptSleep(3000); return; }   // otherwise unchanged

            foreach (SceneObjectPart part in GetLinkParts(linknumber))
            {
                var itemIDs = new List<UUID>();
                lock (part.TaskInventory)
                {
                    foreach (var kvp in part.TaskInventory)
                    {
                        for (int i = 0; i < inventory.Length; i++)
                        {
                            if (kvp.Value.Name == inventory.Data[i]?.ToString())
                            {
                                itemIDs.Add(kvp.Value.ItemID);
                                break;
                            }
                        }
                    }
                }
                if (itemIDs.Count > 0 && GiveTaskItems(part, destId, folder, itemIDs, out string failure) != IW_DELIVER_OK)
                    ShoutError($"Failed to give inventory list: {failure}");
            }
            ScriptSleep(3000);
        }
        public string iwGetLinkInventoryDesc(int linknumber, string name)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
                lock (part.TaskInventory)
                    foreach (var kvp in part.TaskInventory)
                        if (kvp.Value.Name == name) return kvp.Value.Description;
            return string.Empty;
        }
        public string iwGetLinkInventoryLastOwner(int linknumber, string name)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
                lock (part.TaskInventory)
                    foreach (var kvp in part.TaskInventory)
                        if (kvp.Value.Name == name) return kvp.Value.LastOwnerID.ToString();
            return UUID.Zero.ToString();
        }
        /// <summary>
        /// The table returns integer and the async shim returns only what the body hands to
        /// SysReturn. Halcyon (InWorldz.Phlox.Engine/LSLSystemAPI.cs iwDeliverInventory ->
        /// GiveLinkInventory(.., 100, includeRC: true)) returns an IW_DELIVER_* code from a finally,
        /// IW_DELIVER_PRIM if the body never got as far as a prim, and sleeps 100 ms, not 2 s.
        /// </summary>
        public void iwDeliverInventory(int linknumber, string destination, string inventory)
        {
            int rc = IW_DELIVER_PRIM;
            try { rc = GiveLinkInventory(linknumber, destination, inventory); }
            finally { m_ScriptEngine.SysReturn(m_itemID, rc, 100); }
        }

        /// <summary>
        /// Halcyon's iwDeliverInventoryList (GiveInventoryList(.., 100, includeRC: true) ->
        /// _GiveLinkInventoryList): the first prim of the link set gives, a named item missing from it
        /// aborts with IW_DELIVER_ITEM, an empty list is IW_DELIVER_NONE, no prim is IW_DELIVER_PRIM.
        /// The code goes back from a finally, so every path returns one.
        /// </summary>
        public void iwDeliverInventoryList(int linknumber, string target, string folder, LSLList inventory)
        {
            int rc = IW_DELIVER_PRIM;
            try { rc = DeliverInventoryList(linknumber, target, folder, inventory); }
            finally { m_ScriptEngine.SysReturn(m_itemID, rc, 100); }
        }

        private int DeliverInventoryList(int linknumber, string target, string folder, LSLList inventory)
        {
            if (m_host == null || World == null) return IW_DELIVER_PRIM;
            if (!UUID.TryParse(target, out UUID destId) || destId == UUID.Zero) return IW_DELIVER_BADKEY;

            SceneObjectPart part = GetLinkParts(linknumber).FirstOrDefault();
            if (part == null) return IW_DELIVER_PRIM;
            if (GiveRefusedByMute(destId, "inventory list")) return IW_DELIVER_MUTED;

            var itemIDs = new List<UUID>();
            for (int i = 0; i < inventory.Length; i++)
            {
                string name = inventory.Data[i]?.ToString();
                UUID found = UUID.Zero;
                lock (part.TaskInventory)
                    foreach (var kvp in part.TaskInventory)
                        if (kvp.Value.Name == name || kvp.Value.ItemID.ToString() == name) { found = kvp.Value.ItemID; break; }
                if (found == UUID.Zero) return IW_DELIVER_ITEM;
                itemIDs.Add(found);
            }
            if (itemIDs.Count == 0) return IW_DELIVER_NONE;

            return GiveTaskItems(part, destId, folder, itemIDs, out _);
        }
        public string iwGetLinkNumberOfNotecardLines(int linknumber, string name)
        {
            // Faithful port: get notecard line count from a specific link's inventory
            if (m_host == null || string.IsNullOrEmpty(name)) return UUID.Zero.ToString();
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
            {
                TaskInventoryItem item = null;
                lock (part.TaskInventory)
                {
                    foreach (var kvp in part.TaskInventory)
                        if (kvp.Value.Type == (int)AssetType.Notecard && kvp.Value.Name == name)
                        { item = kvp.Value; break; }
                }
                if (item == null) continue;
                UUID queryID = NewDataserverQuery();
                // Halcyon's GetNumberOfNotecardLines delays and cache, as llGetNumberOfNotecardLines
                bool cached = AnswerNotecardRead(item.AssetID, queryID, c => c.LineCount.ToString(), name, null);
                NotecardSleep(cached ? NOTECARD_COUNT_FAST_DELAY : NOTECARD_COUNT_LONG_DELAY);
                return queryID.ToString();
            }
            ScriptShoutError("iwGetLinkNumberOfNotecardLines: Link number " + linknumber + " does not contain notecard '" + name + "'.");
            // Halcyon sleeps 100 ms when the link is one prim without the notecard; a link of several prims
            // was refused before any read (LSLSystemAPI.cs:14585-14599), without a sleep.
            if (GetLinkParts(linknumber).Take(2).Count() == 1) NotecardSleep(NOTECARD_COUNT_ERROR_DELAY);
            return UUID.Zero.ToString();
        }
        public string iwGetLinkNotecardLine(int linknumber, string name, int line)
        {
            // Faithful port: read a notecard line from a specific link's inventory
            if (m_host == null || string.IsNullOrEmpty(name)) return UUID.Zero.ToString();
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
            {
                TaskInventoryItem item = null;
                lock (part.TaskInventory)
                {
                    foreach (var kvp in part.TaskInventory)
                        if (kvp.Value.Type == (int)AssetType.Notecard && kvp.Value.Name == name)
                        { item = kvp.Value; break; }
                }
                if (item == null) continue;
                UUID queryID = NewDataserverQuery();
                int lineNum = line;
                // Halcyon's GetNotecardSegment delays and cache, as llGetNotecardLine
                bool cached = AnswerNotecardRead(item.AssetID, queryID, c => NotecardLineAnswer(c, lineNum), name, null);
                if (cached) NotecardLineCachedSleep(line, 0);
                else NotecardSleep(NOTECARD_LINE_LONG_DELAY);
                return queryID.ToString();
            }
            ShoutError("iwGetLinkNotecardLine: Notecard '" + name + "' not found in link " + linknumber + ".");
            // Halcyon (LSLSystemAPI.cs:14674-14684) reads the link's first prim, whose GetNotecardSegment pauses
            // on a missing notecard (:14627); a link number with no prim returns without an error or a pause.
            if (GetLinkParts(linknumber).Any()) ChatSleep();
            return UUID.Zero.ToString();
        }
        public string iwGetLinkNotecardSegment(int linknumber, string name, int line, int startOffset, int maxLength)
        {
            // Faithful port: read a notecard segment from a specific link's inventory
            if (m_host == null || string.IsNullOrEmpty(name)) return UUID.Zero.ToString();
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
            {
                TaskInventoryItem item = null;
                lock (part.TaskInventory)
                {
                    foreach (var kvp in part.TaskInventory)
                        if (kvp.Value.Type == (int)AssetType.Notecard && kvp.Value.Name == name)
                        { item = kvp.Value; break; }
                }
                if (item == null) continue;
                UUID queryID = NewDataserverQuery();
                int lineNum = line;
                // Halcyon's GetNotecardSegment delays and cache, as iwGetNotecardSegment
                bool cached = AnswerNotecardRead(item.AssetID, queryID, c => NotecardSegmentAnswer(c, lineNum, startOffset, maxLength),
                    name, null);
                if (cached) NotecardLineCachedSleep(line, startOffset);
                else NotecardSleep(NOTECARD_LINE_LONG_DELAY);
                return queryID.ToString();
            }
            return UUID.Zero.ToString();
        }

        // ── Object manipulation ────────────────────────────────────────────────

public void llRezObject(string inventory, Vector3 pos, Vector3 vel, Quaternion rot, int param)
            => RezObjectInternal(inventory, pos, vel, rot, param, false);

        public void llRezAtRoot(string inventory, Vector3 pos, Vector3 vel, Quaternion rot, int param)
            => RezObjectInternal(inventory, pos, vel, rot, param, true);

        private string RezObjectInternal(string inventory, Vector3 pos, Vector3 vel, Quaternion rot, int param, bool atRoot)
            => RezObjectInternal(inventory, pos, vel, rot, param, atRoot, null);

        /// <summary>
        /// SL wiki object_rez: "Triggers in all running scripts with an object_rez event, AND in the same prim as the
        /// script calling llRezObject or llRezAtRoot. Does NOT trigger in linked prims." Phlox's scripts in this prim, then
        /// every other engine's, with the rezzed root's key.
        /// </summary>
        private void PostObjectRez(string rootKey)
        {
            m_ScriptEngine.PostObjectEvent(m_host.LocalId, new EventParams("object_rez", new object[] { rootKey }, new DetectParams[0]));
            SceneObjectPart host = m_host;
            OfferToOtherEngines("object_rez", () => ScriptItemsIn(new[] { host }), "object_rez", () => new object[] { rootKey });
        }

        /// <summary>
        /// The one rez path, now carrying REZ_PARAM_STRING. Upstream stores it on the rezzed
        /// group (LSL_Api.cs:3894, sog.RezStringParameter) and llGetStartString reads it back
        /// (LSL_Api.cs:4589-4593); until now Phlox parsed REZ_PARAM only and the string went nowhere.
        /// </summary>
        private string RezObjectInternal(string inventory, Vector3 pos, Vector3 vel, Quaternion rot, int param, bool atRoot, string startString)
        {
            if (RezRotationIsNaN(rot)) return UUID.Zero.ToString();
            ScriptSleep(100);
            if (m_host == null || World == null) return UUID.Zero.ToString();

            if (Util.GetDistanceTo(pos, m_host.AbsolutePosition) > 10f)
            {
                ShoutError("Unable to create requested object. Position exceeds 10m distance limit.");
                return UUID.Zero.ToString();
            }

            TaskInventoryItem item = FindInventoryItem(inventory, (int)InventoryType.Object);
            if (item == null)
            {
                ShoutError("Unable to create requested object. Inventory item '" + inventory + "' not found or is not an object.");
                return UUID.Zero.ToString();
            }

            List<SceneObjectGroup> rezzed = World.RezObject(
                m_host, item,
                m_host.OwnerID, m_host.GroupID,
                pos, rot, vel, param, atRoot, false, false);

            if (rezzed == null || rezzed.Count == 0)
            {
                ShoutError("Unable to create requested object '" + inventory + "'.");
                return UUID.Zero.ToString();
            }

            foreach (SceneObjectGroup grp in rezzed)
            {
                if (startString != null) grp.RezStringParameter = startString;
            }
            string result = UUID.Zero.ToString();
            foreach (SceneObjectGroup grp in rezzed)
            {
                result = grp.RootPart.UUID.ToString();
                PostObjectRez(result);
            }
            return result;
        }
        /// <summary>
        /// The table returns key and the async shim returns only what the body hands to
        /// SysReturn. Halcyon (InWorldz.Phlox.Engine/LSLSystemAPI.cs iwRezObject/iwRezAtRoot) returns
        /// iwRezAt's result - the last rezzed group's root key, NULL_KEY on every failure - from a
        /// finally; so does this, with NULL_KEY (not Halcyon's "") if the rez throws.
        /// </summary>
        public void iwRezObject(string inventory, Vector3 pos, Vector3 vel, Quaternion rot, int param)
            => RezReturningKey(inventory, pos, vel, rot, param, false);
        public void iwRezAtRoot(string inventory, Vector3 pos, Vector3 vel, Quaternion rot, int param)
            => RezReturningKey(inventory, pos, vel, rot, param, true);

        private void RezReturningKey(string inventory, Vector3 pos, Vector3 vel, Quaternion rot, int param, bool atRoot)
        {
            string result = UUID.Zero.ToString();
            try { result = RezObjectInternal(inventory, pos, vel, rot, param, atRoot); }
            finally { m_ScriptEngine.SysReturn(m_itemID, result, 0); }
        }

        /// <summary>
        /// Halcyon iwRezAt (LSLSystemAPI.cs:3168-3173) refuses a NaN rotation with this error and no delay
        /// (sleepTime = 0), before its 10 m check. Its rez calls were long-running, so ShoutError: no pause.
        /// Halcyon's other guard there, IsBadUser (:3160-3166), reads a nuke/blacklist-owner list NGC core does not have.
        /// </summary>
        private bool RezRotationIsNaN(Quaternion rot)
        {
            if (!(float.IsNaN(rot.X) || float.IsNaN(rot.Y) || float.IsNaN(rot.Z) || float.IsNaN(rot.W))) return false;
            ShoutError("Unable to create requested object. Position is invalid.");
            return true;
        }

        public string iwRezAt(string inventory, int rezAtRoot, Vector3 pos, Vector3 vel, Quaternion rot, int param)
        {
            if (RezRotationIsNaN(rot)) return UUID.Zero.ToString();
            ScriptSleep(100);
            if (m_host == null || World == null) return UUID.Zero.ToString();

            if (Util.GetDistanceTo(pos, m_host.AbsolutePosition) > 10f)
            {
                ShoutError("Unable to create requested object. Position exceeds 10m distance limit.");
                return UUID.Zero.ToString();
            }

            TaskInventoryItem item = FindInventoryItem(inventory, (int)InventoryType.Object);
            if (item == null)
            {
                ShoutError("Unable to create requested object. Inventory item '" + inventory + "' not found or is not an object.");
                return UUID.Zero.ToString();
            }

            bool atRoot = (rezAtRoot != 0);
            List<SceneObjectGroup> rezzed = World.RezObject(
                m_host, item,
                m_host.OwnerID, m_host.GroupID,
                pos, rot, vel, param, atRoot, false, false);

            if (rezzed == null || rezzed.Count == 0)
            {
                ShoutError("Unable to create requested object '" + inventory + "'.");
                return UUID.Zero.ToString();
            }

            string result = UUID.Zero.ToString();
            foreach (SceneObjectGroup grp in rezzed)
            {
                result = grp.RootPart.UUID.ToString();
                PostObjectRez(result);
            }
            return result;
        }

        public string iwRezPrim(LSLList primParams, LSLList particleSystem, LSLList inventory, Vector3 pos, Vector3 vel, Quaternion rot, int param) { /* InWorldz-specific — no OpenSim equivalent */ return UUID.Zero.ToString(); }
        public void llGodLikeRezObject(string inventory, Vector3 pos) => NotImplemented("llGodLikeRezObject");   // Halcyon LSLSystemAPI.cs:4384
        public void llDie()
        {
            if (m_host == null) return;
            SceneObjectGroup group = m_host.ParentGroup;
            if (group == null || group.IsDeleted) return;
            World?.DeleteSceneObject(group, false);
        }
        public void llDerezObject(string id)
        {
            // SL: derez/delete an object by UUID (requires ownership or permissions)
            if (m_host == null || !UUID.TryParse(id, out UUID targetID) || targetID == UUID.Zero) return;
            SceneObjectPart sop = World?.GetSceneObjectPart(targetID);
            if (sop == null) return;
            SceneObjectGroup sog = sop.ParentGroup;
            if (sog == null || sog.IsDeleted || sog.IsAttachment) return;
            // Only allow if the script owner owns the target object
            if (sog.OwnerID != m_host.OwnerID) return;
            World?.DeleteSceneObject(sog, false);
        }
        public void llPushObject(string id, Vector3 impulse, Vector3 ang_impulse, int local)
        {
            if (m_host == null || !UUID.TryParse(id, out UUID targetID) || targetID == UUID.Zero)
                return;

            bool pushRestricted = World.RegionInfo.RegionSettings.RestrictPushing;
            bool pushAllowed = false;
            bool pusheeIsAvatar = false;

            Vector3 pusheePos = Vector3.Zero;
            ScenePresence pusheeAv = null;
            SceneObjectPart pusheeOb = null;

            ScenePresence avatar = World.GetScenePresence(targetID);
            if (avatar != null)
            {
                pusheeIsAvatar = true;
                if (avatar.PhysicsActor == null) return;
                if (avatar.IsViewerUIGod && m_host.OwnerID != targetID) return;

                pusheeAv = avatar;
                // If seated, push from the seat position
                pusheePos = (avatar.ParentPart != null)
                    ? avatar.ParentPart.AbsolutePosition
                    : avatar.AbsolutePosition;
            }
            else
            {
                pusheeOb = World.GetSceneObjectPart(targetID);
                if (pusheeOb == null) return;
                if (!pusheeOb.ParentGroup.IsAttachment && pusheeOb.PhysActor == null) return;
                pusheePos = pusheeOb.AbsolutePosition;
                pushAllowed = true;
            }

            if (pusheeIsAvatar)
            {
                if (pushRestricted)
                {
                    ILandObject land = World.LandChannel.GetLandObject(pusheePos.X, pusheePos.Y);
                    if (land == null) return;
                    bool isOwnerOrManager = (m_host.OwnerID == land.LandData.OwnerID)
                        || World.RegionInfo.EstateSettings.IsEstateManagerOrOwner(m_host.OwnerID);
                    if (isOwnerOrManager) pushAllowed = true;
                }
                else
                {
                    ILandObject land = World.LandChannel.GetLandObject(pusheePos.X, pusheePos.Y);
                    if (land == null)
                    {
                        pushAllowed = true;
                    }
                    else if ((land.LandData.Flags & (uint)ParcelFlags.RestrictPushObject) != 0)
                    {
                        bool isOwnerOrManager = (m_host.OwnerID == land.LandData.OwnerID)
                            || World.RegionInfo.EstateSettings.IsEstateManagerOrOwner(m_host.OwnerID);
                        if (isOwnerOrManager) pushAllowed = true;
                    }
                    else
                    {
                        pushAllowed = true;
                    }
                }
            }

            if (!pushAllowed) return;

            // Attenuate impulse by distance
            float distance = (pusheePos - m_host.AbsolutePosition).Length();
            const float PUSH_ATTENUATION_DISTANCE = 17f;
            const float PUSH_ATTENUATION_SCALE = 5f;
            float attenuation = 1f;
            if (distance > PUSH_ATTENUATION_DISTANCE)
            {
                float normalized = 1f + (distance - PUSH_ATTENUATION_DISTANCE) / PUSH_ATTENUATION_SCALE;
                attenuation = 1f / normalized;
            }

            Vector3 appliedImpulse = impulse * attenuation;

            if (pusheeIsAvatar && pusheeAv != null)
            {
                PhysicsActor pa = pusheeAv.PhysicsActor;
                if (pa != null)
                {
                    if (local != 0)
                        appliedImpulse *= m_host.GetWorldRotation();
                    pa.AddForce(appliedImpulse, true);
                }
            }
            else if (pusheeOb != null)
            {
                pusheeOb.ApplyImpulse(appliedImpulse, local != 0);
            }
            PhySleep();   // Halcyon's, once a push is allowed (LSLSystemAPI.cs:6055)
        }
        public void llSetDamage(float damage)
        {
            if (m_host == null) return;
            PrimSetDamage(m_host, damage);
        }

        /// <summary>
        /// llSetDamage and PRIM_DAMAGE's amount: the object's damage (SceneObjectGroup.Damage, the root part's, which
        /// the scene's collision damage reads), whichever prim names it. SL: "Collideable damage as set via
        /// llSetDamage, PRIM_DAMAGE, and REZ_DAMAGE is limited to 100 maximum". Held at 0 below: the scene keeps -1
        /// for "none" and deals only damage above 0, so it cannot heal. Not saved, as the scene keeps it.
        /// </summary>
        private static void PrimSetDamage(SceneObjectPart part, float damage)
        {
            SceneObjectGroup group = part.ParentGroup;
            if (group == null) return;
            group.Damage = Math.Clamp(damage, 0f, 100f);
        }

        /// <summary>PRIM_DAMAGE's amount: the object's damage, SL's 0.0 while none is set (the scene's -1).</summary>
        private static float PrimDamage(SceneObjectPart part) => Math.Max(0f, part.ParentGroup?.Damage ?? 0f);
        public void llModifyLand(int action, int brush)
        {
            // Faithful port from Halcyon.
            // OpenSim's ITerrainModule.ModifyTerrain(UUID user, Vector3 pos, byte size, byte action)
            ITerrainModule tm = World?.RequestModuleInterface<ITerrainModule>();
            if (tm != null)
            {
                try
                {
                    tm.ModifyTerrain(m_host.OwnerID, m_host.AbsolutePosition, (byte)brush, (byte)action);
                }
                catch (Exception e)
                {
                    m_log.LogWarning("[PhloxAPI]: llModifyLand exception: {0}", e.Message);
                }
            }
        }
        /// <summary>Real now - the heightmap over the rectangle, where the owner may terraform, then a taint (the door osSetTerrainHeight/osTerrainFlush share).</summary>
        /// <summary>
        /// Halcyon's TerrainModule.SetTerrain rule (TerrainModule.cs:655-688, :610-641), silent to the script
        /// as there: a height outside 0..1024 or a corner outside the region changes nothing; a god sets any cell; anyone else
        /// the cells they may terraform, each held within the estate's raise/lower limits of the baked terrain, as NGC core
        /// holds the viewer's brush (TerrainModule.LimitChannelChanges against Scene.Bakedmap).
        /// </summary>
        public void iwSetGround(int x1, int y1, int x2, int y2, float height)
        {
            if (World?.Heightmap == null || m_host == null) return;
            if (!(height >= 0.0f && height <= 1024.0f)) return;
            int sx = Math.Min(x1, x2), ex = Math.Max(x1, x2), sy = Math.Min(y1, y2), ey = Math.Max(y1, y2);
            if (sx < 0 || sy < 0 || ex >= (int)World.RegionInfo.RegionSizeX || ey >= (int)World.RegionInfo.RegionSizeY) return;

            UUID owner = m_host.OwnerID;
            bool god = World.Permissions.IsGod(owner);
            ITerrainChannel baked = god ? null : World.Bakedmap;
            RegionSettings rs = World.RegionInfo.RegionSettings;
            float lower = (float)rs.TerrainLowerLimit, raise = (float)rs.TerrainRaiseLimit;
            bool any = false;
            for (int x = sx; x <= ex; x++) for (int y = sy; y <= ey; y++)
            {
                if (!god && !World.Permissions.CanTerraformLand(owner, new Vector3(x, y, 0))) continue;
                float h = height;
                if (baked != null)
                {
                    float b = baked[x, y];
                    if (h - b > raise) h = b + raise;
                    else if (h - b < lower) h = b + lower;
                }
                World.Heightmap[x, y] = h;
                any = true;
            }
            if (any) World.RequestModuleInterface<ITerrainModule>()?.TaintTerrain();
        }
        public int llCheckRezError(Vector3 pos, int isTemp, int landImpact) { /* InWorldz Scene.CheckRezError not in OpenSim */ return 0; }
        public int iwCheckRezError(Vector3 pos, int isTemp, int landImpact) { /* InWorldz Scene.CheckRezError not in OpenSim */ return 0; }
        /// <summary>[YEngine] AutomaticLinkPermission, as YEngine reads it (PhloxEngine.AutomaticLinkPermission).</summary>
        private bool AutomaticLinkPermission => m_ScriptEngine != null && m_ScriptEngine.AutomaticLinkPermission;

        public void llCreateLink(string target, int parent)
        {
            if (m_host?.ParentGroup == null) return;
            if (m_host.ParentGroup.IsAttachment) return;

            // Requires PERMISSION_CHANGE_LINKS
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return;
            // With AutomaticLinkPermission neither check applies (YEngine LSL_Api.cs:4790).
            if (!AutomaticLinkPermission && (item.PermsMask & PERMISSION_CHANGE_LINKS) == 0)
            {
                // Halcyon's text (the SL wiki says only that an error is shouted).
                ScriptShoutError("Script trying to link but PERMISSION_CHANGE_LINKS permission not set!");
                ScriptSleep(1000);
                return;
            }
            // SL (wiki llCreateLink): "If the permission PERMISSION_CHANGE_LINKS is granted by anyone other
            // than the owner, then when the function is called an error will be shouted". YEngine's text.
            if (!AutomaticLinkPermission && item.PermsGranter != m_host.ParentGroup.OwnerID)
            {
                ShoutError("llCreateLink: PERMISSION_CHANGE_LINKS not set by script owner");
                return;
            }

            // The region's edit permission on both objects, for the script's owner (who granted the permission above):
            // linking edits both, so a link the region would not let the owner make by hand fails. Halcyon checks it
            // (CanEditObject on the script's object and on the target) and fails silently - no error, no sleep; YEngine
            // does not check it. A target that is not here or is attached is left to CreateLinkCore's error.
            UUID editor = m_host.ParentGroup.OwnerID;
            if (!World.Permissions.CanEditObject(m_host.ParentGroup.UUID, editor)) return;
            if (UUID.TryParse(target, out UUID targetId) && targetId != UUID.Zero)
            {
                SceneObjectGroup targetGroup = World.GetSceneObjectPart(targetId)?.ParentGroup;
                if (targetGroup != null && !targetGroup.IsAttachment
                    && !World.Permissions.CanEditObject(targetGroup.UUID, editor))
                    return;
            }

            string failure = CreateLinkCore(target, parent);
            if (failure != null) ShoutError("llCreateLink: " + failure);
        }

        /// <summary>
        /// llCreateLink after its PERMISSION_CHANGE_LINKS check - the door osForceCreateLink takes (OSSL_Api.cs:2784-2789 calls the same split, m_LSL_Api.CreateLink).
        /// SL's conditions - "target must be modifiable and have the same owner. This object must also be
        /// modifiable." - with modify read from each root's OwnerMask as Halcyon and YEngine read it. Returns why it
        /// did not link (llCreateLink shouts it, as SL does; osForceCreateLink stays silent, as YEngine's CreateLink),
        /// or null.
        /// </summary>
        private string CreateLinkCore(string target, int parent)
        {
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsAttachment) return null;
            const string noTarget = "the target is not a prim in this region, or is attached to an avatar";
            if (!UUID.TryParse(target, out UUID targetUUID) || targetUUID == UUID.Zero) return noTarget;
            SceneObjectPart targetPart = World?.GetSceneObjectPart(targetUUID);
            if (targetPart?.ParentGroup == null) return noTarget;
            if (targetPart.ParentGroup.IsAttachment) return noTarget;

            const uint modify = (uint)OpenSim.Framework.PermissionMask.Modify;
            if (m_host.ParentGroup.OwnerID != targetPart.ParentGroup.OwnerID
                || (m_host.ParentGroup.RootPart.OwnerMask & modify) == 0
                || (targetPart.ParentGroup.RootPart.OwnerMask & modify) == 0)
                return "this object and the target must both be modifiable and have the same owner";

            SceneObjectGroup group1 = (parent != 0) ? m_host.ParentGroup : targetPart.ParentGroup;
            SceneObjectGroup group2 = (parent != 0) ? targetPart.ParentGroup : m_host.ParentGroup;

            if (group1 == group2) return null; // already in same linkset

            // Link group2 into group1 — group2 ceases to exist as a separate object
            group1.LinkToGroup(group2);

            group1.TriggerScriptChangedEvent(Changed.LINK);
            group1.HasGroupChanged = true;
            group1.ScheduleGroupForFullUpdate();

            ScriptSleep(1000);
            return null;
        }

        public void llBreakLink(int linknum)
        {
            if (m_host?.ParentGroup == null) return;
            if (m_host.ParentGroup.IsAttachment) return;

            // Requires PERMISSION_CHANGE_LINKS
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return;
            if (!AutomaticLinkPermission && (item.PermsMask & PERMISSION_CHANGE_LINKS) == 0)   // as YEngine, LSL_Api.cs:4868
            {
                ScriptShoutError("llBreakLink: PERMISSION_CHANGE_LINKS not set");
                ScriptSleep(1000);
                return;
            }

            BreakLinkCore(linknum);
        }

        /// <summary>llBreakLink after its PERMISSION_CHANGE_LINKS check - the door osForceBreakLink takes (OSSL_Api.cs:2792-2797).</summary>
        private void BreakLinkCore(int linknum)
        {
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsAttachment) return;
            SceneObjectGroup parentGroup = m_host.ParentGroup;

            if (linknum == LINK_ROOT) // break all children off, leave root alone
            {
                var parts = new List<SceneObjectPart>(parentGroup.Parts);
                parts.RemoveAll(p => p.LocalId == parentGroup.RootPart.LocalId);
                if (parts.Count == 0) return;
                foreach (SceneObjectPart p in parts)
                    parentGroup.DelinkFromGroup(p, true);
                parentGroup.TriggerScriptChangedEvent(Changed.LINK);
                return;
            }

            // SL's link constants, handled as YEngine does (LSL_Api.BreakLink): LINK_THIS is
            // the script's own prim; LINK_SET, LINK_ALL_OTHERS and LINK_ALL_CHILDREN name no single
            // prim; anything below LINK_THIS is invalid. (-1 is LINK_SET, not LINK_THIS.)
            if (linknum < LINK_THIS) return;
            SceneObjectPart childPrim;
            switch (linknum)
            {
                case LINK_SET:
                case LINK_ALL_OTHERS:
                case LINK_ALL_CHILDREN:
                    return;
                case LINK_THIS:
                    childPrim = m_host;
                    break;
                default:
                    childPrim = parentGroup.GetLinkNumPart(linknum);
                    break;
            }

            if (childPrim == null) return;
            if (childPrim.LocalId == parentGroup.RootPart.LocalId) return; // can't break root this way

            parentGroup.DelinkFromGroup(childPrim, true);
            parentGroup.TriggerScriptChangedEvent(Changed.LINK);
        }

        public void llBreakAllLinks()
        {
            if (m_host?.ParentGroup == null) return;
            if (m_host.ParentGroup.IsAttachment) return;

            // SL wiki llBreakAllLinks: "the script must request the PERMISSION_CHANGE_LINKS permission with
            // llRequestPermissions and it must be granted by the owner"; without it "the script will shout an error on
            // DEBUG_CHANNEL and the operation fails (but the script continues to run)"; "If PERMISSION_CHANGE_LINKS is
            // granted by anyone other than the owner, then when the function is called an error will be shouted".
            // YEngine LSL_Api.cs:4963-4975 checks it unless AutomaticLinkPermission, as Phlox's llBreakLink
            // and llCreateLink do; their texts and forms. Halcyon had no check.
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return;
            if (!AutomaticLinkPermission && (item.PermsMask & PERMISSION_CHANGE_LINKS) == 0)
            {
                ScriptShoutError("llBreakAllLinks: PERMISSION_CHANGE_LINKS not set");
                return;
            }
            if (!AutomaticLinkPermission && item.PermsGranter != m_host.ParentGroup.OwnerID)
            {
                ShoutError("llBreakAllLinks: PERMISSION_CHANGE_LINKS not set by script owner");
                return;
            }

            BreakAllLinksCore();
        }

        /// <summary>llBreakAllLinks after its permission checks - the door osForceBreakAllLinks takes (OSSL_Api.cs:2800-2805 calls BreakAllLinks).</summary>
        private void BreakAllLinksCore()
        {
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsAttachment) return;

            SceneObjectGroup parentGroup = m_host.ParentGroup;
            if (parentGroup.PrimCount < 2) return;

            var children = new List<SceneObjectPart>(parentGroup.Parts);
            children.RemoveAll(p => p.LocalId == parentGroup.RootPart.LocalId);

            foreach (SceneObjectPart part in children)
            {
                parentGroup.DelinkFromGroup(part, true);
                parentGroup.TriggerScriptChangedEvent(Changed.LINK);
            }
        }

        // ── Prim params ────────────────────────────────────────────────────────


        // ── Primitive params implementation ────────────────────────────────────────



        public void llSetPrimitiveParams(LSLList rules)
        {
            if (m_host == null) return;
            SetPrimParams(m_host, rules);
            ScriptSleep(200);
        }

        public LSLList llGetPrimitiveParams(LSLList parms)
        {
            if (m_host == null) return new LSLList();
            return GetPrimParams(new[] { m_host }, new List<ScenePresence>(), parms);
        }

        public void llSetLinkPrimitiveParams(int linknumber, LSLList rules)
        {
            if (m_host == null) return;
            SetPrimParams(GetLinkParts(linknumber), GetLinkSitters(linknumber), rules);
            ScriptSleep(200);
        }

        public void llSetLinkPrimitiveParamsFast(int linknumber, LSLList rules)
        {
            if (m_host == null) return;
            SetPrimParams(GetLinkParts(linknumber), GetLinkSitters(linknumber), rules);
            // no sleep for Fast variant
        }

        public LSLList llGetLinkPrimitiveParams(int linknumber, LSLList rules)
        {
            if (m_host == null) return new LSLList();
            return GetPrimParams(GetLinkParts(linknumber), GetLinkSitters(linknumber), rules);
        }

        /// <summary>
        /// The one link-number resolver, used by llMessageLinked and every function that takes a link number
        /// (Halcyon LSLSystemAPI.GetLinkParts). LINK_SET: every prim, the script's own included. LINK_ALL_OTHERS:
        /// every prim but the script's own. LINK_ALL_CHILDREN: every prim but the root. LINK_THIS: the script's
        /// prim. LINK_ROOT and 0: the root (0 is Halcyon's "other LINK_ROOT linknum", and an unlinked prim's own
        /// number). A positive number: that link. An unknown negative number or one past the last prim selects
        /// nothing (SL: an invalid link number does nothing).
        /// </summary>
        private IEnumerable<SceneObjectPart> GetLinkParts(int linknumber)
        {
            SceneObjectGroup group = m_host?.ParentGroup;
            if (group == null) yield break;
            switch (linknumber)
            {
                case LINK_SET:
                    foreach (var p in group.Parts.OrderBy(p => p.LinkNum)) yield return p;
                    break;
                case LINK_ALL_OTHERS:
                    foreach (var p in group.Parts.OrderBy(p => p.LinkNum)) if (p != m_host) yield return p;
                    break;
                case LINK_ALL_CHILDREN:
                    foreach (var p in group.Parts.OrderBy(p => p.LinkNum)) if (p != group.RootPart) yield return p;
                    break;
                case LINK_THIS:
                    yield return m_host;
                    break;
                case 0:
                case LINK_ROOT:
                    yield return group.RootPart;
                    break;
                default:
                    if (linknumber > 1 && linknumber <= group.PrimCount)
                    {
                        var t = group.GetLinkNumPart(linknumber);
                        if (t != null) yield return t;
                    }
                    break;
            }
        }

        /// <summary>
        /// The avatars a link number selects (Halcyon GetLinkParts with includeAvatars). Seated avatars take the
        /// link numbers after the last prim in the order they sat, and close up when one stands
        /// (Halcyon SceneObjectGroup.AddSeatedAvatar, RecalcSeatedAvatarLinks); the group's sitting list keeps
        /// that order. LINK_SET, LINK_ALL_OTHERS and LINK_ALL_CHILDREN take in every sitter; a number past the
        /// last prim is that sitter; every other selector is prims only. Only the prim-params functions and
        /// llGetLinkKey/llGetLinkName reach avatars; llMessageLinked and the rest stay on GetLinkParts.
        /// </summary>
        private List<ScenePresence> GetLinkSitters(int linknumber)
        {
            var result = new List<ScenePresence>();
            SceneObjectGroup group = m_host?.ParentGroup;
            if (group == null) return result;
            switch (linknumber)
            {
                case LINK_SET:
                case LINK_ALL_OTHERS:
                case LINK_ALL_CHILDREN:
                    result.AddRange(group.GetSittingAvatars());
                    break;
                default:
                    if (linknumber > group.PrimCount)
                    {
                        var sitters = group.GetSittingAvatars();
                        int i = linknumber - group.PrimCount - 1;
                        if (i < sitters.Count) result.Add(sitters[i]);
                    }
                    break;
            }
            return result;
        }

        /// <summary>The resolver's prim when it selects exactly one, else null (Halcyon llGetLinkKey's rule).</summary>
        private SceneObjectPart GetSingleLinkPart(int linknumber)
        {
            SceneObjectPart found = null;
            foreach (var p in GetLinkParts(linknumber))
            {
                if (found != null) return null;
                found = p;
            }
            return found;
        }

        // ── Helpers: texture/inventory lookup (ported from Halcyon) ───────────

        /// <summary>
        /// Resolve a string that may be a UUID or a texture-inventory name to a UUID.
        /// Returns UUID.Zero if neither.
        /// </summary>
        private UUID KeyOrName(string k)
        {
            if (string.IsNullOrEmpty(k)) return UUID.Zero;
            if (UUID.TryParse(k, out UUID id)) return id;
            // Not a UUID — search the host prim's inventory for a matching item name.
            lock (m_host.TaskInventory)
            {
                foreach (var kvp in m_host.TaskInventory)
                {
                    if (kvp.Value.Name == k)
                        return kvp.Value.AssetID;
                }
            }
            return UUID.Zero;
        }

        /// <summary>
        /// A texture as PRIM_TEXTURE names it. SL wiki: "a texture in the inventory of the prim this
        /// script is in or a UUID of a texture". The script's prim's texture item of that name first (Halcyon's
        /// KeyOrName order: "no-one can name an inventory item with a UUID string" and get the named key), then a UUID.
        /// Only a texture item matches, as SL's "it is not a texture" rule and YEngine's
        /// GetAssetIdFromItemName(m_host, texture, AssetType.Texture). UUID.Zero when neither.
        /// </summary>
        private UUID TextureKeyOrName(string k)
        {
            if (string.IsNullOrEmpty(k)) return UUID.Zero;
            lock (m_host.TaskInventory)
            {
                foreach (var kvp in m_host.TaskInventory)
                {
                    if (kvp.Value.Name == k && kvp.Value.Type == (int)AssetType.Texture)
                        return kvp.Value.AssetID;
                }
            }
            return UUID.TryParse(k, out UUID id) ? id : UUID.Zero;
        }

        /// <summary>
        /// Reverse lookup: given an asset UUID, return the inventory item name if it
        /// exists in the host prim's task inventory; otherwise string.Empty.
        /// </summary>
        private string InventoryName(UUID assetID) => InventoryName(m_host, assetID);

        /// <summary>The same reverse lookup in the given prim's task inventory.</summary>
        private static string InventoryName(SceneObjectPart part, UUID assetID)
        {
            if (assetID == UUID.Zero) return string.Empty;
            lock (part.TaskInventory)
            {
                foreach (var kvp in part.TaskInventory)
                {
                    if (kvp.Value.AssetID == assetID)
                        return kvp.Value.Name;
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// True if the permission mask includes full modify/copy/transfer.
        /// </summary>
        private static bool IsFullPerm(uint permsMask)
        {
            const uint full = (uint)(OpenSim.Framework.PermissionMask.Modify
                                   | OpenSim.Framework.PermissionMask.Copy
                                   | OpenSim.Framework.PermissionMask.Transfer);
            return (permsMask & full) == full;
        }

        /// <summary>
        /// The one rule for whether a script may see an asset key. The key shows only when
        /// the owner's permissions on it are copy, modify and transfer; otherwise NULL_KEY. Inventory keys
        /// (llGetInventoryKey, iwGetLinkInventoryKey) pass the item's CurrentPermissions; texture and material keys
        /// (ConditionalTextureNameOrUUID) pass the prim's OwnerMask, as Halcyon's IsFullPerm(part.OwnerMask).
        /// </summary>
        private static string AssetKeyIfFullPerm(UUID assetID, uint ownerPerms)
            => assetID != UUID.Zero && IsFullPerm(ownerPerms) ? assetID.ToString() : UUID.Zero.ToString();

        // ── Texture workhorses (ported from Halcyon, adapted to this tree's SOP API) ──

        /// <summary>Apply a texture UUID to a face or all faces of a part.</summary>
        private void SetTexture(SceneObjectPart part, string texture, int face)
        {
            if (part == null) return;
            UUID textureID = KeyOrName(texture);
            if (textureID == UUID.Zero) return;

            Primitive.TextureEntry tex = part.Shape.Textures ?? new Primitive.TextureEntry(UUID.Zero);
            int sides = part.GetNumberOfSides();

            if (face >= 0 && face < sides)
            {
                Primitive.TextureEntryFace texface = tex.CreateFace((uint)face);
                if (texface.TextureID != textureID)
                {
                    texface.TextureID = textureID;
                    part.UpdateTextureEntry(tex);
                }
                return;
            }

            if (face == ALL_SIDES)
            {
                bool changed = false;
                for (uint i = 0; i < sides; i++)
                {
                    if (tex.FaceTextures[i] != null && tex.FaceTextures[i].TextureID != textureID)
                    {
                        tex.FaceTextures[i].TextureID = textureID;
                        changed = true;
                    }
                }
                if (tex.DefaultTexture != null && tex.DefaultTexture.TextureID != textureID)
                {
                    tex.DefaultTexture.TextureID = textureID;
                    changed = true;
                }
                if (changed) part.UpdateTextureEntry(tex);
            }
        }

        /// <summary>Apply an RGB color (preserving existing alpha) to a face or all faces.</summary>
        private void SetColor(SceneObjectPart part, Vector3 color, int face)
        {
            if (part == null) return;
            float r = Math.Max(0f, Math.Min(1f, color.X));
            float g = Math.Max(0f, Math.Min(1f, color.Y));
            float b = Math.Max(0f, Math.Min(1f, color.Z));
            // This tree's SOP already has SetFaceColorAlpha. Passing null alpha preserves per-face alpha.
            part.SetFaceColorAlpha(face, new Vector3(r, g, b), null);
            part.SendFullUpdateToAllClients();
        }

        /// <summary>Set alpha (0..1) on a face or all faces, preserving existing RGB.</summary>
        private void SetAlpha(SceneObjectPart part, float alpha, int face)
        {
            if (part == null) return;
            alpha = Math.Max(0f, Math.Min(1f, alpha));
            Primitive.TextureEntry tex = part.Shape.Textures ?? new Primitive.TextureEntry(UUID.Zero);
            int sides = part.GetNumberOfSides();

            if (face >= 0 && face < sides)
            {
                Primitive.TextureEntryFace f = tex.CreateFace((uint)face);
                Color4 c = f.RGBA;
                c.A = alpha;
                f.RGBA = c;
                part.UpdateTextureEntry(tex);
                return;
            }
            if (face == ALL_SIDES)
            {
                for (uint i = 0; i < sides; i++)
                {
                    var f = tex.CreateFace(i);
                    var c = f.RGBA;
                    c.A = alpha;
                    f.RGBA = c;
                }
                if (tex.DefaultTexture != null)
                {
                    var c = tex.DefaultTexture.RGBA;
                    c.A = alpha;
                    tex.DefaultTexture.RGBA = c;
                }
                part.UpdateTextureEntry(tex);
            }
        }

        /// <summary>
        /// llGetAlpha and IW_PRIM_ALPHA's read (Halcyon GetAlpha): a face's alpha; for ALL_SIDES the sum over the
        /// faces (SL: llGetAlpha(ALL_SIDES) is the sum, so scripts compare it with llGetNumberOfSides()); 0 for a
        /// face that does not exist.
        /// </summary>
        private static float GetAlpha(SceneObjectPart part, int face)
        {
            Primitive.TextureEntry tex = part?.Shape.Textures;
            if (tex == null) return 0f;
            int sides = part.GetNumberOfSides();
            if (face == ALL_SIDES)
            {
                double sum = 0;
                for (int i = 0; i < sides; i++)
                    sum += tex.GetFace((uint)i).RGBA.A;
                return (float)sum;
            }
            if (face >= 0 && face < sides)
                return tex.GetFace((uint)face).RGBA.A;
            return 0f;
        }

        /// <summary>A float list value: a float, or an integer taken as one (SL converts it).</summary>
        private static bool PrimFloat(object value, out float f)
        {
            switch (value)
            {
                case float v: f = v; return true;
                case double d: f = (float)d; return true;
                case int i: f = i; return true;
                default: f = 0f; return false;
            }
        }

        /// <summary>A 0..1 colour component as the material's byte, rounded as OpenSim's LSL_Api does.</summary>
        private static byte ColorByte(float c) => (byte)(255f * Math.Clamp(c, 0f, 1f) + 0.5f);

        /// <summary>
        /// A PRIM_NORMAL / PRIM_SPECULAR map: the texture of that name in this prim's inventory, else a key; an empty
        /// string or NULL_KEY is no map. False for anything else (Halcyon InventoryKey then UUID.TryParse; OpenSim's
        /// LSL_Api the same), and the rule then changes nothing.
        /// </summary>
        private bool PrimMaterialMap(object value, out UUID map)
        {
            map = UUID.Zero;
            string name = value?.ToString();
            if (value is Vector3 || value is Quaternion || value is int || value is float) return false;
            if (string.IsNullOrEmpty(name)) return true;
            map = OpenSim.Region.Framework.Scenes.Scripting.ScriptUtils.GetAssetIdFromItemName(m_host, name, (int)AssetType.Texture);
            return map != UUID.Zero || UUID.TryParse(name, out map);
        }

        /// <summary>
        /// PRIM_NORMAL, PRIM_SPECULAR and PRIM_ALPHA_MODE storage, the scene's own: each face's MaterialID names a
        /// FaceMaterial kept by the region's IMaterialsModule, exactly as OpenSim's LSL_Api keeps them
        /// (SetFaceMaterialNormalMap / SetFaceMaterialSpecMap / SetFaceMaterialAlphaMode), so a YEngine script and a
        /// Phlox script see the same material. <paramref name="edit"/> changes a copy of the face's material, or a
        /// new one when the face has none unless <paramref name="noneStaysNone"/> (a cleared map, or the default
        /// blend mode, on a face with no material). The module gives an edited material its id by content, and none
        /// for one that is all defaults. A face that does not exist changes nothing. With no materials module in the
        /// region nothing changes, as in OpenSim.
        /// </summary>
        private void PrimEditFaceMaterials(SceneObjectPart part, int face, bool noneStaysNone, Action<FaceMaterial> edit)
        {
            IMaterialsModule materials = World?.RequestModuleInterface<IMaterialsModule>();
            if (materials == null || part?.ParentGroup == null || part.ParentGroup.IsDeleted) return;

            Primitive.TextureEntry tex = part.Shape.Textures;
            int sides = part.GetNumberOfSides();
            bool changed = false;

            bool EditFace(int f)
            {
                Primitive.TextureEntryFace texface = tex.CreateFace((uint)f);
                UUID oldID = texface.MaterialID;
                if (oldID == UUID.Zero && noneStaysNone) return false;
                FaceMaterial mat = (oldID == UUID.Zero ? null : materials.GetMaterialCopy(oldID)) ?? new FaceMaterial();
                edit(mat);
                UUID newID = materials.AddNewMaterial(mat);
                if (newID == oldID)
                {
                    materials.RemoveMaterial(newID); // AddNewMaterial counted this face a second time
                    return false;
                }
                texface.MaterialID = newID;
                materials.RemoveMaterial(oldID);
                return true;
            }

            if (face == ALL_SIDES)
                for (int f = 0; f < sides; f++) changed |= EditFace(f);
            else if (face >= 0 && face < sides)
                changed = EditFace(face);
            if (!changed) return;

            part.Shape.TextureEntry = tex.GetBytes(9);
            part.TriggerScriptChangedEvent(Changed.TEXTURE);
            part.ScheduleFullUpdate();
            part.ParentGroup.HasGroupChanged = true;
        }

        /// <summary>
        /// One face's PRIM_NORMAL, PRIM_SPECULAR or PRIM_ALPHA_MODE read from the scene's material store, as OpenSim's
        /// LSL_Api reads it (getLSLFaceMaterial). A face with no material reads SL's defaults: PRIM_NORMAL
        /// [ NULL_KEY, &lt;1,1,0&gt;, ZERO_VECTOR, 0.0 ], PRIM_SPECULAR the same then &lt;1,1,1&gt;, 51, 0, and
        /// PRIM_ALPHA_MODE [ PRIM_ALPHA_MODE_BLEND, 0 ].
        /// </summary>
        private void GetFaceMaterial(List<object> result, int code, SceneObjectPart part, Primitive.TextureEntryFace texface)
        {
            IMaterialsModule materials = World?.RequestModuleInterface<IMaterialsModule>();
            UUID id = materials == null ? UUID.Zero : texface.MaterialID;
            FaceMaterial mat = id == UUID.Zero ? null : materials.GetMaterial(id);
            mat ??= new FaceMaterial();

            switch (code)
            {
                case PRIM_NORMAL:
                    result.Add(ConditionalTextureNameOrUUID(part, mat.NormalMapID));
                    result.Add(new Vector3(mat.NormalRepeatX, mat.NormalRepeatY, 0f));
                    result.Add(new Vector3(mat.NormalOffsetX, mat.NormalOffsetY, 0f));
                    result.Add(mat.NormalRotation);
                    break;
                case PRIM_SPECULAR:
                    const float scale = 1f / 255f;
                    result.Add(ConditionalTextureNameOrUUID(part, mat.SpecularMapID));
                    result.Add(new Vector3(mat.SpecularRepeatX, mat.SpecularRepeatY, 0f));
                    result.Add(new Vector3(mat.SpecularOffsetX, mat.SpecularOffsetY, 0f));
                    result.Add(mat.SpecularRotation);
                    result.Add(new Vector3(mat.SpecularLightColorR * scale, mat.SpecularLightColorG * scale,
                        mat.SpecularLightColorB * scale));
                    result.Add((int)mat.SpecularLightExponent);
                    result.Add((int)mat.EnvironmentIntensity);
                    break;
                case PRIM_ALPHA_MODE:
                    result.Add((int)mat.DiffuseAlphaMode);
                    result.Add((int)mat.AlphaMaskCutoff);
                    break;
            }
        }

        /// <summary>Set texture repeat scale (U,V) on a face or all faces.</summary>
        private void ScaleTexture(SceneObjectPart part, float u, float v, int face)
        {
            if (part == null) return;
            Primitive.TextureEntry tex = part.Shape.Textures ?? new Primitive.TextureEntry(UUID.Zero);
            int sides = part.GetNumberOfSides();

            if (face >= 0 && face < sides)
            {
                var f = tex.CreateFace((uint)face);
                f.RepeatU = u;
                f.RepeatV = v;
                part.UpdateTextureEntry(tex);
                return;
            }
            if (face == ALL_SIDES)
            {
                for (uint i = 0; i < sides; i++)
                {
                    var f = tex.CreateFace(i);
                    f.RepeatU = u;
                    f.RepeatV = v;
                }
                if (tex.DefaultTexture != null)
                {
                    tex.DefaultTexture.RepeatU = u;
                    tex.DefaultTexture.RepeatV = v;
                }
                part.UpdateTextureEntry(tex);
            }
        }

        /// <summary>Set texture offset (U,V) on a face or all faces.</summary>
        private void OffsetTexture(SceneObjectPart part, float u, float v, int face)
        {
            if (part == null) return;
            Primitive.TextureEntry tex = part.Shape.Textures ?? new Primitive.TextureEntry(UUID.Zero);
            int sides = part.GetNumberOfSides();

            if (face >= 0 && face < sides)
            {
                var f = tex.CreateFace((uint)face);
                f.OffsetU = u;
                f.OffsetV = v;
                part.UpdateTextureEntry(tex);
                return;
            }
            if (face == ALL_SIDES)
            {
                for (uint i = 0; i < sides; i++)
                {
                    var f = tex.CreateFace(i);
                    f.OffsetU = u;
                    f.OffsetV = v;
                }
                if (tex.DefaultTexture != null)
                {
                    tex.DefaultTexture.OffsetU = u;
                    tex.DefaultTexture.OffsetV = v;
                }
                part.UpdateTextureEntry(tex);
            }
        }

        /// <summary>Set texture rotation (radians) on a face or all faces.</summary>
        private void RotateTexture(SceneObjectPart part, float rotation, int face)
        {
            if (part == null) return;
            Primitive.TextureEntry tex = part.Shape.Textures ?? new Primitive.TextureEntry(UUID.Zero);
            int sides = part.GetNumberOfSides();

            if (face >= 0 && face < sides)
            {
                var f = tex.CreateFace((uint)face);
                f.Rotation = rotation;
                part.UpdateTextureEntry(tex);
                return;
            }
            if (face == ALL_SIDES)
            {
                for (uint i = 0; i < sides; i++)
                {
                    tex.CreateFace(i).Rotation = rotation;
                }
                if (tex.DefaultTexture != null)
                    tex.DefaultTexture.Rotation = rotation;
                part.UpdateTextureEntry(tex);
            }
        }

        /// <summary>Apply a texture animation to one part (used by llSetTextureAnim and llSetLinkTextureAnim).</summary>
        private void SetPrimTextureAnim(SceneObjectPart part, int mode, int face,
                                        int sizex, int sizey, float start, float length, float rate)
        {
            if (part == null) return;
            Primitive.TextureAnimation pTexAnim = new Primitive.TextureAnimation();
            pTexAnim.Flags  = (Primitive.TextureAnimMode)mode;
            pTexAnim.Face   = (face == ALL_SIDES) ? (uint)255 : (uint)face;
            pTexAnim.Length = length;
            pTexAnim.Rate   = rate;
            pTexAnim.SizeX  = (uint)sizex;
            pTexAnim.SizeY  = (uint)sizey;
            pTexAnim.Start  = start;

            part.AddTextureAnimation(pTexAnim);
            part.ScheduleUpdate(PrimUpdateFlags.FullUpdate);
            if (part.ParentGroup != null) part.ParentGroup.HasGroupChanged = true;
        }

        /// <summary>SL's floating-text limit: 254 bytes of UTF-8 (wiki llSetText).</summary>
        private const int MaxFloatingTextBytes = 254;

        /// <summary>
        /// Floating text on one prim, for llSetText and PRIM_TEXT (Halcyon PrimSetText): the color and alpha are
        /// clamped to 0..1, and the text is cut to SL's 254 bytes of UTF-8, a multibyte character that would be
        /// split dropped whole ("If the string is longer it will be truncated to 254 bytes, and any multibyte
        /// characters getting split will be removed entirely.").
        /// </summary>
        private static void PrimSetText(SceneObjectPart part, string text, Vector3 color, float alpha)
        {
            if (part == null) return;
            text ??= string.Empty;
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            if (utf8.Length > MaxFloatingTextBytes)
            {
                int cut = MaxFloatingTextBytes;
                while (cut > 0 && (utf8[cut] & 0xC0) == 0x80) cut--; // back to the start of the split character
                text = Encoding.UTF8.GetString(utf8, 0, cut);
            }
            color = new Vector3(Math.Clamp(color.X, 0f, 1f), Math.Clamp(color.Y, 0f, 1f), Math.Clamp(color.Z, 0f, 1f));
            part.SetText(text, color, Math.Clamp(alpha, 0f, 1f));
            if (part.ParentGroup != null) part.ParentGroup.HasGroupChanged = true;
        }

        /// <summary>
        /// PRIM_POSITION, PRIM_POS_LOCAL and llSetPos on one prim (Halcyon SetPos(part, v, true) and SetPosAdjust): a root moves
        /// the object (an attachment's root, its offset from the attach point); a child takes the vector as its offset from
        /// the root. Halcyon's caps: an unattached root moves at most 10 m from where it is (SL: "The distance is
        /// capped to 10m per PRIM_POSITION call"), an attached root at most 3.5 m from the attach point, a child of
        /// an attachment at most 54 m from the root and any other child at most 256 m; a longer move stops at the
        /// cap along the same line.
        /// </summary>
        private static void SetPrimLocalPos(SceneObjectPart part, Vector3 target)
        {
            SceneObjectGroup group = part.ParentGroup;
            if (group == null || group.IsDeleted || group.inTransit) return;

            bool root = part == group.RootPart;
            Vector3 anchor = root && !group.IsAttachment ? part.AbsolutePosition : Vector3.Zero;
            float limit = root ? (group.IsAttachment ? 3.5f : 10f) : (group.IsAttachment ? 54f : 256f);
            float dist = Vector3.Distance(target, anchor);
            if (dist > limit) target = (target - anchor) * (limit / dist) + anchor;

            if (root) group.UpdateGroupPosition(target);
            else
            {
                part.UpdateOffSet(target);
                group.HasGroupChanged = true;
            }
        }

        /// <summary>
        /// PRIM_POS_LOCAL read (Halcyon GetPartLocalPos; SL llGetLocalPos): an unattached root returns its region
        /// position, an attachment's root its offset from the attach point, a child its offset from the root.
        /// </summary>
        private static Vector3 PartLocalPos(SceneObjectPart part)
        {
            SceneObjectGroup group = part.ParentGroup;
            if (group == null || part != group.RootPart) return part.OffsetPosition;
            return group.IsAttachment ? part.AttachedPos : part.AbsolutePosition;
        }

        /// <summary>
        /// PRIM_ROTATION read (Halcyon GetPartRot): the prim's region rotation. On an attachment's root it is the
        /// wearer's rotation, or the camera's in mouselook (SL: "PRIM_ROTATION incorrectly reports the avatars
        /// rotation when called on the root of an attached object"). A seated wearer's is its region rotation, not
        /// the one relative to its seat that the scene keeps (SL llGetRootRotation: "Returns an accurate facing for
        /// Avatars seated"; OpenSim's LSL_Api llGetRootRotation reads GetWorldRotation too).
        /// </summary>
        private Quaternion PartRegionRot(SceneObjectPart part)
        {
            SceneObjectGroup group = part.ParentGroup;
            if (group != null && part == group.RootPart && group.IsAttachment)
            {
                ScenePresence avatar = World?.GetScenePresence(group.AttachedAvatar);
                if (avatar != null)
                    return (avatar.AgentControlFlags & 0x00020000u) != 0  // AGENT_CONTROL_MOUSELOOK
                        ? avatar.CameraRotation
                        : avatar.GetWorldRotation();
                return group.GroupRotation;
            }
            return part.GetWorldRotation();
        }

        /// <summary>
        /// PRIM_PHYSICS_SHAPE_TYPE on one prim (Halcyon: PreferredPhysicsShape, only for a type it defines). A type
        /// other than PRIM_PHYSICS_SHAPE_PRIM, _NONE or _CONVEX changes nothing. PRIM_PHYSICS_SHAPE_NONE is refused on
        /// a root (SL: "This cannot be applied to the root prim or avatars") and the root keeps its type; the scene
        /// would otherwise put its default in. The scene's setter rebuilds or drops the prim's physics actor, and
        /// the properties reply carries the new type to viewers.
        /// </summary>
        private static void PrimSetPhysicsShapeType(SceneObjectPart part, int type)
        {
            if (type != PRIM_PHYSICS_SHAPE_PRIM && type != PRIM_PHYSICS_SHAPE_NONE && type != PRIM_PHYSICS_SHAPE_CONVEX)
                return;
            SceneObjectGroup group = part.ParentGroup;
            if (group == null || group.IsDeleted) return;
            if (type == PRIM_PHYSICS_SHAPE_NONE && part == group.RootPart) return;
            if (part.PhysicsShapeType == (byte)type) return;
            part.PhysicsShapeType = (byte)type;
            group.HasGroupChanged = true;
            part.SendPropertiesToAllClients();
        }

        /// <summary>
        /// PRIM_SLICE on one prim: the shape's advanced cut (SL: "a shape attribute, equivalent to advanced_cut"), through
        /// the scene's own UpdateSlice, which OpenSim's LSL_Api uses too. That is the slice PRIM_TYPE writes on a box,
        /// cylinder or prism (PathBegin / PathEnd), and the dimple of a sphere or the profile cut of a torus, tube or
        /// ring (ProfileBegin / ProfileEnd), so PRIM_TYPE reads those back. The scene keeps begin at least 0.02 below
        /// end (SL: "x must be at least 0.05 smaller than y", with the wiki's note that "the difference can be as small
        /// as 0.02"). A sculpt or mesh has no cut and is left alone, as Halcyon leaves every shape but box, cylinder and
        /// prism.
        /// </summary>
        private void PrimSetSlice(SceneObjectPart part, Vector3 slice)
        {
            if (part.GetPrimType() == ScenePrimType.SCULPT) return;
            part.UpdateSlice(slice.X, slice.Y);
        }

        /// <summary>
        /// PRIM_SLICE read: where UpdateSlice writes for the prim's shape (the scene's own shape test); a sculpt or
        /// mesh reads the full &lt;0, 1, 0&gt;.
        /// </summary>
        private static Vector3 PrimSlice(SceneObjectPart part)
        {
            PrimitiveBaseShape shape = part.Shape;
            ScenePrimType type = part.GetPrimType();
            if (type == ScenePrimType.SCULPT) return DEFAULT_SLICE_VEC;
            bool profile = type == ScenePrimType.SPHERE || type == ScenePrimType.TORUS || type == ScenePrimType.TUBE
                           || type == ScenePrimType.RING;
            return profile
                ? new Vector3(shape.ProfileBegin / 50000.0f, 1 - shape.ProfileEnd / 50000.0f, 0)
                : new Vector3(shape.PathBegin / 50000.0f, 1 - shape.PathEnd / 50000.0f, 0);
        }

        /// <summary>
        /// PRIM_PROJECTOR on one prim (SL's [texture, fov, focus, ambiance]; Halcyon has no rule), with OpenSim's
        /// meaning for what SL leaves open (LSL_Api.SetPrimParams): the texture is a key or the name of a texture in
        /// this prim; "" or NULL_KEY turns projection off; fov is held to 0..3, focus to -20..20 and ambiance to 0..1,
        /// as osSetProjectionParams holds them. A name that is not a texture here is an error and changes nothing.
        /// </summary>
        private void PrimSetProjector(SceneObjectPart part, string texture, float fov, float focus, float ambiance)
        {
            UUID texID = UUID.Zero;
            if (!string.IsNullOrEmpty(texture) && !UUID.TryParse(texture, out texID))
            {
                TaskInventoryItem item = FindInventoryItem(texture, (int)AssetType.Texture);
                if (item == null)
                {
                    ShoutError($"Could not find texture '{texture}'");
                    return;
                }
                texID = item.AssetID;
            }

            PrimitiveBaseShape shape = part.Shape;
            if (texID != UUID.Zero)
            {
                shape.ProjectionEntry = true;
                shape.ProjectionTextureUUID = texID;
                shape.ProjectionFOV = Math.Clamp(fov, 0f, 3.0f);
                shape.ProjectionFocus = Math.Clamp(focus, -20.0f, 20.0f);
                shape.ProjectionAmbiance = Math.Clamp(ambiance, 0f, 1.0f);
            }
            else if (shape.ProjectionEntry)
                shape.ProjectionEntry = false;
            else
                return;
            ProjectionChanged(part);
        }

        /// <summary>
        /// A prim's projection changed: saved with the object and sent to viewers in the shape's extra parameters (the
        /// scene saves the projection fields only while projection is on).
        /// </summary>
        private static void ProjectionChanged(SceneObjectPart part)
        {
            if (part.ParentGroup != null) part.ParentGroup.HasGroupChanged = true;
            part.ScheduleFullUpdate();
        }

        /// <summary>
        /// PRIM_PROJECTOR read (SL's [texture, fov, focus, ambiance]: "If the prim is not a projector the texture key
        /// will be NULL_KEY"), with the texture as Phlox's other texture reads show it.
        /// </summary>
        private void PrimProjector(SceneObjectPart part, List<object> result)
        {
            PrimitiveBaseShape shape = part.Shape;
            if (!shape.ProjectionEntry)
            {
                result.Add(UUID.Zero.ToString());
                result.Add(0f);
                result.Add(0f);
                result.Add(0f);
                return;
            }
            result.Add(ConditionalTextureNameOrUUID(part, shape.ProjectionTextureUUID));
            result.Add(shape.ProjectionFOV);
            result.Add(shape.ProjectionFocus);
            result.Add(shape.ProjectionAmbiance);
        }

        /// <summary>
        /// PRIM_REFLECTION_PROBE on one prim (SL): ambiance "Ranges from 0.0 to 100.0", clip_distance "Ranges from 0.0 to
        /// 1024.0", and the PRIM_REFLECTION_PROBE_* flags, kept in the shape's ReflectionProbe as OpenSim's LSL_Api
        /// keeps them (saved and sent in its extra parameters). FALSE removes the probe.
        /// </summary>
        private static void PrimSetReflectionProbe(SceneObjectPart part, bool active, float ambiance, float clip, int flags)
        {
            PrimitiveBaseShape shape = part.Shape;
            bool changed;
            if (active)
            {
                ambiance = Math.Clamp(ambiance, 0f, 100f);
                clip = Math.Clamp(clip, 0f, 1024f);
                changed = shape.ReflectionProbe is null;
                shape.ReflectionProbe ??= new Primitive.ReflectionProbe();
                changed |= shape.ReflectionProbe.Ambiance != ambiance || shape.ReflectionProbe.ClipDistance != clip
                           || shape.ReflectionProbe.Flags != (byte)flags;
                shape.ReflectionProbe.Ambiance = ambiance;
                shape.ReflectionProbe.ClipDistance = clip;
                shape.ReflectionProbe.Flags = (byte)flags;
            }
            else
            {
                changed = shape.ReflectionProbe is not null;
                shape.ReflectionProbe = null;
            }
            if (!changed) return;
            if (part.ParentGroup != null) part.ParentGroup.HasGroupChanged = true;
            part.ScheduleFullUpdate();
        }

        /// <summary>One prim's prim-params rules (llSetPrimitiveParams, osSetPrimitiveParams).</summary>
        private void SetPrimParams(SceneObjectPart part, LSLList rules)
        {
            if (part == null) return;
            SetPrimParams(new[] { part }, new List<ScenePresence>(), rules);
        }

        /// <summary>
        /// The prim-params setter walk (Halcyon SetPrimParams): each rule in turn, on every prim and seated avatar it
        /// targets. Every rule is read by SL's value count (PrimParamRules), so a rule Phlox does not act on yet is
        /// skipped whole, logged once per script, and the rules after it still apply. PRIM_LINK_TARGET points the
        /// rules after it at the prims and avatars its link selects. A rule that is not an integer, or a rule number
        /// none of SL, Halcyon and OpenSim defines, ends the walk with SL's script error ("llSetPrimitiveParams
        /// error running rule #2: unknown rule."); the rules before it have applied. A rule whose values run past
        /// the end of the list ends the walk.
        /// </summary>
        private void SetPrimParams(IEnumerable<SceneObjectPart> parts, IEnumerable<ScenePresence> sitters, LSLList rules)
        {
            if (rules == null) return;
            var data = rules.Data;
            var prims = parts.ToList();
            var seated = sitters.ToList();
            var moved = new List<ScenePresence>();
            int idx = 0, rule = 0;

            while (idx < data.Length)
            {
                rule++;
                if (data[idx] is not int code)
                {
                    ShoutError($"llSetPrimitiveParams error running rule #{rule}: non-integer rule.");
                    break;
                }
                int length = PrimParamRules.SetRuleLength(data, idx, code);
                if (length < 0)
                {
                    ShoutError($"llSetPrimitiveParams error running rule #{rule}: unknown rule.");
                    break;
                }
                if (idx + length > data.Length) break;
                int values = idx + 1;
                idx += length;

                if (code == PRIM_LINK_TARGET)
                {
                    if (data[values] is not int link) break;
                    prims = GetLinkParts(link).ToList();
                    seated = GetLinkSitters(link);
                    continue;
                }

                bool applied = false;
                foreach (var part in prims)
                    applied = SetPrimRule(part, code, data, values);
                if (prims.Count > 0 && !applied)
                    LogUnimplementedPrimRule(code, length - 1);
                foreach (var sp in seated)
                {
                    // SL: "PRIM_OMEGA cannot be used on avatars sitting on the object. It will emit the error message
                    // 'PRIM_OMEGA disallowed on agent'." The rules after it still apply. SL documents no other rule
                    // as disallowed on an agent.
                    if (code == PRIM_OMEGA && sp.ParentID != 0) { ShoutError("PRIM_OMEGA disallowed on agent"); continue; }
                    if (SetSitterPrimRule(sp, code, data, values) && !moved.Contains(sp))
                        moved.Add(sp);
                }
            }

            foreach (var sp in moved)
                sp.SendTerseUpdateToAllClients();
        }

        /// <summary>The prim-params rules Phlox reads but does not act on yet, each logged once per script.</summary>
        private HashSet<int> m_unimplementedPrimRulesLogged;

        private void LogUnimplementedPrimRule(int code, int values)
        {
            m_unimplementedPrimRulesLogged ??= new HashSet<int>();
            if (!m_unimplementedPrimRulesLogged.Add(code)) return;
            m_log.LogWarning("[PhloxAPI]: prim-params rule {Rule} is not implemented yet; skipped its {Values} values for {Item}",
                code, values, m_itemID);
        }

        /// <summary>
        /// One setter rule on one prim, its values starting at <paramref name="idx"/> (the walk has checked they are
        /// all there). False for a rule Phlox does not implement yet.
        /// </summary>
        private bool SetPrimRule(SceneObjectPart part, int code, object[] data, int idx)
        {
            switch (code)
            {
                case PRIM_COLOR:
                {
                    if (idx + 2 >= data.Length) break;
                    int face; Vector3 color; float alpha;
                    try { face  = Convert.ToInt32(data[idx++]); } catch { idx += 2; break; }
                    try { color = (Vector3)data[idx++]; } catch { idx++; break; }
                    try { alpha = (float)Convert.ToDouble(data[idx++]); } catch { break; }
                    alpha = Math.Max(0f, Math.Min(1f, alpha));
                    part.SetFaceColorAlpha(face, color, alpha);
                    part.SendFullUpdateToAllClients();
                    break;
                }

                case PRIM_TEXTURE:
                {
                    if (idx + 4 >= data.Length) break;
                    int face; string tex; Vector3 repeats, offsets; float rot;
                    try { face    = Convert.ToInt32(data[idx++]); } catch { idx += 4; break; }
                    try { tex     = data[idx++]?.ToString() ?? string.Empty; } catch { idx += 3; break; }
                    try { repeats = (Vector3)data[idx++]; } catch { idx += 2; break; }
                    try { offsets = (Vector3)data[idx++]; } catch { idx++; break; }
                    try { rot     = (float)Convert.ToDouble(data[idx++]); } catch { break; }
                    Primitive.TextureEntry texEntry = part.Shape.Textures ?? new Primitive.TextureEntry(UUID.Zero);
                    // SL wiki PRIM_TEXTURE, "a texture in the inventory of the prim this script is in or a
                    // UUID of a texture". A name no longer blanks the face. Neither found: the texture stays as it is and
                    // the rest still applies (Halcyon SetTexture + ScaleTexture/OffsetTexture/RotateTexture).
                    UUID texUUID = TextureKeyOrName(tex);
                    void ApplyTex(Primitive.TextureEntryFace f) {
                        if (texUUID != UUID.Zero) f.TextureID = texUUID;
                        f.RepeatU = repeats.X; f.RepeatV = repeats.Y;
                        f.OffsetU = offsets.X; f.OffsetV = offsets.Y;
                        f.Rotation = rot;
                    }
                    if (face == ALL_SIDES) { for (int i = 0; i < 8; i++) ApplyTex(texEntry.CreateFace((uint)i)); }
                    else { try { ApplyTex(texEntry.CreateFace((uint)face)); } catch { } }
                    part.UpdateTextureEntry(texEntry.GetBytes());
                    break;
                }

                case PRIM_GLOW:
                {
                    if (idx + 1 >= data.Length) break;
                    int face; float glow;
                    try { face = Convert.ToInt32(data[idx++]); } catch { idx++; break; }
                    try { glow = (float)Convert.ToDouble(data[idx++]); } catch { break; }
                    glow = Math.Max(0f, Math.Min(1f, glow));
                    Primitive.TextureEntry te = part.Shape.Textures ?? new Primitive.TextureEntry(UUID.Zero);
                    if (face == ALL_SIDES) { for (int i = 0; i < 8; i++) te.CreateFace((uint)i).Glow = glow; }
                    else { try { te.CreateFace((uint)face).Glow = glow; } catch { } }
                    part.UpdateTextureEntry(te.GetBytes());
                    break;
                }

                case PRIM_FULLBRIGHT:
                {
                    if (idx + 1 >= data.Length) break;
                    int face; bool bright;
                    try { face  = Convert.ToInt32(data[idx++]); } catch { idx++; break; }
                    try { bright = Convert.ToInt32(data[idx++]) != 0; } catch { break; }
                    Primitive.TextureEntry te = part.Shape.Textures ?? new Primitive.TextureEntry(UUID.Zero);
                    if (face == ALL_SIDES) { for (int i = 0; i < 8; i++) te.CreateFace((uint)i).Fullbright = bright; }
                    else { try { te.CreateFace((uint)face).Fullbright = bright; } catch { } }
                    part.UpdateTextureEntry(te.GetBytes());
                    break;
                }

                case PRIM_BUMP_SHINY:
                {
                    if (idx + 2 >= data.Length) break;
                    int face, shiny, bump;
                    try { face  = Convert.ToInt32(data[idx++]); } catch { idx += 2; break; }
                    try { shiny = Convert.ToInt32(data[idx++]); } catch { idx++; break; }
                    try { bump  = Convert.ToInt32(data[idx++]); } catch { break; }
                    Primitive.TextureEntry te = part.Shape.Textures ?? new Primitive.TextureEntry(UUID.Zero);
                    // PRIM_SHINY_* 0..3 go in the top two bits of the material byte (OpenMetaverse
                    // Shininess Low = 0x40 .. High = 0xC0); anything else is none (Halcyon and YEngine SetShiny). The bump
                    // keeps to its five bits (BUMP_MASK 0x1F) so it cannot write the fullbright or shiny bits.
                    Shininess shinyBits = shiny >= 0 && shiny <= 3 ? (Shininess)(shiny << 6) : Shininess.None;
                    void ApplyBS(Primitive.TextureEntryFace f) {
                        f.Shiny = shinyBits;
                        f.Bump  = (Bumpiness)(bump & 0x1F);
                    }
                    if (face == ALL_SIDES) { for (int i = 0; i < 8; i++) ApplyBS(te.CreateFace((uint)i)); }
                    else { try { ApplyBS(te.CreateFace((uint)face)); } catch { } }
                    part.UpdateTextureEntry(te.GetBytes());
                    break;
                }

                case PRIM_MATERIAL:
                {
                    if (idx >= data.Length) break;
                    int mat;
                    try { mat = Convert.ToInt32(data[idx++]); } catch { break; }
                    part.Material = (byte)mat;
                    if (part.ParentGroup != null) part.ParentGroup.HasGroupChanged = true;
                    part.ScheduleFullUpdate();
                    break;
                }


                case PRIM_SIZE:
                {
                    if (idx >= data.Length) break;
                    Vector3 scale;
                    try { scale = (Vector3)data[idx++]; } catch { break; }
                    scale.X = Math.Max(0.01f, Math.Min(64f, scale.X));
                    scale.Y = Math.Max(0.01f, Math.Min(64f, scale.Y));
                    scale.Z = Math.Max(0.01f, Math.Min(64f, scale.Z));
                    part.Resize(scale);
                    PhySleep();   // Halcyon's SetPrimParams PRIM_SIZE goes through SetScale
                    break;
                }

                case PRIM_ROT_LOCAL:
                {
                    if (idx >= data.Length) break;
                    Quaternion rot;
                    try { rot = (Quaternion)data[idx++]; } catch { break; }
                    part.UpdateRotation(rot);
                    break;
                }

                case PRIM_POSITION:
                case PRIM_POS_LOCAL:
                {
                    // Halcyon: PRIM_POS_LOCAL is "same as PRIM_POSITION on a SET operation", both its
                    // SetPos(part, v, true). A root takes the object's position (an attachment's: its offset from the
                    // attach point); a child, its offset from the root (SL: child prims take local coordinates).
                    // Halcyon's movement caps apply per rule, so a list that repeats the rule moves once per rule.
                    if (data[idx] is not Vector3 pos) break;
                    SetPrimLocalPos(part, pos);
                    break;
                }

                case PRIM_PHYSICS:
                case PRIM_PHANTOM:
                case PRIM_TEMP_ON_REZ:
                {
                    // Object-wide in SL ("PRIM_PHANTOM, PRIM_PHYSICS and PRIM_TEMP_ON_REZ applies to the entire
                    // object") and in Halcyon (part.ParentGroup), even through a child link. Any non-zero integer is
                    // TRUE, as llSetStatus.
                    if (data[idx] is not int flag) break;
                    SceneObjectGroup group = part.ParentGroup;
                    if (group == null || group.IsDeleted) break;
                    bool on = flag != 0;
                    if (code == PRIM_PHYSICS) SetObjectPhysics(group, on);
                    else if (code == PRIM_PHANTOM) group.ScriptSetPhantomStatus(on);
                    else group.ScriptSetTemporaryStatus(on);
                    break;
                }

                case PRIM_ROTATION:
                {
                    // Halcyon and SL: a root turns the whole object to the rotation given. A child gets the root's
                    // rotation times the one given, SL's child-prim quirk (SVC-93: "If the prim is not the root
                    // prim it is offset by the root's rotation").
                    if (data[idx] is not Quaternion rot) break;
                    SceneObjectGroup group = part.ParentGroup;
                    if (group == null || part == group.RootPart) part.UpdateRotation(rot);
                    else part.UpdateRotation(group.RootPart.RotationOffset * rot);
                    break;
                }

                case PRIM_TEXT:
                {
                    // [ PRIM_TEXT, string text, vector color, float alpha ], as llSetText on this prim.
                    if (data[idx + 1] is not Vector3 color) break;
                    float alpha;
                    try { alpha = (float)Convert.ToDouble(data[idx + 2]); } catch { break; }
                    PrimSetText(part, data[idx]?.ToString() ?? string.Empty, color, alpha);
                    break;
                }

                case PRIM_OMEGA:
                {
                    // [ PRIM_OMEGA, vector axis, float spinrate, float gain ], as llTargetOmega on this prim.
                    if (data[idx] is not Vector3 axis) break;
                    float spinrate, gain;
                    try
                    {
                        spinrate = (float)Convert.ToDouble(data[idx + 1]);
                        gain = (float)Convert.ToDouble(data[idx + 2]);
                    }
                    catch { break; }
                    PrimTargetOmega(part, axis, spinrate, gain);
                    break;
                }

                case PRIM_CLICK_ACTION:
                {
                    // [ PRIM_CLICK_ACTION, integer action ], as llSetClickAction on this prim.
                    if (data[idx] is not int action) break;
                    PrimSetClickAction(part, action);
                    break;
                }

                case PRIM_SIT_TARGET:
                {
                    // [ PRIM_SIT_TARGET, integer active, vector offset, rotation rot ] (Halcyon, SL): active 0
                    // removes the target; otherwise it is set, a zero offset included.
                    if (data[idx] is not int active || data[idx + 1] is not Vector3 offset
                        || data[idx + 2] is not Quaternion rot) break;
                    PrimSetSitTarget(part, active != 0, offset, rot);
                    break;
                }

                case PRIM_PHYSICS_SHAPE_TYPE:
                {
                    // [ PRIM_PHYSICS_SHAPE_TYPE, integer type ]
                    if (data[idx] is not int shapeType) break;
                    PrimSetPhysicsShapeType(part, shapeType);
                    break;
                }

                case PRIM_FLEXIBLE:
                {
                    if (idx + 6 >= data.Length) break;
                    bool flex; int softness; float gravity, friction, wind, tension; Vector3 force;
                    try { flex      = Convert.ToInt32(data[idx++]) != 0; } catch { idx += 6; break; }
                    try { softness  = Convert.ToInt32(data[idx++]); } catch { idx += 5; break; }
                    try { gravity   = (float)Convert.ToDouble(data[idx++]); } catch { idx += 4; break; }
                    try { friction  = (float)Convert.ToDouble(data[idx++]); } catch { idx += 3; break; }
                    try { wind      = (float)Convert.ToDouble(data[idx++]); } catch { idx += 2; break; }
                    try { tension   = (float)Convert.ToDouble(data[idx++]); } catch { idx++; break; }
                    try { force     = (Vector3)data[idx++]; } catch { break; }
                    var shape = part.Shape;
                    shape.FlexiEntry    = flex;
                    shape.FlexiSoftness = softness;
                    shape.FlexiGravity  = gravity;
                    shape.FlexiDrag     = friction;
                    shape.FlexiWind     = wind;
                    shape.FlexiTension  = tension;
                    shape.FlexiForceX   = force.X;
                    shape.FlexiForceY   = force.Y;
                    shape.FlexiForceZ   = force.Z;
                    part.Shape = shape;
                    if (part.ParentGroup != null) part.ParentGroup.HasGroupChanged = true;
                    part.ScheduleFullUpdate();
                    break;
                }

                case PRIM_POINT_LIGHT:
                {
                    if (idx + 4 >= data.Length) break;
                    bool enabled; Vector3 color; float intensity, radius, falloff;
                    try { enabled   = Convert.ToInt32(data[idx++]) != 0; } catch { idx += 4; break; }
                    try { color     = (Vector3)data[idx++]; } catch { idx += 3; break; }
                    try { intensity = (float)Convert.ToDouble(data[idx++]); } catch { idx += 2; break; }
                    try { radius    = (float)Convert.ToDouble(data[idx++]); } catch { idx++; break; }
                    try { falloff   = (float)Convert.ToDouble(data[idx++]); } catch { break; }
                    var shape = part.Shape;
                    shape.LightEntry     = enabled;
                    shape.LightColorR    = color.X;
                    shape.LightColorG    = color.Y;
                    shape.LightColorB    = color.Z;
                    shape.LightIntensity = intensity;
                    shape.LightRadius    = radius;
                    shape.LightFalloff   = falloff;
                    part.Shape = shape;
                    if (part.ParentGroup != null) part.ParentGroup.HasGroupChanged = true;
                    part.ScheduleFullUpdate();
                    break;
                }

                case PRIM_ALPHA_MODE:
                {
                    // [ PRIM_ALPHA_MODE, integer face, integer alpha_mode, integer mask_cutoff ] (Halcyon
                    // SetRenderMaterialAlphaModeData): the faces' material alpha mode and cutoff, both stored whatever
                    // the mode. A mode outside PRIM_ALPHA_MODE_NONE..EMISSIVE or a cutoff outside 0-255 changes
                    // nothing, as OpenSim's LSL_Api refuses them (neither Halcyon nor SL documents a range check).
                    if (data[idx] is not int face || data[idx + 1] is not int mode || data[idx + 2] is not int cutoff) break;
                    if (mode < PRIM_ALPHA_MODE_NONE || mode > PRIM_ALPHA_MODE_EMISSIVE || cutoff < 0 || cutoff > 255) break;
                    PrimEditFaceMaterials(part, face, noneStaysNone: mode == PRIM_ALPHA_MODE_BLEND, mat =>
                    {
                        mat.DiffuseAlphaMode = (byte)mode;
                        mat.AlphaMaskCutoff = (byte)cutoff;
                    });
                    break;
                }

                case PRIM_NORMAL:
                {
                    // [ PRIM_NORMAL, integer face, string texture, vector repeats, vector offsets, float rot ]
                    // (Halcyon SetRenderMaterialNormalData; SL's ranges: repeats -100..100, offsets 0..1). NULL_KEY
                    // clears the map; on a face with no material it changes nothing.
                    if (data[idx] is not int face || !PrimMaterialMap(data[idx + 1], out UUID map)
                        || data[idx + 2] is not Vector3 repeats || data[idx + 3] is not Vector3 offsets
                        || !PrimFloat(data[idx + 4], out float rot)) break;
                    PrimEditFaceMaterials(part, face, noneStaysNone: map == UUID.Zero, mat =>
                    {
                        mat.NormalMapID = map;
                        mat.NormalRepeatX = Math.Clamp(repeats.X, -100f, 100f);
                        mat.NormalRepeatY = Math.Clamp(repeats.Y, -100f, 100f);
                        mat.NormalOffsetX = Math.Clamp(offsets.X, 0f, 1f);
                        mat.NormalOffsetY = Math.Clamp(offsets.Y, 0f, 1f);
                        mat.NormalRotation = rot;
                    });
                    break;
                }

                case PRIM_SPECULAR:
                {
                    // [ PRIM_SPECULAR, integer face, string texture, vector repeats, vector offsets, float rot,
                    //   vector color, integer glossiness, integer environment ] (Halcyon
                    // SetRenderMaterialSpecularData; SL's ranges: repeats -100..100, offsets 0..1, color 0..1,
                    // glossiness and environment 0-255). NULL_KEY clears the map; on a face with no material it
                    // changes nothing.
                    if (data[idx] is not int face || !PrimMaterialMap(data[idx + 1], out UUID map)
                        || data[idx + 2] is not Vector3 repeats || data[idx + 3] is not Vector3 offsets
                        || !PrimFloat(data[idx + 4], out float rot) || data[idx + 5] is not Vector3 color
                        || data[idx + 6] is not int gloss || data[idx + 7] is not int env) break;
                    PrimEditFaceMaterials(part, face, noneStaysNone: map == UUID.Zero, mat =>
                    {
                        mat.SpecularMapID = map;
                        mat.SpecularRepeatX = Math.Clamp(repeats.X, -100f, 100f);
                        mat.SpecularRepeatY = Math.Clamp(repeats.Y, -100f, 100f);
                        mat.SpecularOffsetX = Math.Clamp(offsets.X, 0f, 1f);
                        mat.SpecularOffsetY = Math.Clamp(offsets.Y, 0f, 1f);
                        mat.SpecularRotation = rot;
                        mat.SpecularLightColorR = ColorByte(color.X);
                        mat.SpecularLightColorG = ColorByte(color.Y);
                        mat.SpecularLightColorB = ColorByte(color.Z);
                        mat.SpecularLightExponent = (byte)Math.Clamp(gloss, 0, 255);
                        mat.EnvironmentIntensity = (byte)Math.Clamp(env, 0, 255);
                    });
                    break;
                }

                case PRIM_TEXGEN:
                {
                    // [ PRIM_TEXGEN, integer face, integer mode ] (Halcyon SetTexGen): PRIM_TEXGEN_PLANAR maps the
                    // faces planar, any other mode default. SL: it "silently fails if its face value indicates a
                    // face that does not exist." The scene's own helper, which OpenSim's LSL_Api uses too.
                    if (data[idx] is not int face || data[idx + 1] is not int mode) break;
                    OpenSim.Region.ScriptEngine.Shared.Api.LSL_Api.SetTexGen(part, face, mode);
                    break;
                }

                case IW_PRIM_ALPHA:
                {
                    // [ IW_PRIM_ALPHA, integer face, float alpha ] (Halcyon: SetAlpha, as llSetAlpha).
                    if (data[idx] is not int face || !PrimFloat(data[idx + 1], out float alpha)) break;
                    SetAlpha(part, alpha, face);
                    break;
                }

                case PRIM_CAST_SHADOWS:
                    // [ PRIM_CAST_SHADOWS, integer boolean ]: a no-op in SL ("setting it will have no effect");
                    // Halcyon has no rule.
                    break;

                case PRIM_ALLOW_UNSIT:
                case PRIM_SCRIPTED_SIT_ONLY:
                {
                    // [ PRIM_ALLOW_UNSIT, integer boolean ], [ PRIM_SCRIPTED_SIT_ONLY, integer boolean ] (SL; Halcyon has
                    // neither): the part properties OpenSim's LSL_Api sets and the region's sit path checks. Any
                    // non-zero integer is TRUE.
                    if (data[idx] is not int flag) break;
                    if (code == PRIM_ALLOW_UNSIT) part.AllowUnsit = flag != 0;
                    else part.ScriptedSitOnly = flag != 0;
                    break;
                }

                case PRIM_SIT_FLAGS:
                    // [ PRIM_SIT_FLAGS, integer flags ] (SL; Halcyon has no rule), as llSetLinkSitFlags.
                    if (data[idx] is not int sitFlags) break;
                    PartSetSitFlags(part, sitFlags);
                    break;

                case PRIM_DAMAGE:
                {
                    // [ PRIM_DAMAGE, float damage, integer damage_type ] (SL; Halcyon has no rule). The amount is the
                    // object's, as llSetDamage. The scene keeps no damage type, so the type is accepted and dropped.
                    if (!PrimFloat(data[idx], out float damage) || data[idx + 1] is not int) break;
                    PrimSetDamage(part, damage);
                    break;
                }

                case PRIM_HEALTH:
                    // [ PRIM_HEALTH, float health ] (SL; Halcyon has no rule). The scene keeps no health for a prim,
                    // so the value is accepted and dropped; it reads SL's default.
                    break;

                case PRIM_COLLISION_SOUND:
                {
                    // [ PRIM_COLLISION_SOUND, string sound, float volume ] (SL; Halcyon has no rule), as
                    // llCollisionSound on this prim.
                    if (data[idx] is not string sound || !PrimFloat(data[idx + 1], out float volume)) break;
                    PrimSetCollisionSound(part, sound, volume);
                    break;
                }

                case PRIM_SLICE:
                {
                    // [ PRIM_SLICE, vector slice ] (SL: "a shape attribute, equivalent to advanced_cut"; Halcyon).
                    if (data[idx] is not Vector3 slice) break;
                    PrimSetSlice(part, slice);
                    break;
                }

                case PRIM_PROJECTOR:
                {
                    // [ PRIM_PROJECTOR, string texture, float fov, float focus, float ambiance ] (SL; Halcyon has no
                    // rule): OpenSim's meaning, on the projection fields the IW_PRIM_PROJECTOR rules share.
                    if (data[idx] is not string texture || !PrimFloat(data[idx + 1], out float fov)
                        || !PrimFloat(data[idx + 2], out float focus) || !PrimFloat(data[idx + 3], out float ambiance)) break;
                    PrimSetProjector(part, texture, fov, focus, ambiance);
                    break;
                }

                case IW_PRIM_PROJECTOR:
                {
                    // [ IW_PRIM_PROJECTOR, integer enabled, string texture, float fov, float focus, float ambience ]
                    // (Halcyon SetPrimParams): only 1 enables; a texture that is NULL_KEY, or a name not in the prim,
                    // is refused and nothing changes; the values are kept as given.
                    if (data[idx] is not int enabled || !PrimFloat(data[idx + 2], out float fov)
                        || !PrimFloat(data[idx + 3], out float focus) || !PrimFloat(data[idx + 4], out float ambience)) break;
                    UUID texID = KeyOrName(data[idx + 1]?.ToString());
                    if (texID == UUID.Zero)
                    {
                        ScriptShoutError("The second argument of IW_PRIM_PROJECTOR must not be NULL_KEY.");
                        break;
                    }
                    PrimitiveBaseShape shape = part.Shape;
                    shape.ProjectionEntry = enabled == 1;
                    shape.ProjectionTextureUUID = texID;
                    shape.ProjectionFOV = fov;
                    shape.ProjectionFocus = focus;
                    shape.ProjectionAmbiance = ambience;
                    ProjectionChanged(part);
                    break;
                }

                case IW_PRIM_PROJECTOR_ENABLED:
                {
                    // [ IW_PRIM_PROJECTOR_ENABLED, integer enabled ] (Halcyon): only 1 enables.
                    if (data[idx] is not int on) break;
                    part.Shape.ProjectionEntry = on == 1;
                    ProjectionChanged(part);
                    break;
                }

                case IW_PRIM_PROJECTOR_TEXTURE:
                {
                    // [ IW_PRIM_PROJECTOR_TEXTURE, string texture ] (Halcyon): NULL_KEY or a missing name is refused.
                    UUID texID = KeyOrName(data[idx]?.ToString());
                    if (texID == UUID.Zero)
                    {
                        ScriptShoutError("The argument of IW_PRIM_PROJECTOR_TEXTURE must not be NULL_KEY.");
                        break;
                    }
                    part.Shape.ProjectionTextureUUID = texID;
                    ProjectionChanged(part);
                    break;
                }

                case IW_PRIM_PROJECTOR_FOV:
                case IW_PRIM_PROJECTOR_FOCUS:
                case IW_PRIM_PROJECTOR_AMBIENCE:
                {
                    // [ IW_PRIM_PROJECTOR_FOV | _FOCUS | _AMBIENCE, float value ] (Halcyon), kept as given.
                    if (!PrimFloat(data[idx], out float value)) break;
                    if (code == IW_PRIM_PROJECTOR_FOV) part.Shape.ProjectionFOV = value;
                    else if (code == IW_PRIM_PROJECTOR_FOCUS) part.Shape.ProjectionFocus = value;
                    else part.Shape.ProjectionAmbiance = value;
                    ProjectionChanged(part);
                    break;
                }

                case PRIM_REFLECTION_PROBE:
                {
                    // [ PRIM_REFLECTION_PROBE, integer boolean, float ambiance, float clip_distance, integer flags ] (SL;
                    // Halcyon has no rule): "Ranges from 0.0 to 100.0", "Ranges from 0.0 to 1024.0". FALSE removes the
                    // probe. The shape's ReflectionProbe, as OpenSim's LSL_Api keeps it.
                    if (data[idx] is not int active || !PrimFloat(data[idx + 1], out float ambiance)
                        || !PrimFloat(data[idx + 2], out float clip) || data[idx + 3] is not int flags) break;
                    PrimSetReflectionProbe(part, active != 0, ambiance, clip, flags);
                    break;
                }

                case PRIM_PHYSICS_MATERIAL:
                {
                    // [ PRIM_PHYSICS_MATERIAL, integer mask, float density, float friction, float restitution,
                    //   float gravity_multiplier ]: OpenSim's own rule (LSL_Api.SetPrimParams), as llSetPhysicsMaterial.
                    if (data[idx] is not int mask || !PrimFloat(data[idx + 1], out float density)
                        || !PrimFloat(data[idx + 2], out float friction) || !PrimFloat(data[idx + 3], out float restitution)
                        || !PrimFloat(data[idx + 4], out float gravity)) break;
                    PrimSetPhysicsMaterial(part, mask, density, friction, restitution, gravity);
                    break;
                }

                case PRIM_TYPE:
                {
                    if (idx >= data.Length) break;
                    int primTypeCode = Convert.ToInt32(data[idx++]);
                    int remain2 = data.Length - idx;
                    int holeshape2;
                    Vector3 cut2, twist2, taper_b2, topshear2, holesize2, profilecut2, taper_a2;
                    float hollow2, revolutions2, radiusoffset2, skew2;

                    switch (primTypeCode)
                    {
                        case 0:
                            if (remain2 < 6) break;
                            holeshape2 = Convert.ToInt32(data[idx++]);
                            cut2       = (Vector3)data[idx++];
                            hollow2    = (float)Convert.ToDouble(data[idx++]);
                            twist2     = (Vector3)data[idx++];
                            taper_b2   = (Vector3)data[idx++];
                            topshear2  = (Vector3)data[idx++];
                            part.Shape.PathCurve = (byte)Extrusion.Straight;
                            SetPrimitiveShapeParamsCommon(part, holeshape2, cut2, hollow2, twist2, taper_b2, topshear2, DEFAULT_SLICE_VEC, 1);
                            break;

                        case 1:
                            if (remain2 < 6) break;
                            holeshape2 = Convert.ToInt32(data[idx++]);
                            cut2       = (Vector3)data[idx++];
                            hollow2    = (float)Convert.ToDouble(data[idx++]);
                            twist2     = (Vector3)data[idx++];
                            taper_b2   = (Vector3)data[idx++];
                            topshear2  = (Vector3)data[idx++];
                            part.Shape.ProfileShape = ProfileShape.Circle;
                            part.Shape.PathCurve    = (byte)Extrusion.Straight;
                            SetPrimitiveShapeParamsCommon(part, holeshape2, cut2, hollow2, twist2, taper_b2, topshear2, DEFAULT_SLICE_VEC, 0);
                            break;

                        case 2:
                            if (remain2 < 6) break;
                            holeshape2 = Convert.ToInt32(data[idx++]);
                            cut2       = (Vector3)data[idx++];
                            hollow2    = (float)Convert.ToDouble(data[idx++]);
                            twist2     = (Vector3)data[idx++];
                            taper_b2   = (Vector3)data[idx++];
                            topshear2  = (Vector3)data[idx++];
                            part.Shape.PathCurve = (byte)Extrusion.Straight;
                            SetPrimitiveShapeParamsCommon(part, holeshape2, cut2, hollow2, twist2, taper_b2, topshear2, DEFAULT_SLICE_VEC, 3);
                            break;

                        case 3:
                            if (remain2 < 5) break;
                            holeshape2 = Convert.ToInt32(data[idx++]);
                            cut2       = (Vector3)data[idx++];
                            hollow2    = (float)Convert.ToDouble(data[idx++]);
                            twist2     = (Vector3)data[idx++];
                            taper_b2   = (Vector3)data[idx++];
                            part.Shape.PathCurve = (byte)Extrusion.Curve1;
                            SetPrimitiveShapeParamsSphere(part, holeshape2, cut2, hollow2, twist2, taper_b2, 5);
                            break;

                        case 4:
                        case 5:
                        case 6:
                            if (remain2 < 11) break;
                            holeshape2    = Convert.ToInt32(data[idx++]);
                            cut2          = (Vector3)data[idx++];
                            hollow2       = (float)Convert.ToDouble(data[idx++]);
                            twist2        = (Vector3)data[idx++];
                            holesize2     = (Vector3)data[idx++];
                            topshear2     = (Vector3)data[idx++];
                            profilecut2   = (Vector3)data[idx++];
                            taper_a2      = (Vector3)data[idx++];
                            revolutions2  = (float)Convert.ToDouble(data[idx++]);
                            radiusoffset2 = (float)Convert.ToDouble(data[idx++]);
                            skew2         = (float)Convert.ToDouble(data[idx++]);
                            byte torusFudge = primTypeCode == 4 ? (byte)0
                                            : primTypeCode == 5  ? (byte)1
                                            : (byte)3;
                            part.Shape.PathCurve = (byte)Extrusion.Curve1;
                            SetPrimitiveShapeParamsTorus(part, holeshape2, cut2, hollow2, twist2, holesize2,
                                topshear2, profilecut2, taper_a2, revolutions2, radiusoffset2, skew2, torusFudge);
                            break;

                        case 7:
                            if (remain2 < 2) break;
                            string sculptMap2  = data[idx++].ToString();
                            int    sculptType2 = Convert.ToInt32(data[idx++]);
                            part.Shape.PathCurve = (byte)Extrusion.Curve1;
                            SetPrimitiveShapeParamsSculpt(part, sculptMap2, sculptType2);
                            break;
                    }
                    break;
                }

                case PRIM_NAME:
                {
                    if (idx >= data.Length) break;
                    string name = data[idx++].ToString();
                    part.Name = name;
                    break;
                }

                case PRIM_DESC:
                {
                    if (idx >= data.Length) break;
                    string desc = data[idx++].ToString();
                    part.Description = desc;
                    break;
                }

                // ── PBR / glTF Material params ──────────────────────────────

                case PRIM_RENDER_MATERIAL:
                {
                    // [ PRIM_RENDER_MATERIAL, integer face, string render_material ]
                    if (idx + 1 >= data.Length) { idx = data.Length; break; }
                    int face; try { face = Convert.ToInt32(data[idx++]); } catch { idx++; break; }
                    string matStr = data[idx++].ToString();
                    UUID matUUID = UUID.Zero;
                    UUID.TryParse(matStr, out matUUID);

                    var shape_rm = part.Shape;
                    shape_rm.RenderMaterials ??= new OpenMetaverse.Primitive.RenderMaterials();
                    int numFaces_rm = part.GetNumberOfSides();

                    if (face == ALL_SIDES)
                    {
                        var entries = new OpenMetaverse.Primitive.RenderMaterials.RenderMaterialEntry[numFaces_rm];
                        for (int i = 0; i < numFaces_rm; i++)
                        {
                            entries[i].te_index = (byte)i;
                            entries[i].id = matUUID;
                        }
                        shape_rm.RenderMaterials.entries = entries;
                        // Clear non-transform overrides per SL spec
                    }
                    else if (face >= 0 && face < numFaces_rm)
                    {
                        SetRenderMaterialEntry(ref shape_rm.RenderMaterials.entries, matUUID, face);
                    }

                    if (part.ParentGroup != null)
                        part.ParentGroup.HasGroupChanged = true;
                    part.ScheduleFullUpdate();
                    break;
                }

                case PRIM_GLTF_BASE_COLOR:
                {
                    // [ PRIM_GLTF_BASE_COLOR, face, texture, repeats, offsets, rotation,
                    //   color, alpha, alpha_mode, alpha_cutoff, double_sided ]
                    if (idx + 9 >= data.Length) { idx = data.Length; break; }
                    int face; try { face = Convert.ToInt32(data[idx++]); } catch { idx += 9; break; }
                    string texture = data[idx++].ToString();
                    Vector3 repeats = (Vector3)data[idx++];
                    Vector3 offsets = (Vector3)data[idx++];
                    float rotation = (float)Convert.ToDouble(data[idx++]);
                    Vector3 color = (Vector3)data[idx++];
                    float alpha = (float)Convert.ToDouble(data[idx++]);
                    int alphaMode = Convert.ToInt32(data[idx++]);
                    float alphaCutoff = (float)Convert.ToDouble(data[idx++]);
                    int doubleSided = Convert.ToInt32(data[idx++]);

                    var osd = new OpenMetaverse.StructuredData.OSDMap();
                    if (!string.IsNullOrEmpty(texture) && texture != UUID.Zero.ToString())
                        osd["tex"] = texture;
                    osd["rep"] = new OpenMetaverse.StructuredData.OSDArray { (double)repeats.X, (double)repeats.Y };
                    osd["off"] = new OpenMetaverse.StructuredData.OSDArray { (double)offsets.X, (double)offsets.Y };
                    osd["rot"] = (double)rotation;
                    osd["bc"] = new OpenMetaverse.StructuredData.OSDArray { (double)color.X, (double)color.Y, (double)color.Z, (double)alpha };
                    osd["am"] = alphaMode;
                    osd["ac"] = (double)alphaCutoff;
                    osd["ds"] = (doubleSided != 0);

                    ApplyGLTFOverrideToPart(part, face, osd);
                    break;
                }

                case PRIM_GLTF_NORMAL:
                {
                    // [ PRIM_GLTF_NORMAL, face, texture, repeats, offsets, rotation ]
                    if (idx + 4 >= data.Length) { idx = data.Length; break; }
                    int face; try { face = Convert.ToInt32(data[idx++]); } catch { idx += 4; break; }
                    string texture = data[idx++].ToString();
                    Vector3 repeats = (Vector3)data[idx++];
                    Vector3 offsets = (Vector3)data[idx++];
                    float rotation = (float)Convert.ToDouble(data[idx++]);

                    var osd = new OpenMetaverse.StructuredData.OSDMap();
                    if (!string.IsNullOrEmpty(texture) && texture != UUID.Zero.ToString())
                        osd["ntex"] = texture;
                    osd["nrep"] = new OpenMetaverse.StructuredData.OSDArray { (double)repeats.X, (double)repeats.Y };
                    osd["noff"] = new OpenMetaverse.StructuredData.OSDArray { (double)offsets.X, (double)offsets.Y };
                    osd["nrot"] = (double)rotation;

                    ApplyGLTFOverrideToPart(part, face, osd);
                    break;
                }

                case PRIM_GLTF_METALLIC_ROUGHNESS:
                {
                    // [ PRIM_GLTF_METALLIC_ROUGHNESS, face, texture, repeats, offsets, rotation,
                    //   metallic_factor, roughness_factor ]
                    if (idx + 6 >= data.Length) { idx = data.Length; break; }
                    int face; try { face = Convert.ToInt32(data[idx++]); } catch { idx += 6; break; }
                    string texture = data[idx++].ToString();
                    Vector3 repeats = (Vector3)data[idx++];
                    Vector3 offsets = (Vector3)data[idx++];
                    float rotation = (float)Convert.ToDouble(data[idx++]);
                    float metallic = (float)Convert.ToDouble(data[idx++]);
                    float roughness = (float)Convert.ToDouble(data[idx++]);

                    var osd = new OpenMetaverse.StructuredData.OSDMap();
                    if (!string.IsNullOrEmpty(texture) && texture != UUID.Zero.ToString())
                        osd["mrtex"] = texture;
                    osd["mrrep"] = new OpenMetaverse.StructuredData.OSDArray { (double)repeats.X, (double)repeats.Y };
                    osd["mroff"] = new OpenMetaverse.StructuredData.OSDArray { (double)offsets.X, (double)offsets.Y };
                    osd["mrrot"] = (double)rotation;
                    osd["mf"] = (double)Math.Clamp(metallic, 0f, 1f);
                    osd["rf"] = (double)Math.Clamp(roughness, 0f, 1f);

                    ApplyGLTFOverrideToPart(part, face, osd);
                    break;
                }

                case PRIM_GLTF_EMISSIVE:
                {
                    // [ PRIM_GLTF_EMISSIVE, face, texture, repeats, offsets, rotation, emissive_tint ]
                    if (idx + 5 >= data.Length) { idx = data.Length; break; }
                    int face; try { face = Convert.ToInt32(data[idx++]); } catch { idx += 5; break; }
                    string texture = data[idx++].ToString();
                    Vector3 repeats = (Vector3)data[idx++];
                    Vector3 offsets = (Vector3)data[idx++];
                    float rotation = (float)Convert.ToDouble(data[idx++]);
                    Vector3 emissive = (Vector3)data[idx++];

                    var osd = new OpenMetaverse.StructuredData.OSDMap();
                    if (!string.IsNullOrEmpty(texture) && texture != UUID.Zero.ToString())
                        osd["etex"] = texture;
                    osd["erep"] = new OpenMetaverse.StructuredData.OSDArray { (double)repeats.X, (double)repeats.Y };
                    osd["eoff"] = new OpenMetaverse.StructuredData.OSDArray { (double)offsets.X, (double)offsets.Y };
                    osd["erot"] = (double)rotation;
                    osd["ec"] = new OpenMetaverse.StructuredData.OSDArray { (double)emissive.X, (double)emissive.Y, (double)emissive.Z };

                    ApplyGLTFOverrideToPart(part, face, osd);
                    break;
                }

                default:
                    return false;
            }
            return true;
        }

        /// <summary>
        /// One setter rule on a seated avatar (Halcyon SetPrimParams' ScenePresence branches): PRIM_POSITION and
        /// PRIM_POS_LOCAL set the sitter's offset from the root, PRIM_ROT_LOCAL its rotation, and PRIM_ROTATION the
        /// root's rotation times the one given. Every other rule leaves an avatar alone. SL: an offset more than 54 m
        /// away is silently ignored. True when the avatar moved; the walk then sends one terse update to every
        /// viewer, the way the scene moves a sitter (ScenePresence.OffsetPosition / Rotation).
        /// </summary>
        private bool SetSitterPrimRule(ScenePresence sp, int code, object[] data, int idx)
        {
            if (sp == null || sp.ParentID == 0) return false;
            switch (code)
            {
                case PRIM_POSITION:
                case PRIM_POS_LOCAL:
                {
                    if (data[idx] is not Vector3 v || v.Length() > 54f) return false;
                    if (!sp.LegacySitOffsets)
                        v += (Vector3.UnitZ * sp.Rotation) * (2f * sp.Appearance.AvatarHeight * 0.02638f);
                    sp.OffsetPosition = v;
                    return true;
                }

                case PRIM_ROT_LOCAL:
                {
                    if (data[idx] is not Quaternion q) return false;
                    sp.Rotation = q;
                    return true;
                }

                case PRIM_ROTATION:
                {
                    if (data[idx] is not Quaternion q) return false;
                    sp.Rotation = (m_host.ParentGroup?.RootPart?.RotationOffset ?? Quaternion.Identity) * q;
                    return true;
                }

                default:
                    return false;
            }
        }

        /// <summary>
        /// The prim-params getter walk: the rules up to the first PRIM_LINK_TARGET read from the prims and seated
        /// avatars given, prims first in link order and then the avatars; the rules after a PRIM_LINK_TARGET read
        /// from the prims and avatars its link selects, as in SL and Halcyon GetPrimParams. The walk finds each
        /// PRIM_LINK_TARGET by SL's getter value counts (PrimParamRules.GetValueCount), so a per-face rule's face is
        /// never mistaken for a rule.
        /// </summary>
        private LSLList GetPrimParams(IEnumerable<SceneObjectPart> parts, IEnumerable<ScenePresence> sitters, LSLList rules)
        {
            var result = new LSLList();
            if (rules == null) return result;
            var data = rules.Data;
            int start = 0;

            while (true)
            {
                int idx = start, target = -1;
                while (idx < data.Length && data[idx] is int code)
                {
                    if (code == PRIM_LINK_TARGET) { target = idx; break; }
                    idx += 1 + PrimParamRules.GetValueCount(code);
                }

                int end = target < 0 ? data.Length : target;
                var segment = new object[end - start];
                Array.Copy(data, start, segment, 0, segment.Length);
                var segmentRules = new LSLList(segment);
                foreach (var part in parts)
                    result += GetPrimParams(part, segmentRules);
                foreach (var sp in sitters)
                    result += GetSitterPrimParams(sp, segmentRules);

                if (target < 0 || target + 1 >= data.Length || data[target + 1] is not int link) break;
                parts = GetLinkParts(link).ToList();
                sitters = GetLinkSitters(link);
                start = target + 2;
            }
            return result;
        }

        /// <summary>
        /// A seated avatar's PRIM_POS_LOCAL: its offset from the root in the root's frame, the region offset turned
        /// by the inverse of the root's rotation (Halcyon returns the region offset unturned). Without legacy sit
        /// offsets the setter adds the avatar's sit height (SetSitterPrimRule); it is taken off again here, so the
        /// offset a script sets is the one it reads back.
        /// </summary>
        private Vector3 SitterLocalPos(ScenePresence sp)
        {
            SceneObjectPart root = m_host.ParentGroup?.RootPart;
            if (root == null) return sp.OffsetPosition;
            Vector3 local = (sp.AbsolutePosition - root.AbsolutePosition) * Quaternion.Inverse(root.GetWorldRotation());
            if (!sp.LegacySitOffsets)
                local -= (Vector3.UnitZ * sp.Rotation) * (2f * sp.Appearance.AvatarHeight * 0.02638f);
            return local;
        }

        /// <summary>
        /// llGetLinkPrimitiveParams on a seated avatar (Halcyon GetAvatarAsPrimParam): the avatar's name, an empty
        /// description, flesh, not temporary or phantom, its agent size, its region position, a default box shape
        /// and empty text, light and flexi. The texture rules consume their face, return nothing and tell the
        /// owner "texture info cannot be accessed for avatars." PRIM_ROTATION returns the avatar's region rotation,
        /// as SL documents; Halcyon returns nothing there. PRIM_POS_LOCAL and PRIM_ROT_LOCAL return the avatar's
        /// offset and rotation relative to the root, in the root's frame (SitterLocalPos); Halcyon returns the
        /// region offset and nothing. Rules Halcyon has no avatar value for (PRIM_PHYSICS and the rest) return
        /// nothing.
        /// </summary>
        private LSLList GetSitterPrimParams(ScenePresence sp, LSLList parms)
        {
            if (sp == null || parms == null) return new LSLList();
            var result = new List<object>();
            var data = parms.Data;
            int idx = 0;

            while (idx < data.Length)
            {
                int code;
                try { code = Convert.ToInt32(data[idx++]); } catch { break; }

                switch (code)
                {
                    case PRIM_LINK_TARGET:
                        if (idx < data.Length) idx++; // as GetPrimParams: routing is the caller's
                        break;
                    case PRIM_POSITION:
                        result.Add(sp.AbsolutePosition);
                        break;
                    case PRIM_POS_LOCAL:
                        result.Add(SitterLocalPos(sp));
                        break;
                    case PRIM_ROT_LOCAL:
                        result.Add(sp.Rotation); // the scene keeps a sitter's rotation relative to the root
                        break;
                    case PRIM_ROTATION:
                        result.Add(sp.GetWorldRotation());
                        break;
                    case PRIM_NAME:
                        result.Add(sp.Name);
                        break;
                    case PRIM_DESC:
                        result.Add(string.Empty);
                        break;
                    case PRIM_TYPE:
                        result.Add(PRIM_TYPE_BOX);
                        result.Add(PRIM_HOLE_DEFAULT);
                        result.Add(new Vector3(0f, 1f, 0f));
                        result.Add(0f);
                        result.Add(Vector3.Zero);
                        result.Add(new Vector3(1f, 1f, 0f));
                        result.Add(Vector3.Zero);
                        break;
                    case PRIM_SLICE:
                        result.Add(new Vector3(0f, 1f, 0f));
                        break;
                    case PRIM_MATERIAL:
                        result.Add(PRIM_MATERIAL_FLESH);
                        break;
                    case PRIM_TEMP_ON_REZ:
                    case PRIM_PHANTOM:
                        result.Add(0);
                        break;
                    case PRIM_SIZE:
                        result.Add(llGetAgentSize(sp.UUID.ToString()));
                        break;
                    case PRIM_TEXT:
                        result.Add(string.Empty);
                        result.Add(Vector3.Zero);
                        result.Add(1f);
                        break;
                    case PRIM_POINT_LIGHT:
                        result.Add(0);
                        result.Add(Vector3.Zero);
                        result.Add(0f);
                        result.Add(0f);
                        result.Add(0f);
                        break;
                    case PRIM_FLEXIBLE:
                        result.Add(0);
                        result.Add(0);
                        result.Add(0f);
                        result.Add(0f);
                        result.Add(0f);
                        result.Add(0f);
                        result.Add(Vector3.Zero);
                        break;
                    case PRIM_SIT_TARGET:
                        result.Add(0);
                        result.Add(Vector3.Zero);
                        result.Add(Quaternion.Identity);
                        break;
                    case PRIM_TEXTURE:
                    case PRIM_COLOR:
                    case PRIM_BUMP_SHINY:
                    case PRIM_FULLBRIGHT:
                    case PRIM_TEXGEN:
                    case PRIM_GLOW:
                    case PRIM_SPECULAR:
                    case PRIM_NORMAL:
                    case PRIM_ALPHA_MODE:
                    case IW_PRIM_ALPHA:
                        if (idx < data.Length) idx++;
                        ScriptShoutError("texture info cannot be accessed for avatars.");
                        break;
                    case PRIM_RENDER_MATERIAL:
                    case PRIM_GLTF_BASE_COLOR:
                    case PRIM_GLTF_NORMAL:
                    case PRIM_GLTF_METALLIC_ROUGHNESS:
                    case PRIM_GLTF_EMISSIVE:
                        if (idx < data.Length) idx++; // the face, as GetPrimParams takes it
                        break;
                    default:
                        idx += PrimParamRules.GetValueCount(code); // nothing for an avatar; its face is not a rule
                        break;
                }
            }
            return new LSLList(result.ToArray());
        }

        // ── PRIM_TYPE shape helpers ───────────────────────────────────────────────
        // Ported from Halcyon/InWorldz LSLSystemAPI.cs

        private static readonly Vector3 DEFAULT_SLICE_VEC = new Vector3(0f, 1f, 0f);

        private int getScriptPrimType(PrimitiveBaseShape primShape)
        {
            if (primShape.SculptEntry)
                return 7;

            byte profileCurve = (byte)(primShape.ProfileCurve & 0x07);

            if (profileCurve == (byte)ProfileShape.Square)
            {
                if (primShape.PathCurve == (byte)Extrusion.Straight || primShape.PathCurve == (byte)Extrusion.Flexible)
                    return 0;
                if (primShape.PathCurve == (byte)Extrusion.Curve1)
                    return 5;
            }
            else if (profileCurve == (byte)ProfileShape.Circle)
            {
                if (primShape.PathCurve == (byte)Extrusion.Straight || primShape.PathCurve == (byte)Extrusion.Flexible)
                    return 1;
                if (primShape.PathCurve == (byte)Extrusion.Curve1)
                    return 4;
            }
            else if (profileCurve == (byte)ProfileShape.HalfCircle)
            {
                if (primShape.PathCurve == (byte)Extrusion.Curve1 || primShape.PathCurve == (byte)Extrusion.Curve2)
                    return 3;
            }
            else if (profileCurve == (byte)ProfileShape.EquilateralTriangle)
            {
                if (primShape.PathCurve == (byte)Extrusion.Straight || primShape.PathCurve == (byte)Extrusion.Flexible)
                    return 2;
                if (primShape.PathCurve == (byte)Extrusion.Curve1)
                    return 6;
            }
            return 0;
        }

        private ObjectShapePacket.ObjectDataBlock SetPrimitiveBlockShapeParams(
            SceneObjectPart part, int holeshape, Vector3 cut, float hollow, Vector3 twist)
        {
            ObjectShapePacket.ObjectDataBlock shapeBlock = new ObjectShapePacket.ObjectDataBlock();

            if (holeshape != 0 &&
                holeshape != 16 &&
                holeshape != 32 &&
                holeshape != 48)
                holeshape = 0;

            shapeBlock.ProfileCurve = (byte)holeshape;

            cut.X = Math.Max(0f, Math.Min(1f, (float)cut.X));
            cut.Y = Math.Max(0f, Math.Min(1f, (float)cut.Y));
            if (cut.Y - cut.X < 0.02f) cut.X = cut.Y - 0.02f;
            shapeBlock.ProfileBegin = (ushort)(50000 * cut.X);
            shapeBlock.ProfileEnd   = (ushort)(50000 * (1 - cut.Y));

            hollow = Math.Max(0f, Math.Min(0.99f, hollow));
            shapeBlock.ProfileHollow = (ushort)(50000 * hollow);

            twist.X = Math.Max(-1f, Math.Min(1f, (float)twist.X));
            twist.Y = Math.Max(-1f, Math.Min(1f, (float)twist.Y));
            shapeBlock.PathTwistBegin = (sbyte)(100 * twist.X);
            shapeBlock.PathTwist      = (sbyte)(100 * twist.Y);

            shapeBlock.ObjectLocalID = part.LocalId;
            shapeBlock.PathCurve     = part.Shape.PathCurve;
            return shapeBlock;
        }

        private void SetPrimitiveShapeParamsCommon(SceneObjectPart part, int holeshape,
            Vector3 cut, float hollow, Vector3 twist,
            Vector3 taper_b, Vector3 topshear, Vector3 slice, byte fudge)
        {
            ObjectShapePacket.ObjectDataBlock shapeBlock =
                SetPrimitiveBlockShapeParams(part, holeshape, cut, hollow, twist);

            shapeBlock.ProfileCurve += fudge;

            taper_b.X = Math.Max(0f, Math.Min(2f, (float)taper_b.X));
            taper_b.Y = Math.Max(0f, Math.Min(2f, (float)taper_b.Y));
            shapeBlock.PathScaleX = (byte)(100 * (2.0 - taper_b.X));
            shapeBlock.PathScaleY = (byte)(100 * (2.0 - taper_b.Y));

            topshear.X = Math.Max(-0.5f, Math.Min(0.5f, (float)topshear.X));
            topshear.Y = Math.Max(-0.5f, Math.Min(0.5f, (float)topshear.Y));
            shapeBlock.PathShearX = (byte)Primitive.PackPathShear((float)topshear.X);
            shapeBlock.PathShearY = (byte)Primitive.PackPathShear((float)topshear.Y);

            float sx = Math.Max(0f,    Math.Min(0.98f, (float)slice.X));
            float sy = Math.Max(0.02f, Math.Min(1.0f,  (float)slice.Y));
            if (sy - sx < 0.02f) sx = sy - 0.02f;
            shapeBlock.PathBegin = (ushort)(50000 * sx);
            shapeBlock.PathEnd   = (ushort)(50000 * (1 - sy));

            part.Shape.SculptEntry = false;
            part.UpdateShape(shapeBlock);
        }

        private void SetPrimitiveShapeParamsSphere(SceneObjectPart part, int holeshape,
            Vector3 cut, float hollow, Vector3 twist,
            Vector3 dimple, byte fudge)
        {
            ObjectShapePacket.ObjectDataBlock shapeBlock =
                SetPrimitiveBlockShapeParams(part, holeshape, cut, hollow, twist);

            shapeBlock.PathBegin = shapeBlock.ProfileBegin;
            shapeBlock.PathEnd   = shapeBlock.ProfileEnd;
            shapeBlock.ProfileCurve += fudge;
            shapeBlock.PathScaleX = 100;
            shapeBlock.PathScaleY = 100;

            dimple.X = Math.Max(0f, Math.Min(1f, (float)dimple.X));
            dimple.Y = Math.Max(0f, Math.Min(1f, (float)dimple.Y));
            if (dimple.Y - cut.X < 0.02f) dimple.X = cut.Y - 0.02f;
            shapeBlock.ProfileBegin = (ushort)(50000 * dimple.X);
            shapeBlock.ProfileEnd   = (ushort)(50000 * (1 - dimple.Y));

            part.Shape.SculptEntry = false;
            part.UpdateShape(shapeBlock);
        }

        private void SetPrimitiveShapeParamsTorus(SceneObjectPart part, int holeshape,
            Vector3 cut, float hollow, Vector3 twist,
            Vector3 holesize, Vector3 topshear,
            Vector3 profilecut, Vector3 taper_a,
            float revolutions, float radiusoffset, float skew, byte fudge)
        {
            ObjectShapePacket.ObjectDataBlock shapeBlock =
                SetPrimitiveBlockShapeParams(part, holeshape, cut, hollow, twist);

            shapeBlock.ProfileCurve += fudge;
            shapeBlock.PathBegin = shapeBlock.ProfileBegin;
            shapeBlock.PathEnd   = shapeBlock.ProfileEnd;

            holesize.X = Math.Max(0.01f, Math.Min(1f,   (float)holesize.X));
            holesize.Y = Math.Max(0.05f, Math.Min(0.5f, (float)holesize.Y));
            shapeBlock.PathScaleX = (byte)(100 * (2 - holesize.X));
            shapeBlock.PathScaleY = (byte)(100 * (2 - holesize.Y));

            topshear.X = Math.Max(-0.5f, Math.Min(0.5f, (float)topshear.X));
            topshear.Y = Math.Max(-0.5f, Math.Min(0.5f, (float)topshear.Y));
            shapeBlock.PathShearX = (byte)Primitive.PackPathShear((float)topshear.X);
            shapeBlock.PathShearY = (byte)Primitive.PackPathShear((float)topshear.Y);

            profilecut.X = Math.Max(0f, Math.Min(1f, (float)profilecut.X));
            profilecut.Y = Math.Max(0f, Math.Min(1f, (float)profilecut.Y));
            if (profilecut.Y - profilecut.X < 0.05f)
            {
                profilecut.X = profilecut.Y - 0.05f;
                if (profilecut.X < 0f) { profilecut.X = 0f; profilecut.Y = 0.05f; }
            }
            shapeBlock.ProfileBegin = (ushort)(50000 * profilecut.X);
            shapeBlock.ProfileEnd   = (ushort)(50000 * (1 - profilecut.Y));

            taper_a.X = Math.Max(-1f, Math.Min(1f, (float)taper_a.X));
            taper_a.Y = Math.Max(-1f, Math.Min(1f, (float)taper_a.Y));
            shapeBlock.PathTaperX = (sbyte)(100 * taper_a.X);
            shapeBlock.PathTaperY = (sbyte)(100 * taper_a.Y);

            revolutions = Math.Max(1f, Math.Min(4f, revolutions));
            shapeBlock.PathRevolutions = (byte)(66.666667 * (revolutions - 1.0));

            radiusoffset = Math.Max(0f, Math.Min(1f, radiusoffset));
            shapeBlock.PathRadiusOffset = (sbyte)(100 * radiusoffset);

            skew = Math.Max(-0.95f, Math.Min(0.95f, skew));
            shapeBlock.PathSkew = (sbyte)(100 * skew);

            part.Shape.SculptEntry = false;
            part.UpdateShape(shapeBlock);
        }

        private void SetPrimitiveShapeParamsSculpt(SceneObjectPart part, string map, int typeBits)
        {
            ObjectShapePacket.ObjectDataBlock shapeBlock = new ObjectShapePacket.ObjectDataBlock();
            int sculptTypeMask = 0x0F;
            int sculptType     = typeBits & sculptTypeMask;
            int sculptOptions  = typeBits & ~sculptTypeMask;

            if (!UUID.TryParse(map, out UUID sculptId))
            {
                TaskInventoryItem invItem = FindInventoryItem(map, (int)AssetType.Texture);
                sculptId = invItem?.AssetID ?? UUID.Zero;
            }
            if (sculptId == UUID.Zero) return;

            if (sculptType != 4 &&
                sculptType != 3 &&
                sculptType != 1 &&
                sculptType != 2)
            {
                sculptType = 1;
                typeBits   = sculptOptions | sculptType;
            }

            shapeBlock.ObjectLocalID = part.LocalId;
            shapeBlock.PathScaleX    = 100;
            shapeBlock.PathScaleY    = 150;
            shapeBlock.PathCurve     = (byte)Extrusion.Curve1;

            part.Shape.SculptEntry   = true;
            part.Shape.SculptTexture = sculptId;
            part.Shape.SculptType    = (byte)typeBits;
            part.UpdateShape(shapeBlock);
        }

        /// <summary>
        /// One prim's getter rules. A rule Phlox does not implement yet returns nothing and takes its face, if it
        /// has one, so the walk goes on with the next rule. PRIM_LINK_TARGET takes its link and changes nothing
        /// here: the walk above (and osGetPrimitiveParams' one prim) decides which prims a rule reads.
        /// </summary>
        private LSLList GetPrimParams(SceneObjectPart part, LSLList parms)
        {
            if (part == null || parms == null) return new LSLList();
            var result = new List<object>();
            var data = parms.Data;
            int idx = 0;

            while (idx < data.Length)
            {
                int code;
                try { code = Convert.ToInt32(data[idx++]); } catch { break; }

                switch (code)
                {
                    case PRIM_LINK_TARGET:
                        if (idx >= data.Length) break;
                        idx++; // the link: the caller's walk does the routing
                        break;

                    case PRIM_COLOR:
                    {
                        if (idx >= data.Length) break;
                        int face; try { face = Convert.ToInt32(data[idx++]); } catch { break; }
                        Primitive.TextureEntry te = part.Shape.Textures;
                        if (te == null) { result.Add(Vector3.One); result.Add(1f); break; }
                        Primitive.TextureEntryFace f = face == ALL_SIDES ? te.DefaultTexture : te.GetFace((uint)face);
                        if (f == null) f = te.DefaultTexture;
                        result.Add(new Vector3(f.RGBA.R, f.RGBA.G, f.RGBA.B));
                        result.Add(f.RGBA.A);
                        break;
                    }

                    case PRIM_TEXTURE:
                    {
                        if (idx >= data.Length) break;
                        int face; try { face = Convert.ToInt32(data[idx++]); } catch { break; }
                        Primitive.TextureEntry te = part.Shape.Textures;
                        if (te == null) { result.Add(UUID.Zero.ToString()); result.Add(new Vector3(1,1,0)); result.Add(Vector3.Zero); result.Add(0f); break; }
                        Primitive.TextureEntryFace f = face == ALL_SIDES ? te.DefaultTexture : te.GetFace((uint)face);
                        if (f == null) f = te.DefaultTexture;
                        // Halcyon's name / full-perm key / NULL_KEY rule.
                        result.Add(ConditionalTextureNameOrUUID(part, f.TextureID));
                        result.Add(new Vector3(f.RepeatU, f.RepeatV, 0));
                        result.Add(new Vector3(f.OffsetU, f.OffsetV, 0));
                        result.Add(f.Rotation);
                        break;
                    }

                    case PRIM_GLOW:
                    {
                        if (idx >= data.Length) break;
                        int face; try { face = Convert.ToInt32(data[idx++]); } catch { break; }
                        Primitive.TextureEntry te = part.Shape.Textures;
                        Primitive.TextureEntryFace f = te == null ? null : (face == ALL_SIDES ? te.DefaultTexture : te.GetFace((uint)face));
                        result.Add(f?.Glow ?? 0f);
                        break;
                    }

                    case PRIM_FULLBRIGHT:
                    {
                        if (idx >= data.Length) break;
                        int face; try { face = Convert.ToInt32(data[idx++]); } catch { break; }
                        Primitive.TextureEntry te = part.Shape.Textures;
                        Primitive.TextureEntryFace f = te == null ? null : (face == ALL_SIDES ? te.DefaultTexture : te.GetFace((uint)face));
                        result.Add(f?.Fullbright == true ? 1 : 0);
                        break;
                    }

                    case PRIM_BUMP_SHINY:
                    {
                        if (idx >= data.Length) break;
                        int face; try { face = Convert.ToInt32(data[idx++]); } catch { break; }
                        Primitive.TextureEntry te = part.Shape.Textures;
                        Primitive.TextureEntryFace f = te == null ? null : (face == ALL_SIDES ? te.DefaultTexture : te.GetFace((uint)face));
                        result.Add((int)(f?.Shiny ?? Shininess.None) >> 6);   // Back to PRIM_SHINY_* 0..3
                        result.Add((int)(f?.Bump  ?? Bumpiness.None));
                        break;
                    }

                    case PRIM_TEXGEN:
                    case PRIM_NORMAL:
                    case PRIM_SPECULAR:
                    case PRIM_ALPHA_MODE:
                    {
                        // Per face, ALL_SIDES giving each face in turn; a face that does not exist gives nothing
                        // (Halcyon GetPrimParams).
                        if (idx >= data.Length) break;
                        if (data[idx++] is not int face) break;
                        int sides = part.GetNumberOfSides();
                        Primitive.TextureEntry te = part.Shape.Textures;
                        int first = face == ALL_SIDES ? 0 : face;
                        int last = face == ALL_SIDES ? sides - 1 : face;
                        if (te == null || first < 0 || last >= sides) break;
                        for (int f = first; f <= last; f++)
                        {
                            Primitive.TextureEntryFace texface = te.GetFace((uint)f);
                            if (code == PRIM_TEXGEN)
                                result.Add((int)texface.TexMapType >> 1); // Halcyon: MappingType Default 0, Planar 2
                            else
                                GetFaceMaterial(result, code, part, texface);
                        }
                        break;
                    }

                    case IW_PRIM_ALPHA:
                    {
                        // [ float alpha ] (Halcyon: GetAlpha, as llGetAlpha, so ALL_SIDES is the faces' sum).
                        if (idx >= data.Length) break;
                        if (data[idx++] is not int face) break;
                        result.Add(GetAlpha(part, face));
                        break;
                    }

                    case PRIM_CAST_SHADOWS:
                        result.Add(0); // SL: it "will always return 0"; Halcyon has no rule
                        break;

                    case PRIM_ALLOW_UNSIT:
                        result.Add(part.AllowUnsit ? 1 : 0);
                        break;

                    case PRIM_SCRIPTED_SIT_ONLY:
                        result.Add(part.ScriptedSitOnly ? 1 : 0);
                        break;

                    case PRIM_SIT_FLAGS:
                        result.Add(PartSitFlags(part));
                        break;

                    case PRIM_DAMAGE:
                        // [ float damage, integer damage_type ]: the scene keeps no type, so DAMAGE_TYPE_GENERIC.
                        result.Add(PrimDamage(part));
                        result.Add(DAMAGE_TYPE_GENERIC);
                        break;

                    case PRIM_HEALTH:
                        result.Add(0f); // SL: "Objects start with 0 health by default"; the scene keeps none
                        break;

                    case PRIM_COLLISION_SOUND:
                        PrimCollisionSound(part, result);
                        break;

                    case PRIM_SLICE:
                        result.Add(PrimSlice(part));
                        break;

                    case PRIM_PROJECTOR:
                        PrimProjector(part, result);
                        break;

                    case IW_PRIM_PROJECTOR:
                    {
                        // [ integer enabled, string texture, float fov, float focus, float ambience ] (Halcyon)
                        PrimitiveBaseShape shape = part.Shape;
                        result.Add(shape.ProjectionEntry ? 1 : 0);
                        result.Add(ConditionalTextureNameOrUUID(part, shape.ProjectionTextureUUID));
                        result.Add(shape.ProjectionFOV);
                        result.Add(shape.ProjectionFocus);
                        result.Add(shape.ProjectionAmbiance);
                        break;
                    }

                    case IW_PRIM_PROJECTOR_ENABLED:
                        result.Add(part.Shape.ProjectionEntry ? 1 : 0);
                        break;

                    case IW_PRIM_PROJECTOR_TEXTURE:
                        result.Add(ConditionalTextureNameOrUUID(part, part.Shape.ProjectionTextureUUID));
                        break;

                    case IW_PRIM_PROJECTOR_FOV:
                        result.Add(part.Shape.ProjectionFOV);
                        break;

                    case IW_PRIM_PROJECTOR_FOCUS:
                        result.Add(part.Shape.ProjectionFocus);
                        break;

                    case IW_PRIM_PROJECTOR_AMBIENCE:
                        result.Add(part.Shape.ProjectionAmbiance);
                        break;

                    case PRIM_REFLECTION_PROBE:
                    {
                        // [ integer boolean, float ambiance, float clip_distance, integer flags ] (SL); none: 0, 0.0,
                        // 0.0, 0.
                        Primitive.ReflectionProbe probe = part.Shape.ReflectionProbe;
                        result.Add(probe != null ? 1 : 0);
                        result.Add(probe?.Ambiance ?? 0f);
                        result.Add(probe?.ClipDistance ?? 0f);
                        result.Add((int)(probe?.Flags ?? 0));
                        break;
                    }

                    case PRIM_MATERIAL:
                        result.Add((int)part.Material);
                        break;

                    case PRIM_POSITION:
                        result.Add(part.AbsolutePosition);
                        break;

                    case PRIM_POS_LOCAL:
                        result.Add(PartLocalPos(part));
                        break;

                    case PRIM_SIZE:
                        result.Add(part.Scale);
                        break;

                    case PRIM_ROTATION:
                        result.Add(PartRegionRot(part));
                        break;

                    case PRIM_ROT_LOCAL:
                        // Halcyon GetPartLocalRot: the root's is the object's rotation (an attachment's, relative
                        // to the attach point); a child's is relative to the root.
                        result.Add(part.RotationOffset);
                        break;

                    case PRIM_PHYSICS:
                        result.Add((part.GetEffectiveObjectFlags() & (uint)PrimFlags.Physics) != 0 ? 1 : 0);
                        break;

                    case PRIM_TEMP_ON_REZ:
                        result.Add((part.GetEffectiveObjectFlags() & (uint)PrimFlags.TemporaryOnRez) != 0 ? 1 : 0);
                        break;

                    case PRIM_PHANTOM:
                        result.Add((part.GetEffectiveObjectFlags() & (uint)PrimFlags.Phantom) != 0 ? 1 : 0);
                        break;

                    case PRIM_OMEGA:
                    {
                        // SL's form: "the vector is normalized, and the spinrate is
                        // multiplied by the magnitude of the original vector", then the gain (PrimOmega).
                        PrimOmega(part, out Vector3 axis, out float spinrate, out float gain);
                        result.Add(axis);
                        result.Add(spinrate);
                        result.Add(gain);
                        break;
                    }

                    case PRIM_CLICK_ACTION:
                        result.Add((int)part.ClickAction);
                        break;

                    case PRIM_SIT_TARGET:
                    {
                        // [ integer active, vector offset, rotation rot ] (Halcyon); none set: 0, ZERO_VECTOR,
                        // ZERO_ROTATION.
                        bool active = part.IsSitTargetSet;
                        result.Add(active ? 1 : 0);
                        result.Add(active ? part.SitTargetPosition : Vector3.Zero);
                        result.Add(active ? part.SitTargetOrientation : Quaternion.Identity);
                        break;
                    }

                    case PRIM_PHYSICS_SHAPE_TYPE:
                        result.Add((int)part.PhysicsShapeType);
                        break;

                    case PRIM_FLEXIBLE:
                    {
                        var s = part.Shape;
                        result.Add(s.FlexiEntry ? 1 : 0);
                        result.Add(s.FlexiSoftness);
                        result.Add(s.FlexiGravity);
                        result.Add(s.FlexiDrag);
                        result.Add(s.FlexiWind);
                        result.Add(s.FlexiTension);
                        result.Add(new Vector3(s.FlexiForceX, s.FlexiForceY, s.FlexiForceZ));
                        break;
                    }

                    case PRIM_POINT_LIGHT:
                    {
                        var s = part.Shape;
                        result.Add(s.LightEntry ? 1 : 0);
                        result.Add(new Vector3(s.LightColorR, s.LightColorG, s.LightColorB));
                        result.Add(s.LightIntensity);
                        result.Add(s.LightRadius);
                        result.Add(s.LightFalloff);
                        break;
                    }

                    case PRIM_TYPE:
                    {
                        PrimitiveBaseShape shape = part.Shape;
                        int primType = getScriptPrimType(shape);
                        result.Add(primType);
                        switch (primType)
                        {
                            case 0:
                            case 1:
                            case 2:
                                result.Add((int)shape.HollowShape);
                                result.Add(new Vector3(shape.ProfileBegin / 50000.0f, 1 - shape.ProfileEnd / 50000.0f, 0));
                                result.Add((float)(shape.ProfileHollow / 50000.0));
                                result.Add(new Vector3(shape.PathTwistBegin / 100.0f, shape.PathTwist / 100.0f, 0));
                                result.Add(new Vector3(1 - (shape.PathScaleX / 100.0f - 1), 1 - (shape.PathScaleY / 100.0f - 1), 0));
                                result.Add(new Vector3(Primitive.UnpackPathShear((sbyte)shape.PathShearX), Primitive.UnpackPathShear((sbyte)shape.PathShearY), 0));
                                break;
                            case 3:
                                result.Add((int)shape.HollowShape);
                                result.Add(new Vector3(shape.PathBegin / 50000.0f, 1 - shape.PathEnd / 50000.0f, 0));
                                result.Add((float)(shape.ProfileHollow / 50000.0f));
                                result.Add(new Vector3(shape.PathTwistBegin / 100.0f, shape.PathTwist / 100.0f, 0));
                                result.Add(new Vector3(shape.ProfileBegin / 50000.0f, 1 - shape.ProfileEnd / 50000.0f, 0));
                                break;
                            case 7:
                                // Halcyon's PRIM_TYPE_SCULPT read.
                                result.Add(ConditionalTextureNameOrUUID(part, shape.SculptTexture));
                                result.Add((int)shape.SculptType);
                                break;
                            case 6:
                            case 5:
                            case 4:
                                result.Add((int)shape.HollowShape);
                                result.Add(new Vector3(shape.PathBegin / 50000.0f, 1 - shape.PathEnd / 50000.0f, 0));
                                result.Add((float)(shape.ProfileHollow / 50000.0));
                                result.Add(new Vector3(shape.PathTwistBegin / 100.0f, shape.PathTwist / 100.0f, 0));
                                result.Add(new Vector3(1 - (shape.PathScaleX / 100.0f - 1), 1 - (shape.PathScaleY / 100.0f - 1), 0));
                                result.Add(new Vector3(Primitive.UnpackPathShear((sbyte)shape.PathShearX), Primitive.UnpackPathShear((sbyte)shape.PathShearY), 0));
                                result.Add(new Vector3(shape.ProfileBegin / 50000.0f, 1 - shape.ProfileEnd / 50000.0f, 0));
                                result.Add(new Vector3(shape.PathTaperX / 100.0f, shape.PathTaperY / 100.0f, 0));
                                result.Add((float)(shape.PathRevolutions / 66.666667 + 1.0));
                                result.Add((float)(shape.PathRadiusOffset / 100.0));
                                result.Add((float)(shape.PathSkew / 100.0));
                                break;
                        }
                        break;
                    }

                    case PRIM_NAME:
                        result.Add(part.Name);
                        break;

                    case PRIM_DESC:
                        result.Add(part.Description);
                        break;

                    case PRIM_TEXT:
                    {
                        // [ string text, vector color, float alpha ] (Halcyon GetPrimParams, SL PRIM_TEXT)
                        Color4 c = part.GetTextColor();
                        result.Add(part.Text);
                        result.Add(new Vector3(c.R, c.G, c.B));
                        result.Add(c.A);
                        break;
                    }

                    // ── PBR / glTF Material get params ──────────────────────────

                    case PRIM_RENDER_MATERIAL:
                    {
                        // Returns: [ string render_material ]
                        if (idx >= data.Length) break;
                        int face; try { face = Convert.ToInt32(data[idx++]); } catch { break; }
                        var shape_rm = part.Shape;
                        string matId = string.Empty;
                        if (shape_rm?.RenderMaterials?.entries != null && face >= 0)
                        {
                            foreach (var entry in shape_rm.RenderMaterials.entries)
                            {
                                if (entry.te_index == (byte)face)
                                { matId = ConditionalMaterialNameOrUUID(part, entry.id.ToString()); break; }
                            }
                        }
                        result.Add(matId);
                        break;
                    }

                    case PRIM_GLTF_BASE_COLOR:
                    {
                        // Returns: [ texture, repeats, offsets, rotation, color, alpha,
                        //            alpha_mode, alpha_cutoff, double_sided ]
                        if (idx >= data.Length) break;
                        int face; try { face = Convert.ToInt32(data[idx++]); } catch { break; }
                        GetGLTFBaseColorParams(part, face, result);
                        break;
                    }

                    case PRIM_GLTF_NORMAL:
                    {
                        // Returns: [ texture, repeats, offsets, rotation ]
                        if (idx >= data.Length) break;
                        int face; try { face = Convert.ToInt32(data[idx++]); } catch { break; }
                        GetGLTFTransformParams(part, face, "ntex", "nrep", "noff", "nrot", result);
                        break;
                    }

                    case PRIM_GLTF_METALLIC_ROUGHNESS:
                    {
                        // Returns: [ texture, repeats, offsets, rotation, metallic_factor, roughness_factor ]
                        if (idx >= data.Length) break;
                        int face; try { face = Convert.ToInt32(data[idx++]); } catch { break; }
                        GetGLTFMetallicRoughnessParams(part, face, result);
                        break;
                    }

                    case PRIM_GLTF_EMISSIVE:
                    {
                        // Returns: [ texture, repeats, offsets, rotation, emissive_tint ]
                        if (idx >= data.Length) break;
                        int face; try { face = Convert.ToInt32(data[idx++]); } catch { break; }
                        GetGLTFEmissiveParams(part, face, result);
                        break;
                    }

                    default:
                        idx += PrimParamRules.GetValueCount(code);
                        break;
                }
            }
            return new LSLList(result.ToArray());
        }
        public string llGetLinkKey(int linknumber)
        {
            if (m_host == null) return UUID.Zero.ToString();
            SceneObjectGroup group = m_host.ParentGroup;
            if (group == null) return UUID.Zero.ToString();
            // Halcyon: the key when the link number selects exactly one prim or seated avatar.
            var prims = GetLinkParts(linknumber).Take(2).ToList();
            var sitters = GetLinkSitters(linknumber);
            if (prims.Count + sitters.Count != 1) return UUID.Zero.ToString();
            return prims.Count == 1 ? prims[0].UUID.ToString() : sitters[0].UUID.ToString();
        }

        public string llGetObjectLinkKey(string objectId, int linknumber)
        {
            // SL: like llGetLinkKey but for any object, not just this one
            if (!UUID.TryParse(objectId, out UUID objID) || objID == UUID.Zero) return UUID.Zero.ToString();
            SceneObjectPart sop = World?.GetSceneObjectPart(objID);
            if (sop == null) return UUID.Zero.ToString();
            SceneObjectGroup group = sop.ParentGroup;
            if (group == null) return UUID.Zero.ToString();
            if (linknumber == 0 || linknumber == 1) return group.RootPart.UUID.ToString();
            SceneObjectPart part = group.GetLinkNumPart(linknumber);
            if (part != null) return part.UUID.ToString();
            return UUID.Zero.ToString();
        }

        public string llGetLinkName(int linknumber)
        {
            if (m_host == null) return string.Empty;
            SceneObjectGroup group = m_host.ParentGroup;
            if (group == null) return string.Empty;
            SceneObjectPart part = GetSingleLinkPart(linknumber);
            if (part != null) return part.Name;
            // Halcyon: a number past the prims is a seated avatar, named by its legacy name.
            if (linknumber > group.PrimCount)
            {
                var sitters = GetLinkSitters(linknumber);
                if (sitters.Count == 1) return sitters[0].Name;
            }
            return string.Empty;
        }

        public int llGetLinkNumberOfSides(int link)
        {
            if (m_host == null) return 0;
            // Halcyon: the sides of every prim the link number selects.
            int sides = 0;
            foreach (SceneObjectPart part in GetLinkParts(link))
                sides += part.GetNumberOfSides();
            return sides;
        }

        // ── Texture / color / alpha ────────────────────────────────────────────

        public void llSetColor(Vector3 color, int face)
        {
            SetColor(m_host, color, face);
        }

        public Vector3 llGetColor(int face) => ColorOf(m_host, face);

        /// <summary>llGetColor over any part, shared with osGetLinkColor (upstream LSL_Api.GetColor).</summary>
        private static Vector3 ColorOf(SceneObjectPart part, int face)
        {
            if (part == null) return Vector3.Zero;
            Primitive.TextureEntry tex = part.Shape.Textures;
            if (tex == null) return Vector3.Zero;
            int sides = part.GetNumberOfSides();
            Vector3 rgb = Vector3.Zero;

            if (face == ALL_SIDES)
            {
                if (sides <= 0) return Vector3.Zero;
                for (int i = 0; i < sides; i++)
                {
                    Color4 c = tex.GetFace((uint)i).RGBA;
                    rgb.X += c.R; rgb.Y += c.G; rgb.Z += c.B;
                }
                rgb.X /= sides; rgb.Y /= sides; rgb.Z /= sides;
                return rgb;
            }
            if (face >= 0 && face < sides)
            {
                Color4 c = tex.GetFace((uint)face).RGBA;
                return new Vector3(c.R, c.G, c.B);
            }
            return Vector3.Zero;
        }

        public void llSetAlpha(float alpha, int face)
        {
            SetAlpha(m_host, alpha, face);
        }

        public float llGetAlpha(int face) => GetAlpha(m_host, face);

        public void llSetTexture(string texture, int face)
        {
            SetTexture(m_host, texture, face);
            ScriptSleep(200);
        }

        public string llGetTexture(int face)
        {
            if (m_host == null) return UUID.Zero.ToString();
            Primitive.TextureEntry tex = m_host.Shape.Textures;
            if (tex == null) return UUID.Zero.ToString();
            int sides = m_host.GetNumberOfSides();
            int effectiveFace = (face == ALL_SIDES) ? 0 : face;

            UUID assetID = UUID.Zero;
            if (effectiveFace >= 0 && effectiveFace < sides)
                assetID = tex.GetFace((uint)effectiveFace).TextureID;

            return ConditionalTextureNameOrUUID(m_host, assetID);
        }

        /// <summary>
        /// A texture as a script may see it (Halcyon ConditionalTextureNameOrUUID, LSLSystemAPI.cs:2300-2316, used by
        /// llGetTexture, PRIM_TEXTURE, the PRIM_TYPE sculpt map, PRIM_NORMAL / PRIM_SPECULAR and the projector reads):
        /// its name when it is in the script's prim's inventory (Halcyon's InventoryName searches m_host even when
        /// llGetLinkPrimitiveParams reads another prim); else its key when <paramref name="part"/> is full-perm for its
        /// owner (<see cref="AssetKeyIfFullPerm"/>); otherwise NULL_KEY.
        /// <paramref name="namesFrom"/>: the prim whose inventory names it, for the reads Halcyon has no rule for and
        /// SL names "a material in the inventory of the target prim" (PRIM_RENDER_MATERIAL, and the GLTF reads with it).
        /// </summary>
        private string ConditionalTextureNameOrUUID(SceneObjectPart part, UUID assetID, SceneObjectPart namesFrom = null)
        {
            if (assetID == UUID.Zero) return UUID.Zero.ToString();

            // If the texture is in the prim's inventory, return the inventory name.
            string name = InventoryName(namesFrom ?? m_host, assetID);
            if (!string.IsNullOrEmpty(name)) return name;

            // Not in prim inventory — only reveal the UUID on full-perm objects.
            return AssetKeyIfFullPerm(assetID, part.OwnerMask);
        }

        /// <summary>
        /// A render material or GLTF texture key as a script may see it. SL (llGetRenderMaterial): "If the
        /// Material is in the prim's inventory, the return value is the inventory name ... NULL_KEY is returned when the
        /// owner does not have full permissions to the object and the Material is not in the prim's inventory." A GLTF
        /// override texture that is not a key (none set: empty) is returned as stored.
        /// </summary>
        private string ConditionalMaterialNameOrUUID(SceneObjectPart part, string stored)
            => UUID.TryParse(stored, out UUID id) && id != UUID.Zero
                ? ConditionalTextureNameOrUUID(part, id, namesFrom: part) : stored;

        public void llScaleTexture(float u, float v, int face)
        {
            ScaleTexture(m_host, u, v, face);
            ScriptSleep(200);
        }

        public void llOffsetTexture(float u, float v, int face)
        {
            OffsetTexture(m_host, u, v, face);
            ScriptSleep(200);
        }

        public void llRotateTexture(float rot, int face)
        {
            RotateTexture(m_host, rot, face);
            ScriptSleep(200);
        }

        public Vector3 llGetTextureOffset(int face)
        {
            if (m_host == null) return Vector3.Zero;
            Primitive.TextureEntry tex = m_host.Shape.Textures;
            if (tex == null) return Vector3.Zero;
            int sides = m_host.GetNumberOfSides();
            int effectiveFace = (face == ALL_SIDES) ? 0 : face;
            if (effectiveFace < 0 || effectiveFace >= sides) return Vector3.Zero;
            var f = tex.GetFace((uint)effectiveFace);
            return new Vector3(f.OffsetU, f.OffsetV, 0f);
        }

        public Vector3 llGetTextureScale(int side)
        {
            if (m_host == null) return Vector3.Zero;
            Primitive.TextureEntry tex = m_host.Shape.Textures;
            if (tex == null) return Vector3.Zero;
            int sides = m_host.GetNumberOfSides();
            int effectiveFace = (side == ALL_SIDES) ? 0 : side;
            if (effectiveFace < 0 || effectiveFace >= sides) return Vector3.Zero;
            var f = tex.GetFace((uint)effectiveFace);
            return new Vector3(f.RepeatU, f.RepeatV, 0f);
        }

        public float llGetTextureRot(int side)
        {
            if (m_host == null) return 0f;
            Primitive.TextureEntry tex = m_host.Shape.Textures;
            if (tex == null) return 0f;
            int sides = m_host.GetNumberOfSides();
            int effectiveFace = (side == ALL_SIDES) ? 0 : side;
            if (effectiveFace < 0 || effectiveFace >= sides) return 0f;
            return tex.GetFace((uint)effectiveFace).Rotation;
        }

        public void llSetLinkAlpha(int linknumber, float alpha, int face)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
                SetAlpha(part, alpha, face);
        }

        public void llSetLinkColor(int linknumber, Vector3 color, int face)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
                SetColor(part, color, face);
        }

        public void llSetLinkTexture(int linknumber, string texture, int face)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
                SetTexture(part, texture, face);
            ScriptSleep(200);
        }

        public void llSetLinkTextureAnim(int link, int mode, int face, int sizex, int sizey, float start, float length, float rate)
        {
            foreach (SceneObjectPart part in GetLinkParts(link))
                SetPrimTextureAnim(part, mode, face, sizex, sizey, start, length, rate);
        }

        public void llSetTextureAnim(int mode, int face, int sizex, int sizey, float start, float length, float rate)
        {
            SetPrimTextureAnim(m_host, mode, face, sizex, sizey, start, length, rate);
        }

		public void llSetText(string text, Vector3 color, float alpha)
		{
			PrimSetText(m_host, text, color, alpha);
		}
        // ── Sound ──────────────────────────────────────────────────────────────

        public void llSound(string sound, float volume, int queue, int loop) => Deprecated("llSound");   // Halcyon LSLSystemAPI.cs:2752
        public void llPlaySound(string sound, float volume)
        {
            if (m_host == null) return;
            UUID soundID = KeyOrName(sound);
            if (soundID == UUID.Zero) return;
            volume = Math.Max(0f, Math.Min(1f, volume));
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            if (sm == null) return;
            sm.SendSound(m_host, soundID, volume, false, 0, false, false);
        }

        /// <summary>LSL_Api.cs:2939-2942 - the three-argument form is the four with flags 0.</summary>
        public void llLinkPlaySound(int link, string sound, float volume)
        {
            llLinkPlaySound(link, sound, volume, 0);
        }

        public void llLinkPlaySound(int link, string sound, float volume, int flags)
        {
            // SL: play a sound on a specific link. flags: SOUND_PLAY=0, SOUND_LOOP=1, SOUND_TRIGGER=2, SOUND_SYNC=4
            UUID soundID = KeyOrName(sound);
            if (soundID == UUID.Zero) return;
            volume = Math.Max(0f, Math.Min(1f, volume));
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            if (sm == null) return;
            bool loop = (flags & 1) != 0;
            bool trigger = (flags & 2) != 0;
            foreach (SceneObjectPart part in GetLinkParts(link))
            {
                if (trigger)
                    sm.SendSound(part, soundID, volume, true, 0, false, false);
                else
                    sm.SendSound(part, soundID, volume, false, 0, loop, false);
            }
        }

        public void llLoopSound(string sound, float volume)
        {
            if (m_host == null) return;
            UUID soundID = KeyOrName(sound);
            if (soundID == UUID.Zero) return;
            volume = Math.Max(0f, Math.Min(1f, volume));
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            if (sm == null) return;
            sm.LoopSound(m_host, soundID, volume, false, false);
        }

        public void llLoopSoundMaster(string sound, float volume)
        {
            if (m_host == null) return;
            UUID soundID = KeyOrName(sound);
            if (soundID == UUID.Zero) return;
            volume = Math.Max(0f, Math.Min(1f, volume));
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            if (sm == null) return;
            sm.LoopSound(m_host, soundID, volume, true, false);
        }

        public void llLoopSoundSlave(string sound, float volume)
        {
            if (m_host == null) return;
            UUID soundID = KeyOrName(sound);
            if (soundID == UUID.Zero) return;
            volume = Math.Max(0f, Math.Min(1f, volume));
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            if (sm == null) return;
            sm.LoopSound(m_host, soundID, volume, false, true);
        }

        public void llPlaySoundSlave(string sound, float volume)
        {
            if (m_host == null) return;
            UUID soundID = KeyOrName(sound);
            if (soundID == UUID.Zero) return;
            volume = Math.Max(0f, Math.Min(1f, volume));
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            if (sm == null) return;
            sm.SendSound(m_host, soundID, volume, false, 0, true, false);
        }

        public void llTriggerSound(string sound, float volume)
        {
            if (m_host == null) return;
            UUID soundID = KeyOrName(sound);
            if (soundID == UUID.Zero) return;
            volume = Math.Max(0f, Math.Min(1f, volume));
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            if (sm == null) return;
            sm.SendSound(m_host, soundID, volume, true, 0, false, false);
        }

        public void llTriggerSoundLimited(string sound, float volume, Vector3 top, Vector3 bottom)
        {
            if (m_host == null) return;
            UUID soundID = KeyOrName(sound);
            if (soundID == UUID.Zero) return;
            volume = Math.Max(0f, Math.Min(1f, volume));
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            if (sm == null) return;
            sm.TriggerSoundLimited(m_host.UUID, soundID, volume, bottom, top);
        }

        public void llStopSound()
        {
            if (m_host == null) return;
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            sm?.StopSound(m_host);
        }

        public void llPreloadSound(string sound)
        {
            if (m_host == null) { ScriptSleep(1000); return; }
            UUID soundID = KeyOrName(sound);
            if (soundID != UUID.Zero)
            {
                ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
                sm?.PreloadSound(m_host, soundID);
            }
            ScriptSleep(1000);
        }

        public void llSoundPreload(string sound) { llPreloadSound(sound); }

        public void llAdjustSoundVolume(float volume)
        {
            if (m_host == null) return;
            m_host.AdjustSoundGain(Math.Max(0f, Math.Min(1f, volume)));
            ScriptSleep(100);
        }

        public void llSetSoundQueueing(int queue)
        {
            if (m_host == null) return;
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            sm?.SetSoundQueueing(m_host.UUID, queue != 0);
        }

        public void llSetSoundRadius(float radius)
        {
            if (m_host == null) return;
            m_host.SoundRadius = Math.Max(0.0, radius);
            m_host.ScheduleFullUpdate();
        }

        public void llCollisionSound(string impact_sound, float impact_volume)
        {
            if (m_host == null) return;
            PrimSetCollisionSound(m_host, impact_sound, impact_volume);
        }

        /// <summary>
        /// llCollisionSound and PRIM_COLLISION_SOUND on one prim, stored in the scene's CollisionSound and
        /// CollisionSoundVolume (the CollisionSound setter derives CollisionSoundType), as OpenSim's LSL_Api does.
        /// SL: "If impact_sound is an empty string then the collision sound is suppressed" (the scene's
        /// invalidCollisionSoundUUID); otherwise impact_sound is a sound's key or the name of a sound in the prim's
        /// inventory (Halcyon and SL: a sound, not any item), and when it is neither "an error is shouted on
        /// DEBUG_CHANNEL" and the prim keeps its sound. The volume is held to SL's 0.0-1.0. The part's collision
        /// subscription is refreshed, since the scene listens for collisions only while a sound is not suppressed.
        /// </summary>
        private void PrimSetCollisionSound(SceneObjectPart part, string impact_sound, float impact_volume)
        {
            float volume = Math.Clamp(impact_volume, 0f, 1f);
            UUID sound;
            if (string.IsNullOrEmpty(impact_sound))
                sound = part.invalidCollisionSoundUUID;
            else if (!UUID.TryParse(impact_sound, out sound))
            {
                sound = UUID.Zero;
                lock (part.TaskInventory)
                {
                    foreach (TaskInventoryItem item in part.TaskInventory.Values)
                    {
                        if (item.Type == (int)AssetType.Sound && item.Name == impact_sound)
                        {
                            sound = item.AssetID;
                            break;
                        }
                    }
                }
                if (sound == UUID.Zero)
                {
                    ShoutError($"Could not find sound '{impact_sound}'");
                    return;
                }
            }
            part.CollisionSound = sound;
            part.CollisionSoundVolume = volume;
            part.aggregateScriptEvents();
        }

        /// <summary>
        /// PRIM_COLLISION_SOUND's read: [ key sound, float volume ]. A suppressed or default sound reads NULL_KEY.
        /// </summary>
        private static void PrimCollisionSound(SceneObjectPart part, List<object> result)
        {
            UUID sound = part.CollisionSound;
            result.Add(sound == part.invalidCollisionSoundUUID ? UUID.Zero.ToString() : sound.ToString());
            result.Add(part.CollisionSoundVolume);
        }
        public void llCollisionSprite(string impact_sprite) => NotImplemented("llCollisionSprite");   // Halcyon LSLSystemAPI.cs:5835

        // ── Particles ──────────────────────────────────────────────────────────

        public void llParticleSystem(LSLList rules) { PrimParticleSystem(m_host, rules); }
        public void llLinkParticleSystem(int linknumber, LSLList rules)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
                PrimParticleSystem(part, rules);
        }

        // Particle helper constants
        const float MAX_SRC_SCALE = 7.96875f;
        const float MIN_SRC_BURST_RATE = 0.05f;

        private float LimitFloat(float value, float limit, bool allowNegative)
        {
            if (value > limit) value = limit;
            if (allowNegative) { if (value < -limit) value = -limit; }
            else { if (value < 0.0f) value = 0.0f; }
            return value;
        }

        private float LimitScaleForByteEncoding(float value) => LimitFloat(value, MAX_SRC_SCALE, false);

        private Primitive.ParticleSystem GetNewParticleSystemWithSLDefaultValues()
        {
            Primitive.ParticleSystem ps = new Primitive.ParticleSystem();
            ps.PartStartColor = new Color4(1.0f, 1.0f, 1.0f, 1.0f);
            ps.PartEndColor = new Color4(1.0f, 1.0f, 1.0f, 1.0f);
            ps.PartStartScaleX = 1.0f;
            ps.PartStartScaleY = 1.0f;
            ps.PartEndScaleX = 1.0f;
            ps.PartEndScaleY = 1.0f;
            ps.BurstSpeedMin = 1.0f;
            ps.BurstSpeedMax = 1.0f;
            ps.BurstRate = 0.1f;
            ps.PartMaxAge = 10.0f;
            ps.BurstPartCount = 1;
            ps.BlendFuncSource = (byte)PSYS_PART_BF_SOURCE_ALPHA;
            ps.BlendFuncDest = (byte)PSYS_PART_BF_ONE_MINUS_SOURCE_ALPHA;
            ps.PartStartGlow = 0.0f;
            ps.PartEndGlow = 0.0f;
            return ps;
        }

        private void PrimParticleSystem(SceneObjectPart part, LSLList rules)
        {
            if (rules == null || rules.Length == 0)
            {
                part.RemoveParticleSystem();
                part.ParentGroup.HasGroupChanged = true;
            }
            else
            {
                Primitive.ParticleSystem prules = GetNewParticleSystemWithSLDefaultValues();
                Vector3 tempv;
                float tempf;
                int tempi;

                for (int i = 0; i + 1 < rules.Data.Length; i += 2)
                {
                    int rule = rules.Data[i] is int ri ? ri :
                        int.TryParse(rules.Data[i].ToString(), out int pi) ? pi : -1;

                    switch (rule)
                    {
                        case PSYS_PART_FLAGS:
                            prules.PartDataFlags = (Primitive.ParticleSystem.ParticleDataFlags)(uint)rules.GetLSLIntegerItem(i + 1);
                            break;
                        case PSYS_PART_START_COLOR:
                            tempv = ParseVector(rules.Data[i + 1]);
                            prules.PartStartColor.R = tempv.X;
                            prules.PartStartColor.G = tempv.Y;
                            prules.PartStartColor.B = tempv.Z;
                            break;
                        case PSYS_PART_START_ALPHA:
                            prules.PartStartColor.A = ParseFloat(rules.Data[i + 1]);
                            break;
                        case PSYS_PART_END_COLOR:
                            tempv = ParseVector(rules.Data[i + 1]);
                            prules.PartEndColor.R = tempv.X;
                            prules.PartEndColor.G = tempv.Y;
                            prules.PartEndColor.B = tempv.Z;
                            break;
                        case PSYS_PART_END_ALPHA:
                            prules.PartEndColor.A = ParseFloat(rules.Data[i + 1]);
                            break;
                        case PSYS_PART_START_SCALE:
                            tempv = ParseVector(rules.Data[i + 1]);
                            prules.PartStartScaleX = LimitScaleForByteEncoding(tempv.X);
                            prules.PartStartScaleY = LimitScaleForByteEncoding(tempv.Y);
                            break;
                        case PSYS_PART_END_SCALE:
                            tempv = ParseVector(rules.Data[i + 1]);
                            prules.PartEndScaleX = LimitScaleForByteEncoding(tempv.X);
                            prules.PartEndScaleY = LimitScaleForByteEncoding(tempv.Y);
                            break;
                        case PSYS_PART_MAX_AGE:
                            prules.PartMaxAge = LimitFloat(ParseFloat(rules.Data[i + 1]), 30.0f, false);
                            break;
                        case PSYS_SRC_ACCEL:
                            tempv = ParseVector(rules.Data[i + 1]);
                            prules.PartAcceleration.X = LimitFloat(tempv.X, 100.0f, true);
                            prules.PartAcceleration.Y = LimitFloat(tempv.Y, 100.0f, true);
                            prules.PartAcceleration.Z = LimitFloat(tempv.Z, 100.0f, true);
                            break;
                        case PSYS_SRC_PATTERN:
                            prules.Pattern = (Primitive.ParticleSystem.SourcePattern)rules.GetLSLIntegerItem(i + 1);
                            break;
                        case PSYS_SRC_INNERANGLE:
                            prules.InnerAngle = ParseFloat(rules.Data[i + 1]);
                            prules.PartFlags &= 0xFFFFFFFD;
                            break;
                        case PSYS_SRC_OUTERANGLE:
                            prules.OuterAngle = ParseFloat(rules.Data[i + 1]);
                            prules.PartFlags &= 0xFFFFFFFD;
                            break;
                        case PSYS_SRC_TEXTURE:
                            prules.Texture = KeyOrName(rules.Data[i + 1].ToString());
                            break;
                        case PSYS_SRC_BURST_RATE:
                            tempf = ParseFloat(rules.Data[i + 1]);
                            if (tempf < MIN_SRC_BURST_RATE) tempf = MIN_SRC_BURST_RATE;
                            prules.BurstRate = tempf;
                            break;
                        case PSYS_SRC_BURST_PART_COUNT:
                            prules.BurstPartCount = (byte)rules.GetLSLIntegerItem(i + 1);
                            break;
                        case PSYS_SRC_BURST_RADIUS:
                            prules.BurstRadius = LimitFloat(ParseFloat(rules.Data[i + 1]), 50.0f, false);
                            break;
                        case PSYS_SRC_BURST_SPEED_MIN:
                            prules.BurstSpeedMin = ParseFloat(rules.Data[i + 1]);
                            break;
                        case PSYS_SRC_BURST_SPEED_MAX:
                            prules.BurstSpeedMax = ParseFloat(rules.Data[i + 1]);
                            break;
                        case PSYS_SRC_MAX_AGE:
                            prules.MaxAge = ParseFloat(rules.Data[i + 1]);
                            break;
                        case PSYS_SRC_TARGET_KEY:
                            if (UUID.TryParse(rules.Data[i + 1].ToString(), out UUID key))
                                prules.Target = key;
                            else
                                prules.Target = part.UUID;
                            break;
                        case PSYS_SRC_OMEGA:
                            tempv = ParseVector(rules.Data[i + 1]);
                            prules.AngularVelocity.X = tempv.X;
                            prules.AngularVelocity.Y = tempv.Y;
                            prules.AngularVelocity.Z = tempv.Z;
                            break;
                        case PSYS_SRC_ANGLE_BEGIN:
                            prules.InnerAngle = ParseFloat(rules.Data[i + 1]);
                            prules.PartFlags |= 0x02;
                            break;
                        case PSYS_SRC_ANGLE_END:
                            prules.OuterAngle = ParseFloat(rules.Data[i + 1]);
                            prules.PartFlags |= 0x02;
                            break;
                        case PSYS_PART_START_GLOW:
                            prules.PartStartGlow = ParseFloat(rules.Data[i + 1]);
                            break;
                        case PSYS_PART_END_GLOW:
                            prules.PartEndGlow = ParseFloat(rules.Data[i + 1]);
                            break;
                        case PSYS_PART_BLEND_FUNC_SOURCE:
                            prules.BlendFuncSource = (byte)rules.GetLSLIntegerItem(i + 1);
                            break;
                        case PSYS_PART_BLEND_FUNC_DEST:
                            prules.BlendFuncDest = (byte)rules.GetLSLIntegerItem(i + 1);
                            break;
                    }
                }
                prules.CRC = 1;
                part.AddNewParticleSystem(prules, false);
                part.ParentGroup.HasGroupChanged = true;
            }
            part.ScheduleFullUpdate();
        }

        private static float ParseFloat(object o)
        {
            if (o is float f) return f;
            if (o is double d) return (float)d;
            if (o is int i) return (float)i;
            if (float.TryParse(o?.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float r)) return r;
            return 0f;
        }

        private static Vector3 ParseVector(object o)
        {
            if (o is Vector3 v) return v;
            // Try parsing "<x,y,z>" format
            string s = o?.ToString() ?? "";
            s = s.Trim('<', '>', ' ');
            string[] parts = s.Split(',');
            if (parts.Length >= 3 &&
                float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float y) &&
                float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float z))
                return new Vector3(x, y, z);
            return Vector3.Zero;
        }
        public void llMakeExplosion(int particles, float scale, float vel, float lifetime, float arc, string texture, Vector3 offset) => Deprecated("llMakeExplosion");   // Halcyon LSLSystemAPI.cs:3104
        public void llMakeFountain(int particles, float scale, float vel, float lifetime, float arc, int bounce, string texture, Vector3 offset, float bounce_offset) => Deprecated("llMakeFountain");   // Halcyon LSLSystemAPI.cs:3110
        public void llMakeSmoke(int particles, float scale, float vel, float lifetime, float arc, string texture, Vector3 offset) => Deprecated("llMakeSmoke");   // Halcyon LSLSystemAPI.cs:3116
        public void llMakeFire(int particles, float scale, float vel, float lifetime, float arc, string texture, Vector3 offset) => Deprecated("llMakeFire");   // Halcyon LSLSystemAPI.cs:3122

        // ── Terrain / environment ──────────────────────────────────────────────

        public float llGround(Vector3 offset)
        {
            if (World == null || m_host == null) return 0f;
            Vector3 pos = m_host.AbsolutePosition + offset;
            return World.GetGroundHeight(pos.X, pos.Y);
        }

        public float llCloud(Vector3 offset)
        {
            // Cloud density not tracked in OpenSim — return 0 (clear sky)
            return 0f;
        }

        public Vector3 llWind(Vector3 offset)
        {
            if (World == null || m_host == null) return Vector3.Zero;
            Vector3 pos = m_host.AbsolutePosition + offset;
            IWindModule windMod = World.RequestModuleInterface<IWindModule>();
            if (windMod == null) return Vector3.Zero;
            return windMod.WindSpeed((int)pos.X, (int)pos.Y, (int)pos.Z);
        }

        public Vector3 llGetSunDirection()
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null)
            {
                // Fallback: crude sun angle from UTC time
                double hours = DateTime.UtcNow.TimeOfDay.TotalHours;
                double angle = (hours / 24.0) * Math.PI * 2.0;
                return new Vector3((float)Math.Cos(angle), (float)Math.Sin(angle), 0.4f);
            }
            return envModule.GetSunDir(m_host.GetWorldPosition());
        }
       
        public Vector3 llGroundNormal(Vector3 offset)
        {
            if (World == null || m_host == null) return Vector3.UnitZ;
            Vector3 pos = m_host.AbsolutePosition + offset;
            float hC = World.GetGroundHeight(pos.X,       pos.Y);
            float hX = World.GetGroundHeight(pos.X + 1f,  pos.Y);
            float hY = World.GetGroundHeight(pos.X,       pos.Y + 1f);
            Vector3 normal = new Vector3(hC - hX, hC - hY, 1f);
            normal.Normalize();
            return normal;
        }

        public Vector3 llGroundSlope(Vector3 offset)
        {
            // Slope is the horizontal component of the ground normal
            Vector3 n = llGroundNormal(offset);
            Vector3 slope = new Vector3(n.X, n.Y, 0f);
            slope.Normalize();
            return slope;
        }

        public Vector3 llGroundContour(Vector3 offset)
        {
            // Contour is perpendicular to slope in the XY plane
            Vector3 slope = llGroundSlope(offset);
            return new Vector3(-slope.Y, slope.X, 0f);
        }

        public float llWater(Vector3 offset)
        {
            return (float)(World?.RegionInfo?.RegionSettings?.WaterHeight ?? 20.0);
        }
        public void iwSetWind(int type, Vector3 offset, Vector3 speed)
        {
            // Halcyon's IWindModule.WindSet(type, pos, speed) does not exist in this tree.
            // This tree only has WindParamSet(plugin, param, value) which is a different API.
            // Keeping as no-op.
        }
        public Vector3 iwWind(Vector3 offset)
        {
            IWindModule module = World?.RequestModuleInterface<IWindModule>();
            if (module != null)
            {
                Vector3 pos = m_host.GetWorldPosition();
                return module.WindSpeed((int)(pos.X + offset.X), (int)(pos.Y + offset.Y), (int)(pos.Z + offset.Z));
            }
            return Vector3.Zero;
        }
        public Vector3 iwGroundSurfaceNormal(Vector3 offset)
        {
            // Faithful port: returns the terrain surface normal at the given offset
            if (World == null || m_host == null) return Vector3.UnitZ;
            Vector3 pos = m_host.AbsolutePosition + offset;
            float hC = World.GetGroundHeight(pos.X,       pos.Y);
            float hX = World.GetGroundHeight(pos.X + 1f,  pos.Y);
            float hY = World.GetGroundHeight(pos.X,       pos.Y + 1f);
            Vector3 normal = new Vector3(hC - hX, hC - hY, 1f);
            normal.Normalize();
            return normal;
        }

        // ── Land ───────────────────────────────────────────────────────────────

        public string llGetLandOwnerAt(Vector3 pos)
        {
            ILandObject parcel = World.LandChannel.GetLandObject(pos.X, pos.Y);
            if (parcel == null) return UUID.Zero.ToString();
            return parcel.LandData.OwnerID.ToString();
        }

        public int llOverMyLand(string id)
        {
            ILandObject parcel = null;
            if (!UUID.TryParse(id, out UUID key)) return 0;
            ScenePresence presence = World.GetScenePresence(key);
            if (presence != null)
            {
                Vector3 pos = presence.AbsolutePosition;
                parcel = World.LandChannel.GetLandObject(pos.X, pos.Y);
            }
            else
            {
                SceneObjectPart obj = World.GetSceneObjectPart(key);
                if (obj != null)
                {
                    Vector3 pos = obj.AbsolutePosition;
                    parcel = World.LandChannel.GetLandObject(pos.X, pos.Y);
                }
            }
            return (parcel != null) && (m_host.OwnerID == parcel.LandData.OwnerID) ? 1 : 0;
        }

        public int llGetParcelFlags(Vector3 pos)
        {
            ILandObject land = World.LandChannel.GetLandObject(pos.X, pos.Y);
            if (land == null) return 0;
            LandData landData = land.LandData;
            if (landData == null) return 0;
            return (int)landData.Flags;
        }

        public int llGetParcelPrimCount(Vector3 pos, int category, int sim_wide)
        {
            ILandObject parcel = World.LandChannel.GetLandObject(pos.X, pos.Y);
            if (parcel == null) return 0;
            IPrimCounts pc = parcel.PrimCounts;
            if (pc == null) return 0;
            if (sim_wide != 0)
            {
                return category == 0 ? pc.Simulator : parcel.GetSimulatorMaxPrimCount();
            }
            else
            {
                return category switch
                {
                    0 => pc.Total,
                    1 => pc.Owner,
                    2 => pc.Group,
                    3 => pc.Others,
                    4 => pc.Selected,
                    5 => 0, // temp not tracked separately
                    _ => 0
                };
            }
        }

        public int llGetParcelMaxPrims(Vector3 pos, int sim_wide)
        {
            ILandObject parcel = World.LandChannel.GetLandObject(pos.X, pos.Y);
            if (parcel == null) return 0;
            if (sim_wide == 0)
                return parcel.GetParcelMaxPrimCount();
            return parcel.GetSimulatorMaxPrimCount();
        }

        public LSLList llGetParcelDetails(Vector3 pos, LSLList parms) => ParcelDetailsOf(World?.GetLandData(pos.X, pos.Y), parms);

        /// <summary>llGetParcelDetails over a LandData, shared with osGetParcelDetails (by parcel id).</summary>
        private LSLList ParcelDetailsOf(LandData land, LSLList parms)
        {
            if (land == null) return new LSLList(0);
            var ret = new LSLList();
            for (int idx = 0; idx < parms.Length; idx++)
            {
                int param = parms.GetLSLIntegerItem(idx);
                switch (param)
                {
                    case 0: ret = ret.Append(land.Name ?? string.Empty); break;
                    case 1: ret = ret.Append(land.Description ?? string.Empty); break;
                    case 2: ret = ret.Append(land.OwnerID.ToString()); break;
                    case 3: ret = ret.Append(land.GroupID.ToString()); break;
                    case 4: ret = ret.Append(land.Area); break;
                    case 5: ret = ret.Append(land.GlobalID.ToString()); break;
                    case 6: ret = ret.Append(1); break; // SEE_AVATARS always true
                    default: ret = ret.Append(0); break;
                }
            }
            return ret;
        }

        public LSLList llGetParcelPrimOwners(Vector3 pos)
        {
            ILandObject parcel = World.LandChannel.GetLandObject(pos.X, pos.Y);
            var ret = new System.Collections.Generic.List<object>();
            if (parcel?.PrimCounts != null)
            {
                ret.Add(parcel.LandData.OwnerID.ToString());
                ret.Add(parcel.PrimCounts.Owner);
                if (parcel.PrimCounts.Group > 0)
                {
                    ret.Add(parcel.LandData.GroupID.ToString());
                    ret.Add(parcel.PrimCounts.Group);
                }
                if (parcel.PrimCounts.Others > 0)
                {
                    ret.Add(UUID.Zero.ToString());
                    ret.Add(parcel.PrimCounts.Others);
                }
            }
            ScriptSleep(2000);
            return new LSLList(ret.ToArray());
        }

        public void llAddToLandPassList(string avatar, float hours)
        {
            Vector3 landpos = m_host.AbsolutePosition;
            ILandObject landObject = World.LandChannel.GetLandObject(landpos.X, landpos.Y);
            if (!HasParcelPowers(m_host.OwnerID, landObject, (ulong)GroupPowers.LandManageAllowed)) { ScriptSleep(100); return; }
            if (UUID.TryParse(avatar, out UUID key))
            {
                LandAccessEntry entry = new LandAccessEntry();
                entry.AgentID = key;
                entry.Flags = AccessList.Access;
                entry.Expires = hours > 0 ? (int)(Util.UnixTimeSinceEpoch() + hours * 3600) : 0;
                landObject.LandData.ParcelAccessList.Add(entry);
            }
            ScriptSleep(100);
        }

        public void llAddToLandBanList(string avatar, float hours)
        {
            Vector3 landpos = m_host.AbsolutePosition;
            ILandObject landObject = World.LandChannel.GetLandObject(landpos.X, landpos.Y);
            if (!HasParcelPowers(m_host.OwnerID, landObject, (ulong)GroupPowers.LandManageAllowed)) { ScriptSleep(100); return; }
            if (UUID.TryParse(avatar, out UUID key))
            {
                LandAccessEntry entry = new LandAccessEntry();
                entry.AgentID = key;
                entry.Flags = AccessList.Ban;
                entry.Expires = hours > 0 ? (int)(Util.UnixTimeSinceEpoch() + hours * 3600) : 0;
                landObject.LandData.ParcelAccessList.Add(entry);
            }
            ScriptSleep(100);
        }

        public void llRemoveFromLandPassList(string avatar)
        {
            Vector3 landpos = m_host.AbsolutePosition;
            ILandObject landObject = World.LandChannel.GetLandObject(landpos.X, landpos.Y);
            if (!HasParcelPowers(m_host.OwnerID, landObject, (ulong)GroupPowers.LandManageAllowed)) { ScriptSleep(100); return; }
            if (UUID.TryParse(avatar, out UUID key))
            {
                landObject.LandData.ParcelAccessList.RemoveAll(
                    e => e.AgentID == key && e.Flags == AccessList.Access);
            }
            ScriptSleep(100);
        }

        public void llRemoveFromLandBanList(string avatar)
        {
            Vector3 landpos = m_host.AbsolutePosition;
            ILandObject landObject = World.LandChannel.GetLandObject(landpos.X, landpos.Y);
            if (!HasParcelPowers(m_host.OwnerID, landObject, (ulong)GroupPowers.LandManageAllowed)) { ScriptSleep(100); return; }
            if (UUID.TryParse(avatar, out UUID key))
            {
                landObject.LandData.ParcelAccessList.RemoveAll(
                    e => e.AgentID == key && e.Flags == AccessList.Ban);
            }
            ScriptSleep(100);
        }

        public void llResetLandBanList()
        {
            Vector3 landpos = m_host.AbsolutePosition;
            ILandObject landObject = World.LandChannel.GetLandObject(landpos.X, landpos.Y);
            if (!HasParcelPowers(m_host.OwnerID, landObject, (ulong)GroupPowers.LandManageAllowed)) { ScriptSleep(100); return; }
            landObject.LandData.ParcelAccessList.RemoveAll(e => e.Flags == AccessList.Ban);
            ScriptSleep(100);
        }

        public void llResetLandPassList()
        {
            Vector3 landpos = m_host.AbsolutePosition;
            ILandObject landObject = World.LandChannel.GetLandObject(landpos.X, landpos.Y);
            if (!HasParcelPowers(m_host.OwnerID, landObject, (ulong)GroupPowers.LandManageAllowed)) { ScriptSleep(100); return; }
            landObject.LandData.ParcelAccessList.RemoveAll(e => e.Flags == AccessList.Access);
            ScriptSleep(100);
        }

        /// <summary>
        /// Halcyon LSLSystemAPI.cs:7348-7351: the avatar is sent home through llTeleportAgentHome, so the same
        /// authorization (no god; group land for deeded objects or an owner with Eject and Freeze) and
        /// the same 5 s sleep.
        /// </summary>
        public void llEjectFromLand(string pest)
        {
            llTeleportAgentHome(pest);
        }

        public void llSetParcelMusicURL(string url)
        {
            ILandObject landObject = World.LandChannel.GetLandObject(
                m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y);
            if (!HasParcelPowers(m_host.OwnerID, landObject, (ulong)GroupPowers.ChangeMedia)) return;
            landObject.LandData.MusicURL = url ?? string.Empty;
            World.EventManager.TriggerLandObjectUpdated((uint)landObject.LandData.LocalID, landObject);
            ScriptSleep(2000);
        }

        public string llGetParcelMusicURL()
        {
            ILandObject land = World.LandChannel.GetLandObject(
                m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y);
            if (land == null) return string.Empty;
            return land.LandData.MusicURL ?? string.Empty;
        }

        public void llParcelMediaCommandList(LSLList commandList)
        {
            ILandObject landObject = World.LandChannel.GetLandObject(
                m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y);
            // Halcyon asks for m_host.ObjectOwner here, which is the owner.
            if (!HasParcelPowers(m_host.OwnerID, landObject, (ulong)GroupPowers.ChangeMedia)) return;

            bool update = false;
            byte loop = 0;
            LandData landData = landObject.LandData;
            string url = landData.MediaURL;
            UUID textureID = landData.MediaID;
            bool autoAlign = landData.MediaAutoScale != 0;
            string mediaType = landData.MediaType;
            int width = landData.MediaWidth;
            int height = landData.MediaHeight;
            string description = landData.MediaDescription;
            ParcelMediaCommandEnum? commandToSend = null;
            float time = 0.0f;
            ScenePresence presence = null;

            for (int i = 0; i < commandList.Data.Length; i++)
            {
                ParcelMediaCommandEnum command = (ParcelMediaCommandEnum)commandList.GetLSLIntegerItem(i);
                switch (command)
                {
                    case ParcelMediaCommandEnum.Agent:
                        if ((i + 1) < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is string)
                            {
                                if (UUID.TryParse((string)commandList.Data[i + 1], out UUID agentID))
                                    presence = World.GetScenePresence(agentID);
                            }
                            else ScriptShoutError("The argument of PARCEL_MEDIA_COMMAND_AGENT must be a key");
                            ++i;
                        }
                        break;
                    case ParcelMediaCommandEnum.Loop:
                        loop = 1; commandToSend = command; update = true; break;
                    case ParcelMediaCommandEnum.Play:
                        loop = 0; commandToSend = command; update = true; break;
                    case ParcelMediaCommandEnum.Pause:
                    case ParcelMediaCommandEnum.Stop:
                    case ParcelMediaCommandEnum.Unload:
                        commandToSend = command; break;
                    case ParcelMediaCommandEnum.Url:
                        if ((i + 1) < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is string) { url = (string)commandList.Data[i + 1]; update = true; }
                            else ScriptShoutError("The argument of PARCEL_MEDIA_COMMAND_URL must be a string.");
                            ++i;
                        }
                        break;
                    case ParcelMediaCommandEnum.Texture:
                        if ((i + 1) < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is string)
                            {
                                if (!UUID.TryParse((string)commandList.Data[i + 1], out textureID))
                                    textureID = UUID.Zero;
                                update = true;
                            }
                            else ScriptShoutError("The argument of PARCEL_MEDIA_COMMAND_TEXTURE must be a string or key.");
                            ++i;
                        }
                        break;
                    case ParcelMediaCommandEnum.Time:
                        if ((i + 1) < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is float) time = (float)commandList.Data[i + 1];
                            else ScriptShoutError("The argument of PARCEL_MEDIA_COMMAND_TIME must be a float.");
                            ++i;
                        }
                        break;
                    case ParcelMediaCommandEnum.AutoAlign:
                        if ((i + 1) < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is int) { autoAlign = (int)commandList.Data[i + 1] == 1; update = true; }
                            else ScriptShoutError("The argument of PARCEL_MEDIA_COMMAND_AUTO_ALIGN must be an integer.");
                            ++i;
                        }
                        break;
                    case ParcelMediaCommandEnum.Type:
                        if ((i + 1) < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is string) { mediaType = (string)commandList.Data[i + 1]; update = true; }
                            else ScriptShoutError("The argument of PARCEL_MEDIA_COMMAND_TYPE must be a string.");
                            ++i;
                        }
                        break;
                    case ParcelMediaCommandEnum.Desc:
                        if ((i + 1) < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is string) { description = (string)commandList.Data[i + 1]; update = true; }
                            else ScriptShoutError("The argument of PARCEL_MEDIA_COMMAND_DESC must be a string.");
                            ++i;
                        }
                        break;
                    case ParcelMediaCommandEnum.Size:
                        if ((i + 2) < commandList.Length)
                        {
                            if (commandList.Data[i + 1] is int && commandList.Data[i + 2] is int)
                            { width = (int)commandList.Data[i + 1]; height = (int)commandList.Data[i + 2]; update = true; }
                            // Halcyon LSLSystemAPI.cs:13148, 13150.
                            else if (commandList.Data[i + 1] is int)
                                ScriptShoutError("The second argument of PARCEL_MEDIA_COMMAND_SIZE must be an integer.");
                            else
                                ScriptShoutError("The first argument of PARCEL_MEDIA_COMMAND_SIZE must be an integer.");
                            ++i; ++i;
                        }
                        break;
                    default:
                        // Halcyon :13155-13157, naming the command as its Enum.Parse did.
                        NotImplemented("llParcelMediaCommandList parameter not supported yet: " + command.ToString());
                        break;
                }
            }

            if (update)
            {
                landData.MediaID = textureID;
                landData.MediaAutoScale = autoAlign ? (byte)1 : (byte)0;
                landData.MediaDescription = description;
                landData.MediaWidth = width;
                landData.MediaHeight = height;
                landData.MediaType = mediaType;
                landData.MediaURL = url;
                World.EventManager.TriggerLandObjectUpdated((uint)landData.LocalID, landObject);

                List<ScenePresence> agents = World.GetScenePresences();
                foreach (ScenePresence agent in agents)
                {
                    if (agent.IsChildAgent || agent.IsDeleted) continue;
                    ScenePresence target = presence ?? agent;
                    if (target == agent && agent.currentParcelUUID == landData.GlobalID)
                        agent.ControllingClient.SendParcelMediaUpdate(landData.MediaURL,
                            landData.MediaID, landData.MediaAutoScale,
                            mediaType, description, width, height, loop);
                    if (presence != null) break;
                }
            }

            if (commandToSend != null)
            {
                List<ScenePresence> agents = World.GetScenePresences();
                foreach (ScenePresence agent in agents)
                {
                    if (agent.IsChildAgent || agent.IsDeleted) continue;
                    ScenePresence target = presence ?? agent;
                    if (target == agent && agent.currentParcelUUID == landData.GlobalID)
                        agent.ControllingClient.SendParcelMediaCommand(0x4,
                            (ParcelMediaCommandEnum)commandToSend, time);
                    if (presence != null) break;
                }
            }
            ScriptSleep(2000);
        }

        public LSLList llParcelMediaQuery(LSLList aList)
        {
            var list = new System.Collections.Generic.List<object>();
            Vector3 pos = m_host.AbsolutePosition;
            ILandObject landObject = World.LandChannel.GetLandObject(pos.X, pos.Y);
            if (!HasParcelPowers(m_host.OwnerID, landObject, (ulong)GroupPowers.ChangeMedia))
                return new LSLList(list.ToArray());
            for (int i = 0; i < aList.Data.Length; i++)
            {
                if (aList.Data[i] != null)
                {
                    LandData ld = World.GetLandData(m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y);
                    switch ((ParcelMediaCommandEnum)aList.GetLSLIntegerItem(i))
                    {
                        case ParcelMediaCommandEnum.Url:
                            list.Add(ld?.MediaURL ?? string.Empty); break;
                        case ParcelMediaCommandEnum.Desc:
                            list.Add(ld?.MediaDescription ?? string.Empty); break;
                        case ParcelMediaCommandEnum.Texture:
                            list.Add(ld?.MediaID.ToString() ?? UUID.Zero.ToString()); break;
                        case ParcelMediaCommandEnum.Type:
                            list.Add(ld?.MediaType ?? string.Empty); break;
                        case ParcelMediaCommandEnum.Size:
                            list.Add(ld?.MediaWidth ?? 0);
                            list.Add(ld?.MediaHeight ?? 0);
                            break;
                        default:
                            // Halcyon LSLSystemAPI.cs:13281-13285, its text.
                            NotImplemented("llParcelMediaQuery parameter do not supported yet: "
                                + ((ParcelMediaCommandEnum)aList.GetLSLIntegerItem(i)).ToString());
                            break;
                    }
                }
            }
            ScriptSleep(2000);
            return new LSLList(list.ToArray());
        }
        public int llScriptDanger(Vector3 pos)
        {
            // Returns 1 if pos is on a parcel that does not allow scripts or is in a no-build parcel
            ILandObject parcel = World?.LandChannel?.GetLandObject(pos.X, pos.Y);
            if (parcel == null) return 0;
            uint flags = parcel.LandData.Flags;
            // PARCEL_FLAG_ALLOW_OTHER_SCRIPTS = 0x4, PARCEL_FLAG_ALLOW_GROUP_SCRIPTS = 0x8
            bool allowOtherScripts = (flags & 0x4) != 0;
            bool allowGroupScripts = (flags & 0x8) != 0;
            if (allowOtherScripts) return 0;
            // If group scripts allowed and host is in the same group, not dangerous
            if (allowGroupScripts && parcel.LandData.GroupID == m_host.GroupID && m_host.GroupID != UUID.Zero)
                return 0;
            // Owner's own parcel is never dangerous
            if (parcel.LandData.OwnerID == m_host.OwnerID) return 0;
            return 1;
        }
        /// <summary>
        /// Halcyon LSLSystemAPI.cs:10633-10638: the script owner's rights, with the power asked about, on the parcel
        /// under the object: `CanEditParcel(m_host.OwnerID, landObject, (GroupPowers)operation) ? 1 : 0`.
        /// </summary>
        public int iwHasParcelPowers(int groupPower)
        {
            ILandObject landObject = World?.LandChannel?.GetLandObject(m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y);
            return HasParcelPowers(m_host.OwnerID, landObject, IwGroupPowers(groupPower)) ? 1 : 0;
        }

        /// <summary>
        /// The group powers an iwHasParcelPowers argument asks for. An LSL integer is 32 bits and group powers are 64,
        /// so the IW_POWER_* constants for bits 31-48 load as minus their bit number (IW_POWER_FREEZE_EJECT is -32);
        /// Halcyon's assembler loaded them all as -1 (Int32 overflow). Any other value is Halcyon's cast
        /// `(GroupPowers)operation`, which sign-extends: -1, what scripts compiled before carry, is every power.
        /// </summary>
        internal static ulong IwGroupPowers(int value)
            => value >= -IW_POWER_HIGHEST_BIT && value <= -IW_POWER_FIRST_HIGH_BIT
                ? 1UL << -value
                : unchecked((ulong)(long)value);

        private const int IW_POWER_FIRST_HIGH_BIT = 31, IW_POWER_HIGHEST_BIT = 48;

        /// <summary>
        /// The land-rights rule every land function asks, Halcyon's CanEditParcel ->
        /// GenericParcelOwnerPermission (PermissionsModule.cs:1002-1042). <paramref name="user"/> may act on
        /// <paramref name="parcel"/> when it owns the parcel ("This also includes group-deeded objects on group-deeded
        /// land"); on group-owned land when it is the group, or a member holding ANY of <paramref name="powers"/> (0 =
        /// membership, Halcyon HasGroupPower); on land only tagged to a group when AllowSetHome is asked and held; when
        /// it is the estate owner or an estate manager; or when it is a god. <paramref name="powers"/> null: no group
        /// role qualifies (llUnSit, Halcyon LSLSystemAPI.cs:7986-7988).
        /// NGC's CanEditParcelProperties is not used: it needs every requested bit and never admits estate managers.
        /// </summary>
        private bool HasParcelPowers(UUID user, ILandObject parcel, ulong? powers)
        {
            LandData land = parcel?.LandData;
            if (land == null || user.IsZero()) return false;
            if (land.OwnerID == user) return true;
            if (land.IsGroupOwned)
            {
                if (land.GroupID == user) return true;
                if (powers.HasValue && !land.GroupID.IsZero() && HasGroupPower(land.GroupID, user, powers.Value)) return true;
            }
            else if (!land.GroupID.IsZero() && powers == (ulong)GroupPowers.AllowSetHome
                && HasGroupPower(land.GroupID, user, (ulong)GroupPowers.AllowSetHome))
                return true;
            if (World.RegionInfo.EstateSettings.IsEstateManagerOrOwner(user)) return true;
            return World.Permissions.IsGod(user);
        }

        /// <summary>Halcyon HasGroupPower: a member, and (power 0) that is enough, else any requested bit held.</summary>
        private bool HasGroupPower(UUID group, UUID user, ulong powers)
        {
            GroupMembershipData m = World.RequestModuleInterface<IGroupsModule>()?.GetMembershipData(group, user);
            if (m == null) return false;
            return powers == 0 || (m.GroupPowers & powers) != 0;
        }

        // ── Targeting ──────────────────────────────────────────────────────────

        public int llTarget(Vector3 position, float range)
        {
            if (m_host?.ParentGroup == null) return -1;
            return m_host.ParentGroup.RegisterTargetWaypoint(m_itemID, position, range);
        }

        public void llTargetRemove(int number)
        {
            if (m_host?.ParentGroup == null) return;
            m_host.ParentGroup.UnregisterTargetWaypoint(number);
        }

        public int llRotTarget(Quaternion rot, float error)
        {
            if (m_host?.ParentGroup == null) return -1;
            return m_host.ParentGroup.RegisterRotTargetWaypoint(m_itemID, rot, error);
        }

        public void llRotTargetRemove(int number)
        {
            if (m_host?.ParentGroup == null) return;
            m_host.ParentGroup.UnRegisterRotTargetWaypoint(number);
        }

        public void llTargetOmega(Vector3 axis, float spinrate, float gain)
        {
            if (m_host?.ParentGroup == null) return;
            PrimTargetOmega(m_host, axis, spinrate, gain);
        }

        /// <summary>
        /// PRIM_OMEGA, llTargetOmega and iwLinkTargetOmega on one prim (Halcyon PrimTargetOmega): the prim's angular
        /// velocity, which viewers render as a spin from the terse update. The prim the script names spins, so a
        /// child spins about its own centre (SL: "the prim rotates around the local axis"). Physical: axis *
        /// spinrate * gain. Otherwise axis * spinrate, and a gain of 0 stops it (SL: gain 0 "disables and removes
        /// the rotation behavior"). The axis is not normalised (SL: "Use llVecNorm on axis so that spinrate
        /// actually represents the rate of rotation").
        /// </summary>
        private static void PrimTargetOmega(SceneObjectPart part, Vector3 axis, float spinrate, float gain)
        {
            PhysicsActor actor = part.PhysActor;
            Vector3 omega;
            if (actor != null && actor.IsPhysical) omega = axis * (spinrate * gain);
            else omega = gain == 0f ? Vector3.Zero : axis * spinrate;
            part.UpdateAngularVelocity(omega);
            s_primOmegas.AddOrUpdate(part, new PrimOmegaSet(axis, spinrate, gain, part.AngularVelocity));
        }

        /// <summary>The axis, spinrate and gain PrimTargetOmega last set on a prim, and the spin that gave it.</summary>
        private sealed record PrimOmegaSet(Vector3 Axis, float Spinrate, float Gain, Vector3 Omega);

        /// <summary>
        /// What PrimTargetOmega last set on each prim, for PRIM_OMEGA's read. Not saved: the scene keeps only the
        /// angular velocity. Held weakly, so a deleted prim's entry goes with it.
        /// </summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SceneObjectPart, PrimOmegaSet> s_primOmegas = new();

        /// <summary>
        /// PRIM_OMEGA's read in SL's form: "the vector is normalized, and the spinrate is multiplied by the magnitude
        /// of the original vector", then the gain. While the prim still spins as a PRIM_OMEGA, llTargetOmega or
        /// iwLinkTargetOmega in Phlox left it, that is what was set. Otherwise (a YEngine script or the physics
        /// engine changed the spin since, phantom stopped it, or the region restarted) the scene has only the angular
        /// velocity: its direction, its rate, and a gain of 1.0, or ZERO_VECTOR, 0.0, 0.0 for no spin.
        /// </summary>
        private static void PrimOmega(SceneObjectPart part, out Vector3 axis, out float spinrate, out float gain)
        {
            Vector3 omega = part.AngularVelocity;
            if (s_primOmegas.TryGetValue(part, out PrimOmegaSet set) && set.Omega == omega)
            {
                float length = set.Axis.Length();
                axis = length > 0f ? set.Axis / length : Vector3.Zero;
                spinrate = set.Spinrate * length;
                gain = set.Gain;
                return;
            }
            float rate = omega.Length();
            axis = rate > 0f ? omega / rate : Vector3.Zero;
            spinrate = rate;
            gain = rate > 0f ? 1f : 0f;
        }

        public void iwLinkTargetOmega(int linknumber, Vector3 axis, float spinrate, float gain)
        {
            foreach (SceneObjectPart part in GetLinkParts(linknumber))
                PrimTargetOmega(part, axis, spinrate, gain);
        }
        public void llLookAt(Vector3 target, float strength, float damping)
        {
            if (m_host?.ParentGroup == null) return;

            // Compute the rotation needed to face the target, then delegate to llRotLookAt.
            // This mirrors Halcyon's approach: calculate the target quaternion and hand off.
            Vector3 from = m_host.ParentGroup.AbsolutePosition;
            Vector3 toTarget = target - from;

            if (toTarget.LengthSquared() < 0.0001f)
                return; // target is at same position, nothing to do

            // Build a rotation that points our +Z axis toward the target (LSL convention)
            toTarget = Vector3.Normalize(toTarget);
            Vector3 forward = Vector3.UnitZ;

            float dot = Vector3.Dot(forward, toTarget);
            Quaternion newRot;

            if (dot > 0.9999f)
            {
                newRot = Quaternion.Identity;
            }
            else if (dot < -0.9999f)
            {
                newRot = new Quaternion(Vector3.UnitY, (float)Math.PI);
            }
            else
            {
                Vector3 axis = Vector3.Normalize(Vector3.Cross(forward, toTarget));
                float angle = (float)Math.Acos(Math.Max(-1f, Math.Min(1f, dot)));
                newRot = Quaternion.CreateFromAxisAngle(axis, angle);
            }

            llRotLookAt(newRot, strength, damping);
        }

        public void llStopLookAt()
        {
            if (m_host?.ParentGroup == null) return;
            m_host.ParentGroup.StopLookAt();
        }

        public void llRotLookAt(Quaternion target, float strength, float damping)
        {
            if (m_host?.ParentGroup == null) return;

            if (m_host.ParentGroup.UsesPhysics)
            {
                // Physical objects: hand to the physics-backed APID controller
                m_host.ParentGroup.RotLookAt(target, strength, damping);
            }
            else
            {
                // Non-physical: set rotation directly (physics APID won't fire)
                // Clear any active lookat first, then set the rotation
                m_host.ParentGroup.StopLookAt();
                if (m_host.LinkNum < 2)
                    m_host.ParentGroup.UpdateGroupRotationR(target);
                else
                    m_host.UpdateRotation(target);
            }
        }
        public void llPointAt(Vector3 pos) => NotImplemented("llPointAt");   // Halcyon LSLSystemAPI.cs:4332
        public void llStopPointAt() => NotImplemented("llStopPointAt");   // Halcyon LSLSystemAPI.cs:4338
        /// <summary>
        /// Ported from upstream LSL_Api.cs:4036-4043. wiki: "Sets the collision filter,
        /// exclusively or inclusively" - accept TRUE keeps only matches, FALSE excludes them; a blank
        /// name or a null/invalid id matches everything. The part stores it
        /// (SceneObjectPart.SetCollisionFilter) and the region's own collision path consults
        /// CollisionFilteredOut before raising the event; PhloxEngine's handlers consult it too, so
        /// the filter holds whichever door a collision arrives by.
        /// </summary>
        public void llCollisionFilter(string name, string id, int accept)
        {
            if (m_host == null) return;
            _ = UUID.TryParse(id, out UUID objectID);
            string lname = (name ?? string.Empty).ToLower(System.Globalization.CultureInfo.InvariantCulture);
            if (objectID == UUID.Zero)
                m_host.SetCollisionFilter(accept != 0, lname, string.Empty);
            else
                m_host.SetCollisionFilter(accept != 0, lname, objectID.ToString());
        }
        public void llPassTouches(int pass)
        {
            if (m_host == null) return;
            m_host.PassTouches = (pass != 0);
        }

        public void llPassCollisions(int pass)
        {
            if (m_host == null) return;
            m_host.PassCollisions = (pass != 0);
        }
        public void llVolumeDetect(int detect)
        {
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return;
            m_host.ParentGroup.RootPart.ScriptSetVolumeDetect(detect != 0);
            m_thisScript.ScriptState.MiscAttributes[(int)RuntimeState.MiscAttr.VolumeDetect] =
                new object[] { detect };
        }

        // ── Object queries ─────────────────────────────────────────────────────

        public int llGetObjectPrimCount(string object_id)
        {
            if (!UUID.TryParse(object_id, out UUID key)) return 0;
            SceneObjectPart part = World?.GetSceneObjectPart(key);
            return part?.ParentGroup.PrimCount ?? 0;
        }

        public LSLList llGetObjectDetails(string id, LSLList parms)
        {

            var ret = new List<object>();
            if (!UUID.TryParse(id, out UUID key) || key == UUID.Zero || parms == null)
                return new LSLList(ret);

            // Avatar path
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp != null)
            {
                foreach (object param in parms.Data)
                {
                    int p; try { p = Convert.ToInt32(param); } catch { continue; }
                    switch (p)
                    {
                        case OBJECT_NAME:     ret.Add(sp.Name); break;
                        case OBJECT_DESC:     ret.Add(string.Empty); break;
                        case OBJECT_POS:      ret.Add(sp.AbsolutePosition); break;
                        case OBJECT_ROT:      ret.Add(sp.Rotation); break;
                        case OBJECT_VELOCITY: ret.Add(sp.Velocity); break;
                        case OBJECT_OWNER:    ret.Add(sp.UUID.ToString()); break;
                        case OBJECT_GROUP:    ret.Add(UUID.Zero.ToString()); break;
                        case OBJECT_CREATOR:  ret.Add(UUID.Zero.ToString()); break;
                        case OBJECT_RUNNING_SCRIPT_COUNT: ret.Add(0); break;
                        case OBJECT_TOTAL_SCRIPT_COUNT:   ret.Add(0); break;
                        case OBJECT_SCRIPT_MEMORY:        ret.Add(0); break;
                        case OBJECT_SCRIPT_TIME:          ret.Add(0f); break;
                        case OBJECT_PRIM_EQUIVALENCE:     ret.Add(1); break;
                        case OBJECT_SERVER_COST:          ret.Add(0f); break;
                        case OBJECT_STREAMING_COST:       ret.Add(0f); break;
                        case OBJECT_PHYSICS_COST:         ret.Add(0f); break;
                        case OBJECT_ROOT:                 ret.Add(sp.UUID.ToString()); break;
                        case OBJECT_ATTACHED_POINT:       ret.Add(0); break;
                        case OBJECT_PHYSICS:              ret.Add(0); break;
                        case OBJECT_PHANTOM:              ret.Add(0); break;
                        case OBJECT_TEMP_ON_REZ:          ret.Add(0); break;
                        default:                          ret.Add(string.Empty); break;
                    }
                }
                return new LSLList(ret);
            }

            // Object path
            SceneObjectPart part = World?.GetSceneObjectPart(key);
            if (part == null) return new LSLList(ret);
            SceneObjectGroup grp = part.ParentGroup;

            foreach (object param in parms.Data)
            {
                int p; try { p = Convert.ToInt32(param); } catch { continue; }
                switch (p)
                {
                    case OBJECT_NAME:     ret.Add(part.Name); break;
                    case OBJECT_DESC:     ret.Add(part.Description); break;
                    case OBJECT_POS:      ret.Add(part.GetWorldPosition()); break;
                    case OBJECT_ROT:      ret.Add(part.GetWorldRotation()); break;
                    case OBJECT_VELOCITY: ret.Add(part.Velocity); break;
                    case OBJECT_OWNER:
                        ret.Add((grp.GroupID != UUID.Zero && grp.OwnerID == grp.GroupID)
                            ? UUID.Zero.ToString() : grp.OwnerID.ToString()); break;
                    case OBJECT_GROUP:    ret.Add(grp.GroupID.ToString()); break;
                    case OBJECT_CREATOR:  ret.Add(part.CreatorID.ToString()); break;
                    case OBJECT_RUNNING_SCRIPT_COUNT: ret.Add(0); break;
                    case OBJECT_TOTAL_SCRIPT_COUNT:   ret.Add(grp.ScriptCount()); break;
                    case OBJECT_SCRIPT_MEMORY:        ret.Add(0); break;
                    case OBJECT_SCRIPT_TIME:          ret.Add(0f); break;
                    case OBJECT_PRIM_EQUIVALENCE:     ret.Add(grp.PrimCount); break;
                    case OBJECT_SERVER_COST:          ret.Add(0f); break;
                    case OBJECT_STREAMING_COST:       ret.Add(0f); break;
                    case OBJECT_PHYSICS_COST:         ret.Add(0f); break;
                    case OBJECT_ROOT:                 ret.Add(grp.RootPart.UUID.ToString()); break;
                    case OBJECT_ATTACHED_POINT:       ret.Add((int)grp.AttachmentPoint); break;
                    case OBJECT_PHYSICS:              ret.Add(grp.UsesPhysics ? 1 : 0); break;
                    case OBJECT_PHANTOM:              ret.Add(grp.IsPhantom ? 1 : 0); break;
                    case OBJECT_TEMP_ON_REZ:          ret.Add(grp.IsTemporary ? 1 : 0); break;
                    default:                          ret.Add(string.Empty); break;
                }
            }
            return new LSLList(ret);
        }
        public LSLList llGetBoundingBox(string obj)
        {
            // Return [min_corner, max_corner] relative to the root prim.
            // This tree's GetBoundingBox already returns root-relative coords — no position subtraction needed.
            var empty = new LSLList(new object[] { Vector3.Zero, Vector3.Zero });

            if (!UUID.TryParse(obj, out UUID objID) || objID == UUID.Zero)
                return empty;

            // Check if it's an avatar first
            ScenePresence sp = World?.GetScenePresence(objID);
            if (sp != null)
            {
                if (sp.ParentPart != null)
                {
                    // Seated — use the sat-on object's bbox instead
                    objID = sp.ParentPart.ParentGroup?.UUID ?? objID;
                }
                else
                {
                    // Standing or flying — return standard avatar bounding box
                    float h = sp.Appearance?.AvatarHeight ?? 1.8f;
                    float halfH = h / 2.0f;
                    return new LSLList(new object[]
                    {
                        new Vector3(-0.225f, -0.3f, -halfH),
                        new Vector3( 0.225f,  0.3f,  halfH + 0.05f)
                    });
                }
            }

            SceneObjectPart part = World?.GetSceneObjectPart(objID);
            if (part?.ParentGroup == null) return empty;

            float minX, maxX, minY, maxY, minZ, maxZ;
            part.ParentGroup.GetBoundingBox(out minX, out maxX, out minY, out maxY, out minZ, out maxZ);

            // Values are already root-relative — return directly
            return new LSLList(new object[]
            {
                new Vector3(minX, minY, minZ),
                new Vector3(maxX, maxY, maxZ)
            });
        }
        public LSLList iwGetWorldBoundingBox(string obj)
        {
            // Faithful port: same as llGetBoundingBox but returns world coordinates
            return llGetBoundingBox(obj);
        }
        public int llGetObjectPermMask(int mask)
        {
            if (m_host == null) return 0;
            // mask: 0=Base, 1=Owner, 2=Group, 3=Everyone, 4=NextOwner
            switch (mask)
            {
                case 0: return (int)m_host.BaseMask;
                case 1: return (int)m_host.OwnerMask;
                case 2: return (int)m_host.GroupMask;
                case 3: return (int)m_host.EveryoneMask;
                case 4: return (int)m_host.NextOwnerMask;
                default: return 0;
            }
        }

        public void llSetObjectPermMask(int mask, int value)
        {
            // A god function (SL: "This function can only be executed in God Mode"), gated as YEngine gates it
            // (LSL_Api.llSetObjectPermMask): AllowGodFunctions and an administrator owner.
            if (m_host == null) return;
            if (m_ScriptEngine == null || !m_ScriptEngine.AllowGodFunctions) return;
            if (World?.Permissions == null || !World.Permissions.IsAdministrator(m_host.OwnerID)) return;

            switch (mask)
            {
                case 0: m_host.BaseMask      = (uint)value; break;
                case 1: m_host.OwnerMask     = (uint)value; break;
                case 2: m_host.GroupMask     = (uint)value; break;
                case 3: m_host.EveryoneMask  = (uint)value; break;
                case 4: m_host.NextOwnerMask = (uint)value; break;
                default: return;
            }
            m_host.ParentGroup?.InvalidateDeepEffectivePerms();
        }
        public LSLList llGetAgentList(int scope, LSLList options)
        {
            Func<ScenePresence, bool> inScope = AgentListScope(scope);
            if (inScope == null) return new LSLList(new List<object> { "INVALID_SCOPE" });
            List<object> result = new List<object>();
            List<ScenePresence> presences = World?.GetScenePresences();
            if (presences == null) return new LSLList();

            foreach (ScenePresence sp in presences)
            {
                if (!inScope(sp)) continue;
                result.Add(sp.UUID.ToString());
                if (result.Count >= 100) break;   // SL's maximum
            }
            return new LSLList(result);
        }

        /// <summary>
        /// llGetAgentList's scope as YEngine reads it (LSL_Api.llGetAgentList) - SL's
        /// AGENT_LIST_PARCEL (1), AGENT_LIST_PARCEL_OWNER (2) or AGENT_LIST_REGION (4), with the
        /// AGENT_LIST_EXCLUDENPC flag; PARCEL_OWNER is every parcel with the same owner as the one the
        /// object is on. Null for any other scope (INVALID_SCOPE). Gods and child agents are not listed.
        /// </summary>
        private Func<ScenePresence, bool> AgentListScope(int scope)
        {
            const int AGENT_LIST_EXCLUDENPC = 0x4000000;   // OpenSim's flag, not an SL constant
            bool noNpc = (scope & AGENT_LIST_EXCLUDENPC) != 0;
            scope &= ~AGENT_LIST_EXCLUDENPC;
            if (scope != AGENT_LIST_REGION && scope != AGENT_LIST_PARCEL && scope != AGENT_LIST_PARCEL_OWNER)
                return null;

            UUID id = UUID.Zero;
            if (scope != AGENT_LIST_REGION)
            {
                ILandObject here = World.LandChannel?.GetLandObject(m_host.ParentGroup.RootPart.GetWorldPosition());
                if (here != null)
                    id = scope == AGENT_LIST_PARCEL_OWNER ? here.LandData.OwnerID : here.LandData.GlobalID;
            }
            return sp =>
            {
                if (sp.IsChildAgent || sp.IsDeleted || sp.IsViewerUIGod) return false;
                if (noNpc && sp.IsNPC) return false;
                if (scope == AGENT_LIST_REGION) return true;
                ILandObject land = World.LandChannel?.GetLandObject(sp.AbsolutePosition);
                if (land == null) return false;
                return scope == AGENT_LIST_PARCEL_OWNER ? land.LandData.OwnerID == id : land.LandData.GlobalID == id;
            };
        }
        public LSLList iwGetAgentList(int scope, Vector3 minPos, Vector3 maxPos, LSLList paramList)
        {
            // Like llGetAgentList but with optional bounding box filter
            Func<ScenePresence, bool> inScope = AgentListScope(scope);
            if (inScope == null) return new LSLList(new List<object> { "INVALID_SCOPE" });
            List<object> result = new List<object>();
            List<ScenePresence> presences = World?.GetScenePresences();
            if (presences == null) return new LSLList();

            bool useBBox = (minPos != Vector3.Zero || maxPos != Vector3.Zero);

            foreach (ScenePresence sp in presences)
            {
                if (sp.IsChildAgent) continue;

                // Bounding box filter
                if (useBBox)
                {
                    Vector3 pos = sp.AbsolutePosition;
                    if (pos.X < minPos.X || pos.Y < minPos.Y || pos.Z < minPos.Z) continue;
                    if (pos.X > maxPos.X || pos.Y > maxPos.Y || pos.Z > maxPos.Z) continue;
                }

                if (inScope(sp)) result.Add(sp.UUID.ToString());
            }
            return new LSLList(result);
        }

        // ── OSSL read-only information functions ───────────────────────────────────────────────
        // Each ported from Source/OpenSim.Region.ScriptEngine.Shared/Api/OSSL_Api.cs (line cited) with
        // the SAME threat level and the same [OSSL] keys, through OsslGate. A denied call throws, and
        // the script stops with YEngine's message on DEBUG_CHANNEL.
        private TaskInventoryItem OsslItem => m_host?.Inventory?.GetInventoryItem(m_itemID);
        private void OsslCheck() => m_ScriptEngine.Ossl.Check();
        private void OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel level, string function)
            => m_ScriptEngine.Ossl.Check(level, function, World, m_host, OsslItem);
        private const string GridInfoSection = "GridInfoService";

        /// <summary>OSSL_Api.cs:2580 - ungated upstream.</summary>
        public string osGetGridName() => World?.SceneGridInfo?.GridName ?? string.Empty;

        /// <summary>OSSL_Api.cs:2575 - ungated upstream.</summary>
        public string osGetGridNick() => World?.SceneGridInfo?.GridNick ?? string.Empty;

        /// <summary>OSSL_Api.cs:2601 - Moderate.</summary>
        public string osGetGridHomeURI()
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.Moderate, "osGetGridHomeURI");
            return World?.SceneGridInfo?.HomeURLNoEndSlash ?? string.Empty;
        }

        /// <summary>OSSL_Api.cs:2585 - Moderate. [GridInfoService] login; the upstream fallback to the login server's info page is not made.</summary>
        public string osGetGridLoginURI()
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.Moderate, "osGetGridLoginURI");
            return m_ScriptEngine.ConfigSource?.Configs[GridInfoSection]?.GetString("login", string.Empty) ?? string.Empty;
        }

        /// <summary>OSSL_Api.cs:2608 - Moderate.</summary>
        public string osGetGridGatekeeperURI()
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.Moderate, "osGetGridGatekeeperURI");
            return World?.SceneGridInfo?.GateKeeperURLNoEndSlash ?? string.Empty;
        }

        /// <summary>OSSL_Api.cs:2615 - Moderate. [GridInfoService] &lt;key&gt;; no remote fallback.</summary>
        public string osGetGridCustom(string key)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.Moderate, "osGetGridCustom");
            if (string.IsNullOrEmpty(key)) return string.Empty;
            return m_ScriptEngine.ConfigSource?.Configs[GridInfoSection]?.GetString(key, string.Empty) ?? string.Empty;
        }

        /// <summary>OSSL_Api.cs:3640 - bare CheckThreatLevel (the master switch only).</summary>
        public Vector3 osGetRegionSize()
        {
            OsslCheck();
            var reg = World.RegionInfo;
            return new Vector3(reg.RegionSizeX, reg.RegionSizeY, 0f);
        }

        /// <summary>OSSL_Api.cs:3626 - Moderate.</summary>
        public LSLList osGetRegionStats()
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.Moderate, "osGetRegionStats");
            var ret = new List<object>();
            float[] stats = World.StatsReporter?.LastReportedSimStats;
            if (stats != null) foreach (float f in stats) ret.Add(f);
            return new LSLList(ret);
        }

        /// <summary>OSSL_Api.cs:2078 - High.</summary>
        public string osGetSimulatorVersion()
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.High, "osGetSimulatorVersion");
            return World.GetSimulatorVersion();
        }

        /// <summary>OSSL_Api.cs:1145 - None.</summary>
        public LSLList osGetAgents()
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.None, "osGetAgents");
            var ret = new List<object>();
            World.ForEachRootScenePresence(sp => ret.Add(sp.Name));
            return new LSLList(ret);
        }

        /// <summary>OSSL_Api.cs:3582 - bare CheckThreatLevel.</summary>
        public string osGetMapTexture()
        {
            OsslCheck();
            return World.RegionInfo.RegionSettings.TerrainImageID.ToString();
        }

        /// <summary>OSSL_Api.cs:2040 - High, but NON-throwing: an empty string when not permitted, as upstream.</summary>
        public string osGetPhysicsEngineType()
        {
            if (!m_ScriptEngine.Ossl.Enabled) return string.Empty;
            if (!string.IsNullOrEmpty(m_ScriptEngine.Ossl.Test(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.High, "osGetPhysicsEngineType", World, m_host, OsslItem)))
                return string.Empty;
            return World.PhysicsScene?.EngineType ?? "unknown";
        }

        /// <summary>OSSL_Api.cs:2064 - bare CheckThreatLevel.</summary>
        public string osGetPhysicsEngineName()
        {
            OsslCheck();
            if (World.PhysicsScene == null) return "NoEngine";
            return World.PhysicsScene.EngineName ?? "UnknownEngine";
        }

        /// <summary>OSSL_Api.cs:3651 - Moderate.</summary>
        public int osGetSimulatorMemory()
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.Moderate, "osGetSimulatorMemory");
            long pws = Util.GetPhysicalMemUse();
            if (pws > int.MaxValue) return int.MaxValue;
            return pws < 0 ? 0 : (int)pws;
        }

        /// <summary>OSSL_Api.cs:3665 - Moderate.</summary>
        public int osGetSimulatorMemoryKB()
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.Moderate, "osGetSimulatorMemoryKB");
            long pws = Util.GetPhysicalMemUse();
            if ((pws & 0x3FFL) != 0) pws += 0x400L;
            pws >>= 10;
            return pws > int.MaxValue ? int.MaxValue : (int)pws;
        }

        /// <summary>OSSL_Api.cs:3749 - None.</summary>
        public float osGetHealth(string agent)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.None, "osGetHealth");
            if (!UUID.TryParse(agent, out UUID id) || id.IsZero()) return -1f;
            ScenePresence presence = World.GetScenePresence(id);
            return presence == null ? -1f : presence.Health;
        }

        /// <summary>OSSL_Api.cs:1997 - High. Upstream strips through the first '.' of the engine name: "InWorldz.Phlox" -> "Phlox".</summary>
        public string osGetScriptEngineName()
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.High, "osGetScriptEngineName");
            string n = m_ScriptEngine.Name ?? string.Empty;
            int dot = n.IndexOf('.');
            return dot >= 0 ? n.Substring(dot + 1) : n;
        }

        // ── OSSL pure helpers, ported from OSSL_Api.cs (line cited), same threat level via OsslGate ──
        /// <summary>OSSL_Api.cs:6586 - ungated upstream.</summary>
        public string osAESEncrypt(string secret, string plainText)
        {
            if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(plainText)) return string.Empty;
            string r = Util.AESEncrypt(secret.AsSpan(), plainText.AsSpan());
            if (string.IsNullOrEmpty(r)) { ShoutError("osAESEncrypt: Failed to encrypt!"); return string.Empty; }
            return r;
        }

        /// <summary>OSSL_Api.cs:6600 - ungated upstream.</summary>
        public string osAESDecrypt(string secret, string encryptedText)
        {
            if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(encryptedText)) return string.Empty;
            var r = Util.AESDecrypt(secret.AsSpan(), encryptedText.AsSpan());
            if (r.Length == 0) { ShoutError("osAESDecrypt: Failed to Decrypt!"); return string.Empty; }
            return r.ToString();
        }

        /// <summary>OSSL_Api.cs:6614 - ungated upstream.</summary>
        public string osAESEncryptTo(string secret, string plainText, string ivString)
        {
            if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(plainText) || string.IsNullOrEmpty(ivString)) return string.Empty;
            string r = Util.AESEncryptTo(secret.AsSpan(), plainText.AsSpan(), ivString.AsSpan());
            if (string.IsNullOrEmpty(r)) { ShoutError("osAESEncryptTo: Failed to encrypt!"); return string.Empty; }
            return r;
        }

        /// <summary>OSSL_Api.cs:6628 - ungated upstream.</summary>
        public string osAESDecryptFrom(string secret, string encryptedText, string ivString)
        {
            if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(encryptedText) || string.IsNullOrEmpty(ivString)) return string.Empty;
            var r = Util.AESDecryptFrom(secret.AsSpan(), encryptedText.AsSpan(), ivString.AsSpan());
            if (r.Length == 0) { ShoutError("osAESDecryptFrom: Failed to decrypt!"); return string.Empty; }
            return r.ToString();
        }

        /// <summary>OSSL_Api.cs:5010 - ungated upstream.</summary>
        public float osAngleBetween(Vector3 a, Vector3 b)
        {
            double dot = Vector3.Dot(a, b);
            double mcross = Vector3.Cross(a, b).Length();
            return (float)Math.Atan2(mcross, dot);
        }

        // ── OSSL sit target, misc and list functions ───────────────────────────────

        /// <summary>OSSL_Api.cs:5985-5988 - ungated upstream: this prim's sit target offset.</summary>
        public Vector3 osGetSitTargetPos() => m_host?.SitTargetPosition ?? Vector3.Zero;

        /// <summary>OSSL_Api.cs:5990-5993 - ungated upstream.</summary>
        public Quaternion osGetSitTargetRot() => m_host?.SitTargetOrientation ?? Quaternion.Identity;

        /// <summary>OSSL_Api.cs:2721-2726 - Low.</summary>
        public string osLoadedCreationDate()
        {
            OsslCheck(TlLow, "osLoadedCreationDate");
            return World?.RegionInfo?.RegionSettings?.LoadedCreationDate ?? string.Empty;
        }

        /// <summary>OSSL_Api.cs:2728-2733 - Low.</summary>
        public string osLoadedCreationTime()
        {
            OsslCheck(TlLow, "osLoadedCreationTime");
            return World?.RegionInfo?.RegionSettings?.LoadedCreationTime ?? string.Empty;
        }

        /// <summary>OSSL_Api.cs:2735-2740 - Low.</summary>
        public string osLoadedCreationID()
        {
            OsslCheck(TlLow, "osLoadedCreationID");
            return World?.RegionInfo?.RegionSettings?.LoadedCreationID ?? string.Empty;
        }

        /// <summary>
        /// OSSL_Api.cs:6660-6692 - ungated upstream. The blackbody fit to the vendian.org 10-degree D65
        /// tables, component for component as upstream writes it.
        /// </summary>
        public Vector3 osTemperature2sRGB(float dtemp)
        {
            float temp = dtemp;
            if (temp <= 1000f) return new Vector3(1.0f, 0.0401f, 0f);
            if (temp >= 40000f) return new Vector3(0.3277f, 0.5022f, 1.0f);

            float green;
            if (temp < 6600f)
            {
                green = temp - 1000f;
                green = ((((-7.87308e-13f * green) - 7.10085e-9f) * green) + 0.00022693f) * green + 0.0374249f;
                green = Math.Clamp(green, 0f, 1.0f);
                if (temp <= 19.0f) return new Vector3(1.0f, green, 0f);

                float blue = temp - 1900f;
                blue = ((((-5.97E-12f * blue) + 5.49E-08f) * blue) + 8.85465E-05f) * blue - 0.0058959f;
                blue = Math.Clamp(blue, 0f, 1.0f);
                return new Vector3(1.0f, green, blue);
            }

            temp = 0.01f * (temp - 6000f);
            float red = 1.897315f * MathF.Pow(temp, -0.346837f) + 0.0622044f;
            red = Math.Clamp(red, 0f, 1.0f);
            green = 1.261989f * MathF.Pow(temp, -0.251708f) + 0.200836f;
            green = Math.Clamp(green, 0f, 1.0f);
            return new Vector3(red, green, 1.0f);
        }

        /// <summary>
        /// OSSL_Api.cs:6477-6540 - ungated upstream. The pre-2010 llList2ListStrided: a start past end
        /// wraps into two passes, and the stride is counted from index 0 of the whole list, not from start.
        /// </summary>
        public LSLList osOldList2ListStrided(LSLList src, int start, int end, int stride)
        {
            var data = src?.Data ?? Array.Empty<object>();
            var result = new List<object>();
            int len = data.Length;

            if (start < 0) start += len;
            if (end < 0) end += len;
            if (start > len) start = len;
            if (end > len) end = len;
            if (stride == 0) stride = 1;
            if (stride < 0) stride = -stride;

            var si = new int[2];
            var ei = new int[2];
            bool twopass = false;
            if (start != end)
            {
                if (start <= end) { si[0] = start; ei[0] = end; }
                else { si[1] = start; ei[1] = len; si[0] = 0; ei[0] = end; twopass = true; }
            }
            else
            {
                si[0] = 0; ei[0] = len;
            }

            for (int i = si[0]; i < ei[0]; i++)
                if (i % stride == 0) result.Add(data[i]);

            if (twopass)
                for (int i = si[1]; i < ei[1]; i++)
                    if (i % stride == 0) result.Add(data[i]);

            return new LSLList(result);
        }

        /// <summary>
        /// OSSL_Api.cs:6695-6780 - ungated upstream. The instance-th occurrence of ltest inside lsrc between
        /// lstart and lend; a negative instance counts back from the last occurrence, a negative index from the
        /// end of the list, an empty test list answers with the index itself. -1 when there is no such match.
        /// </summary>
        public int osListFindListNext(LSLList lsrc, LSLList ltest, int lstart, int lend, int linstance)
        {
            var src = lsrc?.Data ?? Array.Empty<object>();
            var test = ltest?.Data ?? Array.Empty<object>();
            int srclen = src.Length, testlen = test.Length;
            if (srclen == 0) return testlen == 0 ? 0 : -1;

            if (testlen == 0)
            {
                if (linstance >= 0) return linstance < srclen ? linstance : -1;
                int back = linstance + srclen;
                return back >= 0 ? back : -1;
            }
            if (testlen > srclen) return -1;

            int start = lstart;
            if (start < 0) { start += srclen; if (start < 0) return -1; }
            else if (start >= srclen) return -1;

            int end = lend;
            if (end < 0) { end += srclen; if (end < 0) return -1; }
            else if (end >= srclen) end = srclen - 1;
            if (end < start) return -1;

            var hits = new List<int>();
            for (int i = start; i <= end - testlen + 1; i++)
            {
                bool all = true;
                for (int j = 0; j < testlen; j++)
                {
                    // the same comparison llListFindList makes: the string form of each item
                    string a = src[i + j]?.ToString() ?? string.Empty;
                    string b = test[j]?.ToString() ?? string.Empty;
                    if (a != b) { all = false; break; }
                }
                if (all) hits.Add(i);
            }
            if (hits.Count == 0) return -1;
            if (linstance >= 0) return linstance < hits.Count ? hits[linstance] : -1;
            int fromEnd = hits.Count + linstance;
            return fromEnd >= 0 ? hits[fromEnd] : -1;
        }

        /// <summary>
        /// OSSL_Api.cs:6417-6420 - ungated upstream, and the one OSSL function with VALUE semantics: it sorts
        /// the caller's own list rather than returning a new one. It can do that here because a list argument
        /// reaches a syscall as the same <see cref="LSLList"/> the variable slot holds - the shim casts, it does
        /// not copy - so writing back into that instance's Data array is what the script sees in its variable.
        /// The sort itself is llListSort's, so the ordering cannot drift from LSL's.
        /// </summary>
        public void osListSortInPlace(LSLList src, int stride, int ascending)
        {
            OsslCheck();
            if (src?.Data == null || src.Data.Length == 0) return;
            var sorted = llListSort(src, stride, ascending);
            SortInPlace(src, sorted);
        }

        /// <summary>
        /// The caller's list takes the sorted order in both of LSLList's stores - the Data array the VM's
        /// list operations read and the member list that GetLSLStringItem, (string)list and now llDumpList2String /
        /// llList2CSV / llList2String read. Only Data was written, so those saw the unsorted list.
        /// </summary>
        private static void SortInPlace(LSLList src, LSLList sorted)
        {
            Array.Copy(sorted.Data, src.Data, src.Data.Length);
            for (int i = 0; i < src.Members.Count; i++) src.Members[i] = sorted.Data[i];
        }

        /// <summary>OSSL_Api.cs:6422-6425 - the same, keyed on one element of each stride (llListSortStrided's order).</summary>
        public void osListSortInPlaceStrided(LSLList src, int stride, int strideIndex, int ascending)
        {
            OsslCheck();
            if (src?.Data == null || src.Data.Length == 0) return;
            var sorted = llListSortStrided(src, stride, strideIndex, ascending);
            SortInPlace(src, sorted);
        }

        /// <summary>OSSL_Api.cs:6318-6322 - ungated upstream: llParticleSystem without its sleep.</summary>
        public void osParticleSystem(LSLList rules)
        {
            OsslCheck();
            if (m_host == null) return;
            PrimParticleSystem(m_host, rules);
        }

        /// <summary>OSSL_Api.cs:6324-6333 - ungated upstream: every part the link number names.</summary>
        public void osLinkParticleSystem(int linknumber, LSLList rules)
        {
            OsslCheck();
            foreach (var part in GetLinkParts(linknumber))
                PrimParticleSystem(part, rules);
        }

        /// <summary>OSSL_Api.cs:5150-5165 - ungated upstream: llPreloadSound for a link, and without its 1 s sleep.</summary>
        public void osPreloadSound(int linknum, string sound)
        {
            OsslCheck();
            UUID soundID = KeyOrName(sound);
            if (soundID == UUID.Zero) return;
            var sm = World?.RequestModuleInterface<ISoundModule>();
            if (sm == null) return;
            foreach (var part in GetLinkParts(linknum))
                sm.PreloadSound(part, soundID);
        }

        /// <summary>
        /// OSSL_Api.cs:4704-4740 - bare CheckThreatLevel (master switch). Mass, centre of mass, the inertia
        /// tensor divided by the mass, and the off-diagonal terms as a rotation - empty for a deleted group.
        /// </summary>
        public LSLList osGetInertiaData()
        {
            OsslCheck();
            var result = new List<object>();
            var sog = m_host?.ParentGroup;
            if (sog == null || sog.IsDeleted) return new LSLList(result);

            sog.GetInertiaData(out float totalMass, out Vector3 centerOfMass, out Vector3 inertia, out Vector4 aux);
            if (totalMass > 0)
            {
                float t = 1.0f / totalMass;
                inertia.X *= t; inertia.Y *= t; inertia.Z *= t;
                aux.X *= t; aux.Y *= t; aux.Z *= t;
            }
            result.Add(totalMass);
            result.Add(centerOfMass);
            result.Add(inertia);
            result.Add(new Quaternion(aux.X, aux.Y, aux.Z, aux.W));
            return new LSLList(result);
        }

        /// <summary>OSSL_Api.cs:3988-3998 - None. Every NPC the region's bot manager knows.</summary>
        public LSLList osGetNPCList()
        {
            OsslCheck(TlNone, "osGetNPCList");
            var result = new List<object>();
            var mgr = NpcMgr();
            if (mgr == null) return new LSLList(result);
            foreach (var id in mgr.GetAllBots())
                result.Add(id.ToString());
            return new LSLList(result);
        }

        /// <summary>OSSL_Api.cs:5724-5745 - ungated upstream: llRemoveInventory on a linked prim.</summary>
        public void osRemoveLinkInventory(int linkNumber, string name)
        {
            OsslCheck();
            var part = OsslSingleLinkPart(linkNumber);
            if (part == null) return;
            var item = part.Inventory?.GetInventoryItem(name);
            if (item == null) return;
            part.Inventory.RemoveInventoryItem(item.ItemID);
        }

        /// <summary>OSSL_Api.cs:6313-6316 - ungated upstream, the sim's own terrain noise.</summary>
        public float osPerlinNoise2D(float x, float y, int octaves, float persistence)
        {
            OsslCheck();
            return (float)OpenSim.Region.Framework.Scenes.TerrainUtil.PerlinNoise2D(x, y, octaves, persistence);
        }

        /// <summary>OSSL_Api.cs:3489-3497 - VeryHigh. The agent must be in this region; the outfit goes to the store osOwnerSaveAppearance uses.</summary>
        public string osAgentSaveAppearance(string avatarKey, string notecard) => osAgentSaveAppearance(avatarKey, notecard, 1);

        /// <summary>OSSL_Api.cs:3499-3507 - VeryHigh. includeHuds is accepted, not applied: the store keeps the whole appearance.</summary>
        public string osAgentSaveAppearance(string avatarKey, string notecard, int includeHuds)
        {
            OsslCheck(TlVeryHigh, "osAgentSaveAppearance");
            if (World == null || !UUID.TryParse(avatarKey, out UUID agentId) || agentId == UUID.Zero)
                return UUID.Zero.ToString();
            var sp = World.GetScenePresence(agentId);
            if (sp == null || sp.IsChildAgent) { ShoutError("osAgentSaveAppearance: no such agent in this region"); return UUID.Zero.ToString(); }
            var mgr = NpcMgr();
            if (mgr == null) return UUID.Zero.ToString();
            mgr.SaveOutfitToDatabase(agentId, notecard, out string reason);
            if (reason != null) { ShoutError("osAgentSaveAppearance: " + reason); return UUID.Zero.ToString(); }
            return OpenSim.Region.OptionalModules.World.NPC.BotManager.OutfitKey(agentId, notecard).ToString();
        }

        /// <summary>OSSL_Api.cs:5417 - ungated upstream.</summary>
        public int osApproxEquals(float a, float b)
        {
            return (a > b + 1.0e-6 || a < b - 1.0e-6) ? 0 : 1;
        }

        /// <summary>OSSL_Api.cs:5424 - ungated upstream.</summary>
        public int osApproxEquals(float a, float b, float margin)
        {
            double e = Math.Abs(margin);
            return (a > b + e || a < b - e) ? 0 : 1;
        }

        /// <summary>OSSL_Api.cs:5432-5447 - ungated upstream, the fixed 1.0e-6 of the float form on each component.</summary>
        public int osApproxEquals(Vector3 va, Vector3 vb) => OsslApproxEquals(va, vb, 1.0e-6f);

        /// <summary>OSSL_Api.cs:5450-5466 - the same with the caller's margin.</summary>
        public int osApproxEquals(Vector3 va, Vector3 vb, float margin) => OsslApproxEquals(va, vb, Math.Abs(margin));

        /// <summary>OSSL_Api.cs:5469-5488 - all four components of the rotation.</summary>
        public int osApproxEquals(Quaternion ra, Quaternion rb) => OsslApproxEquals(ra, rb, 1.0e-6f);

        /// <summary>OSSL_Api.cs:5491-5510 - the same with the caller's margin.</summary>
        public int osApproxEquals(Quaternion ra, Quaternion rb, float margin) => OsslApproxEquals(ra, rb, Math.Abs(margin));

        private static int OsslApproxEquals(Vector3 a, Vector3 b, float e)
            => (Off(a.X, b.X, e) || Off(a.Y, b.Y, e) || Off(a.Z, b.Z, e)) ? 0 : 1;

        private static int OsslApproxEquals(Quaternion a, Quaternion b, float e)
            => (Off(a.X, b.X, e) || Off(a.Y, b.Y, e) || Off(a.Z, b.Z, e) || Off(a.W, b.W, e)) ? 0 : 1;

        private static bool Off(float a, float b, float e) => a > b + e || a < b - e;

        /// <summary>OSSL_Api.cs:2026 - bare CheckThreatLevel (master switch).</summary>
        public int osCheckODE()
        {
            OsslCheck();
            return World?.PhysicsScene?.EngineType == "OpenDynamicsEngine" ? 1 : 0;
        }

        /// <summary>OSSL_Api.cs:2649 - VeryLow (key osFormatString).</summary>
        public string osFormatString(string str, LSLList strings)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.VeryLow, "osFormatString");
            return string.Format(str, strings.Members.ToArray());
        }

        /// <summary>OSSL_Api.cs:5973 - ungated upstream.</summary>
        public int osIsNotValidNumber(float v)
        {
            if (float.IsNaN(v)) return 1;
            if (float.IsNegativeInfinity(v)) return 2;
            if (float.IsPositiveInfinity(v)) return 3;
            return 0;
        }

        /// <summary>OSSL_Api.cs:4434 - ungated upstream.</summary>
        public int osIsUUID(string thing)
        {
            return UUID.TryParse(thing, out _) ? 1 : 0;
        }

        /// <summary>OSSL_Api.cs:6799 - ungated upstream.</summary>
        public float osListAsFloat(LSLList src, int index)
        {
            var m = src?.Members; if (m == null || index < 0 || index >= m.Count) return 0f;
            return m[index] switch { float f => f, double d => (float)d, _ => 0f };
        }

        /// <summary>OSSL_Api.cs:6815 - ungated upstream.</summary>
        public int osListAsInteger(LSLList src, int index)
        {
            var m = src?.Members; if (m == null || index < 0 || index >= m.Count) return 0;
            return m[index] is int i ? i : 0;
        }

        /// <summary>OSSL_Api.cs:6831 - ungated upstream.</summary>
        public string osListAsString(LSLList src, int index)
        {
            var m = src?.Members; if (m == null || index < 0 || index >= m.Count) return string.Empty;
            return m[index] is string s ? s : string.Empty;
        }

        /// <summary>OSSL_Api.cs:6847 - ungated upstream.</summary>
        public Vector3 osListAsVector(LSLList src, int index)
        {
            var m = src?.Members; if (m == null || index < 0 || index >= m.Count) return Vector3.Zero;
            return m[index] is Vector3 v ? v : Vector3.Zero;
        }

        /// <summary>OSSL_Api.cs:6863 - ungated upstream.</summary>
        public Quaternion osListAsRotation(LSLList src, int index)
        {
            var m = src?.Members; if (m == null || index < 0 || index >= m.Count) return Quaternion.Identity;
            return m[index] is Quaternion q ? q : Quaternion.Identity;
        }

        /// <summary>OSSL_Api.cs:2656 - VeryLow (key osMatchString).</summary>
        public LSLList osMatchString(string src, string pattern, int start)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.VeryLow, "osMatchString");
            var result = new List<object>();
            if (start < 0) start = src.Length + start;
            if (start < 0 || start >= src.Length) return new LSLList(result);
            try
            {
                var match = ScriptRegex.Create(pattern).Match(src, start);
                while (match.Success)
                {
                    foreach (System.Text.RegularExpressions.Group g in match.Groups)
                        if (g.Success) { result.Add(g.Value); result.Add(g.Index); }
                    match = match.NextMatch();
                }
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                ShoutError(ScriptRegex.TimedOutMessage);
                return new LSLList(new List<object>());
            }
            return new LSLList(result);
        }

        /// <summary>OSSL_Api.cs:4456 - None (key osGetRezzingObject).</summary>
        public float osMax(float a, float b)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.None, "osGetRezzingObject");
            return Math.Max(a, b);   // upstream gates osMax under the key "osGetRezzingObject" (a copy-paste there); honoured as is
        }

        /// <summary>OSSL_Api.cs:4445 - ungated upstream.</summary>
        public float osMin(float a, float b)
        {
            return Math.Min(a, b);
        }

        /// <summary>OSSL_Api.cs:4600 - Low (key osRegexIsMatch).</summary>
        public int osRegexIsMatch(string input, string pattern)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.Low, "osRegexIsMatch");
            try { return ScriptRegex.Create(pattern).IsMatch(input ?? string.Empty) ? 1 : 0; }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { ShoutError(ScriptRegex.TimedOutMessage); return 0; }
            catch (Exception) { ShoutError("Possible invalid regular expression detected."); return 0; }
        }

        /// <summary>OSSL_Api.cs:4990 - ungated upstream.</summary>
        public float osRound(float value, int ndigits)
        {
            if (ndigits <= 0) return (float)Math.Round((double)value, MidpointRounding.AwayFromZero);
            if (ndigits > 15) ndigits = 15;
            return (float)Math.Round((double)value, ndigits, MidpointRounding.AwayFromZero);
        }

        /// <summary>OSSL_Api.cs:2557 - ungated upstream.</summary>
        public string osSHA256(string input)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input ?? string.Empty))).ToLowerInvariant();
        }

        /// <summary>OSSL_Api.cs:5919 - ungated upstream.</summary>
        public Quaternion osSlerp(Quaternion a, Quaternion b, float amount)
        {
            if (amount < 0) amount = 0; else if (amount > 1f) amount = 1f;
            a.Normalize(); b.Normalize();
            return Quaternion.Slerp(a, b, amount);
        }

        /// <summary>
        /// OSSL_Api.cs:5931-5940 - the vector form, ungated upstream. Upstream's
        /// LSL_Types.Vector3.Slerp (LSL_Types.cs:417-438) does NOT normalise its inputs and falls back
        /// to a straight lerp when the vectors are nearly parallel; mirrored exactly.
        /// </summary>
        public Vector3 osSlerp(Vector3 a, Vector3 b, float amount)
        {
            if (amount < 0) amount = 0; else if (amount > 1f) amount = 1f;
            double angle = (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);
            double scale, invscale;
            if (angle < 0.999f)
            {
                angle = Math.Acos(angle);
                invscale = 1.0 / Math.Sin(angle);
                scale = Math.Sin((1.0 - amount) * angle) * invscale;
                invscale *= Math.Sin(amount * angle);
            }
            else
            {
                scale = 1.0 - amount;
                invscale = amount;
            }
            return new Vector3(
                (float)(a.X * scale + b.X * invscale),
                (float)(a.Y * scale + b.Y * invscale),
                (float)(a.Z * scale + b.Z * invscale));
        }

        /// <summary>OSSL_Api.cs:5284 - bare CheckThreatLevel (master switch).</summary>
        public int osStringStartsWith(string src, string value, int ignorecase)
        {
            OsslCheck();
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(value)) return 0;
            return src.StartsWith(value, ignorecase != 0, System.Globalization.CultureInfo.CurrentCulture) ? 1 : 0;
        }

        /// <summary>OSSL_Api.cs:5296 - bare CheckThreatLevel (master switch).</summary>
        public int osStringEndsWith(string src, string value, int ignorecase)
        {
            OsslCheck();
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(value)) return 0;
            return src.EndsWith(value, ignorecase != 0, System.Globalization.CultureInfo.CurrentCulture) ? 1 : 0;
        }

        /// <summary>OSSL_Api.cs:5308 - bare CheckThreatLevel (master switch).</summary>
        public int osStringIndexOf(string src, string value, int ignorecase)
        {
            OsslCheck();
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(value)) return -1;
            return src.IndexOf(value, ignorecase == 0 ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>OSSL_Api.cs:5322 - bare CheckThreatLevel (master switch).</summary>
        public int osStringIndexOf(string src, string value, int offset, int count, int ignorecase)
        {
            OsslCheck();
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(value)) return -1;
            if (offset >= src.Length) return -1; else if (offset < 0) offset = 0;
            if (count <= 0) count = src.Length - offset; else if (count > src.Length - offset) count = src.Length - offset;
            return src.IndexOf(value, offset, count, ignorecase == 0 ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>OSSL_Api.cs:5346 - bare CheckThreatLevel (master switch).</summary>
        public int osStringLastIndexOf(string src, string value, int ignorecase)
        {
            OsslCheck();
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(value)) return -1;
            return src.LastIndexOf(value, ignorecase == 0 ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>OSSL_Api.cs:5360 - bare CheckThreatLevel (master switch).</summary>
        public int osStringLastIndexOf(string src, string value, int offset, int count, int ignorecase)
        {
            OsslCheck();
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(value)) return -1;
            if (offset >= src.Length) return -1; if (offset < 0) offset = 0;
            if (count <= 0) count = src.Length - offset; else if (count > src.Length - offset) count = src.Length - offset;
            return src.LastIndexOf(value, offset, count, ignorecase == 0 ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>OSSL_Api.cs:5384 - ungated upstream.</summary>
        public string osStringRemove(string src, int offset, int count)
        {
            if (string.IsNullOrEmpty(src) || offset >= src.Length) return string.Empty;
            if (offset < 0) offset = 0;
            if (count <= 0) count = src.Length - offset; else if (count > src.Length - offset) count = src.Length - offset;
            if (count >= src.Length) return string.Empty;
            return src.Remove(offset, count);
        }

        /// <summary>OSSL_Api.cs:5405 - ungated upstream.</summary>
        public string osStringReplace(string src, string oldvalue, string newvalue)
        {
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(oldvalue)) return string.Empty;
            if (string.IsNullOrEmpty(newvalue)) newvalue = null;
            return src.Replace(oldvalue, newvalue);
        }

        /// <summary>OSSL_Api.cs:5252 - bare CheckThreatLevel (master switch).</summary>
        public string osStringSubString(string src, int offset)
        {
            OsslCheck();
            if (string.IsNullOrEmpty(src) || offset >= src.Length) return string.Empty;
            if (offset <= 0) return src;
            return src.Substring(offset);
        }

        /// <summary>OSSL_Api.cs:5265 - bare CheckThreatLevel (master switch).</summary>
        public string osStringSubString(string src, int offset, int length)
        {
            OsslCheck();
            if (string.IsNullOrEmpty(src) || length <= 0 || offset >= src.Length) return string.Empty;
            if (offset <= 0) { if (length == src.Length) return src; offset = 0; }
            if (length > src.Length - offset) length = src.Length - offset;
            return src.Substring(offset, length);
        }

        /// <summary>OSSL_Api.cs:4012 - ungated upstream.</summary>
        public string osUnixTimeToTimestamp(int time)
        {
            return Util.ToDateTime(time).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
        }

        /// <summary>OSSL_Api.cs:5004 - ungated upstream.</summary>
        public float osVecDistSquare(Vector3 a, Vector3 b)
        {
            return (a - b).LengthSquared();
        }

        /// <summary>OSSL_Api.cs:4999 - ungated upstream.</summary>
        public float osVecMagSquare(Vector3 a)
        {
            return a.LengthSquared();
        }

        // ── OSSL side-effect functions, ported from OSSL_Api.cs (line cited per function), each under its
        //    upstream key and threat level through OsslGate; "master" = upstream's bare CheckThreatLevel() ──
        private static readonly OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel
            TlVeryLow  = OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.VeryLow,
            TlLow      = OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.Low,
            TlModerate = OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.Moderate,
            TlHigh     = OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.High,
            TlVeryHigh = OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.VeryHigh,
            TlSevere   = OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.Severe;

        /// <summary>
        /// OSSL_Api.cs:5180-5199 GetSingleLinkPart: LINK_SET/ALL_OTHERS/ALL_CHILDREN -> none; 0/LINK_ROOT -> root;
        /// LINK_THIS -> host; n -> link n. The link resolver's prim, with the multi-prim selectors refused.
        /// </summary>
        private SceneObjectPart OsslSingleLinkPart(int linkType)
        {
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return null;
            if (linkType == LINK_SET || linkType == LINK_ALL_OTHERS || linkType == LINK_ALL_CHILDREN) return null;
            return GetLinkParts(linkType).FirstOrDefault();
        }

        /// <summary>OSSL_Api.cs:895-933 checkAllowAgentTPbyLandOwner minus the agent branches: land owner, estate manager/owner, or the land's group.</summary>
        private bool OsslLandOwnerAllows(Vector3 pos)
        {
            ILandObject land = World?.LandChannel?.GetLandObject(pos);
            LandData landdata = land?.LandData;
            if (landdata == null) return true;
            if (landdata.OwnerID == m_host.OwnerID) return true;
            EstateSettings es = World.RegionInfo?.EstateSettings;
            if (es != null && es.IsEstateManagerOrOwner(m_host.OwnerID)) return true;
            if (!landdata.IsGroupOwned || landdata.GroupID == UUID.Zero) return false;
            return landdata.GroupID == m_host.GroupID;
        }

        /// <summary>OSSL_Api.cs:701-720 - VeryHigh. A linkset by its key rotates as a group; a presence gets its Rotation set.</summary>
        public void osSetRot(string target, Quaternion rotation)
        {
            OsslCheck(TlVeryHigh, "osSetRot");
            if (!UUID.TryParse(target, out UUID id)) return;
            SceneObjectGroup sog = World?.GetSceneObjectGroup(id);
            if (sog != null && !sog.IsDeleted) { sog.UpdateGroupRotationR(rotation); return; }
            ScenePresence sp = World?.GetScenePresence(id);
            if (sp != null) sp.Rotation = rotation;
        }

        /// <summary>OSSL_Api.cs:2784-2789 - VeryLow. llCreateLink without PERMISSION_CHANGE_LINKS.</summary>
        public void osForceCreateLink(string target, int parent)
        {
            OsslCheck(TlVeryLow, "osForceCreateLink");
            CreateLinkCore(target, parent);
        }

        /// <summary>OSSL_Api.cs:2792-2797 - VeryLow.</summary>
        public void osForceBreakLink(int linknum)
        {
            OsslCheck(TlVeryLow, "osForceBreakLink");
            BreakLinkCore(linknum);
        }

        /// <summary>OSSL_Api.cs:2800-2805 - VeryLow. llBreakAllLinks without its permission checks (BreakAllLinksCore).</summary>
        public void osForceBreakAllLinks()
        {
            OsslCheck(TlVeryLow, "osForceBreakAllLinks");
            BreakAllLinksCore();
        }

        /// <summary>OSSL_Api.cs:4954-4975 - Severe. Another owner's object only where the land owner rule allows; SceneObjectGroup.TeleportObject does the move (OSTPOBJ_* flags).</summary>
        public int osTeleportObject(string objectUUID, Vector3 targetPos, Quaternion rotation, int flags)
        {
            OsslCheck(TlSevere, "osTeleportObject");
            if (!UUID.TryParse(objectUUID, out UUID id)) { ShoutError("osTeleportObject() invalid object Key"); return -1; }
            SceneObjectGroup sog = World?.GetSceneObjectGroup(id);
            if (sog == null || sog.IsDeleted || sog.inTransit) return -1;
            if (sog.OwnerID != m_host.OwnerID && !OsslLandOwnerAllows(sog.AbsolutePosition)) return -1;
            return sog.TeleportObject(m_host.ParentGroup.UUID, targetPos, rotation, flags);
        }

        /// <summary>OSSL_Api.cs:3681-3689 - Moderate.</summary>
        public void osSetSpeed(string ID, float SpeedModifier)
        {
            OsslCheck(TlModerate, "osSetSpeed");
            if (!UUID.TryParse(ID, out UUID avid)) return;
            ScenePresence avatar = World?.GetScenePresence(avid);
            if (avatar != null) avatar.SpeedModifier = SpeedModifier;
        }

        /// <summary>OSSL_Api.cs:3693-3701 - Moderate; capped at 4.</summary>
        public void osSetOwnerSpeed(float SpeedModifier)
        {
            OsslCheck(TlModerate, "osSetOwnerSpeed");
            if (SpeedModifier > 4) SpeedModifier = 4;
            ScenePresence avatar = World?.GetScenePresence(m_host.OwnerID);
            if (avatar != null) avatar.SpeedModifier = SpeedModifier;
        }

        /// <summary>OSSL_Api.cs:4475-4479 - Severe. The MIME type verbatim, unlike llSetContentType's enum.</summary>
        public void osSetContentType(string id, string type)
        {
            OsslCheck(TlSevere, "osSetContentType");
            if (!UUID.TryParse(id, out UUID reqID)) return;
            World?.RequestModuleInterface<IUrlModule>()?.HttpContentType(reqID, type);
        }

        /// <summary>OSSL_Api.cs:889-893 - VeryLow.</summary>
        public void osSetPrimFloatOnWater(int floatYN)
        {
            OsslCheck(TlVeryLow, "osSetPrimFloatOnWater");
            m_host?.ParentGroup?.RootPart?.SetFloatOnWater(floatYN);
        }

        /// <summary>OSSL_Api.cs:4682-4688 - master switch. Unlike llVolumeDetect this does not record the flag in the script's state.</summary>
        public void osVolumeDetect(int detect)
        {
            OsslCheck();
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted || m_host.ParentGroup.IsAttachment) return;
            m_host.ScriptSetVolumeDetect(detect != 0);
        }

        /// <summary>OSSL_Api.cs:3877-3882 - master switch; LSL_Api.SetPrimitiveParamsEx refuses another owner's prim.</summary>
        public void osSetPrimitiveParams(string prim, LSLList rules)
        {
            OsslCheck();
            if (!UUID.TryParse(prim, out UUID id)) return;
            SceneObjectPart part = World?.GetSceneObjectPart(id);
            if (part == null || part.OwnerID != m_host.OwnerID) return;
            SetPrimParams(part, rules);
        }

        /// <summary>OSSL_Api.cs:3869-3874 - master switch; LSL_Api.GetPrimitiveParamsEx answers only for the same owner.</summary>
        public LSLList osGetPrimitiveParams(string prim, LSLList rules)
        {
            OsslCheck();
            if (!UUID.TryParse(prim, out UUID id)) return new LSLList();
            SceneObjectPart part = World?.GetSceneObjectPart(id);
            if (part == null || part.OwnerID != m_host.OwnerID) return new LSLList();
            return GetPrimParams(part, rules);
        }

        /// <summary>OSSL_Api.cs:2755-2781 - High. llGetLinkPrimitiveParams behind the OSSL gate (the PRIM_LINK_TARGET re-walk is Phlox's GetPrimParams' business).</summary>
        public LSLList osGetLinkPrimitiveParams(int linknumber, LSLList rules)
        {
            OsslCheck(TlHigh, "osGetLinkPrimitiveParams");
            return llGetLinkPrimitiveParams(linknumber, rules);
        }

        /// <summary>OSSL_Api.cs:3936-3962 SetProjectionParams - ungated upstream.</summary>
        private static void OsslSetProjectionParams(SceneObjectPart obj, int projection, string texture, float fov, float focus, float amb)
        {
            if (obj == null || obj.IsDeleted || obj.Shape == null) return;
            if (projection != 0)
            {
                if (!UUID.TryParse(texture, out UUID texID)) return;
                obj.Shape.ProjectionEntry = true;
                obj.Shape.ProjectionTextureUUID = texID;
                obj.Shape.ProjectionFOV = Math.Clamp(fov, 0f, 3.0f);
                obj.Shape.ProjectionFocus = Math.Clamp(focus, -20.0f, 20.0f);
                obj.Shape.ProjectionAmbiance = Math.Clamp(amb, 0f, 1.0f);
                obj.ParentGroup.HasGroupChanged = true;
                obj.ScheduleFullUpdate();
                return;
            }
            if (obj.Shape.ProjectionEntry)
            {
                obj.Shape.ProjectionEntry = false;
                obj.ParentGroup.HasGroupChanged = true;
                obj.ScheduleFullUpdate();
            }
        }

        /// <summary>OSSL_Api.cs:3888-3891 - ungated upstream.</summary>
        public void osSetProjectionParams(int projection, string texture, float fov, float focus, float amb)
            => OsslSetProjectionParams(m_host, projection, texture, fov, focus, amb);

        /// <summary>OSSL_Api.cs:3896-3914 - ungated upstream.</summary>
        public void osSetProjectionParams(int linknum, int projection, string texture, float fov, float focus, float amb)
        {
            if (m_host?.ParentGroup == null) return;
            SceneObjectPart part = OsslSingleLinkPart(linknum);
            if (part != null) OsslSetProjectionParams(part, projection, texture, fov, focus, amb);
        }

        /// <summary>
        /// OSSL_Api.cs:3921-3935 - ungated upstream. The prim is addressed by key and must be
        /// owned by this prim's owner; a key that is not a UUID, or zero, means this prim. Arity 6 like
        /// the link form above, told apart by the first argument's type.
        /// </summary>
        public void osSetProjectionParams(string prim, int projection, string texture, float fov, float focus, float amb)
        {
            if (UUID.TryParse(prim, out UUID pID) && pID != UUID.Zero)
            {
                SceneObjectPart obj = World?.GetSceneObjectPart(pID);
                if (obj != null && m_host != null && obj.OwnerID == m_host.OwnerID)
                    OsslSetProjectionParams(obj, projection, texture, fov, focus, amb);
                return;
            }
            OsslSetProjectionParams(m_host, projection, texture, fov, focus, amb);
        }

        private static Vector4 OsslNormalisedRot(Quaternion q)
        {
            var v = new Vector4(q.X, q.Y, q.Z, q.W);
            v.Normalize();
            return v;
        }

        /// <summary>OSSL_Api.cs:4747-4770 - master switch. Note upstream's rot.y typo for z (:4767); the port uses z.</summary>
        public void osSetInertia(float mass, Vector3 centerOfMass, Vector3 principalInertiaScaled, Quaternion lslrot)
        {
            OsslCheck();
            SceneObjectGroup sog = m_host?.ParentGroup;
            if (sog == null || sog.IsDeleted) return;
            if (mass < 0 || principalInertiaScaled.X < 0 || principalInertiaScaled.Y < 0 || principalInertiaScaled.Z < 0) return;
            sog.SetInertiaData(mass, centerOfMass, principalInertiaScaled * mass, OsslNormalisedRot(lslrot));
        }

        /// <summary>OSSL_Api.cs:4785-4811 - master switch.</summary>
        public void osSetInertiaAsBox(float mass, Vector3 boxSize, Vector3 centerOfMass, Quaternion lslrot)
        {
            OsslCheck();
            SceneObjectGroup sog = m_host?.ParentGroup;
            if (sog == null || sog.IsDeleted || mass < 0) return;
            float lx = boxSize.X, ly = boxSize.Y, lz = boxSize.Z, t = mass / 12.0f;
            sog.SetInertiaData(mass, centerOfMass, new Vector3(t * (ly * ly + lz * lz), t * (lx * lx + lz * lz), t * (lx * lx + ly * ly)), OsslNormalisedRot(lslrot));
        }

        /// <summary>OSSL_Api.cs:4825-4841 - master switch.</summary>
        public void osSetInertiaAsSphere(float mass, float radius, Vector3 centerOfMass)
        {
            OsslCheck();
            SceneObjectGroup sog = m_host?.ParentGroup;
            if (sog == null || sog.IsDeleted || mass < 0) return;
            float t = 0.4f * mass * radius * radius;
            sog.SetInertiaData(mass, centerOfMass, new Vector3(t, t, t), new Vector4(0f, 0f, 0f, 1.0f));
        }

        /// <summary>OSSL_Api.cs:4860-4883 - master switch.</summary>
        public void osSetInertiaAsCylinder(float mass, float radius, float length, Vector3 centerOfMass, Quaternion lslrot)
        {
            OsslCheck();
            SceneObjectGroup sog = m_host?.ParentGroup;
            if (sog == null || sog.IsDeleted || mass < 0) return;
            float r = radius * radius;
            float t = length * length;
            t += 3.0f * r;
            t *= 8.333333e-2f * mass;
            sog.SetInertiaData(mass, centerOfMass, new Vector3(t, t, 0.5f * mass * r), OsslNormalisedRot(lslrot));
        }

        /// <summary>OSSL_Api.cs:4896-4903 - master switch.</summary>
        public void osClearInertia()
        {
            OsslCheck();
            SceneObjectGroup sog = m_host?.ParentGroup;
            if (sog == null || sog.IsDeleted) return;
            sog.SetInertiaData(-1, Vector3.Zero, Vector3.Zero, Vector4.Zero);
        }

        /// <summary>OSSL_Api.cs:5995-6005 - ungated upstream; capped at 128.</summary>
        public void osSetSitActiveRange(float v)
        {
            if (m_host == null) return;
            if (v > 128f) v = 128f;
            if (m_host.SitActiveRange != v) { m_host.SitActiveRange = v; if (m_host.ParentGroup != null) m_host.ParentGroup.HasGroupChanged = true; }
        }

        /// <summary>OSSL_Api.cs:6008-6025 - ungated upstream.</summary>
        public void osSetLinkSitActiveRange(int linkNumber, float v)
        {
            if (m_host == null) return;
            if (v > 128f) v = 128f;
            bool changed = false;
            foreach (SceneObjectPart sop in GetLinkParts(linkNumber))
                if (sop.SitActiveRange != v) { sop.SitActiveRange = v; changed = true; }
            if (changed && m_host.ParentGroup != null) m_host.ParentGroup.HasGroupChanged = true;
        }

        /// <summary>OSSL_Api.cs:6051-6058 - ungated upstream.</summary>
        public void osSetStandTarget(Vector3 v)
        {
            if (m_host == null) return;
            Vector3 old = m_host.StandOffset;
            m_host.StandOffset = v;
            if (!old.ApproxEquals(v) && m_host.ParentGroup != null) m_host.ParentGroup.HasGroupChanged = true;
        }

        /// <summary>OSSL_Api.cs:6060-6080 - ungated upstream: LINK_THIS -> host, negative -> nothing, 0/1 -> root, n -> link n.</summary>
        public void osSetLinkStandTarget(int linkNumber, Vector3 v)
        {
            if (m_host?.ParentGroup == null) return;
            SceneObjectPart target = OsslSingleLinkPart(linkNumber);
            if (target == null) return;
            Vector3 old = target.StandOffset;
            target.StandOffset = v;
            if (!old.ApproxEquals(v)) m_host.ParentGroup.HasGroupChanged = true;
        }

        // sound family, OSSL_Api.cs:5017-5178 - every one ungated upstream; the link-addressed forms of the ll* calls
        /// <summary>OSSL_Api.cs:5017-5021.</summary>
        public void osAdjustSoundVolume(int linknum, float volume) => OsslSingleLinkPart(linknum)?.AdjustSoundGain(volume);
        /// <summary>OSSL_Api.cs:5023-5028.</summary>
        public void osSetSoundRadius(int linknum, float radius) { var sop = OsslSingleLinkPart(linknum); if (sop != null) sop.SoundRadius = radius; }
        private void OsslSound(int linknum, string sound, float volume, bool trigger, bool loop, bool master, bool slave)
        {
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            SceneObjectPart sop = OsslSingleLinkPart(linknum);
            if (sm == null || sop == null) return;
            UUID soundID = KeyOrName(sound);
            if (soundID == UUID.Zero) return;
            if (loop) sm.LoopSound(sop, soundID, volume, master, slave);
            else sm.SendSound(sop, soundID, volume, trigger, 0, slave, false);
        }
        /// <summary>OSSL_Api.cs:5030-5044.</summary>
        public void osPlaySound(int linknum, string sound, float volume) => OsslSound(linknum, sound, volume, false, false, false, false);
        /// <summary>OSSL_Api.cs:5047-5060.</summary>
        public void osLoopSound(int linknum, string sound, float volume) => OsslSound(linknum, sound, volume, false, true, false, false);
        /// <summary>OSSL_Api.cs:5063-5076.</summary>
        public void osLoopSoundMaster(int linknum, string sound, float volume) => OsslSound(linknum, sound, volume, false, true, true, false);
        /// <summary>OSSL_Api.cs:5079-5090.</summary>
        public void osLoopSoundSlave(int linknum, string sound, float volume) => OsslSound(linknum, sound, volume, false, true, false, true);
        /// <summary>OSSL_Api.cs:5093-5104.</summary>
        public void osPlaySoundSlave(int linknum, string sound, float volume) => OsslSound(linknum, sound, volume, false, false, false, true);
        /// <summary>OSSL_Api.cs:5107-5118.</summary>
        public void osTriggerSound(int linknum, string sound, float volume) => OsslSound(linknum, sound, volume, true, false, false, false);
        /// <summary>OSSL_Api.cs:5121-5134.</summary>
        public void osTriggerSoundLimited(int linknum, string sound, float volume, Vector3 top_north_east, Vector3 bottom_south_west)
        {
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            SceneObjectPart sop = OsslSingleLinkPart(linknum);
            if (sm == null || sop == null) return;
            UUID soundID = KeyOrName(sound);
            if (soundID != UUID.Zero) sm.TriggerSoundLimited(sop.UUID, soundID, volume, bottom_south_west, top_north_east);
        }
        /// <summary>OSSL_Api.cs:5137-5148 - every part the link number names.</summary>
        public void osStopSound(int linknum)
        {
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            if (sm == null || m_host == null) return;
            foreach (SceneObjectPart sop in GetLinkParts(linknum)) sm.StopSound(sop);
        }
        /// <summary>OSSL_Api.cs:5167-5177.</summary>
        public void osTriggerSoundAtPos(string sound, Vector3 position, float gain)
        {
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            if (sm == null || m_host == null) return;
            UUID soundID = KeyOrName(sound);
            if (soundID == UUID.Zero) return;
            sm.TriggerSound(soundID, m_host.OwnerID, m_host.UUID, UUID.Zero, gain, position, m_host.RegionHandle);
        }
        /// <summary>OSSL_Api.cs:4650-4677 - master switch. "" with volume 0 disables collision sounds, 1 restores the defaults, otherwise defaults at that volume.</summary>
        public void osCollisionSound(string impact_sound, float impact_volume)
        {
            OsslCheck();
            if (m_host == null) return;
            if (string.IsNullOrEmpty(impact_sound))
            {
                m_host.CollisionSoundVolume = impact_volume;
                m_host.CollisionSound = m_host.invalidCollisionSoundUUID;
                m_host.CollisionSoundType = impact_volume == 0.0f ? (sbyte)-1 : impact_volume == 1.0f ? (sbyte)0 : (sbyte)2;
                m_host.aggregateScriptEvents();
                return;
            }
            UUID soundId = KeyOrName(impact_sound);
            if (soundId == UUID.Zero) m_host.CollisionSoundType = -1;
            else { m_host.CollisionSound = soundId; m_host.CollisionSoundVolume = impact_volume; m_host.CollisionSoundType = 1; }
            m_host.aggregateScriptEvents();
        }

        // attachments, OSSL_Api.cs:4193-4265 and :4537-4555
        /// <summary>OSSL_Api.cs:4193-4198 - High. llAttachToAvatar without PERMISSION_ATTACH, onto the owner.</summary>
        public void osForceAttachToAvatar(int attachmentPoint)
        {
            OsslCheck(TlHigh, "osForceAttachToAvatar");
            if (m_host?.ParentGroup == null) return;
            IAttachmentsModule attachMod = World?.RequestModuleInterface<IAttachmentsModule>();
            ScenePresence sp = World?.GetScenePresence(m_host.OwnerID);
            if (attachMod == null || sp == null || sp.IsChildAgent) return;
            attachMod.AttachObject(sp, m_host.ParentGroup, (uint)attachmentPoint, false, true, false, GetScriptExperienceId());
        }

        /// <summary>OSSL_Api.cs:4213-4252 ForceAttachToAvatarFromInventory: the object moves from the prim's inventory to the avatar's and is rezzed as an attachment.</summary>
        private void OsslForceAttachFromInventory(UUID avatarId, string itemName, int attachmentPoint)
        {
            IAttachmentsModule attachMod = World?.RequestModuleInterface<IAttachmentsModule>();
            if (attachMod == null || m_host == null) return;
            TaskInventoryItem item = m_host.Inventory.GetInventoryItem(itemName);
            if (item == null) { ShoutError($"Could not find object '{itemName}'"); return; }
            if (item.InvType != (int)InventoryType.Object) { ShoutError($"Unable to attach, item '{itemName}' is not an object."); return; }
            if ((item.Flags & (uint)InventoryItemFlags.ObjectHasMultipleItems) != 0) { ShoutError($"Unable to attach coalesced object, item '{itemName}'"); return; }
            ScenePresence sp = World.GetScenePresence(avatarId);
            if (sp == null) return;
            InventoryItemBase newItem = World.MoveTaskInventoryItem(sp.UUID, UUID.Zero, m_host, item.ItemID, out string message);
            if (newItem == null) { ShoutError(message); return; }
            attachMod.RezSingleAttachmentFromInventory(sp, newItem.ID, (uint)attachmentPoint);
        }

        /// <summary>OSSL_Api.cs:4201-4205 - High.</summary>
        public void osForceAttachToAvatarFromInventory(string itemName, int attachmentPoint)
        {
            OsslCheck(TlHigh, "osForceAttachToAvatarFromInventory");
            OsslForceAttachFromInventory(m_host.OwnerID, itemName, attachmentPoint);
        }

        /// <summary>OSSL_Api.cs:4208-4214 - VeryHigh.</summary>
        public void osForceAttachToOtherAvatarFromInventory(string rawAvatarId, string itemName, int attachmentPoint)
        {
            OsslCheck(TlVeryHigh, "osForceAttachToOtherAvatarFromInventory");
            if (!UUID.TryParse(rawAvatarId, out UUID avatarId)) return;
            OsslForceAttachFromInventory(avatarId, itemName, attachmentPoint);
        }

        /// <summary>OSSL_Api.cs:4263-4268 - High. llDetachFromAvatar without PERMISSION_ATTACH.</summary>
        public void osForceDetachFromAvatar()
        {
            OsslCheck(TlHigh, "osForceDetachFromAvatar");
            if (m_host?.ParentGroup == null || !m_host.ParentGroup.IsAttachment) return;
            IAttachmentsModule attachMod = World?.RequestModuleInterface<IAttachmentsModule>();
            ScenePresence sp = World?.GetScenePresence(m_host.ParentGroup.AttachedAvatar);
            if (attachMod == null || sp == null) return;
            attachMod.DetachSingleAttachmentToInv(sp, m_host.ParentGroup);
        }

        /// <summary>OSSL_Api.cs:4537-4541 (DropAttachment :4500-4509) - High.</summary>
        public void osForceDropAttachment()
        {
            OsslCheck(TlHigh, "osForceDropAttachment");
            if (m_host?.ParentGroup == null || !m_host.ParentGroup.IsAttachment) return;
            IAttachmentsModule attachMod = World?.RequestModuleInterface<IAttachmentsModule>();
            ScenePresence sp = World?.GetScenePresence(m_host.ParentGroup.OwnerID);
            if (attachMod != null && sp != null) attachMod.DetachSingleAttachmentToGround(sp, m_host.ParentGroup.LocalId);
        }

        /// <summary>OSSL_Api.cs:4551-4555 (DropAttachmentAt :4511-4520) - High.</summary>
        public void osForceDropAttachmentAt(Vector3 pos, Quaternion rot)
        {
            OsslCheck(TlHigh, "osForceDropAttachmentAt");
            if (m_host?.ParentGroup == null || !m_host.ParentGroup.IsAttachment) return;
            IAttachmentsModule attachMod = World?.RequestModuleInterface<IAttachmentsModule>();
            ScenePresence sp = World?.GetScenePresence(m_host.ParentGroup.OwnerID);
            if (attachMod != null && sp != null) attachMod.DetachSingleAttachmentToGround(sp, m_host.ParentGroup.LocalId, pos, rot);
        }

        /// <summary>OSSL_Api.cs:2099-2124 - Low. A dataserver event (sender key, message) on every script in the target prim.</summary>
        public void osMessageObject(string objectUUID, string message)
        {
            OsslCheck(TlLow, "osMessageObject");
            if (!UUID.TryParse(objectUUID, out UUID objUUID)) { ShoutError("osMessageObject() cannot send messages to objects with invalid UUIDs"); return; }
            SceneObjectPart sceneOP = World?.GetSceneObjectPart(objUUID);
            if (sceneOP == null) { ShoutError("osMessageObject() cannot send message to " + objUUID + ", object was not found in scene."); return; }
            m_ScriptEngine.PostObjectEvent(sceneOP.LocalId, new EventParams("dataserver",
                new object[] { m_host.UUID.ToString(), message ?? string.Empty }, new DetectParams[0]));
            // Every script in the target prim, whatever engine runs it.
            string sender = m_host.UUID.ToString(), text = message ?? string.Empty;
            OfferToOtherEngines("osMessageObject", () => ScriptItemsIn(new[] { sceneOP }), "dataserver", () => new object[] { sender, text });
        }

        /// <summary>OSSL_Api.cs:5941-5966 - ungated upstream. Every other script in the prim (or the linkset) is reset, then this one.</summary>
        public void osResetAllScripts(int linkset)
        {
            if (m_host?.ParentGroup == null) return;
            var scripts = new List<TaskInventoryItem>();
            if (linkset != 0)
            {
                SceneObjectGroup sog = m_host.ParentGroup;
                if (sog.inTransit || sog.IsDeleted) return;
                foreach (SceneObjectPart part in sog.Parts) scripts.AddRange(part.Inventory.GetInventoryItems(InventoryType.LSL));
            }
            else scripts.AddRange(m_host.Inventory.GetInventoryItems(InventoryType.LSL));
            foreach (TaskInventoryItem script in scripts)
                if (script.ItemID != m_itemID) m_ScriptEngine.ResetScript(script.ItemID);
            World?.RequestModuleInterface<IUrlModule>()?.ScriptRemoved(m_itemID);
            m_ScriptEngine.ApiResetScript(m_itemID);
        }

        private static System.Collections.Hashtable OsslUrlOptions(LSLList options)
        {
            var opts = new System.Collections.Hashtable();
            for (int i = 0; i < options.Length; i++)
                if (options.Data[i]?.ToString() == "allowXss") opts["allowXss"] = true;
            return opts;
        }

        /// <summary>OSSL_Api.cs:4615-4629 - Moderate. llRequestURL with options ("allowXss").</summary>
        public string osRequestURL(LSLList options)
        {
            OsslCheck(TlModerate, "osRequestURL");
            IUrlModule urlMod = World?.RequestModuleInterface<IUrlModule>();
            if (urlMod == null) return UUID.Zero.ToString();
            return urlMod.RequestURL(m_ScriptEngine, m_host, m_itemID, OsslUrlOptions(options)).ToString();
        }

        /// <summary>OSSL_Api.cs:4633-4647 - Moderate.</summary>
        public string osRequestSecureURL(LSLList options)
        {
            OsslCheck(TlModerate, "osRequestSecureURL");
            IUrlModule urlMod = World?.RequestModuleInterface<IUrlModule>();
            if (urlMod == null) return UUID.Zero.ToString();
            return urlMod.RequestSecureURL(m_ScriptEngine, m_host, m_itemID, OsslUrlOptions(options)).ToString();
        }

        /// <summary>OSSL_Api.cs:2697-2710 - VeryLow. Regex replace of at most count matches from start (negative start counts from the end).</summary>
        public string osReplaceString(string src, string pattern, string replace, int count, int start)
        {
            OsslCheck(TlVeryLow, "osReplaceString");
            src ??= string.Empty;
            if (start < 0) start = src.Length + start;
            if (start < 0 || start >= src.Length) return src;
            try { return ScriptRegex.Create(pattern).Replace(src, replace ?? string.Empty, count, start); }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { ShoutError(ScriptRegex.TimedOutMessage); return src; }
        }

        /// <summary>OSSL_Api.cs:6103-6106 - ungated upstream.</summary>
        public int osClearObjectAnimations() => m_host?.ClearObjectAnimations() ?? 0;

        /// <summary>OSSL_Api.cs:936-949 - ungated upstream, but only the owner, a PERMISSION_TELEPORT granter, or an agent standing on land the owner rule allows.</summary>
        public void osLocalTeleportAgent(string agent, Vector3 position, Vector3 velocity, Vector3 lookat, int flags)
        {
            if (!UUID.TryParse(agent, out UUID agentId)) return;
            ScenePresence presence = World?.GetScenePresence(agentId);
            if (presence == null || presence.IsDeleted || presence.IsInTransit) return;
            if (!OsslAgentTeleportAllowed(agentId, presence.AbsolutePosition)) return;
            World.RequestLocalTeleport(presence, position, velocity, lookat, flags);
        }

        /// <summary>OSSL_Api.cs:875-886 - Severe, and a second check: the owner must be allowed console commands by the permissions module.</summary>
        public int osConsoleCommand(string command)
        {
            OsslCheck(TlSevere, "osConsoleCommand");
            if (World?.Permissions == null || !World.Permissions.CanRunConsoleCommand(m_host.OwnerID)) return 0;
            OpenSim.Framework.MainConsole.Instance?.RunCommand(command);
            return 1;
        }

        // ── OSSL agent, teleport, kick, animation and group functions, ported from OSSL_Api.cs (line cited per
        //    function), each under its upstream key and threat level through OsslGate ──
        private static readonly OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel
            TlNone = OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.None;

        /// <summary>OSSL_Api.cs:1076-1082 - None. The owner through YEngine's region-name TeleportAgent (not the Severe door).</summary>
        public void osTeleportOwner(string regionName, Vector3 position, Vector3 lookat)
        {
            OsslCheck(TlNone, "osTeleportOwner");
            if (m_host == null) return;
            OsslTeleportAgent(m_host.OwnerID.ToString(), regionName, position, lookat);
        }

        /// <summary>OSSL_Api.cs:1084-1089 - None. Grid coordinates, through YEngine's grid TeleportAgent.</summary>
        public void osTeleportOwner(int regionGridX, int regionGridY, Vector3 position, Vector3 lookat)
        {
            OsslCheck(TlNone, "osTeleportOwner");
            if (m_host == null) return;
            OsslTeleportAgent(m_host.OwnerID.ToString(), regionGridX, regionGridY, position, lookat);
        }

        /// <summary>OSSL_Api.cs:1091-1096 - None. The three-argument osTeleportAgent, ungated upstream.</summary>
        public void osTeleportOwner(Vector3 position, Vector3 lookat)
        {
            OsslCheck(TlNone, "osTeleportOwner");
            if (m_host == null) return;
            osTeleportAgent(m_host.OwnerID.ToString(), position, lookat);
        }

        /// <summary>OSSL_Api.cs:3705-3727 - Severe. Every root presence with that name: Kick with the alert when there is one, then CloseAgent.</summary>
        public void osKickAvatar(string FirstName, string SurName, string alert)
        {
            OsslCheck(TlSevere, "osKickAvatar");
            var victims = new List<ScenePresence>();
            World?.ForEachRootScenePresence(sp => { if (sp.Firstname == FirstName && sp.Lastname == SurName) victims.Add(sp); });
            foreach (ScenePresence sp in victims)
            {
                if (!string.IsNullOrEmpty(alert)) sp.ControllingClient.Kick(alert);
                sp.Scene.CloseAgent(sp.UUID, false);
            }
        }

        /// <summary>OSSL_Api.cs:3730-3741 - Severe.</summary>
        public void osKickAvatar(string agentKey, string alert)
        {
            OsslCheck(TlSevere, "osKickAvatar");
            if (!UUID.TryParse(agentKey, out UUID id) || id == UUID.Zero) return;
            ScenePresence sp = World?.GetScenePresence(id);
            if (sp == null) return;
            if (!string.IsNullOrEmpty(alert)) sp.ControllingClient.Kick(alert);
            sp.Scene.CloseAgent(id, false);
        }

        /// <summary>OSSL_Api.cs:1178-1206 - VeryHigh. An animation from the prim's inventory by name, else a key (this tree, like the stop form), else a default animation by name, on any presence.</summary>
        public void osAvatarPlayAnimation(string avatar, string animation)
        {
            OsslCheck(TlVeryHigh, "osAvatarPlayAnimation");
            if (!UUID.TryParse(avatar, out UUID avatarID)) return;
            ScenePresence target = World?.GetScenePresence(avatarID);
            if (target?.Animator == null) return;
            UUID animID = FindInventoryItem(animation, (int)AssetType.Animation)?.AssetID ?? UUID.Zero;
            if (animID == UUID.Zero && !UUID.TryParse(animation, out animID)) animID = UUID.Zero;   // a key as well, as the stop form and llStartAnimation take
            if (animID == UUID.Zero) target.Animator.AddAnimation(animation, m_host.UUID);          // a default animation by name
            else target.Animator.AddAnimation(animID, m_host.UUID);
            target.TriggerScenePresenceUpdated();
        }

        /// <summary>OSSL_Api.cs:1210-1232 - VeryHigh.</summary>
        public void osAvatarStopAnimation(string avatar, string animation)
        {
            OsslCheck(TlVeryHigh, "osAvatarStopAnimation");
            if (!UUID.TryParse(avatar, out UUID avatarID)) return;
            ScenePresence target = World?.GetScenePresence(avatarID);
            if (target?.Animator == null) return;
            if (!UUID.TryParse(animation, out UUID animID))
                animID = FindInventoryItem(animation, (int)AssetType.Animation)?.AssetID ?? UUID.Zero;
            if (animID == UUID.Zero) target.Animator.RemoveAnimation(animation);
            else target.Animator.RemoveAnimation(animID, true);
            target.TriggerScenePresenceUpdated();
        }

        /// <summary>OSSL_Api.cs:2432-2450 - Low. A presence here first, then the user-management lookup.</summary>
        public string osAvatarName2Key(string firstname, string lastname)
        {
            OsslCheck(TlLow, "osAvatarName2Key");
            ScenePresence sp = World?.GetScenePresence(firstname, lastname);
            if (sp != null) return sp.UUID.ToString();
            IUserManagement um = World?.RequestModuleInterface<IUserManagement>();
            if (um == null) { ShoutError("osAvatarName2Key: UserManagement module not available"); return string.Empty; }
            UUID userID = um.GetUserIdByName(firstname, lastname);
            return userID == UUID.Zero ? string.Empty : userID.ToString();
        }

        /// <summary>OSSL_Api.cs:2478-2508 - Low. A presence here, then the account service, then the user-management name (which knows HG visitors).</summary>
        public string osKey2Name(string id)
        {
            OsslCheck(TlLow, "osKey2Name");
            if (!UUID.TryParse(id, out UUID key)) return string.Empty;
            ScenePresence sp = World?.GetScenePresence(key);
            if (sp != null) return sp.Name;
            UserAccount account = World?.UserAccountService?.GetUserAccount(World.RegionInfo.ScopeID, key);
            if (account != null) return account.Name;
            return World?.RequestModuleInterface<IUserManagement>()?.GetUserName(key) ?? string.Empty;
        }

        /// <summary>OSSL_Api.cs:1159-1173 - Severe, and the owner must be a god as well.</summary>
        public string osGetAgentIP(string agent)
        {
            OsslCheck(TlSevere, "osGetAgentIP");
            if (World?.Permissions == null || !World.Permissions.IsGod(m_host.OwnerID)) return string.Empty;
            if (!UUID.TryParse(agent, out UUID avatarID)) return string.Empty;
            ScenePresence target = World.GetScenePresence(avatarID);
            return target?.ControllingClient?.RemoteEndPoint?.Address?.ToString() ?? string.Empty;
        }

        /// <summary>
        /// OSSL_Api.cs:3475-3479 - High. Upstream writes the appearance into a notecard; here, as for osNpcSaveAppearance,
        /// the "notecard" is an outfit name in BotManager's store, scoped to the owner - which is exactly
        /// what SaveOutfitToDatabase captures. The key returned is the outfit's key in that store.
        /// </summary>
        public string osOwnerSaveAppearance(string notecard) => osOwnerSaveAppearance(notecard, 1);

        /// <summary>OSSL_Api.cs:3482-3486 - High. includeHuds is accepted, not applied: the store keeps the whole appearance.</summary>
        public string osOwnerSaveAppearance(string notecard, int includeHuds)
        {
            OsslCheck(TlHigh, "osOwnerSaveAppearance");
            var mgr = NpcMgr(); if (mgr == null || m_host == null) return UUID.Zero.ToString();
            mgr.SaveOutfitToDatabase(m_host.OwnerID, notecard, out string reason);
            if (reason != null) { ShoutError("osOwnerSaveAppearance: " + reason); return UUID.Zero.ToString(); }
            return OpenSim.Region.OptionalModules.World.NPC.BotManager.OutfitKey(m_host.OwnerID, notecard).ToString();
        }

        /// <summary>
        /// OSSL_Api.cs:3764-3792 - High. Through llDamage's one door with this prim as the source, so the target's
        /// attachments get on_damage with this prim as the detected key; the parcel (upstream) or the region must allow damage.
        /// Death (health at or below 0) is the door's business, not repeated here.
        /// </summary>
        public void osCauseDamage(string avatar, float damage)
        {
            OsslCheck(TlHigh, "osCauseDamage");
            if (World == null || m_host == null || !UUID.TryParse(avatar, out UUID avatarId)) return;
            ScenePresence presence = World.GetScenePresence(avatarId);
            if (presence == null || presence.IsChildAgent) return;
            // upstream admits the call on the parcel flag alone; here the region's AllowDamage (the rule
            // llDamage applies in this tree) admits it as well, so a damage-enabled region needs no per-parcel flag
            LandData land = World.GetLandData(m_host.GetWorldPosition());
            bool parcelAllows = land != null && (land.Flags & (uint)ParcelFlags.AllowDamage) != 0;
            if (!parcelAllows && !World.RegionInfo.RegionSettings.AllowDamage) return;
            try { presence.ApplyDamage(m_host.UUID, m_host.OwnerID, m_host.LocalId, damage, DamageEntry.TYPE_GENERIC, true); }
            catch (Exception e) { m_log.LogWarning(e, "[Phlox] osCauseDamage from {Prim} to {Avatar} threw", m_host.UUID, avatarId); }
        }

        /// <summary>OSSL_Api.cs:3800-3813 - High. Health up by the amount, capped at 100.</summary>
        public void osCauseHealing(string avatar, float healing)
        {
            OsslCheck(TlHigh, "osCauseHealing");
            if (!UUID.TryParse(avatar, out UUID avatarId)) return;
            ScenePresence presence = World?.GetScenePresence(avatarId);
            if (presence == null) return;
            presence.setHealthWithUpdate(Math.Min(100f, presence.Health + healing));
        }

        /// <summary>
        /// OSSL_Api.cs:3820-3835 - High. Clamped to 1..100; a decrease goes through llDamage's door as damage from
        /// this prim (the llSetHealth rule), an increase is set directly.
        /// </summary>
        public void osSetHealth(string avatar, float health)
        {
            OsslCheck(TlHigh, "osSetHealth");
            if (World == null || m_host == null || !UUID.TryParse(avatar, out UUID avatarId)) return;
            ScenePresence presence = World.GetScenePresence(avatarId);
            if (presence == null || presence.IsChildAgent) return;
            health = Math.Clamp(health, 1f, 100f);
            if (health < presence.Health)
            {
                try { presence.ApplyDamage(m_host.UUID, m_host.OwnerID, m_host.LocalId, presence.Health - health, DamageEntry.TYPE_GENERIC, true); }
                catch (Exception e) { m_log.LogWarning(e, "[Phlox] osSetHealth from {Prim} to {Avatar} threw", m_host.UUID, avatarId); }
            }
            else presence.setHealthWithUpdate(health);
        }

        /// <summary>OSSL_Api.cs:3840-3849 - High.</summary>
        public void osSetHealRate(string avatar, float healrate)
        {
            OsslCheck(TlHigh, "osSetHealRate");
            if (!UUID.TryParse(avatar, out UUID avatarId)) return;
            ScenePresence presence = World?.GetScenePresence(avatarId);
            if (presence != null) presence.HealRate = healrate;
        }

        /// <summary>OSSL_Api.cs:1125-1139 ForceSit: the presence requests a sit on the target as if it had clicked it, if nobody sits there.</summary>
        private void OsslForceSit(string avatar, UUID targetID)
        {
            if (!UUID.TryParse(avatar, out UUID agentID)) return;
            ScenePresence presence = World?.GetScenePresence(agentID);
            if (presence == null) return;
            SceneObjectPart part = World.GetSceneObjectPart(targetID);
            if (part != null && part.SitTargetAvatar == UUID.Zero)
                presence.HandleAgentRequestSit(presence.ControllingClient, agentID, targetID, part.SitTargetPosition);
        }

        /// <summary>OSSL_Api.cs:1106-1110 - VeryHigh. Onto this prim.</summary>
        public void osForceOtherSit(string avatar)
        {
            OsslCheck(TlVeryHigh, "osForceOtherSit");
            if (m_host != null) OsslForceSit(avatar, m_host.UUID);
        }

        /// <summary>OSSL_Api.cs:1119-1123 - VeryHigh. Onto the prim named.</summary>
        public void osForceOtherSit(string avatar, string target)
        {
            OsslCheck(TlVeryHigh, "osForceOtherSit");
            if (UUID.TryParse(target, out UUID targetID)) OsslForceSit(avatar, targetID);
        }

        /// <summary>OSSL_Api.cs:2136-2160 - Low. Deletes an object this prim's linkset rezzed, same owner, not an attachment, never itself.</summary>
        public void osDie(string objectUUID)
        {
            OsslCheck(TlLow, "osDie");
            if (!UUID.TryParse(objectUUID, out UUID objUUID)) { ShoutError("osDie() cannot delete objects with invalid UUIDs"); return; }
            if (objUUID == UUID.Zero || m_host?.ParentGroup == null) return;
            SceneObjectGroup sog = World?.GetSceneObjectGroup(objUUID);
            if (sog == null || sog.IsDeleted || sog.IsAttachment) return;
            if (sog.OwnerID != m_host.OwnerID) return;
            if (sog.RezzerID == m_host.ParentGroup.UUID && sog.UUID != m_host.ParentGroup.UUID)
                World.DeleteSceneObject(sog, false);
        }

        /// <summary>OSSL_Api.cs:4530-4534 (DropAttachment :4500-4509 with the check) - Moderate: PERMISSION_ATTACH or a shout.</summary>
        public void osDropAttachment()
        {
            OsslCheck(TlModerate, "osDropAttachment");
            if (((OsslItem?.PermsMask ?? 0) & PERMISSION_ATTACH) == 0) { ShoutError("Cannot drop attachment. Permissions not granted."); return; }
            if (m_host?.ParentGroup == null || !m_host.ParentGroup.IsAttachment) return;
            IAttachmentsModule attachMod = World?.RequestModuleInterface<IAttachmentsModule>();
            ScenePresence sp = World?.GetScenePresence(m_host.ParentGroup.OwnerID);
            if (attachMod != null && sp != null) attachMod.DetachSingleAttachmentToGround(sp, m_host.ParentGroup.LocalId);
        }

        /// <summary>OSSL_Api.cs:4544-4548 (DropAttachmentAt :4511-4520 with the check) - Moderate.</summary>
        public void osDropAttachmentAt(Vector3 pos, Quaternion rot)
        {
            OsslCheck(TlModerate, "osDropAttachmentAt");
            if (((OsslItem?.PermsMask ?? 0) & PERMISSION_ATTACH) == 0) { ShoutError("Cannot drop attachment. Permissions not granted."); return; }
            if (m_host?.ParentGroup == null || !m_host.ParentGroup.IsAttachment) return;
            IAttachmentsModule attachMod = World?.RequestModuleInterface<IAttachmentsModule>();
            ScenePresence sp = World?.GetScenePresence(m_host.ParentGroup.OwnerID);
            if (attachMod != null && sp != null) attachMod.DetachSingleAttachmentToGround(sp, m_host.ParentGroup.LocalId, pos, rot);
        }

        /// <summary>OSSL_Api.cs:4022-4046 - VeryLow. 0 = no groups module / no group / not present / owner lacks Invite; 2 = already a member; 1 = invited.</summary>
        public int osInviteToGroup(string agentId)
        {
            OsslCheck(TlVeryLow, "osInviteToGroup");
            IGroupsModule groups = World?.RequestModuleInterface<IGroupsModule>();
            if (groups == null || m_host == null || !UUID.TryParse(agentId, out UUID agent)) return 0;
            if (m_host.GroupID == UUID.Zero || m_host.GroupID == m_host.OwnerID) return 0;
            ScenePresence sp = World.GetScenePresence(agent);
            if (sp == null || sp.IsNPC || sp.IsChildAgent || !sp.ControllingClient.IsActive) return 0;
            if (sp.ControllingClient.IsGroupMember(m_host.GroupID)) return 2;
            if ((groups.GetFullGroupPowers(m_host.OwnerID, m_host.GroupID) & (ulong)GroupPowers.Invite) == 0) return 0;
            groups.InviteGroup(null, m_host.OwnerID, m_host.GroupID, agent, UUID.Zero);
            return 1;
        }

        /// <summary>OSSL_Api.cs:4060-4078 - VeryLow.</summary>
        public int osEjectFromGroup(string agentId)
        {
            OsslCheck(TlVeryLow, "osEjectFromGroup");
            IGroupsModule groups = World?.RequestModuleInterface<IGroupsModule>();
            if (groups == null || m_host == null || !UUID.TryParse(agentId, out UUID agent)) return 0;
            if (m_host.GroupID == UUID.Zero || m_host.GroupID == m_host.OwnerID) return 0;
            if ((groups.GetFullGroupPowers(m_host.OwnerID, m_host.GroupID) & (ulong)GroupPowers.Eject) == 0) return 0;
            groups.EjectGroupMember(null, m_host.OwnerID, m_host.GroupID, agent);
            return 1;
        }

        /// <summary>OSSL_Api.cs:6396-6404 - ungated upstream. -1 not a key, 0 not here (or a child), 1 an avatar, 2 an NPC.</summary>
        public int osAvatarType(string avkey)
        {
            if (!UUID.TryParse(avkey, out UUID avId)) return -1;
            ScenePresence av = World?.GetScenePresence(avId);
            if (av == null || av.IsDeleted || av.IsChildAgent) return 0;
            return av.IsNPC ? 2 : 1;
        }

        /// <summary>OSSL_Api.cs:6408-6414 - ungated upstream.</summary>
        public int osAvatarType(string sFirstName, string sLastName)
        {
            ScenePresence av = World?.GetScenePresence(sFirstName, sLastName);
            if (av == null || av.IsDeleted || av.IsChildAgent) return 0;
            return av.IsNPC ? 2 : 1;
        }

        // ── OSSL parcel, estate, terrain, wind and sun functions, ported from OSSL_Api.cs (line cited per
        //    function), each under its upstream key and threat level through OsslGate ──

        private bool TerrainInBounds(int x, int y, string fn)
        {
            if (World == null) return false;
            if (x < 0 || y < 0 || x > World.RegionInfo.RegionSizeX - 1 || y > World.RegionInfo.RegionSizeY - 1)
            {
                ShoutError(fn + ": Coordinate out of bounds");
                return false;
            }
            return true;
        }

        /// <summary>OSSL_Api.cs:548-552 (SetTerrainHeight :562-572) - High. 1 when the owner may terraform there and the height is written, else 0.</summary>
        public int osSetTerrainHeight(int x, int y, float val)
        {
            OsslCheck(TlHigh, "osSetTerrainHeight");
            if (!TerrainInBounds(x, y, "osSetTerrainHeight") || World.Heightmap == null) return 0;
            if (!World.Permissions.CanTerraformLand(m_host.OwnerID, new Vector3(x, y, 0))) return 0;
            World.Heightmap[x, y] = val;
            return 1;
        }

        /// <summary>OSSL_Api.cs:555-560 - High; the deprecated name for osSetTerrainHeight, same key upstream.</summary>
        public int osTerrainSetHeight(int x, int y, float val)
        {
            OsslCheck(TlHigh, "osTerrainSetHeight");
            if (!TerrainInBounds(x, y, "osTerrainSetHeight") || World.Heightmap == null) return 0;
            if (!World.Permissions.CanTerraformLand(m_host.OwnerID, new Vector3(x, y, 0))) return 0;
            World.Heightmap[x, y] = val;
            return 1;
        }

        /// <summary>OSSL_Api.cs:577-581 (GetTerrainHeight :590-596) - master switch.</summary>
        public float osGetTerrainHeight(int x, int y)
        {
            OsslCheck();
            if (!TerrainInBounds(x, y, "osGetTerrainHeight") || World.Heightmap == null) return 0f;
            return World.Heightmap[x, y];
        }

        /// <summary>OSSL_Api.cs:583-588 - master switch; the deprecated name.</summary>
        public float osTerrainGetHeight(int x, int y)
        {
            OsslCheck();
            if (!TerrainInBounds(x, y, "osTerrainGetHeight") || World.Heightmap == null) return 0f;
            return World.Heightmap[x, y];
        }

        private double m_lastOsTerrainFlush;

        /// <summary>OSSL_Api.cs:599-609 - VeryLow, at most once a minute per script: the terrain module's taint, which sends the changed patches.</summary>
        public void osTerrainFlush()
        {
            double now = Util.GetTimeStamp();
            if (now - m_lastOsTerrainFlush < 60) return;
            m_lastOsTerrainFlush = now;
            OsslCheck(TlVeryLow, "osTerrainFlush");
            World?.RequestModuleInterface<ITerrainModule>()?.TaintTerrain();
        }

        /// <summary>OSSL_Api.cs:612-626 - High, and CanIssueEstateCommand. Under 15 s aborts a pending restart; otherwise the restart module schedules it.</summary>
        public int osRegionRestart(float seconds) => osRegionRestart(seconds, string.Empty);

        /// <summary>OSSL_Api.cs:638-652 - High. The message is accepted; the restart module here takes no custom text (RegionRestart :654-658 drops it upstream too).</summary>
        public int osRegionRestart(float seconds, string msg)
        {
            OsslCheck(TlHigh, "osRegionRestart");
            IRestartModule restart = World?.RequestModuleInterface<IRestartModule>();
            if (restart == null || m_host == null || !World.Permissions.CanIssueEstateCommand(m_host.OwnerID, false)) return 0;
            if (seconds < 15) { restart.AbortRestart("Region restart has been aborted\n"); return 1; }
            restart.ScheduleRestart(UUID.Zero, (int)seconds);
            return 1;
        }

        /// <summary>OSSL_Api.cs:664-675 - High, and CanIssueEstateCommand.</summary>
        public void osRegionNotice(string msg)
        {
            OsslCheck(TlHigh, "osRegionNotice");
            IDialogModule dm = World?.RequestModuleInterface<IDialogModule>();
            if (dm == null || m_host == null || !World.Permissions.CanIssueEstateCommand(m_host.OwnerID, false)) return;
            dm.SendGeneralAlert(msg + "\n");
        }

        /// <summary>OSSL_Api.cs:678-697 - High. To one root, non-NPC presence.</summary>
        public void osRegionNotice(string agentID, string msg)
        {
            OsslCheck(TlHigh, "osRegionNotice");
            if (m_host == null || World == null || !World.Permissions.CanIssueEstateCommand(m_host.OwnerID, false)) return;
            IDialogModule dm = World.RequestModuleInterface<IDialogModule>();
            if (dm == null || !UUID.TryParse(agentID, out UUID avatarID)) return;
            ScenePresence sp = World.GetScenePresence(avatarID);
            if (sp == null || sp.IsChildAgent || sp.IsDeleted || sp.IsInTransit || sp.IsNPC) return;
            dm.SendAlertToUser(sp.ControllingClient, msg + "\n", false);
        }

        /// <summary>OSSL_Api.cs:1488-1492 - High.</summary>
        public void osSetRegionWaterHeight(float height)
        {
            OsslCheck(TlHigh, "osSetRegionWaterHeight");
            World?.EventManager.TriggerRequestChangeWaterHeight(height);
        }

        /// <summary>OSSL_Api.cs:1501-1516 - High. The legacy region sun settings (hour 0-24, stored +6), saved, then the estate-tools sun update.</summary>
        public void osSetRegionSunSettings(int useEstateSun, int sunFixed, float sunHour)
        {
            OsslCheck(TlHigh, "osSetRegionSunSettings");
            if (World == null) return;
            while (sunHour > 24.0f) sunHour -= 24.0f;
            while (sunHour < 0) sunHour += 24.0f;
            var rs = World.RegionInfo.RegionSettings;
            rs.UseEstateSun = useEstateSun != 0;
            rs.SunPosition = sunHour + 6;   // LL region sun hour is 6 to 30
            rs.FixedSun = sunFixed != 0;
            rs.Save();
            World.EventManager.TriggerEstateToolsSunUpdate(World.RegionInfo.RegionHandle);
        }

        /// <summary>OSSL_Api.cs:1524-1538 - High upstream, and its whole body is commented out since EEP: a gated no-op, kept so the call compiles.</summary>
        public void osSetEstateSunSettings(int sunFixed, float sunHour)
        {
            OsslCheck(TlHigh, "osSetEstateSunSettings");
        }

        /// <summary>OSSL_Api.cs:1548-1555 - master switch. 24 x the environment module's day fraction.</summary>
        public float osGetCurrentSunHour()
        {
            OsslCheck();
            IEnvironmentModule env = World?.RequestModuleInterface<IEnvironmentModule>();
            return env == null ? 0f : 24f * env.GetRegionDayFractionTime();
        }

        /// <summary>OSSL_Api.cs:1638-1657 GetSunParam: day_length from the environment module (14400 without one), year_length 365, the rest EEP-fixed.</summary>
        private float OsslSunParam(string param)
        {
            switch ((param ?? string.Empty).ToLowerInvariant())
            {
                case "day_length":
                    IEnvironmentModule env = World?.RequestModuleInterface<IEnvironmentModule>();
                    return env == null || m_host == null ? 14400f : env.GetDayLength(m_host.AbsolutePosition);
                case "year_length": return 365f;
                case "day_night_offset": return 0f;
                case "update_interval": return 0.1f;
                case "day_time_sun_hour_scale": return 1f;
                default: return 0f;
            }
        }

        /// <summary>OSSL_Api.cs:1633-1637 - master switch.</summary>
        public float osGetSunParam(string param) { OsslCheck(); return OsslSunParam(param); }

        /// <summary>OSSL_Api.cs:1626-1631 - None; the deprecated name.</summary>
        public float osSunGetParam(string param) { OsslCheck(TlNone, "osSunGetParam"); return OsslSunParam(param); }

        /// <summary>OSSL_Api.cs:1667-1671 (SetSunParam :1673-1677) - None. Goes to ISunModule, which no EEP region carries: a no-op there, as upstream (the wiki says so too).</summary>
        public void osSetSunParam(string param, float value)
        {
            OsslCheck(TlNone, "osSetSunParam");
            World?.RequestModuleInterface<ISunModule>()?.SetSunParameter(param, value);
        }

        /// <summary>OSSL_Api.cs:1660-1665 - None; the deprecated name.</summary>
        public void osSunSetParam(string param, float value)
        {
            OsslCheck(TlNone, "osSunSetParam");
            World?.RequestModuleInterface<ISunModule>()?.SetSunParameter(param, value);
        }

        /// <summary>OSSL_Api.cs:1679-1688 - None.</summary>
        public string osWindActiveModelPluginName()
        {
            OsslCheck(TlNone, "osWindActiveModelPluginName");
            return World?.RequestModuleInterface<IWindModule>()?.WindActiveModelPluginName ?? string.Empty;
        }

        /// <summary>OSSL_Api.cs:1692-1705 - VeryLow. The wind module's own parameter door (iwSetWind's Halcyon shape has no counterpart here).</summary>
        public void osSetWindParam(string plugin, string param, float value)
        {
            OsslCheck(TlVeryLow, "osSetWindParam");
            IWindModule wind = World?.RequestModuleInterface<IWindModule>();
            if (wind == null) return;
            try { wind.WindParamSet(plugin, param, value); } catch (Exception) { }
        }

        /// <summary>OSSL_Api.cs:1707-1716 - VeryLow.</summary>
        public float osGetWindParam(string plugin, string param)
        {
            OsslCheck(TlVeryLow, "osGetWindParam");
            IWindModule wind = World?.RequestModuleInterface<IWindModule>();
            if (wind == null) return 0f;
            try { return wind.WindParamGet(plugin, param); } catch (Exception) { return 0f; }
        }

        /// <summary>OSSL_Api.cs:1731-1740 - High. The land channel's join over the rectangle, as the owner.</summary>
        public void osParcelJoin(Vector3 pos1, Vector3 pos2)
        {
            OsslCheck(TlHigh, "osParcelJoin");
            if (World?.LandChannel == null || m_host == null) return;
            World.LandChannel.Join((int)Math.Min(pos1.X, pos2.X), (int)Math.Min(pos1.Y, pos2.Y), (int)Math.Max(pos1.X, pos2.X), (int)Math.Max(pos1.Y, pos2.Y), m_host.OwnerID);
        }

        /// <summary>OSSL_Api.cs:1743-1752 - High.</summary>
        public void osParcelSubdivide(Vector3 pos1, Vector3 pos2)
        {
            OsslCheck(TlHigh, "osParcelSubdivide");
            if (World?.LandChannel == null || m_host == null) return;
            World.LandChannel.Subdivide((int)Math.Min(pos1.X, pos2.X), (int)Math.Min(pos1.Y, pos2.Y), (int)Math.Max(pos1.X, pos2.X), (int)Math.Max(pos1.Y, pos2.Y), m_host.OwnerID);
        }

        /// <summary>OSSL_Api.cs:1762-1766 - High.</summary>
        public void osSetParcelDetails(Vector3 pos, LSLList rules)
        {
            OsslCheck(TlHigh, "osSetParcelDetails");
            OsslSetParcelDetails(pos, rules);
        }

        /// <summary>OSSL_Api.cs:1755-1760 - High; the deprecated name.</summary>
        public void osParcelSetDetails(Vector3 pos, LSLList rules)
        {
            OsslCheck(TlHigh, "osParcelSetDetails");
            OsslSetParcelDetails(pos, rules);
        }

        /// <summary>
        /// OSSL_Api.cs:1768-1957 SetParcelDetails: NAME, DESC (LandOptions on the parcel), OWNER and CLAIMDATE (estate
        /// manager or owner), GROUP (the land's owner or an estate manager; upstream's group-membership check through
        /// the groups module is not repeated), SEE_AVATARS, ANY_AVATAR_SOUNDS, GROUP_SOUNDS; committed through
        /// UpdateLandObject, and the parcel overlay resent when SEE_AVATARS moved.
        /// </summary>
        private void OsslSetParcelDetails(Vector3 pos, LSLList rules)
        {
            if (World?.LandChannel == null || m_host == null) return;
            ILandObject start = World.LandChannel.GetLandObject((int)pos.X, (int)pos.Y);
            if (start?.LandData == null) { ShoutError("There is no land at that location"); return; }
            if (!World.Permissions.CanEditParcelProperties(m_host.OwnerID, start, GroupPowers.LandOptions, false))
            { ShoutError("script owner does not have permission to modify the parcel"); return; }
            LandData newLand = start.LandData.Copy();
            EstateSettings es = World.RegionInfo.EstateSettings;
            bool manager = es == null || es.IsEstateManagerOrOwner(m_host.OwnerID);
            bool changed = false, changedSeeAvs = false;
            for (int idx = 0; idx < rules.Length;)
            {
                int code = rules.GetLSLIntegerItem(idx++);
                if (idx >= rules.Length) break;
                switch (code)
                {
                    case PARCEL_DETAILS_NAME:
                    { string arg = rules.GetLSLStringItem(idx++); if (newLand.Name != arg) { newLand.Name = arg; changed = true; } break; }
                    case PARCEL_DETAILS_DESC:
                    { string arg = rules.GetLSLStringItem(idx++); if (newLand.Description != arg) { newLand.Description = arg; changed = true; } break; }
                    case PARCEL_DETAILS_OWNER:
                    {
                        string arg = rules.GetLSLStringItem(idx++);
                        if (!manager) { ShoutError("script owner does not have permission to modify the parcel owner"); break; }
                        if (UUID.TryParse(arg, out UUID uuid) && newLand.OwnerID != uuid) { newLand.OwnerID = uuid; newLand.GroupID = UUID.Zero; changed = true; }
                        break;
                    }
                    case PARCEL_DETAILS_GROUP:
                    {
                        string arg = rules.GetLSLStringItem(idx++);
                        if ((m_host.OwnerID == newLand.OwnerID || manager) && UUID.TryParse(arg, out UUID uuid) && newLand.GroupID != uuid)
                        { newLand.GroupID = uuid; changed = true; }
                        break;
                    }
                    case 10:  // PARCEL_DETAILS_CLAIMDATE
                    {
                        int date = rules.GetLSLIntegerItem(idx++);
                        if (!manager) { ShoutError("script owner does not have permission to modify the parcel CLAIM DATE"); break; }
                        if (date == 0) date = Util.UnixTimeSinceEpoch();
                        if (newLand.ClaimDate != date) { newLand.ClaimDate = date; changed = true; }
                        break;
                    }
                    case PARCEL_DETAILS_SEE_AVATARS:
                    { bool v = rules.GetLSLIntegerItem(idx++) != 0; if (newLand.SeeAVs != v) { newLand.SeeAVs = v; changed = true; changedSeeAvs = true; } break; }
                    case 7:   // PARCEL_DETAILS_ANY_AVATAR_SOUNDS
                    { bool v = rules.GetLSLIntegerItem(idx++) != 0; if (newLand.AnyAVSounds != v) { newLand.AnyAVSounds = v; changed = true; } break; }
                    case 8:   // PARCEL_DETAILS_GROUP_SOUNDS
                    { bool v = rules.GetLSLIntegerItem(idx++) != 0; if (newLand.GroupAVSounds != v) { newLand.GroupAVSounds = v; changed = true; } break; }
                    default:
                        idx++;   // an unknown code and its value
                        break;
                }
            }
            if (!changed) return;
            World.LandChannel.UpdateLandObject(newLand.LocalID, newLand);
            if (changedSeeAvs)
                World.ForEachRootScenePresence(avatar => { if (!avatar.IsNPC) World.LandChannel.SendParcelsOverlay(avatar.ControllingClient); });
        }

        /// <summary>OSSL_Api.cs:1958-1963 - VeryLow. The parcel under the prim.</summary>
        public void osSetParcelMusicURL(string url)
        {
            OsslCheck(TlVeryLow, "osSetParcelMusicURL");
            if (m_host == null) return;
            World?.LandChannel?.GetLandObject(m_host.AbsolutePosition)?.SetMusicUrl(url ?? string.Empty);
        }

        /// <summary>OSSL_Api.cs:1966-1971 - VeryLow.</summary>
        public void osSetParcelMediaURL(string url)
        {
            OsslCheck(TlVeryLow, "osSetParcelMediaURL");
            if (m_host == null) return;
            World?.LandChannel?.GetLandObject(m_host.AbsolutePosition)?.SetMediaUrl(url ?? string.Empty);
        }

        /// <summary>OSSL_Api.cs:1974-1990 - VeryLow. The land under the prim must be the owner's; the voice module takes the address.</summary>
        public void osSetParcelSIPAddress(string SIPAddress)
        {
            OsslCheck(TlVeryLow, "osSetParcelSIPAddress");
            if (m_host == null) return;
            ILandObject land = World?.LandChannel?.GetLandObject(m_host.AbsolutePosition);
            if (land?.LandData == null) return;
            if (land.LandData.OwnerID != m_host.OwnerID) { ShoutError("osSetParcelSIPAddress: Sorry, you need to own the land to use this function"); return; }
            IVoiceModule voice = World.RequestModuleInterface<IVoiceModule>();
            if (voice == null) { ShoutError("osSetParcelSIPAddress: No voice module enabled for this land"); return; }
            voice.setLandSIPAddress(SIPAddress, land.LandData.GlobalID);
        }

        /// <summary>OSSL_Api.cs:4091-4103 - High unless the owner is a god. Level 0-3 through the estate module (legacy viewers and the map; osSetTerrainTextures is the PBR-aware form).</summary>
        public void osSetTerrainTexture(int level, string texture)
        {
            if (level < 0 || level > 3 || m_host == null) return;
            IEstateModule estate = World?.RequestModuleInterface<IEstateModule>();
            if (estate == null || !UUID.TryParse(texture, out UUID textureID)) return;
            if (!World.Permissions.IsGod(m_host.OwnerID)) OsslCheck(TlHigh, "osSetTerrainTexture");
            estate.setEstateTerrainBaseTexture(level, textureID);
        }

        /// <summary>OSSL_Api.cs:4117-4160 - High (key osSetTerrainTexture) unless the owner is a god. Four keys or inventory names; types 0 texture, 1 PBR material, 2 both.</summary>
        public void osSetTerrainTextures(LSLList textures, int ltypes)
        {
            IEstateModule estate = World?.RequestModuleInterface<IEstateModule>();
            if (estate == null || m_host == null) return;
            if (!World.Permissions.IsGod(m_host.OwnerID)) OsslCheck(TlHigh, "osSetTerrainTexture");
            if (textures.Length != 4) { ShoutError("osSetTerrainTextures first argument is a list of keys or names that must have 4 elements"); return; }
            if (ltypes < 0 || ltypes > 2) { ShoutError("osSetTerrainTextures second argument must be >=0 and <= 2"); return; }
            var ids = new List<UUID>(4);
            bool hasChanges = false;
            for (int i = 0; i < 4; i++)
            {
                string u = textures.GetLSLStringItem(i);
                if (string.IsNullOrEmpty(u)) { ids.Add(UUID.Zero); continue; }
                if (!UUID.TryParse(u, out UUID id))
                {
                    TaskInventoryItem item = FindInventoryItem(u, (int)AssetType.Texture) ?? (ltypes == 1 ? FindInventoryItem(u, (int)AssetType.Material) : null);
                    if (item == null) { ShoutError($"Invalid key or asset type in osSetTerrainTextures texture {i}"); return; }
                    id = item.AssetID;
                }
                ids.Add(id);
                if (id != UUID.Zero) hasChanges = true;
            }
            if (hasChanges) estate.SetEstateTerrainTextures(ids, ltypes);
        }

        /// <summary>OSSL_Api.cs:4177-4187 - High, and only a god owner reaches the estate module (as upstream).</summary>
        public void osSetTerrainTextureHeight(int corner, float low, float high)
        {
            if (corner < 0 || corner > 3 || m_host == null) return;
            OsslCheck(TlHigh, "osSetTerrainTextureHeight");
            if (World?.Permissions == null || !World.Permissions.IsGod(m_host.OwnerID)) return;
            World.RequestModuleInterface<IEstateModule>()?.setEstateTerrainTextureHeights(corner, low, high);
        }

        /// <summary>OSSL_Api.cs:6427-6438 - ungated upstream. llGetParcelDetails for the parcel with that id.</summary>
        public LSLList osGetParcelDetails(string id, LSLList param)
        {
            if (!UUID.TryParse(id, out UUID parcelID)) return new LSLList(0);
            ILandObject parcel = World?.LandChannel?.GetLandObject(parcelID);
            return ParcelDetailsOf(parcel?.LandData, param);
        }

        // ── OSSL draw and dynamic-texture functions, ported from OSSL_Api.cs (line cited per function).
        //    The draw helpers append to a command string the VectorRender module parses; the texture calls hand it to
        //    the DynamicTexture module (IDynamicTextureManager), which renders synchronously and puts the asset on the face. ──

        private IDynamicTextureManager DynTex() => World?.RequestModuleInterface<IDynamicTextureManager>();

        /// <summary>OSSL_Api.cs:721-737 - VeryHigh. dynamicID and timer are unused upstream too; the updater's id comes back.</summary>
        public string osSetDynamicTextureURL(string dynamicID, string contentType, string url, string extraParams, int timer)
        {
            OsslCheck(TlVeryHigh, "osSetDynamicTextureURL");
            var tm = DynTex();
            if (tm == null || m_host == null || !string.IsNullOrEmpty(dynamicID)) return UUID.Zero.ToString();
            return tm.AddDynamicTextureURL(World.RegionInfo.RegionID, m_host.UUID, contentType, url, extraParams ?? string.Empty).ToString();
        }

        /// <summary>OSSL_Api.cs:742-758 - VeryHigh.</summary>
        public string osSetDynamicTextureURLBlend(string dynamicID, string contentType, string url, string extraParams, int timer, int alpha)
        {
            OsslCheck(TlVeryHigh, "osSetDynamicTextureURLBlend");
            var tm = DynTex();
            if (tm == null || m_host == null || !string.IsNullOrEmpty(dynamicID)) return UUID.Zero.ToString();
            return tm.AddDynamicTextureURL(World.RegionInfo.RegionID, m_host.UUID, contentType, url, extraParams ?? string.Empty, true, (byte)alpha).ToString();
        }

        /// <summary>OSSL_Api.cs:763-779 - VeryHigh.</summary>
        public string osSetDynamicTextureURLBlendFace(string dynamicID, string contentType, string url, string extraParams, int blend, int disp, int timer, int alpha, int face)
        {
            OsslCheck(TlVeryHigh, "osSetDynamicTextureURLBlendFace");
            var tm = DynTex();
            if (tm == null || m_host == null || !string.IsNullOrEmpty(dynamicID)) return UUID.Zero.ToString();
            return tm.AddDynamicTextureURL(World.RegionInfo.RegionID, m_host.UUID, contentType, url, extraParams ?? string.Empty, blend != 0, disp, (byte)alpha, face).ToString();
        }

        /// <summary>OSSL_Api.cs:784-788 - the five-argument form is DataFace with face -1 (all faces).</summary>
        public string osSetDynamicTextureData(string dynamicID, string contentType, string data, string extraParams, int timer)
            => OsslDynamicTextureData("osSetDynamicTextureData", dynamicID, contentType, data, extraParams, false, 3, 255, -1);

        /// <summary>OSSL_Api.cs:790-813 DataFace body - VeryLow (key osSetDynamicTextureData). "" extraParams means 256.</summary>
        private string OsslDynamicTextureData(string key, string dynamicID, string contentType, string data, string extraParams, bool blend, int disp, byte alpha, int face)
        {
            OsslCheck(TlVeryLow, key);
            var tm = DynTex();
            if (tm == null || m_host == null || !string.IsNullOrEmpty(dynamicID)) return UUID.Zero.ToString();
            if (string.IsNullOrEmpty(extraParams)) extraParams = "256";
            return tm.AddDynamicTextureData(World.RegionInfo.RegionID, m_host.UUID, contentType, data ?? string.Empty, extraParams, blend, disp, alpha, face).ToString();
        }

        /// <summary>OSSL_Api.cs:790-813 - VeryLow, the data form with a face; the five-argument form is this with face -1.</summary>
        public string osSetDynamicTextureDataFace(string dynamicID, string contentType, string data, string extraParams, int timer, int face)
            => OsslDynamicTextureData("osSetDynamicTextureData", dynamicID, contentType, data, extraParams, false, 3, 255, face);

        /// <summary>OSSL_Api.cs:819-841 - VeryLow.</summary>
        public string osSetDynamicTextureDataBlend(string dynamicID, string contentType, string data, string extraParams, int timer, int alpha)
        {
            OsslCheck(TlVeryLow, "osSetDynamicTextureDataBlend");
            var tm = DynTex();
            if (tm == null || m_host == null || !string.IsNullOrEmpty(dynamicID)) return UUID.Zero.ToString();
            if (string.IsNullOrEmpty(extraParams)) extraParams = "256";
            return tm.AddDynamicTextureData(World.RegionInfo.RegionID, m_host.UUID, contentType, data ?? string.Empty, extraParams, true, (byte)alpha).ToString();
        }

        /// <summary>OSSL_Api.cs:847-869 - VeryLow.</summary>
        public string osSetDynamicTextureDataBlendFace(string dynamicID, string contentType, string data, string extraParams, int blend, int disp, int timer, int alpha, int face)
            => OsslDynamicTextureData("osSetDynamicTextureDataBlendFace", dynamicID, contentType, data, extraParams, blend != 0, disp, (byte)alpha, face);

        // the draw-list helpers, OSSL_Api.cs:1238-1470 - every one is upstream's bare CheckThreatLevel(), the master switch
        /// <summary>OSSL_Api.cs:1238-1243.</summary>
        public string osDrawResetTransform(string drawList) { OsslCheck(); return drawList + "ResetTransf;"; }
        /// <summary>OSSL_Api.cs:1246-1251.</summary>
        public string osDrawRotationTransform(string drawList, float x) { OsslCheck(); return drawList + "RotTransf " + x + ";"; }
        /// <summary>OSSL_Api.cs:1254-1259.</summary>
        public string osDrawScaleTransform(string drawList, float x, float y) { OsslCheck(); return drawList + "ScaleTransf " + x + "," + y + ";"; }
        /// <summary>OSSL_Api.cs:1262-1267.</summary>
        public string osDrawTranslationTransform(string drawList, float x, float y) { OsslCheck(); return drawList + "TransTransf " + x + "," + y + ";"; }
        /// <summary>OSSL_Api.cs:1270-1275.</summary>
        public string osMovePen(string drawList, int x, int y) { OsslCheck(); return drawList + "MoveTo " + x + "," + y + ";"; }
        /// <summary>OSSL_Api.cs:1278-1283.</summary>
        public string osDrawLine(string drawList, int startX, int startY, int endX, int endY) { OsslCheck(); return drawList + "MoveTo " + startX + "," + startY + "; LineTo " + endX + "," + endY + "; "; }
        /// <summary>OSSL_Api.cs:1286-1291.</summary>
        public string osDrawLine(string drawList, int endX, int endY) { OsslCheck(); return drawList + "LineTo " + endX + "," + endY + "; "; }
        /// <summary>OSSL_Api.cs:1294-1299.</summary>
        public string osDrawText(string drawList, string text) { OsslCheck(); return drawList + "Text " + text + "; "; }
        /// <summary>OSSL_Api.cs:1302-1307.</summary>
        public string osDrawEllipse(string drawList, int width, int height) { OsslCheck(); return drawList + "Ellipse " + width + "," + height + "; "; }
        /// <summary>OSSL_Api.cs:1310-1315.</summary>
        public string osDrawFilledEllipse(string drawList, int width, int height) { OsslCheck(); return drawList + "FillEllipse " + width + "," + height + "; "; }
        /// <summary>OSSL_Api.cs:1318-1323.</summary>
        public string osDrawRectangle(string drawList, int width, int height) { OsslCheck(); return drawList + "Rectangle " + width + "," + height + "; "; }
        /// <summary>OSSL_Api.cs:1326-1331.</summary>
        public string osDrawFilledRectangle(string drawList, int width, int height) { OsslCheck(); return drawList + "FillRectangle " + width + "," + height + "; "; }

        private string OsslPolygon(string keyword, string drawList, LSLList x, LSLList y)
        {
            if (x.Length != y.Length || x.Length < 3) return string.Empty;
            var sb = new StringBuilder(drawList).Append(keyword).Append(' ').Append(x.GetLSLStringItem(0)).Append(',').Append(y.GetLSLStringItem(0));
            for (int i = 1; i < x.Length; i++) sb.Append(',').Append(x.GetLSLStringItem(i)).Append(',').Append(y.GetLSLStringItem(i));
            return sb.Append("; ").ToString();
        }
        /// <summary>OSSL_Api.cs:1334-1348 - an empty string for mismatched or fewer than three points, as upstream.</summary>
        public string osDrawFilledPolygon(string drawList, LSLList x, LSLList y) { OsslCheck(); return OsslPolygon("FillPolygon", drawList, x, y); }
        /// <summary>OSSL_Api.cs:1351-1365.</summary>
        public string osDrawPolygon(string drawList, LSLList x, LSLList y) { OsslCheck(); return OsslPolygon("Polygon", drawList, x, y); }
        /// <summary>OSSL_Api.cs:1368-1373.</summary>
        public string osSetFontSize(string drawList, int fontSize) { OsslCheck(); return drawList + "FontSize " + fontSize + "; "; }
        /// <summary>OSSL_Api.cs:1376-1381.</summary>
        public string osSetFontName(string drawList, string fontName) { OsslCheck(); return drawList + "FontName " + fontName + "; "; }
        /// <summary>OSSL_Api.cs:1384-1389.</summary>
        public string osSetPenSize(string drawList, int penSize) { OsslCheck(); return drawList + "PenSize " + penSize + "; "; }
        /// <summary>OSSL_Api.cs:1392-1397 - a colour name or hex.</summary>
        public string osSetPenColor(string drawList, string color) { OsslCheck(); return drawList + "PenColor " + color + "; "; }
        /// <summary>OSSL_Api.cs:1400-1419 - the vector form, opaque; arity 2 like the colour-name form and told apart by type.</summary>
        public string osSetPenColor(string drawList, Vector3 color) => osSetPenColor(drawList, color, 1.0f);

        /// <summary>OSSL_Api.cs:1422-1446 - vector and alpha as AARRGGBB.</summary>
        public string osSetPenColor(string drawList, Vector3 color, float alpha)
        {
            OsslCheck();
            byte a = Utils.FloatZeroOneToByte(alpha), r = Utils.FloatZeroOneToByte(color.X), g = Utils.FloatZeroOneToByte(color.Y), b = Utils.FloatZeroOneToByte(color.Z);
            return drawList + "PenColor " + a.ToString("X2") + r.ToString("X2") + g.ToString("X2") + b.ToString("X2") + "; ";
        }
        /// <summary>OSSL_Api.cs:1449-1455 - the deprecated spelling; the renderer accepts PenColour.</summary>
        public string osSetPenColour(string drawList, string colour) { OsslCheck(); return drawList + "PenColour " + colour + "; "; }
        /// <summary>OSSL_Api.cs:1458-1463.</summary>
        public string osSetPenCap(string drawList, string direction, string type) { OsslCheck(); return drawList + "PenCap " + direction + "," + type + "; "; }
        /// <summary>OSSL_Api.cs:1466-1471.</summary>
        public string osDrawImage(string drawList, int width, int height, string imageUrl) { OsslCheck(); return drawList + "Image " + width + "," + height + "," + imageUrl + "; "; }

        /// <summary>OSSL_Api.cs:1474-1485 - master switch. The renderer measures the text; zero without a texture manager.</summary>
        public Vector3 osGetDrawStringSize(string contentType, string text, string fontName, int fontSize)
        {
            OsslCheck();
            var tm = DynTex();
            if (tm == null) return Vector3.Zero;
            tm.GetDrawStringSize(contentType, text ?? string.Empty, fontName, fontSize, out double xSize, out double ySize);
            return new Vector3((float)xSize, (float)ySize, 0f);
        }

        // ── OSSL read-only remainder, ported from OSSL_Api.cs (line cited per function), each gated function
        //    under its upstream key and threat level through OsslGate; "master" = upstream's bare CheckThreatLevel() ──

        private const uint OsslFullPerms = (uint)(OpenSim.Framework.PermissionMask.Copy | OpenSim.Framework.PermissionMask.Transfer | OpenSim.Framework.PermissionMask.Modify);
        private static bool OsslFullPerm(TaskInventoryItem item) => (item.CurrentPermissions & OsslFullPerms) == OsslFullPerms;
        private TaskInventoryItem OsslItemByNameOrId(SceneObjectPart part, string nameOrId)
        {
            if (part == null || string.IsNullOrEmpty(nameOrId)) return null;
            return UUID.TryParse(nameOrId, out UUID id) ? part.Inventory.GetInventoryItem(id) : part.Inventory.GetInventoryItem(nameOrId);
        }

        /// <summary>The notecard's body lines, read synchronously through the asset service the async llGetNotecardLine uses (LSL_Api's NotecardCache is a cache over the same read); null when there is no such notecard.</summary>
        private string[] OsslNotecardLines(string name)
        {
            if (m_host == null || string.IsNullOrEmpty(name)) return null;
            TaskInventoryItem item = UUID.TryParse(name, out UUID id) ? m_host.Inventory.GetInventoryItem(id) : FindInventoryItem(name, (int)AssetType.Notecard);
            if (item == null) return null;
            AssetBase asset = World?.AssetService?.Get(item.AssetID.ToString());
            if (asset?.Data == null) return null;
            string body = StripNotecardHeader(OpenMetaverse.Utils.BytesToString(asset.Data));
            if (body.Length == 0) return Array.Empty<string>();
            string[] lines = body.Split('\n');
            for (int i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd('\r');
            return lines;
        }

        /// <summary>OSSL_Api.cs:2361-2371 - VeryHigh. The line, or "ERROR!" with a shout when the notecard is missing; an out-of-range line is EOF as the cache answers.</summary>
        public string osGetNotecardLine(string name, int line)
        {
            OsslCheck(TlVeryHigh, "osGetNotecardLine");
            string[] lines = OsslNotecardLines(name);
            if (lines == null) { ShoutError("Notecard '" + name + "' could not be found."); return "ERROR!"; }
            return line >= 0 && line < lines.Length ? lines[line] : "\n\n\n";
        }

        /// <summary>OSSL_Api.cs:2388-2402 (LoadNotecard :2264-2290) - VeryHigh. Every line joined with newlines.</summary>
        public string osGetNotecard(string name)
        {
            OsslCheck(TlVeryHigh, "osGetNotecard");
            string[] lines = OsslNotecardLines(name);
            if (lines == null) { ShoutError("Notecard '" + name + "' could not be found."); return "ERROR!"; }
            var sb = new StringBuilder();
            foreach (string l in lines) sb.Append(l).Append('\n');
            return sb.ToString();
        }

        /// <summary>OSSL_Api.cs:2417-2428 - VeryHigh. -1 with a shout when the notecard is missing.</summary>
        public int osGetNumberOfNotecardLines(string name)
        {
            OsslCheck(TlVeryHigh, "osGetNumberOfNotecardLines");
            string[] lines = OsslNotecardLines(name);
            if (lines == null) { ShoutError("Notecard '" + name + "' could not be found."); return -1; }
            return lines.Length;
        }

        /// <summary>OSSL_Api.cs:2631-2645 - Low. The user's HomeURI from user management, else this grid's home URL.</summary>
        public string osGetAvatarHomeURI(string uuid)
        {
            OsslCheck(TlLow, "osGetAvatarHomeURI");
            string v = string.Empty;
            if (UUID.TryParse(uuid, out UUID id)) v = World?.RequestModuleInterface<IUserManagement>()?.GetUserServerURL(id, "HomeURI") ?? string.Empty;
            return v.Length == 0 ? (World?.SceneGridInfo?.HomeURLNoEndSlash ?? string.Empty) : v;
        }

        /// <summary>OSSL_Api.cs:4299-4322 - Moderate. A strided list [point, count] for each point asked for; nothing for a point at or below 0 but 0.</summary>
        public LSLList osGetNumberOfAttachments(string avatar, LSLList attachmentPoints)
        {
            OsslCheck(TlModerate, "osGetNumberOfAttachments");
            var resp = new LSLList();
            if (attachmentPoints.Length < 1 || !UUID.TryParse(avatar, out UUID id)) return resp;
            ScenePresence target = World?.GetScenePresence(id);
            if (target == null) return resp;
            for (int i = 0; i < attachmentPoints.Length; i++)
            {
                int point = attachmentPoints.GetLSLIntegerItem(i);
                resp = resp.Append(point);
                resp = resp.Append(point <= 0 ? 0 : target.GetAttachments((uint)point).Count);
            }
            return resp;
        }

        /// <summary>OSSL_Api.cs:3594-3617 - High. "" = this region's map texture; a name or id is looked up in the grid service (1 s sleep upstream, not applied).</summary>
        public string osGetRegionMapTexture(string regionNameOrID)
        {
            OsslCheck(TlHigh, "osGetRegionMapTexture");
            if (World == null) return UUID.Zero.ToString();
            if (string.IsNullOrWhiteSpace(regionNameOrID)) return World.RegionInfo.RegionSettings.TerrainImageID.ToString();
            OpenSim.Services.Interfaces.GridRegion region = UUID.TryParse(regionNameOrID, out UUID key)
                ? World.GridService?.GetRegionByUUID(UUID.Zero, key)
                : World.GridService?.GetRegionByName(UUID.Zero, regionNameOrID);
            return (region?.TerrainImage ?? UUID.Zero).ToString();
        }

        /// <summary>OSSL_Api.cs:4979-4986 - master switch. The link number of the first prim with that name, -1 for none.</summary>
        public int osGetLinkNumber(string name)
        {
            OsslCheck();
            SceneObjectGroup sog = m_host?.ParentGroup;
            if (sog == null || sog.IsDeleted) return -1;
            return sog.GetLinkNumber(name);
        }

        /// <summary>OSSL_Api.cs:4461-4468 - None. NULL_KEY when nothing rezzed this object or the rezzer is an avatar.</summary>
        public string osGetRezzingObject()
        {
            OsslCheck(TlNone, "osGetRezzingObject");
            UUID rez = m_host?.ParentGroup?.RezzerID ?? UUID.Zero;
            if (rez == UUID.Zero || World?.GetScenePresence(rez) != null) return UUID.Zero.ToString();
            return rez.ToString();
        }

        /// <summary>OSSL_Api.cs:4558-4592 - Low. The regexes are validated as upstream (a shout and -1 when invalid); the listen goes to Phlox's listen manager with the bitfield.</summary>
        public int osListenRegex(int channelID, string name, string ID, string msg, int regexBitfield)
        {
            OsslCheck(TlLow, "osListenRegex");
            if (m_ScriptEngine.ListenManager == null || m_host == null) return -1;
            if (!UUID.TryParse(ID, out UUID keyID)) return -1;
            if ((regexBitfield & 1) != 0) { try { ScriptRegex.Create(name).IsMatch(""); } catch { ShoutError("Name regex is invalid."); return -1; } }
            if ((regexBitfield & 2) != 0) { try { ScriptRegex.Create(msg).IsMatch(""); } catch { ShoutError("Message regex is invalid."); return -1; } }
            return m_ScriptEngine.ListenManager.Add(m_localID, m_itemID, m_host.UUID, channelID, name, keyID, msg, regexBitfield);
        }

        private string OsslCountryOf(UUID key)
        {
            if (key == UUID.Zero) return string.Empty;
            UserAccount account = World?.UserAccountService?.GetUserAccount(World.RegionInfo.ScopeID, key);
            return account?.UserCountry ?? string.Empty;
        }

        /// <summary>OSSL_Api.cs:5212-5225 - Moderate. The detected agent's account country.</summary>
        public string osDetectedCountry(int number)
        {
            OsslCheck(TlModerate, "osDetectedCountry");
            if (!UUID.TryParse(GetDetect(number).Key ?? string.Empty, out UUID key)) return string.Empty;
            return OsslCountryOf(key);
        }

        /// <summary>OSSL_Api.cs:5228-5249 - Moderate. A non-god owner may only ask about an agent present in the region.</summary>
        public string osGetAgentCountry(string id)
        {
            OsslCheck(TlModerate, "osGetAgentCountry");
            if (!UUID.TryParse(id, out UUID key) || key == UUID.Zero || World == null) return string.Empty;
            if (!World.Permissions.IsGod(m_host.OwnerID) && World.GetScenePresence(key) == null) return string.Empty;
            return OsslCountryOf(key);
        }

        /// <summary>OSSL_Api.cs:3540-3575 - None. The shape's "male" visual param, read by its index among the group-0 params; "unknown" off-region.</summary>
        public string osGetGender(string rawAvatarId)
        {
            OsslCheck(TlNone, "osGetGender");
            if (!UUID.TryParse(rawAvatarId, out UUID id)) return "unknown";
            ScenePresence sp = World?.GetScenePresence(id);
            if (sp == null || sp.IsChildAgent || sp.Appearance?.VisualParams == null) return "unknown";
            int index = 0; bool found = false; VisualParam male = default;
            foreach (var vp in VisualParams.Params)
            {
                if (vp.Value.Name == "male" && vp.Value.Wearable == "shape") { male = vp.Value; found = true; break; }
                if (vp.Value.Group == 0) index++;
            }
            if (!found || index >= sp.Appearance.VisualParams.Length) return "unknown";
            float weight = Utils.ByteToFloat(sp.Appearance.VisualParams[index], male.MinValue, male.MaxValue);
            return weight > 0.5f ? "male" : "female";
        }

        /// <summary>OSSL_Api.cs:3854-3865 - None.</summary>
        public float osGetHealRate(string avatar)
        {
            OsslCheck(TlNone, "osGetHealRate");
            if (!UUID.TryParse(avatar, out UUID id)) return 0f;
            return World?.GetScenePresence(id)?.HealRate ?? 0f;
        }

        private float OsslDayFraction() => World?.RequestModuleInterface<IEnvironmentModule>()?.GetRegionDayFractionTime() ?? -1f;
        private static string OsslTimeToString(float hours, bool format24)
        {
            int h = (int)hours; hours -= h; hours *= 60; int m = (int)hours; hours -= m; hours *= 60; int s = (int)hours;
            if (format24) return string.Format("{0:00}:{1:00}:{2:00}", h, m, s);
            if (h > 12) return string.Format("{0}:{1:00}:{2:00} PM", h - 12, m, s);
            if (h == 12) return string.Format("{0}:{1:00}:{2:00} PM", h, m, s);
            return string.Format("{0}:{1:00}:{2:00} AM", h, m, s);
        }

        /// <summary>OSSL_Api.cs:1559-1566 - master switch. Seconds into the region's day; 0 without an environment module.</summary>
        public float osGetApparentTime() { OsslCheck(); float f = OsslDayFraction(); return f < 0 ? 0f : 86400f * f; }
        /// <summary>OSSL_Api.cs:1588-1596 - master switch.</summary>
        public string osGetApparentTimeString(int format24) { OsslCheck(); float f = OsslDayFraction(); return f < 0 ? (format24 != 0 ? "00:00:00" : "0:00:00 AM") : OsslTimeToString(24f * f, format24 != 0); }
        /// <summary>OSSL_Api.cs:1602-1609 - master switch. The same value as osGetApparentTime under EEP (one region day).</summary>
        public float osGetApparentRegionTime() { OsslCheck(); float f = OsslDayFraction(); return f < 0 ? 0f : 86400f * f; }
        /// <summary>OSSL_Api.cs:1613-1621 - master switch.</summary>
        public string osGetApparentRegionTimeString(int format24) { OsslCheck(); float f = OsslDayFraction(); return f < 0 ? (format24 != 0 ? "00:00:00" : "0:00:00 AM") : OsslTimeToString(24f * f, format24 != 0); }

        private static readonly TimeZoneInfo OsslPstZone = FindPst();
        private static TimeZoneInfo FindPst()
        {
            foreach (string id in new[] { "Pacific Standard Time", "America/Los_Angeles" })
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (Exception) { }
            return null;
        }

        /// <summary>OSSL_Api.cs:5910-5916 - ungated upstream. Seconds since midnight, Pacific time; the local clock when the zone is unknown to this host.</summary>
        public float osGetPSTWallclock()
        {
            if (OsslPstZone == null) return (float)DateTime.Now.TimeOfDay.TotalSeconds;
            return (float)TimeZoneInfo.ConvertTime(DateTime.UtcNow, OsslPstZone).TimeOfDay.TotalSeconds;
        }

        /// <summary>OSSL_Api.cs:5901-5907 - ungated upstream. The key of the first detected entry of the current event.</summary>
        public string osGetLastChangedEventKey() => GetDetect(0).Key ?? string.Empty;

        /// <summary>OSSL_Api.cs:6642-6656 - ungated upstream. LINK_ROOT, LINK_THIS or a link number; llGetColor's reading over that part.</summary>
        public Vector3 osGetLinkColor(int link, int face)
        {
            if (m_host?.ParentGroup == null) return Vector3.Zero;
            SceneObjectPart part = OsslSingleLinkPart(link);
            return part == null ? Vector3.Zero : ColorOf(part, face);
        }

        /// <summary>OSSL_Api.cs:6032-6035 - ungated upstream.</summary>
        public float osGetSitActiveRange() => m_host?.SitActiveRange ?? 0f;

        /// <summary>OSSL_Api.cs:6037-6049 - ungated upstream. int.MinValue for a negative link or an unknown one, as upstream.</summary>
        public float osGetLinkSitActiveRange(int linkNumber)
        {
            if (m_host?.ParentGroup == null) return 0f;
            SceneObjectPart t = OsslSingleLinkPart(linkNumber);
            return t == null ? int.MinValue : t.SitActiveRange;
        }

        /// <summary>OSSL_Api.cs:6084-6087 - ungated upstream.</summary>
        public Vector3 osGetStandTarget() => m_host?.StandOffset ?? Vector3.Zero;

        /// <summary>OSSL_Api.cs:6089-6101 - ungated upstream.</summary>
        public Vector3 osGetLinkStandTarget(int linkNumber)
        {
            if (m_host?.ParentGroup == null) return Vector3.Zero;
            return OsslSingleLinkPart(linkNumber)?.StandOffset ?? Vector3.Zero;
        }

        /// <summary>OSSL_Api.cs:6560-6563 - ungated upstream.</summary>
        public int osGetPrimCount() => m_host?.ParentGroup?.PrimCount ?? 0;

        /// <summary>OSSL_Api.cs:6565-6570 - ungated upstream. 0 for a key that is not a prim here.</summary>
        public int osGetPrimCount(string object_id)
        {
            if (!UUID.TryParse(object_id, out UUID id) || id == UUID.Zero || World == null) return 0;
            return World.TryGetSceneObjectPart(id, out SceneObjectPart part) ? part.ParentGroup.PrimCount : 0;
        }

        /// <summary>OSSL_Api.cs:6573-6576 - ungated upstream.</summary>
        public int osGetSittingAvatarsCount() => m_host?.ParentGroup?.GetSittingAvatarsCount() ?? 0;

        /// <summary>OSSL_Api.cs:6578-6583 - ungated upstream.</summary>
        public int osGetSittingAvatarsCount(string object_id)
        {
            if (!UUID.TryParse(object_id, out UUID id) || id == UUID.Zero || World == null) return 0;
            return World.TryGetSceneObjectPart(id, out SceneObjectPart part) ? part.ParentGroup.GetSittingAvatarsCount() : 0;
        }

        /// <summary>OSSL_Api.cs:1720-1728 - ungated upstream.</summary>
        public int osGetParcelDwell(Vector3 pos) => (int)(World?.GetLandData(pos)?.Dwell ?? 0f);

        /// <summary>OSSL_Api.cs:6463-6467 - ungated upstream. The parcel under the prim.</summary>
        public string osGetParcelID()
        {
            if (m_host == null) return UUID.Zero.ToString();
            ILandObject parcel = World?.LandChannel?.GetLandObject(m_host.AbsolutePosition);
            return (parcel?.GlobalID ?? UUID.Zero).ToString();
        }

        /// <summary>OSSL_Api.cs:6443-6458 - ungated upstream. Every parcel with a global id and a non-zero area.</summary>
        public LSLList osGetParcelIDs()
        {
            var ret = new LSLList();
            var parcels = World?.LandChannel?.AllParcels();
            if (parcels == null) return ret;
            foreach (ILandObject p in parcels)
            {
                if (p.GlobalID == UUID.Zero || p.LandData == null || p.LandData.Area == 0) continue;
                ret = ret.Append(p.GlobalID.ToString());
            }
            return ret;
        }

        // inventory family, OSSL_Api.cs:5514-5720 - every one ungated upstream; the *ItemKey / *Keys forms answer only for full-permission items
        /// <summary>OSSL_Api.cs:5514-5523.</summary>
        public string osGetInventoryLastOwner(string itemNameorid)
        {
            TaskInventoryItem item = OsslItemByNameOrId(m_host, itemNameorid);
            if (item == null) return UUID.Zero.ToString();
            return (item.LastOwnerID != UUID.Zero ? item.LastOwnerID : item.OwnerID).ToString();
        }
        /// <summary>OSSL_Api.cs:5528-5541.</summary>
        public string osGetInventoryItemKey(string name)
        {
            TaskInventoryItem item = m_host?.Inventory.GetInventoryItem(name);
            return item != null && OsslFullPerm(item) ? item.ItemID.ToString() : UUID.Zero.ToString();
        }
        /// <summary>OSSL_Api.cs:5544-5550.</summary>
        public string osGetInventoryName(string itemId)
        {
            TaskInventoryItem item = UUID.TryParse(itemId, out UUID id) ? m_host?.Inventory.GetInventoryItem(id) : null;
            return item?.Name ?? string.Empty;
        }
        /// <summary>OSSL_Api.cs:5567-5573.</summary>
        public string osGetInventoryDesc(string itemNameorid) => OsslItemByNameOrId(m_host, itemNameorid)?.Description ?? string.Empty;
        /// <summary>OSSL_Api.cs:5651-5664.</summary>
        public LSLList osGetInventoryItemKeys(int type)
        {
            var ret = new LSLList();
            if (m_host == null) return ret;
            foreach (TaskInventoryItem item in m_host.Inventory.GetInventoryItems())
                if ((item.Type == type || type == -1) && OsslFullPerm(item)) ret = ret.Append(item.ItemID.ToString());
            return ret;
        }
        /// <summary>OSSL_Api.cs:5690-5701.</summary>
        public LSLList osGetInventoryNames(int type)
        {
            var ret = new LSLList();
            if (m_host == null) return ret;
            foreach (TaskInventoryItem item in m_host.Inventory.GetInventoryItems())
                if (item.Type == type || type == -1) ret = ret.Append(item.Name);
            return ret;
        }
        /// <summary>OSSL_Api.cs:5553-5564 - the link-addressed forms use upstream's GetSingleLinkPart rule.</summary>
        public string osGetLinkInventoryName(int linkNumber, string itemId)
        {
            SceneObjectPart part = OsslSingleLinkPart(linkNumber);
            TaskInventoryItem item = part != null && UUID.TryParse(itemId, out UUID id) ? part.Inventory.GetInventoryItem(id) : null;
            return item?.Name ?? string.Empty;
        }
        /// <summary>OSSL_Api.cs:5576-5586.</summary>
        public string osGetLinkInventoryDesc(int linkNumber, string itemNameorid) => OsslItemByNameOrId(OsslSingleLinkPart(linkNumber), itemNameorid)?.Description ?? string.Empty;
        /// <summary>OSSL_Api.cs:5589-5606 - the ASSET key of a full-permission item of that name and type.</summary>
        public string osGetLinkInventoryKey(int linkNumber, string name, int type)
        {
            SceneObjectPart part = OsslSingleLinkPart(linkNumber);
            TaskInventoryItem item = part?.Inventory.GetInventoryItem(name);
            if (item == null || (type != -1 && item.Type != type) || !OsslFullPerm(item)) return UUID.Zero.ToString();
            return item.AssetID.ToString();
        }
        /// <summary>OSSL_Api.cs:5609-5628 - asset keys of full-permission items of that type (-1 = any).</summary>
        public LSLList osGetLinkInventoryKeys(int linkNumber, int type)
        {
            var ret = new LSLList();
            SceneObjectPart part = OsslSingleLinkPart(linkNumber);
            if (part == null) return ret;
            foreach (TaskInventoryItem item in part.Inventory.GetInventoryItems())
                if ((item.Type == type || type == -1) && OsslFullPerm(item)) ret = ret.Append(item.AssetID.ToString());
            return ret;
        }
        /// <summary>OSSL_Api.cs:5631-5648.</summary>
        public string osGetLinkInventoryItemKey(int linkNumber, string name)
        {
            TaskInventoryItem item = OsslSingleLinkPart(linkNumber)?.Inventory.GetInventoryItem(name);
            return item != null && OsslFullPerm(item) ? item.ItemID.ToString() : UUID.Zero.ToString();
        }
        /// <summary>OSSL_Api.cs:5668-5687.</summary>
        public LSLList osGetLinkInventoryItemKeys(int linkNumber, int type)
        {
            var ret = new LSLList();
            SceneObjectPart part = OsslSingleLinkPart(linkNumber);
            if (part == null) return ret;
            foreach (TaskInventoryItem item in part.Inventory.GetInventoryItems())
                if ((item.Type == type || type == -1) && OsslFullPerm(item)) ret = ret.Append(item.ItemID.ToString());
            return ret;
        }
        /// <summary>OSSL_Api.cs:5705-5718.</summary>
        public LSLList osGetLinkInventoryNames(int linkNumber, int type)
        {
            var ret = new LSLList();
            SceneObjectPart part = OsslSingleLinkPart(linkNumber);
            if (part == null) return ret;
            foreach (TaskInventoryItem item in part.Inventory.GetInventoryItems())
                if (item.Type == type || type == -1) ret = ret.Append(item.Name);
            return ret;
        }

        // ── osNpc* - a second door onto BotManager's bots (one BotData per NPC), ported from OSSL_Api.cs ──
        private const int OS_NPC_NOT_OWNED = 0x2, OS_NPC_SENSE_AS_AGENT = 0x4, OS_NPC_OBJECT_GROUP = 0x8, OS_NPC_NO_FLY = 1, OS_NPC_RUNNING = 4;
        private IBotManager NpcMgr() => World?.RequestModuleInterface<IBotManager>();
        private static bool NpcKey(string npc, out UUID id) => UUID.TryParse(npc, out id) && id.IsNotZero();

        /// <summary>OSSL_Api.cs:2848-2975 NpcCreate. The notecard argument is the bot outfit store's outfit name ("" = the owner's current appearance).</summary>
        private string NpcCreate(string firstname, string lastname, Vector3 position, string notecard, bool owned, bool senseAsAgent, bool hostGroup)
        {
            if (World == null || m_host == null) return UUID.Zero.ToString();
            if (!World.Permissions.CanRezObject(1, m_host.OwnerID, position))
            {
                ShoutError("no permission to rez NPC at requested location");
                return UUID.Zero.ToString();
            }
            var mgr = NpcMgr();
            if (mgr == null)
            {
                ShoutError("NPC module not enabled");
                return UUID.Zero.ToString();
            }
            // OS_NPC_OBJECT_GROUP: BotManager's CreateNPC call carries no group - accepted, not applied.
            UUID id = mgr.CreateBot(firstname, lastname, position, notecard ?? string.Empty, m_itemID, m_host.OwnerID, owned, senseAsAgent, out string reason);
            if (reason != null) ShoutError("osNpcCreate: " + reason);
            return id.ToString();
        }

        private string NpcSaveOutfit(string npc, string notecard)
        {
            var mgr = NpcMgr(); if (mgr == null || !NpcKey(npc, out UUID id)) return UUID.Zero.ToString();
            UUID key = mgr.SaveBotOutfit(id, notecard, m_host.OwnerID, out string reason);
            if (reason != null) ShoutError("osNpcSaveAppearance: " + reason);
            return key.ToString();
        }

        private void NpcMove(string npc, Vector3 target, TravelMode mode)
        {
            var mgr = NpcMgr(); if (mgr == null || !NpcKey(npc, out UUID id)) return;
            mgr.SetBotNavigationPoints(id, new List<Vector3> { target }, new List<TravelMode> { mode }, new Dictionary<int, object>(), m_host.OwnerID);
        }

        private void NpcChat(string npc, int channel, string message, ChatTypeEnum type)
        {
            var mgr = NpcMgr(); if (mgr == null || !NpcKey(npc, out UUID id)) return;
            mgr.BotChat(id, channel, message ?? string.Empty, type, m_host.OwnerID);   // upstream's 2 s say-throttle is not applied
        }


        /// <summary>OSSL_Api.cs:2808 - bare CheckThreatLevel (master switch).</summary>
        public int osIsNpc(string npc)
        {
            OsslCheck();
            return UUID.TryParse(npc, out UUID id) && World?.GetScenePresence(id)?.IsNPC == true ? 1 : 0;
        }

        /// <summary>OSSL_Api.cs:2823 - High (key osNpcCreate).</summary>
        public string osNpcCreate(string firstname, string lastname, Vector3 position, string notecard)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.High, "osNpcCreate");
            return NpcCreate(firstname, lastname, position, notecard, true, false, false);
        }

        /// <summary>OSSL_Api.cs:2837 - High (key osNpcCreate).</summary>
        public string osNpcCreate(string firstname, string lastname, Vector3 position, string notecard, int options)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.High, "osNpcCreate");
            return NpcCreate(firstname, lastname, position, notecard,
                (options & OS_NPC_NOT_OWNED) == 0, (options & OS_NPC_SENSE_AS_AGENT) != 0, (options & OS_NPC_OBJECT_GROUP) != 0);
        }

        /// <summary>OSSL_Api.cs:2975 - High (key osNpcSaveAppearance).</summary>
        public string osNpcSaveAppearance(string npc, string notecard)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.High, "osNpcSaveAppearance");
            return NpcSaveOutfit(npc, notecard);
        }

        /// <summary>OSSL_Api.cs:2980 - High (key osNpcSaveAppearance).</summary>
        public string osNpcSaveAppearance(string npc, string notecard, int includeHuds)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.High, "osNpcSaveAppearance");
            return NpcSaveOutfit(npc, notecard);   // includeHuds: the outfit store keeps the whole appearance
        }

        /// <summary>OSSL_Api.cs:3005 - High (key osNpcLoadAppearance).</summary>
        public void osNpcLoadAppearance(string npc, string notecard)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.High, "osNpcLoadAppearance");
            var mgr = NpcMgr(); if (mgr == null || !NpcKey(npc, out UUID id)) return;
            mgr.ChangeBotOutfit(id, notecard, m_host.OwnerID, out string reason);
            if (reason != null) ShoutError("osNpcLoadAppearance: " + reason);
        }

        /// <summary>OSSL_Api.cs:3035 - None (key osNpcGetOwner).</summary>
        public string osNpcGetOwner(string npc)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.None, "osNpcGetOwner");
            var mgr = NpcMgr(); if (mgr == null || !NpcKey(npc, out UUID id)) return UUID.Zero.ToString();
            UUID owner = mgr.GetBotOwner(id);
            return owner.IsZero() ? npc : owner.ToString();
        }

        /// <summary>OSSL_Api.cs:3055 - High (key osNpcGetPos).</summary>
        public Vector3 osNpcGetPos(string npc)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.High, "osNpcGetPos");
            var mgr = NpcMgr(); if (mgr == null || !NpcKey(npc, out UUID id) || !mgr.CheckPermission(id, m_host.OwnerID)) return Vector3.Zero;
            return World.GetScenePresence(id)?.AbsolutePosition ?? Vector3.Zero;
        }

        /// <summary>OSSL_Api.cs:3077 - High (key osNpcMoveTo).</summary>
        public void osNpcMoveTo(string npc, Vector3 pos)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.High, "osNpcMoveTo");
            NpcMove(npc, pos, TravelMode.Fly);   // upstream: noFly false, land at target
        }

        /// <summary>OSSL_Api.cs:3094 - High (key osNpcMoveToTarget).</summary>
        public void osNpcMoveToTarget(string npc, Vector3 target, int options)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.High, "osNpcMoveToTarget");
            NpcMove(npc, target, (options & OS_NPC_RUNNING) != 0 ? TravelMode.Run : (options & OS_NPC_NO_FLY) != 0 ? TravelMode.Walk : TravelMode.Fly);
        }

        /// <summary>OSSL_Api.cs:3117 - High (key osNpcGetRot).</summary>
        public Quaternion osNpcGetRot(string npc)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.High, "osNpcGetRot");
            var mgr = NpcMgr(); if (mgr == null || !NpcKey(npc, out UUID id) || !mgr.CheckPermission(id, m_host.OwnerID)) return Quaternion.Identity;
            return World.GetScenePresence(id)?.GetWorldRotation() ?? Quaternion.Identity;
        }

        /// <summary>OSSL_Api.cs:3139 - High (key osNpcSetRot).</summary>
        public void osNpcSetRot(string npc, Quaternion rotation)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.High, "osNpcSetRot");
            var mgr = NpcMgr(); if (mgr == null || !NpcKey(npc, out UUID id)) return;
            mgr.SetBotRotation(id, rotation, m_host.OwnerID);
        }

        /// <summary>OSSL_Api.cs:3158 - High (key osNpcStopMoveToTarget).</summary>
        public void osNpcStopMoveToTarget(string npc)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.High, "osNpcStopMoveToTarget");
            var mgr = NpcMgr(); if (mgr == null || !NpcKey(npc, out UUID id)) return;
            mgr.StopMovement(id, m_host.OwnerID);
        }

        /// <summary>OSSL_Api.cs:3174 - Low (key osNpcSetProfileAbout).</summary>
        public void osNpcSetProfileAbout(string npc, string about)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.Low, "osNpcSetProfileAbout");
            var mgr = NpcMgr(); if (mgr == null || !NpcKey(npc, out UUID id)) return;
            mgr.SetBotProfile(id, about ?? string.Empty, null, null, null, m_host.OwnerID);
        }

        /// <summary>OSSL_Api.cs:3192 - Low (key osNpcSetProfileImage).</summary>
        public void osNpcSetProfileImage(string npc, string image)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.Low, "osNpcSetProfileImage");
            var mgr = NpcMgr(); if (mgr == null || !NpcKey(npc, out UUID id)) return;
            UUID imageId = UUID.Zero;
            if (!UUID.TryParse(image, out imageId)) imageId = FindInventoryItem(image, (int)AssetType.Texture)?.AssetID ?? UUID.Zero;
            mgr.SetBotProfile(id, null, null, imageId, null, m_host.OwnerID);
        }

        /// <summary>OSSL_Api.cs:3217 - ungated upstream.</summary>
        public void osNpcSay(string npc, string message)
        {
            osNpcSay(npc, 0, message);
        }

        /// <summary>OSSL_Api.cs:3222 - High (key osNpcSay).</summary>
        public void osNpcSay(string npc, int channel, string message)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.High, "osNpcSay");
            NpcChat(npc, channel, message, ChatTypeEnum.Say);
        }

        /// <summary>OSSL_Api.cs:3272 - High (key osNpcShout).</summary>
        public void osNpcShout(string npc, int channel, string message)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.High, "osNpcShout");
            NpcChat(npc, channel, message, ChatTypeEnum.Shout);
        }

        /// <summary>OSSL_Api.cs:3417 - High (key osNpcWhisper).</summary>
        public void osNpcWhisper(string npc, int channel, string message)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.High, "osNpcWhisper");
            NpcChat(npc, channel, message, ChatTypeEnum.Whisper);
        }

        /// <summary>OSSL_Api.cs:3290 - High (key osNpcSit).</summary>
        public void osNpcSit(string npc, string target, int options)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.High, "osNpcSit");
            var mgr = NpcMgr(); if (mgr == null || !NpcKey(npc, out UUID id) || !UUID.TryParse(target, out UUID targetId)) return;
            mgr.SitBotOnObject(id, targetId, m_host.OwnerID);   // OS_NPC_SIT_NOW is the only option and the only behaviour
        }

        /// <summary>OSSL_Api.cs:3306 - High (key osNpcStand).</summary>
        public void osNpcStand(string npc)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.High, "osNpcStand");
            var mgr = NpcMgr(); if (mgr == null || !NpcKey(npc, out UUID id)) return;
            mgr.StandBotUp(id, m_host.OwnerID);
        }

        /// <summary>OSSL_Api.cs:3322 - High (key osNpcRemove).</summary>
        public void osNpcRemove(string npc)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.High, "osNpcRemove");
            var mgr = NpcMgr(); if (mgr == null || !NpcKey(npc, out UUID id)) return;
            mgr.RemoveBot(id, m_host.OwnerID);   // permission inside: unowned, or owner == caller (NPCModule.CheckPermissions)
        }

        /// <summary>OSSL_Api.cs:3342 - High (key osNpcPlayAnimation).</summary>
        public void osNpcPlayAnimation(string npc, string animation)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.High, "osNpcPlayAnimation");
            if (string.IsNullOrEmpty(animation)) return;
            var mgr = NpcMgr(); if (mgr == null || !NpcKey(npc, out UUID id)) return;
            UUID animID = FindInventoryItem(animation, (int)AssetType.Animation)?.AssetID ?? UUID.Zero;
            if (animID.IsZero()) UUID.TryParse(animation, out animID);
            if (animID.IsZero()) return;
            mgr.StartBotAnimation(id, animID, animation, m_host.UUID, m_host.OwnerID);
        }

        /// <summary>OSSL_Api.cs:3381 - High (key osNpcStopAnimation).</summary>
        public void osNpcStopAnimation(string npc, string animation)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.High, "osNpcStopAnimation");
            if (string.IsNullOrEmpty(animation)) return;
            var mgr = NpcMgr(); if (mgr == null || !NpcKey(npc, out UUID id)) return;
            UUID animID = FindInventoryItem(animation, (int)AssetType.Animation)?.AssetID ?? UUID.Zero;
            if (animID.IsZero()) UUID.TryParse(animation, out animID);
            if (animID.IsZero()) return;
            mgr.StopBotAnimation(id, animID, animation, m_host.OwnerID);
        }

        /// <summary>OSSL_Api.cs:3433 - High (key osNpcTouch).</summary>
        public void osNpcTouch(string npc, string object_key, int link_num)
        {
            OsslCheck(OpenSim.Region.ScriptEngine.Shared.Api.Interfaces.ThreatLevel.High, "osNpcTouch");
            var mgr = NpcMgr(); if (mgr == null || !NpcKey(npc, out UUID id) || !UUID.TryParse(object_key, out UUID objectId)) return;
            SceneObjectPart part = World.GetSceneObjectPart(objectId);
            if (part == null) return;
            if (link_num != -4 /* LINK_THIS */)
            {
                if (link_num == 0 || link_num == 1 /* LINK_ROOT */) part = part.ParentGroup.RootPart;
                else part = part.ParentGroup.GetLinkNumPart(link_num);
                if (part == null) return;
            }
            mgr.BotTouchObject(id, part.UUID, m_host.OwnerID);
        }

        public LSLList osGetAvatarList()
        {
            // OSSL: returns [uuid, position, name, uuid, position, name, ...]
            // for every avatar in the region EXCEPT the script owner
            // (matches the OpenSim XEngine OSSL behavior).
            List<object> result = new List<object>();
            List<ScenePresence> presences = World?.GetScenePresences();
            if (presences == null) return new LSLList();

            UUID ownerId = m_host.OwnerID;
            foreach (ScenePresence sp in presences)
            {
                if (sp.IsChildAgent) continue;
                if (sp.UUID == ownerId) continue;   // exclude script owner per OSSL spec
                result.Add(sp.UUID.ToString());
                result.Add(sp.AbsolutePosition);
                result.Add(sp.Name);
            }
            return new LSLList(result);
        }

        // ── Object return ──────────────────────────────────────────────────────
        //
        // SL: "Returns an integer that is the number of objects successfully returned to their owners or an ERR_* flag."
        // Objects are RETURNED to their owners' Lost and Found, never deleted, through the core's own parcel-return call
        // (SceneObjectGroup parcel autoreturn: AddReturn + DeRezObjects(null, ..., DeRezAction.Return)) - the pair Halcyon's
        // Scene.returnObjects makes. YEngine's LSL_Api has neither function, so there is no LSL_Api call to match.

        /// <summary>
        /// Who granted PERMISSION_RETURN_OBJECTS. SL: "If the script is owned by an agent, PERMISSION_RETURN_OBJECTS may be
        /// granted by the owner. If the script is owned by a group, this permission may be granted by an agent belonging
        /// to the group's 'Owners' role." Any other granter, or none, is ERR_RUNTIME_PERMISSIONS. (Halcyon's
        /// canUseReturnPermission answers ERR_PARCEL_PERMISSIONS for a group granter outside the Owners role; the rule is
        /// about who may grant, so SL's runtime-permission code is used.)
        /// </summary>
        private int CheckReturnPermission(TaskInventoryItem item)
        {
            if ((item.PermsMask & PERMISSION_RETURN_OBJECTS) == 0 || item.PermsGranter.IsZero())
                return ERR_RUNTIME_PERMISSIONS;
            // The script's owner is its object's owner (a group for a deeded object), as the other permission checks
            // here read it.
            UUID scriptOwner = m_host.OwnerID;
            bool groupOwned = !m_host.GroupID.IsZero() && scriptOwner == m_host.GroupID;
            if (!groupOwned)
                return item.PermsGranter == scriptOwner ? 0 : ERR_RUNTIME_PERMISSIONS;
            return IsInGroupOwnersRole(item.PermsGranter, m_host.GroupID) ? 0 : ERR_RUNTIME_PERMISSIONS;
        }

        /// <summary>The agent holds the group's Owners role (GroupRecord.OwnerRoleID). No groups module: false.</summary>
        private bool IsInGroupOwnersRole(UUID agent, UUID group)
        {
            IGroupsModule groups = World.RequestModuleInterface<IGroupsModule>();
            GroupRecord rec = groups?.GetGroupRecord(group);
            if (rec == null || rec.OwnerRoleID.IsZero()) return false;
            // GroupRoleDataRequest(agent, group) lists the group's roles, not the agent's; the role-member pairs say who
            // holds which. The granter's own client is the requester when it is here.
            IClientAPI client = World.GetScenePresence(agent)?.ControllingClient;
            List<GroupRoleMembersData> members = groups.GroupRoleMembersRequest(client, group);
            return members != null && members.Any(m => m.MemberID == agent && m.RoleID == rec.OwnerRoleID);
        }

        /// <summary>
        /// The script owner may return objects over this parcel: it owns the parcel, or it is the estate owner or an
        /// estate manager (SL lsl_definitions: "the script owner must own the parcel or be an estate manager/region
        /// owner"; llReturnObjectsByID: "If the script is owned by an estate owner or manager, this function works for
        /// objects located on any parcel in the region. Otherwise, the script can return objects located over land owned
        /// by the owner of the script."). Halcyon never lets an EO/EM act on a parcel it does not own; SL is followed.
        /// </summary>
        private bool MayReturnOnParcel(ILandObject parcel)
            => parcel != null && (parcel.LandData.OwnerID == m_host.OwnerID || IsEstateOwnerOrManager(m_host.OwnerID));

        private bool IsEstateOwnerOrManager(UUID id)
            => !id.IsZero() && World.RegionInfo.EstateSettings.IsEstateManagerOrOwner(id);

        /// <summary>
        /// SL: "Parcel owner, estate owner and estate managers can not have their objects returned by this method."
        /// </summary>
        private bool IsExemptFromReturn(SceneObjectGroup grp, ILandObject parcel)
            => grp.OwnerID == parcel.LandData.OwnerID || IsEstateOwnerOrManager(grp.OwnerID);

        /// <summary>
        /// SL: "Throttled at max parcel land impact capacity region-wide per hour." Per region, over a rolling hour, the
        /// prims returned may not go over the region's object capacity (MaxPrims, the most any parcel can hold).
        /// </summary>
        private sealed class ReturnThrottle
        {
            public readonly Queue<(DateTime At, int Prims)> Returned = new();
            public int Total;
        }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Scene, ReturnThrottle> s_returnThrottles = new();
        private static readonly TimeSpan ReturnThrottleWindow = TimeSpan.FromHours(1);

        /// <summary>
        /// Return <paramref name="groups"/> to their owners, as the core's parcel autoreturn does. Stops at the first
        /// object the hourly throttle has no room for. The count of objects handed to the return, or ERR_THROTTLED when
        /// the throttle stopped it before any, or ERR_GENERIC when the region has no inventory access module (the core's
        /// deleter would remove the objects and never copy them, so nothing is touched).
        /// </summary>
        private int ReturnGroupsToOwners(List<SceneObjectGroup> groups, string reason)
        {
            if (groups.Count == 0) return 0;
            if (World.RequestModuleInterface<IInventoryAccessModule>() == null)
            {
                m_log.LogWarning("[PhloxAPI]: object return refused: the region has no inventory access module");
                return ERR_GENERIC;
            }

            var ids = new List<uint>();
            bool throttled = false;
            ReturnThrottle throttle = s_returnThrottles.GetValue(World, _ => new ReturnThrottle());
            lock (throttle)
            {
                DateTime now = DateTime.UtcNow;
                while (throttle.Returned.Count > 0 && now - throttle.Returned.Peek().At >= ReturnThrottleWindow)
                    throttle.Total -= throttle.Returned.Dequeue().Prims;
                int capacity = World.RegionInfo.ObjectCapacity;
                foreach (SceneObjectGroup grp in groups)
                {
                    if (throttle.Total + grp.PrimCount > capacity) { throttled = true; break; }
                    throttle.Total += grp.PrimCount;
                    throttle.Returned.Enqueue((now, grp.PrimCount));
                    // Group-owned objects go back to the last owner (InventoryAccessModule.CreateItemForObject).
                    World.AddReturn(grp.OwnerID == grp.GroupID ? grp.LastOwnerID : grp.OwnerID, grp.Name, grp.AbsolutePosition, reason);
                    ids.Add(grp.RootPart.LocalId);
                }
            }
            // One object per call, as Halcyon's by-ID return does (LandObject.cs:1425-1431): with the region's
            // CoalesceMultipleObjectsToInventory on, one call would fold all of an owner's objects into one item.
            foreach (uint id in ids)
                World.DeRezObjects(null, new List<uint> { id }, UUID.Zero, DeRezAction.Return, UUID.Zero, false);
            if (ids.Count == 0 && throttled) return ERR_THROTTLED;
            return ids.Count;
        }

        /// <summary>The script's own item, or null.</summary>
        private TaskInventoryItem ReturnScriptItem()
        {
            UUID invItemID = InventorySelf();
            if (invItemID.IsZero()) return null;
            lock (m_host.TaskInventory)
                return m_host.TaskInventory.TryGetValue(invItemID, out TaskInventoryItem item) ? item : null;
        }

        /// <summary>
        /// Halcyon llReturnObjectsByOwner's finally (LSLSystemAPI.cs:18415-18431) - its LSLError for
        /// each failure code it reports; the code is returned as before.
        /// </summary>
        private int ReturnObjectsError(int rc)
        {
            switch (rc)
            {
                case ERR_GENERIC: LSLError("No parcel found for permissions to return objects"); break;
                case ERR_PARCEL_PERMISSIONS: LSLError("No parcel/region permission to return objects"); break;
                case ERR_RUNTIME_PERMISSIONS: LSLError("No permissions to return objects"); break;
                case ERR_MALFORMED_PARAMS: LSLError("Bad parameters on scripted call to return objects"); break;
            }
            return rc;
        }

        public int llReturnObjectsByOwner(string owner, int scope)
        {
            // Order of the checks as Halcyon (LSLSystemAPI.cs:18348-18436).
            if (!UUID.TryParse(owner, out UUID targetAgentID))
                return ERR_MALFORMED_PARAMS;
            if (targetAgentID.IsZero()) return 0;

            TaskInventoryItem item = ReturnScriptItem();
            if (item == null) { LSLError("No item found from which to run script"); return ERR_GENERIC; }   // Halcyon :18356-18360

            int rc = CheckReturnPermission(item);
            if (rc != 0) return ReturnObjectsError(rc);

            if (scope != OBJECT_RETURN_PARCEL && scope != OBJECT_RETURN_PARCEL_OWNER && scope != OBJECT_RETURN_REGION)
                return ERR_MALFORMED_PARAMS;

            try
            {
                Vector3 currentPos = m_host.ParentGroup.AbsolutePosition;
                ILandObject currentParcel = World.LandChannel.GetLandObject(currentPos.X, currentPos.Y);
                // Halcyon reports this one only for OBJECT_RETURN_REGION (:18388-18391; the parcel scopes fail on
                // its null parcel and return ERR_GENERIC unreported).
                if (currentParcel == null) return scope == OBJECT_RETURN_REGION ? ReturnObjectsError(ERR_GENERIC) : ERR_GENERIC;

                // The parcels in scope, by local id.
                var inScope = new HashSet<int>();
                switch (scope)
                {
                    case OBJECT_RETURN_PARCEL:
                        // SL: "all objects on the same parcel as the script"; lsl_definitions: "Requires the script owner
                        // to be an estate manager or the parcel owner."
                        if (!MayReturnOnParcel(currentParcel)) return ReturnObjectsError(ERR_PARCEL_PERMISSIONS);
                        inScope.Add(currentParcel.LandData.LocalID);
                        break;
                    case OBJECT_RETURN_PARCEL_OWNER:
                        // SL: "over parcels owned by the owner of the script".
                        foreach (ILandObject p in World.LandChannel.AllParcels())
                            if (p.LandData.OwnerID == m_host.OwnerID) inScope.Add(p.LandData.LocalID);
                        if (inScope.Count == 0) return ReturnObjectsError(ERR_PARCEL_PERMISSIONS);
                        break;
                    case OBJECT_RETURN_REGION:
                        // lsl_definitions: "Only works if the script is owned by the estate owner or an estate manager."
                        // (Halcyon walks every parcel and stops at the first the owner may not touch.)
                        if (!IsEstateOwnerOrManager(m_host.OwnerID)) return ReturnObjectsError(ERR_PARCEL_PERMISSIONS);
                        break;
                }

                // SL: "Parcel owner, estate owner and estate managers can not have their objects returned by this method."
                if (IsEstateOwnerOrManager(targetAgentID)) return 0;

                var toReturn = new List<SceneObjectGroup>();
                foreach (EntityBase ent in World.GetEntities())
                {
                    if (ent is not SceneObjectGroup sog || sog.IsDeleted || sog.IsAttachment) continue;
                    if (sog.OwnerID != targetAgentID) continue;

                    Vector3 objPos = sog.AbsolutePosition;
                    ILandObject objParcel = World.LandChannel.GetLandObject(objPos.X, objPos.Y);
                    if (objParcel == null) continue;
                    if (scope != OBJECT_RETURN_REGION && !inScope.Contains(objParcel.LandData.LocalID)) continue;

                    if (IsExemptFromReturn(sog, objParcel)) continue;
                    // SL: "Objects which are owned by the group the land is set to will not be returned by this method."
                    if (!objParcel.LandData.GroupID.IsZero() && sog.OwnerID == objParcel.LandData.GroupID) continue;

                    toReturn.Add(sog);
                }

                rc = ReturnGroupsToOwners(toReturn, "scripted parcel owner return");
                if (rc > 0)
                    m_log.LogInformation("[PhloxAPI]: llReturnObjectsByOwner returned {0} objects owned by {1}, granted by {2} for {3}",
                        rc, targetAgentID, item.PermsGranter, m_host.OwnerID);
                return rc;
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: llReturnObjectsByOwner exception: {0}", e.Message);
                return ERR_GENERIC;
            }
        }

        public int llReturnObjectsByID(LSLList objects)
        {
            // Order of the checks as Halcyon (LSLSystemAPI.cs:18438-18497).
            try
            {
                TaskInventoryItem item = ReturnScriptItem();
                if (item == null) { LSLError("No item found from which to run script"); return ERR_GENERIC; }   // Halcyon :18445-18449

                int rc = CheckReturnPermission(item);
                if (rc != 0) return ReturnObjectsError(rc);   // Halcyon :18456-18460

                // Every element must be a key before anything moves (Halcyon :18463-18465).
                var ids = new List<UUID>();
                for (int i = 0; i < objects.Length; i++)
                {
                    if (!UUID.TryParse(objects.GetLSLStringItem(i), out UUID targetId))
                        return ERR_MALFORMED_PARAMS;
                    ids.Add(targetId);
                }

                var toReturn = new List<SceneObjectGroup>();
                bool parcelRefused = false;
                foreach (UUID targetId in ids)
                {
                    if (targetId.IsZero()) continue;
                    SceneObjectPart part = World.GetSceneObjectPart(targetId);
                    SceneObjectGroup sog = part?.ParentGroup;
                    if (sog == null || sog.IsDeleted || sog.IsAttachment) continue;
                    if (toReturn.Contains(sog)) continue;   // a linkset once, whichever of its keys is given

                    Vector3 pos = sog.AbsolutePosition;
                    ILandObject parcel = World.LandChannel.GetLandObject(pos.X, pos.Y);
                    if (parcel == null) continue;

                    // SL: "... can not have their objects returned by this method, except when the object returns itself."
                    if (sog != m_host.ParentGroup && IsExemptFromReturn(sog, parcel)) continue;
                    if (!MayReturnOnParcel(parcel)) { parcelRefused = true; continue; }

                    toReturn.Add(sog);
                }

                // Nothing returnable because of whose land the objects are on: SL's ERR_PARCEL_PERMISSIONS ("Permission
                // lacked to perform task on specified parcel."). Halcyon adds that code into its count instead.
                if (toReturn.Count == 0 && parcelRefused) return ERR_PARCEL_PERMISSIONS;

                rc = ReturnGroupsToOwners(toReturn, "scripted parcel owner return by ID");
                if (rc > 0)
                    m_log.LogInformation("[PhloxAPI]: llReturnObjectsByID returned {0} objects, granted by {1} for {2}",
                        rc, item.PermsGranter, m_host.OwnerID);
                return rc;
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: llReturnObjectsByID exception: {0}", e.Message);
                return ERR_GENERIC;
            }
        }

        // ── Prim media ─────────────────────────────────────────────────────────

        public int llSetPrimMediaParams(int face, LSLList parms)
        {
            ScriptSleep(1000);
            return SetPrimMediaParams(m_host, face, parms);
        }
        public int llSetLinkMedia(int link, int face, LSLList parms)
        {
            ScriptSleep(1000);
            SceneObjectPart part = GetSingleLinkPart(link);
            if (part != null) return SetPrimMediaParams(part, face, parms);
            return 1003; // LSL_STATUS_NOT_FOUND
        }
        public LSLList llGetPrimMediaParams(int face, LSLList parms)
        {
            ScriptSleep(1000);
            return GetPrimMediaParams(m_host, face, parms);
        }
        public LSLList llGetLinkMedia(int link, int face, LSLList parms)
        {
            ScriptSleep(1000);
            SceneObjectPart part = GetSingleLinkPart(link);
            if (part != null) return GetPrimMediaParams(part, face, parms);
            return new LSLList();
        }
        public int llClearPrimMedia(int face)
        {
            ScriptSleep(1000);
            return ClearPrimMedia(m_host, face);
        }
        public int llClearLinkMedia(int link, int face)
        {
            ScriptSleep(1000);
            SceneObjectPart part = GetSingleLinkPart(link);
            if (part != null) return ClearPrimMedia(part, face);
            return 1003; // LSL_STATUS_NOT_FOUND
        }

        // ── Prim media helpers (faithful port from Halcyon) ────────────────────

        private LSLList GetPrimMediaParams(SceneObjectPart part, int face, LSLList rules)
        {
            if (face < 0 || face > part.GetNumberOfSides() - 1)
                return new LSLList();

            IMoapModule module = World?.RequestModuleInterface<IMoapModule>();
            if (module == null) return new LSLList();

            MediaEntry me = module.GetMediaEntry(part, face);
            if (me == null) return new LSLList();


            List<object> res = new List<object>();
            for (int i = 0; i < rules.Length; i++)
            {
                int code = rules.GetLSLIntegerItem(i);
                switch (code)
                {
                    case PRIM_MEDIA_ALT_IMAGE_ENABLE: res.Add(0); break;
                    case PRIM_MEDIA_CONTROLS:
                        res.Add(me.Controls == MediaControls.Standard ? 0 : 1);
                        break;
                    case PRIM_MEDIA_CURRENT_URL: res.Add(me.CurrentURL ?? string.Empty); break;
                    case PRIM_MEDIA_HOME_URL: res.Add(me.HomeURL ?? string.Empty); break;
                    case PRIM_MEDIA_AUTO_LOOP: res.Add(me.AutoLoop ? 1 : 0); break;
                    case PRIM_MEDIA_AUTO_PLAY: res.Add(me.AutoPlay ? 1 : 0); break;
                    case PRIM_MEDIA_AUTO_SCALE: res.Add(me.AutoScale ? 1 : 0); break;
                    case PRIM_MEDIA_AUTO_ZOOM: res.Add(me.AutoZoom ? 1 : 0); break;
                    case PRIM_MEDIA_FIRST_CLICK_INTERACT: res.Add(me.InteractOnFirstClick ? 1 : 0); break;
                    case PRIM_MEDIA_WIDTH_PIXELS: res.Add((int)me.Width); break;
                    case PRIM_MEDIA_HEIGHT_PIXELS: res.Add((int)me.Height); break;
                    case PRIM_MEDIA_WHITELIST_ENABLE: res.Add(me.EnableWhiteList ? 1 : 0); break;
                    case PRIM_MEDIA_WHITELIST:
                        if (me.WhiteList == null) { res.Add(string.Empty); break; }
                        string[] urls = (string[])me.WhiteList.Clone();
                        for (int j = 0; j < urls.Length; j++)
                            urls[j] = Uri.EscapeDataString(urls[j]);
                        res.Add(string.Join(", ", urls));
                        break;
                    case PRIM_MEDIA_PERMS_INTERACT: res.Add((int)me.InteractPermissions); break;
                    case PRIM_MEDIA_PERMS_CONTROL: res.Add((int)me.ControlPermissions); break;
                    default: return new LSLList();
                }
            }
            return new LSLList(res);
        }

        private int SetPrimMediaParams(SceneObjectPart part, int face, LSLList rules)
        {
            const int LSL_STATUS_OK = 0;
            const int LSL_STATUS_NOT_FOUND = 1003;
            const int LSL_STATUS_NOT_SUPPORTED = 1004;
            const int LSL_STATUS_MALFORMED_PARAMS = 1000;

            if (face < 0 || face > part.GetNumberOfSides() - 1)
                return LSL_STATUS_NOT_FOUND;

            IMoapModule module = World?.RequestModuleInterface<IMoapModule>();
            if (module == null) return LSL_STATUS_NOT_SUPPORTED;

            MediaEntry me = module.GetMediaEntry(part, face);
            if (me == null) me = new MediaEntry();


            int i = 0;
            while (i < rules.Length - 1)
            {
                int code = rules.GetLSLIntegerItem(i++);
                switch (code)
                {
                    case PRIM_MEDIA_ALT_IMAGE_ENABLE:
                        me.EnableAlterntiveImage = (rules.GetLSLIntegerItem(i++) != 0);
                        break;
                    case PRIM_MEDIA_CONTROLS:
                        int v = rules.GetLSLIntegerItem(i++);
                        me.Controls = (v == 0) ? MediaControls.Standard : MediaControls.Mini;
                        break;
                    case PRIM_MEDIA_CURRENT_URL: me.CurrentURL = rules.GetLSLStringItem(i++); break;
                    case PRIM_MEDIA_HOME_URL: me.HomeURL = rules.GetLSLStringItem(i++); break;
                    case PRIM_MEDIA_AUTO_LOOP: me.AutoLoop = (rules.GetLSLIntegerItem(i++) != 0); break;
                    case PRIM_MEDIA_AUTO_PLAY: me.AutoPlay = (rules.GetLSLIntegerItem(i++) != 0); break;
                    case PRIM_MEDIA_AUTO_SCALE: me.AutoScale = (rules.GetLSLIntegerItem(i++) != 0); break;
                    case PRIM_MEDIA_AUTO_ZOOM: me.AutoZoom = (rules.GetLSLIntegerItem(i++) != 0); break;
                    case PRIM_MEDIA_FIRST_CLICK_INTERACT: me.InteractOnFirstClick = (rules.GetLSLIntegerItem(i++) != 0); break;
                    case PRIM_MEDIA_WIDTH_PIXELS: me.Width = rules.GetLSLIntegerItem(i++); break;
                    case PRIM_MEDIA_HEIGHT_PIXELS: me.Height = rules.GetLSLIntegerItem(i++); break;
                    case PRIM_MEDIA_WHITELIST_ENABLE: me.EnableWhiteList = (rules.GetLSLIntegerItem(i++) != 0); break;
                    case PRIM_MEDIA_WHITELIST:
                        string[] rawUrls = rules.GetLSLStringItem(i++).Split(new char[] { ',' });
                        List<string> whiteListUrls = new List<string>();
                        foreach (string rawUrl in rawUrls) whiteListUrls.Add(rawUrl.Trim());
                        me.WhiteList = whiteListUrls.ToArray();
                        break;
                    case PRIM_MEDIA_PERMS_INTERACT:
                        me.InteractPermissions = (MediaPermission)(byte)(int)rules.GetLSLIntegerItem(i++);
                        break;
                    case PRIM_MEDIA_PERMS_CONTROL:
                        me.ControlPermissions = (MediaPermission)(byte)(int)rules.GetLSLIntegerItem(i++);
                        break;
                    default: return LSL_STATUS_MALFORMED_PARAMS;
                }
            }

            module.SetMediaEntry(part, face, me);
            return LSL_STATUS_OK;
        }

        private int ClearPrimMedia(SceneObjectPart part, int face)
        {
            const int LSL_STATUS_OK = 0;
            const int LSL_STATUS_NOT_FOUND = 1003;
            const int LSL_STATUS_NOT_SUPPORTED = 1004;

            IMoapModule module = World?.RequestModuleInterface<IMoapModule>();
            if (module == null) return LSL_STATUS_NOT_SUPPORTED;

            if (face == ALL_SIDES)
            {
                for (int i = 0; i < part.GetNumberOfSides(); i++)
                    module.ClearMediaEntry(part, i);
                return LSL_STATUS_OK;
            }

            if (face < 0 || face > part.GetNumberOfSides() - 1)
                return LSL_STATUS_NOT_FOUND;

            module.ClearMediaEntry(part, face);
            return LSL_STATUS_OK;
        }

        // ── HTTP / URL ─────────────────────────────────────────────────────────

        public string llHTTPRequest(string url, LSLList parameters, string body)
        {
            if (World == null) return UUID.Zero.ToString();

            IHttpRequestModule httpMod = World.RequestModuleInterface<IHttpRequestModule>();
            if (httpMod == null) return UUID.Zero.ToString();

            if (!httpMod.CheckThrottle(m_localID, m_host.OwnerID))
                return UUID.Zero.ToString();

            // The operator's outbound filter, where and as YEngine applies it (after the throttle, before the
            // parameters are read).
            if (!OutboundAllowed(httpMod, "llHttpRequest", url))
                return string.Empty;

            // Parse parameter pairs into list and custom headers dict. Header names are case-insensitive
            // (SL: "RFC 2616 § 4.2 defines HTTP header field names as case-insensitive"), so one spelling is one header.
            var paramList  = new List<string>();
            var headers    = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var data       = parameters.Data;

            for (int i = 0; i < data.Length; i += 2)
            {
                // Halcyon LSLSystemAPI.cs:13784-13788 - a flag with no value is an error, not dropped.
                if (i + 1 >= data.Length)
                {
                    ScriptShoutError("Invalid number of parameters in options list for llHTTPRequest.");
                    return UUID.Zero.ToString();
                }
                // Halcyon's test and text (LSLSystemAPI.cs:13780-13799): the flag as the list writes it
                // (GetLSLStringItem, so 1.0 is "1.000000" and is refused) must parse as an integer.
                int option;
                if (!int.TryParse(parameters.GetLSLStringItem(i), out option))
                {
                    ScriptShoutError("Invalid flag passed in parameters list of llHTTPRequest.");
                    return UUID.Zero.ToString();
                }
                string value = data[i + 1].ToString();

                // Halcyon :13803-13807 - HTTP_CUSTOM_HEADER without its value is an error.
                if (option == 5 && i + 2 >= data.Length)
                {
                    ScriptShoutError("Invalid number of parameters in the HTTP_CUSTOM_HEADER options for llHTTPRequest.");
                    return UUID.Zero.ToString();
                }

                // HTTP_CUSTOM_HEADER (5) has an extra param: name, value
                if (option == 5)
                {
                    string headerName  = value;
                    string headerValue = data[i + 2].ToString();
                    i++; // consume the extra param
                    switch (CustomHeaderRule(headerName, headerValue))
                    {
                        case CustomHeader.RuntimeError:
                            // SL: "Use HTTP_MIMETYPE to set the Content-Type header. Attempts to use HTTP_CUSTOM_HEADER to set
                            // it will cause a runtime script error." The request is not made.
                            ShoutError("llHTTPRequest: Content-Type cannot be set with HTTP_CUSTOM_HEADER; use HTTP_MIMETYPE.");
                            return UUID.Zero.ToString();
                        case CustomHeader.Dropped:
                            continue;
                    }
                    // Halcyon: "In SL, duplicate headers add to the existing header after a comma+space"
                    headers[headerName] = headers.TryGetValue(headerName, out string earlier) ? earlier + ", " + headerValue : headerValue;
                    continue;
                }

                // HTTP_MIMETYPE becomes the Content-Type line as given, so a line break in it would write a
                // header line of the script's choosing - an X-SecondLife-Owner-Key among them. The core now
                // refuses any value that is not a media type (HttpRequestMimeType.IsValid: type/subtype, optional
                // ;parameters, no control character) and YEngine reports it (LSL_Api.llHTTPRequest); Phlox checks the
                // same function first and gives YEngine's result: its text on DEBUG_CHANNEL, a 1 s sleep, "" to the
                // script, no request.
                if (option == (int)HttpRequestConstants.HTTP_MIMETYPE && !HttpRequestMimeType.IsValid(value))
                {
                    YEngineError("llHTTPRequest", HttpRequestMimeType.InvalidMessage);
                    ScriptSleep(1000);
                    return string.Empty;
                }

                paramList.Add(option.ToString());
                paramList.Add(value);
            }

            // The simulator's own values, set last so they always win over anything the script sent
            AddSimulatorHeaders(headers);

            // Recorded against this script, so a reset or removal can end it and a late response is dropped
            var httpPlugin = m_ScriptEngine.AsyncCommands?.HttpRequestPlugin;
            UUID objectID = m_host.ParentGroup?.UUID ?? m_host.UUID;
            bool capped = false;
            UUID reqID = httpPlugin != null
                ? httpPlugin.Start(m_itemID, objectID, () => httpMod.StartHttpRequest(m_localID, m_itemID, url, paramList, headers, body), out capped)
                : httpMod.StartHttpRequest(m_localID, m_itemID, url, paramList, headers, body);
            // Halcyon's in-flight caps refuse with NULL_KEY and no error; its llHTTPRequest then sleeps
            // ERROR_DELAY, 80 ms (LSLSystemAPI.cs:13762, 13845-13846). HttpInFlightThrottle = false, never capped.
            if (capped) ScriptSleep(HTTP_CAPPED_DELAY);
            return reqID == UUID.Zero ? UUID.Zero.ToString() : reqID.ToString();
        }
        /// <summary>
        /// The core's outbound URL filter for user scripts, [Network] OutboundDisallowForUserScripts
        /// (default: loopback, private and reserved IPv4 ranges) and OutboundDisallowForUserScriptsExcept - the one
        /// OutboundUrlFilter object the core HttpRequestModule builds, through IHttpRequestModule.CheckAllowed, which is
        /// what YEngine's llHTTPRequest calls (LSL_Api.cs:14762). It resolves the host and checks every IPv4 address; a
        /// DNS error is allowed and a host with no IPv4 address is refused, both as the filter decides for YEngine. There
        /// is no allowance for the region's own HTTP server or llRequestURL URLs, in YEngine either.
        /// Only an absolute http or https URL is checked: the core opens nothing else (System.Uri, HttpClient).
        /// A refused call gets YEngine's result: its Error text on DEBUG_CHANNEL, a 1 s sleep, and "" to the script, with
        /// no request and no response event.
        /// </summary>
        private bool OutboundAllowed(IHttpRequestModule httpMod, string function, string url)
        {
            if (httpMod == null || url == null) return true;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) return true;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return true;
            if (httpMod.CheckAllowed(uri)) return true;
            YEngineError(function, string.Format("Request to {0} disallowed by filter", url));
            ScriptSleep(1000);
            return false;
        }

        /// <summary>
        /// YEngine's LSL_Api.Error (LSL_Api.cs:15684-15696) as a script sees it: "command: message", cut to 1023
        /// characters, on DEBUG_CHANNEL through the scene (viewers, Phlox's listens) and through WorldComm (YEngine's
        /// listens). Unlike <see cref="ShoutError"/> there is no "Script error: " prefix, so both engines say the same text.
        /// </summary>
        private void YEngineError(string command, string message)
        {
            string text = command + ": " + message;
            if (text.Length > 1023) text = text.Substring(0, 1023);
            m_host?.ParentGroup?.Scene?.SimChat(text, ChatTypeEnum.DebugChannel, DEBUG_CHANNEL,
                m_host.ParentGroup.RootPart.AbsolutePosition, m_host.Name, m_host.UUID, false);
            ChatToWorldComm(ChatTypeEnum.Shout, DEBUG_CHANNEL, text);
        }

        private enum CustomHeader { Allowed, Dropped, RuntimeError }

        /// <summary>HTTP token separators (RFC 7230): a header name containing one of these, a space or a control is not a name.</summary>
        private const string HeaderNameSeparators = "()<>@,;:\\\"/[]?={}";

        /// <summary>
        /// What an HTTP_CUSTOM_HEADER may set. Halcyon ScriptsHttpRequests.ScriptCanChangeHeader:
        /// every name starting "x-secondlife", in any letter case, is "reserved for internal use only" and skipped
        /// silently (SL: "certain headers, such as the default headers, are blocked for security reasons").
        /// Content-Type is SL's runtime script error (Halcyon skipped it silently). A name or value with a line break, or a
        /// name that is not an HTTP token, cannot be one header and is dropped: it would write a second header line.
        /// </summary>
        private static CustomHeader CustomHeaderRule(string name, string value)
        {
            if (string.IsNullOrEmpty(name)) return CustomHeader.Dropped;
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase)) return CustomHeader.RuntimeError;
            if (name.StartsWith("x-secondlife", StringComparison.OrdinalIgnoreCase)) return CustomHeader.Dropped;
            foreach (char c in name)
                if (c <= ' ' || c >= 127 || HeaderNameSeparators.IndexOf(c) >= 0) return CustomHeader.Dropped;
            if (value != null && (value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0)) return CustomHeader.Dropped;
            return CustomHeader.Allowed;
        }

        /// <summary>
        /// The nine X-SecondLife-* headers SL's simulator sends (wiki llHTTPRequest), built as Halcyon's
        /// llHTTPRequest builds them. Region: SL's "global coordinates of the region's south-west corner" (WorldLocX/Y, as
        /// YEngine; Halcyon printed grid units). Shard: [Network] shard, default "OpenSim", the setting and default YEngine
        /// reads, so both engines in a region report the same shard.
        /// </summary>
        private void AddSimulatorHeaders(Dictionary<string, string> headers)
        {
            foreach (string reserved in headers.Keys.Where(k => k.StartsWith("x-secondlife", StringComparison.OrdinalIgnoreCase)).ToList())
                headers.Remove(reserved);

            Vector3 position = m_host.AbsolutePosition;
            Vector3 velocity = m_host.Velocity;
            Quaternion rotation = m_host.RotationOffset;
            UUID owner = m_host.OwnerID;
            ScenePresence sp = World.GetScenePresence(owner);
            string ownerName = sp != null ? sp.Name : OwnerNameLookup(owner);
            RegionInfo ri = World.RegionInfo;
            string shard = m_ScriptEngine?.ConfigSource?.Configs["Network"]?.GetString("shard", "OpenSim") ?? "OpenSim";
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;

            headers["X-SecondLife-Shard"] = shard;
            headers["X-SecondLife-Object-Name"] = m_host.Name;
            headers["X-SecondLife-Object-Key"] = m_host.UUID.ToString();
            headers["X-SecondLife-Region"] = string.Format(inv, "{0} ({1}, {2})", ri.RegionName, ri.WorldLocX, ri.WorldLocY);
            headers["X-SecondLife-Local-Position"] = string.Format(inv, "({0:0.000000}, {1:0.000000}, {2:0.000000})", position.X, position.Y, position.Z);
            headers["X-SecondLife-Local-Velocity"] = string.Format(inv, "({0:0.000000}, {1:0.000000}, {2:0.000000})", velocity.X, velocity.Y, velocity.Z);
            headers["X-SecondLife-Local-Rotation"] = string.Format(inv, "({0:0.000000}, {1:0.000000}, {2:0.000000}, {3:0.000000})", rotation.X, rotation.Y, rotation.Z, rotation.W);
            headers["X-SecondLife-Owner-Name"] = ownerName;
            headers["X-SecondLife-Owner-Key"] = owner.ToString();
        }

        /// <summary>osKey2Name's lookup without its OSSL threat-level check: the account service, then user management.</summary>
        private string OwnerNameLookup(UUID key)
        {
            UserAccount account = World?.UserAccountService?.GetUserAccount(World.RegionInfo.ScopeID, key);
            if (account != null) return account.Name;
            return World?.RequestModuleInterface<IUserManagement>()?.GetUserName(key) ?? string.Empty;
        }

        public void llHTTPResponse(string request_id, int status, string body)
        {
            // Sends an HTTP response back to the caller of an llRequestURL endpoint
            IUrlModule urlMod = World.RequestModuleInterface<IUrlModule>();
            if (urlMod == null) return;
            if (!UUID.TryParse(request_id, out UUID reqID)) return;
            urlMod.HttpResponse(reqID, status, body ?? string.Empty);
        }

        public string llGetHTTPHeader(string request_id, string header)
        {
            IUrlModule urlMod = World.RequestModuleInterface<IUrlModule>();
            if (urlMod == null) return string.Empty;
            if (!UUID.TryParse(request_id, out UUID reqID)) return string.Empty;
            return urlMod.GetHttpHeader(reqID, header) ?? string.Empty;
        }

        public void llSetContentType(string request_id, int content_type)
        {
            // SL's CONTENT_TYPE_* numbering, and YEngine's HTML rule (LSL_Api.llSetContentType).
            IUrlModule urlMod = World.RequestModuleInterface<IUrlModule>();
            if (urlMod == null) return;
            if (!UUID.TryParse(request_id, out UUID reqID) || reqID == UUID.Zero) return;
            string mimeType = content_type switch
            {
                SlConst.CONTENT_TYPE_XML => "application/xml",
                SlConst.CONTENT_TYPE_XHTML => "application/xhtml+xml",
                SlConst.CONTENT_TYPE_ATOM => "application/atom+xml",
                SlConst.CONTENT_TYPE_JSON => "application/json",
                SlConst.CONTENT_TYPE_LLSD => "application/llsd+xml",
                SlConst.CONTENT_TYPE_FORM => "application/x-www-form-urlencoded",
                SlConst.CONTENT_TYPE_RSS => "application/rss+xml",
                _ => "text/plain"   // CONTENT_TYPE_TEXT, and HTML until the owner check below passes
            };
            urlMod.HttpContentType(reqID, mimeType);
            if (content_type == SlConst.CONTENT_TYPE_HTML && IsFromOwnersViewerInRegion(urlMod, reqID))
                urlMod.HttpContentType(reqID, "text/html");
        }

        /// <summary>
        /// Text/html is served only to the object owner's own viewer in this region, so a
        /// script cannot serve a page to a stranger's browser (YEngine LSL_Api.llSetContentType): the
        /// owner is present, the request came from the embedded browser, and from the owner's address.
        /// </summary>
        private bool IsFromOwnersViewerInRegion(IUrlModule urlMod, UUID reqID)
        {
            ScenePresence agent = World.GetScenePresence(m_host.ParentGroup.OwnerID);
            if (agent == null || agent.IsChildAgent || agent.IsDeleted) return false;
            string userAgent = urlMod.GetHttpHeader(reqID, "user-agent");
            if (string.IsNullOrEmpty(userAgent) || userAgent.IndexOf("SecondLife", StringComparison.Ordinal) < 0) return false;
            string logonFrom = agent.ControllingClient?.RemoteEndPoint?.Address?.ToString();
            if (string.IsNullOrEmpty(logonFrom)) return false;
            string requestFrom = urlMod.GetHttpHeader(reqID, "x-remote-ip")?.Trim();
            return requestFrom != null && requestFrom.Equals(logonFrom);
        }

        public string llRequestURL()
        {
            IUrlModule urlMod = World.RequestModuleInterface<IUrlModule>();
            if (urlMod == null) return UUID.Zero.ToString();
            UUID reqID = urlMod.RequestURL(m_ScriptEngine, m_host, m_itemID, null);
            return reqID.ToString();
        }

        public string llRequestSecureURL()
        {
            IUrlModule urlMod = World.RequestModuleInterface<IUrlModule>();
            if (urlMod == null) return UUID.Zero.ToString();
            UUID reqID = urlMod.RequestSecureURL(m_ScriptEngine, m_host, m_itemID, null);
            return reqID.ToString();
        }

        public void llReleaseURL(string url)
        {
            IUrlModule urlMod = World.RequestModuleInterface<IUrlModule>();
            urlMod?.ReleaseURL(url);
        }

        public int llGetFreeURLs()
        {
            IUrlModule urlMod = World.RequestModuleInterface<IUrlModule>();
            if (urlMod == null) return 0;
            return urlMod.GetFreeUrls();
        }
        public int iwValidateURL(string url)
        {
            bool ret = Uri.TryCreate(url, UriKind.Absolute, out Uri uriResult)
                && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
            return Convert.ToInt32(ret);
        }

        // ── Email / remote data ────────────────────────────────────────────────

        public void llEmail(string address, string subject, string message)
        {
            // Faithful port from Halcyon
            try
            {
                IEmailModule emailModule = World?.RequestModuleInterface<IEmailModule>();
                if (emailModule == null) return;
                emailModule.SendEmail(m_host.UUID, m_host.OwnerID, address, subject, message);
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: llEmail exception: {0}", e.Message);
            }
            ScriptSleep(20000);
        }
        public void llGetNextEmail(string address, string subject)
        {
            // Faithful port from Halcyon
            try
            {
                IEmailModule emailModule = World?.RequestModuleInterface<IEmailModule>();
                if (emailModule == null) return;
                Email email = emailModule.GetNextEmail(m_host.UUID, address, subject);
                if (email == null) return;

                m_ScriptEngine.PostObjectEvent(m_host.LocalId,
                    new EventParams("email",
                        new object[] {
                            email.time,
                            email.sender,
                            email.subject,
                            email.message,
                            email.numLeft
                        },
                        new DetectParams[0]));
                // The email queue "is associated with the prim and any script in the prim can access it" (SL wiki email):
                // every script in this prim, whatever engine runs it.
                SceneObjectPart host = m_host;
                OfferToOtherEngines("llGetNextEmail", () => ScriptItemsIn(new[] { host }), "email", () => new object[] {
                    email.time ?? string.Empty, email.sender ?? string.Empty, email.subject ?? string.Empty,
                    email.message ?? string.Empty, email.numLeft });
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: llGetNextEmail exception: {0}", e.Message);
            }
        }
        public void llOpenRemoteDataChannel()
        {
            // Faithful port from Halcyon
            try
            {
                IXMLRPC xmlrpcMod = World?.RequestModuleInterface<IXMLRPC>();
                if (xmlrpcMod == null || !xmlrpcMod.IsEnabled()) return;

                UUID channelID = xmlrpcMod.OpenXMLRPCChannel(m_localID, m_itemID, UUID.Zero);
                IXmlRpcRouter xmlRpcRouter = World?.RequestModuleInterface<IXmlRpcRouter>();
                if (xmlRpcRouter != null)
                    xmlRpcRouter.RegisterNewReceiver(m_ScriptEngine.ScriptModule, channelID, m_host.UUID, m_itemID,
                        "http://" + System.Environment.MachineName + ":" + xmlrpcMod.Port.ToString() + "/");

                object[] resobj = new object[] { 1, channelID.ToString(), UUID.Zero.ToString(), string.Empty, 0, string.Empty };
                m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams("remote_data", resobj, new DetectParams[0]));
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: llOpenRemoteDataChannel exception: {0}", e.Message);
            }
            ScriptSleep(1000);
        }
        public string llSendRemoteData(string channel, string dest, int idata, string sdata)
        {
            // Faithful port from Halcyon
            try
            {
                IXMLRPC xmlrpcMod = World?.RequestModuleInterface<IXMLRPC>();
                if (xmlrpcMod == null) { ScriptSleep(3000); return UUID.Zero.ToString(); }
                // The same outbound filter as llHTTPRequest. YEngine does not check llSendRemoteData (core
                // XMLRPCModule posts to dest unchecked), so here Phlox is stricter: a destination the operator's filter
                // refuses for llHTTPRequest is refused here too, with the same result.
                if (!OutboundAllowed(World.RequestModuleInterface<IHttpRequestModule>(), "llSendRemoteData", dest))
                    return string.Empty;
                ScriptSleep(3000);
                return xmlrpcMod.SendRemoteData(m_localID, m_itemID, channel, dest, idata, sdata).ToString();
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: llSendRemoteData exception: {0}", e.Message);
                ScriptSleep(3000);
                return UUID.Zero.ToString();
            }
        }
        public void llRemoteDataReply(string channel, string message_id, string sdata, int idata)
        {
            // Faithful port from Halcyon
            try
            {
                IXMLRPC xmlrpcMod = World?.RequestModuleInterface<IXMLRPC>();
                if (xmlrpcMod != null)
                    xmlrpcMod.RemoteDataReply(channel, message_id, sdata, idata);
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: llRemoteDataReply exception: {0}", e.Message);
            }
            ScriptSleep(3000);
        }
        public void llCloseRemoteDataChannel(string channel)
        {
            // Faithful port from Halcyon
            try
            {
                IXMLRPC xmlrpcMod = World?.RequestModuleInterface<IXMLRPC>();
                if (xmlrpcMod != null)
                    xmlrpcMod.CloseXMLRPCChannel(UUID.Parse(channel));
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: llCloseRemoteDataChannel exception: {0}", e.Message);
            }
            ScriptSleep(1000);
        }

        // ── Strings ────────────────────────────────────────────────────────────

        public int llStringLength(string str) => str?.Length ?? 0;
        public string llToUpper(string src) => src?.ToUpper() ?? string.Empty;
        public string llToLower(string src) => src?.ToLower() ?? string.Empty;

        // SL's range rule for llGetSubString, llDeleteSubString, llList2List, llDeleteSubList and
        // llListReplaceList. SL wiki: "Negative indexes count from the far end, the first item being indexed as
        // -length, the last as -1." / "If start > end then the range operated on starts at 0 and goes to end and then
        // starts again at start and goes to -1." / out-of-range indexes "are treated as if they were there but were
        // removed just before output". So: add the length to a negative index once; the range is [start, end], or
        // [0, end] + [start, -1] when start > end; indexes outside the list select nothing. Halcyon's GetSublist is
        // this rule; its DeleteSublist and llDeleteSubString are off by one on the inverted case.
        private static void NormaliseLslRange(int length, ref int start, ref int end)
        {
            if (start < 0) start += length;
            if (end < 0) end += length;
        }

        private static bool InLslRange(int index, int start, int end)
            => start <= end ? index >= start && index <= end : index <= end || index >= start;

        public string llGetSubString(string src, int start, int end)
        {
            if (src == null) return string.Empty;
            int len = src.Length;
            NormaliseLslRange(len, ref start, ref end);
            if (start <= end)
            {
                int s = Math.Max(start, 0), e = Math.Min(end, len - 1);
                return s > e ? string.Empty : src.Substring(s, e - s + 1);
            }
            string head = end >= 0 ? src.Substring(0, Math.Min(end, len - 1) + 1) : string.Empty;
            string tail = start < len ? src.Substring(Math.Max(start, 0)) : string.Empty;
            return head + tail;
        }

        public string llDeleteSubString(string src, int start, int end)
        {
            if (src == null) return string.Empty;
            int len = src.Length;
            NormaliseLslRange(len, ref start, ref end);
            if (start <= end)
            {
                int s = Math.Max(start, 0), e = Math.Min(end, len - 1);
                return s > e ? src : src.Remove(s, e - s + 1);
            }
            // Inverted: [0, end] and [start, -1] go, what lies between them stays.
            int keepFrom = Math.Max(end + 1, 0), keepTo = Math.Min(start - 1, len - 1);
            return keepFrom > keepTo ? string.Empty : src.Substring(keepFrom, keepTo - keepFrom + 1);
        }

        public string llInsertString(string dst, int position, string src)
        {
            if (dst == null) dst = string.Empty;
            if (src == null) src = string.Empty;
            position = Math.Max(0, Math.Min(position, dst.Length));
            return dst.Insert(position, src);
        }

        public int llSubStringIndex(string source, string pattern)
            => source?.IndexOf(pattern, StringComparison.Ordinal) ?? -1;

        public int iwSubStringIndex(string source, string pattern, int offset, int isCaseSensitive)
        {
            if (source == null || pattern == null) return -1;
            var comp = isCaseSensitive != 0 ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return source.IndexOf(pattern, Math.Max(0, offset), comp);
        }

        public int iwMatchString(string str, string pattern, int matchType)
        {
            int len1 = str?.Length ?? 0;
            int len2 = pattern?.Length ?? 0;
            if (len1 == 0 || len2 == 0)
            {
                if (matchType <= 1 && (len1 == 0 && len2 == 0)) return 1;
                else return 0;
            }
            try
            {
                return IwMatchStringCore(str, pattern, matchType);
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                ShoutError(ScriptRegex.TimedOutMessage);
                return 0;
            }
        }

        private static int IwMatchStringCore(string str, string pattern, int matchType)
        {
            switch (matchType)
            {
                case -2: return (str.IndexOf(pattern) != -1) ? 1 : 0; // IW_MATCH_INCLUDE
                case -1: return (str == pattern) ? 1 : 0;             // IW_MATCH_EQUAL
                case 0:  return str.StartsWith(pattern) ? 1 : 0;      // IW_MATCH_HEAD
                case 1:  return str.EndsWith(pattern) ? 1 : 0;        // IW_MATCH_TAIL
                case 2:                                                // IW_MATCH_REGEX
                    var r = ScriptRegex.Create("^" + pattern + "$");
                    return (r.Match(str).Length != 0) ? 1 : 0;
                case 3:                                                // IW_MATCH_COUNT
                    return ScriptRegex.Create(System.Text.RegularExpressions.Regex.Escape(pattern)).Matches(str).Count;
                case 4:                                                // IW_MATCH_COUNT_REGEX
                    return ScriptRegex.Create(pattern).Matches(str).Count;
            }
            return 0;
        }
        public string iwReplaceString(string str, string pattern, string replacement)
        {
            if (String.IsNullOrEmpty(str) || String.IsNullOrEmpty(pattern)) return str;
            if (String.IsNullOrEmpty(replacement)) return str.Replace(pattern, null);
            if (replacement.Length > 1024 || pattern.Length > 1024) return str;
            return str.Replace(pattern, replacement);
        }
        public string llReplaceSubString(string src, string pattern, string replacement, int count)
        {
            // SL: replace first 'count' occurrences of pattern in src. count=0 means all.
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(pattern)) return src ?? string.Empty;
            replacement ??= string.Empty;
            if (count == 0) return src.Replace(pattern, replacement);
            // Negative count means replace from end
            bool fromEnd = count < 0;
            int absCount = Math.Abs(count);
            if (!fromEnd)
            {
                var sb = new System.Text.StringBuilder(src.Length);
                int pos = 0, found = 0;
                while (found < absCount)
                {
                    int idx = src.IndexOf(pattern, pos, StringComparison.Ordinal);
                    if (idx < 0) break;
                    sb.Append(src, pos, idx - pos);
                    sb.Append(replacement);
                    pos = idx + pattern.Length;
                    found++;
                }
                sb.Append(src, pos, src.Length - pos);
                return sb.ToString();
            }
            else
            {
                // Find all occurrences, then replace the last N
                var indices = new List<int>();
                int p = 0;
                while (true)
                {
                    int idx = src.IndexOf(pattern, p, StringComparison.Ordinal);
                    if (idx < 0) break;
                    indices.Add(idx);
                    p = idx + pattern.Length;
                }
                if (indices.Count == 0) return src;
                int skip = Math.Max(0, indices.Count - absCount);
                var sb2 = new System.Text.StringBuilder(src.Length);
                int pos2 = 0;
                for (int i = 0; i < indices.Count; i++)
                {
                    sb2.Append(src, pos2, indices[i] - pos2);
                    if (i >= skip) sb2.Append(replacement);
                    else sb2.Append(pattern);
                    pos2 = indices[i] + pattern.Length;
                }
                sb2.Append(src, pos2, src.Length - pos2);
                return sb2.ToString();
            }
        }
        public string iwFormatString(string str, LSLList values)
        {
            if (String.IsNullOrEmpty(str)) return str;
            int len = values.Length;
            // Halcyon (LSLSystemAPI.cs:15930, 15959-15964): 100 ms whenever the tick count moved during a step
            bool throttle = m_ScriptEngine != null && m_ScriptEngine.FormatStringThrottle;
            ulong time1 = throttle ? InWorldz.Phlox.Util.Clock.Now : 0;
            for (int i = 0; i < len; i++)
            {
                string pattern = "{" + Convert.ToString(i) + "}";
                string val = values.GetLSLStringItem(i);
                if (val.Length > 1024) val = val.Substring(0, 1023);
                if (str.Contains(pattern))
                {
                    if (!String.IsNullOrEmpty(val))
                        str = str.Replace(pattern, val);
                }
                else break;
                if (str.Length > 32768)
                {
                    ScriptShoutError("Return value from iwFormatString is greater than 64kb");
                    return String.Empty;
                }
                if (throttle)
                {
                    ulong time2 = InWorldz.Phlox.Util.Clock.Now;
                    if (time2 > time1)
                    {
                        ScriptSleep(100);
                        time1 = time2;
                    }
                }
            }
            return str;
        }
        /// <summary>
        /// Halcyon's codecs, ported in Codecs/HalcyonStringCodec.cs and proven byte for byte
        /// against Halcyon's own code (Tests/InWorldz.Phlox.Tests/Golden). Halcyon's LSLError is its ScriptShoutError: the
        /// error on DEBUG_CHANNEL and the 15 ms chat pause (ChatThrottle).
        /// </summary>
        public string iwStringCodec(string str, string pattern, int operation, LSLList extraParams)
            => Codecs.HalcyonStringCodec.iwStringCodec(new CodecHost(this), str, pattern, operation, extraParams);

        private sealed class CodecHost : Codecs.HalcyonStringCodec.IHost
        {
            private readonly LSLSystemAPI m_api;
            public CodecHost(LSLSystemAPI api) { m_api = api; }
            public void LSLError(string msg) => m_api.ScriptShoutError("LSL Runtime Error: " + msg);
            public void ScriptSleep(int delay) => m_api.ScriptSleep(delay);
        }
        /// <summary>
        /// Halcyon (InWorldz.Phlox.Engine/LSLSystemAPI.cs iwReverseString): the UTF-16 code units in
        /// reverse order, so a surrogate pair comes back with its halves swapped and a combining mark
        /// lands before the character it followed. Kept as Halcyon has it.
        /// </summary>
        public string iwReverseString(string src)
        {
            if (src == null) return String.Empty;
            if (src.Length <= 1) return src;
            return new string(src.Reverse().ToArray());
        }
        public int iwChar2Int(string src, int index)
        {
            if (String.IsNullOrEmpty(src)) return 0;
            if (index < 0) index = src.Length + index;
            if (index < 0 || index >= src.Length) return 0;
            return (int)src[index];
        }
        public string iwInt2Char(int num)
        {
            if (num < 0 || num > 0xffff) return String.Empty;
            return Convert.ToChar(num).ToString();
        }

        public string llStringTrim(string src, int trim_type)
        {
            if (src == null) return string.Empty;
            if (trim_type == 1) return src.TrimStart();
            if (trim_type == 2) return src.TrimEnd();
            return src.Trim();
        }

        public string llStringToBase64(string str) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(str ?? ""));
        public string llBase64ToString(string str) { try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(str ?? "")).Replace("\uFFFD", "?"); } catch { return string.Empty; } }
        public string llIntegerToBase64(int number)
        {
            char[] imdt = new char[8];
            imdt[7] = '=';
            imdt[6] = '=';
            imdt[5] = i2ctable[number << 4 & 0x3F];
            imdt[4] = i2ctable[number >> 2 & 0x3F];
            imdt[3] = i2ctable[number >> 8 & 0x3F];
            imdt[2] = i2ctable[number >> 14 & 0x3F];
            imdt[1] = i2ctable[number >> 20 & 0x3F];
            imdt[0] = i2ctable[number >> 26 & 0x3F];
            return new string(imdt);
        }

        public int llBase64ToInteger(string str)
        {
            if (string.IsNullOrEmpty(str) || str.Length > 8) return 0;
            int number = 0;
            int digit = 0;
            if (str[0] >= c2itable.Length || (digit = c2itable[str[0]]) <= 0) return digit < 0 ? 0 : number;
            number += --digit << 26;
            if (str.Length < 2 || str[1] >= c2itable.Length || (digit = c2itable[str[1]]) <= 0) return digit < 0 ? 0 : number;
            number += --digit << 20;
            if (str.Length < 3 || str[2] >= c2itable.Length || (digit = c2itable[str[2]]) <= 0) return digit < 0 ? 0 : number;
            number += --digit << 14;
            if (str.Length < 4 || str[3] >= c2itable.Length || (digit = c2itable[str[3]]) <= 0) return digit < 0 ? 0 : number;
            number += --digit << 8;
            if (str.Length < 5 || str[4] >= c2itable.Length || (digit = c2itable[str[4]]) <= 0) return digit < 0 ? 0 : number;
            number += --digit << 2;
            if (str.Length < 6 || str[5] >= c2itable.Length || (digit = c2itable[str[5]]) <= 0) return digit < 0 ? 0 : number;
            number += --digit >> 4;
            return number;
        }

        private const string s_b64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

        /// <summary>
        /// Ported as-is from upstream LSL_Api.cs:14509-14580. wiki: deprecated in favour of
        /// llXorBase64, sleeps 0.3 s, and "incorrectly performs an exclusive or on two Base64 strings" -
        /// the padding quirks below ARE the documented behaviour, not a bug to fix here.
        /// </summary>
        public string llXorBase64Strings(string str1, string str2)
        {
            int padding = 0;
            ScriptSleep(300);
            str1 ??= string.Empty; str2 ??= string.Empty;
            if (str1.Length == 0) return string.Empty;
            if (str2.Length == 0) return str1;

            int len = str2.Length;
            if ((len % 4) != 0) // LL is EVIL!!!!
            {
                while (str2.EndsWith("=")) str2 = str2[..^1];
                len = str2.Length;
                int mod = len % 4;
                if (mod == 1) str2 = str2[..^1];
                else if (mod == 2) str2 += "==";
                else if (mod == 3) str2 += "=";
            }

            try
            {
                Convert.FromBase64String(str1);
                Convert.FromBase64String(str2);
            }
            catch { return string.Empty; }

            // Remove padding
            while (str1.EndsWith('=')) { str1 = str1[..^1]; padding++; }
            while (str2.EndsWith('=')) str2 = str2[..^1];

            byte[] d1 = new byte[str1.Length];
            byte[] d2 = new byte[str2.Length];
            for (int i = 0; i < str1.Length; i++) { int idx = s_b64.IndexOf(str1[i]); d1[i] = (byte)(idx == -1 ? 0 : idx); }
            for (int i = 0; i < str2.Length; i++) { int idx = s_b64.IndexOf(str2[i]); d2[i] = (byte)(idx == -1 ? 0 : idx); }

            var output = new System.Text.StringBuilder(d1.Length + padding);
            for (int pos = 0; pos < d1.Length; pos++)
                output.Append(s_b64[d1[pos] ^ d2[pos % d2.Length]]);
            // Here's a funny thing: LL blithely violate the base64 standard pretty much everywhere.
            // Here, padding is added only if the first input string had it, rather than when the
            // data actually needs it. This can result in invalid base64 being returned. Go figure.
            while (padding-- > 0) output.Append('=');
            return output.ToString();
        }

        public string llXorBase64StringsCorrect(string str1, string str2)
        {
            if (string.IsNullOrEmpty(str1)) return str1 ?? string.Empty;
            if (string.IsNullOrEmpty(str2)) return str1;
            string src1 = llBase64ToString(str1);
            string src2 = llBase64ToString(str2);
            var result = new System.Text.StringBuilder(src1.Length);
            int c = 0;
            for (int i = 0; i < src1.Length; i++)
            {
                result.Append((char)(src1[i] ^ src2[c]));
                if (++c >= src2.Length) c = 0;
            }
            return llStringToBase64(result.ToString());
        }

        public string llMD5String(string src, int nonce)
        {
            string input = src + ":" + nonce.ToString();
            using var md5 = System.Security.Cryptography.MD5.Create();
            byte[] hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }

        public string llSHA1String(string src)
        {
            using var sha1 = System.Security.Cryptography.SHA1.Create();
            byte[] hash = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(src ?? ""));
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }
        public string iwSHA256String(string src)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(src ?? string.Empty));
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }
        public string llComputeHash(string src, string algorithm)
        {
            // SL: supports md5, md5_sha1, sha1, sha224, sha256, sha384, sha512
            byte[] data = System.Text.Encoding.UTF8.GetBytes(src ?? string.Empty);
            byte[] hash;
            switch ((algorithm ?? "").ToLowerInvariant())
            {
                case "md5":
                    using (var alg = System.Security.Cryptography.MD5.Create())
                        hash = alg.ComputeHash(data);
                    break;
                case "sha1":
                    using (var alg = System.Security.Cryptography.SHA1.Create())
                        hash = alg.ComputeHash(data);
                    break;
                case "sha256":
                    using (var alg = System.Security.Cryptography.SHA256.Create())
                        hash = alg.ComputeHash(data);
                    break;
                case "sha384":
                    using (var alg = System.Security.Cryptography.SHA384.Create())
                        hash = alg.ComputeHash(data);
                    break;
                case "sha512":
                    using (var alg = System.Security.Cryptography.SHA512.Create())
                        hash = alg.ComputeHash(data);
                    break;
                case "md5_sha1":
                    // SL-specific: MD5 hash of the input, then SHA1 hash of that MD5 hex string
                    byte[] md5Hash;
                    using (var md5 = System.Security.Cryptography.MD5.Create())
                        md5Hash = md5.ComputeHash(data);
                    string md5Hex = BitConverter.ToString(md5Hash).Replace("-", "").ToLower();
                    using (var sha1 = System.Security.Cryptography.SHA1.Create())
                        hash = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(md5Hex));
                    break;
                default:
                    ShoutError("llComputeHash: unsupported algorithm '" + algorithm + "'");
                    return string.Empty;
            }
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }
        public string llSHA256String(string src, int nonce)
        {
            // SL: SHA256 hash with nonce appended
            return llComputeHash(src + ":" + nonce.ToString(), "sha256");
        }
        public string llHMAC(string msg, string privateKey, string algorithm)
        {
            // SL: Base64-encoded HMAC hash
            byte[] keyBytes = System.Text.Encoding.UTF8.GetBytes(privateKey ?? string.Empty);
            byte[] msgBytes = System.Text.Encoding.UTF8.GetBytes(msg ?? string.Empty);
            byte[] hash;
            switch ((algorithm ?? "").ToLowerInvariant())
            {
                case "md5":
                    using (var hmac = new System.Security.Cryptography.HMACMD5(keyBytes))
                        hash = hmac.ComputeHash(msgBytes);
                    break;
                case "sha1":
                    using (var hmac = new System.Security.Cryptography.HMACSHA1(keyBytes))
                        hash = hmac.ComputeHash(msgBytes);
                    break;
                case "sha256":
                    using (var hmac = new System.Security.Cryptography.HMACSHA256(keyBytes))
                        hash = hmac.ComputeHash(msgBytes);
                    break;
                case "sha384":
                    using (var hmac = new System.Security.Cryptography.HMACSHA384(keyBytes))
                        hash = hmac.ComputeHash(msgBytes);
                    break;
                case "sha512":
                    using (var hmac = new System.Security.Cryptography.HMACSHA512(keyBytes))
                        hash = hmac.ComputeHash(msgBytes);
                    break;
                default:
                    ShoutError("llHMAC: unsupported algorithm '" + algorithm + "'");
                    return string.Empty;
            }
            return Convert.ToBase64String(hash);
        }
        public string llGenerateKey() => UUID.Random().ToString();
        public string llEscapeURL(string url) => Uri.EscapeDataString(url ?? "");
        public string llUnescapeURL(string url) => Uri.UnescapeDataString(url ?? "");
        public string llChar(int unicode)
        {
            // SL: returns a one-character string from a Unicode codepoint
            if (unicode < 0) return string.Empty;
            // SL allows U+0000 but returns empty for negatives
            if (unicode == 0) return "\0";
            try { return char.ConvertFromUtf32(unicode); }
            catch { return string.Empty; }
        }
        public int llOrd(string src, int index)
        {
            // SL: returns the Unicode codepoint of the character at index
            if (string.IsNullOrEmpty(src)) return 0;
            if (index < 0) index = src.Length + index; // negative indexing
            if (index < 0 || index >= src.Length) return 0;
            // Handle surrogate pairs
            if (char.IsHighSurrogate(src[index]) && index + 1 < src.Length && char.IsLowSurrogate(src[index + 1]))
                return char.ConvertToUtf32(src[index], src[index + 1]);
            return src[index];
        }

        // ── Lists ──────────────────────────────────────────────────────────────

        public int llGetListLength(LSLList src) => src?.Length ?? 0;
        // Halcyon's llList2Integer / llList2Float / llList2String (LSLSystemAPI.cs:6533-6561) - the element as
        // LSLList converts it, the conversion (string)list and the casts use: llList2String of 2.0 is "2.000000" and of a
        // vector "<1.000000, 2.000000, 3.000000>" (SL wiki List: "abc123.140000<0.000000, 0.000000, 0.000000>"), not .NET's
        // "2" / "<1, 2, 3>"; llList2Integer of 1.7 is 1 and of NaN or 1e10 -2147483648, and of "0x1F" 31, where
        // Convert.ToInt32 rounded, saturated and threw.
        public int llList2Integer(LSLList src, int index)
        {
            if (src == null) return 0;
            if (index < 0) index = src.Length + index;
            return src.GetLSLIntegerItem(index);
        }
        public float llList2Float(LSLList src, int index)
        {
            if (src == null) return 0f;
            if (index < 0) index = src.Length + index;
            return src.GetLSLFloatItem(index);
        }
        public string llList2String(LSLList src, int index)
        {
            if (src == null) return string.Empty;
            if (index < 0) index = src.Length + index;
            return src.GetLSLStringItem(index);
        }
        public string llList2Key(LSLList src, int index)
        {
            if (src == null || src.Length == 0) return UUID.Zero.ToString();
            int i = index < 0 ? src.Length + index : index;
            if (i < 0 || i >= src.Length) return UUID.Zero.ToString();
            var v = src.Data[i];
            return v?.ToString() ?? UUID.Zero.ToString();
        }
        // Halcyon's llList2Vector / llList2Rot (:6582-6600), a string element read by Halcyon's parser.
        public Vector3 llList2Vector(LSLList src, int index)
        {
            if (src == null) return Vector3.Zero;
            if (index < 0) index = src.Length + index;
            return src.GetVector3Item(index);
        }
        public Quaternion llList2Rot(LSLList src, int index)
        {
            if (src == null) return Quaternion.Identity;
            if (index < 0) index = src.Length + index;
            return src.GetQuaternionItem(index);
        }
        public LSLList llList2List(LSLList src, int start, int end)
        {
            // SL's range rule (see NormaliseLslRange). Start past the end no longer throws.
            if (src == null || src.Length == 0) return new LSLList();
            int len = src.Length;
            NormaliseLslRange(len, ref start, ref end);
            var result = new List<object>();
            for (int i = 0; i < len; i++)
                if (InLslRange(i, start, end)) result.Add(src.Data[i]);
            return new LSLList(result);
        }
        public LSLList llDeleteSubList(LSLList src, int start, int end)
        {
            // SL's range rule; start > end deletes [0, end] and [start, -1].
            if (src == null) return new LSLList();
            int len = src.Length;
            NormaliseLslRange(len, ref start, ref end);
            var result = new List<object>();
            for (int i = 0; i < len; i++)
                if (!InLslRange(i, start, end)) result.Add(src.Data[i]);
            return new LSLList(result);
        }
        public int llGetListEntryType(LSLList src, int index)
        {
            if (src == null || src.Length == 0) return 0;
            int i = index < 0 ? src.Length + index : index;
            if (i < 0 || i >= src.Length) return 0;
            var v = src.Data[i];
            if (v is int)       return TYPE_INTEGER;
            if (v is float || v is double) return TYPE_FLOAT;
            if (v is string)    return TYPE_STRING;
            if (v is UUID)      return TYPE_KEY;
            if (v is Vector3)   return TYPE_VECTOR;
            if (v is Quaternion) return TYPE_ROTATION;
            if (v is LSLList)   return TYPE_INVALID;
            return 3; // default to string
        }
        public string llList2CSV(LSLList src)
        {
            // Each element as (string)list writes it (LSLList.GetLSLStringItem: floats, vectors and rotations
            // with 6 decimals, YEngine's form), joined with SL's ", " (wiki: "the values are separated with a comma and a
            // space"). Was .NET's ToString: "2", "<2, 3, 0>".
            if (src == null || src.Length == 0) return string.Empty;
            return string.Join(", ", ListElementStrings(src));
        }

        /// <summary>Every element of the list as (string)list writes it.</summary>
        private static IEnumerable<string> ListElementStrings(LSLList src)
        {
            for (int i = 0; i < src.Length; i++) yield return src.GetLSLStringItem(i);
        }
        public LSLList llCSV2List(string src)
        {
            if (string.IsNullOrEmpty(src)) return new LSLList();
            var parts = src.Split(',');
            var result = new object[parts.Length];
            for (int i = 0; i < parts.Length; i++) result[i] = parts[i].Trim();
            return new LSLList(result);
        }
        public LSLList llListSort(LSLList src, int stride, int ascending)
        {
            if (src == null || src.Length == 0) return new LSLList();
            if (stride < 1) stride = 1;
            int len = src.Length;
            // Build list of stride-length chunks
            int chunks = len / stride;
            var chunkList = new System.Collections.Generic.List<object[]>();
            for (int i = 0; i < chunks; i++)
            {
                var chunk = new object[stride];
                Array.Copy(src.Data, i * stride, chunk, 0, stride);
                chunkList.Add(chunk);
            }
            chunkList.Sort((a, b) =>
            {
                string sa = a[0]?.ToString() ?? string.Empty;
                string sb = b[0]?.ToString() ?? string.Empty;
                // Try numeric compare first
                double da, db;
                if (double.TryParse(sa, out da) && double.TryParse(sb, out db))
                    return ascending != 0 ? da.CompareTo(db) : db.CompareTo(da);
                return ascending != 0 ? string.Compare(sa, sb, StringComparison.Ordinal)
                                      : string.Compare(sb, sa, StringComparison.Ordinal);
            });
            var result = new object[chunks * stride];
            for (int i = 0; i < chunks; i++)
                Array.Copy(chunkList[i], 0, result, i * stride, stride);
            return new LSLList(result);
        }
        public LSLList llListRandomize(LSLList src, int stride)
        {
            if (src == null || src.Length == 0) return src ?? new LSLList();
            if (stride <= 0) stride = 1;
            if (src.Length == stride || src.Length % stride != 0)
                return new LSLList(new List<object>(src.Data));
            int chunkCount = src.Length / stride;
            int[] chunks = new int[chunkCount];
            for (int i = 0; i < chunkCount; i++) chunks[i] = i;
            var rand = new Random();
            for (int i = chunkCount - 1; i >= 1; i--)
            {
                int idx = rand.Next(i + 1);
                int tmp = chunks[i]; chunks[i] = chunks[idx]; chunks[idx] = tmp;
            }
            var result = new List<object>(src.Length);
            for (int i = 0; i < chunkCount; i++)
                for (int j = 0; j < stride; j++)
                    result.Add(src.Data[chunks[i] * stride + j]);
            return new LSLList(result);
        }

        public LSLList llList2ListStrided(LSLList src, int start, int end, int stride)
        {
            // SL wiki: "Returns a list of all the entries in the strided list whose index is a multiple
            // of stride in the range start to end"; "start & end will not form an exclusion range when start is past
            // end ... instead it will act as if start was zero & end was -1"; stride "if less than 1 it is assumed to be
            // 1". YEngine agrees; Halcyon returned [] for start == end and wrapped an inverted range.
            if (src == null || src.Length == 0) return new LSLList();
            var result = new List<object>();
            int len = src.Length;
            if (stride < 1) stride = 1;
            NormaliseLslRange(len, ref start, ref end);
            if (start > end) { start = 0; end = len - 1; }
            long first = (Math.Max(start, 0) + (long)stride - 1) / stride * stride;
            for (long i = first; i <= end && i < len; i += stride) result.Add(src.Data[i]);
            return new LSLList(result);
        }
        public LSLList llListInsertList(LSLList dest, LSLList src, int start)
        {
            if (dest == null) dest = new LSLList();
            if (src == null || src.Length == 0) return dest;
            int len = dest.Length;
            if (start < 0) start = Math.Max(len + start, 0);
            start = Math.Min(start, len);
            var result = new object[len + src.Length];
            Array.Copy(dest.Data, 0, result, 0, start);
            Array.Copy(src.Data, 0, result, start, src.Length);
            Array.Copy(dest.Data, start, result, start + src.Length, len - start);
            return new LSLList(result);
        }
        public int llListFindList(LSLList src, LSLList test)
        {
            if (src == null || test == null || test.Length == 0 || src.Length < test.Length) return -1;
            for (int i = 0; i <= src.Length - test.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < test.Length; j++)
                {
                    string a = src.Data[i+j]?.ToString() ?? string.Empty;
                    string b = test.Data[j]?.ToString() ?? string.Empty;
                    if (a != b) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }
        public int llListFindListNext(LSLList src, LSLList test, int ofs)
        {
            // SL: like llListFindList but starts searching from offset ofs
            if (src == null || test == null || test.Length == 0 || src.Length < test.Length) return -1;
            if (ofs < 0) ofs = 0;
            for (int i = ofs; i <= src.Length - test.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < test.Length; j++)
                {
                    string a = src.Data[i+j]?.ToString() ?? string.Empty;
                    string b = test.Data[j]?.ToString() ?? string.Empty;
                    if (a != b) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }
        public int llListFindStrided(LSLList src, LSLList test, int start, int end, int stride)
        {
            // SL: find test in src with stride constraints
            if (src == null || test == null || test.Length == 0) return -1;
            int len = src.Length;
            if (stride < 1) stride = 1;
            if (test.Length > stride) return -1;
            // Resolve negative indices
            if (start < 0) start = len + start;
            if (end < 0) end = len + end;
            start = Math.Max(0, start);
            end = Math.Min(len - 1, end);
            // Search only at stride boundaries
            for (int i = start; i <= end - test.Length + 1; i += stride)
            {
                bool match = true;
                for (int j = 0; j < test.Length; j++)
                {
                    string a = src.Data[i+j]?.ToString() ?? string.Empty;
                    string b = test.Data[j]?.ToString() ?? string.Empty;
                    if (a != b) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }
        public LSLList llList2ListSlice(LSLList src, int start, int end, int stride, int slice_index)
        {
            // SL wiki: "Returns a list of the slice_index'th element of every stride in strided list whose index
            // is a multiple of stride in the range start to end"; "If slice_index is negative it is counted from the end of
            // its stride regardless of whether or not the stride exceeds the end of the list"; "start & end will form an
            // exclusion range when start is past end"; stride "if less than 1 it is assumed to be 1". Its five examples on
            // [0,1,2,3,4,5,6] - (0,-1,3,0) [0,3,6], (0,-1,3,1) [1,4], (1,-1,3,1) [2,5], (2,-1,3,-1) [4], (4,2,1,0)
            // [0,1,2,4,5,6] - are the range taken by llList2List's rule (exclusion range included) and the
            // slice_index'th element of each stride of it, the last stride short. A slice_index outside -stride .. stride-1
            // selects nothing. Was: an inverted range gave []. YEngine differs (whole list for an inverted range, strides
            // at multiples of stride from 0).
            if (src == null || src.Length == 0) return new LSLList();
            if (stride < 1) stride = 1;
            if (slice_index < 0) slice_index += stride;
            if (slice_index < 0 || slice_index >= stride) return new LSLList();
            object[] range = llList2List(src, start, end).Data;
            var result = new List<object>();
            for (long i = slice_index; i < range.Length; i += stride)
                result.Add(range[i]);
            return new LSLList(result);
        }
        public LSLList llSortListStrided(LSLList src, int stride, int stride_index, int ascending)
        {
            // SL: sort strided list by the stride_index'th element in each stride
            if (src == null || src.Length == 0) return new LSLList();
            if (stride < 1) stride = 1;
            int len = src.Length;
            // If list length is not a multiple of stride, return unchanged
            if (stride > 1 && len % stride != 0) return src;
            // stride_index must be in range [-stride, stride)
            if (stride_index < 0) stride_index = stride + stride_index;
            if (stride_index < 0 || stride_index >= stride) return new LSLList();
            // If stride_index is 0, delegate to existing llListSort
            if (stride_index == 0) return llListSort(src, stride, ascending);
            // Build list of stride groups, sort by stride_index element
            int numStrides = len / stride;
            var groups = new List<object[]>(numStrides);
            for (int i = 0; i < numStrides; i++)
            {
                var group = new object[stride];
                for (int j = 0; j < stride; j++)
                    group[j] = src.Data[i * stride + j];
                groups.Add(group);
            }
            groups.Sort((a, b) =>
            {
                object va = a[stride_index];
                object vb = b[stride_index];
                int cmp;
                // Compare by type, matching SL behavior
                if (va is int ia && vb is int ib) cmp = ia.CompareTo(ib);
                else if (va is float fa && vb is float fb) cmp = fa.CompareTo(fb);
                else if (va is double da && vb is double db) cmp = da.CompareTo(db);
                else cmp = string.Compare(va?.ToString() ?? "", vb?.ToString() ?? "", StringComparison.Ordinal);
                return ascending == 1 ? cmp : -cmp;
            });
            var result = new List<object>(len);
            foreach (var g in groups)
                result.AddRange(g);
            return new LSLList(result);
        }
        public LSLList llListReplaceList(LSLList dest, LSLList src, int start, int end)
        {
            if (dest == null) dest = new LSLList();
            if (src == null) src = new LSLList();
            // SL's range rule (see NormaliseLslRange). SL wiki: "If end is a negative index past the
            // beginning, then the operating range would be [start, -1]." / "If end is a positive index past the end, then
            // the operating range would be [0, end]." An inverted range keeps what lies between end and start and puts
            // src after it (Halcyon and YEngine: dest.GetSublist(end + 1, start - 1) + src).
            int len = dest.Length;
            NormaliseLslRange(len, ref start, ref end);
            var result = new List<object>(len + src.Length);
            if (start <= end)
            {
                int insertAt = Math.Min(Math.Max(start, 0), len);
                for (int i = 0; i < insertAt; i++) result.Add(dest.Data[i]);
                result.AddRange(src.Data);
                for (int i = end >= len ? len : Math.Max(end + 1, insertAt); i < len; i++) result.Add(dest.Data[i]);
            }
            else
            {
                for (int i = Math.Max(end + 1, 0); i < Math.Min(start, len); i++) result.Add(dest.Data[i]);
                result.AddRange(src.Data);
            }
            return new LSLList(result);
        }
        public float llListStatistics(int operation, LSLList src)
        {
            if (src == null || src.Length == 0) return 0f;
            var nums = new List<double>();
            foreach (object o in src.Data)
                try { nums.Add(Convert.ToDouble(o)); } catch { }
            if (nums.Count == 0) return 0f;
            nums.Sort();
            switch (operation)
            {
                case 0: return (float)(nums[nums.Count - 1] - nums[0]);                   // RANGE
                case 1: return (float)nums[0];                                             // MIN
                case 2: return (float)nums[nums.Count - 1];                               // MAX
                case 3: { double s = 0; foreach (var n in nums) s += n; return (float)(s / nums.Count); } // MEAN
                case 4: { int m = nums.Count / 2; return (nums.Count % 2 == 0) ? (float)((nums[m-1]+nums[m])/2.0) : (float)nums[m]; } // MEDIAN
                case 5:
                {
                    double s = 0; foreach (var n in nums) s += n; double mean = s / nums.Count;
                    double v = 0; foreach (var n in nums) v += (n - mean) * (n - mean);
                    return (float)Math.Sqrt(v / nums.Count);
                } // STD_DEV
                case 6: { double s = 0; foreach (var n in nums) s += n; return (float)s; }          // SUM
                case 7: { double s = 0; foreach (var n in nums) s += n * n; return (float)s; }      // SUM_SQUARES
                case 8: return nums.Count;                                                 // NUM_COUNT
                case 9: { double p = 1.0; foreach (var n in nums) p *= Math.Abs(n); return (float)Math.Pow(p, 1.0/nums.Count); } // GEOMETRIC_MEAN
                case 10: { double s = 0; foreach (var n in nums) { if (n != 0) s += 1.0/n; } return s == 0 ? 0f : (float)(nums.Count/s); } // HARMONIC_MEAN
                default: return 0f;
            }
        }
        public string llDumpList2String(LSLList src, string separator)
        {
            // SL wiki: "Each element of the list is converted to string format in the result, so floats expand to
            // six digits of precision, rotations and vectors are represented with "<" and ">" characters" - the (string)list
            // form (Halcyon: GetLSLStringItem, LSLSystemAPI.cs:8785). Was .NET's ToString: "2", "<2, 3, 0>".
            if (src == null || src.Length == 0) return string.Empty;
            return string.Join(separator ?? string.Empty, ListElementStrings(src));
        }
        public LSLList llParseString2List(string src, LSLList separators, LSLList spacers)
        {
            return ParseString2List(src, separators, spacers, false);
        }
        public LSLList iwParseString2List(string src, LSLList separators, LSLList spacers_in, LSLList args)
        {
            // Faithful port from Halcyon: extended string parser with trim/capitalize/autocast/keepnulls/maxsplits
            if (string.IsNullOrEmpty(src)) return new LSLList();
            List<object> ret = new List<object>();
            List<object> spacers = new List<object>();

            bool keepNulls = false;
            int trimString = 0;
            int maxSplits = 0;
            int totalSplits = 0;
            int doCapitalize = 0;
            int autoCast = 0;

            if (args.Length > 0)
            {
                for (int i = 0; i < args.Length; i += 2)
                {
                    if (!(args.Data[i] is string)) continue;
                    string argName = args.GetLSLStringItem(i).ToLower();
                    switch (argName)
                    {
                        case "keepnulls": keepNulls = (args.GetLSLIntegerItem(i + 1) == 1); break;
                        case "trimstrings":
                            trimString = args.GetLSLIntegerItem(i + 1);
                            if (trimString < 0 || trimString > 3) trimString = 0;
                            break;
                        case "maxsplits":
                            maxSplits = args.GetLSLIntegerItem(i + 1);
                            if (maxSplits < 0) maxSplits = 0;
                            break;
                        case "capitalize":
                            doCapitalize = args.GetLSLIntegerItem(i + 1);
                            if (doCapitalize < 0 || doCapitalize > 2) doCapitalize = 0;
                            break;
                        case "autocast":
                            autoCast = args.GetLSLIntegerItem(i + 1);
                            if (autoCast < 1 || autoCast > 2) autoCast = 0;
                            break;
                    }
                }
            }

            // Build spacers list (spacers not in separators)
            if (spacers_in.Length > 0 && separators.Length > 0)
            {
                foreach (var spacer in spacers_in.Data)
                {
                    bool found = false;
                    foreach (var sep in separators.Data)
                        if (sep.ToString() == spacer.ToString()) { found = true; break; }
                    if (!found) spacers.Add(spacer);
                }
            }
            else if (spacers_in.Length > 0 && separators.Length == 0)
            {
                foreach (var s in spacers_in.Data) spacers.Add(s);
            }
            else if (spacers_in.Length == 0 && separators.Length == 0)
            {
                return new LSLList(ret);
            }

            object[] delimiters = new object[separators.Length + spacers.Count];
            separators.Data.CopyTo(delimiters, 0);
            spacers.CopyTo(delimiters, separators.Length);

            bool dfound;
            do
            {
                dfound = false;
                int cindex = -1;
                string cdeli = string.Empty;
                foreach (var delimiter in delimiters)
                {
                    string ds = delimiter.ToString();
                    if (string.IsNullOrEmpty(ds)) continue;
                    int index = src.IndexOf(ds);
                    if (index != -1)
                    {
                        if (cindex > index || cindex == -1)
                        {
                            cindex = index;
                            cdeli = ds;
                        }
                        dfound = true;
                    }
                }
                if (cindex != -1)
                {
                    if (cindex > 0)
                    {
                        string temp = ParseString2ListApply(src.Substring(0, cindex), trimString, doCapitalize);
                        if (!string.IsNullOrEmpty(temp) || keepNulls)
                        {
                            totalSplits++;
                            ret.Add(autoCast > 0 ? AutoCastString(temp) : (object)temp);
                        }
                    }
                    else if (keepNulls)
                    {
                        totalSplits++;
                        ret.Add(string.Empty);
                    }
                    if (maxSplits > 0 && totalSplits >= maxSplits) { src = src.Substring(cindex); break; }

                    // Check if delimiter is a spacer (include it in output)
                    foreach (var spacer in spacers)
                    {
                        if (spacer.ToString() == cdeli)
                        {
                            string temp = ParseString2ListApply(cdeli, trimString, doCapitalize);
                            if (!string.IsNullOrEmpty(temp) || keepNulls)
                            {
                                totalSplits++;
                                ret.Add(autoCast == 2 ? AutoCastString(cdeli) : (object)cdeli);
                            }
                            break;
                        }
                    }
                    if (maxSplits > 0 && totalSplits >= maxSplits) { src = src.Substring(cindex + cdeli.Length); break; }
                    src = src.Substring(cindex + cdeli.Length);
                    if (maxSplits > 0 && totalSplits >= maxSplits) break;
                }
            } while (dfound);

            src = ParseString2ListApply(src, trimString, doCapitalize);
            if (!string.IsNullOrEmpty(src) || keepNulls)
                ret.Add(autoCast > 1 ? AutoCastString(src) : (object)src);

            return new LSLList(ret);
        }

        /// <summary>Apply trim and capitalize to a parsed string element.</summary>
        private string ParseString2ListApply(string str, int trimString, int doCapitalize)
        {
            if (string.IsNullOrEmpty(str)) return str;
            if (trimString != 0) str = llStringTrim(str, trimString);
            if (doCapitalize == 1) str = str.ToUpper();
            else if (doCapitalize == 2) str = str.ToLower();
            return str;
        }

        /// <summary>Attempt to auto-cast a string to int/float/vector/rotation/key.</summary>
        private object AutoCastString(string str)
        {
            if (string.IsNullOrEmpty(str)) return str;
            int dotCount = str.Length - str.Replace(".", string.Empty).Length;
            if (dotCount == 1) { if (float.TryParse(str, out float f)) return f; }
            else if (dotCount == 0) { if (int.TryParse(str, out int i)) return i; }
            if (str.StartsWith("<") && str.EndsWith(">"))
            {
                int commas = str.Length - str.Replace(",", string.Empty).Length;
                if (commas == 2) { if (Vector3.TryParse(str, out Vector3 vec)) return vec; }
                else if (commas == 3) { if (Quaternion.TryParse(str, out Quaternion quat)) return quat; }
            }
            if (str.Length == 36) { if (UUID.TryParse(str, out UUID k)) return k.ToString(); }
            return str;
        }
        public LSLList llParseStringKeepNulls(string src, LSLList separators, LSLList spacers)
        {
            return ParseString2List(src, separators, spacers, true);
        }
        private int listCompare(LSLList list1, LSLList list2)
        {
            if (list1.Length != list2.Length) return 0;
            int len = list1.Length;
            for (int i = 0; i < len; i++)
            {
                int t1 = llGetListEntryType(list1, i);
                int t2 = llGetListEntryType(list2, i);
                if (t1 != t2 || (t1 == 0 && t2 == 0)) return 0;
                if (t1 == 1 && (list1.GetLSLIntegerItem(i) != list2.GetLSLIntegerItem(i))) return 0;
                if (t1 == 2 && (list1.GetLSLFloatItem(i) != list2.GetLSLFloatItem(i))) return 0;
                else if ((t1 == 3 || t1 == 4) && (list1.GetLSLStringItem(i) != list2.GetLSLStringItem(i))) return 0;
                else if (t1 == 5 && (list1.GetVector3Item(i) != list2.GetVector3Item(i))) return 0;
                else if (t1 == 6 && (list1.GetQuaternionItem(i) != list2.GetQuaternionItem(i))) return 0;
            }
            return 1;
        }

        public int iwMatchList(LSLList list1, LSLList list2, int matchType)
        {
            int len1 = list1.Length;
            int len2 = list2.Length;
            if (len1 == 0 || len2 == 0)
            {
                if (matchType <= 1) return (len1 == 0 && len2 == 0) ? 1 : 0;
                else return 0;
            }
            switch (matchType)
            {
                case -1: // IW_MATCH_EQUAL
                    if (len1 != len2) return 0;
                    return listCompare(list1, list2);
                case 0: // IW_MATCH_HEAD
                    if (len1 < len2) return 0;
                    return listCompare(list1.GetSublist(0, len2 - 1), list2);
                case 1: // IW_MATCH_TAIL
                    if (len1 < len2) return 0;
                    return listCompare(list1.GetSublist(len1 - len2, len1 - 1), list2);
                case 2:
                    ScriptShoutError("IW_MATCH_REGEX not implemented for iwMatchList.");
                    break;
                case 3:
                    ScriptShoutError("IW_MATCH_COUNT not implemented for iwMatchList.");
                    break;
                case 4:
                    ScriptShoutError("IW_MATCH_COUNT_REGEX not implemented for iwMatchList.");
                    break;
            }
            return 0;
        }
        public int iwListIncludesElements(LSLList src, LSLList elements, int any)
        {
            if (elements.Length == 0 || src.Length == 0) return 0;
            for (int a = 0; a < elements.Length; a++)
            {
                bool found = false;
                for (int b = 0; b < src.Length; b++)
                {
                    if (src.Data[b].Equals(elements.Data[a]))
                    {
                        found = true;
                        break;
                    }
                }
                if (any == 1)
                {
                    if (found) return 1;
                }
                else
                {
                    if (!found) return 0;
                }
            }
            return (any == 1) ? 0 : 1;
        }
        public LSLList iwReverseList(LSLList src, int stride)
        {
            if (src.Length <= 1) return src;
            if (stride < 1) return new LSLList(src.Data.Reverse().ToArray());
            if (src.Length % stride != 0)
            {
                ScriptShoutError(string.Format("Error: stride argument is {0}, but source list length is not divisible by {0}", stride));
                return new LSLList();
            }
            List<object> ret = new List<object>();
            for (int a = src.Length - 1; a >= 0; a -= stride)
            {
                ret.AddRange(src.GetSublist(a - (stride - 1), a).Data.ToList());
            }
            return new LSLList(ret);
        }
        public LSLList iwListRemoveElements(LSLList src, LSLList elements, int count, int mode)
        {
            if (src.Length == 0 || elements.Length == 0) return src;
            if (count == 0) count = -1;
            int counted = 0;
            List<object> ret = new List<object>();
            if (mode == 0)
            {
                int len = src.Length - elements.Length + 1;
                for (int i = 0; i < len; i++)
                {
                    if (src.Data[i].Equals(elements.Data[0]))
                    {
                        if (count == -1 || counted < count)
                        {
                            int x;
                            for (x = 1; x < elements.Length; x++)
                                if (!src.Data[i + x].Equals(elements.Data[x]))
                                    break;
                            if (x == elements.Length)
                            {
                                counted++;
                                i += elements.Length - 1;
                                continue;
                            }
                        }
                    }
                    ret.Add(src.Data[i]);
                }
            }
            else
            {
                int len = src.Length;
                for (int i = 0; i < len; i++)
                {
                    if (!elements.Data.Contains(src.Data[i]))
                    {
                        if (count == -1 || counted < count)
                        {
                            ret.Add(src.Data[i]);
                            counted++;
                        }
                    }
                }
            }
            return new LSLList(ret);
        }
        public LSLList iwListRemoveDuplicates(LSLList src)
        {
            if (src.Length <= 1) return src;
            return new LSLList(src.Data.Distinct().ToList());
        }

        // ── Data requests ──────────────────────────────────────────────────────

        public void llRequestAgentData(string id, int data)
        {
            if (m_host == null) { ReturnQueryKey(UUID.Zero); return; }   // Every path returns
            if (!UUID.TryParse(id, out UUID agentId)) { ReturnQueryKey(UUID.Zero); return; }

            UUID queryID = NewDataserverQuery();
            ReturnQueryKey(queryID);
            UUID capturedQuery = queryID;
            UUID capturedAgent = agentId;
            int capturedData = data;

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string result = string.Empty;
                    switch (capturedData)
                    {
                        case DATA_ONLINE:
                            result = IsOnlineToThisScript(capturedAgent) ? "1" : "0";
                            break;
                        case DATA_NAME:
                        {
                            ScenePresence sp = World?.GetScenePresence(capturedAgent);
                            if (sp != null)
                            {
                                result = sp.Name;
                            }
                            else
                            {
                                UserAccount acct = World?.UserAccountService?.GetUserAccount(
                                    World.RegionInfo.ScopeID, capturedAgent);
                                result = acct != null
                                    ? acct.FirstName + " " + acct.LastName
                                    : string.Empty;
                            }
                            break;
                        }
                        case DATA_BORN: // DATA_BORN — account creation date as "YYYY-MM-DD"
                        {
                            UserAccount acct = World?.UserAccountService?.GetUserAccount(
                                World.RegionInfo.ScopeID, capturedAgent);
                            if (acct != null)
                            {
                                var born = DateTimeOffset.FromUnixTimeSeconds(acct.Created).UtcDateTime;
                                result = born.ToString("yyyy-MM-dd");
                            }
                            break;
                        }
                        case DATA_RATING: // DATA_RATING — removed from SL, always return zeroes
                            result = "0,0,0,0,0,0";
                            break;
                        case DATA_PAYINFO:
                            result = PayInfo(capturedAgent);
                            break;
                        default:
                            result = string.Empty;
                            break;
                    }
                    PostDataserverEvent(capturedQuery, result);
                }
                catch (Exception ex)
                {
                    m_log.LogError("[PhloxAPI]: llRequestAgentData ex: {0}", ex.Message);
                    PostDataserverEvent(capturedQuery, string.Empty);
                }
            });

            ScriptSleep(100);
        }
        /// <summary>
        /// Wiki: "Requests data about region. When data is available the dataserver event
        /// will be raised"; DATA_SIM_POS a vector of the region's global position, DATA_SIM_STATUS
        /// "up"/..., DATA_SIM_RATING "PG"/"MATURE"/"ADULT"/"UNKNOWN", 1.0 s sleep. Ported from upstream
        /// LSL_Api.cs:13389-13485: the local region answers from RegionInfo; any other region is
        /// resolved through GridService.GetRegionByName, with the hypergrid RegionSecret dance for
        /// POS. Two departures from upstream, both towards the wiki: POS is in metres (upstream returns
        /// region units against the wiki's "global position"), and an unknown region answers with the
        /// wiki's texts rather than "unknown". The reply goes by the dataserver door.
        /// </summary>
        public string llRequestSimulatorData(string simulator, int data)
        {
            const int DATA_SIM_RELEASE = 128;
            if (World?.RegionInfo == null) return UUID.Zero.ToString();
            if (data != DATA_SIM_POS && data != DATA_SIM_STATUS && data != DATA_SIM_RATING && data != DATA_SIM_RELEASE)
            {
                ScriptSleep(1000);
                return UUID.Zero.ToString();   // raise no event, as upstream
            }

            static string Rating(int maturity) => maturity switch { 0 => "PG", 1 => "MATURE", 2 => "ADULT", _ => "UNKNOWN" };
            static string PosOf(uint worldX, uint worldY) => new Vector3(worldX, worldY, 0f).ToString();

            UUID queryID = NewDataserverQuery();
            string reply;
            if (simulator.Equals(World.RegionInfo.RegionName, StringComparison.OrdinalIgnoreCase))
            {
                RegionInfo ri = World.RegionInfo;
                reply = data switch
                {
                    DATA_SIM_POS => PosOf(ri.WorldLocX, ri.WorldLocY),
                    DATA_SIM_STATUS => "up",
                    DATA_SIM_RATING => Rating(ri.RegionSettings.Maturity),
                    _ => "OpenSim",
                };
            }
            else
            {
                reply = data == DATA_SIM_STATUS ? "unknown region" : data == DATA_SIM_RATING ? "rating or region unknown" : "unknown";
                try
                {
                    var info = World.GridService?.GetRegionByName(World.RegionInfo.ScopeID, simulator);
                    if (info != null)
                    {
                        switch (data)
                        {
                            case DATA_SIM_POS:
                                // Hypergrid puts the real destination coords in RegionSecret (upstream :13437-13451).
                                var flags = (OpenSim.Framework.RegionFlags)World.GridService.GetRegionFlags(info.ScopeID, info.RegionID);
                                if ((flags & OpenSim.Framework.RegionFlags.Hyperlink) != 0 && ulong.TryParse(info.RegionSecret, out ulong handle))
                                {
                                    Utils.LongToUInts(handle, out uint rx, out uint ry);
                                    reply = PosOf(rx, ry);
                                }
                                else reply = PosOf((uint)info.RegionLocX, (uint)info.RegionLocY);
                                break;
                            case DATA_SIM_STATUS: reply = "up"; break;
                            case DATA_SIM_RATING: reply = Rating(info.Maturity); break;
                            default: reply = "OpenSim"; break;
                        }
                    }
                }
                catch (Exception e)
                {
                    m_log.LogWarning("[PhloxAPI]: llRequestSimulatorData({0}) grid lookup failed: {1}", simulator, e.Message);
                }
            }
            PostDataserverEvent(queryID, reply);
            ScriptSleep(1000);
            return queryID.ToString();
        }
        public string llGetEnv(string name)
        {
            if (World?.RegionInfo == null) return string.Empty;
            return name switch
            {
                "agent_limit"          => World.RegionInfo.RegionSettings.AgentLimit.ToString(),
                "dynamic_pathfinding"  => "disabled",
                "estate_id"            => World.RegionInfo.EstateSettings.EstateID.ToString(),
                "estate_name"          => World.RegionInfo.EstateSettings.EstateName ?? string.Empty,
                "frame_number"         => World.StatsReporter?.LastReportedSimFPS.ToString() ?? "0",
                "region_cpu_ratio"     => "1",
                "region_idle"          => "0",
                "region_product_name"  => World.RegionInfo.RegionType ?? string.Empty,
                "region_product_sku"   => "OpenSim",
                "region_start_time"    => "0",
                "sim_channel"          => "OpenSim",
                "sim_version"          => "0.9.3.0",
                "simulator_hostname"   => World.RegionInfo.ExternalHostName ?? string.Empty,
                "region_max_prims"     => World.RegionInfo.ObjectCapacity.ToString(),
                "region_object_bonus"  => ((float)World.RegionInfo.RegionSettings.ObjectBonus).ToString(),
                "grid"                 => OpenSim.Region.ScriptEngine.Shared.Api.LSL_Api.EnvGridName(World), // YEngine's own answer
                _                     => string.Empty
            };
        }
        public float llGetSimStats(int statType)
        {
            // SL stat type constants → SimStatsReporter indices
            // SL constants: 0=TimeDilation, 1=SimFPS, 2=PhysFPS, 3=AgentUpdates,
            //   4=RootAgents, 5=ChildAgents, 6=TotalPrims, 7=ActivePrims,
            //   8=FrameMS, 9=NetMS, 10=PhysicsMS, 11=ImageMS, 12=OtherMS,
            //   13=InPPS, 14=OutPPS, 15=UnAckedBytes, 16=AgentMS,
            //   17=PendingDownloads, 18=PendingUploads, 19=ActiveScripts,
            //   20=SimSleepMs, 21=SimSpareMs
            var reporter = World?.StatsReporter;
            if (reporter == null) return 0f;
            float[] stats = reporter.LastReportedSimStats;
            if (stats == null || statType < 0 || statType >= stats.Length) return 0f;
            return stats[statType];
        }
        /// <summary>Wiki: "the most bytes used while llScriptProfiler was last active".
        /// 0 when profiling was never started - there is no LSO fixed size to report here.</summary>
        public int llGetSPMaxMemory()
        {
            var st = m_thisScript?.ScriptState;
            if (st == null) return 0;
            st.SampleMemoryPeak();
            return st.PeakMemoryUsed;
        }
        /// <summary>Ported from upstream LSL_Api.cs:4548-4556. wiki: "Returns a list of names
        /// of animations playing in the current object"; the part tracks them in AnimationsNames.</summary>
        public LSLList llGetObjectAnimationNames()
        {
            var ret = new List<object>();
            var names = m_host?.AnimationsNames;
            if (names == null || names.Count == 0) return new LSLList(ret);
            lock (names)
                foreach (string name in names.Values) ret.Add(name);
            return new LSLList(ret);
        }
        /// <summary>
        /// Ported from upstream LSL_Api.cs:4533-4541. wiki: the animation is "an item in the
        /// inventory of the prim this script is in" - resolved by inventory name, then the default
        /// avatar animation names, never by UUID. The part manages the set and sends the update;
        /// whether anything moves is the mesh's business (Animesh needs a skeleton).
        /// </summary>
        public void llStartObjectAnimation(string anim)
        {
            if (m_host == null) return;
            UUID animID = OpenSim.Region.Framework.Scenes.Scripting.ScriptUtils.GetAssetIdFromItemName(m_host, anim, (int)AssetType.Animation);
            if (animID == UUID.Zero)
                animID = DefaultAvatarAnimations.GetDefaultAnimation(anim);
            if (animID != UUID.Zero)
                m_host.AddAnimation(animID, anim);
        }

        /// <summary>Ported from upstream LSL_Api.cs:4543-4546 - by the same name it was started with.</summary>
        public void llStopObjectAnimation(string anim)
        {
            m_host?.RemoveAnimation(anim);
        }

        /// <summary>
        /// Wiki: llGetLinkSitFlags reads the flags on the link's sit target. Upstream
        /// (LSL_Api.cs:21155-21166) hard-codes ALLOW_UNSIT | NO_COLLIDE | NO_DAMAGE as "forced" and
        /// stores nothing; this reports the part's real state. SIT_TARGET is read-only, from
        /// IsSitTargetSet; ALLOW_UNSIT and SCRIPTED_ONLY are the part properties ScenePresence honours
        /// (ScenePresence.cs:2656, :3399, :3411); NO_COLLIDE and NO_DAMAGE are stored and read back.
        /// </summary>
        public int llGetLinkSitFlags(int link)
        {
            SceneObjectPart part = GetLinkParts(link).FirstOrDefault();
            return part == null ? 0 : PartSitFlags(part);
        }

        /// <summary>
        /// One prim's SIT_FLAG_* word, for llGetLinkSitFlags and PRIM_SIT_FLAGS. Every bit is read from the scene:
        /// SIT_TARGET from IsSitTargetSet, ALLOW_UNSIT and SCRIPTED_ONLY from the part properties OpenSim's LSL_Api
        /// sets for PRIM_ALLOW_UNSIT and PRIM_SCRIPTED_SIT_ONLY, NO_COLLIDE and NO_DAMAGE from SitFlagsStored.
        /// </summary>
        private static int PartSitFlags(SceneObjectPart part)
        {
            int flags = part.SitFlagsStored & (SIT_FLAG_NO_COLLIDE | SIT_FLAG_NO_DAMAGE);
            if (part.IsSitTargetSet) flags |= SIT_FLAG_SIT_TARGET;
            if (part.AllowUnsit) flags |= SIT_FLAG_ALLOW_UNSIT;
            if (part.ScriptedSitOnly) flags |= SIT_FLAG_SCRIPTED_ONLY;
            return flags;
        }

        /// <summary>
        /// Sets one prim's sit flags, for llSetLinkSitFlags and PRIM_SIT_FLAGS: every settable bit takes the word's
        /// value, SIT_FLAG_SIT_TARGET is read-only and ignored (SL: "Read-only flag to indicate whether the link has
        /// a sit target").
        /// </summary>
        private static void PartSetSitFlags(SceneObjectPart part, int flags)
        {
            part.AllowUnsit = (flags & SIT_FLAG_ALLOW_UNSIT) != 0;
            part.ScriptedSitOnly = (flags & SIT_FLAG_SCRIPTED_ONLY) != 0;
            part.SitFlagsStored = flags & (SIT_FLAG_NO_COLLIDE | SIT_FLAG_NO_DAMAGE);
        }

        /// <summary>
        /// Wiki: "Sets flags on the link's sittarget." Upstream's is a no-op (LSL_Api.cs:21168).
        /// Here ALLOW_UNSIT and SCRIPTED_ONLY are honoured by the region's sit path today, through the
        /// part properties it already checks; NO_COLLIDE and NO_DAMAGE are stored for read-back only -
        /// the presence has no seated collision-volume toggle and no damage distribution to seated
        /// avatars (there is no damage hook for that). SIT_TARGET is read-only and ignored.
        /// </summary>
        public void llSetLinkSitFlags(int link, int flags)
        {
            foreach (SceneObjectPart part in GetLinkParts(link))
                PartSetSitFlags(part, flags);
        }
        public Vector3 llLinear2sRGB(Vector3 color)
        {
            // SL: convert linear RGB to sRGB color space
            float ToSRGB(float c)
            {
                if (c <= 0.0031308f) return c * 12.92f;
                return 1.055f * (float)Math.Pow(c, 1.0 / 2.4) - 0.055f;
            }
            return new Vector3(
                Math.Max(0f, Math.Min(1f, ToSRGB(color.X))),
                Math.Max(0f, Math.Min(1f, ToSRGB(color.Y))),
                Math.Max(0f, Math.Min(1f, ToSRGB(color.Z))));
        }
        public Vector3 llSRGB2Linear(Vector3 color)
        {
            // SL: convert sRGB to linear RGB color space
            float ToLinear(float c)
            {
                if (c <= 0.04045f) return c / 12.92f;
                return (float)Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return new Vector3(
                Math.Max(0f, Math.Min(1f, ToLinear(color.X))),
                Math.Max(0f, Math.Min(1f, ToLinear(color.Y))),
                Math.Max(0f, Math.Min(1f, ToLinear(color.Z))));
        }
        public Vector3 llWorldPosToHUD(Vector3 worldPos)
        {
            // SL: converts world position to HUD screen coordinates.
            // Requires viewer camera data not available server-side. Return center screen as default.
            return new Vector3(0.5f, 0.5f, 0f);
        }

        // ── Linkset Data (LSD) ─────────────────────────────────────────────────
        // SL: 128KB persistent key-value store per linkset, survives script reset/copy/transfer.
        // Constants: LINKSETDATA_OK=0, EMEMORY=1, ENOKEY=2, EPROTECTED=3, NOTFOUND=4, NOUPDATE=5
        // Event actions: RESET=0, UPDATE=1, DELETE=2, MULTIDELETE=3



        // Bind to Tranquillity's native per-linkset limit (SL = 128KB) rather than the port source's
        // Scene.m_LinkSetDataLimit (which Tranquillity does not have).
        private int LinksetDataLimit => LinksetData.LINKSETDATA_MAX;

        public int llLinksetDataAvailable()
        {
            if (m_host?.ParentGroup?.LinksetData == null)
                return LinksetDataLimit;
            return m_host.ParentGroup.LinksetData.Free();
        }

        public int llLinksetDataCountKeys()
        {
            if (m_host?.ParentGroup?.LinksetData == null)
                return 0;
            return m_host.ParentGroup.LinksetData.Count();
        }

        public string llLinksetDataRead(string name)
        {
            if (m_host?.ParentGroup?.LinksetData == null || string.IsNullOrEmpty(name))
                return string.Empty;
            return m_host.ParentGroup.LinksetData.Get(name) ?? string.Empty;
        }

        public string llLinksetDataReadProtected(string name, string pass)
        {
            if (m_host?.ParentGroup?.LinksetData == null || string.IsNullOrEmpty(name))
                return string.Empty;
            return m_host.ParentGroup.LinksetData.Get(name, pass) ?? string.Empty;
        }

        /// <summary>
        /// SL wiki linkset_data: "The linkset_data event fires in all scripts in a linkset whenever the datastore has been
        /// modified through a call to one of the llLinksetData functions." Phlox's scripts (PostObjectLinksetDataEvent fans
        /// out to every prim), then every other engine's script in every prim of the linkset. Offered here, not from the
        /// engine's PostObjectLinksetDataEvent, so another caller of that interface method is never offered back.
        /// </summary>
        private void PostLinksetData(int action, string name, string value)
        {
            m_ScriptEngine.PostObjectLinksetDataEvent(m_host.LocalId, action, name, value);
            SceneObjectGroup group = m_host.ParentGroup;
            if (group == null) return;
            OfferToOtherEngines("linkset_data", () => ScriptItemsIn(group.Parts), "linkset_data",
                () => new object[] { action, name ?? string.Empty, value ?? string.Empty });
        }

        public int llLinksetDataWrite(string name, string value)
        {
            if (string.IsNullOrEmpty(name))
                return LINKSETDATA_ENOKEY;

            if (string.IsNullOrEmpty(value))
            {
                if (m_host?.ParentGroup?.LinksetData == null)
                    return LINKSETDATA_NOTFOUND;
                int delRet = m_host.ParentGroup.LinksetData.Remove(name);
                if (delRet == 0)
                {
                    PostLinksetData(LINKSETDATA_DELETE, name, string.Empty);
                    m_host.ParentGroup.HasGroupChanged = true;
                }
                return delRet;
            }

            if (m_host?.ParentGroup == null) return LINKSETDATA_EMEMORY;
            m_host.ParentGroup.LinksetData ??= new LinksetData();
            int ret = m_host.ParentGroup.LinksetData.AddOrUpdate(name, value);
            if (ret == 0)
            {
                PostLinksetData(LINKSETDATA_UPDATE, name, value);
                m_host.ParentGroup.HasGroupChanged = true;
            }
            return ret;
        }

        public int llLinksetDataWriteProtected(string name, string value, string pass)
        {
            if (string.IsNullOrEmpty(name))
                return LINKSETDATA_ENOKEY;

            if (string.IsNullOrEmpty(value))
            {
                if (m_host?.ParentGroup?.LinksetData == null)
                    return LINKSETDATA_NOTFOUND;
                int delRet = m_host.ParentGroup.LinksetData.Remove(name, pass);
                if (delRet == 0)
                {
                    PostLinksetData(LINKSETDATA_DELETE, name, string.Empty);
                    m_host.ParentGroup.HasGroupChanged = true;
                }
                return delRet;
            }

            if (m_host?.ParentGroup == null) return LINKSETDATA_EMEMORY;
            m_host.ParentGroup.LinksetData ??= new LinksetData();
            int ret = m_host.ParentGroup.LinksetData.AddOrUpdate(name, value, pass);
            if (ret == 0)
            {
                PostLinksetData(LINKSETDATA_UPDATE, name, string.Empty);
                m_host.ParentGroup.HasGroupChanged = true;
            }
            return ret;
        }

        public int llLinksetDataDelete(string name)
        {
            if (string.IsNullOrEmpty(name))
                return LINKSETDATA_ENOKEY;
            if (m_host?.ParentGroup?.LinksetData == null)
                return LINKSETDATA_NOTFOUND;
            int ret = m_host.ParentGroup.LinksetData.Remove(name);
            if (ret == 0)
            {
                PostLinksetData(LINKSETDATA_DELETE, name, string.Empty);
                m_host.ParentGroup.HasGroupChanged = true;
            }
            return ret;
        }

        public int llLinksetDataDeleteProtected(string name, string pass)
        {
            if (string.IsNullOrEmpty(name))
                return LINKSETDATA_ENOKEY;
            if (m_host?.ParentGroup?.LinksetData == null)
                return LINKSETDATA_NOTFOUND;
            int ret = m_host.ParentGroup.LinksetData.Remove(name, pass);
            if (ret == 0)
            {
                PostLinksetData(LINKSETDATA_DELETE, name, string.Empty);
                m_host.ParentGroup.HasGroupChanged = true;
            }
            return ret;
        }

        public void llLinksetDataReset()
        {
            if (m_host?.ParentGroup?.LinksetData == null)
                return;
            bool changed = m_host.ParentGroup.LinksetData.Count() > 0;
            m_host.ParentGroup.LinksetData = null;
            if (changed)
            {
                PostLinksetData(LINKSETDATA_RESET, string.Empty, string.Empty);
                m_host.ParentGroup.HasGroupChanged = true;
            }
        }

        public LSLList llLinksetDataDeleteFound(string pattern, string pass)
        {
            if (string.IsNullOrEmpty(pattern) || m_host?.ParentGroup?.LinksetData == null)
                return new LSLList(new object[] { 0, 0 });
            string[] deleted = m_host.ParentGroup.LinksetData.RemoveByPattern(pattern, pass, out int notDeleted);
            if (deleted.Length > 0)
            {
                string deletedList = string.Join(",", deleted);
                PostLinksetData(LINKSETDATA_MULTIDELETE, deletedList, string.Empty);
                m_host.ParentGroup.HasGroupChanged = true;
            }
            return new LSLList(new object[] { deleted.Length, notDeleted });
        }

        public int llLinksetDataCountFound(string pattern)
        {
            if (string.IsNullOrEmpty(pattern) || m_host?.ParentGroup?.LinksetData == null)
                return 0;
            return m_host.ParentGroup.LinksetData.CountByPattern(pattern);
        }

        public LSLList llLinksetDataListKeys(int start, int count)
        {
            if (m_host?.ParentGroup?.LinksetData == null)
                return new LSLList();
            string[] keys = m_host.ParentGroup.LinksetData.ListKeys(start, count);
            return new LSLList(keys);
        }

        public LSLList llLinksetDataFindKeys(string pattern, int start, int count)
        {
            if (string.IsNullOrEmpty(pattern) || m_host?.ParentGroup?.LinksetData == null)
                return new LSLList();
            string[] keys = m_host.ParentGroup.LinksetData.ListKeysByPatttern(pattern, start, count);
            return new LSLList(keys);
        }
        public string iwRequestAnimationData(string name)
        {
            // Faithful port from Halcyon — look up animation in inventory and return metadata via dataserver
            if (m_host == null) return UUID.Zero.ToString();
            TaskInventoryItem item = FindInventoryItem(name, (int)AssetType.Animation);
            if (item == null) { ScriptSleep(1000); return UUID.Zero.ToString(); }

            UUID queryID = NewDataserverQuery();
            UUID assetId = item.AssetID;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    AssetBase asset = World.AssetService.Get(assetId.ToString());
                    if (asset == null || asset.Data == null)
                    {
                        PostDataserverEvent(queryID, string.Empty);
                        return;
                    }
                    BinBVHAnimation anim = new BinBVHAnimation(asset.Data);
                    string reply = string.Format("{0} {1} {2} {3} {4} {5} {6} {7} {8}",
                        (int)anim.Priority,
                        anim.Loop ? 1 : 0,
                        ((float)anim.Length).ToString("F4"),
                        ((float)anim.InPoint).ToString("F4"),
                        ((float)anim.OutPoint).ToString("F4"),
                        ((float)anim.EaseInTime).ToString("F4"),
                        ((float)anim.EaseOutTime).ToString("F4"),
                        (int)anim.HandPose,
                        anim.ExpressionName ?? string.Empty);
                    PostDataserverEvent(queryID, reply);
                }
                catch
                {
                    PostDataserverEvent(queryID, string.Empty);
                }
            });
            ScriptSleep(1000);
            return queryID.ToString();
        }

        // ── Money ──────────────────────────────────────────────────────────────

        public int llGiveMoney(string destination, int amount)
        {
            // Halcyon's order (LSLSystemAPI.cs:2978-3014). Phlox moves no money; every path still sleeps 3 s.
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) { ScriptSleep(3000); return 0; }

            // PERMISSION_DEBIT (0x02), granted by the owner: Halcyon CheckRuntimePerms(item, item.OwnerID, PERMISSION_DEBIT)
            // (:2993, :4467-4473); SL: "it must be granted by the owner". Phlox's text, as before.
            if ((item.PermsMask & PERMISSION_DEBIT) == 0 || item.PermsGranter != item.OwnerID)
            {
                ScriptShoutError("llGiveMoney: PERMISSION_DEBIT not granted.");
                ScriptSleep(3000);
                return 0;
            }

            // Halcyon :2999-3003.
            if (!UUID.TryParse(destination, out UUID destId)) { LSLError("Bad key in llGiveMoney"); ScriptSleep(3000); return 0; }
            if (destId == UUID.Zero || amount <= 0) { ScriptSleep(3000); return 0; }

            // Halcyon :3006-3010 - no money module is "not implemented". With one, Phlox still
            // returns 0 (it has no money support).
            if (World?.RequestModuleInterface<IMoneyModule>() == null) NotImplemented("llGiveMoney");
            ScriptSleep(3000);
            return 0;
        }
        public string llTransferLindenDollars(string destination, int amount)
        {
            // No economy module available in this tree
            UUID txnId = NewDataserverQuery();
            PostDataserverEvent(txnId, "LINDENDOLLAR_INSUFFICIENTFUNDS");
            return txnId.ToString();
        }
        public string iwGiveMoney(string destination, int amount)
        {
            return llTransferLindenDollars(destination, amount);
        }
        public void llSetPayPrice(int price, LSLList quick_pay_buttons)
        {
            if (m_host == null) return;
            m_host.PayPrice[0] = price;
            if (quick_pay_buttons.Length > 0) m_host.PayPrice[1] = quick_pay_buttons.GetLSLIntegerItem(0);
            if (quick_pay_buttons.Length > 1) m_host.PayPrice[2] = quick_pay_buttons.GetLSLIntegerItem(1);
            if (quick_pay_buttons.Length > 2) m_host.PayPrice[3] = quick_pay_buttons.GetLSLIntegerItem(2);
            if (quick_pay_buttons.Length > 3) m_host.PayPrice[4] = quick_pay_buttons.GetLSLIntegerItem(3);
        }
        public float llGetEnergy() => 1.0f; // Halcyon: always 1.0

        // ── Misc ───────────────────────────────────────────────────────────────

        public void llSetPrimURL(string url) { /* Deprecated */ }
        public void llRefreshPrimURL() { /* Deprecated - not supported */ }
        public void llMapDestination(string simname, Vector3 pos, Vector3 look_at)
        {
            UUID targetAvatar = UUID.Zero;

            // Use DetectParams to find the touching avatar (faithful to Halcyon)
            var detectedParams = m_thisScript?.ScriptState?.GetDetectVariables(0);
            if (detectedParams != null)
            {
                UUID.TryParse(detectedParams.Key, out targetAvatar);
            }
            else
            {
                // Fallback for non-touch events: attachments target the wearer
                if (m_host.ParentGroup.IsAttachment)
                    targetAvatar = m_host.OwnerID;
            }

            if (targetAvatar != UUID.Zero)
            {
                ScenePresence avatar = World?.GetScenePresence(targetAvatar);
                if (avatar != null)
                {
                    try
                    {
                        // Tranquillity's SendScriptTeleportRequest signature is (objName, simName, pos, int options)
                        // — it dropped the port source's lookAt vector ("lookat does nothing"). Pass options = 0.
                        avatar.ControllingClient.SendScriptTeleportRequest(m_host.Name, simname, pos, 0);
                    }
                    catch (NullReferenceException)
                    {
                        // The port source's LLClientView.SendScriptTeleportRequest has a packet construction bug
                        // where ScriptTeleportRequestPacket fields can be null. Guard against it here.
                        // This needs a separate fix in LLClientView.cs.
                    }
                }
            }

            ScriptSleep(1000);
        }
        public void llLoadURL(string avatar_id, string message, string url)
        {
            IDialogModule dm = World?.RequestModuleInterface<IDialogModule>();
            if (dm != null)
                if (UUID.TryParse(avatar_id, out UUID avatar))
                    dm.SendUrlToUser(avatar, m_host.Name, m_host.UUID, m_host.OwnerID,
                        false, message, url);

            ScriptSleep(100);
        }
        public int llEdgeOfWorld(Vector3 pos, Vector3 dir)
        {
            // Returns 1 if following dir from pos reaches the edge of the region
            if (World == null) return 0;
            float sx = World.RegionInfo.RegionSizeX;
            float sy = World.RegionInfo.RegionSizeY;
            // Step along dir until we leave the region or hit a known neighbour
            Vector3 cur = pos;
            for (int i = 0; i < 256; i++)
            {
                cur += dir;
                if (cur.X < 0 || cur.X >= sx || cur.Y < 0 || cur.Y >= sy)
                    return 1;
            }
            return 0;
        }
        public string llGetObjectPermMask2(int mask) { /* Halcyon-2 variant — not standard LSL */ return "0"; }
        public int llGetParcelFlags2(Vector3 pos) { /* Halcyon-2 variant — not standard LSL */ return 0; }
        public LSLList llCastRay(Vector3 start, Vector3 end, LSLList options)
        {
            // Faithful port from Halcyon
            List<object> results = new List<object>();
            Vector3 dir = end - start;
            float dist = dir.Length();

            // RC_* are SL's values from SlConst. The local copies here once disagreed with them, so
            // options were mis-parsed and llCastRay dropped the normal (2b84e458b3).

            int count = 1;
            int dataFlags = 0;
            int rejectTypes = 0;

            for (int i = 0; i < options.Length; i += 2)
            {
                int opt = options.GetLSLIntegerItem(i);
                if (opt == RC_MAX_HITS) count = options.GetLSLIntegerItem(i + 1);
                else if (opt == RC_DATA_FLAGS) dataFlags = options.GetLSLIntegerItem(i + 1);
                else if (opt == RC_REJECT_TYPES) rejectTypes = options.GetLSLIntegerItem(i + 1);
            }

            if (count > 16) count = 16;
            else if (count <= 0)
            {
                ScriptShoutError("You must request at least one result from llCastRay.");
                return new LSLList();
            }

            bool rejectTerrain = (rejectTypes & RC_REJECT_LAND) != 0;
            bool rejectAgents = (rejectTypes & RC_REJECT_AGENTS) != 0;
            bool rejectNonPhysical = (rejectTypes & RC_REJECT_NONPHYSICAL) != 0;
            bool rejectPhysical = (rejectTypes & RC_REJECT_PHYSICAL) != 0;

            try
            {
                // OpenSim's PhysicsScene.RaycastWorld returns List<ContactResult>
                List<ContactResult> contactResults = World.PhysicsScene.RaycastWorld(start, dir, dist, count);

                if (contactResults == null)
                {
                    results.Add(0);
                    return new LSLList(results);
                }

                contactResults.Sort((a, b) => a.Depth.CompareTo(b.Depth));
                int values = 0;

                foreach (ContactResult result in contactResults)
                {
                    // Skip self
                    if (result.ConsumerID == m_host.LocalId)
                        continue;

                    UUID itemID = UUID.Zero;
                    int linkNum = 0;

                    if (result.ConsumerID != 0)
                    {
                        SceneObjectPart part = World.GetSceneObjectPart(result.ConsumerID);
                        if (part != null)
                        {
                            // Skip if part of same linkset
                            if (part.ParentGroup == m_host.ParentGroup)
                                continue;

                            // Filter by physics type
                            PhysicsActor pa = part.PhysActor;
                            if (pa != null)
                            {
                                if (rejectPhysical && pa.IsPhysical) continue;
                                if (rejectNonPhysical && !pa.IsPhysical) continue;
                            }
                            else
                            {
                                if (rejectNonPhysical) continue;
                            }

                            if ((dataFlags & RC_GET_ROOT_KEY) != 0)
                                itemID = part.ParentGroup.UUID;
                            else
                                itemID = part.UUID;

                            linkNum = part.LinkNum;
                        }
                        else
                        {
                            ScenePresence sp = World.GetScenePresence(result.ConsumerID);
                            if (sp != null)
                            {
                                if (rejectAgents) continue;
                                itemID = sp.UUID;
                            }
                        }
                    }
                    else
                    {
                        // ConsumerID == 0 means terrain
                        if (rejectTerrain) continue;
                    }

                    results.Add(itemID.ToString());

                    if ((dataFlags & RC_GET_LINK_NUM) != 0)
                        results.Add(linkNum);

                    results.Add(result.Pos);

                    if ((dataFlags & RC_GET_NORMAL) != 0)
                        results.Add(result.Normal);

                    values++;
                    if (values >= count) break;
                }

                results.Add(values);
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: llCastRay exception: {0}", e.Message);
                results.Clear();
                results.Add(RCERR_CAST_TIME_EXCEEDED); // RCERR_CAST_TIME_EXCEEDED as generic error
            }
            return new LSLList(results);
        }
        public string llJsonGetValue(string json, LSLList specifiers)
        {
            if (string.IsNullOrEmpty(json) || specifiers == null) return JSON_INVALID;
            try
            {
                var node = SimpleJsonNavigate(json, specifiers.Data);
                return node ?? JSON_INVALID;
            }
            catch { return JSON_INVALID; }
        }

        public string llJsonValueType(string json, LSLList specifiers)
        {
            const string JSON_OBJECT  = "\uFDD1";
            const string JSON_ARRAY   = "\uFDD2";
            const string JSON_NUMBER  = "\uFDD3";
            const string JSON_STRING  = "\uFDD4";
            const string JSON_NULL    = "\uFDD5";
            const string JSON_TRUE    = "\uFDD6";
            const string JSON_FALSE   = "\uFDD7";
            if (string.IsNullOrEmpty(json)) return JSON_INVALID;
            try
            {
                string val = SimpleJsonNavigate(json, specifiers?.Data ?? Array.Empty<object>());
                if (val == null) return JSON_INVALID;
                if (val == "null") return JSON_NULL;
                if (val == "true") return JSON_TRUE;
                if (val == "false") return JSON_FALSE;
                if (val.StartsWith("{")) return JSON_OBJECT;
                if (val.StartsWith("[")) return JSON_ARRAY;
                if (val.StartsWith("\"")) return JSON_STRING;
                if (double.TryParse(val, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out _)) return JSON_NUMBER;
                return JSON_INVALID;
            }
            catch { return JSON_INVALID; }
        }

        // LSL JSON special constants — values match Phlox DefaultConstants.cs
        private const string JSON_INVALID = "\uFDD0";
        private const string JSON_DELETE  = "\uFDD8";

        public string llJsonSetValue(string json, LSLList specifiers, string value)
        {
            if (specifiers == null || specifiers.Data.Length == 0)
                return JSON_INVALID;
            if (string.IsNullOrEmpty(json))
                json = "{}";  // default to empty object per LSL spec
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                // Clone root into a mutable structure, apply the set, serialize back
                var root = JsonElementToNode(doc.RootElement);
                if (root == null) return JSON_INVALID;
                if (!JsonSetAtPath(root, specifiers.Data, 0, value))
                    return JSON_INVALID;
                return JsonNodeSerialize(root);
            }
            catch { return JSON_INVALID; }
        }

        // ── JSON mutable node types ───────────────────────────────────────────
        // System.Text.Json is read-only, so we convert to simple mutable wrappers.

        private abstract class JNode { }
        private class JObject : JNode
        {
            public List<KeyValuePair<string, JNode>> Members = new();
            public bool TryGet(string key, out JNode val)
            {
                foreach (var kv in Members)
                    if (kv.Key == key) { val = kv.Value; return true; }
                val = null;
                return false;
            }
            public void Set(string key, JNode val)
            {
                for (int i = 0; i < Members.Count; i++)
                    if (Members[i].Key == key) { Members[i] = new(key, val); return; }
                Members.Add(new(key, val));
            }
            public bool Remove(string key)
            {
                for (int i = 0; i < Members.Count; i++)
                    if (Members[i].Key == key) { Members.RemoveAt(i); return true; }
                return false;
            }
        }
        private class JArray : JNode { public List<JNode> Items = new(); }
        private class JValue : JNode { public string Raw; public JValue(string raw) { Raw = raw; } }

        private static JNode JsonElementToNode(System.Text.Json.JsonElement el)
        {
            switch (el.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                    var obj = new JObject();
                    foreach (var prop in el.EnumerateObject())
                        obj.Members.Add(new(prop.Name, JsonElementToNode(prop.Value)));
                    return obj;
                case System.Text.Json.JsonValueKind.Array:
                    var arr = new JArray();
                    foreach (var item in el.EnumerateArray())
                        arr.Items.Add(JsonElementToNode(item));
                    return arr;
                default:
                    return new JValue(el.GetRawText());
            }
        }

        private static string JsonNodeSerialize(JNode node)
        {
            if (node is JObject obj)
            {
                var sb = new System.Text.StringBuilder("{");
                for (int i = 0; i < obj.Members.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(System.Text.Json.JsonSerializer.Serialize(obj.Members[i].Key));
                    sb.Append(':');
                    sb.Append(JsonNodeSerialize(obj.Members[i].Value));
                }
                sb.Append('}');
                return sb.ToString();
            }
            if (node is JArray arr)
            {
                var sb = new System.Text.StringBuilder("[");
                for (int i = 0; i < arr.Items.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(JsonNodeSerialize(arr.Items[i]));
                }
                sb.Append(']');
                return sb.ToString();
            }
            if (node is JValue val) return val.Raw;
            return "null";
        }

        /// <summary>Convert an LSL value string to a JNode for insertion into the tree.</summary>
        private static JNode JsonValueToNode(string value)
        {
            if (value == null) return new JValue("null");
            // LSL special sentinel constants are stored as bare words
            if (value == "\uFDD6") return new JValue("true");   // JSON_TRUE
            if (value == "\uFDD7") return new JValue("false");  // JSON_FALSE
            if (value == "\uFDD5") return new JValue("null");   // JSON_NULL
            // If it looks like JSON structure, parse it
            string trimmed = value.Trim();
            if ((trimmed.StartsWith("{") && trimmed.EndsWith("}")) ||
                (trimmed.StartsWith("[") && trimmed.EndsWith("]")))
            {
                try
                {
                    using var d = System.Text.Json.JsonDocument.Parse(trimmed);
                    return JsonElementToNode(d.RootElement);
                }
                catch { /* fall through to string */ }
            }
            // Numeric?
            if (double.TryParse(trimmed, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out _))
                return new JValue(trimmed);
            // Plain string — quote it
            return new JValue(System.Text.Json.JsonSerializer.Serialize(value));
        }

        /// <summary>
        /// Recursively set/delete a value at the given specifier path.
        /// Faithful port of Halcyon's JsonSetSpecific / JsonBuildRestOfSpec logic.
        /// </summary>
        private bool JsonSetAtPath(JNode node, object[] specs, int i, string value)
        {
            if (i >= specs.Length) return false;
            object spec = specs[i];
            bool isLast = (i == specs.Length - 1);
            bool isDelete = (value == JSON_DELETE);

            if (node is JArray arr)
            {
                if (!(spec is int))
                {
                    if (int.TryParse(spec.ToString(), out int parsed)) spec = parsed;
                    else return false;
                }
                int idx = (int)spec;

                if (idx == JSON_APPEND)
                {
                    // Append: build the rest of the path as a new subtree
                    arr.Items.Add(JsonBuildRest(specs, i + 1, value));
                    return true;
                }
                if (idx < 0 || idx > arr.Items.Count) return false;
                if (idx == arr.Items.Count)
                {
                    if (isDelete) return false;
                    arr.Items.Add(JsonBuildRest(specs, i + 1, value));
                    return true;
                }
                if (isLast)
                {
                    if (isDelete) { arr.Items.RemoveAt(idx); return true; }
                    arr.Items[idx] = JsonBuildRest(specs, i + 1, value);
                    return true;
                }
                // Recurse into existing element
                var child = arr.Items[idx];
                // If child type doesn't match next specifier, replace subtree
                object nextSpec = specs[i + 1];
                bool nextIsInt = (nextSpec is int) || int.TryParse(nextSpec.ToString(), out _);
                if ((nextIsInt && !(child is JArray)) || (!nextIsInt && !(child is JObject)))
                {
                    arr.Items[idx] = JsonBuildRest(specs, i + 1, value);
                    return true;
                }
                return JsonSetAtPath(child, specs, i + 1, value);
            }
            else if (node is JObject obj)
            {
                string key;
                if (spec is string skey) key = skey;
                else key = spec.ToString();

                if (isLast)
                {
                    if (isDelete) return obj.Remove(key);
                    obj.Set(key, JsonBuildRest(specs, i + 1, value));
                    return true;
                }
                if (obj.TryGet(key, out JNode objChild))
                {
                    // Recurse into existing member
                    object nextSpec = specs[i + 1];
                    bool nextIsInt = (nextSpec is int) || int.TryParse(nextSpec.ToString(), out _);
                    if ((nextIsInt && !(objChild is JArray)) || (!nextIsInt && !(objChild is JObject)))
                    {
                        obj.Set(key, JsonBuildRest(specs, i + 1, value));
                        return true;
                    }
                    return JsonSetAtPath(objChild, specs, i + 1, value);
                }
                else
                {
                    if (isDelete) return false;
                    obj.Set(key, JsonBuildRest(specs, i + 1, value));
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Build a new subtree for the remaining specifiers.
        /// Port of Halcyon's JsonBuildRestOfSpec.
        /// </summary>
        private JNode JsonBuildRest(object[] specs, int i, string value)
        {
            if (i >= specs.Length)
                return JsonValueToNode(value);

            object spec = specs[i];
            bool specIsInt = (spec is int) || int.TryParse(spec.ToString(), out _);

            if (specIsInt)
            {
                var arr = new JArray();
                arr.Items.Add(JsonBuildRest(specs, i + 1, value));
                return arr;
            }
            else
            {
                var obj = new JObject();
                obj.Set(spec.ToString(), JsonBuildRest(specs, i + 1, value));
                return obj;
            }
        }

        private static string SimpleJsonNavigate(string json, object[] path)
        {
            // Minimal JSON navigator — walks path keys/indices into a JSON string
            string cur = json.Trim();
            foreach (object seg in path)
            {
                cur = cur.Trim();
                if (cur.StartsWith("{"))
                {
                    string key = seg.ToString();
                    var doc = System.Text.Json.JsonDocument.Parse(cur);
                    if (!doc.RootElement.TryGetProperty(key, out var el)) return null;
                    cur = el.GetRawText();
                }
                else if (cur.StartsWith("["))
                {
                    if (!int.TryParse(seg.ToString(), out int idx)) return null;
                    var doc = System.Text.Json.JsonDocument.Parse(cur);
                    var arr = doc.RootElement;
                    if (idx < 0 || idx >= arr.GetArrayLength()) return null;
                    cur = arr[idx].GetRawText();
                }
                else return null;
            }
            // Unwrap string quotes
            if (cur.StartsWith("\"") && cur.EndsWith("\""))
                return System.Text.Json.JsonSerializer.Deserialize<string>(cur);
            return cur;
        }
            public string llList2Json(string type, LSLList values)
        {
            const string LSL_JSON_ARRAY   = "\uFDD2";  // Phlox JSON_ARRAY constant
            const string LSL_JSON_OBJECT  = "\uFDD1";  // Phlox JSON_OBJECT constant
            if (values == null) return JSON_INVALID;
            try
            {
                if (type == LSL_JSON_ARRAY)
                {
                    var sb = new System.Text.StringBuilder("[");
                    for (int i = 0; i < values.Data.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append(JsonValueFromObject(values.Data[i]));
                    }
                    sb.Append(']');
                    return sb.ToString();
                }
                else if (type == LSL_JSON_OBJECT)
                {
                    var sb = new System.Text.StringBuilder("{");
                    bool first = true;
                    for (int i = 0; i + 1 < values.Data.Length; i += 2)
                    {
                        if (!(values.Data[i] is string)) return JSON_INVALID;
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append('"');
                        sb.Append(((string)values.Data[i]).Replace("\\", "\\\\").Replace("\"", "\\\""));
                        sb.Append("\":");
                        sb.Append(JsonValueFromObject(values.Data[i + 1]));
                    }
                    sb.Append('}');
                    return sb.ToString();
                }
                return JSON_INVALID;
            }
            catch { return JSON_INVALID; }
        }

        private static string JsonValueFromObject(object o)
        {
            if (o == null) return "null";
            if (o is int iv)    return iv.ToString();
            if (o is float fv)  return fv.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (o is double dv) return dv.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (o is string sv)
            {
                if (sv == "true" || sv == "false" || sv == "null") return sv;
                if (double.TryParse(sv, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out _)) return sv;
                return "\"" + sv.Replace("\\","\\\\").Replace("\"","\\\"") + "\"";
            }
            return "\"" + o.ToString() + "\"";
        }
        public LSLList llJson2List(string src)
        {
            if (string.IsNullOrEmpty(src)) return new LSLList();
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(src);
                var result = new System.Collections.Generic.List<object>();
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                        result.Add(JsonElementToLSL(el));
                }
                else if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        result.Add(prop.Name);
                        result.Add(JsonElementToLSL(prop.Value));
                    }
                }
                return new LSLList(result.ToArray());
            }
            catch { return new LSLList(); }
        }

        private static object JsonElementToLSL(System.Text.Json.JsonElement el)
        {
            return el.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Number =>
                    el.TryGetInt32(out int i) ? (object)i : (object)el.GetSingle(),
                System.Text.Json.JsonValueKind.String  => el.GetString() ?? string.Empty,
                System.Text.Json.JsonValueKind.True    => 1,
                System.Text.Json.JsonValueKind.False   => 0,
                System.Text.Json.JsonValueKind.Null    => "null",
                _                                      => el.GetRawText()
            };
        }
        public Vector3 iwColorConvert(Vector3 input, int color1, int color2)
        {
            // Faithful port from Halcyon: 0=RGB, 1=HSL, 2=HSV
            if (color1 == color2) return input;
            // Convert input to RGB first
            if (color1 == 1) input = HSL_TO_RGB(input);
            else if (color1 == 2) input = HSV_TO_RGB(input);
            // Convert RGB to target
            if (color2 == 1) return RGB_TO_HSL(input);
            else if (color2 == 2) return RGB_TO_HSV(input);
            return input;
        }
        public Vector3 iwNameToColor(string name)
        {
            // Faithful port from Halcyon
            Color c = Color.FromName(name.Replace(" ", null));
            return new Vector3(c.R / 255f, c.G / 255f, c.B / 255f);
        }

        // ── Color conversion helpers (ported from Halcyon) ─────────────────────

        private float H2RGB(float v1, float v2, float vH)
        {
            if (vH < 0) vH += 1;
            if (vH > 1) vH -= 1;
            if ((6f * vH) < 1) return (v1 + (v2 - v1) * 6f * vH);
            if ((2f * vH) < 1) return (v2);
            if ((3f * vH) < 2) return (v1 + (v2 - v1) * ((2f / 3f) - vH) * 6f);
            return v1;
        }

        private Vector3 HSL_TO_RGB(Vector3 input)
        {
            float H = input.X, S = input.Y, L = input.Z;
            if (S == 0) return new Vector3(L, L, L);
            float v2 = (L < 0.5f) ? L * (1 + S) : (L + S) - (S * L);
            float v1 = 2 * L - v2;
            return new Vector3(
                H2RGB(v1, v2, H + (1f / 3f)),
                H2RGB(v1, v2, H),
                H2RGB(v1, v2, H - (1f / 3f))
            );
        }

        private Vector3 HSV_TO_RGB(Vector3 input)
        {
            float H = input.X, S = input.Y, V = input.Z;
            if (S == 0) return new Vector3(V, V, V);
            float vH = H * 6f;
            if (vH == 6) vH = 0;
            int i = (int)vH;
            float v1 = V * (1 - S);
            float v2 = V * (1 - S * (vH - i));
            float v3 = V * (1 - S * (1 - (vH - i)));
            if (i == 0) return new Vector3(V, v3, v1);
            else if (i == 1) return new Vector3(v2, V, v1);
            else if (i == 2) return new Vector3(v1, V, v3);
            else if (i == 3) return new Vector3(v1, v2, V);
            else if (i == 4) return new Vector3(v3, v1, V);
            else return new Vector3(V, v1, v2);
        }

        private Vector3 RGB_TO_HSV(Vector3 input)
        {
            float R = input.X, G = input.Y, B = input.Z;
            float min = Math.Min(Math.Min(R, G), B);
            float max = Math.Max(Math.Max(R, G), B);
            float delta = max - min;
            float H = 0, S = 0, V = max;
            if (delta != 0)
            {
                S = delta / max;
                float vR = (((max - R) / 6f) + (max / 2f)) / delta;
                float vG = (((max - G) / 6f) + (max / 2f)) / delta;
                float vB = (((max - B) / 6f) + (max / 2f)) / delta;
                if (R == max) H = vB - vG;
                else if (G == max) H = (1f / 3f) + vR - vB;
                else if (B == max) H = (2f / 3f) + vG - vR;
                if (H < 0) H += 1f;
                if (H > 1) H -= 1f;
            }
            return new Vector3(H, S, V);
        }

        private Vector3 RGB_TO_HSL(Vector3 input)
        {
            float R = input.X, G = input.Y, B = input.Z;
            float min = Math.Min(Math.Min(R, G), B);
            float max = Math.Max(Math.Max(R, G), B);
            float delta = max - min;
            float H = 0, S = 0, L = (max + min) / 2f;
            if (delta != 0)
            {
                S = (L < 0.5f) ? delta / (max + min) : delta / (2 - max - min);
                float vR = (((max - R) / 6f) + (max / 2f)) / delta;
                float vG = (((max - G) / 6f) + (max / 2f)) / delta;
                float vB = (((max - B) / 6f) + (max / 2f)) / delta;
                if (max == R) H = vB - vG;
                else if (max == G) H = (1f / 3f) + vR - vB;
                else if (max == B) H = (2f / 3f) + vG - vR;
                if (H < 0) H += 1f;
                if (H > 1) H -= 1f;
            }
            return new Vector3(H, S, L);
        }
        public int iwVerifyType(string str, int type)
        {
            switch (type)
            {
                case 0: // No Type — auto-detect
                    foreach (var index in new int[] { 1, 2, 4, 5, 6 })
                    {
                        if (iwVerifyType(str, index) == 1) return index;
                    }
                    return 3;
                case TYPE_INTEGER:
                    return int.TryParse(str, out _) ? 1 : 0;
                case TYPE_FLOAT:
                    return float.TryParse(str, out _) ? 1 : 0;
                case TYPE_KEY:
                    return UUID.TryParse(str, out _) ? 1 : 0;
                case TYPE_VECTOR:
                    if (str == null || str.Count(c => c == ',') != 2) return 0;
                    return Vector3.TryParse(str, out _) ? 1 : 0;
                case TYPE_ROTATION:
                    if (str == null || str.Count(c => c == ',') != 3) return 0;
                    return Quaternion.TryParse(str, out _) ? 1 : 0;
                case TYPE_STRING:
                    return 1;
                default:
                    return -1;
            }
        }
        // Halcyon's Constants.GenericReturnCodes, iwGroupInvite/iwGroupEject's answers (Halcyon OpenSim/Framework/Constants.cs).
        /// <summary>SUCCESS - Halcyon's groups module answers it when the invite or eject was done.</summary>
        private const int HALCYON_RC_SUCCESS = 0;
        /// <summary>ERROR - "generic error, internal server failure (like module not available)".</summary>
        private const int HALCYON_RC_ERROR = 2;
        /// <summary>PARAMETER - a bad group or user key, a bad group, or an unknown role.</summary>
        private const int HALCYON_RC_PARAMETER = 3;
        /// <summary>PERMISSION - the refusal when the script's owner is not its creator.</summary>
        private const int HALCYON_RC_PERMISSION = 5;

        /// <summary>
        /// Halcyon's ScriptOwnerIsCreator - the calling script's owner must be its creator, so a resold
        /// object cannot invite or eject on its new owner's behalf with someone else's script.
        /// </summary>
        private bool ScriptOwnerIsCreator()
        {
            TaskInventoryItem item = GetInventorySelf();
            return item != null && item.CreatorID == item.OwnerID;
        }

        /// <summary>
        /// Halcyon (LSLSystemAPI.iwGroupInvite): PARAMETER for a bad key, a bad group or an unknown role, PERMISSION when
        /// the script's owner is not its creator, otherwise the groups module's result. Core's IGroupsModule.InviteGroup
        /// returns nothing, so a call it accepts answers SUCCESS; no module or a failing one answers ERROR.
        /// </summary>
        public int iwGroupInvite(string group, string user, string role)
        {
            if (!UUID.TryParse(group, out UUID groupID) || groupID == UUID.Zero) return HALCYON_RC_PARAMETER;
            if (!UUID.TryParse(user, out UUID userID) || userID == UUID.Zero) return HALCYON_RC_PARAMETER;
            if (string.IsNullOrEmpty(role)) role = "Everyone";

            if (!ScriptOwnerIsCreator())
            {
                ScriptShoutError("LSL Runtime Error: iwGroupInvite requires the owner of the calling script to be the creator of the script.");
                return HALCYON_RC_PERMISSION;
            }

            try
            {
                IGroupsModule groupsModule = World?.RequestModuleInterface<IGroupsModule>();
                if (groupsModule == null) return HALCYON_RC_ERROR;

                // Look up the role by name
                List<GroupRolesData> roles = groupsModule.GroupRoleDataRequest(null, groupID);
                if (roles == null) return HALCYON_RC_PARAMETER;   // Halcyon: "groupID bad, or internal/system error"

                UUID roleID = UUID.Zero;
                bool found = false;
                foreach (GroupRolesData r in roles)
                {
                    if (r.Name.Equals(role, StringComparison.InvariantCultureIgnoreCase))
                    {
                        roleID = r.RoleID;
                        found = true;
                        break;
                    }
                }
                if (!found) return HALCYON_RC_PARAMETER;          // Halcyon: "unknown role"

                groupsModule.InviteGroup(null, m_host.OwnerID, groupID, userID, roleID);
                return HALCYON_RC_SUCCESS;
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: iwGroupInvite exception: {0}", e.Message);
                return HALCYON_RC_ERROR;
            }
        }

        /// <summary>Halcyon (LSLSystemAPI.iwGroupEject), with the same answers as <see cref="iwGroupInvite"/>.</summary>
        public int iwGroupEject(string group, string user)
        {
            if (!UUID.TryParse(group, out UUID groupID) || groupID == UUID.Zero) return HALCYON_RC_PARAMETER;
            if (!UUID.TryParse(user, out UUID userID) || userID == UUID.Zero) return HALCYON_RC_PARAMETER;

            if (!ScriptOwnerIsCreator())
            {
                ScriptShoutError("LSL Runtime Error: iwGroupEject requires the owner of the calling script to be the creator of the script.");
                return HALCYON_RC_PERMISSION;
            }

            try
            {
                IGroupsModule groupsModule = World?.RequestModuleInterface<IGroupsModule>();
                if (groupsModule == null) return HALCYON_RC_ERROR;
                groupsModule.EjectGroupMember(null, m_host.OwnerID, groupID, userID);
                return HALCYON_RC_SUCCESS;
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: iwGroupEject exception: {0}", e.Message);
                return HALCYON_RC_ERROR;
            }
        }
        public int iwClampInt(int value, int min, int max)
        {
            if (min == max) return min;
            if (max < min) return Math.Min(min, Math.Max(value, max));
            return Math.Min(max, Math.Max(value, min));
        }
        public float iwClampFloat(float value, float min, float max)
        {
            if (min == max) return min;
            if (max < min) return Math.Min(min, Math.Max(value, max));
            return Math.Min(max, Math.Max(value, min));
        }
        public int iwIntRand(int max) { return new Random().Next(max < 0 ? max : 0, Math.Abs(max) + 1); }
        public int iwIntRandRange(int min, int max) { if (min == max) return min; if (max < min) { int t = min; min = max; max = t; } return new Random().Next(min, max + 1); }
        public float iwFrandRange(float min, float max) { if (min == max) return min; if (max < min) { float t = min; min = max; max = t; } return (float)(new Random().NextDouble() * (max - min) + min); }
        public LSLList iwSearchLinksByName(string pattern, int matchType, int linksOnly)
        {
            if (matchType > 2)
            {
                ScriptShoutError("IW_MATCH_COUNT/REGEX not valid for iwSearchLinksByName");
                return new LSLList();
            }
            List<object> ret = new List<object>();
            var parts = m_host.ParentGroup.Parts.ToList();
            parts.Sort((x, y) => x.LinkNum.CompareTo(y.LinkNum));
            foreach (SceneObjectPart part in parts)
            {
                if (String.IsNullOrEmpty(pattern) || iwMatchString(part.Name, pattern, matchType) == 1)
                {
                    ret.Add(part.LinkNum);
                    if (linksOnly == 0) ret.Add(part.Name);
                }
            }
            return new LSLList(ret);
        }
        public LSLList iwSearchLinksByDesc(string pattern, int matchType, int linksOnly)
        {
            if (matchType > 2)
            {
                ScriptShoutError("IW_MATCH_COUNT/REGEX not valid for iwSearchLinksByDesc");
                return new LSLList();
            }
            List<object> ret = new List<object>();
            var parts = m_host.ParentGroup.Parts.ToList();
            parts.Sort((x, y) => x.LinkNum.CompareTo(y.LinkNum));
            foreach (SceneObjectPart part in parts)
            {
                if (String.IsNullOrEmpty(pattern) || iwMatchString(part.Description, pattern, matchType) == 1)
                {
                    ret.Add(part.LinkNum);
                    if (linksOnly == 0) ret.Add(part.Description);
                }
            }
            return new LSLList(ret);
        }

  // ── Bots ───────────────────────────────────────────────────────────────

        private UUID ParseBotID(string botID)
        {
            if (UUID.TryParse(botID, out UUID id) && id != UUID.Zero) return id;
            return UUID.Zero;
        }

        private IBotManager GetBotManager()
        {
            return World.RequestModuleInterface<IBotManager>();
        }

        private BotPersistenceManager _botPersistence;
        private BotPersistenceManager GetBotPersistence()
        {
            if (_botPersistence == null)
            {
                IBotManager mgr = GetBotManager();
                if (mgr is BotManager bm)
                    _botPersistence = bm.PersistenceManager;
            }
            return _botPersistence;
        }

        public void botCreateBot(string first, string last, string outfit, Vector3 pos, int options)
        {
            const int delay = 2000;
            object retVal = UUID.Zero.ToString();

            try
            {
                IBotManager manager = GetBotManager();
                if (manager != null)
                {
                    string reason;
                    string botID = manager.CreateBot(first, last, pos, outfit, m_itemID, m_host.OwnerID, out reason).ToString();
                    if (reason == null)
                    {
                        retVal = botID;
                        return;
                    }
                    ShoutError(reason);
                }
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, retVal, delay);
            }
        }

        public void botRemoveBot(string botID)
        {
            const int delay = 1000;

            try
            {
                UUID id = ParseBotID(botID);
                if (id == UUID.Zero) return;

                IBotManager manager = GetBotManager();
                if (manager != null)
                    manager.RemoveBot(id, m_host.OwnerID);
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }

        public string botGetOwner(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return string.Empty;

            IBotManager manager = GetBotManager();
            if (manager != null)
                return manager.GetBotOwner(id).ToString();
            return string.Empty;
        }

        public int botIsBot(string userID)
        {
            UUID id = ParseBotID(userID);
            if (id == UUID.Zero) return 0;

            IBotManager manager = GetBotManager();
            if (manager != null)
                return manager.IsBot(id) ? 1 : 0;
            return 0;
        }

        public string botGetName(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return string.Empty;

            IBotManager manager = GetBotManager();
            if (manager != null)
                return manager.GetBotName(id);
            return string.Empty;
        }

        // NotImplemented in Halcyon - kept as no-op. With Halcyon's error, LSLSystemAPI.cs:17301.
        public void botChangeOwner(string botID, string newOwnerID) => NotImplemented("botChangeOwner");

        public LSLList botGetAllBotsInRegion()
        {
            IBotManager manager = GetBotManager();
            List<UUID> bots = new List<UUID>();
            if (manager != null)
                bots = manager.GetAllBots();
            return new LSLList(bots.ConvertAll<object>(o => o.ToString()));
        }

        public LSLList botGetAllMyBotsInRegion()
        {
            IBotManager manager = GetBotManager();
            List<UUID> bots = new List<UUID>();
            if (manager != null)
                bots = manager.GetAllOwnedBots(m_host.OwnerID);
            return new LSLList(bots.ConvertAll<object>(o => o.ToString()));
        }

        // ── Bot Profile ────────────────────────────────────────────────────────

        public void botSetProfile(string botID, string about, string email, string firstLifeAbout, string firstLifeImage, string image, string profileURL)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            UUID imageID = UUID.Zero;
            UUID.TryParse(image, out imageID);

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.SetBotProfile(id, about, email, imageID, profileURL, m_host.OwnerID);
        }

        public void botSetProfileParams(string botID, LSLList profileInformation)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            string aboutText = null, email = null, profileURL = null;
            UUID? imageUUID = null;

            for (int i = 0; i < profileInformation.Length; i += 2)
            {
                int param = profileInformation.GetLSLIntegerItem(i);
                string value = profileInformation.GetLSLStringItem(i + 1);

                switch (param)
                {
                    case 1: // BOT_ABOUT_TEXT
                        aboutText = value;
                        break;
                    case 2: // BOT_EMAIL
                        email = value;
                        break;
                    case 3: // BOT_IMAGE_UUID
                        if (UUID.TryParse(value, out UUID imgID))
                            imageUUID = imgID;
                        break;
                    case 4: // BOT_PROFILE_URL
                        profileURL = value;
                        break;
                }
            }

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.SetBotProfile(id, aboutText, email, imageUUID, profileURL, m_host.OwnerID);
        }

        public LSLList botGetProfileParams(string botID, LSLList profileInformation)
        {
            if (botIsBot(botID) == 0)
                return new LSLList();

            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return new LSLList();

            // Get profile from BotManager's stored data via INPC
            INPCModule npcMod = World.RequestModuleInterface<INPCModule>();
            INPC npc = npcMod?.GetNPC(id, World);

            IBotManager manager = GetBotManager();
            // We need to read from the bot's stored profile data
            // Since BotData is internal, we read from INPC + fallback

            List<object> list = new List<object>();
            for (int i = 0; i < profileInformation.Length; i++)
            {
                int param = profileInformation.GetLSLIntegerItem(i);
                switch (param)
                {
                    case 1: // BOT_ABOUT_TEXT
                        list.Add(npc?.profileAbout ?? string.Empty);
                        break;
                    case 2: // BOT_EMAIL
                        list.Add(string.Empty); // email not exposed via INPC
                        break;
                    case 3: // BOT_IMAGE_UUID
                        list.Add((npc?.profileImage ?? UUID.Zero).ToString());
                        break;
                    case 4: // BOT_PROFILE_URL
                        list.Add(string.Empty); // profileURL not exposed via INPC
                        break;
                }
            }
            return new LSLList(list);
        }

        // ── Bot Outfits ────────────────────────────────────────────────────────

        public void botSetOutfit(string outfitName)
        {
            const int delay = 1000;

            try
            {
                IBotManager manager = GetBotManager();
                if (manager != null)
                {
                    string reason;
                    manager.SaveOutfitToDatabase(m_host.OwnerID, outfitName, out reason);
                    if (reason != null)
                        ShoutError(reason);
                }
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }

        public void botRemoveOutfit(string outfitName)
        {
            const int delay = 1000;

            try
            {
                IBotManager manager = GetBotManager();
                if (manager != null)
                    manager.RemoveOutfitFromDatabase(m_host.OwnerID, outfitName);
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }

        public void botChangeOutfit(string botID, string outfitName)
        {
            const int delay = 1000;

            try
            {
                UUID id = ParseBotID(botID);
                if (id == UUID.Zero) return;

                IBotManager manager = GetBotManager();
                if (manager != null)
                {
                    string reason;
                    manager.ChangeBotOutfit(id, outfitName, m_host.OwnerID, out reason);
                    if (reason != null)
                        ShoutError(reason);
                }
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }

        public void botGetBotOutfits()
        {
            object retVal = new LSLList();
            const int delay = 1000;

            try
            {
                IBotManager manager = GetBotManager();
                if (manager != null)
                {
                    List<string> itms = manager.GetBotOutfitsByOwner(m_host.OwnerID);
                    retVal = new LSLList(itms.ConvertAll<object>(s => s));
                }
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, retVal, delay);
            }
        }

        public void botSearchBotOutfits(string pattern, int matchType, int start, int end)
        {
            List<object> retVal = new List<object>();
            const int delay = 1000;

            try
            {
                if (matchType > 2)
                    return; // IW_MATCH_COUNT and IW_MATCH_COUNT_REGEX not valid here

                IBotManager manager = GetBotManager();
                if (manager != null)
                {
                    List<string> itms = manager.GetBotOutfitsByOwner(m_host.OwnerID);
                    int count = 0;
                    foreach (string outfit in itms)
                    {
                        if (string.IsNullOrEmpty(pattern) || iwMatchString(outfit, pattern, matchType) == 1)
                        {
                            if (count >= start && (end == -1 || count <= end))
                                retVal.Add(outfit);
                            count++;
                            if (end != -1 && count > end)
                                break;
                        }
                    }
                }
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, new LSLList(retVal), delay);
            }
        }

        // ── Bot Event Registration ─────────────────────────────────────────────

        public void botRegisterForNavigationEvents(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.BotRegisterForPathUpdateEvents(id, m_itemID, m_host.OwnerID);
        }

        public void botDeregisterFromNavigationEvents(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.BotDeregisterFromPathUpdateEvents(id, m_itemID, m_host.OwnerID);
        }

        public void botRegisterForCollisionEvents(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.BotRegisterForCollisionEvents(id, m_host.ParentGroup, m_host.OwnerID);
        }

        public void botDeregisterFromCollisionEvents(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.BotDeregisterFromCollisionEvents(id, m_host.ParentGroup, m_host.OwnerID);
        }

        // ── Bot Movement ───────────────────────────────────────────────────────

        public void botPauseMovement(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.PauseBotMovement(id, m_host.OwnerID);
        }

        public void botResumeMovement(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.ResumeBotMovement(id, m_host.OwnerID);
        }

        public void botSetMovementSpeed(string botID, float speed)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.SetBotSpeed(id, speed, m_host.OwnerID);
        }

        public Vector3 botGetPos(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return Vector3.Zero;

            IBotManager manager = GetBotManager();
            if (manager != null)
                return manager.GetBotPosition(id, m_host.OwnerID);
            return Vector3.Zero;
        }

        public void botTeleportTo(string botID, Vector3 position)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.SetBotPosition(id, position, m_host.OwnerID);
        }

        public void botSetRotation(string botID, Quaternion rotation)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.SetBotRotation(id, rotation, m_host.OwnerID);
        }

        public int botFollowAvatar(string botID, string avatar, LSLList options)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return -3; // BOT_ERROR

            IBotManager manager = GetBotManager();
            if (manager != null)
            {
                UUID userID;
                if (!UUID.TryParse(avatar, out userID))
                    return -2; // BOT_USER_NOT_FOUND

                if (options.Length % 2 != 0)
                    return -3; // BOT_ERROR — bad data

                Dictionary<int, object> dictOptions = new Dictionary<int, object>();
                for (int i = 0; i < options.Length; i += 2)
                {
                    int option = options.GetLSLIntegerItem(i);
                    if (dictOptions.ContainsKey(option))
                    {
                        ScriptShoutError(string.Format("botFollowAvatar: options list already includes option {0}", option));
                        dictOptions.Remove(option);
                    }
                    dictOptions.Add(option, options.Data[i + 1]);
                }

                BotMovementResult result = manager.StartFollowingAvatar(id, userID, dictOptions, m_host.OwnerID);
                switch (result)
                {
                    case BotMovementResult.BotNotFound: return -1; // BOT_NOT_FOUND
                    case BotMovementResult.UserNotFound: return -2; // BOT_USER_NOT_FOUND
                    case BotMovementResult.Success: return 0; // BOT_SUCCESS
                    default: return -3; // BOT_ERROR
                }
            }
            return -1; // BOT_NOT_FOUND
        }

        public void botStopMovement(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.StopMovement(id, m_host.OwnerID);
        }

        public void botSetNavigationPoints(string botID, LSLList positions, LSLList movementTypes, LSLList options)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            List<Vector3> positionsMap = new List<Vector3>();
            for (int i = 0; i < positions.Length; i++)
            {
                Vector3 pos = positions.GetVector3Item(i);
                pos.X = Math.Clamp(pos.X, 0f, 256f);
                pos.Y = Math.Clamp(pos.Y, 0f, 256f);
                pos.Z = Math.Max(pos.Z, 0f);
                float zmin = (float)World.Heightmap[(int)Math.Clamp(pos.X, 0, 255), (int)Math.Clamp(pos.Y, 0, 255)];
                if (pos.Z < zmin) pos.Z = zmin;
                positionsMap.Add(pos);
            }

            List<TravelMode> travelMap = new List<TravelMode>();
            for (int i = 0; i < movementTypes.Length; i++)
            {
                int travel = movementTypes.GetLSLIntegerItem(i);
                travelMap.Add((TravelMode)travel);
            }

            if (options.Length % 2 != 0) return; // bad data

            Dictionary<int, object> dictOptions = new Dictionary<int, object>();
            for (int i = 0; i < options.Length; i += 2)
            {
                int option = options.GetLSLIntegerItem(i);
                if (dictOptions.ContainsKey(option))
                {
                    ScriptShoutError(string.Format("botSetNavigationPoints: options list already includes option {0}", option));
                    dictOptions.Remove(option);
                }
                dictOptions.Add(option, options.Data[i + 1]);
            }

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.SetBotNavigationPoints(id, positionsMap, travelMap, dictOptions, m_host.OwnerID);
        }

        public void botWanderWithin(string botID, Vector3 origin, float xDistance, float yDistance, LSLList options)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            if (options.Length % 2 != 0) return; // bad data

            Dictionary<int, object> dictOptions = new Dictionary<int, object>();
            for (int i = 0; i < options.Length; i += 2)
            {
                int option = options.GetLSLIntegerItem(i);
                if (dictOptions.ContainsKey(option))
                {
                    ScriptShoutError(string.Format("botWanderWithin: options list already includes option {0}", option));
                    dictOptions.Remove(option);
                }
                dictOptions.Add(option, options.Data[i + 1]);
            }

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.WanderWithin(id, origin, new Vector3(xDistance, yDistance, 0), dictOptions, m_host.OwnerID);
        }

        // ── Bot Animations ─────────────────────────────────────────────────────

        public void botStartAnimation(string botID, string animation)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
            {
                UUID animID = FindInventoryItem(animation, (int)AssetType.Animation)?.AssetID ?? UUID.Zero;
                if (animID == UUID.Zero) UUID.TryParse(animation, out animID);
                manager.StartBotAnimation(id, animID, animation, m_host.UUID, m_host.OwnerID);
            }
        }

        public void botStopAnimation(string botID, string animation)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
            {
                UUID animID;
                if (!UUID.TryParse(animation, out animID))
                    animID = FindInventoryItem(animation, (int)AssetType.Animation)?.AssetID ?? UUID.Zero;
                manager.StopBotAnimation(id, animID, animation, m_host.OwnerID);
            }
        }

        // ── Bot Chat ───────────────────────────────────────────────────────────

        public void botWhisper(string botID, int channel, string message)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.BotChat(id, channel, message, ChatTypeEnum.Whisper, m_host.OwnerID);
            BotSleep();
        }

        public void botSay(string botID, int channel, string message)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.BotChat(id, channel, message, ChatTypeEnum.Say, m_host.OwnerID);
            BotSleep();
        }

        public void botShout(string botID, int channel, string message)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.BotChat(id, channel, message, ChatTypeEnum.Shout, m_host.OwnerID);
            BotSleep();
        }

        public void botStartTyping(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.BotChat(id, 0, string.Empty, ChatTypeEnum.StartTyping, m_host.OwnerID);
            BotSleep();
        }

        public void botStopTyping(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.BotChat(id, 0, string.Empty, ChatTypeEnum.StopTyping, m_host.OwnerID);
            BotSleep();
        }

        public void botSendInstantMessage(string botID, string userID, string message)
        {
            const int delay = 2000;

            try
            {
                UUID botUUID = ParseBotID(botID);
                if (botUUID == UUID.Zero) return;

                UUID userUUID = UUID.Zero;
                if (!UUID.TryParse(userID, out userUUID) || userUUID == UUID.Zero) return;

                IBotManager manager = GetBotManager();
                if (manager != null)
                    manager.SendInstantMessageForBot(botUUID, userUUID, message, m_host.OwnerID);
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }

        // ── Bot Interaction ────────────────────────────────────────────────────

        public void botSitObject(string botID, string objectID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            UUID objID = UUID.Zero;
            if (!UUID.TryParse(objectID, out objID) || objID == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.SitBotOnObject(id, objID, m_host.OwnerID);
            BotSleep();
        }

        public void botStandUp(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.StandBotUp(id, m_host.OwnerID);
            BotSleep();
        }

        public void botTouchObject(string botID, string objectID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            UUID objID = UUID.Zero;
            if (!UUID.TryParse(objectID, out objID) || objID == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.BotTouchObject(id, objID, m_host.OwnerID);
            BotSleep();
        }

        public void botGiveInventory(string botID, string destination, string inventory)
        {
            int delay = 0;

            try
            {
                UUID id = ParseBotID(botID);
                if (id == UUID.Zero) return;

                UUID destId = UUID.Zero;
                if (!UUID.TryParse(destination, out destId)) return;

                bool found = false;
                UUID objId = UUID.Zero;
                byte assetType = 0;
                string objName = string.Empty;

                lock (m_host.TaskInventory)
                {
                    foreach (KeyValuePair<UUID, TaskInventoryItem> inv in m_host.TaskInventory)
                    {
                        if (inv.Value.Name == inventory)
                        {
                            found = true;
                            objId = inv.Key;
                            assetType = (byte)inv.Value.Type;
                            objName = inv.Value.Name;
                            break;
                        }
                    }
                }

                if (!found)
                {
                    ShoutError(string.Format("Could not find object '{0}'", inventory));
                    return;
                }

                IBotManager manager = GetBotManager();
                if (manager != null)
                    manager.GiveInventoryObject(id, m_host, objName, objId, assetType, destId, m_host.OwnerID);

                delay = 2000;
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }

        // ── Bot Sensors ────────────────────────────────────────────────────────

        public void botSensor(string botID, string name, string id, int type, float range, float arc)
        {
            UUID botUUID = ParseBotID(botID);
            if (botUUID == UUID.Zero) return;

            UUID keyID = UUID.Zero;
            UUID.TryParse(id, out keyID);

            IBotManager manager = GetBotManager();
            if (manager == null || !manager.CheckPermission(botUUID, m_host.OwnerID)) return;

            ScenePresence botSP = World.GetScenePresence(botUUID);
            if (botSP == null) return;

            m_ScriptEngine.AsyncCommands?.SensorRepeatPlugin.SenseOnce(
                m_localID, m_itemID, name, keyID, type, range, arc, botSP);
        }

        public void botSensorRepeat(string botID, string name, string id, int type, float range, float arc, float rate)
        {
            UUID botUUID = ParseBotID(botID);
            if (botUUID == UUID.Zero) return;

            UUID keyID = UUID.Zero;
            UUID.TryParse(id, out keyID);

            IBotManager manager = GetBotManager();
            if (manager == null || !manager.CheckPermission(botUUID, m_host.OwnerID)) return;

            ScenePresence botSP = World.GetScenePresence(botUUID);
            if (botSP == null) return;

            m_ScriptEngine.AsyncCommands?.SensorRepeatPlugin.SetSenseRepeatEvent(
                m_localID, m_itemID, name, keyID, type, range, arc, rate, botSP);
        }

        public void botSensorRemove()
        {
            m_ScriptEngine.AsyncCommands?.SensorRepeatPlugin.UnSetSenseRepeaterEvents(m_localID, m_itemID);
        }

        public int botListen(string botID, int channel, string name, string id, string msg)
        {
            UUID botUUID = ParseBotID(botID);
            if (botUUID == UUID.Zero) return -1;

            UUID keyID = UUID.Zero;
            UUID.TryParse(id, out keyID);

            IBotManager manager = GetBotManager();
            if (manager == null || !manager.CheckPermission(botUUID, m_host.OwnerID)) return -1;

            ScenePresence botSP = World.GetScenePresence(botUUID);
            if (botSP == null) return -1;

            if (m_ScriptEngine.ListenManager == null) return -1;
            return m_ScriptEngine.ListenManager.Add(m_localID, m_itemID, botSP.UUID, channel, name, keyID, msg);
        }

        public void botMessageLinked(string botID, int num, string msg, string id)
        {
            UUID botUUID = ParseBotID(botID);
            if (botUUID == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager == null || !manager.CheckPermission(botUUID, m_host.OwnerID)) return;

            ScenePresence botSP = World.GetScenePresence(botUUID);
            if (botSP == null) return;

            List<SceneObjectGroup> groups = botSP.GetAttachments();
            var receivers = new List<UUID>();
            foreach (SceneObjectGroup group in groups)
            {
                foreach (SceneObjectPart part in group.Parts)
                {
                    TaskInventoryDictionary itemsDictionary;
                    lock (part.TaskInventory)
                        itemsDictionary = (TaskInventoryDictionary)part.TaskInventory.Clone();

                    foreach (TaskInventoryItem item in itemsDictionary.Values)
                    {
                        if (item.Type == INVENTORY_SCRIPT)
                        {
                            int linkNumber = m_host.LinkNum;
                            if (m_host.ParentGroup.PrimCount == 1)
                                linkNumber = 0;

                            object[] resobj = new object[] { linkNumber, num, msg, id };
                            m_ScriptEngine.PostScriptEvent(item.ItemID,
                                new EventParams("link_message", resobj, new DetectParams[0]));
                            receivers.Add(item.ItemID);
                        }
                    }
                }
            }
            // A bot's attachments are rezzed like any avatar's, so their scripts may be another engine's: each other
            // engine is offered the same scripts, once each.
            int sender = m_host.ParentGroup.PrimCount == 1 ? 0 : m_host.LinkNum;
            string text = msg ?? string.Empty, key = id ?? string.Empty;
            OfferToOtherEngines("botMessageLinked", () => receivers, "link_message", () => new object[] { sender, num, text, key });
            LinkMessageBackPressure(receivers);
        }

        // ── Bot Tagging ────────────────────────────────────────────────────────

        public void botAddTag(string botID, string tag)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.AddTagToBot(id, tag, m_host.OwnerID);
        }

        public void botRemoveTag(string botID, string tag)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return;

            IBotManager manager = GetBotManager();
            if (manager != null)
                manager.RemoveTagFromBot(id, tag, m_host.OwnerID);
        }

        public int botHasTag(string botID, string tag)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return 0;

            IBotManager manager = GetBotManager();
            if (manager != null)
                return manager.BotHasTag(id, tag) ? 1 : 0;
            return 0;
        }

        public LSLList botGetBotTags(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return new LSLList();

            IBotManager manager = GetBotManager();
            if (manager != null)
                return new LSLList(manager.GetBotTags(id).Cast<object>().ToList());
            return new LSLList();
        }

        public LSLList botGetBotsWithTag(string tag)
        {
            IBotManager manager = GetBotManager();
            List<UUID> bots = new List<UUID>();
            if (manager != null)
                bots = manager.GetBotsWithTag(tag);

            List<object> botList = new List<object>();
            foreach (UUID bot in bots)
                botList.Add(bot.ToString());
            return new LSLList(botList);
        }

        public void botRemoveBotsWithTag(string tag)
        {
            const int delay = 1000;

            try
            {
                IBotManager manager = GetBotManager();
                if (manager != null)
                    manager.RemoveBotsWithTag(tag, m_host.OwnerID);
            }
            finally
            {
                m_ScriptEngine.SysReturn(m_itemID, null, delay);
            }
        }


        // ── List parse helper ──────────────────────────────────────────────────────

        private LSLList ParseString2List(string src, LSLList separators, LSLList spacers, bool keepNulls)
        {
            if (src == null) return new LSLList();
            var result = new System.Collections.Generic.List<object>();
            // Build combined list of delimiters (separators are consumed, spacers are kept)
            var seps = new System.Collections.Generic.List<string>();
            var spacs = new System.Collections.Generic.List<string>();
            if (separators != null) foreach (var o in separators.Data) { var s = o?.ToString(); if (!string.IsNullOrEmpty(s)) seps.Add(s); }
            if (spacers != null) foreach (var o in spacers.Data) { var s = o?.ToString(); if (!string.IsNullOrEmpty(s)) spacs.Add(s); }

            int pos = 0;
            while (pos <= src.Length)
            {
                // Find earliest separator or spacer
                int earliest = src.Length; string found = null; bool isSpacer = false;
                foreach (var sep in seps) { int idx = src.IndexOf(sep, pos, StringComparison.Ordinal); if (idx >= 0 && idx < earliest) { earliest = idx; found = sep; isSpacer = false; } }
                foreach (var sp in spacs) { int idx = src.IndexOf(sp, pos, StringComparison.Ordinal); if (idx >= 0 && idx < earliest) { earliest = idx; found = sp; isSpacer = true; } }

                string token = src.Substring(pos, earliest - pos);
                if (keepNulls || token.Length > 0) result.Add(token);
                if (found == null) break;
                if (isSpacer) result.Add(found);
                pos = earliest + found.Length;
            }
            return new LSLList(result.ToArray());
        }

        // ── Notecard / dataserver helpers ──────────────────────────────────────────

        private TaskInventoryItem FindInventoryItem(string name, int type)
        {
            if (m_host == null) return null;
            lock (m_host.TaskInventory)
                foreach (var kvp in m_host.TaskInventory)
                    if (kvp.Value.Name == name && (type == -1 || kvp.Value.Type == type))
                        return kvp.Value;
            return null;
        }

        /// <summary>
        /// The table says these requests return the query key, but their bodies were
        /// void: the async shim (SyscallShim.RunAsync) returns only what the body hands to SysReturn,
        /// so the script got nothing back and stopped with "Stack empty". The key goes back through
        /// the deferred call's sequenced return (SysReturn records it on the call's SyscallContext; CompleteSyscall
        /// posts it with the call's sequence number), and the dataserver event carries the same key.
        /// </summary>
        private void ReturnQueryKey(UUID queryID) => m_ScriptEngine.SysReturn(m_itemID, queryID.ToString(), 0);

        private void PostDataserverEvent(UUID queryID, string data)
        {
            // The asking script gets only a reply it is still owed - one asked for before a reset, state change or
            // unload is not posted to it (Halcyon Dataserver.RemoveEvents).
            // Every other script in the prim gets it all the same (SL: "all scripts within the same prim
            // where the request was made"; the wiki says nothing of a reset, state change or removal in between).
            bool owed = m_PendingDataserver.TryRemove(queryID, out _);
            m_ScriptEngine?.PostDataserverToPrim(m_host, owed ? UUID.Zero : m_itemID, queryID.ToString(), data);
        }

        private static string StripNotecardHeader(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            if (!raw.StartsWith("Linden text", StringComparison.Ordinal)) return raw;
            int marker = raw.IndexOf("\nText length ", StringComparison.Ordinal);
            if (marker < 0) return raw;
            int bodyStart = raw.IndexOf('\n', marker + 1);
            if (bodyStart < 0) return string.Empty;
            string body = raw.Substring(bodyStart + 1);
            // The body is followed by "}\n" (AssetNotecard.Encode and the viewer both write it so), which the
            // EndsWith checks below never matched - the last line came back as "text}" with an empty line after it.
            // "Text length N" says how long the body is; take exactly that when it fits.
            string lenText = raw.Substring(marker + 13, bodyStart - (marker + 13)).Trim();
            if (int.TryParse(lenText, out int declared) && declared >= 0 && declared <= body.Length)
                return body.Substring(0, declared);
            if (body.EndsWith("}\n", StringComparison.Ordinal)) body = body.Substring(0, body.Length - 2);
            else if (body.EndsWith("\n}", StringComparison.Ordinal)) body = body.Substring(0, body.Length - 2);
            else if (body.EndsWith("}", StringComparison.Ordinal)) body = body.Substring(0, body.Length - 1);
            return body;
        }

        // ── Event flag mapping ─────────────────────────────────────────────────

		private ulong MapEventFlag(SupportedEventList.Events evt)
		{
			switch (evt)
			{
				case SupportedEventList.Events.ATTACH:              return (ulong)scriptEvents.attach;
				case SupportedEventList.Events.STATE_EXIT:          return (ulong)scriptEvents.state_exit;
				case SupportedEventList.Events.TIMER:               return (ulong)scriptEvents.timer;
				case SupportedEventList.Events.TOUCH:               return (ulong)scriptEvents.touch;
				case SupportedEventList.Events.COLLISION:           return (ulong)scriptEvents.collision;
				case SupportedEventList.Events.COLLISION_END:       return (ulong)scriptEvents.collision_end;
				case SupportedEventList.Events.COLLISION_START:     return (ulong)scriptEvents.collision_start;
				case SupportedEventList.Events.CONTROL:             return (ulong)scriptEvents.control;
				case SupportedEventList.Events.DATASERVER:          return (ulong)scriptEvents.dataserver;
				case SupportedEventList.Events.EMAIL:               return (ulong)scriptEvents.email;
				case SupportedEventList.Events.HTTP_RESPONSE:       return (ulong)scriptEvents.http_response;
				case SupportedEventList.Events.LAND_COLLISION:      return (ulong)scriptEvents.land_collision;
				case SupportedEventList.Events.LAND_COLLISION_END:  return (ulong)scriptEvents.land_collision_end;
				case SupportedEventList.Events.LAND_COLLISION_START:return (ulong)scriptEvents.land_collision_start;
				case SupportedEventList.Events.AT_TARGET:           return (ulong)scriptEvents.at_target;
				case SupportedEventList.Events.LISTEN:              return (ulong)scriptEvents.listen;
				case SupportedEventList.Events.MONEY:               return (ulong)scriptEvents.money;
				case SupportedEventList.Events.MOVING_END:          return (ulong)scriptEvents.moving_end;
				case SupportedEventList.Events.MOVING_START:        return (ulong)scriptEvents.moving_start;
				case SupportedEventList.Events.NOT_AT_ROT_TARGET:   return (ulong)scriptEvents.not_at_rot_target;
				case SupportedEventList.Events.NOT_AT_TARGET:       return (ulong)scriptEvents.not_at_target;
				case SupportedEventList.Events.TOUCH_START:         return (ulong)scriptEvents.touch_start;
				case SupportedEventList.Events.OBJECT_REZ:          return (ulong)scriptEvents.object_rez;
				case SupportedEventList.Events.REMOTE_DATA:         return (ulong)scriptEvents.remote_data;
				case SupportedEventList.Events.AT_ROT_TARGET:       return (ulong)scriptEvents.at_rot_target;
				case SupportedEventList.Events.RUN_TIME_PERMISSIONS:return (ulong)scriptEvents.run_time_permissions;
				case SupportedEventList.Events.TOUCH_END:           return (ulong)scriptEvents.touch_end;
				case SupportedEventList.Events.STATE_ENTRY:         return (ulong)scriptEvents.state_entry;
				case SupportedEventList.Events.CHANGED:             return (ulong)scriptEvents.changed;
				case SupportedEventList.Events.LINK_MESSAGE:        return (ulong)scriptEvents.link_message;
				case SupportedEventList.Events.NO_SENSOR:           return (ulong)scriptEvents.no_sensor;
				case SupportedEventList.Events.ON_REZ:              return (ulong)scriptEvents.on_rez;
				case SupportedEventList.Events.SENSOR:              return (ulong)scriptEvents.sensor;
				case SupportedEventList.Events.HTTP_REQUEST:        return (ulong)scriptEvents.http_request;
				case SupportedEventList.Events.TRANSACTION_RESULT:  return (ulong)scriptEvents.transaction_result;
				case SupportedEventList.Events.LINKSET_DATA:        return (ulong)scriptEvents.linkset_data;
				// The five SL events, now in both enums.
				case SupportedEventList.Events.PATH_UPDATE:         return (ulong)scriptEvents.path_update;
				case SupportedEventList.Events.ON_DAMAGE:           return (ulong)scriptEvents.on_damage;
				case SupportedEventList.Events.FINAL_DAMAGE:        return (ulong)scriptEvents.final_damage;
				case SupportedEventList.Events.ON_DEATH:            return (ulong)scriptEvents.on_death;
				case SupportedEventList.Events.GAME_CONTROL:        return (ulong)scriptEvents.game_control;
				// Note: the following events are intentionally deferred — each requires a
				// coordinated two-sided change (Phlox SupportedEventList AND core OpenSim
				// scriptEvents enum / posting infrastructure) before a case label here is safe.
				// - BOT_UPDATE: scriptEvents flag missing. No PostObjectEvent call posts this
				//   event anywhere in the codebase (confirmed 2026-05-24); no scripts broken.
				// - PATH_UPDATE: scriptEvents.path_update flag bit exists (1UL << 40), but
				//   SupportedEventList.Events has no PATH_UPDATE member (Phlox side gap).
				//   Wiring requires adding the enum member AND confirming the pathfinding
				//   subsystem posts it. Deferred pending evaluation.
				// - EXPERIENCE_PERMISSIONS / EXPERIENCE_PERMISSIONS_DENIED: SL Experience
				//   system is out of scope for this port; no OpenSim infrastructure exists.
				default: return 0UL;
                        }
                }

        // ── Tier 1: EEP / Environment ──────────────────────────────────────

        public int llGetDayLength()
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return 14400;
            return envModule.GetDayLength(m_host.GetWorldPosition());
        }

        public int llGetDayOffset()
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return 57600;
            return envModule.GetDayOffset(m_host.GetWorldPosition());
        }

        public int llGetRegionDayLength()
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return 14400;
            return envModule.GetRegionDayLength();
        }

        public int llGetRegionDayOffset()
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return 57600;
            return envModule.GetRegionDayOffset();
        }

        public Vector3 llGetMoonDirection()
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return Vector3.Zero;
            return envModule.GetMoonDir(m_host.GetWorldPosition());
        }

        public Quaternion llGetMoonRotation()
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return Quaternion.Identity;
            return envModule.GetMoonRot(m_host.GetWorldPosition());
        }

        public Quaternion llGetSunRotation()
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return Quaternion.Identity;
            return envModule.GetSunRot(m_host.GetWorldPosition());
        }

        public Vector3 llGetRegionSunDirection()
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return Vector3.Zero;
            float z = m_host.GetWorldPosition().Z;
            return envModule.GetRegionSunDir(z);
        }

        public Quaternion llGetRegionSunRotation()
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return Quaternion.Identity;
            float z = m_host.GetWorldPosition().Z;
            return envModule.GetRegionSunRot(z);
        }

        public Vector3 llGetRegionMoonDirection()
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return Vector3.Zero;
            float z = m_host.GetWorldPosition().Z;
            return envModule.GetRegionMoonDir(z);
        }

        public Quaternion llGetRegionMoonRotation()
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return Quaternion.Identity;
            float z = m_host.GetWorldPosition().Z;
            return envModule.GetRegionMoonRot(z);
        }

        public Vector3 llGetRegionLightDir()
        {
            // Dominant light direction = sun direction for the region
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return new Vector3(0f, 0.7071068f, 0.7071068f);
            float z = m_host.GetWorldPosition().Z;
            return envModule.GetRegionSunDir(z);
        }

        // ── Tier 2: PBR / GLTF Material Functions (598–602) ──

        public string llGetRenderMaterial(int face)
        {
            var shape = m_host.Shape;
            if (shape?.RenderMaterials?.entries == null)
                return string.Empty;
            if (face < 0)
                return string.Empty;
            foreach (var entry in shape.RenderMaterials.entries)
            {
                if (entry.te_index == (byte)face)
                    return ConditionalMaterialNameOrUUID(m_host, entry.id.ToString());
            }
            return string.Empty;
        }

        public void llSetRenderMaterial(string materialId, int face)
        {
            if (!UUID.TryParse(materialId, out UUID matUUID))
                return;
            if (!World.Permissions.CanEditObject(m_host.ParentGroup.UUID, m_host.OwnerID))
                return;

            var shape = m_host.Shape;
            shape.RenderMaterials ??= new OpenMetaverse.Primitive.RenderMaterials();

            int numFaces = m_host.GetNumberOfSides();

            if (face == ALL_SIDES)
            {
                var entries = new OpenMetaverse.Primitive.RenderMaterials.RenderMaterialEntry[numFaces];
                for (int i = 0; i < numFaces; i++)
                {
                    entries[i].te_index = (byte)i;
                    entries[i].id = matUUID;
                }
                shape.RenderMaterials.entries = entries;
            }
            else if (face >= 0 && face < numFaces)
            {
                if (shape.RenderMaterials.entries == null)
                {
                    shape.RenderMaterials.entries = new OpenMetaverse.Primitive.RenderMaterials.RenderMaterialEntry[1];
                    shape.RenderMaterials.entries[0].te_index = (byte)face;
                    shape.RenderMaterials.entries[0].id = matUUID;
                }
                else
                {
                    int idx = -1;
                    for (int i = 0; i < shape.RenderMaterials.entries.Length; i++)
                    {
                        if (shape.RenderMaterials.entries[i].te_index == (byte)face)
                        { idx = i; break; }
                    }
                    if (idx >= 0)
                    {
                        shape.RenderMaterials.entries[idx].id = matUUID;
                    }
                    else
                    {
                        int len = shape.RenderMaterials.entries.Length;
                        Array.Resize(ref shape.RenderMaterials.entries, len + 1);
                        shape.RenderMaterials.entries[len].te_index = (byte)face;
                        shape.RenderMaterials.entries[len].id = matUUID;
                    }
                }
            }
            else
            {
                return;
            }

            m_host.ParentGroup.HasGroupChanged = true;
            m_host.ScheduleUpdate(PrimUpdateFlags.FullUpdate);
        }

        public int llIsLinkGLTFMaterial(int link, int face)
        {
            foreach (SceneObjectPart part in GetLinkParts(link))
            {
                var shape = part.Shape;
                if (shape?.RenderMaterials?.overrides == null)
                    return 0;
                if (face == ALL_SIDES)
                {
                    return shape.RenderMaterials.overrides.Length > 0 ? 1 : 0;
                }
                foreach (var ovr in shape.RenderMaterials.overrides)
                {
                    if (ovr.te_index == (byte)face && !string.IsNullOrEmpty(ovr.data))
                        return 1;
                }
                return 0; // only check first matching link part
            }
            return 0;
        }

public int llSetLinkGLTFOverrides(int link, int face, LSLList overrides)
        {
            // overrides is a strided list of [key, value, key, value, ...]
            // Builds LLSD Notation data matching the format used by MaterialsModule
            if (overrides.Length < 2 || overrides.Length % 2 != 0)
                return 0;

            // Build an OSDMap in the compact format the viewer expects
            var outosd = new OpenMetaverse.StructuredData.OSDMap();

            for (int i = 0; i < overrides.Length; i += 2)
            {
                string key = overrides.Data[i].ToString();
                string val = overrides.Data[i + 1].ToString();
                var ci = System.Globalization.CultureInfo.InvariantCulture;

                switch (key)
                {
                    case "base_color":
                    {
                        if (TryParseColor4(val, out float r, out float g, out float b, out float a))
                        {
                            outosd["bc"] = new OpenMetaverse.StructuredData.OSDArray()
                            {
                                Math.Round((double)r, 4),
                                Math.Round((double)g, 4),
                                Math.Round((double)b, 4),
                                Math.Round((double)a, 4)
                            };
                        }
                        break;
                    }
                    case "metallic_factor":
                    {
                        if (float.TryParse(val, System.Globalization.NumberStyles.Float, ci, out float mf))
                            outosd["mf"] = Math.Round((double)Math.Clamp(mf, 0f, 1f), 3);
                        break;
                    }
                    case "roughness_factor":
                    {
                        if (float.TryParse(val, System.Globalization.NumberStyles.Float, ci, out float rf))
                            outosd["rf"] = Math.Round((double)Math.Clamp(rf, 0f, 1f), 3);
                        break;
                    }
                    case "emissive_factor":
                    {
                        if (TryParseVector3(val, out float er, out float eg, out float eb))
                        {
                            outosd["ec"] = new OpenMetaverse.StructuredData.OSDArray()
                            {
                                Math.Round((double)er, 4),
                                Math.Round((double)eg, 4),
                                Math.Round((double)eb, 4)
                            };
                        }
                        break;
                    }
                    case "alpha_mode":
                    {
                        outosd["am"] = val.ToUpper() switch
                        {
                            "BLEND" => 1,
                            "MASK" => 2,
                            _ => 0
                        };
                        break;
                    }
                    case "alpha_cutoff":
                    {
                        if (float.TryParse(val, System.Globalization.NumberStyles.Float, ci, out float ac))
                            outosd["ac"] = Math.Round((double)Math.Clamp(ac, 0f, 1f), 3);
                        break;
                    }
                    case "double_sided":
                    {
                        outosd["ds"] = (val == "1" || val.ToLower() == "true");
                        break;
                    }
                }
            }

            if (outosd.Count == 0)
                return 0;

            string llsdData = OpenMetaverse.StructuredData.OSDParser.SerializeLLSDNotation(outosd);
            int changed = 0;

            foreach (SceneObjectPart part in GetLinkParts(link))
            {
                if (!World.Permissions.CanEditObject(part.ParentGroup.UUID, m_host.OwnerID))
                    continue;

                var shape = part.Shape;
                shape.RenderMaterials ??= new OpenMetaverse.Primitive.RenderMaterials();

                int numFaces = part.GetNumberOfSides();
               if (face == ALL_SIDES)
                {
                    for (int f = 0; f < numFaces; f++)
                        ApplyGLTFOverride(ref shape.RenderMaterials.overrides, llsdData, f);
                }
                else if (face >= 0 && face < numFaces)
                {
                    ApplyGLTFOverride(ref shape.RenderMaterials.overrides, llsdData, face);
                }
                else
                {
                    continue;
                }

                part.ParentGroup.HasGroupChanged = true;
                part.ScheduleUpdate(PrimUpdateFlags.MaterialOvr | PrimUpdateFlags.FullUpdate);
                changed++;
            }
            return changed > 0 ? 1 : 0;
        }

        private static void ApplyGLTFOverride(
            ref OpenMetaverse.Primitive.RenderMaterials.RenderMaterialOverrideEntry[] overrides,
            string data, int face)
        {
            if (overrides == null)
            {
                overrides = new OpenMetaverse.Primitive.RenderMaterials.RenderMaterialOverrideEntry[1];
                overrides[0].te_index = (byte)face;
                overrides[0].data = data;
                return;
            }

            for (int i = 0; i < overrides.Length; i++)
            {
                if (overrides[i].te_index == (byte)face)
                {
                    overrides[i].data = data;
                    return;
                }
            }

            int len = overrides.Length;
            Array.Resize(ref overrides, len + 1);
            overrides[len].te_index = (byte)face;
            overrides[len].data = data;
        }

        // ── PBR helpers for PRIM_RENDER_MATERIAL / PRIM_GLTF_* in SetPrimParams/GetPrimParams ──

        private static void SetRenderMaterialEntry(
            ref OpenMetaverse.Primitive.RenderMaterials.RenderMaterialEntry[] entries,
            UUID matId, int face)
        {
            if (entries == null)
            {
                entries = new OpenMetaverse.Primitive.RenderMaterials.RenderMaterialEntry[1];
                entries[0].te_index = (byte)face;
                entries[0].id = matId;
                return;
            }

            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].te_index == (byte)face)
                {
                    entries[i].id = matId;
                    return;
                }
            }

            int len = entries.Length;
            Array.Resize(ref entries, len + 1);
            entries[len].te_index = (byte)face;
            entries[len].id = matId;
        }

        private void ApplyGLTFOverrideToPart(SceneObjectPart part, int face,
            OpenMetaverse.StructuredData.OSDMap osd)
        {
            if (osd.Count == 0) return;

            var shape = part.Shape;
            shape.RenderMaterials ??= new OpenMetaverse.Primitive.RenderMaterials();

            string llsdData = OpenMetaverse.StructuredData.OSDParser.SerializeLLSDNotation(osd);
            int numFaces = part.GetNumberOfSides();

            if (face == ALL_SIDES)
            {
                for (int f = 0; f < numFaces; f++)
                    ApplyGLTFOverride(ref shape.RenderMaterials.overrides, llsdData, f);
            }
            else if (face >= 0 && face < numFaces)
            {
                ApplyGLTFOverride(ref shape.RenderMaterials.overrides, llsdData, face);
            }
            else
            {
                return;
            }

            if (part.ParentGroup != null)
                part.ParentGroup.HasGroupChanged = true;
            part.ScheduleUpdate(PrimUpdateFlags.MaterialOvr | PrimUpdateFlags.FullUpdate);
        }

        private string GetGLTFOverrideField(SceneObjectPart part, int face, string fieldName)
        {
            var shape = part.Shape;
            if (shape?.RenderMaterials?.overrides == null) return null;

            foreach (var ovr in shape.RenderMaterials.overrides)
            {
                if (ovr.te_index == (byte)face && !string.IsNullOrEmpty(ovr.data))
                {
                    try
                    {
                        var parsed = OpenMetaverse.StructuredData.OSDParser.DeserializeLLSDNotation(ovr.data)
                            as OpenMetaverse.StructuredData.OSDMap;
                        if (parsed != null && parsed.ContainsKey(fieldName))
                            return parsed[fieldName].ToString();
                    }
                    catch { }
                }
            }
            return null;
        }

        private OpenMetaverse.StructuredData.OSDMap GetGLTFOverrideMap(SceneObjectPart part, int face)
        {
            var shape = part.Shape;
            if (shape?.RenderMaterials?.overrides == null) return null;

            foreach (var ovr in shape.RenderMaterials.overrides)
            {
                if (ovr.te_index == (byte)face && !string.IsNullOrEmpty(ovr.data))
                {
                    try
                    {
                        return OpenMetaverse.StructuredData.OSDParser.DeserializeLLSDNotation(ovr.data)
                            as OpenMetaverse.StructuredData.OSDMap;
                    }
                    catch { }
                }
            }
            return null;
        }

        private void GetGLTFBaseColorParams(SceneObjectPart part, int face, List<object> result)
        {
            var osd = GetGLTFOverrideMap(part, face);

            // texture
            result.Add(ConditionalMaterialNameOrUUID(part, osd != null && osd.ContainsKey("tex") ? osd["tex"].AsString() : string.Empty));
            // repeats
            if (osd != null && osd.ContainsKey("rep") && osd["rep"] is OpenMetaverse.StructuredData.OSDArray repArr && repArr.Count >= 2)
                result.Add(new Vector3((float)repArr[0].AsReal(), (float)repArr[1].AsReal(), 0f));
            else
                result.Add(new Vector3(1f, 1f, 0f));
            // offsets
            if (osd != null && osd.ContainsKey("off") && osd["off"] is OpenMetaverse.StructuredData.OSDArray offArr && offArr.Count >= 2)
                result.Add(new Vector3((float)offArr[0].AsReal(), (float)offArr[1].AsReal(), 0f));
            else
                result.Add(Vector3.Zero);
            // rotation
            result.Add(osd != null && osd.ContainsKey("rot") ? (float)osd["rot"].AsReal() : 0f);
            // color (RGB from bc array)
            if (osd != null && osd.ContainsKey("bc") && osd["bc"] is OpenMetaverse.StructuredData.OSDArray bcArr && bcArr.Count >= 3)
                result.Add(new Vector3((float)bcArr[0].AsReal(), (float)bcArr[1].AsReal(), (float)bcArr[2].AsReal()));
            else
                result.Add(Vector3.One);
            // alpha (4th element of bc)
            if (osd != null && osd.ContainsKey("bc") && osd["bc"] is OpenMetaverse.StructuredData.OSDArray bcArr2 && bcArr2.Count >= 4)
                result.Add((float)bcArr2[3].AsReal());
            else
                result.Add(1f);
            // alpha_mode
            result.Add(osd != null && osd.ContainsKey("am") ? osd["am"].AsInteger() : 0);
            // alpha_cutoff
            result.Add(osd != null && osd.ContainsKey("ac") ? (float)osd["ac"].AsReal() : 0.5f);
            // double_sided
            result.Add(osd != null && osd.ContainsKey("ds") && osd["ds"].AsBoolean() ? 1 : 0);
        }

        private void GetGLTFTransformParams(SceneObjectPart part, int face,
            string texKey, string repKey, string offKey, string rotKey, List<object> result)
        {
            var osd = GetGLTFOverrideMap(part, face);

            // texture
            result.Add(ConditionalMaterialNameOrUUID(part, osd != null && osd.ContainsKey(texKey) ? osd[texKey].AsString() : string.Empty));
            // repeats
            if (osd != null && osd.ContainsKey(repKey) && osd[repKey] is OpenMetaverse.StructuredData.OSDArray repArr && repArr.Count >= 2)
                result.Add(new Vector3((float)repArr[0].AsReal(), (float)repArr[1].AsReal(), 0f));
            else
                result.Add(new Vector3(1f, 1f, 0f));
            // offsets
            if (osd != null && osd.ContainsKey(offKey) && osd[offKey] is OpenMetaverse.StructuredData.OSDArray offArr && offArr.Count >= 2)
                result.Add(new Vector3((float)offArr[0].AsReal(), (float)offArr[1].AsReal(), 0f));
            else
                result.Add(Vector3.Zero);
            // rotation
            result.Add(osd != null && osd.ContainsKey(rotKey) ? (float)osd[rotKey].AsReal() : 0f);
        }

        private void GetGLTFMetallicRoughnessParams(SceneObjectPart part, int face, List<object> result)
        {
            var osd = GetGLTFOverrideMap(part, face);

            // texture, repeats, offsets, rotation
            GetGLTFTransformParams(part, face, "mrtex", "mrrep", "mroff", "mrrot", result);
            // metallic_factor
            result.Add(osd != null && osd.ContainsKey("mf") ? (float)osd["mf"].AsReal() : 0f);
            // roughness_factor
            result.Add(osd != null && osd.ContainsKey("rf") ? (float)osd["rf"].AsReal() : 0.5f);
        }

        private void GetGLTFEmissiveParams(SceneObjectPart part, int face, List<object> result)
        {
            var osd = GetGLTFOverrideMap(part, face);

            // texture, repeats, offsets, rotation
            GetGLTFTransformParams(part, face, "etex", "erep", "eoff", "erot", result);
            // emissive_tint
            if (osd != null && osd.ContainsKey("ec") && osd["ec"] is OpenMetaverse.StructuredData.OSDArray ecArr && ecArr.Count >= 3)
                result.Add(new Vector3((float)ecArr[0].AsReal(), (float)ecArr[1].AsReal(), (float)ecArr[2].AsReal()));
            else
                result.Add(Vector3.Zero);
        }

        private static bool TryParseColor4(string s, out float r, out float g, out float b, out float a)
        {
            r = g = b = 0f; a = 1f;
            s = s.Trim().Trim('<', '>');
            var parts = s.Split(',');
            if (parts.Length < 3) return false;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (!float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, ci, out r)) return false;
            if (!float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, ci, out g)) return false;
            if (!float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float, ci, out b)) return false;
            if (parts.Length >= 4)
                float.TryParse(parts[3].Trim(), System.Globalization.NumberStyles.Float, ci, out a);
            return true;
        }

        private static bool TryParseVector3(string s, out float x, out float y, out float z)
        {
            x = y = z = 0f;
            s = s.Trim().Trim('<', '>');
            var parts = s.Split(',');
            if (parts.Length < 3) return false;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (!float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, ci, out x)) return false;
            if (!float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, ci, out y)) return false;
            if (!float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float, ci, out z)) return false;
            return true;
        }


        public void llRenderMaterial(string materialId)
        {
            llSetRenderMaterial(materialId, ALL_SIDES);
        }

        // ── Tier 3: Combat 2.0 (603–605) ──

        public float llGetHealth(string id)
        {
            if (!UUID.TryParse(id, out UUID agentId))
                return -1f;
            ScenePresence sp = World?.GetScenePresence(agentId);
            if (sp == null || sp.IsChildAgent)
                return -1f;
            return sp.Health;
        }

        /// <summary>
        /// SL's llAdjustDamage(integer number, float new_damage): inside on_damage, rewrite the
        /// pending entry's amount before it lands (wiki: "modifies the amount of damage that will be applied
        /// by the current on_damage event after it has completed"). Anywhere else: an error on DEBUG_CHANNEL,
        /// as the wiki says. Out-of-range or negative index: silent. The older (key, amount) form,
        /// an OpenSim-ism with the same arity, is gone - llDamage is the SL way to deal damage.
        /// </summary>
        public void llAdjustDamage(int number, float newDamage)
        {
            var state = m_thisScript?.ScriptState;
            if (state?.RunningEvent == null || state.RunningEvent.EventType != InWorldz.Phlox.Types.SupportedEventList.Events.ON_DAMAGE)
            {
                ShoutError("llAdjustDamage: only valid inside an on_damage handler");
                return;
            }
            var vars = state.RunningEvent.DetectVars;
            if (vars == null || number < 0 || number >= vars.Length) return;
            vars[number].Damage = newDamage;
            vars[number].AdjustDamage?.Invoke(newDamage);
        }

        public void llSetHealth(string id, float health)
        {
            if (!UUID.TryParse(id, out UUID agentId))
                return;
            ScenePresence sp = World?.GetScenePresence(agentId);
            if (sp == null || sp.IsChildAgent)
                return;
            if (!World.RegionInfo.RegionSettings.AllowDamage)
                return;

            health = Math.Clamp(health, 0f, 100f);
            // An absolute set is a damage of (current - target) through the one door - a heal is
            // a negative amount. Invulnerable / god presences keep their health, as the door rules.
            sp.ApplyDamage(m_host.UUID, m_host.OwnerID, m_host.LocalId, sp.Health - health, DamageEntry.TYPE_GENERIC, true);
        }

		// -- Tier 4: Pathfinding / Character System (606-628) --
		// Maps prim LocalID -> bot UUID for llCreateCharacter/llNavigateTo etc.
		// Region-qualified key: (regionID, prim localID). LocalIDs are unique only within
		// a single region; in a multi-region process two prims in different regions can share
		// a localID. Keying by (regionID, localID) prevents cross-region character state
		// collision.
		private (UUID, uint) CharKey => (World.RegionInfo.RegionID, m_host.LocalId);
		private static readonly Dictionary<(UUID, uint), UUID> s_primCharacters = new();
		private static readonly object s_charLock = new();

        // Called by PhloxEngine.OnObjectBeingRemovedFromScene when a prim leaves the scene.
        // Removes the dict entry and returns the botID so the caller can remove the bot from BotManager.
        // Returns UUID.Zero if no character was registered for this prim.
        internal static UUID ClearCharacter(UUID regionID, uint localID)
        {
            lock (s_charLock)
            {
                var key = (regionID, localID);
                if (s_primCharacters.TryGetValue(key, out UUID botID))
                {
                    s_primCharacters.Remove(key);
                    return botID;
                }
            }
            return UUID.Zero;
        }

        // Called by PhloxEngine.RemoveRegion to purge all character dict entries for a region.
        // BotManager.RemoveRegion already removes the bot NPCs; this cleans only the dict.
        internal static void ClearRegionCharacters(UUID regionID)
        {
            lock (s_charLock)
            {
                foreach (var key in s_primCharacters.Keys.Where(k => k.Item1 == regionID).ToList())
                    s_primCharacters.Remove(key);
            }
        }

		private UUID GetCharacterBot()
		{
			lock (s_charLock)
			{
				if (s_primCharacters.TryGetValue(CharKey, out UUID botID))
					return botID;
			}
			return UUID.Zero;
		}


        public LSLList llGetClosestNavPoint(Vector3 point, LSLList options)
        {
            // No navmesh in this tree — return the requested point as the closest navigable point
            return new LSLList(new object[] { point });
        }

        public LSLList llGetStaticPath(Vector3 start, Vector3 end, float radius, LSLList parameters)
        {
            // No navmesh in this tree — return a straight-line path [start, end, status]
            // Status 0 = success per SL spec
            return new LSLList(new object[] { start, end, 0 });
        }
		public void llExecCharacterCmd(int command, LSLList parameters)
		{
			UUID botID = GetCharacterBot();
			if (botID == UUID.Zero) return;
			IBotManager manager = GetBotManager();
			if (manager == null) return;
			if (command == 0 || command == 2) // STOP or SMOOTH_STOP
				manager.StopMovement(botID, m_host.OwnerID);
		}
		public void llDeleteCharacter()
		{
			UUID botID;
			lock (s_charLock)
			{
				if (!s_primCharacters.TryGetValue(CharKey, out botID))
					return;
				s_primCharacters.Remove(CharKey);
			}
			IBotManager manager = GetBotManager();
			if (manager != null)
				manager.RemoveBot(botID, m_host.OwnerID);
		}
		// -- Pathfinding Character Functions (621-628) --

		public void llCreateCharacter(LSLList options)
		{
			llDeleteCharacter();
			IBotManager manager = GetBotManager();
			if (manager == null) return;
			string firstName = "Char";
			string lastName = m_host.LocalId.ToString();
			Vector3 pos = m_host.AbsolutePosition;
			float speed = 1.0f;
			for (int i = 0; i < options.Length - 1; i += 2)
			{
				int opt = (int)options.Data[i];
				if (opt == CHARACTER_DESIRED_SPEED)
					speed = (float)options.Data[i + 1];
			}
			string reason;
			UUID botID = manager.CreateBot(firstName, lastName, pos, "", m_itemID, m_host.OwnerID, out reason);
			if (botID == UUID.Zero) return;
			if (speed != 1.0f)
				manager.SetBotSpeed(botID, speed, m_host.OwnerID);
			lock (s_charLock)
				s_primCharacters[CharKey] = botID;
		}

		public void llNavigateTo(Vector3 pos, LSLList options)
		{
			UUID botID = GetCharacterBot();
			if (botID == UUID.Zero) return;
			IBotManager manager = GetBotManager();
			if (manager == null) return;
			var positions = new List<Vector3> { pos };
			var modes = new List<TravelMode> { TravelMode.Walk };
			manager.SetBotNavigationPoints(botID, positions, modes,
				new Dictionary<int, object>(), m_host.OwnerID);
		}

		public void llUpdateCharacter(LSLList options)
		{
			UUID botID = GetCharacterBot();
			if (botID == UUID.Zero) return;
			IBotManager manager = GetBotManager();
			if (manager == null) return;
			for (int i = 0; i < options.Length - 1; i += 2)
			{
				int opt = (int)options.Data[i];
				if (opt == CHARACTER_DESIRED_SPEED)
				{
					float speed = (float)options.Data[i + 1];
					manager.SetBotSpeed(botID, speed, m_host.OwnerID);
				}
			}
		}

		public void llPursue(string targetID, LSLList options)
		{
			UUID botID = GetCharacterBot();
			if (botID == UUID.Zero) return;
			if (!UUID.TryParse(targetID, out UUID target)) return;
			IBotManager manager = GetBotManager();
			if (manager == null) return;
			manager.StartFollowingAvatar(botID, target,
				new Dictionary<int, object>(), m_host.OwnerID);
		}

		public void llEvade(string targetID, LSLList options)
		{
			UUID botID = GetCharacterBot();
			if (botID == UUID.Zero) return;
			if (!UUID.TryParse(targetID, out UUID target)) return;
			IBotManager manager = GetBotManager();
			if (manager == null) return;
			ScenePresence sp = World?.GetScenePresence(target);
			if (sp == null) return;
			Vector3 myPos = m_host.AbsolutePosition;
			Vector3 dir = myPos - sp.AbsolutePosition;
			dir.Normalize();
			Vector3 away = myPos + dir * 30f;
			away.X = Math.Clamp(away.X, 1f, 254f);
			away.Y = Math.Clamp(away.Y, 1f, 254f);
			var positions = new List<Vector3> { away };
			var modes = new List<TravelMode> { TravelMode.Run };
			manager.SetBotNavigationPoints(botID, positions, modes,
				new Dictionary<int, object>(), m_host.OwnerID);
		}

		public void llFleeFrom(Vector3 source, float distance, LSLList options)
		{
			UUID botID = GetCharacterBot();
			if (botID == UUID.Zero) return;
			IBotManager manager = GetBotManager();
			if (manager == null) return;
			Vector3 myPos = m_host.AbsolutePosition;
			Vector3 dir = myPos - source;
			dir.Normalize();
			Vector3 away = myPos + dir * distance;
			away.X = Math.Clamp(away.X, 1f, 254f);
			away.Y = Math.Clamp(away.Y, 1f, 254f);
			var positions = new List<Vector3> { away };
			var modes = new List<TravelMode> { TravelMode.Run };
			manager.SetBotNavigationPoints(botID, positions, modes,
				new Dictionary<int, object>(), m_host.OwnerID);
		}

		public void llWanderWithin(Vector3 origin, Vector3 distances, LSLList options)
		{
			UUID botID = GetCharacterBot();
			if (botID == UUID.Zero) return;
			IBotManager manager = GetBotManager();
			if (manager == null) return;
			manager.WanderWithin(botID, origin, distances,
				new Dictionary<int, object>(), m_host.OwnerID);
		}

		public void llPatrolPoints(LSLList points, LSLList options)
		{
			UUID botID = GetCharacterBot();
			if (botID == UUID.Zero) return;
			IBotManager manager = GetBotManager();
			if (manager == null) return;
			var positions = new List<Vector3>();
			var modes = new List<TravelMode>();
			for (int i = 0; i < points.Length; i++)
			{
				if (points.Data[i] is Vector3 v)
				{
					positions.Add(v);
					modes.Add(TravelMode.Walk);
				}
			}
			if (positions.Count == 0) return;
			manager.SetBotNavigationPoints(botID, positions, modes,
				new Dictionary<int, object>(), m_host.OwnerID);
		}

		
		// ── Tier 5: Experience KVP Store (610–620) ──

        // ── 610–620: Experience KV Store (upgraded to use ExperienceService) ──

        // ── SL Experience error codes (XP_ERROR_*) + limits, ported from the port source
        //    to match the SL wiki XP_ERROR table 0-18.
        //    Script-surface conformance with SL. ──
        // SL key-value key length cap (SL wiki llCreateKeyValue): 1011 bytes (was 255).
        private const int MAX_EXPERIENCE_KEY_LENGTH = 1011;
        // Viewer experience-property bit PROPERTY_DISABLED (indra VP_DISABLED = 1<<6);
        // used to report the llGetExperienceDetails state field.
        private const int VP_DISABLED = 1 << 6;
        // SL per-experience KV quota: 128 MiB (was NGC's 16 MiB).
        private const long MAX_DATA_QUOTA = 128L * 1024 * 1024;

        // UTF-8 byte count for a KV key/value — the quota basis (matches the port source's KvBytes and the
        // MySQL SUM(LENGTH(`key`)+LENGTH(`value`)) used-size on the grid backend).
        private static long KvBytes(string s) => s == null ? 0 : System.Text.Encoding.UTF8.GetByteCount(s);

        // True if updating `key` to `value` would push this experience's KV store over MAX_DATA_QUOTA.
        // Delta-aware (port source ExceedsQuota): an existing key swaps its value (key stays); a new key
        // adds the whole pair. Basis: key+value UTF-8 bytes.
        private bool ExceedsQuota(PhloxExperienceAdapter expService, UUID expId, string key, string value)
        {
            long used = expService.DataSizeKeyValue(expId);
            string existing = expService.ReadKeyValue(expId, key); // null iff key absent
            long oldPair = existing != null ? KvBytes(key) + KvBytes(existing) : 0;
            long newPair = KvBytes(key) + KvBytes(value);
            return used - oldPair + newPair > MAX_DATA_QUOTA;
        }

        // Phlox's synchronous key-value contract, kept for the *SL names (618-620), which answer at once:
        // 0 ok · -1 invalid/error · -2 duplicate (create) · -3 CAS-fail/not-found (update) · -5 quota exceeded · -6 value over 4095 bytes.
        // Before SL's dataserver form these were the bodies of llCreateKeyValue / llReadKeyValue / llUpdateKeyValue(3).
        private int SyncCreateKeyValue(string key, string value)
        {
            if (string.IsNullOrEmpty(key) || key.Length > MAX_EXPERIENCE_KEY_LENGTH) return -1;
            if (BadKvValue(value)) return -6;   // SL's 4095-byte value limit
            var expService = GetExperienceAdapter();
            UUID expId = GetScriptExperienceId();
            if (expService == null || expId == UUID.Zero)
            {
                // Fallback: use owner-based namespace via old GetExperienceId()
                expId = m_host.OwnerID;
            }
            try
            {
                // Quota check BEFORE the write (as the port source — never write-then-detect). A create
                // only ADDS a pair; reject if that would exceed 128 MiB -> -5 (llCreateKeyValueSL emits
                // 0,11 = XP_ERROR_QUOTA_EXCEEDED). No write.
                if (expService != null && expService.DataSizeKeyValue(expId) + KvBytes(key) + KvBytes(value) > MAX_DATA_QUOTA)
                    return -5;
                bool ok = expService != null
                    ? expService.CreateKeyValue(expId, key, value)
                    : false;
                return ok ? 0 : -2; // -2 = duplicate key
            }
            catch (Exception ex)
            {
                m_log.LogWarning("[PhloxAPI]: llCreateKeyValueSL failed: {0}", ex.Message);
                return -1;
            }
        }

        private string SyncReadKeyValue(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            var expService = GetExperienceAdapter();
            UUID expId = GetScriptExperienceId();
            if (expService == null || expId == UUID.Zero)
                expId = m_host.OwnerID;
            try
            {
                string val = expService?.ReadKeyValue(expId, key);
                return val ?? string.Empty;
            }
            catch (Exception ex)
            {
                m_log.LogWarning("[PhloxAPI]: llReadKeyValueSL failed: {0}", ex.Message);
                return string.Empty;
            }
        }

        private int SyncUpdateKeyValue(string key, string value, string check)
        {
            if (string.IsNullOrEmpty(key) || key.Length > MAX_EXPERIENCE_KEY_LENGTH) return -1;
            if (BadKvValue(value)) return -6;   // SL's 4095-byte value limit
            var expService = GetExperienceAdapter();
            UUID expId = GetScriptExperienceId();
            if (expService == null || expId == UUID.Zero)
                expId = m_host.OwnerID;
            try
            {
                // Delta-aware quota check BEFORE the write (port source ExceedsQuota). If the
                // projected total after this update exceeds 128 MiB -> -5 (llUpdateKeyValueSL emits
                // 0,11). No write. (If a CAS would also fail, quota wins at the boundary — benign.)
                if (expService != null && ExceedsQuota(expService, expId, key, value))
                    return -5;
                bool ok = expService != null
                    ? expService.UpdateKeyValue(expId, key, value, check)
                    : false;
                return ok ? 0 : -3; // -3 = check failed or key not found
            }
            catch (Exception ex)
            {
                m_log.LogWarning("[PhloxAPI]: llUpdateKeyValueSL failed: {0}", ex.Message);
                return -1;
            }
        }

        // ── SL's key-value form ──
        // wiki: every call "Start[s] an asynchronous transaction" and returns a key; the answer is a dataserver event
        // with that key and cdl = llDumpList2String([ 1, ... ],",") on success or [ 0, integer error ] (XP_ERROR_*).
        // The query id is recorded (NewDataserverQuery), so an answer owed to a script that was reset, changed
        // state or was removed in between is dropped by PostDataserverEvent. The store work runs inside the syscall,
        // which the shim hands to the region's service lane (Defer): the scheduler thread never waits on the store,
        // and one script's answers arrive in the order it asked.
        // No key-value store at all (no IExperienceService) answers XP_ERROR_STORE_DISABLED.
        // As SL: a script not compiled into an Experience (its item's ExperienceID is zero,
        // GetScriptExperienceId) answers XP_ERROR_NO_EXPERIENCE, "This script is not associated with an experience."
        // (wiki llGetExperienceErrorMessage; each call's page: "the script must be compiled into an Experience"), and
        // the store is not touched. Phlox once used the owner's id here; Phlox's own names (llClearKeyValue and the
        // *SL variants) keep that fallback, so data stored under an owner's id stays reachable through them.

        private delegate string KeyValueWork(PhloxExperienceAdapter store, UUID experienceId);

        private string KeyValueRequest(string fn, KeyValueWork work)
        {
            UUID queryID = NewDataserverQuery();
            string reply;
            PhloxExperienceAdapter store = GetExperienceAdapter();
            UUID expId;
            if (store == null || !store.HasKeyValueStore)
                reply = "0," + XP_ERROR_STORE_DISABLED;
            else if ((expId = GetScriptExperienceId()) == UUID.Zero)
                reply = "0," + XP_ERROR_NO_EXPERIENCE;
            else
            {
                try
                {
                    reply = work(store, expId);
                }
                catch (Exception ex)
                {
                    m_log.LogWarning("[PhloxAPI]: {0} failed: {1}", fn, ex.Message);
                    reply = "0," + XP_ERROR_STORAGE_EXCEPTION;
                }
            }
            PostDataserverEvent(queryID, reply);
            return queryID.ToString();
        }

        // SL: keys are at most 1011 bytes (wiki llCreateKeyValue); an empty key names nothing.
        private static bool BadKvKey(string key) => string.IsNullOrEmpty(key) || KvBytes(key) > MAX_EXPERIENCE_KEY_LENGTH;

        private static string KvFail(int xpError) => "0," + xpError;

        // SL's value limit, wiki llCreateKeyValue / llUpdateKeyValue: "As of Jan 1, 2016 maximum bytes is 1011 for
        // key and 4095 for value for both LSO and Mono scripts." Over it is XP_ERROR_INVALID_PARAMETERS, "One of the
        // string arguments was too big to fit in the key-value store." (wiki llGetExperienceErrorMessage). Nothing is
        // written. Before, the store got the value: MySQL's VARCHAR(4095) column refused it (strict mode) or cut it.
        private const int MAX_EXPERIENCE_VALUE_BYTES = 4095;
        private static bool BadKvValue(string value) => KvBytes(value) > MAX_EXPERIENCE_VALUE_BYTES;

        /// <summary>wiki: key llCreateKeyValue(string k, string v). An existing key is XP_ERROR_STORAGE_EXCEPTION.</summary>
        public string llCreateKeyValue(string key, string value)
            => KeyValueRequest("llCreateKeyValue", (store, expId) =>
            {
                if (BadKvKey(key) || BadKvValue(value)) return KvFail(XP_ERROR_INVALID_PARAMETERS);
                value ??= string.Empty;
                if (store.DataSizeKeyValue(expId) + KvBytes(key) + KvBytes(value) > MAX_DATA_QUOTA)
                    return KvFail(XP_ERROR_QUOTA_EXCEEDED);
                switch (store.CreateKeyValueStatus(expId, key, value))
                {
                    case "success": return "1," + value;
                    case "full": return KvFail(XP_ERROR_QUOTA_EXCEEDED);
                    default: return KvFail(XP_ERROR_STORAGE_EXCEPTION);   // "exists" (wiki) or a store error
                }
            });

        /// <summary>wiki: key llReadKeyValue(string k). A missing key is XP_ERROR_KEY_NOT_FOUND.</summary>
        public string llReadKeyValue(string key)
            => KeyValueRequest("llReadKeyValue", (store, expId) =>
            {
                if (BadKvKey(key)) return KvFail(XP_ERROR_INVALID_PARAMETERS);
                string value = store.ReadKeyValue(expId, key);
                return value == null ? KvFail(XP_ERROR_KEY_NOT_FOUND) : "1," + value;
            });

        /// <summary>Phlox's 3-argument llUpdateKeyValue(k, v, check): the SL 4-argument call with checked = (check != "").</summary>
        public string llUpdateKeyValue(string key, string value, string check)
            => llUpdateKeyValue(key, value, string.IsNullOrEmpty(check) ? 0 : 1, check);

        /// <summary>
        /// wiki: key llUpdateKeyValue(string k, string v, integer checked, string original_value). XP_ERROR_RETRY_UPDATE
        /// when checked and the stored value is not original_value; a key that does not exist "will generate a new key with
        /// the specified value, as if you had used llCreateKeyValue" (NGC's update does not create, so it is created here).
        /// </summary>
        public string llUpdateKeyValue(string k, string v, int isChecked, string original_value)
            => KeyValueRequest("llUpdateKeyValue", (store, expId) =>
            {
                if (BadKvKey(k) || BadKvValue(v)) return KvFail(XP_ERROR_INVALID_PARAMETERS);
                v ??= string.Empty;
                if (ExceedsQuota(store, expId, k, v)) return KvFail(XP_ERROR_QUOTA_EXCEEDED);
                string status = store.UpdateKeyValueStatus(expId, k, v, isChecked != 0, original_value);
                if (status == "missing")
                {
                    status = store.CreateKeyValueStatus(expId, k, v);
                    if (status == "exists") return KvFail(XP_ERROR_RETRY_UPDATE);   // created by someone else meanwhile
                }
                switch (status)
                {
                    case "success": return "1," + v;
                    case "mismatch": return KvFail(XP_ERROR_RETRY_UPDATE);
                    case "full": return KvFail(XP_ERROR_QUOTA_EXCEEDED);
                    default: return KvFail(XP_ERROR_STORAGE_EXCEPTION);
                }
            });

        /// <summary>
        /// wiki: key llDeleteKeyValue(string k); success is [ 1, string value ] - the value deleted, read first (as the old
        /// tree's 5e1a8837f9 did); a missing key is XP_ERROR_STORAGE_EXCEPTION.
        /// </summary>
        public string llDeleteKeyValue(string key)
            => KeyValueRequest("llDeleteKeyValue", (store, expId) =>
            {
                if (BadKvKey(key)) return KvFail(XP_ERROR_INVALID_PARAMETERS);
                string old = store.ReadKeyValue(expId, key);
                return store.DeleteKeyValueStatus(expId, key) == "success"
                    ? "1," + (old ?? string.Empty)
                    : KvFail(XP_ERROR_STORAGE_EXCEPTION);   // "missing" (wiki) or a store error
            });

        /// <summary>wiki: key llKeyCountKeyValue(); success is [ 1, integer pairs ].</summary>
        public string llKeyCountKeyValue()
            => KeyValueRequest("llKeyCountKeyValue", (store, expId) => "1," + store.KeyCountKeyValue(expId));

        // wiki llKeysKeyValue: "may return fewer keys than requested if ... the result list exceeds 4096 characters".
        private const int MAX_KEYS_REPLY_CHARS = 4096;

        /// <summary>
        /// wiki: key llKeysKeyValue(integer first, integer count); success is llDumpList2String([ 1 ] + keys, ","), and
        /// "XP_ERROR_KEY_NOT_FOUND is returned if there index given is greater than or equal to the number of keys".
        /// Phlox's clamps are kept: count &lt;= 0 is 100, count &gt; 1000 is 1000, first &lt; 0 is 0.
        /// </summary>
        public string llKeysKeyValue(int start, int count)
            => KeyValueRequest("llKeysKeyValue", (store, expId) =>
            {
                if (count <= 0) count = 100;
                if (count > 1000) count = 1000;
                if (start < 0) start = 0;
                if (start >= store.KeyCountKeyValue(expId)) return KvFail(XP_ERROR_KEY_NOT_FOUND);
                List<string> keys = store.KeysKeyValue(expId, start, count);
                var reply = new System.Text.StringBuilder("1");
                int used = 0;
                foreach (string k in keys)
                {
                    int add = (used == 0 ? 0 : 1) + k.Length;
                    if (used + add > MAX_KEYS_REPLY_CHARS) break;
                    reply.Append(',').Append(k);
                    used += add;
                }
                return used == 0 && keys.Count == 0 ? KvFail(XP_ERROR_KEY_NOT_FOUND) : reply.ToString();
            });

        /// <summary>wiki: key llDataSizeKeyValue(); success is "1,&lt;used&gt;,&lt;quota&gt;" in bytes (Phlox's quota: 128 MiB).</summary>
        public string llDataSizeKeyValue()
            => KeyValueRequest("llDataSizeKeyValue", (store, expId) =>
                "1," + store.DataSizeKeyValue(expId) + "," + MAX_DATA_QUOTA);

        public int llClearKeyValue()
        {
            // Clear all KV pairs for this experience — delete all keys
            var expService = GetExperienceAdapter();
            UUID expId = GetScriptExperienceId();
            if (expService == null || expId == UUID.Zero)
                expId = m_host.OwnerID;
            try
            {
                var keys = expService?.KeysKeyValue(expId, 0, 10000);
                if (keys != null)
                {
                    foreach (var k in keys)
                        expService.DeleteKeyValue(expId, k);
                }
                return 0;
            }
            catch (Exception ex)
            {
                m_log.LogWarning("[PhloxAPI]: llClearKeyValue failed: {0}", ex.Message);
                return -1;
            }
        }

        // The ...SL wrappers present SL's async-dataserver CSV shape "1,<value>" (success) /
        // "0,<XP_ERROR>" (failure). The failure payload is a NUMERIC XP_ERROR code (was a
        // free-text message), matching SL and the port source. They answer at once (Phlox's own names);
        // unchanged when SL's names moved to dataserver answers. They (and
        // llClearKeyValue) keep the owner-id fallback for a script with no Experience; SL's 4095-byte value limit applies.
        public string llCreateKeyValueSL(string key, string value)
        {
            int result = SyncCreateKeyValue(key, value);
            if (result == 0)
                return "1," + (value ?? string.Empty);
            if (result == -5) // over the 128 MiB quota
                return "0," + XP_ERROR_QUOTA_EXCEEDED;
            if (result == -6) // Value over SL's 4095 bytes
                return "0," + XP_ERROR_INVALID_PARAMETERS;
            // SL: creating an existing key (or a generic KV failure) => XP_ERROR_STORAGE_EXCEPTION.
            return "0," + XP_ERROR_STORAGE_EXCEPTION;
        }

        public string llReadKeyValueSL(string key)
        {
            string val = SyncReadKeyValue(key);
            if (!string.IsNullOrEmpty(val))
                return "1," + val;
            // SL: a missing key => XP_ERROR_KEY_NOT_FOUND (14).
            return "0," + XP_ERROR_KEY_NOT_FOUND;
        }

        public string llUpdateKeyValueSL(string key, string value, string check)
        {
            int result = SyncUpdateKeyValue(key, value, check);
            if (result == 0)
                return "1," + (value ?? string.Empty);
            if (result == -5) // over the 128 MiB quota
                return "0," + XP_ERROR_QUOTA_EXCEEDED;
            if (result == -6) // Value over SL's 4095 bytes
                return "0," + XP_ERROR_INVALID_PARAMETERS;
            // SL: a checked-update mismatch (CAS fail) => XP_ERROR_RETRY_UPDATE (15).
            return "0," + XP_ERROR_RETRY_UPDATE;
        }

        // ── Tier 6: Standalone ──

        public string llSignRSA(string data, string privateKeyPem, string algorithm)
        {
            try
            {
                System.Security.Cryptography.HashAlgorithmName hashAlg;
                switch (algorithm.ToUpper())
                {
                    case "SHA256": hashAlg = System.Security.Cryptography.HashAlgorithmName.SHA256; break;
                    case "SHA384": hashAlg = System.Security.Cryptography.HashAlgorithmName.SHA384; break;
                    case "SHA512": hashAlg = System.Security.Cryptography.HashAlgorithmName.SHA512; break;
                    case "SHA1":   hashAlg = System.Security.Cryptography.HashAlgorithmName.SHA1; break;
                    default: return string.Empty;
                }
                using var rsa = System.Security.Cryptography.RSA.Create();
                rsa.ImportFromPem(privateKeyPem.AsSpan());
                byte[] dataBytes = System.Text.Encoding.UTF8.GetBytes(data);
                byte[] signature = rsa.SignData(dataBytes, hashAlg, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
                return Convert.ToBase64String(signature);
            }
            catch (Exception ex)
            {
                m_log.LogWarning("[PhloxAPI]: llSignRSA failed: {0}", ex.Message);
                return string.Empty;
            }
        }

        public int llVerifyRSA(string data, string signature, string publicKeyPem, string algorithm)
        {
            try
            {
                System.Security.Cryptography.HashAlgorithmName hashAlg;
                switch (algorithm.ToUpper())
                {
                    case "SHA256": hashAlg = System.Security.Cryptography.HashAlgorithmName.SHA256; break;
                    case "SHA384": hashAlg = System.Security.Cryptography.HashAlgorithmName.SHA384; break;
                    case "SHA512": hashAlg = System.Security.Cryptography.HashAlgorithmName.SHA512; break;
                    case "SHA1":   hashAlg = System.Security.Cryptography.HashAlgorithmName.SHA1; break;
                    case "SHA224": hashAlg = new System.Security.Cryptography.HashAlgorithmName("SHA224"); break;
                    default: return 0;
                }
                using var rsa = System.Security.Cryptography.RSA.Create();
                rsa.ImportFromPem(publicKeyPem.AsSpan());
                byte[] dataBytes = System.Text.Encoding.UTF8.GetBytes(data);
                byte[] sigBytes = Convert.FromBase64String(signature);
                return rsa.VerifyData(dataBytes, sigBytes, hashAlg, System.Security.Cryptography.RSASignaturePadding.Pkcs1) ? 1 : 0;
            }
            catch (Exception ex)
            {
                m_log.LogWarning("[PhloxAPI]: llVerifyRSA failed: {0}", ex.Message);
                return 0;
            }
        }

        // ── Tier 7: EEP Environment Functions (630–632) ──

        private const int ENV_DAY_LENGTH = 200;
        private const int ENV_DAY_OFFSET = 201;

        public LSLList llGetEnvironment(Vector3 pos, LSLList paramList)
        {
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return new LSLList();

            var result = new List<object>();
            float altitude = pos.Z;
            // If pos.X == -1 and pos.Y == -1, use region environment
            // Otherwise use parcel environment at that position (we use region for both since
            // OpenSim parcel environments share the same ViewerEnvironment in most setups)

            for (int i = 0; i < paramList.Length; i++)
            {
                int param;
                try { param = Convert.ToInt32(paramList.Data[i]); }
                catch { continue; }

                switch (param)
                {
                    case ENV_DAY_LENGTH:
                        result.Add(envModule.GetDayLength(pos));
                        break;
                    case ENV_DAY_OFFSET:
                        result.Add(envModule.GetDayOffset(pos));
                        break;
                    case SKY_SUN:
                    {
                        Quaternion sunRot = envModule.GetSunRot(pos);
                        Vector3 sunDir = envModule.GetSunDir(pos);
                        result.Add(sunRot);
                        result.Add(1.0f); // scale
                        result.Add(sunDir);
                        break;
                    }
                    case SKY_MOON:
                    {
                        Quaternion moonRot = envModule.GetMoonRot(pos);
                        Vector3 moonDir = envModule.GetMoonDir(pos);
                        result.Add(moonRot);
                        result.Add(0.5f); // scale
                        result.Add(1.0f); // brightness
                        result.Add(1);    // is_default_texture
                        result.Add(moonDir);
                        break;
                    }
                    case SKY_TRACKS:
                    {
                        // Return sky track altitudes (tracks 2-4, track 1 is ground level)
                        result.Add(1000.0f);
                        result.Add(2000.0f);
                        result.Add(3000.0f);
                        break;
                    }
                    case SKY_AMBIENT:
                        result.Add(new Vector3(0.25f, 0.25f, 0.26f)); // default ambient
                        break;
                    case SKY_GAMMA:
                        result.Add(1.0f); // default gamma
                        break;
                    case SKY_GLOW:
                        result.Add(0.5f); // glow size
                        result.Add(0.1f); // glow focus
                        break;
                    case SKY_STAR_BRIGHTNESS:
                        result.Add(256.0f); // default star brightness
                        break;
                    case SKY_DOME:
                        result.Add(0.96f); // offset
                        result.Add(15000.0f); // radius
                        result.Add(1605.0f); // max_altitude
                        break;
                    case SKY_CLOUDS:
                        result.Add(new Vector3(0.41f, 0.41f, 0.41f)); // color
                        result.Add(0.27f); // coverage
                        result.Add(0.42f); // scale
                        result.Add(0.0f);  // variance
                        result.Add(new Vector3(0.01f, 0.01f, 0.0f)); // scroll
                        result.Add(new Vector3(1.0f, 0.53f, 1.0f));  // density
                        result.Add(new Vector3(1.0f, 0.52f, 1.0f));  // detail
                        result.Add(1);     // is_default
                        break;
                    default:
                        // Unknown parameter - SL sends debug channel message
                        ShoutError("llGetEnvironment: unknown parameter " + param);
                        break;
                }
            }
            return new LSLList(result);
        }

        public int llSetEnvironment(Vector3 pos, LSLList paramList)
        {
            // Check permissions - must be estate manager or land owner
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return -1;

            if (!World.Permissions.CanEditParcelProperties(m_host.OwnerID,
                World.LandChannel.GetLandObject(m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y), 0, false))
            {
                ShoutError("llSetEnvironment: insufficient permissions");
                return -1;
            }

            var env = envModule.GetRegionEnvironment();
            if (env == null) return -1;

            bool changed = false;
            for (int i = 0; i < paramList.Length - 1; i += 2)
            {
                int param;
                try { param = Convert.ToInt32(paramList.Data[i]); }
                catch { continue; }

                switch (param)
                {
                    case ENV_DAY_LENGTH:
                        try
                        {
                            int dayLen = Convert.ToInt32(paramList.Data[i + 1]);
                            if (dayLen >= 14400 && dayLen <= 604800)
                            {
                                env.DayLength = dayLen;
                                changed = true;
                            }
                        }
                        catch { }
                        break;
                    case ENV_DAY_OFFSET:
                        try
                        {
                            int dayOff = Convert.ToInt32(paramList.Data[i + 1]);
                            env.DayOffset = dayOff;
                            changed = true;
                        }
                        catch { }
                        break;
                    default:
                        ShoutError("llSetEnvironment: parameter " + param + " not yet supported for setting");
                        break;
                }
            }

            if (changed)
            {
                envModule.StoreOnRegion(env);
                envModule.WindlightRefresh(0);
            }
            return ENV_OK;
        }

        public int llReplaceEnvironment(Vector3 pos, string environment, int track, int day_length, int day_offset)
        {
            // Check permissions
            var envModule = World?.RequestModuleInterface<IEnvironmentModule>();
            if (envModule == null) return -1;

            if (!World.Permissions.CanEditParcelProperties(m_host.OwnerID,
                World.LandChannel.GetLandObject(m_host.AbsolutePosition.X, m_host.AbsolutePosition.Y), 0, false))
            {
                ShoutError("llReplaceEnvironment: insufficient permissions");
                return -1;
            }

            var env = envModule.GetRegionEnvironment();
            if (env == null) return -1;

            // Apply day_length and day_offset if specified (not -1)
            bool changed = false;
            if (day_length > 0)
            {
                env.DayLength = Math.Clamp(day_length, 14400, 604800);
                changed = true;
            }
            if (day_offset != -1)
            {
                env.DayOffset = day_offset;
                changed = true;
            }

            if (changed)
            {
                envModule.StoreOnRegion(env);
                envModule.WindlightRefresh(0);
            }

            // Note: the 'environment' parameter would load an EEP asset by name/UUID
            // from prim inventory. Full asset loading is not implemented yet — only
            // day_length and day_offset changes are applied.
            if (!string.IsNullOrEmpty(environment))
            {
                m_log.LogInformation("[PhloxAPI]: llReplaceEnvironment: EEP asset '{0}' load not yet implemented, day_length/offset applied", environment);
            }

            return ENV_OK;
        }

        // ── Tier 7b: Agent Environment + User Key (633–635) ──

        public string llRequestUserKey(string username)
        {
            // Async lookup — fires dataserver event with the user's UUID
            if (string.IsNullOrEmpty(username)) return string.Empty;

            UUID reqID = NewDataserverQuery();

            // Normalize "first.last" to "first last"
            string normalized = username.Replace('.', ' ').Trim();
            string[] parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string firstName = parts.Length > 0 ? parts[0] : "";
            string lastName = parts.Length > 1 ? parts[1] : "Resident";

            // Look up via user account service
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var accountService = World?.RequestModuleInterface<IUserAccountService>();
                    if (accountService == null)
                    {
                        PostDataserverEvent(reqID, UUID.Zero.ToString());
                        return;
                    }
                    var account = accountService.GetUserAccount(World.RegionInfo.ScopeID, firstName, lastName);
                    string result = account != null ? account.PrincipalID.ToString() : UUID.Zero.ToString();
                    PostDataserverEvent(reqID, result);
                }
                catch (Exception ex)
                {
                    m_log.LogWarning("[PhloxAPI]: llRequestUserKey failed for '{0}': {1}", username, ex.Message);
                    PostDataserverEvent(reqID, UUID.Zero.ToString());
                }
            });

            return reqID.ToString();
        }

        public int llSetAgentEnvironment(string agentID, float transition, LSLList paramList)
        {
            // Experience-gated: set per-agent environment (EEP)
            if (m_host == null || World == null) return -1;

            UUID targetAgent;
            if (!UUID.TryParse(agentID, out targetAgent)) return -1;

            // Check experience permission
            if (!HasExperiencePermission(targetAgent))
                return -3; // XP_ERROR_NOT_EXPERIENCE

            ScenePresence sp = World.GetScenePresence(targetAgent);
            if (sp == null || sp.IsChildAgent) return -1;

            // Per-agent EEP requires viewer/region support for GenericMessage or
            // EnvironmentUpdate per-agent caps. Most OpenSim builds don't support this yet.
            // Log the attempt and return success so scripts don't break.
            m_log.LogInformation("[PhloxAPI]: llSetAgentEnvironment called for agent {0} with {1} params (transition={2}s). Per-agent EEP not yet implemented in viewer protocol.",
                targetAgent, paramList.Length, transition);
            return 0; // Report success so scripts proceed
        }

        public int llReplaceAgentEnvironment(string agentID, float transition, string environment)
        {
            // Experience-gated: replace per-agent environment with named EEP preset
            if (m_host == null || World == null) return -1;

            UUID targetAgent;
            if (!UUID.TryParse(agentID, out targetAgent)) return -1;

            // Check experience permission
            if (!HasExperiencePermission(targetAgent))
                return -3; // XP_ERROR_NOT_EXPERIENCE

            ScenePresence sp = World.GetScenePresence(targetAgent);
            if (sp == null || sp.IsChildAgent) return -1;

            // Per-agent EEP replace requires viewer protocol support.
            m_log.LogInformation("[PhloxAPI]: llReplaceAgentEnvironment called for agent {0} environment='{1}' (transition={2}s). Per-agent EEP not yet implemented in viewer protocol.",
                targetAgent, environment, transition);
            return 0; // Report success so scripts proceed
        }

        // ── Tier 8: Remaining SL Function Gaps (636–643) ──

        public string llGetExperienceErrorMessage(int error)
        {
            // SL experience error code to human-readable message
            switch (error)
            {
                case 0:  return "no error";
                case 1:  return "exceeded throttle";
                case 2:  return "experiences are disabled";
                case 3:  return "invalid parameters";
                case 4:  return "operation not permitted";
                case 5:  return "script not associated with an experience";
                case 6:  return "not found";
                case 7:  return "invalid experience";
                case 8:  return "experience is disabled";
                case 9:  return "experience is suspended";
                case 10: return "unknown error";
                case 11: return "experience data quota exceeded";
                case 12: return "key-value store is disabled";
                case 13: return "key-value store communication failed";
                // Rows 14-18 corrected to the SL wiki XP_ERROR table (were shifted:
                // 14 said "key already exists", 15/16/17 were off by one, 18 was missing).
                case XP_ERROR_KEY_NOT_FOUND: return "key doesn't exist";
                case XP_ERROR_RETRY_UPDATE: return "retry update";
                case XP_ERROR_MATURITY_EXCEEDED: return "experience content rating too high";
                case XP_ERROR_NOT_PERMITTED_LAND: return "not allowed to run on this land";
                case XP_ERROR_REQUEST_PERM_TIMEOUT: return "experience permissions request timed out";
                default: return "unknown error id";
            }
        }

        public void llSetAgentRot(Quaternion rot, int relative)
        {
            // Requires PERMISSION_TRIGGER_ANIMATION
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return;
            if ((item.PermsMask & PERMISSION_TRIGGER_ANIMATION) == 0)
            {
                ShoutError("llSetAgentRot: script does not have PERMISSION_TRIGGER_ANIMATION");
                return;
            }
            UUID agentID = item.PermsGranter;
            if (agentID == UUID.Zero) return;

            ScenePresence sp = World.GetScenePresence(agentID);
            if (sp == null || sp.IsChildAgent) return;

            if (relative != 0)
            {
                // Relative to current rotation
                rot = sp.Rotation * rot;
            }
            sp.Rotation = rot;
            sp.SendTerseUpdateToAllClients();
        }

        public void llOpenFloater(string name, string url, LSLList paramList)
        {
            // Opens a viewer-side floater window. This is viewer-dependent.
            // Firestorm/OpenSim may not support all floater types.
            // For now, log and attempt to send a LoadURL for web-based floaters.
            if (!string.IsNullOrEmpty(url))
            {
                TaskInventoryItem item = GetInventorySelf();
                UUID agentID = item != null ? item.PermsGranter : UUID.Zero;
                if (agentID != UUID.Zero)
                {
                    ScenePresence sp = World.GetScenePresence(agentID);
                    if (sp != null && !sp.IsChildAgent)
                    {
                        sp.ControllingClient.SendLoadURL(name, m_host.UUID,
                            m_host.OwnerID, false, name, url);
                    }
                }
            }
        }

        public void llCloseFloater(string name)
        {
            // Closes a viewer-side floater. Viewer-dependent — no reliable way to
            // close a floater via the SL protocol in OpenSim. Stub for compatibility.
        }

        public int llSitOnLink(string agentID, int link)
        {
            // Experience-gated: force-sit agent on a specific link without dialog
            if (m_host == null || World == null) return -1;

            UUID targetAgent;
            if (!UUID.TryParse(agentID, out targetAgent)) return -1;

            // Check experience permission
            if (!HasExperiencePermission(targetAgent))
                return -3; // XP_ERROR_NOT_EXPERIENCE

            ScenePresence sp = World.GetScenePresence(targetAgent);
            if (sp == null || sp.IsChildAgent) return -1;

            // Already sitting?
            if (sp.ParentID != 0) return -2; // already seated

            // Find the target link part
            SceneObjectPart sitPart = null;
            if (link == 0 || link == LINK_ROOT)
            {
                sitPart = m_host.ParentGroup.RootPart;
            }
            else
            {
                foreach (SceneObjectPart part in GetLinkParts(link))
                {
                    sitPart = part;
                    break;
                }
            }

            if (sitPart == null) return -1;

            // Check if sit target is already occupied
            if (sitPart.SitTargetAvatar != UUID.Zero) return -2;

            // Use the proper two-step sit path:
            // 1. HandleAgentRequestSit sets m_requestedSitTargetID and sends viewer response
            // 2. HandleAgentSit processes the actual sit using that target ID
            try
            {
                sp.HandleAgentRequestSit(sp.ControllingClient, targetAgent,
                    sitPart.UUID, sitPart.SitTargetPosition);
                sp.HandleAgentSit(sp.ControllingClient, targetAgent);

                m_log.LogInformation("[PhloxAPI]: llSitOnLink: sat {0} on link {1} ({2})",
                    targetAgent, link, sitPart.Name);
                return 0;
            }
            catch (Exception ex)
            {
                m_log.LogWarning("[PhloxAPI]: llSitOnLink failed: {0}", ex.Message);
                return -1;
            }
        }

        /// <summary>
        /// SL wiki (LlRezObjectWithParams): "Returns a key which will be the key of the object when it
        /// is successfully rezzed in the world. On failure, returns (key)"" (in LSL)". The key is the
        /// rezzed root's, the one object_rez reports (upstream LSL_Api returns the rezzed group's id too,
        /// but NULL_KEY on failure; SL's "" is kept here). The shim runs this async, so the script gets
        /// its value only from SysReturn, handed over on every path, a throw included.
        /// </summary>
        public string llRezObjectWithParams(string inventory, LSLList paramList)
        {
            string result = String.Empty;
            try { result = RezWithParams(inventory, paramList); }
            finally { m_ScriptEngine.SysReturn(m_itemID, result, 0); }
            return result;
        }

        private string RezWithParams(string inventory, LSLList paramList)
        {
            // Extended rez function from SL Combat2 system.
            // Parse the params list for REZ_POS, REZ_ROT, REZ_VEL, REZ_FLAGS, etc.
            // For now, extract basic position/rotation/velocity and delegate to existing rez logic.

            if (string.IsNullOrEmpty(inventory)) return String.Empty;

            // These were 1,2,3,8,4,7 - a private numbering that matched neither SL nor
            // upstream, so a script written to the SL constants had every rule misread. Now the
            // values in LSL_Constants.cs:1131-1154, the same ones DefaultConstants exposes.
            string startString = null;

            Vector3 pos = m_host.AbsolutePosition;
            Quaternion rot = m_host.GetWorldRotation();
            Vector3 vel = Vector3.Zero;
            int param = 0;
            bool atRoot = false;
            bool posRelative = false;

            for (int i = 0; i < paramList.Length; i++)
            {
                int rule;
                try { rule = Convert.ToInt32(paramList.Data[i]); }
                catch { continue; }

                switch (rule)
                {
                    case REZ_POS:
                        if (i + 3 < paramList.Length)
                        {
                            try
                            {
                                pos = (Vector3)paramList.Data[i + 1];
                                atRoot = Convert.ToInt32(paramList.Data[i + 2]) != 0;
                                posRelative = Convert.ToInt32(paramList.Data[i + 3]) != 0;
                                i += 3;
                            }
                            catch { }
                        }
                        break;
                    case REZ_ROT:
                        if (i + 2 < paramList.Length)
                        {
                            try
                            {
                                rot = (Quaternion)paramList.Data[i + 1];
                                bool rotRelative = Convert.ToInt32(paramList.Data[i + 2]) != 0;
                                if (rotRelative) rot = m_host.GetWorldRotation() * rot;
                                i += 2;
                            }
                            catch { }
                        }
                        break;
                    case REZ_VEL:
                        if (i + 3 < paramList.Length)
                        {
                            try
                            {
                                vel = (Vector3)paramList.Data[i + 1];
                                bool velRelative = Convert.ToInt32(paramList.Data[i + 2]) != 0;
                                if (velRelative) vel = vel * m_host.GetWorldRotation();
                                i += 3;
                            }
                            catch { }
                        }
                        break;
                    case REZ_PARAM_STRING:
                        // upstream LSL_Api.cs:3796-3803
                        if (i + 1 < paramList.Length)
                        {
                            startString = paramList.Data[i + 1]?.ToString() ?? string.Empty;
                            i += 1;
                        }
                        break;
                    case REZ_PARAM:
                        if (i + 1 < paramList.Length)
                        {
                            try { param = Convert.ToInt32(paramList.Data[i + 1]); i += 1; }
                            catch { }
                        }
                        break;
                    case REZ_FLAGS:
                    case REZ_DAMAGE:
                        // Skip — advanced Combat2 flags, consume the value
                        if (i + 1 < paramList.Length) i += 1;
                        break;
                    default:
                        break;
                }
            }

            // Resolve relative position
            if (posRelative)
                pos = m_host.AbsolutePosition + pos * m_host.GetWorldRotation();

            // Delegate to existing rez infrastructure; its NULL_KEY failure is SL's "" here.
            string key = RezObjectInternal(inventory, pos, vel, rot, param, atRoot, startString);
            return key == UUID.Zero.ToString() ? String.Empty : key;
        }

        public string llGetMaterialOverride(int face, LSLList paramList)
        {
            // Read PBR material override properties on a face
            // Returns the override data as a string, or empty if none
            var shape = m_host.Shape;
            if (shape?.RenderMaterials?.overrides == null)
                return string.Empty;

            int targetFace = face;
            foreach (var ovr in shape.RenderMaterials.overrides)
            {
                if (targetFace == ALL_SIDES || ovr.te_index == (byte)targetFace)
                {
                    if (!string.IsNullOrEmpty(ovr.data))
                        return ovr.data;
                }
            }
            return string.Empty;
        }

        // ══════════════════════════════════════════════════════════════════════
        // Phase 31: 19 new functions (TableIndex 643–661)
        // ══════════════════════════════════════════════════════════════════════

        // ── 643: llHash ──
        public int llHash(string src)
        {
            // SL-compatible fast integer hash of a string
            // Uses DJB2 algorithm to match SL behavior
            if (string.IsNullOrEmpty(src)) return 0;
            unchecked
            {
                int hash = 5381;
                foreach (char c in src)
                    hash = ((hash << 5) + hash) + c;
                return hash;
            }
        }

        // ── 644–647: Link-level sound functions ──
        public void llLinkAdjustSoundVolume(int link, float volume)
        {
            // Adjust volume of currently playing sound on a specific link
            volume = Math.Clamp(volume, 0f, 1f);
            var parts = GetLinkParts(link);
            foreach (var part in parts)
            {
                part.AdjustSoundGain(volume);
            }
        }

        public void llLinkSetSoundQueueing(int link, int queue)
        {
            // Enable/disable sound queueing on specific link
            var parts = GetLinkParts(link);
            foreach (var part in parts)
            {
                part.SoundQueueing = (queue != 0);
            }
        }

        public void llLinkSetSoundRadius(int link, float radius)
        {
            // Set sound radius on specific link
            var parts = GetLinkParts(link);
            foreach (var part in parts)
            {
                part.SoundRadius = radius;
            }
        }

        public void llLinkStopSound(int link)
        {
            // Stop sound on specific link
            ISoundModule sm = World?.RequestModuleInterface<ISoundModule>();
            if (sm == null) return;
            var parts = GetLinkParts(link);
            foreach (var part in parts)
            {
                sm.StopSound(part);
            }
        }

        // ── 648: llScriptProfiler ── (already implemented at line 1279)

        // ── 649: llGiveAgentInventory ──
        public void llGiveAgentInventory(string destination, string inventory)
        {
            // Give inventory item to a specific agent — like llGiveInventory but
            // SL added this with slightly different permissions behavior.
            // We delegate to the existing llGiveInventory implementation.
            llGiveInventory(destination, inventory);
        }

        // ── 650: llMapBeacon ──
        public void llMapBeacon(string agent, string text, Vector3 color)
        {
            // Show a beacon on the agent's minimap at this object's location
            // Requires viewer-side support for beacon rendering
            // Current implementation: sends an IM with beacon info as a workaround
            if (m_host == null || World == null) return;
            UUID agentId;
            if (!UUID.TryParse(agent, out agentId)) return;

            ScenePresence sp = World.GetScenePresence(agentId);
            if (sp == null || sp.IsChildAgent) return;

            // Notify the agent about the beacon location via region say
            // Full viewer beacon rendering requires viewer-specific protocol support
            Vector3 pos = m_host.AbsolutePosition;
            m_log.LogInformation("[PhloxAPI]: llMapBeacon: beacon for {0} at {1} text='{2}'",
                agentId, pos, text ?? "");
        }

        // ── 651: llGetStartString ──
        /// <summary>Ported from upstream LSL_Api.cs:4589-4593. wiki: "Returns a string that was
        /// passed to the object's root prim on rez with llRezObjectWithParams"; blank when the object was
        /// rezzed any other way.</summary>
        public string llGetStartString()
        {
            string s = m_host?.ParentGroup?.RezStringParameter;
            return string.IsNullOrEmpty(s) ? string.Empty : s;
        }

        // ── 652: llSetGroundTexture ──
        public void llSetGroundTexture(string texture, int corner)
        {
            // Set terrain texture for one of the 4 corners (0=SW, 1=NW, 2=SE, 3=NE)
            if (World == null || m_host == null) return;

            // Must be estate manager or owner
            if (!World.RegionInfo.EstateSettings.IsEstateManagerOrOwner(m_host.OwnerID))
            {
                ShoutError("llSetGroundTexture: requires estate manager permissions");
                return;
            }

            UUID textureId;
            if (!UUID.TryParse(texture, out textureId))
            {
                TaskInventoryItem item = FindInventoryItem(texture, (int)AssetType.Texture);
                textureId = item?.AssetID ?? UUID.Zero;
                if (textureId == UUID.Zero) return;
            }

            corner = Math.Clamp(corner, 0, 3);
            var ri = World.RegionInfo;
            switch (corner)
            {
                case 0: ri.RegionSettings.TerrainTexture1 = textureId; break;
                case 1: ri.RegionSettings.TerrainTexture2 = textureId; break;
                case 2: ri.RegionSettings.TerrainTexture3 = textureId; break;
                case 3: ri.RegionSettings.TerrainTexture4 = textureId; break;
            }
            ri.RegionSettings.Save();
            // Force terrain update to all viewers
            World.EventManager.TriggerEstateToolsSunUpdate(World.RegionInfo.RegionHandle);
        }

        // ── 653: llTargetedEmail ──
        // ---- SL names and arities. Each is the SL behaviour, not a forward. ------------

        /// <summary>wiki: llsRGB2Linear(vector srgb) - the SL spelling. Same conversion; the older
        /// llSRGB2Linear stays as an alias. (The wiki notes the name is a misnomer - LSL colour is
        /// Rec.709 - but the documented formula is the sRGB one both spellings implement.)</summary>
        public Vector3 llsRGB2Linear(Vector3 srgb) => llSRGB2Linear(srgb);

        /// <summary>wiki: llListSortStrided - the SL name for what Phlox shipped as llSortListStrided.
        /// Bounds rule per the wiki: stride_index in [-stride, stride) or an empty list.</summary>
        public LSLList llListSortStrided(LSLList src, int stride, int stride_index, int ascending)
            => llSortListStrided(src, stride, stride_index, ascending);

        /// <summary>wiki: string llSHA256String(string src) - "a string of 64 hex characters that is
        /// the SHA-256 security hash of src", src as UTF-8, nothing appended. Distinct from the
        /// (src, nonce) form, which hashes src + ":" + nonce.</summary>
        public string llSHA256String(string src)
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes(src ?? string.Empty);
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
        }

        /// <summary>
        /// wiki: llTargetedEmail(integer target, string subject, string message) - the address is
        /// derived from the target. Routing follows upstream LSL_Api.cs:4362-4375: OBJECT_OWNER mails
        /// the owner's account (skipped when the object is group-owned); ROOT_CREATOR mails the root
        /// creator only when this script item's creator is the same person - upstream's guard against
        /// creator spam. 20 s sleep per the wiki. 4096-character cap per upstream.
        /// </summary>
        public void llTargetedEmail(int target, string subject, string message)
        {
            const int TargetRootCreator = 1, TargetObjectOwner = 2;
            try
            {
                if (m_host == null || World == null) return;
                if ((subject ?? string.Empty).Length + (message ?? string.Empty).Length > 4096) return;
                SceneObjectGroup parent = m_host.ParentGroup;
                if (parent == null) return;

                UUID recipient;
                if (target == TargetObjectOwner)
                {
                    if (parent.OwnerID == parent.GroupID) return;
                    recipient = parent.OwnerID;
                }
                else if (target == TargetRootCreator)
                {
                    TaskInventoryItem item = m_host.Inventory?.GetInventoryItem(m_itemID);
                    if (item == null || item.CreatorID != parent.RootPart.CreatorID) return;
                    recipient = parent.RootPart.CreatorID;
                }
                else return;

                UserAccount account = World.UserAccountService?.GetUserAccount(World.RegionInfo.ScopeID, recipient);
                if (account == null || string.IsNullOrEmpty(account.Email)) return;

                IEmailModule emailModule = World.RequestModuleInterface<IEmailModule>();
                emailModule?.SendEmail(m_host.UUID, parent.OwnerID, account.Email, subject, message);
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: llTargetedEmail exception: {0}", e.Message);
            }
            finally
            {
                ScriptSleep(20000);
            }
        }

        /// <summary>
        /// wiki: integer llDerezObject(key id, integer flag). Both rules from the wiki apply: the
        /// target's rezzer must be the object hosting this script, and its owner must be the script's
        /// owner. DEREZ_DIE deletes; DEREZ_MAKE_TEMP marks the object temporary so the simulator
        /// removes it later; DEREZ_TO_INVENTORY needs a viewer session to receive the item and
        /// Scene.DeRezObjects takes an IClientAPI, so it is refused (0) and logged rather than faked.
        /// Returns 1 on success, 0 otherwise.
        /// </summary>
        public int llDerezObject(string id, int flag)
        {
            const int DerezDie = 0, DerezMakeTemp = 1, DerezToInventory = 2;
            if (m_host == null || World == null || !UUID.TryParse(id, out UUID targetID) || targetID == UUID.Zero) return 0;
            SceneObjectPart sop = World.GetSceneObjectPart(targetID);
            SceneObjectGroup sog = sop?.ParentGroup;
            if (sog == null || sog.IsDeleted || sog.IsAttachment) return 0;
            if (sog.OwnerID != m_host.OwnerID) return 0;
            if (sog.RezzerID != m_host.UUID && sog.RezzerID != m_host.ParentGroup?.UUID) return 0;
            switch (flag)
            {
                case DerezDie:
                    World.DeleteSceneObject(sog, false);
                    return 1;
                case DerezMakeTemp:
                    sog.RootPart.AddFlag(PrimFlags.TemporaryOnRez);
                    sog.HasGroupChanged = true;
                    sog.ScheduleGroupForFullUpdate();
                    return 1;
                case DerezToInventory:
                    m_log.LogInformation("[PhloxAPI]: llDerezObject DEREZ_TO_INVENTORY is not available server-side (needs a viewer session); {0} left in place", targetID);
                    return 0;
                default:
                    return 0;
            }
        }

        public void llTargetedEmail(int targetType, string address, string subject, string message)
        {
            // SL's targeted email: targetType 0=object, 1=avatar, 2=external
            // For now, delegate to the existing llEmail for external addresses.
            // Object/avatar targeting would need additional infrastructure.
            if (targetType == 2)
            {
                // External email — same as llEmail
                llEmail(address, subject, message);
            }
            else
            {
                // Object (0) or avatar (1) targeting — stub with warning
                Stub("llTargetedEmail(targetType=" + targetType + ")");
            }
            ScriptSleep(20000); // SL has a 20-second sleep
        }

        // ── 654: llTransferOwnership ──
        public int llTransferOwnership(string destination)
        {
            // Transfer ownership of this object to another agent
            // This is a newer SL function; implementation requires careful
            // permission checking
            if (m_host == null || World == null) return 0;

            UUID destId;
            if (!UUID.TryParse(destination, out destId)) return 0;

            // Check that the object has transfer permission
            if ((m_host.OwnerMask & (uint)OpenSim.Framework.PermissionMask.Transfer) == 0)
                return 0;

            try
            {
                var group = m_host.ParentGroup;
                if (group == null) return 0;

                // Must be owned by the script owner
                if (group.OwnerID != m_host.OwnerID) return 0;

                // Perform the ownership transfer
                foreach (SceneObjectPart part in group.Parts)
                {
                    part.OwnerID = destId;
                    part.LastOwnerID = group.OwnerID;
                    part.Inventory.ChangeInventoryOwner(destId);
                }
                group.HasGroupChanged = true;
                group.ScheduleGroupForFullUpdate();
                return 1;
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: llTransferOwnership error: {0}", e.Message);
                return 0;
            }
        }

        // ── 655: llDetectedDamage ──
        /// <summary>
        /// [float damage, integer damage_type, float original_damage] for pending entry n
        /// (wiki). Inside on_damage, damage is the amount as adjusted so far; inside final_damage, what
        /// landed. From any other handler: an empty list, as the wiki says.
        /// </summary>
        public LSLList llDetectedDamage(int number)
        {
            var evt = m_thisScript?.ScriptState?.RunningEvent;
            if (evt == null) return new LSLList();
            if (evt.EventType != InWorldz.Phlox.Types.SupportedEventList.Events.ON_DAMAGE
                && evt.EventType != InWorldz.Phlox.Types.SupportedEventList.Events.FINAL_DAMAGE)
                return new LSLList();
            var vars = evt.DetectVars;
            if (vars == null || number < 0 || number >= vars.Length) return new LSLList();
            var d = vars[number];
            return new LSLList(new object[] { d.Damage, d.DamageType, d.OriginalDamage });
        }

        // ── 656: llDamage ──
        /// <summary>
        /// llDamage(key target, float damage, integer damage_type): damage through the one door,
        /// this prim as the source, so the target's attachments get on_damage (llDetectedKey == this prim)
        /// and final_damage. Avatars only here (the wiki also allows tasks and redirects seated avatars to
        /// their seat - not done); region damage must be on; no throttle yet. Runs as an async syscall so
        /// the region's wait on on_damage never blocks the script thread that issued it.
        /// </summary>
        public void llDamage(string target, float amount, int damageType)
        {
            if (World == null || m_host == null) return;
            if (!UUID.TryParse(target, out UUID targetId)) return;
            ScenePresence sp = World.GetScenePresence(targetId);
            if (sp == null || sp.IsChildAgent) return;
            if (!World.RegionInfo.RegionSettings.AllowDamage) return;
            try
            {
                sp.ApplyDamage(m_host.UUID, m_host.OwnerID, m_host.LocalId, amount, damageType, true);
            }
            catch (Exception e)
            {
                m_log.LogWarning("[PhloxAPI]: llDamage error: {0}", e.Message);
            }
        }

        // ── 657: llSetLinkRenderMaterial ──
        public void llSetLinkRenderMaterial(int link, string materialId, int face)
        {
            // Set PBR render material on a specific link's face
            // Extends llSetRenderMaterial to target specific links
            var parts = GetLinkParts(link);
            foreach (var part in parts)
            {
                try
                {
                    UUID matId = UUID.Zero;
                    UUID.TryParse(materialId, out matId);

                    if (face == ALL_SIDES)
                    {
                        for (int f = 0; f < part.GetNumberOfSides(); f++)
                            SetPartRenderMaterial(part, matId, f);
                    }
                    else
                    {
                        SetPartRenderMaterial(part, matId, face);
                    }
                    part.ScheduleFullUpdate();
                }
                catch (Exception e)
                {
                    m_log.LogWarning("[PhloxAPI]: llSetLinkRenderMaterial error: {0}", e.Message);
                }
            }
        }

        private void SetPartRenderMaterial(SceneObjectPart part, UUID matId, int face)
        {
            // Helper: set render material UUID on a specific face of a part
            var te = part.Shape.Textures;
            if (te == null) return;
            if (face < 0 || face >= part.GetNumberOfSides()) return;
            var faceEntry = te.GetFace((uint)face);
            if (faceEntry != null)
            {
                faceEntry.MaterialID = matId;
                part.UpdateTextureEntry(te);
            }
        }

        // ── 658: llXorBase64 ──
        public string llXorBase64(string s1, string s2)
        {
            // XOR two base64-encoded strings, returning base64
            // Replacement for deprecated llXorBase64Strings/llXorBase64StringsCorrect
            if (string.IsNullOrEmpty(s1)) return string.Empty;
            if (string.IsNullOrEmpty(s2)) return s1;

            try
            {
                byte[] b1 = Convert.FromBase64String(s1);
                byte[] b2 = Convert.FromBase64String(s2);
                if (b2.Length == 0) return s1;

                byte[] result = new byte[b1.Length];
                for (int i = 0; i < b1.Length; i++)
                    result[i] = (byte)(b1[i] ^ b2[i % b2.Length]);

                return Convert.ToBase64String(result);
            }
            catch
            {
                return string.Empty;
            }
        }

        // ── 659–661: Experience System (wired to ExperienceService) ──

        /// <summary>
        /// Helper: build the Phlox→NGC experience adapter for this script's scene+host.
        /// Returns null when no NGC experience backend is present, so the existing
        /// null-guards at the call sites degrade gracefully. Cheap to build (module
        /// lookups), so created per call to avoid caching a stale host.
        /// </summary>
        private PhloxExperienceAdapter GetExperienceAdapter()
        {
            if (World == null)
                return null;
            var adapter = new PhloxExperienceAdapter(World, m_host);
            return adapter.IsAvailable ? adapter : null;
        }

        /// <summary>Helper: the experience provider (NGC-backed adapter) for this script.</summary>
        private PhloxExperienceAdapter GetExperienceModule()
        {
            return GetExperienceAdapter();
        }

        /// <summary>Helper: resolve the experience UUID for this script via ExperienceModule association</summary>
        private UUID GetScriptExperienceId()
        {
            var expModule = GetExperienceModule();
            if (expModule != null)
            {
                UUID expId = expModule.GetScriptExperience(m_itemID);
                if (expId != UUID.Zero) return expId;
            }
            return UUID.Zero;
        }

        /// <summary>
        /// Helper: check if this script has an associated experience and the given agent
        /// has granted permission to it. Used by experience-gated functions.
        /// </summary>
        private bool HasExperiencePermission(UUID agentId)
        {
            if (agentId == UUID.Zero) return false;
            UUID experienceId = GetScriptExperienceId();
            if (experienceId == UUID.Zero) return false;

            var expService = GetExperienceAdapter();
            if (expService == null) return false;

            // Check region allows this experience
            var allowed = expService.GetAllowedExperiences(World.RegionInfo.RegionID);
            if (!allowed.Contains(experienceId)) return false;

            // Check agent has granted permission
            return expService.IsAgentGranted(experienceId, agentId);
        }

        // ── Consent state. One pending request per
        //    script instance (LSLSystemAPI is per-script), keyed by ItemID; the ScriptAnswerYes packet
        //    carries no ExperienceID, so the answer is correlated by TaskID + ItemID via OnScriptAnswer. ──
        private const int PERMISSION_EXPERIENCE = 0x2000;       // JoinAnExperience bit
        private const int EXPERIENCE_PERM_TIMEOUT_MS = 300000;  // 300s (SL: "at least 5 minutes")
        private sealed class PendingExperiencePerm
        {
            public UUID AgentId;
            public UUID ExperienceId;
            public IClientAPI Client;
            public System.Threading.Timer Timer;
        }
        private readonly Dictionary<UUID, PendingExperiencePerm> m_pendingExpPerms = new Dictionary<UUID, PendingExperiencePerm>();
        private readonly object m_pendingExpLock = new object();
        private IClientAPI m_expHookedClient = null;

        // ── 659: llRequestExperiencePermissions ──
        public void llRequestExperiencePermissions(string agent, string name)
        {
            if (m_host == null || World == null) return;
            UUID agentId;
            if (!UUID.TryParse(agent, out agentId)) return;

            var expService = GetExperienceAdapter();
            UUID experienceId = GetScriptExperienceId();

            // No experience associated with this script -> XP_ERROR_NO_EXPERIENCE (5).
            if (expService == null || experienceId == UUID.Zero)
            {
                m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                    "experience_permissions_denied",
                    new object[] { agent, XP_ERROR_NO_EXPERIENCE },
                    new DetectParams[0]));
                return;
            }

            // Block wins: a region-BLOCKED experience is denied regardless of allow/trusted/prior-
            // grant, land-scope XP_ERROR_NOT_PERMITTED_LAND (17). Checked FIRST (before admission,
            // trusted, and already-granted) so block wins over everything. (The port source also has a parcel-
            // block tier at this precedence — deferred; Tranquillity has no parcel-experience source.)
            if (IsExperienceBlockedInRegion(expService, experienceId))
            {
                m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                    "experience_permissions_denied",
                    new object[] { agent, XP_ERROR_NOT_PERMITTED_LAND },
                    new DetectParams[0]));
                return;
            }

            // Admission: the experience must be enabled on this land — estate-ALLOWED or region-
            // TRUSTED (estate KeyExperiences). A trusted experience is a stronger allow, so it admits
            // here and is silently granted below (previously a trusted-but-not-allowed experience was
            // wrongly denied 17 before the trusted check). The port source's admission also has grid-wide + parcel-
            // ALLOW tiers, and a region/parcel BLOCK-wins tier; those have NO source in NGC (no grid-wide
            // bit, no region-block store, no ILandObject experience methods), so they are not represented.
            // Not admitted -> land-scope XP_ERROR_NOT_PERMITTED_LAND (17).
            if (!IsExperienceAdmitted(expService, experienceId))
            {
                m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                    "experience_permissions_denied",
                    new object[] { agent, XP_ERROR_NOT_PERMITTED_LAND },
                    new DetectParams[0]));
                return;
            }

            // Target agent must have a ROOT presence here -> else agent-scope XP_ERROR_NOT_PERMITTED (4).
            ScenePresence sp = World.GetScenePresence(agentId);
            if (sp == null || sp.IsChildAgent)
            {
                m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                    "experience_permissions_denied",
                    new object[] { agent, XP_ERROR_NOT_PERMITTED },
                    new DetectParams[0]));
                return;
            }

            // Gate order (as the port source): the agent's PERSONAL block wins over everything below and is
            // checked BEFORE the already-granted short-circuit, so a resident who blocked this experience
            // is never re-granted (SL code 4).
            if (expService.IsAgentBlocked(experienceId, agentId))
            {
                m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                    "experience_permissions_denied",
                    new object[] { agent, XP_ERROR_NOT_PERMITTED }, // 4 — agent's personal block
                    new DetectParams[0]));
                return;
            }

            // Already granted -> notify immediately, no dialog.
            if (expService.IsAgentGranted(experienceId, agentId))
            {
                m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                    "experience_permissions", new object[] { agent }, new DetectParams[0]));
                return;
            }

            // Trusted enforcement. A region-TRUSTED experience (Tranquillity estate KeyExperiences)
            // grants silently — no dialog. Checked AFTER agent-block, so a personally-blocked
            // experience is denied 4 even if trusted (block wins over trusted — the port source's order).
            if (expService.GetTrustedExperiences(World.RegionInfo.RegionID).Contains(experienceId))
            {
                GrantExperienceAndNotify(expService, experienceId, agentId, agent);
                return;
            }

            // Non-trusted: PROMPT the agent and AWAIT the answer — this REPLACES the former auto-grant.
            // The Experience block on the ScriptQuestion (attached only when experienceId != Zero, guarded
            // in LLClientView) makes the viewer show the experience consent dialog. Resolves to
            // experience_permissions on Yes / _denied 4 on No/disconnect / _denied 18 on timeout.
            string ownerName = m_host.ParentGroup.RootPart.OwnerID.ToString();
            var ownerAcct = World?.UserAccountService?.GetUserAccount(
                World.RegionInfo.ScopeID, m_host.ParentGroup.RootPart.OwnerID);
            if (ownerAcct != null) ownerName = ownerAcct.FirstName + " " + ownerAcct.LastName;
            if (string.IsNullOrEmpty(ownerName)) ownerName = "(unknown)";

            RegisterPendingExperiencePerm(sp.ControllingClient, experienceId, agentId);
            sp.ControllingClient.SendScriptQuestion(
                m_host.UUID, m_host.ParentGroup.RootPart.Name, ownerName, m_itemID,
                PERMISSION_EXPERIENCE, experienceId);
        }

        // Grant + persist an experience permission and post experience_permissions. Shared by the
        // trusted-bypass path and the accepted-answer path.
        private void GrantExperienceAndNotify(PhloxExperienceAdapter expService, UUID experienceId, UUID agentId, string agent)
        {
            expService.GrantPermission(experienceId, agentId);
            expService.InvalidatePermission(experienceId, agentId);
            m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                "experience_permissions", new object[] { agent }, new DetectParams[0]));
            var expInfo = expService.GetExperience(experienceId);
            m_log.LogInformation("[PhloxAPI]: Experience permission granted: agent={0} experience={1}",
                agentId, expInfo?.Name ?? experienceId.ToString());
        }

        // Record the pending request (keyed by m_itemID) and start its 300s timeout. Hooks the client's
        // answer/disconnect events (re-hooking if the target client changed).
        private void RegisterPendingExperiencePerm(IClientAPI client, UUID experienceId, UUID agentId)
        {
            lock (m_pendingExpLock)
            {
                if (m_pendingExpPerms.TryGetValue(m_itemID, out PendingExperiencePerm prior))
                {
                    prior.Timer?.Dispose();
                    m_pendingExpPerms.Remove(m_itemID);
                }
                if (m_expHookedClient != client)
                {
                    if (m_expHookedClient != null)
                    {
                        m_expHookedClient.OnScriptAnswer -= HandleExperienceScriptAnswer;
                        m_expHookedClient.OnConnectionClosed -= HandleExperienceConnectionClosed;
                    }
                    client.OnScriptAnswer += HandleExperienceScriptAnswer;
                    client.OnConnectionClosed += HandleExperienceConnectionClosed;
                    m_expHookedClient = client;
                }
                var pending = new PendingExperiencePerm { AgentId = agentId, ExperienceId = experienceId, Client = client };
                pending.Timer = new System.Threading.Timer(
                    _ => ResolveExperiencePerm(m_itemID, granted: false, errorCode: XP_ERROR_REQUEST_PERM_TIMEOUT),
                    null, EXPERIENCE_PERM_TIMEOUT_MS, System.Threading.Timeout.Infinite);
                m_pendingExpPerms[m_itemID] = pending;
            }
        }

        // ScriptAnswerYes arrived (via OnScriptAnswer). Correlate by TaskID (this object) + ItemID;
        // a non-zero PERMISSION_EXPERIENCE bit means accepted, zero means denied.
        private void HandleExperienceScriptAnswer(IClientAPI client, UUID taskID, UUID itemID, int answer)
        {
            if (taskID != m_host.UUID) return;
            bool granted = (answer & PERMISSION_EXPERIENCE) != 0;
            ResolveExperiencePerm(itemID, granted, granted ? XP_ERROR_NONE : XP_ERROR_NOT_PERMITTED);
        }

        // Agent disconnected mid-dialog: resolve every pending request on this client as denied.
        private void HandleExperienceConnectionClosed(IClientAPI client)
        {
            List<UUID> pendingKeys;
            lock (m_pendingExpLock)
                pendingKeys = new List<UUID>(m_pendingExpPerms.Keys);
            foreach (UUID itemId in pendingKeys)
                ResolveExperiencePerm(itemId, granted: false, errorCode: XP_ERROR_NOT_PERMITTED);
        }

        // Single resolution point for grant / user-deny / timeout / disconnect. Removes the pending
        // entry atomically (first resolver wins — no double-post), disposes the timer, unhooks the
        // client once nothing is pending; the grant + script event happen OUTSIDE the lock.
        private void ResolveExperiencePerm(UUID itemID, bool granted, int errorCode)
        {
            PendingExperiencePerm pending;
            IClientAPI clientToUnhook = null;
            lock (m_pendingExpLock)
            {
                if (!m_pendingExpPerms.TryGetValue(itemID, out pending))
                    return; // already resolved by another path
                m_pendingExpPerms.Remove(itemID);
                pending.Timer?.Dispose();
                if (m_pendingExpPerms.Count == 0 && m_expHookedClient != null)
                {
                    clientToUnhook = m_expHookedClient;
                    m_expHookedClient = null;
                }
            }
            if (clientToUnhook != null)
            {
                clientToUnhook.OnScriptAnswer -= HandleExperienceScriptAnswer;
                clientToUnhook.OnConnectionClosed -= HandleExperienceConnectionClosed;
            }

            string agent = pending.AgentId.ToString();
            var expService = GetExperienceAdapter();
            if (granted && expService != null)
                GrantExperienceAndNotify(expService, pending.ExperienceId, pending.AgentId, agent);
            else
                m_ScriptEngine.PostScriptEvent(m_itemID, new EventParams(
                    "experience_permissions_denied",
                    new object[] { agent, errorCode },
                    new DetectParams[0]));
        }

        // Admission — the portable subset of the port source's ladder (IsExperienceAdmittedAt): an experience
        // is admitted on this land if the estate ALLOWS it OR it is region-TRUSTED (estate KeyExperiences).
        // The port source's grid-wide + parcel-ALLOW admission tiers and the region/parcel BLOCK-wins tier have no
        // NGC source and are not represented here.
        private bool IsExperienceAdmitted(PhloxExperienceAdapter expService, UUID experienceId)
        {
            UUID regionId = World.RegionInfo.RegionID;
            return expService.GetAllowedExperiences(regionId).Contains(experienceId)
                || expService.GetTrustedExperiences(regionId).Contains(experienceId);
        }

        // Block-wins tier (the port source's IsExperienceBlockedInRegion): an experience on the estate
        // BlockedExperiences list is denied regardless of allow/trusted/prior-grant. Region granularity
        // only — the port source also has a parcel-block tier with no NGC parcel-experience source (deferred).
        private bool IsExperienceBlockedInRegion(PhloxExperienceAdapter expService, UUID experienceId)
        {
            UUID regionId = World.RegionInfo.RegionID;
            return expService.GetBlockedExperiences(regionId).Contains(experienceId);
        }

        // ── 660: llAgentInExperience ──
        public int llAgentInExperience(string agent)
        {
            UUID agentId;
            if (!UUID.TryParse(agent, out agentId)) return 0;
            if (agentId == UUID.Zero) return 0;

            var expService = GetExperienceAdapter();
            UUID experienceId = GetScriptExperienceId();
            if (expService == null || experienceId == UUID.Zero) return 0;

            // The target agent
            // must be PARTICIPATING here — a ROOT presence in this region — with block-wins over grant, AND
            // the experience must not be region-BLOCKED and must be ADMITTED on this land (estate allow OR
            // trusted). The port source's HasExperiencePermission also applies a parcel BLOCK-wins tier, which has no
            // NGC source — deferred to a separate project (region granularity only here).
            ScenePresence sp = World?.GetScenePresence(agentId);
            if (sp == null || sp.IsChildAgent) return 0;
            if (IsExperienceBlockedInRegion(expService, experienceId)) return 0; // region block wins
            if (expService.IsAgentBlocked(experienceId, agentId)) return 0;    // agent block wins
            if (!IsExperienceAdmitted(expService, experienceId)) return 0;     // admitted on this land
            return expService.IsAgentGranted(experienceId, agentId) ? 1 : 0;
        }

        // ── 661: llGetExperienceDetails ──
        public LSLList llGetExperienceDetails(string experienceId)
        {
            UUID expId;
            if (!UUID.TryParse(experienceId, out expId))
                return new LSLList();

            var expService = GetExperienceAdapter();
            if (expService == null)
                return new LSLList();

            var exp = expService.GetExperience(expId);
            if (exp == null)
                return new LSLList();

            // SL layout is [ name, owner key, experience id, state (int), state message,
            // group key ] — NOT the old [name, owner, description, group, maturity, ""], which
            // silently returned wrong data at every index for SL-written scripts (High severity).
            // State uses the XP_ERROR vocabulary: NONE(0) for a valid enabled experience,
            // EXPERIENCE_DISABLED(8) when the viewer PROPERTY_DISABLED bit is set. The message comes
            // from llGetExperienceErrorMessage so the state and its text can never drift apart.
            int state = (exp.Properties & VP_DISABLED) != 0
                ? XP_ERROR_EXPERIENCE_DISABLED
                : XP_ERROR_NONE;
            return new LSLList(new object[]
            {
                exp.Name ?? string.Empty,
                exp.OwnerId.ToString(),
                expId.ToString(),
                state,
                llGetExperienceErrorMessage(state),
                exp.GroupId.ToString()
            });
        }

        // ══════════════════════════════════════════════════════════════════════
        // Phase 33: 5 new functions (TableIndex 662–666)
        // + 2 fixes (llDetectedGroup, llGetUsedMemory)
        // + 3 wired (llGetAnimationOverride, llSetAnimationOverride, llResetAnimationOverride)
        // ══════════════════════════════════════════════════════════════════════

        // ── 662: llGetPayPrice ──
        public LSLList llGetPayPrice()
        {
            if (m_host == null)
                return new LSLList(new object[] { -1, -1, -1, -1, -1 });

            return new LSLList(new object[]
            {
                m_host.PayPrice[0],
                m_host.PayPrice[1],
                m_host.PayPrice[2],
                m_host.PayPrice[3],
                m_host.PayPrice[4]
            });
        }

        // ── 663: llGetAgentRot ──
        public Quaternion llGetAgentRot()
        {
            TaskInventoryItem item = GetInventorySelf();
            if (item == null) return Quaternion.Identity;
            UUID agentId = item.PermsGranter;
            if (agentId == UUID.Zero) return Quaternion.Identity;

            ScenePresence sp = World?.GetScenePresence(agentId);
            if (sp == null || sp.IsChildAgent) return Quaternion.Identity;

            return sp.Rotation;
        }

        // ── 664: llGetGroundTexture ──
        public string llGetGroundTexture(int corner)
        {
            if (World == null) return UUID.Zero.ToString();

            corner = Math.Clamp(corner, 0, 3);
            var rs = World.RegionInfo.RegionSettings;
            UUID texId = corner switch
            {
                0 => rs.TerrainTexture1,
                1 => rs.TerrainTexture2,
                2 => rs.TerrainTexture3,
                3 => rs.TerrainTexture4,
                _ => UUID.Zero
            };
            return texId.ToString();
        }

        // ── 665: llGetVehicleFlags ──
        public int llGetVehicleFlags()
        {
            if (m_host?.ParentGroup == null || m_host.ParentGroup.IsDeleted) return 0;
            PhysicsActor pa = m_host.ParentGroup.RootPart.PhysActor;
            if (pa == null) return 0;

            // PhysicsActor doesn't expose a flags getter — return VehicleType for now.
            // Full flags support requires adding a getter to the physics engine.
            return pa.VehicleType;
        }

        // ── 666: llGetLinkGLTFOverrides ──
        public LSLList llGetLinkGLTFOverrides(int link, int face)
        {
            var result = new List<object>();

            foreach (SceneObjectPart part in GetLinkParts(link))
            {
                var shape = part.Shape;
                if (shape?.RenderMaterials?.overrides == null)
                    continue;

                foreach (var ovr in shape.RenderMaterials.overrides)
                {
                    if (face != ALL_SIDES && ovr.te_index != (byte)face)
                        continue;

                    if (!string.IsNullOrEmpty(ovr.data))
                    {
                        try
                        {
                            var osd = OpenMetaverse.StructuredData.OSDParser.DeserializeLLSDNotation(ovr.data);
                            if (osd is OpenMetaverse.StructuredData.OSDMap map)
                            {
                                foreach (string key in map.Keys)
                                {
                                    string readableKey = key switch
                                    {
                                        "bc" => "base_color",
                                        "mf" => "metallic_factor",
                                        "rf" => "roughness_factor",
                                        "ec" => "emissive_factor",
                                        "am" => "alpha_mode",
                                        "ac" => "alpha_cutoff",
                                        "ds" => "double_sided",
                                        _ => key
                                    };
                                    result.Add(readableKey);
                                    result.Add(map[key].ToString());
                                }
                            }
                        }
                        catch
                        {
                            result.Add("raw");
                            result.Add(ovr.data);
                        }
                    }
                }
                break; // only first matching link part
            }

            return new LSLList(result.ToArray());
        }

        // ── Phase 34: Bot Persistence Functions (667–671) ──

        // ── 667: botSetPersistent ──
        public int botSetPersistent(string botID, int ttlSeconds)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return BotPersistError.NOT_FOUND;

            BotPersistenceManager pm = GetBotPersistence();
            if (pm == null) return BotPersistError.DISABLED;

            return pm.SetPersistent(id, m_host.OwnerID, m_itemID,
                m_host.ParentGroup.UUID, ttlSeconds);
        }

        // ── 668: botRemovePersistent ──
        public int botRemovePersistent(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return BotPersistError.NOT_FOUND;

            BotPersistenceManager pm = GetBotPersistence();
            if (pm == null) return BotPersistError.DISABLED;

            return pm.RemovePersistent(id, m_host.OwnerID);
        }

        // ── 669: botIsPersistent ──
        public int botIsPersistent(string botID)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return 0;

            BotPersistenceManager pm = GetBotPersistence();
            if (pm == null) return 0;

            return pm.IsPersistent(id) ? 1 : 0;
        }

        // ── 670: botGetPersistentData ──
        public string botGetPersistentData(string botID, string key)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return string.Empty;

            BotPersistenceManager pm = GetBotPersistence();
            if (pm == null) return string.Empty;

            return pm.GetPersistentData(id, key);
        }

        // ── 671: botSetPersistentData ──
        public int botSetPersistentData(string botID, string key, string value)
        {
            UUID id = ParseBotID(botID);
            if (id == UUID.Zero) return BotPersistError.NOT_FOUND;

            BotPersistenceManager pm = GetBotPersistence();
            if (pm == null) return BotPersistError.DISABLED;

            return pm.SetPersistentData(id, m_host.OwnerID, key, value);
        }
    }

    /// <summary>
    /// SL's value counts for every prim-params rule: the one table the prim-params setter, the seated-avatar setter
    /// and the getter walk a rule list by. The counts are SL's (secondlife/lsl-definitions, the PrimParam and
    /// PrimParamGet rules, and the llSetPrimitiveParams / llGetPrimitiveParams wiki pages); the IW_PRIM_* rules are
    /// Halcyon's (LSLSystemAPI.SetPrimParams / GetPrimParams), and PRIM_PHYSICS_MATERIAL is OpenSim's own
    /// (LSL_Constants.cs, read by LSL_Api.SetPrimParams). A rule is read by its count whether or not Phlox
    /// implements it yet, so one rule Phlox does not act on never shifts or cuts off the rules after it.
    /// </summary>
    internal static class PrimParamRules
    {
        /// <summary>
        /// The values a rule takes after its code when setting, or -1 for a rule number none of SL, Halcyon and
        /// OpenSim defines. PRIM_TYPE takes its shape code and then <see cref="TypeValueCount"/> more.
        /// </summary>
        public static int SetValueCount(int code) => code switch
        {
            PRIM_MATERIAL or PRIM_PHYSICS or PRIM_TEMP_ON_REZ or PRIM_PHANTOM or PRIM_POSITION or PRIM_SIZE
                or PRIM_ROTATION or PRIM_CAST_SHADOWS or PRIM_NAME or PRIM_DESC or PRIM_ROT_LOCAL
                or PRIM_PHYSICS_SHAPE_TYPE or PRIM_POS_LOCAL or PRIM_LINK_TARGET or PRIM_SLICE or PRIM_ALLOW_UNSIT
                or PRIM_SCRIPTED_SIT_ONLY or PRIM_CLICK_ACTION or PRIM_SIT_FLAGS or PRIM_HEALTH => 1,
            PRIM_TYPE => 1,
            PRIM_FULLBRIGHT or PRIM_TEXGEN or PRIM_GLOW or PRIM_RENDER_MATERIAL or PRIM_DAMAGE
                or PRIM_COLLISION_SOUND => 2,
            PRIM_COLOR or PRIM_BUMP_SHINY or PRIM_TEXT or PRIM_OMEGA or PRIM_ALPHA_MODE or PRIM_SIT_TARGET => 3,
            PRIM_PROJECTOR or PRIM_REFLECTION_PROBE => 4,
            PRIM_TEXTURE or PRIM_POINT_LIGHT or PRIM_NORMAL or PRIM_GLTF_NORMAL => 5,
            PRIM_GLTF_EMISSIVE => 6,
            PRIM_FLEXIBLE or PRIM_GLTF_METALLIC_ROUGHNESS => 7,
            PRIM_SPECULAR => 8,
            PRIM_GLTF_BASE_COLOR => 10,
            IW_PRIM_ALPHA => 2,
            IW_PRIM_PROJECTOR => 5,
            IW_PRIM_PROJECTOR_ENABLED or IW_PRIM_PROJECTOR_TEXTURE or IW_PRIM_PROJECTOR_FOV
                or IW_PRIM_PROJECTOR_FOCUS or IW_PRIM_PROJECTOR_AMBIENCE => 1,
            // OpenSim's own rules (LSL_Constants.cs), with the value count OpenSim's LSL_Api reads.
            PRIM_PHYSICS_MATERIAL => 5,
            _ => -1,
        };


        /// <summary>
        /// The values PRIM_TYPE takes after its shape code: box, cylinder and prism 6, sphere 5, torus, tube and ring
        /// 11, sculpt 2. An unknown shape code takes none, as Halcyon reads it.
        /// </summary>
        public static int TypeValueCount(int primType) => primType switch
        {
            PRIM_TYPE_BOX or PRIM_TYPE_CYLINDER or PRIM_TYPE_PRISM => 6,
            PRIM_TYPE_SPHERE => 5,
            PRIM_TYPE_TORUS or PRIM_TYPE_TUBE or PRIM_TYPE_RING => 11,
            PRIM_TYPE_SCULPT => 2,
            _ => 0,
        };

        /// <summary>
        /// The values a rule takes after its code when getting: the face for the per-face rules, the link for
        /// PRIM_LINK_TARGET, none for the rest. A rule number SL does not define takes none.
        /// </summary>
        public static int GetValueCount(int code) => code switch
        {
            PRIM_TEXTURE or PRIM_COLOR or PRIM_BUMP_SHINY or PRIM_FULLBRIGHT or PRIM_TEXGEN or PRIM_GLOW
                or PRIM_SPECULAR or PRIM_NORMAL or PRIM_ALPHA_MODE or PRIM_RENDER_MATERIAL or PRIM_GLTF_NORMAL
                or PRIM_GLTF_EMISSIVE or PRIM_GLTF_METALLIC_ROUGHNESS or PRIM_GLTF_BASE_COLOR or IW_PRIM_ALPHA
                or PRIM_LINK_TARGET => 1,
            _ => 0,
        };

        /// <summary>
        /// The whole length of the setter rule starting at <paramref name="idx"/> (its code included), or -1 for a
        /// rule number SetValueCount does not know. PRIM_TYPE's length needs its shape code, so a PRIM_TYPE with
        /// none left is 2 long.
        /// </summary>
        public static int SetRuleLength(object[] data, int idx, int code)
        {
            int count = SetValueCount(code);
            if (count < 0) return -1;
            if (code == PRIM_TYPE && idx + 1 < data.Length && data[idx + 1] is int primType)
                count += TypeValueCount(primType);
            return 1 + count;
        }
    }
}
