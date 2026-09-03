#!/usr/bin/env python3
"""Long-form single-speaker narration for audiocpp-tts-controls.

The skill user provides a script file and voice; this tool owns preflight,
sentence-aware chunking, per-chunk synthesis, silence healing, and concat.

Workflow (poll until done — each call stays within the sandbox script budget):
  python3 narration.py start script.txt -o narration.wav --voice doug
  python3 narration.py step narration.wav
  python3 narration.py status narration.wav

Preflight runs inside ``start``. Synthesis HTTP timeouts are hours-scale; the
producer only polls ``step`` / ``status`` and never waits on one long call.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import time
import urllib.error
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from narration_core import (
    DEFAULT_MAX_CHUNK_SECONDS,
    DEFAULT_WORDS_PER_SECOND,
    concat_wavs,
    count_words,
    estimate_audio_seconds,
    needs_chunking,
    plan_chunks,
    strip_script_markdown,
)
from preflight import run_scenario
from skill_gateway_client import (
    fail_http,
    gateway_engine_prefix,
    gateway_request,
    using_skill_gateway,
)

ENGINE_TTS_DEFAULT = "http://127.0.0.1:18084"
STATE_DIR = ".audiocpp-narration"
SYNTHESIS_TIMEOUT_SECONDS = float(os.environ.get("AUDIOCPP_TTS_SYNTHESIS_TIMEOUT_SECONDS", str(4 * 3600)))


def _utc_now() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat()


def _job_path(output: str) -> Path:
    stem = Path(output).name
    return Path(STATE_DIR) / f"{stem}.job.json"


def _chunk_wav_path(output: str, index: int) -> str:
    stem = Path(output).stem
    return str(Path(STATE_DIR) / f"{stem}.chunk_{index:02d}.wav")


def _emit(payload: dict) -> None:
    print(json.dumps(payload, indent=2))


def _fail(message: str, **extra) -> None:
    payload = {"ok": False, "error": message, **extra}
    _emit(payload)
    sys.exit(1)


def _resolve_tts_model(engine_url: str, explicit: str | None) -> str:
    if explicit:
        return explicit
    if using_skill_gateway():
        try:
            body = json.loads(gateway_request("/health", timeout=15).decode("utf-8"))
        except Exception as exc:
            _fail(f"Could not auto-detect model from skill gateway /health: {exc}")
        wrappers = body.get("wrappers") or {}
        wrapper = (wrappers.get("tts") or {}).get("body") or {}
        if not wrapper:
            wrapper = ((body.get("upstream") or {}).get("wrapperTts") or {}).get("body") or {}
        model = wrapper.get("catalogEntryId") if isinstance(wrapper, dict) else None
        if not model:
            _fail("Skill gateway TTS wrapper has no catalogEntryId (is a model loaded?)", evidence=body)
        return model

    import urllib.request

    try:
        with urllib.request.urlopen("http://127.0.0.1:8084/health", timeout=10) as response:
            body = json.loads(response.read().decode("utf-8"))
    except Exception as exc:
        _fail(f"Could not auto-detect model from TTS wrapper health: {exc}")
    model = body.get("catalogEntryId")
    if not model:
        _fail("TTS wrapper health has no catalogEntryId", evidence=body)
    return model


def _synth_chunk(
    *,
    model: str,
    engine_url: str,
    voice: str,
    text: str,
    seed: int | None,
    timeout: float,
) -> bytes:
    payload: dict = {"model": model, "input": text, "voice": voice}
    if seed is not None:
        payload["seed"] = seed
    try:
        if using_skill_gateway():
            prefix = gateway_engine_prefix(engine_url)
            return gateway_request(
                f"{prefix}/v1/audio/speech",
                payload=payload,
                timeout=timeout,
            )
        import urllib.request

        data = json.dumps(payload).encode("utf-8")
        request = urllib.request.Request(
            f"{engine_url.rstrip('/')}/v1/audio/speech",
            data=data,
            method="POST",
            headers={"Content-Type": "application/json"},
        )
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.read()
    except urllib.error.HTTPError as exc:
        fail_http(exc, "/v1/audio/speech")
        raise


def _load_job(output: str) -> dict:
    path = _job_path(output)
    if not path.is_file():
        _fail(f"No narration job for output {output!r}. Run `start` first.", job_path=str(path))
    with path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


def _save_job(output: str, job: dict) -> None:
    job["updated_at"] = _utc_now()
    path = _job_path(output)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as handle:
        json.dump(job, handle, indent=2)


def _preflight_or_fail() -> dict:
    verdict = run_scenario("tts-controls")
    if not verdict.get("open"):
        _fail(
            "TTS preflight blocked",
            preflight=verdict,
            poll_hint="Fix blockers on Max (load TTS model, check gateway) then re-run `start`.",
        )
    return verdict


def _status_payload(job: dict) -> dict:
    chunks = job.get("chunks") or []
    done = sum(1 for chunk in chunks if chunk.get("status") == "done")
    failed = [chunk for chunk in chunks if chunk.get("status") == "failed"]
    pending = [chunk for chunk in chunks if chunk.get("status") == "pending"]
    return {
        "ok": True,
        "status": job.get("status"),
        "output": job.get("output"),
        "voice": job.get("voice"),
        "script_path": job.get("script_path"),
        "words_total": job.get("words_total"),
        "estimated_seconds_total": job.get("estimated_seconds_total"),
        "chunking_required": job.get("chunking_required"),
        "chunks_total": len(chunks),
        "chunks_done": done,
        "chunks_pending": len(pending),
        "chunks_failed": len(failed),
        "heuristics": job.get("heuristics"),
        "next_action": job.get("next_action"),
        "error": job.get("error"),
        "result": job.get("result"),
    }


def cmd_start(args: argparse.Namespace) -> None:
    if not os.path.isfile(args.script):
        _fail(f"Script file not found: {args.script}")

    preflight = _preflight_or_fail()

    with open(args.script, "r", encoding="utf-8") as handle:
        text = strip_script_markdown(handle.read())
    if not text:
        _fail("Script file is empty after markdown stripping", script_path=args.script)

    words_total = count_words(text)
    estimated_total = estimate_audio_seconds(words_total, args.words_per_second)
    chunking_required = needs_chunking(
        text,
        max_chunk_seconds=args.max_chunk_seconds,
        words_per_second=args.words_per_second,
    )
    plans = plan_chunks(
        text,
        max_chunk_seconds=args.max_chunk_seconds,
        words_per_second=args.words_per_second,
    )
    if not plans:
        _fail("Chunk planner produced no segments", script_path=args.script)

    model = _resolve_tts_model(args.engine_url, args.model)
    chunks = []
    for plan in plans:
        chunks.append(
            {
                "index": plan.index,
                "words": plan.words,
                "estimated_seconds": round(plan.estimated_seconds, 2),
                "text_preview": plan.text[:120] + ("..." if len(plan.text) > 120 else ""),
                "wav": _chunk_wav_path(args.output, plan.index),
                "status": "pending",
            }
        )

    job = {
        "status": "pending",
        "created_at": _utc_now(),
        "updated_at": _utc_now(),
        "script_path": os.path.abspath(args.script),
        "output": os.path.abspath(args.output),
        "voice": args.voice,
        "seed": args.seed,
        "model": model,
        "engine_url": args.engine_url,
        "words_total": words_total,
        "estimated_seconds_total": round(estimated_total, 2),
        "chunking_required": chunking_required,
        "chunk_texts": [plan.text for plan in plans],
        "chunks": chunks,
        "heuristics": {
            "words_per_second": args.words_per_second,
            "max_chunk_seconds": args.max_chunk_seconds,
            "synthesis_timeout_seconds": args.synthesis_timeout,
            "note": (
                "Device-tuned for Max chatterbox + voice-pack clone (~186 wpm). "
                "Chunk when estimated segment audio exceeds max_chunk_seconds."
            ),
        },
        "preflight": {
            "open": preflight.get("open"),
            "warnings": preflight.get("warnings"),
            "route": preflight.get("route"),
        },
        "next_action": f"python3 narration.py step {args.output}",
        "error": None,
        "result": None,
    }

    Path(STATE_DIR).mkdir(parents=True, exist_ok=True)
    chunk_text_dir = Path(STATE_DIR) / Path(args.output).stem
    chunk_text_dir.mkdir(parents=True, exist_ok=True)
    for plan in plans:
        chunk_text_path = chunk_text_dir / f"chunk_{plan.index:02d}.txt"
        chunk_text_path.write_text(plan.text, encoding="utf-8")

    _save_job(args.output, job)
    payload = _status_payload(job)
    payload["message"] = (
        "Preflight passed; narration job created. Poll with `step` until status is `done`."
    )
    payload["poll"] = {
        "step": f"python3 narration.py step {args.output}",
        "status": f"python3 narration.py status {args.output}",
    }
    _emit(payload)


def _finalize(job: dict, output: str) -> dict:
    chunk_paths = [chunk["wav"] for chunk in job["chunks"]]
    missing = [path for path in chunk_paths if not os.path.isfile(path)]
    if missing:
        job["status"] = "failed"
        job["error"] = f"Missing chunk wav(s): {missing}"
        job["next_action"] = f"python3 narration.py step {output}"
        _save_job(output, job)
        return _status_payload(job)

    result = concat_wavs(chunk_paths, output, trim_boundaries=True)
    job["status"] = "done"
    job["error"] = None
    job["result"] = result
    job["next_action"] = None
    _save_job(output, job)
    payload = _status_payload(job)
    payload["message"] = "Narration complete."
    return payload


def cmd_step(args: argparse.Namespace) -> None:
    job = _load_job(args.output)
    if job.get("status") == "done":
        payload = _status_payload(job)
        payload["message"] = "Already done."
        _emit(payload)
        return
    if job.get("status") == "failed" and not args.retry:
        payload = _status_payload(job)
        payload["message"] = "Job failed. Fix the error or re-run `start`."
        _emit(payload)
        sys.exit(1)

    pending = [chunk for chunk in job["chunks"] if chunk.get("status") == "pending"]
    if not pending:
        payload = _finalize(job, args.output)
        _emit(payload)
        return

    chunk = pending[0]
    index = chunk["index"]
    text = job["chunk_texts"][index]
    started = time.monotonic()

    try:
        wav_bytes = _synth_chunk(
            model=job["model"],
            engine_url=job["engine_url"],
            voice=job["voice"],
            text=text,
            seed=job.get("seed"),
            timeout=args.synthesis_timeout,
        )
    except Exception as exc:
        chunk["status"] = "failed"
        chunk["error"] = f"{type(exc).__name__}: {exc}"
        job["status"] = "failed"
        job["error"] = chunk["error"]
        job["next_action"] = f"python3 narration.py step {args.output} --retry"
        _save_job(args.output, job)
        payload = _status_payload(job)
        payload["message"] = "Chunk synthesis failed."
        _emit(payload)
        sys.exit(1)

    if not wav_bytes or wav_bytes[:4] != b"RIFF":
        chunk["status"] = "failed"
        chunk["error"] = f"Engine returned non-WAV payload ({len(wav_bytes)} bytes)"
        job["status"] = "failed"
        job["error"] = chunk["error"]
        _save_job(args.output, job)
        _fail(chunk["error"])

    os.makedirs(os.path.dirname(chunk["wav"]) or ".", exist_ok=True)
    with open(chunk["wav"], "wb") as handle:
        handle.write(wav_bytes)

    elapsed = time.monotonic() - started
    chunk["status"] = "done"
    chunk["bytes"] = len(wav_bytes)
    chunk["synthesis_seconds"] = round(elapsed, 2)
    chunk.pop("error", None)

    remaining = [item for item in job["chunks"] if item.get("status") == "pending"]
    if remaining:
        job["status"] = "running"
        job["error"] = None
        job["next_action"] = f"python3 narration.py step {args.output}"
        _save_job(args.output, job)
        payload = _status_payload(job)
        payload["message"] = (
            f"Synthesized chunk {index + 1}/{len(job['chunks'])} in {elapsed:.1f}s. Poll again."
        )
        payload["poll"] = {
            "step": f"python3 narration.py step {args.output}",
            "status": f"python3 narration.py status {args.output}",
        }
        _emit(payload)
        return

    payload = _finalize(job, args.output)
    _emit(payload)


def cmd_status(args: argparse.Namespace) -> None:
    job = _load_job(args.output)
    payload = _status_payload(job)
    if job.get("status") == "done":
        payload["message"] = "Done."
    elif job.get("status") == "failed":
        payload["message"] = "Failed."
    else:
        payload["message"] = "In progress — call `step` to continue."
        payload["poll"] = {
            "step": f"python3 narration.py step {args.output}",
            "status": f"python3 narration.py status {args.output}",
        }
    _emit(payload)


def main() -> None:
    parser = argparse.ArgumentParser(description="Long-form single-speaker narration (poll-based)")
    sub = parser.add_subparsers(dest="command", required=True)

    def add_shared_flags(target: argparse.ArgumentParser) -> None:
        target.add_argument(
            "--engine-url",
            default=ENGINE_TTS_DEFAULT,
            help="TTS engine URL (local-style; mapped through gateway when remote)",
        )
        target.add_argument("--model", default=None, help="TTS model id (auto-detected if omitted)")
        target.add_argument(
            "--words-per-second",
            type=float,
            default=DEFAULT_WORDS_PER_SECOND,
            help="Device words/sec heuristic for chunk sizing (~3.1 on Max chatterbox)",
        )
        target.add_argument(
            "--max-chunk-seconds",
            type=float,
            default=DEFAULT_MAX_CHUNK_SECONDS,
            help="Max estimated audio seconds per synthesis call (~95s from Max tests)",
        )
        target.add_argument(
            "--synthesis-timeout",
            type=float,
            default=SYNTHESIS_TIMEOUT_SECONDS,
            help="HTTP timeout per chunk synthesis call (default: 4 hours)",
        )

    p_start = sub.add_parser("start", help="Preflight, plan chunks, create job (returns immediately)")
    p_start.add_argument("script", help="Narration script file (markdown ok)")
    p_start.add_argument("-o", "--output", required=True, help="Final WAV output path")
    p_start.add_argument("--voice", required=True, help="Voice-pack voice id (e.g. doug)")
    p_start.add_argument("--seed", type=int, default=None)
    add_shared_flags(p_start)

    p_step = sub.add_parser("step", help="Synthesize the next pending chunk (one per call)")
    p_step.add_argument("output", help="Final WAV output path (job key)")
    p_step.add_argument("--retry", action="store_true", help="Retry after a failed chunk")
    add_shared_flags(p_step)

    p_status = sub.add_parser("status", help="Read job progress without synthesizing")
    p_status.add_argument("output", help="Final WAV output path (job key)")

    args = parser.parse_args()
    {
        "start": cmd_start,
        "step": cmd_step,
        "status": cmd_status,
    }[args.command](args)


if __name__ == "__main__":
    main()
