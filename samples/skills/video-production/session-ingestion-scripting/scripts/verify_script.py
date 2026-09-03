#!/usr/bin/env python3
"""Verify a TTS script file against the editor brief's audit requirements.

Checks that the spoken block (between the TTS-READY markers) does not contain
any user-removed phrase and does contain every story beat the user expects.
Prints one JSON verdict (preflight shape):

  {"scenario": "tts-script-verify", "open": bool, "blockers": [...],
   "warnings": [...], "evidence": {...}}

Usage:
  python3 verify_script.py <script.md> \\
      --removed "chatbot is a lie" --removed "forgive my scattered brain" \\
      --beat "wire api" --beat "first line of defense"
"""
from __future__ import annotations

import argparse
import json
import re
import sys

MARK_RE = re.compile(r"^#\s*TTS-READY\s*$(.*?)^#\s*END\s+TTS-READY\s*$", re.M | re.S)


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("script")
    ap.add_argument("--removed", action="append", default=[],
                    help="phrase that must NOT be in the spoken block (repeatable)")
    ap.add_argument("--beat", action="append", default=[],
                    help="story beat that MUST be in the spoken block (repeatable)")
    ap.add_argument("--min-words", type=int, default=50,
                    help="warn below this spoken word count")
    args = ap.parse_args()

    blockers: list[str] = []
    warnings: list[str] = []
    try:
        doc = open(args.script, encoding="utf-8").read()
    except OSError as exc:
        print(json.dumps({"scenario": "tts-script-verify", "open": False,
                          "blockers": [f"cannot read {args.script}: {exc}"],
                          "warnings": [], "evidence": {}}))
        sys.exit(1)

    m = MARK_RE.search(doc)
    if not m:
        print(json.dumps({
            "scenario": "tts-script-verify", "open": False,
            "blockers": ["TTS-READY / END TTS-READY markers missing — no extractable spoken block"],
            "warnings": [], "evidence": {},
        }, indent=2))
        sys.exit(1)

    spoken = m.group(1).strip()
    spoken_lower = spoken.lower()
    words = len(spoken.split())

    removed_found = [p for p in args.removed if p.lower() in spoken_lower]
    beats_missing = [b for b in args.beat if b.lower() not in spoken_lower]

    for p in removed_found:
        blockers.append(f"removed phrase still in spoken block: {p!r}")
    for b in beats_missing:
        blockers.append(f"story beat missing from spoken block: {b!r}")
    if words < args.min_words:
        warnings.append(f"spoken word count {words} < {args.min_words}")
    for section in ("Removed", "Kept beats", "Corrections applied"):
        if not re.search(rf"^#+\s*.*{re.escape(section)}", doc, re.M):
            warnings.append(f"audit section {section!r} not found in document")

    evidence = {
        "spoken_words": words,
        "spoken_chars": len(spoken),
        "removed_checked": len(args.removed),
        "removed_absent": [p for p in args.removed if p.lower() not in spoken_lower],
        "beats_checked": len(args.beat),
        "beats_present": [b for b in args.beat if b.lower() in spoken_lower],
    }

    open_ok = not blockers
    print(json.dumps({
        "scenario": "tts-script-verify",
        "open": open_ok,
        "blockers": blockers,
        "warnings": warnings,
        "evidence": evidence,
    }, indent=2))
    sys.exit(0 if open_ok else 1)


if __name__ == "__main__":
    main()
