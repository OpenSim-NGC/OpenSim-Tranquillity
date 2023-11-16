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
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using OpenMetaverse;
using OpenMetaverse.Assets;
using OpenMetaverse.Rendering;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.PhysicsModules.SharedBase;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.Api.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using LSL_Float = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLFloat;
using LSL_Integer = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLInteger;
using LSL_Key = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_List = OpenSim.Region.ScriptEngine.Shared.LSL_Types.list;
using LSL_Rotation = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Quaternion;
using LSL_String = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_Vector = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Vector3;

namespace OpenSim.Region.ScriptEngine.Shared.Api
{
    public partial class LSL_Api : MarshalByRefObject, ILSL_Api, IScriptApi
    {
        public void llResetTime()
        {
            m_timer = Util.GetTimeStampMS();
        }

        public LSL_Float llGetObjectMass(LSL_Key id)
        {
            if (!UUID.TryParse(id, out var key) || key.IsZero())
                return 0;

            // return total object mass
            var part = World.GetSceneObjectPart(key);
            if (part != null)
                return part.ParentGroup.GetMass();

            // the object is null so the key is for an avatar
            var avatar = World.GetScenePresence(key);
            if (avatar != null)
            {
                if (avatar.IsChildAgent)
                    // reference http://www.lslwiki.net/lslwiki/wakka.php?wakka=llGetObjectMass
                    // child agents have a mass of 1.0
                    return 1;
                return avatar.GetMass();
            }

            return 0;
        }

        //These are the implementations of the various ll-functions used by the LSL scripts.
        public LSL_Float llSin(double f)
        {
            return Math.Sin(f);
        }

        public LSL_Float llCos(double f)
        {
            return Math.Cos(f);
        }

        public LSL_Float llTan(double f)
        {
            return Math.Tan(f);
        }

        public LSL_Float llAtan2(LSL_Float x, LSL_Float y)
        {
            return Math.Atan2(x, y);
        }

        public LSL_Float llSqrt(double f)
        {
            return Math.Sqrt(f);
        }

        public LSL_Float llPow(double fbase, double fexponent)
        {
            return Math.Pow(fbase, fexponent);
        }

        public LSL_Integer llAbs(LSL_Integer i)
        {
            // changed to replicate LSL behaviour whereby minimum int value is returned untouched.
            if (i == int.MinValue)
                return i;
            return Math.Abs(i);
        }

        public LSL_Float llFabs(double f)
        {
            return Math.Abs(f);
        }

        public LSL_Float llFrand(double mag)
        {
            lock (Util.RandomClass)
            {
                return Util.RandomClass.NextDouble() * mag;
            }
        }

        public LSL_Integer llFloor(double f)
        {
            return (int)Math.Floor(f);
        }

        public LSL_Integer llCeil(double f)
        {
            return (int)Math.Ceiling(f);
        }

        // Xantor 01/May/2008 fixed midpointrounding (2.5 becomes 3.0 instead of 2.0, default = ToEven)
        public LSL_Integer llRound(double f)
        {
            return (int)Math.Round(f, MidpointRounding.AwayFromZero);
        }

        //This next group are vector operations involving squaring and square root. ckrinke
        public LSL_Float llVecMag(LSL_Vector v)
        {
            return LSL_Vector.Mag(v);
        }

        public LSL_Vector llVecNorm(LSL_Vector v)
        {
            return LSL_Vector.Norm(v);
        }


        public LSL_Float llVecDist(LSL_Vector a, LSL_Vector b)
        {
            return VecDist(a, b);
        }

        //Now we start getting into quaternions which means sin/cos, matrices and vectors. ckrinke

        // Utility function for llRot2Euler

        public LSL_Vector llRot2Euler(LSL_Rotation q1)
        {
            var eul = new LSL_Vector();

            var sqw = q1.s * q1.s;
            var sqx = q1.x * q1.x;
            var sqy = q1.z * q1.z;
            var sqz = q1.y * q1.y;
            var unit = sqx + sqy + sqz + sqw; // if normalised is one, otherwise is correction factor
            var test = q1.x * q1.z + q1.y * q1.s;
            if (test > 0.4999 * unit)
            {
                // singularity at north pole
                eul.z = 2 * Math.Atan2(q1.x, q1.s);
                eul.y = Math.PI / 2;
                eul.x = 0;
                return eul;
            }

            if (test < -0.4999 * unit)
            {
                // singularity at south pole
                eul.z = -2 * Math.Atan2(q1.x, q1.s);
                eul.y = -Math.PI / 2;
                eul.x = 0;
                return eul;
            }

            eul.z = Math.Atan2(2 * q1.z * q1.s - 2 * q1.x * q1.y, sqx - sqy - sqz + sqw);
            eul.y = Math.Asin(2 * test / unit);
            eul.x = Math.Atan2(2 * q1.x * q1.s - 2 * q1.z * q1.y, -sqx + sqy - sqz + sqw);
            return eul;
        }

        public LSL_Rotation llEuler2Rot(LSL_Vector v)
        {
            double x, y, z, s;
            v.x *= 0.5;
            v.y *= 0.5;
            v.z *= 0.5;
            var c1 = Math.Cos(v.x);
            var c2 = Math.Cos(v.y);
            var c1c2 = c1 * c2;
            var s1 = Math.Sin(v.x);
            var s2 = Math.Sin(v.y);
            var s1s2 = s1 * s2;
            var c1s2 = c1 * s2;
            var s1c2 = s1 * c2;
            var c3 = Math.Cos(v.z);
            var s3 = Math.Sin(v.z);

            x = s1c2 * c3 + c1s2 * s3;
            y = c1s2 * c3 - s1c2 * s3;
            z = s1s2 * c3 + c1c2 * s3;
            s = c1c2 * c3 - s1s2 * s3;

            return new LSL_Rotation(x, y, z, s);
        }

        public LSL_Rotation llAxes2Rot(LSL_Vector fwd, LSL_Vector left, LSL_Vector up)
        {
            double s;
            var tr = fwd.x + left.y + up.z + 1.0;

            if (tr >= 1.0)
            {
                s = 0.5 / Math.Sqrt(tr);
                return new LSL_Rotation(
                    (left.z - up.y) * s,
                    (up.x - fwd.z) * s,
                    (fwd.y - left.x) * s,
                    0.25 / s);
            }

            var max = left.y > up.z ? left.y : up.z;

            if (max < fwd.x)
            {
                s = Math.Sqrt(fwd.x - (left.y + up.z) + 1.0);
                var x = s * 0.5;
                s = 0.5 / s;
                return new LSL_Rotation(
                    x,
                    (fwd.y + left.x) * s,
                    (up.x + fwd.z) * s,
                    (left.z - up.y) * s);
            }

            if (max == left.y)
            {
                s = Math.Sqrt(left.y - (up.z + fwd.x) + 1.0);
                var y = s * 0.5;
                s = 0.5 / s;
                return new LSL_Rotation(
                    (fwd.y + left.x) * s,
                    y,
                    (left.z + up.y) * s,
                    (up.x - fwd.z) * s);
            }

            s = Math.Sqrt(up.z - (fwd.x + left.y) + 1.0);
            var z = s * 0.5;
            s = 0.5 / s;
            return new LSL_Rotation(
                (up.x + fwd.z) * s,
                (left.z + up.y) * s,
                z,
                (fwd.y - left.x) * s);
        }

