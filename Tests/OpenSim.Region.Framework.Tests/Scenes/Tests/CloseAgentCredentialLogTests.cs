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
    /// A network close request carries the agent's session id as its authorization token. When it is wrong, the
    /// warning names the agent and the region but must not repeat the token the caller presented.
    ///
    /// <para><see cref="LogCapture"/> swaps the process-wide logger factory, so this joins the collection the other
    /// logger-swapping scene tests use.</para>
    /// </summary>
    [Collection("process-logger")]
    public class CloseAgentCredentialLogTests : OpenSimTestCase
    {
        [Fact]
        public void CloseAgentWithWrongTokenDoesNotLogTheToken()
        {
            TestHelpers.InMethod();

            string presented = "presented-close-token-" + Guid.NewGuid().ToString("N");

            SceneHelpers sh = new SceneHelpers();
            Scene scene = sh.SetupScene();
            AgentCircuitData agent = SceneHelpers.GenerateAgentData(UUID.Random());
            SceneHelpers.AddScenePresence(scene, agent);

            using LogCapture log = new();
            Assert.False(scene.CloseAgent(agent.AgentID, false, presented));

            Assert.False(log.Contains(presented), "presented token reached the log");
            Assert.False(log.Contains(agent.SessionID.ToString()), "stored session id reached the log");
            Assert.Equal(LogLevel.Warning, log.LevelOf("invalid authorization token"));
            Assert.True(log.Contains(agent.AgentID.ToString()));
        }
    }
}
