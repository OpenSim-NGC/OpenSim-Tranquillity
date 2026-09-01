/*
 * Copyright (c) 2025, Tranquillity - OpenSimulator NGC
 * Utopia Skye LLC
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;

namespace OpenSim.Server.Base.Hosting;

public sealed class MainServerAccessor : IMainServerAccessor
{
    public IHttpServer DefaultServer => MainServer.Instance.DefaultServer;

    public IHttpServer GetHttpServer(uint port)
    {
        return MainServer.Instance.GetHttpServer(port);
    }

    public void Stop()
    {
        MainServer.Instance.Stop();
    }
}
