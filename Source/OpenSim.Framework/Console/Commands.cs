namespace OpenSim.Framework.Console;

using OpenSim.Framework.Utilities;

public class Commands : ICommands
{
    public static bool _ConsoleIsCaseSensitive = true;

    /// <value>
    ///     Commands organized by keyword in a tree
    /// </value>
    private readonly CommandSet tree = new();

    /// <summary>
    ///     Get help for the given help string
    /// </summary>
    /// <param name="cmd">Parsed parts of the help string.  If empty then general help is returned.</param>
    /// <returns></returns>
    public List<string> GetHelp(string[] cmd)
    {
        return tree.GetHelp(new List<string>(0));
    }

    /// <summary>
    ///     Add a command to those which can be invoked from the console.
    /// </summary>
    /// <param name="command">The string that will make the command execute</param>
    /// <param name="commandHelp">The message that will show the user how to use the command</param>
    /// <param name="infomessage">Any information about how the command works or what it does</param>
    /// <param name="fn"></param>
    /// <param name="requiresAScene">Whether this command requires a scene to be fired</param>
    /// <param name="fireOnceForAllScenes">Whether this command will only be executed once if there is no current scene</param>
    public void AddCommand(string command, string commandHelp, string infomessage, CommandDelegate fn,
        bool requiresAScene, bool fireOnceForAllScenes)
    {
        var info = new CommandInfo
        {
            command = command,
            commandHelp = commandHelp,
            info = infomessage,
            fireOnceForAllScenes = fireOnceForAllScenes,
            requiresAScene = requiresAScene,
            fn = new List<CommandDelegate> { fn }
        };
        tree.AddCommand(info);
    }

    public bool ContainsCommand(string command)
    {
        return tree.FindCommands(new[] { command }).Length > 0;
    }

    public string[] FindNextOption(string[] cmd)
    {
        return tree.FindCommands(cmd);
    }

    public string[] Resolve(string[] cmd)
    {
        return tree.ExecuteCommand(cmd);
    }

    #region Nested type: CommandInfo

    /// <summary>
    ///     Encapsulates a command that can be invoked from the console
    /// </summary>
    private class CommandInfo
    {
        /// <summary>
        ///     The command for this commandinfo
        /// </summary>
        public string command;

        /// <summary>
        ///     The help info for how to use this command
        /// </summary>
        public string commandHelp;

        /// <summary>
        ///     Whether this command will only be executed once if there is no current scene
        /// </summary>
        public bool fireOnceForAllScenes;

        /// <value>
        ///     The method to invoke for this command
        /// </value>
        public List<CommandDelegate> fn;

        /// <summary>
        ///     Any info about this command
        /// </summary>
        public string info;

        /// <summary>
        ///     Whether this command requires a scene to be fired
        /// </summary>
        public bool requiresAScene;
    }

    #endregion

    #region Nested type: CommandSet

    private class CommandSet
    {
        private readonly Dictionary<string, CommandInfo> commands = new();
        private readonly Dictionary<string, CommandSet> commandsets = new();
        private bool m_allowSubSets = true;
        private string ourPath = "";
        public string Path = "";

        public void Initialize(string path, bool allowSubSets)
        {
            m_allowSubSets = allowSubSets;
            ourPath = path;
            var paths = path.Split(' ');
            if (paths.Length != 0) Path = paths[paths.Length - 1];
        }

        public void AddCommand(CommandInfo info)
        {
            if (!_ConsoleIsCaseSensitive) //Force to all lowercase
                info.command = info.command.ToLower();

            //If our path is "", we can't replace, otherwise we just get ""
            var innerPath = info.command;
            if (ourPath != "") innerPath = info.command.Replace(ourPath, "");
            if (innerPath.StartsWith(" ", StringComparison.Ordinal)) innerPath = innerPath.Remove(0, 1);
            var commandPath = innerPath.Split(new[] { " " }, StringSplitOptions.RemoveEmptyEntries);
            if (commandPath.Length == 1 || !m_allowSubSets)
            {
                // Only one command after our path, its ours

                // Add commands together if there is more than one event hooked to one command
                if (!commands.ContainsKey(info.command)) commands[info.command] = info;
            }
            else
            {
                // Its down the tree somewhere
                CommandSet downTheTree;
                if (!commandsets.TryGetValue(commandPath[0], out downTheTree))
                {
                    //Need to add it to the tree then
                    downTheTree = new CommandSet();
                    downTheTree.Initialize((ourPath == "" ? "" : ourPath + " ") + commandPath[0], false);
                    commandsets.Add(commandPath[0], downTheTree);
                }

                downTheTree.AddCommand(info);
            }
        }

