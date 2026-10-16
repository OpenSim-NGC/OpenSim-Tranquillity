/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.CoreModules.World.Wind;
using OpenSim.Region.Framework.Interfaces;
using Xunit;

namespace OpenSim.Region.CoreModules.Tests.World.Wind;

/// <summary>
/// The wind-volume store: shapes and falloff, lifetime, every cap at the limit and one past it, remove and list,
/// sampling while other threads change the set, and that a store that is switched off keeps nothing.
/// </summary>
// Each test owns its store and a test clock; the concurrency tests use real threads but only their own store, so the
// class runs in parallel with the others.
public class WindVolumeStoreTests
{
    private class Clock
    {
        public long Ms = 1000;
        public long Now() => Interlocked.Read(ref Ms);
    }

    private static readonly UUID ObjA = UUID.Random();
    private static readonly UUID ObjB = UUID.Random();
    private static readonly UUID Owner = UUID.Random();

    private static WindVolumeStore Store(Clock clock = null, WindVolumeLimits limits = null, bool enabled = true)
        => new WindVolumeStore(enabled, limits, clock == null ? null : clock.Now);

    private static WindVolumeSpec Sphere(float radius = 10f, float falloff = 0.5f, float lifetime = 60f, Vector3? velocity = null, Vector3? center = null)
        => new WindVolumeSpec
        {
            Shape = WindVolumeShape.Sphere, Center = center ?? new Vector3(100, 100, 50), Radius = radius,
            FalloffStart = falloff, LifetimeSeconds = lifetime, Velocity = velocity ?? new Vector3(10, 0, 0),
        };

    private static WindVolumeSpec Cylinder(float radius = 10f, float height = 20f, float falloff = 0.5f, float lifetime = 60f, Vector3? velocity = null)
        => new WindVolumeSpec
        {
            Shape = WindVolumeShape.Cylinder, Center = new Vector3(100, 100, 50), Radius = radius, Height = height,
            FalloffStart = falloff, LifetimeSeconds = lifetime, Velocity = velocity ?? new Vector3(10, 0, 0),
        };

