/*
 * Copyright (c) Legion Builds
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using Xunit;

using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Tests.Common;

using LSL_Float = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLFloat;
using LSL_Integer = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLInteger;
using LSL_List = OpenSim.Region.ScriptEngine.Shared.LSL_Types.list;

namespace OpenSim.Region.CoreModules.Scripting.PrimParams.Tests;

/// <summary>
/// PRIM_REFLECTION_PROBE read through llGetPrimitiveParams in the shared LSL_Api. The SL wiki gives the rule as
/// [ PRIM_REFLECTION_PROBE, integer active, float ambiance, float clip_distance, integer flags ], so flags is an
/// integer whether or not the prim is a probe.
/// </summary>
public class ReflectionProbeParamsTests : OpenSimTestCase
{
    /// <summary>A stand-in engine: the API reads only its scene and configuration.</summary>
    public class StandInEngine : DispatchProxy
    {
        public Scene Scene;
        public IConfigSource Source;

        protected override object Invoke(MethodInfo method, object[] args)
        {
            switch (method.Name)
            {
                case "get_World": return Scene;
                case "get_ConfigSource": return Source;
                case "get_Config": return Source.Configs["YEngine"];
                case "get_ScriptEngineName": return "YEngine";
            }
            Type rt = method.ReturnType;
            return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
        }
    }

    private static LSL_Api ApiIn(SceneObjectPart host, Scene scene)
    {
        IniConfigSource source = new();
        source.AddConfig("YEngine");
        IScriptEngine engine = DispatchProxy.Create<IScriptEngine, StandInEngine>();
        ((StandInEngine)(object)engine).Scene = scene;
        ((StandInEngine)(object)engine).Source = source;

        LSL_Api api = new();
        api.Initialize(engine, host, new TaskInventoryItem { ItemID = UUID.Random(), Name = "probe script" });
        return api;
    }

    private static LSL_List ProbeRule() => new(new LSL_Integer(ScriptBaseClass.PRIM_REFLECTION_PROBE));

    [Fact]
    public void AProbesFlagsReadBackAsAnInteger()
    {
        TestScene scene = new SceneHelpers().SetupScene();
        SceneObjectGroup so = SceneHelpers.CreateSceneObject(1, UUID.Random());
        scene.AddNewSceneObject(so, false);
        LSL_Api api = ApiIn(so.RootPart, scene);

        api.llSetPrimitiveParams(new LSL_List(
            new LSL_Integer(ScriptBaseClass.PRIM_REFLECTION_PROBE),
            new LSL_Integer(1), new LSL_Float(2.0), new LSL_Float(5.0), new LSL_Integer(3)));
        LSL_List result = api.llGetPrimitiveParams(ProbeRule());

        Assert.Equal(4, result.Length);
        Assert.IsType<LSL_Integer>(result.Data[0]);
        Assert.Equal(1, ((LSL_Integer)result.Data[0]).value);
        Assert.IsType<LSL_Float>(result.Data[1]);
        Assert.IsType<LSL_Float>(result.Data[2]);
        Assert.IsType<LSL_Integer>(result.Data[3]);
        Assert.Equal(3, ((LSL_Integer)result.Data[3]).value);
    }

    [Fact]
    public void APrimThatIsNotAProbeReadsFlagsAsIntegerZero()
    {
        TestScene scene = new SceneHelpers().SetupScene();
        SceneObjectGroup so = SceneHelpers.CreateSceneObject(1, UUID.Random());
        scene.AddNewSceneObject(so, false);
        LSL_Api api = ApiIn(so.RootPart, scene);

        LSL_List result = api.llGetPrimitiveParams(ProbeRule());

        Assert.Equal(4, result.Length);
        Assert.Equal(0, ((LSL_Integer)result.Data[0]).value);
        Assert.IsType<LSL_Integer>(result.Data[3]);
        Assert.Equal(0, ((LSL_Integer)result.Data[3]).value);
    }
}
