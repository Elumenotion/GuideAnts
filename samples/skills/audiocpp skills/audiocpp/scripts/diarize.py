#!/usr/bin/env python3
"""Speaker diarization ("who spoke when") via a sortformer_diar engine.

Drives a private audiocpp_server that has the sortformer_diar model loaded
(spawn it first with `spawn_engine.py start --family sortformer_diar --task diar`),
then optionally labels each speaker turn with text from the wrapper-spawned ASR
engine. Stdlib-only; needs the container's ffmpeg only when the input is not
already 16 kHz mono PCM16 WAV.

Pipeline:
  1. prep     input -> 16 kHz mono PCM16 WAV (the raw engine does NOT resample;
              it errors on a sample-rate mismatch)
  2. turns    overlapping Sortformer windows (session_len_sec must cover window)
              + remap SPEAKER_* IDs via overlap agreement; single pass when short
  3. merge    sort, drop micro-turns, merge same-speaker turns across small gaps
  4. label    (unless --turns-only) slice each turn with the wave module and
              transcribe it via the ASR engine's path-based /v1/audio/transcriptions
  5. write    <out-base>.diarization.json + <out-base>.transcript.txt (CWD-relative)
"""
import argparse
import json
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import wave

from skill_gateway_client import (
    fail_http,
    gateway_engine_prefix,
    gateway_request,
    stage_file,
    using_skill_gateway,
)

DIAR_ENGINE_DEFAULT = "http://127.0.0.1:18099"
ASR_ENGINE_DEFAULT = "http://127.0.0.1:18082"
ASR_ENGINE_MODEL_ID = "qwen3-asr"
TARGET_SAMPLE_RATE = 16000
STATE_DIR = os.path.join(os.getcwd(), ".audiocpp-extended")
BUDGET_SECONDS = 240  # leave headroom under the ~5 min sandbox script budget
SCRIPT_START = time.monotonic()

# SortformerOffline (nvidia/diar_sortformer_4spk-v1) in audio.cpp:
#   default graph is session_len_sec=20; spawn with --option session_len_sec=N
#   up to DIAR_MAX_SESSION_S (~120 s; tf max_source_positions=1500).
# Long audio: overlapping windows + label stitch across the overlap (not hard cuts).
# Hard cuts destroy cross-speaker contrast; overlap stitching remaps SPEAKER_* IDs.
DIAR_DEFAULT_SESSION_S = 20.0
DIAR_MAX_SESSION_S = 120.0
DIAR_DEFAULT_WINDOW_S = 100.0
DIAR_DEFAULT_OVERLAP_S = 30.0
DIAR_FRAME_S = 0.1


def budget_left() -> float:
    return BUDGET_SECONDS - (time.monotonic() - SCRIPT_START)


