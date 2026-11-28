/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// NUnit's default execution was serial; these tests also share a process-wide logger and static registries.
// Facts and theories carry the 60-second safety timeout, with longer overrides for opt-in gateway tests.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
[assembly: Xunit.AssemblyFixture(typeof(VoiceTestLoggingSetUp))]
