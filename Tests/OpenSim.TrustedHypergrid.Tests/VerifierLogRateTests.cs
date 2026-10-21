/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.TrustedHypergrid;
using Xunit;

namespace OpenSim.TrustedHypergrid.Tests;

/// <summary>
/// A remote grid chooses how often a call fails verification, so each failure line is written once in a
/// 60 second window and the rest are counted; the first call after the window writes the count. The test drives
/// <see cref="GridSignatureVerifier.Verify"/> as the hypergrid handlers do, with the gate's clock set by hand.
///
/// <para>The gate clock and the process's logger factory are process-wide; this assembly runs its classes one at a
/// time (AssemblyInfo.cs).</para>
/// </summary>
public class VerifierLogRateTests : IDisposable
{
    private const string Method = "get_region";
    private const int Calls = 200;

    private sealed class Capture : ILoggerFactory
    {
        public readonly List<(LogLevel Level, string Text)> Lines = new();
        public ILogger CreateLogger(string category) => new L(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class L : ILogger
        {
            private readonly Capture m_c;
            public L(Capture c) => m_c = c;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter)
            { lock (m_c.Lines) m_c.Lines.Add((logLevel, formatter(state, exception))); }
        }
    }

    private long m_ms = 9_000_000;
    private readonly Capture m_capture = new();
    private readonly ILoggerFactory m_previous;

    public VerifierLogRateTests()
    {
        m_previous = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = m_capture;
        RateLimitedLog.UseClockForTesting(() => m_ms);
    }

    public void Dispose()
    {
        RateLimitedLog.UseClockForTesting(null);
        LoggerProvider.LoggerFactory = m_previous;
    }

    private static Hashtable SampleParams() => new() { { "region_id", "abc" }, { "x", "1000" }, { "y", "2000" } };

    private List<(LogLevel Level, string Text)> Lines(string contains)
    {
        lock (m_capture.Lines) return m_capture.Lines.Where(l => l.Text.Contains(contains)).ToList();
    }

    /// <summary>
    /// Calls <paramref name="attempt"/> <see cref="Calls"/> times inside one window and expects a single full line
    /// at <paramref name="level"/>; then one more call after the window and expects the summary on one more line.
    /// </summary>
    private void AssertOneLineThenOneSummary(string text, LogLevel level, Action attempt)
    {
        for (int i = 0; i < Calls; i++)
        {
            attempt();
            m_ms += 100;                                    // 200 calls over 20 s
        }

        var inWindow = Lines(text);
        Assert.Single(inWindow);
        Assert.Equal(level, inWindow[0].Level);
        Assert.DoesNotContain("more like this", inWindow[0].Text);

        m_ms += 60_000;
        attempt();

        var all = Lines(text);
        Assert.Equal(2, all.Count);
        Assert.Equal(level, all[1].Level);
        // The first call was written; the other 199 were counted. The line before was written 20 s + 60 s ago.
        Assert.EndsWith($"({Calls - 1} more like this in the last 80 s)", all[1].Text);
    }

    [Fact]
    public void ABadSignatureWritesOneLineAMinute()
    {
        var signer = new GridSignatureSigner(GridKeypair.Generate());
        var verifier = new GridSignatureVerifier(lookup: null);
        Hashtable p = SampleParams();
        DateTime now = DateTime.UtcNow;

        AssertOneLineThenOneSummary("signature did not verify", LogLevel.Information, () =>
        {
            SignatureMaterial material = signer.Sign(Method, p, now);
            byte[] sig = Convert.FromBase64String(material.Signature);
            sig[0] ^= 0xFF;
            material.Signature = Convert.ToBase64String(sig);
            Assert.Equal(VerificationOutcome.Unverified, verifier.Verify(material, Method, p, now).Outcome);
        });
    }

    [Fact]
    public void AClockSkewWritesOneLineAMinute()
    {
        var signer = new GridSignatureSigner(GridKeypair.Generate());
        var verifier = new GridSignatureVerifier(lookup: null);
        Hashtable p = SampleParams();
        DateTime now = DateTime.UtcNow;

        AssertOneLineThenOneSummary("timestamp skew", LogLevel.Information, () =>
        {
            SignatureMaterial material = signer.Sign(Method, p, now);
            Assert.Equal(VerificationOutcome.Unverified, verifier.Verify(material, Method, p, now.AddMinutes(30)).Outcome);
        });
    }

