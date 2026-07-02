# Skills Support — Execution & Orchestration Guide

Last updated: 2026-07-01

This is the **conductor** document for executing
[`../skills-support-proposal.md`](../skills-support-proposal.md). It is written for the
**top-level (orchestrating) agent**. It defines how the work is split into **subagent task
briefs**, the **dependency order**, the **verification gates** the orchestrator runs after
each phase, and the **deviation/failure protocol** that keeps the plan on-rails so it is
executed correctly the first time.

> **Audience split**
>
> - **You (orchestrator)** read this file plus [`DECISIONS.md`](./DECISIONS.md),
>   [`STATUS.md`](./STATUS.md), [`progressive-disclosure-gate.md`](./progressive-disclosure-gate.md),
>   [`no-index-gate.md`](./no-index-gate.md), [`ui-gate.md`](./ui-gate.md),
>   [`wire-distribution-gate.md`](./wire-distribution-gate.md), and
>   [`codeql-gate.md`](./codeql-gate.md). You dispatch subagents, run gates, and update
>   `STATUS.md`.
> - **Subagents** read only their own `task-phase-N-*.md` brief, the proposal sections it
>   cites, and `DECISIONS.md`. A subagent should **not** need any other context.

---

## 0. How to use this folder

| File | Owner | Purpose |
|------|-------|---------|
| `00-orchestration.md` (this) | Orchestrator | Dispatch order, gates, deviation protocol. |
| `DECISIONS.md` | Orchestrator (locked before dispatch) | Locks proposal §2 firm decisions + §20 open questions as S1–S13. Single source of truth. |
| `STATUS.md` | Orchestrator (update after every gate) | Living ledger: phase state, gate results, deviations, re-dispatches. |
| `progressive-disclosure-gate.md` | Orchestrator + Phases 1,4,5 | Tier-1-only injection; bodies via `skills.read`; no bodies in `AssistantDefinition`; locator + path-safety. |
| `no-index-gate.md` | Orchestrator + Phases 1,2,3,5 | `Skill` `FolderKind` is inert to markdown-shadow / Kernel Memory / `file_search`. |
| `ui-gate.md` | Orchestrator + Phase 2 | Guide Builder Skills-tab authoring contract (import, author, create-from-skill, gating display, reuse + decomposition). |
| `wire-distribution-gate.md` | Orchestrator + Phase 4 | Skills as MCP / orchestrator resources over the published wire; trace `Source=skills`. |
| `codeql-gate.md` | Orchestrator + security-sensitive phases | Local baseline-vs-current diff (path traversal, resource exposure, secret handling). |
| `task-phase-1-storage-and-runtime.md` | Subagent | `Skill` FolderKind + `SkillFrontmatter` + `SkillDescriptor` + `BuildSkills` + `AssistantDefinition.Skills` + `skills.list`/`skills.read` + discovery block. |
| `task-phase-2-guide-builder-authoring.md` | Subagent | Skills tab, import `SKILL.md`, author-new, DTOs, `GuidesService`, `SkillPackageParser`/`ISkillImportService`, create-assistant-from-skill + toolset mapping. |
| `task-phase-3-export-import-bootstrap.md` | Subagent | `Skills/` import loop in `GuideExportImportService`; curated bootstrap skill; round-trip. |
| `task-phase-4-wire-mcp-distribution.md` | Subagent | Skills as MCP resources on `/api/published/mcp`; orchestrator-resource locators; trace source tagging. |
| `task-phase-5-optional-metadata-sidecar.md` | Subagent (optional) | `AssistantSkillMeta` sidecar (metadata only) if listing/toggle perf requires it. |
| `acceptance-evidence.md` | Orchestrator + Phase (final) | Captured commands/outputs proving final acceptance. |

Each task brief follows the **same template**: Mission → Read first → Preconditions →
Guardrails → Tasks → Files in/out of scope → Self-verification → Definition of Done →
Report-back contract. The Report-back contract is what you diff against the brief to
**detect deviations**.

---

## 1. Pre-flight (do this once, before any subagent is dispatched)

Executing "the first time" depends on locking cross-cutting choices up front. **Do not
dispatch Phase 1 until all of the following are true.**

- [ ] **`DECISIONS.md` is fully LOCKED** (S1–S13 + frozen invariants). Any value still open
      that blocks a phase keeps that phase blocked.
