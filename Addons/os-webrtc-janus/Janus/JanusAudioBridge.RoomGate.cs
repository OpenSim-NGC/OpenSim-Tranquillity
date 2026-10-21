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
// Adapted 2026 by Legion Builds

using System.Collections.Concurrent;
using System.Reflection;

using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Framework;

namespace osWebRtcVoice;

// Room creation: every select asks the mixer, with one create at a time per room number.
public partial class JanusAudioBridge
{
    // Run a create attempt; if it comes back null (inconclusive), run it once more.
    // Pure orchestration (no Janus dependency) so the retry policy is unit-testable.
    public static async Task<JanusRoom> CreateWithRecheck(Func<Task<JanusRoom>> pAttempt)
    {
        JanusRoom ret = await pAttempt().ConfigureAwait(false);
        if (ret is null)
        {
            // Inconclusive first attempt -> the room may already exist (a cross-process
            // create won the race). Re-attempt; a now-existing room returns 486 -> reuse.
            ret = await pAttempt().ConfigureAwait(false);
        }
        return ret;
    }

    // A single create attempt. Returns a JanusRoom on "created" or 486 (already exists
    // -> reuse); null on any other error / unexpected return / exception (the caller
    // may re-check for a cross-process create).
    private async Task<JanusRoom> TryCreateRoomOnce(int pRoomId, bool pSpatial, string pRoomDesc, bool pVisAuthority)
    {
        JanusRoom ret = null;
        try
        {
            var createReq = new AudioBridgeCreateRoomReq(pRoomId, pSpatial, pRoomDesc, pVisAuthority);
            if (JoinCapabilityEnabled)
                createReq.SetSimCreated();
            JanusMessageResp resp = await SendPluginMsg(createReq);
            AudioBridgeResp abResp = new AudioBridgeResp(resp);

            m_log.LogDebug("{0} CreateRoom. ReturnCode: {1}", LogHeader, abResp.AudioBridgeReturnCode);
            switch (abResp.AudioBridgeReturnCode)
            {
                case "created":
                    ret = new JanusRoom(this, pRoomId);
                    RoomCreated?.Invoke(pRoomId);
                    break;
                case "event":
                    if (abResp.AudioBridgeErrorCode == 486)
                    {
                        // Every JSEP join is preceded by a create, so "already exists" is the normal
                        // answer on almost every provision - success, and DEBUG, not a warning.
                        m_log.LogDebug("{0} CreateRoom. Room {1} already exists. Reusing! {2}", LogHeader, pRoomId, abResp.ToString());
                        // if room already exists, just use it
                        ret = new JanusRoom(this, pRoomId);
                    }
                    else
                    {
                        m_log.LogError("{LogHeader} CreateRoom. XX Room creation inconclusive: {AudioBridgeResponse}", LogHeader, abResp.ToString());
                    }
                    break;
                default:
                    m_log.LogError("{LogHeader} CreateRoom. YY Room creation inconclusive: {AudioBridgeResponse}", LogHeader, abResp.ToString());
                    break;
            }
        }
        catch (Exception e)
        {
            m_log.LogError("{0} CreateRoom. Exception {1}", LogHeader, e);
        }
        return ret;
    }

    // Room EXISTENCE is grid-global and Janus is the only source of truth for it.
    //
    // There is NO process-side "room exists" hint: the mixer's 60 s empty-room grace destroys rooms
    // without telling the sim, so any cached answer goes stale, and a stale one makes the JSEP join
    // draw error 485, which cannot be re-sent on the same handle. Every select therefore asks the mixer
    // to create, "already exists" (486) counting as success, BEFORE the JSEP join is sent. What remains
    // is a per-room gate that serializes creates of one room number within this
    // process, so concurrent provisions send their creates one at a time (one "created", the rest 486) instead of all
    // at once; the cross-process race is the 486 itself.
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> _roomCreateLocks = new();

