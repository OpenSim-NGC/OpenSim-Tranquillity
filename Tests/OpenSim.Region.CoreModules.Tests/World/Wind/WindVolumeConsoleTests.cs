/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Region.CoreModules.World.Wind;
using OpenSim.Region.Framework.Interfaces;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.World.Wind;

/// <summary>
/// The text and the effect of "weather volumes" and "weather clear", through the helper the console handlers call.
/// Each test owns its store and clock, so the class runs in parallel with the others.
/// </summary>
public class WindVolumeConsoleTests
{
    private sealed class Clock
    {
        public long Ms = 5000;
        public long Now() => Interlocked.Read(ref Ms);
    }

    private static readonly UUID ObjA = UUID.Random();
    private static readonly UUID ObjB = UUID.Random();
    private static readonly UUID Owner = UUID.Random();

    private static WindVolumeSpec Sphere(Vector3? center = null, float seconds = 60f)
        => new WindVolumeSpec
        {
            Shape = WindVolumeShape.Sphere, Center = center ?? new Vector3(100, 110, 50), Radius = 12.5f,
            FalloffStart = 0.5f, LifetimeSeconds = seconds, Velocity = new Vector3(8f, -3f, 1.5f),
        };

    private static WindVolumeSpec Cylinder()
        => new WindVolumeSpec
        {
            Shape = WindVolumeShape.Cylinder, Center = new Vector3(20, 30, 40), Radius = 6f, Height = 30f,
            FalloffStart = 0.5f, LifetimeSeconds = 90f, Velocity = new Vector3(1, 2, 3),
        };

    private static (WindVolumeStore Store, Clock Clock) NewStore(bool enabled = true)
    {
        var clock = new Clock();
        return (new WindVolumeStore(enabled, null, clock.Now), clock);
    }

    private static string[] Run(Action<Action<string>> command)
    {
        var lines = new List<string>();
        command(lines.Add);
        return lines.ToArray();
    }

    // ---- weather volumes ----

    [Fact]
    public void ListShowsEveryFieldOfEveryVolume()
    {
        var (store, clock) = NewStore();
        int a = store.Create(ObjA, Owner, Sphere());
        int b = store.Create(ObjB, Owner, Cylinder());

        string[] lines = Run(o => WindVolumeConsole.List(store, "Test Region", clock.Now(), o));

        Assert.Equal(3, lines.Length);
        Assert.Contains("Test Region", lines[0]);
        Assert.Contains("2 of at most 16", lines[0]);

        Assert.Contains($"handle {a} ", lines[1]);
        Assert.Contains(ObjA.ToString(), lines[1]);
        Assert.Contains(Owner.ToString(), lines[1]);
        Assert.Contains("sphere", lines[1]);
        Assert.Contains("centre <100, 110, 50>", lines[1]);
        Assert.Contains("radius 12.5", lines[1]);
        Assert.Contains("wind <8, -3, 1.5>", lines[1]);
        Assert.Contains("60 s left", lines[1]);

        Assert.Contains($"handle {b} ", lines[2]);
        Assert.Contains("cylinder", lines[2]);
        Assert.Contains("radius 6 height 30", lines[2]);
        Assert.Contains("90 s left", lines[2]);
    }

    [Fact]
    public void ListShowsTheTimeLeftAsTheClockAdvancesAndDropsExpiredVolumes()
    {
        var (store, clock) = NewStore();
        store.Create(ObjA, Owner, Sphere(seconds: 60f));

        clock.Ms += 45_000;
        string[] lines = Run(o => WindVolumeConsole.List(store, "R", clock.Now(), o));
        Assert.Contains("15 s left", lines[1]);

        clock.Ms += 20_000;
        lines = Run(o => WindVolumeConsole.List(store, "R", clock.Now(), o));
        Assert.Single(lines);
        Assert.Contains("0 of at most", lines[0]);
    }

    [Fact]
    public void ListWithNoVolumesSaysSo()
    {
        var (store, clock) = NewStore();

        string[] lines = Run(o => WindVolumeConsole.List(store, "R", clock.Now(), o));

        Assert.Single(lines);
        Assert.Contains("0 of at most", lines[0]);
    }

    [Fact]
    public void ListWithWeatherOffSaysWeatherIsOff()
    {
        var (store, clock) = NewStore(enabled: false);

        string[] lines = Run(o => WindVolumeConsole.List(store, "R", clock.Now(), o));

        Assert.Single(lines);
        Assert.Contains("Weather is off", lines[0]);
        Assert.Single(Run(o => WindVolumeConsole.List(null, "R", 0, o)));
    }

