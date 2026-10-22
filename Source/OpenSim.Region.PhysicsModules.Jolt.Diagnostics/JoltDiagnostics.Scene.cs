/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.Jolt.Backend;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModules.Jolt.Diagnostics
{
    // Everything the test commands read from or call on the scene they serve. These are the members of JoltScene that
    // the module makes internal for this assembly (InternalsVisibleTo); the commands use them under the names they had
    // when they were part of JoltScene.
    internal sealed partial class JoltDiagnostics
    {
        private Scene _scene => _s.RegionScene;
        private IPhysicsBackend _backend => _s.Backend;
        private System.Collections.Generic.Dictionary<uint, JoltPrim> _prims => _s.Prims;
        private int _regionSizeX => _s.RegionSizeX;
        private int _regionSizeY => _s.RegionSizeY;
        private long _stepCount => _s.StepCount;
        private int _lastActiveBodyCount => _s.LastActiveBodyCount;
        private JoltScene.MeshStats _lastMeshStats => _s.LastMeshStats;
        private string RegionName => _s.RegionName;
        private float WaterLevel => _s.WaterLevel;

        private float TerrainHeightAt(float x, float y) => _s.TerrainHeightAt(x, y);
        private void SetTerrain(float[] heightMap) => _s.SetTerrain(heightMap);
        private void RemovePrim(PhysicsActor prim) => _s.RemovePrim(prim);
        private ScenePresence FirstRootAvatar() => _s.FirstRootAvatar();
        private void SitStatus() => _s.SitStatus();
        private bool WrongConsoleScene() => _s.WrongConsoleScene();

        private ShapeId CookPrimShape(IPhysicsBackend backend, PrimitiveBaseShape pbs, OpenMetaverse.Vector3 size, bool isPhysical,
                                      out System.Numerics.Quaternion axisCorrection, out string kind)
            => _s.CookPrimShape(backend, pbs, size, isPhysical, out axisCorrection, out kind);
    }
}
