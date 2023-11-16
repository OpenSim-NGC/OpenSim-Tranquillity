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
using System.Text.RegularExpressions;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.Api.Interfaces;
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
        public LSL_List llListSort(LSL_List src, int stride, int ascending)
        {
            return src.Sort(stride, ascending == 1);
        }

        public LSL_Integer llGetListLength(LSL_List src)
        {
            return src.Length;
        }

        public LSL_Integer llList2Integer(LSL_List src, int index)
        {
            if (index < 0)
                index = src.Length + index;

            if (index >= src.Length || index < 0)
                return 0;

            var item = src.Data[index];

            // Vectors & Rotations always return zero in SL, but
            //  keys don't always return zero, it seems to be a bit complex.
            if (item is LSL_Vector || item is LSL_Rotation)
                return 0;

            try
            {
                if (item is LSL_Integer)
                    return (LSL_Integer)item;
                if (item is LSL_Float)
                    return Convert.ToInt32(((LSL_Float)item).value);
                ;
                return new LSL_Integer(item.ToString());
            }
            catch (FormatException)
            {
                return 0;
            }
        }

        public LSL_Float llList2Float(LSL_List src, int index)
        {
            if (index < 0)
                index = src.Length + index;

            if (index >= src.Length || index < 0)
                return 0;

            var item = src.Data[index];

            // Vectors & Rotations always return zero in SL
            if (item is LSL_Vector || item is LSL_Rotation)
                return 0;

            // valid keys seem to get parsed as integers then converted to floats
            if (item is LSL_Key)
            {
                var s = item.ToString();
                if (UUID.TryParse(s, out var uuidt))
                    return Convert.ToDouble(new LSL_Integer(s).value);
// we can't do this because a string is also a LSL_Key for now :(
//                else
//                    return 0;
            }

            try
            {
                if (item is LSL_Integer) return Convert.ToDouble(((LSL_Integer)item).value);

                if (item is LSL_Float) return Convert.ToDouble(((LSL_Float)item).value);

                if (item is LSL_String)
                {
                    var str = ((LSL_String)item).m_string;
                    var m = Regex.Match(str, "^\\s*(-?\\+?[,0-9]+\\.?[0-9]*)");
                    if (m != Match.Empty)
                    {
                        str = m.Value;
                        if (!double.TryParse(str, out var d))
                            return 0.0;
                        return d;
                    }

                    return 0.0;
                }

                return Convert.ToDouble(item);
            }
            catch (FormatException)
            {
                return 0.0;
            }
        }

        public LSL_String llList2String(LSL_List src, int index)
        {
            if (index < 0)
                index = src.Length + index;

            if (index >= src.Length || index < 0)
                return string.Empty;

            return src.Data[index].ToString();
        }

        public LSL_Key llList2Key(LSL_List src, int index)
        {
            if (index < 0)
                index = src.Length + index;

            if (index >= src.Length || index < 0)
                return string.Empty;

            var item = src.Data[index];

            // SL spits out an empty string for types other than key & string
            // At the time of patching, LSL_Key is currently LSL_String,
            // so the OR check may be a little redundant, but it's being done
            // for completion and should LSL_Key ever be implemented
            // as it's own struct
            // NOTE: 3rd case is needed because a NULL_KEY comes through as
            // type 'obj' and wrongly returns ""
            if (!(item is LSL_String ||
                  item is LSL_Key ||
                  item.ToString().Equals("00000000-0000-0000-0000-000000000000")))
                return string.Empty;

            return item.ToString();
        }

        public LSL_Vector llList2Vector(LSL_List src, int index)
        {
            if (index < 0)
                index = src.Length + index;

            if (index >= src.Length || index < 0)
                return new LSL_Vector(0, 0, 0);

            var item = src.Data[index];

            if (item.GetType() == typeof(LSL_Vector))
                return (LSL_Vector)item;

            // SL spits always out ZERO_VECTOR for anything other than
            // strings or vectors. Although keys always return ZERO_VECTOR,
            // it is currently difficult to make the distinction between
            // a string, a key as string and a string that by coincidence
            // is a string, so we're going to leave that up to the
            // LSL_Vector constructor.
            if (item is LSL_Vector)
                return (LSL_Vector)item;

            if (item is LSL_String || item is string) // xengine sees string
                return new LSL_Vector(item.ToString());

            return new LSL_Vector(0, 0, 0);
        }

        public LSL_Rotation llList2Rot(LSL_List src, int index)
        {
            if (index < 0)
                index = src.Length + index;

            if (index >= src.Length || index < 0)
                return new LSL_Rotation(0, 0, 0, 1);

            var item = src.Data[index];

            // SL spits always out ZERO_ROTATION for anything other than
            // strings or vectors. Although keys always return ZERO_ROTATION,
            // it is currently difficult to make the distinction between
            // a string, a key as string and a string that by coincidence
            // is a string, so we're going to leave that up to the
            // LSL_Rotation constructor.

            if (item.GetType() == typeof(LSL_Rotation))
                return (LSL_Rotation)item;

            if (item is LSL_String || item is string) // xengine sees string)
                return new LSL_Rotation(src.Data[index].ToString());

            return new LSL_Rotation(0, 0, 0, 1);
        }

        public LSL_List llList2List(LSL_List src, int start, int end)
        {
            return src.GetSublist(start, end);
        }

        public LSL_List llDeleteSubList(LSL_List src, int start, int end)
        {
            return src.DeleteSublist(start, end);
        }

        public LSL_Integer llGetListEntryType(LSL_List src, int index)
        {
            if (index < 0)
                index = src.Length + index;
            if (index >= src.Length || index < 0)
                return 0;

            var o = src.Data[index];
            if (o is LSL_Integer || o is int)
                return 1;
            if (o is LSL_Float || o is float || o is double)
                return 2;
            if (o is LSL_String || o is string)
            {
                if (UUID.TryParse(o.ToString(), out var tuuid))
                    return 4;
                return 3;
            }

            if (o is LSL_Key)
                return 4;
            if (o is LSL_Vector)
                return 5;
            if (o is LSL_Rotation)
                return 6;
            if (o is LSL_List)
                return 7;
            return 0;
        }

        /// <summary>
        ///     Process the supplied list and return the
        ///     content of the list formatted as a comma
        ///     separated list. There is a space after
        ///     each comma.
        /// </summary>
        public LSL_String llList2CSV(LSL_List src)
        {
            return string.Join(", ",
                new List<object>(src.Data).ConvertAll(o => { return o.ToString(); }).ToArray());
        }

        /// <summary>
        ///     The supplied string is scanned for commas
        ///     and converted into a list. Commas are only
        ///     effective if they are encountered outside
        ///     of '<' '>' delimiters. Any whitespace
        ///     before or after an element is trimmed.
        /// </summary>
        public LSL_List llCSV2List(string src)
        {
            var result = new LSL_List();
            var parens = 0;
            var start = 0;
            var length = 0;

            for (var i = 0; i < src.Length; i++)
                switch (src[i])
                {
                    case '<':
                        parens++;
                        length++;
                        break;
                    case '>':
                        if (parens > 0)
                            parens--;
                        length++;
                        break;
                    case ',':
                        if (parens == 0)
                        {
                            result.Add(new LSL_String(src.Substring(start, length).Trim()));
                            start += length + 1;
                            length = 0;
                        }
                        else
                        {
                            length++;
                        }

                        break;
                    default:
                        length++;
                        break;
                }

            result.Add(new LSL_String(src.Substring(start, length).Trim()));

            return result;
        }

        /// <summary>
        ///     Randomizes the list, be arbitrarily reordering
        ///     sublists of stride elements. As the stride approaches
        ///     the size of the list, the options become very
        ///     limited.
        /// </summary>
        /// <remarks>
        ///     This could take a while for very large list
        ///     sizes.
        /// </remarks>
        public LSL_List llListRandomize(LSL_List src, int stride)
        {
            LSL_List result;
            var rand = new BetterRandom();

            int chunkk;
            int[] chunks;


            if (stride <= 0) stride = 1;

            // Stride MUST be a factor of the list length
            // If not, then return the src list. This also
            // traps those cases where stride > length.

            if (src.Length != stride && src.Length % stride == 0)
            {
                chunkk = src.Length / stride;

                chunks = new int[chunkk];

                for (var i = 0; i < chunkk; i++) chunks[i] = i;

                // Knuth shuffle the chunkk index
                for (var i = chunkk - 1; i > 0; i--)
                {
                    // Elect an unrandomized chunk to swap
                    var index = rand.Next(i + 1);

                    // and swap position with first unrandomized chunk
                    var tmp = chunks[i];
                    chunks[i] = chunks[index];
                    chunks[index] = tmp;
                }

                // Construct the randomized list

                result = new LSL_List();

                for (var i = 0; i < chunkk; i++)
                for (var j = 0; j < stride; j++)
                    result.Add(src.Data[chunks[i] * stride + j]);
            }
            else
            {
                var array = new object[src.Length];
                Array.Copy(src.Data, 0, array, 0, src.Length);
                result = new LSL_List(array);
            }

            return result;
        }

        /// <summary>
        ///     Elements in the source list starting with 0 and then
        ///     every i+stride. If the stride is negative then the scan
        ///     is backwards producing an inverted result.
        ///     Only those elements that are also in the specified
        ///     range are included in the result.
        /// </summary>
        public LSL_List llList2ListStrided(LSL_List src, int start, int end, int stride)
        {
            var result = new LSL_List();
            var si = new int[2];
            var ei = new int[2];
            var twopass = false;


            //  First step is always to deal with negative indices

            if (start < 0)
                start = src.Length + start;
            if (end < 0)
                end = src.Length + end;

            //  Out of bounds indices are OK, just trim them
            //  accordingly

            if (start > src.Length)
                start = src.Length;

            if (end > src.Length)
                end = src.Length;

            if (stride == 0)
                stride = 1;

            //  There may be one or two ranges to be considered

            if (start != end)
            {
                if (start <= end)
                {
                    si[0] = start;
                    ei[0] = end;
                }
                else
                {
                    si[1] = start;
                    ei[1] = src.Length;
                    si[0] = 0;
                    ei[0] = end;
                    twopass = true;
                }

                //  The scan always starts from the beginning of the
                //  source list, but members are only selected if they
                //  fall within the specified sub-range. The specified
                //  range values are inclusive.
                //  A negative stride reverses the direction of the
                //  scan producing an inverted list as a result.

                if (stride > 0)
                    for (var i = 0; i < src.Length; i += stride)
                    {
                        if (i <= ei[0] && i >= si[0])
                            result.Add(src.Data[i]);
                        if (twopass && i >= si[1] && i <= ei[1])
                            result.Add(src.Data[i]);
                    }
                else if (stride < 0)
                    for (var i = src.Length - 1; i >= 0; i += stride)
                    {
                        if (i <= ei[0] && i >= si[0])
                            result.Add(src.Data[i]);
                        if (twopass && i >= si[1] && i <= ei[1])
                            result.Add(src.Data[i]);
                    }
            }
            else
            {
                if (start % stride == 0) result.Add(src.Data[start]);
            }

            return result;
        }


        /// <summary>
        ///     Insert the list identified by <paramref name="src" /> into the
        ///     list designated by <paramref name="dest" /> such that the first
        ///     new element has the index specified by <paramref name="index" />
        /// </summary>
        public LSL_List llListInsertList(LSL_List dest, LSL_List src, int index)
        {
            LSL_List pref;
            LSL_List suff;


            if (index < 0)
            {
                index = index + dest.Length;
                if (index < 0) index = 0;
            }

            if (index != 0)
            {
                pref = dest.GetSublist(0, index - 1);
                if (index < dest.Length)
                {
                    suff = dest.GetSublist(index, -1);
                    return pref + src + suff;
                }

                return pref + src;
            }

            if (index < dest.Length)
            {
                suff = dest.GetSublist(index, -1);
                return src + suff;
            }

            return src;
        }

        /// <summary>
        ///     Returns the index of the first occurrence of test
        ///     in src.
        /// </summary>
        /// <param name="src">Source list</param>
        /// <param name="test">List to search for</param>
        /// <returns>
        ///     The index number of the point in src where test was found if it was found.
        ///     Otherwise returns -1
        /// </returns>
        public LSL_Integer llListFindList(LSL_List src, LSL_List test)
        {
            var index = -1;
            var length = src.Length - test.Length + 1;


            // If either list is empty, do not match
            if (src.Length != 0 && test.Length != 0)
                for (var i = 0; i < length; i++)
                {
                    var needle = llGetListEntryType(test, 0).value;
                    var haystack = llGetListEntryType(src, i).value;

                    // Why this piece of insanity?  This is because most script constants are C# value types (e.g. int)
                    // rather than wrapped LSL types.  Such a script constant does not have int.Equal(LSL_Integer) code
                    // and so the comparison fails even if the LSL_Integer conceptually has the same value.
                    // Therefore, here we test Equals on both the source and destination objects.
                    // However, a future better approach may be use LSL struct script constants (e.g. LSL_Integer(1)).
                    if (needle == haystack && (src.Data[i].Equals(test.Data[0]) || test.Data[0].Equals(src.Data[i])))
                    {
                        int j;
                        for (j = 1; j < test.Length; j++)
                        {
                            needle = llGetListEntryType(test, j).value;
                            haystack = llGetListEntryType(src, i + j).value;

                            if (needle != haystack || !(src.Data[i + j].Equals(test.Data[j]) ||
                                                        test.Data[j].Equals(src.Data[i + j])))
                                break;
                        }

                        if (j == test.Length)
                        {
                            index = i;
                            break;
                        }
                    }
                }

            return index;
        }

        public LSL_List llParseString2List(string str, LSL_List separators, LSL_List in_spacers)
        {
            return ParseString2List(str, separators, in_spacers, false);
        }


        public LSL_String llDumpList2String(LSL_List src, string seperator)
        {
            if (src.Length == 0) return string.Empty;
            var ret = string.Empty;
            foreach (var o in src.Data) ret = ret + o + seperator;
            ret = ret.Substring(0, ret.Length - seperator.Length);
            return ret;
        }

        //  <summary>
        //  Scan the string supplied in 'src' and
        //  tokenize it based upon two sets of
        //  tokenizers provided in two lists,
        //  separators and spacers.
        //  </summary>
        //
        //  <remarks>
        //  Separators demarcate tokens and are
        //  elided as they are encountered. Spacers
        //  also demarcate tokens, but are themselves
        //  retained as tokens.
        //
        //  Both separators and spacers may be arbitrarily
        //  long strings. i.e. ":::".
        //
        //  The function returns an ordered list
        //  representing the tokens found in the supplied
        //  sources string. If two successive tokenizers
        //  are encountered, then a null-string entry is
        //  added to the list.
        //
        //  It is a precondition that the source and
        //  toekizer lisst are non-null. If they are null,
        //  then a null pointer exception will be thrown
        //  while their lengths are being determined.
        //
        //  A small amount of working memoryis required
        //  of approximately 8*#tokenizers + 8*srcstrlen.
        //
        //  There are many ways in which this function
        //  can be implemented, this implementation is
        //  fairly naive and assumes that when the
        //  function is invooked with a short source
        //  string and/or short lists of tokenizers, then
        //  performance will not be an issue.
        //
        //  In order to minimize the perofrmance
        //  effects of long strings, or large numbers
        //  of tokeizers, the function skips as far as
        //  possible whenever a toekenizer is found,
        //  and eliminates redundant tokenizers as soon
        //  as is possible.
        //
        //  The implementation tries to minimize temporary
        //  garbage generation.
        //  </remarks>

        public LSL_List llParseStringKeepNulls(string src, LSL_List separators, LSL_List spacers)
        {
            return ParseString2List(src, separators, spacers, true);
        }

        /// <summary>
        ///     llListReplaceList removes the sub-list defined by the inclusive indices
        ///     start and end and inserts the src list in its place. The inclusive
        ///     nature of the indices means that at least one element must be deleted
        ///     if the indices are within the bounds of the existing list. I.e. 2,2
        ///     will remove the element at index 2 and replace it with the source
        ///     list. Both indices may be negative, with the usual interpretation. An
        ///     interesting case is where end is lower than start. As these indices
        ///     bound the list to be removed, then 0->end, and start->lim are removed
        ///     and the source list is added as a suffix.
        /// </summary>
        public LSL_List llListReplaceList(LSL_List dest, LSL_List src, int start, int end)
        {
            LSL_List pref;


            // Note that although we have normalized, both
            // indices could still be negative.
            if (start < 0) start = start + dest.Length;

            if (end < 0) end = end + dest.Length;
            // The comventional case, remove a sequence starting with
            // start and ending with end. And then insert the source
            // list.
            if (start <= end)
            {
                // If greater than zero, then there is going to be a
                // surviving prefix. Otherwise the inclusive nature
                // of the indices mean that we're going to add the
                // source list as a prefix.
                if (start > 0)
                {
                    pref = dest.GetSublist(0, start - 1);
                    // Only add a suffix if there is something
                    // beyond the end index (it's inclusive too).
                    if (end + 1 < dest.Length)
                        return pref + src + dest.GetSublist(end + 1, -1);
                    return pref + src;
                }
                // If start is less than or equal to zero, then
                // the new list is simply a prefix. We still need to
                // figure out any necessary surgery to the destination
                // based upon end. Note that if end exceeds the upper
                // bound in this case, the entire destination list
                // is removed.

                if (start == 0)
                {
                    if (end + 1 < dest.Length)
                        return src + dest.GetSublist(end + 1, -1);
                    return src;
                }

                // Start < 0
                if (end + 1 < dest.Length)
                    return dest.GetSublist(end + 1, -1);
                return new LSL_List();
            }
            // Finally, if start > end, we strip away a prefix and
            // a suffix, to leave the list that sits <between> ens
            // and start, and then tag on the src list. AT least
            // that's my interpretation. We can get sublist to do
            // this for us. Note that one, or both of the indices
            // might have been negative.

            return dest.GetSublist(end + 1, start - 1) + src;
        }

        public LSL_String llReplaceSubString(LSL_String src, LSL_String pattern, LSL_String replacement, int count)
        {
            RegexOptions RegexOptions;
            if (count < 0)
            {
                RegexOptions = RegexOptions.CultureInvariant | RegexOptions.RightToLeft;
                count = -count;
            }
            else
            {
                RegexOptions = RegexOptions.CultureInvariant;
                if (count == 0)
                    count = -1;
            }


            try
            {
                if (string.IsNullOrEmpty(src.m_string))
                    return src;

                if (string.IsNullOrEmpty(pattern.m_string))
                    return src;

                var rx = new Regex(pattern, RegexOptions, new TimeSpan(500000)); // 50ms)
                if (replacement == null)
                    return rx.Replace(src.m_string, string.Empty, count);

                return rx.Replace(src.m_string, replacement.m_string, count);
            }
            catch
            {
                return src;
            }
        }
    }
}