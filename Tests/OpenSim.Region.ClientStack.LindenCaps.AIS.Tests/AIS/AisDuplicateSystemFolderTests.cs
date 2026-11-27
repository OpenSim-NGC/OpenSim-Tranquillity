/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using Xunit;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenCaps.AIS;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS.Tests;

/// <summary>
/// The live failure this resolution rule exists for: an agent with two Current Outfit folders, where the
/// inventory service resolves <c>"current"</c> to the wrong one and a take-off is written into a folder no
/// viewer reads.
///
/// <para>The existing suite could not catch this because every fixture gives its fake agent exactly **one** folder
/// per type. `FakeAisBackend.GetFolderForType` then answers correctly no matter what rule it uses, so the tests
/// agree with the service on an input where the service cannot be wrong. The bug only exists on the input nobody
/// constructed: two folders of the same type. It is a resolution bug, not a route bug, and the 121 route tests
/// resolve nothing.</para>
/// </summary>
public class AisDuplicateSystemFolderTests
{
    private static readonly UUID Agent = new("a7d2ff2e-dc32-44d8-aa61-3d22070a4964");

    /// <summary>The warning is written once per agent for the life of the process, so each test starts with none written.</summary>
    public AisDuplicateSystemFolderTests() => AisInventory.ResetDuplicateWarnings();

    // The live ids, so the test reads as the incident it encodes.
    private static readonly UUID RealCof = new("71c3c184-410b-4dae-b20a-855741cf1faf");   // version 457, the one the viewer uses
    private static readonly UUID DupeCof = new("52c327c4-cb7d-4365-a7f0-62a6f7545265");   // version 1, the one we returned

    /// <summary>
    /// A real account's inventory as it actually was: a root, two type-46 folders, and a backend whose
    /// <c>GetFolderForType</c> returns the version-1 duplicate — which is what the unordered
    /// <c>folders[0]</c> query did on the day.
    /// </summary>
    private static FakeAisBackend TwoCurrentOutfits(int realVersion = 457, int dupeVersion = 1)
    {
        var backend = new FakeAisBackend(Agent);
        var root = UUID.Random();
        backend.AddFolder(root, UUID.Zero, "My Inventory", 1, (short)FolderType.Root);
        backend.AddFolder(DupeCof, root, "Current Outfit", dupeVersion, (short)FolderType.CurrentOutfit);
        backend.AddFolder(RealCof, root, "Current Outfit", realVersion, (short)FolderType.CurrentOutfit);

        // Reproduce the failure exactly: the service hands back the duplicate.
        backend.CurrentOutfitId = DupeCof;
        return backend;
    }

    [Fact]
    public void current_resolves_to_the_folder_the_viewer_uses_not_the_one_the_service_returns()
    {
        var backend = TwoCurrentOutfits();

        var cof = AisInventory.GetCurrentOutfit(backend, Agent);

        Assert.NotNull(cof);
        Assert.Equal(RealCof, cof.ID);
        Assert.Equal(457, cof.Version);
    }

    [Fact]
    public void the_backends_own_answer_is_the_wrong_one_so_the_fixture_really_does_reproduce_the_bug()
    {
        var backend = TwoCurrentOutfits();

        // Guards the test itself: if the fake ever stopped returning the duplicate, the test above would pass
        // for the wrong reason.
        Assert.Equal(DupeCof, backend.GetFolderForType(Agent, FolderType.CurrentOutfit).ID);
    }

    [Fact]
    public void a_warning_names_every_candidate_its_version_and_the_one_chosen()
    {
        var backend = TwoCurrentOutfits();

        using var log = new CapturedLog();
        AisInventory.GetCurrentOutfit(backend, Agent);

        var warning = log.Warnings.SingleOrDefault(w => w.Contains("folders of type"));
        Assert.True(warning is not null, "an operator must see this without running a DB query");
        Assert.Contains(Agent.ToString(), warning);
        Assert.Contains(RealCof.ToString(), warning);
        Assert.Contains(DupeCof.ToString(), warning);
        Assert.Multiple(() => { Assert.Contains("v457", warning); Assert.Contains("v1", warning); });
        Assert.Contains("CurrentOutfit", warning);
    }