    [Fact]
    public void AReplayedNonceWritesOneLineAMinute()
    {
        var signer = new GridSignatureSigner(GridKeypair.Generate());
        var verifier = new GridSignatureVerifier(lookup: null);
        Hashtable p = SampleParams();
        DateTime now = DateTime.UtcNow;
        SignatureMaterial material = signer.Sign(Method, p, now);
        Assert.Equal(VerificationOutcome.Verified, verifier.Verify(material, Method, p, now).Outcome);

        AssertOneLineThenOneSummary("nonce replay", LogLevel.Information, () =>
            Assert.Equal(VerificationOutcome.Unverified, verifier.Verify(material, Method, p, now).Outcome));
    }

    [Fact]
    public void MalformedBase64WritesOneLineAMinute()
    {
        var signer = new GridSignatureSigner(GridKeypair.Generate());
        var verifier = new GridSignatureVerifier(lookup: null);
        Hashtable p = SampleParams();
        DateTime now = DateTime.UtcNow;

        AssertOneLineThenOneSummary("malformed base64", LogLevel.Warning, () =>
        {
            SignatureMaterial material = signer.Sign(Method, p, now);
            material.Key = "not*base64*at*all";
            Assert.Equal(VerificationOutcome.Unverified, verifier.Verify(material, Method, p, now).Outcome);
        });
    }

    [Fact]
    public void AKeyOfTheWrongLengthWritesOneLineAMinute()
    {
        var signer = new GridSignatureSigner(GridKeypair.Generate());
        var verifier = new GridSignatureVerifier(lookup: null);
        Hashtable p = SampleParams();
        DateTime now = DateTime.UtcNow;

        AssertOneLineThenOneSummary("presented key was", LogLevel.Warning, () =>
        {
            SignatureMaterial material = signer.Sign(Method, p, now);
            material.Key = Convert.ToBase64String(new byte[5]);
            Assert.Equal(VerificationOutcome.Unverified, verifier.Verify(material, Method, p, now).Outcome);
        });
    }

    [Fact]
    public void AnUnparseableTimestampWritesOneLineAMinute()
    {
        var signer = new GridSignatureSigner(GridKeypair.Generate());
        var verifier = new GridSignatureVerifier(lookup: null);
        Hashtable p = SampleParams();
        DateTime now = DateTime.UtcNow;

        AssertOneLineThenOneSummary("unparseable timestamp", LogLevel.Warning, () =>
        {
            SignatureMaterial material = signer.Sign(Method, p, now);
            material.Timestamp = "last tuesday";
            Assert.Equal(VerificationOutcome.Unverified, verifier.Verify(material, Method, p, now).Outcome);
        });
    }

    private sealed class Unprintable
    {
        public override string ToString() => throw new InvalidOperationException("cannot be printed");
    }

    [Fact]
    public void AnUnexpectedFaultWritesOneLineAMinute()
    {
        var signer = new GridSignatureSigner(GridKeypair.Generate());
        var verifier = new GridSignatureVerifier(lookup: null);
        DateTime now = DateTime.UtcNow;

        AssertOneLineThenOneSummary("unexpected error during verification", LogLevel.Warning, () =>
        {
            Hashtable p = SampleParams();
            SignatureMaterial material = signer.Sign(Method, p, now);
            p["region_name"] = new Unprintable();           // the digest cannot be built
            Assert.Equal(VerificationOutcome.Unverified, verifier.Verify(material, Method, p, now).Outcome);
        });
    }

    [Fact]
    public void DifferentFailuresAreGatedApart()
    {
        var signer = new GridSignatureSigner(GridKeypair.Generate());
        var verifier = new GridSignatureVerifier(lookup: null);
        Hashtable p = SampleParams();
        DateTime now = DateTime.UtcNow;

        for (int i = 0; i < 50; i++)
        {
            SignatureMaterial skewed = signer.Sign(Method, p, now);
            verifier.Verify(skewed, Method, p, now.AddMinutes(30));
            SignatureMaterial bad = signer.Sign(Method, p, now);
            byte[] sig = Convert.FromBase64String(bad.Signature);
            sig[0] ^= 0xFF;
            bad.Signature = Convert.ToBase64String(sig);
            verifier.Verify(bad, Method, p, now);
        }

        Assert.Single(Lines("timestamp skew"));
        Assert.Single(Lines("signature did not verify"));
    }
}
