/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Xunit;
using Xunit.Sdk;

namespace osWebRtcVoice.Tests;

internal static class TestAssert
{
    public static void WithMessage(Action assertion, string message)
    {
        try
        {
            assertion();
        }
        catch (XunitException error)
        {
            Assert.Fail($"{message}{Environment.NewLine}{error}");
        }
    }
}
