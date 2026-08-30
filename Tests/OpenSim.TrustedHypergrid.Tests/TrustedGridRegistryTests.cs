using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework.TrustedHypergrid;
using OpenSim.Services.HypergridService;
using Xunit;

namespace OpenSim.TrustedHypergrid.Tests;

/// <summary>
/// Slice 3: the trust registry wired end to end over SQLite — data plugin loaded from config the
/// way Robust does it, TOFU recording, key-change flagging, operator operations, alias resolution,
/// the hgtrust output formats, and the guarantee that nothing here refuses a caller.
/// </summary>
public class TrustedGridRegistryTests : IDisposable
{
    private readonly List<string> m_tempFiles = new();

    public void Dispose()
    {
        // Restore the process-wide hooks so other test classes see a clean slate.
        TrustedHypergridHooks.Runtime = null;
        TrustedHypergridHooks.Lookup = null;
        foreach (string f in m_tempFiles)
        {
            try { File.Delete(f); } catch { /* best effort */ }
        }
    }

    // ---- fixtures ------------------------------------------------------------------------

    private string TempFile(string ext)
    {
        string f = Path.Combine(Path.GetTempPath(), "thg-registry-" + Guid.NewGuid().ToString("N") + ext);
        m_tempFiles.Add(f);
        return f;
    }

    /// <summary>Config exactly as Robust would carry it: [DatabaseService] supplies the store.</summary>
    private IConfigSource RegistryConfig(bool enabled = true, string keyFile = null, bool overrideInTrustSection = false)
    {
        string db = TempFile(".db");
        string sqliteDll = Path.Combine(AppContext.BaseDirectory, "OpenSim.Data.SQLite.dll");
        Assert.True(File.Exists(sqliteDll), "SQLite data plugin must be in the test output");

        IniConfigSource cfg = new IniConfigSource();
        IConfig dbs = cfg.AddConfig("DatabaseService");
        if (!overrideInTrustSection)
        {
            dbs.Set("StorageProvider", sqliteDll);
            dbs.Set("ConnectionString", "URI=file:" + db + ",version=3");
        }
        else
        {
            // A deliberately broken [DatabaseService] that [TrustedHypergrid] must override.
            dbs.Set("StorageProvider", "Does.Not.Exist.dll");
            dbs.Set("ConnectionString", "nonsense");
        }

        IConfig th = cfg.AddConfig(TrustedHypergridRuntime.ConfigSection);
        th.Set("Enabled", enabled ? "true" : "false");
        th.Set("PrivateKeyFile", keyFile ?? TempFile(".ini"));
        if (overrideInTrustSection)
        {
            th.Set("StorageProvider", sqliteDll);
            th.Set("ConnectionString", "URI=file:" + db + ",version=3");
        }
        return cfg;
    }

    private static byte[] Key(byte fill)
    {
        byte[] k = new byte[32];
        Array.Fill(k, fill);
        return k;
    }

    private static string Fp(byte[] key) => HGSignatureEnvelope.Sha256Hex(key);

    private const string GridA = "http://grid-a.example:8002/";
    private const string GridB = "http://grid-b.example:8002/";

    // ---- construction follows the Robust data-plugin convention -----------------------------

    [Fact]
    public void Registry_LoadsDataPluginFrom_DatabaseService_AndRunsMigration()
    {
        TrustedGridRegistryService reg = new(RegistryConfig());
        Assert.Empty(reg.List());          // migration ran: the table exists and is empty
    }

    [Fact]
    public void Registry_TrustedHypergridSection_OverridesDatabaseService()
    {
        TrustedGridRegistryService reg = new(RegistryConfig(overrideInTrustSection: true));
        Assert.Empty(reg.List());
    }

    [Fact]
    public void Registry_WithoutStorageProvider_Throws_DoesNotSilentlyRunUnbacked()
    {
        IniConfigSource cfg = new IniConfigSource();
        cfg.AddConfig(TrustedHypergridRuntime.ConfigSection).Set("Enabled", "true");
        Assert.Throws<Exception>(() => new TrustedGridRegistryService(cfg));
    }

    // ---- TOFU (§3) ----------------------------------------------------------------------------

