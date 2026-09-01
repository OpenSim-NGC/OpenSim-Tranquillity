/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
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

using System.Net;
using System.Text;
using OpenSim.Framework.Servers.HttpServer;

namespace OpenSim.Framework.Servers;

public sealed class MainServer : IMainServer
{
    private static readonly IMainServer _instance = new MainServer();

    public static IMainServer Instance => _instance;

    private MainServer()
    {
    }

//    private BaseHttpServer _defaultServer = null;
//    private BaseHttpServer _insecureServer = null;
    private Dictionary<uint, IHttpServer> _httpServers = new();

    /// <summary>
    /// Control the printing of certain debug messages.
    /// </summary>
    /// <remarks>
    /// If DebugLevel >= 1 then short warnings are logged when receiving bad input data.
    /// If DebugLevel >= 2 then long warnings are logged when receiving bad input data.
    /// If DebugLevel >= 3 then short notices about all incoming non-poll HTTP requests are logged.
    /// If DebugLevel >= 4 then the time taken to fulfill the request is logged.
    /// If DebugLevel >= 5 then the start of the body of incoming non-poll HTTP requests will be logged.
    /// If DebugLevel >= 6 then the entire body of incoming non-poll HTTP requests will be logged.
    /// </remarks>
    public int DebugLevel
    {
        get { return s_debugLevel; }
        set
        {
            s_debugLevel = value;

            lock (_httpServers)
                foreach (var server in _httpServers.Values)
                    server.DebugLevel = s_debugLevel;
        }
    }

    private int s_debugLevel;

    /// <summary>
    /// Set the main HTTP server instance.
    /// </summary>
    /// <remarks>
    /// This will be used to register all handlers that listen to the default port.
    /// </remarks>
    /// <exception cref='Exception'>
    /// Thrown if the HTTP server has not already been registered via AddHttpServer()
    /// </exception>
    public IHttpServer DefaultServer { get; private set; }

    /// <summary>
    /// Get all the registered servers.
    /// </summary>
    /// <remarks>
    /// Returns a copy of the dictionary so this can be iterated through without locking.
    /// </remarks>
    /// <value></value>
    public Dictionary<uint, IHttpServer> Servers
    {
        get { return new Dictionary<uint, IHttpServer>(_httpServers); }
    }

    public void RegisterHttpConsoleCommands(ICommandConsole console)
    {
        console.Commands.AddCommand(
            "Comms", false, "show http-handlers",
            "show http-handlers",
            "Show all registered http handlers", HandleShowHttpHandlersCommand);

        console.Commands.AddCommand(
            "Debug", false, "debug http", "debug http <in|out|all> [<level>]",
            "Turn on http request logging.",
            "If in or all and\n"
                + "  level <= 0 then no extra logging is done.\n"
                + "  level >= 1 then short warnings are logged when receiving bad input data.\n"
                + "  level >= 2 then long warnings are logged when receiving bad input data.\n"
                + "  level >= 3 then short notices about all incoming non-poll HTTP requests are logged.\n"
                + "  level >= 4 then the time taken to fulfill the request is logged.\n"
                + "  level >= 5 then a sample from the beginning of the data is logged.\n"
                + "  level >= 6 then the entire data is logged.\n"
                + "  no level is specified then the current level is returned.\n\n"
                + "If out or all and\n"
                + "  level >= 3 then short notices about all outgoing requests going through WebUtil are logged.\n"
                + "  level >= 4 then the time taken to fulfill the request is logged.\n"
                + "  level >= 5 then a sample from the beginning of the data is logged.\n"
                + "  level >= 6 then the entire data is logged.\n",
            HandleDebugHttpCommand);
    }

