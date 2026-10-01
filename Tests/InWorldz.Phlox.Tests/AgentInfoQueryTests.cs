/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Animation;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// llGetAgentInfo's flags as Halcyon sets them (LSLSystemAPI.llGetAgentInfo) and the SL wiki lists them: AGENT_TYPING
/// from the typing state, AGENT_BUSY from the BUSY animation, AGENT_CROUCHING and AGENT_WALKING from the movement
/// animation ("walking, running or crouch walking"), AGENT_SITTING for a ground sit. llSameGroup as Halcyon's
/// HasMatchingGroup. llSetCameraParams' vector rules. llGetRegionFlags from the estate module.
/// </summary>
// No test reaches a network service. No process-wide state: the class runs in parallel.
public class AgentInfoQueryTests
{
    private const int AGENT_SITTING = 0x10, AGENT_WALKING = 0x80, AGENT_IN_AIR = 0x100, AGENT_TYPING = 0x200,
        AGENT_CROUCHING = 0x400, AGENT_BUSY = 0x800;

    private static int Info(ApiCallRig r, ScenePresence sp) => r.Api.llGetAgentInfo(sp.UUID.ToString());

    [Fact]
    public void TypingSetsAgentTyping()
    {
        using var r = new ApiCallRig();
        ScenePresence sp = r.AddAvatar();
        Assert.Equal(0, Info(r, sp) & AGENT_TYPING);
        sp.State |= (byte)AgentState.Typing;
        Assert.Equal(AGENT_TYPING, Info(r, sp) & AGENT_TYPING);
    }

    [Fact]
    public void TheBusyAnimationSetsAgentBusy()
    {
        using var r = new ApiCallRig();
        ScenePresence sp = r.AddAvatar();
        sp.Animator.AddAnimation(DefaultAvatarAnimations.GetDefaultAnimation("BUSY"), UUID.Zero);
        Assert.Equal(AGENT_BUSY, Info(r, sp) & AGENT_BUSY);
    }

    [Fact]
    public void CrouchingSetsAgentCrouching()
    {
        using var r = new ApiCallRig();
        ScenePresence sp = r.AddAvatar();
        ApiCallRig.SetPrivate(sp.Animator, nameof(ScenePresenceAnimator.CurrentMovementAnimation), "CROUCH");
        int f = Info(r, sp);
        Assert.Equal(AGENT_CROUCHING, f & AGENT_CROUCHING);
        Assert.Equal(0, f & AGENT_WALKING);
    }

    [Theory]
    [InlineData("WALK")]
    [InlineData("CROUCHWALK")]
    [InlineData("RUN")]
    public void WalkingComesFromTheMovementAnimation(string movement)
    {
        using var r = new ApiCallRig();
        ScenePresence sp = r.AddAvatar();
        ApiCallRig.SetPrivate(sp.Animator, nameof(ScenePresenceAnimator.CurrentMovementAnimation), movement);
        int f = Info(r, sp);
        Assert.Equal(AGENT_WALKING, f & AGENT_WALKING);
        Assert.Equal(0, f & AGENT_IN_AIR);
    }

    [Fact]
    public void HoldingForwardWhileStandingIsNotWalking()
    {
        using var r = new ApiCallRig();
        ScenePresence sp = r.AddAvatar();
        ApiCallRig.SetPrivate(sp.Animator, nameof(ScenePresenceAnimator.CurrentMovementAnimation), "STAND");
        sp.AgentControlFlags = (uint)AgentManager.ControlFlags.AGENT_CONTROL_AT_POS;
        Assert.Equal(0, Info(r, sp) & AGENT_WALKING);
    }

    [Fact]
    public void AGroundSitIsSitting()
    {
        using var r = new ApiCallRig();
        ScenePresence sp = r.AddAvatar();
        sp.Animator.Animations.SetImplicitDefaultAnimation(DefaultAvatarAnimations.GetDefaultAnimation("SIT_GROUND_CONSTRAINED"), 0, UUID.Zero);
        Assert.Equal(AGENT_SITTING, Info(r, sp) & AGENT_SITTING);
    }

