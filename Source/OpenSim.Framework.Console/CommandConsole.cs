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

using System.Text.RegularExpressions;
using System.Xml;
using Nini.Config;

namespace OpenSim.Framework.Console;

public class Commands : ICommands
{
    public const string GeneralHelpText
        = "To enter an argument that contains spaces, surround the argument with double quotes.\nFor example, show object name \"My long object name\"\n";

    public const string ItemHelpText
        = @"For more information, type 'help all' to get a list of all commands,
              or type help <item>' where <item> is one of the following:";

    /// <summary>
    ///     Commands organized by module
    /// </summary>
    private readonly Dictionary<string, List<CommandInfo>> _modulesCommands = new();

    /// <value>
    ///     Commands organized by keyword in a tree
    /// </value>
    private readonly Dictionary<string, object> tree = new();

    /// <summary>
    ///     Get help for the given help string
    /// </summary>
    /// <param name="helpParts">Parsed parts of the help string.  If empty then general help is returned.</param>
    /// <returns></returns>
    public List<string> GetHelp(string[] cmd)
    {
        var help = new List<string>();
        var helpParts = new List<string>(cmd);

        // Remove initial help keyword
        helpParts.RemoveAt(0);

        help.Add(""); // Will become a newline.

        // General help
        if (helpParts.Count == 0)
        {
            help.Add(GeneralHelpText);
            help.Add(ItemHelpText);
            help.AddRange(CollectModulesHelp(tree));
        }
        else if (helpParts.Count == 1 && helpParts[0] == "all")
        {
            help.AddRange(CollectAllCommandsHelp());
        }
        else
        {
            help.AddRange(CollectHelp(helpParts));
        }

        help.Add(""); // Will become a newline.

        return help;
    }

//        private List<string> CollectHelp(Dictionary<string, object> dict)
//        {
//            List<string> result = new List<string>();
//
//            foreach (KeyValuePair<string, object> kvp in dict)
//            {
//                if (kvp.Value is Dictionary<string, Object>)
//                {
//                    result.AddRange(CollectHelp((Dictionary<string, Object>)kvp.Value));
//                }
//                else
//                {
//                    if (((CommandInfo)kvp.Value).long_help != String.Empty)
//                        result.Add(((CommandInfo)kvp.Value).help_text+" - "+
//                                ((CommandInfo)kvp.Value).long_help);
//                }
//            }
//            return result;
//        }

/// <summary>
///     Add a command to those which can be invoked from the console.
/// </summary>
/// <param name="module"></param>
/// <param name="command"></param>
/// <param name="help"></param>
/// <param name="longhelp"></param>
/// <param name="fn"></param>
public void AddCommand(string module, bool shared, string command,
        string help, string longhelp, CommandDelegate fn)
    {
        AddCommand(module, shared, command, help, longhelp, string.Empty, fn);
    }

    /// <summary>
    ///     Add a command to those which can be invoked from the console.
    /// </summary>
    /// <param name="module"></param>
    /// <param name="command"></param>
    /// <param name="help"></param>
    /// <param name="longhelp"></param>
    /// <param name="descriptivehelp"></param>
    /// <param name="fn"></param>
    public void AddCommand(string module, bool shared, string command,
        string help, string longhelp, string descriptivehelp,
        CommandDelegate fn)
    {
        var parts = Parser.Parse(command);

        var current = tree;

        foreach (var part in parts)
            if (current.ContainsKey(part))
            {
                if (current[part] is Dictionary<string, object>)
                    current = (Dictionary<string, object>)current[part];
                else
                    return;
            }
            else
            {
                current[part] = new Dictionary<string, object>();
                current = (Dictionary<string, object>)current[part];
            }

        CommandInfo info;

        if (current.ContainsKey(string.Empty))
        {
            info = (CommandInfo)current[string.Empty];
            if (!info.Shared && !info.Fn.Contains(fn))
                info.Fn.Add(fn);

            return;
        }

        info = new CommandInfo();
        info.Module = module;
        info.Shared = shared;
        info.HelpText = help;
        info.LongHelp = longhelp;
        info.DescriptiveHelp = descriptivehelp;
        info.Fn = new List<CommandDelegate>();
        info.Fn.Add(fn);
        current[string.Empty] = info;

        // Now add command to modules dictionary
        lock (_modulesCommands)
        {
            List<CommandInfo> commands;
            if (_modulesCommands.ContainsKey(module))
            {
                commands = _modulesCommands[module];
            }
            else
            {
                commands = new List<CommandInfo>();
                _modulesCommands[module] = commands;
            }

//                m_log.DebugFormat("[COMMAND CONSOLE]: Adding to category {0} command {1}", module, command);
            commands.Add(info);
        }
    }

