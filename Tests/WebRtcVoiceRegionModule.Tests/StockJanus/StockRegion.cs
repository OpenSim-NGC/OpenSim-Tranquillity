/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * A region wired the way a region server wires voice, for the opt-in Janus integration tests: a real Scene with
 * two parcels, the region module and the voice service module read from an ini, and the service module loading
 * the Janus service by its DLL name. Requests go in through the region module's own capability handlers, as
 * the viewer's do, and nothing between the handler and the gateway is replaced.
 *
 * The one fake is the event queue, which only records what the region would send to viewers.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.CoreModules.World.Land;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using osWebRtcVoice.Tests.StockJanus;

namespace osWebRtcVoice.Tests
{
    /// <summary>A POST carrying an LLSD XML body, as the viewer sends to a capability.</summary>
    internal sealed class LlsdPost : IOSHttpRequest
    {
        public LlsdPost(OSDMap body)
        {
            InputStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(OSDParser.SerializeLLSDXmlString(body)));
        }
        public string HttpMethod => "POST";
        public Uri Url => new Uri("http://sim.example.org/cap");
        public string RawUrl => "/cap";
        public string UriPath => "/cap";
        public Stream InputStream { get; set; }
        public System.Collections.Specialized.NameValueCollection Headers { get; } = new();
        public bool HasEntityBody => true;
        public long ContentLength => InputStream.Length;
        public long ContentLength64 => InputStream.Length;
        public string ContentType => "application/llsd+xml";
        public string[] AcceptTypes => Array.Empty<string>();
        public System.Text.Encoding ContentEncoding => System.Text.Encoding.UTF8;
        public bool IsSecured => false;
        public bool KeepAlive => false;
        public System.Collections.Specialized.NameValueCollection QueryString => throw new NotImplementedException();
        public System.Collections.Hashtable Query => throw new NotImplementedException();
        public HashSet<string> QueryFlags => throw new NotImplementedException();
        public Dictionary<string, string> QueryAsDictionary => throw new NotImplementedException();
        public IPEndPoint RemoteIPEndPoint => new(IPAddress.Parse("192.0.2.1"), 1);
        public IPEndPoint LocalIPEndPoint => new(IPAddress.Parse("192.0.2.2"), 2);
        public string UserAgent => "test";
        public double ArrivalTS => 0;
    }

    /// <summary>Records what the region sends to viewers; accepts everything.</summary>
    internal sealed class RecordingEventQueue : IEventQueue
    {
        public readonly List<(string Name, OSD Body, UUID To)> Sent = new();
        public readonly List<(UUID SessionId, UUID TempSessionId, UUID To)> StartReplies = new();
        private string _lastName;
        private OSD _lastBody;

        public byte[] BuildEvent(string eventName, OSD eventBody) { _lastName = eventName; _lastBody = eventBody; return new byte[] { 1 }; }
        public bool Enqueue(byte[] o, UUID avatarID) { lock (Sent) Sent.Add((_lastName, _lastBody, avatarID)); return true; }
        public bool Enqueue(OSD o, UUID avatarID) { lock (Sent) Sent.Add(("osd", o, avatarID)); return true; }
        public bool Enqueue(osUTF8 o, UUID avatarID) => true;
        public void EnableSimulator(ulong handle, IPEndPoint endPoint, UUID avatarID, int regionSizeX, int regionSizeY) { }
        public void EstablishAgentCommunication(UUID avatarID, IPEndPoint endPoint, string capsPath, ulong regionHandle, int regionSizeX, int regionSizeY) { }
        public void TeleportFinishEvent(ulong regionHandle, byte simAccess, IPEndPoint regionExternalEndPoint, uint locationID, uint flags, string capsURL, UUID agentID, int regionSizeX, int regionSizeY) { }
        public void CrossRegion(ulong handle, Vector3 pos, Vector3 lookAt, IPEndPoint newRegionExternalEndPoint, string capsURL, UUID avatarID, UUID sessionID, int regionSizeX, int regionSizeY) { }
        public void ChatterboxInvitation(UUID sessionID, string sessionName, UUID fromAgent, string message, UUID toAgent, string fromName, byte dialog, uint timeStamp, bool offline, int parentEstateID, Vector3 position, uint ttl, UUID transactionID, bool fromGroup, byte[] binaryBucket) { }
        public void ChatterBoxSessionStartReply(UUID sessionID, string sessionName, int type, bool voiceEnabled, bool voiceModerated, UUID tmpSessionID, bool sucess, string error, UUID toAgent)
        {
            lock (StartReplies) StartReplies.Add((sessionID, tmpSessionID, toAgent));
        }
        public void ChatterBoxSessionAgentListUpdates(UUID sessionID, UUID toAgent, List<GroupChatListAgentUpdateData> updates) { }
        public void ChatterBoxForceClose(UUID toAgent, UUID sessionID, string reason) { }
        public void GroupMembershipData(UUID receiverAgent, GroupMembershipData[] data) { }
        public void ScriptRunningEvent(UUID objectID, UUID itemID, bool running, UUID avatarID) { }
        public void partPhysicsProperties(uint localID, byte physhapetype, float density, float friction, float bounce, float gravmod, UUID avatarID) { }
        public void WindlightRefreshEvent(int interpolate, UUID avatarID) { }
        public void SendEnvironmentUpdate(UUID experience_id, UUID agent_id, EnvironmentUpdate update) { }
        public void SendBulkUpdateInventoryItem(InventoryItemBase item, UUID avatarID, UUID? transationID = null) { }
        public osUTF8 StartEvent(string eventName) => throw new NotSupportedException();
        public osUTF8 StartEvent(string eventName, int cap) => throw new NotSupportedException();
        public void SendLargeGenericMessage(UUID avatarID, UUID? transationID, UUID? sessionID, string method, UUID invoice, List<byte[]> message) { }
        public void SendLargeGenericMessage(UUID avatarID, UUID? transationID, UUID? sessionID, string method, UUID invoice, List<string> message) { }
        public byte[] EndEventToBytes(osUTF8 sb) => throw new NotSupportedException();
    }

    /// <summary>What a capability answered.</summary>
    internal sealed class CapAnswer
    {
        public int Status;
        public OSDMap Body;

        public bool Provisioned => Body is not null && Body.ContainsKey("viewer_session");
        public string ViewerSession => Body?["viewer_session"].AsString();
        /// <summary>The mixer room the participant joined. The answer does not carry it, so the test works it out
        /// from the request the same way the service does (JanusAudioBridge.CalcRoomNumber).</summary>
        public int Room;
        public string AnswerSdp => (Body?["jsep"] as OSDMap)?["sdp"].AsString();
    }

    /// <summary>One participant that joined through the region: the peer, and what the region answered.</summary>
    internal sealed class Joined : IDisposable
    {
        public UUID Agent;
        public AiortcPeer Peer;
        public CapAnswer Answer;
        public void Dispose() => Peer?.Dispose();
    }

    internal sealed class StockRegion : IDisposable
    {
        public Scene Scene;
        public WebRtcVoiceRegionModule Region;
        public WebRtcVoiceServiceModule Service;
        public RecordingEventQueue Queue = new();
        /// <summary>The agent who owns both parcels.</summary>
        public static readonly UUID ParcelOwner = new UUID("a11ce000-0000-4000-8000-000000000001");
        public int WestParcelLocalId;
        public int EstateChannelParcelLocalId;

        /// <summary>The left half of the region (x below 128): an ordinary parcel with its own voice room.</summary>
        public static readonly Vector3 InWestParcel = new Vector3(64, 128, 25);
        /// <summary>The right half (x from 128): a parcel set to use the estate voice channel.</summary>
        public static readonly Vector3 InEstateChannelParcel = new Vector3(192, 128, 25);

        public static StockRegion Create(Action<IConfig, IConfig> tweak = null)
        {
            var r = new StockRegion();
            IConfigSource config = StockJanusEnvironment.MakeConfig(tweak);

            r.Scene = new SceneHelpers().SetupScene();
            var land = new LandManagementModule();
            SceneHelpers.SetupSceneModules(r.Scene, land);
            r.Scene.Permissions.OnIsAdministrator += _ => false;
            r.Scene.RegionInfo.EstateSettings.AllowVoice = true;
            r.Scene.RegisterModuleInterface<IEventQueue>(r.Queue);

            ILandObject west = AddParcel(r.Scene, land, 0, 0, 128, 256, estateChannel: false);
            ILandObject east = AddParcel(r.Scene, land, 128, 0, 256, 256, estateChannel: true);
            r.WestParcelLocalId = west.LandData.LocalID;
            r.EstateChannelParcelLocalId = east.LandData.LocalID;

            // The service module loads the Janus service by DLL name from [WebRtcVoice], exactly as a region server does.
            r.Service = new WebRtcVoiceServiceModule();
            r.Service.Initialise(config);
            r.Service.AddRegion(r.Scene);

            r.Region = new WebRtcVoiceRegionModule();
            r.Region.Initialise(config);
            r.Region.AddRegion(r.Scene);
            r.Region.RegionLoaded(r.Scene);
            return r;
        }

        private static ILandObject AddParcel(Scene scene, LandManagementModule land, int x0, int y0, int x1, int y1, bool estateChannel)
        {
            ILandObject lo = new LandObject(ParcelOwner, false, scene);
            lo.SetLandBitmap(lo.GetSquareLandBitmap(x0, y0, x1, y1));
            lo = land.AddLandObject(lo);
            lo.LandData.Flags |= (uint)ParcelFlags.AllowVoiceChat;
            if (estateChannel)
                lo.LandData.Flags |= (uint)ParcelFlags.UseEstateVoiceChan;
            return lo;
        }

        public ScenePresence AddAvatar(UUID id, Vector3 position)
        {
            ScenePresence sp = SceneHelpers.AddScenePresence(Scene, id);
            sp.AbsolutePosition = position;
            return sp;
        }

        public Task<CapAnswer> ProvisionAsync(UUID agent, OSDMap body) => Task.Run(() =>
        {
            var resp = new TestOSHttpResponse();
            Region.ProvisionVoiceAccountRequest(new LlsdPost(body), resp, agent, Scene);
            return Answer(resp);
        });

        public Task<CapAnswer> ChatSessionAsync(UUID agent, OSDMap body) => Task.Run(() =>
        {
            var resp = new TestOSHttpResponse();
            Region.ChatSessionRequest(new LlsdPost(body), resp, agent, Scene);
            return Answer(resp);
        });

        private static CapAnswer Answer(TestOSHttpResponse resp)
        {
            OSDMap parsed = null;
            if (resp.RawBuffer is { Length: > 0 })
            {
                try { parsed = OSDParser.DeserializeLLSDXml(resp.RawBuffer) as OSDMap; } catch (Exception) { }
            }
            return new CapAnswer { Status = resp.StatusCode, Body = parsed };
        }

        public static OSDMap LocalBody(string offerSdp, int? parcelLocalId = null)
        {
            var m = new OSDMap
            {
                ["channel_type"] = OSD.FromString("local"),
                ["voice_server_type"] = OSD.FromString("webrtc"),
                ["jsep"] = new OSDMap { ["type"] = OSD.FromString("offer"), ["sdp"] = OSD.FromString(offerSdp) },
            };
            if (parcelLocalId.HasValue)
                m["parcel_local_id"] = OSD.FromInteger(parcelLocalId.Value);
            return m;
        }

        public static OSDMap MultiagentBody(string offerSdp, string channel, string credentials) => new OSDMap
        {
            ["channel_type"] = OSD.FromString("multiagent"),
            ["voice_server_type"] = OSD.FromString("webrtc"),
            ["channel"] = OSD.FromString(channel),
            ["credentials"] = OSD.FromString(credentials),
            ["jsep"] = new OSDMap { ["type"] = OSD.FromString("offer"), ["sdp"] = OSD.FromString(offerSdp) },
        };

        public static OSDMap LogoutBody(string viewerSession) => new OSDMap
        {
            ["logout"] = OSD.FromBoolean(true),
            ["viewer_session"] = OSD.FromString(viewerSession),
            ["voice_server_type"] = OSD.FromString("webrtc"),
        };

        /// <summary>Starts a real WebRTC peer, sends its offer through the region, and applies the answer to the peer.
        /// <paramref name="makeBody"/> turns the offer SDP into the capability body.</summary>
        public async Task<Joined> JoinAsync(UUID agent, Func<string, OSDMap> makeBody, double toneHz = 0, bool waitConnected = true)
        {
            var peer = await AiortcPeer.StartAsync(toneHz);
            var joined = new Joined { Agent = agent, Peer = peer };
            OSDMap body = makeBody(peer.Offer);
            joined.Answer = await ProvisionAsync(agent, body);
            if (joined.Answer.Provisioned)
                joined.Answer.Room = LocalRoomOf(body);
            if (joined.Answer.Provisioned && joined.Answer.AnswerSdp is not null)
            {
                peer.Answer(joined.Answer.AnswerSdp);
                if (waitConnected)
                    await peer.WaitConnectedAsync(TimeSpan.FromSeconds(30));
            }
            return joined;
        }

        /// <summary>The room a "local" provision joins: the region-wide estate room, or the room of the named parcel.</summary>
        private int LocalRoomOf(OSDMap body)
        {
            int parcel = JanusAudioBridge.REGION_ROOM_ID;
            if (body.TryGetInt("parcel_local_id", out int named) && named != EstateChannelParcelLocalId)
                parcel = named;
            return JanusAudioBridge.CalcRoomNumber(Scene.RegionInfo.RegionID.ToString(), "local", parcel, string.Empty);
        }

        public async Task<CapAnswer> LogoutAsync(Joined j) => await ProvisionAsync(j.Agent, LogoutBody(j.Answer.ViewerSession));

        public T GetPrivate<T>(object target, string field)
        {
            Type t = target.GetType();
            FieldInfo f = t.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                          ?? throw new MissingFieldException(t.Name, field);
            return (T)f.GetValue(target);
        }

        public void SetPrivate(object target, string field, object value)
        {
            Type t = target.GetType();
            FieldInfo f = t.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                          ?? t.GetField(field, BindingFlags.Static | BindingFlags.NonPublic)
                          ?? throw new MissingFieldException(t.Name, field);
            f.SetValue(f.IsStatic ? null : target, value);
        }

        public void Dispose()
        {
            try { Region?.RemoveRegion(Scene); } catch (Exception) { }
            try { Service?.RemoveRegion(Scene); } catch (Exception) { }
        }
    }
}
