# Responsible disclosure: OpenSim grid security issues

This document describes security weaknesses that affect **stock OpenSimulator grids**, written from an
attacker's point of view so that operators can quickly recognise whether their grid is exposed and know
what to change. Every issue below has been confirmed against current upstream OpenSim; none is specific
to any one grid or fork.

Scope and assumptions:

- Unless a heading says otherwise, the attack is **unauthenticated**: no account, no session, and no
  presence on the grid are required.
- Most attacks need only **public information** about the target: an avatar's display name (which resolves
  to a UUID through ordinary Hypergrid lookups), a region name, or a region's map coordinates.
- "Region public port" means the simulator's HTTP listener; "ROBUST public port" means the grid services
  port (commonly `:8002`). Both are, by design, reachable from the public internet on a Hypergrid-enabled
  grid.
- Details that would only serve to make exploitation easier (exact payloads, magic constants, field
  layouts) are deliberately omitted. The mechanism and the fix are described; a working exploit is not.

The single most valuable defensive change for the majority of these issues is to **treat the grid's own
control-plane endpoints as privileged and refuse them from any source that is not one of your own
servers.** Upstream OpenSim ships many inter-server control endpoints on the same public ports it serves
foreign Hypergrid traffic on, with no caller authentication. Adding a source-address allowlist (your
ROBUST host(s) and all of your region hosts, plus loopback) in front of those endpoints closes a large
fraction of what follows. Several issues need their own additional fix as noted.

---

## I can take over any local user's account on a vulnerable grid, as an unauthenticated user

**What.** The Hypergrid "home agent" login endpoint (`/homeagent/`) has no caller authentication. On the
path where the requested destination is the grid's *own* gateway (the "return home" path), the service
mints a brand-new in-world session for the named user and vouches for that session against itself, without
ever proving the caller holds the user's real session. The result is a full, usable in-world session as
the victim: their inventory, their currency balance, the ability to act and message as them.

**How it works.** The endpoint regenerates the service session token as part of processing the request,
so the grid's own gatekeeper check (which only verifies the token belongs to this grid) passes trivially.
Nothing in the flow requires the caller to have authenticated as the user, or to be the user's real home
region. The victim's UUID and the destination region are both discoverable through ordinary unauthenticated
Hypergrid lookups, so a display name is enough to start.

**Fix.** There is no way to authenticate a return-home request, so do not try. Any grid a user legitimately
visits is handed the user's full circuit, session key included, and can replay it verbatim; nothing the home
grid receives distinguishes a genuine return from a visited grid impersonating it, so enforcing or verifying
the session key does **not** close this. The only trustworthy way back onto the home grid is a fresh login,
which authenticates directly against the login service. Accordingly, **block Hypergrid home-return entirely**:
refuse any login whose destination is the local grid unless it is a first-login originating from your own
login service. Legitimate users return home by relogging, which is unaffected; "go home" over the Hypergrid
stops working, which is the intended and necessary trade-off.

*Related residual (inherent to Hypergrid, harder to fully close):* any grid a user has legitimately
visited holds a copy of that user's circuit and can replay it to your home endpoint to forward the user's
avatar onward to another grid, impersonating them to third grids. This cannot be distinguished by a token
check alone. Tracking each user's active travel session and only honouring onward-forwarding for a known,
current, matching session materially limits it.

---

## I can log in as any avatar in any region, as an unauthenticated user

**What.** The region public port exposes an agent-creation endpoint (`POST /agent/<id>/`) that takes no
credentials and does not check the caller's source address. Supplying an online session ID for a user yields
a **full root login as that user** in a region - complete account takeover of their inventory, assets and
capabilities. And a session ID is not secret from *anyone*: the credential-harvest issue below lets an
unauthenticated attacker collect session IDs wholesale (they are also held by every region and every foreign
grid the user has visited). In the narrower situation where no session ID is available, an attacker can
still hijack the user's capabilities or kick them out of a region using nothing but their UUID.

**How it works.** The endpoint accepts an entirely attacker-forged agent circuit. The outcome depends on the
victim's presence in the targeted region:

- **Full root login (main case).** Target a region where the victim has *no* presence. The only gate is a
  check that the supplied session ID belongs to an online session of that user - which the attacker satisfies
  with a session ID harvested via the issue below. This yields a full root session as the user, indefinitely.
