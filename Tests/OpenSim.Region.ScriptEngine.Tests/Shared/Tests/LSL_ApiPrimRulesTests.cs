/*
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
using OpenSim.Framework;
using PermissionMask = OpenSim.Framework.PermissionMask;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Tests.Common;
using Xunit;
using LSL_Float = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLFloat;
using LSL_Integer = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLInteger;
using LSL_Key = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_List = OpenSim.Region.ScriptEngine.Shared.LSL_Types.list;
using LSL_String = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;

namespace OpenSim.Region.ScriptEngine.Shared.Tests
{
    /// <summary>
    /// The prim-params rules YEngine refused or could not compile (PRIM_SIT_FLAGS, PRIM_DAMAGE, PRIM_HEALTH,
    /// PRIM_CLICK_ACTION, PRIM_COLLISION_SOUND), the PRIM_ALLOW_UNSIT / PRIM_SCRIPTED_SIT_ONLY reads, the sit-flag
    /// functions, OBJECT_LOCKED / OBJECT_VOLUME_DETECT, and SL's values for the constants added with them.
    /// </summary>
    // Each test builds its own scene and object and touches no process-wide state, so the class runs in parallel.
    public class LSL_ApiPrimRulesTests
    {
        private static readonly UUID Sound = new("44444444-0000-0000-0000-000000000001");

        private readonly SceneObjectGroup m_group;
        private readonly SceneObjectPart m_part;
        private readonly LSL_Api m_api;

        public LSL_ApiPrimRulesTests()
        {
            IConfigSource config = new IniConfigSource();
            config.AddConfig("XEngine").Set("Enabled", "true");

            Scene scene = new SceneHelpers().SetupScene();
            m_group = SceneHelpers.AddSceneObject(scene, 2, UUID.Random(), "rules-", 0x10);
            m_part = m_group.RootPart;

            MockScriptEngine engine = new MockScriptEngine();
            engine.Initialise(config);
            engine.AddRegion(scene);

            m_api = new LSL_Api();
            m_api.Initialize(engine, m_part, null);
        }

        private static LSL_Integer I(int i) => new(i);
        private static LSL_Float F(double f) => new(f);
        private static LSL_String S(string s) => new(s);

        private LSL_List Get(int rule) => m_api.llGetPrimitiveParams(new LSL_List(I(rule)));

        [Fact]
        public void SitFlagsSetAndReadBack()
        {
            m_api.llSetPrimitiveParams(new LSL_List(I(ScriptBaseClass.PRIM_SIT_FLAGS),
                I(ScriptBaseClass.SIT_FLAG_SCRIPTED_ONLY | ScriptBaseClass.SIT_FLAG_NO_COLLIDE)));

            Assert.False(m_part.AllowUnsit);
            Assert.True(m_part.ScriptedSitOnly);
            int flags = Get(ScriptBaseClass.PRIM_SIT_FLAGS).GetIntegerItem(0);
            Assert.Equal(0, flags & ScriptBaseClass.SIT_FLAG_ALLOW_UNSIT);
            Assert.NotEqual(0, flags & ScriptBaseClass.SIT_FLAG_SCRIPTED_ONLY);
            Assert.Equal(0, flags & ScriptBaseClass.SIT_FLAG_SIT_TARGET);
            Assert.Equal(0, Get(ScriptBaseClass.PRIM_ALLOW_UNSIT).GetIntegerItem(0));
            Assert.Equal(1, Get(ScriptBaseClass.PRIM_SCRIPTED_SIT_ONLY).GetIntegerItem(0));
        }

        [Fact]
        public void LinkSitFlagsActOnTheNamedLink()
        {
            SceneObjectPart child = m_group.GetLinkNumPart(2);
            child.AllowUnsit = true;

            m_api.llSetLinkSitFlags(I(2), I(ScriptBaseClass.SIT_FLAG_SCRIPTED_ONLY));

            Assert.False(child.AllowUnsit);
            Assert.True(child.ScriptedSitOnly);
            Assert.False(m_part.ScriptedSitOnly);
            Assert.Equal(ScriptBaseClass.SIT_FLAG_SCRIPTED_ONLY,
                m_api.llGetLinkSitFlags(I(2)).value & (ScriptBaseClass.SIT_FLAG_SCRIPTED_ONLY | ScriptBaseClass.SIT_FLAG_ALLOW_UNSIT));
        }

        [Fact]
        public void DamageIsTheObjectsAndHeldTo100()
        {
            m_api.llSetPrimitiveParams(new LSL_List(I(ScriptBaseClass.PRIM_DAMAGE), F(250.0), I(ScriptBaseClass.DAMAGE_TYPE_FIRE)));

            Assert.Equal(100f, m_group.Damage);
            LSL_List res = Get(ScriptBaseClass.PRIM_DAMAGE);
            Assert.Equal(2, res.Length);
            Assert.Equal(100.0, res.GetStrictFloatItem(0), 3);
            Assert.Equal(ScriptBaseClass.DAMAGE_TYPE_GENERIC, res.GetIntegerItem(1));
        }

        [Fact]
        public void HealthIsAcceptedAndReadsZero()
        {
            m_api.llSetPrimitiveParams(new LSL_List(I(ScriptBaseClass.PRIM_HEALTH), F(50.0), I(ScriptBaseClass.PRIM_NAME), S("after")));

            Assert.Equal("after", m_part.Name);
            Assert.Equal(0.0, Get(ScriptBaseClass.PRIM_HEALTH).GetStrictFloatItem(0));
        }

        [Fact]
        public void ClickActionSetsAndReads()
        {
            m_api.llSetPrimitiveParams(new LSL_List(I(ScriptBaseClass.PRIM_CLICK_ACTION), I(ScriptBaseClass.CLICK_ACTION_SIT)));

            Assert.Equal((byte)ScriptBaseClass.CLICK_ACTION_SIT, m_part.ClickAction);
            Assert.Equal(ScriptBaseClass.CLICK_ACTION_SIT, Get(ScriptBaseClass.PRIM_CLICK_ACTION).GetIntegerItem(0));
        }

        [Fact]
        public void CollisionSoundSetsAndReads()
        {
            m_api.llSetPrimitiveParams(new LSL_List(I(ScriptBaseClass.PRIM_COLLISION_SOUND), S(Sound.ToString()), F(0.5)));

            Assert.Equal(Sound, m_part.CollisionSound);
            LSL_List res = Get(ScriptBaseClass.PRIM_COLLISION_SOUND);
            Assert.Equal(Sound.ToString(), res.GetStringItem(0));
            Assert.Equal(0.5, res.GetStrictFloatItem(1), 3);

            m_api.llSetPrimitiveParams(new LSL_List(I(ScriptBaseClass.PRIM_COLLISION_SOUND), S(""), F(0.0)));

            Assert.Equal(ScriptBaseClass.NULL_KEY, Get(ScriptBaseClass.PRIM_COLLISION_SOUND).GetStringItem(0));
        }

        [Fact]
        public void LinkTargetRulesReachTheChild()
        {
            SceneObjectPart child = m_group.GetLinkNumPart(2);

            m_api.llSetLinkPrimitiveParamsFast(1, new LSL_List(
                I(ScriptBaseClass.PRIM_SIT_FLAGS), I(0),
                I(ScriptBaseClass.PRIM_LINK_TARGET), I(2),
                I(ScriptBaseClass.PRIM_CLICK_ACTION), I(ScriptBaseClass.CLICK_ACTION_TOUCH)));

            Assert.False(m_part.AllowUnsit);
            Assert.Equal((byte)ScriptBaseClass.CLICK_ACTION_TOUCH, child.ClickAction);
        }

        [Fact]
        public void LockedAndVolumeDetectAreReported()
        {
            m_group.RootPart.OwnerMask &= ~(uint)PermissionMask.Move;
            m_group.InvalidateEffectivePerms();
            m_group.ScriptSetVolumeDetect(true);

            LSL_List res = m_api.llGetObjectDetails(new LSL_Key(m_part.UUID.ToString()),
                new LSL_List(I(ScriptBaseClass.OBJECT_LOCKED), I(ScriptBaseClass.OBJECT_VOLUME_DETECT)));

            Assert.Equal(1, res.GetIntegerItem(0));
            Assert.Equal(1, res.GetIntegerItem(1));
        }

        [Fact]
        public void TheAddedConstantsCarrySlsValues()
        {
            // secondlife/lsl-definitions; a different value would make an SL script do something else here
            var sl = new Dictionary<string, int>
            {
                ["DEREZ_DIE"] = 0, ["DEREZ_MAKE_TEMP"] = 1, ["DEREZ_TO_INVENTORY"] = 2,
                ["PASS_IF_NOT_HANDLED"] = 0, ["PASS_ALWAYS"] = 1, ["PASS_NEVER"] = 2,
                ["STATUS_OK"] = 0, ["STATUS_MALFORMED_PARAMS"] = 1000, ["STATUS_TYPE_MISMATCH"] = 1001,
                ["STATUS_BOUNDS_ERROR"] = 1002, ["STATUS_NOT_FOUND"] = 1003, ["STATUS_NOT_SUPPORTED"] = 1004,
                ["STATUS_INTERNAL_ERROR"] = 1999, ["STATUS_WHITELIST_FAILED"] = 2001,
                ["PRIM_CLICK_ACTION"] = 43, ["PRIM_COLLISION_SOUND"] = 53,
                ["DAMAGE_TYPE_IMPACT"] = -1, ["DAMAGE_TYPE_GENERIC"] = 0, ["DAMAGE_TYPE_EMOTIONAL"] = 14,
                ["PRIM_MEDIA_PARAM_MAX"] = 14, ["PRIM_MEDIA_MAX_URL_LENGTH"] = 1024,
                ["OBJECT_LOCKED"] = 55, ["OBJECT_VOLUME_DETECT"] = 56,
                ["TP_ROUTING_BLOCKED"] = 0, ["TP_ROUTING_LANDINGP"] = 1, ["TP_ROUTING_FREE"] = 2,
                ["LAND_BRUSH_SMALL"] = 0, ["LAND_BRUSH_MEDIUM"] = 1, ["LAND_BRUSH_LARGE"] = 2,
                ["SIT_OK"] = 1, ["PAYMENT_INFO_ON_FILE"] = 1, ["PAYMENT_INFO_USED"] = 2,
                ["AGENT_AUTOMATED"] = 0x4000, ["AGENT_FLOATING_VIA_SCRIPTED_ATTACHMENT"] = 0x8000,
                ["HTTP_BODY_TRUNCATED"] = 0, ["REGION_FLAG_BLOCK_FLYOVER"] = 0x8000000,
                ["STATUS_DIE_ON_NO_REZZER"] = 0x1000, ["REZ_FLAG_DIE_ON_NO_REZZER"] = 0x0100,
            };
            foreach (var (name, value) in sl)
            {
                FieldInfo f = typeof(ScriptBaseClass).GetField(name, BindingFlags.Public | BindingFlags.Static);
                Assert.True(f is not null, name + " is missing");
                Assert.True(value == (int)f!.GetValue(null)!, $"{name} is {f.GetValue(null)}, SL's is {value}");
            }
        }
    }
}
