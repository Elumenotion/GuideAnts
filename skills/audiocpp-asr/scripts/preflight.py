#!/usr/bin/env python3
"""Scoped capability preflight for the audiocpp task skills.

Every audiocpp-* task skill ships an identical copy of this script and invokes
it with its own scenario. It checks only that scenario's prerequisites and
prints one JSON verdict:

  {"scenario": ..., "open": bool, "blockers": [...], "warnings": [...], "evidence": {...}}

`open: false` means a hard prerequisite failed — do not attempt the scenario;
tell the user what `blockers` says. `warnings` are soft gaps (a degraded but
workable path exists). Trust this report over any skill documentation.
Stdlib-only.

Scenarios: voice-clone | tts-controls | asr-extended | diarize | deferred-tts | host-tts
"""
import argparse
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
HOST_ENGINE_DEFAULT = "http://host.docker.internal:8080"
PRIVATE_ENGINE_PORT = 18099
BINARY_CANDIDATES = ["/usr/local/bin/audiocpp_server"]
MODEL_DIRS = ["/models-local/tts", "/models-local/asr"]
DIAR_MODEL_WEIGHTS = "/models-local/asr/diar_sortformer_4spk-v1/model.safetensors"
HF_PROBE_URL = "https://huggingface.co/api/models/Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice"


def http_json(url: str, timeout: int = 5):
    with urllib.request.urlopen(urllib.request.Request(url, method="GET"), timeout=timeout) as response:
        body = response.read().decode("utf-8", errors="replace")
        try:
            return response.status, json.loads(body)
        except json.JSONDecodeError:
            return response.status, body[:300]


def check_url(url: str, timeout: int = 5) -> dict:
    try:
        status, body = http_json(url, timeout)
        return {"ok": status == 200, "status": status, "body": body}
    except urllib.error.HTTPError as exc:
        return {"ok": False, "status": exc.code, "body": exc.read().decode("utf-8", errors="replace")[:300]}
    except Exception as exc:
        return {"ok": False, "error": f"{type(exc).__name__}: {exc}"}


def check_binary() -> dict:
    override = os.environ.get("GA_TTS_SERVER_PATH", "").strip()
    for path in ([override] if override else []) + BINARY_CANDIDATES:
        if os.path.isfile(path) and os.access(path, os.X_OK):
            return {"ok": True, "path": path}
    discovered = shutil.which("audiocpp_server")
    if discovered:
        return {"ok": True, "path": discovered}
    return {"ok": False, "error": "audiocpp_server not found"}


def check_binary_features(binary: dict, tokens: dict) -> dict:
    if not binary.get("ok"):
        return {"ok": False, "error": "binary missing"}
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
        return {"ok": False, "error": str(exc)}
    return {"ok": all(found.values()), **found}


def check_downloads_possible() -> dict:
    try:
        status, body = http_json(HF_PROBE_URL, timeout=10)
        hf = {"ok": status == 200}
    except Exception as exc:
        hf = {"ok": False, "error": f"{type(exc).__name__}: {exc}"}
    writable = []
    for path in MODEL_DIRS:
        if os.path.isdir(path):
            try:
                fd, probe_path = tempfile.mkstemp(prefix=".ga-skill-preflight-", dir=path)
                os.close(fd)
                os.unlink(probe_path)
                writable.append(path)
            except OSError:
                pass
    workspace_writable = os.access(os.getcwd(), os.W_OK)
    return {
        "ok": hf["ok"] and (bool(writable) or workspace_writable),
        "hfEgress": hf,
        "writableModelDirs": writable,
        "workspaceWritable": workspace_writable,
    }


def check_asr_dir_writable() -> dict:
    path = "/models-local/asr"
    if not os.path.isdir(path):
        return {"ok": False, "error": f"{path} does not exist"}
    try:
        fd, probe_path = tempfile.mkstemp(prefix=".ga-skill-preflight-", dir=path)
        os.close(fd)
        os.unlink(probe_path)
        return {"ok": True, "path": path}
    except OSError as exc:
        return {"ok": False, "error": str(exc)}


def check_private_port() -> dict:
    """OK when a skill-spawned engine already answers on the port, or the port is free."""
    health = check_url(f"http://127.0.0.1:{PRIVATE_ENGINE_PORT}/health")
    if health.get("ok"):
        return {"ok": True, "engineAlreadyRunning": True}
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        sock.bind(("127.0.0.1", PRIVATE_ENGINE_PORT))
        return {"ok": True, "engineAlreadyRunning": False, "bindable": True}
    except OSError as exc:
        return {"ok": False, "error": f"port {PRIVATE_ENGINE_PORT} occupied by something unhealthy: {exc}"}
    finally:
        sock.close()


def check_detach() -> dict:
    try:
        process = subprocess.Popen(
            ["sleep", "30"], start_new_session=True,
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        )
        alive = process.poll() is None
        if alive:
            process.terminate()
        return {"ok": alive, "note": "cross-script survival unproven until spawn_engine.py status succeeds on a later call"}
    except Exception as exc:
        return {"ok": False, "error": f"{type(exc).__name__}: {exc}"}


def check_ffmpeg() -> dict:
    path = shutil.which("ffmpeg")
    return {"ok": bool(path), **({"path": path} if path else {})}


def check_gpu() -> dict:
    binary = shutil.which("nvidia-smi")
    if not binary:
        return {"ok": False, "error": "nvidia-smi not found"}
    try:
        output = subprocess.run(
            [binary, "--query-gpu=name,memory.total,memory.used", "--format=csv,noheader"],
            capture_output=True, text=True, timeout=20,
        )
        return {"ok": True, "gpus": output.stdout.strip().splitlines()}
    except Exception as exc:
        return {"ok": False, "error": f"{type(exc).__name__}: {exc}"}


