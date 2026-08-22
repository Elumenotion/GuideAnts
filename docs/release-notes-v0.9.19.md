# GuideAnts v0.9.19

**Qwen 3.8 27B** — curated install for Unsloth’s dense 27B vision model (262k context, hybrid thinking, in-GGUF draft-mtp), plus catalog-edit recovery when llama.cpp rejects a preset, clearer ReadWeb errors, and CWD-relative copy paths. AI / support images republished to GHCR for this cut.

## Get started

1. Install [Docker Desktop](https://www.docker.com/products/docker-desktop/) (Windows/macOS) or Docker Engine 24+ with Compose (Linux). Windows needs WSL2.
2. Download **`guideants-installer-v0.9.19.zip`** from this release.
3. Unzip and run:
   - **Windows:** double-click `guideants.cmd`
   - **Linux / macOS:** `chmod +x guideants.sh && ./guideants.sh`
4. Choose database layout, AI backend, and optional services when prompted.
5. Open **http://localhost:5107/** — first account becomes Admin.

Existing installs: relaunch the installer; if `:main` moved past your pins, accept the update prompt to pick up this channel. Volumes are kept.

## Highlights

### Qwen 3.8 27B
- **Qwen 3.8 27B** (`unsloth/Qwen3.8-27B-GGUF`) in the curated llama catalog: vision mmproj, 262k native context, in-GGUF draft-mtp, and hybrid thinking with `reasoning_effort` (default **xhigh**)
- Sampling defaults on the catalog row (temperature 1.0 / top_p 0.95 / top_k 20); Unsloth 4-bit typical **17–19GB** RAM+VRAM (`UD-Q4_K_XL`)
- Needs the republished AI images from this release (older llama.cpp crashes on `draft-mtp` / `reasoning-preserve`)

### Local model catalog
- Saving a router preset no longer 502s after llama-server rejects unknown keys; unrecognized options are stripped on respawn so the editor can recover
- `no-mmproj` is rewritten to `mmproj-auto=false`

### ReadWeb & notebook
- ReadWeb failures report the actual status (401 / 403 / 404 / 429 / 5xx / timeout) instead of a generic error
- GitHub hosts are not auto-excluded; excluded-host blocks are honored before fetch
- Copy-path and attachment paths are CWD-relative so pasted paths work in chat tools

### Images
- AI / support images republished to GHCR (`:main`, `:latest`, and `:v0.9.19`) for this cut

## Notes

- Apple Silicon: prefer the **slim** AI backend (`linux/amd64` under emulation).
- Qwen 3.8 27B needs the republished AI images from this release (newer llama.cpp).
- Operator details: `docs/release-runbook.md`, `installer/README.md`, `deploy/azure/README.md`.
