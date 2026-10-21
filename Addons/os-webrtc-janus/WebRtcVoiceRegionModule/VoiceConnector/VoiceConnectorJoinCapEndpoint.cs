/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The connector join-capability endpoint.
 *
 * A connector peer (connectors/recorder, connectors/injector) joins the mixer from its own environment, so the
 * capability the sim mints for an avatar's join (JanusRoom.JoinRoom) never reaches it. Connector peers are
 * NOT exempted from JS_JOIN_CAP_REQUIRED: the viewer never reaches the Janus API, so they are the mixer's only
 * non-sim clients, which is exactly the party the capability checks. Instead the peer fetches one before every join:
 *
 *   POST /voice/connector/<name>/join-cap        Authorization: Bearer <[VoiceConnector.<name>] CapabilitySecret>
 *   200 {"display","room","session_id","join_cap","expires","position"} (position: {"x","y","z"} as
 *       global centimetres, integers - the viewer's own SLData frame)
 *
 * minted by the same minter as an avatar's join (JoinCapability.Mint), agent = the NPC id, session = the record's
 * ViewerSessionId, room = the record's recorded room, and (epoch, generation) from JoinCapabilityAuthority, the
 * source JanusRoom.JoinRoom uses for an avatar.
 *
 * No oracle: EVERY failure before authentication succeeds is 404 with an empty body (unknown name, inactive record,
 * no secret configured, wrong or missing bearer, wrong method or path). The handler is registered for the whole
 * "/voice" prefix precisely so an unknown name gets this handler's empty 404 and not the server's HTML one. Only
 * after authentication: 503 (empty) when the sim cannot mint. The bearer, the secrets and the capability are never
 * logged; one Debug line per mint names the record, the room and the expiry (a program fetches before every join, so an
 * Information line would be written for every fetch of every connector).
 *
 * Registered only while at least one attached record carries a CapabilitySecret; with none (the default) no handler
 * exists and nothing here runs.
 */

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework.Servers.HttpServer;

namespace osWebRtcVoice;

public sealed class VoiceConnectorJoinCapEndpoint
{
    /// <summary>The shortest CapabilitySecret enforced (never a weak key).</summary>
    public const int MinSecretLength = 32;
    /// <summary>The handler's registration key: the server matches var-path handlers on the first segment.</summary>
    public const string PathRoot = "/voice";
    public const string PathPrefix = "/voice/connector/";
    public const string PathSuffix = "/join-cap";

    /// <summary>One region's records plus the sim's mint settings ([WebRtcVoice] JoinCapabilityEnabled,
    /// [JanusWebRtcVoice] JoinCapabilitySecret).</summary>
    public sealed class Source
    {
        public Func<IEnumerable<VoiceConnectorRecord>> Records { get; }
        public bool MintEnabled { get; }
        public string MintSecret { get; }
        /// <summary>Re-resolve this record's room the way an avatar's provision would, make sure it
        /// EXISTS, and move the record if the parcel's channel changed. Returns the room, or null when it could not be
        /// ensured (then the recorded room is used as it stands). With no hook the record's room is used as it stands.</summary>
        public Func<VoiceConnectorRecord, int?> ResolveAndEnsureRoom { get; }
        /// <summary>This record's position in the viewer's SLData frame (global centimetres,
        /// integers), computed at every fetch; the grant carries it as "position". Null hook = no position field.</summary>
        public Func<VoiceConnectorRecord, (int X, int Y, int Z)?> PositionGlobalCm { get; }

        public Source(Func<IEnumerable<VoiceConnectorRecord>> pRecords, bool pMintEnabled, string pMintSecret,
            Func<VoiceConnectorRecord, int?> pResolveAndEnsureRoom = null,
            Func<VoiceConnectorRecord, (int X, int Y, int Z)?> pPositionGlobalCm = null)
        {
            Records = pRecords;
            MintEnabled = pMintEnabled;
            MintSecret = pMintSecret ?? string.Empty;
            ResolveAndEnsureRoom = pResolveAndEnsureRoom;
            PositionGlobalCm = pPositionGlobalCm;
        }
    }

    public readonly record struct Response(int Status, byte[] Body, string ContentType);

    private static readonly byte[] Empty = Array.Empty<byte>();
    private static readonly byte[] NoSecretHash = SHA256.HashData(Encoding.UTF8.GetBytes("no-secret-configured"));

    private readonly IHttpServer m_server;
    private readonly ILogger m_log;
    private readonly Func<long> m_nowUnix;
    private readonly object m_lock = new object();
    private readonly List<Source> m_sources = new List<Source>();
    private readonly HashSet<string> m_warnedAmbiguous = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> m_warnedUnpinned = new HashSet<string>(StringComparer.Ordinal);
    private bool m_registered;

    public VoiceConnectorJoinCapEndpoint(IHttpServer pServer, ILogger pLog, Func<long> pNowUnix = null)
    {
        m_server = pServer;
        m_log = pLog;
        m_nowUnix = pNowUnix ?? JoinCapability.NowUnix;
    }

    public bool IsRegistered { get { lock (m_lock) return m_registered; } }

    /// <summary>Attach one region's records. Registers the handler when the first record carrying a
    /// CapabilitySecret appears; a source whose records carry none changes nothing.</summary>
    public void Attach(Source pSource)
    {
        lock (m_lock)
        {
            if (!m_sources.Contains(pSource))
                m_sources.Add(pSource);
            UpdateRegistrationLocked();
            WarnUnpinnedLocked();
        }
    }

    /// <summary>A record with a CapabilitySecret and no Region= pin, in an instance running more than one
    /// region, starts in every region, so its endpoint can never choose one and answers 404 at request time. Say so at
    /// startup, once per record, naming it: every region's module attaches here from RegionLoaded, so the second attach
    /// is the moment an instance is known to run more than one region.</summary>
    private void WarnUnpinnedLocked()
    {
        if (m_sources.Count < 2)
            return;
        foreach (Source s in m_sources)
            foreach (VoiceConnectorRecord r in s.Records())
                if (r.CapabilitySecret is not null && r.Region is null && m_warnedUnpinned.Add(r.Name))
                    m_log?.LogWarning("[CONNECTOR] {Name}: CapabilitySecret is set but the record has no Region= pin, and this " +
                        "instance runs {Regions} regions: the connector starts in every one of them, so its join-capability " +
                        "endpoint cannot choose one and will answer 404. Add Region=<region name> to [VoiceConnector.{Name}]",
                        r.Name, m_sources.Count, r.Name);
    }

    public void Detach(Source pSource)
    {
        lock (m_lock)
        {
            m_sources.Remove(pSource);
            UpdateRegistrationLocked();
        }
    }

    private void UpdateRegistrationLocked()
    {
        bool wanted = m_sources.Any(s => s.Records().Any(r => r.CapabilitySecret is not null));
        if (wanted && !m_registered)
        {
            m_server.AddSimpleStreamHandler(new SimpleStreamHandler(PathRoot, Process), true);
            m_registered = true;
            m_log?.LogInformation("[CONNECTOR] join-capability endpoint served at POST {Prefix}<name>{Suffix} " +
                "for records with a CapabilitySecret", PathPrefix, PathSuffix);
        }
        else if (!wanted && m_registered)
        {
            m_server.RemoveSimpleStreamHandler(PathRoot);
            m_registered = false;
        }
    }

    private void Process(IOSHttpRequest pRequest, IOSHttpResponse pResponse)
    {
        pResponse.KeepAlive = false;
        Response r = Handle(pRequest.HttpMethod, pRequest.UriPath, pRequest.Headers?["Authorization"]);
        pResponse.StatusCode = r.Status;
        if (r.ContentType is not null)
            pResponse.ContentType = r.ContentType;
        pResponse.RawBuffer = r.Body;
    }

    /// <summary>The whole decision, transport-free. See the file header for the contract.</summary>
    public Response Handle(string pMethod, string pPath, string pAuthorization)
    {
        string name = ParseName(pPath);
        (VoiceConnectorRecord record, Source source) = name is null ? (null, null) : Resolve(name);

        // Always one constant-time comparison, whether or not a record was found, so the work done does not say which.
        byte[] expected = record?.CapabilitySecret is string secret ? SHA256.HashData(Encoding.UTF8.GetBytes(secret)) : NoSecretHash;
        byte[] given = SHA256.HashData(Encoding.UTF8.GetBytes(BearerOf(pAuthorization) ?? string.Empty));
        bool authenticated = CryptographicOperations.FixedTimeEquals(expected, given)
            && record is not null && string.Equals(pMethod, "POST", StringComparison.Ordinal)
            && BearerOf(pAuthorization) is not null;
        if (!authenticated)
            return NotFound;

        // Authenticated from here on.
        // The room is re-resolved and ensured to exist on EVERY fetch, before minting, so a
        // capability never names a room the mixer does not have and never names the estate room for a connector
        // standing on a parcel with its own channel. Idempotent: an existing room is reused.
        int? ensured = source.ResolveAndEnsureRoom?.Invoke(record);
        int room = ensured ?? record.Room.Value;
        (string epoch, uint generation) = JoinCapabilityAuthority.Resolve(room);
        long now = m_nowUnix();
        string capability = source.MintEnabled
            ? JoinCapability.Mint(source.MintSecret, record.NpcId.ToString(), record.ViewerSessionId, room, epoch, generation, now)
            : null;
        if (capability is null)
            return new Response((int)HttpStatusCode.ServiceUnavailable, Empty, null);

        long expires = now + JoinCapability.LifetimeSeconds;
        var body = new OSDMap
        {
            ["display"] = OSD.FromString(record.NpcId.ToString()),
            ["room"] = OSD.FromInteger(room),
            ["session_id"] = OSD.FromString(record.ViewerSessionId),
            ["join_cap"] = OSD.FromString(capability),
            ["expires"] = OSD.FromLong(expires),
        };
        // The connector's position, in the viewer's SLData frame (global cm, integers), re-computed
        // at every fetch. The peer sends it as its sp and lp once its data channel opens.
        (int X, int Y, int Z)? position = source.PositionGlobalCm?.Invoke(record);
        if (position.HasValue)
            body["position"] = new OSDMap
            {
                ["x"] = OSD.FromInteger(position.Value.X),
                ["y"] = OSD.FromInteger(position.Value.Y),
                ["z"] = OSD.FromInteger(position.Value.Z),
            };
        m_log?.LogDebug("[CONNECTOR] {Name}: join capability minted for room {Room}, expires {Expires}",
            record.Name, room, expires);
        return new Response((int)HttpStatusCode.OK, Encoding.UTF8.GetBytes(OSDParser.SerializeJsonString(body)), "application/json");
    }

    public static readonly Response NotFound = new Response((int)HttpStatusCode.NotFound, Empty, null);

    private static string ParseName(string pPath)
    {
        if (pPath is null || !pPath.StartsWith(PathPrefix, StringComparison.Ordinal) || !pPath.EndsWith(PathSuffix, StringComparison.Ordinal))
            return null;
        int len = pPath.Length - PathPrefix.Length - PathSuffix.Length;
        if (len <= 0)
            return null;
        string name = pPath.Substring(PathPrefix.Length, len);
        return name.Contains('/') ? null : name;
    }

    private static string BearerOf(string pAuthorization)
    {
        const string Scheme = "Bearer ";
        if (pAuthorization is null || !pAuthorization.StartsWith(Scheme, StringComparison.Ordinal))
            return null;
        string token = pAuthorization.Substring(Scheme.Length);
        return token.Length == 0 ? null : token;
    }

    /// <summary>The one ACTIVE record with this name and a secret, across every attached region. A record active in more
    /// than one region (no Region= pin) cannot be resolved: that is a 404 like any other, and the operator is told once
    /// in the log (never the client).</summary>
    private (VoiceConnectorRecord, Source) Resolve(string pName)
    {
        List<(VoiceConnectorRecord, Source)> hits = new List<(VoiceConnectorRecord, Source)>();
        lock (m_lock)
        {
            foreach (Source s in m_sources)
                foreach (VoiceConnectorRecord r in s.Records())
                    if (string.Equals(r.Name, pName, StringComparison.Ordinal) && r.CapabilitySecret is not null
                        && r.NpcId != UUID.Zero && r.ViewerSessionId is not null && r.Room.HasValue)
                        hits.Add((r, s));
            if (hits.Count > 1 && m_warnedAmbiguous.Add(pName))
                m_log?.LogWarning("[CONNECTOR] {Name}: active in {Count} regions, so its join-capability endpoint cannot " +
                    "choose one and answers 404; pin the record with Region=", pName, hits.Count);
        }
        return hits.Count == 1 ? hits[0] : (null, null);
    }
}
