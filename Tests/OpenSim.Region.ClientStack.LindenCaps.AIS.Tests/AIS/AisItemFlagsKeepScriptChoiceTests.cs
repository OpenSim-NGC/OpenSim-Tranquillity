/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Xunit;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenCaps.AIS;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS.Tests;

/// <summary>
/// An AIS item PATCH carries the flags the viewer was told, which never include the region's own script bits (the
/// "LSL on Lua" bit and the saved engine, <see cref="ScriptCompileTargets.ForViewer"/>). Applying it keeps those bits
/// as the item has them and takes every other bit from the viewer, as the UDP item update keeps all but the low byte.
/// So editing a script's properties in inventory neither wipes nor sets an engine choice.
/// </summary>
public class AisItemFlagsKeepScriptChoiceTests
{
    private static InventoryItemBase Script(uint flags) => new()
    {
        ID = UUID.Random(),
        Name = "script",
        AssetType = (int)AssetType.LSLText,
        InvType = (int)InventoryType.LSL,
        Flags = flags,
    };

    private static readonly uint Chosen = ScriptEngineChoices.WithChoice(ScriptCompileTargets.LslOnLuauFlag, ScriptEngineChoices.Phlox);

    [Fact]
    public void APatchEchoingTheFlagsTheViewerWasToldKeepsTheChoice()
    {
        InventoryItemBase item = Script(Chosen | 0x100u);
        uint told = ScriptCompileTargets.ForViewer(item.Flags);

        AisMutation.ApplyToItem(new OSDMap { ["flags"] = (int)told, ["desc"] = "new description" }, item);

        Assert.Equal(Chosen | 0x100u, item.Flags);
        Assert.Equal(ScriptEngineChoices.Phlox, ScriptEngineChoices.FromFlags(item.Flags));
    }

    [Fact]
    public void APatchChangesTheOtherBits()
    {
        InventoryItemBase item = Script(Chosen | 0x100u);

        AisMutation.ApplyToItem(new OSDMap { ["flags"] = 0x40000000 }, item);

        Assert.Equal(Chosen | 0x40000000u, item.Flags);
    }

    [Fact]
    public void APatchCannotSetTheRegionsBits()
    {
        InventoryItemBase item = Script(0u);

        AisMutation.ApplyToItem(new OSDMap { ["flags"] = (int)ScriptEngineChoices.WithChoice(ScriptCompileTargets.LslOnLuauFlag, ScriptEngineChoices.YEngine) }, item);

        Assert.Equal(0u, item.Flags);
    }
}
