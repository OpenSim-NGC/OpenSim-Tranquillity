# WebRTC voice: the position feed

With a spatial mixer, a viewer tells the mixer where its avatar is, and the mixer places the avatar's voice there. A
modified viewer could claim a false position. The position feed lets the region send the mixer where each voice participant
really is, so the claim can be compared with it. The feed is added by #364. This page describes what exists today:
**observe mode**. Enforcement is not available yet: the `enforce` setting is accepted, and the feed still runs in observe
mode.

The mixer's description of the `peer_pos` message is section 7.6 of its protocol document (see
[mixer-feed-protocol.md](mixer-feed-protocol.md), section 6.6, for the region server's side). Settings are in
[configuration.md](configuration.md).

## What it needs

- The slvoice mixer, release **1.2.0 or later**, which advertises `vis_protocol` 3. A mixer that advertises a lower version
  is sent nothing. With the stock AudioBridge `PositionFeedEnabled` logs one warning at start naming the switch and the
  plugin, and is turned off.
- `VisibilityFeederEnabled` and `VisibilityEmitEnabled`, because positions go through the same admin connection as the
  visibility messages (`JanusGatewayAdminURI` and `AdminAPIToken`). Without them the region logs a warning and the feed
  stays inactive.
- `VisibilityArmingEnabled` is not required. The feed learns the mixer's version on its own: when no `peer_ctl` request or
  heartbeat has told it, it asks once with a `peer_ctl_heartbeat` that names no room. The mixer answers with its version and
  adopts no epoch, marks nothing stale and arms no listener. A mixer that answers with no version, or with an error, is taken
  to be older than version 3 and is sent nothing more; the question is asked again after a request fails in transport (a
  mixer restart), and the version is read again from every `peer_pos` reply, so a mixer replaced by an older one stops the
  feed. Without this, a region with arming off in which nobody is excluded or muted would never send a `peer_ctl_batch`, and
  the feed would stay silent.
