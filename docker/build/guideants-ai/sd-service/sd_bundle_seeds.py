"""
Default Stable Diffusion bundle recipes shipped with GuideAnts.

These are definition-only seeds: they create bundle-definition.json under
/models-local/sd/bundles/<id>/ so fresh installs surface ready-to-download
recipes in Settings. Model files are not downloaded automatically.
"""

from __future__ import annotations

import json
import os
import uuid
from typing import Any

# Stable ids + (repo, file) tuples for the three operator-facing bundles.
DEFAULT_BUNDLE_DEFINITION_SEEDS: list[dict[str, Any]] = [
    {
        "bundleId": "FLUX.2-dev-GGUF-Q5_K_M",
        "revision": "main",
        "roles": {
            "diffusion": {
                "repo": "unsloth/FLUX.2-dev-GGUF",
                "file": "flux2-dev-Q5_K_M.gguf",
            },
            "vae": {
                "repo": "black-forest-labs/FLUX.2-small-decoder",
                "file": "full_encoder_small_decoder.safetensors",
            },
            "textEncoder": {
                "repo": "unsloth/Mistral-Small-3.2-24B-Instruct-2506-GGUF",
                "file": "Mistral-Small-3.2-24B-Instruct-2506-Q2_K_L.gguf",
            },
        },
    },
    {
        "bundleId": "flux2-klein-4b-q4ks",
        "revision": "main",
        "roles": {
            "diffusion": {
                "repo": "unsloth/FLUX.2-klein-4B-GGUF",
                "file": "flux-2-klein-4b-Q4_K_S.gguf",
            },
            "vae": {
                "repo": "black-forest-labs/FLUX.2-small-decoder",
                "file": "full_encoder_small_decoder.safetensors",
            },
            "textEncoder": {
                "repo": "unsloth/Qwen3-4B-GGUF",
                "file": "Qwen3-4B-Q4_K_M.gguf",
            },
        },
    },
    {
        "bundleId": "flux2-klein-9b-q5",
        "revision": "main",
        "roles": {
            "diffusion": {
                "repo": "unsloth/FLUX.2-klein-9B-GGUF",
                "file": "flux-2-klein-9b-Q5_K_M.gguf",
            },
            "vae": {
                "repo": "black-forest-labs/FLUX.2-small-decoder",
                "file": "full_encoder_small_decoder.safetensors",
            },
            "textEncoder": {
                "repo": "unsloth/Qwen3-8B-GGUF",
                "file": "Qwen3-8B-Q5_K_M.gguf",
            },
        },
    },
]


def bundle_definition_path(model_dir: str, bundle_id: str) -> str:
    return os.path.join(model_dir, "bundles", bundle_id, "bundle-definition.json")


def _write_bundle_definition_atomic(bundle_path: str, payload: dict[str, Any]) -> None:
    os.makedirs(bundle_path, exist_ok=True)
    target = os.path.join(bundle_path, "bundle-definition.json")
    temp = f"{target}.{uuid.uuid4().hex}.tmp"
    with open(temp, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=True, sort_keys=True)
    os.replace(temp, target)


def seed_default_bundle_definitions(model_dir: str) -> list[str]:
    """
    Write bundled recipe JSON for any default bundle that does not already have
    bundle-definition.json on disk. Never overwrites an existing definition.
    Returns the bundle ids that were seeded this call.
    """
    if not model_dir:
        return []

    seeded: list[str] = []
    for seed in DEFAULT_BUNDLE_DEFINITION_SEEDS:
        bundle_id = str(seed["bundleId"])
        target = bundle_definition_path(model_dir, bundle_id)
        if os.path.isfile(target):
            continue
        bundle_path = os.path.dirname(target)
        _write_bundle_definition_atomic(bundle_path, seed)
        seeded.append(bundle_id)

    return seeded
