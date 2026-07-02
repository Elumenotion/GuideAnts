# CodeQL Gate — Skills Support

Companion to `00-orchestration.md`. Local baseline-vs-current only (no GitHub parity). Run
after Phases 1, 4, and final 5.

The skills feature adds two security-sensitive surfaces: `skills.read` (a file reader keyed
by a client-influenced `file_path`) and the MCP resource exposure (serving definition
content to external clients). This gate ensures neither introduces new findings versus a
captured baseline.

---

## 1. Gate intent

Pass when **NEW** findings versus `.codeql/baseline/` are **zero**, with specific attention
to:

- **Path traversal / arbitrary file read** in `skills.read` and the MCP resource read
  (`file_path` must not escape `Skills/<name>/`).
- **Uncontrolled resource exposure** in the `/api/published/mcp` skill mapping (only enabled
  skills of the resolved guide are listed/readable; no cross-guide/cross-assistant leak).
- **Secret handling**: `skills.read` returns file text verbatim and must not resolve or log
  `{{secret:…}}` values; no raw secrets in logs/preview.
- No new silent `catch {}` masking parse/IO failures (user rule + design invariant).

---

## 2. Procedure

1. **Baseline (pre-flight):** run CodeQL for C#, Python, and JS; save SARIFs under
   `.codeql/baseline/`. Record counts in `STATUS.md`.
2. **After each security-sensitive phase (1, 4) and final (5):** re-run; diff by
   `(ruleId, file)` against baseline.
3. **Pass** when the NEW set is empty. Any new path-traversal, SSRF/exposure, or
   clear-text-logging finding on the skill surfaces is an automatic FAIL — fix in code, no
   suppression.

---

## 3. Focused manual review (in addition to the scan)

- [ ] `skills.read` resolves `Skills/<name>/` + `file_path` with an explicit containment
      check (canonicalize, then assert the resolved path starts with the skill root). `..`,
      absolute paths, and cross-skill paths throw, not clamp.
- [ ] MCP resource read reuses the same containment check; the skill set is scoped to the
      resolved `PublishedApiExecutionContext` guide.
- [ ] No `Skill` file bytes are written to logs; errors log the locator/relative path, not
      content.

---

## 4. Report-back addition (Phases 1, 4)

```text
CODEQL GATE:
- New vs baseline (ruleId+file): <count → ids/files or none>
- skills.read containment (.. / absolute / cross-skill rejected): <pass/fail + test refs>
- MCP resource read scoped to guide + same containment: <pass/fail>
- No secret/content logging: <pass/fail>
```
