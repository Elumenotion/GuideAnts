#!/usr/bin/env python3
"""Long-form single-speaker narration for audiocpp-tts-controls.

The skill user provides a script file and voice; this tool owns preflight,
chunk planning, synthesis, silence healing, concat, and a detached worker.

Workflow (poll until done — each call stays within the sandbox script budget):
  python3 narration.py start script.txt -o narration.wav --voice narrator
  python3 narration.py status narration.wav
  python3 narration.py cancel narration.wav   # optional

``start`` spawns a background worker that processes the full script. The
producer only polls ``status`` (or cancels). Chunking is internal.
"""
from __future__ import annotations

import argparse
import json
import os
import signal
import subprocess
import sys
import time
import urllib.error
import urllib.request
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


def _parse_utc(value: str | None) -> datetime | None:
    if not value:
        return None
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None


def _job_path(output: str) -> Path:
    stem = Path(output).name
    return Path(STATE_DIR) / f"{stem}.job.json"


def _pid_path(output: str) -> Path:
    stem = Path(output).name
    return Path(STATE_DIR) / f"{stem}.worker.pid"


def _cancel_path(output: str) -> Path:
    stem = Path(output).name
    return Path(STATE_DIR) / f"{stem}.cancel"


def _worker_log_path(output: str) -> Path:
    stem = Path(output).name
    return Path(STATE_DIR) / f"{stem}.worker.log"


def _chunk_wav_path(output: str, index: int) -> str:
    stem = Path(output).stem
    return str(Path(STATE_DIR) / f"{stem}.chunk_{index:02d}.wav")


def _emit(payload: dict) -> None:
    print(json.dumps(payload, indent=2))


def _fail(message: str, **extra) -> None:
    payload = {"ok": False, "error": message, **extra}
    _emit(payload)
    sys.exit(1)


def _read_pid(output: str) -> int | None:
    path = _pid_path(output)
    if not path.is_file():
        return None
    try:
        return int(path.read_text(encoding="utf-8").strip())
    except (OSError, ValueError):
        return None


def _pid_alive(pid: int) -> bool:
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def _clear_pid(output: str) -> None:
    path = _pid_path(output)
    try:
        path.unlink(missing_ok=True)
    except OSError:
        pass


def _cancel_requested(output: str) -> bool:
    return _cancel_path(output).is_file()


def _worker_alive(output: str) -> bool:
    pid = _read_pid(output)
    return pid is not None and _pid_alive(pid)


def _terminate_worker(output: str) -> bool:
    pid = _read_pid(output)
    if pid is None or not _pid_alive(pid):
        return False
    try:
        os.kill(pid, signal.SIGTERM)
    except OSError:
        return False
    deadline = time.monotonic() + 10.0
    while time.monotonic() < deadline:
        if not _pid_alive(pid):
            return True
        time.sleep(0.2)
    try:
        os.kill(pid, signal.SIGKILL)
    except OSError:
        pass
    return not _pid_alive(pid)


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
            poll_hint="Fix blockers on the GPU host (load TTS model, check gateway) then re-run `start`.",
        )
    return verdict


def _job_progress(job: dict) -> float:
    chunks = job.get("chunks") or []
    if not chunks:
        return 0.0
    if job.get("status") == "done":
        return 1.0
    done = sum(1 for chunk in chunks if chunk.get("status") == "done")
    return round(done / len(chunks), 3)


def _elapsed_seconds(job: dict) -> float | None:
    started = _parse_utc(job.get("worker_started_at") or job.get("created_at"))
    if started is None:
        return None
    return round((datetime.now(timezone.utc) - started).total_seconds(), 1)


def _public_status_payload(job: dict, *, output: str | None = None) -> dict:
    progress = _job_progress(job)
    elapsed = _elapsed_seconds(job)
    estimated = job.get("estimated_seconds_total")
    eta = None
    if (
        estimated is not None
        and isinstance(estimated, (int, float))
        and progress > 0.0
        and progress < 1.0
        and elapsed is not None
    ):
        eta = round((elapsed / progress) * (1.0 - progress), 1)

    payload: dict = {
        "ok": True,
        "status": job.get("status"),
        "output": job.get("output"),
        "voice": job.get("voice"),
        "script_path": job.get("script_path"),
        "words_total": job.get("words_total"),
        "estimated_seconds_total": estimated,
        "progress": progress,
        "error": job.get("error"),
    }
    if elapsed is not None:
        payload["elapsed_seconds"] = elapsed
    if eta is not None:
        payload["eta_seconds"] = eta
    if job.get("status") == "done" and job.get("result"):
        payload["result"] = {
            "output": job["result"].get("output"),
            "duration_seconds": job["result"].get("duration_seconds"),
            "sample_rate": job["result"].get("sample_rate"),
        }
    if output is not None and job.get("status") in {"pending", "running"}:
        payload["worker_alive"] = _worker_alive(output)
    return payload