    public string[] FindNextOption(string[] cmd, bool term)
    {
        var current = tree;

        var remaining = cmd.Length;

        foreach (var s in cmd)
        {
            remaining--;

            var found = new List<string>();

            foreach (var opt in current.Keys)
            {
                if (remaining > 0 && opt == s)
                {
                    found.Clear();
                    found.Add(opt);
                    break;
                }

                if (opt.StartsWith(s)) found.Add(opt);
            }

            if (found.Count == 1 && (remaining != 0 || term))
                current = (Dictionary<string, object>)current[found[0]];
            else if (found.Count > 0)
                return found.ToArray();
            else
                break;
            //                    return new string[] {"<cr>"};
        }

        if (current.Count > 1)
        {
            var choices = new List<string>();

            var addcr = false;
            foreach (var s in current.Keys)
                if (s.Length == 0)
                {
                    var ci = (CommandInfo)current[string.Empty];
                    if (ci.Fn.Count != 0)
                        addcr = true;
                }
                else
                {
                    choices.Add(s);
                }

            if (addcr)
                choices.Add("<cr>");
            return choices.ToArray();
        }

        if (current.ContainsKey(string.Empty))
            return new[] { "Command help: " + ((CommandInfo)current[string.Empty]).HelpText };

        return new[] { new List<string>(current.Keys)[0] };
    }

    public bool HasCommand(string command)
    {
        string[] result;
        return ResolveCommand(Parser.Parse(command), out result) != null;
    }

    public string[] Resolve(string[] cmd)
    {
        string[] result;
        var ci = ResolveCommand(cmd, out result);

        if (ci == null)
            return new string[0];

        if (ci.Fn.Count == 0)
            return new string[0];

        foreach (var fn in ci.Fn)
            if (fn != null)
                fn(ci.Module, result);
            else
                return new string[0];

        return result;
    }

    public XmlElement GetXml(XmlDocument doc)
    {
        var help = (CommandInfo)((Dictionary<string, object>)tree["help"])[string.Empty];
        ((Dictionary<string, object>)tree["help"]).Remove(string.Empty);
        if (((Dictionary<string, object>)tree["help"]).Count == 0)
            tree.Remove("help");

        var quit = (CommandInfo)((Dictionary<string, object>)tree["quit"])[string.Empty];
        ((Dictionary<string, object>)tree["quit"]).Remove(string.Empty);
        if (((Dictionary<string, object>)tree["quit"]).Count == 0)
            tree.Remove("quit");

        var root = doc.CreateElement("", "HelpTree", "");

        ProcessTreeLevel(tree, root, doc);

        if (!tree.ContainsKey("help"))
            tree["help"] = new Dictionary<string, object>();
        ((Dictionary<string, object>)tree["help"])[string.Empty] = help;

        if (!tree.ContainsKey("quit"))
            tree["quit"] = new Dictionary<string, object>();
        ((Dictionary<string, object>)tree["quit"])[string.Empty] = quit;

        return root;
    }

