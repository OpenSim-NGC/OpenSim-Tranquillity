# WebRTC voice: moderation

Two separate features, with separate switches:

| Feature | Switch | Added by | Moderates |
|---|---|---|---|
| Parcel voice moderation | `VoiceModerationEnabled` | #362 | The voice of the people on a parcel |
| Call moderation | `CallModerationEnabled` | #357 (the mixer arm of a voice mute: #362) | Group calls (from the viewer and the console) and conferences (console only) |

Both are off by default. Settings are in [configuration.md](configuration.md).

## Parcel voice moderation

With `VoiceModerationEnabled` on, the region offers the viewer's `SpatialVoiceModerationRequest` capability. A parcel's
owner, an estate manager or owner, or (for a group-owned parcel) a member of the group holding the Moderate Group Chat
power can mute one avatar, unmute one avatar, mute everyone, or unmute everyone on the parcel. The region checks this
on every request; it does not trust the viewer's own idea of who is a moderator. The parcel is the one the requester is
standing on, not one the request names.

- **Who cannot be muted.** The parcel's owner, estate managers and group moderators are never muted.
- **How the mute is carried out.** With the slvoice mixer it is carried by the visibility feeder, so `VisibilityFeederEnabled`
  must be on; if it is not, a request is answered 501 and nothing is recorded. With the stock AudioBridge it is the plugin's
  own mute of that participant. On the stock plugin the mute belongs to the participant, not the parcel: an avatar muted on one
  parcel stays muted in its room if it walks to another parcel, until a moderator unmutes it. A mute-everyone is remembered
  per parcel and also applies to whoever joins the parcel afterwards.
- **Answers.** 200 done; 400 unknown or incomplete request; 403 the requester may not moderate that parcel; 404 the requester
  or their parcel could not be found; 501 the voice server cannot carry it out, or the feeder is off (nothing recorded);
  503 (stock plugin) the plugin failed to apply it (nothing recorded).
- **State is in memory.** Moderation state is not stored: a region restart clears every mute.

Console commands, for operators, on the region selected with `change region <name>`. They work on the visibility feeder's
state, so they report that no state exists on a region that is not running the feeder (including every region on the stock
plugin):

| Command | Does |
|---|---|
| `show voice moderation` | Lists the muted avatars and the mute-everyone state of each parcel |
| `voice moderation mute <agent-uuid-or-name> [parcel-local-id]` | Mutes one avatar on a parcel (the avatar's own parcel if no id is given). A name is matched against the root avatars of the region |
| `voice moderation unmute <agent-uuid-or-name>` | Clears one avatar's mute on every parcel of the region. A name is matched against the avatars that are actually muted. It does not clear a mute-everyone, which the viewer's "unmute everyone" does |

## Call moderation

With `CallModerationEnabled` on, and `GroupVoiceEnabled` or `AdhocVoiceEnabled` on (without one, a warning is logged and
the feature stays off):

### Who may moderate

- **A group call:** a member of the group who holds the Moderate Group Chat power. The power is looked up from the
  groups service on every request.
- **A conference:** nobody from the viewer. The viewers offer a conference no moderator menu, and a moderation request for a
  conference is answered 403. Moderate conferences from the region console.
- **An avatar-to-avatar call:** not moderated; a request is answered 403.
- **Never muted:** a moderator, and nobody mutes themselves. A request to mute a moderator or oneself is refused 403.

### What the viewer shows

The region marks the moderators of a group call in the participant list it sends, so the stock viewer shows its
moderator menu to them. Through that menu a moderator can mute or unmute one member's voice, mute or unmute one member's
text, and mute or unmute everyone's voice. A member who is text-muted has their lines dropped before the groups module
delivers them, through the `IGroupChatGate` hook (#351). The flags and the powers of the group members seated in a call are
checked again every `CallModerationRevalidateSeconds` (60; `0` = never); a pass reads each call once and asks the groups
service once for each member (#358). A member who no longer holds the powers the call needs, or has left the
group, is let go of the call on that check; no removal is recorded, and they can come back when the powers do.

### Voice mutes

- A voice mute is carried out by the stock plugin's own mute of that participant in the call's room (#357) or, from
  #362 on, by a slvoice mixer: one request for the whole room, or several in a row when it is over the mixer's size
  limit, and the mixer's inner reply decides whether it counts as done. A mute that cannot be carried out is answered 501
  (no voice mute is available with this voice server, which is what a plugin other than the stock one answers with #357
  alone) or 503 (the voice server failed), and is taken back out of the record.
- The member must have a participant in the call's room; otherwise the answer is 404 and nothing is recorded.
- A mute-everyone stays until someone unmutes everyone, or until the call ends. It applies to whoever joins later, except
  moderators. When the last seat of a call is released, however it is released (a leave, a departure, a seat lease running
  out, or the release when a region starts), mute-all and every member's voice and text mute are cleared, in the engine of
  one process and in the grid session service alike, and the next call of that group starts unmuted. A removal keeps its
  own lifetime (below).
- A member can be unmuted individually while everyone is muted.

### Removal (console only)

The stock viewers have no removal in the participant list. An operator removes a member with the console. The member's voice
is dropped, their viewer is told to close the call, and they are refused every way back: a new call, accepting an
invitation, being invited again and a voice provision. The refusal is answered from a short cache, so a viewer that retries at
once does not repeat the checks. A removal lasts until the call ends, or for `CallRemovalMinutes` when above 0 (and
the call's record is kept that long). A removal from a group call leaves the group's text alone; a removal from a conference
also takes the member out of the conference's text. `voice call readmit` lets a member back; they then join the way anyone does.

With `SessionStoreMode = local` a mute or removal is a record of that region server process only. With `grid` it is recorded
in the call session service, and a region applies a removal to a member it hosts when its next seat renewal reports it, up to
`SessionSeatRenewSeconds` (60) later.

### Console commands

The call id is the group's id for a group call, or the id `show voice calls` prints for a conference. Members are shown and
given by agent id.

| Command | Does |
|---|---|
| `show voice calls` | Lists the group calls and conferences this region can see: id, kind, seats, how many members are muted or removed. With the grid session service a region lists the calls it holds a seat in |
| `show voice call <call-id>` | Shows one call's members with their voice mute, text mute and removal |
| `voice call mute <call-id> <agent-uuid> voice\|text` | Mutes one member's voice or text |
| `voice call unmute <call-id> <agent-uuid> voice\|text` | Lifts it |
| `voice call mute-all <call-id>` | Mutes everyone's voice, and whoever joins later; moderators are never silenced |
| `voice call unmute-all <call-id>` | Lifts the mute on everyone; also clears individual voice mutes |
| `voice call remove <call-id> <agent-uuid> [reason]` | Removes a member and keeps them out |
| `voice call readmit <call-id> <agent-uuid>` | Lets a removed member back |

### Limits

- A removal of a member whose viewer is on a region of another process is not instant in `grid` mode (it waits for the
  next seat renewal) and does not happen at all in `local` mode.
- A stock viewer's group ban or eject does not take the member out of the call at once: the powers check above lets them go
  on its next pass.
- Text moderation needs a call. A text mute is kept in the call's record, and the gate lets every line through for a group
  that has had no such record, so the chat of a group that has had no call is not moderated.
- While the call session service cannot be reached, the text gate lets lines through. The gate keeps what it read from a
  grid call record for 10 seconds (#358), so a text mute made through another region server can take that long to apply; a
  mute made in this process applies at once.
- The stock viewer's participant list offers a group ban and no "remove from call"; a conference has no moderator menu in the
  viewer.
