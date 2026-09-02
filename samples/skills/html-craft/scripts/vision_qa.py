#!/usr/bin/env python3
"""vision_qa.py - read-only visual QA of a rendered asset via the Wire API.

Uses the OpenAI SDK against OPENAI_API_KEY / OPENAI_BASE_URL (the `guide`
model alias accepts image inputs). The prompt is wrapped in a fixed
read-only boilerplate: numbered checks, no fix proposals, capped bullet
count, small max_tokens.

WHY the boilerplate exists: a vague "critique this" prompt sent the Wire
thread into long, unrequested remediation work and the call ran until the
platform killed it at its 30-minute timeout. Bounded + read-only = fast.

Usage:
  python3 vision_qa.py IMAGE --ask "1) Is all text legible? 2) Any clipping?" \
      [--label "INTAKE frame: paper sheet over hopper"] \
      [--model guide] [--max-tokens 250] [--timeout 240] [--max-bullets 6]

Prints one JSON object:
  {"ok": true,  "model", "review": "...", "usage": {...}}
  {"ok": false, "error": "..."}
Exit 0 when ok, 1 otherwise. Stdlib + sandbox-venv openai only.
"""
from __future__ import annotations

import argparse
import base64
import json
import mimetypes
import os
import sys

BOILERPLATE = (
    "You are a read-only visual QA reviewer for one rendered UI asset.\n"
    "STRICT RULES - follow exactly:\n"
    "1. Review only. Do NOT propose, plan, generate, or apply any fixes, edits, rewrites, or alternatives.\n"
    "2. Answer ONLY the numbered checks provided, in order - nothing else.\n"
    "3. One bullet per check, exactly this format: 'PASS: <check>' or 'FAIL: <check> - <one line of observed evidence>'.\n"
    "4. At most {max_bullets} bullets total. No preamble, no praise, no summary, no closing remarks.\n"
    "5. If you cannot see the relevant part of the image, answer FAIL with that as the evidence.\n"
    "6. Stop writing immediately after the last bullet.\n"
)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("image", help="screenshot/asset to review")
    ap.add_argument("--ask", required=True,
                    help='numbered checks, e.g. "1) Is all text legible? 2) Any clipping?"')
    ap.add_argument("--label", default="", help="one line describing what the image is")
    ap.add_argument("--model", default=os.environ.get("HTML_CRAFT_VISION_MODEL", "guide"))
    ap.add_argument("--max-tokens", type=int, default=250)
    ap.add_argument("--timeout", type=float, default=240.0, help="client timeout seconds (no retries)")
    ap.add_argument("--max-bullets", type=int, default=6)
    args = ap.parse_args()

    if not (os.environ.get("OPENAI_API_KEY") and os.environ.get("OPENAI_BASE_URL")):
        print(json.dumps({"ok": False, "error": "OPENAI_API_KEY / OPENAI_BASE_URL not set in environment"}))
        return 1
    try:
        import openai
    except ImportError:
        print(json.dumps({"ok": False, "error": "openai SDK not importable in sandbox venv"}))
        return 1

    path = os.path.abspath(args.image)
    if not os.path.exists(path):
        print(json.dumps({"ok": False, "error": f"file not found: {path}"}))
        return 1
    mime = mimetypes.guess_type(path)[0] or "image/png"
    b64 = base64.b64encode(open(path, "rb").read()).decode()

    prompt = BOILERPLATE.format(max_bullets=args.max_bullets)
    prompt += f"\nAsset under review: {args.label or os.path.basename(path)}\n\nChecks:\n{args.ask}"

    client = openai.OpenAI(timeout=args.timeout, max_retries=0)
    try:
        r = client.chat.completions.create(
            model=args.model,
            messages=[{"role": "user", "content": [
                {"type": "text", "text": prompt},
                {"type": "image_url", "image_url": {"url": f"data:{mime};base64,{b64}"}},
            ]}],
            max_tokens=args.max_tokens,
        )
    except Exception as e:
        print(json.dumps({"ok": False,
                          "error": f"{type(e).__name__}: {str(e)[:300]}",
                          "hint": "timeout? make the checks more specific and rerun once with smaller --max-tokens"}))
        return 1

    usage = None
    if getattr(r, "usage", None) is not None:
        usage = r.usage.model_dump()
    print(json.dumps({"ok": True, "model": args.model,
                      "review": r.choices[0].message.content, "usage": usage}, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
