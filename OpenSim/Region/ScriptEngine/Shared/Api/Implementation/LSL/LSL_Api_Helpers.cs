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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Remoting.Lifetime;
using System.Threading;
using log4net;
using OpenMetaverse;
using OpenMetaverse.Rendering;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Scripting;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.Api.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Services.Interfaces;
using LSL_Float = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLFloat;
using LSL_Integer = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLInteger;
using LSL_Key = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_List = OpenSim.Region.ScriptEngine.Shared.LSL_Types.list;
using LSL_Rotation = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Quaternion;
using LSL_String = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_Vector = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Vector3;
using PermissionMask = OpenSim.Framework.PermissionMask;
using PresenceInfo = OpenSim.Services.Interfaces.PresenceInfo;

namespace OpenSim.Region.ScriptEngine.Shared.Api
{
    public partial class LSL_Api : MarshalByRefObject, ILSL_Api, IScriptApi
    {
        public delegate void AssetRequestCallback(UUID assetID, AssetBase asset);


        /* particle system rules should be coming into this routine as doubles, that is
        rule[0] should be an integer from this list and rule[1] should be the arg
        for the same integer. wiki.secondlife.com has most of this mapping, but some
        came from http://www.caligari-designs.com/p4u2

        We iterate through the list for 'Count' elements, incrementing by two for each
        iteration and set the members of Primitive.ParticleSystem, one at a time.
        */

        public enum PrimitiveRule
        {
            PSYS_PART_FLAGS = 0,
            PSYS_PART_START_COLOR = 1,
            PSYS_PART_START_ALPHA = 2,
            PSYS_PART_END_COLOR = 3,
            PSYS_PART_END_ALPHA = 4,
            PSYS_PART_START_SCALE = 5,
            PSYS_PART_END_SCALE = 6,
            PSYS_PART_MAX_AGE = 7,
            PSYS_SRC_ACCEL = 8,
            PSYS_SRC_PATTERN = 9,
            PSYS_SRC_INNERANGLE = 10,
            PSYS_SRC_OUTERANGLE = 11,
            PSYS_SRC_TEXTURE = 12,
            PSYS_SRC_BURST_RATE = 13,
            PSYS_SRC_BURST_PART_COUNT = 15,
            PSYS_SRC_BURST_RADIUS = 16,
            PSYS_SRC_BURST_SPEED_MIN = 17,
            PSYS_SRC_BURST_SPEED_MAX = 18,
            PSYS_SRC_MAX_AGE = 19,
            PSYS_SRC_TARGET_KEY = 20,
            PSYS_SRC_OMEGA = 21,
            PSYS_SRC_ANGLE_BEGIN = 22,
            PSYS_SRC_ANGLE_END = 23,
            PSYS_PART_BLEND_FUNC_SOURCE = 24,
            PSYS_PART_BLEND_FUNC_DEST = 25,
            PSYS_PART_START_GLOW = 26,
            PSYS_PART_END_GLOW = 27
        }

        private const uint fullperms = (uint)PermissionMask.All; // no export for now

        private const string b64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        protected static List<CastRayCall> m_castRayCalls = new List<CastRayCall>();
        protected static Dictionary<ulong, FacetedMesh> m_cachedMeshes = new Dictionary<ulong, FacetedMesh>();

        private static readonly Dictionary<string, string> MovementAnimationsForLSL =
            new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase)
            {
                { "CROUCH", "Crouching" },
                { "CROUCHWALK", "CrouchWalking" },
                { "FALLDOWN", "Falling Down" },
                { "FLY", "Flying" },
                { "FLYSLOW", "FlyingSlow" },
                { "HOVER", "Hovering" },
                { "HOVER_UP", "Hovering Up" },
                { "HOVER_DOWN", "Hovering Down" },
                { "JUMP", "Jumping" },
                { "LAND", "Landing" },
                { "PREJUMP", "PreJumping" },
                { "RUN", "Running" },
                { "SIT", "Sitting" },
                { "SITGROUND", "Sitting on Ground" },
                { "STAND", "Standing" },
                { "STANDUP", "Standing Up" },
                { "STRIDE", "Striding" },
                { "SOFT_LAND", "Soft Landing" },
                { "TURNLEFT", "Turning Left" },
                { "TURNRIGHT", "Turning Right" },
                { "WALK", "Walking" }
            };

        //llHTTPRequest custom headers use control
        // true means fatal error,
        // false means ignore,
        // missing means allowed
        private static readonly Dictionary<string, bool> HttpForbiddenHeaders =
            new Dictionary<string, bool>(StringComparer.InvariantCultureIgnoreCase)
            {
                { "Accept", true },
                { "Accept-Charset", true },
                { "Accept-CH", false },
                { "Accept-CH-Lifetime", false },
                { "Access-Control-Request-Headers", false },
                { "Access-Control-Request-Method", false },
                { "Accept-Encoding", false },
                //{"Accept-Language", false},
                { "Accept-Patch", false }, // it is server side
                { "Accept-Post", false }, // it is server side
                { "Accept-Ranges", false }, // it is server side
                //{"Age", false},
                //{"Allow", false},
                //{"Authorization", false},
                { "Cache-Control", false },
                { "Connection", false },
                { "Content-Length", false },
                //{"Content-Encoding", false},
                //{"Content-Location", false},
                //{"Content-MD5", false},
                //{"Content-Range", false},
                { "Content-Type", true },
                { "Cookie", false },
                { "Cookie2", false },
                { "Date", false },
                { "Device-Memory", false },
                { "DTN", false },
                { "Early-Data", false },
                //{"ETag", false},
                { "Expect", false },
                //{"Expires", false},
                { "Feature-Policy", false },
                { "From", true },
                { "Host", true },
                { "Keep-Alive", false },
                { "If-Match", false },
                { "If-Modified-Since", false },
                { "If-None-Match", false },
                //{"If-Range", false},
                { "If-Unmodified-Since", false },
                //{"Last-Modified", false},
                //{"Location", false},
                { "Max-Forwards", false },
                { "Origin", false },
                { "Pragma", false },
                //{"Proxy-Authenticate", false},
                //{"Proxy-Authorization", false},
                //{"Range", false},
                { "Referer", true },
                //{"Retry-After", false},
                { "Server", false },
                { "Set-Cookie", false },
                { "Set-Cookie2", false },
                { "TE", true },
                { "Trailer", true },
                { "Transfer-Encoding", false },
                { "Upgrade", true },
                { "User-Agent", true },
                { "Vary", false },
                { "Via", true },
                { "Viewport-Width", false },
                { "Warning", false },
                { "Width", false },
                //{"WWW-Authenticate", false},

                { "X-Forwarded-For", false },
                { "X-Forwarded-Host", false },
                { "X-Forwarded-Proto", false },

                { "x-secondlife-shard", false },
                { "x-secondlife-object-name", false },
                { "x-secondlife-object-key", false },
                { "x-secondlife-region", false },
                { "x-secondlife-local-position", false },
                { "x-secondlife-local-velocity", false },
                { "x-secondlife-local-rotation", false },
                { "x-secondlife-owner-name", false },
                { "x-secondlife-owner-key", false }
            };

        private static readonly HashSet<string> HttpForbiddenInHeaders =
            new HashSet<string>(StringComparer.InvariantCultureIgnoreCase)
            {
                "x-secondlife-shard", "x-secondlife-object-name", "x-secondlife-object-key",
                "x-secondlife-region", "x-secondlife-local-position", "x-secondlife-local-velocity",
                "x-secondlife-local-rotation", "x-secondlife-owner-name", "x-secondlife-owner-key",
                "connection", "content-length", "from", "host", "proxy-authorization",
                "referer", "trailer", "transfer-encoding", "via", "authorization"
            };

        /// <summary>
        ///     Not fully implemented yet. Still to do:-
        ///     AGENT_BUSY
        ///     Remove as they are done
        /// </summary>
        private static readonly UUID busyAnimation = new UUID("efcf670c-2d18-8128-973a-034ebc806b67");

        //  <remarks>
        //  <para>
        //  The .NET definition of base 64 is:
        //  <list>
        //  <item>
        //  Significant: A-Z a-z 0-9 + -
        //  </item>
        //  <item>
        //  Whitespace: \t \n \r ' '
        //  </item>
        //  <item>
        //  Valueless: =
        //  </item>
        //  <item>
        //  End-of-string: \0 or '=='
        //  </item>
        //  </list>
        //  </para>
        //  <para>
        //  Each point in a base-64 string represents
        //  a 6 bit value. A 32-bit integer can be
        //  represented using 6 characters (with some
        //  redundancy).
        //  </para>
        //  <para>
        //  LSL requires a base64 string to be 8
        //  characters in length. LSL also uses '/'
        //  rather than '-' (MIME compliant).
        //  </para>
        //  <para>
        //  RFC 1341 used as a reference (as specified
        //  by the SecondLife Wiki).
        //  </para>
        //  <para>
        //  SL do not record any kind of exception for
        //  these functions, so the string to integer
        //  conversion returns '0' if an invalid
        //  character is encountered during conversion.
        //  </para>
        //  <para>
        //  References
        //  <list>
        //  <item>
        //  http://lslwiki.net/lslwiki/wakka.php?wakka=Base64
        //  </item>
        //  <item>
        //  </item>
        //  </list>
        //  </para>
        //  </remarks>

        //  <summary>
        //  Table for converting 6-bit integers into
        //  base-64 characters
        //  </summary>

