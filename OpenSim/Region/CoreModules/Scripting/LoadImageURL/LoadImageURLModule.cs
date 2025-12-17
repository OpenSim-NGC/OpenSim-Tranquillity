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
using SkiaSharp;
using System.IO;
using System.Net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.Imaging;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using log4net;
using System.Reflection;
using Mono.Addins;

namespace OpenSim.Region.CoreModules.Scripting.LoadImageURL
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "LoadImageURLModule")]
    public class LoadImageURLModule : ISharedRegionModule, IDynamicTextureRender
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private string m_name = "LoadImageURL";
        private Scene m_scene;
        private IDynamicTextureManager m_textureManager;

        private OutboundUrlFilter m_outboundUrlFilter;
        WebProxy m_proxy = null;

        #region IDynamicTextureRender Members

        public string GetName()
        {
            return m_name;
        }

        public string GetContentType()
        {
            return ("image");
        }

        public bool SupportsAsynchronous()
        {
            return true;
        }

//        public bool AlwaysIdenticalConversion(string bodyData, string extraParams)
//        {
//            // We don't support conversion of body data.
//            return false;
//        }

        public IDynamicTexture ConvertUrl(string url, string extraParams)
        {
            return null;
        }

        public IDynamicTexture ConvertData(string bodyData, string extraParams)
        {
            return null;
        }

        public bool AsyncConvertUrl(UUID id, string url, string extraParams)
        {
            return MakeHttpRequest(url, id);
        }

        public bool AsyncConvertData(UUID id, string bodyData, string extraParams)
        {
            return false;
        }

        public void GetDrawStringSize(string text, string fontName, int fontSize,
                                      out double xSize, out double ySize)
        {
            xSize = 0;
            ySize = 0;
        }

        #endregion

        #region ISharedRegionModule Members

        public void Initialise(IConfigSource config)
        {
            m_outboundUrlFilter = new OutboundUrlFilter("Script dynamic texture image module", config);
            string proxyurl = config.Configs["Startup"].GetString("HttpProxy");
            if(!string.IsNullOrEmpty(proxyurl))
            {
                string proxyexcepts = config.Configs["Startup"].GetString("HttpProxyExceptions");
                if (!string.IsNullOrEmpty(proxyexcepts))
                {
                    string[] elist = proxyexcepts.Split(';');
                    m_proxy = new WebProxy(proxyurl, true, elist);
                }
                else
                {
                    m_proxy = new WebProxy(proxyurl, true);
                }
            }
        }

        public void PostInitialise()
        {
        }

        public void AddRegion(Scene scene)
        {
            m_scene ??= scene;
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void RegionLoaded(Scene scene)
        {
            if (m_textureManager is null && m_scene == scene)
            {
                m_textureManager = m_scene.RequestModuleInterface<IDynamicTextureManager>();
                m_textureManager?.RegisterRender(GetContentType(), this);
            }
        }

        public void Close()
        {
        }

        public string Name
        {
            get { return m_name; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        #endregion

        private bool MakeHttpRequest(string url, UUID requestID)
        {
            if (m_textureManager is null)
            {
                m_log.WarnFormat("[LOADIMAGEURLMODULE]: No texture manager. Can't function.");
                return false;
            }

            if (!m_outboundUrlFilter.CheckAllowed(new Uri(url)))
                return false;

            var client = new System.Net.Http.HttpClient();
            var requestState = new RequestState(url, requestID);
            client.GetAsync(url).ContinueWith((task) => HttpRequestReturn(task, requestState));
            return true;
        }

        private void HttpRequestReturn(System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> task, RequestState state)
        {
            if (m_textureManager == null)
                return;

            byte[] imageJ2000 = Array.Empty<byte>();
            (int width, int height) newSize = (0, 0);

            try
            {
                if (task.IsCompletedSuccessfully)
                {
                    var response = task.Result;
                    if (response.IsSuccessStatusCode)
                    {
                        var content = response.Content.ReadAsStreamAsync().Result;
                        if (content != null)
                        {
                            try
                            {
                                var image = SKImage.FromEncodedData(content);
                                if (image != null)
                                {
                                    // TODO: make this a bit less hard coded
                                    if ((image.Height < 64) && (image.Width < 64))
                                    {
                                        newSize.width = 32;
                                        newSize.height = 32;
                                    }
                                    else if ((image.Height < 128) && (image.Width < 128))
                                    {
                                        newSize.width = 64;
                                        newSize.height = 64;
                                    }
                                    else if ((image.Height < 256) && (image.Width < 256))
                                    {
                                        newSize.width = 128;
                                        newSize.height = 128;
                                    }
                                    else if ((image.Height < 512 && image.Width < 512))
                                    {
                                        newSize.width = 256;
                                        newSize.height = 256;
                                    }
                                    else if ((image.Height < 1024 && image.Width < 1024))
                                    {
                                        newSize.width = 512;
                                        newSize.height = 512;
                                    }
                                    else
                                    {
                                        newSize.width = 1024;
                                        newSize.height = 1024;
                                    }

                                    if (newSize.width != image.Width || newSize.height != image.Height)
                                    {
                                        // Resize the image using SkiaSharp
                                        var info = new SKImageInfo(newSize.width, newSize.height);
                                        using (var surface = SKSurface.Create(info))
                                        {
                                            var canvas = surface.Canvas;
                                            var srcRect = SKRect.Create(0, 0, image.Width, image.Height);
                                            var dstRect = SKRect.Create(0, 0, newSize.width, newSize.height);
                                            canvas.DrawImage(image, srcRect, dstRect);
                                            canvas.Flush();

                                            using (var resized = surface.Snapshot())
                                            {
                                                var encoded = resized.Encode(SKEncodedImageFormat.Jpeg, 100);
                                                imageJ2000 = encoded.ToArray();
                                            }
                                        }
                                    }
                                    else
                                    {
                                        var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 100);
                                        imageJ2000 = encoded.ToArray();
                                    }

                                    image.Dispose();
                                }
                            }
                            catch (Exception)
                            {
                                m_log.Error("[LOADIMAGEURLMODULE]: Image Conversion Failed.  Empty byte data returned!");
                            }
                        }
                        else
                        {
                            m_log.WarnFormat("[LOADIMAGEURLMODULE] No data returned");
                        }
                    }
                    response.Dispose();
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[LOADIMAGEURLMODULE]: unexpected exception {0}", e.Message);
            }

            m_log.DebugFormat("[LOADIMAGEURLMODULE]: Returning {0} bytes of image data for request {1}",
                              imageJ2000.Length, state.RequestID);

            m_textureManager.ReturnData(
                state.RequestID,
                new OpenSim.Region.CoreModules.Scripting.DynamicTexture.DynamicTexture(
                state.Url, null, imageJ2000, newSize, false));
        }

        #region Nested type: RequestState

        public class RequestState
        {
            public string Url = null;
            public UUID RequestID = UUID.Zero;
            public int TimeOfRequest = 0;

            public RequestState(string url, UUID requestID)
            {
                Url = url;
                RequestID = requestID;
                TimeOfRequest = Util.UnixTimeSinceEpoch();
            }
        }

        #endregion
    }
}
