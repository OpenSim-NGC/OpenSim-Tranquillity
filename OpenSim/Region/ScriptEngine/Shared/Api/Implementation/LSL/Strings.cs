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
using System.Security.Cryptography;
using System.Text;
using OpenSim.Framework;
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
        public LSL_Key llMD5String(string src, int nonce)
        {
            return Util.Md5Hash(string.Format("{0}:{1}", src, nonce.ToString()), Encoding.UTF8);
        }

        public LSL_Key llSHA1String(string src)
        {
            return Util.SHA1Hash(src, Encoding.UTF8).ToLower();
        }

        public LSL_Key llSHA256String(LSL_Key input)
        {
            // Create a SHA256
            using (var sha256Hash = SHA256.Create())
            {
                // ComputeHash - returns byte array
                var bytes = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(input));
                return Util.bytesToHexString(bytes, true);
            }
        }

        /// <summary>
        ///     Return a portion of the designated string bounded by
        ///     inclusive indices (start and end). As usual, the negative
        ///     indices, and the tolerance for out-of-bound values, makes
        ///     this more complicated than it might otherwise seem.
        /// </summary>
        public LSL_String llGetSubString(string src, int start, int end)
        {
            // Normalize indices (if negative).
            // After normlaization they may still be
            // negative, but that is now relative to
            // the start, rather than the end, of the
            // sequence.

            if (start < 0) start = src.Length + start;
            if (end < 0) end = src.Length + end;

            // Conventional substring
            if (start <= end)
            {
                // Implies both bounds are out-of-range.
                if (end < 0 || start >= src.Length) return string.Empty;
                // If end is positive, then it directly
                // corresponds to the lengt of the substring
                // needed (plus one of course). BUT, it
                // must be within bounds.
                if (end >= src.Length) end = src.Length - 1;

                if (start < 0) return src.Substring(0, end + 1);
                // Both indices are positive
                return src.Substring(start, end + 1 - start);
            }

            // Inverted substring (end < start)

            // Implies both indices are below the
            // lower bound. In the inverted case, that
            // means the entire string will be returned
            // unchanged.
            if (start < 0) return src;
            // If both indices are greater than the upper
            // bound the result may seem initially counter
            // intuitive.
            if (end >= src.Length) return src;

            if (end < 0)
            {
                if (start < src.Length)
                    return src.Substring(start);
                return string.Empty;
            }

            if (start < src.Length)
                return src.Substring(0, end + 1) + src.Substring(start);
            return src.Substring(0, end + 1);
        }

        /// <summary>
        ///     Delete substring removes the specified substring bounded
        ///     by the inclusive indices start and end. Indices may be
        ///     negative (indicating end-relative) and may be inverted,
        ///     i.e. end < start.
        /// </summary>
        public LSL_String llDeleteSubString(string src, int start, int end)
        {
            // Normalize indices (if negative).
            // After normlaization they may still be
            // negative, but that is now relative to
            // the start, rather than the end, of the
            // sequence.
            if (start < 0) start = src.Length + start;
            if (end < 0) end = src.Length + end;
            // Conventionally delimited substring
            if (start <= end)
            {
                // If both bounds are outside of the existing
                // string, then return unchanged.
                if (end < 0 || start >= src.Length) return src;
                // At least one bound is in-range, so we
                // need to clip the out-of-bound argument.
                if (start < 0) start = 0;

                if (end >= src.Length) end = src.Length - 1;

                return src.Remove(start, end - start + 1);
            }
            // Inverted substring

            // In this case, out of bounds means that
            // the existing string is part of the cut.
            if (start < 0 || end >= src.Length) return string.Empty;

            if (end > 0)
            {
                if (start < src.Length)
                    return src.Remove(start).Remove(0, end + 1);
                return src.Remove(0, end + 1);
            }

            if (start < src.Length)
                return src.Remove(start);
            return src;
        }

        /// <summary>
        ///     Insert string inserts the specified string identified by src
        ///     at the index indicated by index. Index may be negative, in
        ///     which case it is end-relative. The index may exceed either
        ///     string bound, with the result being a concatenation.
        /// </summary>
        // this is actually wrong. according to SL wiki, this function should not support negative indexes.
        public LSL_String llInsertString(string dest, int index, string src)
        {
            // Normalize indices (if negative).
            // After normalization they may still be
            // negative, but that is now relative to
            // the start, rather than the end, of the
            // sequence.
            char c;
            if (index < 0)
            {
                index = dest.Length + index;

                // Negative now means it is less than the lower
                // bound of the string.
                if (index > 0)
                {
                    c = dest[index];
                    if (c >= 0xDC00 && c <= 0xDFFF)
                        --index;
                }

                if (index < 0) return src + dest;
            }
            else
            {
                c = dest[index];
                if (c >= 0xDC00 && c <= 0xDFFF)
                    ++index;
            }

            if (index >= dest.Length) return dest + src;

            // The index is in bounds.
            // In this case the index refers to the index that will
            // be assigned to the first character of the inserted string.
            // So unlike the other string operations, we do not add one
            // to get the correct string length.
            return dest.Substring(0, index) + src + dest.Substring(index);
        }

        public LSL_String llToUpper(string src)
        {
            return src.ToUpper();
        }

        public LSL_String llToLower(string src)
        {
            return src.ToLower();
        }

        public LSL_Integer llStringLength(string str)
        {
            if (str == null || str.Length <= 0)
                return 0;
            return str.Length;
        }


        public LSL_Integer llSubStringIndex(string source, string pattern)
        {
            if (string.IsNullOrEmpty(source))
                return -1;
            if (string.IsNullOrEmpty(pattern))
                return 0;
            return source.IndexOf(pattern);
        }

        public LSL_String llStringToBase64(string str)
        {
            try
            {
                byte[] encData_byte;
                encData_byte = Util.UTF8.GetBytes(str);
                var encodedData = Convert.ToBase64String(encData_byte);
                return encodedData;
            }
            catch
            {
                Error("llBase64ToString", "Error encoding string");
                return string.Empty;
            }
        }

        public LSL_String llBase64ToString(string str)
        {
            try
            {
                var b = Convert.FromBase64String(str);
                return Encoding.UTF8.GetString(b);
            }
            catch
            {
                Error("llBase64ToString", "Error decoding string");
                return string.Empty;
            }
        }

        public LSL_String llGetTimestamp()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
        }

        public LSL_String llEscapeURL(string url)
        {
            try
            {
                return Uri.EscapeDataString(url);
            }
            catch (Exception ex)
            {
                return "llEscapeURL: " + ex;
            }
        }

        public LSL_String llUnescapeURL(string url)
        {
            try
            {
                return Uri.UnescapeDataString(url);
            }
            catch (Exception ex)
            {
                return "llUnescapeURL: " + ex;
            }
        }


        public LSL_String llStringTrim(LSL_String src, LSL_Integer type)
        {
            if (type == ScriptBaseClass.STRING_TRIM_HEAD) return ((string)src).TrimStart();
            if (type == ScriptBaseClass.STRING_TRIM_TAIL) return ((string)src).TrimEnd();
            if (type == ScriptBaseClass.STRING_TRIM) return ((string)src).Trim();
            return src;
        }

        public LSL_String llChar(LSL_Integer unicode)
        {
            if (unicode == 0)
                return string.Empty;
            try
            {
                return char.ConvertFromUtf32(unicode);
            }
            catch
            {
            }

            return "\ufffd";
        }
    }
}