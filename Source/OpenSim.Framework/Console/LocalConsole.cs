/*
 * Copyright (c) Contributors, http://whitecore-sim.org/, http://aurora-sim.org,
 *  http://opensimulator.org/, OpenSim NGC
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

using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OpenSim.Framework.Console;

/// <summary>
///     A console that uses cursor control and color
/// </summary>
public class LocalConsole : CommandConsole
{
    private static readonly object _cmdlock = new();

    private static readonly ConsoleColor[] Colors =
    {
        // the dark colors don't seem to be visible on some black background terminals like putty :(
        //ConsoleColor.DarkBlue,
        //ConsoleColor.DarkGreen,
        //ConsoleColor.Gray, 
        //ConsoleColor.DarkGray,
        ConsoleColor.DarkCyan,
        ConsoleColor.DarkMagenta,
        ConsoleColor.DarkYellow,
        ConsoleColor.Green,
        ConsoleColor.Blue,
        ConsoleColor.Magenta,
        ConsoleColor.Red,
        ConsoleColor.Yellow,
        ConsoleColor.Cyan
    };

    private static readonly object _lock = new();
    protected readonly IConfiguration _config;
    protected readonly ILogger<LocalConsole> _logger;

    private readonly List<string> history = new();

    private StringBuilder cmdline = new();
    private int cp;
    private bool echo = true;
    private int h = 1;
    protected string prompt = "# ";
    private int y = -1;

    public LocalConsole(IConfiguration config, ILogger<LocalConsole> logger) :
        base(config, logger)
    {
        _config = config;
        _logger = logger;
    }

    public override string Name => "LocalConsole";

    private static ConsoleColor DeriveColor(string input)
    {
        // it is important to do Abs, hash values can be negative
        return Colors[Math.Abs(input.ToUpper().Length) % Colors.Length];
    }

    private void AddToHistory(string text)
    {
        while (history.Count >= 100)
            history.RemoveAt(0);

        history.Add(text);
    }

    /// <summary>
    ///     Set the cursor row.
    /// </summary>
    /// <param name="top">
    ///     Row to set.  If this is below 0, then the row is set to 0.  If it is equal to the buffer height or greater
    ///     then it is set to one less than the height.
    /// </param>
    /// <returns>
    ///     The new cursor row.
    /// </returns>
    private int SetCursorTop(int top)
    {
        // From at least mono 2.4.2.3, window resizing can give mono an invalid row and column values.  If we try
        // to set a cursor row position with a currently invalid column, mono will throw an exception.
        // Therefore, we need to make sure that the column position is valid first.
        var left = System.Console.CursorLeft;

        if (left < 0)
        {
            System.Console.CursorLeft = 0;
        }
        else
        {
            var bw = System.Console.BufferWidth;

            // On Mono 2.4.2.3 (and possibly above), the buffer value is sometimes erroneously zero (Mantis 4657)
            if (bw > 0 && left >= bw)
                System.Console.CursorLeft = bw - 1;
        }

        if (top < 0)
        {
            top = 0;
        }
        else
        {
            var bh = System.Console.BufferHeight;

            // On Mono 2.4.2.3 (and possibly above), the buffer value is sometimes erroneously zero (Mantis 4657)
            if (bh > 0 && top >= bh)
                top = bh - 1;
        }

        System.Console.CursorTop = top;

        return top;
    }

    /// <summary>
    ///     Set the cursor column.
    /// </summary>
    /// <param name="left">
    ///     Column to set.  If this is below 0, then the column is set to 0.  If it is equal to the buffer width or greater
    ///     then it is set to one less than the width.
    /// </param>
    /// <returns>
    ///     The new cursor column.
    /// </returns>
    private int SetCursorLeft(int left)
    {
        // From at least mono 2.4.2.3, window resizing can give mono an invalid row and column values.  If we try
        // to set a cursor column position with a currently invalid row, mono will throw an exception.
        // Therefore, we need to make sure that the row position is valid first.
        var top = System.Console.CursorTop;

        if (top < 0)
        {
            System.Console.CursorTop = 0;
        }
        else
        {
            var bh = System.Console.BufferHeight;
            // On Mono 2.4.2.3 (and possibly above), the buffer value is sometimes erroneously zero (Mantis 4657)
            if (bh > 0 && top >= bh)
                System.Console.CursorTop = bh - 1;
        }

        if (left < 0)
        {
            left = 0;
        }
        else
        {
            var bw = System.Console.BufferWidth;

            // On Mono 2.4.2.3 (and possibly above), the buffer value is sometimes erroneously zero (Mantis 4657)
            if (bw > 0 && left >= bw)
                left = bw - 1;
        }

        System.Console.CursorLeft = left;

        return left;
    }

    private void Show()
    {
        lock (_cmdlock)
        {
            if (y == -1 || System.Console.BufferWidth == 0)
                return;

            var xc = prompt.Length + cp;
            var new_x = xc % System.Console.BufferWidth;
            var new_y = y + xc / System.Console.BufferWidth;
            var end_y = y + (cmdline.Length + prompt.Length) / System.Console.BufferWidth;
            if (end_y / System.Console.BufferWidth >= h)
                h++;
            if (end_y >= System.Console.BufferHeight) // wrap
            {
                y--;
                new_y--;
                SetCursorLeft(0);
                SetCursorTop(System.Console.BufferHeight - 1);
                System.Console.WriteLine(" ");
            }

            y = SetCursorTop(y);
            SetCursorLeft(0);

            if (echo)
                System.Console.Write("{0}{1}", prompt, cmdline);
            else
                System.Console.Write("{0}", prompt);

            SetCursorTop(new_y);
            SetCursorLeft(new_x);
        }
    }

    public override void LockOutput()
    {
        Monitor.Enter(_lock);
        try
        {
            if (y != -1)
            {
                y = SetCursorTop(y);
                System.Console.CursorLeft = 0;

                var count = cmdline.Length + prompt.Length;

                while (count-- > 0)
                    System.Console.Write(" ");

                y = SetCursorTop(y);
                SetCursorLeft(0);
            }
        }
        catch (Exception)
        {
        }
    }

    public override void UnlockOutput()
    {
        if (y != -1)
        {
            y = System.Console.CursorTop;
            Show();
        }

        Monitor.Exit(_lock);
    }

    private void WriteColorText(ConsoleColor color, string sender)
    {
        try
        {
            lock (this)
            {
                try
                {
                    System.Console.ForegroundColor = color;
                    System.Console.Write(sender);
                    System.Console.ResetColor();
                }
                catch (ArgumentNullException)
                {
                    // Some older systems don't support colored text.
                    System.Console.WriteLine(sender);
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void WriteLocalText(string text, Level level)
    {
        var logtext = "";
        if (text != "")
        {
            var CurrentLine = 0;
            var Lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            // This exists so that we don't have issues with multiline stuff, since something is messed up with the Regex
            foreach (var line in Lines)
            {
                var split = line.Split(new[] { "[", "]" }, StringSplitOptions.None);
                var currentPos = 0;
                var boxNum = 0;
                foreach (var s in split)
                {
                    if (line[currentPos] == '[')
                    {
                        if (level >= Level.Fatal)
                            WriteColorText(ConsoleColor.White, "[");
                        else if (level >= Level.Error)
                            WriteColorText(ConsoleColor.Red, "[");
                        else if (level >= Level.Warn)
                            WriteColorText(ConsoleColor.Yellow, "[");
                        else
                            WriteColorText(ConsoleColor.Gray, "[");
                        boxNum++;
                        currentPos++;
                    }
                    else if (line[currentPos] == ']')
                    {
                        if (level == Level.Error)
                            WriteColorText(ConsoleColor.Red, "]");
                        else if (level == Level.Warn)
                            WriteColorText(ConsoleColor.Yellow, "]");
                        else
                            WriteColorText(ConsoleColor.Gray, "]");
                        boxNum--;
                        currentPos++;
                    }

                    if (boxNum == 0)
                    {
                        if (level == Level.Error)
                            WriteColorText(ConsoleColor.Red, s);
                        else if (level == Level.Warn)
                            WriteColorText(ConsoleColor.Yellow, s);
                        else
                            WriteColorText(ConsoleColor.Gray, s);
                    }
                    else // We're in a box
                    {
                        WriteColorText(DeriveColor(s), s);
                    }

                    currentPos += s.Length; //Include the extra 1 for the [ or ]
                }

                CurrentLine++;
                if (Lines.Length - CurrentLine != 0)
                    System.Console.WriteLine();

                logtext += line;
            }
        }

        System.Console.WriteLine();
    }

    public override void Output(string text, Level level)
    {
        if (Threshold <= level)
        {
            MainConsole.TriggerLog(level.ToString(), text);
            var ts = Culture.LocaleLogStamp() + " - ";
            var fullText = string.Format("{0} {1}", ts, text);
            MainConsole.TriggerLog(level.ToString(), fullText);

            lock (_cmdlock)
            {
                if (y == -1)
                {
                    WriteColorText(ConsoleColor.DarkCyan, ts);
                    WriteLocalText(text, level);

                    return;
                }

                y = SetCursorTop(y);
                SetCursorLeft(0);

                var count = cmdline.Length + prompt.Length;

                while (count-- > 0)
                    System.Console.Write(" ");

                y = SetCursorTop(y);
                SetCursorLeft(0);

                WriteColorText(ConsoleColor.DarkCyan, ts);
                WriteLocalText(text, level);

                y = System.Console.CursorTop;

                Show();
            }
        }
    }

    private bool ContextHelp()
    {
        var words = Parser.Parse(cmdline.ToString());

        var trailingSpace = cmdline.ToString().EndsWith(" ", StringComparison.Ordinal);

        // Allow ? through while typing a URI
        //
        if (words.Length > 0 && words[words.Length - 1].StartsWith("http", StringComparison.Ordinal) && !trailingSpace)
            return false;

        var opts = Commands.FindNextOption(words);

        if (opts.Length == 0)
            OutputNoTime("\n  No options.", Threshold);
        else if (opts[0].StartsWith("Command help:", StringComparison.Ordinal))
            OutputNoTime("\n  " + opts[0], Threshold);
        else
            OutputNoTime(string.Format("\n  Options: {0}", string.Join("\n           ", opts)), Threshold);

        return true;
    }

    public override string ReadLine(string p, bool isCommand, bool e)
    {
        lock (_cmdlock)
        {
            h = 1;
            cp = 0;
        }

        prompt = p;
        echo = e;
        var historyLine = history.Count;
        var allSelected = false;

        SetCursorLeft(0); // Needed for mono
        System.Console.Write(" "); // Needed for mono

        lock (_cmdlock)
        {
            y = System.Console.CursorTop;
            cmdline.Remove(0, cmdline.Length);
        }

        while (true)
        {
            Show();

            var key = System.Console.ReadKey(true);
            var c = key.KeyChar;
            var changed = false;

            if (!char.IsControl(c))
            {
                if (cp >= 318)
                    continue;

                if (c == '?' && isCommand)
                    if (ContextHelp())
                        continue;

                cmdline.Insert(cp, c);
                cp++;
            }
            else
            {
                switch (key.Key)
                {
                    case ConsoleKey.Backspace:
                        if (cp == 0)
                            break;
                        var toReplace = " ";
                        if (allSelected)
                        {
                            for (var i = 0; i < cmdline.Length; i++) toReplace += " ";
                            cmdline.Remove(0, cmdline.Length);
                            cp = 0;
                            allSelected = false;
                        }
                        else
                        {
                            if (cmdline.Length >= cp)
                                cmdline.Remove(cp - 1, 1);
                            cp--;
                        }

                        SetCursorLeft(0);
                        y = SetCursorTop(y);

                        if (echo) // This space makes the last line part disappear
                            System.Console.Write("{0}{1}", prompt, cmdline + toReplace);
                        else
                            System.Console.Write("{0}", prompt);

                        break;
                    case ConsoleKey.A:
                        if ((key.Modifiers | ConsoleModifiers.Control) == ConsoleModifiers.Control)
                            allSelected = true;
                        break;
                    case ConsoleKey.Delete:
                        if (cp == cmdline.Length || cp < 0)
                            break;
                        var stringToReplace = " ";
                        if (allSelected)
                        {
                            for (var i = 0; i < cmdline.Length; i++) stringToReplace += " ";
                            cmdline.Remove(0, cmdline.Length);
                            cp = 0;
                            allSelected = false; // All done
                        }
                        else
                        {
                            cmdline.Remove(cp, 1);
                            cp--;
                        }

                        SetCursorLeft(0);
                        y = SetCursorTop(y);

                        if (echo) // This space makes the last line part disappear
                            System.Console.Write("{0}{1}", prompt, cmdline + stringToReplace);
                        else
                            System.Console.Write("{0}", prompt);

                        break;
                    case ConsoleKey.End:
                        cp = cmdline.Length;
                        allSelected = false;
                        break;
                    case ConsoleKey.Home:
                        cp = 0;
                        allSelected = false;
                        break;
                    case ConsoleKey.UpArrow:
                        if (historyLine < 1)
                            break;
                        allSelected = false;
                        historyLine--;
                        LockOutput();
                        cmdline.Remove(0, cmdline.Length);
                        cmdline.Append(history[historyLine]);
                        cp = cmdline.Length;
                        UnlockOutput();
                        break;
                    case ConsoleKey.DownArrow:
                        if (historyLine >= history.Count)
                            break;
                        allSelected = false;
                        historyLine++;
                        LockOutput();
                        if (historyLine == history.Count)
                        {
                            cmdline.Remove(0, cmdline.Length);
                        }
                        else
                        {
                            cmdline.Remove(0, cmdline.Length);
                            cmdline.Append(history[historyLine]);
                        }

                        cp = cmdline.Length;
                        UnlockOutput();
                        break;
                    case ConsoleKey.LeftArrow:
                        if (cp > 0)
                        {
                            changed = true;
                            cp--;
                        }

                        if (IsPrompting && PromptOptions.Count > 0)
                        {
                            var last = LastSetPromptOption;
                            if (changed)
                                cp++;
                            if (LastSetPromptOption > 0)
                                LastSetPromptOption--;
                            cmdline = new StringBuilder(PromptOptions[LastSetPromptOption]);
                            var pr = PromptOptions[LastSetPromptOption];
                            if (last - LastSetPromptOption != 0)
                            {
                                var charDiff = PromptOptions[last].Length -
                                               PromptOptions[LastSetPromptOption].Length;
                                for (var i = 0; i < charDiff; i++)
                                    pr += " ";
                            }

                            LockOutput();
                            System.Console.CursorLeft = 0;
                            System.Console.Write("{0}{1}", prompt, pr);
                            UnlockOutput();
                        }

                        allSelected = false;
                        break;
                    case ConsoleKey.RightArrow:
                        if (cp < cmdline.Length)
                        {
                            changed = true;
                            cp++;
                        }

                        if (IsPrompting && PromptOptions.Count > 0)
                        {
                            var last = LastSetPromptOption;
                            if (LastSetPromptOption < PromptOptions.Count - 1)
                                LastSetPromptOption++;
                            if (changed)
                                cp--;
                            cmdline = new StringBuilder(PromptOptions[LastSetPromptOption]);
                            var pr = PromptOptions[LastSetPromptOption];
                            if (last - LastSetPromptOption != 0)
                            {
                                var charDiff = PromptOptions[last].Length -
                                               PromptOptions[LastSetPromptOption].Length;
                                for (var i = 0; i < charDiff; i++)
                                    pr += " ";
                            }

                            LockOutput();
                            System.Console.CursorLeft = 0;
                            System.Console.Write("{0}{1}", prompt, pr);
                            UnlockOutput();
                        }

                        allSelected = false;
                        break;
                    case ConsoleKey.Tab:
                        ContextHelp();
                        allSelected = false;
                        break;
                    case ConsoleKey.Enter:
                        allSelected = false;
                        SetCursorLeft(0);
                        y = SetCursorTop(y);

                        if (echo)
                            System.Console.WriteLine("{0}{1}", prompt, cmdline);
                        else
                            System.Console.WriteLine("{0}", prompt);

                        lock (_cmdlock)
                        {
                            y = -1;
                        }

                        var commandLine = cmdline.ToString();

                        if (isCommand)
                        {
                            if (cmdline.ToString() == "clear console")
                            {
                                history.Clear();
                                System.Console.Clear();
                                return string.Empty;
                            }

                            var cmd = Commands.Resolve(Parser.Parse(commandLine));

                            if (cmd.Length != 0)
                            {
                                int i;

                                for (i = 0; i < cmd.Length; i++)
                                    if (cmd[i].Contains(" "))
                                        cmd[i] = "\"" + cmd[i] + "\"";
                            }

                            AddToHistory(commandLine);
                            return string.Empty;
                        }

                        // If we're not echoing to screen (e.g. a password) then we probably don't want it in history
                        if (echo && commandLine != "")
                            AddToHistory(commandLine);

                        return cmdline.ToString();
                    default:
                        allSelected = false;
                        break;
                }
            }
        }
    }
}