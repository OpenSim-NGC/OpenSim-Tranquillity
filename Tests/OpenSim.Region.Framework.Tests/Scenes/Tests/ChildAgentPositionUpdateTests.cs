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
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// A child agent's position update (PUT /agent, AgentPosition) is open to any caller: the region a Hypergrid visitor
/// comes from sends its agent update through the same PUT, so it is not limited to the trusted hosts. Its session was not checked, and anyone who knew an
/// avatar's and a region's ids (both public) could set the agent's position, draw distance, throttles and seed
/// capabilities in that region.
/// </summary>
public class ChildAgentPositionUpdateTests : OpenSimTestCase
{
    private static AgentPosition Update(ScenePresence sp, UUID session, float far) => new()
    {
        AgentID = sp.UUID,
        SessionID = session,
        RegionHandle = sp.Scene.RegionInfo.RegionHandle,
        Position = new Vector3(10, 20, 30),
        Center = new Vector3(10, 20, 32),
        Far = far,
        Size = new Vector3(0.45f, 0.6f, 1.9f),
    };

    [Fact]
    public void AnUpdateWithAnotherSessionIsIgnored()
    {
        TestHelpers.InMethod();
        Scene scene = new SceneHelpers().SetupScene();
        ScenePresence sp = SceneHelpers.AddChildScenePresence(scene, TestHelpers.ParseTail(0x1));
        float far = sp.DrawDistance;
        Vector3 at = sp.AbsolutePosition;

        Assert.False(scene.IncomingUpdateChildAgent(Update(sp, UUID.Random(), 200)));
        Assert.False(scene.IncomingUpdateChildAgent(Update(sp, UUID.Zero, 200)));

        Assert.Equal(far, sp.DrawDistance);
        Assert.Equal(at, sp.AbsolutePosition);
    }

    [Fact]
    public void AnUpdateWithTheAgentsSessionIsApplied()
    {
        TestHelpers.InMethod();
        Scene scene = new SceneHelpers().SetupScene();
        ScenePresence sp = SceneHelpers.AddChildScenePresence(scene, TestHelpers.ParseTail(0x2));
        float far = sp.DrawDistance == 64 ? 128 : 64;

        Assert.True(scene.IncomingUpdateChildAgent(Update(sp, sp.ControllingClient.SessionId, far)));

        Assert.Equal(far, sp.DrawDistance);
        Assert.Equal(new Vector3(10, 20, 30), sp.AbsolutePosition);
    }
}
