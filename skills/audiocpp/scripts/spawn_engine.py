#!/usr/bin/env python3
"""Spawn a private audiocpp_server for a model the GuideAnts wrappers won't load.

Writes an engine config mirroring what the GuideAnts wrapper services generate,
launches the container's audiocpp_server binary detached on a private port, and
tracks it via a state dir in the workspace so later script calls can poll or stop
it. Stdlib-only.

Subcommands:
  start  --path <model dir> --family <loader family> [--task tts] [--port 18099] ...
  status [--port 18099]     poll /health; prints starting/ready/dead
  stop   [--port 18099]
  logs   [--port 18099] [--tail 100]
"""
import argparse
import json
import os
import shutil
import signal
import subprocess
import sys
import time
import urllib.request

DEFAULT_PORT = 18099
STATE_DIR = os.path.join(os.getcwd(), ".audiocpp-extended")
SERVER_BINARY_CANDIDATES = ["/usr/local/bin/audiocpp_server"]
START_POLL_SECONDS = 120  # stay well under the ~5 min script budget; use `status` after


def state_paths(port: int) -> dict:
    base = os.path.join(STATE_DIR, f"engine-{port}")
    return {
        "config": base + ".json",
        "pid": base + ".pid",
        "log": base + ".log",
        "meta": base + ".meta.json",
    }


def resolve_binary() -> str:
    override = os.environ.get("GA_TTS_SERVER_PATH", "").strip()
    candidates = ([override] if override else []) + SERVER_BINARY_CANDIDATES
    for path in candidates:
        if os.path.isfile(path) and os.access(path, os.X_OK):
            return path
    discovered = shutil.which("audiocpp_server")
    if discovered:
        return discovered
    sys.stderr.write("audiocpp_server binary not found (checked GA_TTS_SERVER_PATH, /usr/local/bin, $PATH).\n")
    sys.exit(1)


def health_ok(port: int, timeout: int = 5) -> bool:
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=timeout) as response:
            return response.status == 200
    except Exception:
        return False


def read_pid(paths: dict) -> int | None:
    try:
        with open(paths["pid"], "r", encoding="utf-8") as handle:
            return int(handle.read().strip())
    except (OSError, ValueError):
        return None


def pid_alive(pid: int) -> bool:
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def parse_kv(pairs: list[str]) -> dict:
    options = {}
    for pair in pairs:
        if "=" not in pair:
            sys.stderr.write(f"--option expects key=value, got: {pair}\n")
            sys.exit(1)
        key, value = pair.split("=", 1)
        options[key] = value
    return options


def cmd_start(args: argparse.Namespace) -> None:
    model_path = os.path.abspath(args.path)
    if not os.path.isdir(model_path):
        sys.stderr.write(f"Model directory not found: {model_path}\n")
        sys.exit(1)
    paths = state_paths(args.port)
    existing_pid = read_pid(paths)
    if existing_pid and pid_alive(existing_pid):
        print(json.dumps({"state": "already-running", "pid": existing_pid,
                          "ready": health_ok(args.port), "port": args.port}))
        return

    binary = resolve_binary()
    model_id = args.model_id or os.path.basename(model_path.rstrip("/"))
    options = parse_kv(args.option or [])
    model_config: dict = {
        "id": model_id,
        "family": args.family,
        "path": model_path,
        "task": args.task,
        "mode": "offline",
    }
    if options:
        model_config["load_options"] = dict(options)
        model_config["session_options"] = dict(options)
    config = {
        "host": "127.0.0.1",
        "port": args.port,
        "backend": args.backend,
        "device": args.device,
        "threads": args.threads,
        "lazy_load": False,
        "models": [model_config],
    }
    os.makedirs(STATE_DIR, exist_ok=True)
    with open(paths["config"], "w", encoding="utf-8") as handle:
        json.dump(config, handle, indent=2)
        handle.write("\n")

    command = [
        binary, "--config", paths["config"],
        "--host", "127.0.0.1", "--port", str(args.port),
        "--device", str(args.device), "--threads", str(args.threads),
    ]
    log_handle = open(paths["log"], "ab")
    process = subprocess.Popen(
        command, start_new_session=True,
        stdout=log_handle, stderr=subprocess.STDOUT,
    )
    with open(paths["pid"], "w", encoding="utf-8") as handle:
        handle.write(str(process.pid))
    with open(paths["meta"], "w", encoding="utf-8") as handle:
        json.dump({"command": command, "modelId": model_id, "family": args.family,
                   "task": args.task, "startedAtEpoch": time.time()}, handle, indent=2)

    deadline = time.monotonic() + min(args.wait, START_POLL_SECONDS)
    while time.monotonic() < deadline:
        if process.poll() is not None:
            log_tail = tail_log(paths, 40)
            print(json.dumps({"state": "dead", "exitCode": process.returncode,
                              "logTail": log_tail, "port": args.port}))
            sys.exit(1)
        if health_ok(args.port):
            print(json.dumps({"state": "ready", "pid": process.pid, "port": args.port,
                              "model": model_id, "engineUrl": f"http://127.0.0.1:{args.port}"}))
            return
        time.sleep(3)
    print(json.dumps({
        "state": "starting", "pid": process.pid, "port": args.port,
        "note": "engine left running; poll with `spawn_engine.py status` in a follow-up script call",
    }))


