# WebRTC voice (os-webrtc-janus): overview

This guide is for grid operators. It describes what the `os-webrtc-janus` addon does, which voice server to run
it against, how to switch it on, and what each optional feature needs. Every key is listed with its default in
[configuration.md](configuration.md). The other documents in this directory, and the order to read them in, are
listed in [README.md](README.md).

The voice code reaches the tree as a series of pull requests, and every feature in it is behind a setting that is
off by default. This guide names the pull request that adds each feature, as #<number>, so that you can tell what
your tree contains: a feature whose pull request your tree does not have has no setting to switch on, and the
documents describe it anyway.

| Pull request | Adds |
|---|---|
| #345 | The start-up fix: 64-bit Janus session ids, and one DLL serving both service roles |
| #346 | Groups ignore chat lines for sessions that are not groups; an NPC create refuses an agent id already in use |
| #347 | `PluginName` (choose the Janus plugin) and `StunServers` |
| #348 | Hardening: request timeouts, clean-up after failures, room handling, the refusal cache, removal of emptied rooms |
| #349 | Secrets kept out of the log, a warning for weak secrets, `JanusGatewayCACertificateFile` |
| #350 | The parcel under the avatar decides voice, on every estate |
| #351 | Three optional engine and chat hooks: `ChatterBoxSessionUpdate`, `IGroupChatGate`, `PhysicsScene.MaximumLinearVelocity` |
| #352 | Project references, so the region server and the grid server build the voice assemblies they load |
| #353 | Avatar-to-avatar calls (`A2AVoiceEnabled`) |
| #354 | Group voice (`GroupVoiceEnabled`), and signed rings between region server processes (`CarrierSecret`) |
| #355 | Ad-hoc conferences, voice and text (`AdhocVoiceEnabled`) |
| #356 | The call session service on the grid server (`SessionStoreMode = grid`) |
| #357 | Call moderation (`CallModerationEnabled`) |
| #358 | Quieter logs and fewer background lookups |
| #362 | Per-listener visibility for a slvoice mixer, and parcel voice moderation |
| #363 | The join capability (`JoinCapabilityEnabled`) and the 32-character minimum on its secret |
| #365 | NPC voice connectors (`ConnectorsEnabled`) |
| #364 | The position feed, in observe mode (`PositionFeedEnabled`) |

## What the addon does