    // ---------------------------------------------------------------- llSameGroup (Halcyon :7953-7954, :7925-7927)

    [Fact]
    public void AGrouplessPrimIsInTheSameGroupAsNullKey()
    {
        using var r = new ApiCallRig();
        r.H.Prim.GroupID = UUID.Zero;
        Assert.Equal(1, r.Api.llSameGroup(UUID.Zero.ToString()));
    }

    [Fact]
    public void AGrouplessPrimIsInTheSameGroupAsAGrouplessObject()
    {
        using var r = new ApiCallRig();
        r.H.Prim.GroupID = UUID.Zero;
        SceneObjectGroup other = OpenSim.Tests.Common.SceneHelpers.AddSceneObject(r.H.Scene, 1, UUID.Random(), "other", 0x20);
        Assert.Equal(1, r.Api.llSameGroup(other.RootPart.UUID.ToString()));
    }

    [Fact]
    public void AChildAgentIsNeverInTheSameGroup()
    {
        using var r = new ApiCallRig();
        UUID group = UUID.Random();
        r.H.Prim.GroupID = group;
        ScenePresence sp = r.AddAvatar();
        sp.ControllingClient = Fake<IClientAPI>.Create((m, a) => m.Name == "get_ActiveGroupId" ? group : (object)null);
        Assert.Equal(1, r.Api.llSameGroup(sp.UUID.ToString()));
        sp.IsChildAgent = true;
        Assert.Equal(0, r.Api.llSameGroup(sp.UUID.ToString()));
    }

    // ---------------------------------------------------------------- llSetCameraParams (Halcyon :13617-13626)

    [Fact]
    public void VectorCameraRulesReachTheViewerAsThreeFloats()
    {
        using var r = new ApiCallRig();
        ScenePresence sp = r.AddAvatar();
        SortedDictionary<int, float> sent = null;
        sp.ControllingClient = Fake<IClientAPI>.Create((m, a) =>
        {
            if (m.Name == nameof(IClientAPI.SendSetFollowCamProperties)) sent = (SortedDictionary<int, float>)a[1];
            return null;
        });
        r.Grant(sp.UUID, 0x800);   // PERMISSION_CONTROL_CAMERA
        // CAMERA_POSITION 13, CAMERA_FOCUS 17, CAMERA_FOCUS_OFFSET 9, CAMERA_DISTANCE 7
        r.Api.llSetCameraParams(ApiCallRig.L(13, new Vector3(1, 2, 3), 17, new Vector3(4, 5, 6),
            9, new Vector3(7, 8, 9), 7, 2.5f));
        Assert.NotNull(sent);
        Assert.Equal(1f, sent[14]); Assert.Equal(2f, sent[15]); Assert.Equal(3f, sent[16]);
        Assert.Equal(4f, sent[18]); Assert.Equal(5f, sent[19]); Assert.Equal(6f, sent[20]);
        Assert.Equal(7f, sent[10]); Assert.Equal(8f, sent[11]); Assert.Equal(9f, sent[12]);
        Assert.Equal(2.5f, sent[7]);
    }

    // ---------------------------------------------------------------- llGetRegionFlags (Halcyon :13722-13725)

    [Fact]
    public void RegionFlagsComeFromTheEstateModule()
    {
        using var r = new ApiCallRig();
        const uint fixedSunAndTerraform = 0x10 | 0x40;
        r.H.Scene.RegisterModuleInterface<IEstateModule>(Fake<IEstateModule>.Create((m, a) =>
            m.Name == nameof(IEstateModule.GetRegionFlags) ? fixedSunAndTerraform : (object)null));
        Assert.Equal((int)fixedSunAndTerraform, r.Api.llGetRegionFlags());
    }

    [Fact]
    public void WithoutAnEstateModuleLandResellDoesNotSetAllowSetHome()
    {
        using var r = new ApiCallRig();
        Assert.Null(r.H.Scene.RequestModuleInterface<IEstateModule>());
        r.H.Scene.RegionInfo.RegionSettings.AllowLandResell = true;
        Assert.Equal(0, r.Api.llGetRegionFlags() & 0x4);
    }
}
