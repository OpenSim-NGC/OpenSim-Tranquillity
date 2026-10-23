/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Framework;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// <see cref="RestClient"/> builds its address from query parameters that callers may use for a credential, and
/// logs that address when a request fails.
/// </summary>
[Collection("LogCapture")]
public class RestClientCredentialLogTests
{
    [Xunit.Fact]
    public void FailedRequestLogsTheAddressWithoutItsQueryString()
    {
        string secret = "fake-query-secret-" + Guid.NewGuid().ToString("N");

        using LogCapture log = new();
        // Nothing listens on this loopback port, so the request is refused at once.
        using (RestClient client = new("http://127.0.0.1:9/service"))
        {
            client.AddQueryParameter("secret", secret);
            client.RequestMethod = "GET";
            Xunit.Assert.Null(client.Request(null));
        }

        Xunit.Assert.False(log.Contains(secret), "query parameter reached the log");
        Xunit.Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error,
            log.LevelOf("Error fetching resource from server: http://127.0.0.1:9/service "));
    }
}
