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
using System.IO;
using System.Security.Cryptography;
using OpenSim.Framework.TrustedHypergrid;
using Xunit;

namespace OpenSim.TrustedHypergrid.Tests;

public class GridKeypairTests
{
    [Fact]
    public void GenerateProducesEd25519SizedKeys()
    {
        GridKeypair kp = GridKeypair.Generate();

        Assert.Equal(32, kp.PrivateKey.Length);
        Assert.Equal(32, kp.PublicKey.Length);
        Assert.Equal(64, kp.Fingerprint.Length);
    }

    [Fact]
    public void FingerprintIsSha256HexOfPublicKey()
    {
        GridKeypair kp = GridKeypair.Generate();

        string expected = Convert.ToHexString(SHA256.HashData(kp.PublicKey)).ToLowerInvariant();
        Assert.Equal(expected, kp.Fingerprint);

        // Stable: the same private key always yields the same public key and fingerprint.
        GridKeypair reloaded = GridKeypair.FromPrivateKey(kp.PrivateKey);
        Assert.Equal(kp.PublicKey, reloaded.PublicKey);
        Assert.Equal(kp.Fingerprint, reloaded.Fingerprint);
    }

    [Fact]
    public void SaveLoadRoundTrips()
    {
        GridKeypair original = GridKeypair.Generate();
        string path = Path.Combine(Path.GetTempPath(), "tg-keypair-" + Guid.NewGuid().ToString("N") + ".ini");

        try
        {
            original.Save(path);
            Assert.True(File.Exists(path));

            GridKeypair loaded = GridKeypair.Load(path);

            Assert.Equal(original.PrivateKey, loaded.PrivateKey);
            Assert.Equal(original.PublicKey, loaded.PublicKey);
            Assert.Equal(original.Fingerprint, loaded.Fingerprint);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void LoadOrCreateGeneratesThenReloadsSameKey()
    {
        string path = Path.Combine(Path.GetTempPath(), "tg-keypair-" + Guid.NewGuid().ToString("N") + ".ini");

        try
        {
            GridKeypair created = GridKeypair.LoadOrCreate(path);   // first run: generates + saves
            GridKeypair reloaded = GridKeypair.LoadOrCreate(path);  // second run: loads existing

            Assert.Equal(created.Fingerprint, reloaded.Fingerprint);
            Assert.Equal(created.PrivateKey, reloaded.PrivateKey);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
