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
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// The TestClient helpers for script permission questions: SendScriptQuestion is recorded, and FireScriptAnswer
/// answers through OnScriptAnswer the way the viewer's ScriptAnswerYes packet does.
/// </summary>
public class TestClientScriptQuestionTests : OpenSimTestCase
{
    [Fact]
    public void AQuestionIsRecordedAndAnAnswerIsRaised()
    {
        Scene scene = new SceneHelpers().SetupScene();
        ScenePresence sp = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x1));
        TestClient client = (TestClient)sp.ControllingClient;

        UUID task = UUID.Random();
        UUID item = UUID.Random();
        client.SendScriptQuestion(task, "Object", "Owner", item, 0x10, UUID.Zero);

        Assert.Equal((task, item, 0x10), Assert.Single(client.ScriptQuestions));

        (IClientAPI who, UUID task, UUID item, int answer)? got = null;
        client.OnScriptAnswer += (c, t, i, a) => got = (c, t, i, a);
        client.FireScriptAnswer(task, item, 0x10);

        Assert.NotNull(got);
        Assert.Same(client, got.Value.who);
        Assert.Equal(task, got.Value.task);
        Assert.Equal(item, got.Value.item);
        Assert.Equal(0x10, got.Value.answer);
    }
}
