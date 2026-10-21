/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using OpenSimNGC.Appearance.Baking;
using Xunit;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// What server-side baking writes to the log, and at which level. A bake runs on every login and every outfit change,
/// and each region logs its set-up once, so on a normal run none of it is Information; only a bake that failed or was
/// slow, and the one line that reports the global setting, are.
///
/// <para>The logger factory is process-wide, so these tests share one collection and run one at a time. Other
/// classes in the project may log while a capture is installed, so every assertion looks only at lines that name this
/// test's own agent or region.</para>
/// </summary>
[Collection("ssb-log-capture")]
public class BakeLogLevelTests
{
    private sealed class Capture : ILoggerFactory
    {
        private readonly ILoggerFactory m_previous = LoggerProvider.LoggerFactory;
        private readonly List<(LogLevel Level, string Message)> m_lines = new();

        public Capture() => LoggerProvider.LoggerFactory = this;
        public void Dispose() => LoggerProvider.LoggerFactory = m_previous;

        public IReadOnlyList<(LogLevel Level, string Message)> Mentioning(string text)
        {
            lock (m_lines) return m_lines.Where(l => l.Message.Contains(text)).ToList();
        }

        public ILogger CreateLogger(string categoryName) => new L(this);
        public void AddProvider(ILoggerProvider provider) { }