    /// <summary>
    /// Turn on some debugging values for OpenSim.
    /// </summary>
    /// <param name="args"></param>
    private void HandleDebugHttpCommand(string module, string[] cmdparams)
    {
        if (cmdparams.Length < 3)
        {
            MainConsole.Instance.Output("Usage: debug http <in|out|all> 0..6");
            return;
        }

        bool inReqs = false;
        bool outReqs = false;
        bool allReqs = false;

        string subCommand = cmdparams[2];

        if (subCommand.ToLower() == "in")
        {
            inReqs = true;
        }
        else if (subCommand.ToLower() == "out")
        {
            outReqs = true;
        }
        else if (subCommand.ToLower() == "all")
        {
            allReqs = true;
        }
        else
        {
            MainConsole.Instance.Output("You must specify in, out or all");
            return;
        }

        if (cmdparams.Length >= 4)
        {
            string rawNewDebug = cmdparams[3];
            int newDebug;

            if (!int.TryParse(rawNewDebug, out newDebug))
            {
                MainConsole.Instance.Output("{0} is not a valid debug level", rawNewDebug);
                return;
            }

            if (newDebug < 0 || newDebug > 6)
            {
                MainConsole.Instance.Output("{0} is outside the valid debug level range of 0..6", newDebug);
                return;
            }

            if (allReqs || inReqs)
            {
                DebugLevel = newDebug;
                MainConsole.Instance.Output("IN debug level set to {0}", newDebug);
            }

            if (allReqs || outReqs)
            {
                WebUtil.DebugLevel = newDebug;
                MainConsole.Instance.Output("OUT debug level set to {0}", newDebug);
            }
        }
        else
        {
            if (allReqs || inReqs)
                MainConsole.Instance.Output("Current IN debug level is {0}", DebugLevel);

            if (allReqs || outReqs)
                MainConsole.Instance.Output("Current OUT debug level is {0}", WebUtil.DebugLevel);
        }
    }

    private void HandleShowHttpHandlersCommand(string module, string[] args)
    {
        if (args.Length != 2)
        {
            MainConsole.Instance.Output("Usage: show http-handlers");
            return;
        }

        StringBuilder handlers = new StringBuilder();

        lock (_httpServers)
        {
            foreach (BaseHttpServer httpServer in _httpServers.Values)
            {
                handlers.AppendFormat(
                    "Registered HTTP Handlers for server at {0}:{1}\n", httpServer.ListenIPAddress, httpServer.Port);

                List<string> lst = httpServer.GetGLobalMethodsKeys();
                if (lst.Count > 0)
                {
                    handlers.AppendFormat("* Global query methods ({0}):\n", lst.Count);
                    foreach (string s in lst)
                        handlers.AppendFormat("\t***:{0}\n", s);
                }

                lst = httpServer.GetXmlRpcHandlerKeys();
                if (lst.Count > 0)
                {
                    handlers.AppendFormat("* XMLRPC methods ({0}):\n", lst.Count);
                    foreach (string s in lst)
                        handlers.AppendFormat("\t{0}\n", s);
                }

                lst = httpServer.GetJsonRpcHandlerKeys();
                if (lst.Count > 0)
                {
                    handlers.AppendFormat("* JSONRPC methods ({0}):\n", lst.Count);
                    foreach (string s in lst)
                        handlers.AppendFormat("\t{0}\n", s);
                }

                lst = httpServer.GetIndexPHPHandlerKeys();
                if (lst.Count > 0)
                {
                    handlers.AppendFormat("* index.php methods ({0}):\n", lst.Count);
                    foreach (string s in lst)
                        handlers.AppendFormat("\t{0}\n", s);
                }

                lst = httpServer.GetHTTPHandlerKeys();
                if (lst.Count > 0)
                {
                    handlers.AppendFormat("* HTTP ({0}):\n", lst.Count);
                    foreach (string s in lst)
                        handlers.AppendFormat("\t{0}\n", s);
                }

                lst = httpServer.GetPollServiceHandlerKeys();
                if (lst.Count > 0)
                {
                    handlers.AppendFormat("* HTTP poll ({0}):\n", lst.Count);
                    foreach (string s in lst)
                        handlers.AppendFormat("\t{0}\n", s);
                }

                lst = httpServer.GetLLSDHandlerKeys();
                if (lst.Count > 0)
                {
                    handlers.AppendFormat("* LLSD ({0}):\n", lst.Count);
                    foreach (string s in lst)
                        handlers.AppendFormat("\t{0}\n", s);
                }

                lst = httpServer.GetStreamHandlerKeys();
                if (lst.Count > 0)
                {
                    handlers.AppendFormat("* StreamHandlers ({0}):\n", lst.Count);
                    foreach (string s in lst)
                        handlers.AppendFormat("\t{0}\n", s);
                }

                lst = httpServer.GetSimpleStreamHandlerKeys();
                if (lst.Count > 0)
                {
                    handlers.AppendFormat("* SimpleStreamHandlers ({0}):\n", lst.Count);
                    foreach (string s in lst)
                        handlers.AppendFormat("\t***:{0}\n", s);
                }

                handlers.Append("\n");
            }
        }

        MainConsole.Instance.Output(handlers.ToString());
    }

