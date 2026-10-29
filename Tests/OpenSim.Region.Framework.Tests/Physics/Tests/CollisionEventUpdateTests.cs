/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * Copyright (c) Legion Builds
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

using OpenMetaverse;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Tests.Common;

namespace OpenSim.Region.PhysicsModules.SharedBase.Tests
{
    /// <summary>
    /// CollisionEventUpdate.AddCollider merges several contacts with one collider in one report: it keeps the deepest
    /// contact and the largest relative speed seen (the speed drives collision sounds). A penetration depth never
    /// becomes the speed.
    /// </summary>
    public class CollisionEventUpdateTests : OpenSimTestCase
    {
        private static ContactPoint Contact(float depth, float speed)
            => new ContactPoint(new Vector3(1, 2, 3), Vector3.UnitZ, depth) { RelativeSpeed = speed };

        private static ContactPoint Merged(params ContactPoint[] contacts)
        {
            var update = new CollisionEventUpdate();
            foreach (ContactPoint c in contacts)
                update.AddCollider(7, c);
            Assert.Equal(1, update.Count);
            return update.m_objCollisionList[7];
        }

        [Fact]
        public void ADeeperSlowerContactKeepsTheFasterSpeed()
        {
            TestHelpers.InMethod();

            ContactPoint cp = Merged(Contact(0.01f, -3f), Contact(0.05f, -0.5f));

            Assert.Equal(0.05f, cp.PenetrationDepth);
            Assert.Equal(-3f, cp.RelativeSpeed);
        }

        [Fact]
        public void ADepthIsNeverTakenAsTheSpeed()
        {
            TestHelpers.InMethod();

            ContactPoint cp = Merged(Contact(0.4f, -0.1f), Contact(0.6f, -0.05f));

            Assert.Equal(0.6f, cp.PenetrationDepth);
            Assert.Equal(-0.1f, cp.RelativeSpeed);
        }

        [Fact]
        public void ADeeperFasterContactKeepsItsOwnSpeed()
        {
            TestHelpers.InMethod();

            ContactPoint cp = Merged(Contact(0.01f, -0.5f), Contact(0.05f, -3f));

            Assert.Equal(0.05f, cp.PenetrationDepth);
            Assert.Equal(-3f, cp.RelativeSpeed);
        }

        [Fact]
        public void AShallowerFasterContactRaisesTheSpeedOnly()
        {
            TestHelpers.InMethod();

            ContactPoint cp = Merged(Contact(0.05f, -0.5f), Contact(0.01f, -3f));

            Assert.Equal(0.05f, cp.PenetrationDepth);
            Assert.Equal(-3f, cp.RelativeSpeed);
        }
    }
}
