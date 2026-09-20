using System;
using System.Linq;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenCaps.AIS;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS.Tests;

/// <summary>
/// The A7 live failure: an agent with two Current Outfit folders, where the inventory service resolves
/// <c>"current"</c> to the wrong one and a take-off is written into a folder no viewer reads
/// (Docs/feature/ais-v3/A7-DUPLICATE-COF.md).
///
/// <para>The existing suite could not catch this because every fixture gives its fake agent exactly **one** folder
/// per type. `FakeAisBackend.GetFolderForType` then answers correctly no matter what rule it uses, so the tests
/// agree with the service on an input where the service cannot be wrong. The bug only exists on the input nobody
/// constructed: two folders of the same type. It is a resolution bug, not a route bug, and the 121 route tests
/// resolve nothing.</para>
/// </summary>
[TestFixture]
public class AisDuplicateSystemFolderTests
{
    private static readonly UUID Agent = new("a7d2ff2e-dc32-44d8-aa61-3d22070a4964");

    // The live ids, so the test reads as the incident it encodes.
    private static readonly UUID RealCof = new("71c3c184-410b-4dae-b20a-855741cf1faf");   // version 457, the one the viewer uses
    private static readonly UUID DupeCof = new("52c327c4-cb7d-4365-a7f0-62a6f7545265");   // version 1, the one we returned

    /// <summary>
    /// Truly Bazar's inventory as it actually is: a root, two type-46 folders, and a backend whose
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

    [Test]
    public void current_resolves_to_the_folder_the_viewer_uses_not_the_one_the_service_returns()
    {
        var backend = TwoCurrentOutfits();

        var cof = AisInventory.GetCurrentOutfit(backend, Agent);

        Assert.That(cof, Is.Not.Null);
        Assert.That(cof.ID, Is.EqualTo(RealCof),
            "resolved to the version-1 duplicate. A slam against it is written to a folder no viewer reads, so the "
            + "outfit change silently does not stick (A7 live failure, checklist step 10).");
        Assert.That(cof.Version, Is.EqualTo(457));
    }

    [Test]
    public void the_backends_own_answer_is_the_wrong_one_so_the_fixture_really_does_reproduce_the_bug()
    {
        var backend = TwoCurrentOutfits();

        // Guards the test itself: if the fake ever stopped returning the duplicate, the test above would pass
        // for the wrong reason.
        Assert.That(backend.GetFolderForType(Agent, FolderType.CurrentOutfit).ID, Is.EqualTo(DupeCof));
    }

    [Test]
    public void a_warning_names_every_candidate_its_version_and_the_one_chosen()
    {
        var backend = TwoCurrentOutfits();

        using var log = new CapturedLog();
        AisInventory.GetCurrentOutfit(backend, Agent);

        var warning = log.Warnings.SingleOrDefault(w => w.Contains("folders of type"));
        Assert.That(warning, Is.Not.Null, "an operator must see this without running a DB query");
        Assert.That(warning, Does.Contain(Agent.ToString()), "names the agent");
        Assert.That(warning, Does.Contain(RealCof.ToString()), "names the kept folder");
        Assert.That(warning, Does.Contain(DupeCof.ToString()), "names the duplicate");
        Assert.That(warning, Does.Contain("v457").And.Contain("v1"), "names both versions");
        Assert.That(warning, Does.Contain("CurrentOutfit"), "names the type");
    }

    [Test]
    public void one_folder_of_a_type_emits_no_warning()
    {
        var backend = new FakeAisBackend(Agent);
        var root = UUID.Random();
        backend.AddFolder(root, UUID.Zero, "My Inventory", 1, (short)FolderType.Root);
        backend.AddFolder(RealCof, root, "Current Outfit", 457, (short)FolderType.CurrentOutfit);

        using var log = new CapturedLog();
        var cof = AisInventory.GetCurrentOutfit(backend, Agent);

        Assert.That(cof.ID, Is.EqualTo(RealCof));
        Assert.That(log.Warnings.Any(w => w.Contains("folders of type")), Is.False,
            "the normal case must stay quiet or the warning is noise");
    }

    /// <summary>
    /// Two folders with no usage history cannot be told apart by version, so the only requirement is that the
    /// answer is stable — the same one on every call and on every region, rather than whatever the database
    /// happened to return first.
    /// </summary>
    [Test]
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

            Assert.That(AisInventory.GetCurrentOutfit(backend, Agent).ID, Is.EqualTo(lower),
                $"tie-break must not depend on enumeration order (higher inserted first: {insertHigherFirst})");
        }
    }

    /// <summary>The same coin flip applies to every system type, so the resolution is general, not COF-specific.</summary>
    [TestCase(FolderType.Trash)]
    [TestCase(FolderType.Clothing)]
    [TestCase(FolderType.Object)]
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

        Assert.That(folder.ID, Is.EqualTo(live), $"{type} must resolve by the same rule as CurrentOutfit");
    }

    /// <summary>
    /// With no skeleton to work from, the backend's own answer stands — the fix must not turn a resolvable
    /// folder into a null for a backend that has no skeleton (the library backend is one).
    /// </summary>
    [Test]
    public void an_empty_skeleton_falls_back_to_the_backend()
    {
        var backend = new FakeAisBackend(Agent);
        var cof = AisInventory.GetCurrentOutfit(backend, Agent);

        Assert.That(cof, Is.Null, "no folders at all means no Current Outfit, not a crash");
    }

    [Test]
    public void a_type_absent_from_the_skeleton_falls_back_to_the_backend()
    {
        var backend = new FakeAisBackend(Agent);
        var root = UUID.Random();
        backend.AddFolder(root, UUID.Zero, "My Inventory", 1, (short)FolderType.Root);

        Assert.That(AisInventory.GetCurrentOutfit(backend, Agent), Is.Null);
    }
}
