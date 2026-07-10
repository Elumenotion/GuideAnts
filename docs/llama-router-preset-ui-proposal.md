# Curated Local Llama Model UX and Configuration Proposal

Last updated: 2026-07-10

This document proposes a curated-first local llama experience in which an operator normally chooses
only a model and a quant. A curated definition supplies the Hugging Face repository and concrete
defaults required to install and run that model. Quant choices are discovered from the live contents
of the declared repository through the GuideAnts Hugging Face API; they are not maintained as a
duplicated array in the curated definition.

The proposal also preserves one authoritative runtime destination per concern:

- downloaded GGUF and projector artifacts live in the local-model volume;
- per-alias llama-server switches live in `router-models.ini`;
- model-family chat request behavior lives in runtime profiles;
- catalog routing identity lives in `Models.RuntimeConfigJson`;
- fleet-wide router switches live in persisted **fleet llama runtime settings** (UI-editable), not operator compose edits;

The normal UI does not ask the operator to understand or independently assemble those layers.
See **§2.6** for the full layer map (excluding download) and how each concern is addressed.

Related docs:

- [llama-model-download-and-runtime-management.md](llama-model-download-and-runtime-management.md)
- [settings-architecture.md](settings-architecture.md)
- [docker/llama/README.md](../docker/llama/README.md)

---

## 1. Problem and current state

Today, local llama onboarding is a free-form Hugging Face workflow. The operator must understand
catalog identity, router identity, runtime profiles, repository files, context allocation, cache
settings, and advanced request behavior before GuideAnts has enough information to install a model.

| Layer | What it holds | Problem |
| --- | --- | --- |
| `router-models.ini` (volume) | `model`, `mmproj`, `ctx-size`, `cache-ram`, hand-edited extras | Authoritative for spawned children, but UI only writes two keys |
| `Models.RuntimeConfigJson` (SQL) | `routerModelId`, `runtimeProfileId`, duplicated `routerContextSize` / `routerCacheRamMib`, `loadParams` | Duplicates INI; `loadParams` is not a documented llama-server preset path |
| Compose `GA_LLAMA_*` env | `--parallel`, `--threads`, `--kv-unified`, etc. | Bootstrap defaults only; **defect:** ongoing operator changes require compose edits instead of Settings |
| Runtime profiles (SQL) | sampling definitions, thinking mappings, message normalization | Correct model-family abstraction, but not selected automatically from a curated model |
| HF repository browser | one GGUF filename and optional `mmproj` | Free-form; preferred quant is guessed; sharded quants are currently disabled |
| Download operation | repository and include patterns | Source repository, resolved revision, and selected quant are not retained after installation |

There is already a curated-manifest/catalog pipeline for embeddings, ASR, and TTS. Llama chat models
do not use it yet.

**Verified runtime behavior (2026-07-09):**

- Router starts once: `llama-server --models-preset /models-local/router-models.ini` + global flags.
- Loading alias `Qwen3.6-35B-A3B-MTP-GGUF` spawns a **child** whose argv comes from that alias's INI
  section (e.g. `--spec-type draft-mtp` appeared after adding keys to INI).
- Saving context/cache in Settings syncs to INI and triggers router reload (SIGTERM → respawn →
  re-load loaded aliases).
- `GA_LLAMA_MODELS_MAX=1` enforces one loaded model at a time.
- Runtime profiles affect **chat API** (sampling, reasoning), not llama-server argv.

---

## 2. Goals and design rules

### 2.1 Product goal

The primary experience is:

1. choose a curated model;
2. choose one of the quants discovered in its declared Hugging Face repository;
3. review the resolved installation;
4. install.

The operator does not choose a router alias, runtime profile, projector, context size, cache size,
load JSON, parallel-tool behavior, or preset template in this flow.

### 2.2 Curated definitions are complete recipes, not runtime capability abstractions

A curated definition declares the source repository and concrete defaults. It must not contain
abstract executable properties such as:

```json
{
  "thinking": true,
  "vision": true,
  "supportsParallelTools": true
}
```

Those values require model-specific interpretation and therefore cannot safely drive runtime
behavior. Human-facing labels such as `Reasoning`, `Vision`, and `Tool use` may be present for
display and filtering, but labels never change a request or llama-server process.

Executable behavior is concrete:

- thinking is expressed through runtime-profile request actions;
- parallel tool calling is expressed as the exact request field applied when tools are present;
- vision is established by an explicit projector artifact and any required router/profile settings;
- context allocation is the concrete `ctx-size` router preset key.

### 2.3 One source of truth per concern

```
Curated definition     → repository + concrete installation defaults
HF repository API      → quant choices available at a resolved repository revision
Installed artifacts    → exact GGUF shard set and projector files
router-models.ini      → llama-server per-alias argv
RuntimeConfigJson      → catalog-to-router/profile identity
RuntimeProfiles        → sampling, thinking, message, and tool-request behavior
Installation record    → source and installed-variant provenance
Fleet llama settings   → fleet-wide router base preset (SQL; UI under Models & Runtime)
Compose GA_LLAMA_*     → first-boot bootstrap seed only — not the operator write path
```

The curated definition is consumed during onboarding. It is not another runtime layer. Installation
projects each value into its authoritative destination and records what was installed.

### 2.4 Operator configuration never requires compose edits

Any llama-server switch an operator must change — fleet-wide or per-model — MUST be writable through
Settings (`/api/settings/*`). Requiring `docker-compose.yml` or `.env` edits for normal operation is
a **defect**, not an accepted layering choice.

| Scope | Correct operator path | Defect (to eliminate) |
| --- | --- | --- |
| Per-model llama-server argv | Layer B: curated manifest + **Customize** preset editor (§4.7, §5.8) | Hand-edited INI; SQL `loadParams`; ctx/cache duplicated in catalog JSON |
| Fleet-wide llama-server argv | Layer A: **Fleet llama server settings** in Models & Runtime (§4.7.3, §6.4) | Compose `GA_LLAMA_*` as the only write path |
| Chat request behavior | Layer E: runtime profiles | `parallelToolCalls` in catalog JSON |

Compose and `start-llama.sh` remain the **mechanism** that passes argv to `llama-server`. They are
not the **operator configuration store**. Bootstrap compose values seed first run; persisted settings
override them on router restart.

### 2.5 No duplicated quant catalog

The curated definition does not contain a `variants` or `quantOptions` array. Quant sources are
determined by repository contents and returned by the Hugging Face repository API. The definition
contains the repository, revision policy, and defaults that apply to a selected quant.

### 2.6 Configurable layers and how this proposal addresses each

Local llama configuration is split into distinct layers. Each layer has one authoritative store, one
write path, and one read path in the UI. **Download and HF artifact resolution are out of scope for
this table** — they are covered in §5. This section covers everything that governs how an installed
model runs and how chat requests are shaped.

```text
Operator-facing curated flow
  └─ picks model + quant only
       └─ install projects defaults into the layers below (one time)
            └─ runtime reads each layer from its authoritative store forever after
```

| Layer | What it configures | Authoritative store | Today | This proposal |
| --- | --- | --- | --- | --- |
| **A. Fleet router base preset** | Process-wide llama-server flags merged into every child: `--jinja`, `--parallel`, `--threads`, `--kv-unified`, `--flash-attn`, `--tensor-split`, … | Persisted fleet llama settings (SQL) → applied on router restart | **Defect:** compose `GA_LLAMA_*` is the only write path | **Fleet llama server settings** UI under Models & Runtime (§4.7.3, §6.4). Compose seeds defaults on first boot only. New fleet-wide llama.cpp switches are added to the settings schema + UI — never as an operator compose edit. |
| **B. Per-alias llama-server preset** | Child-process argv for one loaded model: `ctx-size`, `cache-ram`, `image-min-tokens`, `spec-type`, `spec-draft-n-max`, any future per-model switch | `router-models.ini` extras under `[alias]` | INI is authoritative but admin API only writes `ctx-size` / `cache-ram`; other keys require hand edits | **Curated `defaults.routerPreset`** declares defaults. **§5.8** writes full preset on install/repair. **Customize / Custom** expose an open preset editor for alias-scoped keys (§4.7.7). New model-specific llama.cpp switches land here via manifest update or operator edit — not compose. |
| **C. Artifact paths** | Which GGUF shards and projector file the alias loads | `router-models.ini` `model` / `mmproj` keys + volume files | Written on download; paths not retained in provenance | **Install record** stores exact files and commit. **Repair** re-verifies paths. Paths written to INI with preset atomically (§5.8). Curated manifest declares default `mmproj` path; quant files come from HF API at install time only. |
| **D. Catalog routing identity** | Which router alias and runtime profile a catalog model uses | `Models.RuntimeConfigJson` | Also stores duplicated ctx/cache, `loadParams`, `parallelToolCalls` | **Shrunk to two fields:** `routerModelId`, `runtimeProfileId` (§4.8). All server argv lives in INI (layer B). All chat behavior lives in profiles (layer E). |
| **E. Chat request behavior** | Sampling defaults, thinking control, message normalization, tool-request fields (`parallel_tool_calls`, `enable_thinking`, …) | Runtime profiles (SQL bootstrap + optional operator edits) | Correct abstraction; not auto-selected from curated model; `parallelToolCalls` wrongly in catalog JSON | **Curated `defaults.runtimeProfileId`** binds each catalog entry to the right family profile (§4.11). Tool policy moves to `requestFieldsWhenToolsPresent` in profiles. New profiles `deepseek_r1`, `qwen3_coder`. Curated flow does not expose profile picker; **Customize** does. |
| **F. Curated manifest** | Repository, versioned defaults, quant curator labels, hardware notes | Version-controlled `manifest.json` in llama-admin image | Does not exist for llama | **New catalog task `llama`** with 14 v1 entries (§4.12–§4.16). Manifest is an **input recipe**, not a runtime store — values are projected into B, C, D, E on install and recorded in G. |
| **G. Installation provenance** | What was installed: commit, quant, files, preset snapshot, definition version | None (lost after download) | Cannot repair, change quant, or audit | **`LocalModelInstallation`** record (§4.9). Powers repair, change-quant, read-only technical view, and curated adoption. Preset snapshot matches what was written to INI. |
| **H. Catalog presentation** | Display name, description, sort order, active flag | `Models` catalog columns | Mixed with runtime fields in one form | **Separated from runtime.** Normal edit surface changes presentation and triggers install operations (change quant, repair). Does not directly edit layers B or E unless operator enters Customize. |

