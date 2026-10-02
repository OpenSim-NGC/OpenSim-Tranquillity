/*
 * Phlox Script Engine tests
 * Copyright (c) Legion Builds
 */

using System;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The JSON functions against the SL wiki: llJsonGetValue ("true" gives JSON_TRUE, "True" JSON_INVALID; null gives
/// JSON_NULL), llJsonValueType, llJson2List ("a list with 1 item" for a single value; nested values "returned as json
/// strings"), llList2Json (string items "are interpreted as JSON"; true/false/null become literals; strings are trimmed;
/// "Strings containing valid JSON numbers convert to JSON strings"; a JSON_OBJECT list must be strided key, value pairs)
/// and llJsonSetValue (empty input starts an array; plain true/false/null become literals; a type mismatch
/// replaces the subtree, as YEngine).
/// </summary>
// No process-wide state: the class runs in parallel.
public class JsonConformanceTests
{
    private const string JSON_INVALID = "﷐", JSON_OBJECT = "﷑", JSON_ARRAY = "﷒", JSON_NUMBER = "﷓",
        JSON_STRING = "﷔", JSON_NULL = "﷕", JSON_TRUE = "﷖", JSON_FALSE = "﷗";

    private static LSLSystemAPI Api() => new LSLSystemAPI(null, null, 0, UUID.Random());
    private static LSLList L(params object[] items) => new LSLList(items);
    private static readonly LSLList None = new LSLList();

    // ── llJsonGetValue ──────────────────────────────────────────────────────

    [Fact]
    public void GetValueGivesTheSentinelsForTrueFalseAndNull()
    {
        var api = Api();
        string j = "{\"t\":true,\"f\":false,\"n\":null}";
        Assert.Equal(JSON_TRUE, api.llJsonGetValue(j, L("t")));
        Assert.Equal(JSON_FALSE, api.llJsonGetValue(j, L("f")));
        Assert.Equal(JSON_NULL, api.llJsonGetValue(j, L("n")));
    }

    [Fact]
    public void GetValueReadsATopLevelScalar()
    {
        // SL: llJsonGetValue("true", []) is JSON_TRUE; "True" and "TRUE" are JSON_INVALID.
        var api = Api();
        Assert.Equal(JSON_TRUE, api.llJsonGetValue("true", None));
        Assert.Equal(JSON_INVALID, api.llJsonGetValue("True", None));
        Assert.Equal(JSON_INVALID, api.llJsonGetValue("TRUE", None));
        Assert.Equal("3.14", api.llJsonGetValue("\"3.14\"", None));
    }

    [Fact]
    public void GetValueNeedsAnIntegerForAnArrayAndAStringForAnObject()
    {
        var api = Api();
        Assert.Equal(JSON_INVALID, api.llJsonGetValue("{\"0\":\"a\"}", L(0)));
        Assert.Equal(JSON_INVALID, api.llJsonGetValue("[\"a\",\"b\"]", L("1")));
        Assert.Equal("a", api.llJsonGetValue("{\"0\":\"a\"}", L("0")));
        Assert.Equal("b", api.llJsonGetValue("[\"a\",\"b\"]", L(1)));
    }

    [Fact]
    public void GettersSkipAByteOrderMark()
    {
        var api = Api();
        string j = "﻿{\"a\":[1,2]}";
        Assert.Equal("2", api.llJsonGetValue(j, L("a", 1)));
        Assert.Equal(JSON_ARRAY, api.llJsonValueType(j, L("a")));
        Assert.Equal(new object[] { "a", "[1,2]" }, api.llJson2List(j).Data);
    }

    // ── llJsonValueType ─────────────────────────────────────────────────────

    [Fact]
    public void ValueTypeReportsEachKind()
    {
        var api = Api();
        string j = "{\"s\":\"abc\",\"q\":\"5\",\"w\":\"true\",\"n\":5,\"o\":{},\"a\":[],\"t\":true,\"f\":false,\"z\":null}";
        Assert.Equal(JSON_STRING, api.llJsonValueType(j, L("s")));
        Assert.Equal(JSON_STRING, api.llJsonValueType(j, L("q")));
        Assert.Equal(JSON_STRING, api.llJsonValueType(j, L("w")));
        Assert.Equal(JSON_NUMBER, api.llJsonValueType(j, L("n")));
        Assert.Equal(JSON_OBJECT, api.llJsonValueType(j, L("o")));
        Assert.Equal(JSON_ARRAY, api.llJsonValueType(j, L("a")));
        Assert.Equal(JSON_TRUE, api.llJsonValueType(j, L("t")));
        Assert.Equal(JSON_FALSE, api.llJsonValueType(j, L("f")));
        Assert.Equal(JSON_NULL, api.llJsonValueType(j, L("z")));
        Assert.Equal(JSON_INVALID, api.llJsonValueType(j, L("missing")));
    }

    // ── llJson2List ─────────────────────────────────────────────────────────

    [Fact]
    public void Json2ListGivesTheSentinelsForTrueFalseAndNull()
    {
        Assert.Equal(new object[] { JSON_TRUE, JSON_FALSE, JSON_NULL, 1 }, Api().llJson2List("[true,false,null,1]").Data);
    }

    [Fact]
    public void Json2ListOfASingleValueIsAOneItemList()
    {
        var api = Api();
        Assert.Equal(new object[] { 5 }, api.llJson2List("5").Data);
        Assert.Equal(new object[] { "abc" }, api.llJson2List("\"abc\"").Data);
        Assert.Equal(new object[] { JSON_TRUE }, api.llJson2List("true").Data);
        Assert.Equal(new object[] { JSON_NULL }, api.llJson2List("null").Data);
    }

    [Fact]
    public void Json2ListOfTextThatIsNotJsonIsThatText()
    {
        // Halcyon (LSLSystemAPI.cs:15296-15305) and YEngine: the text as a one-item list.
        Assert.Equal(new object[] { "hello there" }, Api().llJson2List("hello there").Data);
    }

    [Fact]
    public void Json2ListKeepsNestedValuesAsJsonText()
    {
        // SL's example: {"pi": 3.14, "set": [1,2,3], "status": "ok"}.
        var got = Api().llJson2List("{\"pi\": 3.14, \"set\": [1,2,3], \"status\": \"ok\"}").Data;
        Assert.Equal("pi", got[0]);
        Assert.Equal(3.14f, (float)got[1], 5);
        Assert.Equal("set", got[2]);
        Assert.Equal("[1,2,3]", got[3]);
        Assert.Equal(new object[] { "status", "ok" }, new[] { got[4], got[5] });
    }

    // ── llList2Json ─────────────────────────────────────────────────────────

    [Fact]
    public void List2JsonWritesTheSentinelsAndPlainWordsAsLiterals()
    {
        Assert.Equal("[true,false,null,true,false,null]",
            Api().llList2Json(JSON_ARRAY, L(JSON_TRUE, JSON_FALSE, JSON_NULL, "true", "false", "null")));
    }

    [Fact]
    public void List2JsonEmbedsNestedJson()
    {
        Assert.Equal("[[1,2],{\"a\":1},\"quoted\"]",
            Api().llList2Json(JSON_ARRAY, L("[1,2]", "{\"a\":1}", "\"quoted\"")));
    }

    [Fact]
    public void List2JsonQuotesNumberLookingStringsAndOtherText()
    {
        // SL: "Strings containing valid JSON numbers convert to JSON strings". NaN, Infinity and 1,000 were
        // written bare, which no JSON reader accepts.
        Assert.Equal("[\"5\",\"NaN\",\"Infinity\",\"1,000\",\"bacon\",5,1.5]",
            Api().llList2Json(JSON_ARRAY, L("5", "NaN", "Infinity", "1,000", "bacon", 5, 1.5f)));
    }

    [Fact]
    public void List2JsonEscapesControlCharacters()
    {
        string got = Api().llList2Json(JSON_OBJECT, L("k\"ey", "a\nb\tc\\d\u0001"));
        Assert.Equal("{\"k\\\"ey\":\"a\\nb\\tc\\\\d\\u0001\"}", got);
        Assert.Equal("a\nb\tc\\d\u0001", Api().llJsonGetValue(got, L("k\"ey")));
    }

    [Fact]
    public void List2JsonTrimsStrings()
    {
        Assert.Equal("[\"a\",true]", Api().llList2Json(JSON_ARRAY, L("  a  ", " true ")));
    }

    [Fact]
    public void List2JsonObjectWithAnOddListIsInvalid()
    {
        Assert.Equal(JSON_INVALID, Api().llList2Json(JSON_OBJECT, L("a", 1, "b")));
        Assert.Equal("{\"a\":1}", Api().llList2Json(JSON_OBJECT, L("a", 1)));
    }

    [Fact]
    public void List2JsonWritesNonFiniteFloatsAsStrings()
    {
        string got = Api().llList2Json(JSON_ARRAY, L(float.NaN, float.PositiveInfinity));
        Assert.Equal(JSON_ARRAY, Api().llJsonValueType(got, None));
        Assert.Equal(JSON_STRING, Api().llJsonValueType(got, L(0)));
    }

    // ── llJsonSetValue (Q37 (b)) ────────────────────────────────────────────

    [Fact]
    public void SetValueOnEmptyInputStartsAnArray()
    {
        Assert.Equal("[\"x\"]", Api().llJsonSetValue("", L(0), "x"));
    }

    [Fact]
    public void SetValueWritesPlainTrueFalseNullAsLiterals()
    {
        var api = Api();
        Assert.Equal("{\"a\":true}", api.llJsonSetValue("{}", L("a"), "true"));
        Assert.Equal("{\"a\":false}", api.llJsonSetValue("{}", L("a"), "false"));
        Assert.Equal("{\"a\":null}", api.llJsonSetValue("{}", L("a"), "null"));
        Assert.Equal("{\"a\":true}", api.llJsonSetValue("{}", L("a"), JSON_TRUE));
    }

    [Fact]
    public void SetValueKeepsAQuotedValueAsAString()
    {
        // Ruled: quoted values stay strings, quotes included (YEngine), not Halcyon's quote-stripping.
        Assert.Equal("{\"a\":\"\\\"x\\\"\"}", Api().llJsonSetValue("{}", L("a"), "\"x\""));
    }

    [Fact]
    public void SetValueWritesOnlyRealNumbersBare()
    {
        var api = Api();
        Assert.Equal("{\"a\":-1.5e3}", api.llJsonSetValue("{}", L("a"), "-1.5e3"));
        Assert.Equal("{\"a\":\"NaN\"}", api.llJsonSetValue("{}", L("a"), "NaN"));
        Assert.Equal("{\"a\":\"1,000\"}", api.llJsonSetValue("{}", L("a"), "1,000"));
        Assert.Equal("{\"a\":\"line\\nbreak\"}", api.llJsonSetValue("{}", L("a"), "line\nbreak"));
    }

    [Fact]
    public void SetValueReplacesAMismatchedSubtree()
    {
        // YEngine's subtree replacement, which Phlox keeps.
        Assert.Equal("{\"a\":[\"x\"]}", Api().llJsonSetValue("{\"a\":5}", L("a", 0), "x"));
    }
}