    [Fact]
    public void FirstContact_UnknownVerifiedGrid_IsRecordedOpenPending_WithKeyAndFingerprint()
    {
        TrustedGridRegistryService reg = new(RegistryConfig());
        byte[] key = Key(0xA1);
        DateTime t = new(2026, 8, 29, 20, 0, 0, DateTimeKind.Utc);

        reg.RecordPresentedKey(GridA, key, Fp(key), t);

        TrustedGridData rec = reg.Find(GridA);
        Assert.NotNull(rec);
        Assert.Equal((int)TrustTier.Open, rec.Tier);
        Assert.Equal((int)TrustState.Pending, rec.State);
        Assert.Equal(Fp(key), rec.KeyFingerprint);
        Assert.Equal(key, rec.PublicKey);
        Assert.Equal(t, rec.FirstSeen);
        Assert.Equal(t, rec.LastSeen);
        Assert.Equal(string.Empty, rec.ApprovedBy);
        Assert.Null(rec.ApprovedAt);

        // and it resolves — to Open, never higher, without operator action
        Assert.True(reg.TryResolveByFingerprint(Fp(key), out UUID id, out int tier));
        Assert.Equal(rec.Id, id);
        Assert.Equal(GridTrustContext.TierOpen, tier);
    }

    [Fact]
    public void SecondContact_SameKey_NoDuplicateRow_LastSeenUpdated_FirstSeenKept()
    {
        TrustedGridRegistryService reg = new(RegistryConfig());
        byte[] key = Key(0xA2);
        DateTime t1 = new(2026, 8, 29, 20, 0, 0, DateTimeKind.Utc);
        DateTime t2 = t1.AddHours(3);

        reg.RecordPresentedKey(GridA, key, Fp(key), t1);
        reg.RecordPresentedKey("HTTP://GRID-A.EXAMPLE:8002", key, Fp(key), t2);   // same grid, un-normalised spelling

        TrustedGridData[] all = reg.List();
        Assert.Single(all);
        Assert.Equal(t1, all[0].FirstSeen);
        Assert.Equal(t2, all[0].LastSeen);
        Assert.Equal((int)TrustState.Pending, all[0].State);
        Assert.Equal(GridA, all[0].HomeUri);
    }

    [Fact]
    public void DifferentKey_ForExistingUri_FlagsState2_KeepsOriginalKey_NewKeyDoesNotResolve()
    {
        TrustedGridRegistryService reg = new(RegistryConfig());
        byte[] original = Key(0xB1);
        byte[] impostor = Key(0xB2);
        DateTime t = DateTime.UtcNow;

        reg.RecordPresentedKey(GridB, original, Fp(original), t);
        reg.Approve(GridB, "operator");                      // established relationship
        reg.RecordPresentedKey(GridB, impostor, Fp(impostor), t.AddMinutes(1));

        TrustedGridData rec = reg.Find(GridB);
        Assert.Equal((int)TrustState.KeyChangedPendingReapproval, rec.State);
        Assert.Equal(original, rec.PublicKey);
        Assert.Equal(Fp(original), rec.KeyFingerprint);

        Assert.False(reg.TryResolveByFingerprint(Fp(impostor), out _, out _));   // new key: unknown → Open
        Assert.True(reg.TryResolveByFingerprint(Fp(original), out _, out _));
    }

    // ---- operator surface (§8) ------------------------------------------------------------------

    [Fact]
    public void Approve_SetsTrusted_Approved_ApprovedByAndAt_AndIsTheOnlyPathToTrusted()
    {
        TrustedGridRegistryService reg = new(RegistryConfig());
        byte[] key = Key(0xC1);
        reg.RecordPresentedKey(GridA, key, Fp(key), DateTime.UtcNow);

        // Repeated contact never promotes.
        for (int i = 0; i < 3; i++)
            reg.RecordPresentedKey(GridA, key, Fp(key), DateTime.UtcNow);
        Assert.Equal((int)TrustTier.Open, reg.Find(GridA).Tier);

        TrustedGridData rec = reg.Approve(GridA, "john");
        Assert.Equal((int)TrustTier.Trusted, rec.Tier);
        Assert.Equal((int)TrustState.Approved, rec.State);
        Assert.Equal("john", rec.ApprovedBy);
        Assert.NotNull(rec.ApprovedAt);

        Assert.True(reg.TryResolveByFingerprint(Fp(key), out _, out int tier));
        Assert.Equal(GridTrustContext.TierTrusted, tier);
    }

