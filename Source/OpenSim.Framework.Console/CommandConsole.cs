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
using Microsoft.Extensions.Configuration;

namespace OpenSim.Framework.Console;

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
public class CommandConsole : ICommandConsole
{    
    public delegate void OnOutputDelegate(string text);
    public delegate void OnCntrCCelegate();
    public event OnOutputDelegate OnOutput;
    public event OnCntrCCelegate OnCntrC;

    private readonly IConfiguration _configuration;
    private readonly IConsole _console;
    private readonly ICommands _commands;

    public CommandConsole(IConfiguration configuration, ICommands commands, IConsole console)
    {
        _configuration = configuration;
        _commands = commands;
        _console = console;

        _commands.AddCommand(
            "Help", false, "help", "help [<item>]",
            "Display help on a particular command or on a list of commands in a category", Help);
    }

    event Framework.OnOutputDelegate ICommandConsole.OnOutput
    {
        add
        {
            throw new NotImplementedException();
        }

        remove
        {
            throw new NotImplementedException();
        }
    }

    public ICommands Commands { get => _commands; }

    /// <summary>
    ///     Display a command prompt on the console and wait for user input
    /// </summary>
    public void Prompt()
    {
        var line = ReadLine($"{DefaultPrompt}#", true, true);

        if (line != string.Empty)
            Output("Invalid command");
    }

    public void RunCommand(string cmd)
    {
        var parts = Parser.Parse(cmd);
        Commands.Resolve(parts);
    }

    public string ReadLine(string p, bool isCommand, bool e)
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

    public void SetCntrCHandler(OnCntrCCelegate handler)
    {
        if (OnCntrC == null)
        {
            OnCntrC += handler;
            System.Console.CancelKeyPress += CancelKeyPressed;
        }
    }

    private void Help(string module, string[] cmd)
    {
        var help = Commands.GetHelp(cmd);

        foreach (var s in help)
            Output(s);
    }

    public void FireOnOutput(string text)
    {
        OnOutput?.Invoke(text);
    }

    public void CancelKeyPressed(object sender, ConsoleCancelEventArgs args)
    {
        if (OnCntrC != null && args.SpecialKey == ConsoleSpecialKey.ControlC)
        {
            OnCntrC?.Invoke();
            args.Cancel = false;
        }
    }

    public IScene ConsoleScene
    {
        get => _console.ConsoleScene;
        set => _console.ConsoleScene = value;
    }

    public string DefaultPrompt
    {
        get => _console.DefaultPrompt;
        set => _console.DefaultPrompt = value;
    }

    public void Output(string format)
    {
        _console.Output(format);
    }

    public void Output(string format, params object[] components)
    {
        _console.Output(format, components);
    }

    public string Prompt(string p)
    {
        return _console.Prompt(p);
    }

    public string Prompt(string p, string def)
    {
        return _console.Prompt(p, def);
    }

    public string Prompt(string p, List<char> excludedCharacters)
    {
        return _console.Prompt(p, excludedCharacters);
    }

    public string Prompt(string p, string def, List<char> excludedCharacters, bool echo = true)
    {
        return _console.Prompt(p, def, excludedCharacters, echo);
    }

    public string Prompt(string prompt, string defaultresponse, List<string> options)
    {
        return _console.Prompt(prompt, defaultresponse, options);
    }

    public void SetCntrCHandler(Framework.OnCntrCCelegate handler)
    {
        throw new NotImplementedException();
    }

    public void ReadConfig(IConfiguration configSource)
    {
        throw new NotImplementedException();
    }
}