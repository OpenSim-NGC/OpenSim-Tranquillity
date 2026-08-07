using System;
using System.Collections.Generic;

namespace InWorldz.Phlox.Types
{
	public class Defaults
	{
		static public Dictionary<string, FunctionSig> SystemMethods = new Dictionary<string, FunctionSig>()
		{
			{"llSin", new FunctionSig {
				FunctionName =  "llSin",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {VarType.Float},
				ParamNames = new string[] {"theta"},
				TableIndex = 0
			}},
			{"llCos", new FunctionSig {
				FunctionName =  "llCos",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {VarType.Float},
				ParamNames = new string[] {"theta"},
				TableIndex = 1
			}},
			{"llTan", new FunctionSig {
				FunctionName =  "llTan",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {VarType.Float},
				ParamNames = new string[] {"theta"},
				TableIndex = 2
			}},
			{"llAtan2", new FunctionSig {
				FunctionName =  "llAtan2",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {VarType.Float, VarType.Float},
				ParamNames = new string[] {"y", "x"},
				TableIndex = 3
			}},
			{"llSqrt", new FunctionSig {
				FunctionName =  "llSqrt",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {VarType.Float},
				ParamNames = new string[] {"val"},
				TableIndex = 4
			}},
			{"llPow", new FunctionSig {
				FunctionName =  "llPow",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {VarType.Float, VarType.Float},
				ParamNames = new string[] {"base", "exponent"},
				TableIndex = 5
			}},
			{"llAbs", new FunctionSig {
				FunctionName =  "llAbs",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"val"},
				TableIndex = 6
			}},
			{"llFabs", new FunctionSig {
				FunctionName =  "llFabs",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {VarType.Float},
				ParamNames = new string[] {"val"},
				TableIndex = 7
			}},
			{"llFrand", new FunctionSig {
				FunctionName =  "llFrand",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {VarType.Float},
				ParamNames = new string[] {"mag"},
				TableIndex = 8
			}},
			{"llFloor", new FunctionSig {
				FunctionName =  "llFloor",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Float},
				ParamNames = new string[] {"val"},
				TableIndex = 9
			}},
			{"llCeil", new FunctionSig {
				FunctionName =  "llCeil",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Float},
				ParamNames = new string[] {"val"},
				TableIndex = 10
			}},
			{"llRound", new FunctionSig {
				FunctionName =  "llRound",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Float},
				ParamNames = new string[] {"val"},
				TableIndex = 11
			}},
			{"llVecMag", new FunctionSig {
				FunctionName =  "llVecMag",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {VarType.Vector},
				ParamNames = new string[] {"v"},
				TableIndex = 12
			}},
			{"llVecNorm", new FunctionSig {
				FunctionName =  "llVecNorm",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.Vector},
				ParamNames = new string[] {"v"},
				TableIndex = 13
			}},
			{"llVecDist", new FunctionSig {
				FunctionName =  "llVecDist",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {VarType.Vector, VarType.Vector},
				ParamNames = new string[] {"v1", "v2"},
				TableIndex = 14
			}},
			{"llRot2Euler", new FunctionSig {
				FunctionName =  "llRot2Euler",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.Rotation},
				ParamNames = new string[] {"q"},
				TableIndex = 15
			}},
			{"llEuler2Rot", new FunctionSig {
				FunctionName =  "llEuler2Rot",
				ReturnType = VarType.Rotation,
				ParamTypes = new VarType[] {VarType.Vector},
				ParamNames = new string[] {"v"},
				TableIndex = 16
			}},
			{"llAxes2Rot", new FunctionSig {
				FunctionName =  "llAxes2Rot",
				ReturnType = VarType.Rotation,
				ParamTypes = new VarType[] {VarType.Vector, VarType.Vector, VarType.Vector},
				ParamNames = new string[] {"fwd", "left", "up"},
				TableIndex = 17
			}},
			{"llRot2Fwd", new FunctionSig {
				FunctionName =  "llRot2Fwd",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.Rotation},
				ParamNames = new string[] {"q"},
				TableIndex = 18
			}},
			{"llRot2Left", new FunctionSig {
				FunctionName =  "llRot2Left",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.Rotation},
				ParamNames = new string[] {"q"},
				TableIndex = 19
			}},
			{"llRot2Up", new FunctionSig {
				FunctionName =  "llRot2Up",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.Rotation},
				ParamNames = new string[] {"q"},
				TableIndex = 20
			}},
			{"llRotBetween", new FunctionSig {
				FunctionName =  "llRotBetween",
				ReturnType = VarType.Rotation,
				ParamTypes = new VarType[] {VarType.Vector, VarType.Vector},
				ParamNames = new string[] {"v1", "v2"},
				TableIndex = 21
			}},
			{"llWhisper", new FunctionSig {
				FunctionName =  "llWhisper",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.String},
				ParamNames = new string[] {"channel", "msg"},
				TableIndex = 22
			}},
			{"llSay", new FunctionSig {
				FunctionName =  "llSay",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.String},
				ParamNames = new string[] {"channel", "msg"},
				TableIndex = 23
			}},
			{"llShout", new FunctionSig {
				FunctionName =  "llShout",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.String},
				ParamNames = new string[] {"channel", "msg"},
				TableIndex = 24
			}},
			{"llListen", new FunctionSig {
				FunctionName =  "llListen",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Integer, VarType.String, VarType.Key, VarType.String},
				ParamNames = new string[] {"channel", "name", "id", "msg"},
				TableIndex = 25
			}},
			{"llListenControl", new FunctionSig {
				FunctionName =  "llListenControl",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Integer},
				ParamNames = new string[] {"number", "active"},
				TableIndex = 26
			}},
			{"llListenRemove", new FunctionSig {
				FunctionName =  "llListenRemove",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"number"},
				TableIndex = 27
			}},
			{"llSensor", new FunctionSig {
				FunctionName =  "llSensor",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String, VarType.Key, VarType.Integer, VarType.Float, VarType.Float},
				ParamNames = new string[] {"name", "id", "type", "range", "arc"},
				TableIndex = 28
			}},
			{"llSensorRepeat", new FunctionSig {
				FunctionName =  "llSensorRepeat",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String, VarType.Key, VarType.Integer, VarType.Float, VarType.Float, VarType.Float},
				ParamNames = new string[] {"name", "id", "type", "range", "arc", "rate"},
				TableIndex = 29
			}},
			{"llSensorRemove", new FunctionSig {
				FunctionName =  "llSensorRemove",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 30
			}},
			{"llDetectedName", new FunctionSig {
				FunctionName =  "llDetectedName",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"number"},
				TableIndex = 31
			}},
			{"llDetectedKey", new FunctionSig {
				FunctionName =  "llDetectedKey",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"number"},
				TableIndex = 32
			}},
			{"llDetectedOwner", new FunctionSig {
				FunctionName =  "llDetectedOwner",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"number"},
				TableIndex = 33
			}},
			{"llDetectedType", new FunctionSig {
				FunctionName =  "llDetectedType",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"number"},
				TableIndex = 34
			}},
			{"llDetectedPos", new FunctionSig {
				FunctionName =  "llDetectedPos",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"number"},
				TableIndex = 35
			}},
			{"llDetectedVel", new FunctionSig {
				FunctionName =  "llDetectedVel",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"number"},
				TableIndex = 36
			}},
			{"llDetectedGrab", new FunctionSig {
				FunctionName =  "llDetectedGrab",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"number"},
				TableIndex = 37
			}},
			{"llDetectedRot", new FunctionSig {
				FunctionName =  "llDetectedRot",
				ReturnType = VarType.Rotation,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"number"},
				TableIndex = 38
			}},
			{"llDetectedGroup", new FunctionSig {
				FunctionName =  "llDetectedGroup",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"number"},
				TableIndex = 39
			}},
			{"llDetectedLinkNumber", new FunctionSig {
				FunctionName =  "llDetectedLinkNumber",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"number"},
				TableIndex = 40
			}},
			{"llDie", new FunctionSig {
				FunctionName =  "llDie",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 41
			}},
			{"llGround", new FunctionSig {
				FunctionName =  "llGround",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {VarType.Vector},
				ParamNames = new string[] {"offset"},
				TableIndex = 42
			}},
			{"llCloud", new FunctionSig {
				FunctionName =  "llCloud",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {VarType.Vector},
				ParamNames = new string[] {"offset"},
				TableIndex = 43
			}},
			{"llWind", new FunctionSig {
				FunctionName =  "llWind",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.Vector},
				ParamNames = new string[] {"offset"},
				TableIndex = 44
			}},
			{"llSetStatus", new FunctionSig {
				FunctionName =  "llSetStatus",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Integer},
				ParamNames = new string[] {"status", "value"},
				TableIndex = 45
			}},
			{"llGetStatus", new FunctionSig {
				FunctionName =  "llGetStatus",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"status"},
				TableIndex = 46
			}},
			{"llSetScale", new FunctionSig {
				FunctionName =  "llSetScale",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Vector},
				ParamNames = new string[] {"scale"},
				TableIndex = 47
			}},
			{"llGetScale", new FunctionSig {
				FunctionName =  "llGetScale",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 48
			}},
			{"llSetColor", new FunctionSig {
				FunctionName =  "llSetColor",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Vector, VarType.Integer},
				ParamNames = new string[] {"color", "face"},
				TableIndex = 49
			}},
			{"llGetAlpha", new FunctionSig {
				FunctionName =  "llGetAlpha",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"face"},
				TableIndex = 50
			}},
			{"llSetAlpha", new FunctionSig {
				FunctionName =  "llSetAlpha",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Float, VarType.Integer},
				ParamNames = new string[] {"alpha", "face"},
				TableIndex = 51
			}},
			{"llGetColor", new FunctionSig {
				FunctionName =  "llGetColor",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"face"},
				TableIndex = 52
			}},
			{"llSetTexture", new FunctionSig {
				FunctionName =  "llSetTexture",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String, VarType.Integer},
				ParamNames = new string[] {"texture", "face"},
				TableIndex = 53
			}},
			{"llScaleTexture", new FunctionSig {
				FunctionName =  "llScaleTexture",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Float, VarType.Float, VarType.Integer},
				ParamNames = new string[] {"u", "v", "face"},
				TableIndex = 54
			}},
			{"llOffsetTexture", new FunctionSig {
				FunctionName =  "llOffsetTexture",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Float, VarType.Float, VarType.Integer},
				ParamNames = new string[] {"u", "v", "face"},
				TableIndex = 55
			}},
			{"llRotateTexture", new FunctionSig {
				FunctionName =  "llRotateTexture",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Float, VarType.Integer},
				ParamNames = new string[] {"rot", "face"},
				TableIndex = 56
			}},
			{"llGetTexture", new FunctionSig {
				FunctionName =  "llGetTexture",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"face"},
				TableIndex = 57
			}},
			{"llSetPos", new FunctionSig {
				FunctionName =  "llSetPos",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Vector},
				ParamNames = new string[] {"pos"},
				TableIndex = 58
			}},
			{"llGetPos", new FunctionSig {
				FunctionName =  "llGetPos",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 59
			}},
			{"llGetLocalPos", new FunctionSig {
				FunctionName =  "llGetLocalPos",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 60
			}},
			{"llSetRot", new FunctionSig {
				FunctionName =  "llSetRot",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Rotation},
				ParamNames = new string[] {"rot"},
				TableIndex = 61
			}},
			{"llGetRot", new FunctionSig {
				FunctionName =  "llGetRot",
				ReturnType = VarType.Rotation,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 62
			}},
			{"llGetLocalRot", new FunctionSig {
				FunctionName =  "llGetLocalRot",
				ReturnType = VarType.Rotation,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 63
			}},
			{"llSetForce", new FunctionSig {
				FunctionName =  "llSetForce",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Vector, VarType.Integer},
				ParamNames = new string[] {"force", "local"},
				TableIndex = 64
			}},
			{"llGetForce", new FunctionSig {
				FunctionName =  "llGetForce",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 65
			}},
			{"llTarget", new FunctionSig {
				FunctionName =  "llTarget",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Vector, VarType.Float},
				ParamNames = new string[] {"position", "range"},
				TableIndex = 66
			}},
			{"llTargetRemove", new FunctionSig {
				FunctionName =  "llTargetRemove",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"number"},
				TableIndex = 67
			}},
			{"llRotTarget", new FunctionSig {
				FunctionName =  "llRotTarget",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Rotation, VarType.Float},
				ParamNames = new string[] {"rot", "error"},
				TableIndex = 68
			}},
			{"llRotTargetRemove", new FunctionSig {
				FunctionName =  "llRotTargetRemove",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"number"},
				TableIndex = 69
			}},
			{"llMoveToTarget", new FunctionSig {
				FunctionName =  "llMoveToTarget",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Vector, VarType.Float},
				ParamNames = new string[] {"target", "tau"},
				TableIndex = 70
			}},
			{"llStopMoveToTarget", new FunctionSig {
				FunctionName =  "llStopMoveToTarget",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 71
			}},
			{"llApplyImpulse", new FunctionSig {
				FunctionName =  "llApplyImpulse",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Vector, VarType.Integer},
				ParamNames = new string[] {"force", "local"},
				TableIndex = 72
			}},
			{"llApplyRotationalImpulse", new FunctionSig {
				FunctionName =  "llApplyRotationalImpulse",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Vector, VarType.Integer},
				ParamNames = new string[] {"force", "local"},
				TableIndex = 73
			}},
			{"llSetTorque", new FunctionSig {
				FunctionName =  "llSetTorque",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Vector, VarType.Integer},
				ParamNames = new string[] {"torque", "local"},
				TableIndex = 74
			}},
			{"llGetTorque", new FunctionSig {
				FunctionName =  "llGetTorque",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 75
			}},
			{"llSetForceAndTorque", new FunctionSig {
				FunctionName =  "llSetForceAndTorque",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Vector, VarType.Vector, VarType.Integer},
				ParamNames = new string[] {"force", "torque", "local"},
				TableIndex = 76
			}},
			{"llGetVel", new FunctionSig {
				FunctionName =  "llGetVel",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 77
			}},
			{"llGetAccel", new FunctionSig {
				FunctionName =  "llGetAccel",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 78
			}},
			{"llGetOmega", new FunctionSig {
				FunctionName =  "llGetOmega",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 79
			}},
			{"llGetTimeOfDay", new FunctionSig {
				FunctionName =  "llGetTimeOfDay",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 80
			}},
			{"llGetWallclock", new FunctionSig {
				FunctionName =  "llGetWallclock",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 81
			}},
			{"llGetTime", new FunctionSig {
				FunctionName =  "llGetTime",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 82
			}},
			{"llResetTime", new FunctionSig {
				FunctionName =  "llResetTime",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 83
			}},
			{"llGetAndResetTime", new FunctionSig {
				FunctionName =  "llGetAndResetTime",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 84
			}},
			{"llSound", new FunctionSig {
				FunctionName =  "llSound",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String, VarType.Float, VarType.Integer, VarType.Integer},
				ParamNames = new string[] {"sound", "volume", "queue", "loop"},
				TableIndex = 85
			}},
			{"llPlaySound", new FunctionSig {
				FunctionName =  "llPlaySound",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String, VarType.Float},
				ParamNames = new string[] {"sound", "volume"},
				TableIndex = 86
			}},
			{"llLoopSound", new FunctionSig {
				FunctionName =  "llLoopSound",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String, VarType.Float},
				ParamNames = new string[] {"sound", "volume"},
				TableIndex = 87
			}},
			{"llLoopSoundMaster", new FunctionSig {
				FunctionName =  "llLoopSoundMaster",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String, VarType.Float},
				ParamNames = new string[] {"sound", "volume"},
				TableIndex = 88
			}},
			{"llLoopSoundSlave", new FunctionSig {
				FunctionName =  "llLoopSoundSlave",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String, VarType.Float},
				ParamNames = new string[] {"sound", "volume"},
				TableIndex = 89
			}},
			{"llPlaySoundSlave", new FunctionSig {
				FunctionName =  "llPlaySoundSlave",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String, VarType.Float},
				ParamNames = new string[] {"sound", "volume"},
				TableIndex = 90
			}},
			{"llTriggerSound", new FunctionSig {
				FunctionName =  "llTriggerSound",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String, VarType.Float},
				ParamNames = new string[] {"sound", "volume"},
				TableIndex = 91
			}},
			{"llStopSound", new FunctionSig {
				FunctionName =  "llStopSound",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 92
			}},
			{"llPreloadSound", new FunctionSig {
				FunctionName =  "llPreloadSound",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"sound"},
				TableIndex = 93
			}},
			{"llGetSubString", new FunctionSig {
				FunctionName =  "llGetSubString",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.String, VarType.Integer, VarType.Integer},
				ParamNames = new string[] {"src", "start", "end"},
				TableIndex = 94
			}},
			{"llDeleteSubString", new FunctionSig {
				FunctionName =  "llDeleteSubString",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.String, VarType.Integer, VarType.Integer},
				ParamNames = new string[] {"src", "start", "end"},
				TableIndex = 95
			}},
			{"llInsertString", new FunctionSig {
				FunctionName =  "llInsertString",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.String, VarType.Integer, VarType.String},
				ParamNames = new string[] {"dst", "position", "src"},
				TableIndex = 96
			}},
			{"llToUpper", new FunctionSig {
				FunctionName =  "llToUpper",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"src"},
				TableIndex = 97
			}},
			{"llToLower", new FunctionSig {
				FunctionName =  "llToLower",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"src"},
				TableIndex = 98
			}},
			{"llGiveMoney", new FunctionSig {
				FunctionName =  "llGiveMoney",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Key, VarType.Integer},
				ParamNames = new string[] {"destination", "amount"},
				TableIndex = 99
			}},
			{"llMakeExplosion", new FunctionSig {
				FunctionName =  "llMakeExplosion",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Float, VarType.Float, VarType.Float, VarType.Float, VarType.String, VarType.Vector},
				ParamNames = new string[] {"particles", "scale", "vel", "lifetime", "arc", "texture", "offset"},
				TableIndex = 100
			}},
			{"llMakeFountain", new FunctionSig {
				FunctionName =  "llMakeFountain",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Float, VarType.Float, VarType.Float, VarType.Float, VarType.Integer, VarType.String, VarType.Vector, VarType.Float},
				ParamNames = new string[] {"particles", "scale", "vel", "lifetime", "arc", "bounce", "texture", "offset", "bounce_offset"},
				TableIndex = 101
			}},
			{"llMakeSmoke", new FunctionSig {
				FunctionName =  "llMakeSmoke",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Float, VarType.Float, VarType.Float, VarType.Float, VarType.String, VarType.Vector},
				ParamNames = new string[] {"particles", "scale", "vel", "lifetime", "arc", "texture", "offset"},
				TableIndex = 102
			}},
			{"llMakeFire", new FunctionSig {
				FunctionName =  "llMakeFire",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Float, VarType.Float, VarType.Float, VarType.Float, VarType.String, VarType.Vector},
				ParamNames = new string[] {"particles", "scale", "vel", "lifetime", "arc", "texture", "offset"},
				TableIndex = 103
			}},
			{"llRezObject", new FunctionSig {
				FunctionName =  "llRezObject",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String, VarType.Vector, VarType.Vector, VarType.Rotation, VarType.Integer},
				ParamNames = new string[] {"name", "pos", "vel", "rot", "param"},
				TableIndex = 104
			}},
			{"llLookAt", new FunctionSig {
				FunctionName =  "llLookAt",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Vector, VarType.Float, VarType.Float},
				ParamNames = new string[] {"target", "strength", "damping"},
				TableIndex = 105
			}},
			{"llStopLookAt", new FunctionSig {
				FunctionName =  "llStopLookAt",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 106
			}},
			{"llSetTimerEvent", new FunctionSig {
				FunctionName =  "llSetTimerEvent",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Float},
				ParamNames = new string[] {"sec"},
				TableIndex = 107
			}},
			{"llSleep", new FunctionSig {
				FunctionName =  "llSleep",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Float},
				ParamNames = new string[] {"sec"},
				TableIndex = 108
			}},
			{"llGetMass", new FunctionSig {
				FunctionName =  "llGetMass",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 109
			}},
			{"llCollisionFilter", new FunctionSig {
				FunctionName =  "llCollisionFilter",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String, VarType.Key, VarType.Integer},
				ParamNames = new string[] {"name", "id", "accept"},
				TableIndex = 110
			}},
			{"llTakeControls", new FunctionSig {
				FunctionName =  "llTakeControls",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Integer, VarType.Integer},
				ParamNames = new string[] {"controls", "accept", "pass_on"},
				TableIndex = 111
			}},
			{"llReleaseControls", new FunctionSig {
				FunctionName =  "llReleaseControls",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 112
			}},
			{"llAttachToAvatar", new FunctionSig {
				FunctionName =  "llAttachToAvatar",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"attach_point"},
				TableIndex = 113
			}},
			{"llDetachFromAvatar", new FunctionSig {
				FunctionName =  "llDetachFromAvatar",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 114
			}},
			{"llTakeCamera", new FunctionSig {
				FunctionName =  "llTakeCamera",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"avatar"},
				TableIndex = 115
			}},
			{"llReleaseCamera", new FunctionSig {
				FunctionName =  "llReleaseCamera",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"avatar"},
				TableIndex = 116
			}},
			{"llGetOwner", new FunctionSig {
				FunctionName =  "llGetOwner",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 117
			}},
			{"llInstantMessage", new FunctionSig {
				FunctionName =  "llInstantMessage",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key, VarType.String},
				ParamNames = new string[] {"user", "message"},
				TableIndex = 118
			}},
			{"llEmail", new FunctionSig {
				FunctionName =  "llEmail",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String, VarType.String, VarType.String},
				ParamNames = new string[] {"address", "subject", "message"},
				TableIndex = 119
			}},
			{"llGetNextEmail", new FunctionSig {
				FunctionName =  "llGetNextEmail",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String, VarType.String},
				ParamNames = new string[] {"address", "subject"},
				TableIndex = 120
			}},
			{"llGetKey", new FunctionSig {
				FunctionName =  "llGetKey",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 121
			}},
			{"llSetBuoyancy", new FunctionSig {
				FunctionName =  "llSetBuoyancy",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Float},
				ParamNames = new string[] {"buoyancy"},
				TableIndex = 122
			}},
			{"llSetHoverHeight", new FunctionSig {
				FunctionName =  "llSetHoverHeight",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Float, VarType.Integer, VarType.Float},
				ParamNames = new string[] {"height", "water", "tau"},
				TableIndex = 123
			}},
			{"llStopHover", new FunctionSig {
				FunctionName =  "llStopHover",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 124
			}},
			{"llMinEventDelay", new FunctionSig {
				FunctionName =  "llMinEventDelay",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Float},
				ParamNames = new string[] {"delay"},
				TableIndex = 125
			}},
			{"llSoundPreload", new FunctionSig {
				FunctionName =  "llSoundPreload",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"sound"},
				TableIndex = 126
			}},
			{"llRotLookAt", new FunctionSig {
				FunctionName =  "llRotLookAt",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Rotation, VarType.Float, VarType.Float},
				ParamNames = new string[] {"target", "strength", "damping"},
				TableIndex = 127
			}},
			{"llStringLength", new FunctionSig {
				FunctionName =  "llStringLength",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"str"},
				TableIndex = 128
			}},
			{"llStartAnimation", new FunctionSig {
				FunctionName =  "llStartAnimation",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"anim"},
				TableIndex = 129
			}},
			{"llStopAnimation", new FunctionSig {
				FunctionName =  "llStopAnimation",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"anim"},
				TableIndex = 130
			}},
			{"llPointAt", new FunctionSig {
				FunctionName =  "llPointAt",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Vector},
				ParamNames = new string[] {"pos"},
				TableIndex = 131
			}},
			{"llStopPointAt", new FunctionSig {
				FunctionName =  "llStopPointAt",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 132
			}},
			{"llTargetOmega", new FunctionSig {
				FunctionName =  "llTargetOmega",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Vector, VarType.Float, VarType.Float},
				ParamNames = new string[] {"axis", "spinrate", "gain"},
				TableIndex = 133
			}},
			{"llGetStartParameter", new FunctionSig {
				FunctionName =  "llGetStartParameter",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 134
			}},
			{"llGodLikeRezObject", new FunctionSig {
				FunctionName =  "llGodLikeRezObject",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key, VarType.Vector},
				ParamNames = new string[] {"name", "pos"},
				TableIndex = 135
			}},
			{"llRequestPermissions", new FunctionSig {
				FunctionName =  "llRequestPermissions",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key, VarType.Integer},
				ParamNames = new string[] {"agent", "perm"},
				TableIndex = 136
			}},
			{"llGetPermissionsKey", new FunctionSig {
				FunctionName =  "llGetPermissionsKey",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 137
			}},
			{"llGetPermissions", new FunctionSig {
				FunctionName =  "llGetPermissions",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 138
			}},
			{"llGetLinkNumber", new FunctionSig {
				FunctionName =  "llGetLinkNumber",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 139
			}},
			{"llSetLinkColor", new FunctionSig {
				FunctionName =  "llSetLinkColor",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Vector, VarType.Integer},
				ParamNames = new string[] {"linknumber", "color", "face"},
				TableIndex = 140
			}},
			{"llCreateLink", new FunctionSig {
				FunctionName =  "llCreateLink",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key, VarType.Integer},
				ParamNames = new string[] {"target", "parent"},
				TableIndex = 141
			}},
			{"llBreakLink", new FunctionSig {
				FunctionName =  "llBreakLink",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"linknum"},
				TableIndex = 142
			}},
			{"llBreakAllLinks", new FunctionSig {
				FunctionName =  "llBreakAllLinks",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 143
			}},
			{"llGetLinkKey", new FunctionSig {
				FunctionName =  "llGetLinkKey",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"linknumber"},
				TableIndex = 144
			}},
			{"llGetLinkName", new FunctionSig {
				FunctionName =  "llGetLinkName",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"linknumber"},
				TableIndex = 145
			}},
			{"llGetInventoryNumber", new FunctionSig {
				FunctionName =  "llGetInventoryNumber",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"type"},
				TableIndex = 146
			}},
			{"llGetInventoryName", new FunctionSig {
				FunctionName =  "llGetInventoryName",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Integer},
				ParamNames = new string[] {"type", "number"},
				TableIndex = 147
			}},
			{"llSetScriptState", new FunctionSig {
				FunctionName =  "llSetScriptState",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String, VarType.Integer},
				ParamNames = new string[] {"name", "run"},
				TableIndex = 148
			}},
			{"llGetEnergy", new FunctionSig {
				FunctionName =  "llGetEnergy",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 149
			}},
			{"llGiveInventory", new FunctionSig {
				FunctionName =  "llGiveInventory",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key, VarType.String},
				ParamNames = new string[] {"destination", "name"},
				TableIndex = 150
			}},
			{"llRemoveInventory", new FunctionSig {
				FunctionName =  "llRemoveInventory",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"name"},
				TableIndex = 151
			}},
			{"llSetText", new FunctionSig {
				FunctionName =  "llSetText",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String, VarType.Vector, VarType.Float},
				ParamNames = new string[] {"text", "color", "alpha"},
				TableIndex = 152
			}},
			{"llWater", new FunctionSig {
				FunctionName =  "llWater",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {VarType.Vector},
				ParamNames = new string[] {"offset"},
				TableIndex = 153
			}},
			{"llPassTouches", new FunctionSig {
				FunctionName =  "llPassTouches",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"pass"},
				TableIndex = 154
			}},
			{"llRequestAgentData", new FunctionSig {
				FunctionName =  "llRequestAgentData",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.Key, VarType.Integer},
				ParamNames = new string[] {"id", "data"},
				TableIndex = 155
			}},
			{"llRequestInventoryData", new FunctionSig {
				FunctionName =  "llRequestInventoryData",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"name"},
				TableIndex = 156
			}},
			{"llSetDamage", new FunctionSig {
				FunctionName =  "llSetDamage",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Float},
				ParamNames = new string[] {"damage"},
				TableIndex = 157
			}},
			{"llTeleportAgentHome", new FunctionSig {
				FunctionName =  "llTeleportAgentHome",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"id"},
				TableIndex = 158
			}},
			{"llModifyLand", new FunctionSig {
				FunctionName =  "llModifyLand",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Integer},
				ParamNames = new string[] {"action", "brush"},
				TableIndex = 159
			}},
			{"llCollisionSound", new FunctionSig {
				FunctionName =  "llCollisionSound",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String, VarType.Float},
				ParamNames = new string[] {"impact_sound", "impact_volume"},
				TableIndex = 160
			}},
			{"llCollisionSprite", new FunctionSig {
				FunctionName =  "llCollisionSprite",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"impact_sprite"},
				TableIndex = 161
			}},
			{"llGetAnimation", new FunctionSig {
				FunctionName =  "llGetAnimation",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"id"},
				TableIndex = 162
			}},
			{"llResetScript", new FunctionSig {
				FunctionName =  "llResetScript",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 163
			}},
			{"llMessageLinked", new FunctionSig {
				FunctionName =  "llMessageLinked",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Integer, VarType.String, VarType.Key},
				ParamNames = new string[] {"linknum", "num", "str", "id"},
				TableIndex = 164
			}},
			{"llPushObject", new FunctionSig {
				FunctionName =  "llPushObject",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key, VarType.Vector, VarType.Vector, VarType.Integer},
				ParamNames = new string[] {"id", "impulse", "ang_impulse", "local"},
				TableIndex = 165
			}},
			{"llPassCollisions", new FunctionSig {
				FunctionName =  "llPassCollisions",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"pass"},
				TableIndex = 166
			}},
			{"llGetScriptName", new FunctionSig {
				FunctionName =  "llGetScriptName",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 167
			}},
			{"llGetNumberOfSides", new FunctionSig {
				FunctionName =  "llGetNumberOfSides",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 168
			}},
			{"llAxisAngle2Rot", new FunctionSig {
				FunctionName =  "llAxisAngle2Rot",
				ReturnType = VarType.Rotation,
				ParamTypes = new VarType[] {VarType.Vector, VarType.Float},
				ParamNames = new string[] {"axis", "angle"},
				TableIndex = 169
			}},
			{"llRot2Axis", new FunctionSig {
				FunctionName =  "llRot2Axis",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.Rotation},
				ParamNames = new string[] {"rot"},
				TableIndex = 170
			}},
			{"llRot2Angle", new FunctionSig {
				FunctionName =  "llRot2Angle",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {VarType.Rotation},
				ParamNames = new string[] {"rot"},
				TableIndex = 171
			}},
			{"llAcos", new FunctionSig {
				FunctionName =  "llAcos",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {VarType.Float},
				ParamNames = new string[] {"val"},
				TableIndex = 172
			}},
			{"llAsin", new FunctionSig {
				FunctionName =  "llAsin",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {VarType.Float},
				ParamNames = new string[] {"val"},
				TableIndex = 173
			}},
			{"llAngleBetween", new FunctionSig {
				FunctionName =  "llAngleBetween",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {VarType.Rotation, VarType.Rotation},
				ParamNames = new string[] {"a", "b"},
				TableIndex = 174
			}},
			{"llGetInventoryKey", new FunctionSig {
				FunctionName =  "llGetInventoryKey",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"name"},
				TableIndex = 175
			}},
			{"llAllowInventoryDrop", new FunctionSig {
				FunctionName =  "llAllowInventoryDrop",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"add"},
				TableIndex = 176
			}},
			{"llGetSunDirection", new FunctionSig {
				FunctionName =  "llGetSunDirection",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 177
			}},
			{"llGetTextureOffset", new FunctionSig {
				FunctionName =  "llGetTextureOffset",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"face"},
				TableIndex = 178
			}},
			{"llGetTextureScale", new FunctionSig {
				FunctionName =  "llGetTextureScale",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"side"},
				TableIndex = 179
			}},
			{"llGetTextureRot", new FunctionSig {
				FunctionName =  "llGetTextureRot",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"side"},
				TableIndex = 180
			}},
			{"llSubStringIndex", new FunctionSig {
				FunctionName =  "llSubStringIndex",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.String, VarType.String},
				ParamNames = new string[] {"source", "pattern"},
				TableIndex = 181
			}},
			{"llGetOwnerKey", new FunctionSig {
				FunctionName =  "llGetOwnerKey",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"id"},
				TableIndex = 182
			}},
			{"llGetCenterOfMass", new FunctionSig {
				FunctionName =  "llGetCenterOfMass",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 183
			}},
			{"llListSort", new FunctionSig {
				FunctionName =  "llListSort",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.List, VarType.Integer, VarType.Integer},
				ParamNames = new string[] {"src", "stride", "ascending"},
				TableIndex = 184
			}},
			{"llGetListLength", new FunctionSig {
				FunctionName =  "llGetListLength",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.List},
				ParamNames = new string[] {"src"},
				TableIndex = 185
			}},
			{"llList2Integer", new FunctionSig {
				FunctionName =  "llList2Integer",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.List, VarType.Integer},
				ParamNames = new string[] {"src", "index"},
				TableIndex = 186
			}},
			{"llList2Float", new FunctionSig {
				FunctionName =  "llList2Float",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {VarType.List, VarType.Integer},
				ParamNames = new string[] {"src", "index"},
				TableIndex = 187
			}},
			{"llList2String", new FunctionSig {
				FunctionName =  "llList2String",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.List, VarType.Integer},
				ParamNames = new string[] {"src", "index"},
				TableIndex = 188
			}},
			{"llList2Key", new FunctionSig {
				FunctionName =  "llList2Key",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.List, VarType.Integer},
				ParamNames = new string[] {"src", "index"},
				TableIndex = 189
			}},
			{"llList2Vector", new FunctionSig {
				FunctionName =  "llList2Vector",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.List, VarType.Integer},
				ParamNames = new string[] {"src", "index"},
				TableIndex = 190
			}},
			{"llList2Rot", new FunctionSig {
				FunctionName =  "llList2Rot",
				ReturnType = VarType.Rotation,
				ParamTypes = new VarType[] {VarType.List, VarType.Integer},
				ParamNames = new string[] {"src", "index"},
				TableIndex = 191
			}},
			{"llList2List", new FunctionSig {
				FunctionName =  "llList2List",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.List, VarType.Integer, VarType.Integer},
				ParamNames = new string[] {"src", "start", "end"},
				TableIndex = 192
			}},
			{"llDeleteSubList", new FunctionSig {
				FunctionName =  "llDeleteSubList",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.List, VarType.Integer, VarType.Integer},
				ParamNames = new string[] {"src", "start", "end"},
				TableIndex = 193
			}},
			{"llGetListEntryType", new FunctionSig {
				FunctionName =  "llGetListEntryType",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.List, VarType.Integer},
				ParamNames = new string[] {"src", "index"},
				TableIndex = 194
			}},
			{"llList2CSV", new FunctionSig {
				FunctionName =  "llList2CSV",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.List},
				ParamNames = new string[] {"src"},
				TableIndex = 195
			}},
			{"llCSV2List", new FunctionSig {
				FunctionName =  "llCSV2List",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"src"},
				TableIndex = 196
			}},
			{"llListRandomize", new FunctionSig {
				FunctionName =  "llListRandomize",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.List, VarType.Integer},
				ParamNames = new string[] {"src", "stride"},
				TableIndex = 197
			}},
			{"llList2ListStrided", new FunctionSig {
				FunctionName =  "llList2ListStrided",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.List, VarType.Integer, VarType.Integer, VarType.Integer},
				ParamNames = new string[] {"src", "start", "end", "stride"},
				TableIndex = 198
			}},
			{"llGetRegionCorner", new FunctionSig {
				FunctionName =  "llGetRegionCorner",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 199
			}},
			{"llListInsertList", new FunctionSig {
				FunctionName =  "llListInsertList",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.List, VarType.List, VarType.Integer},
				ParamNames = new string[] {"dest", "src", "start"},
				TableIndex = 200
			}},
			{"llListFindList", new FunctionSig {
				FunctionName =  "llListFindList",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.List, VarType.List},
				ParamNames = new string[] {"src", "test"},
				TableIndex = 201
			}},
			{"llGetObjectName", new FunctionSig {
				FunctionName =  "llGetObjectName",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 202
			}},
			{"llSetObjectName", new FunctionSig {
				FunctionName =  "llSetObjectName",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"name"},
				TableIndex = 203
			}},
			{"llGetDate", new FunctionSig {
				FunctionName =  "llGetDate",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 204
			}},
			{"llEdgeOfWorld", new FunctionSig {
				FunctionName =  "llEdgeOfWorld",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Vector, VarType.Vector},
				ParamNames = new string[] {"pos", "dir"},
				TableIndex = 205
			}},
			{"llGetAgentInfo", new FunctionSig {
				FunctionName =  "llGetAgentInfo",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"id"},
				TableIndex = 206
			}},
			{"llAdjustSoundVolume", new FunctionSig {
				FunctionName =  "llAdjustSoundVolume",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Float},
				ParamNames = new string[] {"volume"},
				TableIndex = 207
			}},
			{"llSetSoundQueueing", new FunctionSig {
				FunctionName =  "llSetSoundQueueing",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"queue"},
				TableIndex = 208
			}},
			{"llSetSoundRadius", new FunctionSig {
				FunctionName =  "llSetSoundRadius",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Float},
				ParamNames = new string[] {"radius"},
				TableIndex = 209
			}},
			{"llKey2Name", new FunctionSig {
				FunctionName =  "llKey2Name",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"id"},
				TableIndex = 210
			}},
			{"llSetTextureAnim", new FunctionSig {
				FunctionName =  "llSetTextureAnim",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Integer, VarType.Integer, VarType.Integer, VarType.Float, VarType.Float, VarType.Float},
				ParamNames = new string[] {"mode", "face", "sizex", "sizey", "start", "length", "rate"},
				TableIndex = 211
			}},
			{"llTriggerSoundLimited", new FunctionSig {
				FunctionName =  "llTriggerSoundLimited",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String, VarType.Float, VarType.Vector, VarType.Vector},
				ParamNames = new string[] {"sound", "volume", "top_north_east", "bottom_south_west"},
				TableIndex = 212
			}},
			{"llEjectFromLand", new FunctionSig {
				FunctionName =  "llEjectFromLand",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"avatar"},
				TableIndex = 213
			}},
			{"llParseString2List", new FunctionSig {
				FunctionName =  "llParseString2List",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.String, VarType.List, VarType.List},
				ParamNames = new string[] {"src", "separators", "spacers"},
				TableIndex = 214
			}},
			{"llOverMyLand", new FunctionSig {
				FunctionName =  "llOverMyLand",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"id"},
				TableIndex = 215
			}},
			{"llGetLandOwnerAt", new FunctionSig {
				FunctionName =  "llGetLandOwnerAt",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.Vector},
				ParamNames = new string[] {"pos"},
				TableIndex = 216
			}},
			{"llGetNotecardLine", new FunctionSig {
				FunctionName =  "llGetNotecardLine",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.String, VarType.Integer},
				ParamNames = new string[] {"name", "line"},
				TableIndex = 217
			}},
			{"llGetAgentSize", new FunctionSig {
				FunctionName =  "llGetAgentSize",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"id"},
				TableIndex = 218
			}},
			{"llSameGroup", new FunctionSig {
				FunctionName =  "llSameGroup",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"id"},
				TableIndex = 219
			}},
			{"llUnSit", new FunctionSig {
				FunctionName =  "llUnSit",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"id"},
				TableIndex = 220
			}},
			{"llGroundSlope", new FunctionSig {
				FunctionName =  "llGroundSlope",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.Vector},
				ParamNames = new string[] {"offset"},
				TableIndex = 221
			}},
			{"llGroundNormal", new FunctionSig {
				FunctionName =  "llGroundNormal",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.Vector},
				ParamNames = new string[] {"offset"},
				TableIndex = 222
			}},
			{"llGroundContour", new FunctionSig {
				FunctionName =  "llGroundContour",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.Vector},
				ParamNames = new string[] {"offset"},
				TableIndex = 223
			}},
			{"llGetAttached", new FunctionSig {
				FunctionName =  "llGetAttached",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 224
			}},
			{"llGetFreeMemory", new FunctionSig {
				FunctionName =  "llGetFreeMemory",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 225
			}},
			{"llGetRegionName", new FunctionSig {
				FunctionName =  "llGetRegionName",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 226
			}},
			{"llGetRegionTimeDilation", new FunctionSig {
				FunctionName =  "llGetRegionTimeDilation",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 227
			}},
			{"llGetRegionFPS", new FunctionSig {
				FunctionName =  "llGetRegionFPS",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 228
			}},
			{"llParticleSystem", new FunctionSig {
				FunctionName =  "llParticleSystem",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.List},
				ParamNames = new string[] {"rules"},
				TableIndex = 229
			}},
			{"llGroundRepel", new FunctionSig {
				FunctionName =  "llGroundRepel",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Float, VarType.Integer, VarType.Float},
				ParamNames = new string[] {"height", "water", "tau"},
				TableIndex = 230
			}},
			{"llGiveInventoryList", new FunctionSig {
				FunctionName =  "llGiveInventoryList",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key, VarType.String, VarType.List},
				ParamNames = new string[] {"target", "folder", "inventory"},
				TableIndex = 231
			}},
			{"llSetVehicleType", new FunctionSig {
				FunctionName =  "llSetVehicleType",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"type"},
				TableIndex = 232
			}},
			{"llSetVehicleFloatParam", new FunctionSig {
				FunctionName =  "llSetVehicleFloatParam",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Float},
				ParamNames = new string[] {"param", "value"},
				TableIndex = 233
			}},
			{"llSetVehicleVectorParam", new FunctionSig {
				FunctionName =  "llSetVehicleVectorParam",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Vector},
				ParamNames = new string[] {"param", "vec"},
				TableIndex = 234
			}},
			{"llSetVehicleFlags", new FunctionSig {
				FunctionName =  "llSetVehicleFlags",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"flags"},
				TableIndex = 235
			}},
			{"llRemoveVehicleFlags", new FunctionSig {
				FunctionName =  "llRemoveVehicleFlags",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"flags"},
				TableIndex = 236
			}},
			{"llSitTarget", new FunctionSig {
				FunctionName =  "llSitTarget",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Vector, VarType.Rotation},
				ParamNames = new string[] {"offset", "rot"},
				TableIndex = 237
			}},
			{"llAvatarOnSitTarget", new FunctionSig {
				FunctionName =  "llAvatarOnSitTarget",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 238
			}},
			{"llAddToLandPassList", new FunctionSig {
				FunctionName =  "llAddToLandPassList",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key, VarType.Float},
				ParamNames = new string[] {"avatar", "hours"},
				TableIndex = 239
			}},
			{"llSetTouchText", new FunctionSig {
				FunctionName =  "llSetTouchText",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"text"},
				TableIndex = 240
			}},
			{"llSetSitText", new FunctionSig {
				FunctionName =  "llSetSitText",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"text"},
				TableIndex = 241
			}},
			{"llSetCameraEyeOffset", new FunctionSig {
				FunctionName =  "llSetCameraEyeOffset",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Vector},
				ParamNames = new string[] {"offset"},
				TableIndex = 242
			}},
			{"llSetCameraAtOffset", new FunctionSig {
				FunctionName =  "llSetCameraAtOffset",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Vector},
				ParamNames = new string[] {"offset"},
				TableIndex = 243
			}},
			{"llDumpList2String", new FunctionSig {
				FunctionName =  "llDumpList2String",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.List, VarType.String},
				ParamNames = new string[] {"src", "separator"},
				TableIndex = 244
			}},
			{"llScriptDanger", new FunctionSig {
				FunctionName =  "llScriptDanger",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Vector},
				ParamNames = new string[] {"pos"},
				TableIndex = 245
			}},
			{"llDialog", new FunctionSig {
				FunctionName =  "llDialog",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key, VarType.String, VarType.List, VarType.Integer},
				ParamNames = new string[] {"avatar", "message", "buttons", "chat_channel"},
				TableIndex = 246
			}},
			{"llVolumeDetect", new FunctionSig {
				FunctionName =  "llVolumeDetect",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"detect"},
				TableIndex = 247
			}},
			{"llResetOtherScript", new FunctionSig {
				FunctionName =  "llResetOtherScript",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"name"},
				TableIndex = 248
			}},
			{"llGetScriptState", new FunctionSig {
				FunctionName =  "llGetScriptState",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"name"},
				TableIndex = 249
			}},
			{"llSetRemoteScriptAccessPin", new FunctionSig {
				FunctionName =  "llSetRemoteScriptAccessPin",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"pin"},
				TableIndex = 250
			}},
			{"llRemoteLoadScriptPin", new FunctionSig {
				FunctionName =  "llRemoteLoadScriptPin",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key, VarType.String, VarType.Integer, VarType.Integer, VarType.Integer},
				ParamNames = new string[] {"target", "name", "pin", "running", "start_param"},
				TableIndex = 251
			}},
			{"llOpenRemoteDataChannel", new FunctionSig {
				FunctionName =  "llOpenRemoteDataChannel",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 252
			}},
			{"llSendRemoteData", new FunctionSig {
				FunctionName =  "llSendRemoteData",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.Key, VarType.String, VarType.Integer, VarType.String},
				ParamNames = new string[] {"channel", "dest", "idata", "sdata"},
				TableIndex = 253
			}},
			{"llRemoteDataReply", new FunctionSig {
				FunctionName =  "llRemoteDataReply",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key, VarType.Key, VarType.String, VarType.Integer},
				ParamNames = new string[] {"channel", "message_id", "sdata", "idata"},
				TableIndex = 254
			}},
			{"llCloseRemoteDataChannel", new FunctionSig {
				FunctionName =  "llCloseRemoteDataChannel",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"channel"},
				TableIndex = 255
			}},
			{"llMD5String", new FunctionSig {
				FunctionName =  "llMD5String",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.String, VarType.Integer},
				ParamNames = new string[] {"src", "nonce"},
				TableIndex = 256
			}},
			{"llSetPrimitiveParams", new FunctionSig {
				FunctionName =  "llSetPrimitiveParams",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.List},
				ParamNames = new string[] {"rules"},
				TableIndex = 257
			}},
			{"llStringToBase64", new FunctionSig {
				FunctionName =  "llStringToBase64",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"str"},
				TableIndex = 258
			}},
			{"llBase64ToString", new FunctionSig {
				FunctionName =  "llBase64ToString",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"str"},
				TableIndex = 259
			}},
			{"llXorBase64Strings", new FunctionSig {
				FunctionName =  "llXorBase64Strings",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.String, VarType.String},
				ParamNames = new string[] {"s1", "s2"},
				TableIndex = 260
			}},
			{"llLog10", new FunctionSig {
				FunctionName =  "llLog10",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {VarType.Float},
				ParamNames = new string[] {"val"},
				TableIndex = 261
			}},
			{"llLog", new FunctionSig {
				FunctionName =  "llLog",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {VarType.Float},
				ParamNames = new string[] {"val"},
				TableIndex = 262
			}},
			{"llGetAnimationList", new FunctionSig {
				FunctionName =  "llGetAnimationList",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"id"},
				TableIndex = 263
			}},
			{"llSetParcelMusicURL", new FunctionSig {
				FunctionName =  "llSetParcelMusicURL",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"url"},
				TableIndex = 264
			}},
			{"llGetRootPosition", new FunctionSig {
				FunctionName =  "llGetRootPosition",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 265
			}},
			{"llGetRootRotation", new FunctionSig {
				FunctionName =  "llGetRootRotation",
				ReturnType = VarType.Rotation,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 266
			}},
			{"llGetObjectDesc", new FunctionSig {
				FunctionName =  "llGetObjectDesc",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 267
			}},
			{"llSetObjectDesc", new FunctionSig {
				FunctionName =  "llSetObjectDesc",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"name"},
				TableIndex = 268
			}},
			{"llGetCreator", new FunctionSig {
				FunctionName =  "llGetCreator",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 269
			}},
			{"llGetTimestamp", new FunctionSig {
				FunctionName =  "llGetTimestamp",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 270
			}},
			{"llSetLinkAlpha", new FunctionSig {
				FunctionName =  "llSetLinkAlpha",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Float, VarType.Integer},
				ParamNames = new string[] {"linknumber", "alpha", "face"},
				TableIndex = 271
			}},
			{"llGetNumberOfPrims", new FunctionSig {
				FunctionName =  "llGetNumberOfPrims",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 272
			}},
			{"llGetNumberOfNotecardLines", new FunctionSig {
				FunctionName =  "llGetNumberOfNotecardLines",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"name"},
				TableIndex = 273
			}},
			{"llGetBoundingBox", new FunctionSig {
				FunctionName =  "llGetBoundingBox",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"object"},
				TableIndex = 274
			}},
			{"llGetGeometricCenter", new FunctionSig {
				FunctionName =  "llGetGeometricCenter",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 275
			}},
			{"llGetPrimitiveParams", new FunctionSig {
				FunctionName =  "llGetPrimitiveParams",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.List},
				ParamNames = new string[] {"params"},
				TableIndex = 276
			}},
			{"llIntegerToBase64", new FunctionSig {
				FunctionName =  "llIntegerToBase64",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"number"},
				TableIndex = 277
			}},
			{"llBase64ToInteger", new FunctionSig {
				FunctionName =  "llBase64ToInteger",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"str"},
				TableIndex = 278
			}},
			{"llGetGMTclock", new FunctionSig {
				FunctionName =  "llGetGMTclock",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 279
			}},
			{"llGetSimulatorHostname", new FunctionSig {
				FunctionName =  "llGetSimulatorHostname",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 280
			}},
			{"llSetLocalRot", new FunctionSig {
				FunctionName =  "llSetLocalRot",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Rotation},
				ParamNames = new string[] {"rot"},
				TableIndex = 281
			}},
			{"llParseStringKeepNulls", new FunctionSig {
				FunctionName =  "llParseStringKeepNulls",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.String, VarType.List, VarType.List},
				ParamNames = new string[] {"src", "separators", "spacers"},
				TableIndex = 282
			}},
			{"llRezAtRoot", new FunctionSig {
				FunctionName =  "llRezAtRoot",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String, VarType.Vector, VarType.Vector, VarType.Rotation, VarType.Integer},
				ParamNames = new string[] {"name", "pos", "vel", "rot", "param"},
				TableIndex = 283
			}},
			{"llGetObjectPermMask", new FunctionSig {
				FunctionName =  "llGetObjectPermMask",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"mask"},
				TableIndex = 284
			}},
			{"llSetObjectPermMask", new FunctionSig {
				FunctionName =  "llSetObjectPermMask",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Integer},
				ParamNames = new string[] {"mask", "value"},
				TableIndex = 285
			}},
			{"llGetInventoryPermMask", new FunctionSig {
				FunctionName =  "llGetInventoryPermMask",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.String, VarType.Integer},
				ParamNames = new string[] {"name", "mask"},
				TableIndex = 286
			}},
			{"llSetInventoryPermMask", new FunctionSig {
				FunctionName =  "llSetInventoryPermMask",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String, VarType.Integer, VarType.Integer},
				ParamNames = new string[] {"name", "mask", "value"},
				TableIndex = 287
			}},
			{"llGetInventoryCreator", new FunctionSig {
				FunctionName =  "llGetInventoryCreator",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"name"},
				TableIndex = 288
			}},
			{"llOwnerSay", new FunctionSig {
				FunctionName =  "llOwnerSay",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"msg"},
				TableIndex = 289
			}},
			{"llRequestSimulatorData", new FunctionSig {
				FunctionName =  "llRequestSimulatorData",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.String, VarType.Integer},
				ParamNames = new string[] {"simulator", "data"},
				TableIndex = 290
			}},
			{"llForceMouselook", new FunctionSig {
				FunctionName =  "llForceMouselook",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"mouselook"},
				TableIndex = 291
			}},
			{"llGetObjectMass", new FunctionSig {
				FunctionName =  "llGetObjectMass",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"id"},
				TableIndex = 292
			}},
			{"llListReplaceList", new FunctionSig {
				FunctionName =  "llListReplaceList",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.List, VarType.List, VarType.Integer, VarType.Integer},
				ParamNames = new string[] {"dest", "src", "start", "end"},
				TableIndex = 293
			}},
			{"llLoadURL", new FunctionSig {
				FunctionName =  "llLoadURL",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key, VarType.String, VarType.String},
				ParamNames = new string[] {"avatar", "message", "url"},
				TableIndex = 294
			}},
			{"llParcelMediaCommandList", new FunctionSig {
				FunctionName =  "llParcelMediaCommandList",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.List},
				ParamNames = new string[] {"command"},
				TableIndex = 295
			}},
			{"llParcelMediaQuery", new FunctionSig {
				FunctionName =  "llParcelMediaQuery",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.List},
				ParamNames = new string[] {"query"},
				TableIndex = 296
			}},
			{"llModPow", new FunctionSig {
				FunctionName =  "llModPow",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Integer, VarType.Integer},
				ParamNames = new string[] {"a", "b", "c"},
				TableIndex = 297
			}},
			{"llGetInventoryType", new FunctionSig {
				FunctionName =  "llGetInventoryType",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"name"},
				TableIndex = 298
			}},
			{"llSetPayPrice", new FunctionSig {
				FunctionName =  "llSetPayPrice",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.List},
				ParamNames = new string[] {"price", "quick_pay_buttons"},
				TableIndex = 299
			}},
			{"llGetCameraPos", new FunctionSig {
				FunctionName =  "llGetCameraPos",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 300
			}},
			{"llGetCameraRot", new FunctionSig {
				FunctionName =  "llGetCameraRot",
				ReturnType = VarType.Rotation,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 301
			}},
			{"llSetPrimURL", new FunctionSig {
				FunctionName =  "llSetPrimURL",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"url"},
				TableIndex = 302
			}},
			{"llRefreshPrimURL", new FunctionSig {
				FunctionName =  "llRefreshPrimURL",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 303
			}},
			{"llEscapeURL", new FunctionSig {
				FunctionName =  "llEscapeURL",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"url"},
				TableIndex = 304
			}},
			{"llUnescapeURL", new FunctionSig {
				FunctionName =  "llUnescapeURL",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"url"},
				TableIndex = 305
			}},
			{"llMapDestination", new FunctionSig {
				FunctionName =  "llMapDestination",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String, VarType.Vector, VarType.Vector},
				ParamNames = new string[] {"simname", "pos", "look_at"},
				TableIndex = 306
			}},
			{"llAddToLandBanList", new FunctionSig {
				FunctionName =  "llAddToLandBanList",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key, VarType.Float},
				ParamNames = new string[] {"avatar", "hours"},
				TableIndex = 307
			}},
			{"llRemoveFromLandPassList", new FunctionSig {
				FunctionName =  "llRemoveFromLandPassList",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"avatar"},
				TableIndex = 308
			}},
			{"llRemoveFromLandBanList", new FunctionSig {
				FunctionName =  "llRemoveFromLandBanList",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"avatar"},
				TableIndex = 309
			}},
			{"llSetCameraParams", new FunctionSig {
				FunctionName =  "llSetCameraParams",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.List},
				ParamNames = new string[] {"rules"},
				TableIndex = 310
			}},
			{"llClearCameraParams", new FunctionSig {
				FunctionName =  "llClearCameraParams",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 311
			}},
			{"llListStatistics", new FunctionSig {
				FunctionName =  "llListStatistics",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {VarType.Integer, VarType.List},
				ParamNames = new string[] {"operation", "src"},
				TableIndex = 312
			}},
			{"llGetUnixTime", new FunctionSig {
				FunctionName =  "llGetUnixTime",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 313
			}},
			{"llGetParcelFlags", new FunctionSig {
				FunctionName =  "llGetParcelFlags",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Vector},
				ParamNames = new string[] {"pos"},
				TableIndex = 314
			}},
			{"llGetRegionFlags", new FunctionSig {
				FunctionName =  "llGetRegionFlags",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 315
			}},
			{"llXorBase64StringsCorrect", new FunctionSig {
				FunctionName =  "llXorBase64StringsCorrect",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.String, VarType.String},
				ParamNames = new string[] {"s1", "s2"},
				TableIndex = 316
			}},
			{"llHTTPRequest", new FunctionSig {
				FunctionName =  "llHTTPRequest",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.String, VarType.List, VarType.String},
				ParamNames = new string[] {"url", "parameters", "body"},
				TableIndex = 317
			}},
			{"llResetLandBanList", new FunctionSig {
				FunctionName =  "llResetLandBanList",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 318
			}},
			{"llResetLandPassList", new FunctionSig {
				FunctionName =  "llResetLandPassList",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 319
			}},
			{"llGetObjectPrimCount", new FunctionSig {
				FunctionName =  "llGetObjectPrimCount",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"object_id"},
				TableIndex = 320
			}},
			{"llGetParcelPrimOwners", new FunctionSig {
				FunctionName =  "llGetParcelPrimOwners",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.Vector},
				ParamNames = new string[] {"pos"},
				TableIndex = 321
			}},
			{"llGetParcelPrimCount", new FunctionSig {
				FunctionName =  "llGetParcelPrimCount",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Vector, VarType.Integer, VarType.Integer},
				ParamNames = new string[] {"pos", "category", "sim_wide"},
				TableIndex = 322
			}},
			{"llGetParcelMaxPrims", new FunctionSig {
				FunctionName =  "llGetParcelMaxPrims",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Vector, VarType.Integer},
				ParamNames = new string[] {"pos", "sim_wide"},
				TableIndex = 323
			}},
			{"llGetParcelDetails", new FunctionSig {
				FunctionName =  "llGetParcelDetails",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.Vector, VarType.List},
				ParamNames = new string[] {"pos", "params"},
				TableIndex = 324
			}},
			{"llSetLinkPrimitiveParams", new FunctionSig {
				FunctionName =  "llSetLinkPrimitiveParams",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.List},
				ParamNames = new string[] {"linknumber", "rules"},
				TableIndex = 325
			}},
			{"llSetLinkTexture", new FunctionSig {
				FunctionName =  "llSetLinkTexture",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.String, VarType.Integer},
				ParamNames = new string[] {"linknumber", "texture", "face"},
				TableIndex = 326
			}},
			{"llStringTrim", new FunctionSig {
				FunctionName =  "llStringTrim",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.String, VarType.Integer},
				ParamNames = new string[] {"src", "trim_type"},
				TableIndex = 327
			}},
			{"llRegionSay", new FunctionSig {
				FunctionName =  "llRegionSay",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.String},
				ParamNames = new string[] {"channel", "msg"},
				TableIndex = 328
			}},
			{"llGetObjectDetails", new FunctionSig {
				FunctionName =  "llGetObjectDetails",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.Key, VarType.List},
				ParamNames = new string[] {"id", "params"},
				TableIndex = 329
			}},
			{"llSetClickAction", new FunctionSig {
				FunctionName =  "llSetClickAction",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"action"},
				TableIndex = 330
			}},
			{"llGetRegionAgentCount", new FunctionSig {
				FunctionName =  "llGetRegionAgentCount",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 331
			}},
			{"llTextBox", new FunctionSig {
				FunctionName =  "llTextBox",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key, VarType.String, VarType.Integer},
				ParamNames = new string[] {"avatar", "message", "chat_channel"},
				TableIndex = 332
			}},
			{"llGetAgentLanguage", new FunctionSig {
				FunctionName =  "llGetAgentLanguage",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"avatar"},
				TableIndex = 333
			}},
			{"llDetectedTouchUV", new FunctionSig {
				FunctionName =  "llDetectedTouchUV",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"index"},
				TableIndex = 334
			}},
			{"llDetectedTouchFace", new FunctionSig {
				FunctionName =  "llDetectedTouchFace",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"index"},
				TableIndex = 335
			}},
			{"llDetectedTouchPos", new FunctionSig {
				FunctionName =  "llDetectedTouchPos",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"index"},
				TableIndex = 336
			}},
			{"llDetectedTouchNormal", new FunctionSig {
				FunctionName =  "llDetectedTouchNormal",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"index"},
				TableIndex = 337
			}},
			{"llDetectedTouchBinormal", new FunctionSig {
				FunctionName =  "llDetectedTouchBinormal",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"index"},
				TableIndex = 338
			}},
			{"llDetectedTouchST", new FunctionSig {
				FunctionName =  "llDetectedTouchST",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"index"},
				TableIndex = 339
			}},
			{"llSHA1String", new FunctionSig {
				FunctionName =  "llSHA1String",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"src"},
				TableIndex = 340
			}},
			{"llGetFreeURLs", new FunctionSig {
				FunctionName =  "llGetFreeURLs",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 341
			}},
			{"llRequestURL", new FunctionSig {
				FunctionName =  "llRequestURL",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 342
			}},
			{"llRequestSecureURL", new FunctionSig {
				FunctionName =  "llRequestSecureURL",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 343
			}},
			{"llReleaseURL", new FunctionSig {
				FunctionName =  "llReleaseURL",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"url"},
				TableIndex = 344
			}},
			{"llHTTPResponse", new FunctionSig {
				FunctionName =  "llHTTPResponse",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key, VarType.Integer, VarType.String},
				ParamNames = new string[] {"request_id", "status", "body"},
				TableIndex = 345
			}},
			{"llGetHTTPHeader", new FunctionSig {
				FunctionName =  "llGetHTTPHeader",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.Key, VarType.String},
				ParamNames = new string[] {"request_id", "header"},
				TableIndex = 346
			}},
			{"llSetPrimMediaParams", new FunctionSig {
				FunctionName =  "llSetPrimMediaParams",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Integer, VarType.List},
				ParamNames = new string[] {"face", "params"},
				TableIndex = 347
			}},
			{"llGetPrimMediaParams", new FunctionSig {
				FunctionName =  "llGetPrimMediaParams",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.Integer, VarType.List},
				ParamNames = new string[] {"face", "params"},
				TableIndex = 348
			}},
			{"llClearPrimMedia", new FunctionSig {
				FunctionName =  "llClearPrimMedia",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"face"},
				TableIndex = 349
			}},
			{"llSetLinkPrimitiveParamsFast", new FunctionSig {
				FunctionName =  "llSetLinkPrimitiveParamsFast",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.List},
				ParamNames = new string[] {"linknumber", "rules"},
				TableIndex = 350
			}},
			{"llGetLinkPrimitiveParams", new FunctionSig {
				FunctionName =  "llGetLinkPrimitiveParams",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.Integer, VarType.List},
				ParamNames = new string[] {"linknumber", "rules"},
				TableIndex = 351
			}},
			{"llLinkParticleSystem", new FunctionSig {
				FunctionName =  "llLinkParticleSystem",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.List},
				ParamNames = new string[] {"linknumber", "rules"},
				TableIndex = 352
			}},
			{"llSetLinkTextureAnim", new FunctionSig {
				FunctionName =  "llSetLinkTextureAnim",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Integer, VarType.Integer, VarType.Integer, VarType.Integer, VarType.Float, VarType.Float, VarType.Float},
				ParamNames = new string[] {"link", "mode", "face", "sizex", "sizey", "start", "length", "rate"},
				TableIndex = 353
			}},
			{"llGetLinkNumberOfSides", new FunctionSig {
				FunctionName =  "llGetLinkNumberOfSides",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"link"},
				TableIndex = 354
			}},
			{"llGetUsername", new FunctionSig {
				FunctionName =  "llGetUsername",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"id"},
				TableIndex = 355
			}},
			{"llRequestUsername", new FunctionSig {
				FunctionName =  "llRequestUsername",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"id"},
				TableIndex = 356
			}},
			{"llGetDisplayName", new FunctionSig {
				FunctionName =  "llGetDisplayName",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"id"},
				TableIndex = 357
			}},
			{"llRequestDisplayName", new FunctionSig {
				FunctionName =  "llRequestDisplayName",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"id"},
				TableIndex = 358
			}},
			{"iwMakeNotecard", new FunctionSig {
				FunctionName =  "iwMakeNotecard",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String, VarType.List},
				ParamNames = new string[] {"name", "data"},
				TableIndex = 359
			}},
			{"iwAvatarName2Key", new FunctionSig {
				FunctionName =  "iwAvatarName2Key",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.String, VarType.String},
				ParamNames = new string[] {"firstName", "lastName"},
				TableIndex = 360
			}},
            {"iwLinkTargetOmega", new FunctionSig {
				FunctionName =  "iwLinkTargetOmega",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Vector, VarType.Float, VarType.Float},
				ParamNames = new string[] {"linkeNumber", "axis", "spinRate", "gain"},
				TableIndex = 361
			}},
            {"llSetVehicleRotationParam", new FunctionSig {
				FunctionName =  "llSetVehicleRotationParam",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Rotation},
				ParamNames = new string[] {"param", "rot"},
				TableIndex = 362
			}},
			{"llGetParcelMusicURL", new FunctionSig {
				FunctionName =  "llGetParcelMusicURL",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 363
			}},
			{"llSetRegionPos", new FunctionSig {
				FunctionName =  "llSetRegionPos",
				ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.Vector},
				ParamNames = new string[] {"v"},
                TableIndex = 364
			}},

            {"iwGetLinkInventoryNumber", new FunctionSig {
				FunctionName =  "iwGetLinkInventoryNumber",
				ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.Integer, VarType.Integer},
                ParamNames = new string[] {"linknumber", "type"},
                TableIndex = 365
			}},
            {"iwGetLinkInventoryType", new FunctionSig {
				FunctionName =  "iwGetLinkInventoryType",
				ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.Integer, VarType.String},
                ParamNames = new string[] {"linknumber", "name"},
                TableIndex = 366
			}},
            {"iwGetLinkInventoryPermMask", new FunctionSig {
				FunctionName =  "iwGetLinkInventoryPermMask",
				ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.Integer, VarType.String, VarType.Integer},
                ParamNames = new string[] {"linknumber", "name", "mask"},
                TableIndex = 367
			}},
            {"iwGetLinkInventoryName", new FunctionSig {
				FunctionName =  "iwGetLinkInventoryName",
				ReturnType = VarType.String,
                ParamTypes = new VarType[] {VarType.Integer, VarType.Integer, VarType.Integer},
                ParamNames = new string[] {"linknumber", "type", "number"},
                TableIndex = 368
			}},
            {"iwGetLinkInventoryKey", new FunctionSig {
				FunctionName =  "iwGetLinkInventoryKey",
				ReturnType = VarType.String,
                ParamTypes = new VarType[] {VarType.Integer, VarType.String},
                ParamNames = new string[] {"linknumber", "name"},
                TableIndex = 369
			}},
            {"iwGetLinkInventoryCreator", new FunctionSig {
				FunctionName =  "iwGetLinkInventoryCreator",
				ReturnType = VarType.String,
                ParamTypes = new VarType[] {VarType.Integer, VarType.String},
                ParamNames = new string[] {"linknumber", "name"},
                TableIndex = 370
			}},
            {"iwSHA256String", new FunctionSig {
				FunctionName =  "iwSHA256String",
				ReturnType = VarType.String,
                ParamTypes = new VarType[] {VarType.String},
                ParamNames = new string[] {"src"},
                TableIndex = 371
			}},
            {"iwTeleportAgent", new FunctionSig {
				FunctionName =  "iwTeleportAgent",
				ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.String, VarType.String, VarType.Vector, VarType.Vector},
                ParamNames = new string[] {"agent", "region", "pos", "lookat"},
                TableIndex = 372
			}},
			{"llAvatarOnLinkSitTarget", new FunctionSig {
				FunctionName =  "llAvatarOnLinkSitTarget",
				ReturnType = VarType.Key,
                ParamTypes = new VarType[] {VarType.Integer},
                ParamNames = new string[] {"linknumber"},
				TableIndex = 373
			}},
			{"iwGetLastOwner", new FunctionSig {
				FunctionName =  "iwGetLastOwner",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 374
			}},
			{"iwRemoveLinkInventory", new FunctionSig {
				FunctionName =  "iwRemoveLinkInventory",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.String},
				ParamNames = new string[] {"linknumber", "name"},
				TableIndex = 375
			}},
			{"iwGiveLinkInventory", new FunctionSig {
				FunctionName =  "iwGiveLinkInventory",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Key, VarType.String},
				ParamNames = new string[] {"linknumber", "target", "name"},
				TableIndex = 376
			}},
			{"iwGiveLinkInventoryList", new FunctionSig {
				FunctionName =  "iwGiveLinkInventoryList",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Key, VarType.String, VarType.List},
				ParamNames = new string[] {"linknumber", "target", "folder", "inventory"},
				TableIndex = 377
			}},
			{"iwGetNotecardSegment", new FunctionSig {
				FunctionName =  "iwGetNotecardSegment",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.String, VarType.Integer, VarType.Integer, VarType.Integer},
				ParamNames = new string[] {"name", "line", "startOffset", "maxLength"},
				TableIndex = 378
			}},
			{"iwGetLinkNumberOfNotecardLines", new FunctionSig {
				FunctionName =  "iwGetLinkNumberOfNotecardLines",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.Integer, VarType.String},
				ParamNames = new string[] {"linknumber", "name"},
				TableIndex = 379
			}},
			{"iwGetLinkNotecardLine", new FunctionSig {
				FunctionName =  "iwGetLinkNotecardLine",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.Integer, VarType.String, VarType.Integer},
				ParamNames = new string[] {"linknumber", "name", "line"},
				TableIndex = 380
			}},
			{"iwGetLinkNotecardSegment", new FunctionSig {
				FunctionName =  "iwGetLinkNotecardSegment",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.Integer, VarType.String, VarType.Integer, VarType.Integer, VarType.Integer},
				ParamNames = new string[] {"linknumber", "name", "line", "startOffset", "maxLength"},
				TableIndex = 381
			}},
			{"iwActiveGroup", new FunctionSig {
				FunctionName =  "iwActiveGroup",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Key, VarType.Key},
				ParamNames = new string[] {"agentId", "groupId"},
				TableIndex = 382
			}},
			{"iwAvatarOnLink", new FunctionSig {
				FunctionName =  "iwAvatarOnLink",
				ReturnType = VarType.Key,
                ParamTypes = new VarType[] {VarType.Integer},
                ParamNames = new string[] {"linknumber"},
				TableIndex = 383
			}},
			{"llRegionSayTo", new FunctionSig {
				FunctionName =  "llRegionSayTo",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key, VarType.Integer, VarType.String},
				ParamNames = new string[] {"destination", "channel", "msg"},
				TableIndex = 384
			}},
			{"llGetUsedMemory", new FunctionSig {
				FunctionName =  "llGetUsedMemory",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 385
			}},
			{"iwGetLinkInventoryDesc", new FunctionSig {
				FunctionName =  "iwGetLinkInventoryDesc",
				ReturnType = VarType.String,
                ParamTypes = new VarType[] {VarType.Integer, VarType.String},
                ParamNames = new string[] {"linknumber", "name"},
				TableIndex = 386
			}},
			{"llGenerateKey", new FunctionSig {
				FunctionName =  "llGenerateKey",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 387
			}},
			{"iwGetLinkInventoryLastOwner", new FunctionSig {
				FunctionName =  "iwGetLinkInventoryLastOwner",
				ReturnType = VarType.Key,
                ParamTypes = new VarType[] {VarType.Integer, VarType.String},
                ParamNames = new string[] {"linknumber", "name"},
				TableIndex = 388
			}},
			{"llSetLinkMedia", new FunctionSig {
				FunctionName =  "llSetLinkMedia",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Integer, VarType.List},
				ParamNames = new string[] {"linknumber", "face", "params"},
				TableIndex = 389
			}},
			{"llGetLinkMedia", new FunctionSig {
				FunctionName =  "llGetLinkMedia",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Integer, VarType.List},
				ParamNames = new string[] {"linknumber", "face", "params"},
				TableIndex = 390
			}},
			{"llClearLinkMedia", new FunctionSig {
				FunctionName =  "llClearLinkMedia",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Integer},
				ParamNames = new string[] {"linknumber", "face"},
				TableIndex = 391
			}},
			{"llGetEnv", new FunctionSig {
				FunctionName =  "llGetEnv",
				ReturnType = VarType.String,
                ParamTypes = new VarType[] {VarType.String},
                ParamNames = new string[] {"name"},
				TableIndex = 392
			}},
            {"llSetAngularVelocity", new FunctionSig {
				FunctionName =  "llSetAngularVelocity",
				ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Vector, VarType.Integer},
                ParamNames = new string[] {"force", "local"},
				TableIndex = 393
			}},
            {"llSetPhysicsMaterial", new FunctionSig {
				FunctionName =  "llSetPhysicsMaterial",
				ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Integer, VarType.Float, VarType.Float, VarType.Float, VarType.Float},
                ParamNames = new string[] {"mask", "gravity_multiplier", "restitution", "friction", "density"},
				TableIndex = 394
			}},
            {"llSetVelocity", new FunctionSig {
				FunctionName =  "llSetVelocity",
				ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Vector, VarType.Integer},
                ParamNames = new string[] {"force", "local"},
				TableIndex = 395
			}},
            {"iwRezObject", new FunctionSig {
				FunctionName =  "iwRezObject",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.String, VarType.Vector, VarType.Vector, VarType.Rotation, VarType.Integer},
				ParamNames = new string[] {"name", "pos", "vel", "rot", "param"},
				TableIndex = 396
			}},
            {"iwRezAtRoot", new FunctionSig {
				FunctionName =  "iwRezAtRoot",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.String, VarType.Vector, VarType.Vector, VarType.Rotation, VarType.Integer},
				ParamNames = new string[] {"name", "pos", "vel", "rot", "param"},
				TableIndex = 397
			}},
            {"iwRezPrim", new FunctionSig {
				FunctionName =  "iwRezPrim",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.List, VarType.List, VarType.List, VarType.Vector, VarType.Vector, VarType.Rotation, VarType.Integer},
				ParamNames = new string[] {"primParams", "particleSystem", "inventoryList", "pos", "vel", "rot", "param"},
				TableIndex = 398
			}},
			{"llGetAgentList", new FunctionSig {
				FunctionName =  "llGetAgentList",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.Integer, VarType.List},
				ParamNames = new string[] {"id", "options"},
				TableIndex = 399
			}},
			{"iwGetAgentList", new FunctionSig {
				FunctionName =  "iwGetAgentList",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Vector, VarType.Vector, VarType.List},
				ParamNames = new string[] {"id", "minPos", "maxPos", "params"},
				TableIndex = 400
			}},
			{"iwGetWorldBoundingBox", new FunctionSig {
				FunctionName =  "iwGetWorldBoundingBox",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"object"},
				TableIndex = 401
			}},
			{"llSetMemoryLimit", new FunctionSig {
				FunctionName =  "llSetMemoryLimit",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"limit"},
				TableIndex = 402
			}},
			{"llGetMemoryLimit", new FunctionSig {
				FunctionName =  "llGetMemoryLimit",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 403
			}},
			{"llManageEstateAccess", new FunctionSig {
				FunctionName =  "llManageEstateAccess",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Key},
				ParamNames = new string[] {"action", "avatar"},
				TableIndex = 404
			}},
			{"iwSubStringIndex", new FunctionSig {
				FunctionName =  "iwSubStringIndex",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.String, VarType.String, VarType.Integer, VarType.Integer},
				ParamNames = new string[] {"source", "pattern", "offset", "isCaseSensitive"},
				TableIndex = 405
			}},
			{"llLinkSitTarget", new FunctionSig {
				FunctionName =  "llLinkSitTarget",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Vector, VarType.Rotation},
				ParamNames = new string[] {"link", "offset", "rot"},
				TableIndex = 406
			}},
			{"llGetMassMKS", new FunctionSig {
				FunctionName =  "llGetMassMKS",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 407
			}},
			{"iwGetObjectMassMKS", new FunctionSig {
				FunctionName =  "iwGetObjectMassMKS",
				ReturnType = VarType.Float,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"id"},
				TableIndex = 408
			}},
			{"llSetLinkCamera", new FunctionSig {
				FunctionName =  "llSetLinkCamera",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Vector, VarType.Vector},
				ParamNames = new string[] {"linknum", "eyeOffset", "cameraAt"},
				TableIndex = 409
			}},
			{"iwSetGround", new FunctionSig {
				FunctionName =  "iwSetGround",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Integer, VarType.Integer, VarType.Integer, VarType.Float},
				ParamNames = new string[] {"x1", "y1", "x2", "y2", "height"},
				TableIndex = 410
			}},
			{"llSetContentType", new FunctionSig {
				FunctionName =  "llSetContentType",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key, VarType.Integer},
				ParamNames = new string[] {"request_id", "content_type"},
				TableIndex = 411
			}},
			{"llJsonGetValue", new FunctionSig {
				FunctionName =  "llJsonGetValue",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.String, VarType.List},
				ParamNames = new string[] {"json", "specifiers"},
				TableIndex = 412
			}},
			{"llJsonValueType", new FunctionSig {
				FunctionName =  "llJsonValueType",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.String, VarType.List},
				ParamNames = new string[] {"json", "specifiers"},
				TableIndex = 413
			}},
			{"llJsonSetValue", new FunctionSig {
				FunctionName =  "llJsonSetValue",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.String, VarType.List, VarType.String},
				ParamNames = new string[] {"json", "specifiers", "value"},
				TableIndex = 414
			}},
			{"llList2Json", new FunctionSig {
				FunctionName =  "llList2Json",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.String, VarType.List},
				ParamNames = new string[] {"type", "values"},
				TableIndex = 415
			}},
			{"llJson2List", new FunctionSig {
				FunctionName =  "llJson2List",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"src"},
				TableIndex = 416
			}},
            {"iwSetWind", new FunctionSig {
				FunctionName =  "iwSetWind",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Vector, VarType.Vector},
				ParamNames = new string[] {"type", "offset", "speed"},
				TableIndex = 417
			}},
			{"iwHasParcelPowers", new FunctionSig {
				FunctionName =  "iwHasParcelPowers",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"groupPower"},
				TableIndex = 418
			}},
			{"iwGroundSurfaceNormal", new FunctionSig {
				FunctionName =  "iwGroundSurfaceNormal",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.Vector},
				ParamNames = new string[] {"offset"},
				TableIndex = 419
			}},
			{"iwRequestAnimationData", new FunctionSig {
				FunctionName =  "iwRequestAnimationData",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"name"},
				TableIndex = 420
			}},
			{"llCastRay", new FunctionSig {
				FunctionName =  "llCastRay",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.Vector, VarType.Vector, VarType.List},
				ParamNames = new string[] {"start", "end", "options"},
				TableIndex = 421
			}},
			{"llSetKeyframedMotion", new FunctionSig {
				FunctionName =  "llSetKeyframedMotion",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.List, VarType.List},
				ParamNames = new string[] {"keyframes", "options"},
				TableIndex = 422
			}},
            {"iwWind", new FunctionSig {
				FunctionName =  "iwWind",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.Vector},
				ParamNames = new string[] {"offset"},
				TableIndex = 423
			}},
            {"llGetPhysicsMaterial", new FunctionSig {
				FunctionName =  "llGetPhysicsMaterial",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 424
			}},
            {"iwGetLocalTime", new FunctionSig {
				FunctionName =  "iwGetLocalTime",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 425
			}},
            {"iwGetLocalTimeOffset", new FunctionSig {
				FunctionName =  "iwGetLocalTimeOffset",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 426
			}},
            {"iwFormatTime", new FunctionSig {
				FunctionName =  "iwFormatTime",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.Integer, VarType.Integer, VarType.String},
				ParamNames = new string[] {"unixtime", "isUTC", "format"},
				TableIndex = 427
			}},
            {"botCreateBot", new FunctionSig {
				FunctionName =  "botCreateBot",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.String, VarType.String, VarType.String, VarType.Vector, VarType.Integer},
				ParamNames = new string[] {"firstName", "lastName", "outfitName", "startPosition", "options"},
				TableIndex = 428
			}},
            {"botAddTag", new FunctionSig {
				FunctionName =  "botAddTag",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key, VarType.String},
				ParamNames = new string[] {"botID", "tag"},
				TableIndex = 429
			}},
            {"botRemoveTag", new FunctionSig {
				FunctionName =  "botRemoveTag",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key, VarType.String},
				ParamNames = new string[] {"botID", "tag"},
				TableIndex = 430
			}},
            {"botGetBotsWithTag", new FunctionSig {
				FunctionName =  "botGetBotsWithTag",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"tag"},
				TableIndex = 431
			}},
            {"botRemoveBotsWithTag", new FunctionSig {
				FunctionName =  "botRemoveBotsWithTag",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"tag"},
				TableIndex = 432
			}},
            {"Shim_botRemoveBot", new FunctionSig {
				FunctionName =  "botRemoveBot",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"botID"},
				TableIndex = 433
			}},
            {"botPauseMovement", new FunctionSig {
				FunctionName =  "botPauseMovement",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"botID"},
				TableIndex = 434
			}},
            {"botResumeMovement", new FunctionSig {
				FunctionName =  "botResumeMovement",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"botID"},
				TableIndex = 435
			}},
            {"botWhisper", new FunctionSig {
				FunctionName =  "botWhisper",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key, VarType.Integer, VarType.String},
				ParamNames = new string[] {"botID", "channel", "message"},
				TableIndex = 436
			}},
            {"botSay", new FunctionSig {
				FunctionName =  "botSay",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key, VarType.Integer, VarType.String},
				ParamNames = new string[] {"botID", "channel", "message"},
				TableIndex = 437
			}},
            {"botShout", new FunctionSig {
				FunctionName =  "botShout",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key, VarType.Integer, VarType.String},
				ParamNames = new string[] {"botID", "channel", "message"},
				TableIndex = 438
			}},
            {"botStartTyping", new FunctionSig {
				FunctionName =  "botStartTyping",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"botID"},
				TableIndex = 439
			}},
            {"botStopTyping", new FunctionSig {
				FunctionName =  "botStopTyping",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"botID"},
				TableIndex = 440
			}},
            {"botSendInstantMessage", new FunctionSig {
				FunctionName =  "botSendInstantMessage",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key,VarType.Key,VarType.String},
				ParamNames = new string[] {"botID","userID","message"},
				TableIndex = 441
			}},
            {"botSitObject", new FunctionSig {
				FunctionName =  "botSitObject",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key,VarType.Key},
				ParamNames = new string[] {"botID","objectID"},
				TableIndex = 442
			}},
            {"botStandUp", new FunctionSig {
				FunctionName =  "botStandUp",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"botID"},
				TableIndex = 443
			}},
            {"botGetOwner", new FunctionSig {
				FunctionName =  "botGetOwner",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"botID"},
				TableIndex = 444
			}},
            {"botIsBot", new FunctionSig {
				FunctionName =  "botIsBot",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"userID"},
				TableIndex = 445
			}},
            {"botTouchObject", new FunctionSig {
				FunctionName =  "botTouchObject",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key,VarType.Key},
				ParamNames = new string[] {"botID", "objectID"},
				TableIndex = 446
			}},
            {"botSetMovementSpeed", new FunctionSig {
				FunctionName =  "botSetMovementSpeed",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key,VarType.Float},
				ParamNames = new string[] {"botID", "speed"},
				TableIndex = 447
			}},
            {"botGetPos", new FunctionSig {
				FunctionName =  "botGetPos",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"botID"},
				TableIndex = 448
			}},
            {"botGetName", new FunctionSig {
				FunctionName =  "botGetName",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"botID"},
				TableIndex = 449
			}},
            {"botStartAnimation", new FunctionSig {
				FunctionName =  "botStartAnimation",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key,VarType.String},
				ParamNames = new string[] {"botID", "animation"},
				TableIndex = 450
			}},
            {"botStopAnimation", new FunctionSig {
				FunctionName =  "botStopAnimation",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key,VarType.String},
				ParamNames = new string[] {"botID", "animation"},
				TableIndex = 451
			}},
            {"botTeleportTo", new FunctionSig {
				FunctionName =  "botTeleportTo",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key,VarType.Vector},
				ParamNames = new string[] {"botID", "position"},
				TableIndex = 452
			}},
            {"botChangeOwner", new FunctionSig {
				FunctionName =  "botChangeOwner",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key,VarType.Key},
				ParamNames = new string[] {"botID", "newOwnerID"},
				TableIndex = 453
			}},
            {"botGetAllBotsInRegion", new FunctionSig {
				FunctionName =  "botGetAllBotsInRegion",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 454
			}},
            {"botGetAllMyBotsInRegion", new FunctionSig {
				FunctionName =  "botGetAllMyBotsInRegion",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 455
			}},
            {"botFollowAvatar", new FunctionSig {
				FunctionName =  "botFollowAvatar",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Key, VarType.Key, VarType.List},
				ParamNames = new string[] {"botID", "avatar", "options"},
				TableIndex = 456
			}},
            {"botStopMovement", new FunctionSig {
				FunctionName =  "botStopMovement",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"botID"},
				TableIndex = 457
			}},
            {"botSetNavigationPoints", new FunctionSig {
				FunctionName =  "botSetNavigationPoints",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key, VarType.List, VarType.List,VarType.List},
				ParamNames = new string[] {"botID", "positions", "movementTypes","options"},
				TableIndex = 458
			}},
            {"botRegisterForNavigationEvents", new FunctionSig {
				FunctionName =  "botRegisterForNavigationEvents",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"botID"},
				TableIndex = 459
			}},
            {"botSetProfile", new FunctionSig {
				FunctionName =  "botSetProfile",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key,VarType.String,VarType.String,VarType.String,VarType.String,VarType.String,VarType.String},
				ParamNames = new string[] {"botID","aboutText","email","firstLifeAboutText","firstLifeImageUUID","imageUUID","profileURL"},
				TableIndex = 460
			}},
            {"botSetRotation", new FunctionSig {
				FunctionName =  "botSetRotation",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key,VarType.Rotation},
				ParamNames = new string[] {"botID","rotation"},
				TableIndex = 461
			}},
            {"botGiveInventory", new FunctionSig {
				FunctionName =  "botGiveInventory",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key,VarType.Key,VarType.String},
				ParamNames = new string[] {"botID","destination","inventory"},
				TableIndex = 462
			}},
            {"botSensor", new FunctionSig {
				FunctionName =  "botSensor",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key,VarType.String,VarType.Key,VarType.Integer,VarType.Float,VarType.Float},
				ParamNames = new string[] {"botID","name","id","type","range","arc"},
				TableIndex = 463
			}},
            {"botSensorRepeat", new FunctionSig {
				FunctionName =  "botSensorRepeat",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String,VarType.String,VarType.String,VarType.Integer,VarType.Float,VarType.Float,VarType.Float},
				ParamNames = new string[] {"botID","name","id","type","range","arc","rate"},
				TableIndex = 464
			}},
            {"botSensorRemove", new FunctionSig {
				FunctionName =  "botSensorRemove",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 465
			}},
            {"iwDetectedBot", new FunctionSig {
				FunctionName =  "iwDetectedBot",
				ReturnType = VarType.Key,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 466
			}},
			{"botListen", new FunctionSig {
				FunctionName =  "botListen",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Key, VarType.Integer, VarType.String, VarType.Key, VarType.String},
				ParamNames = new string[] {"botID","channel", "name", "id", "msg"},
				TableIndex = 467
			}},
			{"botRegisterForCollisionEvents", new FunctionSig {
				FunctionName =  "botRegisterForCollisionEvents",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"botID"},
				TableIndex = 468
			}},
			{"botDeregisterFromCollisionEvents", new FunctionSig {
				FunctionName =  "botDeregisterFromCollisionEvents",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"botID"},
				TableIndex = 469
			}},
			{"botDeregisterFromNavigationEvents", new FunctionSig {
				FunctionName =  "botDeregisterFromNavigationEvents",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"botID"},
				TableIndex = 470
			}},
			{"botSetOutfit", new FunctionSig {
				FunctionName =  "botSetOutfit",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"outfitName"},
				TableIndex = 471
			}},
			{"botRemoveOutfit", new FunctionSig {
				FunctionName =  "botRemoveOutfit",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.String},
				ParamNames = new string[] {"outfitName"},
				TableIndex = 472
			}},
			{"botChangeOutfit", new FunctionSig {
				FunctionName =  "botChangeOutfit",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key,VarType.String},
				ParamNames = new string[] {"botID","outfitName"},
				TableIndex = 473
			}},
			{"botGetBotOutfits", new FunctionSig {
				FunctionName =  "botGetBotOutfits",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 474
			}},
			{"botWanderWithin", new FunctionSig {
				FunctionName =  "botWanderWithin",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key,VarType.Vector,VarType.Float,VarType.Float,VarType.List},
				ParamNames = new string[] {"botID","origin","xDistance","yDistance","options"},
				TableIndex = 475
			}},
			{"botMessageLinked", new FunctionSig {
				FunctionName =  "botMessageLinked",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key,VarType.Integer,VarType.String,VarType.Key},
				ParamNames = new string[] {"botID","num","msg","id"},
				TableIndex = 476
			}},
			{"botSetProfileParams", new FunctionSig {
				FunctionName =  "botSetProfileParams",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Key,VarType.List},
				ParamNames = new string[] {"botID","profileParams"},
				TableIndex = 477
			}},
			{"botGetProfileParams", new FunctionSig {
				FunctionName =  "botGetProfileParams",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.Key, VarType.List},
				ParamNames = new string[] {"botID", "profileParams"},
				TableIndex = 478
			}},
            {"iwCheckRezError", new FunctionSig {
				FunctionName =  "iwCheckRezError",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Vector, VarType.Integer, VarType.Integer},
				ParamNames = new string[] {"offset", "isTemp", "landImpact"},
				TableIndex = 479
			}},
            {"iwGetAngularVelocity", new FunctionSig {
				FunctionName =  "iwGetAngularVelocity",
				ReturnType = VarType.Vector,
				ParamTypes = new VarType[] {},
				ParamNames = new string[] {},
				TableIndex = 480
			}},
            {"iwGetAppearanceParam", new FunctionSig {
				FunctionName =  "iwGetAppearanceParam",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Key, VarType.Integer},
				ParamNames = new string[] {"avatarKey","whichParam"},
				TableIndex = 481
			}},
			{"iwParseString2List", new FunctionSig {
				FunctionName =  "iwParseString2List",
				ReturnType = VarType.List,
				ParamTypes = new VarType[] {VarType.String, VarType.List, VarType.List, VarType.List},
				ParamNames = new string[] {"src", "separators", "spacers", "args"},
				TableIndex = 482
			}},
            {"iwChar2Int", new FunctionSig {
                FunctionName = "iwChar2Int",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.String, VarType.Integer},
                ParamNames = new string[] {"src", "index"},
                TableIndex = 483
            }},
            {"iwInt2Char", new FunctionSig {
                FunctionName = "iwInt2Char",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] {VarType.Integer},
                ParamNames = new string[] {"num"},
                TableIndex = 484
            }},
            {"iwReplaceString", new FunctionSig {
                FunctionName = "iwReplaceString",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] {VarType.String, VarType.String, VarType.String},
                ParamNames = new string[] {"str", "pattern", "replacement"},
                TableIndex = 485
            }},
            {"iwFormatString", new FunctionSig {
                FunctionName = "iwFormatString",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] {VarType.String, VarType.List},
                ParamNames = new string[] {"str", "values"},
                TableIndex = 486
            }},
            {"iwMatchString", new FunctionSig {
                FunctionName = "iwMatchString",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.String, VarType.String, VarType.Integer},
                ParamNames = new string[] {"str", "pattern", "matchType"},
                TableIndex = 487
            }},
            {"iwStringCodec", new FunctionSig {
                FunctionName = "iwStringCodec",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] {VarType.String, VarType.String, VarType.Integer, VarType.List},
                ParamNames = new string[] {"str", "codec", "operation", "extraParams"},
                TableIndex = 488
            }},
            {"iwMatchList", new FunctionSig {
                FunctionName = "iwMatchList",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.List, VarType.List, VarType.Integer},
                ParamNames = new string[] {"list1", "list2", "matchType"},
                TableIndex = 489
            }},
            {"iwColorConvert", new FunctionSig {
                FunctionName = "iwColorConvert",
                ReturnType = VarType.Vector,
                ParamTypes = new VarType[] {VarType.Vector, VarType.Integer, VarType.Integer},
                ParamNames = new string[] {"input", "color1", "color2"},
                TableIndex = 490
            }},
            {"iwNameToColor", new FunctionSig {
                FunctionName = "iwNameToColor",
                ReturnType = VarType.Vector,
                ParamTypes = new VarType[] {VarType.String},
                ParamNames = new string[] {"name"},
                TableIndex = 491
            }},
            {"iwVerifyType", new FunctionSig {
                FunctionName = "iwVerifyType",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.String, VarType.Integer},
                ParamNames = new string[] {"str", "type"},
                TableIndex = 492
            }},
			{"iwGroupInvite", new FunctionSig {
				FunctionName =  "iwGroupInvite",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Key, VarType.Key, VarType.String},
				ParamNames = new string[] {"group", "user", "rolename"},
				TableIndex = 493
			}},
			{"iwGroupEject", new FunctionSig {
				FunctionName =  "iwGroupEject",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Key, VarType.Key},
				ParamNames = new string[] {"group", "user"},
				TableIndex = 494
			}},
 			{"iwGetAgentData", new FunctionSig {
				FunctionName =  "iwGetAgentData",
				ReturnType = VarType.String,
				ParamTypes = new VarType[] {VarType.Key, VarType.Integer},
				ParamNames = new string[] {"id", "data"},
				TableIndex = 495
			}},
 			{"iwIsPlusUser", new FunctionSig {
				FunctionName =  "iwIsPlusUser",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Key},
				ParamNames = new string[] {"id"},
				TableIndex = 496
			}},
            {"llAttachToAvatarTemp", new FunctionSig {
				FunctionName =  "llAttachToAvatarTemp",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] {VarType.Integer},
				ParamNames = new string[] {"attachPoint"},
				TableIndex = 497
			}},
            {"iwListIncludesElements", new FunctionSig {
                FunctionName = "iwListIncludesElements",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.List, VarType.List, VarType.Integer},
                ParamNames = new string[] {"src", "elements", "any"},
                TableIndex = 498
            }},
            {"iwReverseString", new FunctionSig {
                FunctionName = "iwReverseString",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] {VarType.String},
                ParamNames = new string[] {"src"},
                TableIndex = 499
            }},
            {"iwReverseList", new FunctionSig {
                FunctionName = "iwReverseList",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] {VarType.List, VarType.Integer},
                ParamNames = new string[] {"src", "stride"},
                TableIndex = 500
            }},
            {"iwSearchInventory", new FunctionSig {
                FunctionName = "iwSearchInventory",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] {VarType.Integer, VarType.String, VarType.Integer},
                ParamNames = new string[] {"type", "pattern", "matchtype"},
                TableIndex = 501
            }},
            {"iwSearchLinkInventory", new FunctionSig {
                FunctionName = "iwSearchLinkInventory",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] {VarType.Integer, VarType.Integer, VarType.String, VarType.Integer},
                ParamNames = new string[] {"link", "type", "pattern", "matchtype"},
                TableIndex = 502
            }},
            {"iwIntRand", new FunctionSig {
                FunctionName = "iwIntRand",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.Integer},
                ParamNames = new string[] {"max"},
                TableIndex = 503
            }},
            {"iwIntRandRange", new FunctionSig {
                FunctionName = "iwIntRandRange",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.Integer, VarType.Integer},
                ParamNames = new string[] {"min", "max"},
                TableIndex = 504
            }},
            {"iwFrandRange", new FunctionSig {
                FunctionName = "iwFrandRange",
                ReturnType = VarType.Float,
                ParamTypes = new VarType[] {VarType.Float, VarType.Float},
                ParamNames = new string[] {"min", "max"},
                TableIndex = 505
            }},
            {"botSearchBotOutfits", new FunctionSig {
                FunctionName = "botSearchBotOutfits",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] {VarType.String, VarType.Integer, VarType.Integer, VarType.Integer},
                ParamNames = new string[] {"pattern", "matchType", "start", "end"},
                TableIndex = 506
            }},
            {"iwListRemoveElements", new FunctionSig {
                FunctionName = "iwListRemoveElements",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] {VarType.List, VarType.List, VarType.Integer, VarType.Integer},
                ParamNames = new string[] {"src", "elements", "count", "mode"},
                TableIndex = 507
            }},
            {"iwListRemoveDuplicates", new FunctionSig {
                FunctionName = "iwListRemoveDuplicates",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] {VarType.List},
                ParamNames = new string[] {"src"},
                TableIndex = 508
            }},
            {"iwStartLinkAnimation", new FunctionSig {
                FunctionName = "iwStartLinkAnimation",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Integer, VarType.String},
                ParamNames = new string[] {"link", "anim"},
                TableIndex = 509
            }},
            {"iwStopLinkAnimation", new FunctionSig {
                FunctionName = "iwStopLinkAnimation",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Integer, VarType.String},
                ParamNames = new string[] {"link", "anim"},
                TableIndex = 510
            }},
            {"iwClampInt", new FunctionSig {
                FunctionName = "iwClampInt",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.Integer, VarType.Integer, VarType.Integer},
                ParamNames = new string[] {"value", "min", "max"},
                TableIndex = 511
            }},
            {"iwClampFloat", new FunctionSig {
                FunctionName = "iwClampFloat",
                ReturnType = VarType.Float,
                ParamTypes = new VarType[] {VarType.Float, VarType.Float, VarType.Float},
                ParamNames = new string[] {"value", "min", "max"},
                TableIndex = 512
            }},
            {"iwSearchLinksByName", new FunctionSig {
                FunctionName = "iwSearchLinksByName",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] {VarType.String, VarType.Integer, VarType.Integer},
                ParamNames = new string[] {"pattern", "matchType", "linksOnly"},
                TableIndex = 513
            }},
            {"iwSearchLinksByDesc", new FunctionSig {
                FunctionName = "iwSearchLinksByDesc",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] {VarType.String, VarType.Integer, VarType.Integer},
                ParamNames = new string[] {"pattern", "matchType", "linksOnly"},
                TableIndex = 514
            }},
            {"botHasTag", new FunctionSig {
                FunctionName = "botHasTag",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.Key, VarType.String},
                ParamNames = new string[] {"botID", "tag"},
                TableIndex = 515
            }},
            {"botGetBotTags", new FunctionSig {
                FunctionName = "botGetBotTags",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] {VarType.Key},
                ParamNames = new string[] {"botID"},
                TableIndex = 516
			}},
            {"iwValidateURL", new FunctionSig {
                FunctionName = "iwValidateURL",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.String},
                ParamNames = new string[] {"url"},
                TableIndex = 517
            }},
            {"iwRemoteLoadScriptPin", new FunctionSig {
				FunctionName = "iwRemoteLoadScriptPin",
				ReturnType = VarType.Integer,
				ParamTypes = new VarType[] {VarType.Key, VarType.String, VarType.Integer, VarType.Integer, VarType.Integer},
				ParamNames = new string[] {"target", "name", "pin", "running", "start_param"},
				TableIndex = 518
			}},
            {"iwDeliverInventory", new FunctionSig {
                FunctionName =  "iwDeliverInventory",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.Integer, VarType.Key, VarType.String},
                ParamNames = new string[] {"linknumber", "target", "name"},
                TableIndex = 519
            }},
            {"iwDeliverInventoryList", new FunctionSig {
                FunctionName =  "iwDeliverInventoryList",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.Integer, VarType.Key, VarType.String, VarType.List},
                ParamNames = new string[] {"linknumber", "target", "folder", "inventory"},
                TableIndex = 520
            }},
            {"llGetAttachedList", new FunctionSig {
                FunctionName =  "llGetAttachedList",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] {VarType.Key},
                ParamNames = new string[] {"avatar"},
                TableIndex = 521
            }},
            {"llReturnObjectsByOwner", new FunctionSig {
                FunctionName =  "llReturnObjectsByOwner",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.Key, VarType.Integer},
                ParamNames = new string[] {"owner", "scope"},
                TableIndex = 522
            }},
            {"llReturnObjectsByID", new FunctionSig {
                FunctionName =  "llReturnObjectsByID",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.List},
                ParamNames = new string[] {"objects"},
                TableIndex = 523
            }},
            {"llTransferLindenDollars", new FunctionSig {
                FunctionName =  "llTransferLindenDollars",
                ReturnType = VarType.Key,
                ParamTypes = new VarType[] {VarType.Key, VarType.Integer},
                ParamNames = new string[] {"destination", "amount"},
                TableIndex = 524
            }},
            {"iwGiveMoney", new FunctionSig {
                FunctionName =  "iwGiveMoney",
                ReturnType = VarType.Key,
                ParamTypes = new VarType[] {VarType.Key, VarType.Integer},
                ParamNames = new string[] {"destination", "amount"},
                TableIndex = 525
            }},
            {"iwStandTarget", new FunctionSig {
                FunctionName =  "iwStandTarget",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Vector, VarType.Rotation},
                ParamNames = new string[] {"offset", "rot"},
                TableIndex = 526
            }},
            {"iwLinkStandTarget", new FunctionSig {
                FunctionName =  "iwLinkStandTarget",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Integer, VarType.Vector, VarType.Rotation},
                ParamNames = new string[] {"linknum", "offset", "rot"},
                TableIndex = 527
            }},
            {"llGetAnimationOverride", new FunctionSig {
                FunctionName =  "llGetAnimationOverride",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] {VarType.String},
                ParamNames = new string[] {"anim_state"},
                TableIndex = 528
            }},
            {"llSetAnimationOverride", new FunctionSig {
                FunctionName =  "llSetAnimationOverride",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.String, VarType.String},
                ParamNames = new string[] {"anim_state", "anim"},
                TableIndex = 529
            }},
            {"llResetAnimationOverride", new FunctionSig {
                FunctionName =  "llResetAnimationOverride",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.String},
                ParamNames = new string[] {"anim_state"},
              TableIndex = 530
            }},
            {"iwRezAt", new FunctionSig {
                FunctionName =  "iwRezAt",
                ReturnType = VarType.Key,
                ParamTypes = new VarType[] {VarType.String, VarType.Integer, VarType.Vector, VarType.Vector, VarType.Rotation, VarType.Integer},
                ParamNames = new string[] {"name", "rezAtRoot", "pos", "vel", "rot", "param"},
                TableIndex = 531
            }},
			// === ADD THESE LINES to Defaults.cs ===