        protected static readonly char[] i2ctable =
        {
            'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H',
            'I', 'J', 'K', 'L', 'M', 'N', 'O', 'P',
            'Q', 'R', 'S', 'T', 'U', 'V', 'W', 'X',
            'Y', 'Z',
            'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h',
            'i', 'j', 'k', 'l', 'm', 'n', 'o', 'p',
            'q', 'r', 's', 't', 'u', 'v', 'w', 'x',
            'y', 'z',
            '0', '1', '2', '3', '4', '5', '6', '7',
            '8', '9',
            '+', '/'
        };

        //  <summary>
        //  Table for converting base-64 characters
        //  into 6-bit integers.
        //  </summary>

        protected static readonly int[] c2itable =
        {
            -1, -1, -1, -1, -1, -1, -1, -1, // 0x
            -1, -1, -1, -1, -1, -1, -1, -1,
            -1, -1, -1, -1, -1, -1, -1, -1, // 1x
            -1, -1, -1, -1, -1, -1, -1, -1,
            -1, -1, -1, -1, -1, -1, -1, -1, // 2x
            -1, -1, -1, 63, -1, -1, -1, 64,
            53, 54, 55, 56, 57, 58, 59, 60, // 3x
            61, 62, -1, -1, -1, 0, -1, -1,
            -1, 1, 2, 3, 4, 5, 6, 7, // 4x
            8, 9, 10, 11, 12, 13, 14, 15,
            16, 17, 18, 19, 20, 21, 22, 23, // 5x
            24, 25, 26, -1, -1, -1, -1, -1,
            -1, 27, 28, 29, 30, 31, 32, 33, // 6x
            34, 35, 36, 37, 38, 39, 40, 41,
            42, 43, 44, 45, 46, 47, 48, 49, // 7x
            50, 51, 52, -1, -1, -1, -1, -1,
            -1, -1, -1, -1, -1, -1, -1, -1, // 8x
            -1, -1, -1, -1, -1, -1, -1, -1,
            -1, -1, -1, -1, -1, -1, -1, -1, // 9x
            -1, -1, -1, -1, -1, -1, -1, -1,
            -1, -1, -1, -1, -1, -1, -1, -1, // Ax
            -1, -1, -1, -1, -1, -1, -1, -1,
            -1, -1, -1, -1, -1, -1, -1, -1, // Bx
            -1, -1, -1, -1, -1, -1, -1, -1,
            -1, -1, -1, -1, -1, -1, -1, -1, // Cx
            -1, -1, -1, -1, -1, -1, -1, -1,
            -1, -1, -1, -1, -1, -1, -1, -1, // Dx
            -1, -1, -1, -1, -1, -1, -1, -1,
            -1, -1, -1, -1, -1, -1, -1, -1, // Ex
            -1, -1, -1, -1, -1, -1, -1, -1,
            -1, -1, -1, -1, -1, -1, -1, -1, // Fx
            -1, -1, -1, -1, -1, -1, -1, -1
        };

        protected int EMAIL_PAUSE_TIME = 20; // documented delay value for smtp.
        protected bool m_addStatsInGetBoundingBox;
        protected bool m_AllowGodFunctions;

        protected AsyncCommandManager m_AsyncCommands;
        protected bool m_automaticLinkPermission;

        protected float m_avatarHeightCorrection = 0.2f;
        protected DetailLevel m_avatarLodInCastRay = DetailLevel.Medium;
        protected bool m_debuggerSafe;
        protected bool m_detectExitsInCastRay;

        private bool m_disable_underground_movement = true;
        protected bool m_doAttachmentsInCastRay;
        protected IEmailModule m_emailModule;
        protected IEnvironmentModule m_envModule;
        protected float m_floatTolerance2InCastRay = 0.001f;

        protected float m_floatToleranceInCastRay = 0.00001f;

        protected string m_GetWallclockTimeZone = string.Empty; // Defaults to UTC
        protected SceneObjectPart m_host;
        protected string m_internalObjectHost = "lsl.opensim.local";

        /// <summary>
        ///     The item that hosts this script
        /// </summary>
        protected TaskInventoryItem m_item;

        protected float m_lABB1GrsX0 = -0.3875f;
        protected float m_lABB1GrsY0 = -0.5f;
        protected float m_lABB1GrsZ0 = -0.05f;
        protected float m_lABB1GrsZ1 = -0.375f;
        protected float m_lABB1SitX0 = -0.5875f;
        protected float m_lABB1SitY0 = -0.35f;
        protected float m_lABB1SitZ0 = -0.35f;
        protected float m_lABB1SitZ1 = -0.375f;

        //LSL Avatar Bounding Box (lABB), lower (1) and upper (2),
        //standing (Std), Groundsitting (Grs), Sitting (Sit),
        //along X, Y and Z axes, constants (0) and coefficients (1)
        protected float m_lABB1StdX0 = -0.275f;
        protected float m_lABB1StdY0 = -0.35f;
        protected float m_lABB1StdZ0 = -0.1f;
        protected float m_lABB1StdZ1 = -0.5f;
        protected float m_lABB2GrsX0 = 0.3875f;
        protected float m_lABB2GrsY0 = 0.5f;
        protected float m_lABB2GrsZ0 = 0.5f;
        protected float m_lABB2GrsZ1;
        protected float m_lABB2SitX0 = 0.1875f;
        protected float m_lABB2SitY0 = 0.35f;
        protected float m_lABB2SitZ0 = -0.25f;
        protected float m_lABB2SitZ1 = 0.25f;
        protected float m_lABB2StdX0 = 0.275f;
        protected float m_lABB2StdY0 = 0.35f;
        protected float m_lABB2StdZ0 = 0.1f;
        protected float m_lABB2StdZ1 = 0.5f;
        private DateTime m_lastSayShoutCheck;

        private int m_llRequestAgentDataCacheTimeout;

        private string m_lsl_shard = "OpenSim";
        private string m_lsl_user_agent = string.Empty;
        protected IMaterialsModule m_materialsModule;
        protected int m_maxHitsInCastRay = 16;
        protected int m_maxHitsPerObjectInCastRay = 16;
        protected int m_maxHitsPerPrimInCastRay = 16;
        protected DetailLevel m_meshLodInCastRay = DetailLevel.Highest;
        protected float m_MinTimerInterval = 0.5f;
        protected int m_msMaxInCastRay = 40;
        protected int m_msMinInCastRay = 2;
        protected int m_msPerAvatarInCastRay = 10;
        protected int m_msPerRegionInCastRay = 40;
        protected int m_msThrottleInCastRay = 200;
        protected int m_notecardLineReadCharsMax = 255;

        protected ExpiringCacheOS<UUID, PresenceInfo> m_PresenceInfoCache =
            new ExpiringCacheOS<UUID, PresenceInfo>(10000);

        protected DetailLevel m_primLodInCastRay = DetailLevel.Medium;

        protected float m_primSafetyCoeffX = 2.414214f;
        protected float m_primSafetyCoeffY = 2.414214f;
        protected float m_primSafetyCoeffZ = 1.618034f;
        protected float m_recoilScaleFactor;
        protected string m_regionName = string.Empty;
        protected bool m_restrictEmail;
        private int m_saydistance = 20;

//        protected Timer m_ShoutSayTimer;
        protected int m_SayShoutCount;
        protected float m_Script10mDistance = 10.0f;
        protected float m_Script10mDistanceSquare = 100.0f;
        protected int m_scriptConsoleChannel = 0;
        protected bool m_scriptConsoleChannelEnabled = false;
        protected float m_ScriptDelayFactor = 1.0f;

        protected IScriptEngine m_ScriptEngine;
        protected DetailLevel m_sculptLodInCastRay = DetailLevel.Medium;
        private int m_shoutdistance = 100;
        protected int m_sleepMsOnAddToLandBanList = 100;
        protected int m_sleepMsOnAddToLandPassList = 100;
        protected int m_sleepMsOnAdjustSoundVolume = 100;
        protected int m_sleepMsOnClearLinkMedia = 1000;
        protected int m_sleepMsOnClearPrimMedia = 1000;
        protected int m_sleepMsOnCloseRemoteDataChannel = 1000;
        protected int m_sleepMsOnCreateLink = 1000;
        protected int m_sleepMsOnDialog = 1000;
        protected int m_sleepMsOnEjectFromLand = 1000;
        protected int m_sleepMsOnEmail = 30000;
        protected int m_sleepMsOnGetLinkMedia = 1000;
        protected int m_sleepMsOnGetNotecardLine = 100;
        protected int m_sleepMsOnGetNumberOfNotecardLines = 100;
        protected int m_sleepMsOnGetParcelPrimOwners = 2000;
        protected int m_sleepMsOnGetPrimMediaParams = 1000;
        protected int m_sleepMsOnGiveInventory = 3000;
        protected int m_sleepMsOnInstantMessage = 2000;
        protected int m_sleepMsOnLoadURL = 1000;
        protected int m_sleepMsOnMakeExplosion = 100;
        protected int m_sleepMsOnMakeFire = 100;
        protected int m_sleepMsOnMakeFountain = 100;
        protected int m_sleepMsOnMakeSmoke = 100;
        protected int m_sleepMsOnMapDestination = 1000;
        protected int m_sleepMsOnModPow = 1000;
        protected int m_sleepMsOnOffsetTexture = 200;
        protected int m_sleepMsOnOpenRemoteDataChannel = 1000;
        protected int m_sleepMsOnParcelMediaCommandList = 2000;
        protected int m_sleepMsOnParcelMediaQuery = 2000;
        protected int m_sleepMsOnPreloadSound = 1000;
        protected int m_sleepMsOnRefreshPrimURL = 20000;
        protected int m_sleepMsOnRemoteDataReply = 3000;
        protected int m_sleepMsOnRemoteLoadScript = 3000;
        protected int m_sleepMsOnRemoteLoadScriptPin = 3000;
        protected int m_sleepMsOnRemoveFromLandBanList = 100;
        protected int m_sleepMsOnRemoveFromLandPassList = 100;
        protected int m_sleepMsOnRequestAgentData = 100;
        protected int m_sleepMsOnRequestInventoryData = 1000;
        protected int m_sleepMsOnRequestSimulatorData = 1000;
        protected int m_sleepMsOnResetLandBanList = 100;
        protected int m_sleepMsOnResetLandPassList = 100;
        protected int m_sleepMsOnRezAtRoot = 100;
        protected int m_sleepMsOnRotateTexture = 200;
        protected int m_sleepMsOnScaleTexture = 200;
        protected int m_sleepMsOnSendRemoteData = 3000;
        protected int m_sleepMsOnSetDamage = 5000;
        protected int m_sleepMsOnSetLinkMedia = 1000;
        protected int m_sleepMsOnSetLinkPrimitiveParams = 200;
        protected int m_sleepMsOnSetLinkTexture = 200;
        protected int m_sleepMsOnSetLocalRot = 200;
        protected int m_sleepMsOnSetParcelMusicURL = 2000;
        protected int m_sleepMsOnSetPos = 200;
        protected int m_sleepMsOnSetPrimitiveParams = 200;
        protected int m_sleepMsOnSetPrimMediaParams = 1000;
        protected int m_sleepMsOnSetPrimURL = 2000;
        protected int m_sleepMsOnSetRot = 200;
        protected int m_sleepMsOnSetTexture = 200;
        protected int m_sleepMsOnTextBox = 1000;
        protected int m_sleepMsOnXorBase64Strings = 300;
        protected ISoundModule m_SoundModule;
        protected double m_timer = Util.GetTimeStampMS();
        protected IMessageTransferModule m_TransferModule;
        protected IUrlModule m_UrlModule;
        protected bool m_useMeshCacheInCastRay = true;
        protected IUserAccountService m_userAccountService;
        protected bool m_useSimpleBoxesInGetBoundingBox;
        protected bool m_waitingForScriptAnswer;

