/*
 * Phlox Script Engine tests
 * Copyright (c) Legion Builds
 */

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A sound the prim does not have. SL (llPlaySound, llLoopSound): "If sound is missing from the prim's inventory and it
/// is not a UUID or it is not a sound then an error is shouted on DEBUG_CHANNEL." Halcyon also stops the prim's current
/// sound (UpdateSound with a zero key). Both, for every call that plays a sound on the prim; the trigger calls, which
/// play no attached sound, give only the error.
/// </summary>
// No test reaches a network service: the sound module is an in-memory recorder.
// Test grouping: no process-wide state, so the class runs in parallel.
public class SoundMissingTests
{
    private const int DEBUG_CHANNEL = 0x7FFFFFFF;

    internal class RecordingSounds : DispatchProxy
    {
        public readonly ConcurrentQueue<string> Calls = new();

        public static ISoundModule Create(out RecordingSounds rec)
        {
            var p = Create<ISoundModule, RecordingSounds>();
            rec = (RecordingSounds)(object)p;
            return p;
        }

        protected override object Invoke(MethodInfo m, object[] a)
        {
            Calls.Enqueue(m.Name);
            var rt = m.ReturnType;
            return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
        }
    }

    private static (SchedulerHarness H, LSLSystemAPI Api, RecordingSounds Sounds) Rig()
    {
        var h = new SchedulerHarness();
        // The test scene has the region's own sound module; the recorder takes its place.
        var regionSounds = h.Scene.RequestModuleInterface<ISoundModule>();
        if (regionSounds != null) h.Scene.UnregisterModuleInterface(regionSounds);
        h.Scene.RegisterModuleInterface<ISoundModule>(RecordingSounds.Create(out var rec));
        AddItem(h, "bell", AssetType.Sound);
        AddItem(h, "card", AssetType.Notecard);
        // A loaded script's own API: the sound calls reach the sound module through the engine's scene.
        UUID item = h.RezScript("default { state_entry() { } }");
        Assert.True(h.PumpUntil(() => h.RunStateOf(item) == "Waiting"), "the script never loaded: " + h.RunStateOf(item));
        object exe = Field(h.Engine, "m_ExeScheduler");
        return (h, ((System.Collections.Generic.Dictionary<UUID, LSLSystemAPI>)Field(exe, "m_Apis"))[item], rec);
    }

    private static object Field(object o, string name)
    {
        for (Type t = o.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f.GetValue(o);
        }
        throw new MissingFieldException(o.GetType().Name, name);
    }

    private static void AddItem(SchedulerHarness h, string name, AssetType type)
    {
        var item = new TaskInventoryItem { ItemID = UUID.Random(), AssetID = UUID.Random(), Name = name, Type = (int)type,
            InvType = (int)InventoryType.Sound, ParentPartID = h.Prim.UUID };
        lock (h.Prim.TaskInventory) h.Prim.TaskInventory.Add(item.ItemID, item);
    }

    private static bool Shouted(SchedulerHarness h, string name)
        => h.SaidOn.Any(s => s.Channel == DEBUG_CHANNEL && s.Message.EndsWith($"Could not find sound '{name}'"));

    public static TheoryData<string> AttachedCalls => new() { "llPlaySound", "llLoopSound", "llLoopSoundMaster",
        "llLoopSoundSlave", "llPlaySoundSlave", "llLinkPlaySound" };

    private static void Play(LSLSystemAPI api, string fn, string sound)
    {
        switch (fn)
        {
            case "llPlaySound": api.llPlaySound(sound, 1); break;
            case "llLoopSound": api.llLoopSound(sound, 1); break;
            case "llLoopSoundMaster": api.llLoopSoundMaster(sound, 1); break;
            case "llLoopSoundSlave": api.llLoopSoundSlave(sound, 1); break;
            case "llPlaySoundSlave": api.llPlaySoundSlave(sound, 1); break;
            case "llLinkPlaySound": api.llLinkPlaySound(-4, sound, 1); break;   // LINK_THIS
            case "llTriggerSound": api.llTriggerSound(sound, 1); break;
            case "llTriggerSoundLimited": api.llTriggerSoundLimited(sound, 1, new Vector3(1, 1, 1), Vector3.Zero); break;
            default: throw new ArgumentException(fn);
        }
    }

    [Theory]
    [MemberData(nameof(AttachedCalls))]
    public void AMissingSoundStopsThePrimsSoundAndShouts(string fn)
    {
        var (h, api, sounds) = Rig();
        using (h)
        {
            Play(api, fn, "nothing here");
            Assert.Contains("StopSound", sounds.Calls);
            Assert.DoesNotContain(sounds.Calls, c => c is "SendSound" or "LoopSound");
            Assert.True(Shouted(h, "nothing here"), fn + ": no error on DEBUG_CHANNEL");
        }
    }

    [Theory]
    [MemberData(nameof(AttachedCalls))]
    public void AnItemThatIsNotASoundIsMissing(string fn)
    {
        var (h, api, sounds) = Rig();
        using (h)
        {
            Play(api, fn, "card");
            Assert.Contains("StopSound", sounds.Calls);
            Assert.True(Shouted(h, "card"), fn + ": no error on DEBUG_CHANNEL");
        }
    }

    [Theory]
    [MemberData(nameof(AttachedCalls))]
    public void AFoundSoundPlaysWithoutAnError(string fn)
    {
        var (h, api, sounds) = Rig();
        using (h)
        {
            Play(api, fn, "bell");
            Assert.Contains(sounds.Calls, c => c is "SendSound" or "LoopSound");
            Assert.DoesNotContain("StopSound", sounds.Calls);
            Assert.False(Shouted(h, "bell"));
        }
    }

    [Theory]
    [InlineData("llTriggerSound")]
    [InlineData("llTriggerSoundLimited")]
    public void ATriggeredMissingSoundShoutsWithoutStopping(string fn)
    {
        var (h, api, sounds) = Rig();
        using (h)
        {
            Play(api, fn, "nothing here");
            Assert.Empty(sounds.Calls);
            Assert.True(Shouted(h, "nothing here"), fn + ": no error on DEBUG_CHANNEL");
        }
    }

    [Fact]
    public void ASoundGivenByKeyPlays()
    {
        var (h, api, sounds) = Rig();
        using (h)
        {
            api.llPlaySound(UUID.Random().ToString(), 1);
            Assert.Contains("SendSound", sounds.Calls);
            Assert.DoesNotContain(h.SaidOn, s => s.Channel == DEBUG_CHANNEL);
        }
    }
}
