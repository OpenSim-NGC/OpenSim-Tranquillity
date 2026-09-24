# Security hardening guide

This guide describes the operational requirements for the security hardening in this branch. Read it before deploying the changes to a production grid.

## Control-plane trust

OpenSim exposes public HTTP endpoints for viewers and Hypergrid interoperability. Some of those endpoints are also used as an internal control plane. The hardening separates the two:

- Public viewer and federation traffic remains available where required.
- High-impact control-plane requests are accepted only from explicitly trusted simulator and service hosts.
- No implicit trust domain is inferred from grid topology, region coordinates, a shared message key, or a Hypergrid identity.

Configure trusted hosts in `[Network]` or `[Security]` on every region process that accepts region control-plane traffic:

```ini
[Network]
ControlPlaneTrustedHosts = 10.20.0.10, 10.20.0.21, 10.20.0.22
```

Entries can be IPv4 addresses, IPv6 addresses, hostnames, or full HTTP(S) URLs. Separate entries with commas, semicolons, pipes, or whitespace. Loopback is always trusted.

The configured local `[Network] hostname` is also trusted. This keeps same-host multi-region deployments working when regions contact each other through the host's LAN name instead of loopback.

Add the actual source addresses of:

- Every region/simulator that can create child agents, cross objects, announce neighbours, or relay friendship state to this process.
- ROBUST/GridServer hosts that send privileged control-plane requests to regions.
- Any trusted reverse proxy only when it is the direct TCP peer of the service.

Do not add public client networks, arbitrary foreign Hypergrid grids, or broad internet ranges.

### Grid login and child-agent `403` diagnosis

Viewer login requests to the XML-RPC/LLSD login endpoint are not directly gated by `ControlPlaneTrustedHosts`. However, a successful grid login still requires the login service to create the root agent on the selected region with `POST /agent/<avatar-id>/...`. If credentials are accepted but the login finishes with `403`, add the login service or ROBUST/GridServer host's actual outbound IP address to `ControlPlaneTrustedHosts` on the destination region host.

A child-agent create is `POST /agent/<avatar-id>/...` from the source simulator to the destination simulator. If a crossing, teleport, or visibility update fails with `403`, add the **source simulator's outbound IP address** to `ControlPlaneTrustedHosts` on the destination simulator.

### Neighbour hello `404` diagnosis

Neighbour startup sends `POST /region/<region-id>/...` to the neighbouring simulator. An untrusted caller deliberately receives `404 NotFound` so the endpoint is not exposed to scanners. A log entry such as `Exception on DoHelloNeighbourCall from Skye Garden back to Musings ... 404` uses confusing legacy wording: the first named region is the HTTP destination. In that example, add the outbound source address of **Musings** to `ControlPlaneTrustedHosts` on **Skye Garden**.

If the two regions run on different hosts, configure each destination host to trust the other host's actual outbound address. If they run in the same process or host via loopback, no entry is normally required.

If each region has its own per-region `.ini` with a `[Network]` section, verify that the destination process logs the expected `Trusted control-plane addresses` line at startup. Put `ControlPlaneTrustedHosts` in the per-region file if the shared setting is not present after per-region overrides are merged.

This is expected fail-closed behavior. There is no separate domain-association configuration. In a multi-host grid, each region should normally list all region-service hosts and relevant ROBUST hosts. A one-host standalone commonly needs no setting because loopback is trusted.

The protected region endpoints are:

- `POST /agent/...` -- agent creation only; query, update, and delete paths retain their protocol behavior.
- `POST /object/...` -- object crossings/creation.
- `POST /region/...` -- neighbour announcements.
- `POST /friends` -- inter-region friendship state.

The same trusted-host policy protects sensitive profile operations, Hypergrid groups write operations, privileged instant-message control dialogs, and untrusted Hypergrid logout requests.

Requests carrying `X-SecondLife-Shard` are rejected on control-plane paths even when their source address is trusted. This prevents in-world `llHTTPRequest` calls from using a trusted simulator as a control-plane proxy.

### Marketplace and third-party delivery services

No Kitely Market integration module is present in this source tree, so compatibility depends on the delivery protocol used by the external service.