        private int m_whisperdistance = 10;

        protected UUID RegionScopeID = UUID.Zero;

        protected bool throwErrorOnNotImplemented = false;

        public int LlRequestAgentDataCacheTimeoutMs
        {
            get => 1000 * m_llRequestAgentDataCacheTimeout;
            set => m_llRequestAgentDataCacheTimeout = value / 1000;
        }

        /// <summary>
        ///     Check for co-operative termination.
        /// </summary>
        /// <param name='delay'>If called with 0, then just the check is performed with no wait.</param>

        public Scene World => m_ScriptEngine.World;

        [DebuggerNonUserCode]
        public void state(string newState)
        {
            m_ScriptEngine.SetState(m_item.ItemID, newState);
        }


        public void SetPrimitiveParamsEx(LSL_Key prim, LSL_List rules, string originFunc)
        {
            if (!UUID.TryParse(prim, out var id) || id.IsZero())
                return;

            var obj = World.GetSceneObjectPart(id);
            if (obj == null)
                return;

            var sog = obj.ParentGroup;
            if (sog == null || sog.IsDeleted)
                return;

            var objRoot = sog.RootPart;
            if (objRoot == null || objRoot.OwnerID.NotEqual(m_host.OwnerID) ||
                (objRoot.OwnerMask & (uint)PermissionMask.Modify) == 0)
                return;

            uint rulesParsed = 0;
            var remaining = SetPrimParams(obj, rules, originFunc, ref rulesParsed);

            while (remaining.Length > 2)
            {
                int linknumber;
                try
                {
                    linknumber = remaining.GetLSLIntegerItem(0);
                }
                catch (InvalidCastException)
                {
                    Error(originFunc,
                        string.Format("Error running rule #{0} -> PRIM_LINK_TARGET parameter must be integer",
                            rulesParsed));
                    return;
                }

                var entities = GetLinkEntities(obj, linknumber);
                if (entities.Count == 0)
                    break;

                rules = remaining.GetSublist(1, -1);
                foreach (var entity in entities)
                    if (entity is SceneObjectPart)
                        remaining = SetPrimParams((SceneObjectPart)entity, rules, originFunc, ref rulesParsed);
                    else
                        remaining = SetAgentParams((ScenePresence)entity, rules, originFunc, ref rulesParsed);
            }
        }

        public LSL_List GetPrimitiveParamsEx(LSL_Key prim, LSL_List rules)
        {
            var result = new LSL_List();

            if (!UUID.TryParse(prim, out var id))
                return result;

            var obj = World.GetSceneObjectPart(id);
            if (obj == null)
                return result;

            var sog = obj.ParentGroup;
            if (sog == null || sog.IsDeleted)
                return result;

            var objRoot = sog.RootPart;
            if (objRoot == null || objRoot.OwnerID.NotEqual(m_host.OwnerID) ||
                (objRoot.OwnerMask & (uint)PermissionMask.Modify) == 0)
                return result;

            var remaining = GetPrimParams(obj, rules, ref result);

            while (remaining.Length > 1)
            {
                int linknumber;
                try
                {
                    linknumber = remaining.GetLSLIntegerItem(0);
                }
                catch (InvalidCastException)
                {
                    Error("", "Error PRIM_LINK_TARGET: parameter must be integer");
                    return result;
                }

                var entities = GetLinkEntities(obj, linknumber);
                if (entities.Count == 0)
                    break;

                rules = remaining.GetSublist(1, -1);
                foreach (var entity in entities)
                    if (entity is SceneObjectPart)
                        remaining = GetPrimParams((SceneObjectPart)entity, rules, ref result);
                    else
                        remaining = GetPrimParams((ScenePresence)entity, rules, ref result);
            }

            return result;
        }

        public void Initialize(IScriptEngine scriptEngine, SceneObjectPart host, TaskInventoryItem item)
        {
            m_lastSayShoutCheck = DateTime.UtcNow;

            m_ScriptEngine = scriptEngine;
            m_host = host;
            m_item = item;
            m_debuggerSafe = m_ScriptEngine.Config.GetBoolean("DebuggerSafe", false);

            LoadConfig();

            m_TransferModule = m_ScriptEngine.World.RequestModuleInterface<IMessageTransferModule>();
            m_UrlModule = m_ScriptEngine.World.RequestModuleInterface<IUrlModule>();
            m_SoundModule = m_ScriptEngine.World.RequestModuleInterface<ISoundModule>();
            m_materialsModule = m_ScriptEngine.World.RequestModuleInterface<IMaterialsModule>();

            m_emailModule = m_ScriptEngine.World.RequestModuleInterface<IEmailModule>();
            m_envModule = m_ScriptEngine.World.RequestModuleInterface<IEnvironmentModule>();

            m_AsyncCommands = new AsyncCommandManager(m_ScriptEngine);
            m_userAccountService = World.UserAccountService;
            if (World.RegionInfo != null)
            {
                RegionScopeID = World.RegionInfo.ScopeID;
                m_regionName = World.RegionInfo.RegionName;
            }
        }

