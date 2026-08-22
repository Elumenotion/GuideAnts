# GuideAnts v0.9.18

**Muse Glimmer support** — curated install for Muse Glimmer 30B (vision + 131k context + DFlash companion weights), plus notebook folder UX, richer PDF/OCR sandboxes, and connection/routing fixes. AI / support images republished to GHCR for this cut.

## Get started

1. Install [Docker Desktop](https://www.docker.com/products/docker-desktop/) (Windows/macOS) or Docker Engine 24+ with Compose (Linux). Windows needs WSL2.
2. Download **`guideants-installer-v0.9.18.zip`** from this release.
3. Unzip and run:
   - **Windows:** double-click `guideants.cmd`
   - **Linux / macOS:** `chmod +x guideants.sh && ./guideants.sh`
4. Choose database layout, AI backend, and optional services when prompted.
5. Open **http://localhost:5107/** — first account becomes Admin.

Existing installs: relaunch the installer; if `:main` moved past your pins, accept the update prompt to pick up this channel. Volumes are kept.

## Highlights

### Muse Glimmer
- **Muse Glimmer 30B** (`unsloth/Muse-Glimmer-30B-GGUF`) in the curated llama catalog: vision mmproj, 131k context, Meta-recommended sampling defaults, and reasoning tiers via system-prompt directive
- **Companion artifacts:** optional HF files beyond model + mmproj (e.g. DFlash `dflash-kquant.gguf`), with install progress and DB provenance
- **Chat behavior lives on the catalog entry:** curated installs write sampling/reasoning defaults straight to the model row; Runtime Profiles are retired
- AI images rebuilt against a newer llama.cpp (needed for the `muse-glimmer` arch); ROCm ASR/TTS backend env corrected

### Notebook & samples
- Copy path for files and folders; multi-select folders (Shift/Ctrl) with bulk delete
- Sample guide for Notion MCP

### Script sandboxes
- Apt/pip additions for PDF and OCR workflows: `tesseract-ocr`, `ghostscript`, `poppler-utils`, `ocrmypdf`, `pypdf`, `openpyxl`, plus dependency pin hotfixes

### Fixes
- Microsoft Foundry Connections no longer overwrite the stored API key when editing only the Resource field
- Unmatched SPA routes show a NotFound page (auto-home after 30s) instead of a blank screen

### Images
- AI / support images republished to GHCR (`:main`, `:latest`, and `:v0.9.18`) for this cut

## Notes

- Apple Silicon: prefer the **slim** AI backend (`linux/amd64` under emulation).
- Glimmer needs the republished AI images from this release (newer llama.cpp).
- Operator details: `docs/release-runbook.md`, `installer/README.md`, `deploy/azure/README.md`.