#### 2.6.1 Layer A vs B — runtime merge vs operator paths

**llama.cpp merge rule (unchanged physics):** B does **not** override A. For any key set in both
places, **A wins** — the router CLI base preset merges into each alias section with CLI taking highest
precedence (`start-llama.sh` / `common/preset.cpp`).

**Operator rule (requirements alignment):** neither A nor B may be compose-only. Both scopes are
UI-writable. Compose-only configuration for either scope is a defect.

| | Layer A (fleet) | Layer B (per-alias) |
| --- | --- | --- |
| **Runtime role** | Base preset merged into every spawned child | Model-specific argv for one alias |
| **Authoritative store** | SQL fleet llama settings | `router-models.ini` |
| **Operator UI** | Settings → Models & Runtime → **Fleet llama server** | Curated manifest + **Customize** preset editor |
| **New llama.cpp switch (model-specific)** | Must **not** go here | Manifest `routerPreset` or Customize |
| **New llama.cpp switch (fleet-wide)** | Fleet settings UI + schema | Must **not** go here |
| **Compose `GA_LLAMA_*`** | Bootstrap seed only | Never |

**Why A still exists as a runtime layer**

Fleet-wide switches (`jinja`, `parallel`, `threads`, `tensor-split`, …) should apply uniformly to
every child without duplicating them across 14 catalog rows. That is a real runtime concern. The
defect is not that A exists — it is that A is only writable through compose today.

**Why B exists**

Model-specific switches (`ctx-size`, `image-min-tokens`, MTP keys, future per-model flags) must not
propagate to every child. Global `image-min-tokens` in fleet settings would break non-vision models
(verified).

**Collision policy**

GuideAnts partitions keys by **scope**, not by "compose vs INI":

| Scope | Keys | Operator writes via |
| --- | --- | --- |
| Alias | `ctx-size`, `cache-ram`, `image-min-tokens`, `spec-type`, `spec-draft-n-max`, per-model overrides | Layer B preset editor |
| Fleet | `jinja`, `flash-attn`, `parallel`, `threads`, `kv-unified`, `cont-batching`, `tensor-split`, … | Layer A fleet settings |
| Forbidden overlap | Same key in A and B | UI prevents; fleet wins at runtime if both set |

`start-llama.sh` omits `ctx-size` / `cache-ram` from router CLI in preset mode so alias values are
not clobbered. Fleet settings must likewise omit alias-scoped keys.

**New llama.cpp feature switch — required workflow**

1. Classify scope: does it apply to **one model** or **every model on the host**?
2. **Per-model** → add to curated `defaults.routerPreset` and/or operator Customize; never compose.
3. **Fleet-wide** → add to fleet llama settings schema + UI; never operator compose.
4. If unsure, default to **B** (per-alias) — safer than a global that hits every child.

No allowlist gate on unknown keys in operator surfaces. Curated manifest CI validates authored
defaults; Customize accepts any alias-scoped preset key llama-admin can persist.

#### How the curated flow addresses each layer without operator assembly

| Layer | Operator action in curated flow | What happens automatically |
| --- | --- | --- |
| A | None | Inherits persisted fleet llama settings |
| B | None (read-only on review) | `defaults.routerPreset` → INI via §5.8 |
| C | Chooses quant only | HF API resolves shard group; manifest `mmproj` + chosen quant → INI paths |
| D | None | `routerModelId` + `runtimeProfileId` from manifest → `RuntimeConfigJson` |
| E | None | `runtimeProfileId` from manifest → chat client applies profile on every completion |
| F | Chooses curated model row | Server loads definition version used for resolution |
| G | None | Written at end of successful install |
| H | Optional after install | Display fields on catalog edit; defaults seeded at install |

#### How Customize / Custom addresses layers the curated flow hides

| Layer | Customize (installed → operator-managed) | Custom HF install |
| --- | --- | --- |
| A | **Fleet llama server** settings editor | **Fleet llama server** settings editor |
| B | Full preset editor; changes write INI via §5.8 | Full preset editor; operator supplies all required keys |
| C | Change quant / repair operations | Explicit artifact-group selection |
| D | Can change `routerModelId` / `runtimeProfileId` | Operator supplies alias + profile |
| E | Runtime profile picker | Runtime profile picker |
| F | Detaches from curated version tracking; provenance retained | No curated definition |
| G | Updated on repair / change-quant | Written on install |
| H | Unchanged | Operator supplies display metadata |

#### What this removes (cross-layer duplication)

| Removed | Was duplicated across | Now lives only in |
| --- | --- | --- |
| `routerContextSize` / `routerCacheRamMib` in SQL | RuntimeConfigJson + INI | INI layer B |
| `loadParams` in SQL | RuntimeConfigJson + load API | Dropped; load is alias-only |
| `parallelToolCalls` in catalog JSON | RuntimeConfigJson + chat factory | Profile layer E |
| Abstract capability booleans in manifest | Would have implied B + E | Concrete keys in B and E separately |
| Global `image-min-tokens` in compose | Would clobber all children | Per-alias layer B only |

---

## 3. Proposed UX

### 3.1 Entry points

Both existing onboarding surfaces use the same curated flow and API:

- Settings → Models & Runtime → Add Model → Local llama
- Home → Add AI Services → Local AI → Models

The first screen provides:

- **Curated models** — selected by default
- **Custom Hugging Face repository** — explicitly advanced
- **Attach existing router alias** — operational adoption path

### 3.2 Curated step 1 — choose a model

Display searchable cards sourced from the llama curated catalog. A card contains:

- display name and concise description;
- Hugging Face owner/repository;
- license and gated-access status;
- model parameter count and architecture, when authored;
- informational labels such as Text, Vision, Reasoning, and Tool use;
- curator notes and documentation links.

Selecting a card does not install a predefined GGUF. It causes GuideAnts to query the declared
repository for currently available quant artifact groups.

### 3.3 Curated step 2 — choose a quant

The API groups repository files into logical selectable quants. One row represents either one GGUF
or a complete ordered shard set. Each row displays:

- quant label, for example `Q4_K_M`, `Q5_K_M`, or `Q6_K_XL`;
- total download bytes across all shards;
- shard count;
- authored RAM/VRAM guidance when present in the curated definition;
- repository filename summary;
- an authored recommendation badge, only when the curated definition explicitly declares a
  recommendation rule or quant label.

There is no client-side preferred-quant guess. The current `Q5_K_M → Q5_K_S → Q4_K_M → largest`
selection heuristic is removed from the curated path. No row is selected until the operator selects
it.

The projector is not selectable in the normal flow. The curated definition declares the projector
file or a deterministic projector match. The resolved projector is shown read-only in technical
details.

### 3.4 Curated step 3 — review

The review screen shows:

- catalog display name;
- selected quant and total bytes;
- source repository;
- resolved repository commit;
- exact GGUF file/shard list;
- projector file, if any;
- configured context window from the router preset;
- destination volume;
- any gated-repository or hardware warnings.

A collapsed **Technical details** panel shows the runtime profile ID and complete router preset.
These values are read-only in the curated flow.

The primary action is **Install model**.

### 3.5 Progress and completion

Retain the existing operation stages:

```
queued → resolvingFiles → downloading → registeringAlias → completed
```

`resolvingFiles` now resolves the curated definition, HF commit, selected quant group, projector,
runtime profile, and router preset as one immutable installation input.

On completion, offer:

- Load now
- Use as default chat model
- View installed model

### 3.6 Installed curated model / catalog edit

The normal edit surface contains:

- display name, description, order, and active status;
- curated model name and definition version;
- selected quant;
- repository and resolved commit;
- exact installed artifacts;
- effective runtime profile;
- effective router preset and runtime state, read-only.

Actions:

- **Change quant** — starts a replacement operation using a newly selected HF quant group;
- **Repair** — verifies/re-downloads the recorded artifact set and rewrites the recorded preset;
- **View technical configuration**;
- **Customize** — explicitly converts the installation to operator-managed configuration.

The normal form does not expose router alias, runtime profile selection, context/cache inputs,
load-params JSON, parallel-tool checkboxes, or preset-template selection. Fleet llama settings are
edited in a separate Models & Runtime panel, not the per-model catalog form.

### 3.7 Custom Hugging Face installation

The advanced custom path preserves extensibility for repositories without a curated definition.
It explicitly exposes:

1. repository and revision;
2. HF repository browse;
3. quant artifact-group selection, including sharded groups;
4. optional projector selection;
5. catalog identity and router alias;
6. runtime profile;
7. full router-preset editor for **per-alias llama-server argv** (§4.7.7);
8. target directory.

The preset editor is the surface for alias-scoped server args (layer B). Fleet-scoped args use the
**Fleet llama server** settings editor (layer A).

Custom means operator-managed. The UI does not infer a runtime profile, projector, context window, or
router switches from the model name. Validation requires the operator to provide every required
value.

### 3.8 Attach existing alias

The attach path continues to list aliases with model artifacts and no catalog binding. It requires a
catalog identity and runtime profile but does not alter the alias's existing INI preset unless the
operator enters the advanced customization flow.

---

## 4. Curated definition and resolved model

### 4.1 Definition shape

The definition is version-controlled catalog content. It supplies the repository and defaults that
apply to any quant selected from that repository.

```json
{
  "schemaVersion": 1,
  "id": "qwen3.6-35b-a3b-mtp",
  "version": "2026-07-10",
  "display": {
    "name": "Qwen 3.6 35B A3B",
    "description": "Curated local Qwen model with MTP and vision configuration.",
    "labels": ["Text", "Vision", "Reasoning", "Tool use"],
    "license": "Apache-2.0",
    "documentationUrl": "https://huggingface.co/example/Qwen3.6-35B-A3B-GGUF"
  },
  "source": {
    "repository": "example/Qwen3.6-35B-A3B-GGUF",
    "revision": "main"
  },
  "defaults": {
    "catalogModelId": "qwen3.6-35b-a3b-local",
    "routerModelId": "Qwen3.6-35B-A3B-MTP-GGUF",
    "runtimeProfileId": "qwen3_6",
    "targetDirectory": "Qwen3.6-35B-A3B-MTP-GGUF",
    "mmproj": {
      "path": "mmproj-F16.gguf"
    },
    "routerPreset": {
      "ctx-size": "131072",
      "image-min-tokens": "1024",
      "spec-type": "draft-mtp",
      "spec-draft-n-max": "2"
    }
  },
  "quantMetadata": {
    "recommendedLabels": ["Q6_K_XL"],
    "guidance": {
      "Q4_K_M": {
        "summary": "Lower memory requirement."
      },
      "Q6_K_XL": {
        "summary": "Curator-recommended quality."
      }
    }
  }
}
```