// Insert BEFORE the closing "};" of the dictionary, after the iwRezAt entry (TableIndex 531)

            // ── Batch 10: SL compatibility functions ──
            {"llChar", new FunctionSig {
                FunctionName = "llChar",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] {VarType.Integer},
                ParamNames = new string[] {"unicode"},
                TableIndex = 532
            }},
            {"llOrd", new FunctionSig {
                FunctionName = "llOrd",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.String, VarType.Integer},
                ParamNames = new string[] {"src", "index"},
                TableIndex = 533
            }},
            {"llComputeHash", new FunctionSig {
                FunctionName = "llComputeHash",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] {VarType.String, VarType.String},
                ParamNames = new string[] {"src", "algorithm"},
                TableIndex = 534
            }},
            {"llDetectedRezzer", new FunctionSig {
                FunctionName = "llDetectedRezzer",
                ReturnType = VarType.Key,
                ParamTypes = new VarType[] {VarType.Integer},
                ParamNames = new string[] {"number"},
                TableIndex = 535
            }},
            {"llGetInventoryDesc", new FunctionSig {
                FunctionName = "llGetInventoryDesc",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] {VarType.String},
                ParamNames = new string[] {"item"},
                TableIndex = 536
            }},
            {"llGetInventoryAcquireTime", new FunctionSig {
                FunctionName = "llGetInventoryAcquireTime",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] {VarType.String},
                ParamNames = new string[] {"item"},
                TableIndex = 537
            }},
            {"llName2Key", new FunctionSig {
                FunctionName = "llName2Key",
                ReturnType = VarType.Key,
                ParamTypes = new VarType[] {VarType.String},
                ParamNames = new string[] {"name"},
                TableIndex = 538
            }},
            {"llGetRegionTimeOfDay", new FunctionSig {
                FunctionName = "llGetRegionTimeOfDay",
                ReturnType = VarType.Float,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 539
            }},
            {"llGetSimStats", new FunctionSig {
                FunctionName = "llGetSimStats",
                ReturnType = VarType.Float,
                ParamTypes = new VarType[] {VarType.Integer},
                ParamNames = new string[] {"statType"},
                TableIndex = 540
            }},
            {"llGetSPMaxMemory", new FunctionSig {
                FunctionName = "llGetSPMaxMemory",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 541
            }},
            {"llGetObjectAnimationNames", new FunctionSig {
                FunctionName = "llGetObjectAnimationNames",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 542
            }},
            {"llGetObjectLinkKey", new FunctionSig {
                FunctionName = "llGetObjectLinkKey",
                ReturnType = VarType.Key,
                ParamTypes = new VarType[] {VarType.Key, VarType.Integer},
                ParamNames = new string[] {"objectId", "linknumber"},
                TableIndex = 543
            }},
            {"llDerezObject", new FunctionSig {
                FunctionName = "llDerezObject",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Key},
                ParamNames = new string[] {"id"},
                TableIndex = 544
            }},
            {"llTeleportAgent", new FunctionSig {
                FunctionName = "llTeleportAgent",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Key, VarType.String, VarType.Vector, VarType.Vector},
                ParamNames = new string[] {"agent", "landmark", "pos", "lookAt"},
                TableIndex = 545
            }},
            {"llTeleportAgentGlobalCoords", new FunctionSig {
                FunctionName = "llTeleportAgentGlobalCoords",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Key, VarType.Vector, VarType.Vector, VarType.Vector},
                ParamNames = new string[] {"agent", "globalCoords", "regionPos", "lookAt"},
                TableIndex = 546
            }},
            {"llScaleByFactor", new FunctionSig {
                FunctionName = "llScaleByFactor",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.Float},
                ParamNames = new string[] {"factor"},
                TableIndex = 547
            }},
            {"llGetMaxScaleFactor", new FunctionSig {
                FunctionName = "llGetMaxScaleFactor",
                ReturnType = VarType.Float,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 548
            }},
            {"llGetMinScaleFactor", new FunctionSig {
                FunctionName = "llGetMinScaleFactor",
                ReturnType = VarType.Float,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 549
            }},
            {"llGetCameraAspect", new FunctionSig {
                FunctionName = "llGetCameraAspect",
                ReturnType = VarType.Float,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 550
            }},
            {"llGetCameraFOV", new FunctionSig {
                FunctionName = "llGetCameraFOV",
                ReturnType = VarType.Float,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 551
            }},
            {"llGetAttachedListFiltered", new FunctionSig {
                FunctionName = "llGetAttachedListFiltered",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] {VarType.Key, VarType.Integer},
                ParamNames = new string[] {"avatar", "attachmentPoint"},
                TableIndex = 552
            }},
            {"llIsFriend", new FunctionSig {
                FunctionName = "llIsFriend",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.Key},
                ParamNames = new string[] {"agent"},
                TableIndex = 553
            }},
            {"llGetVisualParams", new FunctionSig {
                FunctionName = "llGetVisualParams",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] {VarType.Key, VarType.List},
                ParamNames = new string[] {"agent", "paramIndices"},
                TableIndex = 554
            }},
            {"llGetNotecardLineSync", new FunctionSig {
                FunctionName = "llGetNotecardLineSync",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] {VarType.String, VarType.Integer},
                ParamNames = new string[] {"name", "line"},
                TableIndex = 555
            }},
            {"llFindNotecardTextSync", new FunctionSig {
                FunctionName = "llFindNotecardTextSync",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.String, VarType.String, VarType.Integer, VarType.Integer},
                ParamNames = new string[] {"name", "text", "line", "caseSensitive"},
                TableIndex = 556
            }},
            {"llStartObjectAnimation", new FunctionSig {
                FunctionName = "llStartObjectAnimation",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.String},
                ParamNames = new string[] {"anim"},
                TableIndex = 557
            }},
            {"llStopObjectAnimation", new FunctionSig {
                FunctionName = "llStopObjectAnimation",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.String},
                ParamNames = new string[] {"anim"},
                TableIndex = 558
            }},
            {"llGetLinkSitFlags", new FunctionSig {
                FunctionName = "llGetLinkSitFlags",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.Integer},
                ParamNames = new string[] {"link"},
                TableIndex = 559
            }},
            {"llSetLinkSitFlags", new FunctionSig {
                FunctionName = "llSetLinkSitFlags",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Integer, VarType.Integer},
                ParamNames = new string[] {"link", "flags"},
                TableIndex = 560
            }},
            {"llReplaceSubString", new FunctionSig {
                FunctionName = "llReplaceSubString",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] {VarType.String, VarType.String, VarType.String, VarType.Integer},
                ParamNames = new string[] {"src", "pattern", "replacement", "count"},
                TableIndex = 561
            }},
            {"llListFindStrided", new FunctionSig {
                FunctionName = "llListFindStrided",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.List, VarType.List, VarType.Integer, VarType.Integer, VarType.Integer},
                ParamNames = new string[] {"src", "test", "start", "end", "stride"},
                TableIndex = 562
            }},
            {"llList2ListSlice", new FunctionSig {
                FunctionName = "llList2ListSlice",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] {VarType.List, VarType.Integer, VarType.Integer, VarType.Integer, VarType.Integer},
                ParamNames = new string[] {"src", "start", "end", "stride", "slice_index"},
                TableIndex = 563
            }},
            {"llSortListStrided", new FunctionSig {
                FunctionName = "llSortListStrided",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] {VarType.List, VarType.Integer, VarType.Integer, VarType.Integer},
                ParamNames = new string[] {"src", "stride", "stride_index", "ascending"},
                TableIndex = 564
            }},
            {"llListFindListNext", new FunctionSig {
                FunctionName = "llListFindListNext",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.List, VarType.List, VarType.Integer},
                ParamNames = new string[] {"src", "test", "ofs"},
                TableIndex = 565
            }},
            {"llHMAC", new FunctionSig {
                FunctionName = "llHMAC",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] {VarType.String, VarType.String, VarType.String},
                ParamNames = new string[] {"msg", "privateKey", "algorithm"},
                TableIndex = 566
            }},
            {"llSHA256String", new FunctionSig {
                FunctionName = "llSHA256String",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] {VarType.String, VarType.Integer},
                ParamNames = new string[] {"src", "nonce"},
                TableIndex = 567
            }},
            {"llLinkPlaySound", new FunctionSig {
                FunctionName = "llLinkPlaySound",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {VarType.Integer, VarType.String, VarType.Float, VarType.Integer},
                ParamNames = new string[] {"link", "sound", "volume", "flags"},
                TableIndex = 568
            }},
            {"llLinear2sRGB", new FunctionSig {
                FunctionName = "llLinear2sRGB",
                ReturnType = VarType.Vector,
                ParamTypes = new VarType[] {VarType.Vector},
                ParamNames = new string[] {"color"},
                TableIndex = 569
            }},
            {"llSRGB2Linear", new FunctionSig {
                FunctionName = "llSRGB2Linear",
                ReturnType = VarType.Vector,
                ParamTypes = new VarType[] {VarType.Vector},
                ParamNames = new string[] {"color"},
                TableIndex = 570
            }},
            {"llWorldPosToHUD", new FunctionSig {
                FunctionName = "llWorldPosToHUD",
                ReturnType = VarType.Vector,
                ParamTypes = new VarType[] {VarType.Vector},
                ParamNames = new string[] {"worldPos"},
                TableIndex = 571
            }},
            {"llLinksetDataAvailable", new FunctionSig {
                FunctionName = "llLinksetDataAvailable",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 572
            }},
            {"llLinksetDataCountKeys", new FunctionSig {
                FunctionName = "llLinksetDataCountKeys",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 573
            }},
            {"llLinksetDataRead", new FunctionSig {
                FunctionName = "llLinksetDataRead",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] {VarType.String},
                ParamNames = new string[] {"name"},
                TableIndex = 574
            }},
            {"llLinksetDataReadProtected", new FunctionSig {
                FunctionName = "llLinksetDataReadProtected",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] {VarType.String, VarType.String},
                ParamNames = new string[] {"name", "pass"},
                TableIndex = 575
            }},
            {"llLinksetDataWrite", new FunctionSig {
                FunctionName = "llLinksetDataWrite",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.String, VarType.String},
                ParamNames = new string[] {"name", "value"},
                TableIndex = 576
            }},
            {"llLinksetDataWriteProtected", new FunctionSig {
                FunctionName = "llLinksetDataWriteProtected",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.String, VarType.String, VarType.String},
                ParamNames = new string[] {"name", "value", "pass"},
                TableIndex = 577
            }},
            {"llLinksetDataDelete", new FunctionSig {
                FunctionName = "llLinksetDataDelete",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.String},
                ParamNames = new string[] {"name"},
                TableIndex = 578
            }},
            {"llLinksetDataDeleteProtected", new FunctionSig {
                FunctionName = "llLinksetDataDeleteProtected",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.String, VarType.String},
                ParamNames = new string[] {"name", "pass"},
                TableIndex = 579
            }},
            {"llLinksetDataReset", new FunctionSig {
                FunctionName = "llLinksetDataReset",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 580
            }},
            {"llLinksetDataDeleteFound", new FunctionSig {
                FunctionName = "llLinksetDataDeleteFound",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] {VarType.String, VarType.String},
                ParamNames = new string[] {"pattern", "pass"},
                TableIndex = 581
            }},
            {"llLinksetDataCountFound", new FunctionSig {
                FunctionName = "llLinksetDataCountFound",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {VarType.String},
                ParamNames = new string[] {"pattern"},
                TableIndex = 582
            }},
            {"llLinksetDataListKeys", new FunctionSig {
                FunctionName = "llLinksetDataListKeys",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] {VarType.Integer, VarType.Integer},
                ParamNames = new string[] {"start", "count"},
                TableIndex = 583
            }},
            {"llLinksetDataFindKeys", new FunctionSig {
                FunctionName = "llLinksetDataFindKeys",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] {VarType.String, VarType.Integer, VarType.Integer},
                ParamNames = new string[] {"pattern", "start", "count"},
                TableIndex = 584
            }},
			// ── Tier 1: EEP / Environment ──
            {"llGetDayLength", new FunctionSig {
                FunctionName = "llGetDayLength",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 585
            }},
            {"llGetDayOffset", new FunctionSig {
                FunctionName = "llGetDayOffset",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 586
            }},
            {"llGetRegionDayLength", new FunctionSig {
                FunctionName = "llGetRegionDayLength",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 587
            }},
            {"llGetRegionDayOffset", new FunctionSig {
                FunctionName = "llGetRegionDayOffset",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 588
            }},
            {"llGetMoonDirection", new FunctionSig {
                FunctionName = "llGetMoonDirection",
                ReturnType = VarType.Vector,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 589
            }},
            {"llGetMoonRotation", new FunctionSig {
                FunctionName = "llGetMoonRotation",
                ReturnType = VarType.Rotation,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 590
            }},
            {"llGetSunRotation", new FunctionSig {
                FunctionName = "llGetSunRotation",
                ReturnType = VarType.Rotation,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 591
            }},
            {"llGetRegionSunDirection", new FunctionSig {
                FunctionName = "llGetRegionSunDirection",
                ReturnType = VarType.Vector,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 592
            }},
            {"llGetRegionSunRotation", new FunctionSig {
                FunctionName = "llGetRegionSunRotation",
                ReturnType = VarType.Rotation,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 593
            }},
            {"llGetRegionMoonDirection", new FunctionSig {
                FunctionName = "llGetRegionMoonDirection",
                ReturnType = VarType.Vector,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 594
            }},
            {"llGetRegionMoonRotation", new FunctionSig {
                FunctionName = "llGetRegionMoonRotation",
                ReturnType = VarType.Rotation,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 595
            }},
            {"llGetRegionLightDir", new FunctionSig {
                FunctionName = "llGetRegionLightDir",
                ReturnType = VarType.Vector,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 596
            }},
			// ── Tier 2: PBR / GLTF Materials ──
            {"llGetRenderMaterial", new FunctionSig {
                FunctionName = "llGetRenderMaterial",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] { VarType.Integer },
                ParamNames = new string[] { "face" },
                TableIndex = 598
            }},
            {"llSetRenderMaterial", new FunctionSig {
                FunctionName = "llSetRenderMaterial",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] { VarType.String, VarType.Integer },
                ParamNames = new string[] { "material_id", "face" },
                TableIndex = 599
            }},
            {"llIsLinkGLTFMaterial", new FunctionSig {
                FunctionName = "llIsLinkGLTFMaterial",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] { VarType.Integer, VarType.Integer },
                ParamNames = new string[] { "link", "face" },
                TableIndex = 600
            }},
            {"llSetLinkGLTFOverrides", new FunctionSig {
                FunctionName = "llSetLinkGLTFOverrides",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] { VarType.Integer, VarType.Integer, VarType.List },
                ParamNames = new string[] { "link", "face", "overrides" },
                TableIndex = 601
            }},
            {"llRenderMaterial", new FunctionSig {
                FunctionName = "llRenderMaterial",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] { VarType.String },
                ParamNames = new string[] { "material_id" },
                TableIndex = 602
            }},

