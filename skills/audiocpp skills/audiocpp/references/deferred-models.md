# Deferred / non-catalog model recipes

GuideAnts' native-ai-migration inventory (GuideAnts repo,
`docs/native-ai-migration/INVENTORY.md`) lists TTS families that are **released in
audio.cpp** but excluded from the GuideAnts catalog because of packaging/UI gaps —
not because the engine can't run them. Each recipe below names the blocker and how
a sandbox script compensates. Confidence varies; treat every recipe as an
experiment and verify against the audio.cpp README / `model_manager.py` (in the
user's fork) when something doesn't match.

General pattern for all of them (Route 3):

```bash
python3 .../fetch_model.py <repo> --dest <dir> [--include <prefix>]   # repeat per repo if composite
# any conversion step the family needs (below)
python3 .../spawn_engine.py start --path <dir> --family <family> --task tts
python3 .../spawn_engine.py status        # until ready
python3 .../engine_tool.py speech "..." --engine-url http://127.0.0.1:18099 --model <id> [voice flags] -o Output/out.wav
python3 .../spawn_engine.py stop
```

---

## qwen3_tts_1_7b_custom_voice — easiest, start here

- **Repo:** `Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice` (single plain snapshot, not gated)
- **Family/task:** `qwen3_tts` / `tts`
- **GuideAnts blocker:** builtin speaker ids (`Vivian`, `Ryan`, …) live in the model
  config, not in `/v1/audio/voices`, so the GuideAnts voice picker would be empty.
  A skill doesn't need a picker — it needs the list.
- **Recipe:** download, spawn, then find the speaker list: try
  `/v1/audio/voices?model=...` first; if empty, grep the downloaded `config.json` /
  `generation_config.json` for a speaker/talker list and pass one as `voice`.
  Known-good ids from GuideAnts' notes: `Vivian`, `Ryan` (more in the config).
- **Why it's the best first experiment:** identical family/layout to catalog models
  GuideAnts already runs, so the only new variables are the download and the
  private engine itself.

## pocket_tts

- **Repo:** gated on HF — requires `HF_TOKEN` with accepted terms (set via guide
  Environment variables UI).
- **Family/task:** `pocket_tts` / `tts`, `load_options: {"language": "english"}`
- **GuideAnts blocker:** gated repo + snapshot layout: required files live under
  `languages/english/`, and a naive full-repo verify fails.
- **Recipe:** `fetch_model.py <repo> --include languages/english/ --dest .../pocket_tts`,
  then point `--path` at the directory the engine expects (check the audio.cpp
  README for whether it wants the repo root or the language subdir — try root first).

## miotts_1_7b

- **Repos:** composite — MioTTS-1.7B **plus** MioCodec **plus** a WavLM checkpoint.
  Exact repo ids: check audio.cpp `model_manager.py` (the GuideAnts inventory
  confirms three sources; it does not record the ids).
- **Family/task:** `miotts` / `tts`
- **GuideAnts blocker:** its downloader only fetches `sourceRepos[0]`.
- **Recipe:** three `fetch_model.py` calls into the sub-layout `model_manager.py`
  prescribes, then spawn. Expect iteration on directory layout — the engine's
  load error names the missing file, use that as the guide.

## voxcpm2

- **Family/task:** `voxcpm2` / `tts`
- **GuideAnts blocker:** post-download conversion `audiovae.pth` →
  `audiovae.safetensors` is not implemented in the wrapper.
- **Recipe:** after download, convert in the sandbox (needs `torch` +
  `safetensors` importable — the probe's python-packages check tells you):

```python
import torch, safetensors.torch
state = torch.load("audiovae.pth", map_location="cpu", weights_only=True)
safetensors.torch.save_file(state, "audiovae.safetensors")
```

  If the checkpoint nests the state dict (`state["state_dict"]` etc.), unwrap it
  first. Then spawn as usual.

## vibevoice_1_5b / vibevoice_7b — multi-speaker dialogue

- **Family/task:** `vibevoice` / `tts`
- **GuideAnts blockers:** (a) the HF repos lack the Qwen2.5 tokenizer files the
  loader needs (audio.cpp's model_manager copies them in from a bundle — fetch the
  tokenizer files from the matching `Qwen/Qwen2.5-*` repo into the model dir);
  (b) its primary API is multi-speaker `voice_samples`, which the GuideAnts
  single-voice contract can't express.
- **Recipe:** download model repo + tokenizer files, spawn, then experiment with
  the speech request: the multi-speaker field shape (`voice_samples`) is not
  documented in GuideAnts — check the audio.cpp server README for the request
  schema before assuming. This is the flagship "scenario GuideAnts cannot do"
  demo (multi-voice dialogue in one WAV) if it works.

## vevo2 — voice conversion (speech-to-speech)

- **Family:** `vevo2`, multi-route (synthesis and conversion tasks)
- **GuideAnts blockers:** composite layout + a `whisper_stats` conversion + routes
  beyond the single synth contract.
- **Recipe:** hardest of the set; consult audio.cpp `docs/vevo2.md` for layout,
  task token for the conversion route, and request fields. Attempt only after a
  simpler Route 3 model has already worked in this deployment.

---

## Beyond TTS: sortformer diarization — verified in the container binary

- **Repo:** `nvidia/diar_sortformer_4spk-v1` (single plain snapshot; audio.cpp
  release-0.2 `tools/model_manager.py` requires `config.json`, `model.safetensors`,
  `processor_config.json`)
- **Family/task:** `sortformer_diar` / `diar`, offline mode only, up to 4 speakers
- **GuideAnts blocker:** diarization is a whole task class outside the ASR/TTS
  wrapper contracts (the archived catalog design explicitly scoped it out); the
  local provider never had it — only the Azure cloud provider does.
- **Verified:** the cuda13 container binary contains the `sortformer_diar` loader
  and the `/v1/tasks/run` route (probe reports both under `binaryFeatures`).
- **Recipe:** download, spawn on the private port, then run `diarize.py` — it
  converts the input to the required 16 kHz mono WAV (the raw engine does not
  resample), fetches `speaker_turns`, merges them, and (unless `--turns-only`)
  labels each turn with text via the wrapper ASR engine:

```bash
python3 .../fetch_model.py nvidia/diar_sortformer_4spk-v1 \
  --dest /models-local/asr/diar_sortformer_4spk-v1 --exclude diar_sortformer_4spk-v1.nemo
python3 .../spawn_engine.py start --path /models-local/asr/diar_sortformer_4spk-v1 \
  --family sortformer_diar --task diar
python3 .../diarize.py Output/uploads/meeting.mp3 -o Output/meeting
python3 .../spawn_engine.py stop
```

- The `--exclude` skips the repo's redundant 493 MB `.nemo` bundle; the engine
  loads `model.safetensors` (~494 MB). The model is small enough to run next to
  the wrappers' loaded models without an unload (verified on an 8 GB GPU with
  Qwen3-ASR-0.6B loaded).
- **E2E-verified 2026-07-15** in the cuda13 container: fetch → spawn (ready in
  seconds) → `diarize.py` on a 19 s two-voice clip → 2 speakers, 3 turns, all
  ASR-labeled, correct timestamps.
- Tuning: re-spawn with `--option speaker_threshold=0.4` etc. (session options —
  see engine-api.md); `diarize.py --merge-gap-seconds/--min-turn-seconds` shape the
  output without a re-spawn.
- The binary also ships `silero_vad` / `marblenet_vad` loaders (`--task vad`,
  `speech_segments` output), but silero's model files live in the audio.cpp repo's
  `assets/framework/models/silero_vad` — not on HF and not in the container image —
  so VAD needs those assets fetched from the audio.cpp GitHub repo first. Untested.

## Not possible with the container binary (don't burn time)

| Family | Why |
|---|---|
| `kokoro_tts` | Loader not present in audio.cpp release builds — not compiled into the container binary |
| `parakeet_tdt` | Downloadable, but loader commented out in release `registry.cpp` |
| `seed_vc` | Voice-conversion-only family GuideAnts never wired; loader status unverified — treat as Route 4 material |
| `moss_tts`, `heartmula`, … | Downloader-only packages upstream (no released loader) |

These become possible only via **Route 4** (the user's host-native fork compiled
with the loader enabled).

## ASR side (Route 2)

The ASR wrapper loads bare directories under `/models-local/asr` without a catalog
entry, assuming family `qwen3_asr`. That makes other qwen3_asr-family snapshots
sideloadable through the *wrapper itself* (no private engine needed) — but
non-qwen3 ASR families would need `GA_ASR_ENGINE_FAMILY` set at service start
(compose change → out of skill scope) or a private engine with `--task asr`.