`recommendedLabels` affects badges and initial focus only. It does not silently select a quant.
`quantMetadata` may annotate quant labels discovered by the HF API, but it does not declare files and
cannot create a quant that is absent from the resolved repository.

### 4.2 Definition ownership and mutation

- Definitions are authored and reviewed in source control.
- The llama catalog manifest is shipped with llama-admin, following the existing emb/ASR/TTS
  catalog pattern.
- `GET /admin/catalog` returns definitions; the GuideAnts API proxies a llama-specific catalog
  endpoint.
- Runtime code never appends discovered files or local installation state to the definition.
- A curator changes a definition by publishing a new `version`.
- Existing installations retain their recorded definition version and resolved artifact commit.

### 4.3 Quant discovery

After a definition is selected, the API lists files at `source.repository` and `source.revision`.
The response includes the resolved commit and groups GGUF files by quant and shard family:

```json
{
  "catalogId": "qwen3.6-35b-a3b-mtp",
  "repository": "example/Qwen3.6-35B-A3B-GGUF",
  "requestedRevision": "main",
  "resolvedRevision": "8f4c3f1a...",
  "quants": [
    {
      "id": "q4_k_m",
      "label": "Q4_K_M",
      "totalBytes": 20123456789,
      "files": [
        {
          "path": "Qwen3.6-35B-A3B-Q4_K_M.gguf",
          "size": 20123456789
        }
      ]
    },
    {
      "id": "q6_k_xl",
      "label": "Q6_K_XL",
      "totalBytes": 28765432100,
      "files": [
        {
          "path": "Qwen3.6-35B-A3B-Q6_K_XL-00001-of-00002.gguf",
          "size": 14380000000,
          "shardIndex": 1,
          "shardCount": 2
        },
        {
          "path": "Qwen3.6-35B-A3B-Q6_K_XL-00002-of-00002.gguf",
          "size": 14385432100,
          "shardIndex": 2,
          "shardCount": 2
        }
      ]
    }
  ],
  "projector": {
    "path": "mmproj-F16.gguf",
    "size": 123456789
  }
}
```

The grouping implementation must:

- group all shards belonging to the same quant;
- reject incomplete shard groups;
- preserve deterministic shard order;
- exclude unrelated GGUF files such as projectors from model quant groups;
- match the definition's projector against the same resolved revision;
- return a stable quant ID derived from the normalized quant label, not from array position.

The quant response is transient repository data. It is not written back into the curated manifest.

### 4.4 Projector policy

The normal curated flow never asks the operator to select an `mmproj`.

The definition declares one of:

```json
{ "mmproj": null }
```

or:

```json
{ "mmproj": { "path": "mmproj-F16.gguf" } }
```

If the projector is hosted in another repository, the definition may declare an explicit source:

```json
{
  "mmproj": {
    "repository": "example/Qwen3.6-35B-A3B-GGUF",
    "revision": "main",
    "path": "mmproj-F16.gguf"
  }
}
```

Do not add projector selection or quant-to-projector mapping until a verified model requires it. If
such a model exists, represent the mapping explicitly in that definition and test it; do not infer
compatibility from filenames.

### 4.5 Model parameters and runtime profile

The curated definition references a runtime profile. The profile is the authoritative model-family
chat contract and contains:

- sampling definitions/defaults such as `temperature`, `top_p`, `top_k`, `min_p`, and penalties;
- allowed ranges and guide-builder exposure;
- system/developer message normalization;
- thought-block parsing;
- concrete thinking/reasoning request actions;
- concrete tool-request fields.

An abstract value such as `"thinking": true` is forbidden. Qwen thinking remains represented by
concrete actions such as:

```json
{
  "defaultChoice": "enabled",
  "choiceActions": {
    "none": [
      {
        "target": "NestedRequestField",
        "key": "chat_template_kwargs.enable_thinking",
        "value": false
      },
      {
        "target": "RequestField",
        "key": "reasoning_format",
        "value": "none"
      }
    ],
    "enabled": [
      {
        "target": "NestedRequestField",
        "key": "chat_template_kwargs.enable_thinking",
        "value": true
      }
    ]
  }
}
```

Parallel tool calling also belongs to concrete runtime-profile request shaping:

```json
{
  "requestFieldsWhenToolsPresent": {
    "parallel_tool_calls": true
  }
}
```

This extends the runtime-profile contract beyond sampling/reasoning without introducing an abstract
capability boolean. `LlamaCppChatClient` applies these exact fields only when the outgoing request
contains tools.

The current `RuntimeConfigJson.parallelToolCalls` property is migrated into this profile request
policy and removed from the normal catalog form.

### 4.6 Context size

“Context size” represents two distinct facts:

- documented model context limit — informational curated metadata;
- configured `ctx-size` — concrete llama-server allocation for this installation.

Only `defaults.routerPreset["ctx-size"]` affects runtime. A documented limit must never be converted
into an allocation automatically.

For curated definitions, explicitly set `ctx-size`; do not omit it merely to inherit a fleet
default. Custom installations may deliberately omit it after the UI shows the effective fleet
default.

### 4.7 Llama-server runtime arguments

Llama-server argv combines curated manifest defaults (layer B), fleet runtime settings (layer A),
and runtime profiles (chat API only — not argv).

Today the stack already supports arbitrary per-alias keys in `router-models.ini` — the INI parser
stores any key other than `model` / `mmproj` in `extras` and llama.cpp applies them when spawning a
child. The **gap** is that GuideAnts can only **write** `ctx-size` and `cache-ram` through
llama-admin; keys such as `image-min-tokens`, `spec-type`, and `spec-draft-n-max` require manual INI
edits. The curated manifest already declares a full `routerPreset`, but the install path must be
extended to persist it.

#### 4.7.1 Three layers

```text
┌─────────────────────────────────────────────────────────────────┐
│ Layer 1 — Fleet router base preset (persisted settings → router CLI) │
│   --jinja, --parallel, --threads, --kv-unified, --flash-attn, …       │
│   Operator path: Settings → Fleet llama server (NOT compose)            │
├─────────────────────────────────────────────────────────────────┤
│ Layer 2 — Per-alias router preset (router-models.ini extras)      │
│   ctx-size, cache-ram, image-min-tokens, spec-type, …             │
│   Authoritative for spawned child argv for alias-controlled keys. │
│   Written by llama-admin on install/repair/customize.             │
├─────────────────────────────────────────────────────────────────┤
│ Layer 3 — Chat request fields (runtime profiles)                  │
│   temperature, top_p, enable_thinking, parallel_tool_calls, …     │
│   Sent on POST /v1/chat/completions — NOT llama-server argv.      │
└─────────────────────────────────────────────────────────────────┘
```

Router spawn (verified):

```text
llama-server --models-preset /models-local/router-models.ini + fleet base preset flags
  → POST /models/load { "model": "<alias>" }
  → child llama-server argv = alias INI section + merged base preset
```

#### 4.7.2 Per-alias router preset (`defaults.routerPreset`)

The curated definition field `defaults.routerPreset` is a string map of llama.cpp **preset option
names** (no leading `--`). These become INI keys under the alias section, alongside `model` and
`mmproj`:

```ini
[Qwen3.6-35B-A3B-GGUF]
model = /models-local/llama/.../shard-00001-of-00002.gguf
mmproj = /models-local/llama/.../mmproj-F16.gguf
ctx-size = 131072
image-min-tokens = 1024
```

MTP example:

```ini
[Qwen3.6-35B-A3B-MTP-GGUF]
model = /models-local/llama/.../shard-00001-of-00002.gguf
mmproj =
ctx-size = 131072
spec-type = draft-mtp
spec-draft-n-max = 2
```

The installer sends the resolved paths plus the full preset map to llama-admin (§5.8). The API must
**replace** the alias section's preset extras atomically on install/repair while preserving unrelated
hand-edited keys only in the custom/operator-managed path.

#### 4.7.3 Fleet router base preset (layer A)

Fleet-wide llama-server switches are **not** operator-configured through compose. Compose `GA_LLAMA_*`
values bootstrap first boot only. Ongoing operator changes use persisted **fleet llama runtime
settings** (SQL), edited in Settings → Models & Runtime → **Fleet llama server**, written through
`/api/settings/llama/runtime/fleet-preset` (name TBD).

On save, GuideAnts persists the settings, materializes them into the router process environment for
the next restart, and triggers llama-server reload. `start-llama.sh` continues translating env vars to
CLI flags — the storage layer changes, not llama.cpp merge semantics.

| Setting key | llama-server flag | Merges into children | Notes |
| --- | --- | --- | --- |
| `modelsPreset` | `--models-preset` | — | Fixed infrastructure; not operator-tuned |
| `modelsMax` | `--models-max` | — | `1` = one loaded model |
| `noAutoload` | `--no-models-autoload` | — | Load via API |
| `threads` | `--threads` | Yes | CPU thread count |
| `parallel` | `--parallel` | Yes | Server slot count |
| `gpuLayers` | `--n-gpu-layers` | Yes | Fleet default; per-alias override in B |
| `kvOffload` | `--kv-offload` / `--no-kv-offload` | Yes | |
| `kvUnified` | `--kv-unified` | Yes | |
| `jinja` | `--jinja` | Yes | Required for tool templates |
| `contBatching` | `--cont-batching` | Yes | |
| `noMmap` | `--no-mmap` | Yes | |
| `flashAttn` | `--flash-attn` | Yes | `on` on ROCm |
| `cacheTypeK` | `--cache-type-k` | Yes | |
| `cacheTypeV` | `--cache-type-v` | Yes | |
| `tensorSplit` | `--tensor-split` | Yes | Multi-GPU layer split |
| `cudaVisibleDevices` | env override | — | Hardware topology |

