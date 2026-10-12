/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Microsoft.Extensions.Logging;
using OpenSim.Framework;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// Closing a region kicks each root avatar, and the kick waits long enough for the viewer to log out
    /// and the presence to be removed. The close must not then use the client of a presence that is gone.
    ///
    /// <para>The process's logger factory is swapped for a capture while the test runs, hence its own
    /// collection; the check only looks at the scene graph's error line.</para>
    /// </summary>
    [Collection("process-logger")]
    public class SceneCloseWithAvatarTests : OpenSimTestCase
    {
        /// <summary>A client whose viewer logs out while the kick is waiting.</summary>
        private sealed class LogsOutDuringKick : TestClient
        {
            private readonly Scene m_scene;
            public bool Kicked;

            public LogsOutDuringKick(AgentCircuitData agentData, Scene scene) : base(agentData, scene)
            {
                m_scene = scene;
            }

            public override void Kick(string message)
            {
                Kicked = true;
                m_scene.RemoveClient(AgentId, false);
            }
        }

        private sealed class Capture : ILoggerFactory, ILoggerProvider
        {
            public readonly List<string> Lines = new();
            public ILogger CreateLogger(string category) => new L(this);
            public void AddProvider(ILoggerProvider provider) { }
            public void Dispose() { }

            private sealed class L : ILogger
            {
                private readonly Capture m_c;
                public L(Capture c) => m_c = c;
                public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
                public bool IsEnabled(LogLevel logLevel) => true;
                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                    Func<TState, Exception, string> formatter)
                {
                    lock (m_c.Lines) m_c.Lines.Add(formatter(state, exception));
                }
            }
        }

        [Fact]
        public void ClosingARegionWhileTheAvatarLogsOutDuringTheKickLogsNoSceneGraphError()
        {
            TestHelpers.InMethod();

            var capture = new Capture();
            ILoggerFactory previous = LoggerProvider.LoggerFactory;
            LoggerProvider.LoggerFactory = capture;
            try
            {
                SceneHelpers sh = new SceneHelpers();
                Scene scene = sh.SetupScene();
                AgentCircuitData agent = SceneHelpers.GenerateAgentData(UUID.Random());
                var client = new LogsOutDuringKick(agent, scene);
                SceneHelpers.AddScenePresence(scene, client, agent);

                sh.SceneManager.Close();

                client.Kicked.Should().BeTrue();
                scene.ShuttingDown.Should().BeTrue();
                lock (capture.Lines)
                    capture.Lines.Where(l => l.Contains("[SCENEGRAPH]: Error in")).Should().BeEmpty();
            }
            finally
            {
                LoggerProvider.LoggerFactory = previous;
            }
        }
    }
}