    // Without eviction the gate table would grow by one entry per room number ever selected (every parcel,
    // every agent-to-agent pair) for the life of the process. Entries idle for RoomCreateLockIdle are swept at most once
    // a minute from SelectRoomCoalesced. Evicting is safe: a swept gate is simply re-created on the next select;
    // the one race - a caller that fetched a gate just before it was swept - can at worst send two creates at
    // once, which the 486 absorbs.
    public static readonly TimeSpan RoomCreateLockIdle = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<int, long> _roomLastUseMs = new();

    private static long _lastRoomLockSweepMs = Environment.TickCount64;

    // Fold a 32-bit hash to a POSITIVE Janus room number. Math.Abs(int.MinValue) throws
    // OverflowException (there is no positive int.MinValue), and because the hash inputs are stable
    // per agent+parcel, one unlucky combination would crash provisioning on that parcel forever.
    // Redirect ONLY int.MinValue to a valid positive room (int.MaxValue); every other value
    // goes through Math.Abs, so no other (region,parcel)->room mapping is affected
    // (int.MinValue cannot yield a room through Math.Abs -- it throws). Extracted so the guard is unit-testable.
    public static int FoldHashToRoom(int hashCode)
        => hashCode == int.MinValue ? int.MaxValue : Math.Abs(hashCode);

    /// <summary>Make sure the room for a parcel exists. This is SelectRoom under a name that says what the caller
    /// wants: a select asks the mixer every time, so a room that already exists is reused and one that does not is
    /// created with the flags a viewer's provision would use.</summary>
    public Task<JanusRoom> EnsureRoom(string pRegionId, string pChannelType, bool pSpatial, int pParcelLocalID, string pChannelID)
        => SelectRoom(pRegionId, pChannelType, pSpatial, pParcelLocalID, pChannelID);

    /// <summary>Send the create for <paramref name="pRoomNumber"/> under its per-room gate: creates of one room number
    /// from this process go one at a time, so concurrent provisions produce one "created" and 486s. Always creates -
    /// there is nothing to skip on. Func-based and static so it is unit-testable without Janus.</summary>
    public static async Task<JanusRoom> SelectRoomCoalesced(int pRoomNumber, Func<Task<JanusRoom>> pCreate)
    {
        long now = Environment.TickCount64;
        MaybeSweepRoomCreateLocks(now);
        _roomLastUseMs[pRoomNumber] = now;
        SemaphoreSlim gate = _roomCreateLocks.GetOrAdd(pRoomNumber, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await pCreate().ConfigureAwait(false);
        }
        finally
        {
            _roomLastUseMs[pRoomNumber] = Environment.TickCount64;
            gate.Release();
        }
    }

    // At most once a minute, drop the gate and the last-use time of every room number idle
    // for RoomCreateLockIdle (see the field comment for why eviction is safe).
    private static void MaybeSweepRoomCreateLocks(long pNowMs)
    {
        long last = Interlocked.Read(ref _lastRoomLockSweepMs);
        if (pNowMs - last < 60_000 || Interlocked.CompareExchange(ref _lastRoomLockSweepMs, pNowMs, last) != last)
            return;
        SweepRoomCreateLocks(pNowMs, RoomCreateLockIdle);
    }

    /// Evict every room number whose last select is older than pIdle and whose gate is not held right now.
    /// Returns how many were evicted. Public so the bound is unit-testable with an explicit clock.
    public static int SweepRoomCreateLocks(long pNowMs, TimeSpan pIdle)
    {
        int evicted = 0;
        foreach (KeyValuePair<int, long> kvp in _roomLastUseMs)
        {
            if (pNowMs - kvp.Value < (long)pIdle.TotalMilliseconds)
                continue;
            if (_roomCreateLocks.TryGetValue(kvp.Key, out SemaphoreSlim held) && held.CurrentCount == 0)
                continue;   // a select for this room is in progress
            _roomCreateLocks.TryRemove(kvp.Key, out _);
            _roomLastUseMs.TryRemove(kvp.Key, out _);
            evicted++;
        }
        return evicted;
    }

    /// Room numbers currently holding a create gate (diagnostics and tests).
    public static int RoomCreateLockCount => _roomCreateLocks.Count;
}
