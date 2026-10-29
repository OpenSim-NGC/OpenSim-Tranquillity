using System;
using System.IO;
using System.Threading;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// In world: <c>[PhloxState] Failed to save ...: "Collection was modified
/// after the enumerator was instantiated."</c> - the state saver walked a script's live collections while
/// the script thread mutated them. A flush must never throw: the saver snapshots every collection it
/// walks, and every EventQueue mutation takes the lock the saver snapshots under. Here one thread hammers
/// a RuntimeState the way a running script does (events queued and removed, operands pushed and popped)
/// while another serialises it 100 times, protobuf included.
/// </summary>
public class FlushRaceTests
{
    private readonly ITestOutputHelper _out;
    public FlushRaceTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void AHundredFlushesAgainstAHammeredQueueThrowNothing()
    {
        var state = new InWorldz.Phlox.VM.RuntimeState(4);
        bool stop = false; int mutations = 0;
        var hammer = new Thread(() =>
        {
            var rnd = new Random(1);
            while (!Volatile.Read(ref stop))
            {
                state.QueueEvent(new InWorldz.Phlox.VM.PostedEvent { EventType = InWorldz.Phlox.Types.SupportedEventList.Events.TIMER, Args = Array.Empty<object>() });
                state.QueueEvent(new InWorldz.Phlox.VM.PostedEvent { EventType = InWorldz.Phlox.Types.SupportedEventList.Events.TOUCH_START, Args = new object[] { 1 } });
                state.RemovePendingTimerEvent();
                if (mutations % 25 == 24) state.Reset();   // clears the queue, as a state change does
                for (int i = 0; i < 20; i++) state.Operands.Push(rnd.Next());
                for (int i = 0; i < 20 && state.Operands.Count > 0; i++) state.Operands.Pop();
                state.Globals[rnd.Next(4)] = rnd.Next();
                Interlocked.Increment(ref mutations);
            }
        }) { IsBackground = true };
        hammer.Start();

        int flushes = 0, failures = 0; string first = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int n = 0; n < 100; n++)
        {
            try
            {
                var ser = InWorldz.Phlox.Serialization.SerializedRuntimeState.FromRuntimeState(state);
                using var ms = new MemoryStream();
                ProtoBuf.Serializer.Serialize(ms, ser);
                flushes++;
            }
            catch (Exception e) { failures++; first ??= e.GetType().Name + ": " + e.Message; }
        }
        Volatile.Write(ref stop, true);
        hammer.Join();
        _out.WriteLine($"{flushes} flushes ok, {failures} threw, {mutations} mutation rounds in {sw.ElapsedMilliseconds} ms; first: {first ?? "(none)"}");
        Assert.True(failures == 0, $"{failures} of 100 flushes threw; first: {first}");
    }
}
