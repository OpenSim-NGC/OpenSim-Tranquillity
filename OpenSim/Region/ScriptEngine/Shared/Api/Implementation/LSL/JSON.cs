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
using System.Text;
using LitJson;
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
        public LSL_List llJson2List(LSL_Key json)
        {
            if (string.IsNullOrEmpty(json))
                return new LSL_List();
            if (json == "[]")
                return new LSL_List();
            if (json == "{}")
                return new LSL_List();
            var first = ((string)json)[0];

            if (first != '[' && first != '{')
            {
                // we already have a single element
                var l = new LSL_List();
                l.Add(json);
                return l;
            }

            JsonData jsdata;
            try
            {
                jsdata = JsonMapper.ToObject(json);
            }
            catch (Exception e)
            {
                var m = e.Message; // debug point
                return json;
            }

            try
            {
                return JsonParseTop(jsdata);
            }
            catch (Exception e)
            {
                var m = e.Message; // debug point
                return (LSL_Key)ScriptBaseClass.JSON_INVALID;
            }
        }

        public LSL_Key llList2Json(LSL_Key type, LSL_List values)
        {
            try
            {
                var sb = new StringBuilder();
                if (type == ScriptBaseClass.JSON_ARRAY)
                {
                    sb.Append("[");
                    var i = 0;
                    foreach (var o in values.Data)
                    {
                        sb.Append(ListToJson(o));
                        if (i++ < values.Data.Length - 1)
                            sb.Append(",");
                    }

                    sb.Append("]");
                    return (LSL_Key)sb.ToString();
                    ;
                }

                if (type == ScriptBaseClass.JSON_OBJECT)
                {
                    sb.Append("{");
                    for (var i = 0; i < values.Data.Length; i += 2)
                    {
                        if (!(values.Data[i] is LSL_Key))
                            return ScriptBaseClass.JSON_INVALID;
                        var key = ((LSL_Key)values.Data[i]).m_string;
                        key = EscapeForJSON(key, true);
                        sb.Append(key);
                        sb.Append(":");
                        sb.Append(ListToJson(values.Data[i + 1]));
                        if (i < values.Data.Length - 2)
                            sb.Append(",");
                    }

                    sb.Append("}");
                    return (LSL_Key)sb.ToString();
                }

                return ScriptBaseClass.JSON_INVALID;
            }
            catch
            {
                return ScriptBaseClass.JSON_INVALID;
            }
        }

        public LSL_Key llJsonSetValue(LSL_Key json, LSL_List specifiers, LSL_Key value)
        {
            var noSpecifiers = specifiers.Length == 0;
            JsonData workData;
            try
            {
                if (noSpecifiers)
                    specifiers.Add(new LSL_Integer(0));

                if (!string.IsNullOrEmpty(json))
                {
                    workData = JsonMapper.ToObject(json);
                }
                else
                {
                    workData = new JsonData();
                    workData.SetJsonType(JsonType.Array);
                }
            }
            catch (Exception e)
            {
                var m = e.Message; // debug point
                return ScriptBaseClass.JSON_INVALID;
            }

            try
            {
                var replace = JsonSetSpecific(workData, specifiers, 0, value);
                if (replace != null)
                    workData = replace;
            }
            catch (Exception e)
            {
                var m = e.Message; // debug point
                return ScriptBaseClass.JSON_INVALID;
            }

            try
            {
                var r = JsonMapper.ToJson(workData);
                if (noSpecifiers)
                    r = r.Substring(1, r.Length - 2); // strip leading and trailing brakets
                return r;
            }
            catch (Exception e)
            {
                var m = e.Message; // debug point
            }

            return ScriptBaseClass.JSON_INVALID;
        }

        public LSL_Key llJsonGetValue(LSL_Key json, LSL_List specifiers)
        {
            if (string.IsNullOrWhiteSpace(json))
                return ScriptBaseClass.JSON_INVALID;

            if (specifiers.Length > 0 && (json == "{}" || json == "[]"))
                return ScriptBaseClass.JSON_INVALID;

            var first = ((string)json)[0];
            if (first != '[' && first != '{')
            {
                if (specifiers.Length > 0)
                    return ScriptBaseClass.JSON_INVALID;
                json = "[" + json + "]"; // could handle single element case.. but easier like this
                specifiers.Add((LSL_Integer)0);
            }

            JsonData jsonData;
            try
            {
                jsonData = JsonMapper.ToObject(json);
            }
            catch (Exception e)
            {
                var m = e.Message; // debug point
                return ScriptBaseClass.JSON_INVALID;
            }

            JsonData elem = null;
            if (specifiers.Length == 0)
            {
                elem = jsonData;
            }
            else
            {
                if (!JsonFind(jsonData, specifiers, 0, out elem))
                    return ScriptBaseClass.JSON_INVALID;
            }

            return JsonElementToString(elem);
        }

        public LSL_Key llJsonValueType(LSL_Key json, LSL_List specifiers)
        {
            if (string.IsNullOrWhiteSpace(json))
                return ScriptBaseClass.JSON_INVALID;

            if (specifiers.Length > 0 && (json == "{}" || json == "[]"))
                return ScriptBaseClass.JSON_INVALID;

            var first = ((string)json)[0];
            if (first != '[' && first != '{')
            {
                if (specifiers.Length > 0)
                    return ScriptBaseClass.JSON_INVALID;
                json = "[" + json + "]"; // could handle single element case.. but easier like this
                specifiers.Add((LSL_Integer)0);
            }

            JsonData jsonData;
            try
            {
                jsonData = JsonMapper.ToObject(json);
            }
            catch (Exception e)
            {
                var m = e.Message; // debug point
                return ScriptBaseClass.JSON_INVALID;
            }

            JsonData elem = null;
            if (specifiers.Length == 0)
            {
                elem = jsonData;
            }
            else
            {
                if (!JsonFind(jsonData, specifiers, 0, out elem))
                    return ScriptBaseClass.JSON_INVALID;
            }

            if (elem == null)
                return ScriptBaseClass.JSON_NULL;

            var elemType = elem.GetJsonType();
            switch (elemType)
            {
                case JsonType.Array:
                    return ScriptBaseClass.JSON_ARRAY;
                case JsonType.Boolean:
                    return (bool)elem ? ScriptBaseClass.JSON_TRUE : ScriptBaseClass.JSON_FALSE;
                case JsonType.Double:
                case JsonType.Int:
                case JsonType.Long:
                    return ScriptBaseClass.JSON_NUMBER;
                case JsonType.Object:
                    return ScriptBaseClass.JSON_OBJECT;
                case JsonType.String:
                    var s = (string)elem;
                    if (s == ScriptBaseClass.JSON_NULL)
                        return ScriptBaseClass.JSON_NULL;
                    if (s == ScriptBaseClass.JSON_TRUE)
                        return ScriptBaseClass.JSON_TRUE;
                    if (s == ScriptBaseClass.JSON_FALSE)
                        return ScriptBaseClass.JSON_FALSE;
                    return ScriptBaseClass.JSON_STRING;
                case JsonType.None:
                    return ScriptBaseClass.JSON_NULL;
                default:
                    return ScriptBaseClass.JSON_INVALID;
            }
        }
    }
}