        public LSL_Vector llRot2Fwd(LSL_Rotation r)
        {
            double x, y, z, m;

            m = r.x * r.x + r.y * r.y + r.z * r.z + r.s * r.s;
            // m is always greater than zero
            // if m is not equal to 1 then Rotation needs to be normalized
            if (Math.Abs(1.0 - m) > 0.000001) // allow a little slop here for calculation precision
            {
                m = 1.0 / Math.Sqrt(m);
                r.x *= m;
                r.y *= m;
                r.z *= m;
                r.s *= m;
            }

            // Fast Algebric Calculations instead of Vectors & Quaternions Product
            x = r.x * r.x - r.y * r.y - r.z * r.z + r.s * r.s;
            y = 2 * (r.x * r.y + r.z * r.s);
            z = 2 * (r.x * r.z - r.y * r.s);
            return new LSL_Vector(x, y, z);
        }

        public LSL_Vector llRot2Left(LSL_Rotation r)
        {
            double x, y, z, m;

            m = r.x * r.x + r.y * r.y + r.z * r.z + r.s * r.s;
            // m is always greater than zero
            // if m is not equal to 1 then Rotation needs to be normalized
            if (Math.Abs(1.0 - m) > 0.000001) // allow a little slop here for calculation precision
            {
                m = 1.0 / Math.Sqrt(m);
                r.x *= m;
                r.y *= m;
                r.z *= m;
                r.s *= m;
            }

            // Fast Algebric Calculations instead of Vectors & Quaternions Product
            x = 2 * (r.x * r.y - r.z * r.s);
            y = -r.x * r.x + r.y * r.y - r.z * r.z + r.s * r.s;
            z = 2 * (r.x * r.s + r.y * r.z);
            return new LSL_Vector(x, y, z);
        }

        public LSL_Vector llRot2Up(LSL_Rotation r)
        {
            double x, y, z, m;

            m = r.x * r.x + r.y * r.y + r.z * r.z + r.s * r.s;
            // m is always greater than zero
            // if m is not equal to 1 then Rotation needs to be normalized
            if (Math.Abs(1.0 - m) > 0.000001) // allow a little slop here for calculation precision
            {
                m = 1.0 / Math.Sqrt(m);
                r.x *= m;
                r.y *= m;
                r.z *= m;
                r.s *= m;
            }

            // Fast Algebric Calculations instead of Vectors & Quaternions Product
            x = 2 * (r.x * r.z + r.y * r.s);
            y = 2 * (-r.x * r.s + r.y * r.z);
            z = -r.x * r.x - r.y * r.y + r.z * r.z + r.s * r.s;
            return new LSL_Vector(x, y, z);
        }

        public LSL_Rotation llRotBetween(LSL_Vector a, LSL_Vector b)
        {
            //A and B should both be normalized

            // This method mimics the 180 errors found in SL
            // See www.euclideanspace.com... angleBetween

            // Eliminate zero length
            LSL_Float vec_a_mag = LSL_Vector.MagSquare(a);
            LSL_Float vec_b_mag = LSL_Vector.MagSquare(b);
            if (vec_a_mag < 1e-12 ||
                vec_b_mag < 1e-12)
                return new LSL_Rotation(0.0f, 0.0f, 0.0f, 1.0f);

            // Normalize
            a = llVecNorm(a);
            b = llVecNorm(b);

            // Calculate axis and rotation angle
            var axis = a % b;
            var cos_theta = a * b;

            // Check if parallel
            if (cos_theta > 0.99999) return new LSL_Rotation(0.0f, 0.0f, 0.0f, 1.0f);
            // Check if anti-parallel
            if (cos_theta < -0.99999)
            {
                var orthog_axis = new LSL_Vector(1.0, 0.0, 0.0) - a.x / (a * a) * a;
                if (LSL_Vector.MagSquare(orthog_axis) < 1e-12)
                    orthog_axis = new LSL_Vector(0.0, 0.0, 1.0);
                return new LSL_Rotation((float)orthog_axis.x, (float)orthog_axis.y, (float)orthog_axis.z, 0.0);
            }

            // other rotation
            var theta = (LSL_Float)Math.Acos(cos_theta) * 0.5f;
            axis = llVecNorm(axis);
            double x, y, z, s, t;
            s = Math.Cos(theta);
            t = Math.Sin(theta);
            x = axis.x * t;
            y = axis.y * t;
            z = axis.z * t;
            return new LSL_Rotation(x, y, z, s);
        }

        public LSL_Float llGround(LSL_Vector offset)
        {
            var pos = m_host.GetWorldPosition() + (Vector3)offset;

            //Get the slope normal.  This gives us the equation of the plane tangent to the slope.
            var vsn = llGroundNormal(offset);

            // Clamp to valid position
            if (pos.X < 0)
                pos.X = 0;
            else if (pos.X >= World.Heightmap.Width)
                pos.X = World.Heightmap.Width - 1;
            if (pos.Y < 0)
                pos.Y = 0;
            else if (pos.Y >= World.Heightmap.Height)
                pos.Y = World.Heightmap.Height - 1;

            //Get the height for the integer coordinates from the Heightmap
            var baseheight = World.Heightmap[(int)pos.X, (int)pos.Y];

            //Calculate the difference between the actual coordinates and the integer coordinates
            var xdiff = pos.X - (int)pos.X;
            var ydiff = pos.Y - (int)pos.Y;

            //Use the equation of the tangent plane to adjust the height to account for slope

            return (vsn.x * xdiff + vsn.y * ydiff) / (-1 * vsn.z) + baseheight;
        }

        public LSL_Float llCloud(LSL_Vector offset)
        {
            var cloudCover = 0f;
            var module = World.RequestModuleInterface<ICloudModule>();
            if (module != null)
            {
                var pos = m_host.GetWorldPosition();
                var x = (int)(pos.X + offset.x);
                var y = (int)(pos.Y + offset.y);

                cloudCover = module.CloudCover(x, y, 0);
            }

            return cloudCover;
        }

        public LSL_Vector llWind(LSL_Vector offset)
        {
            var wind = new LSL_Vector(0, 0, 0);
            var module = World.RequestModuleInterface<IWindModule>();
            if (module != null)
            {
                var pos = m_host.GetWorldPosition();
                var x = (int)(pos.X + offset.x);
                var y = (int)(pos.Y + offset.y);

                var windSpeed = module.WindSpeed(x, y, 0);

                wind.x = windSpeed.X;
                wind.y = windSpeed.Y;
            }

            return wind;
        }


        public void llApplyImpulse(LSL_Vector force, LSL_Integer local)
        {
            //No energy force yet
            Vector3 v = force;
            if (v.Length() > 20000.0f)
            {
                v.Normalize();
                v = v * 20000.0f;
            }

            m_host.ApplyImpulse(v, local != 0);
        }


        public void llApplyRotationalImpulse(LSL_Vector force, int local)
        {
            m_host.ParentGroup.RootPart.ApplyAngularImpulse(force, local != 0);
        }

        public void llSetTorque(LSL_Vector torque, int local)
        {
            m_host.ParentGroup.RootPart.SetAngularImpulse(torque, local != 0);
        }

