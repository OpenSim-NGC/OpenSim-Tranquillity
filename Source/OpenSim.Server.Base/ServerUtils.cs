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
using System.Xml;
using System.Xml.Serialization;
using System.Text;
using OpenSim.Framework;
using OpenMetaverse;

namespace OpenSim.Server.Base
{
    public static class ServerUtils
    {
        public static  byte[] SerializeResult(XmlSerializer xs, object data)
        {
            using (MemoryStream ms = new MemoryStream())
            using (XmlTextWriter xw = new XmlTextWriter(ms, Util.UTF8))
            {
                xw.Formatting = Formatting.Indented;
                xs.Serialize(xw, data);
                xw.Flush();

                ms.Seek(0, SeekOrigin.Begin);
                byte[] ret = ms.ToArray();

                return ret;
            }
        }

        public static void ParseService(string serviceName, out string dllName, out string className)
        {
            className = String.Empty;

            // The path for a dynamic plugin will contain ":" on Windows
            string[] parts = serviceName.Split(new char[] { ':' });

            if (parts.Length < 3)
            {
                // Linux. There will be ':' but the one we're looking for
                dllName = parts[0];
                if (parts.Length > 1)
                    className = parts[1];
            }
            else
            {
                // This is Windows. Deal with the : in the Drive letter since
                // the : is also our seperator in the serviceName
                dllName = String.Format("{0}:{1}", parts[0], parts[1]);
                if (parts.Length > 2)
                    className = parts[2];
            }
        }

        public static string ParseServiceName(string service)
        {
            string dllName = string.Empty;
            string serviceName = string.Empty;
            ParseService(service, out dllName, out serviceName);
            return (serviceName);
        }

        public static string ParseDllName(string service)
        {
            string dllName = string.Empty;
            string serviceName = string.Empty;
            ParseService(service, out dllName, out serviceName);
            return (dllName);
        }
        
        public static Dictionary<string, object> ParseQueryString(string query)
        {
            string[] terms = query.Split(new char[] { '&' });

            int nterms = terms.Length;
            if (nterms == 0)
                return new Dictionary<string, object>();

            Dictionary<string, object> result = new Dictionary<string, object>(nterms);
            string name;

            for (int i = 0; i < nterms; ++i)
            {
                string[] elems = terms[i].Split(new char[] { '=' });

                if (elems.Length == 0)
                    continue;

                if (String.IsNullOrWhiteSpace(elems[0]))
                    continue;

                name = System.Web.HttpUtility.UrlDecode(elems[0]);

                if (name.EndsWith("[]"))
                {
                    name = name.Substring(0, name.Length - 2);
                    if (String.IsNullOrWhiteSpace(name))
                        continue;
                    if (result.ContainsKey(name))
                    {
                        if (result[name] is not List<string> l)
                            continue;

                        if (elems.Length > 1 && !string.IsNullOrWhiteSpace(elems[1]))
                            l.Add(System.Web.HttpUtility.UrlDecode(elems[1]));
                        else
                            l.Add(string.Empty);
                    }
                    else
                    {
                        List<string> newList = new List<string>();
                        if (elems.Length > 1 && !String.IsNullOrWhiteSpace(elems[1]))
                            newList.Add(System.Web.HttpUtility.UrlDecode(elems[1]));
                        else
                            newList.Add(String.Empty);
                        result[name] = newList;
                    }
                }
                else
                {
                    if (!result.ContainsKey(name))
                    {
                        if (elems.Length > 1 && !String.IsNullOrWhiteSpace(elems[1]))
                            result[name] = System.Web.HttpUtility.UrlDecode(elems[1]);
                        else
                            result[name] = String.Empty;
                    }
                }
            }

            return result;
        }

        public static string BuildQueryString(Dictionary<string, object> data)
        {
            // this is not conform to html url encoding
            // can only be used on Body of POST or PUT
            StringBuilder sb = new StringBuilder(4096);

            string pvalue;

            foreach (KeyValuePair<string, object> kvp in data)
            {
                if (kvp.Value is List<string> l)
                {
                    string nkey = System.Web.HttpUtility.UrlEncode(kvp.Key);
                    for (int i = 0; i < l.Count; ++i)
                    {
                        if (sb.Length != 0)
                            sb.Append('&');
                        sb.Append(nkey);
                        sb.Append("[]=");
                        sb.Append(System.Web.HttpUtility.UrlEncode(l[i]));
                    }
                }
                else if (kvp.Value is Dictionary<string, object>)
                {
                    // encode complex structures as JSON
                    string js;
                    try
                    {
                        LitJson.JsonMapper.RegisterExporter<UUID>((uuid, writer) => writer.Write(uuid.ToString()));
                        js = LitJson.JsonMapper.ToJson(kvp.Value);
                    }
                    //catch(Exception e)
                    catch
                    {
                        continue;
                    }
                    if (sb.Length != 0)
                        sb.Append('&');
                    sb.Append(System.Web.HttpUtility.UrlEncode(kvp.Key));
                    sb.Append('=');
                    sb.Append(System.Web.HttpUtility.UrlEncode(js));
                }
                else
                {
                    if (sb.Length != 0)
                        sb.Append('&');
                    sb.Append(System.Web.HttpUtility.UrlEncode(kvp.Key));
 
                    pvalue = kvp.Value.ToString();
                    if (!string.IsNullOrEmpty(pvalue))
                    {
                        sb.Append('=');
                        sb.Append(System.Web.HttpUtility.UrlEncode(pvalue));
                    }
                }
            }

            return sb.ToString();
        }


