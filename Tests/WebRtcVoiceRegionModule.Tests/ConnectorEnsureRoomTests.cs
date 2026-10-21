/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The connector module asks the voice service to make a record's room exist, both when it registers the record and
 * at every capability fetch (VoiceConnectorModule.ResolveAndEnsureRoom). When the service cannot make the room, the
 * module says so in one warning and still answers with the room number the provisioning path computes, so the
 * capability names the room an avatar on that parcel would be in.
 */

using System.Reflection;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using osWebRtcVoice;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    [NonParallelizable]   // swaps the process-wide logger factory
    public class ConnectorEnsureRoomTests
    {
        private sealed class Capture : ILoggerProvider, ILogger
        {
            public readonly List<(LogLevel Level, string Text)> Lines = new();
            public ILogger CreateLogger(string categoryName) => this;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter)
            {
                lock (Lines)
                    Lines.Add((logLevel, formatter(state, exception)));
            }
            public void Dispose() { }
        }

        private sealed class EnsureOnlyService : IWebRtcVoiceService
        {
            public int? Answer;
            public readonly List<(UUID Scene, int Parcel)> Asked = new();
            public int? EnsureSpatialRoom(UUID pSceneID, int pParcelLocalID)
            {
                Asked.Add((pSceneID, pParcelLocalID));
                return Answer;
            }
            public OSDMap ProvisionVoiceAccountRequest(OSDMap pRequest, UUID pUserID, UUID pScene) => new OSDMap();
            public OSDMap VoiceSignalingRequest(OSDMap pRequest, UUID pUserID, UUID pScene) => new OSDMap();
            public OSDMap ProvisionVoiceAccountRequest(IVoiceViewerSession pVSession, OSDMap pRequest, UUID pUserID, UUID pScene) => new OSDMap();
            public OSDMap VoiceSignalingRequest(IVoiceViewerSession pVSession, OSDMap pRequest, UUID pUserID, UUID pScene) => new OSDMap();
            public IVoiceViewerSession CreateViewerSession(OSDMap pRequest, UUID pUserID, UUID pScene) => null;
        }

        private Capture m_log;
        private ILoggerFactory m_previous;

        [SetUp]
        public void SetUp()
        {
            m_log = new Capture();
            m_previous = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = LoggerFactory.Create(b => b.AddProvider(m_log).SetMinimumLevel(LogLevel.Trace));
        }

        [TearDown]
        public void TearDown()
        {
            ILoggerFactory ours = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = m_previous;
            ours.Dispose();
            m_log.Dispose();
        }

        private static void Set(object target, string field, object value)
            => typeof(VoiceConnectorModule).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

        private static int ResolveAndEnsure(VoiceConnectorModule module, VoiceConnectorRecord record)
        {
            MethodInfo method = typeof(VoiceConnectorModule).GetMethod("ResolveAndEnsureRoom", BindingFlags.Instance | BindingFlags.NonPublic);
            return ((int?)method.Invoke(module, new object[] { record })).Value;
        }

        private static VoiceConnectorRecord Record()
            => new VoiceConnectorRecord("Recorder", true, "Recorder", "NPC", new Vector3(128, 128, 25),
                VoiceConnectorScope.Estate, false, "Operator", null, null);

        /// <summary>The room an avatar standing where the record stands is provisioned into.</summary>
        private static int ParcelRoom(Scene scene)
            => ConnectorRoomResolver.RoomFor(scene.RegionInfo.RegionID,
                scene.LandChannel.GetLandObject(Record().Position.X, Record().Position.Y).LandData);

        private (VoiceConnectorModule Module, Scene Scene, EnsureOnlyService Service) Module(int? answer)
        {
            Scene scene = new SceneHelpers().SetupScene();
            var service = new EnsureOnlyService { Answer = answer };
            var module = new VoiceConnectorModule();
            Set(module, "m_scene", scene);
            Set(module, "m_voiceService", service);
            return (module, scene, service);
        }

        [Test]
        public void WhenTheServiceCannotMakeTheRoom_OneWarningIsWritten_AndTheComputedRoomIsStillReturned()
        {
            (VoiceConnectorModule module, Scene scene, EnsureOnlyService service) = Module(null);
            int expected = ParcelRoom(scene);

            int room = ResolveAndEnsure(module, Record());

            Assert.That(room, Is.EqualTo(expected));
            Assert.That(service.Asked, Has.Count.EqualTo(1));
            lock (m_log.Lines)
            {
                var warned = m_log.Lines.Where(l => l.Level == LogLevel.Warning && l.Text.Contains("could not ensure mixer room")).ToList();
                Assert.That(warned, Has.Count.EqualTo(1));
                Assert.That(warned[0].Text, Does.Contain(expected.ToString()));
            }
        }

        [Test]
        public void WhenTheServiceMakesTheRoom_NothingIsWarned_AndTheServiceWasAskedForTheParcelsRoom()
        {
            (VoiceConnectorModule module, Scene scene, EnsureOnlyService service) = Module(123456);

            int room = ResolveAndEnsure(module, Record());

            Assert.That(room, Is.EqualTo(ParcelRoom(scene)));
            LandData land = scene.LandChannel.GetLandObject(Record().Position.X, Record().Position.Y).LandData;
            int parcel = ConnectorRoomResolver.IsEstateChannel(land) ? JanusAudioBridge.REGION_ROOM_ID : land.LocalID;
            Assert.That(service.Asked, Is.EqualTo(new[] { (scene.RegionInfo.RegionID, parcel) }));
            lock (m_log.Lines)
                Assert.That(m_log.Lines.Where(l => l.Level >= LogLevel.Warning), Is.Empty);
        }
    }
}
