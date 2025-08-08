/*
 * Copyright (c) Contributors, http://whitecore-sim.org/, http://aurora-sim.org, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the WhiteCore-Sim Project nor the
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

using System.Collections;
using System.Text;
using System.Web;
using System.Xml;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenMetaverse;

namespace OpenSim.Framework.Console;

public class ConsoleConnection
{
    public int last;
    public long lastLineSeen;
    public bool newConnection = true;
}

// A console that uses REST interfaces
//
public class RemoteConsole : CommandConsole, ICommandConsole
{
    private readonly IConfiguration _config;
    private readonly ILogger<RemoteConsole> _logger;
    
    private readonly Dictionary<UUID, ConsoleConnection> m_Connections = new();
    private readonly ManualResetEvent m_DataEvent = new(false);
    private readonly List<string> m_InputData = new();
    private readonly List<string> m_Scrollback = new();

    private uint _consolePort = 0;
    private long m_LineNumber;

    //private IHttpServer m_Server;
    private string _username = string.Empty;
    private string _password = string.Empty;

    public RemoteConsole(IConfiguration config, ILogger<RemoteConsole> logger) : base(config, logger)
    {
        _config = config;
        _logger = logger;
    }

    public override string Name => "RemoteConsole";

    public override bool Initialize( )
    {
        if (base.Initialize() is false)
            return false;

        var consoleConfig = _config.GetSection("Console");
        if (consoleConfig.Exists() is true)
        {
            _consolePort = consoleConfig.GetValue<uint>("remote_console_port", 0);
            _username = consoleConfig.GetValue("RemoteConsoleUser", string.Empty);
            _password = consoleConfig.GetValue("RemoteConsolePass", string.Empty);
        }
        else
        {
            return false;
        }

        //SetServer(m_consolePort == 0 ? MainServer.Instance : simBase.GetHttpServer(m_consolePort));
        
        return true;
    }

    // public void SetServer(IHttpServer server)
    // {
    //     m_Server = server;
    //
    //     m_Server.AddStreamHandler(new GenericStreamHandler("GET", "/StartSession/", HandleHttpStartSession));
    //     m_Server.AddStreamHandler(new GenericStreamHandler("GET", "/CloseSession/", HandleHttpCloseSession));
    //     m_Server.AddStreamHandler(new GenericStreamHandler("GET", "/SessionCommand/", HandleHttpSessionCommand));
    // }

    public override void Output(string text, Level level)
    {
        lock (m_Scrollback)
        {
            while (m_Scrollback.Count >= 1000)
                m_Scrollback.RemoveAt(0);
            m_LineNumber++;
            m_Scrollback.Add(string.Format("{0}", m_LineNumber) + ":" + level + ":" + text);
        }

        System.Console.WriteLine(text.Trim());
    }

    public override string ReadLine(string p, bool isCommand, bool e)
    {
        string cmdinput;

        if (isCommand)
            Output("+++" + p, Threshold);
        else
            Output("-++" + p, Threshold);

        lock (m_InputData)
        {
            m_DataEvent.WaitOne();

            if (m_InputData.Count == 0)
            {
                m_DataEvent.Reset();
                return "";
            }

            cmdinput = m_InputData[0];
            m_InputData.RemoveAt(0);
            if (m_InputData.Count == 0)
                m_DataEvent.Reset();
        }

        if (isCommand)
        {
            var cmd = Commands.Resolve(Parser.Parse(cmdinput));

            if (cmd.Length != 0)
            {
                int i;

                for (i = 0; i < cmd.Length; i++)
                    if (cmd[i].Contains(" "))
                        cmd[i] = "\"" + cmd[i] + "\"";
                return string.Empty;
            }
        }

        return cmdinput;
    }

    private void DoExpire()
    {
        var expired = new List<UUID>();

        lock (m_Connections)
        {
            expired.AddRange(from kvp in m_Connections
                where Environment.TickCount - kvp.Value.last > 500000
                select kvp.Key);

            foreach (var id in expired)
            {
                m_Connections.Remove(id);
                CloseConnection(id);
            }
        }
    }

    // private byte[] HandleHttpStartSession(string path, Stream request, OSHttpRequest httpRequest,
    //     OSHttpResponse httpResponse)
    // {
    //     DoExpire();
    //
    //     var post = DecodePostString(HttpServerHandlerHelpers.ReadString(request));
    //
    //     httpResponse.StatusCode = 401;
    //     httpResponse.ContentType = "text/plain";
    //     if (m_UserName == string.Empty)
    //         return MainServer.BlankResponse;
    //
    //     if (post["USER"] == null || post["PASS"] == null)
    //         return MainServer.BlankResponse;
    //
    //     if (m_UserName != post["USER"].ToString() ||
    //         m_Password != post["PASS"].ToString())
    //         return MainServer.BlankResponse;
    //
    //     var c = new ConsoleConnection { last = Environment.TickCount, lastLineSeen = 0 };
    //
    //     var sessionID = UUID.Random();
    //
    //     lock (m_Connections)
    //     {
    //         m_Connections[sessionID] = c;
    //     }
    //
    //     var uri = "/ReadResponses/" + sessionID + "/";
    //
    //     m_Server.AddPollServiceHTTPHandler(uri, new PollServiceEventArgs(null, HasEvents, GetEvents, NoEvents,
    //         sessionID));
    //
    //     var xmldoc = new XmlDocument();
    //     var xmlnode = xmldoc.CreateNode(XmlNodeType.XmlDeclaration, "", "");
    //
    //     xmldoc.AppendChild(xmlnode);
    //     var rootElement = xmldoc.CreateElement("", "ConsoleSession", "");
    //
    //     xmldoc.AppendChild(rootElement);
    //
    //     var id = xmldoc.CreateElement("", "SessionID", "");
    //     id.AppendChild(xmldoc.CreateTextNode(sessionID.ToString()));
    //
    //     rootElement.AppendChild(id);
    //
    //     var prompt = xmldoc.CreateElement("", "Prompt", "");
    //     prompt.AppendChild(xmldoc.CreateTextNode(DefaultPrompt));
    //
    //     rootElement.AppendChild(prompt);
    //
    //     httpResponse.StatusCode = 200;
    //     httpResponse.ContentType = "text/xml";
    //     return Encoding.UTF8.GetBytes(xmldoc.InnerXml);
    // }
    //
    // private byte[] HandleHttpCloseSession(string path, Stream request, OSHttpRequest httpRequest,
    //     OSHttpResponse httpResponse)
    // {
    //     DoExpire();
    //
    //     var post = DecodePostString(HttpServerHandlerHelpers.ReadString(request));
    //
    //     httpResponse.StatusCode = 401;
    //     httpResponse.ContentType = "text/plain";
    //     if (post["ID"] == null)
    //         return MainServer.BlankResponse;
    //
    //     UUID id;
    //     if (!UUID.TryParse(post["ID"].ToString(), out id))
    //         return MainServer.BlankResponse;
    //
    //     lock (m_Connections)
    //     {
    //         if (m_Connections.ContainsKey(id))
    //         {
    //             m_Connections.Remove(id);
    //             CloseConnection(id);
    //         }
    //     }
    //
    //     var xmldoc = new XmlDocument();
    //     var xmlnode = xmldoc.CreateNode(XmlNodeType.XmlDeclaration, "", "");
    //
    //     xmldoc.AppendChild(xmlnode);
    //     var rootElement = xmldoc.CreateElement("", "ConsoleSession", "");
    //
    //     xmldoc.AppendChild(rootElement);
    //
    //     var res = xmldoc.CreateElement("", "Result", "");
    //     res.AppendChild(xmldoc.CreateTextNode("OK"));
    //
    //     rootElement.AppendChild(res);
    //
    //     httpResponse.StatusCode = 200;
    //     httpResponse.ContentType = "text/xml";
    //     return Encoding.UTF8.GetBytes(xmldoc.InnerXml);
    // }
    //
    // private byte[] HandleHttpSessionCommand(string path, Stream request, OSHttpRequest httpRequest,
    //     OSHttpResponse httpResponse)
    // {
    //     DoExpire();
    //
    //     var post = DecodePostString(HttpServerHandlerHelpers.ReadString(request));
    //
    //     httpResponse.StatusCode = 401;
    //     httpResponse.ContentType = "text/plain";
    //     if (post["ID"] == null)
    //         return MainServer.BlankResponse;
    //
    //     UUID id;
    //     if (!UUID.TryParse(post["ID"].ToString(), out id))
    //         return MainServer.BlankResponse;
    //
    //     lock (m_Connections)
    //     {
    //         if (!m_Connections.ContainsKey(id))
    //             return MainServer.BlankResponse;
    //     }
    //
    //     if (post["COMMAND"] == null)
    //         return MainServer.BlankResponse;
    //
    //     lock (m_InputData)
    //     {
    //         m_DataEvent.Set();
    //         m_InputData.Add(post["COMMAND"].ToString());
    //     }
    //
    //     var xmldoc = new XmlDocument();
    //     var xmlnode = xmldoc.CreateNode(XmlNodeType.XmlDeclaration, "", "");
    //
    //     xmldoc.AppendChild(xmlnode);
    //     var rootElement = xmldoc.CreateElement("", "ConsoleSession", "");
    //
    //     xmldoc.AppendChild(rootElement);
    //
    //     var res = xmldoc.CreateElement("", "Result", "");
    //     res.AppendChild(xmldoc.CreateTextNode("OK"));
    //
    //     rootElement.AppendChild(res);
    //
    //     httpResponse.StatusCode = 200;
    //     httpResponse.ContentType = "text/xml";
    //     return Encoding.UTF8.GetBytes(xmldoc.InnerXml);
    // }

    private Hashtable DecodePostString(string data)
    {
        var result = new Hashtable();

        var terms = data.Split(new[] { '&' });

        foreach (var term in terms)
        {
            var elems = term.Split(new[] { '=' });
            if (elems.Length == 0)
                continue;

            var name = HttpUtility.UrlDecode(elems[0]);
            var value = string.Empty;

            if (elems.Length > 1)
                value = HttpUtility.UrlDecode(elems[1]);

            result[name] = value;
        }

        return result;
    }

    public void CloseConnection(UUID id)
    {
        try
        {
            var uri = "/ReadResponses/" + id + "/";
            //m_Server.RemovePollServiceHTTPHandler("", uri);
        }
        catch (Exception)
        {
        }
    }

    private bool HasEvents(UUID RequestID, UUID sessionID)
    {
        ConsoleConnection c;

        lock (m_Connections)
        {
            if (!m_Connections.ContainsKey(sessionID))
                return false;
            c = m_Connections[sessionID];
        }

        c.last = Environment.TickCount;
        lock (m_Scrollback)
        {
            if (c.lastLineSeen < m_LineNumber)
                return true;
            return false;
        }
    }

    // private byte[] GetEvents(UUID RequestID, UUID sessionID, string req, OSHttpResponse response)
    // {
    //     ConsoleConnection c;
    //
    //     lock (m_Connections)
    //     {
    //         if (!m_Connections.ContainsKey(sessionID))
    //             return NoEvents(RequestID, UUID.Zero, response);
    //         c = m_Connections[sessionID];
    //     }
    //
    //     c.last = Environment.TickCount;
    //     lock (m_Scrollback)
    //     {
    //         if (c.lastLineSeen >= m_LineNumber)
    //             return NoEvents(RequestID, UUID.Zero, response);
    //     }
    //
    //     var xmldoc = new XmlDocument();
    //     var xmlnode = xmldoc.CreateNode(XmlNodeType.XmlDeclaration, "", "");
    //
    //     xmldoc.AppendChild(xmlnode);
    //     var rootElement = xmldoc.CreateElement("", "ConsoleSession", "");
    //
    //     if (c.newConnection)
    //     {
    //         c.newConnection = false;
    //         Output("+++" + DefaultPrompt, Threshold);
    //     }
    //
    //     lock (m_Scrollback)
    //     {
    //         var startLine = m_LineNumber - m_Scrollback.Count;
    //         var sendStart = startLine;
    //         if (sendStart < c.lastLineSeen)
    //             sendStart = c.lastLineSeen;
    //
    //         for (var i = sendStart; i < m_LineNumber; i++)
    //         {
    //             var res = xmldoc.CreateElement("", "Line", "");
    //             var line = i + 1;
    //             res.SetAttribute("Number", line.ToString());
    //             res.AppendChild(xmldoc.CreateTextNode(m_Scrollback[(int)(i - startLine)]));
    //
    //             rootElement.AppendChild(res);
    //         }
    //
    //         c.lastLineSeen = m_LineNumber;
    //     }
    //
    //     xmldoc.AppendChild(rootElement);
    //
    //
    //     response.StatusCode = 200;
    //     response.ContentType = "application/xml";
    //
    //     return Encoding.UTF8.GetBytes(xmldoc.InnerXml);
    // }
    //
    // private byte[] NoEvents(UUID RequestID, UUID id, OSHttpResponse response)
    // {
    //     var xmldoc = new XmlDocument();
    //     var xmlnode = xmldoc.CreateNode(XmlNodeType.XmlDeclaration, "", "");
    //
    //     xmldoc.AppendChild(xmlnode);
    //     var rootElement = xmldoc.CreateElement("", "ConsoleSession", "");
    //
    //     xmldoc.AppendChild(rootElement);
    //
    //     response.StatusCode = 200;
    //     response.ContentType = "text/xml";
    //     return Encoding.UTF8.GetBytes(xmldoc.InnerXml);
    // }
}