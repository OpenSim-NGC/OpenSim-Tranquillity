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
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.Api.Plugins;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ScriptEngine.Shared.Tests;

/// <summary>
/// The dataserver's request lock and AsyncCommandManager's static lock are taken in one order by the
/// command handler thread (static lock, then ExpireRequests takes the request lock) and must never be
/// taken in the other by a request: RegisterRequest used to hand its action to the work pool inside the
/// request lock, and with FireAndForgetMethod None or RegressionTest the pool runs it on that thread, so an
/// action that reached AsyncCommandManager (DataserverPlugin's getter takes the static lock) deadlocked
/// with a command handler pass. LSL_ApiNotecardTests hung that way now and then.
///
/// The test forces that interleaving: one thread holds the static lock and waits to expire requests while
/// the action, running inline, asks for DataserverPlugin. With the lock order kept, both finish.
/// The assembly runs its tests one at a time; the test restores Util.FireAndForgetMethod.
/// </summary>
public class DataserverLockOrderTests
{
    [Fact]
    public void AnInlineActionThatReachesTheCommandManagerDoesNotDeadlockWithACommandPass()
    {
        FireAndForgetMethod before = Util.FireAndForgetMethod;
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;   // the work pool runs the action inline
        try
        {
            MockScriptEngine engine = new();
            SceneHelpers.SetupSceneModules(new SceneHelpers().SetupScene(), engine);
            AsyncCommandManager commands = new(engine);
            Dataserver dataserver = commands.DataserverPlugin;
            object staticLock = typeof(AsyncCommandManager)
                .GetField("staticLock", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

            using ManualResetEventSlim actionRunning = new(false);
            using ManualResetEventSlim commandPassHoldsTheStaticLock = new(false);

            // The command handler's pass: the static lock, then the request lock.
            Task commandPass = Task.Run(() =>
            {
                actionRunning.Wait();
                lock (staticLock)
                {
                    commandPassHoldsTheStaticLock.Set();
                    dataserver.ExpireRequests();
                }
            });

            // A request whose action reaches the command manager, as llGetNotecardLine's does.
            Task request = Task.Run(() =>
                dataserver.RegisterRequest(1, UUID.Random(), eventId =>
                {
                    actionRunning.Set();
                    commandPassHoldsTheStaticLock.Wait(TimeSpan.FromSeconds(5));
                    Assert.NotNull(commands.DataserverPlugin);
                }));

            Assert.True(Task.WaitAll(new[] { commandPass, request }, TimeSpan.FromSeconds(15)),
                "the request and the command pass deadlocked");
        }
        finally
        {
            Util.FireAndForgetMethod = before;
        }
    }
}
