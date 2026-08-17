#!/usr/bin/env python3
"""Drop high-volume ComfyUI stdout; keep errors and warnings."""

from __future__ import annotations

import re
import sys

_DROP = (
    re.compile(r"^\s*\d+%\|"),  # tqdm progress bars
    re.compile(r"\[32m\[INFO\]"),  # model load chatter
    re.compile(r"\[INFO\] Requested to load"),
    re.compile(r"\[INFO\] loaded completely"),
    re.compile(r"\[INFO\] Found quantization"),
    re.compile(r"\[INFO\] Using MixedPrecisionOps"),
    re.compile(r"\[INFO\] CLIP/text encoder model load device"),
)


def _keep(line: str) -> bool:
    stripped = line.strip()
    if not stripped:
        return False
    return not any(pattern.search(line) for pattern in _DROP)


def main() -> int:
    for line in sys.stdin:
        if _keep(line):
            sys.stdout.write(line)
            sys.stdout.flush()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