def _reconcile_worker_state(output: str, job: dict) -> dict:
    status = job.get("status")
    if status in {"done", "failed", "cancelled"}:
        return job
    if status in {"pending", "running"} and not _worker_alive(output):
        if _cancel_requested(output):
            job["status"] = "cancelled"
            job["error"] = job.get("error") or "Cancelled"
        elif status == "running":
            job["status"] = "failed"
            job["error"] = job.get("error") or "Worker process exited unexpectedly"
        elif status == "pending":
            job["status"] = "failed"
            job["error"] = job.get("error") or "Worker never started"
        _save_job(output, job)
    return job


def _spawn_worker(output: str) -> int:
    cancel_path = _cancel_path(output)
    if cancel_path.is_file():
        cancel_path.unlink(missing_ok=True)

    log_path = _worker_log_path(output)
    log_path.parent.mkdir(parents=True, exist_ok=True)
    log_handle = open(log_path, "a", encoding="utf-8")
    script_path = os.path.abspath(__file__)
    proc = subprocess.Popen(
        [sys.executable, script_path, "worker", output],
        cwd=os.getcwd(),
        stdout=log_handle,
        stderr=subprocess.STDOUT,
        start_new_session=True,
        close_fds=True,
    )
    log_handle.close()
    _pid_path(output).write_text(str(proc.pid), encoding="utf-8")
    return proc.pid


def _finalize(job: dict, output: str) -> dict:
    chunk_paths = [chunk["wav"] for chunk in job["chunks"]]
    missing = [path for path in chunk_paths if not os.path.isfile(path)]
    if missing:
        job["status"] = "failed"
        job["error"] = "Missing synthesized audio segments"
        _save_job(output, job)
        return job

    result = concat_wavs(chunk_paths, output, trim_boundaries=True)
    job["status"] = "done"
    job["error"] = None
    job["result"] = result
    _save_job(output, job)
    return job


def _run_worker(output: str, synthesis_timeout: float) -> None:
    job = _load_job(output)
    if job.get("status") in {"done", "cancelled"}:
        return

    heuristics = job.get("heuristics") or {}
    timeout = float(heuristics.get("synthesis_timeout_seconds", synthesis_timeout))

    job["status"] = "running"
    job["worker_started_at"] = job.get("worker_started_at") or _utc_now()
    job["error"] = None
    _save_job(output, job)

    try:
        for chunk in job["chunks"]:
            if _cancel_requested(output):
                job["status"] = "cancelled"
                job["error"] = "Cancelled"
                _save_job(output, job)
                return

            if chunk.get("status") == "done":
                continue
            if chunk.get("status") == "failed":
                job["status"] = "failed"
                job["error"] = chunk.get("error") or "Synthesis failed"
                _save_job(output, job)
                return

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
                    timeout=timeout,
                )
            except Exception as exc:
                chunk["status"] = "failed"
                chunk["error"] = f"{type(exc).__name__}: {exc}"
                job["status"] = "failed"
                job["error"] = chunk["error"]
                _save_job(output, job)
                return

            if not wav_bytes or wav_bytes[:4] != b"RIFF":
                chunk["status"] = "failed"
                chunk["error"] = f"Engine returned non-WAV payload ({len(wav_bytes)} bytes)"
                job["status"] = "failed"
                job["error"] = chunk["error"]
                _save_job(output, job)
                return

            os.makedirs(os.path.dirname(chunk["wav"]) or ".", exist_ok=True)
            with open(chunk["wav"], "wb") as handle:
                handle.write(wav_bytes)

            elapsed = time.monotonic() - started
            chunk["status"] = "done"
            chunk["bytes"] = len(wav_bytes)
            chunk["synthesis_seconds"] = round(elapsed, 2)
            chunk.pop("error", None)
            _save_job(output, job)

        if _cancel_requested(output):
            job = _load_job(output)
            job["status"] = "cancelled"
            job["error"] = "Cancelled"
            _save_job(output, job)
            return

        pending = [item for item in job["chunks"] if item.get("status") != "done"]
        if pending:
            job["status"] = "failed"
            job["error"] = "Worker finished with incomplete synthesis"
            _save_job(output, job)
            return

        _finalize(job, output)
    finally:
        _clear_pid(output)


