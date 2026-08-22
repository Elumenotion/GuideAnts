# GuideAnts v0.9.15

Patch release: faster, safer skill/MCP sandbox venv apply, OpenRouter/Hugging Face thinking controls, setup-wizard API key fixes, and Azure deploy venv migration cleanup. AI / support images republished to GHCR for this cut.

## Get started

1. Install [Docker Desktop](https://www.docker.com/products/docker-desktop/) (Windows/macOS) or Docker Engine 24+ with Compose (Linux). Windows needs WSL2.
2. Download **`guideants-installer-v0.9.15.zip`** from this release.
3. Unzip and run:
   - **Windows:** double-click `guideants.cmd`
   - **Linux / macOS:** `chmod +x guideants.sh && ./guideants.sh`
4. Choose database layout, AI backend, and optional services when prompted.
5. Open **http://localhost:5107/** — first account becomes Admin.

Existing installs: relaunch the installer; if `:main` moved past your pins, accept the update prompt to pick up this channel. Volumes are kept.

## Highlights

### Script execution / sandboxes
- **Surgical scoped apply:** global/startup reconcile is apt-only; scoped apply updates one guide venv (`pip` + install scripts) instead of walking every scope on startup
- **Durable scoped venvs:** packages persist across restarts; repeat apply skips when already satisfied
- **Pip reconcile fixes:** reinstall when desired packages are missing even if the applied-state hash matched; prune/list only the scoped venv’s own site-packages so base-runtime inheritance is not torn down every run

### Models & providers
- **OpenRouter and Hugging Face rows** own `ThinkingControlJson` and `RequestFieldsWhenToolsPresentJson` (e.g. `chat_template_kwargs.enable_thinking`, extra body fields such as `parallel_tool_calls`)
- Guide reasoning choice now reaches the request instead of being silently overridden by the row default

### Setup wizard
- Fix API key handling in step 4 on first run for OpenAI, OpenRouter, Hugging Face, and Gemini flows

### Azure deploy
- Reset / migrate scoped script venvs when the Azure Files mount layout changes (mfsymlinks), without blocking deploy or mutating SQL config unexpectedly
- Deploy scripts and slim entrypoint updated for the new venv lifecycle

### Images
- AI / support images republished to GHCR (`:main`, `:latest`, and `:v0.9.15`) for this cut

## Notes

- Apple Silicon: prefer the **slim** AI backend (`linux/amd64` under emulation).
- Operator details: `docs/release-runbook.md`, `installer/README.md`, `deploy/azure/README.md`.
