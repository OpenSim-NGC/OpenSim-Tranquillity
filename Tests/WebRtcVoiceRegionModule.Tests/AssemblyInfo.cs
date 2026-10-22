/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Assembly-wide safety backstop: every test in this assembly is timed out after 10s. Same rationale
// as WebRtcJanusService.Tests — the runner-hang failure is unpredictable, so the default is at
// assembly scope. This assembly has no never-completing-task tests today; the backstop is insurance.
// Timeout is [Obsolete] but the only assembly-scope timeout attribute (CancelAfter is class/method
// only); suppress the obsolete warning.
#pragma warning disable CS0618
[assembly: NUnit.Framework.Timeout(10000)]
#pragma warning restore CS0618