    [Fact]
    public void one_folder_of_a_type_emits_no_warning()
    {
        var backend = new FakeAisBackend(Agent);
        var root = UUID.Random();
        backend.AddFolder(root, UUID.Zero, "My Inventory", 1, (short)FolderType.Root);
        backend.AddFolder(RealCof, root, "Current Outfit", 457, (short)FolderType.CurrentOutfit);

        using var log = new CapturedLog();
        var cof = AisInventory.GetCurrentOutfit(backend, Agent);

        Assert.Equal(RealCof, cof.ID);
        Assert.False(log.Warnings.Any(w => w.Contains("folders of type")), "the normal case must stay quiet or the warning is noise");
    }

    /// <summary>
    /// Two folders with no usage history cannot be told apart by version, so the only requirement is that the
    /// answer is stable — the same one on every call and on every region, rather than whatever the database
    /// happened to return first.
    /// </summary>
    [Fact]
    public void a_version_tie_is_broken_deterministically_by_id()
    {
        var lower = new UUID("11111111-1111-4111-8111-111111111111");
        var higher = new UUID("22222222-2222-4222-8222-222222222222");

        foreach (var insertHigherFirst in new[] { true, false })
        {
            var backend = new FakeAisBackend(Agent);
            var root = UUID.Random();
            backend.AddFolder(root, UUID.Zero, "My Inventory", 1, (short)FolderType.Root);
            if (insertHigherFirst)
            {
                backend.AddFolder(higher, root, "Current Outfit", 1, (short)FolderType.CurrentOutfit);
                backend.AddFolder(lower, root, "Current Outfit", 1, (short)FolderType.CurrentOutfit);
            }
            else
            {
                backend.AddFolder(lower, root, "Current Outfit", 1, (short)FolderType.CurrentOutfit);
                backend.AddFolder(higher, root, "Current Outfit", 1, (short)FolderType.CurrentOutfit);
            }
            backend.CurrentOutfitId = higher;

            Assert.Equal(lower, AisInventory.GetCurrentOutfit(backend, Agent).ID);
        }
    }

    /// <summary>The same coin flip applies to every system type, so the resolution is general, not COF-specific.</summary>
    [Theory]
    [InlineData(FolderType.Trash)]
    [InlineData(FolderType.Clothing)]
    [InlineData(FolderType.Object)]
    public void duplicates_of_any_system_type_resolve_by_the_same_rule(FolderType type)
    {
        var backend = new FakeAisBackend(Agent);
        var root = UUID.Random();
        var stale = UUID.Random();
        var live = UUID.Random();
        backend.AddFolder(root, UUID.Zero, "My Inventory", 1, (short)FolderType.Root);
        backend.AddFolder(stale, root, type.ToString(), 1, (short)type);
        backend.AddFolder(live, root, type.ToString(), 92, (short)type);

        var folder = AisInventory.GetSystemFolder(backend, Agent, type);

        Assert.Equal(live, folder.ID);
    }

    /// <summary>
    /// With no skeleton to work from, the backend's own answer stands — the fix must not turn a resolvable
    /// folder into a null for a backend that has no skeleton (the library backend is one).
    /// </summary>
    [Fact]
    public void an_empty_skeleton_falls_back_to_the_backend()
    {
        var backend = new FakeAisBackend(Agent);
        var cof = AisInventory.GetCurrentOutfit(backend, Agent);

        Assert.True(cof is null, "no folders at all means no Current Outfit, not a crash");
    }

    [Fact]
    public void a_type_absent_from_the_skeleton_falls_back_to_the_backend()
    {
        var backend = new FakeAisBackend(Agent);
        var root = UUID.Random();
        backend.AddFolder(root, UUID.Zero, "My Inventory", 1, (short)FolderType.Root);

        Assert.Null(AisInventory.GetCurrentOutfit(backend, Agent));
    }
}
