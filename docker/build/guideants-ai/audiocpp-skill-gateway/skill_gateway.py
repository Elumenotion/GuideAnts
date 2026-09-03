#!/usr/bin/env python3
"""LAN-facing raw audiocpp_server access (not GuideAntsApi / ServiceModes).

External clients (PC sandboxes, agents, curl) get the full engine HTTP surface
via transparent reverse proxy:

  /asr/{path}      -> wrapper ASR engine  (127.0.0.1:18082)
  /tts/{path}      -> wrapper TTS engine  (127.0.0.1:18084)
  /private/{path}  -> skill-spawned engine (127.0.0.1:18099)

Plus gateway-owned helpers that raw engines cannot do over the LAN:

  POST /files                 stage an upload; returns a Max-local path for
                              path-based engine JSON fields
  POST /admin/models/fetch    HF download into /models-local/skill
  POST /admin/private/start   spawn private audiocpp_server
  GET  /admin/private/status
  POST /admin/private/stop

Auth: X-Audiocpp-Skill-Token == GA_AUDIOCPP_SKILL_TOKEN.
"""
from __future__ import annotations

import asyncio
import json
import logging
import os
import shutil
import signal
import socket
import subprocess
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from urllib.parse import urlparse

import uvicorn
from fastapi import Depends, FastAPI, Header, HTTPException, Request, UploadFile
from fastapi.responses import JSONResponse, Response
from pydantic import BaseModel, Field

APP = FastAPI(title="GuideAnts audiocpp raw gateway", version="2.0.0")

HF_BASE = "https://huggingface.co"
CHUNK_SIZE = 1024 * 1024
PROXY_METHODS = ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "HEAD"]
PRIVATE_LOCK = threading.Lock()
PRIVATE_STATE: dict[str, Any] = {
    "pid": None,
    "port": None,
    "config_path": None,
    "log_path": None,
    "meta": None,
}

# Gateway→engine proxy in-flight (skills bypass wrappers; wrappers.busy stays false
# unless we surface this). Keyed by requestId.
IN_FLIGHT_LOCK = threading.Lock()
IN_FLIGHT: dict[str, dict[str, Any]] = {}


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def env_int(name: str, default: int) -> int:
    raw = (os.getenv(name) or "").strip()
    if not raw:
        return default
    try:
        value = int(raw)
        return value if value > 0 else default
    except ValueError:
        return default


def log_event(event: str, **fields: Any) -> None:
    payload = {"event": event, "ts": utc_now_iso()}
    payload.update(fields)
    print(json.dumps(payload, ensure_ascii=True, sort_keys=True), flush=True)


def proxy_heartbeat_interval_seconds() -> float:
    raw = (os.getenv("GA_AUDIOCPP_SKILL_HEARTBEAT_SECONDS") or "").strip()
    if not raw:
        return 10.0
    try:
        value = float(raw)
    except ValueError:
        return 10.0
    return value if value > 0 else 10.0


def require_token() -> str:
    token = (os.getenv("GA_AUDIOCPP_SKILL_TOKEN") or "").strip()
    if not token:
        raise HTTPException(
            status_code=503,
            detail="GA_AUDIOCPP_SKILL_TOKEN is not configured on this host",
        )
    return token


def auth_dependency(
    x_audiocpp_skill_token: str | None = Header(default=None, alias="X-Audiocpp-Skill-Token"),
) -> None:
    expected = require_token()
    if not x_audiocpp_skill_token or x_audiocpp_skill_token != expected:
        raise HTTPException(status_code=401, detail="invalid or missing X-Audiocpp-Skill-Token")


def staging_root() -> Path:
    root = Path(os.getenv("GA_AUDIOCPP_SKILL_STAGING_DIR", "/var/lib/guideants/audiocpp-skill/staging"))
    root.mkdir(parents=True, exist_ok=True)
    return root


def models_root() -> Path:
    root = Path(os.getenv("GA_AUDIOCPP_SKILL_MODELS_DIR", "/models-local/skill"))
    root.mkdir(parents=True, exist_ok=True)
    return root


def private_state_dir() -> Path:
    root = Path(os.getenv("GA_AUDIOCPP_SKILL_STATE_DIR", "/var/lib/guideants/audiocpp-skill/private"))
    root.mkdir(parents=True, exist_ok=True)
    return root


def asr_engine_url() -> str:
    host = os.getenv("GA_ASR_ENGINE_HOST", "127.0.0.1").strip() or "127.0.0.1"
    port = env_int("GA_ASR_ENGINE_PORT", 18082)
    return f"http://{host}:{port}"


def tts_engine_url() -> str:
    host = os.getenv("GA_TTS_ENGINE_HOST", "127.0.0.1").strip() or "127.0.0.1"
    port = env_int("GA_TTS_ENGINE_PORT", 18084)
    return f"http://{host}:{port}"


def private_engine_port() -> int:
    return env_int("GA_AUDIOCPP_SKILL_PRIVATE_PORT", 18099)


def private_engine_url(port: int | None = None) -> str:
    return f"http://127.0.0.1:{port or private_engine_port()}"


