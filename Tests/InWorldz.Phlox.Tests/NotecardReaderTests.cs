/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using System.Text;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Tests.Common;
using Xunit;
using static InWorldz.Phlox.Tests.InventoryGivesRig;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// How a notecard's text is split into the lines every notecard reader answers (llGetNotecardLine,
/// llGetNumberOfNotecardLines, iwGetNotecardSegment, the iwGetLink variants, llGetNotecardLineSync and the
/// osGetNotecard functions).
/// - "Text length N" in the asset counts bytes of UTF-8, not characters: OSSL SaveNotecard writes the byte count
///   (OsMakeNotecardTests), and so does libomv's AssetNotecard.Encode. Text with characters of more than one
///   byte is read in full and nothing after it.
/// - A text that ends with a newline has no empty line after that newline. The SL wiki pages for llGetNotecardLine and
///   llGetNumberOfNotecardLines do not say; YEngine's reader (SLUtil.ParseNotecardToArray, which its NotecardCache
///   holds) ends the last line at the newline and adds no line after it. An empty notecard has no lines.
/// Each case is checked against the lines written and against SLUtil.ParseNotecardToArray on the same asset bytes.
/// Each test has its own harness and scene, and YEngine's parser is a pure function, so the class runs in parallel.
/// No network.
/// </summary>
public class NotecardReaderTests
{
    private const string EOF = "\n\n\n";
    private const string Reader = "default { dataserver(key q, string d) { llSay(0, \"ds [\" + d + \"]\"); } }";

