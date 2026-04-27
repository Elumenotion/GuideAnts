You're right. My previous answer had two corner cuts baked into it:

1. I offered "if that's out of scope … a banner on Catalog" as a fallback for when the proper routing design is too much work. That's exactly the "fallback to hide a bug" pattern your rule calls out, just dressed as UX.
2. I accepted the plan's Phase 3 ("delete the Catalog Create/Edit form") at face value and built around it, instead of questioning whether the plan's Phase 3 itself is a shortcut. It is. The plan is solving "the current Create/Edit form is broken for llama-cpp" by deleting the whole form, which also deletes the path for every other provider and for every non-create use of the form (rename, re-bind profile, deactivate). That's scope-dumping, not design.

Let me redo this as a feature design, not a tweak to the existing plan.

## The actual domain, stated cleanly

Four distinct concepts are getting conflated:

- **Catalog model** — identity + metadata a user sees ("GPT-4o", "Qwen3.5 9B Q5_K_M (Local)"). Provider-scoped. Referenced by assistants.
- **Provider** — determines which fields exist on the catalog row and which side operations are required to make the row usable (cloud: none; llama-cpp: install bytes + register alias).
- **Runtime profile** — sampling/thinking presets for llama-cpp. Independent lifecycle. N:1 with catalog rows.
- **Router alias** — llama-cpp-specific runtime handle with its own state (file present, loaded, in use). N:M with catalog rows in principle, 1:1 in current practice.

Every UX problem in the current design comes from not respecting that these are four things. The "Download & Register" form is a catalog-row-creator, a runtime-profile-referencer, an alias-installer, and a file-downloader in one anonymous submit. That's also precisely where the Phase 1 server smells (hidden reasoning-choices default, silent catalog auto-register catch) live. The UI structure and the server smells are the same bug.

## The user workflows, each deserving its own first-class surface

1. **Add Model.** Primary. Provider-first wizard, lives on Catalog.
2. **Edit model metadata.** Rename, swap runtime profile, toggle active. Per-row on Catalog.
3. **Remove model from catalog.** Per-row on Catalog. For llama-cpp: *removes the catalog entry only*, leaves the alias. Because a user who set up both a "reasoning" and a "fast" catalog entry against the same alias needs to be able to delete one without wiping the install.
4. **Operate a llama alias.** Load / Unload / Delete alias-and-its-files. Lives on Local Llama Runtime. This is the ops surface, not the add surface.
5. **Diagnose an alias.** Runtime state + router mapping preview + file presence. Same tab as (4).
6. **Install from an existing alias (recovery).** "The files are on disk, the alias is registered, but my catalog row got blown away — rebuild the catalog entry without re-downloading." This case exists today (torn state from silent failures) and will exist tomorrow (manual DB ops, image swaps). Needs a path.

Six workflows. The plan as written has one surface for (1), no surface for (2), a half-surface for (3), (4) and (5) collapsed into one tab with (1), and (6) missing entirely.

## Design

### 1. Add Model is a provider-driven wizard, and it is on Catalog

A single "Add Model" button on the Catalog tab. Wizard, not an inline form, because the steps genuinely differ by provider and asking the user to pick provider first is the only honest flow.

**Step 1 — Choose provider.** Populated from the provider registry. Each row shows whether its credentials section is configured; unconfigured providers are selectable but the wizard will hard-block submit at Step 4 with a deep-link to Connections. No silent skipping, no "we'll try anyway" — if OpenAI credentials aren't saved, you cannot add an OpenAI model.

**Step 2 — Catalog entry (provider-agnostic).**
- Catalog model id (validated unique)
- Display name
- Description
- Active toggle (default on)

**Step 3 — Provider-specific configuration.** Contributed by the provider's UI contract.

