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

import json
import logging
import os
import shutil
import signal
import subprocess
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

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


def private_health_ok(port: int, timeout: float = 3.0) -> bool:
    probe = probe_url(f"{private_engine_url(port)}/health", timeout=timeout)
    return bool(probe.get("reachable") and probe.get("status") == 200)


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


async def proxy_engine(base: str, path: str, request: Request) -> Response:
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

    req = urllib.request.Request(target, data=body if body else None, method=request.method, headers=headers)
    timeout = float(os.getenv("GA_AUDIOCPP_SKILL_PROXY_TIMEOUT_SECONDS", "3600") or "3600")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as upstream:
            content_type = upstream.headers.get("Content-Type", "application/octet-stream")
            payload = upstream.read()
            response_headers = {}
            for name in ("Content-Disposition", "X-Request-Id"):
                if upstream.headers.get(name):
                    response_headers[name] = upstream.headers.get(name)
            return Response(
                content=payload,
                status_code=upstream.status,
                media_type=content_type,
                headers=response_headers,
            )
    except urllib.error.HTTPError as exc:
        detail = exc.read()
        content_type = exc.headers.get("Content-Type", "application/octet-stream") if exc.headers else "application/octet-stream"
        return Response(content=detail, status_code=exc.code, media_type=content_type)
    except Exception as exc:
        raise HTTPException(status_code=502, detail=f"upstream error: {exc}") from exc


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
    private_port = PRIVATE_STATE.get("port") or private_engine_port()
    return {
        "status": "ok",
        "service": "audiocpp-raw-gateway",
        "api_version": "2",
        "ts": utc_now_iso(),
        "engines": {
            "asr": {"base": "/asr", "upstream": asr_engine_url(), **probe_url(f"{asr_engine_url()}/health")},
            "tts": {"base": "/tts", "upstream": tts_engine_url(), **probe_url(f"{tts_engine_url()}/health")},
            "private": {
                "base": "/private",
                "upstream": private_engine_url(int(private_port)),
                **probe_url(f"{private_engine_url(int(private_port))}/health"),
            },
        },
        "wrappers": {
            "asr": probe_url(wrapper_asr_health_url()),
            "tts": probe_url(wrapper_tts_health_url()),
        },
        "modelsRoot": str(models_root()),
        "note": "All /asr/*, /tts/*, /private/* paths are transparent proxies to audiocpp_server.",
    }


@APP.get("/admin/upstream")
def admin_upstream(_: None = Depends(auth_dependency)) -> dict[str, Any]:
    return health()


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
    return await proxy_engine(asr_engine_url(), path, request)


@APP.api_route("/tts", methods=PROXY_METHODS)
@APP.api_route("/tts/{path:path}", methods=PROXY_METHODS)
async def proxy_tts(request: Request, path: str = "", _: None = Depends(auth_dependency)) -> Response:
    return await proxy_engine(tts_engine_url(), path, request)


@APP.api_route("/private", methods=PROXY_METHODS)
@APP.api_route("/private/{path:path}", methods=PROXY_METHODS)
async def proxy_private(request: Request, path: str = "", _: None = Depends(auth_dependency)) -> Response:
    return await proxy_engine(private_engine_url(), path, request)


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