**Alias-scoped keys are forbidden in fleet settings** (`ctx-size`, `cache-ram`, `image-min-tokens`,
`spec-type`, …). The fleet UI rejects them; they belong in layer B only.

**Critical rule (from `start-llama.sh`):** fleet settings must not include `ctx-size` or `cache-ram`
on the router CLI when `--models-preset` is active — those are alias-scoped in B.

Installer compose files retain `GA_LLAMA_*` as **default seeds** for empty DB settings. They are not
the operator write path after bootstrap.

#### 4.7.4 Per-alias preset keys — scope validation, not compose lockout

Preset keys are validated by **scope**, not by a fixed allowlist that forces compose edits:

| Scope | Where operator edits | Validation |
| --- | --- | --- |
| **Alias** (layer B) | Customize / Custom preset editor; curated `defaults.routerPreset` | Any llama.cpp per-alias preset key; reject fleet-scoped keys with redirect to fleet settings |
| **Fleet** (layer A) | Fleet llama server settings | Any fleet-scoped preset key; reject alias-scoped keys |

**v1 curated manifest** documents known keys (`ctx-size`, `image-min-tokens`, MTP, …). CI validates
authored defaults. **Operator Customize** accepts new alias-scoped keys without a product release —
llama-admin persists to INI and reports spawn failures against the key.

Fleet-scoped keys (`jinja`, `flash-attn`, `parallel`, `threads`, `kv-unified`, `tensor-split`, …)
are editable in fleet settings, not rejected from the alias editor because "use compose instead."
Compose-only access to fleet keys is the defect being removed.

#### 4.7.5 Current implementation gap

| Component | Today | Required |
| --- | --- | --- |
| `llama-admin` `POST /router/entries` | `contextSize`, `cacheRamMib` only | `preset: Record<string, string>` full map |
| `llama-admin` `GET /router/entries` | Returns ctx/cache only | Returns full `preset` extras |
| `upsert_router_entry()` | Writes ctx/cache flags | Merges/replaces full preset map |
| Download completion | Paths only | Paths + definition `routerPreset` |
| `LlamaRuntimeAdminClient` | Two overloads, no preset | Passes preset through |
| Inventory / installed-model DTO | Duplicated SQL ctx/cache | `routerPreset` read from INI |
| Custom UI | ctx/cache text fields | Key-value router preset editor |

`loadParams` in `RuntimeConfigJson` is **removed** — it was never a supported llama-server preset
path and duplicated alias identity.

#### 4.7.6 Install payload (paths + preset)

```json
{
  "alias": "Qwen3.6-35B-A3B-MTP-GGUF",
  "modelPaths": [
    "/models-local/llama/Qwen3.6-35B-A3B-MTP-GGUF/Qwen3.6-Q6_K_XL-00001-of-00002.gguf",
    "/models-local/llama/Qwen3.6-35B-A3B-MTP-GGUF/Qwen3.6-Q6_K_XL-00002-of-00002.gguf"
  ],
  "mmprojPath": "/models-local/llama/Qwen3.6-35B-A3B-MTP-GGUF/mmproj-F16.gguf",
  "preset": {
    "ctx-size": "131072",
    "image-min-tokens": "1024",
    "spec-type": "draft-mtp",
    "spec-draft-n-max": "2"
  }
}
```

Sharded `model` path representation must follow the supported `models-preset` contract. The transport
must not invent repeated INI keys without validating that contract.

#### 4.7.7 Customize / custom UI — router preset editor

The **Customize** and **Custom HF install** flows expose a structured editor for **layer B**
(alias-scoped llama-server argv):

- link to **Fleet llama server** settings for layer A (separate editor, same Models & Runtime area);
- editable key-value table for alias-scoped preset keys (open for new llama.cpp switches);
- live preview of the resulting INI section;
- scope validation: fleet-scoped keys redirect to fleet settings, not compose.

Curated **View technical configuration** shows effective A + B read-only. **Customize** copies
installation provenance preset into the editor and marks the catalog row operator-managed.

### 4.8 Minimal catalog runtime configuration

After moving tool request behavior into the runtime profile and all llama-server arguments into INI:

```json
{
  "routerModelId": "Qwen3.6-35B-A3B-MTP-GGUF",
  "runtimeProfileId": "qwen3_6"
}
```

`RuntimeConfigJson` no longer stores `loadParams`, context/cache, or parallel-tool policy.

### 4.9 Installation provenance

Source repository and selected quant are currently lost after download. Add a
`LocalModelInstallation` record (or equivalent dedicated persistence) containing:

```json
{
  "catalogModelId": "qwen3.6-35b-a3b-local",
  "curatedDefinitionId": "qwen3.6-35b-a3b-mtp",
  "curatedDefinitionVersion": "2026-07-10",
  "repository": "example/Qwen3.6-35B-A3B-GGUF",
  "requestedRevision": "main",
  "resolvedRevision": "8f4c3f1a...",
  "quantId": "q6_k_xl",
  "quantLabel": "Q6_K_XL",
  "modelFiles": [
    "Qwen3.6-35B-A3B-Q6_K_XL-00001-of-00002.gguf",
    "Qwen3.6-35B-A3B-Q6_K_XL-00002-of-00002.gguf"
  ],
  "mmprojFiles": ["mmproj-F16.gguf"],
  "routerPreset": {
    "ctx-size": "131072",
    "image-min-tokens": "1024",
    "spec-type": "draft-mtp",
    "spec-draft-n-max": "2"
  },
  "managementMode": "curated"
}
```

Progress and transient operation logs do not belong in this record. The record captures the resolved
input required to inspect, repair, or replace an installation.

### 4.10 Shared best-practice parameters

These conventions apply to every v1 curated entry unless a model definition explicitly overrides
them.

#### Fleet defaults (layer A — not duplicated per curated model)

Bootstrap seeds from `installer/docker/docker-compose.ghcr-rocm.yml` and siblings. After first boot,
operators edit these through **Fleet llama server** settings (§4.7.3), not compose.

| Setting | Bootstrap value | Role |
| --- | --- | --- |
| `jinja` | `true` | Required for Qwen/Gemma/DeepSeek tool calling and thinking templates |
| `contBatching` | `true` | Continuous batching |
| `kvUnified` | `true` | Unified KV cache |
| `parallel` | `5` | Server slot count (not MTP, not tool parallelism) |
| `flashAttn` | `on` | ROCm profile |

Curated definitions set explicit per-alias `ctx-size` in layer B so context allocation is never
dependent on fleet defaults alone.

#### Quant selection policy

| Source | Default recommendation | Higher-quality option |
| --- | --- | --- |
| Unsloth repos | `UD-Q4_K_XL` | `UD-Q5_K_XL`, `UD-Q6_K_XL`, `Q6_K` |
| `ggml-org` repos | `Q4_K_M` | `Q5_K_M`, `Q8_0` |

`recommendedLabels` in each definition lists curator badges only. The UI never auto-selects a quant.

#### Runtime profile request policy (all tool-capable chat profiles)

Extend every llama-cpp runtime profile used by the v1 catalog with concrete tool-request fields:

```json
{
  "requestFieldsWhenToolsPresent": {
    "parallel_tool_calls": true
  }
}
```

This replaces per-catalog `RuntimeConfigJson.parallelToolCalls`.

#### Router preset keys used in v1

| Key | Typical value | When |
| --- | --- | --- |
| `ctx-size` | `65536`–`131072` | Always, per model class |
| `image-min-tokens` | `1024` | Vision-capable Qwen/Gemma only |
| `spec-type` | `draft-mtp` | MTP repos only |
| `spec-draft-n-max` | `2` | MTP repos only |

Do **not** set `image-min-tokens` on MTP entries. llama.cpp MTP + vision remains unstable; v1 MTP
curated models are text-first.

Do **not** set global router `image-min-tokens`. GuideAnts already normalizes this to per-alias
Qwen-VL sections only (`docker/build/guideants-ai/entrypoint.sh`).

#### Context-size classes

| Class | `ctx-size` | Used by |
| --- | --- | --- |
| `large` | `131072` | 27B dense, 35B MoE, 31B dense, coder, reasoning |
| `compact` | `65536` | 9B, E4B, small multimodal |

Qwen and Qwen-Coder families support 256K natively, but v1 curated installs use `131072` as the
practical local default that preserves thinking and repository-scale chat without assuming 128GB+
unified memory.

### 4.11 Runtime profiles required for v1

Reuse existing bootstrap profiles:

- `qwen3_5` — Qwen 3.5 family
- `qwen3_6` — Qwen 3.6 family
- `gemma4` — Gemma 4 family
- `gpt_oss` — OpenAI `gpt-oss` family

Add two new bootstrap profiles.

#### `deepseek_r1`

For `unsloth/DeepSeek-R1-Distill-Qwen-14B-GGUF`. Distilled reasoning model; uses Qwen-style
`redacted_thinking` blocks. DeepSeek and Qwen3 families both benefit from llama.cpp
`reasoning_format=deepseek` behavior (handled by current compose `GA_LLAMA_JINJA=1` + server
defaults).

```json
{
  "profileId": "deepseek_r1",
  "displayName": "DeepSeek R1 Distill",
  "description": "Distilled reasoning models (Qwen/Llama bases). Thinking via redacted_thinking blocks.",
  "providers": ["llama-cpp"],
  "combineSystemAndDeveloperMessages": true,
  "thoughtBlockPattern": "<think>[\\s\\S]*?</think>",
  "samplingParametersJson": {
    "temperature": { "key": "temperature", "label": "Temperature", "min": 0.0, "max": 2.0, "step": 0.1, "default": 0.6, "displayOrder": 0, "exposedInGuideBuilder": true },
    "top_p": { "key": "top_p", "label": "Top P", "min": 0.0, "max": 1.0, "step": 0.05, "default": 0.95, "displayOrder": 1, "exposedInGuideBuilder": true },
    "top_k": { "key": "top_k", "label": "Top K", "min": 1, "max": 100, "step": 1, "default": 20, "displayOrder": 2, "exposedInGuideBuilder": true },
    "min_p": { "key": "min_p", "label": "Min P", "min": 0.0, "max": 1.0, "step": 0.01, "default": 0.0, "displayOrder": 3, "exposedInGuideBuilder": false }
  },
  "thinkingControlJson": {
    "defaultChoice": "enabled",
    "choiceActions": {
      "none": [
        { "target": "NestedRequestField", "key": "chat_template_kwargs.enable_thinking", "value": false }
      ],
      "enabled": [
        { "target": "NestedRequestField", "key": "chat_template_kwargs.enable_thinking", "value": true }
      ]
    }
  },
  "requestFieldsWhenToolsPresent": {
    "parallel_tool_calls": false
  }
}
```

