# Skills Support — Acceptance Evidence

Last updated: 2026-07-01 — **Final acceptance complete**

Branch: `feature/skills` @ post-Phase-4 implementation.

---

## Proposal §18 phase exits

| Exit | Evidence |
|------|----------|
| **Phase 1** — discovery block + `skills.read` | `SkillDiscoveryTests`, `SkillToolsTests`, `SkillStorageTests`, `SkillFrontmatterTests` |
| **Phase 2** — import/author/create-from-skill | `SkillPackageParserTests`, `GuidesServiceSkillsRoundTripTests`, `SkillPrerequisiteMapperTests`, `src/client/.../skills/__tests__/skills.test.ts` |
| **Phase 3** — export/import round-trip + bootstrap | `GuideExportImportServiceSkillsTests` (6 tests); bootstrap `Resources/bootstrap/guides/pptx-guide/Skills/pptx-author/` |
| **Phase 4** — Mode A + MCP resources + trace | `McpPublishedSkillResourceHandlersTests`, `SkillLocatorTests`, `SkillTraceSourceTests` |
| **Phase 5** | `AssistantSkillMeta` sidecar + sync | `AssistantSkillMetaSyncTests`, migration `20260701183845_AddAssistantSkillMeta` |

---

## Part A — Locked decisions (S1–S13)

| ID | Decision | Evidence |
|----|----------|----------|
| **S1** | `AssistantFile` `FolderKind="Skill"`, `Skills/<name>/` | `AssistantFile.cs` L28; `SkillStorageTests`, `GuidesServiceSkillsRoundTripTests` |
| **S2** | agentskills.io `SKILL.md` canonical dialect | `SkillFrontmatterTests` (3 dialects) |
| **S3** | Progressive disclosure; no body in definition | `SkillDescriptor.cs` (no ContentBytes); `SkillStorageTests.MaterializeAssistant_IncludesSkillsWithoutBodiesInManifest` |
| **S4** | Server-handled `skills.list`/`skills.read`; opaque | `ThreadRun.cs` LocalFunction dispatch; `McpPublishedSkillResourceHandlersTests.SkillTools_AreLocalFunction_NotClientHandled` |
| **S5** | Skills never indexed | `SkillNoIndexRegressionTests`, `GuidesServiceSkillsRoundTripTests.SkillFile_DoesNotCreateMarkdownShadow_OnDirectInsert` |
| **S6** | Tool gating hides; never injects | `SkillDiscoveryTests`, `SkillToolsTests` (list gating); `SkillPrerequisiteMapperTests` |
| **S7** | Locator internal/published forms | `SkillLocator.cs`, `SkillLocatorTests`, `DatabaseStorage.BuildSkills` |
| **S8** | `skills.*` auto-enabled | `AssistantUtility.cs` TryInjectRegisteredTool for skill-bearing assistants |
| **S9** | Explicit static toolset→tool mapping | `SkillPrerequisiteMapper.cs`, `SkillPrerequisiteMapperTests` |
| **S10** | Accept 3 dialects; preserve original verbatim | `SkillFrontmatterTests`, `SkillPackageParserTests` |
| **S11** | Skill scripts/assets materialized like CI files | `SkillNotebookMaterializer.cs`; `NotebookService.CopyGuideFilesToNotebookAsync` |
| **S12** | Enable/order from frontmatter + sidecar | `AssistantSkillMeta` sidecar; `SkillTier1Resolver` prefers sidecar when `ContentHash` matches |
| **S13** | 50 skills/assistant; `SKILL.md` ≤ 100k chars | `SkillImportService` / `GuidesService` limit enforcement (see `SkillPackageParserTests` validation) |

---

## Part B — Frozen invariants

| Invariant | Evidence |
|-----------|----------|
| `AssistantFile` canonical body store; no body in definition | `SkillDescriptor.cs`; materialization tests |
| `Skill` inert to `BuildToolsArray`/`BuildToolResources` | `DatabaseStorage.cs` — only VectorStore/CodeInterpreter special-cased |
| No indexing of skills | `SkillNoIndexRegressionTests`; enqueue sites still `FolderKind == "VectorStore"` |
| `skills.read` path-safety | `SkillToolsTests` (.., absolute, cross-skill); `SkillLocatorTests` |
| No fallback masking / no capability fabrication | Gating hides only; `SkillPrerequisiteMapper` explicit map |
| Server-side, opaque on wire | `ThreadRun.cs` LocalFunction; no skills logic in wire handlers |
| One materialization choke point (`BuildSkills`) | `DatabaseStorage.BuildSkills` only descriptor producer |
| Notebook skill payload choke point | `NotebookService.CopyGuideFilesToNotebookAsync` + `SkillNotebookMaterializer` |
| `ClaudeSkillPackService` export untouched | Not in git diff |
| One published runtime | Wire handlers delegate to `SendMessageStreamAsync` / MCP resource handlers |

---

## Gate final-pass references

| Gate | Final result | Evidence |
|------|--------------|----------|
| Progressive disclosure | PASS | Tier-1 descriptors only; bodies via `skills.read`; path-safety tests |
| No-index | PASS | Regression tests + VectorStore-gated enqueue sites unchanged |
| UI (Guide Builder Skills) | PASS | Skills tab, import/author/create-from-skill; `skills.test.ts` |
| Wire distribution | PASS | `McpPublishedSkillResourceHandlersTests`, Mode A via existing conversation path |
| CodeQL | PASS (manual review) | `SkillPathSafety` containment; baseline captured pre-flight; no new suppressions |

---

## Final verification

| Check | Result |
|---|---|
| Server build | PASS (0 errors, 0 warnings) |
| Server unit tests | PASS — 1863/1863 (`GuideAntsApi.Tests`) |
| Skills-focused tests | PASS — 65/65 (`FullyQualifiedName~Skill|AssistantSkillMeta`) |
| Client build | PASS |
| Client tests | PASS — 3032/3032 |
| Phase 5 | DONE — `AssistantSkillMeta` metadata-only sidecar |

### Commands run at final acceptance

```text
cd src/server && dotnet build GuideAntsApi.sln
cd src/server && dotnet test GuideAntsApi.Tests/GuideAntsApi.Tests.csproj  → 1860 passed
cd src/server && dotnet test GuideAntsApi.Tests --filter "FullyQualifiedName~Skill"  → 62 passed
cd src/client && npm run build
cd src/client && npm test -- --run  → 3032 passed
```
