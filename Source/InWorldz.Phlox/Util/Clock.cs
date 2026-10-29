using System;

namespace InWorldz.Phlox.Util
{
    /// <summary>
    /// The single tick source for the whole script engine.
    ///
    /// <para>
    /// There used to be two, on different bases, and both were broken:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item><description>
    /// <b>This class</b> P/Invoked <c>GetTickCount64</c> on Windows and returned
    /// <c>(UInt64)Environment.TickCount</c> everywhere else. <c>Environment.TickCount</c> is a signed
    /// 32-bit millisecond counter that <b>goes negative after 24.9 days</b> of uptime, and the cast to
    /// <c>UInt64</c> turns that into roughly 1.8e19 - every sleep and every timer then sits behind a
    /// wake-up time eighteen quintillion milliseconds away.
    /// </description></item>
    /// <item><description>
    /// <b>The schedulers</b> compared their queues against
    /// <c>(ulong)OpenSim.Framework.Util.EnvironmentTickCount()</c>, which is system uptime <b>masked to
    /// 30 bits</b> (<c>Util.cs:3618-3623</c>). At every 12.4-day boundary that value drops back to
    /// nearly zero, below every queued <c>ReadyOn</c>, and all timers and sleeps stall until it climbs
    /// back past them.
    /// </description></item>
    /// <item><description>
    /// And the two did not agree: the serializer restored <c>NextWakeup</c> on the Clock basis while the
    /// scheduler compared it on the masked basis, so a restored sleep was measured against a clock that
    /// never set it.
    /// </description></item>
    /// </list>
    ///
    /// <para>
    /// <c>Environment.TickCount64</c> is signed 64-bit milliseconds since boot: it does not wrap in any
    /// practical lifetime, it needs no P/Invoke, and it is the same value on every platform.
    /// </para>
    /// </summary>
    public static class Clock
    {
        private static Func<UInt64> s_source = () => (UInt64)Environment.TickCount64;

        /// <summary>
        /// Milliseconds since boot. Every readyOn, NextWakeup and TimerLastScheduledOn in the engine is
        /// on this basis and only this one.
        /// </summary>
        public static UInt64 Now => s_source();

        /// <summary>
        /// The historical name, kept because the serializer and RuntimeState already read well with it.
        /// Same value as <see cref="Now"/> - there is only one clock now.
        /// </summary>
        public static UInt64 GetLongTickCount() => s_source();

        /// <summary>
        /// Test seam: drive the clock by hand so a 12-day boundary or a 24.9-day wrap can be crossed in
        /// a unit test instead of waited for. Pass <c>null</c> to restore the real one.
        /// </summary>
        public static void SetSourceForTesting(Func<UInt64> source)
            => s_source = source ?? (() => (UInt64)Environment.TickCount64);

        public static DateTime TickCountToDateTime(UInt64 tickCount, UInt64 currentTickCount)
        {
            //tick count should never be zero. if it is it's just uninitialized
            if (tickCount == 0)
            {
                return DateTime.Now;
            }

            try
            {
                Int64 diff = (Int64)tickCount - (Int64)currentTickCount;
                return DateTime.Now.AddMilliseconds((double)diff);
            }
            catch (ArgumentOutOfRangeException e)
            {

                throw new ArgumentOutOfRangeException(
                    String.Format("{0}: tickCount: {1}, currentTickCount: {2}", e.Message, tickCount, currentTickCount),
                    e);
            }
        }
    }
}
