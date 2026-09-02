# Default parameters

Adapter defaults for InfiniteTalk i2v (`DEFAULT_PARAMETERS` / skill CLI overrides).
Skills must use these tested profiles. Do not invent sampler settings.

## i2v (`infinitetalk-i2v-v1`)

Skill CLI defaults (delivery-oriented 416×256):

| Parameter | Default | Notes |
|-----------|---------|-------|
| width | 416 | multiples of 8 preferred |
| height | 256 | |
| steps | 4 | LightX2V distill LoRA (InfiniteTalk README: 4 steps) |
| cfg | 1 | |
| fps | 25 | |
| seed | -1 | CLI resolves to random int before submit; adapter accepts `0..2^63-1` only |

Adapter absolute defaults if a parameter is omitted server-side may differ
(`width=832 height=480 cfg=5`); prefer the skill CLI defaults above for notebook runs.

## Seed policy

- `-1` (CLI default) → pick a concrete random int, then submit that value.
- Always log `seed=` on submit and on quiet progress lines.
- Write `run-meta.json` beside the output with `seed` and `jobId`.

## Inputs

| Field | Multipart name | Required |
|-------|----------------|----------|
| Avatar image | `source` | yes |
| Audio (wav preferred) | `audio` | yes |
| Background plate | `background` | yes |

Output must be `.mp4` (delivery composite).
