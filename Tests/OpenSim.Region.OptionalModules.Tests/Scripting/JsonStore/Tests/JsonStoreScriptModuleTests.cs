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
using System.Reflection;
using System.Text;
using log4net;
using Nini.Config;
using Xunit;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Scripting.ScriptModuleComms;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Region.OptionalModules.Scripting.JsonStore.Tests
{
    /// <summary>
    /// Tests for inventory functions in LSL
    /// </summary>
    public class JsonStoreScriptModuleTests : OpenSimTestCase
    {
        private Scene m_scene;
        private MockScriptEngine m_engine;
        private ScriptModuleCommsModule m_smcm;
        private JsonStoreScriptModule m_jssm;

        [TestFixtureSetUp]
        public void FixtureInit()
        {
            // Don't allow tests to be bamboozled by asynchronous events.  Execute everything on the same thread.
            Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
        }

        [TestFixtureTearDown]
        public void TearDown()
        {
            // We must set this back afterwards, otherwise later tests will fail since they're expecting multiple
            // threads.  Possibly, later tests should be rewritten so none of them require async stuff (which regression
            // tests really shouldn't).
            Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        }

        public override void SetUp()
        {
            base.SetUp();

            IConfigSource configSource = new IniConfigSource();
            IConfig jsonStoreConfig = configSource.AddConfig("JsonStore");
            jsonStoreConfig.Set("Enabled", "true");

            m_engine = new MockScriptEngine();
            m_smcm = new ScriptModuleCommsModule();
            JsonStoreModule jsm = new JsonStoreModule();
            m_jssm = new JsonStoreScriptModule();

            m_scene = new SceneHelpers().SetupScene();
            SceneHelpers.SetupSceneModules(m_scene, configSource, m_engine, m_smcm, jsm, m_jssm);

            try
            {
                m_smcm.RegisterScriptInvocation(this, "DummyTestMethod");
            }
            catch (ArgumentException)
            {
                Assert.Ignore("Ignoring test since running on .NET 3.5 or earlier.");
            }

            // XXX: Unfortunately, ICommsModule currently has no way of deregistering methods.
        }

        private object InvokeOp(string name, params object[] args)
        {
            return InvokeOpOnHost(name, UUID.Zero, args);
        }

        private object InvokeOpOnHost(string name, UUID hostId, params object[] args)
        {
            return m_smcm.InvokeOperation(hostId, UUID.Zero, name, args);
        }

        [Fact]
        public void TestJsonCreateStore()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            // Test blank store
            {
                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{}");
                Assert.That(storeId, Is.Not.EqualTo(UUID.Zero));
            }

            // Test single element store
            {
                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{ 'Hello' : 'World' }");
                Assert.That(storeId, Is.Not.EqualTo(UUID.Zero));
            }

            // Test with an integer value
            {
                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{ 'Hello' : 42.15 }");
                Assert.That(storeId, Is.Not.EqualTo(UUID.Zero));

                string value = (string)InvokeOp("JsonGetValue", storeId, "Hello");
                Assert.Equal(,);
            }

            // Test with an array as the root node
            {
                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "[ 'one', 'two', 'three' ]");
                Assert.That(storeId, Is.Not.EqualTo(UUID.Zero));

                string value = (string)InvokeOp("JsonGetValue", storeId, "[1]");
                Assert.Equal(,);
            }
        }

        [Fact]
        public void TestJsonDestroyStore()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{ 'Hello' : 'World' }");
            int dsrv = (int)InvokeOp("JsonDestroyStore", storeId);

            Assert.Equal(,);

            int tprv = (int)InvokeOp("JsonGetNodeType", storeId, "Hello");
            Assert.Equal(,);
        }

        [Fact]
        public void TestJsonDestroyStoreNotExists()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            UUID fakeStoreId = TestHelpers.ParseTail(0x500);

            int dsrv = (int)InvokeOp("JsonDestroyStore", fakeStoreId);

            Assert.Equal(,);
        }

        [Fact]
        public void TestJsonGetValue()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{ 'Hello' : { 'World' : 'Two' } }");

            {
                string value = (string)InvokeOp("JsonGetValue", storeId, "Hello.World");
                Assert.Equal(,);
            }

            // Test get of path section instead of leaf
            {
                string value = (string)InvokeOp("JsonGetValue", storeId, "Hello");
                Assert.Equal(,);
            }

            // Test get of non-existing value
            {
                string fakeValueGet = (string)InvokeOp("JsonGetValue", storeId, "foo");
                Assert.Equal(,);
            }

            // Test get from non-existing store
            {
                UUID fakeStoreId = TestHelpers.ParseTail(0x500);
                string fakeStoreValueGet = (string)InvokeOp("JsonGetValue", fakeStoreId, "Hello");
                Assert.Equal(,);
            }
        }

        [Fact]
        public void TestJsonGetJson()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{ 'Hello' : { 'World' : 'Two' } }");

            {
                string value = (string)InvokeOp("JsonGetJson", storeId, "Hello.World");
                Assert.Equal(,);
            }

            // Test get of path section instead of leaf
            {
                string value = (string)InvokeOp("JsonGetJson", storeId, "Hello");
                Assert.Equal(,);
            }

            // Test get of non-existing value
            {
                string fakeValueGet = (string)InvokeOp("JsonGetJson", storeId, "foo");
                Assert.Equal(,);
            }

            // Test get from non-existing store
            {
                UUID fakeStoreId = TestHelpers.ParseTail(0x500);
                string fakeStoreValueGet = (string)InvokeOp("JsonGetJson", fakeStoreId, "Hello");
                Assert.Equal(,);
            }
        }