def check_host_engine() -> dict:
    base = (os.environ.get("AUDIOCPP_ENGINE_URL") or HOST_ENGINE_DEFAULT).rstrip("/")
    health = check_url(f"{base}/health")
    entry = {"baseUrl": base, **health}
    if health.get("ok"):
        # A llama-server also answers /health 200; only audiocpp has /v1/audio/*.
        voices = check_url(f"{base}/v1/audio/voices")
        entry["isAudioEngine"] = voices.get("status") not in (None, 404)
        entry["ok"] = entry["isAudioEngine"]
        if not entry["isAudioEngine"]:
            entry["error"] = "answers /health but has no /v1/audio/* — likely a llama-server, not audiocpp"
    return entry


def run_scenario(scenario: str) -> dict:
    evidence: dict = {}
    blockers: list[str] = []
    warnings: list[str] = []

    def required(name: str, result: dict, hint: str) -> None:
        evidence[name] = result
        if not result.get("ok"):
            blockers.append(f"{name}: {result.get('error', result.get('status', 'failed'))} — {hint}")

    def optional(name: str, result: dict, hint: str) -> None:
        evidence[name] = result
        if not result.get("ok"):
            warnings.append(f"{name}: {hint}")

    if scenario in ("voice-clone", "tts-controls"):
        required("engineTts", check_url(f"{ENGINE_TTS}/health"),
                 "no TTS engine — ask the user to load a TTS model via GuideAnts Settings")
        wrapper = check_url(f"{WRAPPER_TTS}/health")
        evidence["wrapperTts"] = wrapper
        body = wrapper.get("body") if isinstance(wrapper.get("body"), dict) else {}
        evidence["loadedModel"] = body.get("catalogEntryId")
        if scenario == "voice-clone" and body.get("catalogEntryId") not in (None, "chatterbox"):
            warnings.append(f"loaded model is {body.get('catalogEntryId')!r}, not a clon-task family — "
                            "voice_ref may be rejected; the engine's error text will say")

    elif scenario == "asr-extended":
        required("engineAsr", check_url(f"{ENGINE_ASR}/health"),
                 "no ASR engine — ask the user to load an ASR model via GuideAnts Settings")
        optional("asrDirWritable", check_asr_dir_writable(), "sideloading non-catalog ASR snapshots is blocked")
        optional("hfEgress", check_downloads_possible(), "downloading snapshots to sideload is blocked")

    elif scenario == "diarize":
        binary = check_binary()
        required("binary", binary, "no audiocpp_server binary to spawn")
        required("binaryFeatures", check_binary_features(
            binary, {"sortformerDiarLoader": b"sortformer_diar", "tasksRunEndpoint": b"/v1/tasks/run"}),
            "this build lacks the sortformer_diar loader or /v1/tasks/run")
        required("privatePort", check_private_port(), "cannot host a private engine")
        required("detachedSpawn", check_detach(), "cannot leave an engine running")
        model_present = os.path.isfile(DIAR_MODEL_WEIGHTS)
        evidence["modelAlreadyDownloaded"] = {"ok": model_present, "path": DIAR_MODEL_WEIGHTS}
        if not model_present:
            required("downloads", check_downloads_possible(),
                     "model not on disk and cannot be downloaded (HF egress / writable dir)")
        optional("ffmpeg", check_ffmpeg(), "only 16 kHz mono PCM16 WAV inputs will work (no conversion)")
        optional("engineAsr", check_url(f"{ENGINE_ASR}/health"),
                 "turns will not get text labels — ask the user to load an ASR model, or run --turns-only")
        optional("gpu", check_gpu(), "GPU state unknown")

    elif scenario == "deferred-tts":
        required("binary", check_binary(), "no audiocpp_server binary to spawn")
        required("privatePort", check_private_port(), "cannot host a private engine")
        required("detachedSpawn", check_detach(), "cannot leave an engine warming up across script calls")
        required("downloads", check_downloads_possible(), "cannot download model snapshots")
        optional("gpu", check_gpu(), "GPU state unknown")
        wrapper = check_url(f"{WRAPPER_TTS}/health")
        evidence["wrapperTts"] = wrapper
        body = wrapper.get("body") if isinstance(wrapper.get("body"), dict) else {}
        if body.get("loaded"):
            warnings.append(f"GuideAnts TTS model {body.get('catalogEntryId')!r} is loaded — a second engine "
                            "competes for VRAM; ask the user before unloading anything")

    elif scenario == "host-tts":
        required("hostEngine", check_host_engine(),
                 "host-native audiocpp_server unreachable — ask the user to start it bound to 0.0.0.0 "
                 "or set AUDIOCPP_ENGINE_URL in the guide's Environment variables")

    else:
        sys.stderr.write(f"unknown scenario: {scenario}\n")
        sys.exit(2)

    return {"scenario": scenario, "open": not blockers, "blockers": blockers,
            "warnings": warnings, "evidence": evidence}


def main() -> None:
    parser = argparse.ArgumentParser(description="Scoped preflight for one audiocpp task skill")
    parser.add_argument("--for", dest="scenario", required=True,
                        choices=["voice-clone", "tts-controls", "asr-extended", "diarize", "deferred-tts", "host-tts"])
    args = parser.parse_args()
    print(json.dumps(run_scenario(args.scenario), indent=2))


if __name__ == "__main__":
    main()
