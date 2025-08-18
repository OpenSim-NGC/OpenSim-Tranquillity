using System.Xml;

namespace OpenSim.Framework.Console;

public class Commands : ICommands
{
    public const string GeneralHelpText
        = "To enter an argument that contains spaces, surround the argument with double quotes.\n" +
          "For example, show object name \"My long object name\"\n";

    public const string ItemHelpText
        = "For more information, type 'help all' to get a list of all commands, " +
           "or type help <item>' where <item> is one of the following:";

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