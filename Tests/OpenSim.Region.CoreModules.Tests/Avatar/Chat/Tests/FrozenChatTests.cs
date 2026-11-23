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

using System.Collections.Generic;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Avatar.Chat.Tests;

/// <summary>
/// While an avatar is in the chat module's freeze list, its chat is not delivered and it is told
/// "You may not talk as you are frozen."; its typing start and stop are not delivered either, and say nothing.
/// </summary>
public class FrozenChatTests : OpenSimTestCase
{
    private const string FrozenAlert = "You may not talk as you are frozen.";

    private sealed class Rig
    {
        public ChatModule Chat = null!;
        public ScenePresence Frozen = null!, Other = null!;
        public TestClient FrozenClient = null!;
        public List<string> Alerts = new(), OtherHeard = new();
    }

    private static Rig Build()
    {
        var r = new Rig();
        TestScene scene = new SceneHelpers().SetupScene();
        IConfigSource config = new IniConfigSource();
        config.AddConfig("Chat");
        r.Chat = new ChatModule();
        SceneHelpers.SetupSceneModules(scene, config, r.Chat);

        r.Frozen = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x11));
        r.Other = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x22));
        r.Frozen.AbsolutePosition = new Vector3(128, 128, 25);
        r.Other.AbsolutePosition = new Vector3(130, 128, 25);

        r.FrozenClient = (TestClient)r.Frozen.ControllingClient;
        r.FrozenClient.OnReceivedAgentAlertMessage += (msg, modal) => r.Alerts.Add(msg);
        ((TestClient)r.Other.ControllingClient).OnReceivedChatMessage +=
            (msg, type, pos, name, from, owner, src, aud) => r.OtherHeard.Add($"{(ChatTypeEnum)type}:{msg}");
        return r;
    }

    private static void Freeze(Rig r, bool frozen)
        => r.Chat.ParcelFreezeUser(r.Other.ControllingClient, r.Other.UUID, frozen ? 0u : 1u, r.Frozen.UUID);

    [Fact]
    public void TypingWhileFrozenSaysNothing()
    {
        var r = Build();
        Freeze(r, true);
        try
        {
            r.FrozenClient.Chat(0, ChatTypeEnum.StartTyping, "");
            r.FrozenClient.Chat(0, ChatTypeEnum.StopTyping, "");

            Assert.True(r.Alerts.Count == 0, "alerts: " + string.Join(" | ", r.Alerts));
            Assert.Empty(r.OtherHeard);
        }
        finally
        {
            Freeze(r, false);
        }
    }

    [Fact]
    public void ChatWhileFrozenIsRefusedWithOneAlert()
    {
        var r = Build();
        Freeze(r, true);
        try
        {
            r.FrozenClient.Chat(0, ChatTypeEnum.StartTyping, "");
            r.FrozenClient.Chat(0, ChatTypeEnum.Say, "hello");
            r.FrozenClient.Chat(0, ChatTypeEnum.StopTyping, "");

            Assert.Equal(new[] { FrozenAlert }, r.Alerts);
            Assert.Empty(r.OtherHeard);
        }
        finally
        {
            Freeze(r, false);
        }
    }

    [Fact]
    public void ChatAfterUnfreezingIsDelivered()
    {
        var r = Build();
        Freeze(r, true);
        Freeze(r, false);

        r.FrozenClient.Chat(0, ChatTypeEnum.Say, "hello");

        Assert.Empty(r.Alerts);
        Assert.Equal(new[] { "Say:hello" }, r.OtherHeard);
    }
}