Viewers that support WebRTC voice ask the region for a voice session. The addon decides whether that avatar
may have voice on that parcel (estate and parcel voice settings, bans), then creates the session on an
external [Janus Gateway](https://janus.conf.meetecho.com/) server and returns the connection details to the
viewer. Audio never passes through the region server: it flows between the viewer and Janus.

Through one Janus gateway the addon provides:

- **Spatial voice** for a parcel or an estate channel: everyone in the same room can hear each other.
- **Avatar-to-avatar calls**: the Call button in an IM window.
- **Group calls**: the call button in a group's chat window.
- **Ad-hoc conferences**: an IM conference of several people, with voice and with conference text.
- **Moderation**: of a parcel's voice, and of group calls and conferences.
- With a spatial mixer only: **per-listener visibility**, a **join capability**, a **position feed** and **NPC voice
  connectors**.

What each needs switched on (every optional switch is `false` unless the table says otherwise):

| Feature | Added by | Switches | Also needs |
|---|---|---|---|
| Spatial voice | #345, #347 to #350 | `[WebRtcVoice] Enabled = true`, the service keys, and the four `[JanusWebRtcVoice]` values (gateway and admin URI and secret) | A Janus gateway; `StunServers` for viewers outside your network |
| Avatar-to-avatar calls | #353 | `A2AVoiceEnabled` | - |
| Group calls | #354 | `GroupVoiceEnabled` | The Groups module |
| Conference voice and text | #355 | `AdhocVoiceEnabled` | The Groups module, for the text; #346 |
| Group and conference calls between region server processes | #354, #356 | `SessionStoreMode = grid`, or `CarrierSecret` | See [deployment.md](deployment.md) |
| Group and conference call moderation | #357 | `CallModerationEnabled` | `GroupVoiceEnabled` or `AdhocVoiceEnabled`; #351 |
| Parcel voice moderation | #362 | `VoiceModerationEnabled` | Stock AudioBridge: nothing more. Slvoice mixer: `VisibilityFeederEnabled` |
| Visibility | #362 | `VisibilityFeederEnabled`, `VisibilityEmitEnabled` | Slvoice mixer |
| Visibility arming | #362 | `VisibilityArmingEnabled` | Slvoice mixer, feeder, emission |
| Join capability | #363 | `JoinCapabilityEnabled`, `JoinCapabilitySecret` | Slvoice mixer |
| Position feed | #364 | `PositionFeedEnabled` | Slvoice mixer 1.2.0 or later (`vis_protocol` 3), feeder, emission; #351 |
| NPC voice connectors | #365 | `ConnectorsEnabled` | Slvoice mixer, the NPC module; #346 |

You must run Janus yourself. See the `os-webrtc-janus-docker` project for running the stock Janus.

## Choosing a voice server

The region attaches to one Janus plugin, chosen by `PluginName` in `[JanusWebRtcVoice]`.

| Plugin | Setting | What you get |
|---|---|---|
| Stock AudioBridge | `janus.plugin.audiobridge` (the default) | Flat mixing: everyone in a room hears everyone at the same level, with no distance falloff or panning. Rooms are still separate per parcel, per estate channel, per call and per conference. |
| Spatial mixer ("slvoice mixer") | `janus.plugin.slvoice` | A mixer that implements the `janus.plugin.slvoice` protocol: spatial mixing, and the per-listener features marked below. The protocol is defined by the mixer, not by this addon. Any plugin name other than the stock one is taken to be such a mixer. |

Which feature works with which plugin:

| Feature | Switch | Stock AudioBridge | Slvoice mixer |
|---|---|---|---|
| Spatial voice | `Enabled` | Works, flat mix | Works, spatial |
| STUN servers advertised to viewers | `StunServers` | Works | Works |
| Avatar-to-avatar calls | `A2AVoiceEnabled` | Works | Works |
| Group calls | `GroupVoiceEnabled` | Works | Works |
| Conference voice and text | `AdhocVoiceEnabled` | Works | Works |
| Call moderation: text mute, roster flags, removal | `CallModerationEnabled` | Works | Works |
| Call moderation: voice mute | `CallModerationEnabled` | The plugin's own mute of one participant. A mute the plugin cannot carry out is refused (501 or 503) and not recorded | The mixer's mute channel |
| Parcel voice moderation | `VoiceModerationEnabled` | The plugin's own mute of one participant. The console commands find nothing | Carried by the visibility feeder; answered 501 and not recorded if the feeder is off |
| Visibility (bans, "see avatars", moderation) | `VisibilityFeederEnabled`, `VisibilityEmitEnabled` | Switched off at start, with a warning | Works |
| Visibility arming | `VisibilityArmingEnabled` | Switched off at start, with a warning | Works |
| Join capability | `JoinCapabilityEnabled` | Switched off at start, with a warning | Works |
| Position feed | `PositionFeedEnabled` | Switched off at start, with a warning | Works at `vis_protocol` 3 |
| NPC voice connectors | `ConnectorsEnabled` | Switched off at start, with a warning | Works |
| Removal of emptied rooms | `RemoveEmptyRooms` | The addon removes rooms it created | The mixer removes its own |

"Switched off at start, with a warning" means: with the stock plugin, a mixer-only switch that is `true` logs a
warning naming the switch and the plugin, and is treated as `false`; nothing else about it runs. Only the slvoice
mixer enforces who can hear whom. With the stock plugin every listener in a room hears every source, whatever the
visibility switches say.

The region server reads `PluginName` from its own `[JanusWebRtcVoice]` section even when Janus is reached through a
grid service, so set it there as well. Without it the region assumes the stock plugin and switches the mixer-only
features off.

## The slvoice mixer

The mixer is a Janus plugin (`janus.plugin.slvoice`) in a repository of its own, https://github.com/JohnLegionH/legion-voice-mixer, with the container image `ghcr.io/johnlegionh/legion-voice-mixer:1.2.0`. Everything marked "slvoice mixer" in this guide needs release
**1.2.0 or later**; the mixer's own documentation is the reference for running it. The region server is one half of
each such feature and the mixer is the other, so the two have to be kept in step. What 1.2.0 sets, and which part of the
region server depends on it:

| Mixer 1.2.0 | What the region server does about it |
|---|---|
| A room admits **50 participants** by default (`JS_ROOM_SIZE`, 2 to 110; 0.7.0 allowed 64). A join beyond the limit is refused as room full | The region answers it as HTTP 409, the viewer's "channel full". `GroupVoiceCap` (#354) defaults to 50 to match, and is never above 110 |
| The Opus encoder complexity is **5** (0.7.0 used 9). It is a build-time setting of the mixer | None. The mixer's release notes give the CPU cost of a tick for 50, 64 and 80 participants; check the mixer's tick histogram under real load before raising `JS_ROOM_SIZE` |
| `peer_ctl_batch` is refused whole above 65,536 bytes (`too_big`) | The region sends a room's request in parts, cut between listeners, when it would be over that size (#362). A request that fits is sent as before |
| `peer_pos` and `vis_protocol` 3 | The position feed sends nothing to a mixer that does not advertise 3. Its full snapshot is sent every **2000 ms** by default, so a point stays fresh in the mixer's 6000 ms stale window after one lost request (#364, [positions.md](positions.md)). The feed learns the mixer's version without `VisibilityArmingEnabled`: when no `peer_ctl` request has told it, it asks once with a heartbeat that names no room, which arms nothing |
| Accepts 128 `peer_pos` entries in a request | The region sends at most 120 |
| Join capability and `JS_JOIN_CAP_REQUIRED` | The region signs the capability (#363). The mixer refuses a join without one only when started with `JS_JOIN_CAP_REQUIRED=1` |
| Visibility staleness window `JS_VIS_STALE_MS`, 8000 ms by default | `VisibilityHeartbeatMs` (#362) may be 250 to 4000, half of that window |

The mixer has enforcing modes of its own (`JS_VIS_FAIL_CLOSED`, `JS_JOIN_CAP_REQUIRED`) that are off by default and
count what they would do. The region server's settings do not turn them on.

Position **enforcement** is not available yet: the position feed runs in observe mode only (see
[positions.md](positions.md)).

## Enabling voice

The addon ships in this tree and is built with the region server. Copy `Addons/os-webrtc-janus/os-webrtc-janus.ini`
into the region server's `config` folder (the folder named by `[Startup] inidirectory`), then edit it.

Minimal region-local set-up, stock plugin:

```ini
[WebRtcVoice]
    Enabled = true
    SpatialVoiceService = WebRtcJanusService.dll:WebRtcJanusService
    NonSpatialVoiceService = WebRtcJanusService.dll:WebRtcJanusService
    ; Viewers need at least one STUN server to build a connection
    StunServers = stun:stun.example.org:3478

[JanusWebRtcVoice]
    JanusGatewayURI = http://janus.example.org:14223/voice
    APIToken = <the Janus API secret>
    JanusGatewayAdminURI = http://janus.example.org:14225/admin
    AdminAPIToken = <the Janus admin secret>
```

The four Janus values must all be set or the Janus service disables itself, and the secrets must match the
ones configured on the Janus server. Use random secrets of at least 32 characters. Check the region log at start-up:
the lines to look for are in [troubleshooting.md](troubleshooting.md).

To use a slvoice mixer add `PluginName = janus.plugin.slvoice` in `[JanusWebRtcVoice]` and use that mixer's
own documentation to run it.

Switch the call features on one at a time, with the same file:

```ini
[WebRtcVoice]
    A2AVoiceEnabled = true
    GroupVoiceEnabled = true
    AdhocVoiceEnabled = true
```

If your regions run in more than one region server process, read [deployment.md](deployment.md) before you do.

Grid-wide Janus service: the `WebRtcVoiceServerConnector` that hosts the Janus service on Robust loads
`WebRtcVoiceServiceModule.dll` and `WebRtcJanusService.dll`. Since #352 the grid server project references those
projects as well as `WebRtcVoice`, so a grid server built from the tree contains them; nothing loads them unless
`VoiceServiceConnector` is in the grid server's `[ServiceList]` (see the add-on README, "Configure Robust"). The
set-ups in this guide run the Janus service in the region server process. What Robust hosts in them is the call
session service (#356), which is described in [deployment.md](deployment.md).

## Optional features

Every switch is in `[WebRtcVoice]`, is commented out in the shipped file, and defaults to off. Turning one on
affects only that feature.

### STUN servers

`StunServers` is a comma-separated list of full ICE URIs. It is advertised to viewers as the `stun-servers`
simulator feature. Stock viewers need at least one STUN server to connect from outside the grid's network, and
fail to create a peer connection without a valid entry, so set it unless the core server already advertises one.
If the core server's own `StunServers` setting is also set, the core server's value is the one viewers receive.
The shipped `os-webrtc-janus.ini` lists it commented out with a public server as the example
(`stun:stun.l.google.com:19302`). Empty (the default) adds nothing. Works with either plugin.

### Avatar-to-avatar calls

`A2AVoiceEnabled` (#353). Handles the viewer's "call" and "start p2p voice" requests: the region records the
invitation, delivers it to the callee, and provisions a two-party voice session for the pair. A call is private
to its two avatars: only they, holding the call's token, are admitted to its room. With the switch off the chat
session handler behaves as it does without the feature, and the Call button does not work.

The invitation is delivered directly to a callee who has a root presence in the caller's own region server process.
A callee whose region is hosted by another process (or who is only a child presence in the caller's process) is rung
through a carrier message, which #354 adds:

- With the default `SessionStoreMode = local` the message is signed with `CarrierSecret`, set to the same value on
  every region server (see configuration). Without it a callee in another process is not rung and the caller's call
  rings out.
- With `SessionStoreMode = grid` (#356) no secret is needed: the receiving region asks the session service whether the
  call exists and whether the join, decline or hang-up it was told about is true, and acts on the service's answer.

With #353 alone nothing crosses between processes.

### Group calls

`GroupVoiceEnabled` (#354), with `GroupVoiceRequireVoicePower` (default `true`: the role needs both the join and the
voice power) and `GroupVoiceCap` (default 50 seats, never more than 110; the slvoice mixer admits 50 by default and
110 at most, see [The slvoice mixer](#the-slvoice-mixer)). Needs the Groups module. The powers are checked again at
the provision, because they can be revoked after the call started. Seats are released when a member leaves or their
presence closes. The first seat rings the group's members who are online and hold the powers; those whose region is
in another process are rung through the carrier message described above.

### Ad-hoc conferences

`AdhocVoiceEnabled` (#355; it needs #346 so that the groups module leaves a conference's chat lines alone). Supports
"start conference": the region starts a conference session, rings the invitees, handles accepts and declines, and
relays the conference text and roster. Being invited is the membership: a provision is admitted only for an invited
member holding the conference's token. A conference line is delivered to the other members who have accepted, with no
echo to the sender, and is not stored for a member who is offline. Needs the Groups module for the conference text
session. With the switch off, "start conference" is answered with an empty 200 reply, as before.
`ConferenceRequestBurst` and `ConferenceRequestRefillSeconds` limit how fast one agent can start conferences and invite
people. Voice, the invitation and the text are three separate lifecycles: chat membership ends when the member leaves the
conference window, an invitation ends with a decline, and a voice seat ends when the voice connection is torn down.

### Visibility

Lets the region decide who may hear whom and tell the mixer (#362).

- `VisibilityFeederEnabled` runs a per-region process that recomputes, every `VisibilityTickMs` (250), whether
  each listener may hear each source: estate bans, parcel bans and restrictions, parcels that hide avatars, and
  voice moderation. With emission off it only computes (useful for diagnostics). Console: `show voice visibility`
  prints the counters of the most recent send per region.
- `VisibilityEmitEnabled` sends the result to the mixer as `peer_ctl` batches over the Janus admin API. It needs
  `JanusGatewayAdminURI` and `AdminAPIToken`; without them the region logs a warning and stays compute-only.
  `VisibilityRoomSendConcurrency` (4) is how many rooms one send addresses at once. A room whose request would be
  over the mixer's size limit is sent in several parts.
- `VisibilityArmingEnabled` additionally makes the sim the declared visibility authority for each spatial room,
  using epochs and heartbeats. The mixer withholds audio from a room whose authority goes silent, only when the mixer
  itself runs with `JS_VIS_FAIL_CLOSED=1`; otherwise it counts what it would do. It needs the feeder and emission to be
  on as well. `VisibilityHeartbeatMs` (default 1000, 250 to 4000) sets the interval between heartbeats; a region server
  with many regions may lengthen it.

All three need the slvoice mixer.

### Join capability and position feed

`JoinCapabilityEnabled` (#363): the region signs a short-lived (60 seconds) capability for every join and the mixer can
refuse joins that lack one. The secret must be at least 32 characters. See [security.md](security.md).

`PositionFeedEnabled` (#364): the region tells the mixer where it has each voice participant, in observe mode
only. See [positions.md](positions.md). Both need the slvoice mixer 1.2.0 or later.

### Moderation

`VoiceModerationEnabled` (#362) for a parcel's voice and `CallModerationEnabled` (#357) for group calls and
conferences. A group call's voice mute reaches a slvoice mixer from #362 on; with #357 alone the voice mute is carried
out on the stock AudioBridge only. See [moderation.md](moderation.md).

### NPC voice connectors

`ConnectorsEnabled` (#365). An operator-defined NPC can stand at a fixed position as a recorder (listen only)
or injector (may speak, for example text to speech). Each connector is a `[VoiceConnector.<name>]` section; the
section is the authorisation, so only someone with access to the configuration can create one. The recorder and
injector programs are in the mixer's repository (`connectors/`); the region side creates the NPC, makes sure its room
exists and signs the programs' join. Related switches: `NpcNameToken` (the word every connector NPC's name must
contain, default `NPC`), `VoiceRangeMetres` (default 20) and `AllowNpcVoice` (default `false`: an NPC is refused voice
unless it is a registered connector). Disclosure cannot be disabled: connector NPCs carry the name token, and the
region posts notices when they appear and when avatars come close. Needs the NPC module (#346 makes a create of an
agent id already in use fail cleanly) and the slvoice mixer. The room is made to exist before anyone has joined it
(`EnsureSpatialRoom`); the grid-service client (`WebRtcVoiceServiceConnector`) owns no rooms, so with it the room is
created only when an avatar has joined that parcel, and a warning says so. Grid mode was not run with connectors. See [configuration.md](configuration.md) for the record
keys.

### Log volume

#358 makes the voice code write fewer lines in normal use: a run of refused provisions is two lines, the end of a
Janus long poll is a Debug line, and repeated refusal warnings are limited to one a minute with a count. To see only
voice warnings and errors, set the `osWebRtcVoice` logger to `Warning` in `appsettings.json`; the add-on README
section "Quieting voice in the log" has the whole `LogLevel` block.

## Upgrading from develop

With every optional switch off, the addon handles voice as the develop branch does, with these differences. Each is a fix to
something that did not work or had no limit; each has a setting where one makes sense.

| What changed | Setting and default | Reason |
|---|---|---|
| A refused or failed voice provision is remembered for 5 seconds, and the same answer is replayed to that viewer's immediate retries (#348) | `RefusalCacheSeconds = 5`; `0` turns it off | The viewer asks again at once after a failure. The cache answers those retries without repeating the estate and parcel checks or calling Janus |
| A Janus request answered with "ack" fails after 5000 ms if its result never arrives, and an admin request after 5000 ms (#348, #362) | `RequestTimeoutMs = 5000`, `AdminTimeoutMs = 5000` | A result that never came used to hold the provision indefinitely |
| A room the addon created on the stock AudioBridge is removed once it has been empty for 60 seconds (#348) | `RemoveEmptyRooms = true`, `EmptyRoomGraceSeconds = 60` | The stock plugin never removes an emptied room, so every estate, parcel, call and conference room stayed on the gateway until Janus restarted. Rooms the addon did not create are never removed; the slvoice mixer removes its own |
| A provision whose `channel_type` is neither `local` nor `multiagent` is answered 403 (#353) | none | develop answered 500 for these. With any call feature on, a provision with no `channel_type` and no logout is also answered 403; with all call features off it goes to the voice service as before |
| A logout for a viewer session the voice service does not know is answered `closed` (#348) | none | develop answered an empty 200 |
| Voice on a parcel is decided by the parcel under the avatar, and the parcel's voice-off setting and ban list apply on every estate, including a TaxFree one (#350) | none | develop took the parcel from the `parcel_local_id` the viewer sent and did nothing when it sent none, which is the case for a parcel that uses the estate voice channel (the stock viewers' default); it also skipped the voice-off check on a TaxFree estate. The viewer's id is now only a hint, logged when it differs from the server's |
| A `multiagent` provision takes its room from the request's `channel` (with `channel_id` as the fallback), and the room number includes the grid's identity, read from `GatekeeperURI` (#353) | none | The viewer sends `channel`, so reading only `channel_id` put every such provision in one room, and two grids on one gateway shared it. If no gatekeeper address is set, the number is derived without the grid part and a warning is logged |
| A provision into a full room is answered HTTP 409 (#348) | none | The viewer maps 409 to its "channel full" message |
| API and admin secrets under 32 characters log a warning at start (#349) | none | The services start as before. A join-capability secret under 32 characters is refused when the capability is on (#363) |

Settings that keep develop's behaviour: the Janus plugin stays `janus.plugin.audiobridge`, and the visibility,
arming, capability, moderation, group, call, conference and connector features stay off. `AllowNpcVoice = false` matches
the stock server, which gives an NPC no way to reach voice. The shipped `os-webrtc-janus.ini` differs from develop's only
by commented-out keys.

## Known limits

- **TURN is not delivered.** The addon can advertise STUN servers only. It has no setting for TURN, so stock
  viewers behind networks that block direct UDP will not get a relay from the addon.
- **Call sessions are held in memory.** Call invitations, group and conference seats, and moderation state are not
  stored in a database. With `SessionStoreMode = local` they are lost when the region server process restarts; with
  `grid` they are lost when the grid server restarts. See [deployment.md](deployment.md).
- **Avatar-to-avatar calls across processes, with `SessionStoreMode = local`** (#354): two people calling each other at
  the same instant from two processes both ring out (each process already holds a call of its own for the pair), and a
  call whose two people leave in different processes can wait for the idle sweep (8 hours) in the process of the one
  who left first. `SessionStoreMode = grid` (#356) has neither: one record serves both processes.
- **Only the slvoice mixer enforces visibility.** With the stock plugin every listener in a room hears every
  source, whatever the visibility switches say.
- **The stock plugin has no spatial audio.** It mixes a room flat.
- **Room size.** A slvoice mixer room admits 50 participants by default (`JS_ROOM_SIZE`) and at most 110; the addon never
  seats more than 110 in a group or conference session, with either plugin, and `GroupVoiceCap` defaults to 50.
- **Positions are observed, not enforced.** The position feed only lets the mixer count how often a viewer's claimed
  position differs from the region's. Enforcement is not available yet.
- **Viewer crash.** A known viewer crash follows a Call started from the viewer's nearby-voice list. Start calls from
  the IM window or the group window. See [troubleshooting.md](troubleshooting.md).
- **No audio from the region.** If viewers connect but hear nothing, check the Janus server's public address
  and its UDP media port range in your firewall; the region server cannot see the media path. See
  [deployment.md](deployment.md).
