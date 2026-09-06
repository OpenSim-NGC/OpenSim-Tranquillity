/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES ARE DISCLAIMED. IN NO EVENT SHALL THE
 * CONTRIBUTORS BE LIABLE FOR ANY DAMAGES ARISING IN ANY WAY OUT OF THE USE OF
 * THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections;
using System.IO;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework.TrustedHypergrid;
using Xunit;

namespace OpenSim.TrustedHypergrid.Tests;

/// <summary>
/// Slice 2b — the operationalised keypair and the gatekeeper XML-RPC wiring, exercised through the
/// same ambient hooks the call sites use (<see cref="TrustedHypergridHooks"/>).
/// </summary>
public class TrustedHypergridWiringTests : IDisposable
{
    private string m_keyFile;

    public void Dispose()
    {
        TrustedHypergridHooks.Runtime = null;
        if (!string.IsNullOrEmpty(m_keyFile) && File.Exists(m_keyFile))
            File.Delete(m_keyFile);
    }

    private string TempKeyPath()
    {
        m_keyFile = Path.Combine(Path.GetTempPath(), "tg-secret-" + Guid.NewGuid().ToString("N") + ".ini");
        return m_keyFile;
    }

    private static IConfigSource Config(bool enabled, string keyFile)
    {
        var src = new IniConfigSource();
        IConfig c = src.AddConfig(TrustedHypergridRuntime.ConfigSection);
        c.Set("Enabled", enabled ? "true" : "false");
        if (keyFile != null)
            c.Set("PrivateKeyFile", keyFile);
        return src;
    }

    private sealed class AlwaysTier : IGridTrustLookup
    {
        private readonly UUID m_id;
        private readonly int m_tier;
        public AlwaysTier(UUID id, int tier) { m_id = id; m_tier = tier; }
        public bool TryResolveByFingerprint(string fingerprint, out UUID gridId, out int tier)
        {
            gridId = m_id;
            tier = m_tier;
            return true;
        }
    }

    // 1 — Enabled=false: no signer, no verifier, no key file, Hashtable byte-identical to unsigned.
    [Fact]
    public void Disabled_DoesNothing_AndWritesNoKeyFile()
    {
        string keyFile = TempKeyPath();
        TrustedHypergridRuntime rt = TrustedHypergridRuntime.FromConfig(Config(false, keyFile));
        TrustedHypergridHooks.Runtime = rt;

        Assert.False(rt.Enabled);
        Assert.Null(rt.Signer);
        Assert.Null(rt.Verifier);
        Assert.False(File.Exists(keyFile));   // Enabled=false never touches the key file

        var hash = new Hashtable { { "region_name", "Welcome" } };
        TrustedHypergridHooks.SignOutbound(hash, "link_region");

        Assert.Single(hash);                                   // unchanged
        Assert.Equal("Welcome", hash["region_name"]);
        Assert.False(hash.ContainsKey(SignatureMaterial.XmlRpcKey));

        // Verifier not invoked either: classification returns null when disabled.
        Assert.Null(TrustedHypergridHooks.ClassifyInbound(hash, "link_region"));
    }

    // 2 — Enabled=true, no key file: keypair generated (and its fingerprint logged on generation).
    [Fact]
    public void Enabled_FirstRun_GeneratesKeypair()
    {
        string keyFile = TempKeyPath();
        Assert.False(File.Exists(keyFile));

        TrustedHypergridRuntime rt = TrustedHypergridRuntime.FromConfig(Config(true, keyFile));

        Assert.True(rt.Enabled);
        Assert.True(rt.KeypairWasGenerated);
        Assert.True(File.Exists(keyFile));
        Assert.NotNull(rt.Fingerprint);
        Assert.Equal(64, rt.Fingerprint.Length);
        Assert.NotNull(rt.Signer);
        Assert.NotNull(rt.Verifier);
    }

    // 3 — Enabled=true, existing key file: same fingerprint loaded, not regenerated.
    [Fact]
    public void Enabled_SecondRun_LoadsSameKey_NotRegenerated()
    {
        string keyFile = TempKeyPath();
        TrustedHypergridRuntime first = TrustedHypergridRuntime.FromConfig(Config(true, keyFile));
        Assert.True(first.KeypairWasGenerated);
        string fingerprint = first.Fingerprint;

        TrustedHypergridRuntime second = TrustedHypergridRuntime.FromConfig(Config(true, keyFile));

        Assert.False(second.KeypairWasGenerated);   // loaded, not generated
        Assert.Equal(fingerprint, second.Fingerprint);
    }

    // 4 — round-trip: sign outbound into a Hashtable, verify inbound from that same Hashtable.
    [Fact]
    public void RoundTrip_SignOutbound_ThenClassifyInbound_IsTrustedEligible()
    {
        string keyFile = TempKeyPath();
        UUID gridId = UUID.Random();
        TrustedHypergridRuntime rt = TrustedHypergridRuntime.FromConfig(
            Config(true, keyFile), new AlwaysTier(gridId, GridTrustContext.TierTrusted));
        TrustedHypergridHooks.Runtime = rt;

        // Outbound: the connector's Hashtable, then sign into it.
        var hash = new Hashtable { { "region_name", "Welcome" } };
        TrustedHypergridHooks.SignOutbound(hash, "link_region");

        Assert.True(hash.ContainsKey(SignatureMaterial.XmlRpcKey));
        Assert.True(hash.ContainsKey(SignatureMaterial.XmlRpcSignature));

        // Inbound: the handler receives exactly that Hashtable and classifies it.
        GridTrustContext ctx = TrustedHypergridHooks.ClassifyInbound(hash, "link_region");

        Assert.NotNull(ctx);
        Assert.Equal(VerificationOutcome.Verified, ctx.Outcome);
        Assert.Equal(GridTrustContext.TierTrusted, ctx.Tier);
        Assert.Equal(gridId, ctx.GridId);
    }

    // 5 — a Hashtable with no tg_* keys (a stock grid) yields an Open context; the handler proceeds.
    [Fact]
    public void UnsignedInbound_YieldsOpen_AndHandlerProceeds()
    {
        string keyFile = TempKeyPath();
        TrustedHypergridHooks.Runtime = TrustedHypergridRuntime.FromConfig(Config(true, keyFile));

        var hash = new Hashtable { { "region_uuid", UUID.Random().ToString() } };   // no tg_* keys

        GridTrustContext ctx = null;
        Exception ex = Record.Exception(() => ctx = TrustedHypergridHooks.ClassifyInbound(hash, "get_region"));

        Assert.Null(ex);   // never throws → the handler after this call proceeds normally
        Assert.NotNull(ctx);
        Assert.Equal(VerificationOutcome.Unverified, ctx.Outcome);
        Assert.Equal(GridTrustContext.TierOpen, ctx.Tier);
    }
}
