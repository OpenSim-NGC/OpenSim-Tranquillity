/*
 * Copyright (c) 2026 Legion Builds
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

using System.Diagnostics;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Groups;
using OpenSim.Region.Framework.Interfaces;
using Xunit;

namespace OpenSim.Region.CoreModules.Avatar.Groups.Tests;

/// <summary>
/// A notice to a large group goes to the transfer module a few messages at a time, so the region's other instant
/// messages never wait behind one job per member; every member still gets it.
/// </summary>
public class GroupNoticeSenderTests
{
    /// <summary>A transfer module that takes a while over each message and records how many it holds at once.</summary>
    private sealed class SlowTransfer : IMessageTransferModule
    {
        private readonly TimeSpan m_each;
        private readonly bool m_answer;
        private int m_now;
        public int MostAtOnce;
        public readonly System.Collections.Concurrent.ConcurrentBag<Guid> Delivered = new();

        public SlowTransfer(TimeSpan each, bool answer = true)
        {
            m_each = each;
            m_answer = answer;
        }

        public event UndeliveredMessage OnUndeliveredMessage { add { } remove { } }

        public void SendInstantMessage(GridInstantMessage im, MessageResultNotification result, bool ToRootOnly = false)
        {
            int now = Interlocked.Increment(ref m_now);
            int seen;
            while ((seen = MostAtOnce) < now && Interlocked.CompareExchange(ref MostAtOnce, now, seen) != seen) { }
            _ = Task.Run(async () =>
            {
                await Task.Delay(m_each);
                Delivered.Add(im.toAgentID);
                Interlocked.Decrement(ref m_now);
                if (m_answer)
                    result(true);
            });
        }

        public void HandleUndeliverableMessage(GridInstantMessage im, MessageResultNotification result) => result(false);
    }

    private static List<GridInstantMessage> Notices(int n) => Enumerable.Range(0, n)
        .Select(_ => new GridInstantMessage { toAgentID = UUID.Random().Guid, dialog = (byte)InstantMessageDialog.GroupNotice })
        .ToList();

    private static bool WaitFor(Func<bool> done, TimeSpan within)
    {
        var t = Stopwatch.StartNew();
        while (t.Elapsed < within)
        {
            if (done())
                return true;
            Thread.Sleep(20);
        }
        return done();
    }

    [Fact]
    public void ANoticeGoesOutAFewMessagesAtATimeAndReachesEveryone()
    {
        var transfer = new SlowTransfer(TimeSpan.FromMilliseconds(50));
        using var sender = new GroupNoticeSender(() => transfer, inFlight: 4);
        List<GridInstantMessage> notices = Notices(60);

        var t = Stopwatch.StartNew();
        sender.Send(notices);
        Assert.True(t.ElapsedMilliseconds < 200, $"Send took {t.ElapsedMilliseconds} ms: it must not wait for the delivery");

        Assert.True(WaitFor(() => transfer.Delivered.Count == notices.Count, TimeSpan.FromSeconds(20)),
            $"{transfer.Delivered.Count} of {notices.Count} delivered");
        Assert.Equal(notices.Select(n => n.toAgentID).OrderBy(g => g), transfer.Delivered.OrderBy(g => g));
        Assert.True(transfer.MostAtOnce <= 4, $"{transfer.MostAtOnce} messages with the transfer module at once");
        Assert.True(transfer.MostAtOnce > 1, "the messages went one at a time");
    }

    [Fact]
    public void AMessageThatIsNeverAnsweredDoesNotStopTheRest()
    {
        // a transfer module that never calls back: the sender moves on after its wait
        var transfer = new SlowTransfer(TimeSpan.FromMilliseconds(10), answer: false);
        using var sender = new GroupNoticeSender(() => transfer, inFlight: 2, wait: TimeSpan.FromMilliseconds(100));
        List<GridInstantMessage> notices = Notices(10);

        sender.Send(notices);
        Assert.True(WaitFor(() => transfer.Delivered.Count == notices.Count, TimeSpan.FromSeconds(10)),
            $"{transfer.Delivered.Count} of {notices.Count} delivered");
    }

    [Fact]
    public void NoTransferModuleMeansNothingIsSentAndNothingStalls()
    {
        using var sender = new GroupNoticeSender(() => null, inFlight: 2);
        sender.Send(Notices(5));
        Assert.True(WaitFor(() => sender.Waiting == 0, TimeSpan.FromSeconds(5)));
    }
}
