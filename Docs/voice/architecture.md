# WebRTC Voice Architecture (os-webrtc-janus)

This describes how the `os-webrtc-janus` addon is put together: which modules exist, how a voice
request travels from the viewer to the mixer, how the addon is configured, and where a different
Janus plugin can be substituted. It is written for grid operators and for developers who maintain or
extend the addon. The code in `Addons/os-webrtc-janus/` is the authority; where this document and the
code disagree, the code is right.

The addon lives in five projects:

| Project | Role |
|---|---|
| `WebRtcVoiceRegionModule/` | Viewer-facing capabilities, the parcel and estate voice checks, avatar-to-avatar, group and conference signalling, the visibility feed, voice moderation, the position feed and voice connectors. |
| `WebRtcVoiceServiceModule/` | The dispatcher: routes each request to a spatial or non-spatial service and tracks viewer sessions. |
| `WebRtcVoice/` | Interfaces, the viewer-session registry, the region-to-Robust connector pair, the join capability, the refusal cache, and (since #356) the call session engine and the call session service that the grid server hosts. |
| `Janus/` | Janus REST client, plugin handles, room handling and the Janus admin client (#362). |
| `Visibility/` | The scene-independent part of the feed (#362): the visibility matrix and its rules, delta batches, the `peer_ctl` serializer, partitioner and splitter, the voice-state feeder, and (#364) the position types, tolerance, serializer and feeder. |

## 1. Modules and what they register

- **`WebRtcVoiceRegionModule`** (shared region module). When `[WebRtcVoice] Enabled = true` it
  advertises `VoiceServerType = webrtc` in the simulator features, adds `stun-servers` if
  `StunServers` is set, and registers these per-agent capabilities:
  - `ProvisionVoiceAccountRequest`: viewer sends a WebRTC offer and receives an answer.
  - `VoiceSignalingRequest`: trickled ICE candidates and the completion marker.
  - `ChatSessionRequest`: avatar-to-avatar, group and conference session signalling.
  - `SpatialVoiceModerationRequest`: only when `VoiceModerationEnabled = true`.
- **`WebRtcVoiceServiceModule`** (shared region module, also loadable on Robust). Registers itself as
  the scene's voice service and loads a spatial and a non-spatial *leaf* service from configuration.
- **Leaf services** are loaded by name from configuration, not as region modules:
  - `WebRtcJanusService` talks to a Janus gateway directly.
  - `WebRtcVoiceServiceConnector` forwards requests to a Robust grid service over JSON-RPC.
- **`WebRtcVoiceServerConnector`** is the Robust side of that JSON-RPC link.
- **`VoiceConnectorModule`** (non-shared region module) manages voice connector NPCs, only when
  `ConnectorsEnabled = true`.

## 2. Request flow

### 2.1 Provision

1. The viewer posts to its `ProvisionVoiceAccountRequest` capability.
2. The region module checks `voice_server_type` is `webrtc`, then decides admission by channel type:
   - `local` (spatial): estate voice setting, the parcel looked up from the viewer-supplied parcel
     id (never trusted on its own), the parcel voice flag, and parcel ban and access checks. When the
     estate uses a single estate channel, the parcel id is removed so everything resolves to the
     estate room; the ban and access check is still applied in that case.
   - `multiagent` (avatar-to-avatar, group, conference): admitted only if the corresponding feature is
     enabled and the request names a live session the agent is party to with matching credentials.
   - A logout body (no channel type) is routed to the service by viewer session and ends only the
     caller's own session.
   - Anything else is refused with 403. A refused provision is remembered for `RefusalCacheSeconds`
     (default 5) so a viewer's immediate retries are answered without repeating the checks.
3. The region module calls the dispatcher with the request.
4. The dispatcher looks up the viewer session (the request carries `viewer_session` after the first
   round trip) or creates one on the right leaf: the spatial leaf for `local`, the non-spatial leaf
   otherwise. It then calls the leaf with the session.
5. The leaf creates (or reuses) the room on the mixer, then forwards the viewer's SDP offer in a
   `join` and returns the SDP answer and the viewer session id.

The service contract has two forms of each call. A **dispatcher** implements the form without a
session; a **leaf** implements the form with an already-resolved session. The unimplemented half of
each class is intentionally unreachable and throws.

### 2.2 Topologies

- **Region-local Janus** (the default example configuration): the region's dispatcher loads
  `WebRtcJanusService` as its leaf and talks to Janus directly. Suitable for a standalone or
  single-region setup.
- **Grid service**: the region's dispatcher loads `WebRtcVoiceServiceConnector` as its leaf
  (`SpatialVoiceService = WebRtcVoice.dll:WebRtcVoiceServiceConnector`,
  `WebRtcVoiceServerURI = <Robust private URL>`). The connector sends `provision_voice_account_request`
  and `voice_signaling_request` JSON-RPC calls to Robust, where `WebRtcVoiceServerConnector`
  (added to Robust's `[ServiceList]` as `VoiceServiceConnector`) hands them to a second dispatcher
  that drives `WebRtcJanusService`. The viewer-session registry is a separate in-memory map in each
  process; the connector reconciles the ids when a reply arrives.

`[ServiceList] VoiceServiceConnector` is not present in the stock Robust example files and must be
added by hand; see the addon README. Since #352 the grid server project references `WebRtcVoice`,
`WebRtcVoiceServiceModule` and `WebRtcJanusService`, so the assemblies that `WebRtcVoiceServerConnector` loads for the
Janus service are part of a grid server built from this tree; nothing loads them unless `VoiceServiceConnector` is in the
service list.

### 2.3 Signalling

`VoiceSignalingRequest` relays trickled ICE candidates and the completion marker to Janus. The
viewer's SDP offer is passed to the mixer unmodified, including any data channel section; the region
server does not interpret, route or answer the viewer's data channel. Per-peer data (positions,
volume, join and leave notices) is the mixer's business.

## 3. Janus surface

All Janus messages are built as structured data maps in `JanusMessages.cs`.

- **Session level:** `create`, `destroy`, `attach` (with a plugin name), `detach`, `trickle`.
  Replies that arrive as `ack` wait on the long-poll event loop for their result; `[JanusWebRtcVoice]
  RequestTimeoutMs` (default 5000) bounds that wait.
- **Room level** (plugin request bodies): `create`, `destroy`, `join`, `leave`, `list` and
  `listparticipants` (the last two only for console commands).
  - `create` sends `is_private=false`, `permanent=false`, `sampling_rate=48000`, `spatial_audio`,
    `denoise=false`, `record=false` and an optional description. With the join capability on (#363) it also sends
    `sim_created=true`, and `vis_authority=true` for spatial rooms when authority arming is on (#362).
  - `join` carries `room`, `display` (the agent id), the offer, and a join capability when enabled.
  - Error 486 on create ("already exists") is treated as success. Each viewer provision sends a
    create before the join, so the mixer is the only record of whether a room exists.
- **Admin level:** `message_plugin` posts used for the visibility feed and the position feed. See
  [mixer-feed-protocol.md](mixer-feed-protocol.md).

Room numbers are positive integers from a deterministic hash (see the protocol document), so every
region and every process computes the same number for the same parcel or session. Creates of one room
number from one process are serialised so concurrent provisions produce one create and the rest
"already exists".

Long numeric Janus ids (session and handle ids exceed 32 bits) are handled as 64-bit values when read
from replies.

## 4. Non-spatial voice

All of these are off unless their switch is set, and the call logic does not depend on the plugin: it
works with the stock AudioBridge and with a spatial mixer implementing `janus.plugin.slvoice`.

- **Avatar-to-avatar** (`A2AVoiceEnabled`): the viewer's `start p2p voice` records an invitation
  (session id is the viewer's XOR of the two agent ids); the caller's `call` returns credentials and,
  once, delivers an invitation to the callee; each party provisions with the channel and credentials
  to enter a two-seat room. Invitations expire if unanswered and are held in memory on each region
  server instance. A callee whose viewer is in a region hosted by another instance is rung through a
  grid instant message. Under `SessionStoreMode = local` it is signed with `CarrierSecret`: that instance
  records the call with the same credentials and delivers the invitation, so the callee's accept is admitted
  there, and without a secret the callee is not rung. Under `grid` the receiving instance asks the session
  service whether the call exists and the target is its callee, and uses the service's credentials; nothing is
  signed or adopted. The callee joining, declining or hanging up reaches the caller's instance the same way,
  and under `grid` is acted on only when the service says it is true. Media is always relayed through the
  mixer, never directly between viewers.
- **Group voice** (`GroupVoiceEnabled`): requires the Groups module. Joining needs the group
  membership power, and speaking needs the voice power unless `GroupVoiceRequireVoicePower = false`.
  Seats are capped by `GroupVoiceCap` (default 50, never above 110, the largest room a slvoice mixer can be set to hold; it
  admits 50 by default). A capacity refusal is returned as HTTP 409, which the viewer shows as a full channel.
- **Conferences** (`AdhocVoiceEnabled`): answers `start conference` with a session reply and carries
  conference text between region servers as instant messages.

`fetch history` is answered with an empty array because no chat history is kept.

## 5. Visibility, moderation and join capabilities

These need a mixer implementing `janus.plugin.slvoice` and are off by default.

- **Visibility feed** (`VisibilityFeederEnabled`, `VisibilityEmitEnabled`): each region runs a feeder
  on a dedicated thread (`VisibilityTickMs`, default 250) that builds a per-listener matrix from
  estate bans, parcel bans and access lists, parcels that hide avatars, and moderation, and a sender
  that pushes changes to the mixer. With the feeder on and emission off it runs matrix-only. The
  message formats, rules and failure handling are in [mixer-feed-protocol.md](mixer-feed-protocol.md).
- **Authority arming** (`VisibilityArmingEnabled`): the region declares itself the authority for its
  rooms, with an epoch and generations, and heartbeats that state; a mixer in authority mode then
  silences what it has not been told to carry. See section 6 of the protocol document.
- **Join capability** (`JoinCapabilityEnabled`, `[JanusWebRtcVoice] JoinCapabilitySecret`, #363): the region
  signs a short-lived, single-use token for each join. The viewer never sees it. The secret must be at least 32 characters.
- **Moderation** (`VoiceModerationEnabled`): the `SpatialVoiceModerationRequest` capability accepts
  `mute`/`unmute` for an agent and `mute_all`/`unmute_all` for the moderator's current parcel. The
  moderator must be the parcel owner, an estate owner or manager, or a member of the owning group with
  the chat-moderation power; the check is made on the server. State is in memory and per parcel, and
  needs the visibility feeder to be enforced; without it the request is refused. Console commands:
  `show voice moderation`, `voice moderation mute`, `voice moderation unmute`.
- **Voice connectors** (`ConnectorsEnabled`): an operator-declared `[VoiceConnector.<name>]` record
  creates an NPC that represents an external recorder or injector in the voice session. The NPC's id
  is derived from the grid, region and record name, so it is stable across restarts. A record with
  `MayInject = false` is muted at registration (recording only). Disclosure notices (a region alert on
  attach and detach, an entry notice, and a proximity notice from a voiced NPC) are always sent and
  cannot be switched off. Console commands: `voice connector start <name>` and
  `voice connector stop <name>`.
- **Position feed** (`PositionFeedEnabled`, #364): a pump on the visibility feeder's thread reads where the scene has each
  voice participant and sends the mixer one `peer_pos` request for each occupied room. It sends nothing until the mixer has
  advertised `vis_protocol` 3, which it learns from the replies to `peer_ctl` requests or, when none is sent, from a heartbeat
  that names no room. The mixer only observes in this release. See [positions.md](positions.md).

## 6. Configuration

Every optional feature is off unless its switch in `[WebRtcVoice]` is set. The authoritative list
with defaults is `os-webrtc-janus.ini`; the keys are:

| Key | Default |
|---|---|
| `Enabled` | `false` |
| `SpatialVoiceService`, `NonSpatialVoiceService` | the example file selects the local Janus service; at least one must be set |
| `WebRtcVoiceServerURI` | none (grid-service topology only) |
| `MessageDetails` | `false` |
| `StunServers` | empty (nothing is advertised) |
| `A2AVoiceEnabled`, `GroupVoiceEnabled`, `AdhocVoiceEnabled` | `false` |
| `GroupVoiceRequireVoicePower` / `GroupVoiceCap` | `true` / `50` |
| `VisibilityFeederEnabled`, `VisibilityEmitEnabled`, `VisibilityArmingEnabled` | `false` |
| `VisibilityTickMs` / `VisibilityRoomSendConcurrency` | `250` / `4` |
| `CarrierSecret` | unset (cross-process messages off) |
| `SessionStoreMode` | `local` |
| `CallModerationEnabled` | `false` |
| `VisibilityHeartbeatMs` | `1000` |
| `PositionFeedEnabled` / `PositionFeedMode` | `false` / `observe` |
| `PositionFeedTickMs` / `PositionFeedKeepAliveMs` | `500` / `2000` |
| `JoinCapabilityEnabled`, `VoiceModerationEnabled`, `ConnectorsEnabled` | `false` |
| `AllowNpcVoice` | `false` |
| `NpcNameToken` / `VoiceRangeMetres` | `NPC` / `20` |
| `RefusalCacheSeconds` | `5` (0 disables) |

`[JanusWebRtcVoice]` holds the gateway (`JanusGatewayURI`, `APIToken`) and admin
(`JanusGatewayAdminURI`, `AdminAPIToken`) endpoints, all four of which must be set or the Janus
service disables itself. `PluginName` defaults to `janus.plugin.audiobridge`; set it to
`janus.plugin.slvoice` to use a spatial mixer. `RequestTimeoutMs` and `AdminTimeoutMs` default to
5000. `JoinCapabilitySecret` has no default (32 characters or more when the capability is on).

On Robust, `[WebRtcVoice]` may also set `LocalServiceModule` (default
`WebRtcVoiceServiceModule.dll:WebRtcVoiceServiceModule`).

Console commands outside the feature sections: `janus info`, `janus list rooms` (lists rooms the
plugin reports), `show voice visibility` (feed counters, when the feeder is on) and
`show voice closing` (voice sessions whose teardown is pending or has failed).

## 7. Substituting the Janus plugin

The plugin name is read from `[JanusWebRtcVoice] PluginName` and passed to the plugin handle; nothing
else names a plugin. A plugin that speaks the same room vocabulary (`create`, `join`, `leave`,
`destroy` with the same response codes) needs only that setting. A plugin with a different vocabulary
would change only: the request classes and response readers in `JanusMessages.cs`, the room
operations in `JanusAudioBridge.cs`, and join and leave in `JanusRoom.cs`. The region module, the
dispatcher, the Robust connector pair, and the Janus session and plugin-handle layers are unaffected.
The visibility feed always addresses `janus.plugin.slvoice`.

## 8. Known limitations

- The Janus leaf blocks on asynchronous calls when handling provision and signalling requests
  (methods carry a `BAD` suffix for that reason). This is inherited from the original addon.
- Rooms are not reference counted by the addon (a user count per room is an open to-do in the Janus
  service); the mixer owns room lifetime.
- The viewer-session registry is per-process memory.
- Avatar-to-avatar invitations, moderation state and the connector registry are in memory and are
  lost on restart. Call sessions are in the region server's memory, or with `SessionStoreMode = grid` in the
  grid server's (see [deployment.md](deployment.md)).
- An avatar-to-avatar invitation is delivered directly only to a callee whose root presence is in the caller's own
  region server process. A callee who is root in another process is rung through a carrier message (signed with
  `CarrierSecret` under `SessionStoreMode = local`, checked against the session service under `grid`).
- Several message classes are defined but not used: a Janus `hangup` request, an AudioBridge
  `configure` request and its response.
- Mixer-side behaviour (spatial rendering, per-connection diagnostics, an echo test, camera-position
  listening, the data channel contents) is outside this repository and is not described here.