    private static void Near(float expected, float actual, float tolerance = 0.0005f)
        => Assert.True(MathF.Abs(expected - actual) <= tolerance, $"expected {expected}, got {actual}");

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 0.0005f)
        => Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

    // ---- shapes and falloff ----

    [Fact]
    public void ASphereIsFullStrengthInsideItsFalloffStartAndNothingOutsideTheRadius()
    {
        var s = Store();
        Assert.True(s.Create(ObjA, Owner, Sphere(radius: 10f, falloff: 0.5f)) >= 1);
        var c = new Vector3(100, 100, 50);
        Near(new Vector3(10, 0, 0), s.Sample(c));
        Near(new Vector3(10, 0, 0), s.Sample(c + new Vector3(5, 0, 0)));   // exactly at the falloff start
        Near(Vector3.Zero, s.Sample(c + new Vector3(10, 0, 0)));            // exactly on the radius
        Near(Vector3.Zero, s.Sample(c + new Vector3(10.01f, 0, 0)));
        Near(Vector3.Zero, s.Sample(c + new Vector3(0, 0, 11)));            // the radius counts in all three directions
        Near(new Vector3(10, 0, 0), s.Sample(c + new Vector3(0, 0, -4)));
    }

    [Fact]
    public void ASphereFallsOffSmoothlyHalfwayThroughTheBandIsHalfStrength()
    {
        var s = Store();
        s.Create(ObjA, Owner, Sphere(radius: 10f, falloff: 0.5f));
        // Smoothstep: halfway through the band (7.5 m from the middle) is 0.5; a quarter of the way is 1 - 0.15625.
        Near(5f, s.Sample(new Vector3(107.5f, 100, 50)).X);
        Near(10f * (1f - 0.15625f), s.Sample(new Vector3(106.25f, 100, 50)).X);
    }

    [Fact]
    public void FalloffStartOneGivesAnEdgeAndZeroFallsFromTheMiddle()
    {
        var s = Store();
        s.Create(ObjA, Owner, Sphere(radius: 10f, falloff: 1f));
        Near(10f, s.Sample(new Vector3(109.9f, 100, 50)).X);
        Near(0f, s.Sample(new Vector3(110f, 100, 50)).X);

        var t = Store();
        t.Create(ObjA, Owner, Sphere(radius: 10f, falloff: 0f));
        Near(10f, t.Sample(new Vector3(100, 100, 50)).X);
        Near(5f, t.Sample(new Vector3(105f, 100, 50)).X);
    }

    [Fact]
    public void ACylinderIsLimitedByRadiusAcrossAndByHeightUpAndDown()
    {
        var s = Store();
        s.Create(ObjA, Owner, Cylinder(radius: 10f, height: 20f, falloff: 0.5f));
        var c = new Vector3(100, 100, 50);
        Near(new Vector3(10, 0, 0), s.Sample(c));
        Near(new Vector3(10, 0, 0), s.Sample(c + new Vector3(0, 0, 5.9f)));  // below the top band (it starts 6 m up): full strength
        Near(Vector3.Zero, s.Sample(c + new Vector3(0, 0, 10)));           // the top face
        Near(Vector3.Zero, s.Sample(c + new Vector3(0, 0, -10)));          // the bottom face
        Near(Vector3.Zero, s.Sample(c + new Vector3(0, 0, 10.5f)));
        Near(Vector3.Zero, s.Sample(c + new Vector3(10, 0, 0)));           // the side
        Near(Vector3.Zero, s.Sample(c + new Vector3(0, 10, 0)));
        Near(new Vector3(10, 0, 0), s.Sample(c + new Vector3(0, 4, 0)));
    }

    [Fact]
    public void ACylinderFallsOffOverTheTopAndBottomFifthOfItsHeight()
    {
        var s = Store();
        s.Create(ObjA, Owner, Cylinder(radius: 10f, height: 20f, falloff: 0.5f));
        var c = new Vector3(100, 100, 50);
        // The band is 4 m (20% of 20 m): from 6 m to 10 m above the middle. 8 m is halfway: 0.5.
        Near(5f, s.Sample(c + new Vector3(0, 0, 8)).X);
        Near(5f, s.Sample(c + new Vector3(0, 0, -8)).X);
        Near(10f, s.Sample(c + new Vector3(0, 0, 6)).X);
    }

    [Fact]
    public void TheWindHasAllThreeComponentsAndTheyAllScaleWithTheWeight()
    {
        var s = Store();
        s.Create(ObjA, Owner, Sphere(radius: 10f, falloff: 0.5f, velocity: new Vector3(4, -6, 8)));
        Near(new Vector3(4, -6, 8), s.Sample(new Vector3(100, 100, 50)));
        Near(new Vector3(2, -3, 4), s.Sample(new Vector3(107.5f, 100, 50)));
    }

    [Fact]
    public void VolumesAddUpAndTheSumIsClampedToTheMaximumSpeed()
    {
        var s = Store(limits: new WindVolumeLimits { MaxSpeed = 50f });
        s.Create(ObjA, Owner, Sphere(velocity: new Vector3(30, 0, 0)));
        s.Create(ObjA, Owner, Sphere(velocity: new Vector3(0, 20, 0)));
        Near(new Vector3(30, 20, 0), s.Sample(new Vector3(100, 100, 50)));

        s.Create(ObjB, Owner, Sphere(velocity: new Vector3(30, 0, 0)));   // 60, 20, 0: length above 50
        Vector3 w = s.Sample(new Vector3(100, 100, 50));
        Near(50f, w.Length());
        Near(new Vector3(60, 20, 0).X / new Vector3(60, 20, 0).Length() * 50f, w.X);
    }

    [Fact]
    public void ASampleIgnoresNonFinitePoints()
    {
        var s = Store();
        s.Create(ObjA, Owner, Sphere());
        Assert.Equal(Vector3.Zero, s.Sample(new Vector3(float.NaN, 100, 50)));
        Assert.Equal(Vector3.Zero, s.Sample(new Vector3(100, float.PositiveInfinity, 50)));
    }

    // ---- lifetime ----

    [Fact]
    public void AVolumeLivesForItsLifetimeAndNotAMillisecondLonger()
    {
        var clock = new Clock();
        var s = Store(clock);
        int id = s.Create(ObjA, Owner, Sphere(lifetime: 30f));
        Assert.True(id >= 1);
        var c = new Vector3(100, 100, 50);

        clock.Ms += 29_999;
        Assert.Equal(1, s.Count);
        Near(new Vector3(10, 0, 0), s.Sample(c));
        Assert.Single(s.List(UUID.Zero));

        clock.Ms += 1;   // exactly 30 s after creation
        Assert.Equal(0, s.Count);
        Near(Vector3.Zero, s.Sample(c));
        Assert.Empty(s.List(UUID.Zero));
        Assert.Equal(WindVolumeError.NotFound, s.Remove(id, UUID.Zero));
    }

    [Fact]
    public void AnExpiredVolumeNoLongerCountsAgainstTheCaps()
    {
        var clock = new Clock();
        var s = Store(clock, new WindVolumeLimits { MaxVolumesRegion = 2, MaxVolumesObject = 2 });
        Assert.True(s.Create(ObjA, Owner, Sphere(lifetime: 10f)) >= 1);
        Assert.True(s.Create(ObjA, Owner, Sphere(lifetime: 100f)) >= 1);
        Assert.Equal(WindVolumeError.RegionFull, s.Create(ObjB, Owner, Sphere()));

        clock.Ms += 10_000;
        Assert.True(s.Create(ObjA, Owner, Sphere()) >= 1);   // the first has expired, so there is room again
    }

    [Fact]
    public void PruneFreesExpiredVolumesAndKeepsTheOthers()
    {
        var clock = new Clock();
        var s = Store(clock);
        s.Create(ObjA, Owner, Sphere(lifetime: 5f));
        int keep = s.Create(ObjA, Owner, Sphere(lifetime: 50f));
        clock.Ms += 5_000;
        s.PruneExpired();
        Assert.Equal(keep, Assert.Single(s.List(UUID.Zero)).Id);
    }

    // ---- caps: at the limit, and one past it ----

    [Fact]
    public void TheRegionCapAcceptsTheLimitAndRefusesOnePast()
    {
        var s = Store(limits: new WindVolumeLimits { MaxVolumesRegion = 3, MaxVolumesObject = 10 });
        for (int i = 0; i < 3; i++) Assert.True(s.Create(UUID.Random(), Owner, Sphere()) >= 1);
        Assert.Equal(WindVolumeError.RegionFull, s.Create(UUID.Random(), Owner, Sphere()));
        Assert.Equal(3, s.Count);
    }

    [Fact]
    public void TheObjectCapAcceptsTheLimitAndRefusesOnePastButAnotherObjectStillGetsRoom()
    {
        var s = Store(limits: new WindVolumeLimits { MaxVolumesRegion = 10, MaxVolumesObject = 2 });
        Assert.True(s.Create(ObjA, Owner, Sphere()) >= 1);
        Assert.True(s.Create(ObjA, Owner, Sphere()) >= 1);
        Assert.Equal(WindVolumeError.ObjectFull, s.Create(ObjA, Owner, Sphere()));
        Assert.True(s.Create(ObjB, Owner, Sphere()) >= 1);
    }

    [Fact]
    public void TheRadiusCapAcceptsTheLimitAndRefusesOnePast()
    {
        var s = Store(limits: new WindVolumeLimits { MaxRadius = 40f });
        Assert.True(s.Create(ObjA, Owner, Sphere(radius: 40f)) >= 1);
        Assert.Equal(WindVolumeError.TooLarge, s.Create(ObjA, Owner, Sphere(radius: 40.01f)));
        Assert.True(s.Create(ObjA, Owner, Cylinder(radius: 40f)) >= 1);
        Assert.Equal(WindVolumeError.TooLarge, s.Create(ObjA, Owner, Cylinder(radius: 40.01f)));
    }

    [Fact]
    public void TheHeightCapAcceptsTheLimitAndRefusesOnePastForACylinder()
    {
        var s = Store(limits: new WindVolumeLimits { MaxHeight = 100f });
        Assert.True(s.Create(ObjA, Owner, Cylinder(height: 100f)) >= 1);
        Assert.Equal(WindVolumeError.TooLarge, s.Create(ObjA, Owner, Cylinder(height: 100.01f)));
    }

    [Fact]
    public void TheLifetimeCapAcceptsTheLimitAndRefusesOnePast()
    {
        var s = Store(limits: new WindVolumeLimits { MaxLifetimeSeconds = 120f });
        Assert.True(s.Create(ObjA, Owner, Sphere(lifetime: 120f)) >= 1);
        Assert.Equal(WindVolumeError.LifetimeTooLong, s.Create(ObjA, Owner, Sphere(lifetime: 120.5f)));
    }

    [Fact]
    public void TheSpeedCapAcceptsTheLimitAndRefusesOnePast()
    {
        var s = Store(limits: new WindVolumeLimits { MaxSpeed = 20f });
        Assert.True(s.Create(ObjA, Owner, Sphere(velocity: new Vector3(20, 0, 0))) >= 1);
        Assert.True(s.Create(ObjA, Owner, Sphere(velocity: new Vector3(0, 0, -20))) >= 1);   // vertical counts
        Assert.Equal(WindVolumeError.TooFast, s.Create(ObjA, Owner, Sphere(velocity: new Vector3(20.01f, 0, 0))));
        Assert.Equal(WindVolumeError.TooFast, s.Create(ObjA, Owner, Sphere(velocity: new Vector3(12, 12, 12))));
    }

    [Fact]
    public void ARefusedCreateStoresNothing()
    {
        var s = Store(limits: new WindVolumeLimits { MaxRadius = 10f });
        Assert.Equal(WindVolumeError.TooLarge, s.Create(ObjA, Owner, Sphere(radius: 11f)));
        Assert.Equal(0, s.Count);
        Assert.Empty(s.List(UUID.Zero));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void ABadRadiusOrLifetimeIsInvalid(float bad)
    {
        var s = Store();
        Assert.Equal(WindVolumeError.InvalidParameter, s.Create(ObjA, Owner, Sphere(radius: bad)));
        Assert.Equal(WindVolumeError.InvalidParameter, s.Create(ObjA, Owner, Sphere(lifetime: bad)));
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void OtherBadValuesAreInvalid()
    {
        var s = Store();
        Assert.Equal(WindVolumeError.InvalidParameter, s.Create(ObjA, Owner, Sphere(falloff: -0.1f)));
        Assert.Equal(WindVolumeError.InvalidParameter, s.Create(ObjA, Owner, Sphere(falloff: 1.1f)));
        Assert.Equal(WindVolumeError.InvalidParameter, s.Create(ObjA, Owner, Cylinder(height: 0f)));
        Assert.Equal(WindVolumeError.InvalidParameter, s.Create(ObjA, Owner, Sphere(velocity: new Vector3(float.NaN, 0, 0))));
        Assert.Equal(WindVolumeError.InvalidParameter, s.Create(ObjA, Owner, Sphere(center: new Vector3(0, float.PositiveInfinity, 0))));
        Assert.Equal(WindVolumeError.InvalidParameter, s.Create(ObjA, Owner, new WindVolumeSpec { Shape = (WindVolumeShape)7, Radius = 5, LifetimeSeconds = 5 }));
        Assert.Equal(0, s.Count);
    }

    // ---- remove and list ----

    [Fact]
    public void RemoveTakesAVolumeOutAndOnlyItsCreatorMayDoIt()
    {
        var s = Store();
        int a = s.Create(ObjA, Owner, Sphere());
        int b = s.Create(ObjB, Owner, Sphere());

        Assert.Equal(WindVolumeError.NotAllowed, s.Remove(a, ObjB));
        Assert.Equal(2, s.Count);
        Assert.Equal(WindVolumeError.Ok, s.Remove(a, ObjA));
        Assert.Equal(WindVolumeError.NotFound, s.Remove(a, ObjA));
        Assert.Equal(WindVolumeError.NotFound, s.Remove(9999, ObjA));
        Assert.Equal(b, Assert.Single(s.List(UUID.Zero)).Id);
        Assert.Equal(WindVolumeError.Ok, s.Remove(b, UUID.Zero));   // no creator: an administrative removal
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void ARemovedVolumeNoLongerBlowsAndFreesItsRoom()
    {
        var s = Store(limits: new WindVolumeLimits { MaxVolumesRegion = 1 });
        int a = s.Create(ObjA, Owner, Sphere());
        Assert.Equal(WindVolumeError.RegionFull, s.Create(ObjA, Owner, Sphere()));
        s.Remove(a, ObjA);
        Near(Vector3.Zero, s.Sample(new Vector3(100, 100, 50)));
        Assert.True(s.Create(ObjA, Owner, Sphere()) >= 1);
    }

    [Fact]
    public void ListGivesTheCreatorsVolumesOldestFirstWithTheirValues()
    {
        var s = Store();
        int a1 = s.Create(ObjA, Owner, Sphere(radius: 5f));
        int b1 = s.Create(ObjB, Owner, Sphere(radius: 6f));
        int a2 = s.Create(ObjA, Owner, Cylinder(radius: 7f, height: 9f));

        Assert.Equal(new[] { a1, a2 }, s.List(ObjA).Select(v => v.Id));
        Assert.Equal(new[] { b1 }, s.List(ObjB).Select(v => v.Id));
        Assert.Equal(new[] { a1, b1, a2 }, s.List(UUID.Zero).Select(v => v.Id));
        Assert.Empty(s.List(UUID.Random()));

        WindVolume cyl = s.List(ObjA)[1];
        Assert.Equal(WindVolumeShape.Cylinder, cyl.Shape);
        Assert.Equal(7f, cyl.Radius);
        Assert.Equal(9f, cyl.Height);
        Assert.Equal(Owner, cyl.Owner);
        Assert.Equal(ObjA, cyl.CreatorObject);
    }

    [Fact]
    public void HandlesAreDistinctAndStartAtOne()
    {
        var s = Store();
        int a = s.Create(ObjA, Owner, Sphere());
        int b = s.Create(ObjA, Owner, Sphere());
        Assert.Equal(1, a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void RemoveByCreatorAndClearTakeOutWhatTheySay()
    {
        var s = Store();
        s.Create(ObjA, Owner, Sphere());
        s.Create(ObjA, Owner, Sphere());
        s.Create(ObjB, Owner, Sphere());
        Assert.Equal(2, s.RemoveByCreator(ObjA));
        Assert.Equal(1, s.Count);
        Assert.Equal(1, s.Clear());
        Assert.Equal(0, s.Count);
    }

    // ---- switched off ----

    [Fact]
    public void ADisabledStoreKeepsNothingAndAnswersAsIfEmpty()
    {
        var s = Store(enabled: false);
        Assert.False(s.Enabled);
        Assert.Equal(WindVolumeError.Disabled, s.Create(ObjA, Owner, Sphere()));
        Assert.Equal(0, s.Count);
        Assert.Empty(s.List(UUID.Zero));
        Assert.Equal(Vector3.Zero, s.Sample(new Vector3(100, 100, 50)));
        Assert.Equal(WindVolumeError.NotFound, s.Remove(1, ObjA));
    }

    // ---- config ----

    [Fact]
    public void WithNoWeatherSectionOrWithEnabledLeftOutTheStoreIsOff()
    {
        Assert.False(WindModule.CreateWindVolumes(null).Enabled);
        IConfig empty = new IniConfigSource().AddConfig("Weather");
        Assert.False(WindModule.CreateWindVolumes(empty).Enabled);
        empty.Set("max_volumes_region", "5");
        var store = WindModule.CreateWindVolumes(empty);
        Assert.False(store.Enabled);
        Assert.Equal(WindVolumeError.Disabled, store.Create(ObjA, Owner, Sphere()));
    }

    [Fact]
    public void TheDefaultsAreTheDocumentedOnes()
    {
        IConfig weather = new IniConfigSource().AddConfig("Weather");
        weather.Set("enabled", "true");
        WindVolumeLimits l = WindModule.CreateWindVolumes(weather).Limits;
        Assert.Equal(16, l.MaxVolumesRegion);
        Assert.Equal(4, l.MaxVolumesObject);
        Assert.Equal(128f, l.MaxRadius);
        Assert.Equal(512f, l.MaxHeight);
        Assert.Equal(50f, l.MaxSpeed);
        Assert.Equal(3600f, l.MaxLifetimeSeconds);
    }

    [Fact]
    public void EveryKeyIsRead()
    {
        IConfig weather = new IniConfigSource().AddConfig("Weather");
        weather.Set("enabled", "true");
        weather.Set("max_volumes_region", "3");
        weather.Set("max_volumes_object", "2");
        weather.Set("max_radius", "30");
        weather.Set("max_height", "60");
        weather.Set("max_speed", "12.5");
        weather.Set("max_lifetime", "90");
        var store = WindModule.CreateWindVolumes(weather);
        Assert.True(store.Enabled);
        Assert.Equal(3, store.Limits.MaxVolumesRegion);
        Assert.Equal(2, store.Limits.MaxVolumesObject);
        Assert.Equal(30f, store.Limits.MaxRadius);
        Assert.Equal(60f, store.Limits.MaxHeight);
        Assert.Equal(12.5f, store.Limits.MaxSpeed);
        Assert.Equal(90f, store.Limits.MaxLifetimeSeconds);
    }

    // ---- rule lists ----

    [Fact]
    public void RulesBuildASpecWithDefaultsForTheOptionalOnes()
    {
        var rules = new object[] { WindVolumeRules.WV_CENTER, new Vector3(1, 2, 3), WindVolumeRules.WV_RADIUS, 12.5f,
            WindVolumeRules.WV_VELOCITY, new Vector3(0, 0, 4), WindVolumeRules.WV_LIFETIME, 30 };
        Assert.Equal(WindVolumeError.Ok, WindVolumeRules.TryParse(rules, out WindVolumeSpec spec));
        Assert.Equal(WindVolumeShape.Sphere, spec.Shape);
        Assert.Equal(new Vector3(1, 2, 3), spec.Center);
        Assert.Equal(12.5f, spec.Radius);
        Assert.Equal(0.5f, spec.FalloffStart);
        Assert.Equal(30f, spec.LifetimeSeconds);
        Assert.Equal(new Vector3(0, 0, 4), spec.Velocity);
    }

    [Fact]
    public void ACylinderNeedsAHeightAndTheLaterOfTwoEqualRulesCounts()
    {
        var noHeight = new object[] { WindVolumeRules.WV_SHAPE, WindVolumeRules.WV_CYLINDER, WindVolumeRules.WV_CENTER, Vector3.Zero,
            WindVolumeRules.WV_RADIUS, 5f, WindVolumeRules.WV_VELOCITY, Vector3.UnitX, WindVolumeRules.WV_LIFETIME, 5f };
        Assert.Equal(WindVolumeError.InvalidParameter, WindVolumeRules.TryParse(noHeight, out _));

        var withHeight = noHeight.Concat(new object[] { WindVolumeRules.WV_HEIGHT, 9f, WindVolumeRules.WV_HEIGHT, 11f }).ToArray();
        Assert.Equal(WindVolumeError.Ok, WindVolumeRules.TryParse(withHeight, out WindVolumeSpec spec));
        Assert.Equal(11f, spec.Height);
        Assert.Equal(WindVolumeShape.Cylinder, spec.Shape);
    }

    [Fact]
    public void BadRuleListsAreInvalid()
    {
        var good = new object[] { WindVolumeRules.WV_CENTER, Vector3.Zero, WindVolumeRules.WV_RADIUS, 5f,
            WindVolumeRules.WV_VELOCITY, Vector3.UnitX, WindVolumeRules.WV_LIFETIME, 5f };
        Assert.Equal(WindVolumeError.Ok, WindVolumeRules.TryParse(good, out _));

        Assert.Equal(WindVolumeError.InvalidParameter, WindVolumeRules.TryParse(null, out _));
        Assert.Equal(WindVolumeError.InvalidParameter, WindVolumeRules.TryParse(Array.Empty<object>(), out _));            // required rules missing
        Assert.Equal(WindVolumeError.InvalidParameter, WindVolumeRules.TryParse(good.Take(7).ToArray(), out _));           // odd length
        Assert.Equal(WindVolumeError.InvalidParameter, WindVolumeRules.TryParse(good.Concat(new object[] { 99, 1 }).ToArray(), out _));   // unknown rule
        Assert.Equal(WindVolumeError.InvalidParameter, WindVolumeRules.TryParse(good.Concat(new object[] { WindVolumeRules.WV_FALLOFF, "x" }).ToArray(), out _));   // wrong type
        Assert.Equal(WindVolumeError.InvalidParameter, WindVolumeRules.TryParse(good.Concat(new object[] { WindVolumeRules.WV_SHAPE, 5 }).ToArray(), out _));       // unknown shape
        Assert.Equal(WindVolumeError.InvalidParameter, WindVolumeRules.TryParse(good.Concat(new object[] { WindVolumeRules.WV_CENTER, 3f }).ToArray(), out _));    // not a vector
        Assert.Equal(WindVolumeError.InvalidParameter, WindVolumeRules.TryParse(new object[] { "a", 1 }, out _));
    }

    // ---- concurrency ----

    [Fact]
    public void SamplingWhileOtherThreadsAddAndRemoveNeverSeesATornVolume()
    {
        // Every volume's velocity has three equal components, and every volume covers the sampled point at full
        // strength, so any sample must have X == Y == Z, and a whole number of volumes' worth.
        var limits = new WindVolumeLimits { MaxVolumesRegion = 64, MaxVolumesObject = 64, MaxSpeed = 5000f };
        var s = Store(limits: limits);
        var point = new Vector3(100, 100, 50);
        var stop = new CancellationTokenSource();
        int bad = 0;
        long samples = 0;

        var writers = Enumerable.Range(0, 3).Select(w => Task.Run(() =>
        {
            var obj = UUID.Random();
            var mine = new Queue<int>();
            var rnd = new Random(w);
            while (!stop.IsCancellationRequested)
            {
                if (mine.Count < 8 && rnd.Next(2) == 0)
                {
                    int id = s.Create(obj, Owner, Sphere(radius: 20f, falloff: 1f, velocity: new Vector3(1, 1, 1)));
                    if (id >= 1) mine.Enqueue(id);
                }
                else if (mine.Count > 0)
                {
                    s.Remove(mine.Dequeue(), obj);
                }
            }
        })).ToArray();

        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                Vector3 w = s.Sample(point);
                if (w.X != w.Y || w.Y != w.Z || w.X != MathF.Round(w.X) || w.X < 0f || w.X > 24f) Interlocked.Increment(ref bad);
                Interlocked.Increment(ref samples);
                if (s.List(UUID.Zero).Count < 0) Interlocked.Increment(ref bad);
            }
        })).ToArray();

        // Run until the readers have made plenty of samples, with a generous cap.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (Interlocked.Read(ref samples) < 300_000 && DateTime.UtcNow < deadline) Thread.Sleep(20);
        stop.Cancel();
        Task.WaitAll(writers.Concat(readers).ToArray());

        Assert.True(Interlocked.Read(ref samples) >= 300_000, "the readers did not make enough samples");
        Assert.Equal(0, bad);
    }

    [Fact]
    public void ConcurrentCreatesNeverPassTheRegionCap()
    {
        var s = Store(limits: new WindVolumeLimits { MaxVolumesRegion = 10, MaxVolumesObject = 100 });
        int accepted = 0;
        System.Threading.Tasks.Parallel.For(0, 200, _ =>
        {
            if (s.Create(UUID.Random(), Owner, Sphere()) >= 1) Interlocked.Increment(ref accepted);
        });
        Assert.Equal(10, accepted);
        Assert.Equal(10, s.Count);
        Assert.Equal(10, s.List(UUID.Zero).Select(v => v.Id).Distinct().Count());
    }

    [Fact]
    public void ASamplerDoesNotWaitForAWriterThatHoldsTheStoreBusy()
    {
        // A reader that finishes while a writer is stuck inside the write lock proves reads take no lock.
        var inside = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var blocker = new BlockingClock(inside, release);
        var s2 = new WindVolumeStore(true, null, blocker.Now);
        s2.Create(ObjA, Owner, Sphere());   // the clock does not block yet
        blocker.Armed = true;
        var writer = Task.Run(() => s2.Create(ObjB, Owner, Sphere()));   // blocks inside the lock, in the clock call
        Assert.True(inside.Wait(TimeSpan.FromSeconds(20)));

        // The sampler uses the explicit-time overload, so it does not touch the blocked clock; it must not need the lock.
        var read = Task.Run(() => s2.Sample(new Vector3(100, 100, 50), 1500));
        Assert.True(read.Wait(TimeSpan.FromSeconds(20)), "the sampler waited for the writer");
        Near(new Vector3(10, 0, 0), read.Result);

        release.Set();
        Assert.True(writer.Wait(TimeSpan.FromSeconds(20)));
    }

    private class BlockingClock
    {
        private readonly ManualResetEventSlim m_inside, m_release;
        public volatile bool Armed;
        public BlockingClock(ManualResetEventSlim inside, ManualResetEventSlim release) { m_inside = inside; m_release = release; }
        public long Now()
        {
            if (Armed) { m_inside.Set(); m_release.Wait(); }
            return 1000;
        }
    }
}
