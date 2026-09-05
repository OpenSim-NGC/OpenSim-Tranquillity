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
using System.IO;
using System.Security.Cryptography;
using Nini.Config;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace OpenSim.Framework.TrustedHypergrid;

/// <summary>
/// The local grid's Ed25519 identity for the Trusted Hypergrid (Design Brief §4).
///
/// Ed25519 is provided by BouncyCastle.Cryptography — pure managed, so a single build runs
/// on win-x64, linux-x64 self-contained, and linux-arm64 with no per-RID native dependency.
///
/// The private key is held in memory as its 32-byte seed and persisted, when persisted at
/// all, to a file OUTSIDE version control following the <c>bin/DirectDeliverySecret.ini</c>
/// convention: a small INI carrying the secret, loaded via Nini. It is never stored in the
/// database and never written into the source tree. Callers supply the path; this type does
/// not choose an in-repo default.
/// </summary>
public sealed class GridKeypair
{
    private const string SectionName = "TrustedHypergrid";
    private const string PrivateKeyEntry = "PrivateKey";

    /// <summary>The 32-byte Ed25519 private key (seed).</summary>
    public byte[] PrivateKey { get; }

    /// <summary>The 32-byte Ed25519 public key.</summary>
    public byte[] PublicKey { get; }

    /// <summary>Lowercase SHA-256 hex (64 chars) of <see cref="PublicKey"/> — the operator-facing identifier.</summary>
    public string Fingerprint { get; }

    private GridKeypair(byte[] privateKey, byte[] publicKey)
    {
        PrivateKey = privateKey;
        PublicKey = publicKey;
        Fingerprint = ComputeFingerprint(publicKey);
    }

    /// <summary>Generate a fresh random keypair.</summary>
    public static GridKeypair Generate()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        AsymmetricCipherKeyPair kp = generator.GenerateKeyPair();

        var priv = (Ed25519PrivateKeyParameters)kp.Private;
        var pub = (Ed25519PublicKeyParameters)kp.Public;
        return new GridKeypair(priv.GetEncoded(), pub.GetEncoded());
    }

    /// <summary>Reconstruct a keypair from a 32-byte Ed25519 private key seed.</summary>
    public static GridKeypair FromPrivateKey(byte[] privateKey)
    {
        if (privateKey == null || privateKey.Length != Ed25519PrivateKeyParameters.KeySize)
            throw new ArgumentException(
                $"Ed25519 private key must be {Ed25519PrivateKeyParameters.KeySize} bytes", nameof(privateKey));

        var priv = new Ed25519PrivateKeyParameters(privateKey, 0);
        Ed25519PublicKeyParameters pub = priv.GeneratePublicKey();
        return new GridKeypair(priv.GetEncoded(), pub.GetEncoded());
    }

    /// <summary>
    /// Persist the private key to a gitignored INI include at <paramref name="path"/>.
    /// The public key and fingerprint are derived on load and are not stored.
    /// </summary>
    public void Save(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("path must not be null or empty", nameof(path));

        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var source = new IniConfigSource();
        IConfig cfg = source.AddConfig(SectionName);
        cfg.Set(PrivateKeyEntry, Convert.ToHexString(PrivateKey).ToLowerInvariant());
        source.Save(path);
    }

    /// <summary>Load a keypair from an INI include previously written by <see cref="Save"/>.</summary>
    public static GridKeypair Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("grid keypair include not found", path);

        var source = new IniConfigSource(path);
        IConfig cfg = source.Configs[SectionName];
        if (cfg == null)
            throw new FormatException($"grid keypair include '{path}' has no [{SectionName}] section");

        string hex = cfg.GetString(PrivateKeyEntry, string.Empty);
        if (string.IsNullOrWhiteSpace(hex))
            throw new FormatException($"grid keypair include '{path}' has no {PrivateKeyEntry}");

        return FromPrivateKey(Convert.FromHexString(hex.Trim()));
    }

    /// <summary>
    /// Load the keypair at <paramref name="path"/>, generating and saving a new one on first run
    /// if the file does not yet exist (Design Brief §4: "generated on first run").
    /// </summary>
    public static GridKeypair LoadOrCreate(string path)
    {
        if (File.Exists(path))
            return Load(path);

        GridKeypair created = Generate();
        created.Save(path);
        return created;
    }

    private static string ComputeFingerprint(byte[] publicKey)
    {
        byte[] hash = SHA256.HashData(publicKey);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
