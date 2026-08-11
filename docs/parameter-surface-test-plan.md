# Parameter Surface Test Plan (Row-Owned Catalog Contract)

> **Universal contract (all providers):** [model-chat-behavior-contract.md](model-chat-behavior-contract.md)

Last updated: 2026-07-30

## 1. Purpose

Qualify the row-owned parameter surface contract end to end:

1. Catalog rows own parameter surfaces (`SamplingParametersJson`, `ReasoningChoicesJson`; llama also owns full chat-behavior JSON).
2. Settings Add Model / catalog edit edit those fields directly. There is no runtime-profile picker in Settings.
3. Settings Overview, Guide Builder, and catalog edit flows render controls from catalog API surfaces.
4. Chat execution honors configured sampling and reasoning values per provider family.

Configuration of empty or missing surfaces is **part of this test**, not a prerequisite done outside it.

Contract reference: `docs/model-sampling-policy-regression-fix.md`.

## 2. Scope

### In scope

| Layer | What |
|-------|------|
| **Phase 0** | Restore baseline, runtime prep, open playwright-cli Chrome extension session |
| **Phase 1** | Configure catalog surfaces through Settings UI (agent drives `playwright-cli`; operator observes in Chrome) |
| **Phase 2** | UI rendering — Overview sliders, catalog edit round-trip, Guide Builder params |
| **Phase 3** | Live chat smoke — one turn per surface shape, params reach provider |
| **Phase 4** | Automated contract tests (CI) — server + client unit tests (no browser) |

### Out of scope

- Provider connection setup (OpenRouter, Foundry, HF, LlamaCpp must already be configured in baseline).
- Non-chat services (embeddings, image, TTS, ASR) — see `docs/full-provider-test-plan.md`.
- Runtime profile column (removed from catalog table).

## 3. Hard Rules

1. **Step 1 of plan setup (once): create the baseline backup** — snapshot the DB in its pre-test starting state (Section 4.2). Do this before the first test run.
2. **Step 1 of every test run: restore baseline** — each run starts from that snapshot, not from whatever the last run left behind (Section 4.3).
3. **Phases 1–3 use `playwright-cli --extension`** — agent drives the operator's real Chrome tab; operator observes and consents (Section 5). No headless browser, no direct API/DB for UI steps.
4. **Configure through Settings UI** during Phase 1 — no direct SQL updates to `Models` for surface fields.
5. **No model substitutions** — use exact model IDs in Section 7.
6. **Empty surfaces are test cases** — configuring them in Phase 1 is a pass/fail step, not a pre-test fix.
7. **Stop on defect** — if save, reload, or chat fails after one clean retry, classify and report with evidence.
8. **Baseline assumes migration already applied** — `20260730173356_BackfillNonLocalModelRowAuthority` is on `guideants-dev-open-router-tests` (confirmed 2026-07-30). New environments must run `dotnet ef database update` once before creating the baseline backup.

## 4. Database

### 4.1 Environment (current dev)

| Setting | Value |
|---------|-------|
| Server | `localhost,1434` |
| Database | `guideants-dev-open-router-tests` |
| Container | `guideants-mssql-express-1` |
| SA password | `YourStrong!Passw0rd` (from compose default) |
| API | `http://localhost:5107` (check `docker ps` for mapped port) |

### 4.2 Step 1 — Create baseline backup (once, before first test run)

Snapshot the DB in the state you want every run to start from: provider connections configured, catalog matching Section 7.1 (**before** Phase 1 configuration — empty OpenRouter surfaces included). On the current dev DB, `BackfillNonLocalModelRowAuthority` is already applied — no migration step needed before this backup.

```powershell
docker exec guideants-mssql-express-1 /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "YourStrong!Passw0rd" -C -Q "
BACKUP DATABASE [guideants-dev-open-router-tests]
  TO DISK = N'/var/opt/mssql/data/guideants-parameter-surface-baseline.bak'
  WITH COPY_ONLY, INIT, COMPRESSION, CHECKSUM;
"
```

Copy off-container for safekeeping:

```powershell
docker cp guideants-mssql-express-1:/var/opt/mssql/data/guideants-parameter-surface-baseline.bak .\output\db\
```

