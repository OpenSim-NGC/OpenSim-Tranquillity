# Setting up WebRTC voice for a test

A step-by-step path from "no voice" to a region where a viewer with WebRTC voice can talk, for a test grid. It
uses one machine, one region server process and one voice server in a Docker container. Each step is one command
or one file edit, followed by what you should see. For the reasons behind a setting, follow the links to the
other pages: [overview.md](overview.md) says what the addon is, [deployment.md](deployment.md) covers real
layouts, [configuration.md](configuration.md) lists every key, [security.md](security.md) the secrets and
[troubleshooting.md](troubleshooting.md) the log lines.

## Choose a route

You need a voice server (a Janus gateway) for the region to talk to. Choose one; the rest of the page is the same
for both.

| | Route A: stock Janus AudioBridge | Route B: slvoice mixer 1.2.0 |
|---|---|---|
| What you get | Flat mixing: everyone in a room hears everyone | Spatial mixing, and the mixer-only features of [overview.md](overview.md) |
| What you run | An image you build from `os-webrtc-janus-docker` (a few minutes) | The published 1.2.0 image (named in [overview.md](overview.md#the-slvoice-mixer)) |
| `PluginName` | `janus.plugin.audiobridge` (the default, not set) | `janus.plugin.slvoice` |

Do route A or route B, then the common sections ("The region server", "Robust", "Check that it works"). Run one
route at a time: both use the same ports. The steps use the ports of the two projects' own defaults.

| What | Port | Used by |
|---|---|---|
| Janus HTTP API (`/voice`) | TCP 14223 | The region server |
| Janus admin API (`/voiceAdmin`) | TCP 14225 | The region server only |
| Media (RTP) | UDP 10000 to 10200 | Viewers |

If a port is taken on your machine, pick another and use it everywhere in this page. On Windows, some ports
cannot be bound at all because the system reserves them; list the ranges with
`netsh int ipv4 show excludedportrange protocol=tcp` (and `protocol=udp`) and pick ports outside every range.

## Before you start

- **A region server built from this tree**, and a copy of this source tree for one file
  (`Addons/os-webrtc-janus/os-webrtc-janus.ini`). The voice assemblies (`WebRtc*.dll`) are built with the region
  server; check that they sit beside `OpenSim.Server.RegionServer.exe`.
- **Docker.** On Windows use Docker Desktop with the WSL2 backend. The commands below are for a Linux shell, WSL or
  Git Bash. In Git Bash, put `MSYS_NO_PATHCONV=1` in front of a `docker run` command that has a `/path` inside
  the container, or Git Bash rewrites the path.
- **A viewer that supports WebRTC voice** (check your viewer's release notes), and a second avatar if you want to
  hear someone.
- **Two secrets**, one for the Janus API and one for its admin API. Make each 48 random hex characters:

  ```sh
  openssl rand -hex 24
  ```

  Run it twice. Below, `<API SECRET>` and `<ADMIN SECRET>` stand for the two values. The same values go in the
  voice server's configuration and in the region's. Keep them out of version control.
- **A public address**, `<PUBLIC ADDRESS>`: the address viewers can reach the voice server's media ports on. For a
  test with viewers on your own network it is the machine's address on that network (`192.0.2.10` in the examples);
  for viewers elsewhere it is the address your router forwards the media ports to.

## Route A: the stock Janus AudioBridge

**Step 1. Get the project.**

```sh
git clone https://github.com/Misterblue/os-webrtc-janus-docker.git
cd os-webrtc-janus-docker
```

You should see `Cloning into 'os-webrtc-janus-docker'...`. The folder holds `build-janus.sh`, a `Dockerfile` and
`etc/janus/`, the Janus configuration you edit below.

**Step 2. Pin the Janus version.** In `build-janus.sh`, change the line

```
JANUS_GIT_BRANCH=master
```

to

```
JANUS_GIT_BRANCH=v1.4.1
```

Janus's current development branch does not compile against the base image this project uses (the SIP plugin
fails to build); release 1.4.1 does.

**Step 3. Build the image.**

```sh
./build-janus.sh
```

It takes several minutes and ends without an error. You should see the image in `docker image ls
os-webrtc-janus-docker`. To confirm the version:

```sh
docker run --rm --entrypoint /opt/janus/bin/janus os-webrtc-janus-docker:latest --version
```

You should see `Janus version: 1401 (1.4.1)`.

**Step 4. Set the secrets and the media settings.** Edit `etc/janus/janus.jcfg`. Four lines are already there, some
commented out with `#`. Make them read:

```
	api_secret = "<API SECRET>"
	admin_secret = "<ADMIN SECRET>"
	rtp_port_range = "10000-10200"
	nat_1_1_mapping = "<PUBLIC ADDRESS>"
```

`api_secret` and `admin_secret` must equal the region's `APIToken` and `AdminAPIToken` (the `[JanusWebRtcVoice]` section of "The region server", below).
`rtp_port_range` is the UDP range viewers send media to; the range you publish when you start Janus and open in the firewall
must be this one. `nat_1_1_mapping` is the address Janus tells viewers to send media to. Inside a container, Janus
otherwise advertises the container's private address and media fails even on one machine.

**Step 5. Set the ports and paths.** Edit `etc/janus/janus.transport.http.jcfg`. Under `general:` and `admin:` set:

```
	base_path = "/voice"
	port = 14223
	admin_base_path = "/voiceAdmin"
	admin_http = true
	admin_port = 14225
```

Do not use the project's `run-janus.sh` or `updateConfiguration.sh` for this: `updateConfiguration.sh` leaves
the admin API switched off and takes the admin port from the wrong variable, and the region needs the admin API.
(The project's `docker-compose.yml` uses `network_mode: host`, which Docker Desktop on Windows and macOS does not
make reachable from the host; the next step publishes ports instead and works on every platform.)

**Step 6. Start Janus.**

```sh
docker run -d --name voice-janus -v "$(pwd)/etc/janus:/opt/janus/etc/janus" -p 127.0.0.1:14223:14223 -p 127.0.0.1:14225:14225 -p 10000-10200:10000-10200/udp os-webrtc-janus-docker:latest
```

It prints a container id. The two TCP ports are published on the loopback address, which suits a region server on
the same machine; for a region server on another host drop `127.0.0.1:` (and read [security.md](security.md)
about plain `http://` first). `docker ps` should list `voice-janus` as `Up`.

**Step 7. Check the API.**

```sh
curl http://127.0.0.1:14223/voice/info
```

You should see JSON with `"name": "Janus WebRTC Server"`, `"version_string": "1.4.1"` and your
`<PUBLIC ADDRESS>` as `"public-ip"`. Janus's log (`docker logs voice-janus`) should show the lines
`HTTP webserver started (port 14223, /voice path listener)...`, `Admin/monitor HTTP webserver started (port
14225, /voiceAdmin path listener)...` and `JANUS AudioBridge plugin initialized!`, and a warning that no STUN
server is set, which does not matter for a test on one network.

**Step 8. Check the admin API and the admin secret.**

```sh
curl -X POST http://127.0.0.1:14225/voiceAdmin -d '{"janus":"list_sessions","transaction":"t1","admin_secret":"<ADMIN SECRET>"}'
```

You should see `"janus": "success"` and an empty `"sessions"` list. `"janus": "error"` with code 403 and
`Unauthorized request (wrong or missing secret/token)` means the admin secret in `janus.jcfg` is not the one you
sent.

**Step 9. Open the firewall** for a viewer on another machine (see "Firewall" below). A viewer on the same machine does
not need it.

To stop Janus later, `docker stop voice-janus`; to start it again, `docker start voice-janus`. Janus does not
react to the stop signal, so Docker waits ten seconds and then ends the container, which reports exit status 137.
That is expected.

## Route B: the slvoice mixer 1.2.0

The mixer's own documents are the reference: its `README.md`, `docs/docker-notes.md` and `docs/RELEASES.md`, in the
repository linked from [overview.md](overview.md#the-slvoice-mixer) (tag `v1.2.0`). This is the shortest path to a
working test.

**Step 1. Make a folder and fetch the two files.**

```sh
mkdir voice-mixer
cd voice-mixer
MIXER_RAW=https://raw.githubusercontent.com/JohnLegionH/legion-voice-mixer/v1.2.0
curl -LO $MIXER_RAW/docker-compose.yml
curl -LO $MIXER_RAW/env.sample
```

You should see the two files in the folder. Nothing else is needed: the image holds Janus 1.4.1 and the mixer.

**Step 2. Pin the image.** In `docker-compose.yml`, on the `image:` line under `janus:`, change `:latest` to `:1.2.0`:

```
    image: ghcr.io/johnlegionh/legion-voice-mixer:1.2.0
```

**Step 3. Make the environment file.**

```sh
cp env.sample .env
```

**Step 4. Edit `.env`.** Set these lines (they are empty or commented out in the sample):

```
JS_PUBLIC_IP=<PUBLIC ADDRESS>
JS_API_SECRET=<API SECRET>
JS_ADMIN_SECRET=<ADMIN SECRET>
JS_ADMIN_BIND=127.0.0.1
```

`JS_API_SECRET` and `JS_ADMIN_SECRET` must equal the region's `APIToken` and `AdminAPIToken`; the container
refuses to start if either is empty. `JS_PUBLIC_IP` is the address viewers are given for media.
`JS_ADMIN_BIND=127.0.0.1` publishes the admin API on the loopback address only, which suits a region server on the
same machine (use the host's address if the region server is elsewhere). The sample already has the ports of the
table above: `JS_HTTP_PORT=14223`, `JS_ADMIN_PORT=14225`, `JS_RTP_PORT_RANGE=10000-10200`, and the base paths
`/voice` and `/voiceAdmin`, which are the paths the addon expects.

**Step 5. Start the mixer.**

```sh
docker compose up -d
```

The first run pulls the image. You should see `Container voice-mixer-janus-1  Started`, and `docker ps` should
list it as `Up`.

**Step 6. Check the API.**

```sh
curl http://127.0.0.1:14223/voice/info
```

You should see JSON with `"version_string":"1.4.1"` and, in the `plugins` object, `"janus.plugin.slvoice"` listed
with a description of a spatial voice mixer.

**Step 7. Read the mixer's own check.**

```sh
docker exec voice-mixer-janus-1 legion-voice-selfcheck
```

On a test with a private `JS_PUBLIC_IP` you should see `PASS` on the line for the RTP port range ("is bindable and
matches Janus") and on the line for signalling ("HTTP transport answers"). `FAIL` on the first line, "advertised
address is public", is expected there: the check is telling you that a viewer outside your network cannot reach a
private address. The container's start-up log (`docker logs voice-mixer-janus-1`) prints
the same finding as two `ERROR` lines for the same reason. Both go away when `JS_PUBLIC_IP` (or `JS_PUBLIC_HOST`)
is an address viewers can reach; the mixer's `docs/recipe-home-hosted.md` and `docs/recipe-vps.md` cover that.

**Step 8. Open the firewall** for a viewer on another machine (next section).

To stop the mixer later, `docker compose stop` in the same folder.

## Firewall

A viewer on the same machine as the voice server works without any of this. For a viewer on another machine, three
things must be reachable, and a container's published ports, the host's firewall and any router must all agree:

| Port | Protocol | From | Why |
|---|---|---|---|
| 10000 to 10200 (your `rtp_port_range` or `JS_RTP_PORT_RANGE`) | UDP | Viewers | The audio itself. If the viewer joins the room but there is no sound, or the connection drops about ten seconds after joining, this is usually the cause |
| 14223 | TCP | The region server | The Janus API |
| 14225 | TCP | The region server only | The admin API. Never forward it from the internet |

One UDP port is used for each person in voice at the same time; see "Sizing" in [deployment.md](deployment.md).
Your region's own UDP and HTTP ports are the ones every region needs. A viewer on another network also needs the
router to forward the media range to the voice server and `<PUBLIC ADDRESS>` to be your public address;
[deployment.md](deployment.md) ("Networking") explains why, and tells you to test from a second machine.

## The region server

These steps are the same for both routes, with one extra line for route B.

**Step 1. Copy the configuration file.** Copy `Addons/os-webrtc-janus/os-webrtc-janus.ini` from the source tree into
the region server's configuration folder: the folder named by `[Startup] inidirectory`, `config` next to the
program unless you changed it. Every `.ini` file in that folder is read after `OpenSim.ini`, so the values you
set here win over it. If another file in that folder also sets a `[WebRtcVoice]` key, keep only one of them.

```sh
cp Addons/os-webrtc-janus/os-webrtc-janus.ini <region server folder>/config/os-webrtc-janus.ini
```

**Step 2. Switch voice on.** In `[WebRtcVoice]`, change `Enabled=false` to `Enabled = true` and add a STUN server
under it (viewers outside your network need one; see "STUN servers" in [overview.md](overview.md)):

```ini
[WebRtcVoice]
    Enabled = true
    StunServers = stun:stun.l.google.com:19302
    SpatialVoiceService = WebRtcJanusService.dll:WebRtcJanusService
    NonSpatialVoiceService = WebRtcJanusService.dll:WebRtcJanusService
```

The two service keys are already in the file with these values; leave them. Both pointing at
`WebRtcJanusService.dll` means this region server talks to Janus itself.

**Step 3. Set the four Janus values.** In `[JanusWebRtcVoice]` replace the sample values. All four must be set, or
the Janus service disables itself:

```ini
[JanusWebRtcVoice]
    JanusGatewayURI = http://127.0.0.1:14223/voice
    APIToken = <API SECRET>
    JanusGatewayAdminURI = http://127.0.0.1:14225/voiceAdmin
    AdminAPIToken = <ADMIN SECRET>
```

The sample's admin address, `http://janus.example.org/admin`, has neither the port nor the `/voiceAdmin` path.
Use `127.0.0.1` when the region server runs on the same machine as the container, otherwise the container host's
name or address. See [configuration.md](configuration.md) for the other keys of this section.

**Step 4. Route B only: name the plugin.** Add one line to `[JanusWebRtcVoice]`:

```ini
    PluginName = janus.plugin.slvoice
```

The default, `janus.plugin.audiobridge`, is the stock plugin. A mixer-only switch left off here stays off with
either plugin; turn the others on later, one at a time ([overview.md](overview.md)).

**Step 5. Start the region server** and read its log.

## Robust

For this test, nothing changes on Robust. Spatial voice and the non-spatial voice for calls both run inside the
region server process (see "The region server"), which talks to Janus; the grid server is not involved and needs no voice section.
Keep it that way until you need one of these, each described in [deployment.md](deployment.md):

- Group calls and conferences need the Groups module on the grid.
- Calls that must work between several region server processes need either `CarrierSecret` on each region server
  or the call session service on Robust (`SessionStoreMode = grid`). One process needs neither.

If login is refused with a 403 and the region log says `[CONTROL PLANE ACCESS]: Refusing "POST" "/agent/..."
... source address is not in ControlPlaneTrustedHosts`, that is the region server's trust list, not voice: add the
address Robust connects from to `[Network] ControlPlaneTrustedHosts` ([Docs/security/Hardening.md](../security/Hardening.md)).

## Check that it works

**Step 1. Read the start-up lines.** In the region server's log, look for these four lines (the exact wording is in
[troubleshooting.md](troubleshooting.md)):

```
[REGION WEBRTC VOICE]: enabled
[JANUS WEBRTC SERVICE] Janus plugin (mixer) = janus.plugin.audiobridge
[JANUS WEBRTC SERVICE] Enabled
[WEBRTC VOICE SERVICE MODULE] WebRtcVoiceService enabled
```

Route B shows `janus.plugin.slvoice` in the second. An informational line says cross-process voice carriers are
off because `CarrierSecret` is not set; that is right for one process. If `[JANUS WEBRTC SERVICE] Enabled` is
missing, the log has the reason: `failed connection to Janus Gateway. Disabled` means a wrong address, a wrong
secret or a Janus that is not running; `configuration section missing required fields` means one of the four values
is empty. If `[REGION WEBRTC VOICE]: enabled` is missing, `Enabled` is not `true` in the file the region read.

**Step 2. Ask Janus through the region.** In the region server console:

```
janus list rooms
```

Route A prints a table with one room, `1234`, `Demo Room`, which the stock Janus ships with. Route B prints the
table header and no rows until someone joins. If it prints an error instead, the region cannot reach Janus.

```
show voice sessions
```

You should see `Call sessions: kept in this process (SessionStoreMode local); 0 held.`

```
janus info
```

You should see one line of JSON with `"version_string":"1.4.1"`. If it does not, the region cannot reach Janus.

**Step 3. Join with a viewer.** Log in with a viewer that supports WebRTC voice, go to the region, and switch voice
on in the viewer's preferences (the viewer decides whether it offers a choice of voice system). Stand on a parcel
whose land settings allow voice, and speak. A second avatar in the same region should hear you; on route A,
everyone in the room hears everyone at the same level, on route B the sound follows distance and direction.

The same join shows on the voice server, on both routes. In the region console, `janus list rooms` now lists a
room for the parcel with `Num` 1 and a line for the participant, which names the avatar's id:

```
        Room     Description   Num SampleRate Spatial Recording
  1881733002 <region id>/local/1/     1      48000       1       0
      2323686956572555/<avatar id>,muted=0,talking=0,pos=50
```

(The room number, the participant number and, on route B, the last three values differ.) The voice server's log
shows it too. Route A: `docker logs voice-janus` may print `Room <number> already exists!` at error level when a
second person joins a room the region has already made; the join still succeeds. Route B: `docker logs
voice-mixer-janus-1` shows `Participant <number> (<avatar id>) joined room <number>`, and `Handling request
'leave'` when the viewer logs out.

**Step 4. Test from a second machine** before you call it done: a viewer on the machine that runs the region can have
voice when everyone else cannot ([deployment.md](deployment.md), "Networking").

**If it does not work.** No sound with a connected viewer: the media range and the public address, above and in
[troubleshooting.md](troubleshooting.md) ("Viewers connect but hear nothing"). No voice at all: the first two checks above.
The viewer is refused: [troubleshooting.md](troubleshooting.md) lists each refusal and its HTTP code.

## Next

- Turn on calls, group calls and conferences: [overview.md](overview.md) ("Optional features").
- More than one region server process: [deployment.md](deployment.md).
- Before the voice server is reachable from outside your network: [security.md](security.md) (https, the join
  capability).
- Every key: [configuration.md](configuration.md).