        /// <summary>
        ///     Load configuration items that affect script, object and run-time behavior. */
        /// </summary>
        private void LoadConfig()
        {
            LlRequestAgentDataCacheTimeoutMs = 20000;

            var seConfig = m_ScriptEngine.Config;

            if (seConfig != null)
            {
                var scriptDistanceFactor = seConfig.GetFloat("ScriptDistanceLimitFactor", 1.0f);
                m_Script10mDistance = 10.0f * scriptDistanceFactor;
                m_Script10mDistanceSquare = m_Script10mDistance * m_Script10mDistance;

                m_ScriptDelayFactor = seConfig.GetFloat("ScriptDelayFactor", m_ScriptDelayFactor);
                m_MinTimerInterval = seConfig.GetFloat("MinTimerInterval", m_MinTimerInterval);
                m_automaticLinkPermission = seConfig.GetBoolean("AutomaticLinkPermission", m_automaticLinkPermission);
                m_notecardLineReadCharsMax = seConfig.GetInt("NotecardLineReadCharsMax", m_notecardLineReadCharsMax);

                m_GetWallclockTimeZone = seConfig.GetString("GetWallclockTimeZone", m_GetWallclockTimeZone);

                // Rezzing an object with a velocity can create recoil. This feature seems to have been
                //    removed from recent versions of SL. The code computes recoil (vel*mass) and scales
                //    it by this factor. May be zero to turn off recoil all together.
                m_recoilScaleFactor = seConfig.GetFloat("RecoilScaleFactor", m_recoilScaleFactor);
                m_AllowGodFunctions = seConfig.GetBoolean("AllowGodFunctions", false);

                m_disable_underground_movement = seConfig.GetBoolean("DisableUndergroundMovement", true);
            }

            if (m_notecardLineReadCharsMax > 65535)
                m_notecardLineReadCharsMax = 65535;

            // load limits for particular subsystems.
            var seConfigSource = m_ScriptEngine.ConfigSource;

            if (seConfigSource != null)
            {
                var netConfig = seConfigSource.Configs["Network"];
                if (netConfig != null)
                {
                    m_lsl_shard = netConfig.GetString("shard", m_lsl_shard);
                    m_lsl_user_agent = netConfig.GetString("user_agent", m_lsl_user_agent);
                }

                var lslConfig = seConfigSource.Configs["LL-Functions"];
                if (lslConfig != null)
                {
                    m_restrictEmail = lslConfig.GetBoolean("RestrictEmail", m_restrictEmail);
                    m_avatarHeightCorrection = lslConfig.GetFloat("AvatarHeightCorrection", m_avatarHeightCorrection);
                    m_useSimpleBoxesInGetBoundingBox = lslConfig.GetBoolean("UseSimpleBoxesInGetBoundingBox",
                        m_useSimpleBoxesInGetBoundingBox);
                    m_addStatsInGetBoundingBox =
                        lslConfig.GetBoolean("AddStatsInGetBoundingBox", m_addStatsInGetBoundingBox);
                    m_lABB1StdX0 = lslConfig.GetFloat("LowerAvatarBoundingBoxStandingXconst", m_lABB1StdX0);
                    m_lABB2StdX0 = lslConfig.GetFloat("UpperAvatarBoundingBoxStandingXconst", m_lABB2StdX0);
                    m_lABB1StdY0 = lslConfig.GetFloat("LowerAvatarBoundingBoxStandingYconst", m_lABB1StdY0);
                    m_lABB2StdY0 = lslConfig.GetFloat("UpperAvatarBoundingBoxStandingYconst", m_lABB2StdY0);
                    m_lABB1StdZ0 = lslConfig.GetFloat("LowerAvatarBoundingBoxStandingZconst", m_lABB1StdZ0);
                    m_lABB1StdZ1 = lslConfig.GetFloat("LowerAvatarBoundingBoxStandingZcoeff", m_lABB1StdZ1);
                    m_lABB2StdZ0 = lslConfig.GetFloat("UpperAvatarBoundingBoxStandingZconst", m_lABB2StdZ0);
                    m_lABB2StdZ1 = lslConfig.GetFloat("UpperAvatarBoundingBoxStandingZcoeff", m_lABB2StdZ1);
                    m_lABB1GrsX0 = lslConfig.GetFloat("LowerAvatarBoundingBoxGroundsittingXconst", m_lABB1GrsX0);
                    m_lABB2GrsX0 = lslConfig.GetFloat("UpperAvatarBoundingBoxGroundsittingXconst", m_lABB2GrsX0);
                    m_lABB1GrsY0 = lslConfig.GetFloat("LowerAvatarBoundingBoxGroundsittingYconst", m_lABB1GrsY0);
                    m_lABB2GrsY0 = lslConfig.GetFloat("UpperAvatarBoundingBoxGroundsittingYconst", m_lABB2GrsY0);
                    m_lABB1GrsZ0 = lslConfig.GetFloat("LowerAvatarBoundingBoxGroundsittingZconst", m_lABB1GrsZ0);
                    m_lABB1GrsZ1 = lslConfig.GetFloat("LowerAvatarBoundingBoxGroundsittingZcoeff", m_lABB1GrsZ1);
                    m_lABB2GrsZ0 = lslConfig.GetFloat("UpperAvatarBoundingBoxGroundsittingZconst", m_lABB2GrsZ0);
                    m_lABB2GrsZ1 = lslConfig.GetFloat("UpperAvatarBoundingBoxGroundsittingZcoeff", m_lABB2GrsZ1);
                    m_lABB1SitX0 = lslConfig.GetFloat("LowerAvatarBoundingBoxSittingXconst", m_lABB1SitX0);
                    m_lABB2SitX0 = lslConfig.GetFloat("UpperAvatarBoundingBoxSittingXconst", m_lABB2SitX0);
                    m_lABB1SitY0 = lslConfig.GetFloat("LowerAvatarBoundingBoxSittingYconst", m_lABB1SitY0);
                    m_lABB2SitY0 = lslConfig.GetFloat("UpperAvatarBoundingBoxSittingYconst", m_lABB2SitY0);
                    m_lABB1SitZ0 = lslConfig.GetFloat("LowerAvatarBoundingBoxSittingZconst", m_lABB1SitZ0);
                    m_lABB1SitZ1 = lslConfig.GetFloat("LowerAvatarBoundingBoxSittingZcoeff", m_lABB1SitZ1);
                    m_lABB2SitZ0 = lslConfig.GetFloat("UpperAvatarBoundingBoxSittingZconst", m_lABB2SitZ0);
                    m_lABB2SitZ1 = lslConfig.GetFloat("UpperAvatarBoundingBoxSittingZcoeff", m_lABB2SitZ1);
                    m_primSafetyCoeffX = lslConfig.GetFloat("PrimBoundingBoxSafetyCoefficientX", m_primSafetyCoeffX);
                    m_primSafetyCoeffY = lslConfig.GetFloat("PrimBoundingBoxSafetyCoefficientY", m_primSafetyCoeffY);
                    m_primSafetyCoeffZ = lslConfig.GetFloat("PrimBoundingBoxSafetyCoefficientZ", m_primSafetyCoeffZ);
                    m_floatToleranceInCastRay =
                        lslConfig.GetFloat("FloatToleranceInLlCastRay", m_floatToleranceInCastRay);
                    m_floatTolerance2InCastRay =
                        lslConfig.GetFloat("FloatTolerance2InLlCastRay", m_floatTolerance2InCastRay);
                    m_primLodInCastRay =
                        (DetailLevel)lslConfig.GetInt("PrimDetailLevelInLlCastRay", (int)m_primLodInCastRay);
                    m_sculptLodInCastRay =
                        (DetailLevel)lslConfig.GetInt("SculptDetailLevelInLlCastRay", (int)m_sculptLodInCastRay);
                    m_meshLodInCastRay =
                        (DetailLevel)lslConfig.GetInt("MeshDetailLevelInLlCastRay", (int)m_meshLodInCastRay);
                    m_avatarLodInCastRay =
                        (DetailLevel)lslConfig.GetInt("AvatarDetailLevelInLlCastRay", (int)m_avatarLodInCastRay);
                    m_maxHitsInCastRay = lslConfig.GetInt("MaxHitsInLlCastRay", m_maxHitsInCastRay);
                    m_maxHitsPerPrimInCastRay =
                        lslConfig.GetInt("MaxHitsPerPrimInLlCastRay", m_maxHitsPerPrimInCastRay);
                    m_maxHitsPerObjectInCastRay =
                        lslConfig.GetInt("MaxHitsPerObjectInLlCastRay", m_maxHitsPerObjectInCastRay);
                    m_detectExitsInCastRay = lslConfig.GetBoolean("DetectExitHitsInLlCastRay", m_detectExitsInCastRay);
                    m_doAttachmentsInCastRay =
                        lslConfig.GetBoolean("DoAttachmentsInLlCastRay", m_doAttachmentsInCastRay);
                    m_msThrottleInCastRay = lslConfig.GetInt("ThrottleTimeInMsInLlCastRay", m_msThrottleInCastRay);
                    m_msPerRegionInCastRay =
                        lslConfig.GetInt("AvailableTimeInMsPerRegionInLlCastRay", m_msPerRegionInCastRay);
                    m_msPerAvatarInCastRay =
                        lslConfig.GetInt("AvailableTimeInMsPerAvatarInLlCastRay", m_msPerAvatarInCastRay);
                    m_msMinInCastRay = lslConfig.GetInt("RequiredAvailableTimeInMsInLlCastRay", m_msMinInCastRay);
                    m_msMaxInCastRay = lslConfig.GetInt("MaximumAvailableTimeInMsInLlCastRay", m_msMaxInCastRay);
                    m_useMeshCacheInCastRay = lslConfig.GetBoolean("UseMeshCacheInLlCastRay", m_useMeshCacheInCastRay);
                }

                var smtpConfig = seConfigSource.Configs["SMTP"];
                if (smtpConfig != null)
                {
                    // there's an smtp config, so load in the snooze time.
                    EMAIL_PAUSE_TIME = smtpConfig.GetInt("email_pause_time", EMAIL_PAUSE_TIME);

                    m_internalObjectHost = smtpConfig.GetString("internal_object_host", m_internalObjectHost);
                }

                var chatConfig = seConfigSource.Configs["SMTP"];
                if (chatConfig != null)
                {
                    m_whisperdistance = chatConfig.GetInt("whisper_distance", m_whisperdistance);
                    m_saydistance = chatConfig.GetInt("say_distance", m_saydistance);
                    m_shoutdistance = chatConfig.GetInt("shout_distance", m_shoutdistance);
                }
            }

            m_sleepMsOnEmail = EMAIL_PAUSE_TIME * 1000;
        }

        public override object InitializeLifetimeService()
        {
            var lease = (ILease)base.InitializeLifetimeService();

            if (lease.CurrentState == LeaseState.Initial) lease.InitialLeaseTime = TimeSpan.FromMinutes(0);
            //                lease.RenewOnCallTime = TimeSpan.FromSeconds(10.0);
            //                lease.SponsorshipTimeout = TimeSpan.FromMinutes(1.0);
            return lease;
        }

        protected SceneObjectPart MonitoringObject()
        {
            var m = m_host.ParentGroup.MonitoringObject;
            if (m.IsZero())
                return null;

            var p = m_ScriptEngine.World.GetSceneObjectPart(m);
            if (p == null)
                m_host.ParentGroup.MonitoringObject = UUID.Zero;

            return p;
        }

        protected virtual void ScriptSleep(int delay)
        {
            delay = (int)(delay * m_ScriptDelayFactor);
            if (delay < 10)
                return;

            Sleep(delay);
        }

        protected virtual void Sleep(int delay)
        {
            if (m_item == null) // Some unit tests don't set this
                Thread.Sleep(delay);
            else
                m_ScriptEngine.SleepScript(m_item.ItemID, delay);
        }


