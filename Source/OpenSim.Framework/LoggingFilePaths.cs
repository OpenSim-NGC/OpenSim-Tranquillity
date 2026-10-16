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

namespace OpenSim.Framework;

/// <summary>
/// Holds the file paths used by the configured logging pipeline.
/// </summary>
public static class LoggingFilePaths
{
    private static string _logFilePath = "./OpenSim.log";
    private static string _statsLogFilePath = "./OpenSimStats.log";
    private static bool _rollingFilesEnabled;
    private static bool _statsLogFileEnabled;

    public static string CurrentLogFilePath
    {
        get => _rollingFilesEnabled ? GetCurrentRollingFilePath(_logFilePath) : _logFilePath;
    }

    public static string CurrentStatsLogFilePath
    {
        get => _statsLogFileEnabled ? GetCurrentRollingFilePath(_statsLogFilePath) : _statsLogFilePath;
    }

    public static void Configure(string logFilePath, string statsLogFilePath, bool statsLogFileEnabled)
    {
        _logFilePath = logFilePath;
        _statsLogFilePath = statsLogFilePath;
        _rollingFilesEnabled = true;
        _statsLogFileEnabled = statsLogFileEnabled;
    }

    private static string GetCurrentRollingFilePath(string path)
    {
        string extension = Path.GetExtension(path);
        string dateSuffix = DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);

        return extension.Length == 0
            ? $"{path}{dateSuffix}"
            : $"{path[..^extension.Length]}{dateSuffix}{extension}";
    }
}
