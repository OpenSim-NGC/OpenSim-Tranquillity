using System.Reflection;
using InWorldz.Phlox.Glue;
using InWorldz.Phlox.VM;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A syscall that may be handed to the service lane (SyscallShim.Defer) and returns a value must not answer null:
/// pushing nothing leaves the operand stack one short and the script fails later, far from the cause. As Halcyon's
/// SafeOperandsPush did, the call stops the script with "Attempt to push null operand" - inline at the call, deferred
/// as the call's fault (the scheduler raises a fault in the script). A function with no value (failure value null)
/// still pushes nothing.
/// Drives the shim directly; no process-wide state, so the class runs in parallel.
/// </summary>
public class DeferNullResultTests
{
    /// <summary>The API the shim sees: every call answered by the test, and the deferral question answered yes.</summary>
    public interface IAdvisingApi : ISystemAPI, ISyscallDeferralAdvisor { }

    public class AlwaysDefer : DispatchProxy
    {
        protected override object Invoke(MethodInfo method, object[] args)
            => method.Name == nameof(ISyscallDeferralAdvisor.NeedsService) ? true
             : method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null;
    }

    private static readonly MethodInfo DeferMethod = typeof(SyscallShim)
        .GetMethod("Defer", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static (SyscallShim shim, Interpreter interp) NewShim(bool deferring, List<DeferredServiceCall> deferred)
    {
        var shim = new SyscallShim(null);
        var interp = new Interpreter(ExprRunner.CompileLsl("default { state_entry() { } }"), shim);
        shim.Interpreter = interp;
        if (deferring)
        {
            shim.SystemAPI = DispatchProxy.Create<IAdvisingApi, AlwaysDefer>();
            shim.DeferServiceCall = deferred.Add;
        }
        return (shim, interp);
    }

    private static void Defer(SyscallShim shim, Func<object> call, object failure, bool safePush)
    {
        try { DeferMethod.Invoke(null, new object[] { shim, "testCall", Array.Empty<object>(), call, failure, safePush }); }
        catch (TargetInvocationException e) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AValueCallAnsweringNullInlineStopsTheScript(bool safePush)
    {
        var (shim, interp) = NewShim(false, null);
        int depth = interp.ScriptState.Operands.Count;
        var e = Assert.Throws<VMException>(() => Defer(shim, () => null, "", safePush));
        Assert.StartsWith("Attempt to push null operand", e.Message);
        Assert.Equal(depth, interp.ScriptState.Operands.Count);
    }

    [Fact]
    public void AValueCallAnsweringNullOnTheServiceLaneFaults()
    {
        var deferred = new List<DeferredServiceCall>();
        var (shim, _) = NewShim(true, deferred);
        Defer(shim, () => null, "", true);
        var call = Assert.Single(deferred);
        var e = Assert.Throws<VMException>(() => call.Body());
        Assert.StartsWith("Attempt to push null operand", e.Message);
    }

    [Fact]
    public void AValueCallStillPushesItsValueBothWays()
    {
        var (shim, interp) = NewShim(false, null);
        Defer(shim, () => "answer", "", true);
        Assert.Equal("answer", interp.ScriptState.Operands.Peek());

        var deferred = new List<DeferredServiceCall>();
        var (lane, _) = NewShim(true, deferred);
        Defer(lane, () => 7, -1, false);
        Assert.Equal(7, Assert.Single(deferred).Body());
    }

    [Fact]
    public void AVoidCallStillPushesNothingBothWays()
    {
        var (shim, interp) = NewShim(false, null);
        int depth = interp.ScriptState.Operands.Count;
        Defer(shim, () => null, null, true);
        Assert.Equal(depth, interp.ScriptState.Operands.Count);

        var deferred = new List<DeferredServiceCall>();
        var (lane, _) = NewShim(true, deferred);
        Defer(lane, () => null, null, true);
        Assert.Null(Assert.Single(deferred).Body());
    }
}