        public List<ScenePresence> GetLinkAvatars(int linkType)
        {
            if (m_host == null)
                return new List<ScenePresence>();

            return GetLinkAvatars(linkType, m_host.ParentGroup);
        }

        public List<ScenePresence> GetLinkAvatars(int linkType, SceneObjectGroup sog)
        {
            var ret = new List<ScenePresence>();
            if (sog == null || sog.IsDeleted)
                return ret;

            var avs = sog.GetSittingAvatars();
            switch (linkType)
            {
                case ScriptBaseClass.LINK_SET:
                    return avs;

                case ScriptBaseClass.LINK_ROOT:
                    return ret;

                case ScriptBaseClass.LINK_ALL_OTHERS:
                    return avs;

                case ScriptBaseClass.LINK_ALL_CHILDREN:
                    return avs;

                case ScriptBaseClass.LINK_THIS:
                    return ret;

                default:
                    if (linkType < 0)
                        return ret;

                    var partCount = sog.GetPartCount();

                    linkType -= partCount;
                    if (linkType <= 0) return ret;

                    if (linkType > avs.Count) return ret;

                    ret.Add(avs[linkType - 1]);
                    return ret;
            }
        }

        /// <summary>
        ///     Get a given link entity from a linkset (linked objects and any sitting avatars).
        /// </summary>
        /// <remarks>
        ///     If there are any ScenePresence's in the linkset (i.e. because they are sat upon one of the prims), then
        ///     these are counted as extra entities that correspond to linknums beyond the number of prims in the linkset.
        ///     The ScenePresences receive linknums in the order in which they sat.
        /// </remarks>
        /// <returns>
        ///     The link entity.  null if not found.
        /// </returns>
        /// <param name='part'></param>
        /// <param name='linknum'>
        ///     Can be either a non-negative integer or ScriptBaseClass.LINK_THIS (-4).
        ///     If ScriptBaseClass.LINK_THIS then the entity containing the script is returned.
        ///     If the linkset has one entity and a linknum of zero is given, then the single entity is returned.  If any
        ///     positive integer is given in this case then null is returned.
        ///     If the linkset has more than one entity and a linknum greater than zero but equal to or less than the number
        ///     of entities, then the entity which corresponds to that linknum is returned.
        ///     Otherwise, if a positive linknum is given which is greater than the number of entities in the linkset, then
        ///     null is returned.
        /// </param>
        public ISceneEntity GetLinkEntity(SceneObjectPart part, int linknum)
        {
            if (linknum < 0)
            {
                if (linknum == ScriptBaseClass.LINK_THIS)
                    return part;
                return null;
            }

            var actualPrimCount = part.ParentGroup.PrimCount;
            var sittingAvatars = part.ParentGroup.GetSittingAvatars();
            var adjustedPrimCount = actualPrimCount + sittingAvatars.Count;

            // Special case for a single prim.  In this case the linknum is zero.  However, this will not match a single
            // prim that has any avatars sat upon it (in which case the root prim is link 1).
            if (linknum == 0)
            {
                if (actualPrimCount == 1 && sittingAvatars.Count == 0)
                    return part;

                return null;
            }
            // Special case to handle a single prim with sitting avatars.  GetLinkPart() would only match zero but
            // here we must match 1 (ScriptBaseClass.LINK_ROOT).

            if (linknum == ScriptBaseClass.LINK_ROOT && actualPrimCount == 1)
            {
                if (sittingAvatars.Count > 0)
                    return part.ParentGroup.RootPart;
                return null;
            }

            if (linknum <= adjustedPrimCount)
            {
                if (linknum <= actualPrimCount)
                    return part.ParentGroup.GetLinkNumPart(linknum);
                return sittingAvatars[linknum - actualPrimCount - 1];
            }

            return null;
        }

        public List<SceneObjectPart> GetLinkParts(int linkType)
        {
            return GetLinkParts(m_host, linkType);
        }

        public static List<SceneObjectPart> GetLinkParts(SceneObjectPart part, int linkType)
        {
            var ret = new List<SceneObjectPart>();
            if (part == null || part.ParentGroup == null || part.ParentGroup.IsDeleted)
                return ret;

            switch (linkType)
            {
                case ScriptBaseClass.LINK_SET:
                    return new List<SceneObjectPart>(part.ParentGroup.Parts);

                case ScriptBaseClass.LINK_ROOT:
                    ret.Add(part.ParentGroup.RootPart);
                    return ret;

                case ScriptBaseClass.LINK_ALL_OTHERS:
                    ret = new List<SceneObjectPart>(part.ParentGroup.Parts);

                    if (ret.Contains(part))
                        ret.Remove(part);

                    return ret;

                case ScriptBaseClass.LINK_ALL_CHILDREN:
                    ret = new List<SceneObjectPart>(part.ParentGroup.Parts);

                    if (ret.Contains(part.ParentGroup.RootPart))
                        ret.Remove(part.ParentGroup.RootPart);
                    return ret;

                case ScriptBaseClass.LINK_THIS:
                    ret.Add(part);
                    return ret;

                default:
                    if (linkType < 0)
                        return ret;

                    var target = part.ParentGroup.GetLinkNumPart(linkType);
                    if (target == null)
                        return ret;
                    ret.Add(target);
                    return ret;
            }
        }

        public List<ISceneEntity> GetLinkEntities(int linkType)
        {
            return GetLinkEntities(m_host, linkType);
        }

        public List<ISceneEntity> GetLinkEntities(SceneObjectPart part, int linkType)
        {
            List<ISceneEntity> ret;

            switch (linkType)
            {
                case ScriptBaseClass.LINK_SET:
                    return new List<ISceneEntity>(part.ParentGroup.Parts);

                case ScriptBaseClass.LINK_ROOT:
                    return new List<ISceneEntity> { part.ParentGroup.RootPart };

                case ScriptBaseClass.LINK_ALL_OTHERS:
                    ret = new List<ISceneEntity>(part.ParentGroup.Parts);

                    if (ret.Contains(part))
                        ret.Remove(part);

                    return ret;

                case ScriptBaseClass.LINK_ALL_CHILDREN:
                    ret = new List<ISceneEntity>(part.ParentGroup.Parts);

                    if (ret.Contains(part.ParentGroup.RootPart))
                        ret.Remove(part.ParentGroup.RootPart);

                    var avs = part.ParentGroup.GetSittingAvatars();
                    if (avs != null && avs.Count > 0)
                        ret.AddRange(avs);

                    return ret;

                case ScriptBaseClass.LINK_THIS:
                    return new List<ISceneEntity> { part };

                default:
                    if (linkType < 0)
                        return new List<ISceneEntity>();

                    var target = GetLinkEntity(part, linkType);
                    if (target == null)
                        return new List<ISceneEntity>();

                    return new List<ISceneEntity> { target };
            }
        }

        public string resolveName(UUID objecUUID)
        {
            // try avatar username surname
            var account = m_userAccountService.GetUserAccount(RegionScopeID, objecUUID);
            if (account != null)
            {
                var avatarname = account.Name;
                return avatarname;
            }

            // try an scene object
            var SOP = World.GetSceneObjectPart(objecUUID);
            if (SOP != null)
            {
                var objectname = SOP.Name;
                return objectname;
            }

            World.Entities.TryGetValue(objecUUID, out var SensedObject);

            if (SensedObject == null)
            {
                var groups = World.RequestModuleInterface<IGroupsModule>();
                if (groups != null)
                {
                    var gr = groups.GetGroupRecord(objecUUID);
                    if (gr != null)
                        return gr.GroupName;
                }

                return string.Empty;
            }

            return SensedObject.Name;
        }


        private bool IsPhysical()
        {
            return (m_host.GetEffectiveObjectFlags() & (uint)PrimFlags.Physics) == (uint)PrimFlags.Physics;
        }

        protected void SetScale(SceneObjectPart part, LSL_Vector scale)
        {
            // TODO: this needs to trigger a persistance save as well
            if (part == null || part.ParentGroup.IsDeleted)
                return;

            // First we need to check whether or not we need to clamp the size of a physics-enabled prim
            var pa = part.ParentGroup.RootPart.PhysActor;
            if (pa != null && pa.IsPhysical)
            {
                scale.x = Math.Max(World.m_minPhys, Math.Min(World.m_maxPhys, scale.x));
                scale.y = Math.Max(World.m_minPhys, Math.Min(World.m_maxPhys, scale.y));
                scale.z = Math.Max(World.m_minPhys, Math.Min(World.m_maxPhys, scale.z));
            }
            else
            {
                // If not physical, then we clamp the scale to the non-physical min/max
                scale.x = Math.Max(World.m_minNonphys, Math.Min(World.m_maxNonphys, scale.x));
                scale.y = Math.Max(World.m_minNonphys, Math.Min(World.m_maxNonphys, scale.y));
                scale.z = Math.Max(World.m_minNonphys, Math.Min(World.m_maxNonphys, scale.z));
            }

            var tmp = part.Scale;
            tmp.X = (float)scale.x;
            tmp.Y = (float)scale.y;
            tmp.Z = (float)scale.z;
            part.Scale = tmp;
            part.ParentGroup.HasGroupChanged = true;
            part.SendFullUpdateToAllClients();
        }

        protected void SetColor(SceneObjectPart part, LSL_Vector color, int face)
        {
            if (part == null || part.ParentGroup == null || part.ParentGroup.IsDeleted)
                return;

            m_host.SetFaceColorAlpha(face, color, null);
        }

