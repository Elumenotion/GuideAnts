# Local AI lifecycle authority

## Invariant

`GuideAntsApi` is the only component allowed to decide whether a local AI
service is enabled, which model or bundle is selected, and when the resulting
plan must be applied.

`ga-admin` is a mechanical executor. Engines are mechanical workers. Neither
may infer policy from environment defaults, model folders, marker files,
previous status, or container startup.

## Authority flow

1. `ServiceModes` and API-owned model configuration describe live routing.
2. `LocalAiDesiredStateBuilder` builds one complete JSON plan. Every local
   service has an explicit `enabled` boolean and, when enabled, an execution
   reference.
3. `LocalAiStartupWarmupService` sends the plan in the body of
   `POST /warmup/apply`.
4. `ga-admin` validates the complete plan, assigns an execution revision, and
   performs ordered load/unload calls.
5. `.warmup-state.json` records status only. It is never read as desired policy.

There is no persisted desired-state INI. There is no container-side autoload.
On every `ga-admin` startup, executor state is reset to empty/idle and no engine
call is made. The API must submit a new plan.

## Image generation

Image bundle definitions are inventory. The selected bundle ID lives on the
local ImageGeneration `ServiceMode`. `active_bundle.json`, loaded-engine state,
and `/admin/bundles` are diagnostics only; they must never update ServiceModes
or choose what loads next.

When OpenRouter or another cloud provider is active, the API plan sets
ImageGeneration `enabled: false`. Bundle projection and SD loading are skipped.

## Failure behavior

An enabled local route without an API-owned model or bundle selection is a
configuration error. Do not guess from disk, engine inventory, environment
variables, or a prior successful load.

## Endpoint topology (holistic stack hosts)

Each local AI capability is owned by **one stack host URL**. That same URL is
used for inference, settings admin proxy, and lifecycle apply.

| Service | Config key | Lifecycle target |
|---------|------------|------------------|
| Chat / llama | `LlamaCpp:BaseUrl` | `{stack}/llama-admin/` derived from the llama-cpp URL |
| Embeddings | `LocalServiceHosts:EmbeddingsBaseUrl` | `{stack}/llama-admin/` on that host |
| ASR | `LocalServiceHosts:SpeechTranscriptionBaseUrl` | same |
| TTS | `LocalServiceHosts:SpeechSynthesisBaseUrl` | same |
| Image / SD | `LocalServiceHosts:ImageGenerationBaseUrl` | same |

Settings inference and admin proxy compose service paths (`/emb`, `/asr`, …) on
the stack host via `LocalServiceAdminRouting`. Lifecycle apply uses the same
host: `LocalAiWarmupPlanSplitter` sends a **complete per-stack plan** to each
configured stack's ga-admin. Services that belong on another stack are explicit
`enabled: false` on that box so loopback engines there unload.

### Single gateway (typical)

When `LlamaCpp:BaseUrl` and all `LocalServiceHosts:*` normalize to the same
gateway (for example `http://guideants-ai:80`), apply is a single POST — same as
before.

### Split stacks (chat on PC, aux on Max)

When `LocalServiceHosts:EmbeddingsBaseUrl` points at Max but `LlamaCpp:BaseUrl`
points at the local PC:

1. The API builds one policy plan from routing.
2. It POSTs a **PC plan** (llama on, aux off) to local `/llama-admin/`.
3. It POSTs a **Max plan** (emb/asr/tts on, llama off) to Max `/llama-admin/`.
4. Inference and load/unload for embeddings on Max both use the Max host.

No inference-only URL split: lifecycle follows `LocalServiceHosts` for aux
services and `LlamaCpp:BaseUrl` for chat.
