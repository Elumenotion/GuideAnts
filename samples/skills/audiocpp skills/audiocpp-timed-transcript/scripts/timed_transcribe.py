#!/usr/bin/env python3
"""Time-coded transcript via GPU host product ASR + private qwen3_forced_aligner.

Pure AudioCPP: text from gateway /asr, word samples from /private/v1/tasks/run
(align). Builds industry-standard SRT + WebVTT + segment JSON. Stdlib-only.

Pipeline:
  1. prep     input -> 16 kHz mono PCM16 WAV
  2. chunk    <= ALIGN_MAX_CHUNK_S (default 30) on 80 ms grid
  3. per chunk: ASR text -> align words -> offset by chunk start
  4. cues     group words into ~3-8 s captions on punctuation/pauses
  5. write    <out-base>.srt / .vtt / .json / .transcript.txt
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import wave
from pathlib import Path

from skill_gateway_client import (
    fail_http,
    gateway_engine_prefix,
    gateway_request,
    normalize_sandbox_relative_path,
    stage_file,
    using_skill_gateway,
)

ASR_ENGINE_DEFAULT = "http://127.0.0.1:18082"
ALIGN_ENGINE_DEFAULT = "http://127.0.0.1:18099"
ASR_ENGINE_MODEL_ID = "qwen3-asr"
TARGET_SAMPLE_RATE = 16000
FRAME_S = 0.08
ALIGN_MAX_CHUNK_S = 30.0
BUDGET_SECONDS = 240
SCRIPT_START = time.monotonic()
PUNCT_END = re.compile(r"[.!?…]$")


def budget_left(budget_seconds: float | None = None) -> float:
    limit = BUDGET_SECONDS if budget_seconds is None else budget_seconds
    return limit - (time.monotonic() - SCRIPT_START)


def fail(message: str) -> None:
    sys.stderr.write(message.rstrip() + "\n")
    sys.exit(1)


def post_json(url: str, payload: dict, timeout: float):
    request = urllib.request.Request(
        url,
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.loads(response.read().decode("utf-8", errors="replace"))


def wav_is_target_format(path: str) -> bool:
    try:
        with open(path, "rb") as handle:
            header = handle.read(12)
            if len(header) < 12 or header[:4] != b"RIFF" or header[8:12] != b"WAVE":
                return False
            while True:
                chunk = handle.read(8)
                if len(chunk) < 8:
                    return False
                chunk_id, chunk_size = chunk[:4], struct.unpack("<I", chunk[4:])[0]
                if chunk_id == b"fmt ":
                    fmt = handle.read(min(chunk_size, 16))
                    if len(fmt) < 16:
                        return False
                    audio_format, channels, rate, _, _, bits = struct.unpack("<HHIIHH", fmt)
                    return (
                        audio_format == 1
                        and channels == 1
                        and rate == TARGET_SAMPLE_RATE
                        and bits == 16
                    )
                handle.seek(chunk_size + (chunk_size & 1), os.SEEK_CUR)
    except OSError:
        return False


def prep_audio(input_path: str, work_dir: str) -> str:
    if wav_is_target_format(input_path):
        return input_path
    ffmpeg = shutil.which("ffmpeg")
    if not ffmpeg:
        fail(
            f"{input_path} is not 16 kHz mono PCM16 WAV and ffmpeg is not on PATH — "
            "the raw aligner does not resample."
        )
    prepped = os.path.join(work_dir, "input.16k.wav")
    result = subprocess.run(
        [
            ffmpeg,
            "-nostdin",
            "-loglevel",
            "error",
            "-y",
            "-i",
            input_path,
            "-ac",
            "1",
            "-ar",
            str(TARGET_SAMPLE_RATE),
            "-c:a",
            "pcm_s16le",
            "-f",
            "wav",
            prepped,
        ],
        capture_output=True,
        text=True,
        timeout=120,
    )
    if result.returncode != 0 or not os.path.isfile(prepped):
        fail(f"ffmpeg conversion failed: {result.stderr.strip()[:500]}")
    return prepped


def read_pcm16_mono(path: str) -> tuple[bytes, int]:
    with wave.open(path, "rb") as handle:
        if handle.getnchannels() != 1 or handle.getsampwidth() != 2:
            fail(f"{path} must be mono PCM16 after prep")
        if handle.getframerate() != TARGET_SAMPLE_RATE:
            fail(f"{path} sample rate {handle.getframerate()} != {TARGET_SAMPLE_RATE}")
        return handle.readframes(handle.getnframes()), handle.getframerate()


def chunk_wav(path: str, dest_dir: str, max_s: float):
    """Yield (chunk_path, offset_seconds) on the 80 ms frame grid.

    The final chunk may be off-grid so a sub-frame tail is never its own pass
    (avoids tiny leftover clips that ASR invents words for).
    """
    raw, sr = read_pcm16_mono(path)
    total = len(raw) // 2
    frame = int(sr * FRAME_S)
    max_samples = int(max_s * sr)
    i, idx = 0, 0
    while i < total:
        j = min(i + max_samples, total)
        if j < total:
            snapped = j - (j % frame)
            if snapped <= i:
                j = min(i + max_samples, total)
            else:
                j = snapped
            # Fold a sub-frame remainder into this chunk instead of another pass.
            if 0 < total - j < frame:
                j = total
        seg = raw[i * 2 : j * 2]
        cp = os.path.join(dest_dir, f"chunk_{idx:03d}.wav")
        with wave.open(cp, "wb") as handle:
            handle.setnchannels(1)
            handle.setsampwidth(2)
            handle.setframerate(sr)
            handle.writeframes(seg)
        yield cp, i / sr
        i, idx = j, idx + 1
        if j >= total:
            break


def resolve_align_model(engine_url: str, explicit: str | None) -> str:
    if explicit:
        return explicit
    if using_skill_gateway():
        try:
            body = json.loads(gateway_request("/admin/private/status", timeout=15).decode("utf-8"))
        except Exception as exc:
            fail(f"Could not read private engine status from skill gateway: {exc}")
        meta = body.get("meta") or {}
        model_id = meta.get("modelId")
        if not model_id:
            fail("Private engine status has no modelId; pass --model explicitly.")
        return model_id
    try:
        with urllib.request.urlopen(f"{engine_url.rstrip('/')}/v1/models", timeout=10) as response:
            body = json.loads(response.read().decode("utf-8", errors="replace"))
    except Exception as exc:
        fail(f"Could not list models on {engine_url}: {exc}")
    entries = body.get("data") or body.get("models") or []
    ids = [entry.get("id") for entry in entries if isinstance(entry, dict) and entry.get("id")]
    if len(ids) != 1:
        fail(f"Engine at {engine_url} serves {len(ids)} models ({ids}); pass --model explicitly.")
    return ids[0]


def asr_transcribe(
    engine_url: str,
    audio_path: str,
    language: str | None,
    budget: float | None = None,
) -> str:
    payload: dict = {"model": ASR_ENGINE_MODEL_ID, "audio": audio_path}
    if language:
        payload["language"] = language
    timeout = max(30.0, budget_left(budget))
    try:
        if using_skill_gateway():
            prefix = gateway_engine_prefix(engine_url)
            raw = gateway_request(
                f"{prefix}/v1/audio/transcriptions",
                payload=payload,
                timeout=timeout,
            )
            body = json.loads(raw.decode("utf-8", errors="replace"))
        else:
            body = post_json(
                f"{engine_url.rstrip('/')}/v1/audio/transcriptions",
                payload,
                timeout=timeout,
            )
    except urllib.error.HTTPError as exc:
        if using_skill_gateway():
            fail_http(exc, "/v1/audio/transcriptions")
            return ""
        fail(f"ASR failed HTTP {exc.code}: {exc.read().decode('utf-8', errors='replace')[:400]}")
    return (body.get("text") or "").strip()


def align_words(
    engine_url: str,
    model: str,
    audio_path: str,
    text: str,
    language: str,
    budget: float | None = None,
) -> list[dict]:
    payload = {
        "model": model,
        "request": {"audio": audio_path, "text": text, "language": language},
    }
    timeout = max(30.0, budget_left(budget))
    try:
        if using_skill_gateway():
            prefix = gateway_engine_prefix(engine_url)
            raw = gateway_request(
                f"{prefix}/v1/tasks/run",
                payload=payload,
                timeout=timeout,
            )
            body = json.loads(raw.decode("utf-8", errors="replace"))
        else:
            body = post_json(
                f"{engine_url.rstrip('/')}/v1/tasks/run",
                payload,
                timeout=timeout,
            )
    except urllib.error.HTTPError as exc:
        if using_skill_gateway():
            fail_http(exc, "/v1/tasks/run")
            return []
        fail(f"align /v1/tasks/run failed HTTP {exc.code}: "
             f"{exc.read().decode('utf-8', errors='replace')[:400]}")
    words = body.get("words")
    if words is None:
        fail(
            "Align response has no words — is the private engine family "
            f"qwen3_forced_aligner with task align? keys={sorted(body)}"
        )
    return words


def words_to_seconds(raw_words: list[dict], offset_s: float) -> list[dict]:
    out: list[dict] = []
    for entry in raw_words:
        start = entry.get("start_sample", 0) / TARGET_SAMPLE_RATE + offset_s
        end = entry.get("end_sample", 0) / TARGET_SAMPLE_RATE + offset_s
        word = str(entry.get("word") or "").strip()
        if not word:
            continue
        out.append(
            {
                "word": word,
                "start": round(start, 3),
                "end": round(end, 3),
                "confidence": entry.get("confidence"),
            }
        )
    return out


def build_cues(
    words: list[dict],
    *,
    min_cue_s: float = 3.0,
    max_cue_s: float = 8.0,
) -> list[dict]:
    """Group words into caption cues on punctuation / pause / max span."""
    if not words:
        return []
    cues: list[dict] = []
    buf: list[dict] = []

    def flush() -> None:
        nonlocal buf
        if not buf:
            return
        text = " ".join(w["word"] for w in buf).strip()
        cues.append(
            {
                "id": len(cues) + 1,
                "start": buf[0]["start"],
                "end": buf[-1]["end"],
                "text": text,
            }
        )
        buf = []

    for word in words:
        if not buf:
            buf = [word]
            continue
        span = word["end"] - buf[0]["start"]
        gap = word["start"] - buf[-1]["end"]
        prev = buf[-1]["word"]
        soft_break = bool(PUNCT_END.search(prev)) and span >= min_cue_s
        hard_break = span >= max_cue_s or gap >= 0.6
        if soft_break or hard_break:
            flush()
            buf = [word]
        else:
            buf.append(word)
    flush()
    return cues


def srt_ts(t: float) -> str:
    if t < 0:
        t = 0.0
    h = int(t // 3600)
    m = int((t % 3600) // 60)
    s = int(t % 60)
    ms = int(round((t - int(t)) * 1000))
    if ms == 1000:
        s += 1
        ms = 0
    return f"{h:02d}:{m:02d}:{s:02d},{ms:03d}"


def vtt_ts(t: float) -> str:
    if t < 0:
        t = 0.0
    h = int(t // 3600)
    m = int((t % 3600) // 60)
    s = t % 60
    return f"{h:02d}:{m:02d}:{s:06.3f}"


def write_srt(path: Path, cues: list[dict]) -> None:
    lines: list[str] = []
    for cue in cues:
        lines.append(str(cue["id"]))
        lines.append(f"{srt_ts(cue['start'])} --> {srt_ts(cue['end'])}")
        lines.append(cue["text"])
        lines.append("")
    path.write_text("\n".join(lines), encoding="utf-8")


def write_vtt(path: Path, cues: list[dict]) -> None:
    lines = ["WEBVTT", ""]
    for cue in cues:
        lines.append(f"{vtt_ts(cue['start'])} --> {vtt_ts(cue['end'])}")
        lines.append(cue["text"])
        lines.append("")
    path.write_text("\n".join(lines), encoding="utf-8")


def write_outputs(
    out_base: Path,
    *,
    language: str,
    words: list[dict],
    cues: list[dict],
    chunks: int,
    wall_s: float,
) -> None:
    out_base.parent.mkdir(parents=True, exist_ok=True)
    duration = words[-1]["end"] if words else 0.0
    payload = {
        "language": language,
        "duration": duration,
        "word_count": len(words),
        "chunks": chunks,
        "timing": {"wall_s": round(wall_s, 2)},
        "segments": cues,
        "words": words,
    }
    out_base.with_suffix(".json").write_text(json.dumps(payload, indent=2), encoding="utf-8")
    write_srt(out_base.with_suffix(".srt"), cues)
    write_vtt(out_base.with_suffix(".vtt"), cues)
    with out_base.with_suffix(".transcript.txt").open("w", encoding="utf-8") as handle:
        for word in words:
            handle.write(f"[{word['start']:8.3f} -> {word['end']:8.3f}] {word['word']}\n")


def main() -> None:
    parser = argparse.ArgumentParser(description="Time-coded SRT/VTT via AudioCPP aligner")
    parser.add_argument("audio", help="Input audio file")
    parser.add_argument("-o", "--out-base", required=True, help="Output path prefix without suffix")
    parser.add_argument("--language", default="English", help="ASR/align language hint")
    parser.add_argument("--asr-engine-url", default=ASR_ENGINE_DEFAULT)
    parser.add_argument("--align-engine-url", default=ALIGN_ENGINE_DEFAULT)
    parser.add_argument("--model", default=None, help="Private aligner model id")
    parser.add_argument("--max-chunk-s", type=float, default=ALIGN_MAX_CHUNK_S)
    parser.add_argument("--min-cue-s", type=float, default=3.0)
    parser.add_argument("--max-cue-s", type=float, default=8.0)
    parser.add_argument(
        "--budget-seconds",
        type=float,
        default=BUDGET_SECONDS,
        help="Wall-clock budget for the whole run (raise for long-form)",
    )
    args = parser.parse_args()

    if not os.path.isfile(args.audio):
        fail(f"audio not found: {args.audio}")
    if args.max_chunk_s <= 0 or args.max_chunk_s > 60:
        fail("--max-chunk-s must be in (0, 60] (standalone aligner max_source_positions)")
    if args.budget_seconds < 60:
        fail("--budget-seconds must be >= 60")

    started = time.perf_counter()
    align_model = resolve_align_model(args.align_engine_url, args.model)
    words: list[dict] = []
    full_text_parts: list[str] = []
    n_chunks = 0
    budget = float(args.budget_seconds)

    with tempfile.TemporaryDirectory(prefix="timed-transcribe-") as work:
        wav = prep_audio(args.audio, work)
        for chunk_path, offset in chunk_wav(wav, work, args.max_chunk_s):
            if budget_left(budget) < 20:
                fail(f"script budget exhausted after {n_chunks} chunks; re-run with --budget-seconds")
            n_chunks += 1
            if using_skill_gateway():
                try:
                    remote = stage_file(chunk_path, timeout=max(30.0, budget_left(budget)))
                except urllib.error.HTTPError as exc:
                    fail_http(exc, "/files")
                    return
            else:
                remote = os.path.abspath(chunk_path)
            text = asr_transcribe(args.asr_engine_url, remote, args.language, budget)
            if not text:
                continue
            full_text_parts.append(text)
            raw = align_words(
                args.align_engine_url,
                align_model,
                remote,
                text,
                args.language or "English",
                budget,
            )
            words.extend(words_to_seconds(raw, offset))

    cues = build_cues(words, min_cue_s=args.min_cue_s, max_cue_s=args.max_cue_s)
    out_base = Path(normalize_sandbox_relative_path(args.out_base))
    wall_s = time.perf_counter() - started
    write_outputs(
        out_base,
        language=args.language or "auto",
        words=words,
        cues=cues,
        chunks=n_chunks,
        wall_s=wall_s,
    )
    print(
        json.dumps(
            {
                "text": " ".join(full_text_parts).strip(),
                "language": args.language or "auto",
                "word_count": len(words),
                "segment_count": len(cues),
                "chunks": n_chunks,
                "timing": {"wall_s": round(wall_s, 2)},
                "outputs": [
                    str(out_base.with_suffix(".srt")),
                    str(out_base.with_suffix(".vtt")),
                    str(out_base.with_suffix(".json")),
                    str(out_base.with_suffix(".transcript.txt")),
                ],
            }
        )
    )


if __name__ == "__main__":
    main()