        public LSL_Vector llGetTorque()
        {
            return new LSL_Vector(m_host.ParentGroup.GetTorque());
        }

        public void llSetForceAndTorque(LSL_Vector force, LSL_Vector torque, int local)
        {
            llSetForce(force, local);
            llSetTorque(torque, local);
        }


        public LSL_Vector llGetVel()
        {
            var vel = Vector3.Zero;

            if (m_host.ParentGroup.IsAttachment)
            {
                var avatar = m_host.ParentGroup.Scene.GetScenePresence(m_host.ParentGroup.AttachedAvatar);
                if (avatar != null)
                    vel = avatar.GetWorldVelocity();
            }
            else
            {
                vel = m_host.ParentGroup.RootPart.Velocity;
            }

            return new LSL_Vector(vel);
        }

        public LSL_Vector llGetAccel()
        {
            return new LSL_Vector(m_host.Acceleration);
        }

        public LSL_Vector llGetOmega()
        {
            var avel = m_host.AngularVelocity;
            return new LSL_Vector(avel.X, avel.Y, avel.Z);
        }

        public LSL_Float llGetTimeOfDay()
        {
            return DateTime.Now.TimeOfDay.TotalMilliseconds / 1000 % (3600 * 4);
        }

        public LSL_Float llGetWallclock()
        {
            var dateTimeOffset = DateTimeOffset.Now;

            if (string.IsNullOrEmpty(m_GetWallclockTimeZone) == false)
                dateTimeOffset =
                    TimeZoneInfo.ConvertTimeBySystemTimeZoneId(
                        DateTimeOffset.UtcNow, m_GetWallclockTimeZone);

            return Math.Truncate(dateTimeOffset.DateTime.TimeOfDay.TotalSeconds);
        }

        public LSL_Float llGetTime()
        {
            var ScriptTime = Util.GetTimeStampMS() - m_timer;
            return (float)Math.Round(ScriptTime / 1000.0, 3);
        }

        public LSL_Float llGetAndResetTime()
        {
            var now = Util.GetTimeStampMS();
            var ScriptTime = now - m_timer;
            m_timer = now;
            return (float)Math.Round(ScriptTime / 1000.0, 3);
        }

        public LSL_Integer llGiveMoney(LSL_Key destination, LSL_Integer amount)
        {
            if (m_item.PermsGranter.IsZero())
                return 0;

            if ((m_item.PermsMask & ScriptBaseClass.PERMISSION_DEBIT) == 0)
            {
                Error("llGiveMoney", "No permissions to give money");
                return 0;
            }

            if (!UUID.TryParse(destination, out var toID))
            {
                Error("llGiveMoney", "Bad key in llGiveMoney");
                return 0;
            }

            var money = World.RequestModuleInterface<IMoneyModule>();
            if (money == null)
            {
                NotImplemented("llGiveMoney");
                return 0;
            }

            Action<string> act = dontcare =>
            {
                money.ObjectGiveMoney(m_host.ParentGroup.RootPart.UUID, m_host.ParentGroup.RootPart.OwnerID,
                    toID, amount, UUID.Zero, out var reason);
            };

            m_AsyncCommands.DataserverPlugin.RegisterRequest(m_host.LocalId, m_item.ItemID, act);
            return 0;
        }


        public LSL_Float llGetMass()
        {
            if (m_host.ParentGroup.IsAttachment)
            {
                var attachedAvatar = World.GetScenePresence(m_host.ParentGroup.AttachedAvatar);

                if (attachedAvatar != null)
                    return attachedAvatar.GetMass();
                return 0;
            }

            // new SL always returns object mass
//                if (m_host.IsRoot)
//                {
            return m_host.ParentGroup.GetMass();
//                }
//                else
//                {
//                    return m_host.GetMass();
//                }
        }

        public LSL_Float llGetMassMKS()
        {
            return 100f * llGetMass();
        }

        public LSL_Float llWater(LSL_Vector offset)
        {
            return World.RegionInfo.RegionSettings.WaterHeight;
        }


        // Xantor 29/apr/2008
        // Returns rotation described by rotating angle radians about axis.
        // q = cos(a/2) + i (x * sin(a/2)) + j (y * sin(a/2)) + k (z * sin(a/2))
        public LSL_Rotation llAxisAngle2Rot(LSL_Vector axis, double angle)
        {
            double x, y, z, s, t;

            s = Math.Cos(angle * 0.5);
            t = Math.Sin(angle * 0.5); // temp value to avoid 2 more sin() calcs
            axis = LSL_Vector.Norm(axis);
            x = axis.x * t;
            y = axis.y * t;
            z = axis.z * t;

            return new LSL_Rotation(x, y, z, s);
        }

        /// <summary>
        ///     Returns the axis of rotation for a quaternion
        /// </summary>
        /// <returns></returns>
        /// <param name='rot'></param>
        public LSL_Vector llRot2Axis(LSL_Rotation rot)
        {
            rot.Normalize();

            var s = Math.Sqrt(1 - rot.s * rot.s);
            if (s < 1e-8)
                return new LSL_Vector(0, 0, 0);

            var invS = 1.0 / s;
            if (rot.s < 0)
                invS = -invS;
            return new LSL_Vector(rot.x * invS, rot.y * invS, rot.z * invS);
        }


        // Returns the angle of a quaternion (see llRot2Axis for the axis)
        public LSL_Float llRot2Angle(LSL_Rotation rot)
        {
            rot.Normalize();

            var angle = 2 * Math.Acos(rot.s);
            if (angle > Math.PI)
                angle = 2 * Math.PI - angle;

            return angle;
        }

        public LSL_Float llAcos(LSL_Float val)
        {
            return Math.Acos(val);
        }

        public LSL_Float llAsin(LSL_Float val)
        {
            return Math.Asin(val);
        }

        // jcochran 5/jan/2012
        public LSL_Float llAngleBetween(LSL_Rotation a, LSL_Rotation b)
        {
            var aa = a.x * a.x + a.y * a.y + a.z * a.z + a.s * a.s;
            var bb = b.x * b.x + b.y * b.y + b.z * b.z + b.s * b.s;
            var aa_bb = aa * bb;
            if (aa_bb == 0) return 0.0;
            var ab = a.x * b.x + a.y * b.y + a.z * b.z + a.s * b.s;
            var quotient = ab * ab / aa_bb;
            if (quotient >= 1.0) return 0.0;
            return Math.Acos(2 * quotient - 1);
        }


        public LSL_Vector llGetCenterOfMass()
        {
            return new LSL_Vector(m_host.GetCenterOfMass());
        }


        public LSL_Float llLog10(double val)
        {
            return Math.Log10(val);
        }

        public LSL_Float llLog(double val)
        {
            return Math.Log(val);
        }

        //  <summary>
        //  Converts a 32-bit integer into a Base64
        //  character string. Base64 character strings
        //  are always 8 characters long. All iinteger
        //  values are acceptable.
        //  </summary>
        //  <param name="number">
        //  32-bit integer to be converted.
        //  </param>
        //  <returns>
        //  8 character string. The 1st six characters
        //  contain the encoded number, the last two
        //  characters are padded with "=".
        //  </returns>

