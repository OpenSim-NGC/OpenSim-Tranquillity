using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Threading;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Caps = OpenSim.Framework.Capabilities.Caps;
using OSDArray = OpenMetaverse.StructuredData.OSDArray;
using OSDMap = OpenMetaverse.StructuredData.OSDMap;

[assembly: Addin("TasiaAddons.FriendConference", "1.0.0")]
[assembly: AddinDescription("Ad-hoc friend conference IM module")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]

namespace TasiaAddons.FriendConference;

[Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "FriendConferenceModule")]
public class FriendConferenceModule : ISharedRegionModule
{
    private sealed class SessionInfo
    {
        public UUID SessionId;
        public UUID Owner;
        public readonly HashSet<UUID> Members = new();
        public long LastActiveUnix;
        public string Name = "Conference";
    }

    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    private readonly object m_sync = new();
    private readonly List<Scene> m_scenes = new();
    private readonly Dictionary<UUID, SessionInfo> m_sessions = new();
    private readonly Dictionary<string, long> m_inviteThrottleByPair = new();
    private readonly Dictionary<Scene, EventManager.RegisterCapsEvent> m_capsHandlers = new();

    private bool m_enabled;
    private bool m_requireFriendship = true;
    private bool m_debug;
    private int m_maxParticipants = 20;
    private int m_sessionIdleSeconds = 3600;
    private int m_inviteThrottleMs = 500;
    private string m_systemName = "Grid System";
    private string m_defaultConferenceText = "Conference Started from Grid System";
    private Timer m_cleanupTimer;

    public string Name => "FriendConferenceModule";
    public Type ReplaceableInterface => null;

    public void Initialise(IConfigSource source)
    {
        IConfig cfg = source.Configs["FriendConference"];
        if (cfg == null)
        {
            m_enabled = false;
            return;
        }

        m_enabled = cfg.GetBoolean("Enabled", false);
        if (!m_enabled)
            return;

        m_requireFriendship = cfg.GetBoolean("RequireFriendship", true);
        m_debug = cfg.GetBoolean("Debug", false);
        m_maxParticipants = Math.Max(2, cfg.GetInt("MaxParticipants", 20));
        m_sessionIdleSeconds = Math.Max(60, cfg.GetInt("SessionIdleSeconds", 3600));
        m_inviteThrottleMs = Math.Max(0, cfg.GetInt("InviteThrottleMs", 500));
        m_systemName = cfg.GetString("SystemName", "Grid System");
        m_defaultConferenceText = cfg.GetString("DefaultConferenceText", "Conference Started from Grid System");
        if (string.IsNullOrWhiteSpace(m_systemName))
            m_systemName = "Grid System";
        if (string.IsNullOrWhiteSpace(m_defaultConferenceText))
            m_defaultConferenceText = "Conference Started from Grid System";

        Log.InfoFormat("[FRIEND CONFERENCE]: Enabled. maxParticipants={0}, idle={1}s, requireFriendship={2}",
            m_maxParticipants, m_sessionIdleSeconds, m_requireFriendship);
    }

    public void AddRegion(Scene scene)
    {
        if (!m_enabled)
            return;

        lock (m_sync)
        {
            if (!m_scenes.Contains(scene))
                m_scenes.Add(scene);
        }

        scene.EventManager.OnNewClient += OnNewClient;
        scene.EventManager.OnIncomingInstantMessage += OnIncomingInstantMessage;
        EventManager.RegisterCapsEvent capsHandler = (agentId, caps) => OnRegisterCaps(scene, agentId, caps);
        m_capsHandlers[scene] = capsHandler;
        scene.EventManager.OnRegisterCaps += capsHandler;

        EnsureCleanupTimer();
    }

    public void RegionLoaded(Scene scene)
    {
    }

    public void RemoveRegion(Scene scene)
    {
        if (!m_enabled)
            return;

        scene.EventManager.OnNewClient -= OnNewClient;
        scene.EventManager.OnIncomingInstantMessage -= OnIncomingInstantMessage;
        if (m_capsHandlers.TryGetValue(scene, out EventManager.RegisterCapsEvent capsHandler))
        {
            scene.EventManager.OnRegisterCaps -= capsHandler;
            m_capsHandlers.Remove(scene);
        }

        lock (m_sync)
        {
            m_scenes.Remove(scene);
            if (m_scenes.Count == 0)
                StopCleanupTimer();
        }
    }

