/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Tests.Common;

public class OpenSimTestCaseLifecycleTests
{
    [Fact]
    public async Task AsyncLifecycle_InvokesSetupAndSynchronousTeardownOverrides()
    {
        List<string> calls = [];
        var fixture = new RecordingFixture(calls);

        await fixture.InitializeAsync();
        await fixture.DisposeAsync();

        calls.Should().Equal("setup", "dispose");
    }

    private sealed class RecordingFixture(List<string> calls) : OpenSimTestCase
    {
        public override void SetUp() => calls.Add("setup");
        public override void Dispose() => calls.Add("dispose");
    }
}
