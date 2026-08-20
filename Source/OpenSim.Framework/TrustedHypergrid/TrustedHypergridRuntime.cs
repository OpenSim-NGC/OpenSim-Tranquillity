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

using System.IO;
using System.Reflection;
using log4net;
using Nini.Config;

namespace OpenSim.Framework.TrustedHypergrid;

/// <summary>
/// The operational holder for the local grid's Trusted Hypergrid identity (ADR-010 config surface;
/// Design Brief §8, D3). Built once from <c>[TrustedHypergrid]</c> in <c>Robust.HG.ini</c>.
///
/// When <see cref="Enabled"/> is false NOTHING is loaded — no key file is read or written, no
/// signer, no verifier — so behaviour is byte-identical to stock. When true, the keypair is loaded
/// from <c>PrivateKeyFile</c>, or generated and saved there on first run, and the fingerprint is
/// logged at INFO. The private key never touches the database and is never committed.
/// </summary>
public sealed class TrustedHypergridRuntime
{
    private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

    public const string ConfigSection = "TrustedHypergrid";
    public const string DefaultKeyFile = "TrustedHypergridSecret.ini";

    public bool Enabled { get; }
    public GridKeypair Keypair { get; }
    public GridSignatureSigner Signer { get; }
    public GridSignatureVerifier Verifier { get; }

    /// <summary>True when this load generated a fresh keypair rather than loading an existing one.</summary>
    public bool KeypairWasGenerated { get; }

    public string Fingerprint => Keypair?.Fingerprint;

    private TrustedHypergridRuntime()
    {
        Enabled = false;
    }

    private TrustedHypergridRuntime(GridKeypair keypair, GridSignatureVerifier verifier, bool generated)
    {
        Enabled = true;
        Keypair = keypair;
        Signer = new GridSignatureSigner(keypair);
        Verifier = verifier;
        KeypairWasGenerated = generated;
    }

    /// <summary>A disabled runtime: no key, no signer, no verifier.</summary>
    public static TrustedHypergridRuntime Disabled() => new();

    /// <summary>
    /// Build the runtime from config. <paramref name="lookup"/> resolves a verified fingerprint to
    /// a registry tier; null (the Slice 2b default) means a verified caller classifies as Open, which
    /// is all this slice does with the result.
    /// </summary>
    public static TrustedHypergridRuntime FromConfig(IConfigSource config, IGridTrustLookup lookup = null)
    {
        IConfig c = config?.Configs[ConfigSection];
        bool enabled = c != null && c.GetBoolean("Enabled", false);
        if (!enabled)
            return Disabled();

        string file = c.GetString("PrivateKeyFile", DefaultKeyFile);
        string path = Path.GetFullPath(file);

        GridKeypair keypair;
        bool generated;
        if (File.Exists(path))
        {
            keypair = GridKeypair.Load(path);
            generated = false;
            m_log.InfoFormat("[TRUSTED HG]: loaded grid identity from {0}, fingerprint {1}", file, keypair.Fingerprint);
        }
        else
        {
            keypair = GridKeypair.Generate();
            keypair.Save(path);
            generated = true;
            m_log.InfoFormat("[TRUSTED HG]: generated new grid identity at {0}, fingerprint {1}", file, keypair.Fingerprint);
        }

        return new TrustedHypergridRuntime(keypair, new GridSignatureVerifier(lookup), generated);
    }
}
