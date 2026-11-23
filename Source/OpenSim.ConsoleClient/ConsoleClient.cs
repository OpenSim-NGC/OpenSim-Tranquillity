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

using Nini.Config;
using System.Xml;
using System.CommandLine;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Server.Base.Hosting;
using OpenMetaverse;

namespace OpenSim.ConsoleClient;

public class OpenSimConsoleClient
{
    private static bool m_Running = true;
    private static string m_Host;
    private static int m_Port;
    private static string m_User;
    private static string m_Pass;
    private static UUID m_SessionID;
    private static string m_Prompt = String.Empty;
    private static bool m_NewSession;

    static int Main(string[] args)
    {
        var inifileOption = new Option<List<string>>("--inifile")
        {
            Description = "Specify the location of zero or more .ini file(s) to read."
        };
        var inimasterOption = new Option<string>("--inimaster")
        {
            Description = "The path to the master ini file. The master ini file will be read first and then overridden by any .ini files specified by --inifile options.",
            DefaultValueFactory = _ => "OpenSim.ConsoleClient.ini",
        };
        var consoleOption = new Option<string>("--console")
        {
            Description = "console type, one of basic, local, rest or mock.",
            DefaultValueFactory = _ => "local",
        };
        consoleOption.AcceptOnlyFromAmong("basic", "local", "rest", "mock");

        var hostOption = new Option<string>("--host", "-h")
        {
            Description = "The remote console host to connect to."
        };
        var portOption = new Option<int?>("--port", "-p")
        {
            Description = "The remote console port to connect to."
        };
        var userOption = new Option<string>("--user", "-u")
        {
            Description = "The user name used to authenticate with the remote console."
        };
        var passOption = new Option<string>("--pass", "-P")
        {
            Description = "The password used to authenticate with the remote console."
        };

        RootCommand rootCommand = new RootCommand("OpenSim Remote Console Client");

        rootCommand.Options.Add(inifileOption);
        rootCommand.Options.Add(inimasterOption);
        rootCommand.Options.Add(consoleOption);
        rootCommand.Options.Add(hostOption);
        rootCommand.Options.Add(portOption);
        rootCommand.Options.Add(userOption);
        rootCommand.Options.Add(passOption);

        ParseResult parseResult = rootCommand.Parse(args);

        if (parseResult.Errors.Count != 0)
        {
            foreach (var parseError in parseResult.Errors)
                System.Console.Error.WriteLine(parseError.Message);

            return 1;
        }

        rootCommand.SetAction(parseResult => Run(
            iniFiles: parseResult.GetValue(inifileOption),
            iniMaster: parseResult.GetValue(inimasterOption),
            consoleType: parseResult.GetValue(consoleOption),
            host: parseResult.GetValue(hostOption),
            port: parseResult.GetValue(portOption),
            user: parseResult.GetValue(userOption),
            pass: parseResult.GetValue(passOption)));

        return rootCommand.Parse(args).Invoke();
    }

    static void Run(
        List<string> iniFiles,
        string iniMaster,
        string consoleType,
        string host,
        int? port,
        string user,
        string pass)
    {
        // Request failures go to stderr even when no logging host is configured.
        IConfigSource config = LoadConfig(iniMaster, iniFiles);
        IConfig startupConfig = config.Configs["Startup"];

        // Command-line options take precedence over the ini file, which in turn
        // overrides the built-in defaults.
        m_User = !string.IsNullOrEmpty(user) ? user : startupConfig?.GetString("user", "Test") ?? "Test";
        m_Host = !string.IsNullOrEmpty(host) ? host : startupConfig?.GetString("host", "localhost") ?? "localhost";
        m_Port = port ?? startupConfig?.GetInt("port", 8003) ?? 8003;
        m_Pass = !string.IsNullOrEmpty(pass) ? pass : startupConfig?.GetString("pass", "secret") ?? "secret";

        string prompt = "Client> ";
        MainConsole.Instance = consoleType switch
        {
            "basic" => new CommandConsole(prompt),
            "rest" => new RemoteConsole(prompt),
            "mock" => new MockConsole(),
            _ => new LocalConsole(prompt),
        };

        Requester.MakeRequest("http://"+m_Host+":"+m_Port.ToString()+"/StartSession/", String.Format("USER={0}&PASS={1}", m_User, m_Pass), LoginReply);

        while (m_Running)
        {
            System.Threading.Thread.Sleep(500);
            MainConsole.Instance.Prompt();
        }

        string pidFile = startupConfig?.GetString("PIDFile", string.Empty) ?? string.Empty;
        if (pidFile.Length > 0)
            File.Delete(pidFile);

        Environment.Exit(0);
    }

    private static IConfigSource LoadConfig(string iniMaster, List<string> iniFiles)
    {
        IniConfigSource config = new IniConfigSource();

        if (!string.IsNullOrEmpty(iniMaster) && File.Exists(iniMaster))
            config.Merge(new IniConfigSource(iniMaster));

        if (iniFiles != null)
        {
            foreach (string iniFile in iniFiles)
            {
                if (File.Exists(iniFile))
                    config.Merge(new IniConfigSource(iniFile));
            }
        }

        return config;
    }

    private static void SendCommand(string module, string[] cmd)
    {
        string sendCmd = "";
        string[] cmdlist = new string[cmd.Length - 1];

        sendCmd = cmd[0];

        if (cmd.Length > 1)
        {
            Array.Copy(cmd, 1, cmdlist, 0, cmd.Length - 1);
            sendCmd += " \"" + String.Join("\" \"", cmdlist) + "\"";
        }

        Requester.MakeRequest("http://"+m_Host+":"+m_Port.ToString()+"/SessionCommand/", String.Format("ID={0}&COMMAND={1}", m_SessionID, sendCmd), CommandReply);
    }