def tail_log(paths: dict, lines: int) -> list[str]:
    try:
        with open(paths["log"], "r", encoding="utf-8", errors="replace") as handle:
            return handle.readlines()[-lines:]
    except OSError:
        return []


def cmd_status(args: argparse.Namespace) -> None:
    paths = state_paths(args.port)
    pid = read_pid(paths)
    alive = bool(pid and pid_alive(pid))
    ready = health_ok(args.port)
    state = "ready" if ready else ("starting" if alive else "dead")
    result = {"state": state, "pid": pid, "port": args.port}
    if state == "dead":
        result["logTail"] = tail_log(paths, 40)
        result["note"] = ("no pid file — never started, or the detached process did not survive "
                          "between script calls (Route 3 limitation in this deployment)") if pid is None else "process exited"
    print(json.dumps(result, indent=2))
    if state == "dead":
        sys.exit(1)


def cmd_stop(args: argparse.Namespace) -> None:
    paths = state_paths(args.port)
    pid = read_pid(paths)
    if not pid or not pid_alive(pid):
        print(json.dumps({"state": "not-running", "port": args.port}))
        return
    os.kill(pid, signal.SIGTERM)
    for _ in range(20):
        if not pid_alive(pid):
            break
        time.sleep(0.5)
    if pid_alive(pid):
        os.kill(pid, signal.SIGKILL)
    try:
        os.unlink(paths["pid"])
    except OSError:
        pass
    print(json.dumps({"state": "stopped", "pid": pid, "port": args.port}))


def cmd_logs(args: argparse.Namespace) -> None:
    paths = state_paths(args.port)
    sys.stdout.writelines(tail_log(paths, args.tail))


def main() -> None:
    parser = argparse.ArgumentParser(description="Private audiocpp_server lifecycle")
    sub = parser.add_subparsers(dest="command", required=True)

    p_start = sub.add_parser("start")
    p_start.add_argument("--path", required=True, help="Absolute or workspace-relative model directory")
    p_start.add_argument("--family", required=True, help="audio.cpp loader family (e.g. qwen3_tts, vibevoice)")
    p_start.add_argument("--task", default="tts", help="tts | clon | vdes | asr | diar | vad (see references/engine-api.md)")
    p_start.add_argument("--model-id", default=None, help="Engine model id for requests (default: dir leaf name)")
    p_start.add_argument("--port", type=int, default=DEFAULT_PORT)
    p_start.add_argument("--backend", default="cuda")
    p_start.add_argument("--device", type=int, default=0)
    p_start.add_argument("--threads", type=int, default=max(1, (os.cpu_count() or 2) // 2))
    p_start.add_argument("--option", action="append", default=[], help="load/session option key=value (repeatable), e.g. language=english")
    p_start.add_argument("--wait", type=int, default=START_POLL_SECONDS, help="Seconds to poll for readiness before detaching")

    for name in ("status", "stop"):
        p = sub.add_parser(name)
        p.add_argument("--port", type=int, default=DEFAULT_PORT)

    p_logs = sub.add_parser("logs")
    p_logs.add_argument("--port", type=int, default=DEFAULT_PORT)
    p_logs.add_argument("--tail", type=int, default=100)

    args = parser.parse_args()
    {"start": cmd_start, "status": cmd_status, "stop": cmd_stop, "logs": cmd_logs}[args.command](args)


if __name__ == "__main__":
    main()