- [ ] **Confirm the two product-scope decisions** with the user if they must not default:
      S7 (published locator form) and S13 (per-assistant skill limits). Recommended values
      are locked; confirm before Phase 4 (S7) / Phase 1 (S13) if the defaults are wrong.
- [ ] **Capture a clean baseline** and record it in `STATUS.md`:
  - `cd src/server && dotnet build GuideAntsApi.sln`
  - `cd src/server && dotnet test GuideAntsApi.sln`
  - `cd src/client && npm run build`
  - `cd src/client && npm test -- --run`
- [ ] **Capture the no-index baseline** (`no-index-gate.md` §2): confirm today's
      markdown-shadow / extraction enqueue sites are gated on `FolderKind == "VectorStore"`
      (`GuidesService` create-guide, create-assistant, save-files paths; `DatabaseStorage`
      `BuildToolsArray`/`BuildToolResources`). Record the exact call sites.
- [ ] **Capture CodeQL baseline** (`codeql-gate.md`) and save SARIFs under
      `.codeql/baseline/`.
- [ ] **Inventory existing touchpoints** so scope is known, not guessed:
      `src/server/GuideAntsApi.DataModel/Models/AssistantFile.cs`,
      `src/server/AntRunner.Chat/AntRunner.ToolCalling/AssistantDefinitions.Storage/DatabaseStorage.cs`
      (`BuildToolsArray`/`BuildToolResources`),
      `src/server/AntRunner.Chat/AntRunner.ToolCalling/AssistantDefinitions/AssistantDefinition.cs`,
      `src/server/AntRunner.Chat/AntRunner.Chat/ThreadRun.cs` (`DoToolCalls`, `SearchAssistantFiles` pattern),
      `src/server/GuideAntsApi/Services/Conversations/Mapping/ConversationHistoryBuilder.cs`,
      `src/server/GuideAntsApi/Services/Guides/GuidesService.cs`,
      `src/server/GuideAntsApi/Services/Guides/GuideExportImportService.cs`,
      `src/server/GuideAntsApi/Services/Mcp/ClaudeSkillPackService.cs` (export precedent — do not change),
      `src/client/src/components/guides/editor/{BaseEntityEditor,EditorTabs}.tsx`.
- [ ] Confirm clean working tree (`git status`) and an active feature branch
      (e.g. `feature/skills-support`) per the repo branch-safety rule. **Never** set upstream
      to `origin/main`.
- [ ] Confirm `dotnet ef --version` is available (Phase 5 needs a migration **only** if the
      optional sidecar is built; Phases 1–4 add **no** DB schema — `Skill` reuses the
      existing `AssistantFile` table).

If any blocking decision is unresolved, stop and ask before dispatching the dependent phase.

---

## 2. Dependency graph (dispatch order)

```text
                 Phase 1  Storage + runtime (own conversations)
                 (Skill FolderKind + Skills/<name>/ convention; SkillFrontmatter;
                  SkillDescriptor; BuildSkills; AssistantDefinition.Skills;
                  skills.list/skills.read; discovery block)  S1/S3/S4/S5/S6
                          │
             ┌────────────┼───────────────┬───────────────┐
             ▼            ▼                ▼               │
         Phase 2      Phase 3          Phase 4             │
   (Guide Builder  (Export/import   (Wire + MCP            │
    Skills tab;     + bootstrap      distribution;         │
    import/author;  Skills/ loop;    orchestrator          │
    create-from-    curated skill)   resources; trace)     │
    skill; DTOs)    S15 (round-trip) S7/S4-wire            │
    S9 (mapping)                                           │
             └────────────┴───────────────┴───────────────┘
                          ▼
                 Phase 5  Optional metadata sidecar
                 (AssistantSkillMeta — only if listing/toggle perf requires)  S12
```

**Rules:**

- Phases run in dependency order. **A phase is not "done" until its gate (section 4)
  passes.** A downstream phase must **never** start on top of a failed gate.
- **Phases 2, 3, and 4 may run in parallel** after Phase 1's gate is green — Phase 2 is
  client + `GuidesService`/DTOs; Phase 3 is `GuideExportImportService` + bootstrap; Phase 4
  is `PublishedWire/*` + MCP mapping + trace. They share few files. Prefer sequential
  (2 → 3 → 4) unless schedule pressure demands parallel.
- **Phase 5 is optional.** Only dispatch it if the Phase 2 UI or listing performance proves
  the derive-from-frontmatter path is insufficient. Default: `SKIPPED`.