    public void Close()
    {
        StopCleanupTimer();

        lock (m_sync)
        {
            foreach (Scene scene in m_scenes)
            {
                scene.EventManager.OnNewClient -= OnNewClient;
                scene.EventManager.OnIncomingInstantMessage -= OnIncomingInstantMessage;
                if (m_capsHandlers.TryGetValue(scene, out EventManager.RegisterCapsEvent capsHandler))
                    scene.EventManager.OnRegisterCaps -= capsHandler;
            }

            m_capsHandlers.Clear();
            m_scenes.Clear();
            m_sessions.Clear();
            m_inviteThrottleByPair.Clear();
        }
    }

    public void PostInitialise()
    {
    }

    private void OnNewClient(IClientAPI client)
    {
        client.OnInstantMessage += OnViewerInstantMessage;
    }

    private void OnRegisterCaps(Scene scene, UUID agentId, Caps caps)
    {
        if (!m_enabled || scene == null || caps == null)
            return;

        caps.RegisterSimpleHandler("ChatSessionRequest",
            new SimpleStreamHandler("/" + UUID.Random(), (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse) =>
            {
                HandleChatSessionRequest(scene, agentId, httpRequest, httpResponse);
            }));

        if (m_debug)
            Log.InfoFormat("[FRIEND CONFERENCE]: Registered ChatSessionRequest cap for agent={0} scene={1}", agentId, scene.RegionInfo?.RegionName ?? "?");
    }

    private void HandleChatSessionRequest(Scene scene, UUID agentId, IOSHttpRequest request, IOSHttpResponse response)
    {
        if (request.HttpMethod != "POST")
        {
            response.StatusCode = 404;
            return;
        }

        OSDMap map = ReadRequestMap(request);
        string method = ExtractMethod(map);

        if (m_debug)
            Log.InfoFormat("[FRIEND CONFERENCE]: ChatSessionRequest agent={0} method='{1}' payload={2}", agentId, method, SafePayloadPreview(map));

        IClientAPI caller = GetActiveClient(agentId);

        if (caller != null && method.Contains("start", StringComparison.OrdinalIgnoreCase))
        {
            UUID[] targets = ExtractInviteTargetsFromRequest(map, agentId);
            string sessionName = ExtractSessionName(map);
            GridInstantMessage synthetic = new()
            {
                fromAgentID = agentId.Guid,
                toAgentID = (targets.Length > 0 ? targets[0] : UUID.Zero).Guid,
                fromAgentName = caller.Name,
                dialog = (byte)InstantMessageDialog.SessionGroupStart,
                fromGroup = false,
                imSessionID = ExtractSessionIdFromRequest(map).Guid,
                message = sessionName,
                offline = 0,
                timestamp = (uint)Util.UnixTimeSinceEpoch(),
                binaryBucket = PackUuidArray(targets)
            };

            HandleSessionStart(caller, synthetic, agentId);
        }

        response.StatusCode = 200;
        response.RawBuffer = Util.UTF8NBGetbytes("<llsd><boolean>true</boolean></llsd>");
    }

    private static OSDMap ReadRequestMap(IOSHttpRequest request)
    {
        try
        {
            using Stream s = request.InputStream;
            if (s == null)
                return new OSDMap();

            if (s.CanSeek)
                s.Position = 0;

            OSD osd = OSDParser.DeserializeLLSDXml(s);
            return osd as OSDMap ?? new OSDMap();
        }
        catch
        {
            return new OSDMap();
        }
    }

    private static string ExtractMethod(OSDMap map)
    {
        if (map == null)
            return string.Empty;

        if (map.TryGetValue("method", out OSD method))
            return method.AsString() ?? string.Empty;
        if (map.TryGetValue("method_name", out OSD methodName))
            return methodName.AsString() ?? string.Empty;

        if (map.TryGetValue("params", out OSD paramsOsd) && paramsOsd is OSDMap p)
        {
            if (p.TryGetValue("method", out OSD nestedMethod))
                return nestedMethod.AsString() ?? string.Empty;
            if (p.TryGetValue("method_name", out OSD nestedMethodName))
                return nestedMethodName.AsString() ?? string.Empty;
        }

        return string.Empty;
    }