- **Capability hijack (fallback, no session ID needed).** If the victim instead has a *child* agent present
  in the targeted region - which they do in every region within view of wherever they actually are - the
  presence and authorization checks are skipped entirely. The forged circuit replaces the real one and a
  fresh capability set is issued to the attacker: inventory fetch, inventory create/move/delete, asset
  upload, the event queue, and for a privileged user the region console. This needs only the victim's UUID.
- **Kick.** If the victim is a non-transiting *root* presence in the targeted region, the region force-closes
  them before any verification runs. Again, only the UUID is needed.

**Fix.** Gate the `POST` (agent create) branch of this endpoint to your own trusted server addresses only,
before the request body is parsed. Leave the query/put/delete branches open where they are legitimately
called cross-grid during Hypergrid teleport handoff. Additionally reject any request that carries the
in-world-script HTTP marker header, so a script running on one of your own (trusted-IP) regions cannot be
used to reach the endpoint from inside.

---

## I can rez a scripted object owned by any user into any region, as an unauthenticated user

**What.** The region public port exposes an object-creation endpoint (`POST /object/`) that takes no
credentials. The request body carries a complete serialized object, *including its owner UUID*, and the
region trusts that owner field. Setting the owner to the estate or parcel owner passes every permission
check, the object is added and persisted, and its scripts are started running as that owner.

**How it works.** Because the scripts run with the chosen owner's identity, owner-gated scripting becomes
available to an anonymous caller: estate/parcel management functions, land ejection and teleport-home,
ban-list edits, and elevated-threat script functions. An object flagged as an attachment can be force-
attached to any avatar present in the region. There is also an unlimited object-injection griefing/DoS
vector, since objects of any size and position are persisted to the region store.

**Fix.** Gate this endpoint to your own trusted server addresses only, before the body is parsed, and reject
requests carrying the in-world-script marker header. The only legitimate caller is a map-adjacent region of
your own grid; no viewer and no foreign simulator posts here directly.

---

## I can harvest the live session credentials of every avatar in a region, as an unauthenticated user

**What.** The region public port exposes a "neighbour hello" endpoint (`POST /region/<uuid>/`) that is
unauthenticated: both the connector-level key check and the in-handler key check ship commented out. The
request body is an attacker-written region description. When accepted, the region announces the new
"neighbour" to every root avatar present and posts each avatar's child-agent circuit to the URL the attacker
supplied.

**How it works.** That circuit contains the avatar's session ID, secure session ID, circuit code, client
IP, MAC and machine-id ban identifiers, and service URLs - a complete set of live credentials, for every
avatar in the region at once. The only precondition is public map data (a region UUID and coordinates). The
harvested session IDs then feed the region login-as-any-user issue above. If the attacker's listener answers
success, the affected viewers are additionally instructed to connect out to the attacker's host.

**Fix.** Gate this endpoint to your own trusted server addresses only, before the body is parsed, and reject
requests carrying the in-world-script marker header. Return a not-found status to untrusted callers so the
endpoint does not confirm its own existence to scanners. Only your own regions legitimately call it.

---

## I can kick any online user off a vulnerable grid, as an unauthenticated user

