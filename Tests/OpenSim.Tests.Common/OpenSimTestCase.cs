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

using OpenSim.Framework.Servers;

namespace OpenSim.Tests.Common;

/// <summary>
/// Base class for every xunit test class in the tree. xunit constructs a fresh instance per test; NUnit's
/// per-test [SetUp] is reproduced through <see cref="IAsyncLifetime.InitializeAsync"/>, which xunit calls after
/// the constructor (subclass constructor bodies included) and before the test method, exactly where NUnit ran
/// [SetUp]. Subclasses override <see cref="SetUp"/> and call base.SetUp(). Teardown stays on
/// <see cref="Dispose"/>: xunit 2 calls DisposeAsync and then Dispose, so DisposeAsync is a no-op.
///
/// History: the xunit migration (#197) turned the subclasses' [SetUp] methods into overrides of SetUp() and
/// nothing invoked them, so every test relying on fields assigned in SetUp ran against null
/// (Docs/feature/repo-audit/T1-TEST-FIXTURES.md).
/// </summary>
public class OpenSimTestCase : IDisposable, Xunit.IAsyncLifetime
{
    protected OpenSimTestCase()
    {
        //TestHelpers.InMethod();
        // Disable logging for each test so that one where logging is enabled doesn't cause all subsequent tests
        // to have logging on if it failed with an exception.
        TestHelpers.DisableLogging();

        // This is an unfortunate bit of clean up we have to do because MainServer manages things through static
        // variables and the VM is not restarted between tests.
        if (MainServer.Instance?.DefaultServer != null)
        {
            MainServer.Instance.RemoveHttpServer(MainServer.Instance.DefaultServer.Port);
            // MainServer.Instance = null;
        }
    }

    /// <summary>
    /// Per-test setup, run before every test method (the NUnit [SetUp] equivalent). Override and call base.SetUp().
    /// </summary>
    public virtual void SetUp()
    {
        // Override in subclasses for per-test setup
    }

    /// <summary>xunit's post-construction hook: runs <see cref="SetUp"/> before the test method.</summary>
    public virtual Task InitializeAsync()
    {
        SetUp();
        return Task.CompletedTask;
    }

    /// <summary>xunit calls this before <see cref="Dispose"/>; teardown lives in Dispose so nothing runs twice.</summary>
    public virtual Task DisposeAsync() => Task.CompletedTask;

    public virtual void Dispose()
    {
        // Do "global" teardown here; Called after every test method.
    }
}