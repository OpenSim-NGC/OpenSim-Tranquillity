using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenMetaverse;

using InWorldz.Phlox.Types;

namespace InWorldz.Phlox.Glue
{
	public class SyscallShim : ISyscallShim
	{
		public delegate void ShimCall(SyscallShim self);
        public delegate void LongRunSyscallDelegate();
        public delegate void PerformAsyncCallDelegate(LongRunSyscallDelegate longCall);

		private InWorldz.Phlox.VM.Interpreter _interpreter;
		private ISystemAPI _systemAPI;
        private PerformAsyncCallDelegate _asyncCallDelegate;

		public InWorldz.Phlox.VM.Interpreter Interpreter
		{
			get
			{
				return _interpreter;
			}

			set
			{
				_interpreter = value;
			}
		}

		public ISystemAPI SystemAPI
		{
			get
			{
				return _systemAPI;
			}

			set
			{
				_systemAPI = value;
			}
		}

private static string ConvToString(object o) 
        { 
            if (o is string s) return s;
            if (o == null) return string.Empty;
            return o.ToString();
        }
        private static int ConvToInt(object o) 
        { 
            if (o is int i) return i;
            if (o is float f) return (int)f;
            if (o is string s && int.TryParse(s, out int parsed)) return parsed;
            return Convert.ToInt32(o);
        }
        private static float ConvToFloat(object o) 
        { 
            if (o is float f) return f;
            if (o is int i) return (float)i;
            if (o is string s && float.TryParse(s, System.Globalization.NumberStyles.Float, 
                System.Globalization.CultureInfo.InvariantCulture, out float parsed)) return parsed;
            return Convert.ToSingle(o);
        }
        private static Vector3 ConvToVector(object o) { return (Vector3)o; }
        private static LSLList ConvToLSLList(object o) { return (LSLList)o; }
        private static Quaternion ConvToQuat(object o) { return (Quaternion)o; }
        private static UUID ConvToUUID(object o) { return UUID.Parse((string)o); }

		private static string ConvToLSLType(string o) { return o; }
        private static int ConvToLSLType(int o) { return o; }
        private static float ConvToLSLType(float o) { return o; }
        private static Vector3 ConvToLSLType(Vector3 o) { return o; }
        private static LSLList ConvToLSLType(LSLList o) { return o; }
        private static Quaternion ConvToLSLType(Quaternion o) { return o; }
        private static string ConvToLSLType(UUID o) { return o.ToString(); }

