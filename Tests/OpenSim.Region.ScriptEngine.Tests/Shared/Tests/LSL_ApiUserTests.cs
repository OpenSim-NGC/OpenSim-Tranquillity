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
using System.Threading;
using Nini.Config;
using Xunit;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Framework.UserManagement;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Region.ScriptEngine.Shared.Tests
{
    public class LSL_ApiUserTests : OpenSimTestCase
    {
        private Scene m_scene;
        private MockScriptEngine m_engine;

        public override void SetUp()
        {
            base.SetUp();

            m_engine = new MockScriptEngine();

            m_scene = new SceneHelpers().SetupScene();
            // llRequestAgentData looks users up through IUserManagement, and answers nothing without it.
            IniConfigSource config = new IniConfigSource();
            config.AddConfig("Modules");
            SceneHelpers.SetupSceneModules(m_scene, config, m_engine, new UserManagementModule());
        }

        // llRequestAgentData(DATA_ONLINE) answers at once for an avatar in the region (the fast local path,
        // RequestWithImediatePost). For anyone else it asks the presence service on the dataserver's work
        // pool and posts the dataserver event when the answer comes, caching it for
        // LlRequestAgentDataCacheTimeoutMs; so an offline answer arrives after the call returns.
        [Fact]
        public void TestLlRequestAgentDataOnline()
        {
            TestHelpers.InMethod();

            // Not ParseTail(0x1): other tests in the assembly log that user in, and the presence service the test
            // scenes share would still report them online.
            UUID userId = UUID.Random();

            UserAccount ua1 = UserAccountHelpers.CreateUserWithInventory(m_scene, userId);

            SceneObjectPart part = SceneHelpers.AddSceneObject(m_scene).RootPart;
            TaskInventoryItem scriptItem = TaskInventoryHelpers.AddScript(m_scene.AssetService, part);

            LSL_Api apiGrp1 = new LSL_Api();
            apiGrp1.Initialize(m_engine, part, scriptItem);
            apiGrp1.LlRequestAgentDataCacheTimeoutMs = 20;

            // Not in the region and not online: the presence service's answer, posted from the work pool.
            apiGrp1.llRequestAgentData(userId.ToString(), ScriptBaseClass.DATA_ONLINE);
            Assert.Equal(0, WaitForOnlineReply(scriptItem.ItemID));

            // In the region: answered at once.
            ScenePresence sp = SceneHelpers.AddScenePresence(m_scene, ua1);
            apiGrp1.llRequestAgentData(userId.ToString(), ScriptBaseClass.DATA_ONLINE);
            Assert.Equal(1, WaitForOnlineReply(scriptItem.ItemID));

            // Gone again, once the cached answer has expired: offline.
            m_scene.CloseAgent(userId, false);
            Thread.Sleep(apiGrp1.LlRequestAgentDataCacheTimeoutMs + 50);
            apiGrp1.llRequestAgentData(userId.ToString(), ScriptBaseClass.DATA_ONLINE);
            Assert.Equal(0, WaitForOnlineReply(scriptItem.ItemID));
        }

        /// <summary>
        /// Waits up to 10 s for the one dataserver event a DATA_ONLINE request posts, returns its answer
        /// (0 or 1), and clears the posted events.
        /// </summary>
        private int WaitForOnlineReply(UUID itemId)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                lock (m_engine.PostedEvents)
                {
                    if (m_engine.PostedEvents.TryGetValue(itemId, out List<EventParams> events) && events.Count > 0)
                    {
                        Assert.Single(events);
                        Assert.Equal("dataserver", events[0].EventName);
                        int answer = int.Parse(events[0].Params[1].ToString());
                        m_engine.PostedEvents.Clear();
                        return answer;
                    }
                }
                Assert.True(DateTime.UtcNow < deadline, "no dataserver event within 10 s");
                Thread.Sleep(10);
            }
        }
    }
}