//        [Fact]
//        public void TestJsonTakeValue()
//        {
//            TestHelpers.InMethod();
////            TestHelpers.EnableLogging();
//
//            UUID storeId
//                = (UUID)m_smcm.InvokeOperation(
//                    UUID.Zero, UUID.Zero, "JsonCreateStore", new object[] { "{ 'Hello' : 'World' }" });
//
//            string value
//                = (string)m_smcm.InvokeOperation(
//                    UUID.Zero, UUID.Zero, "JsonTakeValue", new object[] { storeId, "Hello" });
//
//            Assert.Equal(,);
//
//            string value2
//                = (string)m_smcm.InvokeOperation(
//                    UUID.Zero, UUID.Zero, "JsonGetValue", new object[] { storeId, "Hello" });
//
//            Assert.Null();
//        }

        [Fact]
        public void TestJsonRemoveValue()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            // Test remove of node in object pointing to a string
            {
                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{ 'Hello' : 'World' }");

                int returnValue = (int)InvokeOp( "JsonRemoveValue", storeId, "Hello");
                Assert.Equal(,);

                int result = (int)InvokeOp("JsonGetNodeType", storeId, "Hello");
                Assert.Equal(,);

                string returnValue2 = (string)InvokeOp("JsonGetValue", storeId, "Hello");
                Assert.Equal(,);
            }

            // Test remove of node in object pointing to another object
            {
                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{ 'Hello' : { 'World' : 'Wally' } }");

                int returnValue = (int)InvokeOp( "JsonRemoveValue", storeId, "Hello");
                Assert.Equal(,);

                int result = (int)InvokeOp("JsonGetNodeType", storeId, "Hello");
                Assert.Equal(,);

                string returnValue2 = (string)InvokeOp("JsonGetJson", storeId, "Hello");
                Assert.Equal(,);
            }

            // Test remove of node in an array
            {
                UUID storeId
                    = (UUID)InvokeOp("JsonCreateStore", "{ 'Hello' : [ 'value1', 'value2' ] }");

                int returnValue = (int)InvokeOp( "JsonRemoveValue", storeId, "Hello[0]");
                Assert.Equal(,);

                int result = (int)InvokeOp("JsonGetNodeType", storeId, "Hello[0]");
                Assert.Equal(,);

                result = (int)InvokeOp("JsonGetNodeType", storeId, "Hello[1]");
                Assert.Equal(,);

                string stringReturnValue = (string)InvokeOp("JsonGetValue", storeId, "Hello[0]");
                Assert.Equal(,);

                stringReturnValue = (string)InvokeOp("JsonGetJson", storeId, "Hello[1]");
                Assert.Equal(,);
            }

            // Test remove of non-existing value
            {
                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{ 'Hello' : 'World' }");

                int fakeValueRemove = (int)InvokeOp("JsonRemoveValue", storeId, "Cheese");
                Assert.Equal(,);
            }

            {
                // Test get from non-existing store
                UUID fakeStoreId = TestHelpers.ParseTail(0x500);
                int fakeStoreValueRemove = (int)InvokeOp("JsonRemoveValue", fakeStoreId, "Hello");
                Assert.Equal(,);
            }
        }

