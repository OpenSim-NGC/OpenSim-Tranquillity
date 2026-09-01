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

    static int Main(string[] args)
    {
        var logconfigOption = new Option<string>("--logconfig")
        {
            Description = "Instruct log4net to use this file as configuration file.",
            DefaultValueFactory = _ => "OpenSim.ConsoleClient.dll.config",
        };
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

        rootCommand.Options.Add(logconfigOption);
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
            logConfig: parseResult.GetValue(logconfigOption),
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
        string logConfig,
        List<string> iniFiles,
        string iniMaster,
        string consoleType,
        string host,
        int? port,
        string user,
        string pass)
    {
        ILog4NetBootstrapper log4NetBootstrapper = new Log4NetBootstrapper();
        log4NetBootstrapper.Configure(logConfig, "OpenSim.ConsoleClient.dll.config");

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

        List<string> lines = new List<string>();

        foreach (XmlNode part in rootNodeL[0].ChildNodes)
        {
            if (part.Name != "Line")
                continue;

            lines.Add(part.InnerText);
        }

        // Cut down scrollback to 100 lines (4 screens)
        // for the command line client
        //
        while (lines.Count > 100)
            lines.RemoveAt(0);

        string prompt = String.Empty;

        foreach (string l in lines)
        {
            string[] parts = l.Split(new char[] {':'}, 3);
            if (parts.Length != 3)
                continue;

            if (parts[2].StartsWith("+++") || parts[2].StartsWith("-++"))
                prompt = parts[2];
            else
                MainConsole.Instance.Output(parts[2].Trim(), parts[1]);
        }


        Requester.MakeRequest(requestUrl, requestData, ReadResponses);

//            if (prompt.StartsWith("+++"))
            MainConsole.Instance.ReadLine(prompt.Substring(0), true, true);
//            else if (prompt.StartsWith("-++"))
//                SendCommand(String.Empty, new string[] { MainConsole.Instance.ReadLine(prompt.Substring(3), false, true) });
    }

    public static void CommandReply(string requestUrl, string requestData, string replyData)
    {
    }
}
