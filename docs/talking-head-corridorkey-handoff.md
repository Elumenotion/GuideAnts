# Talking-head / CorridorKey handoff

Date: 2026-08-15

## Read this first

The work described here is in this repository:

```text
D:\repos\GuideAnts-infinitetalk-comfyui
```

The Cursor chat that produced this work was attached to a different workspace:

```text
D:\repos\GuideAnts
```

That workspace mismatch is why the work was not visible in the Cursor file tree.
Open `D:\repos\GuideAnts-infinitetalk-comfyui` as the active folder before
continuing.

No commit was created. The working tree contains many uncommitted changes,
including pre-existing GuideAnts changes unrelated to this pipeline. Do not
reset, clean, or discard the tree without first separating the intended changes.

## User goals

The intended product is a talking-head video:

1. Start with `doug-on-green.png`.
2. Generate lip-synced motion from a WAV clip.
3. Generate at the source size needed by InfiniteTalk, currently 416x256.
4. Replace the green screen with `office-plate.png`.
5. Deliver a 1280x720 MP4 with the original audio duration.

The visual requirements are important:

- Preserve ears, hair, clothing edges, and other fine details.
- Do not produce green halos, black borders, jagged binary edges, or missing ears.
- The foreground must not look visibly softer than the background.
- The result should look like a professionally keyed talking head, not a
  low-resolution cutout pasted over a sharp plate.

The operational requirement is equally important:

- Every long-running stage must expose live progress and timing.
- Generation must show the ComfyUI job ID, node, step, elapsed time, and state.
- CorridorKey must show frame progress, throughput, elapsed time, and ETA.
- The composite/encode stage must show the same kind of progress.
- Telemetry must remain visible in the terminal and be written to the pipeline
  log. Do not replace the observable pipeline with a direct silent ComfyUI
  submission.

## Why this work was needed

The original generated video was H.264 `yuv420p` at 416x256. Chroma
subsampling discarded the color resolution needed for a clean green-screen
edge. Repeatedly adjusting a simple `colorkey` or HSV key could not recover
information that had already been discarded.

The current approach keeps a full-chroma, lossless intermediate and uses
CorridorKey to predict a continuous matte and unmixed foreground. The final
delivery encode is allowed to be normal H.264 `yuv420p`; the keying happens
before that final delivery compression.

The remaining softness issue is a resolution/MTF mismatch: the foreground is
generated at 416x256 and enlarged roughly 3x, while the office plate is already
sharp at 1280x720.

## Current pipeline

```text
avatar PNG + WAV
        |
        v
InfiniteTalk / ComfyUI
  416x256, audio-duration-derived frame count
  FFV1 + 16-bit RGB intermediate
        |
        v
CorridorKey
  deterministic coarse HSV alpha hint
  foreground + matte inference
        |
        v
linear-light composite
  optional background blur
  optional foreground-only sharpening
  premultiplied alpha
        |
        v
1280x720 FFV1 full-chroma composite master MKV
        |
        v
1280x720 H.264/AAC yuv420p delivery MP4
```

The normal orchestration entry point is:

```text
scripts/run-talking-head-pipeline.ps1
```

The keyer/compositor is:

```text
scripts/run-corridorkey-composite.py
```

The CorridorKey installer and pin verification are:

```text
scripts/install-corridorkey.ps1
```

## Completed implementation

### Lossless InfiniteTalk intermediate

Both workflows now use the VideoHelperSuite FFV1 format:

```text
docker/build/comfyui-video/workflows/infinitetalk-i2v-v1.json
docker/build/comfyui-video/workflows/infinitetalk-i2v-v1-rocm.json
```

The relevant output settings are:

- `format: video/ffv1-mkv`
- `pix_fmt: rgba64le`
- FFV1 level 3
- intra-frame GOP (`gop_size: 1`)
- audio remains connected and trimmed to the audio

The workflow revision was incremented in:

```text
docker/build/comfyui-video/source-lock.json
```

The generated source is confirmed as:

- FFV1
- `gbrap16le`
- 416x256
- FLAC audio
- 30.000 seconds for the current 30-second clip

### Adapter and client contract

Talking-head intermediate filenames now use `.mkv` rather than `.mp4` in the
adapter/client validation and tests. Relevant files include:

