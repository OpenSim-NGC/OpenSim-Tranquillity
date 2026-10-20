/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// The region module the host loads for Jolt (see PluginRegistration.cs).
//
// The host makes one instance of every non-shared region module for every region: its plugin discovery takes the
// registered type and every other class that implements INonSharedRegionModule (IPluginDiscovery.cs,
// GetExtensionNodes), and the region module controller calls Initialise, AddRegion, RegionLoaded, RemoveRegion and
// Close on each, whatever [Startup] physics names. This module reads [Startup] physics and nothing else; unless it
// names Jolt, every call returns at once. It then never makes a JoltScene, so the native is not loaded or hashed, no
// [Jolt] key is read, no job pool or thread starts, no console command is registered and nothing is logged.
// With physics = Jolt it makes the JoltScene and hands every call to it. JoltScene takes them through IJoltRegion
// rather than INonSharedRegionModule, so discovery does not make a second Jolt module for each region.
// ubODE splits its module the same way: ubODEModule makes its ODEScene only when selected
// (Source/OpenSim.Region.PhysicsModules.ubODE/ODEModule.cs, AddRegion).

using System.Runtime.CompilerServices;
using Nini.Config;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    public sealed class JoltModule : INonSharedRegionModule
    {
        // The [Startup] physics value that selects this module. Compared exactly, as every engine compares its own.
        internal const string EngineName = "Jolt";

        // The scene, made in Initialise only when Jolt is selected. Typed as the interface so that this class does
        // not refer to JoltScene's fields, and loading it loads none of the assemblies JoltScene needs.
        private IJoltRegion m_scene;

        public string Name => EngineName;

        public System.Type ReplaceableInterface => null;

        /// <summary>True when [Startup] physics names Jolt.</summary>
        internal static bool Selected(IConfigSource source)
            => source?.Configs["Startup"]?.GetString("physics", string.Empty) == EngineName;

        public void Initialise(IConfigSource source)
        {
            if (!Selected(source))
                return;
            IJoltRegion scene = MakeScene();
            scene.Initialise(source);   // throws, with its one clear error line, when the native or meshing is unusable
            m_scene = scene;
        }

        // Kept out of Initialise so that compiling Initialise does not load JoltScene.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IJoltRegion MakeScene() => new JoltScene();

        public void AddRegion(Scene scene) => m_scene?.AddRegion(scene);

        public void RegionLoaded(Scene scene) => m_scene?.RegionLoaded(scene);

        public void RemoveRegion(Scene scene) => m_scene?.RemoveRegion(scene);

        public void Close() => m_scene?.Close();
    }

    /// <summary>The region module calls JoltModule hands to the JoltScene it made.</summary>
    internal interface IJoltRegion
    {
        void Initialise(IConfigSource source);
        void AddRegion(Scene scene);
        void RegionLoaded(Scene scene);
        void RemoveRegion(Scene scene);
        void Close();
    }
}
