/*
 * Phlox Script Engine tests
 * Copyright (c) Legion Builds
 */

using System.Collections.Generic;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// LSLList overrides Object.Equals, so its hash code has to agree with that equality: two lists that compare equal
/// must hash the same, and a hashed collection must find a list by an equal one.
/// </summary>
// No test reaches a network service. Test grouping: no process-wide state, so the class runs in parallel.
public class LSLListHashTests
{
    private static LSLList L(params object[] items) => new LSLList(items);

    [Fact]
    public void EqualListsShareAHashCode()
    {
        LSLList left = L(1, "two", 3.0f);
        LSLList right = L(1, "two", 3.0f);

        Assert.True(left.Equals(right));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void AnEqualListFindsAHashedEntry()
    {
        Dictionary<LSLList, string> byList = new()
        {
            [L("key", 7)] = "found"
        };

        Assert.True(byList.TryGetValue(L("key", 7), out string value));
        Assert.Equal("found", value);
        Assert.False(byList.ContainsKey(L("key", 8)));
    }

    [Fact]
    public void AnEmptyListHashesWithoutMembers()
    {
        Assert.Equal(L().GetHashCode(), L().GetHashCode());
        Assert.True(new HashSet<LSLList> { L(), L() }.Count == 1);
    }

    [Fact]
    public void ListsOfDifferentLengthsRemainDistinct()
    {
        UUID id = UUID.Random();

        Assert.False(L(id).Equals(L(id, id)));
        Assert.NotEqual(L(id).GetHashCode(), L(id, id).GetHashCode());
    }
}