Normal viewer login requests, CAPS, inventory, and asset-service workflows are not gated by `ControlPlaneTrustedHosts`. The server-to-region agent placement performed after login is gated as described above. A marketplace using ordinary user-facing paths should be unaffected.

A third-party delivery service that directly posts to a protected control-plane endpoint, particularly `POST /agent/...` or `POST /object/...`, will receive `403` unless its actual outbound source IP is listed in `ControlPlaneTrustedHosts`. Do not broadly allow a public marketplace network by default. First confirm the exact endpoint and source addresses with the provider, then allow only stable, provider-controlled addresses if that integration genuinely requires it.

Before rollout, perform a sandbox purchase and redelivery test. Capture the destination service logs and verify that the delivery uses ordinary inventory/capability APIs or, if it uses a protected endpoint, record the provider address and add it to the allowlist deliberately.

## Reverse proxies and client IPs

The HTTP server now reports the socket peer as the remote endpoint. It does not trust client-supplied `X-Forwarded-For` values. This is necessary for every source-address decision above.

If services sit behind a reverse proxy, put the proxy's source address in `ControlPlaneTrustedHosts` only when the proxy itself is responsible for access control and is the direct connection peer. Do not expose the protected service directly while relying on a forwarded header to identify callers.

## Hypergrid behavior

### Returning home

Hypergrid return-home requests to the local grid are refused unless they originate from the local login service. Users must log in again to return home. This intentionally disables Hypergrid "go home" return behavior because a visited grid can replay an avatar circuit.

### Egress filtering

Caller-supplied Hypergrid home and gatekeeper URLs are checked before outbound verification or agent-transfer requests. Targets resolving to loopback, private, link-local, CGNAT, ULA, multicast, or reserved addresses are rejected. The local grid gateway itself remains allowed.

This check validates DNS resolution before the request. It does not pin the resolved address for the TCP connection and does not independently constrain redirects. Operators should still enforce network egress restrictions at the firewall or proxy layer.

### Hypergrid user controls

- A foreign duplicate-presence request only displaces an existing foreign session when the stored session home matches the arriving agent's claimed HomeURI.
- Hypergrid friendship deletion requires an exact friend UUID and complete shared secret.
- `GodLikeRequestTeleport` and nonlocal god-kick IM dialogs require a trusted source. Ordinary text, typing, inventory, group, friendship, and normal teleport messages remain available to federation.

## Profiles and groups

Cross-grid public profile reads remain available. Sensitive profile methods require a trusted source and appear as unavailable to untrusted callers. This includes private notes, preferences/email, profile writes, picks/classifieds writes or deletes, interests writes, and user-data writes.

When the Groups addon is enabled, Hypergrid `POSTGROUP` and `ADDNOTICE` writes require a trusted source. Read and membership-token methods retain their existing behavior.

## Default services and logging

- The OpenID connector is disabled by default in `Robust.ini.example` and `Robust.HG.ini.example`. Enable it only when it is intentionally deployed and protected.
- Reusable web login keys are no longer logged by the XML-RPC login handler.
- Map tile requests always release their process-wide lock, including malformed requests.

## Gloebit addon

The optional Gloebit money addon now stores a random transaction callback key and requires it for enact, consume, and cancel callbacks. OAuth authorization uses a persisted, one-shot `state` value.

Before upgrading a grid that uses Gloebit, back up the database and test the transaction and user-table migrations for the deployed provider:

- `GloebitTransactions`: adds `CallbackKey`.
- `GloebitUsers`: adds `PendingAuthState`.

## Deployment checklist

1. Inventory region and ROBUST/GridServer source IPs.
2. Set `ControlPlaneTrustedHosts` on every region host.
3. Test child-agent creation, neighbouring regions, crossings, object crossings, friends, profiles, and Hypergrid travel between every host pair.
4. Confirm untrusted requests to protected endpoints receive denial responses.
5. Keep the service ports behind firewall rules in addition to the application allowlist.
6. If using Gloebit, test database migrations and an authorization/transaction callback in a non-production environment.
7. Record that Hypergrid return-home now requires relogin.
