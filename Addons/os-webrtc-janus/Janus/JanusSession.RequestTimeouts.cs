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

using System.Net.Mime;
using System.Reflection;

using OpenMetaverse.StructuredData;

using Microsoft.Extensions.Logging;
using OpenSim.Framework;

namespace osWebRtcVoice;

// Bounded ack'd requests: a request that gets no event completes with an error.
public partial class JanusSession
{
    // How long an ack'd request waits for its completing event before SendToJanus returns a
    // synthetic error. [JanusWebRtcVoice] RequestTimeoutMs (WebRtcJanusService), default 5000 ms.
    public const int DefaultRequestTimeoutMs = 5000;

    public TimeSpan JanusRequestTimeout { get; set; } = TimeSpan.FromMilliseconds(DefaultRequestTimeoutMs);

    // Error.reason of the synthetic "error" responses SendToJanus returns for a request that
    // never got its event. Callers see an ordinary Janus error (ReturnCode "error").
    public const string RequestTimeoutReason = "timeout";

    public const string SessionDestroyedReason = "session destroyed";

    public const string LongPollExitedReason = "long poll exited";

    // A trickle is a handle-level request: if the audiobridge is active, it is sent to its handle.
    private async Task<JanusMessageResp> SendTrickle(JanusViewerSession pVSession, TrickleReq pReq)
    {
        if (pVSession.AudioBridge is null)
            return await SendToJanusNoWait(pReq);
        return await SendToJanusNoWait(pReq, pVSession.AudioBridge.PluginUri);
    }

    // Requests parked waiting for their event (diagnostics and tests).
    public int OutstandingRequestCount
    {
        get { lock (_OutstandingRequests) return _OutstandingRequests.Count; }
    }

    // Remove pReq's entry under the lock, only if the entry for its transaction is still pReq
    // (a duplicate-transaction Add that threw must not remove the original). TRUE if this call removed it.
    private bool RemoveOutstanding(OutstandingRequest pReq)
    {
        lock (_OutstandingRequests)
        {
            if (_OutstandingRequests.TryGetValue(pReq.TransactionId, out OutstandingRequest current)
                && ReferenceEquals(current, pReq))
            {
                return _OutstandingRequests.Remove(pReq.TransactionId);
            }
            return false;
        }
    }

    // Complete every parked request with a synthetic error. Called when no event can arrive any
    // more (DestroySession, long-poll exit). Snapshot-and-clear under the lock; complete outside it with
    // TrySetResult, so an event that claimed an entry first simply wins.
    private void FailOutstanding(string pReason)
    {
        List<OutstandingRequest> pending;
        lock (_OutstandingRequests)
        {
            pending = new List<OutstandingRequest>(_OutstandingRequests.Values);
            _OutstandingRequests.Clear();
        }
        if (pending.Count > 0)
            m_log.LogDebug("{0} FailOutstanding: completing {1} pending request(s) with error \"{2}\"", LogHeader, pending.Count, pReason);
        foreach (OutstandingRequest r in pending)
            r.TaskCompletionSource.TrySetResult(MakeErrorResp(r.TransactionId, pReason));
    }

    // A Janus-shaped error ({"janus":"error","transaction":...,"error":{"code":0,"reason":...}}) for a
    // request that never got its event. Code 0: synthetic, never sent by Janus; the reason distinguishes.
    private static JanusMessageResp MakeErrorResp(string pTransactionId, string pReason)
    {
        var err = new ErrorResp();
        err.TransactionId = pTransactionId;
        err.SetError(0, pReason);
        return err;
    }

    // "janus" op plus the plugin body's "request" when present, e.g. "message/join" (timeout log only).
    private static string DescribeOp(JanusMessageReq pReq)
    {
        OSDMap body = pReq.RawBody;
        string op = body.TryGetString("janus", out string janus) ? janus : "?";
        if (body.TryGetOSDMap("body", out OSDMap pluginBody) && pluginBody.TryGetString("request", out string request))
            op += "/" + request;
        return op;
    }
}
