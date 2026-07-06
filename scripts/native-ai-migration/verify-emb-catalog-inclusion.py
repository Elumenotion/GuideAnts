#!/usr/bin/env python3
"""Build-time inclusion gate: each emb catalog GGUF loads on llama-server --embeddings."""

from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import time
import urllib.request

MANIFEST = os.environ.get(
    "GA_EMB_CATALOG_PATH",
    os.path.join(os.path.dirname(__file__), "../../docker/build/guideants-ai/emb-service/catalog/manifest.json"),
)
LLAMA_SERVER = os.environ.get("GA_EMB_SERVER_PATH", "llama-server")
ENGINE_PORT = int(os.environ.get("GA_EMB_INCLUSION_GATE_PORT", "19085"))


def wait_ready(port: int, timeout: float = 120.0) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=2) as resp:
                if resp.status == 200:
                    return
        except Exception:
            pass
        time.sleep(0.5)
    raise RuntimeError(f"llama-server did not become ready on port {port}")


def probe_dimension(gguf_path: str, pooling: str, port: int) -> int:
    proc = subprocess.Popen(
        [
            LLAMA_SERVER,
            "--embeddings",
            "--pooling",
            pooling,
            "-m",
            gguf_path,
            "--host",
            "127.0.0.1",
            "--port",
            str(port),
            "-ngl",
            "0",
        ],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
    )
    try:
        wait_ready(port)
        req = urllib.request.Request(
            f"http://127.0.0.1:{port}/v1/embeddings",
            data=json.dumps({"input": ["probe"]}).encode("utf-8"),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        with urllib.request.urlopen(req, timeout=60) as resp:
            body = json.load(resp)
        dim = len(body["data"][0]["embedding"])
        return dim
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            proc.kill()


def main() -> int:
    with open(MANIFEST, encoding="utf-8") as handle:
        manifest = json.load(handle)
    failures: list[str] = []
    for entry in manifest.get("entries", []):
        if entry.get("task") != "emb":
            continue
        expected = int(entry["producedDimension"])
        pooling = entry.get("pooling", "last")
        entry_id = entry["id"]
        for repo in entry.get("sourceRepos", []):
            filename = repo["filename"]
            with tempfile.TemporaryDirectory() as tmp:
                from huggingface_hub import hf_hub_download

                path = hf_hub_download(
                    repo_id=repo["repoId"],
                    filename=filename,
                    revision=repo.get("revision"),
                    local_dir=tmp,
                )
                actual = probe_dimension(path, pooling, ENGINE_PORT)
                if actual != expected:
                    failures.append(f"{entry_id}: expected dim {expected}, got {actual}")
                else:
                    print(f"OK {entry_id} {filename} dim={actual}")
    if failures:
        for line in failures:
            print(line, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
