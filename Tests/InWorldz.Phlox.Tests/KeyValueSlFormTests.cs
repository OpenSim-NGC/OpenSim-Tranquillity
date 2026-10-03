using System.Collections.Concurrent;
using System.Diagnostics;
using OpenMetaverse;
using OpenSim.Services.Interfaces;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Experience key-value calls take SL's form (calls return a request key, the answer arrives in a
/// dataserver event). Each SL wiki page gives the answer as cdl = llDumpList2String([ 1, ... ],",") on success and
/// [ 0, integer error ] (XP_ERROR_*) on failure. The store is NGC's IExperienceService, here an in-memory fake that
/// answers with NGC's own statuses (Source/OpenSim.Services.ExperienceService/ExperienceService.cs:268-326). Each test
/// builds its own harness and registers the fake on its own scene only, so the class runs in parallel. No network.
/// </summary>
public class KeyValueSlFormTests
{
    private readonly ITestOutputHelper _out;
    public KeyValueSlFormTests(ITestOutputHelper o) => _out = o;

    private const long PhloxQuota = 128L * 1024 * 1024;   // LSLSystemAPI.MAX_DATA_QUOTA

    /// <summary>NGC ExperienceService's key-value logic over a dictionary, plus test knobs.</summary>
    internal sealed class FakeStore : IExperienceService   // DataserverToPrimTests uses it too
    {
        public readonly ConcurrentDictionary<UUID, SortedDictionary<string, string>> Data = new();
        public readonly ConcurrentQueue<UUID> ExperiencesSeen = new();
        public int ServiceQuota = int.MaxValue;         // NGC's MAX_QUOTA
        public int ExtraReportedSize;                   // added to GetSize, to reach Phlox's 128 MiB check
        public bool Throw;
        public ManualResetEventSlim ReadGate;           // a read waits here when set
        public readonly ManualResetEventSlim ReadEntered = new(false);

        private SortedDictionary<string, string> Of(UUID e)
        {
            ExperiencesSeen.Enqueue(e);
            if (Throw) throw new InvalidOperationException("store down");
            return Data.GetOrAdd(e, _ => new SortedDictionary<string, string>(StringComparer.Ordinal));
        }

        private static int Size(SortedDictionary<string, string> d) { lock (d) return d.Sum(p => p.Key.Length + p.Value.Length); }

        public string GetKeyValue(UUID experience, string key)
        {
            var d = Of(experience);
            if (ReadGate != null) { ReadEntered.Set(); ReadGate.Wait(TimeSpan.FromSeconds(30)); }
            lock (d) return d.TryGetValue(key, out var v) ? v : null;
        }

        public string CreateKeyValue(UUID experience, string key, string value)
        {
            var d = Of(experience);
            lock (d)
            {
                if (Size(d) + key.Length + value.Length > ServiceQuota) return "full";
                if (d.ContainsKey(key)) return "exists";
                d[key] = value;
                return "success";
            }
        }

        public string UpdateKeyValue(UUID experience, string key, string val, bool check, string original)
        {
            var d = Of(experience);
            lock (d)
            {
                if (!d.TryGetValue(key, out var get)) return "missing";
                if (check && get != original) return "mismatch";
                if (Size(d) - get.Length + val.Length > ServiceQuota) return "full";
                d[key] = val;
                return "success";
            }
        }

        public string DeleteKey(UUID experience, string key)
        {
            var d = Of(experience);
            lock (d) return d.Remove(key) ? "success" : "missing";
        }

        public int GetKeyCount(UUID experience) { var d = Of(experience); lock (d) return d.Count; }

        public string[] GetKeys(UUID experience, int start, int count)
        {
            var d = Of(experience);
            lock (d) return d.Keys.Skip(start).Take(count).ToArray();
        }

        public int GetSize(UUID experience) => Size(Of(experience)) + ExtraReportedSize;

        public Dictionary<UUID, bool> FetchExperiencePermissions(UUID agent_id) => new();
        public bool UpdateExperiencePermissions(UUID agent_id, UUID experience, ExperiencePermission perm) => false;
        public ExperienceInfo[] GetExperienceInfos(UUID[] experiences) => Array.Empty<ExperienceInfo>();
        public UUID[] GetAgentExperiences(UUID agent_id) => Array.Empty<UUID>();
        public ExperienceInfo UpdateExperienceInfo(ExperienceInfo info) => info;
        public ExperienceInfo[] FindExperiencesByName(string search) => Array.Empty<ExperienceInfo>();
        public UUID[] GetGroupExperiences(UUID group_id) => Array.Empty<UUID>();
        public UUID[] GetExperiencesForGroups(UUID[] groups) => Array.Empty<UUID>();
    }