        public string[] ExecuteCommand(string[] commandPath)
        {
            if (commandPath.Length != 0)
            {
                var commandPathList = new List<string>(commandPath);
                var commandOptions = new List<string>();
                int i;
                for (i = commandPath.Length - 1; i >= 0; --i)
                    if (commandPath[i].Length > 1 && commandPath[i].Substring(0, 2) == "--")
                    {
                        commandOptions.Add(commandPath[i]);
                        commandPathList.RemoveAt(i);
                    }
                    else
                    {
                        break;
                    }

                commandOptions.Reverse();
                commandPath = commandPathList.ToArray();
                // if (commandOptions.Count > 0)
                //    MainConsole.Instance.Info("Options: " + string.Join(", ", commandOptions.ToArray()));
                List<string> cmdList;
                if (commandPath.Length == 1 || !m_allowSubSets)
                {
                    for (i = 1; i <= commandPath.Length; i++)
                    {
                        var comm = new string [i];
                        Array.Copy(commandPath, comm, i);
                        var com = string.Join(" ", comm);
                        // Only one command after our path, its ours
                        if (commands.ContainsKey(com))
                        {
                            MainConsole.Instance.HasProcessedCurrentCommand = false;

                            foreach (var fn in commands[com].fn.Where(fn => fn != null))
                            {
                                cmdList = new List<string>(commandPath);
                                cmdList.AddRange(commandOptions);
                                foreach (var scene in GetScenes(commands[com]))
                                    fn(scene, cmdList.ToArray());
                            }

                            return new string [0];
                        }

                        if (commandPath[0] == "help")
                        {
                            var help = GetHelp(commandOptions);

                            foreach (var s in help) MainConsole.Instance.FormatNoTime(Level.Off, s);
                            return new string [0];
                        }

                        // not 'help'
                        foreach (var cmd in commands)
                        {
                            var cmdSplit = cmd.Key.Split(' ');
                            if (cmdSplit.Length == commandPath.Length)
                            {
                                var any = false;
                                for (var k = 0; k < commandPath.Length; k++)
                                    if (!cmdSplit[k].StartsWith(commandPath[k], StringComparison.Ordinal))
                                    {
                                        any = true;
                                        break;
                                    }

                                var same = !any;
                                if (same)
                                {
                                    foreach (var fn in cmd.Value.fn)
                                    {
                                        cmdList = new List<string>(commandPath);
                                        cmdList.AddRange(commandOptions);
                                        if (fn != null)
                                            foreach (var scene in GetScenes(cmd.Value))
                                                fn(scene, cmdList.ToArray());
                                    }

                                    return new string [0];
                                }
                            }
                        }
                    }

                    // unable to determine multi word command
                    MainConsole.Instance.Warn(" Sorry.. missed that...");
                }
                else if (commandPath.Length > 0)
                {
                    var cmdToExecute = commandPath[0];
                    if (cmdToExecute == "help") cmdToExecute = commandPath[1];
                    if (!_ConsoleIsCaseSensitive) cmdToExecute = cmdToExecute.ToLower();
                    // Its down the tree somewhere
                    CommandSet downTheTree;
                    if (commandsets.TryGetValue(cmdToExecute, out downTheTree))
                    {
                        cmdList = new List<string>(commandPath);
                        cmdList.AddRange(commandOptions);
                        return downTheTree.ExecuteCommand(cmdList.ToArray());
                    }

                    // See if this is part of a word, and if it is part of a word, execute it
                    foreach (
                        var cmd in
                        commandsets.Where(cmd => cmd.Key.StartsWith(commandPath[0], StringComparison.Ordinal)))
                    {
                        cmdList = new List<string>(commandPath);
                        cmdList.AddRange(commandOptions);
                        return cmd.Value.ExecuteCommand(cmdList.ToArray());
                    }

                    if (commands.ContainsKey(cmdToExecute))
                    {
                        foreach (var fn in commands[cmdToExecute].fn.Where(fn => fn != null))
                        {
                            cmdList = new List<string>(commandPath);
                            cmdList.AddRange(commandOptions);
                            foreach (var scene in GetScenes(commands[cmdToExecute]))
                                fn(scene, cmdList.ToArray());
                        }

                        return new string [0];
                    }

                    MainConsole.Instance.Warn(" Sorry.. missed that...");
                }
            }

            return new string [0];
        }