        public LSL_String llIntegerToBase64(int number)
        {
            // uninitialized string

            var imdt = new char[8];


            // Manually unroll the loop

            imdt[7] = '=';
            imdt[6] = '=';
            imdt[5] = i2ctable[(number << 4) & 0x3F];
            imdt[4] = i2ctable[(number >> 2) & 0x3F];
            imdt[3] = i2ctable[(number >> 8) & 0x3F];
            imdt[2] = i2ctable[(number >> 14) & 0x3F];
            imdt[1] = i2ctable[(number >> 20) & 0x3F];
            imdt[0] = i2ctable[(number >> 26) & 0x3F];

            return new string(imdt);
        }

        //  <summary>
        //  Converts an eight character base-64 string
        //  into a 32-bit integer.
        //  </summary>
        //  <param name="str">
        //  8 characters string to be converted. Other
        //  length strings return zero.
        //  </param>
        //  <returns>
        //  Returns an integer representing the
        //  encoded value providedint he 1st 6
        //  characters of the string.
        //  </returns>
        //  <remarks>
        //  This is coded to behave like LSL's
        //  implementation (I think), based upon the
        //  information available at the Wiki.
        //  If more than 8 characters are supplied,
        //  zero is returned.
        //  If a NULL string is supplied, zero will
        //  be returned.
        //  If fewer than 6 characters are supplied, then
        //  the answer will reflect a partial
        //  accumulation.
        //  <para>
        //  The 6-bit segments are
        //  extracted left-to-right in big-endian mode,
        //  which means that segment 6 only contains the
        //  two low-order bits of the 32 bit integer as
        //  its high order 2 bits. A short string therefore
        //  means loss of low-order information. E.g.
        //
        //  |<---------------------- 32-bit integer ----------------------->|<-Pad->|
        //  |<--Byte 0----->|<--Byte 1----->|<--Byte 2----->|<--Byte 3----->|<-Pad->|
        //  |3|3|2|2|2|2|2|2|2|2|2|2|1|1|1|1|1|1|1|1|1|1| | | | | | | | | | |P|P|P|P|
        //  |1|0|9|8|7|6|5|4|3|2|1|0|9|8|7|6|5|4|3|2|1|0|9|8|7|6|5|4|3|2|1|0|P|P|P|P|
        //  |  str[0]   |  str[1]   |  str[2]   |  str[3]   |  str[4]   |  str[6]   |
        //
        //  </para>
        //  </remarks>

        public LSL_Integer llBase64ToInteger(string str)
        {
            var number = 0;
            int digit;


            //    Require a well-fromed base64 string

            if (str.Length > 8)
                return 0;

            //    The loop is unrolled in the interests
            //    of performance and simple necessity.
            //
            //    MUST find 6 digits to be well formed
            //      -1 == invalid
            //       0 == padding

            if ((digit = c2itable[str[0]]) <= 0) return digit < 0 ? 0 : number;
            number += --digit << 26;

            if ((digit = c2itable[str[1]]) <= 0) return digit < 0 ? 0 : number;
            number += --digit << 20;

            if ((digit = c2itable[str[2]]) <= 0) return digit < 0 ? 0 : number;
            number += --digit << 14;

            if ((digit = c2itable[str[3]]) <= 0) return digit < 0 ? 0 : number;
            number += --digit << 8;

            if ((digit = c2itable[str[4]]) <= 0) return digit < 0 ? 0 : number;
            number += --digit << 2;

            if ((digit = c2itable[str[5]]) <= 0) return digit < 0 ? 0 : number;
            number += --digit >> 4;

            // ignore trailing padding

            return number;
        }

        public LSL_Float llGetGMTclock()
        {
            return DateTime.UtcNow.TimeOfDay.TotalSeconds;
        }


        public LSL_Integer llModPow(int a, int b, int c)
        {
            Math.DivRem((long)Math.Pow(a, b), c, out var tmp);
            ScriptSleep(m_sleepMsOnModPow);
            return (int)tmp;
        }

        public LSL_String llXorBase64Strings(string str1, string str2)
        {
            var padding = 0;

            ScriptSleep(300);

            if (str1.Length == 0)
                return string.Empty;
            if (str2.Length == 0)
                return str1;

            var len = str2.Length;
            if (len % 4 != 0) // LL is EVIL!!!!
            {
                while (str2.EndsWith("="))
                    str2 = str2.Substring(0, str2.Length - 1);

                len = str2.Length;
                var mod = len % 4;

                if (mod == 1)
                    str2 = str2.Substring(0, str2.Length - 1);
                else if (mod == 2)
                    str2 += "==";
                else if (mod == 3)
                    str2 += "=";
            }

            byte[] data1;
            byte[] data2;
            try
            {
                data1 = Convert.FromBase64String(str1);
                data2 = Convert.FromBase64String(str2);
            }
            catch
            {
                return string.Empty;
            }

            // Remove padding
            while (str1.EndsWith("="))
            {
                str1 = str1.Substring(0, str1.Length - 1);
                padding++;
            }

            while (str2.EndsWith("="))
                str2 = str2.Substring(0, str2.Length - 1);

            var d1 = new byte[str1.Length];
            var d2 = new byte[str2.Length];

            for (var i = 0; i < str1.Length; i++)
            {
                var idx = b64.IndexOf(str1.Substring(i, 1));
                if (idx == -1)
                    idx = 0;
                d1[i] = (byte)idx;
            }

            for (var i = 0; i < str2.Length; i++)
            {
                var idx = b64.IndexOf(str2.Substring(i, 1));
                if (idx == -1)
                    idx = 0;
                d2[i] = (byte)idx;
            }

            var output = string.Empty;

            for (var pos = 0; pos < d1.Length; pos++)
                output += b64[d1[pos] ^ d2[pos % d2.Length]];

            // Here's a funny thing: LL blithely violate the base64
            // standard pretty much everywhere. Here, padding is
            // added only if the first input string had it, rather
            // than when the data actually needs it. This can result
            // in invalid base64 being returned. Go figure.

            while (padding-- > 0)
                output += "=";

            return output;
        }

        public LSL_String llXorBase64StringsCorrect(string str1, string str2)
        {
            if (str1.Length == 0)
                return string.Empty;
            if (str2.Length == 0)
                return str1;

            var len = str2.Length;
            if (len % 4 != 0) // LL is EVIL!!!!
            {
                str2.TrimEnd('=');

                len = str2.Length;
                if (len == 0)
                    return str1;

                var mod = len % 4;

                if (mod == 1)
                    str2 = str2.Substring(0, len - 1);
                else if (mod == 2)
                    str2 += "==";
                else if (mod == 3)
                    str2 += "=";
            }

            byte[] data1;
            byte[] data2;
            try
            {
                data1 = Convert.FromBase64String(str1);
                data2 = Convert.FromBase64String(str2);
            }
            catch (Exception)
            {
                return string.Empty;
            }

            var len2 = data2.Length;
            if (len2 == 0)
                return str1;

            for (int pos = 0, pos2 = 0; pos < data1.Length; pos++)
            {
                data1[pos] ^= data2[pos2];
                if (++pos2 >= len2)
                    pos2 = 0;
            }

            return Convert.ToBase64String(data1);
        }