    /// <summary>
    /// A script that asks on touch; every answer is said as "tag|data", matched to its call by the returned key. Chat
    /// carries at most 1024 bytes (SL llSay), so an answer over 1000 characters is said as "tag|first field,#fields/characters".
    /// </summary>
    private static string Script(string calls) => @"
list q;
ask(key k, string tag) { q += [k, tag]; if (k) return; llSay(0, ""not-a-key|"" + tag); }
default
{
    touch_start(integer n)
    {
" + calls + @"
        llSay(0, ""asked"");
    }
    dataserver(key id, string data)
    {
        integer i = llListFindList(q, [id]);
        if (i < 0) llSay(0, ""unknown|"" + data);
        else if (llStringLength(data) > 1000)
        {
            list f = llParseStringKeepNulls(data, ["",""], []);
            llSay(0, llList2String(q, i + 1) + ""|"" + llList2String(f, 0) + "",#"" + (string)llGetListLength(f)
                + ""/"" + (string)llStringLength(data));
        }
        else llSay(0, llList2String(q, i + 1) + ""|"" + data);
    }
}";

    private sealed class Run : IDisposable
    {
        public SchedulerHarness H;
        public UUID Item;
        public void Dispose() => H.Dispose();
        public string Answer(string tag) => H.Said.Single(s => s.StartsWith(tag + "|")).Substring(tag.Length + 1);
    }

    private static UUID Exp = new("63636363-0000-4000-8000-00000000e001");

    /// <summary>Rez, give the script its Experience, touch, and wait for <paramref name="answers"/> answers.</summary>
    private Run Ask(FakeStore store, string calls, int answers, UUID? experience = null, string deferral = "auto")
    {
        var r = new Run { H = new SchedulerHarness(cfg => cfg.Configs["InWorldz.Phlox"].Set("ServiceCallDeferral", deferral)) };
        if (store != null) r.H.Scene.RegisterModuleInterface<IExperienceService>(store);
        r.Item = r.H.RezScript(Script(calls));
        r.H.Prim.Inventory.GetInventoryItem(r.Item).ExperienceID = experience ?? Exp;
        Assert.True(r.H.PumpUntil(() => r.H.InterpreterFor(r.Item) != null), "not loaded: " + r.H.Diagnose(r.Item));
        r.H.PostTouch(r.Item);
        bool done = r.H.PumpUntil(() => r.H.Said.Contains("asked") && r.H.Said.Count(s => s.Contains('|')) >= answers,
            TimeSpan.FromSeconds(30));
        _out.WriteLine(string.Join("\n", r.H.Said));
        Assert.True(done, "answers missing: " + string.Join(" | ", r.H.Said) + " " + r.H.Diagnose(r.Item));
        Assert.DoesNotContain(r.H.Said, s => s.StartsWith("not-a-key|") || s.StartsWith("unknown|"));
        return r;
    }

