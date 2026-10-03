/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using OpenMetaverse;
using OpenSim.Framework;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The loader takes loads and unloads in the order they were posted (Halcyon kept one list), and compiles only an asset
/// that is script text (Halcyon ScriptLoader.AssetReceived: "Expected LSLText").
/// </summary>
// Runs in parallel: each test has its own harness and items.
public class LoaderOrderAndAssetTypeTests
{
    private const string Src = "default { state_entry() { llSay(0, \"started\"); } }";

    /// <summary>
    /// A load posted before an unload of the same item is done first, and the unload then removes the script. All unloads
    /// used to go before all loads, so the unload found nothing and the load then started a script that was being removed.
    /// </summary>
    [Fact]
    public void ALoadPostedBeforeAnUnloadIsUndoneByIt()
    {
        using var h = new SchedulerHarness();
        var item = UUID.Random();
        OpenSim.Tests.Common.TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, item, UUID.Random(), "s", Src);
        SavedStateRig.PostRez(h, h.Prim, item, Src, 0, false, 0);
        SavedStateRig.PostRemove(h, h.Prim, item);

        h.PumpUntilIdle(TimeSpan.FromSeconds(10));
        h.PumpFor(TimeSpan.FromSeconds(1));   // proves nothing starts late
        Assert.False(SavedStateRig.Exe(h).IsLoaded(item), "the script was left running after its unload");
        Assert.DoesNotContain("started", h.Said);
    }

    /// <summary>An asset fetched for a script that is not script text is not compiled, and the script does not start.</summary>
    [Fact]
    public void AnAssetThatIsNotScriptTextDoesNotStart()
    {
        using var h = new SchedulerHarness();
        var item = UUID.Random(); var textureId = UUID.Random();
        var taskItem = OpenSim.Tests.Common.TaskInventoryHelpers.AddScript(h.Scene.AssetService, h.Prim, item, UUID.Random(), "s", Src);
        // The script item points at a texture that holds the script's bytes.
        var texture = new AssetBase(textureId, "not a script", (sbyte)AssetType.Texture, UUID.Zero.ToString())
        {
            Data = System.Text.Encoding.UTF8.GetBytes(Src)
        };
        h.Scene.AssetService.Store(texture);
        taskItem.AssetID = textureId;

        SavedStateRig.PostRez(h, h.Prim, item, string.Empty, 0, false, 0);   // no text: the loader fetches the asset
        h.PumpFor(TimeSpan.FromSeconds(2));   // proves it does not start
        Assert.DoesNotContain("started", h.Said);
        Assert.False(SavedStateRig.Exe(h).IsLoaded(item));
    }
}
