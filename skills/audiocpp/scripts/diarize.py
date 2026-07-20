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
  2. turns    POST /v1/tasks/run {"model": ..., "request": {"audio": <abs path>}}
              -> speaker_turns [{start_sample, end_sample, speaker_id, confidence}]
  3. merge    sort, drop micro-turns, merge same-speaker turns across small gaps
  4. label    (unless --turns-only) slice each turn with the wave module and
              transcribe it via the ASR engine's path-based /v1/audio/transcriptions
  5. write    <out-base>.diarization.json + <out-base>.transcript.txt in Output/
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

DIAR_ENGINE_DEFAULT = "http://127.0.0.1:18099"
ASR_ENGINE_DEFAULT = "http://127.0.0.1:18082"
ASR_ENGINE_MODEL_ID = "qwen3-asr"
TARGET_SAMPLE_RATE = 16000
STATE_DIR = os.path.join(os.getcwd(), ".audiocpp-extended")
BUDGET_SECONDS = 240  # leave headroom under the ~5 min sandbox script budget
SCRIPT_START = time.monotonic()


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


def request_turns(engine_url: str, model: str, audio_path: str) -> list[dict]:
    payload = {"model": model, "request": {"audio": os.path.abspath(audio_path)}}
    try:
        body = post_json(f"{engine_url}/v1/tasks/run", payload, timeout=max(30.0, budget_left()))
    except urllib.error.HTTPError as exc:
        fail(f"/v1/tasks/run failed with HTTP {exc.code}: {http_error_detail(exc)}")
    turns = body.get("speaker_turns")
    if turns is None:
        fail("Engine response has no speaker_turns — is the loaded model really "
             f"family sortformer_diar with task diar? Response keys: {sorted(body)}")
    return turns


def merge_turns(raw: list[dict], min_seconds: float, merge_gap: float) -> list[dict]:
    turns = sorted(
        (
            {
                "start": turn["start_sample"] / TARGET_SAMPLE_RATE,
                "end": turn["end_sample"] / TARGET_SAMPLE_RATE,
                "speaker": str(turn.get("speaker_id", "?")),
                "confidence": turn.get("confidence"),
            }
            for turn in raw
        ),
        key=lambda t: (t["start"], t["end"]),
    )
    merged: list[dict] = []
    for turn in turns:
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


def transcribe_turns(turns: list[dict], prepped: str, args: argparse.Namespace) -> dict:
    """Label turns in place; returns a status dict for the report."""
    asr_url = args.asr_engine_url.rstrip("/")
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
                payload = {"model": args.asr_model, "audio": os.path.abspath(segment_path)}
                if args.language:
                    payload["language"] = args.language
                try:
                    body = post_json(f"{asr_url}/v1/audio/transcriptions", payload,
                                     timeout=min(60.0, max(10.0, budget_left())))
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
                        help="Output path base (default Output/<input stem>); "
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
    args = parser.parse_args()

    if not os.path.isfile(args.audio_file):
        fail(f"audio file not found: {args.audio_file}")
    stem = os.path.splitext(os.path.basename(args.audio_file))[0]
    out_base = args.output_base or os.path.join("Output", stem)
    os.makedirs(STATE_DIR, exist_ok=True)

    engine_url = args.engine_url.rstrip("/")
    model = resolve_diar_model(engine_url, args.model)
    prepped, created_prep = prep_audio(args.audio_file, out_base)
    try:
        raw_turns = request_turns(engine_url, model, prepped)
        turns = merge_turns(raw_turns, args.min_turn_seconds, args.merge_gap_seconds)
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
        "speakers": speakers,
        "rawTurnCount": len(raw_turns),
        "labeling": labeling,
        "turns": turns,
    }
    outputs = write_outputs(out_base, report, turns)
    print(json.dumps({"outputs": outputs, "speakers": speakers, "turnCount": len(turns),
                      "labeling": labeling}, indent=2))


if __name__ == "__main__":
    main()
