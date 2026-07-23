# Phase 4 — Tests + Acceptance

**Branch:** `feature/notebook-sync-unification`  
**Depends on:** Phase 3 `DONE`  
**Blocks:** PR open

---

## Mission

Lock in behavior with integration and regression tests. Complete `acceptance-evidence.md`.
Run final acceptance checklist. Open **one PR** with full diff.

---

## Read first

- `docs/notebook-sync-execution/00-orchestration.md` §6
- `docs/notebook-sync-execution/serving-gate.md` §2
- `docs/notebook-sync-execution/acceptance-evidence.md`
- Existing tests:
  - `NotebookFileSyncEndpointsTests`
  - `NotebookSyncMountReparseTests`
  - `SyncNotebookHandlerTests`
  - `ConversationService*` tests if stream engine touched

---

## Preconditions

- [ ] Phases 1–3 `DONE`
- [ ] Serving gate green on branch

---

## Guardrails

- No change-detector tests (hardcoded model lists, enum counts).
- Tests use temp storage / test web app factory patterns already in repo.
- Tests must not write to real `~/.hermes` or production paths.

---

## Tasks

### 1. Required test coverage

| Test | Asserts |
|------|---------|
| `RegisterFilesAsync` unit/integration | Placeholder hash; row exists; no index job enqueued |
| Serving gate integration | Content 200 + tree before `SyncNotebook` handler |
| Full reconcile after register | SHA-256 backfill; placeholder → real hash |
| Mount reparse | Existing suite still passes |
| Stale delete with attachment | Row with `MessageAttachment` not deleted |
| Temp script exclusion | `{guid}_script.py` not indexed |
| Turn-end ordering | Mock/spy: register invoked before complete event (if testable) |

### 2. Regression suite

```bash
cd src/server && dotnet test GuideAntsApi.sln
cd src/client && npm run build && npm test -- --run
```

### 3. Manual smoke (record in acceptance-evidence)

1. Private chat: assistant generates image → renders in cell, sidebar shows file.
2. Download relative path from markdown → works without refresh.
3. Wait for background job → file still correct; indexing proceeds for markdown/text.
4. Host mount notebook: mount entry visible; host content not bulk-indexed.

### 4. Complete `acceptance-evidence.md`

Paste command outputs, test filter results, manual smoke notes.

### 5. Update `STATUS.md`

- All phases `DONE`
- Final serving gate row
- Final acceptance checklist checked

### 6. Open PR

- Title: `fix(notebook-sync): fast register before complete; unified reconciler`
- Body: summary from orchestration §7 report-back template
- Link to `docs/notebook-sync-execution/` for reviewers

---

## Definition of Done

- [ ] All tests in §1 implemented and green
- [ ] Full solution test suite green
- [ ] `acceptance-evidence.md` complete
- [ ] `STATUS.md` final acceptance checked
- [ ] Single PR opened (or ready per user request)

---

## Report-back

Use full template from `00-orchestration.md` §7.