Reasoning-first model; parallel tool calls are disabled by default because tool+reasoning
combinations are less predictable on distilled R1 weights.

#### `qwen3_coder`

For `unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF`. Uses Qwen3-Coder official instruct sampling
defaults and XML-style tool calling (requires `--jinja`, already global).

```json
{
  "profileId": "qwen3_coder",
  "displayName": "Qwen3 Coder",
  "description": "Agentic coding profile for Qwen3-Coder family.",
  "providers": ["llama-cpp"],
  "combineSystemAndDeveloperMessages": true,
  "thoughtBlockPattern": "<think>[\\s\\S]*?</think>",
  "samplingParametersJson": {
    "temperature": { "key": "temperature", "label": "Temperature", "min": 0.0, "max": 2.0, "step": 0.1, "default": 0.7, "displayOrder": 0, "exposedInGuideBuilder": true },
    "top_p": { "key": "top_p", "label": "Top P", "min": 0.0, "max": 1.0, "step": 0.05, "default": 0.8, "displayOrder": 1, "exposedInGuideBuilder": true },
    "top_k": { "key": "top_k", "label": "Top K", "min": 1, "max": 100, "step": 1, "default": 20, "displayOrder": 2, "exposedInGuideBuilder": true },
    "repetition_penalty": { "key": "repetition_penalty", "label": "Repetition Penalty", "min": 1.0, "max": 2.0, "step": 0.05, "default": 1.05, "displayOrder": 3, "exposedInGuideBuilder": false }
  },
  "thinkingControlJson": {
    "defaultChoice": "none",
    "choiceActions": {
      "none": [
        { "target": "NestedRequestField", "key": "chat_template_kwargs.enable_thinking", "value": false },
        { "target": "RequestField", "key": "reasoning_format", "value": "none" }
      ],
      "enabled": [
        { "target": "NestedRequestField", "key": "chat_template_kwargs.enable_thinking", "value": true }
      ]
    }
  },
  "requestFieldsWhenToolsPresent": {
    "parallel_tool_calls": true
  }
}
```

#### Profile extensions for existing families

Add `requestFieldsWhenToolsPresent.parallel_tool_calls: true` to `qwen3_5`, `qwen3_6`, `gemma4`,
and `gpt_oss` during Phase 3 cleanup. No sampling default changes are required for v1; existing
bootstrap values already match vendor guidance.

### 4.12 V1 curated catalog (14 models)

Ship all entries in one manifest release. Primary source is Unsloth unless noted. Quant choices are
discovered live from each repository; definitions declare repository, defaults, and curator metadata
only.

Recommended UI badge per entry is listed in **Recommended quant badge**; it is not auto-selected.

#### Qwen 3.6

| ID | Repository | Profile | mmproj | Router preset | Recommended quant badge |
| --- | --- | --- | --- | --- | --- |
| `qwen3.6-35b-a3b` | `unsloth/Qwen3.6-35B-A3B-GGUF` | `qwen3_6` | `mmproj-F16.gguf` | `ctx-size=131072`, `image-min-tokens=1024` | `UD-Q4_K_XL` |
| `qwen3.6-27b` | `unsloth/Qwen3.6-27B-GGUF` | `qwen3_6` | `mmproj-F16.gguf` | `ctx-size=131072`, `image-min-tokens=1024` | `UD-Q4_K_XL` |
| `qwen3.6-35b-a3b-mtp` | `unsloth/Qwen3.6-35B-A3B-MTP-GGUF` | `qwen3_6` | `null` | `ctx-size=131072`, `spec-type=draft-mtp`, `spec-draft-n-max=2` | `UD-Q4_K_XL` |
| `qwen3.6-27b-mtp` | `unsloth/Qwen3.6-27B-MTP-GGUF` | `qwen3_6` | `null` | `ctx-size=131072`, `spec-type=draft-mtp`, `spec-draft-n-max=2` | `UD-Q4_K_XL` |

Display labels: Text, Vision, Reasoning, Tool use (base); Text, Reasoning, Tool use, MTP (MTP rows).

Primary recommendation: `qwen3.6-35b-a3b` for general local use; `qwen3.6-27b` for single-GPU
dense installs; `qwen3.6-35b-a3b-mtp` when operator wants faster text-only inference.

#### Qwen 3.5

| ID | Repository | Profile | mmproj | Router preset | Recommended quant badge |
| --- | --- | --- | --- | --- | --- |
| `qwen3.5-35b-a3b` | `unsloth/Qwen3.5-35B-A3B-GGUF` | `qwen3_5` | `mmproj-F16.gguf` | `ctx-size=131072`, `image-min-tokens=1024` | `UD-Q4_K_XL` |
| `qwen3.5-27b` | `unsloth/Qwen3.5-27B-GGUF` | `qwen3_5` | `mmproj-F16.gguf` | `ctx-size=131072`, `image-min-tokens=1024` | `UD-Q4_K_XL` |
| `qwen3.5-9b` | `unsloth/Qwen3.5-9B-GGUF` | `qwen3_5` | `mmproj-F16.gguf` | `ctx-size=65536`, `image-min-tokens=1024` | `UD-Q4_K_XL` |

Display labels: Text, Vision, Reasoning, Tool use.

`qwen3.5-9b` is the minimum viable dev/smoke-test model.

#### Gemma 4

| ID | Repository | Profile | mmproj | Router preset | Recommended quant badge |
| --- | --- | --- | --- | --- | --- |
| `gemma4-31b` | `unsloth/gemma-4-31B-it-qat-GGUF` | `gemma4` | `mmproj-F16.gguf` | `ctx-size=131072`, `image-min-tokens=1024` | `UD-Q4_K_XL` |
| `gemma4-26b-a4b` | `unsloth/gemma-4-26B-A4B-it-GGUF` | `gemma4` | `mmproj-F16.gguf` | `ctx-size=131072`, `image-min-tokens=1024` | `UD-Q4_K_XL` |
| `gemma4-12b` | `unsloth/gemma-4-12b-it-GGUF` | `gemma4` | `mmproj-F16.gguf` | `ctx-size=131072`, `image-min-tokens=1024` | `UD-Q4_K_XL` |
| `gemma4-e4b` | `unsloth/gemma-4-E4B-it-GGUF` | `gemma4` | `mmproj-F16.gguf` | `ctx-size=65536`, `image-min-tokens=1024` | `UD-Q4_K_XL` |

Display labels: Text, Vision, Reasoning, Tool use. `gemma4-e4b` also carries label Audio (display
only; v1 does not expose audio-specific install controls).

Primary Google-multimodal recommendation: `gemma4-31b`. MoE sweet spot: `gemma4-26b-a4b`.

#### Specialist models

| ID | Repository | Profile | mmproj | Router preset | Recommended quant badge |
| --- | --- | --- | --- | --- | --- |
| `gpt-oss-20b` | `ggml-org/gpt-oss-20b-GGUF` | `gpt_oss` | `null` | `ctx-size=131072` | `Q4_K_M` |
| `deepseek-r1-14b` | `unsloth/DeepSeek-R1-Distill-Qwen-14B-GGUF` | `deepseek_r1` | `null` | `ctx-size=131072` | `Q4_K_M` |
| `qwen3-coder-30b` | `unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF` | `qwen3_coder` | `null` | `ctx-size=131072` | `UD-Q4_K_XL` |

Display labels:

- `gpt-oss-20b`: Reasoning, Tool use
- `deepseek-r1-14b`: Reasoning, Math
- `qwen3-coder-30b`: Coding, Tool use, Agentic

`ggml-org` is used for `gpt-oss-20b` because it is the official llama.cpp conversion path.

### 4.13 Example full definition (`qwen3.6-35b-a3b`)

```json
{
  "schemaVersion": 1,
  "id": "qwen3.6-35b-a3b",
  "version": "2026-07-10",
  "display": {
    "name": "Qwen 3.6 35B A3B",
    "description": "Primary curated local model. Vision, reasoning, and tool use.",
    "labels": ["Text", "Vision", "Reasoning", "Tool use"],
    "license": "Apache-2.0",
    "documentationUrl": "https://huggingface.co/unsloth/Qwen3.6-35B-A3B-GGUF",
    "recommendedQuantLabels": ["UD-Q4_K_XL"],
    "primaryRecommendation": true
  },
  "source": {
    "repository": "unsloth/Qwen3.6-35B-A3B-GGUF",
    "revision": "main"
  },
  "defaults": {
    "catalogModelId": "qwen3.6-35b-a3b-local",
    "routerModelId": "Qwen3.6-35B-A3B-GGUF",
    "runtimeProfileId": "qwen3_6",
    "targetDirectory": "Qwen3.6-35B-A3B-GGUF",
    "mmproj": {
      "path": "mmproj-F16.gguf"
    },
    "routerPreset": {
      "ctx-size": "131072",
      "image-min-tokens": "1024"
    }
  },
  "quantMetadata": {
    "recommendedLabels": ["UD-Q4_K_XL", "UD-Q5_K_XL", "Q4_K_M"],
    "guidance": {
      "UD-Q4_K_XL": { "summary": "Curator default. Best quality/size balance on Unsloth Dynamic 2.0." },
      "UD-Q5_K_XL": { "summary": "Higher quality when memory allows." },
      "Q6_K": { "summary": "Near-full quality; largest practical install." }
    }
  },
  "hardwareNotes": {
    "summary": "MoE with ~3B active parameters per token. Typical install: 24GB+ VRAM or large unified memory.",
    "contextClass": "large"
  }
}
```

### 4.14 Example MTP definition (`qwen3.6-35b-a3b-mtp`)

