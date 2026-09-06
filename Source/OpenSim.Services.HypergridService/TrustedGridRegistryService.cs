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

using System;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Framework.TrustedHypergrid;

namespace OpenSim.Services.HypergridService;

/// <summary>
/// The trust registry (Design Brief §3, §4, §8): resolves a signature-verified caller's fingerprint
/// to its registry row (<see cref="IGridTrustLookup"/>), records first contact and key changes
/// (<see cref="IGridTrustRecorder"/>), and exposes the <c>hgtrust</c> console commands.
/// </summary>
/// <remarks>
/// <para>This class NEVER makes an access decision. It reports a tier; nothing in this slice reads
/// that tier to refuse anything (ADR-005 / Slice 3 NOT). Approval is explicit operator action only;
/// there is no code path that sets tier Trusted other than <see cref="Approve"/>.</para>
/// <para>Every URI that reaches the data layer is normalised by the one shared
/// <see cref="HGUriNormalizer"/> (Recon R6); this class never compares URI strings itself.</para>
/// </remarks>
public class TrustedGridRegistryService : TrustedGridServiceBase, IGridTrustLookup, IGridTrustRecorder
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private static TrustedGridRegistryService m_RootInstance = null;

    public const string DefaultApprovedBy = "console";

    public TrustedGridRegistryService(IConfigSource config)
        : base(config)
    {
        m_log.LogDebug("[TRUSTED HG]: trust registry starting");

        // In case there are several instances of this class in the same process,
        // the console commands are only registered for the root instance
        if (m_RootInstance == null)
        {
            m_RootInstance = this;

            if (MainConsole.Instance != null)
                RegisterConsoleCommands();
        }
    }

    #region IGridTrustLookup / IGridTrustRecorder

    /// <summary>
    /// Resolve a verified fingerprint to its grid and tier. A fingerprint that is not registered,
    /// or belongs to a grid whose stored key differs (key change pending re-approval stores the
    /// ORIGINAL key, so the new fingerprint does not match), resolves to nothing → Open.
    /// </summary>
    public bool TryResolveByFingerprint(string keyFingerprint, out UUID gridId, out int tier)
    {
        gridId = UUID.Zero;
        tier = GridTrustContext.TierOpen;
        if (string.IsNullOrEmpty(keyFingerprint))
            return false;

        try
        {
            TrustedGridData rec = m_Database.GetByFingerprint(keyFingerprint);
            if (rec == null)
                return false;
            gridId = rec.Id;
            tier = rec.Tier;
            return true;
        }
        catch (Exception e)
        {
            // ADR-005: a registry fault must not become a request fault. Unresolved → Open.
            m_log.LogWarning(e, "[TRUSTED HG]: registry lookup failed for fingerprint {0}; treating as unregistered", keyFingerprint);
            return false;
        }
    }

    /// <summary>
    /// TOFU on first contact (§3), delegating the establishment rule to Slice 1's
    /// <see cref="ITrustedGridData.RecordPresentedKey"/>: unknown → Open/pending; same key →
    /// last_seen; different key → state 2 with the original key preserved. Never promotes.
    /// </summary>
    public void RecordPresentedKey(string homeUri, byte[] publicKey, string keyFingerprint, DateTime whenUtc)
    {
        try
        {
            TrustedGridData before = m_Database.GetByHomeUri(homeUri);
            TrustedGridData after = m_Database.RecordPresentedKey(homeUri, publicKey, keyFingerprint, whenUtc);
            if (after == null)
                return;

            if (before == null)
                m_log.LogInformation("[TRUSTED HG]: first contact from {0} fingerprint {1}; recorded tier=Open state=pending id={2}",
                    after.HomeUri, keyFingerprint, after.Id);
            else if (before.State != (int)TrustState.KeyChangedPendingReapproval
                     && after.State == (int)TrustState.KeyChangedPendingReapproval)
                m_log.LogWarning("[TRUSTED HG]: {0} presented a DIFFERENT key (fingerprint {1}, registered {2}); flagged for re-approval, original key kept",
                    after.HomeUri, keyFingerprint, after.KeyFingerprint);
        }
        catch (Exception e)
        {
            m_log.LogWarning(e, "[TRUSTED HG]: failed to record presented key for {0}", homeUri);
        }
    }

    #endregion

    #region Operator operations (used by the console commands and by tests)

    public TrustedGridData[] List() => m_Database.GetAll() ?? Array.Empty<TrustedGridData>();

    /// <summary>Find by home URI, alias URI, or fingerprint (64 hex). Null when not found.</summary>
    public TrustedGridData Find(string uriOrFingerprint)
    {
        if (string.IsNullOrWhiteSpace(uriOrFingerprint))
            return null;
        string s = uriOrFingerprint.Trim();
        if (LooksLikeFingerprint(s))
            return m_Database.GetByFingerprint(s.ToLowerInvariant());

        // Anything else must be an absolute URI; the data layer normalises it (and throws on
        // garbage). An operator typo is "not found", not a console exception.
        if (!Uri.TryCreate(s, UriKind.Absolute, out _))
            return null;

        TrustedGridData rec = m_Database.GetByHomeUri(s);
        return rec ?? m_Database.GetByAlias(s);
    }

    /// <summary>Operator approval: tier Trusted, state approved. The ONLY path to Trusted.</summary>
    public TrustedGridData Approve(string uri, string approvedBy)
    {
        TrustedGridData rec = Find(uri);
        if (rec == null)
            return null;
        rec.Tier = (int)TrustTier.Trusted;
        rec.State = (int)TrustState.Approved;
        rec.ApprovedBy = string.IsNullOrWhiteSpace(approvedBy) ? DefaultApprovedBy : approvedBy;
        rec.ApprovedAt = DateTime.UtcNow;
        m_Database.Store(rec);
        m_log.LogInformation("[TRUSTED HG]: {0} approved as Trusted by {1} (fingerprint {2})", rec.HomeUri, rec.ApprovedBy, rec.KeyFingerprint);
        return m_Database.Get(rec.Id);
    }

    /// <summary>Operator block: tier Blocked. Recorded only; nothing enforces it in this slice.</summary>
    public TrustedGridData Block(string uri)
    {
        TrustedGridData rec = Find(uri);
        if (rec == null)
            return null;
        rec.Tier = (int)TrustTier.Blocked;
        m_Database.Store(rec);
        m_log.LogInformation("[TRUSTED HG]: {0} set to Blocked (fingerprint {1})", rec.HomeUri, rec.KeyFingerprint);
        return m_Database.Get(rec.Id);
    }

    /// <summary>Remove the grid and its aliases. Next contact starts over as Open/pending.</summary>
    public bool Forget(string uri)
    {
        TrustedGridData rec = Find(uri);
        if (rec == null)
            return false;
        bool ok = m_Database.Delete(rec.Id);
        if (ok)
            m_log.LogInformation("[TRUSTED HG]: {0} forgotten (fingerprint {1})", rec.HomeUri, rec.KeyFingerprint);
        return ok;
    }

    public string[] Aliases(UUID gridId) => m_Database.GetAliases(gridId) ?? Array.Empty<string>();

    /// <summary>
    /// Record an alias URI for an existing grid (§3: several HomeURIs may map to one key). Both
    /// URIs go through the shared normaliser inside the data layer. Not a console command in this
    /// slice; aliases are otherwise only ever read.
    /// </summary>
    public bool AddAlias(string uri, string aliasUri)
    {
        TrustedGridData rec = Find(uri);
        return rec != null && m_Database.StoreAlias(rec.Id, aliasUri);
    }

    #endregion

    #region Console

    private void RegisterConsoleCommands()
    {
        MainConsole.Instance.Commands.AddCommand("Hypergrid", false, "hgtrust list",
            "hgtrust list",
            "List every remote grid in the trust registry with its tier and state.",
            HandleList);

        MainConsole.Instance.Commands.AddCommand("Hypergrid", false, "hgtrust show",
            "hgtrust show <uri|fingerprint>",
            "Show a registry entry by home URI, alias URI, or key fingerprint.",
            HandleShow);

        MainConsole.Instance.Commands.AddCommand("Hypergrid", false, "hgtrust approve",
            "hgtrust approve <uri> [approved-by]",
            "Promote a grid to Trusted. The only way a grid becomes Trusted.",
            HandleApprove);

        MainConsole.Instance.Commands.AddCommand("Hypergrid", false, "hgtrust block",
            "hgtrust block <uri>",
            "Set a grid's tier to Blocked.",
            HandleBlock);

        MainConsole.Instance.Commands.AddCommand("Hypergrid", false, "hgtrust forget",
            "hgtrust forget <uri>",
            "Remove a grid and its aliases from the registry; its next contact is treated as first contact.",
            HandleForget);

        MainConsole.Instance.Commands.AddCommand("Hypergrid", false, "hgtrust key show",
            "hgtrust key show",
            "Show this grid's Trusted Hypergrid identity (public key fingerprint).",
            HandleKeyShow);
    }

    private void HandleList(string module, string[] cmd)
    {
        MainConsole.Instance.Output(FormatList());
    }

    private void HandleShow(string module, string[] cmd)
    {
        if (cmd.Length < 3)
        {
            MainConsole.Instance.Output("Usage: hgtrust show <uri|fingerprint>");
            return;
        }
        TrustedGridData rec = Find(cmd[2]);
        MainConsole.Instance.Output(rec == null ? $"No registry entry matches {cmd[2]}" : FormatEntry(rec));
    }

    private void HandleApprove(string module, string[] cmd)
    {
        if (cmd.Length < 3)
        {
            MainConsole.Instance.Output("Usage: hgtrust approve <uri> [approved-by]");
            return;
        }
        TrustedGridData rec = Approve(cmd[2], cmd.Length > 3 ? cmd[3] : DefaultApprovedBy);
        MainConsole.Instance.Output(rec == null ? $"No registry entry matches {cmd[2]}" : FormatEntry(rec));
    }

    private void HandleBlock(string module, string[] cmd)
    {
        if (cmd.Length < 3)
        {
            MainConsole.Instance.Output("Usage: hgtrust block <uri>");
            return;
        }
        TrustedGridData rec = Block(cmd[2]);
        MainConsole.Instance.Output(rec == null ? $"No registry entry matches {cmd[2]}" : FormatEntry(rec));
    }

    private void HandleForget(string module, string[] cmd)
    {
        if (cmd.Length < 3)
        {
            MainConsole.Instance.Output("Usage: hgtrust forget <uri>");
            return;
        }
        MainConsole.Instance.Output(Forget(cmd[2]) ? $"Forgotten {cmd[2]}" : $"No registry entry matches {cmd[2]}");
    }

    private void HandleKeyShow(string module, string[] cmd)
    {
        MainConsole.Instance.Output(FormatKeyShow(TrustedHypergridHooks.Runtime));
    }

    /// <summary>Output of <c>hgtrust list</c>. Public so the format is testable without a console.</summary>
    public string FormatList()
    {
        TrustedGridData[] rows = List();
        if (rows.Length == 0)
            return "Trust registry is empty.";

        ConsoleDisplayTable table = new ConsoleDisplayTable();
        table.AddColumn("Home URI", 40);
        table.AddColumn("Tier", 8);
        table.AddColumn("State", 22);
        table.AddColumn("Fingerprint", 16);
        table.AddColumn("First seen", 16);
        table.AddColumn("Last seen", 16);
        foreach (TrustedGridData r in rows)
        {
            table.AddRow(
                r.HomeUri,
                TierName(r.Tier),
                StateName(r.State),
                ShortFingerprint(r.KeyFingerprint),
                r.FirstSeen.ToString("yyyy-MM-dd HH:mm"),
                r.LastSeen.ToString("yyyy-MM-dd HH:mm"));
        }
        return table.ToString();
    }

    /// <summary>Output of <c>hgtrust show</c> (and the echo after approve/block).</summary>
    public string FormatEntry(TrustedGridData r)
    {
        ConsoleDisplayList list = new ConsoleDisplayList();
        list.AddRow("Home URI", r.HomeUri);
        list.AddRow("ID", r.Id);
        list.AddRow("Tier", $"{TierName(r.Tier)} ({r.Tier})");
        list.AddRow("State", $"{StateName(r.State)} ({r.State})");
        list.AddRow("Fingerprint", string.IsNullOrEmpty(r.KeyFingerprint) ? "(none - never signed)" : r.KeyFingerprint);
        list.AddRow("Public key", r.PublicKey == null || r.PublicKey.Length == 0 ? "(none)" : Convert.ToBase64String(r.PublicKey));
        list.AddRow("First seen", r.FirstSeen.ToString("u"));
        list.AddRow("Last seen", r.LastSeen.ToString("u"));
        list.AddRow("Approved by", string.IsNullOrEmpty(r.ApprovedBy) ? "-" : r.ApprovedBy);
        list.AddRow("Approved at", r.ApprovedAt.HasValue ? r.ApprovedAt.Value.ToString("u") : "-");
        string[] aliases = Aliases(r.Id);
        list.AddRow("Aliases", aliases.Length == 0 ? "-" : string.Join(", ", aliases));
        if (!string.IsNullOrEmpty(r.Notes))
            list.AddRow("Notes", r.Notes);
        if (r.State == (int)TrustState.KeyChangedPendingReapproval)
            list.AddRow("Attention", "presented a different key; the registered (original) key is kept. To accept the new key: hgtrust forget, let it reconnect, then hgtrust approve.");
        return list.ToString();
    }

    /// <summary>Output of <c>hgtrust key show</c>.</summary>
    public static string FormatKeyShow(TrustedHypergridRuntime rt)
    {
        ConsoleDisplayList list = new ConsoleDisplayList();
        if (rt == null || !rt.Enabled || rt.Keypair == null)
        {
            list.AddRow("Trusted Hypergrid", "disabled (no grid identity loaded)");
            return list.ToString();
        }
        list.AddRow("Trusted Hypergrid", "enabled");
        list.AddRow("Fingerprint", rt.Fingerprint);
        list.AddRow("Public key", Convert.ToBase64String(rt.Keypair.PublicKey));
        list.AddRow("Key origin", rt.KeypairWasGenerated ? "generated this run" : "loaded from file");
        return list.ToString();
    }

    public static string TierName(int tier) => tier switch
    {
        (int)TrustTier.Blocked => "Blocked",
        (int)TrustTier.Open => "Open",
        (int)TrustTier.Trusted => "Trusted",
        _ => $"?{tier}",
    };

    public static string StateName(int state) => state switch
    {
        (int)TrustState.Pending => "pending",
        (int)TrustState.Approved => "approved",
        (int)TrustState.KeyChangedPendingReapproval => "key-changed (re-approve)",
        _ => $"?{state}",
    };

    private static string ShortFingerprint(string fp) =>
        string.IsNullOrEmpty(fp) ? "-" : (fp.Length > 16 ? fp.Substring(0, 16) : fp);

    private static bool LooksLikeFingerprint(string s)
    {
        if (s.Length != 64)
            return false;
        foreach (char c in s)
            if (!Uri.IsHexDigit(c))
                return false;
        return true;
    }

    #endregion
}