//        [Fact]
//        public void TestJsonTestPath()
//        {
//            TestHelpers.InMethod();
////            TestHelpers.EnableLogging();
//
//            UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{ 'Hello' : { 'World' : 'One' } }");
//
//            {
//                int result = (int)InvokeOp("JsonTestPath", storeId, "Hello.World");
//                Assert.Equal(,);
//            }
//
//            // Test for path which does not resolve to a value.
//            {
//                int result = (int)InvokeOp("JsonTestPath", storeId, "Hello");
//                Assert.Equal(,);
//            }
//
//            {
//                int result2 = (int)InvokeOp("JsonTestPath", storeId, "foo");
//                Assert.Equal(,);
//            }
//
//            // Test with fake store
//            {
//                UUID fakeStoreId = TestHelpers.ParseTail(0x500);
//                int fakeStoreValueRemove = (int)InvokeOp("JsonTestPath", fakeStoreId, "Hello");
//                Assert.Equal(,);
//            }
//        }

//        [Fact]
//        public void TestJsonTestPathJson()
//        {
//            TestHelpers.InMethod();
////            TestHelpers.EnableLogging();
//
//            UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{ 'Hello' : { 'World' : 'One' } }");
//
//            {
//                int result = (int)InvokeOp("JsonTestPathJson", storeId, "Hello.World");
//                Assert.Equal(,);
//            }
//
//            // Test for path which does not resolve to a value.
//            {
//                int result = (int)InvokeOp("JsonTestPathJson", storeId, "Hello");
//                Assert.Equal(,);
//            }
//
//            {
//                int result2 = (int)InvokeOp("JsonTestPathJson", storeId, "foo");
//                Assert.Equal(,);
//            }
//
//            // Test with fake store
//            {
//                UUID fakeStoreId = TestHelpers.ParseTail(0x500);
//                int fakeStoreValueRemove = (int)InvokeOp("JsonTestPathJson", fakeStoreId, "Hello");
//                Assert.Equal(,);
//            }
//        }

        [Fact]
        public void TestJsonGetArrayLength()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{ 'Hello' : { 'World' : [ 'one', 2 ] } }");

            {
                int result = (int)InvokeOp("JsonGetArrayLength", storeId, "Hello.World");
                Assert.Equal(,);
            }

            // Test path which is not an array
            {
                int result = (int)InvokeOp("JsonGetArrayLength", storeId, "Hello");
                Assert.Equal(,);
            }

            // Test fake path
            {
                int result = (int)InvokeOp("JsonGetArrayLength", storeId, "foo");
                Assert.Equal(,);
            }

            // Test fake store
            {
                UUID fakeStoreId = TestHelpers.ParseTail(0x500);
                int result = (int)InvokeOp("JsonGetArrayLength", fakeStoreId, "Hello.World");
                Assert.Equal(,);
            }
        }

        [Fact]
        public void TestJsonGetNodeType()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{ 'Hello' : { 'World' : [ 'one', 2 ] } }");

            {
                int result = (int)InvokeOp("JsonGetNodeType", storeId, ".");
                Assert.Equal(,);
            }

            {
                int result = (int)InvokeOp("JsonGetNodeType", storeId, "Hello");
                Assert.Equal(,);
            }

            {
                int result = (int)InvokeOp("JsonGetNodeType", storeId, "Hello.World");
                Assert.Equal(,);
            }

            {
                int result = (int)InvokeOp("JsonGetNodeType", storeId, "Hello.World[0]");
                Assert.Equal(,);
            }

            {
                int result = (int)InvokeOp("JsonGetNodeType", storeId, "Hello.World[1]");
                Assert.Equal(,);
            }

            // Test for non-existent path
            {
                int result = (int)InvokeOp("JsonGetNodeType", storeId, "foo");
                Assert.Equal(,);
            }

            // Test for non-existent store
            {
                UUID fakeStoreId = TestHelpers.ParseTail(0x500);
                int result = (int)InvokeOp("JsonGetNodeType", fakeStoreId, ".");
                Assert.Equal(,);
            }
        }

        [Fact]
        public void TestJsonList2Path()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            // Invoking these methods directly since I just couldn't get comms module invocation to work for some reason
            // - some confusion with the methods that take a params object[] invocation.
            {
                string result = m_jssm.JsonList2Path(UUID.Zero, UUID.Zero, new object[] { "foo" });
                Assert.Equal(,);
            }

            {
                string result = m_jssm.JsonList2Path(UUID.Zero, UUID.Zero, new object[] { "foo", "bar" });
                Assert.Equal(,);
            }

            {
                string result = m_jssm.JsonList2Path(UUID.Zero, UUID.Zero, new object[] { "foo", 1, "bar" });
                Assert.Equal(,);
            }
        }

        [Fact]
        public void TestJsonSetValue()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            {
                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{}");

                int result = (int)InvokeOp("JsonSetValue", storeId, "Fun", "Times");
                Assert.Equal(,);

                string value = (string)InvokeOp("JsonGetValue", storeId, "Fun");
                Assert.Equal(,);
            }

            // Test setting a key containing periods with delineation
            {
                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{}");

                int result = (int)InvokeOp("JsonSetValue", storeId, "{Fun.Circus}", "Times");
                Assert.Equal(,);

                string value = (string)InvokeOp("JsonGetValue", storeId, "{Fun.Circus}");
                Assert.Equal(,);
            }

            // *** Test [] ***

            // Test setting a key containing unbalanced ] without delineation.  Expecting failure
            {
                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{}");

                int result = (int)InvokeOp("JsonSetValue", storeId, "Fun]Circus", "Times");
                Assert.Equal(,);

                string value = (string)InvokeOp("JsonGetValue", storeId, "Fun]Circus");
                Assert.Equal(,);
            }

            // Test setting a key containing unbalanced [ without delineation.  Expecting failure
            {
                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{}");

                int result = (int)InvokeOp("JsonSetValue", storeId, "Fun[Circus", "Times");
                Assert.Equal(,);

                string value = (string)InvokeOp("JsonGetValue", storeId, "Fun[Circus");
                Assert.Equal(,);
            }

            // Test setting a key containing unbalanced [] without delineation.  Expecting failure
            {
                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{}");

                int result = (int)InvokeOp("JsonSetValue", storeId, "Fun[]Circus", "Times");
                Assert.Equal(,);

                string value = (string)InvokeOp("JsonGetValue", storeId, "Fun[]Circus");
                Assert.Equal(,);
            }

            // Test setting a key containing unbalanced ] with delineation
            {
                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{}");

                int result = (int)InvokeOp("JsonSetValue", storeId, "{Fun]Circus}", "Times");
                Assert.Equal(,);

                string value = (string)InvokeOp("JsonGetValue", storeId, "{Fun]Circus}");
                Assert.Equal(,);
            }

            // Test setting a key containing unbalanced [ with delineation
            {
                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{}");

                int result = (int)InvokeOp("JsonSetValue", storeId, "{Fun[Circus}", "Times");
                Assert.Equal(,);

                string value = (string)InvokeOp("JsonGetValue", storeId, "{Fun[Circus}");
                Assert.Equal(,);
            }

            // Test setting a key containing empty balanced [] with delineation
            {
                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{}");

                int result = (int)InvokeOp("JsonSetValue", storeId, "{Fun[]Circus}", "Times");
                Assert.Equal(,);

                string value = (string)InvokeOp("JsonGetValue", storeId, "{Fun[]Circus}");
                Assert.Equal(,);
            }