    public void FromXml(XmlElement root, CommandDelegate fn)
    {
        var help = (CommandInfo)((Dictionary<string, object>)tree["help"])[string.Empty];
        ((Dictionary<string, object>)tree["help"]).Remove(string.Empty);
        if (((Dictionary<string, object>)tree["help"]).Count == 0)
            tree.Remove("help");

        var quit = (CommandInfo)((Dictionary<string, object>)tree["quit"])[string.Empty];
        ((Dictionary<string, object>)tree["quit"]).Remove(string.Empty);
        if (((Dictionary<string, object>)tree["quit"]).Count == 0)
            tree.Remove("quit");

        tree.Clear();

        ReadTreeLevel(tree, root, fn);

        if (!tree.ContainsKey("help"))
            tree["help"] = new Dictionary<string, object>();
        ((Dictionary<string, object>)tree["help"])[string.Empty] = help;

        if (!tree.ContainsKey("quit"))
            tree["quit"] = new Dictionary<string, object>();
        ((Dictionary<string, object>)tree["quit"])[string.Empty] = quit;
    }

    /// <summary>
    ///     Collects the help from all commands and return in alphabetical order.
    /// </summary>
    /// <returns></returns>
    private List<string> CollectAllCommandsHelp()
    {
        var help = new List<string>();

        lock (_modulesCommands)
        {
            foreach (var commands in _modulesCommands.Values)
            {
                var ourHelpText = commands.ConvertAll(c => string.Format("{0} - {1}", c.HelpText, c.LongHelp));
                help.AddRange(ourHelpText);
            }
        }

        help.Sort();

        return help;
    }

    /// <summary>
    ///     See if we can find the requested command in order to display longer help
    /// </summary>
    /// <param name="helpParts"></param>
    /// <returns></returns>
    private List<string> CollectHelp(List<string> helpParts)
    {
        var originalHelpRequest = string.Join(" ", helpParts.ToArray());
        var help = new List<string>();

        // Check modules first to see if we just need to display a list of those commands
        if (TryCollectModuleHelp(originalHelpRequest, help))
        {
            help.Insert(0, ItemHelpText);
            return help;
        }

        var dict = tree;
        while (helpParts.Count > 0)
        {
            var helpPart = helpParts[0];

            if (!dict.ContainsKey(helpPart))
                break;

            //m_log.Debug("Found {0}", helpParts[0]);

            if (dict[helpPart] is Dictionary<string, object>)
                dict = (Dictionary<string, object>)dict[helpPart];

            helpParts.RemoveAt(0);
        }

        // There was a command for the given help string
        if (dict.ContainsKey(string.Empty))
        {
            var commandInfo = (CommandInfo)dict[string.Empty];
            help.Add(commandInfo.HelpText);
            help.Add(commandInfo.LongHelp);

            var descriptiveHelp = commandInfo.DescriptiveHelp;

            // If we do have some descriptive help then insert a spacing line before for readability.
            if (descriptiveHelp != string.Empty)
                help.Add(string.Empty);

            help.Add(commandInfo.DescriptiveHelp);
        }
        else
        {
            help.Add(string.Format("No help is available for {0}", originalHelpRequest));
        }

        return help;
    }

    /// <summary>
    ///     Try to collect help for the given module if that module exists.
    /// </summary>
    /// <param name="moduleName"></param>
    /// <param name="helpText">
    ///     /param>
    ///     <returns>true if there was the module existed, false otherwise.</returns>
    private bool TryCollectModuleHelp(string moduleName, List<string> helpText)
    {
        lock (_modulesCommands)
        {
            foreach (var key in _modulesCommands.Keys)
                // Allow topic help requests to succeed whether they are upper or lowercase.
                if (moduleName.ToLower() == key.ToLower())
                {
                    var commands = _modulesCommands[key];
                    var ourHelpText = commands.ConvertAll(c => string.Format("{0} - {1}", c.HelpText, c.LongHelp));
                    ourHelpText.Sort();
                    helpText.AddRange(ourHelpText);

                    return true;
                }

            return false;
        }
    }