    [Fact]
    public void Approve_UnknownUri_ReturnsNull_CreatesNothing()
    {
        TrustedGridRegistryService reg = new(RegistryConfig());
        Assert.Null(reg.Approve("http://nobody.example:8002/", "john"));
        Assert.Empty(reg.List());
    }

    [Theory]
    [InlineData("nobody")]
    [InlineData("not a uri")]
    [InlineData("deadbeef")]
    [InlineData("")]
    public void Find_ApproveBlockForget_WithGarbageInput_ReturnNotFound_NeverThrow(string input)
    {
        TrustedGridRegistryService reg = new(RegistryConfig());
        Assert.Null(reg.Find(input));
        Assert.Null(reg.Approve(input, "john"));
        Assert.Null(reg.Block(input));
        Assert.False(reg.Forget(input));
        Assert.Empty(reg.List());
    }

    [Fact]
    public void Block_SetsBlockedTier_AndStillResolves_ReportingOnly()
    {
        TrustedGridRegistryService reg = new(RegistryConfig());
        byte[] key = Key(0xD1);
        reg.RecordPresentedKey(GridA, key, Fp(key), DateTime.UtcNow);

        TrustedGridData rec = reg.Block(GridA);
        Assert.Equal((int)TrustTier.Blocked, rec.Tier);

        // The registry reports Blocked; it does not refuse. Resolution succeeds normally.
        Assert.True(reg.TryResolveByFingerprint(Fp(key), out UUID id, out int tier));
        Assert.Equal(rec.Id, id);
        Assert.Equal(GridTrustContext.TierBlocked, tier);
    }

    [Fact]
    public void Forget_RemovesRowAndAliases_NextContactIsFirstContact()
    {
        TrustedGridRegistryService reg = new(RegistryConfig());
        byte[] key = Key(0xE1);
        reg.RecordPresentedKey(GridA, key, Fp(key), DateTime.UtcNow);
        UUID id = reg.Find(GridA).Id;
        Assert.True(reg.AddAlias(GridA, "http://alias.grid-a.example:8002/"));
        reg.Approve(GridA, "john");

        Assert.True(reg.Forget(GridA));
        Assert.Empty(reg.List());
        Assert.Empty(reg.Aliases(id));
        Assert.Null(reg.Find("http://alias.grid-a.example:8002/"));
        Assert.False(reg.TryResolveByFingerprint(Fp(key), out _, out _));
        Assert.False(reg.Forget(GridA));

        reg.RecordPresentedKey(GridA, key, Fp(key), DateTime.UtcNow);
        TrustedGridData again = reg.Find(GridA);
        Assert.NotEqual(id, again.Id);
        Assert.Equal((int)TrustTier.Open, again.Tier);
        Assert.Equal((int)TrustState.Pending, again.State);
    }

    [Fact]
    public void Aliases_ResolveToOneGrid_ThroughTheSharedNormaliser()
    {
        TrustedGridRegistryService reg = new(RegistryConfig());
        byte[] key = Key(0xF1);
        reg.RecordPresentedKey(GridA, key, Fp(key), DateTime.UtcNow);
        UUID id = reg.Find(GridA).Id;

        Assert.True(reg.AddAlias(GridA, "HTTP://Login.Grid-A.Example:8002"));
        Assert.True(reg.AddAlias("http://grid-a.example:8002", "http://grid-a.example:8002/gatekeeper"));

        Assert.Equal(id, reg.Find("http://login.grid-a.example:8002/").Id);
        Assert.Equal(id, reg.Find("HTTP://LOGIN.GRID-A.EXAMPLE:8002/").Id);
        Assert.Equal(id, reg.Find("http://grid-a.example:8002/gatekeeper/").Id);
        Assert.Equal(id, reg.Find(Fp(key)).Id);                       // by fingerprint
        Assert.Equal(id, reg.Find(Fp(key).ToUpperInvariant()).Id);    // fingerprint case-insensitive
        Assert.Equal(new[] { "http://grid-a.example:8002/gatekeeper/", "http://login.grid-a.example:8002/" }, reg.Aliases(id));
        Assert.Single(reg.List());
    }

    // ---- hooks: verified caller resolves to registry tier; first contact recorded when URI known ---

    private sealed class Signed
    {
        public TrustedHypergridRuntime Runtime;
        public GridKeypair CallerKey;
        public Hashtable Params;
    }

