using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using OpenMetaverse;

namespace InWorldz.Phlox.Util
{
    internal class Encoding
    {
        private static readonly CultureInfo _cultureInfo = new CultureInfo("en-US", true);

        public static int GetInt(byte[] memory, int index)
        {
            int b1 = memory[index++] & 0xFF; // mask off sign-extended bits
            int b2 = memory[index++] & 0xFF;
            int b3 = memory[index++] & 0xFF;
            int b4 = memory[index++] & 0xFF;
            int word = b1 << (8 * 3) | b2 << (8 * 2) | b3 << (8 * 1) | b4;
            return word;
        }

        /// <summary>
        /// Write value at index into a byte array highest to lowest byte
        /// </summary>
        /// <param name="bytes"></param>
        /// <param name="index"></param>
        /// <param name="value"></param>
        public static void WriteInt(IList<byte> bytes, int index, int value)
        {
            bytes[index + 0] = (byte)((value >> (8 * 3)) & 0xFF); // get highest byte
            bytes[index + 1] = (byte)((value >> (8 * 2)) & 0xFF);
            bytes[index + 2] = (byte)((value >> (8 * 1)) & 0xFF);
            bytes[index + 3] = (byte)(value & 0xFF);
        }

        public static int CastToInt(string s)
        {
            int value;

            //if string begins with + remove it (fixes mantis 522)
            if (s != String.Empty && s[0] == '+')
            {
                s = s.Remove(0, 1);
            }

            Regex r = new Regex("(^[ ]*0[xX][0-9A-Fa-f][0-9A-Fa-f]*)|(^[ ]*-?[0-9][0-9]*)");
            Match m = r.Match(s);
            string v = m.Groups[0].Value;

            if (v == String.Empty)
            {
                value = 0;
            }
            else
            {
                try
                {
                    if (v.Contains("x") || v.Contains("X"))
                    {
                        value = int.Parse(v.Substring(2), System.Globalization.NumberStyles.HexNumber);
                    }
                    else
                    {
                        value = int.Parse(v, System.Globalization.NumberStyles.Integer);
                    }
                }
                catch (OverflowException)
                {
                    value = -1;
                }
            }

            return value;
        }

        public static float CastToFloat(string s)
        {
            float value;

            //if string begins with + remove it (fixes mantis 522)
            if (s != String.Empty && s[0] == '+')
            {
                s = s.Remove(0, 1);
            }

            Regex r = new Regex("^ *(\\+|-)?([0-9]+\\.?[0-9]*|\\.[0-9]+)([eE](\\+|-)?[0-9]+)?");
            Match m = r.Match(s);
            string v = m.Groups[0].Value;

            v = v.Trim();

            if (v == String.Empty || v == null)
                v = "0.0";
            else
                if (!v.Contains(".") && !v.ToLower().Contains("e"))
                    v = v + ".0";
                else
                    if (v.EndsWith("."))
                        v = v + "0";
            try
            {
                value = float.Parse(v, System.Globalization.NumberStyles.Float, _cultureInfo);
            }
            catch (OverflowException)
            {
                value = float.PositiveInfinity;
            }

            return value;
        }

        public static string Vector3ToStringWith5FractionalDigits(Vector3 vPrimitive)
        {
            return String.Format("<{0:0.00000}, {1:0.00000}, {2:0.00000}>", vPrimitive.X, vPrimitive.Y, vPrimitive.Z);
        }

        public static string QuaternionToStringWith5FractionalDigits(Quaternion rPrimitive)
        {
            return String.Format("<{0:0.00000}, {1:0.00000}, {2:0.00000}, {3:0.00000}>",
                rPrimitive.X, rPrimitive.Y, rPrimitive.Z, rPrimitive.W);
        }

        public static string Vector3ToStringWith6FractionalDigits(Vector3 vPrimitive)
        {
            return String.Format("<{0:0.000000}, {1:0.000000}, {2:0.000000}>", vPrimitive.X, vPrimitive.Y, vPrimitive.Z);
        }

        public static string QuaternionToStringWith6FractionalDigits(Quaternion rPrimitive)
        {
            return String.Format("<{0:0.000000}, {1:0.000000}, {2:0.000000}, {3:0.000000}>",
                rPrimitive.X, rPrimitive.Y, rPrimitive.Z, rPrimitive.W);
        }

        public static string FloatToStringWith6FractionalDigits(float f)
        {
            return f.ToString("0.000000");
        }
    }
}
