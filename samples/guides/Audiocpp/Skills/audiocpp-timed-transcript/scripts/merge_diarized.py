#!/usr/bin/env python3
"""Merge diarization turns with word timestamps into speaker-labeled captions.

Stdlib-only. Inputs:
  --words-json   from timed_transcribe.py (.json with words[{word,start,end}])
  --turns-json   from diarize.py (.diarization.json) or a raw speaker_turns list

Or pass audio + engines and this script will call diar (and optionally reuse
precomputed words). Writes RTTM, speaker text, SRT/VTT, JSON.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

TARGET_SAMPLE_RATE = 16000


def fail(message: str) -> None:
    sys.stderr.write(message.rstrip() + "\n")
    sys.exit(1)


def load_words(path: str) -> list[dict]:
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    words = data.get("words")
    if not isinstance(words, list) or not words:
        fail(f"no words[] in {path}")
    return words


def load_turns(path: str) -> list[dict]:
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    if isinstance(data, list):
        raw = data
    else:
        raw = data.get("turns") or data.get("speaker_turns")
    if not isinstance(raw, list) or not raw:
        fail(f"no turns/speaker_turns in {path}")
    turns: list[dict] = []
    for turn in raw:
        if "start" in turn and "end" in turn:
            start, end = float(turn["start"]), float(turn["end"])
        else:
            start = turn["start_sample"] / TARGET_SAMPLE_RATE
            end = turn["end_sample"] / TARGET_SAMPLE_RATE
        turns.append(
            {
                "start": start,
                "end": end,
                "speaker": str(turn.get("speaker") or turn.get("speaker_id") or "?"),
            }
        )
    return sorted(turns, key=lambda t: (t["start"], t["end"]))


def assign_speaker(mid: float, turns: list[dict], previous: str | None) -> str:
    hits = [t for t in turns if t["start"] <= mid <= t["end"]]
    if len(hits) == 1:
        return hits[0]["speaker"]
    if len(hits) > 1:
        # majority by overlap length with a 1 ms window around midpoint
        best = max(hits, key=lambda t: min(t["end"], mid + 0.001) - max(t["start"], mid - 0.001))
        return best["speaker"]
    # gap: keep previous speaker when available
    if previous is not None:
        return previous
    # otherwise nearest turn by start
    if not turns:
        return "SPEAKER_00"
    nearest = min(turns, key=lambda t: abs(((t["start"] + t["end"]) / 2) - mid))
    return nearest["speaker"]


def merge_words_to_segments(words: list[dict], turns: list[dict]) -> list[dict]:
    labeled: list[dict] = []
    previous = None
    for word in words:
        mid = (word["start"] + word["end"]) / 2
        speaker = assign_speaker(mid, turns, previous)
        previous = speaker
        labeled.append({**word, "speaker": speaker})

    segments: list[dict] = []
    buf: list[dict] = []
    for item in labeled:
        if buf and buf[-1]["speaker"] != item["speaker"]:
            segments.append(
                {
                    "start": buf[0]["start"],
                    "end": buf[-1]["end"],
                    "speaker": buf[0]["speaker"],
                    "text": " ".join(w["word"] for w in buf),
                    "words": list(buf),
                }
            )
            buf = [item]
        else:
            buf.append(item)
    if buf:
        segments.append(
            {
                "start": buf[0]["start"],
                "end": buf[-1]["end"],
                "speaker": buf[0]["speaker"],
                "text": " ".join(w["word"] for w in buf),
                "words": list(buf),
            }
        )
    return segments


def srt_ts(t: float) -> str:
    h = int(t // 3600)
    m = int((t % 3600) // 60)
    s = int(t % 60)
    ms = int(round((t - int(t)) * 1000)) % 1000
    return f"{h:02d}:{m:02d}:{s:02d},{ms:03d}"


def vtt_ts(t: float) -> str:
    h = int(t // 3600)
    m = int((t % 3600) // 60)
    s = t % 60
    return f"{h:02d}:{m:02d}:{s:06.3f}"


def format_timestamp(seconds: float) -> str:
    hours, remainder = divmod(seconds, 3600)
    minutes, secs = divmod(remainder, 60)
    if hours >= 1:
        return f"{int(hours):02d}:{int(minutes):02d}:{secs:04.1f}"
    return f"{int(minutes):02d}:{secs:04.1f}"


def write_rttm(path: Path, file_id: str, segments: list[dict]) -> None:
    lines = []
    for seg in segments:
        dur = max(0.0, seg["end"] - seg["start"])
        lines.append(
            f"SPEAKER {file_id} 1 {seg['start']:.3f} {dur:.3f} "
            f"<NA> <NA> {seg['speaker']} <NA> <NA>"
        )
    path.write_text("\n".join(lines) + ("\n" if lines else ""), encoding="utf-8")


def write_outputs(out_base: Path, segments: list[dict], speakers: list[str]) -> None:
    out_base.parent.mkdir(parents=True, exist_ok=True)
    file_id = out_base.name
    payload = {"speakers": speakers, "segments": segments}
    out_base.with_suffix(".diarized.json").write_text(json.dumps(payload, indent=2), encoding="utf-8")
    write_rttm(out_base.with_suffix(".rttm"), file_id, segments)

    with out_base.with_suffix(".transcript.txt").open("w", encoding="utf-8") as handle:
        for seg in segments:
            handle.write(
                f"[{format_timestamp(seg['start'])} -> {format_timestamp(seg['end'])}] "
                f"{seg['speaker']}: {seg['text']}\n"
            )

    srt_lines: list[str] = []
    for idx, seg in enumerate(segments, start=1):
        srt_lines.append(str(idx))
        srt_lines.append(f"{srt_ts(seg['start'])} --> {srt_ts(seg['end'])}")
        srt_lines.append(f"[{seg['speaker']}] {seg['text']}")
        srt_lines.append("")
    out_base.with_suffix(".srt").write_text("\n".join(srt_lines), encoding="utf-8")

    vtt_lines = ["WEBVTT", ""]
    for seg in segments:
        vtt_lines.append(f"{vtt_ts(seg['start'])} --> {vtt_ts(seg['end'])}")
        vtt_lines.append(f"[{seg['speaker']}] {seg['text']}")
        vtt_lines.append("")
    out_base.with_suffix(".vtt").write_text("\n".join(vtt_lines), encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser(description="Merge word timestamps with diar turns")
    parser.add_argument("--words-json", required=True, help="timed_transcribe .json with words[]")
    parser.add_argument("--turns-json", required=True, help="diarize .diarization.json or turns list")
    parser.add_argument("-o", "--out-base", required=True)
    args = parser.parse_args()

    words = load_words(args.words_json)
    turns = load_turns(args.turns_json)
    segments = merge_words_to_segments(words, turns)
    speakers = sorted({seg["speaker"] for seg in segments})
    out_base = Path(args.out_base)
    write_outputs(out_base, segments, speakers)
    print(
        json.dumps(
            {
                "speakers": speakers,
                "segment_count": len(segments),
                "word_count": len(words),
                "outputs": [
                    str(out_base.with_suffix(".rttm")),
                    str(out_base.with_suffix(".transcript.txt")),
                    str(out_base.with_suffix(".srt")),
                    str(out_base.with_suffix(".vtt")),
                    str(out_base.with_suffix(".diarized.json")),
                ],
            }
        )
    )


if __name__ == "__main__":
    main()
