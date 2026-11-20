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

using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using OpenSim.Framework;
using OpenSim.Framework.Console;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// OpenSim.ConsoleClient builds its commands from the help tree the server sends at StartSession. The server leaves
/// help and quit out of that tree, and the client's own console registers help but not quit, so reading the tree
/// threw KeyNotFoundException on "quit" and every login failed before a command could be sent. The client also
/// reads null when its input closes, which the command parser threw on.
/// </summary>
public class ConsoleClientHelpTreeTests
{
    private sealed class QuietConsole : CommandConsole
    {
        public readonly List<string> Said = new();

        public QuietConsole() : base("Client> ") { }

        public override void Output(string format) => Said.Add(format);

        public override void Output(string format, params object[] components) => Said.Add(string.Format(format, components));
    }

    private static readonly CommandDelegate Nothing = (module, cmd) => { };

    /// <summary>The help tree a server sends: its own commands, without help and quit.</summary>
    private static XmlElement ServerHelpTree()
    {
        Commands server = new();
        server.AddCommand("Help", false, "help", "help [<item>]", "Display help", Nothing);
        server.AddCommand("General", false, "quit", "quit", "Quit the application", Nothing);
        server.AddCommand("General", false, "show version", "show version", "Show server version", Nothing);

        XmlDocument doc = new();
        XmlElement tree = server.GetXml(doc);
        doc.AppendChild(tree);
        return tree;
    }

    [Xunit.Fact]
    public void TheServerTreeLeavesHelpAndQuitOut()
    {
        XmlElement tree = ServerHelpTree();

        Xunit.Assert.Contains("version", tree.OuterXml);
        Xunit.Assert.DoesNotContain("Quit the application", tree.OuterXml);
        Xunit.Assert.DoesNotContain("Display help", tree.OuterXml);
    }

    [Xunit.Fact]
    public void AClientWithoutQuitReadsTheTreeAndSendsServerCommands()
    {
        QuietConsole client = new();
        List<string> sent = new();

        client.Commands.FromXml(ServerHelpTree(), (module, cmd) => sent.Add(string.Join(" ", cmd)));

        client.Commands.Resolve(new[] { "show", "version" });
        Xunit.Assert.Equal(new[] { "show version" }, sent);

        // The client's own help is still there, and quit was not invented.
        Xunit.Assert.True(client.Commands.HasCommand("help"));
        Xunit.Assert.False(client.Commands.HasCommand("quit"));
    }

    [Xunit.Fact]
    public void AConsoleThatHasQuitKeepsItAcrossTheTree()
    {
        QuietConsole client = new();
        int quits = 0;
        client.Commands.AddCommand("General", false, "quit", "quit", "Quit", (module, cmd) => quits++);

        client.Commands.FromXml(ServerHelpTree(), Nothing);
        client.Commands.Resolve(new[] { "quit" });

        Xunit.Assert.Equal(1, quits);
    }

    [Xunit.Fact]
    public void AServerWithoutQuitStillSendsItsTree()
    {
        Commands server = new();
        server.AddCommand("General", false, "show version", "show version", "Show server version", Nothing);

        XmlElement tree = server.GetXml(new XmlDocument());

        Xunit.Assert.Contains("version", tree.OuterXml);
    }

    [Xunit.Fact]
    public void ClosedInputIsNoCommand()
    {
        Xunit.Assert.Empty(Parser.Parse(null));

        QuietConsole console = new();
        TextReader input = Console.In;
        TextWriter output = Console.Out;
        try
        {
            Console.SetIn(new StringReader(string.Empty));
            Console.SetOut(new StringWriter());

            console.Prompt();
        }
        finally
        {
            Console.SetIn(input);
            Console.SetOut(output);
        }

        Xunit.Assert.DoesNotContain("Invalid command", console.Said);
    }
}
