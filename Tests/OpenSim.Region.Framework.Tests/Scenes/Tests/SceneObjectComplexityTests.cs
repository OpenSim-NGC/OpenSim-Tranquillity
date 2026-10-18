/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// A physics engine that limits how complex a physical object may be reports it through
    /// <see cref="PhysicsActor.OnComplexityError"/>. Core tells the object's owner "Object was too complex to be made
    /// physical: " and the engine's count, and makes the whole object non-physical, as llSetStatus(STATUS_PHYSICS, FALSE)
    /// does. A report made while the object is being made physical (an engine can only count once its parts are in) is
    /// carried out when that operation has gone through all the parts, never in the middle of it. An engine whose cost
    /// is a count of collision shapes has it reported as it is.
    /// </summary>
    public class SceneObjectComplexityTests : OpenSimTestCase
    {
        private const string Detail = "(300 of 256 convex hulls)";
        private const string Message = "Object was too complex to be made physical: " + Detail;

        /// <summary>A stand-in prim actor that remembers whether it was made physical, as an engine's actor does.</summary>
        private class Body : NullPhysicsActor
        {
            public override bool IsPhysical { get; set; }
            public float Cost = 0.1f;
            public bool CostIsCount;
            public override float PhysicsCost => Cost;
            public override bool PhysicsCostIsShapeCount => CostIsCount;
        }

        /// <summary>An actor that reports the object too complex when it is made physical.</summary>
        private sealed class RefusesWhenMadePhysical : Body
        {
            public override bool IsPhysical
            {
                get => base.IsPhysical;
                set
                {
                    base.IsPhysical = value;
                    if (value)
                        RaiseComplexityError(Detail);
                }
            }
        }

        /// <summary>An actor that reports the object more complex each time, as an engine does while the parts arrive.</summary>
        private sealed class CountsAsItGoes : Body
        {
            public static int Next;
            public override bool IsPhysical
            {
                get => base.IsPhysical;
                set
                {
                    base.IsPhysical = value;
                    if (value)
                        RaiseComplexityError($"({Next++} of 5 convex hulls)");
                }
            }
        }

        /// <summary>An actor that reports the object it is linked into too complex.</summary>
        private sealed class RefusesWhenLinked : Body
        {
            public override void link(PhysicsActor obj) => obj.RaiseComplexityError(Detail);
        }

        /// <summary>Records the alerts an owner is sent.</summary>
        private class Alerts : DispatchProxy
        {
            public readonly List<(UUID Agent, string Text)> Sent = new();

            public static IDialogModule Create(out Alerts alerts)
            {
                IDialogModule module = Create<IDialogModule, Alerts>();
                alerts = (Alerts)(object)module;
                return module;
            }

            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "SendAlertToUser" && args.Length > 0 && args[0] is UUID agent)
                    lock (Sent) Sent.Add((agent, args.OfType<string>().First()));
                Type returns = method.ReturnType;
                return returns == typeof(void) || !returns.IsValueType ? null : Activator.CreateInstance(returns);
            }
        }

        private static Delegate Handlers(PhysicsActor pa, string eventName)
            => (Delegate)typeof(PhysicsActor).GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(pa);

        private static (TestScene, SceneObjectGroup, Alerts) NewObject(int parts, Func<PhysicsActor> actor)
        {
            TestScene scene = new SceneHelpers().SetupScene();
            scene.RegisterModuleInterface(Alerts.Create(out Alerts alerts));
            SceneObjectGroup so = SceneHelpers.AddSceneObject(scene, parts, TestHelpers.ParseTail(0x1), "part", 0x10);
            foreach (SceneObjectPart part in so.Parts)
                part.PhysActor = actor();
            return (scene, so, alerts);
        }

        [Fact]
        public void ThePhysicalObjectsOwnerIsToldAndEveryPartBecomesNonPhysical()
        {
            TestHelpers.InMethod();
            (TestScene scene, SceneObjectGroup so, Alerts alerts) = NewObject(3, () => new Body());
            so.ScriptSetPhysicsStatus(true);
            Assert.All(so.Parts, p => Assert.True(p.PhysActor.IsPhysical));
            int physical = scene.SceneGraph.GetActiveObjectsCount();

            so.RootPart.PhysActor.RaiseComplexityError(Detail);

            Assert.Equal(new[] { (so.OwnerID, Message) }, alerts.Sent);
            Assert.False(so.UsesPhysics);
            Assert.All(so.Parts, p => Assert.Equal((PrimFlags)0, p.Flags & PrimFlags.Physics));
            Assert.All(so.Parts, p => Assert.False(p.PhysActor.IsPhysical));
            Assert.Equal(physical - 3, scene.SceneGraph.GetActiveObjectsCount());
        }

        [Fact]
        public void ThePartsStopListeningToTheirActors()
        {
            TestHelpers.InMethod();
            (TestScene scene, SceneObjectGroup so, Alerts alerts) = NewObject(1, () => new Body());
            so.ScriptSetPhysicsStatus(true);
            PhysicsActor pa = so.RootPart.PhysActor;
            Assert.NotNull(Handlers(pa, "OnComplexityError"));

            pa.RaiseComplexityError(Detail);

            Assert.Null(Handlers(pa, "OnComplexityError"));
        }

        [Fact]
        public void AnObjectThatIsNotPhysicalIsNotToldAbout()
        {
            TestHelpers.InMethod();
            (TestScene scene, SceneObjectGroup so, Alerts alerts) = NewObject(2, () => new Body());

            so.RootPart.PhysActor.RaiseComplexityError(Detail);

            Assert.Empty(alerts.Sent);
            Assert.False(so.UsesPhysics);
        }

        [Fact]
        public void AReportMadeWhileTheObjectBecomesPhysicalIsCarriedOutWhenAllThePartsAreDone()
        {
            TestHelpers.InMethod();
            (TestScene scene, SceneObjectGroup so, Alerts alerts) = NewObject(3, () => new RefusesWhenMadePhysical());

            so.ScriptSetPhysicsStatus(true);

            // every part was gone through (each one's actor was asked to become physical), and then the refusal came
            Assert.Equal(new[] { (so.OwnerID, Message) }, alerts.Sent);
            Assert.False(so.UsesPhysics);
            Assert.All(so.Parts, p => Assert.Equal((PrimFlags)0, p.Flags & PrimFlags.Physics));
            Assert.All(so.Parts, p => Assert.False(p.PhysActor.IsPhysical));
            Assert.Equal(0, scene.SceneGraph.GetActiveObjectsCount());
        }

        [Fact]
        public void TheOwnerIsToldTheLastCountOfAnEngineThatReportsAsThePartsArrive()
        {
            TestHelpers.InMethod();
            CountsAsItGoes.Next = 6;
            (TestScene scene, SceneObjectGroup so, Alerts alerts) = NewObject(3, () => new CountsAsItGoes());

            so.ScriptSetPhysicsStatus(true);

            Assert.Single(alerts.Sent);
            Assert.EndsWith("(8 of 5 convex hulls)", alerts.Sent[0].Text);   // 6, 7 and 8 were reported, one per part
            Assert.False(so.UsesPhysics);
        }

        [Fact]
        public void TheBuildToolIsToldOnceThatTheOwnerWasToldWhy()
        {
            TestHelpers.InMethod();
            (TestScene scene, SceneObjectGroup so, Alerts alerts) = NewObject(1, () => new RefusesWhenMadePhysical());

            so.UpdateFlags(true, false, false, false);

            Assert.True(so.TakePhysicsTooComplexTold());
            Assert.False(so.TakePhysicsTooComplexTold());

            // a later change that is not refused does not read as one
            so.UpdateFlags(false, false, false, false);
            Assert.False(so.TakePhysicsTooComplexTold());
        }

        [Fact]
        public void AReportMadeWhileAPartIsLinkedIsCarriedOutWhenTheLinkIsDone()
        {
            TestHelpers.InMethod();
            TestScene scene = new SceneHelpers().SetupScene();
            scene.RegisterModuleInterface(Alerts.Create(out Alerts alerts));
            SceneObjectGroup grp1 = SceneHelpers.AddSceneObject(scene, 2, TestHelpers.ParseTail(0x1), "first", 0x10);
            SceneObjectGroup grp2 = SceneHelpers.AddSceneObject(scene, 1, TestHelpers.ParseTail(0x2), "second", 0x20);
            foreach (SceneObjectPart part in grp1.Parts)
                part.PhysActor = new Body();
            grp2.RootPart.PhysActor = new RefusesWhenLinked();
            grp1.ScriptSetPhysicsStatus(true);
            grp1.RootPart.ClearUpdateSchedule();
            grp2.RootPart.ClearUpdateSchedule();
            Assert.Empty(alerts.Sent);

            grp1.LinkToGroup(grp2);

            Assert.Equal(3, grp1.Parts.Length);
            Assert.Single(alerts.Sent);
            Assert.Equal(Message, alerts.Sent[0].Text);
            Assert.False(grp1.UsesPhysics);
            Assert.All(grp1.Parts, p => Assert.Equal((PrimFlags)0, p.Flags & PrimFlags.Physics));
            Assert.All(grp1.Parts, p => Assert.False(p.PhysActor.IsPhysical));
        }

        [Fact]
        public void AnEngineThatNeverReportsLeavesAnyObjectPhysicalAndSaysNothing()
        {
            TestHelpers.InMethod();
            (TestScene scene, SceneObjectGroup so, Alerts alerts) = NewObject(40, () => new Body());

            so.ScriptSetPhysicsStatus(true);
            so.UpdateFlags(true, false, false, false);

            Assert.True(so.UsesPhysics);
            Assert.All(so.Parts, p => Assert.True(p.PhysActor.IsPhysical));
            Assert.Empty(alerts.Sent);
        }

        [Fact]
        public void AnEnginesShapeCountIsReportedAsItIsAndAWeightIsScaledBySizeWhilePhysical()
        {
            TestHelpers.InMethod();
            (TestScene scene, SceneObjectGroup so, Alerts alerts) = NewObject(1, () => new Body { Cost = 12f, CostIsCount = true });
            SceneObjectPart part = so.RootPart;
            part.Scale = new Vector3(4f, 4f, 4f);

            Assert.Equal(12f, part.PhysicsCost);
            so.ScriptSetPhysicsStatus(true);
            Assert.Equal(12f, part.PhysicsCost);

            ((Body)part.PhysActor).CostIsCount = false;
            Assert.True(part.PhysicsCost > 12f);   // a weight is scaled up by the size of a physical part
        }
    }
}