//            // Commented out as this currently unexpectedly fails.
//            // Test setting a key containing brackets around an integer with delineation
//            {
//                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{}");
//
//                int result = (int)InvokeOp("JsonSetValue", storeId, "{Fun[0]Circus}", "Times");
//                Assert.Equal(,);
//
//                string value = (string)InvokeOp("JsonGetValue", storeId, "{Fun[0]Circus}");
//                Assert.Equal(,);
//            }

            // *** Test {} ***

            // Test setting a key containing unbalanced } without delineation.  Expecting failure (?)
            {
                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{}");

                int result = (int)InvokeOp("JsonSetValue", storeId, "Fun}Circus", "Times");
                Assert.Equal(,);

                string value = (string)InvokeOp("JsonGetValue", storeId, "Fun}Circus");
                Assert.Equal(,);
            }

            // Test setting a key containing unbalanced { without delineation.  Expecting failure (?)
            {
                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{}");

                int result = (int)InvokeOp("JsonSetValue", storeId, "Fun{Circus", "Times");
                Assert.Equal(,);

                string value = (string)InvokeOp("JsonGetValue", storeId, "Fun}Circus");
                Assert.Equal(,);
            }

//            // Commented out as this currently unexpectedly fails.
//            // Test setting a key containing unbalanced }
//            {
//                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{}");
//
//                int result = (int)InvokeOp("JsonSetValue", storeId, "{Fun}Circus}", "Times");
//                Assert.Equal(,);
//            }

            // Test setting a key containing unbalanced { with delineation
            {
                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{}");

                int result = (int)InvokeOp("JsonSetValue", storeId, "{Fun{Circus}", "Times");
                Assert.Equal(,);

                string value = (string)InvokeOp("JsonGetValue", storeId, "{Fun{Circus}");
                Assert.Equal(,);
            }

            // Test setting a key containing balanced {} with delineation.  This should fail.
            {
                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{}");

                int result = (int)InvokeOp("JsonSetValue", storeId, "{Fun{Filled}Circus}", "Times");
                Assert.Equal(,);

                string value = (string)InvokeOp("JsonGetValue", storeId, "{Fun{Filled}Circus}");
                Assert.Equal(,);
            }

            // Test setting to location that does not exist.  This should fail.
            {
                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{}");

                int result = (int)InvokeOp("JsonSetValue", storeId, "Fun.Circus", "Times");
                Assert.Equal(,);

                string value = (string)InvokeOp("JsonGetValue", storeId, "Fun.Circus");
                Assert.Equal(,);
            }

            // Test with fake store
            {
                UUID fakeStoreId = TestHelpers.ParseTail(0x500);
                int fakeStoreValueSet = (int)InvokeOp("JsonSetValue", fakeStoreId, "Hello", "World");
                Assert.Equal(,);
            }
        }

        [Fact]
        public void TestJsonSetJson()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            // Single quoted token case
            {
                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{ }");

                int result = (int)InvokeOp("JsonSetJson", storeId, "Fun", "'Times'");
                Assert.Equal(,);

                string value = (string)InvokeOp("JsonGetValue", storeId, "Fun");
                Assert.Equal(,);
            }

            // Sub-tree case
            {
                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{ }");

                int result = (int)InvokeOp("JsonSetJson", storeId, "Fun", "{ 'Filled' : 'Times' }");
                Assert.Equal(,);

                string value = (string)InvokeOp("JsonGetValue", storeId, "Fun.Filled");
                Assert.Equal(,);
            }

            // If setting single strings in JsonSetValueJson, these must be single quoted tokens, not bare strings.
            {
                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{ }");

                int result = (int)InvokeOp("JsonSetJson", storeId, "Fun", "Times");
                Assert.Equal(,);

                string value = (string)InvokeOp("JsonGetValue", storeId, "Fun");
                Assert.Equal(,);
            }

            // Test setting to location that does not exist.  This should fail.
            {
                UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{ }");

                int result = (int)InvokeOp("JsonSetJson", storeId, "Fun.Circus", "'Times'");
                Assert.Equal(,);

                string value = (string)InvokeOp("JsonGetValue", storeId, "Fun.Circus");
                Assert.Equal(,);
            }

            // Test with fake store
            {
                UUID fakeStoreId = TestHelpers.ParseTail(0x500);
                int fakeStoreValueSet = (int)InvokeOp("JsonSetJson", fakeStoreId, "Hello", "'World'");
                Assert.Equal(,);
            }
        }

        /// <summary>
        /// Test for writing json to a notecard
        /// </summary>
        /// <remarks>
        /// TODO: Really needs to test correct receipt of the link_message event.  Could do this by directly fetching
        /// it via the MockScriptEngine or perhaps by a dummy script instance.
        /// </remarks>
        [Fact]
        public void TestJsonWriteNotecard()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            SceneObjectGroup so = SceneHelpers.CreateSceneObject(1, TestHelpers.ParseTail(0x1));
            m_scene.AddSceneObject(so);

            UUID storeId = (UUID)InvokeOp("JsonCreateStore", "{ 'Hello':'World' }");

            {
                string notecardName = "nc1";

                // Write notecard
                UUID writeNotecardRequestId = (UUID)InvokeOpOnHost("JsonWriteNotecard", so.UUID, storeId, "", notecardName);
                Assert.That(writeNotecardRequestId, Is.Not.EqualTo(UUID.Zero));

                TaskInventoryItem nc1Item = so.RootPart.Inventory.GetInventoryItem(notecardName);
                Assert.NotNull();

                // TODO: Should independently check the contents.
            }

            // TODO: Write partial test

            {
                // Try to write notecard for a bad path
                // In this case we do get a request id but no notecard is written.
                string badPathNotecardName = "badPathNotecardName";

                UUID writeNotecardBadPathRequestId
                    = (UUID)InvokeOpOnHost("JsonWriteNotecard", so.UUID, storeId, "flibble", badPathNotecardName);
                Assert.That(writeNotecardBadPathRequestId, Is.Not.EqualTo(UUID.Zero));

                TaskInventoryItem badPathItem = so.RootPart.Inventory.GetInventoryItem(badPathNotecardName);
                Assert.Null();
            }

            {
                // Test with fake store
                // In this case we do get a request id but no notecard is written.
                string fakeStoreNotecardName = "fakeStoreNotecardName";

                UUID fakeStoreId = TestHelpers.ParseTail(0x500);
                UUID fakeStoreWriteNotecardValue
                    = (UUID)InvokeOpOnHost("JsonWriteNotecard", so.UUID, fakeStoreId, "", fakeStoreNotecardName);
                Assert.That(fakeStoreWriteNotecardValue, Is.Not.EqualTo(UUID.Zero));

                TaskInventoryItem fakeStoreItem = so.RootPart.Inventory.GetInventoryItem(fakeStoreNotecardName);
                Assert.Null();
            }
        }

        /// <summary>
        /// Test for reading json from a notecard
        /// </summary>
        /// <remarks>
        /// TODO: Really needs to test correct receipt of the link_message event.  Could do this by directly fetching
        /// it via the MockScriptEngine or perhaps by a dummy script instance.
        /// </remarks>
        [Fact]
        public void TestJsonReadNotecard()
        {
            TestHelpers.InMethod();
//            TestHelpers.EnableLogging();

            string notecardName = "nc1";

            SceneObjectGroup so = SceneHelpers.CreateSceneObject(1, TestHelpers.ParseTail(0x1));
            m_scene.AddSceneObject(so);

            UUID creatingStoreId = (UUID)InvokeOp("JsonCreateStore", "{ 'Hello':'World' }");

            // Write notecard
            InvokeOpOnHost("JsonWriteNotecard", so.UUID, creatingStoreId, "", notecardName);

            {
                // Read notecard
                UUID receivingStoreId = (UUID)InvokeOp("JsonCreateStore", "{}");
                UUID readNotecardRequestId = (UUID)InvokeOpOnHost("JsonReadNotecard", so.UUID, receivingStoreId, "", notecardName);
                Assert.That(readNotecardRequestId, Is.Not.EqualTo(UUID.Zero));

                string value = (string)InvokeOp("JsonGetValue", receivingStoreId, "Hello");
                Assert.Equal(,);
            }

            {
                // Read notecard to new single component path
                UUID receivingStoreId = (UUID)InvokeOp("JsonCreateStore", "{}");
                UUID readNotecardRequestId = (UUID)InvokeOpOnHost("JsonReadNotecard", so.UUID, receivingStoreId, "make", notecardName);
                Assert.That(readNotecardRequestId, Is.Not.EqualTo(UUID.Zero));

                string value = (string)InvokeOp("JsonGetValue", receivingStoreId, "Hello");
                Assert.Equal(,);

                value = (string)InvokeOp("JsonGetValue", receivingStoreId, "make.Hello");
                Assert.Equal(,);
            }

            {
                // Read notecard to new multi-component path.  This should not work.
                UUID receivingStoreId = (UUID)InvokeOp("JsonCreateStore", "{}");
                UUID readNotecardRequestId = (UUID)InvokeOpOnHost("JsonReadNotecard", so.UUID, receivingStoreId, "make.it", notecardName);
                Assert.That(readNotecardRequestId, Is.Not.EqualTo(UUID.Zero));

                string value = (string)InvokeOp("JsonGetValue", receivingStoreId, "Hello");
                Assert.Equal(,);

                value = (string)InvokeOp("JsonGetValue", receivingStoreId, "make.it.Hello");
                Assert.Equal(,);
            }

            {
                // Read notecard to existing multi-component path.  This should work
                UUID receivingStoreId = (UUID)InvokeOp("JsonCreateStore", "{ 'make' : { 'it' : 'so' } }");
                UUID readNotecardRequestId = (UUID)InvokeOpOnHost("JsonReadNotecard", so.UUID, receivingStoreId, "make.it", notecardName);
                Assert.That(readNotecardRequestId, Is.Not.EqualTo(UUID.Zero));

                string value = (string)InvokeOp("JsonGetValue", receivingStoreId, "Hello");
                Assert.Equal(,);

                value = (string)InvokeOp("JsonGetValue", receivingStoreId, "make.it.Hello");
                Assert.Equal(,);
            }

            {
                // Read notecard to invalid path.  This should not work.
                UUID receivingStoreId = (UUID)InvokeOp("JsonCreateStore", "{ 'make' : { 'it' : 'so' } }");
                UUID readNotecardRequestId = (UUID)InvokeOpOnHost("JsonReadNotecard", so.UUID, receivingStoreId, "/", notecardName);
                Assert.That(readNotecardRequestId, Is.Not.EqualTo(UUID.Zero));

                string value = (string)InvokeOp("JsonGetValue", receivingStoreId, "Hello");
                Assert.Equal(,);
            }

            {
                // Try read notecard to fake store.
                UUID fakeStoreId = TestHelpers.ParseTail(0x500);
                UUID readNotecardRequestId = (UUID)InvokeOpOnHost("JsonReadNotecard", so.UUID, fakeStoreId, "", notecardName);
                Assert.That(readNotecardRequestId, Is.Not.EqualTo(UUID.Zero));

                string value = (string)InvokeOp("JsonGetValue", fakeStoreId, "Hello");
                Assert.Equal(,);
            }
        }

        public object DummyTestMethod(object o1, object o2, object o3, object o4, object o5) { return null; }
    }
}
