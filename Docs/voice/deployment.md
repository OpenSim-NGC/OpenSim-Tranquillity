# WebRTC voice: deployment

How to lay out regions, the Janus gateway and the Robust call session service, and which settings matter for each
layout. Settings are described in [configuration.md](configuration.md); what the features do is in
[overview.md](overview.md).

## Region layouts

### Several regions in one region server process

One voice module serves every region in the process, and it keeps one record of each call for all of them. Avatar-to-avatar
calls, group calls and conferences work between avatars on any of those regions with no extra setting. Leave
`CarrierSecret` unset and `SessionStoreMode` at `local`.

Regions in the same process need no entry in `ControlPlaneTrustedHosts` for each other (see
`Docs/security/Hardening.md`).

### One region per process (the usual layout)

Each process is a separate voice module with its own records. With the defaults (`SessionStoreMode = local`):

- A group call, a conference or an avatar-to-avatar call rings people on other processes only when `CarrierSecret`
  is set to the same value on every region server.
- Two people who call each other at the same instant from two processes both ring out, and an avatar-to-avatar call
  whose two people leave in different processes can wait for the idle sweep (8 hours) in the process of the one who
  left first.
- The seat limit of a group call or conference counts per process: a limit of 50 can be reached once in each process.
- A mute-all, a mute or a removal made on one process is a record of that process only.

Choose one of:

1. **`SessionStoreMode = grid`** with the call session service on Robust (below). All processes share one record of every
   call: one seat count, one ring, moderation seen everywhere, and seats given back when a region restarts. No
   `CarrierSecret` is needed, and neither avatar-to-avatar limit above applies. Recommended for a grid with more
   than one process that offers calls, group calls or conferences.
2. **`CarrierSecret`** on every region server, with `SessionStoreMode = local`. Group, conference and
   avatar-to-avatar rings, the answers of an avatar-to-avatar call, and conference text lines, are sent between
   processes as signed grid instant messages. Nothing runs on Robust.

If your regions are neighbours on different hosts, each region server needs the other hosts' addresses in
`[Network] ControlPlaneTrustedHosts`, as for any region server (see `Docs/security/Hardening.md`). The voice addon does
not read that list, and the signed ring and text messages between processes are not covered by it: they are ordinary
instant messages (dialog 17), not one of the privileged dialogs that list guards.

### The Janus gateway on another host

The region server reaches the gateway over HTTP(S) at `JanusGatewayURI` and `JanusGatewayAdminURI`. Every request
carries the API or admin secret, so use `https://` (see [security.md](security.md)) or a network you control. The
gateway must also be reachable by viewers for media (next section). In this layout the settings that matter are the
four `[JanusWebRtcVoice]` values, `JanusGatewayCACertificateFile` for a private CA, `PluginName`, and the timeouts
`RequestTimeoutMs` and `AdminTimeoutMs` if the link is slow.

## Networking

A viewer must reach three things:

1. **The region's UDP circuit**, on the address the region hands out. By default that is the address the region's
   `ExternalHostName` resolves to; the `[ViewerAddress]` section of `RegionServer.ini` can give different viewer
   networks different addresses.
2. **The region's HTTP listener**, which serves the viewer's capabilities, including the voice requests
   (`ProvisionVoiceAccountRequest`, `VoiceSignalingRequest`, `ChatSessionRequest`), on the same address.
3. **The gateway's media (RTP) UDP port range.** Audio flows between the viewer and the gateway on these ports. The
   addon cannot see this path.

