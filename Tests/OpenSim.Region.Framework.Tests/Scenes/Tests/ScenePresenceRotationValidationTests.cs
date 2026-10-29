/*
 * Copyright (c) Legion Builds
 * ScenePresenceRotationValidationTests.cs - checks that an avatar ignores a body rotation
 * that is not finite or is too short to be a rotation.
 */

using System;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// A body rotation that reaches an avatar from outside the simulator (the viewer's agent update,
    /// agent data from another simulator, or a caller of ScenePresence.Rotation such as a script)
    /// must be finite and long enough to be a rotation. Anything else is ignored and the previous
    /// rotation is kept. Valid rotations are stored exactly as given.
    /// </summary>
    public class ScenePresenceRotationValidationTests : OpenSimTestCase
    {
        // Valid but deliberately not unit length, to show that valid input is stored as given
        // and not normalised.
        private static readonly Quaternion ValidRotation = new Quaternion(0f, 0f, 0.6f, 0.9f);
        private static readonly Quaternion OtherValidRotation = new Quaternion(0f, 0f, -0.3826834f, 0.9238795f);

        public static TheoryData<string> BadRotations => new TheoryData<string>
        {
            "NaN", "PartNaN", "PositiveInfinity", "NegativeInfinity", "Zero", "Tiny"
        };

        private static Quaternion Bad(string kind)
        {
            switch (kind)
            {
                case "NaN": return new Quaternion(float.NaN, float.NaN, float.NaN, float.NaN);
                case "PartNaN": return new Quaternion(0f, 0f, float.NaN, 1f);
                case "PositiveInfinity": return new Quaternion(0f, 0f, float.PositiveInfinity, 1f);
                case "NegativeInfinity": return new Quaternion(float.NegativeInfinity, 0f, 0f, 0f);
                case "Zero": return new Quaternion(0f, 0f, 0f, 0f);
                case "Tiny": return new Quaternion(0f, 0f, 1e-5f, 1e-5f);
                default: throw new ArgumentException(kind);
            }
        }

        private static void AssertBitwiseEqual(Quaternion expected, Quaternion actual)
        {
            Assert.Equal(BitConverter.SingleToInt32Bits(expected.X), BitConverter.SingleToInt32Bits(actual.X));
            Assert.Equal(BitConverter.SingleToInt32Bits(expected.Y), BitConverter.SingleToInt32Bits(actual.Y));
            Assert.Equal(BitConverter.SingleToInt32Bits(expected.Z), BitConverter.SingleToInt32Bits(actual.Z));
            Assert.Equal(BitConverter.SingleToInt32Bits(expected.W), BitConverter.SingleToInt32Bits(actual.W));
        }

        private static void SendAgentUpdate(ScenePresence sp, Quaternion bodyRotation, uint controlFlags = 0)
        {
            AgentUpdateArgs args = new AgentUpdateArgs();
            args.BodyRotation = bodyRotation;
            args.HeadRotation = Quaternion.Identity;
            args.ControlFlags = controlFlags;
            sp.HandleAgentUpdate(sp.ControllingClient, args);
        }

        private static AgentData AgentDataWithRotation(ScenePresence source, Quaternion bodyRotation)
        {
            AgentData data = new AgentData();
            source.CopyTo(data, false);
            data.BodyRotation = bodyRotation;
            return data;
        }

        [Theory]
        [MemberData(nameof(BadRotations))]
        public void AgentUpdateWithBadBodyRotationKeepsPreviousRotation(string kind)
        {
            TestHelpers.InMethod();

            TestScene scene = new SceneHelpers().SetupScene();
            ScenePresence sp = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x1));
            sp.AbsolutePosition = new Vector3(128, 128, 30);
            Assert.NotNull(sp.PhysicsActor);

            SendAgentUpdate(sp, ValidRotation);
            AssertBitwiseEqual(ValidRotation, sp.Rotation);

            // A bad rotation arrives together with a walk-forward control, so the rotation is used
            // to turn the movement into a velocity.
            SendAgentUpdate(sp, Bad(kind), (uint)AgentManager.ControlFlags.AGENT_CONTROL_AT_POS);

            AssertBitwiseEqual(ValidRotation, sp.Rotation);

            for (int i = 0; i < 10; i++)
                scene.Update(1);

            Assert.True(sp.AbsolutePosition.IsFinite(), "position " + sp.AbsolutePosition);
            Assert.True(sp.Velocity.IsFinite(), "velocity " + sp.Velocity);

            // What a neighbouring region would be sent on a crossing.
            AgentData crossing = new AgentData();
            sp.CopyTo(crossing, true);
            AssertBitwiseEqual(ValidRotation, crossing.BodyRotation);
        }

        [Fact]
        public void AgentUpdateWithValidBodyRotationStoresItUnchanged()
        {
            TestHelpers.InMethod();

            TestScene scene = new SceneHelpers().SetupScene();
            ScenePresence sp = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x1));

            SendAgentUpdate(sp, ValidRotation);
            AssertBitwiseEqual(ValidRotation, sp.Rotation);

            SendAgentUpdate(sp, OtherValidRotation);
            AssertBitwiseEqual(OtherValidRotation, sp.Rotation);
        }

        [Theory]
        [MemberData(nameof(BadRotations))]
        public void ChildAgentDataWithBadBodyRotationKeepsPreviousRotation(string kind)
        {
            TestHelpers.InMethod();

            TestScene scene = new SceneHelpers().SetupScene();
            ScenePresence source = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x1));
            ScenePresence child = SceneHelpers.AddChildScenePresence(scene, TestHelpers.ParseTail(0x2));
            Assert.True(child.IsChildAgent);

            child.UpdateChildAgent(AgentDataWithRotation(source, ValidRotation));
            AssertBitwiseEqual(ValidRotation, child.Rotation);

            child.UpdateChildAgent(AgentDataWithRotation(source, Bad(kind)));
            AssertBitwiseEqual(ValidRotation, child.Rotation);
        }

        [Fact]
        public void ChildAgentDataWithValidBodyRotationStoresItUnchanged()
        {
            TestHelpers.InMethod();

            TestScene scene = new SceneHelpers().SetupScene();
            ScenePresence source = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x1));
            ScenePresence child = SceneHelpers.AddChildScenePresence(scene, TestHelpers.ParseTail(0x2));

            child.UpdateChildAgent(AgentDataWithRotation(source, OtherValidRotation));
            AssertBitwiseEqual(OtherValidRotation, child.Rotation);
        }

        /// <summary>
        /// Scripts (PRIM_ROTATION and PRIM_ROT_LOCAL on a seated avatar, NPC rotation functions)
        /// assign ScenePresence.Rotation directly.
        /// </summary>
        [Theory]
        [MemberData(nameof(BadRotations))]
        public void SettingBadRotationKeepsPreviousRotation(string kind)
        {
            TestHelpers.InMethod();

            TestScene scene = new SceneHelpers().SetupScene();
            ScenePresence sp = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x1));

            sp.Rotation = ValidRotation;
            sp.Rotation = Bad(kind);

            AssertBitwiseEqual(ValidRotation, sp.Rotation);
        }

        [Fact]
        public void SettingValidRotationStoresItUnchanged()
        {
            TestHelpers.InMethod();

            TestScene scene = new SceneHelpers().SetupScene();
            ScenePresence sp = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x1));

            sp.Rotation = ValidRotation;
            AssertBitwiseEqual(ValidRotation, sp.Rotation);
        }
    }
}