        public LSL_String llXorBase64(string str1, string str2)
        {
            if (string.IsNullOrEmpty(str2))
                return str1;

            str1 = truncateBase64(str1);
            if (string.IsNullOrEmpty(str1))
                return string.Empty;

            str2 = truncateBase64(str2);
            if (string.IsNullOrEmpty(str2))
                return str1;

            byte[] data1;
            byte[] data2;
            try
            {
                data1 = Convert.FromBase64String(str1);
                data2 = Convert.FromBase64String(str2);
            }
            catch (Exception)
            {
                return string.Empty;
            }

            var len2 = data2.Length;
            if (len2 == 0)
                return str1;

            for (int pos = 0, pos2 = 0; pos < data1.Length; pos++)
            {
                data1[pos] ^= data2[pos2];
                if (++pos2 >= len2)
                    pos2 = 0;
            }

            return Convert.ToBase64String(data1);
        }

        public LSL_List llCastRay(LSL_Vector start, LSL_Vector end, LSL_List options)
        {
            var list = new LSL_List();

            Vector3 rayStart = start;
            Vector3 rayEnd = end;
            var dir = rayEnd - rayStart;

            var dist = dir.LengthSquared();
            if (dist < 1e-6)
            {
                list.Add(new LSL_Integer(0));
                return list;
            }

            var count = 1;
            var detectPhantom = false;
            var dataFlags = 0;
            var rejectTypes = 0;

            for (var i = 0; i < options.Length; i += 2)
                if (options.GetLSLIntegerItem(i) == ScriptBaseClass.RC_MAX_HITS)
                    count = options.GetLSLIntegerItem(i + 1);
                else if (options.GetLSLIntegerItem(i) == ScriptBaseClass.RC_DETECT_PHANTOM)
                    detectPhantom = options.GetLSLIntegerItem(i + 1) > 0;
                else if (options.GetLSLIntegerItem(i) == ScriptBaseClass.RC_DATA_FLAGS)
                    dataFlags = options.GetLSLIntegerItem(i + 1);
                else if (options.GetLSLIntegerItem(i) == ScriptBaseClass.RC_REJECT_TYPES)
                    rejectTypes = options.GetLSLIntegerItem(i + 1);

            if (count > 16)
                count = 16;

            var results = new List<ContactResult>();

            bool checkTerrain = (rejectTypes & ScriptBaseClass.RC_REJECT_LAND) == 0;
            bool checkAgents = (rejectTypes & ScriptBaseClass.RC_REJECT_AGENTS) == 0;
            bool checkNonPhysical = (rejectTypes & ScriptBaseClass.RC_REJECT_NONPHYSICAL) == 0;
            bool checkPhysical = (rejectTypes & ScriptBaseClass.RC_REJECT_PHYSICAL) == 0;
            bool rejectHost = (rejectTypes & ScriptBaseClass.RC_REJECT_HOST) != 0;
            bool rejectHostGroup = (rejectTypes & ScriptBaseClass.RC_REJECT_HOSTGROUP) != 0;

            if (World.SupportsRayCastFiltered())
            {
                RayFilterFlags rayfilter = 0;
                if (checkTerrain)
                    rayfilter = RayFilterFlags.land;
                if (checkAgents)
                    rayfilter |= RayFilterFlags.agent;
                if (checkPhysical)
                    rayfilter |= RayFilterFlags.physical;
                if (checkNonPhysical)
                    rayfilter |= RayFilterFlags.nonphysical;
                if (detectPhantom)
                    rayfilter |= RayFilterFlags.LSLPhantom;

                if (rayfilter == 0)
                {
                    list.Add(new LSL_Integer(0));
                    return list;
                }

                rayfilter |= RayFilterFlags.BackFaceCull;

                dist = (float)Math.Sqrt(dist);
                var direction = dir * (1.0f / dist);

                // get some more contacts to sort ???
                var physresults = World.RayCastFiltered(rayStart, direction, dist, 2 * count, rayfilter);

                if (physresults != null) results = (List<ContactResult>)physresults;

                // for now physics doesn't detect sitted avatars so do it outside physics
                if (checkAgents)
                {
                    var agentHits = AvatarIntersection(rayStart, rayEnd, true);
                    foreach (var r in agentHits)
                        results.Add(r);
                }

                // TODO: Replace this with a better solution. ObjectIntersection can only
                // detect nonphysical phantoms. They are detected by virtue of being
                // nonphysical (e.g. no PhysActor) so will not conflict with detecting
                // physicsl phantoms as done by the physics scene
                // We don't want anything else but phantoms here.
                if (detectPhantom)
                {
                    var objectHits = ObjectIntersection(rayStart, rayEnd, false, false, true);
                    foreach (var r in objectHits)
                        results.Add(r);
                }

                // Double check this because of current ODE distance problems
                if (checkTerrain && dist > 60)
                {
                    var skipGroundCheck = false;

                    foreach (var c in results)
                        if (c.ConsumerID == 0) // Physics gave us a ground collision
                            skipGroundCheck = true;

                    if (!skipGroundCheck)
                    {
                        var tmp = dir.X * dir.X + dir.Y * dir.Y;
                        if (tmp > 2500)
                        {
                            var groundContact = GroundIntersection(rayStart, rayEnd);
                            if (groundContact != null)
                                results.Add((ContactResult)groundContact);
                        }
                    }
                }
            }
            else
            {
                if (checkAgents)
                {
                    var agentHits = AvatarIntersection(rayStart, rayEnd, false);
                    foreach (var r in agentHits)
                        results.Add(r);
                }

                if (checkPhysical || checkNonPhysical || detectPhantom)
                {
                    var objectHits =
                        ObjectIntersection(rayStart, rayEnd, checkPhysical, checkNonPhysical, detectPhantom);
                    for (var iter = 0; iter < objectHits.Length; iter++)
                    {
                        // Redistance the Depth because the Scene RayCaster returns distance from center to make the rezzing code simpler.
                        objectHits[iter].Depth = Vector3.Distance(objectHits[iter].Pos, rayStart);
                        results.Add(objectHits[iter]);
                    }
                }

                if (checkTerrain)
                {
                    var groundContact = GroundIntersection(rayStart, rayEnd);
                    if (groundContact != null)
                        results.Add((ContactResult)groundContact);
                }
            }

            results.Sort(delegate(ContactResult a, ContactResult b) { return a.Depth.CompareTo(b.Depth); });

            var values = 0;
            var thisgrp = m_host.ParentGroup;

            foreach (var result in results)
            {
                if (result.Depth > dist)
                    continue;

                // physics ray can return colisions with host prim
                if (rejectHost && m_host.LocalId == result.ConsumerID)
                    continue;

                var itemID = UUID.Zero;
                var linkNum = 0;

                var part = World.GetSceneObjectPart(result.ConsumerID);
                // It's a prim!
                if (part != null)
                {
                    if (rejectHostGroup && part.ParentGroup == thisgrp)
                        continue;

                    if ((dataFlags & ScriptBaseClass.RC_GET_ROOT_KEY) == ScriptBaseClass.RC_GET_ROOT_KEY)
                        itemID = part.ParentGroup.UUID;
                    else
                        itemID = part.UUID;

                    linkNum = part.LinkNum;
                }
                else
                {
                    var sp = World.GetScenePresence(result.ConsumerID);
                    /// It it a boy? a girl?
                    if (sp != null)
                        itemID = sp.UUID;
                }

                list.Add(new LSL_String(itemID.ToString()));
                list.Add(new LSL_String(result.Pos.ToString()));

                if ((dataFlags & ScriptBaseClass.RC_GET_LINK_NUM) == ScriptBaseClass.RC_GET_LINK_NUM)
                    list.Add(new LSL_Integer(linkNum));

                if ((dataFlags & ScriptBaseClass.RC_GET_NORMAL) == ScriptBaseClass.RC_GET_NORMAL)
                    list.Add(new LSL_Vector(result.Normal));

                values++;
                if (values >= count)
                    break;
            }

            list.Add(new LSL_Integer(values));
            return list;
        }

