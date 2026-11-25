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

using System.Reflection;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Groups;

/// <summary>
/// Sends a group notice to the members who are not in this process, through the message transfer module, a few at a
/// time. The transfer module works through one queue shared by every instant message the region sends, and each
/// notice message costs a presence lookup and, for an offline member, an offline message stored; a notice to a large
/// group put one job per member in that queue at once, so every other message waited behind all of them. Here at
/// most <see cref="InFlight"/> notice messages are with the transfer module at a time; the rest wait here, and
/// other messages are never behind more than those few.
/// </summary>
public sealed class GroupNoticeSender : IDisposable
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    public const int DefaultInFlight = 4;

    /// <summary>How long one message may take before the next is sent anyway.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(30);

    private readonly Channel<GridInstantMessage> m_queue =
        Channel.CreateUnbounded<GridInstantMessage>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly Func<IMessageTransferModule> m_transfer;
    private readonly TimeSpan m_wait;
    private readonly Task[] m_workers;

    public int InFlight { get; }

    /// <summary>Messages waiting to go to the transfer module.</summary>
    public int Waiting => m_queue.Reader.Count;

    public GroupNoticeSender(Func<IMessageTransferModule> transfer, int inFlight = DefaultInFlight, TimeSpan? wait = null)
    {
        m_transfer = transfer;
        InFlight = Math.Max(1, inFlight);
        m_wait = wait ?? DefaultWait;
        m_workers = Enumerable.Range(0, InFlight).Select(_ => Task.Run(Work)).ToArray();
    }

    /// <summary>Queue the messages; returns at once.</summary>
    public void Send(IEnumerable<GridInstantMessage> messages)
    {
        foreach (GridInstantMessage im in messages)
            m_queue.Writer.TryWrite(im);
    }

    private async Task Work()
    {
        await foreach (GridInstantMessage im in m_queue.Reader.ReadAllAsync())
        {
            IMessageTransferModule transfer = m_transfer();
            if (transfer is null)
                continue;

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                transfer.SendInstantMessage(im, _ => done.TrySetResult());
            }
            catch (Exception e)
            {
                m_log.LogWarning("[Groups]: a notice to {0} could not be sent: {1}", im.toAgentID, e.Message);
                continue;
            }

            if (await Task.WhenAny(done.Task, Task.Delay(m_wait)) != done.Task)
                m_log.LogDebug("[Groups]: a notice to {0} took longer than {1}s; sending the next", im.toAgentID, m_wait.TotalSeconds);
        }
    }

    /// <summary>Stop taking messages; those queued are still sent.</summary>
    public void Dispose() => m_queue.Writer.TryComplete();
}
