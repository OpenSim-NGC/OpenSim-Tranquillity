# os-webrtc-janus

Addon-module for [OpenSimulator] to provide webrtc voice support
using Janus-gateway.

For an explanation of background and architecture, 
this project was presented at the
[OpenSimulator Community Conference] 2024
in the presentation
[WebRTC Voice for OpenSimulator](https://www.youtube.com/watch?v=nL78fieIFYg).

This addon works by taking viewer requests for voice service and
using a separate, external [Janus-Gateway WebRTC server].
This can be configured to allow local region spatial voice
and grid-wide group and spatial voice. See the sections below.

For running that separate Janus server, check out
[os-webrtc-janus-docker] which has instructions for running
Janus-Gateway on Linux and Windows WSL using Docker.

Instructions for:

- [Building into OpenSimulator](#Building): Build OpenSimulator with WebRTC voice service
- [Configuring Simulator for Voice Services](#Configure_Simulator)
- [Configuring Robust Grid Service](#Configure_Robust)
- [Configure Standalone Region](#Configure_Standalone)
- [Managing Voice Service](#Managing_Voice) (console commands, etc)

**Note**: as of January 2024, this solution does not provide true spatial
voice service using Janus. There are people working on additions to Janus
to provide this but the existing solution provides only non-spatial
voice services using the `AudioBridge` Janus plugin. Additionally,
features like muting and individual avatar volume are not yet implemented.

<a id="Known_Issues"></a>
## Known Issues

- No spatial audio
- One can see your own "white dot" but you don't see other avatar's white dots
- No muting
- No individual volume control

And probably more found at [os-webrtc-janus issues](https://github.com/Misterblue/os-webrtc-janus/issues).

<a id="Building"></a>
## Building Plugin into OpenSimulator

`os-webrtc-janus` is integrated as a source build into [OpenSimulator].
It uses the [OpenSimulator] addon-module feature which makes the
build as easy as cloning the `os-webrtc-janus` sources into the
[OpenSimulator] source tree, running the build configuration script,
and then building OpenSimulator.

The steps are:

```
# Get the OpenSimulator sources
git clone git://opensimulator.org/git/opensim
cd opensim     # cd into the top level OpenSim directory

# Fetch the WebRtc addon
cd addon-modules
git clone https://github.com/Misterblue/os-webrtc-janus.git
cd ..

# Build the project files
./runprebuild.sh

# Compile OpenSimulator with the webrtc addon
./compile.sh

# Copy the INI file for webrtc into a config dir that is read at boot
mkdir bin/config
cp addon-modules/os-webrtc-janus/os-webrtc-janus.ini bin/config
```

These building steps create several `.dll` files for `os-webrtc-janus`
in `bin/WebRtc*.dll`. Some adventurous people have found that, rather
than building the [OpenSimulator] sources, you can just copy the `.dll`s
into an existing `/bin` directory. Just make sure the `WebRtc*.dll` files
were built on the same version of [OpenSimulator] you are running.

<a id="Configure_Simulator"></a>
## Configure a Region for Voice

The last step in [Building](#Building) copied `os-webrtc-janus.ini` into 
the `bin/config` directory. [OpenSimulator] reads all the `.ini` files
in that directory so this copy operation adds the configuration for `os-webrtc-janus`
and this is what needs to be configured for the simulator and region.

The sample `.ini` file has two sections: `[WebRtcVoice]` and `[JanusWebRtcVoice]`.
The `WebRtcVoice` section configures the what services the simulator uses
for WebRtc voice. The `[JanusWebRtcVoice]` section configures any connection
the simulator makes to the Janus server. The latter section is only updated
if this simulator is using a local Janus server for spatial voice.

The values for `SpatialVoiceService` and `NonSpatialVoiceService` point
either directly to a Janus service or to a Robust grid server that is providing
the grid voice service. Both these options are in the sample `os-webrtc-janus.ini`
file and the proper one should be uncommented.

The viewer makes requests for either spatial voice (used in the region and parcels)
or non-spatial voice (used for group chats or person-to-person voice conversations).
`os-webrtc-janus` allows these two types of voice connections to be handled by
different voice services. Thus there are two different configurations:

- all voice service is provided by the grid (both spatial and non-spatial point to a robust service), and
- the region simulator provides a local Janus server for region spatial voice while the grid service is used for group chats

#### Grid Only Voice Services

The most common configuration will be for a simulator that uses the grid supplied
voice services. For this configuration, `os-webrtc-janus.ini` would look like:

```
[WebRtcVoice]
    Enabled = true
    SpatialVoiceService = WebRtcVoice.dll:WebRtcVoiceServiceConnector
    NonSpatialVoiceService = WebRtcVoice.dll:WebRtcVoiceServiceConnector
    WebRtcVoiceServerURI = ${Const|PrivURL}:${Const|PrivatePort}
```

This directs both spatial and non-spatial voice to the grid service connector
and `WebRtcVoiceServerURI` points to the configured Robust grid service.

There is no need for a `[JanusWebRtcVoice]` section because all that is handled by the grid services.

#### Local Simulator Janus Service

In a grid setup, there might be a need for a single simulator/region to use its own
Janus server for either privacy or to off-load the grid voice service.
In this configuration, spatial voice is directed to the local Janus service
while the non-spatial voice goes to the grid services to allow grid wide group
chat and region independent person-to-person chat.

This is done with a `os-webrtc-janus.ini` that looks like:
```
[WebRtcVoice]
    Enabled = true
    SpatialVoiceService = WebRtcJanusService.dll:WebRtcJanusService
    NonSpatialVoiceService = WebRtcVoice.dll:WebRtcVoiceServiceConnector
    WebRtcVoiceServerURI = ${Const|PrivURL}:${Const|PrivatePort}
[JanusWebRtcVoice]
    JanusGatewayURI = http://janus.example.org:14223/voice
    APIToken = APITokenToNeverCheckIn
    JanusGatewayAdminURI = http://janus.example.org/admin
    AdminAPIToken = AdminAPITokenToNeverCheckIn
```

Notice that, since the simulator has its own Janus service, it must configure the
connection parameters to access that Janus service. The details of running and
configuring a Janus service is provided at [os-webrtc-janus-docker] but, the configuration
here needs to specify the URI to address the Janus server and the API keys
to allow this simulator access to its interfaces. The example above
contains just sample entries.

`APIToken` and `AdminAPIToken` are sent with every request to the gateway. Use random values of at least 32 characters;
a shorter one is logged as a warning at startup (the key is named, the value is not). When the gateway is on another
host, use `https://` URLs or a private network: over plain `http://` the secrets cross the network in clear.
`https://` URLs are validated as before: the certificate must chain to a trusted root and name the host contacted.
`JanusGatewayCACertificateFile` adds a private CA (PEM or DER), trusted alongside the system roots. No setting turns
validation off.

#### Optional settings

These keys are optional. The shipped `os-webrtc-janus.ini` lists them commented out with their defaults.

| Key | Section | Default | Notes |
|---|---|---|---|
| `PluginName` | `[JanusWebRtcVoice]` | `janus.plugin.audiobridge` | The Janus plugin the region attaches voice handles to. The default is Janus's stock AudioBridge. `janus.plugin.slvoice` selects a spatial mixer that implements that plugin. |
| `RequestTimeoutMs` | `[JanusWebRtcVoice]` | `5000` | Milliseconds a Janus request that is answered with `ack` waits for its result before it fails. Before this setting such a request waited without limit. |
| `RemoveEmptyRooms`, `EmptyRoomGraceSeconds` | `[JanusWebRtcVoice]` | `true`, `60` | Remove a room this service created once it has stood empty for the grace period. The stock AudioBridge never removes an emptied room. Rooms the service did not create are never removed, and a plugin other than the stock AudioBridge is expected to remove its own. |
| `RefusalCacheSeconds` | `[WebRtcVoice]` | `5` | Seconds a refused voice provision is remembered, so the viewer's immediate retries are answered without repeating the estate and parcel checks. `0` turns it off. |
| `JanusGatewayCACertificateFile` | `[JanusWebRtcVoice]` | unset | A PEM or DER file with a private CA certificate, trusted alongside the system roots for an `https://` gateway URL. The host name must still match, and no setting turns validation off. |
| `StunServers` | `[WebRtcVoice]` | empty | Comma-separated STUN server URIs (full ICE URIs, for example `stun:stun.example.org:3478`) advertised to viewers as the `stun-servers` simulator feature. Stock viewers need a valid entry to connect from outside the grid's network. When empty the entry is not advertised. Works with either plugin. |
| `A2AVoiceEnabled` | `[WebRtcVoice]` | `false` | Avatar-to-avatar calls (the `call` and `start p2p voice` chat session requests). With it off the chat session handler answers as before. A callee whose region runs in another region-server process is rung by a message that crosses between the processes: signed with `CarrierSecret`, or, with `SessionStoreMode = grid`, checked through the call session service. |

A call, group call or conference gets its own room, numbered from the grid's identity (`GatekeeperURI`, read from
`[Const]`, `[Startup]` or `[Hypergrid]`), the call's channel and its type, so two grids that share one mixer do not
share rooms. Without a `GatekeeperURI` the number is derived without the grid part, and a warning is logged at
startup when a call feature is on.
| `MaxNonSpatialSessions`, `MaxSessionsPerAgent` | `[WebRtcVoice]` | `5000`, `20` | The most non-spatial sessions this region server holds at once, and the most live ones a single agent may have started. A start beyond either is refused. An unanswered invitation expires after 2 minutes and an idle session after 8 hours. With `SessionStoreMode = grid` the call session service enforces its own limits, set in `[WebRtcVoiceSessionService]`. |
| `GroupVoiceEnabled` | `[WebRtcVoice]` | `false` | Group voice. Needs the Groups module. |
| `GroupVoiceRequireVoicePower` | `[WebRtcVoice]` | `true` | Require the group role power to speak (`GP_SESSION_VOICE`) as well as to join. `false` requires only the power to join. |
| `GroupVoiceCap` | `[WebRtcVoice]` | `50` | The most seats in one group voice session. |
| `CarrierSecret` | `[WebRtcVoice]` | empty | Signs the grid instant messages that ring a group or conference member whose region runs in another process, and the conference text lines that cross the same way. A grid with regions in more than one process must set the same value, at least 32 characters, on every region server for group and conference calls to ring across them. Unset, nothing is sent across processes and every such message that arrives is refused; regions of one process are not affected. With `SessionStoreMode = grid` it is not needed: the receiving region asks the call session service, and conference text lines are signed with a key the service keeps for the conference. |
| `AdhocVoiceEnabled` | `[WebRtcVoice]` | `false` | Ad-hoc voice and conference text (`start conference`). Needs the Groups module for the conference text session. |
| `ConferenceRequestBurst`, `ConferenceRequestRefillSeconds` | `[WebRtcVoice]` | `5`, `10` | A per-agent rate limit shared by `start conference` and `invite`: how many requests may come back to back, and the seconds for one more to become available. |
| `SessionStoreMode` | `[WebRtcVoice]` | `local` | `local` keeps call sessions in each region-server process. `grid` keeps them in the call session service that the grid server hosts (see [Sharing call sessions between region servers](#Configure_Session_Store)). |
| `SessionStoreServerURI` | `[WebRtcVoice]` | empty | Required with `grid`: the base address of the grid server that hosts the service, for example `http://robust.example.org:8003`. |
| `AuthType`, `HttpAuthUsername`, `HttpAuthPassword` | `[WebRtcVoice]` or `[Network]` | none | Required with `grid`: `BasicHttpAuthentication` and the user and password of `[WebRtcVoiceSessionService]`. |
| `SessionStoreTimeoutMs` | `[WebRtcVoice]` | `3000` | Milliseconds to wait for the service before a request is refused. Values under 100 read as the default. |
| `SessionSeatRenewSeconds` | `[WebRtcVoice]` | `60` | Seconds between a region telling the service which seats it still holds. Values under 5 read as the default. |
| `SessionStoreOutboxMax`, `SessionStoreOutboxSeconds` | `[WebRtcVoice]` | `256`, `60` | Departures the service could not be told about are kept and sent when it answers: the most kept, and the seconds each is kept before it is given up. |
| `SessionStoreBreakerFailures`, `SessionStoreBreakerWindowSeconds`, `SessionStoreBreakerOpenSeconds` | `[WebRtcVoice]` | `5`, `10`, `5` | After this many requests get no answer within the window, further requests are refused at once for the open period, then one is tried to see whether the service is back. |

| `AdminTimeoutMs` | `[JanusWebRtcVoice]` | `5000` | Milliseconds a request to the Janus admin API waits for its answer. |
| `VisibilityFeederEnabled`, `VisibilityEmitEnabled` | `[WebRtcVoice]` | `false`, `false` | Send per-listener visibility (who may hear whom: estate and parcel bans, a parcel's "see avatars" setting, voice moderation) to a mixer that understands it. They need `JanusGatewayAdminURI` and `AdminAPIToken`. Mixer only. |
| `VisibilityTickMs`, `VisibilityRoomSendConcurrency` | `[WebRtcVoice]` | `250`, `4` | Milliseconds between recomputations, and how many rooms one send addresses at once. |
| `VisibilityHeartbeatMs` | `[WebRtcVoice]` | `1000` | Milliseconds between the heartbeats an armed region sends to the mixer, 250 to 4000. A region server that runs many regions can lengthen it; the mixer treats a region silent for its staleness window (`JS_VIS_STALE_MS`, 8000 by default) as gone. A value outside the range is refused with a warning. |
| `VisibilityArmingEnabled` | `[WebRtcVoice]` | `false` | Declare the sim as the visibility authority when a room is created, with epochs and heartbeats; the mixer withholds audio from a room whose authority goes silent. Needs both visibility keys above. Mixer only. |
| `JoinCapabilityEnabled` | `[WebRtcVoice]` | `false` | The region signs a join capability for every avatar join (60 seconds, bound to the agent, the viewer session and the room) and sends it to the mixer, which can refuse a join without a valid one (error 496). The capability goes from the region to the mixer and the viewer never sees it. Needs `JoinCapabilitySecret`. Mixer only: with the stock plugin the switch logs a warning and is turned off, because that plugin does not check a capability. |
| `JoinCapabilitySecret` | `[JanusWebRtcVoice]` | empty | The key the capability is signed with; it must equal the mixer's `JS_JOIN_CAP_SECRET`. At least 32 characters: with `JoinCapabilityEnabled` on, an empty or shorter value is refused at start with an error and no capability is minted. Never logged. Changing it needs the region server and the mixer changed together. |
| `VoiceModerationEnabled` | `[WebRtcVoice]` | `false` | Parcel voice moderation: the `SpatialVoiceModerationRequest` capability and the `voice moderation` console commands. On the stock AudioBridge a mute is that plugin's own mute of the participant in its room (a mute-all is remembered for the parcel and also applies to whoever joins afterwards). With the mixer it needs `VisibilityFeederEnabled`. |
| `PositionFeedEnabled`, `PositionFeedMode` | `[WebRtcVoice]` | `false`, `observe` | Tell the mixer where the region has each voice participant (see "The position feed" below). Needs both visibility keys above and a mixer that speaks `vis_protocol` 3 (the slvoice mixer, release 1.2.0 or later). `enforce` is accepted and sent as `observe`. Mixer only. |
| `PositionFeedTickMs` | `[WebRtcVoice]` | `500` | Milliseconds between looks at where the participants are (100 or more). The region checks every 250 ms, so a look comes 500 to 750 ms after the last one. |
| `PositionFeedKeepAliveMs` | `[WebRtcVoice]` | `2000` | A room's full set of positions is sent at the first look at or after this age. 1 to 2250; a value outside that logs a warning and 2000 is used. The mixer drops a point 6000 ms after it received it, so a larger value would let one lost request make a standing participant stale. |
| `PositionFeedMoveThresholdCm`, `PositionFeedMarginCm` | `[WebRtcVoice]` | `100`, `500` | Between full sets, a participant is sent again once it has moved this far; and the distance, in centimetres, a viewer's claimed position may differ from the region's before the mixer counts it as off. |
| `PositionFeedSpeedFloorCmS`, `PositionFeedSpeedMaxCmS` | `[WebRtcVoice]` | `800`, `10000` | The smallest speed allowance sent, and the speed ceiling used when the physics engine declares no maximum (both in centimetres a second). |
| `ConnectorsEnabled`, `AllowNpcVoice` | `[WebRtcVoice]` | `false`, `false` | Voice connectors: NPC presences, configured by `[VoiceConnector.<name>]` records, that join a parcel's voice room for a recorder or an injector program (see "Voice connectors" below). `AllowNpcVoice` lets any NPC presence provision voice, not only a registered connector. `ConnectorsEnabled` needs the slvoice mixer and the NPC module. Mixer only. |
| `NpcNameToken`, `VoiceRangeMetres` | `[WebRtcVoice]` | `NPC`, `20` | The word a connector NPC's first or last name must carry, and the range in metres within which an avatar is told, in local chat, that a connector is present. |

**Mixer-only switches.** The visibility switches, the join capability, the position feed, the voice connectors and call voice mutes in a group call need a Janus plugin that
implements the `janus.plugin.slvoice` protocol (the slvoice mixer, release 1.2.0 or later). The mixer is
legion-voice-mixer: its repository is https://github.com/JohnLegionH/legion-voice-mixer and its container image is
`ghcr.io/johnlegionh/legion-voice-mixer:1.2.0`. With the stock
AudioBridge, which is the default, a mixer-only switch that is on logs one warning at start naming the switch and the
plugin, and is turned off. Parcel voice moderation and call voice mutes work on the stock AudioBridge.

#### The position feed

A viewer reports its own position to the mixer, and nothing in the visibility feed says where the region has that
avatar. With `PositionFeedEnabled` the region sends the mixer, for every voice participant, the position the region
has for it, with a speed allowance and a tolerance, as a `peer_pos` request (mixer 1.2.0, `mixer-feed-protocol.md`
section 7.6). In 1.2.0 the mixer only **observes**: it compares each point with the position the viewer claims,
counts what a correction would have changed (`visibility.positions` in the admin API's `handle_info`) and changes
no audio and no roster. `PositionFeedMode = enforce` is accepted and sent as `observe`.

With the switch off nothing is read from the scene and nothing is sent. The region sends nothing until a reply from
the mixer has advertised `vis_protocol` 3, and it learns that itself; the feed needs no other switch for it, with
`VisibilityArmingEnabled` on or off. The version is read from the replies to the `peer_ctl_batch` and heartbeat
requests the region already sends. When it has sent none (arming off, and nobody excluded or muted) the region asks
once, with a `peer_ctl_heartbeat` that names no room: the mixer answers with its version and changes nothing in any
room. A mixer older than 1.2.0 is sent nothing more after that. The region asks again after a request to the mixer
fails (a restart), and a mixer that was replaced by a newer one is noticed from its next `peer_ctl` reply.

Cost. One request names one room. At each look the region sends, for every occupied room, at most one request: a
full set if `PositionFeedKeepAliveMs` has passed since the room's last full set, otherwise only the participants that
are new or have moved `PositionFeedMoveThresholdCm`, and nothing if there are none. A request carries up to 120
participants (the mixer accepts 128) at about 73 bytes each: 50 participants are 3,679 bytes and 110 are 7,999 bytes.
A room of the mixer's largest size (110) is therefore always one request.

| Room | Requests a minute for each occupied room |
|---|---|
| Someone moved 1 m or more since their last sent point (a room of 50 almost always) | 60,000 / 750 to 60,000 / 500 = 80 to 120 |
| Nobody moves, `PositionFeedKeepAliveMs` 2000 (a full set at every 3rd or 4th look, 2,000 to 2,750 ms apart) | 60,000 / 2,750 to 60,000 / 2,000 = 22 to 30 |

Four rooms of 50 are 320 to 480 requests a minute when busy and 87 to 120 when quiet. The rooms of one look are
sent one after another, so a look takes the number of occupied rooms times the round trip to the mixer; when that
is longer than `PositionFeedTickMs`, looks are skipped (one is in flight at a time) and the points age. On a region
server with many occupied rooms, lengthen `PositionFeedTickMs`.

What the mixer answered is counted: applied, and refused by reason (`stale_seq`, `unknown_room`, `too_big`, other).
The counts are in `show voice visibility` and in the feed's one-minute summary (a Debug line), and a refusal writes
one warning a minute.

#### Voice connectors

A voice connector is an NPC presence the region creates for an operator-written record, so that a program outside the
region server can take part in a parcel's voice: a recorder that listens, or an injector that speaks. The programs are
in the `connectors` directory of the mixer's repository (a Python recorder and injector); the region side
described here creates the NPC, makes sure its room exists and signs the join for the program. Connectors need the
slvoice mixer, release 1.2.0 or later. With the stock AudioBridge `ConnectorsEnabled` logs one warning at start and
stays off.

With `ConnectorsEnabled` off, which is the default, no `[VoiceConnector.*]` section is read, no NPC is created and no
endpoint is registered. With it on, each enabled `[VoiceConnector.<name>]` record (see the shipped ini) creates one NPC
at the record's `Position`. The NPC's agent id is derived from the grid id, the region name and the record name, so it
is the same after a restart; renaming the section or the region gives the connector a new id. A record is the
authorisation: only someone who can edit the ini can create one. `MayInject = false` records a connector that only
listens; its NPC is muted at registration.

The connector's room is the one an avatar standing at that position would be provisioned into: the parcel's own
channel, or the estate channel when the parcel uses it. The region creates the room when the record is registered and
again at every fetch below, the same way a viewer's provision creates it, so the connector's program has a room to join
before any avatar arrives. That is one extra room-create request to the mixer for each connector each time its
program fetches a capability.

Disclosure cannot be switched off. The region sends a region-wide alert when a connector attaches or detaches, an
entry notice to each avatar that becomes a root agent in a region with a connector, and a local-chat notice, spoken
as the NPC, to an avatar that comes within `VoiceRangeMetres` of it. The NPC's name must carry `NpcNameToken`.

**The join capability endpoint.** A connector's program joins the mixer from its own environment, so the capability
the region signs for an avatar's join never reaches it. For a record that has a `CapabilitySecret`, the region answers

    POST /voice/connector/<name>/join-cap
    Authorization: Bearer <the record's CapabilitySecret>

with `200` and a JSON object holding `display`, `room`, `session_id`, `join_cap`, `expires` and `position` (`x`, `y`, `z`
in global centimetres, the frame a viewer reports). The program fetches one before every join. Every failure before
the bearer is accepted (unknown name, a record with no secret, a wrong or missing bearer, any other method or path) is
a `404` with an empty body, so the endpoint does not say which names exist. After that, `503` with an empty body means
the region cannot mint a capability, which is the case unless `JoinCapabilityEnabled` is on and `JoinCapabilitySecret` is
set. The capability is the one `JoinCapabilityEnabled` describes; the mixer enforces it when it runs with
`JS_JOIN_CAP_REQUIRED=1`. A `CapabilitySecret` shorter than 32 characters is dropped with a warning. The handler is
registered only while at least one record carries a secret. On a server that hosts several regions, a record with a
secret needs `Region=` to pin it to one of them, otherwise the endpoint cannot tell which region to use and answers
`404`. The secret, the bearer and the capability are never logged.

The console commands `voice connector start <name>` and `voice connector stop <name>` start or stop one record without
a region restart.

#### Which parcel a voice request is checked against

A stock viewer sends `parcel_local_id` only when the avatar stands on a parcel that has its own voice channel (the
parcel's "use estate voice channel" option is off). On a parcel that uses the estate voice channel it sends none,
and it sends none on the connection it opens to each neighbouring region.

For an avatar that is in the region, the parcel is the one the avatar is standing on. The viewer's
`parcel_local_id` is not used to choose it: a different id is logged and ignored. Voice applies the parcel's
voice-off setting and ban list itself, on every estate, including TaxFree estates: a parcel with voice turned off
refuses voice (HTTP 403), and an avatar on the parcel's ban list is refused (HTTP 403), whether or not the parcel
uses the estate voice channel. The parcel's owner, the region's estate managers and owner, and administrators are
not refused by the ban list.

The avatar's room follows from that parcel. A parcel on the estate voice channel gets the region-wide room; a
parcel with its own voice channel gets that parcel's room.

For the viewer of an avatar that is in a neighbouring region, the named parcel is used only if it is near where
that avatar is known to be; otherwise the request is treated as one that named no parcel and gets the region-wide
room.

<a id="Configure_Robust"></a>
## Configure Robust Server for WebRTC Voice

For the grid services side, `os-webrtc-janus` is configured as an additional service
in the Robust OpenSimulator server. The additions to `Robust.ini` are:

```
...
[ServiceList]
    ...
    VoiceServiceConnector = "${Const|PrivatePort}/WebRtcVoice.dll:WebRtcVoiceServerConnector"
    ...

[WebRtcVoice]
    Enabled = true
    SpatialVoiceService = WebRtcJanusService.dll:WebRtcJanusService
    NonSpatialVoiceService = WebRtcJanusService.dll:WebRtcJanusService
[JanusWebRtcVoice]
    JanusGatewayURI = http://janus.example.org:14223/voice
    APIToken = APITokenToNeverCheckIn
    JanusGatewayAdminURI = http://janus.example.org/admin
    AdminAPIToken = AdminAPITokenToNeverCheckIn
...
```

This adds `VoiceServiceConnector` to the list of services presented by this Robust server
and adds the WebRtcVoice configuration that says to do both spatial and non-spatial voice
using the Janus server, and the configuration for the Janus server itself.

One can configure multiple Robust services to distribute the load of services
and a Robust server with only `VoiceServiceConnector` in its ServiceList is possible.

<a id="Configure_Session_Store"></a>
## Sharing call sessions between region servers

Group, conference and avatar-to-avatar calls are sessions kept by the region server. A grid with
more than one region server process has one record of each call per process unless the processes
share a store: each process counts its own seat limit, each rings the group itself, and a call
between people on different processes depends on `CarrierSecret`. The grid voice session service
keeps one record of every call instead. It is off by default and nothing changes until both sides
are configured.

In `Robust.ini` (or `GridServer.ini`), add the connector to `[ServiceList]` and enable the service.
The service refuses to load without HTTP basic authentication and a password of at least 32 characters:

```
[ServiceList]
    VoiceSessionConnector = "${Const|PrivatePort}/WebRtcVoice.dll:VoiceSessionServerConnector"

[WebRtcVoiceSessionService]
    Enabled = true
    AuthType = "BasicHttpAuthentication"
    HttpAuthUsername = "voice-sessions"
    HttpAuthPassword = "a-password-of-at-least-32-characters-kept-out-of-version-control"
```

In the voice settings of every region server, with the same user and password:

```
[WebRtcVoice]
    SessionStoreMode = grid
    SessionStoreServerURI = http://robust.example.org:8003
    AuthType = "BasicHttpAuthentication"
    HttpAuthUsername = "voice-sessions"
    HttpAuthPassword = "a-password-of-at-least-32-characters-kept-out-of-version-control"
```

Set it on all region servers or none: a call started on a server that keeps its own record is not
seen by the others. Calls are kept in the service's memory and end when Robust restarts.

- A region that cannot reach the service refuses new calls rather than keeping its own record. A
  viewer asking to start or join an avatar-to-avatar call while the service is unreachable is answered
  "service unavailable" (503) and may try again, and only a service that says the call does not exist
  gives "not found". A group or conference join is refused.
- A region checks the service once when it starts and logs whether it answered. A wrong address, a
  Robust without the service and a wrong password are all reported there.
- Seats are renewed by the region that holds them and released by the service when the region stops
  renewing them, or when it restarts.
- Group and conference rings are checked by the service, and conference text lines are signed with a key the
  service keeps for each conference and gives only to its members, so neither needs `CarrierSecret`. An
  avatar-to-avatar ring, join, decline or hang-up that arrives from another region server is acted on
  only when the service says it is true.
- A call is not ended for being idle while a region is still renewing a seat in it.

The console command `show voice sessions` prints where the region keeps its sessions, the service
address, whether it answered, the circuit breaker state, and the seats and queued departures this
region holds.

<a id="Configure_Standalone"></a>
## Configure Standalone Region

[OpenSimulator] can be run "standalone" where all the grid services and regions are
run in one simulator instance. Adding voice to this configuration is sometimes useful
for very private meetings or testing. For this configuration, a Janus server is set up
and the standalone simulator is configured to point all voice to that Janus server:

```
[WebRtcVoice]
    Enabled = true
    SpatialVoiceService = WebRtcJanusService.dll:WebRtcJanusService
    NonSpatialVoiceService = WebRtcJanusService.dll:WebRtcJanusService
    WebRtcVoiceServerURI = ${Const|PrivURL}:${Const|PrivatePort}
[JanusWebRtcVoice]
    JanusGatewayURI = http://janus.example.org:14223/voice
    APIToken = APITokenToNeverCheckIn
    JanusGatewayAdminURI = http://janus.example.org/admin
    AdminAPIToken = AdminAPITokenToNeverCheckIn
```

This directs both spatial and non-spatial voice to the Janus server
and configures the URI address of  the Janus server and the API access
keys for that server.

<a id="Managing_Voice"></a>
## Managing Voice (Console commands)

There are a few console commands for checking on and controlling the voice system.
The current list of commands for the simulator can be listed with the
console command `help webrtc`.

This is a growing section and will be added to over time.

**webrtc list sessions** -- not implemented

**janus info** -- list many details of the Janus-Gateway configuration. Very ugly, non-formated JSON.

**janus list rooms** -- list the rooms that have been allocated in the `AudioBridge` Janus plugin

**show voice visibility** -- show the visibility feeder: whether it runs, its rooms, and what it last sent to the mixer; with the position feed on, also what the mixer has answered to the position requests

**show voice moderation** -- list the parcel voice mutes held by the regions that run the visibility feeder

**voice moderation mute** `<agent-uuid-or-name> [parcel-local-id]` and **voice moderation unmute** `<agent-uuid-or-name>` --
mute or unmute one avatar's voice on a parcel of the selected region, from the console. Both need
`VisibilityFeederEnabled` and so the mixer; on the stock AudioBridge moderation is done from the viewer

**show voice sessions** -- show where this region keeps its call sessions (in this process, or in the grid
voice session service) and, for the service, whether it is answering

<a id="Quieting_Voice_In_The_Log"></a>
## Quieting voice in the log

Every voice class logs under the one category prefix `osWebRtcVoice`, so one line of logging configuration sets the
level for all of them. At the default console level (`Information`) the voice code writes its start-up lines, a line for
each call removal, and, for a viewer that keeps being refused a provision, one line for the first refusal and one with the
count when the run ends. To raise voice to warnings and errors only, add this line to the `Logging:LogLevel`
section of the region server's `appsettings.json` (the file in the folder the region server is started from):

```json
"osWebRtcVoice": "Warning"
```

so that the section reads:

```json
"Logging": {
  "LogLevel": {
    "Default": "Information",
    "osWebRtcVoice": "Warning",
    "Microsoft": "Warning",
    "System": "Warning"
  }
}
```

The other categories keep their levels. The region server reads `appsettings.json` when it starts, so restart it for
the change to apply. To see everything the voice code does while chasing a fault, set the same line to `"Debug"`.

Which output the line reaches depends on whether pull request #367 (category log settings reaching the log file, and
`set log level` working) is in your build. With #367, the line applies to the console and to the log file, and the log
file otherwise keeps Debug and above. Without it, the line quiets the console only and the log file still receives every
voice line down to Debug. A category entry also wins over `set log level` for that category.

[SecondLife WebRTC Voice]: https://wiki.secondlife.com/wiki/WebRTC_Voice
[OpenSimulator]: http://opensimulator.org
[OpenSimulator Community Conference]: https://conference.opensimulator.org
[os-webrtc-janus]: https://github.com/Misterblue/os-webrtc-janus
[Janus-Gateway WebRTC server]: https://janus.conf.meetecho.com/
[os-webrtc-janus-docker]: https://github.com/Misterblue/os-webrtc-janus-docker
