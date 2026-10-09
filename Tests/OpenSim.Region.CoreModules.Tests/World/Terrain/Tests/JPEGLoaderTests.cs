/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 */

using OpenSim.Tests.Common;
using OpenSim.Region.CoreModules.World.Terrain.FileLoaders;
using Xunit;

namespace OpenSim.Region.CoreModules.World.Terrain.Tests;

public class JPEGLoaderTests : OpenSimTestCase
{
    [Fact]
    public void BaseAndInterfaceReferencesUseJpegContracts()
    {
        GenericSystemDrawing loader = new JPEG();
        ITerrainLoader terrainLoader = (ITerrainLoader)loader;
        using MemoryStream stream = new MemoryStream();

        Assert.True(loader.SupportsTileSave());
        Assert.Equal(".jpg", terrainLoader.FileExtension);
        Assert.Throws<NotImplementedException>(() => loader.LoadStream(stream));
    }
}
