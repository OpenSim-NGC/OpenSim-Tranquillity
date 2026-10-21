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

#### Optional settings

These keys are optional. The shipped `os-webrtc-janus.ini` lists them commented out with their defaults.

| Key | Section | Default | Notes |
|---|---|---|---|
| `PluginName` | `[JanusWebRtcVoice]` | `janus.plugin.audiobridge` | The Janus plugin the region attaches voice handles to. The default is Janus's stock AudioBridge. `janus.plugin.slvoice` selects a spatial mixer that implements that plugin. |
| `RequestTimeoutMs` | `[JanusWebRtcVoice]` | `5000` | Milliseconds a Janus request that is answered with `ack` waits for its result before it fails. Before this setting such a request waited without limit. |
| `RemoveEmptyRooms`, `EmptyRoomGraceSeconds` | `[JanusWebRtcVoice]` | `true`, `60` | Remove a room this service created once it has stood empty for the grace period. The stock AudioBridge never removes an emptied room. Rooms the service did not create are never removed, and a plugin other than the stock AudioBridge is expected to remove its own. |
| `RefusalCacheSeconds` | `[WebRtcVoice]` | `5` | Seconds a refused voice provision is remembered, so the viewer's immediate retries are answered without repeating the estate and parcel checks. `0` turns it off. |
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
