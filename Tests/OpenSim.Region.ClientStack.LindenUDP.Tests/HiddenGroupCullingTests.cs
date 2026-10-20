/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

namespace OpenSim.Region.ClientStack.LindenUDP.Tests;

/// <summary>
/// With culling by distance on, a viewer is sent the objects near it. An object that has been hidden from the viewers
/// because it crossed into another region (its old copy waits for the other region to report) is not sent again by
/// the culling pass, and is again once it is shown.
/// </summary>
public class HiddenGroupCullingTests : OpenSimTestCase
{
    private static readonly FieldInfo GroupsInView = typeof(LLClientView).GetField("GroupsInView", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo SetHidden = typeof(SceneObjectGroup).GetProperty(nameof(SceneObjectGroup.HiddenForCrossing))!.GetSetMethod(true)!;

    // the group is hidden by its own crossing (the old copy at the moment the other region has the object)
    private static void Hide(SceneObjectGroup group, bool hidden) => SetHidden.Invoke(group, new object[] { hidden });

    public HiddenGroupCullingTests()
    {
        // everything on this thread: the circuit is made when the packet is handled
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
    }

    public override void Dispose()
    {
        Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        base.Dispose();
    }

    private static bool InView(LLClientView client, SceneObjectGroup group)
    {
        HashSet<SceneObjectGroup> groups = (HashSet<SceneObjectGroup>)GroupsInView.GetValue(client)!;
        lock (groups)
            return groups.Contains(group);
    }

    [Fact]
    public void A_group_hidden_for_a_crossing_is_not_put_in_view_by_the_culling_pass_until_it_is_shown_again()
    {
        TestHelpers.InMethod();
        Scene scene = new SceneHelpers().SetupScene();
        scene.ObjectsCullingByDistance = true;
        TestLLUDPServer udpServer = ClientStackHelpers.AddUdpServer(scene);
        ScenePresence sp = ClientStackHelpers.AddChildClient(
            scene, udpServer, TestHelpers.ParseTail(0x1), TestHelpers.ParseTail(0x2), 123456);
        LLClientView client = (LLClientView)sp.ControllingClient;
        client.IsActive = true;

        Vector3 here = sp.AbsolutePosition;
        SceneObjectGroup shown = SceneHelpers.AddSceneObject(scene, 1, TestHelpers.ParseTail(0x10), "shown", 0x20);
        SceneObjectGroup hidden = SceneHelpers.AddSceneObject(scene, 1, TestHelpers.ParseTail(0x10), "hidden", 0x21);
        shown.AbsolutePosition = here + new Vector3(2f, 0f, 0f);
        hidden.AbsolutePosition = here + new Vector3(0f, 2f, 0f);
        Hide(hidden, true);

        client.CheckGroupsInView();

        Assert.True(InView(client, shown), "the control group near the viewer is not in view");
        Assert.False(InView(client, hidden), "the hidden group was put in view");

        Hide(hidden, false);
        client.CheckGroupsInView();

        Assert.True(InView(client, hidden), "the group is not in view once it is shown again");
    }
}
