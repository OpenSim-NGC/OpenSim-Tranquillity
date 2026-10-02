using System.Reflection;
using OpenSim.Region.Framework.Interfaces;

namespace InWorldz.Phlox.Tests;

/// <summary>An IDialogModule that records SendAlertToUser text (where a compile error goes to the owner).</summary>
public class RecordingDialogs : DispatchProxy
{
    public readonly List<string> Alerts = new();

    public static IDialogModule Create(out RecordingDialogs rec)
    {
        var p = Create<IDialogModule, RecordingDialogs>();
        rec = (RecordingDialogs)(object)p;
        return p;
    }

    protected override object Invoke(MethodInfo m, object[] a)
    {
        if (m.Name == "SendAlertToUser") lock (Alerts) Alerts.Add(a.OfType<string>().FirstOrDefault() ?? "");
        var rt = m.ReturnType;
        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
    }
}