    private UUID ExtractSessionIdFromRequest(OSDMap map)
    {
        UUID sid = ExtractUuidByKey(map, "session-id");
        if (!sid.IsZero())
            return sid;

        sid = ExtractUuidByKey(map, "session_id");
        if (!sid.IsZero())
            return sid;

        sid = ExtractUuidByKey(map, "temp_session_id");
        if (!sid.IsZero())
            return sid;

        return UUID.Random();
    }

    private static string ExtractSessionName(OSDMap map)
    {
        string name = ExtractStringByKey(map, "session_name");
        if (!string.IsNullOrWhiteSpace(name))
            return name.Trim();

        name = ExtractStringByKey(map, "session-name");
        if (!string.IsNullOrWhiteSpace(name))
            return name.Trim();

        return "Conference";
    }

    private static string ExtractStringByKey(OSDMap map, string key)
    {
        if (map == null)
            return string.Empty;

        if (map.TryGetValue(key, out OSD v))
            return v.AsString() ?? string.Empty;

        if (map.TryGetValue("params", out OSD paramsOsd) && paramsOsd is OSDMap p && p.TryGetValue(key, out OSD nested))
            return nested.AsString() ?? string.Empty;

        return string.Empty;
    }

    private static UUID ExtractUuidByKey(OSDMap map, string key)
    {
        if (map == null)
            return UUID.Zero;

        if (map.TryGetValue(key, out OSD v) && UUID.TryParse(v.AsString(), out UUID id))
            return id;

        if (map.TryGetValue("params", out OSD paramsOsd)
            && paramsOsd is OSDMap p
            && p.TryGetValue(key, out OSD nested)
            && UUID.TryParse(nested.AsString(), out id))
            return id;

        return UUID.Zero;
    }

    private static UUID[] ExtractInviteTargetsFromRequest(OSDMap map, UUID inviter)
    {
        HashSet<UUID> targets = new();
        AddInviteCandidates(map, targets, inviter);

        if (map.TryGetValue("params", out OSD paramsOsd))
        {
            if (paramsOsd is OSDMap paramsMap)
                AddInviteCandidates(paramsMap, targets, inviter);

            AddUuidCandidatesFromOsd(paramsOsd, targets, inviter);
        }

        return targets.ToArray();
    }

