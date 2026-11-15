/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using Xunit.Abstractions;
using static OpenSim.Region.PhysicsModules.Jolt.Tests.MeshShapesTodayTests;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// A manual sit on a part with no sit target, through a real region scene running on the Jolt engine
/// (ScenePresence.HandleAgentRequestSit -> SendSitResponse -> PhysicsSit -> JoltScene.SitAvatar).
///
/// With [Jolt] PhysicsSit = false the engine returns 0 and core seats the avatar itself, as it did before the engine
/// answered sits: the clicked point raised by half the avatar's height, and only when the avatar is within 10 m of the
/// clicked point (ScenePresence.SendSitResponse, DistanceSquared > 100 returns with no answer to the viewer). With the
/// switch on, the engine answers through its callback and core's 10 m rule is not reached. Serial with the other
/// native tests.
/// </summary>
[Collection(JoltNativeSerial.Name)]
[DeleteScratchFolders]
public class PhysicsSitSceneTests
{
    private readonly ITestOutputHelper _out;
    public PhysicsSitSceneTests(ITestOutputHelper output) { _out = output; }

    private static readonly Vector3 BoxAt = new(128f, 128f, 25.25f);
    private static readonly Vector3 TopCentre = new(0f, 0f, 0.25f);   // the viewer's click on the top centre

    private sealed class Region : IDisposable
    {
        public readonly TestScene Scene;
        public readonly JoltScene Jolt;

        public Region(bool physicsSit)
        {
            Jolt = NewScene(Scratch(), ("PhysicsSit", physicsSit ? "true" : "false"));
            Scene = new SceneHelpers().SetupScene();
            Scene.PhysicsScene = Jolt;
        }

        public SceneObjectPart Box()
        {
            SceneObjectGroup so = SceneHelpers.CreateSceneObject(1, UUID.Random(), "seat", 0x60);
            SceneObjectPart part = so.RootPart;
            part.Shape = PrimitiveBaseShape.CreateBox();
            part.Scale = new Vector3(0.5f, 0.5f, 0.5f);
            so.AbsolutePosition = BoxAt;
            Scene.AddNewSceneObject(so, false);
            Jolt.Simulate(1f / 11f);
            return part;
        }

        public ScenePresence Avatar(Vector3 at)
        {
            ScenePresence sp = SceneHelpers.AddScenePresence(Scene, UUID.Random());
            sp.AbsolutePosition = at;
            return sp;
        }

        public void Dispose() => Jolt.Dispose();
    }

    private void Say(ScenePresence sp, string what)
        => _out.WriteLine($"SIT-SCENE {what}: parent={sp.ParentID} offset={sp.OffsetPosition} at={sp.AbsolutePosition}");

    // Switch off, avatar 2 m from the box: core's own seat, the clicked point raised by half the avatar's height,
    // as an offset from the root (SendSitResponse sets AbsolutePosition = part + offset + (0, 0, h/2); HandleAgentSit
    // takes the group position off).
    [Fact]
    public void With_PhysicsSit_off_core_seats_the_avatar_at_the_clicked_point_as_before()
    {
        using var r = new Region(physicsSit: false);
        SceneObjectPart box = r.Box();
        ScenePresence sp = r.Avatar(BoxAt + new Vector3(-2f, 0f, 0.6f));
        float half = sp.PhysicsActor != null ? sp.PhysicsActor.Size.Z * 0.5f : 0f;

        sp.HandleAgentRequestSit(sp.ControllingClient, sp.UUID, box.UUID, TopCentre);
        Say(sp, "off 2 m");

        Assert.Equal(box.LocalId, sp.ParentID);
        Assert.Contains(sp.UUID, box.GetSittingAvatars().Select(a => a.UUID));
        Vector3 expected = TopCentre + new Vector3(0f, 0f, half);
        Assert.True(Vector3.Distance(expected, sp.OffsetPosition) < 0.001f, $"offset {sp.OffsetPosition}, expected {expected}");
    }

    // Switch off, avatar 10 m and a little from the clicked point (JoltTestingRegion1's seat table stood the avatar
    // at <170, 60, 26.05> for a box at <180, 60, 25.5>): core's fallback refuses beyond 10 m and sends nothing.
    // The fallback is synchronous, so no wait is needed.
    [Fact]
    public void With_PhysicsSit_off_a_sit_from_beyond_10_m_is_refused_by_core()
    {
        using var r = new Region(physicsSit: false);
        SceneObjectPart box = r.Box();
        ScenePresence sp = r.Avatar(BoxAt + TopCentre + new Vector3(-10f, 0f, 0.3f));

        sp.HandleAgentRequestSit(sp.ControllingClient, sp.UUID, box.UUID, TopCentre);
        Say(sp, "off 10 m");

        Assert.Equal(0u, sp.ParentID);
        Assert.True(box.GetSittingAvatars() is null || box.GetSittingAvatars().Count == 0, "the box has a sitter");
    }

    // Switch on, the same 10 m: the engine answers (on another thread) and seats the avatar; core's 10 m rule is
    // not reached. Waits for the seat, with a generous cap.
    [Fact]
    public void With_PhysicsSit_on_the_engine_seats_the_avatar_from_10_m()
    {
        using var r = new Region(physicsSit: true);
        SceneObjectPart box = r.Box();
        ScenePresence sp = r.Avatar(BoxAt + TopCentre + new Vector3(-10f, 0f, 0.3f));

        sp.HandleAgentRequestSit(sp.ControllingClient, sp.UUID, box.UUID, TopCentre);
        DateTime until = DateTime.UtcNow.AddSeconds(10);
        while (sp.ParentID == 0 && DateTime.UtcNow < until)
            Thread.Sleep(50);
        Say(sp, "on 10 m");

        Assert.Equal(box.LocalId, sp.ParentID);
        Assert.Contains(sp.UUID, box.GetSittingAvatars().Select(a => a.UUID));
    }
}
