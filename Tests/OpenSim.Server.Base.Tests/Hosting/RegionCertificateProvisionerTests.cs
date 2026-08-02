/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using Nini.Config;
using OpenSim.Server.RegionServer;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

/// <summary>
/// Unit tests for <see cref="RegionCertificateProvisioner"/>, the extracted startup
/// certificate provisioning. The filesystem and certificate operations are replaced
/// with in-memory seams so the configuration-driven decision logic can be asserted
/// without touching disk or generating real certificates.
/// </summary>
public sealed class RegionCertificateProvisionerTests
{
    private sealed class Recorder
    {
        public List<string> SelfSignedArgs { get; } = new();
        public List<string> ConverterArgs { get; } = new();
        public int SelfSignedCalls { get; private set; }
        public int ConverterCalls { get; private set; }

        public void SelfSigned(string a, string b, string c, string d)
        {
            SelfSignedCalls++;
            SelfSignedArgs.AddRange(new[] { a, b, c, d });
        }

        public void Converter(string a, string b, string c, string d)
        {
            ConverterCalls++;
            ConverterArgs.AddRange(new[] { a, b, c, d });
        }
    }

    private static IConfig MakeConfig(Action<IConfig> configure)
    {
        var source = new IniConfigSource();
        IConfig startup = source.AddConfig("Startup");
        configure?.Invoke(startup);
        return startup;
    }

    private static (RegionCertificateProvisioner sut, Recorder rec) MakeSut(bool fileExists)
    {
        var rec = new Recorder();
        var sut = new RegionCertificateProvisioner
        {
            FileExists = _ => fileExists,
            CreateOrUpdateSelfsignedCert = rec.SelfSigned,
            ConvertPemToPKCS12 = rec.Converter,
        };
        return (sut, rec);
    }

    [Fact]
    public void Provision_WhenAllDisabled_DoesNothing()
    {
        var (sut, rec) = MakeSut(fileExists: false);

        sut.Provision(MakeConfig(_ => { }));

        Assert.Equal(0, rec.SelfSignedCalls);
        Assert.Equal(0, rec.ConverterCalls);
    }

    [Fact]
    public void Provision_SelfSigned_WhenFileMissing_CreatesWithDefaults()
    {
        var (sut, rec) = MakeSut(fileExists: false);

        sut.Provision(MakeConfig(c => c.Set("EnableSelfsignedCertSupport", "true")));

        Assert.Equal(1, rec.SelfSignedCalls);
        Assert.Equal(new[] { "OpenSim", "localhost", "127.0.0.1", "" }, rec.SelfSignedArgs);
    }

    [Fact]
    public void Provision_SelfSigned_WhenFileExistsAndNoRenew_DoesNotCreate()
    {
        var (sut, rec) = MakeSut(fileExists: true);

        sut.Provision(MakeConfig(c =>
        {
            c.Set("EnableSelfsignedCertSupport", "true");
            c.Set("CertRenewOnStartup", "false");
        }));

        Assert.Equal(0, rec.SelfSignedCalls);
    }

    [Fact]
    public void Provision_SelfSigned_WhenFileExistsAndRenew_Creates()
    {
        var (sut, rec) = MakeSut(fileExists: true);

        sut.Provision(MakeConfig(c =>
        {
            c.Set("EnableSelfsignedCertSupport", "true");
            c.Set("CertRenewOnStartup", "true");
        }));

        Assert.Equal(1, rec.SelfSignedCalls);
    }

    [Fact]
    public void Provision_SelfSigned_PassesConfiguredValues()
    {
        var (sut, rec) = MakeSut(fileExists: false);

        sut.Provision(MakeConfig(c =>
        {
            c.Set("EnableSelfsignedCertSupport", "true");
            c.Set("CertFileName", "MyCert");
            c.Set("CertHostName", "example.org");
            c.Set("CertHostIp", "10.0.0.1");
            c.Set("CertPassword", "secret");
        }));

        Assert.Equal(new[] { "MyCert", "example.org", "10.0.0.1", "secret" }, rec.SelfSignedArgs);
    }

    [Fact]
    public void Provision_Converter_WhenEnabled_ConvertsWithDefaults()
    {
        var (sut, rec) = MakeSut(fileExists: false);

        sut.Provision(MakeConfig(c => c.Set("EnableCertConverter", "true")));

        Assert.Equal(1, rec.ConverterCalls);
        Assert.Equal(new[] { "letsencrypt", "", "", "" }, rec.ConverterArgs);
    }

    [Fact]
    public void Provision_Converter_WhenDisabled_DoesNotConvert()
    {
        var (sut, rec) = MakeSut(fileExists: false);

        sut.Provision(MakeConfig(c => c.Set("EnableCertConverter", "false")));

        Assert.Equal(0, rec.ConverterCalls);
    }
}