    private static void AddUuidCandidatesFromOsd(OSD raw, HashSet<UUID> targets, UUID inviter)
    {
        if (raw == null)
            return;

        if (raw is OSDArray arr)
        {
            foreach (OSD item in arr)
                AddUuidCandidatesFromOsd(item, targets, inviter);
            return;
        }

        if (raw is OSDMap map)
        {
            AddInviteCandidates(map, targets, inviter);
            foreach (KeyValuePair<string, OSD> kv in map)
                AddUuidCandidatesFromOsd(kv.Value, targets, inviter);
            return;
        }

        string text = raw.AsString();
        if (string.IsNullOrWhiteSpace(text))
            return;

        foreach (string part in text.Split(new[] { ',', ';', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (UUID.TryParse(part.Trim(), out UUID id) && !id.IsZero() && id != inviter)
                targets.Add(id);
        }
    }

    private static void AddInviteCandidates(OSDMap map, HashSet<UUID> targets, UUID inviter)
    {
        TryAddUuid(map, "to_id", targets, inviter);
        TryAddUuid(map, "to_agent_id", targets, inviter);
        TryAddUuid(map, "to_agent", targets, inviter);
        TryAddUuid(map, "agent_id", targets, inviter);

        TryAddUuidList(map, "to_ids", targets, inviter);
        TryAddUuidList(map, "to_agent_ids", targets, inviter);
        TryAddUuidList(map, "invitees", targets, inviter);
        TryAddUuidList(map, "agent_ids", targets, inviter);
    }

    private static void TryAddUuid(OSDMap map, string key, HashSet<UUID> targets, UUID inviter)
    {
        if (!map.TryGetValue(key, out OSD raw))
            return;

        if (UUID.TryParse(raw.AsString(), out UUID id) && !id.IsZero() && id != inviter)
            targets.Add(id);
    }

    private static void TryAddUuidList(OSDMap map, string key, HashSet<UUID> targets, UUID inviter)
    {
        if (!map.TryGetValue(key, out OSD raw))
            return;

        if (raw is OSDArray arr)
        {
            foreach (OSD item in arr)
            {
                if (UUID.TryParse(item.AsString(), out UUID id) && !id.IsZero() && id != inviter)
                    targets.Add(id);
            }
            return;
        }

        string s = raw.AsString();
        if (string.IsNullOrWhiteSpace(s))
            return;

        foreach (string part in s.Split(new[] { ',', ';', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (UUID.TryParse(part.Trim(), out UUID id) && !id.IsZero() && id != inviter)
                targets.Add(id);
        }
    }

    private static byte[] PackUuidArray(UUID[] ids)
    {
        if (ids == null || ids.Length == 0)
            return Array.Empty<byte>();

        byte[] data = new byte[ids.Length * 16];
        int offset = 0;
        foreach (UUID id in ids)
        {
            byte[] b = id.GetBytes();
            Buffer.BlockCopy(b, 0, data, offset, 16);
            offset += 16;
        }
        return data;
    }

    private static string SafePayloadPreview(OSDMap map)
    {
        if (map == null || map.Count == 0)
            return "{}";

        string payload;
        try
        {
            payload = OSDParser.SerializeJsonString(map, true);
        }
        catch
        {
            payload = map.ToString();
        }

        if (payload.Length > 600)
            payload = payload[..600] + "...";
        return payload;
    }

    private void OnViewerInstantMessage(IClientAPI remoteClient, GridInstantMessage im)
    {
        if (!m_enabled || remoteClient == null || im == null)
            return;

        const byte SessionConferenceStartDialog = 16;

        InstantMessageDialog dialog = (InstantMessageDialog)im.dialog;
        bool isStart = im.dialog == SessionConferenceStartDialog || dialog == InstantMessageDialog.SessionGroupStart;
        if (!isStart && dialog is not (InstantMessageDialog.SessionAdd or InstantMessageDialog.SessionDrop or InstantMessageDialog.SessionSend))
            return;

        if (m_debug)
        {
            Log.InfoFormat("[FRIEND CONFERENCE]: IM dialog={0} from={1} to={2} session={3} msg='{4}' bucketLen={5}",
                dialog,
                im.fromAgentID,
                im.toAgentID,
                im.imSessionID,
                im.message,
                im.binaryBucket?.Length ?? 0);
        }

        UUID from = new(im.fromAgentID);
        if (from.IsZero())
            from = remoteClient.AgentId;

        if (isStart)
        {
            HandleSessionStart(remoteClient, im, from);
            return;
        }

        switch (dialog)
        {
            case InstantMessageDialog.SessionAdd:
                HandleSessionAdd(remoteClient.Scene as Scene, im, from);
                return;

            case InstantMessageDialog.SessionDrop:
                HandleSessionDrop(remoteClient.Scene as Scene, im, from);
                return;

            case InstantMessageDialog.SessionSend:
                HandleSessionSend(remoteClient.Scene as Scene, im, from);
                return;
        }
    }

    private void OnIncomingInstantMessage(GridInstantMessage im)
    {
        if (!m_enabled || im == null)
            return;

        const byte SessionConferenceStartDialog = 16;

        InstantMessageDialog dialog = (InstantMessageDialog)im.dialog;
        bool isStart = im.dialog == SessionConferenceStartDialog || dialog == InstantMessageDialog.SessionGroupStart;
        if (!isStart && dialog is not (InstantMessageDialog.SessionAdd or InstantMessageDialog.SessionDrop or InstantMessageDialog.SessionSend))
            return;

        UUID sessionId = new(im.imSessionID);
        if (sessionId.IsZero())
            return;

        UUID to = new(im.toAgentID);
        IClientAPI client = GetActiveClient(to);
        if (client != null)
            client.SendInstantMessage(im);

        if (client != null && dialog == InstantMessageDialog.SessionAdd)
        {
            UUID fromInvite = new(im.fromAgentID);
            SessionInfo imported = EnsureSessionForIncomingInvite(im, fromInvite, to);
            SendChatterboxInvitation(client, imported, fromInvite.IsZero() ? to : fromInvite, im.message);

            if (m_debug)
                Log.InfoFormat("[FRIEND CONFERENCE]: Imported remote invite session={0} inviter={1} invitee={2}", imported.SessionId, fromInvite, to);
        }

        if (isStart && client != null)
        {
            UUID from = new(im.fromAgentID);
            if (from.IsZero())
                from = client.AgentId;
            HandleSessionStart(client, im, from);
        }
    }

    private void HandleSessionStart(IClientAPI caller, GridInstantMessage im, UUID from)
    {
        UUID sessionId = new(im.imSessionID);
        if (sessionId.IsZero())
            sessionId = UUID.Random();

        UUID[] inviteTargets = ExtractInviteTargets(im);

        SessionInfo session;
        lock (m_sync)
        {
            if (!m_sessions.TryGetValue(sessionId, out session))
            {
                session = new SessionInfo
                {
                    SessionId = sessionId,
                    Owner = from,
                    LastActiveUnix = Util.UnixTimeSinceEpoch(),
                    Name = string.IsNullOrWhiteSpace(im.message) ? m_defaultConferenceText : im.message.Trim()
                };

                m_sessions[sessionId] = session;
            }

            session.Members.Add(from);
            foreach (UUID invitee in inviteTargets)
            {
                if (!invitee.IsZero() && invitee != from && session.Members.Count < m_maxParticipants)
                    session.Members.Add(invitee);
            }
            session.LastActiveUnix = Util.UnixTimeSinceEpoch();
        }

        SendSessionStartReply(caller, session);

        if (m_debug)
            Log.InfoFormat("[FRIEND CONFERENCE]: Session start {0} by {1}, invitees={2}", session.SessionId, from, inviteTargets.Length);

        foreach (UUID invitee in inviteTargets)
            InviteMember(caller.Scene as Scene, session, from, invitee, im.message);
    }

    private void HandleSessionAdd(Scene scene, GridInstantMessage im, UUID from)
    {
        UUID sessionId = new(im.imSessionID);
        if (sessionId.IsZero())
            return;

        SessionInfo session;
        lock (m_sync)
        {
            if (!m_sessions.TryGetValue(sessionId, out session))
                return;
        }

        if (!session.Members.Contains(from))
            return;

        foreach (UUID invitee in ExtractInviteTargets(im))
            InviteMember(scene, session, from, invitee, im.message);
    }

    private void HandleSessionDrop(Scene scene, GridInstantMessage im, UUID from)
    {
        UUID sessionId = new(im.imSessionID);
        if (sessionId.IsZero())
            return;

        lock (m_sync)
        {
            if (!m_sessions.TryGetValue(sessionId, out SessionInfo session))
                return;

            session.Members.Remove(from);
            session.LastActiveUnix = Util.UnixTimeSinceEpoch();

            if (session.Members.Count == 0)
                m_sessions.Remove(sessionId);
        }
    }

    private void HandleSessionSend(Scene scene, GridInstantMessage im, UUID from)
    {
        UUID sessionId = new(im.imSessionID);
        if (sessionId.IsZero())
            return;

        SessionInfo session;
        UUID[] targets;

        lock (m_sync)
        {
            if (!m_sessions.TryGetValue(sessionId, out session))
            {
                session = new SessionInfo
                {
                    SessionId = sessionId,
                    Owner = from,
                    LastActiveUnix = Util.UnixTimeSinceEpoch(),
                    Name = string.IsNullOrWhiteSpace(im.message) ? m_defaultConferenceText : im.message.Trim()
                };
                m_sessions[sessionId] = session;
            }

            session.Members.Add(from);
            UUID direct = new(im.toAgentID);
            if (!direct.IsZero() && direct != from)
                session.Members.Add(direct);

            foreach (UUID hinted in ExtractInviteTargets(im))
            {
                if (!hinted.IsZero() && hinted != from)
                    session.Members.Add(hinted);
            }

            session.LastActiveUnix = Util.UnixTimeSinceEpoch();
            targets = session.Members.Where(m => m != from).ToArray();
        }

        foreach (UUID target in targets)
        {
            GridInstantMessage msg = CloneForTarget(im, target);
            SendMessage(scene, msg);
        }
    }

    private void InviteMember(Scene scene, SessionInfo session, UUID inviter, UUID invitee, string message)
    {
        if (scene == null || invitee.IsZero() || invitee == inviter)
        {
            if (m_debug)
                Log.InfoFormat("[FRIEND CONFERENCE]: Invite skipped scene/invitee invalid inviter={0} invitee={1}", inviter, invitee);
            return;
        }

        long now = (long)Util.GetTimeStampMS();
        lock (m_sync)
        {
            string throttleKey = inviter.ToString() + ":" + invitee.ToString();
            if (m_inviteThrottleByPair.TryGetValue(throttleKey, out long last) && (now - last) < m_inviteThrottleMs)
            {
                if (m_debug)
                    Log.InfoFormat("[FRIEND CONFERENCE]: Invite throttled inviter={0} waitMs={1}", inviter, m_inviteThrottleMs - (now - last));
                return;
            }

            m_inviteThrottleByPair[throttleKey] = now;

            if (session.Members.Count >= m_maxParticipants)
            {
                if (m_debug)
                    Log.InfoFormat("[FRIEND CONFERENCE]: Invite skipped max participants reached session={0}", session.SessionId);
                return;
            }

            if (m_requireFriendship)
            {
                IFriendsModule fm = scene.RequestModuleInterface<IFriendsModule>();
                if (fm != null && !fm.IsFriend(inviter, invitee))
                {
                    if (m_debug)
                        Log.InfoFormat("[FRIEND CONFERENCE]: Invite denied by friendship inviter={0} invitee={1}", inviter, invitee);
                    return;
                }
            }

            session.Members.Add(invitee);
            session.LastActiveUnix = Util.UnixTimeSinceEpoch();
        }

        if (m_debug)
            Log.InfoFormat("[FRIEND CONFERENCE]: Invite accepted session={0} inviter={1} invitee={2}", session.SessionId, inviter, invitee);

        GridInstantMessage invite = new()
        {
            fromAgentID = inviter.Guid,
            toAgentID = invitee.Guid,
            dialog = (byte)InstantMessageDialog.SessionAdd,
            fromGroup = false,
            imSessionID = session.SessionId.Guid,
            fromAgentName = m_systemName,
            message = string.IsNullOrWhiteSpace(message) ? m_defaultConferenceText : message,
            offline = 0,
            timestamp = (uint)Util.UnixTimeSinceEpoch(),
            binaryBucket = invitee.GetBytes()
        };

        SendMessage(scene, invite);

        IClientAPI local = GetActiveClient(invitee);
        if (local != null)
            SendChatterboxInvitation(local, session, inviter, invite.message);
    }

    private void SendSessionStartReply(IClientAPI caller, SessionInfo session)
    {
        if (caller?.Scene == null)
            return;

        OSDMap moderatedMap = new() { ["voice"] = OSD.FromBoolean(false) };
        OSDMap sessionMap = new()
        {
            ["moderated_mode"] = moderatedMap,
            ["session_name"] = OSD.FromString(session.Name),
            ["type"] = OSD.FromInteger(0),
            ["voice_enabled"] = OSD.FromBoolean(false)
        };

        OSDMap bodyMap = new()
        {
            ["session_id"] = OSD.FromUUID(session.SessionId),
            ["temp_session_id"] = OSD.FromUUID(session.SessionId),
            ["success"] = OSD.FromBoolean(true),
            ["session_info"] = sessionMap
        };

        IEventQueue queue = caller.Scene.RequestModuleInterface<IEventQueue>();
        if (queue == null)
            return;

        queue.Enqueue(queue.BuildEvent("ChatterBoxSessionStartReply", bodyMap), caller.AgentId);

        // Keep viewer session state consistent (same pattern used by GroupsMessagingModule)
        var updates = new List<GroupChatListAgentUpdateData>();
        lock (m_sync)
        {
            foreach (UUID member in session.Members)
                updates.Add(new GroupChatListAgentUpdateData(member));
        }
        if (updates.Count == 0)
            updates.Add(new GroupChatListAgentUpdateData(caller.AgentId));

        queue.ChatterBoxSessionAgentListUpdates(session.SessionId, caller.AgentId, updates);

        if (m_debug)
            Log.InfoFormat("[FRIEND CONFERENCE]: Sent session start reply for {0} to {1}", session.SessionId, caller.AgentId);
    }

    private void SendChatterboxInvitation(IClientAPI target, SessionInfo session, UUID inviter, string message)
    {
        IEventQueue eq = target.Scene.RequestModuleInterface<IEventQueue>();
        if (eq == null)
            return;

        eq.ChatterboxInvitation(
            session.SessionId,
            session.Name,
            inviter,
            message,
            target.AgentId,
            m_systemName,
            (byte)InstantMessageDialog.SessionAdd,
            (uint)Util.UnixTimeSinceEpoch(),
            false,
            0,
            Vector3.Zero,
            1,
            session.SessionId,
            false,
            Utils.StringToBytes(session.Name));
    }

    private SessionInfo EnsureSessionForIncomingInvite(GridInstantMessage im, UUID inviter, UUID invitee)
    {
        UUID sessionId = new(im.imSessionID);
        if (sessionId.IsZero())
            sessionId = UUID.Random();

        lock (m_sync)
        {
            if (!m_sessions.TryGetValue(sessionId, out SessionInfo session))
            {
                session = new SessionInfo
                {
                    SessionId = sessionId,
                    Owner = inviter.IsZero() ? invitee : inviter,
                    LastActiveUnix = Util.UnixTimeSinceEpoch(),
                    Name = string.IsNullOrWhiteSpace(im.message) ? m_defaultConferenceText : im.message.Trim()
                };
                m_sessions[sessionId] = session;
            }

            if (!inviter.IsZero())
                session.Members.Add(inviter);
            if (!invitee.IsZero())
                session.Members.Add(invitee);

            session.LastActiveUnix = Util.UnixTimeSinceEpoch();
            return session;
        }
    }

    private static GridInstantMessage CloneForTarget(GridInstantMessage src, UUID to)
    {
        return new GridInstantMessage
        {
            fromAgentID = src.fromAgentID,
            fromAgentName = src.fromAgentName,
            toAgentID = to.Guid,
            dialog = src.dialog,
            fromGroup = src.fromGroup,
            imSessionID = src.imSessionID,
            message = src.message,
            binaryBucket = src.binaryBucket,
            ParentEstateID = src.ParentEstateID,
            Position = src.Position,
            RegionID = src.RegionID,
            timestamp = src.timestamp == 0 ? (uint)Util.UnixTimeSinceEpoch() : src.timestamp,
            offline = 0
        };
    }

    private void SendMessage(Scene scene, GridInstantMessage im)
    {
        IMessageTransferModule transfer = scene?.RequestModuleInterface<IMessageTransferModule>();
        if (transfer == null)
            return;

        transfer.SendInstantMessage(im, _ => { });
    }

    private UUID[] ExtractInviteTargets(GridInstantMessage im)
    {
        HashSet<UUID> targets = new();

        UUID direct = new(im.toAgentID);
        if (!direct.IsZero())
            targets.Add(direct);

        // Some viewers may place session name/text in binaryBucket for session start.
        // Only parse as UUID list when payload looks like pure UUID packing.
        if (im.binaryBucket != null && im.binaryBucket.Length >= 16 && (im.binaryBucket.Length % 16) == 0 && im.binaryBucket.Length <= 16 * 64)
        {
            for (int i = 0; i + 16 <= im.binaryBucket.Length; i += 16)
            {
                UUID id = new(im.binaryBucket, i);
                if (!id.IsZero())
                    targets.Add(id);
            }
        }

        return targets.ToArray();
    }

    private IClientAPI GetActiveClient(UUID agentId)
    {
        if (agentId.IsZero())
            return null;

        List<Scene> scenes;
        lock (m_sync)
            scenes = m_scenes.ToList();

        IClientAPI child = null;
        foreach (Scene scene in scenes)
        {
            ScenePresence sp = scene.GetScenePresence(agentId);
            if (sp == null || sp.IsDeleted)
                continue;

            if (!sp.IsChildAgent)
                return sp.ControllingClient;

            child ??= sp.ControllingClient;
        }

        return child;
    }

    private void EnsureCleanupTimer()
    {
        lock (m_sync)
        {
            if (m_cleanupTimer != null)
                return;

            m_cleanupTimer = new Timer(_ => CleanupSessions(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
        }
    }

    private void StopCleanupTimer()
    {
        lock (m_sync)
        {
            Timer t = m_cleanupTimer;
            m_cleanupTimer = null;
            t?.Dispose();
        }
    }

    private void CleanupSessions()
    {
        long cutoff = Util.UnixTimeSinceEpoch() - m_sessionIdleSeconds;
        int removed = 0;

        lock (m_sync)
        {
            UUID[] expired = m_sessions.Values
                .Where(s => s.LastActiveUnix < cutoff || s.Members.Count == 0)
                .Select(s => s.SessionId)
                .ToArray();

            foreach (UUID sid in expired)
            {
                if (m_sessions.Remove(sid))
                    removed++;
            }
        }

        if (removed > 0 && m_debug)
            Log.InfoFormat("[FRIEND CONFERENCE]: cleaned {0} idle session(s)", removed);
    }
}