        public void SetTexGen(SceneObjectPart part, int face, int style)
        {
            if (part == null || part.ParentGroup == null || part.ParentGroup.IsDeleted)
                return;

            var tex = part.Shape.Textures;
            MappingType textype;
            textype = MappingType.Default;
            if (style == ScriptBaseClass.PRIM_TEXGEN_PLANAR)
                textype = MappingType.Planar;

            var nsides = GetNumberOfSides(part);

            if (face >= 0 && face < nsides)
            {
                tex.CreateFace((uint)face);
                tex.FaceTextures[face].TexMapType = textype;
                part.UpdateTextureEntry(tex);
                return;
            }

            if (face == ScriptBaseClass.ALL_SIDES)
            {
                for (uint i = 0; i < nsides; i++)
                    if (tex.FaceTextures[i] != null)
                        tex.FaceTextures[i].TexMapType = textype;
                tex.DefaultTexture.TexMapType = textype;
                part.UpdateTextureEntry(tex);
            }
        }

        public void SetGlow(SceneObjectPart part, int face, float glow)
        {
            if (part == null || part.ParentGroup == null || part.ParentGroup.IsDeleted)
                return;

            var tex = part.Shape.Textures;
            var nsides = GetNumberOfSides(part);

            if (face >= 0 && face < nsides)
            {
                tex.CreateFace((uint)face);
                tex.FaceTextures[face].Glow = glow;
                part.UpdateTextureEntry(tex);
                return;
            }

            if (face == ScriptBaseClass.ALL_SIDES)
            {
                for (uint i = 0; i < nsides; i++)
                    if (tex.FaceTextures[i] != null)
                        tex.FaceTextures[i].Glow = glow;
                tex.DefaultTexture.Glow = glow;
                part.UpdateTextureEntry(tex);
            }
        }

        public void SetShiny(SceneObjectPart part, int face, int shiny, Bumpiness bump)
        {
            if (part == null || part.ParentGroup == null || part.ParentGroup.IsDeleted)
                return;

            var sval = new Shininess();

            switch (shiny)
            {
                case 0:
                    sval = Shininess.None;
                    break;
                case 1:
                    sval = Shininess.Low;
                    break;
                case 2:
                    sval = Shininess.Medium;
                    break;
                case 3:
                    sval = Shininess.High;
                    break;
                default:
                    sval = Shininess.None;
                    break;
            }

            var nsides = GetNumberOfSides(part);

            var tex = part.Shape.Textures;
            if (face >= 0 && face < nsides)
            {
                tex.CreateFace((uint)face);
                tex.FaceTextures[face].Shiny = sval;
                tex.FaceTextures[face].Bump = bump;
                part.UpdateTextureEntry(tex);
                return;
            }

            if (face == ScriptBaseClass.ALL_SIDES)
            {
                for (uint i = 0; i < nsides; i++)
                    if (tex.FaceTextures[i] != null)
                    {
                        tex.FaceTextures[i].Shiny = sval;
                        tex.FaceTextures[i].Bump = bump;
                    }

                tex.DefaultTexture.Shiny = sval;
                tex.DefaultTexture.Bump = bump;
                part.UpdateTextureEntry(tex);
            }
        }

        public void SetFullBright(SceneObjectPart part, int face, bool bright)
        {
            if (part == null || part.ParentGroup == null || part.ParentGroup.IsDeleted)
                return;

            var nsides = GetNumberOfSides(part);
            var tex = part.Shape.Textures;
            if (face >= 0 && face < nsides)
            {
                tex.CreateFace((uint)face);
                tex.FaceTextures[face].Fullbright = bright;
                part.UpdateTextureEntry(tex);
                return;
            }

            if (face == ScriptBaseClass.ALL_SIDES)
            {
                tex.DefaultTexture.Fullbright = bright;
                for (uint i = 0; i < nsides; i++)
                    if (tex.FaceTextures[i] != null)
                        tex.FaceTextures[i].Fullbright = bright;
                part.UpdateTextureEntry(tex);
            }
        }

        protected LSL_Float GetAlpha(SceneObjectPart part, int face)
        {
            var tex = part.Shape.Textures;
            var nsides = GetNumberOfSides(part);
            if (face == ScriptBaseClass.ALL_SIDES)
            {
                int i;
                var sum = 0.0;
                for (i = 0; i < nsides; i++)
                    sum += tex.GetFace((uint)i).RGBA.A;
                return sum;
            }

            if (face >= 0 && face < nsides) return (double)tex.GetFace((uint)face).RGBA.A;
            return 0.0;
        }

        protected void SetAlpha(SceneObjectPart part, double alpha, int face)
        {
            if (part == null || part.ParentGroup == null || part.ParentGroup.IsDeleted)
                return;

            var tex = part.Shape.Textures;
            var nsides = GetNumberOfSides(part);
            Color4 texcolor;

            if (face >= 0 && face < nsides)
            {
                texcolor = tex.CreateFace((uint)face).RGBA;
                texcolor.A = Utils.Clamp((float)alpha, 0.0f, 1.0f);
                tex.FaceTextures[face].RGBA = texcolor;
                part.UpdateTextureEntry(tex);
                return;
            }

            if (face == ScriptBaseClass.ALL_SIDES)
            {
                for (var i = 0; i < nsides; i++)
                    if (tex.FaceTextures[i] != null)
                    {
                        texcolor = tex.FaceTextures[i].RGBA;
                        texcolor.A = Utils.Clamp((float)alpha, 0.0f, 1.0f);
                        tex.FaceTextures[i].RGBA = texcolor;
                    }

                // In some cases, the default texture can be null, eg when every face
                // has a unique texture
                if (tex.DefaultTexture != null)
                {
                    texcolor = tex.DefaultTexture.RGBA;
                    texcolor.A = Utils.Clamp((float)alpha, 0.0f, 1.0f);
                    tex.DefaultTexture.RGBA = texcolor;
                }

                part.UpdateTextureEntry(tex);
            }
        }

        /// <summary>
        ///     Set flexi parameters of a part.
        ///     FIXME: Much of this code should probably be within the part itself.
        /// </summary>
        /// <param name="part"></param>
        /// <param name="flexi"></param>
        /// <param name="softness"></param>
        /// <param name="gravity"></param>
        /// <param name="friction"></param>
        /// <param name="wind"></param>
        /// <param name="tension"></param>
        /// <param name="Force"></param>
        protected void SetFlexi(SceneObjectPart part, bool flexi, int softness, float gravity, float friction,
            float wind, float tension, LSL_Vector Force)
        {
            if (part == null)
                return;
            var sog = part.ParentGroup;

            if (sog == null || sog.IsDeleted || sog.inTransit)
                return;

            var pbs = part.Shape;
            pbs.FlexiSoftness = softness;
            pbs.FlexiGravity = gravity;
            pbs.FlexiDrag = friction;
            pbs.FlexiWind = wind;
            pbs.FlexiTension = tension;
            pbs.FlexiForceX = (float)Force.x;
            pbs.FlexiForceY = (float)Force.y;
            pbs.FlexiForceZ = (float)Force.z;

            pbs.FlexiEntry = flexi;

            if (!pbs.SculptEntry &&
                (pbs.PathCurve == (byte)Extrusion.Straight || pbs.PathCurve == (byte)Extrusion.Flexible))
            {
                if (flexi)
                {
                    pbs.PathCurve = (byte)Extrusion.Flexible;
                    if (!sog.IsPhantom)
                    {
                        sog.ScriptSetPhantomStatus(true);
                        return;
                    }
                }
                else
                {
                    // Other values not set, they do not seem to be sent to the viewer
                    // Setting PathCurve appears to be what actually toggles the check box and turns Flexi on and off
                    pbs.PathCurve = (byte)Extrusion.Straight;
                }
            }

            part.ParentGroup.HasGroupChanged = true;
            part.ScheduleFullUpdate();
        }

        /// <summary>
        ///     Set a light point on a part
        /// </summary>
        /// FIXME: Much of this code should probably be in SceneObjectGroup
        /// <param name="part"></param>
        /// <param name="light"></param>
        /// <param name="color"></param>
        /// <param name="intensity"></param>
        /// <param name="radius"></param>
        /// <param name="falloff"></param>
        protected void SetPointLight(SceneObjectPart part, bool light, LSL_Vector color, float intensity, float radius,
            float falloff)
        {
            if (part == null || part.ParentGroup == null || part.ParentGroup.IsDeleted)
                return;

            var pbs = part.Shape;

            if (light)
            {
                pbs.LightEntry = true;
                pbs.LightColorR = Utils.Clamp((float)color.x, 0.0f, 1.0f);
                pbs.LightColorG = Utils.Clamp((float)color.y, 0.0f, 1.0f);
                pbs.LightColorB = Utils.Clamp((float)color.z, 0.0f, 1.0f);
                pbs.LightIntensity = Utils.Clamp(intensity, 0.0f, 1.0f);
                pbs.LightRadius = Utils.Clamp(radius, 0.1f, 20.0f);
                pbs.LightFalloff = Utils.Clamp(falloff, 0.01f, 2.0f);
            }
            else
            {
                pbs.LightEntry = false;
            }

            part.ParentGroup.HasGroupChanged = true;
            part.ScheduleFullUpdate();
        }

        private void CheckSayShoutTime()
        {
            var now = DateTime.UtcNow;
            if ((now - m_lastSayShoutCheck).Ticks > 10000000) // 1sec
            {
                m_lastSayShoutCheck = now;
                m_SayShoutCount = 0;
            }
            else
            {
                m_SayShoutCount++;
            }
        }

        public void ThrottleSay(int channelID, int timeMs)
        {
            if (channelID == 0)
                CheckSayShoutTime();
            if (m_SayShoutCount >= 11)
                ScriptSleep(timeMs);
        }