        public static string BuildXmlResponse(Dictionary<string, object> data)
        {
            XmlDocument doc = new XmlDocument();

            XmlNode xmlnode = doc.CreateNode(XmlNodeType.XmlDeclaration, "", "");

            doc.AppendChild(xmlnode);

            XmlElement rootElement = doc.CreateElement("", "ServerResponse", "");

            doc.AppendChild(rootElement);

            BuildXmlData(rootElement, data);

            return doc.InnerXml;
        }

        private static void BuildXmlData(XmlElement parent, Dictionary<string, object> data)
        {
            foreach (KeyValuePair<string, object> kvp in data)
            {
                if (kvp.Value is null)
                    continue;

                XmlElement elem = parent.OwnerDocument.CreateElement("", XmlConvert.EncodeLocalName(kvp.Key), "");

                if (kvp.Value is Dictionary<string, object> dic)
                {
                    XmlAttribute type = parent.OwnerDocument.CreateAttribute("", "type", "");
                    type.Value = "List";
                    elem.Attributes.Append(type);

                    BuildXmlData(elem, dic);
                }
                else
                {
                    elem.AppendChild(parent.OwnerDocument.CreateTextNode(kvp.Value.ToString()));
                }

                parent.AppendChild(elem);
            }
        }

        private static Dictionary<string, object> ScanXmlResponse(XmlReader xr)
        {
            Dictionary<string, object> ret = new Dictionary<string, object>();
            xr.Read();
            while (!xr.EOF && xr.NodeType != XmlNodeType.EndElement)
            {
                if (xr.IsStartElement())
                {
                    string type = xr.GetAttribute("type");
                    if (type != "List")
                    {
                        if (xr.IsEmptyElement)
                        {
                            ret[XmlConvert.DecodeName(xr.Name)] = "";
                            xr.Read();
                        }
                        else
                            ret[XmlConvert.DecodeName(xr.Name)] = xr.ReadElementContentAsString();
                    }
                    else
                    {
                        string name = XmlConvert.DecodeName(xr.Name);
                        if (xr.IsEmptyElement)
                            ret[name] = new Dictionary<string, object>();
                        else
                            ret[name] = ScanXmlResponse(xr);
                        xr.Read();
                    }
                }
                else
                    xr.Read();
            }
            return ret;
        }

        private static readonly XmlReaderSettings ParseXmlStringResponseXmlReaderSettings = new()
        {
            IgnoreWhitespace = true,
            IgnoreComments = true,
            ConformanceLevel = ConformanceLevel.Fragment,
            CloseInput = true,
            MaxCharactersInDocument = 50_000_000
        };

        private static readonly XmlParserContext ParseXmlResponseXmlParserContext = new(null, null, null, XmlSpace.None)
        {
            Encoding = Util.UTF8NoBomEncoding
        };

        public static Dictionary<string, object> ParseXmlResponse(string data)
        {
            try
            {
                using XmlReader xr = XmlReader.Create(new StringReader(data), 
                    ParseXmlStringResponseXmlReaderSettings, ParseXmlResponseXmlParserContext);
                if (!xr.ReadToFollowing("ServerResponse"))
                            return new Dictionary<string, object>();
                    return ScanXmlResponse(xr);
            }
            catch (Exception e)
            {
                //m_log.DebugFormat("[serverUtils.ParseXmlResponse]: failed error: {0}\n --string:\n{1}\n", e.Message, data);
            }
            return new Dictionary<string, object>();
        }

        private static readonly XmlReaderSettings ParseXmlStreamResponseXmlReaderSettings = new()
        {
            IgnoreWhitespace = true,
            IgnoreComments = true,
            ConformanceLevel = ConformanceLevel.Fragment,
            CloseInput = true,
            MaxCharactersInDocument = 50_000_000
        };

        public static Dictionary<string, object> ParseXmlResponse(Stream src)
        {
            using XmlReader xr = XmlReader.Create(src, 
                ParseXmlStreamResponseXmlReaderSettings, ParseXmlResponseXmlParserContext);
            if (xr.ReadToFollowing("ServerResponse"))
                    return ScanXmlResponse(xr);
            return new Dictionary<string, object>();
        }
    }
}
