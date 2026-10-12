/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using Phlox.ScriptEngine;
using Xunit;
using static InWorldz.Phlox.Tests.InventoryGivesRig;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// TaskInventoryDictionary's lock gives up a thread's existing hold when the same thread asks again: a write request
/// from a thread holding the read lock logs "Recursive read lock requested" and releases the read lock first, and a
/// read request from a thread holding the write lock logs "Recursive write lock requested" and releases the write
/// lock. A script call therefore must not call a core inventory method (RemoveInventoryItem takes the write lock)
/// while it holds the read lock. llRemoveInventory and iwRemoveLinkInventory find the item under the read lock and
/// remove it after letting go; these tests fail if either removes it inside the scope, and if a nested
/// <see cref="InventoryLock.Read"/> takes the lock a second time. The log is process-wide, hence "phlox-state".
/// </summary>
[Collection("phlox-state")]
public class InventoryLockReentryTests
{
    private sealed class Capture : ILoggerFactory, ILoggerProvider
    {
        private readonly List<string> m_lines = new();
        public string[] Lines { get { lock (m_lines) return m_lines.ToArray(); } }
        public ILogger CreateLogger(string category) => new L(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        private sealed class L : ILogger
        {
            private readonly Capture m_c;
            public L(Capture c) => m_c = c;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            { lock (m_c.m_lines) m_c.m_lines.Add($"{logLevel} {formatter(state, exception)}"); }
        }
    }

    private const string Remover = @"
        default {
            state_entry() { llListen(9, """", NULL_KEY, """"); llSay(0, ""R entry""); }
            listen(integer c, string n, key k, string m) {
                if (m == ""rm"") { llRemoveInventory(""victim""); llSay(0, ""R removed""); }
                else if (m == ""rml"") { iwRemoveLinkInventory(LINK_THIS, ""victim2""); llSay(0, ""R removed2""); }
            }
        }";

    [Fact]
    public void RemovingAnItemFromAScriptDoesNotAskForTheInventoryLockWhileHoldingIt()
    {
        var log = new Capture();
        ILoggerFactory previous = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = log;
        try
        {
            using var h = new SchedulerHarness();
            AddNotecard(h, h.Prim, "victim");
            AddNotecard(h, h.Prim, "victim2");
            h.RezScriptInto(h.Prim, Remover);
            Assert.True(h.PumpUntil(() => h.Said.Contains("R entry")), string.Join(" | ", h.Said));

            h.Scene.SimChat("rm", ChatTypeEnum.Region, 9, h.Prim.AbsolutePosition, "tester", UUID.Random(), false);
            Assert.True(h.PumpUntil(() => h.Said.Contains("R removed")), string.Join(" | ", h.Said));
            h.Scene.SimChat("rml", ChatTypeEnum.Region, 9, h.Prim.AbsolutePosition, "tester", UUID.Random(), false);
            Assert.True(h.PumpUntil(() => h.Said.Contains("R removed2")), string.Join(" | ", h.Said));

            Assert.Null(h.Prim.Inventory.GetInventoryItem("victim"));
            Assert.Null(h.Prim.Inventory.GetInventoryItem("victim2"));
            Assert.DoesNotContain(log.Lines, l => l.Contains("Recursive"));
            Assert.DoesNotContain(log.Lines, l => l.StartsWith("Error"));
        }
        finally { LoggerProvider.LoggerFactory = previous; }
    }

    [Fact]
    public async Task AReadScopeInsideAReadScopeTakesNothingAndLetsGoOnlyAtTheOuterEnd()
    {
        var log = new Capture();
        ILoggerFactory previous = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = log;
        try
        {
            var inventory = new TaskInventoryDictionary();
            Task<bool> writer;
            using (InventoryLock.Read(inventory))
            {
                using (InventoryLock.Read(inventory)) { }
                // The inner scope ended; the outer hold is still there, so a writer on another thread must wait.
                writer = Task.Run(() =>
                {
                    inventory.LockItemsForWrite(true);
                    inventory.LockItemsForWrite(false);
                    return true;
                });
                Assert.NotSame(writer, await Task.WhenAny(writer, Task.Delay(300)));
            }
            Assert.NotSame(Task.Delay(0), await Task.WhenAny(writer, Task.Delay(TimeSpan.FromSeconds(10))));
            await writer;
            Assert.DoesNotContain(log.Lines, l => l.Contains("Recursive"));
        }
        finally { LoggerProvider.LoggerFactory = previous; }
    }
}
