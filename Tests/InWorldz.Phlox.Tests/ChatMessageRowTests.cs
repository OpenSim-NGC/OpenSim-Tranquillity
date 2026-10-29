/*
 * Phlox Script Engine tests
 * Copyright (c) Legion Builds
 */

using System;
using System.Linq;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// An object's instant message is kept for a recipient who is offline and carries the object's location, as Halcyon
/// (LSLSystemAPI.cs:3910-3916) and YEngine send it; SL (llInstantMessage): "If the specified user is not signed in, the
/// messages will be delivered to their email just like a regular instant message". And empty chat reaches Phlox
/// listeners, as it reaches Halcyon's and YEngine's (WorldCommModule.DeliverMessage has no empty-message guard).
/// </summary>
// No test reaches a network service: the IM transfer module is an in-memory recorder.
// Test grouping: no process-wide state, so the class runs in parallel.
public class ChatMessageRowTests
{
    private static readonly UUID To = new UUID("5a5a5a5a-0000-4000-8000-00000000b2b2");

    [Fact]
    public void AnObjectInstantMessageIsStoredForAnOfflineRecipientAndCarriesItsLocation()
    {
        using var h = new SchedulerHarness();
        h.Scene.RegisterModuleInterface<IMessageTransferModule>(RecordingIms.Create(out var ims));
        h.Prim.ParentGroup.AbsolutePosition = new Vector3(12.7f, 34.2f, 56.9f);
        h.RezScript("default { state_entry() { llInstantMessage(\"" + To + "\", \"hi\"); } }");
        Assert.True(h.PumpUntil(() => !ims.Sent.IsEmpty), "no instant message was sent");
        Assert.True(ims.Sent.TryPeek(out GridInstantMessage im));

        Assert.Equal(1, im.offline);
        string bucket = System.Text.Encoding.UTF8.GetString(im.binaryBucket).TrimEnd('\0');
        Assert.Equal(h.Scene.RegionInfo.RegionName + "/12/34/56", bucket);
    }

    [Fact]
    public void EmptyChatReachesAPhloxListener()
    {
        using var h = new SchedulerHarness();
        var listener = SceneHelpers.AddSceneObject(h.Scene, "listener", UUID.Random());
        listener.AbsolutePosition = h.Prim.ParentGroup.AbsolutePosition + new Vector3(1, 0, 0);
        h.RezScriptInto(listener.RootPart,
            "default { state_entry() { llListen(5, \"\", NULL_KEY, \"\"); llSay(99, \"ready\"); } " +
            "listen(integer c, string n, key k, string m) { llSay(99, \"heard [\" + m + \"]\"); } }");
        Assert.True(h.PumpUntil(() => h.Said.Contains("ready")), "the listener never started");

        h.RezScript("default { state_entry() { llSay(5, \"\"); } }");
        Assert.True(h.PumpUntil(() => h.Said.Contains("heard []")), "the empty message was not heard: " + string.Join(" | ", h.Said));
    }
}