def post_json(url: str, payload: dict, timeout: float):
    request = urllib.request.Request(
        url, data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"}, method="POST",
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.loads(response.read().decode("utf-8", errors="replace"))


def get_json(url: str, timeout: float = 10):
    with urllib.request.urlopen(url, timeout=timeout) as response:
        return json.loads(response.read().decode("utf-8", errors="replace"))


def fail(message: str) -> None:
    sys.stderr.write(message.rstrip() + "\n")
    sys.exit(1)


def http_error_detail(exc: urllib.error.HTTPError) -> str:
    return exc.read().decode("utf-8", errors="replace")[:500]


def wav_is_target_format(path: str) -> bool:
    """True when the file is already PCM16 mono at TARGET_SAMPLE_RATE."""
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
                    return audio_format == 1 and channels == 1 and rate == TARGET_SAMPLE_RATE and bits == 16
                handle.seek(chunk_size + (chunk_size & 1), os.SEEK_CUR)
    except OSError:
        return False


def prep_audio(input_path: str, out_base: str) -> tuple[str, bool]:
    """Return (path to 16 kHz mono PCM16 WAV, whether we created it)."""
    if wav_is_target_format(input_path):
        return input_path, False
    ffmpeg = shutil.which("ffmpeg")
    if not ffmpeg:
        fail(
            f"{input_path} is not 16 kHz mono PCM16 WAV and ffmpeg is not on PATH — "
            "the raw engine does not resample, so the input must be converted first."
        )
    prepped = out_base + ".16k.wav"
    os.makedirs(os.path.dirname(prepped) or ".", exist_ok=True)
    result = subprocess.run(
        [ffmpeg, "-nostdin", "-loglevel", "error", "-y", "-i", input_path,
         "-ac", "1", "-ar", str(TARGET_SAMPLE_RATE), "-c:a", "pcm_s16le", "-f", "wav", prepped],
        capture_output=True, text=True, timeout=120,
    )
    if result.returncode != 0 or not os.path.isfile(prepped):
        fail(f"ffmpeg conversion failed: {result.stderr.strip()[:500]}")
    return prepped, True


def resolve_diar_model(engine_url: str, explicit: str | None) -> str:
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
        body = get_json(f"{engine_url}/v1/models")
    except Exception as exc:
        fail(f"Could not list models on {engine_url} (is the diar engine running? "
             f"`spawn_engine.py status`): {exc}")
    entries = body.get("data") or body.get("models") or []
    ids = [entry.get("id") for entry in entries if isinstance(entry, dict) and entry.get("id")]
    if len(ids) != 1:
        fail(f"Engine at {engine_url} serves {len(ids)} models ({ids}); pass --model explicitly.")
    return ids[0]


def wav_duration_seconds(path: str) -> float:
    with wave.open(path, "rb") as handle:
        rate = handle.getframerate()
        if rate <= 0:
            fail(f"invalid sample rate in {path}")
        return handle.getnframes() / float(rate)


def capacity_error_hint(detail: str) -> str | None:
    lowered = detail.lower()
    if "exceeds prepared session context" in lowered or "graph capacity" in lowered:
        return (
            "Sortformer fixed graph is shorter than this audio window. Re-spawn the private "
            f"engine with --option session_len_sec=<window> "
            f"(default graph {DIAR_DEFAULT_SESSION_S:.0f}s, hard max {DIAR_MAX_SESSION_S:.0f}s). "
            f"For long files use overlapping windows (see --window-seconds), not hard cuts."
        )
    if "max_source_positions" in lowered or "max_position_embeddings" in lowered:
        return (
            f"session_len_sec exceeds the model architecture ceiling "
            f"({DIAR_MAX_SESSION_S:.0f}s). Lower --window-seconds."
        )
    return None


def window_plan(duration_s: float, window_s: float, overlap_s: float) -> list[tuple[float, float]]:
    """Return [start, end) windows covering duration_s with the given overlap."""
    if duration_s <= 0:
        fail("audio duration must be > 0")
    if window_s <= 0 or window_s > DIAR_MAX_SESSION_S:
        fail(f"--window-seconds must be in (0, {DIAR_MAX_SESSION_S:.0f}]")
    if overlap_s < 0 or overlap_s >= window_s:
        fail("--overlap-seconds must be in [0, window)")
    if duration_s <= window_s:
        return [(0.0, duration_s)]
    hop = window_s - overlap_s
    if hop <= 0:
        fail("window - overlap must be > 0")
    windows: list[tuple[float, float]] = []
    start = 0.0
    while start < duration_s:
        end = min(duration_s, start + window_s)
        windows.append((start, end))
        if end >= duration_s:
            break
        start += hop
        # Absorb a tiny final stub into the previous window when it would be
        # shorter than the overlap (no useful exclusive region to stitch).
        if duration_s - start < overlap_s and windows:
            windows[-1] = (windows[-1][0], duration_s)
            break
    return windows


def normalize_raw_turns(raw: list[dict], time_offset: float = 0.0) -> list[dict]:
    return sorted(
        (
            {
                "start": turn["start_sample"] / TARGET_SAMPLE_RATE + time_offset,
                "end": turn["end_sample"] / TARGET_SAMPLE_RATE + time_offset,
                "speaker": str(turn.get("speaker_id", "?")),
                "confidence": turn.get("confidence"),
            }
            for turn in raw
        ),
        key=lambda t: (t["start"], t["end"]),
    )


def turns_to_frames(turns: list[dict], n_frames: int, frame_s: float = DIAR_FRAME_S) -> list[str | None]:
    frames: list[str | None] = [None] * n_frames
    for turn in turns:
        a = max(0, int(turn["start"] / frame_s))
        b = min(n_frames, int((turn["end"] + frame_s - 1e-9) / frame_s) + 1)
        for index in range(a, b):
            frames[index] = turn["speaker"]
    return frames


def map_local_speakers_via_overlap(
    global_frames: list[str | None],
    local_frames: list[str | None],
    overlap_lo: int,
    overlap_hi: int,
    next_speaker_index: int,
) -> tuple[dict[str, str], int]:
    """Greedy bipartite match of local→global labels by frame agreement in overlap."""
    if overlap_hi <= overlap_lo:
        mapping: dict[str, str] = {}
        for label in {frame for frame in local_frames if frame}:
            mapping[label] = f"SPEAKER_{next_speaker_index:02d}"
            next_speaker_index += 1
        return mapping, next_speaker_index

    local_ids = sorted({frame for frame in local_frames[overlap_lo:overlap_hi] if frame})
    global_ids = sorted({frame for frame in global_frames[overlap_lo:overlap_hi] if frame})
    scores: list[tuple[int, str, str]] = []
    for local_id in local_ids:
        for global_id in global_ids:
            score = sum(
                1
                for index in range(overlap_lo, overlap_hi)
                if local_frames[index] == local_id and global_frames[index] == global_id
            )
            if score > 0:
                scores.append((score, local_id, global_id))
    scores.sort(key=lambda item: (-item[0], item[1], item[2]))

    mapping = {}
    used_local: set[str] = set()
    used_global: set[str] = set()
    for score, local_id, global_id in scores:
        if local_id in used_local or global_id in used_global:
            continue
        mapping[local_id] = global_id
        used_local.add(local_id)
        used_global.add(global_id)

    for local_id in local_ids:
        if local_id in mapping:
            continue
        mapping[local_id] = f"SPEAKER_{next_speaker_index:02d}"
        next_speaker_index += 1
    # Locals that never appear in the overlap still need IDs.
    for label in {frame for frame in local_frames if frame}:
        if label not in mapping:
            mapping[label] = f"SPEAKER_{next_speaker_index:02d}"
            next_speaker_index += 1
    return mapping, next_speaker_index


def frames_to_turns(frames: list[str | None], frame_s: float = DIAR_FRAME_S) -> list[dict]:
    turns: list[dict] = []
    index = 0
    while index < len(frames):
        speaker = frames[index]
        if speaker is None:
            index += 1
            continue
        start = index
        while index < len(frames) and frames[index] == speaker:
            index += 1
        turns.append(
            {
                "start": start * frame_s,
                "end": index * frame_s,
                "speaker": speaker,
                "confidence": None,
            }
        )
    return turns


def write_wav_slice(source_path: str, start: float, end: float, dest_path: str) -> None:
    with wave.open(source_path, "rb") as source:
        slice_wav(source, start, end, dest_path)


def request_turns(engine_url: str, model: str, audio_path: str) -> list[dict]:
    try:
        if using_skill_gateway():
            staged = stage_file(audio_path, timeout=max(30.0, budget_left()))
            payload = {"model": model, "request": {"audio": staged}}
            prefix = gateway_engine_prefix(engine_url)
            raw = gateway_request(
                f"{prefix}/v1/tasks/run",
                payload=payload,
                timeout=max(30.0, budget_left()),
            )
            body = json.loads(raw.decode("utf-8", errors="replace"))
        else:
            payload = {"model": model, "request": {"audio": os.path.abspath(audio_path)}}
            body = post_json(f"{engine_url}/v1/tasks/run", payload, timeout=max(30.0, budget_left()))
    except urllib.error.HTTPError as exc:
        detail = http_error_detail(exc)
        hint = capacity_error_hint(detail)
        if hint:
            fail(f"/v1/tasks/run failed with HTTP {exc.code}: {hint}\nEngine detail: {detail[:300]}")
        if using_skill_gateway():
            fail_http(exc, "/v1/tasks/run")
            return []
        fail(f"/v1/tasks/run failed with HTTP {exc.code}: {detail}")
    turns = body.get("speaker_turns")
    if turns is None:
        fail("Engine response has no speaker_turns - is the loaded model really "
             f"family sortformer_diar with task diar? Response keys: {sorted(body)}")
    return turns


def diarize_with_overlap(
    engine_url: str,
    model: str,
    wav_path: str,
    duration_s: float,
    window_s: float,
    overlap_s: float,
) -> tuple[list[dict], dict]:
    """Run Sortformer on overlapping windows and stitch speaker IDs via overlap agreement."""
    windows = window_plan(duration_s, window_s, overlap_s)
    n_frames = max(1, int((duration_s + DIAR_FRAME_S - 1e-9) / DIAR_FRAME_S))
    global_frames: list[str | None] = [None] * n_frames
    next_speaker_index = 0
    window_meta: list[dict] = []

    tmp_dir = tempfile.mkdtemp(prefix="diarize-win-", dir=STATE_DIR)
    try:
        for index, (start, end) in enumerate(windows):
            if budget_left() < 20:
                fail(
                    f"script budget exhausted after {index}/{len(windows)} diar windows; "
                    "re-run or raise sandbox budget"
                )
            slice_path = os.path.join(tmp_dir, f"window-{index:03d}.wav")
            write_wav_slice(wav_path, start, end, slice_path)
            raw = request_turns(engine_url, model, slice_path)
            local_turns = normalize_raw_turns(raw, time_offset=start)
            local_frames = turns_to_frames(local_turns, n_frames)

            if index == 0:
                mapping = {}
                for label in sorted({turn["speaker"] for turn in local_turns}):
                    mapping[label] = f"SPEAKER_{next_speaker_index:02d}"
                    next_speaker_index += 1
                write_lo = 0
            else:
                overlap_lo = int(start / DIAR_FRAME_S)
                overlap_hi = int(min(duration_s, start + overlap_s) / DIAR_FRAME_S)
                mapping, next_speaker_index = map_local_speakers_via_overlap(
                    global_frames, local_frames, overlap_lo, overlap_hi, next_speaker_index
                )
                # Keep prior labels in the overlap; write only the exclusive suffix.
                write_lo = int((start + overlap_s) / DIAR_FRAME_S)

            write_hi = int((end + DIAR_FRAME_S - 1e-9) / DIAR_FRAME_S)
            remapped = 0
            for frame_i in range(max(0, write_lo), min(n_frames, write_hi)):
                local_label = local_frames[frame_i]
                if local_label is None:
                    continue
                global_frames[frame_i] = mapping.get(local_label, local_label)
                remapped += 1

            window_meta.append(
                {
                    "index": index,
                    "start": start,
                    "end": end,
                    "rawTurns": len(raw),
                    "mapping": mapping,
                    "wroteFrames": remapped,
                }
            )
    finally:
        shutil.rmtree(tmp_dir, ignore_errors=True)

    stitched = frames_to_turns(global_frames)
    meta = {
        "mode": "single" if len(windows) == 1 else "overlap_stitch",
        "windowSeconds": window_s,
        "overlapSeconds": overlap_s,
        "windows": window_meta,
    }
    return stitched, meta


def merge_turns(turns: list[dict], min_seconds: float, merge_gap: float) -> list[dict]:
    """Merge adjacent same-speaker turns; accepts already-normalized second-based turns."""
    ordered = sorted((dict(turn) for turn in turns), key=lambda t: (t["start"], t["end"]))
    merged: list[dict] = []
    for turn in ordered:
        previous = merged[-1] if merged else None
        if previous and previous["speaker"] == turn["speaker"] and turn["start"] - previous["end"] <= merge_gap:
            previous["end"] = max(previous["end"], turn["end"])
            if turn.get("confidence") is not None and previous.get("confidence") is not None:
                previous["confidence"] = min(previous["confidence"], turn["confidence"])
        else:
            merged.append(dict(turn))
    return [turn for turn in merged if turn["end"] - turn["start"] >= min_seconds]


def slice_wav(source: wave.Wave_read, start: float, end: float, dest_path: str) -> None:
    rate = source.getframerate()
    start_frame = max(0, int(start * rate))
    end_frame = min(source.getnframes(), int(end * rate))
    source.setpos(start_frame)
    frames = source.readframes(max(0, end_frame - start_frame))
    with wave.open(dest_path, "wb") as out:
        out.setnchannels(source.getnchannels())
        out.setsampwidth(source.getsampwidth())
        out.setframerate(rate)
        out.writeframes(frames)



def gateway_product_engine_ready(health_body: dict, kind: str) -> bool:
    """True when GPU host product ASR/TTS is available. Busy/listening != down."""
    engines = health_body.get("engines") or {}
    wrappers = health_body.get("wrappers") or {}
    upstream = health_body.get("upstream") or {}
    eng = engines.get(kind) or upstream.get(f"{kind}Engine") or {}
    state = str(eng.get("state") or "").strip().lower()
    if state in {"listening", "up", "busy"} or eng.get("listening") is True:
        wrap = wrappers.get(kind) or {}
        body = wrap.get("body") if isinstance(wrap.get("body"), dict) else {}
        if body:
            return bool(body.get("loaded") or body.get("busy") or body.get("loading"))
        return True
    if eng.get("status") == 200:
        return True
    wrap = wrappers.get(kind) or {}
    body = wrap.get("body") if isinstance(wrap.get("body"), dict) else {}
    return bool(body.get("loaded") or body.get("busy") or body.get("loading"))


def transcribe_turns(turns: list[dict], prepped: str, args: argparse.Namespace) -> dict:
    """Label turns in place; returns a status dict for the report."""
    asr_url = args.asr_engine_url.rstrip("/")
    if using_skill_gateway():
        try:
            health = json.loads(gateway_request("/health", timeout=10).decode("utf-8"))
            asr_ok = gateway_product_engine_ready(health, "asr")
            if not asr_ok:
                # Deep probe: busy engines may look down on legacy checks.
                ready = json.loads(gateway_request("/ready", timeout=5).decode("utf-8"))
                probes = ready.get("probes") or {}
                for key in ("engineAsr", "wrapperAsr"):
                    state = str((probes.get(key) or {}).get("state") or "").lower()
                    if state in {"up", "busy"}:
                        asr_ok = True
                        break
        except Exception as exc:
            return {"labeled": False, "reason": f"skill gateway unreachable ({exc})"}
        if not asr_ok:
            return {"labeled": False, "reason": "ASR engine not reachable via skill gateway; "
                                                "load an ASR model via GuideAnts Settings for labeled transcripts"}
    else:
        try:
            get_json(f"{asr_url}/health")
        except Exception as exc:
            return {"labeled": False, "reason": f"ASR engine unreachable at {asr_url} ({exc}); "
                                                "load an ASR model via GuideAnts Settings for labeled transcripts"}
    tmp_dir = tempfile.mkdtemp(prefix="diarize-", dir=STATE_DIR)
    labeled = 0
    partial = False
    try:
        with wave.open(prepped, "rb") as source:
            duration = source.getnframes() / source.getframerate()
            for index, turn in enumerate(turns):
                if budget_left() < 15:
                    partial = True
                    break
                segment_path = os.path.join(tmp_dir, f"turn-{index:04d}.wav")
                slice_wav(source, max(0.0, turn["start"] - args.pad_seconds),
                          min(duration, turn["end"] + args.pad_seconds), segment_path)
                try:
                    payload = {"model": args.asr_model}
                    if args.language:
                        payload["language"] = args.language
                    if using_skill_gateway():
                        payload["audio"] = stage_file(
                            segment_path,
                            timeout=min(60.0, max(10.0, budget_left())),
                        )
                        raw = gateway_request(
                            f"{gateway_engine_prefix(asr_url)}/v1/audio/transcriptions",
                            payload=payload,
                            timeout=min(60.0, max(10.0, budget_left())),
                        )
                        body = json.loads(raw.decode("utf-8", errors="replace"))
                    else:
                        payload["audio"] = os.path.abspath(segment_path)
                        body = post_json(
                            f"{asr_url}/v1/audio/transcriptions",
                            payload,
                            timeout=min(60.0, max(10.0, budget_left())),
                        )
                    turn["text"] = (body.get("text") or "").strip()
                    labeled += 1
                except urllib.error.HTTPError as exc:
                    turn["textError"] = f"HTTP {exc.code}: {http_error_detail(exc)[:200]}"
                except Exception as exc:
                    turn["textError"] = f"{type(exc).__name__}: {exc}"
    finally:
        shutil.rmtree(tmp_dir, ignore_errors=True)
    return {"labeled": labeled > 0, "labeledTurns": labeled, "totalTurns": len(turns),
            **({"partial": True, "reason": "sandbox script budget nearly spent; "
                                           "unlabeled turns have no text field"} if partial else {})}


def format_timestamp(seconds: float) -> str:
    hours, remainder = divmod(seconds, 3600)
    minutes, secs = divmod(remainder, 60)
    if hours >= 1:
        return f"{int(hours):02d}:{int(minutes):02d}:{secs:04.1f}"
    return f"{int(minutes):02d}:{secs:04.1f}"


def write_outputs(out_base: str, report: dict, turns: list[dict]) -> dict:
    json_path = out_base + ".diarization.json"
    text_path = out_base + ".transcript.txt"
    os.makedirs(os.path.dirname(json_path) or ".", exist_ok=True)
    with open(json_path, "w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=2)
        handle.write("\n")
    with open(text_path, "w", encoding="utf-8") as handle:
        for turn in turns:
            window = f"{format_timestamp(turn['start'])}-{format_timestamp(turn['end'])}"
            line = f"[{turn['speaker']} {window}]"
            if turn.get("text"):
                line += f" {turn['text']}"
            elif turn.get("textError"):
                line += f" <transcription failed: {turn['textError']}>"
            handle.write(line + "\n")
    return {"json": json_path, "transcript": text_path}


def main() -> None:
    parser = argparse.ArgumentParser(description="Diarize an audio file via a sortformer_diar engine")
    parser.add_argument("audio_file", help="Input audio (any ffmpeg-decodable format)")
    parser.add_argument("-o", "--output-base", default=None,
                        help="Output path base (default <input stem>, CWD-relative); "
                             "writes <base>.diarization.json and <base>.transcript.txt")
    parser.add_argument("--engine-url", default=DIAR_ENGINE_DEFAULT, help="Engine serving the diar model")
    parser.add_argument("--model", default=None, help="Diar engine model id (auto-detected when the engine serves exactly one)")
    parser.add_argument("--turns-only", action="store_true", help="Skip per-turn ASR labeling")
    parser.add_argument("--asr-engine-url", default=ASR_ENGINE_DEFAULT)
    parser.add_argument("--asr-model", default=ASR_ENGINE_MODEL_ID)
    parser.add_argument("--language", default=None, help="Language hint passed to the ASR engine")
    parser.add_argument("--min-turn-seconds", type=float, default=0.3, help="Drop turns shorter than this")
    parser.add_argument("--merge-gap-seconds", type=float, default=0.6,
                        help="Merge same-speaker turns separated by at most this gap")
    parser.add_argument("--pad-seconds", type=float, default=0.15, help="Padding around each turn before ASR")
    parser.add_argument("--keep-prep", action="store_true", help="Keep the intermediate 16 kHz WAV")
    parser.add_argument(
        "--window-seconds",
        type=float,
        default=DIAR_DEFAULT_WINDOW_S,
        help=f"Sortformer window length (must fit spawned session_len_sec; max {DIAR_MAX_SESSION_S:.0f})",
    )
    parser.add_argument(
        "--overlap-seconds",
        type=float,
        default=DIAR_DEFAULT_OVERLAP_S,
        help="Overlap between windows for speaker-ID stitching (ignored when audio fits one window)",
    )
    args = parser.parse_args()

    if not os.path.isfile(args.audio_file):
        fail(f"audio file not found: {args.audio_file}")
    if args.window_seconds <= 0 or args.window_seconds > DIAR_MAX_SESSION_S:
        fail(f"--window-seconds must be in (0, {DIAR_MAX_SESSION_S:.0f}]")
    if args.overlap_seconds < 0 or args.overlap_seconds >= args.window_seconds:
        fail("--overlap-seconds must be in [0, window)")
    stem = os.path.splitext(os.path.basename(args.audio_file))[0]
    out_base = args.output_base or stem
    os.makedirs(STATE_DIR, exist_ok=True)

    engine_url = args.engine_url.rstrip("/")
    model = resolve_diar_model(engine_url, args.model)
    prepped, created_prep = prep_audio(args.audio_file, out_base)
    windowing: dict = {}
    try:
        duration_s = wav_duration_seconds(prepped)
        stitched, windowing = diarize_with_overlap(
            engine_url,
            model,
            prepped,
            duration_s,
            args.window_seconds,
            args.overlap_seconds,
        )
        turns = merge_turns(stitched, args.min_turn_seconds, args.merge_gap_seconds)
        labeling = {"labeled": False, "reason": "skipped (--turns-only)"}
        if turns and not args.turns_only:
            labeling = transcribe_turns(turns, prepped, args)
    finally:
        if created_prep and not args.keep_prep:
            try:
                os.unlink(prepped)
            except OSError:
                pass

    speakers = sorted({turn["speaker"] for turn in turns})
    report = {
        "audio": os.path.abspath(args.audio_file),
        "model": model,
        "engineUrl": engine_url,
        "sampleRate": TARGET_SAMPLE_RATE,
        "durationSeconds": duration_s,
        "windowing": windowing,
        "speakers": speakers,
        "rawTurnCount": sum(w.get("rawTurns", 0) for w in windowing.get("windows") or []),
        "labeling": labeling,
        "turns": turns,
    }
    outputs = write_outputs(out_base, report, turns)
    print(json.dumps({
        "outputs": outputs,
        "speakers": speakers,
        "turnCount": len(turns),
        "durationSeconds": duration_s,
        "windowing": {"mode": windowing.get("mode"), "windows": len(windowing.get("windows") or [])},
        "labeling": labeling,
    }, indent=2))


if __name__ == "__main__":
    main()
