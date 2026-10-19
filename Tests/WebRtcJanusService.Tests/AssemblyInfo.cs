/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Assembly-wide safety backstop: every test in this assembly is timed out after 10s. A per-test
// attribute only protects tests you thought to mark; the failure it guards against (an async test
// wedged on a never-completing Task, with no runner timeout) is exactly the one you can't predict,
// so the default lives at ASSEMBLY scope. Individual tests may override with a longer timeout only
// if genuinely needed — none currently does (the whole suite runs in ~1s).
//
// TimeoutAttribute is [Obsolete] in NUnit 4 (superseded by CancelAfter for cooperative cancellation),
// but CancelAfter is class/method-only — Timeout is the only attribute valid at assembly scope, and
// it enforces on async tests (which is what the stall tests are). Suppress the obsolete warning.
#pragma warning disable CS0618
[assembly: NUnit.Framework.Timeout(10000)]
#pragma warning restore CS0618