    // ---- weather clear ----

    [Fact]
    public void ClearWithNoWordsRemovesEveryVolume()
    {
        var (store, _) = NewStore();
        store.Create(ObjA, Owner, Sphere());
        store.Create(ObjB, Owner, Cylinder());
        store.Create(ObjB, Owner, Sphere());

        int removed = 0;
        string[] lines = Run(o => removed = WindVolumeConsole.Clear(store, "R", Array.Empty<string>(), o));

        Assert.Equal(3, removed);
        Assert.Equal(0, store.Count);
        Assert.Contains("Removed 3", lines.Single());
    }

    [Fact]
    public void ClearAllIsTheSameAsClearWithNoWords()
    {
        var (store, _) = NewStore();
        store.Create(ObjA, Owner, Sphere());

        int removed = WindVolumeConsole.Clear(store, "R", new[] { "ALL" }, _ => { });

        Assert.Equal(1, removed);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void ClearByHandleRemovesThatVolumeWhoeverMadeIt()
    {
        var (store, _) = NewStore();
        int a = store.Create(ObjA, Owner, Sphere());
        int b = store.Create(ObjB, Owner, Sphere());

        int removed = 0;
        string[] lines = Run(o => removed = WindVolumeConsole.Clear(store, "R", new[] { a.ToString() }, o));

        Assert.Equal(1, removed);
        Assert.Equal(new[] { b }, store.List(UUID.Zero).Select(v => v.Id).ToArray());
        Assert.Contains($"volume {a}", lines.Single());
    }

    [Theory]
    [InlineData("999")]
    [InlineData("0")]
    public void ClearByAnUnknownHandleChangesNothingAndSaysSo(string handle)
    {
        var (store, _) = NewStore();
        store.Create(ObjA, Owner, Sphere());

        int removed = -1;
        string[] lines = Run(o => removed = WindVolumeConsole.Clear(store, "R", new[] { handle }, o));

        Assert.Equal(0, removed);
        Assert.Equal(1, store.Count);
        Assert.Contains("no wind volume", lines.Single());
    }

    [Fact]
    public void ClearByObjectRemovesOnlyThatObjectsVolumes()
    {
        var (store, _) = NewStore();
        store.Create(ObjA, Owner, Sphere());
        store.Create(ObjA, Owner, Cylinder());
        int kept = store.Create(ObjB, Owner, Sphere());

        int removed = 0;
        string[] lines = Run(o => removed = WindVolumeConsole.Clear(store, "R", new[] { "object", ObjA.ToString() }, o));

        Assert.Equal(2, removed);
        Assert.Equal(new[] { kept }, store.List(UUID.Zero).Select(v => v.Id).ToArray());
        Assert.Contains("Removed 2", lines.Single());
    }

    [Fact]
    public void ClearByAnObjectThatMadeNoneRemovesNothing()
    {
        var (store, _) = NewStore();
        store.Create(ObjA, Owner, Sphere());

        int removed = WindVolumeConsole.Clear(store, "R", new[] { "object", UUID.Random().ToString() }, _ => { });

        Assert.Equal(0, removed);
        Assert.Equal(1, store.Count);
    }

    [Theory]
    [InlineData("object")]
    [InlineData("object not-a-uuid")]
    [InlineData("object 00000000-0000-0000-0000-000000000000")]
    [InlineData("-3")]
    [InlineData("1.5")]
    [InlineData("everything")]
    [InlineData("1 2")]
    public void ClearWithABadArgumentPrintsTheUsageAndChangesNothing(string words)
    {
        var (store, _) = NewStore();
        store.Create(ObjA, Owner, Sphere());

        string[] lines = Run(o => WindVolumeConsole.Clear(store, "R", words.Split(' '), o));

        Assert.Equal(1, store.Count);
        Assert.Equal(WindVolumeConsole.ClearUsage, lines.Single());
    }

    [Fact]
    public void ClearWithWeatherOffSaysSoAndChangesNothing()
    {
        var (store, _) = NewStore(enabled: false);

        string[] lines = Run(o => WindVolumeConsole.Clear(store, "R", Array.Empty<string>(), o));

        Assert.Contains("Weather is off", lines.Single());
        Assert.Equal(0, WindVolumeConsole.Clear(null, "R", Array.Empty<string>(), _ => { }));
    }
}
