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

using System.Collections;
using System.Net;
using System.Text;

using OpenMetaverse;
using OpenMetaverse.StructuredData;

using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.CoreModules.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.OptionalModules.World.NPC;
using OpenSim.Tests.Common;
using Nini.Config;
using Xunit;
using OpenSim.Region.ClientStack.LindenCaps;

namespace OpenSim.Region.ClientStack.Linden.Tests
{
    public class EventQueueTests : OpenSimTestCase
    {
        private TestScene m_scene;
        private EventQueueGetModule m_eqgMod;
        private BaseHttpServer m_server;
        private NPCModule m_npcMod;

        public override void SetUp()
        {
            base.SetUp();

            uint port = 9999;

            // This is an unfortunate bit of clean up we have to do because MainServer manages things through static
            // variables and the VM is not restarted between tests.
            // MainServer is instance-based now; OpenSimTestCase removed the previous default server, so this one becomes DefaultServer.
            MainServer.Instance.RemoveHttpServer(port);

            m_server = new BaseHttpServer(port, false, "","","");
            MainServer.Instance.AddHttpServer(m_server);

            IConfigSource config = new IniConfigSource();
            config.AddConfig("Startup");

            CapabilitiesModule capsModule = new CapabilitiesModule();
            m_eqgMod = new EventQueueGetModule();

            // For NPC test support
            config.AddConfig("NPC");
            config.Configs["NPC"].Set("Enabled", "true");
            m_npcMod = new NPCModule();

            m_scene = new SceneHelpers().SetupScene();
            SceneHelpers.SetupSceneModules(m_scene, config, capsModule, m_eqgMod, m_npcMod);
        }

        [Fact]
        public void TestAddForClient()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            SceneHelpers.AddScenePresence(m_scene, TestHelpers.ParseTail(0x1));

            // TODO: Add more assertions for the other aspects of event queues
            Assert.Single(m_server.GetPollServiceHandlerKeys());
        }

        [Fact]
        public void TestRemoveForClient()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            UUID spId = TestHelpers.ParseTail(0x1);

            SceneHelpers.AddScenePresence(m_scene, spId);
            m_scene.CloseAgent(spId, false);

            // TODO: Add more assertions for the other aspects of event queues
            Assert.Empty(m_server.GetPollServiceHandlerKeys());
        }

        [Fact]
        public void TestEnqueueMessage()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            ScenePresence sp = SceneHelpers.AddScenePresence(m_scene, TestHelpers.ParseTail(0x1));

            string messageName = "TestMessage";

            m_eqgMod.Enqueue(m_eqgMod.BuildEvent(messageName, new OSDMap()), sp.UUID);

            Hashtable eventsResponse = m_eqgMod.GetEvents(UUID.Zero, sp.UUID);

            // initial queue as null events
//            eventsResponse = m_eqgMod.GetEvents(UUID.Zero, sp.UUID);
            if((int)eventsResponse["int_response_code"] != (int)HttpStatusCode.OK)
            {
                eventsResponse = m_eqgMod.GetEvents(UUID.Zero, sp.UUID);
                if((int)eventsResponse["int_response_code"] != (int)HttpStatusCode.OK)
                    eventsResponse = m_eqgMod.GetEvents(UUID.Zero, sp.UUID);
            }

            Assert.Equal((int)HttpStatusCode.OK, (int)eventsResponse["int_response_code"]);

