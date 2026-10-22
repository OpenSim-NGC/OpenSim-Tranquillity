/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Xunit;

namespace OpenSim.Region.PhysicsModules.Jolt.Tests;

/// <summary>The scratch folders of the shape tests are made under the system temp path and deleted when the test ends.</summary>
[Collection(JoltNativeSerial.Name)]
public class ScratchFoldersTests
{
    private static string TempRoot() => Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    [Fact]
    public void The_folder_is_made_under_the_system_temp_path_with_the_mesh_shapes_prefix()
    {
        string d = ScratchFolders.Create();
        try
        {
            Assert.True(Directory.Exists(d));
            Assert.Equal(TempRoot(), Path.GetFullPath(Path.GetDirectoryName(d)!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            Assert.StartsWith("mesh-shapes-", Path.GetFileName(d));
        }
        finally
        {
            ScratchFolders.DeleteAll();
        }
    }

    [Fact]
    public void The_attribute_deletes_the_folder_and_what_is_in_it_when_the_test_ends()
    {
        string a = ScratchFolders.Create();
        string b = ScratchFolders.Create();
        File.WriteAllText(Path.Combine(a, "smap_1"), "x");
        Directory.CreateDirectory(Path.Combine(b, "sub"));
        File.WriteAllText(Path.Combine(b, "sub", "f"), "y");

        new DeleteScratchFoldersAttribute().After(typeof(ScratchFoldersTests).GetMethod(nameof(The_attribute_deletes_the_folder_and_what_is_in_it_when_the_test_ends))!);

        Assert.False(Directory.Exists(a));
        Assert.False(Directory.Exists(b));
        Assert.Empty(ScratchFolders.Pending());
    }

    [Fact]
    public void A_folder_that_is_already_gone_is_not_an_error()
    {
        string a = ScratchFolders.Create();
        Directory.Delete(a);
        new DeleteScratchFoldersAttribute().After(typeof(ScratchFoldersTests).GetMethod(nameof(A_folder_that_is_already_gone_is_not_an_error))!);
        Assert.Empty(ScratchFolders.Pending());
    }

    [Fact]
    public void A_folder_that_cannot_be_deleted_fails_the_test_instead_of_being_left_behind_silently()
    {
        string a = ScratchFolders.Create();
        // An open file inside the folder stops its deletion on Windows; elsewhere the delete succeeds and there is nothing to check.
        using var held = new FileStream(Path.Combine(a, "held"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var method = typeof(ScratchFoldersTests).GetMethod(nameof(A_folder_that_cannot_be_deleted_fails_the_test_instead_of_being_left_behind_silently))!;
        if (OperatingSystem.IsWindows())
            Assert.Throws<IOException>(() => new DeleteScratchFoldersAttribute().After(method));
        held.Dispose();
        ScratchFolders.DeleteAll();
        Assert.False(Directory.Exists(a));
    }
}
