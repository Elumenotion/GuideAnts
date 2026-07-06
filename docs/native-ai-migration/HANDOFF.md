# Session handoff

Use this to start a new agent session without repeating the failure cycle (subset shipping, archive confusion, false “done”).

---

## Quick start

1. Attach `@docs/native-ai-migration/TASKS.md` and `@docs/native-ai-migration/STATE.md`
2. Copy the **agent prompt** below into the chat
3. Name the task if not doing “first unchecked” (e.g. “Task 2 only”)

---

## Reading order (agents)

| Order | File | What you learn |
|-------|------|----------------|
| 1 | [GOALS.md](./GOALS.md) | Three goals, success definition, non-goals |
| 2 | [INVENTORY.md](./INVENTORY.md) | Every model id, source, files, voiceInput, UI semantics |
| 3 | [ARCHITECTURE.md](./ARCHITECTURE.md) | Request paths, code locations, voice-pack vs model |
| 4 | [STATE.md](./STATE.md) | What actually works today |
| 5 | [TASKS.md](./TASKS.md) | What to implement next |
| 6 | [RULES.md](./RULES.md) | Verify commands before claiming done |

**Do not read** `docs/native-ai-migration/_archive/` for product decisions.

---

## Agent prompt (copy below)

```
Native local AI — follow docs/native-ai-migration/HANDOFF.md.

Read in order: GOALS.md → INVENTORY.md → ARCHITECTURE.md → STATE.md → TASKS.md.
Do NOT read _archive/ unless I ask.

Work continuously through TASKS.md from the first unchecked task. Do NOT stop after one task and do NOT ask permission between tasks. Keep going until every code-complete criterion passes and tests are green. The ONLY work that waits is runtime rows needing the operator's container + GPU + HF token — mark those pending-operator in STATE.md with exact commands; that is never a reason to stop coding. Ship nothing as "done" that is a subset of INVENTORY.

Three goals (non-negotiable):
1. Curated manifests = product. Discovery done. Sources, files, family, voiceInput in catalog manifests. Download from allowlist just works.
2. UI from catalog: model picker + per-model config controls matching voiceInput (voice_pack / builtin / instruct / optional_ref / gated). No hardcoded parallel lists in React or .NET.
3. Services use the selection: family-aware load + inference for every INVENTORY row. Loud errors only — no Chatterbox funnel, no fallback.

Product list: INVENTORY.md — 2 ASR + 11 TTS (+ 3 emb done). Partial manifest = defect.

Done means:
- STATE.md updated with pass/fail + evidence (test name or command output shown in chat)
- Verify commands from RULES.md run and output pasted
- Per-model matrix updated when runtime work is in scope

Forbidden:
- Declaring victory on 1 ASR + 1 TTS
- "Candidate", "defer", "stub", "MVP", "assumed pass"
- Hardcoding catalogEntries, LocalTtsVoiceNames, LocalTtsVoiceLanguageCodes
- Reading archived phase docs for what to ship

If I ask a question only: answer without editing files.
```

---

## For humans

| Question | Read |
|----------|------|
| What are we building? | [GOALS.md](./GOALS.md) |
| Which models? | [INVENTORY.md](./INVENTORY.md) |
| Where does code live? | [ARCHITECTURE.md](./ARCHITECTURE.md) |
| What's left? | [STATE.md](./STATE.md) + [TASKS.md](./TASKS.md) |
| How to verify? | [RULES.md](./RULES.md) |

Changing the product list: edit **INVENTORY.md first**, then manifests, then STATE matrix, then code.

---

## Why sessions failed before

| Failure mode | Prevention |
|--------------|------------|
| Agent read 20 archived phase docs | Fixed reading list; archive off-limits |
| “Done” = Task 1 or one model works | STATE matrix requires every inventory row |
| “Candidates” instead of full list | INVENTORY is authoritative — no subset language |
| Docs contradicted each other | Single active set; archive quarantined |
| Brevity hid requirements | INVENTORY has per-entry fields; TASKS has touch files + verify |
