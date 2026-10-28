#!/usr/bin/env python3
"""qa_inpaint.py - text-content QA for ONE inpaint output (single roll).

Implements the qwen-image-inpaint SKILL.md "Text-content QA" protocol:
crop mask region -> blind VLM glyph enumeration -> canonicalized
comparison in code -> one JSON log line.

WHY the shape is strict (production incidents - read before changing):
- Asking the VLM for a verdict ("does it say X?") auto-corrects: the
  `guide` model enumerated a missing letter and still answered "Yes -
  exactly Fundamentals!". So: blind enumeration only, comparison in code.
- Building the expected glyph list from the raw target string (spaces
  included) makes a multi-word comparison that can never pass; every
  verdict in the run is then void. So: canonical form on BOTH sides.
- A vague VLM prompt can burn the sandbox call's client-side platform
  timeout (see html-craft/scripts/vision_qa.py). So: fixed short prompt,
  temperature=0, max_tokens=120, explicit 240s HTTP timeout.

One image per invocation. Do NOT wrap this in a batch loop inside a single
sandbox call - one roll per sandbox call (see SKILL.md).

Usage:
  python3 qa_inpaint.py "Fundamentals!" IMAGE --mask MASK \\
      [--seed N] [--job JOBID] [--bbox x0,y0,x1,y1] \\
      [--model guide] [--max-tokens 120] [--timeout 240]
  python3 qa_inpaint.py --self-test     # validate harness vs qa_fixtures/

Exit codes:
  0  PASS     parsed glyphs == expected glyphs
  1  REJECT   parsed glyphs != expected (valid evidence against the roll)
  2  QA-ERROR empty/unparseable VLM response or API/IO failure
             (NOT evidence against the render - re-run once, then judge
             the crop visually; see SKILL.md protocol step 4)

Stdout: one JSON object:
  {"target":..., "image":..., "seed":..., "job":...,
   "raw":..., "parsed":[...], "expected":[...],
   "verdict":"PASS"|"REJECT"|"QA-ERROR", "note":"..."}

Requires OPENAI_API_KEY / OPENAI_BASE_URL (the `guide` model alias
accepts image inputs). Stdlib + sandbox-venv openai only.
"""
from __future__ import annotations

import argparse
import base64
import json
import os
import sys

# Characters the model may legitimately enumerate as written glyphs.
# Everything else in the target (spaces, backticks, prompt formatting) is
# dropped when building the expected list.
_WRITABLE_PUNCT = set("!.?:;,+\"%#&@*()[]{}<>/-")

_ENUM_PROMPT = (
    "A short message is hand-written in marker on this whiteboard. "
    "List its glyphs in writing order, separated by hyphens. "
    "Include ! as a glyph if written. Do not correct spelling. "
    "Output only the hyphen-separated list, one line."
)

# Labels the model sometimes prepends even when told to output only the list.
_LABEL_PREFIXES = ("TRANSCRIPT:", "transcript:", "Answer:", "answer:",
                   "Glyphs:", "glyphs:", "List:", "list:")


def canonical_expected(target: str) -> list[str]:
    """Target string -> expected glyph list (no spaces, no format chars)."""
    return [ch for ch in target
            if ch.isalnum() or ch in _WRITABLE_PUNCT]


def canonical_parsed(raw: str) -> list[str]:
    """Model enumeration -> glyph list; [] means UNPARSEABLE (=> QA-ERROR).

    Dash variants normalized, labels/ticks stripped. If the model ignores
    the format (prose sentence, run-on text), a single over-long token
    results - that is a parse failure, never a reject: an empty list makes
    the caller emit QA-ERROR.
    """
    text = raw.strip().replace("\u2013", "-").replace("\u2014", "-")
    for pref in _LABEL_PREFIXES:
        if text.startswith(pref):
            text = text[len(pref):]
    text = text.strip().strip("`").strip()
    if not text:
        return []
    glyphs = [g.strip() for g in text.split("-") if g.strip()]
    # Fallback: model used spaces instead of hyphens -> single hyphen-less
    # token containing whitespace is a space-separated list.
    if len(glyphs) == 1 and " " in glyphs[0]:
        glyphs = glyphs[0].split()
    # Sanity: hand-written glyphs are short. Any over-long token means the
    # model returned prose, not a list -> unparseable.
    if any(len(g) > 3 for g in glyphs):
        return []
    return glyphs


