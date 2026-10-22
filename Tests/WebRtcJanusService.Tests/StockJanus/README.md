# Janus integration tests (opt-in)

These tests drive the voice region module and the Janus service, unchanged, against a real Janus
gateway with real WebRTC peers. They are skipped unless `WEBRTC_JANUS_TEST_URL` is set, so an
ordinary test run does not change.

They live in `Tests/WebRtcVoiceRegionModule.Tests/StockJanus`; this folder holds the shared support
code (`StockJanusSupport.cs`) and the peer script (`aiortc_peer.py`).

## What you need

- A Janus gateway you can discard, with the plugin you want to test (the default is the stock
  `janus.plugin.audiobridge`). Do not point the tests at a gateway that serves real users: they
  create rooms, and one test restarts the gateway's container.
- Python with the `aiortc` package, for the tests that use a peer.
- Docker, for the restart test.

## Environment variables

| Variable | Meaning |
|---|---|
| `WEBRTC_JANUS_TEST_URL` | Gateway API URL, for example `http://localhost:8088/janus`. Required. |
| `WEBRTC_JANUS_TEST_API_SECRET` | The gateway's `api_secret`. |
| `WEBRTC_JANUS_TEST_ADMIN_URL`, `WEBRTC_JANUS_TEST_ADMIN_SECRET` | The admin API URL and its secret. |
| `WEBRTC_JANUS_TEST_PLUGIN` | Plugin to drive. Default `janus.plugin.audiobridge`. |
| `WEBRTC_JANUS_TEST_PYTHON` | Path to a Python interpreter that has `aiortc`. Tests that need a peer are skipped without it. |
| `WEBRTC_JANUS_TEST_CONTAINER` | Name of the gateway's Docker container. The restart test is skipped without it. |
| `WEBRTC_JANUS_TEST_GRACE_SECONDS` | How long the room-lifetime test waits for an emptied room to disappear. Default 70. |

A gateway running in a container needs its media port range published, and `nat_1_1_mapping` set to
an address the test machine can reach, so the peers can connect.

## Run

    dotnet test Tests/WebRtcVoiceRegionModule.Tests --filter "FullyQualifiedName~StockJanus"

Audio checks play a sine tone from each peer and measure, with a Goertzel filter, how much of what
the other peer receives is at the expected frequency. A tone in a mix of other tones is judged by
the median of several short windows, so one late packet does not fail a run.