- `JoinCapabilityEnabled` (#363) is not required, but the feed logs a warning without it: a viewer could then join the mixer
  as another agent, and positions are matched to the agent id a viewer joins as, so the comparison is only as strong as that
  binding.

## Observe mode

`PositionFeedMode = observe` is the default. In observe mode the region sends positions and the mixer counts what it would
have changed and changes nothing: nobody's audio is moved or muted, and no roster changes. The mixer compares each point
with the position the viewer claims and counts the cases `would_clamp`, `would_use_sim` and `no_sim`, per participant and per
room (`visibility.positions` in the mixer's admin API). Use it to see how often claimed positions differ from
the region's before anything depends on it. A room that has accepted no `peer_pos` is not observed at all.

`PositionFeedMode = enforce` is accepted and logged, and the positions are sent as `observe`. Enforcement is not available
yet.

## What the region sends

Per voice participant: the position the region has for the avatar in global centimetres, a speed allowance (how fast the
avatar can really be moving, never below `PositionFeedSpeedFloorCmS` and never above what the physics engine allows any body,
read from `PhysicsScene.MaximumLinearVelocity` (#351); `PositionFeedSpeedMaxCmS` when the engine declares no limit), and a
tolerance (the margin the mixer allows at the moment of the sample, `PositionFeedMarginCm`, widened for an avatar whose
position is the stale one core passes across a region border).

- A root agent's position is measured by its own region every frame. A child agent in a neighbouring region has the
  position its root region last sent across, which core sends only after the avatar has moved about 10 metres and at
  least 2 seconds have passed, so a child's tolerance is wider.
- Every `PositionFeedTickMs` (500 ms) the region works out what to send. The look comes from the visibility feeder's thread,
  which runs every `VisibilityTickMs` (250 ms), so the gap between looks is 500 to 750 ms. It sends a participant that is
  new to a room or has moved at least `PositionFeedMoveThresholdCm` (100) since the last point sent, and a full set of each
  room at the first look at or after `PositionFeedKeepAliveMs` (**2000 ms**) since the room's last, so an avatar that stands
  still is never silent for long and a lost request is repaired within one interval. A participant not listed in a full set
  has left.
- A room whose participants have all left gets one empty full set, then nothing.
- An agent that cannot be placed in a room is left out and counted.
- Nothing is computed or sent while `PositionFeedEnabled` is `false`.

### Why the keep-alive is 2000 ms and not 1000

The mixer treats a point as stale 6000 ms after it received it. Two full sets are at most the keep-alive plus one look
(750 ms) apart, so one lost request leaves a standing participant fresh when (keep-alive + 750) x 2 is under 6000. 2000 gives
5500, and 2250 is the largest value that fits; `PositionFeedKeepAliveMs` accepts 1 to 2250 and uses 2000 for anything outside
that. With 1000, a room in which nobody moves costs 40 to 60 requests a minute; with 2000 it costs 22 to 30.

## What it costs

One request names one room. At each look the region sends, for every occupied room, at most one request: a full set if the
keep-alive has passed since the room's last, otherwise the participants that are new or have moved, and nothing if there are
none. A request carries up to 120 entries (the mixer accepts 128 entries and 65,536 bytes) at about 73 bytes each: 50
participants are 3,679 bytes and 110 are 7,999 bytes. A room admits at most 110 participants, so a room is always one request;
a larger room would be split, with only the first request `full: true`.

Requests a minute for each occupied room of 50 (arithmetic from the defaults, not a measurement):

| Case | Arithmetic | Requests a minute |
|---|---|---|
| Someone moved 1 m or more since their last sent point, at every look | 60,000 / 750 to 60,000 / 500 | 80 to 120 |
| Nobody moves, keep-alive 2000 (a full set 2,000 to 2,750 ms apart) | 60,000 / 2,750 to 60,000 / 2,000 | 22 to 30 |

Four rooms of 50 are 320 to 480 requests a minute when busy and 87 to 120 when quiet. The rooms of one look are sent one
after another, so a look takes the number of occupied rooms times the round trip to the mixer; when that is longer than
`PositionFeedTickMs`, looks are skipped (one set of sends is in flight at a time) and the points age. On a region server
with many occupied rooms, lengthen `PositionFeedTickMs`. A mixer's `peer_pos` names one room, so several rooms cannot be
sent in one request.

## What it logs

At start, per region:

- `[REGION WEBRTC VOICE][Positions]: position feed enabled, mode observe` (Information).
- With `PositionFeedMode = enforce`: `PositionFeedMode enforce is accepted but not active yet; positions are sent in observe mode`.
- A warning if the feed is on without the feeder and emission (`position feed is inactive`), without `JoinCapabilityEnabled`
  (the binding warning above), or with a mode that is neither `observe` nor `enforce` (`using observe`). A
  `PositionFeedKeepAliveMs` outside 1 to 2250 logs a warning and uses 2000.
- `[VOICE VISIBILITY] position feed started for <region> every <n> ms; positions are sent once the mixer advertises vis_protocol 3`.

While running (`[VOICE POSITIONS]`):

- `the mixer advertises vis_protocol 3; sending positions in observe mode`: the feed is live.
- `asking the mixer which protocol version it speaks; no positions are sent until it advertises vis_protocol 3`: normal for
  the first moments after start.
- `the mixer advertises vis_protocol <n>, and peer_pos needs 3: no positions are sent`: the mixer is too old. A
  `the mixer advertises no protocol version` warning means it answered without a version.
- `the mixer did not accept a peer_pos request for room <n>; the next full snapshot repeats it`: at most once a minute, with the
  number of failures so far. The pump's other failures (reading positions, the tick, sending, the version check) are each one
  warning a minute with the number held back, and a Debug line in between.
- A one-minute summary at Debug, written only for a minute in which something was sent or answered.

What the mixer answered is counted by the region: applied, or refused by reason (`stale_seq`, `unknown_room`, `too_big`,
other). A request the mixer refuses comes back as a successful transport with an error inside, so without these counts a
mixer that applied every request could not be told from one that refused them all. The counts are in `show voice visibility`
when the feed is on and in the summary above, and a refusal writes one warning a minute naming the room and the reason. The
counts of what the mixer compared (`would_clamp` and the others) are kept by the mixer, not in the region log.