def _crop_for_qa(image_path: str, mask_path: str | None,
                 bbox: tuple[int, int, int, int] | None) -> tuple:
    from PIL import Image
    im = Image.open(image_path).convert("RGB")
    if bbox is None and mask_path:
        m = Image.open(mask_path).convert("L")
        if m.size != im.size:
            m = m.resize(im.size)
        import numpy as np
        ys, xs = np.where(np.array(m) > 128)
        if xs.size:
            bbox = (int(xs.min()), int(ys.min()), int(xs.max()) + 1,
                    int(ys.max()) + 1)
    if bbox is not None:
        crop = im.crop(bbox)
    else:
        crop = im
        bbox = (0, 0, im.width, im.height)
    crop = crop.resize((crop.width * 2, crop.height * 2), Image.LANCZOS)
    return crop, bbox


def qa(target: str, image: str, mask: str | None = None,
       bbox: str | None = None, model: str = "guide",
       max_tokens: int = 120, timeout: float = 240.0) -> dict:
    result = {"target": target, "image": image, "seed": None, "job": None,
              "raw": None, "parsed": [], "expected": canonical_expected(target),
              "verdict": "QA-ERROR", "note": ""}
    try:
        b = None
        if bbox:
            b = tuple(int(v) for v in bbox.split(","))
        crop, _ = _crop_for_qa(image, mask, b)
        buf = __import__("io").BytesIO()
        crop.save(buf, format="PNG")
        b64 = base64.b64encode(buf.getvalue()).decode()

        from openai import OpenAI
        client = OpenAI(timeout=timeout)
        v = client.chat.completions.create(
            model=model,
            messages=[{"role": "user", "content": [
                {"type": "text", "text": _ENUM_PROMPT},
                {"type": "image_url",
                 "image_url": {"url": f"data:image/png;base64,{b64}"}},
            ]}],
            temperature=0.0,
            max_tokens=max_tokens,
        )
        raw = (v.choices[0].message.content or "").strip()
        result["raw"] = raw
        parsed = canonical_parsed(raw)
        result["parsed"] = parsed
        if not parsed:
            result["note"] = "VLM returned no parsable glyphs"
            return result
        if parsed == result["expected"]:
            result["verdict"] = "PASS"
        else:
            result["verdict"] = "REJECT"
            result["note"] = (
                f"length {len(parsed)} vs {len(result['expected'])}; "
                f"first diff at index "
                + next((str(i) for i, (a, e) in
                        enumerate(zip(parsed, result["expected"])) if a != e),
                       "n/a (length only)" if len(parsed) != len(result["expected"])
                       else "none"))
    except Exception as exc:  # API/IO failure = QA-ERROR, not a reject
        result["note"] = f"{type(exc).__name__}: {exc}"
    return result


def _self_test() -> int:
    fixdir = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                          "qa_fixtures")
    good = os.path.join(fixdir, "known_good.png")
    bad = os.path.join(fixdir, "known_bad_missing_n.png")
    if not (os.path.isfile(good) and os.path.isfile(bad)):
        print(json.dumps({"verdict": "QA-ERROR",
                          "note": f"fixtures missing: {fixdir}"}))
        return 2
    target = "Fundamentals!"
    vg = qa(target, good)
    vb = qa(target, bad)
    ok = vg["verdict"] == "PASS" and vb["verdict"] == "REJECT"
    print(json.dumps({"self_test": ok,
                      "known_good": vg["verdict"],
                      "known_bad": vb["verdict"],
                      "note": "" if ok else
                      "harness broken - no roll verdicts are valid"}))
    return 0 if ok else 2


def main() -> int:
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("target", nargs="?",
                    help="exact text that must be written (e.g. Fundamentals!)")
    ap.add_argument("image", nargs="?", help="inpaint output PNG to QA")
    ap.add_argument("--mask", help="mask PNG (white = editable); crop = its bbox")
    ap.add_argument("--bbox", help="x0,y0,x1,y1 crop override (image px)")
    ap.add_argument("--seed", help="roll seed (recorded in the log line)")
    ap.add_argument("--job", help="inpaint job ID (recorded in the log line)")
    ap.add_argument("--model", default="guide")
    ap.add_argument("--max-tokens", type=int, default=120)
    ap.add_argument("--timeout", type=float, default=240.0)
    ap.add_argument("--self-test", action="store_true",
                    help="run the harness against scripts/qa_fixtures/ and exit")
    args = ap.parse_args()

    if args.self_test:
        return _self_test()
    if not args.target or not args.image:
        ap.error("target and image are required (or use --self-test)")

    r = qa(args.target, args.image, args.mask, args.bbox,
           args.model, args.max_tokens, args.timeout)
    if args.seed:
        r["seed"] = int(args.seed)
    if args.job:
        r["job"] = args.job
    print(json.dumps(r))
    return {"PASS": 0, "REJECT": 1, "QA-ERROR": 2}[r["verdict"]]


if __name__ == "__main__":
    sys.exit(main())