        private static ShimCall[] _shimMap = new ShimCall[]
        {
            	Shim_llSin,                 //0
				Shim_llCos,                 //1
				Shim_llTan,                 //2
				Shim_llAtan2,               //3
				Shim_llSqrt,                //4
				Shim_llPow,                 //5
				Shim_llAbs,                 //6
				Shim_llFabs,                //7
				Shim_llFrand,               //8
				Shim_llFloor,               //9
				Shim_llCeil,                //10
				Shim_llRound,               //11
				Shim_llVecMag,              //12
				Shim_llVecNorm,             //13
				Shim_llVecDist,             //14
				Shim_llRot2Euler,           //15
				Shim_llEuler2Rot,           //16
				Shim_llAxes2Rot,            //17
				Shim_llRot2Fwd,             //18
				Shim_llRot2Left,            //19
				Shim_llRot2Up,              //20
				Shim_llRotBetween,          //21
				Shim_llWhisper,             //22
				Shim_llSay,                 //23
				Shim_llShout,               //24
				Shim_llListen,              //25
				Shim_llListenControl,       //26
				Shim_llListenRemove,        //27
				Shim_llSensor,              //28
				Shim_llSensorRepeat,        //29
				Shim_llSensorRemove,        //30
				Shim_llDetectedName,        //31
				Shim_llDetectedKey,         //32
				Shim_llDetectedOwner,       //33
				Shim_llDetectedType,        //34
				Shim_llDetectedPos,         //35
				Shim_llDetectedVel,         //36
				Shim_llDetectedGrab,        //37
				Shim_llDetectedRot,         //38
				Shim_llDetectedGroup,       //39
				Shim_llDetectedLinkNumber,  //40
				Shim_llDie,                 //41
				Shim_llGround,              //42
				Shim_llCloud,               //43
				Shim_llWind,                //44
				Shim_llSetStatus,           //45
				Shim_llGetStatus,           //46
				Shim_llSetScale,            //47
				Shim_llGetScale,            //48
				Shim_llSetColor,            //49
				Shim_llGetAlpha,            //50
				Shim_llSetAlpha,            //51
				Shim_llGetColor,            //52
				Shim_llSetTexture,          //53
				Shim_llScaleTexture,        //54
				Shim_llOffsetTexture,       //55
				Shim_llRotateTexture,       //56
				Shim_llGetTexture,          //57
				Shim_llSetPos,              //58
				Shim_llGetPos,              //59
				Shim_llGetLocalPos,         //60
				Shim_llSetRot,              //61
				Shim_llGetRot,              //62
				Shim_llGetLocalRot,         //63
				Shim_llSetForce,            //64
				Shim_llGetForce,            //65
				Shim_llTarget,              //66
				Shim_llTargetRemove,        //67
				Shim_llRotTarget,           //68
				Shim_llRotTargetRemove,     //69
				Shim_llMoveToTarget,        //70
				Shim_llStopMoveToTarget,    //71
				Shim_llApplyImpulse,        //72
				Shim_llApplyRotationalImpulse,  //73
				Shim_llSetTorque,           //74
				Shim_llGetTorque,           //75
				Shim_llSetForceAndTorque,   //76
				Shim_llGetVel,              //77
				Shim_llGetAccel,            //78
				Shim_llGetOmega,            //79
				Shim_llGetTimeOfDay,        //80
				Shim_llGetWallclock,        //81
				Shim_llGetTime,             //82
				Shim_llResetTime,           //83
				Shim_llGetAndResetTime,     //84
				Shim_llSound,               //85
				Shim_llPlaySound,           //86
				Shim_llLoopSound,           //87
				Shim_llLoopSoundMaster,     //88
				Shim_llLoopSoundSlave,      //89
				Shim_llPlaySoundSlave,      //90
				Shim_llTriggerSound,        //91
				Shim_llStopSound,           //92
				Shim_llPreloadSound,        //93
				Shim_llGetSubString,        //94
				Shim_llDeleteSubString,     //95
				Shim_llInsertString,        //96
				Shim_llToUpper,             //97
				Shim_llToLower,             //98
				Shim_llGiveMoney,           //99
				Shim_llMakeExplosion,       //100
				Shim_llMakeFountain,        //101
				Shim_llMakeSmoke,           //102
				Shim_llMakeFire,            //103
				Shim_llRezObject,           //104
				Shim_llLookAt,              //105
				Shim_llStopLookAt,          //106
				Shim_llSetTimerEvent,       //107
				Shim_llSleep,               //108
				Shim_llGetMass,             //109
				Shim_llCollisionFilter,     //110
				Shim_llTakeControls,        //111
				Shim_llReleaseControls,     //112
				Shim_llAttachToAvatar,      //113
				Shim_llDetachFromAvatar,    //114
				Shim_llTakeCamera,          //115
				Shim_llReleaseCamera,       //116
				Shim_llGetOwner,            //117
				Shim_llInstantMessage,      //118
				Shim_llEmail,               //119
				Shim_llGetNextEmail,        //120
				Shim_llGetKey,              //121
				Shim_llSetBuoyancy,         //122
				Shim_llSetHoverHeight,      //123
				Shim_llStopHover,           //124
				Shim_llMinEventDelay,       //125
				Shim_llSoundPreload,        //126
				Shim_llRotLookAt,           //127
				Shim_llStringLength,        //128
				Shim_llStartAnimation,      //129
				Shim_llStopAnimation,       //130
				Shim_llPointAt,             //131
				Shim_llStopPointAt,         //132
				Shim_llTargetOmega,         //133
				Shim_llGetStartParameter,   //134
				Shim_llGodLikeRezObject,    //135
				Shim_llRequestPermissions,  //136
				Shim_llGetPermissionsKey,   //137
				Shim_llGetPermissions,      //138
				Shim_llGetLinkNumber,       //139
				Shim_llSetLinkColor,        //140
				Shim_llCreateLink,          //141
				Shim_llBreakLink,           //142
				Shim_llBreakAllLinks,       //143
				Shim_llGetLinkKey,          //144
				Shim_llGetLinkName,         //145
				Shim_llGetInventoryNumber,  //146
				Shim_llGetInventoryName,    //147
				Shim_llSetScriptState,      //148
				Shim_llGetEnergy,           //149
				Shim_llGiveInventory,       //150
				Shim_llRemoveInventory,     //151
				Shim_llSetText,             //152
				Shim_llWater,               //153
				Shim_llPassTouches,         //154
				Shim_llRequestAgentData,    //155
				Shim_llRequestInventoryData,    //156
				Shim_llSetDamage,           //157
				Shim_llTeleportAgentHome,   //158
				Shim_llModifyLand,          //159
				Shim_llCollisionSound,      //160
				Shim_llCollisionSprite,     //161
				Shim_llGetAnimation,        //162
				Shim_llResetScript,         //163
				Shim_llMessageLinked,       //164
				Shim_llPushObject,          //165
				Shim_llPassCollisions,      //166
				Shim_llGetScriptName,       //167
				Shim_llGetNumberOfSides,    //168
				Shim_llAxisAngle2Rot,       //169
				Shim_llRot2Axis,            //170
				Shim_llRot2Angle,           //171
				Shim_llAcos,                //172
				Shim_llAsin,                //173
				Shim_llAngleBetween,        //174
				Shim_llGetInventoryKey,     //175
				Shim_llAllowInventoryDrop,  //176
				Shim_llGetSunDirection,     //177
				Shim_llGetTextureOffset,    //178
				Shim_llGetTextureScale,     //179
				Shim_llGetTextureRot,       //180
				Shim_llSubStringIndex,      //181
				Shim_llGetOwnerKey,         //182
				Shim_llGetCenterOfMass,     //183
				Shim_llListSort,            //184
				Shim_llGetListLength,       //185
				Shim_llList2Integer,        //186
				Shim_llList2Float,          //187
				Shim_llList2String,         //188
				Shim_llList2Key,            //189
				Shim_llList2Vector,         //190
				Shim_llList2Rot,            //191
				Shim_llList2List,           //192
				Shim_llDeleteSubList,       //193
				Shim_llGetListEntryType,    //194
				Shim_llList2CSV,            //195
				Shim_llCSV2List,            //196
				Shim_llListRandomize,       //197
				Shim_llList2ListStrided,    //198
				Shim_llGetRegionCorner,     //199
				Shim_llListInsertList,      //200
				Shim_llListFindList,        //201
				Shim_llGetObjectName,       //202
				Shim_llSetObjectName,       //203
				Shim_llGetDate,             //204
				Shim_llEdgeOfWorld,         //205
				Shim_llGetAgentInfo,        //206
				Shim_llAdjustSoundVolume,   //207
				Shim_llSetSoundQueueing,    //208
				Shim_llSetSoundRadius,      //209
				Shim_llKey2Name,            //210
				Shim_llSetTextureAnim,      //211
				Shim_llTriggerSoundLimited, //212
				Shim_llEjectFromLand,       //213
				Shim_llParseString2List,    //214
				Shim_llOverMyLand,          //215
				Shim_llGetLandOwnerAt,      //216
				Shim_llGetNotecardLine,     //217
				Shim_llGetAgentSize,        //218
				Shim_llSameGroup,           //219
				Shim_llUnSit,               //220
				Shim_llGroundSlope,         //221
				Shim_llGroundNormal,        //222
				Shim_llGroundContour,       //223
				Shim_llGetAttached,         //224
				Shim_llGetFreeMemory,       //225
				Shim_llGetRegionName,       //226
				Shim_llGetRegionTimeDilation,   //227
				Shim_llGetRegionFPS,        //228
				Shim_llParticleSystem,      //229
				Shim_llGroundRepel,         //230
				Shim_llGiveInventoryList,   //231
				Shim_llSetVehicleType,      //232
				Shim_llSetVehicleFloatParam,//233
				Shim_llSetVehicleVectorParam,   //234
				Shim_llSetVehicleFlags,     //235
				Shim_llRemoveVehicleFlags,  //236
				Shim_llSitTarget,           //237
				Shim_llAvatarOnSitTarget,   //238
				Shim_llAddToLandPassList,   //239
				Shim_llSetTouchText,        //240
				Shim_llSetSitText,          //241
				Shim_llSetCameraEyeOffset,  //242
				Shim_llSetCameraAtOffset,   //243
				Shim_llDumpList2String,     //244
                Shim_llScriptDanger,        //245
				Shim_llDialog,              //246
				Shim_llVolumeDetect,        //247
				Shim_llResetOtherScript,    //248
				Shim_llGetScriptState,      //249
				Shim_llSetRemoteScriptAccessPin,    //250
				Shim_llRemoteLoadScriptPin, //251
				Shim_llOpenRemoteDataChannel,   //252
				Shim_llSendRemoteData,      //253
				Shim_llRemoteDataReply,     //254
				Shim_llCloseRemoteDataChannel,  //255
				Shim_llMD5String,           //256
				Shim_llSetPrimitiveParams,  //257
				Shim_llStringToBase64,      //258
				Shim_llBase64ToString,      //259
				Shim_llXorBase64Strings,    //260
				Shim_llLog10,               //261
				Shim_llLog,                 //262
				Shim_llGetAnimationList,    //263
				Shim_llSetParcelMusicURL,   //264
				Shim_llGetRootPosition,     //265
				Shim_llGetRootRotation,     //266
				Shim_llGetObjectDesc,       //267
				Shim_llSetObjectDesc,       //268
				Shim_llGetCreator,          //269
				Shim_llGetTimestamp,        //270
				Shim_llSetLinkAlpha,        //271
				Shim_llGetNumberOfPrims,    //272
				Shim_llGetNumberOfNotecardLines,    //273
				Shim_llGetBoundingBox,      //274
				Shim_llGetGeometricCenter,  //275
				Shim_llGetPrimitiveParams,  //276
				Shim_llIntegerToBase64,     //277
				Shim_llBase64ToInteger,     //278
				Shim_llGetGMTclock,         //279
				Shim_llGetSimulatorHostname,//280
				Shim_llSetLocalRot,         //281
				Shim_llParseStringKeepNulls,//282
				Shim_llRezAtRoot,           //283
				Shim_llGetObjectPermMask,   //284
				Shim_llSetObjectPermMask,   //285
				Shim_llGetInventoryPermMask,//286
				Shim_llSetInventoryPermMask,//287
				Shim_llGetInventoryCreator, //288
				Shim_llOwnerSay,            //289
				Shim_llRequestSimulatorData,//290
				Shim_llForceMouselook,      //291
				Shim_llGetObjectMass,       //292
				Shim_llListReplaceList,     //293
				Shim_llLoadURL,             //294
				Shim_llParcelMediaCommandList,  //295
				Shim_llParcelMediaQuery,    //296
				Shim_llModPow,              //297
				Shim_llGetInventoryType,    //298
				Shim_llSetPayPrice,         //299
				Shim_llGetCameraPos,        //300
				Shim_llGetCameraRot,        //301
				Shim_llSetPrimURL,          //302
				Shim_llRefreshPrimURL,      //303
				Shim_llEscapeURL,           //304
				Shim_llUnescapeURL,         //305
				Shim_llMapDestination,      //306
				Shim_llAddToLandBanList,    //307
				Shim_llRemoveFromLandPassList,  //308
				Shim_llRemoveFromLandBanList,   //309
				Shim_llSetCameraParams,     //310
				Shim_llClearCameraParams,   //311
				Shim_llListStatistics,      //312
				Shim_llGetUnixTime,         //313
				Shim_llGetParcelFlags,      //314
				Shim_llGetRegionFlags,      //315
				Shim_llXorBase64StringsCorrect, //316
				Shim_llHTTPRequest,         //317
				Shim_llResetLandBanList,    //318
				Shim_llResetLandPassList,   //319
				Shim_llGetObjectPrimCount,  //320
				Shim_llGetParcelPrimOwners, //321
				Shim_llGetParcelPrimCount,  //322
				Shim_llGetParcelMaxPrims,   //323
				Shim_llGetParcelDetails,    //324
				Shim_llSetLinkPrimitiveParams,  //325
				Shim_llSetLinkTexture,      //326
				Shim_llStringTrim,          //327
				Shim_llRegionSay,           //328
				Shim_llGetObjectDetails,    //329
				Shim_llSetClickAction,      //330
				Shim_llGetRegionAgentCount, //331
				Shim_llTextBox,             //332
				Shim_llGetAgentLanguage,    //333
				Shim_llDetectedTouchUV,     //334
				Shim_llDetectedTouchFace,   //335
				Shim_llDetectedTouchPos,    //336
				Shim_llDetectedTouchNormal, //337
				Shim_llDetectedTouchBinormal,   //338
				Shim_llDetectedTouchST,     //339
				Shim_llSHA1String,          //340
				Shim_llGetFreeURLs,         //341
				Shim_llRequestURL,          //342
				Shim_llRequestSecureURL,    //343
				Shim_llReleaseURL,          //344
				Shim_llHTTPResponse,        //345
				Shim_llGetHTTPHeader,       //346
				Shim_llSetPrimMediaParams,  //347
				Shim_llGetPrimMediaParams,  //348
				Shim_llClearPrimMedia,      //349
				Shim_llSetLinkPrimitiveParamsFast,  //350
				Shim_llGetLinkPrimitiveParams,  //351
				Shim_llLinkParticleSystem,  //352
				Shim_llSetLinkTextureAnim,  //353
				Shim_llGetLinkNumberOfSides,//354
				Shim_llGetUsername,         //355
				Shim_llRequestUsername,     //356
				Shim_llGetDisplayName,      //357
				Shim_llRequestDisplayName,  //358
				Shim_iwMakeNotecard,        //359
				Shim_iwAvatarName2Key,      //360
                Shim_iwLinkTargetOmega,     //361
                Shim_llSetVehicleRotationParam, //362
				Shim_llGetParcelMusicURL,   //363
                Shim_llSetRegionPos,        //364
                Shim_iwGetLinkInventoryNumber,  //365
                Shim_iwGetLinkInventoryType,    //366
                Shim_iwGetLinkInventoryPermMask,//367
                Shim_iwGetLinkInventoryName,//368
                Shim_iwGetLinkInventoryKey, //369
                Shim_iwGetLinkInventoryCreator, //370
                Shim_iwSHA256String,        //371
                Shim_iwTeleportAgent,       //372
                Shim_llAvatarOnLinkSitTarget,   //373
                Shim_iwGetLastOwner,        //374
				Shim_iwRemoveLinkInventory, //375
				Shim_iwGiveLinkInventory,   //376
				Shim_iwGiveLinkInventoryList,   //377
                Shim_iwGetNotecardSegment,  //378
                Shim_iwGetLinkNumberOfNotecardLines,    //379
                Shim_iwGetLinkNotecardLine, //380
                Shim_iwGetLinkNotecardSegment,  //381
                Shim_iwActiveGroup,         //382
                Shim_iwAvatarOnLink,        //383
				Shim_llRegionSayTo,         //384
                Shim_llGetUsedMemory,       //385
                Shim_iwGetLinkInventoryDesc,//386
                Shim_llGenerateKey,			//387
                Shim_iwGetLinkInventoryLastOwner, //388
				Shim_llSetLinkMedia,        //389
				Shim_llGetLinkMedia,        //390
				Shim_llClearLinkMedia,      //391
                Shim_llGetEnv,              //392
                Shim_llSetAngularVelocity,  //393
                Shim_llSetPhysicsMaterial,  //394
                Shim_llSetVelocity,         //395
				Shim_iwRezObject,           //396
				Shim_iwRezAtRoot,           //397
				Shim_iwRezPrim,             //398
                Shim_llGetAgentList,        //399
                Shim_iwGetAgentList,        //400
                Shim_iwGetWorldBoundingBox, //401
                Shim_llSetMemoryLimit,      //402
                Shim_llGetMemoryLimit,      //403
                Shim_llManageEstateAccess,  //404
				Shim_iwSubStringIndex,      //405
                Shim_llLinkSitTarget,       //406
				Shim_llGetMass,             //407
                Shim_iwGetObjectMassMKS,    //408
                Shim_llSetLinkCamera,       //409
				Shim_iwSetGround,           //410
                Shim_llSetContentType,      //411
                Shim_llJsonGetValue,        //412
                Shim_llJsonValueType,       //413
                Shim_llJsonSetValue,        //414
                Shim_llList2Json,           //415
                Shim_llJson2List,           //416
                Shim_iwSetWind,             //417
                Shim_iwHasParcelPowers,     //418
                Shim_iwGroundSurfaceNormal, //419
                Shim_iwRequestAnimationData,//420
                Shim_llCastRay,             //421
                Shim_llSetKeyframedMotion,  //422
                Shim_iwWind,                //423
                Shim_llGetPhysicsMaterial,  //424
                Shim_iwGetLocalTime,        //425
                Shim_iwGetLocalTimeOffset,  //426
                Shim_iwFormatTime,          //427
                Shim_botCreateBot,          //428
                Shim_botAddTag,             //429
                Shim_botRemoveTag,          //430
                Shim_botGetBotsWithTag,     //431
                Shim_botRemoveBotsWithTag,  //432
                Shim_botRemoveBot,          //433
                Shim_botPauseMovement,      //434
                Shim_botResumeMovement,     //435
                Shim_botWhisper,            //436
                Shim_botSay,                //437
                Shim_botShout,              //438
                Shim_botStartTyping,        //439
                Shim_botStopTyping,         //440
                Shim_botSendInstantMessage, //441
                Shim_botSitObject,          //442
                Shim_botStandUp,            //443
                Shim_botGetOwner,           //444
                Shim_botIsBot,              //445
                Shim_botTouchObject,        //446
                Shim_botSetMovementSpeed,   //447
                Shim_botGetPos,             //448
                Shim_botGetName,            //449
                Shim_botStartAnimation,     //450
                Shim_botStopAnimation,      //451
                Shim_botTeleportTo,         //452
                Shim_botChangeOwner,        //453
                Shim_botGetAllBotsInRegion, //454
                Shim_botGetAllMyBotsInRegion,//455
                Shim_botFollowAvatar,       //456
                Shim_botStopMovement,       //457
                Shim_botSetNavigationPoints,//458
                Shim_botRegisterForNavigationEvents,//459
                Shim_botSetProfile,         //460
                Shim_botSetRotation,        //461
                Shim_botGiveInventory,         //462
                Shim_botSensor,             //463
                Shim_botSensorRepeat,       //464
                Shim_botSensorRemove,       //465
                Shim_iwDetectedBot,         //466
                Shim_botListen,             //467
                Shim_botRegisterForCollisionEvents,//468
                Shim_botDeregisterFromCollisionEvents,//469
                Shim_botDeregisterFromNavigationEvents,//470
                Shim_botSetOutfit,          //471
                Shim_botRemoveOutfit,       //472
                Shim_botChangeOutfit,       //473
                Shim_botGetBotOutfits,      //474
                Shim_botWanderWithin,       //475
                Shim_botMessageLinked,      //476
                Shim_botSetProfileParams,   //477
                Shim_botGetProfileParams,   //478
                Shim_iwCheckRezError,       //479
                Shim_iwGetAngularVelocity,  //480
                Shim_iwGetAppearanceParam,  //481
                Shim_iwParseString2List,    //482
                Shim_iwChar2Int,            //483
                Shim_iwInt2Char,            //484
                Shim_iwReplaceString,       //485
                Shim_iwFormatString,        //486
                Shim_iwMatchString,         //487
                Shim_iwStringCodec,         //488
                Shim_iwMatchList,           //489
                Shim_iwColorConvert,        //490
                Shim_iwNameToColor,         //491
                Shim_iwVerifyType,          //492
                Shim_iwGroupInvite,         //493
                Shim_iwGroupEject,          //494
				Shim_iwGetAgentData,        //495
                Shim_iwIsPlusUser,          //496
                Shim_llAttachToAvatarTemp,  //497
                Shim_iwListIncludesElements,//498
                Shim_iwReverseString,       //499
                Shim_iwReverseList,         //500
                Shim_iwSearchInventory,     //501
                Shim_iwSearchLinkInventory, //502
                Shim_iwIntRand,             //503
                Shim_iwIntRandRange,        //504
                Shim_iwFrandRange,          //505
                Shim_botSearchBotOutfits,   //506
                Shim_iwListRemoveElements,  //507
                Shim_iwListRemoveDuplicates,//508
                Shim_iwStartLinkAnimation,  //509
                Shim_iwStopLinkAnimation,   //510
                Shim_iwClampInt,            //511
                Shim_iwClampFloat,          //512
                Shim_iwSearchLinksByName,   //513
                Shim_iwSearchLinksByDesc,   //514
                Shim_botHasTag,             //515
				Shim_botGetBotTags,         //516
                Shim_iwValidateURL,         //517
				Shim_iwRemoteLoadScriptPin, //518
				Shim_iwDeliverInventory,    //519
				Shim_iwDeliverInventoryList,//520
				Shim_llGetAttachedList,     //521
				Shim_llReturnObjectsByOwner,//522
				Shim_llReturnObjectsByID,   //523
				Shim_llTransferLindenDollars,//524
				Shim_iwGiveMoney,           //525
				Shim_iwStandTarget,         //526
		        Shim_iwLinkStandTarget,     //527
                Shim_llGetAnimationOverride,//528
                Shim_llSetAnimationOverride,//529
                Shim_llResetAnimationOverride,//530
                Shim_iwRezAt,               //531
				// === PART 1: ADD TO SHIM TABLE in SyscallShim.cs ===
				// Insert AFTER "Shim_iwRezAt, //531" in the delegate array

                Shim_llChar,                //532
                Shim_llOrd,                 //533
                Shim_llComputeHash,         //534
                Shim_llDetectedRezzer,      //535
                Shim_llGetInventoryDesc,    //536
                Shim_llGetInventoryAcquireTime, //537
                Shim_llName2Key,            //538
                Shim_llGetRegionTimeOfDay,  //539
                Shim_llGetSimStats,         //540
                Shim_llGetSPMaxMemory,      //541
                Shim_llGetObjectAnimationNames, //542
                Shim_llGetObjectLinkKey,    //543
                Shim_llDerezObject,         //544
                                Shim_llTeleportAgent,       //545
                                Shim_llTeleportAgentGlobalCoords, //546
                                Shim_llScaleByFactor,       //547
                                Shim_llGetMaxScaleFactor,   //548
                                Shim_llGetMinScaleFactor,   //549
                                Shim_llGetCameraAspect,     //550
                                Shim_llGetCameraFOV,        //551
                                Shim_llGetAttachedListFiltered, //552
                                Shim_llIsFriend,            //553
                                Shim_llGetVisualParams,     //554
                                Shim_llGetNotecardLineSync, //555
                                Shim_llFindNotecardTextSync,//556
                                Shim_llStartObjectAnimation,//557
                                Shim_llStopObjectAnimation, //558
                                Shim_llGetLinkSitFlags,     //559
                                Shim_llSetLinkSitFlags,     //560
                                Shim_llReplaceSubString,    //561
                                Shim_llListFindStrided,     //562
                                Shim_llList2ListSlice,      //563
                                Shim_llSortListStrided,     //564
                                Shim_llListFindListNext,    //565
                                Shim_llHMAC,                //566
                                Shim_llSHA256String,        //567
                                Shim_llLinkPlaySound,       //568
                                Shim_llLinear2sRGB,         //569
                                Shim_llSRGB2Linear,         //570
                                Shim_llWorldPosToHUD,       //571
                                Shim_llLinksetDataAvailable,    //572
                                Shim_llLinksetDataCountKeys,    //573
                                Shim_llLinksetDataRead,         //574
                                Shim_llLinksetDataReadProtected,//575
                                Shim_llLinksetDataWrite,        //576
                                Shim_llLinksetDataWriteProtected,//577
                                Shim_llLinksetDataDelete,       //578
                                Shim_llLinksetDataDeleteProtected,//579
                                Shim_llLinksetDataReset,        //580
                                Shim_llLinksetDataDeleteFound,  //581
                                Shim_llLinksetDataCountFound,   //582
                                Shim_llLinksetDataListKeys,     //583
                                Shim_llLinksetDataFindKeys,     //584
								// ── Tier 1: EEP / Environment ──
                                Shim_llGetDayLength,            //585
                                Shim_llGetDayOffset,            //586
                                Shim_llGetRegionDayLength,      //587
                                Shim_llGetRegionDayOffset,      //588
                                Shim_llGetMoonDirection,        //589
                                Shim_llGetMoonRotation,         //590
                                Shim_llGetSunRotation,          //591
                                Shim_llGetRegionSunDirection,   //592
                                Shim_llGetRegionSunRotation,    //593
                                Shim_llGetRegionMoonDirection,  //594
                                Shim_llGetRegionMoonRotation,   //595
                                Shim_llGetRegionLightDir,       //596
                                Shim_llSignRSA,                 //597
                                Shim_llGetRenderMaterial,       //598
                                Shim_llSetRenderMaterial,       //599
                                Shim_llIsLinkGLTFMaterial,      //600
                                Shim_llSetLinkGLTFOverrides,    //601
                                Shim_llRenderMaterial,          //602
								Shim_llGetHealth,               //603
                                Shim_llAdjustDamage,            //604
                                Shim_llSetHealth,               //605
								Shim_llGetClosestNavPoint,      //606
                                Shim_llGetStaticPath,           //607
                                Shim_llExecCharacterCmd,        //608
                                Shim_llDeleteCharacter,         //609
								Shim_llCreateKeyValue,          //610
                                Shim_llReadKeyValue,            //611
                                Shim_llUpdateKeyValue,          //612
                                Shim_llDeleteKeyValue,          //613
                                Shim_llKeyCountKeyValue,        //614
                                Shim_llKeysKeyValue,            //615
                                Shim_llDataSizeKeyValue,        //616
                                Shim_llClearKeyValue,           //617
                                Shim_llCreateKeyValueSL,        //618
                                Shim_llReadKeyValueSL,          //619
                                Shim_llUpdateKeyValueSL,        //620
								Shim_llCreateCharacter,         //621
								Shim_llNavigateTo,              //622
								Shim_llUpdateCharacter,         //623
								Shim_llPursue,                  //624
								Shim_llEvade,                   //625
								Shim_llFleeFrom,                //626
								Shim_llWanderWithin,            //627
								Shim_llPatrolPoints,            //628
								Shim_llVerifyRSA,               //629
								Shim_llGetEnvironment,          //630
								Shim_llSetEnvironment,          //631
								Shim_llReplaceEnvironment,      //632
								Shim_llRequestUserKey,          //633
								Shim_llSetAgentEnvironment,     //634
								Shim_llReplaceAgentEnvironment, //635
								Shim_llGetExperienceErrorMessage, //636
								Shim_llSetAgentRot,             //637
								Shim_llOpenFloater,             //638
								Shim_llCloseFloater,            //639
								Shim_llSitOnLink,               //640
								Shim_llRezObjectWithParams,     //641
								Shim_llGetMaterialOverride,     //642
								Shim_llHash,                    //643
								Shim_llLinkAdjustSoundVolume,   //644
								Shim_llLinkSetSoundQueueing,    //645
								Shim_llLinkSetSoundRadius,      //646
								Shim_llLinkStopSound,           //647
								Shim_llScriptProfiler,          //648
								Shim_llGiveAgentInventory,      //649
								Shim_llMapBeacon,               //650
								Shim_llGetStartString,          //651
								Shim_llSetGroundTexture,        //652
								Shim_llTargetedEmail,           //653
								Shim_llTransferOwnership,       //654
								Shim_llDetectedDamage,          //655
								Shim_llDamage,                  //656
								Shim_llSetLinkRenderMaterial,   //657
								Shim_llXorBase64,               //658
								Shim_llRequestExperiencePermissions, //659
								Shim_llAgentInExperience,       //660
								Shim_llGetExperienceDetails,    //661
								Shim_llGetPayPrice,             //662
								Shim_llGetAgentRot,             //663
								Shim_llGetGroundTexture,        //664
								Shim_llGetVehicleFlags,         //665
								Shim_llGetLinkGLTFOverrides,    //666
								Shim_botSetPersistent,          //667
								Shim_botRemovePersistent,       //668
								Shim_botIsPersistent,           //669
								Shim_botGetPersistentData,      //670
								Shim_botSetPersistentData,      //671
								Shim_osTeleportAgent,           //672
								Shim_osGetAvatarList,           //673
        };

