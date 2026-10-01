/*
 * Phlox Script Engine tests
 * Copyright (c) Legion Builds
 */

using System;
using System.Collections.Generic;
using System.Linq;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// String, list and statistics functions against SL's documented behaviour, or Halcyon's where the function is
/// InWorldz's own: iwIntRand, llStringTrim, llList2Key, llGetListEntryType, iwParseString2List, llListStatistics, and
/// the errors iwFormatString and llRefreshPrimURL give.
/// </summary>
// No test reaches a network service. Test grouping: no process-wide state, so the class runs in parallel.
public class StringListRowTests
{
    private const int DEBUG_CHANNEL = 0x7FFFFFFF;
    private const int TYPE_INTEGER = 1, TYPE_FLOAT = 2, TYPE_STRING = 3, TYPE_KEY = 4, TYPE_VECTOR = 5;

    private static LSLSystemAPI Api() => new LSLSystemAPI(null, null, 0, UUID.Random());
    private static LSLSystemAPI Api(SchedulerHarness h) => new LSLSystemAPI(null, h.Prim, h.Prim.LocalId, UUID.Random());
    private static LSLList L(params object[] items) => new LSLList(items);

    // ── iwIntRand ───────────────────────────────────────────────────────────

    [Fact]
    public void IwIntRandWithANegativeMaxStaysBetweenMaxAndZero()
    {
        // Halcyon (LSLSystemAPI.cs:792-795): max < 0 gives -Next(|max| + 1), one of max..0.
        var api = Api();
        var seen = new HashSet<int>();
        for (int i = 0; i < 2000; i++)
        {
            int v = api.iwIntRand(-5);
            Assert.InRange(v, -5, 0);
            seen.Add(v);
        }
        Assert.Equal(6, seen.Count);
    }

    [Fact]
    public void IwIntRandWithAPositiveMaxStaysBetweenZeroAndMax()
    {
        var api = Api();
        var seen = new HashSet<int>();
        for (int i = 0; i < 2000; i++)
        {
            int v = api.iwIntRand(3);
            Assert.InRange(v, 0, 3);
            seen.Add(v);
        }
        Assert.Equal(4, seen.Count);
    }

    // ── llStringTrim ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public void LlStringTrimLeavesTheStringAloneForAnUnknownType(int type)
    {
        // SL documents only STRING_TRIM_HEAD (1), STRING_TRIM_TAIL (2) and STRING_TRIM (3); Halcyon returns src for
        // anything else (LSLSystemAPI.cs:14109-14115).
        Assert.Equal("  a b  ", Api().llStringTrim("  a b  ", type));
    }

    [Theory]
    [InlineData(1, "a b  ")]
    [InlineData(2, "  a b")]
    [InlineData(3, "a b")]
    public void LlStringTrimTrimsForTheThreeKnownTypes(int type, string expected)
    {
        Assert.Equal(expected, Api().llStringTrim("  a b  ", type));
    }

    // ── llList2Key ──────────────────────────────────────────────────────────

    [Fact]
    public void LlList2KeyOutOfRangeIsTheNullString()
    {
        // SL: "If index describes a location not in src then null string is returned."
        var api = Api();
        Assert.Equal("", api.llList2Key(L("a"), 1));
        Assert.Equal("", api.llList2Key(L("a"), -2));
        Assert.Equal("", api.llList2Key(new LSLList(), 0));
    }

    [Fact]
    public void LlList2KeyOfAnElementThatCannotBeAKeyIsTheNullString()
    {
        // SL: "If it cannot be typecast null string is returned." Halcyon and YEngine give "" for every non-string.
        var api = Api();
        var src = L(5, 1.5f, new Vector3(1, 2, 3), Quaternion.Identity);
        for (int i = 0; i < 4; i++) Assert.Equal("", api.llList2Key(src, i));
    }

    [Fact]
    public void LlList2KeyOfAStringElementIsTheString()
    {
        string k = UUID.Random().ToString();
        var api = Api();
        Assert.Equal(k, api.llList2Key(L(k, "not a key"), 0));
        Assert.Equal("not a key", api.llList2Key(L(k, "not a key"), -1));
    }

    // ── llGetListEntryType ──────────────────────────────────────────────────

    [Fact]
    public void LlGetListEntryTypeReportsAKeyForAStringThatIsAUuid()
    {
        // Halcyon (LSLSystemAPI.cs:6626-6636) and YEngine: a string that parses as a UUID is TYPE_KEY.
        var api = Api();
        var src = L(UUID.Random().ToString(), "plain", 1, 2.5f, new Vector3(1, 2, 3));
        Assert.Equal(TYPE_KEY, api.llGetListEntryType(src, 0));
        Assert.Equal(TYPE_STRING, api.llGetListEntryType(src, 1));
        Assert.Equal(TYPE_INTEGER, api.llGetListEntryType(src, 2));
        Assert.Equal(TYPE_FLOAT, api.llGetListEntryType(src, 3));
        Assert.Equal(TYPE_VECTOR, api.llGetListEntryType(src, 4));
    }

