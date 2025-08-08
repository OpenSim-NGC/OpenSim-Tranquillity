/*
 * Copyright (c) Contributors, http://whitecore-sim.org/, http://aurora-sim.org,
 *   http://opensimulator.org/
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

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OpenSim.Framework.Console;

public static class Parser
{
    public static string[] Parse(string text)
    {
        var result = new List<string>();

        int index;
        var startingIndex = -1;
        var unquoted = text.Split(new[] { '"' });

        for (index = 0; index < unquoted.Length; index++)
            if (unquoted[index].StartsWith("/", StringComparison.Ordinal) || startingIndex >= 0)
            {
                startingIndex = index;
                var qstr = unquoted[index].Trim();
                if (qstr != "")
                    result.Add(qstr);
            }
            else
            {
                startingIndex = 0;
                var words = unquoted[index].Split(new[] { ' ' });
                result.AddRange(words.Where(w => w != string.Empty));
            }

        return result.ToArray();
    }
}

/// <summary>
///     A console that processes commands internally
/// </summary>
public class CommandConsole : ICommandConsole
{
    private readonly IConfiguration _config;
    private readonly ILogger _logger;

    public CommandConsole(IConfiguration config, ILogger logger)
    {
        _config = config;
        _logger = logger;
    }

    public bool IsPrompting { get; set; }

    public int LastSetPromptOption { get; set; }
    public List<string> PromptOptions { get; set; } = new();

    public virtual bool Initialize()
    {
        var consoleConfig = _config.GetSection("Console");
        if (consoleConfig.Exists() is false ||
            consoleConfig.GetValue("Console", string.Empty) != Name)
            return false;

        MainConsole.Instance = this;

        Commands.AddCommand(
            "help",
            "help",
            "Get a general command list",
            Help, false, true);

        return true;
    }

    public void RunCommand(string cmd)
    {
        var parts = Parser.Parse(cmd);
        Commands.Resolve(parts);
        Output("", Threshold);
    }

    public string Prompt(string prompt)
    {
        return Prompt(prompt, "");
    }

    public string Prompt(string prompt, string defaultResponse)
    {
        return Prompt(prompt, defaultResponse, new List<string>());
    }

    public string Prompt(string prompt, string defaultResponse, List<char> excludedCharacters)
    {
        return Prompt(prompt, defaultResponse, new List<string>(), excludedCharacters);
    }

    public string Prompt(string prompt, string defaultresponse, List<string> options)
    {
        return Prompt(prompt, defaultresponse, options, new List<char>());
    }

    // Displays a prompt and waits for the user to enter a string, then returns that string
    // (Done with no echo and suitable for passwords)
    public string PasswordPrompt(string p)
    {
        IsPrompting = true;
        var line = ReadLine(p + ": ", false, false);
        IsPrompting = false;

        return line;
    }

    public virtual void LockOutput()
    {
    }

    public virtual void UnlockOutput()
    {
    }

    public virtual bool CompareLogLevels(string a, string b)
    {
        var aa = (Level)Enum.Parse(typeof(Level), a, true);
        var bb = (Level)Enum.Parse(typeof(Level), b, true);
        return aa <= bb;
    }

    /// <summary>
    ///     The default prompt text.
    /// </summary>
    public virtual string DefaultPrompt { get; set; }

    public virtual string Name => "CommandConsole";

    public ICommands Commands { get; set; } = new Commands();

    public List<IScene> ConsoleScenes { get; set; } = new();

    public IScene ConsoleScene { get; set; }

    public bool HasProcessedCurrentCommand { get; set; }

    /// <summary>
    ///     Starts the prompt for the console. This will never stop until the region is closed.
    /// </summary>
    public void ReadConsole()
    {
        while (true) Prompt();
    }

    public Level Threshold { get; set; }

    public void Help(IScene scene, string[] cmd)
    {
        var help = Commands.GetHelp(cmd);

        foreach (var s in help)
            OutputNoTime(s, Level.Off);
    }

    /// <summary>
    ///     Display a command prompt on the console and wait for user input
    /// </summary>
    public void Prompt()
    {
        // Set this culture for the thread 
        // to en-US to avoid number parsing issues
        Culture.SetCurrentCulture();
        var line = ReadLine(DefaultPrompt + "# ", true, true);

        if (line != string.Empty && line.Replace(" ", "") != string.Empty) //If there is a space, its fine
            MainConsole.Instance.Info("[CONSOLE] Invalid command");
    }

    public virtual string ReadLine(string p, bool isCommand, bool e)
    {
        var oldDefaultPrompt = DefaultPrompt;
        DefaultPrompt = p;
        System.Console.Write("{0}", p);
        var cmdinput = System.Console.ReadLine();

        if (isCommand)
        {
            if (cmdinput != null)
            {
                var cmd = Commands.Resolve(Parser.Parse(cmdinput));

                if (cmd.Length != 0)
                {
                    int i;

                    for (i = 0; i < cmd.Length; i++)
                        if (cmd[i].Contains(" "))
                            cmd[i] = "\"" + cmd[i] + "\"";
                }
            }
            else
            {
                Environment.Exit(0);
            }

            DefaultPrompt = oldDefaultPrompt;
            return string.Empty;
        }

        return cmdinput;
    }

    // Displays a command prompt and returns a default value, user may only enter 1 of 2 options
    public string Prompt(string prompt, string defaultresponse, List<string> options, List<char> excludedCharacters)
    {
        IsPrompting = true;
        PromptOptions = new List<string>(options);

        var itisdone = false;
        var optstr = options.Aggregate(string.Empty, (current, s) => current + " " + s);
        var temp = InternalPrompt(prompt, defaultresponse, options);

        while (!itisdone && options.Count > 0)
            if (options.Contains(temp))
            {
                itisdone = true;
            }
            else
            {
                System.Console.WriteLine("Valid options are" + optstr);
                temp = InternalPrompt(prompt, defaultresponse, options);
            }

        itisdone = false;
        while (!itisdone && excludedCharacters.Count > 0)
            foreach (var c in excludedCharacters.Where(c => temp.Contains(c.ToString())))
            {
                System.Console.WriteLine("The character \"" + c + "\" is not permitted.");
                itisdone = false;
            }

        IsPrompting = false;
        PromptOptions.Clear();
        return temp;
    }

    private string InternalPrompt(string prompt, string defaultresponse, List<string> options)
    {
        var ret = ReadLine(string.Format("{0}{2} [{1}]: ",
            prompt,
            defaultresponse,
            options.Count == 0
                ? ""
                : ", Options are [" + string.Join(", ", options.ToArray()) + "]"
        ), false, true);
        if (ret == string.Empty)
            ret = defaultresponse;

        // let's be a little smarter here if we can
        if (options.Count > 0)
            foreach (var option in options)
                if (option.StartsWith(ret, StringComparison.Ordinal))
                    ret = option;
        return ret;
    }
    public virtual void Output(string text)
    {
        System.Console.WriteLine(text);
    }

    public virtual void Output(string text, Level level)
    {
        if (Threshold <= level)
        {
            MainConsole.TriggerLog(level.ToString(), text);
            text = string.Format("{0} ; {1}", Culture.LocaleLogStamp(), text);
            System.Console.WriteLine(text);
        }
    }

    public virtual void OutputNoTime(string text, Level level)
    {
        if (Threshold <= level)
        {
            MainConsole.TriggerLog(level.ToString(), text);
            System.Console.WriteLine(text);
        }
    }

    #region ILog Members

    public bool IsDebugEnabled => Threshold <= Level.Debug;

    public bool IsErrorEnabled => Threshold <= Level.Error;

    public bool IsFatalEnabled => Threshold <= Level.Fatal;

    public bool IsInfoEnabled => Threshold <= Level.Info;

    public bool IsWarnEnabled => Threshold <= Level.Warn;

    public bool IsTraceEnabled => Threshold <= Level.Trace;

    public void Debug(object message)
    {
        Output(message.ToString(), Level.Debug);
    }

    public void DebugFormat(string format, params object[] args)
    {
        Output(string.Format(format, args), Level.Debug);
    }

    public void Error(object message)
    {
        Output(message.ToString(), Level.Error);
    }

    public void ErrorFormat(string format, params object[] args)
    {
        Output(string.Format(format, args), Level.Error);
    }

    public void Fatal(object message)
    {
        Output(message.ToString(), Level.Fatal);
    }

    public void FatalFormat(string format, params object[] args)
    {
        Output(string.Format(format, args), Level.Fatal);
    }

    public void Format(Level level, string format, params object[] args)
    {
        Output(string.Format(format, args), level);
    }

    public void FormatNoTime(Level level, string format, params object[] args)
    {
        OutputNoTime(string.Format(format, args), level);
    }

    public void Info(object message)
    {
        Output(message.ToString(), Level.Info);
    }

    public void CleanInfo(object message)
    {
        OutputNoTime(message.ToString(), Level.Info);
    }

    public void CleanInfoFormat(string format, params object[] args)
    {
        OutputNoTime(string.Format(format, args), Level.Error);
    }

    public void Ticker()
    {
        System.Console.Write(".");
    }

    public void Ticker(string message, bool newline)
    {
        System.Console.Write(" " + message + " ");
        if (newline)
            System.Console.WriteLine("");
    }

    public void InfoFormat(string format, params object[] args)
    {
        Output(string.Format(format, args), Level.Info);
    }

    public void Log(Level level, object message)
    {
        Output(message.ToString(), level);
    }

    public void Trace(object message)
    {
        Output(message.ToString(), Level.Trace);
    }

    public void TraceFormat(string format, params object[] args)
    {
        Output(string.Format(format, args), Level.Trace);
    }

    public void Warn(object message)
    {
        Output(message.ToString(), Level.Warn);
    }

    public void WarnFormat(string format, params object[] args)
    {
        Output(string.Format(format, args), Level.Warn);
    }

    #endregion
}