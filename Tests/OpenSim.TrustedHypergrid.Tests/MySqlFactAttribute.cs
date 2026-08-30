using System;
using Xunit;

namespace OpenSim.TrustedHypergrid.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that runs only when <c>TRUSTED_HG_MYSQL_CONN</c> is set.
/// xUnit v2 has no runtime skip; setting <see cref="FactAttribute.Skip"/> from the attribute
/// constructor is evaluated at discovery, which gives a real skip (reported, never a silent pass)
/// without a static <c>Skip</c> string that has to be edited to run the MySQL path (LEDGER G-1).
/// </summary>
public sealed class MySqlFactAttribute : FactAttribute
{
    public const string EnvVar = "TRUSTED_HG_MYSQL_CONN";

    public MySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)))
            Skip = $"Requires a MySQL scratch database; set {EnvVar} to run.";
    }
}