```json
{
  "schemaVersion": 1,
  "id": "qwen3.6-35b-a3b-mtp",
  "version": "2026-07-10",
  "display": {
    "name": "Qwen 3.6 35B A3B (MTP)",
    "description": "Text-first faster inference using llama.cpp draft-mtp.",
    "labels": ["Text", "Reasoning", "Tool use", "MTP"],
    "license": "Apache-2.0",
    "documentationUrl": "https://huggingface.co/unsloth/Qwen3.6-35B-A3B-MTP-GGUF"
  },
  "source": {
    "repository": "unsloth/Qwen3.6-35B-A3B-MTP-GGUF",
    "revision": "main"
  },
  "defaults": {
    "catalogModelId": "qwen3.6-35b-a3b-mtp-local",
    "routerModelId": "Qwen3.6-35B-A3B-MTP-GGUF",
    "runtimeProfileId": "qwen3_6",
    "targetDirectory": "Qwen3.6-35B-A3B-MTP-GGUF",
    "mmproj": null,
    "routerPreset": {
      "ctx-size": "131072",
      "spec-type": "draft-mtp",
      "spec-draft-n-max": "2"
    }
  },
  "quantMetadata": {
    "recommendedLabels": ["UD-Q4_K_XL"],
    "guidance": {
      "UD-Q4_K_XL": { "summary": "Unsloth-recommended Dynamic quant for MTP repos." }
    }
  },
  "hardwareNotes": {
    "summary": "Requires recent llama.cpp MTP support. Text-only in v1; do not enable vision on this row.",
    "contextClass": "large"
  }
}
```

### 4.15 Manifest delivery

- **Path:** `docker/build/guideants-ai/llama-admin-service/catalog/manifest.json`
- **Schema:** extend `docs/native-ai-migration/catalog/schema.model.json` with `task: "llama"`
- **Payload:** §4.16 complete `models[]` index (14 entries)
- **Endpoint:** llama-admin `GET /admin/catalog`
- **Proxy:** GuideAnts API `GET /api/settings/llama/catalog`
- **Tests:** one repository-resolution test per manifest entry pinning expected quant labels and
  shard groups; fail manifest CI when HF repo structure drifts

### 4.16 Complete v1 manifest entries

Authoritative `models[]` payload for `manifest.json`. Display names and descriptions follow the same
pattern as §4.13; this index lists every executable default the installer must apply.

```json
{
  "schemaVersion": 1,
  "task": "llama",
  "version": "2026-07-10",
  "models": [
    {
      "id": "qwen3.6-35b-a3b",
      "source": { "repository": "unsloth/Qwen3.6-35B-A3B-GGUF", "revision": "main" },
      "defaults": {
        "catalogModelId": "qwen3.6-35b-a3b-local",
        "routerModelId": "Qwen3.6-35B-A3B-GGUF",
        "runtimeProfileId": "qwen3_6",
        "targetDirectory": "Qwen3.6-35B-A3B-GGUF",
        "mmproj": { "path": "mmproj-F16.gguf" },
        "routerPreset": { "ctx-size": "131072", "image-min-tokens": "1024" }
      },
      "quantMetadata": { "recommendedLabels": ["UD-Q4_K_XL", "UD-Q5_K_XL", "Q4_K_M"] }
    },
    {
      "id": "qwen3.6-27b",
      "source": { "repository": "unsloth/Qwen3.6-27B-GGUF", "revision": "main" },
      "defaults": {
        "catalogModelId": "qwen3.6-27b-local",
        "routerModelId": "Qwen3.6-27B-GGUF",
        "runtimeProfileId": "qwen3_6",
        "targetDirectory": "Qwen3.6-27B-GGUF",
        "mmproj": { "path": "mmproj-F16.gguf" },
        "routerPreset": { "ctx-size": "131072", "image-min-tokens": "1024" }
      },
      "quantMetadata": { "recommendedLabels": ["UD-Q4_K_XL", "UD-Q5_K_XL", "Q4_K_M"] }
    },
    {
      "id": "qwen3.6-35b-a3b-mtp",
      "source": { "repository": "unsloth/Qwen3.6-35B-A3B-MTP-GGUF", "revision": "main" },
      "defaults": {
        "catalogModelId": "qwen3.6-35b-a3b-mtp-local",
        "routerModelId": "Qwen3.6-35B-A3B-MTP-GGUF",
        "runtimeProfileId": "qwen3_6",
        "targetDirectory": "Qwen3.6-35B-A3B-MTP-GGUF",
        "mmproj": null,
        "routerPreset": { "ctx-size": "131072", "spec-type": "draft-mtp", "spec-draft-n-max": "2" }
      },
      "quantMetadata": { "recommendedLabels": ["UD-Q4_K_XL"] }
    },
    {
      "id": "qwen3.6-27b-mtp",
      "source": { "repository": "unsloth/Qwen3.6-27B-MTP-GGUF", "revision": "main" },
      "defaults": {
        "catalogModelId": "qwen3.6-27b-mtp-local",
        "routerModelId": "Qwen3.6-27B-MTP-GGUF",
        "runtimeProfileId": "qwen3_6",
        "targetDirectory": "Qwen3.6-27B-MTP-GGUF",
        "mmproj": null,
        "routerPreset": { "ctx-size": "131072", "spec-type": "draft-mtp", "spec-draft-n-max": "2" }
      },
      "quantMetadata": { "recommendedLabels": ["UD-Q4_K_XL"] }
    },
    {
      "id": "qwen3.5-35b-a3b",
      "source": { "repository": "unsloth/Qwen3.5-35B-A3B-GGUF", "revision": "main" },
      "defaults": {
        "catalogModelId": "qwen3.5-35b-a3b-local",
        "routerModelId": "Qwen3.5-35B-A3B-GGUF",
        "runtimeProfileId": "qwen3_5",
        "targetDirectory": "Qwen3.5-35B-A3B-GGUF",
        "mmproj": { "path": "mmproj-F16.gguf" },
        "routerPreset": { "ctx-size": "131072", "image-min-tokens": "1024" }
      },
      "quantMetadata": { "recommendedLabels": ["UD-Q4_K_XL", "UD-Q5_K_XL", "Q4_K_M"] }
    },
    {
      "id": "qwen3.5-27b",
      "source": { "repository": "unsloth/Qwen3.5-27B-GGUF", "revision": "main" },
      "defaults": {
        "catalogModelId": "qwen3.5-27b-local",
        "routerModelId": "Qwen3.5-27B-GGUF",
        "runtimeProfileId": "qwen3_5",
        "targetDirectory": "Qwen3.5-27B-GGUF",
        "mmproj": { "path": "mmproj-F16.gguf" },
        "routerPreset": { "ctx-size": "131072", "image-min-tokens": "1024" }
      },
      "quantMetadata": { "recommendedLabels": ["UD-Q4_K_XL", "UD-Q5_K_XL", "Q4_K_M"] }
    },
    {
      "id": "qwen3.5-9b",
      "source": { "repository": "unsloth/Qwen3.5-9B-GGUF", "revision": "main" },
      "defaults": {
        "catalogModelId": "qwen3.5-9b-local",
        "routerModelId": "Qwen3.5-9B-GGUF",
        "runtimeProfileId": "qwen3_5",
        "targetDirectory": "Qwen3.5-9B-GGUF",
        "mmproj": { "path": "mmproj-F16.gguf" },
        "routerPreset": { "ctx-size": "65536", "image-min-tokens": "1024" }
      },
      "quantMetadata": { "recommendedLabels": ["UD-Q4_K_XL", "Q4_K_M"] }
    },
    {
      "id": "gemma4-31b",
      "source": { "repository": "unsloth/gemma-4-31B-it-qat-GGUF", "revision": "main" },
      "defaults": {
        "catalogModelId": "gemma4-31b-local",
        "routerModelId": "gemma-4-31B-it-qat-GGUF",
        "runtimeProfileId": "gemma4",
        "targetDirectory": "gemma-4-31B-it-qat-GGUF",
        "mmproj": { "path": "mmproj-F16.gguf" },
        "routerPreset": { "ctx-size": "131072", "image-min-tokens": "1024" }
      },
      "quantMetadata": { "recommendedLabels": ["UD-Q4_K_XL", "UD-Q5_K_XL"] }
    },
    {
      "id": "gemma4-26b-a4b",
      "source": { "repository": "unsloth/gemma-4-26B-A4B-it-GGUF", "revision": "main" },
      "defaults": {
        "catalogModelId": "gemma4-26b-a4b-local",
        "routerModelId": "gemma-4-26B-A4B-it-GGUF",
        "runtimeProfileId": "gemma4",
        "targetDirectory": "gemma-4-26B-A4B-it-GGUF",
        "mmproj": { "path": "mmproj-F16.gguf" },
        "routerPreset": { "ctx-size": "131072", "image-min-tokens": "1024" }
      },
      "quantMetadata": { "recommendedLabels": ["UD-Q4_K_XL", "UD-Q5_K_XL"] }
    },
    {
      "id": "gemma4-12b",
      "source": { "repository": "unsloth/gemma-4-12b-it-GGUF", "revision": "main" },
      "defaults": {
        "catalogModelId": "gemma4-12b-local",
        "routerModelId": "gemma-4-12b-it-GGUF",
        "runtimeProfileId": "gemma4",
        "targetDirectory": "gemma-4-12b-it-GGUF",
        "mmproj": { "path": "mmproj-F16.gguf" },
        "routerPreset": { "ctx-size": "131072", "image-min-tokens": "1024" }
      },
      "quantMetadata": { "recommendedLabels": ["UD-Q4_K_XL", "Q4_K_M"] }
    },
    {
      "id": "gemma4-e4b",
      "source": { "repository": "unsloth/gemma-4-E4B-it-GGUF", "revision": "main" },
      "defaults": {
        "catalogModelId": "gemma4-e4b-local",
        "routerModelId": "gemma-4-E4B-it-GGUF",
        "runtimeProfileId": "gemma4",
        "targetDirectory": "gemma-4-E4B-it-GGUF",
        "mmproj": { "path": "mmproj-F16.gguf" },
        "routerPreset": { "ctx-size": "65536", "image-min-tokens": "1024" }
      },
      "quantMetadata": { "recommendedLabels": ["UD-Q4_K_XL", "Q4_K_M"] }
    },
    {
      "id": "gpt-oss-20b",
      "source": { "repository": "ggml-org/gpt-oss-20b-GGUF", "revision": "main" },
      "defaults": {
        "catalogModelId": "gpt-oss-20b-local",
        "routerModelId": "gpt-oss-20b-GGUF",
        "runtimeProfileId": "gpt_oss",
        "targetDirectory": "gpt-oss-20b-GGUF",
        "mmproj": null,
        "routerPreset": { "ctx-size": "131072" }
      },
      "quantMetadata": { "recommendedLabels": ["Q4_K_M", "Q5_K_M", "Q8_0"] }
    },
    {
      "id": "deepseek-r1-14b",
      "source": { "repository": "unsloth/DeepSeek-R1-Distill-Qwen-14B-GGUF", "revision": "main" },
      "defaults": {
        "catalogModelId": "deepseek-r1-14b-local",
        "routerModelId": "DeepSeek-R1-Distill-Qwen-14B-GGUF",
        "runtimeProfileId": "deepseek_r1",
        "targetDirectory": "DeepSeek-R1-Distill-Qwen-14B-GGUF",
        "mmproj": null,
        "routerPreset": { "ctx-size": "131072" }
      },
      "quantMetadata": { "recommendedLabels": ["Q4_K_M", "UD-Q4_K_XL", "Q5_K_M"] }
    },
    {
      "id": "qwen3-coder-30b",
      "source": { "repository": "unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF", "revision": "main" },
      "defaults": {
        "catalogModelId": "qwen3-coder-30b-local",
        "routerModelId": "Qwen3-Coder-30B-A3B-Instruct-GGUF",
        "runtimeProfileId": "qwen3_coder",
        "targetDirectory": "Qwen3-Coder-30B-A3B-Instruct-GGUF",
        "mmproj": null,
        "routerPreset": { "ctx-size": "131072" }
      },
      "quantMetadata": { "recommendedLabels": ["UD-Q4_K_XL", "UD-Q5_K_XL", "Q4_K_M"] }
    }
  ]
}
```

