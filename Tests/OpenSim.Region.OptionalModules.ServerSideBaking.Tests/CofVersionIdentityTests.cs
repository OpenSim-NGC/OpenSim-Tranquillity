using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenCaps.AIS;
using OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;
using OpenSim.Services.Interfaces;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// S3 Part 1 — the question AIS going live forced: is the <c>cof_version</c> the viewer sends to
/// <c>UpdateAvatarAppearance</c> the same number AIS reports as the Current Outfit folder's version?
///
/// <para>
/// It is, and this proves it rather than asserting it. Both sides read <see cref="InventoryFolderBase.Version"/>
/// of the same folder from the same <see cref="IInventoryService"/>: AIS through
/// <c>AisMutation.ReportVersion</c> (which the viewer stores as its <c>cof_version</c> and posts back) and
/// <c>AisEnvelope.Category</c>'s <c>version</c>; the bake through
/// <see cref="ServerSideBakingModule.CofVersionOf(IInventoryService, UUID)"/>. One field, one writer — the data
/// layer's folder-version bump.
/// </para>
/// </summary>
public class CofVersionIdentityTests
{
    private readonly ITestOutputHelper _out;
    public CofVersionIdentityTests(ITestOutputHelper output) { _out = output; }

    private static readonly UUID Agent = new("a7d2ff2e-dc32-44d8-aa61-3d22070a4964");
    private static readonly UUID CofId = new("11111111-2222-3333-4444-555555555555");

    /// <summary>Just enough inventory service to hand back one Current Outfit folder at a chosen version.</summary>
    private sealed class OneFolderInventory : IInventoryService
    {
        private readonly InventoryFolderBase m_cof;
        public int GetFolderForTypeCalls;

        public OneFolderInventory(int version)
        {
            m_cof = new InventoryFolderBase(CofId, "Current Outfit", Agent, (short)FolderType.CurrentOutfit, UUID.Random(), (ushort)version);
        }

        public InventoryFolderBase Folder => m_cof;

        public InventoryFolderBase GetFolderForType(UUID userID, FolderType type)
        {
            GetFolderForTypeCalls++;
            return type == FolderType.CurrentOutfit && userID == Agent ? m_cof : null;
        }

        public InventoryFolderBase GetFolder(UUID userID, UUID folderID) => folderID == CofId ? m_cof : null;

        // nothing else is exercised
        public bool CreateUserInventory(UUID user) => false;
        public List<InventoryFolderBase> GetInventorySkeleton(UUID userId) => new();
        public InventoryFolderBase GetRootFolder(UUID userID) => null;
        public InventoryCollection GetFolderContent(UUID userID, UUID folderID) => null;
        public InventoryCollection[] GetMultipleFoldersContent(UUID principalID, UUID[] folderIDs) => Array.Empty<InventoryCollection>();
        public List<InventoryItemBase> GetFolderItems(UUID userID, UUID folderID) => new();
        public bool AddFolder(InventoryFolderBase folder) => false;
        public bool UpdateFolder(InventoryFolderBase folder) => false;
        public bool MoveFolder(InventoryFolderBase folder) => false;
        public bool DeleteFolders(UUID userID, List<UUID> folderIDs) => false;
        public bool DeleteFolders(UUID userID, List<UUID> folderIDs, bool onlyIfTrash) => false;
        public bool PurgeFolder(InventoryFolderBase folder) => false;
        public bool AddItem(InventoryItemBase item) => false;
        public bool UpdateItem(InventoryItemBase item) => false;
        public bool MoveItems(UUID ownerID, List<InventoryItemBase> items) => false;
        public bool DeleteItems(UUID userID, List<UUID> itemIDs) => false;
        public InventoryItemBase GetItem(UUID userID, UUID itemID) => null;
        public InventoryItemBase[] GetMultipleItems(UUID userID, UUID[] ids) => Array.Empty<InventoryItemBase>();
        public List<InventoryItemBase> GetActiveGestures(UUID userId) => new();
        public int GetAssetPermissions(UUID userID, UUID assetID) => 0;
        public bool HasInventoryForUser(UUID userID) => true;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4242)]
    [InlineData(65535)]
    public void TheBakeReadsTheSameNumberAisReports(int version)
    {
        var inventory = new OneFolderInventory(version);

        // what the bake stores as BakeCOFVersion and compares in the cap handshake
        int bakeSideValue = ServerSideBakingModule.CofVersionOf(inventory, Agent);

        // what AIS puts in _updated_category_versions, which is what the viewer keeps and posts back as cof_version
        var envelope = new OSDMap();
        AisMutation.ReportVersion(envelope, inventory.GetFolder(Agent, CofId));
        int aisSideValue = ((OSDMap)envelope[AisMutation.UpdatedCategoryVersions])[CofId.ToString()].AsInteger();

        // and what AIS puts in a category envelope's "version"
        int aisCategoryValue = AisEnvelope.Category(inventory.Folder, Agent)["version"].AsInteger();

        Assert.Equal(version, bakeSideValue);
        Assert.Equal(version, aisSideValue);
        Assert.Equal(version, aisCategoryValue);
        _out.WriteLine($"folder.Version={version}  bake={bakeSideValue}  AIS _updated_category_versions={aisSideValue}  AIS category.version={aisCategoryValue}");
    }

    [Fact]
    public void TheBakeReadsTheCurrentOutfitFolderSpecificallyAndFreshEveryTime()
    {
        var inventory = new OneFolderInventory(9);

        Assert.Equal(9, ServerSideBakingModule.CofVersionOf(inventory, Agent));
        Assert.Equal(9, ServerSideBakingModule.CofVersionOf(inventory, Agent));

        // ADR-006: read fresh, never cached — two reads, two service calls
        Assert.Equal(2, inventory.GetFolderForTypeCalls);

        // a different agent has no folder here and must not inherit this one's version
        Assert.Equal(0, ServerSideBakingModule.CofVersionOf(inventory, UUID.Random()));
        // and no inventory service at all is 0, not a throw
        Assert.Equal(0, ServerSideBakingModule.CofVersionOf(null, Agent));
    }

    /// <summary>
    /// The one place the identity can break, recorded rather than hidden: <see cref="InventoryFolderBase.Version"/>
    /// is a <c>ushort</c> (InventoryFolderBase.cs:67) while the cap's <c>cof_version</c> is an S32 and the
    /// database column is wider. That is AIS ledger A-Q13. Both sides read the same truncated field, so they stay
    /// equal to each other — but past 65535 both disagree with the database, and the wrap makes an older outfit
    /// compare equal to a newer one.
    /// </summary>
    [Fact]
    public void TheIdentityHoldsThroughTheUshortWrapBecauseBothSidesTruncateAlike()
    {
        const int past = 65536 + 7;
        var inventory = new OneFolderInventory(past);   // the ctor casts to ushort exactly as the data layer does

        int bakeSideValue = ServerSideBakingModule.CofVersionOf(inventory, Agent);
        var envelope = new OSDMap();
        AisMutation.ReportVersion(envelope, inventory.GetFolder(Agent, CofId));
        int aisSideValue = ((OSDMap)envelope[AisMutation.UpdatedCategoryVersions])[CofId.ToString()].AsInteger();

        Assert.Equal(bakeSideValue, aisSideValue);      // still one number
        Assert.Equal(7, bakeSideValue);                 // but not the database's, which is A-Q13
        Assert.NotEqual(past, bakeSideValue);
    }
}
