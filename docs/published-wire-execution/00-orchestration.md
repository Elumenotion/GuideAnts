# Published Wire Continuation — Execution & Orchestration Guide

Last updated: 2026-06-25

This is the **conductor** document for executing
[`../published-openai-wire-continuation-gap-report.md`](../published-openai-wire-continuation-gap-report.md).
It is written for the **top-level (orchestrating) agent**. It defines how the work
is split into **subagent task briefs**, the **dependency order**, the
**verification gates** the orchestrator runs after each phase, and the
**deviation/failure protocol** that keeps the plan on-rails so it is executed
correctly the first time.

> **Audience split**
>
> - **You (orchestrator)** read this file + [`DECISIONS.md`](./DECISIONS.md) +
>   [`STATUS.md`](./STATUS.md). You dispatch subagents, run gates, and update
>   `STATUS.md`.
> - **Subagents** read their own `task-phase-N-*.md` brief, plus the sections of
>   `../published-openai-wire-continuation-gap-report.md` it cites, plus
>   `DECISIONS.md`. A subagent should **not** need any other context.

---

## 0. How to use this folder

| File | Owner | Purpose |
|------|-------|---------|
| `00-orchestration.md` (this) | Orchestrator | Dispatch order, gates, deviation protocol. |
| `DECISIONS.md` | Orchestrator (fill **before** any dispatch) | Locked invariants + the open decision (DW2). Single source of truth. |
| `STATUS.md` | Orchestrator (update after every gate) | Living ledger: phase state, gate results, deviations, re-dispatches. |
| `task-phase-1-transcript-resolver.md` | Subagent | Phase 1 brief. |
| `task-phase-2-chat-continuation.md` | Subagent | Phase 2 brief. |
| `task-phase-3-responses-conversation.md` | Subagent | Phase 3 brief. |
| `task-phase-4-responses-replay.md` | Subagent | Phase 4 brief. |
| `task-phase-5-tool-bridge-parity.md` | Subagent | Phase 5 brief. |
| `task-phase-6-tests-docs.md` | Subagent | Phase 6 brief. |

Each task brief follows the **same template** (Mission → Read first →
Preconditions → Guardrails → Tasks → Files in/out of scope → Self-verification →
Definition of Done → Report-back contract). The Report-back contract is what you
diff against the brief to **detect deviations**.

---

## 1. Pre-flight (do this once, before any subagent is dispatched)

Executing "the first time" depends on locking cross-cutting choices up front. **Do
not dispatch Phase 1 until all of the following are true.**

- [x] **Activity window (DW4) LOCKED → 60 minutes.** Transcript candidates are
      limited to conversations whose most recent persisted message was created
      within the previous 60 minutes; idle-longer conversations are not eligible
      for replay continuation and fall through to new-conversation creation.
- [x] **Identity scoping (DW5) LOCKED → always scope, never cross-attach.** Every
      published caller carries an identity: an authenticated internal user id, or
      an API-key-derived external caller identity for otherwise anonymous callers.
      Continuation is scoped to that identity; cross-caller attach is forbidden.
- [x] **Matching rule (DW3) LOCKED → normalize + ordinal-equal, no fuzzy, no
      guessing.** See `DECISIONS.md`.
- [x] **No `chatcmpl_*` conversation ids; no silent fallback on invalid explicit
      state (DW6) LOCKED.** An invalid `previous_response_id`/`conversation` is an
      error, never a quiet downgrade to transcript matching.
- [x] **`conversation` id scheme (DW2) LOCKED → encode the internal conversation id
      (`conv_<NotebookConversationId>`), exactly like `previous_response_id` already
      does for `resp_*`.** No mapping table, no migration, no create endpoint. See
      `DECISIONS.md`.
- [ ] Capture a **clean baseline**: from `src/server` run
      `dotnet build GuideAntsApi.sln` and `dotnet test GuideAntsApi.sln`. Record
      pass/fail counts in `STATUS.md` as the "before" line. Every later gate
      compares against this.
- [ ] Confirm a clean working tree (`git status`) and create/confirm the feature
      branch (`feature/published-wire-continuation`) per the repo branch-safety
      rule. **Never** set upstream to `origin/main`.
