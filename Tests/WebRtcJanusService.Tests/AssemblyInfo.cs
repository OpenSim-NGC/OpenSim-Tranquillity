/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Assembly-wide safety backstop: every test in this assembly is timed out after 60s. A per-test
// attribute only protects tests you thought to mark; the failure it guards against (an async test
// wedged on a never-completing Task, with no runner timeout) is exactly the one you can't predict,
// so the default lives at ASSEMBLY scope. It catches hangs; it does not measure speed. The first
// test in the assembly can take more than 10s on a loaded machine before it does any work (the
// assembly loads and JITs while many test hosts start at once), so the guard leaves room for that.
// Individual tests may override with a longer timeout only if genuinely needed — only the opt-in
// live-mixer arming test does, because it restarts a container (the rest of the suite runs in ~1s).
//
// TimeoutAttribute is [Obsolete] in NUnit 4 (superseded by CancelAfter for cooperative cancellation),
// but CancelAfter is class/method-only — Timeout is the only attribute valid at assembly scope, and
// it enforces on async tests (which is what the stall tests are). Suppress the obsolete warning.
#pragma warning disable CS0618
[assembly: NUnit.Framework.Timeout(60000)]
#pragma warning restore CS0618
