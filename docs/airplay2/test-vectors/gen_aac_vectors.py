#!/usr/bin/env python3
"""Generates aac-44100-stereo-1024.json (docs/airplay2/SPEC-receiver.md 7.2, 11.1).

A 2 s 440 Hz tone is encoded once with the ffmpeg CLI to AAC-LC in ADTS framing, split into its ADTS frames and stored
as base64 *raw* AAC blocks with the 7-byte ADTS headers stripped (the receiver gets raw blocks on a buffered stream and
prepends the header itself, SPEC-receiver.md 5.2, so the tests do the same and nothing is double-framed). The expected
values come from decoding the same ADTS file with the ffmpeg CLI to S16: frame count and RMS over the whole decoded
output (AAC is not bit-exact across decoders, so the tests compare RMS within 10 % and the dominant frequency).

Usage (from the repository root; needs an ffmpeg executable with the native "aac" encoder on PATH or via --ffmpeg):

    python -I docs/airplay2/test-vectors/gen_aac_vectors.py [--ffmpeg path/to/ffmpeg.exe]

The commands it runs (also written into the JSON as "encode_command" / "decode_command"):

    ffmpeg -hide_banner -loglevel error -f lavfi -i sine=frequency=440:sample_rate=44100:duration=2 \
           -ac 2 -c:a aac -b:a 128k -f adts <tmp>/tone.aac
    ffmpeg -hide_banner -loglevel error -i <tmp>/tone.aac -f s16le -ac 2 -ar 44100 <tmp>/tone.pcm
"""

import argparse
import base64
import json
import math
import os
import shutil
import struct
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "aac-44100-stereo-1024.json")

FREQUENCY = 440
DURATION_S = 2
SAMPLE_RATE = 44100

ENCODE = ["-hide_banner", "-loglevel", "error", "-f", "lavfi",
          "-i", f"sine=frequency={FREQUENCY}:sample_rate={SAMPLE_RATE}:duration={DURATION_S}",
          "-ac", "2", "-c:a", "aac", "-b:a", "128k", "-f", "adts"]
DECODE = ["-hide_banner", "-loglevel", "error", "-f", "aac"]
DECODE_OUT = ["-f", "s16le", "-ac", "2", "-ar", str(SAMPLE_RATE)]


def split_adts(data):
    """Yields the raw payload of every ADTS frame (ISO/IEC 14496-3 1.A.2), checking the fields the receiver assumes."""
    pos = 0
    while pos < len(data):
        h = data[pos:pos + 7]
        if len(h) < 7 or h[0] != 0xFF or (h[1] & 0xF0) != 0xF0:
            raise ValueError(f"no ADTS sync word at offset {pos}")
        protection_absent = h[1] & 0x01
        profile = (h[2] >> 6) & 0x03
        sampling_index = (h[2] >> 2) & 0x0F
        channels = ((h[2] & 0x01) << 2) | ((h[3] >> 6) & 0x03)
        frame_length = ((h[3] & 0x03) << 11) | (h[4] << 3) | (h[5] >> 5)
        raw_blocks = h[6] & 0x03
        if not protection_absent or profile != 1 or sampling_index != 4 or channels != 2 or raw_blocks != 0:
            raise ValueError(f"unexpected ADTS header {h.hex()} at offset {pos}")
        yield data[pos + 7:pos + frame_length]
        pos += frame_length


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--ffmpeg", default=shutil.which("ffmpeg"))
    args = parser.parse_args()
    if not args.ffmpeg:
        sys.exit("ffmpeg not found; pass --ffmpeg")

    version = subprocess.run([args.ffmpeg, "-hide_banner", "-version"], check=True, capture_output=True,
                             text=True).stdout.splitlines()[0]
    with tempfile.TemporaryDirectory() as tmp:
        adts_path = os.path.join(tmp, "tone.aac")
        pcm_path = os.path.join(tmp, "tone.pcm")
        subprocess.run([args.ffmpeg] + ENCODE + [adts_path], check=True)
        subprocess.run([args.ffmpeg] + DECODE + ["-i", adts_path] + DECODE_OUT + [pcm_path], check=True)
        with open(adts_path, "rb") as f:
            adts = f.read()
        with open(pcm_path, "rb") as f:
            pcm = f.read()

    blocks = list(split_adts(adts))
    samples = struct.unpack(f"<{len(pcm) // 2}h", pcm)
    frames = len(samples) // 2
    rms = math.sqrt(sum(s * s for s in samples) / len(samples))

    vector = {
        "format": "AAC-LC, 44100 Hz, stereo, 1024 frames per block; raw blocks (ADTS headers stripped), decode in order",
        "generator": "docs/airplay2/test-vectors/gen_aac_vectors.py",
        "ffmpeg_version": version,
        "encode_command": "ffmpeg " + " ".join(ENCODE) + " tone.aac",
        "decode_command": "ffmpeg " + " ".join(DECODE) + " -i tone.aac " + " ".join(DECODE_OUT) + " tone.pcm",
        "tone_frequency_hz": FREQUENCY,
        "tone_duration_s": DURATION_S,
        "sample_rate": SAMPLE_RATE,
        "channels": 2,
        "frames_per_block": 1024,
        "expected_frames": frames,
        "expected_rms_s16": round(rms, 1),
        "blocks_base64": [base64.b64encode(b).decode("ascii") for b in blocks],
    }
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        json.dump(vector, f, indent=1)
        f.write("\n")
    print(f"{OUT}: {len(blocks)} blocks, {frames} frames, RMS {rms:.1f}")


if __name__ == "__main__":
    main()