        private sealed class L : ILogger
        {
            private readonly Capture m_c;
            public L(Capture c) => m_c = c;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                var message = formatter(state, exception);
                lock (m_c.m_lines) m_c.m_lines.Add((logLevel, message));
            }
        }
    }

    private sealed record Rig(Scene Scene, ServerSideBakingModule Module, ScenePresence Presence);

    /// <summary>A flag-on region with one root agent. With <paramref name="skinStored"/> false the Skin wearable's asset is missing, so that channel fails.</summary>
    private static Rig Setup(bool skinStored)
    {
        var scene = new SceneHelpers().SetupScene("QuietBake" + Guid.NewGuid().ToString("N"), UUID.Random(), 1000, 1000);
        scene.RegisterModuleInterface<IAvatarService>(new FakeAvatarService());

        var sp = SceneHelpers.AddScenePresence(scene, UUID.Random());
        Wear(scene, sp, skinStored);

        var config = new IniConfigSource();
        var section = config.AddConfig(ServerSideBakingModule.ConfigSection);
        section.Set("ServerSideBaking", "true");
        section.Set("BakeSize", "512");
        var module = new ServerSideBakingModule();
        SceneHelpers.SetupSceneModules(scene, config, module);
        return new Rig(scene, module, sp);
    }

    private static void Wear(Scene scene, ScenePresence sp, bool skinStored)
    {
        var wearables = new AvatarWearable[AvatarWearable.MAX_WEARABLES];
        for (var i = 0; i < wearables.Length; i++) wearables[i] = new AvatarWearable();

        void Put(UUID id, string name, AssetType type, byte[] data)
            => scene.AssetService.Store(new AssetBase(id, name, (sbyte)type, sp.UUID.ToString()) { Data = data });

        var skinTex = UUID.Random();
        Put(skinTex, "skin", AssetType.Texture, BomAuxChannelTests.Texture(200, 170, 140));

        var shape = UUID.Random();
        Put(shape, "Shape", AssetType.Bodypart,
            Encoding.UTF8.GetBytes(BomAuxChannelTests.WearableText(WearableKind.Shape, "Shape", new Dictionary<TextureSlot, UUID>())));
        wearables[(int)WearableType.Shape].Add(UUID.Random(), shape);

        var skin = UUID.Random();
        if (skinStored)
            Put(skin, "Skin", AssetType.Bodypart, Encoding.UTF8.GetBytes(BomAuxChannelTests.WearableText(WearableKind.Skin, "Skin",
                new Dictionary<TextureSlot, UUID>
                {
                    [TextureSlot.HeadBodypaint] = skinTex, [TextureSlot.UpperBodypaint] = skinTex, [TextureSlot.LowerBodypaint] = skinTex,
                })));
        wearables[(int)WearableType.Skin].Add(UUID.Random(), skin);

        sp.Appearance.Wearables = wearables;
    }

    [Fact]
    public async Task ANormalBakeIsLoggedAtDebug()
    {
        using var capture = new Capture();
        var rig = Setup(skinStored: true);

        var outcome = await rig.Module.BakeAsync(rig.Presence, BakeReason.Login, CancellationToken.None);

        Assert.True(outcome.Count(ChannelStatus.Baked) > 0, "the outfit baked nothing, so the control is not a bake");
        var line = Assert.Single(capture.Mentioning(rig.Presence.UUID.ToString()).Where(l => l.Message.Contains("bake for")).ToList());
        Assert.Equal(LogLevel.Debug, line.Level);
    }

    [Fact]
    public async Task ABakeWithAFailedChannelIsLoggedAtInformation()
    {
        using var capture = new Capture();
        var rig = Setup(skinStored: false);

        var outcome = await rig.Module.BakeAsync(rig.Presence, BakeReason.Login, CancellationToken.None);

        Assert.True(outcome.Count(ChannelStatus.Failed) > 0, "no channel failed, so the fixture does not reproduce a failed bake");
        var line = Assert.Single(capture.Mentioning(rig.Presence.UUID.ToString()).Where(l => l.Message.Contains("bake for")).ToList());
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("Failed", line.Message);
    }

    private static BakeOutcome Outcome(long elapsedMs, ChannelStatus status, bool indexWritten)
        => new(UUID.Random(), BakeReason.Login,
            new[]
            {
                new ChannelOutcome(BakeChannel.Head, status, UUID.Random(), "", status == ChannelStatus.Failed ? "asset missing" : "",
                    new FidelityReport(Array.Empty<string>(), Array.Empty<UUID>(), Array.Empty<string>(), Array.Empty<string>())),
            },
            elapsedMs)
        { IndexWritten = indexWritten };

    [Theory]
    [InlineData(45, ChannelStatus.Reused, true, LogLevel.Debug)]
    [InlineData(1800, ChannelStatus.Baked, true, LogLevel.Debug)]
    [InlineData(ServerSideBakingModule.SlowBakeMs, ChannelStatus.Baked, true, LogLevel.Debug)]
    [InlineData(ServerSideBakingModule.SlowBakeMs + 1, ChannelStatus.Reused, true, LogLevel.Information)]
    [InlineData(45, ChannelStatus.Failed, false, LogLevel.Information)]
    [InlineData(45, ChannelStatus.Baked, false, LogLevel.Information)]
    [InlineData(45, ChannelStatus.Skipped, false, LogLevel.Debug)]
    public void TheLevelFollowsTheOutcome(long elapsedMs, ChannelStatus status, bool indexWritten, LogLevel expected)
        => Assert.Equal(expected, ServerSideBakingModule.LogLevelFor(Outcome(elapsedMs, status, indexWritten)));

    [Fact]
    public void ARegionsStartUpLinesAreDebugAndTheGlobalLineStaysInformation()
    {
        using var capture = new Capture();
        var rig = Setup(skinStored: true);

        var regionLines = capture.Mentioning(rig.Scene.Name).Where(l => l.Message.Contains("[SSB]")).ToList();
        Assert.Equal(2, regionLines.Count);   // "server-side baking ON (global)" and "has server-side baking ON: ..."
        Assert.All(regionLines, l => Assert.Equal(LogLevel.Debug, l.Level));
        Assert.Contains(capture.Mentioning("ServerSideBaking=True"), l => l.Level == LogLevel.Information);
    }

    [Fact]
    public void AFlagOffRegionsStartUpLinesAreDebug()
    {
        using var capture = new Capture();
        var scene = new SceneHelpers().SetupScene("QuietBakeOff" + Guid.NewGuid().ToString("N"), UUID.Random(), 1000, 1000);
        var config = new IniConfigSource();
        config.AddConfig(ServerSideBakingModule.ConfigSection).Set("ServerSideBaking", "false");
        SceneHelpers.SetupSceneModules(scene, config, new ServerSideBakingModule());

        var regionLines = capture.Mentioning(scene.Name).Where(l => l.Message.Contains("[SSB]")).ToList();
        Assert.Equal(2, regionLines.Count);   // the flag line and "has server-side baking off"
        Assert.All(regionLines, l => Assert.Equal(LogLevel.Debug, l.Level));
    }
}
