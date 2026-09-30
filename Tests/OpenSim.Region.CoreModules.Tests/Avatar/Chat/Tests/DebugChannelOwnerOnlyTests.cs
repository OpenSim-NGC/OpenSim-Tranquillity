using System.Collections.Generic;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Avatar.Chat.Tests;

/// <summary>
/// PHLOX-9b. Object chat on DEBUG_CHANNEL reaches the object's owner and nobody else. The SL wiki
/// (https://wiki.secondlife.com/wiki/DEBUG_CHANNEL) says the sim broadcasts and "most viewers filter out
/// messages received on DEBUG_CHANNEL from objects owned by others"; not every viewer does, and a
/// second avatar can get the script-warning box for another owner's prim. The sim
/// filters now, so the outcome does not depend on the viewer. Channel 0 from the same part still reaches
/// everyone in range, and an avatar typing on DEBUG_CHANNEL is untouched.
/// </summary>
public class DebugChannelOwnerOnlyTests : OpenSimTestCase
{
    private const int DEBUG_CHANNEL = 0x7FFFFFFF;

    private sealed class Rig
    {
        public TestScene Scene = null!;
        public ScenePresence Owner = null!, Other = null!;
        public SceneObjectPart Part = null!;
        public List<string> OwnerHeard = new(), OtherHeard = new();
    }

    private static Rig Build()
    {
        var r = new Rig();
        r.Scene = new SceneHelpers().SetupScene();
        IConfigSource config = new IniConfigSource();
        config.AddConfig("Chat");
        SceneHelpers.SetupSceneModules(r.Scene, config, new ChatModule());

        UUID ownerId = TestHelpers.ParseTail(0x11), otherId = TestHelpers.ParseTail(0x22);
        r.Owner = SceneHelpers.AddScenePresence(r.Scene, ownerId);
        r.Other = SceneHelpers.AddScenePresence(r.Scene, otherId);
        r.Owner.AbsolutePosition = new Vector3(128, 128, 25);
        r.Other.AbsolutePosition = new Vector3(130, 128, 25);
        r.Part = SceneHelpers.AddSceneObject(r.Scene, "error prim", ownerId).RootPart;
        r.Part.ParentGroup.AbsolutePosition = new Vector3(129, 128, 25);

        ((TestClient)r.Owner.ControllingClient).OnReceivedChatMessage +=
            (msg, type, pos, name, from, owner, src, aud) => r.OwnerHeard.Add($"{type}:{msg}");
        ((TestClient)r.Other.ControllingClient).OnReceivedChatMessage +=
            (msg, type, pos, name, from, owner, src, aud) => r.OtherHeard.Add($"{type}:{msg}");
        return r;
    }

    private static void ObjectSays(Rig r, int channel, string text)
        => r.Scene.SimChat(text, ChatTypeEnum.Shout, channel, r.Part.AbsolutePosition, r.Part.Name, r.Part.UUID, false);

    [Fact]
    public void DebugChannelFromAnObjectReachesExactlyTheOwner()
    {
        var r = Build();
        ObjectSays(r, DEBUG_CHANNEL, "Script error: Script stopped: Attempted to divide by zero.");

        Assert.Single(r.OwnerHeard);
        Assert.Contains("Script error", r.OwnerHeard[0]);
        Assert.True(r.OtherHeard.Count == 0, "the other avatar heard: " + string.Join(" | ", r.OtherHeard));
    }

    [Fact]
    public void ChannelZeroFromTheSamePartReachesBoth()
    {
        var r = Build();
        ObjectSays(r, 0, "hello");

        Assert.Single(r.OwnerHeard);
        Assert.Single(r.OtherHeard);
    }

    [Fact]
    public void DebugChannelFromAnAvatarIsUntouched()
    {
        var r = Build();
        // the other avatar "types" /2147483647 hi - avatar-sourced, not object-sourced
        r.Scene.SimChat("hi", ChatTypeEnum.Say, DEBUG_CHANNEL, r.Other.AbsolutePosition, r.Other.Name, r.Other.UUID, true);

        Assert.Single(r.OwnerHeard);
        Assert.Single(r.OtherHeard);
    }
}
