using Xunit;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A whole script engine on a test scene, driven by hand: rez a script the way
/// <c>EventManager.OnRezScript</c> does, then pump <c>DoWork</c> instead of running the master
/// scheduler's thread, so a test is deterministic and cannot hang.
///
/// <para>
/// This is the harness the scheduler's tests lacked. Everything
/// before it either compiled a script (<see cref="PhloxCompiler"/>) or asserted the scheduler's
/// source text; neither could see whether a script instance actually runs, which is precisely what
/// two earlier builds both failed to do in world.
/// </para>
/// </summary>
public sealed class SchedulerHarness : IDisposable
{
    public TestScene Scene { get; }
    public PhloxEngine Engine { get; }
    public SceneObjectPart Prim { get; }

    private readonly object m_loader;
    private readonly object m_exe;

    /// <summary>
    /// A real YEngine on the same scene, added BEFORE Phlox as on a region running both
    /// ([REGIONMODULE] Adding scene "<region>" to non-shared module "YEngine" precedes "InWorldz.Phlox"), so
    /// SceneObjectPartInventory.GetScriptErrors asks YEngine first. Null unless requested.
    /// </summary>
    public OpenSim.Region.ScriptEngine.Yengine.Yengine YEngine { get; }
    /// <summary>The core WorldComm module that carries YEngine's listens, when YEngine is on the scene.</summary>
    public OpenSim.Region.CoreModules.Scripting.WorldComm.WorldCommModule WorldComm { get; }
    private readonly string m_yengineDir;

    /// <param name="configure">A hook to add config sections (e.g. [OSSL]) before the engine reads them.</param>
    /// <param name="withYEngine">Register YEngine alongside Phlox, first, as a region running both does.</param>
    /// <param name="bytecodeDir">
    /// The engine's bytecode cache folder. Null (the default) is the calling test class's own folder, see
    /// <see cref="BytecodeDirForCaller"/>; <see cref="ProductionBytecodeDir"/> is the loader's own shared folder.
    /// </param>
    public SchedulerHarness(Action<IConfigSource> configure = null, bool withYEngine = false, string bytecodeDir = null)
    {
        var config = new IniConfigSource();
        var phlox = config.AddConfig("InWorldz.Phlox");
        phlox.Set("Enabled", "true");
        var startup = config.AddConfig("Startup");
        startup.Set("DefaultScriptEngine", "InWorldz.Phlox");
        configure?.Invoke(config);
        Config = config;

        Scene = new SceneHelpers().SetupScene();

        if (withYEngine)
        {
            // A region running YEngine has the core WorldComm module, which carries YEngine's listens; without
            // it YEngine's llListen and chat reach nothing, and chat between the engines cannot be tested.
            WorldComm = new OpenSim.Region.CoreModules.Scripting.WorldComm.WorldCommModule();
            WorldComm.Initialise(config);
            WorldComm.AddRegion(Scene);

            m_yengineDir = Path.Combine(Path.GetTempPath(), "phlox-harness-yengine-" + Guid.NewGuid().ToString("N"));
            var y = config.AddConfig("YEngine");
            y.Set("Enabled", "true");
            y.Set("ScriptEnginesPath", m_yengineDir);
            YEngine = new OpenSim.Region.ScriptEngine.Yengine.Yengine();
            YEngine.Initialise(config);
            YEngine.AddRegion(Scene);
        }

        Engine = new PhloxEngine();
        BytecodeDir = bytecodeDir ?? BytecodeDirForCaller();
        // A relative folder is the production one, resolved as the loader resolves it; the seam is left unset for it.
        if (Path.IsPathRooted(BytecodeDir)) Engine.BytecodeCacheDir = BytecodeDir;
        Engine.Initialise(config);
        Engine.AddRegion(Scene);
        // RegionLoaded needs an IWorldComm; the test scene has none, so one is registered first.
        if (Scene.RequestModuleInterface<IWorldComm>() is null)
            Scene.RegisterModuleInterface<IWorldComm>(NullWorldComm.Create());
        Engine.RegionLoaded(Scene);
        if (YEngine != null)
        {
            // As the region does after every module's AddRegion: YEngine hooks its script events (listen among
            // them) in RegionLoaded, and its worker threads run scripts only after StartProcessing.
            YEngine.RegionLoaded(Scene);
            YEngine.StartProcessing();
        }

        var sog = SceneHelpers.AddSceneObject(Scene, "Phlox test prim", UUID.Random());
        Prim = sog.RootPart;

        // llSay goes Scene.SimChat -> EventManager.OnChatFromWorld (Scene.PacketHandlers.cs:51-85),
        // so the real path is observed rather than a stub API injected into the engine - the engine
        // builds its own LSLSystemAPI inside FinishedLoading and takes no seam for one.
        // NPC chat arrives as CLIENT chat (NPCAvatar is a client), with the NPC as sender.
        Scene.EventManager.OnChatFromClient += (sender, chat) =>
        {
            lock (m_said) m_clientChat.Add((chat.Channel, chat.Message ?? string.Empty, chat.Sender?.AgentId ?? chat.SenderUUID));
        };
        Scene.EventManager.OnChatFromWorld += (sender, chat) =>
        {
            lock (m_said)
            {
                m_said.Add(chat.Message ?? string.Empty);
                m_saidOn.Add((chat.Channel, chat.Message ?? string.Empty));
            }
        };

        m_loader = Field(Engine, "m_ScriptLoader");
        m_exe = Field(Engine, "m_ExeScheduler");
        Assert.NotNull(m_loader);
        Assert.NotNull(m_exe);

        // The master scheduler owns a thread; this harness drives DoWork itself instead, so stop it.
        StopMasterThread();
    }