- [ ] Confirm a clean working tree and feature branch (above) — Phase 3 adds no
      migration, so `dotnet ef` is not required for this work.

All phases may proceed in dependency order once the baseline is captured. There is no
remaining decision blocker.

---

## 2. Dependency graph (dispatch order)

```
                 Phase 1  (provider-neutral transcript resolver)   DW3/DW4/DW5 ✅
                 (rename/extract Anthropic resolver; candidate
                  selection: scope + 60-min window + short-circuit)
                          │
              ┌───────────┼─────────────────────┐
              ▼                                   ▼
          Phase 2                            Phase 3
   (Chat Completions                  (Responses `conversation` =
    transcript continuation)           conv_<NotebookConversationId>,
        P0                              mirror of resp_*; no migration)
                                        P0
              │                                   │
              │                                   ▼
              │                              Phase 4
              │                       (Responses manual replay +
              │                        state-resolution ordering)
              │                        P0
              └───────────┬───────────────────────┘
                          ▼
                 Phase 5  (tool bridge parity across Chat/Responses/
                           Anthropic + mixed server/client tool resume)
                 P0
                          │
                          ▼
                 Phase 6  (streaming + error-shape tests, OpenAPI/docs,
                           final acceptance)
                 P1
```

**Rules:**

- Phases run in dependency order. Phase 2 and Phase 3 may run **in parallel**
  *after* Phase 1's gate is green — they touch disjoint code (Chat handler vs.
  Responses `conversation` resolution). Prefer sequential unless schedule pressure
  demands it.
- **A phase is not "done" until its gate (section 4) passes.** A downstream phase
  must **never** start on top of a failed gate. This is the core mechanism that
  prevents compounding failures.
- One subagent per phase. Do **not** hand a subagent more than its brief.
- **Anthropic is the reference implementation.** The existing
  `ResolveAnthropicConversationFromTranscriptAsync` /
  `ResolveAnthropicConversationFromMessageIdsAsync` pattern in
  `PublishedOpenAiWireEndpoints.cs` already does the right thing. Phase 1
  generalizes it; later phases reuse it. No phase invents a second resolver.

---

## 3. Dispatch protocol (per phase)

For each phase, in order:

1. **Confirm preconditions** listed in the brief (prior gate green; DECISIONS
   filled). Update `STATUS.md` → phase `IN_PROGRESS`.
2. **Dispatch one subagent** with a prompt that is exactly: *"Read and execute
   `docs/published-wire-execution/task-phase-N-*.md` end to end. Obey its
   guardrails and Definition of Done. Return the Report-back contract verbatim."*
   Give it no other instructions — the brief is the contract.