    // ── iwParseString2List ──────────────────────────────────────────────────

    [Fact]
    public void IwParseString2ListKeepsEmptyMiddleAndLeadingFieldsWithoutKeepNulls()
    {
        // Halcyon (LSLSystemAPI.cs:7714-7718): `else if (cindex == 0 || keepNulls == true)` adds "" for every empty field
        // at a leading or doubled separator.
        var api = Api();
        Assert.Equal(new object[] { "a", "", "b" }, api.iwParseString2List("a,,b", L(","), new LSLList(), new LSLList()).Data);
        Assert.Equal(new object[] { "", "a" }, api.iwParseString2List(",a", L(","), new LSLList(), new LSLList()).Data);
    }

    [Fact]
    public void IwParseString2ListCountsEmptyFieldsTowardMaxSplits()
    {
        var api = Api();
        // The empty field is the second split, so parsing stops there. Halcyon's stop keeps the rest from the
        // separator it stopped at (str.Substring(cindex)), so the remainder starts with that comma.
        var got = api.iwParseString2List("a,,b,c", L(","), new LSLList(), L("maxsplits", 2)).Data;
        Assert.Equal(new object[] { "a", "", ",b,c" }, got);
    }

    // ── llListStatistics ────────────────────────────────────────────────────

    private const int LIST_STAT_STD_DEV = 5, LIST_STAT_GEOMETRIC_MEAN = 9, LIST_STAT_HARMONIC_MEAN = 100;

    [Fact]
    public void HarmonicMeanAnswersOnItsConstant100()
    {
        // LIST_STAT_HARMONIC_MEAN compiles to 100 (DefaultConstants.cs, as SL); 4 / (1 + 1/2 + 1/4 + 1/4) = 2.
        Assert.Equal(2f, Api().llListStatistics(LIST_STAT_HARMONIC_MEAN, L(1, 2, 4, 4)), 5);
    }

    [Fact]
    public void Operation10IsNotTheHarmonicMean()
    {
        Assert.Equal(0f, Api().llListStatistics(10, L(1, 2, 4, 4)));
    }

    [Fact]
    public void StdDevIsTheSampleStandardDeviation()
    {
        // SL: "Calculates the _sample_ standard deviation"; [1,2,3,4] gives 1.290994 (population would be 1.118034).
        Assert.Equal(1.290994f, Api().llListStatistics(LIST_STAT_STD_DEV, L(1, 2, 3, 4)), 5);
    }

    [Fact]
    public void StdDevOfOneNumberIsZero()
    {
        Assert.Equal(0f, Api().llListStatistics(LIST_STAT_STD_DEV, L(7)));
    }

    [Fact]
    public void GeometricMeanOfMixedSignsIsNotANumber()
    {
        // SL: "Geometric mean applies only to numbers of the same sign." YEngine and Halcyon: exp(log(product) / n),
        // NaN for a negative product. The port used |x| and answered 2.
        Assert.True(float.IsNaN(Api().llListStatistics(LIST_STAT_GEOMETRIC_MEAN, L(-1, 4))));
        Assert.Equal(2f, Api().llListStatistics(LIST_STAT_GEOMETRIC_MEAN, L(1, 4)), 5);
    }

    [Fact]
    public void HarmonicMeanWithAZeroIsZero()
    {
        // YEngine and Halcyon: n / sum(1/x); 1/0 is infinite, so the mean is 0. The port skipped the zero (answer 3).
        Assert.Equal(0f, Api().llListStatistics(LIST_STAT_HARMONIC_MEAN, L(0, 2, 2)));
    }

    // ── errors with Halcyon's text ──────────────────────────────────────────

    [Fact]
    public void IwFormatStringOver64kbGivesHalcyonsRuntimeError()
    {
        // Halcyon (LSLSystemAPI.cs:15955): LSLError("Return value from iwFormatString is greater than 64kb").
        using var h = new SchedulerHarness();
        string big = new string('x', 32760) + "{0}";
        Assert.Equal("", Api(h).iwFormatString(big, L(new string('y', 100))));
        Assert.Contains(h.SaidOn, s => s.Channel == DEBUG_CHANNEL
            && s.Message.Contains("LSL Runtime Error: Return value from iwFormatString is greater than 64kb"));
    }

    [Fact]
    public void LlRefreshPrimUrlSaysItIsNotSupported()
    {
        // Halcyon (LSLSystemAPI.cs:13444-13449): ScriptShoutError("llRefreshPrimURL - not yet supported").
        using var h = new SchedulerHarness();
        Api(h).llRefreshPrimURL();
        Assert.Contains(h.SaidOn, s => s.Channel == DEBUG_CHANNEL && s.Message.Contains("llRefreshPrimURL - not yet supported"));
    }
}
