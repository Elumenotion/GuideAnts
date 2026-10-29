#!/usr/bin/env python3
"""Per-paragraph TTS with designed line-break pauses.

The approved long-form narration approach (the single-take + waveform
tightening experiment was rejected). Rules, locked:
  - split the script into pieces on line breaks (blank lines; markdown
    headers / rules / END markers are ignored, matching narration.py),
  - synthesize each piece with ONE engine call (no clamping, no beat logic),
  - join the RAW pieces exactly as the engine emitted them (no trimming,
    no cutting; each piece's natural leading/trailing silence is kept),
  - per piece, before the join: every interior silent pause >= --clamp-min-ms
    is rescaled to --clamp-ratio of its OWN original length (default 0.80);
    edge (lead/tail) silence is never touched,
  - insert a designed pause per boundary:
        pause_ms = (base_ms + uniform(pad_min_ms, pad_max_ms)) * linefeeds
    where linefeeds is the number of consecutive blank lines at that
    boundary (1 for a normal paragraph break; a double break doubles it).

Pieces are cached under --state-dir keyed by text+voice hash, so an
interrupted run resumes without re-synthesizing finished pieces.

Usage:
  python3 paragraph_tts.py script.md -o out.wav --voice doug \
      [--base-ms 250] [--pad-min-ms 100] [--pad-max-ms 200] [--seed N]

Stdout: one JSON report (also written to <output>.para-report.json).
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import random
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from narration import ENGINE_TTS_DEFAULT, _parse_request_options, _resolve_tts_model, _synth_chunk
from narration_core import read_wav_pcm16, write_wav_pcm16
from preflight import run_scenario

MARKDOWN_HEADER_RE = re.compile(r"^#{1,6}\s+")
HORIZONTAL_RULE_RE = re.compile(r"^-{3,}\s*$")
END_MARKER_RE = re.compile(r"^END OF SCRIPT\b", re.IGNORECASE)

GAP_REPORT_MS = 300  # intra-piece hiccups reported at/above this
CLAMP_MIN_MS = 300.0    # absolute floor: no pause shorter than this is ever
                        # rescaled (below it is natural rhythm, not a hiccup)
CLAMP_RATIO = 0.80      # ...down to this fraction of their original length
FADE_MS = 5.0           # raised-cosine fade at each cut edge (avoid clicks)


def parse_pieces(raw: str) -> list[dict]:
    """Spoken pieces; breaks_before = consecutive blank lines before the piece."""
    pieces: list[dict] = []
    blank_run = 0
    for line in raw.splitlines():
        stripped = line.strip()
        if not stripped:
            if pieces:
                blank_run += 1
            continue
        if MARKDOWN_HEADER_RE.match(stripped):
            continue
        if HORIZONTAL_RULE_RE.match(stripped):
            continue
        if END_MARKER_RE.match(stripped):
            continue
        if not pieces or blank_run > 0:
            pieces.append({"text": stripped, "breaks_before": blank_run})
            blank_run = 0
        else:
            # paragraph wrapped across multiple lines (no blank line): same piece
            pieces[-1]["text"] += " " + stripped
    return pieces


def scan_gaps(samples: list[int], sample_rate: int, *, frame_ms: float = 10.0,
              min_ms: float = GAP_REPORT_MS) -> list[tuple[float, float]]:
    """Silent spans via adaptive RMS (report helper only)."""
    frame = max(1, int(sample_rate * frame_ms / 1000))
    n_frames = len(samples) // frame
    if n_frames == 0:
        return []
    rms = []
    for i in range(n_frames):
        seg = samples[i * frame:(i + 1) * frame]
        acc = 0.0
        for value in seg:
            acc += value * value
        rms.append(math.sqrt(acc / len(seg)))
    voiced = sorted(r for r in rms if r > 10.0)
    if not voiced:
        return []
    threshold = max(50.0, 0.35 * voiced[len(voiced) // 2])
    gaps: list[tuple[float, float]] = []
    i = 0
    while i < n_frames:
        if rms[i] < threshold:
            j = i
            while j < n_frames and rms[j] < threshold:
                j += 1
            if (j - i) * frame_ms >= min_ms:
                gaps.append((round(i * frame_ms / 1000.0, 2), round(j * frame_ms / 1000.0, 2)))
            i = j
        else:
            i += 1
    return gaps


def clamp_gaps(
    samples: list[int],
    sample_rate: int,
    *,
    min_ms: float = CLAMP_MIN_MS,
    ratio: float = CLAMP_RATIO,
    fade_ms: float = FADE_MS,
    frame_ms: float = 10.0,
    quiet_peak: float | None = None,
) -> tuple[list[int], list[dict], list[dict]]:
    """Shorten interior silent pauses to ``ratio`` of their original length.

    SPEECH PROTECTION: a cut may only remove samples from frames whose peak
    amplitude is below the quiet threshold (``quiet_peak``, adaptive). The
    detector's gap span may include the decaying tail of a final word
    (soft speech under the gap threshold); the cut is therefore restricted
    to the quiet core of the span. If the quiet core is shorter than
    ``min_ms`` the gap is not a hiccup and is left alone. This makes it
    impossible to clip a syllable: removed samples are always below the
    quiet peak.
    """
    frame = max(1, int(sample_rate * frame_ms / 1000))
    n_frames = len(samples) // frame
    if n_frames == 0:
        return samples, [], []
    rms = []
    peaks = []
    for i in range(n_frames):
        seg = samples[i * frame:(i + 1) * frame]
        rms.append(math.sqrt(sum(v * v for v in seg) / len(seg)))
        peaks.append(max(abs(v) for v in seg))
    voiced = sorted(r for r in rms if r > 10.0)
    if not voiced:
        return samples, [], []
    threshold = max(50.0, 0.35 * voiced[len(voiced) // 2])
    # quiet = frames that are genuinely silent. Well below soft-speech tails
    # (measured: tails reach 40-100 peak, true silence < 40 on this engine).
    noise_floor = sorted(peaks)[: max(1, len(peaks) // 10)]
    floor = sum(noise_floor) / len(noise_floor)
    if quiet_peak is None:
        quiet_peak = max(60.0, floor * 8.0)

    total_s = len(samples) / sample_rate
    # candidate spans (detector-level silence)
    candidates: list[tuple[int, int]] = []
    i = 0
    while i < n_frames:
        if rms[i] < threshold:
            j = i
            while j < n_frames and rms[j] < threshold:
                j += 1
            if (j - i) * frame_ms >= min_ms:
                candidates.append((i, j))
            i = j
        else:
            i += 1

    out = list(samples)
    report: list[dict] = []
    edge: list[dict] = []
    fade = max(1, int(sample_rate * fade_ms / 1000))
    for fi, fj in sorted(candidates, key=lambda x: x[0], reverse=True):
        s, e = fi * frame, fj * frame
        start_s, end_s = s / sample_rate, e / sample_rate
        if start_s < 0.02 or (total_s - end_s) < 0.02:
            edge.append({"start_s": round(start_s, 3), "end_s": round(end_s, 3),
                         "ms": round((end_s - start_s) * 1000.0, 0)})
            continue
        # quiet core within the span: largest contiguous run of quiet frames
        best = None
        run_start = None
        for k in range(fi, fj):
            if peaks[k] < quiet_peak:
                if run_start is None:
                    run_start = k
            else:
                if run_start is not None:
                    run = (run_start * frame, k * frame)
                    if best is None or (run[1] - run[0]) > (best[1] - best[0]):
                        best = run
                    run_start = None
        if run_start is not None:
            run = (run_start * frame, fj * frame)
            if best is None or (run[1] - run[0]) > (best[1] - best[0]):
                best = run
        if best is None:
            report.append({"start_s": round(start_s, 3), "end_s": round(end_s, 3),
                           "original_ms": round((end_s - start_s) * 1000.0, 0),
                           "clamped_ms": round((end_s - start_s) * 1000.0, 0),
                           "samples_removed": 0, "skipped": "no quiet core (speech)"})
            continue
        cs, ce = best
        silent_len = ce - cs
        if silent_len / sample_rate * 1000.0 < min_ms:
            report.append({"start_s": round(start_s, 3), "end_s": round(end_s, 3),
                           "original_ms": round((end_s - start_s) * 1000.0, 0),
                           "clamped_ms": round((end_s - start_s) * 1000.0, 0),
                           "samples_removed": 0, "skipped": "quiet core < min_ms"})
            continue
        target = int(ratio * silent_len)
        remove = silent_len - target
        cut_start = cs + (silent_len - target) // 2
        cut_end = cut_start + remove
        for k in range(min(fade, cut_start - cs)):
            t = (k + 1) / (min(fade, cut_start - cs) + 1)
            out[cut_start - 1 - k] = int(round(out[cut_start - 1 - k] * math.cos(t * math.pi / 2.0)))
        for k in range(min(fade, ce - cut_end)):
            t = (k + 1) / (min(fade, ce - cut_end) + 1)
            out[cut_end + k] = int(round(out[cut_end + k] * math.sin(t * math.pi / 2.0)))
        del out[cut_start:cut_end]
        report.append({
            "start_s": round(start_s, 3),
            "end_s": round(end_s, 3),
            "original_ms": round((end_s - start_s) * 1000.0, 0),
            "clamped_ms": round(target / sample_rate * 1000.0, 0),
            "samples_removed": remove,
            "removed_start_s": round(cut_start / sample_rate, 4),
            "removed_end_s": round(cut_end / sample_rate, 4),
            "removed_peak": max(abs(v) for v in samples[cut_start:cut_end]),
        })
    return out, report, edge


def edge_silence_ms(samples: list[int], sample_rate: int) -> tuple[int, int]:
    """Informational estimate of the engine's natural lead/tail silence.

    Never used for cutting; the pieces are joined raw.
    """
    f = int(sample_rate * 0.010)
    n = len(samples) // f
    if n == 0:
        return 0, 0
    rms = []
    for i in range(n):
        seg = samples[i * f:(i + 1) * f]
        rms.append(math.sqrt(sum(v * v for v in seg) / len(seg)))
    voiced = sorted(r for r in rms if r > 20.0)
    if not voiced:
        return 0, 0
    threshold = max(70.0, 0.35 * voiced[len(voiced) // 2])
    lead = 0
    for i in range(n):
        if rms[i] >= threshold:
            lead = i * 10
            break
    tail = 0
    for i in range(n - 1, -1, -1):
        if rms[i] >= threshold:
            tail = (n - 1 - i) * 10
            break
    return lead, tail


def main() -> None:
    parser = argparse.ArgumentParser(description="Per-paragraph TTS with designed line-break pauses.")
    parser.add_argument("script", help="Narration script file (markdown ok)")
    parser.add_argument("-o", "--output", required=True, help="Final WAV output path")
    parser.add_argument("--voice", required=True, help="Voice-pack voice id")
    parser.add_argument("--engine-url", default=ENGINE_TTS_DEFAULT)
    parser.add_argument("--model", default=None, help="TTS model id (auto-detected if omitted)")
    parser.add_argument("--base-ms", type=float, default=250.0, help="Base pause per linefeed (default 250)")
    parser.add_argument("--pad-min-ms", type=float, default=100.0, help="Random pad low (default 100)")
    parser.add_argument("--pad-max-ms", type=float, default=200.0, help="Random pad high (default 200)")
    parser.add_argument("--seed", type=int, default=None, help="Fix synthesis + pad RNG for reproducibility")
    parser.add_argument("--request-option", action="append", default=[], metavar="KEY=VALUE", help="Repeatable model request option, merged into the body options object")
    parser.add_argument("--state-dir", default=".audiocpp-para", help="Piece cache dir (resume support)")
    parser.add_argument("--clamp-min-ms", type=float, default=CLAMP_MIN_MS,
                        help="Only rescale intra-piece pauses above this (default 300)")
    parser.add_argument("--clamp-ratio", type=float, default=CLAMP_RATIO,
                        help="Rescale those to this fraction of original (default 0.80)")
    parser.add_argument("--no-clamp", action="store_true",
                        help="Skip intra-piece pause clamping entirely")
    args = parser.parse_args()

    def fail(message: str, **extra) -> None:
        print(json.dumps({"ok": False, "error": message, **extra}))
        sys.exit(1)

    verdict = run_scenario("tts-controls")
    if not verdict.get("open"):
        fail("TTS preflight blocked", preflight=verdict)

    with open(args.script, "r", encoding="utf-8") as handle:
        raw = handle.read()
    pieces = parse_pieces(raw)
    if not pieces:
        fail("no spoken pieces in script", script_path=args.script)

    model = _resolve_tts_model(args.engine_url, args.model)
    options = _parse_request_options(args.request_option)
    rng = random.Random(args.seed)
    os.makedirs(args.state_dir, exist_ok=True)

    sample_rate: int | None = None
    merged: list[int] = []
    report: dict = {
        "ok": True,
        "output": args.output,
        "script": os.path.abspath(args.script),
        "voice": args.voice,
        "model": model,
        "base_ms": args.base_ms,
        "pad_min_ms": args.pad_min_ms,
        "pad_max_ms": args.pad_max_ms,
        "clamp_min_ms": None if args.no_clamp else args.clamp_min_ms,
        "clamp_ratio": None if args.no_clamp else args.clamp_ratio,
        "seed": args.seed,
        "pieces": [],
        "boundaries": [],
    }

    for index, piece in enumerate(pieces):
        digest = hashlib.sha256(
            (piece["text"] + "|" + args.voice + "|" + json.dumps(options, sort_keys=True)).encode("utf-8")
        ).hexdigest()[:16]
        wav_path = os.path.join(args.state_dir, f"piece_{index:02d}_{digest}.wav")
        cached = os.path.isfile(wav_path) and os.path.getsize(wav_path) > 1000
        synthesis_seconds = None
        if not cached:
            started = time.monotonic()
            wav_bytes = _synth_chunk(
                model=model,
                engine_url=args.engine_url,
                voice=args.voice,
                text=piece["text"],
                seed=args.seed,
                timeout=3600.0,
                options=options,
            )
            synthesis_seconds = round(time.monotonic() - started, 2)
            if not wav_bytes or wav_bytes[:4] != b"RIFF":
                fail(f"engine returned non-WAV payload for piece {index}", piece=index)
            with open(wav_path, "wb") as handle:
                handle.write(wav_bytes)

        samples, sr = read_wav_pcm16(wav_path)
        if sample_rate is None:
            sample_rate = sr
        elif sr != sample_rate:
            fail(f"sample rate mismatch on piece {index}: {sr} vs {sample_rate}")

        # NO trimming: the engine's natural edge silence stays. The designed
        # pause is inserted between pieces, on top of it.
        trimmed = samples
        clamped = []
        edge_gaps = []
        if not args.no_clamp:
            # Interior pauses only, per piece, before the join: any silent
            # gap >= --clamp-min-ms is rescaled to --clamp-ratio of its own
            # original length. Edge (lead/tail) silence is never touched.
            trimmed, clamped, edge_gaps = clamp_gaps(
                trimmed, sr, min_ms=args.clamp_min_ms, ratio=args.clamp_ratio
            )

        if index > 0:
            linefeeds = max(1, piece["breaks_before"])
            pad = rng.uniform(args.pad_min_ms, args.pad_max_ms)
            pause_ms = (args.base_ms + pad) * linefeeds
            merged.extend([0] * int(sample_rate * pause_ms / 1000.0))
            report["boundaries"].append(
                {
                    "after_piece": index - 1,
                    "linefeeds": linefeeds,
                    "pad_ms": round(pad, 1),
                    "designed_pause_ms": round(pause_ms, 1),
                }
            )
        merged.extend(trimmed)

        intra = scan_gaps(trimmed, sr)
        lead_ms, tail_ms = edge_silence_ms(trimmed, sr)
        report["pieces"].append(
            {
                "index": index,
                "linefeeds_before": piece["breaks_before"],
                "text": piece["text"],
                "cached": cached,
                "synthesis_seconds": synthesis_seconds,
                "duration_seconds": round(len(trimmed) / sr, 3),
                "raw_lead_silence_ms": lead_ms,
                "raw_tail_silence_ms": tail_ms,
                "edge_gaps_untouched": edge_gaps,
                "clamped_gaps": clamped,
                "intra_piece_gaps_ms": [
                    {"start_s": a, "end_s": b, "ms": round((b - a) * 1000)} for a, b in intra
                ],
            }
        )
        print(
            f"piece {index:02d}/{len(pieces) - 1} done "
            f"({len(trimmed) / sr:.2f}s, cached={cached})",
            file=sys.stderr,
        )

    assert sample_rate is not None
    write_wav_pcm16(args.output, merged, sample_rate)
    report["duration_seconds"] = round(len(merged) / sample_rate, 3)
    report_path = os.path.splitext(args.output)[0] + ".para-report.json"
    with open(report_path, "w", encoding="utf-8") as handle:
        json.dump(report, handle, indent=2)
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