    private Signed SignedRequest(TrustedGridRegistryService reg, string method)
    {
        IConfigSource cfg = RegistryConfig();
        TrustedHypergridRuntime rt = TrustedHypergridRuntime.FromConfig(cfg, reg);   // our side (verifier bound to registry)
        GridKeypair caller = GridKeypair.Generate();                                  // the remote grid
        Hashtable p = new() { ["region_name"] = "Welcome" };
        new GridSignatureSigner(caller).SignInto(p, method, DateTime.UtcNow);
        return new Signed { Runtime = rt, CallerKey = caller, Params = p };
    }

    [Fact]
    public void ClassifyInbound_VerifiedCaller_WithUri_IsRecordedPending_AndContextCarriesGridId()
    {
        TrustedGridRegistryService reg = new(RegistryConfig());
        Signed s = SignedRequest(reg, "link_region");
        TrustedHypergridHooks.Runtime = s.Runtime;
        TrustedHypergridHooks.Lookup = reg;

        GridTrustContext ctx = TrustedHypergridHooks.ClassifyInbound(s.Params, "link_region", GridB);

        Assert.Equal(VerificationOutcome.Verified, ctx.Outcome);
        Assert.Equal(GridTrustContext.TierOpen, ctx.Tier);
        TrustedGridData rec = reg.Find(GridB);
        Assert.NotNull(rec);
        Assert.Equal(rec.Id, ctx.GridId);
        Assert.Equal(s.CallerKey.Fingerprint, rec.KeyFingerprint);
        Assert.Equal((int)TrustState.Pending, rec.State);
    }

    [Fact]
    public void ClassifyInbound_VerifiedCaller_WithoutUri_ResolvesButRecordsNothing()
    {
        TrustedGridRegistryService reg = new(RegistryConfig());
        Signed s = SignedRequest(reg, "get_region");
        TrustedHypergridHooks.Runtime = s.Runtime;
        TrustedHypergridHooks.Lookup = reg;

        GridTrustContext ctx = TrustedHypergridHooks.ClassifyInbound(s.Params, "get_region");   // today's call sites

        Assert.Equal(VerificationOutcome.Verified, ctx.Outcome);
        Assert.Equal(UUID.Zero, ctx.GridId);
        Assert.Equal(GridTrustContext.TierOpen, ctx.Tier);
        Assert.Empty(reg.List());
    }

    [Fact]
    public void ClassifyInbound_ApprovedCaller_ResolvesTrusted_BlockedCaller_ResolvesBlocked_NeitherRefused()
    {
        TrustedGridRegistryService reg = new(RegistryConfig());
        Signed s = SignedRequest(reg, "link_region");
        TrustedHypergridHooks.Runtime = s.Runtime;
        TrustedHypergridHooks.Lookup = reg;

        reg.RecordPresentedKey(GridB, s.CallerKey.PublicKey, s.CallerKey.Fingerprint, DateTime.UtcNow);
        reg.Approve(GridB, "john");
        GridTrustContext trusted = TrustedHypergridHooks.ClassifyInbound(s.Params, "link_region", GridB);
        Assert.Equal(GridTrustContext.TierTrusted, trusted.Tier);
        Assert.Equal(VerificationOutcome.Verified, trusted.Outcome);

        reg.Block(GridB);
        Hashtable again = new() { ["region_name"] = "Welcome" };
        new GridSignatureSigner(s.CallerKey).SignInto(again, "link_region", DateTime.UtcNow);   // fresh nonce
        GridTrustContext blocked = TrustedHypergridHooks.ClassifyInbound(again, "link_region", GridB);
        Assert.NotNull(blocked);                                             // returned, not refused
        Assert.Equal(GridTrustContext.TierBlocked, blocked.Tier);
        Assert.Equal(VerificationOutcome.Verified, blocked.Outcome);
    }

    [Fact]
    public void ClassifyInbound_UnsignedCaller_IsOpenUnverified_AndNothingRecorded()
    {
        TrustedGridRegistryService reg = new(RegistryConfig());
        TrustedHypergridHooks.Runtime = TrustedHypergridRuntime.FromConfig(RegistryConfig(), reg);
        TrustedHypergridHooks.Lookup = reg;

        GridTrustContext ctx = TrustedHypergridHooks.ClassifyInbound(new Hashtable { ["region_name"] = "x" }, "link_region", GridB);

        Assert.Equal(VerificationOutcome.Unverified, ctx.Outcome);
        Assert.Equal(GridTrustContext.TierOpen, ctx.Tier);
        Assert.Empty(reg.List());
    }

