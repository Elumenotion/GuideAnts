---
name: styled-plantuml
description: "Create PlantUML diagrams in the GuideAnts logo theme (via the Diagrams assistant) and style them for video production via the BF16 Qwen-Image-Edit GPU gateway: chrome restyle + crisp label re-composite. Two distinct steps with a user review between them."
metadata:
  guideants:
    enabled: true
    display_order: 44
    requires_toolsets: [sandbox]
---

# Styled PlantUML (create, then style)

Paths — fixed layout, do not probe or re-derive. The sandbox CWD is the
notebook's **output directory**. This skill's scripts live under
`Skills/styled-plantuml/scripts/` relative to it. Write deliverables with
**bare filenames** (e.g. `-o diagram.styled.png`); never prefix with
`Output/`.

This skill has **two distinct steps**. They are never combined in one go:

1. **Create** — the user gets a themed SVG to review. Step 2 does not start
   until the user has seen and accepted the diagram.
2. **Style** — a workflow that restyles the chrome (image model) and
   re-composites the labels (ffmpeg) into a video-ready PNG.

## GPU host gateway (env-configured)

The scripts read `QWEN_IMAGE_SKILL_BASE_URL` and `QWEN_IMAGE_SKILL_TOKEN` from
the guide Environment automatically — do **not** hardcode or export them
inline.

Verify before running:

```bash
printenv QWEN_IMAGE_SKILL_BASE_URL >/dev/null && printenv QWEN_IMAGE_SKILL_TOKEN >/dev/null && echo "env ok" || echo "env missing"
```

If either is missing, stop and ask the user to set it in the guide's
Environment variables. Never scan the LAN or guess the GPU host's IP.

## Preflight

```bash
python3 Skills/styled-plantuml/scripts/preflight.py
```

Requires `image_edit_bf16_ready` plus local `cairosvg` (prepare) and
`ffmpeg` (overlay). Quote the evidence if blocked.

# Step 1 — Create the diagram (user reviews)

The Diagrams assistant is a crew member who understands PlantUML. Do not run
PlantUML yourself. **Describe the diagram in detail and let it create it.**

The instruction you hand to the Diagrams assistant MUST contain the complete
theme block below, verbatim — every hex code, the skinparam line, and the
hierarchy rules. Do not summarize it ("warm brand colors", "the logo
theme", "red and gold" are all failures: the assistant will improvise its
own palette and the diagram is rejected). If you catch yourself writing a
theme description that is not the verbatim block, stop and paste it.

## The theme (verbatim block — paste into the Diagrams instruction)

```
Theme: GuideAnts logo theme. Apply exactly these colors, no others:
- All borders/outline color: #CC0000 (deep red)
- All text/font color: #990000 (dark red)
- Actors, key components, and final output elements: background #FFD700 (gold)
- Standard components/rectangles/classes: background #FFF8DC (cream)
- Packages/group containers: background #FFE4B5 (peach)
- All arrows/connectors: #FF8C00 (orange), thickness 2
- Arrow labels: #990000, small font

Visual hierarchy: gold elements stand out (actors, key nodes, the final
output); cream is the readable default for most components; peach groups
logical layers; deep-red borders and dark-red text give structure; orange
arrows carry the flow.

skinparam: ArrowColor #FF8C00, ArrowThickness 2, BorderColor #CC0000,
FontColor #990000, PackageBackgroundColor #FFE4B5, PackageBorderColor
#CC0000, RectangleBackgroundColor #FFF8DC, RectangleBorderColor #CC0000,
NoteBackgroundColor #FFD700, NoteBorderColor #CC0000, NoteFontColor #990000.
```

## How to run Step 1

1. Work out what the diagram must show (type, layout, elements, flow) from
   the user's request and the material at hand. Keep labels to a few words.
   Keep the layout compact — roughly 2–3:1 (stack the groups vertically,
   flow horizontally inside each); a long banner compresses to unreadable
   labels in Step 2.
2. Write the description: the structure of the diagram — element names and
   types (actor / component / package / class), which elements group into
   which packages, and the arrows between them with short labels — **plus
   the verbatim theme block above**. Do **not** assign colors per element in
   the description: the theme block's hierarchy (gold for actors / key /
   final output, cream for standard components, peach for packages) does all
   the coloring. If an element must stand out, call it a "key node" or
   "final output" (a role) — never write a hex code next to it. A worked
   example of a description that passed review is in
   `references/logo-theme.md`.
