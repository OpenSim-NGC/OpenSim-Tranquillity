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

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Scripting.WorldComm;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Scripting.WorldComm.Tests;

/// <summary>
/// IWorldComm.OnMessageDelivered: raised after WorldCommModule offers a message to its own listens, so a second
/// script engine on the region (which keeps its own listens) can offer it to those.
/// </summary>
public class WorldCommMessageDeliveredTests : OpenSimTestCase
{
    private static (Scene scene, WorldCommModule comm) Setup()
    {
        Scene scene = new SceneHelpers().SetupScene();
        WorldCommModule comm = new WorldCommModule();
        SceneHelpers.SetupSceneModules(scene, new IniConfigSource(), comm);
        return (scene, comm);
    }

    [Fact]
    public void RegionSayIsRaisedWithTheMessage()
    {
        (Scene scene, WorldCommModule comm) = Setup();
        Assert.Same(comm, scene.RequestModuleInterface<IWorldComm>());

        List<OSChatMessage> seen = new();
        comm.OnMessageDelivered += seen.Add;

        UUID sender = UUID.Random();
        comm.DeliverMessage(ChatTypeEnum.Region, 42, "Sender", sender, "hello");

        OSChatMessage m = Assert.Single(seen);
        Assert.Equal(ChatTypeEnum.Region, m.Type);
        Assert.Equal(42, m.Channel);
        Assert.Equal("Sender", m.From);
        Assert.Equal(sender, m.SenderUUID);
        Assert.Equal("hello", m.Message);
        Assert.Same(scene, m.Scene);
    }

    [Fact]
    public void SayFromAPositionCarriesThePosition()
    {
        (_, WorldCommModule comm) = Setup();
        List<OSChatMessage> seen = new();
        comm.OnMessageDelivered += seen.Add;

        Vector3 pos = new Vector3(10, 20, 30);
        comm.DeliverMessage(ChatTypeEnum.Say, 7, "Sender", UUID.Random(), "near", pos);

        OSChatMessage m = Assert.Single(seen);
        Assert.Equal(ChatTypeEnum.Say, m.Type);
        Assert.Equal(pos, m.Position);
    }

    [Fact]
    public void MessageToAPrimIsRaisedAsDirectWithTheTarget()
    {
        (Scene scene, WorldCommModule comm) = Setup();
        SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
        List<OSChatMessage> seen = new();
        comm.OnMessageDelivered += seen.Add;

        comm.DeliverMessageTo(so.RootPart.UUID, 5, Vector3.Zero, "Sender", UUID.Random(), "to you");

        OSChatMessage m = Assert.Single(seen);
        Assert.Equal(ChatTypeEnum.Direct, m.Type);
        Assert.Equal(so.RootPart.UUID, m.Destination);
        Assert.Equal("to you", m.Message);
    }

    [Fact]
    public void AFailingHandlerDoesNotStopTheOthers()
    {
        (_, WorldCommModule comm) = Setup();
        int reached = 0;
        comm.OnMessageDelivered += _ => throw new InvalidOperationException("handler fails");
        comm.OnMessageDelivered += _ => reached++;

        comm.DeliverMessage(ChatTypeEnum.Region, 1, "Sender", UUID.Random(), "x");

        Assert.Equal(1, reached);
    }

    [Fact]
    public void DebugChannelToAPrimIsNotRaised()
    {
        (Scene scene, WorldCommModule comm) = Setup();
        SceneObjectGroup so = SceneHelpers.AddSceneObject(scene);
        int raised = 0;
        comm.OnMessageDelivered += _ => raised++;

        comm.DeliverMessageTo(so.RootPart.UUID, 0x7fffffff, Vector3.Zero, "Sender", UUID.Random(), "debug");

        Assert.Equal(0, raised);
    }
}