def wrapper_tts_health_url() -> str:
    host = os.getenv("GA_TTS_HOST", "127.0.0.1").strip() or "127.0.0.1"
    port = env_int("GA_TTS_PORT", 8084)
    return f"http://{host}:{port}/health"


def wrapper_asr_health_url() -> str:
    host = os.getenv("GA_ASR_HOST", "127.0.0.1").strip() or "127.0.0.1"
    port = env_int("GA_ASR_PORT", 8082)
    return f"http://{host}:{port}/health"


def wrapper_base_url(route: str) -> str | None:
    if route == "asr":
        return wrapper_asr_health_url().rsplit("/", 1)[0]
    if route == "tts":
        return wrapper_tts_health_url().rsplit("/", 1)[0]
    return None


def proxy_timeout_seconds() -> float:
    return float(env_int("GA_AUDIOCPP_SKILL_PROXY_TIMEOUT_SECONDS", 300))


def report_wrapper_engine_failure(route: str, *, reason: str, request_id: str) -> None:
    """Ask the owning wrapper to kill/restart audiocpp_server. HTTP timeout does not stop GPU work."""
    base = wrapper_base_url(route)
    if not base:
        return
    url = f"{base}/admin/report-engine-failure"
    body = json.dumps({"reason": reason, "requestId": request_id}, ensure_ascii=True).encode("utf-8")
    req = urllib.request.Request(
        url,
        data=body,
        method="POST",
        headers={"Accept": "application/json", "Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(req, timeout=30) as response:
            payload = response.read().decode("utf-8", errors="replace")
            status_code = int(response.status)
        log_event(
            "audiocpp_skill_proxy_engine_recycle",
            route=route,
            reason=reason,
            requestId=request_id,
            target=url,
            statusCode=status_code,
            body=payload[:500],
        )
    except Exception as exc:
        log_event(
            "audiocpp_skill_proxy_engine_recycle_failed",
            route=route,
            reason=reason,
            requestId=request_id,
            target=url,
            errorType=type(exc).__name__,
            error=str(exc),
        )


def resolve_binary() -> str:
    override = (os.getenv("GA_TTS_SERVER_PATH") or os.getenv("GA_ASR_SERVER_PATH") or "").strip()
    candidates = ([override] if override else []) + ["/usr/local/bin/audiocpp_server"]
    for path in candidates:
        if path and os.path.isfile(path) and os.access(path, os.X_OK):
            return path
    discovered = shutil.which("audiocpp_server")
    if discovered:
        return discovered
    raise HTTPException(status_code=500, detail="audiocpp_server binary not found")


def probe_url(url: str, timeout: float = 3.0) -> dict[str, Any]:
    try:
        with urllib.request.urlopen(url, timeout=timeout) as response:
            body = response.read().decode("utf-8", errors="replace")
            try:
                parsed: Any = json.loads(body)
            except json.JSONDecodeError:
                parsed = body[:500]
            return {"reachable": True, "status": response.status, "body": parsed}
    except urllib.error.HTTPError as exc:
        return {
            "reachable": True,
            "status": exc.code,
            "body": exc.read().decode("utf-8", errors="replace")[:500],
        }
    except Exception as exc:
        return {"reachable": False, "error": f"{type(exc).__name__}: {exc}"}


def tcp_listening(host: str, port: int, timeout: float = 0.25) -> bool:
    try:
        with socket.create_connection((host, port), timeout=timeout):
            return True
    except OSError:
        return False


def host_port_from_url(url: str) -> tuple[str, int]:
    parsed = urlparse(url if "://" in url else f"http://{url}")
    host = parsed.hostname or "127.0.0.1"
    port = parsed.port or (443 if parsed.scheme == "https" else 80)
    return host, int(port)


def probe_upstream(
    url: str,
    *,
    timeout: float = 0.5,
    expect_busy_on_timeout: bool = True,
) -> dict[str, Any]:
    """Probe an upstream without treating inference stall as downtime.

    audiocpp_server is effectively single-request while synthesizing/transcribing.
    Its HTTP /health can hang until that work finishes. A listening TCP socket
    plus a health timeout means busy — not down.
    """
    host, port = host_port_from_url(url)
    listening = tcp_listening(host, port)
    if not listening:
        return {
            "reachable": False,
            "state": "down",
            "listening": False,
            "error": f"tcp connect failed to {host}:{port}",
        }

    http = probe_url(url, timeout=timeout)
    if http.get("reachable"):
        body = http.get("body")
        busy = False
        if isinstance(body, dict) and body.get("busy") is True:
            busy = True
        return {
            **http,
            "listening": True,
            "state": "busy" if busy else "up",
            "busy": busy,
        }

    if expect_busy_on_timeout:
        return {
            "reachable": True,
            "listening": True,
            "state": "busy",
            "busy": True,
            "error": http.get("error") or "health probe timed out while port is listening",
        }
    return {**http, "listening": True, "state": "down", "busy": False}


def probe_many(targets: dict[str, str], *, timeout: float = 0.5) -> dict[str, dict[str, Any]]:
    results: dict[str, dict[str, Any]] = {}
    with ThreadPoolExecutor(max_workers=max(1, len(targets))) as pool:
        futures = {
            pool.submit(probe_upstream, url, timeout=timeout): name
            for name, url in targets.items()
        }
        for future in as_completed(futures):
            name = futures[future]
            try:
                results[name] = future.result()
            except Exception as exc:
                results[name] = {
                    "reachable": False,
                    "state": "down",
                    "error": f"{type(exc).__name__}: {exc}",
                }
    return results


def private_health_ok(port: int, timeout: float = 0.5) -> bool:
    probe = probe_upstream(f"{private_engine_url(port)}/health", timeout=timeout)
    return probe.get("state") in {"up", "busy"}


def listening_summary(url: str) -> dict[str, Any]:
    host, port = host_port_from_url(url)
    listening = tcp_listening(host, port)
    return {
        "upstream": url,
        "listening": listening,
        "state": "listening" if listening else "down",
    }


def summarize_proxy_work(path: str, body: bytes) -> dict[str, Any]:
    """Extract operator-visible work fields from proxied engine JSON bodies."""
    work: dict[str, Any] = {"path": path or "/"}
    lowered = (path or "").lower()
    if "audio/speech" in lowered:
        work["kind"] = "speech"
    elif "audio/transcriptions" in lowered:
        work["kind"] = "transcription"
    elif "tasks/run" in lowered:
        work["kind"] = "task"
    else:
        work["kind"] = "other"
    if not body:
        return work
    try:
        payload = json.loads(body.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return work
    if not isinstance(payload, dict):
        return work
    model = payload.get("model")
    if isinstance(model, str) and model.strip():
        work["model"] = model.strip()
    voice = payload.get("voice")
    if isinstance(voice, str) and voice.strip():
        work["voice"] = voice.strip()
    if payload.get("voice_ref"):
        work["hasVoiceRef"] = True
    text = payload.get("input")
    if isinstance(text, str):
        work["inputChars"] = len(text)
        work["inputWords"] = len(text.split())
    audio = payload.get("audio")
    if isinstance(audio, str) and audio.strip():
        work["hasAudio"] = True
    request = payload.get("request")
    if isinstance(request, dict) and request.get("audio"):
        work["hasAudio"] = True
    return work


def track_proxy_start(request_id: str, *, route: str, method: str, work: dict[str, Any]) -> None:
    with IN_FLIGHT_LOCK:
        IN_FLIGHT[request_id] = {
            "requestId": request_id,
            "route": route,
            "method": method,
            "startedAtUtc": utc_now_iso(),
            "startedMono": time.monotonic(),
            "work": work,
            "recoveryRequested": False,
        }


def track_proxy_finish(request_id: str) -> None:
    with IN_FLIGHT_LOCK:
        IN_FLIGHT.pop(request_id, None)


def in_flight_for(route: str) -> dict[str, Any]:
    now = time.monotonic()
    with IN_FLIGHT_LOCK:
        items = [dict(item) for item in IN_FLIGHT.values() if item.get("route") == route]
    oldest_ms = None
    if items:
        started = [float(item["startedMono"]) for item in items if item.get("startedMono") is not None]
        if started:
            oldest_ms = int((now - min(started)) * 1000)
    return {"count": len(items), "requests": items[:8], "oldestAgeMs": oldest_ms}


def mark_recovery_requested(request_id: str) -> bool:
    with IN_FLIGHT_LOCK:
        item = IN_FLIGHT.get(request_id)
        if item is None or item.get("recoveryRequested"):
            return False
        item["recoveryRequested"] = True
        return True


def apply_gateway_in_flight(
    route: str,
    *,
    engine: dict[str, Any],
    wrapper: dict[str, Any] | None = None,
) -> None:
    """Mark product engines busy when this gateway is driving inference."""
    snap = in_flight_for(route)
    count = int(snap["count"])
    if count <= 0:
        return
    stalled = snap.get("oldestAgeMs") is not None and int(snap["oldestAgeMs"]) >= int(proxy_timeout_seconds() * 1000)
    engine["busy"] = True
    engine["gatewayInFlight"] = count
    engine["oldestInFlightAgeMs"] = snap.get("oldestAgeMs")
    if stalled:
        engine["failed"] = True
        engine["state"] = "failed"
    elif engine.get("listening") or engine.get("state") in {"listening", "up", "busy"}:
        engine["state"] = "busy"
    if wrapper is None:
        return
    wrapper["busy"] = True
    wrapper["gatewayInFlight"] = count
    wrapper["oldestInFlightAgeMs"] = snap.get("oldestAgeMs")
    if stalled:
        wrapper["failed"] = True
        wrapper["state"] = "failed"
    elif wrapper.get("state") in {None, "up", "listening"}:
        wrapper["state"] = "busy"
    body = wrapper.get("body")
    if isinstance(body, dict):
        wrapper["body"] = {
            **body,
            "busy": True,
            "gatewayInFlight": count,
            **({"failed": True} if stalled else {}),
        }


def pid_alive(pid: int) -> bool:
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def assert_skill_model_path(path: Path) -> Path:
    root = models_root().resolve()
    resolved = path.resolve()
    if root not in resolved.parents and resolved != root:
        raise HTTPException(status_code=400, detail=f"model path must be under {root}")
    return resolved


def _tail_log(path: Path, lines: int) -> list[str]:
    try:
        with path.open("r", encoding="utf-8", errors="replace") as handle:
            return handle.readlines()[-lines:]
    except OSError:
        return []


def stop_private_locked() -> dict[str, Any]:
    pid = PRIVATE_STATE.get("pid")
    port = PRIVATE_STATE.get("port") or private_engine_port()
    if not pid or not pid_alive(int(pid)):
        PRIVATE_STATE.update(
            {"pid": None, "port": None, "config_path": None, "log_path": None, "meta": None}
        )
        return {"state": "not-running", "port": port}
    os.kill(int(pid), signal.SIGTERM)
    for _ in range(20):
        if not pid_alive(int(pid)):
            break
        time.sleep(0.5)
    if pid_alive(int(pid)):
        os.kill(int(pid), signal.SIGKILL)
    PRIVATE_STATE.update(
        {"pid": None, "port": None, "config_path": None, "log_path": None, "meta": None}
    )
    return {"state": "stopped", "pid": int(pid), "port": port}


def hf_open(url: str, token: str | None, timeout: int = 60):
    headers = {"User-Agent": "guideants-audiocpp-raw-gateway/2.0"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    return urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=timeout)


def list_repo_files(repo: str, revision: str, token: str | None) -> list[dict[str, Any]]:
    url = f"{HF_BASE}/api/models/{repo}/tree/{urllib.parse.quote(revision)}?recursive=true"
    with hf_open(url, token) as response:
        entries = json.load(response)
    return [entry for entry in entries if entry.get("type") == "file"]


def _proxy_engine_sync(
    req: urllib.request.Request,
    timeout: float,
) -> tuple[int, bytes, str, dict[str, str]]:
    try:
        with urllib.request.urlopen(req, timeout=timeout) as upstream:
            content_type = upstream.headers.get("Content-Type", "application/octet-stream")
            payload = upstream.read()
            response_headers: dict[str, str] = {}
            for name in ("Content-Disposition", "X-Request-Id"):
                if upstream.headers.get(name):
                    response_headers[name] = upstream.headers.get(name)
            return int(upstream.status), payload, content_type, response_headers
    except urllib.error.HTTPError as exc:
        detail = exc.read()
        content_type = (
            exc.headers.get("Content-Type", "application/octet-stream")
            if exc.headers
            else "application/octet-stream"
        )
        return int(exc.code), detail, content_type, {}


async def proxy_engine(
    base: str,
    path: str,
    request: Request,
    *,
    route: str,
) -> Response:
    """Transparent reverse proxy: method, query, headers, body → engine."""
    query = request.url.query
    target = f"{base.rstrip('/')}/{path.lstrip('/')}"
    if query:
        target = f"{target}?{query}"

    body = await request.body()
    hop_by_hop = {
        "connection",
        "keep-alive",
        "proxy-authenticate",
        "proxy-authorization",
        "te",
        "trailers",
        "transfer-encoding",
        "upgrade",
        "host",
        "content-length",
        "x-audiocpp-skill-token",
    }
    headers: dict[str, str] = {}
    for key, value in request.headers.items():
        if key.lower() in hop_by_hop:
            continue
        headers[key] = value

    request_id = request.headers.get("x-request-id") or str(uuid.uuid4())
    work = summarize_proxy_work(path or "/", body)
    req = urllib.request.Request(
        target,
        data=body if body else None,
        method=request.method,
        headers=headers,
    )
    timeout = proxy_timeout_seconds()
    started = time.perf_counter()
    track_proxy_start(request_id, route=route, method=request.method, work=work)
    log_event(
        "audiocpp_skill_proxy_start",
        requestId=request_id,
        route=route,
        method=request.method,
        path=path or "/",
        bodyBytes=len(body),
        target=target,
        timeoutSeconds=timeout,
        inFlight=in_flight_for(route)["count"],
        **{k: v for k, v in work.items() if k != "path"},
    )

    stop = asyncio.Event()
    interval = proxy_heartbeat_interval_seconds()

    async def _heartbeat() -> None:
        while True:
            try:
                await asyncio.wait_for(stop.wait(), timeout=interval)
                return
            except asyncio.TimeoutError:
                elapsed_ms = int((time.perf_counter() - started) * 1000)
                log_event(
                    "audiocpp_skill_proxy_heartbeat",
                    requestId=request_id,
                    route=route,
                    method=request.method,
                    path=path or "/",
                    elapsedMs=elapsed_ms,
                    inFlight=in_flight_for(route)["count"],
                    **{k: v for k, v in work.items() if k != "path"},
                )
                if elapsed_ms >= int(timeout * 1000) and mark_recovery_requested(request_id):
                    log_event(
                        "audiocpp_skill_proxy_stalled",
                        requestId=request_id,
                        route=route,
                        elapsedMs=elapsed_ms,
                        timeoutSeconds=timeout,
                    )
                    await asyncio.to_thread(
                        report_wrapper_engine_failure,
                        route,
                        reason="proxy_stalled",
                        request_id=request_id,
                    )

    beat_task = asyncio.create_task(_heartbeat(), name=f"audiocpp-proxy-{request_id}")
    try:
        status_code, payload, content_type, response_headers = await asyncio.to_thread(
            _proxy_engine_sync,
            req,
            timeout,
        )
        latency_ms = int((time.perf_counter() - started) * 1000)
        if status_code >= 400:
            log_event(
                "audiocpp_skill_proxy_failed",
                requestId=request_id,
                route=route,
                method=request.method,
                path=path or "/",
                statusCode=status_code,
                latencyMs=latency_ms,
                responseBytes=len(payload),
                **{k: v for k, v in work.items() if k != "path"},
            )
            if status_code == 503 and mark_recovery_requested(request_id):
                await asyncio.to_thread(
                    report_wrapper_engine_failure,
                    route,
                    reason="engine_http_503",
                    request_id=request_id,
                )
        else:
            log_event(
                "audiocpp_skill_proxy_success",
                requestId=request_id,
                route=route,
                method=request.method,
                path=path or "/",
                statusCode=status_code,
                latencyMs=latency_ms,
                responseBytes=len(payload),
                **{k: v for k, v in work.items() if k != "path"},
            )
        response_headers = dict(response_headers)
        response_headers.setdefault("X-Request-Id", request_id)
        return Response(
            content=payload,
            status_code=status_code,
            media_type=content_type,
            headers=response_headers,
        )
    except Exception as exc:
        latency_ms = int((time.perf_counter() - started) * 1000)
        log_event(
            "audiocpp_skill_proxy_failed",
            requestId=request_id,
            route=route,
            method=request.method,
            path=path or "/",
            latencyMs=latency_ms,
            errorType=type(exc).__name__,
            error=str(exc),
            **{k: v for k, v in work.items() if k != "path"},
        )
        if mark_recovery_requested(request_id):
            await asyncio.to_thread(
                report_wrapper_engine_failure,
                route,
                reason=f"proxy_{type(exc).__name__}",
                request_id=request_id,
            )
        raise HTTPException(status_code=502, detail=f"upstream error: {exc}") from exc
    finally:
        track_proxy_finish(request_id)
        stop.set()
        try:
            await beat_task
        except Exception:
            pass


class FetchRequest(BaseModel):
    repo: str
    dest: str
    revision: str = "main"
    include: list[str] = Field(default_factory=list)
    exclude: list[str] = Field(default_factory=list)
    strip_prefix: str | None = None
    max_gb: float = 30.0
    dry_run: bool = False
    hf_token: str | None = None


class PrivateModelSpec(BaseModel):
    path: str
    family: str
    task: str = "tts"
    model_id: str | None = None
    options: dict[str, str] = Field(default_factory=dict)


class PrivateStartRequest(BaseModel):
    """Start a private audiocpp_server.

    Single-model (back-compat): path + family (+ task/options).
    Multi-model (Gate M): models=[{path,family,task,...}, ...] — path/family optional
    when models is non-empty; if both are set, path/family is prepended as the first model.
    """

    path: str | None = None
    family: str | None = None
    task: str = "tts"
    model_id: str | None = None
    port: int | None = None
    backend: str | None = None
    device: int = 0
    threads: int | None = None
    options: dict[str, str] = Field(default_factory=dict)
    models: list[PrivateModelSpec] = Field(default_factory=list)
    wait_seconds: int = 120


@APP.get("/health")
def health(_: None = Depends(auth_dependency)) -> dict[str, Any]:
    """Gateway liveness. Never waits on busy audiocpp_server inference.

    Wrapper HTTP probes stay (FastAPI snapshots remain responsive under engine
    load and expose catalogEntryId for skills). Engine checks are TCP-only.
    Deep engine HTTP probes belong on GET /ready.
    """
    private_port = int(PRIVATE_STATE.get("port") or private_engine_port())
    asr_up = asr_engine_url()
    tts_up = tts_engine_url()
    private_up = private_engine_url(private_port)
    wrapper_probes = probe_many(
        {
            "asr": wrapper_asr_health_url(),
            "tts": wrapper_tts_health_url(),
        },
        timeout=0.5,
    )
    engines = {
        "asr": {"base": "/asr", **listening_summary(asr_up)},
        "tts": {"base": "/tts", **listening_summary(tts_up)},
        "private": {"base": "/private", **listening_summary(private_up)},
    }
    wrappers = {
        "asr": {"base": "/asr", **wrapper_probes.get("asr", {})},
        "tts": {"base": "/tts", **wrapper_probes.get("tts", {})},
    }
    apply_gateway_in_flight("asr", engine=engines["asr"], wrapper=wrappers["asr"])
    apply_gateway_in_flight("tts", engine=engines["tts"], wrapper=wrappers["tts"])
    apply_gateway_in_flight("private", engine=engines["private"])
    return {
        "status": "ok",
        "service": "audiocpp-raw-gateway",
        "api_version": "2",
        "ts": utc_now_iso(),
        "engines": engines,
        "wrappers": wrappers,
        "modelsRoot": str(models_root()),
        "note": (
            "Liveness: wrappers are HTTP-probed (fast); engines are TCP listen "
            "checks only. Busy engines often stop answering HTTP /health while "
            "inference runs — that is not downtime. Gateway in-flight proxy work "
            "marks engines/wrappers busy even when wrappers are bypassed. Use "
            "GET /ready for deep engine probes that classify busy vs down."
        ),
    }


@APP.get("/ready")
def ready(_: None = Depends(auth_dependency)) -> dict[str, Any]:
    """Deep readiness with short parallel probes. Busy != down."""
    private_port = int(PRIVATE_STATE.get("port") or private_engine_port())
    targets = {
        "engineAsr": f"{asr_engine_url()}/health",
        "engineTts": f"{tts_engine_url()}/health",
        "enginePrivate": f"{private_engine_url(private_port)}/health",
        "wrapperAsr": wrapper_asr_health_url(),
        "wrapperTts": wrapper_tts_health_url(),
    }
    probes = probe_many(targets, timeout=0.5)
    # Gateway may be driving the engine while wrapper HTTP still says idle.
    for route, key in (("asr", "engineAsr"), ("tts", "engineTts"), ("private", "enginePrivate")):
        snap = in_flight_for(route)
        if snap["count"] <= 0:
            continue
        probe = probes.get(key) or {}
        probe["busy"] = True
        probe["gatewayInFlight"] = snap["count"]
        probe["oldestInFlightAgeMs"] = snap.get("oldestAgeMs")
        stalled = snap.get("oldestAgeMs") is not None and int(snap["oldestAgeMs"]) >= int(
            proxy_timeout_seconds() * 1000
        )
        if stalled:
            probe["failed"] = True
            probe["state"] = "failed"
        elif probe.get("listening") or probe.get("state") in {"up", "busy", "listening"}:
            probe["state"] = "busy"
        probes[key] = probe
        wrapper_key = "wrapperAsr" if route == "asr" else ("wrapperTts" if route == "tts" else None)
        if wrapper_key and wrapper_key in probes:
            wrap = probes[wrapper_key]
            wrap["busy"] = True
            wrap["gatewayInFlight"] = snap["count"]
            wrap["oldestInFlightAgeMs"] = snap.get("oldestAgeMs")
            wrap["state"] = "failed" if stalled else "busy"
            if stalled:
                wrap["failed"] = True
            body = wrap.get("body")
            if isinstance(body, dict):
                wrap["body"] = {
                    **body,
                    "busy": True,
                    "gatewayInFlight": snap["count"],
                    **({"failed": True} if stalled else {}),
                }
    states = [probe.get("state") for probe in probes.values()]
    any_failed = any(state == "failed" for state in states)
    any_up = any(state in {"up", "busy"} for state in states) and not any_failed
    any_busy = any(state == "busy" for state in states) or any(
        in_flight_for(route)["count"] > 0 for route in ("asr", "tts", "private")
    )
    return {
        "status": "failed" if any_failed else ("ok" if any_up else "degraded"),
        "ready": bool(any_up and not any_failed),
        "busy": any_busy,
        "failed": any_failed,
        "service": "audiocpp-raw-gateway",
        "api_version": "2",
        "ts": utc_now_iso(),
        "probes": probes,
        "note": (
            "state=busy means inference is in flight. state=failed means in-flight "
            "work exceeded the proxy timeout and the owning wrapper must recycle the engine."
        ),
    }


@APP.get("/admin/upstream")
def admin_upstream(_: None = Depends(auth_dependency)) -> dict[str, Any]:
    return ready()


@APP.post("/files")
async def stage_file(
    file: UploadFile,
    _: None = Depends(auth_dependency),
) -> dict[str, Any]:
    """Stage an uploaded file on Max; return an absolute path for engine JSON fields."""
    staging = staging_root() / str(uuid.uuid4())
    staging.mkdir(parents=True, exist_ok=True)
    name = Path(file.filename or "upload.bin").name
    dest = staging / name
    with dest.open("wb") as handle:
        while True:
            chunk = await file.read(CHUNK_SIZE)
            if not chunk:
                break
            handle.write(chunk)
    return {"path": str(dest.resolve()), "bytes": dest.stat().st_size, "filename": name}


@APP.api_route("/asr", methods=PROXY_METHODS)
@APP.api_route("/asr/{path:path}", methods=PROXY_METHODS)
async def proxy_asr(request: Request, path: str = "", _: None = Depends(auth_dependency)) -> Response:
    return await proxy_engine(asr_engine_url(), path, request, route="asr")


@APP.api_route("/tts", methods=PROXY_METHODS)
@APP.api_route("/tts/{path:path}", methods=PROXY_METHODS)
async def proxy_tts(request: Request, path: str = "", _: None = Depends(auth_dependency)) -> Response:
    return await proxy_engine(tts_engine_url(), path, request, route="tts")


@APP.api_route("/private", methods=PROXY_METHODS)
@APP.api_route("/private/{path:path}", methods=PROXY_METHODS)
async def proxy_private(request: Request, path: str = "", _: None = Depends(auth_dependency)) -> Response:
    return await proxy_engine(private_engine_url(), path, request, route="private")


@APP.post("/admin/models/fetch")
def fetch_model(body: FetchRequest, _: None = Depends(auth_dependency)) -> dict[str, Any]:
    dest = assert_skill_model_path(Path(body.dest))
    token = (body.hf_token or os.getenv("HF_TOKEN") or "").strip() or None
    try:
        files = list_repo_files(body.repo, body.revision, token)
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")[:300]
        hint = " (gated repo? pass hf_token or set HF_TOKEN)" if exc.code in (401, 403) else ""
        raise HTTPException(status_code=exc.code, detail=f"listing failed{hint}: {detail}") from exc

    selected: list[dict[str, Any]] = []
    for entry in files:
        path = entry["path"]
        if body.include and not any(path.startswith(prefix) for prefix in body.include):
            continue
        if any(path.startswith(prefix) for prefix in body.exclude):
            continue
        selected.append(entry)

    total_bytes = sum(int(entry.get("size") or 0) for entry in selected)
    if body.dry_run:
        return {
            "repo": body.repo,
            "dest": str(dest),
            "files": [{"path": e["path"], "size": e.get("size")} for e in selected],
            "totalGb": round(total_bytes / (1024**3), 2),
        }
    if total_bytes > body.max_gb * (1024**3):
        raise HTTPException(
            status_code=400,
            detail=f"selection is {total_bytes / (1024**3):.1f} GB over max_gb={body.max_gb}",
        )

    downloaded: list[str] = []
    skipped: list[str] = []
    dest.mkdir(parents=True, exist_ok=True)
    for entry in selected:
        repo_path = entry["path"]
        local_rel = repo_path
        if body.strip_prefix and local_rel.startswith(body.strip_prefix):
            local_rel = local_rel[len(body.strip_prefix) :].lstrip("/")
        local_path = dest / local_rel
        expected_size = entry.get("size")
        if expected_size is not None and local_path.is_file() and local_path.stat().st_size == expected_size:
            skipped.append(repo_path)
            continue
        local_path.parent.mkdir(parents=True, exist_ok=True)
        url = (
            f"{HF_BASE}/{body.repo}/resolve/{urllib.parse.quote(body.revision)}/"
            f"{urllib.parse.quote(repo_path)}"
        )
        temp_path = Path(str(local_path) + ".part")
        try:
            with hf_open(url, token, timeout=120) as response, temp_path.open("wb") as handle:
                while True:
                    chunk = response.read(CHUNK_SIZE)
                    if not chunk:
                        break
                    handle.write(chunk)
            os.replace(temp_path, local_path)
            downloaded.append(repo_path)
        except Exception:
            if temp_path.exists():
                temp_path.unlink(missing_ok=True)
            raise
    return {
        "dest": str(dest),
        "downloaded": len(downloaded),
        "skippedUpToDate": len(skipped),
        "totalSelected": len(selected),
        "note": "re-run to resume if truncated",
    }


def build_private_model_configs(body: PrivateStartRequest) -> list[dict[str, Any]]:
    specs: list[PrivateModelSpec] = []
    if body.path and body.family:
        specs.append(
            PrivateModelSpec(
                path=body.path,
                family=body.family,
                task=body.task,
                model_id=body.model_id,
                options=dict(body.options),
            )
        )
    specs.extend(body.models)
    if not specs:
        raise HTTPException(
            status_code=400,
            detail="provide path+family and/or a non-empty models list",
        )
    configs: list[dict[str, Any]] = []
    seen_ids: set[str] = set()
    for spec in specs:
        model_path = assert_skill_model_path(Path(spec.path))
        if not model_path.is_dir():
            raise HTTPException(status_code=400, detail=f"model directory not found: {model_path}")
        model_id = spec.model_id or model_path.name
        if model_id in seen_ids:
            raise HTTPException(status_code=400, detail=f"duplicate model id: {model_id}")
        seen_ids.add(model_id)
        entry: dict[str, Any] = {
            "id": model_id,
            "family": spec.family,
            "path": str(model_path),
            "task": spec.task,
            "mode": "offline",
        }
        if spec.options:
            entry["load_options"] = dict(spec.options)
            entry["session_options"] = dict(spec.options)
        configs.append(entry)
    return configs


@APP.post("/admin/private/start")
def private_start(body: PrivateStartRequest, _: None = Depends(auth_dependency)) -> dict[str, Any]:
    model_configs = build_private_model_configs(body)
    port = body.port or private_engine_port()
    backend = (body.backend or os.getenv("GA_ASR_BACKEND") or os.getenv("GA_TTS_BACKEND") or "cuda").strip()
    threads = body.threads or max(1, (os.cpu_count() or 2) // 2)
    binary = resolve_binary()
    primary = model_configs[0]
    model_ids = [entry["id"] for entry in model_configs]

    with PRIVATE_LOCK:
        existing_pid = PRIVATE_STATE.get("pid")
        if existing_pid and pid_alive(int(existing_pid)):
            ready = private_health_ok(int(PRIVATE_STATE.get("port") or port))
            return {
                "state": "already-running",
                "pid": int(existing_pid),
                "ready": ready,
                "port": PRIVATE_STATE.get("port") or port,
                "engineUrl": private_engine_url(int(PRIVATE_STATE.get("port") or port)),
                "proxyBase": "/private",
                "models": (PRIVATE_STATE.get("meta") or {}).get("modelIds")
                or [(PRIVATE_STATE.get("meta") or {}).get("modelId")],
            }

        config = {
            "host": "127.0.0.1",
            "port": port,
            "backend": backend,
            "device": body.device,
            "threads": threads,
            "lazy_load": False,
            "models": model_configs,
        }
        state_dir = private_state_dir()
        config_path = state_dir / f"engine-{port}.json"
        log_path = state_dir / f"engine-{port}.log"
        with config_path.open("w", encoding="utf-8") as handle:
            json.dump(config, handle, indent=2)
            handle.write("\n")

        command = [
            binary,
            "--config",
            str(config_path),
            "--host",
            "127.0.0.1",
            "--port",
            str(port),
            "--device",
            str(body.device),
            "--threads",
            str(threads),
        ]
        log_handle = open(log_path, "ab")
        process = subprocess.Popen(
            command,
            start_new_session=True,
            stdout=log_handle,
            stderr=subprocess.STDOUT,
        )
        meta = {
            "command": command,
            "modelId": primary["id"],
            "modelIds": model_ids,
            "family": primary["family"],
            "task": primary["task"],
            "models": [
                {"id": entry["id"], "family": entry["family"], "task": entry["task"], "path": entry["path"]}
                for entry in model_configs
            ],
            "startedAtEpoch": time.time(),
        }
        PRIVATE_STATE.update(
            {
                "pid": process.pid,
                "port": port,
                "config_path": str(config_path),
                "log_path": str(log_path),
                "meta": meta,
            }
        )

        deadline = time.monotonic() + max(1, min(body.wait_seconds, 300))
        while time.monotonic() < deadline:
            if process.poll() is not None:
                PRIVATE_STATE.update(
                    {"pid": None, "port": None, "config_path": None, "log_path": None, "meta": None}
                )
                raise HTTPException(
                    status_code=500,
                    detail={
                        "state": "dead",
                        "exitCode": process.returncode,
                        "logTail": _tail_log(log_path, 40),
                    },
                )
            if private_health_ok(port):
                return {
                    "state": "ready",
                    "pid": process.pid,
                    "port": port,
                    "model": primary["id"],
                    "models": model_ids,
                    "engineUrl": private_engine_url(port),
                    "proxyBase": "/private",
                }
            time.sleep(2)
        return {
            "state": "starting",
            "pid": process.pid,
            "port": port,
            "model": primary["id"],
            "models": model_ids,
            "engineUrl": private_engine_url(port),
            "proxyBase": "/private",
            "note": "engine left running; poll /admin/private/status",
        }


@APP.get("/admin/private/status")
def private_status(_: None = Depends(auth_dependency)) -> dict[str, Any]:
    with PRIVATE_LOCK:
        pid = PRIVATE_STATE.get("pid")
        port = int(PRIVATE_STATE.get("port") or private_engine_port())
        alive = bool(pid and pid_alive(int(pid)))
        ready = private_health_ok(port)
        state = "ready" if ready else ("starting" if alive else "dead")
        result: dict[str, Any] = {
            "state": state,
            "pid": pid,
            "port": port,
            "engineUrl": private_engine_url(port),
            "proxyBase": "/private",
            "meta": PRIVATE_STATE.get("meta"),
        }
        if state == "dead" and PRIVATE_STATE.get("log_path"):
            result["logTail"] = _tail_log(Path(str(PRIVATE_STATE["log_path"])), 40)
        return result


@APP.post("/admin/private/stop")
def private_stop(_: None = Depends(auth_dependency)) -> dict[str, Any]:
    with PRIVATE_LOCK:
        return stop_private_locked()


@APP.exception_handler(HTTPException)
async def http_exception_handler(_: Request, exc: HTTPException) -> JSONResponse:
    return JSONResponse(status_code=exc.status_code, content={"detail": exc.detail})


def main() -> None:
    host = os.getenv("GA_AUDIOCPP_SKILL_HOST", "127.0.0.1").strip() or "127.0.0.1"
    port = env_int("GA_AUDIOCPP_SKILL_PORT", 8096)
    log_level = (os.getenv("GA_AUDIOCPP_SKILL_LOG_LEVEL") or "warning").strip() or "warning"
    staging_root()
    models_root()
    private_state_dir()
    logging.getLogger("uvicorn.access").setLevel(logging.WARNING)
    uvicorn.run(APP, host=host, port=port, log_level=log_level, access_log=False)


if __name__ == "__main__":
    main()
