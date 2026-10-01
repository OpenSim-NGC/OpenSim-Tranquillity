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

        // SL wiki Typecast: (string) of a float has 6 decimals - `(string) -PI; // "-3.141593" with
        // precision set to 6 decimal places by zero padding or rounding` - and of a vector or rotation 5 - `(string) <1.0,
        // 2.3, 4.56>; // "<1.00000, 2.30000, 4.56000>"`; lists use 6 for all three (wiki List). The float's own value is
        // written: formatting a float keeps only 7 significant digits on .NET (2147483520.0 printed "2147484000.000000"),
        // so each value is widened to double first, which is exact. Halcyon's rule: -0 prints as 0 (any value
        // that rounds to all zeros has no sign, as .NET Framework wrote it; .NET Core 3.0+ writes "-0.000000"). The
        // invariant culture gives Halcyon's "Infinity", "-Infinity" and "NaN" and a '.' whatever the host's culture.
        private static string Fixed(float f, string format)
        {
            string s = ((double)f).ToString(format, CultureInfo.InvariantCulture);
            if (s.Length > 1 && s[0] == '-' && s.IndexOfAny(NonZeroDigits) < 0) s = s.Substring(1);
            return s;
        }

        private static readonly char[] NonZeroDigits = { '1', '2', '3', '4', '5', '6', '7', '8', '9', 'I', 'N' };

        // "F": .NET Core 3.0+ writes a double's exact digits with it (a custom "0.000000" stops at 15 significant digits).
        private const string Five = "F5", Six = "F6";

        public static string Vector3ToStringWith5FractionalDigits(Vector3 vPrimitive)
        {
            return "<" + Fixed(vPrimitive.X, Five) + ", " + Fixed(vPrimitive.Y, Five) + ", " + Fixed(vPrimitive.Z, Five) + ">";
        }

        public static string QuaternionToStringWith5FractionalDigits(Quaternion rPrimitive)
        {
            return "<" + Fixed(rPrimitive.X, Five) + ", " + Fixed(rPrimitive.Y, Five) + ", " + Fixed(rPrimitive.Z, Five)
                + ", " + Fixed(rPrimitive.W, Five) + ">";
        }

        public static string Vector3ToStringWith6FractionalDigits(Vector3 vPrimitive)
        {
            return "<" + Fixed(vPrimitive.X, Six) + ", " + Fixed(vPrimitive.Y, Six) + ", " + Fixed(vPrimitive.Z, Six) + ">";
        }

        public static string QuaternionToStringWith6FractionalDigits(Quaternion rPrimitive)
        {
            return "<" + Fixed(rPrimitive.X, Six) + ", " + Fixed(rPrimitive.Y, Six) + ", " + Fixed(rPrimitive.Z, Six)
                + ", " + Fixed(rPrimitive.W, Six) + ">";
        }

        public static string FloatToStringWith6FractionalDigits(float f)
        {
            return Fixed(f, Six);
        }

        // Halcyon's string -> vector / rotation, its libomv Vector3.Parse / Quaternion.Parse
        // under TryParse (ThirdParty/libopenmetaverse/OpenMetaverseTypes/Vector3.cs:355-377, Quaternion.cs:664-697): every
        // '<' and '>' removed, split on ',', each part trimmed and parsed as .NET Framework's Single.Parse with en-US; too
        // few parts or a bad part fails. A vector takes the first three parts of any longer list; a rotation of exactly
        // three parts is Quaternion(x, y, z), W = sqrt(1 - x*x - y*y - z*z) or 0. The callers keep their failure values
        // (ZERO_VECTOR, ZERO_ROTATION).
        public static bool TryParseLslVector(string s, out Vector3 result)
        {
            result = Vector3.Zero;
            string[] split = SplitLslTuple(s);
            if (split == null || split.Length < 3) return false;
            if (!TryParseFrameworkSingle(split[0], out float x) || !TryParseFrameworkSingle(split[1], out float y)
                || !TryParseFrameworkSingle(split[2], out float z))
                return false;
            result = new Vector3(x, y, z);
            return true;
        }

        public static bool TryParseLslRotation(string s, out Quaternion result)
        {
            result = Quaternion.Identity;
            string[] split = SplitLslTuple(s);
            if (split == null || split.Length < 3) return false;
            if (!TryParseFrameworkSingle(split[0], out float x) || !TryParseFrameworkSingle(split[1], out float y)
                || !TryParseFrameworkSingle(split[2], out float z))
                return false;
            if (split.Length == 3)
            {
                float xyzsum = 1 - x * x - y * y - z * z;
                result = new Quaternion(x, y, z, (xyzsum > 0) ? (float)Math.Sqrt(xyzsum) : 0);
                return true;
            }
            if (!TryParseFrameworkSingle(split[3], out float w)) return false;
            result = new Quaternion(x, y, z, w);
            return true;
        }

        private static string[] SplitLslTuple(string s)
            => s == null ? null : s.Replace("<", String.Empty).Replace(">", String.Empty).Split(',');

        /// <summary>
        /// .NET Framework Single.Parse(s, en-US) as a Try: NumberStyles.Float | AllowThousands, surrounding white space
        /// allowed. Differences from .NET 10 kept out: a value too large for a float fails (Framework threw
        /// OverflowException; .NET Core 3.0+ gives infinity), and the only symbols are en-US Framework's exact
        /// "Infinity", "-Infinity" and "NaN" (.NET 10 also takes "∞" and any letter case).
        /// </summary>
        private static bool TryParseFrameworkSingle(string part, out float value)
        {
            value = 0;
            string t = part.Trim();
            if (t == "Infinity") { value = float.PositiveInfinity; return true; }
            if (t == "-Infinity") { value = float.NegativeInfinity; return true; }
            if (t == "NaN") { value = float.NaN; return true; }
            foreach (char c in t)
                if (char.IsLetter(c) && c != 'e' && c != 'E') return false;
            if (!double.TryParse(t, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double d)) return false;
            float f = (float)d;
            if (float.IsInfinity(f)) return false;
            value = f;
            return true;
        }
    }
}
