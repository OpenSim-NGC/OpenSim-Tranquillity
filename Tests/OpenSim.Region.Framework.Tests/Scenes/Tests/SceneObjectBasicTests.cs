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

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// Basic scene object tests (create, read and delete but not update).
    /// </summary>
    public class SceneObjectBasicTests : OpenSimTestCase
    {
//        //        public void TearDown()
//        {
//            Console.WriteLine("TearDown");
//            GC.Collect();
//            Thread.Sleep(3000);
//        }

//        public class GcNotify
//        {
//            public static AutoResetEvent gcEvent = new AutoResetEvent(false);
//            private static bool _initialized = false;
//
//            public static void Initialize()
//            {
//                if (!_initialized)
//                {
//                    _initialized = true;
//                    new GcNotify();
//                }
//            }
//
//            private GcNotify(){}
//
//            ~GcNotify()
//            {
//                if (!Environment.HasShutdownStarted)
//                {
//                    Console.WriteLine("GcNotify called");
//                    gcEvent.Set();
//                    new GcNotify();
//                }
//            }
//        }

        /// <summary>
        /// Test adding an object to a scene.
        /// </summary>
        [Fact]
        public void TestAddSceneObject()
        {
            TestHelpers.InMethod();

            Scene scene = new SceneHelpers().SetupScene();
            int partsToTestCount = 3;

            SceneObjectGroup so = SceneHelpers.CreateSceneObject(partsToTestCount, TestHelpers.ParseTail(0x1), "obj1", 0x10);
            SceneObjectPart[] parts = so.Parts;

            scene.AddNewSceneObject(so, false).Should().BeTrue();
            //Assert.That(scene.AddNewSceneObject(so, false));
            
            SceneObjectGroup retrievedSo = scene.GetSceneObjectGroup(so.UUID);
            SceneObjectPart[] retrievedParts = retrievedSo.Parts;

            //m_log.Debug("retrievedPart : {0}", retrievedPart);
            // If the parts have the same UUID then we will consider them as one and the same
            retrievedSo.PrimCount.Should().Be(partsToTestCount);
            // Assert.Equal(,);

            for (int i = 0; i < partsToTestCount; i++)
            {
                retrievedParts[i].Name.Should().Be(parts[i].Name); 
                retrievedParts[i].UUID.Should().Be(parts[i].UUID);
                //Assert.Equal(,);
                //Assert.Equal(,);
            }
        }

        [Fact]
        /// <summary>
        /// It shouldn't be possible to add a scene object if one with that uuid already exists in the scene.
        /// </summary>
        public void TestAddExistingSceneObjectUuid()
        {
            TestHelpers.InMethod();

            Scene scene = new SceneHelpers().SetupScene();

            string obj1Name = "Alfred";
            string obj2Name = "Betty";
            UUID objUuid = new UUID("00000000-0000-0000-0000-000000000001");

            SceneObjectPart part1
                = new SceneObjectPart(UUID.Zero, PrimitiveBaseShape.Default, Vector3.Zero, Quaternion.Identity, Vector3.Zero)
                    { Name = obj1Name, UUID = objUuid };

            Assert.That(scene.AddNewSceneObject(new SceneObjectGroup(part1), false));

            SceneObjectPart part2
                = new SceneObjectPart(UUID.Zero, PrimitiveBaseShape.Default, Vector3.Zero, Quaternion.Identity, Vector3.Zero)
                    { Name = obj2Name, UUID = objUuid };

            Assert.That(scene.AddNewSceneObject(new SceneObjectGroup(part2), false));

            SceneObjectPart retrievedPart = scene.GetSceneObjectPart(objUuid);

            //m_log.Debug("retrievedPart : {0}", retrievedPart);
            // If the parts have the same UUID then we will consider them as one and the same
            Assert.Equal(,);
            Assert.Equal(,);
        }

        /// <summary>
        /// Test retrieving a scene object via the local id of one of its parts.
        /// </summary>
        [Fact]
        public void TestGetSceneObjectByPartLocalId()
        {
            TestHelpers.InMethod();

            Scene scene = new SceneHelpers().SetupScene();
            int partsToTestCount = 3;

            SceneObjectGroup so
                = SceneHelpers.CreateSceneObject(partsToTestCount, TestHelpers.ParseTail(0x1), "obj1", 0x10);
            SceneObjectPart[] parts = so.Parts;

            scene.AddNewSceneObject(so, false);

            // Test getting via the root part's local id
            Assert.True(scene.GetGroupByPrim(so.LocalId));

            // Test getting via a non root part's local id
            Assert.True(scene.GetGroupByPrim(parts[partsToTestCount - 1].LocalId));

            // Test that we don't get back an object for a local id that doesn't exist
            Assert.True(scene.GetGroupByPrim(999));

            uint soid = so.LocalId;
            uint spid = parts[partsToTestCount - 1].LocalId;

            // Now delete the scene object and check again
            scene.DeleteSceneObject(so, false);

            Assert.True(scene.GetGroupByPrim(soid));
            Assert.True(scene.GetGroupByPrim(spid));
        }

        /// <summary>
        /// Test deleting an object from a scene.
        /// </summary>
        /// <remarks>
        /// This is the most basic form of delete.  For all more sophisticated forms of derez (done asynchrnously
        /// and where object can be taken to user inventory, etc.), see SceneObjectDeRezTests.
        /// </remarks>
        [Fact]
        public void TestDeleteSceneObject()
        {
            TestHelpers.InMethod();

            TestScene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);

            Assert.True(so.IsDeleted);
            uint retrievedPartID = so.LocalId;

            scene.DeleteSceneObject(so, false);

            SceneObjectPart retrievedPart = scene.GetSceneObjectPart(retrievedPartID);

            // TODO: Fix this assertion
        }

        /// <summary>
        /// Changing a scene object uuid changes the root part uuid.  This is a valid operation if the object is not
        /// in a scene and is useful if one wants to supply a UUID directly rather than use the one generated by
        /// OpenSim.
        /// </summary>
        [Fact]
        public void TestChangeSceneObjectUuid()
        {
            string rootPartName = "rootpart";
            UUID rootPartUuid = new UUID("00000000-0000-0000-0000-000000000001");
            string childPartName = "childPart";
            UUID childPartUuid = new UUID("00000000-0000-0000-0001-000000000000");

            SceneObjectPart rootPart
                = new SceneObjectPart(UUID.Zero, PrimitiveBaseShape.Default, Vector3.Zero, Quaternion.Identity, Vector3.Zero)
                    { Name = rootPartName, UUID = rootPartUuid };
            SceneObjectPart linkPart
                = new SceneObjectPart(UUID.Zero, PrimitiveBaseShape.Default, Vector3.Zero, Quaternion.Identity, Vector3.Zero)
                    { Name = childPartName, UUID = childPartUuid };

            SceneObjectGroup sog = new SceneObjectGroup(rootPart);
            sog.AddPart(linkPart);

            Assert.Equal(,);
            Assert.Equal(,);
            Assert.Equal(,);

            UUID newRootPartUuid = new UUID("00000000-0000-0000-0000-000000000002");
            sog.UUID = newRootPartUuid;

            Assert.Equal(,);
            Assert.Equal(,);
            Assert.Equal(,);
        }
    }
}