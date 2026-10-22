/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.Jolt.Backend;

namespace OpenSim.Region.PhysicsModules.Jolt
{
    /// <summary>
    /// What the module asks of the diagnostics assembly (OpenSim.Region.PhysicsModules.Jolt.Diagnostics): the console
    /// test commands and the few places they listen in on the region's step. The module makes one per region, only
    /// when [Jolt] TestCommands is true and the assembly is beside the module (<see cref="JoltDiagnosticsLoader"/>), and
    /// calls nothing here otherwise. The interface is internal; the assembly sees it through InternalsVisibleTo.
    /// </summary>
    internal interface IJoltDiagnostics
    {
        /// <summary>The scene this object serves. Called once, before any other member.</summary>
        void Attach(JoltScene scene);

        /// <summary>Register the test subcommands of `jolt`, each as its own console command.</summary>
        void RegisterCommands(ICommands commands);

        /// <summary>Register `jolt parity` for the region's scene (RegionLoaded, before the region's physics is switched on).</summary>
        void RegisterParity(Scene scene);

        /// <summary>The [Startup] JoltAutoDropTest drop, run when the region has loaded.</summary>
        void AutoDrop(IConfigSource source);

        /// <summary>The region is going away: end the test commands' route to the scene.</summary>
        void RegionRemoved(Scene scene);

        /// <summary>A body's state, drained after a step (the drop tracker follows its drops here).</summary>
        void BodyUpdated(in BodyState state);

        /// <summary>A heartbeat's step has been reported (the per-frame drop log).</summary>
        void StepReported(IPhysicsBackend backend, long stepCount, float timeStep, in StepResult result);

        /// <summary>Output lines for the tracked drops, appended to the `jolt heights` report.</summary>
        void DescribeDrops(IPhysicsBackend backend, Action<string> output);
    }

    /// <summary>
    /// Finds and loads the diagnostics assembly. It is a plain library with no region module in it: the plugin
    /// discovery loads every region module's assembly into a load context of its own, where the module's types are not
    /// the ones the running module has, so a region module found that way could not use the scene it is meant to
    /// serve. This loads the file into the module's own load context instead, where its references to the module are the
    /// module's.
    /// </summary>
    internal static class JoltDiagnosticsLoader
    {
        internal const string AssemblyFileName = "OpenSim.Region.PhysicsModules.Jolt.Diagnostics";
        internal const string DiagnosticsTypeName = AssemblyFileName + ".JoltDiagnostics";

        private static int s_missingLogged;
        private static int s_failureLogged;

        /// <summary>Lets the next call log its line again; for tests, which count the lines.</summary>
        internal static void ForgetLogged()
        {
            Volatile.Write(ref s_missingLogged, 0);
            Volatile.Write(ref s_failureLogged, 0);
        }

        /// <summary>The folder the module's own assembly is in, or null where it has none (a single-file publish).</summary>
        internal static string ModuleFolder()
        {
            string location = typeof(JoltDiagnosticsLoader).Assembly.Location;
            return string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
        }

        /// <summary>
        /// Make the diagnostics object from <c>OpenSim.Region.PhysicsModules.Jolt.Diagnostics.dll</c> in
        /// <paramref name="folder"/>, or return null and log one line (once per process for each cause) saying why the
        /// test commands are not there. A missing or unusable assembly never stops the module.
        /// </summary>
        internal static IJoltDiagnostics Load(string folder, ILogger log)
        {
            string path = string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, AssemblyFileName + ".dll");
            if (path == null || !File.Exists(path))
            {
                if (Interlocked.Exchange(ref s_missingLogged, 1) == 0)
                    log.LogWarning($"{JoltScene.LogHeader} [Jolt] TestCommands is true but {AssemblyFileName}.dll is not in the folder of the Jolt module, " +
                                   "so the test commands are not registered; copy it there or set TestCommands = false.");
                return null;
            }

            try
            {
                AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(typeof(JoltDiagnosticsLoader).Assembly) ?? AssemblyLoadContext.Default;
                Assembly assembly = context.Assemblies.FirstOrDefault(a => a.GetName().Name == AssemblyFileName)
                                    ?? context.LoadFromAssemblyPath(path);
                Type type = assembly.GetType(DiagnosticsTypeName, throwOnError: false);
                if (type == null)
                    throw new TypeLoadException($"{DiagnosticsTypeName} is not in {AssemblyFileName}.dll");
                return (IJoltDiagnostics)Activator.CreateInstance(type, nonPublic: true);
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref s_failureLogged, 1) == 0)
                    log.LogWarning($"{JoltScene.LogHeader} [Jolt] TestCommands is true but {AssemblyFileName}.dll could not be used " +
                                   $"({e.GetType().Name}: {e.Message}), so the test commands are not registered.");
                return null;
            }
        }
    }
}
