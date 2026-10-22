/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using Xunit.Sdk;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>
/// The scratch folders the mesh and prim shape tests ask for. Each is made under the system temp path
/// (<see cref="Path.GetTempPath"/>, so TEMP and TMP decide where) and is deleted when the test that asked for it ends.
/// Put <see cref="DeleteScratchFoldersAttribute"/> on a test class that calls <see cref="Create"/>. The classes that do
/// are all in the serial native collection, so no other test ends while one of them is running.
/// </summary>
internal static class ScratchFolders
{
    private static readonly object Gate = new();
    private static readonly List<string> Made = new();

    /// <summary>A new empty folder under the system temp path; it is deleted when the running test ends.</summary>
    internal static string Create(string prefix = "mesh-shapes-")
    {
        string d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        lock (Gate) Made.Add(d);
        return d;
    }

    /// <summary>The folders made and not yet deleted.</summary>
    internal static string[] Pending()
    {
        lock (Gate) return Made.ToArray();
    }

    /// <summary>Delete every folder made and not yet deleted. Returns the ones that could not be deleted; they stay listed, so a later call tries again.</summary>
    internal static string[] DeleteAll()
    {
        string[] folders;
        lock (Gate)
        {
            folders = Made.ToArray();
            Made.Clear();
        }
        var failed = new List<string>();
        foreach (string d in folders)
        {
            try
            {
                if (Directory.Exists(d))
                    Directory.Delete(d, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed.Add(d);
            }
        }
        lock (Gate) Made.AddRange(failed); // tried again by the next call
        return failed.ToArray();
    }
}

/// <summary>Deletes the folders <see cref="ScratchFolders.Create"/> made, after each test of the class (or method) it is put on.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
internal sealed class DeleteScratchFoldersAttribute : BeforeAfterTestAttribute
{
    public override void After(MethodInfo methodUnderTest)
    {
        string[] failed = ScratchFolders.DeleteAll();
        if (failed.Length > 0)
            throw new IOException("Scratch folder(s) could not be deleted: " + string.Join(", ", failed));
    }
}
