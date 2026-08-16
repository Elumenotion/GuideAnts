# InfiniteTalk standalone acceptance

The paired runners exercise the standalone `comfyui-video` service exclusively
through its HTTP ingress:

```powershell
pwsh ./scripts/run-acceptance.ps1
pwsh ./scripts/run-acceptance.ps1 -StartService
```

```bash
./scripts/run-acceptance.sh
./scripts/run-acceptance.sh --start-service
```

Without the explicit start flag, the runners never invoke Docker and require an
already-running service at `http://127.0.0.1:8189`. With the flag, they run only:

```text
docker compose ... up -d --no-deps comfyui-video
```

They do not stop services, restart unrelated containers, use `docker exec`, or
call ComfyUI directly. Runtime calls use `curl`; successful runs preserve the
HTTP transcript, generated request payloads, and host-side MP4 under
`artifacts/infinitetalk/`.

Before either runner can proceed, commit licensed `avatar.png` and `voice.wav`
under `tests/assets/infinitetalk/` and complete `ASSET_PROVENANCE.md`. Missing,
empty, or incorrectly encoded assets are hard preflight failures.

Acceptance passes only when the materialized MP4 duration matches the input
`voice.wav` duration within 0.5 seconds. The adapter derives `frames` from the
uploaded WAV when callers omit an explicit `frames` workflow parameter.