    /// <summary>
    /// Register an already started HTTP server to the collection of known servers.
    /// </summary>
    /// <param name='server'></param>
    public void AddHttpServer(IHttpServer server)
    {
        lock (_httpServers)
        {
            if (_httpServers.ContainsKey(server.Port))
                throw new Exception(string.Format("HTTP server for port {0} already exists.", server.Port));
            _httpServers.Add(server.Port, server);

            if (DefaultServer is null)
                DefaultServer = server;
        }
    }

    /// <summary>
    /// Removes the http server listening on the given port.
    /// </summary>
    /// <remarks>
    /// It is the responsibility of the caller to do clean up.
    /// </remarks>
    /// <param name='port'></param>
    /// <returns></returns>
    public bool RemoveHttpServer(uint port)
    {
        lock (_httpServers)
        {
            if (DefaultServer != null && DefaultServer.Port == port)
                DefaultServer = null;

            return _httpServers.Remove(port);
        }
    }

    /// <summary>
    /// Does this collection of servers contain one with the given port?
    /// </summary>
    /// <remarks>
    /// Unlike GetHttpServer, this will not instantiate a server if one does not exist on that port.
    /// </remarks>
    /// <param name='port'></param>
    /// <returns>true if a server with the given port is registered, false otherwise.</returns>
    public bool ContainsHttpServer(uint port)
    {
        lock (_httpServers)
            return _httpServers.ContainsKey(port);
    }

    /// <summary>
    /// Get the default http server or an http server for a specific port.
    /// </summary>
    /// <remarks>
    /// If the requested HTTP server doesn't already exist then a new one is instantiated and started.
    /// </remarks>
    /// <returns></returns>
    /// <param name='port'>If 0 then the default HTTP server is returned.</param>
    public IHttpServer GetHttpServer(uint port)
    {
        return GetHttpServer(port, null);
    }

    /// <summary>
    /// Get the default http server, an http server for a specific port
    /// and/or an http server bound to a specific address
    /// </summary>
    /// <remarks>
    /// If the requested HTTP server doesn't already exist then a new one is instantiated and started.
    /// </remarks>
    /// <returns></returns>
    /// <param name='port'>If 0 then the default HTTP server is returned.</param>
    /// <param name='ipaddr'>A specific IP address to bind to.  If null then the default IP address is used.</param>
    public IHttpServer GetHttpServer(uint port, IPAddress ipaddr)
    {
        if (port == 0)
        {
            return DefaultServer;
        }

        if (DefaultServer != null && port == DefaultServer.Port)
        {
            return DefaultServer;
        }

        lock (_httpServers)
        {
            if (_httpServers.ContainsKey(port))
                return _httpServers[port];

            _httpServers[port] = new BaseHttpServer(port);

            if (ipaddr != null)
                _httpServers[port].ListenIPAddress = ipaddr;

            _httpServers[port].Start();

            return _httpServers[port];
        }
    }

    public void Stop()
    {
        lock (_httpServers)
        {
            foreach (BaseHttpServer httpServer in _httpServers.Values)
            {
                httpServer.Stop(true);
            }
        }
    }
}