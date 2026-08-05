#!/usr/bin/env python3
"""Capability probe for the audiocpp-extended skill.

Answers, with evidence, which escalation routes are open in this deployment:
  route1: raw engine API on the wrapper-loaded models (127.0.0.1:18082/18084)
  route2: sideload a bare ASR model dir through the ASR wrapper
  route3: spawn a private audiocpp_server for a non-catalog model
  route4: the user's host-native audiocpp_server
plus scenario checks (e.g. sortformer diarization) that ride on those routes.

Stdlib-only. Prints one JSON report to stdout.
"""
import importlib.util
import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request

WRAPPER_ASR = "http://127.0.0.1:8082"
WRAPPER_TTS = "http://127.0.0.1:8084"
ENGINE_ASR = "http://127.0.0.1:18082"
ENGINE_TTS = "http://127.0.0.1:18084"
HOST_ENGINE_CANDIDATES = ["http://host.docker.internal:8080"]
PRIVATE_ENGINE_PORT = 18099
SERVER_BINARY_CANDIDATES = ["/usr/local/bin/audiocpp_server"]
MODEL_DIRS = ["/models-local/tts", "/models-local/asr"]
HF_PROBE_URL = "https://huggingface.co/api/models/Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice"


def http_json(url: str, timeout: int = 5):
    request = urllib.request.Request(url, method="GET")
    with urllib.request.urlopen(request, timeout=timeout) as response:
        body = response.read().decode("utf-8", errors="replace")
        try:
            return response.status, json.loads(body)
        except json.JSONDecodeError:
            return response.status, body[:500]


def check_url(url: str, timeout: int = 5) -> dict:
    try:
        status, body = http_json(url, timeout)
        return {"reachable": True, "status": status, "body": body}
    except urllib.error.HTTPError as exc:
        # An HTTP error still proves something is listening.
        return {"reachable": True, "status": exc.code, "body": exc.read().decode("utf-8", errors="replace")[:500]}
    except Exception as exc:
        return {"reachable": False, "error": f"{type(exc).__name__}: {exc}"}


def check_binary() -> dict:
    override = os.environ.get("GA_TTS_SERVER_PATH", "").strip()
    candidates = ([override] if override else []) + SERVER_BINARY_CANDIDATES
    for path in candidates:
        if os.path.isfile(path) and os.access(path, os.X_OK):
            return {"present": True, "path": path}
    discovered = shutil.which("audiocpp_server")
    if discovered:
        return {"present": True, "path": discovered}
    return {"present": False, "checked": candidates + ["$PATH"]}


def check_binary_features(binary: dict) -> dict:
    """Scan the audiocpp_server binary for compiled-in capabilities the docs
    can't promise (loader registry differs between builds)."""
    tokens = {
        "sortformerDiarLoader": b"sortformer_diar",
        "tasksRunEndpoint": b"/v1/tasks/run",
        "sileroVadLoader": b"silero_vad",
    }
    if not binary.get("present"):
        return {"scanned": False}
    found = {key: False for key in tokens}
    overlap = max(len(token) for token in tokens.values())
    try:
        with open(binary["path"], "rb") as handle:
            tail = b""
            while not all(found.values()):
                chunk = handle.read(4 * 1024 * 1024)
                if not chunk:
                    break
                window = tail + chunk
                for key, token in tokens.items():
                    if not found[key] and token in window:
                        found[key] = True
                tail = window[-overlap:]
    except OSError as exc:
        return {"scanned": False, "error": str(exc)}
    return {"scanned": True, **found}


def check_ffmpeg() -> dict:
    path = shutil.which("ffmpeg")
    return {"present": bool(path), **({"path": path} if path else {})}


def check_dir(path: str) -> dict:
    result: dict = {"path": path, "exists": os.path.isdir(path)}
    if not result["exists"]:
        return result
    try:
        result["entries"] = sorted(os.listdir(path))[:20]
    except OSError as exc:
        result["listable"] = False
        result["error"] = str(exc)
        return result
    result["listable"] = True
    try:
        fd, probe_path = tempfile.mkstemp(prefix=".ga-skill-probe-", dir=path)
        os.close(fd)
        os.unlink(probe_path)
        result["writable"] = True
    except OSError as exc:
        result["writable"] = False
        result["writeError"] = str(exc)
    return result


def check_workspace() -> dict:
    cwd = os.getcwd()
    usage = shutil.disk_usage(cwd)
    return {
        "cwd": cwd,
        "writable": os.access(cwd, os.W_OK),
        "freeDiskGb": round(usage.free / (1024 ** 3), 1),
    }


def check_hf_egress() -> dict:
    try:
        status, body = http_json(HF_PROBE_URL, timeout=10)
        model_id = body.get("id") if isinstance(body, dict) else None
        return {"reachable": True, "status": status, "modelId": model_id}
    except Exception as exc:
        return {"reachable": False, "error": f"{type(exc).__name__}: {exc}"}