```text
docker/build/comfyui-video/adapter/guideants_video_adapter/core.py
docker/build/comfyui-video/client/guideants_video_client/client.py
docker/build/comfyui-video/adapter/guideants_video_adapter/tests/
docker/build/comfyui-video/client/guideants_video_client/tests/
```

The model-generation job still means “generate the green-screen talking-head
source.” CorridorKey compositing remains outside the adapter so that job
semantics are not silently changed to mean “final office composite.”

### CorridorKey pinning

The checkout is here:

```text
artifacts/tools/CorridorKey
```

Pinned source revision:

```text
97e55a453060745bead1befd293f6e523c4b845c
```

Pinned green model:

```text
CorridorKey_v1.0.safetensors
```

Model revision:

```text
f6386ddf042d8e92aeb5fd16cb9b101cff508195
```

Model SHA-256:

```text
74d614f7d92fc559a118c30a7deadedc3cacd8ef83dcb85a030d0bed7af8b20b
```

`run-corridorkey-composite.py` refuses to run if either the source checkout or
the model checksum is wrong.

### CorridorKey processing

The script creates a temporary CorridorKey clip with:

```text
Input/       decoded 8-bit PNG frames
AlphaHint/   deterministic coarse HSV/chroma subject hints
```

CorridorKey produces `FG/*.exr` and `Matte/*.exr`. The compositor then:

1. Upscales the straight sRGB foreground.
2. Upscales the linear matte.
3. Applies optional foreground-only unsharp masking.
4. Converts the foreground to linear light.
5. Premultiplies it by the matte.
6. Blurs the background plate in linear light when configured.
7. Composites foreground over background.
8. Converts to sRGB and writes both a lossless FFV1 full-chroma master and
   an H.264/AAC `yuv420p` delivery encode.

The old `overlay-green-talking-head.py` path is no longer used.

### Current hybrid settings

The latest test used:

```text
background blur sigma:       1.5 px
foreground sharpen amount:   0.15
foreground sharpen sigma:    0.8 px
```

These are exposed by `run-talking-head-pipeline.ps1`:

```text
-BackgroundBlurSigma
-ForegroundSharpenAmount
-ForegroundSharpenSigma
```

The script also supports reusing an existing generated source:

```text
-SkipGenerate
-SourceVideoPath <path>
```

Each composite pass writes both:

```text
<OutputStem>-master-720p.mkv
<OutputStem>-overlay-720p.mp4
```

The MKV is the lossless full-chroma master. The MP4 remains the broadly
compatible H.264/AAC delivery file.

## Current verified artifacts

### 30-second source

```text
artifacts/infinitetalk/doug-office-30s-green-416x256.mkv
```

Technical properties:

- FFV1
- 16-bit full-chroma RGB (`gbrap16le`)
- 416x256
- FLAC audio
- 30.000 seconds
- 143,152,065 bytes

### Current hybrid delivery

```text
artifacts/infinitetalk/doug-office-30s-hybrid-overlay-720p.mp4
```

Technical properties:

- H.264
- AAC
- `yuv420p`
- 1280x720
- 750 frames
- 30.000 seconds
- 3,019,219 bytes

SHA-256:

```text
94F5803B0E463C83C67949749E95A1FBEFFA9962590A4D1AB7DB644566E92750
```

Comparison proof:

```text
artifacts/infinitetalk/doug-office-30s-hybrid-comparison.png
```

Midpoint proof frame:

```text
artifacts/infinitetalk/doug-office-30s-hybrid-overlay-720p-mid.png
```

The first 30-second CorridorKey result, before the hybrid treatment, is:

```text
artifacts/infinitetalk/doug-office-30s-overlay-720p.mp4
```

## Telemetry and timing

The observable 30-second hybrid run reported:

```text
prepare:             4.2s
CorridorKey:       344.3s, 750/750 frames, approximately 2.18 fps
composite/encode:   76.2s, 750/750 frames
total:              423.2s
```

Generation telemetry from the earlier normal run showed:

- job ID: `7c8552acb49249798c85a68a00a15154`
- node-level execution
- WanVideo sampling through `154/154`
- final video readiness at `801/801`

The current scripts now:

- print timestamped elapsed seconds;
- report frame count, rate, and ETA for CorridorKey;
- report frame count, rate, and ETA for compositing;
- stream the compositor output through `Tee-Object` into the pipeline log;
- calculate ETAs from the current stage rather than from the whole run;
- use the source video for validation audio duration when
  `-SkipGenerate -SourceVideoPath` is used.

