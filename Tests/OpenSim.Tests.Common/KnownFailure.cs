/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Runtime.CompilerServices;
using Xunit;

namespace OpenSim.Tests.Common;

public static class KnownFailure
{
    public const string RunVariable = "RUN_KNOWN_FAILURES";

    public static void Skip(string reference, string reason)
    {
        SkipWhen(true, reference, reason);
    }

    public static void SkipWhen(bool condition, string reference, string reason)
    {
        if (!condition || IsRunEnabled())
            return;

        Assert.Skip(SkipReason(reference, reason));
    }

    internal static bool IsRunEnabled()
    {
        string value = Environment.GetEnvironmentVariable(RunVariable);
        return value == "1"
            || value?.Equals("true", StringComparison.OrdinalIgnoreCase) == true
            || value?.Equals("yes", StringComparison.OrdinalIgnoreCase) == true;
    }

    internal static string SkipReason(string reference, string reason)
        => $"{reference}: {reason}. Set {RunVariable}=1 to run this known failure.";
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class KnownFailureFactAttribute : FactAttribute
{
    public KnownFailureFactAttribute(
        string reference,
        string reason,
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!KnownFailure.IsRunEnabled())
            Skip = KnownFailure.SkipReason(reference, reason);
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class KnownFailureTheoryAttribute : TheoryAttribute
{
    public KnownFailureTheoryAttribute(
        string reference,
        string reason,
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!KnownFailure.IsRunEnabled())
            Skip = KnownFailure.SkipReason(reference, reason);
    }
}