    public static void LoginReply(string requestUrl, string requestData, string replyData)
    {
        XmlDocument doc = new XmlDocument();

        doc.LoadXml(replyData);

        XmlNodeList rootL = doc.GetElementsByTagName("ConsoleSession");
        if (rootL.Count != 1)
        {
            MainConsole.Instance.Output("Connection data info was not valid");
            Environment.Exit(1);
        }
        XmlElement rootNode = (XmlElement)rootL[0];

        if (rootNode == null)
        {
            MainConsole.Instance.Output("Connection data info was not valid");
            Environment.Exit(1);
        }

        XmlNodeList helpNodeL = rootNode.GetElementsByTagName("HelpTree");
        if (helpNodeL.Count != 1)
        {
            MainConsole.Instance.Output("Connection data info was not valid");
            Environment.Exit(1);
        }

        XmlElement helpNode = (XmlElement)helpNodeL[0];
        if (helpNode == null)
        {
            MainConsole.Instance.Output("Connection data info was not valid");
            Environment.Exit(1);
        }

        XmlNodeList sessionL = rootNode.GetElementsByTagName("SessionID");
        if (sessionL.Count != 1)
        {
            MainConsole.Instance.Output("Connection data info was not valid");
            Environment.Exit(1);
        }

        XmlElement sessionNode = (XmlElement)sessionL[0];
        if (sessionNode == null)
        {
            MainConsole.Instance.Output("Connection data info was not valid");
            Environment.Exit(1);
        }

        if (!UUID.TryParse(sessionNode.InnerText, out m_SessionID))
        {
            MainConsole.Instance.Output("Connection data info was not valid");
            Environment.Exit(1);
        }

        // The console's prompt; the first lines of the session are scrollback and do not say it (see ShowLines).
        m_Prompt = String.Empty;
        foreach (XmlNode part in rootNode.ChildNodes)
        {
            if (part is XmlElement promptNode && promptNode.Name == "Prompt")
                m_Prompt = promptNode.InnerText;
        }
        m_NewSession = true;

        MainConsole.Instance.Commands.FromXml(helpNode, SendCommand);

        Requester.MakeRequest("http://"+m_Host+":"+m_Port.ToString()+"/ReadResponses/"+m_SessionID.ToString()+"/", String.Empty, ReadResponses);
    }

    public static void ReadResponses(string requestUrl, string requestData, string replyData)
    {
        XmlDocument doc = new XmlDocument();

        doc.LoadXml(replyData);

        XmlNodeList rootNodeL = doc.GetElementsByTagName("ConsoleSession");
        if (rootNodeL.Count != 1 || rootNodeL[0] == null)
        {
            Requester.MakeRequest(requestUrl, requestData, ReadResponses);
            return;
        }

        XmlElement session = (XmlElement)rootNodeL[0];
        bool newSession = m_NewSession;
        // The server sends the scrollback with the first reply that has any line in it.
        if (session.GetElementsByTagName("Line").Count > 0)
            m_NewSession = false;

        m_Prompt = ShowLines(session, MainConsole.Instance, m_Prompt, newSession);

        Requester.MakeRequest(requestUrl, requestData, ReadResponses);

//            if (prompt.StartsWith("+++"))
            MainConsole.Instance.ReadLine(m_Prompt, true, true);
//            else if (prompt.StartsWith("-++"))
//                SendCommand(String.Empty, new string[] { MainConsole.Instance.ReadLine(prompt.Substring(3), false, true) });
    }

    /// <summary>
    /// Shows the Line elements of a ReadResponses reply on the console and answers the prompt to read with: the last
    /// prompt among them, or <paramref name="prompt"/> when there is none. RemoteConsole.GetEvents sends each line's
    /// text as it was said, with its level and whether it is a prompt as attributes. A line without a Number attribute
    /// is in the older "number:level:text" form; a line of that kind that does not split in three is shown whole.
    /// </summary>
    /// <remarks>
    /// The first lines of a session (<paramref name="newSession"/>) are the console's scrollback. For those,
    /// GetEvents leaves out each line's own prompt flag and sets Prompt on the last line to whether the console is
    /// waiting for input, whatever that line is: on a live region it is often a log line said after the prompt. That
    /// line is the prompt only when its text is the prompt StartSession named (<paramref name="prompt"/>); any other
    /// line is shown, and the prompt stays the one StartSession named.
    /// </remarks>
    public static string ShowLines(XmlElement session, ICommandConsole console, string prompt, bool newSession)
    {
        List<XmlElement> lines = new List<XmlElement>();

        foreach (XmlNode part in session.ChildNodes)
        {
            if (part is XmlElement line && line.Name == "Line")
                lines.Add(line);
        }

        // Cut down scrollback to 100 lines (4 screens)
        // for the command line client
        //
        while (lines.Count > 100)
            lines.RemoveAt(0);

        foreach (XmlElement line in lines)
        {
            string text = line.InnerText;
            string level = line.GetAttribute("Level");
            bool isPrompt = line.GetAttribute("Prompt") == "true";

            if (line.HasAttribute("Number"))
            {
                if (isPrompt && newSession && text.Trim() != prompt.Trim())
                    isPrompt = false;
            }
            else
            {
                string[] parts = text.Split(new char[] {':'}, 3);
                if (parts.Length == 3)
                {
                    level = parts[1];
                    text = parts[2];
                    isPrompt = text.StartsWith("+++") || text.StartsWith("-++");
                }
            }

            if (isPrompt)
                prompt = text;
            else
                // A ConsoleLevel, not a string: a string would be taken as a format argument for the text.
                console.Output(text.Trim(), (ConsoleLevel)level);
        }

        return prompt;
    }

    public static void CommandReply(string requestUrl, string requestData, string replyData)
    {
    }
}
