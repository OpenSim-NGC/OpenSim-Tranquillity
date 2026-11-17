/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using InWorldz.Phlox.Types;
using InWorldz.Phlox.VM;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The listen manager reaches the engine that owns it through IListenEventSink: where a heard message goes, the engine's
/// settings, and the calls about a script. These tests build the manager on a stand-in sink, as another script engine would,
/// and check that the manager uses nothing else of the engine; Phlox passes its scheduler, which is such a sink.
/// </summary>
public class ListenManagerSinkTests
{
    private sealed class StandInSink : IListenEventSink
    {
        public readonly List<(UUID Item, PostedEvent Event)> Posted = new();
        public int Wakes;
        public IConfig Config;
        public IConfig EngineConfig => Config;
        public void PostEvent(UUID itemId, PostedEvent evt) { lock (Posted) Posted.Add((itemId, evt)); }
        public void ScriptChangedOutsideARun(UUID itemId) { }
        public void ShoutError(UUID itemId, string message) { }
        public void Wake() => Interlocked.Increment(ref Wakes);
    }

    private static PhloxListenManager ManagerOn(IListenEventSink sink) =>
        new(sink, null, PhloxListenManager.DefaultWhisperDistance, PhloxListenManager.DefaultSayDistance, PhloxListenManager.DefaultShoutDistance,
            PhloxListenManager.DefaultMaxListensPerScript, PhloxListenManager.DefaultMaxListensPerRegion);

    private static readonly UUID Item = UUID.Random();
    private static readonly UUID Host = UUID.Random();
    private static readonly UUID Speaker = UUID.Random();

    [Fact]
    public void AMatchingMessageIsPostedToTheSinkAsAListenEvent()
    {
        var sink = new StandInSink();
        PhloxListenManager manager = ManagerOn(sink);
        manager.Add(1, Item, Host, 7, "", UUID.Zero, "");

        manager.DeliverChat(ChatTypeEnum.Region, 7, "Speaker One", Speaker, "hello", Vector3.Zero, UUID.Zero);

        Assert.Single(sink.Posted);
        Assert.Equal(Item, sink.Posted[0].Item);
        Assert.Equal(SupportedEventList.Events.LISTEN, sink.Posted[0].Event.EventType);
        Assert.Equal(new object[] { 7, "Speaker One", Speaker.ToString(), "hello" }, sink.Posted[0].Event.Args);
    }

    [Fact]
    public void ADifferentChannelOrAFilteredOutNameOrMessageIsNotPosted()
    {
        var sink = new StandInSink();
        PhloxListenManager manager = ManagerOn(sink);
        manager.Add(1, Item, Host, 7, "Speaker One", UUID.Zero, "hello");
        manager.DeliverChat(ChatTypeEnum.Region, 7, "Speaker One", Speaker, "hello", Vector3.Zero, UUID.Zero);
        Assert.Single(sink.Posted);

        manager.DeliverChat(ChatTypeEnum.Region, 8, "Speaker One", Speaker, "hello", Vector3.Zero, UUID.Zero);
        manager.DeliverChat(ChatTypeEnum.Region, 7, "Speaker Two", Speaker, "hello", Vector3.Zero, UUID.Zero);
        manager.DeliverChat(ChatTypeEnum.Region, 7, "Speaker One", Speaker, "goodbye", Vector3.Zero, UUID.Zero);

        Assert.Single(sink.Posted);
    }

    [Fact]
    public void TheRatePerSecondComesFromTheSinksConfig()
    {
        var source = new IniConfigSource();
        IConfig config = source.AddConfig("Engine");
        config.Set("MaxListenEventsPerSecond", 3);
        var sink = new StandInSink { Config = config };

        PhloxListenManager manager = ManagerOn(sink);

        Assert.Equal(3, manager.MaxListenEventsPerSecond);
    }

    [Fact]
    public void AnEventRefusedOverTheRateWakesTheSinkAndTheRestAreStillPosted()
    {
        var source = new IniConfigSource();
        IConfig config = source.AddConfig("Engine");
        config.Set("MaxListenEventsPerSecond", 1);
        var sink = new StandInSink { Config = config };
        PhloxListenManager manager = ManagerOn(sink);
        manager.CurrentSecond = () => 1000;
        manager.Add(1, Item, Host, 7, "", UUID.Zero, "");

        manager.DeliverChat(ChatTypeEnum.Region, 7, "Speaker One", Speaker, "first", Vector3.Zero, UUID.Zero);
        manager.DeliverChat(ChatTypeEnum.Region, 7, "Speaker One", Speaker, "second", Vector3.Zero, UUID.Zero);

        Assert.Single(sink.Posted);
        Assert.Equal(1, sink.Wakes);
    }

    [Fact]
    public void PhloxsSchedulerIsTheSinkPhloxPasses()
    {
        Assert.True(typeof(IListenEventSink).IsAssignableFrom(typeof(PhloxExecutionScheduler)));
    }
}
