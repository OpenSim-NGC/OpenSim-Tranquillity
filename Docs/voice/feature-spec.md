# WebRTC Voice Service: Feature Specification

This document specifies what the WebRTC voice support in `os-webrtc-janus` provides, what it
requires of a spatial mixer, and which switches control each feature. It targets OpenSimulator-derived
grids that want voice compatible with the published Second Life WebRTC viewer protocol
(see the Second Life wiki page "WebRTC Voice").

The addon is the region-server half. The mixer is a separate Janus plugin (release 1.2.0 or later of the slvoice mixer for the
features marked "slvoice mixer") and is not part of this repository. Each statement below is marked as one of:

- **Implemented**: confirmed in the addon code.
- **Mixer requirement**: what the addon expects a mixer to do. Not verifiable from this repository; the mixer's own
  documentation says what it does.

Architecture and configuration details are in [architecture.md](architecture.md). The sim-to-mixer
messages are in [mixer-feed-protocol.md](mixer-feed-protocol.md).

## 1. Defaults

Every optional feature is **off** unless its switch in `[WebRtcVoice]` is set, and `Enabled` itself is
`false`. A deployment that sets only `Enabled = true` and the Janus endpoints gets plain voice through
the Janus plugin named by `[JanusWebRtcVoice] PluginName`, which defaults to `janus.plugin.audiobridge`
(Janus's stock AudioBridge: flat, non-spatial mixing). Set `PluginName = janus.plugin.slvoice` to use a
spatial mixer implementing that plugin. STUN servers are advertised to viewers only when `StunServers`
is set.

| Feature | Added by | Switch (all default `false`) | Needs |
|---|---|---|---|
| Avatar-to-avatar calls | #353 | `A2AVoiceEnabled` | - |
| Group voice | #354 | `GroupVoiceEnabled` | Groups module |
| Conference voice and text | #355 | `AdhocVoiceEnabled` | Groups module for text |
| Calls between region server processes | #354, #356 | `SessionStoreMode = grid`, or `CarrierSecret` | the call session service on Robust, or the same secret on every region server |
| Group and conference call moderation | #357 | `CallModerationEnabled` | `GroupVoiceEnabled` or `AdhocVoiceEnabled` |
| Visibility feed (compute) | #362 | `VisibilityFeederEnabled` | spatial mixer, admin endpoint |
| Visibility feed (send) | #362 | `VisibilityEmitEnabled` | feeder on |
| Authority arming | #362 | `VisibilityArmingEnabled` | feeder and emit on |
| Join capability | #363 | `JoinCapabilityEnabled` | spatial mixer, `JoinCapabilitySecret` (32 characters or more) |
| Voice moderation | #362 | `VoiceModerationEnabled` | stock plugin: nothing more; spatial mixer: feeder on |
| Position feed | #364 | `PositionFeedEnabled` | spatial mixer 1.2.0 or later (`vis_protocol` 3), feeder and emit on |
| Voice connectors | #365 | `ConnectorsEnabled` | spatial mixer, NPC module |

The call logic for the non-spatial features does not depend on the plugin: avatar-to-avatar calls, group
calls and conferences work with the stock AudioBridge and with a spatial mixer. With the stock AudioBridge, a
switch that needs the spatial mixer is switched off at start with a warning. See [overview.md](overview.md).

## 2. Goals and non-goals

**Goals**

- Work with unmodified viewers that implement the Second Life WebRTC voice protocol.
- Compute who may hear whom on the region server, and have the mixer enforce it in the mix.
- Be diagnosable from the region console and logs.
- Run unchanged from a single standalone region up to a grid with a Robust voice service.

**Non-goals**

- Selective forwarding of audio to clients with client-side mixing. Enforcement in the mix requires a
  server-side mixer.
- Direct client-to-client media.
- Video.

## 3. Privacy and trust properties

### 3.1 All media goes through the server (Implemented)

A viewer's WebRTC session is always with the mixer. Avatar-to-avatar calls, group voice and
conferences are rooms on the mixer. No viewer receives another viewer's ICE candidates, and no
setting enables a direct connection.

### 3.2 The region server decides who may hear whom (Implemented)

The permission inputs are region-server state, never viewer-supplied: estate voice setting, estate
bans, parcel voice flag, parcel bans and access lists, parcels that hide avatars, and parcel voice
moderation. The viewer's parcel id on a provision request is only a hint and is checked against the
region's own land records. Details of the rules are in the protocol document, section 7.

### 3.3 Enforcement in the mix (Implemented in the sim; Mixer requirement)

With the visibility feed on, the region server sends each listener's exclusion and mute sets to the
mixer. The mixer must apply them to that listener's audio and to the participant list sent to the
viewer, from the same sets, so that an indicator never shows for audio the listener cannot hear and
a hidden avatar is absent from both. A modified viewer cannot undo this because the audio it receives
never contains the excluded source.

With authority arming and join capabilities also on, the mixer additionally silences any listener or
source the region server has not told it to carry, and requires a region-minted capability to join a
room declared by the region. If the region server stops sending heartbeats, a mixer in authority mode
is expected to stop carrying audio in the affected rooms. These behaviours are the mixer's; the
messages that enable them are documented in the protocol document.

### 3.4 Capture is disclosed (Implemented)

Voice connectors can record or inject audio only through a connector record that an operator writes in
the region server's configuration. A record with `MayInject = false` (the default) is muted at
registration and so is recording-only. Three disclosure notices are always sent and cannot be turned
off: a region alert when a connector attaches or detaches, an entry notice to avatars who arrive, and
a proximity notice from a voiced connector. The addon creates rooms with `record: false`.

## 4. Region-side functions

### 4.1 Provisioning and signalling (Implemented)

- Capabilities: `ProvisionVoiceAccountRequest` (offer/answer, `channel_type` of `local` or
  `multiagent`, `parcel_local_id`, logout), `VoiceSignalingRequest` (trickled ICE and the completion
  marker), `ChatSessionRequest`, and, when moderation is on, `SpatialVoiceModerationRequest`.
- Spatial admission: estate voice setting, parcel voice flag, parcel ban and access checks. Anything
  other than `local` or an admitted `multiagent` request is refused with 403.
- Rooms are created on the mixer before each join; the SDP offer is passed through unchanged.
- A full room is reported to the viewer as HTTP 409.

### 4.2 Avatar-to-avatar calls (#353; across processes #354 and #356; Implemented, off by default)

Two-party calls in a non-spatial room. An invitation is recorded when the caller opens a voice session
with another avatar, the caller's call request returns credentials and delivers one invitation to the
callee, and both parties then provision with those credentials. A callee on another region server
process is rung through a grid instant message: signed with `CarrierSecret` under `SessionStoreMode = local`,
checked against the session service under `grid`. The callee's join, decline and hang-up are carried back the
same way. Unanswered invitations expire.

### 4.3 Group voice (#354) and conferences (#355) (Implemented, off by default)

Group voice requires the group's session-join power, and the session-voice power unless
`GroupVoiceRequireVoicePower = false`. The seat limit is `GroupVoiceCap` (default 50), never above 110, the largest room a
spatial mixer can be set to hold (it admits 50 by default). Conference sessions answer the viewer's start request and exchange conference text
between region servers as instant messages.

### 4.4 Parcel voice moderation (#362) (Implemented, off by default)

A parcel owner, an estate owner or manager, or a member of the owning group with the chat-moderation
power may mute or unmute one avatar, or mute or unmute everyone, on their current parcel. The request
names no parcel; the region server uses the requester's position. Moderated sources stay in the
listener's participant list but are silenced. Owners, managers and group moderators cannot be muted.
State is in memory and cleared at restart. Operators can inspect and change it from the console.

### 4.5 Voice connectors (#365) (Implemented, off by default)

An operator-written `[VoiceConnector.<name>]` record creates an NPC identity in the voice session for
an external recorder or injector peer. The NPC's id is stable across restarts. The external peer
joins the mixer itself, with a join capability it fetches from a small authenticated endpoint on the
region server when that is configured.

### 4.6 Position feed (#364) (Implemented, off by default)

With `PositionFeedEnabled` the region server tells the mixer where it has each voice participant, in global centimetres with
a speed allowance and a tolerance, so that a viewer's own claim of its position can be compared with it. The mixer only
observes: it counts what a correction would change and changes no audio. Enforcement is not available yet. See
[positions.md](positions.md).

### 4.7 Diagnostics (Implemented)

Console commands: `janus info`, `janus list rooms`, `show voice visibility`, `show voice moderation`,
`show voice closing`, `show voice sessions`, `show voice calls`, `show voice call`, `voice moderation mute|unmute`,
`voice call mute|unmute|mute-all|unmute-all|remove|readmit`, `voice connector start|stop`. `MessageDetails`
enables verbose wire logging for the region side and, separately, for Janus traffic.

## 5. Mixer requirements

These are expectations of a spatial mixer and are not verifiable from this repository.

- **Spatial rendering:** each listener receives its own mix, with distance falloff and panning derived
  from positions sent by the viewer over its data channel. Listener orientation from the viewer is
  used. A listener's reported position may follow the camera within a configured radius of the avatar,
  but permission to hear is always decided from the avatar's own position through the visibility feed,
  never from the reported position.
- **Fidelity:** 48 kHz processing end to end, Opus in and out, no server-side noise suppression or
  automatic gain control (viewers already apply it).
- **Data channel:** per-peer position, volume and join/leave updates are the mixer's responsibility;
  the addon does not interpret the viewer's data channel.
- **Feed handling:** apply `excl` and `mute` as described in the protocol document, honour the
  `replace`/`add`/`remove` semantics, and report the inner reply fields.
- **Capability checks:** validate join capabilities when configured, and answer error 496 with a
  reason when one is refused.

## 6. Deployment

The small case is the default case: one region server, one Janus gateway with one mixer, one INI
section pair. For a grid, a Robust voice service takes the place of the per-region Janus link: regions
use `WebRtcVoiceServiceConnector` and Robust runs `WebRtcVoiceServerConnector`. Viewers connect to the
mixer through the Janus gateway in both topologies. One mixer may serve many regions; the addon has no
placement or allocation layer, so spreading regions across several mixers is done by pointing regions
at different gateways in configuration. A grid that runs its regions in more than one region-server process can keep one record
of each call on Robust, in the call session service (#356, `SessionStoreMode = grid`); see [deployment.md](deployment.md).

## 7. Protocol compatibility

- Capabilities and bodies follow the published viewer protocol: JSEP offer/answer, `channel_type`
  `local` or `multiagent`, `parcel_local_id`, logout, trickled ICE with a completion marker.
- Parcel changes inside a region do not change a viewer's connection; the mixer applies them through
  the feed.
- The addon advertises `VoiceServerType = webrtc` in simulator features, and `stun-servers` when
  `StunServers` is set to a comma-separated list of full ICE URIs (for example
  `stun:stun.example.org:3478`).

## 8. Not covered

This addon does not provide hypergrid-specific voice routing, per-connection diagnostics pushed to
viewers, an echo test, voice effects, voice morphing or performer modes. Recording consent policy
beyond the disclosure notices in section 3.4 is the operator's responsibility.