        public void SetScriptEventFlags()
        {
            _systemAPI.SetScriptEventFlags();
        }

        public void ShoutError(string errorText)
        {
            _systemAPI.ShoutError(errorText);
        }

		public SyscallShim(PerformAsyncCallDelegate asyncCallDelegate)
		{
            _asyncCallDelegate = asyncCallDelegate;
		}

        public void OnScriptReset()
        {
            _systemAPI.OnScriptReset();
        }

        public void OnStateChange()
        {
            _systemAPI.OnStateChange();
        }

        public void OnScriptUnloaded(ScriptUnloadReason reason, VM.RuntimeState.LocalDisableFlag localFlag)
        {
            _systemAPI.OnScriptUnloaded(reason, localFlag);
        }

        public void AddExecutionTime(double ms)
        {
            _systemAPI.AddExecutionTime(ms);
        }

        // The average elapsed time (in ms) of the last N script executions.
        public float GetAverageScriptTime()
        {
            return _systemAPI.GetAverageScriptTime();
        }

        public void OnScriptInjected(bool fromCrossing)
        {
            _systemAPI.OnScriptInjected(fromCrossing);
        }

        public void OnGroupCrossedAvatarReady(UUID avatarId)
        {
            _systemAPI.OnGroupCrossedAvatarReady(avatarId);
        }

        #region ISyscallShim Members

        public void Call(int funcid)
        {
            _shimMap[funcid](this);
        }

        #endregion

