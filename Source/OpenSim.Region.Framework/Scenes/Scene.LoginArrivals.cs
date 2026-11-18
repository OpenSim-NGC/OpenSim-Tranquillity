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

using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Framework.Monitoring;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.Framework.Scenes;

/// <summary>
/// Logins whose viewer has not arrived yet. The login service marks the user online (presence and grid user) once
/// this region has taken the login's circuit; until the avatar is root here nothing logs it out again: the presence
/// and activity detectors only log out a root agent. A viewer that never connects (a cancelled login, a crash, UDP
/// blocked) or that goes before it finishes arriving left the user online until their next login, and the circuit in
/// this region for good.
/// </summary>
public partial class Scene
{
    /// <summary>
    /// How long a login's circuit waits for its viewer to connect. Set from [Startup] LoginConnectWaitSeconds; the
    /// default is 120 seconds. Zero keeps the circuit until the region restarts, as before.
    /// </summary>
    public TimeSpan LoginConnectWait { get; set; } = TimeSpan.FromSeconds(120);

    private readonly ConcurrentDictionary<UUID, LoginArrival> m_loginArrivals = new();
    private int m_loginArrivalsHooked;
    private int m_checkingLoginArrivals;

    private sealed record LoginArrival(UUID SessionID, long Deadline);

    /// <summary>
    /// Follow a login that this region has taken, until its avatar is root here.
    /// </summary>
    private void AwaitLoginArrival(AgentCircuitData acd)
    {
        if (LoginConnectWait <= TimeSpan.Zero)
            return;

        if (Interlocked.Exchange(ref m_loginArrivalsHooked, 1) == 0)
        {
            EventManager.OnMakeRootAgent += LoginArrived;
            EventManager.OnRemovePresence += LoginPresenceRemoved;
        }

        m_loginArrivals[acd.AgentID] =
            new LoginArrival(acd.SessionID, Environment.TickCount64 + (long)LoginConnectWait.TotalMilliseconds);
    }

    /// <summary>
    /// Logins still waiting for their viewer, by agent.
    /// </summary>
    public IReadOnlyCollection<UUID> LoginsAwaitingViewer => m_loginArrivals.Keys.ToArray();

    private void LoginArrived(ScenePresence sp)
    {
        if (m_loginArrivals.TryGetValue(sp.UUID, out LoginArrival arrival)
                && sp.ControllingClient?.SessionId == arrival.SessionID)
            ForgetLoginArrival(sp.UUID, arrival);
    }

    // the avatar went before it was root here: its viewer will not finish arriving, and the detectors do not log out
    // a child agent
    private void LoginPresenceRemoved(UUID agentID)
    {
        if (!m_loginArrivals.TryGetValue(agentID, out LoginArrival arrival) || !ForgetLoginArrival(agentID, arrival))
            return;

        m_log.LogInformation(
            "[SCENE]: The viewer of {0} left {1} before it finished arriving from its login: logging the login out",
            agentID, Name);
        WorkManager.RunInThreadPool(_ => LogOutLogin(agentID, arrival.SessionID), null, $"LogOutLogin ({Name})");
    }

    private bool ForgetLoginArrival(UUID agentID, LoginArrival arrival)
        => ((ICollection<KeyValuePair<UUID, LoginArrival>>)m_loginArrivals).Remove(new(agentID, arrival));

    /// <summary>
    /// Look at the logins still waiting for their viewer: one whose viewer has not connected by the deadline is
    /// closed here and logged out. Called from the scene loop; the work runs off it.
    /// </summary>
    private void CheckLoginArrivals()
    {
        if (m_loginArrivals.IsEmpty || Interlocked.Exchange(ref m_checkingLoginArrivals, 1) == 1)
            return;

        WorkManager.RunInThreadPool(_ =>
        {
            try
            {
                ExpireLoginArrivals();
            }
            finally
            {
                m_checkingLoginArrivals = 0;
            }
        }, null, $"CheckLoginArrivals ({Name})");
    }

    /// <summary>
    /// Close and log out the logins whose viewer has not connected by their deadline.
    /// </summary>
    public void ExpireLoginArrivals()
    {
        long now = Environment.TickCount64;
        foreach ((UUID agentID, LoginArrival arrival) in m_loginArrivals)
        {
            AgentCircuitData acd = m_authenticateHandler.GetAgentCircuitData(agentID);
            if (acd is null || acd.SessionID != arrival.SessionID)
            {
                // closed here, or replaced by a later login or teleport: nothing of this login is left to close
                ForgetLoginArrival(agentID, arrival);
                continue;
            }

            // a viewer that connected is followed until its avatar is root or removed; a connection that goes quiet
            // is timed out by the client stack, which removes the presence
            if (now < arrival.Deadline || GetScenePresence(agentID) is not null)
                continue;

            if (!ForgetLoginArrival(agentID, arrival))
                continue;

            lock (m_removeClientLock)
            {
                // the viewer may have connected since the look above
                if (GetScenePresence(agentID) is not null)
                    continue;
                m_authenticateHandler.RemoveCircuit(acd);
            }
            m_capsModule?.RemoveCaps(agentID, acd.circuitcode);

            m_log.LogInformation(
                "[SCENE]: The viewer of {0} {1} {2} did not connect to {3} within {4} s of its login: closing the login",
                acd.firstname, acd.lastname, agentID, Name, LoginConnectWait.TotalSeconds);
            LogOutLogin(agentID, arrival.SessionID);
        }
    }

    /// <summary>
    /// Log out a login that never got its avatar in world: its presence session, and the grid user's online flag.
    /// A later login of the same user has its own session; it is left alone.
    /// </summary>
    private void LogOutLogin(UUID agentID, UUID sessionID)
    {
        try
        {
            IPresenceService presence = PresenceService;
            if (presence is not null)
            {
                if (presence.GetAgent(sessionID) is null)
                    return;
                presence.LogoutAgent(sessionID);
            }

            IGridUserService gridUser = GridUserService;
            if (gridUser is null)
                return;

            string userID = agentID.ToString();
            if (UserManagementModule is not null && UserManagementModule.GetUserUUI(agentID, out string uui))
                userID = uui;

            // where the user last was stays as it was: the avatar was never here
            GridUserInfo info = gridUser.GetGridUserInfo(userID);
            if (info is null || !info.Online)
                return;
            gridUser.LoggedOut(userID, sessionID, info.LastRegionID, info.LastPosition, info.LastLookAt);
        }
        catch (Exception e)
        {
            m_log.LogWarning(e, "[SCENE]: Logging out the login of {0} that never arrived in {1} failed", agentID, Name);
        }
    }
}
