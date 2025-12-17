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

using SkiaSharp;
using System.Globalization;
using System.Net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.Imaging;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using log4net;
using System.Reflection;
using Mono.Addins;

//using Cairo;

namespace OpenSim.Region.CoreModules.Scripting.VectorRender
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "VectorRenderModule")]
    public class VectorRenderModule : ISharedRegionModule, IDynamicTextureRender
    {
        // These fields exist for testing purposes, please do not remove.
//        private static bool s_flipper;
//        private static byte[] s_asset1Data;
//        private static byte[] s_asset2Data;

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static object thisLock = new object();
        private static SKTypeface m_typeface = null; // SkiaSharp typeface for measurements

        private Scene m_scene;
        private IDynamicTextureManager m_textureManager;

        private string m_fontName = "Arial";

        public VectorRenderModule()
        {
        }

        #region IDynamicTextureRender Members

        public string GetContentType()
        {
            return "vector";
        }

        public string GetName()
        {
            return Name;
        }

        public bool SupportsAsynchronous()
        {
            return true;
        }

//        public bool AlwaysIdenticalConversion(string bodyData, string extraParams)
//        {
//            string[] lines = GetLines(bodyData);
//            return lines.Any((str, r) => str.StartsWith("Image"));
//        }

        public IDynamicTexture ConvertUrl(string url, string extraParams)
        {
            return null;
        }

        public IDynamicTexture ConvertData(string bodyData, string extraParams)
        {
            return Draw(bodyData, extraParams);
        }

        public bool AsyncConvertUrl(UUID id, string url, string extraParams)
        {
            return false;
        }

        public bool AsyncConvertData(UUID id, string bodyData, string extraParams)
        {
            if (m_textureManager == null)
            {
                m_log.Warn("[VECTORRENDERMODULE]: No texture manager. Can't function");
                return false;
            }
            // XXX: This isn't actually being done asynchronously!
            m_textureManager.ReturnData(id, ConvertData(bodyData, extraParams));

            return true;
        }

        public void GetDrawStringSize(string text, string fontName, int fontSize,
                                      out double xSize, out double ySize)
        {
            lock (thisLock)
            {
                var typeface = SKTypeface.FromFamilyName(fontName, SKFontStyle.Normal);
                using (var font = new SKFont(typeface, fontSize))
                {
                    xSize = (int)font.MeasureText(text);
                    // For height, use the font metrics
                    ySize = (int)(font.Metrics.Descent - font.Metrics.Ascent);
                }
            }
        }

        /// <summary>
        /// Parse color from hex or name
        /// </summary>
        private static SKColor ParseColor(string value, SKColor defaultColor)
        {
            int hex = 0;
            if (int.TryParse(value, System.Globalization.NumberStyles.HexNumber, 
                System.Globalization.CultureInfo.InvariantCulture, out hex))
            {
                return new SKColor((uint)hex);
            }
            
            // Try to parse named colors
            switch (value.ToLower())
            {
                case "white": return SKColors.White;
                case "black": return SKColors.Black;
                case "red": return SKColors.Red;
                case "green": return SKColors.Green;
                case "blue": return SKColors.Blue;
                case "yellow": return SKColors.Yellow;
                case "cyan": return SKColors.Cyan;
                case "magenta": return SKColors.Magenta;
                case "gray": return SKColors.Gray;
                default: return defaultColor;
            }
        }

        #endregion

        #region ISharedRegionModule Members

        public void Initialise(IConfigSource config)
        {
            IConfig cfg = config.Configs["VectorRender"];
            if (null != cfg)
            {
                m_fontName = cfg.GetString("font_name", m_fontName);
            }
            m_log.DebugFormat("[VECTORRENDERMODULE]: using font \"{0}\" for text rendering.", m_fontName);

            // We won't dispose of these explicitly since this module is only removed when the entire simulator
            // is shut down.
            lock(thisLock)
            {
                if(m_typeface == null)
                {
                    m_typeface = SKTypeface.FromFamilyName(m_fontName, SKFontStyle.Normal);
                }
            }
        }

        public void PostInitialise()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (m_scene == null)
            {
                m_scene = scene;
            }
        }

        public void RegionLoaded(Scene scene)
        {
            if (m_textureManager == null && m_scene == scene)
            {
                m_textureManager = m_scene.RequestModuleInterface<IDynamicTextureManager>();
                if (m_textureManager != null)
                {
                    m_textureManager.RegisterRender(GetContentType(), this);
                }
            }
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void Close()
        {
        }

        public string Name
        {
            get { return "VectorRenderModule"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        #endregion

        private IDynamicTexture Draw(string data, string extraParams)
        {
            // We need to cater for old scripts that didnt use extraParams neatly, they use either an integer size which represents both width and height, or setalpha
            // we will now support multiple comma seperated params in the form  width:256,height:512,alpha:255
            int width = 256;
            int height = 256;
            int alpha = 255; // 0 is transparent
            SKColor bgColor = SKColors.White;  // Default background color
            char altDataDelim = ';';

            char[] paramDelimiter = { ',' };
            char[] nvpDelimiter = { ':' };

            extraParams = extraParams.Trim();
            extraParams = extraParams.ToLower();

            string[] nvps = extraParams.Split(paramDelimiter);

            int temp = -1;
            foreach (string pair in nvps)
            {
                string[] nvp = pair.Split(nvpDelimiter);
                string name = "";
                string value = "";

                if (nvp[0] != null)
                {
                    name = nvp[0].Trim();
                }

                if (nvp.Length == 2)
                {
                    value = nvp[1].Trim();
                }

                switch (name)
                {
                    case "width":
                        temp = parseIntParam(value);
                        if (temp != -1)
                        {
                            if (temp < 1)
                            {
                                width = 1;
                            }
                            else if (temp > 2048)
                            {
                                width = 2048;
                            }
                            else
                            {
                                width = temp;
                            }
                        }
                        break;
                    case "height":
                        temp = parseIntParam(value);
                        if (temp != -1)
                        {
                            if (temp < 1)
                            {
                                height = 1;
                            }
                            else if (temp > 2048)
                            {
                                height = 2048;
                            }
                            else
                            {
                                height = temp;
                            }
                        }
                        break;
                     case "alpha":
                          temp = parseIntParam(value);
                          if (temp != -1)
                          {
                              if (temp < 0)
                              {
                                  alpha = 0;
                              }
                              else if (temp > 255)
                              {
                                  alpha = 255;
                              }
                              else
                              {
                                  alpha = temp;
                              }
                          }
                          // Allow a bitmap w/o the alpha component to be created
                          else if (value.ToLower() == "false") {
                               alpha = 256;
                          }
                          break;
                     case "bgcolor":
                     case "bgcolour":
                          int hex = 0;
                         if (Int32.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hex))
                         {
                             bgColor = new SKColor((uint)hex);
                         }
                         else
                         {
                             bgColor = ParseColor(value, SKColors.White);
                         }
                         break;
                    case "altdatadelim":
                        altDataDelim = value.ToCharArray()[0];
                        break;
                    case "":
                         // blank string has been passed do nothing just use defaults
                     break;
                     default: // this is all for backwards compat, all a bit ugly hopfully can be removed in future
                         // could be either set alpha or just an int
                         if (name == "setalpha")
                         {
                             alpha = 0; // set the texture to have transparent background (maintains backwards compat)
                         }
                         else
                         {
                             // this function used to accept an int on its own that represented both
                             // width and height, this is to maintain backwards compat, could be removed
                             // but would break existing scripts
                             temp = parseIntParam(name);
                             if (temp != -1)
                             {
                                 if (temp > 1024)
                                    temp = 1024;

                                 if (temp < 128)
                                     temp = 128;

                                 width = temp;
                                 height = temp;
                             }
                         }
                     break;
                }
            }

            SKBitmap bitmap = null;
            SKCanvas canvas = null;
            bool reuseable = false;

            try
            {
                lock (this)
                {
                    if (alpha == 256 && bgColor.Alpha != 255)
                        alpha = bgColor.Alpha;

                    bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                    canvas = new SKCanvas(bitmap);
                    
                    if (alpha == 256)
                    {
                        canvas.Clear(bgColor);
                    }
                    else
                    {
                        SKColor newbg = new SKColor(bgColor.Red, bgColor.Green, bgColor.Blue, (byte)alpha);
                        canvas.Clear(newbg);
                    }
                    GDIDraw(data, canvas, altDataDelim, out reuseable);
                }

                byte[] imageJ2000 = Array.Empty<byte>();

                // This code exists for testing purposes, please do not remove.
//                if (s_flipper)
//                    imageJ2000 = s_asset1Data;
//                else
//                    imageJ2000 = s_asset2Data;
//
//                s_flipper = !s_flipper;

                try
                {
                    // Convert SKBitmap to SKImage and encode as JPEG
                    // TODO: Replace with CoreJ2K encoding when encoder becomes available
                    using (var image = SKImage.FromBitmap(bitmap))
                    {
                        var data_encoded = image.Encode(SKEncodedImageFormat.Jpeg, 100);
                        imageJ2000 = data_encoded.ToArray();
                    }
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat(
                        "[VECTORRENDERMODULE]: Image Encoding Failed.  Exception {0}{1}",
                        e.Message, e.StackTrace);
                }

                return new OpenSim.Region.CoreModules.Scripting.DynamicTexture.DynamicTexture(
                    data, extraParams, imageJ2000, (width, height), reuseable);
            }
            finally
            {
                lock (thisLock)
                {
                    if (canvas != null)
                        canvas.Dispose();

                    if (bitmap != null)
                        bitmap.Dispose();
                }
            }
        }

        private int parseIntParam(string strInt)
        {
            int parsed;
            try
            {
                parsed = Convert.ToInt32(strInt);
            }
            catch (Exception)
            {
                //Ckrinke: Add a WriteLine to remove the warning about 'e' defined but not used
                // m_log.Debug("Problem with Draw. Please verify parameters." + e.ToString());
                parsed = -1;
            }

            return parsed;
        }

/*
        private void CairoDraw(string data, System.Drawing.Graphics graph)
        {
            using (Win32Surface draw = new Win32Surface(graph.GetHdc()))
            {
                Context contex = new Context(draw);

                contex.Antialias = Antialias.None;    //fastest method but low quality
                contex.LineWidth = 7;
                char[] lineDelimiter = { ';' };
                char[] partsDelimiter = { ',' };
                string[] lines = data.Split(lineDelimiter);

                foreach (string line in lines)
                {
                    string nextLine = line.Trim();

                    if (nextLine.StartsWith("MoveTO"))
                    {
                        float x = 0;
                        float y = 0;
                        GetParams(partsDelimiter, ref nextLine, ref x, ref y);
                        contex.MoveTo(x, y);
                    }
                    else if (nextLine.StartsWith("LineTo"))
                    {
                        float x = 0;
                        float y = 0;
                        GetParams(partsDelimiter, ref nextLine, ref x, ref y);
                        contex.LineTo(x, y);
                        contex.Stroke();
                    }
                }
            }
            graph.ReleaseHdc();
        }
*/

        /// <summary>
        /// Split input data into discrete command lines.
        /// </summary>
        /// <returns></returns>
        /// <param name='data'></param>
        /// <param name='dataDelim'></param>
        private string[] GetLines(string data, char dataDelim)
        {
            char[] lineDelimiter = { dataDelim };
            return data.Split(lineDelimiter);
        }

        private void GDIDraw(string data, SKCanvas canvas, char dataDelim, out bool reuseable)
        {
            reuseable = true;
            SKPoint startPoint = new SKPoint(0, 0);
            SKPoint endPoint = new SKPoint(0, 0);
            SKPaint drawPaint = null;
            SKTypeface myTypeface = null;
            SKFont myFont = null;
            SKColor drawColor = SKColors.Black;

            try
            {
                drawPaint = new SKPaint
                {
                    Color = SKColors.Black,
                    StrokeWidth = 7,
                    IsStroke = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeCap = SKStrokeCap.Butt,
                    StrokeJoin = SKStrokeJoin.Miter,
                    IsAntialias = true
                };

                string fontName = m_fontName;
                float fontSize = 14;
                myTypeface = SKTypeface.FromFamilyName(fontName, SKFontStyle.Normal);
                myFont = new SKFont(myTypeface, fontSize);

                char[] partsDelimiter = {','};

                foreach (string line in GetLines(data, dataDelim))
                {
                    string nextLine = line.TrimStart();

//                    m_log.DebugFormat("[VECTOR RENDER MODULE]: Processing line '{0}'", nextLine);

                    if (nextLine.StartsWith("Text") && nextLine.Length > 5)
                    {
                        int start = 4;
                        if (nextLine[4] == ' ')
                            start++;
                        if (start < nextLine.Length)
                        {
                            nextLine = nextLine.Substring(start);
                            var textPaint = new SKPaint
                            {
                                Color = drawColor,
                                IsAntialias = true
                            };
                            canvas.DrawText(nextLine, startPoint.X, startPoint.Y, SKTextAlign.Left, myFont, textPaint);
                            textPaint.Dispose();
                        }
                        continue;
                    }

                    nextLine = nextLine.TrimEnd();
                    if (nextLine.StartsWith("ResetTransf"))
                    {
                        canvas.ResetMatrix();
                    }
                    else if (nextLine.StartsWith("TransTransf"))
                    {
                        float x = 0;
                        float y = 0;
                        GetParams(partsDelimiter, ref nextLine, 11, ref x, ref y);
                        canvas.Translate(x, y);
                    }
                    else if (nextLine.StartsWith("ScaleTransf"))
                    {
                        float x = 0;
                        float y = 0;
                        GetParams(partsDelimiter, ref nextLine, 11, ref x, ref y);
                        canvas.Scale(x, y);
                    }
                    else if (nextLine.StartsWith("RotTransf"))
                    {
                        float x = 0;
                        GetParams(partsDelimiter, ref nextLine, 9, ref x);
                        canvas.RotateDegrees(x);
                    }
                    //replace with switch, or even better, do some proper parsing
                    else if (nextLine.StartsWith("MoveTo"))
                    {
                        float x = 0;
                        float y = 0;
                        GetParams(partsDelimiter, ref nextLine, 6, ref x, ref y);
                        startPoint = new SKPoint(x, y);
                    }
                    else if (nextLine.StartsWith("LineTo"))
                    {
                        float x = 0;
                        float y = 0;
                        GetParams(partsDelimiter, ref nextLine, 6, ref x, ref y);
                        endPoint = new SKPoint(x, y);
                        canvas.DrawLine(startPoint, endPoint, drawPaint);
                        startPoint = endPoint;
                    }
                    else if (nextLine.StartsWith("Image"))
                    {
                        // We cannot reuse any generated texture involving fetching an image via HTTP since that image
                        // can change.
                        reuseable = false;

                        float x = 0;
                        float y = 0;
                        GetParams(partsDelimiter, ref nextLine, 5, ref x, ref y);
                        endPoint = new SKPoint(x, y);

                        var image = ImageHttpRequest(nextLine);
                        if (image != null)
                        {
                            var srcRect = SKRect.Create(0, 0, image.Width, image.Height);
                            var dstRect = SKRect.Create(startPoint.X, startPoint.Y, x, y);
                            canvas.DrawImage(image, srcRect, dstRect);
                            image.Dispose();
                        }
                        else
                        {
                            var errorFont = new SKFont(SKTypeface.FromFamilyName(m_fontName, SKFontStyle.Normal), 6);
                            var errorPaint = new SKPaint
                            {
                                Color = drawColor,
                                IsAntialias = true
                            };
                            canvas.DrawText("URL couldn't be resolved or is", startPoint.X, startPoint.Y, SKTextAlign.Left, errorFont, errorPaint);
                            canvas.DrawText("not an image. Please check URL.", startPoint.X, startPoint.Y + 12, SKTextAlign.Left, errorFont, errorPaint);
                            errorPaint.Dispose();
                            errorFont.Dispose();

                            // Draw rectangle to show error area
                            var rectPaint = new SKPaint
                            {
                                Color = drawColor,
                                StrokeWidth = drawPaint.StrokeWidth,
                                Style = SKPaintStyle.Stroke,
                                IsAntialias = true
                            };
                            var rect = SKRect.Create(startPoint.X, startPoint.Y, endPoint.X, endPoint.Y);
                            canvas.DrawRect(rect, rectPaint);
                            rectPaint.Dispose();
                        }

                        startPoint = new SKPoint(startPoint.X + endPoint.X, startPoint.Y + endPoint.Y);
                    }
                    else if (nextLine.StartsWith("Rectangle"))
                    {
                        float x = 0;
                        float y = 0;
                        GetParams(partsDelimiter, ref nextLine, 9, ref x, ref y);
                        endPoint = new SKPoint(x, y);
                        var rect = SKRect.Create(startPoint.X, startPoint.Y, endPoint.X, endPoint.Y);
                        canvas.DrawRect(rect, drawPaint);
                        startPoint = new SKPoint(startPoint.X + endPoint.X, startPoint.Y + endPoint.Y);
                    }
                    else if (nextLine.StartsWith("FillRectangle"))
                    {
                        float x = 0;
                        float y = 0;
                        GetParams(partsDelimiter, ref nextLine, 13, ref x, ref y);
                        endPoint = new SKPoint(x, y);
                        var fillPaint = new SKPaint { Color = drawColor, Style = SKPaintStyle.Fill };
                        var rect = SKRect.Create(startPoint.X, startPoint.Y, endPoint.X, endPoint.Y);
                        canvas.DrawRect(rect, fillPaint);
                        fillPaint.Dispose();
                        startPoint = new SKPoint(startPoint.X + endPoint.X, startPoint.Y + endPoint.Y);
                    }
                    else if (nextLine.StartsWith("FillPolygon"))
                    {
                        SKPoint[] points = null;
                        GetParams(partsDelimiter, ref nextLine, 11, ref points);
                        var fillPaint = new SKPaint { Color = drawColor, Style = SKPaintStyle.Fill };
                        using (var path = new SKPath())
                        {
                            if (points != null && points.Length > 0)
                            {
                                path.MoveTo(points[0]);
                                for (int i = 1; i < points.Length; i++)
                                    path.LineTo(points[i]);
                                path.Close();
                                canvas.DrawPath(path, fillPaint);
                            }
                        }
                        fillPaint.Dispose();
                    }
                    else if (nextLine.StartsWith("Polygon"))
                    {
                        SKPoint[] points = null;
                        GetParams(partsDelimiter, ref nextLine, 7, ref points);
                        using (var path = new SKPath())
                        {
                            if (points != null && points.Length > 0)
                            {
                                path.MoveTo(points[0]);
                                for (int i = 1; i < points.Length; i++)
                                    path.LineTo(points[i]);
                                path.Close();
                                canvas.DrawPath(path, drawPaint);
                            }
                        }
                    }
                    else if (nextLine.StartsWith("Ellipse"))
                    {
                        float x = 0;
                        float y = 0;
                        GetParams(partsDelimiter, ref nextLine, 7, ref x, ref y);
                        endPoint = new SKPoint(x, y);
                        var rect = SKRect.Create(startPoint.X, startPoint.Y, endPoint.X, endPoint.Y);
                        canvas.DrawOval(rect, drawPaint);
                        startPoint = new SKPoint(startPoint.X + endPoint.X, startPoint.Y + endPoint.Y);
                    }
                    else if (nextLine.StartsWith("FillEllipse"))
                    {
                        float x = 0;
                        float y = 0;
                        GetParams(partsDelimiter, ref nextLine, 11, ref x, ref y);
                        endPoint = new SKPoint(x, y);
                        var fillPaint = new SKPaint { Color = drawColor, Style = SKPaintStyle.Fill };
                        var rect = SKRect.Create(startPoint.X, startPoint.Y, endPoint.X, endPoint.Y);
                        canvas.DrawOval(rect, fillPaint);
                        fillPaint.Dispose();
                        startPoint = new SKPoint(startPoint.X + endPoint.X, startPoint.Y + endPoint.Y);
                    }
                    else if (nextLine.StartsWith("FontSize"))
                    {
                        nextLine = nextLine.Remove(0, 8);
                        nextLine = nextLine.Trim();
                        fontSize = Convert.ToSingle(nextLine, CultureInfo.InvariantCulture);

                        myFont?.Dispose();
                        myFont = new SKFont(myTypeface, fontSize);
                    }
                    else if (nextLine.StartsWith("FontProp"))
                    {
                        SKFontStyle currentStyle = myTypeface.FontStyle;
                        bool bold = currentStyle == SKFontStyle.Bold || currentStyle == SKFontStyle.BoldItalic;
                        bool italic = currentStyle == SKFontStyle.Italic || currentStyle == SKFontStyle.BoldItalic;

                        nextLine = nextLine.Remove(0, 8);
                        nextLine = nextLine.Trim();

                        string[] fprops = nextLine.Split(partsDelimiter);
                        foreach (string prop in fprops)
                        {
                            switch (prop.Trim())
                            {
                                case "B":
                                    bold = true;
                                    break;
                                case "I":
                                    italic = true;
                                    break;
                                case "U":
                                    // SkiaSharp doesn't directly support underline, would need custom drawing
                                    break;
                                case "S":
                                    // SkiaSharp doesn't directly support strikeout, would need custom drawing
                                    break;
                                case "R":   //This special case resets all font properties
                                    bold = false;
                                    italic = false;
                                    break;
                            }
                        }
                        SKFontStyle newStyle = SKFontStyle.Normal;
                        if (bold && italic)
                            newStyle = SKFontStyle.BoldItalic;
                        else if (bold)
                            newStyle = SKFontStyle.Bold;
                        else if (italic)
                            newStyle = SKFontStyle.Italic;

                        if (newStyle != currentStyle)
                        {
                            myTypeface?.Dispose();
                            myTypeface = SKTypeface.FromFamilyName(myTypeface.FamilyName, newStyle);
                            myFont?.Dispose();
                            myFont = new SKFont(myTypeface, fontSize);
                        }
                    }
                    else if (nextLine.StartsWith("FontName"))
                    {
                        nextLine = nextLine.Remove(0, 8);
                        string newFontName = nextLine.Trim();
                        myTypeface?.Dispose();
                        myTypeface = SKTypeface.FromFamilyName(newFontName, SKFontStyle.Normal);
                        myFont?.Dispose();
                        myFont = new SKFont(myTypeface, fontSize);
                    }
                    else if (nextLine.StartsWith("PenSize"))
                    {
                        nextLine = nextLine.Remove(0, 7);
                        nextLine = nextLine.Trim();
                        float size = Convert.ToSingle(nextLine, CultureInfo.InvariantCulture);
                        drawPaint.StrokeWidth = size;
                    }
                    else if (nextLine.StartsWith("PenCap"))
                    {
                        bool start = true, end = true;
                        nextLine = nextLine.Remove(0, 6);
                        nextLine = nextLine.Trim();
                        string[] cap = nextLine.Split(partsDelimiter);
                        if (cap[0].ToLower() == "start")
                            end = false;
                        else if (cap[0].ToLower() == "end")
                            start = false;
                        else if (cap[0].ToLower() != "both")
                            return;
                        string type = cap[1].ToLower().Trim();

                        SKStrokeCap skCap = SKStrokeCap.Butt;
                        switch (type)
                        {
                            case "arrow":
                                // SkiaSharp doesn't have arrow caps, use round as fallback
                                skCap = SKStrokeCap.Round;
                                break;
                            case "round":
                                skCap = SKStrokeCap.Round;
                                break;
                            case "diamond":
                                // SkiaSharp doesn't have diamond caps, use round as fallback
                                skCap = SKStrokeCap.Round;
                                break;
                            case "flat":
                                skCap = SKStrokeCap.Butt;
                                break;
                        }
                        
                        if (end || start)
                        {
                            drawPaint.StrokeCap = skCap;
                        }
                    }
                    else if (nextLine.StartsWith("PenColour") || nextLine.StartsWith("PenColor"))
                    {
                        nextLine = nextLine.Remove(0, 9);
                        nextLine = nextLine.Trim();
                        int hex = 0;

                        SKColor newColor;
                        if (Int32.TryParse(nextLine, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hex))
                        {
                            newColor = new SKColor((uint)hex);
                        }
                        else
                        {
                            newColor = ParseColor(nextLine, SKColors.Black);
                        }

                        drawColor = newColor;
                        drawPaint.Color = newColor;
                    }
                }
            }
            finally
            {
                if (drawPaint != null)
                    drawPaint.Dispose();

                if (myFont != null)
                    myFont.Dispose();

                if (myTypeface != null)
                    myTypeface.Dispose();
            }
        }

        private static void GetParams(char[] partsDelimiter, ref string line, int startLength, ref float x)
        {
            line = line.Remove(0, startLength);
            string[] parts = line.Split(partsDelimiter);
            if (parts.Length > 0)
            {
                string xVal = parts[0].Trim();
                x = Convert.ToSingle(xVal, CultureInfo.InvariantCulture);
            }
        }

        private static void GetParams(char[] partsDelimiter, ref string line, int startLength, ref float x, ref float y)
        {
            line = line.Remove(0, startLength);
            string[] parts = line.Split(partsDelimiter);
            if (parts.Length == 2)
            {
                string xVal = parts[0].Trim();
                string yVal = parts[1].Trim();
                x = Convert.ToSingle(xVal, CultureInfo.InvariantCulture);
                y = Convert.ToSingle(yVal, CultureInfo.InvariantCulture);
            }
            else if (parts.Length > 2)
            {
                string xVal = parts[0].Trim();
                string yVal = parts[1].Trim();
                x = Convert.ToSingle(xVal, CultureInfo.InvariantCulture);
                y = Convert.ToSingle(yVal, CultureInfo.InvariantCulture);

                line = "";
                for (int i = 2; i < parts.Length; i++)
                {
                    line = line + parts[i].Trim();
                    line = line + " ";
                }
            }
        }

        private static void GetParams(char[] partsDelimiter, ref string line, int startLength, ref SKPoint[] points)
        {
            line = line.Remove(0, startLength);
            string[] parts = line.Split(partsDelimiter);
            if (parts.Length > 1 && parts.Length % 2 == 0)
            {
                points = new SKPoint[parts.Length / 2];
                for (int i = 0; i < parts.Length; i = i + 2)
                {
                    string xVal = parts[i].Trim();
                    string yVal = parts[i+1].Trim();
                    float x = Convert.ToSingle(xVal, CultureInfo.InvariantCulture);
                    float y = Convert.ToSingle(yVal, CultureInfo.InvariantCulture);
                    SKPoint point = new SKPoint(x, y);
                    points[i / 2] = point;

//                    m_log.DebugFormat("[VECTOR RENDER MODULE]: Got point {0}", points[i / 2]);
                }
            }
        }

        private SKImage ImageHttpRequest(string url)
        {
            try
            {
                var handler = new HttpClientHandler();
                using (var client = new System.Net.Http.HttpClient(handler))
                {
                    using (var response = client.GetAsync(url).Result)
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            using (var s = response.Content.ReadAsStreamAsync().Result)
                            {
                                var data = new byte[s.Length];
                                s.Read(data, 0, (int)s.Length);
                                return SKImage.FromEncodedData(data);
                            }
                        }
                    }
                }
            }
            catch { }

            return null;
        }
    }
}