        static private void Shim_llSin(SyscallShim self)
        {
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.llSin(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llCos(SyscallShim self)
        {
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.llCos(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llTan(SyscallShim self)
        {
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.llTan(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llAtan2(SyscallShim self)
        {
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.llAtan2(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSqrt(SyscallShim self)
        {
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.llSqrt(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llPow(SyscallShim self)
        {
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.llPow(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llAbs(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llAbs(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llFabs(SyscallShim self)
        {
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.llFabs(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llFrand(SyscallShim self)
        {
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.llFrand(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llFloor(SyscallShim self)
        {
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llFloor(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llCeil(SyscallShim self)
        {
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llCeil(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llRound(SyscallShim self)
        {
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llRound(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llVecMag(SyscallShim self)
        {
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.llVecMag(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llVecNorm(SyscallShim self)
        {
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.llVecNorm(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llVecDist(SyscallShim self)
        {
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.llVecDist(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llRot2Euler(SyscallShim self)
        {
            Quaternion p0 = ConvToQuat(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.llRot2Euler(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llEuler2Rot(SyscallShim self)
        {
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            Quaternion ret = self._systemAPI.llEuler2Rot(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llAxes2Rot(SyscallShim self)
        {
            Vector3 p2 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            Quaternion ret = self._systemAPI.llAxes2Rot(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llRot2Fwd(SyscallShim self)
        {
            Quaternion p0 = ConvToQuat(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.llRot2Fwd(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llRot2Left(SyscallShim self)
        {
            Quaternion p0 = ConvToQuat(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.llRot2Left(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llRot2Up(SyscallShim self)
        {
            Quaternion p0 = ConvToQuat(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.llRot2Up(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llRotBetween(SyscallShim self)
        {
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            Quaternion ret = self._systemAPI.llRotBetween(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llWhisper(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llWhisper(p0, p1);

        }

        static private void Shim_llSay(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSay(p0, p1);

        }

        static private void Shim_llShout(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llShout(p0, p1);

        }

        static private void Shim_llListen(SyscallShim self)
        {
            string p3 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llListen(p0, p1, p2, p3);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llListenControl(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llListenControl(p0, p1);

        }

        static private void Shim_llListenRemove(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llListenRemove(p0);

        }

        static private void Shim_llSensor(SyscallShim self)
        {
            float p4 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p3 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSensor(p0, p1, p2, p3, p4);

        }

        static private void Shim_llSensorRepeat(SyscallShim self)
        {
            float p5 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p4 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p3 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSensorRepeat(p0, p1, p2, p3, p4, p5);

        }

        static private void Shim_llSensorRemove(SyscallShim self)
        {

            self._systemAPI.llSensorRemove();

        }

        static private void Shim_llDetectedName(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llDetectedName(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llDetectedKey(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llDetectedKey(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llDetectedOwner(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llDetectedOwner(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llDetectedType(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llDetectedType(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llDetectedPos(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.llDetectedPos(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llDetectedVel(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.llDetectedVel(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llDetectedGrab(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.llDetectedGrab(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llDetectedRot(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            Quaternion ret = self._systemAPI.llDetectedRot(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llDetectedGroup(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llDetectedGroup(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llDetectedLinkNumber(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llDetectedLinkNumber(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llDie(SyscallShim self)
        {

            self._systemAPI.llDie();

        }

        static private void Shim_llGround(SyscallShim self)
        {
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.llGround(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llCloud(SyscallShim self)
        {
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.llCloud(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llWind(SyscallShim self)
        {
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.llWind(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetStatus(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetStatus(p0, p1);

        }

        static private void Shim_llGetStatus(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llGetStatus(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetScale(SyscallShim self)
        {
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetScale(p0);

        }

        static private void Shim_llGetScale(SyscallShim self)
        {

            Vector3 ret = self._systemAPI.llGetScale();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetColor(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetColor(p0, p1);

        }

        static private void Shim_llGetAlpha(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.llGetAlpha(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetAlpha(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetAlpha(p0, p1);

        }

        static private void Shim_llGetColor(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.llGetColor(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetTexture(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetTexture(p0, p1);

        }

        static private void Shim_llScaleTexture(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llScaleTexture(p0, p1, p2);

        }

        static private void Shim_llOffsetTexture(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llOffsetTexture(p0, p1, p2);

        }

        static private void Shim_llRotateTexture(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llRotateTexture(p0, p1);

        }

        static private void Shim_llGetTexture(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llGetTexture(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetPos(SyscallShim self)
        {
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetPos(p0);

        }

        static private void Shim_llGetPos(SyscallShim self)
        {

            Vector3 ret = self._systemAPI.llGetPos();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetLocalPos(SyscallShim self)
        {

            Vector3 ret = self._systemAPI.llGetLocalPos();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetRot(SyscallShim self)
        {
            Quaternion p0 = ConvToQuat(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetRot(p0);

        }

        static private void Shim_llGetRot(SyscallShim self)
        {

            Quaternion ret = self._systemAPI.llGetRot();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetLocalRot(SyscallShim self)
        {

            Quaternion ret = self._systemAPI.llGetLocalRot();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetForce(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetForce(p0, p1);

        }

        static private void Shim_llGetForce(SyscallShim self)
        {

            Vector3 ret = self._systemAPI.llGetForce();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llTarget(SyscallShim self)
        {
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llTarget(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llTargetRemove(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llTargetRemove(p0);

        }

        static private void Shim_llRotTarget(SyscallShim self)
        {
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            Quaternion p0 = ConvToQuat(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llRotTarget(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llRotTargetRemove(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llRotTargetRemove(p0);

        }

        static private void Shim_llMoveToTarget(SyscallShim self)
        {
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llMoveToTarget(p0, p1);

        }

        static private void Shim_llStopMoveToTarget(SyscallShim self)
        {

            self._systemAPI.llStopMoveToTarget();

        }

        static private void Shim_llApplyImpulse(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llApplyImpulse(p0, p1);

        }

        static private void Shim_llApplyRotationalImpulse(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llApplyRotationalImpulse(p0, p1);

        }

        static private void Shim_llSetTorque(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetTorque(p0, p1);

        }

        static private void Shim_llGetTorque(SyscallShim self)
        {

            Vector3 ret = self._systemAPI.llGetTorque();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetForceAndTorque(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetForceAndTorque(p0, p1, p2);

        }

        static private void Shim_llGetVel(SyscallShim self)
        {

            Vector3 ret = self._systemAPI.llGetVel();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetAccel(SyscallShim self)
        {

            Vector3 ret = self._systemAPI.llGetAccel();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetOmega(SyscallShim self)
        {

            Vector3 ret = self._systemAPI.llGetOmega();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetTimeOfDay(SyscallShim self)
        {

            float ret = self._systemAPI.llGetTimeOfDay();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetWallclock(SyscallShim self)
        {

            float ret = self._systemAPI.llGetWallclock();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetTime(SyscallShim self)
        {

            float ret = self._systemAPI.llGetTime();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llResetTime(SyscallShim self)
        {

            self._systemAPI.llResetTime();

        }

        static private void Shim_llGetAndResetTime(SyscallShim self)
        {

            float ret = self._systemAPI.llGetAndResetTime();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSound(SyscallShim self)
        {
            int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSound(p0, p1, p2, p3);

        }

        static private void Shim_llPlaySound(SyscallShim self)
        {
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llPlaySound(p0, p1);

        }

        static private void Shim_llLoopSound(SyscallShim self)
        {
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llLoopSound(p0, p1);

        }

        static private void Shim_llLoopSoundMaster(SyscallShim self)
        {
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llLoopSoundMaster(p0, p1);

        }

        static private void Shim_llLoopSoundSlave(SyscallShim self)
        {
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llLoopSoundSlave(p0, p1);

        }

        static private void Shim_llPlaySoundSlave(SyscallShim self)
        {
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llPlaySoundSlave(p0, p1);

        }

        static private void Shim_llTriggerSound(SyscallShim self)
        {
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llTriggerSound(p0, p1);

        }

        static private void Shim_llStopSound(SyscallShim self)
        {

            self._systemAPI.llStopSound();

        }

        static private void Shim_llPreloadSound(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llPreloadSound(p0);

        }

        static private void Shim_llGetSubString(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llGetSubString(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llDeleteSubString(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llDeleteSubString(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llInsertString(SyscallShim self)
        {
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llInsertString(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llToUpper(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llToUpper(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llToLower(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llToLower(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGiveMoney(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llGiveMoney(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llMakeExplosion(SyscallShim self)
        {
            Vector3 p6 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            string p5 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            float p4 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p3 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p2 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llMakeExplosion(p0, p1, p2, p3, p4, p5, p6);

        }

        static private void Shim_llMakeFountain(SyscallShim self)
        {
            float p8 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p7 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            string p6 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p5 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            float p4 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p3 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p2 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llMakeFountain(p0, p1, p2, p3, p4, p5, p6, p7, p8);

        }

        static private void Shim_llMakeSmoke(SyscallShim self)
        {
            Vector3 p6 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            string p5 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            float p4 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p3 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p2 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llMakeSmoke(p0, p1, p2, p3, p4, p5, p6);

        }

        static private void Shim_llMakeFire(SyscallShim self)
        {
            Vector3 p6 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            string p5 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            float p4 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p3 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p2 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llMakeFire(p0, p1, p2, p3, p4, p5, p6);

        }

        static private void Shim_llRezObject(SyscallShim self)
        {
            int p4 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            Quaternion p3 = ConvToQuat(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p2 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.llRezObject(p0, p1, p2, p3, p4);
            });
        }

        static private void Shim_llLookAt(SyscallShim self)
        {
            float p2 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llLookAt(p0, p1, p2);

        }

        static private void Shim_llStopLookAt(SyscallShim self)
        {

            self._systemAPI.llStopLookAt();

        }

        static private void Shim_llSetTimerEvent(SyscallShim self)
        {
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetTimerEvent(p0);

        }

        static private void Shim_llSleep(SyscallShim self)
        {
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSleep(p0);

        }

        static private void Shim_llGetMass(SyscallShim self)
        {

            float ret = self._systemAPI.llGetMass();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llCollisionFilter(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llCollisionFilter(p0, p1, p2);

        }

        static private void Shim_llTakeControls(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llTakeControls(p0, p1, p2);

        }

        static private void Shim_llReleaseControls(SyscallShim self)
        {

            self._systemAPI.llReleaseControls();

        }

        static private void Shim_llAttachToAvatar(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llAttachToAvatar(p0);

        }

        static private void Shim_llDetachFromAvatar(SyscallShim self)
        {

            self._systemAPI.llDetachFromAvatar();

        }

        static private void Shim_llTakeCamera(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llTakeCamera(p0);

        }

        static private void Shim_llReleaseCamera(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llReleaseCamera(p0);
        }

        static private void Shim_llGetOwner(SyscallShim self)
        {
            string ret = self._systemAPI.llGetOwner();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llInstantMessage(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.llInstantMessage(p0, p1);
            });
        }

        static private void Shim_llEmail(SyscallShim self)
        {
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.llEmail(p0, p1, p2);
            });
        }

        static private void Shim_llGetNextEmail(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.llGetNextEmail(p0, p1);
            });

        }

        static private void Shim_llGetKey(SyscallShim self)
        {

            string ret = self._systemAPI.llGetKey();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetBuoyancy(SyscallShim self)
        {
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetBuoyancy(p0);

        }

        static private void Shim_llSetHoverHeight(SyscallShim self)
        {
            float p2 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetHoverHeight(p0, p1, p2);

        }

        static private void Shim_llStopHover(SyscallShim self)
        {

            self._systemAPI.llStopHover();

        }

        static private void Shim_llMinEventDelay(SyscallShim self)
        {
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llMinEventDelay(p0);

        }

        static private void Shim_llSoundPreload(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSoundPreload(p0);

        }

        static private void Shim_llRotLookAt(SyscallShim self)
        {
            float p2 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            Quaternion p0 = ConvToQuat(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llRotLookAt(p0, p1, p2);

        }

        static private void Shim_llStringLength(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llStringLength(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llStartAnimation(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llStartAnimation(p0);

        }

        static private void Shim_llStopAnimation(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llStopAnimation(p0);

        }

        static private void Shim_llPointAt(SyscallShim self)
        {
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llPointAt(p0);

        }

        static private void Shim_llStopPointAt(SyscallShim self)
        {

            self._systemAPI.llStopPointAt();

        }

        static private void Shim_llTargetOmega(SyscallShim self)
        {
            float p2 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llTargetOmega(p0, p1, p2);

        }

        static private void Shim_llGetStartParameter(SyscallShim self)
        {

            int ret = self._systemAPI.llGetStartParameter();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGodLikeRezObject(SyscallShim self)
        {
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llGodLikeRezObject(p0, p1);

        }

        static private void Shim_llRequestPermissions(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llRequestPermissions(p0, p1);

        }

        static private void Shim_llGetPermissionsKey(SyscallShim self)
        {

            string ret = self._systemAPI.llGetPermissionsKey();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetPermissions(SyscallShim self)
        {

            int ret = self._systemAPI.llGetPermissions();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetLinkNumber(SyscallShim self)
        {

            int ret = self._systemAPI.llGetLinkNumber();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetLinkColor(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetLinkColor(p0, p1, p2);

        }

        static private void Shim_llCreateLink(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llCreateLink(p0, p1);

        }

        static private void Shim_llBreakLink(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llBreakLink(p0);

        }

        static private void Shim_llBreakAllLinks(SyscallShim self)
        {

            self._systemAPI.llBreakAllLinks();

        }

        static private void Shim_llGetLinkKey(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llGetLinkKey(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetLinkName(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llGetLinkName(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetInventoryNumber(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llGetInventoryNumber(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetInventoryName(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llGetInventoryName(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetScriptState(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetScriptState(p0, p1);

        }

        static private void Shim_llGetEnergy(SyscallShim self)
        {

            float ret = self._systemAPI.llGetEnergy();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGiveInventory(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.llGiveInventory(p0, p1);
            });
        }

        static private void Shim_llRemoveInventory(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llRemoveInventory(p0);
        }

        static private void Shim_llSetText(SyscallShim self)
        {
            float p2 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetText(p0, p1, p2);

        }

        static private void Shim_llWater(SyscallShim self)
        {
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.llWater(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llPassTouches(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llPassTouches(p0);

        }

        static private void Shim_llRequestAgentData(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.llRequestAgentData(p0, p1);
            });
        }

        static private void Shim_llRequestInventoryData(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llRequestInventoryData(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetDamage(SyscallShim self)
        {
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetDamage(p0);

        }

        static private void Shim_llTeleportAgentHome(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.llTeleportAgentHome(p0);
            });
        }

        static private void Shim_llModifyLand(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llModifyLand(p0, p1);

        }

        static private void Shim_llCollisionSound(SyscallShim self)
        {
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llCollisionSound(p0, p1);

        }

        static private void Shim_llCollisionSprite(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llCollisionSprite(p0);

        }

        static private void Shim_llGetAnimation(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llGetAnimation(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llResetScript(SyscallShim self)
        {

            self._systemAPI.llResetScript();

        }

        static private void Shim_llMessageLinked(SyscallShim self)
        {
            string p3 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llMessageLinked(p0, p1, p2, p3);

        }

        static private void Shim_llPushObject(SyscallShim self)
        {
            int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p2 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llPushObject(p0, p1, p2, p3);

        }

        static private void Shim_llPassCollisions(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llPassCollisions(p0);

        }

        static private void Shim_llGetScriptName(SyscallShim self)
        {

            string ret = self._systemAPI.llGetScriptName();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetNumberOfSides(SyscallShim self)
        {

            int ret = self._systemAPI.llGetNumberOfSides();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llAxisAngle2Rot(SyscallShim self)
        {
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            Quaternion ret = self._systemAPI.llAxisAngle2Rot(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llRot2Axis(SyscallShim self)
        {
            Quaternion p0 = ConvToQuat(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.llRot2Axis(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llRot2Angle(SyscallShim self)
        {
            Quaternion p0 = ConvToQuat(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.llRot2Angle(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llAcos(SyscallShim self)
        {
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.llAcos(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llAsin(SyscallShim self)
        {
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.llAsin(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llAngleBetween(SyscallShim self)
        {
            Quaternion p1 = ConvToQuat(self._interpreter.ScriptState.Operands.Pop());
            Quaternion p0 = ConvToQuat(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.llAngleBetween(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetInventoryKey(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llGetInventoryKey(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llAllowInventoryDrop(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llAllowInventoryDrop(p0);

        }

        static private void Shim_llGetSunDirection(SyscallShim self)
        {

            Vector3 ret = self._systemAPI.llGetSunDirection();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetTextureOffset(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.llGetTextureOffset(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetTextureScale(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.llGetTextureScale(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetTextureRot(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.llGetTextureRot(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSubStringIndex(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llSubStringIndex(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwSubStringIndex(SyscallShim self)
        {
            int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.iwSubStringIndex(p0, p1, p2, p3);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetOwnerKey(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llGetOwnerKey(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetCenterOfMass(SyscallShim self)
        {

            Vector3 ret = self._systemAPI.llGetCenterOfMass();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llListSort(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.llListSort(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetListLength(SyscallShim self)
        {
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llGetListLength(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llList2Integer(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llList2Integer(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llList2Float(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.llList2Float(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llList2String(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llList2String(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llList2Key(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llList2Key(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llList2Vector(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.llList2Vector(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llList2Rot(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            Quaternion ret = self._systemAPI.llList2Rot(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llList2List(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.llList2List(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llDeleteSubList(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.llDeleteSubList(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetListEntryType(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llGetListEntryType(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llList2CSV(SyscallShim self)
        {
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llList2CSV(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llCSV2List(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.llCSV2List(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llListRandomize(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.llListRandomize(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llList2ListStrided(SyscallShim self)
        {
            int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.llList2ListStrided(p0, p1, p2, p3);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetRegionCorner(SyscallShim self)
        {

            Vector3 ret = self._systemAPI.llGetRegionCorner();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llListInsertList(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.llListInsertList(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llListFindList(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llListFindList(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetObjectName(SyscallShim self)
        {

            string ret = self._systemAPI.llGetObjectName();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetObjectName(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetObjectName(p0);

        }

        static private void Shim_llGetDate(SyscallShim self)
        {

            string ret = self._systemAPI.llGetDate();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llEdgeOfWorld(SyscallShim self)
        {
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llEdgeOfWorld(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetAgentInfo(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llGetAgentInfo(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llAdjustSoundVolume(SyscallShim self)
        {
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llAdjustSoundVolume(p0);

        }

        static private void Shim_llSetSoundQueueing(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetSoundQueueing(p0);

        }

        static private void Shim_llSetSoundRadius(SyscallShim self)
        {
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetSoundRadius(p0);

        }

        static private void Shim_llKey2Name(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llKey2Name(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetTextureAnim(SyscallShim self)
        {
            float p6 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p5 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p4 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetTextureAnim(p0, p1, p2, p3, p4, p5, p6);

        }

        static private void Shim_llTriggerSoundLimited(SyscallShim self)
        {
            Vector3 p3 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p2 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llTriggerSoundLimited(p0, p1, p2, p3);

        }

        static private void Shim_llEjectFromLand(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.llEjectFromLand(p0);
            });
        }

        static private void Shim_llParseString2List(SyscallShim self)
        {
            LSLList p2 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.llParseString2List(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwParseString2List(SyscallShim self)
        {
            LSLList p3 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            LSLList p2 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.iwParseString2List(p0, p1, p2, p3);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llOverMyLand(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llOverMyLand(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetLandOwnerAt(SyscallShim self)
        {
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llGetLandOwnerAt(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetNotecardLine(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llGetNotecardLine(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetAgentSize(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.llGetAgentSize(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSameGroup(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llSameGroup(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llUnSit(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llUnSit(p0);
        }

        static private void Shim_llGroundSlope(SyscallShim self)
        {
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.llGroundSlope(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGroundNormal(SyscallShim self)
        {
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.llGroundNormal(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGroundContour(SyscallShim self)
        {
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.llGroundContour(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetAttached(SyscallShim self)
        {

            int ret = self._systemAPI.llGetAttached();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetFreeMemory(SyscallShim self)
        {

            int ret = self._systemAPI.llGetFreeMemory();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetUsedMemory(SyscallShim self)
        {

            int ret = self._systemAPI.llGetUsedMemory();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetRegionName(SyscallShim self)
        {

            string ret = self._systemAPI.llGetRegionName();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetRegionTimeDilation(SyscallShim self)
        {

            float ret = self._systemAPI.llGetRegionTimeDilation();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetRegionFPS(SyscallShim self)
        {

            float ret = self._systemAPI.llGetRegionFPS();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llParticleSystem(SyscallShim self)
        {
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llParticleSystem(p0);

        }

        static private void Shim_llGroundRepel(SyscallShim self)
        {
            float p2 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llGroundRepel(p0, p1, p2);

        }

        static private void Shim_llGiveInventoryList(SyscallShim self)
        {
            LSLList p2 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.llGiveInventoryList(p0, p1, p2);
            });
        }

        static private void Shim_llSetVehicleType(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetVehicleType(p0);

        }

        static private void Shim_llSetVehicleFloatParam(SyscallShim self)
        {
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetVehicleFloatParam(p0, p1);

        }

        static private void Shim_llSetVehicleVectorParam(SyscallShim self)
        {
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetVehicleVectorParam(p0, p1);

        }

        static private void Shim_llSetVehicleRotationParam(SyscallShim self)
        {
            Quaternion p1 = ConvToQuat(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetVehicleRotationParam(p0, p1);

        }

        static private void Shim_llSetVehicleFlags(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetVehicleFlags(p0);

        }

        static private void Shim_llRemoveVehicleFlags(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llRemoveVehicleFlags(p0);
        }

        static private void Shim_llSitTarget(SyscallShim self)
        {
            Quaternion p1 = ConvToQuat(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSitTarget(p0, p1);
        }

        static private void Shim_llAvatarOnSitTarget(SyscallShim self)
        {
            string ret = self._systemAPI.llAvatarOnSitTarget();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llAddToLandPassList(SyscallShim self)
        {
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llAddToLandPassList(p0, p1);

        }

        static private void Shim_llSetTouchText(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetTouchText(p0);

        }

        static private void Shim_llSetSitText(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetSitText(p0);

        }

        static private void Shim_llSetCameraEyeOffset(SyscallShim self)
        {
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetCameraEyeOffset(p0);

        }

        static private void Shim_llSetCameraAtOffset(SyscallShim self)
        {
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetCameraAtOffset(p0);

        }

        static private void Shim_llDumpList2String(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llDumpList2String(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llScriptDanger(SyscallShim self)
        {
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llScriptDanger(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llDialog(SyscallShim self)
        {
            int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList p2 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llDialog(p0, p1, p2, p3);

        }

        static private void Shim_llVolumeDetect(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llVolumeDetect(p0);

        }

        static private void Shim_llResetOtherScript(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llResetOtherScript(p0);

        }

        static private void Shim_llGetScriptState(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llGetScriptState(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetRemoteScriptAccessPin(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetRemoteScriptAccessPin(p0);

        }

        static private void Shim_llRemoteLoadScriptPin(SyscallShim self)
        {
            int p4 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llRemoteLoadScriptPin(p0, p1, p2, p3, p4);

        }

        static private void Shim_llOpenRemoteDataChannel(SyscallShim self)
        {

            self._systemAPI.llOpenRemoteDataChannel();

        }

        static private void Shim_llSendRemoteData(SyscallShim self)
        {
            string p3 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llSendRemoteData(p0, p1, p2, p3);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llRemoteDataReply(SyscallShim self)
        {
            int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llRemoteDataReply(p0, p1, p2, p3);

        }

        static private void Shim_llCloseRemoteDataChannel(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llCloseRemoteDataChannel(p0);

        }

        static private void Shim_llMD5String(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llMD5String(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetPrimitiveParams(SyscallShim self)
        {
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetPrimitiveParams(p0);

        }

        static private void Shim_llStringToBase64(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llStringToBase64(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llBase64ToString(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llBase64ToString(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llXorBase64Strings(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llXorBase64Strings(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llLog10(SyscallShim self)
        {
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.llLog10(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llLog(SyscallShim self)
        {
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.llLog(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetAnimationList(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.llGetAnimationList(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetParcelMusicURL(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetParcelMusicURL(p0);

        }

        static private void Shim_llGetRootPosition(SyscallShim self)
        {

            Vector3 ret = self._systemAPI.llGetRootPosition();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetRootRotation(SyscallShim self)
        {

            Quaternion ret = self._systemAPI.llGetRootRotation();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetObjectDesc(SyscallShim self)
        {

            string ret = self._systemAPI.llGetObjectDesc();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetObjectDesc(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetObjectDesc(p0);

        }

        static private void Shim_llGetCreator(SyscallShim self)
        {

            string ret = self._systemAPI.llGetCreator();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetTimestamp(SyscallShim self)
        {

            string ret = self._systemAPI.llGetTimestamp();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetLinkAlpha(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetLinkAlpha(p0, p1, p2);

        }

        static private void Shim_llGetNumberOfPrims(SyscallShim self)
        {

            int ret = self._systemAPI.llGetNumberOfPrims();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetNumberOfNotecardLines(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llGetNumberOfNotecardLines(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetBoundingBox(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.llGetBoundingBox(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwGetWorldBoundingBox(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.iwGetWorldBoundingBox(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetGeometricCenter(SyscallShim self)
        {

            Vector3 ret = self._systemAPI.llGetGeometricCenter();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetPrimitiveParams(SyscallShim self)
        {
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.llGetPrimitiveParams(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llIntegerToBase64(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llIntegerToBase64(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llBase64ToInteger(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llBase64ToInteger(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetGMTclock(SyscallShim self)
        {

            float ret = self._systemAPI.llGetGMTclock();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetSimulatorHostname(SyscallShim self)
        {

            string ret = self._systemAPI.llGetSimulatorHostname();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetLocalRot(SyscallShim self)
        {
            Quaternion p0 = ConvToQuat(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetLocalRot(p0);

        }

        static private void Shim_llParseStringKeepNulls(SyscallShim self)
        {
            LSLList p2 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.llParseStringKeepNulls(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llRezAtRoot(SyscallShim self)
        {
            int p4 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            Quaternion p3 = ConvToQuat(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p2 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.llRezAtRoot(p0, p1, p2, p3, p4);
            });
        }

        static private void Shim_llGetObjectPermMask(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llGetObjectPermMask(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetObjectPermMask(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetObjectPermMask(p0, p1);

        }

        static private void Shim_llGetInventoryPermMask(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llGetInventoryPermMask(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetInventoryPermMask(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetInventoryPermMask(p0, p1, p2);

        }

        static private void Shim_llGetInventoryCreator(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llGetInventoryCreator(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llOwnerSay(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llOwnerSay(p0);

        }

        static private void Shim_llRequestSimulatorData(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llRequestSimulatorData(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llForceMouselook(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llForceMouselook(p0);

        }

        static private void Shim_llGetObjectMass(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.llGetObjectMass(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llListReplaceList(SyscallShim self)
        {
            int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.llListReplaceList(p0, p1, p2, p3);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llLoadURL(SyscallShim self)
        {
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llLoadURL(p0, p1, p2);

        }

        static private void Shim_llParcelMediaCommandList(SyscallShim self)
        {
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llParcelMediaCommandList(p0);

        }

        static private void Shim_llParcelMediaQuery(SyscallShim self)
        {
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.llParcelMediaQuery(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llModPow(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llModPow(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetInventoryType(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llGetInventoryType(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetPayPrice(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetPayPrice(p0, p1);

        }

        static private void Shim_llGetCameraPos(SyscallShim self)
        {

            Vector3 ret = self._systemAPI.llGetCameraPos();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetCameraRot(SyscallShim self)
        {

            Quaternion ret = self._systemAPI.llGetCameraRot();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetPrimURL(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetPrimURL(p0);

        }

        static private void Shim_llRefreshPrimURL(SyscallShim self)
        {

            self._systemAPI.llRefreshPrimURL();

        }

        static private void Shim_llEscapeURL(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llEscapeURL(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llUnescapeURL(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llUnescapeURL(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llMapDestination(SyscallShim self)
        {
            Vector3 p2 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llMapDestination(p0, p1, p2);

        }

        static private void Shim_llAddToLandBanList(SyscallShim self)
        {
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llAddToLandBanList(p0, p1);

        }

        static private void Shim_llRemoveFromLandPassList(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llRemoveFromLandPassList(p0);

        }

        static private void Shim_llRemoveFromLandBanList(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llRemoveFromLandBanList(p0);

        }

        static private void Shim_llSetCameraParams(SyscallShim self)
        {
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetCameraParams(p0);

        }

        static private void Shim_llClearCameraParams(SyscallShim self)
        {

            self._systemAPI.llClearCameraParams();

        }

        static private void Shim_llListStatistics(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.llListStatistics(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetUnixTime(SyscallShim self)
        {

            int ret = self._systemAPI.llGetUnixTime();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetParcelFlags(SyscallShim self)
        {
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llGetParcelFlags(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetRegionFlags(SyscallShim self)
        {

            int ret = self._systemAPI.llGetRegionFlags();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llXorBase64StringsCorrect(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llXorBase64StringsCorrect(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llHTTPRequest(SyscallShim self)
        {
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llHTTPRequest(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llResetLandBanList(SyscallShim self)
        {

            self._systemAPI.llResetLandBanList();

        }

        static private void Shim_llResetLandPassList(SyscallShim self)
        {

            self._systemAPI.llResetLandPassList();

        }

        static private void Shim_llGetObjectPrimCount(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llGetObjectPrimCount(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetParcelPrimOwners(SyscallShim self)
        {
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.llGetParcelPrimOwners(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetParcelPrimCount(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llGetParcelPrimCount(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetParcelMaxPrims(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llGetParcelMaxPrims(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetParcelDetails(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.llGetParcelDetails(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetLinkPrimitiveParams(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetLinkPrimitiveParams(p0, p1);

        }

        static private void Shim_llSetLinkTexture(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetLinkTexture(p0, p1, p2);

        }

        static private void Shim_llStringTrim(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llStringTrim(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llRegionSay(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llRegionSay(p0, p1);

        }

        static private void Shim_llGetObjectDetails(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.llGetObjectDetails(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetClickAction(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetClickAction(p0);

        }

        static private void Shim_llGetRegionAgentCount(SyscallShim self)
        {

            int ret = self._systemAPI.llGetRegionAgentCount();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llTextBox(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llTextBox(p0, p1, p2);

        }

        static private void Shim_llGetAgentLanguage(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llGetAgentLanguage(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llDetectedTouchUV(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.llDetectedTouchUV(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llDetectedTouchFace(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llDetectedTouchFace(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llDetectedTouchPos(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.llDetectedTouchPos(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llDetectedTouchNormal(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.llDetectedTouchNormal(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llDetectedTouchBinormal(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.llDetectedTouchBinormal(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llDetectedTouchST(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.llDetectedTouchST(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSHA1String(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llSHA1String(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetFreeURLs(SyscallShim self)
        {

            int ret = self._systemAPI.llGetFreeURLs();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llRequestURL(SyscallShim self)
        {

            string ret = self._systemAPI.llRequestURL();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llRequestSecureURL(SyscallShim self)
        {

            string ret = self._systemAPI.llRequestSecureURL();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llReleaseURL(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llReleaseURL(p0);

        }

        static private void Shim_llHTTPResponse(SyscallShim self)
        {
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llHTTPResponse(p0, p1, p2);

        }

        static private void Shim_llGetHTTPHeader(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llGetHTTPHeader(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetPrimMediaParams(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llSetPrimMediaParams(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetLinkMedia(SyscallShim self)
        {
            LSLList p2 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llSetLinkMedia(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetPrimMediaParams(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.llGetPrimMediaParams(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetLinkMedia(SyscallShim self)
        {
            LSLList p2 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.llGetLinkMedia(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llClearPrimMedia(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llClearPrimMedia(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llClearLinkMedia(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop()); 
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llClearPrimMedia(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetLinkPrimitiveParamsFast(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetLinkPrimitiveParamsFast(p0, p1);

        }

        static private void Shim_llGetLinkPrimitiveParams(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.llGetLinkPrimitiveParams(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llLinkParticleSystem(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llLinkParticleSystem(p0, p1);

        }

        static private void Shim_llSetLinkTextureAnim(SyscallShim self)
        {
            float p7 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p6 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p5 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            int p4 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetLinkTextureAnim(p0, p1, p2, p3, p4, p5, p6, p7);

        }

        static private void Shim_llGetLinkNumberOfSides(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llGetLinkNumberOfSides(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetUsername(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llGetUsername(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llRequestUsername(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.llRequestUsername(p0);
            });
        }

        static private void Shim_llGetDisplayName(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llGetDisplayName(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llRequestDisplayName(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.llRequestDisplayName(p0);
            });
        }

        static private void Shim_iwMakeNotecard(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.iwMakeNotecard(p0, p1);
            });
        }

        static private void Shim_iwAvatarName2Key(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.iwAvatarName2Key(p0, p1);
            });
        }

        static private void Shim_iwLinkTargetOmega(SyscallShim self)
        {
            float p3 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p2 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.iwLinkTargetOmega(p0, p1, p2, p3);
        }

        static private void Shim_llGetParcelMusicURL(SyscallShim self)
        {
            string ret = self._systemAPI.llGetParcelMusicURL();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetRegionPos(SyscallShim self)
        {
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llSetRegionPos(p0);
            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwGetLinkInventoryNumber(SyscallShim self)
        {
            int type = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int linknumber = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.iwGetLinkInventoryNumber(linknumber, type);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwGetLinkInventoryType(SyscallShim self)
        {
            string name = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int linknumber = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.iwGetLinkInventoryType(linknumber, name);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwGetLinkInventoryPermMask(SyscallShim self)
        {
            int mask = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string item = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int linknumber = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.iwGetLinkInventoryPermMask(linknumber, item, mask);
            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwGetLinkInventoryName(SyscallShim self)
        {
            int number = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int type = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int linknumber = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.iwGetLinkInventoryName(linknumber, type, number);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwGetLinkInventoryKey(SyscallShim self)
        {
            string name = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int linknumber = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.iwGetLinkInventoryKey(linknumber, name);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwGetLinkInventoryCreator(SyscallShim self)
        {
            string item = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int linknumber = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.iwGetLinkInventoryCreator(linknumber, item);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwGetLinkInventoryDesc(SyscallShim self)
        {
            string item = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int linknumber = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.iwGetLinkInventoryDesc(linknumber, item);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwGetLinkInventoryLastOwner(SyscallShim self)
        {
            string item = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int linknumber = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.iwGetLinkInventoryLastOwner(linknumber, item);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwSHA256String(SyscallShim self)
        {
            string src = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.iwSHA256String(src);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwTeleportAgent(SyscallShim self)
        {
            Vector3 lookat = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 pos = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            string region = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string agent = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.iwTeleportAgent(agent, region, pos, lookat);
            });
            
        }

        static private void Shim_llAvatarOnLinkSitTarget(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llAvatarOnLinkSitTarget(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwGetLastOwner(SyscallShim self)
        {
            string ret = self._systemAPI.iwGetLastOwner();
            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwRemoveLinkInventory(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.iwRemoveLinkInventory(p0, p1);
        }

        static private void Shim_iwGiveLinkInventory(SyscallShim self)
        {
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.iwGiveLinkInventory(p0, p1, p2);
            });
        }

        static private void Shim_iwGiveLinkInventoryList(SyscallShim self)
        {
            LSLList p3 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.iwGiveLinkInventoryList(p0, p1, p2, p3);
            });
        }

        static private void Shim_iwGetNotecardSegment(SyscallShim self)
        {
            int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.iwGetNotecardSegment(p0, p1, p2, p3);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwGetLinkNumberOfNotecardLines(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.iwGetLinkNumberOfNotecardLines(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwGetLinkNotecardLine(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.iwGetLinkNotecardLine(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwGetLinkNotecardSegment(SyscallShim self)
        {
            int p4 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.iwGetLinkNotecardSegment(p0, p1, p2, p3, p4);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwActiveGroup(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.iwActiveGroup(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwAvatarOnLink(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.iwAvatarOnLink(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llRegionSayTo(SyscallShim self)
        {
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llRegionSayTo(p0, p1, p2);
        }

        static private void Shim_llGenerateKey(SyscallShim self)
        {
            string ret = self._systemAPI.llGenerateKey();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetEnv(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llGetEnv(p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetAngularVelocity(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetAngularVelocity(p0, p1);
        }

        static private void Shim_llSetPhysicsMaterial(SyscallShim self)
        {
            float p4 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p3 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p2 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetPhysicsMaterial(p0, p1, p2, p3, p4);
        }

        static private void Shim_llSetVelocity(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetVelocity(p0, p1);
        }

        static private void Shim_iwRezObject(SyscallShim self)
        {
            int p4 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            Quaternion p3 = ConvToQuat(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p2 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.iwRezObject(p0, p1, p2, p3, p4);
            });
        }

        static private void Shim_iwRezAtRoot(SyscallShim self)
        {
            int p4 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            Quaternion p3 = ConvToQuat(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p2 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.iwRezAtRoot(p0, p1, p2, p3, p4);
            });
        }

        static private void Shim_iwRezPrim(SyscallShim self)
        {
            int p6 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            Quaternion p5 = ConvToQuat(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p4 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p3 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            LSLList p2 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.iwRezPrim(p0, p1, p2, p3, p4, p5, p6);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));

        }

        // LSLList llGetAgentList(int scope, LSLList options);
        static private void Shim_llGetAgentList(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.llGetAgentList(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        // LSLList iwGetAgentList(int scope, Vector3 minPos, Vector3 maxPos, LSLList paramList);
        static private void Shim_iwGetAgentList(SyscallShim self)
        {
            LSLList p3 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p2 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.iwGetAgentList(p0, p1, p2, p3);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetMemoryLimit(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llSetMemoryLimit(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetMemoryLimit(SyscallShim self)
        {
            int ret = self._systemAPI.llGetMemoryLimit();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llManageEstateAccess(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop()); 
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.llManageEstateAccess(p0, p1);
            });
        }

        static private void Shim_llLinkSitTarget(SyscallShim self)
        {
            Quaternion p2 = ConvToQuat(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llLinkSitTarget(p0, p1, p2);
        }

        static private void Shim_llGetMassMKS(SyscallShim self)
        {
            float ret = self._systemAPI.llGetMassMKS();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwGetObjectMassMKS(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.iwGetObjectMassMKS(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetLinkCamera(SyscallShim self)
        {
            Vector3 p2 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetLinkCamera(p0, p1, p2);

        }

        static private void Shim_iwSetGround(SyscallShim self)
        {
            float p4 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.iwSetGround(p0, p1, p2, p3, p4);
        }

        static private void Shim_llSetContentType(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetContentType(p0, p1);
        }

        static private void Shim_llJsonGetValue(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llJsonGetValue(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llJsonValueType(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llJsonValueType(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llJsonSetValue(SyscallShim self)
        {
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llJsonSetValue(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llList2Json(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop()); 
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llList2Json(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llJson2List(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.llJson2List(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwSetWind(SyscallShim self)
        {
            Vector3 p2 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            int     p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.iwSetWind(p0, p1, p2);
        }

        static private void Shim_iwHasParcelPowers(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.iwHasParcelPowers(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwGroundSurfaceNormal(SyscallShim self)
        {
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.iwGroundSurfaceNormal(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }


        static private void Shim_iwRequestAnimationData(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.iwRequestAnimationData(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llCastRay(SyscallShim self)
        {
            LSLList p2 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.llCastRay(p0,p1,p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llSetKeyframedMotion(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetKeyframedMotion(p0,p1);
        }

        static private void Shim_iwWind(SyscallShim self)
        {
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.iwWind(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llGetPhysicsMaterial(SyscallShim self)
        {
            LSLList ret = self._systemAPI.llGetPhysicsMaterial();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwGetLocalTime(SyscallShim self)
        {
            int ret = self._systemAPI.iwGetLocalTime();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwGetLocalTimeOffset(SyscallShim self)
        {
            int ret = self._systemAPI.iwGetLocalTimeOffset();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwFormatTime(SyscallShim self)
        {
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.iwFormatTime(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwCheckRezError(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.iwCheckRezError(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_botCreateBot(SyscallShim self)
        {
            int p4 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p3 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            
            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.botCreateBot(p0, p1, p2, p3, p4);
            });
        }

        static private void Shim_botAddTag(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botAddTag(p0, p1);
        }

        static private void Shim_botRemoveTag(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botRemoveTag(p0, p1);
        }

        static private void Shim_botGetBotsWithTag(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.botGetBotsWithTag(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_botRemoveBotsWithTag(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            
            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.botRemoveBotsWithTag(p0);
            });
        }

        static private void Shim_botRemoveBot(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            
            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.botRemoveBot(p0);
            });
        }

        static private void Shim_botPauseMovement(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botPauseMovement(p0);
        }

        static private void Shim_botResumeMovement(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botResumeMovement(p0);
        }

        static private void Shim_botWhisper(SyscallShim self)
        {
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botWhisper(p0, p1, p2);
        }

        static private void Shim_botSay(SyscallShim self)
        {
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botSay(p0, p1, p2);
        }

        static private void Shim_botShout(SyscallShim self)
        {
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botShout(p0, p1, p2);
        }

        static private void Shim_botStartTyping(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botStartTyping(p0);
        }

        static private void Shim_botStopTyping(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botStopTyping(p0);
        }

        static private void Shim_botSendInstantMessage(SyscallShim self)
        {
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.botSendInstantMessage(p0, p1, p2);
            });
        }

        static private void Shim_botSitObject(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botSitObject(p0, p1);
        }

        static private void Shim_botStandUp(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botStandUp(p0);
        }

        static private void Shim_botGetOwner(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.botGetOwner(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_botIsBot(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.botIsBot(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_botTouchObject(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botTouchObject(p0, p1);
        }

        static private void Shim_botSetMovementSpeed(SyscallShim self)
        {
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botSetMovementSpeed(p0, p1);
        }

        static private void Shim_botGetPos(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.botGetPos(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_botGetName(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.botGetName(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_botStartAnimation(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botStartAnimation(p0, p1);
        }

        static private void Shim_botStopAnimation(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botStopAnimation(p0, p1);
        }

        static private void Shim_botTeleportTo(SyscallShim self)
        {
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botTeleportTo(p0, p1);
        }

        static private void Shim_botChangeOwner(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botChangeOwner(p0, p1);
        }

        static private void Shim_botGetAllBotsInRegion(SyscallShim self)
        {
            LSLList ret = self._systemAPI.botGetAllBotsInRegion();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_botGetAllMyBotsInRegion(SyscallShim self)
        {
            LSLList ret = self._systemAPI.botGetAllMyBotsInRegion();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_botFollowAvatar(SyscallShim self)
        {
            LSLList p2 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.botFollowAvatar(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_botStopMovement(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botStopMovement(p0);
        }

        static private void Shim_botSetNavigationPoints(SyscallShim self)
        {
            LSLList p3 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            LSLList p2 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botSetNavigationPoints(p0, p1, p2, p3);
        }

        static private void Shim_botRegisterForNavigationEvents(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botRegisterForNavigationEvents(p0);
        }

        static private void Shim_botSetProfile(SyscallShim self)
        {
            string p6 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p5 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p4 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p3 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botSetProfile(p0, p1, p2, p3, p4, p5, p6);
        }

        static private void Shim_botSetRotation(SyscallShim self)
        {
            Quaternion p1 = ConvToQuat(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botSetRotation(p0, p1);
        }

        static private void Shim_botGiveInventory(SyscallShim self)
        {
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.botGiveInventory(p0, p1, p2);
            });
        }

        static private void Shim_botSensor(SyscallShim self)
        {
            float p5 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p4 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botSensor(p0, p1, p2, p3, p4, p5);
        }

        static private void Shim_botSensorRepeat(SyscallShim self)
        {
            float p6 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p5 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p4 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botSensorRepeat(p0, p1, p2, p3, p4, p5, p5);
        }

        static private void Shim_botSensorRemove(SyscallShim self)
        {
            self._systemAPI.botSensorRemove();
        }

        static private void Shim_iwDetectedBot(SyscallShim self)
        {
            string ret = self._systemAPI.iwDetectedBot();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_botListen(SyscallShim self)
        {
            string p4 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p3 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.botListen(p0, p1, p2, p3, p4);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_botRegisterForCollisionEvents(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botRegisterForCollisionEvents(p0);
        }

        static private void Shim_botDeregisterFromCollisionEvents(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botDeregisterFromCollisionEvents(p0);
        }

        static private void Shim_botDeregisterFromNavigationEvents(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botDeregisterFromNavigationEvents(p0);
        }

        static private void Shim_botSetOutfit(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            
            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.botSetOutfit(p0);
            });
        }

        static private void Shim_botRemoveOutfit(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            
            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.botRemoveOutfit(p0);
            });
        }

        static private void Shim_botChangeOutfit(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            
            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.botChangeOutfit(p0, p1);
            });
        }

        static private void Shim_botGetBotOutfits(SyscallShim self)
        {
            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.botGetBotOutfits();
            });
        }

        static private void Shim_botWanderWithin(SyscallShim self)
        {
            LSLList p4 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            float p3 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p2 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botWanderWithin(p0, p1, p2, p3, p4);
        }

        static private void Shim_botMessageLinked(SyscallShim self)
        {
            string p3 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botMessageLinked(p0, p1, p2, p3);
        }

        static private void Shim_botSetProfileParams(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.botSetProfileParams(p0, p1);
        }

        static private void Shim_botGetProfileParams(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.botGetProfileParams(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwGetAngularVelocity(SyscallShim self)
        {
            Vector3 ret = self._systemAPI.iwGetAngularVelocity();

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        // int iwGetAppearanceParam(string who, int which)
        static private void Shim_iwGetAppearanceParam(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.iwGetAppearanceParam(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwChar2Int(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.iwChar2Int(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwInt2Char(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.iwInt2Char(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwReplaceString(SyscallShim self)
        {

            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.iwReplaceString(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwFormatString(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.iwFormatString(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwMatchString(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.iwMatchString(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwStringCodec(SyscallShim self)
        {
            LSLList p3 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.iwStringCodec(p0, p1, p2, p3);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwMatchList(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.iwMatchList(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwColorConvert(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.iwColorConvert(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwNameToColor(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            Vector3 ret = self._systemAPI.iwNameToColor(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwVerifyType(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.iwVerifyType(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwGroupInvite(SyscallShim self)
        {
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.iwGroupInvite(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwGroupEject(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.iwGroupEject(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        // string iwGetAgentData(key id, integer data)
        static private void Shim_iwGetAgentData(SyscallShim self)
        {
            int data = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string id = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.iwGetAgentData(id, data);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        // integer iwIsPlusUser(key id)
        static private void Shim_iwIsPlusUser(SyscallShim self)
        {
            string id = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.iwIsPlusUser(id);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llAttachToAvatarTemp(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llAttachToAvatarTemp(p0);
        }

        // integer iwListIncludesElements(list src, list elements, integer any)
        static private void Shim_iwListIncludesElements(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.iwListIncludesElements(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        // string iwReverseString(string src);
        static private void Shim_iwReverseString(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.iwReverseString(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        // list iwReverseList(list src, integer stride)
        static private void Shim_iwReverseList(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.iwReverseList(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        // list iwSearchInventory(integer type, string pattern, integer matchtype)
        static private void Shim_iwSearchInventory(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.iwSearchInventory(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        // list iwSearchLinkInventory(integer link, integer type, string pattern, integer matchtype)
        static private void Shim_iwSearchLinkInventory(SyscallShim self)
        {
            int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.iwSearchLinkInventory(p0, p1, p2, p3);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        // integer iwIntRand(integer max);
        static private void Shim_iwIntRand(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.iwIntRand(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        // integer iwIntRandRange(integer min, integer max);
        static private void Shim_iwIntRandRange(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.iwIntRandRange(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        // float iwFloatRandRange(float min, float max);
        static private void Shim_iwFrandRange(SyscallShim self)
        {
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.iwFrandRange(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }


        // list botSearchBotOutfits(string pattern, integer matchType, integer start, integer end);
        static private void Shim_botSearchBotOutfits(SyscallShim self)
        {
            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._asyncCallDelegate(delegate ()
            {
                self._systemAPI.botSearchBotOutfits(p0, p1, p2, p3);
            });
        }

        // list iwListRemoveElements(list src, list elements, integer count);
        static private void Shim_iwListRemoveElements(SyscallShim self)
        {
            int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.iwListRemoveElements(p0, p1, p2, p3);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        // list iwListRemoveDuplicates(list src, list elements, integer count);
        static private void Shim_iwListRemoveDuplicates(SyscallShim self)
        {
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.iwListRemoveDuplicates(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        // iwStartLinkAnimation(integer link, string anim);
        static private void Shim_iwStartLinkAnimation(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.iwStartLinkAnimation(p0, p1);
        }

        // iwStopLinkAnimation(integer link, string anim);
        static private void Shim_iwStopLinkAnimation(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.iwStopLinkAnimation(p0, p1);
        }

        // integer iwClampInt(integer value, integer min, integer max);
        static private void Shim_iwClampInt(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.iwClampInt(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        // float iwClampFloat(float value, float min, float max);
        static private void Shim_iwClampFloat(SyscallShim self)
        {
            float p2 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());

            float ret = self._systemAPI.iwClampFloat(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        // list iwSearchLinksByName(string pattern, integer matchType, integer linksOnly);
        static private void Shim_iwSearchLinksByName(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.iwSearchLinksByName(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        // list iwSearchLinksByDesc(string pattern, integer matchType, integer linksOnly);
        static private void Shim_iwSearchLinksByDesc(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.iwSearchLinksByDesc(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        // integer botHasTag(key botID, string tag);
        static private void Shim_botHasTag(SyscallShim self)
        {
            string p1 = ConvToString(self.Interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self.Interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.botHasTag(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_botGetBotTags(SyscallShim self)
        {
            string p0 = ConvToString(self.Interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.botGetBotTags(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwValidateURL(SyscallShim self)
        {
            string p0 = ConvToString(self.Interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.iwValidateURL(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwRemoteLoadScriptPin(SyscallShim self)
		{
			int p4 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
			int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
			int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
			string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
			string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

			int ret = self._systemAPI.iwRemoteLoadScriptPin(p0, p1, p2, p3, p4);

			self._interpreter.SafeOperandsPush (ConvToLSLType (ret));
		}

        static private void Shim_iwDeliverInventory(SyscallShim self)
        {
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            //set the script to long running syscall and call the function async
            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate ()
            {
                self._systemAPI.iwDeliverInventory(p0, p1, p2);
            });
        }

        static private void Shim_iwDeliverInventoryList(SyscallShim self)
        {
            LSLList p3 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate ()
            {
                self._systemAPI.iwDeliverInventoryList(p0, p1, p2, p3);
            });
        }

        static private void Shim_llGetAttachedList(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            LSLList ret = self._systemAPI.llGetAttachedList(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llReturnObjectsByOwner(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llReturnObjectsByOwner(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llReturnObjectsByID(SyscallShim self)
        {
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.llReturnObjectsByID(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_llTransferLindenDollars(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.llTransferLindenDollars(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwGiveMoney(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.iwGiveMoney(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_iwStandTarget(SyscallShim self)
        {
            Quaternion p1 = ConvToQuat(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.iwStandTarget(p0, p1);
        }

        static private void Shim_iwLinkStandTarget(SyscallShim self)
        {
            Quaternion p2 = ConvToQuat(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.iwLinkStandTarget(p0, p1, p2);
        }

	    static private void Shim_llGetAnimationOverride(SyscallShim self)
	    {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
    
            string ret = self._systemAPI.llGetAnimationOverride(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

	    static private void Shim_llSetAnimationOverride(SyscallShim self)
	    {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._systemAPI.llSetAnimationOverride(p0, p1);
        }

        static private void Shim_llResetAnimationOverride(SyscallShim self)
            {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llResetAnimationOverride(p0);
            }

        static private void Shim_iwRezAt(SyscallShim self)
        {
            int p5 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            Quaternion p4 = ConvToQuat(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p3 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p2 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            string ret = null;
            self._asyncCallDelegate(delegate()
            {
                ret = self._systemAPI.iwRezAt(p0, p1, p2, p3, p4, p5);
            });

            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        // ── Batch 10: SL compatibility shims ──

        static private void Shim_llChar(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.llChar(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llOrd(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llOrd(p0, p1);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llComputeHash(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.llComputeHash(p0, p1);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llDetectedRezzer(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.llDetectedRezzer(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetInventoryDesc(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.llGetInventoryDesc(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetInventoryAcquireTime(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.llGetInventoryAcquireTime(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llName2Key(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.llName2Key(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetRegionTimeOfDay(SyscallShim self)
        {
            float ret = self._systemAPI.llGetRegionTimeOfDay();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetSimStats(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            float ret = self._systemAPI.llGetSimStats(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetSPMaxMemory(SyscallShim self)
        {
            int ret = self._systemAPI.llGetSPMaxMemory();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetObjectAnimationNames(SyscallShim self)
        {
            LSLList ret = self._systemAPI.llGetObjectAnimationNames();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetObjectLinkKey(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.llGetObjectLinkKey(p0, p1);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llDerezObject(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llDerezObject(p0);
        }

        // ── Batch 11: Medium-effort SL compatibility shims ──

        static private void Shim_llTeleportAgent(SyscallShim self)
        {
            Vector3 p3 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p2 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llTeleportAgent(p0, p1, p2, p3);
        }

        static private void Shim_llTeleportAgentGlobalCoords(SyscallShim self)
        {
            Vector3 p3 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p2 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llTeleportAgentGlobalCoords(p0, p1, p2, p3);
        }

        static private void Shim_llScaleByFactor(SyscallShim self)
        {
            float p0 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llScaleByFactor(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetMaxScaleFactor(SyscallShim self)
        {
            float ret = self._systemAPI.llGetMaxScaleFactor();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetMinScaleFactor(SyscallShim self)
        {
            float ret = self._systemAPI.llGetMinScaleFactor();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetCameraAspect(SyscallShim self)
        {
            float ret = self._systemAPI.llGetCameraAspect();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetCameraFOV(SyscallShim self)
        {
            float ret = self._systemAPI.llGetCameraFOV();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetAttachedListFiltered(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            LSLList ret = self._systemAPI.llGetAttachedListFiltered(p0, p1);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llIsFriend(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llIsFriend(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetVisualParams(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            LSLList ret = self._systemAPI.llGetVisualParams(p0, p1);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetNotecardLineSync(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.llGetNotecardLineSync(p0, p1);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llFindNotecardTextSync(SyscallShim self)
        {
            int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llFindNotecardTextSync(p0, p1, p2, p3);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llStartObjectAnimation(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llStartObjectAnimation(p0);
        }

        static private void Shim_llStopObjectAnimation(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llStopObjectAnimation(p0);
        }

        static private void Shim_llGetLinkSitFlags(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llGetLinkSitFlags(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llSetLinkSitFlags(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llSetLinkSitFlags(p0, p1);
        }

        // ── Batch 12: String/List/Crypto SL compatibility shims ──

        static private void Shim_llReplaceSubString(SyscallShim self)
        {
            int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.llReplaceSubString(p0, p1, p2, p3);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llListFindStrided(SyscallShim self)
        {
            int p4 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llListFindStrided(p0, p1, p2, p3, p4);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llList2ListSlice(SyscallShim self)
        {
            int p4 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            LSLList ret = self._systemAPI.llList2ListSlice(p0, p1, p2, p3, p4);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llSortListStrided(SyscallShim self)
        {
            int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            LSLList ret = self._systemAPI.llSortListStrided(p0, p1, p2, p3);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llListFindListNext(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llListFindListNext(p0, p1, p2);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llHMAC(SyscallShim self)
        {
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.llHMAC(p0, p1, p2);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llSHA256String(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.llSHA256String(p0, p1);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llLinkPlaySound(SyscallShim self)
        {
            int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            float p2 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llLinkPlaySound(p0, p1, p2, p3);
        }

        static private void Shim_llLinear2sRGB(SyscallShim self)
        {
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 ret = self._systemAPI.llLinear2sRGB(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llSRGB2Linear(SyscallShim self)
        {
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 ret = self._systemAPI.llSRGB2Linear(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llWorldPosToHUD(SyscallShim self)
        {
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 ret = self._systemAPI.llWorldPosToHUD(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        // ── Batch 13: Linkset Data shims ──

        static private void Shim_llLinksetDataAvailable(SyscallShim self)
        {
            int ret = self._systemAPI.llLinksetDataAvailable();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llLinksetDataCountKeys(SyscallShim self)
        {
            int ret = self._systemAPI.llLinksetDataCountKeys();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llLinksetDataRead(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.llLinksetDataRead(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llLinksetDataReadProtected(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.llLinksetDataReadProtected(p0, p1);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llLinksetDataWrite(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llLinksetDataWrite(p0, p1);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llLinksetDataWriteProtected(SyscallShim self)
        {
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llLinksetDataWriteProtected(p0, p1, p2);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llLinksetDataDelete(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llLinksetDataDelete(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llLinksetDataDeleteProtected(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llLinksetDataDeleteProtected(p0, p1);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llLinksetDataReset(SyscallShim self)
        {
            self._systemAPI.llLinksetDataReset();
        }

        static private void Shim_llLinksetDataDeleteFound(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            LSLList ret = self._systemAPI.llLinksetDataDeleteFound(p0, p1);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llLinksetDataCountFound(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llLinksetDataCountFound(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llLinksetDataListKeys(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList ret = self._systemAPI.llLinksetDataListKeys(p0, p1);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llLinksetDataFindKeys(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            LSLList ret = self._systemAPI.llLinksetDataFindKeys(p0, p1, p2);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }
		
		// ── Tier 1: EEP / Environment ──

        static private void Shim_llGetDayLength(SyscallShim self)
        {
            int ret = self._systemAPI.llGetDayLength();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetDayOffset(SyscallShim self)
        {
            int ret = self._systemAPI.llGetDayOffset();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetRegionDayLength(SyscallShim self)
        {
            int ret = self._systemAPI.llGetRegionDayLength();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetRegionDayOffset(SyscallShim self)
        {
            int ret = self._systemAPI.llGetRegionDayOffset();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetMoonDirection(SyscallShim self)
        {
            Vector3 ret = self._systemAPI.llGetMoonDirection();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetMoonRotation(SyscallShim self)
        {
            Quaternion ret = self._systemAPI.llGetMoonRotation();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetSunRotation(SyscallShim self)
        {
            Quaternion ret = self._systemAPI.llGetSunRotation();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetRegionSunDirection(SyscallShim self)
        {
            Vector3 ret = self._systemAPI.llGetRegionSunDirection();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetRegionSunRotation(SyscallShim self)
        {
            Quaternion ret = self._systemAPI.llGetRegionSunRotation();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetRegionMoonDirection(SyscallShim self)
        {
            Vector3 ret = self._systemAPI.llGetRegionMoonDirection();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetRegionMoonRotation(SyscallShim self)
        {
            Quaternion ret = self._systemAPI.llGetRegionMoonRotation();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetRegionLightDir(SyscallShim self)
        {
            Vector3 ret = self._systemAPI.llGetRegionLightDir();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }
		static private void Shim_llGetRenderMaterial(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.llGetRenderMaterial(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llSetRenderMaterial(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llSetRenderMaterial(p0, p1);
        }

        static private void Shim_llIsLinkGLTFMaterial(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llIsLinkGLTFMaterial(p0, p1);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llSetLinkGLTFOverrides(SyscallShim self)
        {
            LSLList p2 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llSetLinkGLTFOverrides(p0, p1, p2);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llRenderMaterial(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llRenderMaterial(p0);
        }

        static private void Shim_llGetHealth(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            float ret = self._systemAPI.llGetHealth(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llAdjustDamage(SyscallShim self)
        {
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llAdjustDamage(p0, p1);
        }

        static private void Shim_llSetHealth(SyscallShim self)
        {
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llSetHealth(p0, p1);
        }

		static private void Shim_llGetClosestNavPoint(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            LSLList ret = self._systemAPI.llGetClosestNavPoint(p0, p1);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llGetStaticPath(SyscallShim self)
        {
            LSLList p3 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            float p2 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            LSLList ret = self._systemAPI.llGetStaticPath(p0, p1, p2, p3);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llExecCharacterCmd(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llExecCharacterCmd(p0, p1);
        }

        static private void Shim_llDeleteCharacter(SyscallShim self)
        {
            self._systemAPI.llDeleteCharacter();
        }
		// -- Pathfinding Character Shims (621-628) --
		static private void Shim_llCreateCharacter(SyscallShim self)
		{
			LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
			self._systemAPI.llCreateCharacter(p0);
		}
		static private void Shim_llNavigateTo(SyscallShim self)
		{
			LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
			Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
			self._systemAPI.llNavigateTo(p0, p1);
		}
		static private void Shim_llUpdateCharacter(SyscallShim self)
		{
			LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
			self._systemAPI.llUpdateCharacter(p0);
		}
		static private void Shim_llPursue(SyscallShim self)
		{
			LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
			string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
			self._systemAPI.llPursue(p0, p1);
		}
		static private void Shim_llEvade(SyscallShim self)
		{
			LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
			string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
			self._systemAPI.llEvade(p0, p1);
		}
		static private void Shim_llFleeFrom(SyscallShim self)
		{
			LSLList p2 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
			float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
			Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
			self._systemAPI.llFleeFrom(p0, p1, p2);
		}
		static private void Shim_llWanderWithin(SyscallShim self)
		{
			LSLList p2 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
			Vector3 p1 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
			Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
			self._systemAPI.llWanderWithin(p0, p1, p2);
		}
		static private void Shim_llPatrolPoints(SyscallShim self)
		{
			LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
			LSLList p0 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
			self._systemAPI.llPatrolPoints(p0, p1);
		}
		static private void Shim_llCreateKeyValue(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llCreateKeyValue(p0, p1);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llReadKeyValue(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.llReadKeyValue(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llUpdateKeyValue(SyscallShim self)
        {
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llUpdateKeyValue(p0, p1, p2);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llDeleteKeyValue(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llDeleteKeyValue(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llKeyCountKeyValue(SyscallShim self)
        {
            int ret = self._systemAPI.llKeyCountKeyValue();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llKeysKeyValue(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList ret = self._systemAPI.llKeysKeyValue(p0, p1);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llDataSizeKeyValue(SyscallShim self)
        {
            int ret = self._systemAPI.llDataSizeKeyValue();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llClearKeyValue(SyscallShim self)
        {
            int ret = self._systemAPI.llClearKeyValue();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llCreateKeyValueSL(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.llCreateKeyValueSL(p0, p1);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llReadKeyValueSL(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.llReadKeyValueSL(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        static private void Shim_llUpdateKeyValueSL(SyscallShim self)
        {
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.llUpdateKeyValueSL(p0, p1, p2);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }
		static private void Shim_llSignRSA(SyscallShim self)
        {
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.llSignRSA(p0, p1, p2);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }
		static private void Shim_llVerifyRSA(SyscallShim self)
        {
            string p3 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llVerifyRSA(p0, p1, p2, p3);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }
		static private void Shim_llGetEnvironment(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            LSLList ret = self._systemAPI.llGetEnvironment(p0, p1);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }
		static private void Shim_llSetEnvironment(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llSetEnvironment(p0, p1);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }
		static private void Shim_llReplaceEnvironment(SyscallShim self)
        {
            int p4 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p3 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            Vector3 p0 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llReplaceEnvironment(p0, p1, p2, p3, p4);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }
		static private void Shim_llRequestUserKey(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.llRequestUserKey(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }
		static private void Shim_llSetAgentEnvironment(SyscallShim self)
        {
            LSLList p2 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llSetAgentEnvironment(p0, p1, p2);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }
		static private void Shim_llReplaceAgentEnvironment(SyscallShim self)
        {
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llReplaceAgentEnvironment(p0, p1, p2);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }
		static private void Shim_llGetExperienceErrorMessage(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.llGetExperienceErrorMessage(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }
		static private void Shim_llSetAgentRot(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            Quaternion p0 = ConvToQuat(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llSetAgentRot(p0, p1);
        }
		static private void Shim_llOpenFloater(SyscallShim self)
        {
            LSLList p2 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llOpenFloater(p0, p1, p2);
        }
		static private void Shim_llCloseFloater(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llCloseFloater(p0);
        }
		static private void Shim_llSitOnLink(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llSitOnLink(p0, p1);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }
		static private void Shim_llRezObjectWithParams(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llRezObjectWithParams(p0, p1);
        }
		static private void Shim_llGetMaterialOverride(SyscallShim self)
        {
            LSLList p1 = ConvToLSLList(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.llGetMaterialOverride(p0, p1);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

		// ── Phase 31: 19 new shim methods (643–661) ──

		static private void Shim_llHash(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llHash(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }
		static private void Shim_llLinkAdjustSoundVolume(SyscallShim self)
        {
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llLinkAdjustSoundVolume(p0, p1);
        }
		static private void Shim_llLinkSetSoundQueueing(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llLinkSetSoundQueueing(p0, p1);
        }
		static private void Shim_llLinkSetSoundRadius(SyscallShim self)
        {
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llLinkSetSoundRadius(p0, p1);
        }
		static private void Shim_llLinkStopSound(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llLinkStopSound(p0);
        }
		static private void Shim_llScriptProfiler(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llScriptProfiler(p0);
        }
		static private void Shim_llGiveAgentInventory(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llGiveAgentInventory(p0, p1);
        }
		static private void Shim_llMapBeacon(SyscallShim self)
        {
            Vector3 p2 = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llMapBeacon(p0, p1, p2);
        }
		static private void Shim_llGetStartString(SyscallShim self)
        {
            string ret = self._systemAPI.llGetStartString();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }
		static private void Shim_llSetGroundTexture(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llSetGroundTexture(p0, p1);
        }
		static private void Shim_llTargetedEmail(SyscallShim self)
        {
            string p3 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llTargetedEmail(p0, p1, p2, p3);
        }
		static private void Shim_llTransferOwnership(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llTransferOwnership(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }
		static private void Shim_llDetectedDamage(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            float ret = self._systemAPI.llDetectedDamage(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }
		static private void Shim_llDamage(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            float p1 = ConvToFloat(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llDamage(p0, p1, p2);
        }
		static private void Shim_llSetLinkRenderMaterial(SyscallShim self)
        {
            int p2 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llSetLinkRenderMaterial(p0, p1, p2);
        }
		static private void Shim_llXorBase64(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.llXorBase64(p0, p1);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }
		static private void Shim_llRequestExperiencePermissions(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            self._systemAPI.llRequestExperiencePermissions(p0, p1);
        }
		static private void Shim_llAgentInExperience(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            int ret = self._systemAPI.llAgentInExperience(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }
		static private void Shim_llGetExperienceDetails(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            LSLList ret = self._systemAPI.llGetExperienceDetails(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

		// ── Phase 33: 5 new shim methods (662–666) ──

		static private void Shim_llGetPayPrice(SyscallShim self)
        {
            LSLList ret = self._systemAPI.llGetPayPrice();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }
		static private void Shim_llGetAgentRot(SyscallShim self)
        {
            Quaternion ret = self._systemAPI.llGetAgentRot();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }
		static private void Shim_llGetGroundTexture(SyscallShim self)
        {
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string ret = self._systemAPI.llGetGroundTexture(p0);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }
		static private void Shim_llGetVehicleFlags(SyscallShim self)
        {
            int ret = self._systemAPI.llGetVehicleFlags();
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }
		static private void Shim_llGetLinkGLTFOverrides(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            int p0 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            LSLList ret = self._systemAPI.llGetLinkGLTFOverrides(p0, p1);
            self._interpreter.ScriptState.Operands.Push(ConvToLSLType(ret));
        }

        // ── Phase 34: Bot Persistence shim methods (667–671) ──

        static private void Shim_botSetPersistent(SyscallShim self)
        {
            int p1 = ConvToInt(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.botSetPersistent(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_botRemovePersistent(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.botRemovePersistent(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_botIsPersistent(SyscallShim self)
        {
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.botIsPersistent(p0);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_botGetPersistentData(SyscallShim self)
        {
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            string ret = self._systemAPI.botGetPersistentData(p0, p1);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        static private void Shim_botSetPersistentData(SyscallShim self)
        {
            string p2 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p1 = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string p0 = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            int ret = self._systemAPI.botSetPersistentData(p0, p1, p2);

            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }

        // OSSL: void osTeleportAgent(string agent, string region, vector pos, vector lookat)
        // Mirrors Shim_iwTeleportAgent — same args, async dispatch.
        static private void Shim_osTeleportAgent(SyscallShim self)
        {
            Vector3 lookat = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            Vector3 pos = ConvToVector(self._interpreter.ScriptState.Operands.Pop());
            string region = ConvToString(self._interpreter.ScriptState.Operands.Pop());
            string agent = ConvToString(self._interpreter.ScriptState.Operands.Pop());

            self._interpreter.ScriptState.RunState = VM.RuntimeState.Status.Syscall;

            self._asyncCallDelegate(delegate()
            {
                self._systemAPI.osTeleportAgent(agent, region, pos, lookat);
            });
        }

        // OSSL: list osGetAvatarList()
        // Synchronous region-local enumeration; no params, returns LSLList.
        static private void Shim_osGetAvatarList(SyscallShim self)
        {
            LSLList ret = self._systemAPI.osGetAvatarList();
            self._interpreter.SafeOperandsPush(ConvToLSLType(ret));
        }
    }
}