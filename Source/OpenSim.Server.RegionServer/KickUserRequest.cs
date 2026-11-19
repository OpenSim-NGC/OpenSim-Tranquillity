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

using NDesk.Options;

namespace OpenSim.Server.RegionServer;

/// <summary>
/// What the console's <c>kick user &lt;first&gt; &lt;last&gt; [--force] [message]</c> asks for.
/// </summary>
public sealed record KickUserRequest(string FirstName, string LastName, bool Force, string Message)
{
    /// <summary>What the user is told when the operator gives no message.</summary>
    public const string DefaultMessage = "You have been logged out by an administrator.";

    /// <summary>
    /// Reads the command's words, the command name included. <c>--force</c> may come anywhere and is
    /// never part of the message. Null when the name is missing.
    /// </summary>
    public static KickUserRequest Parse(string[] cmdparams)
    {
        bool force = false;
        OptionSet options = new OptionSet().Add("f|force", v => force = v is not null);
        List<string> words = options.Parse(cmdparams);
        if (words.Count < 4)
            return null;

        string message = words.Count > 4 ? string.Join(" ", words.Skip(4)) : DefaultMessage;
        return new KickUserRequest(words[2], words[3], force, message);
    }
}
