# WebRTC voice: troubleshooting

What the region logs at start-up and while it works, the console commands, and what the common refusals mean. Settings are in
[configuration.md](configuration.md).

Lines marked **Debug** appear only when the region server's log is set to show Debug messages. Everything else is at
Information, Warning or Error. Secrets are never logged. Every voice logger logs under the category prefix `osWebRtcVoice` (#358),
so one line in the region server's `appsettings.json` raises voice to warnings and errors only; the add-on README section
"Quieting voice in the log" gives the whole block.

## Start-up lines

Look for these in the region server log after a start. The name in square brackets is the prefix of the line.

| Setting or part | Line (shortened) | Meaning |
|---|---|---|
| `[WebRtcVoice] Enabled` | `[REGION WEBRTC VOICE]: enabled` | The region module is on. No line: `Enabled` is not `true` in the file the region read |
| Voice service keys | `[WEBRTC VOICE SERVICE MODULE] WebRtcVoiceService enabled` | The service module loaded. Errors: `No VoiceService specified in configuration` (both service keys empty), `Could not load SpatialVoiceService from <dll:class>, module disabled` |
| Janus connection | `[JANUS WEBRTC SERVICE] Enabled` | The Janus service started and connected. Errors: `No WebRtcVoice configuration section`, `No JanusWebRtcVoice configuration section`, `JanusWebRtcVoice configuration section missing required fields` (one of the four values is empty), `failed connection to Janus Gateway. Disabled` (wrong address, wrong secret, or the gateway is down) |
| `APIToken`, `AdminAPIToken` | `[JanusWebRtcVoice] APIToken is shorter than 32 characters; use a longer random secret` (Warning) | The secret is accepted but weak |
| `PluginName` and the mixer-only switches | `[WebRtcVoice] <Switch> is true, but [JanusWebRtcVoice] PluginName is janus.plugin.audiobridge, Janus's stock AudioBridge, which cannot do this. The switch stays off.` (Warning) | A switch that needs the slvoice mixer was turned on with the stock plugin. Also check that the region server's own `[JanusWebRtcVoice]` section names the plugin |
| `RequestTimeoutMs` | `RequestTimeoutMs = <n> is not positive; using 5000` (Warning) | |
| `RemoveEmptyRooms`, `EmptyRoomGraceSeconds` | `emptied rooms are left in place ([JanusWebRtcVoice] RemoveEmptyRooms is false)`; `not removing emptied rooms: plugin <name> is not the stock AudioBridge ...`; `EmptyRoomGraceSeconds = <n> is negative; using 60` | |
| `JanusGatewayCACertificateFile`, https | `[JANUS TLS] the certificate presented by ... was rejected` | The gateway's certificate did not validate. See [security.md](security.md) |
| `JoinCapabilityEnabled` | `join capability enabled: every join carries one, 60 s lifetime`. Errors: `JoinCapabilitySecret is empty: no capability can be minted` and `JoinCapabilitySecret is shorter than 32 characters: refusing to mint capabilities with it` | The feature is off after either error |
| `VisibilityArmingEnabled` | `spatial "local" rooms are created with vis_authority=true`; `[VOICE VISIBILITY] arming ENABLED for <region>`; warning `VisibilityArmingEnabled is true but <region> has no Janus peer_ctl sink` | |
| `VisibilityFeederEnabled`, `VisibilityEmitEnabled` | `[VOICE VISIBILITY] feeder started for <region> @ 250ms (emit=True)`; warning `VisibilityEmitEnabled but [JanusWebRtcVoice] JanusGatewayAdminURI/AdminAPIToken missing; region "<name>" runs matrix-only (no emission)` | |
| `VisibilityHeartbeatMs` | A warning naming the key | The value is outside 250 to 4000 and 1000 is used (#362) |
| `PositionFeedKeepAliveMs` | A warning naming the key | The value is outside 1 to 2250 and 2000 is used (#364) |
| `PositionFeedEnabled` | See [positions.md](positions.md) | |
| `CarrierSecret` | `cross-process voice carriers on: group and conference rings and conference text are signed with [WebRtcVoice] CarrierSecret`; or `cross-process voice carriers are off: [WebRtcVoice] CarrierSecret is not set ...`; or error `CarrierSecret is shorter than 32 characters and is refused; cross-process voice carriers are off` | |
| `GroupVoiceEnabled` | `GROUP VOICE enabled ([WebRtcVoice] GroupVoiceEnabled): cap <n>, required powers 0x... (GP_SESSION_JOIN + GP_SESSION_VOICE)` | The cap shown is the one in force after clamping |
| `AdhocVoiceEnabled` | `ADHOC VOICE enabled ([WebRtcVoice] AdhocVoiceEnabled) ...` | |
| `CallModerationEnabled` | `CALL MODERATION enabled ...`; or warning `CallModerationEnabled is true, but neither GroupVoiceEnabled nor AdhocVoiceEnabled is ...` | |
| `SessionStoreMode` | `[WebRtcVoice] SessionStoreMode "<value>" is not local or grid; non-spatial sessions stay in this process` (Error); `[WebRtcVoice] SessionStoreMode is grid but SessionStoreServerURI is not set; non-spatial sessions stay in this process` (Error); `non-spatial sessions are kept by the grid session store at <uri> (timeout ...)` | |
| Grid session service check | `grid session store at <uri> answered the start-up check`, or the error `did not answer the start-up check: check SessionStoreServerURI, that the Robust that serves it has the voice session service enabled, and the service credentials. New calls are refused until it answers` | |
| Service credentials | `grid session store: [WebRtcVoice] ... AuthType must be BasicHttpAuthentication` / `HttpAuthUsername is not set` / `HttpAuthPassword must be at least 32 characters; the service will refuse these calls` | Fix the region server's credentials |
| Robust side | `[VOICE SESSION SERVICE]: serving /voicesessions; grid id "..."; limits ... sessions, ... per agent, burst ...`; or `not enabled ([WebRtcVoiceSessionService] Enabled is not true)`; the grid server throws `[WebRtcVoiceSessionService] is enabled but cannot load: <reason>` for bad credentials | |
| Region restart with the grid service | `grid session store: released <n> seat(s) left by an earlier run of region <name>` | The service gave back seats the earlier process held |
| `ConnectorsEnabled` | See [configuration.md](configuration.md); a record that breaks a rule logs a warning naming the reason | |
| Grid identity | `[JANUS WEBRTC SERVICE] grid id for multiagent rooms = <id>`; or the warning `no GatekeeperURI in [Hypergrid]/[Startup]/[Const] (nor [GatekeeperService] ExternalName / [GridService] Gatekeeper): multiagent rooms are derived without a grid id` | Without a grid id two grids on one gateway can share a call's room |

## While it runs

Each of these is one line per decision, with the same words in every line, so you can search for a word.

| Prefix | What it follows | Level |
|---|---|---|
| `[A2A CHATSESSION]` | Every chat session request: `agent=... method="call" session-id=... decision=...`. Decisions include `recorded`, `credentials-issued`, `history-empty`, `refused-400 unknown method` | Debug |
| `[A2A PROVISION]` | Every voice provision's admission: `channel_type`, `channel`, `credentials=present/absent`, `decision=local-admitted`, `multiagent-admitted`, `logout`, `passthrough-no-feature`, `refused-no-session`, `refused-not-party`, `refused-bad-token`, `refused-npc`; a `channel_type` the addon does not serve is refused under a decision word of its own, and the Warning line names the type | Debug; a refusal is also a Warning, at most one a minute for each kind of refusal (and each agent), saying how many it stands for (#358): `refusing provision with channel_type "..." (<decision>) from agent ...` |
| `[A2A INVITE]` | Delivery of an avatar-to-avatar invitation: `decision=sent` or `callee-unreachable(no-presence)` | Debug |
| `[GROUP VOICE]`, `[ADHOC VOICE]`, `[ADHOC CHAT]`, `[ADHOC TEXT]`, `[ADHOC ROSTER]` | Group and conference calls, joining and leaving, conference text lines, roster updates. Words such as `group-call-admitted`, `group-refused-no-power`, `group-refused-capacity`, `adhoc-text-delivered`, `adhoc-text-no-recipients` | Debug |
| `[GROUP INVITE]`, `[ADHOC INVITE]` | A ring to a member in this process: `target=... region="..." decision=sent`, or the reason it was not | Debug |
| `[GROUP RING XHOST]`, `[ADHOC TEXT XHOST]` | A ring or a text line for another process: `decision=sent-cross-instance`, `skipped-offline` (the members who are offline are left out; the line gives the count), `no-message-transfer-module`. A message that arrives: `not-ours`, `malformed` | Debug |
| `carrier accepted: kind=ring\|text session=... type=... from=<region id>` | A cross-process message that passed authentication | Debug |
| `carrier not sent: session=... decision=carrier-off-no-secret` | A ring that could not be sent because no `CarrierSecret` is set | Debug |
| `carrier refused: kind=... reason=... count=...` | A cross-process message was refused. Reasons: `no-secret`, `missing-mac`, `malformed-mac`, `bad-mac`, `stale`, `replay`, `nonce-store-full`. Logged for the first of each reason and then every hundredth | Warning |
| `[CALL MODERATION]`, `[CALL ROSTER]`, `[CALL REMOVAL]` | Moderation requests and their decisions, roster flags, removals | `[CALL REMOVAL]` Information; the others Debug |
| `[PARCEL RESOLVE]` | Which parcel a spatial provision was resolved to and whether the viewer's own id agreed | Debug |
| `[NONSPATIAL SWEEP]` | Expired invitations and idle sessions removed | Debug |
| `non-spatial session limit: agent=... decision=session-cap-global\|session-cap-agent\|rate-limited\|too-many-invitees` | A start or invite was refused by a limit | Warning |
| `[VOICE VISIBILITY]` | The visibility feeder and its sender (#362). A one-minute summary while the region has rooms or listeners (arming replaces, entries the mixer deferred and stale or unarmed reports are counted in it and written at Debug), one line when the region goes idle and one when it wakes. A failing emit (the admin API is down) is one Error a minute with the number held back. The first `unknown_room` for a room whose last listener left is Debug; a second one, or one for a room that still has listeners, is a Warning | Information, Warning and Error as described |
| `[VOICE POSITIONS]` | The position feed (#364); see [positions.md](positions.md) | Information, Warning and Debug |

## Console commands

| Command | Shows |
|---|---|
| `show voice sessions` | Where this region keeps its call sessions. `local`: how many it holds and whether cross-process messages are signed. `grid`: the service address, whether it answered when the region started, the circuit breaker state, the seats this region holds, and departures waiting or given up |
| `show voice calls`, `show voice call <call-id>` | Group calls and conferences, and one call's members (see [moderation.md](moderation.md)) |
| `show voice closing` | Voice sessions whose teardown is in flight or failed, with age and last failure |
| `show voice visibility` | The counters of the most recent visibility send per region (the feeder must be on); with the position feed on, also what the mixer has answered to the position requests |
| `show voice moderation` | Parcel moderation state (the feeder must be on) |
| `janus info` | The gateway's information, as unformatted JSON |
| `janus list rooms` | The rooms on the gateway's AudioBridge plugin |
| `voice connector start\|stop <name>` | Starts or stops a loaded connector record |

## Refusals and their HTTP codes

What a viewer is told, by request. Search the log for the decision word.

**Voice provision**

| Code | When |
|---|---|
| 200 | Done. The body holds the answer; a failure inside it carries no HTTP error |
| 204 | The request had no body |
| 403 | The parcel does not allow voice, or the avatar is banned from or not allowed on it; a `channel_type` that is not served; a call, group or conference provision whose session does not exist (`refused-no-session`, `group-refused-no-session`, `adhoc-refused-no-session`), whose agent is not a party, member or invitee, lacks the powers (`group-refused-no-power`), holds the wrong token (`...-bad-token`) or was removed (`...-removed`) |
| 404 | The voice service is not loaded; the request is not a POST; the avatar or its parcel could not be found |
| 409 | The room is full: a group or conference at its seat cap (`group-refused-capacity`, `adhoc-refused-capacity`), or the mixer reports its room full |
| 501 | The estate does not allow voice, or the region's land data is not ready yet |
| 503 | A group or conference provision when the call session service could not be reached |

**Chat session request** (the Call, Accept and Start Conference buttons)

| Code | When |
|---|---|
| 400 | An unknown method; "start p2p voice" without a valid callee; a moderation request that is malformed |
| 403 | A request from a child agent (a neighbouring region's connection) while a call feature is on; "call" for a call the agent is not a party to; a group "call" or "accept invitation" from a non-member, without the powers (`group-refused-no-power`) or after a removal; a call-moderation request from someone who may not moderate |
| 404 | "call" for a call that does not exist or has expired; a moderation request for a call that is gone |
| 409 | A group call or conference that is full |
| 501 | A voice mute the voice server cannot carry out |
| 503 | The call session service could not be reached: "start p2p voice" and "call" for an avatar-to-avatar call, "call" and "accept invitation" for a group, "start conference", "invite", "call" and "accept invitation" for a conference, and a moderation request. Or a stock-plugin mute failed |

A refused provision is answered from a cache for `RefusalCacheSeconds` (5), so a viewer that retries at once does not repeat
the checks. A run of refusals for one viewer is logged as an episode (#358): one Information line for the first refusal
(`provision from <agent> in "<region>" refused ...; further refusals for this viewer are counted, and reported when they
stop`), and one when the run ends with the count (`provision from <agent> ... was refused <n> times in <s> s; ...`). The
run ends when the viewer is admitted, logs out or is let back in to a call, or when it has not been refused for a minute (a
15-second timer closes quiet episodes, so the closing line can be up to 15 seconds late). With `RefusalCacheSeconds = 0`
nothing is logged.

## Common problems

**Viewers connect but hear nothing.** The region cannot see the media path. Check that the gateway advertises an address viewers
can reach, that the gateway's UDP media port range is open in the firewall and any router, and that a viewer on another network
has a STUN server (`StunServers`). Test from a second machine: a viewer on the machine that runs the region can work when
others cannot (see [deployment.md](deployment.md)).

**Voice does not start at all.** Check the start-up lines above: all four Janus values set, the secrets matching the gateway's,
the gateway reachable.

**The Call button does nothing.** `A2AVoiceEnabled` is off, or the other person is on a region served by another region server
process and neither `CarrierSecret` (the same on every region server) nor `SessionStoreMode = grid` is set (see
[deployment.md](deployment.md)). The carrier steps are logged at Debug as `[A2A XHOST]`; a refused one is logged at
Warning with the reason.

**A group or conference call does not ring a member on another process.** In `local` mode set the same `CarrierSecret` on
every region server, or use `SessionStoreMode = grid`. Look for `carrier refused` lines on the receiving region and
`carrier-off-no-secret` on the sending one.

**A mixer-only feature does nothing.** Check the start-up lines for the warning that names the switch and the plugin; then check
the region server's own `[JanusWebRtcVoice] PluginName`.

**Avatar-to-avatar or group calls fail with the call session service on.** Run `show voice sessions`. If it says the service
did not answer, check `SessionStoreServerURI` (the private port), that `[WebRtcVoiceSessionService] Enabled = true` on Robust,
and the user and password on both sides.

**Rooms pile up on the gateway.** On the stock AudioBridge `RemoveEmptyRooms` removes rooms the region created, after
`EmptyRoomGraceSeconds`. Rooms that existed before, or that another process created, are not removed.

**Viewers are told "channel full" in a group call or conference.** A group or conference holds at most `GroupVoiceCap` seats
(50 by default), and a slvoice mixer admits 50 participants to a room by default (`JS_ROOM_SIZE`, up to 110). Either limit
answers HTTP 409. Raise both, in step, if the room is meant to be larger, and check the mixer's tick histogram first.

**The position feed does nothing.** Check, in this order: `PositionFeedEnabled`, the feeder and emission are on and
`JanusGatewayAdminURI` and `AdminAPIToken` are set; the start-up warning for the stock plugin is absent; the mixer is release
1.2.0 or later (the `[VOICE POSITIONS]` lines say which protocol version it advertises); and `show voice visibility` shows
requests sent and answered. Arming is not needed for the feed to learn the mixer's version. See [positions.md](positions.md).

**A viewer crashes when Call is pressed in the nearby-voice list.** Firestorm 7.2.4 has crashed as the caller within
about 100 milliseconds of sending "start p2p voice" and "call" when Call was pressed in the nearby-voice list. No crash
dump was captured. The region log shows normal replies to the caller, so the cause is in the viewer. Start calls from the
IM window or the group window instead; that works.
