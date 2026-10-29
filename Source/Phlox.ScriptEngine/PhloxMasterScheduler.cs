/*
 * Phlox Script Engine Integration
 * Adapted from InWorldz Halcyon MasterScheduler.cs
 * Copyright (c) InWorldz Halcyon Developers (original)
 * Adapted 2026 by Legion Builds for OpenSim 0.9.3 .NET 8
 */

using System;
using System.Reflection;
using OpenSim.Framework;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Phlox.ScriptEngine
{
    internal class PhloxMasterScheduler
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly PhloxExecutionScheduler m_ExeScheduler;
        private readonly PhloxScriptLoader m_ScriptLoader;
        private readonly ManualResetEvent m_ActionEvent = new ManualResetEvent(false);
        private readonly ThreadPriority m_Priority;
        private Thread m_Thread;
        private volatile bool m_Stop = false;

        public bool IsRunning => !m_Stop;

        /// <param name="priority">The thread's priority: [InWorldz.Phlox] SchedulerThreadPriority, see <see cref="PhloxEngine.SchedulerThreadPriority"/>.</param>
        public PhloxMasterScheduler(PhloxExecutionScheduler exeScheduler, PhloxScriptLoader scriptLoader,
            ThreadPriority priority = PhloxEngine.DefaultSchedulerThreadPriority)
        {
            m_Priority = priority;
            m_ExeScheduler = exeScheduler;
            m_ScriptLoader = scriptLoader;
        }

        public void Start()
        {
            m_Thread = new Thread(WorkLoop)
            {
                Name = "PhloxMasterScheduler",
                Priority = m_Priority,
                IsBackground = true
            };
            m_Thread.Start();
        }

        /// <returns>False if the thread was still running when the 5 s join gave up (see <see cref="StopThread"/>).</returns>
        public bool Stop()
        {
            bool stopped = StopThread();
            m_ScriptLoader.Stop();
            m_ExeScheduler.Stop();
            return stopped;
        }

        /// <summary>
        /// Stop only this scheduler's own thread. The test harness drives DoWork itself and needs the
        /// loader's compile thread to keep running; region shutdown is <see cref="Stop"/>.
        /// </summary>
        /// <returns>False if the thread was still running when the 5 s join gave up.</returns>
        internal bool StopThread()
        {
            m_Stop = true;
            WorkArrived();
            return m_Thread == null || m_Thread.Join(5000);
        }

        public void WorkArrived()
        {
            m_ActionEvent.Set();
        }

        private void WorkLoop()
        {
            const int MAX_CRASHES = 10;
            int crashCount = 0;

            while (!m_Stop)
            {
                try
                {
                    while (!m_Stop)
                    {
                        // Lost-wakeup-safe pattern:
                        //   1. Reset the signal BEFORE checking work.
                        //   2. Do all available work.
                        //   3. If still no work, wait.
                        //   4. If a Set() arrived between (1) and (3), WaitOne returns immediately.
                        //
                        // The OLD pattern (Reset AFTER WaitOne returns) lost any Set() calls
                        // that arrived while we were processing — leading to scripts'
                        // state_entry events sitting in the queue until the NEXT Set() arrived,
                        // which could be 60+ seconds later if nothing else was happening.
                        m_ActionEvent.Reset();

                        // StopThread sets m_Stop and then signals. A stop that landed after the loop's
                        // m_Stop check and before the Reset above had its signal erased, and with no work queued the
                        // loop then waited forever (StopThread's 5 s join timed out). m_Stop is written before the
                        // signal, so reading it again after the Reset can never miss that stop.
                        if (m_Stop) break;

                        WorkStatus exeStatus = m_ExeScheduler.DoWork();
                        WorkStatus loadStatus = m_ScriptLoader.DoWork();

                        // If either has work pending, loop immediately without waiting.
                        if (exeStatus.WorkIsPending || loadStatus.WorkIsPending)
                            continue;

                        ulong wakeAt = Math.Min(exeStatus.NextWakeUpTime, loadStatus.NextWakeUpTime);

                        if (wakeAt != ulong.MaxValue)
                        {
                            // wakeAt and now are both InWorldz.Phlox.Util.Clock, so there is
                            // one basis for the whole engine. This used to read a 30-bit MASKED uptime
                            // tick (OpenSim.Framework.Util.EnvironmentTickCount), which drops back to
                            // near zero every 12.4 days - below every queued wakeAt, making waitMs
                            // enormous and stalling every timer and sleep until it climbed back.
                            long now = (long)InWorldz.Phlox.Util.Clock.Now;
                            long waitMs = (long)wakeAt - now;
                            if (waitMs > 0)
                                m_ActionEvent.WaitOne((int)Math.Min(waitMs, int.MaxValue));
                            // If waitMs <= 0, the wake time has already passed; loop immediately.
                        }
                        else
                        {
                            // No timed wake-up — wait indefinitely for a Set() signal.
                            m_ActionEvent.WaitOne();
                        }
                    }
                }
                catch (ThreadAbortException)
                {
                    // Normal shutdown — don't restart
                    break;
                }
                catch (Exception e)
                {
                    crashCount++;
                    if (crashCount >= MAX_CRASHES)
                    {
                        m_log.LogError(
                            "[PhloxMaster]: CRITICAL — master scheduler crashed {0} times, script engine disabled. {1}",
                            crashCount, e);
                        m_Stop = true;
                        break;
                    }

                    m_log.LogError(
                        "[PhloxMaster]: Master scheduler exception (crash #{0}/{1}), restarting in 1s. {2}",
                        crashCount, MAX_CRASHES, e);

                    // Brief pause before restart to avoid tight crash loops
                    Thread.Sleep(1000);
                }
            }
        }
    }
}