The pipeline log naming pattern is:

```text
artifacts/infinitetalk/<OutputStem>-pipeline.log
```

The latest hybrid run happened just before the final stage-local ETA and log
tee fixes were applied. The next run will have both fixes.

## How to continue in this worktree

Open a PowerShell terminal at:

```powershell
Set-Location D:\repos\GuideAnts-infinitetalk-comfyui
```

Verify the pinned CorridorKey installation:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File scripts/install-corridorkey.ps1
```

Re-run only the post-processing pass against the existing 30-second source:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File scripts/run-talking-head-pipeline.ps1 `
  -SkipGenerate `
  -SourceVideoPath artifacts/infinitetalk/doug-office-30s-green-416x256.mkv `
  -OutputStem doug-office-30s-next `
  -CorridorKeyDevice cuda:1 `
  -BackgroundBlurSigma 1.5 `
  -ForegroundSharpenAmount 0.15 `
  -ForegroundSharpenSigma 0.8
```

To generate a new clip through the normal observable adapter path, omit
`-SkipGenerate` and provide the desired WAV:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File scripts/run-talking-head-pipeline.ps1 `
  -AudioPath tests/runtime/content-files/acceptance-project/authorized-notebook/Input/may_5_cover_30s.wav `
  -OutputStem doug-office-30s-new `
  -CorridorKeyDevice cuda:1
```

Do not bypass the adapter with an ad hoc direct ComfyUI submission when
observability matters. The normal script polls the adapter and exposes node and
step telemetry.

## Validation already performed

The adapter/client regression tests passed:

```text
30 passed
```

The test command used an isolated `uv` environment with the required test
dependencies:

```powershell
$env:PYTHONPATH = `
  ((Resolve-Path 'docker/build/comfyui-video/adapter').Path + ';' + `
   (Resolve-Path 'docker/build/comfyui-video/client').Path)

uv run --with pytest `
  --with 'fastapi>=0.116.1,<1' `
  --with 'python-multipart>=0.0.20,<1' `
  --with 'uvicorn>=0.35.0,<1' `
  --with 'websocket-client>=1.8.0,<2' `
  --with httpx2 `
  python -m pytest `
  docker/build/comfyui-video/adapter/guideants_video_adapter/tests `
  docker/build/comfyui-video/client/guideants_video_client/tests -q
```

The latest hybrid scripts also passed:

- Python bytecode compilation
- PowerShell parsing
- `git diff --check`
- IDE lint diagnostics
- final `ffprobe` validation

## Known caveats and follow-up work

1. **Foreground detail is still fundamentally limited by 416x256 generation.**
   The hybrid pass improves the sharpness relationship, but it cannot recover
   detail that InfiniteTalk never generated. The next quality experiment should
   generate at 832x480 or another larger supported source size, then compare
   cost and quality against the current hybrid pass.

2. **The adapter result MIME metadata still says `video/mp4` in
   `adapter/guideants_video_adapter/app.py`, and capabilities still report
   `video/mp4`, while the talking-head intermediate is now MKV.** The bytes are
   correct and the current client materializes them by extension, but this
   metadata should be made internally consistent before treating the API
   contract as finished.

3. **A targeted test for the new blur/sharpen compositor settings should be
   added.** Existing adapter/client tests cover the MKV contract and workflow
   plumbing; the latest image-processing changes were validated by real
   30-second execution, syntax checks, lint checks, and `ffprobe`.

4. **The current working tree is not clean.** Several modified files and
   untracked test/scripts files predate or surround this handoff. Review
   `git status --short` and separate this feature before committing.

5. **Container policy.** The video container was restarted once with explicit
   user approval to load the MKV adapter contract. Post-processing-only reruns
   do not require a container restart. A future adapter/workflow change may
   require one, but ask first.

## Immediate recommendation

First inspect:

```text
artifacts/infinitetalk/doug-office-30s-hybrid-comparison.png
```

If the blur/sharpen balance is acceptable, keep the hybrid settings as the
baseline. If the foreground still looks too soft, the next meaningful test is
not more keyer tuning; it is a higher-resolution InfiniteTalk generation,
followed by the same CorridorKey and linear-compositing path.
