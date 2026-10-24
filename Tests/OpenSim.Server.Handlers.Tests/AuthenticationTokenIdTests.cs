/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using OpenMetaverse;
using OpenSim.Server.Handlers.Authentication;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// The authentication handler takes a token as an id and passes it to the service in canonical form.
/// </summary>
public class AuthenticationTokenIdTests
{
    private sealed class Calls
    {
        public readonly List<string> Seen = new();
        public bool Result = true;
    }

    private static (AuthenticationServerPostHandler Handler, Calls Calls) NewHandler()
    {
        Calls calls = new();
        IAuthenticationService service = StubProxy.Create<IAuthenticationService>((m, a) =>
        {
            if (m.Name == nameof(IAuthenticationService.Verify) || m.Name == nameof(IAuthenticationService.Release))
            {
                calls.Seen.Add(m.Name + ":" + a[1]);
                return (true, calls.Result);
            }
            return (false, null);
        });
        return (new AuthenticationServerPostHandler(service), calls);
    }

    private static string Post(AuthenticationServerPostHandler handler, string method, string token)
    {
        string body = "METHOD=" + method + "&PRINCIPAL=" + UUID.Random() + "&TOKEN=" + Uri.EscapeDataString(token);
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(body));
        return Encoding.UTF8.GetString(handler.Handle("/auth/plain", stream, null, null));
    }

    [Xunit.Theory]
    [Xunit.InlineData("verify", "Verify")]
    [Xunit.InlineData("release", "Release")]
    public void OrdinaryIdTokenReachesTheServiceAsIssued(string method, string serviceMethod)
    {
        (AuthenticationServerPostHandler handler, Calls calls) = NewHandler();
        string token = UUID.Random().ToString();

        string response = Post(handler, method, token);

        Xunit.Assert.Contains("Success", response);
        Xunit.Assert.Equal(new[] { serviceMethod + ":" + token }, calls.Seen);
    }

    [Xunit.Theory]
    [Xunit.InlineData("verify")]
    [Xunit.InlineData("release")]
    public void TokenThatIsNotAnIdNeverReachesTheService(string method)
    {
        (AuthenticationServerPostHandler handler, Calls calls) = NewHandler();

        foreach (string token in new[] { "x' or '1'='1", "x'; delete from tokens; --", "'", "", "not-an-id" })
            Xunit.Assert.DoesNotContain("Success", Post(handler, method, token));

        Xunit.Assert.Empty(calls.Seen);
    }

    [Xunit.Fact]
    public void IdTokenIsPassedOnInCanonicalForm()
    {
        (AuthenticationServerPostHandler handler, Calls calls) = NewHandler();
        UUID id = UUID.Random();

        Post(handler, "verify", "{" + id.ToString().ToUpperInvariant() + "}");

        Xunit.Assert.Equal(new[] { "Verify:" + id }, calls.Seen);
    }
}
