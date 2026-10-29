using System;
using System.Data.SQLite;
using System.Linq;
using OpenMetaverse;
using Phlox.ScriptEngine;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A load that still fails after the retry must NOT fall through to a fresh script: the
/// script is held Disabled (LocalDisableFlag.StateLoadFailed, visible in phlox status), never runs
/// state_entry, and - the point - its state row is never overwritten, so the next process recovers it.
/// The failure is injected through StateManager.FailLoadForTest, the way the database would throw.
/// </summary>
[Collection("phlox-state")]
public class StateLoadFailedHoldTests
{
    private readonly ITestOutputHelper _out;
    public StateLoadFailedHoldTests(ITestOutputHelper o) => _out = o;

    private const string Src = "integer g = 7; default { state_entry() { g = 41; llSay(0, \"up\"); } touch_start(integer n) { g++; llSay(0, \"g=\" + (string)g); } }";

    private static (byte[] blob, long savedAt) Row(UUID itemId)
    {
        using var conn = new SQLiteConnection("Data Source=ScriptEngines/Phlox/state/script_state.db");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT state_data, saved_at FROM script_state WHERE item_id = @id";
        cmd.Parameters.AddWithValue("@id", itemId.ToString());
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read(), "no row for " + itemId);
        return ((byte[])r[0], r.GetInt64(1));
    }

    [Fact]
    public void AFailedLoadHoldsTheScriptDisabledAndKeepsTheRow()
    {
        var assetId = UUID.Random();
        var itemId = UUID.Random();

        // first process: run, save
        using (var h1 = new SchedulerHarness())
        {
            h1.RezScript(Src, assetId, itemId);
            h1.PumpUntil(() => h1.Said.Contains("up"));
            Assert.Contains("up", h1.Said);
            h1.SaveState(itemId);
        }
        var before = Row(itemId);
        _out.WriteLine($"row before: {before.blob.Length} bytes, saved_at {before.savedAt}");

        // second process: the database "is locked" for this item, twice
        StateManager.FailLoadForTest = id => id == itemId;
        try
        {
            using var h2 = new SchedulerHarness();
            h2.RezScript(Src, assetId, itemId);
            h2.PumpFor(TimeSpan.FromSeconds(1));
            h2.PumpUntil(() =>
            {
                var held = (StateManager)h2.StateManagerOf();
                var st = h2.StatusOf(itemId);
                return held.IsLoadFailed(itemId) && held.LoadFailures >= 2 &&
                       st.Contains("StateLoadFailed") && st.Contains("Enabled=False");
            });

            Assert.DoesNotContain("up", h2.Said);                    // no fresh state_entry
            var sm = (StateManager)h2.StateManagerOf();
            Assert.True(sm.IsLoadFailed(itemId));
            Assert.Equal(2, sm.LoadFailures);                        // one retry, then held
            var status = h2.StatusOf(itemId);
            _out.WriteLine("status: " + status);
            Assert.Contains("StateLoadFailed", status);
            Assert.Contains("Enabled=False", status);

            // a touch must not run it, and shutdown (Dispose -> ScriptUnloaded) must not save it
            h2.PostTouch(itemId);
            h2.PumpFor(TimeSpan.FromMilliseconds(300));
            Assert.DoesNotContain(h2.Said, s => s.StartsWith("g="));
        }
        finally { StateManager.FailLoadForTest = null; }

        var after = Row(itemId);
        _out.WriteLine($"row after : {after.blob.Length} bytes, saved_at {after.savedAt}");
        Assert.Equal(before.savedAt, after.savedAt);
        Assert.Equal(before.blob, after.blob);

        // third process: the database is fine again - the saved state comes back, globals intact
        using (var h3 = new SchedulerHarness())
        {
            h3.RezScript(Src, assetId, itemId);
            h3.Pump();
            Assert.DoesNotContain("up", h3.Said);                    // restored, not fresh
            h3.PostTouch(itemId);
            h3.PumpUntil(() => h3.Said.Any(s => s.StartsWith("g=")));
            _out.WriteLine("said=[" + string.Join(" | ", h3.Said) + "]");
            Assert.Contains("g=42", h3.Said);                        // 41 from the first run, +1
        }
    }
}
