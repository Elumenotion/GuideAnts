#!/usr/bin/env python3
"""Multi-speaker dialogue synthesis via raw audiocpp_server (chatterbox or any TTS engine).

Takes a JSON dialogue file and produces a single mixed WAV with optional
per-line overlap (talk-over / interruption). Stdlib + numpy only.

Dialogue format (JSON array):
  [
    {"voice": "narrator", "text": "What's the air speed...?"},
    {"voice": "bm_george", "text": "African or European?", "overlap_ms": 400},
    {"voice": "bf_alice", "text": "Oh no, you two...", "overlap_ms": 600}
  ]

Fields per line:
  voice       - voice preset id (required)
  text        - text to speak (required)
  overlap_ms  - how many ms this line starts BEFORE the previous line ends
                (default 0 = clean turn-taking; >0 = interruption/talk-over)
  seed        - explicit seed for this line (default: auto per-speaker seed)

Seeds:
  By default, a deterministic seed is derived from the voice name
  (hash(voice_name) % 100000) so each speaker sounds consistent across
  all their lines. Pass --seed-map or per-line "seed" to override.

Usage:
  python3 multi_speaker.py dialogue.json -o scene.wav
  python3 multi_speaker.py dialogue.json -o scene.wav --model chatterbox
  python3 multi_speaker.py dialogue.json -o scene.wav --seed-map '{"narrator":1,"alice":2}'

Remote mode: set AUDIOCPP_SKILL_BASE_URL + AUDIOCPP_SKILL_TOKEN (same as
all other audiocpp skills). The script stages nothing (voices are
server-side presets) and calls /tts/v1/audio/speech per line.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import sys
import urllib.error
import wave

import numpy as np

# Reuse the same gateway helpers as engine_tool.py
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from skill_gateway_client import (
    fail_http,
    gateway_engine_prefix,
    gateway_request,
    using_skill_gateway,
)

ENGINE_TTS_DEFAULT = "http://127.0.0.1:18084"


def _request_json(url: str, payload: dict | None = None, timeout: int = 280):
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    request = urllib.request.Request(
        url,
        data=data,
        method="POST" if payload is not None else "GET",
        headers={"Content-Type": "application/json"} if payload is not None else {},
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return response.read()


def resolve_tts_model(engine_url: str, explicit: str | None) -> str:
    """Auto-detect the loaded TTS model id, same logic as engine_tool.py."""
    if explicit:
        return explicit
    if using_skill_gateway():
        try:
            body = json.loads(gateway_request("/health", timeout=10).decode("utf-8"))
        except Exception as exc:
            sys.stderr.write(f"Could not auto-detect model from gateway /health: {exc}\n")
            sys.exit(1)
        wrappers = body.get("wrappers") or {}
        wrapper = (wrappers.get("tts") or {}).get("body") or {}
        if not wrapper:
            wrapper = ((body.get("upstream") or {}).get("wrapperTts") or {}).get("body") or {}
        model = wrapper.get("catalogEntryId") if isinstance(wrapper, dict) else None
        if not model:
            sys.stderr.write(f"TTS wrapper has no catalogEntryId: {json.dumps(body)[:300]}\n")
            sys.exit(1)
        return model
    # Co-located fallback
    try:
        body = json.loads(_request_json("http://127.0.0.1:8084/health"))
    except Exception as exc:
        sys.stderr.write(f"Cannot detect model: {exc}\n")
        sys.exit(1)
    model = body.get("catalogEntryId")
    if not model:
        sys.stderr.write(f"No catalogEntryId in TTS health: {json.dumps(body)[:300]}\n")
        sys.exit(1)
    return model


def auto_seed(voice: str) -> int:
    """Deterministic seed from voice name so the same speaker is stable across lines."""
    return int(hashlib.md5(voice.encode("utf-8")).hexdigest(), 16) % 100000


def synth_line(model: str, engine_url: str, voice: str, text: str, seed: int | None) -> bytes:
    """Call /v1/audio/speech and return raw WAV bytes."""
    payload: dict = {"model": model, "input": text, "voice": voice}
    if seed is not None:
        payload["seed"] = seed
    try:
        if using_skill_gateway():
            prefix = gateway_engine_prefix(engine_url)
            return gateway_request(f"{prefix}/v1/audio/speech", payload=payload)
        else:
            return _request_json(f"{engine_url.rstrip('/')}/v1/audio/speech", payload)
    except urllib.error.HTTPError as exc:
        fail_http(exc, f"/v1/audio/speech [{voice}]")
        raise


def wav_bytes_to_float(raw: bytes) -> tuple[np.ndarray, int]:
    """Parse PCM16 mono WAV bytes -> (float32 array, sample_rate)."""
    buf = io.BytesIO(raw)
    with wave.open(buf, "rb") as w:
        sr = w.getframerate()
        nch = w.getnchannels()
        frames = w.readframes(w.getnframes())
    arr = np.frombuffer(frames, dtype=np.int16).astype(np.float32) / 32768.0
    if nch > 1:
        arr = arr.reshape(-1, nch).mean(axis=1)
    return arr, sr


def mix_timeline(clips: list[np.ndarray], sr: int, starts_s: list[float]) -> np.ndarray:
    """Place clips on a shared timeline at their start offsets (additive mix)."""
    total_samples = max(int((starts_s[i] + len(clips[i]) / sr) * sr) for i in range(len(clips)))
    master = np.zeros(total_samples, dtype=np.float32)
    for i, arr in enumerate(clips):
        s = int(starts_s[i] * sr)
        e = min(s + len(arr), total_samples)
        master[s:e] += arr[: e - s]
    peak = np.max(np.abs(master))
    if peak > 0.95:
        master = master * (0.95 / peak)
    return master


def main() -> None:
    parser = argparse.ArgumentParser(description="Multi-speaker dialogue synthesis with overlap")
    parser.add_argument("dialogue", help="Path to JSON dialogue file")
    parser.add_argument("-o", "--output", required=True, help="Output WAV path")
    parser.add_argument("--engine-url", default=ENGINE_TTS_DEFAULT)
    parser.add_argument("--model", default=None, help="TTS model id (auto-detected if omitted)")
    parser.add_argument("--seed-map", default=None, help="JSON dict mapping voice->seed (overrides auto)")
    args = parser.parse_args()

    # Load dialogue
    with open(args.dialogue, "r", encoding="utf-8") as f:
        lines = json.load(f)
    if not isinstance(lines, list) or not lines:
        sys.stderr.write("Dialogue file must be a non-empty JSON array\n")
        sys.exit(1)
    for i, line in enumerate(lines):
        if "voice" not in line or "text" not in line:
            sys.stderr.write(f"Line {i+1} missing 'voice' or 'text': {line}\n")
            sys.exit(1)

    # Seed map
    seed_map: dict[str, int] = {}
    if args.seed_map:
        seed_map = json.loads(args.seed_map)

    model = resolve_tts_model(args.engine_url, args.model)
    engine_url = args.engine_url

    print(f"Model: {model}")
    print(f"Lines: {len(lines)}")
    print()

    # Synthesize all lines
    clips: list[np.ndarray] = []
    sr: int | None = None
    for i, line in enumerate(lines):
        voice = line["voice"]
        text = line["text"]
        seed = line.get("seed", seed_map.get(voice, auto_seed(voice)))
        print(f"[{i+1:2d}/{len(lines)}] {voice:14} seed={seed:6d}  {text[:60]}")
        raw = synth_line(model, engine_url, voice, text, seed)
        if raw[:4] != b"RIFF":
            sys.stderr.write(f"Line {i+1}: non-WAV response ({len(raw)} bytes)\n")
            sys.exit(1)
        arr, this_sr = wav_bytes_to_float(raw)
        sr = sr or this_sr
        clips.append(arr)
        print(f"         {len(arr)/this_sr:.2f}s")

    assert sr is not None

    # Compute start times
    starts: list[float] = [0.0]
    for i in range(1, len(lines)):
        prev_dur = len(clips[i - 1]) / sr
        overlap_s = lines[i].get("overlap_ms", 0) / 1000.0
        starts.append(starts[i - 1] + prev_dur - overlap_s)

    total = max(starts[i] + len(clips[i]) / sr for i in range(len(clips)))
    print(f"\nTimeline: {total:.1f}s")
    for i in range(len(clips)):
        end = starts[i] + len(clips[i]) / sr
        ov = lines[i].get("overlap_ms", 0)
        tag = f"  [overlaps prev by {ov}ms]" if ov > 0 else ""
        print(f"  {starts[i]:6.2f}s -> {end:6.2f}s  {lines[i]['voice']:14}{tag}")

    # Mix
    master = mix_timeline(clips, sr, starts)

    # Write output (preserve native sample rate)
    os.makedirs(os.path.dirname(args.output) or ".", exist_ok=True)
    out_buf = io.BytesIO()
    with wave.open(out_buf, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes((master * 32767).astype(np.int16).tobytes())
    with open(args.output, "wb") as f:
        f.write(out_buf.getvalue())

    print(f"\nDone: {args.output}  {sr}Hz  {len(master)/sr:.1f}s  {len(out_buf.getvalue())} bytes")


if __name__ == "__main__":
    main()
