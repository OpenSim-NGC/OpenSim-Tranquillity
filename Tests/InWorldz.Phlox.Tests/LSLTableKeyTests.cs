/*
 * Phlox Script Engine tests
 * Copyright (c) Legion Builds
 */

using InWorldz.Phlox.Types;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Lua uses object identity for reference-type table keys. LSL lists retain content equality for .NET callers, but two
/// separately constructed lists with equal contents must remain distinct when used as SLua table keys.
/// </summary>
// No test reaches a network service. Test grouping: no process-wide state, so the class runs in parallel.
public class LSLTableKeyTests
{
    private static LSLList L(params object[] items) => new LSLList(items);

    [Fact]
    public void EqualListsAreDistinctTableKeys()
    {
        LSLList first = L(1, "two");
        LSLList second = L(1, "two");
        LSLTable table = new LSLTable();

        table.Set(first, "first");
        table.Set(second, "second");

        Assert.Equal(2, table.Count);
        Assert.Equal("first", table.Get(first));
        Assert.Equal("second", table.Get(second));
    }

    [Fact]
    public void RemovingAnEqualListKeyRemovesOnlyThatInstance()
    {
        LSLList first = L("same");
        LSLList second = L("same");
        LSLTable table = new LSLTable();
        table.Set(first, 1);
        table.Set(second, 2);

        table.Set(first, null);

        Assert.Equal(1, table.Count);
        Assert.Null(table.Get(first));
        Assert.Equal(2, table.Get(second));
        Assert.True(table.Next(null, out object key, out object value));
        Assert.Same(second, key);
        Assert.Equal(2, value);
    }

    [Fact]
    public void RebuildingKeepsEqualListInstancesAsSeparateKeys()
    {
        LSLList first = L(7);
        LSLList second = L(7);

        LSLTable table = new LSLTable(
            new object[] { first, second },
            new object[] { "first", "second" });

        Assert.Equal(2, table.Count);
        Assert.Equal("first", table.Get(first));
        Assert.Equal("second", table.Get(second));
        Assert.True(table.Next(first, out object key, out object value));
        Assert.Same(second, key);
        Assert.Equal("second", value);
    }
}