3. Hand the description to the Diagrams assistant; it writes
   `<name>.svg` (and the `.puml`).
4. **Show the SVG to the user. Stop.** That is all of Step 1. Do nothing
   else after generation: no checks, no greps, no re-generation, no "fixing"
   the diagram, no styling. The user reviews the diagram and gives the gate.
   The user's gate is the only thing that moves to Step 2. If the user asks
   for changes (wrong colors included), that is a new instruction — correct
   the description and let the Diagrams assistant re-generate, then show the
   user again.

# Step 2 — Style workflow (BF16, only on a user-accepted SVG)

Locked sampler profile — see `references/parameters.md` for the reasoning
(including why denoise is a quantized, not smooth, knob):

| Parameter | Value |
|-----------|-------|
| workflow | `qwen-image-edit-bf16-v1` |
| steps | 4 |
| cfg | 4.0 |
| denoise | 0.8 |
| shift | 3.1 |
| megapixels | 2.0 |
| lora_strength | 0.5 |
| seed | **fresh random every submission** |

Do not pass sampler overrides. The prompt and negative prompt are fixed in
`diagram_style.py` (subtle restyle + lemon-chiffon background; negative
"extra elements, text").

## The job protocol (mandatory — read before the first submit)

- **`submit` is the only synchronous call.** It uploads the chrome PNG,
  returns the job id, and writes a sentinel `<stem>.chrome-<scale>x.job.json`
  before it returns.
- **`poll` fetches status exactly once per call and returns to you.**
  Its exit code is the state: **0 = completed, 1 = failed/cancelled,
  3 = still running** (check the printed JSON either way).
- **NEVER hold a sandbox call open in a loop for the job's lifetime.** A
  blocking poll loop is always an error: it holds the session for minutes,
  the user can only respond by cancelling, and a cancellation then leaves
  the job state unknown. The job is durable on the gateway — the sentinel
  holds the job id, so any later call can resume.
- Poll cadence: first poll after ~2–5 min; after a host reboot the first
  job carries the cold BF16 load (~10–15 min) — say so up front instead of
  burning the first polls.

## Run it

All intermediates sit next to the source SVG (bare stem + suffixes):
`<stem>.chrome.svg`, `<stem>.text.svg`, `<stem>.chrome-2x.png`
(opaque), `<stem>.text-2x.png` (alpha), `<stem>.chrome-2x.job.json`
(sentinel), `<stem>.chrome-2x.restyled.png`, `<stem>.styled.png`
(deliverable).

```bash
# 1. split + rasterize (fast, local). Fails loudly if the SVG has 0 <text>
#    nodes — labels would be lost; do not continue in that case.
python3 Skills/styled-plantuml/scripts/diagram_style.py prepare diagram.svg

# 2. submit the chrome PNG (one call; writes the sentinel; random seed)
python3 Skills/styled-plantuml/scripts/diagram_style.py submit   diagram.chrome-2x.png

# 3. poll — SEPARATE short calls, until exit code 0 (completed) or 1
python3 Skills/styled-plantuml/scripts/diagram_style.py poll   diagram.chrome-2x.job.json --sleep 45

# 4. download the restyle (chrome only, no labels; smaller than the input)
python3 Skills/styled-plantuml/scripts/diagram_style.py result   diagram.chrome-2x.job.json -o diagram.chrome-2x.restyled.png

# 5. composite the crisp labels on top (scales the text layer to the
#    ACTUAL restyle size — the adapter rescales to megapixels)
python3 Skills/styled-plantuml/scripts/diagram_style.py overlay   diagram.chrome-2x.restyled.png diagram.text-2x.png -o diagram.styled.png
```

Show the user the final PNG (and the bare restyle if they want to compare).
If the user rejects it, resubmit with a fresh random seed (the default) —
**the seed is the variation knob**. Same seed = byte-identical output.

## Failure handling (observed)

- `poll` exits 1 with `error="ComfyUI request failed: timed out"`: this
  happens even after sampling reaches 4/4 (dies at SaveImage); it is
  usually transient. **Resubmit** (fresh seed) and poll again.
- Empty result or 0-byte file: do not composite; resubmit.

## Reporting

Name the deliverable with its notebook-tree path. Report the seed and job
id of the accepted run so it can be reproduced. When blocked, quote the
preflight evidence.