        public LSL_Integer llOrd(LSL_String s, LSL_Integer index)
        {
            if (string.IsNullOrEmpty(s))
                return 0;

            if (index < 0)
                index += s.Length;

            if (index < 0 || index >= s.Length)
                return 0;

            var c = s.m_string[index];
            if (c >= 0xdc00 && c <= 0xdfff)
            {
                --index;
                if (index < 0)
                    return 0;

                var a = c - 0xdc00;
                c = s.m_string[index];
                if (c < 0xd800 || c > 0xdbff)
                    return 0;
                c -= (char)(0xd800 - 0x40);
                return a + (c << 10);
            }

            if (c >= 0xd800)
            {
                if (c < 0xdc00)
                {
                    ++index;
                    if (index >= s.Length)
                        return 0;

                    c -= (char)(0xd800 - 0x40);
                    var a = c << 10;

                    c = s.m_string[index];
                    if (c < 0xdc00 || c > 0xdfff)
                        return 0;
                    c -= (char)0xdc00;
                    return a + c;
                }

                if (c < 0xe000) return 0;
            }

            return (int)c;
        }

        public LSL_Integer llHash(LSL_String s)
        {
            if (string.IsNullOrEmpty(s))
                return 0;
            var hash = 0;
            char c;
            for (var i = 0; i < s.Length; ++i)
            {
                hash *= 65599;
                // on modern intel/amd this is faster than the tradicional optimization:
                // hash = (hash << 6) + (hash << 16) - hash;
                c = s.m_string[i];
                if (c >= 0xd800)
                {
                    if (c < 0xdc00)
                    {
                        ++i;
                        if (i >= s.Length)
                            return 0;

                        c -= (char)(0xd800 - 0x40);
                        hash += c << 10;

                        c = s.m_string[i];
                        if (c < 0xdc00 || c > 0xdfff)
                            return 0;
                        c -= (char)0xdc00;
                    }
                    else if (c < 0xe000)
                    {
                        return 0;
                    }
                }

                hash += c;
            }

            return hash;
        }


