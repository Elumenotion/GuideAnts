# `guideants-ai` — PyTorch Dependency Report

Last updated: 2026-07-02

Audit scope: **`guideants-ai` image definitions**, **requirements.txt files used by that
image build**, and the **running `guideants-ai` container**. Goal: identify every package
that depends on `torch` (direct and transitive).

Evidence from repo sources and container `guideants-ai`
(`ghcr.io/elumenotion/guideants-ai-cuda13:main`, healthy at time of audit).

---

## 1. AI image definitions

`docker/build/guideants-ai/` only.

| Dockerfile | Torch install | Wheel index | Pins |
|---|---|---|---|
| `Dockerfile.cuda` | **Yes** — `pydeps-cuda13-builder` | `https://download.pytorch.org/whl/cu130` | `torch==2.11.0`, `torchaudio==2.11.0`, `torchvision==0.26.0` |
| `Dockerfile.cpu` | **Yes** — `pydeps-cpu-builder` | `https://download.pytorch.org/whl/cpu` | same pins |
| `Dockerfile.rocm` | **Yes** — `pydeps-rocm-builder` | CPU index | same pins |
| `Dockerfile.vulkan` | **Yes** — `pydeps-vulkan-builder` | CPU index | same pins |
| `Dockerfile.slim` | **No** | — | No ASR/TTS/emb |

Install order in full variants:

1. `pip install torch torchaudio torchvision` (backend-specific index)
2. `pip install -r asr-requirements.txt`
3. `pip install -r tts-requirements.txt`
4. `pip install accelerate==1.12.0 transformers==4.57.6 tokenizers==0.22.2`
5. `pip install -r emb-requirements.txt`
6. `pip install -r requirements.txt` (filtered sandbox copy)

Build scripts strip `torch`, `torchaudio`, `torchvision`, `torchtext` from the copied sandbox
requirements before the Docker build:

- `docker/build/build_guideants_ai.sh`
- `docker/build/build_guideants_ai.ps1`
- `.github/workflows/publish-guideants-ai-images.yml`

---

## 2. Requirements.txt files

### 2.1 Service requirements (`docker/build/guideants-ai/`)

| File | Packages that pull torch |
|---|---|
| `asr-requirements.txt` | `qwen-asr` → `accelerate` (`torch>=2.0.0`) |
| `tts-requirements.txt` | `kokoro` (direct `torch`); `accelerate` (`torch>=2.0.0`) |
| `emb-requirements.txt` | `sentence-transformers` (`torch>=1.11.0`) |

Dockerfile also installs (not in the three files above):

- `accelerate==1.12.0`
- `transformers==4.57.6`
- `tokenizers==0.22.2`

### 2.2 Sandbox requirements (copied into `guideants-ai` build)

Source per backend (`build_guideants_ai.sh` / `.ps1`):

| Backend | File |
|---|---|
| cpu | `docker/build/Sandboxes/python311TorchCPU/requirements.txt` |
| cuda13 | `docker/build/Sandboxes/python311TorchCUDA/requirements.txt` |
| rocm | `docker/build/Sandboxes/python311TorchROCM/requirements.txt` |
| vulkan | `docker/build/Sandboxes/python311TorchVulkan/requirements.txt` |
| slim | `docker/build/Sandboxes/python311Slim/requirements.txt` (no torch entries) |

Torch entries in the four `python311Torch*` files:

```
torch==2.11.0          # line 1 — stripped at build; re-installed by Dockerfile
torchaudio==2.11.0     # line 2 — stripped at build; re-installed by Dockerfile
...
torchtext              # line 183 — stripped at build; not re-installed
```

No other torch-requiring packages appear in these sandbox lists.

---

## 3. Running `guideants-ai` container (verified)

Container: `guideants-ai`  
Image: `ghcr.io/elumenotion/guideants-ai-cuda13:main`  
Python venv: `/opt/venv`

### 3.1 Direct torch packages installed

| Package | Version |
|---|---|
| `torch` | 2.11.0+cu130 |
| `torchaudio` | 2.11.0+cu130 |
| `torchvision` | 0.26.0+cu130 |

`torchtext` is in sandbox requirements but **not installed** in the running container.

### 3.2 Packages that require `torch` (pip dependency)

From `pipdeptree -r -p torch`:

| Package | Version | Requires |
|---|---|---|
| `accelerate` | 1.12.0 | `torch>=2.0.0` |
| `kokoro` | 0.9.4 | `torch` |
| `sentence-transformers` | 5.1.1 | `torch>=1.11.0` |
| `curated-transformers` | 0.1.1 | `torch>=1.12.0` |
| `spacy-curated-transformers` | 0.3.1 | `torch>=1.12.0` |
| `torchvision` | 0.26.0+cu130 | `torch==2.11.0` |

### 3.3 Top-level service packages

| Package | Version | Service |
|---|---|---|
| `qwen-asr` | 0.0.6 | ASR (`/asr/`) |
| `kokoro` | 0.9.4 | TTS (`/tts/`) |
| `misaki` | 0.9.4 | TTS — G2P for kokoro |
| `sentence-transformers` | 5.1.1 | Embeddings (`/emb/`) |

### 3.4 Full reverse dependency tree

```
torch==2.11.0+cu130
├── accelerate==1.12.0 [requires: torch>=2.0.0]
│   └── qwen-asr==0.0.6
├── kokoro==0.9.4 [requires: torch]
├── sentence-transformers==5.1.1 [requires: torch>=1.11.0]
├── curated-transformers==0.1.1 [requires: torch>=1.12.0]
│   └── spacy-curated-transformers==0.3.1
│       └── misaki==0.9.4 [extra: en]
│           └── kokoro==0.9.4
├── spacy-curated-transformers==0.3.1 [requires: torch>=1.12.0]
│   └── misaki==0.9.4 [extra: en]
│       └── kokoro==0.9.4
└── torchvision==0.26.0+cu130 [requires: torch==2.11.0]
```

---

## 4. Reproduction commands

```bash
docker exec guideants-ai /opt/venv/bin/pip show torch torchaudio torchvision
docker exec guideants-ai /opt/venv/bin/pipdeptree -r -p torch
```

---

## 5. Summary

**Direct:** `torch`, `torchaudio`, `torchvision` (full variants only; slim has none).

**Pip dependency on `torch`:** `accelerate`, `kokoro`, `sentence-transformers`,
`curated-transformers`, `spacy-curated-transformers`, `torchvision`.

**Service entry points:** `qwen-asr` (ASR), `kokoro` + `misaki` (TTS), `sentence-transformers`
(embeddings).
