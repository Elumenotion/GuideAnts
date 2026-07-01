# Progressive Disclosure Gate — Skills Support

Companion to `00-orchestration.md`. Run after Phases 1, 4, and final 5.

This gate enforces the single most important property of the skills feature: the model
sees **only** tier-1 metadata up front, and skill bodies/reference files load **on demand**
through `skills.read` — never inlined into the prompt or the definition. This is what keeps
prompt cost bounded regardless of skill size and mirrors the Codex/hermes contract captured
in [`../../CodexTrace.md`](../../CodexTrace.md).

---

## 1. Gate intent

Pass when all are true:

- The skills discovery block injected by `ConversationHistoryBuilder` contains **only**
  `name` + `description` + locator per skill — no body, no reference content, no scripts.
- `AssistantDefinition.Skills` carries `SkillDescriptor`s (tier-1 + file-path inventory)
  and **no** skill body/bytes.
- Skill bodies and `references/` are retrievable **only** via `skills.read`, which reads from `AssistantFile`.
- Skill `scripts/` and `assets/` exist in **two** channels: (1) on disk in the notebook sandbox after creation (`Resources/` + `Output/` symlinks — see `docs/project-and-notebook-files-system.md` §7.4); (2) readable as text via `skills.read` for inspection. They are **not** inlined into the discovery block or definition.
- `skills.read` enforces path-safety: the resolved path stays under `Skills/<name>/`;
  `..`, absolute paths, and cross-skill escapes are rejected explicitly (no silent clamp).
- `skills.list`/`skills.read` are **server-handled** (`ActionType.LocalFunction`), run
  between model rounds, and never emit `pending_client_tool` (S4).

---

## 2. Checks

### 2.1 Injection is tier-1 only

- [ ] Render the discovery block for an assistant with a large skill (body > 5 KB). Confirm
      the injected `developer`/`system` message contains the description string but **not**
      the body text. (Diff the seed messages in a prompt trace.)
- [ ] Grep the discovery-block builder: it reads `descriptor.Name` / `descriptor.Description`
      / `descriptor.Locator` only. No read of `ContentBytes`/body at injection time.

### 2.2 No body in the definition

- [ ] Serialize an `AssistantDefinition` for a skill-bearing assistant. Confirm the `skills`
      array elements have no `body`/`content`/`bytes` field; only tier-1 + `files[]` paths.
- [ ] Grep `DatabaseStorage.BuildSkills`: it projects descriptors and does not copy
      `ContentBytes` into the descriptor.

### 2.3 On-demand load + path-safety

- [ ] `skills.read(locator)` returns `SKILL.md` body; `skills.read(locator, "references/x.md")`
      returns that file.
- [ ] `skills.read(locator, "../../etc/passwd")`, `skills.read(locator, "/abs/path")`, and a
      path pointing into a **different** `Skills/<other>/` group are all **rejected with an
      explicit error** (not clamped, not empty-string).
- [ ] Unit tests cover: valid body read, valid reference read, `..` rejection, absolute-path
      rejection, cross-skill rejection, unknown-locator rejection.

### 2.4 Server-handled + opaque

- [ ] `skills.list`/`skills.read` dispatch through `ThreadRun.DoToolCalls` as
      `ActionType.LocalFunction` (same pattern as `SearchAssistantFiles`).
- [ ] A turn that calls `skills.read` continues server-side and completes with assistant
      text; it never surfaces `pending_client_tool`. (Run a notebook turn; inspect trace.)

### 2.5 Locators (Phase 4)

- [ ] Internal locator is `skill://<assistantId>/<name>` (S7).
- [ ] Published/MCP locator is `skill://<guide>/<name>` (+ `/references/<path>`) and resolves
      the same body/reference bytes as the internal `skills.read`.

---

## 3. Report-back addition (Phases 1, 4)

```text
PROGRESSIVE DISCLOSURE GATE:
- Discovery block tier-1 only (no body): <pass/fail + trace ref>
- No body/bytes in AssistantDefinition.Skills: <pass/fail>
- skills.read body + reference load: <pass/fail>
- Path-safety (.. / absolute / cross-skill rejected explicitly): <pass/fail + test refs>
- Server-handled, no pending_client_tool: <pass/fail + trace ref>
- Locators (internal skill://<assistantId>; published skill://<guide>) [Phase 4]: <pass/fail/na>
```
