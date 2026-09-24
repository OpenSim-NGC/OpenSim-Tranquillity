using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The SLua front end on the same shapes as <see cref="ExpressionConformanceTests"/>: operator
/// chains, and/or chains and compound assignment. Expected values follow Lua 5.1 (reference
/// manual 2.5.6: + and - share a level and are left-associative, as are * / %; "and" binds
/// tighter than "or"; 2.5.3: and/or short-circuit and return an operand, not a boolean) and
/// Luau's compound assignment (luau.org/syntax: a op= b is a = a op b, evaluated once).
/// </summary>
public class SLuaExpressionTests
{
    public static IEnumerable<object[]> Cases() => new[]
    {
        new object[] { "p + q - r", "local p, q, r = 10, 5, 3\nll.Say(0, tostring(p + q - r))", "12" },
        new object[] { "a - b + c", "local a, b, c = 10, 5, 3\nll.Say(0, tostring(a - b + c))", "8" },
        new object[] { "a + b + c - d", "local a, b, c, d = 1, 2, 3, 4\nll.Say(0, tostring(a + b + c - d))", "2" },
        new object[] { "a + b - c + d - e", "local a, b, c, d, e = 20, 1, 2, 3, 4\nll.Say(0, tostring(a + b - c + d - e))", "18" },
        new object[] { "a * b / c", "local a, b, c = 6, 4, 3\nll.Say(0, tostring(a * b / c))", "8" },
        new object[] { "a % b * c", "local a, b, c = 7, 4, 3\nll.Say(0, tostring(a % b * c))", "9" },
        new object[] { "a + b * c - d / e", "local a, b, c, d, e = 1, 2, 3, 8, 4\nll.Say(0, tostring(a + b * c - d / e))", "5" },
        new object[] { "x and y and z", "local x, y, z = true, true, false\nll.Say(0, tostring(x and y and z))", "false" },
        new object[] { "1 and 2 and 3", "local a, b, c = 1, 2, 3\nll.Say(0, tostring(a and b and c))", "3" },
        new object[] { "nil or false or 5", "local a, b, c = nil, false, 5\nll.Say(0, tostring(a or b or c))", "5" },
        new object[] { "a or b and c", "local a, b, c = false, 2, 3\nll.Say(0, tostring(a or b and c))", "3" },
        new object[] { "a and b or c", "local a, b, c = false, 2, 3\nll.Say(0, tostring(a and b or c))", "3" },
        new object[] { "compound += -= *= /= %=",
            "local a = 10\na += 5\na -= 3\na *= 2\na /= 4\na %= 4\nll.Say(0, tostring(a))", "2" },
        new object[] { "compound ..=", "local s = \"a\"\ns ..= \"b\"\ns ..= \"c\"\nll.Say(0, s)", "abc" },
        new object[] { "comparison chain", "local a, b = 1, 2\nll.Say(0, tostring(a < b == true))", "true" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void SLua_(string name, string body, string expect)
    {
        var r = ExprRunner.RunSLua(body);
        Assert.True(r.Ok && r.Said.SequenceEqual(new[] { expect }), $"{name}: got {r.Describe()} (expect [{expect}])");
    }
}
