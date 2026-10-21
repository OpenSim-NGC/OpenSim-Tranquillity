# WebRTC voice: configuration reference

Every key read by the `os-webrtc-janus` addon, with the default the code uses when the key is absent.
The shipped file is `Addons/os-webrtc-janus/os-webrtc-janus.ini`; copy it into the `config` folder
(or whatever `[Startup] inidirectory` names) of the region server. For an overview, a minimal set up
and the feature descriptions, see [overview.md](overview.md). Deployment choices are in
[deployment.md](deployment.md).

Conventions used below:

- "Slvoice mixer" means a Janus plugin that implements the `janus.plugin.slvoice` protocol (see the README).
  The stock `janus.plugin.audiobridge` plugin does not understand the features marked this way. When the plugin
  is the stock one, each switch marked "slvoice mixer" that is set to `true` logs a warning at start naming the
  switch and the plugin, and is treated as `false`.
- Booleans accept `true` / `false`.
- Keys that are commented out in the shipped file show their default and behave as if absent.
- Secrets are never logged. Do not commit real tokens or secrets to a repository.
- A value outside its usable range is replaced by the default (or the nearest usable value) where the table says
  so; the code does not stop at start-up for these.

## Where each section is read

| Section | Read by |
|---|---|
| `[WebRtcVoice]` | The region server (all feature switches), and Robust when it hosts the grid voice service (`Enabled`, `MessageDetails`, `SpatialVoiceService`, `NonSpatialVoiceService`, `LocalServiceModule`, `AllowNpcVoice`, `RefusalCacheSeconds`). |
| `[JanusWebRtcVoice]` | Whichever process talks to Janus: the region server for a region-local Janus, Robust for the grid service. The region server also reads `PluginName`, `JanusGatewayAdminURI`, `AdminAPIToken`, `AdminTimeoutMs` and `JanusGatewayCACertificateFile` from its own copy of this section, even when other voice traffic goes through Robust. |
| `[WebRtcVoiceSessionService]` | Robust (the grid server), only when `VoiceSessionConnector` is in its `[ServiceList]`. See [the call session service](#webrtcvoicesessionservice-grid-server). |
| `[Network]` | `AuthType`, `HttpAuthUsername` and `HttpAuthPassword` for the call session service, on both sides; the voice sections override it. |
| `[VoiceConnector.<name>]` | The region server, only when `ConnectorsEnabled = true`. |

## Settings by pull request

The voice code is contributed as a series of pull requests (see [overview.md](overview.md)). A key that your tree
does not read is simply ignored, so it is safe to leave a key from a later pull request in the file. The defaults here
are the ones in the pull requests' texts and the shipped ini.

| Pull request | Keys it adds, with their defaults |
|---|---|
| #347 | `[JanusWebRtcVoice] PluginName` (`janus.plugin.audiobridge`); `[WebRtcVoice] StunServers` (empty) |
| #348 | `[JanusWebRtcVoice] RequestTimeoutMs` (5000), `RemoveEmptyRooms` (`true`), `EmptyRoomGraceSeconds` (60); `[WebRtcVoice] RefusalCacheSeconds` (5) |
| #349 | `[JanusWebRtcVoice] JanusGatewayCACertificateFile` (unset) |
| #353 | `A2AVoiceEnabled` (`false`) |
| #354 | `GroupVoiceEnabled` (`false`), `GroupVoiceRequireVoicePower` (`true`), `GroupVoiceCap` (50), `CarrierSecret` (unset), `MaxNonSpatialSessions` (5000), `MaxSessionsPerAgent` (20) |
| #355 | `AdhocVoiceEnabled` (`false`), `ConferenceRequestBurst` (5), `ConferenceRequestRefillSeconds` (10) |
| #356 | `SessionStoreMode` (`local`), `SessionStoreServerURI` (empty), `AuthType`, `HttpAuthUsername`, `HttpAuthPassword`, `SessionStoreTimeoutMs` (3000), `SessionSeatRenewSeconds` (60), `SessionStoreOutboxMax` (256), `SessionStoreOutboxSeconds` (60), `SessionStoreBreakerFailures` (5), `SessionStoreBreakerWindowSeconds` (10), `SessionStoreBreakerOpenSeconds` (5); on the grid server the `[WebRtcVoiceSessionService]` keys and `VoiceSessionConnector` |
| #357 | `CallModerationEnabled` (`false`), `CallModerationRevalidateSeconds` (60), `CallRemovalMinutes` (0) |
| #362 | `VisibilityFeederEnabled` (`false`), `VisibilityEmitEnabled` (`false`), `VisibilityTickMs` (250), `VisibilityRoomSendConcurrency` (4), `VisibilityArmingEnabled` (`false`), `VisibilityHeartbeatMs` (1000), `VoiceModerationEnabled` (`false`); `[JanusWebRtcVoice] AdminTimeoutMs` (5000) |
| #363 | `JoinCapabilityEnabled` (`false`); `[JanusWebRtcVoice] JoinCapabilitySecret` (empty) |
| #365 | `ConnectorsEnabled` (`false`), `AllowNpcVoice` (`false`), `NpcNameToken` (`NPC`), `VoiceRangeMetres` (20), and the `[VoiceConnector.<name>]` records |
| #364 | `PositionFeedEnabled` (`false`), `PositionFeedMode` (`observe`), `PositionFeedTickMs` (500), `PositionFeedKeepAliveMs` (2000), `PositionFeedMoveThresholdCm` (100), `PositionFeedMarginCm` (500), `PositionFeedSpeedFloorCmS` (800), `PositionFeedSpeedMaxCmS` (10000) |

#345, #346, #350, #351, #352, #358 add no setting. Mixer-only keys also need a slvoice mixer of release 1.2.0 or later;
see [overview.md](overview.md#the-slvoice-mixer).

## [WebRtcVoice]

### Core

| Key | Default | Meaning | Needs |
|---|---|---|---|
| `Enabled` | `false` | Master switch. With it off the addon does nothing. | - |
| `SpatialVoiceService` | empty | `dll:class` of the service handling regional ("local") voice. `WebRtcJanusService.dll:WebRtcJanusService` talks to a Janus gateway directly; `WebRtcVoice.dll:WebRtcVoiceServiceConnector` forwards to a grid service. The shipped file sets the Janus one. | At least one of the two service keys must be set, or voice disables itself. |
| `NonSpatialVoiceService` | the value of `SpatialVoiceService` | Same, for non-spatial voice (groups, calls, conferences). If both keys name the same class, one service instance serves both roles. | - |
| `WebRtcVoiceServerURI` | empty | URL of the grid voice service. Used by `WebRtcVoiceServiceConnector`. | The grid service connector configured on Robust. |
| `LocalServiceModule` | `WebRtcVoiceServiceModule.dll:WebRtcVoiceServiceModule` | Robust side only (`WebRtcVoiceServerConnector`): the service that handles the requests Robust receives. Normally left alone. | - |
| `MessageDetails` | `false` | Write every message sent to and received from the viewer to the log. Very verbose; for debugging. | - |

### Provisioning and viewer-facing settings

| Key | Default | Meaning | Needs |
|---|---|---|---|
| `StunServers` | empty | Comma-separated full ICE URIs (scheme included, no quotes), e.g. `stun:stun.l.google.com:19302`. Stock viewers need one to connect from outside the grid's network. Advertised to viewers as the `stun-servers` simulator feature. Empty: the addon adds nothing. If the core server's own `StunServers` setting (in `[Const]`, `[Startup]`, `[GridInfo]` or `[SimulatorFeatures]`) is also set and holds at least one valid host, the core server's value is the one viewers receive, because it is written after the addon's. | Works with either plugin. |
| `RefusalCacheSeconds` | `5` | After a voice provision is refused or fails, the same answer is replayed to that viewer's immediate retries for this many seconds without repeating the estate and parcel checks or calling Janus. `0` (or a negative value) turns the cache off. | - |
| `AllowNpcVoice` | `false` | Allow any NPC presence to obtain voice. When `false`, an NPC is refused unless it is a registered connector identity (see `ConnectorsEnabled`). Real avatars are never affected. | - |

### Avatar-to-avatar calls, group voice, conferences

| Key | Default | Meaning | Needs |
|---|---|---|---|
| `A2AVoiceEnabled` | `false` | Handle the viewer's "call" and "start p2p voice" chat session requests. Off: the chat session handler answers as it does without the feature. | Works with either plugin. |
| `GroupVoiceEnabled` | `false` | Group voice sessions. Membership and powers are looked up through the region's Groups module. | Groups module. Works with either plugin. |
| `GroupVoiceRequireVoicePower` | `true` | Require the group role power `GP_SESSION_VOICE` as well as `GP_SESSION_JOIN`. `false` requires join only. | `GroupVoiceEnabled`. |
| `GroupVoiceCap` | `50` | Largest number of seats in one group session. Values above 110 are lowered to 110 (the largest room a slvoice mixer can be set to hold, kept for both plugins); values below 2 are raised to 2; 0 or negative means 50. The default matches the mixer's default room size (`JS_ROOM_SIZE`, 50): a mixer refuses a seat beyond its room size as a full room, which the viewer is told as HTTP 409. | `GroupVoiceEnabled`. |
| `AdhocVoiceEnabled` | `false` | Ad-hoc voice and conference text ("start conference"). Off: "start conference" gets an empty 200 reply, as without the feature. | Groups module for the conference text session. Works with either plugin. |

### Call sessions across region server processes

A group call, a conference or an avatar-to-avatar call is a session. By default (`SessionStoreMode = local`) each
region server process keeps its own sessions. See [deployment.md](deployment.md) for what that means and how to
choose.

| Key | Default | Meaning | Needs |
|---|---|---|---|
| `CarrierSecret` | unset | Signs the messages that carry group, conference and avatar-to-avatar rings, the answers of an avatar-to-avatar call (the callee joining, declining or hanging up), and conference text lines, between region servers running in different processes. Unset: nothing is sent across processes and every such message that arrives is refused (calls between regions of one process are unaffected). A value under 32 characters is refused at startup and leaves them off. A grid that runs regions in more than one process and keeps `SessionStoreMode = local` must set the same value on every region server for group, conference and avatar-to-avatar calls to ring across them. Messages older than 60 seconds (clock difference allowed: 30 seconds), repeated, unsigned or wrongly signed are refused. With `SessionStoreMode = grid` it is not needed: the receiving region asks the session service whether a ring, or the answer of an avatar-to-avatar call, is genuine; a region that sets it anyway also signs them and requires the signature. Separate from the join-capability secret. | `A2AVoiceEnabled`, `GroupVoiceEnabled` or `AdhocVoiceEnabled`. |
| `MaxNonSpatialSessions` | `5000` | Most sessions of all kinds one region server process holds at once. A start beyond it is refused. A value below 1 reads as the default. Used only in `local` mode; in `grid` mode the service's `MaxSessions` applies. | `local` mode. |
| `MaxSessionsPerAgent` | `20` | Most live sessions one agent may have started. A start beyond it is refused. A value below 1 reads as the default. `local` mode only. | `local` mode. |
| `ConferenceRequestBurst` | `5` | Requests one agent may make back to back to "start conference" and "invite" (one shared allowance). A value below 1 reads as the default. `local` mode only. | `local` mode. |
| `ConferenceRequestRefillSeconds` | `10` | After a burst, one more request is allowed every this many seconds. A value of 0 or below reads as the default. `local` mode only. | `local` mode. |
| `SessionStoreMode` | `local` | `local`: each region server process keeps its own sessions. `grid`: sessions are kept by the call session service on Robust, so every process shares one record of each call. Any other value logs an error and keeps `local`. Set it the same on every region server. | `grid`: the service configured on Robust. |
| `SessionStoreServerURI` | empty | Required with `grid`: the base address of the Robust server that hosts the service, for example `http://robust.example.org:8003` (the private port). With `grid` and no address, an error is logged and `local` is kept. | `grid`. |
| `AuthType`, `HttpAuthUsername`, `HttpAuthPassword` | `None`, empty, empty | Required with `grid`: the service accepts only HTTP basic authentication. Set them here (or in `[Network]`; this section wins) to the values of `[WebRtcVoiceSessionService]` on Robust. The password must be at least 32 characters. Without them the region logs an error and its calls are refused. Keep the password out of version control. | `grid`. |
| `SessionStoreTimeoutMs` | `3000` | Milliseconds to wait for the service before a request is refused. A value under 100 reads as the default. | `grid`. |
| `SessionSeatRenewSeconds` | `60` | Seconds between a region telling the service which seats it still holds. The service releases a seat that has not been renewed for its `SeatLeaseSeconds` (180), so keep this at a third of that or less. A value under 5 reads as the default. | `grid`. |
| `SessionStoreOutboxMax` | `256` | Departures (a viewer leaving a call) kept while the service cannot be reached, and sent when it answers again. A value below 1 reads as the default. | `grid`. |
| `SessionStoreOutboxSeconds` | `60` | Seconds a kept departure is held before it is given up. A given-up seat is returned by the seat lease. A value below 1 reads as the default. | `grid`. |
| `SessionStoreBreakerFailures` | `5` | Calls with no answer within `SessionStoreBreakerWindowSeconds` that open the circuit breaker. A value below 1 reads as the default. | `grid`. |
| `SessionStoreBreakerWindowSeconds` | `10` | Window for the failures above. | `grid`. |
| `SessionStoreBreakerOpenSeconds` | `5` | Seconds the breaker then refuses calls at once, without waiting out the timeout; then one call is tried. | `grid`. |

### Visibility (who may hear whom)

| Key | Default | Meaning | Needs |
|---|---|---|---|
| `VisibilityFeederEnabled` | `false` | Run the per-region visibility feeder: it works out who may hear whom (estate bans, parcel bans, "see avatars" hiding, voice moderation). With emission off it runs matrix-only (diagnostics). Also registers the console command `show voice visibility`. | Slvoice mixer to have any effect. |
| `VisibilityEmitEnabled` | `false` | Send the computed result to the mixer as `peer_ctl` batches over the Janus admin API. Only takes effect with the feeder on. If the admin URI or token is missing, the region logs a warning and runs matrix-only. | Feeder; `[JanusWebRtcVoice]` `JanusGatewayAdminURI` and `AdminAPIToken`; slvoice mixer. |
| `VisibilityTickMs` | `250` | Milliseconds between visibility recomputations. | Feeder. |
| `VisibilityRoomSendConcurrency` | `4` | Number of rooms addressed concurrently within one batch send. A room whose request would be over the mixer's limit of 65,536 bytes is sent in several parts, one after another. | Emission. |
| `VisibilityArmingEnabled` | `false` | Create spatial rooms declaring the sim as their visibility authority (with epochs and heartbeats). A mixer started with `JS_VIS_FAIL_CLOSED=1` then withholds audio from a room whose authority goes silent; otherwise it counts what it would do. Takes effect only when the feeder and emission are also on. | Slvoice mixer; feeder; emission. |
| `VisibilityHeartbeatMs` | `1000` | Milliseconds between heartbeats to the mixer, 250 to 4000; anything else logs a warning and uses 1000. The mixer's staleness window (`JS_VIS_STALE_MS`, 8000 by default) bounds it, so the ceiling is half of that. A region server with many regions sends one heartbeat for each region each interval and can lengthen it. The heartbeat tells the mixer the interval to expect. | `VisibilityArmingEnabled`. |
| `PositionFeedEnabled` | `false` | Send the mixer each voice participant's position as the region sees it (`peer_pos`, see the region server's view of the protocol in [mixer-feed-protocol.md](mixer-feed-protocol.md), section 6.6), so a viewer's own claim can be compared with it. Nothing is computed or sent while this is off, and a mixer that does not advertise `vis_protocol` 3 is sent nothing. With the stock plugin it logs one warning at start and is turned off. See [positions.md](positions.md). | Slvoice mixer 1.2.0 or later; feeder; emission. |
| `PositionFeedMode` | `observe` | `observe` or `enforce`. Enforcement is not available yet: `enforce` is accepted and the positions are sent as `observe`, so the mixer counts what a correction would change and changes nothing. Any other value logs a warning and uses `observe`. | `PositionFeedEnabled`. |
| `PositionFeedTickMs` | `500` | Milliseconds between position evaluations. Minimum 100. The region's feeder thread runs every `VisibilityTickMs`, so the gap between looks is 500 to 750 ms with the defaults. | `PositionFeedEnabled`. |
| `PositionFeedKeepAliveMs` | `2000` | A room's full set of positions is sent at the first look at or after this age. 1 to 2250; a value outside that logs a warning and uses 2000. The mixer treats a point as stale 6000 ms after it received it, and 2250 is the largest value for which one lost request still leaves a standing participant fresh. | `PositionFeedEnabled`. |
| `PositionFeedMoveThresholdCm` | `100` | Centimetres a participant must move from the last point sent before it is sent again between full sets. Minimum 1. | `PositionFeedEnabled`. |
| `PositionFeedMarginCm` | `500` | Centimetres of difference between the viewer's claimed position and the region's that the mixer allows at the moment of a sample. Minimum 0. | `PositionFeedEnabled`. |
| `PositionFeedSpeedFloorCmS` | `800` | The smallest speed allowance sent, in cm/s, so a standing avatar that starts to run is not flagged. Minimum 0. | `PositionFeedEnabled`. |
| `PositionFeedSpeedMaxCmS` | `10000` | Speed ceiling in cm/s for a physics engine that declares no maximum speed of its own (not below the floor). An engine that does declare one (`PhysicsScene.MaximumLinearVelocity`, #351) uses that instead. | `PositionFeedEnabled`. |

### Join capability and moderation

| Key | Default | Meaning | Needs |
|---|---|---|---|
| `JoinCapabilityEnabled` | `false` | The region signs a short-lived (60 seconds) join capability for each avatar join and sends it to the mixer. If the secret is empty or shorter than 32 characters, an error is logged and the feature stays off. | Slvoice mixer; `[JanusWebRtcVoice]` `JoinCapabilitySecret`, equal to the mixer's secret. |
| `VoiceModerationEnabled` | `false` | Advertise the `SpatialVoiceModerationRequest` capability to viewers so a parcel's owner, an estate manager or a group moderator can mute avatars on the parcel. With the slvoice mixer the mute is carried by the visibility feeder. With the stock plugin it is the plugin's own mute of that participant. The console commands `show voice moderation`, `voice moderation mute` and `voice moderation unmute` are registered whenever the key is on; they work on the feeder's state, so they find nothing with the stock plugin. See [moderation.md](moderation.md). | Stock plugin: nothing more. Slvoice mixer: `VisibilityFeederEnabled`; without it a request is answered 501 and nothing is recorded. |
| `CallModerationEnabled` | `false` | Moderation of group calls from the viewer's participant list, and the console commands that moderate group calls and conferences. Off: the viewer's moderation requests are answered as they were without the feature. A voice mute is carried out by the stock plugin's own mute of the participant and, from #362 on, by a slvoice mixer's mute channel. | `GroupVoiceEnabled` or `AdhocVoiceEnabled`; without one, a warning is logged and it stays off. |
| `CallModerationRevalidateSeconds` | `60` | Seconds between checks that the moderator flags in a call's participant list, and the powers of group members seated in the call, are still right. `0` = never. A negative value reads as 60. | `CallModerationEnabled`. |
| `CallRemovalMinutes` | `0` | How long a removal lasts. `0`: until the call ends. Above 0: that many minutes (at most 525600), and the call's record is kept that long. | `CallModerationEnabled`. |

`CallModerationEnabled` is separate from `VoiceModerationEnabled`, which moderates the voice of a parcel.

### NPC voice connectors

| Key | Default | Meaning | Needs |
|---|---|---|---|
| `ConnectorsEnabled` | `false` | Load the `[VoiceConnector.<name>]` records and start their NPCs. Off: no record is read. | Slvoice mixer; the NPC module. |
| `NpcNameToken` | `NPC` | A word that the NPC's first or last name must carry as a whole word (an exact, case-sensitive match of one word of the name). A record whose NPC name lacks it is refused. | `ConnectorsEnabled`. |
| `VoiceRangeMetres` | `20` | Range, in metres, used for the notice that tells nearby avatars a voiced NPC is close. | `ConnectorsEnabled`. |

`AllowNpcVoice` is read once, by the voice service module, and works the same on both plugins; the connector module does not read it. A registered connector is admitted whether it is on or off.

## [JanusWebRtcVoice]

Required for any process that talks to Janus. If `JanusGatewayURI`, `APIToken`, `JanusGatewayAdminURI`
or `AdminAPIToken` is empty the Janus service logs an error and disables itself; it also disables itself
if it cannot connect to Janus at startup.

| Key | Default | Meaning | Needs |
|---|---|---|---|
| `JanusGatewayURI` | empty (required) | URL of the Janus REST API, e.g. `http://janus.example.org:14223/voice`. | A running Janus gateway. |
| `APIToken` | empty (required) | API secret for the Janus REST API. Must equal the gateway's. Use a random value of at least 32 characters; a shorter one is logged as a warning at startup. | - |
| `JanusGatewayAdminURI` | empty (required) | URL of the Janus admin API. Also used for visibility emission. | - |
| `AdminAPIToken` | empty (required) | Admin API secret. Must equal the gateway's. Use a random value of at least 32 characters; a shorter one is logged as a warning at startup. | - |
| `MessageDetails` | `false` | Write every message sent to and received from Janus to the log. Very verbose. The API and admin secrets are hidden in what is logged. | - |
| `PluginName` | `janus.plugin.audiobridge` | Janus plugin the region attaches voice handles to. `janus.plugin.slvoice` selects a spatial mixer implementing that protocol. Any name other than the stock one is treated as such a mixer. The region server reads this from its own copy of the section even when Janus runs elsewhere, so set it there too. | The named plugin installed in Janus. |
| `RequestTimeoutMs` | `5000` | Milliseconds an "ack"-answered Janus request waits for its result before failing. Zero or negative falls back to 5000. | - |
| `AdminTimeoutMs` | `5000` | Milliseconds a Janus admin API request waits for its answer (used by visibility emission). | - |
| `RemoveEmptyRooms` | `true` | Remove a room this service created once nobody has been in it for `EmptyRoomGraceSeconds`. The stock AudioBridge never removes an emptied room. Rooms the service did not create are never removed. Does nothing with the slvoice mixer, which removes empty rooms itself. | Stock plugin. |
| `EmptyRoomGraceSeconds` | `60` | Seconds a room stands empty before it is removed. A negative value reads as 60. | `RemoveEmptyRooms`. |
| `JanusGatewayCACertificateFile` | empty | Path of a private CA certificate (PEM or DER; a PEM file may hold several) trusted alongside the operating system's roots, for the `https://` gateway and admin URLs. No setting turns validation off. See [Securing the connection to the mixer](#securing-the-connection-to-the-mixer). | A gateway served over https with a certificate from that CA. |
| `JoinCapabilitySecret` | empty | Secret used to sign join capabilities, for avatars and for connectors. Never logged. Must match the mixer. At least 32 characters: with `JoinCapabilityEnabled` on, a shorter one is refused at startup and no capability is minted. | Slvoice mixer. |

### Securing the connection to the mixer

Every request to the gateway carries the API secret or the admin secret. Over plain `http://` those cross the
network in clear, so anyone on the path can read them and take over the gateway. A gateway on another host therefore
needs TLS, or a network that only you control (a private LAN or a VPN). A gateway on the same host, reached through
the loopback address, needs neither.

To use TLS, serve the gateway's REST and admin APIs over https (the gateway needs a certificate and its key), then
point the region server, and Robust if it talks to Janus, at the https URLs:

```ini
[JanusWebRtcVoice]
    JanusGatewayURI = https://mixer.example.org:8089/voice
    JanusGatewayAdminURI = https://mixer.example.org:8090/admin
```

The certificate is checked like any other HTTPS connection: it must chain to a root the operating system trusts and
must name the host in the URL. No setting turns the check off. A certificate that fails it is refused, nothing is
sent to that server, and the log says why (`[JANUS TLS] the certificate presented by ... was rejected`).

If the gateway's certificate comes from a CA the operating system does not know (your own CA, or a self-signed
certificate), set `JanusGatewayCACertificateFile` to the file holding that CA's certificate, or the self-signed
certificate itself:

```ini
[JanusWebRtcVoice]
    JanusGatewayCACertificateFile = /etc/opensim/mixer-ca.pem
```

That certificate is trusted alongside the system roots, only for the connection to the gateway. The host name
must still match, so a CA certificate does not excuse a wrong name. Leave the key unset when the certificate is
from a public CA. If the file is missing or unreadable, an error naming the key is logged and only the system roots
are used. Restart the region server after changing it.

Grid identity: room numbers for non-spatial sessions are derived from the grid's gatekeeper address, so two grids
sharing one gateway do not collide. It is read from `GatekeeperURI` (in `[Hypergrid]`, `[Startup]` or `[Const]`), else
`[GatekeeperService] ExternalName`, else `[GridService] Gatekeeper`. If none is set, a warning is logged and the
rooms are derived without a grid identity.

## [WebRtcVoiceSessionService] (grid server)

The shipped example is in `Source/OpenSim.Server.GridServer/AppData/GridServer.ini.example`. Nothing is served
unless `VoiceSessionConnector = "${Const|PrivatePort}/WebRtcVoice.dll:VoiceSessionServerConnector"` is in
`[ServiceList]` **and** `Enabled = true` here. Sessions are held in the grid server's memory and are gone when it
restarts.

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Nothing is served unless this is `true`. |
| `AuthType` | `None` | Must be `BasicHttpAuthentication`, here or in `[Network]` (this section wins). With anything else the service does not load: the connector throws an error naming the section and what is wrong. |
| `HttpAuthUsername` | empty | Required. |
| `HttpAuthPassword` | empty | Required, at least 32 characters. |
| `GridId` | read like a region reads it | The grid identity that call room keys are derived from. Left empty it is read from `GatekeeperURI` (`[Const]`, `[Startup]`, `[Hypergrid]`), else `[GatekeeperService] ExternalName`, else `[GridService] Gatekeeper`, normalised the same way as on a region, so the service and every region derive the same room key. |
| `MaxSessions` | `5000` | Most sessions of all kinds the service holds. A start beyond it is refused. Minimum 1. |
| `MaxSessionsPerAgent` | `20` | Most live sessions one agent may have started. Minimum 1. |
| `ConferenceRequestBurst` | `5` | Requests one agent may make back to back to "start conference" and "invite". Minimum 1. |
| `ConferenceRequestRefillSeconds` | `10` | Seconds for one more request to become available after a burst. |
| `InviteTtlSeconds` | `120` | How long an unanswered invitation lasts. Minimum 1. |
| `IdleTtlHours` | `8` | How long a call with nobody in it and nobody renewing it is kept. A call with a member that a region still renews is never idle. Minimum 1. |
| `SweepSeconds` | `30` | How often the service looks for expired invitations, idle calls and seats whose lease ran out. Minimum 1. |
| `SeatLeaseSeconds` | `180` | A seat is released when its region has not renewed it for this long. Regions renew every `SessionSeatRenewSeconds`; keep this at three times that or more. Minimum 10. |

## [VoiceConnector.\<name\>]

One section per connector, read only when `ConnectorsEnabled = true`. The section name after the dot is the
record name. Defaults apply when the key is missing; a record that breaks a rule below is refused (a warning
names the reason) and does not load at all.

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | The record is skipped unless `true`. |
| `NpcFirstName`, `NpcLastName` | empty | The NPC's name. Both are required, and one of the two must equal `NpcNameToken`. Full names must be unique across records. |
| `Position` | empty (required) | Region-local position of the NPC, `<x, y, z>`. An unparsable value refuses the record. |
| `Scope` | `estate` | Only `estate` is supported; anything else refuses the record. |
| `MayInject` | `false` | `false`: recording only. The NPC is muted at registration so nothing it sends is heard. This mute is enforced through the visibility feeder, so with the feeder off a `MayInject = false` connector is not started. `true` lets it speak. |
| `AuthorisedBy` | empty | Free text naming who authorised the record. Not validated. |
| `Region` | empty | Optional scene name. Set: the record starts only in that region. Absent: it starts in every region the instance hosts. |
| `CapabilitySecret` | empty | Optional. At least 32 characters; a shorter value is ignored with a warning. When set, the region serves `POST /voice/connector/<name>/join-cap`, which a connector program calls with `Authorization: Bearer <secret>` to obtain a join capability. Minting also needs `JoinCapabilityEnabled` and `JoinCapabilitySecret`. If the instance hosts more than one region, also set `Region`. Over a network, put the endpoint behind TLS or a private network. |

The connector NPC's agent id is derived from the grid identity, the region name and the record name, so it is the
same after a restart. Renaming the section or the region gives the connector a new id.
The id is a version 5 (name-based) UUID in the namespace derived from `urn:opensim:webrtc-voice:connector`.

Connector disclosure cannot be turned off: connector NPCs always carry the name token, and the region always
sends a region-wide alert when a connector attaches or detaches, an entry notice to each avatar that becomes a root
agent in a region with a connector, and a local-chat notice, spoken as the NPC, to an avatar that comes within
`VoiceRangeMetres` of it.
