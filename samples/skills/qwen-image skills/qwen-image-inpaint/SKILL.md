---
name: qwen-image-inpaint
description: "BF16 inpaint via GPU host ComfyUI-video adapter (qwen-image-edit-bf16-inpaint-v1). Use when the user has source + mask PNGs and a fill prompt — masked completion, whiteboard erase, etc."
metadata:
  guideants:
    enabled: true
    display_order: 43
    requires_toolsets: [sandbox]
---

# Qwen Image inpaint (BF16)

Paths — fixed layout, do not probe or re-derive. The sandbox CWD is the
notebook's **output directory**. This skill's scripts live under
`Skills/qwen-image-inpaint/scripts/` relative to it. Write deliverables with
**bare filenames** (e.g. `-o inpaint.png`); never prefix with `Output/`.

Source + mask + prompt → PNG. Mask: **white = editable**, **black = preserve**.

Workflow (required): `qwen-image-edit-bf16-inpaint-v1`

## GPU host gateway (env-configured)

The scripts read `QWEN_IMAGE_SKILL_BASE_URL` and `QWEN_IMAGE_SKILL_TOKEN` from the
guide Environment automatically — do **not** hardcode or export them inline.

Verify before running:

```bash
printenv QWEN_IMAGE_SKILL_BASE_URL >/dev/null && printenv QWEN_IMAGE_SKILL_TOKEN >/dev/null && echo "env ok" || echo "env missing"
```

If either is missing, stop and ask the user to set it in the guide's Environment
variables. Never scan the LAN or guess the GPU host's IP.


## Sampler profile (locked — tested Lightning)

Always use the CLI defaults. Do **not** pass `--steps`, `--cfg`, or `--lora-strength`.
`image_tool.py` forces:

| Parameter | Value |
|-----------|-------|
| steps | 4 |
| cfg | 1 |
| lora_strength | 1 |
| denoise | 1 |
| shift | 3.1 |
| megapixels | 1.6 |

This is the whiteboard / background profile that passed AC-I1. Never invent `steps=20`.

## Preflight

```bash
python3 Skills/qwen-image-inpaint/scripts/preflight.py --for inpaint-bf16
```

Requires `image_edit_bf16_inpaint_ready`.

## Inpaint

```bash
python3 Skills/qwen-image-inpaint/scripts/image_tool.py inpaint \
  uploads/source.png uploads/mask.png "prompt…" -o inpaint.png
```

In ComfyUI: Workflows → `guideants/` (templates) or `guideants-jobs/` (exact submitted graph for this job).

## Seeds (required for rerolls)

`image_tool.py inpaint` takes `--seed` (default `0`). The workflow is
**deterministic**: same source + mask + prompt + seed = byte-identical output.

- First submission: the default is fine.
- Every reroll (reject, re-roll, variation): pass a fresh random `--seed`
  (e.g. `int.from_bytes(os.urandom(4), "big")`). Without it you pay for the
  same image again.
- Fingerprint of a duplicate: two "different" runs producing files of
  identical byte size — compare content hashes to confirm.

## Text-content QA (required when the prompt writes text)

Diffusion silently drops or mangles letters, and the result still looks
plausible. A file existing is not evidence the word is right — verify the
content before presenting or compositing.

One roll at a time. Do **not** write a batch script that rolls + QA's N
images in one call: a comparator bug then poisons every verdict at once and
the per-roll eyeball check that would catch it is gone. If the user wants a
batch, run the loop with one roll per sandbox call.

Why not ask the VLM "does it say X?": in production the `guide` model
enumerated the glyphs it actually saw (one letter missing) and then, in the
same response, answered "Yes — it spells exactly 'Fundamentals!'" with a
confident letter count. The **verdict** auto-corrects; the **enumeration**
is the evidence. Never let the model make the comparison.

### Canonical form (the #1 comparator bug — read before coding)

