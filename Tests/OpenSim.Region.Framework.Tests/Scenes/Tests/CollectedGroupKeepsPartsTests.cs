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

using System.Runtime.CompilerServices;
using OpenMetaverse;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// A group made around parts that then go to another group (as the region store tests do, and as code that wraps a
/// part to store or send it does) used to dispose those parts when the collector finalized it: their inventory
/// went null under the live group, at whatever moment the collector ran, and the next link or save of that group
/// threw. The collection is forced here so the old behaviour shows every time.
/// </summary>
public class CollectedGroupKeepsPartsTests
{
    private static readonly UUID s_owner = new UUID("00000000-0000-0000-0000-0000000000a1");

    private static SceneObjectPart[] Parts(int count)
        => Enumerable.Range(1, count)
            .Select(i => SceneHelpers.CreateSceneObjectPart("P" + i, UUID.Random(), s_owner))
            .ToArray();

    /// <summary>Wraps each part in a group of its own and drops the groups, as a store-it-separately helper does.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WrapAndDrop(SceneObjectPart[] parts)
    {
        foreach (SceneObjectPart p in parts)
            _ = new SceneObjectGroup(p);
    }

    private static void Collect()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    [Fact]
    public void AGroupCollectedAfterItsPartsMovedOnLeavesThemWhole()
    {
        SceneObjectPart[] parts = Parts(4);
        WrapAndDrop(parts);

        SceneObjectGroup linkset = new SceneObjectGroup(parts[0]);
        for (int i = 1; i < parts.Length; i++)
            linkset.AddPart(parts[i]);

        Collect();

        foreach (SceneObjectPart p in parts)
        {
            Assert.NotNull(p.Inventory);
            Assert.False(p.IsDeleted, $"{p.Name} was disposed by a group that no longer held it");
        }
        Assert.False(linkset.IsDeleted);

        // and the linkset can still be linked to, in a scene
        TestScene scene = new SceneHelpers().SetupScene();
        scene.AddSceneObject(linkset);
        SceneObjectGroup another = new SceneObjectGroup(Parts(1)[0]);
        scene.AddSceneObject(another);
        linkset.LinkToGroup(another);
        Assert.Equal(5, linkset.PrimCount);
    }

    [Fact]
    public void AnExplicitDisposeStillDisposesTheParts()
    {
        SceneObjectPart[] parts = Parts(2);
        SceneObjectGroup group = new SceneObjectGroup(parts[0]);
        group.AddPart(parts[1]);

        group.Dispose();

        Assert.True(group.IsDeleted);
        Assert.All(parts, p => Assert.Null(p.Inventory));
    }
}