- One subagent per phase brief. Do **not** hand a subagent more than its brief.
- Phases 1 and 4 are **security-sensitive** (`skills.read` path traversal; resource
  exposure over the wire) and require CodeQL gate passes.

---

## 3. Dispatch protocol (per phase)

For each phase, in order:

1. **Confirm preconditions** in the brief (prior gate green; DECISIONS dependencies).
   Update `STATUS.md` → phase `IN_PROGRESS`.
2. **Dispatch one subagent** with exactly: *"Read and execute
   `docs/skills-execution/task-phase-N-*.md` end to end. Obey its guardrails and Definition
   of Done. Return the Report-back contract verbatim."* Give it no other instructions — the
   brief is the contract.
3. **Receive the Report-back** as a claim, not proof.
4. **Run the gate** (section 4 + the phase's own gate) with your own tools, not the
   subagent's word.
5. **Decide:** PASS → mark phase `DONE`, proceed. FAIL/DEVIATION → follow section 5.

> You verify; the subagent implements. Never let "the subagent said it's done" substitute
> for a green gate.

---

## 4. Verification gates

### 4.1 Global invariants — checked at **every** gate

- [ ] **Server build green:** `cd src/server && dotnet build GuideAntsApi.sln` (0 errors;
      warnings not worse than baseline).
- [ ] **Server tests green:** `cd src/server && dotnet test GuideAntsApi.sln` — no new
      failures vs baseline.
- [ ] **Client build green:** `cd src/client && npm run build`.
- [ ] **Client tests green:** `cd src/client && npm test -- --run`.
- [ ] **No skill body in the definition.** `AssistantDefinition.Skills` carries tier-1
      descriptors only (name, description, locator, gating, file paths). Grep proves no
      skill `BodyMarkdown`/`ContentBytes` is serialized into the definition JSON.
- [ ] **Skills never indexed.** A `Skill` `AssistantFile` creates no
      `AssistantFileMarkdownShadow`, enqueues no `ExtractAssistantFileMarkdownJob`, and never
      appears in `file_search`/`vector_store_ids`. (no-index gate.)
- [ ] **No fallback masking** (user rule). No new silent `catch {}`, no "assume skill is
      valid on parse failure", no capability fabrication when a required toolset is absent
      (gate hides; it does not inject). Parse/validation failures are explicit errors.
- [ ] **`AssistantFile` remains the canonical skill body store;** no unapproved new DB
      columns (Phases 1–4). The optional sidecar (Phase 5) is metadata-only and never stores
      bodies.
- [ ] **`skills.read` path-safety.** Resolved path must stay under `Skills/<name>/`; `..`
      traversal and absolute paths are rejected explicitly.
- [ ] **One materialization choke point.** Skill descriptors are produced only in
      `DatabaseStorage` (`BuildSkills`); the conversation layer and tools consume the
      materialized definition, not ad-hoc DB reads that could diverge.
- [ ] **Existing `ClaudeSkillPackService` export is untouched.**
- [ ] **Scope discipline:** touched files stay within the brief's "Files in scope".
- [ ] **Matches `DECISIONS.md`** (S1–S13 + invariants). A subagent that embedded skill
      bodies in the definition, indexed a `Skill` file, injected tools from gating, or
      allowed path traversal is an automatic FAIL.

### 4.2 Per-phase gate criteria

Each is **in addition** to 4.1. Commands assume `src/server` / `src/client` cwd.

**Phase 1 — Storage + runtime**

- [ ] `Skill` is a valid `AssistantFile.FolderKind`; skills live under
      `Skills/<skill-name>/SKILL.md` (+ `references/`, `scripts/`, `assets/`) (S1).
- [ ] `SkillFrontmatter.Parse` reads the agentskills.io dialect (`name`, `description`,
      `metadata.guideants.*`) and tolerates hermes `metadata.hermes.*` + Claude-Code
      `allowed-tools`/`argument-hint` without failing (S2, S10).
- [ ] `DatabaseStorage.BuildSkills` groups `Skill` files by `Skills/<name>/`, requires a
      `SKILL.md` with `name`+`description`, excludes `enabled: false`, and produces
      `SkillDescriptor[]` (tier-1 only). `AssistantDefinition.Skills` is populated (S3).
- [ ] `skills.list` / `skills.read` exist as **server-handled** local tools in `ThreadRun`
      (`ActionType.LocalFunction`), execute between rounds, and never emit
      `pending_client_tool` (S4). `skills.read` enforces path-safety.
- [ ] `ConversationHistoryBuilder` injects a tier-1 discovery block (name + description +
      locator) with the gating visibility filter applied (S6). Only injected for
      skill-bearing assistants.
- [ ] Skill `scripts/` and `assets/` materialize into notebook `Resources/` + `Output/`
      symlinks at notebook creation via `SkillNotebookMaterializer` + `NotebookService`
      (S11). `SKILL.md` and `references/` are not copied.
- [ ] **no-index gate** passes; **progressive-disclosure gate** passes; CodeQL diff clean.

**Phase 2 — Guide Builder authoring**

- [ ] A Skills tab exists in `BaseEntityEditor` (via `EditorTabs`), parallel to Tools/Files.
- [ ] **Import SKILL.md** (folder or zip) parses frontmatter + body + `references/`/
      `scripts/`/`assets/` and persists `Skill` `AssistantFile` rows via `GuidesService`
      (reusing the Files-tab upload plumbing).
- [ ] **Author new skill** writes a valid `SKILL.md`.
- [ ] **Create assistant from skill(s)** seeds a new assistant, attaches the skills, and
      maps `requires_toolsets`/`requires_tools` to concrete GuideAnts tools via the explicit
      mapping table (S9). No silent capability guessing.
- [ ] DTOs (`AssistantSkillDto`, `Create/UpdateGuideDto` extensions) + `GuidesService`
      persist/round-trip skills; `SkillPackageParser`/`ISkillImportService` exist and are
      unit-tested.
- [ ] **ui-gate** passes (import/author/create-from-skill/gating display, reuse +
      decomposition); **no-index gate** passes (uploads create no shadow).

**Phase 3 — Export/import + bootstrap**

- [ ] `GuideExportImportService` writes `Skills/<name>/...` on export (the existing `else`
      raw-`RelativePath` branch already covers this — verify, do not duplicate) and a new
      `Skills/` import loop creates `FolderKind = "Skill"` rows preserving relative path.
- [ ] At least one curated bootstrap skill ships under
      `Resources/bootstrap/guides/<guide>/Skills/<name>/SKILL.md` and imports cleanly.
- [ ] **Round-trip is lossless** (export → import reproduces the same `Skill` file set +
      relative paths + content). **no-index gate** passes (imported skills create no shadow).

**Phase 4 — Wire + MCP distribution**

- [ ] Each definition skill is exposed as an MCP resource on `/api/published/mcp`:
      `skill://<guide>/<name>` → `SKILL.md` body; `skill://<guide>/<name>/references/<path>`
      → reference file (S7). `list_mcp_resources`/`read_mcp_resource` resolve them.
- [ ] Server-resolved mode (Mode A): the discovery block + `skills.list`/`skills.read` work
      unchanged for OpenAI (`/chat/completions`, `/responses`) and Anthropic (`/messages`)
      wire clients with no client changes; the `/invoke` path also benefits.
- [ ] `TurnTraceToolDefinition.Source` supports `skills`; `skills.list`/`skills.read` calls
      and the discovery block are tagged in the prompt trace.
- [ ] **wire-distribution gate** passes; **progressive-disclosure gate** (locators) passes;
      CodeQL diff clean.

**Phase 5 — Optional metadata sidecar (only if dispatched)**

- [ ] `AssistantSkillMeta` stores metadata only (AssistantId, SkillName, Description,
      Enabled, DisplayOrder, ContentHash) — **never** bodies. `AssistantFile` remains the
      body store.
- [ ] Sidecar is kept in sync on skill create/update/delete; `BuildSkills` may read it for
      listing but bodies still load from `AssistantFile` via `skills.read`.
- [ ] Migration is metadata-only; **no-index** + **progressive-disclosure** gates still pass.

### 4.3 Progressive-disclosure gate (summary)

Defined in `progressive-disclosure-gate.md`. Run after Phases 1, 4, 5. Pass when only
name+description+locator are injected, bodies/reference files load only via `skills.read`
(with path-safety), and no skill body is embedded in `AssistantDefinition`.

### 4.4 No-index gate (summary)

Defined in `no-index-gate.md`. Run after Phases 1, 2, 3, 5. Pass when a `Skill`
`AssistantFile` creates no markdown shadow, enqueues no extraction job, and never enters
`file_search`/Kernel Memory — proven by a regression test plus grep of the enqueue sites.

### 4.5 UI gate (summary)

Defined in `ui-gate.md`. Run after Phase 2. Pass when the Guide Builder Skills tab supports
import / author / create-from-skill, shows gating satisfaction honestly, reuses existing
upload/dialog/toast primitives, and respects the one-component-per-file decomposition rule.

### 4.6 Wire distribution gate (summary)

Defined in `wire-distribution-gate.md`. Run after Phase 4. Pass when skills resolve as MCP /
orchestrator resources over the published wire, server-resolved mode works for all three
chat shapes with no client change, and skill tool calls are trace-tagged `skills`.

### 4.7 CodeQL gate (summary)

Defined in `codeql-gate.md`. Local baseline-vs-current only. Run after Phases 1, 4, and
final 5. Pass when NEW findings versus baseline are zero (focus: path traversal in
`skills.read`, resource exposure in the MCP mapping, secret handling).

---

## 5. Deviation & failure protocol

When a gate fails, **stop the line.** Do not start the next phase.

1. **Classify** the failure in `STATUS.md`:
   - `build/test red` → mechanical; re-dispatch with the exact error + failing command.
   - `body-in-definition` → skill body/bytes serialized into `AssistantDefinition`. Hard
     reject; descriptors are tier-1 only.
   - `index leak` → a `Skill` file created a shadow / entered `file_search`. Hard reject;
     fix the enqueue gating.
   - `path traversal` → `skills.read` accepted `..`/absolute path. Hard reject; security fix.
   - `capability fabrication` → gating injected tools or a skill claimed a capability the
     assistant lacks. Hard reject (user fallback rule).
   - `missing DoD` → under-delivered; re-dispatch with the unchecked items quoted.
   - `scope creep` → out-of-scope files touched; revert unless genuinely required, in which
     case update the brief + `DECISIONS.md` first.
   - `decision drift` → built against the wrong DECISIONS value (wrong dialect, wrong
     locator scheme, sidecar built without being requested). Revert and re-dispatch with
     DECISIONS re-quoted.
   - `ui monolith / reinvention` (Phase 2) → business logic in JSX/effects, a duplicated
     upload/dialog/toast primitive, or a new panel over ~250 lines. Hard reject; require
     decomposition + reuse per `ui-gate.md`.
2. **Re-dispatch** the *same* phase brief with a focused correction note ("Gate failed on X;
   fix only X; do not touch anything else"). Re-run the **full** gate afterward.
3. **Cap retries at 2.** On a required third attempt, escalate to the user with gate output
   and a root-cause hypothesis — the brief or a DECISIONS value may be wrong.
4. **Record everything** in `STATUS.md`: attempt #, failure mode, corrective diff, re-gate
   result.

**Never** advance a phase to fix a problem a later phase will "pick up". Fix it in the phase
that owns it.

---

## 6. Final acceptance (after the last dispatched phase gate)

The plan is "executed fully" only when **all** hold:

- [ ] Proposal §18 phases implemented (or intentionally scoped): Phase 1 (storage +
      runtime), Phase 2 (authoring), Phase 3 (export/import + bootstrap), Phase 4 (wire +
      MCP distribution); Phase 5 done or `SKIPPED` — each marked in `STATUS.md`.
- [ ] **Progressive disclosure everywhere:** tier-1 descriptors injected; bodies/references
      load only via `skills.read` with path-safety; no body in `AssistantDefinition`.
- [ ] **Skills never indexed:** regression test proves a `Skill` file creates no shadow and
      never enters `file_search`.
- [ ] A GuideAnts conversation shows the discovery block and can load a skill body; a
      published wire client (OpenAI + Anthropic) benefits from server-resolved skills with
      no client change; skills resolve as MCP resources on `/api/published/mcp`.
- [ ] Skills round-trip through export/import and bootstrap seeding.
- [ ] Gating hides a skill whose required toolset is missing; `skills.read` on an explicit
      locator still works; no capability is fabricated.
- [ ] Prompt trace tags skill tool calls with a `skills` source.
- [ ] Existing guides, files, tools, and the `ClaudeSkillPackService` export are unaffected.
- [ ] All gates green on the final tree (progressive-disclosure, no-index, ui,
      wire-distribution, CodeQL); `STATUS.md` shows every dispatched phase `DONE` with no
      open deviations; `acceptance-evidence.md` captured.

When all are checked, summarize the run (phases, retries, any DECISIONS that changed
mid-flight, whether the sidecar shipped) for the user.