    [Theory]
    [InlineData("never")]
    [InlineData("always")]
    [InlineData("auto")]
    public void CreateReadUpdateDeleteAnswerInDataserver(string deferral)
    {
        var store = new FakeStore();
        using var r = Ask(store, @"
        ask(llCreateKeyValue(""a"", ""one""), ""create"");
        ask(llReadKeyValue(""a""), ""read"");
        ask(llUpdateKeyValue(""a"", ""two"", FALSE, """"), ""update"");
        ask(llUpdateKeyValue(""a"", ""three"", TRUE, ""two""), ""checked"");
        ask(llUpdateKeyValue(""a"", ""four"", ""three""), ""update3"");
        ask(llReadKeyValue(""a""), ""read2"");
        ask(llDeleteKeyValue(""a""), ""delete"");
        ask(llReadKeyValue(""a""), ""read3"");", 8, deferral: deferral);
        Assert.Equal("1,one", r.Answer("create"));
        Assert.Equal("1,one", r.Answer("read"));
        Assert.Equal("1,two", r.Answer("update"));
        Assert.Equal("1,three", r.Answer("checked"));
        Assert.Equal("1,four", r.Answer("update3"));
        Assert.Equal("1,four", r.Answer("read2"));
        Assert.Equal("1,four", r.Answer("delete"));           // [ 1, string value ]: the value deleted
        Assert.Equal("0,14", r.Answer("read3"));              // XP_ERROR_KEY_NOT_FOUND
        // One script's answers come in the order it asked.
        var order = r.H.Said.Where(s => s.Contains('|')).Select(s => s.Substring(0, s.IndexOf('|'))).ToArray();
        Assert.Equal(new[] { "create", "read", "update", "checked", "update3", "read2", "delete", "read3" }, order);
        Assert.Empty(store.Data[Exp]);
    }

    [Fact]
    public void TheCallsReturnDistinctKeysAtOnce()
    {
        using var r = Ask(new FakeStore(), @"
        key a = llCreateKeyValue(""a"", ""1"");
        key b = llReadKeyValue(""a"");
        key c = llKeyCountKeyValue();
        key d = llKeysKeyValue(0, 1);
        key e = llDataSizeKeyValue();
        key f = llDeleteKeyValue(""a"");
        key g = llUpdateKeyValue(""a"", ""2"", FALSE, """");
        list all = [a, b, c, d, e, f, g];
        q = [a, ""a"", b, ""b"", c, ""c"", d, ""d"", e, ""e"", f, ""f"", g, ""g""];
        integer i; integer distinct = TRUE;
        for (i = 0; i < 7; i++) if (llListFindList(llDeleteSubList(all, i, i), [llList2Key(all, i)]) != -1) distinct = FALSE;
        integer allKeys = 0;
        for (i = 0; i < 7; i++) if (llList2Key(all, i)) allKeys++;
        llSay(0, ""keys|"" + (string)allKeys + "","" + (string)distinct);", 8);
        Assert.Equal("7,1", r.Answer("keys"));
    }

    [Fact]
    public void CreateAnExistingKeyIsStorageException()
    {
        using var r = Ask(new FakeStore(), @"
        ask(llCreateKeyValue(""a"", ""1""), ""first"");
        ask(llCreateKeyValue(""a"", ""2""), ""again"");
        ask(llReadKeyValue(""a""), ""read"");", 3);
        Assert.Equal("1,1", r.Answer("first"));
        Assert.Equal("0,13", r.Answer("again"));              // wiki: XP_ERROR_STORAGE_EXCEPTION
        Assert.Equal("1,1", r.Answer("read"));
    }

    [Fact]
    public void CheckedUpdateWithTheWrongOriginalIsRetryUpdate()
    {
        var store = new FakeStore();
        using var r = Ask(store, @"
        ask(llCreateKeyValue(""a"", ""1""), ""create"");
        ask(llUpdateKeyValue(""a"", ""2"", TRUE, ""not 1""), ""checked"");
        ask(llUpdateKeyValue(""a"", ""3"", ""not 1""), ""checked3"");
        ask(llReadKeyValue(""a""), ""read"");", 4);
        Assert.Equal("0,15", r.Answer("checked"));            // XP_ERROR_RETRY_UPDATE
        Assert.Equal("0,15", r.Answer("checked3"));
        Assert.Equal("1,1", r.Answer("read"));
    }

    [Fact]
    public void UpdatingAMissingKeyCreatesIt()
    {
        // wiki llUpdateKeyValue: "will not generate XP_ERROR_KEY_NOT_FOUND. Instead, it will generate a new key with the
        // specified value, as if you had used llCreateKeyValue." NGC's update answers "missing", so Phlox creates.
        using var r = Ask(new FakeStore(), @"
        ask(llUpdateKeyValue(""x"", ""new"", FALSE, """"), ""unchecked"");
        ask(llUpdateKeyValue(""y"", ""new"", TRUE, ""whatever""), ""checked"");
        ask(llReadKeyValue(""x""), ""rx"");
        ask(llReadKeyValue(""y""), ""ry"");", 4);
        Assert.Equal("1,new", r.Answer("unchecked"));
        Assert.Equal("1,new", r.Answer("checked"));
        Assert.Equal("1,new", r.Answer("rx"));
        Assert.Equal("1,new", r.Answer("ry"));
    }

    [Fact]
    public void DeletingAMissingKeyIsStorageException()
    {
        using var r = Ask(new FakeStore(), @"ask(llDeleteKeyValue(""nope""), ""delete"");", 1);
        Assert.Equal("0,13", r.Answer("delete"));             // wiki: XP_ERROR_STORAGE_EXCEPTION
    }

    [Fact]
    public void AnEmptyValueReadsBackAsOneComma()
    {
        using var r = Ask(new FakeStore(), @"
        ask(llCreateKeyValue(""e"", """"), ""create"");
        ask(llReadKeyValue(""e""), ""read"");", 2);
        Assert.Equal("1,", r.Answer("create"));
        Assert.Equal("1,", r.Answer("read"));
    }

    [Fact]
    public void AValueWithCommasComesBackWhole()
    {
        using var r = Ask(new FakeStore(), @"
        ask(llCreateKeyValue(""c"", ""x,y,z""), ""create"");
        ask(llReadKeyValue(""c""), ""read"");", 2);
        Assert.Equal("1,x,y,z", r.Answer("read"));
    }

    [Fact]
    public void AnEmptyOrTooLongKeyIsInvalidParameters()
    {
        // wiki llCreateKeyValue: keys are at most 1011 bytes. 1011 is fine, 1012 (or 506 two-byte characters) is not.
        using var r = Ask(new FakeStore(), @"
        string k1011 = """"; integer i; for (i = 0; i < 1011; i++) k1011 += ""k"";
        string k1012 = k1011 + ""k"";
        string wide = """"; for (i = 0; i < 506; i++) wide += ""é"";
        ask(llCreateKeyValue("""", ""v""), ""c0"");
        ask(llReadKeyValue(""""), ""r0"");
        ask(llUpdateKeyValue("""", ""v"", FALSE, """"), ""u0"");
        ask(llDeleteKeyValue(""""), ""d0"");
        ask(llCreateKeyValue(k1012, ""v""), ""c1012"");
        ask(llReadKeyValue(k1012), ""r1012"");
        ask(llUpdateKeyValue(k1012, ""v"", FALSE, """"), ""u1012"");
        ask(llDeleteKeyValue(k1012), ""d1012"");
        ask(llCreateKeyValue(wide, ""v""), ""cwide"");
        ask(llCreateKeyValue(k1011, ""v""), ""c1011"");", 10);
        foreach (string t in new[] { "c0", "r0", "u0", "d0", "c1012", "r1012", "u1012", "d1012", "cwide" })
            Assert.Equal("0,3", r.Answer(t));                 // XP_ERROR_INVALID_PARAMETERS
        Assert.Equal("1,v", r.Answer("c1011"));
    }

    [Fact]
    public void CountKeysAndDataSize()
    {
        var store = new FakeStore();
        using var r = Ask(store, @"
        ask(llKeysKeyValue(0, 10), ""keys-empty"");
        ask(llKeyCountKeyValue(), ""count-empty"");
        ask(llCreateKeyValue(""b"", ""22""), ""cb"");
        ask(llCreateKeyValue(""a"", ""1""), ""ca"");
        ask(llCreateKeyValue(""c"", ""333""), ""cc"");
        ask(llKeyCountKeyValue(), ""count"");
        ask(llKeysKeyValue(0, 10), ""all"");
        ask(llKeysKeyValue(1, 1), ""second"");
        ask(llKeysKeyValue(2, 5), ""tail"");
        ask(llKeysKeyValue(3, 1), ""past"");
        ask(llKeysKeyValue(-4, 2), ""negative-first"");
        ask(llKeysKeyValue(0, 0), ""zero-count"");
        ask(llDataSizeKeyValue(), ""size"");", 13);
        Assert.Equal("0,14", r.Answer("keys-empty"));         // first >= number of keys
        Assert.Equal("1,0", r.Answer("count-empty"));
        Assert.Equal("1,3", r.Answer("count"));
        Assert.Equal("1,a,b,c", r.Answer("all"));
        Assert.Equal("1,b", r.Answer("second"));
        Assert.Equal("1,c", r.Answer("tail"));
        Assert.Equal("0,14", r.Answer("past"));
        Assert.Equal("1,a,b", r.Answer("negative-first"));     // Phlox's clamp: first < 0 is 0
        Assert.Equal("1,a,b,c", r.Answer("zero-count"));       // Phlox's clamp: count <= 0 is 100
        Assert.Equal("1,9," + PhloxQuota, r.Answer("size"));  // "1,<used>,<quota>": a1 b22 c333 = 9 bytes
    }

    [Fact]
    public void KeysStopsAt4096Characters()
    {
        // wiki llKeysKeyValue: fewer keys than asked "if ... the result list exceeds 4096 characters".
        var store = new FakeStore();
        var d = store.Data.GetOrAdd(Exp, _ => new SortedDictionary<string, string>(StringComparer.Ordinal));
        for (int i = 0; i < 10; i++) d["k" + i + new string('x', 998)] = "v";   // 1000 characters each
        using var r = Ask(store, @"ask(llKeysKeyValue(0, 10), ""keys"");", 1);
        // "1" and four keys: 2 + 4 x 1000 + 3 commas = 4005 characters; a fifth key would pass 4096.
        Assert.Equal("1,#5/4005", r.Answer("keys"));
    }

    [Fact]
    public void QuotaExceededFromPhloxsLimitAndFromTheStore()
    {
        var phlox = new FakeStore { ExtraReportedSize = (int)(PhloxQuota - 4) };
        using (var r = Ask(phlox, @"
        ask(llCreateKeyValue(""a"", ""1234""), ""create"");
        ask(llUpdateKeyValue(""b"", ""1234"", FALSE, """"), ""update"");
        ask(llCreateKeyValue(""c"", ""1""), ""fits"");", 3))
        {
            Assert.Equal("0,11", r.Answer("create"));            // XP_ERROR_QUOTA_EXCEEDED
            Assert.Equal("0,11", r.Answer("update"));
            Assert.Equal("1,1", r.Answer("fits"));
        }

        var ngc = new FakeStore { ServiceQuota = 6 };
        using (var r = Ask(ngc, @"
        ask(llCreateKeyValue(""a"", ""12345""), ""create"");
        ask(llCreateKeyValue(""b"", ""12345""), ""full"");
        ask(llUpdateKeyValue(""a"", ""123456789"", FALSE, """"), ""update-full"");", 3))
        {
            Assert.Equal("1,12345", r.Answer("create"));
            Assert.Equal("0,11", r.Answer("full"));
            Assert.Equal("0,11", r.Answer("update-full"));
        }
    }

    [Fact]
    public void AStoreFailureIsStorageExceptionForEveryCall()
    {
        using var r = Ask(new FakeStore { Throw = true }, @"
        ask(llCreateKeyValue(""a"", ""1""), ""create"");
        ask(llReadKeyValue(""a""), ""read"");
        ask(llUpdateKeyValue(""a"", ""2"", FALSE, """"), ""update"");
        ask(llUpdateKeyValue(""a"", ""2"", """"), ""update3"");
        ask(llDeleteKeyValue(""a""), ""delete"");
        ask(llKeyCountKeyValue(), ""count"");
        ask(llKeysKeyValue(0, 1), ""keys"");
        ask(llDataSizeKeyValue(), ""size"");", 8);
        foreach (string t in new[] { "create", "read", "update", "update3", "delete", "count", "keys", "size" })
            Assert.Equal("0,13", r.Answer(t));                 // XP_ERROR_STORAGE_EXCEPTION
    }

    [Fact]
    public void ARegionWithNoExperienceServiceAnswersStoreDisabled()
    {
        using var r = Ask(null, @"
        ask(llCreateKeyValue(""a"", ""1""), ""create"");
        ask(llReadKeyValue(""a""), ""read"");
        ask(llUpdateKeyValue(""a"", ""2"", TRUE, ""1""), ""update"");
        ask(llUpdateKeyValue(""a"", ""2"", ""1""), ""update3"");
        ask(llDeleteKeyValue(""a""), ""delete"");
        ask(llKeyCountKeyValue(), ""count"");
        ask(llKeysKeyValue(0, 1), ""keys"");
        ask(llDataSizeKeyValue(), ""size"");", 8);
        foreach (string t in new[] { "create", "read", "update", "update3", "delete", "count", "keys", "size" })
            Assert.Equal("0,12", r.Answer(t));                 // XP_ERROR_STORE_DISABLED "key-value store is disabled"
    }

    [Fact]
    public void TheScriptsExperienceIsTheNamespaceAndWithoutOneTheSlNamesSayNoExperience()
    {
        var store = new FakeStore();
        using (var r = Ask(store, @"ask(llCreateKeyValue(""a"", ""xp""), ""create"");", 1))
            Assert.Equal("xp", store.Data[Exp]["a"]);

        // As SL: a script with no Experience once used its owner's id; now XP_ERROR_NO_EXPERIENCE.
        var store2 = new FakeStore();
        using var r2 = Ask(store2, @"ask(llCreateKeyValue(""a"", ""owner""), ""create"");", 1, experience: UUID.Zero);
        Assert.Equal("0,5", r2.Answer("create"));
        Assert.Empty(store2.Data);
    }

    // ── A script with no Experience (as SL) ──
    // wiki (each call): "For this function to work, the script must be compiled into an Experience."; llGetExperienceErrorMessage:
    // XP_ERROR_NO_EXPERIENCE | 5 | "This script is not associated with an experience." Phlox's own names keep the owner id.

    [Theory]
    [InlineData("never")]
    [InlineData("auto")]
    public void EverySlNameWithNoExperienceAnswersFiveAndTouchesNothing(string deferral)
    {
        var store = new FakeStore();
        using var r = Ask(store, @"
        ask(llCreateKeyValue(""a"", ""1""), ""create"");
        ask(llReadKeyValue(""a""), ""read"");
        ask(llUpdateKeyValue(""a"", ""2"", TRUE, ""1""), ""update"");
        ask(llUpdateKeyValue(""a"", ""2"", ""1""), ""update3"");
        ask(llDeleteKeyValue(""a""), ""delete"");
        ask(llKeyCountKeyValue(), ""count"");
        ask(llKeysKeyValue(0, 1), ""keys"");
        ask(llDataSizeKeyValue(), ""size"");", 8, experience: UUID.Zero, deferral: deferral);
        foreach (string t in new[] { "create", "read", "update", "update3", "delete", "count", "keys", "size" })
            Assert.Equal("0,5", r.Answer(t));                  // XP_ERROR_NO_EXPERIENCE
        Assert.Empty(store.ExperiencesSeen);                   // the store was never called
        Assert.Empty(store.Data);
    }

    [Fact]
    public void PhloxsOwnNamesWithNoExperienceKeepTheOwnersId()
    {
        var store = new FakeStore();
        using var r = Ask(store, @"
        llSay(0, ""c|"" + llCreateKeyValueSL(""a"", ""1""));
        llSay(0, ""u|"" + llUpdateKeyValueSL(""a"", ""2"", ""1""));
        llSay(0, ""r|"" + llReadKeyValueSL(""a""));
        ask(llReadKeyValue(""a""), ""slread"");
        llSay(0, ""clear|"" + (string)llClearKeyValue());
        llSay(0, ""gone|"" + llReadKeyValueSL(""a""));", 6, experience: UUID.Zero);
        Assert.Equal("1,1", r.Answer("c"));
        Assert.Equal("1,2", r.Answer("u"));
        Assert.Equal("1,2", r.Answer("r"));
        Assert.Equal("0,5", r.Answer("slread"));               // SL's name does not reach the owner's data
        Assert.Equal("0", r.Answer("clear"));
        Assert.Equal("0,14", r.Answer("gone"));
        Assert.NotEmpty(store.ExperiencesSeen);
        Assert.All(store.ExperiencesSeen, e => Assert.Equal(r.H.Prim.OwnerID, e));
        Assert.Empty(store.Data[r.H.Prim.OwnerID]);
    }

    // ── SL's value limit ──
    // wiki llCreateKeyValue / llUpdateKeyValue: "As of Jan 1, 2016 maximum bytes is 1011 for key and 4095 for value for both
    // LSO and Mono scripts."; over it XP_ERROR_INVALID_PARAMETERS (3), "One of the string arguments was too big to fit in the
    // key-value store." Bytes are UTF-8: 2048 x "é" is 4096 bytes.

    private const string BuildValues = @"
        string x = ""x""; integer i;
        for (i = 0; i < 12; i++) x += x;                                         // 4096 bytes
        string e = llUnescapeURL(""%C3%A9"");
        for (i = 0; i < 11; i++) e += e;                                         // 2048 x U+00E9 = 4096 bytes
        string x4094 = llGetSubString(x, 0, 4093); string x4095 = llGetSubString(x, 0, 4094);
        string e4095 = llGetSubString(e, 1, -1) + ""x"";                        // 2047 x 2 + 1 = 4095 bytes";

    [Fact]
    public void AValueOver4095BytesIsInvalidParametersAndNotWritten()
    {
        var store = new FakeStore();
        using var r = Ask(store, BuildValues + @"
        ask(llCreateKeyValue(""c4094"", x4094), ""c4094"");
        ask(llCreateKeyValue(""c4095"", x4095), ""c4095"");
        ask(llCreateKeyValue(""c4096"", x), ""c4096"");
        ask(llCreateKeyValue(""e4095"", e4095), ""e4095"");
        ask(llCreateKeyValue(""e4096"", e), ""e4096"");
        ask(llUpdateKeyValue(""u"", x, FALSE, """"), ""u4096"");
        ask(llUpdateKeyValue(""u"", x4095, FALSE, """"), ""u4095"");
        ask(llUpdateKeyValue(""c4094"", e, """"), ""u3e4096"");
        ask(llUpdateKeyValue(""c4094"", x4095, """"), ""u3x4095"");", 9);
        foreach (string t in new[] { "c4094", "c4095", "e4095", "u4095", "u3x4095" })
            Assert.StartsWith("1,", r.Answer(t));
        foreach (string t in new[] { "c4096", "e4096", "u4096", "u3e4096" })
            Assert.Equal("0,3", r.Answer(t));                  // XP_ERROR_INVALID_PARAMETERS
        var d = store.Data[Exp];
        Assert.Equal(4095, d["c4094"].Length);                 // updated by u3x4095 only; u3e4096 wrote nothing
        Assert.Equal(4095, d["c4095"].Length);
        Assert.Equal(4095, System.Text.Encoding.UTF8.GetByteCount(d["e4095"]));
        Assert.Equal(4095, d["u"].Length);
        Assert.False(d.ContainsKey("c4096"));
        Assert.False(d.ContainsKey("e4096"));
    }

    [Fact]
    public void TheSlSuffixedNamesKeepTheValueLimitToo()
    {
        var store = new FakeStore();
        using var r = Ask(store, BuildValues + @"
        llSay(0, ""c4095|"" + llGetSubString(llCreateKeyValueSL(""s"", x4095), 0, 1));
        llSay(0, ""c4096|"" + llCreateKeyValueSL(""t"", x));
        llSay(0, ""ce4096|"" + llCreateKeyValueSL(""t"", e));
        llSay(0, ""u4096|"" + llUpdateKeyValueSL(""s"", x, x4095));
        llSay(0, ""u4094|"" + llGetSubString(llUpdateKeyValueSL(""s"", x4094, x4095), 0, 1));", 5);
        Assert.Equal("1,", r.Answer("c4095"));
        Assert.Equal("0,3", r.Answer("c4096"));
        Assert.Equal("0,3", r.Answer("ce4096"));
        Assert.Equal("0,3", r.Answer("u4096"));
        Assert.Equal("1,", r.Answer("u4094"));
        var d = store.Data[Exp];
        Assert.Equal(4094, d["s"].Length);
        Assert.False(d.ContainsKey("t"));
    }

    [Fact]
    public void TheSlSuffixedNamesStillAnswerAtOnce()
    {
        using var r = Ask(new FakeStore(), @"
        llSay(0, ""c|"" + llCreateKeyValueSL(""a"", ""1""));
        llSay(0, ""r|"" + llReadKeyValueSL(""a""));
        llSay(0, ""u|"" + llUpdateKeyValueSL(""a"", ""2"", ""1""));
        llSay(0, ""bad|"" + llUpdateKeyValueSL(""a"", ""3"", ""1""));
        llSay(0, ""dup|"" + llCreateKeyValueSL(""a"", ""9""));
        llSay(0, ""miss|"" + llReadKeyValueSL(""zz""));
        llSay(0, ""clear|"" + (string)llClearKeyValue());
        llSay(0, ""gone|"" + llReadKeyValueSL(""a""));", 8);
        Assert.Equal("1,1", r.Answer("c"));
        Assert.Equal("1,1", r.Answer("r"));
        Assert.Equal("1,2", r.Answer("u"));
        Assert.Equal("0,15", r.Answer("bad"));
        Assert.Equal("0,13", r.Answer("dup"));
        Assert.Equal("0,14", r.Answer("miss"));
        Assert.Equal("0", r.Answer("clear"));
        Assert.Equal("0,14", r.Answer("gone"));
    }

    /// <summary>
    /// The reset rule: an answer owed to a script that was reset in between is dropped. The read is held in the store
    /// while the script is reset; after it is let go, a second script's read (same store) is answered, and the reset
    /// script never hears the first answer.
    /// </summary>
    [Fact]
    public void AnAnswerOwedToAResetScriptIsDropped()
    {
        var store = new FakeStore { ReadGate = new ManualResetEventSlim(false) };
        store.Data.GetOrAdd(Exp, _ => new SortedDictionary<string, string>(StringComparer.Ordinal))["a"] = "held";
        using var h = new SchedulerHarness(cfg => cfg.Configs["InWorldz.Phlox"].Set("ServiceCallDeferral", "always"));
        h.Scene.RegisterModuleInterface<IExperienceService>(store);
        UUID id = h.RezScript(@"
integer started;
default
{
    state_entry() { started = 1; if (llGetObjectDesc() == ""asked"") llSay(0, ""restarted""); }
    touch_start(integer n) { llSetObjectDesc(""asked""); key k = llReadKeyValue(""a""); llSay(0, ""returned""); }
    dataserver(key q, string data) { llSay(0, ""dataserver|"" + data); }
}");
        h.Prim.Inventory.GetInventoryItem(id).ExperienceID = Exp;
        Assert.True(h.PumpUntil(() => h.InterpreterFor(id) != null));
        h.PostTouch(id);
        Assert.True(h.PumpUntil(() => store.ReadEntered.IsSet), "the read never reached the store: " + h.Diagnose(id));

        h.Engine.ResetScript(id);
        Assert.True(h.PumpUntil(() => h.Said.Contains("restarted")), "no reset: " + string.Join(" | ", h.Said) + h.Diagnose(id));
        store.ReadGate.Set();

        // Positive control through the same store: a second prim's script gets its answer.
        var prim2 = OpenSim.Tests.Common.SceneHelpers.AddSceneObject(h.Scene, "control", h.Prim.OwnerID).RootPart;
        UUID control = h.RezScriptInto(prim2, @"
default
{
    touch_start(integer n) { llReadKeyValue(""a""); }
    dataserver(key q, string data) { llSay(0, ""control|"" + data); }
}");
        prim2.Inventory.GetInventoryItem(control).ExperienceID = Exp;
        Assert.True(h.PumpUntil(() => h.InterpreterFor(control) != null));
        h.PostTouch(control);
        Assert.True(h.PumpUntil(() => h.Said.Contains("control|1,held")), string.Join(" | ", h.Said));

        // "Did not happen" window: the held answer had every chance to arrive.
        h.PumpFor(TimeSpan.FromSeconds(1));
        _out.WriteLine(string.Join("\n", h.Said));
        Assert.DoesNotContain(h.Said, s => s.StartsWith("dataserver|"));
        Assert.DoesNotContain("returned", h.Said);
    }

    [Theory]
    [InlineData("integer r = llCreateKeyValue(\"k\", \"v\");")]
    [InlineData("integer r = llDeleteKeyValue(\"k\");")]
    [InlineData("integer r = llKeyCountKeyValue();")]
    [InlineData("integer r = llDataSizeKeyValue();")]
    [InlineData("list l = llKeysKeyValue(0, 1);")]
    [InlineData("integer r = llUpdateKeyValue(\"k\", \"v\", \"old\");")]
    public void TheImmediateAnswerFormNoLongerCompiles(string body)
    {
        var r = ExprRunner.RunInDefault(body);
        _out.WriteLine(r.Describe());
        Assert.NotNull(r.CompileError);
    }

    [Theory]
    [InlineData("key r = llCreateKeyValue(\"k\", \"v\");")]
    [InlineData("key r = llReadKeyValue(\"k\");")]
    [InlineData("key r = llUpdateKeyValue(\"k\", \"v\", TRUE, \"old\");")]
    [InlineData("key r = llUpdateKeyValue(\"k\", \"v\", \"old\");")]
    [InlineData("key r = llDeleteKeyValue(\"k\");")]
    [InlineData("key r = llKeyCountKeyValue();")]
    [InlineData("key r = llKeysKeyValue(0, 1);")]
    [InlineData("key r = llDataSizeKeyValue();")]
    [InlineData("string s = llReadKeyValue(\"k\");")]
    public void TheSlFormCompiles(string body)
    {
        var c = PhloxCompiler.CompileInDefault(body);
        Assert.False(c.HasErrors(), body + ": " + c.Report);
    }
}
