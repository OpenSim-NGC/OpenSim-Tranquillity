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

using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.Avatar.AvatarFactory;

/// <summary>
/// The arrival check of baked textures (ValidateBakedTextureCache) on a region that bakes server-side: the bakes
/// there are the baking module's and carry no cache ids, so the check must not take any of them off.
/// </summary>
public class AvatarFactoryServerBakesTests : OpenSimTestCase
{
    private sealed class BakingRegion : IServerSideBakingRegion
    {
        public bool ServerSideBakingEnabled => true;
        public int BakedCofVersion(UUID agentId) => 7;
        public bool BakesFor(UUID agentId) => true;
    }

    /// <summary>An avatar as it arrives back in a region it was in: cache items with no ids, and server bakes.</summary>
    private static (AvatarFactoryModule Afm, ScenePresence Sp, UUID Aux1Bake, UUID HeadBake) Arrived(bool regionBakes)
    {
        AvatarFactoryModule afm = new AvatarFactoryModule();
        TestScene scene = new SceneHelpers(new TestsAssetCache()).SetupScene();
        SceneHelpers.SetupSceneModules(scene, afm);
        if (regionBakes)
            scene.RegisterModuleInterface<IServerSideBakingRegion>(new BakingRegion());
        ScenePresence sp = SceneHelpers.AddScenePresence(scene, TestHelpers.ParseTail(0x1));

        UUID aux1 = TestHelpers.ParseTail(0x42);
        UUID head = TestHelpers.ParseTail(0x08);
        sp.Appearance.Texture.CreateFace(42).TextureID = aux1;
        sp.Appearance.Texture.CreateFace(8).TextureID = head;
        sp.Appearance.WearableCacheItems = WearableCacheItem.GetDefaultCacheItem();
        return (afm, sp, aux1, head);
    }

    [Fact]
    public void ARegionThatBakesKeepsEveryBakeFace()
    {
        var (afm, sp, aux1, head) = Arrived(regionBakes: true);

        Assert.True(afm.ValidateBakedTextureCache(sp));

        Assert.Equal(aux1, sp.Appearance.Texture.GetFace(42).TextureID);
        Assert.Equal(head, sp.Appearance.Texture.GetFace(8).TextureID);
    }

    /// <summary>
    /// Where viewers bake, the check keeps its rule for Bakes on Mesh faces with no cache id (it was written for a
    /// viewer that sends such faces it never baked); only regions that bake are exempt.
    /// </summary>
    [Fact]
    public void WhereViewersBakeAnUncachedBakesOnMeshFaceStillGoes()
    {
        var (afm, sp, aux1, _) = Arrived(regionBakes: false);

        afm.ValidateBakedTextureCache(sp);

        Primitive.TextureEntryFace face = sp.Appearance.Texture.FaceTextures[42];
        Assert.True(face is null || face.TextureID.NotEqual(aux1));
    }
}