- *OpenAI / Azure OpenAI / Anthropic*: target model identifier as the provider knows it, endpoint/deployment/region as applicable, reasoning mode toggles when the model supports them. No install step, no runtime profile.
- *llama-cpp*: runtime profile (with inline "create from template" and "create custom" so a user adding Qwen3.5 for the first time doesn't bounce out to the Runtime Profiles sub-tab), router alias (with uniqueness check against the live router), and a **source selector**:
  - **Install from Hugging Face** → repository, quant pattern, mmproj pattern, target subdirectory, HF token preflight (blocked if missing).
  - **Attach existing alias** → pick from live router aliases that have no catalog row. This is workflow (6) — the recovery path. It is not a corner case; it is how you un-tear state without faking it.

Reasoning choices are derived from the chosen runtime profile's `thinkingControlJson.choiceActions`, shown in Step 3 as a read-only preview ("This profile exposes: none, enabled"). No hidden default; if the profile exposes nothing the preview is empty and the catalog row's `ReasoningChoicesJson` is null.

**Step 4 — Review + submit.** Single atomic operation.

**Step 5 — Progress (llama-cpp install only).** Wizard stays open and shows the state machine:

1. Queued
2. Resolving files on Hugging Face
3. Downloading (bytes progress)
4. Registering router alias
5. Adding catalog entry
6. Done

Each of 2–5 can fail independently, and the UI surfaces which step failed with the error code and remediation. On success the wizard offers **Load now** and **Open in Catalog**.

This is also the shape the server enum should take. The UI should render human steps from a single step table instead of transliterating implementation names.

### 2. Local Llama Runtime becomes pure ops

After (1), the tab has no reason to carry an "Add" form. It hosts:

- **Runtime Inventory** — per-alias row with Load / Unload / **Delete alias + files** (the cascading delete from Phase 1.5, now explicitly labeled for what it is: "Delete this alias, its files, and every catalog row that targets it"). Notebook-reference count still gates.
- **Router Mapping** — read-only diagnostic.
- An **"Add local llama model"** link at the top of the tab that routes to Catalog → Add Model with provider preselected. This is not a fallback; it is explicit navigation signalling that adds live on Catalog. Users who land on Local Llama Runtime expecting to add a model get pointed at the right door, not handed a second door that does the same thing.

### 3. Catalog Edit stays, and is provider-scoped

Phase 3 as written ("remove Catalog Create/Edit form and Edit button; keep table + row-delete") is the corner cut. Reasoned properly:

- **Create** → moved to the wizard. Correct.
- **Edit** → must stay. Per-row "Edit" opens a provider-scoped editor. Editable: display name, description, active, runtime profile (llama-cpp only, bounded by what the underlying alias supports). Non-editable: id, provider, router alias (identity). This is a smaller form than today's because the installer fields aren't in it.
- **Delete** → per-row. Catalog-only delete. Does **not** cascade to the alias. The explanatory copy on the confirm dialog states exactly that, and points at Local Llama Runtime → Runtime Inventory → Delete alias for the full teardown.

This gives the two delete operations distinct homes that match their distinct semantics: Catalog delete removes a chat target, Runtime Inventory delete removes an installed alias. Today's plan has only the cascading one, which means a user who wants two catalog rows against one alias (reasonable — one for thinking, one for instruct) cannot delete one of them without a roundtrip.

### 4. Runtime profile creation inline

The wizard's Step 3 "Runtime profile" selector exposes:

- Pick existing profile (usage count shown).
- Create from template (Qwen3.5 / Qwen3.6 / Gemma4).
- Create custom (opens the profile editor in a side panel, returns to the wizard with the new profile preselected).

Without this, "add Qwen3.5 for the first time" requires: go to Runtime Profiles, stamp template, back to Catalog, Add Model, pick the profile. That's the ordering the current setup-guide documents and it's the ordering the plan is trying to kill. If we don't offer inline creation in the wizard we've re-created the same bounce.

### 5. Llama runtime contract

Runtime behavior is keyed by router alias and runtime profile.

### 6. State machine and error surface

`queued → resolving → downloading → registering → completed | failed` is the right decomposition, but:

- The UI never renders enum names. It renders human steps and a failed-step indicator.
- `failed` carries a structured `{ code, step, message, remediation }`, not a message the client has to substring-match.
- The wizard's Step 5 remains the progress surface; if the user closes it, the in-progress download is retrievable from a new Catalog-tab row badge ("Installing…") that re-opens the wizard when clicked. This means an accidental close does not strand the user.

### 7. `docs/setup-guide.md` is the spec this design is built against

Rewrite, not patch:

- **§6 Step 3** — stop describing Models & Runtime as "three sub-tabs with their own editors." Describe it as three workflows:
  - *Add a model* → Catalog → Add Model wizard (one place, all providers).
  - *Manage a local llama alias* → Local Llama Runtime → Runtime Inventory.
  - *Create a sampling preset* → Models & Runtime → Runtime Profiles (or inline from the wizard).
- **§7** — Qwen3.5-9B-Q5_K_M walk-through through the wizard. No pre-insertion of a catalog row, no pre-insertion of a profile (the wizard offers to create it inline). This is the operator's day-one flow and it is also CP2–CP4 of the playwright acceptance; one narrative, two audiences.
- **§7b (new)** — add a cloud provider (GPT-4o) through the same wizard, to prove the wizard is the unified entry and that cloud providers get a genuine add path rather than the Phase-3-shaped hole.
- **§7c (new)** — attach existing alias. Recovery scenario: the alias is healthy, the catalog row is missing. Use the wizard's "Attach existing alias" source option. Documents workflow (6) so operators have a non-manual path to recover from tear.
- **§11 Troubleshooting** — add an entry for each new failure mode the wizard can surface (`HUGGINGFACE_TOKEN_MISSING`, profile-resolution failure at step 5, alias-already-exists at step 4) with the remediation copy matching the UI.

## Implications for the existing plan

The plan is mostly correct underneath — the server work in Phase 1 is sound and necessary regardless of the UI shape. Where it needs to change:

- **Phase 2** grows: it builds the Add Model wizard (Catalog-owned, provider-driven, with inline profile creation and the "Attach existing alias" source option), and shrinks Local Llama Runtime to ops-only.
- **Phase 3** flips. Instead of "remove Catalog Create/Edit," it becomes "split the Catalog form into a wizard-only Create and a slim per-row Edit; remove the provider-picker from the list view because provider is picked in the wizard." Edit stays.
- **Phase 4 docs**: add `docs/setup-guide.md` rewrites of §6 Step 3 and §7 to the list, and author §7b and §7c.
- **Phase 5 playwright**: add CP0 ("Add Model wizard from Catalog, llama-cpp, happy path" — same bytes as today's CP2, entered through the wizard), CP8 ("Add Model wizard, cloud provider, happy path" — proves the wizard is unified), CP9 ("Attach existing alias recovery"), CP10 ("Edit catalog row display name and runtime profile without touching the alias").

That's more scope than the plan currently owns. That's the point — the plan as written fits today's tangled UI. A new feature you're iterating on to be good deserves a design that reflects the four concepts and six workflows, not one that presses them into the existing two-form, three-sub-tab layout because it's cheaper.
