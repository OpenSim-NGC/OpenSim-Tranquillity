/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using log4net.Config;

namespace OpenSim.Server.Base.Hosting;

public sealed class Log4NetBootstrapper : ILog4NetBootstrapper
{
    public string ResolveConfigPath(string configuredPath, string defaultPath)
    {
        return string.IsNullOrWhiteSpace(configuredPath) ? defaultPath : configuredPath;
    }

    public string Configure(string configuredPath, string defaultPath)
    {
        string configPath = ResolveConfigPath(configuredPath, defaultPath);
        XmlConfigurator.Configure(new FileInfo(configPath));
        return configPath;
    }
}