Audio needs the gateway to advertise an address the viewer can reach (for a gateway in a container or behind NAT,
set the gateway's NAT address mapping, `nat_1_1_mapping` in Janus), that range to be published and open in every
firewall and router between the viewer and the gateway, and a STUN server for viewers outside your network
(`StunServers`). The addon does not deliver TURN.

**Test from a second machine.** A viewer on the same machine as the region can have working voice when a viewer on
another machine does not: the gateway's address and ports are reached over the local machine, which skips the router
and firewall rules and the address mapping that every other viewer depends on. Log in from a machine on another network
and check voice there before you call it done.

## Calls across region server processes

The two ways to share calls between processes, and what each limit means for users:

| | `local`, no `CarrierSecret` | `local` with `CarrierSecret` | `grid` |
|---|---|---|---|
| Avatar-to-avatar call, callee on the caller's process | Rings | Rings | Rings |
| Avatar-to-avatar call, callee on another process | Not rung | Rung, signed; the callee's join, decline and hang-up are carried back | Rung; the receiving region asks the service whether the call is genuine, and whether each answer is true |
| Two people calling each other at the same instant, from two processes | Not rung | Both ring out | One call; both can join |
| An avatar-to-avatar call whose two people leave in different processes | Not tracked across processes | A record can wait for the idle sweep (8 hours) in the process of the one who left first | Ended at once |
| Group call or conference ring to a member on another process | Not sent; one that arrives is refused | Sent, signed; received if the signature is valid | Sent; the receiving region asks the service whether it is genuine |
| Conference text to a member on another process | Not delivered | Delivered, signed | Delivered, signed with a key the service gives only to the conference's members |
| Seat limit of a group call or conference | Per process | Per process | One count for the grid |
| Mute, mute-all and removal | That process only | That process only | Recorded in the service; a region applies a removal to a member it hosts when its next seat renewal reports it (60 seconds by default) |
| Seats of a region that stops | Gone with the process | Gone with the process | Given back by the service when the lease runs out (180 seconds), or at once when the region starts again |

Details of `CarrierSecret`:

- At least 32 characters, the same on every region server. A shorter value is refused at start-up with an error and the
  carriers stay off.
- Each ring and text line carries a signature over its contents. A message older than 60 seconds (a clock difference of
  30 seconds is allowed), repeated, unsigned, or signed wrongly is refused. The first refusal of each reason is logged,
  then every hundredth.
- A ring that arrives never changes a session the receiving process already holds, and the receiving process does not
  ring the group again for a call it did not start.
- Keep the clocks of the region servers in step.

## The Robust call session service

The service (#356) is part of `WebRtcVoice.dll`, which the grid server project references since #352. It keeps the records of
group calls, conferences and avatar-to-avatar calls in the grid server's memory.

**Enable it on Robust** (`GridServer.ini`, or `Robust.ini`). Both the `[ServiceList]` entry and `Enabled = true` are
needed, and it refuses to load without HTTP basic authentication and a password of at least 32 characters:

```ini
[ServiceList]
    VoiceSessionConnector = "${Const|PrivatePort}/WebRtcVoice.dll:VoiceSessionServerConnector"

[WebRtcVoiceSessionService]
    Enabled = true
    AuthType = "BasicHttpAuthentication"
    HttpAuthUsername = "voice-sessions"
    HttpAuthPassword = "<a password of at least 32 characters, kept out of version control>"
```

**Point every region server at it**, with the same user and password, in the region server's voice settings:

```ini
[WebRtcVoice]
    SessionStoreMode = grid
    SessionStoreServerURI = http://robust.example.org:8003
    AuthType = "BasicHttpAuthentication"
    HttpAuthUsername = "voice-sessions"
    HttpAuthPassword = "<the same password>"
```

Use the private port in `SessionStoreServerURI`. The service is authenticated by the basic-authentication user and password
only. Set `SessionStoreMode` the same way on every region server: a call started on a server that keeps its own record
is not seen by the others.

**Authentication.** The service accepts nothing without the credentials. The region server's own call to it uses the
section it is given (`[WebRtcVoice]`), falling back to `[Network]`; the service on Robust uses `[WebRtcVoiceSessionService]`,
falling back to `[Network]`. Basic authentication sends the password with every request, so keep the link on the private port and on a network only your
servers can reach.

**What a Robust restart does.** The records live in memory, so a restart empties them. People already talking keep their
audio, because audio does not pass through Robust or the region server. A call that has to be looked up again finds no
session: a viewer that provisions into a group call or conference after the restart is refused with HTTP 403 (the decision
is `group-refused-no-session` or `adhoc-refused-no-session`), and a moderation request is answered 404. Calling the group
again starts a new call.

**When the service cannot be reached.** The region refuses new calls and never falls back to keeping its own record. The
answer is **HTTP 503**, which the viewer can try again, on every path that can carry the refusal: "start p2p voice" and
"call" for an avatar-to-avatar call; "call" and "accept invitation" for a group; "start conference", "invite", "call" and
"accept invitation" for a conference; the voice provision for a group or conference room; and a moderation request. Every
other refusal keeps its code: a missing group power, a wrong token and an uninvited member are still 403, a full room is
still 409, and a call the service does not know is 404. A ring that arrives while the service cannot be reached is dropped.
After 5 calls in 10 seconds with no answer a circuit breaker opens and calls are refused at once for 5 seconds without
waiting for the timeout (3000 ms); then one call is tried. Departures (people leaving a call) are kept, up to 256 for 60
seconds, and sent when the service answers again; a seat given up is given back by the lease. The region logs each of these
(see [troubleshooting.md](troubleshooting.md)).

**At start-up** the region asks the service once whether it answers and logs the result, so a wrong address, a Robust without
the service and a wrong password are reported when the region starts. The console command `show voice sessions` shows the
state at any time.

## Running the slvoice mixer

A grid that uses the mixer-only features runs the slvoice mixer (named and linked in
[overview.md](overview.md#the-slvoice-mixer)) in place of, or beside, the stock Janus gateway. The mixer's own README, `docs/docker-notes.md` and `docs/RELEASES.md` are the
reference; this is what the region server's operator has to line up. Everything here needs release **1.2.0 or later**.

- **Image.** The 1.2.0 image named in the overview, or a build of the mixer's repository at its `v1.2.0` tag. The image holds
  `janus.plugin.slvoice` only: it is built without Janus's stock plugins, so a deployment that also needs the stock
  AudioBridge needs a separate Janus. Coming from 0.7.0, read the mixer's compatibility table before you pull `latest`.
- **Secrets.** `JS_API_SECRET` and `JS_ADMIN_SECRET` equal to the region's `APIToken` and `AdminAPIToken`, at least 32 random
  characters each; a blank one refuses to start. `JS_JOIN_CAP_SECRET`, equal to the region's `JoinCapabilitySecret`, only if
  you use the join capability.
- **Address.** `JS_PUBLIC_IP` or `JS_PUBLIC_HOST`, so viewers are given a media address they can reach.
- **Ports.** The mixer's examples use 14223 for the API (`/voice`) and 14225 for the admin API (`/voiceAdmin`); check the
  repository's `docker-compose.yml`. The API must be reachable by the region servers and the admin API only by them
  (`JS_ADMIN_BIND` narrows it). `JS_TLS_CERT` and `JS_TLS_KEY` turn on https for both; then use https URLs here and
  `JanusGatewayCACertificateFile` for a private CA (see [security.md](security.md)).
- **Region server.** `PluginName = janus.plugin.slvoice` in the region server's own `[JanusWebRtcVoice]`, and
  `JanusGatewayAdminURI` and `AdminAPIToken` for the feeds.
- **Room size and CPU.** A room admits 50 participants by default (`JS_ROOM_SIZE`, 2 to 110; 0.7.0 allowed 64), and the Opus
  encoder complexity is 5, which the mixer's release notes measure at about 35% less CPU per tick than the 0.7.0 setting of 9
  at 50 to 80 participants. On the machine measured (a desktop processor, in a container with no CPU limit), a tick with 64
  participants all talking stayed inside the 20 ms budget at the 95th percentile (15.5 ms) and one with 80 did not (21.4 ms).
  Check the tick histogram in the admin API's `handle_info` (`overruns`, `ge20ms`) under real load before raising
  `JS_ROOM_SIZE`.
- **Modes that act.** `JS_VIS_FAIL_CLOSED` (silence a listener whose arming is stale) and `JS_JOIN_CAP_REQUIRED` (refuse a join
  without a capability) are off by default and only count what they would do. Turn on `JS_JOIN_CAP_REQUIRED` only when every
  client that joins a room the region declared mints a capability.

## Sizing

**Gateway media ports.** The slvoice mixer's documentation gives one UDP port from the gateway's RTP range for every person in
voice at the same time; use the same figure as your starting point for the stock plugin and check it against your Janus
configuration. Size the range for the most people you expect in voice at once across all regions that use the gateway, plus
headroom: 200 ports is about 200 people. When the range is exhausted further joins fail. The range is set on the gateway
(`rtp_port_range` in Janus's configuration); the firewall, any router forward, and a container's published ports must
match it. On the slvoice mixer the range is `JS_RTP_PORT_RANGE`; size it for the busiest room you expect, and the mixer's own
sizing note covers the widest rooms.

**Session limits** (defaults; each is a setting):

| Limit | Default | Where | Setting |
|---|---|---|---|
| Sessions of all kinds held at once | 5000 | Region server process (`local`) or the service (`grid`) | `MaxNonSpatialSessions` / `MaxSessions` |
| Live sessions one agent may have started | 20 | same | `MaxSessionsPerAgent` |
| "Start conference" and "invite" requests one agent can make back to back | 5, refilling one every 10 seconds | same | `ConferenceRequestBurst`, `ConferenceRequestRefillSeconds` |
| Seats in one group call or conference | 50, at most 110 | Region | `GroupVoiceCap` (group). A conference uses 50. A slvoice mixer admits 50 per room by default (`JS_ROOM_SIZE`) and at most 110, and refuses a seat beyond that as a full room |
| Seats in an avatar-to-avatar call | 2 | fixed | - |
| Unanswered invitation | 2 minutes | `local`: fixed. `grid`: `InviteTtlSeconds` (120) | - |
| Idle call (nobody in it) | 8 hours | `local`: fixed. `grid`: `IdleTtlHours` (8) | - |

A start beyond a limit is refused and logged. A call with a member who is still present is never idle.
A conference start may name the seat cap minus one invitees, because the starter holds a seat.

**Gateway rooms.** The addon creates one room per estate channel, parcel, call and conference as needed. On the stock
AudioBridge it removes rooms it created once they have been empty for `EmptyRoomGraceSeconds` (60), so a long-running
gateway does not accumulate them.

**Traffic to the mixer.** These feeds are off by default; the figures are arithmetic from the defaults, not measurements.

- The visibility feed (#362) sends a change when one occurs and a heartbeat for each region every `VisibilityHeartbeatMs`
  (1000 by default, 250 to 4000). A region server with many regions can lengthen the heartbeat. A room whose request would be
  over the mixer's limit of 65,536 bytes is sent in several parts.
- The position feed (#364) sends at most one request for each occupied room at each look, 500 to 750 ms apart by
  default: 80 to 120 requests a minute for a room of 50 where someone is moving, 22 to 30 where nobody is. Four rooms of 50
  are 320 to 480 requests a minute when busy. The rooms of a look are sent one after another, so lengthen
  `PositionFeedTickMs` on a region server with many occupied rooms. See [positions.md](positions.md).
