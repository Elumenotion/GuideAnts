# Task — Phase 5 (OPTIONAL): Metadata sidecar

> Subagent brief. Execute top to bottom. Return the Report-back contract verbatim.
> **Dispatch only if** listing/toggle performance or UI ordering proves the
> derive-from-frontmatter approach insufficient. Default state: `SKIPPED`.

## Mission

Add a metadata-only `AssistantSkillMeta` sidecar so skill listing/enable/ordering do not
require reading `SKILL.md` blobs. Bodies stay in `AssistantFile`; the sidecar caches tier-1
metadata only. This is a performance/UX optimization, not a change to the skill model.

## Read first

- `../skills-support-proposal.md` §7.1 (optional later cleanup).
- `./DECISIONS.md` — S12 + invariants (`AssistantFile` stays the body store; no bodies in
  the sidecar).
- `./no-index-gate.md`, `./progressive-disclosure-gate.md`.
- `src/server/GuideAntsApi.DataModel/Models/AssistantFileMarkdownShadow.cs` (analogous
  metadata-only sidecar pattern).

## Preconditions

- **Phase 1 gate green** (and typically Phase 2). A concrete perf/UX justification recorded
  in `STATUS.md` (why derive-from-frontmatter is insufficient). Without it, do **not**
  dispatch.

## Guardrails (hard)

- **Metadata only.** `AssistantSkillMeta` stores AssistantId, SkillName, Description,
  Enabled, DisplayOrder, ContentHash — **never** body/bytes. `AssistantFile` remains the
  single body store; `skills.read` still loads from `AssistantFile`.
- Migration is **metadata schema only**; do not touch `AssistantFile`.
- Keep the sidecar in sync on skill create/update/delete; a stale row must not change what
  the model sees (the definition/`skills.read` remain authoritative for content).
- No indexing; no bodies in the definition; no fallback masking.

## Tasks

1. Add `AssistantSkillMeta` entity + `DbSet` + EF migration (metadata columns only; PK/uniq
   on `(AssistantId, SkillName)`).
2. Populate/refresh it wherever skills are created/updated/deleted (`GuidesService`, import
   service, bootstrap import) using `SkillFrontmatter` + a content hash.
3. Allow `BuildSkills` / listing to read the sidecar for tier-1 metadata + ordering while
   bodies continue to load from `AssistantFile` via `skills.read`.
4. Tests: sidecar sync on CRUD; listing uses sidecar; content still served from
   `AssistantFile`; no-index + progressive-disclosure unaffected; migration is metadata-only.

## Files in scope

- `src/server/GuideAntsApi.DataModel/Models/AssistantSkillMeta.cs` (new)
- `src/server/GuideAntsApi.DataModel/ApplicationDbContext.cs` (+ migration)
- `src/server/GuideAntsApi/Services/Guides/GuidesService.cs` + import service (sync hooks)
- `.../DatabaseStorage.cs` `BuildSkills` (optional read path)
- Tests under `src/server/GuideAntsApi.Tests/*`.

Out of scope: everything owned by Phases 1–4.

## Self-verification

```bash
cd src/server && dotnet build GuideAntsApi.sln && dotnet test GuideAntsApi.sln
```

Run required gates: `no-index-gate.md`, `progressive-disclosure-gate.md`.

## Definition of Done

- [ ] `AssistantSkillMeta` (metadata only) + migration; no bodies stored.
- [ ] Sidecar synced on skill CRUD + import + bootstrap.
- [ ] Listing uses sidecar; bodies still from `AssistantFile` via `skills.read`.
- [ ] no-index + progressive-disclosure gates still pass; build/tests green.

## Report-back contract (return exactly this)

```text
PHASE 5 REPORT
- AssistantSkillMeta (metadata only) + migration: <pass/fail + paths>
- Sidecar sync on CRUD/import/bootstrap: <pass/fail>
- Listing uses sidecar; body still from AssistantFile: <pass/fail>
- NO-INDEX GATE: <pass/fail>  PROGRESSIVE DISCLOSURE GATE: <pass/fail>
- Verification: server-build=<p/f> server-tests=<counts>
- Files touched: <exhaustive list>
- Deviations / surprises: <list or "none">
```
