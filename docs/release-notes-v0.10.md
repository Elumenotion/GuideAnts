# GuideAnts v0.10

**Streaming that survives disconnects** — Stop is explicit, long thinking turns stay alive, and cancel/timeout keeps the tokens you already saw. OpenAI/Azure **Responses** is a stateless transport owned by GuideAnts (no provider-side conversation chaining). GuideAntsApi is now the sole authority for local AI warmup. AI / support images republished to GHCR for this cut.

## Get started

1. Install [Docker Desktop](https://www.docker.com/products/docker-desktop/) (Windows/macOS) or Docker Engine 24+ with Compose (Linux). Windows needs WSL2.
2. Download **`guideants-installer-v0.10.zip`** from this release.
3. Unzip and run:
   - **Windows:** double-click `guideants.cmd`
   - **Linux / macOS:** `chmod +x guideants.sh && ./guideants.sh`
4. Choose database layout, AI backend, and optional services when prompted.
5. Open **http://localhost:5107/** — first account becomes Admin.

Existing installs: relaunch the installer; if `:main` moved past your pins, accept the update prompt to pick up this channel. Volumes are kept.

## Highlights

### Conversation streaming
- Closing or dropping the SSE connection is **not** Stop. The worker stays registered, senders and observers can reattach, and only **Stop** (`cancelTurn`) cancels the run
- If you accidently navigate you can come back and the stream is there
- If a scheduled job is running you can observe the conversation as it happens
- Thinking deltas are checkpointed, keep-alive streams no longer abort on `NotSupportedException`, and stale-turn recovery does not clobber an in-process run
- Silent or truncated streams are detected on the client (idle timeout, missing terminal events); the server always delivers a terminal event even when the channel is backpressured
- Cancel, idle timeout, and token-limit stops persist thinking blocks and accumulated usage (no zero-token usage rows). Empty assistant shells are hidden from conversation history
- Force-refresh no longer wipes local cancel/idle content, while idle-timeout failures still surface in the UI

### OpenAI Responses API
- Replaced the SDK Responses streaming client with a GuideAnts HTTP transport
- Requests are stateless: `store: false`, no `previous_response_id`. GuideAnts sends its own SQL transcript; the provider does not chain responses
- Live text and thinking come from SSE deltas; the final message, tool calls, and usage come from `response.completed`

### Local AI warmup
- **GuideAntsApi owns warmup policy.** Desired load/unload is an explicit JSON plan derived from `ServiceModes`, not an INI file or container autoload
- `ga-admin` validates and executes that plan only: no engine-inventory backfill, no startup autoload
- Split stacks (chat on one host, embeddings/ASR/TTS on another) get a complete per-host plan so each box loads only what it owns

### Images
- AI / support images republished to GHCR (`:main`, `:latest`, and `:v0.10`) for this cut: cpu, cuda13, rocm, slim, vulkan, plus PlantUML, MSSQL FTS, and SearXNG
- Web API images (`guideants-webapi-ui-slim`, `guideants-webapi-ui-mssql`) published to GHCR `:main` from this commit

## Notes

- Apple Silicon: prefer the **slim** AI backend (`linux/amd64` under emulation).
- Operator details: `docs/release-runbook.md`, `docs/local-ai-lifecycle/`, `installer/README.md`, `deploy/azure/README.md`.