    private List<string> CollectModulesHelp(Dictionary<string, object> dict)
    {
        lock (_modulesCommands)
        {
            var helpText = new List<string>(_modulesCommands.Keys);
            helpText.Sort();
            return helpText;
        }
    }

    private CommandInfo ResolveCommand(string[] cmd, out string[] result)
    {
        result = cmd;
        var index = -1;

        var current = tree;

        foreach (var s in cmd)
        {
            index++;

            var found = new List<string>();

            foreach (var opt in current.Keys)
            {
                if (opt == s)
                {
                    found.Clear();
                    found.Add(opt);
                    break;
                }

                if (opt.StartsWith(s)) found.Add(opt);
            }

            if (found.Count == 1)
            {
                result[index] = found[0];
                current = (Dictionary<string, object>)current[found[0]];
            }
            else if (found.Count > 0)
            {
                return null;
            }
            else
            {
                break;
            }
        }

        if (current.ContainsKey(string.Empty))
            return (CommandInfo)current[string.Empty];

        return null;
    }

    private void ProcessTreeLevel(Dictionary<string, object> level, XmlElement xml, XmlDocument doc)
    {
        foreach (var kvp in level)
            if (kvp.Value is Dictionary<string, object>)
            {
                var next = doc.CreateElement("", "Level", "");
                next.SetAttribute("Name", kvp.Key);

                xml.AppendChild(next);

                ProcessTreeLevel((Dictionary<string, object>)kvp.Value, next, doc);
            }
            else
            {
                var c = (CommandInfo)kvp.Value;

                var cmd = doc.CreateElement("", "Command", "");

                XmlElement e;

                e = doc.CreateElement("", "Module", "");
                cmd.AppendChild(e);
                e.AppendChild(doc.CreateTextNode(c.Module));

                e = doc.CreateElement("", "Shared", "");
                cmd.AppendChild(e);
                e.AppendChild(doc.CreateTextNode(c.Shared.ToString()));

                e = doc.CreateElement("", "HelpText", "");
                cmd.AppendChild(e);
                e.AppendChild(doc.CreateTextNode(c.HelpText));

                e = doc.CreateElement("", "LongHelp", "");
                cmd.AppendChild(e);
                e.AppendChild(doc.CreateTextNode(c.LongHelp));

                e = doc.CreateElement("", "Description", "");
                cmd.AppendChild(e);
                e.AppendChild(doc.CreateTextNode(c.DescriptiveHelp));

                xml.AppendChild(cmd);
            }
    }

    private void ReadTreeLevel(Dictionary<string, object> level, XmlNode node, CommandDelegate fn)
    {
        Dictionary<string, object> next;
        string name;

        var nodeL = node.ChildNodes;
        XmlNodeList cmdL;
        CommandInfo c;

        foreach (XmlNode part in nodeL)
            switch (part.Name)
            {
                case "Level":
                    name = ((XmlElement)part).GetAttribute("Name");
                    next = new Dictionary<string, object>();
                    level[name] = next;
                    ReadTreeLevel(next, part, fn);
                    break;
                case "Command":
                    cmdL = part.ChildNodes;
                    c = new CommandInfo();
                    foreach (XmlNode cmdPart in cmdL)
                        switch (cmdPart.Name)
                        {
                            case "Module":
                                c.Module = cmdPart.InnerText;
                                break;
                            case "Shared":
                                c.Shared = Convert.ToBoolean(cmdPart.InnerText);
                                break;
                            case "HelpText":
                                c.HelpText = cmdPart.InnerText;
                                break;
                            case "LongHelp":
                                c.LongHelp = cmdPart.InnerText;
                                break;
                            case "Description":
                                c.DescriptiveHelp = cmdPart.InnerText;
                                break;
                        }

                    c.Fn = new List<CommandDelegate>();
                    c.Fn.Add(fn);
                    level[string.Empty] = c;
                    break;
            }
    }
//        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

/// <summary>
///     Encapsulates a command that can be invoked from the console
/// </summary>
private class CommandInfo
    {
        /// <value>
        ///     Full descriptive help for this command
        /// </value>
        public string DescriptiveHelp;

