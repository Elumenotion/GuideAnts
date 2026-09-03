#!/usr/bin/env python3
"""Stop WanVideoModelLoader from dumping resident weights before every load.

ComfyUI-WanVideoWrapper calls unload_all_models + soft_empty_cache at the start
of loadmodel. That is a low-VRAM ritual: it throws out InfiniteTalk, then
safetensors restages the 14B weights in CPU RAM before HIP copy. On the 8060S
those HIP pages are host RAM, so the host shows a double fill.

This host is configured for unified high memory. Do not unload to make room.
"""
from __future__ import annotations

from pathlib import Path

LOADER = Path("/opt/ComfyUI/custom_nodes/ComfyUI-WanVideoWrapper/nodes_model_loading.py")
NEEDLE = (
    "        transformer = None\n"
    "        mm.unload_all_models()\n"
    "        mm.cleanup_models()\n"
    "        mm.soft_empty_cache()\n"
)
REPLACEMENT = (
    "        transformer = None\n"
    "        # GUIDEANTS_SKIP_WAN_PRELOAD_UNLOAD: keep resident weights.\n"
)
MARKER = "GUIDEANTS_SKIP_WAN_PRELOAD_UNLOAD"


def main() -> int:
    if not LOADER.is_file():
        raise SystemExit(f"patch-wan-high-memory: missing {LOADER}")
    text = LOADER.read_text(encoding="utf-8")
    if MARKER in text:
        print(f"patch-wan-high-memory: already applied ({LOADER})", flush=True)
        return 0
    if NEEDLE not in text:
        raise SystemExit(
            f"patch-wan-high-memory: unload block not found in {LOADER}; "
            "WanVideoWrapper pin changed"
        )
    LOADER.write_text(text.replace(NEEDLE, REPLACEMENT, 1), encoding="utf-8")
    print(f"patch-wan-high-memory: removed preload unload in {LOADER}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