    /// <summary>The loader's own folder (PhloxScriptLoader.CACHE_DIR), relative to the working directory.</summary>
    public const string ProductionBytecodeDir = "ScriptEngines/Phlox/bytecode";

    /// <summary>The folder this harness's engine caches bytecode in.</summary>
    public string BytecodeDir { get; }

    /// <summary>
    /// One root per test run for the classes' bytecode folders, under the test output folder. The harness made
    /// it, so the harness removes it when the process exits. The test host can be ended before that finishes (a full
    /// run left part of its root behind), so a new run also removes the roots earlier runs of this harness left:
    /// only "run-&lt;pid&gt;-&lt;id&gt;" folders under harness-bytecode, and only when that process is no longer running.
    /// </summary>
    private static readonly System.Lazy<string> s_bytecodeRunRoot = new(() =>
    {
        string parent = Path.Combine(AppContext.BaseDirectory, "ScriptEngines", "Phlox", "harness-bytecode");
        Directory.CreateDirectory(parent);
        foreach (string earlier in Directory.GetDirectories(parent, "run-*"))
        {
            string[] parts = Path.GetFileName(earlier).Split('-');
            if (parts.Length == 3 && int.TryParse(parts[1], out int pid) && pid != Environment.ProcessId && !ProcessRunning(pid))
                RemoveTree(earlier);
        }

        string root = Path.Combine(parent, "run-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RemoveTree(root);
        return root;
    });

    private static bool ProcessRunning(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch { return true; }   // cannot tell: leave it
    }

    /// <summary>Removes a harness bytecode root file by file, going on past any entry it cannot remove.</summary>
    private static void RemoveTree(string dir)
    {
        try
        {
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                try { File.Delete(f); } catch { }
            foreach (string d in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                try { Directory.Delete(d); } catch { }
            Directory.Delete(dir);
        }
        catch { }
    }

    /// <summary>
    /// The bytecode folder for the test class building this harness. Every loader used to share the working
    /// directory's "ScriptEngines/Phlox/bytecode", and its schema stamp is read outside the loader's try
    /// (PhloxScriptLoader.EnsureCacheSchemaVersion): with no stamp on disk, classes starting in parallel each wrote it
    /// while another read it, and the read threw IOException out of harness set-up.
    /// The class is the outermost frame on the stack whose method belongs to this test assembly (the test method, its
    /// async state machine or a lambda, walked up to the top-level type), so the 389 construction sites need no
    /// change. xUnit runs one class's tests one at a time, so harnesses of one class share a folder as all harnesses
    /// did before (a restore test's second engine can still load the first one's bytecode), and no two classes do.
    /// A harness built from no test class gets a folder of its own.
    /// </summary>
    private static string BytecodeDirForCaller()
    {
        Assembly tests = typeof(SchedulerHarness).Assembly;
        Type owner = null;
        foreach (var frame in new System.Diagnostics.StackTrace(1, false).GetFrames())
        {
            Type t = frame.GetMethod()?.DeclaringType;
            if (t == null || t.Assembly != tests) continue;
            while (t.DeclaringType != null) t = t.DeclaringType;
            if (t != typeof(SchedulerHarness)) owner = t;   // keep going: the last one found is the outermost
        }
        string name = owner?.FullName ?? ("unowned-" + Guid.NewGuid().ToString("N"));
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        string dir = Path.Combine(s_bytecodeRunRoot.Value, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static object Field(object o, string name)
        => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o);

    private void StopMasterThread()
    {
        // The thread only - Stop() is region shutdown and would also stop the loader's compile thread.
        var ms = Field(Engine, "m_MasterScheduler");
        var stop = ms?.GetType().GetMethod("StopThread", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                   ?? ms?.GetType().GetMethod("Stop", BindingFlags.Public | BindingFlags.Instance);
        stop?.Invoke(ms, null);
    }

    /// <summary>
    /// Put a script in the prim's inventory and rez it exactly as <c>PhloxEngine.OnRezScript</c>
    /// does. The inventory item is real: <c>PhloxScriptLoader.FindAssetId</c> reads
    /// <c>Prim.Inventory.GetInventoryItem</c> and logs an error and drops the load without one
    /// (<c>PhloxScriptLoader.cs:301-307</c>), so a harness that skips this tests nothing.
    /// </summary>
    /// <summary>
    /// Rez with BOTH ids pinned. A restore test has to stand a second engine up and rez the
    /// same item and asset, because StateManager.LoadState keys on the item id and discards the row
    /// when the asset id does not match.
    /// </summary>
    public UUID RezScript(string source, UUID assetId, UUID itemId) => RezScript(source, assetId, itemId, running: true);

    /// <summary>Rez with the item's Running flag as given - false is the viewer's unticked checkbox.</summary>
    public UUID RezScript(string source, UUID assetId, UUID itemId, bool running)
    {
        var item = TaskInventoryHelpers.AddScript(
            Scene.AssetService, Prim, itemId, assetId, "script" + (++m_scriptSeq), source);
        item.ScriptRunning = running;
        var rez = Engine.GetType().GetMethod("OnRezScript", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(rez);
        rez!.Invoke(Engine, new object[] { Prim.LocalId, item.ItemID, source, 0, false, Engine.Name, 0 });
        return item.ItemID;
    }

    /// <summary>The engine's StateManager, which is internal - reached by reflection.</summary>
    public object StateManagerOf() => Engine.GetType()
        .GetProperty("StateManager", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
        ?.GetValue(Engine);

    /// <summary>Save this script the way shutdown does, through ScriptUnloaded.</summary>
    public void SaveState(UUID itemId)
    {
        var sm = StateManagerOf();
        Assert.NotNull(sm);
        var interp = InterpreterFor(itemId);
        Assert.NotNull(interp);
        sm.GetType().GetMethod("ScriptUnloaded")!.Invoke(sm, new[] { interp });
    }

    /// <summary>
    /// The real shutdown save. PhloxEngine.OnShutdown calls StateManager.Stop() and nothing else - no
    /// ScriptUnloaded for any script - so only the dirty set reaches the row. SaveState above is the unload path,
    /// which a region stop never takes.
    /// </summary>
    public void ShutdownStateManager()
    {
        var sm = StateManagerOf();
        Assert.NotNull(sm);
        sm.GetType().GetMethod("Stop")!.Invoke(sm, null);
    }

    public UUID RezScript(string source, UUID assetId = default)
    {
        var item = TaskInventoryHelpers.AddScript(
            Scene.AssetService, Prim, UUID.Random(), assetId.IsZero() ? UUID.Random() : assetId,
            "script" + (++m_scriptSeq), source);

        var rez = Engine.GetType().GetMethod("OnRezScript", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(rez);
        rez!.Invoke(Engine, new object[] { Prim.LocalId, item.ItemID, source, 0, false, Engine.Name, 0 });
        return item.ItemID;
    }

    /// <summary>Rez a script into a part other than the harness prim (a second attachment).</summary>
    public UUID RezScriptInto(SceneObjectPart part, string source)
    {
        var item = TaskInventoryHelpers.AddScript(
            Scene.AssetService, part, UUID.Random(), UUID.Random(), "script" + (++m_scriptSeq), source);
        var rez = Engine.GetType().GetMethod("OnRezScript", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(rez);
        rez!.Invoke(Engine, new object[] { part.LocalId, item.ItemID, source, 0, false, Engine.Name, 0 });
        return item.ItemID;
    }

    /// <summary>The engine's PhloxScriptLoader.</summary>
    public object Loader => m_loader;

    /// <summary>
    /// Save new text into an existing script item the way the viewer's Save does - the item gets a new
    /// asset, then the region removes and re-rezzes it (Scene.CapsUpdateTaskInventoryScriptAsset).
    /// </summary>
    public void ResaveScript(UUID itemId, string source)
    {
        var item = Prim.Inventory.GetInventoryItem(itemId);
        Assert.NotNull(item);
        var asset = AssetHelpers.CreateAsset(UUID.Random(), OpenMetaverse.AssetType.LSLText, source, item.OwnerID);
        Scene.AssetService.Store(asset);
        item.AssetID = asset.FullID;
        Prim.Inventory.UpdateInventoryItem(item);
        Assert.Equal(asset.FullID, Prim.Inventory.GetInventoryItem(itemId).AssetID);
        var remove = Engine.GetType().GetMethod("OnRemoveScript", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var rez = Engine.GetType().GetMethod("OnRezScript", BindingFlags.NonPublic | BindingFlags.Instance)!;
        remove.Invoke(Engine, new object[] { Prim.LocalId, itemId });
        rez.Invoke(Engine, new object[] { Prim.LocalId, itemId, source, 0, false, Engine.Name, 0 });
    }

    /// <summary>Is the script on the run queue?</summary>
    public bool IsOnRunQueue(UUID itemId) => ((global::Phlox.ScriptEngine.PhloxExecutionScheduler)m_exe).IsOnRunQueue(itemId);

    /// <summary>The scheduler's status record for a script, as `phlox status` reads it.</summary>
    public string StatusOf(UUID itemId)
    {
        var st = ((global::Phlox.ScriptEngine.PhloxExecutionScheduler)m_exe).GetStatus(itemId);
        return $"Found={st.Found} RunState={st.RunState} Enabled={st.Enabled} GeneralEnable={st.GeneralEnable} LocalDisable={st.LocalDisable ?? "None"} queued={st.QueuedEvents} terminated={st.TerminatedReason ?? "-"}";
    }

    private int m_scriptSeq;

    /// <summary>
    /// Add a real client to the scene so what the region SENDS can be asserted, not just
    /// what it stores on the part.
    /// </summary>
    public OpenSim.Tests.Common.TestClient AddClient()
    {
        var sp = SceneHelpers.AddScenePresence(Scene, OpenMetaverse.UUID.Random());
        return (OpenSim.Tests.Common.TestClient)sp.ControllingClient;
    }

    /// <summary>Where a load got to: queues, whether an interpreter exists, and what it said.</summary>
    public string Diagnose(UUID itemId)
    {
        int Count(object owner, string field)
        {
            var v = owner.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(owner);
            return v is System.Collections.ICollection c ? c.Count : -1;
        }
        var all = Field(m_exe, "m_AllScripts") as System.Collections.IDictionary;
        return $"pendingLoads={Count(m_loader, "m_PendingLoads")} " +
               $"waitingForCompile={Count(m_loader, "m_WaitingForCompile")} " +
               $"loadedScripts={Count(m_loader, "m_LoadedScripts")} " +
               $"allScripts={all?.Count ?? -1} " +
               $"interpreter={(InterpreterFor(itemId) is null ? "none" : "created")} " +
               $"runState={RunStateOf(itemId)} said=[{string.Join(",", Said)}] " +
               $"invItem={(Prim.Inventory.GetInventoryItem(itemId) is null ? "MISSING" : "present")}";
    }

    /// <summary>Pump both schedulers until neither has work, or the budget runs out.</summary>
    public void Pump(int rounds = 200)
    {
        var loaderDoWork = m_loader.GetType().GetMethod("DoWork");
        var exeDoWork = m_exe.GetType().GetMethod("DoWork");

        bool loadPending = false;
        for (var i = 0; i < rounds; i++)
        {
            var l = loaderDoWork!.Invoke(m_loader, null);
            var e = exeDoWork!.Invoke(m_exe, null);
            loadPending = Pending(l);
            if (!Pending(l) && !Pending(e)) { /* keep pumping a little; events can arrive late */ }
            System.Threading.Thread.Sleep(1);
        }
        if (loadPending || Engine.ObjectPostsInFlight > 0 || AnyLoadOutstanding()) FinishLateLoads();
    }

    /// <summary>
    /// A load the loader has not finished, for an item still in the scene. The loader's WorkIsPending does not
    /// count a compile running on its compile thread (PhloxScriptLoader.HasPendingWork), so a window could end with the
    /// script still compiling, no interpreter and no late-load wait (RestoredScriptResumeTests under parallel load:
    /// "captured in RunState=(no interpreter)"). PhloxScriptLoader.IsLoading is the loader's own answer per item; an item
    /// no longer in the scene (removed while loading) never gets an outcome and is not waited for.
    /// </summary>
    private bool AnyLoadOutstanding()
    {
        var loader = (global::Phlox.ScriptEngine.PhloxScriptLoader)m_loader;
        object outcomes = loader.GetType().GetField("m_Outcomes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(loader)!;
        var latest = (Dictionary<UUID, long>)loader.GetType().GetField("m_LatestSerial", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(loader)!;
        UUID[] items;
        lock (outcomes) items = latest.Keys.ToArray();
        foreach (UUID item in items)
        {
            if (!loader.IsLoading(item)) continue;
            foreach (var sog in Scene.GetSceneObjectGroups())
                foreach (var part in sog.Parts)
                    if (part.Inventory.GetInventoryItem(item) != null) return true;
        }
        return false;
    }

    /// <summary>
    /// A script whose compile was still running, or an object event still with the thread pool, when a fixed
    /// window ended (a loaded machine, test classes in parallel) is waited for, and its first events are run, as they
    /// would have been inside the window on a quiet machine. Only ever after the window, never instead of it, and only
    /// when something was still on its way: on a quiet machine this never runs, so no window changes there.
    /// </summary>
    private void FinishLateLoads()
    {
        var loaderDoWork = m_loader.GetType().GetMethod("DoWork");
        var exeDoWork = m_exe.GetType().GetMethod("DoWork");
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < until)
        {
            var l = loaderDoWork!.Invoke(m_loader, null);
            exeDoWork!.Invoke(m_exe, null);
            if (!Pending(l) && !AnyLoadOutstanding()) break;
            System.Threading.Thread.Sleep(1);
        }
        PumpUntilIdle(TimeSpan.FromSeconds(30));
    }

    /// <summary>Probe: the interpreter's RuntimeState, by reflection.</summary>
    public object StateOf(UUID itemId)
    {
        var interp = InterpreterFor(itemId);
        return interp?.GetType().GetProperty("ScriptState")?.GetValue(interp);
    }

    private static object Member(object o, string name)
        => o.GetType().GetProperty(name)?.GetValue(o)
           ?? o.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(o);

    /// <summary>Probe: the syscall the script is parked in, or -1; int.MinValue if no state.</summary>
    public int LastSyscallIndexOf(UUID itemId)
    {
        var st = StateOf(itemId);
        return st == null ? int.MinValue : (int)(Member(st, "LastSyscallIndex") ?? int.MinValue);
    }

    /// <summary>Probe: the instruction pointer right now, or -1.</summary>
    public int IpOf(UUID itemId)
    {
        var st = StateOf(itemId);
        return st == null ? -1 : (int)(Member(st, "IP") ?? -1);
    }

    /// <summary>Probe: TopFrame locals with their CLR types, operands, calls, IP.</summary>
    public string DumpFrame(UUID itemId)
    {
        var st = StateOf(itemId);
        if (st == null) return "(no state)";
        var sb = new System.Text.StringBuilder();
        sb.Append("IP=").Append(Member(st, "IP"));
        var calls = Member(st, "Calls") as System.Collections.ICollection;
        sb.Append(" Calls=").Append(calls?.Count.ToString() ?? "null");
        var ops = Member(st, "Operands") as System.Collections.ICollection;
        sb.Append(" Operands=").Append(ops?.Count.ToString() ?? "null");
        sb.Append(" RunState=").Append(Member(st, "RunState"));
        var top = Member(st, "TopFrame");
        if (top == null) { sb.Append(" TopFrame=null"); return sb.ToString(); }
        var locals = Member(top, "Locals") as object[];
        sb.Append(" Locals=[");
        if (locals == null) sb.Append("null");
        else for (int i = 0; i < locals.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            var v = locals[i];
            sb.Append(i).Append(':').Append(v == null ? "null" : v.GetType().Name + "(" + v + ")");
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// One DoWork on each scheduler, as <see cref="PumpOnce"/>; true when either still reports work pending, so a polling
    /// loop pumps again at once instead of sleeping (a 1 ms sleep takes about 15 ms on Windows).
    /// </summary>
    public bool PumpOnceBusy()
    {
        var l = m_loader.GetType().GetMethod("DoWork")!.Invoke(m_loader, null);
        var e = m_exe.GetType().GetMethod("DoWork")!.Invoke(m_exe, null);
        return Pending(l) || Pending(e);
    }

    /// <summary>Exactly one DoWork on each scheduler - one timeslice, no more.</summary>
    public void PumpOnce()
    {
        m_loader.GetType().GetMethod("DoWork")!.Invoke(m_loader, null);
        m_exe.GetType().GetMethod("DoWork")!.Invoke(m_exe, null);
    }

    /// <summary>
    /// Put an event straight on the script's OWN queue (ScriptState.EventQueue), which is
    /// what a script that was interrupted mid-event has when it is saved - not the scheduler's
    /// pending list.
    /// </summary>
    public void QueueEventOnScriptState(UUID itemId)
    {
        var interp = InterpreterFor(itemId);
        Assert.NotNull(interp);
        var state = interp.GetType().GetProperty("ScriptState")!.GetValue(interp)!;
        var q = state.GetType().GetField("EventQueue")!.GetValue(state)!;
        var evt = new global::InWorldz.Phlox.VM.PostedEvent
        {
            EventType = global::InWorldz.Phlox.Types.SupportedEventList.Events.TOUCH_START,
            Args = new object[] { 1 },
        };
        q.GetType().GetMethod("Add")!.Invoke(q, new object[] { evt });
    }

    /// <summary>Pump for a wall-clock duration, so timer cadence can be measured.</summary>
    public void PumpFor(TimeSpan how)
    {
        var loaderDoWork = m_loader.GetType().GetMethod("DoWork");
        var exeDoWork = m_exe.GetType().GetMethod("DoWork");
        var until = DateTime.UtcNow + how;
        bool loadPending = false;
        while (DateTime.UtcNow < until)
        {
            loadPending = Pending(loaderDoWork!.Invoke(m_loader, null));
            exeDoWork!.Invoke(m_exe, null);
            System.Threading.Thread.Sleep(1);
        }
        if (loadPending || Engine.ObjectPostsInFlight > 0 || AnyLoadOutstanding()) FinishLateLoads();
    }

    /// <summary>
    /// Pump until neither scheduler reports work pending, and no object event is still with the thread pool
    /// (PhloxEngine.ObjectPostsInFlight), for <paramref name="quietRounds"/> rounds in a row, or
    /// <paramref name="limit"/> passes; false on the limit. Used AFTER a test's own settle window, never instead of it,
    /// so a "nothing arrived" window is never shorter - only a delivery that is still queued under load is waited for.
    /// </summary>
    public bool PumpUntilIdle(TimeSpan limit, int quietRounds = 5)
    {
        var loaderDoWork = m_loader.GetType().GetMethod("DoWork");
        var exeDoWork = m_exe.GetType().GetMethod("DoWork");
        var until = DateTime.UtcNow + limit;
        int quiet = 0;
        while (DateTime.UtcNow < until)
        {
            var l = loaderDoWork!.Invoke(m_loader, null);
            var e = exeDoWork!.Invoke(m_exe, null);
            bool busy = Pending(l) || Pending(e);
            quiet = busy || Engine.ObjectPostsInFlight > 0 ? 0 : quiet + 1;
            if (quiet >= quietRounds) return true;
            if (!busy) System.Threading.Thread.Sleep(1);   // Quiet rounds still sleep, so a late delivery has time to land
        }
        return false;
    }

    /// <summary>
    /// Pump both schedulers until <paramref name="done"/> holds, or <paramref name="cap"/> (30 s by default)
    /// passes; false on the cap. The shape of every "wait for the result" in this project: a fixed window followed by
    /// an assert that something DID happen fails when a loaded machine is slower than the window, so the window
    /// becomes this. A window that proves something did NOT happen stays a fixed window.
    /// </summary>
    public bool PumpUntil(Func<bool> done, TimeSpan? cap = null)
    {
        var loaderDoWork = m_loader.GetType().GetMethod("DoWork");
        var exeDoWork = m_exe.GetType().GetMethod("DoWork");
        var until = DateTime.UtcNow + (cap ?? TimeSpan.FromSeconds(30));
        while (!done())
        {
            if (DateTime.UtcNow >= until) return false;
            var l = loaderDoWork!.Invoke(m_loader, null);
            var e = exeDoWork!.Invoke(m_exe, null);
            // The scheduler threads do not sleep while they have work, and neither does this loop: a 1 ms sleep takes
            // about 15 ms on Windows, so a script needing many timeslices would otherwise wait 15 ms for each.
            if (!Pending(l) && !Pending(e)) System.Threading.Thread.Sleep(1);
        }
        return true;
    }

    private static bool Pending(object workStatus)
        => (bool)(workStatus.GetType().GetField("WorkIsPending")?.GetValue(workStatus)
                  ?? workStatus.GetType().GetProperty("WorkIsPending")?.GetValue(workStatus)
                  ?? false);

    private readonly List<string> m_said = new();
    private readonly List<(int Channel, string Message)> m_saidOn = new();
    private readonly List<(int Channel, string Message, UUID Sender)> m_clientChat = new();
    /// <summary>Chat that came in as client chat (NPCs), with the sender's key.</summary>
    public IReadOnlyList<(int Channel, string Message, UUID Sender)> ClientChat { get { lock (m_said) return m_clientChat.ToArray(); } }
    /// <summary>The config the engine was initialised with, for adding scene modules after construction.</summary>
    public IConfigSource Config { get; }

    /// <summary>Everything any script in this scene has said, in order.</summary>
    public IReadOnlyList<string> Said { get { lock (m_said) return m_said.ToArray(); } }
    /// <summary>The same chat with its channel - run-time errors must land on DEBUG_CHANNEL, not 0.</summary>
    public IReadOnlyList<(int Channel, string Message)> SaidOn { get { lock (m_said) return m_saidOn.ToArray(); } }

    /// <summary>Whether anything has been said since the last <see cref="ClearSaid"/>.</summary>
    public bool SaidAnything(UUID itemId) { lock (m_said) return m_said.Count > 0; }

    public void ClearSaid(UUID itemId) { lock (m_said) { m_said.Clear(); m_saidOn.Clear(); } }

    /// <summary>
    /// Touch the prim through the SCENE's own path - <c>EventManager.TriggerObjectGrab</c> into the
    /// engine's <c>OnObjectGrab</c> handler - rather than posting an event straight at the
    /// scheduler. This is the route that was silent in world, and the only one that exercises the
    /// part's event mask.
    /// </summary>
    public void TouchViaScene()
    {
        // A client is required: PhloxEngine.BuildTouchDetectParams reads remoteClient.AgentId
        // without a null check (PhloxEngine.cs:464), and in world there is always one.
        Scene.EventManager.TriggerObjectGrab(
            Prim.LocalId, Prim.LocalId, OpenMetaverse.Vector3.Zero, NullClient.Create(),
            new OpenSim.Framework.SurfaceTouchEventArgs());
    }

    /// <summary>Post a touch_start the way the region does when a resident touches the prim.</summary>
    public void PostTouch(UUID itemId)
    {
        // PhloxExecutionScheduler is internal, so the call is by reflection; the event type is not.
        var evt = new global::InWorldz.Phlox.VM.PostedEvent
        {
            EventType = global::InWorldz.Phlox.Types.SupportedEventList.Events.TOUCH_START,
            Args = new object[] { 1 },
        };
        m_exe.GetType().GetMethod("PostEvent")!.Invoke(m_exe, new object[] { itemId, evt });
    }

    /// <summary>The live interpreter for an item, or null if the scheduler never created one.</summary>
    public object InterpreterFor(UUID itemId)
    {
        var all = Field(m_exe, "m_AllScripts") as System.Collections.IDictionary;
        return all != null && all.Contains(itemId) ? all[itemId] : null;
    }

    /// <summary>The item's RunState, as a string, or "(no interpreter)".</summary>
    public string RunStateOf(UUID itemId)
    {
        var interp = InterpreterFor(itemId);
        if (interp is null) return "(no interpreter)";
        var state = interp.GetType().GetProperty("ScriptState")?.GetValue(interp);
        if (state is null) return "(no state)";
        var t = state.GetType();
        var v = t.GetProperty("RunState")?.GetValue(state)
                ?? t.GetField("RunState", BindingFlags.Public | BindingFlags.Instance)?.GetValue(state);
        return v?.ToString() ?? "(no RunState)";
    }

    public void Dispose()
    {
        try { StopMasterThread(); } catch { }
        try { Engine.Close(); } catch { }
        if (YEngine != null)
        {
            try { YEngine.RemoveRegion(Scene); } catch { }
            try { WorldComm.RemoveRegion(Scene); } catch { }
            try { Directory.Delete(m_yengineDir, true); } catch { }   // the harness's own temp dir
        }
    }
}

/// <summary>
/// The test scene has no chat module. IWorldComm has a wide surface and none of it matters here, so
/// it is generated rather than written - the same technique the recording ISystemAPI uses.
/// </summary>
internal class NullWorldComm : System.Reflection.DispatchProxy
{
    public static IWorldComm Create() => Create<IWorldComm, NullWorldComm>();

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        var rt = targetMethod.ReturnType;
        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
    }
}

/// <summary>A do-nothing IClientAPI, generated: the touch path needs one but reads almost nothing.</summary>
internal class NullClient : System.Reflection.DispatchProxy
{
    public static OpenSim.Framework.IClientAPI Create() => Create<OpenSim.Framework.IClientAPI, NullClient>();

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        if (targetMethod.Name == "get_Name") return "Test Toucher";
        var rt = targetMethod.ReturnType;
        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
    }
}
