/*
 * Legion Grid — Phlox Script Engine Integration
 * Adapted from InWorldz Halcyon MasterScheduler.cs
 * Copyright (c) InWorldz Halcyon Developers (original)
 * Adapted 2026 for Legion Grid / OpenSim 0.9.3 .NET 8
 */

using System;
using System.Reflection;
using OpenSim.Framework;
using System.Threading;
using log4net;

namespace Phlox.ScriptEngine
{
    internal class PhloxMasterScheduler
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly PhloxExecutionScheduler m_ExeScheduler;
        private readonly PhloxScriptLoader m_ScriptLoader;
        private readonly ManualResetEvent m_ActionEvent = new ManualResetEvent(false);
        private Thread m_Thread;
        private volatile bool m_Stop = false;

        public bool IsRunning => !m_Stop;

        public PhloxMasterScheduler(PhloxExecutionScheduler exeScheduler, PhloxScriptLoader scriptLoader)
        {
            m_ExeScheduler = exeScheduler;
            m_ScriptLoader = scriptLoader;
        }

        public void Start()
        {
            m_Thread = new Thread(WorkLoop)
            {
                Name = "PhloxMasterScheduler",
                Priority = PhloxEngine.SUBTASK_PRIORITY,
                IsBackground = true
            };
            m_Thread.Start();
        }

        public void Stop()
        {
            m_Stop = true;
            WorkArrived();
            m_Thread?.Join(5000);
            m_ScriptLoader.Stop();
            m_ExeScheduler.Stop();
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

                        WorkStatus exeStatus = m_ExeScheduler.DoWork();
                        WorkStatus loadStatus = m_ScriptLoader.DoWork();

                        // If either has work pending, loop immediately without waiting.
                        if (exeStatus.WorkIsPending || loadStatus.WorkIsPending)
                            continue;

                        ulong wakeAt = Math.Min(exeStatus.NextWakeUpTime, loadStatus.NextWakeUpTime);

                        if (wakeAt != ulong.MaxValue)
                        {
                            // Both wakeAt and EnvironmentTickCount are based on the same
                            // 30-bit masked tick value (see OpenSim.Framework.Util).
                            // Cast Int32 to long directly — the value is always non-negative
                            // (masked to 0x3FFFFFFF), so this is safe.
                            long now = (long)(uint)Util.EnvironmentTickCount();
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
                        m_log.ErrorFormat(
                            "[PhloxMaster]: CRITICAL — master scheduler crashed {0} times, script engine disabled. {1}",
                            crashCount, e);
                        m_Stop = true;
                        break;
                    }

                    m_log.ErrorFormat(
                        "[PhloxMaster]: Master scheduler exception (crash #{0}/{1}), restarting in 1s. {2}",
                        crashCount, MAX_CRASHES, e);

                    // Brief pause before restart to avoid tight crash loops
                    Thread.Sleep(1000);
                }
            }
        }
    }
}