**What.** The unauthenticated cross-grid instant-message ingress (present on both the ROBUST public port and
every region's public port) accepts control-plane messages, not just conversational ones. A "god kick"
control message is acted on with no verification of the sender, force-logging-out any named online user.

**How it works.** The ingress funnels every message through one handler that does not distinguish a
privileged control dialog from an ordinary chat message, and the god-kick handler trusts the well-known
service "god" identity carried in the message rather than authenticating the source. The victim's UUID is
public.

**Fix.** In the shared ingress handler, refuse privileged control dialogs (god kick, and the god-summons
teleport described next) unless the request comes from one of your own trusted server addresses. Leave all
conversational dialogs (text, typing, normal teleport offers, inventory/group/friendship) open so federation
keeps working. Note that the shared cross-grid message key is **not** a suitable authenticator: it is sent
in cleartext and is attached even to messages forwarded to foreign grids, so any grid that receives one
forwarded message learns it. Use a source-address allowlist instead. This fix depends on the source IP being
non-spoofable (see the X-Forwarded-For issue below).

---

## I can force-teleport any user to a destination of my choosing, as an unauthenticated user

**What.** Through the same unauthenticated instant-message ingress, an attacker can deliver a "god summons"
teleport request to any online user. Viewers commonly auto-accept god summons, so the victim is teleported
without consent - including across grids to an attacker-controlled destination, which can log them out of
their home grid.

**How it works.** The god-summons teleport dialog is a privileged control message that the ingress delivers
from any source. It does not bypass the destination's own access control, but it does bypass the victim's
consent.

**Fix.** Same as the kick above: refuse privileged control dialogs from untrusted sources in the shared
ingress handler. The two share one fix.

---

## I can kick any online Hypergrid visitor, as an unauthenticated user

**What.** The Hypergrid arrival endpoint (`/foreignagent/`) authenticates an arriving foreign agent by
calling back to a home-grid URL that the *caller* supplies. Its duplicate-presence check then matches the
victim by UUID only and fires a god-kill against the victim's real online session.

**How it works.** An attacker who stands up a callback responder that always answers "verified", and who
knows the victim's public UUID, triggers the duplicate-presence logic against the victim's genuine session,
disconnecting them. The foreign-agent path skips the impersonation check that would otherwise gate this.

**Fix.** Only fire the duplicate-presence god-kill when the arriving agent's claimed home actually matches
the home recorded for the online session; on mismatch, refuse the login without killing the existing
session, using a generic "already logged in" reason. A forged home passes the caller-controlled callback but
fails the home match; using the victim's real home URL to pass the match sends the callback to the real home,
which rejects the forged session first.

---

## I can grant myself edit rights over another user's objects, as an unauthenticated user

**What.** The region public port exposes a friends endpoint (`POST /friends`) with no authentication for any
method and no source-address check. Its "grant rights" method writes the granted permission flags directly
into the recipient's cached friend entry, with no check that the two users are actually friends or that the
real sender authorized it.

**How it works.** That friend-rights cache is exactly what the permissions system consults for owner-class
rights over a friend's objects. An attacker whose avatar is already on the victim's friends list and who has
a presence in the region can post a self-grant and be treated as owner of the victim's objects there, with
nothing written to any database. The other methods on the same handler allow unauthenticated spoofing of
friendship offers, acceptances, terminations, and online/offline status from any name.

**Fix.** Gate the whole handler to your own trusted server addresses only, and reject requests carrying the
in-world-script marker header. Every legitimate caller is another of your own regions. This one gate closes
both the rights self-grant and the notification spoofing.

---

## I can silently wipe any user's entire friends list, as an unauthenticated user

**What.** The Hypergrid friends endpoint's "delete friendship" method is unauthenticated and matches stored
friendships with a prefix/suffix comparison that degenerates when the attacker supplies an empty friend
identifier and a minimal secret. The empty prefix matches every friendship; a short secret suffix matches by
brute force over a tiny space.

**How it works.** Each request deletes one friendship, silently (the victim gets no notification and only
notices on relog). Iterating over a handful of suffix values empties a victim's whole list from nothing but
their public UUID. It is unauthenticated, unthrottled, and scriptable across every enumerable UUID on the
grid.

**Fix.** Require a non-empty secret; parse the requested friend UUID and, for each stored record, extract the
friend UUID and secret with the same parser the legitimate delete path uses, and require an exact match on
both the friend UUID and the full secret. This kills the blind wipe (empty identifier matches nothing) and
the suffix brute-force (full-secret equality). Bare local friendships parse to an empty secret and become
un-deletable through this remote path, which is correct - local unfriending uses a different code path.
Residual: the friendship secret is short, so an attacker targeting one specific known friendship pair could
still brute-force that single pair; lengthening the secret would break cross-grid interoperability.

---

## I can read any user's email and rewrite their profile, as an unauthenticated user

**What.** When the user-profiles service is exposed on the public port, its JSON-RPC methods are registered
with no authentication and no per-method check. Anonymous callers can read a user's email address, read and
write and delete their private notes, and overwrite their profile, picks, classifieds and interests.

**How it works.** The dispatch layer has no notion of an authenticated or trusted caller, and the sensitive
methods sit alongside the legitimately-public cross-grid profile-view methods. The target UUID comes from an
ordinary name lookup.

**Fix.** Cross-grid profile *viewing* must stay open (foreign grids legitimately read a local user's public
profile, picks and classifieds), so a blanket gate is wrong. Add a per-method trusted-source gate and apply
it only to the sensitive methods: email/preferences, private notes, and every profile/picks/classifieds/
interests/user-data *write* or *delete*. A blocked call should return the same "no such method" error as an
unknown method, so the gate does not reveal that the method exists. The owner's own viewer reads their email
from inside a trusted region, so that keeps working.

---

## I can inject group notices and create groups on a vulnerable grid, as an unauthenticated user

**What.** The Hypergrid groups endpoint validates no token on its two write methods. One creates a group
record for a new UUID and sends a forged group-invitation message to any named agent; the other injects a
group notice - with attacker-chosen sender name, subject, body and attachment fields - into any proxied
group, fanning out to every member who accepts notices. The origin-verification step that would have
authenticated the notice ships commented out.

**How it works.** The notice method checks only that the group is a remote-proxied group and that the notice
ID is new - not who sent it, nor that they are a member. Any caller reaching the public port can inject.

**Fix.** If your grid does not intend to accept externally-originated group creation or notices, refuse both
write methods from untrusted sources with a source-address gate. If you do want cross-grid groups, the fuller
fix is to restore the commented-out origin-verification handshake so a notice is validated against the
originating grid. The read methods and the per-membership-token methods already validate their token and can
stay open.

---

## I can make the grid probe its own internal network, as an unauthenticated user (SSRF)

**What.** Two unauthenticated Hypergrid entry points (`/homeagent/` and `/foreignagent/`) cause the grid
services process to issue outbound HTTP to a URL the caller supplies, with no egress filtering. The target
can be an internal-only service, loopback, another host on the private network, or the cloud metadata
endpoint. Error and message text is reflected back, giving a semi-blind internal port-scan oracle.

**How it works.** One path takes a caller-supplied "gatekeeper" URL and posts to it; the other fires a
caller-supplied home URL callback before the ban and foreign-agent checks even run. Both are fully
unauthenticated.

**Fix.** Apply a shared egress filter to the caller-supplied target at both sinks: resolve the host and
refuse if any resolved address is loopback, private (RFC1918), link-local/metadata, unique-local, CGNAT, or
one of your own infrastructure addresses; carve out only your own grid gateway. Check every resolved address
so an IP-literal target is closed outright. Residuals to be aware of: DNS rebinding for hostname targets
(the check-time resolution is not pinned for the later connect) and redirect-following; fully closing those
needs connect-by-pinned-IP at the HTTP-client layer.

---

## I can permanently disable a grid's map tiles with a single request (DoS)

**What.** The map-tile handler on the ROBUST public port acquires a process-wide lock but has early return
paths that skip releasing it, with no try/finally. One malformed tile request leaves the lock held forever;
every subsequent tile request then blocks and times out until the service is restarted.

**How it works.** A single request that hits one of the un-released early returns (for example a bad scope
identifier or an empty tile path) wedges the shared lock. No authentication is required and the effect is
grid-wide and persistent.

**Fix.** Wrap the handler body after the lock acquisition in try/finally so the lock is released on every
return path. (Since the underlying tile read is a stateless file read, the lock could alternatively be
removed, but that is a separate performance change.)

---

## I can forge my apparent source IP to any of the grid's services

**What.** The HTTP server unconditionally prefers an `X-Forwarded-For` (or `Forwarded`) header over the real
socket peer address when reporting the remote endpoint. Every IP-based decision, log line, and ban check on
the public ports therefore trusts an attacker-chosen value.

**How it works.** An attacker simply sends the header. This both defeats IP-based logging/bans and, more
importantly, undermines any source-address allowlist you add to fix the issues above - so it must be fixed
first, or alongside them.

**Fix.** Honour a forwarded-for header only when the real socket peer is itself a trusted host (your own
reverse proxy). From any other peer, use the socket address. Audit any code path that reads the forwarded
header directly (for example a login handler with its own forwarded-IP handling) and apply the same
trusted-peer condition there.

---

## I can flip any user to "offline" on a vulnerable grid, as an unauthenticated user

**What.** The Hypergrid logout endpoint acts on a logout for any user/session with no proof that the caller
holds the session, letting an attacker mark a user offline and disrupt their presence-dependent state.

**How it works.** The endpoint calls the logout path directly on the supplied user and session identifiers
without verifying them against a known active session.

**Why it matters beyond disruption.** A user's "online" flag is exactly what gates the grid's duplicate-
presence protection: the "already logged in, displace the old session" logic only fires while the user reads
as online. Forcing a user offline defeats that gate, so a new login for the same account proceeds *without*
displacing the existing one. Repeated, this allows an unlimited number of concurrent authenticated sessions
for one account - duplicate avatars of the same user coexisting on the grid - which the duplicate-presence
check is specifically meant to prevent.

**Fix.** When the caller is not one of your own trusted hosts, only honour the logout if it proves knowledge
of the session - i.e. the session exists, belongs to the named user, and the user has an open travel record -
and otherwise refuse. Trusted internal callers are unaffected.

---

## I can brute-force any account's password with no rate limit, on a stock grid (enabled by default)

**What.** This one is a **default-configuration exposure, not a mistake unique to any grid**: the OpenID
server connector ships **uncommented** in the stock ROBUST example configs, in the same block as genuinely
load-bearing connectors and without the "uncomment if you use this" framing that actually-optional
connectors get. Any operator standing up ROBUST from the example config exposes it on the public port
without realising it is optional.

**How it works.** The endpoint authenticates a posted password directly against the real account, bypassing
every protection the normal login path has: allowed/denied client rules, minimum login level, bans, and any
login throttle. Companion requests leak whether an account exists, so an attacker enumerates valid usernames
and then brute-forces each one's password unthrottled. A success is a real, usable credential - full account
takeover.

**Fix.** If you do not use OpenID login (most grids do not), stop loading the connector - comment it out in
your service list and restart. Verify nothing else depends on it first (the only other reference is an
optional, separately-configured login-response field). Operators building from the stock example configs
should treat the uncommented connector line as something to remove unless they specifically need it;
commenting it out in the example templates would protect every future grid built from them.

---

## I can read login credentials out of a grid's logs

**What.** The XMLRPC login handler logs a password-equivalent login credential (the reusable login key) at
info level. Anyone with access to the logs - operators, log aggregation, support tooling - can read a
credential that can be replayed to log in.

**How it works.** The credential is written to the log line verbatim as part of normal login processing.

**Fix.** Remove the log line. The value is still used for login; it simply must not be logged. (Note: the
viewer-supplied password hash inherent to the login protocol is a separate matter and cannot be removed; this
issue is specifically about writing the reusable key to logs.)

---

## Additional issues for grids running the Gloebit money-module addon

These two affect the publicly-available Gloebit currency addon rather than core OpenSim, so they apply only
to grids that run it. Both stem from the addon following only the transaction/callback identifier and never
implementing the additional protections the payment provider's own documentation calls for.

### I can trigger or cancel a sale by forging a payment callback, as an unauthenticated user

**What.** The transaction-state callback endpoint accepts enact/consume/cancel callbacks authenticated only
by the transaction identifier, with no signature, shared secret, or source check. Knowing (or guessing) a
transaction ID lets an attacker drive the asset side of a sale - delivering the object/land or, conversely,
cancelling someone else's in-flight transaction. The transaction ID is not treated as secret by the provider,
so "keep the ID secret" is not a defence.

**Fix.** The grid builds the callback URLs itself and hands them to the provider, so add a second, server-side-
only secret: generate a random key when the transaction is created, append it to the callback URLs, and reject
any callback whose key does not match (using a constant-time comparison) before any state change runs. Store
the key with a non-destructive schema migration.

### I can bind my payment account to a victim's avatar, as an unauthenticated user (OAuth CSRF)

**What.** The authorization-complete callback links whatever payment account an authorization code belongs to
onto whatever avatar ID the request names, with no check that an authorization was actually pending for that
avatar. The anti-CSRF "state" parameter the provider's documentation requires is present only as a
commented-out TODO. An attacker completes their own real authorization, then replays the completion against a
victim's avatar ID, redirecting the victim's future earnings to the attacker's account.

**Fix.** Implement the "state" parameter as documented: mint a random state when building the authorization
URL, store it against the user, and on the completion callback require it to match an unconsumed stored value
(constant-time comparison) before linking the account. Make it one-shot so a captured callback cannot be
replayed. Store it with a non-destructive schema migration.

---

## Summary of remediations

- **Put a trusted-source allowlist in front of your control-plane endpoints.** The region agent-create,
  neighbour-hello, object-create and friends endpoints, and the ROBUST privileged instant-message dialogs,
  group-write and profile-write methods, and logout, should all refuse callers that are not your own ROBUST
  and region hosts (plus loopback). This one measure closes the majority of the account-takeover, credential-
  theft, and unauthorized-action issues above.
- **Fix source-IP trust first.** Only honour `X-Forwarded-For` from your own reverse proxy, or the allowlists
  above can be bypassed by spoofing.
- **Block the "return home" Hypergrid login path** so it cannot mint sessions; users relog to come home.
- **Add egress filtering** to the caller-supplied Hypergrid target URLs.
- **Disable the OpenID connector** unless you use it; it is on by default in the stock config.
- **Fix the map-tile lock leak, stop logging the login key, and gate the logout endpoint.**
- If you run the Gloebit addon, **add a callback secret and implement the OAuth state parameter.**
- Reject the in-world-script HTTP marker header on the region control endpoints, so a script on one of your
  own regions cannot be used to reach them from a trusted source IP.