        private double VecDist(LSL_Vector a, LSL_Vector b)
        {
            var dx = a.x - b.x;
            var dy = a.y - b.y;
            var dz = a.z - b.z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private double VecDistSquare(LSL_Vector a, LSL_Vector b)
        {
            var dx = a.x - b.x;
            var dy = a.y - b.y;
            var dz = a.z - b.z;
            return dx * dx + dy * dy + dz * dz;
        }


        protected LSL_Vector GetColor(SceneObjectPart part, int face)
        {
            var tex = part.Shape.Textures;
            Color4 texcolor;
            var rgb = new LSL_Vector();
            var nsides = GetNumberOfSides(part);

            if (face == ScriptBaseClass.ALL_SIDES)
            {
                int i;
                for (i = 0; i < nsides; i++)
                {
                    texcolor = tex.GetFace((uint)i).RGBA;
                    rgb.x += texcolor.R;
                    rgb.y += texcolor.G;
                    rgb.z += texcolor.B;
                }

                var invnsides = 1.0f / nsides;

                rgb.x *= invnsides;
                rgb.y *= invnsides;
                rgb.z *= invnsides;

                return rgb;
            }

            if (face >= 0 && face < nsides)
            {
                texcolor = tex.GetFace((uint)face).RGBA;
                rgb.x = texcolor.R;
                rgb.y = texcolor.G;
                rgb.z = texcolor.B;

                return rgb;
            }

            return new LSL_Vector();
        }

        protected void SetTextureParams(SceneObjectPart part, string texture, double scaleU, double ScaleV,
            double offsetU, double offsetV, double rotation, int face)
        {
            if (part == null || part.ParentGroup == null || part.ParentGroup.IsDeleted)
                return;

            var textureID = new UUID();
            var dotexture = true;
            if (string.IsNullOrEmpty(texture) || texture == ScriptBaseClass.NULL_KEY)
            {
                dotexture = false;
            }
            else
            {
                textureID = ScriptUtils.GetAssetIdFromItemName(m_host, texture, (int)AssetType.Texture);
                if (textureID.IsZero())
                    if (!UUID.TryParse(texture, out textureID))
                        dotexture = false;
            }

            var tex = part.Shape.Textures;
            var nsides = GetNumberOfSides(part);

            if (face >= 0 && face < nsides)
            {
                var texface = tex.CreateFace((uint)face);
                if (dotexture)
                    texface.TextureID = textureID;
                texface.RepeatU = (float)scaleU;
                texface.RepeatV = (float)ScaleV;
                texface.OffsetU = (float)offsetU;
                texface.OffsetV = (float)offsetV;
                texface.Rotation = (float)rotation;
                tex.FaceTextures[face] = texface;
                part.UpdateTextureEntry(tex);
                return;
            }

            if (face == ScriptBaseClass.ALL_SIDES)
            {
                for (uint i = 0; i < nsides; i++)
                    if (tex.FaceTextures[i] != null)
                    {
                        if (dotexture)
                            tex.FaceTextures[i].TextureID = textureID;
                        tex.FaceTextures[i].RepeatU = (float)scaleU;
                        tex.FaceTextures[i].RepeatV = (float)ScaleV;
                        tex.FaceTextures[i].OffsetU = (float)offsetU;
                        tex.FaceTextures[i].OffsetV = (float)offsetV;
                        tex.FaceTextures[i].Rotation = (float)rotation;
                    }

                if (dotexture)
                    tex.DefaultTexture.TextureID = textureID;
                tex.DefaultTexture.RepeatU = (float)scaleU;
                tex.DefaultTexture.RepeatV = (float)ScaleV;
                tex.DefaultTexture.OffsetU = (float)offsetU;
                tex.DefaultTexture.OffsetV = (float)offsetV;
                tex.DefaultTexture.Rotation = (float)rotation;
                part.UpdateTextureEntry(tex);
            }
        }

        protected void SetTexture(SceneObjectPart part, string texture, int face)
        {
            if (part == null || part.ParentGroup == null || part.ParentGroup.IsDeleted)
                return;

            var textureID = ScriptUtils.GetAssetIdFromItemName(m_host, texture, (int)AssetType.Texture);
            if (textureID.IsZero())
                if (!UUID.TryParse(texture, out textureID) || textureID.IsZero())
                    return;

            var tex = part.Shape.Textures;
            var nsides = GetNumberOfSides(part);

            if (face >= 0 && face < nsides)
            {
                var texface = tex.CreateFace((uint)face);
                texface.TextureID = textureID;
                tex.FaceTextures[face] = texface;
                part.UpdateTextureEntry(tex);
                return;
            }

            if (face == ScriptBaseClass.ALL_SIDES)
            {
                for (uint i = 0; i < nsides; i++)
                    if (tex.FaceTextures[i] != null)
                        tex.FaceTextures[i].TextureID = textureID;
                tex.DefaultTexture.TextureID = textureID;
                part.UpdateTextureEntry(tex);
            }
        }

        protected void ScaleTexture(SceneObjectPart part, double u, double v, int face)
        {
            if (part == null || part.ParentGroup == null || part.ParentGroup.IsDeleted)
                return;

            var tex = part.Shape.Textures;
            var nsides = GetNumberOfSides(part);

            if (face >= 0 && face < nsides)
            {
                var texface = tex.CreateFace((uint)face);
                texface.RepeatU = (float)u;
                texface.RepeatV = (float)v;
                tex.FaceTextures[face] = texface;
                part.UpdateTextureEntry(tex);
                return;
            }

            if (face == ScriptBaseClass.ALL_SIDES)
            {
                for (var i = 0; i < nsides; i++)
                    if (tex.FaceTextures[i] != null)
                    {
                        tex.FaceTextures[i].RepeatU = (float)u;
                        tex.FaceTextures[i].RepeatV = (float)v;
                    }

                tex.DefaultTexture.RepeatU = (float)u;
                tex.DefaultTexture.RepeatV = (float)v;
                part.UpdateTextureEntry(tex);
            }
        }

        protected void OffsetTexture(SceneObjectPart part, double u, double v, int face)
        {
            if (part == null || part.ParentGroup == null || part.ParentGroup.IsDeleted)
                return;

            var tex = part.Shape.Textures;
            var nsides = GetNumberOfSides(part);

            if (face >= 0 && face < nsides)
            {
                var texface = tex.CreateFace((uint)face);
                texface.OffsetU = (float)u;
                texface.OffsetV = (float)v;
                tex.FaceTextures[face] = texface;
                part.UpdateTextureEntry(tex);
                return;
            }

            if (face == ScriptBaseClass.ALL_SIDES)
            {
                for (var i = 0; i < nsides; i++)
                    if (tex.FaceTextures[i] != null)
                    {
                        tex.FaceTextures[i].OffsetU = (float)u;
                        tex.FaceTextures[i].OffsetV = (float)v;
                    }

                tex.DefaultTexture.OffsetU = (float)u;
                tex.DefaultTexture.OffsetV = (float)v;
                part.UpdateTextureEntry(tex);
            }
        }

        protected void RotateTexture(SceneObjectPart part, double rotation, int face)
        {
            if (part == null || part.ParentGroup == null || part.ParentGroup.IsDeleted)
                return;

            var tex = part.Shape.Textures;
            var nsides = GetNumberOfSides(part);

            if (face >= 0 && face < nsides)
            {
                var texface = tex.CreateFace((uint)face);
                texface.Rotation = (float)rotation;
                tex.FaceTextures[face] = texface;
                part.UpdateTextureEntry(tex);
                return;
            }

            if (face == ScriptBaseClass.ALL_SIDES)
            {
                for (var i = 0; i < nsides; i++)
                    if (tex.FaceTextures[i] != null)
                        tex.FaceTextures[i].Rotation = (float)rotation;
                tex.DefaultTexture.Rotation = (float)rotation;
                part.UpdateTextureEntry(tex);
            }
        }

        protected LSL_String GetTexture(SceneObjectPart part, int face)
        {
            var tex = part.Shape.Textures;
            var nsides = GetNumberOfSides(part);

            if (face == ScriptBaseClass.ALL_SIDES) face = 0;

            if (face >= 0 && face < nsides)
            {
                Primitive.TextureEntryFace texface;
                texface = tex.GetFace((uint)face);
                var texture = texface.TextureID.ToString();

                lock (part.TaskInventory)
                {
                    foreach (var inv in part.TaskInventory)
                        if (inv.Value.AssetID.Equals(texface.TextureID))
                        {
                            texture = inv.Value.Name;
                            break;
                        }
                }

                return texture;
            }

            return ScriptBaseClass.NULL_KEY;
        }

        // Capped movemment if distance > 10m (http://wiki.secondlife.com/wiki/LlSetPos)
        // note linked setpos is capped "differently"
        private LSL_Vector SetPosAdjust(LSL_Vector start, LSL_Vector end)
        {
            if (VecDistSquare(start, end) > m_Script10mDistanceSquare)
                return start + m_Script10mDistance * llVecNorm(end - start);
            return end;
        }

        protected LSL_Vector GetSetPosTarget(SceneObjectPart part, LSL_Vector targetPos, LSL_Vector fromPos,
            bool adjust)
        {
            if (part == null)
                return targetPos;
            var grp = part.ParentGroup;
            if (grp == null || grp.IsDeleted || grp.inTransit)
                return targetPos;

            if (adjust)
                targetPos = SetPosAdjust(fromPos, targetPos);

            if (m_disable_underground_movement && grp.AttachmentPoint == 0)
                if (part.IsRoot)
                {
                    var ground = World.GetGroundHeight((float)targetPos.x, (float)targetPos.y);
                    if (targetPos.z < ground)
                        targetPos.z = ground;
                }

            return targetPos;
        }

        /// <summary>
        ///     set object position, optionally capping the distance.
        /// </summary>
        /// <param name="part"></param>
        /// <param name="targetPos"></param>
        /// <param name="adjust">if TRUE, will cap the distance to 10m.</param>
        protected void SetPos(SceneObjectPart part, LSL_Vector targetPos, bool adjust)
        {
            if (part == null)
                return;

            var grp = part.ParentGroup;
            if (grp == null || grp.IsDeleted || grp.inTransit)
                return;

            var currentPos = GetPartLocalPos(part);
            var toPos = GetSetPosTarget(part, targetPos, currentPos, adjust);

            if (part.IsRoot)
            {
                if (!grp.IsAttachment && !World.Permissions.CanObjectEntry(grp, false, toPos))
                    return;
                grp.UpdateGroupPosition(toPos);
            }
            else
            {
                part.OffsetPosition = toPos;
//                SceneObjectGroup parent = part.ParentGroup;
//                parent.HasGroupChanged = true;
//                parent.ScheduleGroupForTerseUpdate();
                part.ScheduleTerseUpdate();
            }
        }

        protected LSL_Vector GetPartLocalPos(SceneObjectPart part)
        {
            Vector3 pos;
            if (part.IsRoot)
            {
                if (part.ParentGroup.IsAttachment)
                    pos = part.AttachedPos;
                else
                    pos = part.AbsolutePosition;
            }
            else
            {
                pos = part.OffsetPosition;
            }

            //m_log.DebugFormat("[LSL API]: Returning {0} in GetPartLocalPos()", pos);
            return new LSL_Vector(pos);
        }

        protected void SetRot(SceneObjectPart part, Quaternion rot)
        {
            if (part == null || part.ParentGroup == null || part.ParentGroup.IsDeleted)
                return;

            var isroot = part == part.ParentGroup.RootPart;
            bool isphys;

            var pa = part.PhysActor;

            // keep using physactor ideia of isphysical
            // it should be SOP ideia of that
            // not much of a issue with ubOde
            if (pa != null && pa.IsPhysical)
                isphys = true;
            else
                isphys = false;

            // SL doesn't let scripts rotate root of physical linksets
            if (isroot && isphys)
                return;

            part.UpdateRotation(rot);

            // Update rotation does not move the object in the physics engine if it's a non physical linkset
            // so do a nasty update of parts positions if is a root part rotation
            if (isroot && pa != null) // with if above implies non physical  root part
            {
                part.ParentGroup.ResetChildPrimPhysicsPositions();
            }
            else // fix sitting avatars. This is only needed bc of how we link avas to child parts, not root part
            {
                //                List<ScenePresence> sittingavas = part.ParentGroup.GetLinkedAvatars();
                var sittingavas = part.ParentGroup.GetSittingAvatars();
                if (sittingavas.Count > 0)
                    foreach (var av in sittingavas)
                        if (isroot || part.LocalId == av.ParentID)
                            av.SendTerseUpdateToAllClients();
            }
        }


        private LSL_Rotation GetPartRot(SceneObjectPart part)
        {
            Quaternion q;
            if (part.LinkNum == 0 || part.LinkNum == 1) // unlinked or root prim
            {
                if (part.ParentGroup.AttachmentPoint != 0)
                {
                    var avatar = World.GetScenePresence(part.ParentGroup.AttachedAvatar);
                    if (avatar != null)
                    {
                        if ((avatar.AgentControlFlags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_MOUSELOOK) != 0)
                            q = avatar.CameraRotation; // Mouselook
                        else
                            q = avatar.GetWorldRotation(); // Currently infrequently updated so may be inaccurate
                    }
                    else
                    {
                        q = part.ParentGroup.GroupRotation; // Likely never get here but just in case
                    }
                }
                else
                {
                    q = part.ParentGroup.GroupRotation; // just the group rotation
                }

                return new LSL_Rotation(q);
            }

            q = part.GetWorldRotation();
            if (part.ParentGroup.AttachmentPoint != 0)
            {
                var avatar = World.GetScenePresence(part.ParentGroup.AttachedAvatar);
                if (avatar != null)
                {
                    if ((avatar.AgentControlFlags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_MOUSELOOK) != 0)
                        q = avatar.CameraRotation * q; // Mouselook
                    else
                        q = avatar.Rotation * q; // Currently infrequently updated so may be inaccurate
                }
            }

            return new LSL_Rotation(q.X, q.Y, q.Z, q.W);
        }

        private LSL_Rotation GetPartLocalRot(SceneObjectPart part)
        {
            var rot = part.RotationOffset;
            return new LSL_Rotation(rot.X, rot.Y, rot.Z, rot.W);
        }

        public void doObjectRez(string inventory, LSL_Vector pos, LSL_Vector vel, LSL_Rotation rot, int param,
            bool atRoot)
        {
            if (string.IsNullOrEmpty(inventory) || double.IsNaN(rot.x) || double.IsNaN(rot.y) || double.IsNaN(rot.z) ||
                double.IsNaN(rot.s))
                return;

            if (VecDistSquare(llGetPos(), pos) > m_Script10mDistanceSquare)
                return;

            var item = m_host.Inventory.GetInventoryItem(inventory);

            if (item == null)
            {
                Error("llRez(AtRoot/Object)", "Can't find object '" + inventory + "'");
                return;
            }

            if (item.InvType != (int)InventoryType.Object)
            {
                Error("llRez(AtRoot/Object)", "Can't create requested object; object is missing from database");
                return;
            }

            Util.FireAndForget(x =>
            {
                Quaternion wrot = rot;
                wrot.Normalize();
                var new_groups = World.RezObject(m_host, item, pos, wrot, vel, param, atRoot);

                // If either of these are null, then there was an unknown error.
                if (new_groups == null)
                    return;

                var notAttachment = !m_host.ParentGroup.IsAttachment;

                foreach (var group in new_groups)
                {
                    // objects rezzed with this method are die_at_edge by default.
                    group.RootPart.SetDieAtEdge(true);

                    group.ResumeScripts();

                    m_ScriptEngine.PostObjectEvent(m_host.LocalId, new EventParams(
                        "object_rez", new object[]
                        {
                            new LSL_String(
                                group.RootPart.UUID.ToString())
                        },
                        new DetectParams[0]));

                    if (notAttachment)
                    {
                        var groupmass = group.GetMass();

                        var pa = group.RootPart.PhysActor;

                        //Recoil.
                        if (pa != null && pa.IsPhysical && !((Vector3)vel).IsZero())
                        {
                            Vector3 recoil = -vel * groupmass * m_recoilScaleFactor;
                            if (!recoil.IsZero()) llApplyImpulse(recoil, 0);
                        }
                    }
                }
            }, null, "LSL_Api.doObjectRez");

            //ScriptSleep((int)((groupmass * velmag) / 10));
            ScriptSleep(m_sleepMsOnRezAtRoot);
        }


        /// <summary>
        ///     Attach the object containing this script to the avatar that owns it.
        /// </summary>
        /// <param name='attachmentPoint'>
        ///     The attachment point (e.g.
        ///     <see cref="ScriptBaseClass.ATTACH_CHEST">ATTACH_CHEST</see>)
        /// </param>
        /// <returns>true if the attach suceeded, false if it did not</returns>
        public bool AttachToAvatar(int attachmentPoint)
        {
            var grp = m_host.ParentGroup;
            var presence = World.GetScenePresence(m_host.OwnerID);

            var attachmentsModule = m_ScriptEngine.World.AttachmentsModule;

            if (attachmentsModule != null)
                return attachmentsModule.AttachObject(presence, grp, (uint)attachmentPoint, false, true, true);
            return false;
        }

        /// <summary>
        ///     Detach the object containing this script from the avatar it is attached to.
        /// </summary>
        /// <remarks>
        ///     Nothing happens if the object is not attached.
        /// </remarks>
        public void DetachFromAvatar()
        {
            Util.FireAndForget(DetachWrapper, m_host, "LSL_Api.DetachFromAvatar");
        }

        private void DetachWrapper(object o)
        {
            if (World.AttachmentsModule != null)
            {
                var host = (SceneObjectPart)o;
                var presence = World.GetScenePresence(host.OwnerID);
                World.AttachmentsModule.DetachSingleAttachmentToInv(presence, host.ParentGroup);
            }
        }

        protected void TargetOmega(SceneObjectPart part, LSL_Vector axis, double spinrate, double gain)
        {
            if (gain == 0)
            {
                part.UpdateAngularVelocity(Vector3.Zero);
                part.ScheduleFullAnimUpdate();
            }
            else
            {
                part.UpdateAngularVelocity(axis * spinrate);
            }
        }


        private void handleScriptAnswer(IClientAPI client, UUID taskID, UUID itemID, int answer)
        {
            if (taskID != m_host.UUID)
                return;

            client.OnScriptAnswer -= handleScriptAnswer;
            m_waitingForScriptAnswer = false;

            if ((answer & ScriptBaseClass.PERMISSION_TAKE_CONTROLS) == 0)
                llReleaseControls();

            m_host.TaskInventory.LockItemsForWrite(true);
            m_host.TaskInventory[m_item.ItemID].PermsMask = answer;
            m_host.TaskInventory.LockItemsForWrite(false);

            m_ScriptEngine.PostScriptEvent(m_item.ItemID, new EventParams(
                "run_time_permissions", new object[]
                {
                    new LSL_Integer(answer)
                },
                new DetectParams[0]));
        }
    }
}