    // ---- console output formats -------------------------------------------------------------------

    [Fact]
    public void FormatList_EmptyRegistry()
    {
        TrustedGridRegistryService reg = new(RegistryConfig());
        Assert.Equal("Trust registry is empty.", reg.FormatList());
    }

    [Fact]
    public void FormatList_TableHasHeaderAndOneRowPerGrid()
    {
        TrustedGridRegistryService reg = new(RegistryConfig());
        byte[] a = Key(0x11); byte[] b = Key(0x22);
        DateTime t = new(2026, 8, 29, 20, 5, 0, DateTimeKind.Utc);
        reg.RecordPresentedKey(GridA, a, Fp(a), t);
        reg.RecordPresentedKey(GridB, b, Fp(b), t);
        reg.Approve(GridB, "john");

        string text = reg.FormatList();
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Home URI", lines[0]);
        Assert.Contains("Tier", lines[0]);
        Assert.Contains("State", lines[0]);
        Assert.Contains("Fingerprint", lines[0]);
        Assert.StartsWith(GridA, lines[1]);
        Assert.Contains("Open", lines[1]);
        Assert.Contains("pending", lines[1]);
        Assert.Contains(Fp(a).Substring(0, 16), lines[1]);
        Assert.Contains("2026-08-29 20:05", lines[1]);
        Assert.StartsWith(GridB, lines[2]);
        Assert.Contains("Trusted", lines[2]);
        Assert.Contains("approved", lines[2]);
    }

    [Fact]
    public void FormatEntry_ShowsEveryColumn_AndKeyChangeAttention()
    {
        TrustedGridRegistryService reg = new(RegistryConfig());
        byte[] a = Key(0x33);
        reg.RecordPresentedKey(GridA, a, Fp(a), DateTime.UtcNow);
        reg.AddAlias(GridA, "http://alias.grid-a.example:8002/");
        string text = reg.FormatEntry(reg.Find(GridA));
        AssertRow(text, "Home URI", GridA);
        AssertRow(text, "Tier", "Open (1)");
        AssertRow(text, "State", "pending (0)");
        AssertRow(text, "Fingerprint", Fp(a));
        AssertRow(text, "Aliases", "http://alias.grid-a.example:8002/");
        Assert.DoesNotContain("Attention", text);

        reg.RecordPresentedKey(GridA, Key(0x34), Fp(Key(0x34)), DateTime.UtcNow);
        string flagged = reg.FormatEntry(reg.Find(GridA));
        AssertRow(flagged, "State", "key-changed (re-approve) (2)");
        Assert.Contains("Attention", flagged);
    }

    [Fact]
    public void FormatKeyShow_DisabledAndEnabled()
    {
        AssertRow(TrustedGridRegistryService.FormatKeyShow(null), "Trusted Hypergrid", "disabled (no grid identity loaded)");
        AssertRow(TrustedGridRegistryService.FormatKeyShow(TrustedHypergridRuntime.Disabled()), "Trusted Hypergrid", "disabled (no grid identity loaded)");

        TrustedHypergridRuntime rt = TrustedHypergridRuntime.FromConfig(RegistryConfig());
        string text = TrustedGridRegistryService.FormatKeyShow(rt);
        AssertRow(text, "Trusted Hypergrid", "enabled");
        AssertRow(text, "Fingerprint", rt.Fingerprint);
        AssertRow(text, "Public key", Convert.ToBase64String(rt.Keypair.PublicKey));
        AssertRow(text, "Key origin", "generated this run");
    }

    /// <summary>
    /// ConsoleDisplayList pads every key to the longest key, so a row renders as
    /// "<key><padding> : <value>". Match that shape exactly at line granularity.
    /// </summary>
    private static void AssertRow(string text, string key, string value)
    {
        string pattern = "^" + System.Text.RegularExpressions.Regex.Escape(key) + @" *: " +
                         System.Text.RegularExpressions.Regex.Escape(value) + "$";
        Assert.Matches(new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.Multiline), text);
    }
}
