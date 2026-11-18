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
using System.Text;
using Xunit;
using OpenSim.Tests.Common;

namespace OpenSim.Framework.Servers.Tests
{
    public class VersionInfoTests : OpenSimTestCase
    {
        // GetVersionString pads to VERSIONINFO_VERSION_LENGTH as a minimum width (PadRight), so short
        // versions line up; it never cuts. A build version carries the commit (Nerdbank.GitVersioning,
        // e.g. "0.9.3.9441+1a2b3c4d5e"), so the real version string is longer than 48 and stays whole.
        [Fact]
        public void TestVersionLength()
        {
            Xunit.Assert.True(VersionInfo.Version.Length >= VersionInfo.VERSIONINFO_VERSION_LENGTH,
                $"\"{VersionInfo.Version}\" is shorter than {VersionInfo.VERSIONINFO_VERSION_LENGTH}");
            Xunit.Assert.StartsWith("OpenSim-NGC Tranquillity ", VersionInfo.Version);
            Xunit.Assert.Contains(VersionInfo.VersionNumber, VersionInfo.Version);
        }

        [Fact]
        public void AShortVersionIsPaddedToTheMinimumWidth()
        {
            string version = VersionInfo.GetVersionString("1.0", "Release");

            Xunit.Assert.Equal(VersionInfo.VERSIONINFO_VERSION_LENGTH, version.Length);
            Xunit.Assert.Equal("OpenSim-NGC Tranquillity Release 1.0", version.TrimEnd());
        }

        [Fact]
        public void ALongVersionIsKeptWhole()
        {
            string number = "0.9.3.9441+" + new string('a', 40);

            string version = VersionInfo.GetVersionString(number, "Release");

            Xunit.Assert.Equal("OpenSim-NGC Tranquillity Release " + number, version);
        }
    }
}