        private List<IScene> GetScenes(CommandInfo cmd)
        {
            if (cmd.requiresAScene)
            {
                if (MainConsole.Instance.ConsoleScene == null)
                {
                    if (cmd.fireOnceForAllScenes)
                    {
                        if (MainConsole.Instance.ConsoleScenes.Count == 1)
                            return new List<IScene> { MainConsole.Instance.ConsoleScenes[0] };

                        MainConsole.Instance.Warn("[Warning] This command requires a selected region");
                        return new List<IScene>();
                    }

                    return MainConsole.Instance.ConsoleScenes;
                }

                return new List<IScene> { MainConsole.Instance.ConsoleScene };
            }

            if (MainConsole.Instance.ConsoleScene == null)
                return cmd.fireOnceForAllScenes ? new List<IScene> { null } : MainConsole.Instance.ConsoleScenes;

            return new List<IScene> { MainConsole.Instance.ConsoleScene };
        }

        public string[] FindCommands(string[] command)
        {
            var values = new List<string>();
            if (command.Length != 0)
            {
                var innerPath = string.Join(" ", command);
                if (!_ConsoleIsCaseSensitive)
                    innerPath = innerPath.ToLower();

                if (ourPath != "")
                    innerPath = innerPath.Replace(ourPath, "");

                if (innerPath.StartsWith(" ", StringComparison.Ordinal))
                    innerPath = innerPath.Remove(0, 1);

                var commandPath = innerPath.Split(new[] { " " }, StringSplitOptions.RemoveEmptyEntries);
                if (commandPath.Length == 1 || !m_allowSubSets)
                {
                    var fullcommand = string.Join(" ", command, 0, 2 > command.Length ? command.Length : 2);
                    values.AddRange(from cmd in commands
                        where cmd.Key.StartsWith(fullcommand, StringComparison.Ordinal)
                        select cmd.Value.commandHelp);
                    if (commandPath.Length != 0)
                    {
                        var cmdToExecute = commandPath[0];
                        if (cmdToExecute == "help")
                            if (commandPath.Length > 1)
                                cmdToExecute = commandPath[1];
                        if (!_ConsoleIsCaseSensitive)
                            cmdToExecute = cmdToExecute.ToLower();

                        CommandSet downTheTree;
                        if (commandsets.TryGetValue(cmdToExecute, out downTheTree))
                            values.AddRange(downTheTree.FindCommands(commandPath));
                        else
                            //See if this is part of a word, and if it is part of a word, execute it
                            foreach (
                                var cmd in
                                commandsets.Where(cmd => cmd.Key.StartsWith(cmdToExecute, StringComparison.Ordinal)))
                                values.AddRange(cmd.Value.FindCommands(commandPath));
                    }
                }
                else if (commandPath.Length != 0)
                {
                    var cmdToExecute = commandPath[0];
                    if (cmdToExecute == "help") cmdToExecute = commandPath[1];
                    if (!_ConsoleIsCaseSensitive) cmdToExecute = cmdToExecute.ToLower();
                    // Its down the tree somewhere
                    CommandSet downTheTree;
                    if (commandsets.TryGetValue(cmdToExecute, out downTheTree))
                        return downTheTree.FindCommands(commandPath);

                    // See if this is part of a word, and if it is part of a word, execute it
                    foreach (
                        var cmd in
                        commandsets.Where(cmd => cmd.Key.StartsWith(cmdToExecute, StringComparison.Ordinal)))
                        return cmd.Value.FindCommands(commandPath);
                }
            }

            return values.ToArray();
        }

        public List<string> GetHelp(List<string> options)
        {
            MainConsole.Instance.Debug("HTML mode: " + options.Contains("--html"));
            var help = new List<string>();

            if (commandsets.Count != 0)
            {
                help.Add("");
                help.Add("------- Help Sets (type the name and help to get more info about that set) -------");
                help.Add("");
            }

            var paths = new List<string>();

            paths.AddRange(commandsets.Values.Select(set => string.Format("-- Help Set: {0}", set.Path)));

            help.AddRange(StringUtils.AlphanumericSort(paths));
            if (help.Count != 0)
            {
                help.Add("");
                help.Add("------- Help options -------");
            }

            paths.Clear();

            paths.AddRange(
                //    commands.Values.Select(
                //    command =>
                //    string.Format("-- {0}  [{1}]:   {2}", command.command, command.commandHelp, command.info)));
                commands.Values.Select(command =>
                    string.Format("-- {0}:\n      {1}", command.commandHelp,
                        command.info.Replace("\n", "\n        "))));

            help.Add("");
            help.AddRange(StringUtils.AlphanumericSort(paths));
            help.Add("");
            return help;
        }
    }

    #endregion
}