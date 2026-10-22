# WebRTC voice documentation

Documentation for the `os-webrtc-janus` addon, which gives viewers that support WebRTC voice spatial voice, avatar-to-avatar
calls, group calls, conferences and moderation through a Janus gateway or a spatial mixer. The pages are written for grid
operators first; the last three are reference for people who maintain the addon or a mixer.

## The documents

| Document | What it covers |
|---|---|
| [quickstart.md](quickstart.md) | Setting up WebRTC voice for a test, step by step: a Janus gateway (the stock AudioBridge or the slvoice mixer) in Docker, the region server's settings, the firewall, and how to check it works |
| [overview.md](overview.md) | What the addon does, the pull requests that add each feature, choosing a voice server (the stock Janus AudioBridge or the slvoice mixer), a minimal set-up, the defaults that change from develop, known limits |
| [deployment.md](deployment.md) | Region layouts, the ports a viewer must reach, calls across region server processes, the call session service on the grid server, running the slvoice mixer, sizing |
| [configuration.md](configuration.md) | Every setting with its default, grouped by the pull request that adds it |
| [security.md](security.md) | Gateway secrets, https, the join capability, who may call and join what |
| [moderation.md](moderation.md) | Moderating a parcel's voice, and group calls and conferences, from the viewer and the console |
| [positions.md](positions.md) | The position feed (observe mode): what it needs, what it sends, what it costs, what it logs |
| [troubleshooting.md](troubleshooting.md) | Start-up lines, log lines, console commands, refusals and their HTTP codes, common problems |
| [feature-spec.md](feature-spec.md) | What the addon implements and what it expects of a mixer, feature by feature |
| [architecture.md](architecture.md) | The projects, the request flow, the Janus surface, how a different Janus plugin can be substituted |
| [mixer-feed-protocol.md](mixer-feed-protocol.md) | The region server's view of the messages it sends to a slvoice mixer and the replies it reads; the mixer's own copy is the canonical protocol document |

## Reading order for an operator

1. [quickstart.md](quickstart.md): to see voice working once on a test grid, before you plan a layout.
2. [overview.md](overview.md): what you can switch on, which voice server each feature needs, and a minimal set-up.
3. [deployment.md](deployment.md): how to lay out regions, the gateway or mixer and, for more than one region server process,
   the call session service.
4. [configuration.md](configuration.md): the settings, while you edit the ini file.
5. [security.md](security.md): secrets and https, before the gateway is reachable from outside.
6. [moderation.md](moderation.md) and [positions.md](positions.md), for the features you turn on.
7. [troubleshooting.md](troubleshooting.md): when something does not work, and to know what a healthy start-up looks like.

[feature-spec.md](feature-spec.md), [architecture.md](architecture.md) and [mixer-feed-protocol.md](mixer-feed-protocol.md) are
for reference.

## Elsewhere

- `Addons/os-webrtc-janus/README.md` builds and configures the addon, and has the example ini files.
- The slvoice mixer is a separate project, named and linked in [overview.md](overview.md#the-slvoice-mixer). The features
  marked "slvoice mixer" in these pages need release 1.2.0 or later. Its `docs/voice/mixer-feed-protocol.md` is the canonical
  protocol document and its `docs/RELEASES.md` lists what changes on upgrade.
- `Docs/security/Hardening.md` covers the region server's control-plane trust list, which the voice addon does not read.
