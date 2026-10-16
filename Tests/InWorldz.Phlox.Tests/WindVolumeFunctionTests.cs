/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using InWorldz.Phlox.Types;
using OpenMetaverse;
using OpenSim.Region.CoreModules.World.Wind;
using OpenSim.Region.Framework.Interfaces;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// iwWindVolumeCreate, iwWindVolumeRemove and iwWindVolumes: who may use them (the estate owner, an estate manager or
/// a god, as iwSetWind), what they return when weather is off or the caller is refused, that they reach the region's
/// wind-volume store, and osSetWindParam, which with weather on needs the same owner and with weather off is as before.
/// </summary>
// Calls the API directly on the harness prim with a store of its own; no clock and no process-wide state, so the class
// runs in parallel.
public class WindVolumeFunctionTests
{
    private static LSLSystemAPI Api(SchedulerHarness h) => new LSLSystemAPI(h.Engine, h.Prim, h.Prim.LocalId, UUID.Random());

    // A wind module with a real store, recording the parameters set through it.
    private class StoreWind : DispatchProxy
    {
        public IWindVolumeStore Store;
        public readonly List<(string Plugin, string Param, float Value)> ParamSets = new();

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod.Name == "get_WindVolumes") return Store;
            if (targetMethod.Name == "WindParamSet")
                lock (ParamSets) ParamSets.Add(((string)args[0], (string)args[1], (float)args[2]));
            var rt = targetMethod.ReturnType;
            return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
        }
    }

    private static StoreWind Wind(SchedulerHarness h, bool weatherOn = true)
    {
        var proxy = DispatchProxy.Create<IWindModule, StoreWind>();
        var wind = (StoreWind)(object)proxy;
        wind.Store = new WindVolumeStore(weatherOn);
        h.Scene.RegisterModuleInterface<IWindModule>(proxy);
        return wind;
    }

    private static void Estate(SchedulerHarness h, bool manager, bool god = false)
    {
        UUID owner = h.Prim.OwnerID;
        h.Scene.Permissions.OnIsAdministrator += id => god && id == owner;
        h.Scene.Permissions.OnIssueEstateCommand += (user, ownerCommand) => manager && user == owner;
    }

    private static LSLList Sphere(float radius = 10f, float lifetime = 60f)
        => new LSLList(new List<object>
        {
            WindVolumeRules.WV_CENTER, new Vector3(128, 128, 60), WindVolumeRules.WV_RADIUS, radius,
            WindVolumeRules.WV_VELOCITY, new Vector3(5, 0, 2), WindVolumeRules.WV_LIFETIME, lifetime,
        });

    private static SchedulerHarness Harness()
        => new SchedulerHarness(cfg => cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "Severe"));

    // ---- the gate ----

    [Fact]
    public void AnEstateManagersObjectCanCreateListAndRemoveAVolume()
    {
        using var h = Harness();
        Estate(h, manager: true);
        var wind = Wind(h);
        var api = Api(h);

        int handle = api.iwWindVolumeCreate(Sphere());
        Assert.True(handle >= 1, $"got {handle}");
        Assert.Equal(new object[] { handle }, api.iwWindVolumes().Data);

        Assert.Equal(0, api.iwWindVolumeRemove(handle));
        Assert.Empty(api.iwWindVolumes().Data);
        Assert.Equal(0, wind.Store.Count);
    }

    [Fact]
    public void AGodsObjectCanCreateAVolume()
    {
        using var h = Harness();
        Estate(h, manager: false, god: true);
        Wind(h);
        Assert.True(Api(h).iwWindVolumeCreate(Sphere()) >= 1);
    }

    [Fact]
    public void AnyoneElseIsRefusedOnEveryFunctionAndNothingIsStored()
    {
        using var h = Harness();
        Estate(h, manager: false);
        var wind = Wind(h);
        var api = Api(h);

        Assert.Equal(WindVolumeError.NotAllowed, api.iwWindVolumeCreate(Sphere()));
        Assert.Equal(0, wind.Store.Count);

        // A volume that exists (made by someone with the right) is not removable or listable by a refused owner.
        int other = wind.Store.Create(h.Prim.ParentGroup.UUID, UUID.Random(), WindSpec());
        Assert.True(other >= 1);
        Assert.Equal(WindVolumeError.NotAllowed, api.iwWindVolumeRemove(other));
        Assert.Empty(api.iwWindVolumes().Data);
        Assert.Equal(1, wind.Store.Count);
    }

    [Fact]
    public void AnObjectCannotRemoveAnotherObjectsVolume()
    {
        using var h = Harness();
        Estate(h, manager: true);
        var wind = Wind(h);
        int theirs = wind.Store.Create(UUID.Random(), UUID.Random(), WindSpec());
        Assert.Equal(WindVolumeError.NotAllowed, Api(h).iwWindVolumeRemove(theirs));
        Assert.Equal(WindVolumeError.NotFound, Api(h).iwWindVolumeRemove(424242));
        Assert.Equal(1, wind.Store.Count);
    }

    // ---- weather off ----

    [Fact]
    public void WithWeatherOffEveryFunctionAnswersDisabledAndNothingIsStored()
    {
        using var h = Harness();
        Estate(h, manager: true);
        var wind = Wind(h, weatherOn: false);
        var api = Api(h);

        Assert.Equal(WindVolumeError.Disabled, api.iwWindVolumeCreate(Sphere()));
        Assert.Equal(WindVolumeError.Disabled, api.iwWindVolumeRemove(1));
        Assert.Empty(api.iwWindVolumes().Data);
        Assert.Equal(0, wind.Store.Count);
    }

    [Fact]
    public void WithNoWindModuleOrNoStoreTheAnswerIsDisabledToo()
    {
        using var h = Harness();
        Estate(h, manager: true);
        var api = Api(h);
        Assert.Equal(WindVolumeError.Disabled, api.iwWindVolumeCreate(Sphere()));   // no module on the scene

        var wind = Wind(h);
        wind.Store = null;                                                          // a module without volumes
        Assert.Equal(WindVolumeError.Disabled, api.iwWindVolumeCreate(Sphere()));
        Assert.Empty(api.iwWindVolumes().Data);
    }

    // ---- reaching the store ----

    [Fact]
    public void ACreatedVolumeIsInTheStoreWithTheScriptsValues()
    {
        using var h = Harness();
        Estate(h, manager: true);
        var wind = Wind(h);

        int handle = Api(h).iwWindVolumeCreate(new LSLList(new List<object>
        {
            WindVolumeRules.WV_SHAPE, WindVolumeRules.WV_CYLINDER, WindVolumeRules.WV_CENTER, new Vector3(100, 90, 40),
            WindVolumeRules.WV_RADIUS, 12f, WindVolumeRules.WV_HEIGHT, 30f, WindVolumeRules.WV_FALLOFF, 0.25f,
            WindVolumeRules.WV_VELOCITY, new Vector3(-3, 4, 1), WindVolumeRules.WV_LIFETIME, 90,
        }));

        WindVolume v = Assert.Single(wind.Store.List(UUID.Zero));
        Assert.Equal(handle, v.Id);
        Assert.Equal(h.Prim.ParentGroup.UUID, v.CreatorObject);
        Assert.Equal(h.Prim.OwnerID, v.Owner);
        Assert.Equal(WindVolumeShape.Cylinder, v.Shape);
        Assert.Equal(new Vector3(100, 90, 40), v.Center);
        Assert.Equal(12f, v.Radius);
        Assert.Equal(30f, v.Height);
        Assert.Equal(0.25f, v.FalloffStart);
        Assert.Equal(new Vector3(-3, 4, 1), v.Velocity);
        Assert.Equal(new Vector3(-3, 4, 1), wind.Store.Sample(new Vector3(100, 90, 40)));
    }

    [Fact]
    public void BadRulesAndOverCapValuesReturnTheirErrorAndStoreNothing()
    {
        using var h = Harness();
        Estate(h, manager: true);
        var wind = Wind(h);
        var api = Api(h);

        Assert.Equal(WindVolumeError.InvalidParameter, api.iwWindVolumeCreate(new LSLList()));
        Assert.Equal(WindVolumeError.InvalidParameter, api.iwWindVolumeCreate(new LSLList(new List<object> { 77, 1 })));
        Assert.Equal(WindVolumeError.TooLarge, api.iwWindVolumeCreate(Sphere(radius: 129f)));
        Assert.Equal(WindVolumeError.LifetimeTooLong, api.iwWindVolumeCreate(Sphere(lifetime: 3601f)));
        Assert.Equal(0, wind.Store.Count);
    }

    [Fact]
    public void TheObjectCapShowsUpAsAnErrorCodeToTheScript()
    {
        using var h = Harness();
        Estate(h, manager: true);
        Wind(h);
        var api = Api(h);
        for (int i = 0; i < 4; i++) Assert.True(api.iwWindVolumeCreate(Sphere()) >= 1);
        Assert.Equal(WindVolumeError.ObjectFull, api.iwWindVolumeCreate(Sphere()));
    }

    // ---- osSetWindParam ----

    [Fact]
    public void OsSetWindParamWithWeatherOnNeedsAnEstateOwnerManagerOrGod()
    {
        using var h = Harness();
        Estate(h, manager: false);
        var wind = Wind(h, weatherOn: true);
        Api(h).osSetWindParam("ZephyrWind", "avg_strength", 9f);
        Assert.Empty(wind.ParamSets);
    }

    [Fact]
    public void OsSetWindParamWithWeatherOnWorksForAnEstateManagerAndForAGod()
    {
        using (var h = Harness())
        {
            Estate(h, manager: true);
            var wind = Wind(h, weatherOn: true);
            Api(h).osSetWindParam("ZephyrWind", "avg_strength", 9f);
            Assert.Equal(("ZephyrWind", "avg_strength", 9f), Assert.Single(wind.ParamSets));
        }
        using (var h = Harness())
        {
            Estate(h, manager: false, god: true);
            var wind = Wind(h, weatherOn: true);
            Api(h).osSetWindParam("ZephyrWind", "avg_strength", 7f);
            Assert.Single(wind.ParamSets);
        }
    }

    [Fact]
    public void OsSetWindParamWithWeatherOffIsAsBeforeForEveryone()
    {
        using var h = Harness();
        Estate(h, manager: false);
        var wind = Wind(h, weatherOn: false);
        Api(h).osSetWindParam("ZephyrWind", "avg_strength", 9f);
        Assert.Equal(("ZephyrWind", "avg_strength", 9f), Assert.Single(wind.ParamSets));
    }

    [Fact]
    public void OsSetWindParamWithAModuleThatHasNoWeatherStoreIsAsBefore()
    {
        using var h = Harness();
        Estate(h, manager: false);
        var wind = Wind(h, weatherOn: true);
        wind.Store = null;
        Api(h).osSetWindParam("ZephyrWind", "avg_strength", 9f);
        Assert.Single(wind.ParamSets);
    }

    // ---- the script table ----

    [Fact]
    public void TheThreeFunctionsAndTheirConstantsAreKnownToTheCompiler()
    {
        foreach (string name in new[] { "iwWindVolumeCreate", "iwWindVolumeRemove", "iwWindVolumes" })
            Assert.True(Defaults.TryGetMethod(name, out _), name);
        Assert.True(Defaults.TryGetMethod("iwWindVolumes", out var list));
        Assert.Equal(VarType.List, list.ReturnType);
        Assert.True(Defaults.TryGetMethod("iwWindVolumeCreate", out var create));
        Assert.Equal(VarType.Integer, create.ReturnType);
    }

    private static WindVolumeSpec WindSpec() => new WindVolumeSpec
    {
        Shape = WindVolumeShape.Sphere, Center = new Vector3(128, 128, 60), Radius = 10f, FalloffStart = 0.5f,
        LifetimeSeconds = 60f, Velocity = new Vector3(1, 0, 0),
    };
}