### 4.17 Parameter ownership matrix

| Parameter | Owner | v1 value |
| --- | --- | --- |
| Fleet globals (`jinja`, `parallel`, `threads`, `flash-attn`, …) | Fleet llama settings (SQL) + UI | §4.7.3, §6.4 |
| Per-alias llama-server argv (`ctx-size`, `image-min-tokens`, MTP, …) | `defaults.routerPreset` → INI via §5.8 | Per curated entry |
| Sampling (`temperature`, `top_p`, `top_k`, penalties) | Runtime profile | Per family (§4.11 + existing bootstrap) |
| Thinking / reasoning effort | Runtime profile `thinkingControlJson` | Per family |
| `parallel_tool_calls` | Runtime profile `requestFieldsWhenToolsPresent` | `true` except `deepseek_r1` (`false`) |
| `ctx-size` | Curated `defaults.routerPreset` → INI | `131072` or `65536` per §4.10 |
| `image-min-tokens` | Curated `defaults.routerPreset` → INI | `1024` on vision Qwen/Gemma only |
| `spec-type` / `spec-draft-n-max` | Curated `defaults.routerPreset` → INI | MTP rows only |
| `mmproj` path | Curated `defaults.mmproj` → INI | `mmproj-F16.gguf` or empty |
| `cache-ram` | Per-alias preset (B) or fleet default when omitted | Operator-tunable in Customize |
| Quant file selection | HF API at install | User choice; curator labels only |
| Router alias / model paths | Installation record + INI | Resolved at install |

---

## 5. API contracts and installation flow

### 5.1 Catalog endpoints

Follow the existing local-model catalog pattern used by embeddings, ASR, and TTS.

```http
GET /api/settings/llama/catalog
```

Returns curated definitions available to the UI. The GuideAnts API proxies llama-admin
`GET /admin/catalog`.

```http
GET /api/settings/llama/catalog/{catalogId}/quants
```

The server:

1. resolves the curated definition;
2. reads the declared HF repository/revision;
3. captures the resolved commit;
4. groups complete model artifact sets;
5. resolves the declared projector;
6. enriches quant rows with authored `quantMetadata`;
7. returns the transient response described in §4.3.

This endpoint requires the configured Hugging Face token when the repository is gated.

### 5.2 Curated install request

Extend the authoritative `POST /api/settings/models:add` contract:

```json
{
  "provider": "llama-cpp",
  "catalog": {
    "displayName": "Qwen 3.6 35B A3B",
    "isActive": true
  },
  "install": {
    "source": "curated",
    "catalogId": "qwen3.6-35b-a3b-mtp",
    "catalogVersion": "2026-07-10",
    "quantId": "q6_k_xl",
    "resolvedRevision": "8f4c3f1a..."
  }
}
```

The client sends identities, not repository paths or presets. The server re-resolves the definition
and selected quant at the supplied commit, verifies that the commit and complete artifact group still
exist, and builds the installation command from authoritative catalog content.

Catalog model ID, router alias, target directory, runtime profile, projector, and router preset are
derived from the definition. The API rejects conflicts; it does not rename identities silently.

### 5.3 Resolution result

Before download starts, the server creates one immutable operation input:

```json
{
  "definitionId": "qwen3.6-35b-a3b-mtp",
  "definitionVersion": "2026-07-10",
  "repository": "example/Qwen3.6-35B-A3B-GGUF",
  "resolvedRevision": "8f4c3f1a...",
  "modelFiles": [
    "Qwen3.6-35B-A3B-Q6_K_XL-00001-of-00002.gguf",
    "Qwen3.6-35B-A3B-Q6_K_XL-00002-of-00002.gguf"
  ],
  "mmprojFiles": ["mmproj-F16.gguf"],
  "routerModelId": "Qwen3.6-35B-A3B-MTP-GGUF",
  "runtimeProfileId": "qwen3_6",
  "routerPreset": {
    "ctx-size": "131072",
    "image-min-tokens": "1024",
    "spec-type": "draft-mtp",
    "spec-draft-n-max": "2"
  }
}
```

All subsequent operation stages use this object. They do not re-query `main` or recalculate file
groups during the same operation.

### 5.4 Curated install sequence

```
UI selects catalogId + quantId
  → API resolves catalog definition and HF commit
  → API validates complete quant group + projector + runtime profile
  → llama-admin downloads every exact artifact
  → llama-admin writes one complete router INI section
  → API creates Models row with minimal RuntimeConfigJson
  → API writes LocalModelInstallation provenance
  → operation reports completed
```

Catalog registration must occur only after all artifacts and the router entry have succeeded. If a
later database write fails, the operation reports the exact partial state and remediation; it does
not report completion.

### 5.5 Custom install request

The custom path continues through `POST /api/settings/models:add`, but its contract must support:

- explicit repository revision;
- explicit ordered `modelFiles`, not one include pattern;
- optional explicit projector files;
- explicit runtime profile;
- full router preset.

The existing include-pattern download API may remain internal for compatibility, but curated and new
custom UIs should submit resolved artifact groups so sharded models are first-class.

### 5.6 Change quant

Changing quant is not a catalog text edit. It is an installation operation:

1. query the definition's current quant groups;
2. select a new group;
3. resolve and record its commit;
4. download into a staging location;
5. validate the complete artifact set;
6. unload the alias if loaded;
7. replace artifact paths and the router section together;
8. update installation provenance;
9. reload the alias if it was previously loaded;
10. remove obsolete artifacts only after successful activation.

The catalog model ID and runtime profile remain unchanged unless the selected curated definition
version explicitly changes them.

### 5.7 Repair

Repair uses the recorded commit and exact artifact list. It does not browse the current repository
head. It verifies files, re-downloads missing/corrupt artifacts, rewrites the recorded router preset
to INI via llama-admin (§5.8), and confirms the alias can be loaded.

### 5.8 Router preset admin API

Extend llama-admin so GuideAnts can read and write the full per-alias llama-server preset, not only
`ctx-size` / `cache-ram`.

#### `GET /router/entries`

Add `preset` to each entry — the complete extras map from INI (excluding `model` / `mmproj` paths):

```json
{
  "alias": "Qwen3.6-35B-A3B-GGUF",
  "modelPath": "/models-local/llama/.../shard-00001-of-00002.gguf",
  "mmprojPath": "/models-local/llama/.../mmproj-F16.gguf",
  "hasModelFile": true,
  "hasMmprojFile": true,
  "contextSize": 131072,
  "cacheRamMib": 8192,
  "preset": {
    "ctx-size": "131072",
    "image-min-tokens": "1024"
  }
}
```

`contextSize` / `cacheRamMib` remain as convenience projections of `preset` for backward
compatibility.

#### `POST /router/entries`

Replace the ctx/cache-only body with:

```json
{
  "alias": "Qwen3.6-35B-A3B-GGUF",
  "modelPath": "/models-local/llama/.../shard-00001-of-00002.gguf",
  "mmprojPath": "/models-local/llama/.../mmproj-F16.gguf",
  "preset": {
    "ctx-size": "131072",
    "image-min-tokens": "1024"
  },
  "presetMode": "replace"
}
```

| Field | Required | Semantics |
| --- | --- | --- |
| `alias` | yes | Router section name |
| `modelPath` | yes | Container path to GGUF (or first shard per contract) |
| `mmprojPath` | no | Empty string clears projector |
| `preset` | no | Per-alias llama-server preset keys (§4.7.4) |
| `presetMode` | no | `replace` (default on curated install) or `merge` (custom patch) |

Validation:

- reject **fleet-scoped** keys in alias `preset` (redirect to fleet settings);
- reject **alias-scoped** keys in fleet settings;
- validate value types per key;
- on `replace`, remove prior preset extras for that alias before applying `preset`;
- on `merge`, upsert keys present in `preset` and leave other extras untouched.

After commit, trigger `signal_llama_server_reload()` as today.

#### GuideAnts API proxy

```http
GET  /api/settings/llama/router/entries
PUT  /api/settings/llama/router/entries/{alias}
```

The PUT body matches llama-admin. Inventory and installed-model detail endpoints include
`routerPreset` sourced from INI (not duplicated in SQL).

#### Download completion hook

When a curated or custom download reaches `registeringAlias`, llama-admin must call
`upsert_router_entry` with `modelPaths`, `mmprojPath`, and the resolved `preset` from the operation
input — not paths alone as today.

---

## 6. Runtime behavior and precedence

### 6.1 Router spawn