    private static InventoryGivesRig Rig()
        => new InventoryGivesRig(Reader, cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "VeryHigh"));

    private static string Header(int bytes)
        => "Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length " + bytes + "\n";

    /// <summary>The asset as OSSL SaveNotecard writes it: the text, then "}".</summary>
    private static byte[] OsslForm(string text)
        => Encoding.UTF8.GetBytes(Header(Encoding.UTF8.GetByteCount(text)) + text + "}");

    /// <summary>The asset as libomv's AssetNotecard.Encode writes it: the text, then "}\n".</summary>
    private static byte[] LibomvForm(string text)
        => Encoding.UTF8.GetBytes(Header(Encoding.UTF8.GetByteCount(text)) + text + "}\n");

    private static void AddCard(InventoryGivesRig r, string name, byte[] data)
    {
        UUID asset = UUID.Random();
        r.H.Scene.AssetService.Store(AssetHelpers.CreateAsset(asset, AssetType.Notecard, data, r.H.Prim.OwnerID));
        r.H.Prim.Inventory.AddInventoryItem(new TaskInventoryItem
        {
            Name = name, AssetID = asset, ItemID = UUID.Random(), Type = (int)AssetType.Notecard, InvType = (int)InventoryType.Notecard
        }, true);
    }

    /// <summary>What the dataserver event carried for the read <paramref name="call"/> made.</summary>
    private static string Answer(InventoryGivesRig r, Func<global::Phlox.ScriptEngine.LSLSystemAPI, object> call)
    {
        r.H.ClearSaid(r.Item);
        r.Accounted(call);
        string said = r.WaitSaid("ds [");
        return said.Substring(4, said.Length - 5);
    }

    private static string Sync(InventoryGivesRig r, Func<global::Phlox.ScriptEngine.LSLSystemAPI, object> call)
        => (string)r.Accounted(call).Ret;

    /// <summary>Every reader of the notecard named <paramref name="name"/> answers exactly <paramref name="want"/>.</summary>
    private static void AssertReadAs(InventoryGivesRig r, string name, string[] want)
    {
        string n = want.Length.ToString();
        Assert.Equal(n, Answer(r, a => a.llGetNumberOfNotecardLines(name)));
        Assert.Equal(n, Answer(r, a => a.iwGetLinkNumberOfNotecardLines(1, name)));
        Assert.Equal(want.Length, (int)r.Accounted(a => a.osGetNumberOfNotecardLines(name)).Ret);
        for (int i = 0; i <= want.Length; i++)
        {
            int line = i;
            string expected = i < want.Length ? want[i] : EOF;
            Assert.Equal(expected, Answer(r, a => a.llGetNotecardLine(name, line)));
            Assert.Equal(expected, Answer(r, a => a.iwGetLinkNotecardLine(1, name, line)));
            Assert.Equal(expected, Answer(r, a => a.iwGetNotecardSegment(name, line, 0, 1024)));
            Assert.Equal(expected, Sync(r, a => a.llGetNotecardLineSync(name, line)));
            Assert.Equal(expected, Sync(r, a => a.osGetNotecardLine(name, line)));
        }
        Assert.Equal(string.Concat(want.Select(l => l + "\n")), Sync(r, a => a.osGetNotecard(name)));
    }

    public static TheoryData<string, string[]> Texts => new()
    {
        { "é", new[] { "é" } },                                                   // two bytes, one character
        { "日", new[] { "日" } },                                                  // three bytes, one character
        { "a\né\n", new[] { "a", "é" } },
        { "café\n日本語 line\nlast é", new[] { "café", "日本語 line", "last é" } },
        { "café\n日本語 line\nlast é\n", new[] { "café", "日本語 line", "last é" } },
        { "one\ntwo", new[] { "one", "two" } },
        { "one\ntwo\n", new[] { "one", "two" } },
        { "one\ntwo\n\n", new[] { "one", "two", "" } },
        { "\n\n", new[] { "", "" } },
        { "\nafter an empty line", new[] { "", "after an empty line" } },
        { "", new string[0] },
    };

    [Theory]
    [MemberData(nameof(Texts))]
    public void AnOsslWrittenNotecardIsReadAsItsLines(string text, string[] lines)
    {
        Assert.Equal(lines, SLUtil.ParseNotecardToArray(OsslForm(text)));   // YEngine reads it so
        using var r = Rig();
        AddCard(r, "card", OsslForm(text));
        AssertReadAs(r, "card", lines);
    }

    [Theory]
    [MemberData(nameof(Texts))]
    public void ALibomvEncodedNotecardIsReadAsItsLines(string text, string[] lines)
    {
        // libomv writes the byte count, the text and "}\n", then a 0 byte; for an empty text it writes "Text length -1".
        var encoded = new OpenMetaverse.Assets.AssetNotecard { BodyText = text };
        encoded.Encode();
        byte[] data = LibomvForm(text);
        if (text.Length > 0)
        {
            Assert.Equal(data.Append((byte)0), encoded.AssetData);
            data = encoded.AssetData;
        }
        Assert.Equal(lines, SLUtil.ParseNotecardToArray(data));   // YEngine reads it so
        using var r = Rig();
        AddCard(r, "card", data);
        AssertReadAs(r, "card", lines);
    }

    /// <summary>A read past the declared length never shows the "}" that closes the text, however many bytes its characters take.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(40)]
    public void TheClosingBraceIsNeverPartOfTheText(int twoByteCharacters)
    {
        string text = "head\n" + new string('é', twoByteCharacters);
        using var r = Rig();
        AddCard(r, "ossl", OsslForm(text));
        AddCard(r, "libomv", LibomvForm(text));
        AssertReadAs(r, "ossl", new[] { "head", new string('é', twoByteCharacters) });
        AssertReadAs(r, "libomv", new[] { "head", new string('é', twoByteCharacters) });
    }

    /// <summary>
    /// osMakeNotecard writes each list item and the string with a newline after it (OSSL_Api.cs osMakeNotecard); the
    /// script reads back exactly the items, or the string's lines, by line and whole.
    /// </summary>
    [Fact]
    public void WhatOsMakeNotecardWritesIsReadBackWholeAndByLine()
    {
        using var r = Rig();
        r.Accounted(a => { a.osMakeNotecard("list", L("first line", "line two, with a comma", "", "unicode é ü 日本", "last line")); return null; });
        r.Accounted(a => { a.osMakeNotecard("string", "string form 1\nstring form é 2"); return null; });
        r.Accounted(a => { a.osMakeNotecard("list", L("again")); return null; });

        AssertReadAs(r, "list", new[] { "first line", "line two, with a comma", "", "unicode é ü 日本", "last line" });
        AssertReadAs(r, "string", new[] { "string form 1", "string form é 2" });
        AssertReadAs(r, "list 1", new[] { "again" });
        Assert.Equal("string form 1\nstring form é 2\n", Sync(r, a => a.osGetNotecard("string")));
    }

    /// <summary>The same, from inside a script: osMakeNotecard, then llGetNotecardLine until EOF, as a region script does it.</summary>
    [Fact]
    public void AScriptReadsItsOwnNotecardBackLineByLine()
    {
        using var h = new SchedulerHarness(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "VeryHigh"));
        h.RezScript(@"list got; integer n; key q;
            default {
                state_entry() { osMakeNotecard(""made"", ""é one\n日本 two""); q = llGetNotecardLine(""made"", 0); }
                dataserver(key id, string d) {
                    if (id != q) return;
                    if (d == EOF) { llSay(0, ""lines "" + (string)llGetListLength(got) + "" ["" + llDumpList2String(got, ""|"") + ""]""); return; }
                    got += [d]; q = llGetNotecardLine(""made"", ++n);
                }
            }");
        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("lines "))), "said: " + string.Join(" | ", h.Said));
        Assert.Contains("lines 2 [é one|日本 two]", h.Said);
    }
}