The expected glyph list is **built from the target string by removing
every space** (and any character that cannot be a written glyph) — the model's
enumeration contains **no space glyphs**, and the example lists in this skill
contain none. A single-word target like "Fundamentals!" is safe either way
(13 glyphs); a multi-word target is not: building the expected list from the
raw string adds one phantom glyph per space and the comparison can **never
pass** — every verdict in the run is then void, including any that
"rejected" a perfect render. Both sides must be in the same canonical form:
model output en-dash/em-dash → `-`, strip labels (`TRANSCRIPT:` etc.) and
code ticks, split on `-`, drop empties.

### Protocol

Use `scripts/qa_inpaint.py` — it implements this protocol and is calibrated
against `scripts/qa_fixtures/` (run `--self-test` before the first roll of a
session; exit 0/1/2 = PASS/REJECT/QA-ERROR, one JSON log line on stdout):

```bash
python3 Skills/qwen-image-inpaint/scripts/qa_inpaint.py \
  "Fundamentals!" inpaint.png --mask uploads/mask.png \
  --seed <roll-seed> --job <jobId>
```

QA-ERROR (exit 2) is a parse/API failure — never evidence against the
render: re-run once, then judge the crop visually. If you must implement the
protocol by hand (no sandbox script available), follow these steps:

1. Crop the mask region (white bounding box of the mask) from the output;
   upscale ~2x for stroke detail.
2. One bounded call to `model="guide"` via the OpenAI SDK
   (`OPENAI_API_KEY` / `OPENAI_BASE_URL`; same pattern as
   `html-craft/scripts/vision_qa.py`):
   - Ask only for a blind enumeration: "List the glyphs in writing order,
     hyphen-separated. Include `!` if written. Do not correct spelling.
     Output only the list."
   - Do **not** put the target word in the prompt. Do **not** ask for a
     verdict. `temperature=0`, `max_tokens` ~120.
   - Keep the call short: sandbox calls have a client-side platform timeout
     (cancelable by the user); a vague prompt can burn the whole budget
     (see the timeout note in `html-craft/scripts/vision_qa.py`).
3. **Compare in code, not the model**, after canonicalizing both sides. The
   length check catches a single dropped letter instantly.
4. **A parse failure is not a reject.** If the raw response is empty or
   unparseable, that is a QA error: do not discard the roll, re-run the QA
   call once (fresh), and if it still fails, judge the crop visually before
   deciding anything.
5. On mismatch: reject — do not present, do not composite, do not report
   the roll — re-roll with a fresh random seed and repeat.
6. On match: proceed. Report the passing roll's seed + job ID and the count
   of rejected rolls.

### Harness failure — stop and audit, don't keep rolling

A **100% reject rate with a constant glyph-count offset** (e.g. every
enumeration is exactly 2 shorter than expected) is evidence about the
comparator, not the renders — audit the expected list and canonicalization
before spending another GPU job.

Never run the QA uncalibrated: `scripts/qa_fixtures/` ships a known-good
crop (`known_good.png` → must PASS) and a known-bad crop
(`known_bad_missing_n.png`, one `n` dropped → must FAIL). Run the
implementation against both fixtures **first**; if either verdict is wrong,
the harness is broken and no subsequent roll verdicts are valid.

### Per-roll log

Append one line per roll so a bad harness is diagnosable at a glance:

```json
{"seed": 123, "job": "abc...", "raw": "F-u-n-d-a-m-e-t-a-l-s-!",
 "parsed": ["F","u","n","d","a","m","e","t","a","l","s","!"],
 "expected": ["F","u","n","d","a","m","e","n","t","a","l","s","!"],
 "verdict": "REJECT", "note": ""}
```

## Job control

Same as edit skill (`status`, `cancel`, `result` on `image_tool.py`). Status includes
`comfy_workflow_file` when published.

## Reporting

Mask is required. Quote preflight evidence when blocked.
