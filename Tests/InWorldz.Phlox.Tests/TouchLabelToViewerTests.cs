using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Setting the touch label has to reach the viewer, not just the part.
///
/// <para>
/// The label a viewer shows comes from the FULL ObjectProperties reply -
/// <c>LLSelectMgr::processObjectProperties</c> fills <c>LLSelectNode::mTouchName</c>
/// (llselectmgr.cpp:6110) and the context menu reads it (llviewermenu.cpp:3096-3101). The region
/// sends that only on select (<c>Scene.PacketHandlers.cs:223</c>); a right-click asks for
/// <c>ObjectPropertiesFamily</c>, whose reply has no touch name. So a script that changes the label
/// after the last select was invisible - the menu still read "Touch" with TouchName set to "Enter".
/// </para>
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class TouchLabelToViewerTests
{
    private readonly ITestOutputHelper _out;
    public TouchLabelToViewerTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void SettingTheTouchLabelPushesObjectPropertiesToClients()
    {
        using var h = new SchedulerHarness();
        var client = h.AddClient();
        client.ObjectPropertiesSent.Clear();

        h.RezScript(@"
default
{
    state_entry()
    {
        llSetTouchText(""Enter"");
    }
}
");
        h.PumpUntil(() => h.Prim.TouchName == "Enter" && client.ObjectPropertiesSent.Any(e => ReferenceEquals(e, h.Prim)));

        _out.WriteLine($"TouchName='{h.Prim.TouchName}' propertiesSent={client.ObjectPropertiesSent.Count}");

        Assert.Equal("Enter", h.Prim.TouchName);
        Assert.Contains(client.ObjectPropertiesSent, e => ReferenceEquals(e, h.Prim));
    }

    [Theory]
    [InlineData("llSetSitText(\"Sit here\");", "SitName", "Sit here")]
    [InlineData("llSetObjectName(\"Renamed\");", "Name", "Renamed")]
    [InlineData("llSetObjectDesc(\"Described\");", "Description", "Described")]
    public void EveryPropertySetterPushesObjectPropertiesToClients(string call, string property, string expected)
    {
        // The sit label, name and description all live in the same full ObjectProperties
        // reply as the touch label (LLClientView.cs:6381, :6384, :6390) and had the identical
        // defect - set the field, never tell anyone.
        using var h = new SchedulerHarness();
        var client = h.AddClient();
        client.ObjectPropertiesSent.Clear();

        // One line of LSL: the compiler does not care, and it keeps the attribute data literal-free.
        h.RezScript("default { state_entry() { " + call + " } }");
        h.PumpUntil(() => client.ObjectPropertiesSent.Any(e => ReferenceEquals(e, h.Prim))
            && h.Prim.GetType().GetProperty(property)!.GetValue(h.Prim)?.ToString() == expected);

        var actual = h.Prim.GetType().GetProperty(property)!.GetValue(h.Prim)?.ToString();
        _out.WriteLine($"{property}='{actual}' propertiesSent={client.ObjectPropertiesSent.Count}");

        Assert.Equal(expected, actual);
        Assert.Contains(client.ObjectPropertiesSent, e => ReferenceEquals(e, h.Prim));
    }
}
