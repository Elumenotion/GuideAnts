# Parameters (tested 2026-09-10)

Locked profile used by `diagram_style.py` (do not override unless the user
asks, and then re-test before accepting):

| Parameter | Value |
|-----------|-------|
| workflow | `qwen-image-edit-bf16-v1` |
| steps | 4 |
| cfg | 4.0 |
| denoise | 0.8 |
| shift | 3.1 |
| megapixels | 2.0 |
| lora_strength | 0.5 |
| seed | **random per run** (the variation knob) |
| prompt | "Add subtle styling to the provided diagram to make it more visually appealing and professional and include a lemon-chiffon gradient background. Preserve all original layout and do not add new elements" |
| negative | "extra elements, text" |

## The seed is the knob

With denoise=0.8 the restyle is a near-identity to the input chrome. That is
the accepted behavior: Qwen polishes, it does not redesign. Variation between
runs comes from the seed. Same seed = byte-identical output (the workflow is
deterministic), so **every new submission must use a fresh random seed** or
you pay GPU time for the same image again. `submit` does this by default;
pass `--seed N` only to reproduce a specific run.

## Denoise quantization (why denoise is NOT a smooth knob)

`comfy/samplers.py` -> `KSampler.set_steps` (ComfyUI, verified against the
source at `comfy/samplers.py:1431`):

```python
if denoise is None or denoise > 0.9999:
    self.sigmas = self.calculate_sigmas(steps).to(self.device)
else:
    ...
    new_steps = int(steps/denoise)
    sigmas = self.calculate_sigmas(new_steps).to(self.device)
    self.sigmas = sigmas[-(steps + 1):]
```

For steps=4 the denoise value only changes the schedule when `int(4/denoise)`
changes. With steps=4:

| denoise | int(4/d) | schedule actually used |
|---------|----------|------------------------|
| 0.75, 0.80 | 5 | 5-step schedule trimmed to lower 5 (starts below max sigma) |
| 0.81 ... 1.0 | 4 (or full branch) | full 4-step schedule (starts at max sigma) |

Consequences (all observed live, 2026-09-10):

- denoise 0.8 -> restyle nearly identical to the input chrome (input survives
  the lower starting sigma). Accepted as the restyle profile.
- denoise 0.81, 0.825, 1.0 -> full regeneration, and 0.825 vs 1.0 with the
  same seed are **byte-identical** (max pixel delta = 0), because they produce
  the identical sigma schedule. That is arithmetic, not a fluke.
- The breakpoint moves with steps: steps=20 puts it at 0.95/0.90/0.85/...,
  steps=4 puts it at 0.80. Do not tune denoise as a slider on this profile.

## Other knobs (what they actually do - sourced)

- **cfg** - prompt steering strength. 4.0 was preferred over 1.0 in review.
- **megapixels** - the adapter rescales the OUTPUT to this many MP
  (`ImageScaleToTotalPixels` in the workflow). 2.0 MP from a 1350x1166 input
  gives 1552x1344. The overlay step MUST read the actual restyle dimensions -
  never assume the output size matches the input.
- **lora_strength** - Lightning 4-step LoRA strength. 0.5 with denoise 0.8
  produced accepted results.
- **shift** - flow-matching timestep shift (`ModelSamplingAuraFlow`): "raise
  the shift and the sampler spends more of its steps in the high-noise region
  where broad composition gets decided; lower it and more steps go to the
  fine-detail end" (comfy.icu ModelSamplingAuraFlow). 3.1 is the Qwen
  family's working value; leave it alone.

## Job failure modes (observed)

- `state=failed, error="ComfyUI request failed: timed out"` can happen even
  after sampling reaches 4/4 (died at SaveImage). It is usually transient
  (cold model / contention): **resubmit with a fresh seed** and it succeeds.
- First job after a host reboot carries the cold BF16 load: budget ~10-15
  minutes extra on the first poll cycle; later jobs run in 1-3 minutes.
