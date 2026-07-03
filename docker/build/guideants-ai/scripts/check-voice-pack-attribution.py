#!/usr/bin/env python3
"""Build-time voice-pack attribution completeness gate (D10 §5.4)."""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ALLOWED_DATASETS = {
    "common_voice",
    "globe",
    "vctk",
    "libritts_r",
    # Common Voice moved dataset downloads behind Mozilla Data Collective (email-gated,
    # manual per-request agreement) in Oct 2025 and can no longer be fetched anonymously.
    # librivox_pd = Internet Archive / LibriVox recordings, US Public Domain Mark 1.0 (a
    # strictly broader grant than CC0-1.0). See voice-pack/NOTICE.md "Sourcing note".
    "librivox_pd",
    # kokoro_synthetic = reference clips synthesized locally with the Kokoro-82M
    # model (Apache-2.0). These are model-generated, not recordings of a real
    # speaker, and carry no third-party recording copyright (CC0-1.0). See
    # voice-pack/NOTICE.md and each entry's sourceClipRef (synthetic:kokoro-82m:...).
    "kokoro_synthetic",
}
ALLOWED_LICENCES = {"CC0-1.0", "CC-BY-4.0"}
VOICE_ID_RE = re.compile(r"^[a-z0-9_]{3,64}$")


def fail(message: str) -> None:
    print(f"VOICE-PACK ATTRIBUTION CHECK FAILED: {message}", file=sys.stderr)
    sys.exit(1)


def load_manifest(pack_root: Path) -> dict:
    manifest_path = pack_root / "manifest.json"
    if not manifest_path.is_file():
        fail(f"missing manifest.json at {manifest_path}")
    try:
        return json.loads(manifest_path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        fail(f"manifest.json is not valid JSON: {exc}")


def validate_manifest(pack_root: Path, manifest: dict) -> None:
    voices = manifest.get("voices")
    if not isinstance(voices, list) or not voices:
        fail("manifest.json must contain a non-empty voices array")

    seen_ids: set[str] = set()
    for entry in voices:
        if not isinstance(entry, dict):
            fail("each voice entry must be an object")

        voice_id = entry.get("voiceId")
        if not isinstance(voice_id, str) or not VOICE_ID_RE.fullmatch(voice_id):
            fail(f"invalid voiceId: {voice_id!r}")
        if voice_id in seen_ids:
            fail(f"duplicate voiceId: {voice_id}")
        seen_ids.add(voice_id)

        for field in ("language", "sourceDataset", "sourceClipRef", "licence", "clipPath", "sampleRate", "durationSec"):
            if field not in entry:
                fail(f"voice {voice_id} is missing required field {field}")

        dataset = entry["sourceDataset"]
        if dataset not in ALLOWED_DATASETS:
            fail(f"voice {voice_id} has unsupported sourceDataset: {dataset}")

        licence = entry["licence"]
        if licence not in ALLOWED_LICENCES:
            fail(f"voice {voice_id} has unsupported licence: {licence}")

        modified = entry.get("modified")
        if not isinstance(modified, dict) or modified.get("changed") is not True:
            fail(f"voice {voice_id} must declare modified.changed=true")

        clip_rel = entry["clipPath"]
        clip_path = pack_root / clip_rel
        if not clip_path.is_file():
            fail(f"voice {voice_id} clipPath does not exist: {clip_rel}")

        if licence != "CC0-1.0":
            attribution = entry.get("attribution")
            if not isinstance(attribution, dict):
                fail(f"voice {voice_id} ({licence}) requires attribution object")
            for field in ("creator", "sourceUrl", "licenceUrl", "modificationNote"):
                value = attribution.get(field)
                if not isinstance(value, str) or not value.strip():
                    fail(f"voice {voice_id} attribution.{field} is required for {licence}")


def validate_notice(pack_root: Path) -> None:
    notice_path = pack_root / "NOTICE.md"
    if not notice_path.is_file():
        fail(f"missing NOTICE.md at {notice_path}")
    text = notice_path.read_text(encoding="utf-8")
    if "GuideAnts voice pack" not in text:
        fail("NOTICE.md must identify the GuideAnts voice pack")


def main() -> None:
    if len(sys.argv) != 2:
        fail("usage: check-voice-pack-attribution.py <voice-pack-root>")
    pack_root = Path(sys.argv[1]).resolve()
    if not pack_root.is_dir():
        fail(f"voice pack directory not found: {pack_root}")
    manifest = load_manifest(pack_root)
    validate_manifest(pack_root, manifest)
    validate_notice(pack_root)
    print(f"voice-pack attribution check passed ({len(manifest['voices'])} voices)")


if __name__ == "__main__":
    main()
