using System.Globalization;
using System.Linq;
using System.Reflection;
using InWorldz.Phlox.Compiler;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// SlConst is the implementation's one copy of the SL constants it tests against; the
/// compiler's table is what scripts see. They must agree name for name and value for value.
/// </summary>
public class SlConstTests
{
    internal static bool TryParseInt(string s, out int v)
    {
        s = s.Trim();
        if (s.StartsWith("0x") || s.StartsWith("0X"))
            return int.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return true;
        if (uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u)) { v = unchecked((int)u); return true; }
        return false;
    }

    [Fact]
    public void SlConstMatchesTable()
    {
        var t = typeof(global::Phlox.ScriptEngine.PhloxEngine).Assembly.GetType("Phlox.ScriptEngine.SlConst");
        Assert.NotNull(t);
        var fields = t!.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).ToList();
        Assert.NotEmpty(fields);
        var bad = fields.Select(f =>
        {
            if (!DefaultConstants.Constants.TryGetValue(f.Name, out var sym)) return f.Name + ": not in DefaultConstants";
            var mine = (int)f.GetRawConstantValue()!;
            if (!TryParseInt(sym.ConstValue, out var table)) return f.Name + ": table value '" + sym.ConstValue + "' is not an integer";
            return mine == table ? null : $"{f.Name}: SlConst {mine} vs table {table}";
        }).Where(s => s != null).ToList();
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }
}
