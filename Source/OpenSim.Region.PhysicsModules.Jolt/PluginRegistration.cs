/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Jolt physics region module.
 *
 * Plugin registration. Mirrors Source/OpenSim.Region.PhysicsModules.ubODE/PluginRegistration.cs:
 * the host's DotNetCorePlugins discovery (DotNetCorePluginsDiscovery.GetExtensionNodes) scans the
 * plugin directory for assemblies exporting IPluginRegistryProvider and calls RegisterPlugins. We
 * register the region-module type at /OpenSim/RegionModules so the RegionModulesController picks it
 * up exactly like ubODE and BulletSim.
 *
 * The registered type is JoltModule, which makes the JoltScene (the PhysicsScene) only when
 * [Startup] physics = Jolt, as ubODE splits ubODEModule from ODEScene. Under any other engine nothing
 * of Jolt runs (see JoltModule.cs).
 */

using System.Reflection;
using OpenSim.Framework;

namespace OpenSim.Region.PhysicsModules.Jolt;

public class PluginRegistration : IPluginRegistryProvider
{
    public void RegisterPlugins(PluginRegistry registry)
    {
        RegisterByName(registry, "/OpenSim/RegionModules", "JoltPhysicsScene", "OpenSim.Region.PhysicsModules.Jolt.JoltModule", "JoltPhysicsScene");
    }

    private static void RegisterByName(PluginRegistry registry, string extensionPath, string id, string typeName, string displayName)
    {
        Assembly assembly = typeof(PluginRegistration).Assembly;
        Type type = assembly.GetType(typeName, false);
        if (type == null)
            return;

        registry.Register(
            extensionPath,
            new PluginDescriptor(id, type, displayName, "0.9"));
    }
}
