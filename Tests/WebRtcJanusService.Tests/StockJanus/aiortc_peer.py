# Copyright (c) 2026 Legion Builds
#
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.
"""A real WebRTC peer for the opt-in Janus integration tests, driven by JSON lines.

The test process starts this script, reads one SDP offer from it, passes that offer to the voice
service as a viewer would, and writes the service's SDP answer back. It needs the aiortc package
(pip install aiortc) and no other third-party module.

stdout, one JSON object per line:
    {"event": "offer", "sdp": "..."}           the offer, candidates included
    {"event": "state", "ice": "...", "connection": "..."}   when either changes
    {"event": "measure", ...}                  the result of a "measure" command
    {"event": "error", "message": "..."}
stdin, one JSON object per line:
    {"cmd": "answer", "sdp": "..."}            apply the service's answer
    {"cmd": "measure", "seconds": 2.0, "freqs": [440, 880]}
                                               listen for that long, report the level of each frequency
    {"cmd": "stats"}                           RTP packet counts and the number of audio frames read
    {"cmd": "close"}

--tone HZ makes the peer talk with a sine of that frequency (omit it for a listen-only peer).
"""

import argparse
import array
import asyncio
import fractions
import json
import math
import sys
import time

import av
from aiortc import RTCPeerConnection, RTCSessionDescription
from aiortc.mediastreams import AudioStreamTrack, MediaStreamError

RATE = 48000
SAMPLES = 960  # 20 ms


class ToneTrack(AudioStreamTrack):
    """Sends a steady sine, paced in real time by AudioStreamTrack."""

    def __init__(self, freq, amplitude=0.3):
        super().__init__()
        self._freq = freq
        self._amp = amplitude
        self._n = 0

    async def recv(self):
        await self._pace()
        pcm = array.array("h", (
            int(self._amp * 32767 * math.sin(2 * math.pi * self._freq * (self._n + i) / RATE))
            for i in range(SAMPLES)))
        frame = av.AudioFrame(format="s16", layout="mono", samples=SAMPLES)
        frame.planes[0].update(pcm.tobytes())
        frame.sample_rate = RATE
        frame.pts = self._n
        frame.time_base = fractions.Fraction(1, RATE)
        self._n += SAMPLES
        return frame

    async def _pace(self):
        if not hasattr(self, "_t0"):
            self._t0 = time.monotonic()
        target = self._t0 + self._n / RATE
        delay = target - time.monotonic()
        if delay > 0:
            await asyncio.sleep(delay)


def goertzel_power(samples, freq, rate):
    """Mean power of one frequency in a block of samples (Goertzel), normalised to the block length."""
    n = len(samples)
    if n == 0:
        return 0.0
    k = 2 * math.cos(2 * math.pi * freq / rate)
    s1 = s2 = 0.0
    for x in samples:
        s0 = x + k * s1 - s2
        s2 = s1
        s1 = s0
    power = s1 * s1 + s2 * s2 - k * s1 * s2
    return 2 * power / (n * n)


class Peer:
    def __init__(self, tone):
        self.pc = RTCPeerConnection()
        self.tone = tone
        self.received = []  # (monotonic time, [float samples] mono, rate)
        self._closing = False

    def emit(self, obj):
        sys.stdout.write(json.dumps(obj) + "\n")
        sys.stdout.flush()

    async def read_remote(self, track):
        try:
            await self._read_remote(track)
        except Exception as e:
            self.emit({"event": "error", "message": f"read_remote {type(e).__name__}: {e}"})

    async def _read_remote(self, track):
        while not self._closing:
            try:
                frame = await track.recv()
            except MediaStreamError:
                return
            channels = len(frame.layout.channels)
            raw = bytes(frame.planes[0])[:frame.samples * channels * 2]  # s16, interleaved
            data = array.array("h")
            data.frombytes(raw)
            if channels > 1:
                mono = [sum(data[i:i + channels]) / channels / 32768.0 for i in range(0, len(data) - channels + 1, channels)]
            else:
                mono = [x / 32768.0 for x in data]
            self.received.append((time.monotonic(), mono, frame.sample_rate))
            if len(self.received) > 3000:
                del self.received[:1000]

    async def offer(self):
        pc = self.pc
        if self.tone:
            pc.addTrack(ToneTrack(self.tone))
        else:
            pc.addTransceiver("audio", direction="recvonly")

        @pc.on("track")
        def on_track(track):
            if track.kind == "audio":
                asyncio.ensure_future(self.read_remote(track))

        last = {}

        def report():
            now = {"ice": pc.iceConnectionState, "connection": pc.connectionState}
            if now != last:
                last.update(now)
                self.emit({"event": "state", **now})

        pc.on("iceconnectionstatechange", report)
        pc.on("connectionstatechange", report)
        await pc.setLocalDescription(await pc.createOffer())
        self.emit({"event": "offer", "sdp": pc.localDescription.sdp})

    async def answer(self, sdp):
        await self.pc.setRemoteDescription(RTCSessionDescription(sdp=sdp, type="answer"))

    async def measure(self, seconds, freqs):
        start = time.monotonic()
        await asyncio.sleep(seconds)
        window = [f for f in self.received if f[0] >= start]
        rate = window[0][2] if window else RATE
        samples = [x for _, block, _ in window for x in block]
        rms = math.sqrt(sum(x * x for x in samples) / len(samples)) if samples else 0.0
        power = {str(f): goertzel_power(samples, f, rate) for f in freqs}
        total = sum(x * x for x in samples) / len(samples) if samples else 0.0
        self.emit({"event": "measure", "frames": len(window), "samples": len(samples), "rms": rms,
                   "power": power, "total_power": total})

    async def stats(self):
        out = {}
        for report in (await self.pc.getStats()).values():
            if report.type in ("inbound-rtp", "outbound-rtp"):
                out[report.type] = {"packets": report.packetsReceived if report.type == "inbound-rtp" else report.packetsSent}
        self.emit({"event": "stats", "frames_read": len(self.received), "rtp": out})

    async def close(self):
        self._closing = True
        await self.pc.close()


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tone", type=float, default=0.0)
    args = ap.parse_args()
    peer = Peer(args.tone)
    loop = asyncio.get_running_loop()
    await peer.offer()
    while True:
        line = await loop.run_in_executor(None, sys.stdin.readline)
        if not line:
            break
        try:
            cmd = json.loads(line)
            if cmd["cmd"] == "answer":
                await peer.answer(cmd["sdp"])
            elif cmd["cmd"] == "measure":
                await peer.measure(float(cmd.get("seconds", 2.0)), cmd.get("freqs", []))
            elif cmd["cmd"] == "stats":
                await peer.stats()
            elif cmd["cmd"] == "close":
                break
        except Exception as e:  # report and keep serving
            peer.emit({"event": "error", "message": f"{type(e).__name__}: {e}"})
    await peer.close()


if __name__ == "__main__":
    asyncio.run(main())
