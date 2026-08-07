/*
 * Copyright (c) InWorldz Halcyon Developers
 * Copyright (c) Contributors, http://opensimulator.org/
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSim Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

// Ported from Halcyon/InWorldz to Legion Grid (dotnet8-modernization)
// Adaptations:
//   - ThreadTracker removed (not present in modern OpenSim)
//   - IScriptEngine is PhloxEngine which already implements the interface
//   - Timer and Dataserver plugins excluded — handled natively by Phlox
//   - Listener plugin excluded — handled by PhloxListenManager

using System;
using System.Collections.Generic;
using System.Threading;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.Api.Plugins;

namespace OpenSim.Region.ScriptEngine.Shared.Api
{
    /// <summary>
    /// Handles LSL commands that take a long time and return an event:
    /// sensors, HTTP requests, and XMLRPC.
    ///
    /// Timer, Dataserver, and Listener are handled natively by PhloxEngine
    /// and are NOT managed here.
    /// </summary>
    public class AsyncCommandManager
    {
        private static readonly log4net.ILog m_log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static Thread cmdHandlerThread;
        private static int cmdHandlerThreadCycleSleepms = 100;

        // Rate-limit for pump-loop error logging (ms of Environment.TickCount64)
        private static long m_lastPumpErrorLog;

        private static readonly List<IScriptEngine> m_ScriptEngines =
            new List<IScriptEngine>();

        public IScriptEngine m_ScriptEngine;

        private static readonly Dictionary<IScriptEngine, SensorRepeat> m_SensorRepeat =
            new Dictionary<IScriptEngine, SensorRepeat>();
        private static readonly Dictionary<IScriptEngine, HttpRequest> m_HttpRequest =
            new Dictionary<IScriptEngine, HttpRequest>();
        private static readonly Dictionary<IScriptEngine, XmlRequest> m_XmlRequest =
            new Dictionary<IScriptEngine, XmlRequest>();

        public SensorRepeat SensorRepeatPlugin
        {
            get { return m_SensorRepeat[m_ScriptEngine]; }
        }

        public HttpRequest HttpRequestPlugin
        {
            get { return m_HttpRequest[m_ScriptEngine]; }
        }

        public XmlRequest XmlRequestPlugin
        {
            get { return m_XmlRequest[m_ScriptEngine]; }
        }

        public IScriptEngine[] ScriptEngines
        {
            get { return m_ScriptEngines.ToArray(); }
        }

        public AsyncCommandManager(IScriptEngine scriptEngine)
        {
            m_ScriptEngine = scriptEngine;

            lock (m_ScriptEngines)
            {
                if (!m_ScriptEngines.Contains(m_ScriptEngine))
                    m_ScriptEngines.Add(m_ScriptEngine);

                if (!m_SensorRepeat.ContainsKey(m_ScriptEngine))
                    m_SensorRepeat[m_ScriptEngine] = new SensorRepeat(this);
                if (!m_HttpRequest.ContainsKey(m_ScriptEngine))
                    m_HttpRequest[m_ScriptEngine] = new HttpRequest(this);
                if (!m_XmlRequest.ContainsKey(m_ScriptEngine))
                    m_XmlRequest[m_ScriptEngine] = new XmlRequest(this);
            }

            StartThread();
        }

        public void Shutdown()
        {
            lock (m_ScriptEngines)
            {
                m_ScriptEngines.Remove(m_ScriptEngine);
                m_SensorRepeat.Remove(m_ScriptEngine);
                m_HttpRequest.Remove(m_ScriptEngine);
                m_XmlRequest.Remove(m_ScriptEngine);
            }
        }

        private static void StartThread()
        {
            if (cmdHandlerThread != null && cmdHandlerThread.IsAlive)
                return;

            cmdHandlerThread = new Thread(CmdHandlerThreadLoop);
            cmdHandlerThread.Name = "PhloxAsyncCmdHandlerThread";
            cmdHandlerThread.IsBackground = true;
            cmdHandlerThread.Start();
        }

        private static void CmdHandlerThreadLoop()
        {
            while (true)
            {
                try
                {
                    while (true)
                    {
                        Thread.Sleep(cmdHandlerThreadCycleSleepms);
                        DoOneCmdHandlerPass();
                    }
                }
                catch (ThreadAbortException)
                {
                    return;
                }
                catch (Exception e)
                {
                    // Keep running on unexpected errors, but never silently: this pump is
                    // the only conduit for HTTP/XMLRPC/sensor results, and a swallowed
                    // recurring fault here looks like "async events never arrive".
                    // Rate-limited so an exception storm cannot flood the log.
                    long now = Environment.TickCount64;
                    if (now - m_lastPumpErrorLog > 10000)
                    {
                        m_lastPumpErrorLog = now;
                        m_log.Error("[PhloxAsyncCmd]: async command pump pass failed (pump continues): ", e);
                    }
                }
            }
        }

        private static void DoOneCmdHandlerPass()
        {
            IScriptEngine[] engines;
            lock (m_ScriptEngines)
                engines = m_ScriptEngines.ToArray();

            if (engines.Length == 0)
                return;

            // HTTP and XMLRPC only need one pass across all engines
            if (m_HttpRequest.TryGetValue(engines[0], out HttpRequest httpPlugin))
                httpPlugin.CheckHttpRequests();

            if (m_XmlRequest.TryGetValue(engines[0], out XmlRequest xmlPlugin))
                xmlPlugin.CheckXMLRPCRequests();

            // Sensors are per-engine
            foreach (IScriptEngine engine in engines)
            {
                if (m_SensorRepeat.TryGetValue(engine, out SensorRepeat sensorPlugin))
                    sensorPlugin.CheckSenseRepeaterEvents();
            }
        }

        /// <summary>
        /// Remove a specific script and all its pending async commands.
        /// </summary>
        public static void RemoveScript(IScriptEngine engine, uint localID, UUID itemID)
        {
            if (m_SensorRepeat.TryGetValue(engine, out SensorRepeat sr))
                sr.UnSetSenseRepeaterEvents(localID, itemID);

            IHttpRequestModule iHttpReq =
                engine.World.RequestModuleInterface<IHttpRequestModule>();
            iHttpReq?.StopHttpRequest(localID, itemID);

            IXMLRPC xmlrpc = engine.World.RequestModuleInterface<IXMLRPC>();
            if (xmlrpc != null)
            {
                xmlrpc.DeleteChannels(itemID);
                xmlrpc.CancelSRDRequests(itemID);
            }
        }

        /// <summary>
        /// Returns serialization data for sensors (used for script state persistence).
        /// </summary>
        public static object[] GetSerializationData(IScriptEngine engine, UUID itemID)
        {
            var data = new List<object>();

            if (m_SensorRepeat.TryGetValue(engine, out SensorRepeat sr))
            {
                object[] sensors = sr.GetSerializationData(itemID);
                if (sensors.Length > 0)
                {
                    data.Add("sensor");
                    data.Add(sensors.Length);
                    data.AddRange(sensors);
                }
            }

            return data.ToArray();
        }

        /// <summary>
        /// Restores async command state from serialized data.
        /// </summary>
        public static void CreateFromData(IScriptEngine engine, uint localID,
            UUID itemID, UUID hostID, object[] data)
        {
            int idx = 0;
            while (idx < data.Length)
            {
                string type = data[idx].ToString();
                int len = (int)data[idx + 1];
                idx += 2;

                if (len > 0)
                {
                    object[] item = new object[len];
                    Array.Copy(data, idx, item, 0, len);
                    idx += len;

                    if (type == "sensor" && m_SensorRepeat.TryGetValue(engine, out SensorRepeat sr))
                        sr.CreateFromData(localID, itemID, hostID, item);
                }
            }
        }
    }
}
