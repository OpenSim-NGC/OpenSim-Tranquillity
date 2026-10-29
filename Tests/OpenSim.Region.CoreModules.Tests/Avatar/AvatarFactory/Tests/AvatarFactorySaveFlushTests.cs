/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Avatar.AvatarFactory;

/// <summary>
/// A pending appearance change must survive the agent leaving.
///
/// <para><c>QueueAppearanceSave</c> defers the write by <c>m_savetime</c> seconds and <c>SaveAppearance</c>
/// resolves the <c>ScenePresence</c> only when the timer fires; if the agent left in between the presence was gone
/// and the write was skipped silently. A detach followed by a logout inside that window left the stored appearance
/// still wearing the garment, and the viewer put it back on the next login. Observed live on 2026-09-04: a detach
/// at 14:09:35 queued a save for ~14:09:40.4 and the avatar left at ~14:09:40.0.</para>
///
/// <para><b>Why the existing suite could not catch this.</b> Every appearance test drives a presence that stays in
/// the scene for the whole test and asserts on <c>sp.Appearance</c> — the in-memory object, which was always
/// correct. Nothing asserted on what reached <c>IAvatarService</c>, and nothing closed a presence with a save
/// still pending. The bug lives entirely in the gap between those two, and it is a *timing* gap, so no test that
/// never ends a session could see it.</para>
/// </summary>
public class AvatarFactorySaveFlushTests : OpenSimTestCase
{
    /// <summary>Counts what actually reached the avatar service, which is the thing the bug lost.</summary>
    private sealed class CountingAvatarService : IAvatarService, ISharedRegionModule
    {
        public int SetAppearanceCalls;
        public readonly List<UUID> SavedFor = new();

        public bool SetAppearance(UUID userID, AvatarAppearance appearance)
        {
            SetAppearanceCalls++;
            SavedFor.Add(userID);
            return true;
        }

        public AvatarAppearance GetAppearance(UUID userID) => null!;
        public AvatarData GetAvatar(UUID userID) => null!;
        public bool SetAvatar(UUID userID, AvatarData avatar) => true;
        public bool ResetAvatar(UUID userID) => true;
        public bool SetItems(UUID userID, string[] names, string[] values) => true;
        public bool RemoveItems(UUID userID, string[] names) => true;

        public string Name => "CountingAvatarService";
        public Type ReplaceableInterface => null!;
        public void Initialise(IConfigSource source) { }
        public void PostInitialise() { }
        public void Close() { }
        public void AddRegion(Scene scene) => scene.RegisterModuleInterface<IAvatarService>(this);
        public void RemoveRegion(Scene scene) { }
        public void RegionLoaded(Scene scene) { }
    }

    /// <summary>Captures what was logged, so "this must never fail silently again" is an assertion.</summary>
    private sealed class CapturedLog : IDisposable, ILoggerFactory
    {
        private readonly ILoggerFactory m_previous;
        private readonly List<(LogLevel Level, string Message)> m_entries = new();

        public CapturedLog()
        {
            m_previous = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = this;
        }

        public List<string> Warnings
        {
            get
            {
                List<string> found = new();
                lock (m_entries)
                    foreach ((LogLevel level, string message) in m_entries)
                        if (level == LogLevel.Warning) found.Add(message);
                return found;
            }
        }

        public void Dispose() => LoggerProvider.LoggerFactory = m_previous;
        ILogger ILoggerFactory.CreateLogger(string categoryName) => new Recorder(this);
        void ILoggerFactory.AddProvider(ILoggerProvider provider) { }

        private void Record(LogLevel level, string message)
        {
            lock (m_entries) m_entries.Add((level, message));
        }

        private sealed class Recorder : ILogger
        {
            private readonly CapturedLog m_owner;
            public Recorder(CapturedLog owner) { m_owner = owner; }
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Scope();
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => m_owner.Record(logLevel, formatter(state, exception));
            private sealed class Scope : IDisposable { public void Dispose() { } }
        }
    }

    private static (AvatarFactoryModule afm, TestScene scene, CountingAvatarService avatars, ScenePresence sp) Setup()
    {
        UUID userId = TestHelpers.ParseTail(0x1);

        CountingAvatarService avatars = new CountingAvatarService();
        AvatarFactoryModule afm = new AvatarFactoryModule();
        TestScene scene = new SceneHelpers().SetupScene();
        SceneHelpers.SetupSceneModules(scene, avatars, afm);
        ScenePresence sp = SceneHelpers.AddScenePresence(scene, userId);

        return (afm, scene, avatars, sp);
    }