def cmd_start(args: argparse.Namespace) -> None:
    if not os.path.isfile(args.script):
        _fail(f"Script file not found: {args.script}")

    if _job_path(args.output).is_file() and not args.force:
        job = _reconcile_worker_state(args.output, _load_job(args.output))
        if job.get("status") in {"pending", "running"} and _worker_alive(args.output):
            payload = _public_status_payload(job, output=args.output)
            payload["message"] = "Narration job already running."
            payload["poll"] = {"status": f"python3 narration.py status {args.output}"}
            _emit(payload)
            return
        if job.get("status") == "done":
            payload = _public_status_payload(job, output=args.output)
            payload["message"] = "Narration already complete. Use --force to re-run."
            _emit(payload)
            return

    if args.force:
        _terminate_worker(args.output)
        cancel_path = _cancel_path(args.output)
        if cancel_path.is_file():
            cancel_path.unlink(missing_ok=True)

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
                "wav": _chunk_wav_path(args.output, plan.index),
                "status": "pending",
            }
        )

    job = {
        "status": "pending",
        "created_at": _utc_now(),
        "updated_at": _utc_now(),
        "worker_started_at": None,
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
        },
        "preflight": {
            "open": preflight.get("open"),
            "warnings": preflight.get("warnings"),
            "route": preflight.get("route"),
        },
        "error": None,
        "result": None,
    }

    Path(STATE_DIR).mkdir(parents=True, exist_ok=True)
    _save_job(args.output, job)
    worker_pid = _spawn_worker(args.output)

    job = _load_job(args.output)
    payload = _public_status_payload(job, output=args.output)
    payload["message"] = "Preflight passed; narration worker started. Poll with `status` until done."
    payload["poll"] = {"status": f"python3 narration.py status {args.output}"}
    payload["worker_pid"] = worker_pid
    _emit(payload)


def cmd_status(args: argparse.Namespace) -> None:
    job = _reconcile_worker_state(args.output, _load_job(args.output))
    payload = _public_status_payload(job, output=args.output)
    status = job.get("status")
    if status == "done":
        payload["message"] = "Done."
    elif status == "failed":
        payload["message"] = "Failed."
    elif status == "cancelled":
        payload["message"] = "Cancelled."
    else:
        payload["message"] = "In progress — poll `status` again."
        payload["poll"] = {"status": f"python3 narration.py status {args.output}"}
    _emit(payload)


def cmd_cancel(args: argparse.Namespace) -> None:
    if not _job_path(args.output).is_file():
        _fail(f"No narration job for output {args.output!r}.", output=args.output)

    _cancel_path(args.output).write_text(_utc_now(), encoding="utf-8")
    terminated = _terminate_worker(args.output)

    job = _reconcile_worker_state(args.output, _load_job(args.output))
    if job.get("status") in {"pending", "running"}:
        job["status"] = "cancelled"
        job["error"] = "Cancelled"
        _save_job(args.output, job)

    payload = _public_status_payload(job, output=args.output)
    payload["message"] = "Cancel requested." if terminated else "Cancel flag set (worker already stopped)."
    payload["cancel"] = {"worker_terminated": terminated}
    _emit(payload)


def cmd_worker(args: argparse.Namespace) -> None:
    try:
        _run_worker(args.output, args.synthesis_timeout)
    except Exception as exc:
        try:
            job = _load_job(args.output)
            job["status"] = "failed"
            job["error"] = f"{type(exc).__name__}: {exc}"
            _save_job(args.output, job)
        except Exception:
            pass
        raise


def main() -> None:
    if len(sys.argv) >= 3 and sys.argv[1] == "worker":
        worker_parser = argparse.ArgumentParser(description="Internal narration worker")
        worker_parser.add_argument("output", help="Final WAV output path (job key)")
        worker_parser.add_argument(
            "--synthesis-timeout",
            type=float,
            default=SYNTHESIS_TIMEOUT_SECONDS,
            help="HTTP timeout per chunk synthesis call (default: 4 hours)",
        )
        cmd_worker(worker_parser.parse_args(sys.argv[2:]))
        return

    parser = argparse.ArgumentParser(description="Long-form single-speaker narration (detached worker)")
    sub = parser.add_subparsers(dest="command", required=True, metavar="{start,status,cancel}")

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
            help="Device words/sec heuristic for chunk sizing (~3.1 on the GPU host chatterbox)",
        )
        target.add_argument(
            "--max-chunk-seconds",
            type=float,
            default=DEFAULT_MAX_CHUNK_SECONDS,
            help="the GPU host estimated audio seconds per synthesis call (~95s from the GPU host tests)",
        )
        target.add_argument(
            "--synthesis-timeout",
            type=float,
            default=SYNTHESIS_TIMEOUT_SECONDS,
            help="HTTP timeout per chunk synthesis call (default: 4 hours)",
        )

    p_start = sub.add_parser("start", help="Preflight, create job, spawn detached worker")
    p_start.add_argument("script", help="Narration script file (markdown ok)")
    p_start.add_argument("-o", "--output", required=True, help="Final WAV output path")
    p_start.add_argument("--voice", required=True, help="Voice-pack voice id (e.g. narrator)")
    p_start.add_argument("--seed", type=int, default=None)
    p_start.add_argument("--force", action="store_true", help="Replace an existing job and re-run")
    add_shared_flags(p_start)

    p_status = sub.add_parser("status", help="Read job progress (scrubbed)")
    p_status.add_argument("output", help="Final WAV output path (job key)")

    p_cancel = sub.add_parser("cancel", help="Request cancellation and stop the worker")
    p_cancel.add_argument("output", help="Final WAV output path (job key)")

    args = parser.parse_args()
    {
        "start": cmd_start,
        "status": cmd_status,
        "cancel": cmd_cancel,
    }[args.command](args)


if __name__ == "__main__":
    main()