def check_gpu() -> dict:
    binary = shutil.which("nvidia-smi")
    if not binary:
        return {"nvidiaSmi": False}
    try:
        output = subprocess.run(
            [binary, "--query-gpu=name,memory.total,memory.used", "--format=csv,noheader"],
            capture_output=True, text=True, timeout=20,
        )
        return {"nvidiaSmi": True, "gpus": output.stdout.strip().splitlines(), "stderr": output.stderr.strip()[:300]}
    except Exception as exc:
        return {"nvidiaSmi": True, "error": f"{type(exc).__name__}: {exc}"}


def check_port_free(port: int) -> dict:
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        sock.bind(("127.0.0.1", port))
        return {"port": port, "bindable": True}
    except OSError as exc:
        return {"port": port, "bindable": False, "error": str(exc)}
    finally:
        sock.close()


def check_detach() -> dict:
    """Spawn a detached sleep and confirm it outlives this check. Cross-script
    survival can only be proven by `spawn_engine.py status` on a later call."""
    try:
        process = subprocess.Popen(
            ["sleep", "30"], start_new_session=True,
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        )
        alive = process.poll() is None
        if alive:
            process.terminate()
        return {"spawnedDetached": alive, "pid": process.pid}
    except Exception as exc:
        return {"spawnedDetached": False, "error": f"{type(exc).__name__}: {exc}"}


def check_python_packages() -> dict:
    return {
        name: importlib.util.find_spec(name) is not None
        for name in ("torch", "safetensors", "huggingface_hub", "requests")
    }


def main() -> None:
    binary = check_binary()
    report: dict = {
        "wrapperAsr": check_url(f"{WRAPPER_ASR}/health"),
        "wrapperTts": check_url(f"{WRAPPER_TTS}/health"),
        "engineAsr": check_url(f"{ENGINE_ASR}/health"),
        "engineTts": check_url(f"{ENGINE_TTS}/health"),
        "hostEngine": {},
        "binary": binary,
        "binaryFeatures": check_binary_features(binary),
        "ffmpeg": check_ffmpeg(),
        "modelDirs": [check_dir(path) for path in MODEL_DIRS],
        "workspace": check_workspace(),
        "hfEgress": check_hf_egress(),
        "gpu": check_gpu(),
        "privatePort": check_port_free(PRIVATE_ENGINE_PORT),
        "detachedSpawn": check_detach(),
        "pythonPackages": check_python_packages(),
        "env": {
            "HF_TOKEN": "set" if os.environ.get("HF_TOKEN") else "missing",
            "AUDIOCPP_ENGINE_URL": os.environ.get("AUDIOCPP_ENGINE_URL", "") or "missing",
        },
    }

    host_candidates = [os.environ["AUDIOCPP_ENGINE_URL"]] if os.environ.get("AUDIOCPP_ENGINE_URL") else HOST_ENGINE_CANDIDATES
    for candidate in host_candidates:
        result = check_url(f"{candidate.rstrip('/')}/health")
        entry = {"baseUrl": candidate, **result}
        if result.get("status") == 200:
            # A llama-server also answers /health 200; only audiocpp has /v1/audio/*.
            voices = check_url(f"{candidate.rstrip('/')}/v1/audio/voices")
            entry["isAudioEngine"] = voices.get("status") not in (None, 404)
            entry["voicesProbeStatus"] = voices.get("status")
        report["hostEngine"] = entry
        if result.get("reachable"):
            break

    writable_model_dir = any(entry.get("writable") for entry in report["modelDirs"])
    downloads_possible = report["hfEgress"]["reachable"] and (
        writable_model_dir or report["workspace"]["writable"]
    )
    report["routes"] = {
        "route1_raw_engine_scenarios": {
            "open": report["engineTts"].get("status") == 200 or report["engineAsr"].get("status") == 200,
            "note": "needs the wrapper to have a model loaded; load one via GuideAnts Settings if both engines are down",
        },
        "route2_asr_sideload": {
            "open": report["wrapperAsr"]["reachable"]
            and any(e.get("writable") and e["path"].endswith("asr") for e in report["modelDirs"])
            and report["hfEgress"]["reachable"],
        },
        "route3_private_engine": {
            "open": report["binary"]["present"]
            and report["privatePort"]["bindable"]
            and report["detachedSpawn"].get("spawnedDetached", False)
            and downloads_possible,
            "note": "cross-script process survival unproven until spawn_engine.py status succeeds on a later call",
        },
        "route4_host_engine": {
            "open": report["hostEngine"].get("status") == 200
            and report["hostEngine"].get("isAudioEngine", False),
            "note": "a llama-server on the same port answers /health but is not an audio engine",
        },
    }
    report["routes"]["scenario_diarization"] = {
        "open": report["routes"]["route3_private_engine"]["open"]
        and report["binaryFeatures"].get("sortformerDiarLoader", False)
        and report["binaryFeatures"].get("tasksRunEndpoint", False),
        "note": "Route 3 with family sortformer_diar / task diar; ffmpeg needed for non-16kHz-mono "
        "inputs, ASR engine (route1) needed for speaker-labeled transcripts — see diarize.py",
    }
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