The router starts once:

```text
llama-server --models-preset /models-local/router-models.ini + fleet base preset flags
```

Loading an alias spawns a child from that alias's INI section. Verified behavior includes
`--spec-type draft-mtp --spec-draft-n-max 2` when those keys are present.

Router preset changes do not affect an already-running child until unload/load or router restart.

### 6.2 Model switching

```
NotebookModelRuntimeService
  → unload aliases not required
  → POST /models/load { "model": "<alias>" }
  → router spawns child from INI
  → GA_LLAMA_MODELS_MAX=1 keeps one loaded model
```

All load paths use alias-only bodies. `loadParams` is removed.

### 6.3 Profile application

On each chat completion:

1. catalog routing resolves `routerModelId` and `runtimeProfileId`;
2. the runtime profile contributes sampling defaults and model-family request actions;
3. guide/assistant overrides are validated against profile definitions;
4. thinking actions modify concrete request fields/messages;
5. tool-request fields are applied only when tools are present;
6. the request is sent to the selected llama alias.

Runtime profiles never modify `router-models.ini`.

### 6.4 Fleet llama runtime settings (layer A)

Fleet-wide router defaults are persisted in SQL and edited through Settings → Models & Runtime →
**Fleet llama server**. They are **not** operator-maintained in `docker-compose.yml`.

```json
{
  "jinja": true,
  "parallel": 5,
  "threads": 16,
  "kvUnified": true,
  "contBatching": true,
  "flashAttn": "on"
}
```

On save, GuideAnts writes settings, applies them to the llama container environment for the next
router restart, and triggers reload. `start-llama.sh` translates to CLI flags.

Installer compose retains `GA_LLAMA_*` as **bootstrap seeds** when DB settings are empty:

```yaml
- GA_LLAMA_JINJA=1
- GA_LLAMA_PARALLEL=5
- GA_LLAMA_THREADS=16
```

Per llama.cpp preset precedence: router CLI base preset (from fleet settings) wins over alias INI
on key collision. Alias-scoped keys (`ctx-size`, `image-min-tokens`, …) must not appear in fleet
settings.

`parallel` is server slot count. It is unrelated to chat `parallel_tool_calls`.

### 6.5 Common router keys

| Key | Example | Purpose |
| --- | --- | --- |
| `ctx-size` | `131072` | Per-alias allocated context |
| `cache-ram` | `8192` | Prompt cache RAM (MiB) |
| `spec-type` | `draft-mtp` | Enable MTP speculative decoding |
| `spec-draft-n-max` | `2` | Draft tokens per MTP step |
| `image-min-tokens` | `1024` | Qwen-VL grounding floor |
| `n-gpu-layers` | `99` | GPU layer offload |
| `flash-attn` | `on` | Flash attention |

Curated definitions are schema-validated before release. llama-admin validates keys and values before
restarting the router. A child-spawn failure is reported against the curated definition and preset
key; it is never converted into a different configuration.

---

## 7. Migration

### 7.1 Existing catalog fields

| Current field | Target |
| --- | --- |
| `routerContextSize` | Write `ctx-size` into the existing INI section, then remove from SQL JSON |
| `routerCacheRamMib` | Write `cache-ram` into the existing INI section, then remove from SQL JSON |
| `loadParams.model` | Remove; all load operations use the router alias |
| Other `loadParams` keys | Require explicit reviewed mapping to valid router-preset keys |
| `parallelToolCalls` | Move to concrete runtime-profile tool request policy |
| Hand-edited INI extras | Preserve exactly; mark installation as operator-managed unless matched to a curated definition |

Do not map arbitrary `loadParams` keys automatically. Produce a migration report for keys without an
explicit mapping.

### 7.2 Existing downloaded models

Existing aliases are not automatically declared curated. Provide an adoption action:

1. compare artifact paths and runtime profile to a selected curated definition;
2. resolve repository provenance when known;
3. show every difference;
4. let the operator either adopt the curated definition or remain operator-managed.

No repository, revision, or quant provenance is invented.

### 7.3 Parallel-tool migration

If all catalog rows referencing a profile have the same explicit `parallelToolCalls` value, migrate
that value into the profile's concrete `requestFieldsWhenToolsPresent`.

If rows disagree, do not change behavior silently. Create an explicit migration conflict requiring
separate profiles or continued operator-managed configuration before the row-level property can be
removed.

### 7.4 UI migration

- Curated picker replaces free-form HF as the default add path.
- Current HF browser moves under Custom.
- Context/cache fields disappear from normal add/edit forms.
- Load params JSON is removed.
- Parallel tool calls disappear from the model row.
- Runtime profile creation/editing remains in Runtime Profiles.
- Router preset editing exists only under Custom/Customize.
- Current inventory is extended to show full preset and installation provenance.

---

## 8. Implementation plan

### Phase 1 — catalog and discovery

1. Add llama task support to the existing curated model manifest/schema or add a llama-specific
   schema where router defaults require it.
2. Publish the v1 manifest with all **14** curated entries from §4.16.
3. Add bootstrap runtime profiles `deepseek_r1` and `qwen3_coder` (§4.11).
4. Add llama-admin `GET /admin/catalog`.
5. Add GuideAnts API `GET /api/settings/llama/catalog`.
6. Extend HF repository listing to return resolved commit and complete quant artifact groups.
7. Support sharded GGUF groups end-to-end.
8. Add schema and **14** repository-resolution tests (one per manifest entry).

### Phase 2 — curated install

1. Extend `AddModelInstallDto` with `source: "curated"`, catalog/version/quant identities, and
   resolved revision.
2. Resolve the complete immutable operation input server-side.
3. Change llama-admin download transport from one quant pattern to exact ordered artifact lists.
4. **Extend llama-admin router upsert with full `preset` map (§5.8).**
5. Extend router-entry upsert with full `preset` and validated model/projector paths.
6. Persist installation provenance including `routerPreset`.
7. Register the catalog row only after complete runtime registration (paths + INI preset).

### Phase 3 — runtime configuration cleanup

1. Extend runtime profiles with concrete tool request fields (`parallel_tool_calls` per §4.11).
2. Add `deepseek_r1` and `qwen3_coder` profiles to bootstrap resources.
3. Move `parallelToolCalls` according to §7.3.
4. **Add persisted fleet llama runtime settings + `/api/settings/llama/runtime/fleet-preset` + UI (§4.7.3, §6.4).** Migrate bootstrap `GA_LLAMA_*` to DB on first read.
5. Remove `loadParams` and align every load path to alias-only requests.
6. Move SQL context/cache values into INI.
7. Shrink `LocalRuntimeConfiguration` to `RouterModelId` and `RuntimeProfileId`.
8. Extend inventory DTOs to return full router preset, fleet settings summary, and provenance.

### Phase 4 — frontend

1. Build `LlamaCuratedModelPicker`.
2. Build quant-group selection with shard and byte totals.
3. Add curated review/progress/completion screens to both onboarding surfaces.
4. Move the existing repository picker into Custom.
5. Add alias preset editing to Custom/Customize; add **Fleet llama server** settings panel.
6. Replace the current catalog edit form with installed-model summary/actions.
7. Add change-quant, repair, and curated-adoption workflows.
8. Preserve Settings/Home onboarding parity tests.

### Phase 5 — migration and release

1. Run the deterministic field migration.
2. Surface unresolved `loadParams` and parallel-tool conflicts.
3. Keep existing models operator-managed until explicitly adopted.
4. Publish initial curated definitions (§4.16) with pinned tests against their HF repository structures.
5. Validate download, load, chat, tools, reasoning, vision, MTP, restart, repair, and quant replacement
   across representative entries: `qwen3.6-35b-a3b`, `qwen3.6-35b-a3b-mtp`, `gemma4-31b`,
   `deepseek-r1-14b`, `qwen3-coder-30b`, `gpt-oss-20b`.

---

## 9. Acceptance criteria

### V1 catalog completeness

- Manifest ships **14** curated models in one release (§4.12, §4.16).
- Every entry specifies `runtimeProfileId`, `routerPreset`, `mmproj`, and `quantMetadata`.
- Vision models set per-alias `image-min-tokens=1024`; MTP models set `spec-type`/`spec-draft-n-max`
  and omit `mmproj`.
- New profiles `deepseek_r1` and `qwen3_coder` include vendor-aligned sampling and thinking defaults.
- Parameter ownership matches §4.17 (no executable capability booleans).

### Curated UX

- A user can install a curated llama model by selecting only the model and quant.
- Quant rows come from current contents of the definition's repository through the API.
- Complete sharded quants are selectable as one row.
- No runtime profile, projector, alias, context, cache, load JSON, tool-policy, or preset selection is
  required.
- Review shows the resolved commit and exact artifacts before install.

### Definition behavior

- Curated definitions contain repository/defaults but no duplicated quant file array.
- Display labels never drive runtime behavior.
- Runtime behavior is represented by concrete profile actions/request fields and router keys.
- Repository discovery never mutates a curated definition.
- Definition changes are versioned.

### Runtime integrity

- `RuntimeConfigJson` contains only router/profile identity.
- Fleet llama-server argv is editable in Settings without compose changes (§2.4, §6.4).
- Per-alias llama-server argv is editable in Customize without INI hand edits (§5.8).
- The full source/quant selection is recoverable from installation provenance.
- Repair uses the recorded commit and files.
- Invalid or changed repository content produces an actionable failure.
- No model identity, quant, projector, profile, or preset is silently substituted.

### Extensibility

- Custom repositories remain installable through the explicit advanced flow.
- Custom installs support sharded GGUFs.
- A custom installation can expose every valid router preset key without adding catalog columns.
- New curated models normally require manifest/profile content, not new UI fields.
- New llama.cpp switches are added through fleet settings (layer A) or alias preset (layer B) — never
  as an operator compose edit.

---

## 10. Out of scope

- Requiring `docker-compose.yml` / `.env` edits for normal llama-server tuning (defect; see §2.4).
- Automatically selecting a quant.
- Automatically selecting a runtime profile from filenames or model names.
- Inferring executable behavior from labels such as Vision, Reasoning, or Tool use.
- Multi-model parallel load; `GA_LLAMA_MODELS_MAX=1` remains unchanged.
- Automatically updating an installed model when an HF repository or curated definition changes.