Re-create this baseline only when the intentional starting catalog changes (new provider, migration, etc.).

### 4.3 Step 1 of each test run — Restore baseline

```powershell
docker exec guideants-mssql-express-1 /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "YourStrong!Passw0rd" -C -Q "
ALTER DATABASE [guideants-dev-open-router-tests] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
RESTORE DATABASE [guideants-dev-open-router-tests]
  FROM DISK = N'/var/opt/mssql/data/guideants-parameter-surface-baseline.bak'
  WITH REPLACE, RECOVERY, CHECKSUM;
ALTER DATABASE [guideants-dev-open-router-tests] SET MULTI_USER;
"
```

Recreate the webapi container or bounce the app if the restore happens while it is running (connection pool may hold stale state).

### 4.4 Optional — Forensic backup after a failed run

Only if you need to inspect a mutated DB before the next restore. Not part of the normal run sequence.

```powershell
$stamp = Get-Date -Format "yyyyMMdd-HHmm"
docker exec guideants-mssql-express-1 /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "YourStrong!Passw0rd" -C -Q "
BACKUP DATABASE [guideants-dev-open-router-tests]
  TO DISK = N'/var/opt/mssql/data/guideants-parameter-surface-failed-$stamp.bak'
  WITH COPY_ONLY, INIT, COMPRESSION, CHECKSUM;
"
```

## 5. Browser automation — playwright-cli + Chrome extension

Phases 1–3 are executed interactively: **you watch in Chrome**, the agent drives via **`playwright-cli --extension`**. Same pattern as `docs/sandbox-wire-api/e2e-test-plan.md` §9 and `docs/skills-execution/ui-testing-plan.md` §4.

### 5.1 Tooling

```powershell
playwright-cli --version
# If missing: npm install -g @playwright/cli@latest
```

### 5.2 Session

