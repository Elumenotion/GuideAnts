# No-Index Gate — Skills Support

Companion to `00-orchestration.md`. Run after Phases 1, 2, 3, and final 5.

Skills are surfaced through **progressive disclosure**, not retrieval-augmented search. A
`Skill` `AssistantFile` must therefore be completely inert to the markdown-shadow /
Document Intelligence / Kernel Memory pipeline. This gate proves that a skill never becomes
searchable RAG content — a leak here would silently change assistant behavior and violate
the design's separation between "knowledge documents" (`VectorStore`) and "skills".

---

## 1. Gate intent

Pass when all are true:

- Creating/importing a `Skill` file creates **no** `AssistantFileMarkdownShadow` row.
- No `ExtractAssistantFileMarkdownJob` (and therefore no `IndexAssistantFileMarkdownShadowJob`)
  is enqueued for a `Skill` file.
- A `Skill` file never contributes to `file_search` / `vector_store_ids` and never adds the
  `file_search` or `code_interpreter` tool.
- The existing enqueue/collect sites remain gated on `FolderKind == "VectorStore"` — no new
  code path treats "any new file" as indexable.

---

## 2. Baseline capture (pre-flight)

Record the current `VectorStore`-gated sites in `STATUS.md`:

- `src/server/GuideAntsApi/Services/Guides/GuidesService.cs` — create-guide (~L470),
  create-assistant (~L815), save-files (~L1976): `Where(f => f.FolderKind == "VectorStore")`
  before shadow creation / extraction enqueue.
- `GuidesService` DTO load (~L165, ~L725): shadow read only for `FolderKind == "VectorStore"`.
- `src/server/AntRunner.Chat/AntRunner.ToolCalling/AssistantDefinitions.Storage/DatabaseStorage.cs`
  — `BuildToolsArray` (~L383 CodeInterpreter, ~L389 VectorStore), `BuildToolResources`
  (~L412 VectorStore), `BuildVectorStoreFiles` (~L469 VectorStore).

These are the invariants the feature must not weaken.

---

## 3. Checks

- [ ] **Shadow absence:** create an assistant with a `Skill` file (via service or import).
      Query `AssistantFileMarkdownShadows` for that file id → **0 rows**.
- [ ] **No job enqueued:** assert (test double / spy on `IJobQueueService`) that no
      `ExtractAssistantFileMarkdown` job is enqueued for a `Skill` file.
- [ ] **Not in tool resources:** materialize the assistant; `BuildToolsArray` did **not**
      add `file_search`/`code_interpreter` from the `Skill` file; `BuildToolResources`
      `vector_store_ids` does **not** include the skill.
- [ ] **Enqueue sites unchanged:** grep the create/save/import paths — every shadow-create /
      extraction-enqueue is still `FolderKind == "VectorStore"`. No `Skill` (or unfiltered)
      branch was added.
- [ ] **Regression test exists:** a dedicated test asserts "adding a `Skill` file creates no
      shadow and enqueues no extraction job" (Phase 1 owns it; re-run in 2/3/5).

---

## 4. Report-back addition (Phases 1, 2, 3)

```text
NO-INDEX GATE:
- Skill file creates no markdown shadow: <pass/fail + query/test ref>
- No extraction/index job enqueued for Skill: <pass/fail + test ref>
- Skill not in file_search/vector_store_ids; no file_search/code_interpreter tool: <pass/fail>
- Enqueue sites still VectorStore-gated (grep): <pass/fail + sites>
- Regression test present: <path>
```
