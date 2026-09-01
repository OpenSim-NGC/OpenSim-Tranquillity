/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSim.Server.Base.Hosting;
using Xunit;

namespace OpenSim.Server.Base.Tests.Hosting;

public sealed class PidFileManagerTests
{
    [Fact]
    public void Create_WritesPidFileAndTracksActivePath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"opensim-pid-{Guid.NewGuid():N}.pid");

        try
        {
            var sut = new PidFileManager(new NullLogger<PidFileManager>());

            sut.Create(path);

            Assert.Equal(path, sut.ActivePath);
            Assert.True(File.Exists(path));

            string content = File.ReadAllText(path, Encoding.ASCII).Trim();
            Assert.False(string.IsNullOrWhiteSpace(content));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Remove_DeletesPidFileAndClearsActivePath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"opensim-pid-{Guid.NewGuid():N}.pid");

        var sut = new PidFileManager(new NullLogger<PidFileManager>());
        sut.Create(path);

        sut.Remove();

        Assert.Equal(string.Empty, sut.ActivePath);
        Assert.False(File.Exists(path));
    }
}