    // ------------------------------------------------------------------ the fix

    [Fact]
    public void A_queued_save_is_written_when_the_presence_closes()
    {
        (AvatarFactoryModule afm, TestScene scene, CountingAvatarService avatars, ScenePresence sp) = Setup();

        afm.QueueAppearanceSave(sp.UUID);
        Assert.Equal(0, avatars.SetAppearanceCalls);   // still only queued: the five-second window

        scene.EventManager.TriggerOnRemovePresence(sp.UUID);

        Assert.Equal(1, avatars.SetAppearanceCalls);
        Assert.Equal(sp.UUID, Assert.Single(avatars.SavedFor));
    }

    /// <summary>
    /// The load-bearing one: the real close path must reach the flush, and must do it while the presence is still
    /// resolvable. <c>Scene.RemoveClient</c> raises <c>OnRemovePresence</c> at <c>Scene.cs:3866</c> and only
    /// removes the presence in the <c>finally</c> block at <c>:3898</c>.
    /// </summary>
    [Fact]
    public void A_queued_save_survives_a_real_client_close()
    {
        (AvatarFactoryModule afm, TestScene scene, CountingAvatarService avatars, ScenePresence sp) = Setup();
        UUID userId = sp.UUID;

        afm.QueueAppearanceSave(userId);
        scene.RemoveClient(userId, false);

        Assert.Equal(1, avatars.SetAppearanceCalls);
        Assert.Equal(userId, Assert.Single(avatars.SavedFor));
        Assert.Null(scene.GetScenePresence(userId));   // and the presence really did go away
    }

    [Fact]
    public void The_queued_save_is_written_exactly_once()
    {
        (AvatarFactoryModule afm, TestScene scene, CountingAvatarService avatars, ScenePresence sp) = Setup();

        afm.QueueAppearanceSave(sp.UUID);
        scene.EventManager.TriggerOnRemovePresence(sp.UUID);
        scene.EventManager.TriggerOnRemovePresence(sp.UUID);   // a second close must not write again

        Assert.Equal(1, avatars.SetAppearanceCalls);
    }

    /// <summary>
    /// The cost guarantee: with the queue empty the flush writes nothing.
    ///
    /// <para>Entering a region queues a save of its own — <c>SceneHelpers.AddScenePresence</c> ends up in
    /// <c>SetAppearance</c>, which queues one. In a running region the timer drains that within
    /// <c>m_savetime</c> seconds and the queue is empty long before logout; in this harness the timer never runs,
    /// so the entry is drained here explicitly. What is asserted after that is the case the guarantee is about: a
    /// close with nothing pending.</para>
    /// </summary>
    [Fact]
    public void A_close_with_nothing_queued_writes_nothing()
    {
        (AvatarFactoryModule _, TestScene scene, CountingAvatarService avatars, ScenePresence sp) = Setup();

        scene.EventManager.TriggerOnRemovePresence(sp.UUID);   // drains the entry that entering the region made
        int afterDrain = avatars.SetAppearanceCalls;

        scene.EventManager.TriggerOnRemovePresence(sp.UUID);   // nothing pending now

        Assert.Equal(afterDrain, avatars.SetAppearanceCalls);
    }

    [Fact]
    public void An_unchanged_session_costs_no_write_through_a_real_close()
    {
        (AvatarFactoryModule _, TestScene scene, CountingAvatarService avatars, ScenePresence sp) = Setup();
        UUID userId = sp.UUID;

        scene.EventManager.TriggerOnRemovePresence(userId);    // drain the region-entry save
        int afterDrain = avatars.SetAppearanceCalls;

        scene.RemoveClient(userId, false);

        Assert.Equal(afterDrain, avatars.SetAppearanceCalls);
    }

    // ------------------------------------------------------------------ the drop must never be silent again

    [Fact]
    public void A_drop_warns_rather_than_losing_the_change_silently()
    {
        (AvatarFactoryModule afm, TestScene scene, CountingAvatarService avatars, ScenePresence _) = Setup();
        UUID ghost = TestHelpers.ParseTail(0x99);   // queued, but no presence to read the appearance from

        afm.QueueAppearanceSave(ghost);

        using CapturedLog log = new CapturedLog();
        scene.EventManager.TriggerOnRemovePresence(ghost);

        Assert.Equal(0, avatars.SetAppearanceCalls);
        string warning = Assert.Single(log.Warnings.FindAll(w => w.Contains("could not be flushed")));
        Assert.Contains(ghost.ToString(), warning);
        Assert.Contains("lost", warning);
    }
}