3. **Receive the Report-back.** Do not trust it blind — it is a claim.
4. **Run the gate** (section 4 + the phase's own gate). The gate is **your**
   independent verification, run with your own tools, not the subagent's word.
5. **Decide**: PASS → mark phase `DONE` in `STATUS.md`, proceed. FAIL/DEVIATION →
   follow section 5.

> You verify; the subagent implements. Never let "the subagent said it's done"
> substitute for a green gate.

---

## 4. Verification gates

### 4.1 Global invariants — checked at **every** gate

Run/inspect these after every phase. Any failure blocks the next phase.

- [ ] **Server build green**: `cd src/server && dotnet build GuideAntsApi.sln`
      (0 errors; warning count not worse than baseline).
- [ ] **Server tests green**: `cd src/server && dotnet test GuideAntsApi.sln` — no
      new failures vs the Pre-flight baseline.
- [ ] **No "fallback" anti-patterns** (per user rule — *fallback is a bug
      generator*). Grep the diff for newly added quiet downgrades: an invalid
      `previous_response_id`/`conversation` that silently routes to transcript
      matching, empty `catch {}`, a resolver that returns "best guess" instead of
      "no match", or any cross-caller attach. An invalid explicit id must surface
      as a provider-shaped error.
- [ ] **No new internal conversation when one should continue.** The whole point
      of this work: a resolved continuation passes `existingConversationId` and
      `clientMessages: null`; a new conversation is created **only** on confident
      no-match. Verify in the diff and by test.
- [ ] **Guide-aware execution intact on every path.** Every continuation path
      still funnels through the same guide-aware execution
      (`ExecuteConversationAsync` / `ResumeConversationAfterToolResultsAsync`) so
      guide instructions, notebook/project context, internal tools, tracing,
      persistence, and usage accounting are never bypassed (report §4, §Non-goals).
- [ ] **Identity scoping holds (DW5).** No transcript- or id-based path attaches
      to a conversation owned by a different internal user or different
      API-key-derived external identity.
- [ ] **Scope discipline**: the subagent only touched files its brief authorized.
      Diff the file list against the brief's "Files in scope". Unexpected files =
      deviation.
- [ ] **Matches `DECISIONS.md`** (DW2–DW6). A subagent that added a `chatcmpl_*`
      conversation key, or a silent fallback, is an automatic FAIL.

### 4.2 Per-phase gate criteria

Each is **in addition** to 4.1. Commands assume `src/server` cwd.

**Phase 1 — Provider-neutral transcript resolver**

- [ ] The Anthropic-named transcript resolver is renamed/extracted into a
      provider-neutral surface (e.g. `WireTranscriptMessage`,
      `BuildWireTranscriptHistory`, `ResolveConversationFromTranscriptAsync`); the
      Anthropic handler now calls the shared method. Grep shows no duplicated
      second resolver.
- [ ] Candidate selection implemented exactly per report §"Candidate selection and
      performance": notebook/guide + caller-identity scope, **60-minute activity
      window**, ordered most-recent-first, **short-circuit on first message
      mismatch**, latest-replayed-assistant must equal latest-persisted-assistant.
- [ ] **Anthropic behavior unchanged**: existing Anthropic
      `PublishedOpenAiWireHandlersTests` continue to pass with no behavior delta.
- [ ] New unit tests prove: 60-min window include/exclude, cross-caller
      non-attachment with identical text, and short-circuit selecting the correct
      candidate when an earlier-ordered one diverges.

**Phase 2 — Chat Completions transcript continuation**

- [ ] `PostChatCompletionsAsync` resolves the replayed prefix via the shared
      resolver **before** `ExecuteConversationAsync`, passing
      `existingConversationId` and `clientMessages: existingConversationId.HasValue
      ? null : clientPrompt.PrefixMessages` (report §2). No `chatcmpl_*`-as-thread.
- [ ] Tests (`PublishedOpenAiWireHandlersTests`): replayed `user→assistant→user`
      continues the first conversation; repeated assistant text across
      conversations does not mis-attach; replay omitting internal tool messages
      still matches; resolved continuation does not seed duplicate prefix; tool
      callback resumes the same conversation (no new one).

**Phase 3 — Responses `conversation` (encode the internal id)**

- [ ] `conversation` added to `OpenAiResponsesRequest`; resolved as
      `conv_<NotebookConversationId:N>` via encode/decode helpers that mirror
      `FormatResponsesId`/`TryParseResponsesMessageId` (DW2). **No** mapping entity,
      **no** EF migration, **no** create endpoint — grep proves none were added.
- [ ] `conversation` resolution validates notebook scope + caller identity; returns
      provider-shaped error when non-decodable/unknown/inaccessible/mismatched; when
      both `conversation` and `previous_response_id` are supplied they must refer to
      the same internal conversation. Error helpers mirror the existing
      `previous_response_id` family.
- [ ] The Responses output surfaces the conversation's `conv_<id>` so the client can
      round-trip it (mirror of how `resp_*` is returned as the response `id`).

**Phase 4 — Responses manual replay + state ordering**

- [ ] `ResolveResponsesConversationAsync` (or its successor) implements the full
      order from report §3: (1) `conversation`, (2) `previous_response_id`,
      (3) both-consistent check, (4) manual replay via the shared resolver,
      (5) new conversation only on no-match. Manual replay reuses Phase 1; no
      second resolver.
- [ ] Invalid `previous_response_id` still errors (no silent transcript fallback).
- [ ] Tests: manual replay without explicit state continues the existing
      conversation; resolved replay does not seed duplicate prefix; replay
      tolerating persisted server-side tool messages matches; `conversation` +
      `previous_response_id` agree/disagree paths.

**Phase 5 — Tool bridge parity + mixed server/client resume**

- [ ] Chat, Responses, and Anthropic share one tool-bridge contract: provider tool
      defs → internal `ChatToolDefinition`; internal pending client tools emitted in
      provider shape with stable ids persisted on the turn; provider callback
      results appended to the pending internal turn; resumption skips
      already-satisfied tools; pending turn stays `pending_client_tool` until the
      callback; historical replayed tool outputs are transcript context, not active
      callbacks (report §"Tool bridge requirements", §5).
- [ ] Shared tool-resume tests prove: server-executed tool then client-executed
      tool does not produce a stale pending-tool error; already-satisfied calls are
      skipped on resume; turn status stays pending until callback — proven through
      **each** provider wire shape (Chat + Responses), not only Anthropic.

**Phase 6 — Streaming, error shapes, OpenAPI, docs, acceptance**

- [ ] Streaming (`stream: true`) coverage for Chat + Responses: tool-call emission
      as deltas, correct finish/stop reason on the final chunk, continuation works
      under SSE (target `GuideAntsApi.IntegrationTests/Services/Conversations/
      PublishedConversationStreamingTests.cs`).
- [ ] Provider error-shape tests for the new `conversation` cases pin `status` +
      `type`/`code` consistent with the existing `previous_response_id` errors.
- [ ] Report's "Acceptance criteria" each map to a passing test or a
      file/commit reference; report updated if any requirement changed during
      execution.

---

## 5. Deviation & failure protocol

When a gate fails, **stop the line**. Do not start the next phase.

1. **Classify** the failure in `STATUS.md`:
   - **Build/test red** → mechanical; re-dispatch same subagent with the exact
     error output and the failing command.
   - **Missing DoD item** → the subagent under-delivered; re-dispatch with the
     specific unchecked items quoted.
   - **Scope creep** (touched out-of-scope files) → review those edits; revert the
     unauthorized ones unless genuinely required, in which case update the brief +
     `DECISIONS.md` first so the change is intentional and recorded.
   - **Decision drift** (built against the wrong DECISIONS value, e.g. added a
     `chatcmpl_*` thread key or a silent fallback) → revert the phase's changes and
     re-dispatch with DECISIONS re-quoted at the top.
   - **Fallback/masking introduced** → hard reject; require removal. Per user rule,
     fallback logic that hides bugs is never acceptable.
2. **Re-dispatch** the *same* phase brief with a focused correction note appended
   ("Gate failed on X; fix only X; do not touch anything else"). Re-run the
   **full** gate afterward (not just the failed check) to catch regressions.
3. **Cap retries at 2.** If a third attempt is needed, escalate to the user with
   the gate output and your hypothesis — the brief itself may be wrong or a
   DECISIONS value may need to change.
4. **Record everything** in `STATUS.md`: attempt #, what failed, what was changed,
   gate re-run result.

**Never** advance a phase to fix a problem in a later phase ("I'll wire tool
parity in Phase 6") — that is how deviations compound. Fix it in the phase that
owns it.

---

## 6. Final acceptance (after Phase 6 gate)

The plan is "executed fully" only when **all** hold:

- [ ] Every bullet in the report's **Acceptance criteria** is satisfiable by
      pointing at a commit/file/test.
- [ ] Chat Completions, Responses, and Anthropic Messages all resolve continuation
      through the **single** provider-neutral transcript resolver, scoped by
      identity + notebook and bounded by the 60-minute activity window.
- [ ] Responses supports `conversation`, `previous_response_id`, and manual replay,
      and errors (never silently falls back) on invalid/inaccessible/stale state.
- [ ] After any resolved continuation, the next run receives the new user
      instruction plus persisted internal history — never duplicated client replay.
- [ ] Tool defs/calls/results bridge for all three providers without dropping guide
      concerns; callbacks resume the pending internal turn without creating a new
      conversation; mixed server/client tool execution produces no stale
      pending-tool error.
- [ ] Global invariants (4.1) green on the final tree.
- [ ] `STATUS.md` shows every phase `DONE` with a passing gate and no open
      deviations.

When all are checked, summarize the run (phases, retries, any DECISIONS that
changed mid-flight) for the user.
