/*
 * Copyright (c) Legion Builds
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

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using Xunit;

using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.CoreModules.Avatar.UserProfiles;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Region.CoreModules.Avatar.UserProfiles.Tests;

/// <summary>
/// IProfileModule.TryGetUserPartner reads the partner on a user's profile from the profiles service. The service
/// is a JSON-RPC listener on 127.0.0.1 that answers avatar_properties_request as UserProfilesService does.
/// </summary>
/// <remarks>
/// MainConsole.Instance is process-wide (the module adds a console command); this project runs its test
/// classes one at a time (AssemblyInfo.cs) and each test puts the previous console back.
/// </remarks>
public class UserPartnerLookupTests : OpenSimTestCase
{
    private static readonly UUID UserId = new("8c3d61f0-a2b9-4e57-91d4-6f0e2c7b3a95");
    private static readonly UUID PartnerId = new("1f9a4e27-6d03-4cb8-b7e1-58a2c0d9f364");

    private readonly ICommandConsole m_savedConsole;
    private readonly SocketsHttpHandler m_savedRedir;
    private readonly SocketsHttpHandler m_savedNoRedir;
    private readonly HttpListener m_listener = new();
    private readonly string m_url;
    private int m_requests;

    /// <summary>What the stand-in service answers: a partner, an error, or nothing usable.</summary>
    private Func<OSDMap, string> m_answer = _ => null;

    public UserPartnerLookupTests()
    {
        m_savedConsole = MainConsole.Instance;
        MainConsole.Instance = new MockConsole();

        // The profiles client posts through WebUtil's shared handlers, which the region server sets up at start.
        m_savedRedir = WebUtil.SharedSocketsHttpHandler;
        m_savedNoRedir = WebUtil.SharedSocketsHttpHandlerNoRedir;
        WebUtil.SetupHTTPClients(false, false, null, 4);

        int port;
        using (TcpListener probe = new(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }
        m_url = $"http://127.0.0.1:{port}/";
        m_listener.Prefixes.Add(m_url);
        m_listener.Start();
        _ = Task.Run(Serve);
    }

    public override void Dispose()
    {
        m_listener.Close();
        SocketsHttpHandler ours = WebUtil.SharedSocketsHttpHandler;
        SocketsHttpHandler oursNoRedir = WebUtil.SharedSocketsHttpHandlerNoRedir;
        WebUtil.SharedSocketsHttpHandler = m_savedRedir;
        WebUtil.SharedSocketsHttpHandlerNoRedir = m_savedNoRedir;
        ours?.Dispose();
        oursNoRedir?.Dispose();
        MainConsole.Instance = m_savedConsole;
        base.Dispose();
    }

    private async Task Serve()
    {
        while (m_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await m_listener.GetContextAsync(); }
            catch { return; }

            Interlocked.Increment(ref m_requests);
            string body;
            using (StreamReader reader = new(ctx.Request.InputStream, Encoding.UTF8))
                body = await reader.ReadToEndAsync();
            OSDMap request = (OSDMap)OSDParser.DeserializeJson(body);
            string reply = request["method"].AsString() == "avatar_properties_request" ? m_answer(request) : null;
            reply ??= "{\"jsonrpc\":\"2.0\",\"id\":\"" + request["id"].AsString() + "\",\"error\":{\"code\":-32601,\"message\":\"no\"}}";

            byte[] bytes = Encoding.UTF8.GetBytes(reply);
            ctx.Response.ContentType = "application/json-rpc";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private static string Properties(OSDMap request, UUID partner)
    {
        OSDMap props = (OSDMap)request["params"];
        props["PartnerId"] = OSD.FromUUID(partner);
        OSDMap reply = new()
        {
            { "jsonrpc", OSD.FromString("2.0") },
            { "id", request["id"] },
            { "result", props }
        };
        return OSDParser.SerializeJsonString(reply);
    }

    /// <summary>A stand-in for IUserManagement that says whether a user belongs to this grid.</summary>
    public class StandInUserManagement : DispatchProxy
    {
        public bool Local = true;

        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == "IsLocalGridUser")
                return Local;
            if (method.Name == "GetUserServerURL" && args.Length == 3)
            {
                args[2] = true;   // the out "failed": a foreign user's home grid is not known here
                return string.Empty;
            }
            Type rt = method.ReturnType;
            return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
        }
    }

    private IProfileModule Module(bool localUser = true)
    {
        TestScene scene = new SceneHelpers().SetupScene();
        IUserManagement users = DispatchProxy.Create<IUserManagement, StandInUserManagement>();
        ((StandInUserManagement)(object)users).Local = localUser;
        scene.RegisterModuleInterface<IUserManagement>(users);

        IConfigSource config = new IniConfigSource();
        config.AddConfig("UserProfiles").Set("ProfileServiceURL", m_url);
        UserProfileModule module = new();
        SceneHelpers.SetupSceneModules(scene, config, module);
        return module;
    }

    [Fact]
    public void ThePartnerOnTheProfileIsReturned()
    {
        m_answer = r => Properties(r, PartnerId);
        IProfileModule profiles = Module();

        Assert.True(profiles.TryGetUserPartner(UserId, out UUID partner));
        Assert.Equal(PartnerId, partner);
    }

    [Fact]
    public void AProfileWithNoPartnerReadsAsZero()
    {
        m_answer = r => Properties(r, UUID.Zero);
        IProfileModule profiles = Module();

        Assert.True(profiles.TryGetUserPartner(UserId, out UUID partner));
        Assert.Equal(UUID.Zero, partner);
    }

    [Fact]
    public void AProfileTheServiceCannotReadIsReportedAsUnknown()
    {
        m_answer = _ => null;   // the service answers with an error
        IProfileModule profiles = Module();

        Assert.False(profiles.TryGetUserPartner(UserId, out UUID partner));
        Assert.Equal(UUID.Zero, partner);
        Assert.Equal(1, m_requests);
    }

    [Fact]
    public void AForeignUsersHomeGridIsNotAsked()
    {
        m_answer = r => Properties(r, PartnerId);
        IProfileModule profiles = Module(localUser: false);

        Assert.False(profiles.TryGetUserPartner(UserId, out UUID partner));
        Assert.Equal(UUID.Zero, partner);
        Assert.Equal(0, m_requests);
    }

    /// <summary>A profile module that does not implement the lookup.</summary>
    private sealed class OtherProfileModule : IProfileModule
    {
        public void RequestAvatarProperties(IClientAPI remoteClient, UUID avatarID) { }
    }

    [Fact]
    public void AProfileModuleWithoutTheLookupReportsUnknown()
    {
        IProfileModule profiles = new OtherProfileModule();

        Assert.False(profiles.TryGetUserPartner(UserId, out UUID partner));
        Assert.Equal(UUID.Zero, partner);
    }
}