        /// <summary>
        ///     Implementation of llCastRay similar to SL 2015-04-21.
        ///     http://wiki.secondlife.com/wiki/LlCastRay
        ///     Uses pure geometry, bounding shapes, meshing and no physics
        ///     for prims, sculpts, meshes, avatars and terrain.
        ///     Implements all flags, reject types and data flags.
        ///     Can handle both objects/groups and prims/parts, by config.
        ///     May sometimes be inaccurate owing to calculation precision,
        ///     meshing detail level and a bug in libopenmetaverse PrimMesher.
        /// </summary>
        public LSL_List llCastRayV3(LSL_Vector start, LSL_Vector end, LSL_List options)
        {
            var result = new LSL_List();

            // Prepare throttle data
            var calledMs = Environment.TickCount;
            var stopWatch = new Stopwatch();
            stopWatch.Start();
            var regionId = World.RegionInfo.RegionID;
            var userId = UUID.Zero;
            var msAvailable = 0;
            // Throttle per owner when attachment or "vehicle" (sat upon)
            if (m_host.ParentGroup.IsAttachment || m_host.ParentGroup.GetSittingAvatarsCount() > 0)
            {
                userId = m_host.OwnerID;
                msAvailable = m_msPerAvatarInCastRay;
            }
            // Throttle per parcel when not attachment or vehicle
            else
            {
                var land = World.GetLandData(m_host.GetWorldPosition());
                if (land != null)
                    msAvailable = m_msPerRegionInCastRay * land.Area / 65536;
            }

            // Clamp for "oversized" parcels on varregions
            if (msAvailable > m_msMaxInCastRay)
                msAvailable = m_msMaxInCastRay;

            // Check throttle data
            var fromCalledMs = calledMs - m_msThrottleInCastRay;
            lock (m_castRayCalls)
            {
                for (var i = m_castRayCalls.Count - 1; i >= 0; i--)
                    // Delete old calls from throttle data
                    if (m_castRayCalls[i].CalledMs < fromCalledMs)
                        m_castRayCalls.RemoveAt(i);
                    // Use current region (in multi-region sims)
                    else if (m_castRayCalls[i].RegionId.Equals(regionId))
                        // Reduce available time with recent calls
                        if (m_castRayCalls[i].UserId.Equals(userId))
                            msAvailable -= m_castRayCalls[i].UsedMs;

                // Return failure if not enough available time
                if (msAvailable < m_msMinInCastRay)
                {
                    result.Add(new LSL_Integer(ScriptBaseClass.RCERR_CAST_TIME_EXCEEDED));
                    return result;
                }
            }

            // Initialize
            var rayHits = new List<RayHit>();
            var tol = m_floatToleranceInCastRay;
            Vector3 pos1Ray = start;
            Vector3 pos2Ray = end;

            // Get input options
            var rejectTypes = 0;
            var dataFlags = 0;
            var maxHits = 1;
            var notdetectPhantom = true;
            for (var i = 0; i < options.Length; i += 2)
                if (options.GetLSLIntegerItem(i) == ScriptBaseClass.RC_REJECT_TYPES)
                    rejectTypes = options.GetLSLIntegerItem(i + 1);
                else if (options.GetLSLIntegerItem(i) == ScriptBaseClass.RC_DATA_FLAGS)
                    dataFlags = options.GetLSLIntegerItem(i + 1);
                else if (options.GetLSLIntegerItem(i) == ScriptBaseClass.RC_MAX_HITS)
                    maxHits = options.GetLSLIntegerItem(i + 1);
                else if (options.GetLSLIntegerItem(i) == ScriptBaseClass.RC_DETECT_PHANTOM)
                    notdetectPhantom = options.GetLSLIntegerItem(i + 1) == 0;
            if (maxHits > m_maxHitsInCastRay)
                maxHits = m_maxHitsInCastRay;
            bool rejectAgents = (rejectTypes & ScriptBaseClass.RC_REJECT_AGENTS) != 0;
            bool rejectPhysical = (rejectTypes & ScriptBaseClass.RC_REJECT_PHYSICAL) != 0;
            bool rejectNonphysical = (rejectTypes & ScriptBaseClass.RC_REJECT_NONPHYSICAL) != 0;
            bool rejectLand = (rejectTypes & ScriptBaseClass.RC_REJECT_LAND) != 0;
            bool getNormal = (dataFlags & ScriptBaseClass.RC_GET_NORMAL) != 0;
            bool getRootKey = (dataFlags & ScriptBaseClass.RC_GET_ROOT_KEY) != 0;
            bool getLinkNum = (dataFlags & ScriptBaseClass.RC_GET_LINK_NUM) != 0;

            // Calculate some basic parameters
            var vecRay = pos2Ray - pos1Ray;
            var rayLength = vecRay.Length();

            // Try to get a mesher and return failure if none, degenerate ray, or max 0 hits
            IRendering primMesher = null;
            var renderers = RenderingLoader.ListRenderers(Util.ExecutingDirectory());
            if (renderers.Count < 1 || rayLength < tol || m_maxHitsInCastRay < 1)
            {
                result.Add(new LSL_Integer(ScriptBaseClass.RCERR_UNKNOWN));
                return result;
            }

            primMesher = RenderingLoader.LoadRenderer(renderers[0]);

            // Iterate over all objects/groups and prims/parts in region
            World.ForEachSOG(
                delegate(SceneObjectGroup group)
                {
                    if (group.IsDeleted || group.RootPart == null)
                        return;
                    // Check group filters unless part filters are configured
                    var isPhysical = group.RootPart.PhysActor != null && group.RootPart.PhysActor.IsPhysical;
                    var isNonphysical = !isPhysical;
                    var isPhantom = group.IsPhantom || group.IsVolumeDetect;
                    var isAttachment = group.IsAttachment;
                    if (isPhysical && rejectPhysical)
                        return;
                    if (isNonphysical && rejectNonphysical)
                        return;
                    if (isPhantom && notdetectPhantom)
                        return;
                    if (isAttachment && !m_doAttachmentsInCastRay)
                        return;

                    // Parse object/group if passed filters
                    // Iterate over all prims/parts in object/group
                    foreach (var part in group.Parts)
                    {
                        // ignore PhysicsShapeType.None as physics engines do
                        // or we will get into trouble in future
                        if (part.PhysicsShapeType == (byte)PhysicsShapeType.None)
                            continue;
                        isPhysical = part.PhysActor != null && part.PhysActor.IsPhysical;
                        isNonphysical = !isPhysical;
                        isPhantom = (part.Flags & PrimFlags.Phantom) != 0 ||
                                    part.VolumeDetectActive;

                        if (isPhysical && rejectPhysical)
                            continue;
                        if (isNonphysical && rejectNonphysical)
                            continue;
                        if (isPhantom && notdetectPhantom)
                            continue;

                        // Parse prim/part and project ray if passed filters
                        var scalePart = part.Scale;
                        var posPart = part.GetWorldPosition();
                        var rotPart = part.GetWorldRotation();
                        var rotPartInv = Quaternion.Inverse(rotPart);
                        var pos1RayProj = (pos1Ray - posPart) * rotPartInv / scalePart;
                        var pos2RayProj = (pos2Ray - posPart) * rotPartInv / scalePart;

                        // Filter parts by shape bounding boxes
                        var shapeBoxMax = new Vector3(0.5f, 0.5f, 0.5f);
                        if (!part.Shape.SculptEntry)
                            shapeBoxMax = shapeBoxMax * new Vector3(m_primSafetyCoeffX, m_primSafetyCoeffY,
                                m_primSafetyCoeffZ);
                        shapeBoxMax = shapeBoxMax + new Vector3(tol, tol, tol);
                        if (RayIntersectsShapeBox(pos1RayProj, pos2RayProj, shapeBoxMax))
                        {
                            // Prepare data needed to check for ray hits
                            var rayTrans = new RayTrans
                            {
                                PartId = part.UUID,
                                GroupId = part.ParentGroup.UUID,
                                Link = group.PrimCount > 1 ? part.LinkNum : 0,
                                ScalePart = scalePart,
                                PositionPart = posPart,
                                RotationPart = rotPart,
                                ShapeNeedsEnds = true,
                                Position1Ray = pos1Ray,
                                Position1RayProj = pos1RayProj,
                                VectorRayProj = pos2RayProj - pos1RayProj
                            };

                            // Get detail level depending on type
                            var lod = 0;
                            // Mesh detail level
                            if (part.Shape.SculptEntry && part.Shape.SculptType == (byte)SculptType.Mesh)
                                lod = (int)m_meshLodInCastRay;
                            // Sculpt detail level
                            else if (part.Shape.SculptEntry && part.Shape.SculptType == (byte)SculptType.Mesh)
                                lod = (int)m_sculptLodInCastRay;
                            // Shape detail level
                            else if (!part.Shape.SculptEntry)
                                lod = (int)m_primLodInCastRay;

                            // Try to get cached mesh if configured
                            ulong meshKey = 0;
                            FacetedMesh mesh = null;
                            if (m_useMeshCacheInCastRay)
                            {
                                meshKey = part.Shape.GetMeshKey(Vector3.One, 4 << lod);
                                lock (m_cachedMeshes)
                                {
                                    m_cachedMeshes.TryGetValue(meshKey, out mesh);
                                }
                            }

                            // Create mesh if no cached mesh
                            if (mesh == null)
                            {
                                // Make an OMV prim to be able to mesh part
                                var omvPrim = part.Shape.ToOmvPrimitive(posPart, rotPart);
                                byte[] sculptAsset = null;
                                if (omvPrim.Sculpt != null)
                                    sculptAsset = World.AssetService.GetData(omvPrim.Sculpt.SculptTexture.ToString());

                                // When part is mesh, get mesh
                                if (omvPrim.Sculpt != null && omvPrim.Sculpt.Type == SculptType.Mesh &&
                                    sculptAsset != null)
                                {
                                    var meshAsset = new AssetMesh(omvPrim.Sculpt.SculptTexture, sculptAsset);
                                    FacetedMesh.TryDecodeFromAsset(omvPrim, meshAsset, m_meshLodInCastRay, out mesh);
                                    meshAsset = null;
                                }

                                // When part is sculpt, create mesh
                                // Quirk: Generated sculpt mesh is about 2.8% smaller in X and Y than visual sculpt.
                                else if (omvPrim.Sculpt != null && omvPrim.Sculpt.Type != SculptType.Mesh &&
                                         sculptAsset != null)
                                {
                                    var imgDecoder = World.RequestModuleInterface<IJ2KDecoder>();
                                    if (imgDecoder != null)
                                    {
                                        var sculpt = imgDecoder.DecodeToImage(sculptAsset);
                                        if (sculpt != null)
                                        {
                                            mesh = primMesher.GenerateFacetedSculptMesh(omvPrim, (Bitmap)sculpt,
                                                m_sculptLodInCastRay);
                                            sculpt.Dispose();
                                        }
                                    }
                                }

                                // When part is shape, create mesh
                                else if (omvPrim.Sculpt == null)
                                {
                                    if (
                                        omvPrim.PrimData.PathBegin == 0.0 && omvPrim.PrimData.PathEnd == 1.0 &&
                                        omvPrim.PrimData.PathTaperX == 0.0 && omvPrim.PrimData.PathTaperY == 0.0 &&
                                        omvPrim.PrimData.PathSkew == 0.0 &&
                                        omvPrim.PrimData.PathTwist - omvPrim.PrimData.PathTwistBegin == 0.0
                                    )
                                        rayTrans.ShapeNeedsEnds = false;
                                    mesh = primMesher.GenerateFacetedMesh(omvPrim, m_primLodInCastRay);
                                }

                                // Cache mesh if configured
                                if (m_useMeshCacheInCastRay && mesh != null)
                                    lock (m_cachedMeshes)
                                    {
                                        if (!m_cachedMeshes.ContainsKey(meshKey))
                                            m_cachedMeshes.Add(meshKey, mesh);
                                    }
                            }

                            // Check mesh for ray hits
                            AddRayInFacetedMesh(mesh, rayTrans, ref rayHits);
                            mesh = null;
                        }
                    }
                }
            );

            // Check avatar filter
            if (!rejectAgents)
                // Iterate over all avatars in region
                World.ForEachRootScenePresence(
                    delegate(ScenePresence sp)
                    {
                        // Get bounding box
                        BoundingBoxOfScenePresence(sp, out var lower, out var upper);
                        // Parse avatar
                        var scalePart = upper - lower;
                        var posPart = sp.AbsolutePosition;
                        var rotPart = sp.GetWorldRotation();
                        var rotPartInv = Quaternion.Inverse(rotPart);
                        posPart = posPart + (lower + upper) * 0.5f * rotPart;
                        // Project ray
                        var pos1RayProj = (pos1Ray - posPart) * rotPartInv / scalePart;
                        var pos2RayProj = (pos2Ray - posPart) * rotPartInv / scalePart;

                        // Filter avatars by shape bounding boxes
                        var shapeBoxMax = new Vector3(0.5f + tol, 0.5f + tol, 0.5f + tol);
                        if (RayIntersectsShapeBox(pos1RayProj, pos2RayProj, shapeBoxMax))
                        {
                            // Prepare data needed to check for ray hits
                            var rayTrans = new RayTrans
                            {
                                PartId = sp.UUID,
                                GroupId = sp.ParentPart != null ? sp.ParentPart.ParentGroup.UUID : sp.UUID,
                                Link = sp.ParentPart != null ? UUID2LinkNumber(sp.ParentPart, sp.UUID) : 0,
                                ScalePart = scalePart,
                                PositionPart = posPart,
                                RotationPart = rotPart,
                                ShapeNeedsEnds = false,
                                Position1Ray = pos1Ray,
                                Position1RayProj = pos1RayProj,
                                VectorRayProj = pos2RayProj - pos1RayProj
                            };

                            // Try to get cached mesh if configured
                            var prim = PrimitiveBaseShape.CreateSphere();
                            var lod = (int)m_avatarLodInCastRay;
                            var meshKey = prim.GetMeshKey(Vector3.One, 4 << lod);
                            FacetedMesh mesh = null;
                            if (m_useMeshCacheInCastRay)
                                lock (m_cachedMeshes)
                                {
                                    m_cachedMeshes.TryGetValue(meshKey, out mesh);
                                }

                            // Create mesh if no cached mesh
                            if (mesh == null)
                            {
                                // Make OMV prim and create mesh
                                prim.Scale = scalePart;
                                var omvPrim = prim.ToOmvPrimitive(posPart, rotPart);
                                mesh = primMesher.GenerateFacetedMesh(omvPrim, m_avatarLodInCastRay);

                                // Cache mesh if configured
                                if (m_useMeshCacheInCastRay && mesh != null)
                                    lock (m_cachedMeshes)
                                    {
                                        if (!m_cachedMeshes.ContainsKey(meshKey))
                                            m_cachedMeshes.Add(meshKey, mesh);
                                    }
                            }

                            // Check mesh for ray hits
                            AddRayInFacetedMesh(mesh, rayTrans, ref rayHits);
                            mesh = null;
                        }
                    }
                );

            // Check terrain filter
            if (!rejectLand)
            {
                // Parse terrain

                // Mesh terrain and check bounding box
                var triangles = TrisFromHeightmapUnderRay(pos1Ray, pos2Ray, out var lower, out var upper);
                lower.Z -= tol;
                upper.Z += tol;
                if ((pos1Ray.Z >= lower.Z || pos2Ray.Z >= lower.Z) && (pos1Ray.Z <= upper.Z || pos2Ray.Z <= upper.Z))
                {
                    // Prepare data needed to check for ray hits
                    var rayTrans = new RayTrans
                    {
                        PartId = UUID.Zero,
                        GroupId = UUID.Zero,
                        Link = 0,
                        ScalePart = new Vector3(1.0f, 1.0f, 1.0f),
                        PositionPart = Vector3.Zero,
                        RotationPart = Quaternion.Identity,
                        ShapeNeedsEnds = true,
                        Position1Ray = pos1Ray,
                        Position1RayProj = pos1Ray,
                        VectorRayProj = vecRay
                    };

                    // Check mesh
                    AddRayInTris(triangles, rayTrans, ref rayHits);
                    triangles = null;
                }
            }

            // Sort hits by ascending distance
            rayHits.Sort((s1, s2) => s1.Distance.CompareTo(s2.Distance));

            // Check excess hits per part and group
            for (var t = 0; t < 2; t++)
            {
                var maxHitsPerType = 0;
                var id = UUID.Zero;
                if (t == 0)
                    maxHitsPerType = m_maxHitsPerPrimInCastRay;
                else
                    maxHitsPerType = m_maxHitsPerObjectInCastRay;

                // Handle excess hits only when needed
                if (maxHitsPerType < m_maxHitsInCastRay)
                {
                    // Find excess hits
                    var hits = new Hashtable();
                    for (var i = rayHits.Count - 1; i >= 0; i--)
                    {
                        if (t == 0)
                            id = rayHits[i].PartId;
                        else
                            id = rayHits[i].GroupId;
                        if (hits.ContainsKey(id))
                            hits[id] = (int)hits[id] + 1;
                        else
                            hits[id] = 1;
                    }

                    // Remove excess hits
                    for (var i = rayHits.Count - 1; i >= 0; i--)
                    {
                        if (t == 0)
                            id = rayHits[i].PartId;
                        else
                            id = rayHits[i].GroupId;
                        var hit = (int)hits[id];
                        if (hit > m_maxHitsPerPrimInCastRay)
                        {
                            rayHits.RemoveAt(i);
                            hit--;
                            hits[id] = hit;
                        }
                    }
                }
            }

            // Parse hits into result list according to data flags
            var hitCount = rayHits.Count;
            if (hitCount > maxHits)
                hitCount = maxHits;
            for (var i = 0; i < hitCount; i++)
            {
                var rayHit = rayHits[i];
                if (getRootKey)
                    result.Add(new LSL_Key(rayHit.GroupId.ToString()));
                else
                    result.Add(new LSL_Key(rayHit.PartId.ToString()));
                result.Add(new LSL_Vector(rayHit.Position));
                if (getLinkNum)
                    result.Add(new LSL_Integer(rayHit.Link));
                if (getNormal)
                    result.Add(new LSL_Vector(rayHit.Normal));
            }

            result.Add(new LSL_Integer(hitCount));

            // Add to throttle data
            stopWatch.Stop();
            lock (m_castRayCalls)
            {
                var castRayCall = new CastRayCall
                {
                    RegionId = regionId,
                    UserId = userId,
                    CalledMs = calledMs,
                    UsedMs = (int)stopWatch.ElapsedMilliseconds
                };
                m_castRayCalls.Add(castRayCall);
            }

            // Return hits
            return result;
        }
    }
}