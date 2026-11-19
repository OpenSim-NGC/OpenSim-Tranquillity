/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using NUnit.Framework;
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
[TestFixture]
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

    [Test]
    public void APatchEchoingTheFlagsTheViewerWasToldKeepsTheChoice()
    {
        InventoryItemBase item = Script(Chosen | 0x100u);
        uint told = ScriptCompileTargets.ForViewer(item.Flags);

        AisMutation.ApplyToItem(new OSDMap { ["flags"] = (int)told, ["desc"] = "new description" }, item);

        Assert.That(item.Flags, Is.EqualTo(Chosen | 0x100u));
        Assert.That(ScriptEngineChoices.FromFlags(item.Flags), Is.EqualTo(ScriptEngineChoices.Phlox));
    }

    [Test]
    public void APatchChangesTheOtherBits()
    {
        InventoryItemBase item = Script(Chosen | 0x100u);

        AisMutation.ApplyToItem(new OSDMap { ["flags"] = 0x40000000 }, item);

        Assert.That(item.Flags, Is.EqualTo(Chosen | 0x40000000u));
    }

    [Test]
    public void APatchCannotSetTheRegionsBits()
    {
        InventoryItemBase item = Script(0u);

        AisMutation.ApplyToItem(new OSDMap { ["flags"] = (int)ScriptEngineChoices.WithChoice(ScriptCompileTargets.LslOnLuauFlag, ScriptEngineChoices.YEngine) }, item);

        Assert.That(item.Flags, Is.EqualTo(0u));
    }
}
