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
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Nini.Config;
using Xunit;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;
using OpenSim.Server.Handlers.Asset;
using OpenSim.Services.AssetService;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Server.Handlers.Asset.Test
{
    public class AssetServerPostHandlerTests : OpenSimTestCase
    {
        [Fact]
        public void TestGoodAssetStoreRequest()
        {
            TestHelpers.InMethod();

            UUID assetId = TestHelpers.ParseTail(0x1);

            IConfigSource config = new IniConfigSource();
            config.AddConfig("AssetService");
            config.Configs["AssetService"].Set("StorageProvider", "OpenSim.Tests.Common.dll");

            AssetService assetService = new AssetService(config);

            AssetServerPostHandler asph = new AssetServerPostHandler(assetService, null, TrustedAccess());

            AssetBase asset = AssetHelpers.CreateNotecardAsset(assetId, "Hello World");

            MemoryStream buffer = new MemoryStream();

            XmlWriterSettings settings = new XmlWriterSettings();
            settings.Encoding = Encoding.UTF8;

            using (XmlWriter writer = XmlWriter.Create(buffer, settings))
            {
                XmlSerializer serializer = new XmlSerializer(typeof(AssetBase));
                serializer.Serialize(writer, asset);
                writer.Flush();
            }

            buffer.Position = 0;
            TestOSHttpResponse response = new TestOSHttpResponse();
            asph.Handle(string.Empty, buffer, TrustedRequest(), response);

            AssetBase retrievedAsset = assetService.Get(assetId.ToString());

            Xunit.Assert.NotNull(retrievedAsset);
        }

        [Fact]
        public void TestUntrustedAssetStoreRequestIsNotGatedByControlPlane()
        {
            TestHelpers.InMethod();

            UUID assetId = TestHelpers.ParseTail(0x1);
            IConfigSource config = new IniConfigSource();
            config.AddConfig("AssetService");
            config.Configs["AssetService"].Set("StorageProvider", "OpenSim.Tests.Common.dll");
            AssetService assetService = new AssetService(config);
            AssetServerPostHandler handler = new AssetServerPostHandler(
                assetService, null, new ControlPlaneAccess(new IniConfigSource()));

            AssetBase asset = AssetHelpers.CreateNotecardAsset(assetId, "Upload without control-plane gating");
            using MemoryStream buffer = new MemoryStream();
            XmlSerializer serializer = new XmlSerializer(typeof(AssetBase));
            serializer.Serialize(buffer, asset);
            buffer.Position = 0;

            TestOSHttpResponse response = new TestOSHttpResponse();
            handler.Handle(string.Empty, buffer,
                new TestRequest(new IPEndPoint(IPAddress.Parse("203.0.113.10"), 9000)), response);

            Xunit.Assert.Equal(0, response.StatusCode);
            Xunit.Assert.NotNull(assetService.Get(assetId.ToString()));
        }

        [Xunit.Theory]
        [InlineData(AllowedRemoteDeleteTypes.All, true)]
        [InlineData(AllowedRemoteDeleteTypes.None, false)]
        [InlineData(AllowedRemoteDeleteTypes.MapTile, false)]
        public void TestUntrustedAssetDeleteRetainsTypeRestrictions(
            AllowedRemoteDeleteTypes allowedTypes, bool expectedDeleted)
        {
            UUID assetId = TestHelpers.ParseTail(0x1);
            IConfigSource config = new IniConfigSource();
            config.AddConfig("AssetService");
            config.Configs["AssetService"].Set("StorageProvider", "OpenSim.Tests.Common.dll");
            DeletionTrackingAssetService assetService = new DeletionTrackingAssetService(config);
            assetService.Store(AssetHelpers.CreateNotecardAsset(assetId, "Delete test"));
            AssetServerDeleteHandler handler = new AssetServerDeleteHandler(
                assetService, allowedTypes, null, new ControlPlaneAccess(new IniConfigSource()));

            TestOSHttpResponse response = new TestOSHttpResponse();
            byte[] result = handler.Handle("/assets/" + assetId, Stream.Null,
                new TestRequest(new IPEndPoint(IPAddress.Parse("203.0.113.10"), 9000)), response);

            using MemoryStream buffer = new MemoryStream(result);
            bool deleted = Xunit.Assert.IsType<bool>(new XmlSerializer(typeof(bool)).Deserialize(buffer));
            Xunit.Assert.Equal(0, response.StatusCode);
            Xunit.Assert.Equal(expectedDeleted, deleted);
            Xunit.Assert.Equal(expectedDeleted, assetService.DeleteCalled);
        }

        private sealed class DeletionTrackingAssetService : AssetService
        {
            public bool DeleteCalled { get; private set; }

            public DeletionTrackingAssetService(IConfigSource config) : base(config) {}

            public override bool Delete(string id)
            {
                DeleteCalled = true;
                return true;
            }
        }

        [Fact]
        public void TestBadXmlAssetStoreRequest()
        {
            TestHelpers.InMethod();

            IConfigSource config = new IniConfigSource();
            config.AddConfig("AssetService");
            config.Configs["AssetService"].Set("StorageProvider", "OpenSim.Tests.Common.dll");

            AssetService assetService = new AssetService(config);

            AssetServerPostHandler asph = new AssetServerPostHandler(assetService, null, TrustedAccess());

            MemoryStream buffer = new MemoryStream();
            byte[] badData = new byte[] { 0x48, 0x65, 0x6c, 0x6c, 0x6f };
            buffer.Write(badData, 0, badData.Length);
            buffer.Position = 0;

            TestOSHttpResponse response = new TestOSHttpResponse();
            asph.Handle(string.Empty, buffer, TrustedRequest(), response);

            Xunit.Assert.Equal((int)HttpStatusCode.BadRequest, response.StatusCode);
        }

        private static ControlPlaneAccess TrustedAccess()
        {
            IniConfigSource config = new IniConfigSource();
            config.AddConfig("Network").Set("ControlPlaneTrustedHosts", "127.0.0.1");
            return new ControlPlaneAccess(config);
        }

        private static IOSHttpRequest TrustedRequest()
        {
            return new TestRequest(new IPEndPoint(IPAddress.Loopback, 9000));
        }

        private sealed class TestRequest : IOSHttpRequest
        {
            public TestRequest(IPEndPoint remoteEndPoint)
            {
                RemoteIPEndPoint = remoteEndPoint;
                Headers = new NameValueCollection();
            }

            public string[] AcceptTypes => Array.Empty<string>();
            public Encoding ContentEncoding => Encoding.UTF8;
            public long ContentLength => 0;
            public long ContentLength64 => 0;
            public string ContentType => string.Empty;
            public bool HasEntityBody => true;
            public NameValueCollection Headers { get; }
            public string HttpMethod => "POST";
            public Stream InputStream => Stream.Null;
            public bool IsSecured => false;
            public bool KeepAlive => false;
            public NameValueCollection QueryString => new();
            public Hashtable Query => new();
            public HashSet<string> QueryFlags => new();
            public Dictionary<string, string> QueryAsDictionary => new();
            public string RawUrl => "/assets";
            public IPEndPoint RemoteIPEndPoint { get; }
            public IPEndPoint LocalIPEndPoint => new(IPAddress.Loopback, 0);
            public Uri Url => new("http://localhost/assets");
            public string UriPath => "/assets";
            public string UserAgent => string.Empty;
            public double ArrivalTS => 0;
        }
    }
}