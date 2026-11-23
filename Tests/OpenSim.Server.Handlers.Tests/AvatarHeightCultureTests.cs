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

using System.Globalization;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Services.AvatarService;
using OpenSim.Services.Interfaces;
using Xunit;
using Assert = Xunit.Assert;
using TheoryAttribute = Xunit.TheoryAttribute;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// The avatar record's AvatarHeight goes between regions and Robust, which may run in different locales. Each server
/// writes it in its current culture, so a server in a comma-decimal locale writes "1,8" and one in an English or
/// invariant locale writes "1.8". It was also read in the current culture, so an English-locale reader took "1,8" as
/// 18, a comma-locale reader took "1.8" as 18, and Robust's range check then replaced the height with its default.
/// Both forms are now read the same in every culture. The written form is unchanged.
/// </summary>
public class AvatarHeightCultureTests
{
    private static readonly UUID Agent = new UUID("11111111-2222-3333-4444-555555555555");

    private static readonly string[] Cultures = { "de-DE", "fr-FR", "en-US", "" };

    private static T InCulture<T>(string name, Func<T> f)
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            return f();
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    private static AvatarAppearance Read(string stored)
    {
        AvatarData record = new AvatarData { AvatarType = 1, Data = new Dictionary<string, string>() };
        record.Data["Serial"] = "0";
        record.Data["AvatarHeight"] = stored;
        return record.ToAvatarAppearance();
    }

    private static float ReadHeight(string stored)
    {
        AvatarAppearance appearance = Read(stored);
        Assert.NotNull(appearance);
        return appearance.AvatarHeight;
    }

    private static AvatarAppearance OfHeight(float height)
    {
        AvatarAppearance appearance = new AvatarAppearance();
        appearance.SetSize(new Vector3(0.45f, 0.6f, height));
        return appearance;
    }

    private static string WrittenHeight(float height) => new AvatarData(OfHeight(height)).Data["AvatarHeight"];

    [Theory]
    [InlineData("de-DE", "1.8125")]
    [InlineData("de-DE", "1,8125")]
    [InlineData("fr-FR", "1.8125")]
    [InlineData("fr-FR", "1,8125")]
    [InlineData("en-US", "1.8125")]
    [InlineData("en-US", "1,8125")]
    [InlineData("", "1.8125")]
    [InlineData("", "1,8125")]
    public void AHeightWithADotOrACommaIsReadInEveryCulture(string culture, string stored)
    {
        Assert.Equal(1.8125f, InCulture(culture, () => ReadHeight(stored)));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    [InlineData("")]
    public void AHeightWrittenInAnyCultureReadsBackInEveryCulture(string writer)
    {
        string written = InCulture(writer, () => WrittenHeight(2.05f));
        foreach (string reader in Cultures)
            Assert.Equal(2.05f, InCulture(reader, () => ReadHeight(written)));
    }

    [Fact]
    public void TheWrittenFormIsUnchanged()
    {
        Assert.Equal("1,8125", InCulture("de-DE", () => WrittenHeight(1.8125f)));
        Assert.Equal("1.8125", InCulture("en-US", () => WrittenHeight(1.8125f)));
    }

    [Fact]
    public void AZeroHeightStillReadsAsTheDefault()
    {
        Assert.Equal(1.9f, InCulture("de-DE", () => ReadHeight("0")));
    }

    [Fact]
    public void AHeightThatIsNotANumberStillFailsTheRecord()
    {
        Assert.Null(InCulture("en-US", () => Read("tall")));
    }

    /// <summary>
    /// Robust's avatar service checks the height a region sends and stores its default (1.771488) for a value it
    /// cannot read or that is out of range. A region in another locale sent the other decimal mark, which it read as
    /// a height over 10.
    /// </summary>
    [Theory]
    [InlineData("de-DE", "en-US")]
    [InlineData("en-US", "de-DE")]
    [InlineData("fr-FR", "")]
    [InlineData("", "fr-FR")]
    [InlineData("de-DE", "de-DE")]
    [InlineData("en-US", "en-US")]
    public void RobustKeepsAHeightSentFromAnotherLocale(string region, string robust)
    {
        TestableAvatarService service = new TestableAvatarService(new RowTable());
        AvatarData sent = InCulture(region, () => new AvatarData(OfHeight(1.8125f)));

        Assert.True(InCulture(robust, () => service.SetAvatar(Agent, sent)));
        AvatarData stored = InCulture(robust, () => service.GetAvatar(Agent));

        foreach (string reader in Cultures)
            Assert.Equal(1.8125f, InCulture(reader, () => ReadHeight(stored.Data["AvatarHeight"])));
    }

    [Theory]
    [InlineData("en-US", "12.5")]
    [InlineData("de-DE", "12,5")]
    [InlineData("en-US", "-1")]
    [InlineData("en-US", "tall")]
    public void RobustStillReplacesAHeightOutOfRange(string robust, string height)
    {
        TestableAvatarService service = new TestableAvatarService(new RowTable());
        AvatarData sent = new AvatarData { AvatarType = 1, Data = new Dictionary<string, string>() };
        sent.Data["Serial"] = "0";
        sent.Data["AvatarHeight"] = height;

        Assert.True(InCulture(robust, () => service.SetAvatar(Agent, sent)));
        string stored = InCulture(robust, () => service.GetAvatar(Agent)).Data["AvatarHeight"];
        Assert.Equal(1.771488f, InCulture(robust, () => ReadHeight(stored)));
    }

    private sealed class TestableAvatarService : AvatarService
    {
        public TestableAvatarService(IAvatarData db) : base(new IniConfigSource(), db) { }
    }

    /// <summary>An avatars table: one value per (principal, name), as the stores keep it.</summary>
    private sealed class RowTable : IAvatarData
    {
        private readonly Dictionary<(UUID, string), string> m_rows = new();

        public AvatarBaseData[] Get(string field, string val)
        {
            Assert.Equal("PrincipalID", field);
            UUID id = new UUID(val);
            return m_rows.Where(r => r.Key.Item1 == id)
                .Select(r => new AvatarBaseData
                {
                    PrincipalID = id,
                    Data = new Dictionary<string, string> { ["Name"] = r.Key.Item2, ["Value"] = r.Value }
                })
                .ToArray();
        }

        public bool Store(AvatarBaseData data)
        {
            m_rows[(data.PrincipalID, data.Data["Name"])] = data.Data["Value"];
            return true;
        }

        public bool Delete(UUID principalID, string name) => m_rows.Remove((principalID, name));

        public bool Delete(string field, string val)
        {
            Assert.Equal("PrincipalID", field);
            UUID id = new UUID(val);
            foreach ((UUID, string) key in m_rows.Keys.Where(k => k.Item1 == id).ToList())
                m_rows.Remove(key);
            return true;
        }
    }
}
