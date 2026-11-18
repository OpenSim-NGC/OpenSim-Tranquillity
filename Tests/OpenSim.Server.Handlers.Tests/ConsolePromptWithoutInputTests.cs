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
using OpenSim.Framework.Console;

namespace OpenSim.Server.Handlers.Tests;

/// <summary>
/// A question put to a console nobody can answer (a server started with --background, as a service, or with its
/// input redirected): the default is used and said on the console, and a question with no default is an error
/// naming it. The console is never asked to read a line, which on the local console reads keys and throws on
/// redirected input.
/// </summary>
public class ConsolePromptWithoutInputTests
{
    private sealed class NoInputConsole : CommandConsole
    {
        public readonly List<string> Said = new();

        public NoInputConsole() : base("test") { }

        public override bool CanPrompt => false;

        public override string ReadLine(string p, bool isCommand, bool e)
            => throw new InvalidOperationException("ReadLine was called on a console with no input");

        public override void Output(string format) => Said.Add(format);

        public override void Output(string format, params object[] components) => Said.Add(string.Format(format, components));
    }

    [Xunit.Fact]
    public void AQuestionWithADefaultTakesItAndSaysSo()
    {
        NoInputConsole console = new();

        Xunit.Assert.Equal("Test", console.Prompt("Estate owner first name", "Test"));
        Xunit.Assert.Equal("4f0c8d2e-7a51-4b8e-9c3d-2e6f1a0b7c55",
            console.Prompt("User ID", "4f0c8d2e-7a51-4b8e-9c3d-2e6f1a0b7c55", new List<char> { ' ' }, true));

        Xunit.Assert.Contains(console.Said, s => s.Contains("\"Estate owner first name\"") && s.Contains("\"Test\""));
        Xunit.Assert.Contains(console.Said, s => s.Contains("\"User ID\""));
    }

    [Xunit.Fact]
    public void AQuestionWithNoDefaultIsAnErrorNamingIt()
    {
        NoInputConsole console = new();

        var bare = Xunit.Assert.Throws<InvalidOperationException>(() => console.Prompt("Password"));
        var withNullDefault = Xunit.Assert.Throws<InvalidOperationException>(() => console.Prompt("Email", null, null, true));
        var withExcluded = Xunit.Assert.Throws<InvalidOperationException>(() => console.Prompt("Region name", new List<char> { ' ' }));

        Xunit.Assert.Contains("\"Password\"", bare.Message);
        Xunit.Assert.Contains("\"Email\"", withNullDefault.Message);
        Xunit.Assert.Contains("\"Region name\"", withExcluded.Message);
        Xunit.Assert.Contains("configuration", bare.Message);
    }

    [Xunit.Fact]
    public void TheRemoteConsoleKeepsAskingOverItsConnection()
    {
        Xunit.Assert.True(new RemoteConsole("test").CanPrompt);
    }
}