| Property | Value |
|----------|-------|
| Session name | `parameter-surface-test` (or reuse an already-connected extension session — see below) |
| Browser | Chrome (Playwright extension — operator's real profile) |
| App URL | `http://localhost:5107/` |

**Reuse an existing session** (preferred if Chrome is already connected to playwright-cli):

```powershell
playwright-cli list
# If your session is already open (any name), reconnect with that name:
playwright-cli -s=<your-existing-session> snapshot
```

Use `parameter-surface-test` as the canonical name for this plan when starting fresh.

**First open** (approve extension connection in Chrome when prompted):

```powershell
playwright-cli -s=parameter-surface-test open --extension http://localhost:5107/
```

**Reconnect** to an already-open session (preferred on reruns — keeps your login):

```powershell
playwright-cli -s=parameter-surface-test snapshot
```

If disconnected, reopen with the same `-s=parameter-surface-test` name.

```powershell
playwright-cli list
playwright-cli -s=parameter-surface-test close   # end of run only
```

### 5.3 Agent workflow commands

Use refs from the latest `snapshot` output (`e19`, etc.):

```powershell
# State capture (primary evidence — YAML under .playwright-cli/)
playwright-cli -s=parameter-surface-test snapshot
playwright-cli -s=parameter-surface-test snapshot --filename=param-surface-01-config-gemma-or.yaml

# Navigation
playwright-cli -s=parameter-surface-test goto http://localhost:5107/settings
playwright-cli -s=parameter-surface-test click "role=button[name='Models & Runtime']"
playwright-cli -s=parameter-surface-test click "role=button[name='Open Settings']"

# Interact
playwright-cli -s=parameter-surface-test click e42
playwright-cli -s=parameter-surface-test fill e79 "minimax/minimax-m3"
playwright-cli -s=parameter-surface-test press Enter

# Failures
playwright-cli -s=parameter-surface-test console
playwright-cli -s=parameter-surface-test network
```

Snapshot artifacts: `.playwright-cli/` (gitignored). Name files per Section 12 evidence table.

**Pacing:** agent snapshots before and after each matrix step so the operator can follow along in the visible Chrome tab. Pause for operator confirmation on destructive steps (DB restore, delete model) if requested.

### 5.4 Settings routes (automation hints)

| Surface | Navigation |
|---------|------------|
| Settings Overview | `/settings` → Overview tab |
| Catalog | `/settings` → Models & Runtime → Catalog |
| Add model wizard | Catalog → **+ Add Model** |
| Catalog row edit | Catalog → pencil icon → **Sampling Parameters JSON** + **Reasoning Choices** fields (no template dropdown) |
| Notebook chat (Phase 3) | Open Creative Guide (or fixed test notebook) → header Chat panel |

## 6. Runtime Setup

Before Phase 1:

1. **Restore baseline** (Section 4.3).
2. Confirm API healthy: `http://localhost:5107` (Settings loads).
3. **Open or reconnect playwright-cli session** (Section 5.2).
4. Log in via Chrome if the extension session is not already authenticated.
5. Capture API logs under `output/logs/parameter-surface-<stamp>/`.
6. Confirm provider connections ready: OpenRouter, AzureOpenAI (Foundry), HuggingFace, LlamaCpp.

### 6.1 Run reset checklist

1. **Restore baseline** (Section 4.3).
2. Restart or verify `guideants-webapi-ui` container.
3. `playwright-cli -s=parameter-surface-test snapshot` (reconnect) or `open --extension` if cold start.
4. `goto http://localhost:5107/settings` and confirm Overview loads.
5. Proceed to Phase 1.

## 7. Parameter Surface Shapes and Model Matrix

Six client template seeds plus llama-cpp runtime profiles define distinct UI/API contracts:

| Shape | Seed / profile | Sampling | Reasoning | Example model |
|-------|----------------|----------|-----------|---------------|
| **A** | `openai_chat_standard` | temperature, top_p | none | `minimax/minimax-m3` |
| **B** | `openai_responses_reasoning` | none | none/low/med/high/xhigh | `openai/gpt-5.5` (OR) |
| **B′** | `openai_responses_reasoning` (variant) | none | minimal/low/med/high | `gpt-5.5` (azure-responses) |
| **C** | `anthropic_standard` | temp (0–1), top_p | minimal/low/med/high | `anthropic/claude-sonnet-4.5` |
| **D** | `google_gemini_25_flash` | temp, top_p (default 0.95) | low/med/high | `google/gemini-2.5-flash` |
| **E** | `qwen3_6` (llama profile) | temp, top_p, top_k, min_p, presence_penalty | none/enabled/low/med/high | `qwen3.6-35b-a3b-mtp-local` |
| **F** | `gemma4` (llama profile) | temp, top_p, top_k | none/enabled | `gemma-4-12B-it-qat-GGUF` |
| **G** | manual / custom | none or minimal | model-specific | `nvidia/nemotron-3-ultra-550b-a55b:free` |
| **H** | `openai_chat_standard` (+ optional top_k) | extended sampling | optional | `xiaomi/mimo-v2.5`, `google/gemma-4-31b-it` |

### 7.1 Baseline catalog (after restore, before Phase 1)

Expected starting rows (surfaces may be empty — that is intentional):

| ModelId | Provider | Pre-test sampling | Pre-test reasoning |
|---------|----------|-------------------|---------------------|
| `gpt-4.1` | `azure-openai-chat` | has | empty |
| `gpt-5.5` | `azure-openai-responses` | empty | has |
| `zai-org/GLM-5.2` | `hf-inference-chat` | has | empty |
| `gemma-4-12B-it-qat-GGUF` | `llama-cpp` | has | has |
| `qwen3.6-27b-mtp-local` | `llama-cpp` | has | has |
| `qwen3.6-35b-a3b-mtp-local` | `llama-cpp` | has | has |
| `minimax/minimax-m3` | `openrouter-chat` | has | empty |
| `google/gemma-4-31b-it` | `openrouter-chat` | **empty** | empty |
| `nvidia/nemotron-3-ultra-550b-a55b:free` | `openrouter-chat` | **empty** | empty |
| `xiaomi/mimo-v2.5` | `openrouter-chat` | **empty** | empty |

### 7.2 Target catalog (after Phase 1)

All rows configured; additional models added where noted:

| ModelId | Provider | Shape | Phase 1 action |
|---------|----------|-------|----------------|
| `minimax/minimax-m3` | `openrouter-chat` | A | Verify only |
| `google/gemma-4-31b-it` | `openrouter-chat` | H | Edit → paste shape **A** JSON → save |
| `xiaomi/mimo-v2.5` | `openrouter-chat` | H | Edit → paste shape **A** JSON → save |
| `nvidia/nemotron-3-ultra-550b-a55b:free` | `openrouter-chat` | G | Edit → `{}` sampling, reasoning `medium,high` → save |
| `openai/gpt-5.5` | `openrouter-chat` | B | Add wizard → optional template or shape **B** JSON |
| `anthropic/claude-sonnet-4.5` | `openrouter-chat` | C | Add wizard → optional template or shape **C** JSON |
| `google/gemini-2.5-flash` | `openrouter-chat` | D | Add wizard → optional template or shape **D** JSON |
| `gpt-4.1` | `azure-openai-chat` | A | Verify unchanged |
| `gpt-5.5` | `azure-openai-responses` | B′ | Verify unchanged |
| `zai-org/GLM-5.2` | `hf-inference-chat` | A | Verify unchanged |
| `qwen3.6-35b-a3b-mtp-local` | `llama-cpp` | E | Verify unchanged |
| `gemma-4-12B-it-qat-GGUF` | `llama-cpp` | F | Verify unchanged |

OpenRouter model metadata (for Shape G nemotron): supports `temperature`, `top_p`, and reasoning efforts **high** and **medium** only.

### 7.3 UI surfaces — edit vs add (read this before Phase 1)

Shape names in this plan (`openai_chat_standard`, `anthropic_standard`, etc.) refer to the JSON in `src/client/src/pages/settings/parameterSurfaceSeeds.ts`. They are **what to write on the model row**, not a control to hunt for in the edit modal.

| Flow | UI |
|------|-----|
| **Catalog edit** (pencil, non-llama) | **Sampling Parameters JSON** + **Reasoning Choices**; OpenRouter and HF rows also show optional **Thinking Control JSON** + **Extra Request Fields JSON** |
| **Catalog edit** (pencil, llama) | Full **Model chat behavior** editor (sampling, reasoning, thinking control, tools fields, combine/pattern) |
| **Add Model** wizard (cloud) | Same sampling/reasoning fields; model id typeahead may pre-fill from `knownCloudModels.json` |
| **Add Model** wizard (llama custom HF / attach) | Same model chat behavior editor — no runtime profile control |

**Edit-path steps (gemma, mimo, nemotron):**

1. Snapshot → assert empty/default (`{}` sampling, empty reasoning).
2. Paste or type the target shape into **Sampling Parameters JSON** / **Reasoning Choices** (Section 7.4).
3. Save → re-open edit → assert fields persisted.
4. Snapshot after save.

### 7.4 Shape reference (copy into edit modal or add wizard)

Source of truth: `parameterSurfaceSeeds.ts`. Minimal shapes for Phase 1:

**Shape A (`openai_chat_standard`)** — gemma-or, mimo:

- **Sampling Parameters JSON:** object with `temperature` and `top_p` keys (each a full slider definition — copy from seed file or from an existing row such as `minimax/minimax-m3`).
- **Reasoning Choices:** leave empty.

**Shape G (nemotron)**:

- **Sampling Parameters JSON:** `{}`
- **Reasoning Choices:** `medium, high`

**Shapes B/C/D (add wizard only in this matrix):** paste the seed JSON from `parameterSurfaceSeeds.ts` for `openai_responses_reasoning`, `anthropic_standard`, or `google_gemini_25_flash` (or pick a known model id that typeahead-seeds those fields).

## 8. Phase 1 — Configure Catalog Surfaces (Settings UI via playwright-cli)

**Goal:** Every matrix row reaches target surface through operator paths. Empty starting state is exercised, not skipped. Agent drives Chrome via `playwright-cli -s=parameter-surface-test`; operator watches the same tab.

### 8.1 Navigation

Settings → **Models & Runtime** → **Catalog**.

### 8.2 Configure empty OpenRouter rows (required)

For each model, open **Edit** (pencil icon).

#### `google/gemma-4-31b-it`

1. Assert **Sampling Parameters JSON** is `{}` and **Reasoning Choices** is empty.
2. Paste **shape A** sampling JSON into **Sampling Parameters JSON** (see Section 7.4; same shape as `minimax/minimax-m3`).
3. Save.
4. Re-open edit → assert `temperature` and `top_p` keys present in sampling JSON.
5. Snapshot: `playwright-cli -s=parameter-surface-test snapshot --filename=param-surface-01-config-gemma-or.yaml`

#### `xiaomi/mimo-v2.5`

Same as gemma-or (shape A via **Sampling Parameters JSON**). Snapshot: `--filename=param-surface-02-config-mimo.yaml`

#### `nvidia/nemotron-3-ultra-550b-a55b:free`

1. Assert **Sampling Parameters JSON** is `{}`.
2. Leave sampling as `{}`.
3. Type `medium, high` in **Reasoning Choices**.
4. Save and re-open → assert exactly those two choices.
5. Snapshot: `--filename=param-surface-03-config-nemotron.yaml`

**Phase 1 assertion:** Overview must **not** show temperature for nemotron if sampling remains `{}`; reasoning dropdown shows medium and high only.

### 8.3 Verify pre-seeded OpenRouter row

#### `minimax/minimax-m3`

1. Edit → assert sampling JSON contains `temperature` and `top_p` (shape A already on row).
2. Save without changes (no-op save must succeed).
3. Snapshot: `--filename=param-surface-04-verify-minimax.yaml`

### 8.4 Add missing shape models (OpenRouter)

Use **+ Add Model** wizard for each. On the provider-config step paste seed JSON into the model parameter fields (or pick a known model id that typeahead-seeds them).

| ModelId | Provider | Target shape |
|---------|----------|--------------|
| `openai/gpt-5.5` | OpenRouter | B (`openai_responses_reasoning`) |
| `anthropic/claude-sonnet-4.5` | OpenRouter | C (`anthropic_standard`) |
| `google/gemini-2.5-flash` | OpenRouter | D (`google_gemini_25_flash`) |

Per model:

1. Complete wizard through review.
2. Assert submitted payload includes row-owned `samplingParametersJson` / `reasoningChoicesJson`.
3. Assert **no** `runtimeProfileId` persisted (catalog API `runtimeConfig` null for non-local).
4. Snapshot: `--filename=param-surface-05-add-<slug>.yaml`

### 8.5 Verify non-OpenRouter rows unchanged

| Model | Checks |
|-------|--------|
| `gpt-4.1` | temp + top_p; no reasoning dropdown |
| `gpt-5.5` (azure) | reasoning choices; no sampling sliders |
| `zai-org/GLM-5.2` | temp + top_p |
| `qwen3.6-35b-a3b-mtp-local` | full qwen3_6 sampling + reasoning incl. low/med/high |
| `gemma-4-12B-it-qat-GGUF` | gemma4 sampling + none/enabled reasoning |

Snapshot: `--filename=param-surface-06-verify-existing.yaml`

### 8.6 Phase 1 pass criteria

- [ ] All Section 7.2 rows exist and are active.
- [ ] Every configure step completed through UI only.
- [ ] Re-open edit modal matches saved surface for each row.
- [ ] Non-local rows have `runtimeConfigJson` null or without `runtimeProfileId` (spot-check via API or SQL read **after** phase for evidence only).

## 9. Phase 2 — UI Surface Rendering (playwright-cli)

**Goal:** Catalog API surfaces render as controls in Settings Overview and Guide Builder.

### 9.1 Settings Overview — default model loop

For each shape, set **Default Chat Model** to the matrix model and capture **Configuration Parameters** panel.

| Shape | Model | Expected controls |
|-------|-------|-------------------|
| A | `minimax/minimax-m3` | Temperature, Top P |
| B | `openai/gpt-5.5` (OR) | Reasoning effort only (none/low/med/high/xhigh) |
| B′ | `gpt-5.5` (azure) | Reasoning (minimal/low/med/high) |
| C | `anthropic/claude-sonnet-4.5` | Temperature (max 1), Top P, reasoning incl. minimal |
| D | `google/gemini-2.5-flash` | Temperature, Top P (default 0.95), reasoning low/med/high |
| E | `qwen3.6-35b-a3b-mtp-local` | Temperature, Top P, Top K, Presence Penalty, Reasoning |
| F | `gemma-4-12B-it-qat-GGUF` | Temperature, Top P, Top K, Reasoning (None/Enabled) |
| G | `nvidia/nemotron-3-ultra-550b-a55b:free` | Reasoning medium/high only; no temperature |
| H | `xiaomi/mimo-v2.5` | Temperature, Top P |

Per model:

1. Select model in Overview dropdown.
2. Assert control set matches table (count labels and min/max where visible).
3. Change one value (e.g. temperature 0.3 or reasoning low).
4. Save Overview defaults.
5. Reload page → values persisted.
6. Snapshot: `--filename=param-surface-overview-<slug>.yaml`

### 9.2 Catalog edit round-trip

Pick one model per provider family; edit surface, save, reload:

| Provider family | Model | Edit action |
|-----------------|-------|-------------|
| openrouter-chat | `google/gemma-4-31b-it` | Change temperature default in JSON editor |
| azure-openai-chat | `gpt-4.1` | Toggle top_p default |
| azure-openai-responses | `gpt-5.5` | Add/remove a reasoning choice (if editor allows) |
| hf-inference-chat | `zai-org/GLM-5.2` | Verify save |
| llama-cpp | `qwen3.6-35b-a3b-mtp-local` | Verify llama form still owns thinkingControl |

Pass: saved values appear in Overview when that model is selected.

### 9.3 Guide Builder (if applicable)

Open a guide that uses catalog model params (Creative Guide or SDLC Guide):

1. Set guide model to `qwen3.6-35b-a3b-mtp-local`.
2. Assert exposed sampling params appear in guide config UI (`exposedInGuideBuilder: true` keys only).
3. Repeat with `minimax/minimax-m3` — temperature and top_p only.

Snapshot: `--filename=param-surface-guide-builder.yaml`

### 9.4 Phase 2 pass criteria

- [ ] Every shape A–H renders correct Overview controls.
- [ ] Overview save/reload persists changes.
- [ ] Guide Builder shows params only when `exposedInGuideBuilder` is true.
- [ ] Empty-surface regression: after Phase 1, no configured model shows blank Configuration Parameters.

## 10. Phase 3 — Live Chat Smoke (playwright-cli)

**Goal:** Configured parameters are accepted at chat dispatch and produce a completion.

Use one notebook (e.g. Creative Guide). Enable **Override all chat models** in header Chat panel or Overview as appropriate.

### 10.1 Standard prompt (all models)

```
Parameter smoke: reply with exactly "ok" and your model id.
```

### 10.2 Per-shape extended checks

| Shape | Model | Extra steps | Pass |
|-------|-------|-------------|------|
| A | `minimax/minimax-m3` | Set temperature 0.2 in Overview, save, send prompt | Completion succeeds; model id in reply |
| B | `openai/gpt-5.5` (OR) | Set reasoning `low`, send prompt | Completion succeeds |
| B′ | `gpt-5.5` (azure) | Set reasoning `minimal` | Completion succeeds |
| C | `anthropic/claude-sonnet-4.5` | Set reasoning `low` | Completion succeeds |
| D | `google/gemini-2.5-flash` | Set reasoning `medium` | Completion succeeds |
| E | `qwen3.6-35b-a3b-mtp-local` | Reasoning `none` then `medium` on two turns | Both succeed; observable behavior difference optional |
| F | `gemma-4-12B-it-qat-GGUF` | Reasoning `none` | Completion succeeds (model may need loaded runtime) |
| G | `nvidia/nemotron-3-ultra-550b-a55b:free` | Reasoning `high` | Completion succeeds |
| H | `xiaomi/mimo-v2.5` | temperature 0.2 | Completion succeeds |

### 10.3 Negative validation (Shape G)

1. Set nemotron reasoning to a value **not** in `["medium","high"]` if UI allows free entry — save should fail validation.
2. If UI only offers medium/high, assert invalid values cannot be selected.

### 10.4 Phase 3 pass criteria

- [ ] All matrix models complete at least one chat turn.
- [ ] No `runtimeProfileId` or surface-missing errors in API logs.
- [ ] Reasoning-only models reject invalid effort where validation exists.

Snapshot per model: `--filename=param-surface-chat-<slug>.yaml`

## 11. Phase 4 — Automated Contract Tests (CI)

Run on every PR touching parameter surface or catalog paths.

### 11.1 Client

```bash
cd src/client
npm test -- --run \
  src/pages/settings/__tests__/parameterSurface.test.ts \
  src/pages/settings/__tests__/utils.test.ts \
  src/pages/settings/components/catalog/__tests__/CatalogRowEditModal.test.tsx \
  src/pages/settings/components/catalog/__tests__/ModelIdTypeahead.test.tsx \
  src/components/home/addAiServicesWizard/__tests__/utils.test.ts
```

### 11.2 Server

```bash
dotnet test src/server/GuideAntsApi.Tests/GuideAntsApi.Tests.csproj \
  --filter "FullyQualifiedName~CatalogServiceRowAuthorityTests|FullyQualifiedName~ApplicationSettingsServiceModelTests|FullyQualifiedName~GuidesServiceDeepTests"
```

### 11.3 Invariants (do not write change-detector tests)

Assert relationships, not literal model catalogs:

- Non-local `CatalogService` DTO: `samplingParameterPolicy` derived from row `SamplingParametersJson`.
- Non-local `reasoningChoices` derived from row `ReasoningChoicesJson`, not runtime profile resolution.
- `PUT` non-local model rejects `runtimeProfileId` in `runtimeConfigJson`.
- `buildAddModelRequest` / `buildCatalogEditRequest` send row fields, not profile pointers.
- Migration backfill: profile-sourced fields copied once; `RuntimeConfigJson` profile pointer cleared.

### 11.4 Phase 4 pass criteria

- [ ] All listed tests green in CI.
- [ ] No new tests that freeze model ID lists or seed key counts.

## 12. Evidence Package

Minimum artifacts per full run:

| Artifact | Phase |
|----------|-------|
| `output/db/guideants-parameter-surface-baseline.bak` (one-time) | setup |
| `output/logs/parameter-surface-<stamp>/` | 0–3 |
| `.playwright-cli/param-surface-01-config-gemma-or.yaml` … `06-verify-existing.yaml` | 1 |
| `.playwright-cli/param-surface-05-add-*.yaml` | 1 |
| `.playwright-cli/param-surface-overview-<slug>.yaml` (one per shape) | 2 |
| `.playwright-cli/param-surface-guide-builder.yaml` | 2 |
| `.playwright-cli/param-surface-chat-<slug>.yaml` (one per shape) | 3 |
| CI test output / link | 4 |

## 13. Result Reporting Template

For each step or blocker:

1. Date/time (ET)
2. Phase (0–4)
3. Model / shape
4. Exact step performed
5. Observed vs expected
6. Outcome: PASS / DEFECT / LIMITATION
7. Evidence paths
8. API log excerpt if relevant

## 14. Overall Pass / Fail

**Pass** requires all:

1. Baseline backup exists (Section 4.2) and each run started with restore (Section 4.3).
2. Phase 1: full matrix configured through UI.
3. Phase 2: all shapes A–H render correct Overview controls.
4. Phase 3: all shapes complete chat smoke.
5. Phase 4: CI contract tests green.

**Fail** if any:

1. Configuration step skipped or done via SQL instead of UI.
2. Empty surface remains after Phase 1 for a required row.
3. Overview or chat ignores row-owned surface.
4. Non-local model persists or reads `runtimeProfileId` as authority.

## 15. Optional Follow-ups (not blocking)

- Add `openrouter_extended_chat` seed with top_k / min_p for Shape H models in `parameterSurfaceSeeds.ts` and `knownCloudModels.json`.
- Separate baseline DB `guideants-parameter-surface-tests` forked from current dev to avoid colliding with other work.
