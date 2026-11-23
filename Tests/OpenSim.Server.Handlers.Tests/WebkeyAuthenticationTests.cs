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

using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Services.AuthenticationService;
using OpenSim.Services.Interfaces;
using Xunit;
using Assert = Xunit.Assert;
using TheoryAttribute = Xunit.TheoryAttribute;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// Web login key authentication built a UUID from the presented key before anything else, so a key that is not a
/// UUID threw FormatException, which the Robust handler answered with a 500. WebkeyOrPasswordAuthenticationService
/// tries the key first, so a password that is not a UUID never reached the password check. A key that is not a UUID
/// now fails as a web login, and the password is tried. Uses the SQLite auth store (see SqliteQueryParameterTests).
/// </summary>
[Collection("SQLite stores")]
public class WebkeyAuthenticationTests
{
    private const string Plain = "correct horse";

    private static IniConfigSource Config()
    {
        MainConsole.Instance ??= new MockConsole();
        string dir = Environment.GetEnvironmentVariable("SQLITE_QUERY_TEST_DIR") is { Length: > 0 } d ? d : AppContext.BaseDirectory;
        IniConfigSource config = new();
        IConfig auth = config.AddConfig("AuthenticationService");
        auth.Set("StorageProvider", "OpenSim.Data.SQLite.dll");
        auth.Set("ConnectionString",
            "Data Source=" + Path.Combine(dir, "webkey-auth-" + Guid.NewGuid().ToString("N") + ".db") + ";Version=3");
        return config;
    }

    /// <summary>
    /// A principal with a web login key and a password hash that matches <see cref="Plain"/> as presented, which is
    /// not a UUID (a viewer sends the MD5 of the password, which is 32 hex digits and does parse as one).
    /// </summary>
    private static (UUID principal, UUID key) NewPrincipal(WebkeyAuthenticationService service)
    {
        UUID principal = UUID.Random();
        UUID key = UUID.Random();
        string salt = Util.Md5Hash(UUID.Random().ToString());
        Assert.True(service.SetAuthInfo(new AuthInfo
        {
            PrincipalID = principal,
            AccountType = "UserAccount",
            WebLoginKey = key.ToString(),
            PasswordHash = Util.Md5Hash(Plain + ":" + salt),
            PasswordSalt = salt,
        }));
        return (principal, key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData(Plain)]
    [InlineData("00000000-0000-0000-0000-00000000000g")]
    [InlineData("$1$5ebe2294ecd0e0f08eab7690d2a6ee69")]
    public void AKeyThatIsNotAUuidFailsAsAWebLogin(string presented)
    {
        WebkeyAuthenticationService service = new(Config());
        (UUID principal, UUID key) = NewPrincipal(service);

        Assert.Equal(string.Empty, service.Authenticate(principal, presented, 30));

        // The stored key is still there and still works once.
        Assert.NotEqual(string.Empty, service.Authenticate(principal, key.ToString(), 30));
        Assert.Equal(string.Empty, service.Authenticate(principal, key.ToString(), 30));
    }

    [Fact]
    public void AZeroOrWrongKeyStillFails()
    {
        WebkeyAuthenticationService service = new(Config());
        (UUID principal, _) = NewPrincipal(service);

        Assert.Equal(string.Empty, service.Authenticate(principal, UUID.Zero.ToString(), 30));
        Assert.Equal(string.Empty, service.Authenticate(principal, UUID.Random().ToString(), 30));
    }

    [Fact]
    public void APasswordThatIsNotAUuidIsTriedAsAPassword()
    {
        IniConfigSource config = Config();
        (UUID principal, UUID key) = NewPrincipal(new WebkeyAuthenticationService(config));
        WebkeyOrPasswordAuthenticationService service = new(config);

        Assert.NotEqual(string.Empty, service.Authenticate(principal, Plain, 30));
        Assert.Equal(string.Empty, service.Authenticate(principal, "wrong password", 30));

        // The web login key still works through the same service, once.
        Assert.NotEqual(string.Empty, service.Authenticate(principal, key.ToString(), 30));
        Assert.Equal(string.Empty, service.Authenticate(principal, key.ToString(), 30));
    }
}
