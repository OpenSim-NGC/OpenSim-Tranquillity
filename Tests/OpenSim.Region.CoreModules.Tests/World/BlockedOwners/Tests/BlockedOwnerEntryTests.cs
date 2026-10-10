/*
 * Copyright (c) Legion Builds
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
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using Xunit;

using OpenSim.Region.CoreModules.Framework.EntityTransfer;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation;
using OpenSim.Region.CoreModules.World.Objects.BlockedOwners;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;

namespace OpenSim.Region.CoreModules.World.BlockedOwners.Tests;

/// <summary>
/// An object crossing into a region whose blocked-owner list holds its owner is refused entry, through
/// the entry check the entity transfer module asks for every arriving object that is not an attachment.
/// </summary>
public class BlockedOwnerEntryTests : OpenSimTestCase
{
    private static readonly UUID BlockedId = new("6f2a9c14-d7e3-4b58-a0c6-91e4b7d2f358");
    private static readonly UUID OtherId = new("e1c58b3a-4f97-42d0-b6e8-7a3c0d915f26");
    private static readonly Vector3 StartPos = new(128, 10, 20);

    private TestScene m_sceneA = null!;
    private TestScene m_sceneB = null!;
    private BlockedOwnerModule m_moduleB = null!;

    public override void SetUp()
    {
        base.SetUp();

        EntityTransferModule etmA = new();
        EntityTransferModule etmB = new();
        LocalSimulationConnectorModule lscm = new();

        IConfigSource config = new IniConfigSource();
        IConfig modulesConfig = config.AddConfig("Modules");
        modulesConfig.Set("EntityTransferModule", etmA.Name);
        modulesConfig.Set("SimulationServices", lscm.Name);

        // Region B lies south of region A: an object moved to y < 0 in A crosses into B.
        SceneHelpers sh = new();
        m_sceneA = sh.SetupScene("Region A", UUID.Random(), 1000, 1000);
        m_sceneB = sh.SetupScene("Region B", UUID.Random(), 1000, 999);

        m_moduleB = new BlockedOwnerModule();
        SceneHelpers.SetupSceneModules(new Scene[] { m_sceneA, m_sceneB }, config, lscm);
        SceneHelpers.SetupSceneModules(m_sceneA, config, etmA);
        SceneHelpers.SetupSceneModules(m_sceneB, config, etmB, m_moduleB);
    }

    // Starts a crossing from A to B and waits until A has finished with the object: it has left A, or the
    // crossing failed and A has ended the object's transit.
    private (SceneObjectGroup? inA, SceneObjectGroup? inB) Cross(UUID owner, int tail)
    {
        SceneObjectGroup so = SceneHelpers.AddSceneObject(m_sceneA, 1, owner, "crosser", tail);
        UUID id = so.UUID;
        so.AbsolutePosition = StartPos;

        so.AbsolutePosition = new Vector3(128, -10, 20);

        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            SceneObjectGroup? inA = m_sceneA.GetSceneObjectGroup(id);
            if (inA is null || !inA.inTransit)
                return (inA, m_sceneB.GetSceneObjectGroup(id));
            Thread.Sleep(50);
        }
        throw new TimeoutException("The crossing did not finish in 20 s.");
    }

    private int EntryHandlerCount()
    {
        FieldInfo f = typeof(ScenePermissions).GetField("OnObjectEntry", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(ScenePermissions).FullName, "OnObjectEntry");
        return (f.GetValue(m_sceneB.Permissions) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    [Fact]
    public void ABlockedOwnersObjectIsRefusedEntryAndStaysInTheRegionItCameFrom()
    {
        m_moduleB.Block(BlockedId);

        (SceneObjectGroup? inA, SceneObjectGroup? inB) = Cross(BlockedId, 0x10);

        Assert.Null(inB);
        Assert.NotNull(inA);
        Assert.False(inA.IsDeleted);
        Assert.True(m_sceneA.PositionIsInCurrentRegion(inA.AbsolutePosition));
        Assert.Equal(StartPos, inA.AbsolutePosition);
    }

    [Fact]
    public void AnotherOwnersObjectEntersWhileSomeoneIsBlocked()
    {
        m_moduleB.Block(BlockedId);

        (SceneObjectGroup? inA, SceneObjectGroup? inB) = Cross(OtherId, 0x20);

        Assert.Null(inA);
        Assert.NotNull(inB);
    }

    [Fact]
    public void ARegionThatBlocksNoOneRegistersNoEntryHandlerAndLetsTheObjectIn()
    {
        Assert.Equal(0, EntryHandlerCount());

        (SceneObjectGroup? inA, SceneObjectGroup? inB) = Cross(BlockedId, 0x30);

        Assert.Null(inA);
        Assert.NotNull(inB);
    }

    [Fact]
    public void TheEntryHandlerIsRegisteredOnlyWhileSomeoneIsBlocked()
    {
        m_moduleB.Block(BlockedId);
        Assert.Equal(1, EntryHandlerCount());

        m_moduleB.Unblock(BlockedId);
        Assert.Equal(0, EntryHandlerCount());
    }

    [Fact]
    public void ABlockedOwnersObjectStillMovesInsideTheRegion()
    {
        // The handler refuses only an object entering the region, not a move within it.
        m_moduleB.Block(BlockedId);
        SceneObjectGroup so = SceneHelpers.AddSceneObject(m_sceneB, 1, BlockedId, "mover", 0x40);

        Assert.True(m_sceneB.Permissions.CanObjectEntry(so, false, new Vector3(50, 50, 25)));
        Assert.False(m_sceneB.Permissions.CanObjectEntry(so, true, new Vector3(50, 50, 25)));
    }
}