// ── Tier 3: Combat 2.0 ──
            {"llGetHealth", new FunctionSig {
                FunctionName = "llGetHealth",
                ReturnType = VarType.Float,
                ParamTypes = new VarType[] { VarType.String },
                ParamNames = new string[] { "id" },
                TableIndex = 603
            }},
            {"llAdjustDamage", new FunctionSig {
                FunctionName = "llAdjustDamage",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] { VarType.String, VarType.Float },
                ParamNames = new string[] { "id", "amount" },
                TableIndex = 604
            }},
            {"llSetHealth", new FunctionSig {
                FunctionName = "llSetHealth",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] { VarType.String, VarType.Float },
                ParamNames = new string[] { "id", "health" },
                TableIndex = 605
            }},
			// ── Tier 4: Pathfinding Shims ──
            {"llGetClosestNavPoint", new FunctionSig {
                FunctionName = "llGetClosestNavPoint",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] { VarType.Vector, VarType.List },
                ParamNames = new string[] { "point", "options" },
                TableIndex = 606
            }},
            {"llGetStaticPath", new FunctionSig {
                FunctionName = "llGetStaticPath",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] { VarType.Vector, VarType.Vector, VarType.Float, VarType.List },
                ParamNames = new string[] { "start", "end", "radius", "parameters" },
                TableIndex = 607
            }},
            {"llExecCharacterCmd", new FunctionSig {
                FunctionName = "llExecCharacterCmd",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] { VarType.Integer, VarType.List },
                ParamNames = new string[] { "command", "parameters" },
                TableIndex = 608
            }},
            {"llDeleteCharacter", new FunctionSig {
                FunctionName = "llDeleteCharacter",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 609
            }},
			// ── Tier 5: Experience KVP Store ──
            {"llCreateKeyValue", new FunctionSig {
                FunctionName = "llCreateKeyValue",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] { VarType.String, VarType.String },
                ParamNames = new string[] { "key", "value" },
                TableIndex = 610
            }},
            {"llReadKeyValue", new FunctionSig {
                FunctionName = "llReadKeyValue",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] { VarType.String },
                ParamNames = new string[] { "key" },
                TableIndex = 611
            }},
            {"llUpdateKeyValue", new FunctionSig {
                FunctionName = "llUpdateKeyValue",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] { VarType.String, VarType.String, VarType.String },
                ParamNames = new string[] { "key", "value", "check" },
                TableIndex = 612
            }},
            {"llDeleteKeyValue", new FunctionSig {
                FunctionName = "llDeleteKeyValue",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] { VarType.String },
                ParamNames = new string[] { "key" },
                TableIndex = 613
            }},
            {"llKeyCountKeyValue", new FunctionSig {
                FunctionName = "llKeyCountKeyValue",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 614
            }},
            {"llKeysKeyValue", new FunctionSig {
                FunctionName = "llKeysKeyValue",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] { VarType.Integer, VarType.Integer },
                ParamNames = new string[] { "start", "count" },
                TableIndex = 615
            }},
            {"llDataSizeKeyValue", new FunctionSig {
                FunctionName = "llDataSizeKeyValue",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 616
            }},
            {"llClearKeyValue", new FunctionSig {
                FunctionName = "llClearKeyValue",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 617
            }},
            {"llCreateKeyValueSL", new FunctionSig {
                FunctionName = "llCreateKeyValueSL",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] { VarType.String, VarType.String },
                ParamNames = new string[] { "key", "value" },
                TableIndex = 618
            }},
            {"llReadKeyValueSL", new FunctionSig {
                FunctionName = "llReadKeyValueSL",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] { VarType.String },
                ParamNames = new string[] { "key" },
                TableIndex = 619
            }},
            {"llUpdateKeyValueSL", new FunctionSig {
                FunctionName = "llUpdateKeyValueSL",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] { VarType.String, VarType.String, VarType.String },
                ParamNames = new string[] { "key", "value", "check" },
                TableIndex = 620
            }},
			
			// -- Pathfinding Character Functions (621-628) --
			{"llCreateCharacter", new FunctionSig {
				FunctionName = "llCreateCharacter",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] { VarType.List },
				ParamNames = new string[] { "options" },
				TableIndex = 621
			}},
			{"llNavigateTo", new FunctionSig {
				FunctionName = "llNavigateTo",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] { VarType.Vector, VarType.List },
				ParamNames = new string[] { "pos", "options" },
				TableIndex = 622
			}},
			{"llUpdateCharacter", new FunctionSig {
				FunctionName = "llUpdateCharacter",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] { VarType.List },
				ParamNames = new string[] { "options" },
				TableIndex = 623
			}},
			{"llPursue", new FunctionSig {
				FunctionName = "llPursue",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] { VarType.String, VarType.List },
				ParamNames = new string[] { "target", "options" },
				TableIndex = 624
			}},
			{"llEvade", new FunctionSig {
				FunctionName = "llEvade",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] { VarType.String, VarType.List },
				ParamNames = new string[] { "target", "options" },
				TableIndex = 625
			}},
			{"llFleeFrom", new FunctionSig {
				FunctionName = "llFleeFrom",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] { VarType.Vector, VarType.Float, VarType.List },
				ParamNames = new string[] { "source", "distance", "options" },
				TableIndex = 626
			}},
			{"llWanderWithin", new FunctionSig {
				FunctionName = "llWanderWithin",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] { VarType.Vector, VarType.Vector, VarType.List },
				ParamNames = new string[] { "origin", "distances", "options" },
				TableIndex = 627
			}},
			{"llPatrolPoints", new FunctionSig {
				FunctionName = "llPatrolPoints",
				ReturnType = VarType.Void,
				ParamTypes = new VarType[] { VarType.List, VarType.List },
				ParamNames = new string[] { "points", "options" },
				TableIndex = 628
			}},
            // ── Tier 6: Standalone ──
            {"llSignRSA", new FunctionSig {
                FunctionName = "llSignRSA",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] { VarType.String, VarType.String, VarType.String },
                ParamNames = new string[] { "data", "privateKeyPem", "algorithm" },
                TableIndex = 597
            }},
            {"llVerifyRSA", new FunctionSig {
                FunctionName = "llVerifyRSA",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] { VarType.String, VarType.String, VarType.String, VarType.String },
                ParamNames = new string[] { "data", "signature", "publicKeyPem", "algorithm" },
                TableIndex = 629
            }},
            {"llGetEnvironment", new FunctionSig {
                FunctionName = "llGetEnvironment",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] { VarType.Vector, VarType.List },
                ParamNames = new string[] { "pos", "params" },
                TableIndex = 630
            }},
            {"llSetEnvironment", new FunctionSig {
                FunctionName = "llSetEnvironment",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] { VarType.Vector, VarType.List },
                ParamNames = new string[] { "pos", "params" },
                TableIndex = 631
            }},
            {"llReplaceEnvironment", new FunctionSig {
                FunctionName = "llReplaceEnvironment",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] { VarType.Vector, VarType.String, VarType.Integer, VarType.Integer, VarType.Integer },
                ParamNames = new string[] { "pos", "environment", "track", "day_length", "day_offset" },
                TableIndex = 632
            }},
            {"llRequestUserKey", new FunctionSig {
                FunctionName = "llRequestUserKey",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] { VarType.String },
                ParamNames = new string[] { "username" },
                TableIndex = 633
            }},
            {"llSetAgentEnvironment", new FunctionSig {
                FunctionName = "llSetAgentEnvironment",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] { VarType.String, VarType.Float, VarType.List },
                ParamNames = new string[] { "agent", "transition", "params" },
                TableIndex = 634
            }},
            {"llReplaceAgentEnvironment", new FunctionSig {
                FunctionName = "llReplaceAgentEnvironment",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] { VarType.String, VarType.Float, VarType.String },
                ParamNames = new string[] { "agent", "transition", "environment" },
                TableIndex = 635
            }},
            {"llGetExperienceErrorMessage", new FunctionSig {
                FunctionName = "llGetExperienceErrorMessage",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] { VarType.Integer },
                ParamNames = new string[] { "error" },
                TableIndex = 636
            }},
            {"llSetAgentRot", new FunctionSig {
                FunctionName = "llSetAgentRot",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] { VarType.Rotation, VarType.Integer },
                ParamNames = new string[] { "rot", "relative" },
                TableIndex = 637
            }},
            {"llOpenFloater", new FunctionSig {
                FunctionName = "llOpenFloater",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] { VarType.String, VarType.String, VarType.List },
                ParamNames = new string[] { "name", "url", "params" },
                TableIndex = 638
            }},
            {"llCloseFloater", new FunctionSig {
                FunctionName = "llCloseFloater",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] { VarType.String },
                ParamNames = new string[] { "name" },
                TableIndex = 639
            }},
            {"llSitOnLink", new FunctionSig {
                FunctionName = "llSitOnLink",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] { VarType.String, VarType.Integer },
                ParamNames = new string[] { "agent", "link" },
                TableIndex = 640
            }},
            {"llRezObjectWithParams", new FunctionSig {
                FunctionName = "llRezObjectWithParams",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] { VarType.String, VarType.List },
                ParamNames = new string[] { "inventory", "params" },
                TableIndex = 641
            }},
            {"llGetMaterialOverride", new FunctionSig {
                FunctionName = "llGetMaterialOverride",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] { VarType.Integer, VarType.List },
                ParamNames = new string[] { "face", "params" },
                TableIndex = 642
            }},

            // ── Phase 31: 19 new functions (TableIndex 643–661) ──
            {"llHash", new FunctionSig {
                FunctionName = "llHash",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] { VarType.String },
                ParamNames = new string[] { "src" },
                TableIndex = 643
            }},
            {"llLinkAdjustSoundVolume", new FunctionSig {
                FunctionName = "llLinkAdjustSoundVolume",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] { VarType.Integer, VarType.Float },
                ParamNames = new string[] { "link", "volume" },
                TableIndex = 644
            }},
            {"llLinkSetSoundQueueing", new FunctionSig {
                FunctionName = "llLinkSetSoundQueueing",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] { VarType.Integer, VarType.Integer },
                ParamNames = new string[] { "link", "queue" },
                TableIndex = 645
            }},
            {"llLinkSetSoundRadius", new FunctionSig {
                FunctionName = "llLinkSetSoundRadius",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] { VarType.Integer, VarType.Float },
                ParamNames = new string[] { "link", "radius" },
                TableIndex = 646
            }},
            {"llLinkStopSound", new FunctionSig {
                FunctionName = "llLinkStopSound",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] { VarType.Integer },
                ParamNames = new string[] { "link" },
                TableIndex = 647
            }},
            {"llScriptProfiler", new FunctionSig {
                FunctionName = "llScriptProfiler",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] { VarType.Integer },
                ParamNames = new string[] { "flags" },
                TableIndex = 648
            }},
            {"llGiveAgentInventory", new FunctionSig {
                FunctionName = "llGiveAgentInventory",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] { VarType.String, VarType.String },
                ParamNames = new string[] { "destination", "inventory" },
                TableIndex = 649
            }},
            {"llMapBeacon", new FunctionSig {
                FunctionName = "llMapBeacon",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] { VarType.String, VarType.String, VarType.Vector },
                ParamNames = new string[] { "agent", "text", "color" },
                TableIndex = 650
            }},
            {"llGetStartString", new FunctionSig {
                FunctionName = "llGetStartString",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] {},
                ParamNames = new string[] {},
                TableIndex = 651
            }},
            {"llSetGroundTexture", new FunctionSig {
                FunctionName = "llSetGroundTexture",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] { VarType.String, VarType.Integer },
                ParamNames = new string[] { "texture", "corner" },
                TableIndex = 652
            }},
            {"llTargetedEmail", new FunctionSig {
                FunctionName = "llTargetedEmail",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] { VarType.Integer, VarType.String, VarType.String, VarType.String },
                ParamNames = new string[] { "target_type", "address", "subject", "message" },
                TableIndex = 653
            }},
            {"llTransferOwnership", new FunctionSig {
                FunctionName = "llTransferOwnership",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] { VarType.String },
                ParamNames = new string[] { "destination" },
                TableIndex = 654
            }},
            {"llDetectedDamage", new FunctionSig {
                FunctionName = "llDetectedDamage",
                ReturnType = VarType.Float,
                ParamTypes = new VarType[] { VarType.Integer },
                ParamNames = new string[] { "number" },
                TableIndex = 655
            }},
            {"llDamage", new FunctionSig {
                FunctionName = "llDamage",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] { VarType.String, VarType.Float, VarType.Integer },
                ParamNames = new string[] { "target", "amount", "damageType" },
                TableIndex = 656
            }},
            {"llSetLinkRenderMaterial", new FunctionSig {
                FunctionName = "llSetLinkRenderMaterial",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] { VarType.Integer, VarType.String, VarType.Integer },
                ParamNames = new string[] { "link", "material_id", "face" },
                TableIndex = 657
            }},
            {"llXorBase64", new FunctionSig {
                FunctionName = "llXorBase64",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] { VarType.String, VarType.String },
                ParamNames = new string[] { "s1", "s2" },
                TableIndex = 658
            }},
            {"llRequestExperiencePermissions", new FunctionSig {
                FunctionName = "llRequestExperiencePermissions",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] { VarType.String, VarType.String },
                ParamNames = new string[] { "agent", "name" },
                TableIndex = 659
            }},
            {"llAgentInExperience", new FunctionSig {
                FunctionName = "llAgentInExperience",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] { VarType.String },
                ParamNames = new string[] { "agent" },
                TableIndex = 660
            }},
            {"llGetExperienceDetails", new FunctionSig {
                FunctionName = "llGetExperienceDetails",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] { VarType.String },
                ParamNames = new string[] { "experienceId" },
                TableIndex = 661
            }},
            // ── Phase 33: 5 new functions (662–666) ──
            {"llGetPayPrice", new FunctionSig {
                FunctionName = "llGetPayPrice",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] { },
                ParamNames = new string[] { },
                TableIndex = 662
            }},
            {"llGetAgentRot", new FunctionSig {
                FunctionName = "llGetAgentRot",
                ReturnType = VarType.Rotation,
                ParamTypes = new VarType[] { },
                ParamNames = new string[] { },
                TableIndex = 663
            }},
            {"llGetGroundTexture", new FunctionSig {
                FunctionName = "llGetGroundTexture",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] { VarType.Integer },
                ParamNames = new string[] { "corner" },
                TableIndex = 664
            }},
            {"llGetVehicleFlags", new FunctionSig {
                FunctionName = "llGetVehicleFlags",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] { },
                ParamNames = new string[] { },
                TableIndex = 665
            }},
            {"llGetLinkGLTFOverrides", new FunctionSig {
                FunctionName = "llGetLinkGLTFOverrides",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] { VarType.Integer, VarType.Integer },
                ParamNames = new string[] { "link", "face" },
                TableIndex = 666
            }},

            // ── Phase 34: Bot Persistence (667–671) ──
            {"botSetPersistent", new FunctionSig {
                FunctionName = "botSetPersistent",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] { VarType.String, VarType.Integer },
                ParamNames = new string[] { "botID", "ttlSeconds" },
                TableIndex = 667
            }},
            {"botRemovePersistent", new FunctionSig {
                FunctionName = "botRemovePersistent",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] { VarType.String },
                ParamNames = new string[] { "botID" },
                TableIndex = 668
            }},
            {"botIsPersistent", new FunctionSig {
                FunctionName = "botIsPersistent",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] { VarType.String },
                ParamNames = new string[] { "botID" },
                TableIndex = 669
            }},
            {"botGetPersistentData", new FunctionSig {
                FunctionName = "botGetPersistentData",
                ReturnType = VarType.String,
                ParamTypes = new VarType[] { VarType.String, VarType.String },
                ParamNames = new string[] { "botID", "key" },
                TableIndex = 670
            }},
            {"botSetPersistentData", new FunctionSig {
                FunctionName = "botSetPersistentData",
                ReturnType = VarType.Integer,
                ParamTypes = new VarType[] { VarType.String, VarType.String, VarType.String },
                ParamNames = new string[] { "botID", "key", "value" },
                TableIndex = 671
            }},
            {"osTeleportAgent", new FunctionSig {
                FunctionName = "osTeleportAgent",
                ReturnType = VarType.Void,
                ParamTypes = new VarType[] { VarType.String, VarType.String, VarType.Vector, VarType.Vector },
                ParamNames = new string[] { "agent", "region", "pos", "lookat" },
                TableIndex = 672
            }},
            {"osGetAvatarList", new FunctionSig {
                FunctionName = "osGetAvatarList",
                ReturnType = VarType.List,
                ParamTypes = new VarType[] { },
                ParamNames = new string[] { },
                TableIndex = 673
            }},
         };
        }
}