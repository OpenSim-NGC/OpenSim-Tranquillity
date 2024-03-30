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

using Microsoft.Extensions.Logging;
using OpenMetaverse;

namespace OpenSim.Region.Framework.Scenes
{
    public class MovementAnimationOverrides
    {
        private object MAOLock = new object();
        private Dictionary<string, UUID> m_overrides = new Dictionary<string, UUID>();

        private readonly ILogger _logger;

        public MovementAnimationOverrides(ILogger<MovementAnimationOverrides> logger)
        {
            _logger = logger;
        }

        public void SetOverride(string state, UUID animID)
        {
            if (animID.IsZero())
            {
                if (state == "ALL")
                    m_overrides.Clear();
                else
                    m_overrides.Remove(state);
                return;
            }

            _logger.LogDebug($"Setting override for {state} to {animID}", state, animID);

            lock (MAOLock)
                m_overrides[state] = animID;
        }

        public UUID GetOverriddenAnimation(string state)
        {
            lock (MAOLock)
            {
                if (m_overrides.ContainsKey(state))
                    return m_overrides[state];
            }

            return UUID.Zero;
        }

        public bool TryGetOverriddenAnimation(string state, out UUID animID)
        {
            lock (MAOLock)
                return m_overrides.TryGetValue(state, out animID);
        }

        public Dictionary<string, UUID> CloneAOPairs()
        {
            lock (MAOLock)
            {
                return new Dictionary<string, UUID>(m_overrides);
            }
        }

        public void CopyAOPairsFrom(Dictionary<string, UUID> src)
        {
            lock (MAOLock)
            {
                m_overrides.Clear();
                m_overrides = new Dictionary<string, UUID>(src);
            }
        }
    }
}