//            Console.WriteLine("Response [{0}]", (string)eventsResponse["str_response_string"]);
            string data = String.Empty;
            if(eventsResponse["bin_response_data"] != null)
                data = Encoding.UTF8.GetString((byte[])eventsResponse["bin_response_data"]);

            OSDMap rawOsd = (OSDMap)OSDParser.DeserializeLLSDXml(data);
            OSDArray eventsOsd = (OSDArray)rawOsd["events"];

            bool foundUpdate = false;
            foreach (OSD osd in eventsOsd)
            {
                OSDMap eventOsd = (OSDMap)osd;

                if (eventOsd["message"] == messageName)
                    foundUpdate = true;
            }

            Assert.True(foundUpdate, string.Format("Did not find {0} in response", messageName));
        }

        /// <summary>
        /// A moderated-mode change reaches the agent as ChatterBoxSessionUpdate, with the mode under
        /// info.moderated_mode.voice and the session id beside it, which is where the viewer reads them.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TestChatterBoxSessionUpdateCarriesTheModeratedMode(bool moderated)
        {
            TestHelpers.InMethod();

            ScenePresence sp = SceneHelpers.AddScenePresence(m_scene, TestHelpers.ParseTail(0x1));
            UUID sessionId = TestHelpers.ParseTail(0x77);

            m_eqgMod.ChatterBoxSessionUpdate(sessionId, sp.UUID, moderated);

            OSDMap update = null;
            for (int attempt = 0; attempt < 3 && update is null; attempt++)
            {
                Hashtable eventsResponse = m_eqgMod.GetEvents(UUID.Zero, sp.UUID);
                if ((int)eventsResponse["int_response_code"] != (int)HttpStatusCode.OK
                    || eventsResponse["bin_response_data"] is null)
                    continue;

                string data = Encoding.UTF8.GetString((byte[])eventsResponse["bin_response_data"]);
                OSDArray eventsOsd = (OSDArray)((OSDMap)OSDParser.DeserializeLLSDXml(data))["events"];
                foreach (OSD osd in eventsOsd)
                {
                    OSDMap eventOsd = (OSDMap)osd;
                    if (eventOsd["message"] == "ChatterBoxSessionUpdate")
                        update = (OSDMap)eventOsd["body"];
                }
            }

            Assert.NotNull(update);
            Assert.Equal(sessionId, update["session_id"].AsUUID());
            Assert.Equal(moderated, ((OSDMap)((OSDMap)update["info"])["moderated_mode"])["voice"].AsBoolean());
        }

        /// <summary>
        /// ScriptRunningReply tells the viewer's script editor the compile target to show: Mono, Luau (the Luau
        /// compiler is in use) and LuauLanguage (the script is written in Lua), the fields Second Life's viewer reads.
        /// "luau" is Luau and LuauLanguage, "lsl-luau" is Luau only, and anything else is Mono as before.
        /// </summary>
        [Theory]
        [InlineData(ScriptCompileTarget.Luau, false, true, true)]
        [InlineData(ScriptCompileTarget.LslOnLuau, false, true, false)]
        [InlineData(ScriptCompileTarget.Unspecified, true, false, false)]
        public void TestScriptRunningReplyCarriesTheCompileTarget(ScriptCompileTarget target, bool mono, bool luau, bool luauLanguage)
        {
            TestHelpers.InMethod();

            ScenePresence sp = SceneHelpers.AddScenePresence(m_scene, TestHelpers.ParseTail(0x1));
            UUID objectId = TestHelpers.ParseTail(0x51), itemId = TestHelpers.ParseTail(0x52);

            m_eqgMod.ScriptRunningEvent(objectId, itemId, true, sp.UUID, target);

            OSDMap script = null;
            for (int attempt = 0; attempt < 3 && script is null; attempt++)
            {
                Hashtable eventsResponse = m_eqgMod.GetEvents(UUID.Zero, sp.UUID);
                if ((int)eventsResponse["int_response_code"] != (int)HttpStatusCode.OK
                    || eventsResponse["bin_response_data"] is null)
                    continue;

                string data = Encoding.UTF8.GetString((byte[])eventsResponse["bin_response_data"]);
                OSDArray eventsOsd = (OSDArray)((OSDMap)OSDParser.DeserializeLLSDXml(data))["events"];
                foreach (OSD osd in eventsOsd)
                {
                    OSDMap eventOsd = (OSDMap)osd;
                    if (eventOsd["message"] == "ScriptRunningReply")
                        script = (OSDMap)((OSDArray)((OSDMap)eventOsd["body"])["Script"])[0];
                }
            }

            Assert.NotNull(script);
            Assert.Equal(objectId, script["ObjectID"].AsUUID());
            Assert.Equal(itemId, script["ItemID"].AsUUID());
            Assert.True(script["Running"].AsBoolean());
            Assert.Equal(mono, script["Mono"].AsBoolean());
            Assert.Equal(luau, script.ContainsKey("Luau") && script["Luau"].AsBoolean());
            Assert.Equal(luauLanguage, script.ContainsKey("LuauLanguage") && script["LuauLanguage"].AsBoolean());
        }

        /// <summary>
        /// Test an attempt to put a message on the queue of a user that is not in the region.
        /// </summary>
        [Fact]
        public void TestEnqueueMessageNoUser()
        {
            TestHelpers.InMethod();
            TestHelpers.EnableLogging();

            string messageName = "TestMessage";

            m_eqgMod.Enqueue(m_eqgMod.BuildEvent(messageName, new OSDMap()), TestHelpers.ParseTail(0x1));

            Hashtable eventsResponse = m_eqgMod.GetEvents(UUID.Zero, TestHelpers.ParseTail(0x1));

            Assert.Equal((int)HttpStatusCode.NotFound, (int)eventsResponse["int_response_code"]);
        }

        /// <summary>
        /// NPCs do not currently have an event queue but a caller may try to send a message anyway, so check behaviour.
        /// </summary>
        [Fact]
        public void TestEnqueueMessageToNpc()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            UUID npcId
                = m_npcMod.CreateNPC(
                    "John", "Smith", new Vector3(128, 128, 30), UUID.Zero, true, m_scene, new AvatarAppearance());

            ScenePresence npc = m_scene.GetScenePresence(npcId);

            string messageName = "TestMessage";

            m_eqgMod.Enqueue(m_eqgMod.BuildEvent(messageName, new OSDMap()), npc.UUID);

            Hashtable eventsResponse = m_eqgMod.GetEvents(UUID.Zero, npc.UUID);

            Assert.Equal((int)HttpStatusCode.NotFound, (int)eventsResponse["int_response_code"]);
        }
    }
}