        /// <value>
        ///     The method to invoke for this command
        /// </value>
        public List<CommandDelegate> Fn;

        /// <value>
        ///     Very short BNF description
        /// </value>
        public string HelpText;

        /// <value>
        ///     Longer one line help text
        /// </value>
        public string LongHelp;

        /// <value>
        ///     The module from which this command comes
        /// </value>
        public string Module;

        /// <value>
        ///     Whether the module is shared
        /// </value>
        public bool Shared;
    }
}

public class Parser
{
    // If an unquoted portion ends with an element matching this regex
    // and the next element contains a space, then we have stripped
    // embedded quotes that should not have been stripped
    private static readonly Regex optionRegex = new("^--[a-zA-Z0-9-]+=$");

    public static string[] Parse(string text)
    {
        var result = new List<string>();

        int index;

        var unquoted = text.Split(new[] { '"' });

        for (index = 0; index < unquoted.Length; index++)
            if (index % 2 == 0)
            {
                var words = unquoted[index].Split();

                var option = false;
                foreach (var w in words)
                    if (w != string.Empty)
                    {
                        if (optionRegex.Match(w) == Match.Empty)
                            option = false;
                        else
                            option = true;
                        result.Add(w);
                    }

                // The last item matched the regex, put the quotes back
                if (option)
                    // If the line ended with it, don't do anything
                    if (index < unquoted.Length - 1)
                    {
                        // Get and remove the option name
                        var optionText = result[result.Count - 1];
                        result.RemoveAt(result.Count - 1);

                        // Add the quoted value back
                        optionText += "\"" + unquoted[index + 1] + "\"";

                        // Push the result into our return array
                        result.Add(optionText);

                        // Skip the already used value
                        index++;
                    }
            }
            else
            {
                result.Add(unquoted[index]);
            }

        return result.ToArray();
    }
}

/// <summary>
///     A console that processes commands internally
/// </summary>
public class CommandConsole : ConsoleBase, ICommandConsole
{
    public CommandConsole(string defaultPrompt) : base(defaultPrompt)
    {
        Commands = new Commands();

        Commands.AddCommand(
            "Help", false, "help", "help [<item>]",
            "Display help on a particular command or on a list of commands in a category", Help);
    }
    //        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

    public event OnOutputDelegate OnOutput;

    public ICommands Commands { get; }

    /// <summary>
    ///     Display a command prompt on the console and wait for user input
    /// </summary>
    public void Prompt()
    {
        var line = ReadLine(DefaultPrompt + "# ", true, true);

        if (line != string.Empty)
            Output("Invalid command");
    }

    public void RunCommand(string cmd)
    {
        var parts = Parser.Parse(cmd);
        Commands.Resolve(parts);
    }

    public override string ReadLine(string p, bool isCommand, bool e)
    {
        System.Console.Write("{0}", p);
        var cmdinput = System.Console.ReadLine();

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

    public virtual void ReadConfig(IConfigSource configSource)
    {
    }

    public virtual void SetCntrCHandler(OnCntrCCelegate handler)
    {
        if (OnCntrC == null)
        {
            OnCntrC += handler;
            System.Console.CancelKeyPress += CancelKeyPressed;
        }
    }

    public static event OnCntrCCelegate OnCntrC;

    private void Help(string module, string[] cmd)
    {
        var help = Commands.GetHelp(cmd);

        foreach (var s in help)
            Output(s);
    }

    protected void FireOnOutput(string text)
    {
        OnOutput?.Invoke(text);
    }

    protected static void CancelKeyPressed(object sender, ConsoleCancelEventArgs args)
    {
        if (OnCntrC != null && args.SpecialKey == ConsoleSpecialKey.ControlC)
        {
            OnCntrC?.Invoke();
            args.Cancel = false;
        }
    }

    protected static void LocalCancelKeyPressed()
    {
        OnCntrC?.Invoke();
    }
}