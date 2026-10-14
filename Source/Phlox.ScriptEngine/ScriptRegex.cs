/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Every regular expression built from script input runs with a match timeout, so a
 * pattern with catastrophic backtracking - (a+)+$ against a long run of a's - costs a quarter of a
 * second and a "regex timed out" error instead of holding the scheduler (or chat delivery) for
 * minutes.
 */
using System;
using System.Text.RegularExpressions;

namespace Phlox.ScriptEngine
{
    internal static class ScriptRegex
    {
        public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

        public const string TimedOutMessage = "regex timed out";

        public static Regex Create(string pattern, RegexOptions options = RegexOptions.None)
            => new Regex(pattern ?? string.Empty, options, MatchTimeout);
    }
}
